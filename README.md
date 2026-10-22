# Glimpse

[English](README.en.md) | 简体中文

Windows 全局翻译工具草稿。支持自定义 API、LibreTranslate 在线实例、Hy-MT2 / Argos 本地翻译，以及 CPU / NVIDIA GPU 截图识字。

项目仓库：[TWH393/Glimpse](https://github.com/TWH393/Glimpse)。划词小窗仍为实验功能，存在已知问题。完整离线安装预览版在 [GitHub Releases](https://github.com/TWH393/Glimpse/releases) 下载。

**状态：开发预览版，安装和真实功能待用户验收。** 默认选择 Hy-MT2 离线引擎，保留 Argos 英译中 1.9。完整安装版自带模型和 OCR 运行时，没有内置公共翻译额度或密钥。真实试译由用户进行。

本地环境位于 `.venv/`，Python 基础运行时位于 `.runtime/`，模型位于 `models/installed/`。这些二进制目录不提交到 Git。在另一台机器上准备好 `uv` 后，可用 PowerShell 7 执行 `./tools/install_offline.ps1` 重建环境及安装模型。版本见 `worker/requirements.lock.txt`，模型来源和上游声明见 [第三方说明](docs/THIRD_PARTY_NOTICES.md)。

Hy-MT2 模型另存于 `models/hy-mt2/`，CUDA 运行时位于 `.runtime/llama/`，独立 CPU 运行时位于 `.runtime/llama-cpu/`。可执行 `python tools/install_hymt.py` 下载固定版本、校验来源哈希并安装，旧 Argos 文件不删除。当前 CUDA 13.4 x64 运行时按本机 NVIDIA 驱动选择，其他电脑可适配 GPU 运行时或使用独立 CPU 路径。

## 下载安装版

下载同一版本的 `setup.exe` 和**全部 `.bin` 分卷**，放在同一个文件夹，运行 `setup.exe`。无需合并分卷，也不要单独打开 `.bin`。安装后从桌面或开始菜单打开 Glimpse，可在 Windows 设置中卸载。

支持 Windows 10 22H2（19045）/ Windows 11 x64，安装在当前用户目录，无需管理员权限。安装期间预留至少 15 GB 可用空间。完整离线版包含 .NET、Python、Hy-MT2、Argos、RapidOCR 和 CPU/NVIDIA GPU 环境，无需另装 Python 或下载模型。无兼容 NVIDIA 显卡时选择 CPU 模式；在线/API 引擎需要自行配置。

安装包未签名，属于开发预览版；安装、真实翻译/OCR 和跨设备兼容性待用户验收。个人设置及 API 密钥不包含在安装包中。已有设置保留，安装版自动定位自带模型和运行时。第三方授权文件随安装包保留。

开发者先准备本地模型和运行环境，再运行 `.\.venv\Scripts\python.exe tools/build_release.py --compiler "D:\Program Files\Inno Setup 7\ISCC.exe"`，产物位于 `artifacts/release/<版本>/assets/`。脚本构建打包，不运行应用或推理；模型与构建产物不提交 Git。

## 显卡适配

当前接入的是 **Windows x64 / NVIDIA CUDA** 后端。下表区分本机使用反馈与尚未验证的型号，不代表所有 NVIDIA 显卡均可直接运行当前安装脚本下载的二进制。

| 显卡型号或系列 | 当前状态 | 说明 |
| --- | --- | --- |
| NVIDIA GeForce RTX 5090 32 GB | 本机使用配置，已收到本地翻译成功与速度改善反馈 | 驱动 616.92；CUDA 13.4；llama.cpp `b11669`；Hy-MT2-1.8B Q4_K_M。GPU 卸载、恢复及性能指标尚未单独验收 |
| 其他 RTX 50 系列，如 RTX 5080 / 5070 / 5060 | 未实测，待适配验证 | 同样使用 CUDA 路线；仍须确认具体显卡、驱动和运行时兼容性 |
| RTX 40 / 30 / 20 系列，包括笔记本型号 | 未实测，待适配验证 | 不需要重新训练模型，但可能需要匹配的 llama.cpp / CUDA 运行时；不能承诺现有 CUDA 13.4 包直接可用 |
| GTX 16 / 10 系列及更早型号 | 未验证，不列为已支持型号 | 需要进一步核对 GPU 架构、运行时编译支持及驱动要求 |
| AMD Radeon / Intel Arc / Intel 核显 | 当前未接入 GPU 加速 | 需另接 Vulkan、HIP 或 SYCL 等后端；不能使用当前 NVIDIA CUDA 包代替 |

- 模型文件可共用，显卡适配主要取决于推理运行时、GPU 架构和驱动。**支持 CUDA 不等于当前二进制已适配该显卡。**
- 约 1.13 GB 是模型文件大小，不是最低显存要求；上下文缓存和计算缓冲也会占显存，具体占用与速度需在对应型号上测量。
- 设置支持“自动 / 优先 GPU / 仅 CPU”，CPU 使用独立 Windows x64 运行时，无需 NVIDIA 显卡或 CUDA。CPU 路径适合核显、无独显或 CUDA 不兼容的电脑；具体 CPU 的兼容性、翻译速度仍需实测。
- 自动显存管理目前只读取首张 NVIDIA 显卡（GPU 0）。多显卡选择和 AMD / Intel 显存监测尚未适配。
- 后续新增验证型号时，应记录具体型号、驱动、运行时版本和用户测试结果，再更新本表。

### CPU / GPU 设备选择

在设置的“Hy-MT2 · 本地翻译”中选择并保存：

| 模式 | 加载时的行为 |
| --- | --- |
| 自动（默认） | NVIDIA GPU 0 显存紧张时使用 CPU，否则先尝试 GPU；GPU 初始化失败或没有确认模型层已加载到 GPU 时回退 CPU |
| 优先 GPU | 加载时始终先尝试 GPU，失败后回退 CPU |
| 仅 CPU | 直接使用独立 CPU 运行时，不加载 CUDA |

两条路径共用同一个 GGUF，不需要下载两份模型。工作台和划词小窗显示实际的 CPU / NVIDIA CUDA、回退原因及耗时。GPU 路径固定使用 CUDA0，只有启动日志确认模型层加载到 GPU 后才标记 GPU；显存数据不可用时，自动模式仍尝试 GPU，失败再回退 CPU。回退仅针对初始化失败；翻译过程出错时直接报告错误，不重复发送请求。

当前设备保持到卸载；切换模式后保存会卸载旧进程，并按当前设置重新预加载。CPU 模式常驻 RAM，不参与显存卸载与自动恢复。想从回退的 CPU 再尝试 GPU，可点“立即卸载模型”后再次翻译。

安装两套运行时：`python tools/install_hymt.py`。无 NVIDIA 显卡的 Windows x64 电脑可运行 `python tools/install_hymt.py --cpu-only`，只下载模型和约 20 MB 的 CPU 运行时，并选中“仅 CPU”；已有 CUDA 文件保留。运行时、模型和用户配置均不提交 Git。两套运行时均固定为 llama.cpp `b11669`，下载后校验 SHA-256。安装不会运行推理；CPU/GPU 选择、回退及显存策略仍由用户实测验收。

### 模型常驻策略

- 程序启动时若已选中 Hy-MT2，会自动预加载；切换到该引擎或保存其设置后也会加载。加载期间界面保持响应，翻译请求等待同一次加载完成并复用进程；失败时显示错误，下次翻译可重试。不修改共享 Ollama 服务；只监听本机回环地址，使用临时鉴权令牌。
- 模型加载后复用。常驻占用内存；GPU 模式还占显存，除权重外还有上下文缓存和计算缓冲，不能把约 1.13 GB 下载大小直接当作运行占用。
- 平时一直保持加载；只有当前 NVIDIA GPU 显存已用 ≥85% 或剩余 ≤2048 MiB，且默认连续 5 分钟没有翻译时，才卸载。空闲时间可设 1–60 分钟；0 关闭显存自动管理。CPU 模式不按显存压力卸载。
- 仅因显存压力卸载的模型，会在显存已用 <70% 且剩余 >3072 MiB 连续一分钟后自动重新加载。高低门槛形成滞回，避免边界附近反复加载；每 15 秒采样，因此判断时间存在一个采样周期的误差。自动恢复不算作用户使用，不重置闲置时长。
- 显存数据通过 NVIDIA 驱动工具读取，只针对当前首张 NVIDIA GPU 的容量计数；不扫描游戏或应用名称。不支持或读取失败时不猜测压力状态，也不继续累计恢复时间。
- 设置页的“立即卸载模型”、切换到其他引擎、退出程序都会释放项目拥有的推理进程，并取消自动恢复计划。模型文件保留，下次主动翻译时仍可重新加载。
- Windows Job Object 将进程生命周期绑定到 Glimpse，包括 Visual Studio 停止调试时的清理。
- 状态栏分别显示加载和翻译耗时；这些值只有用户运行后才有意义，开发阶段未启动模型或测试速度、质量、显存占用。

## 开始开发

使用支持 .NET 10 的 Visual Studio，安装“.NET 桌面开发”工作负载，打开根目录 `Glimpse.slnx`，将 WpfApp1 设为启动项目。已有项目目录暂时保留，程序名与输出文件名为 Glimpse。

```powershell
dotnet build Glimpse.slnx
dotnet run --project WpfApp1/WpfApp1.csproj
```

## 使用

偏好设置顶部提供“开机自启动”开关，切换立即登记或删除当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Glimpse`，无需管理员权限，不需要点击“保存偏好设置”。界面读取系统登记状态，不另外保存一个可能过期的配置值。此版本只添加功能，开发过程中没有替用户开启系统启动项。自启动实际发生在用户登录 Windows 后，使用带引号的程序路径和 `--startup` 参数，并默认收起到托盘；正常手动启动仍显示主窗口。当前引擎为 Hy-MT2 时仍会按既有逻辑后台加载模型。移动/删除程序会使已登记路径失效，移动后重新关闭再开启即可更新；Windows“启动应用”或任务管理器中的禁用设置仍可能阻止启动。登录启动与托盘行为待用户实测。

原文右上角的“×”清空原文和译文，并取消当前工作台翻译。窗口底部可用 A− / A+ 或滑条调整阅读字号（12–30 px），同时作用于原文、译文和划词小窗；右上角日月图标即时切换深浅主题，浅色时显示月亮、深色时显示太阳。字号和主题自动保存，下次启动恢复。

1. 打开“设置”，配置所需引擎并保存。
2. 在其他程序中拖选或双击选中文字，鼠标附近出现小悬浮窗；也可按 Ctrl+Alt+T 手动获取选区。点击小窗的“翻译”才发送翻译请求，结果与复制按钮都留在小窗内，不拉起或写入主工作台。取词依赖目标软件提供 UI Automation 文本接口，不支持时可用手动粘贴或框选。
3. Ctrl+Alt+O 框选屏幕，识别完成后自动使用当前引擎翻译；Esc 取消。
4. 在浮窗中编辑原文、重新翻译、复制译文、置顶，或隐藏到系统托盘。点击主窗口右上角 × 或按 Alt+F4，会弹出“退出程序 / 最小化到任务栏 / 取消”。最小化保留运行状态，退出才停止任务并释放模型；托盘菜单的“退出”直接退出。系统注销或关机不弹出该确认框。

快捷键被占用时，设置页可将字母改成其他 A–Z 或 F1–F12，例如 Ctrl+Alt+F8，保存后立即生效。截图识字按钮无需快捷键。

划词默认先在选区最后一行下方显示一个“翻译”按钮，不展示原文，也不自动发送翻译请求；点击按钮后展开译文卡片并开始翻译。目标软件不提供选区坐标时，按钮放在鼠标下方；靠近屏幕边缘时调整到当前屏幕工作区内。可在设置中关闭自动显示。弹出时不抢走原窗口焦点。翻译过程中及完成后 1 秒内，不因外部点击关闭，也不被新的自动划词结果替换；1 秒后保持显示，直到下一次点击窗外才关闭，不会计时自动消失。保护期内的点击不会延后触发关闭。展开后勾选“固定”可持续保留；关闭按钮和 Esc 始终可主动关闭，并取消未完成请求。键盘选区可用取词快捷键触发。程序只在选择手势后读取当前选区，不持续提取窗口全文；不支持或超时的自动取词保持静默。此交互更新尚未编译或运行测试。

取词优先查找划词结束位置下的 UI Automation 文本控件及其 Raw View 祖先，再尝试焦点控件。接口无结果或超时时，“兼容取词”会在原窗口仍为前台、选择手势未过期且按键已松开的情况下临时发送 Ctrl+C，等待剪贴板序号更新后读取新文字。读取后尝试恢复先前备份的原有格式；若剪贴板在此期间再次更新，则放弃恢复，避免覆盖后续内容。不清空剪贴板来探测复制，不把旧内容当作选区。无法备份原格式时不执行复制，剪贴板占用或系统权限限制可能导致兼容取词失败。密码控件已被接口识别时不使用复制后备。兼容路径可在偏好设置关闭，保存后保留选择；自动划词与 Ctrl+Alt+T 均使用它。WhatsApp 等软件仍需用户实测，无法复制的文字可用框选识字。

## 三类翻译引擎

| 引擎 | 所需配置 |
| --- | --- |
| API | 支持聊天补全协议的完整 HTTPS 地址、模型名、可选密钥；本机地址可使用 HTTP |
| 在线 | LibreTranslate 实例基础地址、可选实例密钥；可用性和费用取决于实例 |
| 离线 · Argos | 安装 Argos Translate 的 Python 环境，以及源语言到目标语言的直接翻译模型 |
| 离线 · Hy-MT2 | GGUF 模型、独立 CPU / CUDA 运行时；可选择自动、优先 GPU、仅 CPU，以及显存紧张时的闲置卸载时间 |

API 地址示意：`https://your-provider.example/v1/chat/completions`，不是可直接使用的服务。在线地址填写实例根地址，程序追加 `/translate`。不内置未经验证的免费公共端点。

严格离线开启时只允许离线引擎，不自动切换联网服务。离线工作进程禁止 Python socket 网络连接，依赖缺失时直接报告错误；离线是否完全可用仍须用实际模型在断网环境验收。

## 本地依赖

草稿版需要自行准备依赖，尚未把模型和本地运行时打包。不要把模型或虚拟环境提交到 Git。

### OCR

使用独立本地 **RapidOCR 1.4.4 / ONNX Runtime CPU 或 NVIDIA CUDA**，模型随 Python 包提供，包含文字检测、方向分类和中英文识别，不依赖 Windows OCR 语言包。CPU 环境位于 `.runtime/ocr-env/`，GPU 环境位于 `.runtime/ocr-gpu-env/`；其他电脑准备好 `uv` 后，用 PowerShell 7 运行 `./tools/install_ocr.ps1` 安装 CPU，或 `./tools/install_ocr.ps1 -Gpu` 安装 GPU（缺少 CPU 环境时一并准备）。安装脚本使用固定版本与哈希锁文件，不执行识别测试。

在偏好设置中可选择 RapidOCR、Windows OCR 或 Tesseract。RapidOCR 当前使用固定中英文模型，忽略下方 OCR 语言字段；不承诺当前模型适用于日文等其他语言。Windows OCR 依赖系统 OCR 语言包，通过 `worker/ocr.ps1` 调用系统 Windows PowerShell 5.1；Tesseract 需要自行配置程序和语言数据。两者的语言字段可填 `eng`、`eng+chi_sim`、`jpn` 等。Windows 一次使用一种识别器，中英组合优先中文。

OCR 设备独立于翻译模型：设置提供“自动 / 优先 GPU / 仅 CPU”，保存后下次框选生效。自动或优先 GPU 在已安装 GPU 环境时尝试 CUDA；初始化失败、任一模型会话未启用 CUDA，或未安装 GPU 环境时回退 CPU，并在状态栏及提示中显示实际设备与回退原因。这项检测确认会话启用了 CUDA，不代表所有算子均在 GPU 执行。

GPU 环境使用 ONNX Runtime GPU `1.23.2`、CUDA 12.8 系列库和 cuDNN `9.10.2.21`，依赖来自其官方 PyPI 包；只在 OCR 子进程中加载独立 DLL，不改全局 PATH，不复用 llama.cpp CUDA 13.4 文件，也不把 CPU/GPU 两个 ONNX Runtime 发行包混装。完整版本和下载哈希见 `worker/ocr-gpu-requirements.lock.txt`，CPU 包依赖替换规则见 `worker/ocr-gpu-overrides.txt`。其他 NVIDIA 型号仍需分别验证，不能视为已经适配 AMD/Intel GPU。

打开框选界面时即启动 OCR 子进程并加载模型，与用户选择区域并行；完成框选后向同一进程发送图片。识别完成后先关闭 OCR，再调用翻译模型；取消、超时、识别失败或退出也关闭该进程。OCR 不常驻，不自动预热推理；识别期间同时占用 RAM，GPU 模式还占 VRAM。实际冷启动耗时、GPU 加速效果和识字质量待用户实测。模型随包保存在各环境的 `Lib/site-packages/rapidocr_onnxruntime/models/`，每套三个模型合计约 16.2 MB，依赖环境另占空间。安装生成 `model-manifest.json` 记录模型名称、大小及 SHA-256；CPU 依赖见 `worker/ocr-requirements.lock.txt`。OCR 工作进程禁用 ONNX Runtime 遥测并阻断 Python socket 联网，不下载模型。识别出的文字交给用户当前选择的翻译引擎，若选在线引擎，文字仍会发送至相应服务。

识字支持取消和超时；取消后不回填过期结果，临时截图在流程结束时删除。开发过程未运行识别、推理或界面测试。

### 离线翻译

用兼容 Argos 依赖的 Python 环境安装：

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r worker/requirements.txt
```

把 `.venv\Scripts\python.exe` 的完整路径填入设置。使用“检查离线依赖”查看环境与已装模型，使用“导入 Argos 模型”导入可信来源的 `.argosmodel` 文件。模型的语言方向必须与界面选择一致，暂不自动下载模型。依赖兼容性和质量还需要在选定模型上实测。

## 配置与 Git

- 设置保存在 `%LOCALAPPDATA%\Glimpse\settings.json`，API 密钥使用当前 Windows 用户的 DPAPI 加密。
- 默认不记录翻译历史。临时截图仅用于所选区域 OCR，流程结束后删除。
- 划词交互排查日志位于 `%LOCALAPPDATA%\Glimpse\selection-popup.log`，最多保留当前进程最近 60 条界面事件，只记录按钮点击、展开/关闭及窗口尺寸，不记录原文、译文或密钥。
- `.gitignore` 排除 `.vs`、`bin`、`obj`、用户设置、虚拟环境、模型、日志及常见本地密钥文件；请勿把密钥直接写进源码。
- 上传前运行 `git status` 检查待提交文件。项目尚未指定开源许可证；第三方模型及运行时的声明单独列在第三方说明中。

## 验证

```powershell
dotnet run --project tests/Glimpse.Smoke/Glimpse.Smoke.csproj
```

协议检查使用本机模拟 HTTP 服务验证请求、响应和错误处理，不代表真实翻译质量。还检查取消、长度限制、离线拦截和密钥序列化。

尚待真实验证：外部服务翻译、安装后的 Tesseract OCR、Argos 模型翻译、不同软件取词、多显示器混合缩放以及登录自启动。每次翻译限 16,000 字符，超出明确提示，暂不自动分段。没有流式输出、模型自动下载或安装包。

更多需求和后续阶段见 [PLAN.md](PLAN.md)。
