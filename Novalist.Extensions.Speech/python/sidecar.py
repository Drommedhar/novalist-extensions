"""Novalist speech sidecar.

One JSON object per line on stdin, one per line on stdout. Audio never travels
in a message: clips are written into the working directory and named back, and
the extension reads the file. Anything the models print goes to stderr, which the
host routes to the debugger only - a model that echoes its prompt must not be
able to write a paragraph of somebody's novel into a log they might send us.

The engine is Chatterbox (Resemble AI), which does two things this needs:

  clone     it speaks in the voice of a reference clip. That is what makes a
            designed voice a *thing* rather than a prompt: the clip is the
            identity, stored by the host, reused for every line that character
            ever speaks.

  exaggerate  it takes an emotional intensity per utterance, separately from the
            voice. That is the other half - one identity, performed differently
            in every scene.

**What "design" means here, honestly.** Chatterbox clones; it is not a
text-to-voice designer. A character's voice is made by speaking a line in the
model's own built-in voice with generation settings seeded from their brief, and
keeping the result as their reference clip. Every character therefore gets a
*distinct and stable* voice, which is what the two-stage design needs - but the
words of the brief steer it only weakly. A true designer can be dropped in
behind the same protocol when one is pip-installable; nothing else here changes.

Both models are local. This file opens no socket; the only network traffic is
the one-off weight download huggingface_hub does on first load, which the writer
started deliberately by pressing Prepare.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import traceback
import wave
from dataclasses import dataclass
from typing import Any

PROTOCOL_VERSION = 1


def _speak_utf8() -> None:
    """Make the protocol UTF-8 at both ends, whatever the machine's locale is.

    Python takes its standard streams from the system locale, which on a German
    or Chinese Windows install is a code page - cp1252, cp936 - and not UTF-8.
    The host writes UTF-8. Left alone, every umlaut and every Chinese character
    in the manuscript arrives here as mojibake or as a decode error that kills
    the read loop, and a reply carrying one cannot be written back at all.

    A novel is exactly the payload this breaks, so it is set explicitly rather
    than left to the environment.
    """
    for stream in (sys.stdin, sys.stdout):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):  # pragma: no cover - very old Python
            pass


_speak_utf8()

# Overridable, because a writer with a better checkpoint should not wait for us
# to publish a release.
MODEL = os.environ.get("NOVALIST_TTS_MODEL", "chatterbox")

# The dimensions the host sends emotion in. It builds its vector in exactly
# these, so reading it is a lookup rather than a guess at either end.
EMOTION_DIMENSIONS = [
    "happy",
    "angry",
    "sad",
    "afraid",
    "disgusted",
    "melancholic",
    "surprised",
    "calm",
]

# How much of the emotion reaches the delivery. Chatterbox takes one number, and
# its useful range is roughly 0.3 (flat) to 1.0 (theatrical); its own default is
# 0.5. A reading pinned at the top of that is exhausting, which is the thing an
# audiobook cannot afford.
EXAGGERATION_FLOOR = 0.35
EXAGGERATION_CEILING = 0.9

# The dimensions that mean "louder" rather than "quieter". Calm and melancholy
# pull a reading down; fury and surprise push it up.
HEIGHTENING = {"angry", "afraid", "surprised", "happy", "disgusted"}


def emit(**payload: Any) -> None:
    """One reply line. Flushed, because the host is waiting on it."""
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def note(message: str) -> None:
    """Diagnostics, to stderr - never stdout, which is the protocol."""
    sys.stderr.write(message + "\n")
    sys.stderr.flush()


@dataclass
class Engine:
    model: Any
    device: str
    sample_rate: int


def pick_device() -> str:
    """The best device this machine actually has.

    CPU is a real answer, not a failure: it is slow, the host says so before the
    writer waits, and a writer without a graphics card is not locked out of
    hearing their book.
    """
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


def load() -> Engine:
    """Loads the model.

    Tens of seconds on a later run, and several minutes on the first, when the
    weights are still being fetched. No fractions are reported: none of these
    steps knows how far through it is, and a bar frozen at seventy per cent
    reads as broken where a moving one reads as working.
    """
    emit(type="progress", step="importing")
    from chatterbox.tts import ChatterboxTTS

    device = pick_device()
    # The first call fetches about a gigabyte of weights; after that it is a
    # read from disk. Said separately because the two feel nothing alike.
    emit(type="progress", step="loading-delivery", detail=device)
    model = ChatterboxTTS.from_pretrained(device=device)
    emit(type="progress", step="loading-design")
    return Engine(model=model, device=device, sample_rate=int(model.sr))


def write_wav(path: str, audio: Any, sample_rate: int) -> float:
    """Writes 16-bit mono and returns the duration in milliseconds."""
    import numpy as np

    samples = np.asarray(audio.detach().cpu().numpy() if hasattr(audio, "detach") else audio)
    samples = samples.squeeze()
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


def seed_from(text: str) -> int:
    """A stable number from a brief.

    Stable so that designing the same character twice from the same words gives
    the same voice, and different so that two characters never share one. Hashed
    rather than left to chance: "arbitrary but repeatable" is the whole
    requirement.
    """
    digest = hashlib.sha256(text.encode("utf-8")).digest()
    return int.from_bytes(digest[:4], "big")


def design_settings(brief: str) -> dict[str, float]:
    """Generation settings for one character, derived from their brief.

    Temperature and guidance move the speaker; the numbers are spread across a
    range that stays intelligible rather than the model's whole span, because a
    voice nobody can follow is not a voice.
    """
    seed = seed_from(brief)
    return {
        "temperature": 0.6 + ((seed >> 3) % 60) / 100.0,
        "cfg_weight": 0.3 + ((seed >> 11) % 45) / 100.0,
        "exaggeration": 0.4 + ((seed >> 19) % 25) / 100.0,
    }


def do_design(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Stage one: make this character's reference clip and hand it back."""
    import torch

    voice_id = str(request.get("voiceId") or "voice")
    brief = str(request.get("description") or "")
    samples = [line for line in (request.get("sampleLines") or []) if str(line).strip()]

    # Their own words where there are any: a voice made from a line the
    # character actually speaks sits closer to how they sound on the page.
    spoken = str(samples[0]) if samples else (
        "This is how I sound when nothing in particular is wrong."
    )
    spoken = spoken.strip()[:200]

    settings = design_settings(brief + voice_id)
    torch.manual_seed(seed_from(brief + voice_id))

    wav = engine.model.generate(
        spoken,
        temperature=settings["temperature"],
        cfg_weight=settings["cfg_weight"],
        exaggeration=settings["exaggeration"],
    )

    name = "design-%s.wav" % hashlib.sha256(voice_id.encode("utf-8")).hexdigest()[:12]
    duration = write_wav(os.path.join(work, name), wav, engine.sample_rate)
    emit(
        type="designed",
        key=voice_id,
        file=name,
        sampleRate=engine.sample_rate,
        durationMs=duration,
    )


def exaggeration_for(segment: dict[str, Any]) -> float:
    """The host's emotion vector as the one number this engine takes.

    The heightening dimensions push the reading up and the settling ones pull it
    down, so grief and fury are not delivered identically just because both are
    strong. Held inside a range that stays listenable for a whole chapter.
    """
    vector = segment.get("vector") or {}
    if not vector:
        return 0.5

    up = sum(float(vector.get(name, 0.0)) for name in EMOTION_DIMENSIONS if name in HEIGHTENING)
    down = sum(
        float(vector.get(name, 0.0)) for name in EMOTION_DIMENSIONS if name not in HEIGHTENING
    )

    value = 0.5 + (up * 0.45) - (down * 0.2)
    return max(EXAGGERATION_FLOOR, min(EXAGGERATION_CEILING, value))


def do_render(engine: Engine, work: str, request: dict[str, Any]) -> None:
    voices = request.get("voices") or {}

    for index, segment in enumerate(request.get("segments") or []):
        key = str(segment.get("key") or "")
        text = str(segment.get("text") or "").strip()
        if not text:
            continue

        reference = voices.get(str(segment.get("voiceId") or ""))
        if not reference:
            emit(type="error", key=key, error="unknown voice")
            continue

        try:
            name = "clip-%04d-%s.wav" % (
                index,
                hashlib.sha256(key.encode("utf-8")).hexdigest()[:10],
            )
            target = os.path.join(work, name)
            wav = engine.model.generate(
                text,
                audio_prompt_path=os.path.join(work, reference),
                exaggeration=exaggeration_for(segment),
            )
            duration = write_wav(target, wav, engine.sample_rate)
            emit(
                type="clip",
                key=key,
                file=name,
                sampleRate=engine.sample_rate,
                durationMs=duration,
            )
        except Exception as failure:  # noqa: BLE001 - one bad line must not end the book
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
        # A byte-order mark, if the host's encoder insists on writing one. It is
        # not whitespace and json.loads will not have it, so it has to come off
        # explicitly rather than by stripping.
        line = line.lstrip("﻿").strip()
        if not line:
            continue

        try:
            request = json.loads(line)
        except json.JSONDecodeError:
            # Said, not swallowed. Dropping an unreadable line in silence is how
            # three stray bytes on the front of the first request turned into a
            # dialog that read "Starting" for ever: both sides waited for the
            # other, and nothing anywhere said why.
            emit(type="error", error="bad-request")
            continue

        op = request.get("op")
        try:
            if engine is None and op in {"status", "design", "render"}:
                engine = load()

            if op == "status":
                emit(
                    type="ready",
                    version=PROTOCOL_VERSION,
                    ready=True,
                    detail="%s on %s" % (MODEL, engine.device),
                )
            elif op == "design":
                do_design(engine, args.work, request)
            elif op == "render":
                do_render(engine, args.work, request)
            else:
                emit(type="error", error="unknown op")
        except Exception as failure:  # noqa: BLE001 - report and keep listening
            note(traceback.format_exc())
            # The type only. The message can quote a path, and the host writes
            # what it is told into a log the writer may send us.
            emit(type="error", error=type(failure).__name__)

    return 0


if __name__ == "__main__":
    sys.exit(main())
