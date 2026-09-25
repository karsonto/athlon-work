#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Qwen3-TTS 本地模型 —— 真·流式语音合成
========================================

加载本地 Qwen3-TTS 权重（无需任何云 API），逐帧吐出音频：
每生成 80ms 音频帧就立刻解码并 yield，而不是等整句合成完。

原理（全部来自官方源码，已逐行核实）
------------------------------------
1) Qwen3-TTS-Tokenizer-12Hz 每帧 = 1920 采样 / 24kHz = **80ms** 音频。
2) talker 是逐帧自回归：每步产出 16 个码本（num_code_groups=16），
   同时按 `trailing_text_hidden[generation_step]` 逐字注入文本 embedding。
3) decoder 是纯因果结构（sliding_window=72，conv 因果），
   所以携带少量左上下文即可增量解码，切块与整段解码结果一致
   —— 官方 `chunked_decode(chunk_size=300, left_context_size=25)` 就是这个道理。
4) 官方 Python API 把整段结果一次性 decode 返回，所以「不流式」；
   但模型本身完全支持流式。本脚本在 wrapper 层补上这一环。

实现要点
--------
* **零重实现**：monkeypatch `Qwen3TTSTalkerForConditionalGeneration.generate`，
  截获官方 `generate_custom_voice()` 算好的全部输入张量（含说话人 embedding、
  方言处理、instruct、左 padding、trailing text padding）后立即中止，
  再用自己的循环驱动 `talker.forward` 逐步生成。官方提示词逻辑 100% 复用。
* **双线程流水线**：生成线程 → 帧队列 → 解码线程 → PCM 队列 → 主线程 yield。
  vocoder 不会阻塞 talker，TTFA（首包延迟）≈ 首帧生成耗时。
* **可打断**：`cancel()` 立即停止生成并清空队列（barge-in）。

依赖
----
    pip install -U qwen-tts soundfile
    pip install sounddevice      # 仅 --play 需要

用法
----
# 基本：合成并写 WAV
python tools/streaming_tts_local.py \
    --model ./Qwen3-TTS-12Hz-0.6B-CustomVoice \
    --text "其实我真的有发现，我是一个特别善于观察别人情绪的人。" \
    --speaker Vivian --out out.wav

# 实时播放（TTFA 会打印出来）
python tools/streaming_tts_local.py --model ./Qwen3-TTS-12Hz-1.7B-CustomVoice \
    --text "你好，这是一段流式语音。" --speaker Serena --play

# 指令控制语气
python tools/streaming_tts_local.py --model ./Qwen3-TTS-12Hz-1.7B-CustomVoice \
    --text "今天天气不错" --instruct "用特别开心的语气说" --play

# 给下游管道裸 PCM（24kHz mono float32）
python tools/streaming_tts_local.py --model ./m --text "测试" --device-out > a.f32

# 调整快慢权衡：--decode-every 3 表示每攒 3 帧解码一次（默认 1，最低延迟）
python tools/streaming_tts_local.py --model ./m --text "测试" --play --decode-every 2
"""

from __future__ import annotations

import argparse
import queue
import sys
import threading
import time
import wave
from dataclasses import dataclass
from typing import Any, Dict, Iterator, List, Optional

# --------------------------------------------------------------------------- #
# 常量
# --------------------------------------------------------------------------- #

# Qwen3-TTS-Tokenizer-12Hz: decode_upsample_rate = 1920, out_sr = 24000
FRAME_UPSAMPLE = 1920
SAMPLE_RATE = 24000

# 官方 hard defaults（见 qwen3_tts_model.py::_merge_generate_kwargs）
HARD_DEFAULTS: Dict[str, Any] = {
    "do_sample": True,
    "top_k": 50,
    "top_p": 1.0,
    "temperature": 0.9,
    "repetition_penalty": 1.05,
    "subtalker_dosample": True,
    "subtalker_top_k": 50,
    "subtalker_top_p": 1.0,
    "subtalker_temperature": 0.9,
    "max_new_tokens": 2048,
}


class _PromptCaptured(Exception):
    """内部信号：提示词已截获，中止官方 blocking generate。"""


# --------------------------------------------------------------------------- #
# 采样：复刻 HF GenerationMixin 在官方参数下的行为
# --------------------------------------------------------------------------- #

def _sample_token(
    logits: Any,
    history: List[int],
    do_sample: bool,
    top_k: int,
    top_p: float,
    temperature: float,
    repetition_penalty: float,
    suppress_tokens: Optional[List[int]],
    torch: Any,
) -> Any:
    """从 (1, V) logits 采样一个 token。"""
    scores = logits.float()

    if suppress_tokens:
        idx = torch.tensor(suppress_tokens, device=scores.device, dtype=torch.long)
        scores[:, idx] = -float("inf")

    if repetition_penalty and repetition_penalty != 1.0 and history:
        hist = torch.tensor(history, device=scores.device, dtype=torch.long).unique()
        picked = scores[:, hist]
        picked = torch.where(
            picked > 0, picked / repetition_penalty, picked * repetition_penalty
        )
        scores[:, hist] = picked

    if not do_sample:
        return torch.argmax(scores, dim=-1, keepdim=True)

    if temperature and temperature != 1.0:
        scores = scores / max(temperature, 1e-5)

    if top_k and top_k > 0:
        k = min(top_k, scores.shape[-1])
        kth = torch.topk(scores, k, dim=-1).values[:, -1:]
        scores = torch.where(
            scores < kth, torch.full_like(scores, -float("inf")), scores
        )

    if top_p and 0.0 < top_p < 1.0:
        sorted_scores, sorted_idx = torch.sort(scores, descending=True, dim=-1)
        probs = torch.softmax(sorted_scores, dim=-1)
        cumulative = torch.cumsum(probs, dim=-1)
        cutoff = (cumulative - probs) >= top_p
        sorted_scores = sorted_scores.masked_fill(cutoff, -float("inf"))
        scores = torch.full_like(scores, -float("inf")).scatter(
            -1, sorted_idx, sorted_scores
        )

    probs = torch.softmax(scores, dim=-1)
    return torch.multinomial(probs, num_samples=1)


# --------------------------------------------------------------------------- #
# 增量解码器：把逐帧codes实时解码成PCM
# --------------------------------------------------------------------------- #

class IncrementalVocoder:
    """
    12Hz decoder 的增量封装。

    每次 push 若干帧，只解码「已提交帧 - left_context」到末尾，
    并按 1920 采样/帧 精确切出新增音频，保证与整段解码逐样本一致。
    """

    def __init__(
        self,
        decoder: Any,
        torch: Any,
        left_context_frames: int = 25,
        frame_upsample: int = FRAME_UPSAMPLE,
        device: Any = None,
    ) -> None:
        self.decoder = decoder
        self.torch = torch
        self.left = int(left_context_frames)
        self.up = int(frame_upsample)
        self.device = device or next(decoder.parameters()).device

        self._codes: Optional[Any] = None  # (T, Q) on device
        self._base = 0                      # _codes[0] 的绝对帧号
        self._emitted = 0                   # 已输出帧数
        self.total_frames = 0

    @property
    def emitted_frames(self) -> int:
        return self._emitted

    def push(self, frames: Any) -> Optional[Any]:
        """frames: (n, Q) long tensor on device。返回新增 float32 numpy，或 None。"""
        torch = self.torch
        frames = frames.to(self.device).long()
        self._codes = (
            frames if self._codes is None else torch.cat([self._codes, frames], dim=0)
        )
        self.total_frames = self._base + self._codes.shape[0]

        new_frames = self.total_frames - self._emitted
        if new_frames <= 0:
            return None

        start_abs = max(0, self._emitted - self.left)
        start_local = start_abs - self._base
        chunk = self._codes[start_local:].transpose(0, 1).unsqueeze(0)

        with torch.inference_mode():
            wav = self.decoder(chunk)  # (1, 1, L)

        offset = (self._emitted - start_abs) * self.up
        audio = wav[0, 0, offset : offset + new_frames * self.up]
        self._emitted = self.total_frames

        # 裁剪缓冲，保持 compute 有界（只留 left 帧上下文）
        keep_from = self.total_frames - self.left
        if keep_from > self._base:
            self._codes = self._codes[keep_from - self._base :]
            self._base = keep_from

        return audio.float().cpu().numpy()


# --------------------------------------------------------------------------- #
# 核心：本地流式 TTS
# --------------------------------------------------------------------------- #

@dataclass
class StreamMetrics:
    """首包延迟等指标。"""

    t_start: float = 0.0
    ttfa_ms: Optional[float] = None           # 首帧音频到达延迟
    t_first_frame_ms: Optional[float] = None  # 首帧码本生成耗时
    frames: int = 0
    audio_seconds: float = 0.0
    wall_seconds: float = 0.0
    error: Optional[str] = None               # 生成线程异常信息

    @property
    def rtf(self) -> float:
        return self.wall_seconds / self.audio_seconds if self.audio_seconds else 0.0


class LocalStreamingTTS:
    """
    本地 Qwen3-TTS CustomVoice 流式合成。

    加载一次模型，可反复调用 `stream()`。
    """

    def __init__(
        self,
        model_path: str,
        device: str = "cuda:0",
        dtype: str = "bfloat16",
        attn_implementation: str = "flash_attention_2",
        tokenizer_path: Optional[str] = None,
        left_context_frames: int = 25,
        decode_every: int = 1,
        debug: bool = False,
    ) -> None:
        self.model_path = model_path
        self.device = device
        self.dtype_str = dtype
        self.attn_implementation = attn_implementation
        self.tokenizer_path = tokenizer_path
        self.left_context_frames = int(left_context_frames)
        self.decode_every = max(1, int(decode_every))
        self.debug = debug

        self.tts = None
        self.torch = None
        self._talker = None
        self._gen_defaults: Dict[str, Any] = {}
        self._talker_cls: Any = None
        self.last_metrics: Optional[StreamMetrics] = None

    # --------------------------- 加载 --------------------------- #

    def load(self) -> "LocalStreamingTTS":
        import torch  # 延迟导入，便于 --help 快速返回
        from qwen_tts import Qwen3TTSModel

        self.torch = torch
        # cuDNN 卷积路径在该环境故障（CUDNN_STATUS_NOT_INITIALIZED），
        # 禁用后 conv1d/conv2d 自动回退 native kernel，vocoder 完全兼容。
        torch.backends.cudnn.enabled = False
        dtype = getattr(torch, self.dtype_str)
        kwargs: Dict[str, Any] = {"device_map": self.device, "dtype": dtype}
        if self.attn_implementation and self.attn_implementation != "none":
            kwargs["attn_implementation"] = self.attn_implementation

        self._log(f"loading model: {self.model_path}")
        self.tts = Qwen3TTSModel.from_pretrained(self.model_path, **kwargs)
        return self.attach(self.tts)

    def attach(self, tts: Any) -> "LocalStreamingTTS":
        """
        绑定一个已加载的 Qwen3TTSModel（或结构兼容对象），完成流式初始化。

        与 load() 分离，便于复用外部已加载的模型实例，也便于测试注入。
        """
        if self.torch is None:
            import torch

            self.torch = torch

        self.tts = tts
        model = tts.model
        model_type = getattr(model, "tts_model_type", None)
        if model_type != "custom_voice":
            raise ValueError(
                f"此脚本面向 CustomVoice 模型，当前模型 tts_model_type={model_type!r}。"
                "请使用 Qwen3-TTS-12Hz-1.7B/0.6B-CustomVoice。"
            )

        self._talker = model.talker
        self._talker_cls = type(self._talker)

        # 若用户显式指定 tokenizer 目录，则在此挂载。
        # 官方 Qwen3TTSForConditionalGeneration.from_pretrained 只认
        # `<model_path>/speech_tokenizer/`，不认自定义路径；服务器部署常把
        # 模型与 tokenizer 分开挂载，故这里补上该能力（不指定则沿用官方行为）。
        if getattr(model, "speech_tokenizer", None) is None:
            if self.tokenizer_path:
                self._log(f"挂载外部 speech_tokenizer: {self.tokenizer_path}")
                try:
                    from qwen_tts import Qwen3TTSTokenizer

                    tokenizer = Qwen3TTSTokenizer.from_pretrained(
                        self.tokenizer_path
                    )
                    loader = getattr(model, "load_speech_tokenizer", None)
                    if callable(loader):
                        loader(tokenizer)
                    else:
                        model.speech_tokenizer = tokenizer
                except Exception as exc:  # noqa: BLE001
                    raise RuntimeError(
                        f"无法从 --tokenizer-dir={self.tokenizer_path!r} 加载 "
                        f"speech_tokenizer: {exc}"
                    ) from exc

        if getattr(model, "speech_tokenizer", None) is None:
            raise RuntimeError(
                "模型未挂载 speech_tokenizer（增量解码必需）。\n"
                "请确认模型目录下存在 `speech_tokenizer/` 子目录，"
                "或用 --tokenizer-dir 手动指定。\n"
                "下载命令示例:\n"
                "  huggingface-cli download Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice \\\n"
                "      --local-dir ./Qwen3-TTS-12Hz-0.6B-CustomVoice"
            )

        # 兼容性自检：本策略依赖 talker.forward 返回 past_hidden / generation_step /
        # hidden_states=(_, codec_ids) 三件套，且接受 trailing_text_hidden 入参。
        import inspect

        sig = inspect.signature(self._talker.forward)
        for name in ("past_hidden", "generation_step", "trailing_text_hidden",
                     "tts_pad_embed"):
            if name not in sig.parameters:
                raise RuntimeError(
                    f"当前 qwen-tts 版本的 talker.forward 缺少 `{name}` 参数，"
                    "流式 patch 不兼容。请升级 qwen-tts，或改用官方批量接口。"
                )
        cfg = model.config.talker_config
        for name in ("num_code_groups", "codec_eos_token_id", "vocab_size"):
            if not hasattr(cfg, name):
                raise RuntimeError(
                    f"talker_config 缺少 `{name}`，流式 patch 不兼容。"
                )

        # 合并 generation_config.json 的默认参数
        self._gen_defaults = dict(HARD_DEFAULTS)
        defaults = getattr(self.tts, "generate_defaults", None) or {}
        self._gen_defaults.update(
            {k: v for k, v in defaults.items() if k in HARD_DEFAULTS}
        )

        self._num_code_groups = int(cfg.num_code_groups)
        self._eos_id = int(cfg.codec_eos_token_id)
        self._vocab_size = int(cfg.vocab_size)
        self._suppress = [
            i for i in range(self._vocab_size - 1024, self._vocab_size)
            if i != self._eos_id
        ]
        self._decoder = model.speech_tokenizer.model.decoder
        self._pad_id = int(cfg.codec_pad_id)
        self._log(
            "ready: code_groups=%d eos=%d frame=%.0fms dtype=%s device=%s"
            % (
                self._num_code_groups,
                self._eos_id,
                FRAME_UPSAMPLE / SAMPLE_RATE * 1000,
                getattr(self.tts, "dtype", self.dtype_str),
                self.device,
            )
        )
        return self

    # --------------------------- 提示词截获 --------------------------- #

    def _capture_prompt(
        self,
        text: str,
        speaker: str,
        language: str,
        instruct: Optional[str],
        max_new_tokens: int,
    ) -> Dict[str, Any]:
        """
        Monkeypatch talker.generate 截获官方构建好的张量，然后中止。

        复用官方 generate_custom_voice 的全部提示词逻辑（说话人 embedding、
        方言、instruct、左 padding、trailing text padding），规避重实现风险。
        """
        captured: Dict[str, Any] = {}
        cls = self._talker_cls
        original = cls.generate

        def _spy(self_talker: Any, **kwargs: Any) -> Any:  # noqa: ANN401
            captured.update(kwargs)
            raise _PromptCaptured()

        cls.generate = _spy
        try:
            self.tts.generate_custom_voice(
                text=text,
                language=language,
                speaker=speaker,
                instruct=instruct,
                non_streaming_mode=False,  # 关键：让模型在只拿到部分文本时即可起跑
                max_new_tokens=max_new_tokens,
            )
        except _PromptCaptured:
            pass
        finally:
            cls.generate = original

        if "inputs_embeds" not in captured:
            raise RuntimeError(
                "未能截获提示词张量，可能与当前 qwen-tts 版本不兼容。"
                "请升级 qwen-tts 后重试。"
            )
        if captured["inputs_embeds"].shape[0] != 1:
            raise ValueError("流式模式仅支持单条文本（batch_size=1）。")
        return captured

    # --------------------------- 流式生成 --------------------------- #

    def _generate_frames(
        self,
        prompt: Dict[str, Any],
        gen: Dict[str, Any],
        frame_q: "queue.Queue[Optional[Any]]",
        metrics: StreamMetrics,
        stop: threading.Event,
    ) -> None:
        """生成线程：逐帧产出 (1, num_code_groups) 码本，推入 frame_q。"""
        torch = self.torch
        talker = self._talker

        embeds = prompt["inputs_embeds"]
        mask = prompt["attention_mask"]
        trailing = prompt["trailing_text_hidden"]
        pad_embed = prompt["tts_pad_embed"]

        suppress = gen.get("suppress_tokens") or self._suppress
        min_new = 2
        max_new = int(gen["max_new_tokens"])

        history: List[int] = []
        sub_kwargs = dict(
            subtalker_dosample=gen["subtalker_dosample"],
            subtalker_top_k=gen["subtalker_top_k"],
            subtalker_top_p=gen["subtalker_top_p"],
            subtalker_temperature=gen["subtalker_temperature"],
        )

        try:
            with torch.no_grad():
                # ---- prefill：一次前向，建立 KV cache 与 rope_deltas ----
                prefill_len = embeds.shape[1]
                out = talker(
                    inputs_embeds=embeds,
                    attention_mask=mask,
                    cache_position=torch.arange(prefill_len, device=embeds.device),
                    use_cache=True,
                    trailing_text_hidden=trailing,
                    tts_pad_embed=pad_embed,
                    **sub_kwargs,
                )
                past = out.past_key_values
                past_hidden = out.past_hidden
                cur_mask = mask
                produced = 0
                n_sampled = 0

                # ---- 逐帧自回归 ----
                # 采样出的 token 即「下一帧的首码本」；再前向一次才拿到该帧全部 16 码本。
                while produced < max_new:
                    if stop.is_set():
                        break

                    next_id = _sample_token(
                        logits=out.logits[:, -1, :],
                        history=history,
                        do_sample=gen["do_sample"],
                        top_k=gen["top_k"],
                        top_p=gen["top_p"],
                        temperature=gen["temperature"],
                        repetition_penalty=gen["repetition_penalty"],
                        suppress_tokens=suppress,
                        torch=torch,
                    )  # (1, 1)
                    token = int(next_id.item())
                    history.append(token)
                    n_sampled += 1

                    if token == self._eos_id and n_sampled > min_new:
                        break

                    cur_mask = torch.cat(
                        [cur_mask, cur_mask.new_ones((1, 1))], dim=1
                    )
                    out = talker(
                        input_ids=next_id,
                        attention_mask=cur_mask,
                        past_key_values=past,
                        past_hidden=past_hidden,
                        trailing_text_hidden=trailing,
                        tts_pad_embed=pad_embed,
                        generation_step=out.generation_step,
                        use_cache=True,
                        cache_position=torch.tensor(
                            [prefill_len + produced], device=embeds.device
                        ),
                        **sub_kwargs,
                    )
                    past = out.past_key_values
                    past_hidden = out.past_hidden

                    frame = out.hidden_states[1]  # (1, num_code_groups)
                    produced += 1
                    metrics.frames = produced
                    if metrics.t_first_frame_ms is None:
                        metrics.t_first_frame_ms = (
                            time.time() - metrics.t_start
                        ) * 1000.0
                    frame_q.put(frame.detach())
        except Exception as exc:  # noqa: BLE001
            self._log(f"[generate error] {exc!r}")
            metrics.error = repr(exc)
        finally:
            frame_q.put(None)

    def _decode_frames(
        self,
        frame_q: "queue.Queue[Optional[Any]]",
        pcm_q: "queue.Queue[Optional[Any]]",
        metrics: StreamMetrics,
        stop: threading.Event,
    ) -> None:
        """解码线程：从 frame_q 取帧，增量解码后推入 pcm_q。"""
        vocoder = IncrementalVocoder(
            decoder=self._decoder,
            torch=self.torch,
            left_context_frames=self.left_context_frames,
            frame_upsample=FRAME_UPSAMPLE,
        )
        pending: List[Any] = []

        try:
            while True:
                frame = frame_q.get()
                if frame is None:
                    if pending:
                        _emit(vocoder, pending, pcm_q, metrics)
                    break
                if stop.is_set():
                    break
                # 保留 (1, num_code_groups) 形状，便于沿 dim=0 堆叠成 (n, Q)
                pending.append(frame)
                if len(pending) >= self.decode_every:
                    _emit(vocoder, pending, pcm_q, metrics)
        finally:
            pcm_q.put(None)

    # --------------------------- 公开 API --------------------------- #

    def stream(
        self,
        text: str,
        speaker: str = "Vivian",
        language: str = "Chinese",
        instruct: Optional[str] = None,
        **gen_kwargs: Any,
    ) -> Iterator[Any]:
        """
        流式合成，逐块 yield float32 numpy（24kHz 单声道）。

        示例::
            tts = LocalStreamingTTS("./Qwen3-TTS-12Hz-0.6B-CustomVoice").load()
            for pcm in tts.stream("你好"):
                player.write(pcm)
        """
        if self.tts is None:
            raise RuntimeError("请先调用 load()。")

        gen = dict(self._gen_defaults)
        gen.update({k: v for k, v in gen_kwargs.items() if v is not None})
        # 官方 generate() 在内部构造 suppress_tokens（屏蔽 vocab 末尾 1024 个控制位），
        # 这里显式补上，保证采样行为与官方一致。
        gen.setdefault("suppress_tokens", self._suppress)

        metrics = StreamMetrics(t_start=time.time())
        stop = threading.Event()
        frame_q: "queue.Queue[Optional[Any]]" = queue.Queue()
        pcm_q: "queue.Queue[Optional[Any]]" = queue.Queue()

        prompt = self._capture_prompt(text, speaker, language, instruct,
                                      int(gen["max_new_tokens"]))

        gen_thread = threading.Thread(
            target=self._generate_frames,
            args=(prompt, gen, frame_q, metrics, stop),
            name="tts-generate",
            daemon=True,
        )
        dec_thread = threading.Thread(
            target=self._decode_frames,
            args=(frame_q, pcm_q, metrics, stop),
            name="tts-decode",
            daemon=True,
        )
        gen_thread.start()
        dec_thread.start()

        audio = 0
        try:
            while True:
                chunk = pcm_q.get()
                if chunk is None:
                    break
                if chunk.size == 0:
                    continue
                if metrics.ttfa_ms is None:
                    metrics.ttfa_ms = (time.time() - metrics.t_start) * 1000.0
                audio += chunk.size
                yield chunk
        finally:
            stop.set()
            gen_thread.join(timeout=5.0)
            dec_thread.join(timeout=5.0)
            metrics.audio_seconds = audio / SAMPLE_RATE
            metrics.wall_seconds = time.time() - metrics.t_start
            self.last_metrics = metrics

        if metrics.error is not None:
            raise RuntimeError(f"流式生成失败: {metrics.error}")

    # --------------------------- 内部工具 --------------------------- #

    def _log(self, msg: str) -> None:
        if self.debug:
            sys.stderr.write(f"[local-tts] {msg}\n")
            sys.stderr.flush()


def _emit(
    vocoder: IncrementalVocoder,
    pending: List[Any],
    pcm_q: "queue.Queue[Optional[Any]]",
    metrics: StreamMetrics,
) -> None:
    """把 pending 里的帧合并成一批送入增量解码器。"""
    import torch

    frames = torch.cat(pending, dim=0)
    pending.clear()
    pcm = vocoder.push(frames)
    if pcm is not None and pcm.size:
        pcm_q.put(pcm)


# --------------------------------------------------------------------------- #
# 输出工具
# --------------------------------------------------------------------------- #

class FloatPcmToWav:
    """边收边写 WAV（float32 -> int16）。"""

    def __init__(self, path: str, sample_rate: int = SAMPLE_RATE) -> None:
        self.path = path
        self.sample_rate = sample_rate
        self._fh = None
        self._wav: Optional[wave.Wave_write] = None

    def __enter__(self) -> "FloatPcmToWav":
        self._fh = open(self.path, "wb")
        self._wav = wave.open(self._fh, "wb")
        self._wav.setnchannels(1)
        self._wav.setsampwidth(2)
        self._wav.setframerate(self.sample_rate)
        return self

    def write(self, audio: Any) -> None:
        import numpy as np

        assert self._wav is not None
        pcm = np.clip(audio, -1.0, 1.0)
        self._wav.writeframes((pcm * 32767.0).astype("<i2").tobytes())

    def __exit__(self, *exc: Any) -> None:
        if self._wav is not None:
            self._wav.close()
        if self._fh is not None:
            self._fh.close()


class Speaker:
    """sounddevice 流式播放。"""

    def __init__(self, sample_rate: int = SAMPLE_RATE) -> None:
        try:
            import sounddevice as sd
        except ImportError as exc:  # pragma: no cover
            raise RuntimeError("实时播放需:  pip install sounddevice") from exc

        self._stream = sd.OutputStream(
            samplerate=sample_rate, channels=1, dtype="float32"
        )
        self._stream.start()

    def write(self, audio: Any) -> None:
        self._stream.write(audio)

    def close(self) -> None:
        try:
            self._stream.stop()
            self._stream.close()
        except Exception:  # noqa: BLE001
            pass


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Qwen3-TTS 本地模型流式语音合成",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    m = p.add_argument_group("模型")
    m.add_argument("--model", required=True,
                   help="本地模型目录或 HuggingFace repo id")
    m.add_argument("--device", default="cuda:0")
    m.add_argument("--dtype", default="bfloat16",
                   choices=["bfloat16", "float16", "float32"])
    m.add_argument("--attn", default="flash_attention_2",
                   help="flash_attention_2 / sdpa / eager / none")
    m.add_argument("--tokenizer-dir", default=None,
                   help="speech_tokenizer 目录（模型内未包含时）")

    t = p.add_argument_group("文本")
    t.add_argument("--text")
    t.add_argument("--file", help="按行读取")
    t.add_argument("--stdin", action="store_true", help="从 stdin 按行读取")

    s = p.add_argument_group("合成")
    s.add_argument("--speaker", default="Vivian",
                   help="Vivian/Serena/Uncle_Fu/Dylan/Eric/Ryan/Aiden/Ono_Anna/Sohee")
    s.add_argument("--language", default="Chinese")
    s.add_argument("--instruct", default=None, help="指令控制，如“用愤怒的语气说”")
    s.add_argument("--temperature", type=float, default=None)
    s.add_argument("--top-k", type=int, default=None)
    s.add_argument("--top-p", type=float, default=None)
    s.add_argument("--repetition-penalty", type=float, default=None)
    s.add_argument("--max-new-tokens", type=int, default=None,
                   help="最多生成多少帧（每帧80ms）")

    st = p.add_argument_group("流式调优")
    st.add_argument("--decode-every", type=int, default=1,
                    help="每攒 N 帧解码一次；1=最低延迟，>1 省算力")
    st.add_argument("--left-context", type=int, default=25,
                    help="增量解码左上下文帧数（官方 chunked_decode 用 25）")

    o = p.add_argument_group("输出")
    o.add_argument("--out", help=".wav 输出文件")
    o.add_argument("--play", action="store_true", help="实时播放")
    o.add_argument("--device-out", action="store_true",
                   help="把 float32 PCM 写入 stdout")
    o.add_argument("--debug", action="store_true")
    return p


def iter_texts(args: argparse.Namespace) -> Iterator[str]:
    if args.stdin:
        for line in sys.stdin:
            line = line.strip()
            if line:
                yield line
    elif args.file:
        with open(args.file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line:
                    yield line
    elif args.text:
        yield args.text
    else:
        raise SystemExit("请提供 --text / --file / --stdin")


def main(argv: Optional[List[str]] = None) -> int:
    args = build_parser().parse_args(argv)

    tts = LocalStreamingTTS(
        model_path=args.model,
        device=args.device,
        dtype=args.dtype,
        attn_implementation=args.attn,
        tokenizer_path=args.tokenizer_dir,
        left_context_frames=args.left_context,
        decode_every=args.decode_every,
        debug=args.debug,
    ).load()

    gen_kwargs = {
        "temperature": args.temperature,
        "top_k": args.top_k,
        "top_p": args.top_p,
        "repetition_penalty": args.repetition_penalty,
        "max_new_tokens": args.max_new_tokens,
    }

    sink = None
    if args.out:
        if not args.out.lower().endswith(".wav"):
            raise SystemExit("--out 目前仅支持 .wav")
        sink = FloatPcmToWav(args.out)
        sink.__enter__()

    player = Speaker() if args.play else None

    try:
        for idx, text in enumerate(iter_texts(args)):
            t0 = time.time()
            total = 0
            first = None
            for chunk in tts.stream(
                text=text,
                speaker=args.speaker,
                language=args.language,
                instruct=args.instruct,
                **gen_kwargs,
            ):
                if first is None:
                    first = (time.time() - t0) * 1000.0
                total += chunk.size
                if sink is not None:
                    sink.write(chunk)
                if player is not None:
                    player.write(chunk)
                if args.device_out:
                    sys.stdout.buffer.write(chunk.astype("<f4").tobytes())
                    sys.stdout.buffer.flush()

            dur = total / SAMPLE_RATE
            wall = time.time() - t0
            m = tts.last_metrics
            first_frame = (
                f"{m.t_first_frame_ms:.1f}ms" if m and m.t_first_frame_ms else "n/a"
            )
            sys.stderr.write(
                f"[{idx}] 首帧码本 {first_frame} | TTFA {first if first else float('nan'):.1f}ms | "
                f"音频 {dur:.2f}s | 耗时 {wall:.2f}s | RTF {wall / max(dur, 1e-6):.2f}\n"
            )
    except KeyboardInterrupt:
        sys.stderr.write("\n[interrupted]\n")
        return 130
    finally:
        if player is not None:
            player.close()
        if sink is not None:
            sink.__exit__(None, None, None)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
