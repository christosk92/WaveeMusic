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
//   playerBar        the bar is always on screen: keyboard focus lands on the remaining-time flip (`Shell.PlayerBarFocusRequest`)
//   titleBar.menu    the title bar's own window menu under the bar (`Shell.TitleBarMenuRequest`; only a theme whose option names it has one)
// A surface with no door here (the tray menu, a dialog, the video's menu) prints its place without a link.
//
// The other way, a link from an in-context surface INTO Settings (the device picker's quality row and "Sound settings", the queue's crossfade
// length, the lyrics ⋯'s AI line) goes through `OpenSettings`: where the surface covers the page (the panel floating or as a sheet, the full
// screen) it gives way first, so the Settings page the link opens is seen; a docked panel stays beside it.

namespace Wavee;

/// <summary>The doors of the in-context homes (see the file header).</summary>
internal static class InContextDoors
{
    static readonly Action s_lyricsPane = static () =>
    {
        if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics)) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
    };

    static readonly Action s_devicePicker = static () => Playback.RequestDevicePicker();

    static readonly Action s_profileMenu = static () => Shell.ProfileMenuRequest.Value = Shell.ProfileMenuRequest.Peek() + 1;

    static readonly Action s_miniPlayer = static () =>
    {
        if (!MiniPlayer.IsOpen.Peek()) MiniPlayer.Toggle("settings-also-in");
    };

    static readonly Action s_trackList = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));

    static readonly Action s_playerBar = static () => Shell.PlayerBarFocusRequest.Value = Shell.PlayerBarFocusRequest.Peek() + 1;

    static readonly Action s_titleBar = static () => Shell.TitleBarMenuRequest.Value = Shell.TitleBarMenuRequest.Peek() + 1;

    /// <summary>Follow a settings link (<see cref="SettingsLinks"/>: a dial or a category) from an in-context surface (see the file header).</summary>
    public static void OpenSettings(string link)
    {
        if (Shell.Ui.RailOpen.Peek() && !Shell.Ui.RailFits.Peek()) Shell.PanelHost.Shared.Close();
        if (Shell.Ui.ImmersiveLyrics.Peek()) Stage.Close(null, "settings-link");
        SettingsLinks.Open(link);
    }

    /// <summary>The door to <paramref name="surface"/> (a <see cref="SettingsSurfaces"/> name), or null where the place has none of its own.</summary>
    public static Action? Of(string surface) => surface switch
    {
        SettingsSurfaces.LyricsPane => s_lyricsPane,
        SettingsSurfaces.DevicePicker => s_devicePicker,
        SettingsSurfaces.ProfileMenu => s_profileMenu,
        SettingsSurfaces.MiniPlayerOptions => s_miniPlayer,
        SettingsSurfaces.TrackListMenu => s_trackList,
        SettingsSurfaces.PlayerBar => s_playerBar,
        SettingsSurfaces.TitleBarMenu => s_titleBar,
        _ => null,
    };
}
