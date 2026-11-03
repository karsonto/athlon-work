"""Rebuild ``Trace`` objects from persisted Athlon Agent sessions.

Input is read-only: ``~/.athlon-agent/sessions/<id>/session.json``. Nothing here
re-runs an agent or talks to a model, so the resulting traces reflect exactly
what the product did at the time.

Message shapes are produced by ``JsonFileStoreOptions.WebIndented``
(camelCase, string enums), ``ChatMessage`` and ``AssistantToolCallsCodec``.
The parser stays tolerant: unknown fields are ignored and legacy shapes are
accepted rather than silently dropping a whole session.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from trace_model import (
    FLAG_COMPACTED,
    FLAG_TRUNCATED_TOOL_OUTPUT,
    CallRecord,
    Issue,
    ToolCall,
    ToolResult,
    Trace,
)

SESSION_FILE = "session.json"
SUBAGENT_FOLDER = "subagents"

# MessageRole enum as persisted by System.Text.Json + JsonStringEnumConverter.
_ROLE_ALIASES = {
    "system": "System",
    "user": "User",
    "assistant": "Assistant",
    "tool": "Tool",
    "summary": "Summary",
    "compaction": "Compaction",
}
_ROLE_BY_ORDINAL = ("System", "User", "Assistant", "Tool", "Summary", "Compaction")

_TOOL_CALL_ID_PREFIX = "toolcallid:"
_TOOL_STATUS_RE = re.compile(r"^Tool `(?P<name>[^`]+)` (?P<status>succeeded|failed)\.", re.IGNORECASE)
_SUMMARY_PREFIX = "summary:"
_ARGUMENTS_PREFIX = "arguments:"

# Placeholders written by ToolResultEvictor.
_EVICTED_MARKERS = ("[Tool result evicted", "[grep output truncated:")


@dataclass
class CollectConfig:
    sessions_root: Path
    include_sub_agents: bool = True
    min_request_chars: int = 8
    exclude_title_contains: Sequence[str] = field(default_factory=tuple)
    max_tool_result_chars: int = 8000
    limit_sessions: int | None = None

    @staticmethod
    def from_mapping(payload: Mapping[str, Any]) -> "CollectConfig":
        raw_root = str(payload.get("sessionsRoot") or "~/.athlon-agent/sessions")
        return CollectConfig(
            sessions_root=Path(os.path.expanduser(raw_root)),
            include_sub_agents=bool(payload.get("includeSubAgents", True)),
            min_request_chars=int(payload.get("minRequestChars", 8) or 0),
            exclude_title_contains=tuple(
                item.lower() for item in payload.get("excludeSessionTitleContains") or ()
            ),
            max_tool_result_chars=int(payload.get("maxToolResultChars", 8000) or 8000),
            limit_sessions=payload.get("limitSessions"),
        )


@dataclass
class CollectReport:
    sessions_seen: int = 0
    sessions_parsed: int = 0
    sessions_skipped: int = 0
    traces: int = 0
    traces_skipped: int = 0
    issue_counts: dict[str, int] = field(default_factory=dict)
    flag_counts: dict[str, int] = field(default_factory=dict)
    skip_reasons: dict[str, int] = field(default_factory=dict)
    errors: list[str] = field(default_factory=list)

    def note_issue(self, value: Issue) -> None:
        self.issue_counts[value.value] = self.issue_counts.get(value.value, 0) + 1

    def note_flag(self, value: str) -> None:
        self.flag_counts[value] = self.flag_counts.get(value, 0) + 1

    def note_skip(self, reason: str) -> None:
        self.skip_reasons[reason] = self.skip_reasons.get(reason, 0) + 1

    def to_dict(self) -> dict[str, Any]:
        return {
            "sessionsSeen": self.sessions_seen,
            "sessionsParsed": self.sessions_parsed,
            "sessionsSkipped": self.sessions_skipped,
            "traces": self.traces,
            "tracesSkipped": self.traces_skipped,
            "issueCounts": dict(sorted(self.issue_counts.items())),
            "flagCounts": dict(sorted(self.flag_counts.items())),
            "skipReasons": dict(sorted(self.skip_reasons.items())),
            "errors": list(self.errors),
        }


# --------------------------------------------------------------------------- #
# Public API
# --------------------------------------------------------------------------- #


def discover_session_files(config: CollectConfig) -> list[Path]:
    root = config.sessions_root
    if not root.is_dir():
        raise FileNotFoundError(f"sessions root not found: {root}")

    files = sorted(root.glob(f"*/{SESSION_FILE}"))
    if config.include_sub_agents:
        files.extend(sorted(root.glob(f"*/{SUBAGENT_FOLDER}/*/*/{SESSION_FILE}")))
    return files


def collect_traces(
    config: CollectConfig,
    report: CollectReport | None = None,
) -> tuple[list[Trace], CollectReport]:
    report = report or CollectReport()
    traces: list[Trace] = []
    files = discover_session_files(config)
    if config.limit_sessions:
        files = files[: int(config.limit_sessions)]

    for path in files:
        report.sessions_seen += 1
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            report.sessions_skipped += 1
            report.errors.append(f"{_relative_session(path, config.sessions_root)}: {error}")
            continue

        if not isinstance(payload, Mapping):
            report.sessions_skipped += 1
            report.errors.append(f"{_relative_session(path, config.sessions_root)}: root is not an object")
            continue

        title = str(payload.get("title") or "")
        if _title_excluded(title, config):
            report.sessions_skipped += 1
            report.note_skip("EXCLUDED_TITLE")
            continue

        session_id = str(payload.get("id") or path.parent.name)
        messages = payload.get("messages")
        if not isinstance(messages, list) or not messages:
            report.sessions_skipped += 1
            report.note_skip("NO_MESSAGES")
            continue

        report.sessions_parsed += 1
        session_traces = build_session_traces(session_id, title, messages, config, report)
        traces.extend(session_traces)

    report.traces = len(traces)
    return traces, report


def build_session_traces(
    session_id: str,
    title: str,
    messages: Sequence[Any],
    config: CollectConfig,
    report: CollectReport | None = None,
) -> list[Trace]:
    report = report or CollectReport()
    traces: list[Trace] = []
    for index, (request, turn_messages) in enumerate(_split_turns(messages)):
        trace = build_trace(
            session_id=session_id,
            title=title,
            turn_index=index,
            request=request,
            messages=turn_messages,
            config=config,
        )
        if trace is None:
            report.traces_skipped += 1
            report.note_skip("EMPTY_REQUEST")
            continue
        for issue in trace.issues:
            report.note_issue(issue)
        for flag in trace.flags:
            report.note_flag(flag)
        traces.append(trace)
    return traces


def build_trace(
    *,
    session_id: str,
    title: str,
    turn_index: int,
    request: str,
    messages: Sequence[Any],
    config: CollectConfig,
) -> Trace | None:
    request_text = _clean_text(request)
    if len(request_text) < config.min_request_chars:
        return None

    issues: list[Issue] = []
    flags: list[str] = []

    turns: list[dict[str, Any]] = []
    calls: dict[str, CallRecord] = {}
    call_order: list[str] = []
    final_answer = ""
    compacted = False

    for raw in messages:
        message = _as_mapping(raw)
        if message is None:
            continue
        role = _parse_role(message.get("role"))
        if role in ("Summary", "Compaction"):
            compacted = True
            continue
        if role is None:
            continue

        text = _clean_text(_as_text(message.get("content")))
        turns.append({"role": role, "text": _clip(text, config.max_tool_result_chars)})

        tool_calls = _parse_tool_calls(message.get("toolCallsJson"))
        if role == "Assistant":
            if tool_calls:
                final_answer = ""
            elif text:
                final_answer = text

        for call in tool_calls:
            if not call.id or call.id in calls:
                _append_issue(issues, Issue.AMBIGUOUS_CALL_ID)
                continue
            calls[call.id] = CallRecord(call=call, result=None, result_message_index=None)
            call_order.append(call.id)

        if role == "Tool":
            _attach_tool_result(
                message=message,
                text=text,
                message_index=len(turns) - 1,
                calls=calls,
                issues=issues,
                flags=flags,
                clip_chars=config.max_tool_result_chars,
            )

    records = [calls[key] for key in call_order]
    if any(record.result is None for record in records):
        _append_issue(issues, Issue.PENDING_TOOL_CALL)
    if not final_answer.strip():
        _append_issue(issues, Issue.EMPTY_FINAL_ANSWER)
    if compacted:
        flags.append(FLAG_COMPACTED)

    tool_results = [
        {
            "id": record.call.id,
            "name": record.call.name,
            "result": record.result.text if record.result else "",
            "result_state": record.result.state if record.result else "unknown",
            "result_summary": record.result.summary if record.result else "",
        }
        for record in records
        if record.result is not None
    ]

    fields: dict[str, Any] = {
        "session_id": session_id,
        "session_title": title,
        "turn_index": turn_index,
        "request": request_text,
        "final_answer": final_answer,
        "messages": turns,
        "tool_calls": [record.to_dict() for record in records],
        "tool_results": tool_results,
    }

    return Trace(
        id=f"{session_id}:{turn_index}",
        fields=fields,
        issues=tuple(dict.fromkeys(issues)),
        flags=tuple(dict.fromkeys(flags)),
    )


# --------------------------------------------------------------------------- #
# Internals
# --------------------------------------------------------------------------- #


def _split_turns(messages: Sequence[Any]) -> Iterable[tuple[str, list[Any]]]:
    """Split a session into turns. A turn starts at every user message."""
    current_request: str | None = None
    current: list[Any] = []
    for raw in messages:
        message = _as_mapping(raw)
        if message is None:
            continue
        role = _parse_role(message.get("role"))
        if role == "User":
            if current_request is not None:
                yield current_request, current
            current_request = _as_text(message.get("content"))
            current = []
            continue
        if current_request is None:
            # Leading system/compaction preamble belongs to the first turn.
            continue
        current.append(message)
    if current_request is not None:
        yield current_request, current


def _attach_tool_result(
    *,
    message: Mapping[str, Any],
    text: str,
    message_index: int,
    calls: dict[str, CallRecord],
    issues: list[Issue],
    flags: list[str],
    clip_chars: int,
) -> None:
    call_id = _extract_tool_call_id(text)
    if not call_id or call_id not in calls:
        _append_issue(issues, Issue.UNPAIRED_TOOL_RESULT)
        return

    record = calls[call_id]
    if record.result is not None:
        _append_issue(issues, Issue.UNPAIRED_TOOL_RESULT)
        return

    name, status, summary, body = _parse_tool_result(text)
    if name and record.call.name and name.casefold() != record.call.name.casefold():
        _append_issue(issues, Issue.UNPAIRED_TOOL_RESULT)
        return

    truncated = any(marker in body for marker in _EVICTED_MARKERS)
    if truncated:
        flags.append(FLAG_TRUNCATED_TOOL_OUTPUT)

    calls[call_id] = CallRecord(
        call=record.call,
        result=ToolResult(
            call_id=call_id,
            name=name or record.call.name,
            text=_clip(body, clip_chars),
            state=status,
            summary=summary,
            truncated=truncated,
        ),
        result_message_index=message_index,
    )


def _parse_tool_calls(raw: Any) -> list[ToolCall]:
    if raw is None:
        return []
    payload = raw
    if isinstance(payload, str):
        if not payload.strip():
            return []
        try:
            payload = json.loads(payload)
        except json.JSONDecodeError:
            return []
    if not isinstance(payload, list):
        return []

    calls: list[ToolCall] = []
    for entry in payload:
        item = _as_mapping(entry)
        if item is None:
            continue
        # Tolerate the OpenAI-shaped {function:{name,arguments}} form too.
        function = _as_mapping(item.get("function")) or {}
        name = str(item.get("name") or function.get("name") or "")
        call_id = str(item.get("id") or "")
        raw_arguments = item.get("arguments", function.get("arguments"))
        arguments, error = _parse_arguments(raw_arguments)
        calls.append(
            ToolCall(
                id=call_id,
                name=name,
                arguments=arguments,
                raw_arguments=raw_arguments if isinstance(raw_arguments, str) else None,
                arguments_error=error,
            )
        )
    return calls


def _parse_arguments(raw: Any) -> tuple[dict[str, Any], str | None]:
    if raw is None:
        return {}, None
    if isinstance(raw, Mapping):
        return dict(raw), None
    if isinstance(raw, str):
        if not raw.strip():
            return {}, None
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError as error:
            return {}, f"invalid json: {error.msg}"
        if not isinstance(parsed, Mapping):
            return {}, "root value is not a json object"
        return dict(parsed), None
    return {}, f"unsupported arguments type: {type(raw).__name__}"


def _extract_tool_call_id(text: str) -> str | None:
    for line in text.splitlines():
        if line.strip().casefold().startswith(_TOOL_CALL_ID_PREFIX):
            value = line.split(":", 1)[1].strip()
            return value or None
    return None


def _parse_tool_result(text: str) -> tuple[str, str, str, str]:
    """Split ``ModelMessageBuilder.FormatToolResult`` output into its parts."""
    lines = text.splitlines()
    name = ""
    status = "unknown"
    summary = ""
    body_start = 0

    for index, line in enumerate(lines):
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.casefold().startswith(_TOOL_CALL_ID_PREFIX):
            body_start = index + 1
            continue
        match = _TOOL_STATUS_RE.match(stripped)
        if match:
            name = match.group("name")
            status = "success" if match.group("status").lower() == "succeeded" else "failure"
            body_start = index + 1
            continue
        lowered = stripped.casefold()
        if lowered.startswith(_ARGUMENTS_PREFIX) or lowered.startswith(_SUMMARY_PREFIX):
            if lowered.startswith(_SUMMARY_PREFIX):
                summary = stripped.split(":", 1)[1].strip()
            body_start = index + 1
            continue
        break

    body = "\n".join(lines[body_start:]).strip()
    return name, status, summary, body


def _parse_role(raw: Any) -> str | None:
    if isinstance(raw, bool):
        return None
    if isinstance(raw, int) and 0 <= raw < len(_ROLE_BY_ORDINAL):
        return _ROLE_BY_ORDINAL[raw]
    if isinstance(raw, str):
        return _ROLE_ALIASES.get(raw.strip().casefold())
    return None


def _as_mapping(value: Any) -> Mapping[str, Any] | None:
    return value if isinstance(value, Mapping) else None


def _as_text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, (int, float, bool)):
        return str(value)
    if isinstance(value, Mapping):
        # Content blocks: keep only text parts so tool metadata never leaks in.
        text = value.get("text")
        return text if isinstance(text, str) else ""
    if isinstance(value, list):
        return "\n".join(filter(None, (_as_text(item) for item in value)))
    return ""


def _clip(text: str, limit: int) -> str:
    if limit <= 0 or len(text) <= limit:
        return text
    return text[:limit] + f"\n...[clipped {len(text) - limit} chars]"


def _clean_text(text: str) -> str:
    return (text or "").replace("\r\n", "\n").strip()


def _append_issue(issues: list[Issue], issue: Issue) -> None:
    if issue not in issues:
        issues.append(issue)


def _title_excluded(title: str, config: CollectConfig) -> bool:
    if not config.exclude_title_contains:
        return False
    lowered = title.casefold()
    return any(token in lowered for token in config.exclude_title_contains)


def _relative_session(path: Path, root: Path) -> str:
    try:
        return str(path.parent.relative_to(root))
    except ValueError:
        return str(path.parent)


# --------------------------------------------------------------------------- #
# Serialization
# --------------------------------------------------------------------------- #


def write_traces(path: Path, traces: Sequence[Trace]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for trace in traces:
            handle.write(json.dumps(trace.to_dict(), ensure_ascii=False, default=str))
            handle.write("\n")


def read_traces(path: Path) -> list[Trace]:
    traces: list[Trace] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            traces.append(Trace.from_dict(json.loads(line)))
    return traces


def write_report(path: Path, report: CollectReport) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(report.to_dict(), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
