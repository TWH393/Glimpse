"""Download a pinned public model into the checkout; never load it or run inference."""
import hashlib
import json
from pathlib import Path
from huggingface_hub import snapshot_download

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "distil-whisper/distil-large-v3.5-ct2"
# Revision is resolved once when adding the model, not from main on every install.
REVISION = "9793ccc07920e0f830e1dba0343efcdf0ef8c903"
MODEL_SHA256 = "c58b88b8585ffcd2135fddaaf421ce72cb223b32edea70d156aed1dea319a119"


def main():
    destination = ROOT / "models/speech/distil-large-v3.5"
    snapshot_download(REPOSITORY, revision=REVISION, local_dir=destination,
                      allow_patterns=["model.bin", "*.json", "vocabulary.*", "README.md", "LICENSE*"])
    files = []
    for name in ("model.bin", "config.json", "tokenizer.json"):
        path = destination / name
        if not path.is_file():
            raise RuntimeError(f"Required model file missing: {name}")
    for path in sorted(destination.iterdir()):
        if path.is_file():
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if path.name == "model.bin" and digest != MODEL_SHA256:
                raise RuntimeError("Speech model SHA-256 differs from its pinned upstream LFS object")
            files.append(dict(name=path.name, bytes=path.stat().st_size, sha256=digest))
    (destination / "glimpse-model-manifest.json").write_text(
        json.dumps(dict(repository=REPOSITORY, revision=REVISION, files=files), indent=2), encoding="utf-8")
    print(f"Downloaded {REPOSITORY}@{REVISION} to {destination}. Inference was not run.")


if __name__ == "__main__":
    main()
