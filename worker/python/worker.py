"""Local Media AI loopback Worker.

The worker is a stateless compute boundary.  It never imports SQLite, never accepts a
SQLite/database path, and never writes durable application state.  C# owns all jobs,
analysis rows, cache publication and user-visible status.
"""
from __future__ import annotations

import argparse
import base64
import gc
import hashlib
import hmac
import json
import os
import platform
import socket
import sys
import threading
import time
from concurrent import futures
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable

PROTOCOL_VERSION = 1
WORKER_VERSION = "0.3.0-phase3"
TOKEN_METADATA_KEY = "x-local-media-ai-token"
READY_KIND = "ready"
CAPABILITIES = ("health", "version", "diagnostic", "shutdown", "resource-snapshot-v1", "model-info-v1", "visual-embedding-v1", "text-embedding-v1")
DEFAULT_MODEL_ID = "google/siglip2-base-patch16-224"


@dataclass(frozen=True)
class EmbeddingModelSpec:
    model_id: str
    revision: str
    processor_version: str
    processor_mode: str
    required_files: tuple[str, ...]


MODEL_SPECS = {
    "google/siglip2-base-patch16-224": EmbeddingModelSpec("google/siglip2-base-patch16-224", "75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2", "siglip2-processor-v1", "siglip_fast", ("config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer_config.json")),
    "google/siglip2-so400m-patch14-384": EmbeddingModelSpec("google/siglip2-so400m-patch14-384", "e8e487298228002f3d8a82e0cd5c8ea9c567f57f", "siglip2-so400m-processor-v1", "siglip_fast", ("config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer_config.json", "tokenizer.model")),
    "facebook/metaclip-2-worldwide-l14": EmbeddingModelSpec("facebook/metaclip-2-worldwide-l14", "f48c6b1784e33f2211894af4d9f1f08381f04f12", "metaclip2-l14-processor-v1", "auto", ("config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer_config.json", "sentencepiece.bpe.model")),
    "google/siglip2-base-patch16-256": EmbeddingModelSpec("google/siglip2-base-patch16-256", "3f9f96cb90da5dbc758b01813f2f6f1aee24c1ab", "siglip2-base-256-processor-v1", "siglip_fast", ("config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer_config.json", "tokenizer.model")),
    "llm-jp/llm-jp-clip-vit-base-patch16": EmbeddingModelSpec("llm-jp/llm-jp-clip-vit-base-patch16", "e3b65fb4a871bacde924b3613dae0eccb7835e95", "llm-jp-openclip-2.31-local-adapter-v1", "openclip_local", ("open_clip_config.json", "open_clip_model.safetensors", "tokenizer.json", "tokenizer_config.json")),
}


@dataclass(frozen=True)
class WorkerInfo:
    protocol_version: int
    worker_version: str
    python_version: str
    process_id: int
    serving: bool
    backend_available: bool
    backend_detail: str


@dataclass
class EmbeddingRuntime:
    model_root: Path
    adapter_root: Path | None
    device: str
    torch: Any
    model: Any
    image_processor: Any
    text_processor: Any
    spec: EmbeddingModelSpec


_runtime_lock = threading.Lock()
_runtime: EmbeddingRuntime | None = None


def configured_protocol_version() -> int:
    """Test-only override is inherited by the child, never logged or persisted."""
    override = os.environ.get("LOCAL_MEDIA_AI_WORKER_PROTOCOL_VERSION_OVERRIDE")
    if not override:
        return PROTOCOL_VERSION
    try:
        return int(override)
    except ValueError:
        return PROTOCOL_VERSION


def worker_info(protocol_version: int, serving: bool) -> WorkerInfo:
    try:
        import torch  # type: ignore
        import transformers  # type: ignore

        available = True
        detail = f"torch={torch.__version__};transformers={transformers.__version__};cuda={torch.cuda.is_available()}"
    except ImportError:
        available = False
        detail = "Portable AI runtime is unavailable."
    return WorkerInfo(
        protocol_version=protocol_version,
        worker_version=WORKER_VERSION,
        python_version=platform.python_version(),
        process_id=os.getpid(),
        serving=serving,
        backend_available=available,
        backend_detail=detail,
    )


def require_token(expected: str, received: str | None) -> bool:
    return bool(expected and received and hmac.compare_digest(expected, received))


def metadata_token(metadata: Iterable[object]) -> str | None:
    values: list[str] = []
    for item in metadata:
        key = getattr(item, "key", "")
        value = getattr(item, "value", "")
        if key.lower() == TOKEN_METADATA_KEY and isinstance(value, str):
            values.append(value)
    return values[0] if len(values) == 1 else None


def _load_grpc_modules():
    try:
        import grpc  # type: ignore
        import local_media_ai_worker_pb2 as pb2  # type: ignore
        import local_media_ai_worker_pb2_grpc as pb2_grpc  # type: ignore
        return grpc, pb2, pb2_grpc
    except ImportError as exc:
        raise RuntimeError("gRPC worker runtime is unavailable") from exc


def _require_offline() -> None:
    if os.environ.get("HF_HUB_OFFLINE") != "1" or os.environ.get("TRANSFORMERS_OFFLINE") != "1":
        raise RuntimeError("HF_HUB_OFFLINE=1 and TRANSFORMERS_OFFLINE=1 are required for embedding jobs")


def _plain(path: Path) -> Path:
    # resolve() may add the \\?\ long-path prefix to one side only (paths over MAX_PATH), which breaks relative_to.
    text = str(path)
    if text.startswith("\\\\?\\UNC\\"):
        return Path("\\\\" + text[8:])
    if text.startswith("\\\\?\\"):
        return Path(text[4:])
    return path


def _within(root: Path, candidate: Path) -> Path:
    resolved_root = _plain(root.resolve(strict=True))
    resolved_candidate = _plain(candidate.resolve(strict=True))
    try:
        resolved_candidate.relative_to(resolved_root)
    except ValueError as exc:
        raise ValueError("path is outside its declared allowed root") from exc
    return resolved_candidate


def _manifest_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _resolve_model_spec(config: dict[str, Any]) -> EmbeddingModelSpec:
    model_id = str(config.get("model_id", DEFAULT_MODEL_ID))
    try:
        return MODEL_SPECS[model_id]
    except KeyError as exc:
        raise ValueError("embedding model is not in the approved Worker registry") from exc


def _validate_model_root(model_root: Path, models_root: Path, spec: EmbeddingModelSpec) -> Path:
    _require_offline()
    root = _within(models_root, model_root)
    manifest = root / "provenance.json"
    if not manifest.is_file():
        raise ValueError("local model provenance manifest is missing")
    provenance = json.loads(manifest.read_text(encoding="utf-8"))
    if provenance.get("model_id") != spec.model_id or provenance.get("revision") != spec.revision:
        raise ValueError("local model provenance does not match the approved embedding model")
    if provenance.get("runtime_network_allowed") is not False:
        raise ValueError("model provenance does not prohibit runtime network access")
    missing = [name for name in spec.required_files if not (root / name).is_file()]
    if missing:
        raise ValueError("local model artifact is incomplete: " + ", ".join(missing))
    return root


def _load_runtime(model_root: Path, models_root: Path, adapter_root: Path | None, backend: str, spec: EmbeddingModelSpec) -> tuple[EmbeddingRuntime, bool]:
    global _runtime
    model_root = _validate_model_root(model_root, models_root, spec)
    adapter_root = _within(models_root, adapter_root) if adapter_root is not None else None
    requested_device = "cuda" if backend == "cuda" else "cpu"
    with _runtime_lock:
        if _runtime is not None and _runtime.model_root == model_root and _runtime.adapter_root == adapter_root and _runtime.device == requested_device and _runtime.spec == spec:
            return _runtime, True
        import torch  # type: ignore
        from transformers import AutoImageProcessor, AutoModel, AutoProcessor, PreTrainedTokenizerFast  # type: ignore

        if requested_device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was required for this embedding job but is unavailable")
        if spec.processor_mode == "openclip_local":
            if adapter_root is None:
                raise ValueError("OpenCLIP embedding model requires approved local adapter descriptors")
            adapter = adapter_root
            adapter_manifest = adapter / "provenance.json"
            adapter_config = adapter / "llm-jp-clip-vit-base-patch16.json"
            adapter_preprocess = adapter / "preprocess_config.json"
            if not adapter_manifest.is_file() or not adapter_config.is_file() or not adapter_preprocess.is_file():
                raise ValueError("OpenCLIP local adapter artifact is incomplete")
            adapter_provenance = json.loads(adapter_manifest.read_text(encoding="utf-8"))
            if adapter_provenance.get("adapter_id") != "llm-jp/llm-jp-clip-vit-base-patch16-openclip-adapter-v1" or adapter_provenance.get("runtime_network_allowed") is not False or adapter_provenance.get("pretrained_hf") is not False:
                raise ValueError("OpenCLIP local adapter provenance is invalid")
            try:
                import open_clip  # type: ignore
            except ImportError as exc:
                raise RuntimeError("OpenCLIP Portable runtime is unavailable for the approved model") from exc
            open_clip.add_model_config(str(adapter_config))
            model = open_clip.create_model(adapter_config.stem, pretrained=str(model_root / "open_clip_model.safetensors"), pretrained_hf=False, device=requested_device).eval()
            preprocess_config = json.loads(adapter_preprocess.read_text(encoding="utf-8"))
            image_processor = open_clip.image_transform(model.visual.image_size, is_train=False, **preprocess_config)
            text_processor = open_clip.get_tokenizer(adapter_config.stem)
        elif spec.processor_mode == "siglip_fast":
            tokenizer_config = json.loads((model_root / "tokenizer_config.json").read_text(encoding="utf-8"))
            tokenizer_kwargs = {key: tokenizer_config[key] for key in ("unk_token", "bos_token", "eos_token", "pad_token", "additional_special_tokens", "add_bos_token", "add_eos_token", "clean_up_tokenization_spaces") if key in tokenizer_config}
            tokenizer_kwargs["model_max_length"] = min(max(int(tokenizer_config.get("model_max_length", 64)), 1), 64)
            image_processor = AutoImageProcessor.from_pretrained(str(model_root), local_files_only=True)
            text_processor = PreTrainedTokenizerFast(tokenizer_file=str(model_root / "tokenizer.json"), **tokenizer_kwargs)
            model = AutoModel.from_pretrained(str(model_root), local_files_only=True, torch_dtype=torch.float32)
            model.to(torch.device(requested_device)).eval()
        else:
            image_processor = AutoProcessor.from_pretrained(str(model_root), local_files_only=True)
            text_processor = image_processor
            model = AutoModel.from_pretrained(str(model_root), local_files_only=True, torch_dtype=torch.float32)
            model.to(torch.device(requested_device)).eval()
        if requested_device == "cuda":
            torch.cuda.synchronize()
        if _runtime is not None:
            del _runtime
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        _runtime = EmbeddingRuntime(model_root, adapter_root, requested_device, torch, model, image_processor, text_processor, spec)
        return _runtime, False


def _move_to_device(values: dict[str, Any], device: str) -> dict[str, Any]:
    return {name: value.to(device) if hasattr(value, "to") else value for name, value in values.items()}


def _embedding_result(vector: Any, backend: str, model_cache_hit: bool, started: float, runtime: EmbeddingRuntime) -> dict[str, Any]:
    torch = runtime.torch
    normalized = torch.nn.functional.normalize(vector, dim=-1)[0].detach().float().cpu().contiguous()
    raw = normalized.numpy().tobytes()
    vram = {
        "allocated_bytes": int(torch.cuda.memory_allocated()) if backend == "cuda" else 0,
        "reserved_bytes": int(torch.cuda.memory_reserved()) if backend == "cuda" else 0,
        "peak_allocated_bytes": int(torch.cuda.max_memory_allocated()) if backend == "cuda" else 0,
        "peak_reserved_bytes": int(torch.cuda.max_memory_reserved()) if backend == "cuda" else 0,
    }
    return {
        "schema_version": 1,
        "model_name": runtime.spec.model_id,
        "model_revision": runtime.spec.revision,
        "processor_version": runtime.spec.processor_version,
        "dtype": "float32",
        "backend": backend,
        "dimension": int(normalized.numel()),
        "normalized": True,
        "vector_float32_base64": base64.b64encode(raw).decode("ascii"),
        "metrics": {
            "inference_ms": (time.perf_counter() - started) * 1000.0,
            "model_cache_hit": 1.0 if model_cache_hit else 0.0,
            **{f"vram_{key}": float(value) for key, value in vram.items()},
        },
    }


def _image_embedding(descriptors: dict[str, str], config: dict[str, Any]) -> dict[str, Any]:
    from PIL import Image  # type: ignore

    backend = str(config.get("backend", "cuda"))
    image_path = _within(Path(descriptors["input_root"]), Path(descriptors["image_path"]))
    spec = _resolve_model_spec(config)
    adapter_value = descriptors.get("adapter_root")
    adapter_root = Path(adapter_value) if adapter_value else None
    runtime, cache_hit = _load_runtime(Path(descriptors["model_root"]), Path(descriptors["models_root"]), adapter_root, backend, spec)
    started = time.perf_counter()
    with Image.open(image_path) as opened:
        image = opened.convert("RGB")
        if runtime.spec.processor_mode == "openclip_local":
            inputs = runtime.image_processor(image).unsqueeze(0).to(runtime.device)
        else:
            inputs = _move_to_device(runtime.image_processor(images=image, return_tensors="pt"), runtime.device)
    with runtime.torch.inference_mode():
        vector = runtime.model.encode_image(inputs) if runtime.spec.processor_mode == "openclip_local" else runtime.model.get_image_features(pixel_values=inputs["pixel_values"]) if runtime.spec.processor_mode == "siglip_fast" else runtime.model.get_image_features(**inputs)
    if backend == "cuda":
        runtime.torch.cuda.synchronize()
    return _embedding_result(vector, backend, cache_hit, started, runtime)


def _text_embedding(descriptors: dict[str, str], config: dict[str, Any]) -> dict[str, Any]:
    backend = str(config.get("backend", "cuda"))
    text = descriptors.get("text", "")
    if not text.strip():
        raise ValueError("text embedding requires non-empty text")
    spec = _resolve_model_spec(config)
    adapter_value = descriptors.get("adapter_root")
    adapter_root = Path(adapter_value) if adapter_value else None
    runtime, cache_hit = _load_runtime(Path(descriptors["model_root"]), Path(descriptors["models_root"]), adapter_root, backend, spec)
    started = time.perf_counter()
    if runtime.spec.processor_mode == "openclip_local":
        tokens = runtime.text_processor([text]).to(runtime.device)
    else:
        tokens = runtime.text_processor([text], padding="max_length", truncation=True, max_length=int(runtime.text_processor.model_max_length), return_tensors="pt") if runtime.spec.processor_mode == "siglip_fast" else runtime.text_processor(text=[text], padding=True, return_tensors="pt")
        tokens = _move_to_device(tokens, runtime.device)
    with runtime.torch.inference_mode():
        vector = runtime.model.encode_text(tokens) if runtime.spec.processor_mode == "openclip_local" else runtime.model.get_text_features(input_ids=tokens["input_ids"], attention_mask=tokens.get("attention_mask")) if runtime.spec.processor_mode == "siglip_fast" else runtime.model.get_text_features(**tokens)
    if backend == "cuda":
        runtime.torch.cuda.synchronize()
    return _embedding_result(vector, backend, cache_hit, started, runtime)


def _resource_snapshot() -> dict[str, Any]:
    import torch  # type: ignore

    if not torch.cuda.is_available():
        return {"schema_version": 1, "gpu_available": False, "available_vram_bytes": None}
    free, total = torch.cuda.mem_get_info()
    return {"schema_version": 1, "gpu_available": True, "available_vram_bytes": int(free), "total_vram_bytes": int(total), "device_name": torch.cuda.get_device_name(0)}


def _run_embedding(request, pb2):
    if request.job_type == "resource-snapshot-v1":
        return _resource_snapshot()
    try:
        config = json.loads(request.config_json)
    except json.JSONDecodeError as exc:
        raise ValueError("embedding config is invalid JSON") from exc
    spec = _resolve_model_spec(config)
    if request.job_type == "model-info-v1":
        return {"schema_version": 1, "model_id": spec.model_id, "model_revision": spec.revision, "processor_version": spec.processor_version, "processor_mode": spec.processor_mode}
    if request.expected_processor_version != spec.processor_version:
        raise ValueError("processor version does not match the approved embedding model")
    descriptors = dict(request.input_descriptors)
    required = ("model_root", "models_root")
    if any(not descriptors.get(name) for name in required):
        raise ValueError("embedding request lacks approved local model descriptors")
    if request.job_type == "visual-embedding-v1":
        return _image_embedding(descriptors, config)
    if request.job_type == "text-embedding-v1":
        return _text_embedding(descriptors, config)
    raise ValueError("unsupported worker job type")


def build_servicer(grpc, pb2, pb2_grpc, expected_token: str, protocol_version: int, shutdown_event: threading.Event):
    class LocalMediaWorkerServicer(pb2_grpc.LocalMediaWorkerServicer):
        def _authorize(self, context) -> None:
            if not require_token(expected_token, metadata_token(context.invocation_metadata())):
                context.abort(grpc.StatusCode.UNAUTHENTICATED, "worker authentication failed")

        def _info(self):
            info = worker_info(protocol_version, serving=True)
            return pb2.WorkerInfo(
                protocol_version=info.protocol_version,
                worker_version=info.worker_version,
                python_version=info.python_version,
                backend_available=info.backend_available,
                backend_detail=info.backend_detail,
                process_id=info.process_id,
                serving=info.serving,
            )

        def Health(self, request, context):
            self._authorize(context)
            return pb2.HealthResponse(worker=self._info())

        def Version(self, request, context):
            self._authorize(context)
            return pb2.VersionResponse(worker=self._info(), capabilities=CAPABILITIES)

        def Shutdown(self, request, context):
            self._authorize(context)
            shutdown_event.set()
            return pb2.ShutdownResponse(accepted=True)

        def RunJob(self, request, context):
            self._authorize(context)
            started = time.perf_counter()
            yield pb2.JobProgressOrResult(progress=pb2.JobProgress(job_id=request.job_id, phase="Validating", current=0, total=1, unit="job", heartbeat_utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())))
            try:
                result = _run_embedding(request, pb2)
                result.setdefault("metrics", {})["job_elapsed_ms"] = (time.perf_counter() - started) * 1000.0
                yield pb2.JobProgressOrResult(result=pb2.JobResult(job_id=request.job_id, outcome="completed", structured_result_json=json.dumps(result, ensure_ascii=False, separators=(",", ":"))))
            except Exception as exc:
                yield pb2.JobProgressOrResult(result=pb2.JobResult(job_id=request.job_id, outcome="failed", error_code="EMBEDDING_FAILED", error_message=str(exc)))

        def CancelJob(self, request, context):
            self._authorize(context)
            return pb2.CancelJobResponse(accepted=False)

    return LocalMediaWorkerServicer()


def serve() -> int:
    expected_token = os.environ.get("LOCAL_MEDIA_AI_WORKER_TOKEN", "")
    if not expected_token:
        print(json.dumps({"kind": "fatal", "code": "WORKER_TOKEN_MISSING"}), file=sys.stderr, flush=True)
        return 2
    try:
        grpc, pb2, pb2_grpc = _load_grpc_modules()
    except RuntimeError:
        print(json.dumps({"kind": "fatal", "code": "WORKER_RUNTIME_UNAVAILABLE"}), file=sys.stderr, flush=True)
        return 3
    protocol_version = configured_protocol_version()
    shutdown_event = threading.Event()
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=1))
    pb2_grpc.add_LocalMediaWorkerServicer_to_server(build_servicer(grpc, pb2, pb2_grpc, expected_token, protocol_version, shutdown_event), server)
    port = server.add_insecure_port("127.0.0.1:0")
    if port <= 0:
        print(json.dumps({"kind": "fatal", "code": "WORKER_BIND_FAILED"}), file=sys.stderr, flush=True)
        return 4
    server.start()
    print(json.dumps({"kind": READY_KIND, "port": port, "protocol_version": protocol_version}, separators=(",", ":")), flush=True)
    try:
        shutdown_event.wait()
    except KeyboardInterrupt:
        pass
    finally:
        server.stop(grace=2).wait()
    return 0


def diagnostic_health() -> int:
    expected = os.environ.get("LOCAL_MEDIA_AI_WORKER_TOKEN", "")
    if not expected:
        raise PermissionError("worker session token is invalid")
    print(json.dumps(asdict(worker_info(configured_protocol_version(), serving=False)), ensure_ascii=False))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--serve", action="store_true")
    parser.add_argument("--diagnostic-health", action="store_true")
    args = parser.parse_args()
    if args.serve:
        return serve()
    if args.diagnostic_health:
        return diagnostic_health()
    parser.error("select --serve or --diagnostic-health")
    return 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except PermissionError as exc:
        print(json.dumps({"kind": "fatal", "code": "WORKER_AUTH_FAILED", "message": str(exc)}), file=sys.stderr, flush=True)
        raise SystemExit(5)
