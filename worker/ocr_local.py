"""Initialize while the capture overlay is open, recognize one image, exit."""
import argparse
import contextlib
import gc
import json
import os
from pathlib import Path
import socket
import sys

os.environ["ORT_DISABLE_TELEMETRY"] = "1"
_dll_directories = []


def deny_network(*args, **kwargs):
    raise RuntimeError("Local OCR cannot access the network; install its models first")


socket.socket.connect = deny_network
socket.socket.connect_ex = deny_network
socket.create_connection = deny_network
socket.getaddrinfo = deny_network


def initialize(device):
    import onnxruntime
    onnxruntime.disable_telemetry_events()
    from rapidocr_onnxruntime import RapidOCR

    def create(gpu):
        return RapidOCR(
            det_use_cuda=gpu, cls_use_cuda=gpu, rec_use_cuda=gpu,
            det_use_dml=False, cls_use_dml=False, rec_use_dml=False,
            intra_op_num_threads=min(4, os.cpu_count() or 1), inter_op_num_threads=1,
        )

    note = ""
    if device != "cpu":
        engine = None
        try:
            if "CUDAExecutionProvider" not in onnxruntime.get_available_providers():
                raise RuntimeError("CUDA provider is not installed")
            # These DLLs belong to this private Python environment, not llama.cpp.
            if os.name == "nt":
                folders = list((Path(sys.prefix) / "Lib" / "site-packages" / "nvidia").glob("*/bin"))
                for folder in folders:
                    _dll_directories.append(os.add_dll_directory(str(folder)))
                os.environ["PATH"] = os.pathsep.join(str(folder) for folder in folders) + os.pathsep + os.environ.get("PATH", "")
            onnxruntime.preload_dlls(directory="")
            engine = create(True)
            for session in (engine.text_det.infer.session, engine.text_cls.infer.session, engine.text_rec.session.session):
                if "CUDAExecutionProvider" not in session.get_providers():
                    raise RuntimeError("A model session fell back to CPU during CUDA initialization")
            return engine, "GPU · NVIDIA CUDA", note
        except Exception as error:
            engine = None
            gc.collect()
            note = f"GPU unavailable; using CPU: {type(error).__name__}: {str(error)[:500]}"
    return create(False), "CPU", note


def recognize(engine, request):
    image = Path(request["image"])
    if not image.is_file():
        raise ValueError("Screenshot file does not exist")
    result, _ = engine(str(image))
    return "\n".join(str(line[1]) for line in (result or []) if str(line[1]).strip())


if __name__ == "__main__":
    try:
        parser = argparse.ArgumentParser()
        parser.add_argument("--device", choices=("auto", "gpu", "cpu"), default="auto")
        options = parser.parse_args()
        with contextlib.redirect_stdout(sys.stderr):
            engine, backend, note = initialize(options.device)
        print(json.dumps({"ok": True, "ready": True, "backend": backend, "note": note}, ensure_ascii=False), flush=True)
        line = sys.stdin.readline()
        if not line:
            sys.exit(0)
        request = json.loads(line)
        with contextlib.redirect_stdout(sys.stderr):
            text = recognize(engine, request)
        response = {"ok": True, "text": text, "backend": backend, "note": note}
    except Exception as error:
        response = {"ok": False, "error": f"{type(error).__name__}: {error}"}
    print(json.dumps(response, ensure_ascii=False))
