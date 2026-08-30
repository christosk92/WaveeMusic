# Spotify music video in the FluentGpu Wavee app — DRM, playback integration, and placement

**Status:** plan, not implemented. **Scope:** the app-side work to play Spotify video (Canvas + full music
videos) in `src/apps/Wavee/` on top of the shipped engine media stack, with three areas taken in depth:
**(2) the PlayReady DRM design, (3) how it ties into the existing playback stack, (4) where the video is
shown (the placement/overlay UX).**

**PlayReady only. There is no Widevine fallback in this plan** — by explicit decision. FluentGpu has no
embeddable Widevine CDM and hosting Chromium/WebView2 is off the table. If a track is Widevine-only, it is
simply *not playable* here (see the gating probe, §1). We do not add a WebView2 lane.

**Out of scope / fenced (per repo `CLAUDE.md`):** the PlayPlay *audio* scheme and `src/apps/.native/**`,
`src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`, the playplay ops tools/docs. Video DRM is unrelated to
PlayPlay.

**Sits on:** `docs/plans/media-playback-api-spec.md` (unified media API, LANDED M0–M5),
`docs/plans/video-compositing-spine-design.md` (the DRM-free spine), `docs/plans/video-drm-layer-design.md`
(native in-process PlayReady, SOLVED as M5). **Reference (read, not copied):** `C:\WAVEE\WaveeMusic`.

---

## 1. The one gating fact — measure before building Lane 2

Whether *any* of the DRM work is worth doing hinges on one unmeasured fact: **does Spotify serve this desktop
client a PlayReady/mp4/H.264 profile?** WaveeMusic *plays* Widevine, but its v9 manifest advertises **both**
PlayReady (mp4/H.264) and Widevine (webm/VP9); it merely picked Widevine because native PlayReady was broken
in WinUI 3 (`0xC00D715B`), a platform bug — not content scarcity. FluentGpu has since solved native PlayReady.

**With no Widevine fallback, this probe is a hard go/no-go**, not a routing choice:

```powershell
$env:WAVEE_AUDIO_FORMAT_PROBE = "1"   # log in, play music-video tracks, read the log:
#   probe video manifest <id>: drm=0:playready:license=<set>, 1:widevine:license=<set>; profiles=...:mp4:h264:...
```

`AudioFormatProbe` (`src/apps/Wavee/SpotifyLive/Audio/AudioFormatProbe.cs`) already derives the `manifest_id`
from `track.OriginalVideo[].Gid` (hex) + the `VIDEO_ASSOCIATIONS` linked URI, GETs
`/manifests/v9/json/sources/{id}/options/supports_drm` with the required `Origin`/`Referer =
https://xpui.app.spotify.com`, and logs `key_system` + profile `file_type`/`codec` per profile.

- **`playready` + an `mp4`/`h264` profile present** → Lane 2 (music videos) is viable. Proceed.
- **`widevine`/`webm` only** → music videos are **not shippable** under this plan. Ship **Canvas only** (§5),
  which is DRM-free and unaffected.

Everything in §2–§4 assumes the probe came back PlayReady-positive.

---

## 2. The DRM design, in depth

### 2.1 The security posture and the single attach point

The engine **never sees a content key or a decrypted pixel**. Decryption + decode happen inside Media
Foundation's protected pipeline (the OS PlayReady CDM, possibly in Windows' own `mfpmp.exe`); the output is a
**protected DirectComposition surface handle**. That handle binds at exactly one place —
`VideoBinding.Bind(nuint)` → `IVideoPresenter.BindSurfaceHandle` — identical to a *clear* video surface.
Nothing in the renderer, hole-punch, crossfade, or registry differs for protected content. DWM enforces output
protection below the handle; protected surfaces **read back black in any screen capture** (including
`--screenshot`), which is expected and bounds how we can test (§2.8).

### 2.2 The native ABI (already built, `FluentGpu.PlayReady.Native.dll`)

`DesktopProtectedVideoPlayer` (`src/FluentGpu.WindowsApi/Media/PlayReady/`) runs the native CDM on a background
**MTA thread** and exchanges POD structs over `LibraryImport` P/Invoke:

- **Open:** `FgPlayReadyRunEx(baseDir, ref FgOpenDescNative desc, licenseCallback, licenseCtx)`. `FgOpenDescNative`
  carries: `Mode` (0 = protected CENC custom source, 1 = clear diagnostic), `InitUrl`, `SegmentBaseUrl`,
  `SegmentPrefix`, `SegmentSuffix`, `StartNumber`, `SegmentCount`, `Pssh`+`PsshLen`, `HttpHeaders`,
  `LicenseServerUrl`.
- **Transport:** `FgPlayReadyPlay/Pause/Seek/SetVolume/SetRate/Stop` — each returns a monotonic **sequence**;
  applied-sequence counters in the snapshot acknowledge them (so a `PlayAsync` can await that its command
  actually took, `AwaitAppliedAsync`).
- **Snapshot (polled each UI-pump frame):** `FgPlayReadyGetSnapshot(out NativeSnapshot)` →
  `{ State, ErrorHr, Handle, Width, Height, PositionMs, DurationMs, Play/Seek/Volume/RateAppliedSeq }`.
  `Handle` is the protected DComp surface handle; because the native DLL lives *in this process*, it is the
  original handle — **no `OpenProcess`/`DuplicateHandle`/IPC** (the pass-3 UWP-sidecar architecture is dead).

**The load-bearing limitation for Spotify:** segment addressing is **numeric and monotonic** —
`SegmentBaseUrl + SegmentPrefix + <StartNumber..StartNumber+SegmentCount> + SegmentSuffix`. Spotify's v9
segments are **absolute-timestamp addressed with a 4 s stride** (0, 4, 8, …). A 4 s stride cannot be expressed
as `StartNumber + 1`. This is the one real impedance and it drives a decision (§2.6, §8).

### 2.3 The managed seam chain (how a license request flows)

```
app: MediaPlayerBuilder.WithDrm(relay)            // MediaPlayer.cs:461  → _licenseRelay
  └▶ MediaOpenOptions.LicenseRelay                // MediaPlayer.cs:301
      └▶ ProtectedMediaBackend.OpenAsync(source.With(DrmConfig), opts)   // routed here iff source.Drm != null
          └▶ BuildRequest(...) → ProtectedVideoRequest { LicenseRelay, InitUrl…, Pssh, DrmConfig }
              └▶ ProtectedMediaSession(player, request, opts)  → IMediaSession + IVideoSurfaceSession
                  └▶ DesktopProtectedVideoPlayer.Start(request)  // seeds transport, spawns MTA thread
                      └▶ native CDM raises KeyMessage(challenge)
                          └▶ LicenseThunk (UnmanagedCallersOnly, stdcall)   // copies challenge out of native mem
                              └▶ DrmLicenseBridge.Resolve(challenge, keyId) // bounded, runs relay on a worker
                                  └▶ app relay: POST challenge → Spotify /playready-license → license bytes
                              └▶ native `deliver(license)` → CDM Update() → key USABLE → decrypt/decode
                          └▶ snapshot.Handle != 0 → ProtectedMediaSession.PumpVideo → binding.Bind(handle)
```

- **Routing:** `MfMediaPlayer` sends a `MediaSource` carrying a `DrmConfig` (from `source.With(drm)`) to
  `ProtectedMediaBackend`; a clear source goes to `MfMediaSession`. `ProtectedMediaBackend.Capabilities` =
  `SupportsVideo, SupportsDrm=true, SupportsAudioGraph=false`, `IsSupported` gate = `H264`/`Hevc`.
- **`DrmLicenseBridge.Resolve`** runs the async relay on a worker and blocks **only** the native CDM thread up
  to `LicenseTimeout` (default 30 s). A null relay, a throw, a timeout, or an empty license each become a typed
  `MediaError{ Category.Drm, Recovery.NeedsLicense }` — **never a silent proceed-without-key** (the thunk
  returns `0x8004110E` DRM_E_CH_BAD_KEY-shaped and the session surfaces the error).
- **`ProtectedMediaSession.PumpVideo`** (UI thread) maps the native snapshot → `MediaSignalSink`
  (state/position/duration/natural-size/commands), binds the protected handle once, and calls
  `binding.SetContentSize(naturalSize)` + `Place(videoRect)` + `SetVisible(true)`. The play/pause level is
  reconciled *natively* (the MTA loop re-asserts Play until the clock advances) so the managed side never
  clobbers a Seek.

### 2.4 The Spotify license relay — the only Spotify-aware code

A `SpotifyLicenseRelay : Func<LicenseRequest, ValueTask<LicenseResponse>>` (app-side, `Wavee`):

- On the CDM challenge, **POST to Spotify's webgate `/playready-license`** with `Authorization: Bearer <token>`
  + `client-token` + `Origin`/`Referer = https://xpui.app.spotify.com`, `Content-Type: text/xml; charset=utf-8`,
  SOAPAction `http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense`; return the license bytes.
- `PlayReadyLicense.HttpRelay(url, headerName, headerValue)` (`src/FluentGpu.WindowsApi/Media/PlayReady/
  PlayReadyLicense.cs`) is the existing generic template — it already POSTs `text/xml` SOAP with an optional
  custom auth header. The Spotify relay is that shape wired to the app's session transport
  (`ITransport`/`SpClient`) for Bearer + client-token + the xpui spoof.

The engine (`DrmLicenseBridge`, native CDM) stays entirely Spotify-agnostic — it hands out an opaque challenge
and takes back opaque license bytes.

### 2.5 PSSH / PRO / KID — the manifest → init-data math

For the mp4/PlayReady profile, the CDM needs the **PSSH** (PlayReady init data) and the correct **key id**:

- `encryption_data` in the v9 manifest is a base64 **CENC PSSH box**; the **PlayReady Object (PRO)** is inside
  it. `default_KID` comes from `profile.key_id` (base64, 16-byte CENC KID).
- **PlayReady KID = the CENC KID with the first 8 bytes byte-swapped** (the mixed-endian GUID). Port the exact
  swap from WaveeMusic's `SpotifyVideoManifest` — a wrong swap silently yields no license.
- The native side can also parse PSSH/KID from the init segment's `tenc`/`pssh` boxes (the WaveeMusic
  `Mp4InitSegmentProtectionParser` behavior); prefer passing an explicit `Pssh` on the request when the
  manifest carries it, and let native parse from the init segment as the fallback.

`DashSourceDescriptor` (the engine's parsed-manifest shape) already carries `Pssh` + `DefaultKid`; the license
server URL is **not** in the manifest — the app supplies it via `DrmConfig.LicenseServerUri` + the relay.

### 2.6 The new component — v9-JSON → `ProtectedVideoRequest` adapter

The one substantial new piece of DRM plumbing, in `Wavee.Core` (portable, `System.Text.Json` + span parsing,
**headless-unit-testable**). **Improvement over WaveeMusic:** skip its DASH-MPD synthesis round-trip entirely —
that MPD existed only to feed WinRT `AdaptiveMediaSource`, which FluentGpu does not use. Emit a
`ProtectedVideoRequest`/`DashSourceDescriptor` directly. The adapter:

1. Selects the **mp4 / `playready` profile** — start with a single ≤480p H.264 representation (the native CENC
   source is H.264-only; `IsSupported` allows HEVC but the demuxer is proven only on H.264).
2. Extracts PSSH, derives the PRO, reads `default_KID`, computes the PlayReady KID (§2.5).
3. Produces **segment addressing** bridging the timestamp/numeric impedance (§2.2). Two remedies, decide at §8:
   - **Preferred (managed-only, no C++ change):** expand the 4 s-stride timestamp template into an **explicit
     per-segment URL list** (or a managed timestamp-stride expander feeding the fetch loop). Keeps the proven
     native binary untouched.
   - **Alternative:** extend the native ABI **once** with a timestamp/stride segment mode. Cleaner long-term,
     but re-opens `ops/tools/playready-native` (arm64 rebuild) — a component whose real-device DRM success took
     a v6→v11 debugging arc to achieve.
4. Carries the license-server endpoint and any per-segment CDN auth (in-URL token vs `HttpHeaders`; §7 risk).

`ProtectedMediaBackend.BuildRequest` already accepts a `DashSourceDescriptor` and maps it straight onto the
native open ABI — the adapter produces exactly that, so the backend needs no change.

### 2.7 Error model — typed, never silent

Every DRM shortfall is a `MediaError{ Category.Drm, Recovery.NeedsLicense }` surfaced through the
`MediaSignalSink` → the session's `Error` state → (via the integration in §3) `PlaybackBridge.NotifyPlaybackError`
(a user-facing toast + the player-bar Error state with retry). A bad/absent license, a timeout, or a CDM
failure is **never** a silent black frame or a silent drop.

### 2.8 What is proven vs unproven (be honest)

- **Proven:** the entire pipeline end-to-end against the **Axinom singlekey H.264 CENC** test vector on the
  ARM64 dev machine (USABLE key → decrypt → decode → non-zero protected handle → composited black).
- **Unproven for Spotify (each a potential multi-day cycle):** Spotify's PSSH/PRO/KID vs Axinom's; whether the
  webgate `/playready-license` accepts the native CDM's SOAP `AcquireLicense` challenge as-is or wants a
  wrapper (WaveeMusic's PlayReady path was dead, so this was *never exercised live anywhere*); temporary vs
  persistent EME session; keyframe SPS/PPS prepend; segment CDN auth; the timestamp-segment addressing.
- **Testing limit:** protected pixels capture black, and licensing needs a real session — so live playback is
  **manual/integration verification only, on the ARM64 device**. What *can* be gated headlessly: the
  v9→`ProtectedVideoRequest` adapter (golden fixtures: profile pick, PSSH, `default_KID`, the KID byte-swap,
  segment addressing), the discovery logic, and the compositing spine (already covered). Do **not** claim a
  zero-alloc gate over the media engine (its allocs are cold: open/license/teardown).

---

## 3. How it ties into the existing playback stack

### 3.1 The two-brain reality (the thing to get right)

The Wavee app has **two independent playback brains**, and they are *not* the same object:

| | **Audio (authoritative today)** | **DRM video (this plan)** |
|---|---|---|
| Entry | `PlaybackController : IPlaybackPlayer` | engine `MediaPlayer` + `ProtectedMediaBackend` |
| Engine | `IAudioHost` = `FluentMediaAudioHost` (wraps `PcmAudioPlayer`/WASAPI + app codecs + PlayPlay decrypt) | `DesktopProtectedVideoPlayer` (native MF CDM) |
| State out | `AudioHostSignal` → `NowPlayingProjection.OnHostSignal` | `MediaSignalSink` (session-local) |
| Fold | `NowPlayingProjection` merges Connect-cluster + local snapshot + host signals → `IPlaybackState` | — (not folded today) |
| UI | `IPlaybackState` → `PlaybackBridge` signals → `PlayerBar` / SMTC | — |

So video cannot just "be played" — it must be introduced as a **third engine coordinated alongside the audio
host**, and its state has to reach the *same* `NowPlayingProjection` so the player bar, Connect, and SMTC stay
uniform. This is exactly what WaveeMusic's `PlaybackOrchestrator.SwitchToVideoAsync` does (stop audio + start
video at the live position, route video state back into the projection) — we do it FluentGpu-native.

### 3.2 A `VideoPlaybackHost` peer to the audio host

Introduce (app-side, `Wavee`) a `VideoPlaybackHost` that owns an engine `MediaPlayer` configured with
`ProtectedMediaBackend` + `WithDrm(spotifyLicenseRelay)` and drives it from a `ProtectedVideoRequest` built by
the §2.6 adapter. It exposes a small transport surface (`Load(manifest)`, `Play/Pause/Seek`, position/state
signals, first-frame) mirroring what `NowPlayingProjection` already consumes for audio.

`PlaybackController` gains a `PreferVideo` swap path (the seam `PlaybackBridge.PreferVideo` already exists):

- **Enter video** (user toggles the player-bar video button, or a queue item is a video association): resolve
  the `manifest_id` (§5 discovery), build the request, and — mirroring WaveeMusic — **flip the engine-active
  flag first, then run "audio host keeps playing / video host starts at the live audio position" in parallel**.
- **Exit video:** stop the video host, keep the audio host running (it never stopped — see §3.3), drop the
  video surface (`VideoBinding.Release`/`SetVisible(false)`), fall back to art/poster.

### 3.3 Audio strategy — master audio, slaved video (recommended)

**The native protected pipeline is video-only (no audio elementary stream), so the protected video is silent by
construction.** That is not a bug to fix here — it is the opportunity: **keep `FluentMediaAudioHost` as the
audio master** (it already nails device picker, EQ, gapless/crossfade, loudness, WASAPI clock, SMTC, and the
PlayPlay decrypt) and **slave the video clock to it.** This is strictly better than WaveeMusic, whose WebView2
audio bypassed the app mixer (no device routing, volume only via `video.volume`).

Mechanism:
- On enter-video, the audio host is **already** playing the track's audio; do **not** stop it. Start the video
  host at the audio host's live `PositionMs`, at rate 1.0.
- The engine's **WASAPI `IAudioClock` is the master timeline** (`media-playback-api-spec.md §7.6`). Each pump,
  compare the video snapshot `PositionMs` to the audio master; correct small drift with a rate trim
  (`SetRate` ±a few %) and, past a threshold (e.g. >250 ms), a single resync `SeekAsync` — never a per-frame
  seek (MF video seek is not smooth).
- **Correctness caveat (the honest risk):** a music-video *edit* can differ from the album master (length /
  arrangement), so audio-master-synced video can drift against the picture. For the common case (the video is
  the same recording as the track) this is fine. Where it matters, the **upgrade** is the CDM-decoded-audio
  path below.

**Upgrade path (deferred): CDM-decoded audio.** Extend the native CENC source to a second (audio) elementary
stream, decrypt+decode it through the same CDM, and route PCM into the existing `PcmAudioPlayer`/WASAPI graph
(so device/volume/EQ stay uniform). Heavier (native ABI + PCM hand-back), and only needed if the video's audio
must be the video's own track. **v1 = master-audio-synced; CDM-audio is a scoped follow-up.**

### 3.4 Projecting video state into the unified now-playing

`NowPlayingProjection` folds audio via `OnHostSignal(AudioHostSignal)`. Add a parallel **`OnVideoSignal`** (or
reuse the same `AudioHostSignal` shape) fed by `VideoPlaybackHost`, so while video is active the projection's
`IsPlaying`/`IsBuffering`/`Position`/`Duration`/error come from whichever engine is the *visible* transport —
but position/duration should stay **audio-master-derived** under §3.3 (video is the slave). Net effect:
`PlaybackBridge`, `PlayerBar`, Connect state, and SMTC keep working unchanged; the video is a visual layer, not
a competing transport. `PlaybackBridge.CurrentTrackHasVideo` (already wired from `store.GetVideoAssociation`)
gates the video button; `PreferVideo` (reset on each track) drives the swap.

### 3.5 Cross-backend queue (mixed audio ↔ video)

`media-playback-api-spec.md` already makes mixed audio↔DRM-video queues a v1 requirement, and the pieces exist:
`ProtectedMediaBackend : IPreparableBackend.PrepareAsync` **spins up the CDM + first protected frame ahead of a
join** (a hard cut, no co-mix — `ProtectedPreparedItem.AudioVoice = null`). Wire this so that when the next
queue item is a video association, the video host prerolls (CDM + license + first frame) before the boundary,
and the swap is glitch-free. Under the master-audio design, the audio host's existing prepared-next/crossfade
(`FluentMediaAudioHost : IPreparedAudioHost`) continues to own the *audio* join; the video host's preroll only
covers the picture.

### 3.6 Transport, seek, and the player bar

All transport stays routed through `PlaybackController`/`PlaybackBridge` (the on-screen controls and SMTC
already call it). Under §3.3, transport is applied to **both** hosts: play/pause/seek go to the audio master
and are mirrored to the video slave (video re-slaves to the audio clock after a seek). The player bar's
scrubber/position/duration continue to read `PlaybackBridge.PositionMs`/`DurationMs` unchanged.

---

## 4. Where to show the video — see the dedicated placement plan

**Placement is a substantial topic of its own and now lives in `docs/plans/wavee-video-placement-plan.md`** (a
UX verdict grounded in user-sentiment research + the FluentGpu engineering reality). This section keeps only the
facts the DRM/integration work depends on.

**Verdict headline (from the placement doc):** a **control-first, audio-anchored** system — video always
available, never forced; audio is the source of truth. Priority: **inline now-playing (must-have)** →
**fullscreen theatre (should-have)** → **lyrics-over-video (the differentiator)** → **detached always-on-top
floating window (must-have *in intent*, sequenced as an early engine fast-follow)**; **in-window PiP is only a
cheap bridge, not a substitute** for the detached window; **ambient/Canvas is deferred/default-off**. The
user-sentiment research overturned the earlier "in-window PiP first" lean: the single most-validated want is
video that stays visible *while working in other apps / on a second monitor*, which only a **detached** window
delivers — so do not let a cheap in-window PiP cannibalize it.

**Reliability contract the DRM/integration design must honor** (the #1 user complaint cluster): toggling video,
minimizing, or backgrounding **must never restart the audio session, reseek, or drop queue/position** — the
master-audio-synced design (§3.3) satisfies this by construction.

### 4.1 The hard engine constraint (still governs the DRM surface)

**Video composites only under the *primary* window's swapchain.** `DCompVideoPresenter.AttachChild` inserts
every video child visual under `D3D12Device.PrimarySwapchain.DcompRoot`. There is no per-popup / per-secondary
presenter, and `VideoBinding` is inert on a non-primary/non-composited window. Consequence: anything drawn
**inside the main window** can host video; anything in a **separate OS window cannot** (it would show no video)
without substantial new engine work.

### 4.2 Feasibility summary (full detail in the placement doc §5)

Inline, fullscreen, and in-window PiP are **app-level work on the shipped engine**, arbitrated by
`VideoSurfaceRegistry.TransferOwnership` (single-writer pump ownership of one slot). The **detached always-on-top
window is an engine project**: a second top-level `Win32Window`, a second swapchain + DComp root in
`D3D12Device`, a per-window `IVideoPresenter`, always-on-top plumbing, cross-monitor drag/resize + geometry
persistence, and **protected-surface binding under the second window's root** (the native protected handle is
process-local, so it stays valid in a second window — it just needs a presenter bound to that window's
swapchain; HDCP/output-protection applies per output). Sequenced as an early engine fast-follow per the verdict,
not deferred indefinitely.

### 4.3 The crossfade host (reused for every placement)

Every placement reuses the engine's 3-layer **album-art → blurred-poster → live-video** crossfade
(`video-compositing-spine-design.md §6`), driven by `VideoReady` (art/poster drawn at `1 − VideoReady`, the
hole punched at `VideoReady`) — no black first frame. This is the FluentGpu-native equivalent of WaveeMusic's
`VideoSurfaceHost` (Win2D blur + composition opacity crossfade), and it is already in the spine.

---

## 5. Canvas — DRM-free, ships first (condensed)

Independent of the DRM go/no-go. Canvas is a plain pre-signed CDN `.mp4` URL (`canvas.url`) embedded in
home/feed GraphQL — no manifest, no segments, no DRM. Play it on the **existing clear stack**
(`MediaSource.FromUri(canvasUrl)` on a clear `MfMediaPlayer`), looping + muted + `UniformToFill`. Two surfaces:
card-hover previews (one shared, leased, idle-torn-down player to bound decode-surface memory — port
WaveeMusic's `SharedCardCanvasPreviewService` idea, not its XAML) and the now-playing backdrop. Do **not** route
Canvas through the manifest/DRM path. This proves the acquisition→UI→compositing loop with zero DRM risk.

---

## 6. Milestones (sequenced; single-thread-correct)

| # | Milestone | Gate |
|---|---|---|
| **S0** | Run the probe (§1). PlayReady/mp4 present? | The `drm=…:playready…` log line for real tracks |
| **L1** | Canvas on the clear stack (hover + now-playing) | Canvas plays; crossfade clean; memory returns to baseline |
| **V-UX** | Placement scaffold: inline `MediaPlayerElement` + fullscreen (`TransferOwnership`) + in-window PiP (drag/resize) driven by a **fake/clear** surface | PiP drags/resizes over any page; inline↔PiP↔fullscreen share one slot; survives nav |
| **L2-D** | Discovery resolver in `Wavee.Core` (`VIDEO_ASSOCIATIONS` + `queryNpvArtist` + `manifest_id`) | Unit tests: audio URI → video URI + manifest_id |
| **L2-M** | v9-JSON → `ProtectedVideoRequest` adapter (KID swap, PSSH, segment addressing) | Golden-fixture unit tests green |
| **L2-R** | `SpotifyLicenseRelay` + `WithDrm`; drive one real Spotify PlayReady track | Protected video plays (black-in-capture); license once; bad license → typed `MediaError{Drm}` |
| **L2-I** | Integration: `VideoPlaybackHost` peer, master-audio-synced clock, `NowPlayingProjection` routing, `PreferVideo` swap | Video shows in the chosen placement, audio in sync, transport/SMTC uniform |
| **L2-Q** | Cross-backend queue (audio↔video) preroll via `IPreparableBackend` | Queue A(audio)→B(video) joins with no black gap |
| **(defer)** | Detached floating OS window (engine: 2nd window + 2nd swapchain + per-window presenter) | — |
| **(defer)** | CDM-decoded audio (native ABI + PCM hand-back) for exact video-audio fidelity | — |

## 7. Risks / open questions

1. **[GO/NO-GO]** Does Spotify serve playready/mp4 to this client? No Widevine fallback → this is binary (§1).
2. **Segment addressing** — timestamp/4 s-stride vs the native numeric ABI (§2.6). Verify against the
   `CencMediaSource.h` segment loop before choosing managed-expand vs native-ABI-extension.
3. **License envelope** — does webgate `/playready-license` accept the native CDM's SOAP `AcquireLicense`
   as-is? Never exercised live anywhere.
4. **A/V sync fidelity** — master-audio-synced video assumes the video's picture matches the album master; a
   video edit that differs drifts. CDM-audio is the fix but heavier.
5. **Segment CDN auth** — in-URL signed token (WaveeMusic used `credentials:'omit'`, suggesting yes) vs headers
   → whether `HttpHeaders` is load-bearing.
6. **Proven only on Axinom H.264** — Spotify bring-up may take several bespoke cycles, verifiable only manually
   on the ARM64 device (protected pixels are black).
7. **arm64-only native DLL** — an x64 ship target needs a `ops/tools/playready-native/build.cmd` build.

## 8. Decisions for the user

1. **Placement:** owned by `docs/plans/wavee-video-placement-plan.md §8` — the call is how far to commit to the
   **detached always-on-top window** (the most-validated want, an engine project) vs a cheap in-window PiP
   bridge. The research-backed recommendation is **foundation-first**: ship inline + sticky toggle + fullscreen
   now, commit the detached window as a named near-term engine milestone, and **skip in-window PiP** so it can't
   cannibalize it.
2. **Audio for protected video:** master-audio-synced (recommended v1; reuses the whole audio stack; risk =
   video-edit drift) or invest in CDM-decoded audio now (exact fidelity, heavier)?
3. **Segment-addressing fix:** managed pre-expanded/timestamp descriptor (preferred, no C++) or a one-time
   native-ABI timestamp mode (touches the proven binary)?
4. **Ship Canvas independently of the DRM go/no-go?** Recommended yes.
5. **x64 native DLL** — build now or stay ARM64-only for the dev/ship target?

## 9. File map (where each piece lands)

- **Engine (reuse as-is):** `FluentGpu.WindowsApi/Media/PlayReady/*` (DRM), `FluentGpu.Engine/Media/Playback/
  {MediaPlayer,VideoSurfaceRegistry,MediaSeams}.cs`, `FluentGpu.Windows/{Media,Pal/DCompVideoPresenter}.cs`,
  `FluentGpu.Controls/Media/MediaPlayerElement.cs`.
- **New — `Wavee.Core` (portable, tested):** the v9-JSON→`ProtectedVideoRequest` adapter; the discovery
  resolver (assoc + NPV + manifest_id); the KID byte-swap + PSSH/PRO helpers.
- **New — `Wavee` app:** `SpotifyLicenseRelay`; `VideoPlaybackHost` (engine `MediaPlayer`+`ProtectedMediaBackend`
  peer); the enter/exit-video swap in `PlaybackController`; `OnVideoSignal` routing in `NowPlayingProjection`;
  the placement components (inline surface, fullscreen view, in-window PiP) under `Features/Shell` +
  `Features/Video`; Canvas plumbing (`canvas.url` from the feed GraphQL).
- **Canon:** app-side only unless §2.6 extends the native ABI — if it does, reconcile
  `design/subsystems/media-pipeline.md §8` and run `docs/design/check-canon.ps1`.
