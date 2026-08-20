# Speech

Local character and narrator speech for Novalist, built on
[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS). The extension designs a voice
from a text brief, keeps that identity, and reads the book without sending the
manuscript to a service.

## Why Qwen3-TTS

Novalist needs two abilities at the same time: free-form voice design and a
speaker identity that survives hundreds of later generations. Qwen documents
that exact workflow:

1. `Qwen3-TTS-12Hz-1.7B-VoiceDesign` speaks a controlled reference passage from
   the approved acoustic description.
2. `Qwen3-TTS-12Hz-1.7B-Base` combines that WAV with its exact transcript into
   a reusable ICL clone prompt.
3. The prompt is cached for the voice and reused for every passage.

Using checkpoints from the same family avoids the timbre reinterpretation that
occurred when one model designed a clip and an unrelated model cloned it. The
reference is three neutral sentences in the book's language. Character dialogue
is not used: the mood of an arbitrary quote would otherwise become part of the
reference and colour every later performance.

The host also joins adjacent sentences from the same speaker into passages of
up to 600 characters. Qwen therefore sees enough prose to produce a natural
cadence instead of restarting pitch and energy at every full stop.

## Emotion and direction

Qwen Base does not expose a reliable per-generation emotion parameter for
voice cloning. The extension therefore advertises `EmotionInferred`, not
`EmotionVector`, `EmotionInstruction`, or `EmotionReference`.

Plain prose is sent to the model—no bracketed emotion tags that might be spoken
aloud, and no controls accepted and silently ignored. Qwen derives tone,
prosody, and emphasis from the words. Novalist labels this delivery as automatic
and hides manual direction controls for this engine.

This trade-off is deliberate: identity and paragraph-level continuity are more
important here than a non-functional emotion slider. Automatic delivery only
knows the text in the passage; it cannot obey stage direction that is not part
of those words.

## Voice prompts

The design prompt contains stable audible traits only: age range, vocal gender,
pitch/register, timbre, articulation, cadence, and accent. Plot, point of view,
tense, body shape, height, clothes, and other visual or story metadata do not
describe a sound and are not sent to VoiceDesign. The narrator receives a
neutral close-mic audiobook baseline rather than a synopsis disguised as a
voice prompt.

The writer can edit the brief and regenerate with a fresh or pinned seed before
keeping the result. The exact reference transcript is stored beside the WAV;
both are required for high-fidelity ICL cloning.

## Languages

Qwen3-TTS currently supports Chinese, English, Japanese, Korean, German, French,
Russian, Portuguese, Spanish, and Italian. Regional tags such as `de-DE` map to
their base language. An unsupported writing language fails explicitly instead
of being silently read with English pronunciation.

## Installing

1. Build this project. On Windows it deploys to
   `%APPDATA%\Novalist\Extensions\Speech`.
2. Open **Settings → Narration** or the cast rail in **Narration**.
3. Choose **Prepare**. The first run creates an isolated Python environment and
   downloads both Qwen checkpoints plus PyTorch. The dialog shows the active
   checkpoint, transferred bytes, and percentage; a cancelled or interrupted
   checkpoint resumes from its partial Hugging Face cache.
4. Design new character and narrator voices, listen, and keep the ones you want.

Version 2 uses engine id `com.novalist.speech.qwen3`. Voices designed by the old
engine have no exact reference transcript and must be redesigned; they are not
silently treated as Qwen voices.

## Requirements

- Python 3.10–3.13. A suitable interpreter is used when present or fetched with
  [uv](https://github.com/astral-sh/uv) into the extension's private folder.
- NVIDIA CUDA when available; CPU remains supported but is much slower.
- About 16 GB of disk for the Python environment, model cache, and both 1.7B
  checkpoints. The model cache is kept under the extension data folder.
- Enough memory for one checkpoint at a time. VoiceDesign and Base are unloaded
  before the other is loaded to keep peak VRAM bounded.

The Speed control is implemented after synthesis with pitch-preserving time
stretching. It does not alter the reference voice or inject a pace phrase into
the manuscript.

### Hugging Face downloads

The weights come from the public Hugging Face repositories
`Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` and
`Qwen/Qwen3-TTS-12Hz-1.7B-Base`. No account is required. Under Novalist's
extension settings, **Speech downloads** offers an optional masked Hugging Face
token. It is passed to the Hub only through the sidecar's environment to avoid
anonymous API rate limits; it is never put in a protocol message, command-line
argument, or log. Authentication does not guarantee a faster CDN connection.

## Process boundary

The model runs in a Python sidecar using one UTF-8 JSON object per line over
standard input/output. Audio travels through private temporary WAV files rather
than base64 JSON or a local network port. Model diagnostics go to stderr so a
prompt cannot leak into an ordinary application log.

The sidecar and dependencies are isolated from system Python. Model traffic is
limited to the user-started preparation download; narration itself runs locally
from the cached checkpoints.

## Tests

```text
dotnet test tests/Novalist.Extensions.Tests --filter SpeechTests
python -m unittest discover -s Novalist.Extensions.Speech/python
```

The .NET tests fake the sidecar process. The Python tests exercise language
mapping, controlled reference text, acoustic instructions, prompt caching,
speed bounds, and seeds without downloading model weights.
