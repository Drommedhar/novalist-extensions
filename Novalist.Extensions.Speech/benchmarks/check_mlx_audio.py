"""Offline intelligibility check for benchmark WAVs, not a voice-quality score."""
import argparse
import json
import os
from pathlib import Path
import re

from benchmark_mlx import TEXTS, sidecar


def word_error_rate(expected, actual):
    left = re.findall(r"\w+", expected.casefold())
    right = re.findall(r"\w+", actual.casefold())
    previous = list(range(len(right) + 1))
    for i, a in enumerate(left, 1):
        current = [i]
        for j, b in enumerate(right, 1):
            current.append(min(current[-1] + 1, previous[j] + 1, previous[j-1] + (a != b)))
        previous = current
    return previous[-1] / len(left)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    from huggingface_hub import snapshot_download
    from mlx_audio.stt.utils import load_model

    model_id = "mlx-community/whisper-small-asr-fp16"
    model_path = Path(snapshot_download(model_id, cache_dir=str(args.cache), local_files_only=True))
    model = load_model(model_path)
    cases = [(name, f"{name}/clone-1.wav", TEXTS["short"]) for name in
             ("torch-short", "clone-public", "clone-matched", "clone-stream")]
    cases.extend([
        ("torch-long", "torch-long/clone-1.wav", TEXTS["long"]),
        ("mlx-long", "clone-long/clone-1.wav", TEXTS["long"]),
        ("mlx-long-stream", "clone-long-stream/clone-1.wav", TEXTS["long"]),
        ("mlx-design", "design/design-1.wav", sidecar.design_text("de")),
    ])
    rows = []
    for label, relative, expected in cases:
        wav = args.artifacts / relative
        assert wav.exists(), f"missing benchmark WAV: {relative}"
        output = model.generate(str(wav), language="de", temperature=0.0,
                                verbose=False, condition_on_previous_text=False)
        actual = output.text
        row = dict(case=label, expected=expected, transcript=actual,
                   word_error_rate=word_error_rate(expected, actual))
        rows.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)

    # A second, limited proxy: the model's speaker encoder compared with the
    # shared reference. This does not measure naturalness, prosody, or emotion.
    del model
    import mlx.core as mx
    import numpy as np
    from mlx_audio.tts.utils import load_model as load_tts
    from mlx_audio.utils import load_audio
    mx.clear_cache()
    tts_path = Path(snapshot_download("mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16",
                                    cache_dir=str(args.cache), local_files_only=True))
    tts = load_tts(tts_path)

    def embedding(path):
        encoded = tts.extract_speaker_embedding(load_audio(str(path), sample_rate=24000))
        mx.eval(encoded)
        values = np.array(encoded.astype(mx.float32)).ravel()
        return values / np.linalg.norm(values)

    reference = embedding(args.artifacts.parent / "speech-m5/design-fp32/design-0.wav")
    for row, (_, relative, _) in zip(rows, cases):
        if row["case"] != "mlx-design":
            row["reference_speaker_cosine"] = float(np.dot(reference, embedding(args.artifacts / relative)))
            print(json.dumps({"case": row["case"], "reference_speaker_cosine": row["reference_speaker_cosine"]}), flush=True)
    (args.artifacts / "intelligibility.json").write_text(json.dumps(
        dict(model=model_id, revision=model_path.name, cases=rows), ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
