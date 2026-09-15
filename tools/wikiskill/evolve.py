#!/usr/bin/env python3
"""
WikiSkill for Athlon Agent — offline skill evolution from local logs.

Implements the WikiSkill paper idea (arXiv:2608.27454) against Athlon's
on-disk layout under ~/.athlon-agent/:

  Raw Layer   → wikiskill-workspace/raw/traces/
  Wiki Layer  → wikiskill-workspace/wiki/{index.md,logs.md,skill-impact.md,patterns/}
  Skills Layer→ ~/.athlon-agent/skills/  (proposals written under proposals/ first)

Default mode is rule-based (no LLM). Pass --llm to use an OpenAI-compatible
endpoint for deeper wiki consolidation and skill proposals.

Usage:
  # Full offline loop (scan → wiki → propose), dry-run by default
  python tools/wikiskill/evolve.py evolve

  # Only scan Athlon logs into Raw Layer
  python tools/wikiskill/evolve.py scan

  # Consolidate Raw → Wiki patterns
  python tools/wikiskill/evolve.py maintain-wiki

  # Propose skill create/patch from Wiki
  python tools/wikiskill/evolve.py propose

  # Apply an accepted proposal into skills/
  python tools/wikiskill/evolve.py apply --proposal proposals/20260831_120000_create_tool-retry.md

  # LLM-assisted evolution
  export ATHLON_ENDPOINT=https://api.openai.com/v1
  export ATHLON_API_KEY=sk-...
  export ATHLON_MODEL=gpt-4o-mini
  python tools/wikiskill/evolve.py evolve --llm

See tools/wikiskill/README.md for details.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import textwrap
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Optional
from urllib import error, request


# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

DEFAULT_ATHLON_ROOT = Path(os.path.expanduser("~/.athlon-agent"))
DEFAULT_WORKSPACE = DEFAULT_ATHLON_ROOT / "wikiskill-workspace"


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def slugify(text: str, max_len: int = 48) -> str:
    s = re.sub(r"[^a-zA-Z0-9._-]+", "-", (text or "").strip().lower())
    s = re.sub(r"-{2,}", "-", s).strip("-")
    return (s or "pattern")[:max_len]


# ---------------------------------------------------------------------------
# JSON helpers (Athlon writes UTF-8 with optional BOM + camelCase)
# ---------------------------------------------------------------------------

def load_json(path: Path) -> Any:
    text = path.read_text(encoding="utf-8-sig")
    return json.loads(text)


def load_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    if not path.is_file():
        return rows
    with path.open("r", encoding="utf-8-sig") as fh:
        for line_no, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError as exc:
                print(f"[warn] skip bad jsonl {path}:{line_no}: {exc}", file=sys.stderr)
    return rows


def write_json(path: Path, data: Any, indent: int = 2) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=indent) + "\n", encoding="utf-8")


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content if content.endswith("\n") else content + "\n", encoding="utf-8")


def append_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as fh:
        fh.write(content if content.endswith("\n") else content + "\n")


# ---------------------------------------------------------------------------
# Data models
# ---------------------------------------------------------------------------

@dataclass
class ToolEvent:
    name: str
    arguments: Any = None
    result: str = ""
    ok: bool = True
    error: str = ""


@dataclass
class TraceSummary:
    trace_id: str
    source: str  # session | training-sft | training-dpo | behavior
    session_id: str = ""
    title: str = ""
    model: str = ""
    workspace: str = ""
    active_skill: str = ""
    user_turns: int = 0
    assistant_turns: int = 0
    tool_calls: int = 0
    tool_failures: int = 0
    corrections: int = 0
    failed_tools: list[str] = field(default_factory=list)
    successful_tools: list[str] = field(default_factory=list)
    error_snippets: list[str] = field(default_factory=list)
    correction_notes: list[str] = field(default_factory=list)
    outcome: str = "unknown"  # success | failure | mixed | unknown
    path: str = ""


@dataclass
class WikiPattern:
    name: str
    kind: str  # failure | success | correction
    title: str
    root_cause: str
    workaround: str
    evidence: list[str] = field(default_factory=list)
    tools: list[str] = field(default_factory=list)
    count: int = 1


@dataclass
class SkillProposal:
    action: str  # create | patch | no_action
    name: str
    reason: str
    skill_md: str = ""
    purpose_md: str = ""
    edits: list[dict[str, str]] = field(default_factory=list)
    patterns_addressed: list[str] = field(default_factory=list)
    proposal_id: str = ""

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SkillProposal":
        return cls(
            action=str(data.get("action") or "no_action"),
            name=str(data.get("name") or ""),
            reason=str(data.get("reason") or ""),
            skill_md=str(data.get("skill_md") or ""),
            purpose_md=str(data.get("purpose_md") or ""),
            edits=list(data.get("edits") or []),
            patterns_addressed=[str(x) for x in (data.get("patterns_addressed") or [])],
            proposal_id=str(data.get("proposal_id") or ""),
        )


# ---------------------------------------------------------------------------
# Athlon scanners
# ---------------------------------------------------------------------------

class AthlonScanner:
    """Scan Athlon local data into immutable Raw Layer traces."""

    def __init__(self, athlon_root: Path, workspace: Path):
        self.root = athlon_root
        self.workspace = workspace
        self.raw_dir = workspace / "raw" / "traces"
        self.manifest_path = workspace / "raw" / "manifest.json"

    def scan(self, max_sessions: int = 50, days: Optional[int] = None) -> list[TraceSummary]:
        self.raw_dir.mkdir(parents=True, exist_ok=True)
        traces: list[TraceSummary] = []
        traces.extend(self._scan_sessions(max_sessions=max_sessions, days=days))
        traces.extend(self._scan_training_data())
        traces.extend(self._scan_behavior(days=days))

        write_json(
            self.manifest_path,
            {
                "scannedAt": utc_now_iso(),
                "athlonRoot": str(self.root),
                "traceCount": len(traces),
                "bySource": dict(Counter(t.source for t in traces)),
                "traces": [asdict(t) for t in traces],
            },
        )
        return traces

    def _scan_sessions(self, max_sessions: int, days: Optional[int]) -> list[TraceSummary]:
        sessions_dir = self.root / "sessions"
        if not sessions_dir.is_dir():
            return []

        session_dirs = sorted(
            [p for p in sessions_dir.iterdir() if p.is_dir() and (p / "session.json").is_file()],
            key=lambda p: (p / "session.json").stat().st_mtime,
            reverse=True,
        )[:max_sessions]

        cutoff = None
        if days is not None and days > 0:
            cutoff = datetime.now(timezone.utc).timestamp() - days * 86400

        out: list[TraceSummary] = []
        for session_dir in session_dirs:
            session_path = session_dir / "session.json"
            if cutoff and session_path.stat().st_mtime < cutoff:
                continue
            try:
                session = load_json(session_path)
            except Exception as exc:
                print(f"[warn] bad session {session_path}: {exc}", file=sys.stderr)
                continue

            messages = session.get("messages") or []
            # Prefer conversation.jsonl if richer
            conv_path = session_dir / "conversation.jsonl"
            if conv_path.is_file():
                conv = load_jsonl(conv_path)
                if len(conv) >= len(messages):
                    # last-wins by id
                    by_id: dict[str, dict] = {}
                    for row in conv:
                        mid = row.get("id") or row.get("Id")
                        if mid:
                            by_id[mid] = row
                    messages = list(by_id.values())

            summary = self._summarize_messages(
                messages,
                source="session",
                session_id=str(session.get("id") or session_dir.name),
                title=str(session.get("title") or ""),
                model=str(session.get("modelName") or ""),
                workspace=str(session.get("activeWorkspace") or ""),
                active_skill=str(session.get("activeSkill") or ""),
            )
            out_path = self.raw_dir / f"session_{summary.session_id}.json"
            write_json(
                out_path,
                {
                    "summary": asdict(summary),
                    "sessionMeta": {
                        "id": session.get("id"),
                        "title": session.get("title"),
                        "modelName": session.get("modelName"),
                        "activeWorkspace": session.get("activeWorkspace"),
                        "activeSkill": session.get("activeSkill"),
                        "updatedAt": session.get("updatedAt"),
                    },
                    "messages": messages,
                    "toolEvents": self._extract_tool_events(messages),
                    "corrections": self._detect_corrections(messages),
                },
            )
            summary.path = str(out_path)
            out.append(summary)
        return out

    def _scan_training_data(self) -> list[TraceSummary]:
        td = self.root / "training-data"
        if not td.is_dir():
            return []
        out: list[TraceSummary] = []
        for path in sorted(td.glob("sft-traces-*.jsonl")):
            for idx, row in enumerate(load_jsonl(path)):
                messages = row.get("messages") or []
                meta = row.get("metadata") or {}
                sid = str(meta.get("sessionId") or f"{path.stem}-{idx}")
                summary = self._summarize_messages(
                    messages,
                    source="training-sft",
                    session_id=sid,
                    model=str(meta.get("model") or ""),
                    title=f"sft:{meta.get('source') or path.name}",
                )
                summary.corrections = 1 if meta.get("hasCorrection") else summary.corrections
                out_path = self.raw_dir / f"sft_{slugify(sid)}_{idx}.json"
                write_json(
                    out_path,
                    {
                        "summary": asdict(summary),
                        "metadata": meta,
                        "messages": messages,
                        "toolEvents": self._extract_tool_events(messages),
                        "corrections": self._detect_corrections(messages),
                    },
                )
                summary.path = str(out_path)
                out.append(summary)
        return out

    def _scan_behavior(self, days: Optional[int]) -> list[TraceSummary]:
        pending = self.root / "behavior" / "pending.jsonl"
        rows = load_jsonl(pending)
        if not rows:
            return []

        # Aggregate tool failures from behavior events
        failures: list[dict[str, Any]] = []
        for row in rows:
            params = row.get("parameters") or row.get("event_params") or {}
            event_id = str(row.get("eventId") or row.get("event_id") or "")
            success = params.get("success")
            tool = str(params.get("tool_name") or params.get("tool") or "")
            if success is False or "fail" in event_id.lower() or "error" in event_id.lower():
                failures.append(
                    {
                        "timestamp": row.get("timestamp") or row.get("event_time"),
                        "eventId": event_id,
                        "tool": tool,
                        "params": params,
                        "message": row.get("messageContent") or row.get("message_content"),
                    }
                )

        if not failures:
            return []

        summary = TraceSummary(
            trace_id=f"behavior-{datetime.now(timezone.utc).strftime('%Y%m%d')}",
            source="behavior",
            title="behavior pending failures",
            tool_failures=len(failures),
            failed_tools=sorted({f["tool"] for f in failures if f.get("tool")}),
            error_snippets=[str(f.get("message") or f.get("eventId"))[:200] for f in failures[:20]],
            outcome="failure" if failures else "unknown",
        )
        out_path = self.raw_dir / f"{summary.trace_id}.json"
        write_json(out_path, {"summary": asdict(summary), "failures": failures})
        summary.path = str(out_path)
        return [summary]

    # ---- message parsing -------------------------------------------------

    @staticmethod
    def _role(msg: dict[str, Any]) -> str:
        role = msg.get("role") or msg.get("Role") or ""
        if isinstance(role, int):
            # enum ordinal fallback: 0 System, 1 User, 2 Assistant, 3 Tool
            return {0: "system", 1: "user", 2: "assistant", 3: "tool"}.get(role, str(role))
        return str(role).lower()

    @staticmethod
    def _content(msg: dict[str, Any]) -> str:
        return str(msg.get("content") or msg.get("Content") or "")

    @classmethod
    def _parse_tool_calls(cls, msg: dict[str, Any]) -> list[dict[str, Any]]:
        raw = msg.get("toolCallsJson") or msg.get("ToolCallsJson") or msg.get("tool_calls")
        if raw is None:
            return []
        if isinstance(raw, str):
            try:
                raw = json.loads(raw)
            except json.JSONDecodeError:
                return []
        if not isinstance(raw, list):
            return []
        calls = []
        for item in raw:
            if not isinstance(item, dict):
                continue
            # Athlon AgentToolCall or OpenAI-style
            name = item.get("name") or (item.get("function") or {}).get("name") or ""
            args = item.get("arguments")
            if args is None and isinstance(item.get("function"), dict):
                args = item["function"].get("arguments")
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except json.JSONDecodeError:
                    pass
            calls.append({"id": item.get("id") or item.get("Id"), "name": name, "arguments": args})
        return calls

    @classmethod
    def _extract_tool_events(cls, messages: list[dict[str, Any]]) -> list[dict[str, Any]]:
        events: list[dict[str, Any]] = []
        pending: dict[str, dict[str, Any]] = {}
        for msg in messages:
            role = cls._role(msg)
            if role == "assistant":
                for call in cls._parse_tool_calls(msg):
                    cid = call.get("id") or f"anon-{len(pending)}"
                    pending[cid] = call
                    events.append(
                        {
                            "phase": "call",
                            "id": cid,
                            "name": call.get("name"),
                            "arguments": call.get("arguments"),
                        }
                    )
            elif role == "tool":
                content = cls._content(msg)
                tool_call_id = msg.get("toolCallId") or msg.get("tool_call_id") or msg.get("parentId")
                name = ""
                if tool_call_id and tool_call_id in pending:
                    name = pending[tool_call_id].get("name") or ""
                ok = not content.lstrip().lower().startswith("error:")
                events.append(
                    {
                        "phase": "result",
                        "id": tool_call_id,
                        "name": name,
                        "ok": ok,
                        "resultPreview": content[:500],
                        "error": "" if ok else content[:500],
                    }
                )
        return events

    @classmethod
    def _detect_corrections(cls, messages: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Port of CorrectionDetector: fail → user → same-tool success."""
        events = cls._extract_tool_events(messages)
        # Build ordered failure indices into messages
        failures: list[tuple[int, str]] = []
        for i, msg in enumerate(messages):
            if cls._role(msg) != "tool":
                continue
            content = cls._content(msg)
            if content.lstrip().lower().startswith("error:"):
                # find preceding assistant tool name
                name = ""
                for j in range(i - 1, -1, -1):
                    if cls._role(messages[j]) == "assistant":
                        calls = cls._parse_tool_calls(messages[j])
                        if calls:
                            name = calls[0].get("name") or ""
                        break
                failures.append((i, name))

        corrections: list[dict[str, Any]] = []
        for fail_idx, failed_name in failures:
            if not failed_name:
                continue
            # next user
            user_idx = None
            for j in range(fail_idx + 1, len(messages)):
                if cls._role(messages[j]) == "user":
                    user_idx = j
                    break
            if user_idx is None:
                continue
            # successful retry of same tool
            success = False
            for j in range(user_idx + 1, len(messages)):
                if cls._role(messages[j]) != "assistant":
                    continue
                calls = cls._parse_tool_calls(messages[j])
                for call in calls:
                    if call.get("name") != failed_name:
                        continue
                    # find matching tool result
                    for k in range(j + 1, len(messages)):
                        if cls._role(messages[k]) == "tool":
                            content = cls._content(messages[k])
                            if not content.lstrip().lower().startswith("error:"):
                                success = True
                            break
                        if cls._role(messages[k]) == "assistant":
                            break
                    if success:
                        break
                if success:
                    break
            if success:
                corrections.append(
                    {
                        "failedTool": failed_name,
                        "userCorrection": cls._content(messages[user_idx])[:300],
                        "failureIndex": fail_idx,
                        "correctionIndex": user_idx,
                    }
                )
        return corrections

    @classmethod
    def _summarize_messages(
        cls,
        messages: list[dict[str, Any]],
        *,
        source: str,
        session_id: str,
        title: str = "",
        model: str = "",
        workspace: str = "",
        active_skill: str = "",
    ) -> TraceSummary:
        events = cls._extract_tool_events(messages)
        failures = [e for e in events if e.get("phase") == "result" and not e.get("ok")]
        successes = [e for e in events if e.get("phase") == "result" and e.get("ok")]
        corrections = cls._detect_corrections(messages)
        user_turns = sum(1 for m in messages if cls._role(m) == "user")
        assistant_turns = sum(1 for m in messages if cls._role(m) == "assistant")
        if failures and successes:
            outcome = "mixed"
        elif failures and not successes:
            outcome = "failure"
        elif successes:
            outcome = "success"
        else:
            outcome = "unknown"
        return TraceSummary(
            trace_id=session_id or hashlib.md5(title.encode(), usedforsecurity=False).hexdigest()[:12],
            source=source,
            session_id=session_id,
            title=title,
            model=model,
            workspace=workspace,
            active_skill=active_skill,
            user_turns=user_turns,
            assistant_turns=assistant_turns,
            tool_calls=sum(1 for e in events if e.get("phase") == "call"),
            tool_failures=len(failures),
            corrections=len(corrections),
            failed_tools=sorted({str(e.get("name") or "") for e in failures if e.get("name")}),
            successful_tools=sorted({str(e.get("name") or "") for e in successes if e.get("name")}),
            error_snippets=[str(e.get("error") or "")[:200] for e in failures[:10]],
            correction_notes=[c.get("userCorrection", "")[:200] for c in corrections[:5]],
            outcome=outcome,
        )


# ---------------------------------------------------------------------------
# Wiki Maintainer (rule-based + optional LLM)
# ---------------------------------------------------------------------------

class WikiMaintainer:
    def __init__(self, workspace: Path, use_llm: bool = False, llm: Optional["LlmClient"] = None):
        self.workspace = workspace
        self.wiki_dir = workspace / "wiki"
        self.patterns_dir = self.wiki_dir / "patterns"
        self.index_path = self.wiki_dir / "index.md"
        self.logs_path = self.wiki_dir / "logs.md"
        self.impact_path = self.wiki_dir / "skill-impact.md"
        self.use_llm = use_llm
        self.llm = llm

    def ensure_layout(self) -> None:
        self.patterns_dir.mkdir(parents=True, exist_ok=True)
        if not self.index_path.is_file():
            write_text(self.index_path, "# Wiki Index\n\nKnown patterns (problem + root cause + fix).\n\n")
        if not self.logs_path.is_file():
            write_text(self.logs_path, "# Evolution Log\n\n")
        if not self.impact_path.is_file():
            write_text(
                self.impact_path,
                "# Skill Impact Tracker\n\n"
                "Records of skill proposals, diffs, validation outcomes, and accept/reject decisions.\n\n",
            )

    def maintain(self) -> list[WikiPattern]:
        self.ensure_layout()
        raw_dir = self.workspace / "raw" / "traces"
        traces = []
        if raw_dir.is_dir():
            for path in sorted(raw_dir.glob("*.json")):
                try:
                    traces.append(load_json(path))
                except Exception as exc:
                    print(f"[warn] skip raw {path}: {exc}", file=sys.stderr)

        patterns = self._rule_based_patterns(traces)
        if self.use_llm and self.llm:
            patterns = self._llm_refine_patterns(patterns, traces)

        for pattern in patterns:
            self._upsert_pattern(pattern)

        self._rewrite_index(patterns)
        append_text(
            self.logs_path,
            f"## {utc_now_iso()}\n"
            f"- Traces analyzed: {len(traces)}\n"
            f"- Patterns created/updated: {len(patterns)}\n"
            f"- Mode: {'llm' if self.use_llm else 'rule-based'}\n\n",
        )
        return patterns

    def _rule_based_patterns(self, traces: list[dict[str, Any]]) -> list[WikiPattern]:
        failure_counter: Counter[str] = Counter()
        failure_examples: dict[str, list[str]] = defaultdict(list)
        correction_counter: Counter[str] = Counter()
        correction_examples: dict[str, list[str]] = defaultdict(list)
        success_counter: Counter[str] = Counter()

        for trace in traces:
            summary = trace.get("summary") or {}
            for tool in summary.get("failedTools") or summary.get("failed_tools") or []:
                if tool:
                    failure_counter[tool] += 1
            for snippet in summary.get("errorSnippets") or summary.get("error_snippets") or []:
                for tool in summary.get("failedTools") or summary.get("failed_tools") or ["unknown"]:
                    if snippet:
                        failure_examples[tool].append(snippet[:240])
            for corr in trace.get("corrections") or []:
                tool = corr.get("failedTool") or "unknown"
                correction_counter[tool] += 1
                note = corr.get("userCorrection") or ""
                if note:
                    correction_examples[tool].append(note[:240])
            for tool in summary.get("successfulTools") or summary.get("successful_tools") or []:
                if tool:
                    success_counter[tool] += 1

            for event in trace.get("toolEvents") or []:
                if event.get("phase") == "result" and not event.get("ok"):
                    name = event.get("name") or "unknown"
                    failure_counter[name] += 1
                    err = event.get("error") or event.get("resultPreview") or ""
                    if err:
                        failure_examples[name].append(str(err)[:240])

        patterns: list[WikiPattern] = []

        for tool, count in failure_counter.most_common(20):
            if count < 1:
                continue
            examples = failure_examples.get(tool, [])[:5]
            root = self._infer_root_cause(tool, examples)
            workaround = self._infer_workaround(tool, root, correction_examples.get(tool, []))
            patterns.append(
                WikiPattern(
                    name=f"tool-failure-{slugify(tool)}",
                    kind="failure",
                    title=f"Repeated failures for `{tool}`",
                    root_cause=root,
                    workaround=workaround,
                    evidence=examples,
                    tools=[tool],
                    count=count,
                )
            )

        for tool, count in correction_counter.most_common(15):
            if count < 1:
                continue
            notes = correction_examples.get(tool, [])[:5]
            patterns.append(
                WikiPattern(
                    name=f"correction-{slugify(tool)}",
                    kind="correction",
                    title=f"User correction after `{tool}` failure",
                    root_cause=f"Agent failed `{tool}` then recovered after user guidance ({count} times).",
                    workaround=self._correction_workaround(tool, notes),
                    evidence=notes,
                    tools=[tool],
                    count=count,
                )
            )

        # Aggregate success strategies for frequently used tools
        for tool, count in success_counter.most_common(10):
            if count < 3:
                continue
            if failure_counter.get(tool, 0) >= count:
                continue
            patterns.append(
                WikiPattern(
                    name=f"success-{slugify(tool)}",
                    kind="success",
                    title=f"Reliable use of `{tool}`",
                    root_cause=f"`{tool}` succeeds often ({count} results) and should be preferred when applicable.",
                    workaround=f"Prefer `{tool}` with validated arguments; verify result before next step.",
                    evidence=[],
                    tools=[tool],
                    count=count,
                )
            )

        return patterns

    @staticmethod
    def _infer_root_cause(tool: str, examples: list[str]) -> str:
        blob = "\n".join(examples).lower()
        if "path" in blob or "not found" in blob or "enoent" in blob:
            return f"`{tool}` often fails due to invalid/missing paths or wrong workspace root."
        if "permission" in blob or "access" in blob or "denied" in blob:
            return f"`{tool}` fails due to permission / sandbox restrictions."
        if "timeout" in blob or "timed out" in blob:
            return f"`{tool}` times out; command/query may be too broad or hung."
        if "approval" in blob or "denied by user" in blob:
            return f"`{tool}` requires approval and was rejected or not confirmed."
        if "argument" in blob or "required" in blob or "invalid" in blob:
            return f"`{tool}` receives invalid or incomplete arguments."
        if examples:
            return f"`{tool}` failed repeatedly. Sample error: {examples[0][:160]}"
        return f"`{tool}` failed repeatedly; inspect raw traces for details."

    @staticmethod
    def _infer_workaround(tool: str, root: str, corrections: list[str]) -> str:
        tips = []
        if "path" in root.lower():
            tips.append(f"Before `{tool}`, verify the path with `file_list` / `glob_files` and prefer workspace-relative paths.")
        if "argument" in root.lower():
            tips.append(f"Validate required args for `{tool}` against the tool schema; never invent required fields.")
        if "timeout" in root.lower():
            tips.append(f"Narrow `{tool}` scope (smaller path, tighter pattern, shorter command).")
        if corrections:
            tips.append("User guidance that recovered the failure: " + " | ".join(corrections[:2]))
        if not tips:
            tips.append(f"On `{tool}` failure starting with `Error:`, stop and re-check args/workspace before retrying once.")
        return " ".join(tips)

    @staticmethod
    def _correction_workaround(tool: str, notes: list[str]) -> str:
        if notes:
            return (
                f"When `{tool}` fails, incorporate user correction intent before retry. "
                f"Observed corrections: " + " | ".join(notes[:3])
            )
        return f"After `{tool}` failure, ask for clarification or re-read the target before retrying."

    def _upsert_pattern(self, pattern: WikiPattern) -> None:
        path = self.patterns_dir / f"{pattern.name}.md"
        body = textwrap.dedent(
            f"""\
            # {pattern.title}

            - **Kind**: {pattern.kind}
            - **Tools**: {', '.join(f'`{t}`' for t in pattern.tools) or 'n/a'}
            - **Count**: {pattern.count}
            - **Updated**: {utc_now_iso()}

            ## Root cause

            {pattern.root_cause}

            ## Workaround / action pattern

            {pattern.workaround}

            ## Evidence

            """
        )
        if pattern.evidence:
            for ev in pattern.evidence:
                safe = str(ev).replace("`", "'")
                body += f"- `{safe}`\n"
        else:
            body += "- (no inline snippets; see raw traces)\n"
        write_text(path, body)

    def _rewrite_index(self, patterns: list[WikiPattern]) -> None:
        # Merge with existing index entries by filename
        existing: dict[str, str] = {}
        if self.index_path.is_file():
            for line in self.index_path.read_text(encoding="utf-8").splitlines():
                m = re.match(r"- \[([^\]]+)\]\(([^)]+)\):\s*(.*)", line)
                if m:
                    existing[m.group(1)] = m.group(3)

        lines = ["# Wiki Index", "", "Known patterns (problem + root cause + fix).", ""]
        for pattern in sorted(patterns, key=lambda p: (-p.count, p.name)):
            desc = f"{pattern.title}. Root cause: {pattern.root_cause} Fix: {pattern.workaround}"
            desc = re.sub(r"\s+", " ", desc)[:280]
            existing[pattern.name] = desc
            rel = f"patterns/{pattern.name}.md"
            lines.append(f"- [{pattern.name}]({rel}): {desc}")

        # Keep older patterns not regenerated this round
        for name, desc in sorted(existing.items()):
            if any(p.name == name for p in patterns):
                continue
            lines.append(f"- [{name}](patterns/{name}.md): {desc}")

        write_text(self.index_path, "\n".join(lines) + "\n")

    def _llm_refine_patterns(
        self, patterns: list[WikiPattern], traces: list[dict[str, Any]]
    ) -> list[WikiPattern]:
        assert self.llm is not None
        # Provide compact sample of failures for refinement
        sample = []
        for trace in traces[:12]:
            summary = trace.get("summary") or {}
            sample.append(
                {
                    "source": summary.get("source"),
                    "outcome": summary.get("outcome"),
                    "failedTools": summary.get("failedTools") or summary.get("failed_tools"),
                    "errors": (summary.get("errorSnippets") or summary.get("error_snippets") or [])[:3],
                    "corrections": (trace.get("corrections") or [])[:2],
                }
            )
        prompt = textwrap.dedent(
            f"""\
            You are the Wiki Maintainer for Athlon Agent (WikiSkill).
            Given rule-based patterns and sample traces, refine/merge them into
            concise actionable wiki patterns.

            Return ONLY a JSON array of objects with keys:
            name, kind (failure|success|correction), title, root_cause, workaround,
            tools (string array), count (int), evidence (string array).

            Rule-based patterns:
            {json.dumps([asdict(p) for p in patterns], ensure_ascii=False, indent=2)}

            Sample traces:
            {json.dumps(sample, ensure_ascii=False, indent=2)}
            """
        )
        try:
            text = self.llm.chat(
                system="You maintain a structured agent wiki. Be concise and actionable.",
                user=prompt,
            )
            data = extract_json_array(text)
            refined: list[WikiPattern] = []
            for item in data:
                refined.append(
                    WikiPattern(
                        name=slugify(str(item.get("name") or "pattern")),
                        kind=str(item.get("kind") or "failure"),
                        title=str(item.get("title") or item.get("name") or "pattern"),
                        root_cause=str(item.get("root_cause") or item.get("rootCause") or ""),
                        workaround=str(item.get("workaround") or ""),
                        evidence=[str(x) for x in (item.get("evidence") or [])][:8],
                        tools=[str(x) for x in (item.get("tools") or [])],
                        count=int(item.get("count") or 1),
                    )
                )
            return refined or patterns
        except Exception as exc:
            print(f"[warn] LLM wiki refine failed, keeping rule-based: {exc}", file=sys.stderr)
            return patterns


# ---------------------------------------------------------------------------
# Skill Proposer
# ---------------------------------------------------------------------------

class SkillProposer:
    def __init__(
        self,
        workspace: Path,
        skills_dir: Path,
        use_llm: bool = False,
        llm: Optional["LlmClient"] = None,
    ):
        self.workspace = workspace
        self.skills_dir = skills_dir
        self.proposals_dir = workspace / "proposals"
        self.wiki_dir = workspace / "wiki"
        self.use_llm = use_llm
        self.llm = llm

    def propose(self, max_proposals: int = 3) -> list[SkillProposal]:
        self.proposals_dir.mkdir(parents=True, exist_ok=True)
        index = self.wiki_dir / "index.md"
        patterns = list((self.wiki_dir / "patterns").glob("*.md")) if (self.wiki_dir / "patterns").is_dir() else []
        impact = (self.wiki_dir / "skill-impact.md").read_text(encoding="utf-8") if (self.wiki_dir / "skill-impact.md").is_file() else ""
        existing_skills = self._list_skills()

        if self.use_llm and self.llm:
            proposals = self._llm_propose(index, patterns, impact, existing_skills, max_proposals)
        else:
            proposals = self._rule_propose(patterns, existing_skills, max_proposals)

        stamped = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
        written: list[SkillProposal] = []
        for i, proposal in enumerate(proposals):
            if proposal.action == "no_action":
                continue
            proposal.proposal_id = f"{stamped}_{proposal.action}_{slugify(proposal.name)}"
            out = self.proposals_dir / f"{proposal.proposal_id}.md"
            write_text(out, self._format_proposal_markdown(proposal))
            # Also dump machine-readable sidecar
            write_json(self.proposals_dir / f"{proposal.proposal_id}.json", asdict(proposal))
            written.append(proposal)
            print(f"[propose] wrote {out}")
        if not written:
            print("[propose] no actionable proposals")
        return written

    def _list_skills(self) -> list[dict[str, str]]:
        skills = []
        if not self.skills_dir.is_dir():
            return skills
        for folder in sorted(self.skills_dir.iterdir()):
            skill_md = folder / "SKILL.md"
            if not skill_md.is_file():
                continue
            text = skill_md.read_text(encoding="utf-8-sig")
            name, desc = parse_skill_frontmatter(text)
            skills.append({"folder": folder.name, "name": name or folder.name, "description": desc, "path": str(skill_md)})
        return skills

    def _rule_propose(
        self, pattern_files: list[Path], existing_skills: list[dict[str, str]], max_proposals: int
    ) -> list[SkillProposal]:
        # Prefer failure/correction patterns with highest signal
        candidates: list[tuple[int, Path, str]] = []
        for path in pattern_files:
            text = path.read_text(encoding="utf-8")
            kind = "failure"
            if "**Kind**: correction" in text:
                kind = "correction"
            elif "**Kind**: success" in text:
                kind = "success"
            count = 1
            m = re.search(r"\*\*Count\*\*:\s*(\d+)", text)
            if m:
                count = int(m.group(1))
            score = count * (3 if kind == "correction" else 2 if kind == "failure" else 1)
            candidates.append((score, path, kind))
        candidates.sort(key=lambda x: -x[0])

        proposals: list[SkillProposal] = []
        existing_names = {s["name"] for s in existing_skills}

        # Bundle top failure/correction patterns into one skill if none exists
        top = [c for c in candidates if c[2] in ("failure", "correction")][:5]
        if not top:
            return [SkillProposal(action="no_action", name="", reason="No failure/correction patterns found.")]

        target_name = "athlon-wikiskill-hardening"
        pattern_names = [c[1].stem for c in top]
        instructions = self._build_skill_body(top)

        if target_name in existing_names:
            proposals.append(
                SkillProposal(
                    action="patch",
                    name=target_name,
                    reason="Update hardening skill with newest wiki patterns.",
                    patterns_addressed=pattern_names,
                    edits=[
                        {
                            "op": "append",
                            "content": "\n\n## WikiSkill refresh "
                            + utc_now_iso()
                            + "\n\n"
                            + instructions
                            + "\n",
                        }
                    ],
                    purpose_md=self._purpose_md(target_name, pattern_names),
                )
            )
        else:
            skill_md = (
                "---\n"
                f"name: {target_name}\n"
                "description: Procedural hardening derived from Athlon WikiSkill — "
                "avoid repeated tool failures and apply correction lessons.\n"
                "---\n\n"
                "# Athlon WikiSkill Hardening\n\n"
                "Use this skill whenever tool calls fail or the user corrects a prior tool choice.\n\n"
                "## When to Apply\n"
                "- Tool results start with `Error:`\n"
                "- User corrects path/args/tool choice after a failure\n"
                "- Repeated failures for the same tool in one turn\n\n"
                "## When NOT to Apply\n"
                "- Simple one-shot successful tasks\n"
                "- Computer Use exclusive mode\n\n"
                "## Instructions\n\n"
                f"{instructions}\n"
            )
            proposals.append(
                SkillProposal(
                    action="create",
                    name=target_name,
                    reason="Create a hardening skill from recurring wiki failure/correction patterns.",
                    skill_md=skill_md,
                    purpose_md=self._purpose_md(target_name, pattern_names),
                    patterns_addressed=pattern_names,
                )
            )

        # Optionally create a focused skill for the #1 correction tool
        if len(proposals) < max_proposals and top:
            best = top[0]
            tool_m = re.search(r"`([^`]+)`", best[1].read_text(encoding="utf-8"))
            tool = tool_m.group(1) if tool_m else best[1].stem
            focused = f"fix-{slugify(tool)}"
            if focused not in existing_names and focused != target_name:
                body = best[1].read_text(encoding="utf-8")
                root = extract_section(body, "Root cause") or ""
                fix = extract_section(body, "Workaround / action pattern") or ""
                skill_md = (
                    "---\n"
                    f"name: {focused}\n"
                    f"description: Recover from `{tool}` failures using WikiSkill patterns.\n"
                    "---\n\n"
                    f"# Fix `{tool}` failures\n\n"
                    "## When to Apply\n"
                    f"- About to call `{tool}` after a previous error\n"
                    f"- User mentions correcting `{tool}` usage\n\n"
                    "## Instructions\n"
                    f"1. Root cause hint: {root.strip()}\n"
                    f"2. Apply: {fix.strip()}\n"
                    "3. Retry once with corrected args; if still failing, stop and report.\n"
                )
                proposals.append(
                    SkillProposal(
                        action="create",
                        name=focused,
                        reason=f"Focused recovery skill for `{tool}`.",
                        skill_md=skill_md,
                        purpose_md=self._purpose_md(focused, [best[1].stem]),
                        patterns_addressed=[best[1].stem],
                    )
                )

        return proposals[:max_proposals]

    def _build_skill_body(self, top: list[tuple[int, Path, str]]) -> str:
        lines = []
        for score, path, kind in top:
            text = path.read_text(encoding="utf-8")
            title = path.stem
            root = extract_section(text, "Root cause") or ""
            fix = extract_section(text, "Workaround / action pattern") or ""
            lines.append(f"### {title} ({kind}, score={score})")
            lines.append(f"- Root cause: {root.strip()}")
            lines.append(f"- Action: {fix.strip()}")
            lines.append("")
        lines.append("### General rules")
        lines.append("1. Prefer find/verify then act; never invent paths or required args.")
        lines.append("2. On `Error:` tool results, diagnose once using wiki lessons before retrying.")
        lines.append("3. One corrective retry max unless the user provides new information.")
        lines.append("4. Load this skill with `load_skill_through_path` when relevant.")
        return "\n".join(lines)

    @staticmethod
    def _purpose_md(name: str, patterns: list[str]) -> str:
        return textwrap.dedent(
            f"""\
            # PURPOSE — {name}

            ## Origin
            Generated by Athlon WikiSkill offline evolution ({utc_now_iso()}).

            ## Patterns Addressed
            {chr(10).join(f'- {p}' for p in patterns) or '- (none)'}

            ## Evolution History
            - Created/updated by tools/wikiskill/evolve.py
            """
        )

    def _llm_propose(
        self,
        index: Path,
        patterns: list[Path],
        impact: str,
        existing_skills: list[dict[str, str]],
        max_proposals: int,
    ) -> list[SkillProposal]:
        assert self.llm is not None
        index_text = index.read_text(encoding="utf-8") if index.is_file() else ""
        pattern_blobs = []
        for path in patterns[:12]:
            pattern_blobs.append({"name": path.stem, "content": path.read_text(encoding="utf-8")[:2500]})
        prompt = textwrap.dedent(
            f"""\
            You are the Skill Proposer for Athlon Agent (WikiSkill paper).
            Propose up to {max_proposals} atomic skill changes (create or patch).
            Do NOT repeat rejected approaches listed in skill-impact.

            Return ONLY a JSON array of objects:
            {{
              "action": "create"|"patch"|"no_action",
              "name": "skill-name",
              "reason": "...",
              "skill_md": "full SKILL.md for create",
              "purpose_md": "...",
              "edits": [{{"op":"append","content":"..."}}],
              "patterns_addressed": ["pattern-name"]
            }}

            Wiki index:
            {index_text[:4000]}

            Patterns:
            {json.dumps(pattern_blobs, ensure_ascii=False)}

            Existing skills:
            {json.dumps(existing_skills, ensure_ascii=False)}

            skill-impact.md (tail):
            {impact[-3000:]}
            """
        )
        try:
            text = self.llm.chat(
                system="Propose concise Athlon SKILL.md modules with YAML frontmatter name+description.",
                user=prompt,
            )
            data = extract_json_array(text)
            out: list[SkillProposal] = []
            for item in data:
                out.append(
                    SkillProposal(
                        action=str(item.get("action") or "no_action"),
                        name=str(item.get("name") or ""),
                        reason=str(item.get("reason") or ""),
                        skill_md=str(item.get("skill_md") or item.get("skillMd") or ""),
                        purpose_md=str(item.get("purpose_md") or item.get("purposeMd") or ""),
                        edits=list(item.get("edits") or []),
                        patterns_addressed=[str(x) for x in (item.get("patterns_addressed") or item.get("patternsAddressed") or [])],
                    )
                )
            return out or self._rule_propose(patterns, existing_skills, max_proposals)
        except Exception as exc:
            print(f"[warn] LLM propose failed, falling back to rules: {exc}", file=sys.stderr)
            return self._rule_propose(patterns, existing_skills, max_proposals)

    @staticmethod
    def _format_proposal_markdown(proposal: SkillProposal) -> str:
        parts = [
            f"# Proposal `{proposal.proposal_id}`",
            "",
            f"- **Action**: `{proposal.action}`",
            f"- **Skill**: `{proposal.name}`",
            f"- **Reason**: {proposal.reason}",
            f"- **Patterns**: {', '.join(proposal.patterns_addressed) or 'n/a'}",
            f"- **Created**: {utc_now_iso()}",
            "",
            "## Review checklist",
            "- [ ] Patterns match real Athlon failures",
            "- [ ] Instructions are actionable and not model-specific hacks",
            "- [ ] Safe to install into `~/.athlon-agent/skills/`",
            "",
        ]
        if proposal.action == "create":
            parts += ["## SKILL.md", "", "```markdown", proposal.skill_md.rstrip(), "```", ""]
            if proposal.purpose_md:
                parts += ["## PURPOSE.md", "", "```markdown", proposal.purpose_md.rstrip(), "```", ""]
        elif proposal.action == "patch":
            parts += ["## Edits", "", "```json", json.dumps(proposal.edits, ensure_ascii=False, indent=2), "```", ""]
        parts += [
            "## Apply",
            "",
            f"```bash\npython tools/wikiskill/evolve.py apply --proposal {proposal.proposal_id}\n```",
            "",
        ]
        return "\n".join(parts)


# ---------------------------------------------------------------------------
# Gating / Apply
# ---------------------------------------------------------------------------

class ProposalApplicator:
    def __init__(self, workspace: Path, skills_dir: Path):
        self.workspace = workspace
        self.skills_dir = skills_dir
        self.proposals_dir = workspace / "proposals"
        self.impact_path = workspace / "wiki" / "skill-impact.md"
        self.backup_dir = workspace / "backups"

    def apply(self, proposal_ref: str, dry_run: bool = False, accept: bool = True) -> None:
        path = self._resolve_proposal(proposal_ref)
        data = load_json(path) if path.suffix == ".json" else None
        if data is None:
            # Prefer sidecar json next to md
            sidecar = path.with_suffix(".json")
            if not sidecar.is_file():
                raise FileNotFoundError(f"Missing proposal json for {path}")
            data = load_json(sidecar)

        proposal = SkillProposal.from_dict(data)
        if proposal.action == "no_action":
            print("[apply] no_action proposal — nothing to do")
            return

        target_dir = self.skills_dir / slugify(proposal.name).replace("_", "-")
        # Prefer existing folder matching skill name frontmatter
        for folder in self.skills_dir.iterdir() if self.skills_dir.is_dir() else []:
            skill_md = folder / "SKILL.md"
            if not skill_md.is_file():
                continue
            name, _ = parse_skill_frontmatter(skill_md.read_text(encoding="utf-8-sig"))
            if name == proposal.name:
                target_dir = folder
                break

        if dry_run:
            print(f"[apply:dry-run] would {proposal.action} skill `{proposal.name}` at {target_dir}")
            return

        self.backup_dir.mkdir(parents=True, exist_ok=True)
        if target_dir.is_dir():
            stamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
            backup = self.backup_dir / f"{target_dir.name}_{stamp}"
            shutil.copytree(target_dir, backup)
            print(f"[apply] backup → {backup}")

        if proposal.action == "create":
            target_dir.mkdir(parents=True, exist_ok=True)
            write_text(target_dir / "SKILL.md", proposal.skill_md)
            if proposal.purpose_md:
                write_text(target_dir / "PURPOSE.md", proposal.purpose_md)
            print(f"[apply] created {target_dir / 'SKILL.md'}")
        elif proposal.action == "patch":
            skill_path = target_dir / "SKILL.md"
            if not skill_path.is_file():
                raise FileNotFoundError(f"Cannot patch missing skill: {skill_path}")
            content = skill_path.read_text(encoding="utf-8-sig")
            content = apply_edits(content, proposal.edits)
            write_text(skill_path, content)
            if proposal.purpose_md:
                write_text(target_dir / "PURPOSE.md", proposal.purpose_md)
            print(f"[apply] patched {skill_path}")
        else:
            raise ValueError(f"Unknown action: {proposal.action}")

        decision = "Accepted" if accept else "Rejected"
        append_text(
            self.impact_path,
            f"## {utc_now_iso()} — {decision}\n"
            f"- Proposal: `{proposal.proposal_id or proposal.name}`\n"
            f"- Action: `{proposal.action}`\n"
            f"- Skill: `{proposal.name}`\n"
            f"- Reason: {proposal.reason}\n"
            f"- Patterns: {', '.join(proposal.patterns_addressed)}\n"
            f"- Target: `{target_dir}`\n\n",
        )

    def _resolve_proposal(self, ref: str) -> Path:
        p = Path(ref)
        if p.is_file():
            return p
        # bare id
        for ext in (".json", ".md"):
            candidate = self.proposals_dir / f"{ref}{ext}"
            if candidate.is_file():
                return candidate
        # prefix match
        matches = sorted(self.proposals_dir.glob(f"{ref}*"))
        if matches:
            return matches[0]
        raise FileNotFoundError(f"Proposal not found: {ref}")


def apply_edits(content: str, edits: list[dict[str, str]]) -> str:
    for edit in edits:
        op = edit.get("op")
        if op == "append":
            content = content.rstrip() + "\n" + (edit.get("content") or "")
        elif op == "replace":
            target = edit.get("target") or ""
            if target not in content:
                raise ValueError(f"replace target not found: {target[:80]!r}")
            content = content.replace(target, edit.get("content") or "", 1)
        elif op == "insert_after":
            target = edit.get("target") or ""
            if target not in content:
                raise ValueError(f"insert_after target not found: {target[:80]!r}")
            idx = content.find(target) + len(target)
            content = content[:idx] + (edit.get("content") or "") + content[idx:]
        else:
            raise ValueError(f"Unsupported edit op: {op}")
    return content


# ---------------------------------------------------------------------------
# LLM client (OpenAI-compatible)
# ---------------------------------------------------------------------------

class LlmClient:
    def __init__(self, endpoint: str, api_key: str, model: str, timeout: int = 120):
        self.endpoint = endpoint.rstrip("/")
        self.api_key = api_key
        self.model = model
        self.timeout = timeout

    @classmethod
    def from_env(cls) -> "LlmClient":
        endpoint = os.environ.get("ATHLON_ENDPOINT") or os.environ.get("OPENAI_BASE_URL") or "https://api.openai.com/v1"
        api_key = os.environ.get("ATHLON_API_KEY") or os.environ.get("OPENAI_API_KEY") or ""
        model = os.environ.get("ATHLON_MODEL") or os.environ.get("OPENAI_MODEL") or "gpt-4o-mini"
        if not api_key:
            raise RuntimeError("Set ATHLON_API_KEY or OPENAI_API_KEY for --llm mode")
        return cls(endpoint=endpoint, api_key=api_key, model=model)

    def chat(self, system: str, user: str) -> str:
        url = self.endpoint + "/chat/completions"
        payload = {
            "model": self.model,
            "temperature": 0.2,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
        }
        data = json.dumps(payload).encode("utf-8")
        req = request.Request(
            url,
            data=data,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.api_key}",
            },
            method="POST",
        )
        try:
            with request.urlopen(req, timeout=self.timeout) as resp:
                body = json.loads(resp.read().decode("utf-8"))
        except error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"LLM HTTP {exc.code}: {detail}") from exc
        return body["choices"][0]["message"]["content"]


def extract_json_array(text: str) -> list[Any]:
    text = text.strip()
    # fenced
    m = re.search(r"```(?:json)?\s*(\[.*?\])\s*```", text, re.DOTALL)
    if m:
        return json.loads(m.group(1))
    # raw array
    start = text.find("[")
    end = text.rfind("]")
    if start >= 0 and end > start:
        return json.loads(text[start : end + 1])
    raise ValueError("No JSON array found in model response")


def parse_skill_frontmatter(text: str) -> tuple[str, str]:
    if not text.startswith("---"):
        return "", ""
    end = text.find("\n---", 3)
    if end < 0:
        return "", ""
    fm = text[3:end]
    name = ""
    desc = ""
    for line in fm.splitlines():
        if line.startswith("name:"):
            name = line.split(":", 1)[1].strip().strip("\"'")
        elif line.startswith("description:"):
            desc = line.split(":", 1)[1].strip().strip("\"'")
    return name, desc


def extract_section(markdown: str, heading: str) -> str:
    pattern = rf"##\s+{re.escape(heading)}\s*\n(.*?)(?=\n##\s+|\Z)"
    m = re.search(pattern, markdown, re.DOTALL | re.IGNORECASE)
    return m.group(1).strip() if m else ""


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="WikiSkill offline evolution for Athlon Agent",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    p.add_argument("--athlon-root", type=Path, default=DEFAULT_ATHLON_ROOT, help="Athlon data root (~/.athlon-agent)")
    p.add_argument("--workspace", type=Path, default=None, help="WikiSkill workspace (default: <athlon-root>/wikiskill-workspace)")
    p.add_argument("--llm", action="store_true", help="Use OpenAI-compatible LLM for wiki/propose")

    sub = p.add_subparsers(dest="command", required=True)

    scan = sub.add_parser("scan", help="Scan Athlon logs into Raw Layer")
    scan.add_argument("--max-sessions", type=int, default=50)
    scan.add_argument("--days", type=int, default=None, help="Only sessions newer than N days")

    sub.add_parser("maintain-wiki", help="Consolidate Raw Layer into Wiki patterns")

    propose = sub.add_parser("propose", help="Propose skill create/patch from Wiki")
    propose.add_argument("--max-proposals", type=int, default=3)

    apply = sub.add_parser("apply", help="Apply a proposal into ~/.athlon-agent/skills/")
    apply.add_argument("--proposal", required=True, help="Proposal id or path")
    apply.add_argument("--dry-run", action="store_true")
    apply.add_argument("--reject", action="store_true", help="Record as rejected without applying")

    evolve = sub.add_parser("evolve", help="scan → maintain-wiki → propose (+ optional apply)")
    evolve.add_argument("--max-sessions", type=int, default=50)
    evolve.add_argument("--days", type=int, default=None)
    evolve.add_argument("--max-proposals", type=int, default=3)
    evolve.add_argument("--apply-first", action="store_true", help="Auto-apply the first proposal (use carefully)")
    evolve.add_argument("--dry-run-apply", action="store_true")

    status = sub.add_parser("status", help="Show workspace summary")

    selftest = sub.add_parser("selftest", help="Run synthetic end-to-end pipeline in a temp workspace")
    selftest.add_argument("--keep", action="store_true", help="Keep temp workspace under ./wikiskill-selftest-out")

    return p


def resolve_workspace(args: argparse.Namespace) -> Path:
    if args.workspace:
        return args.workspace
    return Path(args.athlon_root) / "wikiskill-workspace"


def cmd_scan(args: argparse.Namespace) -> int:
    workspace = resolve_workspace(args)
    scanner = AthlonScanner(Path(args.athlon_root), workspace)
    traces = scanner.scan(max_sessions=args.max_sessions, days=args.days)
    print(f"[scan] {len(traces)} traces → {workspace / 'raw'}")
    by = Counter(t.source for t in traces)
    for src, n in by.most_common():
        print(f"  - {src}: {n}")
    failures = sum(t.tool_failures for t in traces)
    corrections = sum(t.corrections for t in traces)
    print(f"  - tool_failures: {failures}, corrections: {corrections}")
    return 0


def cmd_maintain(args: argparse.Namespace) -> int:
    workspace = resolve_workspace(args)
    llm = LlmClient.from_env() if args.llm else None
    maintainer = WikiMaintainer(workspace, use_llm=args.llm, llm=llm)
    patterns = maintainer.maintain()
    print(f"[wiki] {len(patterns)} patterns → {workspace / 'wiki'}")
    for ptn in patterns[:10]:
        print(f"  - [{ptn.kind}] {ptn.name} (count={ptn.count})")
    return 0


def cmd_propose(args: argparse.Namespace) -> int:
    workspace = resolve_workspace(args)
    skills = Path(args.athlon_root) / "skills"
    llm = LlmClient.from_env() if args.llm else None
    proposer = SkillProposer(workspace, skills, use_llm=args.llm, llm=llm)
    proposals = proposer.propose(max_proposals=getattr(args, "max_proposals", 3))
    print(f"[propose] {len(proposals)} proposal(s)")
    return 0


def cmd_apply(args: argparse.Namespace) -> int:
    workspace = resolve_workspace(args)
    skills = Path(args.athlon_root) / "skills"
    app = ProposalApplicator(workspace, skills)
    if args.reject:
        # record rejection only
        path = app._resolve_proposal(args.proposal)
        sidecar = path.with_suffix(".json") if path.suffix == ".md" else path
        data = load_json(sidecar)
        proposal = SkillProposal.from_dict(data)
        append_text(
            app.impact_path,
            f"## {utc_now_iso()} — Rejected\n"
            f"- Proposal: `{proposal.proposal_id or proposal.name}`\n"
            f"- Reason: manual reject via CLI\n\n",
        )
        print("[apply] recorded rejection")
        return 0
    app.apply(args.proposal, dry_run=args.dry_run, accept=True)
    return 0


def cmd_evolve(args: argparse.Namespace) -> int:
    # scan
    args_scan = argparse.Namespace(
        athlon_root=args.athlon_root,
        workspace=args.workspace,
        max_sessions=args.max_sessions,
        days=args.days,
        llm=args.llm,
    )
    cmd_scan(args_scan)
    cmd_maintain(args)
    cmd_propose(args)
    if args.apply_first:
        workspace = resolve_workspace(args)
        proposals = sorted((workspace / "proposals").glob("*.json"), reverse=True)
        if not proposals:
            print("[evolve] no proposals to apply")
            return 0
        apply_args = argparse.Namespace(
            athlon_root=args.athlon_root,
            workspace=args.workspace,
            proposal=str(proposals[0]),
            dry_run=args.dry_run_apply,
            reject=False,
            llm=args.llm,
        )
        return cmd_apply(apply_args)
    print("\n[evolve] Review proposals under workspace/proposals/, then:")
    print("  python tools/wikiskill/evolve.py apply --proposal <id>")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    workspace = resolve_workspace(args)
    raw = workspace / "raw" / "traces"
    wiki = workspace / "wiki" / "patterns"
    proposals = workspace / "proposals"
    skills = Path(args.athlon_root) / "skills"
    print(f"Athlon root : {args.athlon_root}")
    print(f"Workspace   : {workspace}")
    print(f"Raw traces  : {len(list(raw.glob('*.json'))) if raw.is_dir() else 0}")
    print(f"Wiki patterns: {len(list(wiki.glob('*.md'))) if wiki.is_dir() else 0}")
    print(f"Proposals   : {len(list(proposals.glob('*.json'))) if proposals.is_dir() else 0}")
    print(f"Installed skills: {len([p for p in skills.iterdir() if (p / 'SKILL.md').is_file()]) if skills.is_dir() else 0}")
    manifest = workspace / "raw" / "manifest.json"
    if manifest.is_file():
        data = load_json(manifest)
        print(f"Last scan   : {data.get('scannedAt')}")
    return 0


def cmd_selftest(args: argparse.Namespace) -> int:
    """Synthetic Athlon tree → scan → wiki → propose → dry-run apply."""
    import tempfile

    keep = bool(getattr(args, "keep", False))
    base = Path("wikiskill-selftest-out").resolve() if keep else Path(tempfile.mkdtemp(prefix="wikiskill-"))
    if keep and base.exists():
        shutil.rmtree(base)
    athlon = base / ".athlon-agent"
    sessions = athlon / "sessions" / "demo001"
    sessions.mkdir(parents=True)
    (athlon / "skills").mkdir(parents=True)
    (athlon / "training-data").mkdir(parents=True)
    (athlon / "behavior").mkdir(parents=True)

    messages = [
        {"id": "1", "role": "User", "content": "Read src/App.cs"},
        {
            "id": "2",
            "role": "Assistant",
            "content": "",
            "toolCallsJson": json.dumps([{"id": "c1", "name": "file_read", "arguments": {"path": "App.cs"}}]),
        },
        {"id": "3", "role": "Tool", "content": "Error: path not found: App.cs", "parentId": "c1"},
        {"id": "4", "role": "User", "content": "Use the full path src/App.cs under the workspace"},
        {
            "id": "5",
            "role": "Assistant",
            "content": "",
            "toolCallsJson": json.dumps([{"id": "c2", "name": "file_read", "arguments": {"path": "src/App.cs"}}]),
        },
        {"id": "6", "role": "Tool", "content": "using System;\nclass App {}", "parentId": "c2"},
        {"id": "7", "role": "Assistant", "content": "Here is the file."},
    ]
    write_json(
        sessions / "session.json",
        {
            "id": "demo001",
            "title": "selftest",
            "modelName": "demo",
            "activeWorkspace": "/tmp/demo",
            "messages": messages,
        },
    )

    sft = {
        "messages": [
            {"role": "user", "content": "grep TODO"},
            {
                "role": "assistant",
                "content": None,
                "tool_calls": [
                    {
                        "id": "t1",
                        "type": "function",
                        "function": {"name": "grep_files", "arguments": "{\"pattern\":\"TODO\"}"},
                    }
                ],
            },
            {"role": "tool", "content": "Error: invalid regex", "tool_call_id": "t1"},
            {"role": "user", "content": "escape the pattern properly"},
            {
                "role": "assistant",
                "content": None,
                "tool_calls": [
                    {
                        "id": "t2",
                        "type": "function",
                        "function": {"name": "grep_files", "arguments": "{\"pattern\":\"TODO\"}"},
                    }
                ],
            },
            {"role": "tool", "content": "src/a.cs:1: // TODO", "tool_call_id": "t2"},
        ],
        "metadata": {
            "source": "agent-correction",
            "sessionId": "sft-demo",
            "hasCorrection": True,
            "model": "demo",
        },
    }
    with (athlon / "training-data" / "sft-traces-2099-01-01.jsonl").open("w", encoding="utf-8") as fh:
        fh.write(json.dumps(sft, ensure_ascii=False) + "\n")

    workspace = athlon / "wikiskill-workspace"
    ns = argparse.Namespace(
        athlon_root=athlon,
        workspace=workspace,
        llm=False,
        max_sessions=10,
        days=None,
        max_proposals=3,
        apply_first=False,
        dry_run_apply=False,
    )
    print(f"[selftest] athlon root = {athlon}")
    rc = cmd_evolve(ns)
    proposals = list((workspace / "proposals").glob("*.json"))
    patterns = list((workspace / "wiki" / "patterns").glob("*.md"))
    print(f"[selftest] patterns={len(patterns)} proposals={len(proposals)}")
    if not patterns or not proposals:
        print("[selftest] FAILED: expected patterns and proposals", file=sys.stderr)
        return 1
    apply_ns = argparse.Namespace(
        athlon_root=athlon,
        workspace=workspace,
        proposal=str(proposals[0]),
        dry_run=True,
        reject=False,
        llm=False,
    )
    cmd_apply(apply_ns)
    print("[selftest] OK")
    if keep:
        print(f"[selftest] kept output at {base}")
    else:
        shutil.rmtree(base, ignore_errors=True)
    return rc


def main(argv: Optional[list[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    workspace = resolve_workspace(args)
    workspace.mkdir(parents=True, exist_ok=True)
    args.workspace = workspace

    commands = {
        "scan": cmd_scan,
        "maintain-wiki": cmd_maintain,
        "propose": cmd_propose,
        "apply": cmd_apply,
        "evolve": cmd_evolve,
        "status": cmd_status,
        "selftest": cmd_selftest,
    }
    try:
        return commands[args.command](args)
    except Exception as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
