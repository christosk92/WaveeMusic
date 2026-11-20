# Contributing to Wavee

Wavee is an independent Spotify desktop client for Windows 11, built on the FluentGpu engine. This file covers
the app repository (`WaveeMusic`). Engine contributions go to
[`fluent-gpu`](https://github.com/christosk92/fluent-gpu).

## Before you start

You need Windows 11 and a Spotify Premium account to run and test the app. The build itself needs .NET SDK 10+
(`global.json` rolls forward) and Visual Studio Build Tools with MSVC (NativeAOT link).

The engine is a **sibling checkout**, not a package — clone it next to this repo:

```
C:\wavee\fluent-gpu
C:\wavee\WaveeMusic
```

`Directory.Build.props` resolves `$(EngineRoot)` from that relative path; the build fails with one clear
sentence if it isn't there. See the README's [Build](README.md#build) section for the exact clone and build
commands.

Once you've cloned, install the pre-commit hook:

```powershell
git config core.hooksPath .githooks
```

This guards a private, out-of-scope path (the Spotify playback derivation) from accidentally entering the
public tree. You don't need access to that private repo to contribute: a checkout without its junction builds
the **public-only variant**, and that's the normal, supported way to work on everything except that one module.

## Where things go

This repository holds the app. Rendering, text layout, input, window chrome, or other engine-level bugs and
features belong in [`fluent-gpu`](https://github.com/christosk92/fluent-gpu) instead — check there first if a
bug reproduces outside of Spotify-specific behavior.

- **Questions** — Discussions, Q&A category.
- **Ideas / proposals** — Discussions, Ideas category.
- **Bugs, crashes, feature requests** — the issue forms under `.github/ISSUE_TEMPLATE`
  (`bug_report.yml`, `crash_report.yml`, `feature_request.yml`).

## How issues are triaged

Issues carry a small label scheme:

| Group | Meaning | Example values |
|---|---|---|
| `type:` (exactly one) | what kind of issue this is | `bug`, `crash`, `regression`, `feature`, `enhancement`, `perf`, `docs`, `question`, `chore` |
| `area:` (zero or more) | which part of the app | mirrors `src/apps/Wavee/Features/*` (e.g. `area: player`, `area: library`, `area: home`, `area: sidebar`, `area: video`) |
| `arch:` / `install:` | when relevant | e.g. `arch: arm64`, `install: store` |
| priority | severity/urgency | `P0`–`P3` |
| `status:` (lifecycle) | where it is in triage | `needs-triage` → `needs-info` \| `needs-repro` → `confirmed` → (`blocked` \| `upstream`) |
| `resolution:` | set on close | why it was closed |

Milestones are one per minor codename (`0.2.x Breaker`, `0.3 Crest`, `0.4 Drift`, `Future`); a patch release
inherits its minor's milestone. The public project board "Wavee" tracks status across columns: Triage,
Backlog, Planned, In progress, In review, Done.

## Branches and commits

`main` is what shipped: what users can install from the feed or the Store. Nobody pushes to it and no PR targets
it. All work goes into the **open release branch**, the newest `release/*` branch (today `release/0.3.12`):

```
git fetch origin
git switch -c fix/<name> origin/release/0.3.12
...
gh pr create --base release/0.3.12        # GitHub suggests main: pick the release branch
```

A PR opened against `main` is not lost, it waits until a maintainer moves it. When a release ships while your PR is
open, the PR is moved to the next release branch for you. The full flow (spikes, releases, hotfixes, rollback) is in
`docs/plans/wavee/release-flow-design.md`; agents read `.claude/skills/wavee-contributing`.

Prefix the branch by kind, matching the repo's history:

- `feat/…` — new functionality
- `fix/…` — bug fixes
- `perf/…` — performance work
- `docs/…` — documentation only
- `chore/…` — everything else (tooling, deps, cleanup)
- `spike/…` — an experiment that answers a question; it is never merged (a draft PR at most)

Commit subjects follow `area: summary`, imperative mood, e.g.:

```
audio: adaptive read-ahead, background disk-cache writes, xrun logging
docs(readme): bring back the download buttons
```

Put the *why* in the body, not just the *what*. Keep PRs to one logical change — small, reviewable diffs are
much more likely to land quickly than a large mixed one.

Every fix references its issue: the commit body carries `Fixes #n`, and the change file lists the same number in
`issues:` (→ `github-triage` skill).

## Changelog

Don't edit `CHANGELOG.md`. If your change is user-facing, add one **change file** to `changes/unreleased/`; the
format is in [`changes/README.md`](changes/README.md). Write its text the way the existing CHANGELOG entries read: a
bold, user-facing lead phrase followed by plain-language detail, not an engineering changelog. At release time the
files are assembled into the version's section of `CHANGELOG.md` (which follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/)), and a community change is credited there and on the What's
new page from the file's `by:` field.

If your change touches the theme contract (`src/apps/Wavee/Theming/Contracts/`), say so in the change file
(`contract: additive` or `breaking`) and leave `ContractVersion.cs` alone: the release sets the number.

## Gates before you open a PR

Run, at minimum:

```powershell
dotnet build Wavee.slnx                                   # Debug
```

The Debug build needs to build clean (`TreatWarningsAsErrors`). For UI changes, render and trace them in the headless
inspector (`docs/guide/agent-inspect.md`) and attach screenshots.

A few rules the codebase enforces strictly:

- **No tests.** Test projects and test files are not accepted; the build and the pre-commit hook reject them.
- **No environment-variable switches** for behavior or verification — logs are always-on, gated by the
  diagnostics page, not env vars.
- **No legacy paths kept alongside new ones.** Replace outright and delete the old code; breaking changes are
  acceptable when the replacement is complete.
- **Component props freeze at mount.** `Embed.Comp(() => new T { Field = value })` runs once — changing data
  must reach a child via a `Signal`/`Func`, `Ctx.Provide` + `UseContext`, or a remount through `Key`. See
  `..\fluent-gpu\docs\design\subsystems\component-props-contract.md`.
- Never leave a real `%LOCALAPPDATA%\Wavee` around when testing a packaged build — an unpackaged `dotnet run`
  creates one; wipe it before and after packaged-build testing.

## What we don't accept

- Anything touching the private PlayPlay paths (`src/apps/Wavee.PlayPlay`, `src/apps/.native`,
  `private-runtimes`, and related tooling). That derivation is intentionally kept out of this repo — see
  `README.md` and the pre-commit hook in `.githooks/`.
- CI workflows that build the app. There is no CI here by design — releases are cut locally via
  `ops/release/release.ps1`. If you want automation, discuss it first.
- Large drive-by reformatting mixed into a functional change. Keep formatting and behavior changes in
  separate PRs.

## Releases

Releases are cut and published by maintainers using the local runbook in
[`docs/guide/releasing-wavee.md`](docs/guide/releasing-wavee.md); contributors don't need to run it.
