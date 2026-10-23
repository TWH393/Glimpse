# Local English to Chinese model

## Local English speech recognition (development draft)

- faster-whisper: https://github.com/SYSTRAN/faster-whisper, pinned 1.2.1, MIT. CTranslate2 is pinned to 4.8.3. Package licenses remain in the private speech environment; resolved versions and artifact hashes are in `worker/speech-requirements.lock.txt`.
- Distil-Whisper Large v3.5: https://huggingface.co/distil-whisper/distil-large-v3.5 ; official CT2 weights: https://huggingface.co/distil-whisper/distil-large-v3.5-ct2 , revision `9793ccc07920e0f830e1dba0343efcdf0ef8c903`, MIT. Unmodified `model.bin` is 1,512,927,867 bytes; upstream LFS SHA-256 `c58b88b8585ffcd2135fddaaf421ce72cb223b32edea70d156aed1dea319a119`. The upstream README is retained with the downloaded weights; the installer writes a model manifest without loading weights.
- Silero VAD is supplied by the pinned faster-whisper wheel; its upstream attribution is retained in that package. ONNX Runtime CPU performs VAD; CTranslate2 performs GPU/CPU ASR. NVIDIA cuBLAS 12.9.2.10, cuDNN 9.27.0.42 and their runtime dependencies retain NVIDIA's own terms in their package license directories.
- Process audio capture follows the Windows API demonstrated by Microsoft's ApplicationLoopback sample: https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/ . No full-system fallback or virtual audio driver is installed.
- Source selection enumerates Core Audio output sessions through `IAudioSessionManager2`; explicitly selected global capture uses `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` for Glimpse and its workers. Application capture never automatically changes to global scope.
- The speech environment/models are installation assets excluded from Git and included in the complete offline preview installer starting with `v0.2.0-preview.1`.
- Local audio/video import reuses PyAV and its FFmpeg libraries from the same pinned speech environment for decoding/resampling; no separate FFmpeg executable is downloaded. Their upstream license/notice files remain with the installed packages and must accompany any future bundled distribution. File import and capture share the same unmodified recognition and translation weights.
- The inherited Whisper MIT text is preserved in `docs/licenses/DistilWhisper-LICENSE.txt` (upstream https://github.com/openai/whisper/blob/main/LICENSE).

## Local screenshot OCR

- RapidOCR: https://github.com/RapidAI/RapidOCR ; PyPI `rapidocr-onnxruntime==1.4.4`, package metadata declares Apache-2.0.
- ONNX Runtime CPU: `1.20.1`. Separate GPU environment: `onnxruntime-gpu==1.23.2`, NVIDIA CUDA 12.8 components and cuDNN `9.10.2.21`. The NVIDIA runtime components retain their own license terms. Exact packages and download hashes are recorded in `worker/ocr-gpu-requirements.lock.txt`; original notices remain in installed packages. Neither environment modifies translation runtime files.
- Models included unchanged in the pinned RapidOCR wheel: `ch_PP-OCRv4_det_infer.onnx`, `ch_PP-OCRv4_rec_infer.onnx`, `ch_ppocr_mobile_v2.0_cls_infer.onnx`.
- Dependency versions and wheel hashes: `worker/ocr-requirements.lock.txt`. The installer records installed model names, byte counts and SHA-256 in `.runtime/ocr-env/model-manifest.json`.
- Model files and Python dependencies are local installation assets excluded from Git. `worker/ocr_local.py` is Glimpse's per-capture CPU/CUDA adapter, initialized while selecting the region and closed after recognition; no model weights were modified. Recognition was not run during installation.

## Hy-MT2 and llama.cpp (additional offline engine)

- Model: Tencent `Hy-MT2-1.8B-Q4_K_M.gguf`, 1,133,080,448 bytes.
- Source repository: https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF
- Pinned model revision: `a0c709d9fac510f2c807aa3af52872340dc37a4a`.
- SHA-256: `dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699`.
- Model license: Apache License 2.0, preserved in `docs/licenses/Hy-MT2-LICENSE.txt`.
- llama.cpp: https://github.com/ggml-org/llama.cpp, pinned official release `b11669`, MIT license preserved in `docs/licenses/llama.cpp-LICENSE.txt`.
- Runtimes: official Windows x64 CPU release plus CUDA 13.4 release and its CUDA runtime archive, installed in separate folders without modifying binary contents. CPU runtime SHA-256: `37b3efcf962847ca869745fd4ccf7f5e7e8dacfeff9fa3523b4ca8cdc66d632b`. CUDA components retain NVIDIA's own license terms; they are not covered by llama.cpp's MIT license. Additional runtime notices, including LLVM OpenMP, remain in the extracted runtime folders.
- Exact download URLs and archive hashes are recorded in `tools/install_hymt.py`; downloaded assets stay outside Git in `.runtime/` and `models/`.
- Upstream model files are unmodified. Glimpse adds a private local server integration, preloads on startup/engine selection, and translates using the model author's documented translation prompt.

Historical installer notices: [0.1.0-preview.1](releases/0.1.0-preview.1-legacy-notices.md).
