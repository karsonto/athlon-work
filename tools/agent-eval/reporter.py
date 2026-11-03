"""Reporting: aggregation, Markdown summaries and baseline comparison.

Two rules the reports must never break:

* An ``INCONCLUSIVE`` / ``ERROR`` / ``SKIPPED`` result is **not** a failure and
  **not** a pass. Pass rates are computed over ``DECIDED`` results that carry a
  verdict, and the other states are shown in their own columns.
* A metric with too few samples is not gated. ``minDenominator`` keeps a single
  unlucky trace from tripping CI.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from runner import Entry, Results
from trace_model import MetricResult, Status


@dataclass
class MetricStats:
    id: str
    kind: str = "rule"
    pass_count: int = 0
    fail_count: int = 0
    unsure_count: int = 0
    error_count: int = 0
    skipped_count: int = 0
    signal_labels: dict[str, int] = field(default_factory=dict)

    @property
    def decided(self) -> int:
        return self.pass_count + self.fail_count

    @property
    def total(self) -> int:
        return self.decided + self.unsure_count + self.error_count + self.skipped_count

    @property
    def pass_rate(self) -> float | None:
        return self.pass_count / self.decided if self.decided else None

    def observe(self, result: MetricResult) -> None:
        if result.status is Status.DECIDED:
            if result.passed is True:
                self.pass_count += 1
            elif result.passed is False:
                self.fail_count += 1
            else:
                label = result.label or "UNLABELED"
                self.signal_labels[label] = self.signal_labels.get(label, 0) + 1
        elif result.status is Status.INCONCLUSIVE:
            self.unsure_count += 1
        elif result.status is Status.ERROR:
            self.error_count += 1
        else:
            self.skipped_count += 1

    def to_dict(self) -> dict[str, Any]:
        return {
            "pass": self.pass_count,
            "fail": self.fail_count,
            "unsure": self.unsure_count,
            "error": self.error_count,
            "skipped": self.skipped_count,
            "decided": self.decided,
            "total": self.total,
            "passRate": self.pass_rate,
            "signals": dict(self.signal_labels),
        }


def summarize(entries: Sequence[Entry]) -> list[MetricStats]:
    stats: dict[str, MetricStats] = {}
    order: list[str] = []
    for entry in entries:
        for metric_id, result in entry.results.items():
            if metric_id not in stats:
                stats[metric_id] = MetricStats(id=metric_id)
                order.append(metric_id)
            stats[metric_id].observe(result)
    return [stats[key] for key in order]


def attach_kinds(stats: Sequence[MetricStats], results: Results) -> None:
    kinds = {item.get("id"): item.get("kind", "rule") for item in results.metric_meta}
    for stat in stats:
        stat.kind = str(kinds.get(stat.id, "rule"))


# --------------------------------------------------------------------------- #
# Markdown
# --------------------------------------------------------------------------- #


def _fmt_rate(value: float | None) -> str:
    return "-" if value is None else f"{value * 100:.1f}%"


def summary_markdown(results: Results) -> str:
    stats = summarize(results.entries)
    attach_kinds(stats, results)

    lines: list[str] = []
    lines.append("# Agent 离线评估摘要\n")
    meta = results.to_dict()["meta"]
    lines.append(f"- 生成时间：{meta['generatedAt']}")
    lines.append(f"- 轨迹数：{meta['traceCount']}（已评估 {meta['evaluatedCount']}）")
    lines.append(f"- 判定后端：{results.judge_info.get('backend', 'none')}")
    if results.judge_info.get("model"):
        lines.append(f"- 判定模型：{results.judge_info['model']}")
    lines.append(
        f"- judge 请求：{results.judge_requests}（失败 {results.judge_errors}）"
    )
    usage = results.to_dict()["meta"]["usage"]
    lines.append(
        f"- token 消耗：prompt {usage['promptTokens']} / completion {usage['completionTokens']}"
    )
    lines.append(f"- 耗时：{results.elapsed_ms} ms\n")

    lines.append("## 指标总览\n")
    lines.append("| 指标 | 类型 | PASS | FAIL | 通过率 | 不确定 | 错误 | 跳过 | 信号 |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for stat in stats:
        signals = (
            ", ".join(f"{key}={value}" for key, value in sorted(stat.signal_labels.items()))
            or "-"
        )
        lines.append(
            "| {id} | {kind} | {p} | {f} | {rate} | {u} | {e} | {s} | {sig} |".format(
                id=stat.id,
                kind="规则" if stat.kind == "rule" else "语义",
                p=stat.pass_count,
                f=stat.fail_count,
                rate=_fmt_rate(stat.pass_rate),
                u=stat.unsure_count,
                e=stat.error_count,
                s=stat.skipped_count,
                sig=signals,
            )
        )

    lines.append("\n> 通过率 = PASS / (PASS + FAIL)。INCONCLUSIVE、ERROR、SKIPPED 不计入分母，也不视为失败。\n")

    issue_counts: dict[str, int] = {}
    flag_counts: dict[str, int] = {}
    for entry in results.entries:
        for issue in entry.trace.issues:
            issue_counts[issue.value] = issue_counts.get(issue.value, 0) + 1
        for flag in entry.trace.flags:
            flag_counts[flag] = flag_counts.get(flag, 0) + 1

    if issue_counts or flag_counts:
        lines.append("## 轨迹质量（解析层）\n")
        lines.append("| 标记 | 次数 | 含义 |")
        lines.append("|---|---|---|")
        meanings = {
            "AMBIGUOUS_CALL_ID": "工具调用 id 缺失或重复",
            "UNPAIRED_TOOL_RESULT": "工具结果找不到对应调用",
            "PENDING_TOOL_CALL": "轮次结束时仍有调用没有结果",
            "EMPTY_FINAL_ANSWER": "没有用户可见回答（轮次耗尽或纯工具轮）",
            "TRUNCATED_TOOL_OUTPUT": "工具输出被裁剪或移出上下文",
            "COMPACTED": "本轮发生过上下文压缩",
        }
        for key, count in sorted(issue_counts.items()):
            lines.append(f"| {key} | {count} | {meanings.get(key, '')} |")
        for key, count in sorted(flag_counts.items()):
            lines.append(f"| {key} | {count} | {meanings.get(key, '')} |")
        lines.append("")

    return "\n".join(lines) + "\n"


def failures_markdown(results: Results, limit: int = 200) -> str:
    lines: list[str] = ["# 失败与不确定明细\n"]
    emitted = 0
    for entry in results.entries:
        problems = [
            (metric_id, result)
            for metric_id, result in entry.results.items()
            if (result.status is Status.DECIDED and result.passed is False)
            or result.status is Status.INCONCLUSIVE
        ]
        if not problems:
            continue
        traces_left = limit - emitted
        if traces_left <= 0:
            break
        lines.append(
            f"## {entry.trace.fields.get('session_id')} / turn {entry.trace.fields.get('turn_index')}"
        )
        request = entry.trace.request.replace("\n", " ").strip()
        lines.append(f"- 请求：{request[:180]}")
        if entry.trace.issues:
            lines.append(f"- 轨迹问题：{', '.join(issue.value for issue in entry.trace.issues)}")
        for metric_id, result in problems[:traces_left]:
            verdict = result.verdict.value if result.verdict else result.status.value
            lines.append(f"- `{metric_id}` = {verdict}：{result.reason}")
            emitted += 1
        lines.append("")
    if emitted == 0:
        lines.append("没有 FAIL 或 INCONCLUSIVE 结果。\n")
    return "\n".join(lines) + "\n"


def write_reports(output_dir: Path, results: Results, failures_limit: int = 200) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "summary.md").write_text(summary_markdown(results), encoding="utf-8")
    (output_dir / "failures.md").write_text(
        failures_markdown(results, failures_limit), encoding="utf-8"
    )


# --------------------------------------------------------------------------- #
# Comparison
# --------------------------------------------------------------------------- #


@dataclass
class GateFinding:
    metric: str
    baseline_rate: float | None
    current_rate: float | None
    denominator: int
    allowed_drop: float
    violated: bool

    def describe(self) -> str:
        return (
            f"{self.metric}: {_fmt_rate(self.baseline_rate)} -> {_fmt_rate(self.current_rate)}"
            f" (n={self.denominator}, allowed drop {self.allowed_drop * 100:.1f}%)"
        )


def compare(
    baseline: Results,
    current: Results,
    gate: Sequence[Mapping[str, Any]] = (),
) -> tuple[str, list[GateFinding]]:
    base_stats = {stat.id: stat for stat in summarize(baseline.entries)}
    cur_stats = {stat.id: stat for stat in summarize(current.entries)}
    attach_kinds(list(cur_stats.values()), current)

    metric_ids = list(dict.fromkeys([*base_stats, *cur_stats]))
    lines: list[str] = ["# 基线与当前结果对比\n"]
    lines.append("| 指标 | 基线通过率 | 当前通过率 | 变化 | 基线样本 | 当前样本 |")
    lines.append("|---|---|---|---|---|---|")
    for metric_id in metric_ids:
        before = base_stats.get(metric_id)
        after = cur_stats.get(metric_id)
        before_rate = before.pass_rate if before else None
        after_rate = after.pass_rate if after else None
        delta = (
            f"{(after_rate - before_rate) * 100:+.1f}pt"
            if before_rate is not None and after_rate is not None
            else "-"
        )
        lines.append(
            f"| {metric_id} | {_fmt_rate(before_rate)} | {_fmt_rate(after_rate)} | {delta} |"
            f" {before.decided if before else 0} | {after.decided if after else 0} |"
        )

    findings: list[GateFinding] = []
    for item in gate:
        metric_id = str(item.get("metric") or "")
        if not metric_id:
            continue
        allowed_drop = float(item.get("maxDropRate", 0.0) or 0.0)
        min_denominator = int(item.get("minDenominator", 0) or 0)
        before = base_stats.get(metric_id)
        after = cur_stats.get(metric_id)
        before_rate = before.pass_rate if before else None
        after_rate = after.pass_rate if after else None
        denominator = after.decided if after else 0
        violated = False
        if (
            before_rate is not None
            and after_rate is not None
            and denominator >= min_denominator
        ):
            violated = (before_rate - after_rate) > allowed_drop + 1e-9
        findings.append(
            GateFinding(
                metric=metric_id,
                baseline_rate=before_rate,
                current_rate=after_rate,
                denominator=denominator,
                allowed_drop=allowed_drop,
                violated=violated,
            )
        )

    if findings:
        lines.append("\n## Gate\n")
        lines.append("| 指标 | 基线 | 当前 | 样本 | 阈值 | 结果 |")
        lines.append("|---|---|---|---|---|---|")
        for finding in findings:
            verdict = "FAIL" if finding.violated else "ok"
            lines.append(
                f"| {finding.metric} | {_fmt_rate(finding.baseline_rate)} |"
                f" {_fmt_rate(finding.current_rate)} | {finding.denominator} |"
                f" -{finding.allowed_drop * 100:.1f}pt | {verdict} |"
            )

    new_failures = _new_failures(baseline, current)
    if new_failures:
        lines.append("\n## 新增失败\n")
        for entry_id, metric_id, reason in new_failures[:100]:
            lines.append(f"- `{entry_id}` / `{metric_id}`：{reason}")
        lines.append("")

    return "\n".join(lines) + "\n", findings


def _new_failures(
    baseline: Results,
    current: Results,
) -> list[tuple[str, str, str]]:
    old: dict[str, set[str]] = {}
    for entry in baseline.entries:
        failed = {
            metric_id
            for metric_id, result in entry.results.items()
            if result.status is Status.DECIDED and result.passed is False
        }
        old[entry.trace.id] = failed

    fresh: list[tuple[str, str, str]] = []
    for entry in current.entries:
        failing = {
            metric_id: result
            for metric_id, result in entry.results.items()
            if result.status is Status.DECIDED and result.passed is False
        }
        already = old.get(entry.trace.id, set())
        for metric_id, result in failing.items():
            if metric_id not in already:
                fresh.append((entry.trace.id, metric_id, result.reason))
    return fresh


def gate_failed(findings: Iterable[GateFinding]) -> bool:
    return any(finding.violated for finding in findings)
