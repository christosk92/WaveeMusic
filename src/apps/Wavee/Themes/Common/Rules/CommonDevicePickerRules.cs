// ── Themes/Common/Rules/CommonDevicePickerRules.cs — the numbers of the shared device picker (WinUI and Classic; the 2026-11-19 redesign) ─
//
// Data, not decisions: `CommonDevicePicker.cs` reads them (WVTH003 allows literals only in `Rules\**`). Boards: the device picker concepts,
// handoff/device-picker-2026-11-19/boards/Main.dc.html (playing on this PC) and W-Elsewhere.dc.html (playing on a Connect device): a
// quick-settings flyout 392 wide, padded 12, a "Playing on" card, radio rows 48 tall, a footer under a full-bleed divider. The owner chose
// this one picker for Classic too (2026-11-19); each theme paints it with its own tokens (colours, corner radii, the radio's style).

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The geometry and type of the shared device picker.</summary>
internal static class CommonDevicePickerRules
{
    /// <summary>The picker's key (the inspector and the lint find it by it).</summary>
    public const string Key = "device-picker";

    /// <summary>The flyout's width, bounded by the window less its margin (8 at Compact, 16 above: the overlay margin of both themes), and its padding.</summary>
    public const float PanelW = 392f, Pad = 12f, Gap = 4f, CompactMargin = 8f, Margin = 16f;

    /// <summary>The flyout's width in a window <paramref name="viewportWidth"/> wide at <paramref name="tier"/>.</summary>
    public static float WidthAt(WidthTier tier, float viewportWidth)
        => MathF.Min(PanelW, MathF.Max(1f, viewportWidth - 2f * (tier == WidthTier.Compact ? CompactMargin : Margin)));

    /// <summary>The head row ("Playing on" and the refresh button): its padding, the button's box (the board's 28 raised to the 32 target floor) and glyph.</summary>
    public static readonly Edges4 HeadPad = new(4f, 2f, 4f, 6f);
    public const float HeadButton = 32f, HeadGlyph = 14f;

    /// <summary>The "Playing on" card: padding, the gap between its rows, the icon tile and its glyph and accent strength, the name and its note.</summary>
    public const float CardPad = 12f, CardGap = 12f, TileSize = 40f, TileGlyph = 20f, TileAlpha = 0.16f;
    public const float CardTextGap = 2f, CardNameSize = 14f, CardNameLine = 20f, CardNoteSize = 12f, CardNoteLine = 16f;
    public const ushort CardNameWeight = 600;

    /// <summary>The playing indicator's height (the card's three bars).</summary>
    public const float MeterH = 14f;

    /// <summary>The card's volume row: the speaker glyph, the slider's thickness, the value's column.</summary>
    public const float VolumeGlyph = 16f, VolumeThickness = 20f, VolumeValueW = 22f, VolumeTextSize = 12f;

    /// <summary>The primary action while another device plays ("Play on … instead").</summary>
    public const float ActionH = 32f;

    /// <summary>A section's header ("This PC", "Other devices").</summary>
    public static readonly Edges4 SectionPad = new(4f, 12f, 4f, 4f);
    public const float SectionSize = 12f, SectionLine = 16f;

    /// <summary>A device row: its least height and gap; the radio ring and its dot; the glyph; the name and its second line (two lines at most).</summary>
    public const float RowMinH = 48f, RowGap = 12f, Ring = 18f, RingDot = 8f, RowGlyph = 16f;
    public static readonly Edges4 RowPad = new(8f, 6f, 8f, 6f);
    public const float RowTextGap = 1f, RowNameSize = 14f, RowNameLine = 20f, RowNoteSize = 12f, RowNoteLine = 16f;
    public const int RowNoteLines = 2;

    /// <summary>"Nothing else nearby": its padding (the words on the rows' text column).</summary>
    public static readonly Edges4 EmptyPad = new(8f, 8f, 8f, 8f);

    /// <summary>The divider above the footer: its gap above and below.</summary>
    public static readonly Edges4 DividerMargin = new(0f, 8f, 0f, 4f);

    /// <summary>The footer row: its padding; the quality chip's height, padding, glyph and words; the sound link's two lines.</summary>
    public static readonly Edges4 FooterPad = new(4f, 0f, 4f, 0f);
    public const float FooterGap = 8f, ChipH = 28f, ChipPadX = 8f, ChipGap = 6f, ChipGlyph = 12f, ChipSize = 12f;
    public const float SoundSize = 12f, SoundNoteSize = 11f, SoundGap = 1f;
}
