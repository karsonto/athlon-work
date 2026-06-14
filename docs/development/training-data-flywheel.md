# Training Data Flywheel — Developer Guide

> How Athlon Agent auto-extracts SFT/DPO training data from real agent–user interactions,
> and how to extend it when adding new agent capabilities.

## Overview

Every time a user interacts with the agent, the **CorrectionDetector** scans the conversation
for meaningful patterns — tool failures followed by user corrections, timeouts recovered by
"continue" commands, and successful tool selections. These patterns are extracted as
HuggingFace-compatible JSONL training samples, building a self-sustaining data flywheel.

```
  User asks → Agent calls tool → Tool fails → User corrects → Agent retries → Success
                                                                                   ↓
                                                                          CorrectionDetector
                                                                                   ↓
                                                         ┌──────────────────────────────┐
                                                         │  sft-traces-2026-06-14.jsonl  │
                                                         │  dpo-preference-2026-06-14.jsonl │
                                                         └──────────────────────────────┘
```

## Architecture

### Data Pipeline

```
AgentSession.Messages
         ↓
CorrectionDetector.Detect()              ← trajectory detection (message-only, no tool logs)
CorrectionDetector.DetectOverflowRecoveries()
         ↓
TurnTrajectoryExtractor.Extract*Samples() ← slice messages, build TrainingSample objects
         ↓
TrainingSampleStore.RecordTurnAsync()     ← write to JSONL files
```

### Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `CorrectionDetector` | `TrainingData/CorrectionDetector.cs` | Pure detection logic — scans `IReadOnlyList<ChatMessage>` for correction/overflow patterns |
| `CorrectionTrajectory` | `TrainingData/CorrectionDetector.cs` | Record: `FailedToolCall`, `CorrectionMessage`, `SuccessfulToolCall` |
| `OverflowRecoveryTrajectory` | `TrainingData/CorrectionDetector.cs` | Record: `OverflowNotice`, `ContinuationMessage`, `RecoveryAssistantMessage` |
| `TurnTrajectoryExtractor` | `TrainingData/TurnTrajectoryExtractor.cs` | Converts trajectories to `TrainingSample`/`DpoPreferenceSample` with metadata |
| `TrainingSampleStore` | `TrainingData/TrainingSampleStore.cs` | `ITrainingDataCollector` implementation — writes JSONL files, manages locks |
| `TrainingSample` / `DpoPreferenceSample` | `TrainingData/TrainingSample.cs` | Data models matching HuggingFace/TRL formats |

### File Outputs

```
~/.athlon-agent/training-data/
├── sft-traces-2026-06-14.jsonl      # messages[] format (SFT + CoT + overflow recovery)
└── dpo-preference-2026-06-14.jsonl  # prompt/chosen/rejected format (DPO)
```

## Detection Logic

### 1. Correction Trajectory (tool failure → user fix → success)

```csharp
// CorrectionDetector.FindFailedToolCalls()
//   assistant message with tool_calls
//     → next tool result with "Error:" prefix → FAILURE

// CorrectionDetector.FindNextUserMessage()
//   after failure → find next user message → CORRECTION

// CorrectionDetector.FindSuccessfulRetry()
//   after correction → find same-named tool call without error → SUCCESS
```

**Match conditions:**
- Tool result starts with `"Error:"` (case-insensitive)
- User correction message appears after failure
- Same tool name is called again after correction, with non-error result

### 2. Overflow Recovery (timeout → "continue" → success)

```csharp
// CorrectionDetector.IsOverflowNotice()
//   System message contains timeout/overflow markers

// CorrectionDetector.IsContinuationCommand()
//   User message matches "继续", "continue", "go on", etc.
```

**System message markers** (Chinese + English):
- `"超过配置的超时时间"`
- `"已自动停止"` / `"生成已停止"`
- `"模型调用失败："` / `"Error:"`
- `"timeout"` / `"超时"` / `"overflow"`

**Continuation commands:**
- Chinese: `继续`, `接着`, `继续说`, `继续分析`, `接着分析`, `接着做`, `继续做`
- English: `continue`, `go on`, `keep going`

### 3. DPO Preference Pairs (from correction trajectories)

Each correction trajectory produces one DPO pair:

| Section | Content |
|---------|---------|
| `prompt` | Messages from turn start to just before the failed assistant message |
| `rejected` | Failed assistant message + error tool result |
| `chosen` | Corrected assistant message + success tool result |

## Integration Points for AI Developers

### A. Adding a New Trajectory Type

1. **Define the record** in `CorrectionDetector.cs`:
   ```csharp
   public sealed record MyNewTrajectory(
       ChatMessage TriggerMessage,
       int TriggerIndex,
       ChatMessage ResponseMessage);
   ```

2. **Add detection method** in `CorrectionDetector.cs`:
   ```csharp
   public static IReadOnlyList<MyNewTrajectory> DetectMyPattern(
       IReadOnlyList<ChatMessage> messages)
   {
       // Scan messages for the new pattern
       // Return list of trajectories
   }
   ```

3. **Add extraction method** in `TurnTrajectoryExtractor.cs`:
   ```csharp
   public static IReadOnlyList<TrainingSample> ExtractMyPatternSamples(
       AgentSession session)
   {
       var trajectories = CorrectionDetector.DetectMyPattern(session.Messages);
       // Convert to TrainingSample list with appropriate metadata
   }
   ```

4. **Wire into store** in `TrainingSampleStore.RecordTurnAsync()`:
   ```csharp
   var mySamples = TurnTrajectoryExtractor.ExtractMyPatternSamples(session);
   foreach (var sample in mySamples)
       await WriteSampleAsync(sample, cancellationToken);
   ```

### B. Adding a New Data Source

If you want to capture data from a new interaction point (e.g., tool selection, MCP calls,
skill execution):

1. **Create a new detector** or extend `CorrectionDetector` with the detection logic
2. **Create a new extractor method** in `TurnTrajectoryExtractor`
3. **Add a new metadata source tag** (e.g., `"mcp-tool-selection"`)
4. **Register** in `TrainingSampleStore`

### C. Collection Triggers

Training data is collected at two points:

| Method | When | What |
|--------|------|------|
| `RecordTurnAsync()` | After each agent turn completes | Correction trajectories, overflow recoveries, DPO pairs |
| `RecordSessionAsync()` | After session ends | Full session dump (configurable sample rate) |

### D. Configuration

```json
{
  "trainingData": {
    "enabled": false,     // Off by default — zero overhead when disabled
    "outputDirectory": "", // Empty → ~/.athlon-agent/training-data/
    "sampleRate": 1.0     // 0.1 = sample 10% of sessions
  }
}
```

## Data Models (Serialization)

### SFT Sample (in `sft-traces-*.jsonl`)

```json
{
  "messages": [
    {"role": "system", "content": "You are Athlon Agent..."},
    {"role": "user", "content": "帮我看看项目结构"},
    {"role": "assistant", "content": null, "reasoning": "...",
     "tool_calls": [{"id": "call_1", "type": "function",
       "function": {"name": "file_list", "arguments": "{\"path\":\".\"}"}}]},
    {"role": "tool", "content": "Error: ...", "tool_call_id": "call_1"},
    {"role": "user", "content": "用 src/ 路径"},
    {"role": "assistant", "tool_calls": [...]}
  ],
  "metadata": {
    "source": "agent-correction",
    "hasCorrection": true,
    "hasReasoning": true,
    "score": 0.85,
    "model": "qwen3-32b"
  }
}
```

### DPO Sample (in `dpo-preference-*.jsonl`)

```json
{
  "prompt": [{"role": "system", ...}, {"role": "user", ...}],
  "chosen": [{"role": "assistant", "tool_calls": [...]}],
  "rejected": [{"role": "assistant", "tool_calls": [...]}],
  "metadata": {
    "source": "agent-correction-dpo",
    "failedToolName": "grep_files",
    "successToolName": "file_list",
    "hasReasoning": false
  }
}
```

## Validation

```bash
python tools/validate-training-data.py
```

Validates both SFT and DPO formats:
- Required fields per format
- Role values (`system`, `user`, `assistant`, `tool`)
- Tool call structure (`function.name`, `tool_call_id`)
- Metadata presence and types

## Design Decisions

### Why message-only detection (no tool logs)?

The original design required separate `toolLogs` for failure detection. We eliminated this
dependency so detection works purely from `ChatMessage` sequences:
- Tool failures are identified by `"Error:"` prefix in tool result content
- Overflows are identified by System message markers
- This decouples detection from infrastructure and makes it testable with plain message arrays

### Why separate SFT and DPO files?

| Format | Consumer | Purpose |
|--------|----------|---------|
| `sft-traces-*.jsonl` | HuggingFace `load_dataset` | Supervised fine-tuning, CoT distillation |
| `dpo-preference-*.jsonl` | TRL `DPOTrainer` | Preference optimization for tool selection |

### Score computation

`TurnTrajectoryExtractor.ComputeQualityScore()`:
- Base: `0.5`
- +0.15 if correction message > 20 chars
- +0.10 if correction message > 100 chars
- +0.10 if failed tool call has arguments
- +0.10 if failed tool call has > 2 arguments
- +0.15 if failure message has reasoning content
- Capped at `1.0`

## Testing

- **Unit tests** should test `CorrectionDetector` with synthetic `ChatMessage` arrays
- **Integration tests** should verify `TrainingSampleStore` writes correct JSONL
- **Validation script** (`tools/validate-training-data.py`) acts as integration test

## Related Documents

- [Training Data Guide (detailed)](../superpowers/plans/training-data-guide.md) — usage, training suggestions, data estimates
- [Theme & UI Conventions](theme-and-ui-conventions.md) — UI conventions for contributors

## Training

Use `tools/train-athlon-agent.py` to fine-tune Qwen3-0.6B with the collected data:

```bash
# Install dependencies
pip install torch transformers datasets peft trl accelerate

# SFT: correction trajectories + overflow recoveries
python tools/train-athlon-agent.py --mode sft --epochs 5

# DPO: tool selection preference pairs
python tools/train-athlon-agent.py --mode dpo --epochs 3

# Full pipeline: SFT → DPO
python tools/train-athlon-agent.py --mode all

# Quick test on the trained model
python tools/train-athlon-agent.py --mode test
```

The script auto-loads all `sft-traces-*.jsonl` / `dpo-preference-*.jsonl` files from
`~/.athlon-agent/training-data/`, applies Qwen3 chat template (`<|im_start|>...`),
and writes LoRA weights to `tools/../model/Qwen3-0.6B_*_lora/`.

## Evaluation

Use `tools/eval-agent-benchmark.py` to compare model performance before and after training:

```bash
# Compare base model vs latest SFT LoRA (recommended)
python tools/eval-agent-benchmark.py

# Compare two LoRA checkpoints
python tools/eval-agent-benchmark.py \
    --lora-a ./model/Qwen3-0.6B_sft_lora \
    --lora-b ./model/Qwen3-0.6B_dpo_lora

# First-time baseline (base model only)
python tools/eval-agent-benchmark.py --base-only

# Save detailed JSON results
python tools/eval-agent-benchmark.py --output eval-results.json
```

The evaluation measures **30+ Agent-typical scenarios** across 5 dimensions:

| Category | Tests | Example |
|----------|-------|---------|
| **Tool Selection** | 7 | Picks `file_list` when asked "what's in this directory?" |
| **Format Compliance** | 5 | Tool calls are valid JSON, reasoning is present |
| **Error Recovery** | 6 | After "用 src/ 路径", retries with corrected path |
| **Overflow Recovery** | 6 | After "继续", resumes without restarting from scratch |
| **Instruction Following** | 6 | Respects explicit constraints (e.g. "别用 grep") |

Output format:

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

