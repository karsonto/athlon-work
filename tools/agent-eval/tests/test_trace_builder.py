from pathlib import Path

import pytest

from conftest import FIXTURES
from trace_builder import CollectConfig, collect_traces, read_traces, write_traces
from trace_model import FLAG_COMPACTED, FLAG_TRUNCATED_TOOL_OUTPUT, Issue


def config(**overrides) -> CollectConfig:
    base = CollectConfig(
        sessions_root=FIXTURES / "sessions",
        include_sub_agents=True,
        min_request_chars=8,
    )
    for key, value in overrides.items():
        setattr(base, key, value)
    return base


@pytest.fixture(scope="module")
def collected():
    return collect_traces(config())


def test_discovers_sub_agent_sessions(collected):
    traces, report = collected
    session_ids = {trace.fields["session_id"] for trace in traces}
    assert session_ids == {"SESSION-FULL", "SESSION-BROKEN", "SESSION-PARENT", "SESSION-CHILD"}
    assert report.sessions_parsed == 4


def test_turn_splitting_and_counts(collected):
    traces, report = collected
    by_session = {}
    for trace in traces:
        by_session.setdefault(trace.fields["session_id"], []).append(trace)
    assert len(by_session["SESSION-FULL"]) == 2
    assert len(by_session["SESSION-BROKEN"]) == 2
    assert report.traces == 6


def test_complete_trace_has_no_issues(collected):
    traces, _ = collected
    trace = next(item for item in traces if item.id == "SESSION-FULL:0")

    assert trace.issues == ()
    assert trace.request.startswith("帮我看看 config/settings.json")
    assert trace.final_answer == "配置里的模型名是 `athlon-pro`。"

    records = trace.list("tool_calls")
    assert len(records) == 1
    assert records[0]["name"] == "read_file"
    assert records[0]["arguments"] == {"path": "config/settings.json"}
    assert records[0]["result_state"] == "success"
    assert "athlon-pro" in records[0]["result"]


def test_failed_tool_is_paired_but_not_successful(collected):
    traces, _ = collected
    trace = next(item for item in traces if item.id == "SESSION-FULL:1")
    record = trace.list("tool_calls")[0]
    assert record["result_state"] == "failure"
    assert record["result_summary"] == "permission denied"


def test_broken_trace_detects_all_structural_issues(collected):
    traces, _ = collected
    trace = next(item for item in traces if item.id == "SESSION-BROKEN:0")

    issues = {issue.value for issue in trace.issues}
    assert Issue.AMBIGUOUS_CALL_ID.value in issues
    assert Issue.UNPAIRED_TOOL_RESULT.value in issues
    assert Issue.PENDING_TOOL_CALL.value in issues
    assert FLAG_TRUNCATED_TOOL_OUTPUT in trace.flags
    assert FLAG_COMPACTED in trace.flags

    # The unpaired call keeps its record without inventing a result.
    pending = [record for record in trace.list("tool_calls") if "result_state" not in record]
    assert [record["name"] for record in pending] == ["grep_files"]


def test_empty_final_answer_is_flagged(collected):
    traces, _ = collected
    trace = next(item for item in traces if item.id == "SESSION-BROKEN:1")
    assert Issue.EMPTY_FINAL_ANSWER in trace.issues
    assert trace.final_answer == ""


def test_compaction_message_is_not_counted_as_conversation(collected):
    traces, _ = collected
    trace = next(item for item in traces if item.id == "SESSION-BROKEN:0")
    roles = {message["role"] for message in trace.list("messages")}
    assert "Summary" not in roles
    assert FLAG_COMPACTED in trace.flags


def test_min_request_chars_filters_short_turns(collected):
    traces, _ = collected
    filtered, _ = collect_traces(config(min_request_chars=1000))
    assert traces and filtered == []


def test_sub_agent_sessions_can_be_excluded():
    traces, _ = collect_traces(config(include_sub_agents=False))
    assert all(trace.fields["session_id"] != "SESSION-CHILD" for trace in traces)


def test_title_exclusion():
    traces, report = collect_traces(config(exclude_title_contains=("重构",)))
    assert all(trace.fields["session_id"] != "SESSION-BROKEN" for trace in traces)
    assert report.skip_reasons.get("EXCLUDED_TITLE") == 1


def test_traces_roundtrip_through_jsonl(tmp_path: Path, collected):
    traces, _ = collected
    path = tmp_path / "traces.jsonl"
    write_traces(path, traces)
    restored = read_traces(path)

    assert [item.id for item in restored] == [item.id for item in traces]
    first, again = traces[0], restored[0]
    assert again.fields["request"] == first.fields["request"]
    assert again.fields["final_answer"] == first.fields["final_answer"]
    assert again.issues == first.issues
    assert again.flags == first.flags
