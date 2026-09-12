# Shell overlays (command palette, profile menu, notifications, toasts, tips, play-link dialog, runtime banner/setup card) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Shell/WaveePalette.cs` (200) · `Features/Shell/WaveeCommands.cs` (292) ·
> `Features/Shell/ProfileMenu.cs` (341) · `Features/Shell/NotificationPanel.cs` (640) ·
> `Features/Shell/PlayLinkDialog.cs` (251) · `Features/Shell/PlaybackRuntimeBanner.cs` (121) ·
> `Features/Shell/PlaybackRuntimeSetupCard.cs` (1030) · `Features/Shell/PlaybackRuntimeSetupModel.Phase.cs` (20) ·
> `App/WaveeTips.cs` (188) · `App/WaveeTipsCore.cs` (100) · `App/ToastEscalator.cs` (255) ·
> `App/AppUpdateToasts.cs` (153) · `App/NotificationCenterBridge.cs` (377) · `Actions/PlayLinkActions.cs` (146)
> — **4,114 lines** owned outright (the 14 counts above sum to 4,114; §9.4 carries the same total), plus read-only dependencies (`Actions/Menus.cs` 1197 for the menu grammar,
> `App/NotificationPrefs.cs`, `App/SetupRuntimePresentation.cs`, `App/AppUpdateSurface.cs`,
> `Features/Shell/SettingsPage.About.cs:665` `StateSentence`).
> 0.3 target (settled, A9/A10/A18, plan §2): the named partial **`Shell/+Shell.Overlays.UI.cs`** (1,700: profile
> chip + menu + `Play ▸` cascade, logout confirm, play-a-link dialog, the playback-runtime banner and its gate, the
> digital-signature dialog, teaching tips, the in-app toast decision sites, and the mount of
> `+Screens/Setup.UI.Runtime.cs` — the setup card's body is NOT here), `Shell/Shell.Palette.cs` (600: the command
> palette), `Shell/Shell.cs` (CORE: `WaveeTipsCore`, A10) + `Shell/Shell.Host.cs` (SHELL: the `WaveeTips` host, A10),
> `Shell/Shell.UI.cs` (the notification panel and its rows — A9, not `+Shell.Overlays.UI.cs`), the named partial
> `+Screens/Setup.UI.Runtime.cs` (1,100: the runtime provisioning card BODY, A18 — written here in Wave 4, mounted
> by owner R's wizard in Wave 6), and `Platform/Notify.cs` + `Platform/Notify.Host.cs` (the one notification stack —
> policy, prefs, escalation, the feed fold, `AppUpdateToasts`; A9, shared with ch. 14) | Wave 4, owner I
> After Wave 0 every cited path lives at `src/apps/_old/Wavee/<same relative path>`.
>
> Cross-references (do not re-specify): `00-design-system.md` (tokens, type ramp, motion curves, CTAs),
> `18-shell-frame.md` (the 48-DIP merged chrome row, the trailing island, the bell button + its badge, the
> `MergedChromeLayout` ladder, the ZStack overlay lanes), `01-track-row.md` (context-menu chrome / `MenuFlyoutItem`
> grammar), `27-settings-and-diagnostics.md` (Settings › Notifications dials + "Send event" simulator, the playback-
> runtime diagnostics page), `28-setup-whatsnew-feedback.md` (the setup wizard's own Local-playback page, which hosts
> the SAME `PlaybackRuntimeSetupModel` with `OnWizardExit` set, and the What's New route these surfaces navigate to).

---

## 0. The non-negotiables

1. **The command palette is a 560-DIP acrylic card whose top edge sits exactly 64 DIP below the top of the overlay
   lane, horizontally centred, and it never moves.** `WaveePalette.cs:27` `PanelW = 560f`,
   `WaveePalette.cs:61` `Padding = new Edges4(0, Spacing.XXXL * 2, 0, 0)` = top 64. It is a ZStack **sibling lane**,
   not an overlay entry (`WaveeShell.cs:1475`) — which is why `WaveeShell.cs:2117` has to hand Escape to it by hand.
2. **The palette's selected row shows the word `Enter` at 11 px on its right edge, and only the selected row does.**
   `WaveePalette.cs:175-177`. The selection also flips the leading glyph from `Tok.TextTertiary` to
   `Tok.AccentDefault` (`WaveePalette.cs:169`) and paints `Tok.FillSubtleSecondary` behind the row
   (`WaveePalette.cs:164`). Three simultaneous tells for one selection; losing any of them flattens the list.
3. **The palette list never scrolls and never exceeds 8 rows.** `WaveeCommands.cs:18` `MaxResults = 8`, written into
   a caller-owned `dest` array (`WaveeCommands.cs:92`) with a linear scored insert (`WaveeCommands.cs:259`) — no
   LINQ, no per-keystroke allocation. A `>` prefix restricts the scan to commands; anything else appends a
   `Search for "…"` row **only when there is still room** (`WaveeCommands.cs:121`).
4. **The profile chip's name caption is hard-capped at 76 DIP.** `ProfileMenu.cs:38`
   `NameCapW = ChromeProfileNameW(90) − 8 − 6`. Issue #88: without the cap a long display name pushed the whole
   trailing island left of the budget the row had actually reserved.
5. **Bell and Friends exist in exactly one place at a time.** ≥ 1200 DIP they are trailing-island buttons
   (`MergedChromeRow.cs:151-152`); below, they are profile-menu rows (`ProfileMenu.cs:213-219`). Never both, never
   neither. `ShellResponsiveLayout.cs:42` `ChromeActionsEnterW = 1200f` with a 40-DIP promotion reserve
   (`ShellResponsiveLayout.cs:29`) — so 1200 is the width they *survive* down to; on a widening window they arrive
   at **1240** (see W4). The one exception to "never neither" is the shed stage: below the width at which the whole
   trailing island is dropped there is no chip to open the menu from either (`MergedChromeLayout.cs:134-136`).
6. **The profile flyout takes MENU chrome, not flyout-presenter chrome.** `ProfileMenu.cs:169`
   `Chrome: PopupChrome.Flyout` + `ConstrainToRootBounds = false` → the anchored 250 ms `MenuPopupThemeTransition`
   unfold over a windowed DWM transient-acrylic popup. `PopupChrome.Popup` would give it 83 ms of nothing, then an
   83 ms fade across a 367 ms 50 px slide — visibly unlike every other menu in the shell.
7. **The notification panel is 380 DIP wide, its feed scrolls at most 460 DIP, and the whole panel caps at 520.**
   `NotificationPanel.cs:47,85,92`. Header, filter pills and the pending-sync line are *outside* the scroller; only
   the rows move.
8. **Every notification row carries a trailing 8-DIP slot whether or not it is unread.** `NotificationPanel.cs:202-204`
   — an unread dot in `Tok.AccentDefault`, or an 8-wide `Shrink = 0` spacer. Text never reflows when a row is read.
9. **Notification rows enter downward and leave upward, and reorder by spring.** `NotificationPanel.cs:196-198`
   `Enter = EnterExit(Dy: 6, Opacity: 0)`, `Exit = EnterExit(Dy: −4, Opacity: 0)`,
   `Layout = LayoutTransition.Slide` (spring 0.30 s / 0.85 damping, `LayoutTransition.cs:74`).
10. **One failed play is one toast.** `PlayLinkActions.cs:115` `FailureToastKey = "wavee.play.failed"` — the card
    lane, the deep-link lane and `PlaybackBridge.NotifyPlaybackError` all coalesce onto it; the FIRST (most specific)
    sentence survives, the newer action is adopted (`Toast.cs:179-199`).
11. **The update toast is one card for the whole lifecycle.** `DedupeKey = "update"`
    (`NotificationCenterBridge.cs:237`); the download card is mounted once and its bar binds to
    `UpdateProgress` (`NotificationCenterBridge.cs:58, 254`) — twenty progress ticks cost twenty float writes, not
    twenty reconciles.
12. **A toast never covers the player bar.** `WaveeShell.cs:482` `Toast.EdgeInset = WaveeSize.PlayerBarH` (72) on top
    of the engine's 24-DIP dock → 96 DIP of bottom padding, bottom-right, max 3 visible, 8-DIP gap
    (`Toast.cs:82,299,318`).
13. **One teaching tip at a time, once per launch, never again once acknowledged.** `WaveeTipsCore.cs:94`
    `ShouldShow`. The tip never scrims and never steals focus (`WaveeTips.cs:30`, `FocusTrap: false` +
    `DismissBehavior.None`) and it is scheduled through a **double post** so it rises over a page that has already
    painted (`WaveeTips.cs:129`).
14. **The runtime banner floats; it never reflows the page.** `WaveeShell.cs:1396-1405` — a
    `HitTestPassThrough` lane, top-centred, `Padding = (0, 48+8, 0, 0)` to clear the merged chrome row, inner
    `MaxWidth = 560`. It is an opaque `Tok.FillSolidBase` plate under the InfoBar's translucent caution tint plus
    `Elevation.Flyout`, because it overlays artwork (`PlaybackRuntimeBanner.cs:36-42`).
15. **The setup dialog has exactly ONE command row, always, and its buttons change with the phase.**
    `PlaybackRuntimeSetupCard.cs:938-994`. `ContentDialog`'s own Primary/Secondary/Close are cleared
    (`:39-41`) and `d.Footer` owns everything, so a phase swap never re-opens the dialog. Dismissal is blocked
    while `IsBusy` (`:46`).
16. **The 412-DIP progress bar is the dialog's content width, exactly.** `DialogWidth 460 − 2×Pad 24 = 412` =
    `RuntimeProgressWidth` (`PlaybackRuntimeSetupCard.cs:588`, `ContentDialog.cs:111`). The bar spans the copy.
    460 is **rung 2 of the app-wide modal width ladder (§3.1)**, not a free number: the engine clamps every
    `DialogWidth` into 320…548 (`ContentDialog.cs:283`), four rungs are sanctioned app-wide, and a fifth width —
    or a bar width typed independently of its rung — is a regression.

---

## 1. Anatomy

### 1.1 — 0.2.9 composition tree

```
WaveeShell.Render  (Features/Shell/WaveeShell.cs:475)
└─ Ui.ZStack(...)                                                    WaveeShell.cs:1474   the overlay lanes, bottom→top
   ├─ tinted (page + chrome column)                                  WaveeShell.cs:1381   — see 18-shell-frame.md
   ├─ immersiveLyricsLayer                                           WaveeShell.cs:1441   — see 22-lyrics.md
   ├─ runtimeBannerLayer   Grow 1, HitTestPassThrough, Justify Start, AlignItems Center, Pad(0,56,0,0)
   │  └─ BoxEl MaxWidth 560                                          WaveeShell.cs:1403
   │     └─ PlaybackRuntimeChrome(settings) : Component              PlaybackRuntimeBanner.cs:58   gate + setup-request watcher
   │        └─ PlaybackRuntimeBanner.Build(status, onSetUp, onDismiss)  PlaybackRuntimeBanner.cs:24
   │           └─ BoxEl  Fill FillSolidBase, r4, Elevation.Flyout, ClipToBounds
   │              └─ InfoBar.Create(Warning, title, msg, onClose, closable, Button.Accent("Set up"))  :45
   ├─ fileDropLayer                                                  WaveeShell.cs:1418   — see 18-shell-frame.md
   ├─ WaveeCommandPalette.Overlay(_paletteOpen, GoNav, _actions, _settings, ToggleTheme)   WaveePalette.cs:56
   │  └─ BoxEl  Grow 1, Direction 1, AlignItems Center, HitTestVisible=false, Pad(0,64,0,0)
   │     └─ WaveeCommandPalette : Component                          WaveePalette.cs:18
   │        └─ Popup.Create(anchor 560x0, content, isOpen, BottomEdgeAlignedLeft,
   │                        PopupOptions(FocusTrap:true, Chrome:Popup))    WaveePalette.cs:42
   │           └─ WaveePaletteContent : Component                    WaveePalette.cs:73   (fresh mount per open)
   │              ├─ TextBox.Create(query, TextBoxOptions{Placeholder, Width 544})   WaveePalette.cs:190
   │              ├─ BoxEl Height 2                                  WaveePalette.cs:195
   │              └─ BoxEl Direction 1, Gap 1                        WaveePalette.cs:196
   │                 └─ row × count  (BoxEl h32 r4, glyph 14 + label 14 + "Enter" 11)   WaveePalette.cs:158
   │                    └─ OR the single "No matching commands" row   WaveePalette.cs:145
   ├─ ActionServicesOverlayBinder                                    WaveeShell.cs:1477
   ├─ SidebarOnboardingChrome / SetupChrome / ReportChrome / AfterUpdateChrome   WaveeShell.cs:1481-1497
   └─ (engine) OverlayHost's own top-Z lanes:  ToastHost  +  every popup opened through Overlay.Service
      ├─ ToastHost (auto-mounted)                                    Toast.cs:395
      │  └─ ToastController.BuildLane()  Pad(24,24,24,24+72), Justify End, AlignItems End   Toast.cs:309
      │     └─ strip  Direction 1, Gap 8, OnHoverMove→pause          Toast.cs:297
      │        └─ Card × ≤3   MinW 300 / MaxW 380, r4, FillSolidTertiary, Elevation.Flyout   Toast.cs:346
      │           ├─ InfoBar.Create(severity, title, message, close, action, availableWidth 380)  Toast.cs:334
      │           └─ OR BuildCustomBody(severity, custom)            Toast.cs:371
      │              └─ NotificationCenterBridge.UpdateProgressCard  NotificationCenterBridge.cs:246
      │                 ├─ TextEl 13/600                             NotificationCenterBridge.cs:253
      │                 └─ ProgressBar.Create(UpdateProgress, 240)   NotificationCenterBridge.cs:254
      ├─ TeachingTip.Show(overlay, anchor, configure)                WaveeTips.cs:143      PopupChrome.TeachingTip
      ├─ NotificationPanelLauncher.Open(overlay, nc, anchor, handle) NotificationPanel.cs:29
      │  └─ NotificationPanel : Component  Width 380, MaxHeight 520  NotificationPanel.cs:45
      │     ├─ Header(nc, filter)            Pad(14,12,8,8), title 15/700 + LinkButton×1..2   :126
      │     ├─ FilterPills(nc, filter)       Pad(12,2,12,8), Gap 6, 5 pills h26 r13           :146
      │     ├─ PendingSyncLine()             Embed.Comp(PendingSyncRow) Key "nc-pending-sync" :102
      │     └─ body = ScrollEl MaxHeight 460, AutoEdgeFade, ScrollKey "notifications"         :83
      │        └─ BoxEl Direction 1, Width 380, Pad(6,4,6,8), Gap 2
      │           └─ RowFor(n, …) per notification                                            :174
      │              ├─ Card(key, UpdateRow(u, svc, go, close),  unread, null)                :177 / :210
      │              ├─ Card(key, SocialRow(s, now, go),         unread, ClickSocial)         :178 / :303
      │              ├─ Card(key, NewReleaseRow(r),              unread, ClickRelease)        :179 / :344
      │              └─ ActivityCard(a, …)  → card  (+ ActivityDetail when expanded)          :180 / :392,:452
      │        └─ OR EmptyState(filter, socialState, whatsNewState)  MinHeight 120, Pad 24    :591
      ├─ ProfileMenu.OpenMenu → overlay.Open(anchor, MenuContent, BottomEdgeAlignedRight,
      │                          PopupOptions(FocusTrap, LightDismiss, Chrome:Flyout){ConstrainToRootBounds=false})
      │  └─ BoxEl MinW=MaxW 304, Pad(0,6,0,6)                        ProfileMenu.cs:231
      │     ├─ AccountHeader(name, premium, avatar, email)           ProfileMenu.cs:255
      │     │  ├─ PersonPicture.Create("", 40, displayName, imageSourcePath)  :263
      │     │  └─ BoxEl Direction 1, Gap 2, Grow 1, Basis 0, Clip     :264
      │     │     ├─ TextEl name 14/600 TextPrimary, 1 line, char-ellipsis :273
      │     │     ├─ TierLine(premium)  Gap 5: star 10 | 10-spacer + badge 12  :304
      │     │     └─ TextEl email 12 TextTertiary, 1 line            :283
      │     ├─ HeaderSeparator()  h1, Margin(8,4,8,4), StrokeDividerDefault   :248
      │     └─ MenuFlyout.Create(items, close, 304)                  ProfileMenu.cs:243
      │        rows: Account · Settings · [Play ▸] · —— · [Notifications] · [Friends] · —— · Theme · —— · Log out
      │        Play ▸ submenu items built by PlayItems()             ProfileMenu.cs:112
      ├─ PlayLink.Open → overlay.Open(NodeHandle.Null, PlayLinkDialog, BottomCenter,
      │                   PopupOptions(FocusTrap, Modal, Chrome:Modal))        PlayLinkDialog.cs:38
      │  └─ PlayLinkDialog : Component   W 420 / min 360 / max 480, r8, FillSolidBase,
      │     1px StrokeSurfaceDefault, Elevation.Dialog, Pad 24, Gap 12          PlayLinkDialog.cs:213
      │     ├─ TextEl title 20/600                                   :220
      │     ├─ TextBox.Create(text, null, {Placeholder, Width 372, OnCommit→Submit})  :226
      │     ├─ status row  MinHeight 18, TextEl 12 TextSecondary 1 line          :232
      │     └─ button row  Gap 8, Justify End, MarginTop 8: [Open in browser?] Cancel Play(accent) :243
      ├─ ProfileMenu.ConfirmLogout → overlay.Open(Null, ConfirmCard, BottomCenter, Modal)  ProfileMenu.cs:92
      │  └─ ConfirmCard  W 380 / 320..420, r8, FillSolidBase, Elevation.Dialog, Pad 24, Gap 12  :321
      └─ PlaybackRuntimeSetupCard.Open → ContentDialog.Show(overlay, d => …)   PlaybackRuntimeSetupCard.cs:35
         │  d.DialogWidth 460 · PrimaryText/SecondaryText/CloseText "" · Closing cancelled while IsBusy
         ├─ d.Content = SetupBody(model) : Component                 PlaybackRuntimeSetupCard.cs:522
         │  └─ switch(PhaseSig.Value) →
         │     Offer  → Body(offerBody)                              :532
         │     FetchingCatalog → Column(Lead, ProgressMetricRow, ProgressBar.Indeterminate(412))  :594
         │     Downloading → Column(Lead, ProgressMetricRow, bar, caption 12 TextTertiary)        :601
         │     Verifying → Column(Lead, ProgressMetricRow, Indeterminate, VerifyDetailBox)        :619
         │     Untrusted → Status(StatusWarning@18, SystemFillCaution, heading, body)             :674
         │     Ready → ReadyBadge · [UpToDate body] · ReadyDetailBox · ReadyLinks                 :679
         │     Failed → Status(StatusError@18, SystemFillCritical, heading, Error)                :741
         │     Advanced → Body · (BusyRow | VersionPicker | noPack | catalogUnreachable)
         │                 · 1px divider · LocalSourceRows×2                                      :857
         └─ d.Footer  = SetupFooter(model) : Component               PlaybackRuntimeSetupCard.cs:941
            └─ Row(left link?, right buttons…)  Gap 8, spacer Grow 1                              :1018
```

Model / service objects behind the tree:

```
NotificationCenterBridge  (App/NotificationCenterBridge.cs:24)   provided once at the app root via .Slot
  Items : Signal<IReadOnlyList<WaveeNotification>>      the merged, newest-first feed         :39
  UnreadCount : Signal<int>                             the bell badge (Activity excluded)    :41
  Filter : Signal<NotificationCategory?>                the active pill                        :103
  NowTick : Signal<long>                                bumped every 30 s while the panel is up :44
  SocialState / WhatsNewState : Signal<NotificationFeedState>   loading/error/offline vs empty  :47,49
  UpdateProgress : FloatSignal                          the sticky toast's bar                 :58
  ← NotificationMerge.Build (Wavee.Core/Notifications/NotificationMerge.cs:11)   PURE
  → ToastEscalator.Consider (App/ToastEscalator.cs:39)   OS banners, watermarked
  → AppUpdateToasts.Plan   (App/AppUpdateToasts.cs:55)   PURE in-app-toast decision table

PlaybackRuntimeSetupModel  (PlaybackRuntimeSetupCard.cs:56 + .Phase.cs:9)
  PhaseSig : Signal<Phase>          Offer FetchingCatalog Downloading Verifying Untrusted Ready Failed Advanced
  CatalogSig : Signal<CatalogState> NotFetched Fetching Loaded Failed
  Error : Signal<string?> · Received/Total : Signal<long> · DownloadLabel : Signal<string?>
  SelectedPackIndex : Signal<int> · UpToDate : Signal<bool>
  SupportedPacks : IReadOnlyList<PlayPlayRuntimeCatalogEntry> · AnyForOtherArch : bool · ActiveEntry

PlaybackRuntimeBannerState.Epoch : Signal<int>  (PlaybackRuntimeBanner.cs:17)
  IAppSettings writes are not signals — banner ✕ / "Not now" / Remove all Bump() this so the floating chrome
  re-evaluates on the next frame.

WaveeTips  (App/WaveeTips.cs:50)   process-wide, UI-thread-only, no locking
  _armed : HashSet<string>   per-launch latch          _activeId / _activeHandle   the ONE slot
```

### 1.2 — 0.3 target tree

Props freeze at mount (`..\fluent-gpu\docs\design\subsystems\component-props-contract.md`). The column marked
**live-in** says how a change actually reaches the node.

| 0.3 node | File | Shape | Inputs | Live-in |
|---|---|---|---|---|
| `Shell.Palette.Table` | `Shell/Shell.Palette.cs` **CORE** | `static Entry[] BuildIndex(ModuleRegistry?)` | registry | rebuilt per open |
| `Shell.Palette.Filter` | `Shell/Shell.Palette.cs` **CORE** | `static int Filter(Entry[], string, Entry[] dest, Entry scratch)` | pure | — |
| `Shell.Palette.Invoke` | `Shell/Shell.Palette.cs` **SHELL** | `static void Invoke(in Entry, in Host)` | `Host` readonly struct | — |
| `Shell.PaletteOverlay` | `Shell/Shell.Palette.cs` | `static Element PaletteOverlay(Signal<bool> open, …)` | `Signal<bool>` | Signal |
| `Shell.PaletteCard` | `Shell/Shell.Palette.cs` | `sealed class PaletteCard : Component` | `Host` (reference-stable), `Action Close` | remounted per open by `Popup` |
| `Shell.ProfileChip` | `Shell/+Shell.Overlays.UI.cs` | `sealed class ProfileChip : Component` | `IReadSignal<ChromeLayout>`, `Action toggleTheme`, `Action toggleFriends` | Signal (`_layout.Value` read in `Render`) |
| `Shell.ProfileMenuBody` | `Shell/+Shell.Overlays.UI.cs` | `static Element ProfileMenuBody(…)` | plain values captured at **open** time | rebuilt by `overlay.Open`'s content thunk each open |
| `Shell.Notifications` | `Shell/Shell.UI.cs` (A9: the panel and its rows stay here, not in `+Shell.Overlays.UI.cs`) | `sealed class NotificationPanel : Component` | none (all from context) | `UseContext(Notifications.Slot)` + Signals |
| `Shell.NotifyLauncher` | `Shell/Shell.UI.cs` | `static void OpenPanel(IOverlayService, Func<NodeHandle>, Ref<OverlayHandle?>)` | anchor **thunk** (not a node) | thunk re-reads |
| `Shell.PlayLink` | `Shell/+Shell.Overlays.UI.cs` + `Platform/Modules.cs` **CORE** rules | `static void Open(…)` + `sealed class PlayLinkDialog : Component` | open-time constants only | none needed — see §9 |
| `Shell.RuntimeChrome` | `Shell/+Shell.Overlays.UI.cs` | `sealed class RuntimeChrome : Component` | `IAppSettings` (reference-stable) | `Playback.RuntimeStatus` Signal + `RuntimeBannerEpoch` Signal |
| `Shell.RuntimeSetup` | `Screens/+Setup.UI.Runtime.cs` (A18: the card BODY, written by owner I in Wave 4; `+Shell.Overlays.UI.cs` keeps only the banner, the gate and the mount) | `static OverlayHandle Open(…)` + `SetupBody`/`SetupFooter` | `RuntimeSetupModel` (reference-stable) | model Signals |
| `RuntimeSetupModel` | `Screens/Setup.cs` (**CORE** phase enum + presentation, owner R, Wave 6) / `Setup.Host.cs` (**SHELL** async work, owner R, Wave 6) | — | — | Signals |
| `Shell.Tips` | `Shell/Shell.cs` **CORE** (`TipsCore`) + `Shell.Host.cs` **SHELL** (`Tips`) | statics | — | process-static |
| `Shell.Toasts` | engine `Toast` static API — **no app host** | — | — | — |
| `Notifications` bridge | **DATA GAP** — see §7 | — | — | — |

**Key remounts.** Only two in this surface, and both are load-bearing:
`NotificationPanel.cs:102` `Embed.Comp(() => new PendingSyncRow()) with { Key = "nc-pending-sync" }` (a stable key so
the row's own `LibraryBridge` subscription survives feed re-renders), and
`NotificationPanel.cs:185,432,447` the per-notification `Key = "ntf:" + id` / `"ntf:act:" + id` /
`"ntf:actwrap:" + id`, which is what makes the enter/exit terminals and the slide layout transition orphan **exactly**
the removed card.

---

## 2. Wireframes

≈ 8 DIP per monospace character. Every number annotated is from the code.

### W1 — Command palette, open, empty query @ 1440

```
                        anchor: BoxEl 560 x 0, AlignItems Center  →  x = (1440-560)/2 = 440
   y = 0  ┌ shell ZStack lane ────────────────────────────────────────────────────────────────┐
          │                    (HitTestVisible = false — the page below stays clickable)       │
   y = 64 │        ╔══════════════════════════════════════════════════════════════════════╗   │  ← Pad top 64 = Spacing.XXXL*2
          │        ║ acrylic Tok.AcrylicFlyout · 1px StrokeFlyoutDefault · r8              ║   │     WaveePalette.cs:61
          │        ║ Elevation.Flyout (blur 16 / dy 8 / #00000042 dark, #00000024 light)   ║   │     FlyoutSurface non-menu branch
          │        ║ ┌── pad 8 ──────────────────────────────────────────────────────────┐ ║   │     Padding = Spacing.S all round
          │        ║ │ ┌──────────────────────────────────────────────────────────────┐  │ ║   │
          │        ║ │ │ Search commands — type > for commands only                   │  │ ║   │  TextBox 544 x 32, r4
          │        ║ │ └──────────────────────────────────────────────────────────────┘  │ ║   │  placeholder, no header
          │        ║ │  ▲ 2  (BoxEl Height = Spacing.XXS)                                │ ║   │
          │        ║ │ ┌──────────────────────────────────────────────────────────────┐  │ ║   │  rows column, Gap 1
          │        ║ │ │  Home                                                  Enter │  │ ║   │  h32 r4, Fill FillSubtleSecondary
          │        ║ │ ├──────────────────────────────────────────────────────────────┤  │ ║   │  glyph AccentDefault @14
          │        ║ │ │  Search                                                      │  │ ║   │  glyph TextTertiary @14
          │        ║ │ │  Your Library                                                │  │ ║   │  label 14 TextPrimary, 1 line
          │        ║ │ │  Recents                                                     │  │ ║   │  pad (16,4,16,4), gap 8
          │        ║ │ │  Settings                                                    │  │ ║   │
          │        ║ │ │  Play                                                        │  │ ║   │  8 rows max (MaxResults)
          │        ║ │ │  Next                                                        │  │ ║   │  8*32 + 7*1 = 263
          │        ║ │ │  Previous                                                    │  │ ║   │
          │        ║ │ └──────────────────────────────────────────────────────────────┘  │ ║   │
          │        ║ └───────────────────────────────────────────────────────────────────┘ ║   │
          │        ╚══════════════════════════════════════════════════════════════════════╝   │
          │           560                                             total h = 8+32+2+2+2+263+8 = 317 (cap 460)
                                                       (pad 8 · field 32 · gap 2 · spacer 2 · gap 2 · rows 263 · pad 8)
```

Builtin order is the declaration order of `WaveeCommands.CreateBuiltins()` (`WaveeCommands.cs:195-217`), nav first:
Home · Search · Your Library · Recents · Settings · Play · Next · Previous · Shuffle · Repeat · Theme · Crossfade ·
Zoom in · Zoom out · Reset zoom · (NPV presentation) · (NPV next style) · New playlist · New folder = **19 builtins**
(`WaveeCommands.cs:53 BuiltinCount = 19`), plus every registry action accepting `NowPlaying`/`ActiveRoute`/`None`
(`WaveeCommands.cs:243`).

**The 19 glyphs and the five route keys** (`WaveeCommands.cs:197-216`) — the glyph column is half of what a row looks
like, so copy it rather than re-picking icons:

| # | id | glyph | note |
|--:|---|---|---|
| 1 | `nav.home` | `Home` E80F | route `home` |
| 2 | `nav.search` | `Search` E721 | route `search` |
| 3 | `nav.library` | `MusicNote` EC4F | route **`liked`** — not `library` |
| 4 | `nav.recents` | `Headphones` E7F6 | route `recents` |
| 5 | `nav.settings` | `Settings` E713 | route `settings`; label hard-coded `"Settings"` (§6.10) |
| 6–10 | `playback.*` | `Play` E768 · `Next` E893 · `Previous` E892 · `Shuffle` E8B1 · `RepeatAll` E8EE | |
| 11–12 | `settings.theme` / `.crossfade` | `Brush` E790 · `MusicNote` EC4F | |
| 13–15 | `settings.zoomIn` / `.zoomOut` / `.zoomReset` | `Add` E710 · `Remove` E738 · `Undo` E7A7 | no Zoom glyph exists in the bundled set (`:209`) |
| 16–17 | `settings.npvPresentation` / `.npvNextStyle` | `Picture` EB9F · `Album` E93C | tagged `NpvDiagnostics.SourcePalette` |
| 18–19 | `library.newPlaylist` / `.newFolder` | `Add` E710 · `Folder` E8B7 | |

A registry row's glyph is its manifest's, falling back to `Icons.More` E712 (`WaveeCommands.cs:79`); the
`Search for "…"` row is always `Icons.Search` E721 (`:125`).

**Two structural facts the picture cannot show.** (1) The registry the index is built from is
`UseContext(WaveeExtensionRegistry.Slot) ?? Host.Actions.Extensions` (`WaveePalette.cs:90`) — context first, the
services bag only as a fallback; the index, the `dest` array and the catalog scratch entry are `UseRef`-allocated
**once per mount**, and the mount is one per open (`:85-97`). (2) **The card is a hard 560 with no clamp**
(`WaveePalette.cs:27,185`): the lane centres it and `HitTestVisible = false`, but nothing narrows it, so under about
576 DIP of layout width the card is wider than the window. 0.2.9 never hit it (the shell's own floor is higher), and
0.3 must not "fix" it with a `MaxWidth` that changes the geometry at every normal width — reproduce the constant.

### W2 — Command palette, typed query "pl", 2nd row selected @ 1440

```
 ╔════════════════════════════════════════════════════════════════════════╗
 ║ ┌──────────────────────────────────────────────────────────────────┐   ║
 ║ │ pl                                                            ⊗  │   ║   TextBox delete button (EditableText)
 ║ └──────────────────────────────────────────────────────────────────┘   ║   focused: 2px AccentDefault under-bar
 ║ ┌──────────────────────────────────────────────────────────────────┐   ║   EditableText.cs:418-423
 ║ │  Play                                                            │   ║   score 0 (prefix)     ScoreOf → 0
 ║ │██New playlist                                             Enter ██│   ║   score 1 (contains) · SELECTED
 ║ │  Repeat all                                                      │   ║   score 2 (subsequence: p…l)
 ║ │  Search for “pl”                                                 │   ║   the catalog row, appended last
 ║ └──────────────────────────────────────────────────────────────────┘   ║   WaveeCommands.cs:121-130
 ╚════════════════════════════════════════════════════════════════════════╝
```

Ranking (`WaveeCommands.cs:277-283`): `IndexOf == 0 → 0`, `IndexOf > 0 → 1`, subsequence → `2`, no match → dropped.
Ties keep the table's declaration order (`InsertScored` inserts *after* equal scores — the scan is
`while (at > 0 && scores[at-1] > score)`, a strict `>`, `WaveeCommands.cs:264`).
`Search for “X”` is appended after the scored hits and only when `written < MaxResults` and `q.Length > 0` and the
query does not start with `>`.

Two input rules the ranking depends on (`WaveeCommands.cs:94-108`): a query **without** `>` is `q.Trim()`-ed (a
pasted `"  pl\n"` still matches), and a query **with** `>` skips only the spaces immediately after the `>` and is
*not* otherwise trimmed. The comparison is `ToLowerInvariant` on both sides against a pre-lowercased `LabelLower`
computed once at `BuildIndex` — never per keystroke. An empty query scores every row `0`, so the list is the first
8 builtins in declaration order (W1); `>` alone does the same over the builtins-plus-registry table with no catalog
row. A registry row with no manifest glyph falls back to `Icons.More` (`WaveeCommands.cs:79`).

### W3 — Command palette, `>` commands-only, zero hits @ 1440

```
 ╔════════════════════════════════════════════════════════════════════════╗
 ║ ┌──────────────────────────────────────────────────────────────────┐   ║
 ║ │ > zzzz                                                           │   ║
 ║ └──────────────────────────────────────────────────────────────────┘   ║
 ║ ┌──────────────────────────────────────────────────────────────────┐   ║
 ║ │      No matching commands                                        │   ║   13 TextTertiary
 ║ └──────────────────────────────────────────────────────────────────┘   ║   Pad(16,12,16,12)  WaveePalette.cs:147
 ╚════════════════════════════════════════════════════════════════════════╝   card h = 8+32+2+2+~37+8 ≈ 89
```

The screen reader hears `"Command palette"` once on open and then the throttled count
(`"N matching commands"` / `"No matching commands"`) on every query edit — `WaveePalette.cs:103-113`,
`Announcer.SayThrottled` (100 ms default, `Announcer.cs:35`).

### W4 — Profile chip, the three ladder forms

```
 ≥ 1360 (ChromeNameEnterW)          1200 ≤ w < 1360                  w < 1200 (ChromeActionsEnterW)
 ┌──────────────────────────┐       ┌────────────┐                   ┌────────────┐
 │ ( )  Christos Karapa…    │       │ ( )        │                   │ ( )        │
 └──────────────────────────┘       └────────────┘                   └────────────┘
   4 + 24 + 8 + ≤76 + 10             4 + 24 + 4 = 32                   4 + 24 + 4 = 32
   = ≤122 DIP                        = ChromeProfileChipW              bell + friends fold into the MENU
   h 32, r4, Interaction.Subtle      (budget adds ChromeProfileNameW 90 for the named form)
   ProfileMenu.cs:176-192            ShellResponsiveLayout.cs:67-72
```

Hysteresis: every stage promotes LATE and demotes at the raw threshold — `MergedChromeLayout.Resolve`
(`MergedChromeLayout.cs:51-68`) computes `candidate = StageFor(width)` and `reserved = StageFor(width − 40)`, then
`held = candidate && (old || reserved)`. A stage that is **off** therefore needs `width − 40 ≥ enter` to turn on, and
a stage that is **on** survives while `width ≥ enter`. Concretely: **the name appears at 1400 and survives down to
1360; bell/friends enter the row at 1240 and survive down to 1200.** (The three columns above are the steady-state
bands — the ENTER width of each is the band edge + 40.) `ChromeNameEnterW` / `ChromeActionsEnterW` are the raw
thresholds (`ShellResponsiveLayout.cs:31,42`), never the widths at which the affordance appears on a widening window.

**Zoom moves the whole ladder.** `Resolve` is fed `vpSig.Value.Width` — the **DIP** viewport
(`WaveeShell.cs:815`, `AppHost.cs:4484` `_lastViewportDip`) — and `FluentApp.SetZoom` re-derives the window's
effective scale (`FluentApp.cs:78-85`), so every threshold in this section is a zoom-scaled number. At 125 % a
1440-px window measures 1152 DIP: the name is already gone and bell/friends have already folded into the menu, with
no resize. The palette's `Ctrl +`/`Ctrl −` rows change the chrome ladder as a side effect — reproduce that, and do
not re-express the thresholds in physical pixels.

**A fourth form: no chip at all.** `StageFor` sheds fixed islands before it lets the tab viewport disappear — `newTab`,
then `trailing`, then `back` (`MergedChromeLayout.cs:130-136`). With `ShowTrailing` false `MergedChromeRow.Trailing()`
returns a zero-size, hit-test-invisible box (`MergedChromeRow.cs:142-143`): **the profile chip, and with it the only
home the bell and Friends fold into, is gone.** Reproduce the shed order; do not special-case the chip to survive it.

### W5 — Profile menu flyout @ 1440 (actions in the ROW, so no bell/friends rows)

```
                                                    anchored BottomEdgeAlignedRight to the chip
 ╔══════════════════════════════════════════╗  ← windowed popup: DWM transient acrylic + system shadow + rounded
 ║ presenter pad (0,2,0,2)                  ║    ConstrainToRootBounds = false   ProfileMenu.cs:171
 ║ ┌── body pad (0,6,0,6) ────────────────┐ ║    body MinW = MaxW = 304          ProfileMenu.cs:234-238
 ║ │                                      │ ║
 ║ │  ╭────╮                              │ ║  AccountHeader  Pad(14,10,14,10), Gap 12, AlignItems Center
 ║ │  │ CK │  Christos Karapasias         │ ║    PersonPicture 40 (initials "" → falls through to displayName)
 ║ │  ╰────╯  ★ Spotify Premium           │ ║    name   14/600 TextPrimary, 1 line, char-ellipsis
 ║ │          ohsnap2502@gmail.com        │ ║    tier   Gap 5 · star E734 @10 · badge 12
 ║ │                                      │ ║           premium ink #E6C26C dark / #8A6312 light  ProfileMenu.cs:29,300
 ║ │  ────────────────────────────────    │ ║           free    → Tok.TextSecondary "Spotify Free"
 ║ │                                      │ ║    email  12 TextTertiary, 1 line, char-ellipsis (omitted when null)
 ║ │   E77B  Account                      │ ║  HeaderSeparator h1, Margin(8,4,8,4), StrokeDividerDefault
 ║ │   E713  Settings                     │ ║
 ║ │   EC4F  Play                       › │ ║  MenuFlyout rows: h36, Margin(4,2,4,2), Pad(11,8,11,9), r4
 ║ │  ────────────────────────────────    │ ║    icon column 28 wide, glyph 16; label 14; chevron E974 @12
 ║ │   E706  Light theme                  │ ║    separator: Pad(0,1,0,1) + 1px line bled −4 each side  → 3 tall
 ║ │  ────────────────────────────────    │ ║
 ║ │   F3B1  Log out                      │ ║  glyph/label swap with the TARGET theme: Dark → Sun+“Light theme”
 ║ │                                      │ ║                                       Light → Moon+“Dark theme”
 ║ └──────────────────────────────────────┘ ║                                       ProfileMenu.cs:224-226
 ╚══════════════════════════════════════════╝
   304 + 2 border                              h = 2 + 6 + 76 + 9 + (5*40 + 2*3) + 6 + 2 ≈ 307
                                               (FIVE rows here — Account · Settings · Play · Theme · Log out)
```

### W6 — Profile menu flyout @ 1100 (folded: `ActionsInMenu`)

```
 ║ │   E77B  Account                      │ ║
 ║ │   E713  Settings                     │ ║
 ║ │   EC4F  Play                       › │ ║
 ║ │  ────────────────────────────────    │ ║   ← this separator exists ONLY when either fold row is present
 ║ │   EA8F  Notifications (3)            │ ║   label = Strings.Notifications.OverflowTitle(count) when unread > 0,
 ║ │   E716  Friends                      │ ║     else Strings.Notifications.Title   ProfileMenu.cs:215-217
 ║ │  ────────────────────────────────    │ ║   count is read with .Peek() at OPEN time (ProfileMenu.cs:151)
 ║ │   E706  Light theme                  │ ║
 ║ │  ────────────────────────────────    │ ║
 ║ │   F3B1  Log out                      │ ║
                                                h = 2+6+76+9+(7*40 + 3*3)+6+2 ≈ 390
                                                (SEVEN rows: + Notifications + Friends)
```

Settings is **always** a menu row (it has no row-only form); Pin has **no** menu row at all — it drops below the
threshold and only the tab/page context menu offers it (`ShellResponsiveLayout.cs:33-41`).

**The header's three unknown-data forms** (`ProfileMenu.cs:66-75`), all reached from the SAME render, so the card
never changes height between them:

| field | 0.2.9 when absent | where |
|---|---|---|
| display name | literally `"—"` (an em dash), in both the chip caption and the header name | `ProfileMenu.cs:66` |
| avatar URL | `""` → `PersonPicture` falls through to `displayName` initials (which, with `"—"`, draws a dash) | `ProfileMenu.cs:68,75,263` |
| email | the line is replaced by an empty `BoxEl` — the stack loses a row, it does not reserve one | `ProfileMenu.cs:282-290` |
| tier | there is no unknown: `user?.IsPremium ?? false` renders **"Spotify Free"** speculatively | `ProfileMenu.cs:67` |

The last two rows are the ones 0.3 must change, not copy — see §7.

### W7 — `Play ▸` cascade (2 modules installed that declare `match`)

```
 │   EC4F  Play                       › │──╮  opens after MenuFlyout.SubMenuShowDelayMs (HKCU MenuShowDelay,
 └──────────────────────────────────────┘  │  default 400 ms — MenuFlyout.cs:70), or instantly on click / Right
                                           ╰──╔══════════════════════════════╗   cascade overlap −4 DIP
                                              ║  E8A5  File…                 ║   AnchorOffsetX (PopupOptions)
                                              ║  E71B  Link…                 ║   → PlayLink.Open(…, null, play.placeholder)
                                              ║ ──────────────────────────── ║   ← only when ≥1 module qualifies
                                              ║  E774  YouTube…              ║   label = manifest menu.label,
                                              ║  E774  Twitch…               ║     else DisplayName + "…"
                                              ╚══════════════════════════════╝     PlayLinkActions.cs:63
```

The whole `Play ▸` row is **absent**, not disabled, when `LocalFileActions.CanPlayFiles(actions)` is false
(`ProfileMenu.cs:113`). Cascaded sub-menus use `ClosedRatio 0.67` (root menus 0.5) — `OverlayHost.cs:880`.

### W8 — Logout confirm, modal @ any width

```
   scrim: Tok.FillSmoke #0000004D, opacity 0→1 over 83 ms linear     OverlayHost.cs:1335, :1181
   ┌────────────────────────────────────────────────────┐
   │                                                    │  W 380 (min 320 / max 420), r8
   │  Log out of Wavee?                                 │  Fill FillSolidBase · 1px StrokeSurfaceDefault
   │                                                    │  Elevation.Dialog (blur 64 / dy 16 / #00000066 dark)
   │  You'll need to authorize Wavee again to stream    │  Pad 24, Gap 12          ProfileMenu.cs:321-339
   │  your library.                                     │
   │                                                    │  title 20/600 TextPrimary, wrap
   │                          ┌────────┐  ┌──────────┐  │  message 14 TextPrimary, wrap
   │                          │ Cancel │  │ Log out  │  │  buttons: Gap 8, Justify End, MarginTop 12
   │                          └────────┘  └──────────┘  │  Standard / Accent, both MinWidth 96, h32
   └────────────────────────────────────────────────────┘  loc: auth.logoutConfirmTitle / …Body / auth.logOut
                   380                                      h ≈ 24+26+12+40+12+12+32+24 ≈ 182
```

### W9 — Play-a-link dialog, idle with a clipboard seed

```
   ┌──────────────────────────────────────────────────────┐
   │                                                      │  W 420 (min 360 / max 480), r8, Pad 24, Gap 12
   │  Play a link                                         │  title 20/600 · loc play.title
   │                                                      │
   │  ┌────────────────────────────────────────────────┐  │  TextBox 372 x 32 (= 420 − 48)
   │  │ https://www.youtube.com/watch?v=…              │  │  seeded from the clipboard ONCE, at open time
   │  └────────────────────────────────────────────────┘  │  PlayLinkActions.PrefillFrom → only http(s) + a host
   │                                                      │  placeholder = module's, else play.placeholder
   │  (status line — 18 DIP reserved, empty)              │  MinHeight 18 so the card never jumps
   │                                                      │
   │                        ┌────────┐  ┌──────────────┐  │  Cancel (Standard, MinW 96)
   │                        │ Cancel │  │     Play     │  │  Play   (Accent,  MinW 96, MarginTop 8)
   │                        └────────┘  └──────────────┘  │  Play is DISABLED, not hidden, while the field is
   └──────────────────────────────────────────────────────┘  empty — PlayLinkActions.CanSubmit
                     420                                      h ≈ 24+26+12+32+12+18+12+8+32+24 ≈ 200
```

### W10/W11/W12 — Play-a-link, the three live states (only the status row + button row change)

```
 W10 busy      │  Looking up…                                       │   loc play.lookingUp; Play disabled
               │                        [ Cancel ] [   Play   ]     │   (busy ⇒ canPlay false)

 W11 matched   │  YouTube · Claude FM · LIVE                        │   PlayLinkActions.MatchStatus, " · " joiner
               │                        [ Cancel ] [   Play   ]     │   set BEFORE the play; card then closes

 W12 no owner  │  Nothing installed can play this link.             │   loc play.noOwner — an in-place answer,
               │                        [ Cancel ] [   Play   ]     │   never a toast FROM THE CARD (the card
                                                                        stays open and says so); the card-less
                                                                        deep-link lane, which has no status row,
                                                                        raises the SAME sentence as an
                                                                        Informational toast instead —
                                                                        PlayLinkDialog.cs:60,73

 W13 failed    │  (status cleared — the module's words went to the toast)                                     │
               │  [ Open in browser ]   [ Cancel ] [   Play   ]     │   only when ShellOpen.IsWebUrl(failedInput)
```

The escape-hatch button opens the input that **actually failed**, not whatever is in the box now — a separate
`failedInput` signal exactly so a post-failure edit cannot redirect it (`PlayLinkDialog.cs:118-120, 183`).

### W14 — Notification panel, All filter, mixed rows @ any width (anchored to the bell)

```
 ╔═══════════════════════════════════════════════════╗  380 wide · acrylic + 1px StrokeFlyoutDefault + r8
 ║ Notifications                     Mark all read   ║  header Pad(14,12,8,8), Gap 8
 ║                                                   ║   title 15/700 TextPrimary Grow 1
 ║ ( All ) (Updates) (Spotify) ( New ) (Activity)    ║   LinkButton 12/600 AccentTextPrimary, h28, r6, Pad(8,3,8,3)
 ║                                                   ║  pills Pad(12,2,12,8) Gap 6 · h26 r13 Pad(11,3,11,3)
 ╟───────────────────────────────────────────────────╢   selected: Fill AccentDefault + TextOnAccentPrimary
 ║ ┌───────────────────────────────────────────────┐ ║   rest:     Fill FillSubtleSecondary + TextSecondary
 ║ │ ╭────╮  An update is available            •   │ ║  scroller MaxHeight 460, AutoEdgeFade, content Pad(6,4,6,8)
 ║ │ │ ⬇  │  Wavee 0.3.0 “Crest” is available.     │ ║  card: MinHeight 56, Pad(10,8,10,8), r8, Gap 10
 ║ │ ╰────╯  [Update now] [What's new] [Later]     │ ║   GlyphChip 36x36 r18 FillSubtleSecondary, glyph 16
 ║ └───────────────────────────────────────────────┘ ║   title 13.5/700 1 line · body 12 TextSecondary wrap ≤3
 ║ ┌───────────────────────────────────────────────┐ ║   pill buttons h28 r14 Pad(12,4,12,4), 12/600
 ║ │  ( )   New Keenan Te show just announced  •   │ ║  social: 40 circle art (r20) + title 13 wrap ≤2
 ║ │        near you                               │ ║          + reltime 11/600 TextTertiary
 ║ │        2h ago                                 │ ║
 ║ └───────────────────────────────────────────────┘ ║
 ║ ┌───────────────────────────────────────────────┐ ║
 ║ │ ▭▭▭▭   Midnight Drive           [ SINGLE ]    │ ║  new release: 44x44 r5 art
 ║ │ ▭▭▭▭   Keenan Te                              │ ║   name 13.5/600 1 line · creator 12 TextSecondary 1 line
 ║ └───────────────────────────────────────────────┘ ║   TypePill Pad(9,2,9,2) r10 FillSubtleSecondary, Eyebrow
 ║ ┌───────────────────────────────────────────────┐ ║             12/16/600 + 30/1000em tracking, TextTertiary
 ║ │ ╭────╮  Added 3 songs to Late Night    12m    │ ║  activity: GlyphChip tint TextSecondary
 ║ │ │ +  │                              [Undo]   │ ║   summary 13 wrap ≤2 · right column Gap 4, AlignItems End
 ║ │ ╰────╯                                       │ ║   reltime 11/600 TextTertiary + Undo pill (non-accent)
 ║ └───────────────────────────────────────────────┘ ║
 ╚═══════════════════════════════════════════════════╝  panel MaxHeight 520
```

Row geometry: card inner = 380 − 12 (scroll pad) − 20 (card pad) = 348; the content column is
`348 − 10 (gap) − 8 (dot slot) = 330`.

Details the wireframe cannot show, all load-bearing:

- **The update row's button strip wraps.** `Direction 0, Gap 6, Wrap = true, Margin (0,4,0,0)`
  (`NotificationPanel.cs:277`). Three pills (`Update now` / `What's new` / `Later`) do not fit 330 DIP, so the third
  drops to a second line and the card grows. The progress strip that replaces it uses `Gap 8, MarginTop 6`
  (`:268`) — a different gap and a different top margin; they are not one shared row.
- **The social row's relative time is conditional.** It is appended only when `s.Timestamp > 0`
  (`NotificationPanel.cs:309`); a feed item with no timestamp is a one-line card, not a card with an empty line.
- **The type pill has five labels**, not two (`NotificationPanel.cs:379-389`): `EPISODE` for
  `NewReleaseKind.Episode`, else `AlbumType` upper-cased through `SINGLE` / `EP` / `COMPILATION`, with `ALBUM` as
  the fallback for anything else (including a null `AlbumType`). All five are loc keys `notifications.release.*` and
  all five are authored in CAPS in `en-US.json` — the Eyebrow style does **not** upper-case them.
- **Artwork is hash-seeded, never blank.** Both thumbnails go through `Surfaces.Artwork(image, seed, w, h, r)` with
  `seed = Id.GetHashCode() & 0x7fffffff` (`:320`) / `Uri.GetHashCode() & 0x7fffffff` (`:355`), so a row with no
  image still paints a stable per-item placeholder.
- **The panel with no bridge** (`nc is null`, a probe/headless host) is a bare `BoxEl { Width = 380 }` — no header,
  no pills, no empty state (`NotificationPanel.cs:65`).
- **The panel is rigid and the pill row cannot give.** The root sets `Width = MinWidth = 380` (`:92`), and every
  filter pill is `Shrink = 0` in a row that does not wrap (`:150,159-161`). At `en-US` the five pills fit 356 DIP of
  inner width with room to spare; a longer localisation (or a larger UI font) overflows the fixed panel rather than
  wrapping or ellipsising. Port the constraint as written and treat the overflow as a localisation risk, not as a
  layout to "improve" — narrowing the panel changes every row geometry in this section.
- **The update row top-aligns; every other row centres.** `UpdateRow`'s frame is `AlignItems = Start` (`:284`) so the
  36-DIP glyph chip sits level with the title of a three-line card, while the social / new-release / activity frames
  are `AlignItems = Center` (`:314,349,420`). One value, two visibly different cards.
- **The activity card's dot reads its own flag.** `UnreadDot(!e.Read)` (`:429`) — the entry's read bit, not the
  merged `n.IsUnread` every other row passes (`:177-179`). They agree today (`ActivityNotification.IsUnread` IS
  `!Entry.Read`, `NotificationModels.cs:52`); they are still two reads, and only one of them survives a re-author
  that routes everything through the merge.
- **`RowFor` has a silent default.** An unrecognised `WaveeNotification` subtype renders `new BoxEl()` — no card, no
  spacing, no error (`:181`). Port the arm.

### W15 — Update row, `Downloading` / `Installing` (progress replaces the buttons)

```
 ┌───────────────────────────────────────────────┐   glyph Refresh E72C, tint AccentDefault
 │ ╭────╮  Updating Wavee                    •   │   title = loc update.os.downloading
 │ │ ↻  │  Downloading Crest in the background.  │   body  = AboutUpdatePanel.StateSentence(snapshot)
 │ ╰────╯  Playback continues.                   │          (SettingsPage.About.cs:665 — ONE sentence, shared
 │         ▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░  62%             │           with Settings › About)
 └───────────────────────────────────────────────┘   ProgressBar.Determinate(pct/100, width 200)
   progress row: Gap 8, AlignItems Center, MarginTop 6   pct text 11 TextTertiary, FontFamily "Cascadia Code"
   NotificationPanel.cs:265-275                          — the ONLY monospaced string in this surface
```

There is nothing to press while a download runs: cancelling mid-stage is the deployment API's business
(`NotificationPanel.cs:263-264`).

**The update row is six rows, not two** (`NotificationPanel.cs:213-261`). One row per `AppUpdateState`; the body
sentence under every one of them is the same `AboutUpdatePanel.StateSentence(snapshot)`:

| state | glyph (tint) | title (loc) | trailing |
|---|---|---|---|
| `Available` | `Download` E896 (`AccentDefault`) | `notifications.update.availableTitle` | `Update now` (accent) · `What's new` · `Later` |
| `Snoozed` | `Download` E896 (`AccentDefault`) | same title | `Update now` (accent) · `What's new` — **no `Later`**, the user already answered |
| `Downloading` | `Refresh` E72C (`AccentDefault`) | `update.os.downloading` | the 200-DIP bar + `%` (W15) |
| `Installing` | `Refresh` E72C (`AccentDefault`) | `update.os.downloading` | the same bar + `%` |
| `Completed` | `StatusSuccess` F13E (`SystemFillSuccess`) | `notifications.update.completedTitle` | `What's new` (accent, also acknowledges) · `Dismiss` |
| `Failed` | `StatusError` F13D (`SystemFillCritical`) | `notifications.update.failedTitle` | `Retry` (accent) · `Dismiss` |

`None` / `Checking` never reach the panel (the bridge only mints the row above `State != None`,
`NotificationCenterBridge.cs:185`), but the row's own `_ =>` arm would draw `StatusInfo` F13F in `TextSecondary` with
an **empty title** — port the arm, do not invent copy for it. `body.Length == 0` collapses the sentence line to an
empty `BoxEl` rather than reserving it (`:294`).

### W16 — Activity card, expanded

```
 ┌───────────────────────────────────────────────┐  the card itself is always a Button (expand / collapse)
 │ ╭────╮  Added 3 songs to Late Night    12m    │  OnClick → expanded.Value = isExpanded ? -1 : e.Id
 │ │ +  │                              [Undo]   │  HoverFill WaveeColors.RowHover / Pressed RowPressed
 │ ╰────╯                                   •   │  Key "ntf:act:<id>"
 └───────────────────────────────────────────────┘
      ▼ Gap 2, wrapper Key "ntf:actwrap:<id>"
      ┌──────────────────────────────────────────┐  detail Margin(46, 0, 12, 6) — 46 aligns under the text column
      │ Late Night                     [ Open ]  │   target 12/600 TextSecondary wrap ≤2 + Open pill when routable
      │ Tracks                                   │   Eyebrow TextTertiary — SENTENCE case
      │ •  Midnight Drive                        │   12 TextSecondary, 1 line char-ellipsis
      │ •  Hold The Line                         │   at most 8 lines (NotificationPanel.cs:483)
      │ •  Slow Burn                             │
      └──────────────────────────────────────────┘   Gap 3
```

Failed / undone variants: a `StatusChip` follows the summary — `Pad(7,1,7,1)`, `r8`, `FillSubtleSecondary`, Eyebrow
tinted `Tok.SystemFillCritical` ("Failed") or `Tok.TextTertiary` ("Undone"). An undone summary is also
**struck through** and drops to `Tok.TextTertiary` (`NotificationPanel.cs:399-409`), and its glyph chip tint drops
from `Tok.TextSecondary` to `Tok.TextTertiary` with it (`:423`). The `Undo` pill appears only when
`e.IsUndoable` (`:415`) — a non-invertible entry's right column is the timestamp alone.

**The detail block has three body shapes, not one** (`NotificationPanel.cs:452-490`), stacked in this order and each
optional after the first:

1. the **target line** — `e.TargetName`, or the **raw `spotify:` uri** when the entry predates names (`:458`); the
   `Open` pill only when `RichText.RouteForUri` resolves AND a navigator is in context (`:466`);
2. a **rename line** when the payload carries both names — `Strings.Notifications.Activity.Detail.RenamedFrom`
   ("`{old} → {new}`"), 12 `TextSecondary`, wrapped (`:473-474`);
3. the **`Tracks` eyebrow + up to 8 bullet lines** (`"•  " + name`, or the uri when the row has no name), hard-capped
   at 8 with no "+N more" (`:475-484`). The label is loc `notifications.activity.detail.tracks` = **"Tracks"**, and
   `WaveeType.Eyebrow` takes the string's OWN casing and no call site may caps-transform it (`WaveeType.cs:43-54`) —
   so this eyebrow is sentence case, unlike the release type pills, which are authored in CAPS in `en-US.json`. Do
   not draw it as `TRACKS`.

An entry with none of the three still renders the target line, so expanding is never a visual no-op.

**The glyph chip's 11 activity glyphs** (`NotificationPanel.cs:534-547`) — copy the table, not the vibe:
`Save`/`Unsave` → `Heart` EB51 · `PlaylistAddTracks`/`PlaylistCreate` → `Add` E710 · `PlaylistRemoveTracks`/
`PlaylistDelete` → `Remove` E738 · `PlaylistMoveTracks` → `Sort` E8CB · `PlaylistRename` → `Edit` E70F ·
`PlaylistVisibility` → `Globe` E774 · `PlaylistCoverSet`/`PlaylistCoverClear` → `Picture` EB9F ·
`ContributorInvite` → `Link` E71B · anything else → `StatusInfo` F13F.

**The summary sentences** are `ActivitySummary` (`:492-532`) — 12 `ActivityKind` arms over `notifications.activity.*`,
with `Save`/`Unsave` splitting again by uri kind through the ONE parser (`EntityUri.KindOf`): Track (and the
`spotify:local:` scheme, which has no `EntityKind`) → `Liked "{name}"`, Album → `Saved "{name}" to your library`,
everything else (artist / playlist / show) → `Followed {name}`; a nameless entry falls back to the generic
`Added to your library`. Track counts come from `Strings.Detail.SongCount`, the playlist name from
`e.TargetName` or the literal `a playlist` (`notifications.activity.genericTarget`).

Four arms of that ladder the summary above still flattens (`NotificationPanel.cs:496-513`):

- **`PlaylistVisibility` is two sentences, not one** — `MadePublic(playlist)` / `MadePrivate(playlist)` chosen by
  `e.Payload?.NewIsPublic ?? false` (`:504-506`), i.e. a payload-less visibility entry reads "Made X private".
- **`PlaylistRename` names the NEW name**, `e.Payload?.NewName ?? playlist` (`:503`) — "Renamed a playlist to {name}";
  the old name only appears in the expanded detail's rename line.
- **The unsave half is authored, not derived** — `Removed "{n}" from Liked Songs` / `Removed "{n}" from your library`
  / `Unfollowed {n}`, with the nameless fallback `Removed from your library` (`:518-532`, `en-US.json`
  `notifications.activity.unsave*`). It is not "Saved…" with a prefix swapped.
- **There is a `_ => ""` arm** (`:512`): an `ActivityKind` this switch does not know renders a card with a 36-DIP
  glyph chip (`StatusInfo` F13F), a timestamp, a dot — and an **empty** summary line. Port the empty arm; do not
  invent copy for it, and do not collapse the card.

The rename detail line's format string is `{old}  →  {new}` with **two** spaces on each side of the arrow
(`en-US.json` `notifications.activity.detail.renamedFrom`) — a single-spaced re-author is a visible change.

**Expanding is a key swap, not a reveal.** The list child at that index is the card (`Key = "ntf:act:" + id`) while
collapsed and the *wrapper* (`Key = "ntf:actwrap:" + id`) while expanded (`:432,447`); the card is reparented under
the wrapper rather than kept in place. §5 therefore lists "no transition declared" for the expand itself, which is
true of the wrapper — but the keyed child at that slot changes identity on every toggle, so the card's own
`Enter`/`Exit` terminals (`:438-440`, a SECOND declaration beside the generic `Card`'s at `:196-198`) are in play.
**Frame-record a toggle on the kept 0.2.9 build before assuming a silent swap**, and reproduce whatever it does —
this is the one motion in the surface the source cannot settle on its own.

### W17 — Notification panel, the empty ladder

```
 ╔═══════════════════════════════════════════════════╗
 ║ Notifications                     Mark all read   ║
 ║ ( All ) (Updates) (Spotify) ( New ) (Activity)    ║
 ╟───────────────────────────────────────────────────╢   EmptyState: Direction 1, Width 380, MinHeight 120,
 ║                                                   ║   centred both axes, Pad 24, text 13/600 TextSecondary
 ║              You're all caught up                 ║   wrap, MaxWidth 300              NotificationPanel.cs:612
 ║                                                   ║
 ╚═══════════════════════════════════════════════════╝

 filter → message                                      loc key
 All     · either remote feed Idle/Loading → "Loading…"          notifications.loading
 All     · otherwise            → "You're all caught up"         notifications.empty.all
 Updates                        → "No app updates"               notifications.empty.updates
 Spotify · feed Idle/Loading    → "Loading…"                     notifications.loading
 Spotify · feed Error           → "Couldn't load these notifications."   notifications.feed.error
 Spotify · feed Offline         → "Sign in to see notifications from Spotify."  notifications.feed.offline
 Spotify · feed Loaded          → "No Spotify notifications"     notifications.empty.spotify
 New     · same 4-way ladder    → … / "Nothing new from your artists"   notifications.empty.new
 Activity                       → "No activity yet"              notifications.empty.activity
```

A remote category with zero rows is only *empty* when the feed actually loaded — a failed or offline fetch must say
so instead of masquerading as "no notifications" (`NotificationPanel.cs:592-594`).

**A silenced topic empties the panel too, and the panel never says why.** `NotificationCenterBridge.Rebuild` runs
`ApplyTopicVisibility` **after** the merge (`NotificationCenterBridge.cs:197,357-376`): every row whose
`NotificationPrefs.TopicOf` is dialled `NotifyLevel.Off` in Settings › Notifications is dropped from `Items` **and**
the unread count is recounted over what is left — a silenced topic must not keep the bell lit for rows the user
cannot see. The panel therefore shows the ordinary empty-state sentence for a filter whose topic is off, with the
remote feed state still `Loaded`. Two consequences for 0.3: the dial is a **branch that changes what is drawn** and
belongs in the parity sweep, and the filtering must stay on the model (the merge stays a pure feed fold; the UI must
not re-derive visibility per render).

### W18 — Notification panel with the pending-sync line

```
 ║ ( All ) (Updates) (Spotify) ( New ) (Activity)    ║
 ║  ↻  2 playlist changes still syncing              ║   Pad(14,4,14,8), Gap 8
 ╟───────────────────────────────────────────────────╢   Icon Refresh E72C @12 TextTertiary
                                                        text 12 TextSecondary Grow 1
                                                        loc notifications.pendingSync (ICU plural)
                                                        renders a 0-height, hit-test-invisible box at 0
                                                        NotificationPanel.cs:104-123
```

### W19 — Toast strip, bottom-right, 3 visible @ 1440 × 900

```
 ┌ window ─────────────────────────────────────────────────────────────────────────────┐
 │                                                                                     │
 │                                                    ┌──────────────────────────────┐ │  MinW 300 / MaxW 380
 │                                                    │ (i) Added to Late Night  Undo│ │  r4, Fill FillSolidTertiary
 │                                                    │                           ✕  │ │  1px StrokeSurfaceDefault
 │                                                    └──────────────────────────────┘ │  Elevation.Flyout
 │                                                          ▲ 8 (inter-toast gap)      │
 │                                                    ┌──────────────────────────────┐ │
 │                                                    │ (!) That link couldn't be    │ │  severity tint composed
 │                                                    │     played.      Try again ✕ │ │  ON the solid plate
 │                                                    └──────────────────────────────┘ │
 │                                                          ▲ 8                        │
 │                                                    ┌──────────────────────────────┐ │  newest NEAREST the docked
 │                                                    │ (✓) Local playback is ready ✕│ │  edge (bottom strips list
 │                                                    └──────────────────────────────┘ │  oldest→newest downward)
 │                                                                          ▲ 24       │  Toast.cs:293, :318
 │═════════════════ player bar (72) ═══════════════════════════════════════════════════│  + EdgeInset 72
 └─────────────────────────────────────────────────────────────────────────────────────┘  = 96 bottom padding
```

### W20 — Toast, the update download custom card

```
 ┌──────────────────────────────────────┐   BuildCustomBody: MinHeight 48, Pad 16, r4,
 │  ┌────────────────────────────────┐  │   Fill = SeverityVisuals.For(Informational).Background
 │  │ Downloading Crest…             │  │   = Tok.SystemFillAttentionBackground, 1px StrokeCardDefault
 │  │ ▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░     │  │   Toast.cs:371-387
 │  └────────────────────────────────┘  │   UpdateProgressCard: Direction 1, Gap 8, Pad(16,14,16,14)
 └──────────────────────────────────────┘     title 13/600 · ProgressBar.Create(UpdateProgress, 240)
                                              NotificationCenterBridge.cs:246-256
   NOTE: the two paddings COMPOSE — 16 (frame) + 16/14 (card) = 32 side / 30 top. Reproduce that, or the
   download toast is visibly tighter than every other toast in the strip.
   Sticky: DurationMs = 0 while Downloading / Installing / Failed (AppUpdateToasts.cs:81,88,102).
```

Three rules the strip enforces that the two wireframes above cannot show:

- **The custom card exists only while `Downloading`.** `CustomContent = downloading ? UpdateProgressCard : null`
  (`NotificationCenterBridge.cs:240`). `Installing` and `Failed` are sticky **standard InfoBar** toasts, not cards —
  same `DedupeKey`, so they replace the card's body in place on the same countdown-restarting coalesce.
- **A toast shows ONE action button**, always the first of the plan's list
  (`NotificationCenterBridge.cs:229`, `Toast.cs:328-330`); the notification row renders all of them. So the strip
  offers `Update now` while the panel offers `Update now / What's new / Later`, and the strip offers `Retry` while
  the panel offers `Retry / Dismiss`. `Failed` with `AppUpdateFailureKind.Metered` plans `[Retry]` alone — every
  other failure plans `[Retry, OpenReleasePage]`, of which only `Retry` is ever pressed from a toast
  (`AppUpdateToasts.cs:103-105`).
- **A toast can go vertical.** Unlike the runtime banner, the toast DOES pass `availableWidth: 380`
  (`Toast.cs:341`), so `InfoBar`'s width estimate is live: a long title+message pair flips the card to the vertical
  layout (title over message over button) even with no action. The other two vertical triggers apply as well —
  `contentItems <= 1` (a message with no title and no action) and the ≥ 60-character message beside an action
  (`InfoBar.cs:232-242`).
- **The 4th toast is queued, not dropped.** `MaxVisible = 3` bounds what is *visible*; the rest wait in a FIFO
  overflow queue and their countdowns are not armed until they become visible (`Toast.cs:82,240,268`).
  Coalescing matches across the whole queue, not just the visible window (`Toast.cs:176-178`).
- **A silenced dial kills the update toast outright.** `NotificationPrefs.Level(settings, NotifyTopic.AppUpdates)
  == NotifyLevel.Off` returns before `Toast.Show` (`NotificationCenterBridge.cs:227`) — the progress signal is still
  written first (`:223`), so a later un-silenced card resumes at the right percentage.
- **The four update toasts are four different SHAPES, decided by which of title/body/action `Plan` filled**
  (`AppUpdateToasts.cs:66-109` → `InfoBar.cs:229,238`). This is the `contentItems <= 1` half of the orientation rule
  biting inside this chapter, not a hypothetical:

  | state | title | body | action | items | layout |
  |---|---|---|---|--:|---|
  | `Available` | `Wavee {name} is available` | the **quad**, and only when it differs from `{name}` (`:72`) — otherwise `""` | `Update now` | 2–3 | horizontal |
  | `Downloading` | `Downloading {name}…` | `""` | none | — | **custom card** (no InfoBar at all) |
  | `Installing` | `""` | `Restarting to finish updating…` (30 chars) | none | **1** | **vertical** |
  | `Completed` | `Updated to Wavee {name}` | `""` | `What's new` | 2 | horizontal |
  | `Failed` | `""` | `FailureText(...)` — the 7 sentences + `0xHRESULT` | `Retry` | 2 | vertical whenever that sentence reaches 60 chars |

  So the update lane shows a title-only card, then a padded custom card, then a **message-only vertical** card, then
  a titled horizontal one — all on one `DedupeKey`. A re-author that gives every state a title and a body changes
  the layout of three of the five.

### W21 — Teaching tip, targeted under the playlist `Tune ▾` command

```
                       ┌──────────┐
                       │  Tune ▾  │  ← the anchor (DetailTracks.cs:3767 captures it with OnRealized)
                       └────┬─────┘
                           ╱▔╲          tail 16 x 8, pointing at the target (PreferredPlacement.Bottom)
        ┌──────────────────────────────────────────┐   MinW 320 / MaxW 336, MinH 40 / MaxH 520
        │ ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ ✕  │   1px top highlight #0DFFFFFF dark / #99FFFFFF light
        │  Tune this playlist                      │   title 16 SemiBold
        │  Adjust the mix's direction right from    │   subtitle 14
        │  here — Wavee rebuilds the track list.    │   content margin 12 all round
        └──────────────────────────────────────────┘   40x40 header close button, E711 @16
          SolidBackgroundFillColorTertiary body · 1px SurfaceStrokeColorDefault · r8 · Elevation.Flyout
          loc: detail.tuning.tipTitle / detail.tuning.tipBody       WaveeTips.cs:143-151
          IsLightDismissEnabled = false — the ✕, Escape, or the caller dismisses; the page below stays clickable
```

### W22 — Runtime banner, HORIZONTAL InfoBar (3 of the 4 messages) @ 1440

```
                          ┌───── 48 (merged chrome row) ─────┐
  y = 56 ┌──────────────────────────────────────────────────────────────────────────────┐   centred, MaxWidth 560
         │ opaque FillSolidBase plate · r4 · Elevation.Flyout · ClipToBounds             │   WaveeShell.cs:1396-1405
         │ ┌──────────────────────────────────────────────────────────────────────────┐ │
         │ │ (!)  Local playback setup   Local playback needs a     [ Set up ]     ✕  │ │   InfoBar Warning:
         │ │      ▲ title 14/600         ▲ message 14  one-time setup                 │ │   Fill SystemFillCautionBackground
         │ └──────────────────────────────────────────────────────────────────────────┘ │   #433519 dark / #FFF4CE light
         └──────────────────────────────────────────────────────────────────────────────┘   1px StrokeCardDefault, r4
           MinHeight 48 · root Pad(16,0,0,0) · icon 16 Margin(0,16,14,16) · panel Margin(0,0,16,0)
           title Margin(0,14,0,0) (first child drops its leading-left) · message Margin(12,14,0,0)
           action Margin(16,8,0,0) + Grow 1 · close 38x38 Margin 5, glyph E711 @16
           icon = F136 filled circle in Tok.SystemFillCaution + F13C status glyph in Tok.TextInverse
```

### W23 — Runtime banner, VERTICAL InfoBar (`NoSupportedPack` only)

```
  ┌──────────────────────────────────────────────────────────────┐
  │ ┌──────────────────────────────────────────────────────────┐ │   InfoBar flips vertical when
  │ │ (!)  Local playback setup                             ✕  │ │   hasAction && hasMessage &&
  │ │      Playback support isn't available for your Spotify   │ │   message.Length >= 60   (InfoBar.cs:237-239)
  │ │      version yet.                                        │ │
  │ │      [ Set up ]                                          │ │   panel Pad(0,14,0,18)
  │ └──────────────────────────────────────────────────────────┘ │   title m(0,0,0,0) · message m(0,4,0,0)
  └──────────────────────────────────────────────────────────────┘   action m(0,12,0,0), AlignSelf Start
```

The four messages and their lengths (`PlaybackRuntimeBanner.cs:28-33`):

| `ProvisioningOutcome` | loc key | text | chars | layout |
|---|---|---|--:|---|
| `ArchUnsupported` | `playback.runtime.wrongArch` | "This Spotify build doesn't match your device" | 44 | horizontal |
| `NoSupportedPack` | `playback.runtime.noPack` | "Playback support isn't available for your Spotify version yet." | 62 | **vertical** |
| `HashMismatch` / `SignatureInvalid` | `playback.runtime.unsupported` | "This playback runtime isn't supported" | 37 | horizontal |
| *(default)* | `playback.runtime.missing` | "Local playback needs a one-time setup" | 37 | horizontal |

`availableWidth` is **not** passed here (`PlaybackRuntimeBanner.cs:45`), so the width-estimate branch of the
orientation decision never fires — only the 60-character rule does. Reproduce that exactly; guessing on width gives
a different orientation on three of four messages.

### W24 — Setup dialog, `Offer`

```
  scrim FillSmoke #0000004D · card scale 1.05→1 over 250 ms FluentPopOpen, opacity 0→1 over 83 ms linear
  ┌──────────────────────────────────────────────────────────────┐  W 460 (clamp 320..548), MinH 184, MaxH 756
  │ content region  Fill Tok.FillLayerAlt · Pad 24                │  r8, Fill FillSolidBase,
  │                                                              │  1px StrokeSurfaceDefault, Elevation.Dialog
  │  Local playback setup                                        │  title 20/600, MaxLines 2, WordEllipsis,
  │                                                              │        Margin bottom 12
  │  Play tracks directly on this device. Wavee downloads a       │  body 13 Tok.TextSecondary, wrap
  │  small one-time playback component — nothing else is          │  (SetupBody.Body — PlaybackRuntimeSetupCard.cs:546)
  │  installed.                                                  │
  ├──────────────────────────────────────────────────────────────┤  1px Tok.StrokeCardDefault
  │ command row  Fill FillSolidBase · Pad 24 · Gap 8              │
  │ Advanced options          [ Not now ]  [ Download & set up ] │  link: HyperlinkButton, Margin(−11,0,0,0)
  └──────────────────────────────────────────────────────────────┘  buttons MinW 96, Height 32, Justify Center
      460                                                           accent carries TabIndex 1 (default button)
      content width = 460 − 48 = 412
```

**`Offer` is not always the opening phase.** `PhaseSig = new(bridge.RuntimeStatus.Value.IsReady ? Phase.Ready :
Phase.Offer)` (`PlaybackRuntimeSetupCard.cs:92`) — the dialog opened from Settings (or the banner's own
`OpenPlaybackRuntimeSetup` request) on a machine that already has the runtime opens **straight at W29 `Ready`**,
with `UpToDate` false so the "You're on the latest supported version." line is absent until a `Check for update`
lands on `AlreadyUpToDate`. The wizard-hosted model (`OnWizardExit` set) opens the same way — see
`28-setup-whatsnew-feedback.md`.

### W25 — Setup dialog, `FetchingCatalog`

```
  │  Checking what's available for your device…                   │  Lead: 16 Tok.TextPrimary, wrap
  │                                                              │  Column Gap = Spacing.M (12)
  │  Reaching the runtime catalog                        Arm64   │  ProgressMetricRow: label 13/600 TextPrimary
  │  ▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░    │    Grow 1, 1 line, char-ellipsis
  ├──────────────────────────────────────────────────────────────┤    value 12 TextSecondary, Shrink 0
  │                                              [   Cancel   ]  │  ProgressBar.Indeterminate(412)
                                                                    value = RuntimeInformation.ProcessArchitecture
```

### W26 — Setup dialog, `Downloading`

```
  │  Downloading…                                                │  loc playback.runtime.downloading
  │  Spotify 1.2.93.667 · Arm64                   12.3 / 84.0 MB │  label = model.DownloadLabel
  │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░    │  bytes = "{r/1e6:0.0} / {t/1e6:0.0} MB",
  │  You can't leave this step while a download is in progress.  │          or "{r/1e6:0.0} MB" when total = 0
  ├──────────────────────────────────────────────────────────────┤  bar = Determinate(ProgressFraction(r,t), 412)
  │                                              [   Cancel   ]  │        else Indeterminate(412)
                                                                    caption 12 Tok.TextTertiary, wrap
```

### W27 — Setup dialog, `Verifying`

```
  │  Verifying download…                                         │
  │  Checking integrity and signature                    84.0 MB │  value "—" when Total = 0
  │  ▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░    │  ALWAYS indeterminate: the provisioner reports
  │  ┌──────────────────────────────────────────────────────┐    │  one Verifying stage, not sub-percentages
  │  │ Version        1.2.93.667                            │    │  VerifyDetailBox: Pad 8, Gap 4, r4,
  │  │ Architecture   Arm64                                 │    │    Fill Tok.FillLayerAlt, 1px StrokeCardDefault
  │  │ SHA-256        3f2a…c91d                             │    │  rows: label 12 TextSecondary Width 92,
  │  └──────────────────────────────────────────────────────┘    │         value 12 TextPrimary 1 line ellipsis
  ├──────────────────────────────────────────────────────────────┤  hash = SetupRuntimePresentation.ShortHash
  │                                              [   Cancel   ]  │         (first 4 + "…" + last 4)
                                                                    Cancel is DISABLED here (enabled: false)
```

### W28 — Setup dialog, `Untrusted`

```
  │  ⚠  This DLL isn't Spotify-signed                            │  Status(): glyph F13C @18 Tok.SystemFillCaution
  │     This file isn't Spotify-signed. It matches the expected  │    Shrink 0, Margin(0,1,0,0); Gap 12
  │     fingerprint, but the publisher signature couldn't be     │  heading 14/600 TextPrimary wrap
  │     verified. Only continue if you trust the source.         │  body 13 TextSecondary wrap, Gap 4
  ├──────────────────────────────────────────────────────────────┤
  │                           [ Cancel ]  [   Load anyway    ]  │
```

### W29 — Setup dialog, `Ready`

```
  │  ✓  Local playback is ready                                  │  InfoBadge.Icon(Success) + 14/600 TextPrimary
  │                                                              │  Gap 8, AlignItems Center
  │  You're on the latest supported version.                     │  ← only when UpToDate (a check that found the
  │                                                              │     ALREADY-active pack, not a fresh install)
  │  ┌──────────────────────────────────────────────────────┐    │  ReadyDetailBox: Pad 12, Gap 6, r4,
  │  │ Version        1.2.93.667                            │    │    Fill FillLayerAlt, 1px StrokeCardDefault
  │  │ Architecture   Arm64                                 │    │  labelWidth 92
  │  │ Signature      Digitally signed by Spotify AB  [Sig] │    │  Signature button: Standard, MinW 86, h28
  │  │ Location       C:\…\Wavee\runtime\1.2.93.667         │    │    (absent when SignatureInfo is null)
  │  └──────────────────────────────────────────────────────┘    │
  │  Replace   Remove                                            │  two HyperlinkButtons, Gap 8
  ├──────────────────────────────────────────────────────────────┤
  │  Check for update                            [    Done    ]  │
```

### W30 — Setup dialog, `Failed`

```
  │  ⊗  Local playback needs a one-time setup                    │  glyph F13D @18 Tok.SystemFillCritical
  │     Couldn't download playback support.                      │  body = model.Error ?? loc playback.runtime.noPack
  ├──────────────────────────────────────────────────────────────┤
  │ Advanced options  View diagnostics  [ Not now ] [ Try again ]│  LeftLinks: only the FIRST link takes the −11
  └──────────────────────────────────────────────────────────────┘    flush-left margin; the rest keep their own
      "View diagnostics" exists only when a navigator is in context (go != null) — a clickable-but-inert link is
      worse than no link (PlaybackRuntimeSetupCard.cs:978-983)
```

### W31 — Setup dialog, `Advanced`, catalog loaded

```
  │  Supply a Spotify.dll yourself — Wavee recognizes supported   │  Body 13 TextSecondary; Column Gap = Spacing.S (8)
  │  builds by fingerprint and configures the rest automatically. │
  │                                                              │
  │  Choose a version to install                                 │  RadioButtons header
  │   ( • )  Spotify 1.2.93.667 · Arm64  (Recommended)           │  labels "Spotify {v} · {arch}", index 0 gets
  │   (   )  Spotify 1.2.91.301 · Arm64                          │    "  (Recommended)" appended (two spaces)
  │   (   )  Spotify 1.2.88.120 · Arm64                          │
  │  ──────────────────────────────────────────────────────────  │  1px StrokeCardDefault, Margin(0,4,0,4)
  │  ┌──────────────────────────────────────────────────────┐    │
  │  │ 📁  Choose a Spotify.dll…                         ›  │    │  SettingRow: Pad(12,9,12,9), Gap 13, r4,
  │  │     Pick a folder or file — a supported build is     │    │    Interaction.Subtle
  │  │     recognized automatically                        │    │    glyph 16 TextSecondary, Shrink 0
  │  ├──────────────────────────────────────────────────────┤    │    title 14/600 TextPrimary
  │  │ ♪   Use installed Spotify                         ›  │    │    caption 12 TextSecondary, wrap, Gap 1
  │  │     Reuse the Spotify app already installed on this │    │    chevron E974 @12 TextTertiary
  │  │     PC                                              │    │
  │  └──────────────────────────────────────────────────────┘    │
  ├──────────────────────────────────────────────────────────────┤
  │  Back                                       [   Install   ]  │  Install enabled only when
                                                                    CatalogState.Loaded && SupportedPacks.Count > 0
```

### W32 — Setup dialog, `Advanced`, catalog fetching / no pack / unreachable

```
  fetching   │  ◌  Checking what's available for your device…    │  BusyRow: ProgressRing.Indeterminate() (32 DIP,
             │                                                  │    3 px stroke, 2000 ms loop) + label 13 TextPrimary
             │                                                  │    Gap 12, MinHeight 48
  no pack    │  Playback support isn't available for your        │  Body 13 TextSecondary
             │  Spotify version yet.                            │
  unreachable│  Couldn't reach the catalog — you can still       │  Body 13 TextSecondary
             │  install from a local source.                    │

  NotFetched │  (nothing at all — no busy row, no copy)         │  the switch has no arm for it
```

`CatalogState.NotFetched` is reachable in exactly two ways: no provisioner (`EnsureCatalog` returns before it sets
`Fetching`, `PlaybackRuntimeSetupCard.cs:281-283`), or a cancelled fetch reverting the state (`:305`). The body is
then AdvancedBody · divider · the two local-source rows and nothing between them — port the empty arm rather than
inventing a fifth message. Entering `Advanced` always calls `EnsureCatalog()` (`:275`), which is idempotent and
**re-tries after a failure** but not after a success, so `Back` → `Advanced` does not refetch.

### W32b — Where every exit lands (the phase graph the wireframes imply but never state)

```
  Offer ──Download & set up──▶ FetchingCatalog ──▶ Downloading ──▶ Verifying ──▶ Ready        (:151,238,242,261)
    ▲            │                   │                 │              │
    │            │                   ╰── Cancel ───────╯              ├─▶ Untrusted ──Load anyway──▶ Verifying…
    │            ╰── Advanced ──▶ Advanced ──Install──▶ Downloading    ╰─▶ Failed
    │                               │  ▲       (cancel ⇒ Advanced, :224)
    ╰───────── Cancel / CancelUntrusted ╯      Back (:988)
```

- **Cancel is not one destination.** A cancel on the `StartDownload` flow reverts the catalog to `NotFetched` and the
  phase to **`Offer`** (`:191-196`); a cancel on `InstallSelected` returns to **`Advanced`** (`:224`); `CancelUntrusted`
  goes to **`Offer`** (`:345`). Three buttons all labelled `Cancel`, three different screens after the click.
- **`Check for update` and `Try again` are both literally `StartDownload()`** (`:268,320`). So `Ready`'s quiet-looking
  link re-enters `FetchingCatalog` — footer swaps to a single `Cancel`, the fact box disappears — and comes back to
  `Ready` through `AlreadyUpToDate` (`UpToDate = true`, so the "You're on the latest supported version." line is now
  present, `:429-434`) or drops into `Downloading`. It is a full round trip, not an in-place check.
- **The `Failed` body is not always a localised sentence.** It is `model.Error`, which is
  `AudioFailureText.ToUserMessage(outcome, detail)` on the provisioner paths (`:262,406,482`) but a **raw
  `ex.Message`** on the two catch-alls (`:197,225`) and a loc string on the guard paths (`:148,204,385,389`). Budget
  for an arbitrary-length, arbitrary-shaped string in that wrapped body — it is the one place in this surface where a
  .NET exception message reaches the user verbatim.
- **The local-source rows run `RegisterDir` synchronously on the UI thread** under `Phase.Verifying` (`:401-407`), so
  `Choose a Spotify.dll…` / `Use installed Spotify` flash the Verifying screen (disabled `Cancel`, indeterminate bar)
  for as long as `TryRegisterRuntime` takes and then land on Ready / Untrusted / Failed. It is not an async phase.
- **`UseInstalled` is compile-gated.** `#if WAVEE_PLAYPLAY_LOCAL` (`:383-390`): in a public-only checkout the row is
  still drawn and still clickable, and it answers with the `Failed` phase and `playback.runtime.notActive`. Port the
  row AND the gate — a build where the row silently does nothing is worse than one that says why.

### W33 — Digital-signature details dialog (from `Ready` → `[Signature]`)

```
  ┌────────────────────────────────────────────────────────────────────────────┐  DialogWidth 548 (= MaxW)
  │  Digital signature                                                         │  CloseText "Close", DefaultBtn Close
  │                                                                            │
  │  ✓  Digitally signed by Spotify AB                                         │  InfoBadge.Icon(Success|Caution)
  │  ──────────────────────────────────────────────────────────────────────    │  + 14/600 TextPrimary wrap
  │  Publisher              Spotify AB                                         │  1px StrokeCardDefault
  │  Issuer                 DigiCert Trusted G4 …                              │  DialogDetailRow: Direction 1, Gap 2
  │  Trust                  Trusted                                            │    label 11 TextSecondary
  │  Reason                 (blank → "-")                                      │    value 12 TextPrimary, wrap
  │  Valid from             2024-03-11 09:00:00 +01:00                         │  format "yyyy-MM-dd HH:mm:ss zzz",
  │  Valid to               2027-03-11 09:00:00 +01:00                         │         InvariantCulture, ToLocalTime
  │  Thumbprint             9F2C…                                              │
  │  Pinned fingerprint     No                                                 │  Column Gap = Spacing.M (12)
  │  File                   C:\…\Spotify.dll                                   │
  ├────────────────────────────────────────────────────────────────────────────┤
  │                                                             [   Close   ]  │
  └────────────────────────────────────────────────────────────────────────────┘
```

`SignatureSummary` ladder (`PlaybackRuntimeSetupCard.cs:828`): signed + trusted → `"Digitally signed by {subject}"`;
signed + pinned → `"Signed by {subject}; trusted by pinned fingerprint"`; signed otherwise →
`"Signed by {subject}; {TrustLabel}"`; no subject + pinned → `"Trusted by pinned runtime fingerprint"`; else
`"Verified runtime fingerprint"` / `"Signature not trusted; manual override"` /
`"Signature check unavailable on this platform"` / `"Signature unknown"`. **These nine strings are hard-coded English
in 0.2.9** (not localised) — see the drift note in §6.

### W34 — Hover / press / focus state sheet

```
 palette row      rest  Transparent          hover  Tok.FillSubtleSecondary     selected  Tok.FillSubtleSecondary
                  (selection and hover paint the SAME fill — the Enter hint + accent glyph disambiguate)

 profile chip     Interaction.Subtle → rest FillSubtleTransparent · hover FillSubtleSecondary · press FillSubtleTertiary

 menu row         rest FillSubtleTransparent · hover FillSubtleSecondary · press FillSubtleTertiary
                  keyboard cursor + SubMenuOpened paint the SAME fill as hover (MenuFlyout.cs:182)

 notification card hover WaveeColors.RowHover (#FFFFFF0F dark / #0000000D light)
                   press WaveeColors.RowPressed (#FFFFFF0A dark / #00000012 light)   — only when OnClick != null

 filter pill      selected  Fill AccentDefault → hover AccentSecondary
                  rest      Fill FillSubtleSecondary → hover WaveeColors.RowHover

 pill button      accent   rest AccentDefault · hover AccentSecondary · press AccentTertiary · border 0
                  standard rest FillControlDefault · hover FillControlSecondary · press FillControlTertiary
                           · 1px StrokeControlDefault

 link button      rest transparent · hover WaveeColors.RowHover · press WaveeColors.RowPressed · r6
                  text always Tok.AccentTextPrimary 12/600

 setting row      Interaction.Subtle (same ramp as the chip)

 text field       rest FillControlDefault · hover FillControlSecondary · focus FillControlInputActive
                  + a 2 DIP Tok.AccentDefault bar pinned to the BOTTOM edge while focused (EditableText.cs:418-423)
                  border stays the neutral Tok.ControlElevationBorder gradient in every state
```

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| **Command palette** |||||||
| overlay lane | Grow 1 | pad `(0,64,0,0)` | — | — | — | `HitTestVisible=false` | `WaveePalette.cs:58-62` |
| anchor | 560 × 0 | — | — | — | — | — | `WaveePalette.cs:40` |
| popup surface | ≤ 560 × ≤ 460 | pad 0 (`menuPad` false for `Popup`) | `Radii.Overlay` 8 | — | `Tok.AcrylicFlyout` + `.Fallback`, 1px `Tok.StrokeFlyoutDefault` | `Elevation.Flyout` | `OverlayHost.cs:1612-1628` |
| palette body | W 560, MaxH 460 | pad 8 all, gap `Spacing.XXS` 2 | — | — | — | — | `WaveePalette.cs:185-186` |
| query field | 544 × 32 | — | `Radii.Control` 4 | 14 | `Tok.FillControlDefault` → `…InputActive` | 2px `Tok.AccentDefault` focus bar | `WaveePalette.cs:190-194` |
| result row | MinH 32 (`WaveeSize.ControlH`), MinW 0 | pad `(16,4,16,4)`, gap `Spacing.S` 8 | `Radii.ControlAll` 4 | — | active `Tok.FillSubtleSecondary`, hover same | — | `WaveePalette.cs:158-166` |
| row glyph | 14 | — | — | `Theme.IconFont` | active `Tok.AccentDefault` / `Tok.TextTertiary` | — | `WaveePalette.cs:169` |
| row label | — | — | — | 14, `MaxLines 1`, `CharacterEllipsis` | `Tok.TextPrimary` | — | `WaveePalette.cs:173` |
| row "Enter" | — | — | — | 11 | `Tok.TextTertiary` | — | `WaveePalette.cs:176` |
| empty row | — | pad `(16,12,16,12)` | — | 13 | `Tok.TextTertiary` | — | `WaveePalette.cs:147-148` |
| **Profile chip** |||||||
| chip | H 32 | pad `(4,0,10|4,0)`, gap 8 | `Radii.Control` 4 | — | `Interaction.Subtle` | — | `ProfileMenu.cs:176-192` |
| avatar | 24 | — | circle | — | `PersonPicture` (initials fall back to `displayName`) | — | `ProfileMenu.cs:31,75` |
| name caption | MaxW 76 | — | — | `Ui.Caption` 12/16 + `.Primary()` | `Tok.TextPrimary` | — | `ProfileMenu.cs:38,186` |
| **Profile menu** |||||||
| flyout body | MinW = MaxW 304 | pad `(0,6,0,6)` | 8 (presenter) | — | acrylic plate, 1px `Tok.StrokeFlyoutDefault` | `Elevation.Flyout` + DWM system shadow | `ProfileMenu.cs:231-238` |
| presenter pad | — | `(0,2,0,2)` | — | — | — | — | `OverlayHost.cs:1583` |
| account header | — | pad `(14,10,14,10)`, gap 12 | — | — | — | — | `ProfileMenu.cs:257-260` |
| header avatar | 40 | — | circle | — | `PersonPicture` | — | `ProfileMenu.cs:263` |
| header name | — | gap 2 (stack) | — | 14 / 600, 1 line, ellipsis | `Tok.TextPrimary` | — | `ProfileMenu.cs:273-280` |
| tier row | — | gap 5 | — | — | premium `#E6C26C` dark / `#8A6312` light; free `Tok.TextSecondary` | — | `ProfileMenu.cs:29,300,306` |
| tier star | 10 | — | — | `Icons.FavoriteStar` E734 | tier ink | — | `ProfileMenu.cs:314` |
| tier badge | — | — | — | 12 | tier ink | — | `ProfileMenu.cs:299,301` |
| email | — | — | — | 12, 1 line, ellipsis | `Tok.TextTertiary` | — | `ProfileMenu.cs:283-289` |
| header separator | H 1 | margin `(8,4,8,4)` | — | — | `Tok.StrokeDividerDefault` | — | `ProfileMenu.cs:248-253` |
| menu row | H 36, MinH 32 | margin `(4,2,4,2)`, pad `(11,8,11,9)` | 4 | 14 | subtle ramp | — | `MenuFlyout.cs:61,74-75,182-196` |
| menu icon column | 28 wide, glyph 16 | — | — | — | row fg | — | `MenuFlyout.cs:66,157` |
| menu separator | H 1 (+1/+1 pad) | line margin `(−4,0,−4,0)` | — | — | `Tok.StrokeDividerDefault` | — | `MenuFlyout.cs:118-122` |
| menu chevron | 12 | margin `(24,0,0,0)` | — | `Icons.ChevronRightMed` E974 | check fg | — | `MenuFlyout.cs:65,76,171-177` |
| **Confirm / dialog cards** |||||||
| confirm card | W 380, 320..420 — **off-ladder**, a bare Modal `BoxEl`, see §3.1 | pad 24, gap `Spacing.M` 12 | `Radii.Overlay` 8 | — | `Tok.FillSolidBase`, 1px `Tok.StrokeSurfaceDefault` | `Elevation.Dialog` | `ProfileMenu.cs:321-325` |
| confirm title | — | — | — | 20 / 600, wrap | `Tok.TextPrimary` | — | `ProfileMenu.cs:328` |
| confirm body | — | — | — | 14, wrap | `Tok.TextPrimary` | — | `ProfileMenu.cs:329` |
| confirm buttons | MinW 96 | gap 8, `Justify End`, margin top 12 | 4 | 14 | Standard / Accent | — | `ProfileMenu.cs:330-337` |
| modal scrim | full | — | — | — | `Tok.FillSmoke` `#0000004D` | — | `OverlayHost.cs:1335` |
| **Play-link dialog** |||||||
| card | W 420, 360..480 — **off-ladder**, a bare Modal `BoxEl`, see §3.1 | pad 24, gap 12 | 8 | — | `FillSolidBase`, 1px `StrokeSurfaceDefault` | `Elevation.Dialog` | `PlayLinkDialog.cs:109,215-217` |
| title | — | — | — | 20 / 600, wrap | `Tok.TextPrimary` | — | `PlayLinkDialog.cs:220-222` |
| field | 372 × 32 | — | 4 | 14 | control fills | 2px accent focus bar | `PlayLinkDialog.cs:110,226-230` |
| status row | MinH 18 | — | — | 12, 1 line, ellipsis | `Tok.TextSecondary` | — | `PlayLinkDialog.cs:111,232-241` |
| buttons | MinW 96 (Cancel/Play) | gap 8, `Justify End`, margin top 8 | 4 | 14 | Standard / Accent / Standard | — | `PlayLinkDialog.cs:207-211,243-247` |
| **Notification panel** |||||||
| panel | W 380, MaxH 520 | — | 8 | — | acrylic, 1px `StrokeFlyoutDefault` | `Elevation.Flyout` | `NotificationPanel.cs:47,90-93` |
| header | — | pad `(14,12,8,8)`, gap 8 | — | 15 / 700 | `Tok.TextPrimary` | — | `NotificationPanel.cs:137-140` |
| link button | MinH 28 | pad `(8,3,8,3)` | 6 | 12 / 600 | `Tok.AccentTextPrimary`; hover `RowHover` | — | `NotificationPanel.cs:582-588` |
| filter row | — | pad `(12,2,12,8)`, gap 6 | — | — | — | — | `NotificationPanel.cs:159-161` |
| filter pill | MinH 26 | pad `(11,3,11,3)` | 13 (full) | 12 / 600 | sel `AccentDefault`/`TextOnAccentPrimary`; rest `FillSubtleSecondary`/`TextSecondary` | — | `NotificationPanel.cs:148-156` |
| pending-sync | — | pad `(14,4,14,8)`, gap 8 | — | 12 | icon `TextTertiary`, text `TextSecondary` | — | `NotificationPanel.cs:112-120` |
| scroller | MaxH 460 | content pad `(6,4,6,8)`, gap 2 | — | — | `ContentSized` (shrink-wraps a short feed), `AutoEdgeFade`, `ScrollKey "notifications"` | — | `NotificationPanel.cs:83-87` |
| update action strip | — | gap 6, margin top 4, **`Wrap = true`** | — | — | — | — | `NotificationPanel.cs:277` |
| update progress strip | — | gap 8, margin top 6 | — | — | — | — | `NotificationPanel.cs:268` |
| row card | MinH 56 | pad `(10,8,10,8)`, gap 10 | 8 | — | hover `RowHover`, press `RowPressed` | — | `NotificationPanel.cs:185-200` |
| unread dot | 8 × 8 | — | 4 (circle) | — | `Tok.AccentDefault` | — | `NotificationPanel.cs:203` |
| glyph chip | 36 × 36 | — | 18 (circle) | glyph 16 | `Tok.FillSubtleSecondary` + tint | — | `NotificationPanel.cs:550-555` |
| update title | — | — | — | 13.5 / 700, 1 line, ellipsis | `Tok.TextPrimary` | — | `NotificationPanel.cs:293` |
| update body | — | col gap 3 | — | 12, wrap, `MaxLines 3` | `Tok.TextSecondary` | — | `NotificationPanel.cs:294` |
| progress bar | W 200 | gap 8, margin top 6 | 1.5 (indicator) | — | `Tok.AccentDefault` on `ControlStrongStroke` track | — | `NotificationPanel.cs:271`, `ProgressBar.cs:36-39` |
| progress % | — | — | — | 11, `"Cascadia Code"` | `Tok.TextTertiary` | — | `NotificationPanel.cs:272-273` |
| pill button | MinH 28 | pad `(12,4,12,4)` | 14 (full) | 12 / 600 | accent / control ramps, 1px `StrokeControlDefault` when not accent | — | `NotificationPanel.cs:569-579` |
| social art | 40 × 40 | — | 20 (circle) | — | `Surfaces.Artwork` | `ClipToBounds` | `NotificationPanel.cs:317-321` |
| social title | — | col gap 2 | — | 13, wrap, `MaxLines 2` | `Tok.TextPrimary` | — | `NotificationPanel.cs:307` |
| reltime | — | — | — | 11 / 600 | `Tok.TextTertiary` | — | `NotificationPanel.cs:310,413` |
| release art | 44 × 44 | — | 5 | — | `Surfaces.Artwork` | `ClipToBounds` | `NotificationPanel.cs:350-355` |
| release name / creator | — | col gap 2 | — | 13.5 / 600 · 12 | `TextPrimary` · `TextSecondary` | — | `NotificationPanel.cs:362-363` |
| type pill | — | pad `(9,2,9,2)` | 10 | `WaveeType.Eyebrow` 12/16/600 + 30‰ tracking | `FillSubtleSecondary` + `TextTertiary` | — | `NotificationPanel.cs:563-566` |
| status chip | — | pad `(7,1,7,1)` | 8 | Eyebrow | `FillSubtleSecondary` + severity ink | — | `NotificationPanel.cs:557-560` |
| activity summary | — | inner gap 6 | — | 13, wrap, `MaxLines 2`, strikethrough when undone | `TextPrimary` / `TextTertiary` | — | `NotificationPanel.cs:401-406` |
| activity detail | — | margin `(46,0,12,6)`, gap 3 | — | 12 / 12 600 | `TextSecondary` | — | `NotificationPanel.cs:485-489` |
| empty state | W 380, MinH 120 | pad 24 | — | 13 / 600, wrap, MaxW 300 | `Tok.TextSecondary` | — | `NotificationPanel.cs:612-617` |
| **Toasts (in-app)** |||||||
| lane | Grow 1 | pad `(24,24,24,96)` | — | — | `HitTestPassThrough` | — | `Toast.cs:318` + `WaveeShell.cs:482` |
| strip | — | gap 8 | — | — | hover pauses countdowns | — | `Toast.cs:299-305` |
| card | 300..380 | — | `Radii.ControlAll` 4 | — | `Tok.FillSolidTertiary`, 1px `Tok.StrokeSurfaceDefault` | `Elevation.Flyout` | `Toast.cs:346-362` |
| InfoBar body | MinH 48 | root pad `(16,0,0,0)`, panel margin `(0,0,16,0)` | 4 | title 14/600, message 14 | severity background + 1px `StrokeCardDefault` | — | `InfoBar.cs:65,91-93,313-329` |
| severity icon | 16 | margin `(0,16,14,16)` | — | F136 circle + F13C/D/E/F | see §4 | — | `InfoBar.cs:204-216` |
| InfoBar close | 38 × 38 | margin 5, glyph 16 | 4 | `Icons.Cancel` E711 | hover `FillSubtleSecondary`, press `FillSubtleTertiary` | — | `InfoBar.cs:283-303` |
| custom body | MinH 48 | pad 16 | 4 | — | severity background, 1px `StrokeCardDefault` | — | `Toast.cs:371-387` |
| update card | — | pad `(16,14,16,14)`, gap 8 | — | 13 / 600 | — | — | `NotificationCenterBridge.cs:246-256` |
| update card bar | W 240 | — | 1.5 | — | `Tok.AccentDefault` | — | `NotificationCenterBridge.cs:254` |
| **Teaching tip** |||||||
| card | 320..336 × 40..520 | content margin 12 | 8 | title 16/600, subtitle 14 | `SolidBackgroundFillColorTertiary`, 1px `SurfaceStrokeColorDefault`, 1px top highlight `#0DFFFFFF` dark / `#99FFFFFF` light | `Elevation.Flyout` | `TeachingTip.cs:30-40` |
| tail | 16 × 8 | — | — | — | card fill | — | `TeachingTip.cs:20` |
| close | 40 × 40 | glyph 16 | 4 | E711 | subtle ramp | — | `TeachingTip.cs:38` |
| **Runtime banner** |||||||
| lane | Grow 1 | pad `(0,56,0,0)` | — | — | `HitTestPassThrough`, `AlignItems Center` | — | `WaveeShell.cs:1397-1400` |
| inner cap | MaxW 560 | — | — | — | — | — | `WaveeShell.cs:1403` |
| plate | — | — | `Radii.Control` 4 | — | `Tok.FillSolidBase` (opaque) | `Elevation.Flyout`, `ClipToBounds` | `PlaybackRuntimeBanner.cs:36-42` |
| InfoBar | MinH 48 | see W22 | 4 | 14/600 + 14 | `Tok.SystemFillCautionBackground` | — | `InfoBar.cs:313-329` |
| action | — | margin `(16,8,0,0)` H / `(0,12,0,0)` V | 4 | 14 | `Button.Accent` | — | `PlaybackRuntimeBanner.cs:51` |
| **Setup dialog** |||||||
| card | W **460** = ladder rung 2 (§3.1), MinH 184, MaxH 756 | — | 8 | — | `Tok.FillSolidBase`, 1px `Tok.StrokeSurfaceDefault` | `Elevation.Dialog` | `PlaybackRuntimeSetupCard.cs:38`, `ContentDialog.cs:110,283,355-368` |
| content region | — | pad 24 | — | title 20/600 | `Tok.FillLayerAlt` | — | `ContentDialog.cs:311-330` |
| content scroller | MaxH 556 | — | — | — | `EdgeCues.None` | — | `ContentDialog.cs:307` |
| separator | H 1 | — | — | — | `Tok.StrokeCardDefault` | — | `ContentDialog.cs:337` |
| command row | — | pad 24, gap 8 | — | — | `Tok.FillSolidBase` | — | `ContentDialog.cs:342-351` |
| `Lead` | — | — | — | 16, wrap | `Tok.TextPrimary` | — | `:549-550` |
| `Body` | — | — | — | 13, wrap | `Tok.TextSecondary` (overridable) | — | `:546-547` |
| `Status` | — | gap 12, glyph 18 margin `(0,1,0,0)` | — | heading 14/600, body 13 | severity ink | — | `:554-570` |
| `ProgressMetricRow` | — | gap 12 | — | label 13/600 1 line ellipsis, value 12 | `TextPrimary` / `TextSecondary` | — | `:656-668` |
| bar | W 412 | — | 1.5 | — | `Tok.AccentDefault` | — | `:588,599` |
| detail box | — | pad 8 (verify) / 12 (ready), gap 4 / 6 | 4 | — | `Tok.FillLayerAlt`, 1px `Tok.StrokeCardDefault` | — | `:640-653,705-717` |
| detail row | — | gap 8 | — | label 12 W 92, value 12 1 line ellipsis | `TextSecondary` / `TextPrimary` | — | `:730-739` |
| `BusyRow` | MinH 48 | gap 12 | — | 13 + caption 12 | ring accent | `ProgressRing` 32 DIP, 3 px stroke | `:572-586` |
| `SettingRow` | — | pad `(12,9,12,9)`, gap 13, inner gap 1 | 4 | 14/600 + 12 | `Interaction.Subtle` | — | `:915-935` |
| footer button | MinW 96, H 32 | gap 8 | 4 | 14 | Standard / Accent (TabIndex 1) | — | `:996-1004` |
| footer link | — | margin `(−11,0,0,0)`, own pad `(11,5,11,6)` | 4 | 14 | `Tok.AccentTextPrimary` | — | `:1008-1009`, `HyperlinkButton.cs:45` |
| signature dialog | W **548** = ladder rung 4, the engine max (§3.1) | pad 24, gap 12 | 8 | 11 / 12 rows | `FillSolidBase` | `Elevation.Dialog` | `:779`, `:776-826` |

### 3.1 The modal width ladder (app-wide; this chapter is its home)

Four widths are sanctioned across the whole app, and nothing else is. The engine decides the envelope:
`ContentDialog.cs:110` `const float MinW = 320f, MaxW = 548f, MinH = 184f, MaxH = 756f`, and
`ContentDialog.cs:283` `float cardW = Math.Clamp(DialogWidth ?? (buttons.Count >= 3 ? 480f : MinW), MinW, MaxW)` —
so `DialogWidth` is a **request**, silently clamped into 320…548. A dialog cannot be 600 and cannot be 720; the
only way past 548 is to stop using `ContentDialog` (rung ✗ below). Every rung is one of the engine's own numbers:
320 and 548 are the WinUI Min/Max, 480 is the width `ContentDialog` itself picks for a three-button dialog.

| rung | width | content width | what picks it | 0.2.9 users | file:line |
|---|---|---|---|---|---|
| 1 | **320** (`MinW`, the stock default) | 272 | a pure **title + `Message` string** confirm with 1–2 buttons and no `d.Content` at all — the card has nothing to size to, so the default is right. It is ALSO what you get by forgetting `DialogWidth` on a dialog that *does* have a body, and then the card clamps the body instead of the body sizing the card. | **four** call sites: `SettingsShared.Confirm` — every Settings/diagnostics confirm (27 §2 W27) · `ContainerActions.RenameDialog` — an `EditableText` at `MinWidth = 320` inside a 320 card (06) · `TrackActions` “View credits” — `PrimaryText = ""`, so ONE button, and the unused left star column stays (01) · `Menus.OpenPicker` “Add to playlist” — `PrimaryText = ""`, the rows act, the dialog only dismisses (01/06) | `SettingsShared.cs:29-41` (sets no `DialogWidth`), `ContainerActions.cs:170,180-184`, `TrackActions.cs:161-168`, `Menus.cs:518-524`, `ContentDialog.cs:283`; the defect the accidental case caused: `SidebarItemPickers.cs:55-58` ("round-2 defect 6e") |
| 2 | **460** | 412 | one column of prose plus **one full-width metric/progress row** — the body is a sentence and a bar, not a list | playback-runtime setup dialog (19 §2 W24–W32) | `PlaybackRuntimeSetupCard.cs:38`; the derived bar `:588` `RuntimeProgressWidth = 412f` |
| 3 | **480** | 432 | the body is a **list or a picker** — scrolling rows, a `SelectorBar`, a miniature — or the dialog carries three buttons (the engine's own choice at this width) | sidebar item picker, action picker, template/reset confirmation (26 §2 W11–W13) — these DECLARE `DialogWidth = 480`; plus the Storage move-cache pair (`OfferCacheRelocation` → `OfferStartEmptyChoice`, 27 §2), the app's only Primary+Secondary+Close dialogs, which reach 480 **without** setting `DialogWidth` at all, by the engine's own three-button default | `SidebarItemPickers.cs:58,61,46,79`, `SidebarCustomizerPage.cs:717`; `SettingsPage.Storage.cs:626-640,642-656` (both `ContentDialog.Show` at `:629`/`:645`) |
| 4 | **548** (`MaxW`) | 500 (492 net of an inner column) | the body is a **form or a payload dump** — multi-field input, monospace text, a long label/value sheet | report/feedback dialog (28 §2 W24, content 500), lyrics inspector (22 §2 W17, content 492), the setup dialog's nested signature sheet | `ReportDialog.cs:35-36`, `LyricsInspectorDialog.cs:59`, `PlaybackRuntimeSetupCard.cs:779` |
| ✗ | **> 548** | — | **not a `ContentDialog` at all.** A surface that needs more than 548 is a hand-rolled `PopupChrome.Modal` plate with its own width, and it gives up the whole ContentDialog chrome (title band on `FillLayerAlt`, separator, `FillSolidBase` command row) in exchange. | after-update dialog plate 720 × ≤ 620 (28 §2 W22), setup wizard plate 762 × 490 (28 §0 #1) | `AfterUpdateDialog.cs:24-25,140-142`, `HighlightCardMetrics.PlateWidth` |

**Two cards in THIS chapter are off-ladder on purpose** — the logout confirm (380, min 320 / max 420,
`ProfileMenu.cs:323`) and the play-link dialog (420, min 360 / max 480, `PlayLinkDialog.cs:109,215`). Neither is a
`ContentDialog`: each is a plain `BoxEl` opened with `PopupChrome.Modal`, so it has **no** title band, **no**
separator and **no** command-row fill — just a `FillSolidBase` card padded 24 with a `Spacing.M` gap. They are also
the only two dialogs in the app with a real `MinWidth`/`MaxWidth` *range* rather than a fixed width. Converting
either onto the ladder is a visible change (it adds the `FillLayerAlt` content band and the 1 px separator), not a
tidy-up; and their widths are **not** rungs — a third hand-rolled card must justify its own number or reuse 380/420.

**Shared card metrics — every rung, because they are the engine's, not the caller's** (`ContentDialog.cs`). A rung
chooses a width and *nothing else*: two dialogs on different rungs are the same card at two widths.

| what | value | source |
|---|---|---|
| padding (title band, content region, command row) | **24** all round | `ContentDialog.cs:111` |
| content gap | **12** | `:112` |
| button gap | **8** | `:113` |
| title | **20 SemiBold** | `:114` |
| content text | **14** | `:115` |
| button min width / height | **130 / 32** (NOT the app's 96 — the two off-ladder plates below use 96) | `:116-117` |
| corners / border / shadow | `Radii.OverlayAll` · 1px `Tok.StrokeSurfaceDefault` · `Elevation.Dialog` | `:365-369` |
| envelope | 320…548 × 184…756 | `:110` |
| a body taller than the card | **scrolls inside it** at `MaxH − 200` = **556**, `EdgeCues.None` (an alpha-mask fade, never the colour cue — the content region is a translucent `FillLayerAlt` overlay the colour cue would sail past) | `:308` |
| a SINGLE-button dialog | keeps the unused **left star column** and pushes its one button right | `:286-290` |
| button ORDER | primary, secondary, close — so a destructive verb sits **left** and Cancel **right** | `:279-281` |

**This section is the app-wide home; 29 §2 W12 is its picture.** The ladder was authored twice — once here and once
in 29 §2 W12 (drawn to scale) — and each copy carried call sites the other did not. The union now lives here: 29's
four extra rung-1/rung-3 sites and the shared card metrics above, plus this chapter's two off-ladder plates and the
four consequences below. 29 §2 W12 is to be reduced to the scale drawing plus a one-line pointer at this section, and
29 §12.3's "every `ContentDialog` (the width ladder, the confirm shape)" routing keeps only the **confirm shape**
(29 §2 W13, `SettingsShared.Confirm`); the width ladder routes here. (29 is a sibling file — that edit belongs to its
own pass; this section is written so it is a deletion there, not a rewrite.)

Consequences a re-author must carry:

1. **Cite the rung, never the literal.** A new dialog picks a rung from the table above; a fifth width is a
   regression, and a width outside 320…548 is silently ignored by the engine (it clamps, it does not warn).
2. **Content width is derived, every time.** `rung − 2 × 24` (`ContentDialogPadding`). 460 → 412
   (`RuntimeProgressWidth`, §0 #16), 480 → 432 (`SidebarPickers.BodyW`), 548 → 500 (`ReportDialog.ContentWidth`).
   Moving a dialog between rungs moves its bar/field widths with it; none of the three may be typed independently.
3. **Rung 1 is correct for a `Message` and wrong for a `Content`.** The test is whether `d.Content` is set: a
   message-only confirm (`SettingsShared.Confirm`) legitimately leaves `DialogWidth` unset and lands on 320; any
   dialog that hands `ContentDialog` a body element must name its rung, or the card clamps the body — which is how
   the sidebar pickers shipped once already (`SidebarItemPickers.cs:55-58`).
4. **The rung is set once, at `Show`.** `DialogWidth` is read when the card is built (`ContentDialog.cs:283`), so a
   phased dialog cannot widen mid-flight: the setup dialog's Offer → Downloading → Advanced bodies all live inside
   460, and the widest of them (`Advanced`'s version list) is what sized the rung. The signature sheet is a
   *second, nested* dialog at 548 precisely because the first cannot grow (`PlaybackRuntimeSetupCard.cs:775-779`).

**Sibling chapters point here, not at a literal.** 22 §2 W17 (inspector 548), 26 §2 W11–W13 (picker /
confirmation 480), 27 §2 W27 (`SettingsShared.Confirm`, rung 1) and 28 §2 W24 (report 548) each state a width;
**29 §2 W12** draws the ladder to scale and **29 §12.3** routes dialog questions; 01 (View credits, Add to playlist),
06 (Rename playlist) and 27 §2 (the Storage move-cache pair) each own a call site in the table above. This table is
the authority for *why* that width, and the ✗ rung is why 28's 720 and 762 plates are not on it. A chapter that
introduces a dialog width without a rung here is the regression this section exists to catch.

### 3.2 The toast inventory (app-wide; this chapter owns the strip, so it owns the census)

`Toast.Show` is called **106 times across 38 files** (a 107th `grep` hit, `AppUpdateToasts.cs:27`, is a `<param>` doc
comment, not a call). The strip (W19, W20), the severity ramp (§4.2) and the escalation to Windows (§6.5) are
specified above; this is the missing half — *which* action raises *which* card. Wave 4 owner I folds
`WaveeCommands + Actions/*` into one action table; a re-authored action table with no toast column silently drops
every confirmation and every refusal in the app, and nothing would catch it.

**Defaults, so the table only records departures.** `Severity = Informational`, `DurationMs = 5000`,
`Closable = true`, no `Title`, no `DedupeKey`, no `ActionLabel`, no `CustomContent` (`Toast.cs:28-48`).
The whole app departs from the duration default exactly **three** times and sets `DedupeKey` exactly **three**
(`ReportChrome.cs:58,61` sticky + `crash.pendingReport`; `SettingsPage.Notifications.cs:119` 6000 ms;
`ReportDialog.cs:519` 8000 ms; `NotificationCenterBridge.cs:236-237` `plan.Sticky ? 0 : 5000` + `update`;
`PlayLinkDialog.cs:88,195` and `PlaybackBridge.cs:1096` share `PlayLinkActions.FailureToastKey`). `Title` and
`CustomContent` are used by **one** call site between them (`NotificationCenterBridge.cs:234,240`). Everything else
is a bare sentence at the defaults — which is the contract, not an accident: a toast is one sentence, five seconds.

**Announce?** Only **four** toast sites also speak, and none announce the toast text as such — the announce sits at
the data chokepoint, not on the card (`LibraryBridge.cs:292` `AnnounceSaved`, `FolderActions.cs:277` `Announce`,
`WaveeResourceDrag.cs:464`, `PlaylistCreateFlow.cs:79,97`). Do not add an `Announcer.Say` per toast in 0.3: a screen
reader already hears the change from the store.

| # | call site | sentence / loc key | sev | notes (duration · action button · dedupe · announce) |
|--:|---|---|---|---|
| | **`Actions/ContainerActions.cs` (8)** ||||
| 1 | `:98` | raw `ex.Message` (go-to-artist resolve threw) | Error | — |
| 2 | `:102` | `menu.artistUnavailable` | Warning | — |
| 3 | `:220` | `menu.linkCopied` | Success | — |
| 4 | `:314` | raw `ex.Message` (queue resolve threw) | Error | — |
| 5 | `:320` | `detail.addedToQueue(SongCount(n))` | Success | only when `n > 0` |
| 6 | `:323` | `drag.nothingToAdd` | Warning | the `n == 0` arm — a refusal, not silence |
| 7 | `:346` | `drag.nothingToAdd` | Warning | add-to-playlist, empty container |
| 8 | `:351` | `detail.addedToPlaylist(targetName)` | Success | **action** `detail.goToPlaylist` → `go("pl:" + uri)` |
| | **`Actions/TrackActions.cs` (4)** ||||
| 9 | `:41` | `detail.addedToQueue(SongCount(n))` | Success | Play next; `n > 0` |
| 10 | `:54` | `detail.addedToQueue(SongCount(n))` | Success | Add to queue; `n > 0` |
| 11 | `:96` | `menu.linkCopied` | Success | Share ▸ Copy link |
| 12 | `:185` | `menu.uriCopied` | Success | Share ▸ Copy Spotify URI |
| | **`Actions/Menus.cs` (4)** ||||
| 13 | `:359` | `detail.addedToPlaylist(name)` | Success | **action** `notifications.undo` → `nc.UndoByIdAsync(activityId)` — present ONLY when `nc != null && activityId >= 0` |
| 14 | `:373` | `detail.edit.removedFromPlaylist(count)` | Success | same conditional Undo |
| 15 | `:430` | `menu.movedToPlaylist(name)` | Success | **action** `detail.goToPlaylist` |
| 16 | `:493` | `detail.addedToPlaylist(name)` | Success | **action** `detail.goToPlaylist` |
| | **`Actions/LocalFileActions.cs` (4)** ||||
| 17 | `:48` | raw `ex.Message` (file picker threw) | Error | — |
| 18 | `:66` | `localFile.rejected` | Error | a drop of an unsupported file |
| 19 | `:73` | `localFile.notReady` | **Informational** | the pre-go-live answer — reachable only by DROP (the menu row is absent) |
| 20 | `:93` | `localFile.rejected` | Error | media path |
| | **`Actions/PinActions.cs` (2)** ||||
| 21 | `:110` | `sidebar.pin.pinned` (via `Message(PinnedToastKey, …, name)`) | Success | **action** `sidebar.pin.undo` → `prefs.Unpin(pinId)` |
| 22 | `:128` | `sidebar.pin.unpinned` | **Informational** | **action** `sidebar.pin.undo` → `prefs.InsertPin(removed, at)` — an unpin is not a success |
| | **`Actions/FolderActions.cs` (1)** ||||
| 23 | `:116` | `sidebar.folderCreatedWith(name, moves.Count)` | Success | **action** `sidebar.renameFolder` → `Rename` · **announce** `FolderActions.cs:277` `SayThrottled` |
| | **`Actions/RadioLaunch.cs` (3)** ||||
| 24 | `:27` | raw `ex.Message` | Error | — |
| 25 | `:31` | `menu.radioUnavailable` | Warning | — |
| 26 | `:37` | `menu.radioStarted` | Success | **action** `menu.openRadioPlaylist` → `go("pl:" + uri)` |
| | **`Actions/PlaylistCreateFlow.cs` (1)** ||||
| 27 | `:91` | `detail.edit.createFailed` | Error | **action** `common.retry` → `Start(…)` · **announce** `:97` `Say(assertive: true)`. The SUCCESS path announces (`:79`) and raises **no** toast — the new playlist appearing is its own confirmation |
| | **`Actions/VideoActions.cs` (7)** ||||
| 28 | `:60` | `videoOverride.removed` | Success | **action** `videoOverride.undo` → `Restore(previous.Path)` |
| 29 | `:99` | raw `ex.Message` (picker threw) | Error | — |
| 30 | `:151` | `videoOverride.rejectedNotMp4` ｜ `…rejectedNotFound` | Error | one of two sentences, by `VideoAttachRejection` |
| 31 | `:165` | raw `ex.Message` (attach threw) | Error | — |
| 32 | `:174` | `videoOverride.replaced` ｜ `…attached` | Success | **action** `videoOverride.undo` → `Restore(previousPath)` — undo restores the PRIOR attachment, or none |
| 33 | `:194` | raw `ex.Message` (restore threw) | Error | — |
| 34 | `:195` | `videoOverride.restored` | **Informational** | an undo landing is not a success |
| | **`Actions/Extensibility/BuiltInExtensionTable.cs` (1)** ||||
| 35 | `:210` | `menu.linkCopied` | Success | the registry-declared copy-link action |
| | **`Features/Shell/SettingsPage.VideoOverrides.cs` (7)** ||||
| 36 | `:240` | `videoOverride.removed` | Success | **action** `videoOverride.undo` → `curation.Attach(uri, path)` |
| 37 | `:247` | raw `ex.Message` | Error | raised from INSIDE 36's undo — an undo that fails still speaks |
| 38 | `:267` | raw `ex.Message` (picker threw) | Error | — |
| 39 | `:275` | `videoOverride.rejectedNotMp4` ｜ `…rejectedNotFound` | Error | Settings has room for an inline explanation and deliberately does not use it: the failure stays attached to the click |
| 40 | `:284` | raw `ex.Message` (attach threw) | Error | — |
| 41 | `:287` | `videoOverride.replaced` | Success | **no** undo here, unlike `VideoActions.cs:174` |
| 42 | `:298` | `videoOverride.clearedAll` | Success | no undo — a bulk clear is confirmed, not reversible |
| | **`Features/Shell/SettingsPage.Storage.cs` (7)** ||||
| 43 | `:121` | `settings.storage.metadataCleared` | Success | behind `SettingsShared.Confirm` (rung 1) |
| 44 | `:272` | `settings.storage.oldLogsDeleted(count)` ｜ `…noOldLogsDeleted` | Success | a no-op still answers |
| 45 | `:413` | `settings.storage.detailsReleased` | Success | Release now (`ShedDetails(keep: 16)`) |
| 46 | `:667` | `settings.storage.cacheLocationChanged` | Success | the end of the move-cache flow (rung 3) |
| 47 | `:672` | `settings.storage.cacheLocationFailed` | Error | that flow's failure arm |
| 48 | `:695` | `settings.storage.audioCacheCleared` | Success | — |
| 49 | `:710` | `settings.storage.licenseKeysCleared` | Success | — |
| | **`Features/Shell/SettingsPage.About.cs` (3)** ||||
| 50 | `:157` | `settings.about.diagnosticsCopied` | Success | — |
| 51 | `:176` | `settings.about.noticesMissing` | **Informational** | the file is absent |
| 52 | `:182` | `settings.about.noticesMissing` | **Warning** | the SAME sentence at a HIGHER severity because `Process.Start` threw — port both arms |
| | **`Features/Shell/SettingsPage.Playback.cs` (1)** ||||
| 53 | `:296` | `playback.runtime.viewSignatureFailed` | Warning | the signature sheet (W33) could not be built |
| | **`Features/Shell/SettingsPage.Notifications.cs` (1)** ||||
| 54 | `:119` | `Describe(SimResult)` — four outcome sentences (`settings.notify.outcome*`) | **varies** | **6000 ms** — the only 6 s toast; the "Send event" simulator (27) also `Bump()`s the page |
| | **`Features/Shell/PlayLinkDialog.cs` (4)** ||||
| 55 | `:60` | `play.noOwner` | **Informational** | the `wavee://play?link=` lane — no dialog, so no status row to answer in (§6.7, audit 32) |
| 56 | `:73` | `play.noOwner` | Informational | same lane, after the async resolve |
| 57 | `:84` | `PlayLinkActions.ErrorText(ex, play.failed)` | Error | **dedupe** `wavee.play.failed` · **action** `play.tryAgain` → `PlayDirect` |
| 58 | `:191` | `PlayLinkActions.ErrorText(ex, play.failed)` | Error | **dedupe** `wavee.play.failed` · **action** `play.tryAgain` → `Submit` |
| | **`Features/Shell/PlayerBar.cs` (1)** ||||
| 59 | `:678` | `detail.addedFirstToQueue(n)` when `n < total`, else `detail.addedToQueue(n)` | Success | a partial add says so |
| | **`Features/Shell/PlaybackRuntimeSetupCard.cs` (1)** ||||
| 60 | `:467` | `playback.runtime.ready` | Success | suppressed when the model is wizard-hosted (28) |
| | **`Features/Feedback/ReportDialog.cs` (6)** ||||
| 61 | `:207` | `report.preparing` | **Informational** | a submit before the payload finished composing — returns `false`, the dialog stays open |
| 62 | `:212` | `report.titleRequired` | Warning | validation lives in the toast, not in a field error |
| 63 | `:471` | `report.copied` | Success | — |
| 64 | `:487` | `report.saved(path)` | Success | the sentence NAMES the path |
| 65 | `:491` | raw `ex.Message` | Error | the save failed |
| 66 | `:519` | `report.copiedPaste(channel.PasteBox)` | Success | **8000 ms** — the longest toast in the app: it is an instruction the user must act on in a browser |
| | **`Features/Feedback/ReportChrome.cs` (1)** ||||
| 67 | `:55` | `common.crashLastRun` | Warning | **sticky (`DurationMs = 0`)** · **dedupe** `crash.pendingReport` · **action** `report.reportOnGithub` → `ReportRequests.Open(Crash, …)` |
| | **`App/PlaybackBridge.cs` (6)** ||||
| 68 | `:952` | the remote-command sentence (`msg`) | Warning | **action** `player.chooseDevice` → bumps `DevicePickerRequest` |
| 69 | `:1043` | `player.localPlaybackUnsupported` | Error | **action** `player.chooseDevice` |
| 70 | `:1071` | `player.remoteCommandFailed` | Error | — |
| 71 | `:1089` | the typed local-failure message | Error | **dedupe** `PlayLinkActions.FailureToastKey` — §0 #10: this lane and the paste-a-link card answer for ONE failure; also drives the player bar into its Error state · optional retry action |
| 72 | `:1228` | `videoOverride.missingToast` | Warning | **action** `videoOverride.manage` — **null** when `OpenVideoOverrideManager` is unset, so the card degrades to a plain sentence |
| 73 | `:1241` | `videoOverride.unplayableToast` | Error | the same conditional action |
| | **`App/NotificationCenterBridge.cs` (2)** ||||
| 74 | `:147` | `notifications.undoFailed` | Warning | — |
| 75 | `:232` | `AppUpdateToasts.Plan(previous, next).Body` | **from the plan** | the only site that sets **`Title`** (`:234`), a conditional duration (`plan.Sticky ? 0 : 5000`, `:236`), **dedupe** `update` (`:237`), `plan.Actions[0]` only (`:238-239`) and **`CustomContent`** while Downloading (`:240`) — §0 #11, W15, W20. Suppressed entirely when the App-updates dial is `Off` (`:227`) |
| | **`Features/Detail/PlaylistEditErrors.cs` (1)** — the mapped-error chokepoint every playlist write funnels through ||||
| 76 | `:24` | `PlaylistEditErrorKinds.KeyFor(kind, verb)` | **Informational** when `IsInformational(kind)` (queued offline / still syncing), else **Error** | one call site, N sentences — 06. "Queued offline" is NOT an error: dressing it as one told users their edit was lost |
| | **`Features/Detail/PlaylistInlineEdit.cs` (1)** ||||
| 77 | `:146` | `detail.edit.pickCover` | Warning | a non-JPEG cover |
| | **`Features/Detail/PlaylistPicker.cs` (2)** ||||
| 78 | `:82` | `detail.addedToPlaylist(name)` | Success | **no** action — the picker dialog is still up |
| 79 | `:109` | `detail.addedToPlaylist(name)` | Success | **action** `detail.goToPlaylist` |
| | **`Features/Detail/DetailTracks.cs` (3)** ||||
| 80 | `:3081` | `detail.addedToPlaylist(_model.Title)` | Success | a recommendation → this playlist |
| 81 | `:3789` | `detail.tuning.applied` | Success | the `Tune ▾` command — the same one W21's teaching tip points at |
| 82 | `:3792` | `detail.tuning.applyFailed` | Error | — |
| | **`Features/Detail/DetailShell.cs` (3)** ||||
| 83 | `:342` | `detail.addedToQueue(SongCount(n))` | Success | Add to queue |
| 84 | `:347` | `detail.addedToQueue(SongCount(n))` | Success | Play next |
| 85 | `:353` | `detail.addedToPlaylist(plName)` | Success | **action** `detail.goToPlaylist` |
| | **`Features/Detail/ArtistGalleryLightbox.cs` (2)** ||||
| 86 | `:386` | **hard-coded** `"Image exported"` | Success | §6.10 — unlocalised; record it, do not invent a key silently |
| 87 | `:394` | **hard-coded** `"Image export failed: " + ex.Message` | Error | §6.10 |
| | **`Features/Library/LibraryPage.cs` (3)** ||||
| 88 | `:1083` | `detail.addedToQueue(SongCount(n))` | Success | Add to queue |
| 89 | `:1089` | `detail.addedToQueue(SongCount(n))` | Success | Play next |
| 90 | `:1095` | `detail.addedToPlaylist(plName)` | Success | **action** `detail.goToPlaylist` |
| | **`Features/DragDrop/WaveeResourceDrag.cs` (3)** ||||
| 91 | `:433` | `drag.cantMoveHere` | **Informational** | a refusal, deliberately not a warning |
| 92 | `:472` | `drag.movedTo(name)` ｜ `drag.movedManyTo(count, name)` ｜ `drag.movedToLibrary` | Success | **action** `sidebar.pin.undo` → `UndoAsync` when `undoMoves` is non-empty · **announce** `:464` `SayThrottled(where)` — the plural sentence NAMES the destination |
| 93 | `:582` | `detail.addedToPlaylist(targetName)` | Success | a drop onto a playlist |
| | **`Features/Sidebar/Pane/SidebarPane.cs` (2)** ||||
| 94 | `:2416` | `RefusalSentence(SidebarDropRefusal)` — the five refusals (29 §2 W10) | **Informational** | **action** only for `SortedList`: `SidebarPaneLoc.SortCustom` → `Config.SortedListRefusalAction` (issue #85 H3). A refused drop also logs a warning — nothing in the drop path may fail by returning |
| 95 | `:2821` | `detail.addedToPlaylist(name)` | Success | **action** `detail.goToPlaylist` |
| | **`Features/Player/QueuePanel.cs` (2)** ||||
| 96 | `:282` | `drag.cantMoveAcrossSections` | **Informational** | — |
| 97 | `:330` | `detail.addedFirstToQueue(n)` ｜ `detail.addedToQueue(n)` | Success | the same partial-add rule as `PlayerBar.cs:678` |
| | **`Features/Player/LyricsInspectorDialog.cs` (4)** — a diagnostics surface: all four sentences are hard-coded English (§6.10) ||||
| 98 | `:138` | **hard-coded** `"Lyrics report copied"` | Success | — |
| 99 | `:180` | **hard-coded** `"Lyrics evidence bundle saved"` | Success | also opens the folder |
| 100 | `:386` | **hard-coded** `$"{p.SourceId} payload copied"` | Success | interpolated, per candidate |
| 101 | `:463` | **hard-coded** `"Parsed lyrics copied"` | Success | — |
| | **`Features/Home/HomePage.cs` (2)** ||||
| 102 | `:69` | `home.playFailed` | Error | — |
| 103 | `:1071` | `home.facetFailed` | Error | — |
| | **`Features/ReleaseNotes/ReleaseNotesPage.cs` (1)** ||||
| 104 | `:86` | `whatsNew.linkCopied` | Success | 28 |
| | **`Features/Diagnostics/PlaybackRuntimeDiagnosticsPage.cs` (1)** ||||
| 105 | `:309` | **hard-coded** `"Diagnostics copied"` | Success | §6.10 · 27 |
| | **`SpotifyLive/LiveSessionHost.cs` (1)** ||||
| 106 | `:637` | `playback.runtime.missing` | Warning | **action** `playback.runtime.setUp` → `svc.Playback.OpenPlaybackRuntimeSetup.Value++` — the third door into the setup dialog (W24), beside the banner and Settings |

**Five rules the census makes visible, and a re-author must keep.**

1. **Severity is a judgement, not a lookup.** An unpin (22), a restored override (34), the refusals (19, 91, 94, 96)
   and "queued offline" (76) are all **Informational** even though each is a negative outcome. Warning is for
   *something is wrong and you can fix it*; Error is for *the thing you asked for did not happen*.
2. **A refusal is never silence.** `n == 0`, an empty container, a drop that cannot be honoured and a no-op log sweep
   all raise a card (6, 7, 44, 91, 94, 96). The one shipped exception is deliberate: a successful playlist create
   announces instead (27), because the new row appearing IS the confirmation.
3. **The undo lives in the toast, not only in the panel.** Sites 8, 13, 14, 21, 22, 23, 28, 32, 36 and 92 carry one,
   and `Toast.cs:328-330` dismisses the card when it is pressed. A filing mistake is noticed immediately, while the
   user is still on the page they filed from, so the escape has to be in the confirmation itself — the notification
   panel's Undo (W16) is the second chance, not the first.
4. **Raw `ex.Message` reaches the user at 11 sites** (1, 4, 17, 24, 29, 31, 33, 37, 38, 40, 65, plus the
   interpolated 87). That is the 0.2.9 behaviour, and it is exactly what `PlaylistEditErrors` (76) exists to avoid;
   0.3 must either port them as they are or route each through a mapper deliberately — not "tidy" them into one
   generic sentence.
5. **One event, one card.** The only two lanes that can both answer for one failure share a key (57, 58, 71 →
   `wavee.play.failed`), and the update lifecycle is one card for its whole life (75 → `update`). Everything else
   relies on the message text being its own key (`Toast.cs:41-46`), which is what makes an accidental
   double-`Show` of the same sentence a refresh rather than a stack.

**Where the rest of this table is cross-referenced.** 01 (`TrackActions` 9–12, `Menus` 13–16), 06
(`ContainerActions` 1–8, `PlaylistEditErrors` 76, `PlaylistInlineEdit` 77, `PlaylistPicker` 78–79), 15/25/26
(`PinActions` 21–22, `FolderActions` 23, `SidebarPane` 94–95), 21 (`QueuePanel` 96–97, `PlayerBar` 59),
24/27 (`VideoActions` 28–34, `SettingsPage.VideoOverrides` 36–42, `Storage` 43–49), 28 (`ReportDialog` 61–66,
`ReportChrome` 67, `ReleaseNotesPage` 104) and 29 (`WaveeResourceDrag` 91–93, whose refusals are 29 §2 W10). Those
chapters describe the *action*; this table is the authority for the *card it raises*.

---

## 4. Colour & material

**No cover palette anywhere in this surface.** Every overlay in this chapter is chrome, not content: it reads
`Tok.*` and `WaveeColors.*` only, and it must keep doing so. The one artwork touch is the social / new-release
thumbnail (`Surfaces.Artwork`), which is an image, not a derived tint.

### 4.1 Materials by surface

| surface | material | why | file:line |
|---|---|---|---|
| Command palette | `Tok.AcrylicFlyout` (in-window acrylic layer; `Fallback` fill under it) + 1px `Tok.StrokeFlyoutDefault` + `Elevation.Flyout` + `Radii.Overlay` 8 | `PopupChrome.Popup` → `FlyoutSurface`'s single frosted card, `ClipToBounds`, **no presenter padding** | `OverlayHost.cs:1612-1628` |
| Profile menu | **windowed** DWM `DWMSBT_TRANSIENTWINDOW` acrylic + system shadow + rounded corners, with an engine-drawn plate (acrylic + 1px `StrokeFlyoutDefault` + `Elevation.Flyout`) inside | `PopupChrome.Flyout` + `ConstrainToRootBounds = false` | `OverlayHost.cs:867-873`, `ProfileMenu.cs:169-172` |
| Notification panel | same acrylic card as the palette, `ConstrainToRootBounds = false` so it may escape the window | `PopupChrome.Popup` | `NotificationPanel.cs:36-39` |
| Toast card | **opaque** `Tok.FillSolidTertiary` plate + 1px `Tok.StrokeSurfaceDefault`, the severity tint composed on top | without the solid plate the `Informational` tint (a translucent accent wash) made the card read as see-through | `Toast.cs:353-362` |
| Runtime banner | **opaque** `Tok.FillSolidBase` plate under the caution tint | it floats over artwork, not chrome | `PlaybackRuntimeBanner.cs:36-38` |
| Modal cards (logout, play-link, setup, signature) | `Tok.FillSolidBase` + 1px `Tok.StrokeSurfaceDefault` + `Elevation.Dialog`, over a `Tok.FillSmoke` `#0000004D` scrim | `PopupChrome.Modal` | `ProfileMenu.cs:324-325`, `PlayLinkDialog.cs:216-217`, `ContentDialog.cs:355-368`, `OverlayHost.cs:1335` |
| Teaching tip | `SolidBackgroundFillColorTertiary` + 1px `SurfaceStrokeColorDefault` + the 1px top highlight | `PopupChrome.TeachingTip`; **no scrim, no acrylic** | `TeachingTip.cs:12-15` |
| Setup dialog content region | `Tok.FillLayerAlt` over the card's `FillSolidBase` (the WinUI `ContentDialogTopOverlay`) | the command row stays `FillSolidBase`, so the two bands are one rung apart | `ContentDialog.cs:315,350` |
| Detail fact boxes (verify / ready) | `Tok.FillLayerAlt` + 1px `Tok.StrokeCardDefault` | a third rung inside the content region | `:644-646,708-709` |

### 4.2 Severity ramp (`SeverityVisuals.cs:20-26`) — shared by InfoBar, Toast and the banner, so they cannot drift

| severity | status glyph | icon background | icon foreground | card background (dark → light) |
|---|---|---|---|---|
| Informational | `StatusInfo` F13F | `Tok.SystemFillAttention` (= `AccentDefault`) | `Tok.TextInverse` | `SystemFillAttentionBackground` `#FFFFFF08` → `#F6F6F680` |
| Success | `StatusSuccess` F13E | `Tok.SystemFillSuccess` `#6CCB5F` → `#0C6B0C` | `Tok.TextInverse` | `#393D1B` → `#DFF6DD` |
| Warning | `StatusWarning` F13C | `Tok.SystemFillCaution` `#FCE100` → `#9D5D00` | `Tok.TextInverse` | `#433519` → `#FFF4CE` |
| Error | `StatusError` F13D | `Tok.SystemFillCritical` `#FF99A4` → `#C42B1C` | `Tok.TextInverse` | `#442726` → `#FDE7E9` |

`Tok.TextInverse` = `#000000E4` dark / `#FFFFFF` light. Background glyph is the filled circle `Icons.InfoBarBackgroundCircle` F136, drawn under the status glyph in a 16 × 16 ZStack.

### 4.3 The premium ink (the one hand-authored colour in this surface)

`ProfileMenu.cs:29` `Gold = #E6C26C` (dark) / `ProfileMenu.cs:300,306` `#8A6312` (light). Applied to both the
`FavoriteStar` E734 glyph at 10 DIP and the `Spotify Premium` badge text at 12. Free accounts get
`Tok.TextSecondary` and a 10-wide invisible spacer in place of the star, **so the two lines have identical
baselines** (`ProfileMenu.cs:314`).

### 4.4 Light / dark differences to reproduce

- The menu/flyout stroke changes rung: `Tok.StrokeFlyoutDefault` `#00000033` dark → `#00000017` light.
- `WaveeColors.RowHover` / `RowPressed` **invert polarity**: white-alpha `#FFFFFF0F` / `#FFFFFF0A` in dark
  (`PaletteBuilder.cs:160,162`), black-alpha `#0000000D` / `#00000012` in light (`PaletteBuilder.cs:118-119`). The
  notification row hover is therefore *lighter* in dark and *darker* in light.
- **Pressed is NOT uniformly "stronger" than hover — and must not be made so.** In dark the pressed alpha is
  *lower* than the hover alpha (`0x0A` vs `0x0F`): pressing a row makes it **dimmer**, which is WinUI's own
  Subtle ramp (`SubtleFillColorTertiary` < `...Secondary`). That ramp holds in **both** themes for every
  `Interaction.Subtle` surface in this chapter — the profile chip, the menu rows, the setup dialog's setting rows,
  the InfoBar close button — since `Tok.FillSubtleSecondary`/`Tertiary` are `#FFFFFF0F`/`#FFFFFF0A` dark and
  `#00000009`/`#00000006` light (`PaletteBuilder.cs:214-215,302-303`). Only `WaveeColors.Row*` in **light** has
  pressed darker than hover. Four of the five rungs get *quieter* on press; a re-author that "corrects" them to
  darken on press changes every menu and every row in the shell.
- `Tok.FillLayerAlt` is `#FFFFFF0D` (a 5 % white overlay) in dark but **opaque `#FFFFFF`** in light — which is why
  `ContentDialog` feathers its scroll edges by alpha rather than by an EdgeCue colour band
  (`ContentDialog.cs:301-306`). The setup dialog inherits that; do not add a colour cue.
- `Tok.AccentTextPrimary` (the LinkButton / hyperlink ink) is `#A6D8FF` dark / `#004275` light — always the
  contrast-corrected **ink**, never the raw accent fill.

### 4.5 Transitions of colour

Nothing in this surface cross-fades a colour. Every state colour is a resting `Fill`/`HoverFill`/`PressedFill`
declared on the element, resolved by the engine's surface resolver per frame — so a theme flip re-themes in place
(the theme-text-brush recycle path, `Typography.cs:26-36`) with no remount.

---

## 5. Motion

Every animation below is driven by the engine animation engine off `FrameClock.PresentQpc`. The 30-second
notification refresh is `UseInterval` on the **host frame clock**, which auto-pauses while parked/minimized
(`NotificationPanel.cs:63`, `RenderContext.Timers.cs:333`) — it replaced a `System.Threading.Timer` + post marshal.
The toast countdown runs on `HostTimerQueue.NowMs` (`Toast.cs:251`). **There is no `Environment.TickCount64` and no
`Stopwatch` anywhere in this surface** (verified by grep across all 11 primary files).

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| Palette open (Ctrl+K) | popup surface | Opacity | 0 → 1 | 83 ms | Linear | **83 ms delay** | opacity kept | `OverlayHost.cs:1229` |
| Palette open | popup surface | TranslateY | **−50 → 0** — the card starts 50 DIP ABOVE its resting place and settles DOWNWARD (`PopupEntranceSlide`: `opensUp ? +50 : −50`, and a `BottomEdgeAligned*` popup with room below opens **down**) | 367 ms | `FluentDecelerate` cubic-bezier(0.1,0.9,0.2,1) | 0 | snapped | `OverlayHost.cs:1226-1228`, `:1413-1422` |
| Palette close | popup surface | Opacity | 1 → 0 | 83 ms | Linear | 0 | opacity kept | `OverlayHost.cs:583-604` |
| **Notification panel open** (bell or menu row) | popup surface | Opacity | 0 → 1 | 83 ms | Linear | **83 ms delay** | opacity kept | `OverlayHost.cs:1229` |
| **Notification panel open** | popup surface | TranslateY | −50 → 0 (same `PopupChrome.Popup` transition as the palette; `BottomEdgeAlignedRight`, so it also settles downward) | 367 ms | `FluentDecelerate` | 0 | snapped | `NotificationPanel.cs:36`, `OverlayHost.cs:1226-1228` |
| **Notification panel close** (light dismiss / Escape / anchor re-click) | popup surface | Opacity | 1 → 0 | 83 ms | Linear | 0 | opacity kept | `OverlayHost.cs:583-604` |
| Palette selection move (↑/↓) | row fill | — | — | **none** (instant repaint) | — | — | — | `WaveePalette.cs:129-134` |
| Profile menu open | plate node | ScaleY | 0.5 → 1 (pivot: bottom edge for a downward menu) | 250 ms | `FluentPopOpen` cubic-bezier(0,0,0,1) | 0 | snapped | `OverlayHost.cs:1309` |
| Profile menu open (in-window path) | surface | TranslateY + ClipT/ClipB | ∓slide → 0 | 250 ms | `FluentPopOpen` | 0 | snapped | `OverlayHost.cs:1286-1296` |
| Profile menu open (windowed path) | whole popup HWND | composition slide | `closedRatio` 0.5 | 250 ms | (DWM) | 0 | — | `OverlayHost.cs:1108`, `:880` |
| Sub-menu (`Play ▸`) hover-open | cascade popup | same unfold, `closedRatio` 0.67 | — | 250 ms | `FluentPopOpen` | **`SystemParams.MenuShowDelayMs`, default 400 ms** | snapped | `MenuFlyout.cs:70`, `OverlayHost.cs:881` |
| Menu close | surface | Opacity | 1 → 0 | 83 ms | Linear | 0 | opacity kept | `OverlayHost.cs:580-604` |
| Modal open (logout / play-link / setup / signature) | card | ScaleX + ScaleY | 1.05 → 1 | 250 ms | `FluentPopOpen` | 0 | snapped | `OverlayHost.cs:1185-1186` |
| Modal open | card | Opacity | 0 → 1 | 83 ms | Linear | 0 | opacity kept | `OverlayHost.cs:1187` |
| Modal open | scrim | Opacity | 0 → 1 | 83 ms | Linear | 0 | opacity kept | `OverlayHost.cs:1181` |
| Notification row mount | card | TranslateY + Opacity | +6, 0 → 0, 1 | 300 ms (`MotionTok.StandardEnter`) | `FluentDecelerate` | 0 | `KeepFade` — the fade survives, the slide snaps | `NotificationPanel.cs:196`, `MotionTok.cs:168` |
| Notification row unmount | card | TranslateY + Opacity | 0, 1 → −4, 0 | 200 ms (`MotionTok.StandardExit`) | `FluentAccelerate` cubic-bezier(0.9,0.1,1,0.2) | 0 | `KeepFade` | `NotificationPanel.cs:197`, `MotionTok.cs:169` |
| Notification list reorder (mark-all-read, feed rebuild) | card position | Position | old → new | spring | `TransitionDynamics.Default` = Spring(response 0.30 s, damping 0.85) | 0 | snapped | `NotificationPanel.cs:198`, `LayoutTransition.cs:74,113` |
| Activity card expand / collapse | the wrapper | — | — | **none declared on the wrapper** — but the keyed child at that list slot changes identity (`ntf:act:<id>` ⇄ `ntf:actwrap:<id>`), so the card's OWN terminals below are in play; the siblings ride their `LayoutTransition.Slide` springs either way. Frame-record it (parity 38b) rather than assuming a silent swap | — | — | — | `NotificationPanel.cs:432,437,445-449` |
| Activity card mount / unmount | card | TranslateY + Opacity | +6, 0 → 0, 1 · 0, 1 → −4, 0 | 300 / 200 ms | `FluentDecelerate` / `FluentAccelerate` | 0 | `KeepFade` | `NotificationPanel.cs:438-440` — a SECOND declaration, identical to the generic `Card`'s at `:196-198`; both must move together |
| Relative-time refresh | every `RelTime` string | text | recomputed | — | — | **every 30 000 ms while the panel is open** | unaffected | `NotificationPanel.cs:48,63` |
| Update download progress (panel) | `ProgressBar.Determinate` width | Width | fraction | per-publish | — | — | unaffected | `NotificationPanel.cs:271` |
| Update download progress (toast) | bound bar | Width | `UpdateProgress` FloatSignal | compositor-only, **no re-render** | — | — | unaffected | `NotificationCenterBridge.cs:58,223,254` |
| Indeterminate bar (setup) | two clipped indicators, 40 % and 60 % of track | TranslateX | sweep | 2 000 ms loop | (ProgressBar storyboard) | — | — | `ProgressBar.cs:42-47` |
| Indeterminate ring (`BusyRow`) | arc | Rotation / sweep | — | 2 000 ms loop | cubic-bezier(0.167,0.167,0.833,0.833) | — | — | `ProgressRing.cs:73-75` |
| Toast enter (BottomRight) | card | TranslateX + Opacity | +48, 0 → 0, 1 | 300 ms | `FluentDecelerate` (`MotionTok.StandardEnter`) | 0 | `KeepFade` | `Toast.cs:285,363-365` |
| Toast exit | card | TranslateX + Opacity | 0, 1 → +48, 0 | 200 ms | `FluentAccelerate` | 0 | `KeepFade` | `Toast.cs:364-365` |
| Toast auto-dismiss | — | — | — | `DurationMs` (default 5 000; **0 = sticky**) | — | **paused while the pointer is over the strip, remainder banked and re-armed on exit** | unaffected | `Toast.cs:33,240-261,303-304` |
| Toast coalesce (same `DedupeKey`) | existing card | countdown | restart from full `DurationMs`; newer action adopted; **message NOT overwritten** | — | — | — | — | `Toast.cs:179-199` |
| Teaching tip open | tip card | ScaleX / ScaleY | `Min(0.01, 20/W)` → 1, `Min(0.01, 20/H)` → 1 | 300 ms | cubic-bezier(0.1,0.9,0.2,1) | **two nested `post()` frames** after arm | snapped (no opacity track exists) | `OverlayHost.cs:1162-1174`, `WaveeTips.cs:129` |
| Teaching tip close | tip card | Scale | 1 → 20/W | 200 ms | cubic-bezier(0.7,0,1,0.5) | 0 | snapped | `TeachingTip.cs:42-44` |
| Runtime banner appear / disappear | the whole chrome subtree | — | — | **none** — `PlaybackRuntimeChrome` returns a bare `BoxEl { Shrink = 0 }` when gated off | — | — | — | `PlaybackRuntimeBanner.cs:109` |
| Setup phase change | body + footer | — | — | **none** — the two child components re-render in place; the dialog never re-opens | — | — | — | `PlaybackRuntimeSetupCard.cs:43-44` |
| OS toast progress push | Windows Action Center `<progress>` | value + status | live | — | — | **throttled to 5 % steps** (`ProgressStepPercent`), and always pushed at 100 % | — | `ToastEscalator.cs:107,125-132` |
| Toast card (both terminals) | card | — | — | — | — | `Transition = MotionTok.StandardEnter` — the card's own layout transition, distinct from the Enter/Exit terminals | — | `Toast.cs:365` |
| Bell unread badge | `InfoBadge.Count` | — | — | **none** — the badge appears/disappears with the count, no scale or fade, and is a zero-footprint ZStack overlay so the button never resizes | — | — | — | `ShellToolbar.cs:129-141` |

---

## 6. Interaction

### 6.1 Command palette

| gesture | result | file:line |
|---|---|---|
| **Ctrl+K** | toggles `_paletteOpen` (`OpenPalette` — a *toggle*, not an open) | `WaveeShell.cs:2034,2056` |
| typing | rebuilds the hit list every keystroke; selection clamps to `[0, count−1]` | `WaveePalette.cs:100-101` |
| **↓ / ↑** | moves selection with **wrap-around** (`(sel+1) % count`, `(sel−1+count) % count`) | `WaveePalette.cs:129-134` |
| **Enter** | invokes the selected entry, then closes | `WaveePalette.cs:135-136,118-123` |
| **Escape** | closes. The shell's own Escape handler bails while the palette is open (it is a sibling layer, not an overlay, so the overlay Escape ordering does not cover it) | `WaveePalette.cs:137-138`, `WaveeShell.cs:2117` |
| click a row | invokes + closes | `WaveePalette.cs:166` |
| `>` prefix | commands only; leading spaces after `>` are skipped; no catalog-search row | `WaveeCommands.cs:96-102,121` |
| focus on open | `FocusTrap: true` + `FirstFocusableIn(wrapper)` lands on the **chromeless `EditableText`**, never a `PartRoot` chrome node | `WaveePalette.cs:14-15`, `OverlayHost.cs:1150-1158` |
| screen reader | `"Command palette"` once on open, then throttled `"N matching commands"` / `"No matching commands"` per edit | `WaveePalette.cs:103-113` |

Invoke targets (`WaveeCommands.cs:134-193`): `Navigate` → `host.Go(routeKey, null)`; `Playback` →
`PlayerBarContent.TogglePlayPause` / `Player.NextAsync` / `PreviousAsync` / `ToggleShuffle` / `CycleRepeat`;
`Settings` → `ToggleTheme` / flip `CrossfadeEnabled` / `WaveeShell.ZoomStep(±1|0)` / `NpvPlayerPrefs.*` tagged
`NpvDiagnostics.SourcePalette`; `Registry` → `registry.Execute(actions, binding)`; `Library` →
`PlaylistCreateFlow.Create(navigate: true)` / `FolderActions.NewFolder`; `CatalogSearch` → `host.Go("search", query)`.

### 6.2 Profile chip & menu

- **Click the chip** → toggle the flyout (`handle.Value is { IsOpen: true }` → `Close()`), `ProfileMenu.cs:147`.
- `Role = AutomationRole.Button`, `Focusable = true`, `OnRealized` captures the anchor (`ProfileMenu.cs:180-181`).
- Menu rows, in order, with their conditions:

| # | label (loc key) | icon | condition |
|--:|---|---|---|
| 1 | Account (`auth.account`) | `Contact` E77B | always → `LoginView.OpenUrl("https://www.spotify.com/account")` |
| 2 | Settings (`auth.settings`) | `Settings` E713 | always → `go("settings", null)` |
| 3 | Play ▸ (`play.menu`) | `MusicNote` EC4F | `LocalFileActions.CanPlayFiles(actions)` |
| — | separator | | when row 4 or 5 exists |
| 4 | Notifications / `notifications.overflowTitle(n)` | `Bell` EA8F | `nc != null && layout.ActionsInMenu` |
| 5 | Friends (`shell.friends`) | `Friends` E716 | `layout.ActionsInMenu` |
| — | separator | | always |
| 6 | Light theme / Dark theme (`shell.lightTheme` / `shell.darkTheme`) | `Sun` E706 / `Moon` E708 | always — labelled and glyphed with the **target** theme |
| — | separator | | always |
| 7 | Log out (`auth.logOut`) | `SignOut` F3B1 | always → confirm modal |

- `Play ▸` children: File… (`play.file`, `Document` E8A5), Link… (`play.link`, `Link` E71B), then — behind a
  separator — one row per installed module declaring the `match` capability, glyph `Globe` E774, label from
  `PlayLinkActions.MenuLabel` (`ProfileMenu.cs:112-143`).
- **Every row closes the menu first, then acts** (`Close(); …`) — `ProfileMenu.cs:119,122,139,155-161`.
- Keyboard: `MenuFlyout` roving ↑/↓/Home/End moves the cursor **and** focus; Enter/Space activate; → opens a
  sub-menu, ← closes it; first-letter jump; Escape light-dismisses (`MenuFlyout.cs:196-215`).
- **No tooltip on the chip.** The bell button in the trailing island is tooltipped
  (`Loc.Get(Strings.Notifications.Title)`, `ShellToolbar.cs:143`); the chip is not.

### 6.3 Notification panel

| gesture | result | file:line |
|---|---|---|
| open (either anchor) | `nc.OnPanelOpened()` → refetch stale feeds, advance both last-seen watermarks, drop the per-item read set, rebuild | `NotificationPanel.cs:41`, `NotificationCenterBridge.cs:96-102,165-174` |
| click a filter pill | `nc.SetFilter(cat)`; `null` = All | `NotificationPanel.cs:154` |
| `Mark all read` | activity read flags + both watermarks + clear injected simulated rows | `NotificationCenterBridge.cs:108-118` |
| `Clear` | **only rendered while the Activity filter is active**; clears the local activity log | `NotificationPanel.cs:132-133` |
| click an update row | nothing — the row is not a button; only its pill buttons act (`Card(..., onClick: null)`) | `NotificationPanel.cs:177` |
| `Update now` / `Retry` | `AppUpdateSurface.Resolve(svc).ApplyAsync` — **the simulator when one is walking** | `NotificationPanel.cs:230,246,257` |
| `What's new` | `go("whatsnew", notesVersion)` then close the panel. `notesVersion` = the running build's core version when `Completed`, else `TargetSemVer` ?? `ReleaseTagVersion(TargetQuad)` | `NotificationPanel.cs:231-239` |
| `Later` | `up.Snooze()` — offered only in `Available`, not `Snoozed` | `NotificationPanel.cs:249-250` |
| `Dismiss` | `up.Acknowledge()` | `NotificationPanel.cs:254,259` |
| click a social row | `Navigate` + a routable uri → `go(route)`; otherwise `LoginView.OpenUrl(SpotifyLink.WebUrl(uri))`. Both close the panel | `NotificationPanel.cs:330-341` |
| click a new-release row | Album + routable → `go(route, name)`; Episode (no detail route yet) → web player | `NotificationPanel.cs:371-377` |
| click an activity row | toggles expansion (`expanded.Value = isExpanded ? -1 : e.Id`) — **one at a time** | `NotificationPanel.cs:437` |
| `Undo` | `nc.UndoAsync(entry)`; a failed undo raises a Warning toast `notifications.undoFailed` | `NotificationPanel.cs:416`, `NotificationCenterBridge.cs:143-148` |
| `Open` (in an expanded activity) | `go(route, targetName)` + close | `NotificationPanel.cs:467` |
| light dismiss / Escape | closes (`DismissBehavior.LightDismiss`) | `NotificationPanel.cs:36` |

**Accessibility names**: every clickable card sets `Role = AutomationRole.Button` + `Cursor = Hand` +
`Focusable = true`; non-clickable cards set `Role = None` + `Cursor = Arrow` + `Focusable = false`
(`NotificationPanel.cs:190-195`). The panel has `FocusTrap: true`.

Relative time (`NotificationPanel.cs:630-639`): `< 1 min` → `friends.now`; `< 60 min` → `friends.minAgo(n)`;
`< 24 h` → `friends.hrAgo(n)`; else `friends.dAgo(n)`. Negative ages clamp to 0.

### 6.4 Toasts

- The card's single action button is `Button.Standard(label, () => { OnAction(); Close(); })` — invoking an action
  **always** dismisses the toast (`Toast.cs:328-330`).
- Hovering anywhere on the strip pauses every visible countdown; leaving resumes each with its banked remainder
  (`Toast.cs:225-261`). A strip that unmounts with a stuck hover clears the pause (`Toast.cs:211,221,272`).
- `Closable` defaults true → a 38 × 38 `Icons.Cancel` E711 close button inside the InfoBar body, tooltipped `Close`
  (`InfoBar.cs:278-311`).
- App toast call sites in this surface: `notifications.undoFailed` (Warning),
  `playback.runtime.ready` (Success, suppressed when wizard-hosted), `play.noOwner` (Informational),
  `PlayLinkActions.ErrorText` (Error, `DedupeKey` `wavee.play.failed`, action `play.tryAgain`), and the update
  lifecycle card (`DedupeKey` `update`) — rows 55–58, 60, 67, 74–75 of **§3.2**, which is the app-wide census of
  all 106 `Toast.Show` sites, not just this surface's seven.

### 6.5 OS toasts (`ToastEscalator`)

Activation launches (`ToastEscalator.cs:205-242`): new release → `wavee://play?ctx=<uri>`; social navigate →
`wavee://open?route=<uri>`; update completed → `wavee://open?route=whatsnew&arg=<semver>`; summary →
`wavee://open?route=home`. Followers get a **circular** app logo; everything else square
(`ToastEscalator.cs:173`). `Available` deliberately raises **no** OS banner
(`ToastEscalator.cs:223-226`), and `ActivityNotification` never becomes a banner at all (`:241`).

Five more gates, each of which silences the whole lane and so counts as a state:

| gate | rule | file:line |
|---|---|---|
| host support | `ToastNotifier.IsSupported` false ⇒ 0 banners, nothing recorded | `ToastEscalator.cs:42` |
| **first run** | `watermark <= 0` ⇒ the pass only RECORDS where the feed was; enabling notifications never replays history | `ToastEscalator.cs:64-65` |
| dial | `policy.WindowsEnabled` false, or the row's own topic below Windows / inside quiet hours (`RaisesToastNow`) | `:65,75` |
| already-seen | `ts <= watermark` or the row is read | `:73` |
| update identity | `state:targetQuad` unchanged ⇒ skipped; progress is deliberately NOT part of the identity | `:79,112-119` |

The walk runs **oldest → newest** (`for (int i = items.Count - 1; i >= 0; i--)`, `:69`) so a truncated burst keeps
the FRESHEST rows, and the watermark advances past everything **considered**, not just what was raised (`:86`).
The overflow summary is singular-aware: `"1 more update in Wavee"` / `"N more updates in Wavee"` over
`"Open Wavee to see them."` (`:192-194`). With `policy.Sound` false every banner — summary included — is raised
`Silent()` (`:168,196`). Remote art is localized to a file first because the unpackaged AUMID image path silently
drops http(s), and a failure there is swallowed: the text still says what happened (`:170-175`).

### 6.6 Teaching tips

- The ✕ **is** "don't show again" — the only acknowledgement path the control owns (`WaveeTips.cs:149-150`).
- `Acknowledge(settings, id)` is also called wherever using the taught affordance counts as taught.
- `Close(id)` takes it down **without** burning the id (navigating away, the anchor being evicted) — the per-launch
  latch is what stops it re-opening on the next page in the meantime (`WaveeTips.cs:172-180`).
- The tip is also closed automatically when its anchor leaves the scene (the overlay host's orphaned-owner prune).
- Arm-time liveness gate: the anchor node must be non-null, **live**, and **not `Parked`** — a `KeepAlive` page that
  is parked is just as ineligible as a dead one (`WaveeTips.cs:136-138`).
- **An inert handle releases the slot immediately.** A host-less mount (`NullOverlayService` in a probe) hands back
  `handle.IsOpen == false`; `TryShow` frees the single slot rather than blocking every future tip for the process
  lifetime (`WaveeTips.cs:154`).
- **`ShouldShow` answers false when the settings seam is null** — `IsSeen` returns TRUE for a null store, and
  `canPresent` is false anyway: a tip whose acknowledgement cannot be persisted is never shown, because it would
  return on every page forever (`WaveeTips.cs:70-71`, `WaveeTipsCore.cs:83-99`).
- **Placement is a parameter, not a constant.** `TryShow(..., TeachingTip.PlacementMode placement =
  TeachingTip.PlacementMode.Bottom)` (`WaveeTips.cs:113`) — today's one caller takes the default, and the engine
  honours all 13 WinUI placement states with the tail pinned to the joined edge.
- **`ResetAll(settings)`** clears `TipsSeen` **and** the per-launch `_armed` latches, so a future Settings "Show tips
  again" row works without a restart (`WaveeTips.cs:85-89`). It is a seam, not a rendered control, in 0.2.9.
- `WaveeTipIds` is **append-only** — an id is persisted the moment a user acknowledges it, so renaming or reusing one
  silently re-shows a tip an existing install already dismissed (`WaveeTipsCore.cs:6-18`). Exactly one id exists
  today: `detail.tuning`.

### 6.7 Play-link dialog

- **Enter commits.** `EditableText` raises `OnCommit` on Enter for a single-line field, so the card needs no key
  handler; Escape stays the overlay's modal dismissal (`PlayLinkDialog.cs:223-231`).
- The Play button is **disabled, not hidden**, while the field is empty or a lookup is running
  (`PlayLinkActions.cs:30`, `PlayLinkDialog.cs:129`).
- A new attempt retires the previous failure's escape hatch (`failedInput.Value = ""`, `PlayLinkDialog.cs:146`).
- `Open in browser` is gated by `ShellOpen.IsWebUrl` (http/https + a host, nothing else) — untrusted pasted text
  never reaches the shell without passing it (`PlayLinkDialog.cs:205`).
- The lookup is `UseAsyncCommand(cancelOnUnmount: true)` — closing the card withdraws the question
  (`PlayLinkDialog.cs:124`).

### 6.8 Runtime banner & setup dialog

| gesture | result | file:line |
|---|---|---|
| banner `Set up` | `PlaybackRuntimeSetupCard.Open(...)`, guarded against a second open while one is up | `PlaybackRuntimeBanner.cs:84-98` |
| banner ✕ | `settings.Set(PlaybackRuntimeSetupDismissed, true)` + `PlaybackRuntimeBannerState.Bump()` + an always-on `runtime.banner.dismiss` log line | `PlaybackRuntimeBanner.cs:113-119` |
| `bridge.OpenPlaybackRuntimeSetup` bumped elsewhere (a toast CTA, Settings) | `UseEffect` on the request signal opens the dialog | `PlaybackRuntimeBanner.cs:100-107` |
| Escape while `IsBusy` | **blocked** (`d.Closing` cancels) | `PlaybackRuntimeSetupCard.cs:46` |
| dialog closed | `model.Dispose()` → cancels the in-flight CTS | `PlaybackRuntimeSetupCard.cs:47,103` |
| `Not now` | dismiss the setting (stop offering, banner included) **and** close | `PlaybackRuntimeSetupCard.cs:128` |
| `Remove` | clear the active pointer, **re-enable** the banner (`Dismissed = false`), set status `RuntimeUnavailable`, close | `PlaybackRuntimeSetupCard.cs:410-418` |
| `View diagnostics` | inside the wizard: `SetupGating.MarkDeferred` + exit the **whole** wizard; standalone: close; then `go(PlaybackRuntimeDiagnosticsPage.Route)` | `PlaybackRuntimeSetupCard.cs:352-363` |
| `[Signature]` | nested `ContentDialog`, 548 wide | `PlaybackRuntimeSetupCard.cs:772-786` |
| `Choose a Spotify.dll…` | folder picker; on cancel, falls back to a file picker filtered to `Spotify.dll` | `PlaybackRuntimeSetupCard.cs:367-379` |

Enter routes to the default button through `ContentDialog`'s card-level `OnKeyDown` — the accent footer button
carries `TabIndex = 1` so the focus trap ranks it first (`PlaybackRuntimeSetupCard.cs:1004`, `ContentDialog.cs:1097`).

**The always-on log lines this surface owns** (no environment switch, per the working rules) — a 0.3 re-author that
drops them loses the only record of what the user was offered:
`runtime.banner.open_setup` (Info, with `status`/`dismissed`/`request`) and `runtime.banner.request` (Debug) —
`PlaybackRuntimeBanner.cs:88-94,104-105`; `runtime.banner.dismiss` (Info) — `:115-116`; and, from the model,
`runtime.setup.open` · `runtime.setup.model` · `runtime.setup.phase` (every transition, with a `reason` string) ·
`runtime.setup.catalog` · `runtime.setup.download.start` · `runtime.setup.install_selected` ·
`runtime.setup.register_dir` · `runtime.setup.untrusted.confirm` (Warning) · `runtime.setup.already_current` ·
`runtime.setup.refresh_after_success` · `runtime.setup.retry_current` / `.retry_failed` · `runtime.setup.advanced` ·
`runtime.setup.catalog.failed` (Warning) · `runtime.setup.diagnostics` · `runtime.setup.dismiss` ·
`runtime.setup.remove` (Warning) · `runtime.setup.signature_details` · `runtime.setup.failed` / `.failed_outcome`
(Warning) — `PlaybackRuntimeSetupCard.cs:30,93,122,149,208,272,312,329,354,397,412,420,432,445,473,483,489-497,505-508`.

### 6.9 No drag-and-drop, no inline edit, no selection, no right-click menus

Nothing in this surface is a drag source or target, offers a context menu, or supports multi-selection. The
`Play ▸` cascade is the only nested menu. (The shell's file-drop cue is a different lane — see `18-shell-frame.md`.)

### 6.10 Localisation drift (code wins)

Hard-coded English strings that **should** have loc keys and do not — port them verbatim, and note the gap:
`"Digital signature"`, `"Close"`, `"Publisher"`, `"Issuer"`, `"Trust"`, `"Reason"`, `"Valid from"`, `"Valid to"`,
`"Thumbprint"`, `"Pinned fingerprint"`, `"File"`, `"Signature"` (button), the whole `SignatureSummary`/`TrustLabel`
ladder (`PlaybackRuntimeSetupCard.cs:757,778-855`); `"Settings"` in the palette table
(`WaveeCommands.cs:201`); `"Search commands — type > for commands only"` (`WaveePalette.cs:192`);
`"No matching commands"` (`WaveePalette.cs:148`); `"Enter"` (`WaveePalette.cs:176`); `"Search for “{q}”"`
(`WaveeCommands.cs:127`); `"N more updates in Wavee"` / `"Open Wavee to see them."`
(`ToastEscalator.cs:192-194`, singular + plural + body); `"New release — "` / `"New episode — "`
(`ToastEscalator.cs:209`); `"Album"` / `"Artist"` / `"Playlist"` in `Menus.cs:605-611` (`KindLabel`, with `""` as the
default arm), plus the two menu-header subtitle fallbacks `"Podcast"` (`Menus.cs:620`) and `"Song"`
(`Menus.cs:664`); `"Spotify {version} · {arch}"` and the two-space `"  (Recommended)"` join in the Advanced version
picker (`PlaybackRuntimeSetupCard.cs:893-894` — only the word *Recommended* is localised); the byte readouts
`"{0:0.0} / {0:0.0} MB"` / `"{0:0.0} MB"` and the `"—"` placeholder
(`PlaybackRuntimeSetupCard.cs:626,670-672,736`); `"Yes"` / `"No"` for the pinned-fingerprint row (`:812`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| Palette builtin rows | static table, labels localised at `BuildIndex` | `Shell.Palette.Builtins` — a static array in `Shell.Palette.cs` (CORE) | always ready; no skeleton |
| Palette registry rows | `WaveeExtensionRegistry.Actions` filtered by `AcceptedTargets` | `Modules.Registry.Actions` (`Platform/Modules.cs`) | always ready (installed modules are a boot fact) |
| Palette "Search for X" | the typed query | pure | always |
| Profile chip avatar / name | `PlaybackBridge.User.Value.{DisplayName, AvatarUrl}` | `User.Me` handle + `UserFields.Identity` | **`User.Me.Knows(UserFields.Identity)`** — skeleton chip (a 24 circle + a 76 × 12 shimmer) until then. 0.2.9 shows `"—"`; 0.3 must not. |
| Profile menu tier badge | `User.IsPremium` | `User.Me` `UserFlags.Premium` + `UserFields.Tier` | `Knows(UserFields.Tier)` — hide the whole tier row until known, never render "Spotify Free" speculatively |
| Profile menu email | `User.Email` | `User.Me.Email` (`UserFields.Contact`) | `Knows(UserFields.Contact)`; absent ⇒ omit the line (0.2.9 already does) |
| Bell unread count | `NotificationCenterBridge.UnreadCount` | see **DATA GAPS** | — |
| Notification feed | `Items` = merge of 4 snapshots | see **DATA GAPS** | the panel must render its **own** loading state (`notifications.loading`) from the feed state, not a global skeleton — a feed that has not answered is a distinct thing from an empty one |
| Social / release row artwork | `s.ImageUrl` / `r.ImageUrl` (raw URLs on the notification record) | keep as a `StringId` on the notification row; **do not** route through an `Album`/`Show` handle — the feed carries its own art and the entity may not exist in the scope | image ready ⇒ paint; else `Surfaces.Artwork`'s hash-seeded placeholder (already the behaviour) |
| Release row type pill | `r.Kind` + `r.AlbumType` | notification row column | always (the fallback label is `ALBUM`) |
| Activity rows | `ActivityLog.Snapshot` → `ActivityEntry` | see **DATA GAPS** | — |
| Activity "Open" jump | `RichText.RouteForUri(e.TargetUri)` | `EntityUri.Parse` + `Shell.RouteFor(kind)` (`Shell/Shell.cs` CORE) | pure |
| Pending-sync count | `LibraryBridge.PendingEditsTotal` | count of edges carrying the pending bit: `Edges.PlaylistTracks` payload `Flags & 1|2` + `Edges.Liked` `LibraryEdge.Flags` (plan §4.3, §4.14) | derived on the **model** at commit time and published as one `Signal<int>` — the UI must not scan edges per render |
| Update row state / sentence | `AppUpdateSnapshot` + `AboutUpdatePanel.StateSentence` | `Platform.Update.Snapshot` signal + `ReleaseNotes.StateSentence` (CORE) | `State != None` |
| Runtime banner gate | `bridge.RuntimeStatus` + `PlaybackRuntimeSetupDismissed` + `SetupSession.Covering` + `SetupGating.IsPending` | `Playback.RuntimeStatus : Signal<PlaybackRuntimeStatus>` + `Platform.Settings` + `Setup.Covering` | all four must be readable synchronously; the banner has no loading state and must never flash |
| Which rows are visible at all | `NotificationPrefs.Level(settings, TopicOf(n)) != Off`, applied in `Rebuild` after the merge | `Platform.Settings` read at rebuild time, published inside the same `Items`/`UnreadCount` pair | synchronous settings read; the panel must never re-check a dial per row |
| The bell count once anything is silenced | `ApplyTopicVisibility`'s recount — **which does not reproduce the merge's Activity exclusion** (see the note below) | one counting rule, written once | — |
| Setup dialog catalog | `IPlayPlayProvisioner.FetchCatalogAsync` | unchanged — `Playback.Audio.cs` / `Spotify.Audio.cs` calls the same `Wavee.PlayPlay` entry points from their new home | `CatalogSig` |

**Derived facts live on the model.** `UnreadCount`, `PendingEditsTotal`, `SocialState`/`WhatsNewState`, the
per-notification `IsUnread`, and `LastEscalatedCount` are all computed at **commit/rebuild** time
(`NotificationCenterBridge.Rebuild`, `NotificationMerge.Build`) and published as signals. No overlay in this chapter
probes a store, walks a list to count, or asks "is this loaded yet?" — and none may in 0.3.

**Pages demand their whole model.** The notification panel already does: `OnPanelOpened` refetches both remote
feeds once and rebuilds; the scroller virtualises nothing and manages no visible window (the feed is ≤ ~290 rows by
construction, `NotificationCenterBridge.cs:20`).

### DATA GAPS

| what this surface shows | 0.2.9 source | plan §4 has | proposed 0.3 home |
|---|---|---|---|
| **Display name, avatar URL, email, product tier** | `PlaybackBridge.User` ← login5 / `spclient` profile | **nothing** — §4.14 `User.cs` carries *only* library edges (`Liked`, `Rootlist`, `SavedAlbums`, `FollowedArtists`, `SavedShows`, `Pins`) | `UserTable`: `Column<StringId> DisplayName, Image, Email`, `Column<uint> Flags` (Premium / Verified), `Column<byte> IdentityAuthority`; `[Flags] UserFields { DisplayName, Image, Email, Tier, Identity = … }` |
| **The whole notification feed** — 4 categories, unread math, last-seen watermarks, the per-item read set | `Wavee.Core/Notifications/*` (`NotificationMerge`, `NotificationReadIds`, `NotificationModels`, `ActivityLog`, `SpotifyNotifications`, `WhatsNew`, `AppUpdate`) | **nothing** — no folder, no file, no table in §2 | **`Platform/Notify.cs` (CORE)** — one stack, owner I, Wave 4 (arbitration 2026-09-12): the row model (`Title`, `Body`, `Image`, `ActionUri`, `Kind`, `Category`, `TimestampMs`, an unread bit), `NotificationMerge`/`NotificationReadIds`/`NotificationPolicy`/`NotificationPrefs` ported verbatim as the CORE fold, and the one counting rule. **Not** a synthetic-subject edge and **not** an `Entities/Notification.*` pair: the feed is not an entity graph read — its rows carry their own art and action uri, they are never handles, and §7 above already forbids routing them through an `Album`/`Show`. The four feeds stay **decode** sources (`Spotify.Decode.cs` for gander + what's-new, Wave 2 owner E); the panel and its rows render from the fold in `Shell/Shell.UI.cs`. **This is the largest single omission in the plan for this surface.** |
| **The local activity / undo journal** | `ActivityLog` + `InMemoryActivityStore` + `ActivityUndoExecutor` | §4.14 has `Store.Journal(Intent.Like, uri)` for the optimistic write, but nothing reads it back as a user-visible trail, and there is no `IsUndoable` / `Status` / `Payload` | `Store.cs`: an `activity(id, kind, target_uri, target_name, ts, status, read, payload_json)` table + `Entities.Activity` reader; the undo executor is SHELL (`Playback`/`Spotify.Api`) |
| **App-update snapshot / failure kinds / release codename** | `Wavee.Core/Notifications/AppUpdate.cs` + `AppUpdateVersion` | §2 lists `Screens/ReleaseNotes.*` (which is the notes parser) but no update-service state | `Platform/Notify.cs` (CORE: `AppUpdateToasts.Plan` + the `AppUpdateSnapshot` / `AppUpdateFailureKind` / `AppUpdateVersion` records it folds — A8, they travel with the plan table and are worthless apart from it) + `Platform/Platform.Host.cs` (SHELL: the deployment API, which is not a notification) |
| **Notification dials** (8 topics × 3 levels + quiet hours + sound + Windows) | `App/NotificationPrefs.cs` + `Wavee.Core/Notifications/NotificationPolicy.cs` | nothing | `Platform/Notify.cs` CORE (`NotifyTopic`, `NotifyLevel`, `QuietHours`, `NotificationPolicy`, `NotificationPrefs`) reading the typed keys from `Platform.Settings`; Settings › Notifications (ch. 27) only calls it |
| **OS toast escalation watermark** (`NotifyLastToastedMs`) and the per-process `s_lastUpdateRaised` / `s_lastProgressPushed` latches | `App/ToastEscalator.cs` | nothing | `Platform/Notify.Host.cs` SHELL (the `ToastNotifier` calls, the watermark write, the two process-lifetime latches); the *decision* (`MaxPerRebuild`, the sentinel-timestamp fold, `UpdateChanged`, `ProgressStepPercent`) is pure and is `Notify.EscalationPlan` in `Platform/Notify.cs` CORE — A11, the two halves of one class |
| **Tips-seen set** (`WaveeSettings.TipsSeen`, newline-joined) | `App/WaveeTipsCore.cs` | nothing | `Shell/Shell.cs` CORE (`TipsCore`) + `Shell/Shell.Host.cs` SHELL (the presenter) — a 100-line codec and a 188-line presenter, no data model needed. **Decided** (A10): ch. 28 consults the tip and drops its claim on the files |
| **PlayPlay runtime status + catalog** (`PlaybackRuntimeStatus`, `PlayPlayRuntimeCatalogEntry`, `DigitalSignatureInfo`) | `Wavee.Backend.Audio` / `Wavee.SpotifyLive.Audio.Runtime` (private junction) | §7 says only "`Spotify.Audio.cs` calls the same `Wavee.PlayPlay` entry points from their new home" | `Playback/Playback.Audio.cs` SHELL holds `Signal<PlaybackRuntimeStatus>`; the **phase machine** (`RuntimeSetupModel`) belongs in `Screens/Setup.cs` CORE + `Setup.Host.cs` SHELL |
| **Module manifests** (`menu.label`, `menu.placeholder`, `ModuleCapabilities.Match`, `DisplayName`) | `Wavee.Sdk` `ModuleManifest` + `ModuleHost.Installed` | §2 keeps `Wavee.Sdk` unchanged and adds `Platform/Modules.cs` | fine as-is; `PlayLinkActions` moves into `Modules.cs` CORE |

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `WaveeCommands` | `Features/Shell/WaveeCommands.cs` | the builtin command table (19 entries, declaration order), which registry actions earn a row (`PaletteTargetOf`), the scoring ladder (`ScoreOf` 0/1/2/−1, `Subsequence`), the bounded scored insert (`InsertScored`, `MaxResults 8`), the `>` prefix and the catalog-search row, and the invoke dispatch | **none today** (grep found no `WaveeCommandsTests`) — add one in 0.3: a table-shape test + a scoring/ordering table + a "`>` suppresses the catalog row" case | **CORE** section of `Shell/Shell.Palette.cs` |
| `WaveeTipsCore` | `App/WaveeTipsCore.cs` | the newline-joined seen-set codec (`Contains`/`Add`/`Parse`/`Serialize`, allocation-free scan) and the one gating decision `ShouldShow(seen, id, armedThisSession, anotherTipActive, canPresent)` | `src/apps/Wavee.Tests/WaveeTipsCoreTests.cs` | **CORE** section of `Shell/Shell.cs` |
| `AppUpdateToasts` | `App/AppUpdateToasts.cs` | `(previous, next) → ToastPlan?` — the "state moved" rule, quiet-failure suppression, per-state severity/stickiness/action lists, `Label`, `FailureText` (7 kinds + `0xHRESULT` fallback), `ReleaseName` (codename → semver → quad, never empty) | `src/apps/Wavee.Tests/AppUpdateToastsTests.cs` | **CORE** section of `Platform/Notify.cs` (A8) — one home, not this file and `Screens/ReleaseNotes.cs` both |
| `PlayLinkActions` | `Actions/PlayLinkActions.cs` | `Normalize`, `CanSubmit`, `LooksLikeUrl`, `PrefillFrom`, `DeclaresMatch`, `MenuLabel`, `PlaceholderFor`, `MatchStatus` (" · " joiner, drop-empty segments), `FailureToastKey`, `IsNotOwned`, `IsCancelled`, `ErrorText`, `FormFor` | `src/apps/Wavee.Tests/Actions/PlayLinkActionsTests.cs` | **CORE** section of `Platform/Modules.cs` |
| `NotificationMerge` | `Wavee.Core/Notifications/NotificationMerge.cs` | the four-feed fold, newest-first sort, `long.MaxValue` update pin, watermark + read-id unread math, Activity excluded from the badge count | `src/apps/Wavee.Tests/NotificationAggregationTests.cs` | **CORE** section of `Platform/Notify.cs` (see §7 DATA GAPS) |
| `NotificationReadIds` | `Wavee.Core/Notifications/NotificationReadIds.cs` | the per-item read-id set codec | covered by `NotificationAggregationTests` / `HomeTimelineMergeTests` | same file |
| `NotificationPolicy` + `NotifyLevel` / `NotifyTopic` / `QuietHours` | `Wavee.Core/Notifications/NotificationPolicy.cs` | the Off/InApp/Windows ladder, per-topic clamps and defaults, `RaisesToastNow`, quiet-hours `Contains` (wraps midnight) and `NextAudible` (SHIFT, never drop) | `src/apps/Wavee.Tests/NotificationPolicyTests.cs` | **CORE** section of `Platform/Notify.cs` |
| `NotificationPrefs` | `App/NotificationPrefs.cs` | settings key ↔ topic mapping, `AllTopics` (declaration order **is** the settings-page order), `TopicOf(notification)`, `Level`/`SetLevel`/`Policy` | — (indirect, via `NotificationPolicyTests` + `SimulatedNotificationsTests`) | **CORE** section of `Platform/Notify.cs` beside the ladder it clamps against (A9). Its one engine dependency, the `Epoch` `Signal<int>`, is the open question ch. 14 §9 records: either the signal moves to `Notify.Host.cs` or `Notify.cs` is accepted as engine-referencing |
| `ToastEscalator`'s decision half | `App/ToastEscalator.cs:30,95,112` | `MaxPerRebuild = 3`, the `long.MaxValue` → "now" sentinel fold (keyed on the *sentinel*, not the type), `UpdateChanged` identity `state:targetQuad` with progress deliberately excluded, `ProgressStepPercent = 5` | — (add one: the sentinel fold is the exact bug that produced a banner storm) | **CORE** in `Platform/Notify.cs` as `Notify.EscalationPlan`; the `ToastNotifier` calls stay SHELL in `Platform/Notify.Host.cs` (A11) |
| `ToastCoalescing` | engine `FluentGpu.Controls/ToastCoalescing.cs` (source-included into `Wavee.Tests`) | `IsDuplicate(keyA, msgA, keyB, msgB)` — the message IS the key when there is no key | `src/apps/Wavee.Tests/ToastCoalescingTests.cs` | stays in the engine; keep the app-side test |
| `AboutUpdatePanel.StateSentence` | `Features/Shell/SettingsPage.About.cs:665-679` | the ONE sentence under the update row, the Settings › About description and (via `FailureText` → `AppUpdateToasts.FailureText`) the failure copy — an 8-arm switch over `AppUpdateState`; **four** arms (`Available`, `Downloading`, `Completed`, and `Failed` via `FailureText`) name the release through `AppUpdateToasts.ReleaseName` so they cannot interpolate a codename twice or print a double space, while `Snoozed` deliberately does not — it prints `TargetSemVer ?? TargetQuad ?? ""` (`SettingsPage.About.cs:673`), which is the one arm that CAN render an empty name. Port the asymmetry rather than "harmonising" it | **none today** — add one alongside `AppUpdateToastsTests`; the two double-space bugs the comment records are exactly what a table test pins | **CORE** beside `AppUpdateToasts` in `Platform/Notify.cs` (the plan calls it `ReleaseNotes.StateSentence`; the sentence and the toast share `FailureText` and `ReleaseName`, so they cannot live in different files) |
| `AppUpdateVersion.ReleaseTagVersion` | `Wavee.Core/Notifications/AppUpdate.cs` | quad → release-tag semver; the panel's `What's new` argument when `TargetSemVer` is unknown (`NotificationPanel.cs:233`) and the OS toast's `&arg=` (`ToastEscalator.cs:248-253`) | covered indirectly by `AppUpdateToastsTests` | **CORE** in `Platform/Notify.cs` |
| `SetupRuntimePresentation` | `App/SetupRuntimePresentation.cs` | `ProgressFraction(received, total)` (clamped, 0 on total ≤ 0) and `ShortHash` (first 4 + `…` + last 4, untouched at ≤ 8) | `src/apps/Wavee.Tests/SetupRuntimePresentationTests.cs:15,22` | **CORE** section of `Screens/Setup.cs` |
| `PlaybackRuntimeSetupModel.Phase` + `ShowsReadyToast` | `Features/Shell/PlaybackRuntimeSetupModel.Phase.cs` | the 8-value phase enum (engine-free, split out precisely so tests can enumerate it) and "skip the Ready toast when wizard-hosted" | `src/apps/Wavee.Tests/SetupRuntimePresentationTests.cs:32` | **CORE** section of `Screens/Setup.cs` |
| `SetupGating` | `App/SetupGating.cs:39,71,200` | `IsPending`, `MarkDeferred`, `SuppressesRuntimePrompts(pending, sessionOpen)` — the rule that silences the banner while the wizard is up | `src/apps/Wavee.Tests/SetupCommandsTests.cs` (+ see `28-setup-whatsnew-feedback.md`) | **CORE** section of `Screens/Setup.cs` |
| `MenuLabel.Clip` | `Wavee.Core/Sidebar/MenuLabel.cs` | `NameChars = 28`, trailing-space trim before the ellipsis — the rule that stops an interpolated name widening a whole flyout | (see `25-sidebar.md`) | **CORE** in `Shell/Sidebar.cs` |
| `MergedChromeLayout` / `ShellResponsiveLayout` | `Features/Shell/MergedChromeLayout.cs`, `ShellResponsiveLayout.cs` | `ShowName` / `ActionsInRow` / `ActionsInMenu`, `ChromeNameEnterW 1360`, `ChromeActionsEnterW 1200`, `ChromePromotionHysteresisW 40`, `ChromeProfileChipW 32`, `ChromeProfileNameW 90` | `MergedChromeLayoutTests.cs`, `ShellResponsiveLayoutTests.cs`, `ShellMergedRungTests.cs` | **CORE** in `Shell/Shell.cs` — owned by `18-shell-frame.md`; this chapter only **consumes** `ShowName` / `ActionsInMenu` |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The palette's two-tier keyboard model.** `sel` is a signal; `selClamped` is a per-render clamp
   (`WaveePalette.cs:101`). Down/Up write the *unclamped* signal modulo `count`. Collapsing these into one value
   breaks wrap-around the moment the query shrinks the list under the cursor.
2. **The palette's caller-owned `dest` + reused `catalogScratch`.** `WaveeCommands.Filter` writes into arrays the
   component allocated once in `UseRef` (`WaveePalette.cs:86-97`). A re-author that returns a `List<Entry>` allocates
   on every keystroke. Keep the `Span<int> scores = stackalloc`.
3. **The ladder as a *signal*, not a frozen bool.** `ProfileMenu` takes `IReadSignal<MergedChromeLayout>` and reads
   `.Value` in `Render` (`ProfileMenu.cs:43-44,62`). A `bool showName` ctor arg would freeze at mount — a
   `ComponentEl` never re-runs its factory. This comment is in the source because it was a real bug.
4. **`Peek()` at open time vs `.Value` in render.** `OpenMenu` reads `nc.UnreadCount.Peek()` and
   `_layout.Peek().ActionsInMenu` (`ProfileMenu.cs:151-153`) — the menu body is a one-shot thunk, so subscribing
   there would do nothing but add a phantom dependency. The **chip** subscribes; the **menu body** peeks.
5. **Two independent overlay handles on the chip.** `handle` (the account flyout) and `notifyHandle` (the
   notification panel re-anchored to the chip) — `ProfileMenu.cs:59-60`. One handle would make the two fight.
6. **`NotificationPanelLauncher` is the one open path.** Both the bell and the profile row go through it
   (`NotificationPanel.cs:27-43`) so neither re-derives the placement/chrome or forgets `nc.OnPanelOpened()`. The
   anchor is passed as a **`Func<NodeHandle>`**, not a node, because the button may not have realized on the first
   render.
7. **The reserved 8-DIP unread slot and the reserved 18-DIP status row.** `NotificationPanel.cs:204` and
   `PlayLinkDialog.cs:111`. Both exist so the layout cannot jump. Dropping either is an instant, visible regression.
8. **`failedInput` is a separate signal from `text`.** `PlayLinkDialog.cs:118-120`. The escape hatch must open the
   link that failed, not whatever half-typed text is in the box.
9. **The setup dialog's single command row.** `ContentDialog`'s built-in buttons are cleared and `d.Footer` owns
   everything, so a phase swap never re-opens the dialog and never re-runs the open motion.
10. **`ProgressFraction` and `ShortHash` are pure and tested.** Do not inline `received/(double)total` — the clamp
    and the `total <= 0` case are both pinned.
11. **The 412-DIP bar width is derived, not magic.** If `DialogWidth` ever changes, `RuntimeProgressWidth` must
    follow (`460 − 2 × ContentDialog.Pad`). And `DialogWidth` may only change **to another rung of §3.1** —
    460 / 480 / 548, or 320 by accident. The same derivation governs `SidebarPickers.BodyW` (432) and
    `ReportDialog.ContentWidth` (500); all three are one rule, not three constants.
12. **Both `AlreadyUpToDate` paths.** `StartDownload` and `InstallSelected` each check `Status.PackId == entry.PackId`
    before downloading (`PlaybackRuntimeSetupCard.cs:182-186, 215-219`). Windows refuses to overwrite the runtime DLL
    this very process has loaded — skipping the check produces a self-inflicted, always-reproducible "download
    failed".
13. **The InfoBar orientation rule.** Three of the four banner messages are horizontal and one is vertical, decided
    purely by `message.Length >= 60`. Do not pass `availableWidth` to "improve" it — that changes three of four.
14. **`Toast` coalescing keeps the FIRST message and the NEWEST action.** The first sentence is the specific one (a
    module's own words); the later lane's is the generic fallback.
15. **The tip's double post.** One post lands after the mount's commit; the second after the frame that *painted*
    the host. A single post opens the tip before the anchor's command bar has finished fitting its commands.
16. **The ladder's asymmetry.** `held = candidate && (old || reserved)` is not "a threshold with a 40-DIP dead
    band around it": it promotes 40 DIP late and demotes exactly on the threshold. Re-authoring it as
    `enter/leave` pairs (1400/1360, 1240/1200) is arithmetically equivalent *only* if both numbers move together;
    writing the enter value as the constant and the leave value as `constant − 40` inverts it.
17. **The toast shows one action; the row shows all of them.** `plan.Actions[0]` is not a simplification to undo
    (`NotificationCenterBridge.cs:229`) — the engine `Toast` card has a single action slot by design, and the
    notification centre is where the full set lives.
18. **The topic dials filter on the model, after the merge.** `ApplyTopicVisibility` runs on the rebuilt list and
    recounts unread over what survives (`NotificationCenterBridge.cs:197`). Filtering inside `NotificationMerge`
    would make the fold impure; filtering in the panel would leave the bell lit for rows nobody can see.
    **But the two counts do not agree.** `NotificationMerge` deliberately excludes Activity from the badge —
    `if (x.IsUnread && x.Category != NotificationCategory.Activity)` (`NotificationMerge.cs:36`, and the comment
    above it says why: liking a song must not light the bell). `ApplyTopicVisibility`'s recount is a bare
    `if (n.IsUnread) keptUnread++` (`NotificationCenterBridge.cs:373`), and `ActivityNotification.IsUnread` is
    `!Entry.Read` (`NotificationModels.cs:52`). So the moment ANY topic is dialled Off, every unread activity entry
    starts counting toward the bell badge — the exclusion silently stops applying. 0.3 must write the counting rule
    **once** (a predicate the merge and the visibility filter share) and decide explicitly which behaviour it ships;
    reproducing the divergence by accident is the failure mode this note exists to prevent.
19. **`UpdateProgress` is written even when no toast is planned.** `ConsiderUpdateToast` sets the signal *before*
    the `Plan(...) is not { } plan` early-out and before the dial check (`NotificationCenterBridge.cs:223-227`), so
    a card that appears mid-download starts at the right percentage.

### 9.2 Traps

- **Props freeze at mount.** `PlayLinkDialog` is the *legitimate* case: every prop (`Actions`, `PinnedModuleId`,
  `Placeholder`, `Seed`, `Close`) is an open-time constant, and the comment at `PlayLinkDialog.cs:97-100` says so
  explicitly. `ProfileMenu` is the counter-example (see 9.1 #3). `PlaybackRuntimeChrome` takes `IAppSettings` — a
  reference-stable service — and reads the *epoch signal* for freshness (`PlaybackRuntimeBanner.cs:69`).
- **`IAppSettings` writes are not signals.** Anything that flips `PlaybackRuntimeSetupDismissed` must
  `PlaybackRuntimeBannerState.Bump()` or the banner will not re-evaluate until something else re-renders it. Same
  pattern: `NotificationPrefs.Epoch`, `AppearancePrefs`.
- **`Key` remounts.** `"ntf:" + id` / `"ntf:act:" + id` / `"ntf:actwrap:" + id` / `"nc-pending-sync"` /
  `"toast:" + id` / `"chrome-pin-placeholder"`. Getting a key wrong turns the enter/exit terminals into a wholesale
  list re-animation on every 30-second tick.
- **`ReuseGuard`**: the notification feed re-renders on every push and every `NowTick`. A re-author that builds rows
  through `Embed.Comp(() => new Row { Data = n })` will trip the guard (or, worse, silently freeze the first row's
  data). 0.2.9 builds rows as plain `Element` records from static functions — keep that.
- **Zero-allocation scroll frames vs per-row richness.** 0.2.9 reconciled this by **not virtualising** the
  notification list at all: it is a `ScrollEl` over a materialised `BoxEl[]` of ≤ ~290 rows, built once per feed
  change, and the *palette* is capped at 8 rows written into a reused array. Neither list is a hot scroll surface,
  so richness is affordable. Do not "upgrade" either to `ItemsView.CreateBound` — the binding cost buys nothing here
  and the enter/exit terminals per row are the point.
- **Every anchored handle must null itself on close.** `handle.Value.ClosedAction = () => handle.Value = null` —
  `ProfileMenu.cs:173` and `NotificationPanelLauncher` (`NotificationPanel.cs:40`). The toggle test is
  `handle.Value is { IsOpen: true }`, so a light-dismissed popup that leaves a stale non-null handle behind still
  toggles correctly *today* (IsOpen is false) — but the `Ref` then pins a dead overlay entry for the life of the
  chip. Both call sites clear it; a re-author that keeps one and drops the other will not see a bug until it leaks.
- **`Announcer.IsAvailable` guard.** `WaveePalette.cs:105` — a headless/probe host has no announce hook.
- **`ContentDialog.Footer` vs `PrimaryText`.** `PrimaryText` defaults to `"OK"`; the setup card must clear all three
  or a phantom OK button appears beside the footer.
- **`FirstFocusableIn` and chromeless editors.** The palette's focus lands on the `EditableText`, not a `PartRoot`
  chrome node (see `.claude/skills/wavee/focus-pitfalls.md`). The same applies to the play-link field.

### 9.3 Where the plan is wrong or too thin

1. **§4.14 `User.cs` has no identity columns at all.** It models the library as edges (`Liked`, `Rootlist`, …) and
   nothing else. The profile chip, the account header, the tier badge, the email line and the avatar all need
   `DisplayName` / `Image` / `Email` / `Tier` on `UserTable`. As written, Wave 5 owner O cannot build the account
   header and Wave 4 owner I cannot build the chip.
2. **The notification centre is absent from §2's tree.** Four feed services, a merge, a read-id set, a topic policy,
   an OS escalator, an activity journal and an undo executor — roughly 2,600 lines in 0.2.9 — have no file, no
   folder and no owner. §2 gives `Shell/Shell.UI.cs` 2,800 lines for the *entire* shell UI, which already has to
   hold the toolbar, tabs, drill trail, masthead, player bar and every overlay in this chapter. **Add
   `Platform/Notify.cs` (CORE, ~1,000) + `Platform/Notify.Host.cs` (SHELL, ~715) to §2's `Platform/` row, both owner
   I in Wave 4, and give the feed decode to Wave 2 owner E** (`Spotify.Decode.cs`). Arbitration 2026-09-12: this is
   the SAME pair ch. 14 needs for the OS toasts — one stack, not two — and the earlier proposal here
   (`Entities/Notification.cs` + `Notification.UI.cs`, ≈ 900 + 900) is withdrawn: a notification is not an entity, it
   carries its own art and action uri, and §7 already forbids routing its rows through a handle. `Shell/Shell.UI.cs`
   keeps the bell, the panel and its rows and must be budgeted for them (§9.4).
3. **§2 gives `Shell/Shell.Palette.cs` 2,400 lines** and §5 Wave 4 describes owner I's job as "WaveeCommands +
   `Actions/*` → one table". `Actions/*` in 0.2.9 is ~6,500 lines (14 files). Either the budget or the scope is
   wrong; the *palette* itself is 492 lines (`WaveePalette.cs` + `WaveeCommands.cs`).
4. **§4.12's `Track.Row` sketch has no hover/press/focus states, no `Key`, and no enter/exit terminals** — and it is
   the plan's only worked UI example. Every overlay in this chapter depends on those three being first-class. A
   re-author following §4.12's style alone will produce a static list.
5. **§4.13's page pattern (`UseEffect` → `Entities.Ensure(...)` once at mount) does not fit an overlay.** The
   notification panel's "demand" happens at *open* (`OnPanelOpened`), not at mount, and it re-runs on every open.
   The plan needs an explicit "overlay demand" shape, or Wave 4 will either over-fetch on every render or never
   refresh.
6. **§7's risk table says the PlayPlay junction is "untouched"** — true for the *audio* path, but
   `PlaybackRuntimeSetupCard.cs` (1,030 lines) is a **UI** consumer of `IPlayPlayProvisioner`,
   `PlayPlayRuntimeCatalogEntry`, `PlayPlayRuntimeVerifyResult`, `PlayPlayDownloadProgress`,
   `PlayPlayRuntimePaths` and `DigitalSignatureInfo`, and it is gated by `#if WAVEE_PLAYPLAY_LOCAL`
   (`:383-390`). §5 originally assigned it to nobody. **Settled (A18):** the card body is the named partial
   `Screens/+Setup.UI.Runtime.cs` (1,100), written by owner I in Wave 4 (the Wave-4 shell gate needs a banner with
   something behind its button) and mounted again, unchanged, by owner R's wizard in Wave 6.
7. **No plan section mentions toasts, teaching tips or the OS notification bridge.** They have no file, no owner and
   no budget in the plan as written. The in-app toast host is the engine's; the *app* still owns ≈ 600 lines of
   decision code (`AppUpdateToasts`, `ToastEscalator`, `WaveeTips`, `WaveeTipsCore`). Arbitration 2026-09-12 gives
   each of them exactly one home: the first two in `Platform/Notify.cs` + `Notify.Host.cs` (A8, A11), the last two in
   `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL (A10 — this chapter owns the tip; ch. 28 consults it). §2 of
   the plan still has to gain the rows.

### 9.4 Line budget

| | lines |
|---|--:|
| 0.2.9, this surface (13 owned files + `PlayLinkActions`) | **4,114** |
| plus the shared pure rules it consumes (`NotificationMerge` 40, `NotificationReadIds` ~60, `NotificationPolicy` ~150, `NotificationPrefs` ~100, `SetupRuntimePresentation` 23, `AppUpdateSurface` 33) | ~4,520 |
| plan §2 target for the files that would hold it (`Shell.Palette.cs` 2,400 + a share of `Shell.UI.cs` 2,800) | — (no line is allocated to notifications, toasts or tips at all) |
| **honest 0.3 estimate** | **4,400–4,800** — the composition compresses (one `Overlay` helper, one pill/link/chip vocabulary shared across the panel and the setup card, ≈ 200 lines saved), but §7's data gaps *add* the notification fold and its decode (≈ 400 lines) that 0.2.9 got for free from `Wavee.Core` — in `Platform/Notify.cs`, not a table + edge (§9.3.2) |

Settled split, one file set (plan §2/§9.4, refined past the arbitration note below): `Shell/Shell.Palette.cs` 600
(table + filter + card) · `Shell/Shell.UI.cs` **+300** (**the notification panel and its rows only**, A9) ·
`Shell/+Shell.Overlays.UI.cs` **1,700** (chip, menu, launcher, play-link, banner chrome, the tip host, and the mount
of `+Screens/Setup.UI.Runtime.cs`) · `Shell/Shell.cs` +100 (`TipsCore`, `RouteFor`) ·
`Screens/+Setup.UI.Runtime.cs` 1,100 (the runtime setup card BODY, A18 — written here by owner I in Wave 4, mounted
by owner R's wizard in Wave 6) · `Screens/Setup.cs` 120 (phase enum + presentation, owner R, Wave 6) ·
`Platform/Notify.cs` **~1,000 CORE**, of which **~555 is this chapter's** (`NotificationMerge` 40,
`NotificationReadIds` 90, `NotificationModels` 55, `SpotifyNotifications` 87, `WhatsNew` 80 and the
merge/filter/read-state half of `NotificationCenterBridge`, ≈ 218 — 218 = 377 − the 159 raw lines ch. 14 §0 counts as
its own) and **~445 is ch. 14's** (policy, prefs, `AppUpdateToasts`, `EscalationPlan`) · `Platform/Notify.Host.cs`
**~715, ch. 14's number in full** — this chapter adds nothing to it and must not re-count it.

Counted once, that is 600 + 300 + 1,700 + 100 + 1,100 + 120 + 555 = **4,475** of this chapter's own, inside the
4,400–4,800 band above; ch. 14 carries the other 445 + 715 of the shared pair. There is no
`Entities/Notification.*` row: it was withdrawn by the same arbitration (§9.3.2). All of it is owner I, Wave 4,
except `Screens/Setup.cs`'s phase enum + presentation, which owner R mounts in Wave 6.

### 9.5 Files now in the §2 tree for this surface

`Platform/Notify.cs` + `Platform/Notify.Host.cs` (the one notification stack — the feed fold, the policy, the
escalator and `AppUpdateToasts`; the feed's ROWS are not a separate file, they are `Shell/Shell.UI.cs`), and an
explicit home for `WaveeTips`/`TipsCore` — **decided**: `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL (A10).
Everything else in this chapter that is not the notification panel lives in the named partial
`Shell/+Shell.Overlays.UI.cs`, not `Shell/Shell.UI.cs`. The runtime provisioning card's body is the named partial
`Screens/+Setup.UI.Runtime.cs` (A18 — **CONFIRMED by Christos, plan §9.6 Q9, 2026-09-12**, as written: owner I
writes it in Wave 4, owner R's wizard mounts it in Wave 6); `Screens/Setup.Host.cs` still has no number in §2 — a
separate, still-open gap this chapter's own §9.6 raised, not one of the nine questions §9.6 Q1-Q9 answers.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Unless a row says otherwise, run both builds at the same window size on the same monitor and compare a static
capture. "Frame recording" means capture at ≥ 60 fps and step frames.

**Command palette**

1. `--fake`, any route, 1440 × 900, **Ctrl+K**: the card's top edge is 64 DIP below the top of the content ZStack and
   it is horizontally centred; width 560. *Static capture, measure both edges.*
2. Same: the card is acrylic with a 1 px stroke and an `Elevation.Flyout` shadow, corner radius 8, **no inner
   presenter padding** (the field's top edge is 8 DIP below the card's top edge, not 10).
3. Same: exactly 8 result rows, 32 DIP tall, 1 DIP apart, and the list does **not** scroll.
4. Same: the first row is selected on open — accent glyph, `FillSubtleSecondary` fill, and the word `Enter` at the
   right edge. *Static capture.*
5. Press **↓** seven times then once more: selection wraps to row 1. Press **↑** from row 1: it wraps to row 8.
6. Type `pl`: the ranking is prefix → contains → subsequence, then `Search for “pl”` **last**. *Static capture.*
7. Type `>pl`: the `Search for` row is gone and only commands remain.
8. Type `>zzzz`: a single 13 px `TextTertiary` row reading `No matching commands`, padded `(16,12,16,12)`.
9. **Ctrl+K** while open: the palette closes (it is a toggle, not an open).
10. **Escape** while the palette is open on a page with immersive lyrics up: the palette closes and the lyrics stage
    stays (the shell's Escape guard).
11. *Frame recording of the open:* the card starts 50 DIP **above** its resting place and settles **downward** over
    367 ms, while opacity holds at 0 for 83 ms and then fades in over 83 ms. (A card that rises from below is the
    sign flipped — `PopupEntranceSlide` gives a downward-opening popup −50, not +50.)
11b. Type a query with leading/trailing whitespace (`"  pl "` pasted): it ranks exactly as `pl` does. Type
    `">  pl"`: the same hits, no catalog row. *Static captures.*
11c. Check the eight builtin glyphs against the table under W1 (Home E80F · Search E721 · MusicNote EC4F ·
    Headphones E7F6 · Settings E713 · Play E768 · Next E893 · Previous E892) — a glyph swap is the easiest silent
    regression in this surface. Then invoke `Your Library`: it lands on the **`liked`** route, not a `library` one.
11d. Set app zoom to 125 % (`Ctrl +` twice from the palette itself) at a 1440-px window: the profile name and the
    bell/friends buttons are ALREADY folded (1440 px ≈ 1152 DIP), with no resize. Reset with `Ctrl 0` and they
    return. *Two static captures; the ladder is measured in DIP, never in window pixels.*

**Profile chip & menu**

12. Resize DOWN from 1440 to 1340 to 1180: the name caption disappears **below 1360**, and bell + friends leave the
    trailing island **below 1200**. Now resize back UP: the name does **not** return at 1360 — it returns at
    **1400**, and bell + friends at **1240** (the 40-DIP promotion reserve). *Two static captures per stage, in both
    directions; the asymmetry is the test.*
12b. Squeeze the window until the tab viewport is at its floor: the trailing island (profile chip included) is shed
    entirely — `ShowTrailing` false — before the tabs are allowed to disappear. Verify the shed ORDER is "+" slot,
    then trailing, then Back.
13. At 1440 with a long display name: the caption ellipsises at 76 DIP and the chip never exceeds 122 DIP wide.
14. Click the chip: the flyout is 304 DIP wide, anchored to the chip's right edge, and its top edge is 8 DIP above
    the account header's padding box (6 body + 2 presenter).
15. *Frame recording:* the flyout's border ring stretches in over 250 ms (`ScaleY` 0.5 → 1, pivot on the bottom edge
    for a downward menu) — **not** a 367 ms slide + late fade.
16. Account header: 40 DIP avatar, name at 14/600 on one ellipsised line, then a tier row (10 DIP star + badge at 12),
    then the email at 12 `TextTertiary`. Gap 12 horizontally, 2 vertically.
17. With a Premium account in dark theme, the star and badge are `#E6C26C`; in light theme `#8A6312`.
18. With a Free account, the star slot is an invisible 10 DIP spacer — the badge baseline is identical to the
    premium case. *Overlay the two captures.*
19. Below 1200: rows `Notifications (n)` and `Friends` appear between two separators; above 1200 they are absent and
    the separator count drops from 3 to 2.
20. With unread > 0 the notifications row reads `Notifications (3)`; with 0 it reads `Notifications`.
21. In dark theme the theme row reads `Light theme` with the Sun glyph; in light, `Dark theme` with the Moon.
22. Hover `Play ▸` and wait: the cascade opens after ~400 ms, overlapping its owner by 4 DIP.
23. `Play ▸` contains `File…`, `Link…`, then a separator and one `Globe` row per installed match-capable module.
24. Click `Log out`: the menu closes first, then a 380 DIP modal card appears over a `#0000004D` scrim with
    `Cancel` / `Log out` both 96 DIP wide, right-aligned.

**Play-link dialog**

25. Copy a YouTube URL, then `Play ▸ Link…`: the field is pre-filled. Copy a plain sentence and repeat: the field is
    empty.
26. The card is 420 DIP wide, the field is 372, and the status row reserves 18 DIP even when empty — the button row
    does not move when a status appears. *Two static captures, empty vs `Looking up…`.*
27. With the field empty, `Play` is present but disabled (not hidden).
28. Press **Enter** in the field: it submits (no visible focus move to a button first).
29. Force a module failure: an Error toast appears with `Try again`, and the card gains an `Open in browser` button
    to the LEFT of `Cancel`. Edit the field afterwards — the browser button still opens the **failed** link.
30. Trigger two failures in a row: there is still exactly **one** toast card, with a restarted countdown.

**Notification panel**

31. Click the bell at 1440: a 380 DIP panel anchored to the bell's right edge, header + 5 pills outside the
    scroller, body capped at 460 and the whole panel at 520. *Static capture; measure.*
32. Open the same panel from the profile menu row at 1100: identical panel, identical placement relative to its
    anchor.
33. Filter pills: the selected one is `AccentDefault` with on-accent text; the rest are `FillSubtleSecondary` with
    `TextSecondary`. All are 26 DIP tall with a 13 DIP radius.
34. `Clear` appears in the header **only** while the Activity filter is selected.
35. Every row is 56 DIP minimum with an 8 DIP trailing slot; read and unread rows have identical text positions.
    *Overlay two captures.*
36. Simulate an update (Settings › Notifications › Send event): the update row shows a 36 DIP circular glyph chip,
    a 13.5/700 title, a ≤ 3-line 12 px body, and three pill buttons at 28 DIP tall / 14 DIP radius.
37. Drive the update to `Downloading`: the buttons are replaced by a 200 DIP determinate bar and a **monospaced**
    percentage at 11 px.
38. Click an activity row: it expands into a detail block inset 46 DIP from the left, with at most 8 bullet lines.
    Click another row: the first collapses (one at a time).
38b. *Frame recording of the same toggle:* the expanded row swaps the keyed child at that slot
    (`ntf:act:<id>` → `ntf:actwrap:<id>`), so record whether the card plays its exit/enter terminals or holds still,
    and write the answer into §5. Whatever 0.2.9 does is the contract.
38c. Expand an activity entry whose `ActivityKind` the summary switch does not know (drive one through
    `ActivityLog.Record`): the card still draws — glyph chip, timestamp, unread dot — with an **empty** summary line.
39. An undone activity row is struck through and `TextTertiary`, with an `Undone` eyebrow chip.
39b. The detail block's `Tracks` eyebrow is **sentence case**; the release type pills beside it are CAPS. Both are
    the same `WaveeType.Eyebrow`, so this is authored copy, not a text transform. *Static capture, read the letters.*
39c. Expand a visibility change with no payload: it reads "Made X **private**" (the `?? false` default). Expand a
    rename: the summary names the NEW name, the detail line shows `{old}  →  {new}` with two spaces each side.
40. *Frame recording of `Mark all read`:* rows fade out upward over 200 ms and the remaining rows spring into place;
    nothing jump-cuts.
41. Filter to Spotify with the network off: the empty state reads `Sign in to see notifications from Spotify.`, not
    `No Spotify notifications`.
42. Make a playlist edit and immediately open the panel: the pending-sync line appears between the pills and the
    feed, 12 px, with a 12 DIP `Refresh` glyph.
43. Leave the panel open for 60 s with a row timestamped 1 minute ago: its label advances from `now` to `1m ago`
    without the panel re-laying out.

**Toasts**

44. Add a track to a playlist: the toast docks bottom-right, 24 DIP from the right edge and **96 DIP from the
    bottom** (clearing the 72 DIP player bar). *Static capture; measure the gap to the player bar.*
45. Raise 4 toasts quickly: 3 are visible, stacked 8 DIP apart, newest nearest the bottom.
46. Hover the strip for 10 s: none of the three dismisses. Move away: they resume and dismiss.
47. *Frame recording of a toast entering:* it translates 48 DIP from the right over 300 ms while fading in.
48. Drive an update to `Downloading`: the toast becomes a custom card with a 240 DIP bar and does **not**
    auto-dismiss; its padding is visibly larger than a standard toast's (16 + 16/14).
48b. Drive the same update to `Installing`: the toast is a **message-only vertical** InfoBar (no title line at all,
    "Restarting to finish updating…" stacked over nothing), not a title-over-message card — it has one content item.
    Compare with `Completed`, which is a title + one action laid out horizontally. *Two static captures.*
49. Error toast vs Success toast: the plate is opaque in both (nothing behind shows through the text).

**Teaching tips**

50. Fresh profile (`TipsSeen` empty), open a playlist with tuning available: the tip rises **after** the page has
    painted, below the `Tune ▾` button, with a tail pointing at it. *Frame recording — the page must be fully
    painted first.*
51. The tip does not dim the page: click a track behind it — the track plays and the tip stays.
52. Dismiss with ✕, relaunch, revisit: the tip does not return. Instead, navigate away without dismissing, relaunch,
    revisit: it returns exactly once.
53. Navigate away with the tip up: it disappears immediately (no lingering popup over the new page).

**Runtime banner & setup**

54. With the runtime missing, `--fake` off, at 1440: the banner floats centred, its top edge 56 DIP from the window
    top, capped at 560 DIP wide, over an opaque plate with an `Elevation.Flyout` shadow.
55. The banner does **not** shift the page: compare the content region's first row Y with and without the banner.
56. With `NoSupportedPack`, the InfoBar is vertical (title over message over button); with the default message it is
    horizontal (title, message, button inline). *Two static captures.*
57. Dismiss the banner with ✕: it disappears on the next frame, and stays gone after a relaunch.
58. Open the setup dialog: 460 DIP wide, content region on `FillLayerAlt`, a 1 px divider, a `FillSolidBase` command
    row, all padded 24.
58b. **The width ladder (§3.1), measured end to end.** Capture the four `ContentDialog` rungs side by side and
    measure the card edges: setup **460**, sidebar item picker **480** (26), report dialog **548** (28), lyrics
    inspector **548** (22). Then the two off-ladder Modal cards in this chapter: logout confirm **380**, play-link
    **420** — and confirm neither has a title band, a separator or a command-row fill. No dialog anywhere in the app
    measures a fifth width. *Static captures; measure, do not eyeball.*
58c. In each rung, measure the widest content element: 412 (setup bar), 432 (picker body), 500 (report form).
    Each is exactly `width − 48`. *The derivation, not the number, is the test.*
59. `Offer` footer: `Advanced options` flush-left (its text left edge aligns with the body copy's), then
    `Not now` and an accent `Download & set up`, both 96 × 32.
60. Start the download: the bar is exactly 412 DIP and spans the full content width; the byte readout is
    `12.3 / 84.0 MB` shaped.
61. While downloading, press **Escape**: the dialog does not close.
62. `Verifying`: the bar is indeterminate, `Cancel` is present but disabled, and a three-row fact box (Version /
    Architecture / SHA-256 with a `abcd…wxyz` hash) sits below it, inset 8 with a 4 DIP radius.
63. `Ready`: the success InfoBadge + `Local playback is ready`, a four-row fact box with a 92 DIP label lane, a
    `[Signature]` button in the Signature row, and `Replace` / `Remove` hyperlinks.
64. Click `[Signature]`: a nested 548 DIP dialog with nine label/value rows at 11/12 px and a single `Close` button.
65. `Failed`: two left-hand links (`Advanced options`, `View diagnostics`) where only the first is pulled flush-left;
    right side `Not now` + accent `Try again`.
66. `Advanced` with the catalog loaded: a radio list whose first entry ends `  (Recommended)`, a 1 px divider, then
    two setting rows with a trailing chevron and `Interaction.Subtle` hover.
67. `Advanced` while the catalog is fetching: a 32 DIP indeterminate ring beside the label in a 48 DIP-tall row.
68. Phase change (Offer → FetchingCatalog): *frame recording* — the dialog card does **not** re-play its open
    scale/fade; only the body and footer swap.
69. Hover every interactive element in the panel and the setup dialog in both themes: hover fills are white-alpha in
    dark and black-alpha in light. Then PRESS each: every `Interaction.Subtle` surface (chip, menu row, setting row,
    InfoBar ✕) gets **dimmer** than its hover in BOTH themes, and a notification row gets dimmer in dark but darker
    in light. *Hover + pressed captures, both themes; the polarity is the test.*
70. Tab through the setup dialog: focus stays inside the card, and the accent footer button is reached first.
70b. **The three Cancels.** Press `Cancel` while `FetchingCatalog`/`Downloading` from the Offer path → the dialog
    returns to **Offer**. Enter Advanced, press `Install`, then `Cancel` → it returns to **Advanced**. Reach
    `Untrusted` and press `Cancel` → **Offer**. Same word, three destinations. *Three captures.*
70c. From `Ready`, press `Check for update`: the dialog goes through `FetchingCatalog` (fact box gone, single
    `Cancel` in the footer) and comes back to `Ready` — now WITH the "You're on the latest supported version." line.
    It is a round trip, not an in-place check. *Frame recording.*
70d. In Advanced, click `Choose a Spotify.dll…` and pick a folder: the `Verifying` screen appears (disabled Cancel,
    indeterminate bar) for the duration of the synchronous register, then Ready / Untrusted / Failed. On a
    public-only checkout (`WAVEE_PLAYPLAY_LOCAL` undefined) `Use installed Spotify` lands on `Failed` with
    `playback.runtime.notActive` — the row is present and answers, it does not go dead.

**States the 0.2.9 build has to be driven into (Settings › Notifications ▸ Send event, and the dials)**

71. Dial `App updates` to Off and simulate an update: **no** in-app toast, **no** panel row, and the bell badge does
    not move. Dial it back to In-app: the row returns on the next rebuild with the right unread state.
71b. **The bell count once anything is silenced.** With every dial at its default, like a song and confirm the bell
    badge does NOT move (Activity is excluded from the count). Now dial a DIFFERENT topic (say `New releases`) to
    Off and like another song: `ApplyTopicVisibility` recounts without the Activity exclusion, so the badge
    increments. Decide which behaviour 0.3 ships and pin it with a test — do not port the divergence by accident.
72. Simulate an update, then drive it Available → Downloading → Installing → Completed without closing the strip:
    there is exactly ONE toast card throughout; it becomes the padded custom card only during Downloading, and
    reverts to a standard sticky InfoBar for Installing. *Frame recording.*
73. Simulate `Failed` with a `Metered` failure vs a `Network` one: the panel row offers `Retry` + `Dismiss` in both,
    the toast offers `Retry` in both (the toast shows only the first planned action).
74. Simulate a `Snoozed` update: the row shows `Update now` + `What's new` and **no** `Later`.
75. Raise a toast whose message is ~90 characters with an action: the card lays out vertically (title over message
    over button) at 380 DIP. The runtime banner with the SAME 90-character message would stay horizontal — the
    banner passes no `availableWidth`.
76. Open the setup dialog from Settings on a machine that already has the runtime: it opens at `Ready`, not `Offer`,
    and the "You're on the latest supported version." line is **absent** until `Check for update` runs.
77. Expand an activity entry that renamed a playlist: the detail block shows the target line, then the
    `{old} → {new}` line, then (if any) `TRACKS`. Expand one with 20 tracks: exactly 8 bullets, no "+12 more".
78. Undo an entry, then look at it again: struck-through summary, `TextTertiary` text AND a `TextTertiary` glyph
    chip, an `Undone` eyebrow chip, and **no** `Undo` pill.
79. First launch on a fresh profile with several unread feed rows: **zero** Windows banners (the watermark is 0 and
    the first pass only records). Second launch with one new row: exactly one banner.
80. With `Sound` off, every OS banner — including the "N more updates in Wavee" summary — raises silently.
81. Open every dialog in the app and measure the card: exactly **four** widths occur (320 · 460 · 480 · 548) plus the
    two off-ladder `PopupChrome.Modal` cards (380, 420) and the two non-`ContentDialog` plates (720, 762 × 490).
    A fifth `ContentDialog` width is a regression (§3.1).
82. Open `ContainerActions.RenameDialog`, `TrackActions` "View credits", `Menus.OpenPicker` and any
    `SettingsShared.Confirm`: all four are **320**, and the three with one visible button keep the empty left star
    column with the button pushed right (`ContentDialog.cs:286-290`). Then Settings › Storage › change cache
    location: the three-button pair is **480** without either dialog naming a width.
83. Every dialog, on every rung: padding 24, content gap 12, button gap 8, buttons 130 × 32, title 20 SemiBold,
    body 14, and a body taller than the card scrolls at 556 with no colour edge cue (§3.1 shared metrics).
84. Walk §3.2 row by row against the running app: every one of the 106 sites raises a card with the listed sentence,
    severity, duration and action button. The three non-5000 ms toasts (sticky crash, 6000 simulator, 8000 paste)
    and the three dedupe keys (`crash.pendingReport`, `update`, `wavee.play.failed`) are the only departures.
85. Raise 57 (paste-a-link failure) and 71 (a local playback failure) for the SAME track: **one** card, carrying the
    first (more specific) sentence and the newer retry action (§0 #10, §3.2 rule 5).
86. Press the action button on any toast that has one (§3.2 rule 3): the action runs **and** the card dismisses.
    Press Undo on 21/22/23/28/32/36/92 and the change is actually reversed, not just acknowledged.
87. Trigger 6, 7, 44, 91, 94 and 96 (the refusals and the no-ops): each raises a card. Nothing in the app answers a
    user action with silence except a successful playlist create (27), which announces instead.

---

## 11. Audit log

Adversarial re-read of every cited file against the 0.2.9 source (2026-09-12). One line per correction; `wrong` =
the chapter stated something the code contradicts, `missing` = a state/element/rule the code has and the chapter did
not, `unverified` = a claim that could not be confirmed as written, `overclaim` = a true-ish statement pushed further
than the code supports.

| # | kind | section | correction |
|--:|---|---|---|
| 1 | **wrong** | §2 W4 | Hysteresis was stated backwards ("appears at 1360, survives to 1320"). `Resolve` computes `reserved = StageFor(width − 40)` and ANDs it into a promotion, so a stage promotes **late** and demotes on the raw threshold: the name enters at **1400** / leaves below **1360**; actions enter at **1240** / leave below **1200** (`MergedChromeLayout.cs:51-68,121-124`). §0 #5 and parity 12 corrected with it. |
| 2 | **wrong** | §5 | Palette open slide was `+50 → 0` ("slides up from below"). `PopupEntranceSlide` returns `opensUp ? +50 : −50`, and the palette's `BottomEdgeAlignedLeft` popup opens downward ⇒ **−50 → 0**, settling downward (`OverlayHost.cs:1413-1422`). Parity 11 corrected with it. |
| 3 | **wrong** | §4.4 | "the invariant `pressed > hover` holds in both" is false. Dark `RowPressed` `#FFFFFF0A` < `RowHover` `#FFFFFF0F`, and `FillSubtleTertiary` < `FillSubtleSecondary` in **both** themes (`PaletteBuilder.cs:118-119,160-162,214-215,302-303`). Only `WaveeColors.Row*` in light darkens on press. Parity 69 corrected with it. |
| 4 | **wrong** | §2 W2 | The "score 2 (subsequence)" example row was `Play file…`, which is a **prefix** match for `pl` (score 0). Replaced with a real subsequence example. |
| 5 | **wrong** | §6.10 | `"Podcast"` / `"Song"` are not in `KindLabel` (`Menus.cs:605-611` has Album/Artist/Playlist + `""`); they are the menu-header subtitle fallbacks at `Menus.cs:620` and `:664`. Line refs corrected and six further hard-coded strings added. |
| 6 | **missing** | §2 W15 | Only 2 of the 6 `AppUpdateState` update-row forms were drawn. Added the full glyph/tint/title/actions table, including `Snoozed` (no `Later`), `Installing`, `Completed`, `Failed` and the empty-title `_ =>` arm (`NotificationPanel.cs:213-261`). |
| 7 | **missing** | §2 W17 | The topic dials are a branch that changes what is drawn: `ApplyTopicVisibility` drops silenced rows from `Items` and recounts `UnreadCount` (`NotificationCenterBridge.cs:197,357-376`). Added, plus a §7 data row and parity 71. |
| 8 | **missing** | §2 W14 | The update action strip is `Gap 6, Wrap = true, MarginTop 4` — three pills wrap to two lines at 330 DIP (`:277`); the progress strip is a different `Gap 8, MarginTop 6` (`:268`). Added to the wireframe notes and to §3. |
| 9 | **missing** | §2 W14 | The social row's relative time is conditional on `s.Timestamp > 0` (`:309`); the type pill has **five** labels, not two (`:379-389`); artwork is hash-seeded so a missing image is never blank (`:320,355`); `nc == null` renders a bare 380-wide box (`:65`). |
| 10 | **missing** | §2 W16 | The activity detail block has three body shapes — target (falling back to the raw uri), the `{old} → {new}` rename line, and `TRACKS` ≤ 8 (`:452-490`). Added the 11-glyph `ActivityGlyph` table and the `ActivitySummary` / `SavedSummary` sentence ladder (`:492-547`), none of which the chapter carried. |
| 11 | **missing** | §2 W19/W20 | The custom card exists only while `Downloading`; a toast shows only `plan.Actions[0]`; a toast DOES pass `availableWidth: 380` so it can flip vertical (unlike the banner); the 4th toast queues FIFO rather than dropping; an `Off` App-updates dial suppresses the toast entirely. |
| 12 | **missing** | §5 | The notification panel had no motion rows at all — it is `PopupChrome.Popup`, so it takes the same 367 ms −50 slide + 83/83 fade as the palette. Added, with the toast card's `Transition = MotionTok.StandardEnter` and the bell badge's deliberate absence of motion. |
| 13 | **missing** | §2 W24 | The dialog does not always open at `Offer`: `PhaseSig` seeds to `Ready` when `RuntimeStatus.IsReady` (`PlaybackRuntimeSetupCard.cs:92`). Added, plus parity 76. |
| 14 | **missing** | §2 W32 | `CatalogState.NotFetched` has **no** arm in `Advanced` — no busy row, no copy — and is reachable with no provisioner or after a cancelled fetch (`:281-283,305`). Added as a fourth state with the `EnsureCatalog` idempotence rule. |
| 15 | **missing** | §2 W4/W5 | The chip/menu unknown-data forms (`"—"` display name, initials fallback, the email line that disappears rather than reserving space, the speculative "Spotify Free") were only implied by §7. Added as a table. |
| 16 | **missing** | §2 W4 | `StageFor` sheds the trailing island entirely under width pressure (`MergedChromeLayout.cs:130-136`, `MergedChromeRow.cs:142-143`) — a fourth chip form in which there is no chip and no fold home. Added, plus parity 12b. |
| 17 | **missing** | §6.5 | Five escalator gates were undocumented: `ToastNotifier.IsSupported`, the **first-run** watermark rule, the oldest→newest walk, the singular `"1 more update in Wavee"`, and `policy.Sound` ⇒ `Silent()`. Added as a table; parity 79/80. |
| 18 | **missing** | §6.6 | Tips: the inert-handle slot release (`:154`), the null-settings gate, the caller-overridable `PlacementMode`, `ResetAll`, and the append-only `WaveeTipIds` contract. |
| 19 | **missing** | §6.8 | The always-on log lines this surface owns (banner ×3 + ~18 setup-model events) had no home in the chapter, and the working rules make them non-optional. Added. |
| 20 | **missing** | §8 | Two pure rules were absent: `AboutUpdatePanel.StateSentence` (the ONE sentence shared by the row, the toast body and Settings › About — and **untested** today) and `AppUpdateVersion.ReleaseTagVersion`. |
| 21 | **missing** | §2 W2 | The query normalisation rules (`Trim` for plain queries, space-skip after `>`, pre-lowercased `LabelLower`, `Icons.More` glyph fallback for registry rows) and what an empty query / bare `>` render. Parity 11b. |
| 22 | **overclaim** | §2 W23 | "InfoBar flips vertical when `hasAction && hasMessage && message.Length >= 60`" is one of **three** OR-ed conditions; `contentItems <= 1` and the width estimate are the others (`InfoBar.cs:232-242`). True for the banner (3 items, no `availableWidth`), false as a general statement — restated where the toast's own vertical case is described. |
| 23 | **unverified → confirmed** | §2 W22 | The four banner message lengths (44 / 62 / 37 / 37) were re-counted against `en-US.json`; all four are exact, and only `noPack` clears the 60-character rule. No change. |
| 24 | **unverified → confirmed** | §3 | Spot-checked every engine number the chapter leans on: `Spacing`/`Radii`, `MenuFlyout` row 36 / margin 4,2,4,2 / padding 11,8,11,9 / chevron E974@12 / `SubMenuShowDelayMs` 400, `ContentDialog` 320..548 × 184..756 / pad 24 / scroller `MaxH − 200` = 556, `TeachingTip` 320..336 × 40..520 / content margin 12 / 40×40 ✕, `Toast` 300..380 / gap 8 / pad 24 / `MaxVisible` 3 / 5000 ms, `ProgressBar` 1.5 radius / 2000 ms / 40 %+60 %, `ProgressRing` 2000 ms cubic-bezier(0.167,0.167,0.833,0.833), `Announcer` 100 ms, and every glyph code in the chapter against `glyphs.json`. All correct as written. |
| 25 | **unverified → confirmed** | §8 | `WaveeCommandsTests` genuinely does not exist (`src/apps/Wavee.Tests` has no palette test); every other cited test file does. No change. |
| 26 | **missing** | §3 → new §3.1 | critic-fix: the dialog width ladder existed nowhere. §3 gave the setup dialog's 460 as if it were the chrome constant, while 26 uses 480, 28 and 22 use 548, and 28's plates are 720/762 — with no rule saying which is sanctioned. Added **§3.1 The modal width ladder** with the engine envelope (`ContentDialog.cs:110` Min/Max 320…548 × 184…756; `:283` `Math.Clamp(DialogWidth ?? (buttons.Count >= 3 ? 480f : MinW), MinW, MaxW)` — `DialogWidth` is a request, silently clamped), the four rungs and the rule that picks each (**320** = the forgotten default and a shipped defect, `SidebarItemPickers.cs:55-58`; **460** prose + one full-width bar, `PlaybackRuntimeSetupCard.cs:38`; **480** list/picker or 3 buttons, `SidebarItemPickers.cs:58,61`, `SidebarCustomizerPage.cs:717`; **548** form or payload dump, `ReportDialog.cs:35-36`, `LyricsInspectorDialog.cs:59`, `PlaybackRuntimeSetupCard.cs:779`), plus the derived content widths 412 / 432 / 500 (`rung − 2×24`). §0 #16, the §3 rows for the setup + signature cards, and §9.1 #11 now cite the rung instead of the literal; parity 58b/58c measure it. |
| 26b | **wrong (in the finding)** | §3.1 rung ✗ | The critic listed 28's **720** as a fifth `ContentDialog` width. It cannot be one — 720 > `MaxW` 548, so the engine would clamp it. `AfterUpdateDialog.cs:24-25` says so outright ("A raw overlay with `PopupChrome.Modal`, not `ContentDialog`: the plate is 720 DIP wide and ContentDialog hard-clamps its card"), and 28 §0 #1 says the same of the 762 wizard plate. Recorded as the ladder's escape hatch (rung ✗: above 548 you change control and give up the whole ContentDialog chrome), not as a rung. |
| 26c | **missing** | §3.1 | critic-fix, while building the ladder: rung **320** is not unused. `SettingsShared.Confirm` (`SettingsShared.cs:29-41`) sets **no** `DialogWidth` at all, so every Settings/diagnostics confirm (27 §2 W27) renders at the 320 minimum — correctly, since it passes `d.Message` and no `d.Content`. The rule is therefore "`Message` ⇒ rung 1 is right; `Content` ⇒ name a rung", not "always set `DialogWidth`". Also recorded: `DialogWidth` is read once when the card is built (`ContentDialog.cs:283`), so a phased dialog cannot widen mid-flight — which is why the signature sheet is a nested 548 dialog rather than the setup dialog growing (`PlaybackRuntimeSetupCard.cs:775-779`). |
| 26d | **missing** | §3 | critic-fix, second half: this chapter's own logout-confirm (380, 320..420) and play-link (420, 360..480) cards are **not** `ContentDialog`s — bare `PopupChrome.Modal` `BoxEl`s with no title band, no separator and no command-row fill, and the only two dialogs in the app with a width *range* (`ProfileMenu.cs:323`, `PlayLinkDialog.cs:109,215`). Marked **off-ladder** in §3 and explained in §3.1, so a re-author does not "harmonise" them onto a rung and silently add the `FillLayerAlt` band. |

### Second adversarial pass (2026-09-12, independent re-read of all 15 assigned files + the engine controls)

Round 1's log above holds up where it goes; these are what a fresh read of the source found on top of it.

| # | kind | section | correction |
|--:|---|---|---|
| 27 | **wrong** | §2 W16 | The activity detail's eyebrow was drawn as `TRACKS`. The loc value is `"Tracks"` (`en-US.json` `notifications.activity.detail.tracks`) and `WaveeType.Eyebrow` takes the string's OWN casing — "no call site may caps-transform it" (`WaveeType.cs:43-54`). Sentence case, unlike the release type pills, which really are authored in CAPS. Wireframe + prose + parity 39b corrected. |
| 28 | **wrong** | §2 W5 / W6 | Both menu-card height sums were short one row. W5 at ≥ 1200 draws FIVE rows (Account · Settings · Play · Theme · Log out) + 2 separators = `5*40 + 2*3`, h ≈ **307** (was `4*40`, 267); W6 adds Notifications + Friends = SEVEN rows + 3 separators = `7*40 + 3*3`, h ≈ **390** (was `6*40`, 350). Row 36 + margin (4,2,4,2) = 40; separator 1 + 1 + 1 = 3 (`MenuFlyout.cs:61,74`, `ProfileMenu.cs:201-228`). |
| 29 | **wrong** | §2 W1 | The palette height formula omitted one of the body's two `Spacing.XXS` gaps: pad 8 + field 32 + gap 2 + spacer 2 + **gap 2** + rows 263 + pad 8 = 317. The stated sum `8+32+2+2+263+8` is 315 — the total was right, the derivation was not (`WaveePalette.cs:185,195-196`). |
| 30 | **wrong** | §0 header | "**3,914 lines** owned outright" contradicts §9.4's 4,114. The 14 files listed in the header sum to **4,114** (`wc -l`), which is the number §9.4 uses. Header corrected. |
| 31 | **overclaim** | §8 | `StateSentence` was described as naming the release through `ReleaseName` in every arm "so no arm can interpolate a codename twice". Four arms do; `Snoozed` prints `TargetSemVer ?? TargetQuad ?? ""` (`SettingsPage.About.cs:673`) and is the one arm that can render an empty name. Restated as an asymmetry to port, not to harmonise. |
| 32 | **overclaim** | §2 W12 | "`play.noOwner` — an in-place answer, never a toast" is true of the card and false of the surface: `PlayLink.PlayDirect` (the `wavee://play?link=` lane, which has no status row) raises the same sentence as an Informational toast (`PlayLinkDialog.cs:60,73`). Qualified in place. |
| 33 | **missing** | §7 · §9.1 #18 | **The biggest behavioural find of this pass.** `NotificationMerge` excludes Activity from the bell count by design (`NotificationMerge.cs:34-37`), but `ApplyTopicVisibility`'s recount is a bare `if (n.IsUnread) keptUnread++` (`NotificationCenterBridge.cs:373`) and `ActivityNotification.IsUnread == !Entry.Read` (`NotificationModels.cs:52`) — so the moment ANY topic is dialled Off the exclusion stops applying and unread activity starts lighting the bell. Recorded as a divergence 0.3 must resolve deliberately (one shared counting predicate), plus a §7 row and parity 71b. |
| 34 | **missing** | §2 W32b (new) | The phase graph was implicit in eight wireframes and stated nowhere. Added **W32b**: Cancel has three destinations (`StartDownload` cancel → **Offer** `:191-196`; `InstallSelected` cancel → **Advanced** `:224`; `CancelUntrusted` → **Offer** `:345`); `Check for update` and `Try again` are both `StartDownload()` (`:268,320`), so `Ready` makes a full round trip through `FetchingCatalog`; the `Failed` body can be a **raw `ex.Message`** (`:197,225`) rather than a localised sentence; `RegisterDir` runs synchronously under `Phase.Verifying` (`:401-407`); `UseInstalled` is `#if WAVEE_PLAYPLAY_LOCAL` and answers `Failed`/`notActive` in a public-only checkout (`:383-390`). Parity 70b–70d. |
| 35 | **missing** | §2 W20 | The update lane's toasts are four different InfoBar SHAPES, and `contentItems <= 1` decides one of them: `Installing` has an empty title, a 30-character body and no action ⇒ **vertical, message-only** (`AppUpdateToasts.cs:84-89`, `InfoBar.cs:229,238`; `update.state.installing` = "Restarting to finish updating…"). `Available`'s body is the quad, and only when it differs from the release name (`:72`); `Downloading` and `Completed` are title-only. Added as a table; parity 48b. |
| 36 | **missing** | §2 W1 | The palette's 19 builtin glyphs and five route keys were nowhere in the chapter (`WaveeCommands.cs:197-216`) — including `nav.library → "liked"`, the three zoom rows borrowing Add/Remove/Undo for want of a Zoom glyph, and `Icons.More` E712 as the registry fallback. Added as a table; parity 11c. |
| 37 | **missing** | §2 W1 | Two structural facts: the index is built from `UseContext(WaveeExtensionRegistry.Slot) ?? Host.Actions.Extensions` (`WaveePalette.cs:90`), and **the card is a hard 560 with no clamp** (`:27,185`) — under ~576 DIP of layout width it is wider than the window. Recorded as a constant to port, not a bug to fix. |
| 38 | **missing** | §2 W4 | **App zoom moves every threshold in this chapter.** `Resolve` is fed the DIP viewport (`WaveeShell.cs:815`, `AppHost.cs:4484`) and `FluentApp.SetZoom` re-derives the window scale (`FluentApp.cs:78-85`), so at 125 % a 1440-px window is 1152 DIP and the name + actions have already folded. The palette's own zoom rows therefore change the chrome ladder as a side effect. Parity 11d. |
| 39 | **missing** | §2 W14 | Four row facts: the panel is `Width = MinWidth = 380` and the five filter pills are `Shrink = 0` in a non-wrapping row (`:92,150,159`), so a longer localisation overflows rather than wraps; `UpdateRow` is `AlignItems = Start` while every other row frame is `Center` (`:284` vs `:314,349,420`); the activity card's dot reads `!e.Read` rather than the merged `IsUnread` (`:429`); `RowFor` has a silent `_ => new BoxEl()` arm (`:181`). |
| 40 | **missing** | §2 W16 | Four arms of the summary ladder were flattened: `PlaylistVisibility` is two sentences chosen by `NewIsPublic ?? false` (`:504-506`), `PlaylistRename` names the NEW name (`:503`), the unsave half is separately authored copy ("Removed … from Liked Songs" / "Unfollowed {n}"), and there is a `_ => ""` arm that renders a card with an EMPTY summary line (`:512`). Also: `renamedFrom` is `{old}  →  {new}` with two spaces on each side. Parity 38c, 39c. |
| 41 | **missing** | §2 W16 · §5 | Expanding an activity row **swaps the keyed child** at that list slot (`ntf:act:<id>` ⇄ `ntf:actwrap:<id>`, `:432,447`), and the card carries its own second `Enter`/`Exit`/`Layout` declaration at `:438-440` beside the generic `Card`'s at `:196-198`. §5's "no transition" row was true of the wrapper and misleading about the toggle. Restated, with parity 38b asking for a frame recording — the one motion in this surface the source alone cannot settle. |
| 42 | **missing** | §9.2 | Both anchored handles null themselves on close (`handle.ClosedAction = () => handle.Value = null` — `ProfileMenu.cs:173`, `NotificationPanel.cs:40`). The toggle test tolerates a stale handle today; the `Ref` would still pin a dead overlay entry. Added as a trap. |
| 43 | **unverified → confirmed** | §2 W4 · §3 · §4 · §5 | Re-derived independently rather than trusting round 1: the hysteresis (name 1400/1360, actions 1240/1200) off `MergedChromeLayout.cs:51-68,121-137` + `ShellResponsiveLayout.cs:29,32,42`; the palette's −50 entrance off `OverlayHost.cs:1413-1422` (`BottomEdgeAlignedLeft` ⇒ `opensUp` false ⇒ −50); the press-is-dimmer polarity off `PaletteBuilder.cs:117-119,159-163,214-215,302-303` (dark hover `0x0F` / pressed `0x0A`, light hover `0x0D` / pressed `0x12`); the four banner message lengths (44 / 62 / 37 / 37) off `en-US.json`; `Spacing` (XXS 2 … XXXL 32, so the lane's `XXXL*2` really is 64), `Radii` (Control 4, Overlay 8), `MenuFlyout` (row 36, margin 4,2,4,2, padding 11,8,11,9, chevron E974 @ 12, separator −4 bleed, `SubMenuShowDelayMs` 400), `ContentDialog` (320…548 × 184…756, pad 24, `MaxH − 200` = 556, the `Math.Clamp` at `:283`), `Toast` (300…380, gap 8, pad 24 + `EdgeInset` 72, `MaxVisible` 3, 5000 ms, `Transition = MotionTok.StandardEnter`, `availableWidth: 380`), and every glyph codepoint in the chapter against `glyphs.json`. All correct as written; no change. |
| 44 | **unverified → confirmed** | §5 | Re-ran the grep §5 claims: across all 15 assigned files the only motion/timer APIs are `NotificationPanel.cs:63` (`UseInterval`, 30 000 ms) and `:196-198` / `:438-440` (the Enter/Exit/Layout pairs). No `Stopwatch`, no `Environment.TickCount64`, no app-side `Animate` call anywhere in this surface — every other animation in §5 belongs to an engine control. |

### Completeness-critic pass (2026-09-12)

| # | kind | section | correction |
|--:|---|---|---|
| 45 | **missing** | §3.1 | critic-fix: the modal width ladder was authored **twice** — here (§3.1, "this chapter is its home") and in 29 §2 W12 (drawn to scale) — and each copy carried call sites the other did not. Verified against 0.2.9 and **merged into this section**: rung 1 now names all four sites (`SettingsShared.cs:29-41`, `ContainerActions.cs:170,180-184` `RenameDialog` with a `MinWidth = 320` `EditableText` inside, `TrackActions.cs:161-168` "View credits" with `PrimaryText = ""`, `Menus.cs:518-524` `OpenPicker` likewise), and rung 3 now records that the Storage move-cache pair (`SettingsPage.Storage.cs:626-640,642-656`) reaches 480 **without** setting `DialogWidth`, through the engine's three-button default (`ContentDialog.cs:283`) — the only sites in the app that do. Added the **shared card metrics** block 29 carried and this section did not (pad 24 `:111`, content gap 12 `:112`, button gap 8 `:113`, title 20 SemiBold `:114`, body 14 `:115`, button 130 × 32 `:116-117`, corners/border/shadow `:365-369`, the 556 inner scroller `:308`, the single-button left star column `:286-290`, button order primary→secondary→close `:279-281`). The two off-ladder `PopupChrome.Modal` plates (logout 380/320..420 `ProfileMenu.cs:323`; play-link 420/360..480 `PlayLinkDialog.cs:109,215`) and the four "consequences a re-author must carry" were already here and are kept. The closing pointer paragraph now names 29 §2 W12 and 29 §12.3, plus 01, 06 and 27 §2 for their call sites. **Sibling edit still owed (29 is not this file):** 29 §2 W12 should shrink to the scale drawing + a one-line pointer here, and 29 §12.3's "every `ContentDialog` (the width ladder, the confirm shape)" should keep only the confirm shape (29 §2 W13). Parity 81–83. |
| 46 | **missing** | §3 → new §3.2 | critic-fix: **no chapter owned the toast inventory.** This chapter specified the strip (W19, W20), the severity ramp (§4.2) and the OS escalation (§6.5) but never *which action raises which card* — and Wave 4 owner I folds `WaveeCommands + Actions/*` into one action table, so a table with no toast column would drop every confirmation and every refusal in the app with nothing to catch it. Added **§3.2**, one row per call site: **106 real `Toast.Show` calls across 38 files** (the critic's 107 counts `AppUpdateToasts.cs:27`, which is a `<param>` doc comment, not a call — corrected), each with sentence/loc key, severity, duration, action button and announce. Census findings worth their own line: the whole app departs from `DurationMs = 5000` exactly **three** times (`ReportChrome.cs:58` sticky, `SettingsPage.Notifications.cs:119` 6000, `ReportDialog.cs:519` 8000, plus the update plan's conditional `NotificationCenterBridge.cs:236`); `DedupeKey` is set at exactly **three** keys (`crash.pendingReport`, `update`, `PlayLinkActions.FailureToastKey` — the last shared by `PlayLinkDialog.cs:88,195` and `PlaybackBridge.cs:1096`); `Title` and `CustomContent` have **one** call site between them (`NotificationCenterBridge.cs:234,240`); **11 sites show a raw `ex.Message`** to the user; and only **four** toast sites also announce, always at the data chokepoint rather than on the card (`LibraryBridge.cs:292`, `FolderActions.cs:277`, `WaveeResourceDrag.cs:464`, `PlaylistCreateFlow.cs:79,97`) — so 0.3 must NOT add an `Announcer.Say` per toast. Five rules derived from the census close the section; §6.4's toast-call-site bullet now points at it; parity 84–87. |

**token-reconcile (2026-09-12):** four tokens this chapter names were missing from the first build of `00-design-system.md §12.1` and are now indexed there: `Tok.FillLayerAlt` (`#FFFFFF` OPAQUE / `#FFFFFF0D`), `Tok.SystemFillAttention` (`= Tok.AccentDefault`, hand-written, with **no `TokenSet` field**), `Tok.SystemFillAttentionBackground` (`#F6F6F680` / `#FFFFFF08`) and `Tok.ControlElevationBorder`. No value in this chapter changed.

**arbitration 2026-09-12:** the notification stack and the teaching tips each get one home, and this chapter’s rows
were rewritten to them. **A8/A9/A11** — `AppUpdateToasts`, `NotificationMerge`/`ReadIds`/`Policy`/`Prefs`,
`AboutUpdatePanel.StateSentence`, `AppUpdateVersion` and `ToastEscalator`’s decision half all move from
`Platform/Platform.cs`/`Platform.Host.cs` to **`Platform/Notify.cs` (CORE) + `Platform/Notify.Host.cs` (SHELL),
owner I, Wave 4** (§7 DATA GAPS, §8, §9.3.2, §9.3.7, §9.4, §9.5 and the header target line). The proposed
`Entities/Notification.cs` + `Notification.UI.cs` pair is **withdrawn**: the feed folds into `Notify.cs` and the
panel and its rows stay in `Shell/Shell.UI.cs`, whose share of §9.4’s split rises 1,500 → 2,000 to say so. §9.4 now
also splits `Notify.cs` explicitly — ~555 this chapter’s feed fold, ~445 ch. 14’s toast/policy half, ~1,000 whole —
and marks `Notify.Host.cs`’s ~715 as ch. 14’s number, so the two chapters cannot count the same lines twice.
**A10** — `WaveeTips`/`WaveeTipsCore` are **this chapter’s**, in `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs`
SHELL; §7 and §9.5 now state that as decided rather than proposed, and ch. 28 drops its competing claim. No 0.2.9
fact, number or citation in §§0–6 changed.

**consistency 2026-09-12:** header and §1.2 named only `Shell.UI.cs`/`Shell.Palette.cs`/`Shell.cs` for this
chapter's whole surface. Plan §2 splits it further: the command palette overlay/card are `Shell.Palette.cs` (not
`Shell.UI.cs`); the profile chip/menu, play-link dialog and runtime banner chrome are the named partial
`+Shell.Overlays.UI.cs` (1,700), not `Shell.UI.cs`; only the notification panel and its rows stay in `Shell.UI.cs`
(A9); and the runtime setup card's body is the named partial `+Screens/Setup.UI.Runtime.cs` (A18), not
`Screens/Setup.UI.cs`. Header, §1.2's tree, §9.3 bullet 6 and §9.4/§9.5's split all corrected to match.

**answers 2026-09-12: Q9 (plan §9.6) confirms A18 exactly as this chapter's own §9.3 item 6 and §9.4/§9.5 already
settled it** — the runtime provisioning card's body is `+Screens/Setup.UI.Runtime.cs` (1,100), written by owner I in
Wave 4, mounted again by owner R's wizard in Wave 6. Nothing in this chapter's numbers or file assignments changes;
§9.5's line noting Q9 now distinguishes it from `Screens/Setup.Host.cs`'s separate, still-open budget gap.
