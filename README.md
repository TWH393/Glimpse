# Glimpse

[English](README.en.md) | 简体中文

Windows 本地翻译工具：划词翻译、截图识字、应用实时字幕，以及音视频文件转译。

## 下载与安装

在 [v0.2.0-preview.1 发布页](https://github.com/TWH393/Glimpse/releases/tag/v0.2.0-preview.1) 下载 `setup.exe` 和全部 `.bin` 分卷，放在同一文件夹，运行 `setup.exe`。无需合并或单独解压分卷。

本版共 **1 个安装程序 + 5 个分卷，约 6.7 GB**；校验文件为发布附件中的 `SHA256SUMS.txt`。

**开发预览版。** 完整离线包包含主程序、Hy-MT2、英语识别模型、OCR 和 CPU/NVIDIA GPU 环境。安装在当前用户目录，无需管理员权限；个人设置和密钥不随包分发。已通过 Release 编译、13 项协议/配置检查、GPU 文件转译及 6 个同传识别回归场景；真实应用采音、完整界面和跨设备安装仍待验收。

## 功能

- **文字翻译**：输入、粘贴、划词或快捷键取词。离线翻译统一使用 Hy-MT2，也可配置自定义 API 或 LibreTranslate。
- **截图识字**：`Ctrl+Alt+O` 框选后识别并翻译，自带中英文 RapidOCR。
- **实时字幕**：选择应用或全局声音，本地英语识别 → 透明中文字幕，保存带时间戳的中英文本。
- **音视频转译**：导入单个文件，使用默认音轨，导出带起止时间戳的中英对照 TXT；支持暂停、继续、取消保存。
- **日常使用**：深浅主题、字号调整、托盘、开机启动，以及性能/节能模式。

实时字幕与文件转译不能同时运行。耗时只在界面显示，不写入文本。划词依赖目标软件的取词或复制支持，不能选取的文字可用截图识字。

## 设备要求

Windows x64；普通翻译/OCR 面向 Windows 10 22H2 及以上，按应用/全局实时采音需要 Windows 11。

| 设备 | 说明 |
| --- | --- |
| NVIDIA RTX 5090 | 本机已有本地翻译使用反馈；最新同传、文件转译和安装包仍待验收 |
| 其他 RTX 50 / 40 / 30 / 20、笔记本显卡 | 未逐型号验证，需要匹配的驱动和 CUDA 运行时 |
| GTX 及更早显卡 | 不承诺当前 CUDA 包可用，可选择 CPU |
| AMD / Intel 显卡、核显或无独显 | 当前使用 CPU 路径，未接入这些显卡的 GPU 加速 |

翻译支持 CPU/GPU 选择；同传默认仅 GPU。文件转译可选择仅 GPU 或仅 CPU，速度和显存占用以实际设备为准。

## 从源码运行与打包

使用 Visual Studio 打开 `Glimpse.slnx`（.NET 10/WPF）。模型、运行时和构建产物不提交 Git。

```powershell
python tools/install_hymt.py
./tools/install_ocr.ps1 -Gpu
./tools/install_speech.ps1
```

OCR/语音安装脚本需要 PowerShell 7 和 `uv`。准备好本地环境后，可使用 Inno Setup 生成完整离线包，例如：

```powershell
python tools/build_release.py --version 0.2.0-preview.1
```

产物位于 `artifacts/release/<版本号>/assets/`。打包脚本会编译程序，不会启动应用或执行模型推理；安装与功能测试由用户验收。

## 更多说明

[实时字幕](docs/LIVE_CAPTIONS.md) · [音视频转译](docs/MEDIA_TRANSLATION.md) · [第三方声明](docs/THIRD_PARTY_NOTICES.md)

设置保存在 `%LOCALAPPDATA%\Glimpse\settings.json`，API 密钥使用 Windows 当前用户 DPAPI 加密。工作台默认不保存翻译历史；同传和文件转译按功能保存文本记录。项目尚未指定开源许可证。
