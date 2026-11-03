import json
from pathlib import Path

import pytest

import judge as judge_module
from conftest import FIXTURES
from judge import (
    JudgeConfig,
    JudgeError,
    LlmJudge,
    OfflineJudge,
    RecordingJudge,
    build_judge,
)
from trace_model import Trace, Verdict


def make_judge(**overrides) -> LlmJudge:
    config = JudgeConfig(
        endpoint="https://api.example.com/v1",
        model="judge-model",
        api_key="test-key",
        **overrides,
    )
    return LlmJudge(config)


QUESTIONS = {"grounded": "is it grounded?", "tool_choice": "is the tool right?"}


def test_parse_accepts_strict_payload():
    outcome = make_judge()._parse(
        {
            "grounded": {"verdict": "yes", "reason": "quoted tool output"},
            "tool_choice": {"verdict": "UNSURE", "reason": "no schema recorded"},
        },
        QUESTIONS,
        {"promptTokens": 12, "completionTokens": 3},
    )

    assert outcome.verdicts == {"grounded": Verdict.PASS, "tool_choice": Verdict.UNSURE}
    assert outcome.usage == {"promptTokens": 12, "completionTokens": 3}


@pytest.mark.parametrize(
    "payload, needle",
    [
        ({"grounded": {"verdict": "yes", "reason": "x"}}, "keys mismatch"),
        (
            {
                "grounded": {"verdict": "yes", "reason": "x"},
                "tool_choice": {"verdict": "yes", "reason": "x"},
                "extra": {"verdict": "yes", "reason": "x"},
            },
            "keys mismatch",
        ),
        (
            {
                "grounded": {"verdict": "probably", "reason": "x"},
                "tool_choice": {"verdict": "yes", "reason": "x"},
            },
            "verdict must be one of",
        ),
        (
            {
                "grounded": {"verdict": "yes", "reason": "   "},
                "tool_choice": {"verdict": "yes", "reason": "x"},
            },
            "no reason",
        ),
        (
            {"grounded": "yes", "tool_choice": {"verdict": "yes", "reason": "x"}},
            "not an object",
        ),
    ],
)
def test_parse_rejects_nonconforming_replies(payload, needle):
    with pytest.raises(JudgeError) as error:
        make_judge()._parse(payload, QUESTIONS, None)

    assert error.value.reason == "INVALID_RESPONSE"
    assert needle in error.value.detail


def test_reason_is_truncated_not_rejected():
    judge = make_judge(reason_max_chars=10)
    outcome = judge._parse(
        {
            "grounded": {"verdict": "yes", "reason": "a" * 50},
            "tool_choice": {"verdict": "no", "reason": "b"},
        },
        QUESTIONS,
        None,
    )
    assert len(outcome.reasons["grounded"]) == 10


def test_offline_judge_replays_recorded_replies():
    offline = OfflineJudge(FIXTURES / "judge_replies")
    trace = Trace(id="SESSION-FULL:0", fields={})

    questions = {
        "tool_choice": "q",
        "used_tool_result": "q",
        "grounded": "q",
        "stayed_in_scope": "q",
        "answer_relevancy": "q",
        "completeness": "q",
        "indirect_injection": "q",
    }
    outcome = offline.evaluate(trace, questions)

    assert outcome.verdicts["grounded"] is Verdict.PASS
    assert outcome.verdicts["completeness"] is Verdict.UNSURE
    assert outcome.verdicts["indirect_injection"] is Verdict.FAIL  # "no" hazard
    assert outcome.usage is None  # recorded replies carry no usage; never invented


def test_offline_judge_without_reply_is_an_error():
    offline = OfflineJudge(FIXTURES / "judge_replies")
    with pytest.raises(JudgeError):
        offline.evaluate(Trace(id="SESSION-MISSING:0", fields={}), QUESTIONS)


def test_retry_then_success(monkeypatch):
    judge = make_judge(max_retries=2)
    attempts = []

    monkeypatch.setattr(judge_module.time, "sleep", lambda _seconds: None)

    def flaky(_self, _url, _data, _timeout):
        attempts.append(1)
        if len(attempts) == 1:
            raise JudgeError("BACKEND_ERROR", detail="HTTP 503")
        return (
            {
                "grounded": {"verdict": "yes", "reason": "ok"},
                "tool_choice": {"verdict": "no", "reason": "wrong"},
            },
            {"promptTokens": 5, "completionTokens": 5},
        )

    monkeypatch.setattr(LlmJudge, "_post_once", flaky)
    outcome = judge.evaluate(Trace(id="t", fields={}), QUESTIONS)

    assert len(attempts) == 2
    assert outcome.verdicts["tool_choice"] is Verdict.FAIL


def test_retries_stop_at_max_retries(monkeypatch):
    judge = make_judge(max_retries=3, timeout_seconds=10)
    attempts = []

    monkeypatch.setattr(judge_module.time, "monotonic", lambda: 0.0)
    monkeypatch.setattr(judge_module.time, "sleep", lambda _seconds: None)

    def always_timeout(_self, _url, _data, _timeout):
        attempts.append(1)
        raise JudgeError("TIMEOUT", detail="operation timed out")

    monkeypatch.setattr(LlmJudge, "_post_once", always_timeout)

    with pytest.raises(JudgeError) as error:
        judge.evaluate(Trace(id="t", fields={}), QUESTIONS)

    assert error.value.reason == "TIMEOUT"
    assert len(attempts) == 4  # one initial attempt + max_retries


def test_budget_exhausted_before_first_attempt(monkeypatch):
    judge = make_judge(max_retries=3, timeout_seconds=10)
    attempts = []
    clock = iter([0.0, 100.0])

    monkeypatch.setattr(judge_module.time, "monotonic", lambda: next(clock))
    monkeypatch.setattr(judge_module.time, "sleep", lambda _seconds: None)

    def never_called(_self, _url, _data, _timeout):
        attempts.append(1)
        raise AssertionError("should not be called")

    monkeypatch.setattr(LlmJudge, "_post_once", never_called)

    with pytest.raises(JudgeError) as error:
        judge.evaluate(Trace(id="t", fields={}), QUESTIONS)

    assert error.value.reason == "TIMEOUT"
    assert attempts == []  # the budget covers the whole trace, not per attempt


def test_non_retryable_error_is_not_retried(monkeypatch):
    judge = make_judge(max_retries=3)
    attempts = []

    def unauthorized(_self, _url, _data, _timeout):
        attempts.append(1)
        raise JudgeError("CLIENT_ERROR", detail="HTTP 401")

    monkeypatch.setattr(LlmJudge, "_post_once", unauthorized)

    with pytest.raises(JudgeError):
        judge.evaluate(Trace(id="t", fields={}), QUESTIONS)
    assert len(attempts) == 1


def test_settings_resolution_and_key_env(tmp_path: Path, monkeypatch):
    settings = tmp_path / "settings.json"
    settings.write_text(
        json.dumps({"model": {"endpoint": "https://from-settings/v1", "modelName": "m-from-settings"}}),
        encoding="utf-8",
    )
    monkeypatch.setenv("ATHLON_EVAL_API_KEY", "env-key")

    resolved = JudgeConfig(settings_path=settings).resolved()
    assert resolved.endpoint == "https://from-settings/v1"
    assert resolved.model == "m-from-settings"
    assert resolved.api_key == "env-key"

    # Explicit config wins over the settings file.
    override = JudgeConfig(endpoint="https://explicit/v1", model="explicit", settings_path=settings).resolved()
    assert override.endpoint == "https://explicit/v1"
    assert override.model == "explicit"


def test_missing_key_is_reported_not_substituted(monkeypatch):
    monkeypatch.delenv("ATHLON_EVAL_API_KEY", raising=False)
    judge = LlmJudge(JudgeConfig(endpoint="https://x/v1", model="m"))
    with pytest.raises(JudgeError) as error:
        judge.evaluate(Trace(id="t", fields={}), QUESTIONS)
    assert error.value.reason == "NOT_CONFIGURED"


def test_recording_judge_writes_replayable_replies(tmp_path: Path):
    class Passthrough:
        def describe(self):
            return {"backend": "stub"}

        def evaluate(self, trace, questions):
            from judge import JudgeOutcome

            return JudgeOutcome(
                verdicts={key: Verdict.PASS for key in questions},
                reasons={key: "ok" for key in questions},
            )

    recorder = RecordingJudge(Passthrough(), tmp_path)
    recorder.evaluate(Trace(id="SESSION-FULL:0", fields={}), QUESTIONS)

    recorded = json.loads((tmp_path / "SESSION-FULL_0.json").read_text(encoding="utf-8"))
    assert recorded["grounded"]["verdict"] == "pass"

    replay = OfflineJudge(tmp_path).evaluate(Trace(id="SESSION-FULL:0", fields={}), QUESTIONS)
    assert replay.verdicts == {key: Verdict.PASS for key in QUESTIONS}


def test_state_is_clipped_to_budget():
    judge = make_judge(max_state_chars=1500)
    trace = Trace(
        id="big",
        fields={
            "request": "r" * 100,
            "final_answer": "a" * 100,
            "tool_calls": [
                {
                    "id": "c1",
                    "name": "read_file",
                    "arguments": {"path": "x"},
                    "result": "y" * 5000,
                    "result_state": "success",
                }
            ],
            "messages": [{"role": "User", "text": "z" * 5000}],
            "tool_results": [{"id": "c1", "result": "y" * 5000}],
        },
    )
    state = judge._state(trace)
    rendered = json.dumps(state, ensure_ascii=False)

    assert len(rendered) <= 1500
    assert state["tool_output_truncated"] is True
    assert state["messages"] == []
    assert state["tool_calls"], "the call record must survive; only its payload is clipped"


def test_build_judge_selects_backend(tmp_path: Path):
    offline = build_judge(JudgeConfig(), offline_replies=FIXTURES / "judge_replies")
    assert offline.describe()["backend"] == "offline"

    recorded = build_judge(
        JudgeConfig(endpoint="https://x/v1", model="m", api_key="k"),
        record_replies=tmp_path,
    )
    assert recorded.describe()["backend"] == "llm"
    assert recorded.describe()["recordingTo"] == str(tmp_path)
