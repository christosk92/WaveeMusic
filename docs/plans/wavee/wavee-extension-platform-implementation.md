# Wavee extension platform — implementation plan

Issues: #154 (timecode output), #48 (mix). Status: **design approved 2026-09-21, nothing built.**
Companion docs: `docs/guide/playback-modules.md` (the SDK + wire as it ships today),
`docs/guide/sidebar-extension-platform.md` (the in-proc registrar), `docs/guide/playplay-private-split.md`.

> 🤖 AGENT — `playback-modules.md` still cites 0.2.9 paths (`src/apps/Wavee/Backend/Modules/**`, `Wavee.Core`).
> The host is `src/apps/Wavee/Platform/Modules.Host.cs` + `Modules.cs`; `Wavee.Core` no longer exists. Refreshing
> those paths is part of wave P0.

## Contents

1. Why, and what already exists
2. Decisions of record
3. The five primitives
4. Manifest v2
5. Trust — Official / Verified / Community / Unsigned
6. Activation and events
7. Host API
8. Providers
9. Pages v2
10. `Wavee.Module.Timecode` (#154)
11. `Wavee.Module.Spotify`
12. Wireframes
13. Waves, files, tests
14. Open items

---

## 1. Why, and what already exists

#154 and #48 are both "cool, some people will use it, not tied to Wavee or Spotify". They should be **extensions a
stranger can write, sign, install and distribute** — and so should lyrics sources, Last.fm, custom stats, artwork
overrides and pages nobody has thought of yet.

Two halves of that platform already ship, and they have never met:

| Half | What it is | Limit |
|---|---|---|
| `Wavee.Sdk` + `Platform/Modules.Host.cs` | Out-of-process NativeAOT child EXEs, JSON-RPC 2.0 over stdio, binary `stream/read` frames, `WaveeModule`/`ModuleRunner`/`ModuleTestHost`, declarative `ModulePageDoc`, `module/status\|action\|diagnostics\|progress`, job-object lifetime, crash backoff | **Playback only.** A module wakes for `playback/*` and idle-stops after 10 min. |
| `WaveeExtensionRegistry` (`Shell/Sidebar.cs`, `Sidebar.Host.cs`) | `IWaveeExtensionRegistrar.RegisterAction/RegisterDataSource`, permissions declared, budgets, "first-party is the extension named `wavee`" | **In-proc, first-party only.** Guardrail 1 already says a sandboxed extension's manifest contributions get *replayed onto the same registrar* — nothing replays them yet. |

And one thing is broken: **there is no verification at all** (§5).

NativeAOT rules out `Assembly.Load`, so "drop a DLL in a folder" is not available and never will be. The
out-of-process wire *is* the plugin model — language-agnostic, crash-isolated, and already built.

## 2. Decisions of record

| Decision | Choice | Decided |
|---|---|---|
| Extension UI | **Declarative tree.** The extension sends a document from a fixed SDK vocabulary; the host draws it with real FluentGpu components. No in-proc native UI, no WebView. | 2026-09-21 |
| Spotify module location | **Public** (`src/apps/modules/Wavee.Module.Spotify`); only the key source is source-linked from `wavee-playplay-private` into the *module's* csproj. | 2026-09-21 |
| "Verified" | **Verified publisher**: the owner countersigns a publisher's key once; the publisher signs their own releases; a root-signed revocation list kills versions or publishers. The badge reads *Verified publisher*, never *audited*. | 2026-09-21 |
| Naming | "module" stays the wire/SDK word (`WaveeModule`, `wavee-module.json`); "extension" is the user-facing word (Settings ▸ Extensions). One thing, two audiences. | this doc |
| Protocol version | Everything here is **additive to v1**: new methods are found through capabilities and `-32601` means absent (`playback-modules.md` §4 "Version negotiation"). `schemaVersion` of the manifest goes to **2**. | this doc |

## 3. The five primitives

```
 wavee-module.json (v2)            host                                   extension process (any language)
 ├ permissions[]   ── signed ──►  TrustGate ─ verify lock+sig ─ tier ──►  spawn (entry handle held, deny-write)
 ├ activation[]                   ActivationPlanner (onStartup/onPlayback/onRoute/onProvider:x)
 ├ contributes                    ContributionHost ── replays ──► WaveeExtensionRegistry (sidebar, actions)
 │  ├ providers[]                 ProviderChain<T> (priority, budget, cache)  ◄── lyrics/artwork/facts
 │  ├ pages[]                     wavee://ext/<id>/<entity> → module page renderer (doc v2 + patches)
 │  ├ actions[] / sidebar[]
 │  └ settings                    Settings ▸ Extensions ▸ <module> (controls generated from the schema)
 └ events[]                       ExtensionEvents ── event/* notifications ──►
                                  ModuleHostServices ◄── host/playback|catalog|library|storage|ui/*
```

| Primitive | Direction | Rule |
|---|---|---|
| **Providers** | host asks | A chain, never a slot. First-party Spotify lyrics/artwork are chain member `wavee` — there is no privileged path, the same bet the sidebar made. |
| **Events** | host tells | Edges + the existing 1 s position sample. **Never per-frame**; a consumer that needs 60 Hz extrapolates (§10). |
| **Host API** | extension asks | Every method names a permission; the gate reads the *signed* permission list under the tier's ceiling. |
| **Contributions** | manifest | Static, declarative, available **without spawning the process** (menus, routes, settings, sidebar rows exist while the module sleeps). |
| **UI** | both | A document + patches + semantic events. Hover, scroll, focus, motion never cross the pipe. |

The acceptance test for the whole design: **`Wavee.Module.Timecode` needs nothing timecode-specific in the SDK.**

## 4. Manifest v2

`ModuleManifest` is a positional record; v2 members are added as `init` properties (the `ModuleMenu.LabelLocKey`
pattern) so every v1 manifest still deserializes and unknown members keep round-tripping.

```csharp
// src/apps/Wavee.Sdk/ModuleManifest.cs — additions
public sealed record ModuleManifest(/* …the ten v1 members, unchanged… */)
{
    /// <summary>Host-service permissions. Covered by the signature; capped by the trust tier (see ModuleTrust).
    /// Replaces the v1 "permission:" prefix inside Capabilities, which is deleted, not kept.</summary>
    public string[] Permissions { get; init; } = [];

    /// <summary>When the host may start this process: onStartup | onPlayback | onRoute | onProvider:&lt;kind&gt; |
    /// onAction. Empty = on demand only (the v1 behaviour).</summary>
    public string[] Activation { get; init; } = [];

    /// <summary>Events the host delivers while the process is running (ModuleEvents constants).</summary>
    public string[] Events { get; init; } = [];

    /// <summary>Static contributions, readable without spawning the process.</summary>
    public ModuleContributions? Contributes { get; init; }
}

public sealed record ModuleContributions
{
    public ProviderContribution[] Providers { get; init; } = [];   // { kind: "lyrics", priority: 50 }
    public PageContribution[] Pages { get; init; } = [];           // { entityId: "stats", title, glyph, nav: "sidebar"|"profile"|"none" }
    public ActionContribution[] Actions { get; init; } = [];       // { id, label, targets: ["track","album"], confirm }
    public SidebarContribution[] Sidebar { get; init; } = [];      // { id, title, itemType, facets[], sorts[], pages }
    public SettingsContribution? Settings { get; init; }           // { fields: ConfigField[] }
}

/// <summary>Semantic field kinds only — the sidebar ConfigSchema's vocabulary. Never a colour, pixel or duration.</summary>
public sealed record ConfigField(string Key, string Kind, string Label)   // string | int | bool | enum | entityUri | uriList | secret
{
    public string[]? Values { get; init; }
    public string? Default { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
}
```

`ModuleCapabilities.PermissionPrefix` / `HasPermission(manifest, …)` (`Modules.Host.cs:302,312`) are **deleted**;
the gate at `:976` becomes `module.Trust.Grants(perm)`.

## 5. Trust — Official / Verified / Community / Unsigned

### What is wrong today

- `ModuleCatalog.Validate` (`Modules.Host.cs:204-220`) checks shape and path containment. Nothing else.
- `publisher` is a free string; `"publisher": "wavee"` is indistinguishable from first-party except by directory.
- Permissions ride the module's own unsigned manifest — **a module grants itself its permissions.**
- `Rank` takes the highest version (`:249-265`): no pointer, no floor, no revocation. A planted
  `modules\wavee.spotify\99.0.0\` wins forever and runs full-trust with package identity.
- `host/auth/token`, `host/auth/context`, `spotify/audioKey` are declared in the SDK and **not registered** in the
  host. They stay unregistered until this section is built.

### The chain

```
 Wavee ROOT key  (offline. The app embeds TWO root public keys: current + next, so rotation is a normal release.)
   └─ signs  PublisherCert { publisherId, publicKeySpki, tier: official|verified, idPrefixes[], notAfterUnixMs }
        └─ publisher key signs  ModuleLock { id, version, arch, manifestSha256, files:[{path, sha256, size}] }

 modules.json (the feed) is root-signed and carries
   revoked: [ { publisherId } | { id, versions[] } | { sha256 } ]
```

ECDSA P-256 through the BCL (`ECDsa.ImportSubjectPublicKeyInfo` + `VerifyData`, SHA-256) — AOT-safe, no
dependency. One envelope file beside the manifest; payloads are carried as **base64 of the exact signed bytes**,
so there is no canonicalization step to get wrong:

```jsonc
// wavee-module.sig.json
{ "schemaVersion": 1,
  "lock":    "<base64 UTF-8 JSON of ModuleLock>",   "lockSig": "<base64 P1363, by the publisher key>",
  "cert":    "<base64 UTF-8 JSON of PublisherCert>", "certSig": "<base64 P1363, by a root key>" }
```

### The pure gate

```csharp
// src/apps/Wavee/Platform/Modules.Trust.cs  (engine-free; unit-tested directly)
public enum TrustTier : byte { Unsigned, Community, Verified, Official }

public enum TrustFault : byte { None, NoSignature, BadEnvelope, CertNotByRoot, CertExpired, LockNotByPublisher,
    IdOutsidePrefixes, IdVersionMismatch, ManifestHashMismatch, FileHashMismatch, FileMissing, ExtraExecutable,
    Revoked, PinnedKeyChanged }

public sealed record ModuleTrust(TrustTier Tier, TrustFault Fault, string? PublisherId, string? KeyThumbprint,
    string[] Granted)
{
    public bool Grants(string permission) => Array.IndexOf(Granted, permission) >= 0;
}

public static class ModuleTrustPolicy
{
    /// <summary>The whole verdict as one pure function over the injectable file-system seam.</summary>
    public static ModuleTrust Verify(ModuleManifest manifest, string dir, TrustInputs inputs, ModuleFileSystem fs);

    /// <summary>The tier's ceiling: what a tier may EVER hold, whatever the manifest asks for.</summary>
    public static bool Allows(TrustTier tier, string permission) => permission switch
    {
        "auth.spotify" or "spotify.audioKey"                   => tier == TrustTier.Official,
        "playback.control" or "library.write"                  => tier >= TrustTier.Verified,   // Community: consent sheet
        "playback.read" or "catalog.read" or "library.read"
            or "storage.private" or "ui.toast" or "ui.navigate" => tier >= TrustTier.Community,
        _ => false,                                            // an unknown permission is never granted
    };
}

/// <param name="Roots">The embedded root SPKIs (current + next).</param>
/// <param name="Revocations">The last root-verified feed's list; persisted, so it holds offline.</param>
/// <param name="PinnedKeys">Community keys pinned at first install: id → thumbprint.</param>
/// <param name="Consents">Per-module consents the user gave on the consent sheet.</param>
public sealed record TrustInputs(byte[][] Roots, Revocation[] Revocations,
    IReadOnlyDictionary<string, string> PinnedKeys, IReadOnlyDictionary<string, string[]> Consents,
    bool DeveloperMode, long NowUnixMs);
```

Rules the policy enforces, each one a test:

1. `publisherId` and the tier come from the **cert**, never from `manifest.Publisher` (that field becomes display
   text and is overwritten with the cert's id for signed modules).
2. `manifest.Id` must start with one of the cert's `idPrefixes`. `wavee.` is only ever in an `official` cert.
3. `ManifestHashMismatch` — the signature covers the manifest, **permissions included**.
4. Every file in the lock must exist and hash; any `.exe`/`.dll` in the directory that is *not* in the lock is
   `ExtraExecutable` (a planted sibling DLL is the classic side-load).
5. Community = a well-formed lock signed by a key no root vouches for. First install pins the thumbprint;
   a different key later is `PinnedKeyChanged` — refused until the user re-confirms on the signature sheet.
6. Unsigned loads only with **Settings ▸ Diagnostics ▸ Developer mode** on, only from the user store, with a
   persistent banner. No environment variable. `dotnet <dll>` entries are Unsigned-tier only.
7. `Granted = manifest.Permissions ∩ Allows(tier) ∩ (tier ≥ Verified ? all : Consents[id])`.

### Where it plugs in

```csharp
// Modules.Host.cs — Probe(), after Validate():
var trust = ModuleTrustPolicy.Verify(manifest, dir, inputs, fs);
if (trust.Fault != TrustFault.None && !(trust.Tier == TrustTier.Unsigned && inputs.DeveloperMode))
{ rejections.Add(new ModuleRejection(dir, "trust: " + trust.Fault)); return; }
candidates.Add(new InstalledModule(manifest.Id, manifest.Version, dir, manifest, bundled, trust));
```

**Verified again at every spawn**, and the swap window is closed by holding the file:

```csharp
// ChildProcessChannel.SpawnAsync — before Process.Start (Modules.Host.cs:828)
var pin = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);   // deny write + delete
if (!ModuleTrustPolicy.EntryStillMatches(pin, module.Trust)) { pin.Dispose(); throw new ModuleException(…); }
// … Process.Start … ; `pin` is disposed when the process exits.
```

**Ranking** stops being "highest version wins":

```
 modules\<id>\active.json  { "version": "1.2.0", "previous": "1.1.0", "failedInits": 0 }
```
The pointer chooses; the bundled version is the floor; three failed `module/initialize` flips the pointer back;
a revoked entry is never a candidate. Pure: `ModuleActivePointer.Choose(candidates, pointer, revocations)`.

**One implementation for modules and the PlayPlay runtime pack.** The public shapes of the PlayPlay ladder —
`RuntimeTrust`, `RuntimeSignature`, `RuntimeSignatureLine`, `ProvisioningOutcome` (`Screens/Setup.cs:335-385`,
`Platform/Platform.Settings.cs:719`) and the signature sheet (`Shell/Shell.Overlays.UI.cs:225`) — are generalized
to `ModuleTrust`; the pack inside the Spotify module's data dir is a lock entry like any other. Authenticode stays
what `playback-modules.md` already calls it: **advisory**, a row on the sheet.

**Tooling.** `Wavee.ReleaseTool sign-module --dir … --key …` writes the envelope; `ops/build/pack-module.ps1`
calls it; `wavee-release.ps1` gains a `modules signed` gate that runs `ModuleTrustPolicy.Verify` over every staged
module and fails on anything below `Official`. `ops/release/tests` gets the Pester half.

**Honest limit.** A module is a full-trust process. Permissions gate *host services*; they cannot stop a process
opening a socket. Signing and tiers are the control. AppContainer launch for Community modules is later hardening;
the design never hands a module a handle or a path a sandbox could not honour.

## 6. Activation and events

```csharp
// src/apps/Wavee.Sdk/ModuleEvents.cs
public static class ModuleEvents
{
    public const string PlaybackState = "playback.state";       // edges + the 1 s sample
    public const string PlaybackTrack = "playback.track";       // the item changed
    public const string PlaybackScrobble = "playback.scrobble"; // the 50 % / 4 min point, once per play
    public const string LibraryChanged = "library.changed";     // coalesced, ≤ 1 per second
    public const string SettingsChanged = "settings.changed";   // this module's own contributed fields
}

/// <param name="AsOfQpc">Raw QueryPerformanceCounter ticks at which PositionMs was true. QPC is system-wide on
/// Windows, so a module compares it to its own Stopwatch.GetTimestamp() with no clock exchange.</param>
/// <param name="OutputLatencyMs">Device + mixer latency: subtract it to sync to what is HEARD.</param>
public sealed record PlaybackStateEvent(string? Uri, string State, long PositionMs, long AsOfQpc,
    long QpcFrequency, double Rate, int OutputLatencyMs, long DurationMs);
```

> `State.PosQpc` is **milliseconds on the frame clock**, not QPC ticks (`Playback/Playback.cs:954-958`). The emitter
> converts once, at the edge, from `FrameTime`'s QPC origin. It never calls `Environment.TickCount64`.

`ActivationPlanner` (pure): given the manifest's `activation`, the app's phase and whether playback is active,
answers `Start | Keep | MayIdle`. A module with a delivered event subscription while playback is active is exempt
from the 10-minute idle stop — the same exemption an open `stream/*` handle already has.

Module side, additive virtuals on `WaveeModule`:

```csharp
public virtual ValueTask OnPlaybackStateAsync(PlaybackStateEvent e, CancellationToken ct) => default;
public virtual ValueTask OnPlaybackTrackAsync(TrackEvent e, CancellationToken ct) => default;
public virtual ValueTask OnScrobbleAsync(ScrobbleEvent e, CancellationToken ct) => default;
public virtual ValueTask OnSettingsChangedAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct) => default;
```

## 7. Host API

Registered through the existing `ModuleHostServices.Register<TParams,TResult>(method, permission, …)`
(`Modules.Host.cs:966`); typed mirrors go on `IModuleHost`.

| Method | Permission | Notes |
|---|---|---|
| `host/playback/get` | `playback.read` | The same value `event/playback.state` carries, on demand. |
| `host/playback/command` | `playback.control` | `play\|pause\|next\|prev\|seek\|playUri\|enqueue` — routed through the existing `WaveeActionDescriptor`s, so enablement and refusal reasons are the app's own. |
| `host/catalog/get` | `catalog.read` | `{uris[]}` → title/artists/album/duration/isrc/artworkUrl + facts (tempo, key, Camelot — kind 222, already in `TrackTable`). Whole-model demand; the query layer batches. |
| `host/library/query` | `library.read` | `{kind, sort, page}` over the sidebar binder's sources. |
| `host/storage/get\|set` | `storage.private` | Small KV in the module's data dir; secrets stay on `host/secrets/*`. |
| `host/ui/toast`, `host/ui/navigate` | `ui.toast`, `ui.navigate` | Navigate only to the module's own routes or an entity uri. |
| `host/auth/*`, `spotify/audioKey` | `auth.spotify` | **Official only.** Registered in wave P4, not before. |

## 8. Providers

```csharp
// Wavee.Sdk/Providers.cs
public sealed record TrackRef(string Uri, string Title, string[] Artists, string? Album, long DurationMs, string? Isrc);

public sealed record LyricsDoc(string Kind /* "synced" | "plain" */, LyricsLine[] Lines, string? Source, string? Language);
public sealed record LyricsLine(long StartMs, string Text, LyricsWord[]? Words);

public sealed record ArtworkAnswer(string? Url, string? StreamId, string? ContentType);   // StreamId → stream/open

// WaveeModule — additive virtuals; null = "not mine, ask the next one"
public virtual ValueTask<LyricsDoc?> FetchLyricsAsync(TrackRef track, CancellationToken ct) => default;
public virtual ValueTask<ArtworkAnswer?> ResolveArtworkAsync(string entityUri, CancellationToken ct) => default;
public virtual ValueTask<FactRow[]?> FetchFactsAsync(string entityUri, CancellationToken ct) => default;
```

Host: `ProviderChain<TReq,TRes>` — members ordered by `ProviderChainOrder.For(kind, contributions, userOrder)`
(pure), a per-member budget (lyrics 4 s, artwork 2 s, facts 2 s), a result cache keyed by `(kind, subject,
member, version)`, first non-null wins, a fault skips the member and is counted on the diagnostics page. Results
land **on the model through its single writer** — the lyrics sheet and `Controls.Art` never learn a provider
exists (derived facts live on the model; the DetailNotice pipeline carries provider failures).

`#48` (mix) is a later kind on this same chain: `transition/plan {from, to, facts} → {fadeMs, curve,
entryOffsetMs}`, consumed by the pure gate `EndgamePlan.Decide` (`Playback/Playback.Endgame.cs:53`). Not in scope.

## 9. Pages v2

`ModulePageDoc` today is fetch-once and static. v2 is three additive changes:

```csharp
public sealed record PageSection(string Kind, /* …v1… */)
{
    public string? Id { get; init; }            // addressable by patches and events
}
// new kinds (an unknown kind is skipped, not an error — already the rule):
//   "stats"     Items = tiles { Title = label, Meta = value, Subtitle = delta }
//   "chart"     Extra = { type: "bar"|"line", series: [{ name, points: [[x, y]…] }] }   (host palette, host axes)
//   "table"     Rows + Extra.columns
//   "tracklist" Extra.uris = ["spotify:track:…"]   ← the host draws REAL track rows from its own model
//   "form"      Extra.fields = ConfigField[] ; submit → page/event

// mod→host notification: replace one section in place; the host writes it into that section's signal
public sealed record PagePatch(string EntityId, string SectionId, PageSection Section);      // "page/patch"
// host→mod request: a semantic interaction
public sealed record PageEvent(string EntityId, string? SectionId, string Kind /* click|submit|rowActivated */,
    string? ItemId, Dictionary<string, string>? Values);                                      // "page/event"
```

Routes: `wavee://ext/<moduleId>/<entityId>` → `module:` + `ModuleUri.Encode(moduleId, entityId)`, which is how module
pages are already routed. A `contributes.pages[]` entry with `nav: "sidebar"` is replayed as a sidebar row through
the registrar. Budgets (`ModulePageBudget.Validate`) apply to patches too; a rejected patch is a typed error.

`tracklist` is the important one: an extension hands over **uris**, never rows, so its page gets the host's real
track table — likes, playing indicator, context menu, virtualization — for free, and cannot fake any of it.

## 10. `Wavee.Module.Timecode` (#154)

The issue's "waveform encoding dropdown" rests on a misunderstanding @s0 already corrected in the thread: CLX,
Art-Net timecode, TCNet and MTC are **network / MIDI state protocols sent alongside normal audio**. Only LTC is a
waveform.

```json
{ "schemaVersion": 2, "id": "wavee.timecode", "version": "1.0.0", "displayName": "Timecode",
  "publisher": "wavee", "protocolVersion": 1, "entry": "Wavee.Module.Timecode.exe",
  "capabilities": ["events", "settings", "pages"], "urlPatterns": [],
  "permissions": ["playback.read", "catalog.read"],
  "activation": ["onPlayback"], "events": ["playback.state", "playback.track", "settings.changed"],
  "contributes": { "settings": { "fields": [
    { "key": "enabled",  "kind": "bool",   "label": "Send timecode", "default": "false" },
    { "key": "protocol", "kind": "enum",   "label": "Protocol", "values": ["clx", "artnet"], "default": "clx" },
    { "key": "target",   "kind": "string", "label": "Target address", "default": "239.0.0.1" },
    { "key": "deck",     "kind": "int",    "label": "Deck", "min": 1, "max": 4, "default": "1" },
    { "key": "offsetMs", "kind": "int",    "label": "Offset (ms)", "min": -2000, "max": 2000, "default": "0" } ] } } }
```

```csharp
sealed class TimecodeModule : WaveeModule
{
    PlaybackStateEvent _last = Idle;                       // written on the RPC thread, read by the sender
    readonly UdpClient _udp = new();

    public override ValueTask OnPlaybackStateAsync(PlaybackStateEvent e, CancellationToken ct)
    { Volatile.Write(ref _last, e); return default; }

    /// <summary>Where playback IS, from the last host sample and our own QPC — no 60 Hz IPC.</summary>
    static double PositionSeconds(PlaybackStateEvent s, long nowQpc, int offsetMs)
    {
        if (s.State != "playing") return s.PositionMs / 1000.0;
        double elapsed = (nowQpc - s.AsOfQpc) / (double)s.QpcFrequency;
        return (s.PositionMs - s.OutputLatencyMs + offsetMs) / 1000.0 + elapsed * s.Rate;
    }

    async Task SendLoopAsync(CancellationToken ct)          // started from InitializeAsync when enabled
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 60));
        var buf = new byte[256];
        uint seq = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            var s = Volatile.Read(ref _last);
            int n = Clx.WriteDeck(buf, deck: _deck, positionSeconds: PositionSeconds(s, Stopwatch.GetTimestamp(), _offsetMs),
                                  bpm: _bpm, lengthSeconds: s.DurationMs / 1000.0, pitch: s.Rate, sequence: seq++);
            _udp.Send(buf.AsSpan(0, n), _target);           // UDP 3650; 0x01 + MessagePack map
        }
    }
}
```

- `Clx` is a hand-rolled MessagePack **writer** (fixmap, str, float64, uint) — about 80 lines, AOT, no dependency,
  unit-tested byte-for-byte against the CLX spec's examples. Meta packet (`0x02`) on `OnPlaybackTrackAsync`, BPM
  and key from `host/catalog/get`.
- There is **no beatgrid** anywhere in the tree (kind 222 is tempo + key only), so `Position2` and `Beat` are
  omitted, not invented. If a beatgrid source ever exists it arrives as a `facts` provider.
- Verified against @s0's Timecode Toolbox (CLX input): play, pause, seek, rate, track change; drift over 10 min.
- **LTC is phase 2** and the only part that needs engine work: a module-served PCM stream mixed to a *second*
  WASAPI endpoint (`host/audio/auxOutput`). Filed as its own issue; it does not block closing #154's network half.

## 11. `Wavee.Module.Spotify`

### The cut

| Moves to the module | Stays in the host |
|---|---|
| All of `Spotify/Spotify.Audio.cs`: the ladder (`Format`…`PickRung`/`Choose`), metadata reads, storage-resolve + mirror cache, the key *client* + the AP latch, `Ctr`, `Head`/`HeadCache`, gain rules, `Open`/`Prefetch`/choice cache | The AP socket + `Spotify.AudioKey` packets (→ `spotify/audioKey`), session, tokens, client-token (→ `host/auth/*`) |
| All of `Spotify/Spotify.Audio.Stream.cs`: `Stats`, `ReadAheadBudget`, `IRangeSource`, `DiskCache` (over the SDK's `ChunkDiskCache`), `Fetcher`, `Ring`, `Body` (**→ implements `IModuleStream`**), `OpenSeams`, `OpenBody` | `Playback.Audio` whole: pump, decoders, `HandOff`/`EndgamePlan`, splices, `NormalizationFactor`, `StarvePolicy`, devices, position sampling |
| The PlayPlay seams (`Spotify.Audio.cs:173-195`) | Connect encode/publish, telemetry, queue, kind-222 analysis |

**PlayPlay is an in-module sub-capability, not a second module.** The deriver and the body decryptor run per open
and per landed range (`Spotify.Audio.cs:788`, `Spotify.Audio.Stream.cs:1375`) — a second IPC hop there is a
regression, and `BodyDecrypt(Span<byte>, long)` cannot cross a process boundary without copying every byte twice.

```csharp
// Wavee.Module.Spotify/IKeySource.cs — public; the private repo supplies the only non-null implementation
public interface IKeySource
{
    bool CanDerive { get; }
    ValueTask<byte[]?> DeriveAsync(KeyRequest request, CancellationToken ct);
    BodyDecrypt? DecryptorFor(string fileIdHex);
}
```
```xml
<!-- Wavee.Module.Spotify.csproj — the conditional source link that leaves Wavee.csproj:164-171 -->
<ItemGroup Condition="'$(WaveeSkipPrivateSources)' != 'true' And Exists('..\..\Wavee.PlayPlay\Client\PlayPlayHost.cs')">
  <Compile Include="..\..\Wavee.PlayPlay\**\*.cs" Exclude="…bin…;…obj…;..\..\Wavee.PlayPlay\Tests\**" />
</ItemGroup>
```

Consequences: `WAVEE_PLAYPLAY_LOCAL` and `PlayPlayHost.Install()` (`App.cs:111-113`) leave `Wavee.exe`; the native
runtime loads in the **module's** process, so a runtime crash is a module restart, not an app crash; the runtime
store moves to `<DataDir>\runtimes\<appVersion>\<arch>\`; `Setup.RuntimeHost` is replaced by the generic
`module/status|action|progress` setup card; a public-only build still plays Ogg through AP keys.

### Five host untangles, before anything moves

| # | Tangle | Fix |
|---|---|---|
| 1 | `Spotify.Audio.Format` is the app's universal codec enum (40+ sites: local files, radio, podcasts, `Modules.Host.cs:1571`, `Modules.cs:297`) | Neutral `AudioCodec` in `Wavee.Sdk`. |
| 2 | `StarvePolicy.RefusedFailMs`, `InterruptedRead`, `StarvedRead` are defined from `Spotify.Audio.Body` (`Playback.Audio.cs:169,1305,1309`) | SDK stream sentinels + a documented starved / interrupted / EOF contract on `IModuleStream`. |
| 3 | `RingSource` is typed on `Spotify.Audio.Body` (`:1857`); `Seek` downcasts to it (`:614`) | An interface (random access + normalization + `InterruptPendingRead`) that `ModuleByteStream` implements — keeps the Ogg bisect seek tier for module streams. |
| 4 | `ResolvedPlayable` carries `GainDb` but not `Peak` | Add `Peak`; `NormalizationFactor`'s true-peak cap survives. |
| 5 | `Spotify.Audio.CanDerive` feeds `DeviceIdentity.SupportsLossless` (`Playback.Host.cs:289`) and the settings rung count | A module-reported cap on `module/status`. |

Also: `Playback.Audio.Prefetch` drops its Spotify-only guard (`:598`) and routes to `playback/warm`; podcast and
module-progressive bodies stop going through `Spotify.Audio.ExternalChoice` (`:3162`, `Modules.Host.cs:1617`) and
use a neutral `PlainHttpSource` in `Wavee.Sdk.Streams`; `cacheBudgetBytes` stops being a hard-coded `0`
(`Modules.Host.cs:604`). When the module plays, the in-proc path is **deleted** — no fallback arm.
Budget: instant start within **+30 ms** across the boundary; ciphertext at rest only, in the module's cache.

## 12. Wireframes

```
 Settings ▸ Extensions
 ┌──────────────────────────────────────────────────────────────────────────────────────────┐
 │ Installed                                                          [ Browse ] [ Install… ]│
 │ ┌──────────────────────────────────────────────────────────────────────────────────────┐ │
 │ │ ◉ Spotify playback        ✔ Official            1.0.0   Running     [ ⚙ ] [ ⋯ ]      │ │
 │ │ ◉ Timecode                ✔ Official            1.0.0   Idle        [ ⚙ ] [ ⋯ ]      │ │
 │ │ ◉ Last.fm                 ✔ Verified publisher  0.4.1   Running     [ ⚙ ] [ ⋯ ]      │ │
 │ │     by arcanewizards · playback.read · catalog.read · storage.private                │ │
 │ │ ○ My Stats (dev)          ⚠ Unsigned            0.0.1   Faulted ×3  [ Retry ] [ ⋯ ]  │ │
 │ └──────────────────────────────────────────────────────────────────────────────────────┘ │
 │ Providers                         drag to reorder — first answer wins                     │
 │   Lyrics    ≡ LRCLIB   ≡ Wavee (Spotify)                                                  │
 │   Artwork   ≡ My Overrides   ≡ Wavee (Spotify)                                            │
 │ ▸ Developer mode  (loads unsigned extensions from the user store; shows a banner)         │
 └──────────────────────────────────────────────────────────────────────────────────────────┘
   [ ⚙ ] → the module's contributed settings, generated from ConfigField[] — the sidebar options popover's generator
   [ ⋯ ] → Signature…  ·  Diagnostics  ·  Disable  ·  Remove

 Consent sheet (Community tier, first install or a widened permission set)
 ┌────────────────────────────────────────────────────────────┐
 │  Install "Deck Sync"?                                       │
 │  ⚠ Community extension — signed by a key Wavee has not      │
 │    verified.  Key  3F:9A:…:C2  will be remembered.          │
 │                                                             │
 │  It asks to:                                                │
 │   ☑ Read what is playing                                    │
 │   ☑ Read track details (title, tempo, key)                  │
 │   ☐ Control playback                       ← off by default │
 │                                                             │
 │  It runs as a separate program on this PC and can use the   │
 │  network on its own.                                        │
 │                              [ Cancel ]  [ Install ]        │
 └────────────────────────────────────────────────────────────┘
```

Component tree (one page, everything data-driven from `Modules.State` signals — no props that change after mount):

```
ExtensionsPage
 ├ InstalledList            ItemsView over Signal<ExtensionRow[]>   (item root Grow 1 / Basis 0)
 │   └ ExtensionRowView     TierBadge · StateChip · SettingsButton · OverflowMenu
 ├ ProviderOrderSection     one ReorderList per provider kind  → Prefs (ProviderChainOrder input)
 ├ DeveloperModeToggle
 └ overlays: ExtensionSettingsSheet (ConfigField generator) · SignatureSheet (generalized from
             Shell.OpenSignatureDialog) · ConsentSheet
```

## 13. Waves, files, tests

Implementation is parallel subagents on **disjoint files**; only the orchestrator builds, tests or launches, and
not between waves unless asked.

| Wave | Delivers | New / touched files (disjoint per agent) | Pure classes under test |
|---|---|---|---|
| **P0** | This doc; stale-path refresh in `playback-modules.md`; issues filed (each modifying `gh` call approved first) | docs only | — |
| **P1 Trust** | manifest v2, envelope, policy, spawn pin, pointer + rollback, release signing | `Wavee.Sdk/ModuleManifest.cs`, `Wavee.Sdk/ModuleSignature.cs` · `Platform/Modules.Trust.cs` (new) · `Platform/Modules.Host.cs` (Probe, Rank, Spawn, gate) · `Wavee.ReleaseTool/SignModule.cs` + `ops/build/pack-module.ps1` + `ops/release/*` | `ModuleTrustPolicy`, `ModuleActivePointer`, `LockVerifier` |
| **P2 Primitives + Timecode** | activation, events, host API, settings contributions, the module → closes #154 | `Wavee.Sdk/ModuleEvents.cs`, `WaveeModule.cs`, `IModuleHost.cs`, `ModuleRunner.cs` · `Platform/Modules.Events.cs` (new), `Modules.HostApi.cs` (new) · `modules/Wavee.Module.Timecode/**` (new) · `Screens/Settings.Extensions*.cs` (new) | `ActivationPlanner`, `PlaybackStateProjection`, `Clx` writer, `ConfigFieldRules` |
| **P3 Providers + pages v2** | chains for lyrics/artwork/facts, page patches/events, sidebar manifest replay, sample `Wavee.Module.LastFm` | `Wavee.Sdk/Providers.cs`, `ModulePage.cs` · `Platform/Modules.Providers.cs` (new) · `Shell/Lyrics.Host.cs` (chain member `wavee`) · `Screens/ModulePage*.cs` · `Shell/Sidebar.Host.cs` (replay) | `ProviderChainOrder`, `PagePatchRules`, `ModulePageBudget` (patches) |
| **P4 Spotify module** | untangles 1–5 → host services → module → delete the in-proc path | `Wavee.Sdk` (codec, sentinels, `Peak`) · `Playback/Playback.Audio.cs` · `modules/Wavee.Module.Spotify/**` (new) · deletes `Spotify/Spotify.Audio*.cs`, `Wavee.csproj:164-171` | the moved ladder/cipher/ring tests travel with the code |
| **P5 Distribution** | `IModuleProvisioner`, root-signed feed + revocation, Browse/Install/Update/Remove | `Platform/Modules.Provisioner.cs` (new) · `Screens/Settings.Extensions.Browse*.cs` | `FeedVerifier`, `ProvisionPlan` |

Untangles 1–4 of P4 touch only `Wavee.Sdk` + `Playback.Audio.cs` and can run beside P2.

Trust tests (P1), each a named case: tampered file · tampered manifest (a permission added) · cert not by root ·
expired cert · id outside prefixes · `wavee.` id under a verified cert · planted unlisted DLL · revoked version /
publisher / hash · root rotation (signed by `next`) · pinned key changed · unsigned without developer mode ·
permission above the tier's ceiling · pointer rollback after three failed inits.
Wire round-trips use `ModuleTestHost` and the in-proc `MemoryPipe` channel (`Modules.Host.cs:2833`). No test reads
production source.

Gates before "done": `dotnet build Wavee.slnx` Debug **and** Release, `dotnet test src/apps/Wavee.Tests`,
`Invoke-Pester -Path ops/release/tests`.

## 14. Open items

- **Root key custody.** Where the offline root and the online `official` publisher key live (Azure Key Vault vs a
  hardware token vs a DPAPI-protected file). Azure Trusted Signing signs Authenticode digests, not arbitrary
  blobs, so it does not cover the envelope. Decide before P1's tooling agent starts.
- **LTC / `host/audio/auxOutput`** — engine work in `..\fluent-gpu` (a second `IAudioEndpoint`); own issue.
- **AppContainer launch** for Community modules — later hardening.
- **SDK on NuGet** — `Wavee.Sdk` has zero project references already; publishing it is a P5 checkbox.
- **R&D session with @s0 / the #48 reporter** — the `transition/plan` provider shape should come out of it.
