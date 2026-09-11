# Compiler warnings and loose ends — implementation plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee.Tests/**` (most of it), three app files whose warnings only
appear in the test compilation, and `Wavee.Tests.csproj`. Mechanical pass. **End state:** `Wavee.Tests` builds with zero
warnings and the `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` opt-out at `Wavee.Tests.csproj:16-18` — which
names exactly this backlog — is deleted, so the test project joins the app baseline.

---

## 0. The current set — `dotnet build Wavee.slnx -c Debug`, 2026-09-05

65 warnings, all from the **Wavee.Tests** compilation (the app projects are warning-free — they enforce
`TreatWarningsAsErrors`). Grouped:

| Code | Count | What |
|---|---|---|
| CS0436 | 25 | `Signal<T>` / `IReadSignal<T>` in `VirtualCollectionSignalShim.cs` conflict with the same types in `FluentGpu.Engine` |
| xUnit1031 | 22 | blocking task operations in test methods |
| xUnit2013 | 11 | `Assert.Equal(n, collection.Count)` for n ∈ {0, 1} |
| CS0649 | 6 | field never assigned |
| CS8604 / CS8619 | 3 | nullable flow |
| xUnit2000 | 2 | expected/actual swapped |
| CA2022 | 1 | inexact `Stream.Read` |
| CS0067 | 1 | event never used |

**Stale in the RCA:** the engine's `FluentGpu.VerticalSlice` warnings (`IL2026`/`IL2075`/`IL2070`, `CA2014`, `CS0649`) do
**not** appear — `VerticalSlice` is not in `Wavee.slnx`; they belong to the engine repo's own gates
(`..\fluent-gpu`, `dotnet build src/FluentGpu.slnx`) and are out of scope here (see §C). Two codes the RCA did not list are
present: `CA2022` and `CS0067`.

Every fix below was checked against the current source. Nothing is suppressed via `#pragma`/`NoWarn` in categories A or B.

---

## A. Worth fixing now — cheap and correctness-adjacent

### A.1 CS0436 — the `Signal<T>` shim shadows the real engine type (25 warnings, one root cause)

**What is happening.** `Wavee.Tests/VirtualCollectionSignalShim.cs` (24 lines) defines `FluentGpu.Signals.IReadSignal<T>`
and `FluentGpu.Signals.Signal<T>`. Its header (`:3-7`) and the csproj comments (`:94-97`, `:236`, `:250`, `:445`,
`:510-512`) say the shim exists so the test assembly compiles engine sources (`VirtualCollection.cs`, `SelectionModel.cs`,
`CoverColorPlane.cs`, `DeveloperMode.cs`, `PlayLogStore.cs`, `NavOrigin.cs`, `SidebarPinStore.cs`, `LocalAudioDeviceService.cs`
…) **without referencing FluentGpu.Engine**. That premise is false today: `Wavee.Tests.csproj:89` references
`FluentGpu.WindowsApi.csproj`, and `fluent-gpu/src/FluentGpu.WindowsApi/FluentGpu.WindowsApi.csproj:13` references
`FluentGpu.Engine.csproj`; project references flow transitively, so `FluentGpu.Engine.dll` is already in the compilation —
the csproj even relies on it (`:184-187` "FluentGpu.Foundation/Hooks (Engine, referenced transitively)", `:517-518`,
`:522`). The compiler sees both definitions and picks the source one (CS0436), so every source-included file that uses a
`Signal<T>` is compiled against the **shim** — a value cell with no subscription graph — while the engine types those
files also touch (`Tok`, `ColorF`, `Motion`) are the real ones. That is the risk the RCA named: a test can pass against a
`Signal<T>` whose `Value` setter never notifies, and the same code in the app behaves differently.

**The shim's other justification is also stale.** `:3-6` says referencing the Engine "would shadow the source-included
Backend's System.Threading.Channels / DealerRouter and break the build". The Engine IS referenced and the build passes.

**Fix (proper, not a suppression):**

1. Delete `src/apps/Wavee.Tests/VirtualCollectionSignalShim.cs`.
2. Build. The real `FluentGpu.Signals.Signal<T>` (`fluent-gpu/src/FluentGpu.Engine/Foundation/Signals/Signal.cs:39`) is
   API-compatible with the shim for every use the shim served: ctor `(T initial, IEqualityComparer<T>? comparer = null)`
   (`:45`), `Value { get; set; }` (`:51-55`), `Peek()` (`:70`), and `IReadSignal<T>` with `Value`/`Peek()` (`:4-10`).
   Writing `Value` outside a render context is safe: `SetIfChanged` (`:60-68`) consults `Tracking.Current` only through
   `BackwardsWriteGuard.CheckWrite`, which is a no-op with no current computation, and `NotifySubscribers` over an
   empty list does nothing.
3. Run `Wavee.Tests`. Two behaviours can differ from the shim and are the point of the exercise:
   - equal-value writes are now **coalesced** (the shim always overwrote). A test that asserted a re-render/notification
     after writing an equal value was asserting shim behaviour; fix the test, not the type.
   - `Value` reads subscribe when a computation is tracking — never the case in tests. No effect.
4. Rewrite the six csproj comments that cite the shim (`:94-97`, `:236`, `:250`, `:445`, `:510-512`, `:567`) to say the
   files bind to the engine's `Signal<T>` via the transitive `FluentGpu.WindowsApi` → `FluentGpu.Engine` reference.
   `SelectionModel.cs`/`VirtualCollection.cs` stay source-included (they live in `FluentGpu.Controls`, which is NOT
   referenced, and adding it would pull the whole control set); that is fine — they now compile against the real
   Signals exactly as they do in their home project (`FluentGpu.Controls.csproj:13` references the Engine).

If step 3 surfaces a `BackwardsWriteGuard` assertion in Debug (it is compiled in only when the engine's diag gate is
armed), that is a real finding about a test writing a signal from inside a tracked computation — fix the test.

### A.2 Nullable flow — three one-liners

| Site | Fix |
|---|---|
| `Wavee.Tests/CollectionFetcherTests.cs:48` CS8619 — `Log.Entries.Where(e => e.EventId == eventId).Select(e => e.EventId)` returns `IEnumerable<string?>` (`CapturingWaveeLog.Entry.EventId` is `string?`, `CapturingWaveeLog.cs:13`). | `.Select(_ => eventId)` — provably non-null and identical in value (the `Where` already pinned it). |
| `Wavee.Tests/RecentsGroupingTests.cs:72` CS8604 — `first.Members!` on `:71` does not flow past the `!` to `:72`. | Hoist: `var members = first.Members!;` before `:71` and use `members` on `:71-73`. |
| `Wavee.Tests/StoreLibrarySourceTests.cs:349` CS8604 — same pattern with `pl.Collaborators!` on `:348`. | `var collaborators = pl.Collaborators!;` before `:348`; use it on `:348-349`. |

### A.3 CS0649 — six "never assigned" fields, two root causes

**Dead test knobs (3)** — read once, set nowhere; the branch they guard is unreachable. Delete the field and the branch
(CLAUDE.md: delete obsolete code).

| Site | Delete |
|---|---|
| `Wavee.Tests/SidebarDataSourceTests.cs:37` `public bool Throw;` | the field and `:57` `if (Throw) throw new InvalidOperationException("boom");` |
| `Wavee.Tests/SidebarProjectionBinderTests.cs:61` `public bool Throw;` | the field and `:80` `if (Throw) throw …;` (`PartialThenThrow` on `:62/:81` is live — keep it) |
| `Wavee.Tests/VideoLoadSupersessionTests.cs:55` `public TaskCompletionSource? ClearGate;` | the field and `:75` `if (ClearGate is { } gate) await gate.Task.ConfigureAwait(false);` (`ApplyGate` is live) |

**App latches assigned only by files the test project does not compile (3).** `CrashProbe.Mode` is set in `Program.cs:191`,
`CrashPromptPolicy.ThisLaunch` in `Program.cs:370` + `ReportChrome.cs:51`, `HomeDaylistHydrator.WindowObserved` in
`App/DaylistNotifier.cs:35`; `Wavee.Tests` source-includes the declaring files (`csproj:92`, `:504`) but not the assigners,
so in that compilation the fields are write-never. CS0649 is a **field** diagnostic; the honest shape for a
composition-root latch is a property, which also stops any future "who writes this" question at the declaration.

```csharp
// Diagnostics/CrashProbe.cs:9
public static string? Mode { get; set; }
// Diagnostics/CrashPromptPolicy.cs:20
public static CrashPromptDecision ThisLaunch { get; set; }
// SpotifyLive/HomeDaylistHydrator.cs:418
internal static Action<string, long, string?>? WindowObserved { get; set; }
```

All three are assigned with `X = value` and read as values; no `ref`/`out` use exists (verified by grep), so the change
is source-compatible for every caller.

### A.4 CS0067 — `ConnectControllerTests.cs:63` `FakeDeviceMonitor.Changed` never used

The event is a required member of `IAudioDeviceMonitor` (`SpotifyLive/Audio/AudioDeviceMonitor.cs:57-60`) that the fake
never raises. The idiomatic non-suppression is an explicit interface implementation with empty accessors:

```csharp
event Action<Wavee.SpotifyLive.Audio.AudioDeviceEvent>? Wavee.SpotifyLive.Audio.IAudioDeviceMonitor.Changed { add { } remove { } }
```

(If a later test needs to raise it, switch back to a field-like event and raise it — the warning disappears the moment it
is invoked.)

### A.5 CA2022 — `Audio/LiveHttpAudioStreamTests.cs:279` inexact `Read`

`live.Read(new byte[16], 0, 16)` discards the count. The test only cares that the call parks, then throws
`ObjectDisposedException`; make the intent explicit: `_ = live.Read(new byte[16], 0, 16);`.

---

## B. Style-only analyzer warnings — one mechanical batch

### B.1 xUnit2013 — `Assert.Equal(0|1, x.Count)` → `Assert.Empty` / `Assert.Single` (11)

| File:line | Now | Fix |
|---|---|---|
| `HydrationLedgerTests.cs:124` | `Assert.Equal(1, b.Waits.Count);   // (Count, not Assert.Single: the element IS a Task, and an unawaited one reads as a bug)` | `_ = Assert.Single(b.Waits);` — the discard keeps the author's point (the returned `Task` is deliberately not awaited) and drops the comment. |
| `PlayableHydrationTests.cs:55` | `Assert.Equal(1, h.Envelopes.TrackCalls.Count);` | **delete the line** — `:56` already does `Assert.Single(h.Envelopes.TrackCalls)`. |
| `PlayableHydrationTests.cs:64` | `Assert.Equal(1, h.Envelopes.TrackCalls.Count);` | `Assert.Single(h.Envelopes.TrackCalls);` |
| `PlayableHydrationTests.cs:82` | `Assert.Equal(0, h.Envelopes.TrackCalls.Count);` | `Assert.Empty(h.Envelopes.TrackCalls);` |
| `PlayableHydrationTests.cs:123` | `Assert.Equal(0, h.Envelopes.TrackCalls.Count);` | `Assert.Empty(h.Envelopes.TrackCalls);` |
| `PlaylistHydrationTests.cs:332` | `Assert.Equal(1, h.Hydrator.Batches[1].Uris.Count);` | `Assert.Single(h.Hydrator.Batches[1].Uris);` (`:330-331` — 2 and 300 — stay) |
| `QueueOrderTests.cs:131` | `Assert.Equal(1, QueueOrder.Remove(mixed, idless).Count);` | `Assert.Single(QueueOrder.Remove(mixed, idless));` |
| `SidebarPinStoreTests.cs:69` | `Assert.Equal(1, s.Count);` | `Assert.Single(s);` — `SidebarPinStore : IReadOnlyList<SidebarPin>` (`SidebarPinStore.cs:25`) |
| `SidebarPinStoreTests.cs:379` | `Assert.Equal(0, s.Count);` | `Assert.Empty(s);` (`:378` `Assert.Equal(0, s.Unpin(…))` is an INDEX — leave it) |
| `SidebarPinStoreTests.cs:393` | `Assert.Equal(0, s.Count);` | `Assert.Empty(s);` (`:392` same caveat) |
| `SidebarPinSyncTests.cs:59` | `Assert.Equal(0, pins.Count);` | `Assert.Empty(pins);` (`pins` is a `SidebarPinStore`, `:34`) |

### B.2 xUnit2000 — expected/actual swapped (2)

| File:line | Fix |
|---|---|
| `SidebarPaneInvariantTests.cs:100` | `Assert.Equal(SidebarRowGeometry.ContentLane, SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(0));` — the constant is the expected value, the derivation is what is under test. `:101` has the same shape but did not warn (`ContentLaneEnd` is not a literal/const to the analyzer); swap it too for consistency. |
| `SidebarRowGeometryTests.cs:108` | `=> Assert.Equal(SidebarRowGeometry.ClassicHeight, SidebarRowGeometry.HeightFor(SidebarDensity.Cozy, true));` |

---

## C. Needs a judgment call — flagged, with the recipe, not blindly applied

### C.1 xUnit1031 — blocking task operations (22)

Two shapes. The first is a real conversion candidate; the second is deliberate and needs a different idiom.

**C.1.a Plain "await this" (6 sites, low risk, recommended).** Make the method `async Task` and `await`:

| File:line | Now | Fix |
|---|---|---|
| `ColdStoreSchemaV5Tests.cs:649` (in `Migration_KeepsALegacyTrackWithAnEmptyTitle_…`, `void` at `:625`) | `store.WarmComplete.GetAwaiter().GetResult();` | `await store.WarmComplete;` + `public async Task …` |
| `PersistenceWaveFTests.cs:256`, `:273` (`WarmReplay_…`, `void` at `:226`) | `Assert.True(store.WarmComplete.Wait(TimeSpan.FromSeconds(30)));` | `await store.WarmComplete.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);` — a timeout throws instead of returning false, which is the stricter assertion. |
| `PlaybackStubsTests.cs:86` (`NoLyricsProvider_ReturnsNull`, `void` at `:83`) | `Assert.Null(l.GetLyricsAsync("spotify:track:a").GetAwaiter().GetResult());` | `Assert.Null(await l.GetLyricsAsync("spotify:track:a"));` |
| `StoreVersionTests.cs:81`, `:85` (`void` at `:75`) | `svc.CheckAsync(…).GetAwaiter().GetResult();` / `svc.ApplyAsync(…).GetAwaiter().GetResult();` | `await svc.CheckAsync(…);` / `await svc.ApplyAsync(…);` |

**C.1.b "Must still be blocked" probes (16 sites) — a different idiom, decide once.** `Audio/LiveRingBufferTests.cs`
(`:86, 88, 89, 102, 104, 105, 137, 139, 140, 152, 154, 155` — four `void` tests, each `Task.Run(() => ring.Read(...))` then
`Assert.False(reader.Wait(150))` → poke → `Assert.True(reader.Wait(5000))` → `reader.Result`) and
`Audio/LiveHttpAudioStreamTests.cs:282, 286, 287` (already `async Task` at `:270`). `Assert.False(reader.Wait(150))` is the
whole point of the test — "the read parks" — and cannot become an `await`. The async-safe equivalent is:

```csharp
Assert.NotSame(reader, await Task.WhenAny(reader, Task.Delay(150)));   // still parked after 150 ms
ring.Write(Ramp(8));
Assert.Same(reader, await Task.WhenAny(reader, Task.Delay(5000)));      // woke within 5 s
Assert.Equal(8, await reader);
```

Four `void` tests become `async Task`, 16 lines change shape, and the timing semantics are identical. It is more than a
one-liner per site and touches the audio tests' intent, so it is a decision for whoever owns `LiveRingBuffer` — but it is
the only thing standing between `Wavee.Tests` and a clean `TreatWarningsAsErrors`. **Not** recommended: `<NoWarn>xUnit1031</NoWarn>`
— the analyzer catches real deadlocks in the xUnit v3 sync-context and these tests are exactly the shape it exists for.

### C.2 Engine `FluentGpu.VerticalSlice` warnings (`IL2026`/`IL2075`/`IL2070`, `CA2014`, `CS0649`)

Not produced by `Wavee.slnx` (see §0). `VerticalSlice` is the engine's in-app diagnostic suite, not shipped product; its
trimming annotations matter only if it is ever published NativeAOT, and `CA2014` (`stackalloc` in a loop) is a real but
bounded stack-growth concern in a dev tool. Owner: the engine repo (`..\fluent-gpu`, its own `CLAUDE.md` gates). Flag,
do not touch from here.

### C.3 Removing the opt-out — `Wavee.Tests.csproj:16-18`

After A + B + C.1.a land, the remaining warnings are exactly C.1.b's 16. Once those are converted (or explicitly accepted
by the audio owner), delete:

```xml
<!-- Pre-existing warning backlog (CS0436 VirtualCollectionSignalShim type-shim conflicts, xUnit1031 blocking
     waits); the apps baseline enforces warnings-as-errors — clear the backlog before removing this opt-out. -->
<TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

and let `Directory.Build.props` apply the baseline. Keep `<NoWarn>$(NoWarn);CA1416;xUnit1051</NoWarn>` (`:15`) — both are
justified in the comment above it and neither is part of this backlog.

---

## Order of work and gates

1. A.1 (delete the shim; fix any test that relied on always-notify) — the one item that can reveal a real bug.
2. A.2–A.5 and B.1–B.2 in one commit ("tests: clear the nullable/analyzer backlog").
3. C.1.a in one commit; C.1.b as a proposal to the audio owner (or the same commit if approved).
4. C.3.

Gate: `dotnet build Wavee.slnx -c Debug` **and** `-c Release` report `0 Warning(s)` for `Wavee.Tests`; `dotnet test
src/apps/Wavee.Tests/Wavee.Tests.csproj` green at the documented baseline (`docs/guide/releasing-wavee.md` §gates).
