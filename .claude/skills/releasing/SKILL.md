---
name: releasing
description: Use when cutting, publishing, or troubleshooting a Wavee MSIX release — the local `ops/release/wavee-release.ps1` runbook (no CI job), the `wavee-v*` tag and the rolling `wavee-stable` update feed, building/signing the NativeAOT MSIX, Azure Trusted Signing failures (Invalid tenant id, SignerSign 0x80004005, publisher 0x8007000B), the GitHub release, or the .appinstaller.
---

# Releasing Wavee (signed MSIX, local only)

Wavee ships as a **NativeAOT, packaged-Win32 full-trust MSIX** (arm64 + x64, x64 via the MSVC `HostArm64\x64` cross
toolchain), signed by **Azure Trusted Signing** and published to GitHub Releases on **this** repo. **There is no CI
job**: a CI checkout has no `src/apps/Wavee.PlayPlay` junction and would silently publish the public-only variant, so a
release is cut on the dev box by `ops/release/wavee-release.ps1`.

## Cut a release

```powershell
# 1. hand edits: src/apps/Wavee/Wavee.Version.props (<WaveeVersion> semver, <WaveeCodename> one per MINOR - NEVER <WaveeBuild>),
#    CHANGELOG.md "## [X.Y.Z] - unreleased", ops/release/wavee/<semver>/whatsnew.json (+ media/)
# 2. rehearse
powershell -File ops\release\wavee-release.ps1 -DryRun -SkipTests
# 3. ship (14 phases; the feed is repointed LAST)
powershell -File ops\release\wavee-release.ps1
# failure after the tag was pushed -> re-run with -Resume; abandon -> -Abort; feed only -> -RepointFeed
```

| Thing | Value |
|---|---|
| Tag | `wavee-vX.Y.Z` — created **by the script**, annotated, never deleted |
| Script chain | `ops/release/wavee-release.ps1` → `ops/build/pack-wavee-msix.ps1` (one arch per call) + `ops/build/Wavee.Build.psm1` + `ops/release/Wavee.Release.psm1` |
| Manifest / template | `ops/build/Wavee.AppxManifest.xml`, `ops/build/Wavee.AppInstaller.template.xml` (silent: `ShowPrompt="false"`) |
| Package identity | `cproducts.Wavee` (publisher `CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL` — must equal the cert subject) |
| Version | `Wavee.Version.props`; MSIX quad = `M.m.p.<WaveeBuild>` (the script bumps `WaveeBuild`) |
| Notes | `CHANGELOG.md` + `ops/release/wavee/<semver>/whatsnew.json`, validated/rendered by `Wavee.ReleaseTool` |
| Version-release assets | `Wavee_<quad>_arm64.msix`, `Wavee_<quad>_x64.msix`, `Wavee-<quad>-win-<arch>-symbols.zip` (one per arch: `Wavee.pdb` + `Wavee.map.xml` + `SYMBOLS.txt`), `whatsnew.json`, media, `THIRD-PARTY-NOTICES.txt`, `MANIFEST.txt` |
| Update feed | rolling release **`wavee-stable`** → `Wavee.arm64.appinstaller`, `Wavee.x64.appinstaller`, `whatsnew-index.json` (`--clobber`, repointed last); every install has this URL baked in |

Verify:

```powershell
gh release view wavee-vX.Y.Z --repo christosk92/WaveeMusic --json assets -q '.assets[].name'
Import-Module ops\release\Wavee.Release.psm1
Get-WaveeFeedVersion christosk92/WaveeMusic wavee-stable arm64   # the feed head clients poll
```

The **Microsoft Store leg is a separate runbook**: `ops\release\wavee-store-submit.ps1`, run after the feed release
from the same tag (`-DryRun` / `-Resume` / `-Abort` / `-Status`) — see `docs/guide/microsoft-store-onboarding.md`.

**Full runbook — prerequisites, the `-DryRun` review, the phase table, failure/recovery, rollback, the local E2E
harness (`ops/release/tests/local-update-e2e.ps1 -Scenario inapp|os`, elevated) and the scratch-feed rehearsal:
`docs/guide/releasing-wavee.md`.**

## Symbolicating a crash report

Shipped builds have `StackTraceSupport=false`: a crash report's frames are `at Wavee!<BaseAddress>+0x7b1fc6` and
nothing else. `pack-wavee-msix.ps1` therefore ALWAYS publishes with `NativeDebugSymbols=true DebugType=portable
IlcGenerateMapFile=true`, moves `Wavee.pdb` (from `publish\`) + `Wavee.map.xml` (from `obj\Release\net10.0\<rid>\native\`)
into `<staging>\symbols\<quad>\win-<arch>\` **before** the layout copy, zips them as `Wavee-<quad>-win-<arch>-symbols.zip`,
and the release script uploads that zip beside the `.msix`. The PDB matches exactly one exe (`SYMBOLS.txt` carries its
sha256); the map has names/sizes/hashes but **no addresses**.

```powershell
# the report header says which zip: commit= / quad= / arch=; "Frames (RVA)" lists the offsets one per line
gh release download wavee-vX.Y.Z --repo christosk92/WaveeMusic -D sym -p "Wavee-<quad>-win-<arch>-symbols.zip" -p "Wavee_<quad>_<arch>.msix"
Expand-Archive sym\Wavee-<quad>-win-<arch>-symbols.zip sym; Copy-Item sym\Wavee_<quad>_<arch>.msix sym\pkg.zip; Expand-Archive sym\pkg.zip sym\pkg
cdb -lines -z sym\pkg\Wavee.exe -y sym -c "ln Wavee+0x7b1fc6; q"     # -> Wavee!<mangled method>+0x..; u Wavee+0x<rva> L1 for file:line
```

Full walkthrough and the caveats (`IlcFoldIdenticalMethodBodies`, absolute addresses, releases that shipped without a
zip): `docs/guide/releasing-wavee.md` §5b.

## Gotchas (every one of these actually happened)
- **The active Azure subscription is usually REDLAB** → Trusted Signing fails (`SignerSign() failed` / "Service request failed"). `az account set --subscription "Azure subscription 1"`.
- **`0x8007000B` publisher mismatch** → manifest `Publisher` must EXACTLY equal the cert subject.
- **Timestamp**: `http://timestamp.acs.microsoft.com` (HTTP, not HTTPS).
- **AOT link fails / `vswhere` not found** → needs VS Build Tools (MSVC); the pack script prepends the VS Installer dir to PATH.
- **Tag pushed but the upload failed** → `-Resume`. Staging is hash-verified against `MANIFEST.txt`, then the run continues.
- **Never delete a pushed release tag; never delete or re-tag `wavee-stable`.** The feed's `MainPackage/@Uri` and `whatsnew-index.json` resolve through them; destroying the feed orphans every client. A bad build is fixed by the next release.
- **`releases/latest` is never used** (`--latest=false` on every Wavee release) — clients poll `wavee-stable`.
- **`.gitignore` has `[Rr]elease/`**, so `!ops/release/` is what keeps the release tooling tracked.
- **Testing the tooling never touches production**: `-FeedRelease wavee-stable-test -TagPrefix wavee-test-v -Branch release-test -Force -SkipTests`, or the fully local E2E harness (no GitHub at all).
- **Packaged runs write into the package's LocalCache.** A real `%LOCALAPPDATA%\Wavee` (from an unpackaged `dotnet run`) captures a packaged app's writes; wipe it before testing a package — the startup line prints `logResolved=`.
- **The `issue refs` gate is about consistency, not coverage.** A commit without any issue ref is fine (it lands
  in the release notes as "Other changes"); a CHANGELOG bullet without a ref is fine too (soft `issue coverage`
  warning only). What the hard gate refuses is *disagreement*: a `Fixes #n` commit the CHANGELOG `(#n)` doesn't
  cite, or a `(#n)` the CHANGELOG cites that no commit in range actually closes. Fix it in whichever file is
  wrong — CHANGELOG.md or the commit trailer — and re-run; see `github-triage`'s "Bugfix bookkeeping" section
  for the contract that avoids ever hitting this at release time.
