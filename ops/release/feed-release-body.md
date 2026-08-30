# Wavee update feed

**This release is not a version of Wavee. It is a fixed address.**

Every installed copy of Wavee has the URL below baked into its package by Windows, and asks it "what is the current
version?" on every launch and roughly every eight hours in the background:

```
https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.arm64.appinstaller
https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.x64.appinstaller
```

Those URLs resolve through this release's **tag**, so the tag is an anchor: it is created once and then never moved,
never renamed and never deleted. Delete it and every installed client stops receiving updates permanently - there is
no way to re-point clients that are already out there, because the address they hold no longer exists.

## What actually changes here

`ops/release/wavee-release.ps1` runs `gh release upload --clobber` against this release as the **last** step of every
release, replacing three assets in place:

| Asset | What it is |
|---|---|
| `Wavee.arm64.appinstaller` | root `Version` = the new quad; `MainPackage/@Uri` = the new `wavee-v*` release's arm64 `.msix` |
| `Wavee.x64.appinstaller` | the same, for x64 |
| `whatsnew-index.json` | the newest-first index of the last 12 releases, so the app can name a version it has not downloaded yet |

The `.msix` packages themselves are **not** here. They live on the per-version release (`wavee-v0.2.0`, ...), which is
immutable; this release only points at them. That split is what makes a rollback possible without republishing bytes.

## Rules

- **Never delete or re-tag this release.** See above. If it is ever destroyed, every existing install is orphaned.
- **The feed is updated last.** A release is not visible to users until this step runs, so the script publishes the
  version release, verifies it, and only then repoints the feed.
- **The feed may go backwards, deliberately.** The `.appinstaller` sets `ForceUpdateFromAnyVersion="true"`, so pointing
  this feed at an older release is the one lever that reaches already-installed clients. That is the rollback path:
  `wavee-release.ps1 -RepointFeed <older semver> -AllowDowngrade`.
- **The feed must not go backwards by accident.** Every normal run passes a monotonic gate: the new quad must be
  strictly greater than the current root `Version` here, on every architecture, on every feed being repointed.
- **`releases/latest` is not the feed.** That endpoint is repo-global and belongs to the FluentGpu gallery, which
  versions independently under `v*`. Wavee never links to it.

## If something looks wrong

Read the current head of the feed:

```powershell
Import-Module ops\release\Wavee.Release.psm1
Get-WaveeFeedVersion christosk92/WaveeMusic wavee-stable arm64
```

The full runbook - cutting a release, `-DryRun`, resuming a failed run, aborting an un-pushed one, and rolling back -
is `docs/guide/releasing-wavee.md`.
