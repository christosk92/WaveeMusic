# Wavee OS-surface bridges

Three siblings hang off `PlaybackBridge.Activate` (UI thread, after the Core subscriptions). They mirror the **unified** now-playing signals — never the engine `IMediaPlayer` — onto Windows chrome, and they all fail soft.

| Bridge | Surface | Cadence | HWND / thread |
|---|---|---|---|
| `SystemMediaControlsBridge` | SMTC flyout, lock screen, hardware keys | metadata on state edge; timeline ~1 Hz via `SmtcTimelineCoalescer` | `FluentApp.WindowHandle`; UI thread. OS callbacks hop through `post`. |
| `TaskbarBridge` | progress bar, play/pause overlay, thumb prev/play-pause/next (ids 1/2/3) | progress ~1 Hz (same coalescer); overlay/thumbs on state edge | same HWND + UI thread. `FluentApp.ThumbButtonClicked` / `TaskbarButtonCreated` (re-`SetThumbButtons` after explorer restart). |
| `JumpListBridge` | Tasks (Resume / Search) + "Jump back in" | track-boundary `OnStateChanged`, capped ~1/min | STA / UI thread. No HWND — Jump List is per-AUMID. |

`WaveeNativeBoot.Install(post)` runs once in the same `Activate` block (after SMTC). It sets `ToastNotifier.Default.ActivationDispatcher`, subscribes `Activated` → `DeepLinkChannel.Post` + `DeepLink.WakeWindow`, then `Register`s a stable CLSID. Registration failure never blocks playback.

## Activation grammar

`wavee://` is the **only** verb these bridges emit or consume. Do not register `spotify:` here.

| Source | Argument |
|---|---|
| Jump List → Resume | `wavee://resume` |
| Jump List → Search | `wavee://open?route=search` |
| Jump List → Jump back in | `wavee://open?route=album:<uri>` / `pl:<uri>` / `artist:<uri>` / `show:<uri>` |
| Toast click | whatever `launch=` / button `arguments=` carried, if it contains `wavee://` |
| Thumb buttons | **not** deep links — they call `IPlaybackPlayer` (Previous / Pause-or-Resume / Next) directly |

Intake is `DeepLinkChannel` (`App/DeepLink.cs`). Cold-launch toast args already land there from `Program.cs`; in-proc clicks need the activator `Register` above.

## Where the next OS mirror goes

1. New type next to the three, same ctor shape `(PlaybackBridge, IPlaybackPlayer, Action<Action> post)`.
2. Construct + `Activate(FluentApp.WindowHandle)` in the existing Windows block in `PlaybackBridge.Activate`.
3. Forward `OnStateChanged` / `OnPositionChanged` from `PushState` / `PushPosition` — the two call sites already sit next to the SMTC lines.
4. Edge-dedupe; reuse `SmtcTimelineCoalescer` for any ~1 Hz shell put; zero alloc on the position tick.
5. Emit `wavee://` (or call `IPlaybackPlayer`). Never a second intake.

Jump List category rows read `PlayLogStore` (via `PlaybackBridge.PlayLog`). Optional navigation recents: `JumpListBridge.AttachHistory(HistoryStore)` from the shell once it has the store — do not invent a fourth log.

Glyphs: `src/apps/Wavee/assets/taskbar/{prev,play,pause,next}.ico` (already globbed by the csproj `assets\**\*` Content include). Missing files skip the glyph; tooltips still work.

Toast CLSID (packaged manifest must match): `C8E4A91B-3D52-4F07-9B6A-1E7C4D8F2A30` (`WaveeNativeBoot.ToastActivatorClsid`).

## Not a playback bridge: `ReleaseNotifier` (scheduled pre-save drops)

`App/ReleaseNotifier.cs` is the fourth OS surface but hangs off the **library**, not `PlaybackBridge` — its trigger is a pre-save, not a track change. Attached from `WaveeApp` beside `PowerBridge.Attach`.

| Aspect | Rule |
|---|---|
| Delivery | `ToastNotifier.Schedule` — the **OS** holds the timer, so the toast fires with Wavee closed. No background task, service or tray resident is involved (and none is needed for this). |
| Opt-in | `WaveeSettings.NotifyReleaseDrops`, default **off**. Turning it off calls `UnscheduleAll()` — a switch that only gated future writes would still let yesterday's entries fire. |
| Trigger | `LibraryBridge.SetSaved` → `ReleaseNotifier.OnSavedChanged(uri, saved)`. That is the one chokepoint every heart / menu item / drop target already goes through; non-`spotify:prerelease:` uris no-op. |
| Reconcile | Every launch (`Attach` → `RequestReconcile`) rebuilds from the live saved-set + a fresh `IPreReleaseService` resolve. **Never trust what was scheduled last run**: dates slip, albums drop, users un-pre-save. Tags are stable (`drop:<prerelease-uri>`) so a reconcile REPLACES its own earlier entry instead of stacking duplicates. |
| Staleness | Gate on `PreReleaseLink.IsUpcoming`, never on the link merely existing — the kind-138 payload has a 30-day offline TTL, so a cached link outlives its own release. |
| Play target | `link.AlbumUri`, not the prerelease uri: the two ids are unrelated and only the album resolves once the record is out. |
| Cover | `ToastImageCache.Default.Localize(url)` — the unpackaged AUMID hero path rejects `http(s)`. A failed localize drops the hero, never the toast. |

`DaylistNotifier` is its sibling: the daylist card carries the end of its own window, so a rollover is a known future moment and schedules the same way (fed by `HomeDaylistHydrator.WindowObserved` — a hook, so the hydrator never reaches into the toast layer). Dials, gates and reconcile rules for both: **[notifications.md](notifications.md)**.

**Still missing (T1.6 second half):** the `TimeTrigger` background task that would *discover* things while closed — a followed-artist release that did not exist at schedule time. Anything whose moment is known in advance (release drops, daylist rollover) is scheduled instead and needs none of it. The task needs a packaged (MSIX) identity — `BackgroundTaskBuilder` and `windows.backgroundTasks` are unavailable unpackaged — plus a full-trust COM server and manifest entries in the `ops/` AppxManifest sources.
