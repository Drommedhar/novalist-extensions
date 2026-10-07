"""MLX routing, adapter and download decisions without weights or GPU packages."""
import contextlib
import io
import json
import math
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import sidecar
import mlx_backend


class MacRouting(unittest.TestCase):
    def test_host_can_keep_torch_with_a_native_python_under_rosetta(self):
        with patch.dict(sidecar.os.environ, {"NOVALIST_TTS_BACKEND": "torch"}), \
                patch.object(sys, "platform", "darwin"), \
                patch.object(sidecar.platform, "machine", return_value="arm64"):
            self.assertFalse(sidecar.use_mlx())

    def test_only_native_apple_silicon_selects_mlx(self):
        for os_name, cpu, expected in (("darwin", "arm64", True), ("darwin", "aarch64", True),
                                       ("darwin", "x86_64", False), ("linux", "arm64", False),
                                       ("win32", "arm64", False), ("win32", "AMD64", False)):
            with self.subTest(os=os_name, cpu=cpu), patch.object(sys, "platform", os_name), \
                    patch.object(sidecar.platform, "machine", return_value=cpu):
                self.assertEqual(expected, sidecar.use_mlx())

    def test_mlx_loading_seeding_and_release_never_import_torch(self):
        mx = MagicMock(bfloat16="bfloat16")
        modules = {"mlx": SimpleNamespace(core=mx), "mlx.core": mx, "torch": None, "qwen_tts": None}
        with patch.dict(sys.modules, modules), patch.object(sidecar, "use_mlx", return_value=True), \
                patch.object(sidecar, "emit"), patch.object(sidecar, "download_checkpoint", return_value="snapshot") as download, \
                patch.object(mlx_backend.MlxQwenModel, "from_pretrained", return_value=object()) as load:
            engine = sidecar.new_engine()
            engine.allow_download = True
            self.assertEqual("mlx", engine.device)
            self.assertEqual("bfloat16", engine.dtype)
            sidecar._load_checkpoint(engine, "model", "cloning")
            sidecar._load_checkpoint(engine, "model", "cloning")
            download.assert_called_once_with("model", "cloning", mlx=True)
            self.assertEqual(2, load.call_count)
            sidecar.seed_engine(engine, 42)
            mx.random.seed.assert_called_once_with(42)
            engine.clone_prompts["voice"] = ("hash", object())
            sidecar._release(engine, "clone")
            self.assertEqual({}, engine.clone_prompts)
            mx.synchronize.assert_called_once()
            mx.clear_cache.assert_called_once()

    def test_progress_stays_on_protocol_stdout_while_model_output_is_redirected(self):
        protocol, diagnostic = io.StringIO(), io.StringIO()
        with patch.object(sidecar, "PROTOCOL_STDOUT", protocol), contextlib.redirect_stdout(diagnostic):
            print("model chatter")
            sidecar.emit(type="progress", step="loading-model")
        self.assertEqual("model chatter\n", diagnostic.getvalue())
        self.assertEqual("progress", json.loads(protocol.getvalue())["type"])


class Audio(list):
    @property
    def size(self):
        return len(self)

    def astype(self, dtype):
        return self


class MlxAdapter(unittest.TestCase):
    def setUp(self):
        self.mx = MagicMock(float32="float32")
        numpy = SimpleNamespace(
            array=lambda audio: Audio(audio),
            isfinite=lambda audio: SimpleNamespace(all=lambda: all(math.isfinite(v) for v in audio)),
            concatenate=lambda chunks: Audio(v for chunk in chunks for v in chunk),
        )
        modules = patch.dict(sys.modules, {"mlx": SimpleNamespace(core=self.mx), "mlx.core": self.mx,
                                          "numpy": numpy, "torch": None})
        modules.start()
        self.addCleanup(modules.stop)

    def result(self, audio=(0.1, -0.1), sr=24000):
        return SimpleNamespace(audio=Audio(audio), sample_rate=sr)

    def test_design_preserves_sampling_and_collects_valid_audio(self):
        native = MagicMock()
        native.generate_voice_design.return_value = iter([self.result(), self.result()])
        model = mlx_backend.MlxQwenModel(native)
        wavs, sr = model.generate_voice_design(text="test", language="German", instruct="alto",
                                               temperature=0.9, subtalker_temperature=0.9)
        self.assertEqual([[0.1, -0.1, 0.1, -0.1]], wavs)
        self.assertEqual(24000, sr)
        settings = native.generate_voice_design.call_args.kwargs
        self.assertEqual(1.05, settings["repetition_penalty"])
        self.assertEqual(8192, settings["max_tokens"])
        self.assertTrue(settings["stream"])
        self.assertEqual(0.32, settings["streaming_interval"])

    def test_voice_caches_are_separate_and_invalidated_by_the_shared_sidecar(self):
        native = SimpleNamespace(_icl_cache={}, speech_tokenizer=MagicMock())
        seen = []

        def generate(**kwargs):
            seen.append((kwargs, native._icl_cache))
            native._icl_cache["encoded"] = kwargs["ref_text"]
            yield self.result()

        native._generate_icl = generate
        model = mlx_backend.MlxQwenModel(native)
        first = mlx_backend.ClonePrompt(Audio([0.1]), "One.")
        second = mlx_backend.ClonePrompt(Audio([0.2]), "Two.")
        for prompt in (first, second, first):
            model.generate_voice_clone(text="test", language="German", voice_clone_prompt=prompt,
                                       non_streaming_mode=True, temperature=0.9, subtalker_temperature=0.9)
            self.assertEqual({}, native._icl_cache)
        self.assertIs(seen[0][1], seen[2][1])
        self.assertIsNot(seen[0][1], seen[1][1])
        self.assertEqual("One.", first.cache["encoded"])
        self.assertEqual("Two.", second.cache["encoded"])
        self.assertEqual(1.05, seen[0][0]["repetition_penalty"])

    def test_cache_is_detached_even_when_generation_fails(self):
        native = MagicMock()
        native._generate_icl.side_effect = RuntimeError("failure")
        model = mlx_backend.MlxQwenModel(native)
        with self.assertRaises(RuntimeError):
            model.generate_voice_clone(text="test", language="German",
                voice_clone_prompt=mlx_backend.ClonePrompt(Audio([1]), "One."),
                non_streaming_mode=True, temperature=0.9, subtalker_temperature=0.9)
        self.assertEqual({}, native._icl_cache)

    def test_stream_yields_before_generation_finishes_and_close_clears_state(self):
        native = SimpleNamespace(_icl_cache={}, speech_tokenizer=MagicMock())
        reached = []
        def generate(**kwargs):
            self.assertTrue(kwargs["stream"])
            reached.append("first")
            yield self.result()
            reached.append("second")
            yield self.result()
        native._generate_icl = generate
        model = mlx_backend.MlxQwenModel(native)
        results = model.stream_voice_clone(text="test", language="German",
            voice_clone_prompt=mlx_backend.ClonePrompt(Audio([1]), "One."),
            temperature=0.9, subtalker_temperature=0.9)
        audio, sr = next(results)
        self.assertEqual(["first"], reached)
        self.assertEqual(24000, sr)
        results.close()
        self.assertEqual({}, native._icl_cache)
        native.speech_tokenizer.decoder.reset_streaming_state.assert_called_once()

    def test_invalid_empty_or_mixed_rate_audio_is_rejected(self):
        for results in ([], [self.result(())], [self.result((float("nan"),))],
                        [self.result(), self.result(sr=16000)]):
            with self.subTest(results=results), self.assertRaises(RuntimeError):
                mlx_backend.MlxQwenModel._audio(iter(results))

    def test_partial_model_is_not_reported_as_ready(self):
        loaded = SimpleNamespace(tokenizer=None, speech_tokenizer=object())
        loader = MagicMock(return_value=loaded)
        with patch.dict(sys.modules, {"mlx_audio.tts.utils": SimpleNamespace(load_model=loader)}):
            with self.assertRaises(RuntimeError):
                mlx_backend.MlxQwenModel.from_pretrained("cached")


class StreamingProtocol(unittest.TestCase):
    def test_preview_precedes_complete_clip_and_nondefault_speed_stays_buffered(self):
        for rate, chunks_expected in ((1.0, 2), (0.9, 0)):
            events = []
            model = MagicMock()
            def generate(**kwargs):
                yield Audio([0.1]), 24000
                if rate == 1.0:
                    self.assertEqual(["chunk"], [e["type"] for e in events])
                yield Audio([0.2]), 24000
            model.stream_voice_clone.side_effect = generate
            numpy = SimpleNamespace(concatenate=lambda chunks: Audio(v for c in chunks for v in c))
            with patch.dict(sys.modules, {"numpy": numpy}), \
                    patch.object(sidecar, "ensure_clone", return_value=model), \
                    patch.object(sidecar, "clone_prompt", return_value=object()), \
                    patch.object(sidecar, "write_wav", return_value=320), \
                    patch.object(sidecar, "stretch", side_effect=lambda audio, rate: audio) as stretch, \
                    patch.object(sidecar, "emit", side_effect=lambda **kw: events.append(kw)):
                sidecar.do_render(sidecar.Engine("mlx", "bf16"), "/work", dict(
                    stream=True, rate=rate, language="de", voices={"v": "voice.wav"},
                    voiceTexts={"v": "Hello."}, segments=[dict(key="one", voiceId="v", text="Text.")]))
                self.assertEqual(["chunk"] * chunks_expected + ["clip", "done"], [e["type"] for e in events])
                stretch.assert_called_once_with([0.1, 0.2], rate)

    def test_midstream_failure_never_emits_a_complete_clip(self):
        events = []
        model = MagicMock()
        def generate(**kwargs):
            yield Audio([0.1]), 24000
            raise RuntimeError("decoder failure")
        model.stream_voice_clone.side_effect = generate
        with patch.dict(sys.modules, {"numpy": SimpleNamespace()}), \
                patch.object(sidecar, "ensure_clone", return_value=model), \
                patch.object(sidecar, "clone_prompt", return_value=object()), \
                patch.object(sidecar, "write_wav", return_value=320), patch.object(sidecar, "note"), \
                patch.object(sidecar, "emit", side_effect=lambda **kw: events.append(kw)):
            sidecar.do_render(sidecar.Engine("mlx", "bf16"), "/work", dict(
                stream=True, language="de", voices={"v": "voice.wav"}, voiceTexts={"v": "Hello."},
                segments=[dict(key="one", voiceId="v", text="Text.")]))
        self.assertEqual(["chunk", "error", "done"], [e["type"] for e in events])


class MlxDownload(unittest.TestCase):
    def test_resumed_aggregate_bytes_are_forwarded_through_public_hub_hook(self):
        class Bar:
            def __init__(self, *args, total=0, initial=0, **kwargs):
                self.total, self.n = total, initial

            def close(self):
                pass

        def snapshot_download(**kwargs):
            self.assertEqual(1, kwargs["max_workers"])
            bar = kwargs["tqdm_class"](unit="B", desc="Reconstructing", total=100, initial=25)
            bar.update(75)
            bar.close()
            return "snapshot"

        with patch.dict(sys.modules, {
            "huggingface_hub": SimpleNamespace(snapshot_download=snapshot_download),
            "tqdm.auto": SimpleNamespace(tqdm=Bar),
        }), patch.object(sidecar, "emit") as emit:
            result = mlx_backend.download_checkpoint("model", "cloning", sidecar.HubDownloadProgress)
        self.assertEqual("snapshot", result)
        self.assertEqual(0.25, emit.call_args_list[0].kwargs["fraction"])
        self.assertEqual(1.0, emit.call_args.kwargs["fraction"])


if __name__ == "__main__":
    unittest.main()
