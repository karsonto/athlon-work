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

语音克隆（可选）
----------------
克隆需要 **Base 模型**（``Qwen3-TTS-12Hz-1.7B/0.6B-Base``），CustomVoice 权重
无法克隆，因此本服务支持同时挂两个模型：

    --model        CustomVoice（预置音色）
    --clone-model  Base（语音克隆），按需懒加载，不用不占显存

请求里给 ``ref_audio`` + ``ref_text`` 即走克隆路径。两种用法：

1. **临时克隆**：请求体直接带参考条件，适合一次性试用::

       {"input":"用这个声音说","ref_audio":"./ref/a.wav","ref_text":"参考音频里的话"}

   出于安全考虑，``ref_audio`` 若是本地路径，必须落在 ``--ref-audio-dir`` 之内
   （未配置该参数则只接受 http(s) URL 或 base64，避免把本服务变成任意文件读取器）。

2. **注册音色**：用 ``--clone-voices voices.json`` 预先注册，之后客户端只需传
   ``voice`` 名字，与预置音色用法完全一致（名字会出现在 ``/health`` 的 ``voices``
   里，因此 OpenAI 兼容客户端无需任何改动）::

       {"voices":[{"name":"laoban","ref_audio":"./voices/laoban.wav",
                   "ref_text":"参考音频里说的话","x_vector_only_mode":false}]}

``x_vector_only_mode=true`` 时只需参考音频、无需 ``ref_text``，但官方明确说明
克隆质量会下降；要最佳相似度请提供准确的参考文字。

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
  提示词逻辑仍由官方 ``generate_custom_voice`` / ``generate_voice_clone`` 构建。
* **单例 + 串行推理**：模型只加载一次；由于单卡显存与 forward 线程安全
  限制，推理默认串行执行（可用 ``--max-concurrency`` 调整）。
* **克隆模型的按需加载**：CustomVoice 启动即加载；Base（克隆）在首个克隆
  请求时才加载，未配置克隆的部署完全不受影响。
* **参考音频安全边界**：本地路径限定在 ``--ref-audio-dir`` 内（realpath 比较，
  阻断 ``../`` 与符号链接逃逸），远程 URL 拒绝内网/环回地址（SSRF 防护），
  不配置白名单则只接受 base64。
* **可选转码**：pcm/wav 为原生输出，无需额外依赖；mp3/opus/flac/aac
  需要系统安装 ffmpeg，缺失时返回明确的 400 错误而非静默失败。
* **OpenAI 兼容细节**：语音名同时接受 Qwen 原生名（Vivian 等）与
  OpenAI 别名（alloy 等）；克隆音色注册后同样出现在 ``/health`` 的 ``voices``
  与 ``voice`` 参数里，因此客户端零改动；模型名宽松匹配；错误体遵循 OpenAI 格式。

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

# 同时开启语音克隆（预置音色 + 克隆两套能力并存）
python tools/tts_server.py --model ./Qwen3-TTS-12Hz-0.6B-CustomVoice \
    --clone-model ./Qwen3-TTS-12Hz-1.7B-Base \
    --ref-audio-dir ./voices --clone-voices ./voices/voices.json

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
import ipaddress
import json
import logging
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from typing import Any, AsyncIterator, Dict, Iterator, List, Optional
from urllib.parse import urlparse

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
from streaming_tts_local import (  # noqa: E402
    SAMPLE_RATE,
    LocalStreamingTTS,
    VoiceCloneSpec,
    validate_clone_spec,
)

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
# 语音克隆：参考音频安全校验与音色注册
# --------------------------------------------------------------------------- #

def _looks_like_base64(value: str) -> bool:
    """
    粗略判定「内联 base64」而非文件路径。

    与官方 qwen-tts 的判定保持一致：带 ``data:audio`` 前缀，或没有路径分隔符
    且长度较长（内联音频必然很长）。这种输入不需要落盘校验。
    """
    text = value.strip()
    if text.startswith("data:audio"):
        return True
    return ("/" not in text and "\\" not in text) and len(text) > 256


def _is_remote_url(value: str) -> bool:
    try:
        parsed = urlparse(value.strip())
    except ValueError:
        return False
    return parsed.scheme in ("http", "https") and bool(parsed.netloc)


def _host_is_private(hostname: Optional[str]) -> bool:
    """
    判断主机名是否指向内网/环回/链路本地地址。

    本服务会在服务端 ``urllib.request.urlopen`` 拉取 ``ref_audio``，若不加限制，
    调用方可用 ``http://169.254.169.254/...`` 之类地址探测内网（SSRF）。
    """
    if not hostname:
        return True
    if hostname.lower() in ("localhost", "localhost.localdomain"):
        return True
    try:
        infos = socket.getaddrinfo(hostname, None)
    except socket.gaierror:
        # 解析不了就不放行，避免把判断留给后续的 urlopen
        return True
    for info in infos:
        address = info[4][0]
        try:
            ip = ipaddress.ip_address(address)
        except ValueError:
            return True
        if (
            ip.is_private
            or ip.is_loopback
            or ip.is_link_local
            or ip.is_reserved
            or ip.is_multicast
            or ip.is_unspecified
        ):
            return True
    return False


def validate_ref_audio(
    ref_audio: Any,
    ref_dir: Optional[str],
    *,
    allow_remote: bool = True,
) -> Any:
    """
    校验请求里的 ``ref_audio``，返回可安全交给引擎的值。

    * 内联 base64：直接放行（不落盘、不可用于探测文件系统）。
    * http(s) URL：仅在 ``allow_remote`` 且非内网地址时放行。
      注意这只能挡住「直接用 IP/域名探测」，DNS rebinding 等深度攻击面需要
      在出口层面限制；本服务默认面向可信内网，故采用这一档强度。
    * 本地路径：必须配置 ``--ref-audio-dir`` 且解析后仍在该目录内
      （用 realpath 比较，阻断 ``../`` 与符号链接两种逃逸）。

    校验失败抛 :class:`ApiError`（400）。
    """
    if isinstance(ref_audio, (list, tuple)) and not isinstance(ref_audio, str):
        # (ndarray, sr) 这类内联输入不在本层校验范围（无法从 HTTP 传入）
        raise ApiError(
            "'ref_audio' 只接受字符串（本地路径 / http(s) URL / base64）。",
            param="ref_audio", code="invalid_value",
        )
    if not isinstance(ref_audio, str) or not ref_audio.strip():
        raise ApiError(
            "'ref_audio' 必须是非空字符串（本地路径 / http(s) URL / base64）。",
            param="ref_audio", code="invalid_value",
        )

    value = ref_audio.strip()

    if _looks_like_base64(value):
        return value

    if _is_remote_url(value):
        if not allow_remote:
            raise ApiError(
                "当前服务已禁止远程 ref_audio，请改用本地路径或 base64。",
                param="ref_audio", code="remote_ref_audio_disabled",
            )
        hostname = urlparse(value).hostname
        if _host_is_private(hostname):
            raise ApiError(
                f"拒绝拉取内网/环回地址的参考音频（{hostname}）。"
                "这属于服务端请求伪造（SSRF）防护，请使用可公开访问的地址"
                "或直接上传参考音频到 --ref-audio-dir 目录。",
                param="ref_audio", code="ref_audio_blocked",
            )
        return value

    if not ref_dir:
        raise ApiError(
            "未配置 --ref-audio-dir，出于安全考虑不接受本地路径形式的 ref_audio。"
            "请用 --ref-audio-dir 指定参考音频目录（并把音频放进该目录），"
            "或改用 http(s) URL / base64。",
            param="ref_audio", code="ref_audio_path_not_allowed",
        )

    base = os.path.realpath(ref_dir)
    candidate = os.path.realpath(
        value if os.path.isabs(value) else os.path.join(base, value)
    )
    # 前缀比较必须带分隔符，否则 /data/voices-evil 会骗过 /data/voices
    if candidate != base and not candidate.startswith(base + os.sep):
        raise ApiError(
            f"ref_audio 越界：只允许读取 --ref-audio-dir（{base}）内的文件。",
            param="ref_audio", code="ref_audio_outside_dir",
        )
    if not os.path.isfile(candidate):
        raise ApiError(
            f"参考音频不存在：{candidate}",
            param="ref_audio", code="ref_audio_not_found",
        )
    return candidate


@dataclass
class CloneVoice:
    """一个预先注册的克隆音色。"""

    name: str
    ref_audio: Any
    ref_text: Optional[str] = None
    x_vector_only_mode: bool = False
    language: str = "Auto"

    def spec(self) -> VoiceCloneSpec:
        return VoiceCloneSpec(
            ref_audio=self.ref_audio,
            ref_text=self.ref_text,
            x_vector_only_mode=self.x_vector_only_mode,
        )


class CloneVoiceRegistry:
    """
    克隆音色表：把「名字」映射到「参考音频 + 参考文字」。

    有了它，客户端可以像用预置音色一样只传 ``voice`` 名字，音色名也会出现在
    ``/health`` 的 ``voices`` 里，因此 OpenAI 兼容客户端零改动即可选用。
    """

    def __init__(self) -> None:
        self._by_name: Dict[str, CloneVoice] = {}

    def register(self, voice: CloneVoice) -> None:
        self._by_name[voice.name] = voice

    def get(self, name: Optional[str]) -> Optional[CloneVoice]:
        if not name:
            return None
        return self._by_name.get(name.strip())

    def __len__(self) -> int:
        return len(self._by_name)

    def __contains__(self, name: object) -> bool:
        return isinstance(name, str) and name in self._by_name

    def names(self) -> List[str]:
        return list(self._by_name)


def load_clone_voices(path: str) -> List[CloneVoice]:
    """
    读取 ``--clone-voices`` 指定的 JSON，返回注册项。

    格式::

        {"voices": [{"name": "laoban", "ref_audio": "./voices/laoban.wav",
                     "ref_text": "参考音频里说的话",
                     "x_vector_only_mode": false,
                     "language": "Chinese"}]}

    不支持 ``instructions``（风格指令）：Base 克隆模型没有该能力，官方会静默
    丢弃这个参数，故这里也不提供，避免给出一个不会生效的配置项。
    """
    try:
        with open(path, "r", encoding="utf-8") as fh:
            payload = json.load(fh)
    except OSError as exc:
        raise SystemExit(f"[fatal] 无法读取 --clone-voices={path}: {exc}")
    except json.JSONDecodeError as exc:
        raise SystemExit(f"[fatal] --clone-voices={path} 不是合法 JSON: {exc}")

    raw = payload.get("voices") if isinstance(payload, dict) else payload
    if not isinstance(raw, list):
        raise SystemExit(
            f"[fatal] --clone-voices={path} 应为 JSON 数组，或形如 {{\"voices\": [...]}}。"
        )

    voices: List[CloneVoice] = []
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            raise SystemExit(f"[fatal] --clone-voices 第 {index} 项不是对象。")
        name = str(item.get("name") or "").strip()
        if not name:
            raise SystemExit(f"[fatal] --clone-voices 第 {index} 项缺少 name。")
        voices.append(CloneVoice(
            name=name,
            ref_audio=str(item.get("ref_audio") or "").strip(),
            ref_text=item.get("ref_text"),
            x_vector_only_mode=bool(item.get("x_vector_only_mode", False)),
            language=str(item.get("language") or "Auto"),
        ))
    return voices


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
    # 非空即走语音克隆路径（要求引擎为 Base 模型）
    clone: Optional[VoiceCloneSpec] = None


class BaseEngine:
    """
    引擎接口。测试可注入假实现，无需加载真实权重。

    约定：``stream()`` 只负责产出原始音频，**不处理 speed**；
    变速由服务层统一施加，保证任何引擎实现都自动获得该能力。
    """

    sample_rate: int = SAMPLE_RATE
    loaded: bool = False
    # 该引擎是否支持语音克隆；假引擎/远端引擎可覆写。
    supports_voice_clone: bool = False
    # 引擎已知的音色名（含克隆音色），供服务层做参数校验。
    voices: List[str] = []

    def ensure_loaded(self) -> None:
        raise NotImplementedError

    def list_models(self) -> List[str]:
        raise NotImplementedError

    def list_voices(self) -> List[str]:
        return list(self.voices)

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
    """
    基于本地 Qwen3-TTS 权重的引擎。

    支持同时挂两个模型：CustomVoice（预置音色）与 Base（语音克隆）。
    Base 模型**按需懒加载**——没配 ``--clone-model`` 或没发克隆请求时，
    它完全不占显存；两个模型各自持有独立的推理锁，互不阻塞。
    """

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
        clone_model_path: Optional[str] = None,
        clone_tokenizer_path: Optional[str] = None,
        clone_voices: Optional[List[CloneVoice]] = None,
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

        # 克隆侧：独立的引擎实例、加载锁与推理锁
        self.clone_model_path = clone_model_path
        self.clone_tokenizer_path = clone_tokenizer_path
        self._clone_engine: Optional[LocalStreamingTTS] = None
        self._clone_load_lock = threading.Lock()
        self._clone_infer_lock = threading.Lock()

        self.clone_registry = CloneVoiceRegistry()
        for voice in clone_voices or []:
            self.clone_registry.register(voice)

    @property
    def loaded(self) -> bool:
        return self._loaded

    @property
    def clone_loaded(self) -> bool:
        return self._clone_engine is not None

    @property
    def supports_voice_clone(self) -> bool:  # type: ignore[override]
        """配了克隆模型、或已注册的音色引用了当前引擎可用的模型。"""
        return bool(self.clone_model_path) or len(self.clone_registry) > 0

    def list_models(self) -> List[str]:
        names = [self.served_name]
        if self.clone_model_path:
            names.append(f"{self.served_name}-clone")
        return names

    def list_voices(self) -> List[str]:
        """预置音色 + 已注册的克隆音色（喂给 /health，客户端据此填充下拉框）。"""
        voices = list(QWEN_VOICES)
        for name in self.clone_registry.names():
            if name not in voices:
                voices.append(name)
        return voices

    # ---- 生命周期 ----

    @staticmethod
    def _build_engine(
        model_path: str, tokenizer_path: Optional[str], *,
        device: str, dtype: str, attn: str, decode_every: int,
        left_context: int, debug: bool, label: str,
    ) -> LocalStreamingTTS:
        log.info("加载本地模型[%s]: %s (device=%s dtype=%s)",
                 label, model_path, device, dtype)
        try:
            return LocalStreamingTTS(
                model_path=model_path,
                device=device,
                dtype=dtype,
                attn_implementation=attn,
                tokenizer_path=tokenizer_path,
                decode_every=decode_every,
                left_context_frames=left_context,
                debug=debug,
            ).load()
        except ImportError as exc:
            raise ApiError(
                f"加载模型所需的依赖缺失: {exc}。"
                "请先安装 qwen-tts（pip install -U qwen-tts）。",
                status=503, code="dependency_missing", type_="server_error",
            ) from exc
        except (OSError, ValueError) as exc:
            raise ApiError(
                f"无法加载模型 {model_path!r}: {exc}。"
                f"请确认 --{label} 指向有效的本地模型目录，或该目录权重完整。",
                status=503, code="model_load_failed", type_="server_error",
            ) from exc

    def ensure_loaded(self) -> None:
        if self._loaded:
            return
        with self._load_lock:
            if self._loaded:
                return
            t0 = time.time()
            self._engine = self._build_engine(
                self.model_path, self.tokenizer_path,
                device=self.device, dtype=self.dtype, attn=self.attn,
                decode_every=self.decode_every, left_context=self.left_context,
                debug=self.debug, label="model",
            )
            self._loaded = True
            log.info("模型就绪，耗时 %.1fs", time.time() - t0)

    def ensure_clone_loaded(self) -> LocalStreamingTTS:
        """
        懒加载 Base 模型（语音克隆用）。

        未配置 --clone-model 时给出可操作的错误，而不是静默回退到 CustomVoice
        ——那样只会得到一个「克隆失败」的怪结果。
        """
        if self._clone_engine is not None:
            return self._clone_engine
        if not self.clone_model_path:
            raise ApiError(
                "本服务未配置语音克隆模型。请在启动参数中加入 "
                "--clone-model <Qwen3-TTS-12Hz-1.7B-Base 目录>。",
                status=400, param="ref_audio", code="voice_clone_unavailable",
            )
        with self._clone_load_lock:
            if self._clone_engine is None:
                t0 = time.time()
                self._clone_engine = self._build_engine(
                    self.clone_model_path, self.clone_tokenizer_path,
                    device=self.device, dtype=self.dtype, attn=self.attn,
                    decode_every=self.decode_every,
                    left_context=self.left_context, debug=self.debug,
                    label="clone-model",
                )
                log.info("克隆模型就绪，耗时 %.1fs", time.time() - t0)
        return self._clone_engine

    def stream(self, req: SpeechRequest) -> Iterator["np.ndarray"]:
        if req.clone is not None:
            yield from self._stream_clone(req)
            return

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

    def _stream_clone(self, req: SpeechRequest) -> Iterator["np.ndarray"]:
        assert req.clone is not None
        engine = self.ensure_clone_loaded()
        if not engine.supports_voice_clone:
            raise ApiError(
                f"语音克隆需要 Base 模型，--clone-model 当前是 "
                f"{engine.model_type!r} 类型。请指向 Qwen3-TTS-12Hz-1.7B-Base。",
                status=400, param="ref_audio", code="voice_clone_unavailable",
            )
        # 与预置音色共用同一条流式路径，只是提示词由官方 generate_voice_clone 构建；
        # 这里同样要串行化（talker.generate 的 patch 是类级的，非线程安全）。
        with self._clone_infer_lock:
            yield from engine.stream(
                text=req.text,
                language=req.language,
                instruct=req.instructions,
                ref_audio=req.clone.ref_audio,
                ref_text=req.clone.ref_text,
                x_vector_only_mode=req.clone.x_vector_only_mode,
            )

    def shutdown(self) -> None:
        self._loaded = False
        self._engine = None
        self._clone_engine = None


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
    """接受 Qwen 原生音色名、已注册的克隆音色名与 OpenAI 别名，大小写不敏感。"""
    if not raw:
        return QWEN_VOICES[0]

    available = {v.lower(): v for v in engine.list_voices()}
    key = raw.strip().lower()

    if key in available:
        return available[key]

    # 已注册的克隆音色：Base 模型的 list_voices() 为空，此时按原样透传，
    # 由 service 层去注册表里解析参考音频。
    if getattr(engine, "clone_registry", None) is not None and key in getattr(
        engine, "clone_registry"
    ):
        return key

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


def _resolve_clone_voice(
    voice_name: str,
    engine: BaseEngine,
    ref_audio: Optional[str],
    ref_text: Optional[str],
    x_vector_only_mode: bool,
    ref_dir: Optional[str],
    allow_remote_ref: bool,
) -> Optional[VoiceCloneSpec]:
    """
    决定本次请求是否走语音克隆，并返回规范化的参考条件。

    优先级：请求体显式给的 ref_audio（临时克隆）> ``voice`` 命中的注册音色。
    两者都没有则返回 None（走预置音色）。
    """
    registry = getattr(engine, "clone_registry", None)

    if ref_audio is not None:
        if not engine.supports_voice_clone:
            raise ApiError(
                "本服务未启用语音克隆。请用 --clone-model 指定 Base 模型"
                "（Qwen3-TTS-12Hz-1.7B-Base）。",
                status=400, param="ref_audio", code="voice_clone_unavailable",
            )
        safe_ref = validate_ref_audio(ref_audio, ref_dir, allow_remote=allow_remote_ref)
        spec = VoiceCloneSpec(
            ref_audio=safe_ref,
            ref_text=ref_text,
            x_vector_only_mode=x_vector_only_mode,
        )
        try:
            # 在昂贵操作（加载模型 / 编码音频）之前就把参数问题挡掉
            validate_clone_spec(spec)
        except ValueError as exc:
            raise ApiError(str(exc), param="ref_text", code="invalid_value") from exc
        return spec

    registered = registry.get(voice_name) if registry is not None else None
    if registered is not None:
        if not engine.supports_voice_clone:
            raise ApiError(
                f"音色 {voice_name!r} 是克隆音色，但本服务未启用语音克隆模型。",
                status=400, param="voice", code="voice_clone_unavailable",
            )
        safe_ref = validate_ref_audio(
            registered.ref_audio, ref_dir, allow_remote=allow_remote_ref
        )
        return VoiceCloneSpec(
            ref_audio=safe_ref,
            ref_text=ref_text if ref_text is not None else registered.ref_text,
            x_vector_only_mode=(
                x_vector_only_mode or registered.x_vector_only_mode
            ),
        )

    return None


def parse_speech_payload(
    payload: Dict[str, Any],
    engine: BaseEngine,
    *,
    ref_dir: Optional[str] = None,
    allow_remote_ref: bool = True,
) -> SpeechRequest:
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

    # ---- 语音克隆：显式 ref_audio 或注册音色名 ----
    ref_audio = payload.get("ref_audio")
    if ref_audio is not None and not isinstance(ref_audio, str):
        raise ApiError(
            "'ref_audio' 必须是字符串（本地路径 / http(s) URL / base64）。",
            param="ref_audio", code="invalid_value",
        )
    ref_text = payload.get("ref_text")
    if ref_text is not None and not isinstance(ref_text, str):
        raise ApiError("'ref_text' 必须是字符串。", param="ref_text")

    x_vector_only_mode = bool(
        payload.get("x_vector_only_mode", payload.get("x_vector_only", False))
    )

    clone = _resolve_clone_voice(
        voice, engine, ref_audio, ref_text, x_vector_only_mode,
        ref_dir, allow_remote_ref,
    )

    # 注册音色可以带默认语言；请求没显式指定时用注册值补上。
    # 注意不回填 instructions：Base 克隆模型不支持风格指令，官方会静默丢弃，
    # 回填等于向调用方承诺一个不会生效的行为。
    if clone is not None and ref_audio is None:
        registered = getattr(engine, "clone_registry", None)
        item = registered.get(voice) if registered is not None else None
        if item is not None and language == "Auto" and item.language:
            language = item.language

    return SpeechRequest(
        text=text,
        voice=voice,
        instructions=instructions,
        speed=speed,
        language=language,
        clone=clone,
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
    # 参考音频白名单目录；为空则拒绝本地路径形式的 ref_audio（只允许 URL/base64）
    ref_audio_dir: Optional[str] = None
    # 是否允许服务端代为拉取远程 ref_audio（关闭可彻底消除 SSRF 面）
    allow_remote_ref_audio: bool = True


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
        registry = getattr(engine, "clone_registry", None)
        return {
            "status": "ok",
            "model": engine.list_models()[0],
            "model_loaded": engine.loaded,
            "sample_rate": engine.sample_rate,
            "ffmpeg": ffmpeg_available(),
            "default_format": default_container(),
            "available_formats": available_containers(),
            "voices": engine.list_voices(),
            # 语音克隆能力：客户端可据此决定是否显示克隆相关 UI
            "supports_voice_clone": bool(engine.supports_voice_clone),
            "clone_model_loaded": bool(getattr(engine, "clone_loaded", False)),
            "clone_voices": registry.names() if registry is not None else [],
            "ref_audio_dir": cfg.ref_audio_dir,
            "allow_remote_ref_audio": cfg.allow_remote_ref_audio,
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

        req = parse_speech_payload(
            payload, engine,
            ref_dir=cfg.ref_audio_dir,
            allow_remote_ref=cfg.allow_remote_ref_audio,
        )

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

    c = p.add_argument_group(
        "语音克隆",
        description="需要 Base 模型（Qwen3-TTS-12Hz-1.7B-Base）。"
                    "克隆模型按需懒加载，不用不占显存。",
    )
    c.add_argument("--clone-model", default=None,
                   help="Base 模型的本地目录（启用语音克隆）")
    c.add_argument("--clone-tokenizer-dir", default=None,
                   help="Base 模型的 speech_tokenizer 目录（模型内未包含时）")
    c.add_argument("--clone-voices", default=None,
                   help="预注册克隆音色的 JSON 文件，见文件头说明")
    c.add_argument("--ref-audio-dir", default=None,
                   help="允许作为 ref_audio 的参考音频目录（安全白名单）。"
                        "不配置则请求只能使用 URL / base64")
    c.add_argument("--no-remote-ref-audio", action="store_true",
                   help="禁止服务端拉取远程 ref_audio（彻底消除 SSRF 面）")

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


def _prepare_clone_config(args: argparse.Namespace) -> tuple[List[CloneVoice], Optional[str]]:
    """
    解析并校验克隆相关参数，返回（注册音色, 规范化后的 ref_audio 目录）。

    这里刻意「启动即失败」而不是等运行时报错：参考音频目录写错、JSON 里
    引用了不存在的参考音频，都属于部署期错误，越早暴露越省事。
    """
    ref_dir: Optional[str] = args.ref_audio_dir
    if ref_dir:
        if not os.path.isdir(ref_dir):
            raise SystemExit(f"[fatal] --ref-audio-dir 不是目录: {ref_dir}")
        ref_dir = os.path.realpath(ref_dir)

    voices = load_clone_voices(args.clone_voices) if args.clone_voices else []

    if not args.clone_model and not voices and not ref_dir and not args.no_remote_ref_audio:
        return [], ref_dir

    if not args.clone_model:
        if args.clone_voices:
            raise SystemExit(
                "[fatal] 配置了 --clone-voices，但缺少 --clone-model。\n"
                "        语音克隆需要 Base 模型，请加上 "
                "--clone-model <Qwen3-TTS-12Hz-1.7B-Base 目录>。"
            )
        log.warning(
            "--ref-audio-dir / --no-remote-ref-audio 已配置，但没有 --clone-model，"
            "本次不会启用语音克隆。"
        )
        return [], ref_dir

    for voice in voices:
        try:
            validate_clone_spec(voice.spec())
        except ValueError as exc:
            raise SystemExit(f"[fatal] 克隆音色 {voice.name!r} 配置有误: {exc}")
        if not voice.x_vector_only_mode and not (voice.ref_text or "").strip():
            raise SystemExit(
                f"[fatal] 克隆音色 {voice.name!r} 缺少 ref_text；"
                "ICL 模式必须提供参考音频里说的话，"
                "或显式设置 x_vector_only_mode=true（克隆质量会下降）。"
            )
        # 启动时就把参考音频解析一遍：越界的路径/不可达的 URL 在启动期暴露，
        # 好过等到第一次合成请求才报错。
        try:
            voice.ref_audio = validate_ref_audio(
                voice.ref_audio, ref_dir,
                allow_remote=not args.no_remote_ref_audio,
            )
        except ApiError as exc:
            raise SystemExit(f"[fatal] 克隆音色 {voice.name!r} 的 ref_audio 不可用: {exc.message}")

    return voices, ref_dir


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

    clone_voices, ref_dir = _prepare_clone_config(args)

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
        clone_model_path=args.clone_model,
        clone_tokenizer_path=args.clone_tokenizer_dir,
        clone_voices=clone_voices,
    )

    if args.clone_model:
        log.info("语音克隆已启用：clone-model=%s，已注册克隆音色 %d 个",
                 args.clone_model, len(clone_voices))
    if args.clone_model and not ref_dir and args.no_remote_ref_audio:
        log.warning("--no-remote-ref-audio 且未配置 --ref-audio-dir："
                    "请求将只能通过 base64 传入参考音频")

    cfg = ServerConfig(
        host=args.host,
        port=args.port,
        api_key=resolve_api_key(args.api_key),
        preload=not args.no_preload,
        max_concurrency=args.max_concurrency,
        cors_origins=[o.strip() for o in args.cors.split(",") if o.strip()],
        ref_audio_dir=ref_dir,
        allow_remote_ref_audio=not args.no_remote_ref_audio,
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
