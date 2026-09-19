// ── Platform/Tray.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The notification-area icon's BINDING: one FluentGpu.WindowsApi.Shell.NotifyIcon, fed by the playback push and by the
// facts the push cannot see (sign-out, a culture switch, the late seams), hooked into the window's close and minimize
// edges, dispatching the icon's events and the native menu's ids to the verbs.
//
// Role: SHELL
// Owner: U
// Wave: 6
// Budget: 520 lines
// Spec: docs/plans/wavee/wavee-0.3-tray-implementation.md §4, §5.1, §5.3, §6.5, §7.4-§7.6, §10, §11 — §13 overrides
//
// RULES. UI thread only: the icon's hidden callback window belongs to the thread that creates it, and every verb it
// calls is a UI-thread verb. Nothing here DECIDES — `Tray.cs` does. Every OS call is fail-soft. Edge-deduped: the icon
// file and the tip reach the shell only when they differ from the last push, never on a position tick (`Os.Publish`
// fires on SmtcTimeline effects too, so the dedupe IS the throttle), and the per-push path allocates nothing.
//
// LIFECYCLE (§7.6). `Shell.Run` ARMS the host on Main before the loop (intent only, no OS object). The root component's
// tracked effect (`Watch`) BOOTS it on its first run — inside the root mount, on the UI thread, so a start-hidden window
// that never paints still gets its icon — and re-runs it on the auth fold, the culture epoch and a seam attach. The
// icon goes with the close that ends the loop (still on the UI thread); `Shell.Run`'s exit tail calls `Shutdown` again
// as the belt. A headless run never arms it (plan §10): a tray-hidden Wavee IS the GUI process.
//
// THE GHOST RULE (§11). A hidden window with no icon is reachable only from Task Manager, so every hide goes through
// `EnsureWayBack` (the icon must be in the tray first) and every presence sync ends with `Tray.IsGhost` — a refused
// NIM_ADD shows the window rather than stranding it.

using System.Runtime.InteropServices;

using FluentGpu;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Packaging;
using FluentGpu.WindowsApi.Shell;

namespace Wavee;

public static partial class Tray
{
    public static unsafe partial class Host
    {
        // ── state (UI thread) ───────────────────────────────────────────────────────────────────────────────────────

        static bool s_armed, s_startHidden, s_booted, s_down, s_quitRequested, s_inMenu;
        static bool s_windowVisible = true;
        static NotifyIcon? s_icon;

        static Facts s_facts = new(false, false, false, false, false, "", "", "", Shell.AuthState.SignInRequired, false, false, false, null);
        static Words s_words;
        static int s_wordsEpoch = -1;
        static bool s_updateReady;

        static bool s_haveKey;
        static IconKey s_lastKey;
        static string? s_lastTip;

        static long s_shownAtMs = long.MinValue;
        static ForegroundLatch s_foreground;
        static nint s_foregroundHook;

        static readonly MenuRow[] s_rows = new MenuRow[MenuCapacity];
        static readonly NotifyMenuItem[] s_items = new NotifyMenuItem[MenuCapacity];

        /// <summary>Bumped when a seam attaches, so <see cref="Watch"/> re-runs and subscribes to what the seam reads.</summary>
        static readonly Signal<int> s_seams = new(0);

        // ── the late seams (null = row absent / no badge, never a throw) ────────────────────────────────────────────

        static Func<bool>? s_updateSeam;

        /// <summary>Owner I's update surface attaches: an update is downloaded / ready to apply. Read inside the tracked
        /// effect, so a seam that reads a signal re-badges the icon when that signal moves. UI thread.</summary>
        public static Func<bool>? UpdateReady
        {
            get => s_updateSeam;
            set { s_updateSeam = value; s_seams.Value = s_seams.Peek() + 1; }
        }

        /// <summary>Owner of <c>User.cs</c> attaches: is the current track in Liked Songs. Read when the menu opens; the
        /// Like row appears only while BOTH this and <see cref="ToggleCurrentSaved"/> are attached.</summary>
        public static Func<bool>? IsCurrentSaved { get; set; }

        /// <summary>The Like row's verb. See <see cref="IsCurrentSaved"/>.</summary>
        public static Action? ToggleCurrentSaved { get; set; }

        // ══ 1. ARM / BOOT / SHUTDOWN ═══════════════════════════════════════════════════════════════════════════════

        /// <summary><c>Shell.Run</c>, on Main, before the loop, for the interactive run only (never the harness arms,
        /// never headless). Records intent; the icon is created by <see cref="Watch"/>'s first run on the UI thread.</summary>
        public static void Arm(bool startHidden)
        {
            s_armed = true;
            s_startHidden = startHidden;
        }

        /// <summary>The ROOT component's tracked effect (<c>UseSignalEffect</c>) — call it from nowhere else. Its first run
        /// boots the icon; it re-runs on what the playback push never carries: the auth fold (a sign-out swaps the glyph
        /// and collapses the menu), the culture epoch (the Words are rebuilt), and a seam attach.</summary>
        public static void Watch()
        {
            if (!s_armed || s_down) return;
            _ = s_seams.Value;
            _ = Shell.Auth.Value;
            int epoch = Localization.CultureEpoch.Value;
            if (epoch != s_wordsEpoch)
            {
                s_words = BuildWords();
                s_wordsEpoch = epoch;
            }
            try { s_updateReady = s_updateSeam?.Invoke() ?? false; }
            catch (Exception ex) { s_updateReady = false; Log.Warn("tray", "update seam threw; no badge", ex); }

            if (!s_booted) Boot();
            else Refresh(force: false);
        }

        static void Boot()
        {
            s_booted = true;
            s_windowVisible = !s_startHidden;
            bool guid = UseGuidIdentity(PackageIdentity.IsPackaged);
            try
            {
                s_icon = new NotifyIcon(guid ? IconGuid : Guid.Empty, "Wavee");
                s_icon.Activated += OnIcon;
            }
            catch (Exception ex)
            {
                s_icon = null;   // EffectiveMode reads this as Never: close quits, minimize iconifies, nothing hides
                Log.Warn("tray", "notification icon unavailable this session", ex);
            }

            FluentApp.CloseRequested = OnClose;                   // E1
            FluentApp.WindowStateChanged += OnWindowState;        // E4 (+ the hide/show edges anything else causes)
            Refresh(force: true);                                 // the image and tip exist before NIM_ADD sends them
            SyncPresence();                                       // start hidden + no icon ⇒ the ghost guard shows it
            SyncForegroundHook();
            Log.Info("tray", "boot mode=" + Mode() + " startHidden=" + s_startHidden + " icon=" + (s_icon is not null)
                + " identity=" + (guid ? "guid" : "window") + " shown=" + (s_icon?.IsShown ?? false));
        }

        /// <summary>The icon leaves the tray. Called on the UI thread by the close that ends the loop, and again (a no-op
        /// by then) as the first line of <c>Shell.Run</c>'s exit tail — BEFORE <c>Playback.Os.Shutdown</c>, so the icon
        /// never outlives the window. Idempotent.</summary>
        public static void Shutdown()
        {
            if (!s_booted || s_down) return;
            s_down = true;
            if (s_foregroundHook != 0) { UnhookWinEvent(s_foregroundHook); s_foregroundHook = 0; }
            FluentApp.WindowStateChanged -= OnWindowState;
            FluentApp.CloseRequested = null;
            if (s_icon is { } icon)
            {
                s_icon = null;
                icon.Activated -= OnIcon;
                try { icon.Dispose(); } catch (Exception ex) { Log.Warn("tray", "icon dispose failed", ex); }
            }
            Log.Info("tray", "shutdown");
        }

        static Words BuildWords() => new(
            Paused: Loc.Get(Strings.Tray.Paused), Live: Loc.Get(Strings.Tray.Live),
            OnDevice: Loc.Get(Strings.Tray.OnDeviceKey),   // the raw template: WriteTooltip fills {device} itself
            UpdateReady: Loc.Get(Strings.Tray.UpdateReady), SignedOut: Loc.Get(Strings.Tray.SignedOut),
            Offline: Loc.Get(Strings.Tray.Offline), NothingPlaying: Loc.Get(Strings.Tray.NothingPlaying),
            Play: Loc.Get(Strings.Taskbar.Play), Pause: Loc.Get(Strings.Taskbar.Pause), Next: Loc.Get(Strings.Taskbar.Next),
            Previous: Loc.Get(Strings.Taskbar.Previous), SaveToLiked: Loc.Get(Strings.Tray.SaveToLiked),
            Devices: Loc.Get(Strings.Tray.Devices), OpenWavee: Loc.Get(Strings.Tray.OpenWavee),
            HideWavee: Loc.Get(Strings.Tray.HideWavee), HideIcon: Loc.Get(Strings.Tray.HideIcon), Quit: Loc.Get(Strings.Tray.Quit));

        // ══ 2. THE PUSH SINK (Playback.Os.Publish → here) ══════════════════════════════════════════════════════════

        /// <summary>The fifth OS sink. Snapshots the facts from the STATE (ch 14 rule 1) and pushes only edges. Reads no
        /// signal and calls no seam, so a push inside someone else's computation subscribes it to nothing.</summary>
        public static void Publish(in Playback.State s)
        {
            if (!s_armed || s_down) return;
            s_facts = FactsFrom(in s);
            Refresh(force: false);
        }

        static Facts FactsFrom(in Playback.State s)
        {
            string title = "", artist = "";
            EntityRef cur = s.Current;
            if (!cur.IsNone && cur.Kind == EntityKind.Track && new Track(cur.Slot) is { IsValid: true } t)
            {
                title = t.Title;
                ReadOnlySpan<int> artists = t.ArtistSlots;
                artist = artists.Length == 0 ? "" : new Artist(artists[0]).Name;   // the FIRST artist, like the SMTC card
            }
            else if (!cur.IsNone && cur.Kind == EntityKind.Episode && new Episode(cur.Slot) is { IsValid: true } e)
            {
                title = e.Title;
                artist = e.ShowSlot > 0 ? e.Show.Title : "";
            }
            bool remote = s.Owner == Playback.Owner.Foreign;
            return new Facts(
                HasTrack: s.HasCurrent, Playing: s.Phase == Playback.Phase.Playing,
                Buffering: s.Buffering || s.Phase == Playback.Phase.Loading, IsLive: s.Live.IsLive, RemoteOwner: remote,
                Title: title, Artist: artist, DeviceName: remote ? Playback.Devices.NameOf(s.ActiveDevice) : "",
                Auth: Shell.Auth.Peek(), UpdateReady: s_updateReady, CanSkipPrev: s.CanSkipPrev, CanSkipNext: s.CanSkipNext,
                Saved: null);
        }

        /// <summary>Recompute the icon key and the tip from the last facts and push whatever differs. <paramref name="force"/>
        /// after a boot, an explorer restart or a shell change, when the shell's copy cannot be trusted.</summary>
        static void Refresh(bool force)
        {
            if (s_icon is not { } icon) return;
            s_facts = s_facts with { Auth = Shell.Auth.Peek(), UpdateReady = s_updateReady };

            // The icon's own cached shell facts (re-read on every broadcast, hidden or not), never a registry read per push.
            IconKey key = IconFor(s_facts.Auth, s_facts.UpdateReady, icon.TaskbarUsesLightTheme, icon.TaskbarDpi);
            if (force || !s_haveKey || key != s_lastKey)
            {
                s_haveKey = true;
                s_lastKey = key;   // recorded even on a failed load: a missing file is logged per edge, not per push
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "tray", key.FileName);
                try { if (!icon.SetIcon(path, key.FramePx)) Log.Warn("tray", "icon not loaded: " + key.FileName + " @" + key.FramePx + "px"); }
                catch (Exception ex) { Log.Warn("tray", "icon push failed", ex); }
            }

            Span<char> tip = stackalloc char[TipMax];
            int n = WriteTooltip(in s_facts, in s_words, tip);
            if (force || TipDiffers(tip[..n], s_lastTip))
            {
                s_lastTip = new string(tip[..n]);
                try { icon.SetTip(s_lastTip); }
                catch (Exception ex) { Log.Warn("tray", "tip push failed", ex); }
            }
        }

        // ══ 3. PRESENCE, CLOSE, MINIMIZE ═══════════════════════════════════════════════════════════════════════════

        static IconMode Mode() => EffectiveMode(ModeFrom(Platform.Settings.Get(Platform.Keys.TrayIconMode)), s_icon is not null);

        static bool AnyHideModeOn() => Tray.AnyHideModeOn(Mode(),
            Platform.Settings.Get(Platform.Keys.TrayCloseToTray), Platform.Settings.Get(Platform.Keys.TrayMinimizeToTray));

        /// <summary>Settings ▸ General wrote a tray key (W-D's rows call this; the menu's Hide icon does too): the Run
        /// value's <c>--tray</c>, the icon's presence (Never while hidden shows the window FIRST, then the icon goes) and
        /// the foreground hook follow.</summary>
        public static void OnSettingsChanged()
        {
            Shell.SyncStartupRegistration();
            if (!s_booted || s_down) return;
            SyncPresence();
            SyncForegroundHook();
        }

        /// <summary>The icon follows the mode and the window; a hidden window with no icon is shown (the ghost guard).</summary>
        static void SyncPresence()
        {
            if (!s_booted || s_down) return;
            IconMode mode = Mode();
            if (!s_windowVisible)
            {
                bool shown = s_icon is { } hiddenIcon && IconShown(mode, windowVisible: false) && (hiddenIcon.IsShown || hiddenIcon.Show());
                if (!IsGhost(windowVisible: false, shown)) return;
                Log.Warn("tray", "hidden window with no icon (mode=" + mode + "): showing the window");
                ShowWindowCore();
            }
            if (s_icon is not { } icon) return;
            try
            {
                if (IconShown(mode, windowVisible: true)) { if (!icon.IsShown) icon.Show(); }
                else icon.Hide();   // idempotent; also clears a pending re-add from a refused Show
            }
            catch (Exception ex) { Log.Warn("tray", "presence sync failed", ex); }
        }

        /// <summary>A hide is allowed only once the icon is in the tray to come back through.</summary>
        static bool EnsureWayBack()
            => s_icon is { } icon && IconShown(Mode(), windowVisible: false) && (icon.IsShown || icon.Show());

        /// <summary>E1: the caption ✕, Alt+F4, the system menu, <c>FluentApp.CloseWindow</c>. True keeps the window.</summary>
        static bool s_retiring, s_retired;

        static bool OnClose(FluentGpu.Pal.CloseReason reason)
        {
            var cause = reason == FluentGpu.Pal.CloseReason.SessionEnding ? CloseCause.SessionEnding : CloseCause.User;
            CloseVerdict verdict = OnCloseRequested(Platform.Settings.Get(Platform.Keys.TrayCloseToTray), s_quitRequested, Mode(), cause);
            if (verdict == CloseVerdict.Hide && !EnsureWayBack()) verdict = CloseVerdict.Quit;   // §11: no way back ⇒ quit
            Log.Info("tray", "close requested reason=" + reason + " verdict=" + verdict);
            if (verdict == CloseVerdict.Quit)
            {
                if (!s_retired && reason != FluentGpu.Pal.CloseReason.SessionEnding)
                {
                    if (!s_retiring)
                    {
                        s_retiring = true;
                        // Keep the UI dispatcher alive until the bounded inactive PUT has completed.
                        Spotify.Connect.RetireThen(() => Playback.ToUi(() =>
                        {
                            s_retired = true;
                            s_quitRequested = true;
                            FluentApp.CloseWindow();
                        }));
                    }
                    return true;
                }
                if (!s_retired) Playback.RetireForSignOut(out _);
                Shutdown();   // on the UI thread, in the message that ends the loop: no ghost icon after exit
                return false;
            }
            HideWindow();
            return true;
        }

        /// <summary>E4: minimize-to-tray, plus the hide/show edges anything else causes (the relay is ground truth).</summary>
        static void OnWindowState(FluentGpu.Pal.WindowStateChange change)
        {
            if (s_down) return;
            if (change.Minimized && OnMinimized(Platform.Settings.Get(Platform.Keys.TrayMinimizeToTray), Mode()))
            {
                Log.Info("tray", "minimized: hiding to the notification area");
                HideWindow();   // refused (no icon) ⇒ it simply stays minimized, taskbar button and all
                return;
            }
            if (change.Hidden || change.Shown)
            {
                s_windowVisible = change.Current.Visible;
                SyncPresence();
            }
        }

        static void HideWindow()
        {
            if (s_down || !EnsureWayBack())
            {
                Log.Info("tray", "hide refused: no icon to come back through");
                return;
            }
            s_windowVisible = false;
            FluentApp.SetWindowVisible(false);   // E2: the host parks exactly as minimized
            Log.Info("tray", "window hidden");
            SyncPresence();
        }

        /// <summary>Un-hide, restore and bring to the front — through the engine seam first, so the host un-parks and the
        /// show edge paints. The redirect and deep-link door call this (gated by <c>Tray.WakeFor</c>). Safe without a
        /// window and before boot.</summary>
        public static void ShowWindow()
        {
            ShowWindowCore();
            SyncPresence();
        }

        static void ShowWindowCore()
        {
            s_windowVisible = true;
            s_shownAtMs = Environment.TickCount64;
            FluentApp.SetWindowVisible(true);
            nint hwnd = FluentApp.WindowHandle;
            if (hwnd == 0) return;
            if (IsIconic(hwnd)) NativeShowWindow(hwnd, SwRestore);   // a window hidden while minimized comes back minimized
            SetForegroundWindow(hwnd);
        }

        /// <summary>The explicit quit (the menu, <c>wavee://quit</c>): the latch first, so the close handler never turns it
        /// into a hide.</summary>
        public static void Quit()
        {
            s_quitRequested = true;
            Log.Info("tray", "quit requested");
            FluentApp.CloseWindow();
        }

        // ══ 4. THE ICON'S EVENTS AND THE MENU ══════════════════════════════════════════════════════════════════════

        /// <summary>The engine's event → <see cref="IconEvent"/>, by name (pinned by a test, so an engine addition fails
        /// loudly instead of mapping to the wrong verb).</summary>
        public static bool TryEventOf(NotifyIconEvent e, out IconEvent mapped)
        {
            switch (e)
            {
                case NotifyIconEvent.Select: mapped = IconEvent.Select; return true;
                case NotifyIconEvent.KeySelect: mapped = IconEvent.KeySelect; return true;
                case NotifyIconEvent.DoubleClick: mapped = IconEvent.DoubleClick; return true;
                case NotifyIconEvent.MiddleClick: mapped = IconEvent.MiddleClick; return true;
                case NotifyIconEvent.ContextMenu: mapped = IconEvent.ContextMenu; return true;
                case NotifyIconEvent.Recreated: mapped = IconEvent.Recreated; return true;
                case NotifyIconEvent.ShellChanged: mapped = IconEvent.ShellChanged; return true;
                default: mapped = default; return false;
            }
        }

        /// <summary>A CORE row → the engine's native row. The ids pass through: 0 stays a separator and the now-playing
        /// caption's negative id is one the engine greys and can never return. Text passes verbatim (Tray.cs already
        /// doubled the <c>&amp;</c> in track data; the engine must not escape it again).</summary>
        public static NotifyMenuItem ItemOf(in MenuRow row) => new((int)row.Id, row.Text, row.Enabled, row.Checked, row.Default);

        static void OnIcon(NotifyIconEvent e, int x, int y)
        {
            if (s_down || !TryEventOf(e, out IconEvent ev)) return;
            WindowFacts w = LiveWindow();
            bool anyHide = AnyHideModeOn();
            Facts f = s_facts with { Auth = Shell.Auth.Peek() };
            TrayAction action = OnIconEvent(ev, in w, anyHide, in f);
            // ALWAYS-ON attribution (the SMTC button's rule): a window that appears or vanishes with nothing on screen
            // to explain it must be explainable from the log.
            Log.Info("tray", "icon " + ev + " -> " + action);
            Run(action, x, y, in w, anyHide);
        }

        static void Run(TrayAction action, int x, int y, in WindowFacts w, bool anyHide)
        {
            switch (action)
            {
                case TrayAction.ShowWindow:
                case TrayAction.Foreground: ShowWindow(); break;   // the same restore path; harmless on a visible window
                case TrayAction.HideWindow: HideWindow(); break;
                case TrayAction.ShowMenu: ShowMenuAt(x, y, in w, anyHide); break;
                case TrayAction.Refresh: Refresh(force: true); SyncPresence(); break;
                case TrayAction.TogglePlay: Playback.TogglePlay(); break;
                case TrayAction.Next: Playback.Next(); break;
                case TrayAction.Previous: Playback.Previous(); break;
                case TrayAction.ToggleLike: ToggleCurrentSaved?.Invoke(); break;
                case TrayAction.ShowDevices: ShowWindow(); Playback.RequestDevicePicker(); break;
                case TrayAction.HideIcon:
                    Platform.Settings.Set(Platform.Keys.TrayIconMode, (int)IconMode.Never);
                    OnSettingsChanged();
                    break;
                case TrayAction.Quit: Quit(); break;
            }
        }

        static void ShowMenuAt(int x, int y, in WindowFacts w, bool anyHide)
        {
            if (s_icon is not { } icon || s_inMenu) return;
            bool? saved = ToggleCurrentSaved is not null && IsCurrentSaved is { } probe ? probe() : null;
            Facts f = s_facts with { Auth = Shell.Auth.Peek(), UpdateReady = s_updateReady, Saved = saved };
            int n = Menu(in f, in s_words, in w, anyHide, s_rows);
            for (int i = 0; i < n; i++) s_items[i] = ItemOf(in s_rows[i]);

            int id;
            s_inMenu = true;   // the menu's modal loop pumps this thread: a second ContextMenu must not nest a menu
            try { id = icon.ShowMenu(s_items.AsSpan(0, n), x, y); }
            catch (Exception ex) { Log.Warn("tray", "menu failed", ex); return; }
            finally { s_inMenu = false; }
            if (id == 0) return;

            TrayAction action = OnMenu((MenuId)id, in w, anyHide);   // against the window the menu was built from
            Log.Info("tray", "menu " + (MenuId)id + " -> " + action);
            if (action != TrayAction.ShowMenu) Run(action, 0, 0, in w, anyHide);
        }

        /// <summary>The live window at the event (never a cached one — the thumb click's rule).</summary>
        static WindowFacts LiveWindow()
        {
            nint hwnd = FluentApp.WindowHandle;
            long now = Environment.TickCount64;
            bool frontNow = hwnd != 0 && IsWavee(GetForegroundWindow(), hwnd);
            return new WindowFacts(
                Visible: FluentApp.WindowVisible,
                Minimized: hwnd != 0 && IsIconic(hwnd),
                Foreground: ForegroundAtClick(frontNow, s_foregroundHook != 0 ? s_foreground.MsSinceDeactivated(now) : -1),
                MsSinceShown: s_shownAtMs == long.MinValue ? -1 : now - s_shownAtMs,
                DoubleClickMs: GetDoubleClickTime());
        }

        // ══ 5. THE FOREGROUND HOOK (only while a hide mode is on — the only time "in front" changes a verdict) ════════

        static void SyncForegroundHook()
        {
            bool want = !s_down && s_icon is not null && AnyHideModeOn();
            if (want == (s_foregroundHook != 0)) return;
            if (!want)
            {
                UnhookWinEvent(s_foregroundHook);
                s_foregroundHook = 0;
                return;
            }
            nint hwnd = FluentApp.WindowHandle;
            s_foreground.Seed(hwnd != 0 && IsWavee(GetForegroundWindow(), hwnd));
            delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc = &OnForegroundChanged;
            s_foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, (nint)proc, 0, 0, WinEventOutOfContext);
            if (s_foregroundHook == 0) Log.Warn("tray", "foreground hook refused: a click on the icon will not hide a focused Wavee");
        }

        /// <summary>Delivered on this (the installing, pumping) thread. Allocation-free.</summary>
        [UnmanagedCallersOnly]
        static void OnForegroundChanged(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint timeMs)
        {
            nint main = FluentApp.WindowHandle;
            s_foreground.Observe(main != 0 && IsWavee(hwnd, main), Environment.TickCount64);
        }

        /// <summary>The main window, or a window it owns (a popup that took activation is still Wavee in front).</summary>
        static bool IsWavee(nint hwnd, nint main) => hwnd != 0 && (hwnd == main || GetAncestor(hwnd, GaRootOwner) == main);

        const int SwRestore = 9;
        const uint GaRootOwner = 3, EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000;

        [LibraryImport("user32.dll")]
        private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint winEventProc,
            uint idProcess, uint idThread, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWinEvent(nint hook);

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial nint GetAncestor(nint hwnd, uint flags);

        [LibraryImport("user32.dll")]
        private static partial uint GetDoubleClickTime();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeShowWindow(nint hwnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint hwnd);
    }
}
