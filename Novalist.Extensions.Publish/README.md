# Publish

Turns a project into a self-contained static website: a folder of HTML, CSS and
images that opens from disk and uploads anywhere.

This updated build requires Novalist 3.5.4 or later, the next desktop release
with scene and Codex reader-visibility contracts. Hosts through 3.5.3 need an older extension release.

## What it builds

- **The world** — an article per Codex entry, cross-linked the way the Wiki is,
  with images, relationships and appearances.
- **The manuscript** — the book as readable chapters, for a beta-reader link or
  a sample.
- **Both**, with the world reachable from the prose.

Everything is written out as files. There is no server, no build step and no
JavaScript framework: it opens over `file://` and works the same when copied to
a host, a bucket or a static-site service.

## What it does not do

Everyone with the link sees the same site. Inactive scenes, scenes excluded
from export, and Codex entries or sections hidden from readers are omitted.
There are no accounts or per-reader permissions.

Republishing removes obsolete pages recorded in `.novalist-publish.json` and
preserves unrelated files. Keep this manifest with the generated site. For a
site made by an older version without a manifest, choose an empty output folder.
Files are prepared before replacing the previous publication; cancelled writes
leave the previous site intact.

## Installing

Extensions view, Store tab, install. It reads the project and writes to a
folder you choose; it never reaches the network.
