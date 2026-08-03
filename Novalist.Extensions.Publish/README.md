# Publish

Turns a project into a self-contained static website: a folder of HTML, CSS and
images that opens from disk and uploads anywhere.

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

There is no audience scoping and no spoiler control. Everything selected is
published to everyone who has the link, and the generator says so before it
writes. If you need a reader to see chapter three and not chapter nine, publish
two sites.

## Installing

Extensions view, Store tab, install. It reads the project and writes to a
folder you choose; it never reaches the network.
