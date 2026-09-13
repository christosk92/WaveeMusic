# Wavee 0.3 — system tray (notification area) support: investigation and implementation plan

> Written 2026-09-13 against the `feat/0.3-structure` worktree (`C:\WAVEE\wavee-0.3`) and the pinned engine
> (`C:\WAVEE\fluent-gpu-base`). Sibling of `wavee-0.3-implementation.md` (the tree, owners, waves) and of
> `wavee-0.3-headless-implementation.md`, whose §6 defines the tray/headless boundary this plan's §10 adopts.
> Every line reference below was read on that date; the eight implementation agents are mid-flight, so a line number
> in a `Wavee/` file may have drifted by the time you open it — the `_old/` and engine references are stable.

---

## 0. The one-paragraph answer

0.2.9 had **no tray icon at all** — not a `Shell_NotifyIcon` call, not a close-to-tray branch, not a start-hidden
arm; its whole outside-the-window surface was the taskbar button (overlay + progress + thumbnail toolbar), the SMTC
card, the jump list and the Windows toasts, and 0.3 ports exactly that set in `Playback/Playback.Os.cs` (830 of an
840-line budget) and `Platform/Notify.Host.cs`. The engine offers everything *around* a tray icon (a registered
`TaskbarCreated` message, a hidden top-level sentinel window pattern, `TrackPopupMenu` for the caption menu, the
`ImmersiveColorSet` theme broadcast, a `SingleInstanceGate` that finds a hidden window fine) but **nothing for the
icon itself, no way to hide the main window without destroying it, and no way to intercept `WM_CLOSE`** — so the
tray is two pieces of work: **five small engine seams + one reusable `FluentGpu.WindowsApi.Shell.NotifyIcon` host**
(made and gated in the engine repo), and **two new app files** `Platform/Tray.cs` (CORE, the decisions, xunit-tested)
+ `Platform/Tray.Host.cs` (SHELL, the binding) that reach playback through the same `Playback.Os.Publish` push and
the same `Playback.TogglePlay/Next/Previous` verbs the taskbar uses. Recommended behaviour: icon **always present**
(a "W" monochrome glyph, three states, light/dark taskbar variants, 16–48 px frames), left click = open/hide Wavee,
right click = a **native** context menu (now-playing line, play/pause, next, previous, open, quit), close-to-tray
**off by default** and a General-tab setting, no balloons (toasts already exist), no playing/paused state *in the
glyph* (it is in the tooltip, the menu and the taskbar button). A Windows-11-style mini-player flyout is costed and
**deferred**.

---

## 1. What 0.2.9 had (`src/apps/_old/`)

### 1.1 Tray: nothing

`rg -i "NotifyIcon|Shell_NotifyIcon|TaskbarCreated|StartupTask|autostart|tray"` over `_old/Wavee` finds only:

| Hit | What it is | Tray-relevant? |
|---|---|---|
| `_old/Wavee/App/TaskbarBridge.cs:67, 207-217` | `FluentApp.TaskbarButtonCreated += OnTaskbarCreated` → re-add the thumbnail toolbar after an Explorer restart | the **only** Explorer-restart arm in the app; the tray needs the same discipline for `NIM_ADD` |
| `_old/Wavee/Platform/AppSettings.cs:321-322` | `StartOnLogin = new("app.startOnLogin", false)` — "unpackaged: HKCU Run value; packaged: manifest StartupTask" | start-at-sign-in exists; **start hidden does not** |
| `_old/Wavee/App/DeepLink.cs:40-45` | `SyncStartupRegistration` — both-directions Run-value sync, unpackaged only | ported to `Shell.Host.cs:285-286` |
| `_old/Wavee/Program.cs:452-490` | single-instance gate, `wavee://` registration, `DeepLinkChannel.Post` + `DeepLink.WakeWindow()` on every redirect | the wake-on-redirect rule the tray must refine (§4.7) |
| `_old/Wavee/Program.cs:644-672` | the update-on-quit message pump and its hidden **top-level sentinel window** | the pattern the tray's callback window reuses |
| `_old/Wavee/App/ReleaseNotifier.cs:14` | "no background process, service or tray resident is involved" | an explicit statement that 0.2.9 had no tray resident |
| every other `tray` hit | the CD deck's disc tray (`Deck/Faces/CdDeck.cs:78-92`), "stray" | unrelated |

The word "minimize" appears only as animation/clock gating (`UseInterval` auto-pauses while minimized). There is no
close-to-tray, no minimize-to-tray, no `WM_CLOSE` interception (the engine destroys the window on `WM_CLOSE`,
`fluent-gpu-base/src/FluentGpu.Windows/Pal/Win32Platform.cs:1782`, and the app never sees it).

### 1.2 What users could already do from outside the window

| Surface | 0.2.9 file | 0.3 home | What it gives |
|---|---|---|---|
| SMTC card (Win11 media flyout, lock screen, media keys, headset AVRCP) | `App/SystemMediaControlsBridge.cs` | `Playback.Os.cs:139-292` (`Os.Smtc`) | play/pause/next/prev/seek, title/artist/album/art — **the media keys arrive here** (`:36-39`) |
| Taskbar button overlay + progress | `App/TaskbarBridge.cs:83-136` | `Playback.Os.cs:335-439` | paused badge, green/yellow fill |
| Thumbnail toolbar | `TaskbarBridge.cs:155-191` | `Playback.Os.cs:441-509` | prev / play-pause / next on hover |
| Jump list | `App/JumpListBridge.cs` | `Playback.Os.cs:512-690` | `wavee://pause` / `wavee://resume` task, Search, six recents |
| Toasts | `App/ToastEscalator.cs` etc. | `Notify.Host.cs` | release drops, daylist, social, update progress; a click deep-links |
| Deep links + single instance | `Program.cs:452-490` | `Shell.Host.cs:132-149, 296-361` | `wavee://open|play|resume|pause|report` |

So the tray does **not** need to invent a transport: the verbs already exist (`Playback.Host.cs:595-606`) and every
deep link already lands in `Shell.ApplyDeepLink` (`Shell.Host.cs:336-361`). What it adds is *presence while the
window is gone* and a one-click way back.

---

## 2. What 0.3 has today (the seams a tray needs)

### 2.1 The playback push contract — where icon state comes from

`Playback.Os.Publish(in State s)` (`Playback/Playback.Os.cs:116-128`) is the ONE seam the drain calls
(`Playback.Host.cs:408`: `if (s_fx.Smtc || s_fx.SmtcTimeline) Os.Publish(in s_state)`), and each surface is a sink
holding its own last-pushed snapshot (ch 14 §1b "push contract"). Rule 1 (`Playback.Os.cs:19-21`): read the STATE,
never the engine player, because only the state is correct while a remote Connect device owns playback. The tray
icon is a fifth sink on that same call: **no polling, no signal subscription, one edge-deduped `NIM_MODIFY` per
change.** Position ticks are irrelevant to it (`PublishPosition` is not wired to it).

Verbs the tray calls (`Playback.Host.cs:595-606`): `Playback.TogglePlay()`, `Next()`, `Previous()`, `Pause()`,
`Resume()`; the picker request `Playback.RequestDevicePicker()` (`:561`); `Playback.Snap()` (`:273`) for a whole
copy of the state when composing the tooltip outside a push.

### 2.2 The window and its lifecycle — where the gaps are

`Shell.Run()` (`Shell/Shell.Host.cs:124-206`) is the composition root's second half: gate → protocols → theme →
documents → `FluentAppHarness.Run(...)` → exit tail (`Session.Flush`, `Notify.HostShutdown`, `Playback.Os.Shutdown`,
`gate.Dispose`). `Shell.WakeWindow()` (`:312-318`) is `ShowWindow(IsIconic ? SW_RESTORE : SW_SHOW)` +
`SetForegroundWindow`, called from the redirect handler (`:300-304`) — on **every** redirect, including a jump-list
`wavee://pause` (see §4.7). `Shell.Auth` (`:86`, `AuthState` at `Shell/Shell.cs:1337-1348`: Live / Connecting /
Offline / SignInRequired) is the signed-out fact. `Shell.OnActivationRedirected` parks the payload; `Shell.UI.cs`
drains it on the UI thread.

Three things the shell **cannot** do today, all engine-side:

1. **Intercept close.** `Win32Window.Handle32` handles `WM_CLOSE` by `_closed = true; DestroyWindow(hWnd)`
   (`Win32Platform.cs:1782`); the harness loop is `while (!window.IsClosed)` (`FluentApp.cs:437`). There is no
   `CloseRequested` seam on `IPlatformWindow` (`Engine/Seams/Pal/Pal.cs:603-780`: `Minimize`, `ToggleMaximize`,
   `CloseWindow`, `SetFullscreen` — all one-way commands).
2. **Hide the main window.** `IPlatformWindow` has `Show()` (`Pal.cs:728`) and no `Hide()`; `Win32Window.Show` is
   `ShowWindow(SW_SHOW) + UpdateWindow` (`Win32Platform.cs:871-875`). Only `IPlatformPopupWindow` has `Hide()`
   (`Pal.cs:541`). And the host parks its frame loop on **minimized only**: `AppHost.IsMinimized =>
   _window.State == Minimized` (`AppHost.cs:3285`), `RecommendedWaitMsCore` returns -1 (block on messages) when
   minimized (`:2074`), `UpdateWindowVisible` folds it into the `Activation.IsActive` ambient (`:3289`). A window
   hidden with `SW_HIDE` reports `WindowState.Normal` (`Win32Platform.cs:903-904`: `IsZoomed ? Maximized : IsIconic ?
   Minimized : Normal`) — the loop would keep reconciling and laying out a window nobody can see, with the device's
   covered/cloaked stand-down (`AppHost.cs:2298-2304`) only saving the *present*.
3. **Start without showing.** `FluentApp.RunCoreOnUiThread` calls `window.Show()` unconditionally
   (`FluentApp.cs:360`); `AppOptions` (`:789-830`) has no start-hidden knob.

### 2.3 What the engine already offers that the tray reuses

| Need | Engine has | Where |
|---|---|---|
| `TaskbarCreated` registered message | `RegisterWindowMessageW("TaskbarCreated")` once per process + `ChangeWindowMessageFilterEx(MSGFLT_ALLOW)` (UIPI) on the main window; raised as `FluentApp.TaskbarButtonCreated` | `Win32Platform.cs:677, 700-703, 740-742, 1771-1777`; `FluentApp.cs:109-114` |
| A hidden top-level window that receives broadcasts | `MessagePump.CreateSentinel` — "Top-level is load-bearing: the end-session broadcast skips `HWND_MESSAGE` windows entirely" | `FluentGpu.Windows/Hosting/MessagePump.cs:58-66, 210-239` |
| The UI thread pumps **every** window on the thread | `PeekMessageW(&msg, HWND.NULL, …)` + `MsgWaitForMultipleObjectsEx(QS_ALLINPUT)` | `Win32Platform.cs:1205, 1543-1556, 1617-1624` |
| Native popup menu | `TrackPopupMenu(sys, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, 0, hWnd, null)` for the caption menu | `Win32Platform.cs:2221-2241` |
| OS theme change broadcast | `WM_SETTINGCHANGE("ImmersiveColorSet")` → `Win32App.RaiseSystemColorsChanged` → `FluentApp.SystemColorsChanged` | `Win32Platform.cs:1872-1877`; `FluentApp.cs:116-121` |
| App theme read | `Win32Theme.SystemUsesLightTheme()` reads **`AppsUseLightTheme`** — the *app* theme, not the taskbar's | `FluentGpu.Windows/Pal/Win32Theme.cs:77-87` |
| DPI | `GetDpiForWindow` at creation, `WM_DPICHANGED` for the main window | `Win32Platform.cs:756-759, 1841-1860` |
| Second launch finds a hidden window | `FindWindowW("FluentGpuWindow", null)` — finds hidden windows; `WM_COPYDATA` lands on the UI thread and calls `SetForegroundWindow` | `SingleInstanceGate.cs:138-165`; `Win32Platform.cs:2034-2053` |
| Out-of-bounds engine flyout | `Win32PopupWindow`: `WS_POPUP` **owned** by the main HWND, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, rendered by the host from a scene subtree | `Win32PopupWindow.cs:9-28`; `AppHost.OpenPopupWindow` `:4432-4459` |
| Icon file loading | `LoadImageW(IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE)` (taskbar overlay — SM_CXICON, not the small size) | `TaskbarManager.cs:53-56, 88-91` |
| `.ico` assets | `assets/AppIcon/appicon.ico` 16/24/32/48/64/128/256 from `appicon-source.png` via `generate-appicon.ps1`; `assets/taskbar/{prev,play,pause,next}.ico` 16+32 | `assets/AppIcon/generate-appicon.ps1:77-93`; ch 14 W8 |
| Start at sign-in | unpackaged `ProtocolRegistrar.RegisterStartup` (HKCU `Run`); packaged `desktop:StartupTask TaskId="WaveeStartup" Enabled="false"` | `ProtocolRegistrar.cs:141-162`; `ops/build/Wavee.AppxManifest.xml:81-85` |
| Activation kinds | `ActivationKind` Launch / Protocol / File / ToastActivated — **no StartupTask kind** (`ActivationArgs.cs:8` names WASDK's `StartupTask=39` only in a comment) | `FluentGpu.WindowsApi/Activation/ActivationArgs.cs:14, 111` |

Note the theme trap in row 6: the taskbar's colour follows `SystemUsesLightTheme` (the *Windows mode* toggle in
Settings ▸ Colors), while the engine's reader follows `AppsUseLightTheme` (the *app mode* toggle). A user with a
dark taskbar and light apps — the Windows 11 "Custom" combination — would get a black glyph on a black taskbar if
the tray borrowed the app reader. The tray needs its own reader (§7.1, engine change E5).

### 2.4 The settings side

`Platform.Keys` (`Platform/Platform.cs:75-254`) already carries `StartOnLogin` (`:249`, `app.startOnLogin`) and
`HandleSpotifyLinks` (`:248`); `Shell.RegisterProtocols` (`Shell.Host.cs:273-292`) syncs both to the registry on
every unpackaged launch. Chapter 30 §1.1 (`30-appearance-preferences.md:185-189`) puts `app.startOnLogin` among the
keys "deliberately out of this matrix because they change behaviour, not pixels" — so the tray keys are chapter 27's
(the General tab, W1 at `27-settings-and-diagnostics.md:386-438`), not chapter 30's. The General tab today has
Language & region · Links · Graphics · Developer; the tray adds one group (§8).

---

## 3. Windows guidance and the well-regarded players (what fits Wavee, what does not)

Sources read for this section: Microsoft's notification-area guideline
([winenv-notification](https://learn.microsoft.com/en-us/windows/win32/uxguide/winenv-notification)), the
[`Shell_NotifyIconW`](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
and [`NOTIFYICONDATAW`](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)
references, the [EarTrumpet README](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/README.md),
the [foo_traycontrols](https://github.com/jame25/foo_traycontrols) component, MusicBee's
[close-to-tray thread](https://getmusicbee.com/forum/index.php?topic=11812.0&wap2=), Spotify's
[close-button idea thread](https://community.spotify.com/t5/Implemented-Ideas/Close-Button-Closes-Spotify-Windows/idi-p/1297/highlight/true/page/25),
a [Windows 11 overflow explainer](https://tech-champion.com/microsoft-windows/windows-11-taskbar-overflow-explained-why-some-icons-stay-hidden-even-after-you-enable-them/),
the [DPI icon-size answer](https://learn.microsoft.com/en-us/answers/questions/1425442/windows-11-always-takes-a-bigger-png-from-ico) and
[GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi).

### 3.1 The Win32 contract (taken verbatim)

- **Identity.** `NIF_GUID` + `guidItem` "is the recommended method of identifying the icon" and "overrides uID";
  once used, "you must use that same GUID … in any subsequent calls". **The trap:** "the path of the binary file is
  included in the registration of the icon's GUID and cannot be changed … the only exception … occurs when both the
  original and moved binary files are Authenticode-signed by the same company" (NOTIFYICONDATAW › Troubleshooting).
  An MSIX install path changes on every update (`WindowsApps\Wavee_<version>_<arch>__<hash>`), but the release
  script signs every build with Azure Trusted Signing, so the exception applies **to the packaged build only**. A
  `dotnet run` dev tree is unsigned and its exe path differs from the publish path — GUID identity there fails
  `NIM_ADD` on the second path. Rule (pure, tested): **GUID identity when `PackageIdentity.IsPackaged`, `hwnd+uID`
  otherwise** (§6.3 `TrayCore.UseGuidIdentity`).
- **Version 4 events.** With `uVersion = NOTIFYICON_VERSION_4` (`NIM_SETVERSION` "must be called every time a
  notification area icon is added") the callback carries `LOWORD(lParam)` = event (`WM_CONTEXTMENU`, `NIN_SELECT`,
  `NIN_KEYSELECT`, `NIN_POPUPOPEN/CLOSE`, the `WM_MOUSE*` range), `HIWORD(lParam)` = icon id (16-bit), and
  `GET_X/Y_LPARAM(wParam)` = the **anchor** (for a keyboard event, the icon's top-left). Keyboard: Enter/Space on a
  keyboard-selected icon → `NIN_KEYSELECT`; Enter on a mouse-selected icon → `NIN_SELECT`; the context key →
  `WM_CONTEXTMENU`. `NIF_SHOWTIP` keeps the standard tooltip under version 4 (otherwise it is suppressed in favour of
  the app's own popup). `NIM_SETFOCUS` "returns focus to the taskbar notification area … if the user presses ESC to
  cancel" the menu.
- **Tooltip.** `szTip` is 128 `WCHAR` including the terminator on Windows 2000+ → **127 characters**.
- **Icon sizes.** "If only a 16x16 pixel icon is provided, it is scaled … provide both a 16x16 and a 32x32";
  `LoadIconMetric(LIM_SMALL)`. The taskbar draws the tray at `SM_CXSMICON` for the taskbar monitor's DPI: 16 @ 100 %,
  20 @ 125 %, 24 @ 150 %, 32 @ 200 %, 40 @ 250 %, 48 @ 300 % (the DPI answer above; `GetSystemMetricsForDpi`).
  The guideline says "16x16, 20x20, and 24x24 pixel versions … for high-dpi" — 0.3 ships six.
- **Balloons.** On Windows 10 balloons became banners that "stay in the Notification Center until dismissed"; on
  Windows 11 they are transient. Wavee already has a real toast stack (`Notify.Host.cs`) with the AUMID, the
  activator and quiet hours; **the tray never uses `NIF_INFO`.**
- **Explorer restart.** Re-`NIM_ADD` (and re-`NIM_SETVERSION`) on the registered `TaskbarCreated` message — "the
  icon disappears when Explorer restarts, but the process is still alive" is exactly that bug.

### 3.2 Microsoft's UX guideline, applied

| Guideline (winenv-notification) | Wavee 0.3 |
|---|---|
| "Is the icon primarily to launch a program…? If so, use the Start menu" | The icon exists for **status + presence while hidden**, and its default click is "show/hide", not "launch" |
| "Minimized single-instance application" pattern: single instance ✓, runs for an extended period ✓, icon shows status ✓, can be a notification source ✓, **users must opt in** | Wavee is single-instance (`SingleInstanceGate`), long-running; the icon shows signed-in / offline / update; opt-in is the close-to-tray toggle (off by default). The guide's "no longer recommended for Windows 7 — use taskbar buttons" is why the taskbar button keeps every playback affordance it has and the tray adds no second copy of the thumbnail toolbar |
| "Use the Minimize button …, not the Close button" for minimizing to the area | Both offered; **neither on by default**; Christos decides the default (§11 Q1) |
| Hover: a tooltip "(Company) Feature — Status summary"; "don't include the software version"; "don't explain how to interact" | `Wavee — Midnight City · M83` / `Wavee — Paused: …` / `Wavee — Signed out` / `Wavee` |
| Left single-click: "display whatever users most likely want to see"; "users expect left single-clicks to display something" | show (restore + foreground) or hide the window |
| Left double-click: "perform the default command on the context menu" | Open Wavee (the bold default) |
| Right-click: context menu "near its associated icon, but away from the taskbar", default in bold, `Exit` last, "remove rather than disable items that don't apply", "use check marks to indicate state" | §5.1 — playing state as a check mark on nothing; play/pause is a verb whose label flips, prev/next removed when the state says `CanSkipPrev/Next == false`? **No** — greyed, matching the thumbnail toolbar (ch 14 W11); the guide's "remove" rule is for *features*, not transport enablement |
| "Provide a *Display icon in notification area* option on the icon's context menu"; "Exit quits the program for the current session and removes the icon" | "Hide icon" is a menu item that writes the setting; Exit is `Quit Wavee` |
| "Don't have an About command" | none |
| "Don't change the icon rapidly… don't flash… no long-running animations" | **no playing/paused state in the glyph** (it would flip on every skip); no spinner while buffering |
| "Avoid swaths of pure red, yellow, green" | monochrome glyph; the update badge is a filled dot in the glyph colour, not accent |
| "Display windows launched from notification area icons near the notification area" | the menu is anchored at the event coordinates; the *window* is the main window, restored where it was |
| Windows 11: new icons land in the overflow chevron until the user promotes them; "there is no way for your program to perform this promotion automatically" | documented in the settings caption and in the on-box checklist; nothing to code |

### 3.3 The players

- **EarTrumpet** calls `Shell_NotifyIcon` directly "because `System.Windows.Forms.NotifyIcon` does not support …
  `guidItem`", pre-creates its flyout at startup, uses `Shell_NotifyIconGetRect` to know where the icon is (for its
  raw-input scroll-wheel trick) and themes the flyout from the immersive colours. Taken: direct `Shell_NotifyIconW`,
  GUID identity (when signed), `Shell_NotifyIconGetRect` for anchoring anything we ever show near the icon. Not
  taken: the flyout, in 0.3 (§5.2).
- **foobar2000** (core "minimize to tray" preference; `foo_traycontrols` adds playback controls in the tray menu and
  "always minimize to tray") — a **native** context menu with transport verbs and a now-playing line is the
  convention; middle-click play/pause is a common third-party addition. Taken: the menu shape; middle click =
  play/pause.
- **MusicBee** has minimize-to-tray and users ask for close-to-tray; **Spotify desktop** has no tray *setting* — its
  Close and Minimize buttons do the same thing and the tray icon offers "Hide from taskbar when closed", which is
  the single most complained-about behaviour in the idea thread above. Taken: the two are **separate** toggles, the
  Close button quits by default, and the setting caption says exactly what it does.
- **Windows 11's own media flyout** (the volume/media flyout that hardware keys open) already shows Wavee's SMTC
  card with transport and a scrub bar, and third-party "FluentFlyout"-style tools sit on the same SMTC session. That
  is the strongest argument against building a mini-player flyout in 0.3: the OS already draws one for us from the
  data `Os.Smtc` pushes.

---

## 4. Behaviour specification

### 4.1 When the icon exists

`tray.icon.mode` (§8): **0 Always** (default) · 1 Only while the window is hidden · 2 Never.

- **Always** — added at boot, right after the window exists (the same moment `Playback.Os.Activate` runs from the
  first push, `Playback.Os.cs:120`); removed on process exit.
- **Only while hidden** — added on the hide transition, removed on the show transition. Pure rule
  `TrayCore.IconShown(mode, windowVisible)`.
- **Never** — never added; close-to-tray and minimize-to-tray are **forced off** (`TrayCore.EffectiveCloseVerdict`)
  because a hidden Wavee with no icon is a ghost the user can only kill from Task Manager, and the settings row
  says so ("Turned off while the icon is hidden").

### 4.2 Clicks and keys (all on the icon, all delivered on the UI thread)

| Event (`NOTIFYICON_VERSION_4`) | Action | Rule |
|---|---|---|
| `NIN_SELECT` (left click, or Enter on a mouse-selected icon), `NIN_KEYSELECT` (Enter/Space from Win+B) | **Toggle the window**: hidden or minimized → show + restore + foreground; visible and foreground → hide only if `tray.closeToTray` or `tray.minimizeToTray` is on, else bring to foreground | `TrayCore.OnSelect(windowVisible, windowForeground, anyHideModeOn)` |
| `WM_LBUTTONDBLCLK` | Open Wavee (the menu's default) — same as select when hidden; never hides | `TrayCore.OnDoubleClick` |
| `WM_MBUTTONUP` | Play/pause (`Playback.TogglePlay()`); no-op with no track | `TrayCore.OnMiddle(hasTrack)` |
| `WM_CONTEXTMENU` (right click, or the context key / Shift+F10 from Win+B) | the native menu at the anchor (`GET_X/Y_LPARAM(wParam)`) | §5.1 |
| `NIN_POPUPOPEN` / `NIN_POPUPCLOSE` | ignored; `NIF_SHOWTIP` keeps the standard tooltip | — |
| `WM_MOUSEMOVE` | ignored (no hover state) | — |

The select toggle reads the **live** window state, not a cached one — the same rule as the thumb click
(`Playback.Os.cs:494-509`).

### 4.3 Close-to-tray and minimize-to-tray

| Setting | Default | Effect |
|---|---|---|
| `tray.closeToTray` | **false** (Q1) | the caption ✕, Alt+F4 and the system-menu Close **hide** the window instead of quitting; `Quit Wavee` (tray menu, the profile menu's Quit, `wavee://quit`) still quits |
| `tray.minimizeToTray` | **false** | the caption `–`, the system-menu Minimize and Win+Down hide the window instead of iconifying it (the taskbar button disappears) |

The decision is one pure function: `TrayCore.OnCloseRequested(closeToTray, quitRequested, iconMode)` →
`CloseVerdict.Quit | Hide`. `quitRequested` is a latch the explicit quit verbs set before calling
`FluentApp.CloseWindow()`, so a quit is never turned into a hide. Session-end (`WM_QUERYENDSESSION` / the Restart
Manager, `MessagePump.cs:83-86`) is also a quit: the engine seam passes a `reason` and the rule returns `Quit` for
`CloseReason.SessionEnding` regardless of the setting (§7.1 E1).

When hidden: `FluentApp.SetWindowVisible(false)` → `ShowWindow(SW_HIDE)`; the host parks exactly as if minimized
(no reconcile, no layout, no present, `UseIsActive` false — §7.1 E2); playback, the audio pump, the SMTC card,
the jump list and the toasts keep running (they never depended on visibility); the taskbar button goes away with
the window (a hidden top-level window has no button). The icon's tooltip and menu remain the way back, plus a second
launch (§4.7) and any toast click.

### 4.4 Start in the tray

`tray.startHidden` (default false): "Start hidden in the notification area" — **enabled only while
`app.startOnLogin` is on**, greyed otherwise (the setting has no meaning for a launch the user just clicked).
Mechanism:

- Unpackaged: the `Run` value carries `--tray` (`ProtocolRegistrar.RegisterStartup(taskId, exePath)` writes only the
  quoted exe today, `ProtocolRegistrar.cs:147-153` — engine change E6 adds an optional `arguments` parameter).
- Packaged: the manifest `StartupTask` cannot carry arguments and the engine's `ActivationArgs` has no
  `StartupTask` kind (`ActivationArgs.cs:14`). Two options, Christos's call (Q3): (a) add
  `ActivationKind.StartupTask` by reading `AppInstance.GetActivatedEventArgs()` (WinRT, `Windows.ApplicationModel`)
  in the engine — the WASDK precedent the file's own comment names; (b) treat *every* launch as start-hidden while
  `tray.startHidden` is on, which is wrong for a Start-menu click. This plan assumes (a); until it lands, the packaged
  build honours the setting only through the unpackaged-style flag, i.e. not at all, and the row's caption says
  "applies to the sign-in launch".
- The rule: `TrayCore.StartHidden(setting, activationKind, hasTrayArg)` → true only for `StartupTask` or `--tray`.
  `Shell.Run` then passes `StartHidden = true` in `AppOptions` (E3) and the icon is added before the first frame.

### 4.5 Quit

`Quit Wavee` in the menu → `Tray.Host.Quit()` → `s_quitRequested = true; FluentApp.CloseWindow()` → the normal exit
tail (`Shell.Host.cs:195-205`) plus `Tray.Host.Shutdown()` (`NIM_DELETE`) **before** `Playback.Os.Shutdown()` — the
icon must never outlive the process by a frame (a stale icon that vanishes on hover is the classic tray bug, and
`NIM_DELETE` on exit is what prevents it). `AppDomain.ProcessExit` gets a best-effort `NIM_DELETE` too (crash path).

### 4.6 Sign-out

`Shell.Auth` → `SignInRequired` (`Shell.cs:1347`): the icon switches to the **offline** glyph, the tooltip becomes
`Wavee — Signed out`, the menu collapses to `Open Wavee · ─ · Hide icon · Quit Wavee` (the transport rows are removed,
not greyed — there is nothing to transport). `Playback.Os.SignedOut()` / `Notify.SignedOut()` (`Playback.Os.cs:102-109`,
`Notify.Host.cs:86-95`) gain a sibling `Tray.Host.SignedOut()` called from the same place. `Offline` (credential
stored, session down) uses the same glyph with tooltip `Wavee — Offline`; `Connecting` keeps the normal glyph.

### 4.7 Single instance, deep links, toasts

- A second launch redirects through `WM_COPYDATA` to the main HWND; `FindWindowW` finds a **hidden** window, so
  nothing changes in `SingleInstanceGate`. The receiver already calls `SetForegroundWindow` (`Win32Platform.cs:2047`)
  — on a hidden window that is a no-op, so `Shell.OnActivationRedirected` must un-hide first.
- **But not for every verb.** Today `OnActivationRedirected` calls `WakeWindow()` unconditionally
  (`Shell.Host.cs:300-304`); a jump-list `wavee://pause` therefore also raises the window, which is wrong even
  without a tray and becomes glaring when the window was deliberately hidden. New rule
  `TrayCore.WakeFor(DeepLinkKind)`: `Open`, `Play`, `Report`, and an empty payload (bare relaunch) → wake;
  `Resume`, `Pause`, `Quit` → do not. `Shell.WakeWindow` becomes `Tray.Host.ShowWindow()` (restore + foreground,
  through the engine seam so the host un-parks).
- A toast click arrives through the same door (`Notify.HostInstall`'s `deepLink` hop, `Notify.Host.cs:47-84`) and
  follows the same rule.
- New verb `wavee://quit` (the jump list does not use it; the headless plan may — §10).

### 4.8 Explorer restarts, session end, crashes

- `TaskbarCreated` on the icon's own window → `NIM_ADD` + `NIM_SETVERSION` again, then one full `NIM_MODIFY` from
  the last-pushed facts (icon + tip), then re-read the taskbar DPI and theme (both may have changed while Explorer
  was down).
- `WM_QUERYENDSESSION` on the main window → `CloseReason.SessionEnding` → quit (never hide).
- A crash: `ProcessExit` best-effort `NIM_DELETE`; if the process is killed, the shell removes the icon on the next
  hover — nothing to do.

---

## 5. Menu, flyout and tooltip

### 5.1 The context menu (native, v1)

```
 ┌──────────────────────────────────────┐
 │ Midnight City — M83                  │  ← disabled row (MF_GRAYED): the now-playing line, 60-char ellipsis
 ├──────────────────────────────────────┤
 │ Pause                                │  ← label flips Pause/Play; greyed with no track
 │ Next                                 │  ← greyed when !CanSkipNext
 │ Previous                             │  ← greyed when !CanSkipPrev
 │ ♥ Save to Liked Songs                │  ← check mark when saved; row absent when the like seam is unattached
 ├──────────────────────────────────────┤
 │ Devices…                             │  ← opens the window and asks for the picker
 │ **Open Wavee**                       │  ← DEFAULT (bold) — becomes "Hide Wavee" while visible and a hide mode is on
 ├──────────────────────────────────────┤
 │ Hide icon                            │  ← writes tray.icon.mode = Never after the confirm caption in Settings
 │ Quit Wavee                           │
 └──────────────────────────────────────┘
```

Signed out (§4.6):

```
 ┌──────────────────────────────────────┐
 │ Signed out                           │  ← disabled
 ├──────────────────────────────────────┤
 │ **Open Wavee**                       │
 ├──────────────────────────────────────┤
 │ Hide icon                            │
 │ Quit Wavee                           │
 └──────────────────────────────────────┘
```

Why native (`TrackPopupMenuEx`) and not an engine flyout in 0.3:

1. **Ownership.** `Win32PopupWindow` is an *owned* `WS_POPUP` (`Win32PopupWindow.cs:15`: "hidden when the owner
   minimizes") and its content is a subtree of the main window's scene, rendered by the host's popup lease
   (`AppHost.cs:4432-4459`). With the main window **hidden**, an owned popup is hidden with it and there is no
   frame loop running to paint it. An engine tray flyout needs an *unowned* popup host with its own reconciler
   root — new engine surface, not a reuse.
2. **Accessibility for free.** A native menu is keyboard-navigable, Narrator-read, high-contrast-correct and honours
   `NIM_SETFOCUS` conventions with no work; the engine's menu would have to re-earn all four outside the main window.
3. **Cost.** Native: ~120 lines in the engine host (`ShowMenu`) + ~60 in `Tray.Host.cs`. Engine flyout: §5.2.

The one wart: a Win32 popup menu is drawn light unless the process opts into the shell's dark menu theme
(`uxtheme.dll` ordinal 135 `SetPreferredAppMode(AllowDark)` + ordinal 136 `FlushMenuThemes` — **undocumented**, used
by Explorer itself and by every tray app that has a dark menu). Recommendation: call both **fail-soft** inside the
engine host when the taskbar is dark, guarded by `GetProcAddress` by ordinal, logged once if absent; if Christos
prefers not to touch undocumented ordinals (Q4), the menu is light on a dark taskbar — exactly what most Win32 tray
apps look like today, and not a blocker.

Menu mechanics that are load-bearing (the KB Q135788 rule): call `SetForegroundWindow(callbackHwnd)` **before**
`TrackPopupMenuEx`, and `PostMessage(callbackHwnd, WM_NULL)` **after**, or the menu does not dismiss when the user
clicks elsewhere. `TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN` at the anchor; on `0` (dismissed/ESC) send
`NIM_SETFOCUS`.

### 5.2 A mini-player flyout on the engine — costed, deferred

What it would be: an unowned `WS_POPUP | WS_EX_TOOLWINDOW` window near the icon (`Shell_NotifyIconGetRect` +
`GetWorkArea`) hosting a small component tree — cover, title/artist, ⏮ ⏯ ⏭, a scrub bar, the device row — with
acrylic and light-dismiss.

| Piece | Where | Estimate |
|---|---|---|
| `IPlatformApp.CreateDetachedPopup` (unowned, non-activating, light-dismiss via `WM_ACTIVATEAPP`/`WM_KILLFOCUS`), `AppHost.OpenDetachedHost` reusing the pop-out video path (`AppHost.cs:1529-1621`, `TickDetachedHosts` `:1621`) but with an **engine** root instead of a video surface | engine | 600–900 lines + VerticalSlice gates |
| Keyboard focus and UIA for a second root (the engine's focus manager is single-window today) | engine | 300+ lines, the risky part |
| The flyout component (~ the NPV hero + player bar's centre cluster, re-skinned at 360×140) | app | 400–600 lines |
| Theming from the taskbar, DPI of the taskbar monitor, placement above/below/left/right per taskbar edge | app | 150 |

**≈ 1,500–2,000 lines, with the focus/UIA work the unknown.** Against it: the Windows 11 media flyout already shows
the same content from the SMTC session (§3.3), the taskbar thumbnail toolbar already gives one-hover transport, and
the tray's job in 0.3 is *presence while hidden*. Recommendation: **defer to 0.4**, keep the engine seam
(`NotifyIcon.TryGetRect`) so the flyout can anchor later, and record it as a §11 question so the deferral is a
decision, not an omission.

### 5.3 The tooltip

Format follows the guideline's `(Company) Feature — Status summary` (§3.2) and the 127-char `szTip` cap:

| Facts | Tooltip |
|---|---|
| no track | `Wavee` |
| playing | `Wavee — Midnight City · M83` |
| paused | `Wavee — Paused: Midnight City · M83` |
| buffering / loading | same as playing (no "…" churn — the guideline's "don't change rapidly") |
| remote device owns playback | `Wavee — Midnight City · M83 · on Kitchen` (device name from `Playback.Devices.NameOf`) |
| live stream | `Wavee — <stream title> · LIVE` |
| `AuthState.Offline` | `Wavee — Offline` |
| `AuthState.SignInRequired` | `Wavee — Signed out` |
| update available (any of the above) | suffix ` · Update ready` |

Truncation: title first, then artist, each to a floor of 12 chars before the ellipsis (`…`), the prefix and suffix
never cut; the pure function `TrayCore.Tooltip(...)` returns exactly ≤ 127 chars and the test asserts the length on a
200-char title. **Throttling:** the tooltip is recomputed on every `Publish`, but `NIM_MODIFY` is sent only when the
string differs ordinally from the last one pushed — an identity or phase edge, never a position tick (position is
not in the tooltip by design; `Os.Publish` fires on `Smtc` *and* `SmtcTimeline` effects, `Playback.Host.cs:408`, so
the dedupe is what keeps the tray at a handful of shell calls per track).

---

## 6. Icon states and artwork

### 6.1 The mark

`assets/AppIcon/appicon-source.png` is a navy rounded square carrying a cyan-to-violet **"W" ribbon** whose centre
stroke loops into a wave (the WaveeMusic W-ribbon, `generate-appicon.ps1:1`). The tray glyph is that ribbon's
silhouette, single-colour, no plate — the two outer strokes and the looped centre — drawn as a stroke path so it
survives 16 px.

### 6.2 States (three glyphs, not five)

| Facts | Glyph | Why |
|---|---|---|
| signed in (Live / Connecting), any transport state | **normal** | playing vs paused is *not* in the glyph — it flips on every skip (guideline: "don't change status too frequently"); it lives in the tooltip, the menu label and the taskbar overlay (ch 14 W7-W9) |
| `AuthState.Offline` or `SignInRequired` | **offline** — the ribbon with a small "blocked" cut-out disc at bottom-right (the guideline's Blocked/Offline overlay position) | a signed-out Wavee in the tray must read as "not doing anything" |
| update available / downloaded (`AppUpdateState.Available`, `Downloaded`, `Completed` from `Notify`'s update snapshot) | **update** — the ribbon with a filled dot at top-right | the one status a hidden Wavee has that the user cannot otherwise see; muted (glyph colour, not accent) |

Priority when two apply: offline wins over update (you cannot update usefully while signed out is untrue, but the
offline fact is the one that explains why nothing plays).

Two taskbar variants each: **on-dark** (white `#FFFFFF` glyph, for a dark taskbar) and **on-light** (near-black
`#1B1B1B`, for a light taskbar). Six `.ico` files, six frames each.

### 6.3 Sketches (16 px master grid; `#` = glyph pixel, `+` = anti-aliased half, `.` = transparent)

Normal — the W ribbon, 2-px strokes at 16, the centre loop one row lower than the outer arms:

```
 16×16 normal                16×16 offline                16×16 update
 . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . . . .
 . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . # # .
 . # # . . . . . . . . . . # # .   . # # . . . . . . . . . . # # .   . # # . . . . . . . . . # # # #
 . # # . . . . . . . . . . # # .   . # # . . . . . . . . . . # # .   . # # . . . . . . . . . # # # #
 . + # # . . . . + + . . # # + .   . + # # . . . . + + . . # # + .   . + # # . . . . + + . . # # # .
 . . # # . . . # # # # . # # . .   . . # # . . . # # # # . # # . .   . . # # . . . # # # # . # # . .
 . . # # . . # # . . # # # # . .   . . # # . . # # . . # # # # . .   . . # # . . # # . . # # # # . .
 . . + # # . # # . . . # # + . .   . . + # # . # # . . . # # + . .   . . + # # . # # . . . # # + . .
 . . . # # # # . . . . # # . . .   . . . # # # # . . . . # # . . .   . . . # # # # . . . . # # . . .
 . . . # # # + . . . . # # . . .   . . . # # # + . . . . # # . . .   . . . # # # + . . . . # # . . .
 . . . + # # . . . . . + # . . .   . . . + # # . . . . + + + + . .   . . . + # # . . . . . + # . . .
 . . . . # . . . . . . . # . . .   . . . . # . . . . + . . . . + .   . . . . # . . . . . . . # . . .
 . . . . . . . . . . . . . . . .   . . . . . . . . . + . # # . + .   . . . . . . . . . . . . . . . .
 . . . . . . . . . . . . . . . .   . . . . . . . . . + . . . . + .   . . . . . . . . . . . . . . . .
 . . . . . . . . . . . . . . . .   . . . . . . . . . . + + + + . .   . . . . . . . . . . . . . . . .
 . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . . . .   . . . . . . . . . . . . . . . .
```

At 24 px and above the loop is drawn as a true curve (the stroke path below); at 16 and 20 the hand-tuned
pixel masks above are used verbatim — a downsampled curve reads as a smudge at 16 px, which is the reason every
system tray icon on Windows ships a hand-hinted 16.

The vector master (`assets/tray/src/wavee-tray.svg`, 24-unit box, stroke 3, round caps/joins — the path the
generator rasterises at 24/32/40/48):

```svg
<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
  <!-- left arm, the loop, right arm: one open path so the ribbon reads as one stroke -->
  <path d="M4 4 L8 19 C9 21 11.5 21 12 18 L12 12 C12.5 9 15 9 16 12 L20 4"
        fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
```

Offline overlay: an open circle Ø 8 at (18,18) with the ribbon **cleared** under it (a 2-unit gutter) so the overlay
never merges with the right arm. Update badge: a filled disc Ø 6 at (19,5), same gutter.

### 6.4 How the `.ico` files are produced

A sibling of `generate-appicon.ps1`: `assets/tray/generate-tray-icons.ps1`, run by hand and committed (no build
step, no runtime rasteriser — the 0.2.9 rule for `appicon.ico`).

```powershell
# assets/tray/generate-tray-icons.ps1 — six .ico files × six frames from the SVG path + the two hand-hinted masks.
# Run: powershell -ExecutionPolicy Bypass -File src/apps/Wavee/assets/tray/generate-tray-icons.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sizes = 16,20,24,32,40,48
$variants = @{ 'on-dark' = [System.Drawing.Color]::White; 'on-light' = [System.Drawing.Color]::FromArgb(255,27,27,27) }
$states  = 'normal','offline','update'

# The 24-unit master path (keep in sync with src/wavee-tray.svg — the SVG is documentation, THIS is the source of the pixels).
function New-RibbonPath([float]$s) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddLine(4*$s, 4*$s, 8*$s, 19*$s)
  $p.AddBezier(8*$s,19*$s, 9*$s,21*$s, 11.5*$s,21*$s, 12*$s,18*$s)
  $p.AddLine(12*$s, 18*$s, 12*$s, 12*$s)
  $p.AddBezier(12*$s,12*$s, 12.5*$s,9*$s, 15*$s,9*$s, 16*$s,12*$s)
  $p.AddLine(16*$s, 12*$s, 20*$s, 4*$s)
  return $p
}

function New-Frame([int]$size, [string]$state, [System.Drawing.Color]$ink) {
  $bmp = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  if ($size -le 20) { Paint-Mask $bmp $state $ink; return $bmp }          # hand-hinted masks (tray-mask-16.txt / -20.txt)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $s = $size / 24.0
  $pen = New-Object System.Drawing.Pen($ink, 3*$s)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = $pen.StartCap
  $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $g.DrawPath($pen, (New-RibbonPath $s))
  if ($state -ne 'normal') {
    # clear the gutter first so the overlay never touches the right arm
    $clear = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Transparent)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    if ($state -eq 'offline') { $g.FillEllipse($clear, 13*$s, 13*$s, 10*$s, 10*$s) } else { $g.FillEllipse($clear, 15*$s, 1*$s, 8*$s, 8*$s) }
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    if ($state -eq 'offline') { $ring = New-Object System.Drawing.Pen($ink, 2*$s); $g.DrawEllipse($ring, 14.5*$s, 14.5*$s, 7*$s, 7*$s) }
    else { $g.FillEllipse((New-Object System.Drawing.SolidBrush($ink)), 16*$s, 2*$s, 6*$s, 6*$s) }
  }
  $g.Dispose(); return $bmp
}

function Write-Ico([string]$path, [hashtable]$png) {
  $fs=[System.IO.File]::Create($path); $bw=New-Object System.IO.BinaryWriter($fs)
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  foreach($s in $sizes){ $len=$png[$s].Length
    $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset+=$len }
  foreach($s in $sizes){ $bw.Write($png[$s]) }
  $bw.Flush(); $fs.Close()
}

foreach ($v in $variants.Keys) { foreach ($st in $states) {
  $png=@{}
  foreach ($sz in $sizes) { $b = New-Frame $sz $st $variants[$v]; $ms = New-Object System.IO.MemoryStream
    $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $png[$sz]=$ms.ToArray(); $ms.Dispose(); $b.Dispose() }
  Write-Ico (Join-Path $here "wavee-$v-$st.ico") $png
}}
```

(`Paint-Mask` reads `assets/tray/src/tray-mask-{16,20}-{normal,offline,update}.txt`, the ASCII grids of §6.3 with
`#` = 255 and `+` = 128 alpha, so the hinted frames are text a reviewer can diff.) The `.csproj` copies
`assets/tray/*.ico` beside `assets/taskbar/*.ico` (the same `Content` glob).

### 6.5 Which frame the shell gets

`LoadImageW(path, IMAGE_ICON, cx, cy, LR_LOADFROMFILE)` with `cx = cy = TrayCore.IconSizeFor(dpi)` where `dpi` is
the **taskbar monitor's** effective DPI (`GetDpiForMonitor(MonitorFromPoint((0,0)), MDT_EFFECTIVE_DPI)` — the
Windows 11 tray lives on the primary taskbar), re-read on `TaskbarCreated` and `WM_DISPLAYCHANGE`. `IconSizeFor`
is `round(16 × dpi / 96)`: 96→16, 120→20, 144→24, 192→32, 240→40, 288→48. Passing an explicit size (not
`LR_DEFAULTSIZE`, which is what the taskbar-overlay loader uses at `SM_CXICON`, `TaskbarManager.cs:88-91`) picks the
matching frame from the `ICONDIR` and avoids the "Windows 11 always takes a bigger PNG" resampling.

---

## 7. Architecture

### 7.1 Engine changes (`C:\WAVEE\fluent-gpu-base`, made and gated there — Debug + Release build, VerticalSlice)

| # | Change | Files | Lines |
|---|---|---|---|
| **E1** | `IPlatformWindow.CloseRequested` — a `Func<CloseReason, bool>?` property (`CloseReason { User, SessionEnding }`); `Win32Window.Handle32`'s `WM_CLOSE` case asks it first and, on `true`, consumes the message without `DestroyWindow`; `WM_QUERYENDSESSION` passes `SessionEnding` (returns TRUE regardless — the pump's rule, `MessagePump.cs:248-252`). Relayed as `FluentApp.CloseRequested` | `Pal.cs` (+8), `Win32Platform.cs:1782` (+6), `FluentApp.cs` (+10) | 24 |
| **E2** | `IPlatformWindow.Hide()` + `bool IsVisible` (default true); `Win32Window.Hide` = `ShowWindow(SW_HIDE)`; `AppHost.IsParked => IsMinimized || !_window.IsVisible` replaces the four `IsMinimized` reads (`AppHost.cs:2041, 2074, 3154-3201, 3289`); the show edge forces one frame like the restore edge (`:3155`). Relayed as `FluentApp.SetWindowVisible(bool)` / `FluentApp.WindowVisible` | `Pal.cs` (+6), `Win32Platform.cs` (+6), `AppHost.cs` (~12 edits), `FluentApp.cs` (+14) | ~50 |
| **E3** | `AppOptions.StartHidden` — skip `window.Show()` (`FluentApp.cs:360`) and start parked | `FluentApp.cs` | 6 |
| **E4** | `FluentApp.WindowStateChanged` relay (`InputKind.WindowStateChanged` already exists, `Pal.cs:51`; the host consumes it for the caption glyph) — the minimize-to-tray edge | `AppHost.cs`, `FluentApp.cs` | 15 |
| **E5** | `Win32Theme.TaskbarUsesLightTheme()` reading `Personalize\SystemUsesLightTheme` beside `AppsUseLightTheme` (`Win32Theme.cs:77-87`); `FluentApp.TaskbarUsesLightTheme()` | `Win32Theme.cs`, `FluentApp.cs` | 14 |
| **E6** | `ProtocolRegistrar.RegisterStartup(taskId, exePath, string? arguments = null)` | `ProtocolRegistrar.cs:147-153` | 4 |
| **E7** | **`FluentGpu.WindowsApi.Shell.NotifyIcon`** — the reusable host (below) | new `FluentGpu.WindowsApi/Shell/NotifyIcon.cs` | ~420 |
| **E8** | `ActivationKind.StartupTask` via `AppInstance.GetActivatedEventArgs()` (WinRT) — **only if Q3 = (a)** | `ActivationArgs.cs` | ~60 |

Why the icon host belongs in the engine and not in `Tray.Host.cs`: it is 100 % Win32 (`Shell_NotifyIconW`,
`TrackPopupMenuEx`, `RegisterWindowMessageW`, `LoadImageW`, `GetDpiForMonitor`, a `WNDCLASSEXW` with an
`[UnmanagedCallersOnly]` procedure), it has to be AOT-clean with TerraFX and the `LibraryImport` generator the engine
already configures, its correctness (the `TaskbarCreated` re-add, the version-4 decoding, the menu foreground
dance) is app-independent, and `FluentGpu.WindowsApi/Shell/` already holds its two siblings (`TaskbarManager.cs`,
`JumpList.cs`) with the same "flat call-out COM, no ComWrappers, fail-soft" posture (`TaskbarManager.cs:29-50`). The
gallery gets a Windows-APIs page row for it, which is also its manual test.

```csharp
// fluent-gpu-base/src/FluentGpu.WindowsApi/Shell/NotifyIcon.cs  (E7 — shape; the bodies follow TaskbarManager's style)
namespace FluentGpu.WindowsApi.Shell;

/// <summary>What the user did to the icon. Delivered on the thread that created the <see cref="NotifyIcon"/>
/// (the UI thread — its hidden callback window belongs to that thread's message queue).</summary>
public enum NotifyIconEvent : byte
{
    Select,          // NIN_SELECT: left click / Enter on a mouse-selected icon
    KeySelect,       // NIN_KEYSELECT: Enter or Space from Win+B keyboard selection
    DoubleClick,     // WM_LBUTTONDBLCLK
    MiddleClick,     // WM_MBUTTONUP
    ContextMenu,     // WM_CONTEXTMENU: right click, the context key, Shift+F10
    Recreated,       // explorer restarted — the host has already re-added the icon; refresh what you pushed
    ShellChanged,    // taskbar DPI or light/dark changed — reload your icon file
}

/// <summary>One row of a native context menu. <paramref name="Id"/> 0 is a separator; a negative id is disabled.</summary>
public readonly record struct NotifyMenuItem(int Id, string Text, bool Enabled = true, bool Checked = false, bool Default = false);

/// <summary>A notification-area icon over <c>Shell_NotifyIconW</c> (NOTIFYICON_VERSION_4). Owns a hidden
/// TOP-LEVEL callback window (a message-only window would miss the <c>TaskbarCreated</c> broadcast — the same
/// reason <c>MessagePump</c>'s sentinel is top-level), re-adds itself after an explorer restart, and decodes the
/// version-4 callback into <see cref="NotifyIconEvent"/>s. Fail-soft: a shell that refuses the icon leaves
/// <see cref="IsShown"/> false and every method a no-op.</summary>
[SupportedOSPlatform("windows6.1")]
public sealed unsafe partial class NotifyIcon : IDisposable
{
    public const int MaxTipChars = 127;                       // szTip[128] incl. the terminator

    /// <param name="identity">Icon identity. A non-empty GUID uses NIF_GUID (the shell then keys the user's
    /// show/hide preference on GUID + exe path — only safe for a signed, stable install path); Guid.Empty uses
    /// hwnd+uID. See NOTIFYICONDATAW › Troubleshooting for the exe-path rule.</param>
    public NotifyIcon(Guid identity, string tip);

    public bool IsShown { get; }
    public event Action<NotifyIconEvent, int, int>? Activated;      // (event, anchorX, anchorY) screen px

    /// <summary>NIM_ADD + NIM_SETVERSION(4) + NIF_SHOWTIP. Idempotent.</summary>
    public bool Show();
    /// <summary>NIM_DELETE. Idempotent; also what Dispose does.</summary>
    public void Hide();

    /// <summary>Load <paramref name="icoPath"/> at <paramref name="sizePx"/> (LoadImageW, explicit size — picks the
    /// matching ICONDIR frame) and NIM_MODIFY(NIF_ICON). The previous HICON is destroyed after the call returns.</summary>
    public bool SetIcon(string icoPath, int sizePx);
    /// <summary>NIM_MODIFY(NIF_TIP | NIF_SHOWTIP); truncated to <see cref="MaxTipChars"/>.</summary>
    public bool SetTip(string tip);

    /// <summary>The icon's screen rect (Shell_NotifyIconGetRect), for anchoring a flyout later.</summary>
    public bool TryGetRect(out int x, out int y, out int w, out int h);
    /// <summary>The taskbar monitor's effective DPI (MonitorFromPoint(0,0) + GetDpiForMonitor).</summary>
    public uint TaskbarDpi { get; }

    /// <summary>Show a native menu at the anchor and return the chosen id (0 = dismissed). Does the KB Q135788
    /// dance (SetForegroundWindow before, WM_NULL after) and sends NIM_SETFOCUS on dismissal.</summary>
    public int ShowMenu(ReadOnlySpan<NotifyMenuItem> items, int anchorX, int anchorY);

    public void Dispose();
}
```

Inside: `WM_APP + 0x100` as the callback message; `RegisterWindowMessageW("TaskbarCreated")` compared in the
procedure (the engine already registers it for the main window — `Win32Platform.cs:702-703`; the host registers its
own copy because `s_taskbarButtonCreatedMsg` is `private static` in another assembly) plus
`ChangeWindowMessageFilterEx(MSGFLT_ALLOW)` on the callback window (`:740-742`); `WM_SETTINGCHANGE("ImmersiveColorSet")`
and `WM_DISPLAYCHANGE` → `ShellChanged`; `WM_QUERYENDSESSION` → TRUE. The optional dark-menu opt-in
(`SetPreferredAppMode`/`FlushMenuThemes` by ordinal, Q4) lives in `ShowMenu` behind one `GetProcAddress` probe.

### 7.2 App files (the worktree)

| File | Role | Owner | Lines | What |
|---|---|---|---|---|
| `Platform/Tray.cs` | **CORE** | U (new) | ~380 | `TrayCore`: every decision in §4-§6 as pure functions; `TrayFacts`, `TrayGlyph`, `TrayTheme`, `CloseVerdict`, `TrayMenu` rows; no engine types |
| `Platform/Tray.Host.cs` | **SHELL** | U | ~520 | the `NotifyIcon` binding, the push sink, the close/minimize hooks, the menu dispatch, the settings reads, the sign-out/shutdown tails; UI thread only |
| `Wavee.Tests/TrayTests.cs` | tests | U | ~350 | §9 |
| `assets/tray/*` | assets | U | — | 6 `.ico`, the SVG, the two mask sets, the generator |
| `Platform/Platform.cs` | CORE | S (edit) | +5 | four keys (§8) |
| `Playback/Playback.Os.cs:121-127` | SHELL | H (edit) | +1 | `Tray.Host.Publish(in s);` after `PowerPolicy.OnStateChanged` — the fifth sink |
| `Shell/Shell.Host.cs` | SHELL | I (edit) | +~30 | `StartHidden` in `AppOptions`; `FluentApp.CloseRequested = Tray.Host.OnCloseRequested` before `Run`; `WakeWindow` → `Tray.Host.ShowWindow` + `TrayCore.WakeFor`; `DeepLinkKind.Quit`; the exit tail's `Tray.Host.Shutdown()` |
| `Shell/Shell.cs` | CORE | I (edit) | +~10 | `DeepLinkKind.Quit` + `wavee://quit` in the parser |
| `Screens/Settings.cs` + `Settings.UI.cs` | UI | R (edit) | +~90 | the General-tab "Notification area" group (§8) |
| `assets/loc/en-US.json` | loc | U | +~20 | `tray.*` strings |

**Budget rule (plan §2, P1):** `Playback.Os.cs` is at 830 of 840 and its header lists exactly four surfaces; the
tray is a fifth surface with its own window, its own settings and its own lifecycle hooks, so it is a **named pair of
files**, not a partial of `Playback.Os.cs`, and it sits in `Platform/` (beside `Notify.*`) because half of what it
does — close, minimize, start hidden, quit — is window lifecycle, not playback. The CORE/SHELL split follows the
`Notify.cs`/`Notify.Host.cs` precedent: `Tray.cs` decides, `Tray.Host.cs` calls the OS.

### 7.3 `Platform/Tray.cs` (CORE)

```csharp
// ── Platform/Tray.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// The notification-area icon's DECISIONS: when it exists, what glyph it shows, what the tooltip says, what a click
// does, what a close does, what the menu contains. Pure — no engine type, no OS call, no clock — so every rule in the
// tray plan (docs/plans/wavee/wavee-0.3-tray-implementation.md §4-§6) is a table in Wavee.Tests/TrayTests.cs.
//
// Role: CORE
// Owner: U
// Wave: 6 (lands independently of the UI waves; nothing here renders)
// Budget: 380 lines
// Spec: wavee-0.3-tray-implementation.md

namespace Wavee;

public static partial class Tray
{
    /// <summary>`tray.icon.mode`.</summary>
    public enum IconMode : byte { Always = 0, WhileHidden = 1, Never = 2 }

    /// <summary>Which .ico family. Three, deliberately: playing/paused is NOT a glyph state (plan §6.2).</summary>
    public enum Glyph : byte { Normal, Offline, Update }

    /// <summary>The taskbar's colour, which is `SystemUsesLightTheme` — NOT the app theme.</summary>
    public enum Theme : byte { OnDark, OnLight }

    public enum CloseVerdict : byte { Quit, Hide }
    public enum CloseReason : byte { User, SessionEnding }
    public enum SelectAction : byte { ShowWindow, HideWindow, Foreground }

    /// <summary>Everything the icon is derived from, as one value the host snapshots per push.</summary>
    public readonly record struct Facts(
        bool HasTrack, bool Playing, bool Buffering, bool IsLive, bool RemoteOwner,
        string Title, string Artist, string DeviceName,
        Shell.AuthState Auth, bool UpdateReady,
        bool CanSkipPrev, bool CanSkipNext, bool? Saved);

    // ── 1. presence ─────────────────────────────────────────────────────────────────────────────────────────────

    public static bool IconShown(IconMode mode, bool windowVisible) => mode switch
    {
        IconMode.Always => true,
        IconMode.WhileHidden => !windowVisible,
        _ => false,
    };

    /// <summary>GUID identity only for a signed, stable install path (NOTIFYICONDATAW › Troubleshooting #2): the
    /// packaged build is Trusted-Signing-signed and Windows carries the registration across the per-version
    /// WindowsApps path; a dev tree is neither, and NIM_ADD would fail on the second exe path.</summary>
    public static bool UseGuidIdentity(bool isPackaged) => isPackaged;

    // ── 2. close, minimize, start ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A hide mode is meaningless without an icon to come back through: Never forces both off.</summary>
    public static bool HideModesAllowed(IconMode mode) => mode != IconMode.Never;

    public static CloseVerdict OnCloseRequested(bool closeToTray, bool quitRequested, IconMode mode, CloseReason reason)
    {
        if (quitRequested || reason == CloseReason.SessionEnding) return CloseVerdict.Quit;
        return closeToTray && HideModesAllowed(mode) ? CloseVerdict.Hide : CloseVerdict.Quit;
    }

    public static bool OnMinimized(bool minimizeToTray, IconMode mode) => minimizeToTray && HideModesAllowed(mode);

    /// <summary>Start hidden ONLY for a launch the user did not click: the packaged StartupTask activation or the
    /// unpackaged Run value's `--tray`. A Start-menu click with the setting on still shows the window.</summary>
    public static bool StartHidden(bool setting, bool isStartupActivation, bool hasTrayArg)
        => setting && (isStartupActivation || hasTrayArg);

    // ── 3. clicks ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Left click / Enter / Space. Hidden or minimized → show. Visible but not in front → front. In front
    /// → hide, but only when the user has opted into a hide mode; otherwise a click on a visible, focused Wavee
    /// does the least surprising thing, which is nothing beyond keeping it in front.</summary>
    public static SelectAction OnSelect(bool windowVisible, bool windowForeground, bool anyHideModeOn)
    {
        if (!windowVisible) return SelectAction.ShowWindow;
        if (!windowForeground) return SelectAction.Foreground;
        return anyHideModeOn ? SelectAction.HideWindow : SelectAction.Foreground;
    }

    public static SelectAction OnDoubleClick(bool windowVisible) => windowVisible ? SelectAction.Foreground : SelectAction.ShowWindow;

    public static bool MiddleTogglesPlay(bool hasTrack) => hasTrack;

    /// <summary>Which redirected deep links raise the window. A transport verb from the jump list must not.</summary>
    public static bool WakeFor(Shell.DeepLinkKind kind) => kind switch
    {
        Shell.DeepLinkKind.Resume or Shell.DeepLinkKind.Pause or Shell.DeepLinkKind.Quit => false,
        _ => true,   // Open, Play, Report, and the empty payload of a bare second launch (Kind == None)
    };

    // ── 4. the glyph ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Offline beats Update: the fact that explains why nothing plays wins.</summary>
    public static Glyph GlyphFor(Shell.AuthState auth, bool updateReady)
        => auth is Shell.AuthState.Offline or Shell.AuthState.SignInRequired ? Glyph.Offline
         : updateReady ? Glyph.Update
         : Glyph.Normal;

    public static Theme ThemeFor(bool taskbarUsesLightTheme) => taskbarUsesLightTheme ? Theme.OnLight : Theme.OnDark;

    /// <summary>`assets/tray/wavee-{on-dark|on-light}-{normal|offline|update}.ico`.</summary>
    public static string IconFileName(Glyph glyph, Theme theme)
        => "wavee-" + (theme == Theme.OnLight ? "on-light" : "on-dark") + "-" + glyph switch
        {
            Glyph.Offline => "offline",
            Glyph.Update => "update",
            _ => "normal",
        } + ".ico";

    /// <summary>SM_CXSMICON at the taskbar's DPI: 16 @ 96, 20 @ 120, 24 @ 144, 32 @ 192, 40 @ 240, 48 @ 288.</summary>
    public static int IconSizeFor(uint dpi) => dpi == 0 ? 16 : (int)Math.Round(16.0 * dpi / 96.0);

    // ── 5. the tooltip ──────────────────────────────────────────────────────────────────────────────────────────

    public const int TipMax = 127;          // szTip[128] including the terminator
    const int TipPartFloor = 12;            // never cut a part below this before dropping it
    const string Prefix = "Wavee";

    /// <summary>≤ TipMax chars, always. Prefix and suffix are never cut; the title is trimmed first, then the artist.
    /// Position is NOT in the string by design, which is what keeps NIM_MODIFY at one per edge.</summary>
    public static string Tooltip(in Facts f)
    {
        string suffix = f.UpdateReady ? " · " + Loc.Get(Strings.Tray.UpdateReady) : "";
        if (f.Auth == Shell.AuthState.SignInRequired) return Prefix + " — " + Loc.Get(Strings.Tray.SignedOut) + suffix;
        if (f.Auth == Shell.AuthState.Offline) return Prefix + " — " + Loc.Get(Strings.Tray.Offline) + suffix;
        if (!f.HasTrack) return Prefix + suffix;

        string lead = f.Playing || f.Buffering ? Prefix + " — " : Prefix + " — " + Loc.Get(Strings.Tray.Paused) + ": ";
        string tail = (f.IsLive ? " · " + Loc.Get(Strings.Tray.Live) : "")
                    + (f.RemoteOwner && f.DeviceName.Length > 0 ? " · " + Strings.Tray.OnDevice(f.DeviceName) : "")
                    + suffix;
        int room = TipMax - lead.Length - tail.Length;
        string body = Fit(f.Title, f.Artist, room);
        return lead + body + tail;
    }

    /// <summary>`title · artist` into <paramref name="room"/> chars: cut the title first, then the artist, each to
    /// a 12-char floor, then drop the artist entirely, then cut the title alone.</summary>
    public static string Fit(string title, string artist, int room)
    {
        if (room <= 0) return "";
        if (artist.Length == 0) return Cut(title, room);
        const string sep = " · ";
        if (title.Length + sep.Length + artist.Length <= room) return title + sep + artist;
        int forTitle = Math.Max(TipPartFloor, room - sep.Length - artist.Length);
        string t = Cut(title, forTitle);
        int forArtist = room - t.Length - sep.Length;
        if (forArtist >= TipPartFloor) return t + sep + Cut(artist, forArtist);
        return Cut(title, room);
    }

    static string Cut(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        return max == 1 ? "…" : s.AsSpan(0, max - 1).TrimEnd().ToString() + "…";
    }

    // ── 6. the menu ─────────────────────────────────────────────────────────────────────────────────────────────

    public const int IdNowPlaying = -1, IdSeparator = 0, IdPlayPause = 1, IdNext = 2, IdPrev = 3, IdLike = 4,
                     IdDevices = 5, IdOpen = 6, IdHideIcon = 7, IdQuit = 8;

    public readonly record struct MenuRow(int Id, string Text, bool Enabled = true, bool Checked = false, bool Default = false);

    /// <summary>The rows, top to bottom. Signed out collapses to Open / Hide icon / Quit — transport rows are
    /// REMOVED, not greyed (the guideline's rule for features that do not apply). Rows are greyed for transport
    /// enablement, matching the thumbnail toolbar (ch 14 W11).</summary>
    public static int Menu(in Facts f, bool windowVisible, bool anyHideModeOn, Span<MenuRow> into)
    {
        int n = 0;
        bool signedIn = f.Auth is Shell.AuthState.Live or Shell.AuthState.Connecting;
        if (!signedIn)
        {
            into[n++] = new(IdNowPlaying, Loc.Get(Strings.Tray.SignedOut), Enabled: false);
            into[n++] = new(IdSeparator, "");
        }
        else
        {
            into[n++] = new(IdNowPlaying, f.HasTrack ? Fit(f.Title, f.Artist, 60) : Loc.Get(Strings.Tray.NothingPlaying), Enabled: false);
            into[n++] = new(IdSeparator, "");
            into[n++] = new(IdPlayPause, Loc.Get(f.Playing ? Strings.Taskbar.Pause : Strings.Taskbar.Play), Enabled: f.HasTrack);
            into[n++] = new(IdNext, Loc.Get(Strings.Taskbar.Next), Enabled: f.CanSkipNext);
            into[n++] = new(IdPrev, Loc.Get(Strings.Taskbar.Previous), Enabled: f.CanSkipPrev);
            if (f.Saved is { } saved && f.HasTrack)
                into[n++] = new(IdLike, Loc.Get(Strings.Tray.SaveToLiked), Checked: saved);
            into[n++] = new(IdSeparator, "");
            into[n++] = new(IdDevices, Loc.Get(Strings.Tray.Devices));
        }
        bool offerHide = windowVisible && anyHideModeOn;
        into[n++] = new(IdOpen, Loc.Get(offerHide ? Strings.Tray.HideWavee : Strings.Tray.OpenWavee), Default: true);
        into[n++] = new(IdSeparator, "");
        into[n++] = new(IdHideIcon, Loc.Get(Strings.Tray.HideIcon));
        into[n++] = new(IdQuit, Loc.Get(Strings.Tray.Quit));
        return n;
    }

    public const int MenuCapacity = 12;
}
```

### 7.4 `Platform/Tray.Host.cs` (SHELL)

```csharp
// ── Platform/Tray.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The notification-area icon's BINDING: one FluentGpu.WindowsApi.Shell.NotifyIcon, fed by the playback push and the
// auth fold, hooked into the window's close/minimize edges, dispatching the native menu's ids to the verbs.
//
// Role: SHELL
// Owner: U
// Wave: 6
// Budget: 520 lines
// Spec: wavee-0.3-tray-implementation.md §7
//
// RULES. UI thread only (the NotifyIcon's callback window belongs to it, and every verb it calls is a UI-thread
// verb). Nothing here DECIDES — Tray.cs does. Every OS call is fail-soft (ch 14 §0 rule 16). Edge-deduped: the icon
// file and the tip are pushed only when they differ from the last push, and never on a position tick (Os.Publish
// fires on SmtcTimeline effects too — Playback.Host.cs:408 — so the dedupe IS the throttle).

using FluentGpu;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Packaging;
using FluentGpu.WindowsApi.Shell;

namespace Wavee;

public static partial class Tray
{
    public static class Host
    {
        /// <summary>Stable identity for NIF_GUID (packaged only — TrayCore.UseGuidIdentity). Minted once; never change
        /// it, or every user's "show this icon" preference is lost (NOTIFYICONDATAW › Troubleshooting).</summary>
        static readonly Guid Identity = new("7C2B9E14-3A5D-4F86-9B0C-2D6E5A1F8C43");

        static NotifyIcon? s_icon;
        static bool s_on, s_quitRequested, s_windowVisible = true;
        static string s_lastIcoFile = "", s_lastTip = "";
        static int s_lastSizePx;
        static Facts s_facts;

        // Late-bound seams (the same shape as JumpList.RecentContexts): null means "row absent", never a throw.
        /// <summary>Owner of User.cs attaches: is the current track saved / toggle it.</summary>
        public static Func<bool?>? IsCurrentSaved { get; set; }
        public static Action? ToggleCurrentSaved { get; set; }
        /// <summary>Owner I's Notify attaches: is an update Available / Downloaded / Completed.</summary>
        public static Func<bool>? UpdateReady { get; set; }
        // No headless seam here on purpose: a tray-hidden Wavee IS the GUI process (plan §10), so Open and Quit are
        // the two static verbs below and nothing swaps them.

        // ── 1. boot / shutdown ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Called by Shell.Run once the window exists (right after Notify.HostInstall). Creates the icon
        /// host and shows it when the mode says so; hooks close / minimize / theme. Idempotent, fail-soft.</summary>
        public static void Boot(bool startHidden)
        {
            if (s_on || !OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
            s_on = true;
            s_windowVisible = !startHidden;
            try
            {
                s_icon = new NotifyIcon(UseGuidIdentity(PackageIdentity.IsPackaged) ? Identity : Guid.Empty, "Wavee");
                s_icon.Activated += OnIconEvent;
            }
            catch (Exception ex) { Log.Warn("tray", "notify icon unavailable this session", ex); s_icon = null; return; }

            FluentApp.CloseRequested = OnCloseRequested;          // E1
            FluentApp.WindowStateChanged += OnWindowStateChanged; // E4
            FluentApp.SystemColorsChanged += OnShellChanged;      // the taskbar may have flipped light/dark
            Refresh(force: true);
            SyncPresence();
            Log.Info("tray", "boot mode=" + Mode() + " startHidden=" + startHidden);
        }

        /// <summary>Exit tail — BEFORE Playback.Os.Shutdown, so the icon never outlives the window by a frame.</summary>
        public static void Shutdown()
        {
            if (!s_on) return;
            s_on = false;
            try { FluentApp.WindowStateChanged -= OnWindowStateChanged; } catch { }
            try { FluentApp.SystemColorsChanged -= OnShellChanged; } catch { }
            FluentApp.CloseRequested = null;
            try { s_icon?.Dispose(); } catch { }
            s_icon = null;
        }

        /// <summary>Sign-out: the glyph goes offline, the menu collapses. The push contract will not fire on a
        /// sign-out (playback did not change), so the auth fold's writer calls this beside Notify.SignedOut().</summary>
        public static void SignedOut() => Refresh(force: false);

        // ── 2. the push sink (Playback.Os.Publish → here, one line) ─────────────────────────────────────────────

        /// <summary>The fifth OS sink. Snapshots the facts from the STATE (ch 14 rule 1) and pushes only edges.</summary>
        public static void Publish(in Playback.State s)
        {
            if (!s_on) return;
            s_facts = FactsFrom(in s);
            Refresh(force: false);
        }

        static Facts FactsFrom(in Playback.State s)
        {
            string title = "", artist = "";
            if (s.HasCurrent && s.Current.Kind == EntityKind.Track)
            {
                var t = new Track(s.Current.Slot);
                if (t.Knows(TrackFields.Identity))
                {
                    title = t.Title;
                    ReadOnlySpan<int> artists = t.ArtistSlots;
                    artist = artists.Length == 0 ? "" : new Artist(artists[0]).Name;   // FIRST artist, like Os.Smtc
                }
            }
            bool remote = s.Owner == Playback.Owner.Foreign;
            return new Facts(
                s.HasCurrent, s.Phase == Playback.Phase.Playing, s.Buffering, s.Live.IsLive, remote,
                title, artist, remote ? Playback.Devices.NameOf(s.ActiveDevice) : "",
                Shell.Auth.Peek(), UpdateReady?.Invoke() ?? false,
                s.CanSkipPrev, s.CanSkipNext, IsCurrentSaved?.Invoke());
        }

        /// <summary>Recompute glyph + tip from the last facts and push whatever differs. `force` after a boot or an
        /// explorer restart, when the shell holds nothing.</summary>
        static void Refresh(bool force)
        {
            if (s_icon is not { } icon) return;
            var facts = s_facts with { Auth = Shell.Auth.Peek(), UpdateReady = UpdateReady?.Invoke() ?? false };
            s_facts = facts;

            string file = IconFileName(GlyphFor(facts.Auth, facts.UpdateReady), ThemeFor(FluentApp.TaskbarUsesLightTheme()));
            int size = IconSizeFor(icon.TaskbarDpi);
            if (force || !string.Equals(file, s_lastIcoFile, StringComparison.Ordinal) || size != s_lastSizePx)
            {
                s_lastIcoFile = file;
                s_lastSizePx = size;
                string? path = IconPath(file);
                if (path is null) Log.Warn("tray", "icon file missing: " + file);   // degraded: the shell keeps the last icon
                else { try { icon.SetIcon(path, size); } catch (Exception ex) { Log.Warn("tray", "icon push failed", ex); } }
            }

            string tip = Tooltip(in facts);
            if (force || !string.Equals(tip, s_lastTip, StringComparison.Ordinal))
            {
                s_lastTip = tip;
                try { icon.SetTip(tip); } catch (Exception ex) { Log.Warn("tray", "tip push failed", ex); }
            }
        }

        static string? IconPath(string file)
        {
            string p = Path.Combine(AppContext.BaseDirectory, "assets", "tray", file);
            return File.Exists(p) ? p : null;
        }

        // ── 3. presence, close, minimize ────────────────────────────────────────────────────────────────────────

        static IconMode Mode() => (IconMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.TrayIconMode), 0, 2);
        static bool AnyHideModeOn()
            => HideModesAllowed(Mode()) && (Platform.Settings.Get(Platform.Keys.TrayCloseToTray) || Platform.Settings.Get(Platform.Keys.TrayMinimizeToTray));

        static void SyncPresence()
        {
            if (s_icon is not { } icon) return;
            bool want = IconShown(Mode(), s_windowVisible);
            try
            {
                if (want && !icon.IsShown) { icon.Show(); Refresh(force: true); }
                else if (!want && icon.IsShown) icon.Hide();
            }
            catch (Exception ex) { Log.Warn("tray", "presence sync failed", ex); }
        }

        /// <summary>Settings ▸ General wrote a tray key: re-derive presence (a Never with the window hidden must
        /// show the window first — the setting row disables itself in that case, and this is the belt).</summary>
        public static void OnSettingsChanged()
        {
            if (!HideModesAllowed(Mode()) && !s_windowVisible) ShowWindow();
            SyncPresence();
        }

        /// <summary>E1: the window's WM_CLOSE. True = consumed (hidden), false = let the window destroy itself.</summary>
        static bool OnCloseRequested(FluentGpu.CloseReason reason)
        {
            var verdict = OnCloseRequested(Platform.Settings.Get(Platform.Keys.TrayCloseToTray), s_quitRequested, Mode(),
                reason == FluentGpu.CloseReason.SessionEnding ? CloseReason.SessionEnding : CloseReason.User);
            Log.Info("tray", "close requested reason=" + reason + " verdict=" + verdict);
            if (verdict == CloseVerdict.Quit) return false;
            HideWindow();
            return true;
        }

        /// <summary>E4: the caption's minimize, Win+Down, the system menu.</summary>
        static void OnWindowStateChanged(FluentGpu.Pal.WindowState state)
        {
            if (state != FluentGpu.Pal.WindowState.Minimized) return;
            if (OnMinimized(Platform.Settings.Get(Platform.Keys.TrayMinimizeToTray), Mode())) HideWindow();
        }

        public static void HideWindow()
        {
            if (!s_windowVisible) return;
            s_windowVisible = false;
            FluentApp.SetWindowVisible(false);                     // E2 — the host parks
            SyncPresence();
            Log.Info("tray", "window hidden");
        }

        /// <summary>Restore + foreground. Replaces Shell.WakeWindow: un-hides through the engine seam so the host
        /// un-parks and paints one frame before the window is on screen (E2's show edge).</summary>
        public static void ShowWindow()
        {
            if (!s_windowVisible) { s_windowVisible = true; FluentApp.SetWindowVisible(true); }
            nint hwnd = FluentApp.WindowHandle;
            if (hwnd == 0) return;
            Shell.RestoreAndForeground(hwnd);                      // the two user32 calls Shell.Host.cs:312-332 already owns
            SyncPresence();
        }

        public static void Quit()
        {
            s_quitRequested = true;
            FluentApp.CloseWindow();
        }

        // ── 4. the icon's events ────────────────────────────────────────────────────────────────────────────────

        static void OnIconEvent(NotifyIconEvent e, int x, int y)
        {
            // ALWAYS-ON attribution, like the SMTC button (Playback.Os.cs:269-271): a window that appears or vanishes
            // with nothing on screen to explain it must be explainable from the log.
            Log.Info("tray", "icon event " + e);
            bool foreground = FluentApp.WindowHandle != 0 && Shell.IsForeground(FluentApp.WindowHandle);
            switch (e)
            {
                case NotifyIconEvent.Select:
                case NotifyIconEvent.KeySelect:
                    Apply(OnSelect(s_windowVisible, foreground, AnyHideModeOn()));
                    break;
                case NotifyIconEvent.DoubleClick:
                    Apply(OnDoubleClick(s_windowVisible));
                    break;
                case NotifyIconEvent.MiddleClick:
                    if (MiddleTogglesPlay(s_facts.HasTrack)) Playback.TogglePlay();
                    break;
                case NotifyIconEvent.ContextMenu:
                    ShowMenu(x, y);
                    break;
                case NotifyIconEvent.Recreated:      // explorer restarted: the host re-added; we re-push everything
                case NotifyIconEvent.ShellChanged:   // DPI / light-dark: reload the .ico at the new size/variant
                    Refresh(force: true);
                    break;
            }
        }

        static void Apply(SelectAction a)
        {
            switch (a)
            {
                case SelectAction.ShowWindow: ShowWindow(); break;
                case SelectAction.HideWindow: HideWindow(); break;
                default: ShowWindow(); break;   // Foreground: the same restore path; harmless on a visible window
            }
        }

        static void ShowMenu(int x, int y)
        {
            if (s_icon is not { } icon) return;
            Span<MenuRow> rows = stackalloc MenuRow[MenuCapacity];
            int n = Menu(in s_facts, s_windowVisible, AnyHideModeOn(), rows);
            var items = new NotifyMenuItem[n];   // human rate — one small array per right-click is fine
            for (int i = 0; i < n; i++) items[i] = new(rows[i].Id, rows[i].Text, rows[i].Enabled, rows[i].Checked, rows[i].Default);
            int id;
            try { id = icon.ShowMenu(items, x, y); }
            catch (Exception ex) { Log.Warn("tray", "menu failed", ex); return; }
            if (id == 0) return;
            Log.Info("tray", "menu id=" + id);
            switch (id)
            {
                case IdPlayPause: Playback.TogglePlay(); break;
                case IdNext: Playback.Next(); break;
                case IdPrev: Playback.Previous(); break;
                case IdLike: ToggleCurrentSaved?.Invoke(); break;
                case IdDevices: ShowWindow(); Playback.RequestDevicePicker(); break;
                case IdOpen: Apply(s_windowVisible && AnyHideModeOn() ? SelectAction.HideWindow : SelectAction.ShowWindow); break;
                case IdHideIcon:
                    Platform.Settings.Set(Platform.Keys.TrayIconMode, (int)IconMode.Never);
                    OnSettingsChanged();
                    break;
                case IdQuit: Quit(); break;
            }
        }

        static void OnShellChanged() => Refresh(force: false);   // the file name changes if the taskbar theme did
    }
}
```

The two tiny helpers this needs from `Shell.Host.cs`: `RestoreAndForeground(nint)` (today's `WakeWindow` body,
`:316-317`) and `IsForeground(nint)` (`GetForegroundWindow() == hwnd`, one more `LibraryImport` beside `:322-332`).

### 7.5 The wiring in `Shell.Host.cs` (owner I's edits)

```csharp
// Shell.Run — after RegisterProtocols / the activation intake:
bool startHidden = Tray.StartHidden(
    Platform.Settings.Get(Platform.Keys.TrayStartHidden),
    activation.Kind == ActivationKind.StartupTask,          // E8 (Q3); false until it lands
    Array.IndexOf(args, "--tray") >= 0);

// …in AppOptions:
StartHidden = startHidden,                                   // E3

// …the redirect handler (replaces the unconditional WakeWindow at :300-304):
static void OnActivationRedirected(string raw)
{
    Volatile.Write(ref s_pendingActivation, raw ?? "");
    var kind = raw is { Length: > 0 } ? DeepLink(raw, false).Kind : DeepLinkKind.None;
    if (Tray.WakeFor(kind)) Tray.Host.ShowWindow();          // a jump-list Pause never raises the window
}

// …the exit tail (:195-205), first line:
Tray.Host.Shutdown();
```

`Tray.Host.Boot(startHidden)` is called from the same place `Notify.HostInstall` will be (after the window exists —
the window handle is what the icon's `Show`/`Hide` verbs act on). `RegisterProtocols` passes `"--tray"` as the Run
value's argument when `TrayStartHidden` is on (E6).

### 7.6 Teardown order and threads

- **Thread:** everything on `fgpu-ui` (the STA thread that owns the window, `FluentApp.cs:179-204`). The icon's
  callback window is created on it in `Boot` (called from `Shell.Run` on the UI thread once the harness is up — the
  same moment `Playback.Os.Activate` runs), so its messages are pumped by the same `PeekMessageW(HWND.NULL)` loop
  (`Win32Platform.cs:1205, 1617-1624`), and every handler runs on the UI thread with no hop. A `WM_COPYDATA` redirect
  and a toast activation already arrive there too.
- **Order at exit:** `Tray.Host.Shutdown()` (icon gone) → `Session.Flush` … → `Notify.HostShutdown` →
  `Playback.Os.Shutdown` → `gate.Dispose`. `ProcessExit` gets a best-effort `s_icon?.Dispose()` for the crash path.
- **Parked host:** while hidden the frame loop blocks on messages (E2 makes hidden ≡ minimized); the tray's menu and
  tooltip need no frame. Playback's ticker (`Playback.Host.cs:574-585`) and the audio pump are independent of the
  loop; `UseIsActive` consumers pause (the lyrics ticker, `UseInterval` polls) exactly as they do minimized.

---

## 8. Settings

Keys (`Platform/Platform.cs`, the `Keys` table beside `StartOnLogin` at `:249`):

```csharp
// ── notification area (docs/plans/wavee/wavee-0.3-tray-implementation.md §8) ──────────────────────────────────
// 0 Always · 1 Only while the window is hidden · 2 Never. Never forces the two hide modes off (Tray.HideModesAllowed).
public static readonly SettingKey<int> TrayIconMode = new("tray.icon.mode", 0);
// The caption ✕ / Alt+F4 hide the window instead of quitting. OFF: the Close button quits (Q1).
public static readonly SettingKey<bool> TrayCloseToTray = new("tray.closeToTray", false);
// The caption – / Win+Down hide the window instead of iconifying it.
public static readonly SettingKey<bool> TrayMinimizeToTray = new("tray.minimizeToTray", false);
// Start hidden on the SIGN-IN launch only (StartupTask / the Run value's --tray); meaningless without StartOnLogin.
public static readonly SettingKey<bool> TrayStartHidden = new("tray.startHidden", false);
```

Home: **Settings ▸ General**, a new group between *Links* and *Graphics* (ch 27 W1, `27-settings-and-diagnostics.md:386-430`),
in the tab's existing card grammar (`SettingsCard`, compact toggles 40×20, caption 12/16):

```
│ ▣ Notification area                                                                                                    │
│    Wavee in the taskbar corner                                                                                          │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ ▣  Show Wavee's icon                         ┌ Always                              ▾ ┐ 260     │ Always ·           │
│ │    Windows puts new icons behind the ˄ arrow until you drag them out.                          │ Only while hidden ·│
│ │    Turn it off and closing Wavee always quits.                                                 │ Never              │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ┌ ✕  Closing hides Wavee                Keep playing from the icon when you close the window. (  ●) ┐ disabled: Never │
│ ┌ –  Minimizing hides Wavee             The taskbar button goes away; the icon brings it back.  (  ●) ┐ disabled: Never │
│ ┌ ⏻  Start hidden at sign-in            Only when Wavee starts with Windows.                      (  ●) ┐ disabled: !StartOnLogin │
│ ┌ ⏻  Start Wavee when I sign in                                                                   (  ●) ┐ ← the EXISTING row moves here from wherever Wave 6 lands it │
```

`SettingsCatalog` (`Screens/Settings.cs`) gains the four rows under one `general.tray` group; each writer calls
`Tray.Host.OnSettingsChanged()`. The `Never` caption is the guideline's "Display icon in notification area" option
in the app's own words; the menu's `Hide icon` writes the same key.

Loc keys (`assets/loc/en-US.json`, a `tray` object beside `taskbar` at `:2273-2279`):
`tray.openWavee` "Open Wavee" · `tray.hideWavee` "Hide Wavee" · `tray.quit` "Quit Wavee" · `tray.hideIcon` "Hide
icon" · `tray.devices` "Devices…" · `tray.saveToLiked` "Save to Liked Songs" · `tray.nothingPlaying` "Nothing
playing" · `tray.signedOut` "Signed out" · `tray.offline` "Offline" · `tray.paused` "Paused" · `tray.live` "LIVE" ·
`tray.updateReady` "Update ready" · `tray.onDevice` "on {0}" · the settings labels and captions above.

---

## 9. Tests (`Wavee.Tests/TrayTests.cs`) — no window, no OS, no source text

The pattern is `PlaybackOsTests.cs:1-55` (each rule a small fact; the parity checklist verifies pixels, these verify
rules).

```csharp
public class TrayPresenceTests
{
    [Theory]
    [InlineData(Tray.IconMode.Always, true, true)]
    [InlineData(Tray.IconMode.Always, false, true)]
    [InlineData(Tray.IconMode.WhileHidden, true, false)]
    [InlineData(Tray.IconMode.WhileHidden, false, true)]
    [InlineData(Tray.IconMode.Never, false, false)]
    public void Icon_presence_follows_the_mode_and_the_window(Tray.IconMode mode, bool visible, bool shown)
        => Assert.Equal(shown, Tray.IconShown(mode, visible));

    [Fact]
    public void Guid_identity_only_for_the_signed_packaged_install()
    {
        Assert.True(Tray.UseGuidIdentity(isPackaged: true));
        Assert.False(Tray.UseGuidIdentity(isPackaged: false));
    }
}

public class TrayCloseTests
{
    [Fact]
    public void Close_hides_only_when_opted_in_and_an_icon_can_bring_it_back()
    {
        Assert.Equal(Tray.CloseVerdict.Hide, Tray.OnCloseRequested(true, false, Tray.IconMode.Always, Tray.CloseReason.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(false, false, Tray.IconMode.Always, Tray.CloseReason.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, false, Tray.IconMode.Never, Tray.CloseReason.User));
    }

    [Fact]
    public void An_explicit_quit_and_a_session_end_always_quit()
    {
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, true, Tray.IconMode.Always, Tray.CloseReason.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, false, Tray.IconMode.Always, Tray.CloseReason.SessionEnding));
    }

    [Fact]
    public void Start_hidden_needs_the_setting_AND_a_startup_launch()
    {
        Assert.False(Tray.StartHidden(true, false, false));    // a Start-menu click with the setting on still shows
        Assert.True(Tray.StartHidden(true, true, false));
        Assert.True(Tray.StartHidden(true, false, true));
        Assert.False(Tray.StartHidden(false, true, true));
    }
}

public class TrayClickTests
{
    [Fact]
    public void Select_shows_a_hidden_window_and_hides_a_focused_one_only_when_a_hide_mode_is_on()
    {
        Assert.Equal(Tray.SelectAction.ShowWindow, Tray.OnSelect(false, false, false));
        Assert.Equal(Tray.SelectAction.Foreground, Tray.OnSelect(true, false, true));
        Assert.Equal(Tray.SelectAction.HideWindow, Tray.OnSelect(true, true, true));
        Assert.Equal(Tray.SelectAction.Foreground, Tray.OnSelect(true, true, false));
    }

    [Fact]
    public void A_jump_list_pause_never_raises_the_window()
    {
        Assert.False(Tray.WakeFor(Shell.DeepLinkKind.Pause));
        Assert.False(Tray.WakeFor(Shell.DeepLinkKind.Resume));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.Open));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.None));    // a bare second launch
    }
}

public class TrayGlyphTests
{
    [Fact]
    public void Playing_and_paused_share_one_glyph()
        => Assert.Equal(Tray.Glyph.Normal, Tray.GlyphFor(Shell.AuthState.Live, updateReady: false));

    [Fact]
    public void Offline_beats_update()
        => Assert.Equal(Tray.Glyph.Offline, Tray.GlyphFor(Shell.AuthState.Offline, updateReady: true));

    [Theory]
    [InlineData(96u, 16)] [InlineData(120u, 20)] [InlineData(144u, 24)] [InlineData(192u, 32)] [InlineData(240u, 40)] [InlineData(288u, 48)]
    public void Icon_size_is_SM_CXSMICON_at_the_taskbar_dpi(uint dpi, int px) => Assert.Equal(px, Tray.IconSizeFor(dpi));

    [Fact]
    public void File_name_is_theme_then_glyph()
        => Assert.Equal("wavee-on-light-update.ico", Tray.IconFileName(Tray.Glyph.Update, Tray.Theme.OnLight));
}

public class TrayTooltipTests
{
    static Tray.Facts Playing(string title, string artist) => new(true, true, false, false, false, title, artist, "",
        Shell.AuthState.Live, false, true, true, null);

    [Fact]
    public void Playing_reads_company_dash_title_dot_artist()
        => Assert.Equal("Wavee — Midnight City · M83", Tray.Tooltip(Playing("Midnight City", "M83")));

    [Fact]
    public void Paused_is_prefixed_and_a_remote_owner_is_suffixed()
    {
        var f = Playing("Midnight City", "M83") with { Playing = false };
        Assert.Equal("Wavee — Paused: Midnight City · M83", Tray.Tooltip(f));
        f = f with { Playing = true, RemoteOwner = true, DeviceName = "Kitchen" };
        Assert.Equal("Wavee — Midnight City · M83 · on Kitchen", Tray.Tooltip(f));
    }

    [Fact]
    public void Never_exceeds_szTip_and_cuts_the_title_before_the_artist()
    {
        string tip = Tray.Tooltip(Playing(new string('a', 200), new string('b', 40)));
        Assert.True(tip.Length <= Tray.TipMax);
        Assert.EndsWith(new string('b', 40), tip);           // the artist survived whole
        Assert.Contains("…", tip);
    }

    [Fact]
    public void Signed_out_ignores_the_track()
        => Assert.Equal("Wavee — Signed out", Tray.Tooltip(Playing("x", "y") with { Auth = Shell.AuthState.SignInRequired }));
}

public class TrayMenuTests
{
    [Fact]
    public void Signed_out_collapses_to_open_hide_icon_quit()
    {
        Span<Tray.MenuRow> rows = stackalloc Tray.MenuRow[Tray.MenuCapacity];
        var f = new Tray.Facts(true, true, false, false, false, "t", "a", "", Shell.AuthState.SignInRequired, false, true, true, true);
        int n = Tray.Menu(in f, windowVisible: false, anyHideModeOn: false, rows);
        Assert.Equal(new[] { Tray.IdNowPlaying, Tray.IdSeparator, Tray.IdOpen, Tray.IdSeparator, Tray.IdHideIcon, Tray.IdQuit },
            rows[..n].ToArray().Select(r => r.Id));
    }

    [Fact]
    public void Open_is_the_bold_default_and_becomes_hide_only_when_visible_with_a_hide_mode()
    {
        Span<Tray.MenuRow> rows = stackalloc Tray.MenuRow[Tray.MenuCapacity];
        var f = new Tray.Facts(false, false, false, false, false, "", "", "", Shell.AuthState.Live, false, false, false, null);
        int n = Tray.Menu(in f, true, true, rows);
        var open = rows[..n].ToArray().Single(r => r.Id == Tray.IdOpen);
        Assert.True(open.Default);
        Assert.Equal(Loc.Get(Strings.Tray.HideWavee), open.Text);
    }

    [Fact]
    public void The_like_row_is_absent_until_the_seam_attaches()
    {
        Span<Tray.MenuRow> rows = stackalloc Tray.MenuRow[Tray.MenuCapacity];
        var f = new Tray.Facts(true, true, false, false, false, "t", "a", "", Shell.AuthState.Live, false, true, true, Saved: null);
        int n = Tray.Menu(in f, true, false, rows);
        Assert.DoesNotContain(rows[..n].ToArray(), r => r.Id == Tray.IdLike);
    }
}
```

Engine side (in the engine repo, its own gates): a headless `NotifyIconTests` for the version-4 decode
(`LOWORD/HIWORD(lParam)` → `NotifyIconEvent`, anchor from `wParam`) extracted as a pure `NotifyIconDecode.Decode(wParam,
lParam)` — the same "extract the decision" rule the engine's CLAUDE.md states (`fluent-gpu-base/CLAUDE.md:88-93`).

---

## 10. The headless seam — reconciled against `wavee-0.3-headless-implementation.md` §6 (`:1278-1304`)

The headless plan landed while this one was being written, and its §6 settles the boundary in one sentence: *"a
background run with a tray and no main window is not a variant of headless mode … 'tray, window hidden' is the GUI
process with its main window hidden or closed-to-tray, `FluentAppHarness.Run` still looping (idle-gated), and
`Playback.Os` still armed."* This plan agrees, and the consequences are:

1. **The tray is never hosted by the headless arm.** `--headless` skips `Shell.Run`, the single-instance gate and
   `Playback.Os` (`headless-implementation.md:325-326, 465`); `Tray.Host.Boot` is called only from `Shell.Run`, after
   the window exists. No `hwnd == 0` tolerance is needed beyond the existing fail-soft. The earlier draft's swappable
   `ShowWindowVerb` / `QuitVerb` seams are **dropped** — `ShowWindow()` and `Quit()` are called directly.
2. **A background run = this plan's §4.3 hidden window**, with E2 making the parked host cost what a minimized
   window costs — the exact claim §6 makes ("idle-gated, so it costs what an idle window costs"). The engine's
   `IsParked` change is what makes that claim true for `SW_HIDE`; without E2 it is true only for minimize.
3. **Verbs.** The tray binds to the verb list §6 names (`Playback.TogglePlay / Next / Previous / RequestDevicePicker`,
   `Playback.Host.cs:592-615`) "and to nothing in `Diagnostics.Headless`" — the script grammar is not a UI seam, and
   `wavee://quit` is a deep link, not a headless command.
4. **Marshallers.** The tray must not assign `Playback.ToUi` & co. (`:1300-1301`); it does not. Its own callbacks
   are already UI-thread (§7.6), and `Publish` reaches it from the drain, which runs on the UI thread once the GUI's
   marshaller omission (`headless-implementation.md` §1.6 item 1, owner I's I1 row) is fixed — a fix the tray depends
   on for the same reason every other sink does.
5. **The `--tray` flag.** §6 offers a row in `Diagnostics.Probe.TryRun`'s arm table "if it wants a
   `--start-minimized`-style flag — add a row, do not add a parser". `--tray` is not a probe arm (it never returns
   early; the GUI still runs), so this plan parses it in `Shell.ParseArgs` beside `--width` / `--height`
   (`Shell.Host.cs:208-223`), which already exists and is the window-options parser. Recorded here as a small,
   deliberate divergence, not a second parser.
6. **Device names.** Close-to-tray keeps the GUI's Connect device (`Environment.MachineName`, `Playback.Host.cs:254`)
   alive; the headless `--connect` device is `Wavee (headless)` (`:1302-1304`), so the two never flap. Nothing to do
   on the tray side.
7. **Push order.** Because the tray never runs without a window, the one-line hook sits at the **end** of
   `Playback.Os.Publish` (after `PowerPolicy.OnStateChanged`); the first-push `Activate` recursion (`Playback.Os.cs:120`)
   reaches it on the re-entered call.

---

## 11. Accessibility and robustness checklist (design commitments)

- **Keyboard:** Win+B → arrow to the icon → Enter/Space (`NIN_KEYSELECT`) opens/shows; the context key / Shift+F10
  opens the menu at the icon's top-left (the version-4 anchor rule); Esc closes it and `NIM_SETFOCUS` hands focus back
  to the area. Menu rows are native, so arrow keys, mnemonics and Narrator work without engine involvement.
- **Narrator name:** the tooltip string *is* the icon's accessible name — hence it always starts with `Wavee`.
- **Explorer restart:** `Recreated` → re-add + re-push (§4.8). Verified on-box by `taskkill /f /im explorer.exe`.
- **DPI change / monitor change:** `ShellChanged` → reload the `.ico` at the new `IconSizeFor(TaskbarDpi)`;
  the taskbar's monitor is re-queried each time.
- **Theme change:** `SystemColorsChanged` (already relayed) → `Refresh` → a different file name → one `NIM_MODIFY`.
- **Focus stealing:** the window is raised only from a user gesture on the icon, a second launch (the sender granted
  `ASFW_ANY`, `SingleInstanceGate.cs:143`) or a toast click — all cases where Windows already permits
  `SetForegroundWindow`. A transport deep link never raises it (§4.7).
- **Multiple monitors:** the menu is placed at the event anchor with `TPM_WORKAREA`-safe defaults; the window
  restores where it was (`SW_RESTORE`), never re-centred.
- **Overflow:** nothing to do; the caption says where Windows puts new icons.
- **Fail-soft:** a missing `.ico` degrades to the last icon the shell holds (logged once); a shell that refuses
  `NIM_ADD` leaves `IsShown` false and every hide mode inert (`OnCloseRequested` still returns `Hide` if the setting
  says so — so the host also checks `icon.IsShown` before hiding, and quits instead when there is no way back; this
  is one more clause in `Tray.Host.OnCloseRequested`, tested through the pure rule's `mode` argument by mapping "icon
  refused" to `IconMode.Never`).

---

## 12. Work split and verification

### 12.1 Sequencing (what can land while the UI waves are in flight)

| Step | Where | Who | Depends on | Can start now? |
|---|---|---|---|---|
| **W-A** engine E1–E7 (+E8 if Q3=a) | `fluent-gpu-base` | one engine agent | — | **yes** — nothing in the worktree compiles against it until W-C; verified in the engine repo (`dotnet build src/FluentGpu.slnx` Debug + Release, VerticalSlice, the gallery's Windows-APIs page gains a tray row) |
| **W-B** `Platform/Tray.cs` + `TrayTests.cs` + the four keys + loc strings + `assets/tray/*` (icons, masks, SVG, generator) | worktree | agent U | `Shell.AuthState`, `Shell.DeepLinkKind` (exist) | **yes** — CORE only; compiles today; the tests are the gate |
| **W-C** `Platform/Tray.Host.cs` + the one-line `Playback.Os.Publish` hook + the `Shell.Host.cs` edits | worktree | agent U (+ I for `Shell.Host.cs`, H for the one line) | W-A landed in `fluent-gpu-base` | after W-A |
| **W-D** the General-tab group | worktree | R (Wave 6) | W-B keys; `Settings.cs` catalog | with Wave 6 |
| **W-E** owner I's I1 fix (the GUI marshallers → `AppHost.Post`, headless plan §1.6 item 1) | worktree | I | — | already in I's Wave-4 pass; the tray's push sink is correct only once it lands |

Disjoint files per agent: engine agent touches only `fluent-gpu-base`; U owns `Platform/Tray.cs`, `Platform/Tray.Host.cs`,
`Wavee.Tests/TrayTests.cs`, `assets/tray/**`, the `tray.*` block of `en-US.json`; the three cross-owner edits
(`Platform.cs` +5, `Playback.Os.cs` +1, `Shell.Host.cs` +30, `Shell.cs` +10) are listed above with their owners and are
small enough for the orchestrator to apply.

### 12.2 What the orchestrator runs

1. Engine: `dotnet build src/FluentGpu.slnx` (Debug **and** Release), `dotnet run --project src/FluentGpu.VerticalSlice`
   → `ALL CHECKS PASSED`; the gallery's Windows-APIs page shows the tray row (add / modify tip / menu / delete).
2. App: `dotnet build Wavee.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` with
   `TrayTests` green; `dotnet run --project src/apps/Wavee -- --fake` shows the icon with `Wavee` as the tooltip.
3. **On-box manual checklist** (sandbox-free launch, the memory note `launching-and-capturing-wavee.md`):
   1. Icon present at 100 % and at 150 % (secondary monitor set primary): the 16 and 24 frames, crisp, no fringe.
   2. Windows mode Light ↔ Dark (Settings ▸ Colors) with app mode fixed: the glyph flips on-light/on-dark **without**
      the app theme changing; the reverse (app mode only) leaves the tray alone.
   3. Play a track: tooltip `Wavee — Title · Artist`; pause: `Wavee — Paused: …`; the glyph does not change.
   4. Right-click: menu rows and enablement match §5.1; Esc returns focus to the tray (Win+B highlight visible).
   5. Left click with no hide mode: window comes to front, never hides. Enable *Closing hides Wavee* → ✕ hides,
      taskbar button gone, playback continues, SMTC card still shows; left click restores in place.
   6. `wavee://pause` from the jump list while hidden: playback pauses, **window stays hidden**; a second launch
      (double-click the exe) restores it.
   7. `taskkill /f /im explorer.exe`, restart Explorer: icon returns with the right tooltip within a second.
   8. Sign out: offline glyph, `Wavee — Signed out`, collapsed menu. Sign back in: normal glyph.
   9. Simulate an update (Developer ▸ Simulate an update): the dot appears; the tooltip gains ` · Update ready`.
   10. *Never* while hidden: the window is shown first, then the icon goes; ✕ now quits.
   11. `Quit Wavee` from the menu: process exits, icon gone with no ghost on hover; log ends with the exit tail.
   12. Packaged build (`wavee-release.ps1 -DryRun`, install the MSIX): steps 1, 5, 7 again — the GUID identity path —
       then install the next build over it and confirm the icon's "show on taskbar" preference survived.
   13. Start-on-login + start hidden (unpackaged: sign out/in, or run the `Run` value's command line by hand): the
       process starts with no window, the icon present; left click shows the window.

### 12.3 Definition of done

Debug + Release clean in both repos; `TrayTests` green; the thirteen checks above recorded in this file's audit log;
a CHANGELOG bullet with `(#n)` once the issue exists (Q6); the Settings General-tab wireframe in ch 27 updated with the
new group and its parity items (three: presence, close, tooltip).

---

## 13. Open questions for Christos — ANSWERED 2026-09-13

**Decisions (Christos, 2026-09-13). These override anything above that disagrees.**

| # | Decision |
|---|---|
| 1 | Close-to-tray ships **off**. |
| 2 | The icon mode is a user choice (`Always` / `Only while hidden` / `Never`); the **default is `Only while hidden`**, not `Always`. §4.1, §8's default and the tests follow this. |
| 3 | **Yes**: add `ActivationKind.StartupTask` to the engine (E8) so the packaged sign-in launch can start hidden in 0.3. |
| 4 | **Yes**: use the undocumented `uxtheme` ordinals 135/136 (fail-soft, one probe) for a dark menu on a dark taskbar. |
| 5 | The mini-player flyout is **deferred to 0.4**. |
| 6 | Issue **#152** (christosk92/WaveeMusic#152): the CHANGELOG bullet ends with `(#152)` and the commit carries `Fixes #152`. |
| 7 | Middle click = play/pause **stays**. |

The original questions, for the record:

1. **Close-to-tray default.** This plan says **off** (the Close button quits; Spotify's opposite choice is its
   most-complained-about behaviour). Ship off, or on?
2. **Icon default.** **Always** (this plan) versus *Only while hidden* — Always gives a status glyph and a standard
   place to find Wavee; Only-while-hidden keeps the tray clean for users who never hide it.
3. **Packaged start-hidden.** (a) add `ActivationKind.StartupTask` to the engine via `AppInstance.GetActivatedEventArgs`
   (WinRT, ~60 lines, the WASDK precedent) so the sign-in launch is recognisable, or (b) skip start-hidden for the
   packaged build in 0.3.
4. **Dark menu.** Allow the undocumented `uxtheme` ordinals 135/136 (fail-soft, one probe) so the tray menu is dark
   on a dark taskbar, or accept a light menu?
5. **Mini-player flyout.** Confirm the deferral to 0.4 (§5.2) — or, if wanted in 0.3, it is the unowned-popup +
   second-root engine work (~1,500–2,000 lines) and moves the tray out of Wave 6.
6. **Issue number.** The feature needs a GitHub issue for the `(#n)` bullet; drafted, not filed.
7. **Middle click = play/pause.** Keep (foobar convention) or drop (undiscoverable)?

---

## 14. Audit log

| # | Date | Change |
|---|---|---|
| 1 | 2026-09-13 | First version: investigation of `_old/`, the 0.3 seams, the engine, the Win32 contract and the players; the behaviour spec, artwork, native-menu decision, the E1–E8 engine seams, `Tray.cs`/`Tray.Host.cs`, the tests, settings, the work split and the on-box checklist. |
| 2 | 2026-09-13 | §10 rewritten against the headless plan's §6, which landed mid-write: a tray-hidden Wavee is the GUI process, never the headless arm; the two swappable `ShowWindowVerb`/`QuitVerb` seams removed; `--tray` parsed in `Shell.ParseArgs` (a recorded divergence from §6's arm-table suggestion); W-E re-pointed at owner I's marshaller fix. |
