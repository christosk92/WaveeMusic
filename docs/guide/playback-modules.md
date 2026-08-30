# Wavee playback modules — the SDK, the wire, and how to ship one

> **Scope: the Wavee app** (`src/apps/**`), not the FluentGpu engine. A *playback module* is an independently
> updatable program that teaches Wavee to play a new source — YouTube, Twitch, internet radio, and (next) Spotify
> itself. Modules are written against the public **`Wavee.Sdk`** package; the app never links a module's code.

The one-paragraph version: the app is NativeAOT with `TrimMode full`, so `Assembly.LoadFrom` does not exist in the
shipped binary and a managed plugin cannot be loaded in-process. A module is therefore an **out-of-process
executable** that speaks JSON-RPC 2.0 over stdio, and the SDK hides the transport completely — an author writes a
`WaveeModule` subclass and a one-line `Main`. That buys crash isolation (a bad module cannot take the app down), no
AOT/trim constraints on module authors (any NuGet is fair game), folder-replace updates with **no app restart**, and
a story that works unchanged on the macOS port. The SDK's abstractions are transport-agnostic, so an in-process
native transport could be added later without touching a single line of module code.

---

## Contents

1. [The shape of the system](#1-the-shape-of-the-system)
2. [Discovery and the store layout](#2-discovery-and-the-store-layout)
3. [The manifest](#3-the-manifest)
4. [The wire protocol](#4-the-wire-protocol)
5. [Author quick-start](#5-author-quick-start)
6. [Testing a module](#6-testing-a-module)
7. [Build, packaging and publish layout](#7-build-packaging-and-publish-layout)
8. [Diagnostics](#8-diagnostics)
9. [Module updates (designed now, built next)](#9-module-updates-designed-now-built-next)
10. [Spotify as a module](#10-spotify-as-a-module)
11. [Appendix — research notes](#11-appendix--research-notes)

---

## 1. The shape of the system

```
 profile menu  Play ▸ ──► PlayLinkDialog ──► ModuleRouter.MatchAsync(text)
                                                  │ (manifest urlPatterns prefilter → playback/match RPC)
                                                  ▼
                                 Track(uri = wavee:module:<id>:<b64url(playableId)>)
                                                  │ PlayTrackAsync (MediaForm.Video when form == video)
                                                  ▼
            MediaProviderRegistry ──► ModuleMediaProvider(id) ──► ModuleProcess (JSON-RPC/stdio) ──► module exe
                     │                      │ playback/resolve → ResolvedPlayable {media locator, isLive, title…}
                     │                      ▼
                     │      audio: url(progressive) → ExternalPlain          video/HLS: CompositeVideoResolver
                     │             url(icy/live)    → LiveHttpAudioStream       module tier → PopOutVideoSource
                     │             stream           → ModuleByteStream          → FluentVideoMediaHost → MF
                     ▼                                  (ICY titles → SetMetadataOverride)
          NowPlayingProjection.IsLive / title override ──► PlayerBar LIVE chip, seek disabled, SMTC
```

Three pieces, three owners:

| Piece | Lives in | What it is |
|---|---|---|
| **The SDK** | `src/apps/Wavee.Sdk/**` | The public contract: manifest + DTOs, the JSON-RPC framing/connection, `WaveeModule`, `ModuleRunner`, `ModuleTestHost`, `ModuleUri`, and the reusable byte-stream building blocks in `Wavee.Sdk.Streams`. **Zero project references** — not even `Wavee.Core`. It is the NuGet a third-party module consumes. |
| **The host** | `src/apps/Wavee/Backend/Modules/**` | Discovery and manifest validation (`ModuleCatalog` → `InstalledModule` / `ModuleRejection`), the child process and its lifecycle (`ModuleProcess`, `ModuleProcessState`, `ModuleTimeouts`, `ChildProcessChannel` behind the `IModuleChannel`/`ModuleSpawn` test seam), the façade the app composes (`ModuleHost`), one `IPlayableMediaProvider` per module (`ModuleMediaProvider`), the sync answer cache (`ModulePlayableCache` / `ModulePlayables`), paste-URL routing (`ModuleRouter`, `ModuleCapabilities`), module-served bytes (`ModuleByteStream`), the module→host service handlers (`ModuleHostServices`, `IModuleSecretStore`) and per-module counters (`ModuleStats`). |
| **The modules** | `src/apps/modules/Wavee.Module.{YouTube,Twitch,Radio}/**` | Three bundled first-party modules. Each is a `net10.0` `Exe` with `PublishAot=true`, references **only** `Wavee.Sdk`, and ships a `wavee-module.json` beside its entry point. |

Two rules the composition enforces (see `docs/plans/wavee/architecture.md §4.3`):

1. A source enters the app **only** through a registry. Playback goes through `MediaProviderRegistry` (first `Owns`
   wins, registration order is the routing table); nothing between play-intent and an audio/video host names a source
   type. The **Play ▸** menu is built from `ModuleHost.Installed`, never from a hard-coded list.
2. Capabilities are **declared, never probed**: the manifest's `capabilities` become `MediaProviderCaps`; a missing
   capability selects the simpler, proven path rather than a fallback probe.

### Trust boundary

v1 modules are **full-trust child processes** — the same posture the PlayPlay runtime already has. What the host
enforces anyway: the manifest is validated before anything is launched; `entry` must resolve *inside* the module
directory (path-traversal check) and the module directory must sit under one of the two roots below, otherwise the
manifest is rejected with a diagnostics row and the process is never spawned; each module gets its own data
directory; every host-service call is checked against the manifest's declared `permissions`; bundled modules are
first-party (`publisher == "wavee"`); and third-party installs (the marketplace phase) will require an explicit
untrusted-publisher confirmation, exactly like `SignatureTrust.Untrusted` does for the PlayPlay pack today.

---

## 2. Discovery and the store layout

The host probes two roots and takes the highest *compatible* version per id:

| Root | Purpose |
|---|---|
| `<app dir>\modules\<id>\wavee-module.json` | **Bundled** — shipped with the app, never removed, the version floor. |
| `%LOCALAPPDATA%\Wavee\modules\<id>\<version>\wavee-module.json` | **User store** — installed/updated out-of-band; highest compatible version wins. |

A module's private, writable directory is `%LOCALAPPDATA%\Wavee\modules-data\<id>` and is handed to the process as
`WAVEE_MODULE_DATA_DIR` (and as `ModuleContext.DataDir`). A module must write **only** there — its own install
directory is replaceable at any time by an update.

The process is launched with:

```
FileName          = entry ends with ".dll" ? "dotnet" : <module dir>\<entry>
Arguments         = --wavee-module --protocol 1
WorkingDirectory  = <module dir>
UseShellExecute   = false, CreateNoWindow = true, Redirect{StandardInput,StandardOutput,StandardError} = true
StandardOutputEncoding = UTF-8
env: WAVEE_MODULE_ID, WAVEE_MODULE_DATA_DIR, WAVEE_HOST_PID, WAVEE_HOST_VERSION
```

All module processes are assigned to one **job object** (`FluentGpu.WindowsApi/Shell/ChildProcessJob.cs`,
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) right after start, so they die with the app even if the app is killed. On
macOS the same protocol runs over `posix_spawn` + a process group + `SIGTERM` on exit.

**Lifecycle.** `Stopped → Starting (spawn + module/initialize, 5 s) → Ready → (idle 10 min → module/shutdown, 2 s
grace, kill) → Stopped`. Any exit or broken pipe while `Ready` moves to `Crashed`: in-flight requests fail with
`ModuleErrorCode.Transient`, and the next request restarts the process with backoff 1 s / 4 s / 16 s. After three
consecutive failed starts the module is `Faulted` — no auto-restart until the user presses **Retry** on the
diagnostics page or ten minutes pass. A process holding an open `stream/*` handle is never idle-stopped, and
requests that arrive while `Starting` queue behind the handshake.

**Timeouts** (per request, host side): initialize 5 s, match 5 s, resolve 20 s, `warm` is fire-and-forget,
`stream/open` 10 s, `stream/read` 10 s. On timeout the host sends `$/cancelRequest {id}`; if nothing comes back
within 2 s the process is killed and enters `Crashed`. Concurrent `resolve` calls for the same uri are deduped to
one in-flight task.

### Playable uris

A module owns the uri namespace `wavee:module:<id>:<base64url(playableId)>` — `ModuleUri.Prefix(id)`,
`ModuleUri.Encode(id, playableId)`, `ModuleUri.TryDecode(uri, out id, out playableId)`. The payload is base64url
with padding stripped, so it is colon-free and the uri splits unambiguously no matter what the module's private id
looks like.

---

## 3. The manifest

`wavee-module.json` sits next to the entry point. The authoritative shape is the record `ModuleManifest` in
`src/apps/Wavee.Sdk/ModuleManifest.cs`; property names serialize camelCase.

```json
{
  "schemaVersion": 1,
  "id": "wavee.youtube",
  "version": "1.0.0",
  "displayName": "YouTube",
  "publisher": "wavee",
  "protocolVersion": 1,
  "entry": "Wavee.Module.YouTube.exe",
  "capabilities": ["playback", "match", "metadata", "pages"],
  "urlPatterns": ["youtube.com", "youtu.be"],
  "menu": { "label": "YouTube…", "placeholder": "Paste a YouTube link" }
}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | Version of the manifest document itself (currently `1`), versioned **independently** of `protocolVersion`. |
| `id` | Stable id in `publisher.name` form — the same rules as `WaveeExtensionKey` (ASCII, ≤ 128 chars). Owns the `wavee:module:<id>:` uri namespace and the `modules\<id>\` directory. |
| `version` | Module version; the highest version per id wins during discovery. |
| `displayName` | Shown in menus and on the diagnostics page. |
| `publisher` | `"wavee"` marks a first-party bundled module; anything else is third-party (untrusted-confirmation on install). |
| `protocolVersion` | The wire version this module speaks. |
| `entry` | Executable (publish) or `.dll` (dev) to launch, **relative to the module directory** and never escaping it. |
| `capabilities` | Declared, not probed: `playback`, `match`, `metadata`, `pages`, `fallback`, `search`, `browse`. `pages` means the module answers `module/page` (see [Pages](#pages)). `search`/`browse` are *declared only* in v1 — a later pass maps them onto `ICatalogSource`/`IOnlineCatalog` through `SourceRegistry`. |
| `urlPatterns` | Host substrings used as a **cheap prefilter** before a process is spawned. A bare `http(s)` url with no pattern hit is offered to every module that declares `match`, with `fallback` modules (radio) last. |
| `menu` | Optional row in the profile menu's **Play ▸** submenu: `label` (or `labelLocKey`) plus the dialog's `placeholder`. |

---

## 4. The wire protocol

JSON-RPC 2.0 messages, **LSP-style framing** over the module's stdin/stdout. stderr is the module's log channel.

```
Content-Length: <N>\r\n
\r\n
<N bytes of UTF-8 JSON>
```

Both directions carry requests, responses and notifications. Ids are integers: **host-assigned positive** for
host→module, **module-assigned negative** for module→host, so they can never collide. `JsonRpcFramer` (frame
read/write) and `JsonRpcConnection` (id correlation, dispatch, cancellation) live in `Wavee.Sdk.Protocol` and are
the **same code on both sides** — the app host and `ModuleRunner` share them.

> **stdout is the protocol channel.** `ModuleRunner` captures the real stdout handle and then points
> `Console.Out` at **stderr** before any user code runs, so a stray `Console.WriteLine` anywhere in a module (or in
> a library it uses) cannot corrupt framing. stderr lines stream into `WaveeLog` under the category `module.<id>`
> at level Info; a line starting with `WARN ` or `ERROR ` maps to that level.

### Methods

| Direction | Method | Params → result |
|---|---|---|
| host→mod | `module/initialize` | `{hostVersion, minProtocol, maxProtocol, dataDir, locale, cacheBudgetBytes, prefs}` → `{protocolVersion, capabilities, manifest?}` |
| host→mod | `module/shutdown` | — (the process exits once it answers) |
| host→mod | `playback/match` | `{input}` → `MatchResult? {playableId, title?, form, isLive, confidence}` |
| host→mod | `playback/resolve` | `{playableId, prefs}` → `ResolvedPlayable` |
| host→mod | `playback/warm` | `{playableId}` — fire-and-forget pre-warm |
| host→mod | `stream/open` | `{streamId}` → `{handle, length?, seekable, contentType?}` |
| host→mod | `stream/read` | `{handle, offset, count}` → **binary frame** (below) |
| host→mod | `stream/close` | `{handle}` |
| host→mod | `module/page` | `{entityId}` → `ModulePageDoc?` — the page for one of the module's entities (capability `pages`); `null` = nothing to show |
| host→mod | `module/diagnostics` | — → `DiagnosticsReport {sections: [{title, rows: string[][]}]}` |
| host→mod | `module/action` | `{id}` → `{ok, message?}` |
| mod→host | `playback/metadata` (notification) | `{playableId, title?, artists?, artworkUrl?}` — live "now playing" corrections |
| mod→host | `playback/expired` (notification) | `{playableId}` — the locator died; the host re-resolves |
| mod→host | `module/status` (notification) | `{state: ready\|needsSetup\|error, message?, actions[]}` — drives the generic setup card |
| mod→host | `module/progress` (notification) | `{stage, percent}` |
| mod→host | `module/log` (notification) | `{level, message}` |
| mod→host | `host/auth/token` | `{provider, force}` → `{accessToken, expiresAtUnixMs}` — permission `auth.<provider>` |
| mod→host | `host/auth/context` | `{provider}` → `{deviceId, clientToken?, spclientBaseUrl?, session?}` |
| mod→host | `host/secrets/get` / `host/secrets/set` | `{key}` → `{value?}` / `{key, value}` — per-module, through the app's credential protector; permission `storage.private` |
| both | `$/cancelRequest` (notification) | `{id}` — surfaces as the handler's `CancellationToken` |

Every method name is a `const` on `Wavee.Sdk.Protocol.ModuleMethods`, so host and module cannot drift.

There is deliberately **no `host/http/proxy`**: modules own their own network.

### Binary frames (the audio path)

`stream/read` is answered with a **raw binary frame** — no base64, no JSON parse on the audio path:

```
Content-Length: <N>\r\n
Content-Type: application/octet-stream\r\n
X-Wavee-Request: <request id>\r\n
X-Wavee-Eof: 0|1\r\n
\r\n
<N raw bytes>
```

The framer treats any frame carrying `Content-Type: application/octet-stream` as binary and routes it by
`X-Wavee-Request` instead of parsing it as JSON. On the app side `ModuleByteStream : Stream, IAudioReadStream`
consumes it: ranged reads with a 256 KiB read-ahead, `KnownSize` from `length`, seek = a new offset, and
`AudioSourceKind.ModuleStream` on the handle. On the module side an author implements `IModuleStream`
(`Length`, `Seekable`, `ContentType`, `ReadAsync(offset, dst, ct)`) and returns it from
`WaveeModule.OpenStreamAsync`; `ModuleRunner` owns the handle table and disposes everything on shutdown.

Bandwidth sanity check: 320 kbps Ogg is 40 KB/s and FLAC ≈ 175 KB/s, so 64 KiB frames are one RPC per 0.4–1.6 s of
audio — three orders of magnitude below pipe throughput. The only latency that matters is the *first* read.

### Errors

A failure is a JSON-RPC error `{ code, message, data: { kind, retryAfterMs?, detail? } }` where `kind` is a
`ModuleErrorCode`:

| Code | Meaning | Host behaviour |
|---|---|---|
| `NotOwned` (1001) | This module does not own the input | router moves on |
| `Unavailable` | Geo-blocked shape aside: private, removed, SABR-only, offline service | typed failure, message shown |
| `NeedsAuth` | Sign-in / subscription required | typed failure, message shown |
| `Transient` | Try again shortly | typed failure, message shown — the user retries (the 1 s/4 s/16 s ladder above restarts the PROCESS; no request is auto-retried) |
| `Offline` | The thing exists but is not live right now | typed failure ("stream offline") |
| `GeoBlocked` | Not available in this region | typed failure |
| `Unsupported` | The module cannot do this at all | typed failure |

`-32601` (method not found) is **never** a failure: the host reads it as "capability absent" and takes the simpler
path. Throwing `ModuleException(code, message)` from any handler produces the right error shape; an unhandled
exception is turned into an error response too — a module never crashes the loop by throwing.

### Version negotiation

The manifest carries `protocolVersion`. `module/initialize` params carry the host's supported range
`{minProtocol, maxProtocol}`; the module replies with the version it will speak (always inside that range) plus its
effective capability list — `ModuleProtocol.Negotiate(hostMin, hostMax)` in the SDK picks the highest version both
sides speak, or fails the handshake when the ranges do not overlap.

**Additive changes never bump the version:** unknown JSON members are ignored on both sides by
`System.Text.Json`, and new methods are discovered through capabilities. A breaking change bumps the version, and
the host keeps N−1 for one app release. `schemaVersion` (the manifest document) is versioned separately.

---

## 5. Author quick-start

A module is a console `Exe` that references `Wavee.Sdk` and ships a manifest. In this repo the three bundled
modules use a `ProjectReference`; out-of-repo authors use the NuGet.

**`Wavee.Module.LoFi.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Wavee.Sdk" Version="0.1.0" />
    <Content Include="wavee-module.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

**`Program.cs`** — the whole module:

```csharp
using Wavee.Sdk;

internal sealed class LoFiModule : WaveeModule
{
    public override ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
        => new(input.Contains("lofi.example", StringComparison.OrdinalIgnoreCase)
            ? new MatchResult(input, "Lo-fi radio", MediaForm.Audio, IsLive: true, Confidence: 0.9)
            : null);

    public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        => new(new ResolvedPlayable(
            playableId, "Lo-fi radio", ["lofi.example"], ArtworkUrl: null,
            DurationMs: 0, IsLive: true, MediaForm.Audio,
            MediaLocator.FromUrl("https://lofi.example/stream.mp3", MediaLocator.ContainerIcy, "audio/mpeg"),
            ExpiresAtUnixMs: null, Caps: []));
}

internal static class Program
{
    private static Task<int> Main(string[] args) => ModuleRunner.RunAsync<LoFiModule>(args);
}
```

**`wavee-module.json`**

```json
{ "schemaVersion": 1, "id": "example.lofi", "version": "1.0.0", "displayName": "Lo-fi",
  "publisher": "example", "protocolVersion": 1, "entry": "Wavee.Module.LoFi.exe",
  "capabilities": ["playback", "match"], "urlPatterns": ["lofi.example"],
  "menu": { "label": "Lo-fi…", "placeholder": "Paste a lo-fi stream link" } }
```

That is a complete, working module. Everything else is optional surface on `WaveeModule`:

| Member | Default | Use it for |
|---|---|---|
| `InitializeAsync(ModuleContext, ct)` | no-op | read `ctx.DataDir` / `ctx.Prefs` / `ctx.CacheBudgetBytes`, open caches |
| `MatchAsync(input, ct)` | `null` | claim pasted text; return `null` to decline |
| `ResolveAsync(playableId, ct)` | **abstract** | the one required member |
| `ResolveAsync(playableId, prefs, ct)` | forwards to the above | honour quality / metered / crossfade preferences |
| `WarmAsync(playableId, ct)` | no-op | pre-fetch on hover / prepared-next |
| `OpenStreamAsync(streamId, ct)` | `null` | serve bytes yourself (`IModuleStream`) instead of handing over a url |
| `GetPageAsync(entityId, ct)` | `null` | describe one of your entities as a page the app renders |
| `GetDiagnosticsAsync(ct)` | empty | rows for the app's diagnostics page |
| `InvokeActionAsync(actionId, ct)` | no-op | handle a button you offered through `ModuleStatus` |
| `ShutdownAsync(ct)` | no-op | flush caches |

`Host` (an `IModuleHost`) is the app as the module sees it: `DataDir`, `Log(level, message)`,
`PublishMetadata(update)`, `PublishExpired(playableId)`, `PublishStatus(status)`, `PublishProgress(stage, percent)`,
plus the permission-gated host services `GetTokenAsync`, `GetAuthContextAsync`, `GetSecretAsync`, `SetSecretAsync`
and the escape hatch `CallAsync<TParams,TResult>(method, …)` for anything else (e.g. `"spotify/audioKey"`).

**AOT rule.** Every JSON shape a module serializes must go through an STJ source-generated context of its own —
the SDK's own DTOs already do (`SdkJsonContext`). `CallAsync` takes the `JsonTypeInfo<T>` for both sides for
exactly that reason.

### What `ResolveAsync` must return

`ResolvedPlayable(playableId, title, artists, artworkUrl, durationMs, isLive, form, media, expiresAtUnixMs, caps,
gainDb = 0, wire = null)`:

- `durationMs: 0` means **unknown** — that is what a live stream returns, and it keeps every ending-soon / gapless /
  crossfade arm off.
- `form` picks the host: `MediaForm.Audio` → the house audio mixer, `MediaForm.Video` → Media Foundation (which
  renders audio too, so HLS-with-audio lives here).
- `media` is either `MediaLocator.FromUrl(url, container, contentType, headers)` with `container` ∈
  `progressive` / `hls` / `icy`, or `MediaLocator.FromStream(streamId, contentType)` for module-served bytes.
  **The video host cannot set request headers** — never resolve an HLS url that needs them.
- `expiresAtUnixMs` makes the host schedule a re-resolve *before* the locator dies (one `SetSource` swap instead of
  an error), and re-resolve on a network fault past that instant.
- `caps` are per-playable tokens (`preparedNext`, `connectPublish`, `wireMeta`) that map 1:1 onto
  `MediaProviderCaps`; absent = the simpler proven path.
- `wire` (a `WireMeta`) feeds `PlaybackTrackMeta` → Connect / playback attribution. Only modules that declare
  `wireMeta` populate it.
- `pageEntityId` / `subtitleEntityId` are the two link slots on the player bar and the stage: the **art tile and the
  title** navigate to `pageEntityId` (the playable's own page), the **subtitle** to `subtitleEntityId` (its channel,
  station or show). Both default to `null`, which leaves the link inert — exactly what a module without the `pages`
  capability wants. See [Pages](#pages).

### Pages

A module can describe its own entities — a video, a channel, a station — as a **page the app renders**. Because
modules are out-of-process and untrusted by construction, a page is never code and never markup: it is a small
declarative document, the same posture the sidebar extension platform takes for contributed content.

Declare the capability `pages` in the manifest, then override one method:

```csharp
public override ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct)
```

**Entity ids are yours.** They are module-namespaced strings and the app never parses them — YouTube uses
`video:<id>` and `channel:<id>`, Twitch `channel:<login>`, Radio `station:<url>`. The app routes one as
`module:` + `ModuleUri.Encode(moduleId, entityId)`, so two modules can both call something `channel:x` without
colliding. Return `null` for an id you do not recognise; throw `ModuleException` for one you recognise but cannot
serve right now.

**The document.**

| Type | What it is |
|---|---|
| `ModulePageDoc(Version, Template, Hero?, Actions[], Sections[], ExpiresAtUnixMs?)` | `Template` is `"entity"` (hero + actions + sections), `"custom"` (sections only) or `"watch"` (the video-first layout — see below). `ExpiresAtUnixMs` bounds the app's cache; `null` takes the 10-minute default. |
| `PageHero(Title, Eyebrow?, Subtitle?, ImageUrl?, MetaLine?, IsLive, AvatarUrl?, SubtitleEntityId?)` | The identity block. `IsLive` draws the LIVE badge. `AvatarUrl` is the OWNER's picture (a channel avatar) — distinct from `ImageUrl`, the entity's own artwork, because a watch page shows both at once. `SubtitleEntityId` is the entity `Subtitle` navigates to. |
| `PageAction(Id, Kind, Label, PlayableId?, Url?, Primary)` | `Kind` ∈ `play` (resolves `PlayableId` the normal way) / `openUrl` (http(s) only) / `moduleAction` (comes back as `module/action` with `Id`). At most one `Primary`. |
| `PageSection(Kind, Title?, Text?, Rows?, Items?, Extra?)` | `Kind` ∈ `text` / `facts` (`Rows` = `[label, value]` pairs) / `playables` / `cards` / `links`. **An unknown kind is skipped, not an error** — and its unknown members survive in `Extra`, so a newer module can ship a section an older app cannot draw yet. |
| `PageItem(Title, Subtitle?, ImageUrl?, PlayableId?, EntityId?, Url?, Form?, IsLive, Meta?)` | One entry. `PlayableId` plays it, `EntityId` navigates to another page **of the same module**, `Url` opens the browser. |

A page that wants to link onward to another page of the same module sets `PageHero.SubtitleEntityId`; before that
field existed the only way was a one-card `cards` shelf carrying a `PageItem.EntityId`, which still works and is what
an older module falls back to.

**The watch template.** `Template = "watch"` says *this entity's identity IS its picture* — a video, a live stream.
The app then draws the same document differently: a full-width 16:9 stage pinned at the top, showing the **live video
itself** once that entity is the playing item and a poster plus one play affordance before that; then a caption column
— title, the LIVE pill and meta line, a channel row built from `Subtitle`/`AvatarUrl`/`SubtitleEntityId`, the actions
as capsule chips, a description card whose bold first line is the `facts` section's values, and a 16:9 shelf from the
`playables` (or `cards`) section. While that stage is live the right rail gives its docked video card back and shows
the queue instead, because the app has exactly **one** video surface.

It is a REQUEST, not a requirement: `Template` is a plain string and an app that does not know `"watch"` falls back to
the entity layout with every section still drawn, so a module may emit it before every app understands it. Emit it
only when the entity really is video-first — a radio station with no picture reads better as an entity page, which is
why the Radio module does not.

**Budgets are rejections, not truncations.** `ModulePageBudget.Validate` runs on both sides of the wire and throws
`ModuleException(Unsupported)` when a document exceeds **40 sections**, **500 items** (section entries plus fact
rows), **64 KiB per serialized section** or **2 MiB per serialized document**. It never trims: a page silently
rendered half-way is a bug report nobody can diagnose, while a typed error names the module and the limit it blew.

**Be honest about what you actually know.** The bundled modules are deliberately thin where the upstream API is:

- **YouTube** builds `video:<id>` as a `"watch"` page from two InnerTube calls made **concurrently on the page path
  only** (never on resolve, so playback latency is untouched): `/player` gives the thumbnail, title, channel, LIVE
  badge, lifetime view count and `shortDescription`; `/next` adds the owner **avatar**, the live **"N watching now"**
  count (`videoDetails.viewCount` is lifetime only) and the related-videos shelf. `/next` uses the WEB client — it
  returns metadata, never streams, so the SABR/JS bans that rule WEB out of `/player` do not apply — and **any
  failure of it costs only the enrichment**: the page still renders everything `/player` knew. Counts and dates are
  YouTube's own rendered strings, verbatim; nothing is computed or invented. There is still no channel endpoint for a
  JS-less client, so `channel:<id>` shows only what a resolve happened to learn (its name, the cached avatar, and a
  "Live now" row when that broadcast is on air) plus *Open on YouTube*, makes no extra request, and says so in as
  many words. No invented shelves.
- **Twitch** builds `channel:<login>` from the persisted `StreamMetadata` query: profile image, display name, stream
  title, category and viewer count, *Play* when live (omitted when not) and *Open on Twitch*. When live it is a
  `"watch"` page — the stream preview is the stage's poster and the profile image is the avatar, two pictures that
  previously had to share one slot. Offline it stays an entity page: there is no picture to stage.
- **Radio** builds `station:<url>` from the station's own ICY headers — name, genre, bitrate, format, description and
  the `icy-url` website. There is deliberately **no "now playing" row**: ICY titles arrive interleaved in the audio
  body, which only the *app* demuxes, so the module would be guessing. The app overlays the live title from its own
  projection instead.

---

## 6. Testing a module

**Module tests never spawn a process.** `ModuleTestHost` is an `IModuleHost` that calls the module class directly
and records everything it published:

```csharp
var module = new LoFiModule();
var host = new ModuleTestHost(module, dataDir: tempDir);
await host.InitializeAsync();

MatchResult? match = await host.MatchAsync("https://lofi.example/stream");
Assert.NotNull(match);

ResolvedPlayable resolved = await host.ResolveAsync(match!.PlayableId);
Assert.True(resolved.IsLive);
Assert.Equal(MediaLocator.ContainerIcy, resolved.Media.Container);

Assert.Empty(host.Logs.Where(l => l.Level == ModuleLogLevel.Error));
```

`ModuleTestHost` also exposes `Metadata`, `Expired`, `Status`, `Progress`, `Logs` (all recorded lists) and lets a
test stand in for the host services via `TokenProvider`, `AuthContextProvider`, `SecretReader`/`SecretWriter` /
`Secrets`, and `CallHandler`. `host.PageAsync(entityId)` drives `GetPageAsync` and runs the answer through
`ModulePageBudget.Validate`, so a fixture test trips the same ceiling the wire would.

Network never happens in a module's unit tests: put the module's `HttpClient` behind an injectable
`HttpMessageHandler` and drive it from **recorded JSON fixtures** (sanitized). The three bundled modules are tested
that way in `src/apps/Wavee.Tests/Modules/`.

The **host's** own tests drive an in-memory `JsonRpcConnection` pair against a scripted fake module (including one
that serves a local file over `stream/open|read|close`), so the real framing, id correlation, cancellation and
binary-frame paths are exercised without a process. One integration test spawns the real Radio module exe behind
`[Trait("Category","Integration")]`.

### CLI subcommands (manual testing without the app)

`ModuleRunner` only speaks the protocol when it is launched with `--wavee-module`. Without it, it is a small CLI
over the *same* code paths `ModuleTestHost` drives:

```powershell
.\Wavee.Module.YouTube.exe match "https://www.youtube.com/watch?v=tRsQsTMvPNg"
.\Wavee.Module.YouTube.exe resolve "tRsQsTMvPNg"
.\Wavee.Module.YouTube.exe page "video:tRsQsTMvPNg"
```

The JSON answer goes to **stdout**; logs and errors go to stderr. Exit code `0` = found, `1` = not found / failed,
`2` = bad usage. Host services are not available in CLI mode (they throw `Unsupported`) — that is deliberate, since
there is no host.

### Attach-to-debug

The diagnostics page lists each module's PID; attach with the IDE. For a dev build the entry is a `.dll`, so the
process you attach to is `dotnet Wavee.Module.X.dll` running out of `…\bin\Debug\net10.0\modules\<id>\`.

---

## 7. Build, packaging and publish layout

### Dev (`dotnet run --project src/apps/Wavee`)

`Wavee.csproj` carries a `ProjectReference` to each module with `ReferenceOutputAssembly="false"` and
`Private="false"` — it exists purely to **order the build** (no module assembly ever links into the app) and is
guarded by `Condition="Exists(…)"` so a checkout without the modules still builds. The `CopyBundledModules` target
(`AfterTargets="Build"`) then stages each module's framework-dependent output:

```
src\apps\modules\Wavee.Module.YouTube\bin\<Configuration>\net10.0\**   →   $(TargetDir)modules\wavee.youtube\
src\apps\modules\Wavee.Module.Twitch \bin\<Configuration>\net10.0\**   →   $(TargetDir)modules\wavee.twitch\
src\apps\modules\Wavee.Module.Radio  \bin\<Configuration>\net10.0\**   →   $(TargetDir)modules\wavee.radio\
```

so a debug run finds `src\apps\Wavee\bin\Debug\net10.0\modules\wavee.youtube\wavee-module.json`.

**The manifest, twice.** A `dotnet build` produces a framework-dependent `Wavee.Module.YouTube.dll`; a
`dotnet publish -r <rid>` produces a NativeAOT `Wavee.Module.YouTube.exe`. Each module therefore checks in two
files with disjoint copy destinations — no code-generating target:

| File | `CopyToOutputDirectory` | `CopyToPublishDirectory` | `entry` |
|---|---|---|---|
| `wavee-module.dev.json` (`TargetPath="wavee-module.json"`) | `PreserveNewest` | `Never` | `Wavee.Module.YouTube.dll` |
| `wavee-module.json` (the canonical, publish-shaped one) | `Never` | `PreserveNewest` | `Wavee.Module.YouTube.exe` |

Either way exactly one `wavee-module.json` lands beside the entry point, and the host launches
`dotnet Wavee.Module.YouTube.dll` in dev (`dotnet` is on a dev box by definition) or the exe directly in a publish.
**One host code path; the manifest decides.**

The YouTube module additionally ships `clients.json` (the InnerTube client table) to **both** output and publish:
that table is data, not code, because YouTube retires client versions every few weeks, and a same-named file in
the module's data dir overrides it at runtime.

### Publish (`ops\build\publish-wavee-aot.ps1`)

After the app is published, the script publishes each module **NativeAOT, self-contained** into the app's publish
directory:

```
<publish>\Wavee.exe
<publish>\modules\wavee.youtube\Wavee.Module.YouTube.exe   + wavee-module.json (entry = "Wavee.Module.YouTube.exe")
<publish>\modules\wavee.twitch \Wavee.Module.Twitch.exe    + wavee-module.json
<publish>\modules\wavee.radio  \Wavee.Module.Radio.exe     + wavee-module.json
```

The loop lives in `ops\build\publish-wavee-modules.ps1` (`-OutDir <app publish dir> -Rid win-arm64|win-x64
[-Configuration Release] [-NoAot]`), which both `publish-wavee-aot.ps1` and `pack-wavee-msix.ps1` call so the two
layouts cannot drift. It skips a module whose csproj is absent and fails loudly if a published module has no
`wavee-module.json`.

### MSIX (`ops\build\pack-wavee-msix.ps1`)

The MSIX script does its own `dotnet publish` into `.msix-build\wavee-<arch>\publish`, calls the same module
publish into `…\publish\modules\<id>\`, and then stages the package layout with `Copy-Item "$pubDir\*" $layout
-Recurse -Force` — so the modules land inside the package unchanged. A packaged full-trust Win32 app may launch
executables from inside its own package, which is exactly what the host does. (`.pdb` files are stripped from the
layout, modules included.)

### Distributing one module (`ops\build\pack-module.ps1`)

A module is distributed as a **zip of its directory** (manifest + exe + data files) plus a sha256 sidecar:

```powershell
powershell -File ops\build\pack-module.ps1 `
  -ModuleDir src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\modules\wavee.youtube `
  -OutputDir artifacts\modules -Arch arm64
```

produces `artifacts\modules\wavee.youtube-1.0.0-arm64.zip` and `…zip.sha256`, and prints the feed entry
(`id`, `version`, `protocolVersion`, `arch`, `sha256`, `size`) ready to paste into a `modules.json`. The same
script packs this repo's three modules and the playplay repo's Spotify module.

### 🤖 AGENT — where to change what

| If you're changing… | Edit |
|---|---|
| The wire (a method, a DTO, framing) | `src/apps/Wavee.Sdk/Protocol/**` — and both sides at once; they share this code |
| The author surface (`WaveeModule`, `IModuleHost`) | `src/apps/Wavee.Sdk/{WaveeModule,IModuleHost,ModuleRunner}.cs` |
| The page document (a section kind, a budget) | `src/apps/Wavee.Sdk/ModulePage.cs` — and the app renderer in `src/apps/Wavee/Features/Modules/ModulePage.cs` |
| Discovery / process lifecycle / routing | `src/apps/Wavee/Backend/Modules/**` |
| A bundled module's behaviour | `src/apps/modules/Wavee.Module.<Name>/**` (+ fixtures in `src/apps/Wavee.Tests/Modules/`) |
| The dev copy layout | `CopyBundledModules` in `src/apps/Wavee/Wavee.csproj` |
| The publish/MSIX layout | `ops/build/publish-wavee-modules.ps1` (called by `publish-wavee-aot.ps1` + `pack-wavee-msix.ps1`) |
| The solution | `src/FluentGpu.slnx` (`/Apps/` folder) |

---

## 8. Diagnostics

Everything is on the existing **playback-diagnostics page** (setup dialog → *View diagnostics*), in a **Modules**
section:

- **Per installed module:** id, display name, version, publisher, directory (bundled vs user store), declared
  capabilities and protocol version, process state (`Stopped`/`Starting`/`Ready`/`Crashed`/`Faulted`), PID, and the
  last error.
- **Per module counters** (`ModuleStats`): requests, failures, p50/p95 latency in ms, restarts.
- **Every probed directory that did NOT load, and why** — the PlayPlay diagnostics discipline: a rejected manifest
  is a visible row with its reason (bad schema, id mismatch, entry outside the module directory, protocol range
  mismatch, unreadable JSON), never a silent skip.
- **Module-supplied rows**: whatever `module/diagnostics` returned, rendered generically, plus any
  `module/status` actions as buttons (with confirmation when the module asked for it).

Module stderr is in the normal log under the category `module.<id>`.

There are **no environment switches**. Diagnostics are always on and always rendered; nothing about module
behaviour is gated on an env var.

---

## 9. Module updates (designed now, built next)

The PlayPlay provisioner is already a one-publisher marketplace; the module updater generalizes it.

**Store layout** (this pass): bundled `<app>\modules\<id>\` is the floor and is never removed; the user store is
`%LOCALAPPDATA%\Wavee\modules\<id>\<version>\` with an `active.json` pointer per id. The highest *compatible*
`protocolVersion` wins. `ModuleCatalog` already resolves this.

**Feed** (next plan): `modules.json`

```json
{ "schemaVersion": 1,
  "modules": [ { "id": "wavee.youtube", "version": "1.1.0", "protocolVersion": 1, "arch": "arm64",
                 "urls": ["https://…/wavee.youtube-1.1.0-arm64.zip"], "sha256": "…", "size": 5242880,
                 "compression": "none", "publisher": "wavee", "signature": null } ] }
```

— the `PlayPlayRuntimeCatalog` shape. `IModuleProvisioner` mirrors `IPlayPlayProvisioner`'s members parameterized by
module id: fetch → `SelectBest` → download with progress → **sha256 hard gate over the decompressed bytes** →
Authenticode **advisory** (`SignatureTrust`; untrusted needs an explicit confirmation) → staged directory → atomic
rename → pointer flip. Rollback: if the new version fails `module/initialize` three times, the pointer reverts to
the previous version.

Because modules are out-of-process, an update applies at the module's next (re)start — **no app restart**. A
background check on launch (mirroring the `.appinstaller` 8 h cadence) plus a Settings → Modules page (installed /
available / update / remove; first-party vs third-party publisher trust). The marketplace is the same feed with
many publishers; third-party defaults to untrusted-confirmation, exactly the PlayPlay signature posture.

---

## 10. Spotify as a module

Spotify playback is **designed for** by this system and moves out of this repo next, into the private playplay repo
(`docs/guide/playplay-private-split.md`). This section is the plan of record.

### The cut line

| Component (public tree) | After migration |
|---|---|
| `SpotifyMediaProvider`, `LiveTrackResolver`, `FastTrackPlayback`, `HeadFileClient`, `AudioKeyResolver` / `LiveAudioKeySource`, `SpotifyAesCtr` / `SpotifyCtrCipher`, `SpotifyAudioStream`, `LicenseKeyDiskCache`, the `IPlayPlay*` seams + runtime provisioning | **module** (`wavee.spotify`, playplay repo) |
| `RangedHttpSource` (+ `RangeSet`, `RangedHttpRecoveryPolicy`), `AudioBodyDiskCache` | **`Wavee.Sdk.Streams`** (public: `RangedHttpSource`, `RangeSet`, `RangedHttpRecoveryPolicy`, `ChunkDiskCache`) — used by the app *and* by modules |
| `PlainHttpAudioStream`, `LocalFileAudioStream`, `SkipStream` | stay in the app, over the SDK types |
| `FluentMediaAudioHost` mixer, decoders, gapless/crossfade, device-rate reload, WASAPI | stay; the `SpotifyEncrypted` branch, `BuildDecryptor`, `DetectSkipOffset` and `_nativeDecryptorFactory` are deleted |
| `ApConnection` / login / token providers / `SessionContext` / credentials | **stay** — that is the *session*, not playback; Connect, catalog, search and library all need it. Exposed to the module through host services. |
| Connect (`ConnectStateBuilder`, `track_player`, `play_origin`), Gabo, Herodotus, `ResumePointProjection` | **stay**, unchanged — computed from `PlaybackEvent` + `ResolvedPlayable.Wire` |

### Byte flow across the process boundary

```
app: PlayTrackAsync(spotify:track:X)
  → MediaProviderRegistry → ModuleMediaProvider("wavee.spotify")
  → playback/resolve {playableId:"spotify:track:X", prefs:{quality, metered, crossfadeMs}}
       module: extended metadata → pick file (quality rung) → returns IMMEDIATELY
         ResolvedPlayable { form:audio, durationMs, gainDb, media:{kind:"stream", streamId:"<fileIdHex>"},
                            wire:{…}, caps:[preparedNext, connectPublish, wireMeta] }
  → stream/open {streamId} → {handle, length, seekable:true, contentType:"audio/ogg"}
       module: head GET (80 KB, instant) ∥ storage-resolve + audio key
  → stream/read {handle, offset:0, count:65536} → binary frame (clear head bytes, logical 0 past the 0xa7 header)
       … the decoder starts within the same ~100 ms it does today; ModuleByteStream keeps 256 KiB ahead.
  → seeks = stream/read at a new offset; prepared-next = a second stream/open;
    device-rate soft reload = stream/open again for the same streamId at the same offset.
```

### Host services the Spotify module needs

| Method (module→host) | Permission | Returns | Backed by |
|---|---|---|---|
| `host/auth/token {provider:"spotify", force}` | `auth.spotify` | `{accessToken, expiresAtUnixMs}` | the app's token / force-token providers |
| `host/auth/context {provider:"spotify"}` | `auth.spotify` | `{deviceId, clientToken, spclientBaseUrl, session{account, market, catalogue, locale, tier, explicitFilter}}` | the live spclient + `SessionContext` |
| `spotify/audioKey {fileIdHex, trackGidHex}` | `auth.spotify` | `{key}` or `Unavailable` | `ApConnection.RequestAudioKeyAsync` — the AP socket stays app-side; this is the one Spotify-named host method |
| `host/secrets/get\|set {key}` | `storage.private` | bytes | the app's credential protector |
| `module/status` / `module/diagnostics` / `module/action` / `module/progress` | — | the generic setup card + diagnostics rows | replaces the PlayPlay-specific provisioner UI seam |

`module/initialize` also carries `{dataDir, cacheBudgetBytes, locale, prefs}`, so the module's `ChunkDiskCache`
honours the app's body-cache setting without a Spotify-specific seam. The module makes its own spclient HTTP calls
with the token/client-token from `host/auth/*`; `Wavee.Sdk.Http.AuthenticatedHttp` attaches both headers and
re-asks the host on a 401.

### Phases

- **M0 — this pass, main repo.** The SDK, the host, the three modules, `Wavee.Sdk.Streams`, `stream/*` + binary
  frames + `ModuleByteStream` + `AudioSourceKind.ModuleStream`, `ResolvePreferences`, `ResolvedPlayable.{GainDb,
  Wire}`, the host services above, `module/status|diagnostics|action|progress`, and the pre-login provider registry.
  Exit criterion: a test module served from a local Ogg file plays through `ModuleByteStream` with seek,
  prepared-next hand-off and device-rate reload.
- **M1 — playplay repo.** `Wavee.Module.Spotify` referencing the `Wavee.Sdk` NuGet; the resolver, head client, key
  resolver, cipher and `SpotifyAudioStream` (→ `IModuleStream`, logical offset 0 past the `0xa7` header) move with
  their tests. The runtime pack store moves to `WAVEE_MODULE_DATA_DIR\runtimes\…` and `module/status` drives the
  setup card.
- **M2 — main repo, after M1 ships.** The in-proc Spotify playback path is **deleted**, not kept as a fallback:
  `AudioStreamHandle` becomes a neutral `MediaHandle`, `IFastTrackResolver`/`IFastTrackWarmer` fold into
  `IPlayableMediaProvider`, `LocalPlaybackSupported` means "an audio host exists", and the Spotify module ships
  bundled like the other three (absent → the app builds without Spotify playback and says so on the diagnostics
  page, the same honest posture `NullPlayPlayProvisioner` has today).

**Risks specific to the move.** Instant start depends on the module answering `stream/read(offset 0)` from its head
buffer before body resolution — keep the parallel head/body open behaviour and measure against the current
`InstantStartTests` timings across the process boundary (target: +≤ 30 ms). AP audio keys go through
`spotify/audioKey`, and the session-wide "AP disabled" latch moves into the module. Ciphertext at rest stays in the
module's `ChunkDiskCache`; the app never persists decrypted Spotify audio (`ModuleByteStream` is memory-only).

---

## 11. Appendix — research notes

Four sections of multi-agent web research (2026-08-22) that the three bundled modules are built from, kept here
verbatim so they survive the session that produced them. **They are a snapshot**: the YouTube client table
especially churns every few weeks (`android_vr` went from "no PO token" to "all 403" in six weeks), which is exactly
why the YouTube module is an out-of-process, independently updatable module with its client table in a **data
file** rather than in code.

One deliberate disagreement with the research below: the Media Foundation report suggests **not** sending
`Icy-MetaData: 1`. Wavee **does** send it — the in-app `IcyDemuxer` strips the interleaved blocks, and live titles
are the whole point of the radio now-playing line.

---

*Appendix A — verbatim research output: YouTube.*

## YouTube implementation contract (snapshot 2026-08-22, yt-dlp master / 2026.08.19)

### 1. URL → videoId
Video id charset: `[0-9A-Za-z_-]{11}`. Accept (hosts: `*.youtube.com`, `youtube-nocookie.com`, `youtubekids.com`, `youtube.googleapis.com`, `youtu.be`):

| Pattern (regex) | Note |
|---|---|
| `[?&#!]v=(?<id>[0-9A-Za-z_-]{11})` on `/watch`, `/watch_popup`, `/movie` | standard |
| `youtu\.be/(?<id>[0-9A-Za-z_-]{11})` | short |
| `/(?:v\|embed\|e\|shorts\|live)/(?<id>[0-9A-Za-z_-]{11})` | exclude `videoseries`, `live_stream` |
| naked `^[0-9A-Za-z_-]{11}$` | optional |

Channel-live pages (must be resolved to a watch id by fetching HTML with a desktop browser UA):
- `/(?:@|c/|channel/|user/)?[^/?]+/live/?$`, and `/embed/live_stream?channel=UC…` → rewrite to `https://www.youtube.com/channel/{UC…}/live`.
- Parse, in order: `"currentVideoEndpoint":{...,"watchEndpoint":{"videoId":"<id>"}}` (yt-dlp) → else `<link rel="canonical" href="https://www.youtube.com/watch?v=<id>">` (streamlink). Neither found → **UserNotLive** ("channel is not live").
- A redirect to `consent.youtube.com` must be handled (streamlink POSTs the consent form) — see open questions.

### 2. InnerTube player request
`POST https://www.youtube.com/youtubei/v1/player?prettyPrint=false` — **no API key** ("By default, no API key is used").

Headers (for every client):
```
Content-Type: application/json
User-Agent: <client userAgent below>
X-YouTube-Client-Name: <numeric id below>
X-YouTube-Client-Version: <clientVersion below>
Origin: https://www.youtube.com
```
(`X-Goog-Visitor-Id` optional — omit when unknown. No cookies, no Authorization.)

Body (JS-less clients: **no** `signatureTimestamp`, **no** `serviceIntegrityDimensions`, **no** `params`):
```json
{
  "videoId": "<id>",
  "context": { "client": { /* client block */ , "hl": "en", "timeZone": "UTC", "utcOffsetMinutes": 0 } },
  "playbackContext": { "contentPlaybackContext": { "html5Preference": "HTML5_PREF_WANTS" } },
  "contentCheckOk": true,
  "racyCheckOk": true
}
```

Client blocks, in fallback order (stop at the first that yields `playabilityStatus.status` in {OK, LIVE_STREAM_OFFLINE} with matching `videoDetails.videoId` and a `hlsManifestUrl`):

**1. visionos (primary)** — id `101`
```json
{"clientName":"VISIONOS","clientVersion":"1.02","deviceMake":"Apple","deviceModel":"RealityDevice17,1","osName":"visionOS","osVersion":"26.5.23O471",
 "userAgent":"Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15"}
```
No JS, no PO token (as of 2026-08-22). Caveats: "Made for kids" videos → UNPLAYABLE/ERROR; no legacy itags 17/18/22; AV1 only over https.

**2. android (fallback)** — id `3`
```json
{"clientName":"ANDROID","clientVersion":"21.26.364","androidSdkVersion":30,"osName":"Android","osVersion":"11",
 "userAgent":"com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip"}
```
HLS PO-token policy `required=False, recommended=True`; https/DASH require GVS PO token (we don't use them). Wiki marks android "SABR Only" for https — irrelevant for HLS.

**3. ios (last resort)** — id `5`
```json
{"clientName":"IOS","clientVersion":"21.26.4","deviceMake":"Apple","deviceModel":"iPhone16,2","osName":"iPhone","osVersion":"18.3.2.22D82",
 "userAgent":"com.google.ios.youtube/21.26.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)"}
```
HLS policy `required=True` — "HLS Livestreams require POT 30 seconds in". Use only when the others fail, and tell the user playback may stop after ~30 s.

Do **not** use: `web`/`web_safari` (SABR-only, need JS + GVS POT), `tv`/`tv_downgraded`/`web_embedded` (need the JS player for signatureCipher + `/n/<x>/` manifest challenge), `android_vr` (all formats 403 since 2026-08-17).

### 3. Response fields to read
- `playabilityStatus.status`, `.reason`, `.desktopLegacyAgeGateReason` (presence = age gate).
- `videoDetails.videoId` — **must equal the requested id**, else treat the response as invalid (yt-dlp: "Your IP is likely being blocked") and try the next client.
- `videoDetails.{title, author, channelId, lengthSeconds, viewCount, isLive, isLiveContent, isUpcoming, isPostLiveDvr, isLiveDvrEnabled, isLowLatencyLiveStream, latencyClass, isPrivate, thumbnail.thumbnails[].{url,width,height}, shortDescription}`.
- `microformat.playerMicroformatRenderer.liveBroadcastDetails.{isLiveNow, startTimestamp, endTimestamp}` (ISO-8601).
- `streamingData.expiresInSeconds` (string, typically `"21540"`), `streamingData.hlsManifestUrl` (→ `https://manifest.googlevideo.com/api/manifest/hls_variant/...`), `streamingData.dashManifestUrl` (ignore), `streamingData.serverAbrStreamingUrl` (presence with no `hlsManifestUrl` = SABR-only session → error).
- Live status (yt-dlp `_list_formats` logic): `post_live` if `isPostLiveDvr`; `is_live` if `isLive` (fallback `liveBroadcastDetails.isLiveNow`); `is_upcoming` if `isUpcoming`; `was_live` if `isLiveContent`; else `not_live`.
- VOD pre-roll wait (only when not live): sum of `adPlacements[].adPlacementRenderer` with kind `AD_PLACEMENT_KIND_START` / `adSlots[].adSlotRenderer` with `triggerEvent == "SLOT_TRIGGER_EVENT_BEFORE_CONTENT"` → `...instreamVideoAdRenderer.playerVars` `length_seconds` (or `skipOffsetMilliseconds`). Delay SetSource by that many seconds (cap it and show "waiting for ad window").

### 4. The manifest URL
- `hls_variant` master → per-itag `api/manifest/hls_playlist/.../playlist/index.m3u8` media playlists. Live variants: muxed TS H.264+AAC itags 91–96 (144p–1080p30), 300/301 (720p60/1080p60) and aliases 269/229/230/231/311/312; audio-only 233 (mp4a.40.5 64k) / 234 (mp4a.40.2 144k) whose segments are **raw ADTS**. `#EXT-X-VERSION:3`, `EXT-X-MEDIA-SEQUENCE`, `EXT-X-DISCONTINUITY-SEQUENCE`, `EXT-X-PROGRAM-DATE-TIME`, no ENDLIST, `playlist_type/DVR`, `playlist_duration/3600` (1 h served window), ~5 s segments, chunked segment responses (`noclen/1`), 302s between `rr---sn-*` hosts. No LL-HLS tags.
- Path params: `expire/<unix>` (parse `/expire/(\d+)/`), `ip/<client-ip>` inside signed `sparams` → IP-bound, not UA-bound in practice (yt-dlp fetches with a random Chrome UA).
- **Expiry handling:** `refreshAt = min(expire, now + expiresInSeconds) - 600 s`. At `refreshAt`, re-run §2 and cache the new URL. On `MF_MEDIA_ENGINE_EVENT_ERROR` (NETWORK) or when the cached URL is expired, SetSource the fresh one and seek to the live edge (live restarts at the edge; the gap is a few seconds). Re-resolve also if the public IP changed.
- **Preflight:** GET the master playlist once with Wavee's HttpClient (any desktop UA). 403 → re-resolve/next client; non-`#EXTM3U` body → error.

### 5. Error mapping (`playabilityStatus.status` / `.reason`)
| Condition | Wavee error |
|---|---|
| `OK`, `hlsManifestUrl` present | play |
| `OK` but no `hlsManifestUrl`, `serverAbrStreamingUrl` present | "YouTube served SABR-only for this client" → next client → unsupported |
| `LIVE_STREAM_OFFLINE` (+`isUpcoming`, `startTimestamp`) | "Stream offline / scheduled for <start>" |
| `LOGIN_REQUIRED`, reason contains "bot" ("Sign in to confirm you're not a bot") | "YouTube is blocking this network (VPN/datacenter IP)" — do not retry other clients, same result |
| `LOGIN_REQUIRED` otherwise, `AGE_CHECK_REQUIRED`, `AGE_VERIFICATION_REQUIRED`, reason matches `confirm your age|age-restricted|inappropriate` | "Age-restricted; sign-in not supported" |
| `UNPLAYABLE` / `ERROR` (incl. "made for kids", "not available on this app") | try next client; final → show `reason` verbatim |
| `videoDetails.videoId != id` | invalid response → next client; final → "IP blocked by YouTube" |
| HTTP 403 on manifest/segments after play started | if past `expire` → reload with cached fresh URL; else next client |

> **Superseded by what shipped (2026-08-23).** Two rows above are wrong and the module does not implement them.
> *(a)* "do not retry other clients, same result" was falsified the same day it was written — VISIONOS walled while
> ANDROID served the same stream from the same IP — so a wall is a next-client row. It is now capped at **one**
> alternate, because a wall that walks the whole table spends three flagged requests on one user action. *(b)* The
> "VPN/datacenter IP" wording was deleted outright: measured over a real session, VISIONOS walled on **9 of 9**
> attempts on an ordinary connection, and all three clients walled together only under load, recovering on their own.
> The shipped classification (`YouTubeWallPolicy`) is therefore two verdicts — *rate-limited, try again* (`Transient`)
> and *challenged as a bot* (`Unavailable`) — neither of which claims the user's network is a datacenter, and neither
> of which promises that signing in helps, because nothing here has ever tested that. *(c)* A bare `LOGIN_REQUIRED`
> with no age wording is a wall, **not** an age gate; the old row made it terminal `NeedsAuth`.

### 6. Risks (ranked)
1. **Client table volatility** — all version strings and the "visionos needs no PO token" claim are a 2026-08-22 snapshot; maintainers expect visionos to get PO-token enforcement like android_vr did (2026-07 → 2026-08-17). Keep client blocks in a data table, not code, so they can be updated out-of-band.
2. **visionos live `hlsManifestUrl` is attested only by maintainer descriptions** (wiki PR #81, issue #17226), not a quoted log — the android fallback covers it.
3. **PO tokens are per-video, per-session experiments** ("Detected experiment to bind GVS PO Token to video ID"); behaviour differs between IPs on the same day; a no-JS app cannot mint BotGuard/DroidGuard/iOSGuard tokens. Manifest-path injection point if a token ever becomes available: `.../pot/<token>` inserted before `/file/index.m3u8` or `/playlist/index.m3u8`.
4. **SABR/UMP spreading** (web SABR-only since 2025-04; tv DRM/SABR; android_vr >1.65 SABR) — one day every client may return only `serverAbrStreamingUrl`.
5. **Mid-stream 403s** (issue #16796, now reported on visionos too) and 6 h expiry → reactive reload is mandatory.
6. **IP-bound URLs** — VPN/IPv4↔IPv6 changes 403.
7. **VOD pre-roll waiting period** (yt-dlp `available_at`) and VOD HLS instability since late 2025 → VOD is best-effort.
8. **Made-for-kids** content unplayable on visionos; yt-dlp's fallbacks (web_embedded/tv_downgraded) need JS, so Wavee cannot follow.
9. **Channel-page scraping** (consent redirect, HTML shape of `currentVideoEndpoint`) is the most brittle parsing in this feature.

---

*Appendix B — verbatim research output: Twitch.*

## Twitch implementation contract (snapshot Aug 2026, streamlink 8.5.0 / yt-dlp master)

### 1. URL → login / vodId
| Kind | Regex | Notes |
|---|---|---|
| live | `^https?://(?:(?:www\|go\|m)\.)?twitch\.tv/(?<login>(?!v(?:ideos?)?/\|clip/\|videos/)[^/?#]+)/?(?:[?#].*)?$` and `player\.twitch\.tv/\?.*?\bchannel=(?<login>[^&#]+)` | lowercase `login` for the usher path |
| VOD | `twitch\.tv/(?:[^/]+/v(?:ideo)?\|videos)/(?<vodId>\d+)` and `player\.twitch\.tv/\?.*?\bvideo=v?(?<vodId>\d+)` and `twitch\.tv/[^/]+/schedule\?vodID=(?<vodId>\d+)` | |
| clips (`clips.twitch.tv/…`, `/clip/…`) | reject in v1 | |

### 2. GQL access token
`POST https://gql.twitch.tv/gql`

Headers:
```
Client-ID: kimne78kx3ncx6brgo4mv6wki5h1ko
Content-Type: application/json
User-Agent: <a current desktop Chrome UA>      (custom/odd UAs caused "server error", streamlink #6574)
```
No `Device-Id`, no `Client-Integrity`, no `Authorization` (anonymous). (Report 4 notes streamlink also sends `Referer`/`Origin: https://player.twitch.tv`; harmless to add.)

**Primary body — inline anonymous query (no persisted hash to rotate; yt-dlp form):**
```json
{"query":"{ streamPlaybackAccessToken(channelName: \"<login>\", params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"site\"}) { value signature } }"}
```
VOD: `{ videoPlaybackAccessToken(id: \"<vodId>\", params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"site\"}) { value signature } }`
Read `data.streamPlaybackAccessToken.{value,signature}` / `data.videoPlaybackAccessToken.{value,signature}`.

**Fallback body — persisted query (streamlink 8.x; hash confirmed valid 2026-01-22):**
```json
{"operationName":"PlaybackAccessToken",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9"}},
 "variables":{"isLive":true,"login":"<login>","isVod":false,"vodID":"","playerType":"embed","platform":"site"}}
```
(VOD: `isLive:false, login:"", isVod:true, vodID:"<id>"`.) Same response paths.

Second fallback: the inline query with yt-dlp's Client-ID `ue6666qo983tsx6so1t0vnawi233wa` and `Content-Type: text/plain;charset=UTF-8`.

`value` is a JSON string; decode it for: `expires`, `authorization.{forbidden,reason}`, `geoblock_reason`, `chansub.restricted_bitrates[]`, `show_ads`, `server_ads`, `hide_ads`, `user_ip`, `channel_id`. Anonymous tokens always carry `show_ads:true` regardless of playerType.

### 3. Usher (multivariant playlist URL handed to MF)
Live (primary, what twitch.tv uses since ~Feb 2026):
```
GET https://usher.ttvnw.net/api/v2/channel/hls/{login_lower}.m3u8
  ?sig=<signature>&token=<urlencoded value>
  &allow_source=true&allow_audio_only=true&playlist_include_framerate=true
  &supported_codecs=h264&platform=web&p=<random int 1000000..9999999>
```
- Omit `fast_bread=true` (it adds `#EXT-X-TWITCH-PREFETCH` lines and Twitch LL behaviour MF does not understand).
- `supported_codecs=h264` only: h265/av1 "enhanced broadcasting" renditions are fMP4 + `EXT-X-MAP` + large PTS offset, which MF cannot play.
- Fallback on HTTP 4xx/5xx (not on a JSON error body): legacy `https://usher.ttvnw.net/api/channel/hls/{login}.m3u8` with the same query (yt-dlp still uses it; additionally `allow_spectre=true&player=twitchweb` are what yt-dlp sends).

VOD (best-effort, same MF path):
```
GET https://usher.ttvnw.net/vod/v2/{vodId}.m3u8?nauthsig=<signature>&nauth=<value>&allow_source=true&allow_audio_only=true&playlist_include_framerate=true&supported_codecs=h264&platform=web&p=<random>
```
(streamlink form; yt-dlp uses `/vod/{id}.m3u8?sig=&token=`. Reports disagree on which param names are required — follow streamlink for v2 and retry with `sig`/`token` on 4xx.)

### 4. Master playlist notes (preflight with Wavee's HttpClient)
- v2 shape: `#EXT-X-SESSION-DATA:DATA-ID=...` lines (MANIFEST-NODE, SERVER-TIME, BROADCAST-ID) instead of the v1 `#EXT-X-TWITCH-INFO:...` tag; `#EXT-X-STREAM-INF` carries `IVS-NAME="720p60"|"480p"|"audio_only"`, `BANDWIDTH`, `CODECS="avc1.…,mp4a.40.2"`, `RESOLUTION`, `FRAME-RATE`; `#EXT-X-MEDIA` **may be absent** — derive names from `IVS-NAME` (streamlink PR #6847), never depend on `EXT-X-TWITCH-INFO` or `EXT-X-MEDIA GROUP-ID`.
- v1 shape: `#EXT-X-TWITCH-INFO`, then `#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)"` + `#EXT-X-STREAM-INF:...VIDEO="chunked"`; `audio_only` is `GROUP-ID="audio_only"` (~160 kbit), still offered in 2026.
- Media playlist URLs: `video-weaver.<pop>.hls.ttvnw.net/v1/playlist/...` (live), `vodNNN-ttvnw.akamaized.net|d2nvs31859zcd8.cloudfront.net/.../chunked/index-dvr.m3u8` (VOD). Media playlists: `EXT-X-VERSION:3`, ~2 s TS segments, `EXT-X-PROGRAM-DATE-TIME`, `EXT-X-TWITCH-ELAPSED-SECS/-TOTAL-SECS/-LIVE-SEQUENCE`, `EXT-X-DISCONTINUITY` at ad boundaries.
- Ads (SSAI, unavoidable anonymously): `#EXT-X-DATERANGE:ID="stitched-ad-<n>",CLASS="twitch-stitched-ad",START-DATE=...,DURATION=...,X-TV-TWITCH-AD-ROLL-TYPE="PREROLL"|"MIDROLL",...`; ad `#EXTINF` titles contain `Amazon`. Ad segments may be **fMP4** while content is TS (why streamlink now always filters). MF fetches media playlists itself, so v1 cannot filter; on `MF_MEDIA_ENGINE_ERR_DECODE` during a Twitch session, reload the same usher URL (new token not needed until `expires`).
- Token/URL are short-lived and pin `user_ip`; on ERROR after `expires`, redo §2+§3.

### 5. Channel metadata (optional, for title/now-playing/offline detection)
Primary — persisted `StreamMetadata` (updated by both yt-dlp and streamlink 2025-11-10/11):
```json
{"operationName":"StreamMetadata",
 "extensions":{"persistedQuery":{"version":1,"sha256Hash":"b57f9b910f8cd1a4659d894fe7550ccc81ec9052c01e438b290fd66a040b9b93"}},
 "variables":{"channelLogin":"<login>","includeIsDJ":true}}
```
Read `data.user.displayName`, `data.user.stream` (null ⇒ offline), `.stream.id`, `.stream.game.name`, `.stream.previewImageURL`, `data.user.lastBroadcast.title`, `data.user.broadcastSettings.title` (null-check every path — VOD metadata moved between queries in Nov 2025).
Fallback — inline (community-standard shape, unverified by primary source): `{ user(login:"<login>"){ id displayName profileImageURL(width:300) stream { id title viewersCount game { name displayName } createdAt type } } }`.
VOD metadata: persisted `VideoMetadata` `45111672eea2e507f8ba44d101a61862f9c56b11dee09a15634cb75cb9b9084d`, variables `{"channelLogin":"","videoID":"<id>"}` → `data.video.{id,title,owner.displayName,game.displayName}`.

### 6. Error mapping
| Signal | Meaning / action |
|---|---|
| GQL HTTP 200, `errors[].message == "PersistedQueryNotFound"` | hash retired → use the other query form |
| GQL `data.streamPlaybackAccessToken == null` (no `value`/`signature`) | channel does not exist, **or** Client-Integrity now enforced → "Channel not found or Twitch requires a browser for this channel" (streamlink would now spin up Chromium to POST `https://gql.twitch.tv/integrity` — not possible here) |
| token `authorization.forbidden == true` | show `authorization.reason` |
| token `geoblock_reason` non-empty | "Not available in your region" |
| usher 4xx with JSON `[{"type":"error","error":"...","error_code":"vod_manifest_restricted"\|"unauthorized_entitlements"}]` | "Subscriber-only stream/VOD" |
| usher non-200 with other JSON error / empty body (channel offline) | "Channel is offline" — confirm offline via `StreamMetadata.stream == null` |
| usher 5xx / network | retry once, then surface |
| `chansub.restricted_bitrates[]` matches `(.+_)?archives\|live\|chunked` | warn "source quality requires subscription" |

### 7. Risks
1. Persisted hashes rotate silently (last 2025-11-10, `PersistedQueryNotFound`); the inline query is the hedge, but Twitch could also restrict inline queries (2022 integrity changes already broke mutations and some queries).
2. Client-Integrity enforcement could return (it was mandatory for a week in mid-2023) — Wavee has no way to compute CI tokens.
3. SSAI ads: fMP4 ad segments inside TS streams may make MF fail or stall; mid-rolls cause `EXT-X-DISCONTINUITY` storms; no anonymous playerType avoids them.
4. Usher v1 may be retired any time; v2 has a different master format with non-standard tags/attributes MF may or may not tolerate (see open questions).
5. `supported_codecs=h264` yields transcodes for enhanced-broadcast channels — source quality unavailable; acceptable.
6. Undocumented GQL rate limits; tokens embed `device_id`/`user_ip` — send a browser-like UA and reuse tokens until `expires`.
7. Archived UWP report: Twitch live m3u8 in AdaptiveMediaSource stalled after pause/resume — expect to reload on resume for live Twitch.

---

*Appendix C — verbatim research output: Media Foundation.*

## Media Foundation contract (Win32 IMFMediaEngine path)

### What the built-in HLS source can and cannot do
- **Can** (Windows 10+, documented matrix only up to 1607 but nothing documents removal): HLS up to protocol v4 with **MPEG-2 TS** segments; `EXTM3U`, `EXT-X-VERSION`, `EXTINF`, `EXT-X-BYTERANGE`, `EXT-X-DISCONTINUITY`, `EXT-X-KEY` (NONE/AES-128, SAMPLE-AES 1607+), `EXT-X-TARGETDURATION`, `EXT-X-MEDIA-SEQUENCE`, `EXT-X-ENDLIST`, `EXT-X-PLAYLIST-TYPE`, `EXT-X-MEDIA` (AUDIO/VIDEO, URI, GROUP-ID, LANGUAGE, NAME), `EXT-X-STREAM-INF` (BANDWIDTH, CODECS, RESOLUTION, AUDIO, VIDEO), `EXT-X-INDEPENDENT-SEGMENTS`, `EXT-X-START` (partial). Codecs in TS: H.264 + AAC-LC/HE-AAC (built-in), MP3. Recognised MIME: `application/vnd.apple.mpegurl`, `audio/mpegurl`, `application/x-mpegurl`, `audio/x-mpegURL`; extensions `.m3u8`/`.m3u`.
- **Cannot**: `EXT-X-MAP` (no fMP4/CMAF — Twitch h265/av1, Apple's advanced streams fail), LL-HLS (`EXT-X-PART`, `SERVER-CONTROL`, `PRELOAD-HINT`), `EXT-X-I-FRAME*`, `EXT-X-SESSION-DATA/KEY`; `EXT-X-PROGRAM-DATE-TIME` and `EXT-X-DISCONTINUITY-SEQUENCE` are "Not Supported" (both present in YouTube and Twitch playlists — expected to be ignored, unverified); `EXT-X-DATERANGE` not listed. AC-3 decode removed in Win11 24H2. Historically starts at 360p and caps at 1080p; several unresolved "plays elsewhere, not in MediaPlayer" reports (incl. an audio-only HLS radio, Windows-universal-samples #692).
- **YouTube live HLS** (TS, v3, H.264 ≤1080p + AAC, DVR window, no EXT-X-MAP) and **Twitch h264 HLS** (TS, v3, ~2 s segments) are inside the documented envelope. No first-hand report of either through IMFMediaEngine exists — spike first.
- **DASH**: none on Win32 Media Engine (IMFMediaSourceExtension is "expected to only be called by web browsers"); WinRT AdaptiveMediaSource DASH is `$Time$`-only dynamic from 1703 and cannot play YouTube's `$Number$`/SegmentList live MPD anyway. **Do not implement DASH.**
- **Progressive radio**: MF's network source handles `http(s)://` MP3/ADTS, but no Microsoft source says it accepts SHOUTcast's `ICY 200 OK` status line or unbounded chunked streams; UWP developers needed a custom MediaStreamSource. Route progressive radio through Wavee's managed decoders (NLayer MP3; AAC via the MFT below) over Wavee's own fetch; if the status line starts with `ICY`, fall back to a raw `TcpClient` HTTP/1.0 reader (strict .NET/WinHTTP parsers reject it). Do not send `Icy-MetaData: 1` in v1 (otherwise metadata blocks are interleaved every `icy-metaint` bytes and must be stripped).
- **Runtime probe**: `IMFMediaEngine::CanPlayType(L"application/vnd.apple.mpegurl")` → `MF_MEDIA_ENGINE_CANPLAY_{NOT_SUPPORTED=0,MAYBE=1,PROBABLY=2}`; plus check `HKLM\SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers` for a `.m3u8`/mpegurl key. Handler selection is by URL extension or `MF_BYTESTREAM_CONTENT_TYPE` (no content sniffing) → unmatched gives `MF_E_UNSUPPORTED_BYTESTREAM_TYPE 0xC00D36C4`. Twitch usher paths end in `.m3u8` before the query; YouTube `hls_variant` ends `/file/index.m3u8` (unverified) and `hls_playlist` ends `/playlist/index.m3u8`.

### Live timeline semantics
- `GetDuration()` = HTML5 duration: **NaN** before metadata, **+Infinity** for unbounded live; `MF_MEDIA_ENGINE_EVENT_DURATIONCHANGE` (=21) fires on change. WinRT reports `Int64.MaxValue` for live (snippet, unverified). UI rule: `double.IsInfinity(duration)` ⇒ live badge, seek bar = seekable window.
- `GetSeekable(IMFMediaTimeRange**)` / `GetBuffered` = HTML5 `seekable`/`buffered` (`GetLength`, `GetStart(i,&s)`, `GetEnd(i,&e)`); the last range's end is the live edge (YouTube serves a 1 h DVR window, 12 h max; Twitch live has no DVR).
- `IMFMediaEngineEx::GetResourceCharacteristics(DWORD*)`: `MFMEDIASOURCE_IS_LIVE=0x1`, `CAN_SEEK=0x2`, `CAN_PAUSE=0x4`, `HAS_SLOW_SEEK=0x8`, `CAN_SKIPFORWARD=0x20`, `CAN_SKIPBACKWARD=0x40`.
- No "go to live" API: `SetCurrentTimeEx(end_of_last_seekable_range - ~3 s, MF_MEDIA_ENGINE_SEEK_MODE_NORMAL=0)`; events `SEEKING=16`, `SEEKED=17`, `TIMEUPDATE=18` (via `EnableTimeUpdateTimer`). Pause/resume on live: on resume, seek to live edge (Twitch in AdaptiveMediaSource was reported to stall after pause/resume).
- Readiness: `MF_MEDIA_ENGINE_READY HAVE_NOTHING=0 … HAVE_ENOUGH_DATA=4`; network: `EMPTY=0, IDLE=1, LOADING=2, NO_SOURCE=3`. Load sequence after `SetSource`: `LOADSTART=1` → `LOADEDMETADATA=10` → `LOADEDDATA=11` → `CANPLAY=14` → `CANPLAYTHROUGH=15`, or `ERROR=5`. Other useful events: `STALLED=7`, `WAITING=12`, `BUFFERINGSTARTED=1005`/`ENDED=1006`, `FORMATCHANGE=1000`, `RESOURCELOST=1012`, `STREAMRENDERINGERROR=1014`.
- Errors: `ERROR` event `param1` = `MF_MEDIA_ENGINE_ERR` {`ABORTED=1`, `NETWORK=2`, `DECODE=3`, `SRC_NOT_SUPPORTED=4`, `ENCRYPTED=5`}, `param2` = HRESULT. Map: NETWORK (+`MF_E_NET_*`: `TIMEOUT 0xC00D4278`, `SERVER_ACCESSDENIED 0xC00D4285`, `RESOURCE_GONE 0xC00D428B`, `CANNOTCONNECT 0xC00D4287`, `CONNECTION_FAILURE 0xC00D4283`, `SERVER_UNAVAILABLE 0xC00D428E`, `TOO_MANY_REDIRECTS 0xC00D4277`) ⇒ re-resolve + SetSource (max N retries with backoff); DECODE ⇒ reload same URL once (Twitch fMP4 ad / codec hiccup), then fail; SRC_NOT_SUPPORTED / `MF_E_UNSUPPORTED_BYTESTREAM_TYPE 0xC00D36C4` / `MF_E_UNSUPPORTED_SCHEME 0xC00D36C3` ⇒ "this machine's Media Foundation cannot play HLS". Auto-reconnect (`MFNETSOURCE_AUTORECONNECTLIMIT`) is documented only for the classic network source — implement reconnect in Wavee.

### Header / User-Agent control
- `IMFMediaEngine::SetSource` offers **no way to set arbitrary headers or cookies**. `MF_MEDIA_ENGINE_SOURCE_RESOLVER_CONFIG_STORE {0AC0C497-B3C4-48C9-9CDE-BB8CA2442CA3}` (IPropertyStore at `IMFMediaEngineClassFactory::CreateInstance`) carries only `MFNETSOURCE_BROWSERUSERAGENT`/`MFNETSOURCE_PLAYERUSERAGENT` (VT_LPWSTR, logging UA parts; default identifies as `NSPlayer`), `MFNETSOURCE_STREAM_LANGUAGE` (Accept-Language), proxy, credentials (`MFNETSOURCE_CREDENTIAL_MANAGER`), `MFNETSOURCE_RESOURCE_FILTER` (`IMFNetResourceFilter::OnRedirect/OnSendingRequest`), buffering (`MFNETSOURCE_BUFFERINGTIME`, `MAXBUFFERTIMEMS`); whether the HLS source consults it for segment fetches is undocumented.
- Full override = `IMFHttpDownloadSessionProvider` → `IMFHttpDownloadSession` → `IMFHttpDownloadRequest` (`AddHeader`, `BeginSendRequest`, `BeginReceiveResponse`, `GetHttpStatus`, `QueryHeader`, `BeginReadPayload`, `GetRangeEndOffset`…), Windows 10 1703+, desktop only, registered via the source resolver under `MFNETSOURCE_HTTP_DOWNLOAD_SESSION_PROVIDER` (GUID: read from SDK `mfidl.h`; how the Media Engine wires it is undocumented). **Not needed for v1** — YouTube URLs are not UA-bound in practice and Twitch credentials live in the query string. Keep it as the v2 path for ICY radio through MF or header injection.
- Alternative hooks: `MF_MEDIA_ENGINE_EXTENSION {3109FD46-060D-4B62-8DCF-FAFF811318D2}` (`IMFMediaEngineExtension::BeginCreateObject` to supply a custom source/byte stream) and `IMFMediaEngineEx::SetSourceFromByteStream(IMFByteStream*, BSTR url)` with a custom non-seekable `IMFByteStream` (`MFBYTESTREAM_IS_READABLE 0x1 | IS_REMOTE 0x8`, set `MF_BYTESTREAM_CONTENT_TYPE`). Local-file trick for variant selection (write a sanitized master `.m3u8` with absolute https media-playlist URIs and SetSource on it) is plausible but unverified.

### AAC decoder MFT setup contract (for the managed audio path: ADTS radio / future audio-only HLS)
- `MFStartup(MF_VERSION = 0x00020070, MFSTARTUP_FULL = 0)` once before any MF call (else `MF_E_NOT_INITIALIZED 0xC00D36B6`); `MFShutdown` pairwise; not from static ctors.
- `CoCreateInstance(CLSID_CMSAACDecMFT = {32D186A7-218F-4C75-8876-DD77273A8999}, IID_IMFTransform)` (MSAudDecMFT.dll).
- Input type (`SetInputType(0, type, 0)`), for ADTS streams:
  - `MF_MT_MAJOR_TYPE {48EBA18E-F8C9-4687-BF11-0A74C9F96A8F}` = `MFMediaType_Audio {73647561-0000-0010-8000-00AA00389B71}`
  - `MF_MT_SUBTYPE {F7E34C9A-42E8-4714-B74B-CB29D72C35E5}` = `MFAudioFormat_AAC {00001610-0000-0010-8000-00AA00389B71}`
  - `MF_MT_AAC_PAYLOAD_TYPE {BFBABE79-7434-4D1C-94F0-72A3B9E17188}` = `1` (ADTS; 0 = raw one-frame-per-sample, 3 = LOAS/LATM)
  - `MF_MT_AUDIO_NUM_CHANNELS {37E48BF5-645E-4C5B-89DE-ADA9E29B696A}`, `MF_MT_AUDIO_SAMPLES_PER_SECOND {5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA}` = **core AAC-LC values before SBR/PS** (parse from the first ADTS header: sampling_frequency_index, channel_configuration)
  - `MF_MT_AUDIO_BITS_PER_SAMPLE {F2DEB57F-40FA-4764-AA33-ED4F2D1FF669}` = 16 (desired PCM depth; optional)
  - `MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION {7632F0E6-9538-4D61-ACDA-EA29C8C14456}` optional (0 / 0xFE if unknown)
  - `MF_MT_USER_DATA {B6BC765F-4C3B-40A4-BD51-2535B66FE09D}` = 12-byte HEAACWAVEINFO tail (`WORD wPayloadType=1, WORD wAudioProfileLevelIndication=0, WORD wStructType=0, WORD wReserved1=0, DWORD dwReserved2=0`) **followed by** the AudioSpecificConfig (2 bytes for LC / implicit SBR: `audioObjectType=2 (5 bits) | samplingFrequencyIndex (4) | channelConfiguration (4) | 3 zero bits`; e.g. 48 kHz stereo = `11 90`, 44.1 kHz stereo = `12 10`). The decoder requires ≥2 bytes of ASC (cbSize ≥ 2); an 8-byte tail misaligns it.
- Output type: enumerate `GetOutputAvailableType` and pick `MFAudioFormat_Float {00000003-0000-0010-8000-00AA00389B71}` (32-bit) or `MFAudioFormat_PCM {00000001-…}` (16-bit); output rate is post-SBR (core ×2 for HE-AAC) and ∈ {8, 11.025, 12, 16, 22.05, 24, 32, 44.1, 48} kHz; ≤6 channels. Call `GetOutputStreamInfo` (expect `cbSize ≈ 0xC000`, caller-allocated buffers, `MFT_OUTPUT_STREAM_WHOLE_SAMPLES`).
- Processing: `ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING)` → loop `ProcessInput(0, sample, 0)` (ADTS: a sample may hold multiple or partial frames, frames may span samples; each ADTS header followed by exactly one raw_data_block) / `ProcessOutput` until `MF_E_TRANSFORM_NEED_MORE_INPUT 0xC00D6D72`; `MF_E_NOTACCEPTING 0xC00D36B5` ⇒ drain output first; `MF_E_TRANSFORM_STREAM_CHANGE 0xC00D6D61` ⇒ re-negotiate output type; stream param change (sample-rate/channel change mid-stream) ⇒ `COMMAND_DRAIN`/`COMMAND_FLUSH` then set a new input type (no dynamic format change); end: `NOTIFY_END_OF_STREAM` → `COMMAND_DRAIN` → `ProcessOutput` until NEED_MORE_INPUT; seeks/source switch: `COMMAND_FLUSH` + `MFSampleExtension_Discontinuity` on the next sample.
- Limits: LC / HE-AAC v1 / v2 only (no Main/LTP/ADIF/960-sample frames), ≤48 kHz output, ≤6 ch.

---

*Appendix D — verbatim research output: the 14 open empirical questions.* These are what the verification spike
answers; none of them is settled by documentation.

1. Does a googlevideo `hls_variant` URL (path ending `/file/index.m3u8` — itself unverified) resolve through IMFMediaEngine::SetSource to the HLS byte-stream handler, and does `CanPlayType("application/vnd.apple.mpegurl")` return MAYBE/PROBABLY on the target Windows 11 builds? (No first-hand report of YouTube HLS through IMFMediaEngine exists.)
2. Does the visionos client actually return `streamingData.hlsManifestUrl` for a LIVE broadcast today (attested only by maintainer descriptions), and for VOD? Does android still return it, and does ios's HLS really 403 ~30 s in?
3. Does MF's HLS source tolerate (ignore) `EXT-X-PROGRAM-DATE-TIME`, `EXT-X-DISCONTINUITY-SEQUENCE`, `EXT-X-DATERANGE`, `EXT-X-SESSION-DATA`, `EXT-X-TWITCH-*` tags and the `IVS-NAME` STREAM-INF attribute, or does any of them abort parsing? Test both Twitch usher v2 and v1 masters.
4. Does MF cope with YouTube's chunked segment responses (no Content-Length, `noclen/1`), 302 redirects between `rr---sn-*` hosts, and the 1 h DVR window (what do GetDuration/GetSeekable report: +Inf and a sliding 3600 s range?).
5. Does MF play an audio-only HLS variant whose segments are raw ADTS (YouTube itag 233/234) or packed audio (radio `.m3u8` like the sr.se case that failed in #692)? If not, audio-only YouTube/radio needs an app-side HLS puller feeding ADTS into the AAC MFT.
6. Does SetSource accept a local `file://…/master.m3u8` whose media playlists are absolute https URLs (the sanitized-master / variant-cap trick), and does the HLS source honour EXT-X-START or a bitrate cap from the MF_MEDIA_ENGINE_SOURCE_RESOLVER_CONFIG_STORE?
7. On manifest expiry (~6 h) or IP change, which ERROR pair arrives (MF_MEDIA_ENGINE_ERR_NETWORK + which MF_E_NET_*), and how long after the first 403 does the engine report it? Is a plain SetSource swap plus seek-to-edge seamless enough for a 24/7 music stream?
8. How does MF react to a Twitch stitched-ad pod whose segments are fMP4 inside a TS playlist: DECODE error, stall, or silent skip? Does live Twitch stall after pause/resume (archived UWP report) and does a seek-to-edge fix it?
9. Is the YouTube consent redirect (consent.youtube.com) hit from EU IPs when fetching channel `/live` pages with a desktop UA, and does a cookie suffice or must the form be POSTed as streamlink does? Is the `currentVideoEndpoint.watchEndpoint.videoId` JSON still present in the HTML?
10. Does the inline anonymous `streamPlaybackAccessToken` query succeed with the web Client-ID `kimne78kx3ncx6brgo4mv6wki5h1ko` (yt-dlp uses it with `ue6666qo983tsx6so1t0vnawi233wa`), and is the persisted hash `ed230aa1…` still alive in Aug 2026? Which usher param names does `/vod/v2/` require (`nauthsig/nauth` vs `sig/token`)?
11. What exact HTTP status/body does usher return for an offline channel (map to "offline") versus a non-existent login, and what Content-Type do manifest.googlevideo.com / usher serve for the playlists (matters if MF keys on MIME rather than extension)?
12. Does Wavee's HttpClient (SocketsHttpHandler) reject `ICY 200 OK` status lines, and how many real-world radio URLs in the target catalogue are SHOUTcast-v1 style versus Icecast `HTTP/1.0 200 OK`? Validate the raw-socket fallback and the no-`Icy-MetaData` assumption.
13. AAC MFT: confirm the HEAACWAVEINFO+ASC `MF_MT_USER_DATA` layout is accepted with payload type 1 on Windows 11, that HE-AAC radio (mp4a.40.5, implicit SBR) outputs at the doubled rate without an explicit-SBR ASC, and that mid-stream sample-rate changes are handled by drain+retype.
14. VOD pre-roll: does MF hitting segments before yt-dlp's computed `available_at` actually 403, and is delaying SetSource by the pre-roll duration sufficient? Does the 2025-11 'Sleeping 4 seconds as required by the site' behaviour affect visionos VOD HLS?
