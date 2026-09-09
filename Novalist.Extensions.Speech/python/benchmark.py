"""Offline Qwen benchmark using fixed prose, never a manuscript.

Run with the extension's venv Python. Generate a reference with --mode design,
then pass that WAV to --mode clone for all precision comparisons. Each process
loads just one checkpoint; model loading and prompt preparation are timed
separately from synthesis. Run GPU benchmarks sequentially.
"""

from __future__ import annotations

import argparse
import contextlib
import cProfile
import hashlib
import json
import os
from pathlib import Path
import platform
import sys
import time

import sidecar


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=("design", "clone"), required=True)
    parser.add_argument("--dtype", choices=("default", "float32", "float16", "bfloat16"), default="default")
    parser.add_argument("--reference", type=Path)
    parser.add_argument("--language", choices=("de", "en"), default="de")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--text-file", type=Path, help="optional fixed benchmark prose for longer clone comparisons")
    parser.add_argument("--profile", action="store_true", help="profile the last run (timings include profiler overhead)")
    parser.add_argument("--inference-mode", action="store_true")
    args = parser.parse_args()
    if args.runs < 1 or (args.mode == "clone" and args.reference is None):
        parser.error("positive --runs and a --reference for cloning are required")

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import torch
    import numpy as np
    from huggingface_hub import snapshot_download
    with contextlib.redirect_stdout(sys.stderr):
        from qwen_tts import Qwen3TTSModel

    args.output.mkdir(parents=True, exist_ok=True)
    # Explicit torch selection: production now uses MLX on Apple Silicon.
    device = sidecar.pick_device()
    engine = sidecar.Engine(device, sidecar.model_dtype(device))
    if args.dtype != "default":
        engine.dtype = getattr(torch, args.dtype)

    def sync():
        if engine.device == "mps":
            torch.mps.synchronize()
        elif engine.device == "cuda":
            torch.cuda.synchronize()

    model_id = "Qwen/Qwen3-TTS-12Hz-1.7B-" + ("VoiceDesign" if args.mode == "design" else "Base")
    snapshot = snapshot_download(model_id, cache_dir=str(args.cache), local_files_only=True)
    report = dict(mode=args.mode, dtype=str(engine.dtype), device=engine.device,
                  torch=torch.__version__, macos=platform.mac_ver()[0],
                  architecture=platform.machine(), revision=Path(snapshot).name,
                  language=args.language, runs=[],
                  inference_mode=args.inference_mode, profiled=args.profile,
                  prefer_metal=os.environ.get("PYTORCH_MPS_PREFER_METAL", "0"))
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        model = Qwen3TTSModel.from_pretrained(snapshot, **sidecar._model_kwargs(engine))
    sync()
    report["load_seconds"] = time.perf_counter() - started
    report["parameter_bytes"] = sum(p.numel() * p.element_size() for p in model.model.parameters())
    print(json.dumps({"loaded": report}), flush=True)

    language = sidecar.qwen_language(args.language)
    reference_text = sidecar.design_text(args.language)
    prompt = None
    if args.mode == "clone":
        started = time.perf_counter()
        prompt = sidecar.clone_prompt(engine, model, "benchmark", str(args.reference), reference_text)
        sync()
        report["prompt_seconds"] = time.perf_counter() - started
        print(json.dumps({"prompt_seconds": report["prompt_seconds"]}), flush=True)

    text = ("Am nächsten Morgen öffnete sie das Fenster. Die Straße war still, "
            "und aus dem Garten kam der Duft von frischem Regen.") if args.language == "de" else (
            "The next morning she opened the window. The street was quiet, "
            "and the garden smelled of fresh rain.")
    if args.text_file:
        text = args.text_file.read_text(encoding="utf-8").strip()
    report["text"] = reference_text if args.mode == "design" else text
    report["reference_sha256"] = hashlib.sha256(args.reference.read_bytes()).hexdigest() if args.reference else None
    for index in range(args.runs):
        sidecar.seed_torch(42)
        sync()
        started = time.perf_counter()
        profiler = cProfile.Profile() if args.profile and index == args.runs - 1 else contextlib.nullcontext()
        inference = torch.inference_mode() if args.inference_mode else contextlib.nullcontext()
        with contextlib.redirect_stdout(sys.stderr), profiler, inference:
            if args.mode == "design":
                wavs, sr = model.generate_voice_design(
                    text=reference_text, language=language,
                    instruct=sidecar.voice_instruction("", language),
                    temperature=sidecar.TEMPERATURE, subtalker_temperature=sidecar.TEMPERATURE)
            else:
                wavs, sr = model.generate_voice_clone(
                    text=text, language=language, voice_clone_prompt=prompt,
                    non_streaming_mode=True, temperature=sidecar.TEMPERATURE,
                    subtalker_temperature=sidecar.TEMPERATURE)
        sync()
        elapsed = time.perf_counter() - started
        if isinstance(profiler, cProfile.Profile):
            profiler.dump_stats(str(args.output / "profile.pstats"))
        wav = np.asarray(wavs[0])
        if not wav.size or not np.isfinite(wav).all():
            raise RuntimeError("empty or non-finite benchmark audio")
        duration = wav.size / sr
        result = dict(index=index, cold=index == 0, seconds=elapsed,
                      audio_seconds=duration, real_time_factor=elapsed / duration,
                      peak=float(np.abs(wav).max()), rms=float(np.sqrt(np.mean(wav ** 2))))
        if engine.device == "mps":
            result["mps_allocated_bytes"] = torch.mps.current_allocated_memory()
            result["mps_driver_bytes"] = torch.mps.driver_allocated_memory()
        sidecar.write_wav(str(args.output / f"{args.mode}-{index}.wav"), wav, sr)
        report["runs"].append(result)
        (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
