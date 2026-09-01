---
name: github-triage
description: Use when touching anything on the GitHub side of christosk92/WaveeMusic — labels, milestones, the "Wavee" project board (Projects v2), issue forms / PR template, CONTRIBUTING / SECURITY, repo settings, Discussions, or triaging an issue or PR with `gh`. Every modifying `gh` call needs the user's explicit approval first.
---

# GitHub triage for Wavee (labels · milestones · project · templates)

**Rule zero — always ask for manual approval.** Before *any* `gh` (or `gh api`) call that changes something on
GitHub — labels, milestones, issue/PR edits, comments, project items, repo settings, pushes, PR creation — stop
and ask with `AskUserQuestion`, listing the exact commands. Batch them into one question, run only what was
approved, then report what changed. Read-only calls (`gh issue list`, `gh label list`, `gh project view`,
`gh repo view`, `gh api GET …`) need no approval. Never run `gh auth refresh` / `gh auth login` yourself: it is a
device-code flow — tell the user the command (`! gh auth refresh -s project,read:project`) and wait.

There is **no CI**: everything below is applied locally with `gh` from the dev box, exactly like releases.

## What exists (set up 2026-09-01)

| Thing | Where | Notes |
|---|---|---|
| Label set (53) | `ops/github/labels.json` → applied by `ops/github/sync-labels.ps1` | source of truth; idempotent; never deletes; renames the 7 stock labels in place |
| Label test | `ops/release/tests/GitHub.Labels.Tests.ps1` | pure Pester 3.4 syntax (`Should Be`); runs with `Invoke-Pester -Path ops/release/tests` |
| Milestones | GitHub | `0.2.x Breaker` (#1), `0.3 Crest` (#2), `0.4 Drift` (#3), `Future` (#4) — **one per minor codename**; patches inherit the minor's milestone |
| Project board | https://github.com/users/christosk92/projects/3 (number **3**, public, linked to the repo) | id `PVT_kwHOAM0O7s4BiKKu`; Status field `PVTSSF_lAHOAM0O7s4BiKKuzhhDNNI` (Triage · Backlog · Planned · In progress · In review · Done); Priority field `PVTSSF_lAHOAM0O7s4BiKKuzhhDNN8` (P0–P3) |
| Issue forms | `.github/ISSUE_TEMPLATE/{bug_report,crash_report,feature_request}.yml` + `config.yml` | blank issues off; contact links → Discussions Q&A / Ideas, fluent-gpu issues, Store listing |
| PR template | `.github/PULL_REQUEST_TEMPLATE.md` | the Debug+Release / tests / changelog / no-source-text-tests gates |
| Contributor docs | `CONTRIBUTING.md`, `SECURITY.md` | label scheme, branch/commit conventions, private vulnerability reporting |
| Repo settings | GitHub | Discussions **on** (default categories), wiki **off**, delete-branch-on-merge **on**, private vulnerability reporting **on**, homepage = Store listing, topics set |
| Token scopes | `gh auth status` | `repo, read:org, gist, project` — `project` is required for anything under `gh project` |

## Label scheme (prefix + space, e.g. `type: bug`)

- **`type:`** exactly one per issue — `bug`, `crash`, `regression`, `feature`, `enhancement`, `perf`, `docs`,
  `question`, `chore`.
- **`area:`** zero or more, mirroring `src/apps/Wavee/Features/*` — `playback`, `video`, `lyrics`, `player`,
  `connect`, `library`, `playlists`, `search`, `home`, `browse`, `concerts`, `detail-pages`, `sidebar`, `shell`,
  `auth`, `setup`, `updates`, `store`, `release-tooling`, `diagnostics`, `modules`, `i18n`, `engine`
  (`area: engine` = belongs in `christosk92/fluent-gpu`; cross-reference only, don't fix it here).
- **`arch: x64` / `arch: arm64`**, **`install: store` / `install: sideload`** when relevant.
- **Priority** `P0: critical` · `P1: high` · `P2: medium` · `P3: low`.
- **`status:`** lifecycle `needs-triage → needs-info | needs-repro → confirmed → (blocked | upstream)`. Issue
  forms set `status: needs-triage`; remove it when triaged.
- **`resolution:`** on close — `duplicate`, `wontfix`, `invalid`, `by-design`, `cannot-reproduce`.
- `good first issue`, `help wanted` unchanged.

## Triage recipe (ask first, then run)

```powershell
gh issue view <n> --json title,body,labels,milestone            # read-only
gh issue edit <n> --add-label "type: bug,area: playback,P2: medium" --remove-label "status: needs-triage" --milestone "0.2.x Breaker"
gh project item-add 3 --owner christosk92 --url https://github.com/christosk92/WaveeMusic/issues/<n>
# Status / Priority on the board (item id from item-list; option ids from field-list):
gh project item-list 3 --owner christosk92 --format json --jq '.items[] | "\(.content.number) \(.id)"'
gh project field-list 3 --owner christosk92 --format json --jq '.fields[] | select(.options) | {name, options:[.options[] | {name,id}]}'
gh project item-edit --project-id PVT_kwHOAM0O7s4BiKKu --id <itemId> --field-id <fieldId> --single-select-option-id <optId>
```

Bug reports without a version quad / install source / arch → `status: needs-info` and ask the reporter to use
the form's fields (Settings › About; Settings › Diagnostics › **Copy diagnostics info**). Rendering / text /
input glitches → `area: engine`, `status: upstream`, point at fluent-gpu. Feature requests that are really
half-formed ideas → suggest Discussions › Ideas.

## Bugfix bookkeeping (every fix, no exceptions)

`ops/release/wavee-release.ps1`'s `issue refs` gate (hard) cross-checks git closing keywords against the
CHANGELOG at release time, so every bugfix has to leave behind the same four things — the release reads and
composes them, it never invents them:

- [ ] **An issue exists.** If the report came in by chat/DM instead of a GitHub issue, create one first
      (approval-gated — ask before running this):
      ```powershell
      gh issue create --repo christosk92/WaveeMusic --title "<short, user-facing summary>" `
        --label "type: bug,area: <area>" --milestone "0.2.x Breaker" --body "<repro / context>"
      ```
      Use the current minor's milestone (see "New minor / new milestone" above) and at least one `area:` label
      from the scheme above.
- [ ] **The CHANGELOG bullet cites it.** Under `## [<next>] - unreleased`, the bullet ends with the trailing
      group ` (#n)` — optionally ` (#n, !pr)` if a PR is also known. Only the *trailing* parenthesised group
      counts; a mid-sentence `(#n)` is ignored by design (`- Fixed the #42 regression` does not link — put the
      ref at the end).
- [ ] **The commit body carries a closing keyword.** One line naming the issue: `Fixes #n` (also accepted:
      `close(s|d)`, `fix(es|ed)`, `resolve(s|d)`, case-insensitive). This is what makes GitHub close the issue
      when the commit lands on `main`, and it is the other half of the cross-check.
- [ ] **A squash-merge suffix or `!N` is a PR, never an issue.** `(#N)` appended to a squashed commit's subject
      (or `!N` anywhere) is read as a **PR** reference by the gate's parser — it never satisfies "this commit
      fixes issue #N". Issues and PRs share one number space on GitHub, so always cite the issue explicitly with a
      `Fixes #n` trailer; the squash suffix only ever names the PR.

At release time the gate fails hard when these disagree: a commit in `<prevTag>..HEAD` fixes an issue the
CHANGELOG entry does not cite, or the entry cites an issue no commit in range fixes. Commits without any ref are
fine ("Other changes" in the release notes); CHANGELOG bullets without a ref are fine too but trigger the soft
`issue coverage` warning. **Before cutting a release, run `-DryRun` and read the `issue refs` row** — it lists
exactly what would fail, before anything is bumped or tagged.

## Changing the label set

1. Edit `ops/github/labels.json` (keep the group colours; add `renameFrom` only for a rename).
2. `Invoke-Pester -Path ops/release/tests/GitHub.Labels.Tests.ps1` — must be green.
3. `powershell -File ops/github/sync-labels.ps1 -DryRun` → show the plan to the user → **ask** → run without
   `-DryRun`. Expect `created=0 updated=53 renamed=0` and no `extra` lines on a no-op re-run.
4. Deleting a label is never done by the script; if the user wants one gone: `gh label delete <name>` after approval.

## New minor / new milestone

When `<WaveeCodename>` changes (see `releasing` skill): close the finished `0.x.x <Codename>` milestone, create the
next one (`gh api -X POST repos/christosk92/WaveeMusic/milestones -f title="0.5 Ebb" -f description=…`), move
unfinished issues forward with `gh issue edit --milestone`. Codename series: Abyss 0.1, Breaker 0.2, Crest 0.3,
Drift 0.4, Ebb 0.5, Fetch 0.6, …

## Gotchas

- `gh label list --json name,color,description` — **no spaces after commas**, or gh treats them as extra args.
- `updateProjectV2Field` (GraphQL) takes `fieldId` + `singleSelectOptions` only — no `projectId`; that is how the
  Status options were set. `gh project field-create` cannot edit the built-in Status field.
- Project **views** exist: 1 Triage (table, `status:Triage is:open`), 2 Board (board, by Status), 3 Roadmap
  (table, `is:open -status:Triage` — group by Milestone in the UI), 4 Bugs (table,
  `label:"type: bug","type: crash","type: regression"`). They were made with GraphQL `createProjectV2View` +
  `updateProjectV2View` (name, layout, filter, `configuration.visibleFieldIds`); grouping and sorting have no API.
  Pass a list argument by inlining it in the query — `gh api graphql -f ids='[…]'` sends a string.
- The **auto-add / auto-close workflows** have no create API (only `deleteProjectV2Workflow`): set them in the
  project UI — auto-add → Status Triage, item closed / PR merged → Done.
- Enabling Discussions seeds the default categories; there is no API to create categories.
- `git fetch --prune` may drop many stale `origin/*` refs — GitHub only has `main`; that is expected.
- The user works in the same checkout with uncommitted changes: when committing docs/tooling, `git add` only the
  files you wrote, use a `chore/…` branch, and switch back to `main` afterwards.
