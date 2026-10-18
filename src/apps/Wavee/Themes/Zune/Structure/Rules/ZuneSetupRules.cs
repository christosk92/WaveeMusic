// ── Themes/Zune/Structure/Rules/ZuneSetupRules.cs — the numbers and the tier records of Zune's setup, release-notes and dialog screens (themes plan 6.3, P5 Z8b) ──
//
// Four families of numbers, one file (the lane owns this file and `ZuneSettingsRules.cs`): the SETUP wizard and its sign-in and runtime surfaces (`ZuneSetupRules`,
// `ZuneSetupTiers`), the RELEASE NOTES page, viewer and after-update plate (`ZuneReleaseNotesRules`, `ZuneReleaseNotesTiers`), and the two FORM DIALOGS' bodies, the report and the
// crash prompt (`ZuneDialogRules`, `ZuneDialogTiers`). Data, not decisions (WVTH003 allows literals only in `Rules\**`). A view reads its family's record
// (`Of(ctx.Tier())`) and never compares a width with a number (WVTH013).
//
// SETUP (per WINDOW tier, `Z-Setup`, `-Compact`, `-Small`, `-Huge`): the plate is 762 x 490 with a 192 accent column at Standard and Huge, 720 x 480 with a 160 column (20 in, a 72 QR,
// a 180 progress line, 24 / 22 / 24 / 18 around the page) at Small, and at Compact a sheet that fills the window under the title row with a 120 accent strip across its top and a
// stacked footer.
//
// RELEASE NOTES (per CONTENT tier for the page, per WINDOW tier for the overlays; `Z-ReleaseNotes` and its tiers): the rail at Small and up (160 beside a 32 gap at Small and Huge,
// 208 beside a 64 gap at Standard), a picker under the title at Compact; the highlights in three columns (one at Compact); the title the panorama or its compact step. The viewer and
// the after-update plate (no board draws them, owner Q18) fit the window less 16 a side; Compact stacks the plate and every control is 40.
//
// DIALOGS (per WINDOW tier, `Z-Dialogs`, `-Compact`, `-Small`, `-Huge`): the report and the crash prompt: Compact fills the window less 16 with the preview folded behind a 40 row, Small
// the 752 two-pane card (the form and a 300 preview pane; a card the window leaves too narrow for the pane falls back to the preview under the form), Standard and Huge the 548 card
// with the preview under the form (the Huge board draws nothing wider).
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

// ═════════════════════════════════════════════ SETUP ═══════════════════════════════════════════════════════════════════

/// <summary>What the setup plate lays out in one window tier.</summary>
public sealed record ZuneSetupLayout
{
    /// <summary>The window tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The plate is a sheet that fills the window under the title row (Compact), with the accent as a strip across its top.</summary>
    public required bool Sheet { get; init; }

    /// <summary>The plate's nominal width and height (the board's 762 x 490); a sheet fills the window.</summary>
    public required float PlateWidth { get; init; }
    public required float PlateHeight { get; init; }

    /// <summary>The window margin the plate keeps on each side.</summary>
    public required float Margin { get; init; }

    /// <summary>The least plate (0: the plate may shrink to fit any window).</summary>
    public required float MinPlateWidth { get; init; }
    public required float MinPlateHeight { get; init; }

    /// <summary>The space over a sheet that keeps the title row clear and reachable (0 for a plate).</summary>
    public required float RootTopPad { get; init; }

    /// <summary>The accent column's width, and the strip's height at Compact.</summary>
    public required float AccentWidth { get; init; }
    public required float AccentStripHeight { get; init; }

    /// <summary>The numeral's margin and the accent text's padding.</summary>
    public required Edges4 NumeralMargin { get; init; }
    public required Edges4 AccentPadding { get; init; }

    /// <summary>The page region's padding and the footer's.</summary>
    public required Edges4 ContentPadding { get; init; }
    public required Edges4 FooterPadding { get; init; }

    /// <summary>The footer stacks (the step label over the bar, then the commands): Compact.</summary>
    public required bool FooterStacked { get; init; }

    /// <summary>The footer's progress column (210) where it does not stack.</summary>
    public required float ProgressColumn { get; init; }

    /// <summary>The hit target of the plate's controls (Compact 40).</summary>
    public required float Target { get; init; }

    /// <summary>The wizard's QR edge, and the sign-in cards' basis (two share a line where the plate has room for both).</summary>
    public required float QrSize { get; init; }
    public required float CardBasis { get; init; }

    /// <summary>The root's width in a window <paramref name="viewportWidth"/> wide: the plate's, never wider than the window less the margins (a sheet: the window's).</summary>
    public float RootWidth(float viewportWidth)
        => Sheet ? MathF.Max(1f, viewportWidth) : MathF.Min(PlateWidth, MathF.Max(1f, viewportWidth - 2f * Margin));

    /// <summary>The root's height in a window <paramref name="viewportHeight"/> tall.</summary>
    public float RootHeight(float viewportHeight)
        => Sheet ? MathF.Max(1f, viewportHeight) : MathF.Min(PlateHeight, MathF.Max(1f, viewportHeight - 2f * Margin));

    /// <summary>The footer's progress line: the column's at a plate, the window's less the footer's padding where it stacks.</summary>
    public float ProgressWidth(float viewportWidth)
        => FooterStacked ? MathF.Max(1f, viewportWidth - FooterPadding.Left - FooterPadding.Right) : ProgressColumn;
}

/// <summary>The setup plate's tier records.</summary>
public static class ZuneSetupTiers
{
    private static ZuneSetupLayout Plate(WidthTier tier, float w, float h, float accent) => new()
    {
        Tier = tier, Sheet = false, PlateWidth = w, PlateHeight = h, Margin = ZuneSetupRules.PlateMargin, MinPlateWidth = 0f, MinPlateHeight = 0f, RootTopPad = 0f,
        AccentWidth = accent, AccentStripHeight = 0f,
        NumeralMargin = new Edges4(16f, 8f, 0f, 0f), AccentPadding = Edges4.All(24f),
        ContentPadding = new Edges4(28f, 24f, 24f, 0f), FooterPadding = new Edges4(28f, 12f, 24f, 20f),
        FooterStacked = false, ProgressColumn = 210f, Target = 0f, QrSize = 80f, CardBasis = 232f,
    };

    private static readonly ZuneSetupLayout[] s_records =
    [
        // Compact (Z-Setup-Compact): a sheet under the title row, the accent a 120 strip with the numeral at its right, the footer stacked, every control 40.
        new()
        {
            Tier = WidthTier.Compact, Sheet = true, PlateWidth = 0f, PlateHeight = 0f, Margin = 0f, MinPlateWidth = 0f, MinPlateHeight = 0f,
            RootTopPad = ZuneTiers.Frame(WidthTier.Compact).TitleBarH, AccentWidth = 0f, AccentStripHeight = 120f,
            NumeralMargin = new Edges4(0f, -26f, 12f, 0f), AccentPadding = new Edges4(16f, 0f, 16f, 14f),
            ContentPadding = new Edges4(16f, 20f, 16f, 0f), FooterPadding = new Edges4(16f, 12f, 16f, 16f),
            FooterStacked = true, ProgressColumn = 0f, Target = 40f, QrSize = 96f, CardBasis = 280f,
        },
        // Small (Z-Setup-Small): 720 x 480 with a 160 accent column (20 in, the numeral at 12 / 4), a 72 QR, a 180 progress line, 24 / 22 / 24 / 18 around the page.
        Plate(WidthTier.Small, 720f, 480f, 160f) with
        {
            NumeralMargin = new Edges4(12f, 4f, 0f, 0f), AccentPadding = Edges4.All(20f),
            ContentPadding = new Edges4(24f, 22f, 24f, 0f), FooterPadding = new Edges4(24f, 12f, 24f, 18f),
            ProgressColumn = 180f, QrSize = 72f,
        },
        // Standard (Z-Setup): 762 x 490 with a 192 accent column.
        Plate(WidthTier.Standard, 762f, 490f, 192f),
        // Huge (Z-Setup-Huge) and Huge 4K: the same plate; it never stretches.
        Plate(WidthTier.Huge, 762f, 490f, 192f),
        Plate(WidthTier.Huge4K, 762f, 490f, 192f),
    ];

    /// <summary>The record of window tier <paramref name="window"/>.</summary>
    public static ZuneSetupLayout For(WidthTier window) => s_records[Math.Clamp((int)window, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneSetupLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>The numbers of Zune's setup views.</summary>
public static class ZuneSetupRules
{
    /// <summary>The pages the keep-alive holds, and the window margin a plate keeps.</summary>
    public const int KeepAlivePages = 3;
    public const float PlateMargin = 16f;

    /// <summary>"No value": the em dash.</summary>
    public const string Dash = "—";

    /// <summary>The numeral's alpha over the accent (the board's 35 % black).</summary>
    public const float NumeralAlpha = 0.35f;

    /// <summary>The accent column's gap between the page's name and the steps' names.</summary>
    public const float AccentTextGap = 2f;

    /// <summary>The footer: the gap between the progress and the commands, between the commands, and under the step label.</summary>
    public const float FooterGap = 16f, FooterActionGap = 20f, ProgressGap = 6f;

    /// <summary>The progress line (the cards' bars) width.</summary>
    public const float ProgressWidth = 162f;

    /// <summary>The page body: the gap under the title, between blocks and inside a block, the body's enter and exit offsets.</summary>
    public const float TitleGap = 8f, BodyGap = 12f, BodyInnerGap = 8f, BodyEnterDy = 6f, BodyExitDy = -4f;

    /// <summary>A panel (the board's #292929 card): its padding, gap, glyph, least height, the gap between its words, and the gap between the sign-in cards.</summary>
    public const float CardPad = 16f, CardGap = 14f, CardGlyph = 20f, CardMinHeight = 56f, CardWordsGap = 4f, CardsGap = 16f;

    // ── the sign-in surfaces ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Busy rows' minimum height, the pairing countdown's tick (ms) and the working ring's size.</summary>
    public const float BusyRowMinHeight = 48f, PairingTickMs = 1000f, PairingRingSize = 20f;

    /// <summary>The QR plate: pure black on white whatever the theme (contrast is what a phone camera binarizes on) and the fallback note's green and size.</summary>
    public static readonly ColorF QrInk = ColorF.FromRgba(0x00, 0x00, 0x00);
    public static readonly ColorF QrPaper = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    public static readonly ColorF QrFallbackGlyph = ColorF.FromRgba(0x1D, 0xB9, 0x54);
    public const float QrFallbackGlyphSize = 28f;

    /// <summary>"Is this you?": the avatar's size.</summary>
    public const float AccountPicture = 40f;

    /// <summary>The Busy ladder: the ring, the done / failed glyph, the pending bullet, the row, its width, the mark's box, the bar, the mark's enter and exit scales and the row's slide.</summary>
    public const float StepRingSize = 16f, StepGlyphSize = 15f, StepBulletSize = 11f, StepRowHeight = 26f, StepRowWidth = 300f, StepMarkBox = 18f, StepBarWidth = 220f;
    public const float MarkEnterScale = 0.72f, MarkExitScale = 0.88f, StepEnterDx = -6f;
    public const ushort StepCurrentWeight = 600, StepRestWeight = 400;

    // ── the runtime card ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The inline status block: the severity glyph, its top nudge and the gap between the heading and the copy.</summary>
    public const float StatusGlyphSize = 18f, StatusTextGap = 4f;
    public static readonly Edges4 StatusGlyphMargin = new(0f, 1f, 0f, 0f);

    /// <summary>The rule above the local-source rows.</summary>
    public static readonly Edges4 CardRuleMargin = new(0f, 4f, 0f, 4f);

    /// <summary>The Ready fact box's padding and gap.</summary>
    public const float ReadyFactPad = 12f, ReadyFactGap = 6f;
}

// ═════════════════════════════════════════════ RELEASE NOTES ═══════════════════════════════════════════════════════════

/// <summary>What Zune's What's-new page lays out in one content tier.</summary>
public sealed record ZuneReleaseNotesLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The page gutter (the frame's for the tier).</summary>
    public required float Gutter { get; init; }

    /// <summary>The title is the compact step of the panorama, and its box's height.</summary>
    public required bool CompactTitle { get; init; }
    public required float TitleH { get; init; }

    /// <summary>The release switch is a rail (Standard and up) instead of a picker, the rail's width and the gap between it and the body.</summary>
    public required bool HasRail { get; init; }
    public required float RailWidth { get; init; }
    public required float FrameGap { get; init; }

    /// <summary>The picker's height (Compact 40); 0 for the rail.</summary>
    public required float PickerHeight { get; init; }

    /// <summary>The least hit target of every control on the page (Compact 40).</summary>
    public required float Target { get; init; }

    /// <summary>The body column's widest (NaN fills the region) and the gap between the body's blocks.</summary>
    public required float BodyMaxW { get; init; }
    public required float BodyGap { get; init; }

    /// <summary>The highlight panels' columns (3, 2, 1) and the gap between them.</summary>
    public required int HighlightColumns { get; init; }
    public required float HighlightGap { get; init; }
}

/// <summary>What the highlight viewer and the after-update plate are in one WINDOW tier.</summary>
public sealed record ZuneReleaseOverlayLayout
{
    /// <summary>The window tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall).</summary>
    public required bool Short { get; init; }

    /// <summary>The least hit target of every control (40 at Compact, 0 elsewhere).</summary>
    public required float Target { get; init; }

    /// <summary>The viewer's chrome circles (36; 40 at Compact) and the pager row's height (24; 40 at Compact, where each pip carries a 40-wide hit box).</summary>
    public required float ChromeCircle { get; init; }
    public required float PagerRowH { get; init; }

    /// <summary>The window margin the plate keeps on each side, and its side padding.</summary>
    public required float PlateMargin { get; init; }
    public required float PlatePadX { get; init; }

    /// <summary>The plate's cards are one column (Compact) instead of one row, its footer stacks, and its body scrolls above a pinned footer (Compact, and any Short window).</summary>
    public required bool CardsColumn { get; init; }
    public required bool FooterStacked { get; init; }
    public required bool ScrollBody { get; init; }

    /// <summary>The plate's width in a window <paramref name="viewportWidth"/> wide: its 720, never wider than the window less the margins.</summary>
    public float PlateWidth(float viewportWidth) => MathF.Min(ZuneReleaseNotesRules.PlateWidth, MathF.Max(1f, viewportWidth - 2f * PlateMargin));

    /// <summary>The plate's tallest in a window <paramref name="viewportHeight"/> tall: its 620 cap, never taller than the window less the margins.</summary>
    public float PlateMaxHeight(float viewportHeight) => MathF.Min(ZuneReleaseNotesRules.PlateMaxHeight, MathF.Max(1f, viewportHeight - 2f * PlateMargin));
}

/// <summary>Zune's release-notes records.</summary>
public static class ZuneReleaseNotesTiers
{
    private static ZuneReleaseNotesLayout Page(WidthTier tier, bool compactTitle, bool rail, float railW, float frameGap, float pickerH, float target, float bodyMaxW,
        float bodyGap, int columns, float highlightGap) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, CompactTitle = compactTitle,
        TitleH = compactTitle ? ZuneSettingsRules.TitleHCompact : ZuneSettingsRules.TitleHPanorama,
        HasRail = rail, RailWidth = railW, FrameGap = frameGap, PickerHeight = pickerH, Target = target, BodyMaxW = bodyMaxW, BodyGap = bodyGap,
        HighlightColumns = columns, HighlightGap = highlightGap,
    };

    private static readonly ZuneReleaseNotesLayout[] s_pages =
    [
        // Compact (Z-ReleaseNotes-Compact): a 40 picker under the title, one column of panels, controls 40.
        Page(WidthTier.Compact, compactTitle: true, rail: false, railW: 0f, frameGap: 0f, pickerH: 40f, target: 40f, bodyMaxW: float.NaN, bodyGap: 12f, columns: 1, highlightGap: 12f),
        // Small (Z-ReleaseNotes-Small): the 160 rail 32 from the body (no picker), three panels 12 apart.
        Page(WidthTier.Small, compactTitle: true, rail: true, railW: 160f, frameGap: 32f, pickerH: 0f, target: 0f, bodyMaxW: float.NaN, bodyGap: 12f, columns: 3, highlightGap: 12f),
        // Standard (Z-ReleaseNotes): the 208 rail 64 from the body, three panels.
        Page(WidthTier.Standard, compactTitle: false, rail: true, railW: 208f, frameGap: 64f, pickerH: 0f, target: 0f, bodyMaxW: float.NaN, bodyGap: 16f, columns: 3, highlightGap: 16f),
        // Huge (Z-ReleaseNotes-Huge): the 160 rail 32 from a body capped at the board's two 760 columns and their gap (1552), left-aligned.
        Page(WidthTier.Huge, compactTitle: false, rail: true, railW: 160f, frameGap: 32f, pickerH: 0f, target: 0f, bodyMaxW: 1552f, bodyGap: 20f, columns: 3, highlightGap: 16f),
        Page(WidthTier.Huge4K, compactTitle: false, rail: true, railW: 160f, frameGap: 32f, pickerH: 0f, target: 0f, bodyMaxW: 1552f, bodyGap: 20f, columns: 3, highlightGap: 16f),
    ];

    /// <summary>The page record of content tier <paramref name="content"/>.</summary>
    public static ZuneReleaseNotesLayout For(WidthTier content) => s_pages[Math.Clamp((int)content, 0, s_pages.Length - 1)];

    /// <summary>The page record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share
    /// one left edge, so a docked panel that narrows the content never moves it).</summary>
    public static ZuneReleaseNotesLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static ZuneReleaseNotesLayout OfContent(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    // ── the overlays ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ZuneReleaseOverlayLayout Overlay(WidthTier tier, bool compact, bool scroll) => new()
    {
        Tier = tier, Short = false, Target = compact ? 40f : 0f, ChromeCircle = compact ? 40f : 36f, PagerRowH = compact ? 40f : 24f,
        PlateMargin = 16f, PlatePadX = compact ? 16f : 26f, CardsColumn = compact, FooterStacked = compact, ScrollBody = scroll,
    };

    private static readonly ZuneReleaseOverlayLayout[] s_overlays =
    [
        Overlay(WidthTier.Compact, compact: true, scroll: true),
        Overlay(WidthTier.Small, compact: false, scroll: false),
        Overlay(WidthTier.Standard, compact: false, scroll: false),
        Overlay(WidthTier.Huge, compact: false, scroll: false),
        Overlay(WidthTier.Huge4K, compact: false, scroll: false),
    ];

    /// <summary>The overlay record of window tier <paramref name="window"/>, scrolling its body in a Short window.</summary>
    public static ZuneReleaseOverlayLayout OverlayFor(WidthTier window, bool shortWindow)
    {
        var record = s_overlays[Math.Clamp((int)window, 0, s_overlays.Length - 1)];
        return shortWindow ? record with { Short = true, ScrollBody = true } : record;
    }

    /// <summary>The overlay record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ZuneReleaseOverlayLayout Overlay(in TierFacts facts)
        => OverlayFor(facts.Policy is null ? WidthTier.Standard : facts.Window, facts.Policy is not null && facts.Height == HeightTier.Short);
}

/// <summary>The numbers of Zune's release-notes views.</summary>
public static class ZuneReleaseNotesRules
{
    /// <summary>The monospace face for versions, ids and code.</summary>
    public const string Mono = "Cascadia Code";

    /// <summary>The panorama bleeds this far left of the gutter.</summary>
    public const float PanoramaBleed = 8f;

    /// <summary>An ISO date's length ("2026-10-17"), the loading ring, the empty text's measure, the hero tagline's measure, the page's tail and its scroll inset.</summary>
    public const int IsoDateLength = 10;
    public const float LoadingRingSize = 32f, EmptyTextMaxWidth = 420f, HeroTaglineMaxWidth = 640f, PageTailHeight = 24f, ScrollInset = 6f;

    /// <summary>The since line's gap, the hero's actions' gap, the highlights' heading gap.</summary>
    public const float SinceGap = 12f, HeroActionGap = 20f, StripGap = 12f;

    /// <summary>The release picker's width.</summary>
    public const float PickerWidth = 280f;

    // ── the rail ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public const float RailGap = 8f, RailListGap = 4f, RailLineGap = 1f, RailRowGap = 6f, RailDot = 6f, RailRule = 2f;
    public const ushort RailSelectedWeight = 600, RailRestWeight = 400;

    // ── the highlight panels ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panel's padding, least height, the lines its title and body may take, the poster's height and decode width, the swatch's edge.</summary>
    public const float CardPad = 16f, CardMinHeight = 120f, PosterHeight = 96f, PosterDecodePx = 1200f, SwatchSize = 24f;
    public const int CardTitleLines = 2, CardBodyLines = 3, CardBodyLinesCompact = 2;

    /// <summary>The poster's fade in the viewer (ms).</summary>
    public const float PosterFadeMs = 140f;

    // ── the changelog ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A group's header height and the gap under it, a change's least height and vertical padding, the gap in its reference row, the scope's weight.</summary>
    public const float SectionHeaderHeight = 40f, SectionGap = 6f, RowMinHeight = 36f, RowPadY = 6f, RowRefGap = 6f;
    public const ushort ScopeWeight = 700;

    /// <summary>The reference chip: its gap, height and side padding.</summary>
    public const float ChipGap = 6f, ChipHeight = 20f, ChipPadX = 6f;

    /// <summary>An inline code span: its size and its bold weight.</summary>
    public const float CodeSpanSize = 12f;
    public const ushort CodeBoldWeight = 600;

    /// <summary>The contributors' initials: the disc, its ring and overlap, how many show, the initials' weight and the tints (the board's).</summary>
    public const float AvatarSize = 20f, AvatarRing = 2f, AvatarOverlap = 5f;
    public const int AvatarMax = 3;
    public const ushort AvatarWeight = 700;

    /// <summary>The board's avatar discs.</summary>
    public static readonly ColorF[] AvatarTints =
    [
        ColorF.FromRgba(0x5B, 0x6B, 0x3B), ColorF.FromRgba(0x6B, 0x2F, 0x4A), ColorF.FromRgba(0x2F, 0x5B, 0x6B),
        ColorF.FromRgba(0x5B, 0x3B, 0x6B), ColorF.FromRgba(0x6B, 0x4A, 0x2F),
    ];

    // ── the viewer ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rgba(0,0,0,.72): the media scrim (.55) reads thin stacked over the plate's own smoke.</summary>
    public static readonly ColorF ViewerVeil = ColorF.FromRgba(0, 0, 0, 184);

    /// <summary>The chrome's dimmed and hover media-scrim alphas, a dimmed end button's opacity, the glyph sizes.</summary>
    public const float ChromeDimmedAlpha = 0.40f, ChromeHoverAlpha = 0.70f, DimmedOpacity = 0.3f, ChevronGlyph = 16f, CloseGlyph = 12f;

    /// <summary>The pager's dot, selected capsule, gap, width tween and top margin.</summary>
    public const float PipDot = 6f, PipSelected = 14f, PipGap = 8f, PipMs = 120f, PagerRowTop = 8f;

    /// <summary>The text column's measure and the action row's gap; the watch pill's gap, glyph and padding.</summary>
    public const float ViewerTextMaxWidth = 720f, ViewerActionGap = 8f, WatchGap = 6f, WatchGlyph = 14f, WatchPadX = 14f, WatchPadY = 8f;

    // ── the after-update plate ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plate's width and tallest (the 620 cap the plate-cap test asserts).</summary>
    public const float PlateWidth = 720f, PlateMaxHeight = 620f;

    /// <summary>The plate's tagline measure, a highlight card's cap, the hero's and the row's padding, the footer's vertical padding and the gap between its commands.</summary>
    public const float PlateTaglineMaxWidth = 560f, PlateCardMaxWidth = 420f, PlateHeroPadTop = 24f, PlateHeroPadBottom = 8f, PlateRowPadTop = 8f, PlateRowPadBottom = 24f,
        PlateFooterPadY = 14f, PlateActionGap = 20f;

    /// <summary>The viewer plate's geometry (design §B.1), pure.</summary>
    public static class Viewer
    {
        /// <summary>A 1200 px poster shown at 960 is a 0.8x downsample: sharp, never upsampled; wider plates make the text measure absurd.</summary>
        public const float PlateMaxWidth = 960f;

        /// <summary>Below this the image is unreadable (180 tall); the text column scrolls instead.</summary>
        public const float PlateMinWidth = 320f;

        /// <summary>48 DIP of veil each side, so the plate reads as a plate and not as a page.</summary>
        public const float ScrimInsetX = 96f;

        /// <summary>Pager (36) + text block (up to 260) + 64 vertical margin the image must leave below itself.</summary>
        public const float ReservedBelowImage = 360f;

        /// <summary>The vertical margin, the poster's aspect and the chrome's inset.</summary>
        public const float PlateMarginY = 64f, PosterAspect = 16f / 9f, ChromeInset = 12f;

        /// <summary>W = min(max(320, min(960, vpW - 96, (vpH - 360) x 16/9)), vpW - 96). The floor never pushes the plate past the window edge.</summary>
        public static float PlateWidth(float vpW, float vpH)
        {
            float byWidth = vpW - ScrimInsetX;
            float byHeight = (vpH - ReservedBelowImage) * PosterAspect;
            float w = MathF.Min(PlateMaxWidth, MathF.Min(byWidth, byHeight));
            w = MathF.Max(PlateMinWidth, w);
            w = MathF.Min(w, byWidth);
            return MathF.Round(w);
        }

        /// <summary>The 16:9 band height, from the plate's OWN width, always (poster or not).</summary>
        public static float ImageHeight(float plateWidth) => MathF.Round(plateWidth / PosterAspect);

        /// <summary>The plate's tallest: the window less its vertical margins.</summary>
        public static float PlateMaxHeight(float vpH) => vpH - PlateMarginY;

        /// <summary>The text half's padding (a pager above it takes the smaller top).</summary>
        public static Edges4 TextPadding(bool hasPager) => new(24f, hasPager ? 8f : 16f, 24f, 24f);
    }
}

// ═════════════════════════════════════════════ THE FORM DIALOGS ════════════════════════════════════════════════════════

/// <summary>How the report preview sits in a form dialog.</summary>
public enum ZuneReportPreview : byte
{
    /// <summary>Under the form, always shown (Standard, Huge).</summary>
    Inline,

    /// <summary>Behind a 40-DIP row that expands it (Compact).</summary>
    Collapsed,

    /// <summary>In a second pane beside the form, always shown (Small: the 752 card).</summary>
    Pane,
}

/// <summary>What the report and crash dialogs' bodies lay out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Preview">Where the report preview sits.</param>
/// <param name="Target">The hit target the controls are lifted to (0 keeps the stock controls; Compact 40).</param>
/// <param name="NominalBodyWidth">The body's own width where the card has room (the stock 548 card less its 24 padding).</param>
/// <param name="TextFieldHeight">A single-line field's height (the stock 32; Compact 40).</param>
/// <param name="PreviewHeight">The preview box's height.</param>
/// <param name="FormPaneWidth">The form pane's width in a two-pane body (0 elsewhere).</param>
public sealed record ZuneDialogLayout(WidthTier Tier, ZuneReportPreview Preview, float Target, float NominalBodyWidth, float TextFieldHeight, float PreviewHeight, float FormPaneWidth)
{
    /// <summary>A second pane carries the preview.</summary>
    public bool TwoPane => Preview == ZuneReportPreview.Pane;

    /// <summary>The preview folds behind its row.</summary>
    public bool PreviewFolds => Preview == ZuneReportPreview.Collapsed;

    /// <summary>The body lifts its controls to <see cref="Target"/>.</summary>
    public bool Touch => Target > 0f;
}

/// <summary>What a form dialog's body mounted with: the tier's record and the width the card left it, both frozen at open.</summary>
/// <param name="Layout">The record of the window tier the dialog opened in.</param>
/// <param name="BodyWidth">The card's text region at open.</param>
public sealed record ZuneDialogShape(ZuneDialogLayout Layout, float BodyWidth)
{
    /// <summary>The form's column width: the form pane in a two-pane body, else the whole body.</summary>
    public float FormWidth => Layout.TwoPane ? MathF.Min(Layout.FormPaneWidth, BodyWidth) : BodyWidth;

    /// <summary>The preview pane's width in a two-pane body: what the form pane, the two gaps and the divider leave of the body.</summary>
    public float PreviewPaneWidth => MathF.Max(1f, BodyWidth - Layout.FormPaneWidth - 2f * ZuneDialogRules.PaneGap - ZuneDialogRules.DividerWidth);
}

/// <summary>The report and crash dialogs' tier records.</summary>
public static class ZuneDialogTiers
{
    private static readonly ZuneDialogLayout[] s_layouts =
    [
        // Compact (Z-Dialogs-Compact): the card fills the window less 16; a 40 field, the preview behind its 40 row.
        new(WidthTier.Compact, ZuneReportPreview.Collapsed, Target: 40f, NominalBodyWidth: ZuneDialogRules.StockBodyWidth, TextFieldHeight: 40f, PreviewHeight: 220f, FormPaneWidth: 0f),
        // Small (Z-Dialogs-Small): the 752 card, the form beside a 300 preview pane (the board's flexible form column is what the 704 body leaves it, 355).
        new(WidthTier.Small, ZuneReportPreview.Pane, Target: 0f, NominalBodyWidth: ZuneDialogRules.SmallBodyWidth, TextFieldHeight: 32f, PreviewHeight: 300f,
            FormPaneWidth: ZuneDialogRules.SmallBodyWidth - ZuneDialogRules.SmallPaneWidth - 2f * ZuneDialogRules.PaneGap - ZuneDialogRules.DividerWidth),
        // Standard (Z-Dialogs): the tuned 548 card.
        new(WidthTier.Standard, ZuneReportPreview.Inline, Target: 0f, NominalBodyWidth: ZuneDialogRules.StockBodyWidth, TextFieldHeight: 32f, PreviewHeight: 220f, FormPaneWidth: 0f),
        // Huge (Z-Dialogs-Huge) and Huge 4K: the same 548 card; a dialog never stretches.
        new(WidthTier.Huge, ZuneReportPreview.Inline, Target: 0f, NominalBodyWidth: ZuneDialogRules.StockBodyWidth, TextFieldHeight: 32f, PreviewHeight: 220f, FormPaneWidth: 0f),
        new(WidthTier.Huge4K, ZuneReportPreview.Inline, Target: 0f, NominalBodyWidth: ZuneDialogRules.StockBodyWidth, TextFieldHeight: 32f, PreviewHeight: 220f, FormPaneWidth: 0f),
    ];

    /// <summary>The record of <paramref name="window"/>.</summary>
    public static ZuneDialogLayout For(WidthTier window) => s_layouts[Math.Clamp((int)window, 0, s_layouts.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ZuneDialogLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The shape a body mounts with: the record for <paramref name="facts"/> and the body width in a window <paramref name="viewportWidth"/> wide (the live layout
    /// viewport; the facts' own where none is known; the nominal width where neither is). The width is the text region of the card the active structure's overlay fit gives the
    /// window (`DialogFit.FormBodyWidth`).</summary>
    public static ZuneDialogShape Open(in TierFacts facts, float viewportWidth)
    {
        var layout = Of(facts);
        float window = viewportWidth > 0f ? viewportWidth : facts.ViewportWidth;
        float body = window > 0f ? OverlayFit.Layout.Dialog.FormBodyWidth(layout.NominalBodyWidth, window) : layout.NominalBodyWidth;
        return Shape(layout, body);
    }

    /// <summary>The shape of <paramref name="layout"/> in a card whose text region is <paramref name="body"/> wide: a two-pane body the card leaves too narrow for its preview pane (a
    /// 640 window's 592 card) keeps the preview under the form instead, so neither column is squeezed.</summary>
    public static ZuneDialogShape Shape(ZuneDialogLayout layout, float body)
    {
        if (layout.TwoPane && body < layout.FormPaneWidth + 2f * ZuneDialogRules.PaneGap + ZuneDialogRules.DividerWidth + ZuneDialogRules.PaneMinWidth)
            layout = layout with { Preview = ZuneReportPreview.Inline, FormPaneWidth = 0f, PreviewHeight = ZuneDialogRules.InlinePreviewHeight };
        return new ZuneDialogShape(layout, body);
    }
}

/// <summary>The numbers of Zune's report and crash dialog bodies.</summary>
public static class ZuneDialogRules
{
    /// <summary>The monospace face for the report.</summary>
    public const string Mono = "Cascadia Code";

    /// <summary>The Small two-pane card (752 wide: the 800 window less 24 a side), the margin it keeps, its text region (less the engine's 24 padding either side), the preview
    /// pane the board draws (300) and the stock card's body (548 less 24 either side).</summary>
    public const float SmallCardWidth = 752f, SmallInset = 24f, SmallBodyWidth = 752f - 48f, SmallPaneWidth = 300f, StockBodyWidth = 548f - 48f;

    /// <summary>The gap either side of the divider in a two-pane body, and the divider's own width.</summary>
    public const float PaneGap = 24f, DividerWidth = 1f;

    /// <summary>The preview pane is never narrower than this; a card too narrow for it keeps the preview under the form, at the height the stock card gives it.</summary>
    public const float PaneMinWidth = 240f, InlinePreviewHeight = 220f;

    /// <summary>The kind switch's items at Compact are at least this wide before they wrap, and the gap between them.</summary>
    public const float KindMinWidth = 64f, KindGap = 20f;

    /// <summary>The kind words' weights (the picked one heavier: Z-Dialogs).</summary>
    public const ushort KindSelectedWeight = 600, KindRestWeight = 400;

    /// <summary>The Compact preview row's padding either side, its gap and its chevron.</summary>
    public const float FoldPadX = 12f, FoldGap = 8f, FoldChevronSize = 12f;

    /// <summary>The report form's free-text boxes (the stock and the question / idea body), its combo width, the preview's padding and text inset, and the gap between copy and save.</summary>
    public const float FieldHeight = 72f, BodyFieldHeight = 140f, ComboWidth = 260f, PreviewPadding = 10f, PreviewTextInset = 20f, DoorGap = 20f;

    /// <summary>The crash prompt: the gap between its links, the bullet glyph and its top nudge, the dump caption's indent and the gap above it.</summary>
    public const float LinkGap = 12f, BulletGlyphSize = 14f, BulletGlyphTop = 2f, DumpCaptionIndent = 28f, DumpGap = 2f;
}
