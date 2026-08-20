"""Decision tests for the Qwen sidecar; no weights or GPU required."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest
from unittest.mock import patch


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("novalist_speech_sidecar", HERE / "sidecar.py")
assert SPEC is not None and SPEC.loader is not None
sidecar = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = sidecar
SPEC.loader.exec_module(sidecar)


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
