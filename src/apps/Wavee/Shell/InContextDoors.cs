// ── Shell/InContextDoors.cs — the doors to a setting's in-context homes (the Settings rows' "Also in", settings rework phase 5) ──────────
//
// Role: CORE (handoff/settings-2026-11-18/DESIGN.md 5.1, 5.4). A dial drawn in place somewhere else names that surface in its `Contexts`;
// its Settings row says "Also in: …" and each place is a link that opens it NOW (`SettingsPageModel.DoorOf`, which keeps the queue, the full
// screen, Liked Songs and the logs). These are the rest: each opens the place where the dial's control stands, through the verb that place
// already answers to, never a second path:
//   lyrics.pane      the panel on its lyrics mode (the ⋯ in its header holds the blur)
//   devicePicker     the player bar's picker (the request a toast's "Choose device" already makes)
//   profile.menu     the title bar chip's menu (`Shell.ProfileMenuRequest`)
//   miniPlayer       the mini player (its ⋯ › Options holds the face, keep on top and the colour)
//   trackList.menu   Liked Songs, a track list whose ⋯ holds the columns
//   stage.upNext     the full screen on its Up next column (its Autoplay and Crossfade switches)
//   playerBar        the bar is always on screen: keyboard focus lands on the remaining-time flip (`Shell.PlayerBarFocusRequest`)
//   titleBar.menu    the title bar's own window menu under the bar (`Shell.TitleBarMenuRequest`; only a theme whose option names it has one)
//   home.daylist     Home's All facet with the daylist tile's options open (its Clock and "Tell me when it refreshes", `HomeVerbs.OpenDaylistOptions`)
// A surface with no door here (the tray menu, a dialog, the video's menu) prints its place without a link.
//
// NO DEAD DOOR (owner rule: no dead controls). A door is handed out only while its place can open in the window as it is now; otherwise the
// "Also in" entry is plain text. Each place says so from what is MOUNTED, never from which theme is active:
//   devicePicker   something answers the picker request (`Playback.DevicePickerRequest` has a subscriber: a bar's Devices button, a sheet that
//                  answers for it); a Compact bar with no Devices anywhere has none
//   profile.menu   a profile chip is mounted: the title bar's opens its menu; with only the narrow drawer's (its pane closed), the door opens the
//                  drawer first and that chip opens the menu
//   playerBar      a remaining-time label that takes the door is mounted: the bar's, else the now-playing panel's (the door then shows the panel on
//                  Now playing first); a Compact bar has none
//   titleBar.menu  the title bar answers the window menu request (only a structure with such a menu subscribes to it)
//   home.daylist   Home holds a daylist tile (the Home document, a subscribing read: the row re-reads when the tile comes or goes)
// Whatever mounts or unmounts one of these moves `Epoch` (from an effect), and `Of` reads it, so a Settings body that asks re-reads its doors.
//
// The other way, a link from an in-context surface INTO Settings (the device picker's quality row and "Sound settings", the queue's crossfade
// length, the lyrics ⋯'s AI line) goes through `OpenSettings`: where the surface covers the page (the panel floating or as a sheet, the full
// screen) it gives way first, so the Settings page the link opens is seen; a docked panel stays beside it.

namespace Wavee;

/// <summary>The doors of the in-context homes (see the file header).</summary>
internal static class InContextDoors
{
    static readonly FluentGpu.Signals.Signal<int> s_epoch = new(0);
    static int s_barChips, s_drawerChips, s_barLabels, s_panelLabels;
    static bool s_pickerAnswered;

    /// <summary>Moves whenever a door's place comes or goes (see the file header); <see cref="Of"/> subscribes its caller to it.</summary>
    public static FluentGpu.Signals.IReadSignal<int> Epoch => s_epoch;

    static void Moved() => s_epoch.Value = s_epoch.Peek() + 1;

    /// <summary>A profile chip mounted (+1) or unmounted (-1): the title bar's, or the narrow drawer's. Call from an effect.</summary>
    internal static void ChipMounted(bool drawer, int delta)
    {
        if (drawer) s_drawerChips += delta;
        else s_barChips += delta;
        Moved();
    }

    /// <summary>A remaining-time label that takes the player bar's door mounted (+1) or unmounted (-1). Call from an effect.</summary>
    internal static void LabelMounted(Shell.TimeDoor kind, int delta)
    {
        if (kind == Shell.TimeDoor.Bar) s_barLabels += delta;
        else if (kind == Shell.TimeDoor.Panel) s_panelLabels += delta;
        Moved();
    }

    /// <summary>The bar's labels take the door when one is mounted; the now-playing panel's only when none is.</summary>
    internal static bool BarLabelsMounted => s_barLabels > 0;

    /// <summary>Whether the profile menu door goes through the narrow drawer (no title bar chip is mounted).</summary>
    internal static bool ProfileViaDrawer => s_barChips == 0;

    /// <summary>The bar re-rendered (a level or a tier crossing may have mounted or unmounted its Devices button): note whether anything still
    /// answers the picker request. Call from an effect.</summary>
    internal static void NotePickerAnswerers()
    {
        bool answered = Playback.DevicePickerRequest.HasSubscribers;
        if (answered == s_pickerAnswered) return;
        s_pickerAnswered = answered;
        Moved();
    }

    static readonly Action s_lyricsPane = static () =>
    {
        if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics)) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
    };

    static readonly Action s_devicePicker = static () => Playback.RequestDevicePicker();

    static readonly Action s_profileMenu = static () =>
    {
        bool viaDrawer = ProfileViaDrawer;
        if (viaDrawer) Sidebar.OpenPane();   // the chip lives in the narrow drawer: the drawer opens first, then its chip opens the menu
        Shell.ProfileMenuViaDrawer = viaDrawer;
        Shell.ProfileMenuRequest.Value = Shell.ProfileMenuRequest.Peek() + 1;
    };

    static readonly Action s_miniPlayer = static () =>
    {
        if (!MiniPlayer.IsOpen.Peek()) MiniPlayer.Toggle("settings-also-in");
    };

    static readonly Action s_trackList = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));

    static readonly Action s_stageUpNext = static () =>
    {
        Prefs.Stage.SetMode((int)Stage.Mode.Queue);
        PlayerVerbs.Default.OpenStage(null, "settings-also-in");
    };

    static readonly Action s_playerBar = static () =>
    {
        // No bar label (a Compact bar has none): the now-playing panel's label stands in, so the panel shows on Now playing first (a sheet slides
        // up), then its label takes the focus.
        if (s_barLabels == 0) Shell.PanelHost.Shared.SetMode(Shell.RailMode.NowPlaying);
        Shell.PlayerBarFocusRequest.Value = Shell.PlayerBarFocusRequest.Peek() + 1;
    };

    static readonly Action s_titleBar = static () => Shell.TitleBarMenuRequest.Value = Shell.TitleBarMenuRequest.Peek() + 1;

    static readonly Action s_homeDaylist = HomeUi.HomeVerbs.OpenDaylistOptions;

    /// <summary>Follow a settings link (<see cref="SettingsLinks"/>: a dial or a category) from an in-context surface (see the file header).</summary>
    public static void OpenSettings(string link)
    {
        if (Shell.Ui.RailOpen.Peek() && !Shell.Ui.RailFits.Peek()) Shell.PanelHost.Shared.Close();
        if (Shell.Ui.ImmersiveLyrics.Peek()) Stage.Close(null, "settings-link");
        SettingsLinks.Open(link);
    }

    /// <summary>The surfaces whose door is decided here (asked every time: their places come and go with the window).</summary>
    public static bool Handles(string surface) => surface is SettingsSurfaces.LyricsPane or SettingsSurfaces.DevicePicker or SettingsSurfaces.ProfileMenu
        or SettingsSurfaces.MiniPlayerOptions or SettingsSurfaces.TrackListMenu or SettingsSurfaces.StageUpNext or SettingsSurfaces.PlayerBar
        or SettingsSurfaces.TitleBarMenu or SettingsSurfaces.HomeDaylist;

    /// <summary>The door to <paramref name="surface"/> (a <see cref="SettingsSurfaces"/> name) while its place can open now, else null (the entry is
    /// plain text). Subscribes the caller to <see cref="Epoch"/>.</summary>
    public static Action? Of(string surface)
    {
        _ = s_epoch.Value;
        return surface switch
        {
            SettingsSurfaces.LyricsPane => s_lyricsPane,
            SettingsSurfaces.DevicePicker => Playback.DevicePickerRequest.HasSubscribers ? s_devicePicker : null,
            SettingsSurfaces.ProfileMenu => s_barChips + s_drawerChips > 0 ? s_profileMenu : null,
            SettingsSurfaces.MiniPlayerOptions => s_miniPlayer,
            SettingsSurfaces.TrackListMenu => s_trackList,
            SettingsSurfaces.StageUpNext => s_stageUpNext,
            SettingsSurfaces.PlayerBar => s_barLabels + s_panelLabels > 0 ? s_playerBar : null,
            SettingsSurfaces.TitleBarMenu => Shell.TitleBarMenuRequest.HasSubscribers ? s_titleBar : null,
            SettingsSurfaces.HomeDaylist => HomeUi.HomeVerbs.DaylistItemId() is not null ? s_homeDaylist : null,
            _ => null,
        };
    }
}
