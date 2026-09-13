# Wavee 0.3 — the video engine: song↔video switching and seeking, implementation plan

Status: DRAFT for approval, 2026-09-13. A named partial beside the master plan
(`docs/plans/wavee/wavee-0.3-implementation.md`, §2 `Playback/` + `Shell/`, §5 Waves 3-4, A13) and beside chapter 24
(`docs/plans/wavee/wavee-0.3-ui/24-video-surfaces.md`). It supersedes the two "smooth switching" plans of 2026-09
(`docs/plans/wavee/video-smooth-switching-implementation.md`, app; `..\fluent-gpu\docs\plans\video-smooth-switching-
implementation.md`, engine — both LANDED on the engine side, §1.2) in one respect only: those plans made the *clear*
path warm and left the *protected* path "create-per-open, bounded, measured, not hidden" (engine plan, cross-plan
contract). Every Spotify music video is protected. This plan makes the protected path warm.

Rules P1-P16 / C1-C10 (`C:\Users\ChristosKarapasias\Documents\relationships_wavee.md` §5.11-5.12) apply: CORE
allocates nothing after warm-up, no LINQ / closures / async / boxing in CORE, always-on logs and no environment
switches (CLAUDE.md), no legacy paths, NativeAOT, Windows only. Engine-side changes are DESCRIBED here with their
paths under `C:\wavee\fluent-gpu` and are implemented and gated in that repo (`dotnet build src/FluentGpu.slnx` Debug +
Release, VerticalSlice, `FluentGpu.Windows.Tests`); nothing in this plan edits the engine.

The PlayPlay derivation is a private repository and is not touched, named or depended on here. PlayReady is the
licensed OS path (`..\fluent-gpu\docs\plans\video-drm-layer-design.md` §1) and the engine's native CDM helper under
`ops\tools\playready-native` IS in scope: this plan changes it.

Christos's words, which are the acceptance criterion: *"switching to video is super slow, takes like 5 seconds to
switch to video, seeking is buggy and slow... switching from song to video and back to song in YouTube Music is just
super smooth."*

---

## 0. The decision, in three sentences

1. **The native PlayReady helper becomes a process-lifetime RUNTIME with session handles instead of a
   process-global singleton that is built and torn down on every open.** `FgPlayReadyRunEx` (one blocking call that
   does `MFStartup` → ten serial CDN GETs → D3D11 → CDM + PMP → license → engine → CANPLAY → handle, and releases
   *all* of it at the end — §1.3) is replaced by `FgPrRuntime` (MF, the D3D11 video device + DXGI manager, ONE
   `IMFMediaEngine` in windowless-swapchain mode, ONE CDM with its PMP host — created on first use, kept warm on the
   same 30 s idle policy the clear engine already has) plus `FgPrLicense` (a KID-keyed cache of open CDM key
   sessions, acquired in the background the moment a manifest is known) plus `FgPrSession` (a `CencMediaSource` fed
   from a byte-bounded `SegmentStore`, opened AT the carried position, swapped onto the warm engine with one
   `SetSource`). A song→video switch is then a `SetSource` on a live engine whose license, init segment and first
   segments at the target are already in memory — not a cold open.
2. **Seeking is a pure planner over a keyframe table, executed as flush-not-recreate.** `Video.SeekPlanner` (CORE,
   Wavee) turns (target, mode, the known keyframe/segment table, the buffered ranges) into one of three verbs —
   *Instant* (a keyframe ≤ target is buffered), *Fetch* (one segment pair, video ∥ audio, then Instant), or *Coarse*
   (the scrub preview snaps to the closest known keyframe) — and the native session applies it immediately on the
   session's own thread (no 80 ms tick, no 200 ms poll, no 6 s "landed" window), repositioning the `CencMediaSource`
   to the previous keyframe with a discontinuity and letting the Media Engine decode to the exact PTS; the decoder is
   never recreated and the retained window is time-based (30 s behind / 60 s ahead, bytes-capped) so a near seek
   never touches the network.
3. **The rendering path stays the DComp child + hole-punch for protected content — it is already zero-copy and it
   is the only legal path for a PlayReady frame** (a protected swapchain cannot be sampled by our D3D12 shaders; the
   OS enforces output protection below the handle); what changes is that first-frame and position become
   event-driven (FIRSTFRAMEREADY through a native log/state callback, no 250 ms managed poll), the pump stays
   coalesced, and the audio hand-off across the switch is a deliberate sample-boundary cut with a short fade on the
   app's WASAPI graph while the video's own soundtrack starts through the engine's SAR (§3.1.4 — why it cannot be
   "the same clock continues": the music video's audio is a different master). Targets: first video frame ≤ 300 ms
   on a warm switch, ≤ 1 s cold (≤ 1.5 s for the first video of a process); near seek ≤ 150 ms, far seek ≤ 500 ms
   (§3.5). The pure pieces (planner, prefetch schedule, license-cache policy, the manifest parse) and the engine's
   native rework can start today; the reducer wiring lands with Wave 3 (G, H), and the surfaces consume the host
   contract in Wave 4 (K) without a single engine call of their own.

---

## 1. Research record — what is true today

### 1.1 What is wired in 0.3 today (`src/apps/Wavee/Playback/**`)

Read in full: `Playback.Video.cs` (1,137 lines, owner H), the reducer's `MediaSwitch` block and `Effects` in
`Playback.cs`, `Execute()` in `Playback.Host.cs`, the load entry of `Playback.Audio.cs`.

| Fact | Where |
|---|---|
| **No caller drives the video host.** `Execute()` reads the effect slots once and calls `Audio.Stop/Load/Pause/Resume/Seek/SetVolume/Prepare` — there is no `Video.*` call; `Audio.Load(row, id, kind, epoch, fromMs)` takes the `PlayableKind` and enqueues `LoadCoreAsync` regardless of it; `grep 'Video.Load(\|Manifest.Resolve('` over `src/apps/Wavee` (excluding `_old`) finds zero callers | `Playback.Host.cs:383-411`; `Playback.Audio.cs:359-365` |
| The reducer's kind rules are ported and pure: `KindOf`, `Decide → LoadOnCurrent | SwapThenLoad`, `AllowCrossfade` (audio-only, same kind), `ShouldStopOutgoingHost` (true on any kind change "so two decoders never both output audio at once"), `HostChanges` (only a Video boundary flips hosts) | `Playback.cs:466-509` |
| `Effects` has load/transport/seek/volume/gapless/connect slots and **no video slot** — chapter 24 §9 item 4 already asks for `Effects.VideoKindRefresh` + `Playback.VideoSurface` | `Playback.cs:1259-1299`; ch 24 `:1287-1293` |
| The host (as ported): ONE long-lived `MediaPlayer` (`BuildPlayer` → `MfMediaPlayer(new ProtectedMediaBackend(defaultRelay: null, descriptor: null))`, `WithDrm(RelayForward)`), a serialized latest-wins pump (`Load` → `s_pending`, `RunAsync` → `ApplyAsync`), `Plan(SwitchInput)` → `None | SeekOnly | Switch | Rebuild`, `SwitchInPlaceAsync` = `live.OpenAsync(source)` with a 15 s `OpenTimeoutMs`, `BuildAndOpenAsync` for a rebuild, `TeardownAsync` (`PublishBinding(null)` BEFORE the bounded 5 s dispose) | `Playback.Video.cs:125-130, 230-240, 346-406, 408-477, 486-513, 554-565` |
| The 200 ms ticker is the only video timer: position, duration re-relay (MF revises DASH duration after LOADEDMETADATA), the live window, the carried-position seek, the watchdog, the state fold | `:172, 599-699` |
| **The carried position is applied AFTER the session is ready**: `IsSeekReady(state, durMs)` gates `Seek(target)` because "the open returns in ~30 ms on PlayReady while the native session is still spinning up, and a seek issued there is silently dropped" — so a song→video switch at 1:23 opens the video at 0:00 and seeks afterwards | `:637-647, 723-724` |
| The manifest resolve is ONE synchronous spclient GET, `/manifests/v9/json/sources/{id}/options/supports_drm`; the parse keeps H.264 + AAC rungs that carry the PlayReady `encryption_infos` index, extracts `encryption_data` (PSSH, base64) and `license_server_endpoint`, and builds a `DashSourceDescriptor` whose `SegmentStride` is the segment length in seconds (Spotify names segments by ABSOLUTE TIME); the initial rung is the highest ≤ 480p | `:777-819, 836-924, 928-1023` |
| The license relay POSTs the CDM's challenge to the manifest's endpoint (default `/playready-license`) over the authenticated session, synchronously, on whatever thread the CDM raises it | `:1099-1135` |
| `StartWatchdog` 25 s (Wavee) beneath the engine's 90 s; `PlayReassertBudget` 8 × 200 ms | `:135-168, 179, 669-680` |
| `Seek(ms, accurate = true)`: a committed seek at/past the live edge becomes `GoLiveAsync`; `SeekMode.Keyframe` is reachable only through the `accurate:false` argument, which nothing in the reducer passes | `:277-288` |
| What the catalogue knows: `Track.HasVideo` (kind-99 flag, `TrackFlags.VideoMask`) and `Track.VideoCounterpart` (a track slot filled from `VideoAssociations` kind 99 → `row.VideoUri`) — the manifest id is derivable at track start, before any user gesture | `Entities/Track.cs:113-127, 170, 297, 312, 520-531`; `Spotify/Spotify.Decode.cs:1204-1242` |

**Consequence.** In 0.3 as it stands the switch does not happen at all; what Christos measures is shipped 0.2.9
(§1.4), and the ported host reproduces its structure (one open per switch, position applied after start, a 200 ms
observation tick) on top of the engine path below. The rework therefore has three layers — the native runtime, the
engine session, the Wavee host/reducer wiring — and §1.3 shows the seconds are in the first.

### 1.2 The engine's video pipeline (`C:\wavee\fluent-gpu\src\**`)

Two backends fulfil the same `IMediaPlayer` (`MediaKind.MfVideoOrFile`): the CLEAR path (`MfMediaPlayer` →
`VideoMediaEngine` → `MfMediaSession`) and the PROTECTED path (`ProtectedMediaBackend` → `DesktopProtectedVideoPlayer`
→ the native DLL → `ProtectedMediaSession`). A source carrying a `DrmConfig` is routed to the second before the first
does anything (`MfMediaPlayer.cs:161-167`).

| Question | Answer | Where |
|---|---|---|
| **Which MF API?** | `IMFMediaEngineEx` in **windowless swap-chain mode** for both paths — never `IMFSourceReader`, never a hand-built `IMFMediaSession` topology. Frame-server mode (`TransferVideoFrame`) is not used | `VideoMediaEngine.cs:16-32, 239-244`; native N:2695-2708 |
| Hardware decode | A D3D11 device with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` on the SAME adapter as the D3D12 renderer (LUID-pinned, fallback default), `ID3D10Multithread::SetMultithreadProtected` (vtable slot 5 — "an earlier version wrongly called slot 3"), `MFCreateDXGIDeviceManager` + `ResetDevice`, attached as `MF_MEDIA_ENGINE_DXGI_MANAGER`; output format `DXGI_FORMAT_B8G8R8A8_UNORM` | `VideoMediaEngine.cs:262-327, 216-219` |
| How a frame reaches the UI | `GetVideoSwapchainHandle` (queried once per presentation epoch after LOADEDMETADATA) → snapshot → `MfMediaSession.PumpVideo` → `VideoBinding.Bind(handle)` → `VideoSurfaceRegistry.Drain` at phase 11 on the render thread → `DCompVideoPresenter.BindSurfaceHandle` = `IDCompositionDevice::CreateSurfaceFromHandle` → `IDCompositionVisual::SetContent`; the child visual sits z-BELOW the UI swapchain and is revealed through the `DrawVideoCmd` hole-punch. **FluentGpu never sees a decoded pixel**; MF presents into its own swap chain and DWM composites | `VideoMediaEngine.cs:531-538`; `MfMediaSession.cs:473-508`; `VideoSurfaceRegistry.cs:379-430`; `DCompVideoPresenter.cs:91-110, 206-223`; `DrawList.cs:353-358`; `media-pipeline.md` §8.1, §8.4 |
| The stream is sized to the natural frame capped to what the destination can show (`ContentSizeFor`), and DComp performs the fit (`SetTransform(scale)`); a 4K frame in a 640-px card does not allocate 4K buffers | `MfMediaSession.cs:561-580`; `DCompVideoPresenter.cs:244-254` |
| Threading (clear) | "Snapshot out, commands in": one MTA engine thread is the sole COM toucher and sole writer of a seqlock'd POD `VideoEngineSnapshot`; the UI pump reads one snapshot per turn and POSTS coalesced last-wins commands; the old blocking `Invoke<T>` (up to 7 × 50 ms per pump) is gone | `VideoEngineSeam.cs` (whole); `VideoMediaEngine.cs:34-52, 170-197` |
| Warm engine (clear) | `MfMediaPlayer.LeaseEngine/ReturnEngine`: ONE `VideoMediaEngine` survives many `OpenAsync`s (`PostSetSource` on the live engine); returned engines are paused + detached and disposed after `WarmIdleDisposeMs = 30_000` because "tens to well over a hundred megabytes stayed resident … nothing in the census able to name it" | `MfMediaPlayer.cs:18-32, 53-63, 98-154` |
| Sequencing of a switch | `MediaPlayer.OpenAsync`: sniff → resolve backend → **await the OLD session's `DisposeAsync`** → publish Opening → `backend.OpenAsync` → marshal `ConnectSignals` to the UI thread | `MediaPlayer.cs:319-393` |
| Seek (clear) | `SeekAsync(to, mode)` posts `Seek{a: seconds, i: approximate}`; the engine thread calls `SetCurrentTimeEx(APPROXIMATE)` for `SeekMode.Keyframe` else `SetCurrentTime`; SEEKING/SEEKED bits flow back through the snapshot; the scrub cadence is `SeekPreviewScheduler.IntervalMs = 100` on the element side | `MfMediaSession.cs:206-225`; `VideoMediaEngine.cs:395-408`; `SeekPreviewScheduler.cs:11` |
| Position clock | The engine's `GetCurrentTime` sampled at `PositionTimestamp`, extrapolated by elapsed·rate on the pump; position-only publishes coalesced to ~1 Hz; the engine self-refreshes at 250 ms active / 1 s parked | `VideoMediaEngine.cs:64-71, 199, 483-484`; `MfMediaSession.cs:517-524` |
| **Protected session (managed)** | `ProtectedMediaBackend.OpenAsync` builds a `ProtectedVideoRequest` from `DrmConfig.SourceDescriptor` and constructs a NEW `DesktopProtectedVideoPlayer` per open; `ProtectedMediaSession.ConnectSignals → StartOnce → player.Start(request)` spins a NEW MTA thread `fgpu-playready-desktop` that blocks in `FgPlayReadyRunEx` for the session's lifetime | `ProtectedMediaBackend.cs:75-85`; `ProtectedMediaSession.cs:127-139, 179-186`; `DesktopProtectedVideoPlayer.cs:151-203, 205-335` |
| Observation cadence (protected) | The session POLLS: `PumpPollMs = 250` while opening/seeking/playing, `TransportSettlePollMs = 1_000`; `player.Pump` reads the native snapshot (state, handle, size, position) and binds the handle every pump | `ProtectedMediaSession.cs:90-97, 380-505`; `DesktopProtectedVideoPlayer.cs:456-561` |
| Transport acks (protected) | Play/Pause/Seek are native one-shot slots applied by the native 80 ms keep-alive tick; the managed side polls the snapshot's `*AppliedSeq` every 10 ms for up to 5 s (`TransportAckBudgetMs`), representation switches 20 ms / 10 s | `DesktopProtectedVideoPlayer.Waits.cs:30-56` |
| Seek intent (protected) | `PublishSeekIntent` publishes the target optimistically, holds state = Buffering and suppresses the native position until: native within `SeekReachedToleranceMs = 750` of the target, OR ack + one 250 ms poll, OR `SeekSuppressMs = 6_000` expired | `ProtectedMediaSession.cs:69-81, 220-275, 472-503` |
| **`MediaOpenOptions.StartPosition` is honoured by the clear session (`ConnectSignals` posts a Seek) and IGNORED by the protected one** — `ProtectedVideoRequest`/`FgPlayReadyOpenDesc` carry no start position | `MfMediaSession.cs:162-163`; `ProtectedVideoTypes.cs:22-98`; `DesktopProtectedVideoPlayer.cs:695-720` |
| `PrepareAsync` (the engine's cross-backend preroll seam, `IPreparableBackend`) exists on the protected backend and **starts a whole second native player** and polls it 20 ms / 10 s — against a process-global singleton it can only answer `ERROR_BUSY` | `ProtectedMediaBackend.cs:88-112`; `QueuePreparation.cs:1-60` |
| BUSY self-heal: `FgPlayReadyRunEx` returning `0x800700AA` triggers `FgPlayReadyStop` + 200 ms event-wait retries for up to 5 s — "a previous session that was never stopped … wedges EVERY later video" | `DesktopProtectedVideoPlayer.cs:284-311` |
| Watchdogs: 90 s in the player and 90 s in the session ("above every native ceiling it supervises: 30 s licence + 45 s CANPLAY + 12 s surface handle"); 25 s in Wavee | `DesktopProtectedVideoPlayer.cs:57-65`; `ProtectedMediaSession.cs:83-88`; `Playback.Video.cs:139` |
| Logging | The protected path appends `[video]` lines to `%LOCALAPPDATA%\FluentGpu\PlayReady\desktop-playready.log` (a different file from Wavee's log); the clear path writes to `Console.Error` / `Diag` | `DesktopProtectedVideoPlayer.cs:71-76, 568-574`; `VideoMediaEngine.cs:632-636` |
| Dispose (protected) | `ProtectedMediaSession.DisposeAsync` → `Task.Run(player.Stop(); player.Dispose())` → `FgPlayReadyStop` + `_thread.Join(3_000)` | `ProtectedMediaSession.cs:702-717`; `DesktopProtectedVideoPlayer.cs:435-439, 626-634` |

**Consequences.** (a) The clear path is already the shape this plan wants — warm engine, snapshot/command seam,
one `SetSource` per switch — and it is the path no Spotify video ever takes. (b) On the protected path every layer
polls: native 80 ms tick, managed 250 ms pump poll, Wavee 200 ms tick, ack polls 10 ms; a state change is observed
up to 80 + 250 + 200 ms after it happens, and the observation also decides when the DComp handle is bound and when
`AudioSignal.Started` is posted. (c) The engine's warm-idle policy and its pump/ownership seam do not need to
change; the protected backend needs to be brought to the clear backend's shape, and the native helper is what
prevents that today.

### 1.3 The native PlayReady helper (`C:\wavee\fluent-gpu\ops\tools\playready-native\`)

Read: `PlayReadyNative.cpp` (3,428 lines, **N:**), `CencMediaSource.h` (1,435 lines, **C:**), `README.md`, `build.cmd`
(`FG_UWP FG_WIN32_PMP FG_DESKTOP_DLL`), and the managed mirror in `FluentGpu.WindowsApi\Media\PlayReady\`.

| Fact | Where |
|---|---|
| **Exports**: `FgPlayReadyRunEx(baseDir, desc*, licenseCb, ctx)` — blocks for the session's lifetime; a CAS latch `g_desktopRunning` answers `ERROR_BUSY` when a session is live; per call it does `CoInitializeEx(MTA)` + `MFStartup`, `RunCustomSourceAttempt`, then `MFShutdown`. `GetSnapshot/V2` (atomic reads), `Play/Pause/Stop/SeekEx/SetVolume/SetRate/SelectVideoRepresentation/ResetAdaptive` — all slot-based, applied by the 80 ms tick | N:2870-2980, 2998-3102, 519-600 |
| **Cold open order (strict series, one thread):** GET video init → `ParseInit` → GET audio init → GET **4 video + 4 audio segments serially** (`kInitialBurstSegments = 4`) → `D3D11CreateDevice` + DXGI manager → `CreateAndPrepareCdm` (`IMFMediaEngineClassFactory4::CreateContentDecryptionModuleFactory("com.microsoft.playready.recommendation")`, `IMFContentDecryptionModule` with `MF_CONTENTDECRYPTIONMODULE_STOREPATH`, `SetPMPHostApp`) → `CreateSession(TEMPORARY)` + `GenerateRequest("cenc", pssh)` → **poll `g_cdmUsable` every 200 ms up to 30 s** (the relay POST happens inside) → protection manager → `BuildCencSource` (2 streams) → spawn the feeder thread → `CreateTrustedInput` ×2 + ITA preflight → `IMFMediaEngine` create with `MF_MEDIA_ENGINE_EXTENSION` (a `cenc://` scheme handler returning the source), `MF_MEDIA_ENGINE_DXGI_MANAGER`, `ENABLE_PROTECTED_CONTENT`, `SetContentProtectionManager` → `EnableWindowlessSwapchainMode` → `SetSource("cenc://fluentgpu/protected.mp4")` → `Play()` → **poll CANPLAY every 100 ms up to 45 s** → `UpdateVideoStream` → **poll `GetVideoSwapchainHandle` every 60 ms up to 12 s** → keep-alive loop (80 ms: `ReconcileTransport`, position, `OnVideoStreamTick` + `UpdateVideoStream(nullptr)`) | N:2103-2862 (2148-2241, 2250-2278, 2080-2091, 2281-2318, 2612-2660, 2691-2714, 2718-2763, 2788-2836) |
| **Teardown releases EVERYTHING**: feeder join, `engine->Shutdown`, session, CDM, D3D device, then `MFShutdown` and the latch — the next open rebuilds the CDM and the PMP (`mfpmp.exe`) and re-runs the challenge | N:2845-2859, 2973-2980 |
| **The license is TEMPORARY and re-acquired on every open of the same content**; no `PlayReadyLicenseIterable`, no KID lookup, no `Load()` of a stored session; `MF_EME_PERSISTEDSTATE` optional; the comment: "Production = Spotify, which issues NON-persistable streaming licenses" | N:2073-2079, 1403-1478, 1426 |
| The relay: `KeyMessage` → `HandleCdmKeyMessage` parses the UTF-16 XML envelope, base64-decodes `<Challenge>`, collects `<HttpHeader>`s, calls the managed callback SYNCHRONOUSLY on the CDM thread, then `session->Update(license)`; USABLE arrives asynchronously via `KeyStatusChanged` — "right after a SUCCESSFUL Update() g_cdmUsable is legitimately still false" | N:1018-1165, 1098-1103, 1180 |
| `DrmLicenseBridge.Resolve` runs the relay on `Task.Run` and `task.Wait(30 s)` — a blocked CDM thread per challenge | `DrmLicenseBridge.cs:59-89` |
| **The demuxer**: fMP4 (`moov` + `moof/mdat`), `stsd` → `encv/avc1/avc3` (avcC, sinf/frma/schm/schi/tenc) or `enca/mp4a`, `pssh` collected; `ParseSegment` reads `tfhd/tfdt/trun/senc`, converts AVCC → Annex-B in place, prepends SPS/PPS to keyframes and widens `subsamples[0].clearBytes`; H.264 + AAC only, `nalLenSize == 4` only | C:281-318, 372-543, 341-369, 518-535 |
| **Segment addressing**: no `sidx`, no byte ranges, no managed byte stream — the descriptor's `base + prefix + (startNumber + i·stride) + suffix`, fetched natively with a shared WinRT `HttpClient` ("A per-request client … six serial handshakes before the first frame" — fixed); audio rides the same grid | N:2212, 2462-2473, 2551-2555, 1588-1599, 1614-1680 |
| The feeder: after the burst a background thread streams the rest with backpressure `kMaxSamplesAhead = 900` (50 ms sleep) and `kRetainBehind = 300` samples trimming (≈ 10 s of 30 fps video, ≈ 6 s of AAC) | N:2203, 2444-2451; C:734 |
| **Seek (native)**: `FgPlayReadySeekEx` writes a latest-wins slot (mode 0 exact → `SetCurrentTime`, 1 approximate → `SetCurrentTimeEx(APPROXIMATE)`); it is applied by the tick ONLY when `g_desktopSeekBufferedSeq == seekSeq` — instantly if `CanSeekTo` (keyframe + contiguous coverage), else the feeder rewinds to `targetMs / segmentDurationMs`, cancels the in-flight GET (10 ms cancel poll) and fetches the video+audio segments; then the source's `Start(pd, GUID_NULL, VT_I8)` repositions each stream to the last keyframe ≤ target with `m_discontinuity = true` — **no decoder recreation**; a paused seek adds `FrameStep(TRUE)` ("At rate 0 the MF video renderer does not pre-roll … Chromium's shipped workaround") | N:3086-3092, 522-548, 2801-2825, 2380-2386, 2584-2591, 1641-1661; C:668-713, 858-879, 1011-1077 |
| Decryption is not in software: samples carry `MFSampleExtension_Encryption_*` (KID, IV, subsample map), the stream type is `MFWrapMediaType(clear, MFMediaType_Protected)` + `MF_SD_PROTECTED`, and the source's `IMFTrustedInput::GetInputTrustAuthority` forwards to `cdm->CreateTrustedInput` — the PMP inserts the CDM decryptor MFT | C:906-961, 1280-1293, 1136-1175; N:2612-2618 |
| The video's AAC track is a second `IMFMediaStream` (same key, wrapped Protected) rendered by the Media Engine's own audio path (SAR); `SetVolume`/`SetPlaybackRate` go to `IMFMediaEngine` | C:1222-1246, 1355-1366; N:555, 563 |
| The frame reaches the app as the DComp swap-chain HANDLE published in the snapshot after `UpdateVideoStream` with a non-zero rect; the 80 ms tick then calls `OnVideoStreamTick` + `UpdateVideoStream(nullptr, …)` — unnecessary in windowless-swapchain mode (the engine auto-presents; the clear `VideoMediaEngine` never does this) | N:2746-2763, 2797-2800; `VideoMediaEngine.cs:30-32` |
| Admitted defects: "Downloading the whole track first (~50 serial requests for a 3.5-minute video, doubled once audio joins) is what made 'watch video' sit on a spinner for tens of seconds" (the burst is the mitigation); presentation duration "extrapolated from the burst … accurate to within one segment"; the ITA cache "keyed by stream id … the re-created authority is rejected as DRM_E_LOGICERR"; "passing the same PSSH again creates a second content binding whose ITA proxy is rejected" | N:2196-2198, 2288-2293, 2609-2611; C:1143-1146 |
| **Environment A/B switches still in production native code** (`FG_CENC_BAKED_AXINOM`, `FG_PLAYREADY_LICENSE_URL`, `FG_CENC_PERSIST_SESSION`, `FG_CENC_FORCE_SW_LAYER`, `FG_CENC_LEGACY_ENGINE_WIRING`, `FG_CENC_MARK_SD_PROTECTED`, `FG_CENC_NO_PROTECTED_WRAP`) — against CLAUDE.md's rule; and `FG_VIDEO_ZABOVE` in the presenter | N:1031-1037, 2078, 1504, 2682; C:1276-1281; `DCompVideoPresenter.cs:57` |
| `DashManifestParser.cs` (WindowsApi): `SegmentTemplate` + `$Number$` only (`SegmentBase`/`SegmentList`/`$Time$` throw), first H.264 representation only, no audio adaptation set — the Spotify path does not use it (Wavee builds the descriptor itself, §1.1) | `DashManifestParser.cs:114-116, 156-186, 267-352` |

### 1.4 How 0.2.9 did it, and where the five seconds go

**The log carries no video episode.** `%LOCALAPPDATA%\Wavee\logs\wavee-20260913.log` (the only file: 11,121
lines, one session of 10 h 18 m) has 389 `[video]` lines and every one is the docked host's verdict for a
`spotify:track:` playable — `docked host face=Cap mounts=False placement=None stageHosts=False owner=(none)
active=(none)` (143 ×) and `docked cap fit … natural=0x0 … source=none key=(none)` (245 ×); zero manifest, license,
PlayReady, SetSource, LOADEDMETADATA, natural-size, first-frame or video-seek lines exist in this build's output,
and the `[hydration.traits] video.assoc.recover.done` lines (8, all within 6.4 s of boot; `suspects=77 recovered=69`)
are catalogue relinking. The only seek is an audio one (the boot restore, 223 ms post→apply, 114 ms of it the head
fetch). So there are no measured switch gaps to quote; the numbers in §1.4.2 are DERIVED from the code paths above,
and §4.3 defines the always-on lines that make the next log carry them.

#### 1.4.1 The 0.2.9 switch, as shipped (`src/apps/_old/Wavee/**`, reference only)

Aliases: **H** `SpotifyLive/Audio/FluentVideoMediaHost.cs`, **PC** `Backend/PlaybackController.cs`, **LC**
`SpotifyLive/LiveConnect.cs`, **PB** `App/PlaybackBridge.cs`, **MR** `SpotifyLive/SpotifyVideoManifestResolver.cs`,
**VR** `SpotifyLive/SpotifyVideoResolver.cs`, **M** `SpotifyLive/SpotifyVideoManifest.cs`, **PS**
`SpotifyLive/PopOutVideoSource.cs`, **VP** `Backend/Hydration/Projectors/VideoProjector.cs`.

```
 t0  toggle → PC.RefreshCurrentMediaKindAsync [_lock]                                            PC:600-635
     ├─ await MetaResolver(cur)              SERIAL RTT A — the AUDIO file lookup, unneeded for video   PC:2815-2829
     ├─ SwitchHost: audio.Pause(); audio.Stop()   ◄── the song is SILENT from here                 PC:523-524
     └─ StartVideoLoadDetached: emit, SchedulePreparedNext, spawn RunVideoLoadAsync, release lock  PC:2997-3014
 t1  PB.ResolveVideoSourceForPlaybackAsync  (SingleFlightMemo, 10-min TTL; cold on a fresh toggle) PB:842-873, 712
     ├─ Task.Run(File.Exists) — the local-override tier ("can block for seconds against a UNC share") CompositeVideoResolver.cs:43-46
     ├─ POST extended-metadata {TrackV4, VIDEO_ASSOCIATIONS}     SERIAL RTT 1                      MR:138-143
     ├─ POST TrackV4(counterpart)  (linked/alias tracks only)     SERIAL RTT 2                      MR:155-163
     │   — the store's VideoGidHex, which VP:312-314 says "IS the video manifest_id", is consulted LAST   MR:173-185
     ├─ GET /manifests/v9/json/sources/{id}/options/supports_drm  SERIAL RTT 3, uncached           VR:37-57
     └─ SpotifyVideoManifest.FromJson → DashSourceDescriptor; SpotifyLicenseRelay; publish         M:84-334; MR:83-116
 t2  LoadVideo → VideoLoadPump (latest-wins, epoch-fenced) → VideoSwitchPolicy.Plan                H:421-446
     — every audio→video toggle is HasPlayer:false ⇒ REBUILD: Stop() nulled _player on the way out  H:261-298
     ├─ TeardownAsync: dispose the queued predecessor (5 s cap over a 3 s native join)             H:1056-1114
     ├─ BuildLiveMediaPlayer (new MfMediaPlayer + ProtectedMediaBackend); PlayerChanged → UI       H:586-596, 661
     └─ await OpenAsync(source) ≤ 15 s  → engine: init GET (RTT 4) → CDM challenge → license POST (RTT 5)   H:690-691
        then `_ = PlayAsync` — NOT awaited ("the protected transport ack can take up to 5 s")     H:712-714
 t3  Tick @200 ms: wait Ready/Playing → SeekAsync(carry)  — the video shows 0:00 first            H:874-887
     re-assert Play ≤ 8× if the engine never left Ready                                            H:1007-1018
 t4  first frame — the comment's own figure: "DRM ~1.5 s, clear ~500 ms" AFTER t2                 H:955-956
     ceiling: VideoStartWatchdog 20 000 + 5 000 ms → ONE Fault → toast; audio fallback only if Connect-originated   H:168-169, 984; PC:3764-3776
```

What the 0.2.9 code says about itself: "the open completes in ~30 ms while the native session is still spinning up,
and a seek issued in that window is silently dropped" (H:708-711); "A bare Seek at that instant lands on a null
player and is dropped (that is why every audio→video switch used to restart at 0)" (PS:69-73); "This resolver is
deliberately STATELESS … every call re-walks the tiers and re-fetches" (MR:37-38); "the native PlayReady/CENC
session is a PROCESS-GLOBAL singleton with a session-less ABI, so a predecessor's teardown Stop lands on whatever
session holds the latch" (H:53-56); "when the fire-and-forget PlayAsync never takes … the transport sat paused at
0:00 forever ('it just buffers until I click play')" (H:974-978). The seek path itself was fire-and-forget on the
live session (`SeekPlayer` → `p.SeekAsync`, no teardown — H:307-326) with nothing masking the pre-seek position.

Two 0.2.9 facts the rework keeps: the **kind-99 association already carries the manifest id at hydration time**
(`VideoGidHex`, VP:273-284, 312-314 — 0.3 folds it into `Track.VideoCounterpart`, §1.1), so no metadata round trip
is needed to know WHAT to fetch; and the **next-track prefetch hook existed** (`SchedulePreparedNext` →
`PrefetchVideo` → `PrefetchVideoSource` + `VideoCdnWarm.WarmInit` GETting the init URLs, PC:3492-3497, PB:894-929)
but fetched only the manifest and warmed TLS — never the license, never the first segments, never for the CURRENT
track when its badge lit.

#### 1.4.2 The derived timeline of a song→video switch (0.3 host over today's engine, DRM, position P)

```
 t0   reducer: Effects.Stop → Audio.Stop()  (hard cut: MediaSwitch.ShouldStopOutgoingHost)        Playback.Host.cs:389
      [song audio is SILENT from here]
 t0   Video.Manifest.Resolve(manifestId)  — 1 spclient GET, synchronous on the pump              Playback.Video.cs:786-819
                                                                                   ≈ 150-400 ms   (0.2.9: up to 5 tiers, 0.5-1.5 s — §1.4.1)
 t1   Video.Load → pump → Plan = Switch → SwitchInPlaceAsync → live.OpenAsync(source)             :380-406, 451-477
 t1   MediaPlayer.OpenAsync: await old.DisposeAsync()                                             MediaPlayer.cs:333-338
        ProtectedMediaSession.DisposeAsync → Task.Run(Stop + Dispose) → FgPlayReadyStop
        → native loop exits within one 80 ms tick → feeder join → engine->Shutdown → CDM/session/D3D released
        → MFShutdown → _thread.Join(3000)                                          ≈ 100-500 ms   N:2845-2859; DPVP.cs:626-634
 t2   ProtectedMediaBackend.OpenAsync → new DesktopProtectedVideoPlayer → ConnectSignals (UI hop) → Start
        → new MTA thread → RunNative → FgPlayReadyRunEx                              ≈  20-50 ms   CoInit + MFStartup
 t3   GET video init  → GET audio init                          (2 serial RTT)      ≈ 200-400 ms   N:2148-2194
 t4   GET seg0..3 video, GET seg0..3 audio  (8 serial RTT, ~1-2 MB at 480p)         ≈ 800-2000 ms  N:2209-2241
      [these are segments AT POSITION 0 — the carried position P is not known to native]
 t5   D3D11 device + DXGI manager; CreateAndPrepareCdm (+ mfpmp.exe on first use)   ≈ 200-800 ms   N:2250-2262, 1403-1478
 t6   CreateSession + GenerateRequest → KeyMessage → relay POST (1 RTT) → Update
        → KeyStatusChanged; observed at 200 ms granularity                          ≈ 250-700 ms   N:2080-2091; Playback.Video.cs:1109-1125
 t7   protection manager, CencSource, ITA preflight, engine create, SetSource, Play
        → CANPLAY polled at 100 ms                                                  ≈ 200-600 ms   N:2281-2720
 t8   GetVideoSwapchainHandle polled at 60 ms → snapshot.handle                     ≈  60-200 ms   N:2746-2763
 t9   ProtectedMediaSession pump poll (250 ms) → binding.Bind → phase-11 Drain → DComp Commit
                                                                                    ≈   0-266 ms   ProtectedMediaSession.cs:93, 456-464
 t10  Wavee 200 ms tick sees Playing → AudioSignal.Started                          ≈   0-200 ms   Playback.Video.cs:663-667
      [FIRST FRAME visible — at 0:00, not at P]
 t11  IsSeekReady → Seek(P) → SeekEx slot → 80 ms tick → feeder rewinds to P/segLen, cancels the in-flight GET,
        GET seg(P) video + audio (serial) → CanSeekTo → SetCurrentTime → decode to P → ack poll → 250 ms pump poll
                                                                                    ≈ 500-1200 ms  :641-647; N:2380-2386, 2584-2591
      [the frame at P, audio at P]
                                                                       TOTAL  ≈ 2.7 s (best) … 7 s (typical cold, slow link)
```

That is the "like 5 seconds". Nothing in it is a single slow call; it is ten serial network round trips that
could be three parallel ones, a license round trip that could have happened at track start, a CDM and a PMP that
could have been alive, a start position that is applied after a start at zero, and four polling layers between
the frame and the UI. A video→video skip pays the same bill minus the CDM spin-up, because the runtime is released
at the end of every run (N:2845-2859).

#### 1.4.3 The derived timeline of a seek today (DRM)

```
 UI drag → element SeekPreviewScheduler (100 ms) → SeekMode.Keyframe → MediaPlayer.SeekAsync
   → ProtectedMediaSession.SeekAsync: PublishSeekIntent (Buffering, target, 6 s window)             :220-264
   → DesktopProtectedVideoPlayer.SeekAsync → FgPlayReadySeekEx(ms, 1) (latest-wins slot)            :389-399
   → managed ack poll 10 ms / 5 s (one Task chain per seek)                                         Waits.cs:35-56
 native 80 ms tick: waitForSeekBuffer → CanSeekTo?                                                  N:2801-2825
   yes (target inside the retained ~10 s / buffered-ahead window): SetCurrentTimeEx(APPROXIMATE)      ≈ 0-80 ms + decode
   no: feeder rewinds to targetMs/segLen, cancels the in-flight GET (10 ms poll), GET video seg, GET audio seg (serial),
       ParseSegment, then SetCurrentTime(Ex)                                                          ≈ 400-1200 ms
 CencMediaSource::Start → each stream to the last keyframe ≤ target, discontinuity (no decoder recreate)   C:668-713, 1011-1077
 MF Media Engine: APPROXIMATE presents the keyframe; exact decodes from the keyframe to the PTS; paused → FrameStep
 managed: seek "landed" when |native − target| ≤ 750 ms, OR ack + one 250 ms poll, OR 6 s expired       :490-500
 Wavee tick 200 ms → position
```

**The bugs in today's seek path, each with its cause** (numbered for §3.2's fixes):

| # | Symptom | Cause | Where |
|---|---|---|---|
| S1 | A song→video switch at 1:23 shows 0:00 first, then jumps | The native open has no start position; the carried position is applied after `IsSeekReady`, refetching the segment pair at P after fetching 4 + 4 at 0 | `Playback.Video.cs:641-647`; `ProtectedVideoTypes.cs` (no `StartPosition`) |
| S2 | Every seek/pause/play is late by 0-80 ms even when nothing needs fetching | Transport verbs are slots applied by the 80 ms keep-alive tick | N:519-600, 2795-2836 |
| S3 | A far seek pays two serial GETs, and the cancel of the in-flight one is polled at 10 ms | Video and audio segments for the seek target are fetched one after the other; the feeder is one thread | N:2380-2386, 2584-2591, 1641-1661 |
| S4 | A backward seek of more than ~6-10 s refetches | `kRetainBehind = 300` SAMPLES (not time): ≈ 10 s of 30-fps video, ≈ 6.4 s of AAC (the smaller wins) | C:734 |
| S5 | The scrubber shows the target while the picture is up to a segment away, then "lands" late | APPROXIMATE snaps to the keyframe ≤ target (segment start when segments carry one IDR — §9 Q5); `SeekReachedToleranceMs = 750` never matches, so "landed" waits for the ack + one 250 ms poll; a dropped ack holds Buffering for 6 s | `ProtectedMediaSession.cs:80-81, 490-500` |
| S6 | A 3-second drag leaves ~30 thread-pool `Task.Delay(10)` chains alive | Every `SeekAsync` awaits its own 5 s ack poll; supersession does not cancel the previous waiter | `Waits.cs:35-56` |
| S7 | Position after a seek stair-steps | The native position is read by the 250 ms pump poll and relayed by the 200 ms Wavee tick; no timestamped sample + extrapolation as on the clear path | `ProtectedMediaSession.cs:470-503` vs `MfMediaSession.cs:517-524` |
| S8 | The scrub preview never reaches the video host from the reducer | `Video.Seek(ms, accurate = true)` is the only entry and no effect passes `accurate: false`; the element's `SeekMode.Keyframe` is a UI-side path | `Playback.Video.cs:277-288`; `Playback.Host.cs:392` |
| S9 | A seek right after open is silently dropped | The seek slot is applied only once the feeder has buffered the target; before the burst is parsed nothing can be buffered | N:522-524, `Playback.Video.cs:637-640` |
| S10 | A paused seek shows the old frame until play | Handled natively by `FrameStep` only on the paused-seek path — correct, but the managed side re-asserts Play up to 8 times if the state reads Ready, which can un-pause a deliberate pause after a seek | N:541-548; `Playback.Video.cs:669-680` |

### 1.5 How the good ones do it — the reference engines on disk

Cloned for this plan: **androidx Media3** `C:\WAVEE\media3` (what YouTube Music runs on Android; prefixes below:
`EXO/` = `libraries/exoplayer/src/main/java/androidx/media3/exoplayer/`, `DASH/` = `…/exoplayer_dash/…/dash/`),
**Chromium `media/`** `C:\WAVEE\chromium-media\media` (sparse, `67aa03ec`), **mpv** `C:\WAVEE\mpv` (`10dee0a0`).
Microsoft, W3C and PlayReady documentation is cited by URL in §8. For each: what it does, and the thing we do not.

#### 1.5.1 Media3 / ExoPlayer

| Mechanism | Where | What we do not do |
|---|---|---|
| **One renderer set for the whole session.** Playlist items are `MediaPeriod`s swapped UNDER long-lived renderers: `RendererHolder.replaceStreamsOrDisableRendererForTransition` calls `renderer.replaceStream(...)` when the stream is not final; `disable()` keeps the codec, only `reset()` releases it ("it is recommended to hold onto resources even when entering STATE_DISABLED") | `EXO/RendererHolder.java:776-830`; `EXO/Renderer.java:70-76`; `EXO/mediacodec/MediaCodecRenderer.java:855-873` (`onDisabled` → `flushOrReleaseCodec`; `onReset` → `releaseCodec`) | We release the engine, the CDM, the D3D device and MF itself on every source (N:2845-2859) |
| **Codec reuse taxonomy** on a format change: `REUSE_RESULT_YES_WITHOUT_RECONFIGURATION / WITH_RECONFIGURATION / WITH_FLUSH / NO`, decided by MIME, resolution (adaptive), init data, DRM session; drain-then-reinit so the old item's tail is never cut | `EXO/DecoderReuseEvaluation.java:52-64`; `EXO/mediacodec/MediaCodecInfo.java:466-545`; `MediaCodecRenderer.java:1800-1905, 2235-2243` | Our source switch is a full topology rebuild inside `SetSource`; the MF H.264 MFT handles in-band SPS/PPS changes (that is how the native ABR switch already works, N:2388-2440) |
| **Pre-warming a secondary video renderer** for item N+1 while N plays | `EXO/ExoPlayerImplInternal.java:2863-2891`; `EXO/RendererHolder.java:77-88` | Nothing is warm for the next item (`PrepareAsync` would hit the singleton's BUSY latch) |
| **Seek = flush + decode-only**: `seekToPeriodPosition` → `mediaPeriod.seekToUs` → `resetRendererPosition`; `MediaCodecRenderer.onPositionReset` → `flushOrReinitializeCodec` (flush); output before `lastResetPositionUs` is decode-only (`isDecodeOnlyOutputBuffer`), input-side dropping of non-reference frames; seek INSIDE the buffer first (`SampleQueue.seekTo` before any loader reset) | `EXO/ExoPlayerImplInternal.java:1832-1957`; `MediaCodecRenderer.java:809-838, 2308-2318`; `EXO/video/MediaCodecVideoRenderer.java:1830-1885`; `EXO/source/ProgressiveMediaPeriod.java:583-626, 1136-1165`; `EXO/source/SampleQueue.java:533-560` | Our native seek already flushes not recreates (C:668-713) — but it is gated on the 80 ms tick and a serial fetch, and the managed side has no notion of "inside the buffer" |
| **`SeekParameters`**: `EXACT`, `CLOSEST_SYNC`, `PREVIOUS_SYNC`, `NEXT_SYNC` resolved against real sync points (`resolveSeekPositionUs`); DASH answers from segment start times via the VIDEO stream | `EXO/SeekParameters.java:43-56, 93-115`; `DASH/DefaultDashChunkSource.java:297-318`; `DASH/DashMediaPeriod.java:405-412` | Our two modes are MF's opaque NORMAL/APPROXIMATE with no knowledge of where the sync points are |
| **Joining**: after a seek or surface change the renderer "joins" for `allowedJoiningTimeMs` (late frames skipped, not dropped; `isReady` true until the deadline) instead of reporting buffering; the first frame is force-rendered (`shouldForceRenderOutputBuffer`, `firstFrameState`) | `EXO/video/VideoFrameReleaseControl.java:326-359, 447-455, 531-556`; `MediaCodecVideoRenderer.java:1113-1126, 2285-2287` | We publish `Buffering` for the whole seek and hold the position for up to 6 s |
| **DASH index fetched once**: init + `sidx` merged into ONE request (`attemptMerge`), then every seek is one `Range:` GET found by binary search over `ChunkIndex.timesUs` | `DASH/DefaultDashChunkSource.java:441-465, 726-768`; `EXT/ChunkIndex.java:68-70`; `DASH/DashWrappingSegmentIndex.java:28-92` | Spotify's manifest is template-addressed (segment i = start + i·segLen, no `sidx`) — the index is ARITHMETIC, which is better than `sidx`; what we lack is the keyframe table inside segments |
| **Load control with hysteresis**: `DEFAULT_BUFFER_FOR_PLAYBACK_MS = 1000`, `…_AFTER_REBUFFER_MS = 2000`, min/max 50 s, `backBufferDurationMs` default 0 with `retainBackBufferFromKeyframe`; a `PRELOAD` player is byte-target-only | `EXO/DefaultLoadControl.java:64-188, 766-830` | Our start gate is "4 segments fetched" (16 s of media) regardless of throughput; retention is 300 samples |
| **DRM pre-acquired and kept alive**: `preacquireSession` gets the session "ready in the background … from any thread"; `DEFAULT_SESSION_KEEPALIVE_MS = 5 min` after the last renderer releases it; sessions shared by scheme data; PSSH comes from `parseContentProtection` at MANIFEST time (`Format.drmInitData` before any media byte); a key change is `MediaCrypto.setMediaDrmSession` after a flush, not a codec re-init | `EXO/drm/DefaultDrmSessionManager.java:302, 442-452, 469-527, 877-917`; `EXO/drm/DrmSessionManager.java:100-136`; `DASH/manifest/DashManifestParser.java:645-720, 965-974`; `MediaCodecRenderer.java:297` | Our license is a TEMPORARY session created inside the open and released with it; the PSSH is in the manifest (`encryption_data`) and we parse it, then wait for the CDM to ask |
| **The audio sink is the clock and survives item boundaries**: `DefaultAudioSink.configure` stages a `pendingConfiguration`; `canReuseAudioOutput` keeps the same `AudioTrack`; `handleDiscontinuity` re-anchors `startMediaTimeUs`; `DefaultMediaClock` shadows it with a standalone clock for gaps | `EXO/audio/DefaultAudioSink.java:739-839, 936-975, 1033-1062`; `EXO/DefaultMediaClock.java:94-118, 164-207`; `EXO/audio/AudioTrackPositionTracker.java:237-300` | Our two audio paths (WASAPI graph for songs, the MF SAR for video) are different devices with different clocks — §3.1.4 |
| **Frames scheduled to vsync**: release time snapped to the sampled vsync minus 80 %, `MAX_ALLOWED_ADJUSTMENT_NS = 20 ms` | `EXO/video/VideoFrameReleaseHelper.java:220-267, 405-560` | MF does this inside the engine for the swapchain path (no per-frame work of ours); irrelevant to fix |
| **Surface swap without codec reset**: `MediaCodec.setOutputSurface`; a `PlaceholderSurface` keeps a secure decoder decoding with no view attached | `MediaCodecVideoRenderer.java:1318-1375, 2495-2522`; `EXO/video/PlaceholderSurface.java:42-122` | The DComp handle re-binds to a new registry token on a placement move without an open (`Pump` binds every pump, §1.2) — already ours |
| **Real next-item prefetch**: `DefaultPreloadManager` prepares the source, selects tracks and buffers item N+1 with the player's own capabilities/load control and hands the prepared period over on `createPeriod`; the preload shares the DRM manager, so the license is `OPENED_WITH_KEYS` on hand-off | `EXO/source/preload/DefaultPreloadManager.java:457-466, 631-661, 688-731`; `PreloadMediaSource.java:307-410, 535-621` | The engine's `IPreparableBackend` seam exists (`QueuePreparation.cs`) and the protected backend implements it wrongly (§1.2) |

#### 1.5.2 Chromium `media/` and mpv

| Mechanism | Chromium | mpv | What we do not do |
|---|---|---|---|
| **Flush, never recreate** | `DecoderStream::Reset` → `decoder_->Reset()`; the `Decoder` object survives a seek; recreation only on `kConfigChanged` (EOS-flush, then `PrependDecoder` gives the SAME instance first dibs at the new config) (`filters/decoder_stream.cc:236-292, 878-984, 1032-1075`) | `mp_filter_reset` → `avcodec_flush_buffers` (`video/decode/vd_lavc.c:1470-1478, 899-909`); `reinit_decoder` only on a segment CODEC change (`filters/f_decoder_wrapper.c:1138-1148`) | Our `CencMediaSource` flushes (C:668-713) but the OPEN path recreates everything |
| **Strict seek ordering** | abort demuxer reads → `Renderer::Flush` → `Demuxer::Seek` → `StartPlayingFrom` → re-apply rate (`base/pipeline_impl.cc:449-488, 1132-1160`) | `demux_seek` (blocked) → clear AO → `reset_playback_state` → unblock (`player/playloop.c:307-469`) | Ours is "write a slot, wait for a tick, wait for the feeder" |
| **Previous keyframe, decode-and-drop** | `SourceBufferRange::Seek` → `GetFirstKeyframeAtOrBefore` (`filters/source_buffer_range.cc:174-185`); `SkipPrepareUntil(start)` skips the expensive output prep for pre-target frames (`decoder_stream.cc:364-368, 731-736`) | `--hr-seek` with `hr_seek_demuxer_offset`, `VDCTRL_SET_FRAMEDROP` + `hrseek_pts` skipping, keeping the last dropped frame (`playloop.c:351-450`; `player/video.c:475-568`) | MF NORMAL seek does this inside the engine; APPROXIMATE lands on the keyframe — the difference is who knows where the keyframes are |
| **Cached seeks bypass I/O** | SourceBuffer ranges with `CanSeekTo` (2× inter-buffer fudge room), memory-bounded (`source_buffer_range.cc:187-195, 715-720`) | keyframe-indexed `demux_cached_range`s, 150 MiB forward / 50 MiB back, `find_cache_seek_range` → re-point `reader_head`, no low-level seek (`demux/demux.c:107-144, 3885-4065`) | Our retained window is 300 samples (S4) |
| **Buffering hysteresis; paint the first frame immediately, while paused** | `min_buffered_frames_` 3 → grows to 2× after underflow, RESET on seek "so seek time is not penalized"; `PaintFirstFrame_Locked` → `sink_->PaintSingleFrame` before HAVE_ENOUGH, 250 ms fallback timer (`renderers/video_renderer_impl.cc:99-147, 584-704, 1097-1114`) | "always render when paused" (`video/out/vo.c:983`) | We gate the first frame behind CANPLAY + a swapchain-handle poll + two managed polls |
| **Audio is the only clock** | `AudioClock::WroteAudio` from frames written minus device delay, advanced with silence while paused (`filters/audio_clock.cc:28-65`; `renderers/audio_renderer_impl.cc:1291-1500`) | `playing_audio_pts = written − speed·ao_delay`; `adjust_sync` nudges 10 % (`player/audio.c:617-630`; `player/video.c:350-376`) | MF's presentation clock does this for us; what we lack is a timestamped position sample on the protected path (S7) |
| **Zero-copy hardware decode** | decoder writes into a `Texture2D` ARRAY (`D3D11_BIND_DECODER | SHADER_RESOURCE`, `SHARED_NTHANDLE`), `pic_buffers_required + 1` slices, each wrapped as a shared image for the compositor; back-pressure is the picture-buffer pool released on the compositor `SyncToken`; `Reset` keeps the array (`gpu/windows/d3d_decoder_configurator.cc:165-224`; `d3d_video_decoder.cc:733-752, 822-851, 909-987`) | `ra_d3d11_wrap_tex_video` wraps the decoder texture-array slice with an SRV over `Texture2DArray.FirstArraySlice` (`video/out/d3d11/hwdec_d3d11va.c:246-287`; `ra_d3d11.c:632-668`) | Both are CLEAR-content paths (Chromium's protected path is `MediaFoundationRenderer` with a DComp surface — the same shape as ours); §3.3 |
| **Decryption waits, never tears down** | `DecryptingDemuxerStream` returns `kNoKey` → `kWaitingForKey` + `WaitingReason::kNoDecryptionKey`, resumed by `CdmContext::Event::kHasAdditionalUsableKey`; the pending buffer is retained (`filters/decrypting_demuxer_stream.cc:304-396`) | — | Our CDM path polls `g_cdmUsable` at 200 ms and treats a slow key as a 30 s timeout |
| **Config changes are data** | MSE tags buffers with config ids; `GetNextBuffer` returns `kConfigChange`; the audio path resamples into fixed sink parameters instead of restarting the device (`filters/source_buffer_stream.cc:1583-1587, 1816-1877`; `audio_renderer_impl.cc:1558-1576`) | a segment codec change only reinits the decoder, never the VO (`player/video.c:1207-1218`) | — |
| **Display-synced pacing** | `VideoRendererAlgorithm` cadence + drift budget `[16.7 ms, 125 ms]` (`filters/video_renderer_algorithm.cc:49-160, 566-568`) | `num_vsyncs` per frame from the measured vsync interval with an error accumulator; audio resampled to absorb drift (`player/video.c:805-930`; `vo.c:478-530, 918-1010`) | MF-internal for the swapchain path; no change |

#### 1.5.3 Media Foundation, PlayReady, MSE, YouTube — the documented facts that bound the design

| Fact | Source (§8) |
|---|---|
| The Media Engine has three modes: frame-server (default: "delivers uncompressed video frames to the application … the Media Engine renders the audio"), rendering (window or DComp visual), audio-only; `MF_MEDIA_ENGINE_DXGI_MANAGER` "enables the Media Engine to use hardware acceleration for video decoding and video processing" | `IMFMediaEngineClassFactory::CreateInstance`, `MF_MEDIA_ENGINE_DXGI_MANAGER` |
| Windowless swap-chain mode: "the Media Engine creates a windowless swap chain and presents video frames to the swap chain … call GetVideoSwapchainHandle … associate the handle with a Microsoft DirectComposition visual"; `UpdateVideoStream` repositions / repaints in rendering mode and "has no effect" in frame-server mode | `EnableWindowlessSwapchainMode`, `UpdateVideoStream` |
| Frame-server for protected content: "For protected content, call the IMFMediaEngineProtectedContent::TransferVideoFrame method instead"; `ShareResources` "Enables the Media Engine to access protected content while in frame-server mode … queries this pointer for the ID3D11VideoContext interface" — the destination must be a protected surface, and a protected surface can only be composited, never sampled into an unprotected UI back buffer | `IMFMediaEngine::TransferVideoFrame`, `IMFMediaEngineProtectedContent::ShareResources` |
| `MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT` + `MF_MEDIA_ENGINE_CONTENT_PROTECTION_MANAGER` "required if protected content is to be played"; a custom source arrives through `MF_MEDIA_ENGINE_EXTENSION` → `IMFMediaEngineExtension::BeginCreateObject`; **there is no `SetSourceFromMediaSource`** (the `IMFMediaEngineEx` method list has only `SetSourceFromByteStream`) | `MF_MEDIA_ENGINE_PROTECTION_FLAGS`, `IMFMediaEngineExtension`, `IMFMediaEngineEx` |
| The Media Engine's MSE (`IMFMediaSourceExtension`, `IMFSourceBuffer::Append / AppendByteStream / Remove / SetTimeStampOffset`) is "expected to only be called by web browsers implementing MSE", created by `IMFMediaEngineClassFactoryEx::CreateMediaSourceExtension` (Windows 8.1, desktop only); how it attaches to an engine is not documented; `IMFSourceBufferAppendMode` does not exist | `IMFMediaSourceExtension`, `IMFSourceBuffer`, `CreateMediaSourceExtension` |
| `IMFDXGIDeviceManager::ResetDevice`: create the device with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` and "use multi-thread protection on the device context to prevent deadlock" — what our engine does | `IMFDXGIDeviceManager::ResetDevice` |
| D3D11↔D3D12 sharing: a shared fence is opened in D3D11 with `ID3D11Device5::OpenSharedFence`, a shared resource with `ID3D11Device::OpenSharedResource1` (`D3D11_RESOURCE_MISC_SHARED_NTHANDLE`); `D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS` "can compromise resource fences … and prevent any compression"; D3D11On12 "has not been optimized for performance … significant memory overhead". MF on a D3D12 device exists on Windows 11 (`MF_D3D12_SYNCHRONIZATION_OBJECT`, `MFCreateD3D12SynchronizationObject`) with a GDK-only end-to-end sample and no DRM guidance | `ID3D12Device::CreateSharedHandle`, `Direct3D 11 on 12`, `D3D12_RESOURCE_FLAGS`, `d3d12-mf-guids`; microsoft/media-foundation issue #82 |
| Seeking: `IMFSourceReader::SetCurrentPosition` "does not guarantee exact seeking … typically seeks to the nearest key frame before the desired position … the application should call ReadSample and advance"; `IMFMediaEngine::SetCurrentTime` "corresponds to setting the currentTime attribute … sends SEEKING … SEEKED"; `MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE` is documented in one sentence; `FrameStep` completes with `FRAMESTEPCOMPLETED` "and enters the paused state" | `SetCurrentPosition`, `SetCurrentTime`, `MF_MEDIA_ENGINE_SEEK_MODE`, `FrameStep` |
| Readiness: `HAVE_NOTHING … HAVE_ENOUGH_DATA` mirror `HTMLMediaElement.readyState`; events `LOADEDMETADATA`, `LOADEDDATA` ("enough data to render some content (for example, a video frame)"), `CANPLAY`, `PLAYING`, **`FIRSTFRAMEREADY` (1009) "The first frame of the media source is ready to render"**, `FORMATCHANGE` (param1 0 = video) | `MF_MEDIA_ENGINE_READY`, `MF_MEDIA_ENGINE_EVENT` |
| The Media Engine owns its own audio renderer ("the application is not responsible for audio rendering"); `SetAudioStreamCategory` / `SetAudioEndpointRole` apply "for the next call to SetSource or Load"; audio latency is not documented; no device-id selection | `CreateInstance`, `IMFMediaEngineEx::SetAudioEndpointRole` |
| PlayReady: "**Proactive** license acquisition — The client application explicitly initiates a license request before playback begins … After the license is received, playback can start at any time"; "**Reactive** … If it does not find any usable license, it automatically … acquire[s] the license before resuming the playback"; a v4.2 header carries multiple KIDs so "a single license acquisition [can] acquire all licenses for all streams"; WinRT `PlayReadyLicenseSession.CreateLicenseIterable(contentHeader, fullyEvaluated)` + `PlayReadyLicense.UsableForPlay` / `ExpirationDate` / `InMemoryOnly` answer "do I hold a usable license" locally; "You must maintain the PlayReadyLicenseSession instance until playback has completed" | PlayReady `license-acquisition`, `playready-header-specification`; UWP `playready-client-sdk`; `PlayReadyLicenseIterable`, `PlayReadyLicense` |
| MSE / HTML: a quality/rendition switch is an append of a new init segment + media segments into the SAME `SourceBuffer` (`changeType` for codec changes), `timestampOffset` splices clips onto one timeline, `buffered` is what a scrub bar reads; `fastSeek` "Seeks to near the given time as fast as possible, trading precision for speed … the adjusted new playback position must also be before the current playback position" when seeking backwards | W3C media-source-2 §3.5.7 (not re-verified verbatim), MDN `SourceBuffer`, WHATWG HTML `fastSeek` |
| YouTube: `adaptiveFormats` are "dedicated audio-only and video-only streams, which are overlaid at run-time by the player", each with `initRange` + `indexRange` (init + `sidx` byte ranges); un-ranged requests 403. DASH-IF: "The index segment informs the client of all the media segments that exist … and their byte ranges" — init (cached) + sidx (cached) + one ranged GET per seek is what the profile SPECIFIES; that YouTube does exactly one is inferred. YouTube Music: "with a simple tap, they can instantly start watching the music video or flip back to the audio at the same point in the track … perfectly time-matched over five million official music videos to their respective audio tracks" — the same-player adaptation-set swap is INFERRED, not documented | tyrrrz.me, ytjs.dev `FormatInitializationMetadata`, DASH-IF Guidelines-TimingModel §22, blog.youtube 2018-11 |
| Spotify video manifest (public sources thin): `/manifests/v{7,9}/json/sources/{id}/options/supports_drm`, `contents[]` with `segment_length`, `end_time_millis`, `profiles[]` (`id`, `file_type`, `video_codec`, `video_width/height`, `video_bitrate`, `audio_codec`, `audio_bitrate`), `base_urls`, `initialization_template`, `segment_template`; "There is no m3u8 or MPD for this content"; the only publicly attested license URL is the Widevine one. The PlayReady shape — `encryption_infos[].key_system == "playready"`, `encryption_data` (base64 PSSH), `license_server_endpoint`, `encryption_indices` per profile — is attested by our own parser and its 0.2.9 tests, not by a public document | forum.videohelp.com 407115 / 416309; `Playback.Video.cs:859-913`; `_old/Wavee.Tests/SpotifyVideoManifestTests.cs` |

**The two YouTube facts that decide the shape.** (1) YouTube Music's song↔video switch is an *adaptation-set*
swap inside ONE presentation — the audio rendition is the same bytes in both states ("perfectly time-matched"),
which is why the audio never hiccups. A Spotify music video is a DIFFERENT content id with its OWN soundtrack
under its OWN key (the manifest's audio profile, M:47-53; `AudioInitUrl` on the descriptor); the closest achievable
shape is a warm, pre-licensed, pre-buffered session cut in at a sample boundary (§3.1.4). (2) YouTube's seek is
one ranged GET because the index is fetched once; Spotify's addressing is arithmetic (segment i = start + i·segLen,
`SegmentStride`), so our seek is ALSO one GET per stream with no index fetch at all — better than `sidx`; the
missing piece is the keyframe table INSIDE a segment, which the demuxer already computes per fetched segment
(C:518-535 marks keyframes) and throws away.

---

## 2. The engine — four options, one recommendation

| | (a) Rework the native helper into a RUNTIME + license cache + sessions on the warm `IMFMediaEngine` | (b) The Media Engine's own MSE (`IMFMediaSourceExtension` + `IMFSourceBuffer`) with the CDM — the YouTube-web shape | (c) `IMFSourceReader` / D3D12 video decode + our own A/V pipeline (the mpv shape) | (d) WinRT `MediaPlayer` + `MediaProtectionManager` |
|---|---|---|---|---|
| Plays Spotify (PlayReady) | Yes — it is the proven path (`media-pipeline.md` §8.4: "on-box proven"); only the LIFETIME changes | Undocumented for Win32 ("expected to only be called by web browsers"; attachment to an engine not documented; §1.5.3) — a spike, not a plan | **No.** The Source Reader has no PMP; D3D12 video decode has no CDM. Clear local files only | Disproven: `MF_E_TOPOLOGY_VERIFICATION_FAILED 0xC00D715B` (`video-drm-layer-design.md` §12 Q1) |
| Warm switch cost | `SetSource` on a live engine: topology + decoder init ≈ 100-250 ms with data in memory; CDM, PMP, D3D device, engine, swapchain all survive | Best in theory (append a new init segment, no topology rebuild) | Best in theory for clear content | — |
| Seek | `CencMediaSource::Start` already repositions to the keyframe ≤ target without recreating anything (C:668-713); needs the tick/fetch/retention fixes of §3.2 | The engine seeks inside `buffered` ranges | We own it entirely | — |
| Rendering | The DComp child + hole-punch already built and gated (`DCompVideoPresenter`, `VideoSurfaceRegistry`, `DrawVideoCmd`) | Same | Frame-server-style into our textures — but not for protected content (§3.3) | — |
| Risk | Moderate: ~1,200 lines of C++ restructured around the code that already works; ABI change in one managed file | High: two undocumented seams (MSE attach, MSE + `IMFContentDecryptionModule`) | Very high and useless for the feature | Known-dead |
| Testable | Session/runtime state machine via the managed fake; native via the Axinom single-key vector already baked in (N:2988-2995) + a clear fMP4 vector (§4) | — | — | — |

**Recommendation: (a).** Every second in §1.4.2 is a LIFETIME or SCHEDULING defect in code that otherwise plays the
content correctly; none needs a different MF API. (b) would remove the last ~150 ms of a warm switch (the topology
rebuild inside `SetSource`) at the cost of two seams Microsoft documents as browser-only; it stays an open question
(§7 Q7) with a measurement trigger: if the §4.3 gate shows `open.ok … attachMs` above 300 ms on a warm switch after (a)
lands, (b) is the next step, on the same runtime. (c) is the right shape for clear local files and the wrong one for
the feature. (d) is dead.

---

## 3. The design

### 3.1 The switch — one runtime, sessions, and a switch that is a `SetSource`

#### 3.1.1 The native ABI (`ops/tools/playready-native/FgPlayReady.h` — NEW, replaces the session-less exports)

The whole point of the ABI change is a HANDLE on every call, so a predecessor's `Stop` can never land on a successor
(`Playback.Video.cs:30-35`, the wedge 0.2.9 documented). The old exports (`FgPlayReadyRunEx`, `FgPlayReadyStop`, the
`*Seq` slots) are deleted, not shimmed — no legacy path.

```cpp
// FgPlayReady.h — the C ABI between FluentGpu.WindowsApi and FluentGpu.PlayReady.Native.dll. Every call is
// non-blocking unless stated. Handles are opaque uint64_t; 0 is never a valid handle.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t FgPrRuntime;    // one per process: MF, D3D11 video device + DXGI manager, ONE IMFMediaEngine, ONE CDM + PMP host
typedef uint64_t FgPrLicense;    // one per KID: an open CDM key session (TEMPORARY), cached until Expired/Released
typedef uint64_t FgPrSession;    // one per source: a CencMediaSource + SegmentStore, attached to the runtime's engine by SetSource

// Log + state callback: EVERY native line goes here (the managed side writes it into the app's one log, tag [video.native]).
// `event` is one of the FgPrEvent values; `a`/`b` carry the event's numbers (position, size, hr…). Never called per frame.
typedef void (__stdcall *FgPrEventCallback)(void* ctx, uint64_t session, int32_t event, int64_t a, int64_t b, const wchar_t* text);

// The license relay: the CDM's challenge goes up; the managed side POSTs it and calls `deliver` from ANY thread, later.
// Non-blocking on the CDM thread — the native side never waits for the relay (the old task.Wait(30 s) is gone).
typedef void    (__stdcall *FgPrLicenseDeliver)(void* deliverCtx, const uint8_t* license, int32_t licenseLen, int32_t hr);
typedef int32_t (__stdcall *FgPrLicenseCallback)(void* ctx, FgPrLicense license, const uint8_t* challenge, int32_t challengeLen,
                                                  const wchar_t* keyIdHex, FgPrLicenseDeliver deliver, void* deliverCtx);

enum FgPrEvent : int32_t {
    FgPrEvent_RuntimeReady = 1,     // a = ms spent in bring-up
    FgPrEvent_LicenseUsable = 10,   // session = license handle; a = ms since acquire; b = expires-in ms (0 = unknown)
    FgPrEvent_LicenseFailed = 11,   // a = hr
    FgPrEvent_LicenseExpired = 12,
    FgPrEvent_Bytes = 20,           // a = bytes downloaded (cumulative), b = ms transferring (cumulative) — at most 4 Hz
    FgPrEvent_Buffered = 21,        // a = forward buffered ms, b = backward retained ms
    FgPrEvent_Keyframes = 22,       // a = segment index parsed; keyframe table grew (poll FgPrGetKeyframes)
    FgPrEvent_Metadata = 30,        // a = duration ms, b = (width << 32) | height
    FgPrEvent_CanPlay = 31,
    FgPrEvent_FirstFrame = 32,      // MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY; a = presentation position ms
    FgPrEvent_Handle = 33,          // a = the DComp swapchain handle (re-raised on FORMATCHANGE/RESOURCELOST)
    FgPrEvent_Position = 34,        // a = position ms, b = QPC ticks of the sample — at most 4 Hz, never a pump trigger
    FgPrEvent_Seeking = 35,         // a = target ms
    FgPrEvent_Seeked = 36,          // a = landed ms, b = ms since the seek was posted
    FgPrEvent_Playing = 37,
    FgPrEvent_Paused = 38,
    FgPrEvent_Ended = 39,
    FgPrEvent_Error = 40,           // a = MF_MEDIA_ENGINE_ERR, b = hr
    FgPrEvent_Representation = 41,  // a = active representation index
};

typedef struct FgPrOpenDesc {
    uint32_t structSize;
    const wchar_t* initUrl; const wchar_t* segmentBaseUrl; const wchar_t* segmentPrefix; const wchar_t* segmentSuffix;
    int32_t startNumber; int32_t segmentCount; int32_t segmentStrideSeconds; int32_t segmentLengthMs;
    const wchar_t* audioInitUrl; const wchar_t* audioSegmentBaseUrl; const wchar_t* audioSegmentPrefix; const wchar_t* audioSegmentSuffix;
    const uint8_t* pssh; int32_t psshLen; const wchar_t* keyIdHex; const wchar_t* httpHeaders;
    int64_t startPositionMs;        // NEW: the feeder starts at floor(start / segmentLength); the source's first Start lands here
    int32_t startPaused;            // 1 = open paused at startPositionMs and raise FirstFrame (FrameStep) without playing
    int64_t retainBehindMs;         // NEW: time-based retention (default 30 000) — replaces kRetainBehind = 300 samples
    int64_t bufferAheadMs;          // NEW: forward target (default 60 000) — replaces kMaxSamplesAhead = 900
    int64_t storeBudgetBytes;       // NEW: SegmentStore cap for THIS session (default 32 MiB)
} FgPrOpenDesc;

typedef struct FgPrSnapshot {   // one atomic read; the same shape the managed VideoEngineSnapshot has (§1.2)
    uint32_t structSize; int32_t state; int32_t errorHr; uint64_t handle; int32_t width, height;
    int64_t positionMs; int64_t positionQpc; int64_t durationMs; int64_t bufferedAheadMs, retainedBehindMs;
    uint64_t bytesDownloaded, downloadElapsedMs; int32_t activeRepresentation; int32_t readyState; int32_t seeking;
} FgPrSnapshot;

// ── runtime ─────────────────────────────────────────────────────────────────────────────────────────────────────────
__declspec(dllexport) int32_t __stdcall FgPrRuntimeCreate(const wchar_t* storePath, FgPrEventCallback cb, void* ctx, FgPrRuntime* out);
__declspec(dllexport) void    __stdcall FgPrRuntimeDestroy(FgPrRuntime rt);      // joins the MTA thread (bounded 2 s) — the ONLY blocking export
// ── license cache (KID-keyed; the runtime owns the CDM; sessions stay open until Expired or Release) ────────────────
__declspec(dllexport) int32_t __stdcall FgPrLicenseAcquire(FgPrRuntime rt, const uint8_t* pssh, int32_t psshLen, const wchar_t* keyIdHex,
                                                          FgPrLicenseCallback relay, void* relayCtx, FgPrLicense* out);   // returns an EXISTING usable handle when the KID is cached
__declspec(dllexport) int32_t __stdcall FgPrLicenseState(FgPrRuntime rt, FgPrLicense lic);   // 0 pending, 1 usable, 2 expired, <0 failed hr
__declspec(dllexport) void    __stdcall FgPrLicenseRelease(FgPrRuntime rt, FgPrLicense lic);
// ── sessions ───────────────────────────────────────────────────────────────────────────────────────────────────────
__declspec(dllexport) int32_t __stdcall FgPrSessionCreate(FgPrRuntime rt, const FgPrOpenDesc* desc, FgPrSession* out);   // creates the store + feeder; NO engine call yet
__declspec(dllexport) int32_t __stdcall FgPrSessionPrefetch(FgPrRuntime rt, FgPrSession s, int64_t aroundMs, int32_t segments); // init + N segments around a position, both streams in parallel
__declspec(dllexport) int32_t __stdcall FgPrSessionAttach(FgPrRuntime rt, FgPrSession s, FgPrLicense lic);   // THE switch: SetSource on the warm engine; detaches whatever was attached
__declspec(dllexport) int32_t __stdcall FgPrSessionDetach(FgPrRuntime rt, FgPrSession s);                    // SetSource(null) + Pause; the session and its store survive
__declspec(dllexport) void    __stdcall FgPrSessionDestroy(FgPrRuntime rt, FgPrSession s);                   // frees the store; detaches first if attached
__declspec(dllexport) int32_t __stdcall FgPrSessionPlay(FgPrRuntime rt, FgPrSession s);     // applied on the engine thread IMMEDIATELY (a posted work item), no 80 ms tick
__declspec(dllexport) int32_t __stdcall FgPrSessionPause(FgPrRuntime rt, FgPrSession s);
__declspec(dllexport) int32_t __stdcall FgPrSessionSeek(FgPrRuntime rt, FgPrSession s, int64_t targetMs, int32_t mode, int64_t keyframeMs); // mode 0 exact / 1 keyframe; keyframeMs = the planner's answer (-1 = native decides)
__declspec(dllexport) int32_t __stdcall FgPrSessionSetVolume(FgPrRuntime rt, FgPrSession s, double volume);
__declspec(dllexport) int32_t __stdcall FgPrSessionSetRate(FgPrRuntime rt, FgPrSession s, double rate);
__declspec(dllexport) int32_t __stdcall FgPrSessionSelectRepresentation(FgPrRuntime rt, FgPrSession s, int32_t index, const wchar_t* initUrl,
                                                                        const wchar_t* base, const wchar_t* prefix, const wchar_t* suffix);
__declspec(dllexport) int32_t __stdcall FgPrSessionSnapshot(FgPrRuntime rt, FgPrSession s, FgPrSnapshot* out);
// The keyframe table: every sync sample the demuxer has SEEN (segment starts always; intra-segment IDRs as they are parsed).
// Fills `out` (ms, ascending) up to `cap`; returns the total count (a caller with a smaller buffer re-asks with a bigger one).
__declspec(dllexport) int32_t __stdcall FgPrSessionGetKeyframes(FgPrRuntime rt, FgPrSession s, int64_t* out, int32_t cap);
// Buffered ranges (ms pairs, ascending) — what a scrub bar reads and what the planner checks before any fetch.
__declspec(dllexport) int32_t __stdcall FgPrSessionGetBuffered(FgPrRuntime rt, FgPrSession s, int64_t* outPairs, int32_t capPairs);

#ifdef __cplusplus
}
#endif
```

What moves where inside `PlayReadyNative.cpp` (the file is split; nothing is rewritten that works):

| Today | Becomes | Lines it keeps |
|---|---|---|
| `FgPlayReadyRunEx` prologue: `CoInitializeEx` + `MFStartup` (N:2954-2967), `D3D11CreateDevice` + DXGI manager (N:2250-2259), `CreateAndPrepareCdm` (N:1403-1478), `MediaEngineProtectionManager` (N:1489-1508), engine create + `EnableWindowlessSwapchainMode` (N:2691-2708) | `PrRuntime.cpp` — done ONCE in `FgPrRuntimeCreate` on the runtime's own MTA thread; the engine's `MF_MEDIA_ENGINE_EXTENSION` becomes a scheme handler that resolves `cenc://fluentgpu/<sessionId>` to THAT session's `CencMediaSource` (N:1750-1763 already parametrised by url) | all of them, verbatim, moved |
| `DriveCdmLicenseProactive` (N:2067-2098) + `HandleCdmKeyMessage` (N:1018-1165) | `PrLicense.cpp` — `FgPrLicenseAcquire`: KID lookup in a small table (cap 8, LRU) → `CreateSession(TEMPORARY)` + `GenerateRequest("cenc", pssh)`; the relay is invoked from `KeyMessage` and RETURNS; `deliver` runs `Update` and `KeyStatusChanged` raises `LicenseUsable`; **no 200 ms poll, no 30 s wait** — a session that is still pending when `FgPrSessionAttach` is called is attached anyway and the engine's own `NeedKey` path (N:1557-1574, today ignored) waits for the key exactly as Chromium's `DecryptingDemuxerStream` does (§1.5.2) | the parsing + `Update` + key-status code |
| `BuildCencSource` + feeder + burst (N:2103-2318, 2380-2510, 2584-2591), `CencMediaSource.h` | `PrSession.cpp` + `CencMediaSource.h` + NEW `SegmentStore.h` — per session; the burst becomes `FgPrSessionPrefetch(aroundMs, n)` with video ∥ audio GETs (two in flight per stream, the shared `HttpClient` from N:1593-1599); `startPositionMs` selects the first segment; the keyframe table is kept (`ParseSegment` already marks sync samples, C:518-535) | the demuxer unchanged; the feeder loses its serial fetch and its 50 ms sleep (wakes on demand) |
| `ReconcileTransport` + the 80 ms keep-alive tick (N:519-600, 2788-2836) | Deleted. Transport verbs post work items to the runtime's MTA thread (`BlockingCollection<Action>` — the same shape as `VideoMediaEngine.WakeEngine`, `VideoMediaEngine.cs:163-197`); position is sampled with a QPC stamp on every engine event and at most 4 Hz otherwise; `OnVideoStreamTick`/`UpdateVideoStream(nullptr)` per tick (N:2797-2800) is deleted — windowless swap-chain mode auto-presents (§1.5.3) | — |
| The `g_desktopRunning` CAS latch + BUSY (N:2874) | Deleted with the singleton | — |
| `FG_CENC_*` env switches (§1.3) | Deleted; the proven choices (`MFWrapMediaType(MFMediaType_Protected)` + `MF_SD_PROTECTED`, TEMPORARY sessions) are the only code | — |

#### 3.1.2 Lifetimes

```
 process ────────────────────────────────────────────────────────────────────────────────────────────────────────►
   FgPrRuntime  ──create on first Prefetch/Attach──────────────────────────── idle 30 s after the last Detach ──destroy──►
                 MF │ D3D11+DXGI mgr │ IMFMediaEngine (windowless) │ CDM + PMP host │ MTA thread + work queue
                 (same warm-idle policy as MfMediaPlayer.WarmIdleDisposeMs — the memory argument at MfMediaPlayer.cs:53-63 holds here too)
   FgPrLicense[kid A] ──acquire at manifest time───────── usable ─────────────────────── expired / LRU evict ──►
   FgPrLicense[kid B]                          ──acquire (next track)── usable ─────────────────────────────────►
   FgPrSession[track 1] ─create+prefetch─ attach ── playing ──── detach ─┐ (store kept 30 s for "back to the song")
   FgPrSession[track 2]            ─create+prefetch (ending-soon) ───────┴─ attach ── playing ─── … ──►
```

Two sessions coexist (the playing one and the prefetched next); the engine has ONE attached at a time. A session's
`SegmentStore` is bytes-capped (32 MiB default; §3.5 allocation table) and time-windowed (30 s behind / 60 s ahead of
the playhead, `retainBehindMs` / `bufferAheadMs`) — mpv's `--demuxer-max-back-bytes` / `--demuxer-max-bytes` in
miniature (§1.5.2); prefetch for a NOT-attached session is exactly init + `segments` (2 by default: 8 s) around
`aroundMs` for both streams.

#### 3.1.3 The managed side (`C:\wavee\fluent-gpu\src\FluentGpu.WindowsApi\Media\PlayReady\`)

`DesktopProtectedVideoPlayer.cs` (743 lines: a thread per open, the BUSY loop, the 90 s watchdog, the ack polls) is
replaced by `ProtectedVideoRuntime.cs` (the process-wide runtime + license cache, ~300 lines) and
`ProtectedVideoSession.cs` (one per source, implements `IProtectedVideoPlayer`, ~350 lines). `ProtectedMediaSession.cs`
keeps its role (state mapping onto `MediaSignalSink` on the UI thread) and loses its polls.

```csharp
// ProtectedVideoRuntime.cs — WindowsApi. ONE per process; created on first use by ProtectedMediaBackend.
public sealed partial class ProtectedVideoRuntime : IDisposable
{
    private ulong _rt;                                   // FgPrRuntime
    private readonly Dictionary<string, LicenseEntry> _licenses = new(StringComparer.Ordinal);   // kid → entry (cap 8, LRU by LastUsedTicks)
    private readonly object _gate = new();
    private readonly Action<VideoNativeEvent> _events;   // the ONE sink: ProtectedMediaSession wakes its pump from here

    private struct LicenseEntry { public ulong Handle; public LicenseState State; public long AcquiredTicks, LastUsedTicks, ExpiresTicks; public Func<LicenseRequest, ValueTask<LicenseResponse>> Relay; }
    public enum LicenseState : byte { Pending, Usable, Expired, Failed }

    /// <summary>Start a license acquisition for <paramref name="kid"/> if none is usable or pending. Returns at once;
    /// LicenseUsable/LicenseFailed arrive on the event sink. Called at MANIFEST time (Prefetch) — Media3's
    /// preacquireSession, PlayReady's "proactive acquisition" (§1.5).</summary>
    public LicenseState EnsureLicense(ReadOnlySpan<byte> pssh, string kid, Func<LicenseRequest, ValueTask<LicenseResponse>> relay)
    {
        lock (_gate)
        {
            if (_licenses.TryGetValue(kid, out LicenseEntry e) && e.State is LicenseState.Usable or LicenseState.Pending)
            { e.LastUsedTicks = Stopwatch.GetTimestamp(); _licenses[kid] = e; return e.State; }
            EvictIfFull();
            ulong h;
            fixed (byte* p = pssh)
                Native.FgPrLicenseAcquire(_rt, p, pssh.Length, kid, &LicenseThunk, GCHandle.ToIntPtr(_self), &h);
            _licenses[kid] = new LicenseEntry { Handle = h, State = LicenseState.Pending, Relay = relay, AcquiredTicks = Stopwatch.GetTimestamp(), LastUsedTicks = Stopwatch.GetTimestamp() };
            Diag.Line($"[video.native] license.acquire kid={kid} cached=false");
            return LicenseState.Pending;
        }
    }

    // The relay thunk RETURNS immediately; the POST runs on the pool and `deliver`s from there. Nothing native waits.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LicenseThunk(nint ctx, ulong lic, byte* challenge, int len, char* kidHex, delegate* unmanaged[Stdcall]<nint, byte*, int, int, void> deliver, nint deliverCtx)
    {
        var self = (ProtectedVideoRuntime)GCHandle.FromIntPtr(ctx).Target!;
        var copy = new byte[len]; new ReadOnlySpan<byte>(challenge, len).CopyTo(copy);   // the ONE allocation per license
        string kid = new(kidHex);
        Func<LicenseRequest, ValueTask<LicenseResponse>>? relay;
        lock (self._gate) relay = self._licenses.TryGetValue(kid, out LicenseEntry e) ? e.Relay : null;
        if (relay is null) return unchecked((int)0x80004005);
        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Run(), (self, lic, kid, copy, relay, deliver, deliverCtx), preferLocal: false);
        return 0;
    }
    // … Run(): await relay(new LicenseRequest(copy, kid)) → deliver(deliverCtx, bytes, n, 0) or deliver(…, null, 0, hr)
}
```

```csharp
// ProtectedVideoSession.cs — one per source. Implements IProtectedVideoPlayer for ProtectedMediaSession; no thread of its own.
public sealed partial class ProtectedVideoSession : IProtectedVideoPlayer
{
    private readonly ProtectedVideoRuntime _rt;
    private ulong _s;                                    // FgPrSession
    private ulong _lic;                                  // FgPrLicense (a cached handle, or the one EnsureLicense started)
    private readonly ProtectedVideoRequest _req;
    private VideoEngineSnapshot _snap;                   // the SAME POD the clear path publishes (VideoEngineSeam.cs) — one seam shape for both backends
    private readonly VideoSnapshotBuffer _buffer = new();

    public static ProtectedVideoSession Create(ProtectedVideoRuntime rt, ProtectedVideoRequest req)
    {
        var s = new ProtectedVideoSession(rt, req);
        var desc = new FgPrOpenDesc { /* …from req… */ startPositionMs = (long)req.StartPosition.TotalMilliseconds, startPaused = 1,
                                      retainBehindMs = 30_000, bufferAheadMs = 60_000, storeBudgetBytes = 32L << 20 };
        Native.FgPrSessionCreate(rt.Handle, &desc, &s._s);
        s._lic = rt.LicenseHandleFor(req.DefaultKid!);   // EnsureLicense ran at manifest time; this is a lookup, never a POST
        return s;
    }

    /// <summary>The prefetch half of IPreparableBackend: init + N segments around the start position, both streams in
    /// parallel, and the license already in flight. No engine call, no thread. Returns when the store reports them
    /// (event FgPrEvent_Buffered) or the token cancels.</summary>
    public ValueTask PrefetchAsync(int segments, CancellationToken ct) { Native.FgPrSessionPrefetch(_rt.Handle, _s, (long)_req.StartPosition.TotalMilliseconds, segments); return _rt.WaitBufferedAsync(_s, ct); }

    /// <summary>THE SWITCH. SetSource on the warm engine with this session's source at its start position. The engine
    /// raises Metadata → CanPlay → FirstFrame (FrameStep while paused); the runtime forwards each as an event and the
    /// ProtectedMediaSession pump reads ONE snapshot per turn. Non-blocking.</summary>
    public void Start(ProtectedVideoRequest _) => Native.FgPrSessionAttach(_rt.Handle, _s, _lic);
    public ValueTask PlayAsync()  { Native.FgPrSessionPlay(_rt.Handle, _s);  return ValueTask.CompletedTask; }   // no ack poll: Playing arrives as an event
    public ValueTask PauseAsync() { Native.FgPrSessionPause(_rt.Handle, _s); return ValueTask.CompletedTask; }
    public ValueTask SeekAsync(long ms, SeekMode mode) => SeekAsync(ms, mode, keyframeMs: -1);
    public ValueTask SeekAsync(long ms, SeekMode mode, long keyframeMs) { Native.FgPrSessionSeek(_rt.Handle, _s, ms, mode == SeekMode.Keyframe ? 1 : 0, keyframeMs); return ValueTask.CompletedTask; }
    public int GetKeyframes(Span<long> into) { fixed (long* p = into) return Native.FgPrSessionGetKeyframes(_rt.Handle, _s, p, into.Length); }
    public int GetBuffered(Span<long> pairs) { fixed (long* p = pairs) return Native.FgPrSessionGetBuffered(_rt.Handle, _s, p, pairs.Length / 2); }
    public void Pump(in VideoBinding binding)   // called by ProtectedMediaSession.PumpVideo — value-gated, never per frame
    {
        FgPrSnapshot n; Native.FgPrSessionSnapshot(_rt.Handle, _s, &n);
        _snap = Map(in n);                       // POD → POD, no allocation
        _buffer.Publish(in _snap);
        if (n.handle != 0) binding.Bind((nuint)n.handle);
    }
    public void Stop() => Native.FgPrSessionDetach(_rt.Handle, _s);           // the store survives for 30 s (back-to-song is a re-attach)
    public void Dispose() => Native.FgPrSessionDestroy(_rt.Handle, _s);
}
```

`ProtectedMediaBackend` keeps its `IMediaBackend` + `IPreparableBackend` faces and gets the semantics the seam
always promised (`QueuePreparation.cs:20-31`: "A video/DRM backend spins up its MF session + first-frame-readies"):

```csharp
public async ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
{
    var req = BuildRequest(next, next.Drm!, _defaultRelay, startPaused: true, _descriptor);
    _runtime.EnsureLicense(req.Pssh.Span, req.DefaultKid!, req.LicenseRelay!);         // license: in flight from THIS line
    var session = ProtectedVideoSession.Create(_runtime, req);
    await session.PrefetchAsync(segments: 2, ct).ConfigureAwait(false);                // init + 8 s at the start position, both streams
    _prepared[req.Source!.Key] = session;                                              // OpenAsync consumes it by key; unconsumed sessions expire after 30 s
    return new ProtectedPreparedItem(session, ready: true, duration: TimeSpan.FromMilliseconds(req.DurationMs));
}
public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
{
    var req = BuildRequest(source, source.Drm!, opts.LicenseRelay ?? _defaultRelay, opts.StartPaused, _descriptor) with { StartPosition = opts.StartPosition };
    if (!_prepared.Remove(source.Key, out ProtectedVideoSession? session))            // warm: prepared; cold: create now (the license may still be pending — attach anyway, §3.1.1)
    { _runtime.EnsureLicense(req.Pssh.Span, req.DefaultKid!, req.LicenseRelay!); session = ProtectedVideoSession.Create(_runtime, req); }
    return ValueTask.FromResult<IMediaSession>(new ProtectedMediaSession(session, req, opts));
}
```

`ProtectedMediaSession` changes are deletions: `_pumpPoll` / `PumpPollMs` / `TransportSettlePollMs` / `KeepPollingFor`
/ `ShouldPoll` (`:90-177`) go — the runtime's event sink calls `RequestPump()` (the same `IVideoPumpSource` contract
the clear session uses, `MfMediaSession.cs:117-120`); the seek-intent window (`SeekSuppressMs`, `SeekReachedToleranceMs`,
`:69-81, 472-503`) goes — `Seeking`/`Seeked` are events and position is a timestamped sample extrapolated exactly as
`MfMediaSession.cs:517-524`; both 90 s watchdogs go — a session that never raises `CanPlay` within
`MediaOpenOptions`-derived `BufferPolicy.InitialPlayback` × 10 (10 s) reports `MediaError{Drm | Network}` from the
runtime's own error event, and the Wavee `StartWatchdog` (25 s) stays as the net. `StartPosition` is honoured
(`FgPrOpenDesc.startPositionMs`) — S1.

#### 3.1.4 Audio across the switch — a cut at a sample boundary, on purpose

Why not "the same audio clock continues": in Media3 the clock survives a period boundary because the SAME
`AudioTrack` keeps playing and only `startMediaTimeUs` is re-anchored (`DefaultAudioSink.java:1033-1062`), and in
YouTube Music the song and the video share the audio rendition. Wavee's song plays through the engine's WASAPI graph
(`PcmAudioPlayer`, `Playback.Audio.cs`) and the music video's soundtrack is an AAC track under the video's content key
that only the PMP can decrypt and only the Media Engine's SAR can render (§1.3, C:1222-1246). They are two devices,
two clocks and two masters (the video edit's audio is not the album master), so there is no sample stream to continue.
The design is Chromium's hard-cut discipline (`renderer_impl.cc:867-883`: the time source is started only when BOTH
streams have enough) applied across two engines:

```
 audio graph (song)  ═══════════ playing ═══════════╗ fade 80 ms ╗ silent (session kept, position frozen at Pc)
 video session       …prefetched… attach ─ Metadata ─ FirstFrame(paused @ P') ─┬─ Seek(exact, Pc) ─ Play ═══ soundtrack ═══
 reducer             Effects.SwitchVideo(P)          │ Phase=Presenting         └─ Effects.AudioCut(fadeMs=80)
                                                     │ (the poster drops here — K)
 t:                  0                               ~120-250 ms warm            +80 ms                    first audible video sample
```

Sequence, all posted through the reducer (C1/C4, epoch-stamped), never awaited across layers:

1. The reducer decides `SwapThenLoad` (`MediaSwitch.Decide`) and emits `Effects.Load{Kind = Video, FromMs = P}` **without** `Effects.Stop` — `ShouldStopOutgoingHost` gains a third state, `StopAfterSuccessor` (Playback.cs, owner G): the song keeps playing while the video attaches.
2. `Video.Load` attaches the prefetched session paused at P (`startPaused = 1` → `FrameStep` → `FgPrEvent_FirstFrame`). The docked cap shows the FIRST FRAME under the poster cross-fade while the song is still audible (Chromium's paint-before-HAVE_ENOUGH, §1.5.2).
3. On `FirstFrame` the host posts `Input.Audio(AudioSignal.Started …)` with `Phase = Presenting`; the reducer emits `Effects.AudioCut(fadeMs: 80)` + `Effects.VideoGo(atMs: Pc)` where `Pc` = the audio clock NOW (`Audio.ActivePositionMs()`, `Playback.Audio.cs:1351`) + 80 ms.
4. `Audio.FadeOutAndPark(80)` retargets the master `TransportRamp` (`TransportRamp.cs:6-30`, the frame-domain de-click envelope the graph already owns) to 0 over 80 ms and parks the session (no dispose — the byte source and the decoder stay for the way back).
5. `Video.Go(Pc)`: one exact seek inside the prefetched window (Pc − P ≤ 300 ms, always buffered) then `Play`; the soundtrack's first sample is at Pc. The cut is at a sample boundary on both sides; the gap is the seek's decode-to-target (≤ one GOP, ≈ 30-80 ms hardware) — inaudible as a gap, audible as a cut, which it is.
6. Back to the song: symmetric. `Effects.Load{Kind = Audio, FromMs = Pv}`: `Audio.Resume` the parked session at Pv (the parked decoder seeks inside its own buffer — the fast-start head is still attached; a park older than 30 s reloads, `Playback.Audio.cs` prepared-slot rules) while the video's volume ramps to 0 over 80 ms (`FgPrSessionSetVolume`), then `FgPrSessionDetach` (store kept 30 s for a re-toggle).

Position continuity is the reducer's: `State.PositionMs` is written from the ACTIVE host's clock only, `Pc` is
captured at step 3 (not at the request), and the seek bar never jumps because the video is positioned to the audio
clock, not the other way round.

#### 3.1.5 The Wavee host — prefetch schedule, load with a start position, phases

`Playback/Playback.Video.cs` (H) gains three things and loses two (§6 lists them as requests; the code below is what
the requests ask for). NEW CORE rules live in `Playback/Playback.Video.Rules.cs` (owner V, §6) so they are testable
without an engine.

```csharp
public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>What to prefetch for a track, and when. Pure. Inputs are the facts the reducer already has.</summary>
        public enum PrefetchLevel : byte { None, Manifest, ManifestAndLicense, Full }

        public readonly record struct PrefetchInput(
            bool HasVideo, bool VideoOn, bool Metered, bool IsCurrent, int MsToBoundary, PrefetchLevel Already, bool ManifestFresh);

        public static class PrefetchSchedule
        {
            public const int NextTrackWindowMs = 20_000;     // "ending soon": the same window Audio.Prepare uses for gapless
            public const int ManifestTtlMs = 10 * 60_000;     // 0.2.9's SingleFlightMemo TTL; signed CDN urls outlive it

            /// <summary>The level to reach NOW. Never asks for bytes on a metered link; never asks for a license for a
            /// track whose video will not be shown (VideoOn is the placement != None ∨ the user's video preference).</summary>
            public static PrefetchLevel Decide(in PrefetchInput i)
            {
                if (!i.HasVideo) return PrefetchLevel.None;
                PrefetchLevel want;
                if (!i.VideoOn) want = i.IsCurrent ? PrefetchLevel.Manifest : PrefetchLevel.None;   // the badge is lit: know WHAT, cost one GET
                else if (i.IsCurrent) want = i.Metered ? PrefetchLevel.ManifestAndLicense : PrefetchLevel.Full;
                else if (i.MsToBoundary <= NextTrackWindowMs) want = i.Metered ? PrefetchLevel.ManifestAndLicense : PrefetchLevel.Full;
                else want = PrefetchLevel.None;
                if (i.Already >= want && (i.ManifestFresh || want == PrefetchLevel.None)) return PrefetchLevel.None;
                return want;
            }
        }

        /// <summary>Where a switch is, for the surfaces (K reads it; H writes it on the UI thread).</summary>
        public enum SwitchPhase : byte { Idle, Resolving, Licensing, Buffering, Attaching, Presenting, Playing, Failed }
        public static readonly Signal<SwitchPhase> Phase = new(SwitchPhase.Idle);
    }
}
```

The host's `Load(source, epoch, fromMs)` passes `fromMs` as `MediaOpenOptions.StartPosition` (a new
`MediaPlayer.OpenAsync(source, opts)` overload the engine adds — the facade already builds `MediaOpenOptions`
itself, `MediaPlayer.cs:341-353`; today the caller cannot set `StartPosition`); the carried-position tick arm
(`:637-647`) is deleted with it. `Prefetch(track)` is a new entry the reducer calls from a new `Effects.PrefetchVideo`
slot (G): manifest (memoised, single-flight, `ManifestTtlMs`) → `Manifest.Resolve` (unchanged) → `backend.PrepareAsync`
on the protected backend (§3.1.3). The pump order is the same latest-wins slot; a prefetch never displaces a load.

#### 3.1.6 The switch, after

```
 WARM (badge lit ≥ 2 s ago, VideoOn; license usable, init + 2 segments at P in the store, runtime alive)
   t0   reducer Effects.Load{Video, P}          → Video.Load → Plan = Switch → OpenAsync(source, StartPosition = P)
   t0   ProtectedMediaBackend.OpenAsync: prepared session found → ProtectedMediaSession → ConnectSignals → Attach
   +5   FgPrSessionAttach: SetSource("cenc://…/<id>") on the warm engine; source Start @ keyframe ≤ P from the store
   +60-120  MF: topology (protected wrap, ITA from the cached CDM) + H.264 MFT init + decode to P
   +120-250 FIRSTFRAMEREADY → FgPrEvent_FirstFrame → RequestPump → snapshot.handle → Bind → phase-11 Drain → Commit
            [FIRST VIDEO FRAME at P, song still audible]                                          target ≤ 300 ms
   +130-260 AudioCut(80) ∥ Seek(exact, Pc) → Play                                                 soundtrack at Pc
 COLD (nothing prefetched; runtime alive)
   t0   Manifest GET ∥ (after parse) license POST ∥ init GET ∥ seg(P) GET ×2 streams   — 1 RTT + 1 RTT, all overlapped
   +300-600  store has init + seg(P); license usable (or pending — attach anyway)
   +450-850  FIRSTFRAMEREADY                                                                       target ≤ 1 s
 FIRST VIDEO OF THE PROCESS
   + FgPrRuntimeCreate: MFStartup + D3D11 + engine + CDM + PMP (mfpmp.exe)                        +300-800 ms, target ≤ 1.5 s total
   — unless Video.Boot() ran at the first badge-lit moment (it does: Boot() is called from Load AND from Prefetch), in which case the runtime is up before the toggle.
```

### 3.2 The seek — a planner over a keyframe table, flush-not-recreate

#### 3.2.1 What the native side already does right, and the four changes

`CencMediaSource::Start(pd, GUID_NULL, VT_I8)` repositions each stream to the last keyframe ≤ target with a
discontinuity (C:668-713, 1011-1077); the Media Engine decodes from that keyframe to the PTS on a NORMAL seek and
presents the keyframe on an APPROXIMATE one (§1.5.3); a paused seek gets a `FrameStep` (N:541-548). That is
flush-not-recreate already — Chromium's `DecoderStream::Reset` and Media3's `flushOrReinitializeCodec` (§1.5). The
changes are around it:

1. **Apply immediately.** `FgPrSessionSeek` posts a work item to the runtime thread; the item checks the store
   (`CanSeekTo`, C:858-879 — kept) and either calls `SetCurrentTime(Ex)` now or registers the seek as the feeder's
   NEXT target and lets the feeder's completion callback call it. No 80 ms tick (S2, S9).
2. **Parallel fetch for a far seek.** The feeder issues the video and audio segment GETs concurrently (two `HttpClient`
   requests in flight), cancels an in-flight GET with a cancellation token (no 10 ms poll), and raises
   `FgPrEvent_Buffered` when the pair lands (S3).
3. **Time-based retention.** `retainBehindMs` / `bufferAheadMs` replace `kRetainBehind = 300` / `kMaxSamplesAhead =
   900` (C:734, N:2203); the store trims by presentation time against `storeBudgetBytes` (S4).
4. **Keep the keyframe table.** `ParseSegment` already knows every sync sample (C:518-535); it appends
   `(pts, segmentIndex, byteOffset)` to a per-session sorted table that `FgPrSessionGetKeyframes` reads and
   `FgPrEvent_Keyframes` announces. Segment starts are always in the table (DASH segments begin with an IDR — every
   Spotify segment the native has parsed prepends SPS/PPS to its first sample, C:518-535); intra-segment IDRs
   appear as segments are parsed. §7 Q5 asks what Spotify's GOP actually is; the planner works either way.

#### 3.2.2 `Video.SeekPlanner` (CORE, `Playback/Playback.Video.Rules.cs`, owner V)

```csharp
public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>The seek modes the UI has (Media3's SeekParameters, reduced to what a scrubber needs).</summary>
        public enum SeekIntent : byte { Commit, Preview }

        public enum SeekVerb : byte
        {
            /// <summary>A keyframe ≤ target is buffered: reposition now, decode to target (Commit) or show the keyframe (Preview).</summary>
            Instant,
            /// <summary>Fetch ONE segment pair (video ∥ audio) at <see cref="SeekPlan.SegmentIndex"/>, then Instant.</summary>
            Fetch,
            /// <summary>Preview only: the closest KNOWN keyframe that is buffered — never a fetch while the pointer is down.</summary>
            Coarse,
            /// <summary>The target is the current segment and within one GOP ahead: let playback reach it (no seek at all).</summary>
            Ride,
        }

        /// <summary>The plan for one seek: what to do, where the decoder starts, how many ms it must decode past the keyframe.</summary>
        public readonly record struct SeekPlan(SeekVerb Verb, long KeyframeMs, int SegmentIndex, long DecodeToTargetMs);

        /// <summary>Everything the planner reads. Spans over the host's fixed buffers (P8): no allocation per plan.</summary>
        public readonly ref struct SeekIndex
        {
            public readonly ReadOnlySpan<long> Keyframes;    // ascending presentation ms of every sync sample the demuxer has seen
            public readonly ReadOnlySpan<long> Buffered;     // ascending (start, end) ms pairs
            public readonly long SegmentLengthMs;            // Spotify: segment_length × 1000 (4 000 by default)
            public readonly long DurationMs;
            public readonly long PositionMs;                 // the current playhead
            public SeekIndex(ReadOnlySpan<long> keyframes, ReadOnlySpan<long> buffered, long segmentLengthMs, long durationMs, long positionMs)
            { Keyframes = keyframes; Buffered = buffered; SegmentLengthMs = segmentLengthMs; DurationMs = durationMs; PositionMs = positionMs; }
        }

        public static class SeekPlanner
        {
            /// <summary>Ride instead of seek when the target is ahead of the playhead by less than this (one segment): the
            /// decoder is already producing those frames (Media3 lets a forward seek inside the buffer ride when it maps to
            /// the same sync; Chromium's SourceBufferRange fudge room is the same idea).</summary>
            public const long RideAheadMs = 250;

            public static SeekPlan Plan(in SeekIndex ix, long targetMs, SeekIntent intent)
            {
                long t = Math.Clamp(targetMs, 0, ix.DurationMs > 0 ? ix.DurationMs : long.MaxValue);
                if (intent == SeekIntent.Commit && t > ix.PositionMs && t - ix.PositionMs <= RideAheadMs && IsBuffered(ix.Buffered, t))
                    return new SeekPlan(SeekVerb.Ride, ix.PositionMs, SegmentOf(ix, t), 0);

                long kf = PreviousKeyframe(ix.Keyframes, t);              // -1 when nothing ≤ t is known
                if (kf >= 0 && IsBuffered(ix.Buffered, kf) && IsBuffered(ix.Buffered, t))
                    return new SeekPlan(SeekVerb.Instant, kf, SegmentOf(ix, kf), t - kf);

                int seg = SegmentOf(ix, t);
                long segStart = seg * ix.SegmentLengthMs;                 // a segment start is ALWAYS a keyframe (DASH); it may not be in the table yet
                if (intent == SeekIntent.Preview)
                {
                    long near = ClosestBufferedKeyframe(ix.Keyframes, ix.Buffered, t);
                    return near >= 0 ? new SeekPlan(SeekVerb.Coarse, near, SegmentOf(ix, near), 0)
                                     : new SeekPlan(SeekVerb.Fetch, segStart, seg, 0);   // nothing buffered near it: one fetch, keyframe shown
                }
                return new SeekPlan(SeekVerb.Fetch, segStart, seg, t - segStart);
            }

            public static int SegmentOf(in SeekIndex ix, long ms) => ix.SegmentLengthMs > 0 ? (int)(ms / ix.SegmentLengthMs) : 0;

            /// <summary>Binary search: the last keyframe ≤ ms, or -1.</summary>
            public static long PreviousKeyframe(ReadOnlySpan<long> keyframes, long ms)
            {
                int lo = 0, hi = keyframes.Length - 1, ans = -1;
                while (lo <= hi) { int mid = (lo + hi) >> 1; if (keyframes[mid] <= ms) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
                return ans < 0 ? -1 : keyframes[ans];
            }

            public static bool IsBuffered(ReadOnlySpan<long> pairs, long ms)
            {
                for (int i = 0; i + 1 < pairs.Length; i += 2) if (ms >= pairs[i] && ms < pairs[i + 1]) return true;
                return false;
            }

            static long ClosestBufferedKeyframe(ReadOnlySpan<long> keyframes, ReadOnlySpan<long> buffered, long ms)
            {
                long best = -1, bestDist = long.MaxValue;
                for (int i = 0; i < keyframes.Length; i++)
                {
                    long k = keyframes[i];
                    if (!IsBuffered(buffered, k)) continue;
                    long d = Math.Abs(k - ms);
                    if (d < bestDist) { best = k; bestDist = d; }
                    if (k > ms && d > bestDist) break;
                }
                return best;
            }
        }
    }
}
```

The host feeds it from two fixed buffers (`long[1024]` keyframes — a 4-minute video at one IDR per second is 240;
`long[64]` buffered pairs) refilled from `GetKeyframes` / `GetBuffered` on each `FgPrEvent_Keyframes` /
`FgPrEvent_Buffered` (never per frame), and passes `KeyframeMs` down as the native seek's hint so the native side
does not repeat the search.

#### 3.2.3 The managed seek path, after

```
 UI drag → SeekPreviewScheduler (100 ms) → Video.Seek(ms, accurate:false)          (S8: the reducer's Seek effect carries the mode)
   → SeekPlanner.Plan(ix, ms, Preview) → Coarse → FgPrSessionSeek(ms, mode 1, kf)   → SetCurrentTimeEx(APPROXIMATE) now → Seeked event
     (a preview NEVER fetches while the pointer is down; the scrub shows the closest buffered keyframe — YouTube's thumbnails do the same job)
 release → Video.Seek(ms, accurate:true)
   → Plan(ix, ms, Commit) → Instant | Fetch | Ride
     Instant: FgPrSessionSeek(ms, 0, kf) → SetCurrentTime now → decode kf→ms (≤ 1 GOP) → Seeked            ≈ 40-150 ms
     Fetch:   feeder GET seg video ∥ audio (1 RTT + bytes) → Buffered → SetCurrentTime → decode → Seeked     ≈ 250-500 ms
     Ride:    no engine call; the reducer's position catches up on its own                                   0 ms
 state: `Seeking` is a JOINING state, not Buffering — the surface keeps the previous frame (K: no spinner under 400 ms — Media3's allowedJoiningTimeMs, Chromium's 250 ms first-paint timer); position publishes the target at once and the landed value on Seeked (S5, S7)
 paused: the native FrameStep shows the frame at the target (S10); the host's Play re-assert is deleted (Playing is an event, never inferred)
```

Audio/video re-sync policy: both streams belong to the one presentation the Media Engine clocks, so a seek is one
`SetCurrentTime` and the engine restarts its presentation clock at the landed PTS — audio (every AAC frame is a sync
sample) starts exactly at the target on an exact seek and at the keyframe time on an approximate one; there is no
app-side re-sync to do, and the app's audio graph is parked during video (§3.1.4). What the app does own is the
POSITION it shows: target immediately, landed on `Seeked`, extrapolated by `PositionQpc` between events.

The bug map:

| # | Fix | Where |
|---|---|---|
| S1 | `startPositionMs` on the open; `StartPosition` honoured by the protected session; the tick's carried-seek arm deleted | `FgPlayReady.h`, `ProtectedVideoSession.Create`, `Playback.Video.cs:637-647` (delete) |
| S2, S9 | Verbs are posted work items on the runtime thread; a seek before the store has the target registers as the feeder's next target | `PrSession.cpp` |
| S3 | Video ∥ audio GETs; token cancellation | `PrSession.cpp`, `SegmentStore.h` |
| S4 | `retainBehindMs` / `bufferAheadMs` / `storeBudgetBytes` | `FgPrOpenDesc`, `SegmentStore.h` |
| S5, S7 | `Seeking`/`Seeked`/`Position(qpc)` events; no suppression window; extrapolated position | `ProtectedMediaSession.cs` (delete `:69-81, 472-503`) |
| S6 | No ack waits: `PlayAsync`/`PauseAsync`/`SeekAsync` return completed tasks; `Waits.cs` deleted | `DesktopProtectedVideoPlayer.Waits.cs` (delete) |
| S8 | `Effects.Seek` carries `SeekIntent`; `Execute` routes by `State.Kind` to `Audio.Seek` or `Video.Seek(ms, accurate)` | `Playback.cs` (G), `Playback.Host.cs:392` (G) |
| S10 | `PlayReassertBudget` deleted; `Playing` is an event | `Playback.Video.cs:179, 669-680` (delete) |

### 3.3 The rendering path — zero-copy where it is legal, and honest about where it is not

**Protected content.** The frame's path today is: H.264 MFT (hardware, DXVA on the runtime's D3D11 video device) →
NV12 decoder texture-array slice → the Media Engine's own video processor (one GPU pass to BGRA8 in its windowless
swap chain, sized to `ContentSizeFor` so a 4K stream in a 640-px card allocates 640-px buffers — `MfMediaSession.cs:561-580`)
→ `GetVideoSwapchainHandle` → `IDCompositionDevice::CreateSurfaceFromHandle` → the child visual z-below the UI swap
chain, revealed through the `DrawVideoCmd` hole (§1.2). No CPU byte is touched, no copy is ours, and the visual is
composited by DWM at vsync. **This stays.** The documented alternative — frame-server mode with
`IMFMediaEngineProtectedContent::TransferVideoFrame` + `ShareResources` (§1.5.3) — requires the DESTINATION to be a
protected surface, and a protected surface can be composited (a DComp visual, an HW-protected swap chain) but never
sampled by an unprotected shader into the UI's back buffer: to draw a PlayReady frame as an ordinary `DrawImage` the
whole UI swap chain would have to become `DXGI_SWAP_CHAIN_FLAG_HW_PROTECTED`, every screenshot of Wavee would go
black, and the OPM/HDCP policy would apply to the entire window. That is not a rendering path; it is a regression.
Chromium reaches the same conclusion: its protected Windows path is `MediaFoundationRenderer` presenting into a
DComp surface, not the `D3D11VideoDecoder` shared-image path of §1.5.2.

What changes on the protected path is the CADENCE, not the pixels: `FIRSTFRAMEREADY` → one event → one pump →
`Bind` → the next frame's phase-11 `Drain` → `Commit` (`VideoSurfaceRegistry.cs:379-430`), so the first frame is on
screen one host frame after MF presents it instead of up to 250 + 200 ms later; `OnVideoStreamTick` /
`UpdateVideoStream(nullptr)` every 80 ms (N:2797-2800) is deleted — in windowless swap-chain mode "the Media Engine
… presents video frames to the swap chain" on its own (§1.5.3), which is exactly how the clear `VideoMediaEngine`
already runs (it repaints only on `Repaint` commands, `VideoMediaEngine.cs:442-445`); and the `StreamRect` /
content-size discipline of the clear session (`MfMediaSession.cs:485-500`) is applied to the protected one (today
`ProtectedMediaSession` hands `SetContentSize` the surface size and never sizes the stream — `:455-464`), so the
protected swap chain is also capped to what the destination can show.

**Clear content (local `.mp4` overrides, module streams — `MediaKind.MfVideoOrFile` without `DrmConfig`).** Here a
real zero-copy-into-our-textures path exists and is worth having ONLY if a surface wants to treat the video as an
image (rounded corners for free, a blur/ambient glow behind the docked cap, the deck's "canvas" face). It is Chromium's
`D3D11VideoDecoder` → shared-image shape (§1.5.2) expressed through the Media Engine:

```
 VideoMediaEngine (clear, frame-server mode: NO EnableWindowlessSwapchainMode; MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = B8G8R8A8)
   engine thread, on FrameClock vsync (phase 11 hook, render thread posts "tick" — never the UI thread):
     OnVideoStreamTick(&pts) == S_OK  →  TransferVideoFrame(ring[i].d3d11Tex, src=null, dst=full, border)      (one GPU VPBlt, NV12→BGRA)
     ring[i].fence11->Signal(seq)  (ID3D11DeviceContext4::Signal on a shared ID3D11Fence — the fence D3D12 opened with OpenSharedHandle)
   render thread, phase 11: ID3D12CommandQueue::Wait(fence12, seq) → the frame's DrawImage samples ring[i].tex12 (a TextureHandle in the image pool, ContentEpoch++)
 ring: 3 × BGRA8 textures created ONCE per natural size on the D3D11 side with D3D11_RESOURCE_MISC_SHARED_NTHANDLE, opened on the D3D12 side
       with ID3D12Device::OpenSharedHandle — never per frame (P8); a FORMATCHANGE re-creates the ring (cold path)
```

No CPU copy, one GPU copy (the VPBlt the engine does anyway), no per-frame allocation, and synchronisation through
the documented shared-fence interop (§1.5.3: "a shared fence is opened in DirectX 11 with `ID3D11Device5::OpenSharedFence`
… a shared resource with `OpenSharedResource1`") rather than D3D11On12 ("not optimized for performance … significant
memory overhead") or `ALLOW_SIMULTANEOUS_ACCESS` ("can compromise resource fences"). Pacing is the engine's:
`OnVideoStreamTick` answers S_FALSE when no new frame is due, so a 24 fps clip costs 24 blits per second on a 120 Hz
display, and the UI frame that samples the texture is the host's own (no extra wake: the tick is posted from the
render thread's phase-11 pass only while a clear video surface is `Presenting`, `VideoSurfaceRegistry.SetPresenting`).
This is **Phase C** (§5, after Wave 4), only if K asks for effects on clear video (§7 Q6); it adds one seam
(`VideoDelivery.SharedTexture(TextureHandle, epoch)` beside `VideoDelivery.CompositedSurface`, `MediaSeams.cs:395-399`)
and changes nothing on the protected path.

### 3.4 Placement — the host contract owner K consumes (Wave 4, `Shell/Video.UI.cs`, `Video.Host.cs`)

The four surfaces are K's; this plan gives them what the host publishes and what they call, nothing more (A13). The
contract is the existing one plus four additions, all on the UI thread (C1):

| K reads / calls | Type | Meaning | Today / new |
|---|---|---|---|
| `Playback.Video.Player` | `Signal<Binding(MediaPlayer?, long Generation)>` | the ONE player; the element's `Key` is `"gen:" + Generation` — a source switch never remounts | exists (`Playback.Video.cs:93-98`) |
| `Playback.Video.Source` | `Signal<VideoSource?>` | the resolved source: `Key`, `NaturalWidth/Height` (the aspect seed before any frame), `IsLive` | exists (`:100-102`) |
| **`Playback.Video.Phase`** | `Signal<SwitchPhase>` | `Idle · Resolving · Licensing · Buffering · Attaching · Presenting · Playing · Failed` — the poster/spinner discriminator (W5/W13/W14b/W18 in ch 24 show "no player yet"; `Presenting` is the moment the poster cross-fades) | NEW (§3.1.5) |
| **`Playback.Video.FirstFrame`** | `Signal<long>` (epoch) | bumps once per source when `FirstFrame` lands — the surface drops its poster on this, never on a state guess | NEW |
| `IMediaPlayer.PumpVideo(binding, rect, scale)` | call | the one pump; exactly one mounted surface per player (`OneSurfacePerPlayerGuard`); binds the DComp handle, sizes the stream, places the child | exists (`MediaPlayer.cs:160-164`) |
| `VideoBinding.SetCornerRadius / SetViewport / SetGeometryNode` | call | rounded cap, center-crop PiP, follow a compositor-only move | exists (`VideoSurfaceRegistry.cs:513-533`) |
| **`Playback.Video.Seek(ms, accurate)`** | call | `accurate:false` while the scrubber is down (Preview: never fetches), `true` on release (Commit) | exists; the reducer now routes it (S8) |
| **`Playback.Video.Buffered`** | `Signal<int>` (epoch) + `CopyBuffered(Span<long>)` | the buffered ranges for the seek bar's loaded band — what MSE's `buffered` gives a web scrubber | NEW |
| `Playback.Video.DetachedAvailable`, `CanOpenDetachedWindow` | seam | the pop-out HWND is K's; `Video.Host.cs` sets the hook | exists (`:765-769`) |
| a placement move (docked → PiP → pop-out) | — | re-bind only: the new surface's `PumpVideo` binds the SAME handle to a new registry token (`Pump` binds every pump, `DesktopProtectedVideoPlayer.cs:549-560`); no open, no seek, no phase change | exists; unchanged |

Rules K's surfaces obey that this plan makes possible: (1) a `Seeking` phase under 400 ms shows NO spinner (the
previous frame stays — joining, §3.2.3); (2) the poster cross-fade (ch 24 §5, `PosterCrossFadeMs = 150`) is driven by
`FirstFrame`, and the docked cap sizes from `Source.NaturalWidth/Height` at mount so `NaturalSize` is a confirm
(ch 24 DATA GAP 1); (3) the switching overlay of the old app plan (`video-smooth-switching-implementation.md:129-131`)
stays deleted — ch 24 §9 "do not re-add the app-side switching overlay" — because `Phase` + `FirstFrame` give the
surface the exact moment instead. Nothing K writes calls the engine's media API directly; the deck faces and the rail
read the same signals.

### 3.5 Budgets — latency and allocation

Today's figures are the derived ones of §1.4.2-1.4.3 (no live episode exists in the log — the always-on lines below
are what makes the next log carry real ones). Targets are what the manual gate (§4.3) measures.

| Metric | Today (derived) | Target | What pays for it |
|---|---|---|---|
| Song→video, **warm** (badge lit ≥ 2 s, VideoOn, not metered) — request → first video frame at the carried position | 2.7-7 s, first frame at 0:00 then a jump | **≤ 300 ms** (≤ 450 ms on a first switch after the runtime idled out) | prepared session (license usable, init + 8 s at P in the store), `SetSource` on a warm engine, `startPositionMs`, event-driven first frame |
| Song→video, **cold** (VideoOn toggled on a track whose manifest was never fetched) | 2.7-7 s | **≤ 1 s** (≤ 1.5 s for the first video of the process: runtime create + PMP) | manifest GET → license POST ∥ init GET ∥ seg(P) GETs, all overlapped; runtime alive |
| Audio gap across the switch | silent from the request (t0) to the video's first audible sample: the whole switch | **cut, not gap**: 80 ms fade of the song, the soundtrack's first sample ≤ 100 ms after the fade ends | the song plays through the attach; `AudioCut` only on `FirstFrame` |
| Video→song | `Audio.Load` from scratch: fast-start head fetch + decode ≈ 300-600 ms of silence | **≤ 120 ms** (80 ms video fade ∥ resume of the parked audio session) | park, don't dispose; the audio session's own buffer covers the resume position |
| Seek, **near** (target inside the retained/ahead window, keyframe known) | 0-80 ms tick + APPROXIMATE/NORMAL + 10 ms ack poll + 250 ms pump poll + 200 ms Wavee tick; "landed" up to 6 s | **≤ 150 ms** to the landed frame; position published at once | immediate apply, events, time-based retention, the planner's `Instant` |
| Seek, **far** (one segment pair to fetch) | 80 ms + cancel poll + 2 serial GETs (0.4-1.2 s) + polls | **≤ 500 ms** on a ≥ 5 Mbps link (1 RTT + one 4 s segment pair ≈ 0.5-1 MB at ≤ 480p) | parallel GETs, `Fetch` plan, no polls |
| Scrub preview step (pointer down) | one native seek per 100 ms, each with a 5 s ack waiter; a far step fetches | **≤ 100 ms** to the closest buffered keyframe; **0** network while the pointer is down | `Coarse`; the fetch happens on release |
| First frame after a paused seek | `FrameStep` + polls ≈ 300-500 ms | **≤ 120 ms** | `FrameStep` + `FirstFrame` event |
| Runtime memory while a video plays | a second D3D11 device + MF engine + swap chain + CDM: "tens to well over a hundred megabytes" (`MfMediaPlayer.cs:53-63`), uncounted | same order, **plus** ≤ 32 MiB store per live session (≤ 2 sessions), all counted in the census (`FgPrEvent_Bytes`, `Buffered`) | `storeBudgetBytes`; the 30 s idle teardown |

The allocation plan (P8), stated as a table:

| Site | Allocation | When |
|---|---|---|
| `SegmentStore` (native) | `storeBudgetBytes` (32 MiB) reserved per session in 64 KiB slabs; segments are placed, never `new`'d per fetch | session create (cold path) |
| keyframe table (native) | `int64[4096]` per session (a 4-minute video at one IDR/s is 240 entries; 4096 covers a 4-hour broadcast at 4 s GOP) | session create |
| the host's index buffers (managed) | `long[1024]` keyframes + `long[64]` buffered pairs, static, refilled in place | once |
| `VideoEngineSnapshot` (managed) | POD struct behind the seqlock, 0 bytes per read (`gate.media.seam.snapshot-alloc-free`) | — |
| license relay | ONE `byte[]` per challenge (the copy out of native memory) + the HTTP body — per license, i.e. per KID per session, never per open | license acquire |
| event callback → managed | `VideoNativeEvent` is a 40-byte struct; text is a `ReadOnlySpan<char>` over the native buffer, formatted into the log's own scratch — no string per event | per event (≤ 4 Hz steady state) |
| pump | none (`ProtectedMediaSession.PumpVideo` reads one snapshot, writes value-gated signals) | per pump (coalesced) |
| planner | `SeekPlan` is a 32-byte record struct on the stack; `SeekIndex` is a `ref struct` over the two static buffers | per seek |

The always-on log lines (CLAUDE.md: always-on, no env switch; one file — the native events are written by the
managed sink into Wavee's log under `[video.native]`, and `desktop-playready.log` is retired):

```
[video] prefetch.plan   track=…  level=Manifest|ManifestAndLicense|Full  why=current|next|badge  metered=…
[video] manifest.ok     key=…tail  ms=…  cached=true|false  rungs=…  segLen=…  dur=…
[video] license.ok      kid=…  ms=…  cached=true|false            [video] license.fail kid=… hr=… ms=…
[video] prefetch.ok     key=…  init=2  segs=4  bytes=…  ms=…  at=…ms
[video] switch.begin    key=…  from=…ms  plan=Switch|Rebuild|SeekOnly|None  warm=true|false  epoch=…
[video] open.ok         key=…  epoch=…  attachMs=…  metadataMs=…  canplayMs=…
[video] first.frame     key=…  epoch=…  sinceSwitchMs=…  sinceAttachMs=…  pos=…ms  natural=WxH
[video] audio.cut       fadeMs=80  songPos=…ms  videoPos=…ms  gapMs=…        [video] audio.back  fadeMs=80  resumeMs=…  gapMs=…
[video] seek.plan       target=…  intent=Commit|Preview  verb=Instant|Fetch|Coarse|Ride  kf=…  seg=…  decodeMs=…
[video] seek.done       target=…  landed=…  ms=…  fetched=0|1
[video.native] …        every FgPrEvent, with the session id and the runtime's ms-since-create
```

`switch.begin → first.frame` is the number Christos asked for; `seek.plan → seek.done` is the seek; `audio.cut
gapMs` is the thing that must read "cut" and not "gap".

---

## 4. Tests and the gate

### 4.1 Unit tests — pure, engine-free (`src/apps/Wavee.Tests/VideoRulesTests.cs`, new; the existing pattern of `SetupGating` / `ShutdownUpdatePolicy`)

| Class under test | Facts pinned |
|---|---|
| `Video.Plan(SwitchInput)` (exists) | the 17 rows of 0.2.9's `VideoSwitchPolicyTests` re-hosted: no player → Rebuild; faulted → Rebuild even on the same key; same key → None / SeekOnly on `StartAtMs > 0`; different key → Switch; ordinal keys |
| `Video.PrefetchSchedule.Decide` | no video → None; badge lit + video off → Manifest for the current track only; video on + current → Full, or ManifestAndLicense on metered; next track → Full inside `NextTrackWindowMs`, None outside; an `Already ≥ want` with a fresh manifest → None; a stale manifest re-asks Manifest even when Already = Full |
| `Video.SeekPlanner.Plan` over a fake index | Instant when a keyframe ≤ target and the target are buffered; Fetch with the right segment index and `DecodeToTargetMs = target − segStart` when not; Ride for a forward target within `RideAheadMs` inside the buffer; Preview never returns Fetch while any buffered keyframe exists (Coarse, the closest one, ties broken backwards — `fastSeek`'s "before the current position" rule for backward seeks); Preview with nothing buffered → Fetch of the segment start with `DecodeToTargetMs = 0`; clamping to `[0, Duration]`; `PreviousKeyframe` binary search on 0, 1, 2 and 1,024 entries; `IsBuffered` on the pair boundaries (start inclusive, end exclusive) |
| `Video.LicenseCache` policy (`ProtectedVideoRuntime`'s decision extracted as a pure `LicenseCachePolicy` in the engine — gated by `FluentGpu.Windows.Tests`, listed here because Wavee's prefetch schedule assumes it) | Usable → Reuse; Pending → Await (no second POST); Expired/Failed → Acquire; LRU eviction at 8 keeps the playing KID; a `LicenseUsable` for an evicted handle is ignored |
| `Video.Manifest.Parse` (exists) | the 0.2.9 `SpotifyVideoManifestTests` fixtures re-hosted (both JSON shapes; PlayReady index gating; H.264-only; AAC-only with the highest bitrate; `{{segment_timestamp}}` split at the last `/`; `end_time_millis − start_time_millis` fallback; a manifest without a PlayReady rung → null) |
| `Video.SegmentGrid` (a 20-line helper in `Rules.cs`: `IndexOf(ms)`, `StartOf(i)`, `Count(durationMs)`) | stride arithmetic for `segment_length` 4 and 10; the last partial segment; index 0 at `ms = 0` |
| the audio-cut order (reducer, owner G): `MediaSwitch.ShouldStopOutgoingHost` → `StopAfterSuccessor` for Audio→Video, `StopFirst` for Video→Audio | a `Step` sequence: Load{Video} emits no Stop; `AudioSignal.Started` from the video epoch emits `AudioCut` + `VideoGo` with `atMs = audioPos + 80`; a Load{Audio} while video plays emits `PauseHost` (video fade) + `Load` in one fold; stale-epoch signals emit nothing |

### 4.2 Engine tests (`C:\wavee\fluent-gpu\src\FluentGpu.Windows.Tests`, the engine's own gate)

| Test file | What it proves | Needs |
|---|---|---|
| `ProtectedSessionTests.cs` (new, over the existing `FakeProtectedVideoPlayer` in `Fakes.cs:211` extended with `GetKeyframes/GetBuffered` and an event injector) | `ProtectedMediaSession` publishes Opening → (FirstFrame) → Ready/Playing from EVENTS with no timer; Seeking is published as a joining state and the position is the target then the landed value; `StartPosition` reaches the fake's open; a placement re-bind binds the same handle to a new token | fakes only |
| `LicenseCachePolicyTests.cs` (new) | §4.1's cache rows | pure |
| `ProtectedRuntimeTests.cs` (new) | with the REAL DLL: create the runtime, acquire the license for the baked Axinom single-key vector (the native's own test content, N:2988-2995; license server `https://drm-playready-licensing.axtest.net/AcquireLicense`), `LicenseUsable` arrives once; a second `EnsureLicense` for the same KID returns Usable without a callback; two sessions on one runtime attach in turn and the second's `FirstFrame` arrives ≤ 300 ms after `Attach` when its store was prefetched; runtime idle teardown after the test's shortened idle | Windows dev box with MF + the built DLL (these are the tests `DrmTests.cs` already needs the box for) |
| `CencDemuxTests.cs` (new, via a `FgPrProbeFile(path, out keyframes)` export that runs `ParseInit` + `ParseSegment` on a local file) | the keyframe table of a clear fragmented MP4 is exactly its sync samples; `nalLenSize == 4` accepted, others refused; a CENC file's subsample map survives the Annex-B rewrite | the vectors below |
| `MfMediaSessionTests.cs` (exists) + `MediaSeamSuite` (VerticalSlice, exists) | unchanged gates: `gate.media.seam.snapshot-alloc-free / -no-tear / command-coalesce / …` — the protected session now publishes the SAME `VideoEngineSnapshot`, so `snapshot-alloc-free` covers both backends | — |

**The decode vectors.** No CC0 DASH sample could be verified (DASH-IF's test vectors are Blender content under CC BY;
Bitmovin's public Sintel MPD is CC BY 3.0). Two honest choices, both used: (1) **Chromium's `media/test/data`**
(BSD-3-Clause, redistributable with attribution) — `bear-1280x720-av_frag.mp4` (clear fragmented MP4, init + `moof`
runs) for the demuxer and keyframe-table tests and `bear-1280x720-a_frag-cenc.mp4` (CENC, the subsample map) for the
encrypted rewrite, both on disk at `C:\WAVEE\chromium-media\media\test\data\`; (2) a **generated** clip for the
seek gate — `ffmpeg -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -g 60
-keyint_min 60 -sc_threshold 0 -c:a aac -f dash -seg_duration 4 gate.mpd`, checked in under
`src/FluentGpu.Windows.Tests/Fixtures/video/` (~1 MB) — because a seek gate needs a KNOWN GOP (2 s here) to assert
`SeekPlanner` verbs against the real table the demuxer produces, and no downloaded sample documents its GOP. The
PlayReady half of the gate stays on the Axinom vector the native already uses.

### 4.3 The manual gate — what Christos runs (the numbers come from §3.5's always-on lines)

One purposeful run of the Release arm64 publish (memory: "their app is the Release arm64 publish"), a playlist of
music videos, video on in the docked cap:

1. Play a song with a video badge; wait 3 s (the prefetch lands — `prefetch.ok … at=…ms` in the log); **toggle video**
   → read `switch.begin → first.frame sinceSwitchMs` (warm target ≤ 300) and `audio.cut gapMs` (≤ 100, and it must
   sound like a cut, not a hole). Toggle back → `audio.back gapMs` (≤ 120).
2. **Next** three times with video on → three `first.frame sinceSwitchMs` lines (warm, because `prefetch.plan why=next`
   fired at ending-soon), no `Rebuild` plan, no `[video.native] runtime.create` after the first.
3. Three seeks: a scrub of ~3 s with the pointer down (≥ 10 `seek.plan verb=Coarse` lines, **zero** `fetched=1` while
   down), a release inside the buffer (`verb=Instant`, `seek.done ms` ≤ 150), and a far seek to the last minute
   (`verb=Fetch`, `seek.done ms` ≤ 500, `fetched=1`). Then a paused seek: the frame changes with no play (`first.frame`
   is NOT re-logged; `seek.done` is).
4. Cold: restart the app, play a badge-lit track, toggle video within 1 s of the track start → `first.frame
   sinceSwitchMs` ≤ 1 000 (≤ 1 500 if `runtime.create` is in the same second).
5. Memory: `mem.sample` after 5 minutes of video ≤ the pre-video floor + the runtime's counted bytes; 30 s after
   video off, `[video.native] runtime.destroy` and the floor returns.
6. Nothing else regresses: the audio-only gates of the master plan (Wave 3 §gate), lyrics timing (`lyrics.clock`),
   `nav.frames` on a page navigation during video.

The gate passes on the numbers; a line that is missing is a failed gate (the log IS the instrument).

---

## 5. What starts today, what waits

| Now (no dependency on Waves 3-4) | With Wave 3 (G, H) | With Wave 4 (K) | After Wave 4 |
|---|---|---|---|
| Engine: `FgPlayReady.h` + `PrRuntime/PrLicense/PrSession.cpp` + `SegmentStore.h`; `ProtectedVideoRuntime/Session.cs`; `ProtectedMediaBackend` prefetch; `ProtectedMediaSession` de-polled; `MediaPlayer.OpenAsync(source, opts)`; the engine tests + vectors (§4.2) | `Playback.cs` / `Playback.Host.cs`: `Effects.PrefetchVideo`, `Effects.AudioCut`/`VideoGo`, kind-routed `Load`/`Seek`/`Stop`, `StopAfterSuccessor`; `Playback.Video.cs`: `Prefetch`, `StartPosition`, `Phase`, `FirstFrame`, `Buffered`, the deletions; `Playback.Audio.cs`: `FadeOutAndPark` / `Resume` | the surfaces read `Phase` / `FirstFrame` / `Buffered`, no spinner under 400 ms of `Seeking`, the aspect seed | Phase C (§3.3 clear-content shared texture) if wanted; option (b) MSE if the gate shows `attachMs` > 300 warm |
| Wavee CORE: `Playback.Video.Rules.cs` + `VideoRulesTests.cs` (owner V) | | | |

The engine work can land and be gated in `..\fluent-gpu` before a single Wavee line changes: the protected backend
keeps its `IMediaBackend` / `IPreparableBackend` faces, `ProtectedMediaSession` keeps `IMediaSession`, and the
0.3 host as written today (`Playback.Video.cs`) would simply get faster (the open honours nothing new yet, but the
runtime, the license cache and the event-driven session apply to it unchanged).

---

## 6. The work split

### 6.1 Files, owners, budgets — disjoint from in-flight owners

Engine (implemented and gated in `C:\wavee\fluent-gpu`; three agents, disjoint files; the orchestrator builds Debug +
Release, runs `FluentGpu.Windows.Tests` and VerticalSlice, and `build.cmd` for both arches — §7 Q8):

| Agent | Files (under `C:\wavee\fluent-gpu\`) | Lines | Depends on |
|---|---|---|---|
| **E1 — native runtime** | NEW `ops/tools/playready-native/FgPlayReady.h`; `PlayReadyNative.cpp` split into `PrRuntime.cpp` (runtime thread + work queue, D3D11/DXGI, engine, CDM, PMP host, event callback), `PrLicense.cpp` (KID table, `KeyMessage` → relay → `Update`, key status), `PrSession.cpp` (session table, scheme handler `cenc://fluentgpu/<id>`, feeder with parallel GETs + token cancel, transport work items, seek apply, snapshot); NEW `SegmentStore.h`; `CencMediaSource.h` (+ keyframe table, time-based retain, `startPositionMs`); `build.cmd`; `README.md`; delete every `FG_CENC_*` / `FG_PLAYREADY_LICENSE_URL` read | ≈ 1,900 (of which ≈ 1,200 moved) | — |
| **E2 — managed session** | NEW `src/FluentGpu.WindowsApi/Media/PlayReady/ProtectedVideoRuntime.cs`, `ProtectedVideoSession.cs`, `LicenseCachePolicy.cs`; `ProtectedMediaBackend.cs` (prefetch, prepared table, `_runtime`); `ProtectedMediaSession.cs` (de-polled, joining seek, `StartPosition`); `ProtectedVideoTypes.cs` (`StartPosition`, `DurationMs`, `SegmentLengthMs`); `IProtectedVideoPlayer.cs` (+ `GetKeyframes/GetBuffered/SeekAsync(hint)`); DELETE `DesktopProtectedVideoPlayer.cs`, `DesktopProtectedVideoPlayer.Waits.cs`, `DrmLicenseBridge.cs`; `VideoEngineSeam.cs` (+ `FirstFrameTimestamp`, `BufferedAheadMs`); `MediaPlayer.cs` (`OpenAsync(source, opts)`); `docs/design/subsystems/media-pipeline.md` §8.4 (the "create-per-open residual" paragraph is retired) | ≈ 1,100 | E1's header (pinned above) |
| **E3 — tests + vectors** | `src/FluentGpu.Windows.Tests/{Fakes.cs (extend), ProtectedSessionTests.cs, LicenseCachePolicyTests.cs, ProtectedRuntimeTests.cs, CencDemuxTests.cs}`; `Fixtures/video/` (the generated clip, the two Chromium bear files with their LICENSE); `VerticalSlice/Suites/MediaSeamSuite.cs` (+ `gate.media.seam.protected-snapshot-alloc-free`) | ≈ 900 | E2's types |

Wavee (this repository, `C:\WAVEE\wavee-0.3`):

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|--:|---|
| `Playback/Playback.Video.Rules.cs` | **CORE, new named partial**: `PrefetchSchedule`, `SeekPlanner`, `SeekIndex`, `SegmentGrid`, `SwitchPhase` — no engine type, no `Signal` | **V** (new) | 3-parallel, **starts today** | 350 | this plan §3.1.5, §3.2.2 |
| `Wavee.Tests/VideoRulesTests.cs` | §4.1 | V | 3-parallel | 400 | §4.1 |
| `Playback/Playback.Video.cs` | H's file — the **request list** of §6.2; budget stays 1,100 (≈ +180 −160) | H | 3 | 1,100 | plan A13 |
| `Playback/Playback.cs`, `Playback.Host.cs` | the reducer/host requests of §6.2 (G) | G | 3 | +90 | ch 24 §9 item 4 already asks for the slot |
| `Playback/Playback.Audio.cs` | `FadeOutAndPark(ms)` / `Resume(atMs)` on the existing session (the `TransportRamp` retarget + park) | H | 3 | +60 | §3.1.4 |
| `Shell/Video.cs`, `Video.UI.cs`, `Video.Host.cs` | K's files — **requests, not edits** (§6.2) | K | 4 | 0 | A13 |
| `Entities/Track.cs` | `Column<ushort> VideoW, VideoH` under `TrackFields.Video` (ch 24 DATA GAP 1) — the aspect seed BEFORE the manifest; filled by `Video.Manifest.Resolve` through owner A's commit path | A | 1 (already shipped) → a request | +25 | ch 24 §7 |

### 6.2 Requests to in-flight owners (their files, their waves — nothing here edits them)

**H (`Playback.Video.cs`, Wave 3):**
1. `Load(source, epoch, fromMs)` → `OpenAsync(source, new MediaOpenOptions { StartPosition = fromMs, StartPaused = true })`; delete the carried-seek tick arm (`:637-647`, `s_startSeekPending`, `StartClampGuardMs/BackoffMs`) and `IsSeekReady`.
2. New `Prefetch(EntityRef track)`: memoised manifest (`PrefetchSchedule.ManifestTtlMs`, single-flight per key — the `SingleFlightMemo` of the old app plan §2, engine-free), then `s_protectedBackend.PrepareAsync(source, ctx, ct)`; log `prefetch.plan / manifest.ok / license.ok / prefetch.ok`; `Boot()` from here too.
3. `Phase`, `FirstFrame`, `Buffered` signals written on the UI thread from the session's events; `Video.Go(atMs)` (exact seek inside the window + `Play`); `SetVolumeRamp(to, ms)` for the fade back.
4. `Seek(ms, accurate)` builds the `SeekIndex` from the two static buffers, calls `SeekPlanner.Plan`, logs `seek.plan`, passes `KeyframeMs` as the hint; `Ride` makes no engine call.
5. Delete `PlayReassertBudget` / the Ready-re-assert arm (`:179, 669-680`), `StartWatchdog` stays (25 s) as the only watchdog above the engine.
6. Route the native event text into the app log as `[video.native]` (one file; `desktop-playready.log` is gone).

**G (`Playback.cs` / `Playback.Host.cs`, Wave 3):**
1. `Effects.PrefetchVideo{Row, Id, Level}` emitted from `Step` per `PrefetchSchedule.Decide` — on track start (current), on the badge turning on, on `VideoOn` changing, and at ending-soon (next) beside `PrepareNext`.
2. `Execute`: `Load` routed by `LoadKind` (`Video.Load` vs `Audio.Load`), `Seek` by `State.Kind` with the intent, `Stop` likewise; `AudioCut{FadeMs}` → `Audio.FadeOutAndPark`; `VideoGo{AtMs}` → `Video.Go`; `PrefetchVideo` → `Video.Prefetch`.
3. `MediaSwitch.ShouldStopOutgoingHost` → an enum `{ StopFirst, StopAfterSuccessor }`: Video→Audio stops first (two soundtracks must never overlap — the rule stays), Audio→Video stops AFTER `AudioSignal.Started` from the video epoch. `AllowCrossfade` unchanged.
4. `State.PositionMs` is written from the ACTIVE host only; `Pc` is captured at the `Started` fold.

**K (`Shell/Video.*`, Wave 4):** read `Phase` / `FirstFrame` / `Buffered` as §3.4 says; no spinner under 400 ms of `Seeking`; the scrub row calls `Seek(ms, accurate:false)` while down; the docked cap seeds its aspect from `Track.VideoW/H` then `Source.NaturalWidth/Height` then the frame.

**A (`Entities/Track.cs`, shipped):** the two `ushort` columns above.

**F (`Spotify.Api.cs`, shipped):** nothing — `Manifest.Resolve` and `License.Relay` already speak the wire.

### 6.3 The gate for this plan

Engine: Debug + Release build of `src/FluentGpu.slnx` clean, `FluentGpu.Windows.Tests` green including the four new
files, VerticalSlice "ALL CHECKS PASSED", `check-canon.ps1` exit 0 after the `media-pipeline.md` §8.4 edit, both DLL
arches built by `build.cmd`. Wavee: Debug + Release clean, `Wavee.Tests` green (+ `VideoRulesTests`), then §4.3 by
Christos with the log lines quoted back in the closing report. The CHANGELOG bullet ends with the issue number
(CLAUDE.md) — §7 Q10 asks which.

---

## 7. Open questions — only Christos can answer

1. **The audio hand-off is a cut with an 80 ms fade of the song and the video's own soundtrack from the carried
   position (§3.1.4).** The alternative — keep the SONG playing and show the video muted, YouTube-Music-style — is
   impossible to keep in sync (the video edit is a different master) and would drift within seconds. Is the cut
   acceptable, and is 80 ms the right fade (Spotify's own client cuts hard)?
2. **How long does the video runtime stay warm?** The plan reuses the clear engine's 30 s idle teardown. With video ON
   in the docked cap the runtime is never idle; with video OFF it costs ~100 MB for 30 s after the last toggle. Keep
   30 s, or tie it to "video on" (never idle while the placement is not `None`)?
3. **Prefetch policy.** `PrefetchSchedule` asks for the license + init + 8 s of media for the CURRENT track the moment
   the badge is lit and video is on, and for the NEXT track 20 s before the boundary; never bytes on a metered link.
   That is ≈ 1-2 MB per music video that may never be watched, and one license POST per such track (Spotify sees a
   license request for an unplayed video). Acceptable? Should the license be skipped until the toggle (cost: +1 RTT
   on a cold switch, ≈ 250 ms)?
4. **Retention window.** 30 s behind / 60 s ahead, 32 MiB per session, two sessions max — or smaller (the docked cap
   is the common case; a PiP over a slow link may want less ahead)?
5. **Spotify's GOP.** The planner's `Coarse` preview is only as fine as the keyframe table; if Spotify encodes one
   IDR per 4 s segment, a scrub preview steps in 4 s jumps (YouTube shows thumbnails for this reason). The first gate
   run logs `[video.native] keyframes` per segment — if the GOP is ≥ 2 s, do you want a thumbnail strip (a separate
   feature, K) or is segment-granular preview fine?
6. **Clear-content shared texture (Phase C, §3.3).** Only worth building if K wants effects on clear video (rounded
   corners are already free through the DComp clip; an ambient glow or the deck's canvas face are not). Build it in
   0.3 or leave it?
7. **Option (b) — the Media Engine's MSE.** If the warm `attachMs` lands above 300 ms in the gate, the next lever is
   the browser-only MSE seam (no topology rebuild per source). It is undocumented for Win32 and a spike of a few days.
   Pre-approve the spike on that trigger, or decide then?
8. **The native DLL build.** `build.cmd` is a local MSVC build; the release script asserts the junction for the
   private repo but not this DLL's freshness. Who builds arm64, and should `wavee-release.ps1` gain a gate that the
   shipped `FluentGpu.PlayReady.Native.dll` hash matches the source tree's `build.cmd` output (and which host builds it)?
9. **`FG_VIDEO_ZABOVE` and the `FG_CENC_*` switches.** The plan deletes the native ones (CLAUDE.md). `FG_VIDEO_ZABOVE`
   in `DCompVideoPresenter.cs:57` is the engine's diagnostic — delete it in the same pass, or leave it to the engine's
   own hygiene?
10. **Issue numbers.** Which GitHub issues do "switching to video is super slow" and "seeking is buggy and slow" carry
    (or should be opened — `github-triage`)? The CHANGELOG bullets and the commit bodies need them.

---

## 8. Sources

Engine and app code cited by path:line throughout (`C:\wavee\fluent-gpu\src\**`, `ops\tools\playready-native\*`,
`C:\WAVEE\wavee-0.3\src\apps\Wavee\**`, `src\apps\_old\Wavee\**`). Reference repositories on disk: `C:\WAVEE\media3`
(androidx/media, main), `C:\WAVEE\chromium-media\media` (chromium/src sparse `media/`, `67aa03ec`), `C:\WAVEE\mpv`
(`10dee0a0`). The log: `%LOCALAPPDATA%\Wavee\logs\wavee-20260913.log` (read-only).

Documentation (all learn.microsoft.com unless noted):
- `IMFMediaEngineClassFactory::CreateInstance` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengineclassfactory-createinstance
- `IMFMediaEngine::OnVideoStreamTick` / `TransferVideoFrame` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-onvideostreamtick , https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-transfervideoframe
- `IMFMediaEngineEx::EnableWindowlessSwapchainMode` / `UpdateVideoStream` / `SetAudioEndpointRole` / `FrameStep` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengineex-enablewindowlessswapchainmode , …-updatevideostream , …-setaudioendpointrole , …-framestep
- `IMFMediaEngineProtectedContent::ShareResources` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengineprotectedcontent-shareresources
- `MF_MEDIA_ENGINE_CONTENT_PROTECTION_MANAGER`, `MF_MEDIA_ENGINE_PROTECTION_FLAGS`, `MF_MEDIA_ENGINE_DXGI_MANAGER`, `MF_MEDIA_ENGINE_EXTENSION`, `MF_MEDIA_ENGINE_PLAYBACK_HWND` — https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-content-protection-manager , https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/ne-mfmediaengine-mf_media_engine_protection_flags , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-dxgi-manager , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-extension , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-playback-hwnd
- `IMFMediaSourceExtension`, `IMFSourceBuffer`, `IMFMediaEngineClassFactoryEx::CreateMediaSourceExtension`, `IMFMediaEngineExtension`, `IMFMediaEngineEx` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nn-mfmediaengine-imfmediasourceextension , …/nn-mfmediaengine-imfsourcebuffer , …/nf-mfmediaengine-imfmediaengineclassfactoryex-createmediasourceextension , …/nn-mfmediaengine-imfmediaengineextension , …/nn-mfmediaengine-imfmediaengineex
- `MF_MEDIA_ENGINE_READY`, `MF_MEDIA_ENGINE_EVENT`, `MF_MEDIA_ENGINE_SEEK_MODE`, `MF_MEDIA_ENGINE_PRELOAD`, `IMFMediaEngine::SetCurrentTime` / `Load` — https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/ne-mfmediaengine-mf_media_engine_ready , …/ne-mfmediaengine-mf_media_engine_event , …/ne-mfmediaengine-mf_media_engine_seek_mode , …/ne-mfmediaengine-mf_media_engine_preload , …/nf-mfmediaengine-imfmediaengine-setcurrenttime , …/nf-mfmediaengine-imfmediaengine-load
- `IMFDXGIDeviceManager::ResetDevice`, `IMFDXGIBuffer::GetResource` / `GetSubresourceIndex`, Supporting Direct3D 11 Video Decoding in Media Foundation, `MF_SA_D3D11_*`, `IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx` — https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nf-mfobjects-imfdxgidevicemanager-resetdevice , https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nf-mfobjects-imfdxgibuffer-getresource , https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sa-d3d11-bindflags , https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-imfvideosampleallocatorex-initializesampleallocatorex
- `IMFSourceReader::SetCurrentPosition`, `MF_SOURCE_READER_DISABLE_DXVA`, `MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING`, `MF_SOURCE_READER_D3D_MANAGER` — https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-setcurrentposition , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-source-reader-disable-dxva , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-source-reader-enable-advanced-video-processing , https://learn.microsoft.com/en-us/windows/win32/medfound/mf-source-reader-d3d-manager
- `IMFContentProtectionManager`, `MFCreateContentProtectionDevice` — https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nn-mfidl-imfcontentprotectionmanager , https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatecontentprotectiondevice
- Direct3D 11 on 12; `ID3D12Device::CreateSharedHandle`; `ID3D11Device5::OpenSharedFence`; `ID3D11Device1::OpenSharedResource1`; `IDXGIResource1::CreateSharedHandle`; `D3D11_RESOURCE_MISC_FLAG`; `D3D12_RESOURCE_FLAGS`; Direct3D 12 Video overview; `d3d12-mf-guids`; `MFCreateD3D12SynchronizationObject`; `IMFD3D12SynchronizationObjectCommands` — https://learn.microsoft.com/en-us/windows/win32/direct3d12/direct3d-11-on-12 , https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createsharedhandle , https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nf-d3d11_4-id3d11device5-opensharedfence , https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11device1-opensharedresource1 , https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle , https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_resource_misc_flag , https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_resource_flags , https://learn.microsoft.com/en-us/windows/win32/medfound/direct3d-12-video-overview , https://learn.microsoft.com/en-us/windows/win32/medfound/d3d12-mf-guids , https://learn.microsoft.com/en-us/windows/win32/api/mfd3d12/nf-mfd3d12-mfcreated3d12synchronizationobject , https://learn.microsoft.com/en-us/windows/win32/api/mfd3d12/nn-mfd3d12-imfd3d12synchronizationobjectcommands ; microsoft/media-foundation issue #82 https://github.com/microsoft/media-foundation/issues/82 ; GDK Media Foundation decode https://learn.microsoft.com/en-us/gaming/gdk/_content/gc/system/overviews/mediafoundation-decode
- PlayReady: license acquisition overview; licenses; the PlayReady Header specification; UWP PlayReady client SDK; hardware DRM; `PlayReadyLicenseIterable(.ctor)`, `PlayReadyLicense`, `PlayReadyLicenseSession`, `PlayReadyLicenseManagement`, `PlayReadyContentHeader` — https://learn.microsoft.com/en-us/playready/overview/license-acquisition , https://learn.microsoft.com/en-us/playready/overview/licenses , https://learn.microsoft.com/en-us/playready/specifications/playready-header-specification , https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/playready-client-sdk , https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/hardware-drm , https://learn.microsoft.com/en-us/uwp/api/windows.media.protection.playready.playreadylicenseiterable.-ctor , https://learn.microsoft.com/en-us/uwp/api/windows.media.protection.playready.playreadylicense , https://learn.microsoft.com/en-us/uwp/api/windows.media.protection.playready.playreadylicensesession , https://learn.microsoft.com/en-us/uwp/api/windows.media.protection.playready.playreadylicensemanagement , https://learn.microsoft.com/en-us/uwp/api/windows.media.protection.playready.playreadycontentheader
- W3C Media Source Extensions (media-source-2) https://www.w3.org/TR/media-source-2/ ; MDN `SourceBuffer` https://developer.mozilla.org/en-US/docs/Web/API/SourceBuffer ; WHATWG HTML `fastSeek` https://html.spec.whatwg.org/multipage/media.html ; MDN `HTMLMediaElement.fastSeek` https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/fastSeek
- DASH-IF Guidelines-TimingModel §22 Addressing https://github.com/Dash-Industry-Forum/Guidelines-TimingModel/blob/master/22-Addressing.inc.md ; DASH-IF Test-Vectors https://github.com/Dash-Industry-Forum/Test-Vectors ; DASH-AVC/264 test vectors https://dashif.org/docs/DASH-AVC-264-Test-Vectors-v1.0.pdf ; "URL and timestamp of MPEG-DASH segments" https://dev.to/sunfishshogi/url-and-timestamp-of-mpeg-dash-segments-2pdn ; Bitmovin public test streams https://bitmovin.com/blog/mpeg-dash-hls-examples-sample-streams/ ; community list of public test streams https://github.com/bengarney/list-of-streams
- YouTube: "Reverse-engineering YouTube: revisited" https://tyrrrz.me/blog/reverse-engineering-youtube-revisited ; YouTube.js `FormatInitializationMetadata` https://ytjs.dev/googlevideo/api/exports/protos/interfaces/FormatInitializationMetadata ; "YouTube Music now lets listeners switch seamlessly between audio and video" (blog.youtube, 2018-11) https://blog.youtube/news-and-events/youtube-music-now-lets-listeners-switch/
- Spotify video (public, thin): forum.videohelp.com threads 407115 and 416309 https://forum.videohelp.com/threads/407115-Spotify-Podcast-Video , https://forum.videohelp.com/threads/416309-Need-help-decrypting-Spotify-Video-Podcasts-Widevine
- Chromium test data licence: `C:\WAVEE\chromium-media\media\test\data` (BSD-3-Clause, chromium/src `LICENSE`)
