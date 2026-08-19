# Runtime Diagnostic Failure Matrix (P0/P1)

This matrix defines frozen P0/P1 failure points that must emit structured diagnostics into `runtime-events.jsonl`.

## Fields
- `failurePointId`: stable identifier for coverage tests and review.
- `component`: runtime component name.
- `phase`: lifecycle phase.
- `errorCode`: canonical value from `RuntimeDiagnosticErrorCodes`.
- `severity`: expected baseline severity.

## Current Matrix

| failurePointId | component | phase | errorCode | severity |
|---|---|---|---|---|
| model.request.failed | Model | Request | model.request_failed | Error |
| model.response.json_invalid | Model | Response | model.response_json_invalid | Error |
| model.tool_call.json_invalid | Model | Response | model.tool_call_json_invalid | Error |
| model.context_length.exceeded | Model | Request | model.context_length_exceeded | Error |
| model.streaming.idle_timeout | Model | Response | model.streaming_idle_timeout | Error |
| model.streaming.first_token_timeout | Model | Response | model.streaming_first_token_timeout | Error |
| model.streaming.interrupted | Model | Response | model.streaming_interrupted | Warning |
| tool.invoke.failed | Tool | Invoke | tool.invoke_failed | Error |
| tool.output.evicted | Tool | Persist | tool.output_evicted | Warning |
| storage.persist.failed | Storage | Persist | storage.persist_failed | Error |
| storage.load.failed | Storage | Load | storage.load_failed | Error |
| compaction.summary.failed | Compaction | Persist | compaction.summary_failed | Error |
| compaction.retry.skipped | Compaction | Persist | compaction.retry_skipped | Warning |
| ui.webview.init_failed | UiWebview | Initialize | ui.webview_init_failed | Error |
| ui.webview.script_failed | UiWebview | Invoke | ui.webview_script_failed | Error |
| ui.session_switch.surface_mismatch | UiSessionSwitch | Switch | ui.session_switch_surface_mismatch | Warning |
| mcp.connect.failed | Mcp | Request | mcp.connect_failed | Error |
| mcp.list_tools.failed | Mcp | Request | mcp.list_tools_failed | Error |
| mcp.tool.invoke.failed | Mcp | Invoke | mcp.tool_invoke_failed | Error |
| ssh.connect.failed | Ssh | Request | ssh.connect_failed | Error |
| subagent.run.failed | Subagent | Invoke | subagent.run_failed | Error |
| behavior.upload.failed | Behavior | Persist | behavior.upload_failed | Warning |

## Governance
- Every P0/P1 failure branch must map to one `errorCode` in this matrix.
- Any new P0/P1 failure point requires:
  1. matrix update,
  2. instrumentation update,
  3. coverage test update.
- Legacy plain-text fallback logs are not considered coverage.

