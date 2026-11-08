// ── Theming/Contracts/ViewKeys.Panel.cs — the right panel and the Player family's other keys (themes plan 6.0, T6b) ──────────
//
// T6b publishes the panel (the right rail's body: the frame, the header, the arm per mode), the panel's friends body, the
// now-playing hero (the pinned tile with the style chip and its in-place picker), the device
// picker's menu and the on-media transport (the video overlays' strip). The lyrics and queue bodies are core hosts that mount their
// own keys (`Lyrics.View`, `Queue.Panel`), handed to the panel as builders; the video body is the template's own. The leftovers lane (P3)
// adds the lyrics header, the "Next up" rows and the lyrics peek (props in `PanelBitsProps.cs`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The right panel: the surface, the open/close slide, the header and the arm of the mode on show. Mounted by the frame.
    /// Props: <see cref="PanelProps"/>.</summary>
    public static readonly ViewKey<PanelProps> ShellPanel = new("Shell.Panel");

    /// <summary>The panel's friends body: the activity feed's rows, skeleton, offline, error and empty faces. Props: <see cref="FriendsProps"/>.</summary>
    public static readonly ViewKey<FriendsProps> PanelFriends = new("Panel.Friends");

    /// <summary>The now-playing hero: the pinned tile with its header ("Now playing" and the style chip), the stage (the player style, or the
    /// picker's carousel) and the picker's strip. Props: <see cref="NowPlayingHeroProps"/>.</summary>
    public static readonly ViewKey<NowPlayingHeroProps> NowPlayingHero = new("Player.NowPlayingHero");

    /// <summary>The device picker's menu body (the player bar's Devices button and "Playing on" line open it). Props: <see cref="DevicePickerProps"/>.</summary>
    public static readonly ViewKey<DevicePickerProps> DevicePicker = new("Player.DevicePicker");

    /// <summary>The on-media transport: the strip over a video hole (play, skip, seek, times, mute, volume, more). Props: <see cref="OnMediaTransportProps"/>.</summary>
    public static readonly ViewKey<OnMediaTransportProps> OnMediaTransport = new("Player.OnMediaTransport");

    /// <summary>The panel's lyrics header: the title, the second-line toggle, the AI sparkle, expand, the developer inspector, close.
    /// Mounted by the panel's lyrics arm. Props: <see cref="PanelLyricsHeaderProps"/>.</summary>
    public static readonly ViewKey<PanelLyricsHeaderProps> PanelLyricsHeader = new("Panel.LyricsHeader");

    /// <summary>The "Next up" rows on the shared media surface (the now-playing body and the video body place them). Props: <see cref="NextUpProps"/>.</summary>
    public static readonly ViewKey<NextUpProps> PanelNextUp = new("Panel.NextUp");

    /// <summary>The now-playing body's two-row lyrics peek (or the video note, or nothing). Mounted by the core hosts <c>Lyrics.PeekHost</c> /
    /// <c>PeekReel</c>. Props: <see cref="LyricsPeekProps"/>.</summary>
    public static readonly ViewKey<LyricsPeekProps> LyricsPeek = new("Player.LyricsPeek");
}
