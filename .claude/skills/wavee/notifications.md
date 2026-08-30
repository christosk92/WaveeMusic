# Wavee notifications

Two channels, one dial per topic. Settings → **Notifications**.

Code: `Wavee.Core/Notifications/NotificationPolicy.cs` (the rules, engine-free + tested), `App/NotificationPrefs.cs` (settings → policy, the ONE accessor), `App/ToastEscalator.cs` (live), `App/ReleaseNotifier.cs` + `App/DaylistNotifier.cs` (scheduled), `Features/Shell/SettingsPage.Notifications.cs` (the tab). Library: [wavee-native](../wavee-native/SKILL.md).

## The two channels

| Channel | Surface | Lifetime |
|---|---|---|
| **In-app** | the bell in the top bar (`NotificationCenterBridge` → `NotificationPanel`) | durable — it is the log |
| **Windows** | Action Center banner | transient — a banner disappears |

**The level is a LADDER, not two checkboxes:** `Off < In Wavee < Windows`, and Windows *implies* in-app. A banner without the durable record is incoherent — you would have told the user something and then lost it. `NotifyLevel` encodes this; there is no "toast but not in-app" state to get into.

## Why the dials are finer than the centre's pills

The centre groups for *browsing*: its "New" pill holds albums **and** podcast episodes, its "Spotify" pill holds concerts **and** followers. Those two lumps are the reason people turn a whole feature off instead of the one part that was too loud — a podcast listener gets an order of magnitude more episodes than albums. So the dials key on `NotifyTopic` (8 topics), and `NotificationPrefs.TopicOf(notification)` is the mapping from a concrete row to its topic. `ShowsCategory` maps back the other way for pill visibility, and a category stays visible while **any** of its topics is above Off.

## The topics

| Topic | Source | Delivery |
|---|---|---|
| `NewAlbums` | `NewReleaseNotification` (Album) | live |
| `NewEpisodes` | `NewReleaseNotification` (Episode) | live |
| `ReleaseDrops` | a pre-saved `spotify:prerelease:` album | **scheduled** |
| `Concerts` | `SpotifyUpdates.IsConcert` — announcements *and* "just days away" | live |
| `Followers` | the other `SocialNotification` rows | live |
| `DaylistRefresh` | the daylist card's own window end | **scheduled** |
| `AppUpdates` | `AppUpdateNotification` | live |
| `LibraryActivity` | `ActivityNotification` (the Undo trail) | in-app **only** |

`Concerts` is one dial and not two on purpose: the only honest discriminator the feed gives is the concert action target, and the doc on `SpotifyUpdates.KindOf` forbids classifying by title (server-localized prose). A split we cannot classify would be guesswork wearing a settings row.

`LibraryActivity` caps at in-app (`CeilingFor`) and its dial renders **two** segments — not three with one dead. It is a record of what the user just did; a banner about their own click would be absurd.

## The gates, in order

1. **`NotifyWindows`** (master, default **off**). Off ⇒ the app behaves exactly as it did before this page existed. This is the single opt-in the calm contract asks for; every per-topic default is therefore a *shape* for when the user opts in, not noise to switch off.
2. **The topic's level.**
3. **Quiet hours** — may wrap midnight. A **live** banner inside the window is suppressed (the centre still records it); a **scheduled** one is *shifted* to the end of the window via `QuietHours.NextAudible`. Shift, not drop: the album is still out, the user just hears at a civilised hour. `NextAudible` is idempotent, so the launch reconcile cannot walk a pending drop a day later each time.
4. **Windows itself.** `ToastNotifier.Default.Setting` is read on every render of the tab; a `DisabledFor*` result paints a warning banner with a jump to `ms-settings:notifications`, because without it every dial below is theatre. Group policy gets no button — it would be a dead end. `Unknown` is *not* treated as a problem (an unregistered notifier would cry wolf forever).

## Live escalation: the watermark is the design

`ToastEscalator.Consider` runs from `NotificationCenterBridge.Rebuild` — the one place every feed lands. `NotifyLastToastedMs` holds the newest timestamp already raised:

- Without it, every rebuild **and every relaunch** re-toasts the whole feed. That is the loudest possible bug in a notification system.
- It advances past everything **considered**, not just what was raised, so a topic silenced today cannot arrive as a backlog the moment it is enabled.
- A **zero** watermark means "never escalated": the first rebuild only records where the feed was. Enabling notifications never replays history.
- At most `MaxPerRebuild` (3) individual banners; the rest collapse into one summary. The Action Center is not a log — the bell is, and it already has every row. The walk runs oldest→newest so a truncated burst keeps the *freshest* items.

## Scheduled topics: never trust the existing schedule

A scheduled toast outlives the process, so both notifiers reconcile rather than assume:

- **Release drops** reconcile on every launch from the live saved-set + a fresh resolve (dates slip, albums drop, users un-pre-save while closed). Tags are stable per album, so a reconcile **replaces** its own entry instead of stacking.
- **Daylist** is keyed on the window end, so relearning the same window is idempotent and a genuinely new window replaces the old entry. It can only *revoke* on a settings change: re-scheduling needs a window end, which arrives with the next feed resolve.
- Turning a dial (or the master) down calls `ReconcileScheduled()`, which reaches **both** notifiers. A switch that only gated future writes would leave yesterday's entries to fire under today's stricter settings — the classic scheduled-notification bug.

## Adding a topic

1. Add to `NotifyTopic` (**append** — the values are persisted) and give it a default in `NotificationPolicy.DefaultFor`; set `CeilingFor` / `IsScheduled` if it is not an ordinary live topic.
2. Add a `SettingKey<int>` and map it in `NotificationPrefs.KeyFor`, then add the topic to `AllTopics` (that list **is** the UI order — one list, so a topic cannot be added and forgotten in the tab).
3. Map concrete rows to it in `TopicOf`, and fold it into `ShowsCategory`.
4. Give it a label/sub/glyph in `SettingsPage.Notifications.cs` and loc keys under `settings.notify.*`.
5. Live topic? `ToastEscalator.Present` needs a banner shape. Scheduled? It needs its own notifier with a reconcile.

**Do not add a dial you have not wired.** An unreachable switch is worse than no switch: it teaches the user something false about the product, and nothing in CI will ever catch it.

## Simulating an event (Settings ▸ Notifications ▸ Send event)

Every topic row expands to reveal **Send a test event**, which pushes a synthetic event down **the same code path a real one takes**. The feeds are remote and slow, so without this the dials are unfalsifiable — a correctly-silenced topic and a broken one look identical.

`App/NotificationSimulator.cs` orchestrates; `Wavee.Core/Notifications/SimulatedNotifications.cs` holds the builders (engine-free, therefore tested).

### Three shapes, matching the three real ones

| Topics | How it travels |
|---|---|
| NewAlbums, NewEpisodes, Concerts, Followers, AppUpdates | injected at `NotificationCenterBridge.Simulate` → the real `Rebuild()` runs untouched: same merge, same topic filter, same escalator |
| ReleaseDrops, DaylistRefresh | `ReleaseNotifier.SimulateSchedule` / `DaylistNotifier.SimulateSchedule` → the real OS timer, ~3 min out |
| LibraryActivity | `ActivityLog.Record` — its genuine trigger is the user's own action |

**Nothing calls `ToastNotifier.Show` directly.** A shortcut past the pipeline would prove only that Windows can paint a banner, which is not the question. (The old global "Send a test notification" button did exactly that and was removed when this landed.)

### Why the injection is at the bridge

Not at a feed service: the live ones are `internal sealed` and replace their snapshot wholesale on every fetch, so an injected row is wiped by the next `EnsureFresh` — which the panel calls on every open. Not a `SetInner` decorator either: `LiveSessionHost` and the logout path both call `SetInner` unconditionally, so it would be silently discarded on the next login, and it would leak into the sidebar's New-Releases source. The bridge already owns every stage, so injecting there is the only place that is both durable and faithful.

### Four invariants that fail silently

1. **Beat both watermarks.** `NotificationMerge.Build` gates unread on `Timestamp > lastSeenMs` — *strictly* — and both feed watermarks are stamped to "now" whenever the panel is opened or Mark-all-read is pressed. `SimulatedNotifications.NextTimestamp` handles the same-millisecond case; a plain `UtcNow` can tie and arrive already-read.
2. **Unique id per press.** The merge does not dedup, the panel keys rows on `"ntf:" + Id`, and the toast tag is `"live:" + Id` — a reused id makes Windows *replace* the previous banner, so the second press looks like it did nothing. (`AppUpdateNotification` fixes its own id to `"update"`, so that one legitimately replaces.)
3. **Topic is derived, not declared.** A concert needs `ConcertWireType` or a concert action target or `SpotifyUpdates.IsConcert` is false and it lands on **Followers**. Never classify by title — the real feed's titles are server-localized prose.
4. **Prime the toast watermark.** The escalator raises nothing while `NotifyLastToastedMs` is 0, so enabling notifications never replays history. A simulated event is *now*, not history, so the simulator sets the watermark to `now − 1` first — otherwise the very first press lands in the bell with no banner and reads as broken.

### Two safety rules

- **Never schedule for a topic that is dialled down.** Both scheduled notifiers *revoke* on a disallowed policy, so a naive simulate would destroy a genuinely pending real toast. `SendScheduled` checks the dial before going near them.
- **A simulated activity entry must not be undoable.** The undo for a real save calls `SetSaved(uri, false)`. The simulator records `ActivityKind.PlaylistCreate` (excluded from `ActivityEntry.IsUndoable`) against an unresolvable `wavee:simulated:` target, so no Undo is offered and there is no inverse to get wrong.

### The report is the feature

The confirmation names the stage that consumed the event — dropped by the dial, recorded in the bell only, bannered, held by quiet hours until HH:mm, or scheduled for HH:mm. It reports the **computed** delivery instant, never the requested one, because `QuietHours.NextAudible` can move a scheduled drop hours out. Banner counts come from `ToastEscalator.Consider`'s return value, so the message states what *happened* rather than what the caller predicted.
