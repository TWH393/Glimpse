# Glimpse

English | [简体中文](README.md)

A Windows translation utility prototype built with C#, .NET 10 and WPF. It includes selected-text capture, region capture, a translation window, a tray menu and configurable translation engines.

Repository: [TWH393/Glimpse](https://github.com/TWH393/Glimpse). The selection popup is experimental with known issues and is deferred for further work. This repository contains a development draft; no installer has been released.

The local machine has Hy-MT2-1.8B Q4_K_M and a project-local llama.cpp CUDA runtime installed as the default offline engine. The existing Python 3.12 / Argos English → Chinese 1.9 engine is retained. Translation quality and runtime testing are left to the user. Binary runtimes and models are ignored by Git; `python tools/install_hymt.py` installs the pinned Hy-MT2 assets, while PowerShell 7 and `uv` can run `./tools/install_offline.ps1` to install Argos. See `worker/requirements.lock.txt` and [third-party notices](docs/THIRD_PARTY_NOTICES.md).

Hy-MT2 preloads in the background when the app starts with this engine selected, when switching to it, and after saving its settings. Translation requests wait for the same preload and reuse its process. Preload errors are displayed and the next translation can retry. Manual unload remains effective until the next explicit selection, settings save or translation. GPU mode uses NVIDIA CUDA; the installed CUDA 13.4 x64 runtime matches this machine's driver. CPU mode is available in Settings. Model residency uses RAM and, in GPU mode, VRAM; actual consumption includes caches and buffers in addition to model weights. It normally stays loaded. Automatic unloading requires BOTH five minutes without translation (configurable from 1 to 60) AND NVIDIA VRAM use >=85% or free VRAM <=2048 MiB. Only a pressure-unloaded model automatically reloads after VRAM use stays <70% and free VRAM >3072 MiB for one minute. Sampling is every 15 seconds; unavailable telemetry never triggers guessed actions. Setting the idle interval to 0 disables automatic VRAM management. CPU residency is not controlled by this VRAM policy. Manual unload, switching engines and app exit cancel automatic recovery and stop the private process without deleting files. A Windows Job Object handles process cleanup when the app is terminated. Loading and generation durations are displayed separately. No inference, GPU-offload or memory-usage test has been performed by the agent.

## GPU compatibility

The current integration targets **Windows x64 with NVIDIA CUDA**. The following table distinguishes local usage feedback from untested hardware; it is not a claim that every NVIDIA GPU supports the bundled runtime.

| GPU model or family | Status | Notes |
| --- | --- | --- |
| NVIDIA GeForce RTX 5090 32 GB | Local configuration; the user reported successful local translation and improved responsiveness | Driver 616.92, CUDA 13.4, llama.cpp `b11669`, Hy-MT2-1.8B Q4_K_M. GPU unloading, recovery and performance metrics have not been separately validated |
| Other RTX 50 models, e.g. RTX 5080 / 5070 / 5060 | Untested; compatibility validation pending | Same CUDA integration approach, subject to the specific GPU, driver and runtime compatibility |
| RTX 40 / 30 / 20 families, including laptop GPUs | Untested; compatibility validation pending | The model does not need retraining, but a matching llama.cpp / CUDA runtime may be required; the current CUDA 13.4 package is not guaranteed to work |
| GTX 16 / 10 families and older GPUs | Untested; not listed as supported | GPU architecture, runtime build support and driver requirements require further investigation |
| AMD Radeon / Intel Arc / Intel integrated GPUs | GPU acceleration not integrated | Requires another backend, such as Vulkan, HIP or SYCL; the NVIDIA CUDA package cannot substitute for those backends |

Model weights can be shared, but CUDA capability alone does not establish compatibility with the exact runtime binary. The approximately 1.13 GB model download is not a minimum VRAM requirement: context caches and compute buffers also use memory. Measure consumption and latency on each target GPU. A separate Windows x64 CPU runtime is included, requiring no NVIDIA GPU or CUDA. CPU compatibility and performance still require testing. VRAM monitoring currently reads only the first NVIDIA GPU (GPU 0); multi-GPU selection and AMD/Intel memory monitoring are not integrated. Record the model, driver, runtime version and actual usage result before adding hardware as validated.

## CPU / GPU selection

Select a mode under Hy-MT2 in Settings and save:

| Mode | Behavior on loading |
| --- | --- |
| Auto (default) | Use CPU when NVIDIA GPU 0 is under VRAM pressure; otherwise try GPU, then fall back to CPU on initialization failure |
| Prefer GPU | Always try GPU first, falling back to CPU on initialization failure |
| CPU only | Launch the standalone CPU runtime without CUDA |

Both paths share the same GGUF file. The workbench and selection popup display the actual backend, fallback reason and durations. CUDA0 is explicitly selected and GPU status requires the runtime log to confirm offloaded model layers. Missing telemetry still permits a GPU attempt. Fallback applies to initialization failures; failed translation requests are reported without automatic replay. The chosen device stays active until unloading. Saving a changed mode unloads the old process and preloads with the updated settings. CPU residency uses RAM and does not follow the VRAM unload/reload policy. Manually unload to retry GPU after a CPU fallback.

`python tools/install_hymt.py` installs both runtimes. On Windows x64 computers without a suitable NVIDIA GPU, use `python tools/install_hymt.py --cpu-only` to download only the model and approximately 20 MB CPU runtime and select CPU mode. Existing CUDA files are retained. Both runtimes are pinned to llama.cpp `b11669` with SHA-256 download verification. Model files, runtimes and user configuration are ignored by Git. Installation does not run inference; backend selection, fallback and residency behavior remain pending user validation.

## Build

Open `Glimpse.slnx` in Visual Studio with .NET desktop development support, or run:

```powershell
dotnet build Glimpse.slnx
dotnet run --project WpfApp1/WpfApp1.csproj
```

The original project folder remains `WpfApp1`; the executable is named `Glimpse`.

## Engines

The × beside Paste clears the source and result and cancels any pending workbench translation. Footer controls adjust reading text from 12–30 px in the workbench and selection popup. The sun/moon button in the upper-right corner switches light and dark themes immediately; the moon enables dark mode and the sun restores light mode. Font size and theme are saved automatically and restored on startup.

- API: a complete HTTPS chat-completion endpoint, model name and optional API key.
- Online: a LibreTranslate instance base URL and optional instance key. No public endpoint or free quota is bundled.
- Offline: an Argos Translate Python environment and an installed direct language-pair model. Install `worker/requirements.txt`, configure the interpreter path and import a `.argosmodel` file in Settings.

Region OCR uses RapidOCR 1.4.4 with separate CPU (`.runtime/ocr-env/`) and NVIDIA GPU (`.runtime/ocr-gpu-env/`) environments. Its bundled detection, orientation and Chinese/English recognition models total approximately 16.2 MB per environment, excluding dependencies; Windows OCR language packs are not needed. Run `./tools/install_ocr.ps1` with PowerShell 7 and `uv` for CPU, or add `-Gpu` for GPU installation (also prepares CPU if missing). Dependencies are hash-locked; GPU uses ONNX Runtime GPU 1.23.2, CUDA 12.8 libraries and cuDNN 9.10.2.21 from their official PyPI packages. CPU/GPU ORT distributions are kept in separate environments; no global PATH changes or llama.cpp runtime modifications are made. Models stay in the installed package; `model-manifest.json` records their hashes. The worker disables ONNX Runtime telemetry and Python socket networking and does not download models.

OCR has its own Auto / Prefer GPU / CPU setting, independent of translation. GPU mode requires CUDA to be enabled in all three model sessions; initialization failure or a missing GPU environment falls back to CPU with the actual backend and reason displayed. This confirms CUDA session availability, not that every operator runs on GPU. The OCR process launches and initializes when the capture overlay opens, in parallel with selecting the region. The same process recognizes the screenshot and exits before translation starts. Cancellation, timeout, failure and app exit clean it up; it does not stay resident or run dummy warmup inference. GPU execution, lifecycle behavior, speed and recognition quality remain pending user validation; other NVIDIA cards and AMD/Intel GPU support are not claimed as tested.

Settings also retain explicit Windows OCR and Tesseract options. Their language field applies only to those engines; the current RapidOCR model is fixed to Chinese/English. Windows OCR uses the bundled `worker/ocr.ps1`, Windows PowerShell 5.1 and installed system OCR language packs. Tesseract needs its executable and language data. Recognition supports cancellation and timeout, discards stale results and removes temporary screenshots. Recognized text is passed to the selected translation engine, including an online service if selected. Installation does not perform inference or UI tests.

Ctrl+Alt+T translates selected text where the foreground app exposes a UI Automation text selection. Ctrl+Alt+O captures a region for OCR and translation. Use Escape to cancel region capture. Shortcut keys can be changed in Settings if occupied. Manual paste is available when text selection cannot be read.

Selected text now opens a small non-activating popup near the pointer, automatically after a drag/double-click selection or manually with the selection hotkey. Translation is sent only after clicking Translate in that popup; its result is displayed and copied there without opening or modifying the main workbench. Click outside to dismiss, or pin the popup to keep it open. Closing/replacing it cancels pending requests. Automatic selection popup can be disabled in Settings. Supported apps must expose their selected text through UI Automation; no clipboard hijacking or continuous full-window extraction is used.

## Data and repository hygiene

Settings live in `%LOCALAPPDATA%\Glimpse\settings.json`; API keys are encrypted with Windows user-scoped DPAPI. No translation history is saved. Strict offline mode rejects online engines; the local Python worker blocks Python socket connections. Prepare all dependencies before using it offline.

Build outputs, user files, models, virtual environments and common local secret files are ignored by Git. No project license has been selected; third-party model/runtime notices are documented separately.

## Checks and limitations

```powershell
dotnet run --project tests/Glimpse.Smoke/Glimpse.Smoke.csproj
```

HTTP checks use test doubles, not a real translation service. Real provider calls, installed OCR and offline models, cross-app capture and mixed-DPI multi-monitor behavior still require testing. Requests are limited to 16,000 characters with an explicit error; automatic segmentation, streaming, model downloads and an installer are not implemented yet.
