"""Phase 3 completely-offline Local Model Load Gate for a prepared SigLIP 2 artifact.

This utility is a development/test harness.  It accepts a local artifact directory only;
it never accepts a Hugging Face model identifier, invokes snapshot_download, or uses an
online fallback.  It verifies the existing provenance manifest before loading.
"""
from __future__ import annotations

import argparse
import ctypes
import gc
import hashlib
import json
import os
import socket
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable


class _ProcessMemoryCountersEx(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


def process_memory() -> dict[str, int | None]:
    if os.name != "nt":
        return {"working_set_bytes": None, "peak_working_set_bytes": None, "private_bytes": None}
    counters = _ProcessMemoryCountersEx()
    counters.cb = ctypes.sizeof(_ProcessMemoryCountersEx)
    process = ctypes.windll.kernel32.GetCurrentProcess()
    ok = ctypes.windll.psapi.GetProcessMemoryInfo(process, ctypes.byref(counters), counters.cb)
    if not ok:
        return {"working_set_bytes": None, "peak_working_set_bytes": None, "private_bytes": None}
    return {
        "working_set_bytes": int(counters.WorkingSetSize),
        "peak_working_set_bytes": int(counters.PeakWorkingSetSize),
        "private_bytes": int(counters.PrivateUsage),
    }


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_and_verify_provenance(model_root: Path) -> dict[str, Any]:
    provenance_path = model_root / "provenance.json"
    if not provenance_path.is_file():
        raise RuntimeError(f"missing provenance manifest: {provenance_path}")
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    if provenance.get("runtime_network_allowed") is not False:
        raise RuntimeError("provenance does not prohibit runtime network access")
    revision = provenance.get("revision")
    if not revision or revision != provenance.get("resolved_revision"):
        raise RuntimeError("provenance revision is missing or unresolved")
    for entry in provenance.get("files", []):
        relative_path = entry.get("relative_path")
        if not isinstance(relative_path, str) or not relative_path or Path(relative_path).is_absolute() or ".." in Path(relative_path).parts:
            raise RuntimeError("provenance contains unsafe artifact relative path")
        artifact_path = model_root / relative_path
        if not artifact_path.is_file():
            raise RuntimeError(f"manifest file is missing: {relative_path}")
        if artifact_path.stat().st_size != int(entry["byte_size"]):
            raise RuntimeError(f"manifest size mismatch: {relative_path}")
        if sha256_file(artifact_path) != entry["sha256"]:
            raise RuntimeError(f"manifest SHA-256 mismatch: {relative_path}")
    required = ["config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer.model", "tokenizer_config.json"]
    missing = [name for name in required if not (model_root / name).is_file()]
    if missing:
        raise RuntimeError(f"artifact lacks required local files: {', '.join(missing)}")
    return provenance


@dataclass
class NetworkAttempt:
    operation: str
    address: str


class OfflineNetworkGuard:
    """Fail closed and count attempted socket connections in addition to HF offline flags."""

    def __init__(self) -> None:
        self.attempts: list[NetworkAttempt] = []
        self._connect: Callable[..., Any] | None = None
        self._create_connection: Callable[..., Any] | None = None

    def __enter__(self) -> "OfflineNetworkGuard":
        self._connect = socket.socket.connect
        self._create_connection = socket.create_connection

        def blocked_connect(instance: socket.socket, address: Any) -> Any:
            self.attempts.append(NetworkAttempt("socket.connect", repr(address)))
            raise RuntimeError("network access is prohibited by the Offline Model Load Gate")

        def blocked_create_connection(address: Any, *args: Any, **kwargs: Any) -> Any:
            self.attempts.append(NetworkAttempt("socket.create_connection", repr(address)))
            raise RuntimeError("network access is prohibited by the Offline Model Load Gate")

        socket.socket.connect = blocked_connect  # type: ignore[assignment]
        socket.create_connection = blocked_create_connection  # type: ignore[assignment]
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        assert self._connect is not None and self._create_connection is not None
        socket.socket.connect = self._connect  # type: ignore[assignment]
        socket.create_connection = self._create_connection  # type: ignore[assignment]


def create_fixture(path: Path) -> None:
    from PIL import Image, ImageDraw

    path.parent.mkdir(parents=True, exist_ok=True)
    image = Image.new("RGB", (448, 224), (27, 91, 153))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 112, 448, 224), fill=(13, 102, 166))
    draw.ellipse((270, 22, 322, 74), fill=(255, 220, 125))
    for y in (135, 164, 193):
        draw.arc((-30, y - 18, 170, y + 18), 195, 345, fill=(215, 241, 255), width=3)
        draw.arc((145, y - 13, 370, y + 13), 195, 345, fill=(215, 241, 255), width=3)
    image.save(path, format="PNG")


def now_ms() -> float:
    return time.perf_counter_ns() / 1_000_000.0


def synchronize(torch: Any, device: str) -> None:
    if device == "cuda":
        torch.cuda.synchronize()


def run_profile(backend: str, model_root: Path, fixture_path: Path) -> dict[str, Any]:
    import torch
    from PIL import Image
    from transformers import AutoImageProcessor, AutoModel, PreTrainedTokenizerFast

    if backend not in {"cpu", "cuda"}:
        raise ValueError(f"unsupported backend: {backend}")
    if backend == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA profile requested but torch.cuda.is_available() is false")
    device = torch.device(backend)
    if backend == "cuda":
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()

    processor_start = now_ms()
    image_processor = AutoImageProcessor.from_pretrained(str(model_root), local_files_only=True)
    tokenizer_config = json.loads((model_root / "tokenizer_config.json").read_text(encoding="utf-8"))
    tokenizer_kwargs = {
        key: tokenizer_config[key]
        for key in ("unk_token", "bos_token", "eos_token", "pad_token", "additional_special_tokens", "add_bos_token", "add_eos_token", "clean_up_tokenization_spaces")
        if key in tokenizer_config
    }
    configured_model_max_length = int(tokenizer_config.get("model_max_length", 64))
    tokenizer_kwargs["model_max_length"] = min(max(configured_model_max_length, 1), 64)
    tokenizer = PreTrainedTokenizerFast(tokenizer_file=str(model_root / "tokenizer.json"), **tokenizer_kwargs)
    processor_load_ms = now_ms() - processor_start

    model_start = now_ms()
    model = AutoModel.from_pretrained(str(model_root), local_files_only=True, torch_dtype=torch.float32)
    model.to(device)
    model.eval()
    synchronize(torch, backend)
    model_load_ms = now_ms() - model_start

    with Image.open(fixture_path) as opened:
        image = opened.convert("RGB")
        preprocessing_start = now_ms()
        image_inputs = image_processor(images=image, return_tensors="pt")
        text_inputs = tokenizer(
            ["海が映っている", "a view of the sea"],
            padding="max_length",
            truncation=True,
            max_length=int(tokenizer.model_max_length),
            return_tensors="pt",
        )
        preprocessing_ms = now_ms() - preprocessing_start

    def move_inputs(values: dict[str, Any]) -> dict[str, Any]:
        return {name: value.to(device) if hasattr(value, "to") else value for name, value in values.items()}

    image_inputs = move_inputs(image_inputs)
    text_inputs = move_inputs(text_inputs)

    initial_start = now_ms()
    with torch.inference_mode():
        image_features = model.get_image_features(pixel_values=image_inputs["pixel_values"])
        text_features = model.get_text_features(input_ids=text_inputs["input_ids"], attention_mask=text_inputs.get("attention_mask"))
        image_features = torch.nn.functional.normalize(image_features, dim=-1)
        text_features = torch.nn.functional.normalize(text_features, dim=-1)
        similarities = (text_features @ image_features.T).flatten()
    synchronize(torch, backend)
    initial_inference_ms = now_ms() - initial_start

    warm_start = now_ms()
    with torch.inference_mode():
        warm_image_features = model.get_image_features(pixel_values=image_inputs["pixel_values"])
        warm_text_features = model.get_text_features(input_ids=text_inputs["input_ids"], attention_mask=text_inputs.get("attention_mask"))
        warm_image_features = torch.nn.functional.normalize(warm_image_features, dim=-1)
        warm_text_features = torch.nn.functional.normalize(warm_text_features, dim=-1)
        warm_similarities = (warm_text_features @ warm_image_features.T).flatten()
    synchronize(torch, backend)
    warm_inference_ms = now_ms() - warm_start

    image_vector = image_features[0].detach().float().cpu()
    text_vectors = text_features.detach().float().cpu()
    max_score_delta = float(torch.max(torch.abs(similarities.detach().float().cpu() - warm_similarities.detach().float().cpu())).item())
    vram = {
        "allocated_bytes": int(torch.cuda.memory_allocated()) if backend == "cuda" else 0,
        "reserved_bytes": int(torch.cuda.memory_reserved()) if backend == "cuda" else 0,
        "peak_allocated_bytes": int(torch.cuda.max_memory_allocated()) if backend == "cuda" else 0,
        "peak_reserved_bytes": int(torch.cuda.max_memory_reserved()) if backend == "cuda" else 0,
    }
    result = {
        "backend": backend,
        "dtype": "float32",
        "processor_load_ms": processor_load_ms,
        "image_processor_class": type(image_processor).__name__,
        "tokenizer_class": type(tokenizer).__name__,
        "model_load_ms": model_load_ms,
        "preprocessing_ms": preprocessing_ms,
        "initial_inference_ms": initial_inference_ms,
        "warm_inference_ms": warm_inference_ms,
        "image_dimension": int(image_vector.numel()),
        "image_l2_norm": float(torch.linalg.vector_norm(image_vector).item()),
        "text_l2_norms": [float(torch.linalg.vector_norm(row).item()) for row in text_vectors],
        "queries": ["海が映っている", "a view of the sea"],
        "cosine_similarities": [float(value) for value in similarities.detach().float().cpu().tolist()],
        "warm_similarity_max_abs_delta": max_score_delta,
        "vram": vram,
        "memory": process_memory(),
    }
    del warm_image_features, warm_text_features, image_features, text_features, model, image_processor, tokenizer
    gc.collect()
    if backend == "cuda":
        torch.cuda.empty_cache()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Run a fully-offline local SigLIP 2 model load gate.")
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--run-id", required=True)
    args = parser.parse_args()

    for setting in ("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE"):
        if os.environ.get(setting) != "1":
            raise RuntimeError(f"{setting}=1 is required for this gate")
    os.environ.setdefault("HF_DATASETS_OFFLINE", "1")
    os.environ.setdefault("PYTHONDONTWRITEBYTECODE", "1")

    model_root = Path(args.model_root).resolve()
    fixture_path = Path(args.fixture).resolve()
    output_path = Path(args.output).resolve()
    if not model_root.is_dir():
        raise RuntimeError(f"model root does not exist: {model_root}")
    create_fixture(fixture_path)

    gate_started = now_ms()
    with OfflineNetworkGuard() as network_guard:
        provenance = load_and_verify_provenance(model_root)
        import torch
        import transformers

        profiles = [run_profile("cpu", model_root, fixture_path)]
        if torch.cuda.is_available():
            profiles.append(run_profile("cuda", model_root, fixture_path))
        else:
            raise RuntimeError("CUDA load is required by the Phase 3 Offline Model Load Gate")
    if network_guard.attempts:
        raise RuntimeError(f"runtime attempted network access: {network_guard.attempts}")

    output = {
        "schema_version": 1,
        "gate": "phase3-completely-offline-local-model-load",
        "run_id": args.run_id,
        "completed_at_utc": datetime.now(timezone.utc).isoformat(),
        "runtime_network_access_count": len(network_guard.attempts),
        "portable_python_executable": sys.executable,
        "model_root": str(model_root),
        "model_id": provenance["model_id"],
        "model_revision": provenance["revision"],
        "resolved_revision": provenance["resolved_revision"],
        "runtime_network_allowed_by_provenance": provenance["runtime_network_allowed"],
        "torch_version": torch.__version__,
        "transformers_version": transformers.__version__,
        "cuda_available": torch.cuda.is_available(),
        "cuda_version": torch.version.cuda,
        "cuda_device_count": torch.cuda.device_count(),
        "fixture": str(fixture_path),
        "gate_total_ms": now_ms() - gate_started,
        "profiles": profiles,
        "worker_memory_after_profiles": process_memory(),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary_path.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary_path, output_path)
    print(json.dumps({"outcome": "passed", "output": str(output_path), "run_id": args.run_id}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"outcome": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(1)
