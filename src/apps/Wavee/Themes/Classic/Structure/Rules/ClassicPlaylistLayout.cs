// ── Themes/Classic/Structure/Rules/ClassicPlaylistLayout.cs — the numbers of Classic's playlist views (themes plan 6.1, T9b) ──────────────────────────
//
// What `Entities\Playlist.UI.cs` held as literals and file-level constants beside the pictures they size (ch 06 §3), moved with those pictures
// (`Views\Detail\ClassicPlaylist*.cs`): the cover's saturation and scrim, the status chip, the Save / Cancel buttons, the inline editors' pencil and fields, the
// owner row (the reserved height, the pile, the invite pill, the collaborator flyout), the access panel's rows, the Tune panel, the daylist colons and the
// recommendations' row. They are data, not decisions (WVTH003 allows literals and colour constructors only in `Rules\**`).
//
// WHAT IS NOT HERE, AND WHY. The popups' measures (`Playlist.AccessPanelWidth`, `TunePanelWidth`) belong to the hosts that open the popup and the dialog (the
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

    /// <summary>The buttons: 32 tall, r16, pad 10 / 0 / 12 / 0, glyph and label.</summary>
    public const float EditButtonH = 32f, EditButtonPadL = 10f, EditButtonPadR = 12f, EditButtonRadius = 16f, EditButtonGlyph = 13f, EditButtonText = 12f;

    /// <summary>Save is accent-tinted: rest, hover, pressed.</summary>
    public const float EditAccentRest = 0.16f, EditAccentHover = 0.26f, EditAccentPressed = 0.12f;

    // ── the inline editors (W14, W15) ──

    /// <summary>The status row's gap under the editor while a status shows.</summary>
    public const float StatusRowGap = 4f;

    /// <summary>The description's size and its line cap in the read arm.</summary>
    public const float DescriptionSize = 12f;

    /// <inheritdoc cref="DescriptionSize"/>
    public const int DescriptionLines = 6;

    /// <summary>The read arm's pencil: the glyph (the description's, the title's floor and the title's share of its size) and the box (the title's, the
    /// description's width and height).</summary>
    public const float PencilGlyphDescription = 13f, PencilGlyphMin = 14f, PencilGlyphScale = 0.4f, PencilBox = 20f,
        PencilBoxDescriptionW = 16f, PencilBoxDescriptionH = 18f;

    /// <summary>The read arm bleeds past its text by this much (margin and padding cancel): the hover plate's room.</summary>
    public const float ReadBleedX = 8f, ReadBleedY = 4f;

    /// <summary>The edit field: the description's height, the title's floor and what it adds to the title's size.</summary>
    public const float DescriptionFieldH = 72f, TitleFieldMinH = 40f, TitleFieldExtra = 12f;

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

    // ── the Tune panel (W24) ──

    /// <summary>The header: gap, side padding, top and bottom padding; the accent tile's edge, radius, wash and glyph; the subtitle's size.</summary>
    public const float TuneHeaderGap = 12f, TuneHeaderPadX = 14f, TuneHeaderPadTop = 9f, TuneHeaderPadBottom = 11f, TuneTile = 36f, TuneTileRadius = 10f,
        TuneTileWash = 0.13f, TuneTileGlyph = 18f, TuneSubtitleSize = 12f;

    /// <summary>The panel's vertical padding and its dividers' margin.</summary>
    public const float TunePadTop = 8f, TunePadBottom = 6f, TuneDividerX = 8f, TuneDividerTop = 3f, TuneDividerBottom = 4f;

    /// <summary>A choice row: height, left and right padding, the radio's edge and its dot, the label and the "Current" tag.</summary>
    public const float TuneRowH = 36f, TuneRowPadL = 12f, TuneRowPadR = 14f, RadioEdge = 14f, RadioDot = 6f, TuneRowText = 14f, TuneCurrentText = 12f;

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
