# Toolkit

The small tools that do not belong in the app itself, in one view.

This build requires Novalist 3.6.0 or later and uses SDK 15 / .NET 10.
Earlier hosts need an older compatible extension release.

## Sprints

A timer with a rest interval and history. It records the net word-count gain
during the sprint. Switching project, book or draft ends the sprint before the
new manuscript's total can affect its result.

## The board

Every to-do comment in the book on one board, grouped by chapter. Comments are
made in the editor; this is where they get worked through. Clicking one opens
the scene at the comment.

## Dictionary and thesaurus

Open a scene in the scene editor, select one word, then right-click and choose
**Look up → Define** or **Look up → Synonyms**. From Manuscript view, click the
scene's title to open the scene editor; Manuscript view does not have this menu.

Definitions leave your prose unchanged. Synonyms are offered as suggested edits
for you to review. German and English lookups use the project's writing language
(**Settings → Writing → Quote Style**), independently of the interface language.
Definitions and synonyms come directly from [Wiktionary](https://www.wiktionary.org/)
and show a link to their source entry and its attribution/license information.
An internet connection is required; no API key is needed. German nouns retain
their capitalization, so **Prolog** is looked up as a German word in a German project.

While a lookup runs, the editor shows its name and a progress bar with a
**Cancel** button. If the dictionary does not respond within 15 seconds, the
result explains that the lookup timed out; check your connection and try again.

## Names

An offline name generator with per-culture data. It types the name into the
field you were filling in rather than making you copy it.

## Capture

Paste a web address and keep the readable text of the page as a research note -
the article without the navigation, the cookie banner and the advertising. The
page is fetched once, when you ask.

## Installing

Extensions view, Store tab, install.
