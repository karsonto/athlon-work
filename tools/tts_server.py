#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Qwen3-TTS 本地模型 —— OpenAI 兼容的 Web 语音合成服务
======================================================

在本地加载 Qwen3-TTS 模型（不调用任何云 API），对外暴露 OpenAI 风格的
HTTP 接口，任何兼容 OpenAI 的客户端只要改 base_url 即可接入。

支持的接口
----------
* ``POST /v1/audio/speech``   语音合成（OpenAI 标准接口形式）
* ``GET  /v1/models``        列出可用模型（兼容探测）
* ``GET  /health``           健康检查
* ``GET  /docs``             自动生成的交互式 API 文档
* ``GET  /``                 浏览器试听页（SSE 逐块播放，边合成边出声）

流式的三种形态（同一个接口，用参数切换）
----------------------------------------
1. ``stream=false``（默认）   一次性返回完整音频文件（任意 container）
2. ``stream=true``            以 chunked 方式流式返回裸 PCM / WAV，
                              边合成边下发，首字节延迟最低
3. ``protocol=realtime``      返回 OpenAI Realtime 风格的 SSE 事件流
                              （``response.audio.delta`` 等），
                              适合浏览器 / WebSocket 网关再转发

关于 response_format 与 OpenAI 的差异
-------------------------------------
OpenAI 在不传 ``response_format`` 时默认返回 **mp3**。本服务默认值自适应：

* 有 ffmpeg → 默认 ``mp3``，与 OpenAI 完全一致
* 无 ffmpeg → 默认 ``wav``（原生输出），并记录告警

之所以降级：mp3 依赖 ffmpeg，若无条件对齐 OpenAI，则缺 ffmpeg 的环境下
「不传格式」的所有请求都会 400，比格式不一致更糟。
响应头 ``X-Audio-Format`` 始终返回实际格式，``X-Audio-Format-Defaulted: true``
表示该格式由服务端默认推断（客户端未指定），便于排查兼容问题。

设计要点
--------
* **零重实现**：复用同目录的 ``LocalStreamingTTS``（本地流式引擎），
  提示词逻辑仍由官方 ``generate_custom_voice`` 构建。
* **单例 + 串行推理**：模型只加载一次；由于单卡显存与 forward 线程安全
  限制，推理默认串行执行（可用 ``--max-concurrency`` 调整）。
* **可选转码**：pcm/wav 为原生输出，无需额外依赖；mp3/opus/flac/aac
  需要系统安装 ffmpeg，缺失时返回明确的 400 错误而非静默失败。
* **OpenAI 兼容细节**：语音名同时接受 Qwen 原生名（Vivian 等）与
  OpenAI 别名（alloy 等）；模型名宽松匹配；错误体遵循 OpenAI 格式。

依赖
----
    pip install -U qwen-tts fastapi uvicorn
    # 可选：转码 mp3/opus/flac
    brew install ffmpeg        # macOS
    apt install ffmpeg         # Linux

用法
----
# 启动服务（模型目录自行指定）
python tools/tts_server.py --model ./Qwen3-TTS-12Hz-0.6B-CustomVoice \
    --host 0.0.0.0 --port 8000

# 客户端调用（与 OpenAI SDK 形式一致）
curl -X POST http://127.0.0.1:8000/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen3-tts","input":"你好，这是本地模型合成的语音。","voice":"Vivian"}' \
  --output out.wav

# 流式（低延迟，边合成边播放）
curl -N -X POST "http://127.0.0.1:8000/v1/audio/speech?stream=true" \
  -H "Content-Type: application/json" \
  -d '{"input":"你好","voice":"Vivian","response_format":"pcm"}' \
  --output out.pcm

# OpenAI SDK 直接接入
#   client = OpenAI(base_url="http://127.0.0.1:8000/v1", api_key="not-needed")
#   resp = client.audio.speech.create(model="qwen3-tts", voice="Vivian", input="你好")

客户端接入建议（关于流式）
--------------------------
服务端已用裸 socket 验证为真流式（chunked，逐块到达）。但客户端能否
「逐块」收到，取决于客户端库自身是否缓冲：

* **SSE（``protocol=realtime``）**：任何客户端（含 httpx 同步）都能逐事件
  收到 ``response.audio.delta``，最稳妥，推荐浏览器与网关使用。
* **httpx 异步客户端**（``AsyncClient`` + ``aiter_raw``）：可逐块收到。
* **httpx 同步客户端**（``Client`` + ``iter_bytes``）：会攒批！
  OpenAI SDK 基于 httpx 同步客户端，故 ``with_streaming_response``
  在多数情况下会一次性拿到全部音频。若必须用同步客户端边收边播，
  请改用 SSE，或直接以 ``http.client`` / 裸 socket 消费 chunked 响应。
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import concurrent.futures
import json
import logging
import os
import shutil
import struct
import subprocess
import sys
import threading
import time
import uuid
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from typing import Any, AsyncIterator, Dict, Iterator, List, Optional

try:
    import numpy as np
except ImportError:  # pragma: no cover
    sys.stderr.write("[fatal] 缺少 numpy，请先安装: pip install numpy\n")
    raise SystemExit(1)

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse

# 复用同目录的本地流式引擎
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from streaming_tts_local import SAMPLE_RATE, LocalStreamingTTS  # noqa: E402

log = logging.getLogger("tts-server")


# --------------------------------------------------------------------------- #
# 常量
# --------------------------------------------------------------------------- #

# Qwen3-TTS CustomVoice 原生音色（母语最佳）
QWEN_VOICES = [
    "Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric",
    "Ryan", "Aiden", "Ono_Anna", "Sohee",
]

# OpenAI 音色别名 -> Qwen 音色，方便客户端零改动迁移。
# 仅做近似映射（性别/气质接近），具体听感请以实际试听为准。
OPENAI_VOICE_ALIASES: Dict[str, str] = {
    "alloy": "Vivian",      # 中性偏明亮 -> 明亮年轻女声
    "echo": "Ryan",         # 男声 -> 英文男声
    "fable": "Serena",      # 温暖叙事感 -> 温柔年轻女声
    "onyx": "Uncle_Fu",     # 低沉男声 -> 低沉稳重男声
    "nova": "Serena",       # 温暖女声 -> 温柔年轻女声
    "shimmer": "Ono_Anna",  # 轻快女声 -> 轻快日语女声
    "coral": "Aiden",       # 阳光女声 -> 阳光美式男声
    "sage": "Sohee",        # 平稳女声 -> 温暖韩语女声
    "ash": "Dylan",         # 沉稳男声 -> 京腔青年男声
}

# OpenAI 官方容器名 -> 本地处理方式
#   native: 直接由本服务生成，无需外部依赖
#   ffmpeg: 需要系统 ffmpeg 转码
CONTAINERS: Dict[str, str] = {
    "wav": "native",
    "pcm": "native",
    "mp3": "ffmpeg",
    "opus": "ffmpeg",
    "flac": "ffmpeg",
    "aac": "ffmpeg",
}

# 宽松接受的模型名（方便 OpenAI 客户端直接换 base_url）
ACCEPTED_MODEL_ALIASES = {"tts-1", "tts-1-hd", "gpt-4o-mini-tts", "gpt-4o-audio-preview"}

DEFAULT_MODEL = "qwen3-tts"

# OpenAI 在客户端不传 response_format 时默认返回 mp3。
# 但 mp3 需要 ffmpeg，而本服务的设计前提之一是「无外部依赖也能跑」。
# 因此默认值做自适应：有 ffmpeg 时与 OpenAI 完全一致，没有时降级为原生 wav，
# 否则会出现「不指定格式的请求全部 400」——比不兼容更糟。
OPENAI_DEFAULT_FORMAT = "mp3"
FALLBACK_DEFAULT_FORMAT = "wav"


def default_container() -> str:
    """当前环境的默认音频格式（有无 ffmpeg 决定，见上方说明）。"""
    return OPENAI_DEFAULT_FORMAT if ffmpeg_available() else FALLBACK_DEFAULT_FORMAT


def available_containers() -> List[str]:
    """
    当前环境**实际可用**的容器。

    无 ffmpeg 时不列出 mp3/opus/flac/aac——避免给出一个必然失败的建议。
    """
    has_ff = ffmpeg_available()
    return sorted(n for n, kind in CONTAINERS.items()
                  if kind == "native" or has_ff)


# --------------------------------------------------------------------------- #
# 音频工具
# --------------------------------------------------------------------------- #

def to_int16(audio: "np.ndarray") -> bytes:
    """float32 [-1,1] -> 16bit little-endian PCM bytes。"""
    clipped = np.clip(audio, -1.0, 1.0)
    return (clipped * 32767.0).astype("<i2").tobytes()


def wav_header(
    n_channels: int = 1,
    sample_rate: int = SAMPLE_RATE,
    bits: int = 16,
    data_size: Optional[int] = None,
) -> bytes:
    """
    构造标准 RIFF/WAVE 头。

    data_size=None 时写入 0xFFFFFFFF（未知长度，用于流式 chunked 传输），
    绝大多数播放器与浏览器都能正确解码到流结束。
    """
    byte_rate = sample_rate * n_channels * bits // 8
    block_align = n_channels * bits // 8
    size = 0xFFFFFFFF if data_size is None else data_size
    riff_size = 0xFFFFFFFF if data_size is None else 36 + data_size
    return (
        b"RIFF" + struct.pack("<I", riff_size) + b"WAVE"
        + b"fmt " + struct.pack("<IHHIIHH", 16, 1, n_channels, sample_rate,
                                byte_rate, block_align, bits)
        + b"data" + struct.pack("<I", size)
    )


class StreamingResampler:
    """
    有状态线性重采样器，用于实现 OpenAI 的 ``speed`` 参数。

    跨 chunk 保留小数相位与最后一个样本，保证块边界连续无接缝。
    ratio > 1 表示加速（输出变短）。
    """

    def __init__(self, ratio: float) -> None:
        self.ratio = max(0.1, float(ratio))
        self._pos = 0.0
        self._buf = np.zeros(0, dtype=np.float32)

    @property
    def passthrough(self) -> bool:
        return abs(self.ratio - 1.0) < 1e-9

    def feed(self, x: "np.ndarray") -> "np.ndarray":
        if self.passthrough:
            return x
        self._buf = np.concatenate([self._buf, x.astype(np.float32)])
        n = self._buf.shape[0]
        if n < 2:
            return np.zeros(0, dtype=np.float32)

        out: List[float] = []
        while self._pos + 1.0 < n:
            i0 = int(self._pos)
            frac = self._pos - i0
            out.append(self._buf[i0] * (1.0 - frac) + self._buf[i0 + 1] * frac)
            self._pos += self.ratio

        keep = int(self._pos)
        if keep > 0:
            self._buf = self._buf[keep:]
            self._pos -= keep
        return np.asarray(out, dtype=np.float32) if out else np.zeros(0, dtype=np.float32)

    def flush(self) -> "np.ndarray":
        if self.passthrough or self._buf.shape[0] < 2:
            self._buf = np.zeros(0, dtype=np.float32)
            return np.zeros(0, dtype=np.float32)
        tail = self._buf[-2:]
        self._buf = np.zeros(0, dtype=np.float32)
        return tail[:1].astype(np.float32)


def ffmpeg_available() -> bool:
    return shutil.which("ffmpeg") is not None


def transcode_blocking(pcm: bytes, target: str, sample_rate: int = SAMPLE_RATE) -> bytes:
    """用 ffmpeg 把 16bit PCM 转成目标容器；失败抛 RuntimeError。"""
    if not ffmpeg_available():
        raise RuntimeError(
            f"response_format={target!r} 需要系统安装 ffmpeg。"
            "请执行 `brew install ffmpeg`（macOS）或 `apt install ffmpeg`（Linux），"
            "或改用 response_format=wav / pcm（原生支持，无需额外依赖）。"
        )
    args = [
        "ffmpeg", "-hide_banner", "-loglevel", "error",
        "-f", "s16le", "-ar", str(sample_rate), "-ac", "1", "-i", "pipe:0",
        "-f", target, "pipe:1",
    ]
    proc = subprocess.run(args, input=pcm, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(
            f"ffmpeg 转码失败({target}): {proc.stderr.decode('utf-8', 'ignore')[:400]}"
        )
    return proc.stdout


# --------------------------------------------------------------------------- #
# 引擎抽象（便于替换与测试）
# --------------------------------------------------------------------------- #

@dataclass
class SpeechRequest:
    """一次合成请求的规范化参数。"""

    text: str
    voice: str
    instructions: Optional[str] = None
    speed: float = 1.0
    language: str = "Auto"


class BaseEngine:
    """
    引擎接口。测试可注入假实现，无需加载真实权重。

    约定：``stream()`` 只负责产出原始音频，**不处理 speed**；
    变速由服务层统一施加，保证任何引擎实现都自动获得该能力。
    """

    sample_rate: int = SAMPLE_RATE
    loaded: bool = False

    def ensure_loaded(self) -> None:
        raise NotImplementedError

    def list_models(self) -> List[str]:
        raise NotImplementedError

    def list_voices(self) -> List[str]:
        return list(QWEN_VOICES)

    def stream(self, req: SpeechRequest) -> Iterator["np.ndarray"]:
        """
        产出 float32 mono 音频块，采样率 ``sample_rate``。

        注意：不要在此处应用 ``req.speed``，服务层会统一重采样。
        """
        raise NotImplementedError

    def shutdown(self) -> None:
        pass


def apply_speed(
    chunks: Iterator["np.ndarray"], speed: float
) -> Iterator["np.ndarray"]:
    """
    在服务层统一施加变速（跨块有状态，保证边界连续）。

    speed > 1 加速（输出变短），speed < 1 减速（输出变长）。
    """
    resampler = StreamingResampler(speed)
    if resampler.passthrough:
        yield from chunks
        return
    for chunk in chunks:
        out = resampler.feed(chunk)
        if out.size:
            yield out
    tail = resampler.flush()
    if tail.size:
        yield tail


class QwenEngine(BaseEngine):
    """基于本地 Qwen3-TTS 权重的引擎。"""

    def __init__(
        self,
        model_path: str,
        *,
        device: str = "cuda:0",
        dtype: str = "bfloat16",
        attn: str = "flash_attention_2",
        tokenizer_path: Optional[str] = None,
        decode_every: int = 1,
        left_context: int = 25,
        served_name: str = DEFAULT_MODEL,
        max_concurrency: int = 1,
        debug: bool = False,
    ) -> None:
        self.model_path = model_path
        self.device = device
        self.dtype = dtype
        self.attn = attn
        self.tokenizer_path = tokenizer_path
        self.decode_every = decode_every
        self.left_context = left_context
        self.served_name = served_name
        self.max_concurrency = max(1, int(max_concurrency))
        self.debug = debug

        self._engine: Optional[LocalStreamingTTS] = None
        self._load_lock = threading.Lock()
        self._infer_lock = threading.Lock()   # 串行化推理（单卡）
        self._loaded = False

    @property
    def loaded(self) -> bool:
        return self._loaded

    # ---- 生命周期 ----

    def ensure_loaded(self) -> None:
        if self._loaded:
            return
        with self._load_lock:
            if self._loaded:
                return
            log.info("加载本地模型: %s (device=%s dtype=%s)",
                     self.model_path, self.device, self.dtype)
            t0 = time.time()
            try:
                self._engine = LocalStreamingTTS(
                    model_path=self.model_path,
                    device=self.device,
                    dtype=self.dtype,
                    attn_implementation=self.attn,
                    tokenizer_path=self.tokenizer_path,
                    decode_every=self.decode_every,
                    left_context_frames=self.left_context,
                    debug=self.debug,
                ).load()
            except ImportError as exc:
                raise ApiError(
                    f"加载模型所需的依赖缺失: {exc}。"
                    "请先安装 qwen-tts（pip install -U qwen-tts）。",
                    status=503, code="dependency_missing", type_="server_error",
                ) from exc
            except (OSError, ValueError) as exc:
                raise ApiError(
                    f"无法加载模型 {self.model_path!r}: {exc}。"
                    "请确认 --model 指向有效的本地模型目录，或该目录权重完整。",
                    status=503, code="model_load_failed", type_="server_error",
                ) from exc
            self._loaded = True
            log.info("模型就绪，耗时 %.1fs", time.time() - t0)

    def list_models(self) -> List[str]:
        return [self.served_name]

    def stream(self, req: SpeechRequest) -> Iterator["np.ndarray"]:
        if not self._loaded or self._engine is None:
            self.ensure_loaded()
        assert self._engine is not None
        # 串行化：同一模型实例并发 forward 不安全，且会打满显存。
        # 变速由服务层统一处理（apply_speed），此处不重复施加。
        with self._infer_lock:
            yield from self._engine.stream(
                text=req.text,
                speaker=req.voice,
                language=req.language,
                instruct=req.instructions,
            )

    def shutdown(self) -> None:
        self._loaded = False
        self._engine = None


# --------------------------------------------------------------------------- #
# OpenAI 兼容的错误体
# --------------------------------------------------------------------------- #

class ApiError(Exception):
    def __init__(self, message: str, *, status: int = 400, param: Optional[str] = None,
                 code: str = "invalid_request_error", type_: str = "invalid_request_error"):
        super().__init__(message)
        self.message = message
        self.status = status
        self.param = param
        self.code = code
        self.type = type_


def error_response(err: ApiError) -> JSONResponse:
    return JSONResponse(
        status_code=err.status,
        content={
            "error": {
                "message": err.message,
                "type": err.type,
                "param": err.param,
                "code": err.code,
            }
        },
    )


# --------------------------------------------------------------------------- #
# 请求解析
# --------------------------------------------------------------------------- #

def _resolve_voice(raw: Optional[str], engine: BaseEngine) -> str:
    """接受 Qwen 原生音色名与 OpenAI 别名，大小写不敏感。"""
    if not raw:
        return QWEN_VOICES[0]

    available = {v.lower(): v for v in engine.list_voices()}
    key = raw.strip().lower()

    if key in available:
        return available[key]

    alias = OPENAI_VOICE_ALIASES.get(key)
    if alias and alias.lower() in available:
        return available[alias.lower()]

    # 兜底：别名未映射到真实音色时，回退到默认，避免直接报错打断迁移
    if alias:
        return QWEN_VOICES[0]

    raise ApiError(
        f"不支持的 voice: {raw!r}。可用音色: {', '.join(engine.list_voices())}；"
        f"也接受 OpenAI 别名: {', '.join(sorted(OPENAI_VOICE_ALIASES))}",
        param="voice",
        code="invalid_value",
    )


def parse_speech_payload(payload: Dict[str, Any], engine: BaseEngine) -> SpeechRequest:
    """校验并规范化 OpenAI /v1/audio/speech 的请求体。"""
    text = payload.get("input")
    if text is None:
        raise ApiError("缺少必填字段 'input'。", param="input")
    if not isinstance(text, str):
        raise ApiError("'input' 必须是字符串。", param="input")
    if not text.strip():
        raise ApiError("'input' 不能为空。", param="input")
    if len(text) > 4000:
        raise ApiError(
            f"'input' 过长（{len(text)} 字符），上限 4000。"
            "长文本请自行切分为多次请求以获得流式体验。",
            param="input", code="input_too_long",
        )

    model = payload.get("model")
    known = set(engine.list_models()) | ACCEPTED_MODEL_ALIASES
    if model and str(model) not in known:
        # 宽松：只告警不拒绝，方便客户端复用既有配置
        log.warning("请求了未知 model=%r，仍使用本地模型 %s", model, engine.list_models()[0])

    voice = _resolve_voice(payload.get("voice"), engine)

    speed = payload.get("speed", 1.0)
    try:
        speed = float(speed)
    except (TypeError, ValueError):
        raise ApiError("'speed' 必须是数字。", param="speed")
    if not (0.25 <= speed <= 4.0):
        raise ApiError("'speed' 取值范围 [0.25, 4.0]。", param="speed",
                       code="invalid_value")

    instructions = payload.get("instructions") or payload.get("instruct")
    if instructions is not None and not isinstance(instructions, str):
        raise ApiError("'instructions' 必须是字符串。", param="instructions")

    language = payload.get("language") or "Auto"

    return SpeechRequest(
        text=text,
        voice=voice,
        instructions=instructions,
        speed=speed,
        language=language,
    )


# --------------------------------------------------------------------------- #
# 同步迭代器 -> 异步迭代器（带背压）
# --------------------------------------------------------------------------- #

_SENTINEL = object()


async def sync_to_async(gen: Iterator[Any], *, maxsize: int = 8) -> AsyncIterator[Any]:
    """
    把阻塞的同步生成器桥接成异步生成器。

    用有界队列实现背压：消费端不取，生产线程就阻塞，避免内存无限增长。
    消费端提前退出（如客户端断开）时，会 close 掉底层生成器以停止推理。
    """
    loop = asyncio.get_running_loop()
    queue: asyncio.Queue = asyncio.Queue(maxsize=maxsize)
    cancelled = threading.Event()

    def put(item: Any) -> None:
        """线程安全入队；若已取消则丢弃，避免向已关闭的循环投递。"""
        if cancelled.is_set():
            return
        try:
            asyncio.run_coroutine_threadsafe(queue.put(item), loop).result()
        except (RuntimeError, concurrent.futures.CancelledError):
            # 事件循环已关闭 / future 被取消：静默退出，属于正常收尾
            cancelled.set()

    def worker() -> None:
        try:
            for item in gen:
                if cancelled.is_set():
                    break
                put(item)
        except BaseException as exc:  # noqa: BLE001
            put(exc)
        finally:
            put(_SENTINEL)

    thread = threading.Thread(target=worker, name="tts-bridge", daemon=True)
    thread.start()

    try:
        while True:
            item = await queue.get()
            if item is _SENTINEL:
                break
            if isinstance(item, BaseException):
                raise item
            yield item
    finally:
        cancelled.set()
        # 让底层生成器有机会执行 finally：停止推理并回收线程
        close = getattr(gen, "close", None)
        if callable(close):
            try:
                close()
            except Exception:  # noqa: BLE001
                pass
        # 唤醒仍阻塞在 put 上的生产线程，使其尽快退出
        try:
            while not queue.full():
                queue.put_nowait(_SENTINEL)
        except Exception:  # noqa: BLE001
            pass


# --------------------------------------------------------------------------- #
# 响应体构造
# --------------------------------------------------------------------------- #

def build_http_stream(
    req: SpeechRequest,
    engine: BaseEngine,
    container: str,
    sr: int,
    semaphore: Optional[asyncio.Semaphore] = None,
) -> AsyncIterator[bytes]:
    """
    HTTP chunked 流：裸 PCM 或流式 WAV（头里声明未知长度）。

    信号量在这里获取与释放——必须跨越整个流的生命周期，
    否则并发上限会在返回 StreamingResponse 的瞬间失效。
    """
    async def gen() -> AsyncIterator[bytes]:
        acquired = False
        if semaphore is not None:
            await semaphore.acquire()
            acquired = True
        try:
            if container == "wav":
                yield wav_header(sample_rate=sr, data_size=None)
            async for audio in sync_to_async(apply_speed(engine.stream(req), req.speed)):
                data = to_int16(audio)
                if data:
                    yield data
        except ApiError:
            raise
        except Exception as exc:  # noqa: BLE001
            log.exception("流式合成失败")
            raise ApiError(
                f"合成失败: {exc}", status=500, code="synth_failed",
                type_="server_error",
            )
        finally:
            if acquired:
                semaphore.release()

    return gen()


def build_realtime_sse(
    req: SpeechRequest,
    engine: BaseEngine,
    container: str,
    sr: int,
    semaphore: Optional[asyncio.Semaphore] = None,
) -> AsyncIterator[bytes]:
    """
    OpenAI Realtime 风格的事件流（SSE）。

    事件序列与 Qwen 官方 realtime 端点保持一致：
      response.created -> response.audio.delta* -> response.audio.done -> response.done
    """
    response_id = f"resp_{uuid.uuid4().hex[:24]}"

    def sse(obj: Dict[str, Any]) -> bytes:
        return f"data: {json.dumps(obj, ensure_ascii=False)}\n\n".encode("utf-8")

    async def gen() -> AsyncIterator[bytes]:
        acquired = False
        if semaphore is not None:
            await semaphore.acquire()
            acquired = True
        try:
            yield sse({
                "type": "response.created",
                "response": {
                    "id": response_id,
                    "object": "realtime.response",
                    "status": "in_progress",
                    "output": [],
                },
            })

            total = 0
            try:
                async for audio in sync_to_async(
                    apply_speed(engine.stream(req), req.speed)
                ):
                    data = to_int16(audio)
                    if not data:
                        continue
                    total += len(data)
                    yield sse({
                        "type": "response.audio.delta",
                        "response_id": response_id,
                        "delta": base64.b64encode(data).decode("ascii"),
                        "format": "pcm",
                        "sample_rate": sr,
                        "channels": 1,
                    })
            except Exception as exc:  # noqa: BLE001
                log.exception("SSE 合成失败")
                yield sse({
                    "type": "error",
                    "error": {"message": str(exc), "type": "server_error",
                              "code": "synth_failed"},
                })
                yield sse({"type": "response.done",
                           "response": {"id": response_id, "status": "failed"}})
                yield b"data: [DONE]\n\n"
                return

            yield sse({
                "type": "response.audio.done",
                "response_id": response_id,
                "bytes": total,
                "sample_rate": sr,
            })
            yield sse({
                "type": "response.done",
                "response": {
                    "id": response_id,
                    "object": "realtime.response",
                    "status": "completed",
                    "usage": {"total_bytes": total},
                },
            })
            yield b"data: [DONE]\n\n"
        finally:
            if acquired:
                semaphore.release()

    return gen()


def build_buffered_response(
    req: SpeechRequest,
    engine: BaseEngine,
    container: str,
    sr: int,
) -> bytes:
    """非流式：合成完毕后一次性编码为完整音频文件。"""
    chunks = [to_int16(a) for a in apply_speed(engine.stream(req), req.speed)]
    pcm = b"".join(chunks)

    if container == "pcm":
        return pcm
    if container == "wav":
        return wav_header(sample_rate=sr, data_size=len(pcm)) + pcm
    return transcode_blocking(pcm, container, sample_rate=sr)


# --------------------------------------------------------------------------- #
# 应用
# --------------------------------------------------------------------------- #

MEDIA_TYPES = {
    "pcm": "audio/L16",
    "wav": "audio/wav",
    "mp3": "audio/mpeg",
    "opus": "audio/opus",
    "flac": "audio/flac",
    "aac": "audio/aac",
}


@dataclass
class ServerConfig:
    host: str = "127.0.0.1"
    port: int = 8000
    api_key: Optional[str] = None       # 非空则校验 Authorization: Bearer
    preload: bool = True                # 启动即加载模型
    max_concurrency: int = 1
    cors_origins: List[str] = field(default_factory=lambda: ["*"])


def create_app(
    engine: BaseEngine,
    config: Optional[ServerConfig] = None,
) -> FastAPI:
    """构造 FastAPI 应用。engine 可注入假实现以便测试。"""
    cfg = config or ServerConfig()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if cfg.preload:
            # 权重加载是阻塞的，放线程里避免卡住事件循环
            await asyncio.to_thread(engine.ensure_loaded)
        yield
        await asyncio.to_thread(engine.shutdown)

    app = FastAPI(
        title="Qwen3-TTS Local Server",
        description="本地 Qwen3-TTS 模型的 OpenAI 兼容语音合成服务",
        version="1.0.0",
        lifespan=lifespan,
    )

    app.add_middleware(
        CORSMiddleware,
        allow_origins=cfg.cors_origins,
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
        expose_headers=["X-Sample-Rate", "X-Channels", "X-Audio-Format",
                        "X-Audio-Format-Defaulted"],
    )

    semaphore = asyncio.Semaphore(cfg.max_concurrency)

    # ---- 鉴权 ----
    def check_auth(request: Request) -> None:
        if not cfg.api_key:
            return
        header = request.headers.get("authorization") or ""
        token = header[7:].strip() if header.lower().startswith("bearer ") else ""
        if token != cfg.api_key:
            raise ApiError(
                "Invalid API key provided.",
                status=401, param=None, code="invalid_api_key",
                type_="invalid_request_error",
            )

    @app.exception_handler(ApiError)
    async def _api_error_handler(_: Request, exc: ApiError) -> JSONResponse:
        return error_response(exc)

    # ---- 基础端点 ----

    @app.get("/")
    async def demo_page() -> FileResponse:
        """浏览器试听页（不存在时给出提示而非 500）。"""
        page = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "tts_demo.html")
        if not os.path.exists(page):
            raise ApiError(
                "未找到试听页 tts_demo.html。可直接访问 /docs 使用交互式文档。",
                status=404, code="demo_page_missing",
            )
        return FileResponse(page, media_type="text/html; charset=utf-8")

    @app.get("/health")
    async def health() -> Dict[str, Any]:
        """存活探针：进程活着即 200，始终轻量。"""
        return {
            "status": "ok",
            "model": engine.list_models()[0],
            "model_loaded": engine.loaded,
            "sample_rate": engine.sample_rate,
            "ffmpeg": ffmpeg_available(),
            "default_format": default_container(),
            "available_formats": available_containers(),
            "voices": engine.list_voices(),
        }

    @app.get("/ready")
    async def ready() -> JSONResponse:
        """
        就绪探针：模型加载完成后才 200，否则 503。

        负载均衡器/容器编排应使用本端点判断能否接流量——
        模型加载通常需要数十秒，期间不应把请求转发过来。
        与 /health 分开，避免未就绪被误判为不健康而被重启。
        """
        if not engine.loaded:
            return JSONResponse(
                status_code=503,
                content={"status": "loading", "ready": False},
            )
        return JSONResponse(status_code=200, content={"status": "ready", "ready": True})

    @app.get("/v1/models")
    async def list_models() -> Dict[str, Any]:
        now = int(time.time())
        return {
            "object": "list",
            "data": [
                {"id": name, "object": "model", "created": now, "owned_by": "local"}
                for name in engine.list_models()
            ],
        }

    # ---- 核心：语音合成 ----

    @app.post("/v1/audio/speech")
    async def speech(request: Request) -> Any:
        check_auth(request)

        try:
            payload = await request.json()
        except Exception:  # noqa: BLE001
            raise ApiError("请求体不是合法 JSON。")
        if not isinstance(payload, dict):
            raise ApiError("请求体必须是 JSON 对象。")

        req = parse_speech_payload(payload, engine)

        # 默认格式：对齐 OpenAI（mp3），但无 ffmpeg 时降级为原生 wav，
        # 否则缺 ffmpeg 的环境下「不传 response_format」会全部失败。
        explicit = payload.get("response_format")
        container = str(explicit or default_container()).lower()
        defaulted = not explicit

        if container not in CONTAINERS:
            raise ApiError(
                f"不支持的 response_format: {container!r}。"
                f"当前环境可用: {', '.join(available_containers())}"
                f"（默认 {default_container()}）",
                param="response_format", code="invalid_value",
            )
        if CONTAINERS[container] == "ffmpeg" and not ffmpeg_available():
            raise ApiError(
                f"response_format={container!r} 需要系统安装 ffmpeg；"
                "当前环境未检测到。请安装 ffmpeg，"
                f"或改用 {', '.join(available_containers())}。",
                param="response_format", code="encoder_unavailable",
            )

        qs = request.query_params
        stream = str(qs.get("stream", payload.get("stream", "false"))).lower() in (
            "1", "true", "yes",
        )
        protocol = str(
            qs.get("protocol", payload.get("protocol", "http"))
        ).lower()

        sr = engine.sample_rate
        headers = {
            "X-Sample-Rate": str(sr),
            "X-Channels": "1",
            "X-Audio-Format": container,
            "Cache-Control": "no-store",
        }
        if defaulted:
            # 显式告知客户端：本次格式是服务端默认推断的，便于排查兼容问题
            headers["X-Audio-Format-Defaulted"] = "true"
            if container != OPENAI_DEFAULT_FORMAT:
                log.info("未指定 response_format，已降级为 %s（无 ffmpeg，"
                         "OpenAI 默认 %s 不可用）", container, OPENAI_DEFAULT_FORMAT)

        if protocol == "realtime":
            if container not in ("pcm", "wav"):
                raise ApiError(
                    "realtime 协议仅支持 response_format=pcm 或 wav。",
                    param="response_format", code="stream_unsupported_format",
                )
            headers["Content-Type"] = "text/event-stream; charset=utf-8"
            gen = build_realtime_sse(req, engine, container, sr, semaphore)
            return StreamingResponse(gen, media_type=None, headers=headers)

        if stream:
            if container not in ("pcm", "wav"):
                raise ApiError(
                    f"流式模式仅支持 response_format=pcm 或 wav，收到 {container!r}。"
                    "其它容器请使用 stream=false。",
                    param="response_format", code="stream_unsupported_format",
                )
            gen = build_http_stream(req, engine, container, sr, semaphore)
            return StreamingResponse(
                gen, media_type=MEDIA_TYPES[container], headers=headers
            )

        # 非流式：整段合成完毕后一次性返回
        data = await _run_buffered(req, engine, container, sr, semaphore)
        return StreamingResponse(
            iter([data]), media_type=MEDIA_TYPES[container], headers=headers
        )

    return app


async def _run_buffered(
    req: SpeechRequest,
    engine: BaseEngine,
    container: str,
    sr: int,
    semaphore: asyncio.Semaphore,
) -> bytes:
    """
    非流式路径：在持有信号量期间完成后台线程合成与编码。

    用 run_in_executor 而非 to_thread，确保拿到真正的后台线程句柄，
    从而能在事件循环被取消时正确等待清理，避免信号量泄漏。
    """
    async with semaphore:
        loop = asyncio.get_running_loop()
        try:
            return await loop.run_in_executor(
                None, build_buffered_response, req, engine, container, sr
            )
        except ApiError:
            raise
        except Exception as exc:  # noqa: BLE001
            log.exception("非流式合成失败")
            raise ApiError(
                f"合成失败: {exc}", status=500, code="synth_failed",
                type_="server_error",
            )


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def resolve_api_key(cli_value: Optional[str]) -> Optional[str]:
    """
    按优先级解析 API key，避免把密钥写进命令行（会被 ps aux 看到）：

      1. 环境变量 ``TTS_API_KEY``（推荐，便于容器注入 secret）
      2. 文件 ``TTS_API_KEY_FILE``（推荐，Docker/K8s secret 挂载）
      3. 命令行 ``--api-key``（方便本地调试，容器环境不推荐）

    委派关系：容器编排优先用环境变量；K8s 用文件挂载更佳。
    """
    env_val = os.environ.get("TTS_API_KEY")
    if env_val:
        return env_val.strip() or None

    file_path = os.environ.get("TTS_API_KEY_FILE")
    if file_path:
        try:
            with open(file_path, "r", encoding="utf-8") as f:
                content = f.read().strip()
        except OSError as exc:
            raise SystemExit(f"[fatal] 无法读取 TTS_API_KEY_FILE={file_path}: {exc}")
        return content or None

    if cli_value:
        log.warning("通过 --api-key 传入密钥会暴露在进程列表中，"
                    "生产环境请改用 TTS_API_KEY 环境变量或 TTS_API_KEY_FILE 文件")
        return cli_value
    return None


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Qwen3-TTS 本地模型的 OpenAI 兼容 Web 服务",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    m = p.add_argument_group("模型")
    m.add_argument("--model", required=True, help="本地模型目录或 HF repo id")
    m.add_argument("--device", default="cuda:0")
    m.add_argument("--dtype", default="bfloat16",
                   choices=["bfloat16", "float16", "float32"])
    m.add_argument("--attn", default="flash_attention_2")
    m.add_argument("--tokenizer-dir", default=None,
                   help="speech_tokenizer 目录（模型目录内未包含时单独挂载）")
    m.add_argument("--served-model-name", default=DEFAULT_MODEL,
                   help="对外暴露的模型名（OpenAI 的 model 字段）")
    m.add_argument("--no-preload", action="store_true",
                   help="启动时不加载模型，首个请求再加载")

    st = p.add_argument_group("流式调优")
    st.add_argument("--decode-every", type=int, default=1,
                    help="每攒 N 帧解码一次；1=最低延迟")
    st.add_argument("--left-context", type=int, default=25)
    st.add_argument("--max-concurrency", type=int, default=1,
                    help="并发合成上限（单卡建议 1）")

    s = p.add_argument_group("服务")
    s.add_argument("--host", default="0.0.0.0")
    s.add_argument("--port", type=int, default=8000)
    s.add_argument("--api-key", default=None,
                   help="API 密钥（不推荐：会暴露在 ps 中）。"
                        "生产请用 TTS_API_KEY 或 TTS_API_KEY_FILE")
    s.add_argument("--cors", default="*", help="逗号分隔的允许来源")
    s.add_argument("--debug", action="store_true")
    s.add_argument("--log-level", default="info")
    return p


def main(argv: Optional[List[str]] = None) -> int:
    args = build_parser().parse_args(argv)

    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )

    if not ffmpeg_available():
        log.warning(
            "未检测到 ffmpeg：mp3/opus/flac/aac 不可用。"
            "为避免 OpenAI 客户端不传 response_format 时报错，"
            "默认格式已降级为 wav（原生输出）。"
            "如需与 OpenAI 一致的 mp3 默认值，请安装 ffmpeg（apt install ffmpeg）"
        )
    else:
        log.info("已检测到 ffmpeg，默认格式 mp3（与 OpenAI 一致）")

    engine = QwenEngine(
        model_path=args.model,
        device=args.device,
        dtype=args.dtype,
        attn=args.attn,
        tokenizer_path=args.tokenizer_dir,
        decode_every=args.decode_every,
        left_context=args.left_context,
        served_name=args.served_model_name,
        max_concurrency=args.max_concurrency,
        debug=args.debug,
    )

    cfg = ServerConfig(
        host=args.host,
        port=args.port,
        api_key=resolve_api_key(args.api_key),
        preload=not args.no_preload,
        max_concurrency=args.max_concurrency,
        cors_origins=[o.strip() for o in args.cors.split(",") if o.strip()],
    )

    app = create_app(engine, cfg)

    import uvicorn

    if args.api_key or os.environ.get("TTS_API_KEY") or os.environ.get("TTS_API_KEY_FILE"):
        log.info("已启用 API 密钥校验（Authorization: Bearer <key>）")
    else:
        log.warning("未设置 API 密钥：任何能访问该端口的客户端都可调用，"
                    "公网部署请设置 TTS_API_KEY")

    log.info("服务地址: http://%s:%d  文档: /docs  就绪探针: /ready",
             args.host, args.port)
    # 注意：不可加 workers>1 —— 模型会在每个 worker 各加载一份，显存成倍占用。
    # 单进程事件循环已足够支撑多路并发（推理由信号量串行化）。
    uvicorn.run(app, host=args.host, port=args.port, log_level=args.log_level)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
