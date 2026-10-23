"""Install pinned Hy-MT2 with private CPU/CUDA runtimes. No inference is run."""
from concurrent.futures import ThreadPoolExecutor
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REVISION = "a0c709d9fac510f2c807aa3af52872340dc37a4a"
RELEASE = "b11669"
MODEL = "Hy-MT2-1.8B-Q4_K_M.gguf"
FILES = [
    (f"https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/{REVISION}/{MODEL}",
     ROOT / "models" / "hy-mt2" / MODEL,
     "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699"),
    (f"https://github.com/ggml-org/llama.cpp/releases/download/{RELEASE}/llama-{RELEASE}-bin-win-cuda-13.4-x64.zip",
     ROOT / ".runtime" / "downloads" / "llama-cuda.zip",
     "3b92d5c064f5032e603e531d3ec8961f3bd6613e5dfad047399a92dec8e95dbc"),
    (f"https://github.com/ggml-org/llama.cpp/releases/download/{RELEASE}/cudart-llama-bin-win-cuda-13.4-x64.zip",
     ROOT / ".runtime" / "downloads" / "cudart.zip",
     "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"),
    (f"https://github.com/ggml-org/llama.cpp/releases/download/{RELEASE}/llama-{RELEASE}-bin-win-cpu-x64.zip",
     ROOT / ".runtime" / "downloads" / "llama-cpu.zip",
     "37b3efcf962847ca869745fd4ccf7f5e7e8dacfeff9fa3523b4ca8cdc66d632b"),
]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(item):
    url, target, expected = item
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.is_file() and digest(target) == expected:
        print(f"Already downloaded: {target.name}", flush=True)
        return target
    temporary = target.with_suffix(target.suffix + ".part")
    request = urllib.request.Request(url, headers={"User-Agent": "Glimpse-model-installer"})
    print(f"Downloading {target.name}", flush=True)
    with urllib.request.urlopen(request, timeout=90) as response, temporary.open("wb") as output:
        shutil.copyfileobj(response, output, length=1024 * 1024)
    if digest(temporary) != expected:
        raise RuntimeError(f"Download checksum mismatch: {target.name}; not installing")
    temporary.replace(target)
    print(f"Saved {target.name} ({target.stat().st_size / 1_000_000:.0f} MB)", flush=True)
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cpu-only", action="store_true", help="Install CPU runtime and model without downloading CUDA; select CPU mode")
    options = parser.parse_args()
    selected = [FILES[0], FILES[3]] if options.cpu_only else FILES
    with ThreadPoolExecutor(max_workers=3) as pool:
        files = list(pool.map(download, selected))
    for archive in files[1:]:
        runtime = ROOT / ".runtime" / ("llama-cpu" if archive.name == "llama-cpu.zip" else "llama")
        runtime.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive) as package:
            for member in package.infolist():
                target = (runtime / member.filename).resolve()
                if not target.is_relative_to(runtime.resolve()):
                    raise RuntimeError("Unsafe path in runtime archive")
            package.extractall(runtime)
    cpu_server = next((ROOT / ".runtime" / "llama-cpu").rglob("llama-server.exe"))
    licenses = ROOT / "docs" / "licenses"
    licenses.mkdir(parents=True, exist_ok=True)
    urllib.request.urlretrieve(
        f"https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/{REVISION}/LICENSE.txt",
        licenses / "Hy-MT2-LICENSE.txt",
    )
    urllib.request.urlretrieve(
        f"https://raw.githubusercontent.com/ggml-org/llama.cpp/{RELEASE}/LICENSE",
        licenses / "llama.cpp-LICENSE.txt",
    )
    import os
    config = Path(os.environ["LOCALAPPDATA"]) / "Glimpse" / "settings.json"
    settings = json.loads(config.read_text(encoding="utf-8-sig")) if config.exists() else {}
    if config.exists():
        shutil.copyfile(config, config.with_suffix(".json.before-hymt"))
    settings.update(Engine=2, Offline=True, LocalModel=str(files[0]), CpuLlamaServer=str(cpu_server))
    if options.cpu_only:
        settings.update(DeviceMode=2, UseGpu=False)
    else:
        server = next((ROOT / ".runtime" / "llama").rglob("llama-server.exe"))
        settings["LlamaServer"] = str(server)
        settings.setdefault("DeviceMode", 0 if settings.get("UseGpu", True) else 2)
    settings.setdefault("IdleMinutes", 5)
    config.parent.mkdir(parents=True, exist_ok=True)
    temporary = config.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(settings, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(config)
    print("Installed Hy-MT2 Q4 and CPU runtime" + ("." if options.cpu_only else " plus CUDA runtime.")
          + " No model was loaded or tested.", flush=True)


if __name__ == "__main__":
    main()
