"""Real-ASR regression checks using the generated English release-smoke WAV.

Run with the private speech Python, --model and --sample. No live audio capture.
"""
import argparse
import base64
import json
from pathlib import Path
import re
import queue
import subprocess
import sys
import threading
import time

import av
import numpy as np


def normalize(text):
    return re.findall(r"[a-z]+", text.lower())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sample", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="gpu-only", choices=("gpu-only", "cpu"))
    parser.add_argument("--log-dir", default="artifacts/validation/live-asr-regression")
    args = parser.parse_args()
    resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
    frames = []
    with av.open(args.sample) as container:
        for frame in container.decode(audio=0):
            frames.extend(item.to_ndarray().reshape(-1) for item in resampler.resample(frame))
        frames.extend(item.to_ndarray().reshape(-1) for item in resampler.resample(None))
    audio = np.concatenate(frames)
    logs = Path(args.log_dir)
    logs.mkdir(parents=True, exist_ok=True)
    worker = Path(__file__).resolve().parents[1] / "worker/live_asr.py"

    def packets(values, offset=0):
        return [dict(type="audio", start=offset + index / 16000,
                     pcm=base64.b64encode(values[index:index + 3200].astype("<i2").tobytes()).decode(), transport_s=0)
                for index in range(0, len(values), 3200)]

    def run(name, messages, expected):
        result = subprocess.run([sys.executable, "-X", "utf8", "-u", str(worker), "--model", args.model,
                                 "--device", args.device],
                                input="\n".join(json.dumps(item) for item in messages + [dict(type="stop")]) + "\n",
                                text=True, encoding="utf-8", capture_output=True, timeout=180)
        (logs / (name + ".jsonl")).write_text(result.stdout, encoding="utf-8")
        (logs / (name + ".stderr.txt")).write_text(result.stderr, encoding="utf-8")
        if result.returncode:
            raise AssertionError(f"{name}: worker failed: {result.stdout}\n{result.stderr}")
        events = [json.loads(line) for line in result.stdout.splitlines()]
        assert events[0]["type"] == "ready" and events[-1]["type"] == "done", (name, events)
        assert not any(event["type"] == "error" for event in events), (name, events)
        if args.device == "gpu-only":
            assert events[0]["backend"] == "NVIDIA CUDA", (name, events)
        segments = [event for event in events if event["type"] == "segment"]
        text = " ".join(event["text"] for event in segments)
        assert normalize(text) == normalize(expected), f"{name}: expected {expected!r}, got {text!r}"
        end = 0
        for segment in segments:
            assert segment["start"] >= end - 0.05 and segment["end"] > segment["start"], (name, segments)
            end = segment["end"]
        print(f"PASS {name}: {text or '(silence)'}", flush=True)
        return segments

    full = "Hello. This is a local translation test. The class will be online on Thursday."
    run("opening-with-following-speech", packets(audio), full)
    run("isolated-short-speech-stop", packets(audio[:16000]), "Hello.")
    run("pause-after-short-speech", packets(audio[:16000]) + [dict(type="pause")] + packets(audio[16000:], 1), full)
    segments = run("gap-after-short-speech", packets(audio[:16000]) + packets(audio[16000:], 5), full)
    assert segments[-1]["start"] >= 5, segments
    run("silence", packets(np.zeros(32000, dtype=np.int16)), "")
    process = subprocess.Popen([sys.executable, "-X", "utf8", "-u", str(worker), "--model", args.model,
                                "--device", args.device], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8")
    output, received = [], queue.Queue()
    def read_output():
        for line in process.stdout:
            output.append(line)
            received.put(json.loads(line))
    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    try:
        process.stdin.write("\n".join(json.dumps(item) for item in packets(audio[:16000])) + "\n")
        process.stdin.flush()
        deadline = time.monotonic() + 15
        while True:
            event = received.get(timeout=max(0.01, deadline - time.monotonic()))
            assert event["type"] != "error", event
            if event["type"] == "segment":
                assert normalize(event["text"]) == ["hello"], event
                break
        # Assert a caption arrived while stdin was still open, without Stop.
        print("PASS isolated-short-speech-idle: Hello. (before Stop)", flush=True)
        process.stdin.close()
        assert process.wait(timeout=30) == 0
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        reader.join(timeout=5)
        (logs / "isolated-short-speech-idle.jsonl").write_text("".join(output), encoding="utf-8")
        (logs / "isolated-short-speech-idle.stderr.txt").write_text(process.stderr.read(), encoding="utf-8")
    print("6 real-ASR regression cases passed. Not a UI/application-loopback test.", flush=True)


if __name__ == "__main__":
    main()
