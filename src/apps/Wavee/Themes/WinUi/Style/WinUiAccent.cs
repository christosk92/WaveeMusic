// ── Themes/WinUi/Style/WinUiAccent.cs — the accent of the WinUI style (themes plan 6.2, W1) ────────────────────────────────────
//
// `style.useWindowsAccent` (W-ThemeOptions, default ON): "On reads the system accent and its light/dark variants. Off uses the theme's own blue
// (#0078d4 ramp)." The theme's blue is the Windows 11 ramp of the default accent #0078D4, the shades W-Tokens paints: the fill #60cdff on dark
// and #005fb8 on light (`Tok.AccentDefault` is Light2 in dark, Dark1 in light), the accent text #99ebff on dark and #003e92 on light
// (`Tok.AccentTextPrimary` is Light3 in dark, Dark2 in light).
//
// WHY NOT `AccentRamp.FluentDefault`. That ramp is the engine's MARKER for "the baked per-kind accent tokens", and the neutral palette's baked
// accent TEXT is the Windows 10 shade (#a6d8ff / #004275), not the board's. So the style never hands the engine the marker: with the option off,
// and with it on while the OS accent is unknown (the marker is what the host passes before it reads the OS, and in a headless run), the
// definition carries this ramp.

using FluentGpu.Foundation;
using AccentRamp = FluentGpu.Dsl.AccentRamp;

namespace Wavee.Themes.WinUi;

/// <summary>The accent ramp of the WinUI style.</summary>
public static class WinUiAccent
{
    /// <summary>The theme's own blue: the Windows 11 ramp of #0078D4 (Light1 #0093f9, Light2 #60cdff, Light3 #99ebff; Dark1 #005fb8, Dark2 #003e92,
    /// Dark3 #001968).</summary>
    public static AccentRamp Default { get; } = new(
        Base: ColorF.FromRgba(0x00, 0x78, 0xD4),
        Light1: ColorF.FromRgba(0x00, 0x93, 0xF9), Light2: ColorF.FromRgba(0x60, 0xCD, 0xFF), Light3: ColorF.FromRgba(0x99, 0xEB, 0xFF),
        Dark1: ColorF.FromRgba(0x00, 0x5F, 0xB8), Dark2: ColorF.FromRgba(0x00, 0x3E, 0x92), Dark3: ColorF.FromRgba(0x00, 0x19, 0x68));

    /// <summary>The ramp the definition carries: the OS ramp when the style follows Windows and the host has read one, the theme's blue otherwise.</summary>
    public static AccentRamp RampFor(bool followsWindows, AccentRamp osAccent)
        => followsWindows && osAccent != AccentRamp.FluentDefault ? osAccent : Default;
}
