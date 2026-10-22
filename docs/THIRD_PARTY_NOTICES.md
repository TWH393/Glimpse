# Local English to Chinese model

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

## Argos English → Chinese (retained)

- Package: `translate-en_zh-1_9.argosmodel`, version 1.9, English → Chinese.
- Source: https://data.argosopentech.com/argospm/v1/translate-en_zh-1_9.argosmodel
- Package size: 70,743,021 bytes.
- SHA-256: `433e7c4f034d87fbe2353161e05f18646d7999452f801a4e1f0378522b9850ab`.
- Package author's clarification about Argos model binaries: https://github.com/argosopentech/argos-translate/issues/533 (MIT/CC0).
- The actual English–Chinese package README additionally identifies the original OPUS model as **CC BY 4.0**. Preserve that upstream attribution as well; the Argos software license does not replace upstream notices.

## Attribution supplied in the model package

English - Chinese version 1.9

Authors: Jörg Tiedemann and Santhosh Thottingal

Title: “OPUS-MT — Building open translation services for the World”

Book Title: Proceedings of the 22nd Annual Conference of the European Association for Machine Translation (EAMT)

Year: 2020

Location: Lisbon, Portugal

The original OPUS model from which this packaged model is derived is licensed CC-BY 4.0.

License: https://creativecommons.org/licenses/by/4.0/

The packaged translation weights and SentencePiece tokenizer are installed without modification. Glimpse uses spaCy's blank language tokenizer and rule-based sentencizer for sentence boundaries. It does not load the package's legacy Stanza checkpoint or download sentence-boundary models. The package's original README and tokenizer resources remain unchanged inside `models/installed/translate-en_zh-1_9/`. Previously generated Stanza compatibility metadata is no longer used.

## Runtime dependencies

Argos Translate 1.11.0 is the translation wrapper; CTranslate2 4.8.3 is the inference engine. The exact Python dependency versions installed for this local setup are recorded in `worker/requirements.lock.txt`. Their upstream license files are retained in the environment. A distributable bundle must include the corresponding notices alongside its runtime and model assets.
