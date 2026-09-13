# Wavee 0.3 — headless Wavee, investigation and implementation plan

Status: DRAFT for approval, 2026-09-13. Worktree `C:\WAVEE\wavee-0.3`, branch `feat/0.3-structure`. Companion to
`wavee-0.3-implementation.md` (the plan; §2 file map, §5 waves, §8 gates), `wavee-0.3-vorbis-implementation.md`
(§5.4 requests per scenario, §7.3 the gate the numbers pin), `wavee-0.3-flac-implementation.md` (§8.3 the lossless
smoke line) and the tray plan being written in parallel (`wavee-0.3-tray-implementation.md`; §6 below is the
interface it reads). Engine citations are against the pinned worktree `C:\WAVEE\fluent-gpu-base` (note:
`Directory.Build.props:36` resolves `$(EngineRoot)` to `..\fluent-gpu` unless overridden — see §8 Q6).

Every path under `src/apps/Wavee/` below is relative to `C:\WAVEE\wavee-0.3\src\apps\Wavee\`; `_old/` is
`C:\WAVEE\wavee-0.3\src\apps\_old\`. Nothing in this document reads or describes the audio-key derivation: the seam
is named once (§1.2, `Spotify.Audio.KeyDeriver`) and treated as opaque.

---

## 0. The decision, in three sentences

0.3 is **almost** built for a headless run: every cross-thread seam in the session, the reducer, the fetch runner and
the store is an `Action<Action>` that defaults to "run inline" (`Playback.ToUi`, `Spotify.Post`, `Store.Post`,
`Palette.Host.Post`), the audio pump opens a real WASAPI render client from any thread with no window, and the OS
bridges stay off by construction when there is no HWND — so a headless host is one single-threaded dispatcher, a
boot order, and a command grammar over verbs that already exist. What blocks it today is not the engine but four
holes in the app: nothing parses arguments (`App.Main()` takes none, `Diagnostics.Probe.cs` is a 15-line stub),
nothing calls `Spotify.Login()` or wires the marshallers (the inline default silently breaks the single-writer rule
in the GUI too), `PlayNow` needs an `EntityRef` + `QueueCursor` that only a page can build today, and the silent
sink never starts its feeder (`--fake` would sit in `Loading` forever). The design is a `--headless` arm dispatched
from `Main` into `Screens/Diagnostics.Probe.cs` (SHELL host: the loop, the boot, stdin/named-pipe, the real or silent
endpoint) over a new named CORE partial `+Screens/Diagnostics.Headless.cs` (the command grammar, the wait-condition
evaluator, the script runner, the JSON-lines writer, the exit-code table, the settings overlay and the protected
credential store — all engine-free and unit-tested), driven by a fixed script set under `ops/headless/` that becomes
the Wave 2 / Wave 3 gates and the stream and decoder plans' verification of their own request-count and latency
tables on real traffic.

---

## 1. Research record — what is true today

### 1.1 Is 0.3 already designed for this? What exists, what is a stub

**The boot is a fixed sequence with no arguments.** `App.cs:16-28`: `[STAThread] static void Main()` — GC mode,
`Glyphs.Register()`, `Platform.Boot()`, `Entities.Boot(Platform.Scope)`, `Spotify.Boot()`, `Playback.Boot()`,
`Modules.Boot()`, `Shell.Run()`, `Platform.Shutdown()`. `Main` takes no `args`; the only argument parsing in the tree
is `Shell.Host.cs:126-127` (`Environment.GetCommandLineArgs()`) and `ParseArgs` at `:208-223`, which knows exactly
`--frames`, `--screenshot`, `--width`, `--height` — the engine harness knobs. `--fake` is not parsed anywhere:
`Platform.cs:1003-1006` lists "`--fake` argument parsing and `Clock.SeedEpoch`; `Scope` gets its `--fake` arm THERE"
under LEFT FOR OWNER S (Wave 6). `launchSettings.json:3-13` still carries 0.2.9's `--real-backend` / `--screenshot`.

**`Platform` is real and boot-complete for a no-UI host.** `Platform.cs:960-978` `Boot()`: settings store
(`Platform.Host.cs:60`, HKCU via the engine's `AppDataStore`), `ZoomAutoPolicy.MigrateMode`, the OS culture, the log
(`Platform.Host.cs:78-98`, file sink + `Diag.Sink` bridge + the `logResolved=` startup line), the credential slot
(`Platform.Host.cs:72-76`: `FileLocalStore(StorePath)` + `DpapiProtector`), then `Scope = ResolveScope()`
(`Platform.cs:938-945`: the stored credential's username, else `Keys.LastAccount`, else `CatalogScope.Fake`). "Nothing
here opens a socket, a database or a window" (`:959`). The seams a host needs are all public: `UseSettings`
(`:809`), `UseCredentialSlot` (`:837`), `TryLoadCredential/SaveCredential/ClearCredential/HasStoredCredential`
(`:859-881`), `DeviceId` (`:849`), `Locale`, `Scope`, `Shutdown` (`:982-986`, the flush-on-exit contract).

**`Spotify` is written but never started.** `Spotify.Session.cs:233-252` `Boot()` interns device id / client id /
language and opens nothing; `:256-260` `Login()` posts `SessionEventKind.Login` with `Flag = !LoadCredential().IsEmpty`
and returns; the AP thread (`:297-305`, `wavee-spotify-ap`, background) runs resolve → connect → handshake → login →
mint → pump, the dealer thread (`:307-315`) brings the session to `Online` on the `hm://pusher/` frame. State is
readable from any thread as an immutable box: `Spotify.Current` (`:191`), `Current.Phase/IsOnline/Tier/Country`
(`Spotify.cs:97-129`); `Spotify.Status` / `Spotify.Fault` are signals written inside the post (`:194-197`, `:215-216`).
**No code in the app calls `Spotify.Login()`, sets `Spotify.Post`, sets `Api.Market`, or calls `Entities.Switch` on
the welcome** (the doc at `Platform.cs:928-935` and `Spotify.Api.cs:100-107` says "Wave 2's session calls
`Entities.Switch` the moment the welcome lands" / "App.cs copies … when Online" — neither exists). The PKCE helper
(`Spotify.cs:800-837`) has no caller; there is no OAuth / device-code flow in 0.3 at all, only the wizard's strings
(`assets/loc/en-US.json:1230-1236`).

**`Spotify.Audio` opens a track from a uri string, blocking, on any non-UI thread.** `Spotify.Audio.cs:643-663`:
`PreferredQuality()` reads `Platform.Keys.PlaybackQuality` per open; `Open(uri, quality, fallbackDurationMs, ct)`
returns `Opened.Failed(Fault.Offline)` unless `Current.IsOnline`, then `ChooseTrack` (metadata, the rung ladder, the
lossless `spotify:audio:` entity when `Quality.Lossless`) and `OpenBody` (`Spotify.Audio.Stream.cs:1156-1227`: head ‖
storage-resolve ‖ key, then range 1 ‖ tail — the four-request cold start of vorbis plan §5.4). `Quality` is
`Normal96/High160/VeryHigh320/Lossless` (`:84`). The key path is one named seam, `Spotify.Audio.KeyDeriver`
(`Spotify.Audio.cs:136`, `CanDerive` `:140`); the AP key request is tried first (`:473-508`) and FLAC never asks the AP
(`:168-169`). A public-only build answers `Fault.NoDeriver` for FLAC (flac plan §8.3 already writes that as a gate
line).

**The stream layer already counts.** `Spotify.Audio.Stream.cs:254-266` `Fetcher.Requests / Cancelled / PeakInFlight /
Outstanding / PingMs / BytesPerSecond`; `:496-509` `Ring.Waits / Starves / Want / Cursor / Slots / Seconds / Epoch`;
`:804-825` `Body.HeadBytes / SpliceProof / LengthKnown / FileLength / TailGranule / BytesPerSecond`. The always-on
timeline lines (`:28-30`) are `audio.open.begin → audio.head → audio.open → audio.first → audio.len → audio.splice →
audio.range src=cdn|local → audio.tail → audio.ring`, plus `audio.retarget`, `audio.underrun` (`:549`), `audio.key`,
`audio.prefetch`. Not counted anywhere: head-request count, storage-resolve count, per-body request count (`Requests`
is the shared fetcher's), disk-cache hit/miss, total CDN bytes.

**`Playback` is a reducer with a thread-safe inbox and no loop of its own.** `Playback.Host.cs:248-269` `Boot()`
resets state, builds the Connect `DeviceIdentity` from `Platform.DeviceId` + `Environment.MachineName` (`:252-258`),
reads the saved volume, sets `Spotify.Connect.Wake = () => ToUi(s_drain)` (`:264`), publishes the signals once and
creates (does not start) the 1 s ticker (`:267`, `:574-585`). "The audio pump and the OS bridges arm themselves on
their own first use, so a `--fake` run and a unit test both boot the reducer with no device anywhere" (`:246-247`).
`Post(in Input)` (`:282-300`) writes a 256-deep ring and wakes through `ToUi`; `Drain()` (`:306-329`) folds Connect's
mailbox, then the ring, then `Execute()` once (`:383-411`), then `Publish()` once (`:525-558`). The transport verbs
are `:592-615`: `PlayNow(EntityRef row, EntityId context, QueueCursor cursor, PlayableKind kind = Audio, int fromMs =
0)`, `Next`, `Previous`, `Pause`, `Resume`, `TogglePlay`, `SeekTo(ms)`, `GoLive`, `SetVolume(0..1)`, `SetShuffle`,
`SetRepeat`, `Stop`, `Release`, `TransferTo(rosterSlot)`. Observable: the signals at `:101-145` (`Current`,
`CurrentId`, `PhaseSignal`, `IsPlaying`, `Buffering`, `Error`, `PositionMs`, `DurationMs`, `Volume`, `StreamFormat`,
`OwnerSignal`, …), `Pending.Load/Transfer` (`:150-154`), `Snap()` (`:273`, a struct copy) and `State.Position(nowMs)`
(`Playback.cs:1090-1097`). `Phase.Ended` is reached only at the end of the queue (`Playback.cs:1496-1507`); a track end
is `Input.Ended` → `Advance` (`:1427-1464`) which walks `Queue.TryAdvance` (`Queue.cs:305-312`).

**There is no "play a uri" verb and no queue loader.** `PlayNow` wants an `EntityRef` and a `QueueCursor`; the
factories exist (`Entities.cs:1984-2030`: `Track(EntityId)`, `Ref(EntityId)`; `EntityId.TryParse` at `:326-335`),
`Queue.Replace(ReadOnlySpan<EntityRef>, ReadOnlySpan<QueueEdge>)` exists (`Queue.cs:344-355`) — and **has no caller
anywhere in the tree** (a Wave 5 page will be the first). Album/playlist members arrive through `Entities.Ensure` →
`Fetch.Plan` → `Spotify.Api.SpotifyFetchProvider` (`Spotify.Api.cs:567-621`, api thread, answers through
`Spotify.Post(() => Fetch.Answer(...))`) → `Entities.Commit` → `Entities.Publish()` (`Entities.cs:1967`).
`Fetch.Pump()` (`Fetch.cs:548-567`) is "called by the HOST on its frame tick" — nothing calls it yet either
(`Palette.Host.cs:15,78` name it as the shell's job). The audio pump resolves the uri itself:
`Playback.Audio.cs:1654-1660` `Spotify.Audio.Open(id.Text, ct)` — a bare handle with nothing fetched is enough to
play a single track.

**`Playback.Audio` is real over WASAPI; the silent sink is not.** `Playback.Audio.cs:298-322` `Audio.Boot()` (on
first `Load`/`Prepare`/`SetVolume`) calls `WasapiPcm.CreateBackend` + `MediaPlayer.Build().WithBackend(...)`; the
five sources are inline (`:1591-2118`). `SilentSink` (`:1823`) is the engine's `HeadlessAudioEndpoint`
(`fluent-gpu-base…\Media\Playback\Audio\AudioClock.cs:192-213`) and `OpenSilentAsync` (`:639-668`) builds a
`PcmAudioSession(..., driveWithOwnThread: true, endpoint)` — **but never calls `session.ConnectSignals(...)`**, and the
engine starts its feeder only inside `ConnectSignals` (`PcmAudioPlayer.cs:1079-1087`) while `Advance` returns
immediately with no sink connected (`:1325`). So `PlayAsync` (`:1105-1113`) only sets a flag, the session's state
stays `Idle`, the 200 ms tick (`Playback.Audio.cs:1376`, which reads `sess.CurrentState` for silent sessions) never
posts `Started`/`Position`/`Ended`, and the reducer sits in `Loading` with `Pending.Load` true. Pause/Resume/Seek go
through `s_player` (`:382-405`), which has no session in the silent case; `Load` still probes WASAPI in `Boot()`
(`:361`, `:308`); and even connected, `NullAudioSink.WritableFrames = int.MaxValue` (`AudioClock.cs:265`) makes the
synthetic clock run as fast as `PumpAudio(1024); Thread.Sleep(5)` spins (`PcmAudioPlayer.cs:1708-1715`), not at
wall-clock speed. The existing fact (`Wavee.Tests/PlaybackAudioTests.cs:463-474`) checks only `IsReady` and the
format. **This is the Wave-5 `--fake` "PLAYING player bar over the silent sink" gate (plan §5 :1118-1120) failing
before it is written**; §7 books the fix to owner H.

**`Playback.Os` is off without an HWND, by construction.** `Playback.Os.cs:116-120` `Publish` self-activates with
`FluentApp.WindowHandle`; `Activate` (`:73-77`) returns at `hwnd == 0`. SMTC needs a real top-level window
(`:155-169`, `SystemMediaControls.GetForWindow(hwnd)`); media keys exist only through SMTC's `ButtonPressed`
(`:160-165`); the taskbar needs the HWND and the app host's events (`:352-363`); `PowerPolicy` (keep-awake, no HWND
needed) starts only inside `Activate` (`:82`) — so a headless run loses keep-awake unless it asks for it itself.

**What the plan already says.** §5 Wave 2 gate (`wavee-0.3-implementation.md:1043`): "a console smoke (`dotnet run
-- --login-smoke`, a `Diagnostics.Probe` entry) logs in with the stored credential and decodes one `getAlbum` into a
Staging with the expected row counts. (Network; run by the orchestrator only.)" §5 Wave 3 gate (`:1055`): "the login
smoke plays 10 s of a track through the real pump (orchestrator)". §7 (`:1209`): "Each wave has a gate that runs code
for real (db round-trip, login smoke, 10 s playback, …)"; (`:1213`): "Wave 2 login smoke decodes live payloads and
asserts row counts; the probe archives payloads for new fixtures". §2 (`:213`) books `Screens/Diagnostics.Probe.cs`
(owner S, Wave 6, 800 lines) for "`--perf-bench`, `--startup-bench`, `--crash-probe`, `--lyrics-advance-probe`
(ch 22 (a) — the env-var switch is deleted), `--qr-dump`, `NotificationSimulator`, the process receipts";
`:239` books `--fake` args in `Platform.cs` (owner S). The vorbis plan adds `--vorbis-probe` (`:2463`) and its
§8.3 gate lines "the login smoke … plays a track with `audio.open … head=81920 firstAudioMs<400`, seeks four times
with `audio.seek … requests=1` on at least three, hands off gapless with `exact=1`; a scrub of ten seeks leaves at
most one range in flight" (`:2494-2497`); the flac plan adds `--flac-probe` (`:1996`) and "the login smoke plays 10 s
at `Quality.Lossless` … `audio.open fmt=Flac24 rate=44100 bps=24` … a seek to 2:00 lands in ≤ 4 probes; on a
public-only build … `Fault.NoDeriver` and plays the 320 rung" (`:2006-2009`). The handoffs record the consequence:
"NOT run: the login smoke (network, needs the `Diagnostics.Probe --login-smoke` arm from Wave 6 and a real console —
Christos runs it)" (`handoff-20260912d-0.3-wave0-done.md:63-64`, again at `:89-90` for the 10 s playback smoke). **Two
waves have closed with their real-traffic gate deferred to a file that does not exist.** §5 of this plan is that file.

**What the CORE/SHELL split (G7) implies.** Every CORE file is already engine-free and single-threaded by contract
(`TestScope.cs:3-14`: "these tests never start the engine loop, never open a window and never touch a network");
every SHELL file reaches the UI thread through a settable `Action<Action>`. A headless host is therefore a *second
SHELL composition root* — the same CORE, the same SHELL files, a different marshaller and no `Shell.Run()` — not a
fork of anything.

### 1.2 Does anything require the engine loop or a window?

| Dependency | Where | Headless verdict | Substitution |
|---|---|---|---|
| `Signal<T>` writes | `Playback.Host.cs:525-558`, `Spotify.Session.cs:215-216`, `Playback.Audio.cs:180,195,205` | **Works.** `Signal<T>.SetIfChanged` has no thread check (`Foundation/Signals/Signal.cs:60-68`); `Tracking.Current` is `[ThreadStatic]` (`ReactiveCore.cs:47`) and null off the UI thread, so a write with no subscribers is compare-and-assign; the `BackwardsWriteGuard` compiles in only under `DEBUG || FLUENTGPU_DIAG` (`:62-63`). `ReactiveRuntime.FrameRequested` defaults to a no-op (`ReactiveCore.cs:316`). Read with `.Peek()`. | none |
| The UI-thread marshallers | `Playback.ToUi` (`Playback.Host.cs:60`), `Spotify.Post` (`Spotify.Session.cs:61`), `Store.Post` (`Store.cs:495`), `Palette.Host.Post` (`Palette.Host.cs:151`), `Sidebar.Host.ToUi` (`:64`), `Lyrics.Host.Store.ToUi` (`:1638`), `Spotify.Connect.Wake` (`Spotify.Connect.cs:90`) | **Must be set.** Every default runs inline — on whichever thread posted (timers, the pump, api threads, the dealer). `Drain` clears `s_wakeQueued` at entry (`:308`), so two threads can fold `s_state` concurrently; `Spotify.Apply` does an unlocked read-fold-write of `s_box` (`:212-214`) and calls `Entities.Intern` off the single writer (`:783-785`). **Nothing in the GUI sets them either** (the docs at `Playback.Host.cs:54-59` and `Spotify.Session.cs:58-61` say `App.cs` does; it does not — a live defect, §1.6). | one dedicated thread + a `BlockingCollection<Action>` (§2.3 `HeadlessLoop`) |
| The frame clock | `Playback.FrameNowMs` (`:64`, default `Environment.TickCount64`), `UnixNowMs` (`:68`); `Design.FrameTime.NowMs` falls back to `Stopwatch` when `FrameClock.PresentQpc == 0` (`Design.cs:1497-1507`) | **Works** unset; only `Prefetching`'s deadline reads `TickCount64` directly (`Playback.Audio.cs:1782`) | set to a `Stopwatch` ms clock for a 1 ms resolution |
| `HostDispatch.Current` (the engine's poster for `MediaPlayer` signals) | `fluent-gpu-base…\MediaPlayer.cs:404-415`, used by `ConnectSignals` at `:368-375`; set only by `AppHost` (`Hosting/AppHost.cs:2844, :5471`) | **Works** null — runs inline | leave null |
| Timers | `Playback.Host.cs:571-585` (1 s), `Playback.Audio.cs:1338-1347` (200 ms), `Spotify.Connect.cs:229-230` (50 ms), `Spotify.Session.cs:319-325` (retry) — all `System.Threading.Timer` | **Works** | none |
| `Diag` | `Foundation/Diag.cs:30-41` falls back to `Console.Error`; `Platform.Host.cs:88` bridges it into `Log` | **Works** | none |
| The audio output | `WasapiPcm.CreateBackend` (`FluentGpu.Windows/Wasapi/WasapiPcm.cs:32-67`); `WasapiAudioDevice` opens `GetDefaultAudioEndpoint(eRender, eConsole)` shared-mode, event-driven, 100 ms buffer, its own `CreateEventW` handle (`WasapiAudioDevice.cs:405-475`), COM MTA initialised by itself (`:405`, an `RPC_E_CHANGED_MODE` on an STA caller is ignored); the endpoint factory runs on the pump chain, a pool thread (`PcmAudioPlayer.cs:189`); it starts `FluentGpu.AudioRT` (MMCSS, `MmcssProAudio.cs:18-36`), `AudioWorker`, `AudioClock` (15 ms, publishes position — `AudioFeedThread.cs:553-566`), `AudioDevice`, `AudioProducer` | **Works with no HWND, no message pump, no AppHost.** A plain console process can create and drive a real WASAPI render client; only the *default* device (no per-endpoint open — `Playback.Audio.cs:165-172` documents it) | none; set `WasapiAudioDevice.FormatSink/DiagSink` (`:62-63`, unwired today) so a device-open failure is visible |
| `FluentApp.WindowHandle` | `Playback.Os.cs:120`; `FluentApp.cs:31,683` — 0 with no window | **Works**: the four OS surfaces stay inactive | ask `PowerSession.KeepAwake` directly for soak runs (`FluentGpu.WindowsApi/Power/PowerSession.cs:277`) |
| `Notify.HostInstall` | `Notify.Host.cs:47-48` "Called once, from `Shell.Run`, AFTER the window exists" | not called headless | none |
| `StringTable` (engine interner) | `Entities.cs:1803` → `StringTable.Intern`, single-writer (`StringTable.cs:17-21`) | **Works** as long as every commit runs on the one loop thread | the loop |
| STA | `App.cs:16` `[STAThread]` | harmless; WASAPI initialises MTA on its own pool thread | keep |

**The engine's own headless AppHost is an alternative, not the recommendation.** `FluentGpu.WindowsApp/Probes/
PostFreezeProbe.cs:42-52` builds a real `AppHost(HeadlessPlatformApp, HeadlessWindow, HeadlessGpuDevice,
HeadlessFontSystem, StringTable, root)` and pumps `host.RunFrame()`; `AppHost.Post` (`AppHost.cs:2943`) would then be
the marshaller and the reactive runtime would flush effects. It costs a root `Component`, a font system and the
engine's frame machinery for a host that renders nothing, and it moves the drain "inside a Batch" that has no
subscribers to coalesce for. A `BlockingCollection<Action>` on the main thread gives the same single-writer guarantee
with zero engine surface, keeps `Wavee.Tests` able to run the same `HeadlessLoop` in-process, and is what §2.3 builds.
If a later probe needs effects (a `UseEffect` in a CORE rule), swap the loop for the `AppHost` variant behind the same
`IHeadlessDispatcher` — §8 Q5.

### 1.3 The 0.2.9 precedent (`_old/`)

`P:` = `_old/Wavee/Program.cs`. 0.2.9 had **eleven true headless probe flags** (`.claude/skills/wavee/probes.md:3-6`;
`_old/Wavee/Diagnostics/CliRun.cs` `ProbeFlags`), dispatched at `P:224-363` before the preflight MessageBox, the
single-instance gate and the harness; each ran synchronously on the `[STAThread]` Main with a CTS timeout, echoed
`WaveeLog` to the console (`P:126` `SetEcho(Console.Out.WriteLine)`) and ended in `ExitCli(code)` (`P:22-27`: flush
the log and the dealer archive, then `Environment.Exit`). The console came from `AttachConsole(-1)` +
re-pointed auto-flushing `Console.Out/Error` (`P:28-40`), called only when `args.Length > 0` (`P:82`); the app was a
WinExe then and is now (`Wavee.csproj:5`), so a bare PowerShell launch never waits — the skill's rule is
`Start-Process … -Wait -NoNewWindow` (`probes.md:16-21`). Argument parsing was ad-hoc `Array.IndexOf(args, "--x")`
(no parser class); `--fake` became `Services.UseRealBackend = Array.IndexOf(args,"--fake") < 0` (`P:440`).

The two closest ancestors and why neither is reusable as-is:
- `--spotify-login` (`P:241-246`; `_old/Wavee/SpotifyLive/SpotifyLiveLogin.cs:26-43`): stored credential first, else
  device-code with the prompt printed through a `ChallengeLogger` (`:173-187`); exit 0 = Premium, 1 = failure, 2 =
  Free; passed `clearStoredOnReject: false` (`:48-133`) — the reason `probes.md:28-30` can say "a rejected stored login
  no longer wipes it". 0.3's `Step` always clears on `AuthRejected` (`Spotify.cs:232-237`) — §2.8 restores the
  protection at the store, not in the fold.
- `--connect-live` (`P:358-363`; `_old/Wavee/SpotifyLive/LiveSessionHost.cs:1303-1361`): `Services.CreateReal`,
  `StartAsync(svc, log, ct, uiPost: null)` with the inline-post fallback (`:130`), `GoLive`, then prints "SMOKE TEST"
  instructions and **sleeps a fixed 90 s and returns 0 whether or not anything played**; playback had to be started
  from another Connect device — there was no local play-by-uri.

The windowed benches (`--perf-bench`, `--startup-bench`, the `WAVEE_*_PROBE` family, `WaveeMemSoak`) are takeover
probes through `FluentApp.DiagnosticRun` (`FluentApp.cs:360-367`): they need `Win32Window` + `D3D12Device`, pump
`host.RunFrame()` themselves, wait on `WaveeShell.ProbeNav` (env-gated, `WaveeShell.cs:281-283`) and always exit 0.
`--lyrics-advance-probe` and `--probe-out` never existed as flags in 0.2.9 — only as `WAVEE_LYRICS_ADVANCE_PROBE` /
`WAVEE_PROBE_OUT` (`WaveeNavProbe.cs:2379-2394`); ch 22 (a) is the first spec of the CLI form. The release script's
gate (`ops/release/wavee-release.ps1:597-608`) is builds + `dotnet test` + Pester — **no app launch of any kind**; the
E2E harness (`ops/release/tests/local-update-e2e.ps1`) launches the *packaged* app by AUMID with no stdio or exit code,
polls `Get-Process`, waits on log regexes (`Wait-LogLine` `:789-825`), and wipes `%LOCALAPPDATA%\Wavee` + the HKCU key
before P1 (`:1574-1583`) — the origin of CLAUDE.md's stray-profile rule. `ops/build/bench-wavee.ps1:40-53` is the only
script that launches Wavee with flags (`& $Exe --fake --perf-bench *>&1 | Tee-Object`, the pipe making PowerShell
wait).

**Reused below:** `ExitCli`'s flush-then-exit, `AttachConsole`, arms-before-gate placement, the `clearStoredOnReject`
intent, `Start-Process -Wait -NoNewWindow`, log-regex waits (as JSON events instead), "exit code = failure count" from
the engine gallery's `--windowsapi-smoke` (`FluentGpu.WindowsApp/Program.cs:123-127`) and the gallery's documented
per-probe code tables (`PostFreezeProbe.cs:33-34`: 0 fixed / 2 reproduced / 3 harness error).

### 1.4 Credentials and the profile

Paths (`Platform.Host.cs:17-21, 42-58`): settings HKCU `Software\Wavee\Wavee`; `%LOCALAPPDATA%\Wavee\store.json`
(the DPAPI credential blob under `spotify.credential`, `Platform.cs:825`; the launch-stable `device.id`, `:828,849-856`);
`%LOCALAPPDATA%\Wavee\logs\wavee-yyyyMMdd.log`; `%LOCALAPPDATA%\Wavee\library.db` (`Store.cs:633-639`, only when
`Store.Use(path)` was called — **nobody calls it yet**); the audio body cache under `%LOCALAPPDATA%\Wavee\cache\audio`
(`Spotify.Audio.Stream.cs:140-193`, `DiskCache.Shared`). On a packaged run every one of these is redirected into the
package's `LocalCache` by the OS, not by a branch (`:42-43`); `LocalFolder`'s getter **creates the directory on read**
(`:49`). An unpackaged headless run therefore writes the literal `%LOCALAPPDATA%\Wavee` — the folder CLAUDE.md says
must not linger while a packaged build is under test. The credential is decoded once at boot
(`Platform.cs:889-905`), scheme-tagged (`dpapi:`), decryptable only by this Windows user on this machine
(`Platform.Host.cs:243-254`), and the on-disk shape is 0.2.9's byte for byte (`Platform.cs:907-908`) — so **a machine
that has run 0.2.9 or 0.2.10 signed in already has a credential 0.3 can resume with, and the GUI's Setup wizard
(Wave 6) is the only planned way to create a new one.** Two more hazards: `Spotify.Login` → `AuthRejected` →
`SessionEffects.ClearCredential` (`Spotify.cs:232-237`) wipes the blob (the handoff's "a rejected stored login wipes
`store.json`", `handoff-20260912e…:71-72`); and a headless run beside the GUI shares `device.id`, so both would publish
the same Connect device.

### 1.5 References that do headless well (web, September 2026)

| Reference | Driving model | Login | Events / status | Exit codes | Taken |
|---|---|---|---|---|---|
| **librespot** ([Options](https://github.com/librespot-org/librespot/wiki/Options), [main.rs](https://raw.githubusercontent.com/librespot-org/librespot/master/src/main.rs), [Events](https://github.com/librespot-org/librespot/wiki/Events), [player.rs docs](https://docs.rs/librespot-playback/latest/librespot_playback/player/struct.Player.html), [examples/play.rs](https://raw.githubusercontent.com/librespot-org/librespot/master/examples/play.rs)) | CLI flags mirrored by `LIBRESPOT_*` env vars; no stdin/IPC — Connect-only control; `--onevent <script>` gets `PLAYER_EVENT`, `TRACK_ID`, `POSITION_MS`, … as env vars | `--password` removed (0.5): `--access-token`, cached `credentials.json` (0600), `--enable-oauth` browser flow, or zeroconf; `Credentials::with_access_token` in the library | `PlayerEvent` channel: `Loading, Playing, Paused, Seeked, EndOfTrack, TimeToPreloadNextTrack, Unavailable, PositionCorrection, TrackChanged, SessionConnected…`; `player.await_end_of_track()` | `exit(1)` for everything | the library shape (session → player → typed event channel → Connect controller as a task); `await_end_of_track` as a wait primitive. **Not** the env-var mirroring or env-var hooks (CLAUDE.md) |
| **spotifyd** ([config](https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/configuration/README.md), [auth](https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/configuration/auth.md), [mpris](https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/advanced/mpris.md), [hooks](https://docs.spotifyd.rs/advanced/hooks.html)) | TOML + every key a flag; daemon by default (`--no-daemon`); MPRIS over D-Bus (`OpenUri spotify:track:…`, custom `TransferPlayback`) | OAuth only since 0.4 (`spotifyd authenticate`); cached under `<cache>/oauth/credentials.json` | `on_song_change_hook` with env vars | 1 on error (0.4.1) | "every setting has a flag; the credential file is never echoed" |
| **go-librespot** ([README](https://raw.githubusercontent.com/devgianlu/go-librespot/master/README.md), [api-spec.yml](https://raw.githubusercontent.com/devgianlu/go-librespot/master/api-spec.yml), [API.md](https://raw.githubusercontent.com/devgianlu/go-librespot/master/API.md), [config_schema.json](https://raw.githubusercontent.com/devgianlu/go-librespot/master/config_schema.json)) | `config.yml`; localhost HTTP: `GET /status`, `POST /player/play {uri, skip_to_uri, paused, position}`, `/pause`, `/resume`, `/seek {position, relative}`, `/next`, `/prev`, `/volume`, `/add_to_queue`, `/player/output {device}` | `credentials.type: zeroconf | interactive | device_auth | spotify_token`; persisted `credentials.json`; lockfile → `os.Exit(1)` on a second instance | WebSocket `/events`: `active, inactive, will_play, playing, not_playing, paused, stopped, seek, volume, metadata {format, codec, bitrate, sample_rate, bit_depth}, shuffle_context, repeat_context, repeat_track` | `log.Fatal` (1) | the `/status` snapshot shape and the event vocabulary (`will_play / playing / paused / seek / stopped / metadata`) — §2.5 |
| **mpv** ([ipc.rst](https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/ipc.rst), [options.rst](https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/options.rst), [mpv.rst exit codes](https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/mpv.rst)) | `--idle=yes`, `--input-ipc-server=\\.\pipe\name` (a Windows named pipe); newline-terminated JSON `{"command":[…],"request_id":N}` → `{"error":"success","data":…,"request_id":N}`; `observe_property` push; `--ao=null`; plain-text lines are input.conf commands | n/a | `{"event":…}` lines; `property-change` | documented: 0 ok, 1 init/unknown option, 2 file could not be played, 3 some could, 4 signal; `quit <code>` lets the controller pick the code | the pipe transport, `request_id` correlation, text-or-JSON on one line, `quit <code>`, a *documented* small code table |
| **sysexits(3)** ([FreeBSD man](https://man.freebsd.org/cgi/man.cgi?query=sysexits&sektion=3)) | — | — | — | `EX_USAGE 64, EX_NOUSER 67, EX_UNAVAILABLE 69, EX_SOFTWARE 70, EX_TEMPFAIL 75, EX_NOPERM 77, EX_CONFIG 78` | the vocabulary for the non-zero codes (§2.5) |
| **TAP 14** ([spec](https://testanything.org/tap-version-14-specification.html)) | per-step `ok N - desc` / `not ok N - desc`, `Bail out!`, YAML detail blocks; "a test program … failed if … the exit code is not 0" | — | — | — | the step-result line shape for `--script` runs (as JSON, one line per step) |

### 1.6 Defects found on the way (not headless work; reported so they are not lost)

1. **Nobody sets the UI marshallers in the GUI.** `Playback.ToUi`, `Spotify.Post`, `Store.Post`, `Palette.Host.Post`
   default to inline and are never assigned to `AppHost.Post` (grep of `src/apps/Wavee`: only `Sidebar.Activate`
   assigns its own `ToUi`, `Sidebar.Host.cs:129`). The moment a dealer push and a pump report coincide, two threads
   fold `Playback.s_state` (C1 broken). Owner I's `Shell.Host.Run` must install them before `FluentAppHarness.Run`
   (`Shell.Host.cs:160`) — §7 lists the exact lines; the headless host installs its own and is the first thing that
   will *notice* the GUI's omission because the two cannot share a default.
2. **The welcome does not set `Api.Market` or call `Entities.Switch`** (`Spotify.Session.cs:210-224` vs the promise
   at `Platform.cs:928-935`, `Spotify.Api.cs:100-107`). Metadata requests go out with an empty market until someone
   does. The headless host does it in `OnOnline` (§3.3) and the GUI needs the same call in one place — `Spotify.Apply`
   is the honest home (a `SessionEffects.Welcome` effect), owner D.
3. **The silent sink never plays** (§1.1) — the Wave-5 `--fake` playing-bar gate cannot pass. Owner H, with the audio
   swap (§7).
4. **`Store.Use(library.db)` is never called** (`Store.cs:633-635` says `App.cs` does). The GUI runs memory-only
   today. Orchestrator, `App.cs`, one line — and the headless arm deliberately keeps it *off* by default (§2.8).
5. **The Connect hello PUT is never sent.** `PublishReason.NewDevice/NewConnection` map at `Playback.Host.cs:434,448`
   but no code produces them; a 0.3 device never announces itself. §2.9 sends it from the headless host; the GUI
   needs it on `Online` too (owner G/F).
6. **`Fetch.Pump()` has no caller** (`Fetch.cs:545-548` expects the host's frame tick). A backoff that expires with
   nothing else happening waits for the next plan. The headless tick calls it (§2.3); `RootHost`'s frame tick (owner I)
   must too.

---

## 2. The design

### 2.1 Where it lives in the 0.3 file map

| File | Role | Owner | What goes there | Lines | Why here |
|---|---|---|---|--:|---|
| `App.cs` | CORE | orchestrator | `Main(string[] args)` returns `int`; the one `Diagnostics.Probe.TryRun(args, out int code)` call between `Platform.Boot()` and `Entities.Boot(...)`; the exit tail | +14 (29 → 43 of 400) | §3.5 of the plan already makes `Main` the composition order; a probe arm is a composition |
| `Screens/Diagnostics.Probe.cs` | SHELL | S (Wave 6) — **the headless arm lands now as an orchestrator-owned first cut, exactly the `Platform.cs` precedent (§5 Wave 1 note)**; S adds `--perf-bench` etc. beside it in Wave 6 | `Probe.TryRun` (the arm table), `AttachConsole`, `HeadlessHost` (boot order, the loop, the readers, the session/playback bridges, the JSON writer's sink) | ≈ 520 of 800 | the plan names this file for every CLI arm (§2 :213); `--login-smoke` was always "a `Diagnostics.Probe` entry" (§5 :1043) |
| `+Screens/Diagnostics.Headless.cs` | **CORE (new named partial, declared now)** | X (this plan's implementer) | `Headless.Command` (grammar + parser), `Headless.Condition` (wait/expect evaluator over a `StatusSnapshot`), `Headless.Script` (the runner as a pure state machine), `Headless.JsonLine` (the writer), `Headless.ExitCode`, `OverlaySettings`, `ProtectedLocalStore`, `MetricsDelta`, `ContextQueue.Build` | ≈ 700 | the plan's own rule: a file over budget by 30 % gets a named partial, named on day one; CORE so every rule is a unit test (D17); `Diagnostics.cs` (S, 600) is `LogView`/`BuildReport` and must not absorb 700 lines of grammar |
| `Platform/Platform.cs` | CORE | S — orchestrator first cut | `Platform.ProfileRoot` (settable before `Boot`), read by `Platform.Host.cs:44-52` | +12 | the profile path is Platform's; `--fake` args are booked here already (`:1005`) |
| `Spotify/Spotify.Audio.Stream.cs` | SHELL | F | `Audio.Stream.Stats` — one public readonly snapshot over the counters that already exist (`:254-266, :496-509, :804-825`) plus three new ones (heads, resolves, cacheHits) | +60 | F's file, nobody in flight on it; the counters are already there, they only lack a door |
| `Playback/Playback.Audio.cs` | SHELL | H — **after the audio swap lands** | `Audio.Metrics` (load→first-audio, seek latency by kind, xruns from the engine's `XrunCount`, gapless joins, decoder throughput), the `OpenSilentAsync` fix (`ConnectSignals`, transport routed to the session, wall-clock pacing, no WASAPI probe on a fake load) | +140 | H's file is being rewritten right now (NVorbis removal); the metrics attach to the new adapter, not the old |
| `Wavee.Tests/HeadlessTests.cs` | tests | X | grammar, conditions, script runner, JSON shape, exit codes, overlay, protected store, metrics delta, context queue | ≈ 450 | one test file per core file (§8 item 2) |
| `ops/headless/*.wh` + `ops/headless/Invoke-WaveeHeadless.ps1` | ops | orchestrator | the six smoke scripts and the wrapper that launches a WinExe and captures stdout + exit code | ≈ 200 | `ops/` is the release tooling's home; `ops/build/bench-wavee.ps1` is the precedent for launching the exe with flags |

No new folder under `src/apps/Wavee/`, no environment variable anywhere, no legacy path: `--login-smoke` is
**replaced** by `--headless --script ops/headless/login-smoke.wh` and the two handoff lines that name it are updated
in the same change (§7).

### 2.2 The boot — `Main` with a headless arm

```
Main(args)
  ├─ GCSettings.LatencyMode = SustainedLowLatency
  ├─ Glyphs.Register()                                  (harmless headless: Theme.IconFont is a string)
  ├─ Platform.ProfileRoot = args["--profile"] ?? default  ← BEFORE Boot: LocalFolder reads it
  ├─ Platform.Boot()                                    settings, locale, log, credential slot, Scope
  ├─ if Diagnostics.Probe.TryRun(args, out code)        ← the arm table; --headless is one row
  │     └─ HeadlessHost.Run(HeadlessOptions)            never returns until quit/EOF/timeout/fault
  │           ├─ Platform.UseSettings(new OverlaySettings(Platform.Settings-backing))   writes never reach HKCU
  │           ├─ Platform.UseCredentialSlot(new ProtectedLocalStore(FileLocalStore, deviceIdSuffix:"headless"), DpapiProtector)
  │           ├─ HeadlessLoop loop = new()               the ONE writer thread (the main thread)
  │           ├─ Playback.ToUi = Spotify.Post = Store.Post = Palette.Host.Post = loop.Post
  │           ├─ Playback.FrameNowMs = loop.NowMs        Stopwatch, 1 ms
  │           ├─ Entities.Boot(Platform.Scope)           Store.Use(null) unless --store: memory-only graph
  │           ├─ Spotify.Boot();  Playback.Boot()        no socket, no device, no HWND → Os bridges stay off
  │           ├─ Spotify.Api.Boot() → Fetch.Register      (already inside Api.Run's lazy boot; called eagerly here)
  │           ├─ WasapiAudioDevice.FormatSink/DiagSink → Log
  │           ├─ if !--no-login: Spotify.Login()         AP + dealer threads; loop watches Spotify.Current.Phase
  │           ├─ readers: stdin (or --pipe) → loop.Post(Command); --script file → Script runner fed by the tick
  │           └─ loop.Run(): drain posts; every 100 ms: Fetch.Pump(), Entities.Publish(), Script.Tick(snapshot)
  │     Platform.Shutdown(); return code
  ├─ Entities.Boot(Platform.Scope) … Shell.Run() … (the GUI path, unchanged)
  └─ Platform.Shutdown(); return 0
```

What is deliberately **not** booted: `Modules.Boot()` (stub; module playables are Wave 6), `Shell.Run()` (window,
engine loop, single-instance gate, `Notify.HostInstall`, `Playback.Os`), `Store.Use(library.db)` (off unless
`--store`, so the live profile's cache is never migrated by an unpackaged run — `probes.md:31-32`'s warning).

### 2.3 The loop — one writer, one clock, one named tick

`HeadlessLoop` is the headless stand-in for `AppHost.Post` + the frame tick. The main thread blocks on a
`BlockingCollection<Action>`; every other thread (AP, dealer, api ×4, `Wavee.AudioFetch`, the pump chain, the three
timers, the stdin reader) reaches CORE state only through `loop.Post`. A 100 ms `System.Threading.Timer` (P10: one
named timer, `HeadlessTick`) posts the tick that calls `Fetch.Pump()`, `Entities.Publish()`, samples a
`StatusSnapshot` and advances the script runner. There is no busy loop and no polling thread: a script that is
`wait`-ing costs ten posts a second.

### 2.4 The command surface

One grammar for three transports: a script file (`--script file.wh`), stdin (the default when no script — an agent
pipes commands in), and a named pipe (`--pipe wavee-headless`, `\\.\pipe\wavee-headless`, one client at a time,
mpv's shape). A line is either a text command or a JSON object `{"cmd":"seek","args":["1:30"],"id":7}`; replies carry
the `id` back (mpv's `request_id`). `#` starts a comment; blank lines are ignored.

| Command | Effect | Verb it binds to |
|---|---|---|
| `play <uri> [from <pos>] [quality <q>]` | track: `Entities.Ref` + `PlayNow(ref, ref.Id, QueueCursor.None)`; album/playlist: `Entities.Ensure(members)` → wait for the edge → `ContextQueue.Build` → `Queue.Replace` → `PlayNow(first, context, CursorOf(first))`; episode as track | `Playback.PlayNow` (`Playback.Host.cs:592`) |
| `pause` · `resume` · `toggle` · `stop` | one post each | `:597-599, :605` |
| `seek <pos>` where `<pos>` is `ms`, `m:ss`, `m:ss.fff`, `+5s`, `-10s` | `SeekTo(ms)`; relative against the snapshot's `Position(now)` | `:600` |
| `next` · `prev` | | `:595-596` |
| `volume <0..1>` | | `:602` |
| `shuffle on|off` · `repeat off|context|track` | | `:603-604` |
| `quality normal|high|veryhigh|lossless` | `Settings.Set(Keys.PlaybackQuality, n)` into the overlay; applies at the next open (`Spotify.Audio.cs:641-647`) | settings |
| `set crossfade <ms>` · `set normalization on|off` · `set cache on|off` · `set metered-cap <q>` | overlay writes of `Keys.CrossfadeMs/CrossfadeEnabled`, `NormalizationEnabled`, `AudioBodyCacheEnabled`, `MeteredQualityCap` | settings |
| `queue <uri>` | `Queue.Enqueue(ref)` | `Queue.cs:361` |
| `prepare` | nothing to do explicitly — the pump prepares the next row 8 s out; the command asserts `Pending`/log shows a prefetch (`expect prefetched`) | — |
| `status` | one `status` JSON line (the go-librespot `/status` shape) | `Playback.Snap()`, `Spotify.Current` |
| `stats [reset|mark]` | the stream + audio counters (`Stream.Stats`, `Audio.Metrics`) as one JSON line; `mark` snapshots so the next `stats` prints deltas | §2.7 |
| `wait <cond> [timeout <ms>]` | blocks the script until `<cond>` holds or the timeout; a timeout fails the step | §2.6 |
| `expect <cond>` | evaluates once, now; false fails the step | §2.6 |
| `sleep <ms>` | | |
| `login` | posts `Spotify.Login()` again (after `--no-login`, or after a fault) | `Spotify.Session.cs:256` |
| `connect on|off` | the optional device announce (§2.9) | |
| `log <text>` | writes an `Info` line under category `headless` (a marker for the log reader) | `Log.Info` |
| `quit [code]` | ends the run with that code (default: the script verdict) | |

Interactive and pipe runs are the same grammar with replies per line; a script is the same grammar with a verdict.

### 2.5 Structured output and exit codes

Every line on stdout is one JSON object with `t` (ms since the host started) and `kind`:

```
{"t":12,"kind":"boot","profile":"C:\\Users\\…\\Wavee","credential":"dpapi","account":"chr***as","store":false,"endpoint":"wasapi"}
{"t":15,"kind":"session","phase":"Resolving"}
{"t":842,"kind":"session","phase":"Online","tier":"Premium","country":"NL"}
{"t":1201,"kind":"reply","id":1,"ok":true,"cmd":"play"}
{"t":1203,"kind":"state","phase":"Loading","track":"spotify:track:4uLU6…","pos":0,"dur":0}
{"t":1490,"kind":"audio.open","fmt":"OggVorbis320","head":81920,"firstAudioMs":287,"requests":4,"gainDb":-7.2}
{"t":1492,"kind":"state","phase":"Playing","track":"spotify:track:4uLU6…","pos":0,"dur":214000,"format":"Ogg 320"}
{"t":11500,"kind":"step","n":3,"ok":true,"cmd":"wait position>=10s","elapsedMs":10008}
{"t":11510,"kind":"seek","from":10008,"to":120000,"kind2":"far","latencyMs":143,"requests":1,"probes":1}
{"t":30011,"kind":"stats","cdn":{"requests":6,"cancelled":0,"peakInFlight":1,"pingMs":19,"kbps":24800,"headBytes":81920,"heads":1,"resolves":1,"cacheHits":0},"ring":{"waits":0,"starves":0},"audio":{"xruns":0,"firstAudioMs":287,"seeks":[…],"gapless":{"exact":1,"degraded":0},"decodeXRealtime":112.4}}
{"t":30020,"kind":"verdict","ok":true,"steps":9,"failed":0,"code":0}
```

`session`, `state`, `audio.open`, `seek`, `gapless`, `underrun`, `end`, `fault` are **events** (pushed);
`reply`, `status`, `stats`, `step`, `verdict` answer a command. Secrets never appear: the account is
`Platform.Redact` (`Platform.cs:920-921`), the credential is its scheme tag, tokens are never read by the host.

Exit codes (the mpv rule "the controller can pick the code", the sysexits vocabulary for the rest):

| Code | Meaning | Set by |
|--:|---|---|
| 0 | every script step passed (or interactive `quit`) | `Script.Verdict` |
| 1 | a step failed by a *fault* (`Playback.Error != None`, a session `Failed`, a device loss) | `Script.Verdict` |
| 2 | a step failed by *assertion* (`expect` false, `wait` timed out) | `Script.Verdict` |
| 3 | the host itself failed (exception in the loop, a reader died) — the gallery's "harness error" | `HeadlessHost` |
| 64 | usage: unknown command line flag, unparsable script line (reported with line number before anything boots) | `Command.Parse` |
| 67 | no stored credential on this profile (`EX_NOUSER`) | `HeadlessHost` before login |
| 69 | the audio endpoint could not open (`Audio.Supported == false`; `EX_UNAVAILABLE`) | `HeadlessHost` at first `play` |
| 75 | login did not reach `Online` inside `--login-timeout` (network, AP, dealer — `EX_TEMPFAIL`) | `HeadlessHost` |
| 77 | the stored credential was rejected (`SessionFault.CredentialRejected`; the blob is *kept*, §2.8) | `HeadlessHost` |
| 78 | a settings/profile problem (`--profile` not writable, `--store` path locked) | `HeadlessHost` |

### 2.6 Conditions (`wait` / `expect`)

A condition is a pure predicate over a `StatusSnapshot` sampled on the loop each tick — the same struct `status`
prints — so the evaluator is a table, not a set of ad-hoc branches, and every row is one xunit fact:

```
playing | paused | loading | idle | ended        phase (ended = Phase.Ended OR the track changed since the wait began)
online | offline | failed                        session phase
buffering | !buffering
position>=10s | position<1:00 | position>=+5s     ms, m:ss or relative to the position when the wait began
track==spotify:track:x | track!=…                the current id's text
format==flac | format==flac24 | format==ogg320   StreamFormat badge folded (the pump's ReportFormat)
next                                             the current track differs from the one at the wait's start
prefetched                                       Audio.Metrics.PrepareArmed for the current epoch
cdn.requests<=4 | cdn.inflight<=1                Stream.Stats deltas since the last `stats mark`
xruns==0 | gapless.exact>=1                      Audio.Metrics
```

Operators `== != >= <= > <`, values as integers, `m:ss(.fff)`, durations with `s`/`ms` suffix, or identifiers; `&&`
joins (no `||` — write two steps). A `wait` carries the snapshot it began with, which is what makes `next`, `ended`
and `position>=+5s` well-defined.

### 2.7 Measurements — what the scripts make verifiable

| Measurement | Source | Where the number is read | Lands with |
|---|---|---|---|
| Time to first audio, head-served vs body-served | `audio.first … ms=` (`Spotify.Audio.Stream.cs:1045`) is the first decodable byte; **load → `ReportStarted`** is the honest number and does not exist yet | `Audio.Metrics.FirstAudioMs` (Stopwatch from `Audio.Load` to the `Started` post at `Playback.Audio.cs:1449`) + `Body.HeadBytes > 0` → `"head"` vs `"body"` | H |
| Seek latency by kind | nothing times `ApplySeekAsync` (`:1316-1334`) | `Audio.Metrics.LastSeek {RequestedMs, LatencyMs, Kind}` with `Kind` = `ring` (0 requests), `far` (≥1 probe), `disk` (`audio.range src=local`) — the kind is *derived from the fetcher/cache deltas across the seek*, never guessed | H (timing) + F (`Stats`) |
| CDN requests per scenario | `Fetcher.Requests / Cancelled / PeakInFlight` (`:254-266`) — already counted | `Stream.Stats` snapshot; `stats mark` + `stats` deltas; `wait cdn.requests<=N` | F (+ `Heads`, `Resolves`, `CacheHits` counters, 3 ints) |
| Underruns | `[audio] xrun` lines (`Playback.Audio.cs:1496-1516`), engine `XrunCount` (`PcmAudioPlayer.cs:485`) | `Audio.Metrics.Xruns` | H |
| Gapless joins | `gapless armed join=… clock=…` (`:1181`), `[gapless] arm … exact=` (`:1399`), `join abandoned` (`:1154, :1225, :1282`) | `Audio.Metrics.Gapless {Exact, Degraded, Abandoned}` | H |
| Decoder throughput | none | `Audio.Metrics.DecodeXRealtime` = frames decoded ÷ decode wall time, sampled in the adapter's read path (the vorbis plan's "≥ 50× realtime" floor, §7.3) | H (with the new adapter) |
| FLAC vs Ogg | `audio.open fmt=` (`:1667`) | the `audio.open` event's `fmt`; the flac and ogg scripts differ by one `quality` line | now (log echo) |
| Session timing | `Log.SinceStartMs` | the `session` events' `t` | now |

Until H's `Metrics` lands, the host reports **host-level** timings (command post → the state event it produced) and
echoes the existing `audio.*` log lines as events, which already carry `head=`, `firstAudioMs` and `requests=` (the
vorbis plan's §8.3 gate lines are written against exactly those). The `stats` line grows fields as the seams land; a
script that asserts a field that is not there fails the step with `"reason":"metric unavailable"` — never silently
passes.

### 2.8 Profile and credential safety (the rules the host enforces for an agent)

1. **Settings writes never reach HKCU.** `OverlaySettings` wraps the real store: reads fall through, writes stay in
   memory. `quality lossless` in a smoke run cannot change the user's rung.
2. **The credential is never removed by a headless run.** `ProtectedLocalStore` wraps `FileLocalStore`: `Get`/`Set`
   pass through (the welcome's refreshed blob is *saved* — `Spotify.Session.cs:220`), `Remove(CredentialKey)` is
   refused and logged (`SessionEffects.ClearCredential` becomes a no-op at the store). 0.2.9's
   `clearStoredOnReject: false`, enforced at the seam rather than by a flag through the fold.
3. **A separate Connect identity.** The same wrapper maps `device.id` reads/writes to `device.id.headless`, so the
   headless device is `Wavee (headless) on <machine>` and never collides with the GUI's device in another client's
   picker (`Platform.cs:847-848`'s "a new id every launch is a phantom device" holds: the headless id is created once
   and kept).
4. **No `library.db` unless asked.** `Store.Use(null)` by default (§2.2); `--store` opts in for a test that needs the
   disk tier.
5. **No stray profile.** `--profile <dir>` redirects `LocalFolder` (and with it `store.json`, logs and the audio cache)
   for a run that must not touch `%LOCALAPPDATA%\Wavee` — e.g. while a packaged build is under test. A `--profile`
   run has no credential unless one was saved there (§2.10); the wrapper script refuses to run without `--profile`
   when `Get-AppxPackage cproducts.Wavee*` reports an installed package, and prints why.
6. **No second GUI instance.** The headless arm skips the `SingleInstanceGate` (it never opens a window), so it can
   run beside the GUI — with its own device id (3) and its own read-only view of settings (1). It shares the DPAPI
   credential (that is the point) and the audio disk cache (also the point: "seek into the disk cache" is a scenario).

### 2.9 Headless Spotify Connect (optional) — **recommend IN, behind `--connect`, default off**

Cost: ~40 lines in the host. The session already reaches `Online` with a connection id; `Playback.Boot` already builds
the `DeviceIdentity` (`Playback.Host.cs:252-258`); `Spotify.Connect.PublishNow(snapshot, PutReason.NewDevice)`
(`Spotify.Connect.cs:236`) is the hello PUT nobody sends (§1.6 item 5); cluster pushes and remote commands already
arrive through `Connect.Wake → ToUi(s_drain)` (`Playback.Host.cs:264, :334-349`). With `--connect` the host names the
device `Wavee (headless)`, sends the hello once `ConnectionId()` is non-empty, and every reducer publish goes out
through the existing `Effects.PublishState` path. What it buys: the plan's DoD item 4 "Connect transfer to and from
another device" (`:1225`) becomes a script (`wait online; connect on; wait owner==foreign timeout 120000` while the
orchestrator transfers from a phone), and the spotifyd use case (a scriptable Connect receiver on a box with no
screen) falls out for free. What it costs: a second device row in every client the account is signed into whenever
the smoke runs — hence default off, and the hello is sent only when asked. Out of scope: zeroconf/discovery (Wavee
has none), MPRIS.

### 2.10 When there is no credential

`Platform.HasStoredCredential()` false at boot → one `fault` line (`{"kind":"fault","reason":"no-credential","hint":
"sign in once in the Wavee window on this machine, or copy a 0.2.x store.json into --profile"}`) and exit 67. There
is no interactive login in the headless arm in this cut: the only credential flows 0.3 will have are the Setup
wizard's (owner R, Wave 6) and they do not exist yet; a device-code prompt on the console (0.2.9's `--spotify-login`,
`SpotifyLiveLogin.cs:173-187`) is the obvious v2 and is §8 Q2. A `--profile` run that needs a credential gets it by a
GUI login *into that profile* (`Wavee.exe --profile <dir>` — the same flag, the GUI path) once R's wizard lands.

---

## 3. The code

Names are final; bodies marked `…` are the parts the implementer fills from the cited 0.3 seams. Everything in §3.1
is BCL-only (no `FluentGpu` `using`), which is what lets `HeadlessTests` drive it.

### 3.1 `+Screens/Diagnostics.Headless.cs` — CORE

```csharp
// ── Screens/Diagnostics.Headless.cs ────────────────────────────────────────────────────────────────────────────────
// the headless command grammar, the wait-condition evaluator, the script runner, the JSON-lines shape, the exit
// codes, the settings overlay, the protected credential store, the counter deltas, the context-queue builder
//
// Role: CORE
// Owner: X
// Wave: now (lands beside Waves 4-5; consumed by Diagnostics.Probe.cs's headless arm)
// Budget: 700 lines
// Spec: docs/plans/wavee/wavee-0.3-headless-implementation.md §2.4-2.8, §3.1
//
// Pure by construction: no engine type, no thread, no clock, no I/O. Every decision below is driven by a StatusSnapshot
// the SHELL samples and hands in, which is why HeadlessTests can replay a whole smoke script against a scripted
// sequence of snapshots and pin the verdict and the exit code.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public static class Headless
    {
        // ── 1. exit codes (§2.5) ────────────────────────────────────────────────────────────────────────────────────
        public static class ExitCode
        {
            public const int Ok = 0, Fault = 1, Assertion = 2, HostError = 3,
                             Usage = 64, NoCredential = 67, NoEndpoint = 69, LoginTimeout = 75, CredentialRejected = 77, Config = 78;
        }

        // ── 2. what a script sees: one flat snapshot per tick ───────────────────────────────────────────────────────
        /// <summary>Everything a condition may read. The SHELL fills it from Playback.Snap(), Spotify.Current,
        /// Stream.Stats and Audio.Metrics ONCE per tick on the loop thread; CORE never reaches for a live static.</summary>
        public readonly record struct StatusSnapshot(
            long NowMs,
            string SessionPhase, string SessionFault, string Tier, string Country,
            string Phase, bool Buffering, string Fault, string TrackUri, int PositionMs, int DurationMs,
            string Format, float Volume, string Owner, uint LoadEpoch, bool PrepareArmed,
            long CdnRequests, int CdnInFlight, int CdnCancelled, long CdnHeads, long CdnResolves, long CdnCacheHits,
            int Xruns, int GaplessExact, int GaplessDegraded, int FirstAudioMs);

        // ── 3. the grammar (§2.4) ───────────────────────────────────────────────────────────────────────────────────
        public enum Verb : byte
        {
            None, Play, Pause, Resume, Toggle, Stop, Seek, Next, Prev, Volume, Shuffle, Repeat, Quality, Set, Queue,
            Prepare, Status, Stats, Wait, Expect, Sleep, Login, Connect, Log, Quit,
        }

        /// <summary>One parsed line. Arguments are kept as the spans the verb needs, already validated: a Seek carries
        /// a resolved position (absolute or relative), a Wait carries a parsed Condition, a Quit carries a code.</summary>
        public readonly record struct Command(Verb Verb, string Text, string Arg0 = "", int Int0 = 0, bool Relative = false,
                                              int TimeoutMs = 0, Condition Cond = default, int Id = -1)
        {
            public bool IsNone => Verb == Verb.None;
        }

        /// <summary>Parse one line — text (`seek 1:30`) or JSON (`{"cmd":"seek","args":["1:30"],"id":7}`). Never
        /// throws: a bad line is (false, reason) so a script reports its line number before anything boots.</summary>
        public static bool TryParse(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = default; error = "";
            line = line.Trim();
            if (line.IsEmpty || line[0] == '#') return true;                         // Verb.None: a comment or blank
            if (line[0] == '{') return TryParseJson(line, out cmd, out error);
            … // split on whitespace; switch on the verb word; ParsePosition / ParseCondition / ParseQuality below
        }

        /// <summary>`ms`, `m:ss`, `m:ss.fff`, `h:mm:ss`, `+5s`, `-10s`, `+1500ms`. Relative forms set the flag; the
        /// SHELL adds them to the snapshot's position at post time.</summary>
        public static bool TryParsePosition(ReadOnlySpan<char> s, out int ms, out bool relative) { … }

        public static bool TryParseQuality(ReadOnlySpan<char> s, out int rung)   // 0 normal · 1 high · 2 veryhigh · 3 lossless — Spotify.Audio.Quality's values
        { … }

        // ── 4. conditions (§2.6) ────────────────────────────────────────────────────────────────────────────────────
        public enum Field : byte
        {
            Phase, Session, Buffering, Position, Track, Format, Next, Prefetched, CdnRequests, CdnInFlight, Xruns, GaplessExact, Owner,
        }
        public enum Op : byte { Eq, Ne, Ge, Le, Gt, Lt, Is, IsNot }

        /// <summary>One clause; `&&` chains up to four (MaxClauses). Value is an int for numeric fields, a string for
        /// identity fields; RelativeToStart marks `position>=+5s`.</summary>
        public readonly record struct Clause(Field Field, Op Op, int Int, string Str, bool RelativeToStart);
        public readonly record struct Condition(Clause C0, Clause C1, Clause C2, Clause C3, byte Count)
        {
            public const int MaxClauses = 4;
            public bool IsEmpty => Count == 0;
        }

        public static bool TryParseCondition(ReadOnlySpan<char> s, out Condition cond, out string error) { … }

        /// <summary>The ONE evaluator. <paramref name="start"/> is the snapshot the wait began with — what makes
        /// `next`, `ended` and relative positions mean something; for `expect` it is the same as <paramref name="now"/>.
        /// Deltas (cdn.*) are against <paramref name="mark"/>, the last `stats mark`.</summary>
        public static bool Holds(in Condition cond, in StatusSnapshot now, in StatusSnapshot start, in StatusSnapshot mark)
        {
            for (int i = 0; i < cond.Count; i++)
            {
                ref readonly Clause c = ref ClauseAt(in cond, i);
                if (!Holds(in c, in now, in start, in mark)) return false;
            }
            return true;
        }

        static bool Holds(in Clause c, in StatusSnapshot now, in StatusSnapshot start, in StatusSnapshot mark) => c.Field switch
        {
            Field.Phase      => c.Str == "ended" ? now.Phase == "Ended" || (start.TrackUri.Length > 0 && now.TrackUri != start.TrackUri)
                                                 : Compare(now.Phase, c),
            Field.Session    => Compare(now.SessionPhase, c),
            Field.Buffering  => (c.Op == Op.Is) == now.Buffering,
            Field.Position   => Compare(now.PositionMs, c.RelativeToStart ? start.PositionMs + c.Int : c.Int, c.Op),
            Field.Track      => Compare(now.TrackUri, c),
            Field.Format     => Compare(FoldFormat(now.Format), c),
            Field.Next       => now.TrackUri.Length > 0 && now.TrackUri != start.TrackUri,
            Field.Prefetched => now.PrepareArmed,
            Field.CdnRequests=> Compare((int)(now.CdnRequests - mark.CdnRequests), c.Int, c.Op),
            Field.CdnInFlight=> Compare(now.CdnInFlight, c.Int, c.Op),
            Field.Xruns      => Compare(now.Xruns - mark.Xruns, c.Int, c.Op),
            Field.GaplessExact => Compare(now.GaplessExact - mark.GaplessExact, c.Int, c.Op),
            Field.Owner      => Compare(now.Owner, c),
            _ => false,
        };

        /// <summary>"Ogg 320" → ogg320, "FLAC 24/44.1" → flac24, "FLAC" → flac, "MP3" → mp3 — the badge the pump
        /// reports (`Playback.Audio.cs` ReportFormat) folded to the script's vocabulary. Pure; pinned by a test.</summary>
        public static string FoldFormat(string badge) { … }

        // ── 5. the script runner — a state machine advanced by the tick (§2.4) ──────────────────────────────────────
        public enum StepState : byte { Pending, Waiting, Sleeping, Done, Failed }

        /// <summary>What the runner asks the SHELL to do this tick. One value per tick, never a list: a script is
        /// sequential by definition.</summary>
        public readonly record struct ScriptAction(Verb Verb, Command Cmd, bool Finished, int ExitCode);

        public sealed class Script
        {
            readonly Command[] _steps;
            int _index;
            StepState _state;
            long _deadlineMs, _sleepUntilMs;
            StatusSnapshot _start, _mark;
            int _failed, _faulted;
            public int StepCount => _steps.Length;
            public int Index => _index;
            public int Failed => _failed;

            public Script(Command[] steps) => _steps = steps;

            /// <summary>Parse a whole file. A parse error anywhere is a usage error BEFORE anything boots (§2.5, 64).</summary>
            public static bool TryLoad(ReadOnlySpan<char> text, out Script script, out int badLine, out string error) { … }

            /// <summary>Advance by one tick. The SHELL executes the returned verb (a Play post, a Status print), then
            /// on the NEXT tick the runner sees the new snapshot. `wait`/`sleep` return Verb.None until satisfied.
            /// A fault in the snapshot (Playback.Fault != None, session Failed) fails the current step with code 1;
            /// a timeout or a false `expect` fails it with code 2. The script stops at the first failure — the
            /// verdict line names the step — unless the step was written `wait? …` / `expect? …` (soft: recorded,
            /// continues), which is how a public-only build's FLAC script records NoDeriver without aborting.</summary>
            public ScriptAction Tick(in StatusSnapshot now)
            {
                if (_index >= _steps.Length) return new(Verb.None, default, Finished: true, Verdict());
                ref readonly Command cmd = ref _steps[_index];
                switch (_state)
                {
                    case StepState.Pending:
                        _start = now;
                        if (cmd.Verb == Verb.Wait)  { _state = StepState.Waiting; _deadlineMs = now.NowMs + (cmd.TimeoutMs > 0 ? cmd.TimeoutMs : DefaultWaitMs); return Check(in now); }
                        if (cmd.Verb == Verb.Sleep) { _state = StepState.Sleeping; _sleepUntilMs = now.NowMs + cmd.Int0; return None; }
                        if (cmd.Verb == Verb.Expect) { Finish(Holds(in cmd.Cond, in now, in now, in _mark) ? StepState.Done : StepState.Failed, ExitCode.Assertion); return None; }
                        if (cmd.Verb == Verb.Stats && cmd.Arg0 == "mark") _mark = now;
                        if (cmd.Verb == Verb.Quit) { _index = _steps.Length; return new(Verb.Quit, cmd, true, cmd.Int0 >= 0 ? cmd.Int0 : Verdict()); }
                        Finish(StepState.Done, 0);
                        return new(cmd.Verb, cmd, false, 0);           // the SHELL executes it this tick
                    case StepState.Waiting:  return Check(in now);
                    case StepState.Sleeping: if (now.NowMs >= _sleepUntilMs) Finish(StepState.Done, 0); return None;
                }
                return None;
            }

            ScriptAction Check(in StatusSnapshot now)
            {
                ref readonly Command cmd = ref _steps[_index];
                if (IsFault(in now))                       { Finish(StepState.Failed, ExitCode.Fault); return None; }
                if (Holds(in cmd.Cond, in now, in _start, in _mark)) { Finish(StepState.Done, 0); return None; }
                if (now.NowMs >= _deadlineMs)               { Finish(StepState.Failed, ExitCode.Assertion); return None; }
                return None;
            }

            static bool IsFault(in StatusSnapshot s) => s.Fault.Length > 0 && s.Fault != "None" || s.SessionPhase == "Failed";

            void Finish(StepState state, int code) { … /* record (index, state, code, elapsed) for the step line; soft steps never stop; advance */ }
            int Verdict() => _faulted > 0 ? ExitCode.Fault : _failed > 0 ? ExitCode.Assertion : ExitCode.Ok;

            public const int DefaultWaitMs = 30_000;
            static readonly ScriptAction None = new(Verb.None, default, false, 0);
        }

        // ── 6. JSON lines (§2.5) — the shape is pinned by a test, the writer never allocates a DOM ───────────────────
        public static class JsonLine
        {
            public static string Boot(long t, string profile, string scheme, string account, bool store, string endpoint) { … }
            public static string Session(long t, string phase, string tier = "", string country = "") { … }
            public static string State(long t, in StatusSnapshot s) { … }
            public static string Reply(long t, int id, bool ok, string cmd, string error = "") { … }
            public static string Step(long t, int n, bool ok, string cmd, long elapsedMs, int code, string reason = "") { … }
            public static string Stats(long t, in StatusSnapshot s, in StatusSnapshot mark) { … }
            public static string Verdict(long t, bool ok, int steps, int failed, int code) { … }
            public static string Fault(long t, string reason, string hint) { … }
            public static string Echo(long t, string category, string line) { … }     // an echoed Log ring entry
        }

        // ── 7. the two store wrappers (§2.8) ───────────────────────────────────────────────────────────────────────
        /// <summary>Reads fall through; writes stay here. A headless `quality lossless` never reaches HKCU.</summary>
        public sealed class OverlaySettings(IAppSettings inner) : IAppSettings
        {
            readonly Dictionary<string, object> _writes = new(StringComparer.Ordinal);
            public T Get<T>(SettingKey<T> key) => _writes.TryGetValue(key.Name, out var v) && v is T t ? t : inner.Get(key);
            public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _writes[key.Name] = value; }
            public int WriteCount => _writes.Count;
        }

        /// <summary>The credential blob may be read and refreshed but never removed; the device id is namespaced so the
        /// headless run is its own Connect device. `Platform.CredentialKey` is the one literal shared (Platform.cs:827).</summary>
        public sealed class ProtectedLocalStore(ILocalStore inner, string deviceIdSuffix, Action<string>? refused = null) : ILocalStore
        {
            const string DeviceIdKey = "device.id";
            string Map(string key) => key == DeviceIdKey ? DeviceIdKey + "." + deviceIdSuffix : key;
            public string? Get(string key) => inner.Get(Map(key));
            public void Set(string key, string value) => inner.Set(Map(key), value);
            public void Remove(string key)
            {
                if (key == Platform.CredentialKey) { refused?.Invoke(key); return; }   // never on a headless run (§2.8 rule 2)
                inner.Remove(Map(key));
            }
        }

        // ── 8. the context queue (§2.4 `play <album|playlist>`) ─────────────────────────────────────────────────────
        /// <summary>Rows in reading order for Queue.Replace: the chosen row as NowPlaying, the rest as NextUp — the
        /// bucket invariant Queue.Replace asserts (Queue.cs:335-338). Pure over spans; the SHELL fills `members`
        /// from the album/playlist edge once Entities says the edge is known. Owner Q's Wave-5 page will need this
        /// same fold and should take it into Queue.cs then; it is here so the smoke does not wait for Wave 5.</summary>
        public static int BuildContextQueue(ReadOnlySpan<EntityRef> members, int startIndex, Span<EntityRef> refs, Span<QueueEdge> rows)
        {
            int n = 0;
            for (int i = startIndex; i < members.Length; i++)
            {
                refs[n] = members[i];
                rows[n] = new QueueEdge(0, (byte)QueueProvider.Context, (byte)(i == startIndex ? QueueBucket.NowPlaying : QueueBucket.NextUp));
                n++;
            }
            return n;
        }
    }
}
```

`QueueProvider.Context` / `QueueBucket.NowPlaying/NextUp` are the member names to confirm against `Queue.cs:66-95`
(the enums are "ported member-for-member from 0.2.9's `QueueBucket`"); `QueueEdge(itemId, provider, bucket)` is the
constructor `Queue.Enqueue` uses (`Queue.cs:365`).

### 3.2 `App.cs` — the arm

```csharp
public static class App
{
    [STAThread]
    static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Glyphs.Register();
        Platform.ProfileRoot = Diagnostics.Probe.ProfileArg(args);   // "" = the default %LOCALAPPDATA%\Wavee; read by LocalFolder
        Platform.Boot();
        if (Diagnostics.Probe.TryRun(args, out int code))              // --headless (now); --perf-bench … (owner S, Wave 6)
        {
            Platform.Shutdown();
            return code;
        }
        Entities.Boot(Platform.Scope);
        Spotify.Boot();
        Playback.Boot();
        Modules.Boot();
        Shell.Run();
        Platform.Shutdown();
        return 0;
    }
}
```

### 3.3 `Screens/Diagnostics.Probe.cs` — the SHELL host (first cut)

```csharp
// Role: SHELL · Owner: S (Wave 6) — headless arm: orchestrator-owned first cut, the Platform.cs precedent
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FluentGpu.Windows.Wasapi;

namespace Wavee;

public static partial class Diagnostics
{
    public static class Probe
    {
        /// <summary>The arm table. First match runs; the rest of Main never does. Every arm attaches the parent
        /// console first (a WinExe has none) — 0.2.9's Program.cs:28-40, verbatim in spirit.</summary>
        public static bool TryRun(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--headless") < 0) return false;
            AttachParentConsole();
            if (!HeadlessOptions.TryParse(args, out var options, out string usage)) { Console.Error.WriteLine(usage); code = Headless.ExitCode.Usage; return true; }
            code = HeadlessHost.Run(options);
            return true;
        }

        public static string ProfileArg(string[] args) { int i = Array.IndexOf(args, "--profile"); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(int pid);
        static void AttachParentConsole()
        {
            if (!OperatingSystem.IsWindows() || !AttachConsole(-1)) return;      // -1 = ATTACH_PARENT_PROCESS; false = already redirected
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    /// <summary>`--headless [--script f] [--pipe name] [--profile dir] [--store] [--silent] [--no-login] [--connect]
    /// [--login-timeout ms] [--timeout ms] [--echo-log]`. Pure parse; tested.</summary>
    public readonly record struct HeadlessOptions(string Script, string Pipe, bool Store, bool Silent, bool NoLogin, bool Connect,
                                                  int LoginTimeoutMs, int RunTimeoutMs, bool EchoLog)
    {
        public static bool TryParse(string[] args, out HeadlessOptions o, out string usage) { … }
    }

    /// <summary>The one writer thread. `Post` from anywhere; `Run` on the main thread until `Stop`.</summary>
    public sealed class HeadlessLoop
    {
        readonly BlockingCollection<Action> _posts = new(4096);            // bounded (C8); a full queue blocks the poster, never drops
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public long NowMs => _clock.ElapsedMilliseconds;
        public void Post(Action a) => _posts.Add(a);
        public void Stop() => _posts.CompleteAdding();
        public void Run() { foreach (Action a in _posts.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Log.Error("headless", "post faulted", ex); throw; } } }
    }

    public static class HeadlessHost
    {
        static HeadlessLoop s_loop = null!;
        static Headless.Script? s_script;
        static Headless.StatusSnapshot s_mark, s_last;
        static int s_exit = Headless.ExitCode.Ok;
        static Timer? s_tick;
        static long s_logSeen;

        public static int Run(in HeadlessOptions o)
        {
            // 1. the stores (§2.8) — before anything reads them
            Platform.UseSettings(new Headless.OverlaySettings(Platform.Settings));   // Platform.Settings is the facade; the overlay sits in front of the backing store
            Platform.UseCredentialSlot(new Headless.ProtectedLocalStore(new FileLocalStore(Platform.StorePath), "headless",
                                       refused: k => Log.Warn("headless", "refused to remove " + k)), new DpapiProtector());
            if (!o.NoLogin && !Platform.HasStoredCredential())
            {
                Emit(Headless.JsonLine.Fault(0, "no-credential", "sign in once in the Wavee window on this machine, or copy a 0.2.x store.json into --profile"));
                return Headless.ExitCode.NoCredential;
            }

            // 2. the loop and the marshallers
            s_loop = new HeadlessLoop();
            Playback.ToUi = s_loop.Post; Spotify.Post = s_loop.Post; Store.Post = s_loop.Post; Palette.Host.Post = s_loop.Post;
            Playback.FrameNowMs = () => s_loop.NowMs;
            WasapiAudioDevice.DiagSink = static s => Log.Warn("audio", s);
            WasapiAudioDevice.FormatSink = static f => Log.Info("audio", "device format " + f);

            // 3. boot (no Shell, no Modules, no Store unless asked)
            if (o.Store) Store.Use(Path.Combine(Platform.LocalFolder, "library.db"));
            Entities.Boot(Platform.Scope);
            Spotify.Boot();
            Playback.Boot();
            Spotify.Api.Boot();                                       // Fetch.Register(Transport) — Spotify.Api.cs:119
            if (o.Silent) Playback.Audio.UseSilentEndpoint();         // H's seam (§3.5); until it lands --silent is refused with 69
            Emit(Headless.JsonLine.Boot(s_loop.NowMs, Platform.LocalFolder, Platform.CredentialScheme, Platform.Redact(Platform.Scope.Account), o.Store, o.Silent ? "silent" : "wasapi"));

            // 4. the script and the readers
            if (o.Script.Length > 0)
            {
                if (!Headless.Script.TryLoad(File.ReadAllText(o.Script), out s_script, out int line, out string err))
                { Console.Error.WriteLine($"{o.Script}({line}): {err}"); return Headless.ExitCode.Usage; }
            }
            else StartReader(o.Pipe);                                // stdin, or the named pipe — a background thread posting Commands

            // 5. login, then run
            if (!o.NoLogin) { Spotify.Login(); s_loginDeadline = s_loop.NowMs + o.LoginTimeoutMs; }
            s_tick = new Timer(static _ => s_loop.Post(s_tickAction), null, 100, 100);   // HeadlessTick, the one named timer
            s_runDeadline = o.RunTimeoutMs > 0 ? s_loop.NowMs + o.RunTimeoutMs : long.MaxValue;
            try { s_loop.Run(); } catch { s_exit = Headless.ExitCode.HostError; }
            finally { s_tick.Dispose(); Playback.Stop(); Spotify.Logout_NoClear(); /* see §8 Q3: today Logout also clears; the store wrapper refuses it */ }
            Emit(Headless.JsonLine.Verdict(s_loop.NowMs, s_exit == 0, s_script?.StepCount ?? 0, s_script?.Failed ?? 0, s_exit));
            return s_exit;
        }

        static readonly Action s_tickAction = Tick;
        static long s_loginDeadline = long.MaxValue, s_runDeadline;
        static bool s_wasOnline;

        /// <summary>Loop thread, every 100 ms: the host's frame tick (Fetch.Pump + Entities.Publish), the session
        /// watch, the snapshot, the script step, the log echo.</summary>
        static void Tick()
        {
            Fetch.Pump();
            Entities.Publish();
            var session = Spotify.Current;
            if (session.IsOnline && !s_wasOnline) OnOnline(in session);
            if (!s_wasOnline && session.Phase == Spotify.SessionPhase.Failed) { s_exit = session.Fault == Spotify.SessionFault.CredentialRejected ? Headless.ExitCode.CredentialRejected : Headless.ExitCode.LoginTimeout; s_loop.Stop(); return; }
            if (!s_wasOnline && s_loop.NowMs > s_loginDeadline) { s_exit = Headless.ExitCode.LoginTimeout; s_loop.Stop(); return; }
            if (s_loop.NowMs > s_runDeadline) { s_exit = Headless.ExitCode.Assertion; s_loop.Stop(); return; }

            var now = Snapshot();
            EmitDiffs(in s_last, in now);                             // state/session/seek/gapless events, only on change
            s_last = now;
            if (s_script is { } script)
            {
                var action = script.Tick(in now);
                if (action.Verb != Headless.Verb.None) Execute(in action.Cmd, in now, id: -1);
                if (action.Finished) { s_exit = action.ExitCode; s_loop.Stop(); }
            }
            EchoLog();
        }

        /// <summary>What the GUI also owes and does not do yet (§1.6 item 2): the market from the welcome, the scope
        /// switch, the optional Connect hello.</summary>
        static void OnOnline(in Spotify.Session s)
        {
            s_wasOnline = true;
            string country = Entities.Strings.Resolve(s.Country);
            Spotify.Api.Market = country;
            Entities.Switch(new CatalogScope("spotify", Platform.Scope.Account, Platform.Locale.UiCulture, country, (int)s.Tier, AllowExplicit: true));
            Emit(Headless.JsonLine.Session(s_loop.NowMs, "Online", s.Tier.ToString(), country));
            if (s_connect) Spotify.Connect.PublishNow(Playback.SnapshotForConnect(), Spotify.Connect.PutReason.NewDevice);
        }

        static Headless.StatusSnapshot Snapshot()
        {
            var p = Playback.Snap();
            var s = Spotify.Current;
            var st = Spotify.Audio.Stream.Stats.Read();               // F (§3.4); default(Stats) until it lands
            var m = Playback.Audio.Metrics.Read();                    // H (§3.5); default(Metrics) until it lands
            return new(s_loop.NowMs,
                s.Phase.ToString(), s.Fault.ToString(), s.Tier.ToString(), Entities.Strings.Resolve(s.Country),
                p.Phase.ToString(), p.Buffering, p.Error.ToString(), p.CurrentId.IsNone ? "" : p.CurrentId.Text, p.Position(s_loop.NowMs), p.DurationMs,
                p.StreamFormat.IsEmpty ? "" : Entities.Strings.Resolve(p.StreamFormat), p.Volume, p.Owner.ToString(), p.LoadEpoch, m.PrepareArmed,
                st.Requests, st.InFlight, st.Cancelled, st.Heads, st.Resolves, st.CacheHits,
                m.Xruns, m.GaplessExact, m.GaplessDegraded, m.FirstAudioMs);
        }

        /// <summary>A command from any transport. Loop thread.</summary>
        static void Execute(in Headless.Command c, in Headless.StatusSnapshot now, int id)
        {
            bool ok = true; string err = "";
            switch (c.Verb)
            {
                case Headless.Verb.Play:    ok = Play(c.Arg0, c.Int0, out err); break;
                case Headless.Verb.Pause:   Playback.Pause(); break;
                case Headless.Verb.Resume:  Playback.Resume(); break;
                case Headless.Verb.Toggle:  Playback.TogglePlay(); break;
                case Headless.Verb.Stop:    Playback.Stop(); break;
                case Headless.Verb.Seek:    Playback.SeekTo(c.Relative ? Math.Max(0, now.PositionMs + c.Int0) : c.Int0); s_seekPostedAt = now.NowMs; break;
                case Headless.Verb.Next:    Playback.Next(); break;
                case Headless.Verb.Prev:    Playback.Previous(); break;
                case Headless.Verb.Volume:  Playback.SetVolume(c.Int0 / 1000f); break;
                case Headless.Verb.Quality: Platform.Settings.Set(Platform.Keys.PlaybackQuality, c.Int0); break;   // the overlay
                case Headless.Verb.Set:     ok = ApplySetting(c.Arg0, c.Int0, out err); break;
                case Headless.Verb.Queue:   ok = Enqueue(c.Arg0, out err); break;
                case Headless.Verb.Status:  Emit(Headless.JsonLine.State(now.NowMs, in now)); break;
                case Headless.Verb.Stats:   if (c.Arg0 == "mark") s_mark = now; Emit(Headless.JsonLine.Stats(now.NowMs, in now, in s_mark)); break;
                case Headless.Verb.Login:   Spotify.Login(); break;
                case Headless.Verb.Connect: s_connect = c.Int0 != 0; if (s_connect && s_wasOnline) Spotify.Connect.PublishNow(Playback.SnapshotForConnect(), Spotify.Connect.PutReason.NewDevice); break;
                case Headless.Verb.Log:     Log.Info("headless", c.Arg0); break;
                case Headless.Verb.Quit:    s_exit = c.Int0 >= 0 ? c.Int0 : s_exit; s_loop.Stop(); break;
            }
            if (id >= 0) Emit(Headless.JsonLine.Reply(now.NowMs, id, ok, c.Text, err));
        }

        /// <summary>`play`: a track posts at once; a container asks Entities for its members and posts when the edge
        /// lands (the same tick loop notices `Knows`). Every id becomes a handle through the one factory (D10).</summary>
        static bool Play(string uri, int fromMs, out string error)
        {
            error = "";
            if (!EntityId.TryParse(uri.AsSpan(), out EntityId id) || id.Kind == EntityKind.Unknown) { error = "not a spotify uri"; return false; }
            EntityRef row = Entities.Ref(id);
            if (row.IsNone) { error = "no table for kind " + id.Kind; return false; }
            if (!id.IsContainer) { s_playPostedAt = s_loop.NowMs; Playback.PlayNow(row, id, QueueCursor.None, PlayableKind.Audio, fromMs); return true; }
            s_pendingContext = (row, id, fromMs);                       // resolved by TryStartContext on a later tick
            Entities.Ensure(Entities.TableFor(id.Kind)!, [row.Slot], MembersGroup(id.Kind), FetchPriority.Playback);
            return true;
        }

        static void TryStartContext()                                  // called from Tick while s_pendingContext is set
        {
            … // if !Knows(members) return; read the edge span (Edges.AlbumTracks / PlaylistMembers → EntityRef[]),
              // n = Headless.BuildContextQueue(members, 0, refs, rows); Queue.Replace(refs[..n], rows[..n]);
              // Playback.PlayNow(refs[0], context, Queue.CursorOf(Queue.NowPlayingIndex), Audio, fromMs)
        }

        static void StartReader(string pipe)
        {
            var t = new Thread(() =>
            {
                using TextReader r = pipe.Length == 0 ? Console.In : OpenPipe(pipe);
                string? line;
                while ((line = r.ReadLine()) is not null)
                {
                    if (!Headless.TryParse(line, out var cmd, out string err)) { string l = line; s_loop.Post(() => Emit(Headless.JsonLine.Reply(s_loop.NowMs, cmd.Id, false, l, err))); continue; }
                    if (cmd.IsNone) continue;
                    s_loop.Post(() => Execute(in cmd, in s_last, cmd.Id));
                }
                s_loop.Post(static () => s_loop.Stop());               // EOF ends an interactive run with the current verdict
            }) { IsBackground = true, Name = "wavee-headless-reader" };
            t.Start();
        }

        static TextReader OpenPipe(string name)
        {
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
            server.WaitForConnection();
            s_pipeOut = new StreamWriter(server) { AutoFlush = true };  // replies and events go back down the pipe as well as stdout
            return new StreamReader(server);
        }

        /// <summary>Echo new ring entries (Info+) as `echo` events when --echo-log: the `audio.*` timeline lines are
        /// the measurements until the counter seams land (§2.7).</summary>
        static void EchoLog()
        {
            if (!s_echoLog || Log.Version == s_logSeen) return;
            s_logSeen = Log.Version;
            foreach (var e in Log.Snapshot()) if (e.Sequence > s_lastEchoedSeq) { s_lastEchoedSeq = e.Sequence; if (e.Category is "audio" or "spotify" or "playback" or "connect") Emit(Headless.JsonLine.Echo(s_loop.NowMs, e.Category, e.Format())); }
        }

        static void Emit(string line) { Console.Out.WriteLine(line); s_pipeOut?.WriteLine(line); }
        …
    }
}
```

Three things in that listing are requests on other owners' files, all small and all named in §7: `Playback.
SnapshotForConnect()` (G — the `Playback.Snapshot` the reducer already builds for `PublishState`, exposed),
`Spotify.Api.Boot()` public (F — it is the lazy boot `Api.Run` already performs, `Spotify.Api.cs:119-127`), and
`Spotify.Logout_NoClear` (D — or, simpler, the host just lets `ProtectedLocalStore` refuse the removal and calls the
existing `Logout()`; §8 Q3).

### 3.4 `Spotify/Spotify.Audio.Stream.cs` — `Stats` (owner F, +60)

```csharp
public static partial class Spotify { public static partial class Audio { public static partial class Stream {
    /// <summary>The counters the fetcher, the ring and the body already keep (:254-266, :496-509, :804-825), read as
    /// one value from any thread. Three are NEW and belong to OpenBody: Heads (clear-head GETs), Resolves
    /// (storage-resolve calls) and CacheHits (chunk 0 or a probe range answered by the disk cache). Volatile reads;
    /// no lock; a torn pair is a diagnostic, never a decision.</summary>
    public readonly record struct Stats(long Requests, int Cancelled, int InFlight, int PeakInFlight, int PingMs, long BytesPerSecond,
                                        long Heads, long Resolves, long CacheHits, int RingWaits, int RingStarves, int HeadBytes, bool SpliceProof)
    {
        public static Stats Read() => new(Fetcher.Requests, Fetcher.Cancelled, Fetcher.Outstanding, Fetcher.PeakInFlight, Fetcher.PingMs, Fetcher.BytesPerSecond,
                                          Volatile.Read(ref s_heads), Volatile.Read(ref s_resolves), Volatile.Read(ref s_cacheHits),
                                          s_ring?.Waits ?? 0, s_ring?.Starves ?? 0, s_body?.HeadBytes ?? 0, s_body?.SpliceProof ?? false);
    }
    static long s_heads, s_resolves, s_cacheHits;   // ++ at the three sites in OpenBody (:1156-1227) and the cache-serve branch
}}}
```

### 3.5 `Playback/Playback.Audio.cs` — `Metrics` and the silent fix (owner H, after the audio swap, +140)

```csharp
public static partial class Playback { public static partial class Audio {
    /// <summary>What the headless `stats` line and the vorbis/flac gates read. Written on the pump chain, read
    /// anywhere as a value.</summary>
    public readonly record struct Metrics(int FirstAudioMs, bool FirstAudioFromHead, int LastSeekMs, int LastSeekLatencyMs, byte LastSeekKind /* 0 ring · 1 far · 2 disk */,
                                          int Xruns, int GaplessExact, int GaplessDegraded, int GaplessAbandoned, float DecodeXRealtime, bool PrepareArmed)
    { public static Metrics Read() => s_metrics; }
    static Metrics s_metrics;

    // Load:   s_loadStartedMs = FrameNowMs()              (in Audio.Load, before the chain enqueue)
    // Started: FirstAudioMs = FrameNowMs() - s_loadStartedMs; FirstAudioFromHead = opened.Body.HeadBytes > 0   (beside the ReportStarted post, :1449)
    // Seek:   stopwatch around ApplySeekAsync (:1316-1334); LastSeekKind from (Stream.Stats before/after): 0 requests → ring;
    //         requests>0 and the first audio.range was src=local → disk; else far
    // Xruns:  the engine's XrunCount (PcmAudioPlayer.cs:485) read at the 200 ms tick
    // Gapless: the three arms that already log at :1181/:1399/:1154
    // DecodeXRealtime: frames decoded ÷ Stopwatch ticks spent in the adapter's Read, from the new Vorbis/FLAC adapters
    // PrepareArmed: true from Prepare's completion until the epoch changes

    /// <summary>--silent (and --fake): the engine's headless endpoint, CONNECTED and PACED. The three defects §1.1
    /// names: ConnectSignals so the feeder starts; transport routed to the session (not s_player) while silent; a
    /// wall-clock pace so a 3-minute fake track ends in three minutes; and no WASAPI probe in Boot for a silent run.</summary>
    public static void UseSilentEndpoint() { s_forceSilent = true; }
    // OpenSilentAsync: session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore())); … pace = a 20 ms timer feeding
    // SampleRate/50 frames per tick into a BufferedAudioEndpoint (AudioClock.cs / BufferedAudioEndpoint.cs:74 AdvanceHardware)
    // — or the engine grows a `HeadlessAudioEndpoint(realtime: true)`; §8 Q4
}}
```

### 3.6 `Platform/Platform.cs` + `Platform.Host.cs` — the profile root (+12)

```csharp
public static partial class Platform
{
    /// <summary>Where the profile lives. "" = %LOCALAPPDATA%\Wavee (packaged: the LocalCache, by OS redirection).
    /// Set BEFORE Boot and never after — every path (store.json, logs, library.db, cache/) hangs off it. The GUI
    /// honours the same `--profile` flag, which is how a --profile headless run gets a credential (§2.10).</summary>
    public static string ProfileRoot { get; set; } = "";
}
// Platform.Host.cs:44-52  LocalFolder: string dir = ProfileRoot.Length > 0 ? ProfileRoot : Path.Combine(GetFolderPath(LocalApplicationData), Publisher);
```

---

## 4. The scripts, the gates, the agent workflow

### 4.1 `ops/headless/*.wh` (the smoke set)

```
# login-smoke.wh — the Wave 2 gate (plan §5 :1043), rewritten
wait online timeout 60000
play spotify:album:4aawyAB9vmqN3uQ7FjRGTy       # any album: exercises Ensure → Api → Decode → Commit → Queue.Replace
wait playing timeout 20000
expect track!=
status
quit
```
```
# ogg320.wh — the Wave 3 gate (plan §5 :1055) + vorbis plan §8.3's four lines
quality veryhigh
stats mark
play spotify:track:<T1>
wait playing timeout 20000
expect cdn.requests<=4                          # cold start = head ‖ resolve ‖ key ; range 1 ‖ tail (vorbis §5.4)
wait position>=10s timeout 15000
stats mark
seek 2:00                                       # far, first time in the region
wait position>=2:00 timeout 5000
expect cdn.requests<=2                          # ≤ 2 typical (§4.5 row 1)
stats mark
seek +5s                                        # inside the ring
wait position>=+4s timeout 3000
expect cdn.requests==0
stats mark
seek 0:30                                       # back into what has played: the index, 0 probes
wait position<0:40 timeout 5000
expect cdn.requests<=1
expect xruns==0
stats
quit
```
```
# flac.wh — flac plan §8.3's line (soft on a public-only build)
quality lossless
play spotify:track:<T_LOSSLESS>
wait playing timeout 25000
expect? format==flac24                          # soft: a public-only build logs Fault.NoDeriver and plays 320
seek 2:00
wait position>=2:00 timeout 5000
wait position>=2:10 timeout 15000
expect xruns==0
stats
quit
```
```
# gapless-album.wh — a gapless join across an album boundary (the [gapless] arm line, exact=1)
set crossfade 0
play spotify:album:<A_GAPLESS> 
wait playing timeout 20000
seek -12s                                       # relative to the END is not a form; the SHELL resolves `end-12s` — §8 Q7
wait next timeout 30000
expect gapless.exact>=1
expect xruns==0
quit
```
```
# prefetch-next.wh — the next-track prefetch: no cold start at the boundary
play spotify:album:<A>
wait playing timeout 20000
seek end-10s
wait prefetched timeout 9000
stats mark
wait next timeout 15000
wait playing timeout 2000
expect cdn.requests<=2                          # the range that was still due, never the head/resolve/key trio
quit
```
```
# far-seek-disk.wh — a second run of ogg320.wh's track: the disk cache answers the probe (0 requests)
play spotify:track:<T1>
wait playing timeout 20000
stats mark
seek 3:00
wait position>=3:00 timeout 5000
expect cdn.requests==0
quit
```

### 4.2 `ops/headless/Invoke-WaveeHeadless.ps1`

Launches the WinExe the way `probes.md:16-21` says a WinExe must be launched, captures stdout to a `.jsonl` next to
the script, prints the `verdict` line, and returns the exit code; refuses to run without `-Profile` when a
`cproducts.Wavee*` package is installed (§2.8 rule 5); accepts `-Silent`, `-Store`, `-Connect`, `-TimeoutSec`.

```powershell
param([Parameter(Mandatory)][string]$Script, [string]$Exe = "src\apps\Wavee\bin\Debug\net10.0\Wavee.exe",
      [string]$Profile, [switch]$Silent, [switch]$Store, [switch]$Connect, [int]$TimeoutSec = 300, [switch]$EchoLog)
if (-not $Profile -and (Get-AppxPackage -Name 'cproducts.Wavee*')) { throw "a packaged Wavee is installed: pass -Profile so this run never writes %LOCALAPPDATA%\Wavee (CLAUDE.md)" }
$out = [IO.Path]::ChangeExtension($Script, ".jsonl")
$argList = @('--headless', '--script', $Script, '--timeout', ($TimeoutSec * 1000))
if ($Profile) { $argList += @('--profile', $Profile) }; if ($Silent) { $argList += '--silent' }; if ($Store) { $argList += '--store' }
if ($Connect) { $argList += '--connect' }; if ($EchoLog) { $argList += '--echo-log' }
$p = Start-Process -FilePath $Exe -ArgumentList $argList -Wait -NoNewWindow -PassThru -RedirectStandardOutput $out
Get-Content $out | Where-Object { $_ -match '"kind":"(step|verdict|fault)"' }
exit $p.ExitCode
```

### 4.3 The gates, rewritten

| Gate | Before | Now |
|---|---|---|
| Wave 2 (plan §5 :1043) | "`dotnet run -- --login-smoke`, a `Diagnostics.Probe` entry … decodes one `getAlbum` into a Staging with the expected row counts" | `Invoke-WaveeHeadless.ps1 ops/headless/login-smoke.wh` exits 0; the `state` event shows the album's first track playing — a decoded album that produced a queue *is* the row-count assertion, made audible |
| Wave 3 (:1055) | "the login smoke plays 10 s of a track through the real pump" | `ogg320.wh` exits 0 (10 s of position over the real device, the three seek rows, `xruns==0`) |
| vorbis §8.3 (:2494-2497) | four log lines read by eye | `ogg320.wh`'s `expect cdn.requests` rows + `stats`; the scrub rule (≤ 1 in flight) is `expect cdn.inflight<=1` inside a ten-seek loop script (`scrub.wh`, v2) |
| flac §8.3 (:2006-2009) | "the login smoke plays 10 s at Lossless … `Fault.NoDeriver` on a public-only build" | `flac.wh` exits 0 on a deriver build; on a public-only build the soft `expect?` records `format==ogg320` and the `echo` of `audio.key … NoDeriver`, and the script still exits 0 by design |
| DoD 4 (:1225) "Connect transfer to and from another device" | manual | `connect.wh` (`connect on; wait owner==Foreign timeout 120000; wait owner==Us timeout 120000`) with the orchestrator on a phone — the one script that needs a human |

Where they run: **the orchestrator only**, never an implementer agent, never from a shell against the live profile
without `-Profile` while a packaged build is installed; a device is fine (the smoke is 30 s of audio at the saved
volume — `volume 0.2` is the first line of every script in the set). The release script (`wavee-release.ps1:597-608`)
does not gain an app launch: the headless smoke is a wave gate and a pre-release rehearsal step in
`docs/guide/releasing-wavee.md`'s manual list, not an automated release gate, because it needs a signed-in profile
and network (§8 Q1).

### 4.4 How an agent uses it (interactive)

```
$p = Start-Process Wavee.exe -ArgumentList '--headless','--pipe','wavee-hl' -NoNewWindow -PassThru
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'wavee-hl'); $pipe.Connect(5000)
$w = [IO.StreamWriter]::new($pipe); $w.AutoFlush = $true; $r = [IO.StreamReader]::new($pipe)
$w.WriteLine('{"cmd":"play","args":["spotify:track:…"],"id":1}'); … read lines until {"kind":"reply","id":1}
$w.WriteLine('quit')
```
The pipe carries the same events stdout does, so an agent that drives a scenario by hand still gets `audio.open`,
`seek` and `stats` lines to reason over — and never a token, a key or a credential.

---

## 5. Tests (`Wavee.Tests/HeadlessTests.cs`, xunit, no network, no engine loop)

- **Grammar:** every verb round-trips; `seek 1:30.250` → 90250; `seek +5s` → relative 5000; `seek -10s`; `quality
  lossless` → 3; unknown verb → `(false, "unknown command 'foo'")`; JSON form with `id`; comments and blanks → `None`.
- **Conditions:** each `Field` row with each `Op` it accepts; `position>=+5s` against a start snapshot; `ended` via
  `Phase.Ended` and via track change; `cdn.requests<=N` as a delta against `mark`; `&&` with four clauses, a fifth
  refused; `format==flac24` over `FoldFormat("FLAC 24/44.1")`.
- **Script runner (the real decision):** a scripted sequence of snapshots drives `ogg320.wh` verbatim to `Ok`; a
  snapshot with `Fault="Network"` during a `wait` yields code 1; a never-satisfied `wait` yields 2 at the deadline; a
  soft `expect?` false continues and the verdict is still 0; `quit 7` exits 7; `Tick` returns exactly one action per
  tick and never two.
- **JSON shape:** each `JsonLine.*` parses with `JsonDocument` and carries the documented keys; `Boot` never contains
  the account unredacted (`Platform.Redact` is applied — `PlatformTests` already pins `Redact`).
- **OverlaySettings:** a write is visible to `Get` and absent from the inner `IAppSettings` (a recording fake).
- **ProtectedLocalStore:** `Remove(CredentialKey)` is refused and reported; `Remove("other")` passes; `device.id`
  maps to `device.id.headless` both ways; `Set(CredentialKey)` passes (the welcome refresh).
- **BuildContextQueue:** bucket order NowPlaying then NextUp from `startIndex`; `Queue.IsOrdered` accepts the rows
  (`Queue.Replace` asserts it — `Queue.cs:337`).
- **HeadlessOptions.TryParse:** every flag; `--script` and `--pipe` together → usage; a missing value → usage.
- **HeadlessLoop** (in `Wavee.Tests`, no engine): posts from three threads run on the one consumer thread in order;
  `Stop` drains what is queued and returns.

No test reads production source (CLAUDE.md); no test touches `%LOCALAPPDATA%`; the store wrappers are tested over
in-memory `ILocalStore`/`IAppSettings` fakes exactly as `PlatformTests` does today.

---

## 6. Interface for the tray plan (`wavee-0.3-tray-implementation.md`)

A background run **with a tray and no main window is not a variant of headless mode.** Headless means *no engine
loop*: `Shell.Run()` never runs, `AppHost` never exists, the marshallers point at `HeadlessLoop`, and nothing can draw
a menu or a flyout. A tray icon needs an HWND (Shell_NotifyIcon and its message-only or hidden window), and Wavee's
menus and flyouts are engine popups over `AppHost` — so "tray, window hidden" is the **GUI process with its main
window hidden or closed-to-tray**, `FluentAppHarness.Run` still looping (idle-gated, so it costs what an idle window
costs), and `Playback.Os` still armed (SMTC and media keys keep working, which a tray user expects). The two share
everything below the shell and nothing above it:

- **Verbs a background run exposes** (both modes): `Playback.PlayNow / Pause / Resume / TogglePlay / Next / Previous /
  SeekTo / SetVolume / SetShuffle / SetRepeat / Stop / TransferTo` (`Playback.Host.cs:592-615`); `Playback.Audio.
  SetMuted` (`Playback.Audio.cs:201`); `Spotify.Login / Logout` (`Spotify.Session.cs:256-263`). The tray menu binds
  to these and to nothing in `Diagnostics.Headless` — the command grammar is a *script* surface, not a UI seam.
- **State signals a tray reads** (UI thread, `.Value` inside a component, `.Peek()` elsewhere): `Playback.Current /
  CurrentId / PhaseSignal / IsPlaying / Buffering / Error / PositionMs / DurationMs / Volume / Shuffle / Repeat /
  StreamFormat / OwnerSignal / Pending.Load` (`Playback.Host.cs:101-154`); `Spotify.Status / Fault`
  (`Spotify.Session.cs:194-197`); `Playback.Audio.Muted / Devices / Levels` (`Playback.Audio.cs:138-157`). The track's
  title/artist/art come off the handle in `Playback.Current` through the entity columns, never from playback state.
- **What the tray may reuse from this plan:** `Platform.ProfileRoot` / `--profile` (§3.6) and the arm table in
  `Diagnostics.Probe.TryRun` if it wants a `--start-minimized`-style flag — add a row, do not add a parser; the
  JSON-lines writer if a tray "copy diagnostics" wants the `status` shape.
- **What the tray must not do:** set `Playback.ToUi` & co. — that is `Shell.Host.Run`'s job once §1.6 item 1 is
  fixed (one assignment site, `AppHost.Post`), and `HeadlessHost` is the only other place that may assign them.
- **One shared consequence:** close-to-tray keeps the process alive with playback running; the headless `--connect`
  device name (`Wavee (headless)`) and the GUI's device name (`Environment.MachineName`, `Playback.Host.cs:254`) must
  stay distinct, so a tray-hidden GUI and a headless smoke on the same box are two devices, not one flapping one.

---

## 7. The work split

| # | Owner | Files (disjoint) | Lands | Depends on |
|--:|---|---|---|---|
| X1 | X (new; Opus) | `+Screens/Diagnostics.Headless.cs` (CORE), `Wavee.Tests/HeadlessTests.cs` | **now** — pure over `StatusSnapshot`, `ILocalStore`, `IAppSettings`, `EntityRef`, `QueueEdge` (all Wave 1/2 types) | nothing in flight (nobody edits `Screens/Diagnostics.*` or the tests folder's new file) |
| X2 | X (Sonnet after X1 is green) | `Screens/Diagnostics.Probe.cs` (the SHELL host, §3.3) | **now** for boot + login + `play <track>` + stdin/pipe + script + `--echo-log`; `play <album>` needs the members-edge read (X reads `Edges.cs`'s accessor; nothing to add); `--silent` refused with 69 until H3 | X1; `Spotify.Api.Boot` public (F, one `public`), `Playback.SnapshotForConnect` (G, one accessor) |
| O1 | orchestrator | `App.cs` (§3.2), `Platform.cs` + `Platform.Host.cs` (§3.6), `Wavee.Tests/PlatformTests.cs` (+2 facts for `ProfileRoot`) | now | — (the orchestrator-owned first cut, as `Platform.cs` was) |
| F1 | F | `Spotify.Audio.Stream.cs` `Stats` + the three counters (§3.4); `AudioStreamTests` +3 | now — F's file is not in flight | — |
| H1 | H | `Playback.Audio.cs` `Metrics` (§3.5) | **after the audio swap** (the file is being rewritten by the audio agent: NVorbis removal, the new adapters); the throughput metric attaches to the new adapter | the audio agent's landing |
| H2 | H | `Playback.Audio.cs` `OpenSilentAsync` fix + `UseSilentEndpoint` (§3.5) | with H1 | possibly the engine (`HeadlessAudioEndpoint(realtime)`, §8 Q4) |
| G1 | G | `Playback.Host.cs`: `SnapshotForConnect()`; the `NewDevice` hello on `Online` (§1.6 item 5) | now (tiny; G's file is not in flight) | — |
| I1 | I | `Shell.Host.cs:158-160`: install `Playback.ToUi = Store.Post = Spotify.Post = Palette.Host.Post = host.Post` before `FluentAppHarness.Run`, and `Fetch.Pump()` on `RootHost`'s frame tick (§1.6 items 1, 6) | in I's current Wave-4 pass (the file is in flight; a two-line insertion at a named site) | — |
| D1 | D | `Spotify.Session.Apply`: a `Welcome` effect that sets `Api.Market` + `Entities.Switch` (§1.6 item 2) — the headless host stops doing it itself the day this lands | Wave 2 fix-up | — |
| O2 | orchestrator | `ops/headless/*.wh`, `Invoke-WaveeHeadless.ps1`; plan §5 lines :1043/:1055 and the two handoff lines that say `--login-smoke` | now | X2 |
| Q1 | Q (Wave 5) | takes `BuildContextQueue` into `Queue.cs` when the first page needs it; `Diagnostics.Headless` then calls Q's | Wave 5 | — |

What can land now, while seven UI agents and the audio agent are in flight: **X1, X2, O1, F1, G1, O2** — none touches
`Shell/*.UI.cs`, `Deck.Faces.cs`, `Shell.Palette.cs`, `Actions.UI.cs`, `Setup.UI.Runtime.cs`, `Settings.UI.Video.cs`,
`Playback.Audio.cs` or `Wavee.csproj`. **H1/H2 wait for the audio swap**; until then the smoke reports host-level
timings and echoed `audio.*` lines, which is enough for the Wave 2 and Wave 3 gates as the plan words them.

**Verification the orchestrator runs, in order:** (1) `dotnet build Wavee.slnx` Debug + Release, 0 warnings;
(2) `dotnet test src/apps/Wavee.Tests` — the 1,138 baseline plus `HeadlessTests` (≈ 70 facts) green; (3) offline:
`Wavee.exe --headless --no-login --script ops/headless/parse-only.wh` exits 0 with a `boot` and a `verdict` line and
**no** `%LOCALAPPDATA%\Wavee` created when `--profile <temp>` is passed (the folder assertion the E2E harness makes,
`local-update-e2e.ps1:1574-1580`); (4) `Invoke-WaveeHeadless.ps1 ops/headless/login-smoke.wh` on the orchestrator's
signed-in profile, then `ogg320.wh`, then `far-seek-disk.wh` (the second run, disk-served); (5) with a deriver build,
`flac.wh`; (6) when H1/H2 land, `ogg320.wh -Silent` must give the same verdict with `endpoint:"silent"` and
`gapless-album.wh` must show `exact>=1`; (7) the two handoff lines and plan §5 updated in the same commit, with the
issue number (§8 Q8).

---

## 8. Open questions — only Christos can answer

1. **Is the headless smoke a release gate?** It needs a signed-in profile, network and 30 s of audio, so this plan
   keeps it a *wave* gate + a manual rehearsal step, not a `wavee-release.ps1` gate. Do you want `-DryRun` to run
   `login-smoke.wh -Silent` when a profile is signed in, and skip with a printed reason otherwise?
2. **A headless login when there is no credential.** v1 exits 67. Options for v2: the console device-code prompt
   0.2.9 had (`SpotifyLiveLogin.cs:173-187`, prints `spotify.com/pair` + a code), or a PKCE loopback flow (librespot's
   `--enable-oauth`; `Spotify.Pkce` exists, `Spotify.cs:800-837`). Both are Setup's flows (owner R, Wave 6) — share
   one implementation and expose it as `login` here, or keep headless strictly "resume only"?
3. **`Logout` on a headless exit.** Today `Logout` closes sockets *and* clears the credential (`Spotify.cs:275-279`).
   The store wrapper refuses the removal, so the host can call `Logout()` as-is; the alternative is a
   `SessionEventKind.Disconnect` that closes without clearing (cleaner, D's file). Which?
4. **The silent endpoint's pacing.** Fix it in Wavee (a paced `BufferedAudioEndpoint`, H's file only) or grow the
   engine's `HeadlessAudioEndpoint` a `realtime: true` mode (engine work, its own gates in `..\fluent-gpu`)? The
   engine route makes `--fake`'s playing bar real for every engine app; the Wavee route ships with H2.
5. **`HeadlessLoop` vs the engine's headless `AppHost`.** §1.2 recommends the plain loop; if any CORE rule the smoke
   must exercise depends on a `UseEffect`/`Memo` flush, the `AppHost` variant (`PostFreezeProbe.cs:42-52`) is a swap
   behind the same `Post`. Confirm the plain loop.
6. **Which engine checkout.** `Directory.Build.props:36` resolves `..\fluent-gpu` from the worktree, i.e.
   `C:\WAVEE\fluent-gpu`, while the brief says Wavee builds against the pinned `fluent-gpu-base`. Every engine citation
   here is against `fluent-gpu-base`; if the build really points at the video-rework checkout, `MediaPlayer` /
   `HostDispatch` lines need re-checking before H2.
7. **`seek end-10s`.** The gapless and prefetch scripts want "N seconds before the end" — a position form the grammar
   can carry (`end-10s`, resolved against `DurationMs` at post time). Include it, or make the scripts pick fixed
   positions per track?
8. **The issue number.** CLAUDE.md: every fix references its issue. This is a feature (the headless host) that also
   fixes four live defects (§1.6 items 1, 2, 5, 6) and exposes a fifth (3). One issue "Headless Wavee (the login and
   playback smoke)" with the defects as bullets, or one per defect? The orchestrator drafts; nothing is filed by this
   plan.

---

## 9. Sources

Repo (all under `C:\WAVEE\wavee-0.3` unless noted): `src/apps/Wavee/App.cs`, `Platform/Platform.cs`,
`Platform/Platform.Host.cs`, `Screens/Diagnostics.Probe.cs`, `Shell/Shell.Host.cs`, `Spotify/Spotify.cs`,
`Spotify/Spotify.Session.cs`, `Spotify/Spotify.Api.cs`, `Spotify/Spotify.Audio.cs`, `Spotify/Spotify.Audio.Stream.cs`,
`Spotify/Spotify.Connect.cs`, `Playback/Playback.cs`, `Playback/Playback.Host.cs`, `Playback/Playback.Audio.cs`,
`Playback/Playback.Os.cs`, `Entities/Entities.cs`, `Entities/Queue.cs`, `Entities/Fetch.cs`, `Entities/Store.cs`,
`Entities/Palette.Host.cs`, `Wavee.csproj`, `Properties/launchSettings.json`, `Wavee.Tests/TestScope.cs`,
`Wavee.Tests/Wavee.Tests.csproj`; `docs/plans/wavee/wavee-0.3-implementation.md` (§2, §3.5, §4.7-4.10, §5, §7, §8),
`wavee-0.3-vorbis-implementation.md` (§4.5, §5.4, §7.3, §8), `wavee-0.3-flac-implementation.md` (§8.2-8.3),
`wavee-0.3-ui/22-lyrics.md:687-688`, `handoff-20260912d-0.3-wave0-done.md:63-64, 89-90`,
`C:\wavee\waveemusic\docs\plans\wavee\handoff-20260912e-0.3-fresh-agent.md:69-72`; `_old/Wavee/Program.cs`,
`_old/Wavee/SpotifyLive/SpotifyLiveLogin.cs`, `_old/Wavee/SpotifyLive/LiveSessionHost.cs`,
`_old/Wavee/Features/Diagnostics/WaveeNavProbe.cs`; `.claude/skills/wavee/probes.md`;
`ops/release/wavee-release.ps1:597-608`, `ops/release/tests/local-update-e2e.ps1:550-825, 1561-1592`,
`ops/build/bench-wavee.ps1:40-53`.

Engine (`C:\WAVEE\fluent-gpu-base\src`): `FluentGpu.Engine/Foundation/Signals/Signal.cs`, `ReactiveCore.cs`,
`FluentGpu.Engine/Media/Playback/Audio/AudioClock.cs`, `PcmAudioPlayer.cs`, `AudioFeedThread.cs`,
`BufferedAudioEndpoint.cs`, `FluentGpu.Engine/Media/Playback/MediaPlayer.cs`, `HeadlessScriptedPlayer.cs`,
`FluentGpu.Engine/Hosting/AppHost.cs:2490, 2943`, `FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs`,
`FluentGpu.Windows/Wasapi/WasapiAudioDevice.cs`, `WasapiPcm.cs`, `MmcssProAudio.cs`, `MmDeviceWatcher.cs`,
`FluentGpu.WindowsApi/Media/SystemMediaControls.cs`, `FluentGpu.WindowsApi/Power/PowerSession.cs`,
`FluentGpu.WindowsApp/Program.cs:96-175`, `FluentGpu.WindowsApp/Probes/PostFreezeProbe.cs`,
`FluentGpu.WindowsApp/FluentGpu.WindowsApp.csproj:3-6`.

Web (accessed 2026-09-13): librespot — https://github.com/librespot-org/librespot/wiki/Options ·
https://github.com/librespot-org/librespot/wiki/Events · https://raw.githubusercontent.com/librespot-org/librespot/master/src/main.rs ·
https://raw.githubusercontent.com/librespot-org/librespot/master/core/src/cache.rs ·
https://docs.rs/librespot-playback/latest/librespot_playback/player/struct.Player.html ·
https://raw.githubusercontent.com/librespot-org/librespot/master/connect/src/spirc.rs ·
https://raw.githubusercontent.com/librespot-org/librespot/master/examples/play.rs; spotifyd —
https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/configuration/README.md ·
https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/configuration/auth.md ·
https://raw.githubusercontent.com/Spotifyd/spotifyd/master/docs/src/advanced/mpris.md · https://docs.spotifyd.rs/advanced/hooks.html;
go-librespot — https://raw.githubusercontent.com/devgianlu/go-librespot/master/README.md ·
https://raw.githubusercontent.com/devgianlu/go-librespot/master/api-spec.yml ·
https://raw.githubusercontent.com/devgianlu/go-librespot/master/API.md ·
https://raw.githubusercontent.com/devgianlu/go-librespot/master/config_schema.json; mpv —
https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/ipc.rst ·
https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/options.rst ·
https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/mpv.rst · https://github.com/mpv-player/mpv/issues/10116;
sysexits — https://man.freebsd.org/cgi/man.cgi?query=sysexits&sektion=3; TAP 14 —
https://testanything.org/tap-version-14-specification.html.
