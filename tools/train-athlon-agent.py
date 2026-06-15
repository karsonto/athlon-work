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
from peft import PeftModel
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
LOCAL_MODEL_ID = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B")
DEFAULT_REMOTE_MODEL_ID = "Qwen/Qwen3-0.6B"
MODEL_ID = os.environ.get(
    "ATHLON_BASE_MODEL",
    LOCAL_MODEL_ID if os.path.isdir(LOCAL_MODEL_ID) else DEFAULT_REMOTE_MODEL_ID,
)
OUTPUT_DIR_SFT = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B_sft_lora")
OUTPUT_DIR_DPO = os.path.join(_SRC_DIR, "model", "Qwen3-0.6B_dpo_lora")

SFT_TRAINING_DATA = os.path.expanduser("~/.athlon-agent/training-data/sft-traces-*.jsonl")
DPO_TRAINING_DATA = os.path.expanduser("~/.athlon-agent/training-data/dpo-preference-*.jsonl")

MAX_LENGTH = 2048          # 工具调用训练优先保留近上下文，缩短序列可显著加快收敛
SFT_BATCH_SIZE = 4
SFT_GRAD_ACCUMULATION = 8
LORA_R = 16
LORA_ALPHA = 32
LORA_DROPOUT = 0.05
USE_4BIT = False           # 0.6B 不需要量化，设为 True 可省显存但损失精度
SPLIT_TOOL_TURNS = True    # 将长轨迹拆成每个工具调用回合，强化 tool_call 学习
ADD_SYNTHETIC_TOOL_SFT = True
TOOL_REASONING_MAX_CHARS = 40
SYNTHETIC_TOOL_REPEAT = 12


# ==================== SFT: 消息转 Qwen3 对话格式 ====================

def parse_tool_arguments(arguments):
    """把训练数据里的 arguments 统一成 JSON object，避免模型学习转义字符串。"""
    if isinstance(arguments, dict):
        return arguments
    if isinstance(arguments, str):
        try:
            parsed = json.loads(arguments)
            return parsed if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
    return {}


def normalize_tool_call_for_training(tool_call):
    """转换为 Qwen/Hermes 更容易学习的简洁 tool_call 格式。"""
    function = tool_call.get("function", {}) if isinstance(tool_call, dict) else {}
    return {
        "name": function.get("name", ""),
        "arguments": parse_tool_arguments(function.get("arguments", {})),
    }


def compact_tool_reasoning(reasoning):
    """工具调用场景只保留很短的思考，避免模型生成很久还没到 tool_call。"""
    if not reasoning:
        return "选择合适的工具并传入必要参数。"
    text = " ".join(str(reasoning).split())
    if len(text) <= TOOL_REASONING_MAX_CHARS:
        return text
    return text[:TOOL_REASONING_MAX_CHARS].rstrip() + "..."


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
                if tool_calls:
                    reasoning = compact_tool_reasoning(reasoning)
                assistant_content += f"<think>\n{reasoning}\n</think>\n\n"
            elif tool_calls and add_reasoning:
                assistant_content += f"<think>\n{compact_tool_reasoning(None)}\n</think>\n\n"
            # 2. 普通文本内容
            if content:
                assistant_content += content
            # 3. Tool calls → JSON 格式
            if tool_calls:
                normalized_calls = [
                    normalize_tool_call_for_training(tc)
                    for tc in tool_calls
                ]
                tool_payload = normalized_calls[0] if len(normalized_calls) == 1 else normalized_calls
                tc_json = json.dumps(
                    tool_payload,
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

    # 拼接 prompt + response + eos。长轨迹必须优先保留 response，
    # 否则从左截断会只剩 prompt，labels 全是 -100，样本不会产生训练信号。
    response_ids = response_ids + [tokenizer.eos_token_id]
    if len(response_ids) >= MAX_LENGTH:
        response_ids = response_ids[:MAX_LENGTH]
        prompt_ids = []
    else:
        max_prompt_len = MAX_LENGTH - len(response_ids)
        prompt_ids = prompt_ids[-max_prompt_len:]

    input_ids = prompt_ids + response_ids
    attention_mask = [1] * len(input_ids)

    # Labels: prompt 部分 -100（不计算 loss），response 部分正常
    labels = [-100] * len(prompt_ids) + response_ids

    return {
        "input_ids": input_ids,
        "attention_mask": attention_mask,
        "labels": labels,
    }


def split_tool_turn_samples(sample):
    """把一条长 agent 轨迹拆成多个以当前 assistant tool_call 为目标的 SFT 样本。"""
    if not SPLIT_TOOL_TURNS:
        return [sample]

    messages = sample.get("messages", [])
    split_samples = []
    for idx, msg in enumerate(messages):
        if msg.get("role") != "assistant" or not msg.get("tool_calls"):
            continue
        metadata = dict(sample.get("metadata", {}))
        metadata["source"] = f"{metadata.get('source', 'sft')}-tool-turn"
        metadata["turn_index"] = idx
        split_samples.append({
            "messages": messages[:idx + 1],
            "metadata": metadata,
        })

    return split_samples or [sample]


def make_tool_sample(prompt, tool_name, arguments=None, reasoning=None):
    return {
        "messages": [
            {
                "role": "system",
                "content": "你是 Athlon Agent，一个 Windows 桌面编程助手。你有以下工具可用：file_list、file_read、grep_files、glob_files、execute_command、file_edit、file_write。需要操作文件或命令时，请输出 <tool_call> JSON。",
            },
            {"role": "user", "content": prompt},
            {
                "role": "assistant",
                "content": "",
                "reasoning": reasoning or "选择最合适的工具并填写必要参数。",
                "tool_calls": [
                    {
                        "id": "synthetic_call",
                        "type": "function",
                        "function": {
                            "name": tool_name,
                            "arguments": json.dumps(arguments or {}, ensure_ascii=False),
                        },
                    }
                ],
            },
        ],
        "metadata": {"source": "synthetic-tool-call"},
    }


def make_text_sample(prompt, response):
    return {
        "messages": [
            {
                "role": "system",
                "content": "你是 Athlon Agent，一个 Windows 桌面编程助手。用户明确禁止调用工具时，应直接回复计划或说明。",
            },
            {"role": "user", "content": prompt},
            {"role": "assistant", "content": response},
        ],
        "metadata": {"source": "synthetic-no-tool"},
    }


def build_synthetic_tool_sft_data():
    """补充短上下文工具调用样本，让模型学会快速输出可解析的 tool_call。"""
    if not ADD_SYNTHETIC_TOOL_SFT:
        return []

    base_samples = [
        make_tool_sample("列出当前目录下的文件", "file_list", {}),
        make_tool_sample("看看项目根目录有哪些东西", "file_list", {}),
        make_tool_sample("列出 src 目录", "file_list", {"path": "src"}),
        make_tool_sample("查看 docs 目录结构", "file_list", {"path": "docs"}),
        make_tool_sample("只读检查 tools 文件夹", "file_list", {"path": "tools"}),

        make_tool_sample("打开 README.md 看内容", "file_read", {"path": "README.md"}),
        make_tool_sample("读取 tools/validate-training-data.py", "file_read", {"path": "tools/validate-training-data.py"}),
        make_tool_sample("看一下 appsettings.json 的配置", "file_read", {"path": "appsettings.json"}),
        make_tool_sample("打开 src/Athlon.Agent.App/Athlon.Agent.App.csproj", "file_read", {"path": "src/Athlon.Agent.App/Athlon.Agent.App.csproj"}),
        make_tool_sample("读取训练数据脚本", "file_read", {"path": "tools/train-athlon-agent.py"}),

        make_tool_sample("搜索 TrainingDataSettings 在哪里使用", "grep_files", {"pattern": "TrainingDataSettings"}),
        make_tool_sample("在代码里找 CorrectionDetector", "grep_files", {"pattern": "CorrectionDetector"}),
        make_tool_sample("搜索所有 async 关键字", "grep_files", {"pattern": "async"}),
        make_tool_sample("查找包含 overflow recovery 的地方", "grep_files", {"pattern": "overflow recovery"}),
        make_tool_sample("不要执行 shell，帮我搜 TrainingSampleStore", "grep_files", {"pattern": "TrainingSampleStore"}),

        make_tool_sample("找出所有 .jsonl 文件", "glob_files", {"pattern": "**/*.jsonl"}),
        make_tool_sample("看看有没有测试文件，按 *Tests.cs 找", "glob_files", {"pattern": "**/*Tests.cs"}),
        make_tool_sample("找出所有 XAML 文件", "glob_files", {"pattern": "**/*.xaml"}),
        make_tool_sample("列出所有 Python 脚本", "glob_files", {"pattern": "**/*.py"}),

        make_tool_sample("运行 dotnet --info 检查 SDK", "execute_command", {"command": "dotnet --info"}),
        make_tool_sample("执行 build.bat 看是否能编译", "execute_command", {"command": "build.bat"}),
        make_tool_sample("运行测试命令 dotnet test", "execute_command", {"command": "dotnet test"}),

        make_text_sample("不要调用工具，只告诉我你打算怎么检查", "我会先确认目标范围，再检查相关文件结构和配置项；在你允许调用工具前，我不会执行任何读取、搜索或命令。"),
        make_text_sample("先不要执行命令，给我一个排查计划", "计划是：先确认报错现象和复现步骤，再检查相关配置、依赖版本和最近改动，最后根据证据决定是否需要运行命令验证。"),
        make_text_sample("继续上一段总结，只补充风险点", "补充的风险点是：训练样本过少、工具调用格式不稳定、长 reasoning 抢占输出长度，以及评估集与真实使用分布可能不一致。"),
    ]

    correction_samples = [
        (
            "读取 tools/eval-agent-benchmark.cs",
            "file_read",
            {"path": "tools/eval-agent-benchmark.cs"},
            "文件名写错了，是 tools/eval-agent-benchmark.py",
            "file_read",
            {"path": "tools/eval-agent-benchmark.py"},
        ),
        (
            "列出 source 目录",
            "file_list",
            {"path": "source"},
            "目录名是 src，不是 source",
            "file_list",
            {"path": "src"},
        ),
        (
            "打开所有包含 TrainingSampleStore 的文件",
            "file_read",
            {"path": "TrainingSampleStore"},
            "我是想搜索引用，不是逐个打开文件",
            "grep_files",
            {"pattern": "TrainingSampleStore"},
        ),
    ]
    for prompt, wrong_tool, wrong_args, correction, right_tool, right_args in correction_samples:
        first = make_tool_sample(prompt, wrong_tool, wrong_args)["messages"][2]
        second = make_tool_sample(correction, right_tool, right_args)["messages"][2]
        base_samples.append({
            "messages": [
                {
                    "role": "system",
                    "content": "你是 Athlon Agent，一个 Windows 桌面编程助手。用户修正需求后，应更新工具和参数。",
                },
                {"role": "user", "content": prompt},
                first,
                {"role": "user", "content": correction},
                second,
            ],
            "metadata": {"source": "synthetic-correction-tool-call"},
        })

    format_contract_samples = []
    for sample in base_samples:
        if sample["metadata"]["source"].startswith("synthetic-no-tool"):
            format_contract_samples.append(sample)
            continue
        strengthened = json.loads(json.dumps(sample, ensure_ascii=False))
        strengthened["messages"][0]["content"] += (
            " 严格要求：<tool_call> 内只能是 JSON object 或 JSON array，"
            "格式必须类似 {\"name\":\"file_list\",\"arguments\":{}}，"
            "不要输出 file_list path、grep_files --pattern 这类命令行文本。"
        )
        for _ in range(SYNTHETIC_TOOL_REPEAT):
            format_contract_samples.append(strengthened)

    return format_contract_samples


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
    raw_records = []
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
                    raw_records.append(sample)

    for sample in raw_records:
        all_records.extend(split_tool_turn_samples(sample))

    synthetic_records = build_synthetic_tool_sft_data()
    all_records.extend(synthetic_records)

    print(f"  SFT 原始轨迹: {len(raw_records)}")
    print(f"  SFT 拆分/增强后样本总数: {len(all_records)}")
    if synthetic_records:
        print(f"  合成工具调用样本: {len(synthetic_records)}")
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

def resolve_model_id(model_path=None):
    """解析本地模型路径或 Hugging Face repo id。"""
    candidate = model_path or MODEL_ID
    expanded = os.path.expanduser(candidate)
    if os.path.isdir(expanded) or os.path.isabs(expanded) or candidate.startswith("."):
        return os.path.abspath(expanded), True
    return candidate, False


def load_model_and_tokenizer(model_path=None):
    """加载 Qwen3-0.6B 和 tokenizer。"""
    resolved_model_id, is_local = resolve_model_id(model_path)
    is_lora_adapter = is_local and os.path.isfile(
        os.path.join(resolved_model_id, "adapter_config.json")
    )
    tokenizer_model_id = MODEL_ID if is_lora_adapter else resolved_model_id
    tokenizer_model_id, tokenizer_is_local = resolve_model_id(tokenizer_model_id)
    print(f"  加载模型: {resolved_model_id}")
    if is_lora_adapter:
        print("  检测到 LoRA adapter，将从 base model 挂载并继续训练")

    tokenizer = AutoTokenizer.from_pretrained(
        tokenizer_model_id,
        trust_remote_code=True,
        use_fast=True,
        local_files_only=tokenizer_is_local,
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

    base_model_id = MODEL_ID if is_lora_adapter else resolved_model_id
    base_model_id, base_is_local = resolve_model_id(base_model_id)
    model = AutoModelForCausalLM.from_pretrained(
        base_model_id,
        quantization_config=bnb_config,
        device_map="auto",
        torch_dtype=torch.bfloat16,
        trust_remote_code=True,
        local_files_only=base_is_local,
    )

    if is_lora_adapter:
        model = PeftModel.from_pretrained(model, resolved_model_id, is_trainable=True)

    if USE_4BIT:
        model = prepare_model_for_kbit_training(
            model,
            use_gradient_checkpointing=True,
            gradient_checkpointing_kwargs={"use_reentrant": False},
        )
    else:
        model.enable_input_require_grads()

    return model, tokenizer, is_lora_adapter


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

def train_sft(load_path=None, epochs=None):
    """SFT 训练：修正轨迹 + 溢出恢复。"""
    print("\n" + "=" * 60)
    print("SFT 训练")
    print("=" * 60)

    # 加载数据
    dataset = load_sft_data()
    if dataset is None or len(dataset) == 0:
        print("  [SKIP] 无 SFT 数据，跳过")
        return

    # 加载模型
    model, tokenizer, is_lora_adapter = load_model_and_tokenizer(load_path)
    if not is_lora_adapter:
        model = apply_lora(model)
    else:
        model.print_trainable_parameters()

    # Tokenize
    print("  Tokenizing...")
    tokenized = dataset.map(
        lambda x: tokenize_sft_sample(x, tokenizer),
        remove_columns=dataset.column_names,
        desc="Tokenizing",
    )
    before_filter = len(tokenized)
    tokenized = tokenized.filter(
        lambda x: any(label != -100 for label in x["labels"]),
        desc="Filtering empty-label samples",
    )
    removed = before_filter - len(tokenized)
    if removed:
        print(f"  过滤无有效 label 样本: {removed} 条")
    if len(tokenized) == 0:
        print("  [SKIP] Tokenize 后无可训练样本，跳过")
        return

    # 统计 token 分布
    token_lens = [len(x["input_ids"]) for x in tokenized]
    print(f"  序列长度: avg={sum(token_lens)/len(token_lens):.0f}, "
          f"max={max(token_lens)}, min={min(token_lens)}")

    # 训练参数
    training_args = TrainingArguments(
        output_dir=OUTPUT_DIR_SFT,
        per_device_train_batch_size=SFT_BATCH_SIZE,
        gradient_accumulation_steps=SFT_GRAD_ACCUMULATION,
        num_train_epochs=epochs or 5,
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
    print(f"  [OK] SFT 模型已保存: {OUTPUT_DIR_SFT}")


def train_dpo(load_path=None, epochs=None):
    """DPO 训练：工具选型偏好优化。"""
    # 延迟导入，避免 llm_blender 兼容性问题
    from trl import DPOTrainer, DPOConfig

    print("\n" + "=" * 60)
    print("DPO 训练")
    print("=" * 60)

    # 加载数据
    dataset = load_dpo_data()
    if dataset is None or len(dataset) == 0:
        print("  [SKIP] 无 DPO 数据，跳过")
        return

    # 加载模型（DPO 需要 base model + ref model）
    model, tokenizer, is_lora_adapter = load_model_and_tokenizer(load_path)
    if not is_lora_adapter:
        model = apply_lora(model)
    else:
        model.print_trainable_parameters()

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
        num_train_epochs=epochs or 3,
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
    print(f"  [OK] DPO 模型已保存: {OUTPUT_DIR_DPO}")


# ==================== 推理测试 ====================

def test_inference(model_path, prompt):
    """用训练好的模型做推理测试。"""
    print(f"\n{'=' * 60}")
    print(f"推理测试")
    print(f"{'=' * 60}")

    model, tokenizer, _ = load_model_and_tokenizer(model_path)
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
    global MODEL_ID

    parser = argparse.ArgumentParser(description="Athlon Agent 训练数据 → Qwen3-0.6B 微调")
    parser.add_argument("--mode", choices=["sft", "dpo", "all", "test"], default="sft",
                        help="训练模式: sft=修正轨迹, dpo=工具偏好, all=先sft再dpo, test=测试")
    parser.add_argument("--epochs", type=int, default=None,
                        help="训练轮数（覆盖默认值）")
    parser.add_argument("--base-model", type=str, default=None,
                        help="base model 路径或 Hugging Face 模型名，默认 Qwen/Qwen3-0.6B")
    parser.add_argument("--load", type=str, default=None,
                        help="加载已有 LoRA 权重路径")
    parser.add_argument("--prompt", type=str, default="帮我看看当前目录有什么文件",
                        help="测试推理用的 prompt")
    args = parser.parse_args()
    if args.base_model:
        MODEL_ID = args.base_model

    # 统计数据
    print("\n[INFO] 数据概览")
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
        train_sft(args.load, args.epochs)
    elif args.mode == "dpo":
        train_dpo(args.load, args.epochs)
    elif args.mode == "all":
        train_sft(args.load, args.epochs)
        train_dpo(OUTPUT_DIR_SFT, args.epochs)
    elif args.mode == "test":
        load_path = args.load or OUTPUT_DIR_SFT
        if os.path.isdir(load_path):
            test_inference(load_path, args.prompt)
        else:
            print(f"  [ERROR] 模型路径不存在: {load_path}")
            print("  请先运行: python tools/train-athlon-agent.py --mode sft")

    print("\n[OK] 完成！")


if __name__ == "__main__":
    main()
