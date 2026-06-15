#!/usr/bin/env python3
"""
One-shot script: extract EMA weights from a full F5-TTS training checkpoint,
convert to FP16, and save a lean inference checkpoint (~645 MB vs 5.14 GB).

Usage:
    python quantize_to_fp16.py <path_to_model_last.pt> [output_path]

If output_path is omitted, saves as model_infer_fp16.pt in the same directory.
"""
import sys
import os
import torch

def main():
    if len(sys.argv) < 2:
        print("Usage: quantize_to_fp16.py <model_last.pt> [output_path]")
        sys.exit(1)

    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(src), "model_infer_fp16.pt")

    print(f"Loading {src} ...")
    ckpt = torch.load(src, map_location="cpu", weights_only=False)

    if "ema_model_state_dict" not in ckpt:
        print("ERROR: 'ema_model_state_dict' key not found in checkpoint.")
        print("Keys present:", list(ckpt.keys()))
        sys.exit(1)

    print("Extracting and converting EMA weights to FP16 ...")
    ema_fp16 = {
        k: v.half() if v.is_floating_point() else v
        for k, v in ckpt["ema_model_state_dict"].items()
    }

    # Save in the same format F5-TTS expects: a dict with ema_model_state_dict key
    out = {"ema_model_state_dict": ema_fp16}
    torch.save(out, dst)

    size_mb = os.path.getsize(dst) / 1024 / 1024
    print(f"Saved to {dst}  ({size_mb:.0f} MB)")


if __name__ == "__main__":
    main()
