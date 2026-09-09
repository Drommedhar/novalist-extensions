# Integrated MLX streaming

2026-09-09, Apple M5 Pro, 48 GiB, macOS 26.5.2. Same BF16 checkpoints,
reference WAV and sampling settings as [the MLX comparison](macos-mlx.md).
Measurements exercise the production Python adapter and sidecar, including
chunk WAV writing, protocol emission and the complete cached waveform.

| Passage | Generation | First chunk | Peak MLX allocations | Audio |
| --- | ---: | ---: | ---: | ---: |
| Short, first use of reference | 8.15 s | 0.52 s | 5.81 GB | 7.44 s |
| Short, two warm runs averaged | 7.85 s | 0.40 s | 5.18 GB | 7.44 s |
| Long, two runs averaged | 29.82 s | 0.45 s | 5.23 GB | 28.32 s |
| Short at 0.9× speed | 8.71 s | Buffered | 5.18 GB | 8.27 s |

The previous short non-streamed run peaked at 10.19 GB. Warm streaming reduces
that GPU allocation peak by about **49%**, while active allocations stay about
4.63 GB. These figures exclude macOS, other apps and CPU-side process overhead;
they are not a measurement of total system RAM. M2/16 GB remains untested.

The initial browser implementation started with roughly 640 ms of generated
audio. That proved insufficient under slower generation and has been replaced
by the throughput check described below. First-chunk time is not audible
startup latency. Model loading and uncached reference
preparation can add delay. Generation here is still slightly slower than
playback; an underrun waits for more samples rather than dropping or speeding
up speech. Streaming reduces waiting and memory, not the underlying number of
model operations.

Both narration and voice design use incremental decoding. Normal-speed
narration also sends live chunks through the extension and host to a shared
Web Audio clock. Each chunk is scheduled directly after its predecessor without
an inserted segment gap or independent fade. A complete clip still arrives
once per passage for caching and audiobook export. Other speed settings retain
whole-waveform pitch-preserving stretching after incremental decoding.

Every sample in the emitted chunk WAVs concatenates **exactly** to the final
cached PCM waveform in all five normal-speed benchmark runs. This establishes
preview/cache consistency, not perceptual identity with the previous decoder.
Model weights and sampling settings are unchanged. Full perceived-quality
comparisons still require listening tests.

Raw runs: [macos-streaming.json](macos-streaming.json). To reproduce from the
extension repo root, after the earlier benchmark generated the reference WAV:

```sh
"$HOME/Library/Application Support/Novalist/extensions/com.novalist.speech/venv-mlx/bin/python" \
  Novalist.Extensions.Speech/benchmarks/benchmark_streaming.py
```

Results and WAVs are written under `artifacts/speech-stream/`. The script uses
cached models only and resets MLX's peak counter before each generation.


## Playback buffering correction

The host now measures elapsed generation/delivery time per second of decoded
audio, including request startup. Early playback requires at least seven chunks
observed over a second, at least two seconds of audio in reserve, and both
average throughput and the slowest observed chunk interval to leave room for a
25% slowdown. Bursty delivery cannot establish sustained throughput.

Slower or uneven generation waits for the complete passage. This deliberately
avoids guessing total speech duration from character count. An unexpected
underrun after playback begins buffers the remaining passage once, keeping all
samples instead of repeatedly playing single chunks. The incremental MLX
decoder and its memory savings remain enabled in every case.

A fresh installed-extension measurement produced 7.68 seconds of audio in
7.77 seconds (1.01 seconds of generation per second of audio). Its timestamp
trace is replayed in the host's buffering regression tests, alongside simulated
0.9×, 1.05×, 1.56× and 2.5× compute/audio ratios, faster sustained generation,
bursts, and a mid-passage slowdown. At this measured speed the host buffers the
whole passage, because there is no comfortable sustained playback margin.
Future machine load can still change; the reserve and underrun handling are
safeguards, not a guarantee about future GPU scheduling.
