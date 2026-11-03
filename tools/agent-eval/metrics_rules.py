"""Deterministic metrics: zero cost, reproducible, no model involved.

These run on every trace, including ones that the semantic judge must skip, so a
structurally broken turn still shows up as a failure instead of disappearing.
"""

from __future__ import annotations

import json
import re
from typing import Any, Mapping, Sequence

from trace_model import (
    FLAG_COMPACTED,
    Metric,
    MetricResult,
    Status,
    Trace,
)

# An assistant turn that also emitted a tool call should not dump the raw
# arguments/JSON envelope into the visible text.
_RAW_JSON_RE = re.compile(r"\{\s*\"(?:tool_calls|function|name|arguments)\"\s*:", re.IGNORECASE)


class _Rule(Metric):
    kind = "rule"

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        raise NotImplementedError

    def evaluate(self, trace: Trace) -> MetricResult:
        pre = self.precheck(trace)
        if pre is not None:
            return pre
        try:
            return self.reduce({}, trace)
        except Exception as error:  # pragma: no cover - defensive
            return MetricResult.error(f"RULE_ERROR:{type(error).__name__}")


def _records(trace: Trace) -> list[Mapping[str, Any]]:
    return [record for record in trace.list("tool_calls") if isinstance(record, Mapping)]


def _decide(passed: bool, reason: str, evidence: Mapping[str, Any] | None = None) -> MetricResult:
    return MetricResult(
        Status.DECIDED,
        reason,
        passed=passed,
        evidence=evidence or {},
    )


class ToolArgsValid(_Rule):
    id = "tool_args_valid"
    question = "Every tool call carries JSON-object arguments."

    def precheck(self, trace: Trace) -> MetricResult | None:
        if not _records(trace):
            return MetricResult.skipped("NO_TOOL_CALLS")
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        broken = [
            record.get("name")
            for record in _records(trace)
            if record.get("arguments_error")
        ]
        return _decide(
            not broken,
            "ARGS_OK" if not broken else "INVALID_ARGUMENTS",
            {"invalidCount": len(broken), "tools": broken[:10]},
        )


class ToolCallPaired(_Rule):
    id = "tool_call_paired"
    question = "Every tool call received a persisted result."

    def precheck(self, trace: Trace) -> MetricResult | None:
        if not _records(trace):
            return MetricResult.skipped("NO_TOOL_CALLS")
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        pending = [
            record.get("name")
            for record in _records(trace)
            if record.get("result_state") is None
        ]
        return _decide(
            not pending,
            "ALL_PAIRED" if not pending else "PENDING_CALLS",
            {"pendingCount": len(pending), "tools": pending[:10]},
        )


class NoRepeatToolCall(_Rule):
    id = "no_repeat_tool_call"
    question = "No identical tool call is repeated inside the turn."

    def precheck(self, trace: Trace) -> MetricResult | None:
        if len(_records(trace)) < 2:
            # One call cannot loop; skipping keeps the pass rate honest.
            return MetricResult.skipped("TOO_FEW_CALLS")
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        seen: dict[str, str] = {}
        repeats = 0
        for record in _records(trace):
            signature = json.dumps(
                {"name": record.get("name"), "arguments": record.get("arguments")},
                ensure_ascii=False,
                sort_keys=True,
                default=str,
            )
            if signature in seen:
                repeats += 1
            else:
                seen[signature] = str(record.get("name"))
        return _decide(
            repeats == 0,
            "NO_REPEAT" if repeats == 0 else "REPEATED_CALLS",
            {"repeatCount": repeats, "totalCalls": len(_records(trace))},
        )


class FirstToolSuccess(_Rule):
    id = "first_tool_success"
    question = "The first tool call of the turn succeeded."

    def precheck(self, trace: Trace) -> MetricResult | None:
        if not _records(trace):
            return MetricResult.skipped("NO_TOOL_CALLS")
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        first = _records(trace)[0]
        state = str(first.get("result_state") or "unknown")
        return _decide(
            state == "success",
            "FIRST_CALL_OK" if state == "success" else "FIRST_CALL_FAILED",
            {"tool": first.get("name"), "resultState": state},
        )


class NoRawJsonLeak(_Rule):
    id = "no_raw_json_leak"
    question = "Assistant text does not leak the raw tool-call JSON."

    def precheck(self, trace: Trace) -> MetricResult | None:
        if not _records(trace):
            return MetricResult.skipped("NO_TOOL_CALLS")
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        offenders: list[int] = []
        for index, message in enumerate(trace.list("messages")):
            if not isinstance(message, Mapping):
                continue
            if str(message.get("role")) != "Assistant":
                continue
            text = str(message.get("text") or "")
            if text and _RAW_JSON_RE.search(text):
                offenders.append(index)
        return _decide(
            not offenders,
            "NO_LEAK" if not offenders else "RAW_JSON_IN_TEXT",
            {"messageIndexes": offenders[:10]},
        )


class NoEmptyFinalAnswer(_Rule):
    id = "no_empty_final_answer"
    question = "The turn produced a user-visible answer."

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        has_answer = bool(trace.final_answer.strip())
        return _decide(
            has_answer,
            "ANSWER_PRESENT" if has_answer else "NO_FINAL_ANSWER",
            {"issuePresent": "EMPTY_FINAL_ANSWER" in [issue.value for issue in trace.issues]},
        )


class CompactionInTurn(_Rule):
    """A signal, not a pass/fail: it measures context pressure per turn."""

    id = "compaction_in_turn"
    question = "(signal) The turn required context compaction."

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        compacted = FLAG_COMPACTED in trace.flags
        return MetricResult.signal(
            "COMPACTED" if compacted else "CLEAN",
            "CONTEXT_COMPACTED" if compacted else "NO_COMPACTION",
        )


RULE_METRICS: tuple[_Rule, ...] = (
    ToolArgsValid(),
    ToolCallPaired(),
    NoRepeatToolCall(),
    FirstToolSuccess(),
    NoRawJsonLeak(),
    NoEmptyFinalAnswer(),
    CompactionInTurn(),
)

RULE_METRIC_IDS: tuple[str, ...] = tuple(metric.id for metric in RULE_METRICS)


def evaluate_rules(trace: Trace, metrics: Sequence[_Rule] | None = None) -> dict[str, MetricResult]:
    selected = metrics or RULE_METRICS
    return {metric.id: metric.evaluate(trace) for metric in selected}
