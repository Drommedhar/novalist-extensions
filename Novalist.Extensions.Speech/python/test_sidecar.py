"""What the sidecar decides, tested without a model on the machine.

The sidecar had no tests at all, and it is where three of the things the host
carefully assembled were quietly dropped: the language, the reading rate and the
direction all arrived and were never read, and nothing anywhere failed. The
model is not testable here - it is gigabytes of weights and a GPU - but every
decision in front of it is, and those are the decisions that went wrong.

The one that matters most is the direction. This engine's model takes its
direction as a phrase in front of the words, which is exactly the arrangement
the SDK warns about: an in-band tag is one bad prompt away from being read out
loud. The tests below pin the two invariants that make it safe - the phrase
comes only from a closed vocabulary, and the prose can never occupy the slot.

    python -m unittest discover -s Novalist.Extensions.Speech/python
"""

from __future__ import annotations

import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import sidecar  # noqa: E402


def segment(vector=None, dialogue=True, text="Get out."):
    return {"key": "k", "text": text, "voiceId": "v",
            "isDialogue": dialogue, "vector": vector or {}}


class TheDirectionNeverEntersTheText(unittest.TestCase):
    """The rule the whole arrangement rests on, pinned at the one seam that
    could break it."""

    def test_the_prefix_is_built_only_from_the_closed_vocabulary(self):
        # Every phrase the adapter can emit is a constant in the module. If a
        # prefix ever contains something that is not, it came from somewhere it
        # should not have.
        allowed = {sidecar.STYLE_NEUTRAL, sidecar.NARRATION_WORDS}
        for quiet, strong in sidecar.STYLE_WORDS.values():
            allowed.add(quiet)
            allowed.add(strong)
        for _, word in sidecar.PACE_WORDS:
            if word:
                allowed.add(word)

        for dialogue in (True, False):
            for name in sidecar.EMOTION_DIMENSIONS:
                for weight in (0.0, 0.2, 0.9):
                    for rate in (0.5, 1.0, 1.8):
                        said = sidecar.style_prefix(
                            segment({name: weight}, dialogue), rate)
                        for part in said.split(", "):
                            self.assertIn(part, allowed)

    def test_the_writers_words_never_reach_the_prefix(self):
        # The segment carries an instruction and an emotion name over the wire
        # for engines that take them. This one does not, and must not read them.
        loud = segment({"angry": 0.9})
        loud["instruction"] = "read this as though you were BETRAYED"
        loud["emotion"] = "BETRAYED"
        loud["text"] = "(whispering) she said, and the word was SECRET."

        said = sidecar.style_prefix(loud, 1.0)

        self.assertNotIn("BETRAYED", said)
        self.assertNotIn("SECRET", said)
        self.assertNotIn("whispering", said)

    def test_an_aside_the_novelist_wrote_is_still_read_aloud(self):
        # A novelist writing an aside in brackets is not directing the actor.
        # Two groups in a row is the model's own syntax for a designed voice, so
        # a prefix alone is not enough - the aside behind it was measured being
        # eaten, a clause of somebody's book gone with nothing saying so. The
        # brackets come off and every word stays.
        said = sidecar.directed(
            "(He had never said so aloud.) She left.", sidecar.style_prefix(segment(), 1.0))

        self.assertTrue(said.startswith("(" + sidecar.STYLE_NEUTRAL + ")"))
        self.assertIn("He had never said so aloud. She left.", said)
        # One group only - the direction's - so nothing after it is an
        # instruction.
        self.assertEqual(1, said.count("("))

    def test_a_bracket_in_the_middle_of_a_sentence_is_left_alone(self):
        # It is never in the instruction slot, so there is nothing to protect it
        # from and no reason to touch the writer's punctuation.
        said = sidecar.unbracket("She left (without a word) and did not return.")
        self.assertEqual("She left (without a word) and did not return.", said)

    def test_only_the_leading_aside_is_unbracketed(self):
        # Once the first one is out of the instruction slot the next is in the
        # middle of a sentence, where it was never at risk. Touching it would be
        # rewriting the writer's punctuation for no reason.
        said = sidecar.unbracket("(One.) (Two.) Three.")
        self.assertEqual("One. (Two.) Three.", said)

    def test_a_bracket_the_writer_never_closed_does_not_eat_the_line(self):
        said = sidecar.unbracket("(She never finished the thought")
        self.assertEqual("She never finished the thought", said)

    def test_a_full_width_bracket_counts_too(self):
        said = sidecar.unbracket("（她没有回头）风把它带走了。")
        self.assertTrue(said.startswith("她没有回头"))
        self.assertNotIn("（", said)

    def test_a_prefix_is_emitted_even_when_there_is_nothing_to_say(self):
        self.assertEqual(sidecar.STYLE_NEUTRAL, sidecar.style_prefix(segment(), 1.0))


class TheDirectionIsActuallyApplied(unittest.TestCase):
    """The other half: a direction that is safe but ignored is the bug this
    engine shipped with."""

    def test_a_strong_emotion_reads_differently_from_a_faint_one(self):
        faint = sidecar.style_prefix(segment({"sad": 0.25}), 1.0)
        strong = sidecar.style_prefix(segment({"sad": 0.9}), 1.0)

        self.assertNotEqual(faint, strong)
        self.assertEqual(sidecar.STYLE_WORDS["sad"][0], faint)
        self.assertEqual(sidecar.STYLE_WORDS["sad"][1], strong)

    def test_the_loudest_dimension_wins(self):
        mixed = {"calm": 0.2, "angry": 0.8, "sad": 0.3}
        self.assertIn(
            sidecar.STYLE_WORDS["angry"][1], sidecar.style_prefix(segment(mixed), 1.0))

    def test_a_vector_that_barely_says_anything_is_read_plainly(self):
        self.assertEqual(
            sidecar.STYLE_NEUTRAL, sidecar.style_prefix(segment({"happy": 0.05}), 1.0))

    def test_narration_is_coloured_rather_than_acted(self):
        line = sidecar.style_prefix(segment({"angry": 0.9}, dialogue=True), 1.0)
        prose = sidecar.style_prefix(segment({"angry": 0.9}, dialogue=False), 1.0)

        self.assertNotIn(sidecar.NARRATION_WORDS, line)
        self.assertIn(sidecar.NARRATION_WORDS, prose)

    def test_the_reading_rate_reaches_the_model(self):
        # The model has no rate argument, so this phrase is the only way a Speed
        # control does anything at all. It was sent and discarded before.
        self.assertIsNone(sidecar.pace_word(1.0))
        self.assertEqual("slightly faster", sidecar.pace_word(1.25))
        self.assertEqual("much faster", sidecar.pace_word(2.0))
        self.assertEqual("slightly slower", sidecar.pace_word(0.8))
        self.assertEqual("much slower", sidecar.pace_word(0.5))

    def test_a_rate_that_is_not_a_number_is_simply_not_a_direction(self):
        for rubbish in (None, "", "fast", 0, -1):
            self.assertIsNone(sidecar.pace_word(rubbish))


class TheDesignedVoiceSpeaksTheBooksLanguage(unittest.TestCase):
    """The accent a reading carries is the accent of the clip it was cloned
    from. An English clip is an English accent on every page."""

    def test_a_book_with_no_lines_yet_is_still_designed_in_its_own_language(self):
        # The narrator falls into this on every book: they have no dialogue of
        # their own to sample, so this sentence is what their voice is made
        # from - and it used to be English whatever the book was.
        self.assertEqual(sidecar.DESIGN_LINES["de"], sidecar.design_line("de-low", []))
        self.assertEqual(sidecar.DESIGN_LINES["zh"], sidecar.design_line("zh-CN", []))
        self.assertEqual(sidecar.DESIGN_LINES["fr"], sidecar.design_line("fr", []))

    def test_a_language_we_have_no_sentence_for_falls_back_rather_than_failing(self):
        self.assertEqual(sidecar.DESIGN_LINES["en"], sidecar.design_line("qq", []))
        self.assertEqual(sidecar.DESIGN_LINES["en"], sidecar.design_line(None, []))

    def test_the_characters_own_words_are_preferred(self):
        # Already in the right language by construction, and closer to how they
        # sound on the page than any sentence of ours.
        self.assertEqual(
            "Du kommst zu spät.", sidecar.design_line("de", ["Du kommst zu spät."]))

    def test_a_blank_sample_line_is_not_a_sample_line(self):
        self.assertEqual(sidecar.DESIGN_LINES["de"], sidecar.design_line("de", ["   "]))

    def test_a_speech_is_trimmed_rather_than_used_whole(self):
        said = sidecar.design_line("en", ["word " * 200])
        self.assertLessEqual(len(said), sidecar.DESIGN_SAMPLE_LIMIT)

    def test_every_sentence_is_written_in_its_own_alphabet(self):
        # A transliterated fallback would be a spelling mistake to the person
        # the voice is being designed for.
        self.assertIn("höre", sidecar.DESIGN_LINES["de"])
        for wrong in ("hoere", "ueber", "fuer", "oeffn", "waehl", "aender", "ss."):
            self.assertNotIn(wrong, sidecar.DESIGN_LINES["de"])
        # Every one of these languages is written in a script of its own, and
        # none of those scripts is Latin. A sentence that came back as ASCII is
        # one somebody transliterated or an encoding ate. 0x0370 is where Greek
        # begins and everything below it here is Latin.
        for code in ("zh", "ja", "ko", "ru", "uk", "ar", "el", "he", "hi", "th"):
            said = sidecar.DESIGN_LINES[code]
            self.assertTrue(any(ord(c) >= 0x0370 for c in said), code)
            self.assertNotIn("?", said)

    def test_a_regional_tag_is_the_same_language(self):
        for tag in ("de", "de-DE", "de_AT", "de-low", "de-guillemet"):
            self.assertEqual("de", sidecar.base_language(tag))


class ALineCanBeToldToSoundLikeAnotherOne(unittest.TestCase):
    """The host built the whole path for this - a list of lines already heard, a
    store, an RPC - and no engine claimed it, so the writer picked a delivery,
    pressed apply, and heard exactly what they heard before."""

    def setUp(self):
        self.work = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.work, ignore_errors=True)

    def _write(self, name):
        with open(os.path.join(self.work, name), "wb") as f:
            f.write(b"RIFF")
        return name

    def test_the_clip_pointed_at_is_what_the_line_is_cloned_from(self):
        self._write("design.wav")
        self._write("like-abc.wav")

        said = sidecar.reference_for(
            self.work, "design.wav", segment(text="Get out.") | {"likeThis": "like-abc.wav"})

        self.assertEqual(os.path.join(self.work, "like-abc.wav"), said)

    def test_a_line_pointing_at_nothing_uses_the_characters_own_voice(self):
        # Which is almost every line in every book.
        self._write("design.wav")

        said = sidecar.reference_for(self.work, "design.wav", segment())

        self.assertEqual(os.path.join(self.work, "design.wav"), said)

    def test_a_clip_that_is_not_on_disk_does_not_cost_the_line(self):
        # A cache emptied between the writer picking the line and the render.
        # The resemblance is lost; the line is not, which is the trade that
        # matters - a hole in the reading sounds like the feature is broken.
        self._write("design.wav")

        said = sidecar.reference_for(
            self.work, "design.wav", segment() | {"likeThis": "gone.wav"})

        self.assertEqual(os.path.join(self.work, "design.wav"), said)


class TheDrawCanBeAskedForAgain(unittest.TestCase):
    """Design is not reproducible, so trying again is how a writer gets a second
    answer - and pinning the number is how they get a first one back."""

    def test_no_seed_means_a_fresh_voice_every_time(self):
        # The seed used to come from the words, so pressing Design again on an
        # unchanged brief returned the identical voice and said nothing about
        # it - which made "try again" do nothing at all.
        draws = {sidecar.seed_for({}) for _ in range(8)}
        self.assertGreater(len(draws), 1)

    def test_a_pinned_number_is_the_number_used(self):
        self.assertEqual(4242, sidecar.seed_for({"seed": 4242}))
        self.assertEqual(0, sidecar.seed_for({"seed": 0}))

    def test_a_negative_seed_is_surprise_me(self):
        draws = {sidecar.seed_for({"seed": -1}) for _ in range(8)}
        self.assertGreater(len(draws), 1)

    def test_a_seed_that_is_not_a_number_is_not_a_seed(self):
        for rubbish in (None, "", "42", 1.5, True):
            said = sidecar.seed_for({"seed": rubbish})
            self.assertIsInstance(said, int)
            self.assertGreaterEqual(said, 0)

    def test_a_seed_too_large_for_the_generator_is_brought_into_range(self):
        # Out of range is a crash rather than a voice, and the number came from
        # a text box.
        self.assertLess(sidecar.seed_for({"seed": 2 ** 40}), 2 ** 31)

    def test_the_same_words_can_still_be_asked_for_deliberately(self):
        # Kept for a writer who wants one particular voice back.
        self.assertEqual(sidecar.seed_from("a wiry voice"), sidecar.seed_from("a wiry voice"))
        self.assertNotEqual(sidecar.seed_from("a wiry voice"), sidecar.seed_from("a low voice"))


class TheBriefCannotBreakOutOfItsBrackets(unittest.TestCase):
    """The one place writer-supplied words legitimately reach a prefix, and so
    the one place they have to be cleaned first."""

    def test_a_bracket_in_the_brief_cannot_close_the_group_early(self):
        # Left alone, everything after the writer's own bracket would land in
        # the text slot and be read out loud.
        said = sidecar.brief_prefix("A low voice (northern) with a rasp")

        self.assertNotIn("(", said)
        self.assertNotIn(")", said)
        self.assertIn("northern", said)

    def test_full_width_brackets_count_too(self):
        said = sidecar.brief_prefix("低沉的嗓音（北方口音）")
        self.assertNotIn("（", said)
        self.assertNotIn("）", said)

    def test_an_empty_brief_still_describes_an_instrument(self):
        for nothing in ("", "   ", None):
            self.assertTrue(sidecar.brief_prefix(nothing).strip())

    def test_the_brief_is_otherwise_left_as_the_writer_wrote_it(self):
        brief = "Age: 47. A dry, low voice with a Yorkshire burr."
        self.assertEqual(brief, sidecar.brief_prefix(brief))


if __name__ == "__main__":
    unittest.main()
