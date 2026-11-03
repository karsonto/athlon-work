"""Evaluation orchestration: rules first, then one packed judge call per trace.

The budget and failure isolation mirror ``JevTraceEvaluator``: a single trace gets
one time budget covering retries, a backend failure turns every pending semantic
metric into ``ERROR`` for that trace only, and billable usage is recorded even
when the reply is rejected.
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping, Sequence

from judge import Judge, JudgeError
from metrics_llm import SEMANTIC_METRICS, SemanticMetric, questions_for
from metrics_rules import RULE_METRICS, evaluate_rules
from trace_model import Issue, MetricResult, Trace

TOOL_VERSION = "1"


@dataclass
class Usage:
    prompt_tokens: int = 0
    completion_tokens: int = 0

    def add(self, usage: Mapping[str, int] | None) -> None:
        if not usage:
            return
        self.prompt_tokens += int(usage.get("promptTokens") or 0)
        self.completion_tokens += int(usage.get("completionTokens") or 0)

    def to_dict(self) -> dict[str, Any]:
        return {
            "promptTokens": self.prompt_tokens,
            "completionTokens": self.completion_tokens,
            "totalTokens": self.prompt_tokens + self.completion_tokens,
        }


@dataclass
class Entry:
    trace: Trace
    results: dict[str, MetricResult]

    def to_dict(self) -> dict[str, Any]:
        return {
            **self.trace.signature(),
            "issues": [issue.value for issue in self.trace.issues],
            "flags": list(self.trace.flags),
            "results": {key: value.to_dict() for key, value in self.results.items()},
        }

    @staticmethod
    def from_dict(payload: Mapping[str, Any]) -> "Entry":
        results = {
            key: MetricResult.from_dict(value)
            for key, value in (payload.get("results") or {}).items()
        }
        trace = Trace(
            id=str(payload.get("id") or f"unknown:{payload.get('sessionId')}:{payload.get('turnIndex')}"),
            fields={
                "session_id": payload.get("sessionId"),
                "turn_index": payload.get("turnIndex"),
                "request": payload.get("request") or "",
            },
            issues=tuple(Issue(value) for value in payload.get("issues") or ()),
            flags=tuple(payload.get("flags") or ()),
        )
        return Entry(trace=trace, results=results)


@dataclass
class RunConfig:
    with_rules: bool = True
    with_semantics: bool = True
    limit: int | None = None
    judge_metrics: Sequence[SemanticMetric] = field(default_factory=lambda: SEMANTIC_METRICS)

    @staticmethod
    def from_mapping(payload: Mapping[str, Any]) -> "RunConfig":
        return RunConfig(
            with_rules=bool(payload.get("rules", True)),
            with_semantics=bool(payload.get("semantics", True)),
            limit=payload.get("limit"),
        )


@dataclass
class Results:
    entries: list[Entry]
    metric_meta: list[dict[str, Any]]
    usage: Usage
    judge_info: dict[str, Any]
    started_at: str
    elapsed_ms: int
    trace_count: int
    judge_requests: int = 0
    judge_errors: int = 0

    def to_dict(self) -> dict[str, Any]:
        return {
            "meta": {
                "toolVersion": TOOL_VERSION,
                "generatedAt": self.started_at,
                "elapsedMs": self.elapsed_ms,
                "traceCount": self.trace_count,
                "evaluatedCount": len(self.entries),
                "judgeRequests": self.judge_requests,
                "judgeErrors": self.judge_errors,
                "judge": self.judge_info,
                "usage": self.usage.to_dict(),
            },
            "metrics": self.metric_meta,
            "traces": [entry.to_dict() for entry in self.entries],
        }

    @staticmethod
    def from_dict(payload: Mapping[str, Any]) -> "Results":
        meta = payload.get("meta") or {}
        usage_payload = meta.get("usage") or {}
        return Results(
            entries=[Entry.from_dict(item) for item in payload.get("traces") or ()],
            metric_meta=list(payload.get("metrics") or ()),
            usage=Usage(
                prompt_tokens=int(usage_payload.get("promptTokens") or 0),
                completion_tokens=int(usage_payload.get("completionTokens") or 0),
            ),
            judge_info=dict(meta.get("judge") or {}),
            started_at=str(meta.get("generatedAt") or ""),
            elapsed_ms=int(meta.get("elapsedMs") or 0),
            trace_count=int(meta.get("traceCount") or len(payload.get("traces") or ())),
            judge_requests=int(meta.get("judgeRequests") or 0),
            judge_errors=int(meta.get("judgeErrors") or 0),
        )


def run(
    traces: Sequence[Trace],
    *,
    config: RunConfig,
    judge: Judge | None = None,
) -> Results:
    started = time.time()
    started_iso = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    selected = list(traces[: config.limit]) if config.limit else list(traces)
    rules = RULE_METRICS if config.with_rules else ()
    semantics = config.judge_metrics if (config.with_semantics and judge is not None) else ()

    entries: list[Entry] = []
    usage = Usage()
    judge_requests = 0
    judge_errors = 0

    for trace in selected:
        results: dict[str, MetricResult] = {}

        if rules:
            results.update(evaluate_rules(trace, rules))

        if config.with_semantics and judge is None:
            for metric in config.judge_metrics:
                results[metric.id] = MetricResult.skipped("NO_JUDGE")
        elif semantics:
            questions, terminal = questions_for(semantics, trace)
            results.update(terminal)
            if trace.blocking_issues:
                for metric in semantics:
                    results.setdefault(
                        metric.id,
                        MetricResult.skipped(
                            "INCOMPLETE_TRACE",
                            {"issues": [issue.value for issue in trace.blocking_issues]},
                        ),
                    )
            elif questions:
                judge_requests += 1
                try:
                    outcome = judge.evaluate(trace, questions)
                except JudgeError as error:
                    judge_errors += 1
                    usage.add(error.usage)
                    for metric in semantics:
                        if metric.id in questions:
                            results[metric.id] = MetricResult.error(
                                error.reason, {"detail": error.detail[:200]}
                            )
                else:
                    usage.add(outcome.usage)
                    for metric in semantics:
                        if metric.id not in questions:
                            continue
                        answers = {
                            "verdict": outcome.verdicts.get(metric.id),
                            "reason": outcome.reasons.get(metric.id, ""),
                        }
                        try:
                            results[metric.id] = metric.reduce(answers, trace)
                        except Exception as error:  # noqa: BLE001 - isolate per metric
                            results[metric.id] = MetricResult.error(
                                "REDUCE_ERROR", {"detail": type(error).__name__}
                            )

        entries.append(Entry(trace=trace, results=results))

    metric_meta = [
        {"id": metric.id, "version": metric.version, "kind": metric.kind}
        for metric in (*rules, *semantics)
    ]
    elapsed_ms = int((time.time() - started) * 1000)
    return Results(
        entries=entries,
        metric_meta=metric_meta,
        usage=usage,
        judge_info=dict(judge.describe()) if judge is not None else {"backend": "none"},
        started_at=started_iso,
        elapsed_ms=elapsed_ms,
        trace_count=len(selected),
        judge_requests=judge_requests,
        judge_errors=judge_errors,
    )


def write_results(path: Path, results: Results) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(results.to_dict(), ensure_ascii=False, indent=2, default=str) + "\n",
        encoding="utf-8",
    )


def read_results(path: Path) -> Results:
    return Results.from_dict(json.loads(path.read_text(encoding="utf-8")))
