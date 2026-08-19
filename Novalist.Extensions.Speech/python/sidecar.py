"""Novalist speech sidecar.

One JSON object per line on stdin, one per line on stdout. Audio never travels
in a message: clips are written into the working directory and named back, and
the extension reads the file. Anything the models print goes to stderr, which the
host routes to the debugger only - a model that echoes its prompt must not be
able to write a paragraph of somebody's novel into a log they might send us.

One model, because the feature is one thing done twice.

  VoxCPM2 (OpenBMB, Apache-2.0) designs a speaker out of free-form text with no
  reference recording anywhere - "eine ruhige Frau mittleren Alters" comes back
  as a voice - and then clones that stored clip for every line the character
  ever speaks.

  It designs and it delivers. That is the whole reason it is here rather than a
  better designer beside a better deliverer: our pipeline designs a clip once
  and clones it for ever, and when the designer and the cloner are two models
  the timbre that was designed is not the timbre that comes back. Nobody
  benchmarks that seam because nobody else's pipeline has it. One model has no
  seam.

  It also speaks German, and thirty other languages, natively - which is what
  lets the stored clip be in the book's own language. The accent a reading
  carries is the accent of the clip it was cloned from, not of the language tag
  it was sent, so a clip designed in the book's language is the only reliable
  way to stop a German novel being read by an American.

Local. This file opens no socket; the only network traffic is the one-off weight
download huggingface_hub does on first load, which the writer started
deliberately.
"""

from __future__ import annotations

import argparse
import hashlib
import secrets
import io
import json
import os
import sys
import traceback
import wave
from dataclasses import dataclass
from typing import Any

PROTOCOL_VERSION = 2


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
MODEL = os.environ.get("NOVALIST_TTS_MODEL", "openbmb/VoxCPM2")

# torch.compile needs Triton, which has no Windows build, and the compile step
# fails there with an integer conversion error rather than degrading. Off by
# default on Windows and on for everybody else, overridable either way.
OPTIMIZE = os.environ.get(
    "NOVALIST_TTS_OPTIMIZE", "0" if os.name == "nt" else "1") not in {"0", "false", "False"}

# How firmly the model is held to the instruction. The model's own default.
CFG_VALUE = float(os.environ.get("NOVALIST_TTS_CFG", "2.0"))

# Diffusion steps per utterance. More is slower and slightly cleaner; this is
# the model's own default and the point at which its published numbers were
# measured.
TIMESTEPS = int(os.environ.get("NOVALIST_TTS_TIMESTEPS", "10"))

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

# The request being served, stamped onto everything said about it.
#
# A reading the writer stopped goes on being spoken - the model cannot be
# interrupted mid-utterance - so its replies must be recognisable as belonging
# to a request nobody is listening to any more. Without that they arrive in the
# middle of the next request and are read as answers to it.
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


# ── The style prefix ────────────────────────────────────────────────────────
#
# VoxCPM2 takes its direction as a parenthesised phrase in front of the words:
# "(clipped and angry)Get out." That collides with the rule the whole design
# rests on - a direction is a parameter and never enters the text - because an
# in-band tag is one bad prompt away from the model reading the word "angry" out
# loud in the middle of a sentence.
#
# The rule does not bend; the adapter absorbs it, which is exactly what the SDK
# says an adapter is for. Two invariants make it safe:
#
#   1. Every phrase below is a fixed string in this file. Nothing the writer
#      typed, and nothing derived from the manuscript, can reach the prefix. The
#      host's own natural-language instruction is deliberately not used - the
#      engine does not advertise EmotionInstruction, so it is never sent one.
#
#   2. A prefix is ALWAYS emitted, even for a neutral line. That is what stops
#      the prose occupying the prefix slot: a sentence that legitimately opens
#      with a bracket - "(He had never said so aloud.)" - is no longer the first
#      thing the model sees, so it cannot be mistaken for direction.
#
# The vocabulary is deliberately small and plain. The model reads it as English
# regardless of the book's language, which is what its own examples do.

# What each dimension sounds like, quietly and then strongly. Two rungs rather
# than a scale: the difference between "sad" and "grieving" is audible, the
# difference between eleven gradations of sad is not, and every extra rung is
# another string to go wrong.
STYLE_WORDS = {
    "happy": ("warm", "bright and delighted"),
    "angry": ("terse", "sharply angry"),
    "sad": ("subdued", "sorrowful"),
    "afraid": ("wary", "frightened"),
    "disgusted": ("cold", "repelled"),
    "melancholic": ("wistful", "heavy with melancholy"),
    "surprised": ("caught off guard", "startled"),
    "calm": ("calm", "very calm and even"),
}

# What the reading sounds like when the vector says nothing at all.
STYLE_NEUTRAL = "natural and even"

# Pace, from the host's rate. The model has no rate argument - this is how a
# reading speed reaches it at all, and it is why the Speed control does
# something on this engine rather than being accepted and discarded.
PACE_WORDS = [
    (0.7, "much slower"),
    (0.9, "slightly slower"),
    (1.12, None),
    (1.4, "slightly faster"),
    (99.0, "much faster"),
]

# Narration is coloured, never acted. The prose around a line is not a
# performance of it, and a narrator who emotes through a description of weather
# is the other half of the problem the per-line direction exists to solve.
NARRATION_WORDS = "measured narration"

# Above this a dimension is a performance rather than a tint.
STRONG = 0.55

# Below this the vector is saying nothing worth saying.
QUIET = 0.18


def pace_word(rate: Any) -> str | None:
    """The reading speed as one of five fixed phrases, or none for normal."""
    try:
        value = float(rate)
    except (TypeError, ValueError):
        return None
    if value <= 0:
        return None
    for ceiling, word in PACE_WORDS:
        if value < ceiling:
            return word
    return None


def style_prefix(segment: dict[str, Any], rate: Any) -> str:
    """The direction for one segment, as a phrase from the closed vocabulary.

    Built entirely from numbers and booleans the host sent. No string the writer
    typed is consulted, which is the whole point - see the block comment above.
    """
    vector = segment.get("vector") or {}
    parts: list[str] = []

    strongest = ""
    weight = 0.0
    for name in EMOTION_DIMENSIONS:
        try:
            value = float(vector.get(name, 0.0))
        except (TypeError, ValueError):
            value = 0.0
        if value > weight:
            strongest = name
            weight = value

    if strongest and weight >= QUIET:
        quiet, strong = STYLE_WORDS[strongest]
        parts.append(strong if weight >= STRONG else quiet)
    else:
        parts.append(STYLE_NEUTRAL)

    if not segment.get("isDialogue"):
        parts.append(NARRATION_WORDS)

    if (pace := pace_word(rate)) is not None:
        parts.append(pace)

    return ", ".join(parts)


# The bracket characters the model reads as an instruction group.
OPENERS = "(（"
CLOSERS = ")）"


def unbracket(text: str) -> str:
    """Takes the brackets off an aside that opens the line.

    The model reads a parenthesised group at the front of the text as direction
    and consumes it. Putting our own prefix in front is what stops the prose
    being read AS direction - but it does not stop the next group being eaten,
    because two groups in a row is the model's own syntax for a designed voice.

    Measured, not assumed. "(Sie drehte sich nicht um.) Der Wind nahm es mit."
    behind a prefix came back as 1.44 s where the whole line is 2.40 s and the
    tail alone is 1.12 s: the aside was gone. A space between the groups did not
    help. That is a clause of somebody's novel disappearing out of the reading
    with nothing anywhere saying so, which is the worst way this can fail.

    So the brackets come off and the words stay. Nothing is lost: a bracket has
    no sound of its own, and the clause inside it is read as the writer wrote
    it. Only a leading one is touched - a bracket in the middle of a sentence is
    never in the instruction slot and is left exactly alone.
    """
    said = text.lstrip()
    while said[:1] in OPENERS:
        depth = 0
        for i, ch in enumerate(said):
            if ch in OPENERS:
                depth += 1
            elif ch in CLOSERS:
                depth -= 1
                if depth == 0:
                    said = (said[1:i] + said[i + 1:]).lstrip()
                    break
        else:
            # An opening bracket the writer never closed. Dropping the one mark
            # is enough to get the words out of the instruction slot.
            said = said[1:].lstrip()
            break
    return said


def directed(text: str, prefix: str) -> str:
    """The words with their direction in front, and nothing of the words in it.

    The direction is built from numbers and never from anything the writer
    typed, and a prefix is always emitted - so the prose is never the first
    thing the model reads and can never be mistaken for an instruction.
    """
    return "(" + prefix + ")" + unbracket(text)


# ── The design brief ────────────────────────────────────────────────────────

# What a designed voice says when the character has no lines yet.
#
# In the book's own language, because the clip this produces IS the voice from
# then on and every later line is cloned from it - accent included. An English
# sentence here is an English accent on every page of a German novel, which is
# precisely the bug this table exists to end. The narrator, who speaks most of a
# book and has no dialogue of their own to sample, hit it on every single line.
DESIGN_LINES = {
    "en": "This is how I sound when nothing in particular is wrong.",
    "de": "So höre ich mich an, wenn nichts Besonderes vorgefallen ist.",
    "fr": "Voici comment je parle lorsque rien de particulier ne se passe.",
    "es": "Así sueno cuando no ocurre nada en particular.",
    "it": "Ecco come suono quando non succede niente di particolare.",
    "pt": "É assim que eu soo quando nada de especial acontece.",
    "nl": "Zo klink ik wanneer er niets bijzonders aan de hand is.",
    "da": "Sådan lyder jeg, når der ikke er noget særligt i vejen.",
    "sv": "Så här låter jag när ingenting särskilt har hänt.",
    "no": "Slik høres jeg ut når ingenting spesielt er galt.",
    "fi": "Tältä kuulostan, kun mikään ei ole erityisesti vialla.",
    "pl": "Tak brzmię, kiedy nic szczególnego się nie dzieje.",
    "ru": "Вот как я звучу, когда ничего особенного не случилось.",
    "tr": "Özel bir sorun yokken sesim böyle çıkar.",
    "el": "Έτσι ακούγομαι όταν δεν συμβαίνει τίποτα ιδιαίτερο.",
    "zh": "没有什么特别的事情时，我就是这个声音。",
    "ja": "特に何もないときの私の声はこんな感じです。",
    "ko": "별일 없을 때 제 목소리는 이렇습니다.",
    "hi": "जब कुछ ख़ास नहीं होता, तब मैं ऐसे सुनाई देता हूँ।",
    "ar": "هكذا يبدو صوتي حين لا يحدث شيء خاص.",
    "id": "Beginilah suara saya ketika tidak ada yang istimewa.",
    "vi": "Đây là giọng của tôi khi không có gì đặc biệt xảy ra.",
    "th": "นี่คือเสียงของฉันเมื่อไม่มีอะไรผิดปกติ.",
    "he": "כך אני נשמע כששום דבר מיוחד לא קורה.",
    "ms": "Beginilah bunyi suara saya apabila tiada apa-apa yang istimewa.",
    "uk": "Ось як я звучу, коли нічого особливого не сталося.",
    "cs": "Takhle zním, když se neděje nic zvláštního.",
    "sk": "Takto zniem, keď sa nedeje nič zvláštne.",
}

# How long a design sample may run. A speech says less about a voice than three
# short sentences do, and the clip is conditioning rather than content.
DESIGN_SAMPLE_LIMIT = 220

# How many draws a design gets. The first is the stable one the brief asks for;
# the rest are only reached when that draw produced nothing usable.
DESIGN_ATTEMPTS = 3


def base_language(tag: str | None) -> str:
    """The bare language of a BCP-47 tag: "de-DE" and "de-low" are both German."""
    return (tag or "en").replace("_", "-").split("-")[0].lower()


def design_line(tag: str | None, samples: list[str]) -> str:
    """What the designed voice should say, in the book's own language.

    The character's own words where there are any - a voice made from a line the
    character actually speaks sits closer to how they sound on the page, and it
    is already in the right language by construction. Otherwise a fixed sentence
    in that language, which is the case the narrator always falls into.
    """
    for line in samples:
        said = str(line).strip()
        if said:
            return said[:DESIGN_SAMPLE_LIMIT]

    code = base_language(tag)
    return DESIGN_LINES.get(code) or DESIGN_LINES["en"]


def brief_prefix(description: str) -> str:
    """A brief as a parenthesised instruction the model will not misread.

    Brackets come out. The instruction is one parenthesised group and a bracket
    inside it closes the group early, which would put the rest of the writer's
    own description into the text slot and have it read aloud. This is the only
    place writer-supplied words reach a prefix at all, and it is why they are
    cleaned before they get there.
    """
    said = " ".join(str(description or "").split())
    said = said.replace("(", " ").replace(")", " ").replace("（", " ").replace("）", " ")
    said = " ".join(said.split())
    return said or "A clear, neutral speaking voice at an even tempo."


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

    One model for both stages. The old arrangement loaded a designer beside a
    deliverer and paid for both; this one is loaded once and does everything,
    which is also why the first voice design no longer sits behind four
    gigabytes nobody was warned about.
    """
    emit(type="progress", step="importing")
    from voxcpm import VoxCPM

    device = pick_device()
    emit(type="progress", step="loading-model", detail=device)
    model = VoxCPM.from_pretrained(
        MODEL,
        # The denoiser is for cleaning up a recording somebody made on a phone.
        # Every reference clip here was generated by this same model at 48 kHz,
        # so there is nothing to clean and it would only cost load time and
        # memory. It also runs on the CPU whatever device is chosen.
        load_denoiser=False,
        device=device,
        optimize=OPTIMIZE,
    )
    rate = int(getattr(model, "sample_rate", 0) or 48000)
    return Engine(model=model, device=device, sample_rate=rate)


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

    Kept for a writer who asks for one particular voice back, and no longer the
    default. Deriving every draw from the words meant pressing Design again on
    an unchanged brief returned the identical voice - so "I did not like that
    one, try again", which is the one thing the design dialog is built around,
    did nothing at all and said nothing about it.
    """
    digest = hashlib.sha256(text.encode("utf-8")).digest()
    return int.from_bytes(digest[:4], "big")


def seed_for(request: dict[str, Any]) -> int:
    """The seed this design should draw with.

    A number the writer pinned, or a fresh one. Fresh is the default because
    design is not reproducible and asking twice is the only way to get a second
    answer; pinned is how somebody keeps a voice they liked, or gets it back
    after changing their mind.
    """
    asked = request.get("seed")
    if isinstance(asked, bool) or not isinstance(asked, int):
        return secrets.randbelow(2 ** 31)
    # Negative is the interface's way of saying "surprise me", and a seed
    # outside the generator's range would be a crash rather than a voice.
    return asked % (2 ** 31) if asked >= 0 else secrets.randbelow(2 ** 31)


def do_design(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Stage one: make this character's voice out of what was written about them.

    The brief goes in as the instruction and comes back as a timbre. Nothing is
    cloned and no recording of anybody is involved, which is what lets a voice
    be designed from a Codex entry at all.

    The clip this returns *is* the voice from here on: the host stores it, and
    every line that character ever speaks is delivered in it - accent included,
    which is why the words it speaks are in the book's language and not in ours.
    """
    voice_id = str(request.get("voiceId") or "voice")
    language = request.get("language")
    samples = [line for line in (request.get("sampleLines") or []) if str(line).strip()]

    spoken = design_line(language, samples)
    prompt = directed(spoken, brief_prefix(request.get("description")))

    # Seeded through torch rather than through an argument: generate() takes no
    # seed of its own, so the only handle on the draw is the global generator it
    # samples from. Set immediately before the call, because anything else that
    # touches torch in between moves it on.
    import torch

    base = seed_for(request)
    wav = None
    for attempt in range(DESIGN_ATTEMPTS):
        try:
            torch.manual_seed(base + attempt)
            wav = engine.model.generate(
                text=prompt,
                cfg_value=CFG_VALUE,
                inference_timesteps=TIMESTEPS,
                normalize=True,
                retry_badcase=True,
            )
        except RuntimeError:
            # A draw the model's own decoder could not finish. Because the seed
            # comes from the brief, an unlucky one is not bad luck once - that
            # description would be broken for ever and pressing the button again
            # would reproduce it exactly. So the first seed is the stable one
            # and the next few are fallbacks, tried only when the draw was bad.
            note("design attempt %d could not be decoded" % (attempt + 1))
            wav = None
        if wav is not None:
            break

    if wav is None:
        emit(type="error", key=voice_id, error="designed-nothing")
        return

    name = "design-%s.wav" % hashlib.sha256(voice_id.encode("utf-8")).hexdigest()[:12]
    duration = write_wav(os.path.join(work, name), wav, engine.sample_rate)
    emit(
        type="designed",
        key=voice_id,
        file=name,
        sampleRate=engine.sample_rate,
        durationMs=duration,
        # The number this voice was actually drawn with, including the attempt
        # it took - so a writer who likes what they hear can ask for it again,
        # which is not possible if only the engine ever knew it.
        seed=base + attempt,
    )


def reference_for(work: str, design: str, segment: dict[str, Any]) -> str:
    """Which clip this line is cloned from.

    The character's design clip, unless the writer pointed at a line and said
    "like that" - and then that one.

    The model clones its whole delivery from its reference, the timbre and the
    prosody together, so a clip already performed the way they wanted directs
    this line more exactly than any word for it. The host only ever offers clips
    in the same character's voice, so the identity does not move when the
    delivery does.

    A clip that is not on disk falls back to the design clip rather than
    failing. The writer's point was about the delivery; losing the resemblance
    is a disappointment and losing the line is a hole in the reading.
    """
    pointed = segment.get("likeThis")
    if pointed:
        path = os.path.join(work, str(pointed))
        if os.path.exists(path):
            return path
        note("a clip pointed at is not on disk; using the design clip")
    return os.path.join(work, design)


def do_render(engine: Engine, work: str, request: dict[str, Any]) -> None:
    """Stage two: speak each segment in its character's stored voice."""
    voices = request.get("voices") or {}
    rate = request.get("rate")

    for index, segment in enumerate(request.get("segments") or []):
        key = str(segment.get("key") or "")
        text = str(segment.get("text") or "").strip()
        if not text:
            continue

        voice_id = str(segment.get("voiceId") or "")
        reference = voices.get(voice_id)
        if not reference:
            emit(type="error", key=key, error="unknown-voice")
            continue

        try:
            # The request id is in the name, so an abandoned render can neither
            # overwrite a live one's clip nor have its own deleted out from
            # under it.
            name = "clip-%s-%04d-%s.wav" % (
                CURRENT_ID or "x",
                index,
                hashlib.sha256(key.encode("utf-8")).hexdigest()[:10],
            )
            target = os.path.join(work, name)
            clip = reference_for(work, reference, segment)

            # The plain cloning path, and only ever that one.
            #
            # The model also takes prompt_wav_path with prompt_text, which reads
            # like a higher-fidelity clone and is not: it is CONTINUATION. Given
            # both, it returns the reference clip followed by the new speech -
            # measured at 6.72 s against 1.44 s for the same sentence cloned
            # plainly, the difference being the whole 3.84 s design clip on the
            # front. Every line in the book would have opened with the character
            # reciting the sentence their voice was designed from.
            wav = engine.model.generate(
                text=directed(text, style_prefix(segment, rate)),
                reference_wav_path=clip,
                cfg_value=CFG_VALUE,
                inference_timesteps=TIMESTEPS,
                normalize=True,
                retry_badcase=True,
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
                emit(type="error", error="unknown-op")
        except Exception as failure:  # noqa: BLE001 - report and keep listening
            trace = traceback.format_exc()
            note(trace)
            keep_failure(args.work, op or "request", trace)
            # A model that failed once is not to be trusted to have survived it
            # - a CUDA fault in particular poisons everything after it - so it
            # is dropped and built again next time rather than reused.
            engine = None
            # The type only. The message can quote a path, and the host writes
            # what it is told into a log the writer may send us.
            emit(type="error", error=type(failure).__name__)

    return 0


if __name__ == "__main__":
    sys.exit(main())
