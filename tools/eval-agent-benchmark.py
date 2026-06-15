"""
Athlon Agent 训练前后模型能力对比评估

在 30+ 个典型 Agent 场景上对比 base model vs LoRA-tuned model，
量化评估工具选型、格式合规、纠错恢复、溢出恢复四项能力。

用法:
  # 对比 base 和最新的 sft_lora（推荐）
  python tools/eval-agent-benchmark.py

  # 指定对比两条 LoRA
  python tools/eval-agent-benchmark.py --lora-a ./model/Qwen3-0.6B_sft_lora --lora-b ./model/Qwen3-0.6B_lora

  # 只跑 base（不加载 LoRA），用于首次采集 baseline
  python tools/eval-agent-benchmark.py --base-only

  # 输出详细 JSON 结果
  python tools/eval-agent-benchmark.py --output eval-results.json

依赖:
  pip install torch transformers peft
"""

import argparse
from difflib import SequenceMatcher
import json
import os
import re
import sys
import time

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel

# ==================== 配置 ====================
_SRC_DIR = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.path.join(_SRC_DIR, "model")
if not os.path.isdir(MODEL_DIR):
    MODEL_DIR = os.path.join(_SRC_DIR, "..", "model")  # fallback for older runs
LOCAL_BASE_MODEL_ID = os.path.join(MODEL_DIR, "Qwen3-0.6B")
DEFAULT_REMOTE_MODEL_ID = "Qwen/Qwen3-0.6B"
BASE_MODEL_ID = os.environ.get(
    "ATHLON_BASE_MODEL",
    LOCAL_BASE_MODEL_ID if os.path.isdir(LOCAL_BASE_MODEL_ID) else DEFAULT_REMOTE_MODEL_ID,
)
DEFAULT_LORA_A = os.path.join(MODEL_DIR, "Qwen3-0.6B_sft_lora")
DEFAULT_LORA_B = os.path.join(MODEL_DIR, "Qwen3-0.6B_dpo_lora")
TRAINING_DATA_DIR = os.path.expanduser("~/.athlon-agent/training-data")
LEAK_SIMILARITY_THRESHOLD = 0.92
MAX_NEW_TOKENS = 512
TEMPERATURE = 0.01  # 低温度保证可复现
TOP_P = 0.9
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# ==================== 基准测试集 ====================

BENCHMARKS = [
    # --- 工具选型 (Tool Selection) ---
    {
        "id": "tool-list-files",
        "category": "tool-selection",
        "prompt": "帮我看看当前目录有什么文件",
        "expected_tool": "file_list",
        "expected_args_keys": [],
        "description": "文件列表 - 最常用，应选 file_list",
    },
    {
        "id": "tool-grep-code",
        "category": "tool-selection",
        "prompt": "在代码中搜索所有包含 'TrainingSample' 的地方",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "代码搜索 - 应选 grep_files",
    },
    {
        "id": "tool-read-file",
        "category": "tool-selection",
        "prompt": "打开 src/Athlon.Agent.Core/TrainingData/CorrectionDetector.cs 看看内容",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "读取文件 - 应选 file_read",
    },
    {
        "id": "tool-find-glob",
        "category": "tool-selection",
        "prompt": "找出项目中所有的 .jsonl 文件",
        "expected_tool": "glob_files",
        "expected_args_keys": ["pattern"],
        "description": "通配查找 - 应选 glob_files",
    },
    {
        "id": "tool-execute",
        "category": "tool-selection",
        "prompt": "帮我执行一下 build.bat 看看能不能编译通过",
        "expected_tool": "execute_command",
        "expected_args_keys": ["command"],
        "description": "执行命令 - 应选 execute_command",
    },
    {
        "id": "tool-search-code",
        "category": "tool-selection",
        "prompt": "在整个项目里搜索 'CorrectionDetector' 这个类在哪定义的",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "全文搜索类定义 - 应选 grep_files",
    },
    {
        "id": "tool-list-dir-detail",
        "category": "tool-selection",
        "prompt": "列出 src 目录的详细结构",
        "expected_tool": "file_list",
        "expected_args_keys": ["path"],
        "description": "列出子目录 - 应选 file_list(path=src/)",
    },
    # --- 格式合规 (Format Compliance) ---
    {
        "id": "format-multi-tool",
        "category": "format-compliance",
        "prompt": "先看看当前目录结构，再打开 README.md 内容",
        "expected_tool": "file_list",  # 第一个 tool_call
        "expected_args_keys": [],
        "description": "连续两个工具调用 - 格式是否正确",
        "check_multi": True,
    },
    {
        "id": "format-reasoning",
        "category": "format-compliance",
        "prompt": "分析一下项目的代码质量，先看看整体结构再下定论",
        "expected_tool": "file_list",
        "expected_args_keys": [],
        "description": "要求包含 reasoning 思考过程",
        "check_reasoning": True,
    },
    # --- 纠错恢复 (Error Recovery) ---
    {
        "id": "error-correct-path",
        "category": "error-recovery",
        "prompt": "打开文件 src/Athlon.Agent.Core/Program.cs",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "路径错误 -> 用户修正路径 - 模型在修正后应改变参数",
        "correction": "那个路径不对，用 src/Athlon.Agent/TrainingData/CorrectionDetector.cs",
        "expected_corrected_tool": "file_read",
        "expected_corrected_args_path_contains": "CorrectionDetector",
    },
    {
        "id": "error-wrong-tool",
        "category": "error-recovery",
        "prompt": "搜索文件里包含 'async' 的地方",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "选错工具 -> 用户纠正 - 应换工具",
        "correction": "不要用搜索，用 file_read 直接读文件",
        "expected_corrected_tool": "file_read",
    },
    # --- 溢出恢复 (Overflow Recovery) ---
    {
        "id": "overflow-continue-1",
        "category": "overflow-recovery",
        "prompt": "继续你刚才的分析，不要重复已经说过的内容",
        "expected_tool": None,
        "expected_args_keys": [],
        "description": "溢出恢复 - 模型应理解 '继续' 意味着续接而非重新开始",
        "check_no_restart": True,
    },
    {
        "id": "overflow-continue-2",
        "category": "overflow-recovery",
        "prompt": "接着分析，接着做",
        "expected_tool": None,
        "expected_args_keys": [],
        "description": "溢出恢复 - 中文 '接着做'",
        "check_no_restart": True,
    },
    # --- 系统指令遵从 (Instruction Following) ---
    {
        "id": "instruct-no-shell",
        "category": "instruction-following",
        "prompt": "不要执行任何命令，只告诉我你打算怎么做",
        "expected_tool": None,  # 不应调用 tool
        "expected_args_keys": [],
        "description": "指令遵从 - 用户要求不执行，模型应直接回复",
        "no_tool_call": True,
    },
    {
        "id": "instruct-read-only",
        "category": "instruction-following",
        "prompt": "只读操作，不要修改任何文件。帮我看看 readme 有没有拼写错误",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "指令遵从 - 只读场景，不应出现 file_edit/execute_command",
        "forbidden_tools": ["file_edit", "execute_command", "file_write"],
    },
]

HELDOUT_BENCHMARKS = [
    {
        "id": "heldout-tool-list-root",
        "category": "tool-selection",
        "prompt": "看一下这个仓库根目录下有哪些东西，先不要读文件内容",
        "expected_tool": "file_list",
        "expected_args_keys": [],
        "description": "留出: 根目录列表",
    },
    {
        "id": "heldout-tool-list-docs",
        "category": "tool-selection",
        "prompt": "帮我列一下 docs 目录下面有哪些文档",
        "expected_tool": "file_list",
        "expected_args_keys": ["path"],
        "description": "留出: 子目录列表",
    },
    {
        "id": "heldout-tool-read-csproj",
        "category": "tool-selection",
        "prompt": "读取 src/Athlon.Agent.App/Athlon.Agent.App.csproj 的内容",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "留出: 读取项目文件",
    },
    {
        "id": "heldout-tool-read-settings",
        "category": "tool-selection",
        "prompt": "打开 appsettings.json 看看有没有训练数据配置",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "留出: 读取配置文件",
    },
    {
        "id": "heldout-tool-grep-collector",
        "category": "tool-selection",
        "prompt": "搜索 TrainingDataSettings 在哪里被使用",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 搜索符号引用",
    },
    {
        "id": "heldout-tool-grep-timeout",
        "category": "tool-selection",
        "prompt": "查一下代码里所有提到 overflow recovery 的地方",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 搜索英文短语",
    },
    {
        "id": "heldout-tool-glob-xaml",
        "category": "tool-selection",
        "prompt": "找出所有 XAML 文件",
        "expected_tool": "glob_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 通配符查找 XAML",
    },
    {
        "id": "heldout-tool-glob-tests",
        "category": "tool-selection",
        "prompt": "看看仓库里有没有测试文件，按 *Tests.cs 找",
        "expected_tool": "glob_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 通配符查找测试",
    },
    {
        "id": "heldout-tool-execute-dotnet",
        "category": "tool-selection",
        "prompt": "运行 dotnet --info，确认当前 .NET SDK 环境",
        "expected_tool": "execute_command",
        "expected_args_keys": ["command"],
        "description": "留出: 执行环境检查命令",
    },
    {
        "id": "heldout-format-two-reads",
        "category": "format-compliance",
        "prompt": "先列出 tools 目录，再读取 tools/validate-training-data.py",
        "expected_tool": "file_list",
        "expected_args_keys": ["path"],
        "description": "留出: 多步工具调用格式",
        "check_multi": True,
    },
    {
        "id": "heldout-format-json-args",
        "category": "format-compliance",
        "prompt": "用工具搜索所有包含 SampleRate 的代码位置",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 参数必须是 JSON object",
    },
    {
        "id": "heldout-format-reason-before-action",
        "category": "format-compliance",
        "prompt": "先分析应该用哪个工具，再查看 README 里的训练数据说明",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "留出: 推理后选择读取工具",
        "check_reasoning": True,
    },
    {
        "id": "heldout-error-correct-extension",
        "category": "error-recovery",
        "prompt": "读取 tools/eval-agent-benchmark.cs",
        "expected_tool": "file_read",
        "expected_args_keys": ["path"],
        "description": "留出: 用户修正扩展名",
        "correction": "文件名写错了，是 tools/eval-agent-benchmark.py",
        "expected_corrected_tool": "file_read",
        "expected_corrected_args_path_contains": "eval-agent-benchmark.py",
    },
    {
        "id": "heldout-error-correct-tool",
        "category": "error-recovery",
        "prompt": "打开所有包含 TrainingSampleStore 的文件",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 从含糊打开修正为搜索",
        "correction": "我是想搜索引用，不是逐个打开文件",
        "expected_corrected_tool": "grep_files",
    },
    {
        "id": "heldout-error-correct-dir",
        "category": "error-recovery",
        "prompt": "列出 source 目录结构",
        "expected_tool": "file_list",
        "expected_args_keys": ["path"],
        "description": "留出: 修正目录名",
        "correction": "目录名是 src，不是 source",
        "expected_corrected_tool": "file_list",
        "expected_corrected_args_path_contains": "src",
    },
    {
        "id": "heldout-overflow-continue-summary",
        "category": "overflow-recovery",
        "prompt": "继续上一段总结，只补充遗漏的风险点",
        "expected_tool": None,
        "expected_args_keys": [],
        "description": "留出: 继续总结而不是重启",
        "check_no_restart": True,
    },
    {
        "id": "heldout-overflow-resume-work",
        "category": "overflow-recovery",
        "prompt": "接着刚才的步骤往下做，别从头解释",
        "expected_tool": None,
        "expected_args_keys": [],
        "description": "留出: 续接执行步骤",
        "check_no_restart": True,
    },
    {
        "id": "heldout-instruct-plan-only",
        "category": "instruction-following",
        "prompt": "先不要调用工具，只给我一个检查训练数据质量的计划",
        "expected_tool": None,
        "expected_args_keys": [],
        "description": "留出: 明确禁止工具调用",
        "no_tool_call": True,
    },
    {
        "id": "heldout-instruct-no-write",
        "category": "instruction-following",
        "prompt": "只读检查 tools 目录，不要写文件也不要运行命令",
        "expected_tool": "file_list",
        "expected_args_keys": ["path"],
        "description": "留出: 只读约束",
        "forbidden_tools": ["file_edit", "file_write", "execute_command"],
    },
    {
        "id": "heldout-instruct-no-shell-search",
        "category": "instruction-following",
        "prompt": "不要执行 shell，帮我找一下项目里哪里配置了 TrainingData",
        "expected_tool": "grep_files",
        "expected_args_keys": ["pattern"],
        "description": "留出: 禁止 shell 但允许搜索工具",
        "forbidden_tools": ["execute_command"],
    },
]

def resolve_model_id(model_path):
    """解析本地模型路径或 Hugging Face repo id。"""
    expanded = os.path.expanduser(model_path)
    if os.path.isdir(expanded) or os.path.isabs(expanded) or model_path.startswith("."):
        return os.path.abspath(expanded), True
    return model_path, False


def iter_training_prompts(data_dir):
    """读取训练 JSONL 中出现过的用户 prompt，用于简单泄漏检查。"""
    prompts = []
    if not data_dir or not os.path.isdir(data_dir):
        return prompts

    for name in os.listdir(data_dir):
        if not (name.startswith("sft-traces-") or name.startswith("dpo-preference-")):
            continue
        if not name.endswith(".jsonl"):
            continue
        path = os.path.join(data_dir, name)
        with open(path, encoding="utf-8-sig") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    sample = json.loads(line)
                except json.JSONDecodeError:
                    continue
                message_groups = []
                if isinstance(sample.get("messages"), list):
                    message_groups.append(sample["messages"])
                for key in ("prompt", "chosen", "rejected"):
                    if isinstance(sample.get(key), list):
                        message_groups.append(sample[key])
                for messages in message_groups:
                    for msg in messages:
                        if msg.get("role") == "user" and msg.get("content"):
                            prompts.append(str(msg["content"]))
    return prompts


def similarity(a, b):
    """中文短 prompt 用字符级相似度足够做粗略泄漏过滤。"""
    return SequenceMatcher(None, a, b).ratio()


def filter_leaked_benchmarks(benchmarks, training_prompts, threshold):
    """跳过与训练 prompt 过近的 eval 项，避免把记忆当能力提升。"""
    if not training_prompts:
        return benchmarks, []

    kept = []
    skipped = []
    for bench in benchmarks:
        prompt = bench["prompt"]
        best_score = 0.0
        best_prompt = ""
        for train_prompt in training_prompts:
            score = similarity(prompt, train_prompt)
            if score > best_score:
                best_score = score
                best_prompt = train_prompt
        if best_score >= threshold:
            skipped.append({
                "id": bench["id"],
                "prompt": prompt,
                "matched_training_prompt": best_prompt,
                "similarity": round(best_score, 3),
            })
        else:
            kept.append(bench)
    return kept, skipped


def normalize_tool_call(call):
    """统一 Qwen/Hermes 与 OpenAI 风格 tool call。"""
    if not isinstance(call, dict):
        return None
    if "function" in call and isinstance(call["function"], dict):
        fn = call["function"]
        return {
            "function": {
                "name": fn.get("name", ""),
                "arguments": fn.get("arguments", {}),
            }
        }
    if "name" in call:
        return {
            "function": {
                "name": call.get("name", ""),
                "arguments": call.get("arguments", {}),
            }
        }
    return None


def load_model(model_path, lora_path=None):
    """加载模型（可选加载 LoRA 权重）。"""
    resolved_model_id, is_local = resolve_model_id(model_path)
    tokenizer = AutoTokenizer.from_pretrained(
        resolved_model_id,
        trust_remote_code=True,
        use_fast=True,
        local_files_only=is_local,
    )
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    if tokenizer.chat_template is None:
        tokenizer.chat_template = (
            "{% for message in messages %}"
            "{{'<|im_start|>' + message['role'] + '\\n' + message['content'] + '<|im_end|>' + '\\n'}}"
            "{% endfor %}"
            "{% if add_generation_prompt %}{{ '<|im_start|>assistant\\n' }}{% endif %}"
        )

    model = AutoModelForCausalLM.from_pretrained(
        resolved_model_id,
        device_map="auto",
        torch_dtype=torch.bfloat16 if DEVICE == "cuda" else torch.float32,
        trust_remote_code=True,
        local_files_only=is_local,
    )
    model.eval()

    if lora_path and os.path.isdir(lora_path):
        print(f"    加载 LoRA: {lora_path}")
        model = PeftModel.from_pretrained(model, lora_path)
        model.eval()

    return model, tokenizer


def parse_tool_calls(text):
    """从模型输出中解析 tool_calls JSON 片段。"""
    calls = []

    # Qwen3/Hermes 格式: <tool_call>{"name": "...", "arguments": {...}}</tool_call>
    for m in re.finditer(r"<tool_call>\s*(.*?)\s*</tool_call>", text, re.DOTALL):
        try:
            data = json.loads(m.group(1))
        except json.JSONDecodeError:
            continue
        if isinstance(data, list):
            for item in data:
                normalized = normalize_tool_call(item)
                if normalized:
                    calls.append(normalized)
        else:
            normalized = normalize_tool_call(data)
            if normalized:
                calls.append(normalized)

    if calls:
        return calls

    # OpenAI 风格文本片段: "tool_calls": [...]
    decoder = json.JSONDecoder()
    for m in re.finditer(r'"tool_calls"\s*:\s*', text):
        start = m.end()
        try:
            calls_data, _ = decoder.raw_decode(text[start:])
            if isinstance(calls_data, list):
                for item in calls_data:
                    normalized = normalize_tool_call(item)
                    if normalized:
                        calls.append(normalized)
        except (json.JSONDecodeError, KeyError):
            pass

    # 也尝试匹配函数调用格式: {"name": "xxx", "arguments": {...}}
    if not calls:
        pattern2 = r'"name"\s*:\s*"(\w+)"'
        func_names = re.findall(pattern2, text)
        for name in func_names:
            calls.append({"function": {"name": name, "arguments": {}}})

    return calls


def has_reasoning(text):
    """检查输出是否包含 reasoning/think 块。"""
    return bool(re.search(r'<think>|"reasoning"\s*:|思考过程|我来分析', text))


def has_tool_call(text):
    """检查输出是否包含工具调用。"""
    return bool(re.search(r"<tool_call>|\"tool_calls\"|\"function\"\s*:\s*\{|\"name\"\s*:\s*\"\w+\"", text))


def contains_forbidden_tool(text, forbidden):
    """检查是否包含被禁止的工具调用。"""
    for tool in forbidden:
        if tool in text:
            return True
    return False


def generate(model, tokenizer, messages, system_prompt=None):
    """生成回复。"""
    if system_prompt:
        msgs = [{"role": "system", "content": system_prompt}] + messages
    else:
        msgs = messages

    text = tokenizer.apply_chat_template(msgs, tokenize=False, add_generation_prompt=True)

    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=MAX_NEW_TOKENS,
            do_sample=False,
        )
    response = tokenizer.decode(
        outputs[0][inputs["input_ids"].shape[1]:],
        skip_special_tokens=True,
    )
    return response.strip()


# ==================== 评估 ====================

def evaluate_single(bench, model, tokenizer, system_prompt):
    """在单个 benchmark 上评估模型。"""
    result = {
        "id": bench["id"],
        "category": bench["category"],
        "description": bench["description"],
        "prompt": bench["prompt"],
        "expected_tool": bench.get("expected_tool"),
        "passed": True,
        "checks": {},
        "details": {},
        "failure_reasons": [],
    }

    # --- Round 1: 首次回复 ---
    t0 = time.time()
    response1 = generate(model, tokenizer, [{"role": "user", "content": bench["prompt"]}], system_prompt)
    latency = time.time() - t0
    result["latency"] = round(latency, 2)
    result["response1"] = response1
    calls1 = parse_tool_calls(response1)
    result["tool_calls_1"] = [
        {"name": c.get("function", {}).get("name", "?")}
        for c in calls1[:3]
    ]
    has_tc1 = len(calls1) > 0

    # --- 检查: 格式合规 ---
    if bench.get("check_reasoning"):
        has_re = has_reasoning(response1)
        result["checks"]["has_reasoning"] = has_re
        if not has_re:
            result["passed"] = False
            result["failure_reasons"].append("missing_reasoning")

    if bench.get("check_multi"):
        result["checks"]["multi_tool_count"] = len(calls1)
        if len(calls1) < 1:
            result["passed"] = False
            result["failure_reasons"].append("missing_multi_tool_call")

    if bench.get("no_tool_call"):
        result["checks"]["no_tool_call"] = not has_tc1
        if has_tc1:
            result["passed"] = False
            result["failure_reasons"].append("unexpected_tool_call")

    forbidden = bench.get("forbidden_tools", [])
    if forbidden:
        has_forbidden = contains_forbidden_tool(response1, forbidden)
        result["checks"]["forbidden_used"] = has_forbidden
        if has_forbidden:
            result["passed"] = False
            result["failure_reasons"].append("forbidden_tool_used")

    if bench.get("check_no_restart"):
        # 溢出恢复场景：模型不应重新开始(说"开始分析")
        no_restart = not re.search(r"开始|首先[^,]|第一步", response1)
        result["checks"]["no_restart"] = no_restart
        if no_restart is False:
            result["passed"] = False
            result["failure_reasons"].append("restarted_instead_of_continuing")
        # 跳过工具匹配检查
        result["details"]["note"] = "溢出恢复: 评估重点为续接而非重新开始"
        return result

    # --- 检查: 工具选型 ---
    expected = bench.get("expected_tool")
    if expected and has_tc1:
        first_tool = calls1[0].get("function", {}).get("name", "")
        result["checks"]["tool_match"] = (first_tool == expected)
        if not result["checks"]["tool_match"]:
            result["passed"] = False
            result["failure_reasons"].append("wrong_tool")

        # 检查参数
        expected_args_keys = bench.get("expected_args_keys", [])
        if expected_args_keys:
            args_found = []
            try:
                args_str = calls1[0].get("function", {}).get("arguments", "{}")
                if isinstance(args_str, str):
                    args_obj = json.loads(args_str)
                else:
                    args_obj = args_str
                for k in expected_args_keys:
                    args_found.append(k in args_obj)
                result["checks"]["args_match"] = all(args_found) if args_found else True
            except (json.JSONDecodeError, KeyError):
                result["checks"]["args_match"] = False
            if not result["checks"].get("args_match", True):
                result["passed"] = False
                result["failure_reasons"].append("wrong_or_missing_arguments")
    elif expected and not has_tc1:
        result["checks"]["tool_match"] = False
        result["passed"] = False
        result["failure_reasons"].append("missing_tool_call")

    # --- Round 2: 纠错场景 ---
    correction = bench.get("correction")
    if correction and has_tc1:
        messages = [
            {"role": "user", "content": bench["prompt"]},
            {"role": "assistant", "content": response1},
            {"role": "user", "content": correction},
        ]
        response2 = generate(model, tokenizer, messages, system_prompt)
        result["correction"] = correction
        result["response2"] = response2
        calls2 = parse_tool_calls(response2)

        expected_corrected = bench.get("expected_corrected_tool")
        if expected_corrected and calls2:
            second_tool = calls2[0].get("function", {}).get("name", "")
            result["checks"]["correction_tool_match"] = (second_tool == expected_corrected)
            if not result["checks"]["correction_tool_match"]:
                result["passed"] = False
                result["failure_reasons"].append("wrong_corrected_tool")

            # 检查修正后的路径是否包含预期关键词
            path_contains = bench.get("expected_corrected_args_path_contains")
            if path_contains:
                try:
                    args_str = calls2[0].get("function", {}).get("arguments", "{}")
                    if isinstance(args_str, str):
                        args_obj = json.loads(args_str)
                    else:
                        args_obj = args_str
                    args_text = json.dumps(args_obj)
                    result["checks"]["correction_path_contains"] = path_contains in args_text
                except (json.JSONDecodeError, KeyError):
                    result["checks"]["correction_path_contains"] = False
                if not result["checks"]["correction_path_contains"]:
                    result["passed"] = False
                    result["failure_reasons"].append("corrected_arguments_not_applied")
        elif expected_corrected and not calls2:
            result["checks"]["correction_tool_match"] = False
            result["passed"] = False
            result["failure_reasons"].append("missing_corrected_tool_call")

    return result


def print_report(name, results, total, passed):
    """打印单个模型的评估报告。"""
    print(f"\n{'=' * 65}")
    print(f"  [REPORT] {name}")
    print(f"{'=' * 65}")
    print(f"  总分: {passed}/{total}  ({passed/total*100:.1f}%)")

    # 按类别统计
    categories = {}
    for r in results:
        cat = r["category"]
        categories.setdefault(cat, {"total": 0, "passed": 0, "items": []})
        categories[cat]["total"] += 1
        if r["passed"]:
            categories[cat]["passed"] += 1
        categories[cat]["items"].append(r)

    print(f"\n  {'类别':<25} {'得分':>8} {'通过率':>8}")
    print(f"  {'-'*25} {'-'*8} {'-'*8}")
    for cat, data in sorted(categories.items()):
        rate = data["passed"] / data["total"] * 100
        print(f"  {cat:<25} {data['passed']}/{data['total']:<4} {rate:>6.1f}%")

    # 失败详情
    failures = [r for r in results if not r["passed"]]
    if failures:
        print(f"\n  [FAIL] 失败项 ({len(failures)}):")
        for r in failures:
            print(f"    - [{r['id']}] {r['description']}")
            for check, val in r.get("checks", {}).items():
                if val is False:
                    print(f"       -> {check}: failed")

    return categories


def main():
    global MAX_NEW_TOKENS

    parser = argparse.ArgumentParser(description="Athlon Agent 训练前后模型对比评估")
    parser.add_argument("--base-model", default=BASE_MODEL_ID,
                        help="base model 路径或 Hugging Face 模型名，默认 Qwen/Qwen3-0.6B")
    parser.add_argument("--lora-a", default=DEFAULT_LORA_A, help="LoRA 模型 A 路径")
    parser.add_argument("--lora-b", default=DEFAULT_LORA_B, help="LoRA 模型 B 路径")
    parser.add_argument("--base-only", action="store_true", help="只评估 base model（不加载 LoRA）")
    parser.add_argument("--compare", action="store_true", default=True, help="对比 base vs LoRA A")
    parser.add_argument("--output", default=None, help="输出 JSON 结果到文件")
    parser.add_argument("--max-new-tokens", type=int, default=MAX_NEW_TOKENS,
                        help="每题最大生成 token 数，工具调用评测建议 128")
    parser.add_argument("--training-data-dir", default=TRAINING_DATA_DIR,
                        help="训练数据目录，用于 eval prompt 泄漏检查")
    parser.add_argument("--leak-threshold", type=float, default=LEAK_SIMILARITY_THRESHOLD,
                        help="eval prompt 与训练 prompt 的相似度跳过阈值")
    parser.add_argument("--no-heldout", action="store_true",
                        help="只运行原始 BENCHMARKS，不追加 heldout 题集")
    parser.add_argument("--system-prompt",
                        default="你是 Athlon Agent，一个 Windows 桌面编程助手。"
                                "你有以下工具可用：file_list（列出目录）、file_read（读取文件）、"
                                "grep_files（搜索文件内容）、glob_files（通配符查找文件）、"
                                "execute_command（执行命令）、file_edit（编辑文件）、file_write（写文件）。"
                                "请选最合适的工具。",
                        help="系统提示词")
    args = parser.parse_args()
    MAX_NEW_TOKENS = args.max_new_tokens

    system_prompt = args.system_prompt
    benchmarks = list(BENCHMARKS)
    if not args.no_heldout:
        benchmarks.extend(HELDOUT_BENCHMARKS)
    training_prompts = iter_training_prompts(args.training_data_dir)
    benchmarks, skipped_for_leakage = filter_leaked_benchmarks(
        benchmarks,
        training_prompts,
        args.leak_threshold,
    )

    print(f"\n{'=' * 65}")
    print(f"  Athlon Agent 训练前后对比评估")
    print(f"  基准测试: {len(benchmarks)} 项")
    print(f"  模型: {args.base_model}")
    print(f"  LoRA A: {args.lora_a if os.path.isdir(args.lora_a) else '不存在'}")
    print(f"  LoRA B: {args.lora_b if os.path.isdir(args.lora_b) else '不存在'}")
    print(f"  训练 prompt: {len(training_prompts)} 条")
    print(f"  泄漏跳过: {len(skipped_for_leakage)} 项")
    print(f"{'=' * 65}")
    for item in skipped_for_leakage[:10]:
        print(f"  [LEAK-SKIP] {item['id']} similarity={item['similarity']}")

    all_results = {}

    # --- 1. Base Model ---
    if not args.base_only:
        print(f"\n  >>> [阶段 1/3] 加载 Base Model...")
    else:
        print(f"\n  >>> [加载 Base Model...]")

    model_base, tokenizer = load_model(args.base_model)
    print(f"    设备: {model_base.device}")

    results_base = []
    passed_base = 0
    t_start = time.time()
    for i, bench in enumerate(benchmarks):
        print(f"    [{i+1}/{len(benchmarks)}] {bench['id']}...", end=" ", flush=True)
        result = evaluate_single(bench, model_base, tokenizer, system_prompt)
        results_base.append(result)
        if result["passed"]:
            passed_base += 1
            print("[OK]")
        else:
            print("[FAIL]")
    t_base = time.time() - t_start

    print(f"    耗时: {t_base:.1f}s")
    all_results["base"] = {
        "results": results_base,
        "passed": passed_base,
        "total": len(benchmarks),
        "time": round(t_base, 1),
    }
    if not args.base_only:
        cat_base = print_report("BASE MODEL (原始 Qwen3-0.6B)", results_base, len(benchmarks), passed_base)
    else:
        print(f"\n  [OK] Base 评估完成: {passed_base}/{len(benchmarks)}")

    # 释放 base model 显存
    del model_base
    if DEVICE == "cuda":
        torch.cuda.empty_cache()

    # --- 2. LoRA A (SFT训练后) ---
    if os.path.isdir(args.lora_a):
        print(f"\n  >>> [阶段 2/3] 加载 LoRA A: {args.lora_a}")
        model_a, _ = load_model(args.base_model, args.lora_a)

        results_a = []
        passed_a = 0
        t_start = time.time()
        for i, bench in enumerate(benchmarks):
            print(f"    [{i+1}/{len(benchmarks)}] {bench['id']}...", end=" ", flush=True)
            result = evaluate_single(bench, model_a, tokenizer, system_prompt)
            results_a.append(result)
            if result["passed"]:
                passed_a += 1
                print("[OK]")
            else:
                print("[FAIL]")
        t_a = time.time() - t_start
        print(f"    耗时: {t_a:.1f}s")
        all_results["lora_a"] = {
            "results": results_a,
            "passed": passed_a,
            "total": len(benchmarks),
            "time": round(t_a, 1),
            "path": args.lora_a,
        }
        cat_a = print_report(f"LoRA A (SFT 训练后) - {os.path.basename(args.lora_a)}", results_a, len(benchmarks), passed_a)
        del model_a
        if DEVICE == "cuda":
            torch.cuda.empty_cache()
    else:
        cat_a = None

    # --- 3. LoRA B (可选) ---
    if os.path.isdir(args.lora_b):
        print(f"\n  >>> [阶段 3/3] 加载 LoRA B: {args.lora_b}")
        model_b, _ = load_model(args.base_model, args.lora_b)

        results_b = []
        passed_b = 0
        t_start = time.time()
        for i, bench in enumerate(benchmarks):
            print(f"    [{i+1}/{len(benchmarks)}] {bench['id']}...", end=" ", flush=True)
            result = evaluate_single(bench, model_b, tokenizer, system_prompt)
            results_b.append(result)
            if result["passed"]:
                passed_b += 1
                print("[OK]")
            else:
                print("[FAIL]")
        t_b = time.time() - t_start
        print(f"    耗时: {t_b:.1f}s")
        all_results["lora_b"] = {
            "results": results_b,
            "passed": passed_b,
            "total": len(benchmarks),
            "time": round(t_b, 1),
            "path": args.lora_b,
        }
        cat_b = print_report(f"LoRA B - {os.path.basename(args.lora_b)}", results_b, len(benchmarks), passed_b)
        del model_b
        if DEVICE == "cuda":
            torch.cuda.empty_cache()
    else:
        cat_b = None

    # ==================== 总结对比 ====================
    print(f"\n{'=' * 65}")
    print(f"  [SUMMARY] 总结对比")
    print(f"{'=' * 65}")

    models_info = [("base", "原始 Qwen3-0.6B")]
    if "lora_a" in all_results:
        models_info.append(("lora_a", f"LoRA A ({os.path.basename(args.lora_a)})"))
    if "lora_b" in all_results:
        models_info.append(("lora_b", f"LoRA B ({os.path.basename(args.lora_b)})"))

    print(f"\n  {'模型':<30} {'总分':>10} {'通过率':>8} {'耗时':>8}")
    print(f"  {'-'*30} {'-'*10} {'-'*8} {'-'*8}")
    for key, label in models_info:
        data = all_results[key]
        pct = data["passed"] / data["total"] * 100
        print(f"  {label:<30} {data['passed']}/{data['total']:<4} {pct:>6.1f}% {data['time']:>6.1f}s")

    # 按类别对比
    print(f"\n  [BY CATEGORY] 按类别对比:")
    categories_order = ["tool-selection", "format-compliance", "error-recovery",
                        "overflow-recovery", "instruction-following"]
    cat_labels = {
        "tool-selection": "工具选型",
        "format-compliance": "格式合规",
        "error-recovery": "纠错恢复",
        "overflow-recovery": "溢出恢复",
        "instruction-following": "指令遵从",
    }

    header = f"  {'类别':<16}"
    for _, label in models_info:
        header += f" {label:<18}"
    print(header)
    print(f"  {'-'*16}" + " " + " ".join(["-"*18 for _ in models_info]))

    for cat in categories_order:
        row = f"  {cat_labels.get(cat, cat):<16}"
        for key, _ in models_info:
            cat_data = {}
            for r in all_results[key]["results"]:
                if r["category"] == cat:
                    cat_data.setdefault(f"total", 0)
                    cat_data["total"] += 1
                    if r["passed"]:
                        cat_data.setdefault(f"passed", 0)
                        cat_data["passed"] += 1
            # This line was truncated/corrupted in the original write - fix it
            total = cat_data.get("total", 1)
            passed = cat_data.get("passed", 0)
            row += f" {passed}/{total} ({passed/total*100:.0f}%){'':>5}"
        print(row)

    # 输出 JSON
    if args.output:
        # 精简结果用于输出
        output_data = {
            "_meta": {
                "base_model": args.base_model,
                "benchmark_total": len(benchmarks),
                "training_prompt_count": len(training_prompts),
                "leak_threshold": args.leak_threshold,
                "skipped_for_leakage": skipped_for_leakage,
            }
        }
        for key in all_results:
            # 提取评分摘要
            summary = {
                "passed": all_results[key]["passed"],
                "total": all_results[key]["total"],
                "time": all_results[key]["time"],
                "path": all_results.get(key, {}).get("path", "base"),
            }
            # 每个 benchmark 的通过/失败
            summary["benchmarks"] = {}
            for r in all_results[key]["results"]:
                summary["benchmarks"][r["id"]] = {
                    "passed": r["passed"],
                    "category": r["category"],
                    "checks": r.get("checks", {}),
                    "failure_reasons": r.get("failure_reasons", []),
                    "tool_calls_1": r.get("tool_calls_1", []),
                    "response1": r.get("response1", ""),
                    "response2": r.get("response2", ""),
                }
            output_data[key] = summary

        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(output_data, f, ensure_ascii=False, indent=2)
        print(f"\n  [OK] 详细结果已保存: {args.output}")

    print()


if __name__ == "__main__":
    main()
