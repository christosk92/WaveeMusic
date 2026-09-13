// ── Wavee.Tests/TrayTests.cs — the notification-area icon's decisions, without a shell ─────────────────────────────
//
// W-B's gate for `Platform/Tray.cs` (docs/plans/wavee/wavee-0.3-tray-implementation.md §9, updated to §13's decisions:
// the icon defaults to ONLY WHILE HIDDEN, close- and minimize-to-tray ship OFF, middle click toggles play). No HWND, no
// Shell_NotifyIcon, no Loc tables: the host hands the words in, so these facts pass English and pin the FORMAT and the
// RULES, never a translation. The on-box checklist (§12.2) verifies the pixels and the shell; these stop a re-author
// "simplifying" a rule back into the classic tray bugs — the ghost window, the double-click that hides, the tip that
// overflows szTip, the NIM_MODIFY per position tick.

using Wavee;

using Xunit;

namespace Wavee.Tests;

static class TrayFixtures
{
    // Escapes, not literals: the em dash, the middle dot and the ellipsis the tooltip uses.
    public const string D = " \u2014 ", M = " \u00B7 ", E = "\u2026";

    public static readonly Tray.Words English = new(
        Paused: "Paused", Live: "LIVE", OnDevice: "on {device}", UpdateReady: "Update ready", SignedOut: "Signed out",
        Offline: "Offline", NothingPlaying: "Nothing playing", Play: "Play", Pause: "Pause", Next: "Next", Previous: "Previous",
        SaveToLiked: "Save to Liked Songs", Devices: "Devices" + E, OpenWavee: "Open Wavee", HideWavee: "Hide Wavee",
        HideIcon: "Hide icon", Quit: "Quit Wavee");

    public static Tray.Facts Playing(string title, string artist) => new(
        HasTrack: true, Playing: true, Buffering: false, IsLive: false, RemoteOwner: false, Title: title, Artist: artist,
        DeviceName: "", Auth: Shell.AuthState.Live, UpdateReady: false, CanSkipPrev: true, CanSkipNext: true, Saved: null);

    public static Tray.Facts Idle => Playing("", "") with { HasTrack = false, Playing = false, CanSkipPrev = false, CanSkipNext = false };

    public static string Tip(in Tray.Facts f) => Tip(in f, in English);

    public static string Tip(in Tray.Facts f, in Tray.Words w)
    {
        Span<char> buf = stackalloc char[Tray.TipMax];
        return new string(buf[..Tray.WriteTooltip(in f, in w, buf)]);
    }

    public static Tray.WindowFacts Window(bool visible = true, bool minimized = false, bool foreground = true, long msSinceShown = -1)
        => new(visible, minimized, foreground, msSinceShown, DoubleClickMs: 500);

    public static Tray.MenuRow[] MenuFor(in Tray.Facts f, in Tray.WindowFacts w, bool anyHideModeOn)
    {
        var rows = new Tray.MenuRow[Tray.MenuCapacity];
        return rows[..Tray.Menu(in f, in English, in w, anyHideModeOn, rows)];
    }
}

public class TrayPresenceTests
{
    [Theory]
    [InlineData(Tray.IconMode.Always, true, true)]
    [InlineData(Tray.IconMode.Always, false, true)]
    [InlineData(Tray.IconMode.WhileHidden, true, false)]
    [InlineData(Tray.IconMode.WhileHidden, false, true)]
    [InlineData(Tray.IconMode.Never, true, false)]
    [InlineData(Tray.IconMode.Never, false, false)]
    public void Icon_presence_follows_the_mode_and_the_window(Tray.IconMode mode, bool visible, bool shown)
        => Assert.Equal(shown, Tray.IconShown(mode, visible));

    [Fact]
    public void The_default_is_only_while_hidden_so_a_window_that_never_hides_keeps_the_tray_clean()
    {
        Assert.Equal(Tray.IconMode.WhileHidden, Tray.DefaultIconMode);
        Tray.IconMode stored = Tray.ModeFrom(Platform.Keys.TrayIconMode.Default);
        Assert.Equal(Tray.DefaultIconMode, stored);
        Assert.False(Tray.IconShown(stored, windowVisible: true));
        Assert.True(Tray.IconShown(stored, windowVisible: false));
    }

    [Fact]
    public void Stored_modes_are_the_wire_values()
    {
        Assert.Equal(Tray.IconMode.Always, Tray.ModeFrom(0));
        Assert.Equal(Tray.IconMode.WhileHidden, Tray.ModeFrom(1));
        Assert.Equal(Tray.IconMode.Never, Tray.ModeFrom(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(int.MinValue)]
    public void A_stored_mode_this_build_does_not_define_reads_as_the_default(int stored)
        => Assert.Equal(Tray.DefaultIconMode, Tray.ModeFrom(stored));

    [Fact]
    public void A_shell_that_refused_the_icon_host_is_read_as_never()
    {
        Assert.Equal(Tray.IconMode.Never, Tray.EffectiveMode(Tray.IconMode.Always, iconHostAvailable: false));
        Assert.Equal(Tray.IconMode.WhileHidden, Tray.EffectiveMode(Tray.IconMode.WhileHidden, iconHostAvailable: true));
        Tray.IconMode refused = Tray.EffectiveMode(Tray.IconMode.Always, iconHostAvailable: false);
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, false, refused, Tray.CloseCause.User));
    }

    [Fact]
    public void A_hidden_window_always_has_a_way_back()
    {
        Assert.True(Tray.IsGhost(windowVisible: false, iconShown: false));
        Assert.False(Tray.IsGhost(windowVisible: false, iconShown: true));
        Assert.False(Tray.IsGhost(windowVisible: true, iconShown: false));
        // Never picked while hidden: the icon goes, so the window must come back first (§12.2 check 10).
        Assert.True(Tray.IsGhost(false, Tray.IconShown(Tray.IconMode.Never, windowVisible: false)));
    }

    [Fact]
    public void Guid_identity_only_for_the_signed_packaged_install()
    {
        Assert.True(Tray.UseGuidIdentity(isPackaged: true));
        Assert.False(Tray.UseGuidIdentity(isPackaged: false));
    }

    [Fact]
    public void The_icon_guid_is_persisted_identity_and_never_changes()
        => Assert.Equal(new Guid("7C2B9E14-3A5D-4F86-9B0C-2D6E5A1F8C43"), Tray.IconGuid);
}

public class TraySettingsTests
{
    [Fact]
    public void The_four_keys_keep_their_names_and_ship_opt_in()
    {
        Assert.Equal("tray.icon.mode", Platform.Keys.TrayIconMode.Name);
        Assert.Equal((int)Tray.IconMode.WhileHidden, Platform.Keys.TrayIconMode.Default);
        Assert.Equal("tray.closeToTray", Platform.Keys.TrayCloseToTray.Name);
        Assert.False(Platform.Keys.TrayCloseToTray.Default);
        Assert.Equal("tray.minimizeToTray", Platform.Keys.TrayMinimizeToTray.Name);
        Assert.False(Platform.Keys.TrayMinimizeToTray.Default);
        Assert.Equal("tray.startHidden", Platform.Keys.TrayStartHidden.Name);
        Assert.False(Platform.Keys.TrayStartHidden.Default);
    }

    [Fact]
    public void Out_of_the_box_close_quits_and_minimize_iconifies()
    {
        Tray.IconMode mode = Tray.ModeFrom(Platform.Keys.TrayIconMode.Default);
        bool close = Platform.Keys.TrayCloseToTray.Default, minimize = Platform.Keys.TrayMinimizeToTray.Default;
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(close, false, mode, Tray.CloseCause.User));
        Assert.False(Tray.OnMinimized(minimize, mode));
        Assert.False(Tray.AnyHideModeOn(mode, close, minimize));
    }

    [Fact]
    public void Never_greys_the_hide_rows_and_start_hidden_also_needs_start_on_login()
    {
        Assert.False(Tray.HideModesAllowed(Tray.IconMode.Never));
        Assert.True(Tray.HideModesAllowed(Tray.IconMode.WhileHidden));
        Assert.True(Tray.HideModesAllowed(Tray.IconMode.Always));
        Assert.False(Tray.StartHiddenAvailable(startOnLogin: false, Tray.IconMode.Always));
        Assert.False(Tray.StartHiddenAvailable(startOnLogin: true, Tray.IconMode.Never));
        Assert.True(Tray.StartHiddenAvailable(startOnLogin: true, Tray.IconMode.WhileHidden));
    }
}

public class TrayCloseTests
{
    [Fact]
    public void Close_hides_only_when_opted_in_and_an_icon_can_bring_it_back()
    {
        Assert.Equal(Tray.CloseVerdict.Hide, Tray.OnCloseRequested(true, false, Tray.IconMode.Always, Tray.CloseCause.User));
        Assert.Equal(Tray.CloseVerdict.Hide, Tray.OnCloseRequested(true, false, Tray.IconMode.WhileHidden, Tray.CloseCause.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(false, false, Tray.IconMode.Always, Tray.CloseCause.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, false, Tray.IconMode.Never, Tray.CloseCause.User));
    }

    [Fact]
    public void An_explicit_quit_and_a_session_end_always_quit()
    {
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, true, Tray.IconMode.Always, Tray.CloseCause.User));
        Assert.Equal(Tray.CloseVerdict.Quit, Tray.OnCloseRequested(true, false, Tray.IconMode.Always, Tray.CloseCause.SessionEnding));
    }

    [Fact]
    public void Minimize_hides_only_when_opted_in_and_the_mode_allows_it()
    {
        Assert.True(Tray.OnMinimized(true, Tray.IconMode.WhileHidden));
        Assert.False(Tray.OnMinimized(false, Tray.IconMode.Always));
        Assert.False(Tray.OnMinimized(true, Tray.IconMode.Never));
    }

    [Fact]
    public void A_hide_mode_counts_only_while_the_icon_mode_allows_it()
    {
        Assert.True(Tray.AnyHideModeOn(Tray.IconMode.Always, closeToTray: true, minimizeToTray: false));
        Assert.True(Tray.AnyHideModeOn(Tray.IconMode.WhileHidden, closeToTray: false, minimizeToTray: true));
        Assert.False(Tray.AnyHideModeOn(Tray.IconMode.Never, closeToTray: true, minimizeToTray: true));
        Assert.False(Tray.AnyHideModeOn(Tray.IconMode.Always, closeToTray: false, minimizeToTray: false));
    }
}

public class TrayStartTests
{
    [Fact]
    public void Start_hidden_needs_the_setting_a_sign_in_launch_and_an_icon()
    {
        Assert.False(Tray.StartHidden(true, Tray.IconMode.WhileHidden, false, false));   // a Start-menu click still shows
        Assert.True(Tray.StartHidden(true, Tray.IconMode.WhileHidden, isStartupActivation: true, hasTrayArg: false));
        Assert.True(Tray.StartHidden(true, Tray.IconMode.Always, isStartupActivation: false, hasTrayArg: true));
        Assert.False(Tray.StartHidden(false, Tray.IconMode.Always, true, true));
        Assert.False(Tray.StartHidden(true, Tray.IconMode.Never, true, true));            // never into a tray with no icon
    }

    [Fact]
    public void The_run_value_carries_the_tray_flag_only_when_start_hidden_can_apply()
    {
        Assert.Equal("--tray", Tray.StartupArguments(true, Tray.IconMode.WhileHidden));
        Assert.Null(Tray.StartupArguments(false, Tray.IconMode.Always));
        Assert.Null(Tray.StartupArguments(true, Tray.IconMode.Never));
    }

    [Fact]
    public void The_flag_is_found_anywhere_in_the_arguments_and_in_any_case()
    {
        Assert.True(Tray.HasTrayArg(["--width", "900", "--TRAY"]));
        Assert.False(Tray.HasTrayArg(["--trayx", "tray"]));
        Assert.False(Tray.HasTrayArg([]));
    }

    [Fact]
    public void The_sign_in_launch_round_trips_through_the_run_value()
    {
        string? arg = Tray.StartupArguments(true, Tray.IconMode.WhileHidden);
        Assert.NotNull(arg);
        Assert.True(Tray.StartHidden(true, Tray.IconMode.WhileHidden, isStartupActivation: false, Tray.HasTrayArg([arg])));
    }
}

public class TrayClickTests
{
    [Fact]
    public void Select_shows_a_hidden_or_minimized_window()
    {
        Assert.Equal(Tray.TrayAction.ShowWindow, Tray.OnSelect(TrayFixtures.Window(visible: false, foreground: false), anyHideModeOn: false));
        Assert.Equal(Tray.TrayAction.ShowWindow, Tray.OnSelect(TrayFixtures.Window(minimized: true, foreground: false), anyHideModeOn: true));
    }

    [Fact]
    public void Select_fronts_a_visible_window_and_hides_a_focused_one_only_with_a_hide_mode()
    {
        Assert.Equal(Tray.TrayAction.Foreground, Tray.OnSelect(TrayFixtures.Window(foreground: false), anyHideModeOn: true));
        Assert.Equal(Tray.TrayAction.HideWindow, Tray.OnSelect(TrayFixtures.Window(foreground: true), anyHideModeOn: true));
        Assert.Equal(Tray.TrayAction.Foreground, Tray.OnSelect(TrayFixtures.Window(foreground: true), anyHideModeOn: false));
    }

    [Fact]
    public void The_second_click_of_a_double_click_never_hides_what_the_first_showed()
    {
        Assert.Equal(Tray.TrayAction.Foreground, Tray.OnSelect(TrayFixtures.Window(msSinceShown: 120), anyHideModeOn: true));
        Assert.Equal(Tray.TrayAction.HideWindow, Tray.OnSelect(TrayFixtures.Window(msSinceShown: 900), anyHideModeOn: true));
    }

    [Fact]
    public void A_click_that_took_the_foreground_from_Wavee_still_counts_as_foreground()
    {
        Assert.True(Tray.ForegroundAtClick(foregroundNow: false, msSinceDeactivated: 80));
        Assert.True(Tray.ForegroundAtClick(foregroundNow: true, msSinceDeactivated: -1));
        Assert.False(Tray.ForegroundAtClick(foregroundNow: false, msSinceDeactivated: 2000));
        Assert.False(Tray.ForegroundAtClick(foregroundNow: false, msSinceDeactivated: -1));
    }

    [Fact]
    public void Double_click_opens_and_never_hides()
    {
        var f = TrayFixtures.Playing("t", "a");
        Assert.Equal(Tray.TrayAction.ShowWindow, Tray.OnIconEvent(Tray.IconEvent.DoubleClick, TrayFixtures.Window(visible: false), true, in f));
        Assert.Equal(Tray.TrayAction.Foreground, Tray.OnIconEvent(Tray.IconEvent.DoubleClick, TrayFixtures.Window(), true, in f));
    }

    [Fact]
    public void Middle_click_toggles_play_only_with_a_track_while_signed_in()
    {
        var w = TrayFixtures.Window();
        var playing = TrayFixtures.Playing("t", "a");
        Assert.Equal(Tray.TrayAction.TogglePlay, Tray.OnIconEvent(Tray.IconEvent.MiddleClick, in w, false, in playing));
        Assert.Equal(Tray.TrayAction.None, Tray.OnIconEvent(Tray.IconEvent.MiddleClick, in w, false, TrayFixtures.Idle));
        Assert.Equal(Tray.TrayAction.None,
            Tray.OnIconEvent(Tray.IconEvent.MiddleClick, in w, false, playing with { Auth = Shell.AuthState.SignInRequired }));
    }

    [Fact]
    public void The_menu_key_opens_the_menu_keyboard_select_shows_and_shell_changes_refresh()
    {
        var hidden = TrayFixtures.Window(visible: false, foreground: false);
        var f = TrayFixtures.Idle;
        Assert.Equal(Tray.TrayAction.ShowMenu, Tray.OnIconEvent(Tray.IconEvent.ContextMenu, in hidden, false, in f));
        Assert.Equal(Tray.TrayAction.ShowWindow, Tray.OnIconEvent(Tray.IconEvent.KeySelect, in hidden, false, in f));
        Assert.Equal(Tray.TrayAction.Refresh, Tray.OnIconEvent(Tray.IconEvent.Recreated, in hidden, false, in f));
        Assert.Equal(Tray.TrayAction.Refresh, Tray.OnIconEvent(Tray.IconEvent.ShellChanged, in hidden, false, in f));
    }

    [Fact]
    public void A_jump_list_transport_verb_never_raises_the_window()
    {
        Assert.False(Tray.WakeFor(Shell.DeepLinkKind.Pause));
        Assert.False(Tray.WakeFor(Shell.DeepLinkKind.Resume));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.Open));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.Play));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.Report));
        Assert.True(Tray.WakeFor(Shell.DeepLinkKind.None));    // a bare second launch
    }
}

public class TrayGlyphTests
{
    [Fact]
    public void Playing_paused_and_connecting_share_the_normal_glyph()
    {
        Assert.Equal(Tray.Glyph.Normal, Tray.GlyphFor(Shell.AuthState.Live, updateReady: false));
        Assert.Equal(Tray.Glyph.Normal, Tray.GlyphFor(Shell.AuthState.Connecting, updateReady: false));
    }

    [Fact]
    public void Offline_and_signed_out_beat_update_and_update_badges_a_signed_in_icon()
    {
        Assert.Equal(Tray.Glyph.Offline, Tray.GlyphFor(Shell.AuthState.Offline, updateReady: true));
        Assert.Equal(Tray.Glyph.Offline, Tray.GlyphFor(Shell.AuthState.SignInRequired, updateReady: true));
        Assert.Equal(Tray.Glyph.Update, Tray.GlyphFor(Shell.AuthState.Live, updateReady: true));
    }

    [Theory]
    [InlineData(96u, 16)]
    [InlineData(120u, 20)]
    [InlineData(144u, 24)]
    [InlineData(192u, 32)]
    [InlineData(240u, 40)]
    [InlineData(288u, 48)]
    public void The_frame_is_SM_CXSMICON_at_the_taskbar_dpi_on_every_plateau(uint dpi, int px)
    {
        Assert.Equal(px, Tray.SmallIconMetric(dpi));
        Assert.Equal(px, Tray.IconFrameFor(dpi));
    }

    [Theory]
    [InlineData(0u, 16)]      // no DPI read: the 100 % frame
    [InlineData(106u, 20)]    // 110 %: 18 px wanted
    [InlineData(168u, 32)]    // 175 %: 28 px wanted
    [InlineData(216u, 40)]    // 225 %: 36 px wanted
    [InlineData(336u, 48)]    // 350 %: 56 px wanted, the largest frame shipped
    public void Between_plateaus_the_next_frame_up_is_loaded_so_the_shell_only_downsamples(uint dpi, int px)
        => Assert.Equal(px, Tray.IconFrameFor(dpi));

    [Fact]
    public void Every_shipped_frame_is_reachable()
    {
        foreach (int frame in Tray.Frames)
            Assert.Equal(frame, Tray.IconFrameFor((uint)(frame * 6)));   // frame px at 96 dpi per 16 px
    }

    [Theory]
    [InlineData(Tray.Glyph.Normal, Tray.TaskbarTheme.Dark, "wavee-on-dark-normal.ico")]
    [InlineData(Tray.Glyph.Offline, Tray.TaskbarTheme.Dark, "wavee-on-dark-offline.ico")]
    [InlineData(Tray.Glyph.Update, Tray.TaskbarTheme.Dark, "wavee-on-dark-update.ico")]
    [InlineData(Tray.Glyph.Normal, Tray.TaskbarTheme.Light, "wavee-on-light-normal.ico")]
    [InlineData(Tray.Glyph.Offline, Tray.TaskbarTheme.Light, "wavee-on-light-offline.ico")]
    [InlineData(Tray.Glyph.Update, Tray.TaskbarTheme.Light, "wavee-on-light-update.ico")]
    public void File_name_is_the_taskbar_variant_then_the_glyph(Tray.Glyph glyph, Tray.TaskbarTheme theme, string file)
        => Assert.Equal(file, Tray.IconFileName(glyph, theme));

    [Fact]
    public void A_light_taskbar_gets_the_dark_ink_whatever_the_app_theme()
    {
        Assert.Equal(Tray.TaskbarTheme.Light, Tray.ThemeFor(taskbarUsesLightTheme: true));
        Assert.Equal(Tray.TaskbarTheme.Dark, Tray.ThemeFor(taskbarUsesLightTheme: false));
    }

    [Fact]
    public void The_icon_key_changes_on_an_edge_and_only_on_an_edge()
    {
        Tray.IconKey a = Tray.IconFor(Shell.AuthState.Live, false, taskbarUsesLightTheme: false, taskbarDpi: 96);
        Assert.Equal(a, Tray.IconFor(Shell.AuthState.Connecting, false, false, 96));
        Assert.NotEqual(a, Tray.IconFor(Shell.AuthState.Live, false, true, 96));
        Assert.NotEqual(a, Tray.IconFor(Shell.AuthState.Live, false, false, 144));
        Assert.NotEqual(a, Tray.IconFor(Shell.AuthState.Offline, false, false, 96));
        Assert.Equal("wavee-on-dark-normal.ico", a.FileName);
        Assert.Equal(16, a.FramePx);
    }
}

public class TrayTooltipTests
{
    const string D = TrayFixtures.D, M = TrayFixtures.M, E = TrayFixtures.E;

    [Fact]
    public void Playing_reads_company_dash_title_dot_artist()
        => Assert.Equal("Wavee" + D + "Midnight City" + M + "M83", TrayFixtures.Tip(TrayFixtures.Playing("Midnight City", "M83")));

    [Fact]
    public void Paused_is_prefixed_and_a_remote_owner_is_suffixed()
    {
        var f = TrayFixtures.Playing("Midnight City", "M83") with { Playing = false };
        Assert.Equal("Wavee" + D + "Paused: Midnight City" + M + "M83", TrayFixtures.Tip(in f));
        f = f with { Playing = true, RemoteOwner = true, DeviceName = "Kitchen" };
        Assert.Equal("Wavee" + D + "Midnight City" + M + "M83" + M + "on Kitchen", TrayFixtures.Tip(in f));
    }

    [Fact]
    public void Buffering_and_loading_read_as_playing_so_the_tip_does_not_churn()
    {
        var playing = TrayFixtures.Playing("Midnight City", "M83");
        Assert.Equal(TrayFixtures.Tip(in playing), TrayFixtures.Tip(playing with { Playing = false, Buffering = true }));
    }

    [Fact]
    public void A_live_stream_and_a_waiting_update_are_suffixes()
    {
        var f = TrayFixtures.Playing("Radio 1", "") with { IsLive = true, UpdateReady = true };
        Assert.Equal("Wavee" + D + "Radio 1" + M + "LIVE" + M + "Update ready", TrayFixtures.Tip(in f));
    }

    [Fact]
    public void No_track_or_no_metadata_yet_is_the_company_alone()
    {
        Assert.Equal("Wavee", TrayFixtures.Tip(TrayFixtures.Idle));
        Assert.Equal("Wavee" + M + "Update ready", TrayFixtures.Tip(TrayFixtures.Idle with { UpdateReady = true }));
        Assert.Equal("Wavee", TrayFixtures.Tip(TrayFixtures.Playing("", "")));   // a foreign device's row not fetched yet
    }

    [Fact]
    public void Signed_out_and_offline_ignore_the_track()
    {
        var f = TrayFixtures.Playing("x", "y");
        Assert.Equal("Wavee" + D + "Signed out", TrayFixtures.Tip(f with { Auth = Shell.AuthState.SignInRequired }));
        Assert.Equal("Wavee" + D + "Offline" + M + "Update ready", TrayFixtures.Tip(f with { Auth = Shell.AuthState.Offline, UpdateReady = true }));
    }

    [Fact]
    public void Never_exceeds_szTip_and_cuts_the_title_before_the_artist()
    {
        string tip = TrayFixtures.Tip(TrayFixtures.Playing(new string('a', 200), new string('b', 40)));
        Assert.Equal(Tray.TipMax, tip.Length);
        Assert.StartsWith("Wavee" + D + "aaaa", tip);
        Assert.EndsWith(E + M + new string('b', 40), tip);    // the artist survived whole
    }

    [Fact]
    public void The_artist_is_cut_only_once_the_title_reached_its_floor()
    {
        string tip = TrayFixtures.Tip(TrayFixtures.Playing(new string('a', 200), new string('b', 200)));
        Assert.Equal("Wavee" + D + new string('a', 11) + E + M + new string('b', 103) + E, tip);
        Assert.Equal(Tray.TipMax, tip.Length);
    }

    [Fact]
    public void A_short_artist_survives_a_tight_room_whole()
    {
        var f = TrayFixtures.Playing(new string('a', 200), "M83") with { Playing = false, RemoteOwner = true, DeviceName = "Kitchen", UpdateReady = true };
        string tip = TrayFixtures.Tip(in f);
        Assert.True(tip.Length <= Tray.TipMax);
        Assert.StartsWith("Wavee" + D + "Paused: aaaa", tip);                              // the prefix is never cut
        Assert.EndsWith(E + M + "M83" + M + "on Kitchen" + M + "Update ready", tip);       // nor the suffixes
    }

    [Fact]
    public void A_long_device_name_is_capped_so_the_track_keeps_its_room()
    {
        var f = TrayFixtures.Playing("Midnight City", "M83") with { RemoteOwner = true, DeviceName = new string('d', 80) };
        Assert.Equal("Wavee" + D + "Midnight City" + M + "M83" + M + "on " + new string('d', 31) + E, TrayFixtures.Tip(in f));
    }

    [Fact]
    public void A_translation_without_the_device_slot_still_reads()
    {
        var f = TrayFixtures.Playing("Midnight City", "M83") with { RemoteOwner = true, DeviceName = "Kitchen" };
        var words = TrayFixtures.English with { OnDevice = "elsewhere" };
        Assert.Equal("Wavee" + D + "Midnight City" + M + "M83" + M + "elsewhere", TrayFixtures.Tip(in f, in words));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void A_cut_never_splits_a_surrogate_pair(string lead)
    {
        string notes = lead + string.Concat(Enumerable.Repeat("\U0001F3B5", 200));
        string tip = TrayFixtures.Tip(TrayFixtures.Playing(notes, "M83"));
        Assert.True(tip.Length <= Tray.TipMax);
        Assert.EndsWith(E + M + "M83", tip);
        for (int i = 0; i < tip.Length; i++)
        {
            if (char.IsHighSurrogate(tip[i])) Assert.True(i + 1 < tip.Length && char.IsLowSurrogate(tip[i + 1]), "lone high surrogate at " + i);
            if (char.IsLowSurrogate(tip[i])) Assert.True(i > 0 && char.IsHighSurrogate(tip[i - 1]), "lone low surrogate at " + i);
        }
    }

    [Fact]
    public void Only_a_changed_tip_is_pushed()
    {
        Assert.True(Tray.TipDiffers("Wavee", lastPushed: null));
        Assert.False(Tray.TipDiffers("Wavee", "Wavee"));
        Assert.True(Tray.TipDiffers("Wavee" + D + "x", "Wavee"));
    }

    [Fact]
    public void The_per_push_path_allocates_nothing()
    {
        var f = TrayFixtures.Playing(new string('a', 200), "M83") with { RemoteOwner = true, DeviceName = "Kitchen", UpdateReady = true };
        string last = TrayFixtures.Tip(in f);
        Span<char> buf = stackalloc char[Tray.TipMax];
        bool changed = false;
        for (int i = 0; i < 64; i++) Push(in f, buf, last, ref changed);   // warm every path first
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++) Push(in f, buf, last, ref changed);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(changed);   // the same facts, pushed 320 times, would have cost zero NIM_MODIFYs
    }

    static void Push(in Tray.Facts f, Span<char> buf, string last, ref bool changed)
    {
        int n = Tray.WriteTooltip(in f, in TrayFixtures.English, buf);
        changed |= Tray.TipDiffers(buf[..n], last);
        changed |= Tray.IconFor(f.Auth, f.UpdateReady, false, 144) != new Tray.IconKey(Tray.Glyph.Update, Tray.TaskbarTheme.Dark, 24);
    }
}

public class TrayMenuTests
{
    [Fact]
    public void Signed_out_collapses_to_its_status_open_hide_icon_quit()
    {
        var f = TrayFixtures.Playing("t", "a") with { Auth = Shell.AuthState.SignInRequired, Saved = true };
        Tray.MenuRow[] rows = TrayFixtures.MenuFor(in f, TrayFixtures.Window(visible: false), anyHideModeOn: false);
        Assert.Equal(new[] { Tray.MenuId.NowPlaying, Tray.MenuId.Separator, Tray.MenuId.Open, Tray.MenuId.Separator, Tray.MenuId.HideIcon, Tray.MenuId.Quit },
            rows.Select(r => r.Id));
        Assert.Equal("Signed out", rows[0].Text);
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public void Offline_collapses_the_same_way_under_its_own_status()
    {
        var f = TrayFixtures.Playing("t", "a") with { Auth = Shell.AuthState.Offline };
        Tray.MenuRow[] rows = TrayFixtures.MenuFor(in f, TrayFixtures.Window(visible: false), anyHideModeOn: false);
        Assert.Equal("Offline", rows[0].Text);
        Assert.DoesNotContain(rows, r => r.Id is Tray.MenuId.PlayPause or Tray.MenuId.Next or Tray.MenuId.Previous or Tray.MenuId.Devices);
    }

    [Fact]
    public void Signed_in_lists_now_playing_transport_like_devices_open_hide_icon_quit_and_fills_the_capacity()
    {
        var f = TrayFixtures.Playing("Midnight City", "M83") with { Saved = false };
        Tray.MenuRow[] rows = TrayFixtures.MenuFor(in f, TrayFixtures.Window(visible: false), anyHideModeOn: false);
        Assert.Equal(
            new[] { Tray.MenuId.NowPlaying, Tray.MenuId.Separator, Tray.MenuId.PlayPause, Tray.MenuId.Next, Tray.MenuId.Previous, Tray.MenuId.Like,
                    Tray.MenuId.Separator, Tray.MenuId.Devices, Tray.MenuId.Open, Tray.MenuId.Separator, Tray.MenuId.HideIcon, Tray.MenuId.Quit },
            rows.Select(r => r.Id));
        Assert.Equal(Tray.MenuCapacity, rows.Length);
        Assert.Equal("Midnight City" + TrayFixtures.M + "M83", rows[0].Text);
    }

    [Fact]
    public void Transport_rows_grey_rather_than_vanish()
    {
        Tray.MenuRow[] idle = TrayFixtures.MenuFor(TrayFixtures.Idle, TrayFixtures.Window(visible: false), false);
        Assert.Equal("Nothing playing", idle[0].Text);
        Assert.All(idle.Where(r => r.Id is Tray.MenuId.PlayPause or Tray.MenuId.Next or Tray.MenuId.Previous), r => Assert.False(r.Enabled));
        Assert.Equal(3, idle.Count(r => r.Id is Tray.MenuId.PlayPause or Tray.MenuId.Next or Tray.MenuId.Previous));

        var first = TrayFixtures.Playing("t", "a") with { CanSkipPrev = false };
        Tray.MenuRow[] rows = TrayFixtures.MenuFor(in first, TrayFixtures.Window(visible: false), false);
        Assert.False(rows.Single(r => r.Id == Tray.MenuId.Previous).Enabled);
        Assert.True(rows.Single(r => r.Id == Tray.MenuId.Next).Enabled);
        Assert.True(rows.Single(r => r.Id == Tray.MenuId.PlayPause).Enabled);
    }

    [Fact]
    public void The_play_row_label_follows_the_transport()
    {
        var playing = TrayFixtures.Playing("t", "a");
        Assert.Equal("Pause", TrayFixtures.MenuFor(in playing, TrayFixtures.Window(), false).Single(r => r.Id == Tray.MenuId.PlayPause).Text);
        Assert.Equal("Play", TrayFixtures.MenuFor(playing with { Playing = false }, TrayFixtures.Window(), false).Single(r => r.Id == Tray.MenuId.PlayPause).Text);
    }

    [Theory]
    [InlineData(true, false, true, "Hide Wavee")]
    [InlineData(true, false, false, "Open Wavee")]
    [InlineData(false, false, true, "Open Wavee")]
    [InlineData(true, true, true, "Open Wavee")]
    public void Open_is_the_bold_default_and_reads_hide_only_on_screen_with_a_hide_mode(bool visible, bool minimized, bool hideMode, string label)
    {
        var f = TrayFixtures.Playing("t", "a");
        var w = TrayFixtures.Window(visible: visible, minimized: minimized);
        Tray.MenuRow[] rows = TrayFixtures.MenuFor(in f, in w, hideMode);
        Tray.MenuRow open = Assert.Single(rows, r => r.Default);
        Assert.Equal(Tray.MenuId.Open, open.Id);
        Assert.Equal(label, open.Text);
        Assert.Equal(label == "Hide Wavee" ? Tray.TrayAction.HideWindow : Tray.TrayAction.ShowWindow, Tray.OnMenu(Tray.MenuId.Open, in w, hideMode));
    }

    [Fact]
    public void The_like_row_is_absent_until_the_seam_attaches_and_checked_when_saved()
    {
        var f = TrayFixtures.Playing("t", "a");
        Assert.DoesNotContain(TrayFixtures.MenuFor(in f, TrayFixtures.Window(), false), r => r.Id == Tray.MenuId.Like);
        Assert.True(TrayFixtures.MenuFor(f with { Saved = true }, TrayFixtures.Window(), false).Single(r => r.Id == Tray.MenuId.Like).Checked);
        Assert.False(TrayFixtures.MenuFor(f with { Saved = false }, TrayFixtures.Window(), false).Single(r => r.Id == Tray.MenuId.Like).Checked);
        Assert.DoesNotContain(TrayFixtures.MenuFor(TrayFixtures.Idle with { Saved = true }, TrayFixtures.Window(), false), r => r.Id == Tray.MenuId.Like);
    }

    [Fact]
    public void The_now_playing_line_fits_sixty_and_doubles_ampersands_for_the_native_menu()
    {
        Assert.Equal("Rock && Roll" + TrayFixtures.M + "Band", Tray.NowPlayingLine(TrayFixtures.Playing("Rock & Roll", "Band"), in TrayFixtures.English));
        Assert.True(Tray.NowPlayingLine(TrayFixtures.Playing(new string('a', 100), new string('b', 100)), in TrayFixtures.English).Length <= 60);
    }

    [Fact]
    public void A_chosen_row_maps_to_its_verb()
    {
        var w = TrayFixtures.Window(visible: false);
        Assert.Equal(Tray.TrayAction.TogglePlay, Tray.OnMenu(Tray.MenuId.PlayPause, in w, false));
        Assert.Equal(Tray.TrayAction.Next, Tray.OnMenu(Tray.MenuId.Next, in w, false));
        Assert.Equal(Tray.TrayAction.Previous, Tray.OnMenu(Tray.MenuId.Previous, in w, false));
        Assert.Equal(Tray.TrayAction.ToggleLike, Tray.OnMenu(Tray.MenuId.Like, in w, false));
        Assert.Equal(Tray.TrayAction.ShowDevices, Tray.OnMenu(Tray.MenuId.Devices, in w, false));
        Assert.Equal(Tray.TrayAction.HideIcon, Tray.OnMenu(Tray.MenuId.HideIcon, in w, false));
        Assert.Equal(Tray.TrayAction.Quit, Tray.OnMenu(Tray.MenuId.Quit, in w, false));
        Assert.Equal(Tray.TrayAction.None, Tray.OnMenu(Tray.MenuId.Separator, in w, false));   // dismissed
        Assert.Equal(Tray.TrayAction.None, Tray.OnMenu(Tray.MenuId.NowPlaying, in w, false));
    }
}
