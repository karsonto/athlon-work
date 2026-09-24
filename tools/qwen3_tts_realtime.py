#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Qwen3-TTS Realtime —— 真正的流式语音合成客户端
================================================

严格遵循 OpenAI Realtime API 的事件协议（event_id / type / delta 风格），
通过 WebSocket 双工连接 Qwen3-TTS，实现「边输入文本、边接收音频」的流式合成。

* 无需下载任何模型权重，纯云端 API 调用
* 唯一依赖: pip install websocket-client

环境变量
--------
DASHSCOPE_API_KEY   必填。阿里云百炼 API Key
DASHSCOPE_WORKSPACE 选填。业务空间 ID（使用专属域名时需要）

用法示例
--------
# 1) 一次性文本合成，输出 PCM
python qwen3_tts_realtime.py --text "你好，这是一段流式语音合成测试。" --out hello.pcm

# 2) 直接输出 WAV（自动补 wav 头）
python qwen3_tts_realtime.py --text "你好" --out hello.wav

# 3) 边播边合成（需 pip install sounddevice）
python qwen3_tts_realtime.py --text "你好" --play

# 4) 模拟大模型流式输出：逐行从 stdin 读文本，边读边合成（低首包延迟）
python llm.py | python qwen3_tts_realtime.py --stdin --play

# 5) 指令控制（自动切换到 instruct 模型）
python qwen3_tts_realtime.py --text "今天天气不错" \
    --instructions "用特别开心的语气，语速稍快" --play
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import queue
import sys
import threading
import time
import uuid
import wave
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, Iterator, Optional

try:
    import websocket  # websocket-client
except ImportError:  # pragma: no cover
    sys.stderr.write(
        "[fatal] 缺少依赖，请先执行:  pip install websocket-client\n"
    )
    raise SystemExit(1)


# --------------------------------------------------------------------------- #
# 常量与协议定义
# --------------------------------------------------------------------------- #

DEFAULT_URL = "wss://dashscope.aliyuncs.com/api-ws/v1/realtime"
DEFAULT_MODEL = "qwen3-tts-flash-realtime"
INSTRUCT_MODEL = "qwen3-tts-instruct-flash-realtime"

# 官方音色：Cherry(芊悦) / Serena(苏瑶) / Ethan(晨煦) / Chelsie(千雪)
DEFAULT_VOICE = "Cherry"

DEFAULT_SAMPLE_RATE = 24000
DEFAULT_CHANNELS = 1
DEFAULT_SAMPLE_WIDTH = 2  # 16bit

# ---- 客户端 -> 服务端 事件类型 ----
EVT_SESSION_UPDATE = "session.update"
EVT_TEXT_APPEND = "input_text_buffer.append"
EVT_TEXT_COMMIT = "input_text_buffer.commit"
EVT_TEXT_CLEAR = "input_text_buffer.clear"
EVT_RESPONSE_CANCEL = "response.cancel"
EVT_SESSION_FINISH = "session.finish"

# ---- 服务端 -> 客户端 事件类型 ----
SRV_SESSION_CREATED = "session.created"
SRV_SESSION_UPDATED = "session.updated"
SRV_RESPONSE_CREATED = "response.created"
SRV_AUDIO_DELTA = "response.audio.delta"
SRV_AUDIO_DONE = "response.audio.done"
SRV_RESPONSE_DONE = "response.done"
SRV_SESSION_FINISHED = "session.finished"
SRV_ERROR = "error"


@dataclass
class TTSConfig:
    """会话配置，对应 session.update 事件的 session 字段。"""

    voice: str = DEFAULT_VOICE
    # server_commit: 文本到达即自动合成，首包延迟最低（推荐流式场景）
    # commit:        需显式调用 commit() 才合成，适合按句精准控制
    mode: str = "server_commit"
    response_format: str = "pcm"
    sample_rate: int = DEFAULT_SAMPLE_RATE
    volume: Optional[int] = None          # [0, 100]
    speech_rate: Optional[float] = None   # [0.5, 2.0]
    pitch_rate: Optional[float] = None    # [0.5, 2.0]
    bit_rate: Optional[int] = None        # opus/mp3 生效，6~510 kbps
    language_type: Optional[str] = None   # 默认 auto
    enable_tn: Optional[bool] = None      # 文本正则化
    instructions: Optional[str] = None    # 指令控制（需 instruct 模型）
    optimize_instructions: Optional[bool] = None
    extra: Dict[str, Any] = field(default_factory=dict)

    def to_payload(self) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "voice": self.voice,
            "mode": self.mode,
            "response_format": self.response_format,
            "sample_rate": self.sample_rate,
        }
        optional = {
            "volume": self.volume,
            "speech_rate": self.speech_rate,
            "pitch_rate": self.pitch_rate,
            "bit_rate": self.bit_rate,
            "language_type": self.language_type,
            "enable_tn": self.enable_tn,
            "instructions": self.instructions,
            "optimize_instructions": self.optimize_instructions,
        }
        payload.update({k: v for k, v in optional.items() if v is not None})
        payload.update(self.extra)
        return payload


# --------------------------------------------------------------------------- #
# 核心客户端
# --------------------------------------------------------------------------- #

class QwenTTSRealtime:
    """
    OpenAI Realtime 风格的流式 TTS 客户端。

    线程模型:
        主线程 ── send_event() ──▶ _send_q ──▶ 发送线程 ──▶ WebSocket
        主线程 ◀── audio_chunks() ◀── _audio_q ◀── 接收线程 ◀── WebSocket

    收发分离 + 队列解耦，避免阻塞、支持随时打断(barge-in)。
    """

    def __init__(
        self,
        api_key: Optional[str] = None,
        model: str = DEFAULT_MODEL,
        url: str = DEFAULT_URL,
        workspace: Optional[str] = None,
        headers: Optional[Dict[str, str]] = None,
        connect_timeout: float = 10.0,
        recv_timeout: float = 1.0,
        debug: bool = False,
    ) -> None:
        self.api_key = api_key or os.environ.get("DASHSCOPE_API_KEY")
        if not self.api_key:
            raise ValueError(
                "未提供 API Key。请设置环境变量 DASHSCOPE_API_KEY，"
                "或通过 api_key 参数传入。"
            )

        self.model = model
        self.url = f"{url}?model={model}"
        self.workspace = workspace or os.environ.get("DASHSCOPE_WORKSPACE")
        self.extra_headers = headers or {}
        self.connect_timeout = connect_timeout
        self.recv_timeout = recv_timeout
        self.debug = debug

        self.ws: Optional[websocket.WebSocket] = None
        self._send_q: "queue.Queue[Optional[str]]" = queue.Queue()
        self._audio_q: "queue.Queue[Optional[bytes]]" = queue.Queue()
        self._event_cb: Optional[Any] = None

        self._send_thread: Optional[threading.Thread] = None
        self._recv_thread: Optional[threading.Thread] = None
        self._closed = threading.Event()
        self._finished = threading.Event()

        # 指标
        self.session_id: Optional[str] = None
        self.response_id: Optional[str] = None
        self.first_audio_delay_ms: Optional[float] = None
        self._first_text_ts: Optional[float] = None
        self._bytes_received = 0

    # ----------------------------- 生命周期 ----------------------------- #

    def _build_headers(self) -> Dict[str, str]:
        headers = {
            "Authorization": f"Bearer {self.api_key}",
            "user-agent": "qwen3-tts-realtime-py/1.0",
        }
        if self.workspace:
            headers["X-DashScope-WorkSpace"] = self.workspace
        headers.update(self.extra_headers)
        return headers

    def connect(self) -> "QwenTTSRealtime":
        """建立 WebSocket 连接并启动收发线程。"""
        self.ws = websocket.create_connection(
            self.url,
            header=self._build_headers(),
            timeout=self.connect_timeout,
        )
        # 后续 recv 用短超时轮询，便于优雅退出
        self.ws.settimeout(self.recv_timeout)

        self._send_thread = threading.Thread(
            target=self._send_loop, name="tts-send", daemon=True
        )
        self._recv_thread = threading.Thread(
            target=self._recv_loop, name="tts-recv", daemon=True
        )
        self._send_thread.start()
        self._recv_thread.start()
        self._log(f"connected -> {self.url}")
        return self

    def _send_loop(self) -> None:
        while True:
            item = self._send_q.get()
            if item is None:
                return
            try:
                assert self.ws is not None
                self.ws.send(item)
                self._log(f">> {item[:160]}")
            except Exception as exc:  # noqa: BLE001
                self._log(f"[send error] {exc}")
                return

    def _recv_loop(self) -> None:
        while not self._closed.is_set():
            try:
                assert self.ws is not None
                raw = self.ws.recv()
            except websocket.WebSocketTimeoutException:
                continue
            except Exception as exc:  # noqa: BLE001
                if not self._closed.is_set():
                    self._log(f"[recv error] {exc}")
                break

            if raw is None or raw == "":
                break

            try:
                event = json.loads(raw)
            except (json.JSONDecodeError, TypeError):
                self._log(f"[skip] non-json frame: {str(raw)[:120]}")
                continue

            self._dispatch(event)

        self._finished.set()
        self._audio_q.put(None)  # 关闭音频流

    def _dispatch(self, event: Dict[str, Any]) -> None:
        etype = event.get("type", "")

        if etype == SRV_SESSION_CREATED:
            self.session_id = (event.get("session") or {}).get("id")
            self._log(f"session.created id={self.session_id}")

        elif etype == SRV_RESPONSE_CREATED:
            self.response_id = (event.get("response") or {}).get("id")
            self._log(f"response.created id={self.response_id}")

        elif etype == SRV_AUDIO_DELTA:
            delta_b64 = event.get("delta")
            if delta_b64:
                chunk = base64.b64decode(delta_b64)
                self._bytes_received += len(chunk)
                if self.first_audio_delay_ms is None and self._first_text_ts:
                    self.first_audio_delay_ms = (
                        time.time() - self._first_text_ts
                    ) * 1000.0
                self._audio_q.put(chunk)

        elif etype == SRV_RESPONSE_DONE:
            self._log(f"response.done id={self.response_id}")

        elif etype == SRV_SESSION_FINISHED:
            self._log("session.finished")
            self._finished.set()
            self._audio_q.put(None)

        elif etype == SRV_ERROR:
            err = event.get("error") or event
            self._log(f"[server error] {json.dumps(err, ensure_ascii=False)}")
            self._audio_q.put(None)

        if self._event_cb is not None:
            try:
                self._event_cb(event)
            except Exception as exc:  # noqa: BLE001
                self._log(f"[callback error] {exc}")

    def close(self) -> None:
        self._closed.set()
        try:
            self._send_q.put_nowait(None)
        except Exception:  # noqa: BLE001
            pass
        if self.ws is not None:
            try:
                self.ws.close()
            except Exception:  # noqa: BLE001
                pass

    def __enter__(self) -> "QwenTTSRealtime":
        return self.connect()

    def __exit__(self, *exc_info: Any) -> None:
        self.close()

    # ----------------------------- 事件发送 ----------------------------- #

    def send_event(self, event_type: str, **fields: Any) -> None:
        """发送任意事件，自动补 event_id（OpenAI Realtime 协议要求）。"""
        payload = {"event_id": f"event_{uuid.uuid4().hex}", "type": event_type}
        payload.update(fields)
        self._send_q.put(json.dumps(payload, ensure_ascii=False))

    def update_session(self, config: TTSConfig) -> None:
        """下发会话配置（session.update）。"""
        self.send_event(EVT_SESSION_UPDATE, session=config.to_payload())

    def append_text(self, text: str) -> None:
        """追加一段待合成文本（input_text_buffer.append）。"""
        if not text:
            return
        if self._first_text_ts is None:
            self._first_text_ts = time.time()
        self.send_event(EVT_TEXT_APPEND, text=text)

    def commit(self) -> None:
        """提交缓冲文本并触发合成（仅 commit 模式下需要）。"""
        self.send_event(EVT_TEXT_COMMIT)

    def clear_text(self) -> None:
        """清空尚未合成的缓冲文本。"""
        self.send_event(EVT_TEXT_CLEAR)

    def cancel_response(self) -> None:
        """打断当前合成（barge-in）。"""
        self.send_event(EVT_RESPONSE_CANCEL)

    def finish(self) -> None:
        """声明文本流结束，服务端合成完剩余内容后关闭会话。"""
        self.send_event(EVT_SESSION_FINISH)

    # ----------------------------- 音频接收 ----------------------------- #

    def audio_chunks(self, timeout: Optional[float] = None) -> Iterator[bytes]:
        """
        生成器：逐个产出 PCM 音频块（已 base64 解码）。
        全部合成完毕后自然结束。
        """
        while True:
            try:
                chunk = self._audio_q.get(timeout=timeout)
            except queue.Empty:
                if self._finished.is_set():
                    return
                continue
            if chunk is None:
                return
            yield chunk

    def wait_done(self, timeout: Optional[float] = None) -> bool:
        """阻塞等待会话结束。"""
        return self._finished.wait(timeout=timeout)

    # ----------------------------- 工具 ----------------------------- #

    def on_event(self, callback: Any) -> "QwenTTSRealtime":
        """注册原始事件回调，便于记录日志或做更细粒度控制。"""
        self._event_cb = callback
        return self

    def _log(self, msg: str) -> None:
        if self.debug:
            sys.stderr.write(f"[qwen-tts] {msg}\n")
            sys.stderr.flush()


# --------------------------------------------------------------------------- #
# 高层封装：一步到位
# --------------------------------------------------------------------------- #

def synthesise_stream(
    text: Iterable[str] | str,
    voice: str = DEFAULT_VOICE,
    model: str = DEFAULT_MODEL,
    instructions: Optional[str] = None,
    mode: str = "server_commit",
    sample_rate: int = DEFAULT_SAMPLE_RATE,
    speech_rate: Optional[float] = None,
    volume: Optional[int] = None,
    api_key: Optional[str] = None,
    url: str = DEFAULT_URL,
    workspace: Optional[str] = None,
    text_delay: float = 0.0,
    debug: bool = False,
) -> Iterator[bytes]:
    """
    最简入口：传入文本（字符串或可迭代的文本片段），逐块 yield PCM 音频。

    适合直接对接大模型的流式输出：
        for pcm in synthesise_stream(llm_token_stream):
            websocket.send_bytes(pcm)
    """
    # 指令控制必须使用 instruct 模型
    if instructions:
        if model == DEFAULT_MODEL:
            model = INSTRUCT_MODEL

    if isinstance(text, str):
        text = [text]

    config = TTSConfig(
        voice=voice,
        mode=mode,
        response_format="pcm",
        sample_rate=sample_rate,
        speech_rate=speech_rate,
        volume=volume,
        instructions=instructions,
    )

    client = QwenTTSRealtime(
        api_key=api_key, model=model, url=url, workspace=workspace, debug=debug
    )
    try:
        client.connect()
        client.update_session(config)

        def _feed() -> None:
            try:
                for piece in text:
                    client.append_text(piece)
                    if text_delay:
                        time.sleep(text_delay)
            finally:
                client.finish()

        feeder = threading.Thread(target=_feed, name="tts-feeder", daemon=True)
        feeder.start()

        yield from client.audio_chunks()
    finally:
        client.close()


# --------------------------------------------------------------------------- #
# 输出工具
# --------------------------------------------------------------------------- #

class PcmToWavWriter:
    """把流式 PCM 边收边写成标准 WAV 文件。"""

    def __init__(
        self,
        path: str,
        sample_rate: int = DEFAULT_SAMPLE_RATE,
        channels: int = DEFAULT_CHANNELS,
        sample_width: int = DEFAULT_SAMPLE_WIDTH,
    ) -> None:
        self.path = path
        self.sample_rate = sample_rate
        self.channels = channels
        self.sample_width = sample_width
        self._fh = None
        self._wav: Optional[wave.Wave_write] = None

    def __enter__(self) -> "PcmToWavWriter":
        self._fh = open(self.path, "wb")
        self._wav = wave.open(self._fh, "wb")
        self._wav.setnchannels(self.channels)
        self._wav.setsampwidth(self.sample_width)
        self._wav.setframerate(self.sample_rate)
        return self

    def write(self, pcm: bytes) -> None:
        assert self._wav is not None
        self._wav.writeframes(pcm)

    def __exit__(self, *exc_info: Any) -> None:
        if self._wav is not None:
            self._wav.close()
        if self._fh is not None:
            self._fh.close()


class StreamingSpeaker:
    """基于 sounddevice 的流式播放器（可选依赖）。"""

    def __init__(
        self,
        sample_rate: int = DEFAULT_SAMPLE_RATE,
        channels: int = DEFAULT_CHANNELS,
    ) -> None:
        try:
            import sounddevice as sd  # type: ignore
        except ImportError as exc:  # pragma: no cover
            raise RuntimeError(
                "需要流式播放请先安装:  pip install sounddevice"
            ) from exc

        import numpy as np  # type: ignore

        self._np = np
        self.stream = sd.RawOutputStream(
            samplerate=sample_rate,
            channels=channels,
            dtype="int16",
            blocksize=0,
        )
        self.stream.start()

    def write(self, pcm: bytes) -> None:
        self.stream.write(pcm)

    def close(self) -> None:
        try:
            self.stream.stop()
            self.stream.close()
        except Exception:  # noqa: BLE001
            pass


def read_text_source(args: argparse.Namespace) -> Iterable[str]:
    """按优先级从 --stdin / --file / --text 取得文本片段。"""
    if args.stdin:
        for line in sys.stdin:
            line = line.rstrip("\n")
            if line:
                yield line
    elif args.file:
        with open(args.file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.rstrip("\n")
                if line:
                    yield line
    elif args.text:
        yield args.text
    else:
        raise SystemExit("请通过 --text / --file / --stdin 提供文本。")


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Qwen3-TTS Realtime 流式语音合成（OpenAI Realtime 协议）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    src = p.add_argument_group("文本输入")
    src.add_argument("--text", help="直接指定一段文本")
    src.add_argument("--file", help="从文件按行读取文本，边读边合成")
    src.add_argument("--stdin", action="store_true", help="从标准输入按行流式读取")

    out = p.add_argument_group("输出")
    out.add_argument("--out", help="输出音频文件（.wav 自动加头，.pcm 为裸流）")
    out.add_argument("--play", action="store_true", help="实时播放（需 sounddevice）")
    out.add_argument("--device-out", action="store_true",
                     help="把 PCM 裸流写入 stdout，便于管道对接")

    tts = p.add_argument_group("合成参数")
    tts.add_argument("--voice", default=DEFAULT_VOICE,
                     help="音色：Cherry / Serena / Ethan / Chelsie")
    tts.add_argument("--model", default=DEFAULT_MODEL,
                     help=f"模型，默认 {DEFAULT_MODEL}")
    tts.add_argument("--instructions", help="指令控制，如“用开心的语气说”")
    tts.add_argument("--mode", default="server_commit",
                     choices=["server_commit", "commit"], help="提交模式")
    tts.add_argument("--sample-rate", type=int, default=DEFAULT_SAMPLE_RATE)
    tts.add_argument("--speech-rate", type=float, help="语速 [0.5, 2.0]")
    tts.add_argument("--volume", type=int, help="音量 [0, 100]")
    tts.add_argument("--text-delay", type=float, default=0.0,
                     help="模拟流式输入时每片文本之间的间隔秒数")

    api = p.add_argument_group("接入参数")
    api.add_argument("--api-key", default=None, help="默认读 DASHSCOPE_API_KEY")
    api.add_argument("--url", default=DEFAULT_URL)
    api.add_argument("--workspace", default=None,
                     help="业务空间 ID（默认读 DASHSCOPE_WORKSPACE）")
    api.add_argument("--debug", action="store_true", help="打印协议调试日志")
    return p


def main(argv: Optional[list[str]] = None) -> int:
    args = build_parser().parse_args(argv)

    pcm_sink = None
    if args.out:
        if args.out.lower().endswith(".wav"):
            pcm_sink = PcmToWavWriter(args.out, sample_rate=args.sample_rate)
            pcm_sink.__enter__()
        else:
            pcm_sink = open(args.out, "wb")

    speaker = StreamingSpeaker(args.sample_rate) if args.play else None

    started = time.time()
    total = 0
    try:
        stream = synthesise_stream(
            read_text_source(args),
            voice=args.voice,
            model=args.model,
            instructions=args.instructions,
            mode=args.mode,
            sample_rate=args.sample_rate,
            speech_rate=args.speech_rate,
            volume=args.volume,
            api_key=args.api_key,
            url=args.url,
            workspace=args.workspace,
            text_delay=args.text_delay,
            debug=args.debug,
        )

        for chunk in stream:
            total += len(chunk)
            if pcm_sink is not None:
                pcm_sink.write(chunk)
            if speaker is not None:
                speaker.write(chunk)
            if args.device_out:
                sys.stdout.buffer.write(chunk)
                sys.stdout.buffer.flush()

        elapsed = time.time() - started
        seconds = total / (args.sample_rate * DEFAULT_SAMPLE_WIDTH * DEFAULT_CHANNELS)
        sys.stderr.write(
            f"\n[done] 音频 {seconds:.2f}s / {total} bytes，"
            f"耗时 {elapsed:.2f}s (RTF {elapsed / max(seconds, 1e-6):.2f})\n"
        )
    except KeyboardInterrupt:
        sys.stderr.write("\n[interrupted]\n")
        return 130
    finally:
        if speaker is not None:
            speaker.close()
        if pcm_sink is not None:
            if isinstance(pcm_sink, PcmToWavWriter):
                pcm_sink.__exit__(None, None, None)
            else:
                pcm_sink.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
