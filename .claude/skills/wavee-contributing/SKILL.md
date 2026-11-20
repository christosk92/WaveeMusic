---
name: wavee-contributing
description: How a change travels in WaveeMusic from idea to release - which branch to start from and target (release/<next>, never main), change files instead of CHANGELOG edits, theme contract changes, engine changes and engine.lock, spikes, PR rules, what reviews keep finding, and how releases, hotfixes and rollbacks work. Use before starting any Wavee change, creating a branch, writing a CHANGELOG entry, touching Theming/Contracts or ContractVersion, opening or reviewing a PR (own or community), or planning a release, hotfix or rollback.
---

# Contributing to Wavee: the flow

The full design, with the reasons and the owner's decisions, is `docs/plans/wavee/release-flow-design.md`. This skill
is what an agent needs to act on it.

## What is live (keep this table true when a part lands)

| Part | State |
|---|---|
| `main` protected: PR only, merge commits only, no force push, no deletion | **live** (2026-11-20) |
| `release/**` branches: no deletion, no force push | **live**; the open one is `release/0.3.12` |
| tags `wavee-v*`, `wavee-stable` (and `wavee-v*` in fluent-gpu): never moved or deleted | **live** |
| change files in `changes/unreleased/` | **write them now**; the assembler (`Wavee.ReleaseTool changelog assemble`) lands before 0.3.12 ships |
| `engine.lock` (the pinned fluent-gpu commit) | coming. Until then the engine is the sibling checkout at fluent-gpu `main` |
| `tools/pr-check.py` | coming. Until then check the items of "Before you open a PR" by hand |
| `ops/release/release.ps1` (the new runner) | coming. `wavee-release.ps1` can no longer release: it pushes `main`, which the ruleset refuses. 0.3.12 ships with the new runner |

## The rules

1. **`main` is what shipped**, and it is GitHub's default branch: what users can install. Never push to it, never open
   a PR into it, never merge into it. Only the release runner moves it.
2. **Start every change from the open release branch and target it**: `git switch -c feat/<name> origin/release/0.3.12`
   (the newest `release/*` branch; `git branch -r --list 'origin/release/*'`). GitHub suggests `main` as the base: pick
   the release branch by hand (`gh pr create --base release/0.3.12`).
3. **Never edit `CHANGELOG.md`.** A user-facing change adds one change file (below). The runner writes the CHANGELOG.
4. **Never change `ContractVersion.Major`/`Minor`.** A change to `src/apps/Wavee/Theming/Contracts/**` (or the role
   vocabulary) declares `contract: additive|breaking` with a `contract-note` in its change file. In a minor, a new
   member of a props record is optional (`init`, nullable or defaulted); `required`, a removal or a changed type is
   `breaking` and needs the owner's agreement.
5. **Never push to a `release/*` branch.** Changes reach it by PR; only the runner commits there (the version bump,
   the assembled CHANGELOG).
6. **Never delete or move a tag**, never force-push `main` or a release branch, never rewrite pushed history. Undo is a
   revert commit in a PR.
7. **Merge commits only** (no squash, no rebase merge).
8. **The engine keeps its own flow**: an engine change is a PR into fluent-gpu `main` (as today), merged first; the Wavee
   PR then uses it (and bumps `engine.lock` once that file exists). Same branch name in both repos for a paired change.
9. **A spike ends in a decision, never in a merge**: `spike/<topic>`, timeboxed; it may be pushed with a draft PR for
   discussion, then closed unmerged. Write the answer into the issue or a plan; do not reuse spike code as-is.
10. The house rules still hold: no tests, no env-var switches, no legacy paths, PlayPlay never in this repo, ask the
    owner before any push, PR or other GitHub write.

## Change files

One file per user-facing change in `changes/unreleased/`, named after the issue or the branch
(`226-heart-follows-title.md`). Format and examples: `changes/README.md`.

```markdown
---
kind: fixed              # added | changed | fixed | removed
issues: [225, 226]       # issues this change closes; the commit body carries "Fixes #225" too
by: 0dm                  # a community contributor's GitHub login; omit for the owner's changes
contract: none           # none | additive | breaking
contract-note: ""        # one line when contract is not none
---
**The title links only its text in Classic's player bar.** The space beside a short title no longer lights it up,
and in a wide window the heart sits right after the words.
```

The body is the CHANGELOG line in the house voice: a bold, user-facing lead phrase, then plain detail; name the themes
it applies to. Needs one: anything a listener or a theme author notices. Needs none: refactors, docs, tooling, a fix for
a regression that never shipped.

## Before you open a PR

- Branch from and target `release/<next>`; the diff against it shows only your change.
- One logical change per PR. The description covers everything in the diff (a resize and a scrollbar fix in one PR need
  both described, or two PRs).
- A change file when user-facing; `contract:` set when `Theming/Contracts/**` changed; `Fixes #n` in the commit body
  for every `issues:` entry.
- Debug build clean (`dotnet build src/apps/Wavee/Wavee.csproj`, warnings are errors); UI rendered, traced and linted in
  the headless inspector (`docs/guide/agent-inspect.md`), screenshots looked at and attached.
- Walk `review-checklist.md` (this folder): the things reviews of real PRs keep finding, each with the inspector command
  that proves it.
- The owner's own PRs use the `create-pr` style (Cause / Technical / Fix); contributors use the PR template.

## Reviewing a community PR (owner and owner's agents)

Build the PR merged onto the release branch (never its base alone), run it in the inspector at the sizes and themes it
touches, take screenshots, and comment on the exact lines (`gh api .../pulls/<n>/reviews` with `comments[]`). Post only
when the owner asks. An approved PR merges into `release/<next>` right away; there is no "approved, held".

## When a release ships with your PR open

The runner moves open PRs from the shipped `release/<v>` to the new `release/<v+1>` and comments; the change file stays
valid. Merge the new release branch into your branch if it conflicts. Before merging any branch that other PRs target,
move those PRs first (GitHub's auto-delete closes PRs whose base disappears).

## Releases, hotfixes, rollback (owner only)

- **Release** (`ops/release/release.ps1 -Version <v>`, when built): a plain run is a dry run; `-Go` acts; done steps are
  skipped on the next `-Go`. `-Go -To changelog` assembles the change files so the owner can curate the section and the
  What's new highlights. Steps: preflight, changelog, bump, tag, pack, symbols, sign, GitHub release, feed, merge-main,
  open-next, Store submit, notify, Store poll. Agents never start a `-Go` run on their own.
- **Watch window**: 48 h after the feed moves: crash rate (`https://crash.cproducts.dev`), Store failures
  (`tools/store-failures.ps1`), new issues. S1 (crash at start or on a common path, data loss, wiped login, playback
  broken, security) -> hotfix now.
- **Hotfix**: `release.ps1 -Hotfix <v>` renames the open `release/<v>` one patch up (its PRs follow) and makes a new
  `release/<v>` from the shipped tag; the fix is a normal PR into it, released by the runner with no highlight needed,
  then forward-ported. An engine fix goes on `wavee-hotfix/<v>` in fluent-gpu from the engine's `wavee-v<prev>` tag.
  Until the runner exists, the owner does these steps by hand.
- **Rollback**, first to last resort: fix forward; point the feed back (`-RepointFeed <prev> -AllowDowngrade`, moves
  installed copies back); mark the GitHub release a prerelease. The Store cannot downgrade: it rolls forward.
- **Downgrade safety**: anything persistent stays readable by the previous release (the library cache is
  fingerprint-named and safe; settings readers ignore unknown keys and never rewrite them; a new format ships in two
  releases: read both first, write the new one a release later).
