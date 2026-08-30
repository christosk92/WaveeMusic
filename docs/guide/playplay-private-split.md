# PlayPlay: public/private split

The local playback-runtime package and its supporting material live in a **separate private repo**
(`wavee-playplay-private`), not in this public repo. The public app builds and runs without it; protected
playback is simply unavailable and the UI shows an "install the local package" state.

## Why

Keeps the public `fluent-gpu` repo (the UI engine + app) free of version/arch-specific
reverse-engineering / native-derivation material — out of git, out of AI-agent context, and off the
public GitHub record.

## How it links back in

The build is absence-tolerant. The presence of `src/apps/Wavee.PlayPlay/Client/InProcessPlayPlayKeyDeriver.cs` flips the
`WAVEE_PLAYPLAY_LOCAL` MSBuild symbol (not the sibling csproj alone — a partial junction must not enable code paths
that reference types which never compile); the package's `**/*.cs` + `Protos/playplay.proto` then source-link
into the `Wavee` assembly (`src/apps/Wavee/Wavee.csproj`), and the test project links `Tests/`. With the
package absent, the app compiles against the public seam only: `IPlayPlayKeyDeriver`/`NullPlayPlayKeyDeriver`,
`IPlayPlayProvisioner`/`NullPlayPlayProvisioner`, and the pure DTOs/status enums under
`SpotifyLive/Audio` + `Backend/Audio/Contracts`.

Use the (gitignored) helper to junction the private package in/out:

```powershell
./link-playplay.ps1 -Mode link      # junction the private package -> local DRM build works
./link-playplay.ps1 -Mode status
./link-playplay.ps1 -Mode unlink    # restore the clean/absent default (do this before AI-assisted sessions)
```

Default state is **unlinked/absent** — the clean state agents and CI see.

## Local playback in a NEW worktree or scratchpad

`git worktree add` (and any Claude scratchpad checkout) materializes **tracked files only**. The
`src/apps/Wavee.PlayPlay` junction is gitignored, so it does not come along, `WAVEE_PLAYPLAY_LOCAL`
stays undefined, and the app falls back to `NullPlayPlayProvisioner` — which is exactly the
"local playback needs a one-time setup" dialog people hit and misread as a missing runtime download.

The fix is one command, run **from that worktree's own root**:

```powershell
./link-playplay.ps1 -Mode link      # then rebuild
./link-playplay.ps1 -Mode status    # junction + symbol + canonical-store state, in one call
```

Two things that are easy to get wrong:

- **Never point the helper at the main checkout's path.** The junction is created relative to the
  script's own `$PSScriptRoot`, so running the main checkout's copy by absolute path links the MAIN
  checkout, not the worktree you are standing in. Copy `link-playplay.ps1` into the new root (it is
  gitignored, so it is never inherited) and run it there.
- **No re-download is needed.** The provisioned runtime lives in the per-user canonical store at
  `%LOCALAPPDATA%\Wavee\playplay\runtimes\<appVersion>\<arch>\` — outside every checkout, so every
  worktree on the machine already shares it. `<appVersion>` is the *pack's own* `appVersion` pin —
  Spotify's numeric app version (`129300667` = 1.2.93.667) — **not** Wavee's `<InformationalVersion>`,
  so bumping the Wavee version does not invalidate it. The junction is the only per-checkout gap.

**The zero-setup escape hatch:** in the setup dialog, **Advanced options → Use installed Spotify**
points the app at the Spotify build already installed on the PC. No junction, no download, no
manifest authoring — the store recognizes a supported build by the DLL's hash and synthesizes the
rest. Use it when you just want *some* local playback rather than a reproducible dev setup.

When the dialog still refuses, **View diagnostics** (on its Failed footer, and on the
Settings → Playback problem banner) opens the `playback-diagnostics` page: whether local-playback
support is compiled into this build at all, every directory that was searched with dll/manifest
presence, the locate outcome + reason, and the verify result. That page is the first thing to read
before guessing.

## Next: Spotify as a module

The split above is the *first* half of a bigger move. Wavee now has a **playback-module system**
([playback-modules.md](./playback-modules.md)): a source ships as an independently updatable
out-of-process exe written against the public `Wavee.Sdk`, and the app reaches it through the ordinary
`MediaProviderRegistry` seam. Spotify playback is the migration that system was designed for — see
[playback-modules.md § Spotify as a module](./playback-modules.md#10-spotify-as-a-module) for the cut line
(what leaves, what stays), the byte flow across the process boundary (`stream/open|read|close` + binary frames,
so instant start survives), the host services the module calls back into (`host/auth/token`, `host/auth/context`,
`spotify/audioKey`, `host/secrets/*` — the AP socket stays app-side), and the M0/M1/M2 phasing.

The practical consequence for this document: in **M1** the private repo grows a `Wavee.Module.Spotify` that
references the `Wavee.Sdk` NuGet and absorbs the resolver, the head client, the key resolver, the cipher, the
audio stream and the PlayPlay deriver/provisioner/license client — the runtime store moves to
`WAVEE_MODULE_DATA_DIR\runtimes\…` and `module/status` drives the setup card generically. In **M2** the public
tree deletes its in-proc Spotify playback path outright (no fallback), and the `WAVEE_PLAYPLAY_LOCAL`
source-link plus `link-playplay.ps1` described above **go away with it** — the module ships as a zip
(`ops/build/pack-module.ps1`) that the app publish bundles like any other module. Until then, everything on this
page stands exactly as written.

## `playplay-runtime.json` field schema

The manifest sits beside a bare `Spotify.dll` and pins one build/arch. It is **never committed** —
`**/playplay-runtime.json` is gitignored and `no-drm-material.yml` fails the build if one is ever
tracked — which is why this is a prose table and not a template file. The authoritative shape is the
public record `PlayPlayRuntimeManifest` in
`src/apps/Wavee/Backend/Audio/Contracts/PlayPlayRuntimeManifest.cs`; property names serialize
camelCase.

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | int | Must be `1`. |
| `packId` | string | Pack identity; must end with `-<arch>` (`-arm64` / `-x64`). |
| `spotifyVersion` | string | The Spotify build the pins were derived from. |
| `clientVersion` | string? | Optional client-version string. |
| `appVersion` | string | Spotify's numeric app version for that build (`129300667` = 1.2.93.667). This is the `<appVersion>` folder in the canonical store — nothing to do with Wavee's own version. |
| `playPlayRequestVersion` | int | Wire request version. |
| `arch` | string | `Arm64` / `X64` (parsed case-insensitively into `Architecture`). |
| `algorithmVersion` | string | Which derivation algorithm the pins target; the selector owns the supported list. |
| `dllSha256` | string | 64 hex chars — the hard integrity gate over the *decompressed* DLL. |
| `playPlayTokenHex` | string | Hex token. |
| `config` | object | The per-build pin block (`PlayPlayRuntimeManifestConfig`): hex virtual addresses, the AES-key extraction descriptor, and the fixed buffer sizes. Its exact members are version-specific and are documented by that record, not here. |

The remote catalog (`PlayPlayRuntimeCatalogEntry`) is a **superset** of this: the same pins plus
`urls`, `compression` (`br` / `none`) and `downloadSize`. The app synthesizes the local manifest from
the chosen catalog entry *after* verifying the downloaded DLL — it never trusts the wire.

## Guardrails (do not commit out-of-scope material here)

- `.gitignore` ignores the whole out-of-scope surface (`src/apps/.native/`, `src/apps/Wavee.PlayPlay/`, `src/apps/tmp_*`,
  `ops/scripts/pyghidra*`, `ops/tools/{pyghidra*,playplay_*,x64_*}`, the mechanism docs, runtime payloads).
- **Enable the pre-commit guard once per clone:** `git config core.hooksPath .githooks`. It blocks staging
  any out-of-scope path or mechanism keyword (bypass only for a verified false positive with
  `git commit --no-verify`).
- CI (`.github/workflows/no-drm-material.yml`) is the backstop: it fails if such material is ever tracked.
- Agent fences: `.claude/settings.json` (Claude), `.codex/config.toml` + `.codexignore` (Codex),
  and the steering blocks in `CLAUDE.md` / `AGENTS.md`.
