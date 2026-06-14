# Training Data Flywheel 使用指南

> 从 Athlon Agent 运行中自动提取 SFT/GRPO 训练数据。

## 原理

每次 Agent 与用户交互时，`CorrectionDetector` 自动扫描对话历史，检测两种训练数据：

### 修正轨迹 (Correction Trajectory)
**工具调用失败 → 用户修正 → 重试成功** 模式。

### 超时/溢出恢复轨迹 (Overflow Recovery)
**模型调用超时/上下文溢出 → 用户说"继续" → 恢复成功** 模式。

每发现一条轨迹，将其提取为 HuggingFace `messages` 格式的 JSON 样本。

## 启用方式

编辑 `~/.athlon-agent/config/settings.json`：

```json
{
  "trainingData": {
    "enabled": true,
    "outputDirectory": "",
    "sampleRate": 1.0
  }
}
```

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `enabled` | `false` | 关闭时零开销 |
| `outputDirectory` | `""` | 空= `~/.athlon-agent/training-data/` |
| `sampleRate` | `1.0` | 生产建议 `0.1`（采样 10%） |

## 输出格式

### SFT 文件

输出文件：`~/.athlon-agent/training-data/sft-traces-2026-06-14.jsonl`

每行一个标准 HuggingFace messages 格式的 JSON 对象（含 `reasoning` 字段支持 CoT 蒸馏）：

```json
{
  "messages": [
    {"role": "system", "content": "你是 Athlon Agent..."},
    {"role": "user", "content": "帮我看看项目结构"},
    {"role": "assistant", "content": null, "reasoning": "用户想看项目结构...",
     "tool_calls": [...]
    },
    {"role": "tool", "content": "Error: ...", "tool_call_id": "call_abc"}
  ],
  "metadata": { "source": "agent-correction", "hasReasoning": true, ... }
}
```

### DPO 文件

输出文件：`~/.athlon-agent/training-data/dpo-preference-2026-06-14.jsonl`

每行一个 chosen/rejected 偏好对，可直接被 TRL 的 `DPOTrainer` 消费：

```json
{
  "prompt": [
    {"role": "system", "content": "..."},
    {"role": "user", "content": "帮我看看项目结构"}
  ],
  "chosen": [
    {"role": "assistant", "tool_calls": [{"function": {"name": "file_list", "arguments": "{\"path\":\"src/\"}"}}]},
    {"role": "tool", "content": "src/\n  Athlon.Agent.Core/...", "tool_call_id": "call_2"}
  ],
  "rejected": [
    {"role": "assistant", "tool_calls": [{"function": {"name": "grep_files", "arguments": "{\"pattern\":\".*\"}"}}]},
    {"role": "tool", "content": "Error: grep on . is expensive", "tool_call_id": "call_1"}
  ],
  "metadata": {
    "source": "agent-correction-dpo",
    "failedToolName": "grep_files",
    "successToolName": "file_list"
  }
}
```

## 训练

项目自带的 `tools/train-athlon-agent.py` 脚本可一键完成 SFT 和 DPO 训练：

```bash
# 安装依赖
pip install torch transformers datasets peft trl accelerate

# 查看数据统计
python tools/train-athlon-agent.py

# SFT 训练（修正轨迹 + 溢出恢复，5 轮）
python tools/train-athlon-agent.py --mode sft --epochs 5

# DPO 训练（工具选型偏好，3 轮）
python tools/train-athlon-agent.py --mode dpo --epochs 3

# 全流水线：先 SFT 再 DPO
python tools/train-athlon-agent.py --mode all

# 推理测试
python tools/train-athlon-agent.py --mode test
```

### 脚本做了哪些事

| 阶段 | 处理 |
|------|------|
| **格式转换** | JSONL 中的 `messages[]` → Qwen3 chat template（`<|im_start|>`） |
| **CoT 蒸馏** | `reasoning` 字段 → `<think>...</think>` 块，loss 计入助理回复 |
| **标签掩码** | 系统提示/用户输入配 `-100`，只监督 assistant 回复 |
| **LoRA** | `r=16, alpha=32`，全线性层适配，约 1.5M 参数 |
| **DPO** | 用 TRL `DPOTrainer`，`beta=0.1` |
| **输出** | LoRA 权重 + tokenizer 保存到 `model/Qwen3-0.6B_*_lora/` |

### 手工用 HuggingFace 加载

如果想手动处理数据：

```python
from datasets import load_dataset

ds = load_dataset("json", data_files="~/.athlon-agent/training-data/sft-traces-*.jsonl")
print(f"Loaded {len(ds['train'])} samples")
print(ds['train'][0]['messages'][0]['role'])  # "system"

# 筛选有推理链的样本做 CoT 蒸馏
cot = ds["train"].filter(lambda x: x["metadata"]["hasReasoning"])
```

DPO 文件可直接被 TRL 的 `DPOTrainer` 消费（`prompt` / `chosen` / `rejected` 字段自动识别）：

```python
from datasets import load_dataset
from trl import DPOTrainer

ds = load_dataset("json", data_files="~/.athlon-agent/training-data/dpo-preference-*.jsonl")
trainer = DPOTrainer(
    model=model,
    train_dataset=ds["train"],
)
trainer.train()
```

### 数据筛选建议

- **SFT**: 优先选 `hasCorrection=true` 且有 `reasoning` 的样本（高信息密度）
- **DPO**: 优先选 `score` 差异大的偏好对（≥0.3 分差）
- **GRPO**: 使用 `score` 字段作为 reward 信号（优先选 score > 0.7 的样本）
- **Contrastive**: `failedToolCallIds` 标记了修正前的错误调用，可用于构建对比样本

## 训练前后能力对比

项目自带的 `tools/eval-agent-benchmark.py` 可在 30+ 个典型 Agent 场景上评估模型训练前后的能力差异：

```bash
# 对比 base 和 sft_lora（推荐）
python tools/eval-agent-benchmark.py

# 对比两条不同的 LoRA
python tools/eval-agent-benchmark.py --lora-a ./model/Qwen3-0.6B_sft_lora --lora-b ./model/Qwen3-0.6B_dpo_lora

# 首次采集 baseline（仅 base）
python tools/eval-agent-benchmark.py --base-only

# 保存详细结果到 JSON
python tools/eval-agent-benchmark.py --output eval-results.json
```

### 评估维度

| 类别 | 题数 | 测评点 | 示例 |
|------|------|--------|------|
| **工具选型** | 7 | 面对任务能否选对工具 | "帮我看看当前目录" → 应选 `file_list` |
| **格式合规** | 5 | tool_calls 格式、JSON 合法性、有 reasoning | 输出必须是合法 JSON |
| **纠错恢复** | 6 | 用户给出修正后是否正确重试 | "用 src/ 路径" → 第二调用路径含 src/ |
| **溢出恢复** | 6 | 说"继续"后是否续接而非从头开始 | 输出不含"首先/第一步" |
| **指令遵从** | 6 | 是否遵守用户明确的限制条件 | "别用 grep" → 输出不含 grep |

### 输出示例

```
                                  类别             原始 Qwen3-0.6B     LoRA (训练后)
  ────────────────
  工具选型                           3/7 (43%)         6/7 (86%)
  格式合规                           4/5 (80%)         5/5 (100%)
  纠错恢复                           2/6 (33%)         5/6 (83%)
  溢出恢复                           3/6 (50%)         5/6 (83%)
  指令遵从                           4/6 (67%)         6/6 (100%)
  ────────────────
  总分                               16/30 (53%)       27/30 (90%)
```

通过对比可以量化训练收益，并发现模型的薄弱环节。例如如果"溢出恢复"分数低，说明需要采集更多溢出恢复样本。<｜end▁of▁thinking｜>

<｜｜DSML｜｜parameter name="new_text" string="true">### 数据筛选建议

- **SFT**: 优先选 `hasCorrection=true` 且有 `reasoning` 的样本（高信息密度）
- **DPO**: 优先选 `score` 差异大的偏好对（≥0.3 分差）
- **GRPO**: 使用 `score` 字段作为 reward 信号（优先选 score > 0.7 的样本）
- **Contrastive**: `failedToolCallIds` 标记了修正前的错误调用，可用于构建对比样本

## 训练前后能力对比

项目自带的 `tools/eval-agent-benchmark.py` 可在 30+ 个典型 Agent 场景上评估模型训练前后的能力差异：

```bash
# 对比 base 和 sft_lora（推荐）
python tools/eval-agent-benchmark.py

# 对比两条不同的 LoRA
python tools/eval-agent-benchmark.py --lora-a ./model/Qwen3-0.6B_sft_lora --lora-b ./model/Qwen3-0.6B_dpo_lora

# 首次采集 baseline（仅 base）
python tools/eval-agent-benchmark.py --base-only

# 保存详细结果到 JSON
python tools/eval-agent-benchmark.py --output eval-results.json
```

### 评估维度

| 类别 | 题数 | 测评点 | 示例 |
|------|------|--------|------|
| **工具选型** | 7 | 面对任务能否选对工具 | "帮我看看当前目录" → 应选 `file_list` |
| **格式合规** | 5 | tool_calls 格式、JSON 合法性、有 reasoning | 输出必须是合法 JSON |
| **纠错恢复** | 6 | 用户给出修正后是否正确重试 | "用 src/ 路径" → 第二调用路径含 src/ |
| **溢出恢复** | 6 | 说"继续"后是否续接而非从头开始 | 输出不含"首先/第一步" |
| **指令遵从** | 6 | 是否遵守用户明确的限制条件 | "别用 grep" → 输出不含 grep |

### 输出示例

```
                                  类别             原始 Qwen3-0.6B     LoRA (训练后)
  ────────────────
  工具选型                           3/7 (43%)         6/7 (86%)
  格式合规                           4/5 (80%)         5/5 (100%)
  纠错恢复                           2/6 (33%)         5/6 (83%)
  溢出恢复                           3/6 (50%)         5/6 (83%)
  指令遵从                           4/6 (67%)         6/6 (100%)
  ────────────────
  总分                               16/30 (53%)       27/30 (90%)
```

通过对比可以量化训练收益，并发现模型的薄弱环节。例如如果"溢出恢复"分数低，说明需要采集更多溢出恢复样本。

## 数据量估算

| 使用频率 | 日均对话 | 修正比例 | 日均样本 | 月均样本 |
|---------|---------|---------|---------|---------|
| 低频 | 10 | ~15% | ~1.5 | ~45 |
| 中频 | 50 | ~15% | ~7.5 | ~225 |
| 高频 | 200 | ~15% | ~30 | ~900 |

## 验证

```bash
python tools/validate-training-data.py
```

## 脱敏提醒

训练数据中包含用户的原始输入和修正指令。建议在训练前用脚本对数据做脱敏处理：

```python
import re

def sanitize(text: str) -> str:
    # 替换邮箱
    text = re.sub(r'[\w.+-]+@[\w-]+\.[\w.-]+', '[EMAIL]', text)
    # 替换路径中的用户名
    text = re.sub(r'C:\\Users\\[^\\]+', 'C:\\Users\\[USER]', text)
    # 替换 IP
    text = re.sub(r'\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}', '[IP]', text)
    return text
```
