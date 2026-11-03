#!/usr/bin/env python3
"""Offline link evaluation for Athlon Agent.

Reads persisted sessions, rebuilds one trace per turn, runs deterministic rules,
then asks a chat model a fixed set of yes/no/unsure questions per trace, and
writes a report that can be diffed against a baseline.

    collect    sessions/**/session.json -> traces.jsonl + collect-report.json
    evaluate   traces.jsonl -> results.json + summary.md + failures.md
    report     results.json -> summary.md + failures.md
    compare    baseline vs current -> delta table, optional gate exit code

Exit codes: 0 ok, 1 usage/IO error, 2 gate violation.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Mapping

sys.path.insert(0, str(Path(__file__).resolve().parent))

from judge import JudgeConfig, JudgeError, build_judge  # noqa: E402
from reporter import compare, gate_failed, write_reports  # noqa: E402
from runner import RunConfig, read_results, run, write_results  # noqa: E402
from trace_builder import (  # noqa: E402
    CollectConfig,
    collect_traces,
    read_traces,
    write_report,
    write_traces,
)

TOOL_DIR = Path(__file__).resolve().parent
DEFAULT_CONFIG = TOOL_DIR / "config.json"
EXAMPLE_CONFIG = TOOL_DIR / "config.example.json"
DEFAULT_SETTINGS = Path(os.path.expanduser("~/.athlon-agent/config/settings.json"))


# --------------------------------------------------------------------------- #
# Config loading
# --------------------------------------------------------------------------- #


def load_config(path: Path | None) -> dict[str, Any]:
    if path is None:
        path = DEFAULT_CONFIG if DEFAULT_CONFIG.is_file() else None
    if path is None:
        return {}
    if not path.is_file():
        raise SystemExit(f"config not found: {path}")
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise SystemExit(f"config is not valid json ({path}): {error}") from None
    if not isinstance(payload, Mapping):
        raise SystemExit(f"config root must be an object: {path}")
    return dict(payload)


def judge_config(config: Mapping[str, Any], api_key: str | None) -> JudgeConfig:
    raw = dict(config.get("judge") or {})
    raw.setdefault("settingsPath", str(DEFAULT_SETTINGS))
    if api_key:
        raw["apiKey"] = api_key
    return JudgeConfig.from_mapping(raw)


def default_output_dir() -> Path:
    return TOOL_DIR.parent.parent / "reports" / f"eval-{time.strftime('%Y%m%d-%H%M%S')}"


# --------------------------------------------------------------------------- #
# Commands
# --------------------------------------------------------------------------- #


def cmd_collect(args: argparse.Namespace) -> int:
    config = load_config(args.config)
    collect_config = CollectConfig.from_mapping(config)
    if args.sessions_root:
        collect_config.sessions_root = Path(os.path.expanduser(args.sessions_root))
    if args.limit_sessions:
        collect_config.limit_sessions = args.limit_sessions

    out_dir = Path(args.out).expanduser() if args.out else default_output_dir()
    traces, report = collect_traces(collect_config)

    if args.dry_run:
        print(json.dumps(report.to_dict(), ensure_ascii=False, indent=2))
        _preview(traces, args.preview)
        return 0

    write_traces(out_dir / "traces.jsonl", traces)
    write_report(out_dir / "collect-report.json", report)
    print(f"collected {report.traces} traces from {report.sessions_parsed} sessions -> {out_dir / 'traces.jsonl'}")
    if report.errors:
        print(f"skipped {len(report.errors)} unreadable session(s); see collect-report.json", file=sys.stderr)
    return 0


def cmd_evaluate(args: argparse.Namespace) -> int:
    config = load_config(args.config)
    traces_path = Path(args.traces).expanduser() if args.traces else None
    if traces_path is None:
        out_dir = Path(args.out).expanduser() if args.out else default_output_dir()
        traces_path = out_dir / "traces.jsonl"
    if not traces_path.is_file():
        raise SystemExit(f"traces not found: {traces_path} (run `collect` first)")

    traces = read_traces(traces_path)
    out_dir = Path(args.out).expanduser() if args.out else traces_path.parent

    run_config = RunConfig.from_mapping(config)
    if args.limit:
        run_config.limit = args.limit
    if args.rules_only:
        run_config.with_semantics = False

    judge = None
    if run_config.with_semantics:
        offline_dir = Path(args.offline).expanduser() if args.offline else None
        record_dir = Path(args.record).expanduser() if args.record else None
        judge_settings = judge_config(config, args.api_key)
        if offline_dir is None:
            # Fail fast instead of turning every trace into an ERROR.
            judge_settings.resolved().require_ready()
        judge = build_judge(
            judge_settings,
            offline_replies=offline_dir,
            record_replies=record_dir,
        )

    results = run(traces, config=run_config, judge=judge)
    write_results(out_dir / "results.json", results)
    write_reports(out_dir, results, failures_limit=args.failures_limit)

    meta = results.to_dict()["meta"]
    print(
        f"evaluated {meta['evaluatedCount']} traces "
        f"({meta['judgeRequests']} judge request(s), {meta['judgeErrors']} error(s)) -> {out_dir}"
    )
    if meta["judgeErrors"]:
        print(
            "some judge calls failed; those metrics are ERROR, not FAIL (see summary.md)",
            file=sys.stderr,
        )
    return 0


def cmd_report(args: argparse.Namespace) -> int:
    results_path = Path(args.results).expanduser()
    if not results_path.is_file():
        raise SystemExit(f"results not found: {results_path}")
    out_dir = Path(args.out).expanduser() if args.out else results_path.parent
    results = read_results(results_path)
    write_reports(out_dir, results, failures_limit=args.failures_limit)
    print(f"wrote {out_dir / 'summary.md'} and {out_dir / 'failures.md'}")
    return 0


def cmd_compare(args: argparse.Namespace) -> int:
    baseline = read_results(Path(args.baseline).expanduser())
    current = read_results(Path(args.current).expanduser())
    config = load_config(args.config)
    markdown, findings = compare(baseline, current, config.get("gate") or ())

    out_path = Path(args.out).expanduser() if args.out else Path.cwd() / "comparison.md"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(markdown, encoding="utf-8")
    print(markdown)
    print(f"wrote {out_path}", file=sys.stderr)

    if gate_failed(findings):
        print("gate failed: at least one metric regressed beyond its threshold", file=sys.stderr)
        return 2
    return 0


# --------------------------------------------------------------------------- #
# Presentation helpers
# --------------------------------------------------------------------------- #


def _preview(traces: list[Any], count: int) -> None:
    if count <= 0:
        return
    print(f"\n--- preview ({min(count, len(traces))} of {len(traces)}) ---")
    for trace in traces[:count]:
        print(json.dumps(trace.to_dict(), ensure_ascii=False, indent=2)[:4000])
        print("---")


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="eval.py",
        description="Offline link evaluation for Athlon Agent sessions.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    collect = sub.add_parser("collect", help="rebuild traces from persisted sessions")
    collect.add_argument("--config", type=Path)
    collect.add_argument("--sessions-root")
    collect.add_argument("--out")
    collect.add_argument("--limit-sessions", type=int)
    collect.add_argument("--dry-run", action="store_true", help="print stats instead of writing")
    collect.add_argument("--preview", type=int, default=0, help="print N traces (with --dry-run)")
    collect.set_defaults(func=cmd_collect)

    evaluate = sub.add_parser("evaluate", help="run rule + semantic metrics over traces")
    evaluate.add_argument("--config", type=Path)
    evaluate.add_argument("--traces")
    evaluate.add_argument("--out")
    evaluate.add_argument("--limit", type=int)
    evaluate.add_argument("--rules-only", action="store_true", help="skip the LLM judge entirely")
    evaluate.add_argument("--offline", help="replay judge replies from this directory")
    evaluate.add_argument("--record", help="write judge replies here for later --offline runs")
    evaluate.add_argument("--api-key")
    evaluate.add_argument("--failures-limit", type=int, default=200)
    evaluate.set_defaults(func=cmd_evaluate)

    report = sub.add_parser("report", help="regenerate Markdown from results.json")
    report.add_argument("--results", required=True)
    report.add_argument("--out")
    report.add_argument("--failures-limit", type=int, default=200)
    report.set_defaults(func=cmd_report)

    compare_parser = sub.add_parser("compare", help="diff two results.json files")
    compare_parser.add_argument("--baseline", required=True)
    compare_parser.add_argument("--current", required=True)
    compare_parser.add_argument("--config", type=Path)
    compare_parser.add_argument("--out")
    compare_parser.set_defaults(func=cmd_compare)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return int(args.func(args) or 0)
    except JudgeError as error:
        print(f"judge unavailable ({error.reason}): {error.detail}", file=sys.stderr)
        return 1
    except (FileNotFoundError, NotADirectoryError) as error:
        print(f"not found: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
