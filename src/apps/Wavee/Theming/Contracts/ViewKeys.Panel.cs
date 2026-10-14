// ── Theming/Contracts/ViewKeys.Panel.cs — the right panel and the Player family's other keys (themes plan 6.0, T6b) ──────────
//
// T6b publishes the panel (the right rail's body: the frame, the header, the arm per mode), the panel's friends body, the
// now-playing hero (the pinned tile with the Cover / Player switch and its art menu), the deck's style flyout, the device
// picker's menu and the on-media transport (the video overlays' strip). The lyrics and queue bodies are core hosts that mount their
// own keys (`Lyrics.View`, `Queue.Panel`), handed to the panel as builders; the video body is the template's own.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The right panel: the surface, the open/close slide, the header and the arm of the mode on show. Mounted by the frame.
    /// Props: <see cref="PanelProps"/>.</summary>
    public static readonly ViewKey<PanelProps> ShellPanel = new("Shell.Panel");

    /// <summary>The panel's friends body: the activity feed's rows, skeleton, offline, error and empty faces. Props: <see cref="FriendsProps"/>.</summary>
    public static readonly ViewKey<FriendsProps> PanelFriends = new("Panel.Friends");

    /// <summary>The now-playing hero: the pinned tile with its header strip (eyebrow, Cover | Player switch, the style gear), the
    /// artwork or the deck, and the art menu. Props: <see cref="NowPlayingHeroProps"/>.</summary>
    public static readonly ViewKey<NowPlayingHeroProps> NowPlayingHero = new("Player.NowPlayingHero");

    /// <summary>The deck's style flyout: the preset shelves and the selected preset's options. Props: <see cref="DeckStyleFlyoutProps"/>.</summary>
    public static readonly ViewKey<DeckStyleFlyoutProps> DeckStyleFlyout = new("Player.DeckStyleFlyout");

    /// <summary>The device picker's menu body (the player bar's Devices button and "Playing on" line open it). Props: <see cref="DevicePickerProps"/>.</summary>
    public static readonly ViewKey<DevicePickerProps> DevicePicker = new("Player.DevicePicker");

    /// <summary>The on-media transport: the strip over a video hole (play, skip, seek, times, mute, volume, more). Props: <see cref="OnMediaTransportProps"/>.</summary>
    public static readonly ViewKey<OnMediaTransportProps> OnMediaTransport = new("Player.OnMediaTransport");
}
