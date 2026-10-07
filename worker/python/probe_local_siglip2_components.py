"""Offline component-level probe for the prepared SigLIP 2 artifact."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path


def result_of(label, factory):
    try:
        value = factory()
        return {"label": label, "ok": True, "class": type(value).__name__}
    except Exception as exc:
        return {"label": label, "ok": False, "error": f"{type(exc).__name__}: {exc}"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", required=True)
    args = parser.parse_args()
    if os.environ.get("HF_HUB_OFFLINE") != "1" or os.environ.get("TRANSFORMERS_OFFLINE") != "1":
        raise RuntimeError("offline environment variables must be set")
    root = Path(args.model_root).resolve()
    from transformers import AutoImageProcessor, AutoProcessor, AutoTokenizer

    values = [
        result_of("auto-image-processor", lambda: AutoImageProcessor.from_pretrained(str(root), local_files_only=True)),
        result_of("auto-tokenizer-fast", lambda: AutoTokenizer.from_pretrained(str(root), local_files_only=True, use_fast=True)),
        result_of("auto-tokenizer-slow", lambda: AutoTokenizer.from_pretrained(str(root), local_files_only=True, use_fast=False)),
        result_of("auto-processor", lambda: AutoProcessor.from_pretrained(str(root), local_files_only=True)),
    ]
    print(json.dumps(values, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
