// ── Themes/Classic/Structure/Rules/ClassicPlaylistLayout.cs — the numbers of Classic's playlist views (themes plan 6.1, T9b) ──────────────────────────
//
// What `Entities\Playlist.UI.cs` held as literals and file-level constants beside the pictures they size (ch 06 §3), moved with those pictures
// (`Views\Detail\ClassicPlaylist*.cs`): the cover's saturation and scrim, the status chip, the Save / Cancel buttons, the inline editors' pencil and fields, the
// owner row (the reserved height, the pile, the invite pill, the collaborator flyout), the access panel's rows, the Tune panel, the daylist colons and the
// recommendations' row. They are data, not decisions (WVTH003 allows literals and colour constructors only in `Rules\**`).
//
// WHAT IS NOT HERE, AND WHY. The popups' measures (`Playlist.AccessPanelWidth`) belong to the hosts that open the popup and the dialog (the
// dialog is that much wider than its content) and travel on the props; the editors' size decisions (`Detail.VerticalLayout`'s title rule) and the clock modes
// (`DaylistClockShows`) stay core.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's playlist views.</summary>
public static class ClassicPlaylistLayout
{
    // ── the cover ──

    /// <summary>Saturation 1.18 on BOTH arms (item 3): the editable ↔ read-only flip is structural only.</summary>
    public const float CoverSaturation = 1.18f;

    /// <summary>The scrim's body: the gap between the camera and its words, the camera's glyph, the words' size and measure.</summary>
    public const float CoverScrimGap = 6f, CoverCameraGlyph = 32f, CoverScrimTextSize = 12f, CoverScrimTextW = 120f;

    /// <summary>The scrim at rest on hover: #000 at .52.</summary>
    public static readonly ColorF ScrimHover = ColorF.FromRgba(0, 0, 0, 133);

    /// <summary>The scrim while a file hovers over the cover: #000 at .68.</summary>
    public static readonly ColorF ScrimDrop = ColorF.FromRgba(0, 0, 0, 173);

    /// <summary>The scrim's ink: image overlays never use theme ink (§4).</summary>
    public static readonly ColorF OnScrim = ColorF.FromRgba(255, 255, 255, 255);

    // ── the status chip and the Save / Cancel buttons (W14) ──

    /// <summary>The chip's gap (the buttons' too), padding, radius, icon box and glyph.</summary>
    public const float ChipGap = 6f, ChipPadL = 8f, ChipPadR = 10f, ChipPadY = 3f, ChipRadius = 12f, ChipIcon = 16f, ChipGlyph = 12f;

    // ── the inline editors (W14, W15) ──

    /// <summary>The description's size and its line cap in the read arm.</summary>
    public const float DescriptionSize = 12f;

    /// <inheritdoc cref="DescriptionSize"/>
    public const int DescriptionLines = 6;


    // ── the owner row (W25, W29) ──

    /// <summary>THE RESERVED OWNER ROW: the taller arm's height (the collaborator pile's 32 DIP framed face), always.</summary>
    public const float OwnerRowHeight = Controls.FaceOuter;

    /// <summary>The owner run's avatar.</summary>
    public const float OwnerAvatar = 24f;

    /// <summary>The collaborator pile's button: gap, side padding, radius, chevron glyph, label size.</summary>
    public const float PileGap = 4f, PilePadX = 6f, PileRadius = 8f, PileChevron = 8f, PileLabelSize = 14f;

    /// <summary>The invite pill: the round arm's edge, the width from which it is the labelled pill, its gap, padding, radius, glyphs and label size.</summary>
    public const float InviteRoundEdge = 28f, InviteWideFrom = 260f, InviteGap = 4f, InvitePadL = 10f, InvitePadR = 12f, InviteRadius = 14f,
        InviteGlyphWide = 12f, InviteGlyphRound = 14f, InviteTextSize = 12f;

    /// <summary>The ROUND 28 arm under <see cref="InviteWideFrom"/> DIP, the labelled pill above it (an unconstrained row is the round arm).</summary>
    public static bool InviteIsWide(float width) => float.IsFinite(width) && width >= InviteWideFrom;

    /// <summary>The collaborator flyout's rows: gap, height, side padding, radius, portrait, name size; and the list's gap.</summary>
    public const float MemberGap = 12f, MemberRowH = 44f, MemberPadX = 8f, MemberRadius = 6f, MemberPortrait = 32f, MemberNameSize = 14f,
        MemberListGap = 2f;

    // ── the access panel (W17) ──

    /// <summary>The heading and the hint: sizes.</summary>
    public const float AccessTitleSize = 14f, AccessHintSize = 12f;

    /// <summary>The divider's vertical margin.</summary>
    public const float AccessDividerGap = 2f;

    /// <summary>The copy-invite call to action: glyph, height, radius.</summary>
    public const float CtaGlyph = 14f, CtaH = 34f, CtaRadius = 17f;

    /// <summary>A switch row's gap between its label and caption.</summary>
    public const float SwitchLabelGap = 2f;

    // ── Tune: the AI action and its strip (owner, 2026-11-18: "not a dialog", "a lot more prevalent like an AI button") ──

    /// <summary>Tune's AI stroke in Classic: blue → violet → pink on the pill and the strip.</summary>
    public static readonly GradientSpec TuneStroke = new(GradientShape.Linear, 0f,
    [
        new GradientStop(0f, ColorF.FromRgba(0x3B, 0x82, 0xF6)),
        new GradientStop(0.5f, ColorF.FromRgba(0x8B, 0x5C, 0xF6)),
        new GradientStop(1f, ColorF.FromRgba(0xEC, 0x48, 0x99)),
    ]);

    /// <summary>The same hues, faint: the lit pill's ground and the strip's.</summary>
    public static readonly GradientSpec TuneWash = new(GradientShape.Linear, 0f,
    [
        new GradientStop(0f, ColorF.FromRgba(0x3B, 0x82, 0xF6, 0x24)),
        new GradientStop(0.5f, ColorF.FromRgba(0x8B, 0x5C, 0xF6, 0x24)),
        new GradientStop(1f, ColorF.FromRgba(0xEC, 0x48, 0x99, 0x24)),
    ]);

    /// <summary>The sparkle's ink: the stroke's middle hue.</summary>
    public static readonly ColorF TuneSparkle = ColorF.FromRgba(0x9B, 0x6B, 0xFF);

    /// <summary>The stroke's width; the strip's padding, the gap between its head and its chips, the head's gaps and the chips' gap.</summary>
    public const float TuneStrokeWidth = 1f, TuneStripPad = 14f, TuneStripGap = 12f, TuneHeadGap = 12f, TuneChipGap = 8f;

    /// <summary>The head's sparkle tile (edge, radius, glyph), the title's and the line's sizes, the gap under the title, the close button and the working ring.</summary>
    public const float TuneTile = 36f, TuneTileRadius = 10f, TuneTileGlyph = 18f, TuneTitleSize = 14f, TuneLineSize = 12f, TuneLineGap = 2f, TuneClose = 32f,
        TuneRing = 14f, TuneRingGap = 6f;

    /// <summary>The list bar's Tune pill: its padding before the sparkle and after the word.</summary>
    public const float TunePillPadL = 9f, TunePillPadR = 12f;

    // ── the daylist strip (W26) ──

    /// <summary>The colon between the flip cells: width and size in the compact and the hero strip, weight and face.</summary>
    public const float ColonCompactW = 6f, ColonHeroW = 8f, ColonCompactSize = 14f, ColonHeroSize = 20f;

    /// <summary>The colon's weight.</summary>
    public const int ColonWeight = 300;

    /// <summary>The colon's face.</summary>
    public const string ColonFont = "Segoe UI Variable Display";

    // ── recommended songs (W10) ──

    /// <summary>The header's note size, its refresh / spinner box and the refresh glyph.</summary>
    public const float RecsNoteSize = 12f, RecsButton = 32f, RecsRefreshGlyph = 14f;

    /// <summary>A row: art edge, the add button's box, the duration's width.</summary>
    public const float RecArt = 40f, RecAddBox = 28f, RecDurationW = 52f;

    /// <summary>The "+" glyph inside the add ring.</summary>
    public const float RecAddGlyph = 15f;

    /// <summary>What the header keeps over and under a Compact 40 command (C-Playlist-Compact: a 48 header).</summary>
    public const float RecsHeaderPadY = 8f;

    // The empty playlist's lead (C-Empty-Compact, C-Empty) is `ClassicEmptyLead` (Tiers.Detail.cs): the record per tier.
}
