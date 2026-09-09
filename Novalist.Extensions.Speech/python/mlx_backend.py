"""Qwen adapter for Apple Silicon. Imported only by the macOS MLX path.

Keep the sidecar's Qwen-shaped API so the host's voice storage, language checks,
clip protocol and rate adjustment remain shared. No torch imports belong here.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass
class ClonePrompt:
    audio: Any
    text: str
    # The upstream cache keys audio by length/sum. Give each reference its own
    # cache so two different voices cannot collide, and let the sidecar's SHA256
    # fingerprint invalidate it when either the WAV or transcript changes.
    cache: dict = field(default_factory=dict)


class MlxQwenModel:
    def __init__(self, model: Any):
        self.model = model

    @classmethod
    def from_pretrained(cls, snapshot: str) -> "MlxQwenModel":
        import mlx.core as mx
        from mlx_audio.tts.utils import load_model

        model = load_model(snapshot)
        # Upstream logs resource-load failures and returns a partial model.
        # Refuse readiness here instead of failing on the first narration line.
        if model.tokenizer is None or model.speech_tokenizer is None:
            raise RuntimeError("MLX speech tokenizer could not be loaded")
        mx.eval(model.parameters())
        mx.synchronize()
        return cls(model)

    def create_voice_clone_prompt(self, *, ref_audio, ref_text, x_vector_only_mode=False):
        from mlx_audio.utils import load_audio

        if x_vector_only_mode or not ref_text:
            raise ValueError("MLX cloning requires the exact reference transcript")
        return ClonePrompt(load_audio(ref_audio, sample_rate=self.model.sample_rate), ref_text)

    @staticmethod
    def _chunks(results):
        import mlx.core as mx
        import numpy as np

        found = False
        sample_rate = None
        for result in results:
            mx.eval(result.audio)
            audio = np.array(result.audio.astype(mx.float32))
            if not audio.size or not np.isfinite(audio).all():
                raise RuntimeError("MLX generated invalid audio")
            if sample_rate is not None and result.sample_rate != sample_rate:
                raise RuntimeError("MLX sample rate changed within a passage")
            sample_rate = result.sample_rate
            found = True
            yield audio, sample_rate
        if not found:
            raise RuntimeError("MLX generated no audio")

    @staticmethod
    def _audio(results):
        import numpy as np
        chunks = list(MlxQwenModel._chunks(results))
        return [np.concatenate([audio for audio, _ in chunks])], chunks[0][1]

    def generate_voice_design(self, *, text, language, instruct, temperature, subtalker_temperature):
        return self._audio(self.model.generate_voice_design(
            text=text, language=language, instruct=instruct,
            **self._sampling(temperature, subtalker_temperature)))

    @staticmethod
    def _sampling(temperature, subtalker_temperature):
        # mlx-audio 0.5.3 shares temperature between talker and code predictor.
        if temperature != subtalker_temperature:
            raise ValueError("MLX requires matching talker and subtalker temperatures")
        return dict(temperature=temperature, top_k=50, top_p=1.0,
                    repetition_penalty=1.05, max_tokens=8192, stream=True, streaming_interval=0.32, verbose=False)

    def generate_voice_clone(self, *, text, language, voice_clone_prompt,
                             non_streaming_mode, temperature, subtalker_temperature):
        if not non_streaming_mode:
            raise ValueError("the sidecar returns complete passages")
        import numpy as np
        chunks = list(self.stream_voice_clone(
            text=text, language=language, voice_clone_prompt=voice_clone_prompt,
            temperature=temperature, subtalker_temperature=subtalker_temperature))
        return [np.concatenate([audio for audio, _ in chunks])], chunks[0][1]

    def stream_voice_clone(self, *, text, language, voice_clone_prompt,
                           temperature, subtalker_temperature):
        prompt = voice_clone_prompt
        self.model._icl_cache = prompt.cache
        try:
            # Public generate() forces repetition_penalty >= 1.5. This pinned
            # ICL entry point keeps the same 1.05 behavior as qwen-tts 0.1.1.
            yield from self._chunks(self.model._generate_icl(
                text=text, language=language, ref_audio=prompt.audio, ref_text=prompt.text,
                **self._sampling(temperature, subtalker_temperature)))
        finally:
            self.model._icl_cache = {}
            # Upstream cleanup follows its final yield, so also reset when a
            # consumer closes the generator or decoding raises mid-passage.
            self.model.speech_tokenizer.decoder.reset_streaming_state()


def download_checkpoint(model_id, detail, progress_factory):
    """Use Hub 1.x's public tqdm hook, including resumed aggregate byte counts."""
    from huggingface_hub import snapshot_download
    from tqdm.auto import tqdm

    class Progress(tqdm):
        def __init__(self, *args, **kwargs):
            self.forward = None
            # Hide the terminal bar; byte updates go over the sidecar protocol.
            kwargs["disable"] = True
            super().__init__(*args, **kwargs)
            # Report reconstructed bytes, which include resumed content. The
            # separate transfer bar tracks only network bytes and cannot report
            # an accurate completion fraction for a partially cached model.
            if kwargs.get("unit") == "B" and kwargs.get("desc") != "Downloading bytes":
                self.forward = progress_factory(detail, self.total, self.n)

        def refresh(self, *args, **kwargs):
            if self.forward is not None:
                self.forward.total = int(self.total or 0)
                self.forward.current = int(self.n)
                self.forward.report()

        def update(self, n=1):
            self.n += n or 0
            self.refresh()

        def close(self):
            if self.forward is not None:
                self.refresh()
                self.forward.close()
            super().close()

    return snapshot_download(repo_id=model_id, max_workers=1, tqdm_class=Progress)
