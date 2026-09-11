# Wavee codebase structure overview

Generated 2026-09-11 by static analysis of the working tree (branch `feat/catalog-state`). Covers the
public app source only — **excludes** `src/apps/Wavee.PlayPlay` (a per-checkout symlink to the private
`wavee-playplay-private` repo — out of scope per `CLAUDE.md`), `bin/`, `obj/`, `.vs/`, and stale
`TestResults/*.trx` output. `src/apps/vendor/NVorbis` (third-party) is reported separately, not analyzed
as Wavee code.

Scope: `src/apps/{Wavee, Wavee.Core, Wavee.Sdk, Wavee.Tests, Wavee.ReleaseTool,
Wavee.Playback.IntegrationTests, modules/*}`.

## 1. Files & folders

| Metric | Count |
|---|---|
| Directories | 130 |
| Files (all types) | 1,727 |
| — `.cs` source files | 1,581 |
| — `.proto` (Spotify wire protocol defs) | 42 |
| — `.json` (localization, config, fixtures) | 36 |
| — `.csproj` / `.props` | 11 |
| — assets (`.bin .jpg .png .ico .ttf .otf .lrc .krc .ndjson`) | 46 |
| — other (`.md .py .ps1`) | 4 |

## 2. Lines of code (`.cs` only)

Counting method: a line is a **comment** if it's `//`, `///`, or falls inside a `/* … */` block; a line
with code trailing a block-comment close (`*/ code`) counts as code. Mixed code+trailing-`//`-comment
lines count as code (matches common `cloc`-style conventions). Blank/whitespace-only lines are counted
separately from both.

| Project | Files | Total lines | Code | Comments | Comment % | Blank |
|---|--:|--:|--:|--:|--:|--:|
| `Wavee` (app) | 888 | 219,719 | 153,855 | 46,850 | **21.3%** | 19,014 |
| `Wavee.Tests` | 535 | 111,270 | 87,260 | 9,606 | 8.6% | 14,404 |
| `Wavee.Core` | 112 | 16,120 | 11,085 | 3,213 | 19.9% | 1,822 |
| `Wavee.Sdk` | 24 | 5,084 | 3,233 | 1,246 | 24.5% | 605 |
| `modules/Wavee.Module.YouTube` | 5 | 2,394 | 1,372 | 711 | 29.7% | 311 |
| `modules/Wavee.Module.Twitch` | 4 | 955 | 622 | 195 | 20.4% | 138 |
| `Wavee.Playback.IntegrationTests` | 3 | 887 | 817 | 10 | 1.1% | 60 |
| `Wavee.ReleaseTool` | 6 | 767 | 595 | 99 | 12.9% | 73 |
| `modules/Wavee.Module.Radio` | 4 | 660 | 444 | 119 | 18.0% | 97 |
| **Total** | **1,581** | **357,856** | **259,283** | **62,049** | **17.3%** | **36,524** |

Notable: `Wavee.Tests` is nearly a third of the codebase by line count but has the lowest comment
density (8.6%) — mostly self-describing xUnit fact/theory bodies. `Wavee.Sdk` and the YouTube module
carry the highest comment density, consistent with them being the parts of the codebase meant to be
read by module authors rather than only maintained in-place.

### Largest files

| Lines | File |
|--:|---|
| 4,693 | `Wavee/Features/Detail/DetailTracks.cs` |
| 4,254 | `Wavee/Backend/PlaybackController.cs` |
| 3,152 | `Wavee/Features/Diagnostics/WaveeNavProbe.cs` |
| 3,062 | `Wavee/Features/Player/LyricsView.cs` |
| 2,911 | `Wavee/Features/Sidebar/Pane/SidebarPane.cs` |
| 2,366 | `Wavee/Features/Recents/RecentsPage.cs` |
| 2,353 | `Wavee/Features/Shell/WaveeShell.cs` |
| 2,142 | `Wavee/SpotifyLive/Audio/FluentMediaAudioHost.cs` |
| 2,024 | `Wavee.Core/Spotify/SpotifyExportMapper.cs` |
| 2,004 | `Wavee.Tests/SidebarLayoutReducerTests.cs` |
| 1,848 | `Wavee/App/PlaybackBridge.cs` |
| 1,789 | `Wavee/Features/Detail/LikedFactsPanel.cs` |
| 1,777 | `Wavee/Features/Sidebar/Pane/SidebarPaneSlot.cs` |
| 1,649 | `Wavee.Tests/ConnectControllerTests.cs` |
| 1,579 | `Wavee/Backend/Queries/QueryService.cs` |

## 3. Folder wireframe

`(N)` = `.cs` files directly in that folder (not counting subfolders).

```
src/apps/
├── Wavee/                              888 .cs · 219.7k lines — the app (FluentGpu UI, NativeAOT)
│   ├── Actions/                (22)    command palette: ActionId/ActionRules/ActionTarget registry
│   ├── App/                    (87)    composition root: bootstrap, OS bridges (SMTC, taskbar, jump
│   │                                   list), update/settings/setup policy, DI wiring (Services.cs)
│   ├── Backend/                (34)    service layer — business logic, engine-free contracts
│   │   ├── Audio/              (19)    + Contracts/            playback engine abstraction
│   │   ├── Catalog/            (22)                            catalog/query domain (batch fetch, demand)
│   │   ├── Collections/         (9)                            liked songs / saved collections
│   │   ├── Hydration/                  + Projectors/           model hydration pipeline
│   │   ├── Library/              (1)
│   │   ├── Lyrics/             (13)    + Lyricify/ Sources/    lyrics fetch/sync
│   │   ├── MediaSources/        (6)
│   │   ├── Metadata/            (2)
│   │   ├── Modules/            (11)                            3rd-party module host (Radio/Twitch/YT)
│   │   ├── Persistence/        (17)                            local disk cache / store.json family
│   │   ├── Playback/            (3)
│   │   ├── Playlists/          (21)                            playlist mutation + sync
│   │   ├── Queries/            (10)                            the catalog/query system (QueryService)
│   │   ├── Realtime/            (2)                            Spotify "dealer" push/connect state
│   │   ├── Residency/           (1)
│   │   ├── Spotify/            (18)                            Spotify-specific backend glue
│   │   ├── Sync/                (6)
│   │   └── Wiring/              (4)
│   ├── Components/             (29)    reusable UI atoms: TrackRow, MediaCard, Equalizer, RichText…
│   ├── Design/                 (16)    design tokens / theme surfaces
│   ├── Diagnostics/            (18)    always-on log lines, nav probe, diagnostics page backing
│   ├── Features/                       one folder per page/feature area
│   │   ├── Auth/                (5)    LoginView, QR pairing
│   │   ├── Browse/             (12)
│   │   ├── Concerts/           (16)
│   │   ├── Detail/             (68)    ★ largest feature: album/playlist/artist/track detail pages
│   │   ├── Diagnostics/        (13)
│   │   ├── DragDrop/            (4)
│   │   ├── Feedback/            (6)
│   │   ├── Home/                (26)   + Persistence/
│   │   ├── Library/             (5)
│   │   ├── Modules/             (2)
│   │   ├── Player/             (22)    + Deck/ (8)             now-playing panel, right rail, lyrics
│   │   ├── Recents/             (3)
│   │   ├── ReleaseNotes/       (14)
│   │   ├── Search/               (7)
│   │   ├── Setup/               (13)
│   │   ├── Shell/               (44)   app shell chrome, WaveeCommands, SettingsPage
│   │   └── Sidebar/             (15)   + Curated/ Data/ Modes/ Pane/ Persistence/ Shared/
│   │                                   (the sidebar extension platform — see wavee-sidebar skill)
│   ├── Platform/                 (6)   OS interop
│   ├── Properties/               (0)   assembly manifest
│   ├── SpotifyLive/             (50)   Spotify session/protocol implementation layer
│   │   ├── Audio/               (31)   + Runtime/              FluentMediaAudioHost, decode pipeline
│   │   ├── Catalog/              (4)                           SpotifyCatalogResourceProvider
│   │   ├── Gabo/                 (4)                           Spotify GABO client
│   │   ├── Herodotus/            (1)                           Spotify Herodotus event logging
│   │   ├── Hydration/                                          (nested files only)
│   │   └── Protos/                      42 .proto              generated protobuf contracts
│   └── assets/                          fonts, loc/*.json (en-US, ko-KR, nl…), deck/ media
│
├── Wavee.Core/                          112 .cs · 16.1k lines — pure domain layer, engine-free
│   ├── Auth/ Domain/(7) Fakes/(3, --fake demo data) Home/(3) Hydration/ Library/(14)
│   ├── Notifications/(16) Playback/(4) Reactive/ ReleaseNotes/(12) Sidebar/(9) Social/
│   └── Sources/(15) Spotify/(6) Versioning/(2) Catalog/(14) Diagnostics/
│
├── Wavee.Sdk/                            24 .cs ·  5.1k lines — module/extension contracts
│   ├── Http/(1) Protocol/(6) Streams/(5)
│
├── modules/                                                    pluggable content-source modules
│   ├── Wavee.Module.Radio/      (4)    implements Wavee.Sdk contracts
│   ├── Wavee.Module.Twitch/     (4)
│   └── Wavee.Module.YouTube/    (5)
│
├── Wavee.Tests/                         535 .cs · 111.3k lines — 6.6k+ xUnit tests
│   ├── (403 files at root) Actions/(11) ApiWaste/(2) Audio/(32) Backend/(19) Feedback/(9)
│   ├── Lyrics/(9) Modules/(17) Player/(10) ReleaseNotes/(12) Sdk/(6) Versioning/(1) Wiring/(4)
│   └── TestResults/*.trx        stale local run output (not source, excluded from LOC above)
│
├── Wavee.ReleaseTool/            (6)   standalone release CLI — depends only on Wavee.Core
├── Wavee.Playback.IntegrationTests/ (3) elevated E2E harness — depends on the full Wavee app
│
└── vendor/NVorbis/                      third-party Ogg Vorbis decoder — not analyzed above
```

## 4. Project dependency graph

Built from `<ProjectReference>` entries in every `.csproj` (ground truth, not inferred):

```
FluentGpu.Engine / Controls / Windows / WindowsApi / SourceGen   (sibling engine repo, ..\fluent-gpu)
        │
        ▼
┌──────────────────────────────────────────────────────────┐
│                          Wavee.csproj                     │◄── vendor/NVorbis (Ogg decode)
│           (the app — everything under src/apps/Wavee)     │
└──────────────────────────────────────────────────────────┘
        ▲                    ▲                    ▲
        │ ProjectReference   │                    │
   Wavee.Core            Wavee.Sdk         modules/Wavee.Module.{Radio,Twitch,YouTube}
   (no internal deps)    (no internal deps)        │
                               ▲──────────────────┘
                               │ (each module depends only on Wavee.Sdk)

Wavee.Tests.csproj  ──► Wavee.Core, Wavee.Sdk, all 3 modules, FluentGpu.WindowsApi, NVorbis
                        (does NOT reference Wavee.csproj — see §5)

Wavee.Playback.IntegrationTests.csproj ──► Wavee.csproj   (full-app E2E harness)

Wavee.ReleaseTool.csproj ──► Wavee.Core.csproj            (standalone; no UI/engine dependency)
```

`Wavee.Core` and `Wavee.Sdk` are the two dependency-free leaves — everything else builds on one or
both of them, never the reverse.

## 5. How the files actually talk to each other

Namespaces inside `Wavee.csproj` are **not uniform**: `Features/`, `Actions/`, `App/`, `Components/`,
`Design/`, `Diagnostics/`, `Platform/` mostly share the flat `namespace Wavee;` (538 files), while
`Backend/` and `SpotifyLive/` use real per-folder namespaces (`Wavee.Backend.Catalog`,
`Wavee.SpotifyLive.Audio`, …). That split lines up with actual `using`-graph measurements taken across
the tree:

| Direction | Files with the import | Reading |
|---|--:|---|
| `Features` → `Wavee.Core` | 219 | pages consume domain models directly and heavily |
| `Backend` → `Wavee.Core` | 114 | services are built on the same domain layer |
| `Features` → `Backend` | 28 | pages also call backend services, but less than they touch Core |
| `Backend` → `Features` | 0 | **one-directional** — backend never reaches into UI |
| `SpotifyLive` → `Backend` | 65 | SpotifyLive *implements* contracts Backend defines |
| `Backend` → `SpotifyLive` | 0 | Backend depends on abstractions, not the Spotify implementation |
| `App` → `Backend` | 12 | composition root wires backend services into the shell |
| `App` → `SpotifyLive` | 6 | composition root wires the Spotify implementation in |
| `Components` → `Backend` | 0 | UI atoms are pure — no backend coupling |

Net picture: a clean **dependency-inversion layering**, not a namespace hierarchy —

```
Wavee.Core  (domain models, no engine, no UI)
     ▲
     │
   Backend   (service layer; defines source/provider contracts — e.g. catalog, playback, lyrics)
     ▲
     │  implements Backend's contracts
 SpotifyLive  (Spotify protocol/session implementation of those contracts)
     ▲
     │  wired together at startup
    App     (composition root: Services.cs, bootstrap, OS bridges)
     ▲
     │  consumed by
 Features   (pages — consume Core domain + Backend services)
     ▲
     │  built from
Components  (pure, backend-free UI atoms: TrackRow, MediaCard, Rail…)
```

This is also why `Wavee.Tests` doesn't reference `Wavee.csproj` at all (§4): rather than pull in the
FluentGpu engine and the whole UI tree, its `.csproj` cherry-picks ~30 individual `<Compile Include>`
paths back into `Wavee/App`, `Wavee/Backend`, `Wavee/Features/*` for the specific engine-free
"pure decision" files (e.g. `PageRevealDecision.cs`, `DetailRailPolicy.cs`, `RecsRefetchPolicy.cs`,
`LikedFactsRules.cs`) — the pattern CLAUDE.md calls out under **"No source-text tests"**: business logic
gets extracted into a plain class specifically so it can compile into the test project without the
engine, and get tested without ever reading production source as text.

Other structural relationships worth knowing:
- **Modules are truly pluggable**: `Wavee.Module.{Radio,Twitch,YouTube}` depend only on `Wavee.Sdk`
  (never on `Wavee.Core` or `Wavee`), and `Wavee.csproj` pulls them in via an MSBuild item transform
  (`@(WaveeModule->'..\modules\Wavee.Module.%(Identity)\...')`) rather than one hardcoded reference each.
- **`Wavee/SpotifyLive/Protos`** holds 42 `.proto` files — the wire contracts that `Wavee.Sdk/Protocol`
  and `Wavee/Backend/Spotify` build request/response types around.
- **`Wavee.ReleaseTool`** is intentionally isolated from the UI stack (only references `Wavee.Core`) so
  the release pipeline can run without the engine or a display.
- **`Wavee/Features/Detail`** (68 files, 4,693-line `DetailTracks.cs` alone) and **`Wavee/Backend`**
  (34 root + ~170 across subfolders) are the two largest concentrations of logic in the app — Detail
  because every content type (album/playlist/artist/liked songs) funnels through shared track-table
  code, Backend because it's the seam between Core domain models and the Spotify-specific
  implementation underneath.
