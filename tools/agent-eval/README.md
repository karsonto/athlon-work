# Agent 离线链路评估

读取 `~/.athlon-agent/sessions/**/session.json`，把每个 turn 还原成标准化 trace，先跑零成本的确定性规则，再用 LLM-as-judge 判定语义指标，最后输出可对比基线的报告。

**不重跑 Agent、不改 C# 代码、不产生新的会话数据。** 评的是「链路与策略」，不是模型权重（那属于 `tools/eval-agent-benchmark.py`）。

## 为什么不用 JEV 的 API

`agentscope-java` 的 JEV 依赖 TypeSafe 账号与校准过的概率模型。本仓库已有 OpenAI 兼容端点，所以语义判定改为**离散 verdict**：`yes` / `no` / `unsure`。让普通 chat 模型编一个概率再套阈值，等于制造虚假精度；而 `unsure` 出口保留了「证据不足就不下结论」的能力。

指标定义、`MetricResult` 结构、`DECIDED / INCONCLUSIVE / SKIPPED / ERROR` 四态仍与 `JevTraceMetric` / `JevMetricResult` 对齐，将来接 JEV 只需新增一个 backend，`reporter` / `compare` 不用改。

## 快速开始

```bash
cd /path/to/athlon-work

# 1) 先看解析结果，不落盘
python3 tools/agent-eval/eval.py collect --dry-run --preview 3

# 2) 收集 trace
python3 tools/agent-eval/eval.py collect --out reports/eval-before

# 3) 评估（需要密钥，见下）
export ATHLON_EVAL_API_KEY=sk-...
python3 tools/agent-eval/eval.py evaluate --out reports/eval-before

# 4) 改完 Agent 后再跑一次，然后对比
python3 tools/agent-eval/eval.py collect --out reports/eval-after
python3 tools/agent-eval/eval.py evaluate --out reports/eval-after
python3 tools/agent-eval/eval.py compare \
  --baseline reports/eval-before/results.json \
  --current  reports/eval-after/results.json
```

`compare` 在 gate 指标退化超过阈值时返回 **exit code 2**，可直接用作 CI 门禁。

只用规则、不花钱：

```bash
python3 tools/agent-eval/eval.py evaluate --out reports/rules-only --rules-only
```

离线回放（CI 用，不依赖模型）：

```bash
python3 tools/agent-eval/eval.py evaluate --out /tmp/replay \
  --offline tools/agent-eval/tests/fixtures/judge_replies
```

想固定一次真实判定结果供以后对比，加 `--record <dir>`，之后用 `--offline <dir>` 回放。

## 配置

```bash
cp tools/agent-eval/config.example.json tools/agent-eval/config.json
```

`config.json` 不存在时使用内置默认值；`config.example.json` 只是模板。

| 字段 | 作用 |
|---|---|
| `sessionsRoot` | 会话根目录，默认 `~/.athlon-agent/sessions` |
| `includeSubAgents` | 是否包含 `sessions/<父>/subagents/<类型>/<子>/session.json` |
| `minRequestChars` | 过短的用户请求（如「继续」）不成 trace，避免污染统计 |
| `excludeSessionTitleContains` | 按标题过滤 smoke/test 类会话 |
| `maxToolResultChars` | trace 中单条工具结果保留的字符数 |
| `judge.endpoint` / `judge.model` | 为 `null` 时读 `~/.athlon-agent/config/settings.json` 的 `model.endpoint` / `model.modelName` |
| `judge.apiKeyEnv` | 密钥环境变量名，默认 `ATHLON_EVAL_API_KEY` |
| `judge.timeoutSeconds` | **整条 trace** 的预算（含重试），不是单次请求超时 |
| `judge.maxStateChars` | 送给 judge 的 state 上限，超出先丢 transcript、再裁剪工具输出 |
| `gate[]` | `metric` / `maxDropRate` / `minDenominator` |

密钥不读 DPAPI 存储的凭据：Python 侧绕过去等于破坏安全边界，所以必须显式提供 `ATHLON_EVAL_API_KEY` 或 `--api-key`。

## 判定口径（重要）

| 模型判定 | `status` | `passed` | 计入通过率分母 |
|---|---|---|---|
| `yes` | `DECIDED` | `true` | 是 |
| `no` | `DECIDED` | `false` | 是 |
| `unsure` | `INCONCLUSIVE` | `null` | **否** |
| 调用/解析失败 | `ERROR` | `null` | 否 |
| precheck 不满足、或 trace 结构不完整 | `SKIPPED` | `null` | 否 |

通过率 = `PASS / (PASS + FAIL)`。**`INCONCLUSIVE` 与 `ERROR` 既不并入分母，也不转成 FAIL。** 这是 JEV「不能把没有有效判断统一解释为允许」在无概率后端上的等价表达。

judge 回复的强校验：键集合必须与问题集合完全一致、verdict 必须在枚举内、reason 非空。任何一项不合规 → 该 trace 的语义指标全部 `ERROR`，绝不猜测或补默认值。`score` 恒为 `null`，因为 chat 模型给不出可校准的分数。

## 指标

### 确定性规则（零成本，先跑）

| 指标 | 判定 |
|---|---|
| `tool_args_valid` | 所有 tool call 的参数都能解析成 JSON object |
| `tool_call_paired` | 每个调用都有结果 |
| `no_repeat_tool_call` | turn 内没有相同 `(tool, args)` 重复（少于 2 次调用时 `SKIPPED`，避免虚高通过率） |
| `first_tool_success` | 首次调用成功 |
| `no_raw_json_leak` | 带 tool call 的 assistant 文本不夹带裸 JSON |
| `no_empty_final_answer` | 有用户可见回答 |
| `compaction_in_turn` | **信号**，不是通过/失败：`CLEAN` / `COMPACTED`，不进分母 |

规则**在结构不完整的 trace 上也会跑**，所以被中断的 turn 仍会以 FAIL 出现在报告里，不会凭空消失。

### 语义指标（LLM judge）

`tool_choice`、`used_tool_result`、`grounded`、`stayed_in_scope`、`answer_relevancy`、`completeness`、`indirect_injection`（负向）。

每个指标是「一个是/否问题 + 期望方向」，同一条 trace 的全部问题**打包成一次请求**。trace 存在阻塞性问题（见下）时，语义指标一律 `SKIPPED`，不浪费调用。

## Trace 与解析

按 `User` 消息切 turn，最后一个 `User` 到下一个 `User` 之前为一条 trace。

阻塞性问题（`issues`）：

| issue | 含义 |
|---|---|
| `AMBIGUOUS_CALL_ID` | 调用 id 为空或重复 |
| `UNPAIRED_TOOL_RESULT` | 结果找不到对应调用、或工具名不一致 |
| `PENDING_TOOL_CALL` | turn 结束时仍有调用没有结果（被中断） |
| `EMPTY_FINAL_ANSWER` | 没有用户可见回答（轮次耗尽 / 纯工具轮） |

标记（`flags`，不阻断评估）：`TRUNCATED_TOOL_OUTPUT`（工具输出被 evict 或 grep 截断）、`COMPACTED`（本轮发生上下文压缩）。

`collect` 同时产出 `collect-report.json`：会话数 / turn 数 / 各 issue 与 flag 计数 / 被过滤原因。**先跑 `--dry-run` 人工抽查几条 trace，再做语义评估。**

## 从结果回到代码

| 观察 | 该看的地方 |
|---|---|
| `tool_choice` 低 | `ToolDescriptions.cs`、`Tools/ToolAvailabilityPolicy.cs` |
| `no_repeat_tool_call` 差、`first_tool_success` 低 | `ToolInvocationPolicy.cs`、`Compaction/ToolStormBreaker.cs`、系统提示 |
| `used_tool_result` / `grounded` 低 | `ModelMessageBuilder.FormatToolResult`、`ContextCompaction.ToolResultEviction` |
| `stayed_in_scope` 低、`indirect_injection` 命中 | `Prompt/` 下的系统提示、工具输出信任边界说明 |
| `compaction_in_turn` 高 | `ContextCompactionSettings`、`Compaction/SemanticMessageScorer.cs` |

## 校准与质量控制

1. `--dry-run` 抽查 3 条 trace 与原始会话一致。
2. 真实数据跑 200–500 个 turn，抽 30 条人工核对：`verdict` 与人工判断一致率应 ≥ 85%。
3. `unsure` 比例 > 20% 说明问题措辞或 state 裁剪需要调整（不是模型不行，是证据不够）。
4. judge 与应用用同一模型会放大同类偏好，`judge.model` 应单独配一个更强的模型。
5. 不要用单条 trace 下结论，看指标集合的分布变化。

## 已知限制

- **无 token / latency 指标**：`session.json` 不含用量，`runtime-events.jsonl` 也没有 token 字段。judge 侧 token 有统计，Agent 侧没有。
- **压缩不可逆**：会话只保留压缩后的消息，被压掉的历史原文拿不回来；`conversation.jsonl` 只能部分缓解。
- **工具 schema 不可得**：会话不存可用工具清单，所以 `tool_choice` 的提问要求「仅依据实际调用判断」，并允许 `unsure`。

## 目录

```
tools/agent-eval/
├── eval.py            # CLI：collect / evaluate / report / compare
├── trace_model.py     # Trace / ToolCall / ToolResult / MetricResult / Status / Verdict
├── trace_builder.py   # session.json -> Trace（切 turn、配 call-result、issue 检测）
├── metrics_rules.py   # 确定性指标
├── metrics_llm.py     # 语义指标（precheck / questions / reduce）
├── judge.py           # LLM 后端：打包、强校验、重试、预算、离线回放、录制
├── runner.py          # 编排：规则先跑、按 trace 隔离失败、汇总 usage
├── reporter.py        # summary.md / failures.md / 对比与 gate
├── config.example.json
└── tests/             # fixtures + 单测
```

测试：

```bash
python3 -m pytest tools/agent-eval/tests -q
```

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 正常 |
| 1 | 参数 / IO / judge 未配置 |
| 2 | `compare` 的 gate 指标退化超阈值 |
