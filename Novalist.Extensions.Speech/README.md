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
   downloads both Qwen checkpoints and the speech runtime (MLX on Apple Silicon
   Macs, PyTorch elsewhere). The dialog shows the active
   checkpoint, transferred bytes, and percentage; a cancelled or interrupted
   checkpoint resumes from its partial Hugging Face cache.
4. Design new character and narrator voices, listen, and keep the ones you want.

Version 2 uses engine id `com.novalist.speech.qwen3`. Voices designed by the old
engine have no exact reference transcript and must be redesigned; they are not
silently treated as Qwen voices.

## Requirements

- Python 3.10–3.13. A suitable interpreter is used when present or fetched with
  [uv](https://github.com/astral-sh/uv) into the extension's private folder.
- Native Apple Silicon macOS uses MLX with BF16 Qwen weights. PyTorch uses
  NVIDIA CUDA, AMD ROCm on supported Windows cards, or MPS where available;
  other machines use CPU.
- Allow about 10 GB on Apple Silicon, 16 GB for the usual PyTorch setup, and
  extra space for AMD's runtime, package downloads and any retained older
  environment. The model cache is kept under the extension data folder.
- Enough memory for one checkpoint at a time. VoiceDesign and Base are unloaded
  before the other is loaded to keep peak VRAM bounded.

The Speed control is implemented after synthesis with pitch-preserving time
stretching. It does not alter the reference voice or inject a pace phrase into
the manuscript.

### Windows GPU preparation

Update the extension and choose **Prepare** once. Existing Qwen voices and
cached model weights are reused. Settings shows the actual card and runtime
after preparation: **CUDA: NVIDIA …** or **ROCm: AMD Radeon …**. A CPU label
means the GPU is not doing the speech generation; more system RAM does not
change that.

NVIDIA installs a matched PyTorch/torchaudio 2.9.1 CUDA 12.8 pair, including
support for RTX 40/50-series cards. The installer requests GPU wheels during
dependency resolution so a newer CPU wheel cannot take their place.

On Windows x64, supported Radeon cards get a separate `venv-rocm`, using
Python 3.12 and AMD's pinned ROCm 7.2.1/PyTorch 2.9.1 wheels. Python is fetched
privately when needed. The allowlist follows AMD's Windows matrix: RX 9070 XT,
RX 9070, RX 9060 XT, RX 7900 XTX, RX 7700, AI PRO R9700 and PRO W7900
(including Dual Slot). Other Radeon models retain CPU support. Machines with
both NVIDIA and AMD adapters prefer NVIDIA.

See [AMD's driver prerequisites and installation recipe](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/install/installrad/windows/install-pytorch.html).
That release documents Adrenalin 26.2.2; the local RX 9070 XT validation also
uses 26.9.1. No system driver is installed by the extension. PyTorch calls AMD
devices `cuda` internally; the settings label distinguishes ROCm from CUDA.

Preparation executes a BF16 matrix operation on the GPU before marking the
environment ready. Download or GPU failures retain `install-failed.txt` and
leave preparation retryable. The prior CPU environment remains on disk during
AMD migration; it is not used for GPU narration.

See the [RX 9070 XT validation](benchmarks/windows-rocm.md) for measured
first-use and warm generation times, memory use, and reproduction commands.

Windows ROCm also defaults to MIOpen's `FAST` kernel selection before the first
GPU operation. This avoids lengthy convolution searches when each new passage
has a different audio length. Previously those searches could take tens of
seconds even after the model was warm. An explicit `MIOPEN_FIND_MODE` setting
is respected. This update needs a restart, with no additional preparation or
model download. The 1.7B model can still generate more slowly than playback.

### Book size and rendering time

Books are processed in passages, with resumable chapter output for audiobook
export. A 50,000-word manuscript does not have to fit into the model's context
at once. Generation time and disk space are the practical constraints, and
GPU memory must fit one model plus the current passage's working memory.

At the host's estimate of 155 words/minute, 50,000 words is about 5 h 23 min.
Qwen's 24 kHz mono, 16-bit WAV is about 0.93 GB (0.87 GiB) for that duration;
the host's 64 kb/s MP3/M4B export is about 155 MB, plus metadata and cover art.
These are estimates; pauses and speaking pace change the result. Playback
cache, chapter WAVs kept for resuming, and exported copies can coexist. Allow
a few GB per book in addition to the shared speech runtime and model files.

Benchmark a representative chapter to estimate production time. The export
panel learns from completed renders. More RAM helps only if memory was the
constraint; the playback Speed control stretches generated audio and does not
make the model compute faster. Replaying unchanged cached passages avoids
synthesis, while generating new passages can still be slower than playback.

### macOS performance

Native Apple Silicon Macs use a separate `venv-mlx` environment and the
unquantized BF16 MLX conversions of both Qwen checkpoints. An existing install
needs **Prepare** once to fetch the new runtime and model files. The previous
PyTorch environment and cache are retained; saved Qwen voices and their exact
transcripts work without redesign.

The adapter preserves the existing sampling settings, reference prompt cache,
voice storage, and pitch-preserving speed control. Metal allocations are
released when switching between voice design and narration. Both models decode
in 320 ms chunks to bound GPU memory. At normal speed, an updated host starts
playing narration while generation continues only after measuring sustained
throughput with enough headroom. Slower or uneven generation buffers the full
passage first; a later underrun buffers the remainder once. Complete
clips are still returned for replay, voice references, and audiobook export.
Older hosts retain complete-passage playback with the same memory reduction.

At other speed settings, the decoder still uses chunks, but playback waits for
the complete waveform so pitch-preserving time stretching can avoid seams.
No extra model download or voice redesign is needed for this streaming update.
See [the integrated streaming benchmark](benchmarks/macos-streaming.md).

See [the MLX benchmark report](benchmarks/macos-mlx.md) for measured performance,
weight comparison, quality limitations, and reproduction commands. The earlier
[PyTorch BF16 benchmark](benchmarks/macos-m5-pro.md) is retained for comparison.

### Hugging Face downloads

The weights come from the public Hugging Face repositories
`Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` and
`Qwen/Qwen3-TTS-12Hz-1.7B-Base` on PyTorch. MLX uses the corresponding
`mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16` and
`mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16` repositories. No account is required. Under Novalist's
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
