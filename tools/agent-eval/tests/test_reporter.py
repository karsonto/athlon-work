from pathlib import Path

import pytest

from conftest import FIXTURES
from judge import JudgeOutcome
from reporter import compare, gate_failed, summary_markdown, failures_markdown, write_reports
from runner import Entry, Results, RunConfig, Usage, run, write_results, read_results
from trace_model import MetricResult, Trace, Verdict


class StubJudge:
    def __init__(self, verdicts=None):
        self.verdicts = verdicts or {}

    def describe(self):
        return {"backend": "stub", "model": "stub-model"}

    def evaluate(self, trace, questions):
        return JudgeOutcome(
            verdicts={key: self.verdicts.get(key, Verdict.PASS) for key in questions},
            reasons={key: f"stub:{key}" for key in questions},
            usage={"promptTokens": 100, "completionTokens": 20},
        )


@pytest.fixture
def results():
    from trace_builder import CollectConfig, collect_traces

    traces, _ = collect_traces(
        CollectConfig(sessions_root=FIXTURES / "sessions", min_request_chars=8)
    )
    return run(
        traces,
        config=RunConfig(),
        judge=StubJudge({"completeness": Verdict.UNSURE, "grounded": Verdict.FAIL}),
    )


def test_summary_keeps_undecided_out_of_the_denominator(results):
    markdown = summary_markdown(results)

    assert "| 指标 | 类型 | PASS | FAIL | 通过率 |" in markdown
    assert "INCONCLUSIVE、ERROR、SKIPPED 不计入分母" in markdown
    # completeness is unsure everywhere -> 0 decided, no pass rate
    completeness_line = next(line for line in markdown.splitlines() if line.startswith("| completeness"))
    assert "| - |" in completeness_line
    assert "| 0 | 0 |" in completeness_line

    grounded_line = next(line for line in markdown.splitlines() if line.startswith("| grounded"))
    assert "| 0 | " in grounded_line and "0.0%" in grounded_line


def test_failures_only_lists_fail_and_unsure(results):
    markdown = failures_markdown(results)

    assert "`grounded` = fail" in markdown
    assert "`completeness` = unsure" in markdown
    assert "tools/agent-eval" not in markdown


def test_summary_splits_main_sessions_from_subagents(results):
    """A blended rate hides which scope regressed; both scopes must be shown."""
    markdown = summary_markdown(results)

    assert "主会话 / 子代理拆分" in markdown
    main_line = next(line for line in markdown.splitlines() if line.startswith("| 主会话 |"))
    subagent_line = next(line for line in markdown.splitlines() if line.startswith("| 子代理 |"))

    main_traces = int(main_line.split("|")[2])
    subagent_traces = int(subagent_line.split("|")[2])
    assert subagent_traces >= 1
    assert main_traces + subagent_traces == results.trace_count


def test_summary_omits_split_when_there_are_no_subagents():
    results = make_results("grounded", [Verdict.PASS, Verdict.FAIL])
    assert "主会话 / 子代理拆分" not in summary_markdown(results)


def test_reports_are_written(tmp_path: Path, results):
    write_reports(tmp_path, results)

    assert (tmp_path / "summary.md").is_file()
    assert (tmp_path / "failures.md").is_file()


def test_results_roundtrip(tmp_path: Path, results):
    path = tmp_path / "results.json"
    write_results(path, results)
    restored = read_results(path)

    assert restored.trace_count == results.trace_count
    assert len(restored.entries) == len(results.entries)
    assert restored.usage.to_dict() == results.usage.to_dict()
    original = results.entries[0]
    copy = restored.entries[0]
    assert copy.trace.id == original.trace.id
    assert set(copy.results) == set(original.results)
    assert copy.results["grounded"].status is original.results["grounded"].status


# --------------------------------------------------------------------------- #
# Comparison + gate
# --------------------------------------------------------------------------- #


def make_results(metric: str, verdicts: list[Verdict]) -> Results:
    entries = [
        Entry(
            trace=Trace(id=f"trace-{index}", fields={"request": "q"}),
            results={metric: MetricResult.from_verdict(verdict, "reason")},
        )
        for index, verdict in enumerate(verdicts)
    ]
    return Results(
        entries=entries,
        metric_meta=[{"id": metric, "version": "v1", "kind": "llm"}],
        usage=Usage(),
        judge_info={"backend": "stub"},
        started_at="2026-11-03T00:00:00+0800",
        elapsed_ms=1,
        trace_count=len(entries),
    )


def test_compare_reports_delta_and_new_failures():
    baseline = make_results("grounded", [Verdict.PASS] * 4)
    current = make_results("grounded", [Verdict.PASS, Verdict.PASS, Verdict.FAIL, Verdict.FAIL])

    markdown, findings = compare(baseline, current, [])

    assert "| grounded | 100.0% | 50.0% | -50.0pt |" in markdown
    assert "新增失败" in markdown
    assert findings == []


def test_gate_fails_only_above_min_denominator():
    gate = [{"metric": "grounded", "maxDropRate": 0.05, "minDenominator": 20}]

    # Small sample: one regression must not trip CI.
    small_baseline = make_results("grounded", [Verdict.PASS] * 2)
    small_current = make_results("grounded", [Verdict.PASS, Verdict.FAIL])
    _, findings = compare(small_baseline, small_current, gate)
    assert gate_failed(findings) is False

    big_baseline = make_results("grounded", [Verdict.PASS] * 40)
    big_current = make_results("grounded", [Verdict.PASS] * 20 + [Verdict.FAIL] * 20)
    _, findings = compare(big_baseline, big_current, gate)
    assert gate_failed(findings) is True
    assert findings[0].denominator == 40


def test_gate_ignores_unsure_and_error():
    gate = [{"metric": "grounded", "maxDropRate": 0.0, "minDenominator": 1}]
    baseline = make_results("grounded", [Verdict.PASS] * 10)

    current = make_results("grounded", [Verdict.PASS] * 10)
    current.entries[0].results["grounded"] = MetricResult.from_verdict(Verdict.UNSURE, "no evidence")
    current.entries[1].results["grounded"] = MetricResult.error("BACKEND_ERROR")

    _, findings = compare(baseline, current, gate)

    assert findings[0].current_rate == 1.0  # 10 pass, 0 fail: undecided excluded
    assert gate_failed(findings) is False


def test_missing_metric_is_not_treated_as_regression():
    gate = [{"metric": "grounded", "maxDropRate": 0.0, "minDenominator": 1}]
    baseline = make_results("grounded", [Verdict.PASS] * 5)
    current = make_results("tool_choice", [Verdict.FAIL] * 5)

    markdown, findings = compare(baseline, current, gate)

    assert findings[0].violated is False
    assert "| grounded | 100.0% | - |" in markdown
