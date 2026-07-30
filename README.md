# Novalist extensions

Extensions for [Novalist](https://github.com/Drommedhar/novalist-official),
built against the `Novalist.Sdk` source - see [the SDK these need](#the-sdk-these-need).

Everything here is work that the audit deliberately placed **outside** core:
format readers and writers, read-only analysis, networked lookups, and static
site generation. None of it needs to ship inside the app to be available in it,
and most of it is the kind of specialist, occasionally-breaking work that is
better versioned on its own.

The AI features live in their own repository
([novalist-aiassistant](https://github.com/Drommedhar/novalist-aiassistant)),
because AI reaches Novalist only through extension contributions.

## What is in here

These are the ones to add to the gallery.

| Extension | Id | Tag prefix | What it does |
| --- | --- | --- | --- |
| **Formats** | `com.novalist.formats` | `formats-v*` | Exports to HTML, RTF, ODT, plain text, Fountain and FictionBook 2, each marked with the language you write in and carrying your cover where the format can hold one. Imports Scrivener projects, Ulysses sheets, folders of Markdown, and CSV/TSV files. Checks a finished EPUB before you send it. |
| **Insight** | `com.novalist.insight` | `insight-v*` | Read-only reports over the whole manuscript: name drift and consistency, project health (orphan entries, dangling links, unused images), a continuity worklist when a Codex entry changes, a word-frequency concordance, and a pacing curve. |
| **Toolkit** | `com.novalist.toolkit` | `toolkit-v*` | Writing sprints and a Pomodoro timer, a task board over your to-do comments, dictionary and thesaurus lookup on a selected word, and web page capture that keeps the readable text rather than just the title. |
| **Publish** | `com.novalist.publish` | `publish-v*` | Generates a self-contained static website from your wiki, your world bible or your manuscript, for sharing a draft or publishing a series bible. Codex sections keep their Markdown formatting and their links, and the site is written in your language. |

Each row is one release artefact: `com.novalist.<name>.zip`, containing the
assembly, its manifest, and any locale, web or data files.

## Releasing one

Tag it. The tag names the extension and the version, and only that extension is
built and published:

```
git tag formats-v1.0.0
git push origin formats-v1.0.0
```

The workflow stamps the version from the tag into `extension.json`, builds
against the SDK source, runs the whole test suite, and attaches
`com.novalist.formats.zip` to a GitHub release. Adding it to the gallery is a
separate, manual step.

One repository, several extensions, separate versions on purpose: a fix to the
EPUB check has no business bumping the version of the writing timer.

## The SDK these need

Everything here uses SDK surface that was added for it: research items, review
remarks and suggested edits, scene metadata, structural editing, the command
bus, export checks, the file picker, and an export context that carries the
book's language, author, cover and chapter selection rather than just a path
and a title.

**That surface is not on NuGet yet.** The published `Novalist.Sdk` is 11.1.0,
which predates all of it; these need 11.2.0, which reaches NuGet only when the
host that introduced it is released. So every build here - local, CI and
release - goes against the SDK **source**, and both workflows check out
`novalist-official` alongside to get it.

Building against the package fails with `NU1102: Unable to find package
Novalist.Sdk with version (>= 11.2.0)`. That message is deliberate: the version
is pinned rather than a wildcard, because a wildcard quietly resolved to 11.1.0
and turned "not published yet" into thirty missing-type errors across four
projects.

Once the SDK ships, `-p:UseLocalSdk=false` starts working and nothing else has
to change.

## Building locally

Local builds reference the SDK **source** next door, so a change to the SDK is
testable here without a package round trip:

```
d:/git/
  novalist-official/     <- the app and the SDK
  novalist-extension/    <- this repository
```

```
dotnet build
dotnet test
```

A build also copies each extension into your local Novalist extensions folder
(`%APPDATA%/Novalist/Extensions/<Name>` on Windows), so a rebuild is all it takes
to see the change in the running app.

`-p:UseLocalSdk=false` switches to the published package. It will not work
until the SDK ships - see above.

## Adding an extension to this repository

1. A folder named `Novalist.Extensions.<Name>` with a `csproj` that sets
   `<ExtensionFolder>` and nothing else — `Directory.Build.props` and
   `Directory.Build.targets` supply the rest.
2. An `extension.json` whose `id` is `com.novalist.<name>` and whose
   `entryAssembly` is `Novalist.Extensions.<Name>.dll`. CI checks both.
3. A row in the table above, and a case in `.github/workflows/release.yml`.
4. Tests in `tests/Novalist.Extensions.Tests`.

## What these will not do

The SDK does not allow it, and that is deliberate:

- No extension here rewrites prose you wrote. When one wants to change a
  sentence it proposes a **suggested edit** with its name on it, and you take it
  or turn it down.
- Nothing here erases a chapter, a scene or a Codex entry. The strongest verbs
  available are moving a chapter to the trash and archiving a scene, both of
  which you can undo.
- The EPUB check reads your file and reports on it. It never rewrites an export
  you are about to send.

## Licence

MIT, per extension. See each folder's `LICENSE`.
