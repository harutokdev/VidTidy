"""Explicit development-time SigLIP 2 artifact preparation.

This utility is never invoked by the Local Media AI application or Worker runtime.
It pins a Hugging Face revision, writes files under an explicitly selected ModelsRoot,
and creates a SHA-256 provenance manifest. Runtime must use that local artifact only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_json_hash(value: Any) -> str:
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def read_json(path: Path) -> dict[str, Any] | None:
    if not path.is_file():
        return None
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def main() -> int:
    parser = argparse.ArgumentParser(description="Prepare a pinned local SigLIP 2 model artifact.")
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--models-root", required=True)
    parser.add_argument("--artifact-key", default="siglip2-base-patch16-224")
    parser.add_argument("--provenance-name", default="provenance.json")
    args = parser.parse_args()

    try:
        from huggingface_hub import HfApi, snapshot_download
    except ImportError as exc:
        raise RuntimeError("huggingface_hub is required only for explicit model preparation") from exc

    models_root = Path(args.models_root).resolve()
    models_root.mkdir(parents=True, exist_ok=True)
    artifact_root = (models_root / args.artifact_key).resolve()
    if artifact_root.parent != models_root:
        raise ValueError("artifact key must resolve directly beneath the selected ModelsRoot")

    info = HfApi().model_info(args.model_id, revision=args.revision, files_metadata=True)
    if info.sha != args.revision:
        raise RuntimeError(f"resolved revision mismatch: expected {args.revision}, got {info.sha}")

    # Snapshot download is intentionally isolated from application runtime. A partially
    # downloaded artifact is never published over an existing completed artifact.
    staging = Path(tempfile.mkdtemp(prefix=f".{args.artifact_key}.prepare-", dir=models_root))
    try:
        snapshot_download(
            repo_id=args.model_id,
            revision=args.revision,
            local_dir=staging,
            local_dir_use_symlinks=False,
            allow_patterns=[
                "config.json",
                "model.safetensors",
                "preprocessor_config.json",
                "special_tokens_map.json",
                "tokenizer.json",
                "tokenizer.model",
                "tokenizer_config.json",
                "README.md",
                "LICENSE*",
            ],
        )
        required = ["config.json", "model.safetensors", "preprocessor_config.json", "tokenizer.json", "tokenizer.model", "tokenizer_config.json"]
        missing = [name for name in required if not (staging / name).is_file()]
        if missing:
            raise RuntimeError(f"model snapshot is incomplete: {', '.join(missing)}")

        files: list[dict[str, Any]] = []
        for path in sorted(p for p in staging.rglob("*") if p.is_file() and ".cache" not in p.parts):
            files.append({
                "relative_path": path.relative_to(staging).as_posix(),
                "sha256": sha256_file(path),
                "byte_size": path.stat().st_size,
            })

        card_data = getattr(info, "cardData", None) or {}
        config = read_json(staging / "config.json")
        processor = read_json(staging / "preprocessor_config.json")
        provenance = {
            "schema_version": 1,
            "prepared_at_utc": datetime.now(timezone.utc).isoformat(),
            "preparation_only": True,
            "runtime_network_allowed": False,
            "model_id": args.model_id,
            "revision": args.revision,
            "resolved_revision": info.sha,
            "source": f"https://huggingface.co/{args.model_id}",
            "license": card_data.get("license"),
            "library_name": getattr(info, "library_name", None),
            "pipeline_tag": getattr(info, "pipeline_tag", None),
            "model_config": config,
            "model_config_sha256": canonical_json_hash(config) if config is not None else None,
            "processor_config": processor,
            "processor_config_sha256": canonical_json_hash(processor) if processor is not None else None,
            "files": files,
        }
        temp_manifest = staging / f".{args.provenance_name}.tmp"
        temp_manifest.write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temp_manifest, staging / args.provenance_name)

        if artifact_root.exists():
            existing = artifact_root / args.provenance_name
            if existing.is_file() and json.loads(existing.read_text(encoding="utf-8")).get("revision") == args.revision:
                print(json.dumps({"outcome": "reused", "artifact_root": str(artifact_root), "revision": args.revision}, ensure_ascii=False))
                return 0
            raise RuntimeError(f"refusing to replace an existing model artifact: {artifact_root}")
        os.replace(staging, artifact_root)
        staging = None
        print(json.dumps({"outcome": "prepared", "artifact_root": str(artifact_root), "revision": args.revision, "file_count": len(files)}, ensure_ascii=False))
        return 0
    finally:
        if staging is not None and staging.exists():
            shutil.rmtree(staging, ignore_errors=True)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"outcome": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(1)
