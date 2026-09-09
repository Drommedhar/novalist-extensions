"""Decision tests for the Qwen sidecar; no weights or GPU required."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest
from unittest.mock import MagicMock, patch


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("novalist_speech_sidecar", HERE / "sidecar.py")
assert SPEC is not None and SPEC.loader is not None
sidecar = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = sidecar
SPEC.loader.exec_module(sidecar)


class AppleGpuPrecision(unittest.TestCase):
    def setUp(self):
        runtime = patch.object(sidecar, "use_mlx", return_value=False)
        runtime.start()
        self.addCleanup(runtime.stop)

    def torch(self):
        torch = MagicMock()
        torch.cuda.is_available.return_value = False
        torch.backends.mps.is_available.return_value = True
        torch.float32 = "float32"
        torch.bfloat16 = "bfloat16"
        return torch

    def test_supported_mac_loads_bfloat16_on_mps_with_sdpa(self):
        torch = self.torch()
        with patch.dict(sys.modules, torch=torch), patch.object(sidecar, "emit"):
            engine = sidecar.new_engine()
        self.assertEqual({"device_map": "mps", "dtype": "bfloat16",
                          "attn_implementation": "sdpa"}, sidecar._model_kwargs(engine))
        torch.ones.assert_called_once_with((2, 2), device="mps", dtype="bfloat16")
        torch.ones.return_value.__matmul__.assert_called_once()
        torch.mps.synchronize.assert_called_once()

    def test_older_mac_keeps_float32_if_allocation_or_execution_is_unsupported(self):
        for operation, error in (("allocation", TypeError), ("execution", RuntimeError)):
            with self.subTest(operation=operation):
                torch = self.torch()
                if operation == "allocation":
                    torch.ones.side_effect = error("unsupported dtype")
                else:
                    torch.mps.synchronize.side_effect = error("unsupported operation")
                with patch.dict(sys.modules, torch=torch), patch.object(sidecar, "emit"), \
                        patch.object(sidecar, "note"):
                    engine = sidecar.new_engine()
                self.assertEqual("mps", engine.device)
                self.assertEqual("float32", engine.dtype)

    def test_cpu_and_cuda_do_not_probe_or_use_mps(self):
        for cuda, expected_dtype in ((False, "float32"), (True, "bfloat16")):
            with self.subTest(cuda=cuda):
                torch = self.torch()
                torch.cuda.is_available.return_value = cuda
                torch.backends.mps.is_available.return_value = False
                with patch.dict(sys.modules, torch=torch), patch.object(sidecar, "emit"):
                    engine = sidecar.new_engine()
                self.assertEqual("cuda" if cuda else "cpu", engine.device)
                self.assertEqual(expected_dtype, engine.dtype)
                torch.ones.assert_not_called()

    def test_switching_models_releases_metal_cache_and_stale_clone_prompts(self):
        torch = self.torch()
        engine = sidecar.Engine("mps", "bfloat16", clone_model=object())
        engine.clone_prompts["voice"] = ("fingerprint", object())

        def load_design(*args):
            self.assertIsNone(engine.clone_model)
            self.assertEqual({}, engine.clone_prompts)
            torch.mps.empty_cache.assert_called_once()
            return object()

        with patch.dict(sys.modules, torch=torch), patch.object(sidecar, "gc"), \
                patch.object(sidecar, "_load_checkpoint", side_effect=load_design):
            design = sidecar.ensure_design(engine)
            self.assertIs(design, sidecar.ensure_design(engine))
        torch.cuda.empty_cache.assert_not_called()

        with patch.dict(sys.modules, torch=torch), patch.object(sidecar, "gc"), \
                patch.object(sidecar, "_load_checkpoint", return_value=object()):
            sidecar.ensure_clone(engine)
        self.assertIsNone(engine.design_model)
        self.assertEqual(2, torch.mps.empty_cache.call_count)


class QwenLanguages(unittest.TestCase):
    def test_regional_tags_use_qwens_language_names(self):
        self.assertEqual("German", sidecar.qwen_language("de-DE"))
        self.assertEqual("Chinese", sidecar.qwen_language("zh_CN"))

    def test_an_unsupported_language_is_not_silently_read_as_english(self):
        self.assertIsNone(sidecar.qwen_language("pl"))
        self.assertIsNone(sidecar.qwen_language("sk-SK"))

    def test_every_supported_language_has_a_native_fallback_reference(self):
        for code, language in sidecar.LANGUAGES.items():
            text = sidecar.design_text(code)
            self.assertEqual(sidecar.DESIGN_LINES[language], text)
            self.assertGreater(len(text), 20)
            self.assertGreaterEqual(text.count(".") + text.count("。"), 3)


class TheReferenceTranscriptIsControlled(unittest.TestCase):
    def test_character_dialogue_cannot_bake_its_mood_into_the_reference(self):
        self.assertNotIn("You are late", sidecar.design_text("en"))
        self.assertIn("natural speaking voice", sidecar.design_text("en"))


class TheDesignInstructionDescribesOnlyTheVoice(unittest.TestCase):
    def test_the_approved_acoustic_brief_is_kept(self):
        prompt = sidecar.voice_instruction(
            " low alto, dry timbre, clipped articulation ", "German")
        self.assertIn("low alto, dry timbre, clipped articulation", prompt)
        self.assertIn("native German pronunciation", prompt)
        self.assertIn("natural and neutral", prompt)

    def test_an_empty_brief_gets_an_acoustic_baseline(self):
        prompt = sidecar.voice_instruction("", "English")
        self.assertIn("mid-range pitch", prompt)
        self.assertIn("precise articulation", prompt)


class StableClonePrompt(unittest.TestCase):
    class Model:
        def __init__(self):
            self.calls = 0

        def create_voice_clone_prompt(self, **kwargs):
            self.calls += 1
            return {"call": self.calls, **kwargs}

    def test_audio_and_transcript_are_both_part_of_the_cache_key(self):
        with patch.object(
            sidecar,
            "_reference_fingerprint",
            side_effect=lambda path, text: path + "\0" + text,
        ):
            engine = sidecar.Engine("cpu", None)
            model = self.Model()

            first = sidecar.clone_prompt(engine, model, "mira", "voice-one.wav", "One line.")
            again = sidecar.clone_prompt(engine, model, "mira", "voice-one.wav", "One line.")
            changed_text = sidecar.clone_prompt(
                engine, model, "mira", "voice-one.wav", "Another line.")
            changed_audio = sidecar.clone_prompt(
                engine, model, "mira", "voice-two.wav", "Another line.")

        self.assertIs(first, again)
        self.assertIsNot(again, changed_text)
        self.assertIsNot(changed_text, changed_audio)
        self.assertEqual(3, model.calls)
        self.assertFalse(first["x_vector_only_mode"])
        self.assertEqual("One line.", first["ref_text"])


class ReadingSpeed(unittest.TestCase):
    def test_speed_is_continuous_and_bounded(self):
        self.assertEqual(0.5, sidecar.reading_rate(0.1))
        self.assertEqual(0.87, sidecar.reading_rate(0.87))
        self.assertEqual(2.0, sidecar.reading_rate(3.0))

    def test_invalid_speed_is_normal(self):
        self.assertEqual(1.0, sidecar.reading_rate(None))
        self.assertEqual(1.0, sidecar.reading_rate("fast"))
        self.assertEqual(1.0, sidecar.reading_rate(-1))


class ModelDownloadProgress(unittest.TestCase):
    def test_resumed_bytes_and_completion_are_reported_to_the_host(self):
        with patch.object(sidecar, "emit") as emit:
            progress = sidecar.HubDownloadProgress("voice cloning", 100, 25)
            progress.update(75)

        first = emit.call_args_list[0].kwargs
        last = emit.call_args_list[-1].kwargs
        self.assertEqual("downloading-model", first["step"])
        self.assertEqual(0.25, first["fraction"])
        self.assertIn("voice cloning", first["detail"])
        self.assertEqual(1.0, last["fraction"])

    def test_unknown_size_remains_indeterminate_but_shows_bytes(self):
        with patch.object(sidecar, "emit") as emit:
            sidecar.HubDownloadProgress("voice design", None, 2048)

        report = emit.call_args.kwargs
        self.assertIsNone(report["fraction"])
        self.assertIn("2 KiB", report["detail"])


class TheDrawCanBeAskedForAgain(unittest.TestCase):
    def test_a_pinned_seed_is_used(self):
        self.assertEqual(42, sidecar.seed_for({"seed": 42}))

    def test_an_out_of_range_seed_is_normalized(self):
        self.assertEqual(3, sidecar.seed_for({"seed": 2 ** 31 + 3}))

    def test_boolean_and_negative_values_mean_a_fresh_draw(self):
        for value in (True, -1, None):
            self.assertIn(sidecar.seed_for({"seed": value}), range(2 ** 31))


if __name__ == "__main__":
    unittest.main()
