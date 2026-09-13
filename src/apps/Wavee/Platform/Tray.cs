// ── Platform/Tray.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// The notification-area icon's DECISIONS: when it exists, which glyph and frame it shows, what the tooltip says, what a
// click or a key does, what a close / minimize / sign-in launch does, what the native menu holds. Pure: no engine type,
// no OS call, no clock, no Loc lookup (the host hands the words in), so every rule is a table in TrayTests.cs and
// `Platform/Tray.Host.cs` (SHELL, W-C) only binds.
//
// Role: CORE
// Owner: U
// Wave: 6 (lands independently of the UI waves; nothing here renders)
// Budget: 380 lines
// Spec: docs/plans/wavee/wavee-0.3-tray-implementation.md §4-§6, §8, §11 — §13's decision table overrides the rest
//
// §13, THE AUTHORITY: close-to-tray and minimize-to-tray ship OFF; the icon mode defaults to ONLY WHILE HIDDEN (not
// Always); middle click = play/pause stays; no mini-player flyout. CORE rules: the per-push path (IconFor, WriteTooltip,
// TipDiffers) allocates nothing; Menu() builds strings at human rate (one right-click). No LINQ, no closures, no boxing.

namespace Wavee;

public static partial class Tray
{
    // ══ 0. VOCABULARY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>`tray.icon.mode`. The VALUES are the wire (<see cref="Platform.Keys.TrayIconMode"/>): append only.</summary>
    public enum IconMode : byte { Always = 0, WhileHidden = 1, Never = 2 }

    /// <summary>§13 decision 2: the tray stays clean for users who never hide Wavee; Always is one pick away.</summary>
    public const IconMode DefaultIconMode = IconMode.WhileHidden;

    /// <summary>Three glyphs, deliberately: playing/paused is NOT a glyph state — it would flip on every skip (§6.2).</summary>
    public enum Glyph : byte { Normal, Offline, Update }

    /// <summary>The TASKBAR's theme (`SystemUsesLightTheme`), never the app theme: a dark taskbar with light apps is
    /// Windows 11's "Custom" mode and would otherwise get a black glyph on black (§2.3).</summary>
    public enum TaskbarTheme : byte { Dark, Light }

    public enum CloseCause : byte { User, SessionEnding }
    public enum CloseVerdict : byte { Quit, Hide }

    /// <summary>What the user did to the icon; the host maps the engine's NotifyIconEvent onto this 1:1.</summary>
    public enum IconEvent : byte { Select, KeySelect, DoubleClick, MiddleClick, ContextMenu, Recreated, ShellChanged }

    /// <summary>Everything the host executes. One enum for icon events AND menu commands, so the host has one switch.</summary>
    public enum TrayAction : byte
    { None, ShowWindow, HideWindow, Foreground, ShowMenu, Refresh, TogglePlay, Next, Previous, ToggleLike, ShowDevices, HideIcon, Quit }

    /// <summary>Menu ids. 0 is a separator (and TrackPopupMenu's "dismissed"); a negative id is a display-only row.</summary>
    public enum MenuId : int { NowPlaying = -1, Separator = 0, PlayPause = 1, Next, Previous, Like, Devices, Open, HideIcon, Quit }

    /// <summary>Everything the icon is derived from, snapshotted from `Playback.State` per push (read the STATE, never the
    /// engine player — ch 14 rule 1). <paramref name="Buffering"/> also carries the Loading phase; <paramref name="Saved"/>
    /// is null while the like seam is unattached.</summary>
    public readonly record struct Facts(
        bool HasTrack, bool Playing, bool Buffering, bool IsLive, bool RemoteOwner, string Title, string Artist, string DeviceName,
        Shell.AuthState Auth, bool UpdateReady, bool CanSkipPrev, bool CanSkipNext, bool? Saved);

    /// <summary>The live window, read at the event. <paramref name="Foreground"/> is <see cref="ForegroundAtClick"/>'s
    /// answer; <paramref name="MsSinceShown"/> is -1 when the window was never shown by the tray.</summary>
    public readonly record struct WindowFacts(bool Visible, bool Minimized, bool Foreground, long MsSinceShown, uint DoubleClickMs);

    /// <summary>The localized words, resolved by the host once per culture epoch. <paramref name="OnDevice"/> is a
    /// template carrying <c>{device}</c>.</summary>
    public readonly record struct Words(
        string Paused, string Live, string OnDevice, string UpdateReady, string SignedOut, string Offline,
        string NothingPlaying, string Play, string Pause, string Next, string Previous, string SaveToLiked,
        string Devices, string OpenWavee, string HideWavee, string HideIcon, string Quit);

    // ══ 1. PRESENCE ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A stored int this build does not define reads as the DEFAULT — never as Never, which would silently
    /// turn both hide modes off, and never as Always, which would put an icon in a tray the user kept clean.</summary>
    public static IconMode ModeFrom(int stored) => stored is >= 0 and <= 2 ? (IconMode)stored : DefaultIconMode;

    /// <summary>A shell that refused the icon host is, for every rule below, a user who picked Never (§11 fail-soft).</summary>
    public static IconMode EffectiveMode(IconMode stored, bool iconHostAvailable) => iconHostAvailable ? stored : IconMode.Never;

    public static bool IconShown(IconMode mode, bool windowVisible) => mode switch
    {
        IconMode.Always => true,
        IconMode.WhileHidden => !windowVisible,   // minimized is visible: the taskbar button is the way back
        _ => false,
    };

    /// <summary>A hidden window whose icon is not in the tray is a ghost only Task Manager can reach. The host asks this
    /// after every hide, presence sync and settings write, and shows the window when it answers true.</summary>
    public static bool IsGhost(bool windowVisible, bool iconShown) => !windowVisible && !iconShown;

    /// <summary>NIF_GUID only for a signed, stable install (NOTIFYICONDATAW › Troubleshooting): the packaged build is
    /// Trusted-Signing-signed; a dev tree is neither, and NIM_ADD would fail on its second exe path.</summary>
    public static bool UseGuidIdentity(bool isPackaged) => isPackaged;

    /// <summary>Minted once. The shell keys the user's "show on taskbar" preference on it: never change it.</summary>
    public static readonly Guid IconGuid = new("7C2B9E14-3A5D-4F86-9B0C-2D6E5A1F8C43");

    // ══ 2. CLOSE, MINIMIZE, START ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A hide mode is meaningless without an icon to come back through: Never forces both off (§4.1).</summary>
    public static bool HideModesAllowed(IconMode mode) => mode != IconMode.Never;

    public static bool AnyHideModeOn(IconMode mode, bool closeToTray, bool minimizeToTray)
        => HideModesAllowed(mode) && (closeToTray || minimizeToTray);

    /// <summary>An explicit quit (the latch the quit verbs set before closing) and a session end always quit.</summary>
    public static CloseVerdict OnCloseRequested(bool closeToTray, bool quitRequested, IconMode mode, CloseCause cause)
    {
        if (quitRequested || cause == CloseCause.SessionEnding) return CloseVerdict.Quit;
        return closeToTray && HideModesAllowed(mode) ? CloseVerdict.Hide : CloseVerdict.Quit;
    }

    public static bool OnMinimized(bool minimizeToTray, IconMode mode) => minimizeToTray && HideModesAllowed(mode);

    /// <summary>The unpackaged Run value's flag (the packaged StartupTask cannot carry arguments; E8 recognises it).</summary>
    public const string TrayArg = "--tray";

    /// <summary>Start hidden ONLY for a launch the user did not click, and never into a tray that will not show an icon.</summary>
    public static bool StartHidden(bool setting, IconMode mode, bool isStartupActivation, bool hasTrayArg)
        => setting && HideModesAllowed(mode) && (isStartupActivation || hasTrayArg);

    public static bool HasTrayArg(ReadOnlySpan<string> args)
    {
        foreach (string a in args) if (string.Equals(a, TrayArg, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What the unpackaged Run value carries after the exe path (engine E6); null = nothing.</summary>
    public static string? StartupArguments(bool startHiddenSetting, IconMode mode)
        => startHiddenSetting && HideModesAllowed(mode) ? TrayArg : null;

    /// <summary>The Settings row: greyed unless Wavee starts with Windows AND an icon can bring the window back.</summary>
    public static bool StartHiddenAvailable(bool startOnLogin, IconMode mode) => startOnLogin && HideModesAllowed(mode);

    // ══ 3. CLICKS AND KEYS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Clicking the notification area activates the taskbar, so by NIN_SELECT Wavee has already lost the
    /// foreground. A deactivation this recent was the click itself.</summary>
    public const int ClickActivationGraceMs = 500;

    public static bool ForegroundAtClick(bool foregroundNow, long msSinceDeactivated)
        => foregroundNow || msSinceDeactivated is >= 0 and <= ClickActivationGraceMs;

    /// <summary>Left click / Enter / Space. Hidden or minimized → show. Behind other windows, or no hide mode on → bring
    /// to front. In front with a hide mode on → hide, except for the second NIN_SELECT of a double-click whose first
    /// click just showed the window (a double-click must never flash Wavee and hide it again).</summary>
    public static TrayAction OnSelect(in WindowFacts w, bool anyHideModeOn)
    {
        if (!w.Visible || w.Minimized) return TrayAction.ShowWindow;
        if (!w.Foreground || !anyHideModeOn) return TrayAction.Foreground;
        if (w.MsSinceShown >= 0 && w.MsSinceShown < w.DoubleClickMs) return TrayAction.Foreground;
        return TrayAction.HideWindow;
    }

    /// <summary>The whole click/key table (§4.2). Double-click opens and never hides; middle click = play/pause (§13 #7).</summary>
    public static TrayAction OnIconEvent(IconEvent e, in WindowFacts w, bool anyHideModeOn, in Facts f) => e switch
    {
        IconEvent.Select or IconEvent.KeySelect => OnSelect(in w, anyHideModeOn),
        IconEvent.DoubleClick => !w.Visible || w.Minimized ? TrayAction.ShowWindow : TrayAction.Foreground,
        IconEvent.MiddleClick => CanTogglePlay(in f) ? TrayAction.TogglePlay : TrayAction.None,
        IconEvent.ContextMenu => TrayAction.ShowMenu,
        IconEvent.Recreated or IconEvent.ShellChanged => TrayAction.Refresh,
        _ => TrayAction.None,
    };

    public static bool SignedIn(Shell.AuthState auth) => auth is Shell.AuthState.Live or Shell.AuthState.Connecting;
    public static bool CanTogglePlay(in Facts f) => f.HasTrack && SignedIn(f.Auth);

    /// <summary>Which redirected deep links raise the window. An ALLOW-list, so a verb added later (`wavee://quit`)
    /// stays quiet until someone decides otherwise: a jump-list Pause must never raise a deliberately hidden Wavee.</summary>
    public static bool WakeFor(Shell.DeepLinkKind kind)
        => kind is Shell.DeepLinkKind.None or Shell.DeepLinkKind.Open or Shell.DeepLinkKind.Play or Shell.DeepLinkKind.Report;

    // ══ 4. THE GLYPH AND THE FRAME ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Offline beats Update: the fact that explains why nothing plays wins. Connecting keeps Normal.</summary>
    public static Glyph GlyphFor(Shell.AuthState auth, bool updateReady)
        => auth is Shell.AuthState.Offline or Shell.AuthState.SignInRequired ? Glyph.Offline
         : updateReady ? Glyph.Update
         : Glyph.Normal;

    public static TaskbarTheme ThemeFor(bool taskbarUsesLightTheme) => taskbarUsesLightTheme ? TaskbarTheme.Light : TaskbarTheme.Dark;

    static readonly string[] s_files =
    [
        "wavee-on-dark-normal.ico", "wavee-on-dark-offline.ico", "wavee-on-dark-update.ico",
        "wavee-on-light-normal.ico", "wavee-on-light-offline.ico", "wavee-on-light-update.ico",
    ];

    /// <summary>`assets/tray/wavee-{on-dark|on-light}-{normal|offline|update}.ico` — a light taskbar wants the dark ink.</summary>
    public static string IconFileName(Glyph glyph, TaskbarTheme theme)
        => s_files[(theme == TaskbarTheme.Light ? 3 : 0) + (glyph switch { Glyph.Offline => 1, Glyph.Update => 2, _ => 0 })];

    /// <summary>The frames every tray .ico carries (ops/build/generate-tray-icons.ps1).</summary>
    public static ReadOnlySpan<int> Frames => [16, 20, 24, 32, 40, 48];

    /// <summary>SM_CXSMICON at <paramref name="dpi"/> — MulDiv(16, dpi, 96), what the taskbar draws the icon at.</summary>
    public static int SmallIconMetric(uint dpi) => dpi == 0 ? 16 : (int)((16UL * dpi + 48UL) / 96UL);

    /// <summary>The frame to load (LoadImageW with an explicit size picks that ICONDIR entry): the metric itself on the
    /// six plateaus, else the next frame UP, so the shell only ever downsamples (175 % → 28 px → the 32 frame).</summary>
    public static int IconFrameFor(uint dpi)
    {
        int want = SmallIconMetric(dpi);
        foreach (int frame in Frames) if (frame >= want) return frame;
        return 48;
    }

    /// <summary>The icon push's identity. The host sends NIM_MODIFY(NIF_ICON) only when this changes.</summary>
    public readonly record struct IconKey(Glyph Glyph, TaskbarTheme Theme, int FramePx) { public string FileName => IconFileName(Glyph, Theme); }

    public static IconKey IconFor(Shell.AuthState auth, bool updateReady, bool taskbarUsesLightTheme, uint taskbarDpi)
        => new(GlyphFor(auth, updateReady), ThemeFor(taskbarUsesLightTheme), IconFrameFor(taskbarDpi));

    // ══ 5. THE TOOLTIP ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>szTip[128] including the terminator.</summary>
    public const int TipMax = 127;
    const int TipPartFloor = 12;         // a title or artist is never cut below this before the next rule applies
    const int DeviceNameMax = 32;        // the device name is capped, so the track keeps the room
    const int NowPlayingMax = 60;        // the menu's disabled now-playing row
    // Escapes, not literals (the WaveeIcons rule: the edit chain mangles raw non-ASCII). Em dash, middle dot, ellipsis.
    const string Company = "Wavee", Dash = " \u2014 ", Dot = " \u00B7 ", PausedSep = ": ", DeviceSlot = "{device}";
    const char Ellipsis = '\u2026';

    /// <summary>`Wavee`, `Wavee — Title · Artist[ · LIVE][ · on Device]`, `Wavee — Paused: …`, `Wavee — Signed out`,
    /// `Wavee — Offline`, each with ` · Update ready` when an update waits. At most <see cref="TipMax"/> chars, always.
    /// The prefix and the suffixes are reserved first; the title is cut first, then the artist, each to a 12-char floor,
    /// then the artist is dropped. Position is NOT in the string by design — that is what keeps NIM_MODIFY at one per
    /// edge. Allocation-free.</summary>
    public static int WriteTooltip(in Facts f, in Words w, Span<char> into)
    {
        Span<char> buf = into.Length > TipMax ? into[..TipMax] : into;
        int n = 0;
        Put(buf, ref n, Company);
        int suffix = f.UpdateReady ? Dot.Length + Len(w.UpdateReady) : 0;
        if (f.Auth is Shell.AuthState.SignInRequired or Shell.AuthState.Offline)
        {
            Put(buf, ref n, Dash);
            Put(buf, ref n, f.Auth == Shell.AuthState.Offline ? w.Offline : w.SignedOut);
        }
        else if (f.HasTrack && (Len(f.Title) > 0 || Len(f.Artist) > 0))
        {
            Put(buf, ref n, Dash);
            if (!f.Playing && !f.Buffering) { Put(buf, ref n, w.Paused); Put(buf, ref n, PausedSep); }
            bool device = f.RemoteOwner && Len(f.DeviceName) > 0;
            int nameLen = device ? CutLen(f.DeviceName, DeviceNameMax) : 0;
            int tail = (f.IsLive ? Dot.Length + Len(w.Live) : 0) + (device ? Dot.Length + TemplateLen(w.OnDevice, nameLen) : 0);
            PutFit(buf, ref n, f.Title, f.Artist, buf.Length - n - tail - suffix);
            if (f.IsLive) { Put(buf, ref n, Dot); Put(buf, ref n, w.Live); }
            if (device) { Put(buf, ref n, Dot); PutTemplate(buf, ref n, w.OnDevice, f.DeviceName, DeviceNameMax); }
        }
        if (f.UpdateReady) { Put(buf, ref n, Dot); Put(buf, ref n, w.UpdateReady); }
        return n;
    }

    /// <summary>The tip push's edge: ordinal, against the string the shell holds (null = nothing pushed yet).</summary>
    public static bool TipDiffers(ReadOnlySpan<char> next, string? lastPushed) => lastPushed is null || !next.SequenceEqual(lastPushed);

    static int Len(string? s) => s?.Length ?? 0;

    static void Put(Span<char> buf, ref int n, ReadOnlySpan<char> s)
    {
        int k = Math.Min(s.Length, buf.Length - n);
        if (k > 0) { s[..k].CopyTo(buf[n..]); n += k; }
    }

    /// <summary>Chars of <paramref name="s"/> kept by a cut to <paramref name="max"/> (the ellipsis excluded): never
    /// half a surrogate pair, never a trailing space before the ellipsis.</summary>
    static int CutKeep(ReadOnlySpan<char> s, int max)
    {
        int keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(s[keep - 1])) keep--;
        while (keep > 0 && char.IsWhiteSpace(s[keep - 1])) keep--;
        return keep;
    }

    static int CutLen(ReadOnlySpan<char> s, int max) => max <= 0 ? 0 : s.Length <= max ? s.Length : CutKeep(s, max) + 1;

    static void PutCut(Span<char> buf, ref int n, ReadOnlySpan<char> s, int max)
    {
        if (max <= 0) return;
        if (s.Length <= max) { Put(buf, ref n, s); return; }
        Put(buf, ref n, s[..CutKeep(s, max)]);
        if (n < buf.Length) buf[n++] = Ellipsis;
    }

    /// <summary>`title · artist` into <paramref name="room"/>: both whole; else the title cut toward its floor so the
    /// artist survives; else the artist cut to its floor too; else the title alone.</summary>
    static void PutFit(Span<char> buf, ref int n, ReadOnlySpan<char> title, ReadOnlySpan<char> artist, int room)
    {
        if (room <= 0) return;
        if (artist.IsEmpty || title.IsEmpty) { PutCut(buf, ref n, title.IsEmpty ? artist : title, room); return; }
        int forTitle = title.Length + Dot.Length + artist.Length <= room ? title.Length : Math.Max(TipPartFloor, room - Dot.Length - artist.Length);
        int forArtist = room - CutLen(title, forTitle) - Dot.Length;
        if (forArtist < Math.Min(TipPartFloor, artist.Length)) { PutCut(buf, ref n, title, room); return; }
        PutCut(buf, ref n, title, forTitle);
        Put(buf, ref n, Dot);
        PutCut(buf, ref n, artist, forArtist);
    }

    static int TemplateLen(string? template, int nameLen)
        => template is null ? 0 : template.Contains(DeviceSlot, StringComparison.Ordinal) ? template.Length - DeviceSlot.Length + nameLen : template.Length;

    static void PutTemplate(Span<char> buf, ref int n, string? template, string name, int nameMax)
    {
        ReadOnlySpan<char> t = template;
        int at = t.IndexOf(DeviceSlot, StringComparison.Ordinal);
        if (at < 0) { Put(buf, ref n, t); return; }
        Put(buf, ref n, t[..at]);
        PutCut(buf, ref n, name, nameMax);
        Put(buf, ref n, t[(at + DeviceSlot.Length)..]);
    }

    // ══ 6. THE MENU ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One native menu row. A <see cref="MenuId.Separator"/> row carries no text.</summary>
    public readonly record struct MenuRow(MenuId Id, string Text, bool Enabled = true, bool Checked = false, bool Default = false);

    /// <summary>The longest menu. The host keeps ONE reusable MenuRow[] this size (a row holds a string: no stackalloc).</summary>
    public const int MenuCapacity = 12;
    static bool OnScreen(in WindowFacts w) => w.Visible && !w.Minimized;

    /// <summary>The rows, top to bottom (§5.1); returns the count. Offline / signed out collapse to a status line + Open /
    /// Hide icon / Quit (transport REMOVED, not greyed); signed in, transport rows GREY like the thumbnail toolbar. "Open
    /// Wavee" is the bold default and reads "Hide Wavee" only while Wavee is on screen and a hide mode is on.</summary>
    public static int Menu(in Facts f, in Words w, in WindowFacts window, bool anyHideModeOn, Span<MenuRow> into)
    {
        int n = 0;
        if (!SignedIn(f.Auth))
        {
            into[n++] = new(MenuId.NowPlaying, f.Auth == Shell.AuthState.Offline ? w.Offline : w.SignedOut, Enabled: false);
            into[n++] = new(MenuId.Separator, "");
        }
        else
        {
            into[n++] = new(MenuId.NowPlaying, NowPlayingLine(in f, in w), Enabled: false);
            into[n++] = new(MenuId.Separator, "");
            into[n++] = new(MenuId.PlayPause, f.Playing ? w.Pause : w.Play, Enabled: f.HasTrack);
            into[n++] = new(MenuId.Next, w.Next, Enabled: f.HasTrack && f.CanSkipNext);
            into[n++] = new(MenuId.Previous, w.Previous, Enabled: f.HasTrack && f.CanSkipPrev);
            if (f.HasTrack && f.Saved is { } saved) into[n++] = new(MenuId.Like, w.SaveToLiked, Checked: saved);
            into[n++] = new(MenuId.Separator, "");
            into[n++] = new(MenuId.Devices, w.Devices);
        }
        bool offerHide = OnScreen(in window) && anyHideModeOn;
        into[n++] = new(MenuId.Open, offerHide ? w.HideWavee : w.OpenWavee, Default: true);
        into[n++] = new(MenuId.Separator, "");
        into[n++] = new(MenuId.HideIcon, w.HideIcon);
        into[n++] = new(MenuId.Quit, w.Quit);
        return n;
    }

    /// <summary>`Title · Artist` in 60 chars, or "Nothing playing". Track text is DATA, so a `&amp;` is doubled — the
    /// native menu would otherwise eat it as a mnemonic prefix ("Rock &amp; Roll" → "Rock _Roll").</summary>
    public static string NowPlayingLine(in Facts f, in Words w)
    {
        if (!f.HasTrack || (Len(f.Title) == 0 && Len(f.Artist) == 0)) return w.NothingPlaying;
        Span<char> fit = stackalloc char[NowPlayingMax];
        int n = 0;
        PutFit(fit, ref n, f.Title, f.Artist, NowPlayingMax);
        ReadOnlySpan<char> line = fit[..n];
        if (!line.Contains('&')) return new string(line);
        Span<char> escaped = stackalloc char[2 * NowPlayingMax];
        int j = 0;
        foreach (char c in line) { escaped[j++] = c; if (c == '&') escaped[j++] = '&'; }
        return new string(escaped[..j]);
    }

    /// <summary>A chosen row → what the host does. Decided against the same window the menu was built from (the menu is
    /// modal), so the Open row does exactly what its label said.</summary>
    public static TrayAction OnMenu(MenuId id, in WindowFacts window, bool anyHideModeOn) => id switch
    {
        MenuId.PlayPause => TrayAction.TogglePlay,
        MenuId.Next => TrayAction.Next,
        MenuId.Previous => TrayAction.Previous,
        MenuId.Like => TrayAction.ToggleLike,
        MenuId.Devices => TrayAction.ShowDevices,
        MenuId.Open => OnScreen(in window) && anyHideModeOn ? TrayAction.HideWindow : TrayAction.ShowWindow,
        MenuId.HideIcon => TrayAction.HideIcon,
        MenuId.Quit => TrayAction.Quit,
        _ => TrayAction.None,
    };
}
