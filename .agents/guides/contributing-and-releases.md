# Contributing & releases (agents + humans)

**Never commit or push directly to `master` or `experimental` — they are
protected branches.** All work goes on a short-lived branch and lands via a
pull request. This applies to coding agents (Claude Code, Cursor, Copilot,
Codex, …) exactly as it does to humans.

## Branches

| Branch | Purpose | Merging here publishes… |
|---|---|---|
| `master` | Production | a **production** release (`vX.Y.Z`, draft → you review + Publish) |
| `experimental` | Staging / alpha | an **experimental pre-release** (`vX.Y.Z-beta.N`, auto-published) |
| `feature/<slug>` | New features | — (PR'd into a release branch) |
| `fix/<slug>` | Bug fixes / hotfixes | — (PR'd into a release branch) |

Default target for everyday work is **`experimental`**. `master` only receives
changes via promotion (`experimental → master`) or urgent hotfixes.

## Commit messages drive the version (conventional commits)

The next version is computed automatically from commits since the last tag:

- `feat: …` → **minor** bump (`0.2.0` → `0.3.0`)
- `fix: …` (or any other prefix: `chore:`, `docs:`, `ci:`, `refactor:`, …) → **patch** bump
- `feat!: …` or a `BREAKING CHANGE:` / `(MAJOR)` line in the body → **major** bump

So write honest conventional-commit subjects — they decide the release number.

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

# 4. Make the required check "PR Build & Test" pass (x64 compile + xUnit).
gh pr checks --watch

# 5. Merge. The release publishes AUTOMATICALLY on merge — no manual tagging.
gh pr merge --squash --delete-branch
```

- Merge → `experimental` ⇒ pre-release `vX.Y.Z-beta.N` (auto-published).
- Merge → `master` ⇒ production `vX.Y.Z` (published as a **draft** — review, then
  hit Publish in GitHub Releases).
- Promote a tested batch with a PR `experimental → master`.

## Don't

- **Don't push to `master` / `experimental`** — it's rejected by branch rules.
- **Don't push tags by hand** — the `version` job in `release.yml` tags
  automatically from the computed version.
- **Don't bypass the `PR Build & Test` check.**
- To land a change on a release branch **without** cutting a release (e.g. a
  pure-infra/docs change), put `[skip release]` in the merge commit message.

## Releases reference

- **CI gate:** `.github/workflows/ci.yml` — runs on every PR + on `feature/**` /
  `fix/**` pushes. Job name (and the required status check) is **`PR Build & Test`**.
- **Release pipeline:** `.github/workflows/release.yml` — `version` (compute +
  tag) → `build` (x64 on `windows-latest`, arm64 on `windows-11-arm`) → `sign`
  (both, on x64, Azure Artifact Signing) → `release` (GitHub Release + `.appinstaller`).
- **Signing details:** `signing/README.md`.
- **Design rationale:** `docs/superpowers/specs/2026-05-29-release-pipeline-design.md` (local notes).
