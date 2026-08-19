"""Novalist speech sidecar.

One JSON object per line on stdin, one per line on stdout. Audio never travels
in a message: clips are written into the working directory and named back, and
the extension reads the file. Anything the models print goes to stderr, which the
host routes to the debugger only - a model that echoes its prompt must not be
able to write a paragraph of somebody's novel into a log they might send us.

Two models, because the feature is two things.

  design    MOSS-VoiceGenerator (OpenMOSS, Apache-2.0) makes a speaker's timbre
            out of free-form text and nothing else - no reference recording
            anywhere. "Eine Frau mit ruhiger Stimme, mittleren Alters" comes
            back as a voice. That is what lets a character's voice come from
            what the writer already wrote about them, and it is also what keeps
            any real person's likeness out of this entirely.

            It runs once per character. The clip it returns *is* the voice from
            then on: the host stores it, and it is never generated again -
            design is not deterministic, and a character who sounded different
            every session would not be a character.

  deliver   Chatterbox (Resemble AI) speaks each line in that stored voice,
            taking an emotional intensity per utterance separately from the
            timbre. One identity, performed differently in every scene.

            The multilingual checkpoint, so the book is read in its own
            language. The split helps here too: the designed clip fixes who is
            speaking, and delivery decides what language they speak.

Both are local. This file opens no socket; the only network traffic is the
one-off weight download huggingface_hub does on first load, which the writer
started deliberately by pressing Prepare.
"""

from __future__ import annotations

import argparse
import hashlib
import io
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


# The request being served, stamped onto everything said about it.
#
# A reading the writer stopped goes on being spoken - the model cannot be
# interrupted mid-utterance - so its replies must be recognisable as
# belonging to a request nobody is listening to any more. Without that they
# arrive in the middle of the next request and are read as answers to it.
CURRENT_ID = ""


def emit(**payload: Any) -> None:
    """One reply line. Flushed, because the host is waiting on it."""
    payload.setdefault("id", CURRENT_ID)
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def note(message: str) -> None:
    """Diagnostics, to stderr - never stdout, which is the protocol."""
    sys.stderr.write(message + "\n")
    sys.stderr.flush()


def keep_failure(work: str, what: str, message: str) -> None:
    """Writes a failure down where somebody can read it afterwards.

    The host routes this process's stderr to a debugger and nowhere else, on
    purpose - a model can echo the words it was given, and a diagnostic log a
    writer might send us must never carry a paragraph of their book. But that
    left a failure with nothing behind it but the name of an exception type,
    which is not enough to fix anything.

    So it goes to a file beside the environment, on the writer's own machine,
    never read by the application and never sent anywhere - the same place and
    the same reasoning as install-failed.txt.
    """
    try:
        path = os.path.join(os.path.dirname(os.path.abspath(work)), f"{what}-failed.txt")
        with io.open(path, "w", encoding="utf-8") as handle:
            handle.write(message)
    except OSError:
        pass


# The voice designer: free-form text in, a speaker's timbre out, with no
# reference recording anywhere near it. This is what makes a character's voice
# come from what the writer wrote about them rather than from a real person.
DESIGN_MODEL = os.environ.get("NOVALIST_TTS_DESIGN_MODEL", "OpenMOSS-Team/MOSS-VoiceGenerator")

# How many draws a design gets before giving up. The first is the stable one
# the brief asks for; the rest are only reached when that draw produced audio
# the model's own decoder could not read.
DESIGN_ATTEMPTS = 4


@dataclass
class Engine:
    model: Any
    device: str
    sample_rate: int
    # Loaded the first time a voice is designed, not at startup: a reading needs
    # only the delivery model, and nobody should wait for - or hold the memory
    # of - a designer they are not using.
    designer: Any = None
    design_processor: Any = None
    design_rate: int = 24000


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


def language_id(tag: str | None) -> str:
    """The model's language id for a BCP-47 tag the host sent.

    "de-DE" and "de" are both German; "zh-CN" is Chinese. A language the
    model does not have falls back to English rather than failing the
    reading - a book read in the wrong accent is worse than one not read at
    all only until you consider that the alternative here is silence.
    """
    from chatterbox import SUPPORTED_LANGUAGES

    base = (tag or "en").replace("_", "-").split("-")[0].lower()
    return base if base in SUPPORTED_LANGUAGES else "en"


def load() -> Engine:
    """Loads the model.

    Tens of seconds on a later run, and several minutes on the first, when the
    weights are still being fetched. No fractions are reported: none of these
    steps knows how far through it is, and a bar frozen at seventy per cent
    reads as broken where a moving one reads as working.
    """
    emit(type="progress", step="importing")
    from chatterbox import ChatterboxMultilingualTTS

    device = pick_device()
    # The first call fetches the weights; after that it is a read from disk.
    # Said separately because the two feel nothing alike.
    #
    # The multilingual checkpoint rather than the English one. A German
    # novel read in an English accent is not a reading of that novel, and
    # the host has always sent the book's language - it was simply thrown
    # away here.
    emit(type="progress", step="loading-delivery", detail=device)
    model = ChatterboxMultilingualTTS.from_pretrained(device=device)
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


def native_hint(tag: str | None) -> str:
    """Tells the designer which language this voice speaks natively.

    The designer takes accent as part of its instruction - its own examples say
    things like "in standard American English" - and it has no language
    argument at all. Left unsaid, a German line came back read by an English
    speaker, and because the delivery model clones accent along with timbre,
    that accent was then baked into every line of the book.

    The names come from the delivery model's own table, so the two always agree
    about which languages exist.
    """
    from chatterbox import SUPPORTED_LANGUAGES

    code = language_id(tag)
    name = SUPPORTED_LANGUAGES.get(code)
    if not name or code == "en":
        return ""
    return f" The speaker is a native {name} speaker with no foreign accent."


def load_designer(engine: Engine) -> None:
    """Brings up the voice designer, once.

    Kept apart from the delivery model on purpose. Designing happens a handful
    of times per book - once per character - and reading happens tens of
    thousands, so the designer is fetched when it is first wanted and the
    reading never pays for it.
    """
    if engine.designer is not None:
        return

    import torch
    from huggingface_hub import snapshot_download
    from transformers import AutoModel, AutoProcessor

    emit(type="progress", step="loading-design", detail=engine.device)

    # A local directory rather than the repository name. The model ships its own
    # loading code, and that code does Path(name_or_path) - which on Windows
    # rewrites the slash in "OpenMOSS-Team/MOSS-VoiceGenerator" as a backslash
    # and then fails its own repository-name check. A real path has no slash to
    # rewrite.
    local = snapshot_download(repo_id=DESIGN_MODEL)

    if engine.device == "cuda":
        # The cuDNN attention kernel is broken for this model; the others are
        # the fallbacks its own authors ask for.
        torch.backends.cuda.enable_cudnn_sdp(False)
        torch.backends.cuda.enable_flash_sdp(True)
        torch.backends.cuda.enable_mem_efficient_sdp(True)
        torch.backends.cuda.enable_math_sdp(True)

    processor = AutoProcessor.from_pretrained(
        local, trust_remote_code=True, normalize_inputs=True
    )
    processor.audio_tokenizer = processor.audio_tokenizer.to(engine.device)

    model = AutoModel.from_pretrained(
        local,
        trust_remote_code=True,
        attn_implementation="sdpa" if engine.device == "cuda" else "eager",
        dtype=torch.bfloat16 if engine.device == "cuda" else torch.float32,
    ).to(engine.device)
    model.eval()

    engine.designer = model
    engine.design_processor = processor
    engine.design_rate = int(processor.model_config.sampling_rate)


def do_design(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Stage one: make this character's voice out of what was written about them.

    The brief goes in as the instruction and comes back as a timbre. Nothing is
    cloned and no recording of anybody is involved, which is what lets a voice
    be designed from a Codex entry at all.

    The clip this returns *is* the voice from here on: the host stores it, and
    every line that character ever speaks is delivered in it.
    """
    import torch

    load_designer(engine)

    voice_id = str(request.get("voiceId") or "voice")
    brief = str(request.get("description") or "").strip()
    samples = [line for line in (request.get("sampleLines") or []) if str(line).strip()]

    # Their own words where there are any: a voice made from a line the
    # character actually speaks sits closer to how they sound on the page.
    spoken = str(samples[0]) if samples else (
        "This is how I sound when nothing in particular is wrong."
    )
    spoken = spoken.strip()[:200]

    instruction = (brief or "A clear, neutral speaking voice at an even tempo.")
    instruction += native_hint(request.get("language"))

    processor = engine.design_processor
    conversation = [[processor.build_user_message(text=spoken, instruction=instruction)]]
    batch = processor(conversation, mode="generation")

    # Seeded from the brief, so asking for the same voice twice gives the same
    # voice. Design is otherwise non-deterministic, and a character who sounded
    # different every session would not be a character.
    #
    # But a seed is a draw, and some draws produce audio the model's own decoder
    # cannot parse - "split_sizes to sum exactly to 121, but got [56]", a
    # generation that never closed. Because the seed comes from the brief, an
    # unlucky one is not bad luck once: that description is broken for ever, and
    # pressing the button again reproduces it exactly. So the first seed is the
    # stable one and the next few are fallbacks, tried only when the draw was
    # bad. A voice that designs first time still designs the same every time.
    base = seed_from(brief + voice_id)
    wav = None
    for attempt in range(DESIGN_ATTEMPTS):
        torch.manual_seed(base + attempt)
        try:
            with torch.no_grad():
                outputs = engine.designer.generate(
                    input_ids=batch["input_ids"].to(engine.device),
                    attention_mask=batch["attention_mask"].to(engine.device),
                )
            for message in processor.decode(outputs):
                wav = message.audio_codes_list[0]
                break
        except RuntimeError:
            # The decoder refusing what the model produced. Another draw is
            # worth more than an error the writer can do nothing about.
            note("design attempt %d could not be decoded" % (attempt + 1))
            wav = None
        if wav is not None:
            break

    if wav is None:
        emit(type="error", key=voice_id, error="designed nothing")
        return

    name = "design-%s.wav" % hashlib.sha256(voice_id.encode("utf-8")).hexdigest()[:12]
    duration = write_wav(os.path.join(work, name), wav, engine.design_rate)
    emit(
        type="designed",
        key=voice_id,
        file=name,
        sampleRate=engine.design_rate,
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
    lang = language_id(request.get("language"))

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
            # The request id is in the name, so an abandoned render can
            # neither overwrite a live one's clip nor have its own deleted
            # out from under it.
            name = "clip-%s-%04d-%s.wav" % (
                CURRENT_ID or "x",
                index,
                hashlib.sha256(key.encode("utf-8")).hexdigest()[:10],
            )
            target = os.path.join(work, name)
            wav = engine.model.generate(
                text,
                language_id=lang,
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
        global CURRENT_ID
        CURRENT_ID = str(request.get("id") or "")
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
            trace = traceback.format_exc()
            note(trace)
            keep_failure(args.work, op or "request", trace)
            # A model that failed once is not to be trusted to have survived it
            # - a CUDA fault in particular poisons everything after it - so it
            # is dropped and built again next time rather than reused.
            if engine is not None and op == "design":
                engine.designer = None
                engine.design_processor = None
            # The type only. The message can quote a path, and the host writes
            # what it is told into a log the writer may send us.
            emit(type="error", error=type(failure).__name__)

    return 0


if __name__ == "__main__":
    sys.exit(main())
