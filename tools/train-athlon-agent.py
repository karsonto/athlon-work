"""
Athlon Agent 训练数据 → Qwen3-0.6B LoRA 微调脚本

支持两种训练模式：
- SFT: 修正轨迹 + 溢出恢复 → 监督微调（含 CoT 推理链蒸馏）
- DPO: 工具选型偏好对 → 偏好优化

用法：
  # SFT 训练
  python qwen3-0.6B-finetrunning.py --mode sft --epochs 5

  # DPO 训练
  python qwen3-0.6B-finetrunning.py --mode dpo --epochs 3

  # 先 SFT 再 DPO
  python qwen3-0.6B-finetrunning.py --mode sft --epochs 3
  python qwen3-0.6B-finetrunning.py --mode dpo --load ./model/Qwen3-0.6B_sft_lora

依赖：
  pip install torch transformers datasets peft trl accelerate
"""

import argparse
import json
import glob
import os
# 避免 Windows 上 libiomp5md.dll 多副本冲突
os.environ["KMP_DUPLICATE_LIB_OK"] = "TRUE"
import math

import torch
from datasets import Dataset, concatenate_datasets
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    BitsAndBytesConfig,
    TrainingArguments,
    Trainer,
    DataCollatorForSeq2Seq,
)
# DPOTrainer/DPOConfig 只在 --mode dpo/all 时按需导入（避免 llm_blender 依赖冲突）
# from trl import DPOTrainer, DPOConfig

# ==================== 配置 ====================
_SRC_DIR = os.path.dirname(os.path.abspath(__file__))
MODEL_ID = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B")
OUTPUT_DIR_SFT = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B_sft_lora")
OUTPUT_DIR_DPO = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B_dpo_lora")

SFT_TRAINING_DATA = os.path.expanduser("~/.athlon-agent/training-data/sft-traces-*.jsonl")
DPO_TRAINING_DATA = os.path.expanduser("~/.athlon-agent/training-data/dpo-preference-*.jsonl")

MAX_LENGTH = 4096          # 最大序列长度（Qwen3-0.6B 支持 32k，这里设为 4k 省显存）
LORA_R = 16
LORA_ALPHA = 32
LORA_DROPOUT = 0.05
USE_4BIT = False           # 0.6B 不需要量化，设为 True 可省显存但损失精度


# ==================== SFT: 消息转 Qwen3 对话格式 ====================

def messages_to_text(messages, add_reasoning=True):
    """
    将 HuggingFace messages 格式转换为 Qwen3 chat template 文本。
    
    Qwen3 格式：
      <|im_start|>system\n...<|im_end|>
      <|im_start|>user\n...<|im_end|>
      <|im_start|>assistant\n...<|im_end|>
    """
    parts = []
    for msg in messages:
        role = msg["role"]
        content = msg.get("content") or ""
        reasoning = msg.get("reasoning") if add_reasoning else None
        tool_calls = msg.get("tool_calls")
        tool_call_id = msg.get("tool_call_id")

        if role == "system":
            parts.append(f"<|im_start|>system\n{content}<|im_end|>")

        elif role == "user":
            parts.append(f"<|im_start|>user\n{content}<|im_end|>")

        elif role == "assistant":
            assistant_content = ""
            # 1. 推理链 → <think> 块
            if reasoning and add_reasoning:
                assistant_content += f"<think>\n{reasoning}\n</think>\n\n"
            # 2. 普通文本内容
            if content:
                assistant_content += content
            # 3. Tool calls → JSON 格式
            if tool_calls:
                tc_json = json.dumps(
                    [
                        {
                            "id": tc["id"],
                            "type": "function",
                            "function": {
                                "name": tc["function"]["name"],
                                "arguments": tc["function"]["arguments"],
                            },
                        }
                        for tc in tool_calls
                    ],
                    ensure_ascii=False,
                )
                if assistant_content:
                    assistant_content += "\n"
                assistant_content += f"<tool_call>\n{tc_json}\n</tool_call>"
            parts.append(f"<|im_start|>assistant\n{assistant_content}<|im_end|>")

        elif role == "tool":
            # Tool result → tool 角色消息
            parts.append(f"<|im_start|>tool\n{content}<|im_end|>")

    return "\n".join(parts)


def tokenize_sft_sample(example, tokenizer):
    """将 SFT 样本编码为 input_ids + labels（prompt 部分 mask 掉）。"""
    messages = example["messages"]

    # 找到最后一个 assistant 消息之前作为 prompt，之后作为 response
    last_assistant_idx = None
    for i in range(len(messages) - 1, -1, -1):
        if messages[i]["role"] == "assistant":
            last_assistant_idx = i
            break

    # 如果找不到 assistant（不应该），把整个对话当作 response
    if last_assistant_idx is None:
        full_text = messages_to_text(messages)
        encoded = tokenizer(
            full_text,
            truncation=True,
            max_length=MAX_LENGTH,
            add_special_tokens=False,
        )
        input_ids = encoded["input_ids"] + [tokenizer.eos_token_id]
        attention_mask = encoded["attention_mask"] + [1]
        labels = input_ids.copy()
        return {"input_ids": input_ids, "attention_mask": attention_mask, "labels": labels}

    # Prompt: 从 system/user 到最后一个 assistant 之前的消息
    prompt_messages = messages[:last_assistant_idx]
    # Response: 最后一个 assistant 消息及其后续的 tool 消息
    response_messages = messages[last_assistant_idx:]

    prompt_text = messages_to_text(prompt_messages, add_reasoning=False)
    response_text = messages_to_text(response_messages, add_reasoning=True)

    # Tokenize
    prompt_ids = tokenizer(prompt_text, add_special_tokens=False)["input_ids"]
    response_ids = tokenizer(response_text, add_special_tokens=False)["input_ids"]

    # 拼接 prompt + response + eos
    input_ids = prompt_ids + response_ids + [tokenizer.eos_token_id]
    attention_mask = [1] * len(input_ids)

    # Labels: prompt 部分 -100（不计算 loss），response 部分正常
    labels = (
        [-100] * len(prompt_ids)
        + response_ids
        + [tokenizer.eos_token_id]
    )

    # Truncate
    if len(input_ids) > MAX_LENGTH:
        input_ids = input_ids[:MAX_LENGTH]
        attention_mask = attention_mask[:MAX_LENGTH]
        labels = labels[:MAX_LENGTH]

    return {
        "input_ids": input_ids,
        "attention_mask": attention_mask,
        "labels": labels,
    }


# ==================== DPO: 数据预处理 ====================

def format_dpo_messages(msgs):
    """将 DPO 的 prompt/chosen/rejected 消息列表格式化为纯文本。"""
    return messages_to_text(msgs, add_reasoning=True)


def tokenize_dpo_sample(example, tokenizer):
    """将 DPO 样本编码（prompt/chosen/rejected 各为文本）。"""
    return {
        "prompt": format_dpo_messages(example["prompt"]),
        "chosen": format_dpo_messages(example["chosen"]),
        "rejected": format_dpo_messages(example["rejected"]),
    }


# ==================== 数据加载 ====================

def load_sft_data():
    """加载 SFT 训练数据（修正轨迹 + 溢出恢复）。"""
    all_records = []
    for fpath in sorted(glob.glob(SFT_TRAINING_DATA)):
        print(f"  加载 SFT: {os.path.basename(fpath)}")
        with open(fpath, encoding="utf-8-sig") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                sample = json.loads(line)
                source = sample.get("metadata", {}).get("source", "")
                # 只加载 agent 交互数据
                if source in ("agent-correction", "overflow-recovery"):
                    all_records.append(sample)

    print(f"  SFT 样本总数: {len(all_records)}")
    if all_records:
        sources = {}
        for r in all_records:
            s = r.get("metadata", {}).get("source", "?")
            sources[s] = sources.get(s, 0) + 1
        for s, c in sources.items():
            print(f"    - {s}: {c}")
    return Dataset.from_list(all_records) if all_records else None


def load_dpo_data():
    """加载 DPO 训练数据（工具选型偏好对）。"""
    all_records = []
    for fpath in sorted(glob.glob(DPO_TRAINING_DATA)):
        print(f"  加载 DPO: {os.path.basename(fpath)}")
        with open(fpath, encoding="utf-8-sig") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                sample = json.loads(line)
                all_records.append(sample)
    print(f"  DPO 样本总数: {len(all_records)}")
    return Dataset.from_list(all_records) if all_records else None


# ==================== 模型加载 ====================

def load_model_and_tokenizer(model_path=None):
    """加载 Qwen3-0.6B 和 tokenizer。"""
    model_id = model_path or MODEL_ID
    # 本地路径必须用绝对路径，否则新版 transformers 会当成 HuggingFace repo id
    local_path = os.path.abspath(model_id)
    print(f"  加载模型: {local_path}")

    tokenizer = AutoTokenizer.from_pretrained(
        local_path,
        trust_remote_code=True,
        use_fast=True,
        local_files_only=True,
    )
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    # 确保 chat template 使用 Qwen3 格式
    if tokenizer.chat_template is None:
        tokenizer.chat_template = "{% for message in messages %}{{'<|im_start|>' + message['role'] + '\n' + message['content'] + '<|im_end|>' + '\n'}}{% endfor %}{% if add_generation_prompt %}{{ '<|im_start|>assistant\n' }}{% endif %}"

    # 加载模型
    if USE_4BIT:
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_use_double_quant=True,
        )
    else:
        bnb_config = None

    model = AutoModelForCausalLM.from_pretrained(
        local_path,
        quantization_config=bnb_config,
        device_map="auto",
        torch_dtype=torch.bfloat16,
        trust_remote_code=True,
        local_files_only=True,
    )

    if USE_4BIT:
        model = prepare_model_for_kbit_training(
            model,
            use_gradient_checkpointing=True,
            gradient_checkpointing_kwargs={"use_reentrant": False},
        )
    else:
        model.enable_input_require_grads()

    return model, tokenizer


def apply_lora(model):
    """应用 LoRA。"""
    config = LoraConfig(
        r=LORA_R,
        lora_alpha=LORA_ALPHA,
        lora_dropout=LORA_DROPOUT,
        bias="none",
        task_type="CAUSAL_LM",
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj",
        ],
    )
    model = get_peft_model(model, config)
    model.print_trainable_parameters()
    return model


# ==================== 训练 ====================

def train_sft(load_path=None):
    """SFT 训练：修正轨迹 + 溢出恢复。"""
    print("\n" + "=" * 60)
    print("SFT 训练")
    print("=" * 60)

    # 加载数据
    dataset = load_sft_data()
    if dataset is None or len(dataset) == 0:
        print("  ❌ 无 SFT 数据，跳过")
        return

    # 加载模型
    model, tokenizer = load_model_and_tokenizer(load_path)
    model = apply_lora(model)

    # Tokenize
    print("  Tokenizing...")
    tokenized = dataset.map(
        lambda x: tokenize_sft_sample(x, tokenizer),
        remove_columns=dataset.column_names,
        desc="Tokenizing",
    )

    # 统计 token 分布
    token_lens = [len(x["input_ids"]) for x in tokenized]
    print(f"  序列长度: avg={sum(token_lens)/len(token_lens):.0f}, "
          f"max={max(token_lens)}, min={min(token_lens)}")

    # 训练参数
    training_args = TrainingArguments(
        output_dir=OUTPUT_DIR_SFT,
        per_device_train_batch_size=2,
        gradient_accumulation_steps=8,
        num_train_epochs=5,
        learning_rate=2e-4,
        warmup_ratio=0.05,
        lr_scheduler_type="cosine",
        bf16=True,
        logging_steps=10,
        save_steps=50,
        save_total_limit=2,
        save_on_each_node=True,
        gradient_checkpointing=True,
        optim="adamw_torch",
        report_to="none",
        remove_unused_columns=False,
        dataloader_num_workers=2,
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=tokenized,
        data_collator=DataCollatorForSeq2Seq(
            tokenizer=tokenizer,
            padding=True,
        ),
    )

    print("  开始训练...")
    trainer.train()

    trainer.save_model(OUTPUT_DIR_SFT)
    tokenizer.save_pretrained(OUTPUT_DIR_SFT)
    print(f"  ✅ SFT 模型已保存: {OUTPUT_DIR_SFT}")


def train_dpo(load_path=None):
    """DPO 训练：工具选型偏好优化。"""
    # 延迟导入，避免 llm_blender 兼容性问题
    from trl import DPOTrainer, DPOConfig

    print("\n" + "=" * 60)
    print("DPO 训练")
    print("=" * 60)

    # 加载数据
    dataset = load_dpo_data()
    if dataset is None or len(dataset) == 0:
        print("  ❌ 无 DPO 数据，跳过")
        return

    # 加载模型（DPO 需要 base model + ref model）
    model, tokenizer = load_model_and_tokenizer(load_path)
    model = apply_lora(model)

    # Tokenize DPO 样本（转为文本）
    print("  Tokenizing...")
    tokenized = dataset.map(
        lambda x: tokenize_dpo_sample(x, tokenizer),
        remove_columns=dataset.column_names,
        desc="Formatting DPO",
    )

    # 统计长度
    for field in ("prompt", "chosen", "rejected"):
        lens = [len(tokenizer.encode(x)) for x in tokenized[field]]
        print(f"  {field}: avg={sum(lens)/len(lens):.0f}, max={max(lens)}")

    # DPO 配置
    dpo_args = DPOConfig(
        output_dir=OUTPUT_DIR_DPO,
        per_device_train_batch_size=1,
        gradient_accumulation_steps=4,
        num_train_epochs=3,
        learning_rate=1e-5,
        warmup_ratio=0.1,
        lr_scheduler_type="cosine",
        bf16=True,
        logging_steps=10,
        save_steps=50,
        save_total_limit=2,
        gradient_checkpointing=True,
        optim="adamw_torch",
        report_to="none",
        remove_unused_columns=False,
        dataloader_num_workers=2,
        max_prompt_length=2048,
        max_length=MAX_LENGTH,
    )

    trainer = DPOTrainer(
        model=model,
        args=dpo_args,
        train_dataset=tokenized,
        tokenizer=tokenizer,
        # DPO 用 beta 控制 KL 惩罚强度
        beta=0.1,
    )

    print("  开始 DPO 训练...")
    trainer.train()

    trainer.save_model(OUTPUT_DIR_DPO)
    tokenizer.save_pretrained(OUTPUT_DIR_DPO)
    print(f"  ✅ DPO 模型已保存: {OUTPUT_DIR_DPO}")


# ==================== 推理测试 ====================

def test_inference(model_path, prompt):
    """用训练好的模型做推理测试。"""
    print(f"\n{'=' * 60}")
    print(f"推理测试")
    print(f"{'=' * 60}")

    tokenizer = AutoTokenizer.from_pretrained(
        model_path, trust_remote_code=True, use_fast=True
    )
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    from peft import PeftModel
    base_model = AutoModelForCausalLM.from_pretrained(
        MODEL_ID,
        device_map="auto",
        torch_dtype=torch.bfloat16,
        trust_remote_code=True,
    )
    model = PeftModel.from_pretrained(base_model, model_path)
    model.eval()

    messages = [
        {"role": "system", "content": "你是 Athlon Agent，一个 Windows 桌面编程助手。"},
        {"role": "user", "content": prompt},
    ]
    text = messages_to_text(messages) + "\n<|im_start|>assistant\n"

    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=512,
            temperature=0.7,
            top_p=0.9,
            do_sample=True,
        )
    response = tokenizer.decode(outputs[0][inputs["input_ids"].shape[1]:], skip_special_tokens=True)
    print(f"  prompt: {prompt}")
    print(f"  response: {response[:300]}")


# ==================== 主入口 ====================

def main():
    parser = argparse.ArgumentParser(description="Athlon Agent 训练数据 → Qwen3-0.6B 微调")
    parser.add_argument("--mode", choices=["sft", "dpo", "all", "test"], default="sft",
                        help="训练模式: sft=修正轨迹, dpo=工具偏好, all=先sft再dpo, test=测试")
    parser.add_argument("--epochs", type=int, default=None,
                        help="训练轮数（覆盖默认值）")
    parser.add_argument("--load", type=str, default=None,
                        help="加载已有 LoRA 权重路径")
    parser.add_argument("--prompt", type=str, default="帮我看看当前目录有什么文件",
                        help="测试推理用的 prompt")
    args = parser.parse_args()

    # 统计数据
    print("\n📊 数据概览")
    print("-" * 40)
    sft_files = sorted(glob.glob(SFT_TRAINING_DATA))
    dpo_files = sorted(glob.glob(DPO_TRAINING_DATA))

    for f in sft_files + dpo_files:
        count = 0
        with open(f, encoding="utf-8-sig") as fh:
            for line in fh:
                if line.strip():
                    count += 1
        print(f"  {os.path.basename(f)}: {count} 条")

    if args.mode == "sft":
        train_sft(args.load)
    elif args.mode == "dpo":
        train_dpo(args.load)
    elif args.mode == "all":
        train_sft(args.load)
        train_dpo(OUTPUT_DIR_SFT)
    elif args.mode == "test":
        load_path = args.load or OUTPUT_DIR_SFT
        if os.path.isdir(load_path):
            test_inference(load_path, args.prompt)
        else:
            print(f"  ❌ 模型路径不存在: {load_path}")
            print(f"  请先运行: python qwen3-0.6B-finetrunning.py --mode sft")

    print("\n✅ 完成！")


if __name__ == "__main__":
    main()
