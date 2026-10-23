"""Local file ASR, bounded audio chunks, pull protocol for backpressure/pause."""
import argparse
import contextlib
import os
from pathlib import Path
import sys
import time

# Also applies the same offline network guard as the capture worker.
from live_asr import emit
import json


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--file", required=True)
    parser.add_argument("--device", choices=("gpu-only", "cpu"), default="gpu-only")
    parser.add_argument("--resume", type=float, default=0)
    args = parser.parse_args()
    if not Path(args.file).is_file() or not (Path(args.model) / "model.bin").is_file():
        raise RuntimeError("找不到输入文件或本地英语模型")
    handles = []
    if os.name == "nt":
        for folder in (Path(sys.prefix) / "Lib/site-packages/nvidia").glob("*/bin"):
            handles.append(os.add_dll_directory(str(folder)))
            os.environ["PATH"] = str(folder) + os.pathsep + os.environ.get("PATH", "")
    import av
    import numpy as np
    import onnxruntime
    from av.stream import Disposition
    from faster_whisper import WhisperModel
    from faster_whisper.vad import get_speech_timestamps, VadOptions
    onnxruntime.disable_telemetry_events()
    device = "cpu" if args.device == "cpu" else "cuda"
    with contextlib.redirect_stdout(sys.stderr):
        model = WhisperModel(args.model, device=device,
                             compute_type="int8" if device == "cpu" else "float16",
                             cpu_threads=min(8, os.cpu_count() or 4), local_files_only=True)
    with av.open(args.file, options={"protocol_whitelist": "file"}) as container:
        tracks = list(container.streams.audio)
        if not tracks:
            raise RuntimeError("文件中没有可读取的音轨")
        track = next((stream for stream in tracks if stream.disposition & Disposition.default), tracks[0])
        file_start = (container.start_time or 0) / av.time_base
        track_start = float(track.start_time * track.time_base) if track.start_time is not None else file_start
        offset = max(0.0, track_start - file_start)
        duration = container.duration / av.time_base if container.duration else 0.0
        if track.duration is not None:
            duration = max(duration, offset + float(track.duration * track.time_base))
        emit("ready", backend="NVIDIA CUDA" if device == "cuda" else "CPU", duration=duration,
             track=track.index, language=track.metadata.get("language", "未知"))
        resampler = av.AudioResampler(format="flt", layout="mono", rate=16000)

        def frames():
            cursor = round(offset * 16000)
            def aligned(normalized):
                nonlocal cursor
                values = normalized.to_ndarray().reshape(-1)
                timestamp = cursor if normalized.pts is None or normalized.time_base is None else round((float(normalized.pts * normalized.time_base) - file_start) * 16000)
                # Preserve track offsets/gaps in source timestamps, with bounded silence buffers.
                while timestamp > cursor:
                    size = min(timestamp - cursor, 480000)
                    yield np.zeros(size, dtype=np.float32)
                    cursor += size
                if timestamp < cursor:
                    values = values[min(cursor - timestamp, len(values)):]
                if len(values):
                    cursor += len(values)
                    yield values
            for frame in container.decode(track):
                for normalized in resampler.resample(frame):
                    yield from aligned(normalized)
            for normalized in resampler.resample(None):
                yield from aligned(normalized)

        source = frames()
        buffer = np.empty(0, dtype=np.float32)
        consumed = 0
        eof = False
        previous = ""
        options = VadOptions(min_silence_duration_ms=350, speech_pad_ms=100)
        for line in sys.stdin:
            message = json.loads(line)
            if message.get("type") == "stop":
                return
            if message.get("type") != "next":
                continue
            # No entire-file decode, temporary WAV, or recognition while paused.
            # Recovery reads/discards earlier samples to reproduce exact boundaries.
            target = max(0, round((args.resume - offset) * 16000))
            while consumed < target:
                if not len(buffer):
                    try:
                        buffer = next(source)
                    except StopIteration:
                        eof = True
                        break
                skip = min(target - consumed, len(buffer))
                consumed += skip
                buffer = buffer[skip:]
            while len(buffer) < 480000 and not eof:
                try:
                    buffer = np.concatenate((buffer, next(source)))
                except StopIteration:
                    eof = True
            if not len(buffer):
                emit("done", position=offset + consumed / 16000)
                return
            count = min(480000, len(buffer))
            speech = get_speech_timestamps(buffer[:count], options)
            # Prefer a silence in the latter half of the 30 s window.
            for left, right in reversed(list(zip(speech, speech[1:]))):
                if left["end"] >= 240000 and right["start"] - left["end"] >= 4800:
                    count = min(count, left["end"] + 1600)
                    break
            chunk = buffer[:count]
            start = offset + consumed / 16000
            watch = time.perf_counter()
            result = []
            if speech:
                with contextlib.redirect_stdout(sys.stderr):
                    segments, _ = model.transcribe(chunk, language="en", task="transcribe", beam_size=3,
                                                   vad_filter=True, vad_parameters={"min_silence_duration_ms": 350}, word_timestamps=True,
                                                   condition_on_previous_text=False,
                                                   initial_prompt=message.get("context", "")[-400:] or previous or None)
                    for segment in segments:
                        text = segment.text.strip()
                        if len(text) > 420 and segment.words:
                            words = []
                            length = 0
                            for word in segment.words:
                                if words and length + len(word.word) > 420:
                                    result.append(dict(start=round(start + words[0].start, 3),
                                                       end=round(min(start + count / 16000, start + words[-1].end), 3),
                                                       text="".join(item.word for item in words).strip()))
                                    words, length = [], 0
                                words.append(word)
                                length += len(word.word)
                            if words:
                                result.append(dict(start=round(start + words[0].start, 3),
                                                   end=round(min(start + count / 16000, start + words[-1].end), 3),
                                                   text="".join(item.word for item in words).strip()))
                        elif text:
                            result.append(dict(start=round(start + segment.start, 3),
                                               end=round(min(start + count / 16000, start + segment.end), 3), text=text))
                previous = " ".join(item["text"] for item in result)[-400:]
            consumed += count
            buffer = buffer[count:]
            emit("batch", segments=result, position=offset + consumed / 16000,
                 asr_s=time.perf_counter() - watch, backend="NVIDIA CUDA" if device == "cuda" else "CPU")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", text=f"{type(error).__name__}: {error}")
        sys.exit(1)
