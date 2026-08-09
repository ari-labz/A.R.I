"""Fine-tune Orpheus-3B on a custom voice using LoRA.

Uses Unsloth when CUDA is available, falls back to standard PEFT on CPU/MPS.

Usage:
    python train.py --dataset dataset.jsonl --output ./schwi-lora --epochs 3

Prerequisites:
    pip install trl datasets peft transformers torch
    (optional, CUDA only) pip install unsloth
"""

import argparse, json, sys
from pathlib import Path

import torch
from transformers import TrainingArguments, AutoModelForCausalLM, AutoTokenizer, Trainer

BASE_MODEL  = "unsloth/orpheus-3b-0.1-ft"
MAX_SEQ_LEN = 2048
LORA_R      = 64
LORA_ALPHA  = 64

# Orpheus wraps the transcript in these before the audio tokens.  Inference has
# to build the identical prefix (see External/Orpheus/serve.py).
END_OF_TEXT    = 128009
START_OF_HUMAN = 128259
END_OF_HUMAN   = 128260

HAS_CUDA = torch.cuda.is_available()

try:
    if not HAS_CUDA:
        raise ImportError("Skipping unsloth on non-CUDA device")
    from unsloth import FastLanguageModel
    USE_UNSLOTH = True
except ImportError:
    USE_UNSLOTH = False
    from peft import get_peft_model, LoraConfig, TaskType


def load_dataset(path: str) -> list[dict]:
    examples = []
    with open(path) as f:
        for line in f:
            ex = json.loads(line)
            examples.append({"text": ex["prompt"], "audio_tokens": ex["audio_tokens"]})
    return examples


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", required=True, help="JSONL from prepare_dataset.py")
    parser.add_argument("--output", required=True, help="Output directory for LoRA weights")
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--grad-accum", type=int, default=4)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--max-seq-len", type=int, default=MAX_SEQ_LEN)
    args = parser.parse_args()

    print(f"[train] Backend: {'unsloth (CUDA)' if USE_UNSLOTH else 'PEFT (CPU)'}")
    print(f"[train] Loading base model: {BASE_MODEL}")

    if USE_UNSLOTH:
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_name=BASE_MODEL,
            max_seq_length=args.max_seq_len,
            dtype=None,
            load_in_4bit=False,
        )
        model = FastLanguageModel.get_peft_model(
            model,
            r=LORA_R,
            lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05,
            bias="none",
            use_gradient_checkpointing="unsloth",
        )
    else:
        tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL)
        model = AutoModelForCausalLM.from_pretrained(
            BASE_MODEL,
            dtype=torch.float32,
            device_map="cpu",
        )
        lora_config = LoraConfig(
            r=LORA_R,
            lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05,
            bias="none",
            task_type=TaskType.CAUSAL_LM,
        )
        model = get_peft_model(model, lora_config)
        model.print_trainable_parameters()

    print(f"[train] Loading dataset from {args.dataset}")
    raw_examples = load_dataset(args.dataset)
    print(f"[train] {len(raw_examples)} training examples")

    tokenized_examples = []
    for ex in raw_examples:
        # <start-of-human> <bos> transcript <end-of-text> <end-of-human>, then the
        # spoken turn from prepare_dataset.py. add_special_tokens keeps the BOS the
        # base model expects to see at the front of the transcript.
        prompt_tokens = ([START_OF_HUMAN]
                         + tokenizer.encode(ex["text"], add_special_tokens=True)
                         + [END_OF_TEXT, END_OF_HUMAN])
        full_ids = prompt_tokens + ex["audio_tokens"]
        if len(full_ids) > args.max_seq_len:
            # Dropping the tail would teach the model to stop mid-word, and a clip
            # whose prompt alone overflows has nothing left to learn from.
            print(f"[train] Skipping over-long example ({len(full_ids)} tokens): {ex['text'][:60]}")
            continue
        tokenized_examples.append({
            "input_ids": full_ids,
            "attention_mask": [1] * len(full_ids),
            # Only the spoken turn is learned; the transcript is context.
            "labels": [-100] * len(prompt_tokens) + full_ids[len(prompt_tokens):],
        })
    print(f"[train] Tokenized {len(tokenized_examples)} examples")

    training_args = TrainingArguments(
        output_dir=args.output,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        warmup_steps=10,
        logging_steps=5,
        save_strategy="epoch",
        fp16=HAS_CUDA,
        bf16=False,
        optim="adamw_torch",
        seed=42,
        report_to="none",
        use_cpu=not HAS_CUDA,
        remove_unused_columns=False,
    )

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    from dataclasses import dataclass
    @dataclass
    class PadCollator:
        tokenizer: object
        def __call__(self, features):
            max_len = max(len(f["input_ids"]) for f in features)
            batch = {"input_ids": [], "attention_mask": [], "labels": []}
            for f in features:
                pad_len = max_len - len(f["input_ids"])
                batch["input_ids"].append(f["input_ids"] + [self.tokenizer.pad_token_id] * pad_len)
                batch["attention_mask"].append(f["attention_mask"] + [0] * pad_len)
                batch["labels"].append(f["labels"] + [-100] * pad_len)
            import torch as _t
            return {k: _t.tensor(v) for k, v in batch.items()}

    collator = PadCollator(tokenizer=tokenizer)

    class TokenizedDataset(torch.utils.data.Dataset):
        def __init__(self, examples):
            self.examples = examples
        def __len__(self):
            return len(self.examples)
        def __getitem__(self, idx):
            return self.examples[idx]

    trainer = Trainer(
        model=model,
        processing_class=tokenizer,
        train_dataset=TokenizedDataset(tokenized_examples),
        args=training_args,
        data_collator=collator,
    )

    print(f"[train] Starting training: {args.epochs} epochs, batch={args.batch_size}, grad_accum={args.grad_accum}")
    trainer.train()

    print(f"[train] Saving LoRA weights to {args.output}")
    model.save_pretrained(args.output)
    tokenizer.save_pretrained(args.output)
    print("[train] Done.")


if __name__ == "__main__":
    main()
