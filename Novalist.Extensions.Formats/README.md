# Formats

More ways for a manuscript to get in and out of Novalist.

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
| Scrivener | Handled by Novalist itself; this adds the older layouts and edge cases. |
| Ulysses | Sheets and groups as scenes and chapters. |
| Markdown folder | A directory of `.md` files, one per scene, in name order. |
| Delimited files | CSV or TSV where a column names the chapter and a column holds the prose. |

## Checking an EPUB

The **EPUB preflight** reads a finished EPUB back and reports what a retailer
would reject: a missing cover manifest entry, a spine that does not match the
table of contents, unreferenced files, and metadata a store requires. It reads
the file you are about to upload rather than the project it came from, so it
catches problems introduced by the export itself.

## Installing

Extensions view, Store tab, install. Nothing here needs configuration and
nothing here reaches the network.
