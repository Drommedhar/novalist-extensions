"""Local Qwen3-TTS sidecar for Novalist.

The protocol is one UTF-8 JSON object per line over stdio. Audio is exchanged
through files in the private working directory, never through JSON and never
through a socket.

Qwen's documented character workflow has two checkpoints from one model family:

* VoiceDesign creates a short reference from an acoustic description.
* Base turns that audio and its exact transcript into a reusable ICL clone
  prompt, then reads ordinary prose in that voice.

The prose is sent without emotion tags. Qwen infers tone and prosody from the
text itself; the fixed reference prompt is responsible only for identity. The
design checkpoint is loaded only while designing and the clone checkpoint only
while reading, so both fit on machines that cannot hold both at once.
"""

from __future__ import annotations

import argparse
import contextlib
import gc
import hashlib
import io
import json
import os
import secrets
import sys
import time
import traceback
import wave
from dataclasses import dataclass, field
from typing import Any

PROTOCOL_VERSION = 3

DESIGN_MODEL = os.environ.get(
    "NOVALIST_TTS_DESIGN_MODEL", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign")
CLONE_MODEL = os.environ.get(
    "NOVALIST_TTS_CLONE_MODEL", "Qwen/Qwen3-TTS-12Hz-1.7B-Base")
TEMPERATURE = float(os.environ.get("NOVALIST_TTS_TEMPERATURE", "0.9"))
DESIGN_ATTEMPTS = 3

CURRENT_ID = ""


def _speak_utf8() -> None:
    """Use the same encoding as the .NET host on every operating system."""
    for stream in (sys.stdin, sys.stdout):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):  # pragma: no cover - old Python
            pass


_speak_utf8()


def emit(**payload: Any) -> None:
    """Write and flush one protocol reply."""
    payload.setdefault("id", CURRENT_ID)
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def note(message: str) -> None:
    """Diagnostics go to stderr; stdout belongs exclusively to the protocol."""
    sys.stderr.write(message + "\n")
    sys.stderr.flush()


def keep_failure(work: str, what: str, message: str) -> None:
    """Keep the full local error without putting manuscript text in a log."""
    try:
        path = os.path.join(os.path.dirname(os.path.abspath(work)), f"{what}-failed.txt")
        with io.open(path, "w", encoding="utf-8") as handle:
            handle.write(message)
    except OSError:
        pass


LANGUAGES = {
    "zh": "Chinese",
    "en": "English",
    "ja": "Japanese",
    "ko": "Korean",
    "de": "German",
    "fr": "French",
    "ru": "Russian",
    "pt": "Portuguese",
    "es": "Spanish",
    "it": "Italian",
}

DESIGN_LINES = {
    "Chinese": "这就是我自然说话时的声音。我会用舒适的节奏清楚地表达。现在，我将平静而自然地继续讲述这个故事。",
    "English": "This is my natural speaking voice. I speak clearly at a comfortable pace. Now I will continue the story calmly and without affectation.",
    "Japanese": "これが普段の私の声です。心地よい速さで、はっきりと話します。それでは、落ち着いて自然に物語を続けます。",
    "Korean": "이것이 평소의 제 목소리입니다. 편안한 속도로 또렷하게 말합니다. 이제 차분하고 자연스럽게 이야기를 이어가겠습니다.",
    "German": "So klingt meine natürliche Stimme. Ich spreche deutlich und in einem angenehmen Tempo. Nun werde ich die Geschichte ruhig und ungezwungen weitererzählen.",
    "French": "Voici ma voix naturelle. Je parle clairement, à un rythme confortable. Je vais maintenant poursuivre cette histoire avec calme et naturel.",
    "Russian": "Так звучит мой обычный голос. Я говорю ясно и в удобном темпе. Теперь я спокойно и естественно продолжу этот рассказ.",
    "Portuguese": "Esta é a minha voz natural. Falo com clareza e em um ritmo confortável. Agora continuarei a história com calma e naturalidade.",
    "Spanish": "Así suena mi voz natural. Hablo con claridad y a un ritmo cómodo. Ahora continuaré la historia con calma y naturalidad.",
    "Italian": "Questa è la mia voce naturale. Parlo chiaramente e a un ritmo confortevole. Ora continuerò la storia con calma e naturalezza.",
}


def base_language(tag: str | None) -> str:
    """Return the base part of a BCP-47 language tag."""
    return (tag or "en").replace("_", "-").split("-")[0].lower()


def qwen_language(tag: str | None) -> str | None:
    """Translate a BCP-47 tag to the language name Qwen validates."""
    return LANGUAGES.get(base_language(tag))


def design_text(tag: str | None) -> str:
    """Return a controlled, neutral three-sentence cloning transcript.

    Character dialogue is intentionally not used here. Its semantic emotion
    would become part of the ICL reference and pull every later line toward the
    mood of whichever excerpt happened to be selected during design.
    """
    language = qwen_language(tag) or "English"
    return DESIGN_LINES[language]


def voice_instruction(description: Any, language: str) -> str:
    """Turn the approved brief into a concise acoustic design instruction."""
    said = " ".join(str(description or "").split())
    if not said:
        said = (
            "Adult voice, balanced mid-range pitch, clear natural timbre, "
            "precise articulation, restrained cadence, neutral baseline"
        )
    return (
        f"Design a reusable voice with these stable acoustic traits: {said}. "
        f"Use native {language} pronunciation. Keep this reference natural and neutral; "
        "do not act a scene or add background sound."
    )


def seed_for(request: dict[str, Any]) -> int:
    """Use a pinned non-negative seed, or make a fresh design draw."""
    asked = request.get("seed")
    if isinstance(asked, bool) or not isinstance(asked, int) or asked < 0:
        return secrets.randbelow(2 ** 31)
    return asked % (2 ** 31)


def reading_rate(value: Any) -> float:
    """Clamp the host's reading rate to the range exposed by its UI."""
    try:
        rate = float(value)
    except (TypeError, ValueError):
        return 1.0
    return min(2.0, max(0.5, rate)) if rate > 0 else 1.0


def pick_device() -> str:
    """Use a GPU when available and retain a real, if slow, CPU path."""
    try:
        import torch
    except ImportError:
        return "cpu"
    if torch.cuda.is_available():
        return "cuda"
    mps = getattr(torch.backends, "mps", None)
    if mps is not None and mps.is_available():
        return "mps"
    return "cpu"


@dataclass
class Engine:
    device: str
    dtype: Any
    clone_model: Any = None
    design_model: Any = None
    sample_rate: int = 24000
    clone_prompts: dict[str, tuple[str, Any]] = field(default_factory=dict)


def new_engine() -> Engine:
    """Create the lightweight holder; checkpoints are loaded on demand."""
    emit(type="progress", step="importing")
    import torch

    device = pick_device()
    dtype = torch.bfloat16 if device == "cuda" else torch.float32
    return Engine(device=device, dtype=dtype)


def _model_kwargs(engine: Engine) -> dict[str, Any]:
    target = "cuda:0" if engine.device == "cuda" else engine.device
    kwargs: dict[str, Any] = {"device_map": target, "dtype": engine.dtype}
    kwargs["attn_implementation"] = "sdpa" if engine.device != "cpu" else "eager"
    return kwargs


def byte_count(value: int) -> str:
    """A compact binary-size label for the preparation dialog."""
    amount = float(max(0, value))
    for unit in ("B", "KiB", "MiB", "GiB"):
        if amount < 1024.0 or unit == "GiB":
            digits = 0 if unit in {"B", "KiB"} else 1
            return f"{amount:.{digits}f} {unit}"
        amount /= 1024.0
    return f"{amount:.1f} GiB"


class HubDownloadProgress:
    """The byte bar Hugging Face normally draws on stderr, sent to the host.

    Updates are throttled because a model download advances in small chunks and
    each protocol line ultimately becomes a UI update. Completion is always
    reported even when it falls inside the throttle window.
    """

    def __init__(self, label: str, total: int | None, initial: int = 0):
        self.label = label
        self.total = max(0, int(total or 0))
        self.current = max(0, int(initial))
        self.last_report = 0.0
        self.report(force=True)

    def update(self, amount: int = 1) -> None:
        self.current += max(0, int(amount))
        self.report(force=self.total > 0 and self.current >= self.total)

    def close(self) -> None:
        self.report(force=True)

    def report(self, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now - self.last_report < 0.2:
            return
        self.last_report = now
        fraction = min(1.0, self.current / self.total) if self.total > 0 else None
        amount = byte_count(self.current)
        detail = (
            f"{self.label} · {amount} / {byte_count(self.total)}"
            if self.total > 0 else f"{self.label} · {amount}"
        )
        emit(
            type="progress",
            step="downloading-model",
            detail=detail,
            fraction=fraction,
        )


def download_checkpoint(model_id: str, detail: str) -> str:
    """Download/resume one Hub repository while forwarding its byte progress."""
    emit(type="progress", step="downloading-model", detail=detail)
    from huggingface_hub import snapshot_download
    import huggingface_hub.file_download as file_download

    original = file_download._get_progress_bar_context

    def progress_context(
        *,
        desc: str,
        log_level: int,
        total: int | None = None,
        initial: int = 0,
        unit: str = "B",
        unit_scale: bool = True,
        name: str | None = None,
        _tqdm_bar: Any = None,
    ) -> Any:
        if _tqdm_bar is not None:
            return contextlib.nullcontext(_tqdm_bar)
        if unit == "B":
            return contextlib.closing(HubDownloadProgress(detail, total, initial))
        return original(
            desc=desc,
            log_level=log_level,
            total=total,
            initial=initial,
            unit=unit,
            unit_scale=unit_scale,
            name=name,
            _tqdm_bar=_tqdm_bar,
        )

    file_download._get_progress_bar_context = progress_context
    try:
        # One file at a time gives the dialog one honest byte bar. The model
        # repositories are dominated by their weight files, so concurrent small
        # metadata downloads do not materially improve the total transfer time.
        with contextlib.redirect_stdout(sys.stderr):
            return snapshot_download(repo_id=model_id, max_workers=1)
    finally:
        file_download._get_progress_bar_context = original


def _load_checkpoint(engine: Engine, model_id: str, detail: str) -> Any:
    snapshot = download_checkpoint(model_id, detail)
    emit(type="progress", step="loading-model", detail=detail)
    with contextlib.redirect_stdout(sys.stderr):
        from qwen_tts import Qwen3TTSModel
        return Qwen3TTSModel.from_pretrained(snapshot, **_model_kwargs(engine))


def _release(engine: Engine, which: str) -> None:
    if which == "clone":
        engine.clone_model = None
        engine.clone_prompts.clear()
    else:
        engine.design_model = None
    gc.collect()
    try:
        import torch
        if engine.device == "cuda":
            torch.cuda.empty_cache()
    except ImportError:
        pass


def ensure_clone(engine: Engine) -> Any:
    if engine.clone_model is not None:
        return engine.clone_model
    if engine.design_model is not None:
        _release(engine, "design")
    engine.clone_model = _load_checkpoint(engine, CLONE_MODEL, "voice cloning")
    return engine.clone_model


def ensure_design(engine: Engine) -> Any:
    if engine.design_model is not None:
        return engine.design_model
    if engine.clone_model is not None:
        _release(engine, "clone")
    engine.design_model = _load_checkpoint(engine, DESIGN_MODEL, "voice design")
    return engine.design_model


def seed_torch(seed: int) -> None:
    import torch
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def write_wav(path: str, audio: Any, sample_rate: int) -> float:
    """Write 16-bit mono PCM and return its duration in milliseconds."""
    import numpy as np

    samples = np.asarray(audio.detach().cpu().numpy() if hasattr(audio, "detach") else audio)
    samples = samples.squeeze()
    if samples.ndim > 1:
        samples = samples.mean(axis=0)
    samples = np.nan_to_num(samples)
    if samples.dtype.kind == "f":
        samples = np.clip(samples, -1.0, 1.0)
        samples = (samples * 32767).astype("<i2")
    else:
        samples = samples.astype("<i2")

    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(samples.tobytes())
    return len(samples) * 1000.0 / float(sample_rate)


def stretch(audio: Any, rate: Any) -> Any:
    """Change duration without changing pitch; 1.0 leaves samples untouched."""
    speed = reading_rate(rate)
    if abs(speed - 1.0) < 0.001:
        return audio
    import librosa
    import numpy as np
    return librosa.effects.time_stretch(np.asarray(audio, dtype=np.float32), rate=speed)


def do_design(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Create the approved reference clip and report its exact transcript."""
    voice_id = str(request.get("voiceId") or "voice")
    language = qwen_language(request.get("language"))
    if language is None:
        emit(type="error", key=voice_id, error="unsupported-language")
        return

    spoken = design_text(request.get("language"))
    instruction = voice_instruction(request.get("description"), language)
    model = ensure_design(engine)
    base = seed_for(request)

    wav = None
    used = base
    sample_rate = engine.sample_rate
    for attempt in range(DESIGN_ATTEMPTS):
        used = (base + attempt) % (2 ** 31)
        try:
            seed_torch(used)
            with contextlib.redirect_stdout(sys.stderr):
                wavs, sample_rate = model.generate_voice_design(
                    text=spoken,
                    language=language,
                    instruct=instruction,
                    temperature=TEMPERATURE,
                    subtalker_temperature=TEMPERATURE,
                )
            wav = wavs[0] if wavs else None
        except RuntimeError:
            note("design attempt %d could not be decoded" % (attempt + 1))
            wav = None
        if wav is not None:
            break

    if wav is None:
        emit(type="error", key=voice_id, error="designed-nothing")
        return

    engine.sample_rate = int(sample_rate)
    name = "design-%s.wav" % hashlib.sha256(voice_id.encode("utf-8")).hexdigest()[:12]
    duration = write_wav(os.path.join(work, name), wav, engine.sample_rate)
    emit(
        type="designed",
        key=voice_id,
        file=name,
        text=spoken,
        sampleRate=engine.sample_rate,
        durationMs=duration,
        seed=used,
    )


def _reference_fingerprint(path: str, transcript: str) -> str:
    digest = hashlib.sha256(transcript.encode("utf-8"))
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def clone_prompt(
    engine: Engine,
    model: Any,
    voice_id: str,
    reference_path: str,
    transcript: str,
) -> Any:
    """Build one ICL prompt per approved voice and reuse it across the run."""
    fingerprint = _reference_fingerprint(reference_path, transcript)
    cached = engine.clone_prompts.get(voice_id)
    if cached is not None and cached[0] == fingerprint:
        return cached[1]

    with contextlib.redirect_stdout(sys.stderr):
        prompt = model.create_voice_clone_prompt(
            ref_audio=reference_path,
            ref_text=transcript,
            x_vector_only_mode=False,
        )
    engine.clone_prompts[voice_id] = (fingerprint, prompt)
    return prompt


def do_render(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Read each supplied passage in a stable transcript-conditioned voice."""
    language = qwen_language(request.get("language"))
    if language is None:
        emit(type="error", error="unsupported-language")
        emit(type="done")
        return

    model = ensure_clone(engine)
    voices = request.get("voices") or {}
    voice_texts = request.get("voiceTexts") or {}
    rate = request.get("rate")

    for index, segment in enumerate(request.get("segments") or []):
        key = str(segment.get("key") or "")
        text = str(segment.get("text") or "").strip()
        if not text:
            continue

        voice_id = str(segment.get("voiceId") or "")
        reference = voices.get(voice_id)
        transcript = str(voice_texts.get(voice_id) or "").strip()
        if not reference:
            emit(type="error", key=key, error="unknown-voice")
            continue
        if not transcript:
            emit(type="error", key=key, error="missing-reference-text")
            continue

        try:
            reference_path = os.path.join(work, str(reference))
            prompt = clone_prompt(engine, model, voice_id, reference_path, transcript)
            with contextlib.redirect_stdout(sys.stderr):
                wavs, sample_rate = model.generate_voice_clone(
                    text=text,
                    language=language,
                    voice_clone_prompt=prompt,
                    non_streaming_mode=True,
                    temperature=TEMPERATURE,
                    subtalker_temperature=TEMPERATURE,
                )
            if not wavs:
                raise RuntimeError("generated no audio")
            wav = stretch(wavs[0], rate)
            engine.sample_rate = int(sample_rate)

            name = "clip-%s-%04d-%s.wav" % (
                CURRENT_ID or "x",
                index,
                hashlib.sha256(key.encode("utf-8")).hexdigest()[:10],
            )
            duration = write_wav(os.path.join(work, name), wav, engine.sample_rate)
            emit(
                type="clip",
                key=key,
                file=name,
                sampleRate=engine.sample_rate,
                durationMs=duration,
            )
        except Exception as failure:  # noqa: BLE001 - one bad passage need not end the run
            note(traceback.format_exc())
            emit(type="error", key=key, error=type(failure).__name__)

    emit(type="done")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    os.makedirs(args.work, exist_ok=True)

    engine: Engine | None = None
    for line in sys.stdin:
        line = line.lstrip("﻿").strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError:
            emit(type="error", error="bad-request")
            continue

        op = request.get("op")
        global CURRENT_ID
        CURRENT_ID = str(request.get("id") or "")
        try:
            if engine is None and op in {"status", "design", "render"}:
                engine = new_engine()

            if op == "status":
                # Preparation means both checkpoints are present, not just the
                # reader. Only one remains resident to keep peak VRAM bounded.
                ensure_design(engine)
                _release(engine, "design")
                ensure_clone(engine)
                emit(
                    type="ready",
                    version=PROTOCOL_VERSION,
                    ready=True,
                    detail=f"{CLONE_MODEL} + {DESIGN_MODEL} on {engine.device}",
                )
            elif op == "design":
                do_design(engine, args.work, request)
            elif op == "render":
                do_render(engine, args.work, request)
            else:
                emit(type="error", error="unknown-op")
        except Exception as failure:  # noqa: BLE001 - report and keep listening
            trace = traceback.format_exc()
            note(trace)
            keep_failure(args.work, op or "request", trace)
            engine = None
            emit(type="error", error=type(failure).__name__)

    return 0


if __name__ == "__main__":
    sys.exit(main())
