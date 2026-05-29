# Contributing & releases (agents + humans)

**Never commit or push directly to `master` or `experimental` — they are
protected branches.** All work goes on a short-lived branch and lands via a
pull request. This applies to coding agents (Claude Code, Cursor, Copilot,
Codex, …) exactly as it does to humans.

## Branches

| Branch | Purpose | Merging here publishes… |
|---|---|---|
| `master` | Production | a **production** release (`vX.Y.Z`, draft → you review + Publish) |
| `experimental` | Staging / alpha | an **experimental pre-release** (`v0.1.0-alpha.N` while in alpha, auto-published) |
| `feature/<slug>` | New features | — (PR'd into a release branch) |
| `fix/<slug>` | Bug fixes / hotfixes | — (PR'd into a release branch) |

Default target for everyday work is **`experimental`**. `master` only receives
changes via promotion (`experimental → master`) or urgent hotfixes.

## Versioning (set deliberately; the counter is automatic)

The release version has a **single source of truth**: two values in the
`version` job of `.github/workflows/release.yml`:

    RELEASE_BASE: "0.1.0"        # the X.Y.Z core version
    RELEASE_PRERELEASE: "alpha"  # experimental channel label: alpha -> beta -> rc

- **experimental** merges publish `v$RELEASE_BASE-$RELEASE_PRERELEASE.<N>`; the
  counter `<N>` auto-increments from existing tags, so `v0.1.0-alpha.1` is
  followed by `v0.1.0-alpha.2`, `…alpha.3`, … No manual tagging.
- **master** merges publish `v$RELEASE_BASE` (no label) as a production release.

Change phase by editing those two values **in a PR**:
- mature alpha → beta: set `RELEASE_PRERELEASE: "beta"` (counter restarts at `beta.1`);
- start a new version: bump `RELEASE_BASE` (e.g. `0.2.0`);
- go stable: merge to `master` (drops the label) → `v0.1.0`.

These map onto the `signing/Versioning.ps1` bands (`alpha`→1000+N, `beta`→2000+N,
`rc`→3000+N, stable→10000), so auto-update version ordering stays correct.

Use conventional-commit subjects (`feat:` / `fix:` / `docs:` / `ci:` …) for a
readable history — they no longer pick the version (the two values above do).

## Workflow

```bash
# 1. Branch off experimental (default) or master (hotfix)
git checkout experimental && git pull
git checkout -b feature/<slug>

# 2. Commit with conventional-commit subjects
git commit -m "feat: add the thing"

# 3. Push and open a PR (base = experimental by default)
git push -u origin feature/<slug>
gh pr create --base experimental --fill
#   hotfix / promotion:  gh pr create --base master --fill

# 4. Merge the PR. The release publishes AUTOMATICALLY on merge — no manual tagging.
gh pr merge --squash --delete-branch
```

- Merge → `experimental` ⇒ pre-release `v0.1.0-alpha.N` (auto-published).
- Merge → `master` ⇒ production `vX.Y.Z` (published as a **draft** — review, then
  hit Publish in GitHub Releases).
- Promote a tested batch with a PR `experimental → master`.

## Don't

- **Don't push to `master` / `experimental`** — it's rejected by branch rules.
- **Don't push tags by hand** — the `version` job in `release.yml` tags
  automatically from the computed version.
- To land a change on a release branch **without** cutting a release (e.g. a
  pure-infra/docs change), put `[skip release]` in the merge commit message.

## Releases reference

- **Release pipeline:** `.github/workflows/release.yml` — `version` (compute +
  tag) → `build` (x64 on `windows-latest`, arm64 on `windows-11-arm`) → `sign`
  (both, on x64, Azure Artifact Signing) → `release` (GitHub Release + `.appinstaller`).
- **Signing details:** `signing/README.md`.
- **Design rationale:** `docs/superpowers/specs/2026-05-29-release-pipeline-design.md` (local notes).
