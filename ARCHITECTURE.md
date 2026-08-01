# Architecture & reference

Deep-reference material for WaveeMusic: the repository layout, the full feature catalog, the technology
stack, agent guides, telemetry disclosure, and what's excluded from the public source tree. The
[README](README.md) is the front door; this document is for contributors and the curious.

## Repository structure

The solution is composed of nine first-party `src/` projects, four test suites under `test/`, and two
vendored libraries under `vendor/`. Each project owns a focused concern and has its own README with
developer-facing details.

```
WaveeMusic/
├── src/Wavee/                      Core protocol library (auth, Connect, audio orchestration, metadata)
├── src/Wavee.UI/                   Framework-neutral UI service layer (no XAML)
├── src/Wavee.UI.WinUI/             WinUI 3 desktop app (the headline client)
├── src/Wavee.Local/                Local media library (scan, classify, enrich, query, playback helpers)
├── src/Wavee.Controls.Lyrics/      Lyrics rendering library (D2D shaders, language detect, romanization)
├── src/Wavee.AudioHost/            Out-of-process audio runtime (BASS + NVorbis + PortAudio, x64-only)
├── src/Wavee.Playback.Contracts/   Shared IPC contracts between WinUI app and AudioHost
├── src/Wavee.Console/              AOT-compiled CLI client (Spectre.Console + Docker-friendly)
├── test/Wavee.Tests/               Core protocol library tests (xUnit v3 + librespot-verified crypto vectors)
├── test/Wavee.UI.Tests/            UI service layer tests (no WinUI)
├── test/Wavee.Local.Tests/         Local media classification / URI / subtitle tests
├── test/Wavee.PlayPlay.Tests/      PlayPlay decryption tests (x64 plain-Exe harness, not xUnit)
├── vendor/Lyricify.Lyrics.Helper/  Vendored: multi-provider lyrics search (QQ, Kugou, Netease, Apple, Musixmatch)
├── vendor/NVorbis/                 Vendored: managed Ogg Vorbis decoder
├── signing/                        Azure Artifact Signing scripts + LAF token coupling notes
└── Wavee.slnx                      Solution
```

### `src/Wavee` — core protocol library
[`README`](src/Wavee/README.md)

Clean-room reimplementation of every protocol the Spotify desktop client speaks: AP (TCP+TLS with
Diffie-Hellman + Shannon stream cipher), **Mercury** (legacy request/response over the AP), **Dealer**
(WebSocket bus for Connect cluster state / transfer / volume / remote commands), **SpClient**
(protobuf-over-HTTPS metadata API), **Pathfinder** (GraphQL for search / browse / home / profile),
**Login5** (OAuth backend), **AudioKey** (AES-128 key fetch for Ogg decryption), and the **gabo telemetry
envelope**. AOT-compatible — every IL2xxx / IL3xxx warning is treated as an error. No UI. Heavy
`System.Reactive` usage; most state is `IObservable<T>`. The `Session` type is the anchor of everything:
it connects to an AP, authenticates, and lazily initializes Dealer / Mercury / Pathfinder /
AudioKeyManager / EventService on first access — so a process that only needs metadata never opens a
Dealer WebSocket. See also [`Connect/DEALER_PROTOCOL.md`](src/Wavee/Connect/DEALER_PROTOCOL.md),
[`Connect/DEALER_IMPLEMENTATION_GUIDE.md`](src/Wavee/Connect/DEALER_IMPLEMENTATION_GUIDE.md),
[`OAuth/OAUTH_FLOWS.md`](src/Wavee/OAuth/OAUTH_FLOWS.md), and
[`Core/Crypto/README.md`](src/Wavee/Core/Crypto/README.md).

### `src/Wavee.UI` — framework-neutral UI service layer
[`README`](src/Wavee.UI/README.md)

Plain C# class library that sits between `Wavee` (protocol) and `Wavee.UI.WinUI` (XAML). No XAML, no
WinUI dependency — just contracts (`IPlaybackService`, `IPlaybackStateService`, `IUiDispatcher`, …), enums
(`RepeatMode`, …), models (`ArtistCredit`, `QueueItem`, `PlaybackContextInfo`, …), and services that need
to be testable in plain xUnit without booting WinUI.

### `src/Wavee.UI.WinUI` — desktop client
[`README`](src/Wavee.UI.WinUI/README.md)

The headline app. WinUI 3 / Windows App SDK 2.0, **single-project MSIX**, x86 / x64 / ARM64. Composition
flow on launch:

```
App.OnLaunched
  → AppLifecycleHelper.ConfigureHost()       builds IHost
  → Ioc.Default.ConfigureServices(...)       wires CommunityToolkit MVVM Ioc to the same container
  → Force-resolve IMetadataDatabase          runs schema migrations before first paint
  → MainWindow.Instance.Activate()           user sees the window
  → MainWindow.Instance.InitializeApplicationAsync()   deferred login state, library sync, …
```

Crash handlers are wired at three levels (XAML, AppDomain, unobserved-task scheduler) and funnel through
`LogUnhandledException` → `PiiRedactor` → `AppPaths.CrashLogPath`. Notable pages: `HomePage`,
`SearchPage`, `ArtistPage`, `AlbumPage`, `PlaylistPage`, `LikedSongsView`, `ArtistsLibraryView`,
`AlbumsLibraryView`, `LocalLibraryPage`, `VideoPlayerPage`, `ProfilePage`, `SettingsPage`, `ConcertPage`,
`DebugPage`, and the `ShellPage` shell. Reusable controls: `PlayerBar`, `SidebarPlayer`,
`ExpandedNowPlayingLayout`, `TrackItem`, `TrackDataGrid`, `ContentCard`, `LibraryGridView`,
`ExpandableAlbumGrid`, `SectionShelf`, `HeroHeader`, `Omnibar`, `Sidebar`, `QueueTabView`,
`RightPanelView`, `SpotifyConnectDialog`. Three custom MSBuild targets matter:

- **`BuildAudioHost`** — spawns an isolated `dotnet build` subprocess for `Wavee.AudioHost` with
  `Platform=x64` on every WinUI build. Necessary because a project reference would inherit the parent's
  project-evaluation cache and could land an ARM64 NVorbis.dll next to an x64-only AudioHost.
- **`RemoveDuplicateReferencedProjectAssets`** — removes WinUI's duplicate `<Content>` copies (~2.4 MB
  just for `Core14.profile.xml`). Carefully scoped — don't widen to the full `Wavee.Controls.Lyrics/`
  folder, which also contains compiled per-assembly XAML (`.xbf`).
- **`StripUnusedWindowsAiPayload` + AI workaround targets** — keeps only the Phi Silica payload
  (Microsoft.Windows.AI + ML + onnxruntime + DirectML), strips Image / Generative / ContentModeration
  projections, and works around a WinAppSDK 2.0.1 regression where managed AI projection assemblies don't
  deploy into AppX. Delete these workaround targets when the WinAppSDK / MSIX tooling fix lands.

The on-device AI surface (Copilot+ PCs only, opt-in by default, region-gated) sits in
`Services/AiCapabilities.cs` as a single composite gate (hardware + region + opt-in) that every AI
affordance binds against.

### `src/Wavee.AudioHost` — out-of-process audio runtime
[`README`](src/Wavee.AudioHost/README.md)

Separate `Exe` process, x64-only (because `Spotify.dll` is x86_64-native and must be loaded in-process for
PlayPlay key derivation). AOT-compatible. Audio stack: **ManagedBass** (decoder + mixer + DSP — EQ,
normalization, crossfade), **NVorbis** (managed Ogg Vorbis decoder, vendored), **PortAudioSharp2**
(cross-platform output), **z440.atl.core** (metadata). On first run, `NativeDeps/` downloads the missing
native binaries (`portaudio.dll` for ARM64, `bass.dll` for x64) into the runtime directory; failure exits
with code 3 so the UI can distinguish first-run setup failure from a transient crash. Talks to the WinUI
process over a named pipe with length-prefixed JSON. AudioHost has **zero project references on Wavee\*
assemblies** — the IPC contracts come in via `<Compile Include>`, eliminating a stale-DLL bug class.

### `src/Wavee.Playback.Contracts` — IPC contracts
[`README`](src/Wavee.Playback.Contracts/README.md)

Tiny library (~3 files) defining the wire protocol between WinUI and AudioHost: `IpcMessage` envelope,
command/event DTOs, `IpcPipeTransport` (length-prefixed JSON over named pipe —
`[4 bytes big-endian length][UTF-8 JSON payload]`), and `AudioFileCache`. Consumed two different ways: **as
a project reference** by `Wavee` (and transitively by `Wavee.UI.WinUI`), and **as source-included**
(`<Compile Include>`) by `Wavee.AudioHost`. Wire format is JSON so type identity across assemblies doesn't
matter. To add a new command/event, edit `IpcMessages.cs` only — both sides pick it up automatically.

### `src/Wavee.Controls.Lyrics` — synced lyrics rendering library
[`README`](src/Wavee.Controls.Lyrics/README.md)

WinUI 3 control library for time-synchronized lyrics with shader effects. Tech stack:
`Microsoft.Graphics.Win2D` for canvas + effects, `ComputeSharp.D2D1.WinUI` for AOT-friendly D2D
pixel/compute shaders, `SpoutDx.Net.Interop` for cross-process texture sharing, `NAudio.Wasapi` for audio
I/O during preview, `Vortice.Direct3D11` for DirectX interop, `NTextCat` + `Core14.profile.xml` (~2.4 MB
bundled) for 14-language identification, `csharp-pinyin` and `WanaKana-net` for CJK romanization.

### `src/Wavee.Local` — local media library
[`README`](src/Wavee.Local/README.md)

Framework-neutral, AOT-compatible, **no dependency on `Wavee.dll` or any UI**. Owns scan / classify /
index / enrich / query / edit for `wavee:local:{kind}:{hash}` URIs. Scope: watched-folder management,
filesystem scanning + metadata extraction (ATL.Net), content classification into Music / MusicVideo /
TvEpisode / Movie / Other, TV-series + season auto-grouping from filenames, subtitle discovery, embedded
audio/subtitle/video stream indexing, online metadata enrichment (TMDB for movies/TV, MusicBrainz + Cover
Art Archive for music), local lyrics, user collections, per-item metadata overrides, watched state +
resume position, and liked local tracks. Writes to the shared `Wavee.Core.Storage.MetadataDatabase`
(`Wavee.dll`) rather than owning its own SQLite database.

### `src/Wavee.Console` — AOT CLI client
[`README`](src/Wavee.Console/README.md)

Terminal Spotify Connect client built on the same `Wavee` core. Useful for headless control,
smoke-testing the protocol layer, and reproducing Connect bugs without the UI. `OutputType=Exe`, **Native
AOT**, Linux-friendly (ships a `Dockerfile`). Configures Serilog through a `SpectreUI` sink, sets up
DPAPI-backed `CredentialsCache`, runs OAuth if no stored credentials, and hands off to `ConnectConsole.cs`
for the interactive REPL.

### Test projects

- **`test/Wavee.Tests`** [`README`](test/Wavee.Tests/README.md) — xUnit v3 + FluentAssertions + Moq.
  Crypto primitives are validated against librespot (`ShannonCipher`: 28 tests, `AudioDecryptStream`: 9
  tests against librespot's Rust vectors).
- **`test/Wavee.UI.Tests`** [`README`](test/Wavee.UI.Tests/README.md) — doesn't reference
  `Wavee.UI.WinUI` deliberately, to keep MSIX/packaging/RID complexity out of the test build.
- **`test/Wavee.PlayPlay.Tests`** [`README`](test/Wavee.PlayPlay.Tests/README.md) — x64-only plain
  `Exe` harness (not xUnit) that loads `Spotify.dll`. Public clones get a stub `Program.cs`.
- **`test/Wavee.Local.Tests`** — tests for the classifier, filename parser, URI helpers, and subtitle
  discovery.

### Vendored libraries

- **`vendor/Lyricify.Lyrics.Helper`** [`README`](vendor/Lyricify.Lyrics.Helper/README.md) —
  multi-provider lyrics search + parsing (Lyricify Syllable/Lines, LRC, QRC, KRC, YRC, TTML, raw Spotify
  and Musixmatch JSON; search providers for QQ Music, NetEase, Kugou, SodaMusic, Apple Music, Musixmatch).
- **`vendor/NVorbis`** [`README`](vendor/NVorbis/README.md) — pure-managed Ogg Vorbis decoder, no
  P/Invoke, no unsafe code.

### `signing/`
[`README`](signing/README.md)

Azure Artifact Signing (formerly Trusted Signing) scripts for release MSIX builds. `Sign-Release.ps1` is
the one-command pipeline that swaps the manifest publisher to the release identity, builds + signs +
verifies + installs, then restores the dev publisher via try/finally (even on Ctrl-C). The same flow runs
in CI via `.github/workflows/release.yml`.

## Agent guides

`.agents/guides/` contains component-specific, LLM-friendly guides for the most cross-cutting subsystems.
Read the relevant guide before touching that area:

| Guide | Scope |
|---|---|
| `track-and-episode-ui.md` | Every track/episode row, list, card, search, queue, now-playing surface. |
| `connect-state.md` | Spotify Connect: dealer WebSocket, this-device announce, cluster state, remote commands, device picker / volume / now-playing UI. |
| `library-and-sync.md` | User library lifecycle: collection sync, playlist cache, dealer-driven incremental updates, save / pin / follow write paths, library UI. |
| `playback.md` | Playback runtime: orchestrator, queue + context, track resolution, AudioHost IPC, decode / decrypt / DSP / EQ, prefetch, local-file playback, video playback. |
| `queue.md` | Queue subsystem: `PlaybackQueue` three buckets, cursor + shuffle + repeat, autoplay rollover, cluster sync, drag-drop, context-menu Play next / Add to queue. |
| `composition-image.md` | `CompositionImage`: GPU-resident image primitive, cache lifecycle, LoadedImageSurface, suspension gate, retry behaviors. |
| `content-card.md` | `ContentCard`: reusable shelf / grid card across Home / Search / Browse / Library / Artist / Album / Show / Concert / Profile / Local-media. |
| `discography-expander.md` | Artist-page inline album expander: `ExpandableAlbumGrid`, `ExpandingGridLayout`, `AlbumDetailPanel` overlay. |

Conventions and the index live in `AGENTS.md` (single source of truth) and `CLAUDE.md` (Claude Code
instructions).

## Technology stack

| Component        | Technology                                                                 |
|-------------------|----------------------------------------------------------------------------|
| **Framework**     | .NET 10, C# preview                                                        |
| **UI**            | WinUI 3, Windows App SDK 2.0, CommunityToolkit, ReactiveUI                 |
| **Audio**         | BASS (DSP), NVorbis (Ogg), PortAudio (output) — out-of-process             |
| **Video**         | WebView2 EME, Windows Media Playback, Media Foundation                     |
| **Protocols**     | Protocol Buffers (Google.Protobuf), WebSocket, Mercury, ZStandard, Shannon |
| **Reactive**      | System.Reactive (Rx.NET)                                                   |
| **MVVM / DI**     | CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection / Hosting  |
| **Storage**       | SQLite (Microsoft.Data.Sqlite) for library / playlist cache                |
| **Logging**       | Serilog                                                                    |
| **Lyrics**        | ComputeSharp.D2D1.WinUI, NTextCat, csharp-pinyin, WanaKana                 |
| **On-device AI**  | Phi Silica via `Microsoft.Windows.AI.Text` (Copilot+ PC only, opt-in)      |
| **Signing**       | Azure Artifact Signing (release MSIX), self-signed cert for dev            |

## Full feature catalog

The [README](README.md) highlights are the short version; this is everything.

### Desktop app
- **Home, Browse, Search, Library** — personalized shelves, browse categories, liked songs, saved
  artists, saved albums, playlists, shows, and episodes.
- **Artist, Album, Playlist, Show, Episode pages** — discography, top tracks, biography, related content,
  credits, queue actions, and color extraction from art.
- **Browser-style tabs** with pin / drag-and-drop / context menus, a sidebar with rich navigation, and an
  omnibar for search, suggestions, and fast navigation.
- **Music videos** — Spotify music videos play through WebView2 EME with selectable quality and dedicated
  video controls.
- **Lyrics** — synced lyrics with shader effects, multi-language detection, and CJK romanization (pinyin /
  kana).
- **Now-playing surfaces** — compact player bar, expandable right-panel player, mini video player, full
  video page, and floating player window share the same playback surface.
- **Friends feed**, **profile**, **concerts**, in-app **settings** (theme, audio device, EQ, language,
  storage/network, diagnostics).
- **On-device AI on Copilot+ PCs** (opt-in) — explain a lyric line or summarize a song's themes with Phi
  Silica running locally on the NPU. Nothing leaves the machine; off by default.

### Coming soon after the initial alpha release
- **Local media library** — index audio, music videos, movies, TV shows, episodes, subtitles, and
  embedded tracks from disk; browse them alongside Spotify content.
- **Local metadata enrichment** — TMDB for movies / TV, MusicBrainz + Cover Art Archive for music, local
  artwork caching, thumbnails, watched state, resume position, custom collections, and local likes.
- **Spotify linking for local media** — link local music videos to Spotify tracks so playback, metadata,
  queue state, and now-playing identity stay connected.
- This code is present behind the `WAVEE_ENABLE_LOCAL_FILES` feature flag for internal testing, but it is
  disabled in the initial alpha release.

### Spotify Connect
- Full Dealer WebSocket implementation, real-time cluster state synchronization.
- Device picker for transferring playback between devices.
- Volume sync, queue updates, queue edits, remote command handling — works as both controller and target.
- This-device playback state is mirrored back into Connect so Spotify queue, device state, Recently
  Played, and UI surfaces stay in sync.

### Audio
- Audio runs in a separate process (`Wavee.AudioHost`) over named-pipe IPC, so audio engine crashes don't
  take the UI down.
- BASS for decode + DSP, NVorbis for Ogg Vorbis, and PortAudio for cross-arch output.
- Fast seek with byte-position bisection, predictive range prefetch, and decoder recreation fallback.
- Lazy progressive CDN streaming, head-file caching, audio key resolution, queue prefetch, and gapless /
  crossfade preparation.
- 10-band equalizer, normalization (loudness), compressor / limiter stages, volume control, and crossfade
  between tracks.

### Diagnostics and tooling
- Debug page for IPC health, audio process state, Connect-state capture, in-memory logs, memory pressure,
  and UI operation timing.
- Settings sections for diagnostics logging, Connect updates, cache/storage, network behavior, playback,
  audio output, and on-device AI readiness.

### Authentication
- OAuth 2.0 — both **Authorization Code with PKCE** (browser flow) and **Device Code** flow.
- Credentials cached encrypted on disk via Windows DPAPI, so you only sign in once.

### Architecture highlights
- **.NET 10** with Native AOT compatibility on the core library and console.
- **MVVM + DI** in the desktop app (`Microsoft.Extensions.DependencyInjection`, `CommunityToolkit.Mvvm`,
  `ReactiveUI`).
- **Single-project MSIX** packaging for x86 / x64 / ARM64.

## Telemetry (gabo events)

Wavee posts the bare minimum set of playback events Spotify's backend needs to credit your plays toward
Recently Played, play counts, and the "made for you" recommendations you already get from any official
client. Everything goes to `https://spclient.wg.spotify.com/gabo-receiver-service/v3/events/`. The legacy
`event-service/v1/events` path (both Mercury and HTTPS variants) returns 404 — gabo is the only working
transport, and our event surface is built around it.

### Events sent — playback only

| Event | When | What |
|---|---|---|
| `RawCoreStream` | At the end of each track | Track URI, context URI, ms played, reason started/ended, audio format. **This is the play-history event** — Recently Played and play counts both come from here. |
| `RawCoreStreamSegment` | Per pause/resume/seek split inside a track | Same playback id + segment ms range |
| `AudioSessionEvent` | When playback opens/seeks/closes | Session lifecycle markers |
| `BoomboxPlaybackSession` | Once per track | Buffering / resolve / setup latencies + duration |
| `Download`, `HeadFileDownload` | Per CDN fetch | File id, bytes, latency |
| `CorePlaybackCommandCorrelation` | When a play command runs | Maps command id → playback id |
| `ContentIntegrity` | Per track | Playback id + a flag stating we played in real-time (not ripping) |

### Events Wavee deliberately does NOT send

The desktop client sends these; we don't, because none of them are required to make Recently Played work:

- **Ad pipeline** — `AdEvent`, `AdRequestEvent`, `AdOpportunityEvent`, `AdSlotEvent`. Premium-only client,
  no ads.
- **UI interaction telemetry** — `DesktopUIShellInteractionNonAuth`, `WindowSizeNonAuth`.
- **System / driver fingerprinting** — `AudioDriverInfo`, `WasapiAudioDriverInfo`, `ModuleDebug`,
  `ConfigurationFetched`, `TimeMeasurement`, `ClientRuntimeDiag`.
- **Library / cache reports** — `LocalFilesReport`, `CacheReport`, `OfflinePruneReport`,
  `CollectionEndpointUsage`.
- Anything else from the 100+ event types defined in Spotify's binary.

### How it's wired

| File | Role |
|---|---|
| `src/Wavee/Connect/Events/EventService.cs` | Posts each `IPlaybackEvent` (one envelope per POST). Exposes `IObservable<IPlaybackEvent>` so in-process subscribers can mirror what's sent. |
| `src/Wavee/Connect/Events/GaboEnvelopeFactory.cs` | Builds the protobuf envelope. The per-event payload is one `EventFragment`; the rest are the **context block** — client id, installation id, application/device descriptors, time, SDK. |
| `src/Wavee/Connect/Events/IPlaybackEvent.cs` | Interface for one event type: `RawCoreStreamPlaybackEvent`, `RawCoreStreamSegmentPlaybackEvent`, `AudioSessionPlaybackEvent`, `BoomboxPlaybackSessionEvent`, `DownloadPlaybackEvent`, `HeadFileDownloadPlaybackEvent`, `CorePlaybackCommandCorrelationEvent`, `ContentIntegrityPlaybackEvent`. |
| `src/Wavee/Core/Http/SpClient.cs` (`PostGaboEventAsync`) | The actual HTTPS POST. |

### Mimicry of the desktop client (anti-fraud avoidance)

Spotify's anti-fraud pipeline drops batches whose context block doesn't look like a first-party client. To
stay below that bar, the envelope's `context_sdk` fragment uses the same `sdk_version_name` and
`sdk_type` strings the C++ desktop client emits, the `application_desktop` fragment carries the desktop
client's app version (`1.2.88.483` / version code `128800483`), and the device-context fragments use the
real machine's BIOS manufacturer/model + OS version + Windows machine SID. The breakthrough is documented
inline in `GaboEnvelopeFactory.cs`. If you change the SDK strings or version code, expect Recently Played
to silently stop working.

## What's not in this repo (proprietary)

Three Spotify-property files are deliberately excluded from the public source tree. The build still
compiles without them — stubs are provided where needed — but a few features are degraded or disabled.

| Excluded file | What it does | Effect of the stub |
|---|---|---|
| `src/Wavee/Core/Crypto/AudioDecryptStream.cs` | AES-128-CTR Big-Endian decryption for Spotify audio files (matches librespot's `audio/src/decrypt.rs`). Provides streaming decrypt with arbitrary seeking. | The file is excluded entirely (no stub). Test fixtures (`test/Wavee.Tests/Core/Crypto/…`) document what *would* be tested. Without it, you cannot decrypt encrypted Spotify Ogg streams in this repo's open form. |
| `src/Wavee/Core/Audio/PlayPlayConstants.cs` | Spotify-specific constants used to derive PlayPlay AES keys directly from `Spotify.dll`, used as a fallback when the AP audio-key channel returns a permanent error. | `PlayPlayConstants.Stub.cs` ships in its place; the runtime feature stays disabled. `AudioKeyManager` falls back to AP-only key resolution. |
| `src/Wavee.AudioHost/PlayPlay/PlayPlayKeyEmulator.cs` | The actual emulator that loads `Spotify.dll` (x86_64) in-process and exercises PlayPlay. | `PlayPlayKeyEmulator.Stub.cs` ships in its place. `Wavee.PlayPlay.Tests` runs against the stub and skips the real test vectors. |

**Why excluded:** these files reproduce Spotify's DRM (the AES decryption stream) and proprietary
key-derivation data (PlayPlay constants embedded in `Spotify.dll`). Both are part of Spotify's intellectual
property; we're not in a position to redistribute them. The connection-protocol layer (handshake, Shannon
cipher, packet framing) is fully open and included — see
[`Wavee/Core/Crypto/README.md`](src/Wavee/Core/Crypto/README.md) for the legal note.

**What still works without them:** authentication, session management, Spotify Connect (full controller +
target), all metadata APIs (SpClient + Pathfinder), the WinUI app's UI, Spotify library sync, search,
lyrics, Spotify music videos, Connect command issuance, telemetry, and the feature-flagged local media
code paths when enabled — everything except the actual decryption of Spotify-encrypted audio files. If
you need to play encrypted streams, you'll need to implement audio decryption yourself or obtain proper
licensing.
