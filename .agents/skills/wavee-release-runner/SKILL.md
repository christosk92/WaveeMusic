---
name: wavee-release-runner
description: Use when Codex is asked to publish, cut, run, watch, or inspect a WaveeMusic release using the release-train flow, including natural language such as "publish the release", "cut a new alpha", "run the release workflow", "watch the release", or "ship vX.Y.Z".
---

# Wavee Release Runner

Use the repo script `eng/release.ps1`. Do not hand-roll `gh workflow run`
commands unless the script is broken.

## Commands

From the repo root:

```powershell
./eng/release.ps1 publish -Yes
./eng/release.ps1 "cut milestone on release/0.1.0-alpha" -Yes
./eng/release.ps1 production -VersionTag v0.1.0 -Yes
./eng/release.ps1 status
./eng/release.ps1 watch
```

## Rules

- A pre-release publishes to testers. Only pass `-Yes` when the user clearly
  asked to run/publish/cut the drop now. Otherwise use `-DryRun` or explain the
  command.
- Pre-release refs must be `release/<X.Y.Z>-<label>`, for example
  `release/0.1.0-alpha`. If the user gives `release/0.10-alpha`, ask or correct
  to full semver such as `release/0.10.0-alpha`.
- Production releases require `-VersionTag v<X.Y.Z>` and are created as drafts.
- Use `status` or `watch` after queueing a workflow.
- The canonical flow remains `.agents/guides/contributing-and-releases.md`.
