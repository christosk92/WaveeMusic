// ── Theming/Contracts/DevicePickerProps.cs — the props of `Player.DevicePicker` (themes plan 6.0, T6b) ──────────────────────
//
// THE DEVICE PICKER'S MENU. The player bar's Devices button and its "Playing on" line open ONE picker (the bar's
// `ToggleDevicePicker` owns the overlay: upward, right-aligned, light-dismiss, focus trap, the picker-opened side effect). What
// goes in the overlay is this key: a menu over the entries the core decided.
//
// THE ENTRIES ARE DECIDED. `Shell.DevicePicker.Items` composes the rows from the roster, the owner, the local outputs and the
// player's echo (this computer first, then Spotify Connect with this device filtered out; "none" is two disabled rows; the quality
// row is the player's own echo), and maps every click to a DATA intent that `Shell.DeviceVerbs.Run` executes. This record is that
// answer in words a theme can read: a separator, an information row (a section header, the quality echo, the no-devices hint), a
// command (a this-computer output) or a radio (a Connect device, checked when it is the active one), each with its glyph and, for
// the two that act, the verb.
//
// The view re-renders WHILE OPEN (a transfer landing swaps the checks in place) and re-keys its menu presenter on every change,
// because the presenter freezes its rows at mount: `Stamp` is the core's fingerprint of everything the entries were built from.

namespace Wavee.Theming.Contracts;

/// <summary>What kind of row a picker entry is.</summary>
public enum DevicePickerEntryKind : byte
{
    /// <summary>A divider between the two sections.</summary>
    Separator,
    /// <summary>A disabled row that states something: a section header, the quality echo, "no devices" and its hint.</summary>
    Info,
    /// <summary>A this-computer output: a plain command (choosing it persists the output and pulls playback here).</summary>
    Command,
    /// <summary>A Spotify Connect device: a radio, checked when it is the active device (choosing it transfers playback).</summary>
    Radio,
}

/// <summary>One row of the picker.</summary>
/// <param name="Kind">The row's role.</param>
/// <param name="Label">The (capped) label.</param>
/// <param name="Glyph">The icon glyph, or empty for none.</param>
/// <param name="Enabled">The row can be chosen.</param>
/// <param name="IsChecked">A radio's selected state (the active Connect device); on a this-computer output, that it is the one playing (this computer
/// is the active output and the output is the chosen one). A menu theme reads it on radios only.</param>
/// <param name="Accelerator">The right-hand note ("Unavailable" on a local row while local playback is unsupported), or null.</param>
/// <param name="Invoke">What choosing the row does, or null for a row that does nothing.</param>
public readonly record struct DevicePickerEntry(
    DevicePickerEntryKind Kind, string Label, string Glyph, bool Enabled, bool IsChecked, string? Accelerator, Action? Invoke)
{
    /// <summary>The label is the core's word ("System default"), not a device's own name: a theme may re-case it (a device keeps its casing).
    /// Appended (contract 1.68).</summary>
    public bool Word { get; init; }
}

/// <summary>What one render of the picker draws.</summary>
/// <param name="Entries">The rows, in menu order.</param>
/// <param name="Stamp">A fingerprint of everything the entries were built from; it changes when a row, a check or a label could.</param>
public sealed record DevicePickerView(IReadOnlyList<DevicePickerEntry> Entries, int Stamp);

/// <summary>The props of <see cref="ViewKeys.DevicePicker"/>. Built per open (it carries that open's close verb).</summary>
public sealed record DevicePickerProps
{
    /// <summary>The decided view; call it inside a render (it subscribes the roster, the owner, the active device and the outputs).</summary>
    public required Func<DevicePickerView> Read { get; init; }

    /// <summary>The tallest the menu may be at a window height: the roster scrolls well before the window edge.</summary>
    public required Func<float, float> MaxHeight { get; init; }

    /// <summary>Close the picker (an invoked row closes it).</summary>
    public required Action Close { get; init; }
}
