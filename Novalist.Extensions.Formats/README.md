# Formats

More ways for a manuscript to get in and out of Novalist.

This updated build requires Novalist 3.5.4 or later, the next desktop release
with the export visibility contract. Hosts through 3.5.3 need an older extension release.

Novalist ships eight export formats and reads seven kinds of manuscript file.
This adds the ones that are useful to fewer people and would not earn their
place in the installer: screenplay and ebook interchange on the way out, other
writing tools on the way in.

## Exporting

| Format | For |
| --- | --- |
| HTML | A single styled page, for the web or for pasting somewhere. |
| RTF | The interchange format most word processors still read. |
| ODT | OpenDocument, for LibreOffice and anything that reads it. |
| Plain text | The manuscript with no markup at all. |
| Fountain | Screenplay markup, for a script tool that reads it. |
| FictionBook | FB2, widely read by ebook readers outside the English market. |

## Importing

| Source | What comes across |
| --- | --- |
| Scrivener | Manuscript-root prose and nested chapter folders; research and trash stay out. |
| Ulysses | Sheets and groups as scenes and chapters. |
| Markdown folder | Nested directories of `.md` files, one per scene, in name order. |
| Delimited files | CSV or TSV with quoted multiline prose preserved in one scene. Malformed quoting is reported before any scenes are imported. |

## Checking an EPUB

The **EPUB preflight** reads a finished EPUB back and reports what a retailer
would reject: a missing cover manifest entry, a spine that does not match the
table of contents, unreferenced files, and metadata a store requires. It reads
the file you are about to upload rather than the project it came from, so it
catches problems introduced by the export itself.

## Installing

Extensions view, Store tab, install. Nothing here needs configuration and
nothing here reaches the network.
