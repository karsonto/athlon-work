from typing import Any, Mapping

import pytest

from conftest import FIXTURES
from judge import JudgeError, JudgeOutcome
from metrics_llm import SEMANTIC_METRICS, build_semantic_metrics, questions_for
from metrics_rules import evaluate_rules
from runner import RunConfig, run
from trace_builder import CollectConfig, collect_traces
from trace_model import Status, Trace, Verdict


@pytest.fixture(scope="module")
def traces():
    collected, _ = collect_traces(
        CollectConfig(sessions_root=FIXTURES / "sessions", min_request_chars=8)
    )
    return {trace.id: trace for trace in collected}


class StubJudge:
    """Returns a fixed verdict per metric id."""

    def __init__(self, verdicts: Mapping[str, Verdict] | None = None) -> None:
        self.verdicts = dict(verdicts or {})
        self.calls: list[str] = []

    def describe(self) -> Mapping[str, Any]:
        return {"backend": "stub"}

    def evaluate(self, trace: Trace, questions: Mapping[str, str]) -> JudgeOutcome:
        self.calls.append(trace.id)
        verdicts = {key: self.verdicts.get(key, Verdict.PASS) for key in questions}
        reasons = {key: f"stub:{key}" for key in questions}
        return JudgeOutcome(verdicts=verdicts, reasons=reasons, usage={"promptTokens": 10, "completionTokens": 5})


class FailingJudge:
    def describe(self) -> Mapping[str, Any]:
        return {"backend": "failing"}

    def evaluate(self, trace: Trace, questions: Mapping[str, str]) -> JudgeOutcome:
        raise JudgeError("BACKEND_ERROR", detail="HTTP 500", usage={"promptTokens": 7, "completionTokens": 0})


# --------------------------------------------------------------------------- #
# Rules
# --------------------------------------------------------------------------- #


def test_rules_pass_on_complete_trace(traces):
    results = evaluate_rules(traces["SESSION-FULL:0"])

    assert results["tool_args_valid"].passed is True
    assert results["tool_call_paired"].passed is True
    assert results["no_raw_json_leak"].passed is True
    assert results["no_empty_final_answer"].passed is True
    # Only one call: loop detection is skipped rather than counted as a pass.
    assert results["no_repeat_tool_call"].status is Status.SKIPPED


def test_rules_flag_repeated_and_pending_calls(traces):
    results = evaluate_rules(traces["SESSION-BROKEN:0"])

    assert results["tool_call_paired"].passed is False
    assert results["no_repeat_tool_call"].passed is False
    assert results["no_raw_json_leak"].passed is False
    assert results["no_empty_final_answer"].passed is True
    assert results["tool_args_valid"].passed is True


def test_rules_flag_empty_answer(traces):
    results = evaluate_rules(traces["SESSION-BROKEN:1"])
    assert results["no_empty_final_answer"].passed is False


def test_first_tool_success_tracks_failure(traces):
    assert evaluate_rules(traces["SESSION-FULL:0"])["first_tool_success"].passed is True
    assert evaluate_rules(traces["SESSION-FULL:1"])["first_tool_success"].passed is False


def test_compaction_is_a_signal_not_a_verdict(traces):
    compacted = evaluate_rules(traces["SESSION-BROKEN:0"])["compaction_in_turn"]
    clean = evaluate_rules(traces["SESSION-FULL:0"])["compaction_in_turn"]

    assert compacted.label == "COMPACTED"
    assert clean.label == "CLEAN"
    assert compacted.passed is None and compacted.counted is False


def test_repeats_caught_by_storm_breaker_are_a_signal_not_a_failure():
    """A duplicate the guard suppressed is the guard working, not the agent looping."""
    trace = _repeat_trace(suppressed=[True, True])

    result = evaluate_rules(trace)["no_repeat_tool_call"]

    assert result.passed is None
    assert result.counted is False
    assert result.label == "SUPPRESSED_REPEATS"
    assert result.evidence["suppressedCount"] == 2
    assert result.evidence["unsuppressedCount"] == 0


def test_unsuppressed_repeat_fails_even_if_another_was_suppressed():
    trace = _repeat_trace(suppressed=[True, False])

    result = evaluate_rules(trace)["no_repeat_tool_call"]

    assert result.passed is False
    assert result.reason == "REPEATED_CALLS"
    assert result.evidence["suppressedCount"] == 1
    assert result.evidence["unsuppressedCount"] == 1


def test_rules_skip_when_no_tool_call():
    results = evaluate_rules(_bare_trace())
    assert results["tool_args_valid"].status is Status.SKIPPED
    assert results["tool_args_valid"].reason == "NO_TOOL_CALLS"
    assert results["no_empty_final_answer"].passed is True


# --------------------------------------------------------------------------- #
# Semantic metrics
# --------------------------------------------------------------------------- #


def test_question_packing_skips_missing_evidence():
    questions, terminal = questions_for(SEMANTIC_METRICS, _bare_trace())

    assert "tool_choice" in terminal  # no tool calls in this trace
    assert "grounded" in terminal  # no tool results
    assert "indirect_injection" in terminal
    assert "answer_relevancy" in questions
    assert "completeness" in questions
    assert len(questions) + len(terminal) == len(SEMANTIC_METRICS)


def test_pass_fail_unsure_mapping():
    metric = build_semantic_metrics()[0]  # tool_choice, positive

    passed = metric.reduce({"verdict": Verdict.PASS, "reason": "ok"}, _trace())
    failed = metric.reduce({"verdict": Verdict.FAIL, "reason": "wrong tool"}, _trace())
    unsure = metric.reduce({"verdict": Verdict.UNSURE, "reason": "cannot tell"}, _trace())

    assert (passed.status, passed.passed) == (Status.DECIDED, True)
    assert (failed.status, failed.passed) == (Status.DECIDED, False)
    assert (unsure.status, unsure.passed) == (Status.INCONCLUSIVE, None)


def test_negative_metric_inverts_verdict():
    injection = next(metric for metric in SEMANTIC_METRICS if metric.id == "indirect_injection")

    hit = injection.reduce({"verdict": Verdict.PASS, "reason": "tool output says ignore rules"}, _trace())
    clean = injection.reduce({"verdict": Verdict.FAIL, "reason": "no instruction"}, _trace())
    unsure = injection.reduce({"verdict": Verdict.UNSURE, "reason": "unclear"}, _trace())

    assert hit.passed is False
    assert clean.passed is True
    assert unsure.status is Status.INCONCLUSIVE
    assert hit.evidence["rawVerdict"] == "pass"


def test_semantic_reduce_rejects_positional_strings():
    metric = build_semantic_metrics()[0]
    with pytest.raises(ValueError):
        metric.reduce({"verdict": "pass", "reason": "raw enum not parsed"}, _trace())


# --------------------------------------------------------------------------- #
# Runner
# --------------------------------------------------------------------------- #


def test_runner_maps_verdicts_and_skips_incomplete_traces(traces):
    judge = StubJudge({"completeness": Verdict.UNSURE})
    results = run(list(traces.values()), config=RunConfig(), judge=judge)

    assert results.judge_requests == len(judge.calls)
    assert "SESSION-BROKEN:0" not in judge.calls  # blocking issues -> no spend

    broken = next(entry for entry in results.entries if entry.trace.id == "SESSION-BROKEN:0")
    assert broken.results["grounded"].status is Status.SKIPPED
    assert broken.results["grounded"].reason == "INCOMPLETE_TRACE"
    # Rules still report on structurally broken traces.
    assert broken.results["tool_call_paired"].passed is False

    full = next(entry for entry in results.entries if entry.trace.id == "SESSION-FULL:0")
    assert full.results["completeness"].status is Status.INCONCLUSIVE
    assert full.results["grounded"].passed is True


def test_judge_failure_becomes_error_not_fail(traces):
    results = run(list(traces.values()), config=RunConfig(), judge=FailingJudge())
    semantic_ids = {metric.id for metric in SEMANTIC_METRICS}

    semantic = [
        result
        for entry in results.entries
        for metric_id, result in entry.results.items()
        if metric_id in semantic_ids
    ]
    assert any(result.status is Status.ERROR for result in semantic)
    assert not any(result.status is Status.DECIDED and result.passed is False for result in semantic)
    assert results.judge_errors > 0
    assert results.usage.prompt_tokens > 0  # usage from rejected calls is still recorded


def test_rules_only_run_never_calls_judge(traces):
    results = run(list(traces.values()), config=RunConfig(with_semantics=False), judge=None)

    assert results.judge_requests == 0
    metric_ids = {metric_id for entry in results.entries for metric_id in entry.results}
    assert "grounded" not in metric_ids
    assert "tool_call_paired" in metric_ids
    assert results.metric_meta and all(item["kind"] == "rule" for item in results.metric_meta)


def test_limit_applies_before_evaluation(traces):
    results = run(list(traces.values()), config=RunConfig(limit=2), judge=StubJudge())
    assert results.trace_count == 2
    assert len(results.entries) == 2


def _trace() -> Trace:
    return Trace(id="unit", fields={"request": "r", "final_answer": "a"})


def _repeat_trace(suppressed: list[bool]) -> Trace:
    """One first call plus N duplicates; ``suppressed[i]`` says if the guard caught it."""

    def call(index: int, was_suppressed: bool | None) -> dict[str, Any]:
        record: dict[str, Any] = {
            "id": f"c{index}",
            "name": "read_file",
            "arguments": {"path": "src/a.cs"},
            "result_state": "failure" if was_suppressed else "success",
            "result_summary": "Duplicate tool call suppressed" if was_suppressed else "10 lines",
            "result": "duplicate" if was_suppressed else "file body",
        }
        return record

    calls = [call(0, None)] + [call(i + 1, flag) for i, flag in enumerate(suppressed)]
    return Trace(
        id="unit:repeat",
        fields={
            "request": "读一下 src/a.cs",
            "final_answer": "读完了。",
            "messages": [
                {"role": "User", "text": "读一下 src/a.cs"},
                {"role": "Assistant", "text": "读完了。"},
            ],
            "tool_calls": calls,
            "tool_results": [],
        },
    )


def _bare_trace() -> Trace:
    return Trace(
        id="unit:bare",
        fields={
            "request": "解释一下这个仓库的分层结构",
            "final_answer": "分为 Core / Infrastructure / App 三层。",
            "messages": [
                {"role": "User", "text": "解释一下这个仓库的分层结构"},
                {"role": "Assistant", "text": "分为 Core / Infrastructure / App 三层。"},
            ],
            "tool_calls": [],
            "tool_results": [],
        },
    )
