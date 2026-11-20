# `ops/release` - cutting a Wavee release

Wavee is released by hand from this machine. There is no CI release job: a CI checkout has no
`src/apps/Wavee.PlayPlay` junction, so it would silently ship the public-only variant. Releases are cut from the open
`release/<version>` branch (`main` is protected: it is what shipped, and only the runner moves it). The flow from idea
to release is `docs/plans/wavee/release-flow-design.md`; agents read `.claude/skills/wavee-contributing`.

| File | What it is |
|---|---|
| `release.ps1` | the entry point: parameters, the lock, the exit code, the end-of-run toast |
| `ReleaseRunner.psm1` | the runner: state, preflight, the steps and their "done when" checks, `-Hotfix`, `-RetargetPrs`, `-RepointFeed`, `-Abort` |
| `Wavee.Release.psm1` | the pure helpers (semver, quad, feed monotonicity, `.appinstaller` substitution, manifest, `gh`, wrangler uploads with retries) |
| `wavee-store-submit.ps1` | the runner's Store engine (steps `store-submit` and `store-poll` call it; nobody runs it by hand): one dual-architecture SDK bundle inside the upload, explicit package retirement, schema-2 hash/ownership ledger |
| `Wavee.Store.psm1` | Store quad, nested bundle and executable provenance verification, upload assembly, release notes, status parsing and redacted CLI wrapper |
| `Wavee.StoreSubmission.psm1` | Pure desired-package reconciliation, draft/ingested inventory checks and artifact-hash validation |
| `feed-release-body.md` | the body of the rolling `wavee-stable` feed release - read it before touching that tag |
| `wavee/<semver>/` | hand-authored release notes: `whatsnew.json` plus its `media/` (tagline, highlights, notices) |
| `tests/` | `LocalFeedServer.psm1` (the loopback update feed) and `local-update-e2e.ps1` (the local update rehearsal) |

The build side lives next door in `ops/build` (`Wavee.Build.psm1`, `pack-wavee-msix.ps1`,
`Wavee.AppInstaller.template.xml`, `Wavee.AppxManifest.xml`).

## What you author

Only `ops/release/wavee/<semver>/whatsnew.json` (tagline, up to three hero highlights, notices; a hotfix needs no
highlight) and its `media/`, merged into the release branch. The version props, the CHANGELOG section (assembled from
the change files in `changes/unreleased/`), the contract version and the build counter are the runner's.

**Rehearse the update path first, locally.** `ops\release\tests\local-update-e2e.ps1` (elevated) packs two versions
against a loopback `.appinstaller` and drives the whole update through Windows - no tag, no branch, nothing on
GitHub. Run it twice: `-Scenario inapp` and `-Scenario os`. See `docs/guide/releasing-wavee.md` section 8.

## The commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12              # dry run: preflight + every step's commands
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go -To changelog   # assemble the change files; curate; commit
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go -PauseBefore feed
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go               # continue: done steps are skipped
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Status
```

Also: `-Codename <name>` (a new minor), `-Hotfix <version>`, `-RetargetPrs`, `-RepointFeed <older semver>
-AllowDowngrade`, `-Abort` (each a dry run until `-Go` is added), `-From`/`-To`/`-Only`/`-PauseBefore <step>`,
`-SkipArch x64` or `-X64Msix <path>`, `-PublicOnly`, `-PollMinutes <n>`. State lives in
`%LOCALAPPDATA%\WaveeRelease\<version>\`; a `-Go` works in its own worktree `C:\WAVEE\release\<version>\`.

## Full runbook

**`docs/guide/releasing-wavee.md`** - the steps, what each failure means, hotfixes, the rollback procedure. Signing
specifics (Azure Trusted Signing, the `Azure subscription 1` gotcha, publisher mismatch) are in
`.claude/skills/releasing/SKILL.md`.
