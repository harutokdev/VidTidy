"""Read-only dependency probe for the local SigLIP 2 tokenizer runtime."""
from __future__ import annotations

import importlib.util
import json

result: dict[str, object] = {
    "sentencepiece_module_present": importlib.util.find_spec("sentencepiece") is not None,
}
try:
    import sentencepiece  # type: ignore[import-not-found]

    result["sentencepiece_version"] = getattr(sentencepiece, "__version__", None)
except Exception as exc:  # intentionally records a missing/incompatible local dependency
    result["sentencepiece_import_error"] = f"{type(exc).__name__}: {exc}"
try:
    from transformers import SiglipTokenizer

    result["siglip_tokenizer_importable"] = True
    result["siglip_tokenizer_class"] = SiglipTokenizer.__name__
except Exception as exc:
    result["siglip_tokenizer_importable"] = False
    result["siglip_tokenizer_import_error"] = f"{type(exc).__name__}: {exc}"
print(json.dumps(result, ensure_ascii=False))
