# Behavior Report 事件采集清单

已实现：`BehaviorEventManager` 单例采集 → 本地 `~/.athlon-agent/behavior/pending.jsonl` → 每 N 分钟批量 `POST {BaseUrl}/agent/report`。

配置：`AppSettings.BehaviorReport`（默认 `enabled: false`）。

---

## 上报体（设备字段 + events 数组）

一次请求上送当前 pending 中的全部事件（成功则整批删除，失败整批保留重试）：

| 字段 | 说明 |
|------|------|
| `user_id` | SSO 用户 ID |
| `client_ip` | 客户端本机 IPv4 |
| `mac_address` | 网卡 MAC |
| `os_version` | 操作系统版本 |
| `app_name` | Athlon Agent |
| `app_version` | 应用版本号 |
| `screen_resolution` | 主屏分辨率 |
| `events` | 事件数组 |

每个 `events[]` 元素：

| 字段 | 说明 |
|------|------|
| `event_type` | 业务事件 ID（如 `user_login`、`mcp_tool`） |
| `event_params` | 业务参数；另注入 `event_kind`（`action` / `event`） |
| `message_content` | 描述（多数等于 event_id） |
| `event_time` | 中国标准时间 UTC+8：`yyyy-MM-dd HH:mm:ss.fff` |

---

## 已实现 event_id（18）

| # | 分类 | event_id | type | event_params 要点 | 挂钩位置 |
|---|------|----------|------|-------------------|----------|
| 1 | 身份与应用 | `app_start` | event | `sso_skipped` | `App.OnStartup` |
| 2 | 身份与应用 | `app_shutdown` | event | `reason`, `uptime_ms` | `ApplicationShutdownService` |
| 3 | 身份与应用 | `user_login` | event | `logged_in_at`, `expires_at`, `source`(new/cached) | `ImpSsoStartupGate` |
| 4 | 身份与应用 | `user_login_failed` | event | `status` | `ImpSsoStartupGate` |
| 5 | 身份与应用 | `user_session` | event | `action`(expired/logout), `reason?`, `expires_at?` | Gate 过期 / `NavigationCoordinator.ClearSsoSession` |
| 6 | 身份与应用 | `app_update_check` | event | `has_update`, `version` | `StartupUpdateGate` / `AppUpdateService` |
| 7 | 大模型 | `model_call` | action | `purpose`(Chat/Summary/Memory/SubAgent/Embedding), tokens, `latency_ms`, `result`, `session_id`… | `AppendAttemptEvent` / Embedding Client |
| 8 | 大模型 | `model_usage_summary` | event | `window_minutes`, 各 purpose 的 calls/tokens | BehaviorEventManager 上送周期内聚合 |
| 9 | MCP | `mcp_tool` | action | `server_name`/`tool_name`/`gateway`, `mode`(direct/search), `success`, `latency_ms` | Attempt 分流（MCP 工具名） |
| 10 | MCP | `mcp_server` | event | `server_name`, `action`(connected/disconnected/enabled/disabled), `tool_count?` | `McpRegistry` / `McpServerItemViewModel` |
| 11 | Skill | `skill_load` | action | `skill_id`, `path`, `success` | `LoadSkillThroughPathTool` |
| 12 | Skill | `skill_toggle` | event | `skill_id`, `enabled` | `SkillItemViewModel` |
| 13 | 本地工具 | `tool_invoke` | action | `tool_name`, `success`, `latency_ms`, `session_id` | Attempt 分流（非 MCP/非 skill） |
| 14 | 本地工具 | `tool_approval` | event/action | `tool_name`, `action`(requested/granted/denied) | `ToolInvocationPipeline` |
| 15 | Session | `user_message_sent` | action | `session_id`, `has_image`, `message_length` | `SessionTurnHost.TryStart`（排除 auto-continue） |
| 16 | Session | `turn` | event | `session_id`, `run_id`, `outcome`(started/completed/cancelled/failed/max_tool_rounds) | `SessionTurnHost` / `AgentRuntime` |
| 17 | 上下文 | `context` | event | `action`(compaction/overflow_retry/hygiene), tokens 相关字段 | Compactor / TurnCoordinator |
| 18 | SubAgent | `subagent` | event/action | `action`(started/completed/failed/auto_continue), `role`, session ids… | `SubAgentRunExecutor` / ContinuationService |

---

## Attempt 分流规则

写入 `attempts.jsonl` 后旁路到 BehaviorEventManager：

| AgentAttempt 条件 | event_id |
|-------------------|----------|
| `Kind == Model` | `model_call` |
| Tool 且 MCP 编码名 / gateway（`mcp_search` 等） | `mcp_tool` |
| Tool 且 `load_skill_through_path` | 跳过（由 `skill_load` 单独记） |
| 其他 Tool | `tool_invoke` |

---

## 本地文件

| 路径 | 说明 |
|------|------|
| `~/.athlon-agent/behavior/pending.jsonl` | 待上报事件（JSONL） |

批量上报成功后删除全部已上送行；失败保留下次重试。关机时 `FlushAsync`（最多约 5 秒）。
