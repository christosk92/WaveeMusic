// ── Themes/Zune/Structure/Rules/ZuneDevicePickerRules.cs — the numbers of Zune's device picker (the 2026-11-19 redesign) ─────────────────
//
// Data, not decisions: `Views\Player\ZuneDevicePicker.cs` reads them (WVTH003 allows literals only in `Rules\**`). Boards: the device picker
// concepts, handoff/device-picker-2026-11-19/boards/Z-Here.dc.html and Z-Elsewhere.dc.html: a 400-wide column padded 24/28/20/28, no boxes,
// "playing on" (12) over the device in the accent (36/44 light), its note (13), the volume line, the sections in light 22/28 words, the
// devices as 20/28 words with their note at the end (40 tall), one line when nothing else is nearby, and the footer line.

using FluentGpu.Foundation;

namespace Wavee.Themes.Zune;

/// <summary>The geometry and type of Zune's device picker.</summary>
public static class ZuneDevicePickerRules
{
    /// <summary>The column's width (bounded by the window less the overlay margin) and padding.</summary>
    public const float PanelW = 400f;
    public static readonly Edges4 Pad = new(28f, 24f, 28f, 20f);

    /// <summary>"playing on" and the meter beside it.</summary>
    public const float SmallSize = 12f, SmallLine = 16f, MeterH = 11f, MeterGap = 8f;

    /// <summary>The device in use: display type (never shrunk: a long name wraps), its weight and tracking, the gap above.</summary>
    public const float NameSize = 36f, NameLine = 44f, NameTracking = -0.5f, NameTop = 2f;
    public const ushort NameWeight = 200;

    /// <summary>The device's note (how it is connected, what plays).</summary>
    public const float NoteSize = 13f, NoteLine = 18f;

    /// <summary>The volume line: the gap above, between its parts, the word's column, the slider's thickness, the value's column.</summary>
    public const float VolumeTop = 16f, VolumeGap = 12f, VolumeLabelW = 52f, VolumeThickness = 16f, VolumeValueW = 22f;

    /// <summary>The way back while another device plays: the gap above and its height.</summary>
    public const float ActionTop = 14f, ActionH = 32f;

    /// <summary>A section's title (light 22/28) and the gap above it; the gap under it.</summary>
    public const float SectionSize = 22f, SectionLine = 28f, SectionTop = 24f, SectionGap = 6f;
    public const ushort SectionWeight = 300;

    /// <summary>A device row: least height, the gap, the name (20/28, light; regular in the accent while in use), the note at the end.</summary>
    public const float RowMinH = 40f, RowGap = 12f, RowNameSize = 20f, RowNameLine = 28f, RowNoteSize = 12f, RowNoteLine = 16f;
    public const ushort RowWeight = 300, RowOnWeight = 400;

    /// <summary>The footer line: the gap above and between its words.</summary>
    public const float FooterTop = 24f, FooterGap = 16f, FooterSize = 13f, FooterLine = 18f;
}
