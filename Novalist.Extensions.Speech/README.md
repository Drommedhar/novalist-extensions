# Speech

Gives Novalist a voice. Designs one for each character from their Codex entry, and reads your book in it with every line performed as the prose directs.

Everything runs on your own machine.

## What it is

Novalist works out the reading by itself and entirely offline — who speaks each line, how it should be said, and in whose voice. It has no way to *say* any of it. This extension supplies that, and it is the only part of the arrangement that loads a model.

Two stages, kept apart on purpose:

- **Design** makes a character's voice once, and Novalist stores the audio. The clip *is* the identity from then on.
- **Delivery** performs each line in that fixed voice, with the emotion supplied beside the words as a parameter.

That separation is the whole point. A character is one identity and many performances — furious in chapter three, grieving in chapter twenty, recognisably the same person.

## The model

Both stages are [VoxCPM2](https://huggingface.co/openbmb/VoxCPM2) (OpenBMB, Apache-2.0, commercial use permitted). It designs a speaker's timbre out of free-form text with no reference recording anywhere, and it clones that stored clip for every line the character afterwards speaks. 2B parameters, 48 kHz out, thirty languages.

Write *"a calm woman's voice, middle aged, unhurried and warm"* and that is what you get. Design runs once per character; delivery runs on every line.

### Why one model rather than two good ones

This used to be two: a designer and a separate deliverer. That is the arrangement to avoid, and the reason is specific to how Novalist works rather than to either model.

The pipeline designs a clip once and then clones it for ever. When the designer and the cloner are different models, the timbre that was designed is not the timbre that comes back — the clip is re-interpreted through another model's training distribution on the way. No benchmark measures that loss, because no other pipeline has that seam in it. One model has no seam: what it designed is what it can reproduce.

It also fixed the accent. The previous designer was trained on Chinese and English only, so a German brief was outside what it had ever seen. Its clip was English audio, and because a cloner copies the phonetics of its reference — the vowel space, the consonants, the prosody, which is what an accent is made of — every German line in the book came back English-accented no matter what language tag it was sent. VoxCPM2 speaks German natively, so the stored clip is German and there is no cross-language hop to leak an accent through.

### The direction, and the one place it could have gone wrong

VoxCPM2 takes its style direction as a parenthesised phrase in front of the words: `(sharply angry)Get out.` That is precisely the in-band arrangement the SDK warns about, because a tag in the text is one bad prompt away from being read out loud.

The rule does not bend; the adapter absorbs it, which is what the SDK says an adapter is for. Two invariants make it safe, and both are covered by tests:

1. **Every phrase is a constant in `sidecar.py`.** The prefix is composed from the emotion vector, the dialogue flag and the reading rate — numbers and booleans. Nothing the writer typed, and nothing derived from the manuscript, can reach it. This extension deliberately does not advertise `EmotionInstruction`, so the host never sends it a sentence to splice in.
2. **A prefix is always emitted**, even for a neutral line. That is what stops the prose occupying the slot: a paragraph that legitimately opens with a bracket is no longer the first thing the model sees.

The design brief is the one place words you wrote reach a prefix, because that is what a brief is for. Brackets are removed from it first, so a bracket in your own description cannot close the group early and tip the rest of it into the text.

The reading rate reaches the model the same way. VoxCPM2 has no rate argument, so the Speed control becomes one of five fixed pace phrases — which is why it does something here rather than being accepted and discarded.

### Pointing at a line instead of describing one

The host lets a writer pick a line they have already heard and say "like that". This engine takes it: the clip becomes the reference for that line instead of the character's design clip, and a cloning model copies its whole delivery — the timbre and the prosody together — so a performance somebody has already approved directs the next line more exactly than any word for it.

It is safe here for one reason: the host only ever offers clips in the **same character's voice**. Given another character's line it would not read this one their way, it would read it in their voice. A clip that has since left the cache falls back to the design clip rather than failing, because losing the resemblance is a disappointment and losing the line is a hole in the reading.

The whole path existed on the host side — a list of lines heard, a store, an RPC — and no engine declared `EmotionReference`, so picking a line and pressing apply changed nothing and said nothing about it.

## Installing

1. Build this project. On Windows it deploys itself to `%APPDATA%\Novalist\Extensions\Speech`.
2. Open Novalist and visit **Extensions** once, which loads it.
3. Go to **Settings — Narration**, or to the cast rail in **Narration** (`Ctrl+Alt+R`). Either offers **Prepare**.
4. Press it. The first run builds a Python environment and downloads torch and the model — several gigabytes, with progress and a Cancel button throughout.

After that, every character's row offers **Design a voice**, and so does the narrator's.

**Only the first time.** The model lives in a process that ends with Novalist, so being prepared has never survived a restart — and the host now starts an engine that has everything it needs, in the background, when it opens. What still waits to be asked is an engine with a download outstanding, which is a decision about somebody's connection rather than a startup.

## What it needs

- **No Python of your own.** The model wants 3.10 to 3.12 — a real ceiling rather than a preference, because it publishes no wheel above 3.12 and an interpreter outside the range builds a virtual environment happily and then fails the install minutes later. A machine that has one in range uses it; a machine that does not gets one fetched for it, with [uv](https://github.com/astral-sh/uv), into this extension's own folder. Nothing goes on PATH, nothing is installed system-wide, and deleting the folder undoes all of it.

  It no longer settles for an interpreter it can see will not work. That only moved the failure to the end of a two-gigabyte download, where it arrived as a wall of pip output instead of as a sentence.
- **CUDA 12 or newer** for the GPU path. The environment builder asks the driver what is there and fetches the matching torch.
- **Roughly 8 GB of VRAM** for a comfortable reading, and about 8 GB of disk for the environment and the weights, under the extension's own settings folder. Never in your project.
- **A GPU, ideally.** It runs on CPU and says so — slow is a real answer, and a writer without a graphics card is not locked out of hearing their book. On Windows the compile step is off by default, because it needs Triton and there is no Windows build of it.

Nothing is installed into your system Python. A speech stack pulls in torch and a pile of native wheels, and putting those into whatever interpreter happens to be on PATH is how you break somebody's unrelated work with a writing application.

## Reliability of a design

Voice design is **not deterministic**, and the model's own documentation says so — it recommends generating a description one to three times. Novalist is built around that: it plays a designed voice and asks before keeping it, nothing is stored or cast until you press Keep, and re-designing is a supported thing rather than an admission of failure.

Every attempt is its own draw, and the host offers a **Seed** for the writer who wants one particular voice back. That is the right way round: the seed used to be derived from the brief, so pressing Design again on an unchanged description returned the identical voice — and "try again", which is the one thing the design dialog is built around, did nothing at all.

The number a voice was drawn with is reported back with the clip and stored beside it, because a voice heard once and not kept is otherwise unrecoverable: the clip is stored as audio rather than re-derived, and nothing else remembers the draw.

## The network

This extension **opens no socket**. The model is loaded from disk and runs locally; the only traffic is the one-off download you started by pressing Prepare — the wheels, the weights, and an interpreter where the machine has none.

That is not politeness — it is the contract. Novalist's read-aloud promises that listening to your book sends nothing anywhere, and the interface a voice engine plugs into carries no endpoint, no key and no base URL. An engine is not entitled to break that promise on the application's behalf.

## How it talks to the model

A Python sidecar, one JSON object per line over its own standard input and output. No port to collide with, nothing to firewall, nothing listening when Novalist is not running, and the sidecar dies with the process that started it.

Audio never travels in a message. Clips are written into a working directory and named back; the extension reads the file. Base64 over a pipe would inflate every clip by a third and put a chapter of speech through a JSON parser twice.

The model's own chatter — progress bars, kernel warnings — goes to stderr, which Novalist routes to the debugger and never to a log file. A model that echoes its prompt must not be able to write a paragraph of somebody's novel into a diagnostic they might send us.

The sidecar is source beside the assembly on purpose: a writer whose card needs a different torch build, or who has a better checkpoint, should not wait for us to publish a release. Edit it and it is left alone; delete it to be given ours back.

## The brief describes the instrument

Age, accent, pace, the register they speak in when nothing is wrong. Novalist strips the emotion vocabulary out of a brief before it ever arrives here, because an emotion written into a design prompt is baked into the timbre and cannot be got back out per line — and you would have a character who sounds the same at the funeral and the wedding.

## Tests

    dotnet test tests/Novalist.Extensions.Tests --filter SpeechTests
    python -m unittest discover -s Novalist.Extensions.Speech/python

The first runs without Python, torch or any weights: the process is faked and the decisions are real. The second covers what the sidecar decides before the model is reached — the direction, the language of a designed clip, the pace — which is where three parameters were previously arriving and being silently dropped. A test that needed eight gigabytes of weights is a test nobody runs.
