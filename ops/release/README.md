# `ops/release` - cutting a Wavee release

Wavee is released by hand from this machine. There is no CI release job: a CI checkout has no
`src/apps/Wavee.PlayPlay` junction, so it would silently ship the public-only variant.

| File | What it is |
|---|---|
| `wavee-release.ps1` | the orchestrator - 13 phases (0, 1a, 2, 1b, 3-11), a `release-state.json` ledger, `-Resume` / `-Abort` / `-RepointFeed` |
| `Wavee.Release.psm1` | the pure helpers (semver, quad, feed monotonicity, `.appinstaller` substitution, manifest, `gh`) |
| `wavee-store-submit.ps1` | the Microsoft Store submission runbook - run after the feed release from the same tag; packs both `store`-channel MSIX, one `.msixupload`, submits via msstore-cli, a `store-state.json` ledger in `artifacts\store\<semver>\`, `-DryRun` / `-Resume` / `-Abort` / `-Status` |
| `Wavee.Store.psm1` | its pure decisions (store quad, `.msixupload` assembly, release-notes patching, submission-state classification) plus the thin `msstore` wrapper |
| `feed-release-body.md` | the body of the rolling `wavee-stable` feed release - read it before touching that tag |
| `wavee/<semver>/` | hand-authored release notes: `whatsnew.json` plus its `media/` (tagline, highlights, notices) |
| `tests/` | Pester 3.4 tests over the pure helpers (`Wavee.Release.Tests.ps1`, `Wavee.Store.Tests.ps1`), plus `LocalFeedServer.psm1` (the loopback update feed) and `local-update-e2e.ps1` (the local update rehearsal) - `Invoke-Pester ops\release\tests` |

The build side lives next door in `ops/build` (`Wavee.Build.psm1`, `pack-wavee-msix.ps1`,
`Wavee.AppInstaller.template.xml`, `Wavee.AppxManifest.xml`).

## The three hand edits, before you run anything

1. `src/apps/Wavee/Wavee.Version.props` - `<WaveeVersion>` (semver) and `<WaveeCodename>` (per-MINOR, sea/wave
   series: Abyss 0.1, Breaker 0.2, Crest 0.3, ...). **Never** touch `<WaveeBuild>`; the script owns it.
2. `CHANGELOG.md` - a `## [<semver>] - unreleased` section with `### Added` / `Changed` / `Fixed` / `Removed` /
   `Known limitations`. The script replaces `unreleased` with today's UTC date.
3. `ops/release/wavee/<semver>/whatsnew.json` - tagline, up to three hero highlights, notices. Media (`.webp`/`.mp4`,
   no GIFs) goes in `media/` beside it; the release tool enforces the per-file and total size budgets.

**Rehearse the update path first, locally.** `ops\release\tests\local-update-e2e.ps1` (elevated) generates both
releases' notes, packs two versions against a loopback `.appinstaller`, publishes the second into the same feed and
drives the whole update through Windows - no tag, no branch, nothing on GitHub. There are two paths and they race, so
run it twice: `-Scenario inapp` (the default; A installed bare, so the app's own checker and install-on-quit are what
apply the update) and `-Scenario os` (A installed through the feed, so App Installer applies it silently at the next
activation - what most users will experience). It is the primary rehearsal; the GitHub scratch-feed recipe is only for
the release plumbing itself. See `docs/guide/releasing-wavee.md` section 8.

## The one command

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1
```

Rehearse first - this builds and signs everything into `artifacts\release\<semver>-dryrun\` and stops before any
commit, tag or upload, leaving `git status` clean:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 -DryRun
```

Useful switches: `-SkipTests` (skip the build/test/VerticalSlice gate), `-NoUpload` (do everything up to staging,
including the real bump and tag), `-SkipArch x64` or `-X64Msix <path>` when the x64 cross toolchain is unavailable,
`-PublicOnly` for a build without PlayPlay, `-Resume` to finish a run that died, `-Abort` to unwind an un-pushed one,
and `-RepointFeed <older semver> -AllowDowngrade` to roll the feed back.

`-FeedRelease`, `-TagPrefix` and `-Branch` redirect the whole run into a throwaway namespace for the end-to-end
rehearsal; the real feed is untouched.

## Full runbook

**`docs/guide/releasing-wavee.md`** - the phase-by-phase walkthrough, what each failure means, the resume/abort
table, the rollback procedure, and the scratch-feed test recipe. Signing specifics (Azure Trusted Signing, the
`Azure subscription 1` gotcha, publisher mismatch) are in `.claude/skills/releasing/SKILL.md`.
