# Glimpse

English | [简体中文](README.md)

A Windows translator with selected-text translation, screenshot OCR, application live captions, and audio/video file translation.

## Download and install

Download `setup.exe` and **all** `.bin` parts from [v0.2.0-preview.1](https://github.com/TWH393/Glimpse/releases/tag/v0.2.0-preview.1). Keep them together and run `setup.exe`; do not merge or extract the parts.

This release contains **1 installer + 5 parts, approximately 6.7 GB**. All uploaded asset sizes and SHA-256 digests were verified against GitHub. Checksums are provided in the `SHA256SUMS.txt` release attachment.

**Development preview.** The offline bundle includes the app, Hy-MT2, English speech recognition, OCR, and CPU/NVIDIA GPU runtimes. Installation is per-user without administrator access. Personal settings and keys are excluded. Release compilation, 13 protocol/configuration checks, a GPU file-translation sample and 6 live-ASR regression cases passed. Real application audio capture, full UI behavior and cross-device installation still require validation.

## Features

- **Text translation:** type, paste, select text or use a hotkey. Hy-MT2 is the sole offline engine; custom APIs and LibreTranslate remain available.
- **Screenshot OCR:** select a region with `Ctrl+Alt+O`; bundled RapidOCR recognizes Chinese and English.
- **Live captions:** capture one application or global output, recognize English locally and display Chinese in a transparent overlay, with timestamped bilingual TXT records.
- **File translation:** import one audio/video file, use its default audio track and export timestamped English/Chinese TXT. Includes pause, resume and cancel-and-save.
- **Daily use:** light/dark themes, font sizing, tray controls, sign-in startup, and performance/power-saving modes.

Live capture and file processing are mutually exclusive. Timings are displayed only. Selection capture depends on the target application's text/copy support; use OCR for unselectable text.

## Requirements

Windows x64. Standard translation/OCR targets Windows 10 22H2 or later; application/global audio capture requires Windows 11.

| Hardware | Status |
| --- | --- |
| NVIDIA RTX 5090 | Local translation usage feedback; latest captions, file processing and installer still need validation |
| Other RTX 50 / 40 / 30 / 20 and laptop GPUs | Not individually tested; matching drivers and CUDA runtimes required |
| GTX and older GPUs | Current CUDA bundle compatibility is not guaranteed; use CPU mode |
| AMD / Intel GPUs, integrated graphics or no discrete GPU | CPU path available; these GPU backends are not integrated |

Translation supports CPU/GPU selection. Live captions default to GPU-only; file processing offers GPU-only or CPU-only. Speed and memory usage depend on the actual hardware.

## Development and packaging

Open `Glimpse.slnx` in Visual Studio with .NET 10/WPF. Models, runtimes and build outputs are excluded from Git.

```powershell
python tools/install_hymt.py
./tools/install_ocr.ps1 -Gpu
./tools/install_speech.ps1
```

OCR/speech installation scripts require PowerShell 7 and `uv`. After preparing the local assets, build an offline installer with Inno Setup, for example:

```powershell
python tools/build_release.py --version 0.2.0-preview.1
```

Outputs: `artifacts/release/<version>/assets/`. Packaging compiles the app without launching it or running inference. Installer and functional validation are left to the user.

## Details

[Live captions](docs/LIVE_CAPTIONS.md) · [File translation](docs/MEDIA_TRANSLATION.md) · [Third-party notices](docs/THIRD_PARTY_NOTICES.md)

Settings: `%LOCALAPPDATA%\Glimpse\settings.json`. API keys use Windows user-scoped DPAPI. Workbench translations are not logged by default; captions and file processing explicitly save text records. No project license has been selected.
