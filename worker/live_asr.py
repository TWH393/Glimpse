"""Offline English ASR; one decode serves drafts and stable/final words."""
import argparse
import base64
import contextlib
import json
import os
from pathlib import Path
import queue
import socket
import sys
import threading
import time

os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", ORT_DISABLE_TELEMETRY="1")


def deny_network(*args, **kwargs):
    raise RuntimeError("Speech worker is offline; install its model first")


socket.socket.connect = deny_network
socket.socket.connect_ex = deny_network
socket.create_connection = deny_network
socket.getaddrinfo = deny_network
output_lock = threading.Lock()


def emit(kind, **values):
    with output_lock:
        print(json.dumps(dict(type=kind, **values), ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", choices=("auto", "gpu", "cpu", "gpu-only"), default="gpu-only")
    args = parser.parse_args()
    if not (Path(args.model) / "model.bin").is_file():
        raise RuntimeError("Speech model is missing; run tools/install_speech.ps1")
    dll_handles = []
    if os.name == "nt":
        for folder in (Path(sys.prefix) / "Lib/site-packages/nvidia").glob("*/bin"):
            dll_handles.append(os.add_dll_directory(str(folder)))
            os.environ["PATH"] = str(folder) + os.pathsep + os.environ.get("PATH", "")
    import numpy as np
    import onnxruntime
    onnxruntime.disable_telemetry_events()
    from faster_whisper import WhisperModel
    from faster_whisper.vad import get_speech_timestamps, get_vad_model, VadOptions
    device = "cpu" if args.device == "cpu" else "cuda"

    def load():
        with contextlib.redirect_stdout(sys.stderr):
            return WhisperModel(args.model, device=device, compute_type="float16" if device == "cuda" else "int8",
                                cpu_threads=min(8, os.cpu_count() or 4), local_files_only=True)

    try:
        model = load()
    except Exception as error:
        if device == "cpu" or args.device == "gpu-only":
            raise
        device = "cpu"
        emit("status", text=f"ASR GPU 初始化失败，回退 CPU：{error}")
        model = load()
    get_vad_model()
    emit("ready", backend="NVIDIA CUDA" if device == "cuda" else "CPU")
    inbox = queue.Queue(maxsize=600)
    overflow = threading.Event()

    def read_input():
        try:
            for line in sys.stdin:
                message = json.loads(line)
                message["received_at"] = time.perf_counter()
                try:
                    inbox.put(message, timeout=0.1)
                except queue.Full:
                    overflow.set()
                    return
                if message.get("type") == "stop":
                    return
        except Exception:
            overflow.set()
        finally:
            while not overflow.is_set():
                try:
                    inbox.put(dict(type="stop"), timeout=0.1)
                    break
                except queue.Full:
                    continue

    threading.Thread(target=read_input, daemon=True).start()
    audio = np.empty(0, np.float32)
    origin = 0.0
    checked_at = 0
    decoded_end = -1.0
    committed_until = -1.0
    previous = ""
    hypothesis = []
    cached_words = []
    last_audio_at = time.perf_counter()
    last_decode_s = 0.0
    audio_queue_s = 0.0
    cached_buffer_s = 0.0
    unresolved_audio = False
    unresolved_since = 0.0
    empty_retry_end = -1.0
    committed_words = []
    vad_options = VadOptions(min_silence_duration_ms=300, speech_pad_ms=100)

    def transcribe(retry=False):
        nonlocal model, device, last_decode_s, cached_buffer_s
        timer = time.perf_counter()
        options = dict(language="en", task="transcribe", beam_size=3 if retry else 1, temperature=0,
                       condition_on_previous_text=False, initial_prompt=previous[-400:] or None,
                       word_timestamps=True, vad_filter=False,
                       hallucination_silence_threshold=None if retry else 0.5)
        # A terminal retry gives isolated short speech some trailing context.
        # Padding is local only; timestamps and buffer metrics use real audio.
        decoding_audio = np.pad(audio, (0, max(0, 48000 - len(audio)))) if retry else audio
        try:
            with contextlib.redirect_stdout(sys.stderr):
                segments, _ = model.transcribe(decoding_audio, **options)
                segments = list(segments)
        except Exception as error:
            if device == "cpu" or args.device == "gpu-only":
                raise
            model = None
            import gc
            gc.collect()
            device = "cpu"
            emit("status", text=f"ASR GPU 推理失败，回退 CPU：{error}")
            model = load()
            with contextlib.redirect_stdout(sys.stderr):
                segments, _ = model.transcribe(decoding_audio, **options)
                segments = list(segments)
        last_decode_s = time.perf_counter() - timer
        cached_buffer_s = len(audio) / 16000
        words = []
        for segment in segments:
            if segment.avg_logprob < -1.5 or (segment.no_speech_prob > 0.65 and segment.avg_logprob < -0.8):
                continue
            for word in segment.words or []:
                start = origin + word.start
                end = min(origin + len(audio) / 16000, origin + word.end)
                spelling = "".join(char.lower() for char in word.word if char.isalnum())
                duplicate = spelling and any(start < old["end"] and end > old["start"]
                    and spelling == "".join(char.lower() for char in old["text"] if char.isalnum())
                    for old in committed_words)
                if end > committed_until + 0.04 and start < end and not duplicate:
                    words.append(dict(text=word.word, start=start, end=end))
        return words

    def commit(words):
        nonlocal committed_until, previous
        if not words:
            return
        text = "".join(word["text"] for word in words).strip()
        start = max(committed_until, words[0]["start"], 0)
        end = min(origin + len(audio) / 16000, words[-1]["end"])
        if not text or end <= start:
            return
        committed_until = end
        committed_words.extend(words)
        del committed_words[:-24]
        previous = (previous + " " + text)[-400:]
        emit("segment", start=round(start, 3), end=round(end, 3), text=text,
             backend="NVIDIA CUDA" if device == "cuda" else "CPU", buffer_s=round(cached_buffer_s, 4),
             asr_s=round(last_decode_s, 4), audio_queue_s=round(audio_queue_s, 4))

    def recognize(final=False, closing=False, forced=False):
        nonlocal hypothesis, cached_words, decoded_end, audio, origin, checked_at, audio_queue_s
        nonlocal unresolved_audio, unresolved_since, empty_retry_end
        if len(audio) < 3200:
            return True
        if unresolved_audio and not closing and len(audio) < 32000:
            # Avoid finalizing a different guessed word from the same tiny
            # utterance immediately after an empty decode. Await real context.
            return False
        end = origin + len(audio) / 16000
        if abs(end - decoded_end) > 0.001:
            cached_words = transcribe()
            decoded_end = end
        current = [word for word in cached_words if word["end"] > committed_until + 0.04]
        if not current:
            speech = get_speech_timestamps(audio, vad_options)
            voiced = unresolved_audio or any(origin + span["end"] / 16000 > committed_until + 0.25 for span in speech)
            if voiced and closing and abs(end - empty_retry_end) > 0.001:
                empty_retry_end = end
                cached_words = transcribe(retry=True)
                current = [word for word in cached_words if word["end"] > committed_until + 0.04]
            if voiced and not current:
                if not unresolved_audio:
                    unresolved_since = time.perf_counter()
                unresolved_audio = True
                emit("draft", text="")
                if closing:
                    raise RuntimeError(f"检测到语音但未能识别 {origin:.2f}–{end:.2f} 秒，已停止以避免静默丢句")
                # No confirmation means no advancing origin or trimming audio.
                return False
        unresolved_audio = False
        unresolved_since = 0.0
        stable = 0
        for old, new in zip(hypothesis, current):
            if old["text"].strip().lower() != new["text"].strip().lower() or abs(old["start"] - new["start"]) > 0.5:
                break
            if new["end"] > end - 0.2:
                break
            stable += 1
        weak_tail = {"a", "an", "the", "to", "of", "in", "on", "at", "for", "and", "or", "but", "with", "is", "are", "was", "were", "will", "would", "can", "could", "should"}
        while stable and current[stable - 1]["text"].strip().lower() in weak_tail:
            stable -= 1
        count = len(current) if final else (stable if stable >= 3 or stable and current[stable - 1]["text"].rstrip().endswith((".", "?", "!", ",")) else 0)
        if forced and not final:
            # Submit the buffered phrase at the budget, retaining words nearest
            # the live edge so a word cut mid-syllable can finish next time.
            count = sum(word["end"] <= end - 0.3 for word in current)
        if count:
            commit(current[:count])
        tail = current[count:]
        emit("draft", text="".join(word["text"] for word in tail).strip())
        hypothesis = tail
        if final:
            keep = min(2560, len(audio)) if end - committed_until < 0.35 else 0
            origin = end - keep / 16000
            audio = audio[-keep:] if keep else np.empty(0, np.float32)
            hypothesis = []
            checked_at = 0
            audio_queue_s = 0.0
        elif count:
            cut = max(0, min(len(audio), int((committed_until - origin) * 16000) - 2560))
            origin += cut / 16000
            audio = audio[cut:]
            checked_at = 0
            audio_queue_s = 0.0
        return True

    def flush(closing=False):
        nonlocal audio, checked_at, hypothesis, unresolved_audio
        if len(audio) and (unresolved_audio or get_speech_timestamps(audio, vad_options)):
            if not recognize(True, closing=closing):
                return
        audio = np.empty(0, np.float32)
        checked_at = 0
        hypothesis = []
        unresolved_audio = False

    while True:
        if overflow.is_set():
            raise RuntimeError("语音识别积压过多，已停止采音；请降低负载后重试")
        try:
            message = inbox.get(timeout=0.15)
        except queue.Empty:
            if len(audio) and time.perf_counter() - last_audio_at > 0.45:
                if not unresolved_audio:
                    flush()
                elif time.perf_counter() - unresolved_since >= 1.0:
                    # Some capture sources stop sending packets during silence.
                    # Resolve their retained short utterance without waiting for Stop.
                    flush(closing=True)
            continue
        kind = message.get("type")
        if kind in ("pause", "stop"):
            flush(closing=True)
            if kind == "stop":
                break
            continue
        if kind != "audio":
            continue
        now = time.perf_counter()
        last_audio_at = now
        audio_queue_s = max(audio_queue_s, now - message["received_at"] + float(message.get("transport_s", 0)))
        samples = np.frombuffer(base64.b64decode(message["pcm"]), dtype="<i2").astype(np.float32) / 32768.0
        start = float(message["start"])
        if len(audio) and abs(start - (origin + len(audio) / 16000)) > 0.3:
            flush(closing=True)
        if not len(audio):
            origin = start
        audio = np.concatenate((audio, samples))
        if len(audio) - checked_at < 3200:
            continue
        checked_at = len(audio)
        speech = get_speech_timestamps(audio, vad_options)
        if not speech:
            if unresolved_audio:
                if len(audio) >= 48000:
                    recognize(False, closing=True, forced=True)
                continue
            if len(audio) > 12000:
                origin += (len(audio) - 3200) / 16000
                audio = audio[-3200:]
                checked_at = 0
                hypothesis = []
            continue
        ended = len(audio) - speech[-1]["end"] >= 3200
        forced = len(audio) >= 48000
        due = origin + len(audio) / 16000 - decoded_end >= 1.0 and len(audio) >= 16000
        if ended or forced or due:
            recognize(ended, closing=forced, forced=forced)
    emit("done")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", text=f"{type(error).__name__}: {error}")
        sys.exit(1)
