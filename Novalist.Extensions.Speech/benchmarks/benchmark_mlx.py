"""Isolated MLX Qwen benchmark. Does not import MLX into the extension.

Use a separate venv with mlx-audio==0.5.3 and pre-download BF16 checkpoints.
The matched API bypasses the public clone API's repetition-penalty floor of
1.5 to use the current extension's 1.05. This is benchmark instrumentation,
not an integration proposal. All generation is offline.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
import sidecar

TEXTS = {
    "short": "Am nächsten Morgen öffnete sie das Fenster. Die Straße war still, und aus dem Garten kam der Duft von frischem Regen.",
    "long": "Am nächsten Morgen öffnete sie das Fenster. Die Straße war still, und aus dem Garten kam der Duft von frischem Regen. Auf dem Tisch lag noch immer der Brief, den sie am Abend nicht hatte öffnen wollen. Jetzt nahm sie ihn in die Hand und setzte sich ans Licht. Draußen fuhr ein Fahrrad vorbei, irgendwo schlug eine Tür. Sie faltete das Papier auseinander und begann zu lesen. Nach den ersten Zeilen hielt sie inne. Dann las sie weiter, langsam und aufmerksam, während die Sonne über die Dächer stieg.",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--revision", help="optional pinned Hugging Face checkpoint revision")
    parser.add_argument("--reference", type=Path)
    parser.add_argument("--mode", choices=("clone", "design"), default="clone")
    parser.add_argument("--api", choices=("public", "matched"), default="public")
    parser.add_argument("--passage", choices=TEXTS, default="short")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--stream", action="store_true")
    parser.add_argument("--interval", type=float, default=0.32)
    args = parser.parse_args()
    if args.runs < 1 or (args.mode == "clone" and args.reference is None):
        parser.error("positive --runs and a --reference for cloning are required")
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import mlx.core as mx
    from mlx.utils import tree_flatten
    import numpy as np
    from huggingface_hub import snapshot_download
    from mlx_audio.tts.utils import load_model
    from mlx_audio.utils import load_audio

    assert mx.default_device() == mx.gpu, "benchmark requires the Apple GPU"
    model_id = "mlx-community/Qwen3-TTS-12Hz-1.7B-" + ("Base" if args.mode == "clone" else "VoiceDesign") + "-bf16"
    snapshot = Path(snapshot_download(model_id, revision=args.revision,
                                      cache_dir=str(args.cache), local_files_only=True))
    config = json.loads((snapshot / "config.json").read_text())
    assert not config.get("quantization") and not config.get("quantization_config"), "expected unquantized weights"
    args.output.mkdir(parents=True, exist_ok=True)
    report = dict(backend="mlx", mode=args.mode, api=args.api, model=model_id,
                  revision=snapshot.name, passage=args.passage,
                  text=sidecar.design_text("de") if args.mode == "design" else TEXTS[args.passage],
                  reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest() if args.reference else None,
                  versions={p: importlib.metadata.version(p) for p in ("mlx", "mlx-audio", "mlx-metal", "transformers", "numpy")},
                  python=platform.python_version(), macos=platform.mac_ver()[0],
                  architecture=platform.machine(), stream=args.stream, interval=args.interval,
                  seed=42, temperature=0.9, top_k=50, top_p=1.0, max_tokens=8192,
                  repetition_penalty=1.5 if args.mode == "clone" and args.api == "public" else 1.05,
                  runs=[])
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        model = load_model(snapshot)
    mx.eval(model.parameters())
    mx.synchronize()
    report["load_seconds"] = time.perf_counter() - started
    assert model.tokenizer is not None and model.speech_tokenizer is not None
    dtype_bytes = {}
    for name, value in tree_flatten(model.parameters()):
        dtype_bytes[str(value.dtype)] = dtype_bytes.get(str(value.dtype), 0) + value.nbytes
    report["parameter_bytes_by_dtype"] = dtype_bytes
    print(json.dumps({"loaded": report}), flush=True)
    reference = load_audio(str(args.reference), sample_rate=model.sample_rate) if args.reference else None
    reference_text = sidecar.design_text("de")

    for index in range(args.runs):
        mx.random.seed(42)
        mx.synchronize()
        mx.reset_peak_memory()
        started = time.perf_counter()
        common = dict(temperature=0.9, top_k=50, top_p=1.0, max_tokens=8192,
                      stream=args.stream, streaming_interval=args.interval, verbose=False)
        if args.mode == "design":
            generator = model.generate_voice_design(text=reference_text, language="German",
                instruct=sidecar.voice_instruction("", "German"), repetition_penalty=1.05, **common)
        elif args.api == "matched":
            generator = model._generate_icl(text=TEXTS[args.passage], language="German",
                ref_audio=reference, ref_text=reference_text, repetition_penalty=1.05, **common)
        else:
            generator = model.generate(text=TEXTS[args.passage], lang_code="German",
                ref_audio=reference, ref_text=reference_text, repetition_penalty=1.05, **common)
        chunks = []
        arrivals = []
        tokens = 0
        sr = model.sample_rate
        with contextlib.redirect_stdout(sys.stderr):
            for result in generator:
                mx.eval(result.audio)
                mx.synchronize()
                if not result.audio.size:
                    continue
                arrivals.append(dict(seconds=time.perf_counter() - started,
                                     audio_seconds=result.audio.size / result.sample_rate))
                chunks.append(np.array(result.audio.astype(mx.float32)))
                sr = result.sample_rate
                tokens += result.token_count
        mx.synchronize()
        elapsed = time.perf_counter() - started
        if not chunks:
            raise RuntimeError("generated no audio")
        wav = np.concatenate(chunks)
        assert np.isfinite(wav).all() and np.max(np.abs(wav)) > 0
        duration = wav.size / sr
        audio_before = 0.0
        max_stall = 0.0
        for arrival in arrivals:
            max_stall = max(max_stall, arrival["seconds"] - arrivals[0]["seconds"] - audio_before)
            audio_before += arrival["audio_seconds"]
        row = dict(index=index, first_in_process=index == 0, seconds=elapsed,
                   audio_seconds=duration, real_time_factor=elapsed / duration,
                   first_audio_seconds=arrivals[0]["seconds"], chunks=arrivals,
                   required_extra_buffer_seconds=max_stall, tokens=tokens,
                   peak=float(np.abs(wav).max()), rms=float(np.sqrt(np.mean(wav ** 2))),
                   active_memory_bytes=mx.get_active_memory(), peak_memory_bytes=mx.get_peak_memory())
        sidecar.write_wav(str(args.output / f"{args.mode}-{index}.wav"), wav, sr)
        report["runs"].append(row)
        (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(row), flush=True)


if __name__ == "__main__":
    main()
