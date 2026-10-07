# Insight

Reports over the whole manuscript, computed from what you have written and
nothing else. No network, no model, no account.

This updated build requires Novalist 3.5.4 or later, the next desktop release
with complete entity-content and image projections. Hosts through 3.5.3 need an older extension release.

Everything here is deterministic: the same book produces the same report, and
every number can be traced back to the scenes it came from.

## The reports

**Project health** — one page saying what is unfinished: scenes with no
synopsis, chapters with no act, entities pointing at images that are not in the
project, and the other small omissions that only matter all at once.

**Name drift** — names that appear in the prose in more than one form. Catches
a character who is Mira in chapter one and Myra in chapter twelve, and a place
that quietly gains a definite article.

**Continuity worklist** — what to re-read after a Codex entry changes, built
from where that entry actually appears rather than from a search.
Reviews apply to the revision you read. Later changes reopen affected scenes,
and newly added Codex entries join the baseline automatically. Descriptions and
sections are tracked for every built-in and custom type.

**Word frequency** — a concordance over the book, with the common words
excluded, so the words you overuse are visible without reading for them.

**Pacing** — scene length and dialogue proportion across the book as a curve.
A flat stretch in the middle is the thing it is for.

**Statistics** — counts and distributions across chapters, scenes, points of
view and stages.

**Who drops out of the book** — characters with a long absence between
appearances, with the chapters they miss named rather than numbered.

## Where it lives

Its own view in the main area, one tab per report. The continuity worklist stores
its baseline and review marks in the project's extension data.
