# Speech

Gives Novalist a voice. Designs one for each character from their Codex entry, and reads your book in it with every line performed as the prose directs.

Everything runs on your own machine.

## What it is

Novalist works out the reading by itself and entirely offline — who speaks each line, how it should be said, and in whose voice. It has no way to *say* any of it. This extension supplies that, and it is the only part of the arrangement that loads a model.

Two stages, kept apart on purpose:

- **Design** makes a character's voice once, and Novalist stores the audio. The clip *is* the identity from then on.
- **Delivery** performs each line in that fixed voice, with the emotion supplied beside the words as a parameter.

That separation is the whole point. A character is one identity and many performances — furious in chapter three, grieving in chapter twenty, recognisably the same person.

### The two models

**Design** is [MOSS-VoiceGenerator](https://huggingface.co/OpenMOSS-Team/MOSS-VoiceGenerator) (OpenMOSS, Apache-2.0), a voice-design model that makes a speaker's timbre out of free-form text and nothing else. No reference recording is involved at any point, which is what lets a character's voice come from what you already wrote about them — and what keeps any real person's likeness out of this entirely.

Write *"a calm woman's voice, middle aged, unhurried and warm"* and that is what you get. It runs once per character, on your own machine, in a couple of seconds on a graphics card.

**Delivery** is [Chatterbox](https://github.com/resemble-ai/chatterbox) (Resemble AI), which speaks each line in that stored voice and takes an emotional intensity per utterance, separately from the timbre. The multilingual checkpoint, so your book is read in the language you wrote it in.

The split does more than divide labour: the designed clip fixes *who* is speaking, and delivery decides *what language* they speak and *how they feel*. A voice designed from an English description reads a German novel in German, in that voice.

## Installing

The first **Prepare** builds a Python environment and fetches both models — several gigabytes, once. Designing a voice loads the design model the first time you use it, not before: a reading needs only the delivery model, and nobody should wait for a designer they are not using.

1. Build this project. On Windows it deploys itself to `%APPDATA%\Novalist\Extensions\Speech`.
2. Open Novalist and visit **Extensions** once, which loads it.
3. Go to **Narration** (`Ctrl+Alt+R`). The cast rail offers **Prepare**.
4. Press it. The first run builds a Python environment and downloads torch and the model — several gigabytes, with progress and a Cancel button throughout.

After that, every character's row offers **Design a voice**, and so does the narrator's.

## What it needs

- **Python 3** on the machine. It is not bundled; if it is missing the extension says so and stops rather than guessing.
- **Disk** for a virtual environment and the model weights, under the extension's own settings folder. Never in your project.
- **A GPU, ideally.** It runs on CPU and says so — slow is a real answer, and a writer without a graphics card is not locked out of hearing their book.

Nothing is installed into your system Python. A speech stack pulls in torch and a pile of native wheels, and putting those into whatever interpreter happens to be on PATH is how you break somebody's unrelated work with a writing application.

## Which model

`chatterbox-tts`, which does the two things this arrangement needs:

- **Cloning from a reference clip**, which is what makes a designed voice a thing rather than a prompt.
- **An emotional intensity per utterance**, taken separately from the voice — the other half of one identity, many performances.

Novalist sends an eight-dimension emotion vector; the sidecar folds it into the single intensity the engine takes, with the heightening dimensions (anger, fear, surprise) pushing a reading up and the settling ones (calm, melancholy) pulling it down, held inside a range that stays listenable for a whole chapter.

The plan originally named IndexTTS-2 and MOSS-VoiceGenerator. Neither is on PyPI — `pip install indextts` fails outright — so this ships with what actually installs. The sidecar is source beside the assembly precisely so that can change without waiting for a release.

## The network

This extension **opens no socket**. The models are loaded from disk and run locally; the only traffic is the one-off download you started by pressing Prepare.

That is not politeness — it is the contract. Novalist's read-aloud promises that listening to your book sends nothing anywhere, and the interface a voice engine plugs into carries no endpoint, no key and no base URL. An engine is not entitled to break that promise on the application's behalf.

## How it talks to the models

A Python sidecar, one JSON object per line over its own standard input and output. No port to collide with, nothing to firewall, nothing listening when Novalist is not running, and the sidecar dies with the process that started it.

Audio never travels in a message. Clips are written into a working directory and named back; the extension reads the file. Base64 over a pipe would inflate every clip by a third and put a chapter of speech through a JSON parser twice.

The model's own chatter — progress bars, kernel warnings — goes to stderr, which Novalist routes to the debugger and never to a log file. A model that echoes its prompt must not be able to write a paragraph of somebody's novel into a diagnostic they might send us.

## The direction never enters the text

The emotion for a line travels as a parameter beside the words, never concatenated into them. Splicing a direction into the text is one bad prompt away from the model reading the word "angry" out loud in the middle of a sentence.

Equally, the design brief describes the **instrument** and never the mood — age, accent, pace, the register they speak in when nothing is wrong. Novalist strips the emotion vocabulary out of a brief before it ever arrives here, because an emotion written into a design prompt is baked into the timbre and cannot be got back out per line.

## Tests

`dotnet test tests/Novalist.Extensions.Tests --filter SpeechTests`

They run without Python, torch or any weights: the process is faked and the decisions are real. A test that needed six gigabytes of models is a test nobody runs.
