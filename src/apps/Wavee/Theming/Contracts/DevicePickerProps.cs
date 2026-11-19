// ── Theming/Contracts/DevicePickerProps.cs — the props of `Player.DevicePicker` (themes plan 6.0, T6b; the redesign, contract 2.3) ──
//
// WHERE WAVEE PLAYS. The player bar's Devices button and its "Playing on" line open ONE picker (the bar's `ToggleDevicePicker` owns
// the overlay: upward, right-aligned, light-dismiss, focus trap, the picker-opened side effect). What goes in the overlay is this key.
//
// THE ANSWER IS DECIDED. `Shell.DevicePicker.Compose` reads the roster, the owner, this PC's outputs (named by
// `Playback.AudioDeviceNaming`: the device's own name, never Windows' "Headphones (… - Find My)"), the player's echo and the sound
// switches, and answers in words a theme reads (2.3): the "Playing on" card (the device in use, its connection, what plays, and while
// another device plays the way back), the two sections ("This PC": Match Windows, then the outputs; "Other devices": Spotify Connect),
// which comes first (the other devices while one of them plays), and the footer (the quality as a door to its dial, "Sound settings"
// with the equalizer and normalization at rest). Every row carries the verb `Shell.DeviceVerbs.Run` executes.
//
// `Entries` (1.0) is the same answer as one flat menu (a section's header as an information row, a divider, the "Sound settings"
// link), for a surface that draws a menu (the full screen's device menu). The view re-renders WHILE OPEN (a transfer landing moves the
// check in place); `Stamp` is the core's fingerprint of everything the answer was built from.

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>What kind of row a picker entry is.</summary>
public enum DevicePickerEntryKind : byte
{
    /// <summary>A divider between the two sections.</summary>
    Separator,
    /// <summary>A disabled row that states something: a section header, "Nothing else nearby".</summary>
    Info,
    /// <summary>A this-PC output, or Match Windows: choosing it persists the output and pulls playback here.</summary>
    Command,
    /// <summary>A Spotify Connect device: a radio, checked when it is the active device (choosing it transfers playback).</summary>
    Radio,

    /// <summary>A link into Settings (design 5.4): "Sound settings" with the equalizer and normalization state at rest in
    /// <see cref="DevicePickerEntry.Accelerator"/> (opening the Sound category). Always enabled, never checked; choosing it closes the
    /// picker. Appended (contract 1.117).</summary>
    Link,
}

/// <summary>One row of the picker.</summary>
/// <param name="Kind">The row's role.</param>
/// <param name="Label">The (capped) label: a device's own name, or the core's word ("Match Windows").</param>
/// <param name="Glyph">The icon glyph (the device's kind: headphones, speakers, a display, USB, a laptop, a phone, a TV), or empty.</param>
/// <param name="Enabled">The row can be chosen.</param>
/// <param name="IsChecked">The one in use: the chosen this-PC output (or Match Windows) while this PC plays, the active Connect device.</param>
/// <param name="Accelerator">The right-hand note ("Unavailable" on a this-PC row while local playback is unsupported; the sound state on
/// "Sound settings"), or null.</param>
/// <param name="Invoke">What choosing the row does, or null for a row that does nothing.</param>
public readonly record struct DevicePickerEntry(
    DevicePickerEntryKind Kind, string Label, string Glyph, bool Enabled, bool IsChecked, string? Accelerator, Action? Invoke)
{
    /// <summary>The label is the core's word ("Match Windows"), not a device's own name: a theme may re-case it (a device keeps its casing).
    /// Appended (contract 1.68).</summary>
    public bool Word { get; init; }

    /// <summary>A this-PC output that is the CHOSEN one, whether or not this computer plays (<see cref="IsChecked"/> adds that it does): a
    /// theme may mark it while playback is elsewhere. False on every other row. Appended (contract 1.73, audit B8).</summary>
    public bool Chosen { get; init; }

    /// <summary>The row's second line: how the device is connected ("Bluetooth", "USB", "HDMI", "This PC", "Spotify Connect"), with "Windows
    /// default" on the default output while another device plays; Match Windows says what it follows ("Follows the Windows default · now
    /// Chris's AirPods Max"). Null when there is nothing to say. Appended (contract 2.3).</summary>
    public string? Detail { get; init; }

    /// <summary>The output did not open the last time Wavee tried it: <see cref="Detail"/> says "Not responding". Appended (contract 2.3).</summary>
    public bool Unresponsive { get; init; }

    /// <summary>The row's stable identity (the endpoint id, the Connect device id, "match-windows"): a theme keys its rows on it, so a row
    /// keeps its node while the list re-renders. Appended (contract 2.3).</summary>
    public string Id { get; init; } = "";
}

/// <summary>The "Playing on" card: the device in use (contract 2.3).</summary>
public sealed record DevicePickerCard
{
    /// <summary>The device's plain name, as written ("Chris's AirPods Max", "Built-in speakers", "Living room TV").</summary>
    public required string Name { get; init; }

    /// <summary>The device kind's icon glyph.</summary>
    public required string Glyph { get; init; }

    /// <summary>How it is connected ("Bluetooth", "USB", "This PC", "Spotify Connect"), or "" when the OS did not say.</summary>
    public required string Connection { get; init; }

    /// <summary>While this PC plays: what the player opened, in the setting's words ("Very high · 320 kbps"); null while another device plays
    /// or before anything opened.</summary>
    public string? Quality { get; init; }

    /// <summary>Another device (Spotify Connect) plays.</summary>
    public bool Remote { get; init; }

    /// <summary>The output in use did not open the last time Wavee tried it.</summary>
    public bool Unresponsive { get; init; }

    /// <summary>While another device plays: bring playback back to this PC, on <see cref="HereName"/>. Null while this PC plays.</summary>
    public Action? PlayHere { get; init; }

    /// <summary>The output <see cref="PlayHere"/> plays on (the chosen one, else the Windows default), or null when this PC has none.</summary>
    public string? HereName { get; init; }

    /// <summary>That output's icon glyph, or null.</summary>
    public string? HereGlyph { get; init; }
}

/// <summary>The picker's footer: the quality and the sound settings, each a door into Settings (contract 2.3).</summary>
public sealed record DevicePickerFooter
{
    /// <summary>While this PC plays: the quality's words for its chip ("Very high · 320 kbps", or "Quality" before anything opened); null
    /// while another device plays.</summary>
    public string? Quality { get; init; }

    /// <summary>Open Settings at the audio quality dial; null with <see cref="Quality"/>.</summary>
    public Action? OpenQuality { get; init; }

    /// <summary>While another device plays: who sets the quality ("Quality is set by the TV"); null while this PC plays.</summary>
    public string? QualitySetBy { get; init; }

    /// <summary>The sound settings at rest ("Equalizer off · Normalization on").</summary>
    public required string Sound { get; init; }

    /// <summary>Open Settings at the Sound category.</summary>
    public required Action OpenSound { get; init; }
}

/// <summary>What one render of the picker draws.</summary>
/// <param name="Entries">The whole answer as one flat menu, in menu order (a menu-shaped surface draws it).</param>
/// <param name="Stamp">A fingerprint of everything the answer was built from; it changes when a row, a check or a label could.</param>
public sealed record DevicePickerView(IReadOnlyList<DevicePickerEntry> Entries, int Stamp)
{
    /// <summary>The "Playing on" card. Appended (contract 2.3).</summary>
    public DevicePickerCard? Card { get; init; }

    /// <summary>"This PC": Match Windows first (while this PC plays), then the outputs. Appended (contract 2.3).</summary>
    public IReadOnlyList<DevicePickerEntry> ThisPc { get; init; } = [];

    /// <summary>"Other devices": the Spotify Connect devices, this one filtered out; empty when none is nearby. Appended (contract 2.3).</summary>
    public IReadOnlyList<DevicePickerEntry> Others { get; init; } = [];

    /// <summary>The other devices come first: one of them plays. Appended (contract 2.3).</summary>
    public bool OthersFirst { get; init; }

    /// <summary>The name of the output Windows plays on by default (what Match Windows follows), or null. Appended (contract 2.3).</summary>
    public string? WindowsDefault { get; init; }

    /// <summary>The footer. Appended (contract 2.3).</summary>
    public DevicePickerFooter? Footer { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.DevicePicker"/>. Built per open (it carries that open's close verb).</summary>
public sealed record DevicePickerProps
{
    /// <summary>The decided view; call it inside a render (it subscribes the roster, the owner, the active device and the outputs).</summary>
    public required Func<DevicePickerView> Read { get; init; }

    /// <summary>The tallest the menu may be at a window height: the roster scrolls well before the window edge.</summary>
    public required Func<float, float> MaxHeight { get; init; }

    /// <summary>Close the picker (an invoked row closes it).</summary>
    public required Action Close { get; init; }

    /// <summary>Playback is playing (the card's playing indicator moves while it does). Appended (contract 2.3).</summary>
    public IReadSignal<bool>? Playing { get; init; }

    /// <summary>The volume of the device in use: this PC's own, or the Connect device's while it plays. Appended (contract 2.3).</summary>
    public IReadSignal<float>? Volume { get; init; }

    /// <summary>The ONE cell the card's slider binds, mirrored from <see cref="Volume"/> by the template (its writes go to
    /// <see cref="SetVolume"/>). Appended (contract 2.3).</summary>
    public FloatSignal? VolumeCell { get; init; }

    /// <summary>Set the volume of the device in use (0..1; a Connect device's goes through Connect). Appended (contract 2.3).</summary>
    public Action<float>? SetVolume { get; init; }

    /// <summary>Look again: re-read this PC's outputs and wake the other devices so they announce themselves. Appended (contract 2.3).</summary>
    public Action? LookForDevices { get; init; }
}
