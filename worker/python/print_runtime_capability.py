"""Print the Portable AI runtime capability as JSON during explicit provisioning."""
from __future__ import annotations

import json

import numpy
import PIL
import torch
import transformers


print(
    json.dumps(
        {
            "torch": torch.__version__,
            "transformers": transformers.__version__,
            "numpy": numpy.__version__,
            "pillow": PIL.__version__,
            "cuda_available": torch.cuda.is_available(),
            "cuda_version": torch.version.cuda,
            "cuda_device_count": torch.cuda.device_count() if torch.cuda.is_available() else 0,
        },
        ensure_ascii=False,
    )
)
