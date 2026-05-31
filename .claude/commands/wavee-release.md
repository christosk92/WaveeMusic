---
description: Run or inspect the WaveeMusic release-train workflow.
argument-hint: publish | status | watch | production -VersionTag vX.Y.Z
---

Use the repo release runner; do not reimplement release logic.

Interpret `$ARGUMENTS` as natural language and run the closest command:

- Publish/cut/drop/milestone/pre-release/alpha/beta/rc:
  `./eng/release.ps1 $ARGUMENTS -Yes`
- Status/check/runs:
  `./eng/release.ps1 status`
- Watch/tail:
  `./eng/release.ps1 watch`
- Production/stable/ship:
  require an explicit `vX.Y.Z` version tag, then run
  `./eng/release.ps1 production -VersionTag vX.Y.Z -Yes`

Safety rules:

- Pre-release branches must be `release/<X.Y.Z>-<label>`.
- Production creates a draft; publishing the draft is still a separate human
  go-live decision.
- If the command is ambiguous, run `./eng/release.ps1 "$ARGUMENTS" -DryRun` and
  ask the user to confirm.
