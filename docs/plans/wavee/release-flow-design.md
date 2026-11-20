# How a change travels: idea -> spike -> feature -> release -> main (design)

Status: **built 2026-11-20** (steps 1-7 of section 14; decisions in section 13). The runner's `-Go` paths are proven by
the first release on it (0.3.12); the runbook is `docs/guide/releasing-wavee.md`. It replaces three habits that failed during 0.3.11: pushing to `main`, editing one shared
CHANGELOG list and one shared contract number in every PR, and a release script whose `-Resume` trusts a state file
instead of looking at reality. The release runner borrows its rules from ISOPlanner's production runner
(`isoplanner-api/Tools/release`): a plain run is a dry run, `-Go` acts, and every step decides from reality whether it is
already done.

---

## 1. The rules in one screen

1. **`main` is what shipped, and it is GitHub's default branch, always**: what a visitor sees on the repo is what they
   can install from the feed or the Store. Nobody pushes to it, not even the owner. Only the release runner moves it,
   by merging a released branch right after the feed serves it.
2. **All work lands on `release/<next>`** (today: `release/0.3.12`) through a pull request with a merge commit.
3. **Every user-facing PR adds one change file** under `changes/unreleased/`. Nobody edits `CHANGELOG.md` or the
   contract version by hand any more; the runner assembles both at release time.
4. **A spike ends in a decision, never in a merge.** It may be pushed with a draft PR for discussion; it is closed
   unmerged.
5. **The engine keeps its own flow.** `fluent-gpu` takes PRs into its `main` as today; a Wavee branch pins the engine
   commit it builds against in `engine.lock`.
6. **A release is one command**, `ops/release/release.ps1 -Version <v>`, run when the owner says it is ready: a dry run
   without `-Go`, the real thing with it, and a second `-Go` continues where the first stopped.
7. **Fix forward by default.** A bad release is fixed by the next patch; the feed can be pointed back in an emergency;
   a pushed tag is never deleted or moved.

---

## 2. Branches

**WaveeMusic:**

| Branch | Made from | Merges into | Who | Lifetime |
|---|---|---|---|---|
| `main` | (exists) | nothing | the runner only (`merge-main`) | forever; protected; GitHub's default branch |
| `release/<v>` | `main`, by the runner (`open-next`), or the shipped tag for a hotfix | `main`, by the runner | everyone's PRs | kept after it ships, protected: the base of a hotfix and the record of what went in |
| `feat/`, `fix/`, `perf/`, `docs/`, `chore/<name>` | `release/<next>` | `release/<next>` by PR (merge commit) | owner lanes, contributors' forks | deleted on merge (GitHub auto-delete stays on) |
| `spike/<topic>` | anywhere | **never merged** | owner lanes | may be pushed with a draft PR; closed unmerged and deleted when the decision is written down |

**fluent-gpu** keeps its own flow: branches from its `main`, PRs into its `main`, its gallery released by its own `v*`
tags. Wavee touches it in two ways only:

- **`engine.lock`** (one line in WaveeMusic: the fluent-gpu commit sha). Every Wavee branch builds against exactly that
  commit; the runner checks the engine out at the pin. A Wavee change that needs new engine code merges the engine PR
  into fluent-gpu `main` first, then bumps `engine.lock` in its Wavee PR. Engine `main` can move freely without ever
  breaking a Wavee release branch.
- **The engine tag.** The runner tags the pinned commit `wavee-v<v>` in fluent-gpu, so a hotfix can rebuild exactly
  what shipped (today the engine commit lives only in a gitignored `release-state.json`).

```
            spike/fast-seek  o--o  (draft PR, closed unmerged: the answer goes into the issue or a plan)

feat/device-picker   o---o---o
                              \ PR
release/0.3.12  --o-----o------o-----o--------R------.
                  ^      ^                    |       \  merge-main
                  |      PR from a fork       |        v
main  ------------*-----------------------------------M--------   (default branch = what users can install)
            wavee-v0.3.11           tag wavee-v0.3.12 on R
                                                       \  open-next
release/0.3.13                                          o--- (open PRs moved here) ...
```

**GitHub rulesets** (WaveeMusic, set once, section 14):

| Target | Rules |
|---|---|
| `main` | changes only by pull request; no force push; no deletion |
| `release/**` | no force push; no deletion (this also stops GitHub's auto-delete from removing a release branch when it merges into `main`, which closed five community PRs on 2026-11-19) |
| tags `wavee-v*`, `wavee-stable` | no deletion, no update: the feed and every install resolve through them |

GitHub documents that rules prevent the automatic deletion; one community report says a ruleset did not, so the runner
also moves every open PR off a release branch before it merges it (belt and braces). In fluent-gpu a ruleset protects
the `wavee-v*` tags only.

---

## 3. The life of a change

```
 idea -----> spike? -----> plan? -----> build -----> PR -----> release/<next> -----> release -----> main
 issue       spike/x       docs/plans   feat/x        review     integrated            runner          shipped
 (Triage)    (decision)    + boards     (In progress) (In review)                      (Done)
```

The column in brackets is the Status on the project board (`github-triage` skill: Triage, Backlog, Planned, In progress,
In review, Done).

| Stage | Where | Output | Gate before the next stage |
|---|---|---|---|
| **Idea** | a GitHub issue (`type: feature` / `type: bug`), or the owner's request | the problem in one paragraph, labels, the minor's milestone | triaged: the owner says "worth doing" (Backlog/Planned) |
| **Spike** (only when there is a real question: can the engine do X, which design reads best, what does it cost) | `spike/<topic>`, timeboxed (a day by default); pushed with a draft PR when someone should see it | the answer written into the issue, or a plan or concept boards | the decision is written down; the draft PR is closed unmerged and the branch deleted; its code is not reused as-is |
| **Plan** (multi-part work, as `CLAUDE.md` asks) | `docs/plans/wavee/<topic>-implementation.md` with real code and wireframes; UI gets concept boards per theme | the plan, committed on the feature branch | the owner agrees |
| **Build** | `feat/<name>` from `release/<next>`; an engine part goes first as a fluent-gpu PR into its `main` | commits `area: summary`, `Fixes #n` in the body; one change file; `engine.lock` bumped when the engine changed; verified (Debug build, inspector render/trace/lint/crossing, screenshots) | `tools/pr-check.py` passes |
| **PR** | into `release/<next>` | the body (Cause / Technical / Fix for the owner's PRs, the template for contributors); screenshots | reviewed; for a community PR the owner's runtime review (build it on the release branch, run it, screenshots, inline comments) |
| **Integrated** | merged into `release/<next>` with a merge commit | the change file waits in `changes/unreleased/` | nothing: it ships with the next release, whenever the owner cuts it |
| **Release** | the runner, section 7 | tag, packages, GitHub release, feed, Store, `main` | the runner's preflight and its steps |

**Testing before a release** stays as it is: the owner's arm64 test folders (`run-rN`) built from the head of
`release/<next>`. There is no public preview channel.

**Work that spans releases** stays on its branch and keeps up by merging `release/<next>` into it. It merges when it is
done, or in slices that each stand on their own. There are no feature switches for unfinished work (no env-var
switches, house rule); a slice that is not ready simply has not merged yet.

**The owner's own work** follows the same path: a lane branch, a PR into `release/<next>`, merged by the owner.

---

## 4. Community contributions

```
fork -> branch from release/<next> -> change file (by: <login>) -> PR into release/<next>
     -> owner review (runtime: build on the release branch, run, screenshots, inline comments)
     -> approve + merge | changes requested
     -> ships in the next release: CHANGELOG credit + "Community" badge in What's new
```

- **Target branch:** the open `release/<next>`, the newest `release/*` branch. GitHub offers `main` (the default
  branch, rule 1), so the contributor picks the base by hand; three things catch a PR that targets `main` anyway:
  `CONTRIBUTING.md` and the PR template say it first; `tools/pr-check.py` refuses a base that is not the open release
  branch; and the runner's preflight lists PRs into `main`, which `release.ps1 -RetargetPrs` moves to `release/<next>`
  with one comment each (the owner starts it). Nothing ever merges into `main` by hand, so a PR on the wrong base waits,
  it never lands.
- **Approved means merged.** With a release branch that ships "when ready", there is no reason to hold an approved PR:
  it merges into `release/<next>` and ships with it.
- **Credit is data, not prose:** `by: <login>` in the change file. The assembled CHANGELOG line ends with "A community
  contribution from @login, thank you", the What's new highlight gets its `community` field from it, and
  `CONTRIBUTORS.md` gains the login.
- **A release while a PR is open:** the runner's `open-next` step moves the PR to the new `release/<next>` and leaves
  one comment saying so. The change file is untouched and still valid: nothing is stranded under a shipped version.
- **Theme contract changes:** the contributor writes `contract: additive` (or `breaking`, which needs the owner's
  agreement) in the change file and never touches the number.
- **Engine changes:** a separate PR into fluent-gpu `main`; the Wavee PR bumps `engine.lock` once that one merged.
- **Conflicts:** the contributor merges `release/<next>` into their branch. Change files never conflict (one file each).
- **What a contributor's agent reads:** `CLAUDE.md`/`AGENTS.md` point at the contributor skill
  (`.claude/skills/wavee-contributing`), which holds this flow, the house rules that reviews keep finding, and the
  inspector command that proves each one.

---

## 5. Change files

One file per change, in `changes/unreleased/`, named after the branch or the issue (`226-heart-follows-title.md`).
The front matter is the data; the body is the CHANGELOG line, in the house style (a bold lead phrase, then plain detail).

```markdown
---
kind: fixed              # added | changed | fixed | removed
issues: [225, 226]       # each must be closed by a commit in the release; the release comments on them
by: 0dm                  # omit for the owner's own changes
contract: none           # none | additive | breaking
contract-note: ""        # one line when contract is not none: what was added, removed or changed
---
**The title links only its text in Classic's player bar.** The space beside a short title no longer lights it up,
and in a wide window the heart sits right after the words.
```

Needs one: anything a listener or a theme author would notice. Needs none: refactors, docs, tooling, a fix for a
regression that never shipped. (The same split GitLab uses for its `Changelog:` trailers.)

Why files and not commit trailers: Wavee's entries are written prose, reviewed in the PR diff, and a fork's commits are
merged as they are; a file is the one thing that survives every merge style. This is the towncrier / changie /
changesets pattern, built into `Wavee.ReleaseTool` because the tool already models what What's new needs (issues,
scope, the community credit).

---

## 6. Versions: who sets what

| Value | Example | Set by | When |
|---|---|---|---|
| semver `M.m.p` | 0.3.12 | the owner, as `-Version` (and the branch name) | at release; the runner writes `Wavee.Version.props`, so it is no longer a hand edit |
| codename | Crest | the owner, per minor | when a minor starts |
| `WaveeBuild` | 23 -> 24 | the runner (`bump`) | every release, monotonic |
| MSIX quad | 0.3.12.24 | derived | |
| Store quad | 1.3.1224.0 | derived (`(M+1).m.(p*100+build).0`) | |
| theme contract | 2.7 -> 2.8 | the runner, from the change files' `contract:` (breaking -> major, additive -> minor, none -> unchanged) | at release |
| engine | `engine.lock` | the PR that needs new engine code | when the engine changes |

Patch is the default. A minor (new codename) is the owner's call; when it is decided late, rename the branch
(`release/0.3.12` -> `release/0.4.0`): renaming a base branch on GitHub retargets its open PRs automatically.

`ContractVersion.cs` keeps the two constants and the rules; its 400 lines of history move to a generated
`docs/themes/contract-history.md`, one section per release from the `contract-note` lines. When an external theme
exists, the declaration is replaced by a generated shipped/unshipped contract surface (Roslyn PublicAPI style: one
sorted line per key and member, moved to "shipped" at release, the bump derived from the difference).

---

## 7. The release

Releases ship **when the owner says the release branch is ready**; there is no fixed rhythm. One runner,
`ops/release/release.ps1`, run by the owner in a window of their own from a release worktree the runner makes
(`C:\WAVEE\release\<v>`: WaveeMusic at the release branch, fluent-gpu at `engine.lock`, the PlayPlay junction and the
signing config linked in).

```
release.ps1 -Version 0.3.12                          dry run: the preflight, then the commands of every step
release.ps1 -Version 0.3.12 -Go -To changelog         assemble the change files into CHANGELOG [0.3.12]; stop
   (edit the section and the What's new highlights, commit them on release/0.3.12)
release.ps1 -Version 0.3.12 -Go -PauseBefore feed     stop before installs can see it
release.ps1 -Version 0.3.12 -Go                      continue: done steps are skipped
release.ps1 -Version 0.3.12 -Status                  what is done, what is next
```

| # | Step | Done when (judged from reality, so a second `-Go` skips it) |
|---|---|---|
| 0 | `preflight` | read-only: tools, signing token, 1Password unlocked (first, with the command that fixes it), `gh`, wrangler, PlayPlay junction, the release branch, the engine checkout at `engine.lock` and clean, feed monotonic, change files valid, every `Fixes #n` matched, PRs into `main` (listed for `-RetargetPrs`), the release body under GitHub's 125,000 characters |
| 1 | `changelog` | `changes/unreleased/` is empty and `CHANGELOG.md` has `[v]` (the release tool also raises the contract version, writes `contract-history.md` and credits new contributors) |
| 2 | `bump` | a `release: Wavee <v> <codename> (build <quad>)` commit is on the branch: the version props say `v`, the build is one up, the CHANGELOG heading is dated |
| 3 | `notes` | the release notes are validated into the staging folder for this commit (the commit fold lists merge commits only) |
| 4 | `tag` | `wavee-v<v>` exists locally at the release commit, and in the engine worktree at the pinned commit |
| 5 | `pack-arm64`, `pack-x64` | a staged package with this quad, built from this commit, with the crash stamp |
| 6 | `symbols` | the maps and binaries for this quad are uploaded (each upload retried with backoff: uploads are idempotent) |
| 7 | `sign` | `signtool verify` passes on every package |
| 8 | `stage` | the `.appinstaller` files and the staged assets match `MANIFEST.txt` |
| 9 | `push` | origin has the release commit on `release/<v>` and both tags (WaveeMusic and fluent-gpu) at the right commits |
| 10 | `release` | the GitHub release exists, is published, and its assets are byte-equal to the staged files |
| 11 | `feed` | `wavee-stable` serves this quad and this release's package on both architectures (**the moment installs see it**) |
| 12 | `open-next` | `release/<v+1>` exists (made from the release head) and no open PR targets `release/<v>` or `main`: each moved PR gets one comment |
| 13 | `merge-main` | the release commit is an ancestor of `main`: the runner opens and merges a PR `release/<v>` -> `main` with a merge commit (the ruleset lets `main` change only by PR) |
| 14 | `forward-port` | (a hotfix only) `release/<v>` is merged into the renamed next branch |
| 15 | `store-submit` | Partner Center has a committed submission for this Store quad (to everyone at once: no gradual rollout) |
| 16 | `notify` | every issue the release closed carries the release comment |
| 17 | `store-poll` | the submission is Published (certification takes hours to days; not reaching it within the run is no failure, `-Status` reports it) |

- **The release notes' commit fold lists merge commits only** (`--first-parent` on the release branch): one line per
  PR, not every WIP commit of every lane. 0.3.11's fold had 613 commits and broke GitHub's body limit.
- **When a `-Go` run ends** (done, stopped, paused), a Windows toast on this PC says the exit code and the step.
  Nothing leaves the machine.
- Exit codes: 0 done, 1 usage, 2 preflight refused (nothing changed), 3 stopped on a condition, 4 paused.
- `-Status -Json` and `-Preflight -Json` answer in the shape of ISOPlanner's runner, so its Release Ops view could add
  Wavee later without a change to the runner (not planned now).

**A failure in the middle:**

| Where it stopped | State | What to do |
|---|---|---|
| before `push` | nothing public (the tags and commits are local to the release worktree) | fix, `-Go` again; or `-Abort -Go` (deletes the local tags and the runner's commits) |
| after `push`, before `feed` | the tag and maybe a draft release are public; installs see nothing | fix, `-Go` again. Never delete the tag |
| after `feed` | installs are updating | it shipped. Finish with `-Go`; a bad build goes to section 9 or 10 |

---

## 8. After a release: the watch window

For 48 hours after `feed` (and again when the Store publishes):

- the crash dashboard (`https://crash.cproducts.dev`): crash rate of the new quad against the previous one;
- the Store failure report (`tools/store-failures.ps1`), which lags one to three days;
- new issues with the new version quad.

| Severity | Examples | Response |
|---|---|---|
| **S1** | a crash at start or on a common path, lost user data, a wiped login, playback broken for everyone, a security problem | hotfix now (section 9); point the feed back first when the build is actively harmful (section 10) |
| **S2** | a feature broken with a workaround | the next release, or a hotfix when the next release is far |
| **S3** | cosmetic | the next release |

---

## 9. Emergency hotfix

A hotfix is an ordinary release cut from the shipped tag instead of from the work in progress. It takes the next patch
number, so the work that was waiting moves up one.

```
before:  wavee-v0.3.11 shipped;  release/0.3.12 holds next week's work
         release.ps1 -Hotfix 0.3.12:
           1. renames release/0.3.12 -> release/0.3.13 (its open PRs follow: a base-branch rename retargets them)
           2. makes a new release/0.3.12 from wavee-v0.3.11 (its engine.lock is the engine that shipped)

main          --*(v0.3.11)---------------------------M1---------------M2--
                 \                                  /                 /
release/0.3.12    o-- fix --o-- R(v0.3.12) --------'                 /
  (new, from the tag)            \ forward-port                      /
release/0.3.13  ...o----o---------o----o----o--- R(v0.3.13) ---------'
  (renamed from 0.3.12)
```

To check when this is built: whether GitHub treats a branch rename as a deletion under the `release/**` ruleset; if it
does, the rename runs under the owner's bypass.

1. `fix/<name>` from the new `release/0.3.12`: the smallest change that removes the problem, with a change file
   (`kind: fixed`, the issue) and `Fixes #n`.
   - **When the fix is in the engine:** a branch `wavee-hotfix/0.3.12` in fluent-gpu from its `wavee-v0.3.11` tag takes
     the engine fix, and the hotfix's `engine.lock` points at it; the same fix also goes to fluent-gpu `main` by an
     ordinary PR. The shipped engine never picks up unrelated engine work through a hotfix.
2. PR into `release/0.3.12`, reviewed, merged.
3. `release.ps1 -Version 0.3.12 -Go`: the same runner and steps. **A hotfix needs no highlight**: What's new shows the
   tagline and the fix list (the validator learns that a release from a `-Hotfix` branch may have no highlights; 0.3.10
   needed a filler).
4. `merge-main`, then a `forward-port` step merges `release/0.3.12` into `release/0.3.13`. A conflict stops the step and
   becomes a PR into `release/0.3.13`.
5. The Store gets the same package through `store-submit`. Certification is not instant: for an S1 the feed is the fast
   path and the Store follows.

---

## 10. Rollback

Ranked from first to last resort:

| Lever | Reaches | Speed | Use when |
|---|---|---|---|
| **Fix forward** (a hotfix, section 9) | everyone, through the normal update | hours | the default, always |
| **Point the feed back** (`release.ps1 -RepointFeed 0.3.11 -AllowDowngrade`) | every feed install: `ForceUpdateFromAnyVersion` moves installed copies BACK | minutes | the shipped build is actively harmful (S1) and a fix is hours away |
| **Mark the GitHub release a prerelease** | people reading the release page | instant | together with either of the above |
| **Store** | Store installs | hours to days | the Store cannot downgrade and releases go to everyone at once: Store installs always roll forward with the hotfix |

- **Git is never rolled back.** No deleted or moved tag, no force push of `main` or a release branch. Undoing a change is
  a revert commit in a PR into the open release branch.
- **Downgrade safety (the expand/contract rule for local data).** Pointing the feed back runs the older build on data
  the newer one wrote. So everything persistent must stay readable by the previous release:
  - the library cache is safe by design (its file name is its schema fingerprint, and a cache is never migrated);
  - settings and other stored records: readers ignore keys they do not know and never rewrite them;
  - a format an older build cannot read ships in two releases: first a release that reads both formats, then the one
    that writes the new format.

---

## 11. What catches what

| Guard | Runs | Catches |
|---|---|---|
| `.githooks/pre-commit` | every commit | test files, PlayPlay material |
| `tools/pr-check.py` | before pushing a PR, and the reviewer runs it | no change file on a user-facing change; a contract change without `contract:`; a PR not based on the open release branch; `Fixes #n` without `issues: [n]`; an `engine.lock` that names a commit not on fluent-gpu `main` (or a `wavee-hotfix/*` branch); PlayPlay paths |
| GitHub rulesets | always | pushes to `main`; deleted or force-pushed release branches; moved or deleted release tags |
| runner preflight | every release, dry or not | signing, 1Password, `gh`, wrangler, PlayPlay junction, engine at the pin, feed monotonic, change files, issue refs, body size, PRs into `main` |
| runner steps | `-Go` | each step's "done when" |
| the watch window | 48 h after `feed` | crash rate, Store failures, new issues |

---

## 12. Who does what

| | Contributor | Owner | Runner |
|---|---|---|---|
| open issues, spikes, plans | issues | all | |
| feature branches and PRs into `release/<next>` | yes (from a fork) | yes | |
| merge a PR into `release/<next>` | | yes | |
| write a change file; bump `engine.lock` | yes | yes | |
| edit `CHANGELOG.md`, the version props, the contract number | | curate the assembled section only | assembles and bumps |
| tag, packages, GitHub release, feed, Store | | starts the runner | yes |
| move `main`; create, rename and retarget release branches | | | yes |
| hotfix, feed rollback | | decides and starts the runner | yes |

---

## 13. Decisions (owner, 2026-11-19/20)

| # | Question | Decision |
|---|---|---|
| D1 | GitHub's default branch | `main`, always: it shows what is available on the feed and in the Store |
| D2 | Shipped release branches | kept, protected |
| D3 | Highlights for a hotfix | none needed; What's new shows the fix list |
| D4 | Gradual Store rollout | never: every Store release goes to everyone at once |
| D5 | A hotfix while `release/<next>` is open | the open branch is renamed one patch up; the hotfix takes its name from the tag |
| D6 | The engine's flow | its own (PRs into fluent-gpu `main`, gallery `v*` tags); Wavee pins it in `engine.lock`; the runner tags the pinned commit |
| D7 | A preview channel | none: the owner's arm64 test folders only |
| D8 | Release cadence | when the owner says it is ready |
| D9 | Merge style | merge commits; the release notes' commit fold lists merge commits only |
| D10 | Spikes | may be pushed with a draft PR; closed unmerged |
| D11 | The end of a release run | a Windows toast on this PC |
| D12 | ISOPlanner's Release Ops view | not now; the runner answers `-Status -Json`/`-Preflight -Json` in its shape |

---

## 14. Build order (0.3.12 is the first release on this flow)

1. **GitHub** (each step asked first): the rulesets of section 2 (WaveeMusic; the tag ruleset in fluent-gpu);
   `release/0.3.12` in WaveeMusic from `main`; 0dm's five open PRs moved from `main` to `release/0.3.12`, with a comment.
   The default branch stays `main`.
2. **`engine.lock`** on `release/0.3.12` (the engine commit 0.3.11 shipped with, `6b5395a`); the build reads the
   engine from the sibling checkout as today, and the preflight and `pr-check.py` compare it with the pin.
3. **`Wavee.ReleaseTool`**: read change files; `changelog assemble`; the contract bump and `contract-history.md`; the
   issue check from `issues:`; the What's new `community` field from `by:`; no highlight needed on a hotfix; the commit
   fold from merge commits only.
4. **`ContractVersion.cs`**: shrink to the constants and the rules; the history moves out.
5. **The runner** `ops/release/release.ps1`: the steps of section 7 judged from reality, dry run by default, the Store
   steps folded in (`wavee-store-submit.ps1` stays as the runner's Store engine; nobody runs it by hand), the release worktree, `merge-main`, `open-next`, `-Hotfix`,
   `forward-port`, `-RetargetPrs`, `-RepointFeed` kept, the engine tag, the toast. The current `wavee-release.ps1` is
   deleted when it lands (no legacy paths).
6. **Contributor side**: `tools/pr-check.py`; `.claude/skills/wavee-contributing` (this flow plus the review findings,
   each with its inspector check); `CONTRIBUTING.md`, the PR template, `CLAUDE.md`/`AGENTS.md` pointers;
   `docs/guide/releasing-wavee.md` rewritten for the runner.
7. **Existing open work**: the release-body cap fix (`fix/release-body-cap`) lands first through a PR into
   `release/0.3.12`, as the first change on the new flow.
