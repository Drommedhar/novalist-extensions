# Speech on M5 Pro — 2026-09-09

The macOS optimization increases measured narration throughput by **34%** and
reduces active Metal allocations by **51%**, using the existing Qwen models and
voices. It does **not** make this PyTorch configuration real-time: synthesis
still takes about 2.12 seconds per second of audio.

## Environment and method

- Apple M5 Pro, 48 GiB unified memory, macOS 26.5.2, native ARM64 Python 3.12.
- PyTorch 2.13.0, qwen-tts 0.1.1, Transformers 4.57.3, MPS with SDPA.
- Base revision `fd4b254389122332181a7c3db7f27e918eec64e3`;
  VoiceDesign revision `5ecdb67327fd37bb2e042aab12ff7391903235d3`.
- Cached checkpoints only; downloads disabled. Separate processes run
  sequentially, with GPU synchronization around each timed operation.
- Seed 42, normal reading rate, existing temperature defaults, no text or
  audio truncation. All clone comparisons use the same FP32-designed German
  reference WAV and its exact controlled transcript.
- Narration text: “Am nächsten Morgen öffnete sie das Fenster. Die Straße war
  still, und aus dem Garten kam der Duft von frischem Regen.”
- Three generations per narration configuration. The table averages the last
  two; the first generation is reported separately in the raw results. “First”
  means first in that process, not a cleared OS/Metal shader cache.
- Model loading and clone-prompt preparation are timed separately and excluded
  from generation. Different precisions can produce different audio lengths,
  so throughput comparisons use elapsed time divided by audio duration (RTF).
  Lower RTF is better; RTF below 1 means faster than playback.

## Narration results

| Configuration | Generation | Audio duration | RTF | Active Metal memory |
| --- | ---: | ---: | ---: | ---: |
| Previous macOS default: FP32 | 21.38 s | 7.52 s | 2.843 | 8.61 GB |
| New macOS default: BF16 | 15.80 s | 7.44 s | 2.123 | 4.23 GB |
| FP16 comparison | 15.04 s | 7.12 s | 2.113 | 4.23 GB |
| BF16 + prefer native Metal matmul | 15.88 s | 7.44 s | 2.134 | 4.23 GB |

Generation time for the fixed passage falls by 26%; audio-normalized throughput
increases by 34%. Model parameter storage falls from 7.71 GB to 3.86 GB. Memory
figures above are decimal GB and are post-generation allocations, not peak
memory or process RSS. The allocator's total driver allocation was 17.83 GB in
FP32 and 8.58 GB in BF16.

BF16 preserves the numeric range Qwen uses in its
[upstream evaluation](https://github.com/QwenLM/Qwen3-TTS#evaluation). FP16
offers no meaningful throughput advantage here. The
[native Metal matmul option](https://docs.pytorch.org/docs/2.14/mps_environment_variables.html)
also offers no measured improvement and is not enabled. Wrapping generation in
inference mode and experimenting with the fixed-length code predictor's stop
check did not produce a useful gain, so neither change ships.

Voice design's first generation took 34.97 s for 10.96 s of audio in FP32 and
22.54 s for 10.56 s in BF16. Only one FP32 design generation was measured; this
is a first-generation comparison, not a warmed average.

## What ships

- Probe BF16 allocation and matrix execution on MPS before loading weights.
  Use BF16 when supported, retaining FP32 on unsupported torch/macOS builds.
- Release unused Metal allocator memory when changing checkpoints, just as the
  CUDA path already does.
- Include precision alongside device in the engine's preparation detail.

Sampling, voice identity, the checkpoint family, passage boundaries, and the
audio protocol are unchanged. CPU and CUDA precision choices are unchanged.
Completed passages still arrive one at a time; audio is not streamed within a
passage.

The benchmark verifies non-empty, finite waveforms and records peak/RMS levels.
This is a performance and numerical-validity check, not a perceptual quality
evaluation. WAVs are retained locally for listening. The installed sidecar is
also exercised through preparation, English voice design, and two narration
clips, including both directions of model switching.

## Reproduce

From the extensions repository, using the already prepared extension:

```sh
speech_data="$HOME/Library/Application Support/Novalist/extensions/com.novalist.speech"
speech_python="$speech_data/venv/bin/python"
speech_bench="Novalist.Extensions.Speech/python/benchmark.py"

"$speech_python" "$speech_bench" --cache "$speech_data/models/hub" \
  --output artifacts/speech-benchmark/reference --mode design --dtype float32 --runs 1

for speech_precision in float32 bfloat16; do
  "$speech_python" "$speech_bench" --cache "$speech_data/models/hub" \
    --output "artifacts/speech-benchmark/$speech_precision" --mode clone \
    --dtype "$speech_precision" --runs 3 \
    --reference artifacts/speech-benchmark/reference/design-0.wav
done
```

Use `--dtype default` to exercise the production precision selection. Each
output folder contains `results.json` and generated WAVs. `--profile` profiles
the last run; exclude that run from timing comparisons. `--inference-mode` is
an experimental comparison switch, not a production setting.

See [recorded measurements](macos-m5-pro.json) for individual timings and
memory measurements. Local WAVs and diagnostic profiles are under
`artifacts/speech-m5/` and are not shipped in the extension.
