"""Read-only offline tokenizer construction probe for the prepared SigLIP 2 artifact."""
from __future__ import annotations

import argparse
import inspect
import json
import os
from pathlib import Path


def attempt(label, factory):
    try:
        value = factory()
        return {"label": label, "ok": True, "class": type(value).__name__, "vocab_size": getattr(value, "vocab_size", None)}
    except Exception as exc:
        return {"label": label, "ok": False, "error": f"{type(exc).__name__}: {exc}"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", required=True)
    args = parser.parse_args()
    if os.environ.get("HF_HUB_OFFLINE") != "1" or os.environ.get("TRANSFORMERS_OFFLINE") != "1":
        raise RuntimeError("offline environment variables must be set")
    root = Path(args.model_root).resolve()
    from transformers import AutoTokenizer, GemmaTokenizer, GemmaTokenizerFast, PreTrainedTokenizerFast

    values = {
        "signatures": {
            "GemmaTokenizer": str(inspect.signature(GemmaTokenizer)),
            "GemmaTokenizerFast": str(inspect.signature(GemmaTokenizerFast)),
            "PreTrainedTokenizerFast": str(inspect.signature(PreTrainedTokenizerFast)),
        },
        "attempts": [
            attempt("auto-fast", lambda: AutoTokenizer.from_pretrained(str(root), local_files_only=True, use_fast=True)),
            attempt("auto-slow", lambda: AutoTokenizer.from_pretrained(str(root), local_files_only=True, use_fast=False)),
            attempt("gemma-fast-from-pretrained", lambda: GemmaTokenizerFast.from_pretrained(str(root), local_files_only=True)),
            attempt("gemma-slow-from-pretrained", lambda: GemmaTokenizer.from_pretrained(str(root), local_files_only=True)),
            attempt("pretrained-fast-tokenizer-json", lambda: PreTrainedTokenizerFast(tokenizer_file=str(root / "tokenizer.json"))),
        ],
    }
    print(json.dumps(values, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
