# Contributing & releases (agents + humans)

WaveeMusic uses a **release-train** flow: each version is built up on its own
**release branch**, pre-releases are cut as **deliberate milestone drops**, and the
finished line is **promoted to `master`** to ship production. This applies to coding
agents (Claude Code, Cursor, Copilot, Codex, …) exactly as it does to humans.

**Never commit or push directly to `master` or a `release/*` branch — always open a
pull request.** An active GitHub **ruleset** ("Protect release branches") enforces
this on `master` and `release/**`: it requires a PR and blocks force-push / deletion.

## Branches

| Branch | Role | Merging in publishes… |
|---|---|---|
| `master` | Production / final + **GitHub default branch** | nothing automatically — production is cut deliberately (see below) |
| `release/<X.Y.Z>-<label>` | The **active** per-version line (e.g. `release/0.1.0-alpha`) — integration + pre-release channel | nothing on merge; you cut milestone pre-releases from it deliberately |
| `feature/<slug>` · `fix/<slug>` · `hotfix/<slug>` | Short-lived work | — (PR'd into the active release branch) |

- **Day-to-day work targets the active `release/*` branch**, not `master`.
- `master` only advances via **promotion** (`release/* → master`) when a version ships.
- There is no `experimental` branch (retired — the rolling pre-release *auto-update channel* is still named `experimental-latest`, see below).

## Versioning — the branch name is the source of truth

There are **no version env vars to edit**. The release version is derived from the
ref the release workflow runs on:

- Run on `release/<X.Y.Z>-<label>` → pre-release **`v<X.Y.Z>-<label>.N`**, where `N`
  auto-increments from existing `v<X.Y.Z>-<label>.*` tags (starts at `.1`). No manual tagging.
- Run on `master` with an explicit `version_tag` (e.g. `v0.1.0`) → **production `v<X.Y.Z>`**.
- Change phase by creating the next-phase branch off the current tip:
  `release/0.1.0-alpha` → `release/0.1.0-beta` → `release/0.1.0-rc`. Start a new version
  with `release/0.2.0-alpha`.

These map onto `signing/Versioning.ps1` bands (`alpha`→1000+N, `beta`→2000+N,
`rc`→3000+N, stable→10000) so auto-update ordering stays monotonic. Use
conventional-commit subjects (`feat:` / `fix:` / `docs:` / `ci:` …) for readable history.

## Auto-update: what goes live & who gets priority

WaveeMusic ships under a **single package identity**, so "which build wins" is decided
by the **monotonic 4-part MSIX version + App Installer's forward-only rule**, not a flag.

- **Two channels, chosen at install time:** `Wavee.Experimental.<arch>.appinstaller` →
  the **experimental** channel (rolling `experimental-latest` release; gets every
  milestone drop). `Wavee.<arch>.appinstaller` → the **stable** channel (GitHub `/latest`;
  gets published production only).
- **"Goes live":** a pre-release drop is live the instant CI re-uploads its assets to
  `experimental-latest`. A **production** release is created as a **draft** and only goes
  live when you **Publish** it (the human gate) — `/latest` then flips to it.
- **Priority (per `X.Y.Z`):** `alpha 1000+N < beta 2000+N < rc 3000+N < stable 10000`;
  App Installer never downgrades. Publishing stable `v0.1.0` rolls **even experimental
  testers** forward onto it; starting `v0.2.0-alpha.1` pulls experimental testers ahead
  again while stable users stay on `0.1.0`.

**Rules this forces:**
- Only the **active next-version line** feeds the experimental channel (the rolling
  pointer is single-track — newest upload wins regardless of branch).
- **Hotfixes ship as production (stable), never as experimental drops.** A `v0.1.1`
  reaches stable users; experimental testers already on `0.2.0-alpha.*` are untouched
  (higher version) and get the fix via the forward-merge.
- The stable channel is dormant until the **first Published production** release.
- Truly independent concurrent *public* pre-release channels need a **second package
  identity** (new PFN + Phi-Silica LAF token) — deferred.

## Workflow (the `gh` commands an agent runs)

```bash
# 1. Branch off the ACTIVE release line
git switch release/0.1.0-alpha && git pull
git switch -c feature/<slug>

# 2. Commit (conventional-commit subject)
git commit -m "feat: add the thing"

# 3. Push + PR into the active release branch (NOT master)
git push -u origin feature/<slug>
gh pr create --base release/0.1.0-alpha --fill
gh pr merge --squash --delete-branch          # merge does NOT publish

# 4. Cut a milestone pre-release when it's worth a tester build (DELIBERATE)
gh workflow run release.yml --ref release/0.1.0-alpha
#   → v0.1.0-alpha.N, auto-published, refreshes the experimental .appinstaller channel

# 5. Promote to production when the line is ready
gh pr create --base master --head release/0.1.0-rc --fill && gh pr merge --squash
gh workflow run release.yml --ref master -f version_tag=v0.1.0   # → production DRAFT
gh release edit v0.1.0 --draft=false                            # review, then go live
```

**Concurrency / hotfix:** start the next version (`release/0.2.0-alpha` off `master`)
while patching a shipped one: branch `release/0.1.1` off the `v0.1.0` tag, PR the
`hotfix/*` into it, promote `release/0.1.1 → master`, cut `v0.1.1` production, then
forward-merge `master` into `release/0.2.0-alpha` so the fix isn't lost.

## Agent decision gates — ask the user before these (never auto)

| Gate | What the agent asks |
|---|---|
| Open a new line | "New release line — version & label? (e.g. `release/0.2.0-alpha`)" |
| Cut a milestone | "Cut a drop on `release/X` now? It publishes `vX-label.N` to testers." |
| Phase change | "Move to beta/rc? (branch `release/X-<label>` off the current tip)" |
| Promote to prod | "Promote `release/X` → `master` and cut production `vX.Y.Z` (as a draft)?" |
| Publish (go-live) | "Publish draft `vX.Y.Z` now? It goes live to stable and rolls experimental testers onto it." |
| Hotfix | "Hotfix which shipped version, and what patch tag (`vX.Y.Z+1`)?" |

Routine work — feature/fix PRs into the active release branch — needs **no** release gate.
Everything that **publishes or changes what testers/users receive** is gated.

## Don't

- **Don't push to `master` / `release/*`** — open a PR; the ruleset rejects direct pushes.
- **Don't push tags by hand** — the `version` job in `release.yml` tags from the ref.
- **Don't expect a merge to publish** — pre-releases and production are cut deliberately
  with `gh workflow run` (milestone drops), never on merge.

## Releases reference

- **Release pipeline:** `.github/workflows/release.yml` — `workflow_dispatch` only;
  `version` (derive from ref + tag) → `build` (x64 on `windows-latest`, arm64 on
  `windows-11-arm`) → `sign` (both, on x64, Azure Artifact Signing) → `release`
  (GitHub Release + `.appinstaller`; pre-release auto-publishes + refreshes
  `experimental-latest`, production publishes as a draft).
- **PR validation:** no branch/PR build workflow is configured. The GitHub ruleset
  gates `master` and `release/**` by requiring pull requests and blocking force-push /
  deletion; review/build verification is manual until a useful CI gate is reintroduced.
- **Signing details:** `signing/README.md`.
- **Design:** `docs/superpowers/specs/2026-05-30-release-train-design.md`.
