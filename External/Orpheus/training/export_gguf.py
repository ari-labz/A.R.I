"""Merge LoRA weights into the base model and convert to GGUF.

Usage:
    python export_gguf.py --lora ./schwi-lora --output ./schwi.gguf --quant q4_k_m

Prerequisites:
    - llama-quantize binary (auto-detected from ~/llama.cpp or PATH)
    - pip install peft transformers torch gguf numpy sentencepiece protobuf
"""

import argparse, os, shutil, subprocess, sys, tempfile
from pathlib import Path

import torch
from peft import PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer

BASE_MODEL = "unsloth/orpheus-3b-0.1-ft"
LLAMA_CPP_REPO = "https://github.com/ggml-org/llama.cpp.git"


def find_llama_quantize():
    result = shutil.which("llama-quantize")
    if result:
        return result
    for candidate in [
        Path.home() / "llama.cpp" / "llama-quantize",
        Path("/opt/llama.cpp") / "llama-quantize",
    ]:
        if candidate.exists():
            return str(candidate)
    return None


def find_llama_version():
    """Detect installed llama.cpp version tag (e.g. 'b10305') for pinning the converter."""
    for binary in ["llama-server", "llama-quantize"]:
        path = shutil.which(binary)
        if not path:
            for candidate in [Path.home() / "llama.cpp" / binary]:
                if candidate.exists():
                    path = str(candidate)
                    break
        if path:
            try:
                out = subprocess.run([path, "--version"], capture_output=True, text=True, timeout=5)
                for line in (out.stdout + out.stderr).splitlines():
                    if "version:" in line.lower():
                        # e.g. "version: 10305 ..." → "b10305"
                        parts = line.split()
                        idx = next((i for i, p in enumerate(parts) if p.lower() == "version:"), -1)
                        if idx >= 0 and idx + 1 < len(parts):
                            ver = parts[idx + 1].strip("()")
                            return f"b{ver}" if not ver.startswith("b") else ver
            except Exception:
                pass
    return None


def ensure_converter(work_dir):
    """Clone the llama.cpp conversion tools (sparse checkout, pinned to installed version)."""
    converter_dir = Path(work_dir) / "llama.cpp"
    convert_script = converter_dir / "convert_hf_to_gguf.py"

    if convert_script.exists():
        return convert_script

    tag = find_llama_version()
    print(f"[export] Cloning llama.cpp conversion tools (tag: {tag or 'latest'})...")

    clone_cmd = ["git", "clone", "--depth=1", "--filter=blob:none", "--no-checkout"]
    if tag:
        clone_cmd += ["--branch", tag]
    clone_cmd += [LLAMA_CPP_REPO, str(converter_dir)]

    subprocess.run(clone_cmd, check=True, capture_output=True)
    subprocess.run(
        ["git", "sparse-checkout", "init", "--no-cone"],
        cwd=str(converter_dir), check=True, capture_output=True,
    )
    subprocess.run(
        ["git", "sparse-checkout", "set",
         "convert_hf_to_gguf.py", "convert_lora_to_gguf.py",
         "conversion/*", "gguf-py/*"],
        cwd=str(converter_dir), check=True, capture_output=True,
    )
    subprocess.run(
        ["git", "checkout"],
        cwd=str(converter_dir), check=True, capture_output=True,
    )

    return convert_script


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--lora", required=True, help="LoRA weights directory from train.py")
    parser.add_argument("--output", required=True, help="Output GGUF file path")
    parser.add_argument("--quant", default="q4_k_m", help="Quantization type (q4_k_m, q8_0, f16)")
    args = parser.parse_args()

    quantize_bin = find_llama_quantize()
    if not quantize_bin and args.quant.lower() != "f16":
        print("Error: cannot find llama-quantize. Install llama.cpp or add it to PATH.", file=sys.stderr)
        sys.exit(1)

    with tempfile.TemporaryDirectory() as tmp:
        convert_script = ensure_converter(tmp)
        merged_dir = Path(tmp) / "merged"

        print(f"[export] Loading base model: {BASE_MODEL}")
        base_model = AutoModelForCausalLM.from_pretrained(
            BASE_MODEL, dtype=torch.float32, device_map="cpu",
        )
        tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL)

        print(f"[export] Loading LoRA from {args.lora}")
        model = PeftModel.from_pretrained(base_model, args.lora)

        print(f"[export] Merging LoRA weights...")
        model = model.merge_and_unload()

        print(f"[export] Saving merged HF model to {merged_dir}")
        model.save_pretrained(str(merged_dir))
        tokenizer.save_pretrained(str(merged_dir))

        del model, base_model
        torch.cuda.empty_cache() if torch.cuda.is_available() else None

        f16_gguf = Path(tmp) / "model-f16.gguf"
        print(f"[export] Converting to GGUF (f16)...")
        subprocess.run(
            [sys.executable, str(convert_script), str(merged_dir), "--outfile", str(f16_gguf)],
            check=True,
        )

        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)

        if args.quant.lower() == "f16":
            shutil.move(str(f16_gguf), str(output_path))
        else:
            print(f"[export] Quantizing to {args.quant}...")
            subprocess.run(
                [quantize_bin, str(f16_gguf), str(output_path), args.quant.upper()],
                check=True,
            )

    print(f"[export] Done: {output_path} ({output_path.stat().st_size / 1024 / 1024:.0f} MB)")


if __name__ == "__main__":
    main()
