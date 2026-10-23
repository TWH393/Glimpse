"""Build the complete offline Windows installer; does not run Glimpse or inference."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def copy_tree(source, target):
    if not source.is_dir():
        raise RuntimeError(f"Required offline directory is missing: {source}")
    shutil.copytree(source, target, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.pyo", "_virtualenv.pth", "_virtualenv.py"))


def copy_python(environment, destination):
    # venv launchers contain an absolute 'home'. Ship a real CPython layout
    # (python.exe + DLLs + Lib) instead, with the installed packages overlaid.
    config = (environment / "pyvenv.cfg").read_text(encoding="utf-8")
    home = next(line.split("=", 1)[1].strip() for line in config.splitlines() if line.startswith("home ="))
    base = Path(home)
    if not (base / "python.exe").is_file():
        raise RuntimeError(f"Base Python is missing: {base}")
    copy_tree(base, destination)
    copy_tree(environment / "Lib" / "site-packages", destination / "Lib" / "site-packages")


def run(command):
    print("Running:", " ".join(map(str, command)), flush=True)
    subprocess.run(list(map(str, command)), cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="0.2.0-preview.1")
    parser.add_argument("--compiler", type=Path, default=Path(r"D:\Program Files\Inno Setup 7\ISCC.exe"))
    parser.add_argument("--reuse-runtimes", action="store_true", help="Reuse staged runtimes and models for the same version")
    args = parser.parse_args()
    version = args.version
    if not version or any(char not in "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-" for char in version):
        raise ValueError("Invalid release version")
    output = ROOT / "artifacts" / "release" / version
    payload = output / "payload"
    assets = output / "assets"
    if args.reuse_runtimes:
        staged = payload / "bundle.json"
        if not staged.is_file() or json.loads(staged.read_text(encoding="utf-8")).get("bundle_schema") != 2:
            raise RuntimeError("Staged runtimes use an obsolete layout. Build without --reuse-runtimes.")
    elif payload.exists():
        # Only remove this generated version's payload, never the source runtimes.
        if payload.resolve().parent != output.resolve() or not payload.resolve().is_relative_to((ROOT / "artifacts" / "release").resolve()):
            raise RuntimeError("Unsafe staging path")
        shutil.rmtree(payload)
    output.mkdir(parents=True, exist_ok=True)
    assets.mkdir(exist_ok=True)
    run(["dotnet", "publish", "WpfApp1/WpfApp1.csproj", "-c", "Release", "-r", "win-x64",
         "--self-contained", "true", f"-p:Version={version}", "-p:DebugType=None", "-p:DebugSymbols=false",
         "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", payload, "--nologo"])

    for name in (() if args.reuse_runtimes else ("llama", "llama-cpu")):
        print(f"Staging {name}", flush=True)
        copy_tree(ROOT / ".runtime" / name, payload / ".runtime" / name)
    for name, destination in (() if args.reuse_runtimes else ((".runtime/ocr-env", ".runtime/ocr-env"),
                              (".runtime/ocr-gpu-env", ".runtime/ocr-gpu-env"), (".runtime/speech-env", ".runtime/speech-env"))):
        print(f"Staging relocatable Python: {name}", flush=True)
        copy_python(ROOT / name, payload / destination)
    for name in (() if args.reuse_runtimes else ("hy-mt2", "speech")):
        copy_tree(ROOT / "models" / name, payload / "models" / name)
    copy_tree(ROOT / "docs", payload / "docs")
    for name in ("README.md", "README.en.md"):
        shutil.copy2(ROOT / name, payload / name)
    manifest = {"bundle_schema": 2, "version": version, "platform": "win-x64", "offline": True,
                "python_layout": "standalone", "translation": ["Hy-MT2-1.8B-Q4_K_M"],
                "speech": "Distil-Whisper-large-v3.5", "ocr": "RapidOCR-1.4.4", "inference_tested": False}
    (payload / "bundle.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    if not args.compiler.is_file():
        raise RuntimeError(f"Inno Setup compiler not found: {args.compiler}")
    run([args.compiler, "/Qp", f"/DAppVersion={version}", f"/DPayloadDir={payload}",
         f"/DAssetDir={assets}", ROOT / "installer" / "Glimpse.iss"])
    instructions = (
        f"Glimpse {version} - Windows x64 完整离线预览版\n\n"
        "下载同一版本的 setup.exe 和全部 .bin 文件，放在同一个文件夹，运行 setup.exe。\n"
        "不要单独打开 .bin，不需要合并或自行解压。安装后通过桌面或开始菜单打开 Glimpse。\n"
        "自带 .NET、Hy-MT2、英语语音识别、中英文 OCR 及 CPU/NVIDIA GPU 环境。\n"
        "API/在线翻译仍需要自行配置服务。没有代码签名证书，发布的是开发预览版。\n"
        "安装及真实翻译/OCR/其他显卡兼容性尚未验收；可按实际设备切换 CPU 模式。\n"
    )
    (assets / "INSTALL-zh-CN.txt").write_text(instructions, encoding="utf-8-sig")
    hashes = []
    for file in sorted(assets.iterdir()):
        if not file.is_file() or file.name == "SHA256SUMS.txt":
            continue
        if file.stat().st_size >= 2 * 1024**3:
            raise RuntimeError(f"Release asset exceeds GitHub's file limit: {file.name}")
        with file.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        hashes.append(f"{digest}  {file.name}")
        print(f"Asset: {file.name} ({file.stat().st_size / 1024**2:.1f} MiB)", flush=True)
    (assets / "SHA256SUMS.txt").write_text("\n".join(hashes) + "\n", encoding="ascii")
    print(f"Installer assets ready: {assets}", flush=True)


if __name__ == "__main__":
    main()
