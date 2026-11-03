"""Core evaluation data model.

The shapes mirror the JEV evaluation layer in ``agentscope-java`` so the same
semantics survive a future backend swap:

* :class:`Trace`       ~ ``io.agentscope.extensions.judge.jev.evaluation.JevTrace``
* :class:`MetricResult` ~ ``JevMetricResult``
* :class:`Metric`      ~ ``JevTraceMetric`` (``precheck`` / ``state`` / ``questions`` / ``reduce``)

Only the verdict vocabulary differs: a chat LLM cannot emit calibrated
probabilities, so metrics return ``yes`` / ``no`` / ``unsure`` instead of a
number. See ``docs`` in README for why that is not a loss.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Iterable, Mapping


# --------------------------------------------------------------------------- #
# Trace
# --------------------------------------------------------------------------- #


class Issue(str, Enum):
    """Trace problems detected while rebuilding a turn from persisted messages.

    ``BLOCKING_ISSUES`` never reach the semantic judge: the turn is structurally
    incomplete, so a verdict about its grounding would be fabricated evidence.
    """

    AMBIGUOUS_CALL_ID = "AMBIGUOUS_CALL_ID"
    UNPAIRED_TOOL_RESULT = "UNPAIRED_TOOL_RESULT"
    PENDING_TOOL_CALL = "PENDING_TOOL_CALL"
    EMPTY_FINAL_ANSWER = "EMPTY_FINAL_ANSWER"


BLOCKING_ISSUES: frozenset[Issue] = frozenset(
    {
        Issue.AMBIGUOUS_CALL_ID,
        Issue.UNPAIRED_TOOL_RESULT,
        Issue.PENDING_TOOL_CALL,
        Issue.EMPTY_FINAL_ANSWER,
    }
)

FLAG_TRUNCATED_TOOL_OUTPUT = "TRUNCATED_TOOL_OUTPUT"
FLAG_COMPACTED = "COMPACTED"
FLAG_MAX_TOOL_ROUNDS = "MAX_TOOL_ROUNDS"


@dataclass(frozen=True)
class ToolCall:
    """A single assistant tool call, before its result is known."""

    id: str
    name: str
    arguments: Mapping[str, Any]
    raw_arguments: str | None = None
    arguments_error: str | None = None

    @property
    def arguments_ok(self) -> bool:
        return self.arguments_error is None

    def fingerprint(self) -> str:
        """Stable signature used to detect loops and repeated calls."""
        payload = json.dumps(
            {"name": self.name, "arguments": self.arguments},
            ensure_ascii=False,
            sort_keys=True,
            default=str,
        )
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:16]


@dataclass(frozen=True)
class ToolResult:
    """The persisted outcome of one tool call."""

    call_id: str
    name: str
    text: str
    state: str  # "success" | "failure" | "unknown"
    summary: str = ""
    truncated: bool = False


@dataclass(frozen=True)
class CallRecord:
    """A call paired with its result, matching JEV's ``tool_calls`` entries."""

    call: ToolCall
    result: ToolResult | None
    result_message_index: int | None

    def to_dict(self) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "id": self.call.id,
            "name": self.call.name,
            "arguments": dict(self.call.arguments),
        }
        if self.call.arguments_error:
            payload["arguments_error"] = self.call.arguments_error
        if self.result is not None:
            payload["result"] = self.result.text
            payload["result_state"] = self.result.state
            payload["result_summary"] = self.result.summary
            payload["result_message_index"] = self.result_message_index
            if self.result.truncated:
                payload["result_truncated"] = True
        return payload


@dataclass(frozen=True)
class Trace:
    """A detached, turn-scoped trace.

    Message metadata, reasoning and image payloads are deliberately not kept:
    they are not evaluation evidence and inflate the judge request.
    """

    id: str
    fields: Mapping[str, Any]
    issues: tuple[Issue, ...] = ()
    flags: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        if not self.id:
            raise ValueError("trace id required")

    # -- JevTrace-compatible helpers ------------------------------------- #

    def has(self, key: str) -> bool:
        value = self.fields.get(key)
        return value is not None and (not isinstance(value, str) or bool(value.strip()))

    def text(self, key: str) -> str:
        value = self.fields.get(key)
        return value if isinstance(value, str) else ""

    def list(self, key: str) -> list[Any]:
        value = self.fields.get(key)
        return list(value) if isinstance(value, list) else []

    def with_field(self, key: str, value: Any) -> "Trace":
        merged = dict(self.fields)
        merged[key] = value
        return Trace(self.id, merged, self.issues, self.flags)

    # -- typed accessors -------------------------------------------------- #

    @property
    def request(self) -> str:
        return self.text("request")

    @property
    def final_answer(self) -> str:
        return self.text("final_answer")

    @property
    def records(self) -> list[Any]:
        return self.list("tool_calls")

    @property
    def tool_results(self) -> list[Any]:
        return self.list("tool_results")

    @property
    def blocking_issues(self) -> tuple[Issue, ...]:
        return tuple(issue for issue in self.issues if issue in BLOCKING_ISSUES)

    def signature(self) -> dict[str, Any]:
        """Small, stable identifier set used by offline replays and diffs."""
        return {
            "id": self.id,
            "sessionId": self.fields.get("session_id"),
            "turnIndex": self.fields.get("turn_index"),
            "request": self.request[:200],
        }

    def to_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "fields": _jsonable(self.fields),
            "issues": [issue.value for issue in self.issues],
            "flags": list(self.flags),
        }

    @staticmethod
    def from_dict(payload: Mapping[str, Any]) -> "Trace":
        return Trace(
            id=str(payload["id"]),
            fields=dict(payload.get("fields") or {}),
            issues=tuple(Issue(value) for value in payload.get("issues") or ()),
            flags=tuple(payload.get("flags") or ()),
        )


# --------------------------------------------------------------------------- #
# Metric results
# --------------------------------------------------------------------------- #


class Status(str, Enum):
    DECIDED = "DECIDED"
    INCONCLUSIVE = "INCONCLUSIVE"
    SKIPPED = "SKIPPED"
    ERROR = "ERROR"


#: The wire vocabulary is yes/no/unsure (see ``judge.SYSTEM_PROMPT``); the stored
#: vocabulary is pass/fail/unsure so reports read naturally.
_VERDICT_ALIASES = {
    "yes": "pass",
    "true": "pass",
    "pass": "pass",
    "no": "fail",
    "false": "fail",
    "fail": "fail",
    "unsure": "unsure",
    "unknown": "unsure",
}


class Verdict(str, Enum):
    PASS = "pass"
    FAIL = "fail"
    UNSURE = "unsure"

    @staticmethod
    def parse(value: Any) -> "Verdict":
        if isinstance(value, Verdict):
            return value
        if not isinstance(value, str):
            raise ValueError(f"verdict must be a string, got {type(value).__name__}")
        normalized = _VERDICT_ALIASES.get(value.strip().casefold())
        if normalized is None:
            raise ValueError(f"verdict must be one of yes/no/unsure, got {value!r}")
        return Verdict(normalized)


@dataclass(frozen=True)
class MetricResult:
    """Outcome of one metric on one trace.

    ``score`` stays ``None`` for LLM-backed metrics: a chat model's self-reported
    confidence is not a measured probability, and storing it as a score would
    imply precision the backend does not have.
    """

    status: Status
    reason: str
    verdict: Verdict | None = None
    score: float | None = None
    passed: bool | None = None
    label: str | None = None
    evidence: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.reason:
            raise ValueError("reason required")
        if self.score is not None and not (0.0 <= self.score <= 1.0):
            raise ValueError("score must be in [0,1]")
        if self.passed is not None and self.status is not Status.DECIDED:
            raise ValueError("only DECIDED results may carry passed")
        if self.verdict is Verdict.PASS and self.passed is False:
            raise ValueError("verdict pass contradicts passed=false")
        if self.verdict is Verdict.FAIL and self.passed is True:
            raise ValueError("verdict fail contradicts passed=true")
        if self.verdict is Verdict.UNSURE and self.status is not Status.INCONCLUSIVE:
            raise ValueError("unsure verdict must map to INCONCLUSIVE")

    # -- constructors ----------------------------------------------------- #

    @staticmethod
    def skipped(reason: str, evidence: Mapping[str, Any] | None = None) -> "MetricResult":
        return MetricResult(Status.SKIPPED, reason, evidence=evidence or {})

    @staticmethod
    def error(reason: str, evidence: Mapping[str, Any] | None = None) -> "MetricResult":
        return MetricResult(Status.ERROR, reason, evidence=evidence or {})

    @staticmethod
    def from_verdict(
        verdict: Verdict,
        reason: str,
        evidence: Mapping[str, Any] | None = None,
    ) -> "MetricResult":
        evidence = evidence or {}
        if verdict is Verdict.UNSURE:
            return MetricResult(Status.INCONCLUSIVE, reason, verdict=verdict, evidence=evidence)
        return MetricResult(
            Status.DECIDED,
            reason,
            verdict=verdict,
            passed=verdict is Verdict.PASS,
            evidence=evidence,
        )

    @staticmethod
    def signal(label: str, reason: str, evidence: Mapping[str, Any] | None = None) -> "MetricResult":
        """A measurement with no pass/fail meaning, e.g. "was context compacted?"."""
        return MetricResult(Status.DECIDED, reason, label=label, evidence=evidence or {})

    # -- derived ---------------------------------------------------------- #

    @property
    def counted(self) -> bool:
        """Whether this result belongs in a pass-rate denominator."""
        return self.status is Status.DECIDED and self.passed is not None

    def to_dict(self) -> dict[str, Any]:
        return {
            "status": self.status.value,
            "reason": self.reason,
            "verdict": self.verdict.value if self.verdict else None,
            "score": self.score,
            "passed": self.passed,
            "label": self.label,
            "evidence": _jsonable(self.evidence),
        }

    @staticmethod
    def from_dict(payload: Mapping[str, Any]) -> "MetricResult":
        verdict = payload.get("verdict")
        return MetricResult(
            status=Status(payload["status"]),
            reason=payload.get("reason") or "",
            verdict=Verdict.parse(verdict) if verdict else None,
            score=payload.get("score"),
            passed=payload.get("passed"),
            label=payload.get("label"),
            evidence=payload.get("evidence") or {},
        )


# --------------------------------------------------------------------------- #
# Metric contract
# --------------------------------------------------------------------------- #


class Metric:
    """A reusable metric definition.

    ``precheck`` returns a terminal result (``SKIPPED``) or ``None`` to continue,
    matching ``JevTraceMetric.precheck``.
    """

    id: str = ""
    version: str = "v1"
    kind: str = "rule"  # "rule" | "llm"
    question: str = ""
    positive: bool = True

    def precheck(self, trace: Trace) -> MetricResult | None:  # noqa: D401
        return None

    def reduce(self, answers: Mapping[str, Any], trace: Trace) -> MetricResult:
        raise NotImplementedError


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #


def _jsonable(value: Any) -> Any:
    if isinstance(value, Mapping):
        return {str(key): _jsonable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [_jsonable(item) for item in value]
    if isinstance(value, Enum):
        return value.value
    return value


def estimate_chars(value: Any) -> int:
    return len(json.dumps(_jsonable(value), ensure_ascii=False, default=str))


def truncate_middle(text: str, limit: int) -> str:
    """Keep head and tail; the middle is what a judge can do without."""
    if limit <= 0 or len(text) <= limit:
        return text
    if limit <= 64:
        return text[:limit]
    head = limit * 2 // 3
    tail = limit - head - len("\n...[middle omitted]...\n")
    if tail <= 0:
        return text[:limit]
    return f"{text[:head]}\n...[middle omitted]...\n{text[-tail:]}"


def dedupe(items: Iterable[str]) -> tuple[str, ...]:
    seen: dict[str, None] = {}
    for item in items:
        seen.setdefault(item, None)
    return tuple(seen)


__all__ = [
    "BLOCKING_ISSUES",
    "CallRecord",
    "FLAG_COMPACTED",
    "FLAG_MAX_TOOL_ROUNDS",
    "FLAG_TRUNCATED_TOOL_OUTPUT",
    "Issue",
    "Metric",
    "MetricResult",
    "Status",
    "ToolCall",
    "ToolResult",
    "Trace",
    "Verdict",
    "dedupe",
    "estimate_chars",
    "truncate_middle",
]
