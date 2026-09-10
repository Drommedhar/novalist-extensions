# Windows RX 9070 XT validation

Measured locally on 2026-09-10: Windows 11 (build 26200), AMD Ryzen 9 9950X3D,
Radeon RX 9070 XT, Adrenalin 26.9.1 (32.0.31041.3013), Python 3.12,
PyTorch/torchaudio 2.9.1+rocm7.2.1, Qwen-TTS 0.1.1. Both checkpoints use BF16
and SDPA. The model receives fixed synthetic prose, never manuscript text.

The installer detected the Radeon, created `venv-rocm`, installed AMD's
official Windows packages and passed a BF16 GPU matrix operation. The previous
CPU environment and existing model cache were retained.

| Operation | Generation | Audio | Work / audio |
| --- | ---: | ---: | ---: |
| First voice design | 60.83 s | 8.88 s | 6.85 |
| First clone in process | 81.23 s | 6.56 s | 12.38 |
| Warm clone 1 | 10.47 s | 6.56 s | 1.60 |
| Warm clone 2 | 11.16 s | 6.56 s | 1.70 |

Model loading took 5.11 s for design and 5.48 s for cloning, measured
separately. Preparing the clone reference took 8.70 s. Cloning peaked at
4.79 GiB allocated and 5.27 GiB reserved in PyTorch. These are short passages,
not a bound for long passages or other voices. First-use MIOpen warnings and
substantial initial overhead were observed; the measurements do not isolate
kernel compilation from other initialization costs.

GPU inference is working, but these warm runs remain slower than playback.
They are not an RTX 4060 benchmark, a full-book throughput estimate, or a
quality evaluation. WAVs were checked for nonempty, finite samples. Voice
design and the approved-reference clone workflow both completed. See
[the raw measurements](windows-rocm.json) for revisions, reference hash,
waveform statistics and exact timings.

An additional integration check used the real .NET `VoiceEngine` and
`ProcessSidecarChannel` with the installed interpreter and unpacked sidecar.
It loaded both checkpoints, reported `ROCm: AMD Radeon RX 9070 XT`, and rendered
one requested passage to a 24 kHz WAV (6.48 s, 311,084 bytes). This checks the
host protocol and runtime selection as well as standalone model inference.

Reproduce with the extension's `venv-rocm/Scripts/python.exe`, its
`models/hub` cache, and `python/benchmark.py`:

```text
python benchmark.py --cache CACHE --output design --mode design --language en --runs 1
python benchmark.py --cache CACHE --output clone --mode clone --language en --reference design/design-0.wav --runs 3
```

Run GPU benchmarks sequentially. The first command creates a synthetic
reference using seed 42; subsequent measurements reuse its exact transcript.

## Different passages and the MIOpen fix

The initial warm measurements above repeated identical input. A follow-up with
different passages reproduced sustained stalls, not just model startup:

| Passage | Default generation | Default decoding | Total | FAST total |
| --- | ---: | ---: | ---: | ---: |
| Window and garden | 10.23 s | 20.92 s | 31.16 s | 10.28 s |
| Bird and desk | 8.60 s | 75.20 s | 83.80 s | 7.42 s |

With `MIOPEN_FIND_MODE=FAST`, decoding took 1.10 s and 0.19 s respectively.
The default runs produced 6.56 s and 5.92 s of audio; FAST produced 6.64 s and
5.76 s. The texts, models, reference and random seed were the same, but changing
convolution algorithms can affect floating-point results and sampling. These
are end-to-end workload comparisons, not identical-waveform kernel timings.
All four outputs were nonempty and finite; this is not a listening evaluation.

The default runs emitted repeated zero-workspace `GemmFwdRest` warnings,
consistent with the [reported native Windows Qwen decoder issue](https://github.com/ROCm/TheRock/issues/3077).
AMD documents [FAST selection](https://rocm.docs.amd.com/projects/MIOpen/en/latest/how-to/find-and-immediate.html#find-modes)
as using cached or heuristic solvers instead of the expensive search path.
The sidecar now selects it before GPU operations on Windows ROCm only, unless
the user has already set a mode. GPU inference remains enabled, BF16 weights
and generation settings remain the same, and no runtime download is needed.

The original 10–11 s warm figure should not be used as an estimate for reading
different passages with the old defaults. Raw follow-up measurements and the
effective sidecar results are in [windows-rocm-varied.json](windows-rocm-varied.json).
The diagnostic script is in `artifacts/speech-windows/` in the app checkout.
The production helper was also tested with three different passages: totals
were 10.01 s, 7.42 s and 10.11 s, with decoding taking 0.46 s, 0.22 s and 0.23 s.
