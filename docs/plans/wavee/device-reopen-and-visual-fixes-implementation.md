# Wavee 0.2.7 — audio device-reopen bug (late advance · flickering clock · slowed next track), row-size artwork, sign-in overflow, report-kind default

## Context

Found on 2026-09-02 while 0.2.6 was being cut (all reproduced from the live log / real captures):

1. **A mid-track output-device / format reopen breaks the gapless chain three ways.** Log (`wavee-20260902.log`, pid 88908): the track started on a 48 kHz device (`format open deviceRate=48000` @ 1788358974237) and the NEXT body was `prepare-primed` right then (`token=pf-56 … dur=199302`). 52 s before the end the device reopened (`format open 48000` → `44100` → `44100` within 800 ms, @ 1788359136975–137757). At the end: `[gapless] arm remainMs=1966 … clock=2251680`, `commit-join clock=2277600 join=9486174 … xruns=2`. `join` is absolute (9486174 frames = 3:35 @ 44.1 kHz); `clock` (2277600 ≈ 51.6 s) restarted at the reopen. Result: (a) `audio transition completed` fired 164 s late (@ 1788359353182), the player bar counted to 5:36 / 3:35; (b) the readout flicked between two values (two clocks folded alternately); (c) the joined body, primed against the 48 kHz voice, rendered on the 44.1 kHz voice → ~92 % speed, "sounded slowed"; the user had to restart playback (which re-primed at 44.1 kHz and was fine).
2. **Row size ignores the artwork.** "Row size → Comfortable" makes track rows 64 DIP but the cover stays 32 DIP (`TrackRow.ThumbSize`), a small square in a tall row; the Settings › Appearance density preview mirrors the same bug.
3. **Sign-in page overflows its lane by one line** ("Wavee needs Spotify Premium · … Sign up" half-clipped) with no scrollbar rail although `AlwaysShowScrollbar = true`; the QR paints ~110 DIP although `QrSize = 80`; the scan card's description wraps to two lines.
4. **"Report a problem…" opens with *Question* selected** (Segmented index 2, preview header "Wavee report · Question") although `SettingsPage.About.cs:148` requests `ReportKind.Bug`.

Rules: CLAUDE.md — pure engine-free decision classes + unit tests (no source-text tests), no env switches, replace outright, every fix cites its issue (`(#n)` in CHANGELOG + `Fixes #n`); engine changes are made and gated in `..\fluent-gpu` (Debug + Release build, VerticalSlice). Implementation: Sonnet subagents on disjoint files; the orchestrator builds/tests. Ships in **0.2.7** (`## [0.2.7] - unreleased`).

Issues to create (approval-gated, one `gh issue create` each, milestone `0.2.x Breaker`, on the board): A `type: bug, area: playback` (device reopen); B `type: bug, area: detail-pages` (row-size art); C `type: bug, area: setup` (sign-in overflow/QR); D `type: bug, area: diagnostics` (report kind).

---

## 1. Device-reopen gapless bug (issue A) — app `SpotifyLive/Audio/FluentMediaAudioHost.cs` + engine `PcmAudioPlayer` / `QueuePreparation` / `WasapiPcm`

### Root causes (all verified in source)

| # | Symptom | Cause | Where |
|---|---|---|---|
| A1 | advance 164 s late | `clock` = `PcmAudioSession.SampleClock` = `CrossfadeMixer.ConsumeSeq`, frames consumed **since that session instance was built**; a seek rebases only `AudioClockPosition`, never `ConsumeSeq`. A rate change (`PcmAudioPlayer.cs:1037-1042` `DeviceFormatChanged`) → `OnDeviceFormatChanged` (`FluentMediaAudioHost.cs:916`) → `SoftReloadAsync` (`:964`) → `OpenSessionAsync` (`:846`) = a **new session, clock 0**, then `reopened.SeekAsync(savedPos)` (`:1080-1082`) **directly on the engine session**, bypassing the host's `Seek()` (`:580`) — the one caller that rebases `_activeJoinFrame` (`:594`). `OpenSessionAsync:884` sets `_activeJoinFrame = MsToFrames(bytes.DurationMs, rate)` (track-absolute), so `CommitGaplessJoin:1386-1387` `join = Math.Max(_activeJoinFrame, clock)` schedules the join `savedPos` in the future (9486174 − 2277600 = 163.5 s). `Tick:1599-1612` fires `AnnounceGaplessJoin` only when `SampleClock >= _joinFrame`; the mixer honours the same frame (`MixVoice.StartFrame`), so B was truly silent. `arm remainMs=1966` was right because it uses the ms-domain `PositionMs` (`:1559`) — only the frame-domain estimate is stale. | app |
| A2 | next track ~8.8 % slow | `PrepareNextCoreAsync:1249-1250` primes with `PrepareContext.For(session.Format, …)` → the decoder/resampler is **bound to the rate at prime time** (`PcmAudioPlayer.PrepareAsync:31-49`). `SoftReloadAsync`/`OpenSessionAsync` never touch `_prepItem/_prepStream/_prepBytes/_prepToken`; `CommitGaplessJoin:1394`/`CommitCrossfade:1336`/`TryPromoteAtEnd:1481` splice `item.AudioVoice` into the **new** session with no rate check — `IPreparedItem`/`AudioPreparedItem` (`QueuePreparation.cs:44-73`) carry no rate at all, `RingAudioSource` is frames×channels only. 48000/44100 = 1.0884. | app + engine |
| A3 | readout flicks between two values | `NowPlayingProjection.OnHostSignal:1074` pushes the host's **raw, unclamped** `s.PositionMs` onto `PositionTicks` (200 ms), while the 1 Hz `Tick:1090` pushes `Pos()` (clamped, `:289-303`) — two derivations interleaving on `PlaybackBridge.PushPosition` → `PositionMs.Value`. During the reload window `OpenSessionAsync:871-872` sets `_activeStartMs = 0; _clockStale = false` *before* the restoring seek lands while the 200 ms `_ticker` keeps running → host ticks of `pos ≈ 0` interleave with the wall-clock extrapolation. The `PlaybackController.PublishPositionMs:3168-3172` `ClockValid` guard exists only on the controller's own emit path, not on the host `Signals` stream. | app |
| A4 | three `format open`s, stale `Format` | `MmDeviceWatcher` → `AudioDeviceController` (`_pending` flag only, no debounce) → two rebuilds (`48000` then `44100`) + the soft reload's own open (`44100`). `WasapiPcm.CreateBackend` (`WasapiPcm.cs:38,52-56`) captures the probe format once: `PcmAudioPlayer.Format` stays 48000 after the change and `AudioFeedThread` keeps sizing ms→frames against 48 kHz. `_gaplessXrunsAtArm` (`:1565`) is not reset by `OpenSessionAsync` → `xrunDelta` meaningless after a reopen. | engine |

### Fix

**Engine (`..\fluent-gpu`, gated by Debug + Release build + `FluentGpu.Engine.Tests` + VerticalSlice):**

1. `Media/Playback/QueuePreparation.cs`: `IPreparedItem` / `AudioPreparedItem` gain `int MixRate { get; }` — set in `PcmAudioPlayer.PrepareAsync` from `ctx.Format.SampleRate`. The prepared voice now says which mixer it was built for.
2. `PcmAudioPlayer.cs:1037-1042`: on a rate change set `_format = newFormat` (so `Format`, and therefore every later `PrepareContext.For(session.Format)`, is the live rate) before raising `DeviceFormatChanged(newFormat)`; `WasapiPcm.CreateBackend` passes a `Func<MixFormat>` (the player's current `Format`) to the `AudioDeviceController` endpoint factory instead of the captured probe format, and `AudioFeedThread` re-derives its ms→frames sizing from the session's format on rebuild (`AudioFeedThread.cs:144-149,168-170` — one `Resize(MixFormat)` called from the same site).
3. `AudioDeviceController.cs:69-114`: coalesce default-device notifications for 250 ms (the OS raises the change before the new endpoint's mix format is final; the 48000-then-44100 pair is one switch) — a `_pendingSince` stamp on the cold loop, not a timer.
4. Tests (`FluentGpu.Engine.Tests`): `AudioDeviceStateMachineTests` + `DefaultDeviceChange_DifferentRate_RaisesDeviceFormatChanged_AndUpdatesFormat` (a `HeadlessAudioEndpoint` at 44100 replacing one at 48000); `PrepareAsync_StampsMixRate`; `Debounce_TwoNotificationsWithin250ms_RebuildOnce`.

**App (`SpotifyLive/Audio`), pure seam first — `GaplessJoinClock.cs` (sibling of `XrunLogLine`/`PlayIntentGate`, engine-free, source-included in `Wavee.Tests.csproj`):**

```csharp
/// <summary>Frame-domain arithmetic for the gapless join, in ONE place. The session's SampleClock is frames consumed
/// since THAT PcmAudioSession was built (a seek rebases the position clock, never the sample clock; a device-format
/// soft reload builds a NEW session at clock 0 and seeks it to the saved playhead). Every writer of the active track's
/// natural-end frame therefore expresses it as "clock now + frames still to play", never as "frames from track start".</summary>
internal static class GaplessJoinClock
{
    public static long MsToFrames(long ms, int rate) => ms * rate / 1000L;

    /// <summary>The active track's natural-end frame, on the session clock, given where the playhead is right now.</summary>
    public static long JoinFrameFor(long sampleClockNow, long durationMs, long playheadMs, int rate)
        => sampleClockNow + MsToFrames(Math.Max(0L, durationMs - playheadMs), rate);

    /// <summary>Where to start the next voice: never in the past, and never further out than the active track's own
    /// remaining time + 100 ms — a stale estimate degrades to a ≤100 ms butt-join instead of a 164 s stall.</summary>
    public static long ScheduleJoin(long activeJoinFrame, long sampleClockNow, long remainingMs, int rate)
    {
        long join = Math.Max(activeJoinFrame, sampleClockNow);
        long bound = sampleClockNow + MsToFrames(Math.Max(0L, remainingMs), rate) + rate / 10;
        return Math.Min(join, bound);
    }

    /// <summary>A primed voice is only spliceable into a mixer running at the rate it was resampled for.</summary>
    public static bool PrimedSlotMatches(int primedMixRate, int sessionRate) => primedMixRate == sessionRate;
}
```

`FluentMediaAudioHost.cs`:
- `OpenSessionAsync:884` → `_activeJoinFrame = GaplessJoinClock.JoinFrameFor(pcm.SampleClock, bytes.DurationMs, 0, rate)`; `Seek:594` → `JoinFrameFor(pcm.SampleClock, _activeDurMs, ms, rate)`; `SoftReloadAsync` right after the restoring `SeekAsync(savedPos)` (`:1082`) → `_activeJoinFrame = JoinFrameFor(reopened.SampleClock, savedDurMs, savedPos, reopened.Format.SampleRate)` and, if armed, `_gaplessXrunsAtArm = SessionXruns()` (log `[gapless] rearm-after-reopen clock=… join=…`).
- `CommitGaplessJoin:1386-1387` → `long join = GaplessJoinClock.ScheduleJoin(_activeJoinFrame, clock, _activeDurMs - PositionMs, rate)`; `CommitCrossfade`/`TryPromoteAtEnd` keep their own start math (already clock-relative).
- **Prepared-slot invalidation.** `SoftReloadAsync`, after the new session is up: `if (_prepItem is { } p && !GaplessJoinClock.PrimedSlotMatches(p.MixRate, newRate)) { await DisposePreparedSlotAsync(); _signals.OnNext(AudioHostSignal.PreparedInvalidated(token)); }` — a new `AudioHostSignalKind.PreparedInvalidated`; `PlaybackController.OnAudioSignal` answers it with the existing `SchedulePreparedNext(..., reason: "format-changed")` (same shape as `transition-missed`, `:3369`). Belt and braces at the three splice sites: `if (!PrimedSlotMatches(item.MixRate, sess.Format.SampleRate)) { AbandonPendingJoin(sess, "rate-mismatch"); await DisposePreparedSlotAsync(); return false; }` — the controller's ordinary end-of-track load takes over (a hard cut, never a slow track).
- **Reload window ticks.** `Tick` skips the `PositionTick` emission while `Volatile.Read(ref _softReloading) != 0 || _clockStale` (the host's `ClockValid` already exists — use it), so no `pos ≈ 0` tick leaves the host mid-reload.

`Backend/PlaybackProjection.cs:1074`: `if (!structural) _positionTicks.OnNext(Pos());` — the host tick path goes through the same clamp as every other reader (the #62 clamp then covers the ticks stream, not only `PositionMs`).

Tests (`Wavee.Tests`): `GaplessJoinClockTests` — `JoinFrameFor_FreshOpen_IsDurationFrames`, `JoinFrameFor_AfterReopenAtPlayhead_IsRemainingFramesFromClockNow` (clock 2277600, dur 215100, playhead 163500 @44100 → 2277600 + 2275560), `ScheduleJoin_NeverInThePast`, `ScheduleJoin_BoundsAStaleEstimateToRemainingPlus100ms` (the log's numbers → join − clock ≤ 86 700 + 4410), `PrimedSlotMatches_RateMismatch_False`; `RestoredPositionClampTests` + `HostPositionTick_PastDuration_IsClampedOnTheTicksStream` (subscribe `PositionTicks`, `OnHostSignal(PositionTick, dur + 5000)` → `dur`); `PreparedTransitionTests` + `RateMismatch_AbandonsJoin_AndInvalidatesSlot` if the transition policy is reachable purely (else the guard lives behind `PrimedSlotMatches`, which is).

CHANGELOG (Fixed): "Switching the output device (or its sample rate) mid-track no longer breaks the next track: the gapless hand-off used to be scheduled from the old device's clock (the next song started up to minutes late, the bar counting past the end), the time readout flicked between two values, and the pre-decoded next song played slow and flat at the old rate. The join now follows the live session clock, a pre-decoded song is re-prepared for the new rate, and the readout is clamped on every path. (#65)"

---

## 2. Row-size artwork (issue B)

**Cause (verified).** `Components/TrackRow.cs:107-110` `internal const float ThumbSize = WaveeSize.Thumb32;` and `TrackRow.Grid(...)` (`:192`) receives `float rowH` but `:200` does `float thumb = ThumbSize;` — the art never reads density. The row ladder lives in TWO places that must agree: `TrackRow.RowHeightFor(int)` (`:117-118`, 40/48/56/64) and the one the detail table actually calls, `Features/Detail/DetailTrackTableRules.RowHeightFor(int density, bool classic)` (`:41-43`, modern 40/48/56/64, classic 36/40/44/48). Four more sites are pinned to the 32: `DetailTracks.cs:38,519` (header/row column track `TrackSize.Px(ThumbSize)`), `DetailTracks.cs:607` (drawer rail indent `ThumbSize / 2f`), `DetailTrackTableRules.TrackLane.Thumb = 32f` (`:231-232`, the width-budget lane, "the two are one number"), and the Settings preview `Design/WaveePicker.cs:103-105` (`artworkEdge = TrackRow.ThumbSize * PreviewScale`). `Surfaces.Artwork(...)` (`Design/Surfaces.cs:185`) uses width/height as the decode target too, so an enlarged art must decode larger.

**Fix — one ladder, art keyed to the row.** In the pure `DetailTrackTableRules` (engine-free, already tested):

```csharp
/// <summary>Art edge per density, on the app thumbnail ladder (WaveeSize.Thumb32/40/48): the row keeps ≥ 8 DIP of
/// breathing room above and below (row − art ≥ 16), so Compact 40 → 32 · Default 48 → 32 · Cozy 56 → 40 · Comfortable 64 → 48;
/// Classic rows (36/40/44/48) stay on 32 except Comfortable → 40 (48 − 40 = 8 is the classic skin's tighter room).</summary>
internal static float ArtSizeFor(int density, bool classic) => classic
    ? (density == 3 ? WaveeSize.Thumb40 : WaveeSize.Thumb32)
    : density switch { 2 => WaveeSize.Thumb40, 3 => WaveeSize.Thumb48, _ => WaveeSize.Thumb32 };
```

- `TrackRow.RowHeightFor` stays (the non-detail callers) and gains a sibling `ArtSizeFor(int density)` that forwards to `DetailTrackTableRules.ArtSizeFor(density, classic: false)`; `TrackRow.Grid` takes `float art` next to `rowH` (`float thumb = art;`) and `Surfaces.Artwork(..., art, art, Radii.Control, decodePx: (int)(art * 2))` — the row art column is `TrackSize.Px(art)`.
- `DetailTracks.cs`: `ThumbSize` const → `float art = DetailTrackTableRules.ArtSizeFor(density, set.Classic)` computed once beside `rowH` (`:776`) and threaded to the header track (`:519`), the drawer indent (`:607`) and every `TrackRow.Grid` call; `TrackLane.Thumb` becomes `TrackLane.ThumbFor(density, classic)` = `ArtSizeFor` (the relief ladder at `:165` adds the density-correct lane, so the width budget stays exact).
- `WaveePicker.DensityRows`: `artworkEdge = TrackRow.ArtSizeFor(density) * PreviewScale` — the preview can no longer drift from the real row (its doc comment's contract).
- Non-detail surfaces (`HomeModules.Artists.cs:195`, `RecentsPage.cs:1932/2014`, `PlaylistInsertionPreview.cs:41/44`) keep `TrackRow.ThumbSize` (= the density-1 value) — untouched.
- Tests (`Wavee.Tests/TrackRowStyleRulesTests.cs`): `Modern_RowHeightLadder` (40/48/56/64 — currently untested), `ArtSizeFor_GrowsMonotonically_AndLeavesBreathingRoom` (∀ density, classic ∈ {0,1}: `art ≤ row − 8`, `art` non-decreasing in density, values on the 32/40/48 ladder), `Preview_MirrorsTrackRow` (WaveePicker's edge = `ArtSizeFor × PreviewScale` — pure arithmetic exposed as a static).

CHANGELOG (Fixed): "Row size now scales the cover art with the row: Cozy rows get 40-DIP art, Comfortable rows 48-DIP, instead of a 32-DIP square floating in a 64-DIP row; the Settings density preview follows. (#66)"

---

## 3. Sign-in page overflow · no rail · oversized QR (issue C)

**Causes (verified).**
- **Unbounded scroller.** Every `BoxEl` in the chain declares `Grow=1, Shrink=1, MinHeight=0` (`SetupDialog.cs:129-135, 175-189`, `SetupPageHost.cs:60-99`) but three bare pass-through nodes sit between `PagesHost` and the page frame: `Flow.KeepAlive(...)` (`SetupDialog.cs:180-187`), `SetupPagePlaceholders.For` → `Embed.Comp(SetupPageCapture)` (`SetupPage.Placeholders.cs:19-26`), and `SetupPageHost.Frame` → `Embed.Comp(SetupPageFrame)` (`SetupPageHost.cs:29-31`). `ComponentEl`/`KeepAliveEl` carry no layout columns (`Reconciler.cs:611-620` returns before `WriteColumns`), so they keep `LayoutInput.Default` (column, grow 0, shrink 0, `MinH = NaN`) and shrink-wrap vertically. The ScrollView's height becomes its content height → nothing scrolls, `PagesHost`'s `ClipToBounds` cuts the last row. `SceneRecorder.EmitScrollbar` (`:3196-3215`) returns on `content <= viewport + 0.5` **before** it reads `AlwaysShowBar`, so the "persistent rail" can never appear on an unbounded viewport — the same bug, not a second one. The shell already solves this everywhere else: `ContentHost.cs:70-76` wraps `Flow.KeepAlive` in `BoxEl { Grow=1, Shrink=1, MinWidth=0, MinHeight=0, ClipToBounds=true }` ("give the keep-alive a definite height so ClipToBounds has a box to clip against") and `ContentHost.PageFor` (`:169-171`) wraps every page `Embed.Comp` in `BoxEl { Grow=1, Shrink=1, MinWidth=0, MinHeight=0, Direction=1 }`.
- **QR 111 DIP for `QrSize = 80`.** `Features/Auth/QrGrid.cs:27-31`: `cell = Math.Max(3, (int)(_size / total)); plate = total * cell` — `_size` is only a divisor hint. The pairing URI is a v3 symbol (29 modules + 2×4 quiet = 37): `80/37 → 2 → max(3, 2) = 3` → plate 111. The card is then 2×16 + 111 = **143**; the budget in `SetupLayout.SignInIdleBodyHeight` (`:72-76`) charges `QrSize + 2·CardPadding` = 112, so the painted stack is 40 + 68 + 143 + 32 + 3×20 = **343** vs the 325 lane: 18 DIP over, the 32-DIP "Premium · Sign up" row half-cut. `SetupLayoutTests.SignInIdleBody_FitsTheReferenceLane` passes against the wrong number.
- **Two-line description**: `SetupScanCard.Render` (`SetupPage.SignIn.cs:251`) composes `PairLine(code) + " · " + ExpiresIn(mmss)` ≈ 62 chars into a ~289-DIP column — never one line at this width. Cosmetic (header block 53 < QR); fixed by shorter copy.

**Fix.**
1. `SetupDialog.PagesHost`: wrap `Flow.KeepAlive(...)` exactly like `ContentHost.cs:70-76` (`BoxEl { Grow=1, Shrink=1, MinWidth=0, MinHeight=0, ClipToBounds=true, Children=[keepAlive] }`). `SetupPageHost.Frame` returns `new BoxEl { Key = "setup-frame:" + page, Grow=1, Shrink=1, MinWidth=0, MinHeight=0, Direction=1, Children=[Embed.Comp(...)] }` (the `ContentHost.PageFor` pattern); `SetupPagePlaceholders.For` likewise returns its `Embed.Comp` inside the same box. With a bounded viewport the ScrollView scrolls and `AlwaysShowScrollbar` pins the rail whenever a page overflows — the safety net the onboarding plan promised.
2. `QrGrid`: honour the requested size. Pure static `QrGrid.PlateFor(float size, int modules)` → `total = modules + 8; cell = Math.Max(2, (int)(size / total)); return total * cell;` (a 2-DIP cell is 3 px at 150 %, 2 px at 100 % — integer either way; a 74-DIP symbol scans fine). `Render` derives `cell` from the same function. `SetupLayout`: `QrPlateBudget = QrGrid.PlateFor(QrSize, 33)` (v4 = the longest pairing URI we mint → 41 × 2 = **82**) and `SignInIdleBodyHeight` charges `QrPlateBudget + 2·CardPadding` (114) → 2-line-lead total **314 ≤ 325**.
3. Copy: `setup.signIn.pairLine` → `"{code} · spotify.com/pair"`, the card composes `pairLine + " · " + ExpiresIn` (≈ 40 chars, one line at ≥ 289 DIP); the header stays "Scan the code with your phone".
4. Tests: `QrGridTests` (`PlateFor(80,29) == 74`, `PlateFor(80,33) == 82`, `PlateFor(120,29) == 111`, cell never < 2, plate ≤ size whenever `size/total ≥ 2`); `SetupLayoutTests` re-pinned to `QrPlateBudget` (2-line lead ≤ lane; 3-line lead still overflows). The layout-chain fix has no pure seam (engine layout) — verified by capture (§6).

CHANGELOG (Fixed): "The setup sign-in page no longer cuts off its last line: the page body now really scrolls (and shows its rail) when it overflows, the QR code respects its 80-DIP box instead of growing to 111, and the scan card's text fits one line. (#67)"

---

## 4. "Report a problem…" opens on Question (issue D) — root cause **unverified**; hardening + diagnosability

**What is known.** `SettingsPage.About.cs:148` → `ReportRequests.Open(ReportKind.Bug)` (`ReportRequests.cs:22-27`: two **static fields** `Kind`/`Prefill` + a counter signal) → `ReportChrome.cs:31-39` effect on the counter → `ReportDialog.Open(..., ReportRequests.Kind, ...)` → `InitialKind = effectiveKind` (`:60`) → `UseSignal(KindIndex(InitialKind))` (`:98`), `KindIndex(Bug) = 0` (`:135-142`) → `Segmented.Create(items, kindIndex)` (the second positional arg IS `selectedIndex`, `Segmented.cs:82-92`; the core only coerces values < 0). `ReportKind` is `{ Crash=0, Bug=1, Feature=2, Question=3, Idea=4 }` — an enum-as-index slip would show *Feature*, not Question; the highlight and the preview header read the same `kindIndex` signal, so the signal genuinely held **2**. No caller ever requests Question (six `Open` sites: Bug/Feature/Crash). Two adjacent real defects: (a) the kind travels out-of-band as statics, so two `Open` calls in one flush collapse to the last one, and a stale `Prefill.CrashReportPath` is never cleared (would force `isCrash` on the next open); (b) `ReportChrome`'s `last = UseRef(-1)` baseline swallows the first request after a remount. And `ReportDialogBody` is embedded propless **without a Key** (`ReportDialog.cs:55`) while the card IS keyed by kind (`:126`) — a reused body node keeps its previously seeded signal.

**Fix (removes every path to a stale kind, plus a log line to catch the rest).**
1. `ReportRequests`: replace the statics with a payload signal `Signal<ReportRequest?>`, `record ReportRequest(int Seq, ReportKind Kind, ReportPrefill? Prefill)`; `Open` publishes a new record; `ReportChrome` opens on every distinct `Seq` it has not opened yet (the last *opened* seq kept in a static, not a `-1` ref baseline).
2. `ReportDialog.Open`: log `report.open kind=<kind> effective=<effectiveKind> crash=<mode> prefill=<bool>` (Info); embed the body `with { Key = "report-body:" + (int)effectiveKind }` so the kind is part of the node identity, exactly like the card.
3. Lift the two switches into a pure `Features/Feedback/ReportKindIndex.cs` (`IndexOf(ReportKind)`, `KindAt(int)`, `Segments` order) — source-included in `Wavee.Tests.csproj`; `ReportKindIndexTests`: round-trip for the four visible kinds, `IndexOf(Crash) == 0`, `KindAt(out-of-range) == Bug`, segment order = loc key order (`Strings.Report.KindBug/…`).
4. Re-shoot the dialog after the fix with `Drive-WaveeWindow.ps1`; if Question still appears on a cold About → Report click, the new log line names the kind that arrived and the investigation continues from there (noted in the issue).

CHANGELOG (Fixed): ""Report a problem…" could open with Question selected; the requested kind now travels with the request and is part of the form's identity, and every open is logged. (#68)"

---

## 5. Sequencing

| Wave | Agent | Files (disjoint) |
|---|---|---|
| 1 | engine (Sonnet) | `QueuePreparation.cs` (`MixRate`), `PcmAudioPlayer.cs` (`_format` update + `PrepareAsync` stamp), `WasapiPcm.cs`, `AudioDeviceController.cs` (250 ms coalesce), `AudioFeedThread.cs` (`Resize`), `FluentGpu.Engine.Tests` (3 tests); gates: Debug + Release build, `dotnet test`, VerticalSlice |
| 2a | app audio (Sonnet) | `SpotifyLive/Audio/GaplessJoinClock.cs` (new), `FluentMediaAudioHost.cs`, `Backend/PlaybackController.cs` (`PreparedInvalidated` → `SchedulePreparedNext`), `Backend/PlaybackProjection.cs:1074`, the audio-host signal contract, tests `GaplessJoinClockTests`, `RestoredPositionClampTests`, `PreparedTransitionTests` |
| 2b | app row-size (Sonnet) | `DetailTrackTableRules.cs`, `TrackRow.cs`, `DetailTracks.cs`, `WaveePicker.cs`, `TrackRowStyleRulesTests.cs` |
| 2c | app setup (Sonnet) | `SetupDialog.cs`, `SetupPageHost.cs`, `SetupPage.Placeholders.cs`, `SetupPage.SignIn.cs`, `SetupLayout.cs`, `Features/Auth/QrGrid.cs`, `assets/loc/en-US.json` (`setup.signIn.pairLine`), `QrGridTests`, `SetupLayoutTests` |
| 2d | app report (Sonnet) | `ReportRequests.cs`, `ReportChrome.cs`, `ReportDialog.cs`, `ReportKindIndex.cs` (new), `ReportKindIndexTests` |
| — | orchestrator | `CHANGELOG.md` `## [0.2.7] - unreleased` (4 bullets `(#65)…(#68)`), `Wavee.Version.props` → 0.2.7, builds/tests/captures, commit `Fixes #65…#68`, release + Store as for 0.2.6 |

Before wave 2: create issues #65–#68 (`gh issue create`, approval-gated) so the CHANGELOG refs and the commit trailers can name them.

## 6. Verification

- Engine: `dotnet build src/FluentGpu.slnx` Debug + Release clean; `dotnet test src/FluentGpu.Engine.Tests` green incl. the 3 new tests; `dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`.
- App: `dotnet build Wavee.slnx` Debug + Release clean; `dotnet test src/apps/Wavee.Tests` green (baseline 7064 + new); `Invoke-Pester ops/release/tests` green.
- Captures (`ops/release/tools/Drive-WaveeWindow.ps1`, `--fake`, no other Wavee running): the sign-in page at 1000×640 shows the full "Premium · Sign up" row and a 74-DIP QR, and the rail appears when a page overflows (force it below the 770 breakpoint); a playlist at Row size = Comfortable shows 48-DIP art in 64-DIP rows and the Settings › Appearance preview matches; About → Report a problem opens on **Bug** with `report.open kind=Bug` in the log.
- Audio, on the dev box with the real backend: play a track and switch the default output device (48 kHz ↔ 44.1 kHz) about a minute before its end; expect `[gapless] rearm-after-reopen`, `commit-join` with `join − clock ≤ remaining + 4410`, `audio transition completed` within ±100 ms of the track's end, the next track at normal pitch, and a monotonic time readout (no two-value flicker). Repeat with a track whose next body has NOT primed yet (nothing to invalidate) and with the switch landing inside the join window (`_joinPending` → the existing defer).
