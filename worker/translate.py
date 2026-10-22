"""One JSON request on stdin, one JSON response on stdout. No downloads."""
import contextlib
import json
import os
import pathlib
import sys
import socket

# Both source runs and Visual Studio output resolve the same project-local models.
script_path = pathlib.Path(__file__).resolve()
project_root = next(
    (folder for folder in script_path.parents if (folder / "Glimpse.slnx").is_file()),
    script_path.parent.parent,
)
model_root = pathlib.Path(os.environ.get("GLIMPSE_HOME", project_root)) / "models"
os.environ["ARGOS_PACKAGES_DIR"] = str(model_root / "installed")
os.environ["XDG_DATA_HOME"] = str(model_root / "data")
os.environ["XDG_CONFIG_HOME"] = str(model_root / "config")
os.environ["XDG_CACHE_HOME"] = str(model_root / "cache")

# Keep model discovery and sentence segmentation local.
os.environ["ARGOS_DEVICE_TYPE"] = "cpu"
os.environ["ARGOS_MODEL_PROVIDER"] = "OPENNMT"
os.environ["ARGOS_CHUNK_TYPE"] = "STANZA"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"


def deny_network(*args, **kwargs):
    raise RuntimeError("离线工作进程禁止联网，请提前准备模型及其依赖")


socket.socket.connect = deny_network
socket.socket.connect_ex = deny_network
socket.create_connection = deny_network
socket.getaddrinfo = deny_network


class LocalSentenceSplitter:
    """Model-free sentence boundaries for the pinned Argos integration.

    Uses spaCy's blank language tokenizer plus its rule-based sentencizer.
    This does not load the package's legacy Stanza checkpoint, download NLP
    models, or change the SentencePiece tokenizer used by the translator.
    """
    def __init__(self, package):
        import spacy

        language = {"zt": "zh", "pb": "pt"}.get(package.from_code, package.from_code)
        try:
            self.nlp = spacy.blank(language)
        except (ImportError, ModuleNotFoundError, KeyError):
            self.nlp = spacy.blank("xx")
        self.nlp.add_pipe("sentencizer")

    def split_sentences(self, text):
        return [sentence.text for sentence in self.nlp(text).sents if sentence.text.strip()]

    def __str__(self):
        return "Glimpse local rule-based sentence splitter"


def execute(request):
    import argostranslate.package as package
    import argostranslate.translate as translate

    # Argos 1.11 selects this constructor when ARGOS_CHUNK_TYPE=STANZA.
    # Override its sentence-boundary adapter before languages are created;
    # never initialize a Stanza pipeline or read a legacy .pt checkpoint.
    translate.StanzaSentencizer = LocalSentenceSplitter

    action = request.get("action")
    if action == "check":
        installed = package.get_installed_packages()
        pairs = [f"{p.from_code} → {p.to_code}" for p in installed]
        return "Argos 可用；已安装模型：" + (", ".join(pairs) or "无，请先导入 .argosmodel")
    if action == "install":
        model = pathlib.Path(request["path"])
        if model.suffix != ".argosmodel" or not model.is_file():
            raise ValueError("请选择存在的 .argosmodel 文件")
        package.install_from_path(model)
        return "模型导入完成，可切换到离线引擎试译。"
    if action != "translate":
        raise ValueError("不支持的操作")
    source, target = request["source"], request["target"]
    if source == target:
        return request["text"]
    installed = package.get_installed_packages()
    if not any(p.from_code == source and p.to_code == target for p in installed):
        raise ValueError(f"请先导入直接翻译模型：{source} → {target}")
    return translate.translate(request["text"], source, target)


if __name__ == "__main__":
    try:
        request = json.load(sys.stdin)
        with contextlib.redirect_stdout(sys.stderr):
            text = execute(request)
        response = {"ok": True, "text": text}
    except Exception as error:
        response = {"ok": False, "error": f"{type(error).__name__}: {error}"}
    print(json.dumps(response, ensure_ascii=False))
