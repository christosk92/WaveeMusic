// ── Themes/Zune/Structure/Rules/ZuneOverlayRules.cs — the numbers and the tier records of Zune's overlays and dialogs (themes plan 6.3, P5 Z8a) ──
//
// What the overlays hold as numbers: the account chip and menu, the context menu's header and round-verb strip (and the sheet it becomes at Compact), the notification
// panel, the update toast's card, the runtime banner, the offline strip and vacancy, and the dialog bodies (log out, play a link, the signature sheet, the destination
// picker, the credits sheet). They are data, not decisions: the views under `Views\Overlays` and `Views\Dialogs` read them (WVTH003 allows literals only in `Rules\**`).
//
// BOARDS: `Z-Overlays` (the account menu 304 wide, the track menu 320 wide with a 40 tile, a strip of four 32 round verbs 18 apart, 36 rows inset 16, the notification panel
// 380 wide, toasts 360 wide bottom right above the bar), `Z-Overlays-Small`, `Z-Overlays-Compact` (a 52 toast filling the window less 16, a 40 offline strip edge to edge),
// `Z-Dialogs` and its tiers (the plate #1f1f1f, 24 in, a 26/300 title, a round "go" disc beside its word, the other commands as words). No board draws the log-out card, the
// play-a-link card, the signature sheet, the destination picker, the credits sheet or the Compact context sheet: they are built from the parts above (owner Q18).
//
// ONE RECORD per window tier (`ZuneOverlayTiers.For(window)`), read by the views AND by the core hosts through the policy (`ThemePolicies.Overlays`, built from the same
// records): Compact is the sheet, the full-width toast, the 40-DIP targets and the quiet vacancy; Small and Standard the board; Huge the same with the form dialogs'
// two-pane 1040 card (Z8b's `ZuneDialogRules.HugeCardWidth`). Nothing here names a width edge: the ladder is `ZuneTiers`'.
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The constants of Zune's overlays and dialogs.</summary>
public static class ZuneOverlayRules
{
    // ── the account chip ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The avatar on the chip, the gap to the name, the chip's height (and its Compact target), the unknown name's skeleton bar and its corner.</summary>
    public const float ChipAvatar = 24f, ChipGap = 8f, ChipHeight = 32f, ChipTouchHeight = 40f, ChipNameBarHeight = 12f, SkeletonBarRadius = 2f;

    /// <summary>The chip's padding with and without the name (the named form carries 6 more on the right: part of the chrome budget).</summary>
    public static readonly Edges4 ChipPaddingNamed = new(4f, 0f, 10f, 0f), ChipPaddingBare = new(4f, 0f, 4f, 0f);

    /// <summary>The chip as an account ROW (a drawer form no Zune frame mounts, kept so the key is total): its height, avatar and gap, and its name and line under it.</summary>
    public const float DrawerChipH = 48f, DrawerChipAvatar = 32f, DrawerChipGap = 10f;

    /// <summary>The row form's padding.</summary>
    public static readonly Edges4 DrawerChipPadding = new(8f, 0f, 8f, 0f);

    // ── the account menu (board Z-Overlays, 304 wide) ───────────────────────────────────────────────────────────────

    /// <summary>The menu's width (the rows and the header share it).</summary>
    public const float ProfileMenuWidth = 304f;

    /// <summary>The menu's padding: 8 on top (the plate's own 8 below the rows is the menu style's), so the header sits 16 from the top edge.</summary>
    public static readonly Edges4 ProfileMenuPadding = new(0f, 8f, 0f, 0f);

    /// <summary>The header: its avatar (a circle), the gap to the name, the gap between the name and the tier line, and the name's weight (the display voice at the light weight).</summary>
    public const float HeaderAvatar = 40f, HeaderGap = 12f, HeaderLinesGap = 0f;

    /// <summary>The name's weight (300: the display light).</summary>
    public const ushort HeaderNameWeight = 300;

    /// <summary>The header's padding (16 in, 12 under it before the first row).</summary>
    public static readonly Edges4 HeaderPadding = new(16f, 8f, 16f, 12f);

    /// <summary>The unknown name's bar: its width and height, and its margin (it keeps the header's height).</summary>
    public const float HeaderNameBarWidth = 140f, HeaderNameBarHeight = 14f;

    /// <summary>The unknown name's bar margin.</summary>
    public static readonly Edges4 HeaderNameBarMargin = new(0f, 7f, 0f, 7f);

    /// <summary>The tier line: the spacer a Free badge keeps where Premium has its star, and the star.</summary>
    public const float TierStar = 10f, TierGap = 5f;

    // ── the context menu (board Z-Overlays "track menu") ─────────────────────────────────────────────────────────────

    /// <summary>The header's tile: 40, a circle for an artist (the radius), and its decode size.</summary>
    public const float MenuTile = 40f, MenuTileCircleRadius = 20f;

    /// <summary>The tile's decode size in pixels (twice the tile).</summary>
    public const int MenuTileDecodePx = 80;

    /// <summary>The header: the gap between the tile and the words, and its padding (12 above, 10 under).</summary>
    public const float MenuHeaderGap = 12f;

    /// <summary>The header's padding.</summary>
    public static readonly Edges4 MenuHeaderPadding = new(16f, 4f, 16f, 10f);

    /// <summary>The verb strip: the gap between the round verbs, its padding, the disc of a pointer menu and of a touch menu (Compact's 40 target), the ring's width (the outline
    /// button's 2) and the glyph inside.</summary>
    public const float StripGap = 18f, StripDisc = 32f, StripDiscTouch = 40f, StripRing = 2f, StripGlyph = 14f, StripGlyphTouch = 16f;

    /// <summary>The strip's padding.</summary>
    public static readonly Edges4 StripPadding = new(16f, 4f, 16f, 12f);

    /// <summary>The key a strip verb is mounted under (the verb's index follows), the strip's and the header's.</summary>
    public const string StripVerbKeyPrefix = "zmenu:verb:", StripKey = "zmenu:strip", MenuHeaderKey = "zmenu:header";

    // ── the context sheet (Compact: a pointer menu becomes a sheet over the page's lower edge) ───────────────────────

    /// <summary>The scrim over the page: black at 72 % (the hub overlay of board Z-Overlays-Compact), starting under the title row.</summary>
    public static ColorF Scrim => ColorF.FromRgba(0, 0, 0, 184);

    /// <summary>The sheet's header: its height and padding, the gap between the tile and the words.</summary>
    public const float SheetHeaderHeight = 64f, SheetHeaderGap = 12f;

    /// <summary>The sheet's header padding.</summary>
    public static readonly Edges4 SheetHeaderPadding = new(16f, 8f, 16f, 8f);

    /// <summary>The sheet's top inset (a quiet 8 above the header, no grabber) and the padding under its last row.</summary>
    public const float SheetTop = 8f, SheetBottom = 12f;

    /// <summary>A sheet row: its height (a 40-DIP target and more), side padding, the trailing glyph and the gap to it.</summary>
    public const float SheetRowHeight = 48f, SheetRowPadX = 16f, SheetRowGap = 12f, SheetRowTrail = 12f;

    /// <summary>The sheet's divider margin (a hairline 16 in, 6 above and below, the menu's).</summary>
    public static readonly Edges4 SheetDividerMargin = new(16f, 5f, 16f, 5f);

    /// <summary>The entry slide (up from below).</summary>
    public const float SheetEnterDy = 48f;

    /// <summary>The sheet's card key, and the key a sheet row is mounted under (the row's index follows).</summary>
    public const string SheetCardKey = "zsheet:card", SheetRowKeyPrefix = "zsheet:row:";

    // ── the notification panel (board Z-Overlays, 380 wide) ──────────────────────────────────────────────────────────

    /// <summary>The panel's key, and the key a filter word is mounted under (the filter's number follows).</summary>
    public const string PanelKey = "zn:panel", FilterKeyPrefix = "notify-filter:";

    /// <summary>The panel's width, the feed's tallest and the whole panel's tallest.</summary>
    public const float PanelWidth = 380f, FeedMaxHeight = 420f, PanelMaxHeight = 520f;

    /// <summary>What sits above the feed (the title row, the filter words and their gaps): a window too short for the whole panel bounds the feed by the room less this.</summary>
    public const float PanelFixedHeight = 108f;

    /// <summary>The feed is never shorter than this much of a row.</summary>
    public const float FeedFloor = 56f;

    /// <summary>The panel's padding (20 in) and the gap between its parts (10).</summary>
    public static readonly Edges4 PanelPadding = new(20f, 16f, 20f, 12f);

    /// <summary>The gap between the panel's parts, and between the title and its words.</summary>
    public const float PanelGap = 10f, HeaderWordsGap = 16f;

    /// <summary>The filter words' gap.</summary>
    public const float FilterGap = 16f;

    /// <summary>A row: its least height, the gap between its columns, its art (a 40 square, the social avatar a circle), the vertical padding, and the enter and exit slides.</summary>
    public const float RowMinHeight = 52f, RowGap = 12f, RowArt = 40f, RowGlyph = 20f, RowPadY = 6f, RowEnterDy = 6f, RowExitDy = -4f;

    /// <summary>The row art's decode size in pixels (twice the art).</summary>
    public const int RowArtDecodePx = 80;

    /// <summary>The unread dot (a reserved column: it never moves the row's words) and its radius.</summary>
    public const float UnreadDot = 6f, UnreadDotRadius = 3f;

    /// <summary>The pending-sync line: its glyph and its gap.</summary>
    public const float PendingIcon = 12f, PendingGap = 8f;

    /// <summary>The gap between a row's lines, between an update row's lines, and between its words.</summary>
    public const float LineGap = 1f, UpdateLineGap = 3f, UpdateWordsGap = 4f;

    /// <summary>The update row's progress strip: the bar's width and its gap to the percent.</summary>
    public const float UpdateBarWidth = 200f, UpdateStripGap = 8f;

    /// <summary>An expanded activity row's detail: the gap between its lines, its margin (under the art's column), and the target line's gap to its jump.</summary>
    public const float DetailGap = 4f, DetailTargetGap = 8f;

    /// <summary>The detail's left margin (the art's column and its gap).</summary>
    public static readonly Edges4 DetailMargin = new(RowArt + RowGap, 0f, 0f, 6f);

    /// <summary>The empty line: its least height, its padding and its measure.</summary>
    public const float EmptyMinHeight = 120f, EmptyPadding = 24f, EmptyMessageMaxWidth = 280f;

    /// <summary>The mono face of the update percent.</summary>
    public const string PercentFace = "Cascadia Code";

    // ── the update toast's card ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The download bar's width on a roomy lane; the toast frame's own padding (the engine pads a custom body 16 on every side).</summary>
    public const float UpdateCardBarWidth = 240f, ToastFramePad = 16f;

    /// <summary>A toast slab's width at the desktop tiers (board Z-Overlays: 360; the control table's `ToastLaneStyle` says the same). The shell provides the lane's geometry from the
    /// policy and that provider wins over the table, so the policy carries it too.</summary>
    public const float ToastCardWidth = 360f;

    /// <summary>The toast lane of the desktop tiers: the slab 360 wide, 24 in from the right edge (the engine's side margin).</summary>
    public static ToastLaneStyle DesktopToastLane { get; } = new() { CardMinWidth = ToastCardWidth, CardMaxWidth = ToastCardWidth };

    // ── the runtime banner ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The banner's inner cap.</summary>
    public const float BannerMaxWidth = 560f;

    // ── the offline strip (board Z-Overlays: an amber slab, a word for retry; Z-Overlays-Compact: 40 tall, edge to edge) ──

    /// <summary>The strip's key.</summary>
    public const string OfflineStripKey = "offline-strip";

    /// <summary>The strip's glyph and the gap to the notice, and the gap before its retry word.</summary>
    public const float OfflineIcon = 16f, OfflineStripGap = 12f;

    /// <summary>The cloud-off glyph of the offline voice (the strip and the vacancy): Segoe Fluent Icons U+EC9C, absent from the engine's glyph table.</summary>
    public const string OfflineGlyph = "";

    // ── the offline vacancy ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The glyph (40, tertiary) and the step from it to the title.</summary>
    public const float OfflineVacancyGlyph = 40f, OfflineVacancyTitleTop = 12f;

    /// <summary>The column's gap, the caption's widest measure and the step to the actions.</summary>
    public const float OfflineVacancyGap = 4f, OfflineVacancyCaptionMaxW = 360f, OfflineVacancyActionsTop = 16f;

    /// <summary>The gap between the actions in a row, and stacked (Compact: a column).</summary>
    public const float OfflineVacancyActionsGap = 20f, OfflineVacancyStackGap = 4f;

    /// <summary>The column sits above the page's centre: 120 of bottom padding lifts it.</summary>
    public const float OfflineVacancyLift = 120f;

    // ── the dialogs ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bare plate's key, and its command row's.</summary>
    public const string PlateKey = "zdialog:plate", CommandsKey = "zdialog:commands";

    /// <summary>The account menu's header key.</summary>
    public const string ProfileHeaderKey = "zprofile:header";

    /// <summary>The plate's gap between its parts (the stock dialog's 14) and the gap between its commands (20).</summary>
    public const float PlateGap = 14f, CommandGap = 20f;

    /// <summary>The log-out card: 420 wide, never wider than 480.</summary>
    public const float LogoutWidth = 420f, LogoutMaxWidth = 480f;

    /// <summary>The play-a-link card: 480 wide, never wider than 548; its status line's reserved height (so the commands never jump when a status appears).</summary>
    public const float PlayLinkWidth = 480f, PlayLinkMaxWidth = 548f, PlayLinkStatusHeight = 18f;

    /// <summary>The signature sheet: a summary, a divider and label / value rows; the body's tallest and a row's gap.</summary>
    public const float SignatureMaxHeight = 560f, SignatureRowGap = 2f, SignatureBodyGap = 12f;

    /// <summary>The destination picker: its panel width, the list's cap, the empty line's height and padding, what the panel holds above the list, and the list's gap.</summary>
    public const float PickerPanelWidth = 320f, PickerListMaxHeight = 360f, PickerEmptyHeight = 44f, PickerHeadHeight = 96f, PickerListGap = 0f;

    /// <summary>The empty line's padding.</summary>
    public static readonly Edges4 PickerEmptyPadding = new(8f, 0f, 8f, 0f);

    /// <summary>A picker row: the height with and without art, the gap, the left padding before the indent, the right padding.</summary>
    public const float PickerRowArtHeight = 48f, PickerRowHeight = 40f, PickerRowGap = 12f, PickerRowLeft = 8f, PickerRowRight = 8f;

    /// <summary>The tree indent per level.</summary>
    public const float PickerIndentStep = 12f;

    /// <summary>A row's lead: the art (40, a cover's corner), the plated glyph and the bare glyph, and the art's decode size.</summary>
    public const float PickerLead = 40f, PickerPlatedIcon = 20f, PickerBareIcon = 18f;

    /// <summary>The art's decode size.</summary>
    public const int PickerLeadDecodePx = 80;

    /// <summary>The credits sheet: the body's cap, the list's cap and the loading bars' gap.</summary>
    public const float CreditsMaxWidth = 440f, CreditsListMaxHeight = 420f, CreditsLoadingGap = 10f, CreditsRowGap = 8f;

    /// <summary>The loading bars: heights and widths (a short heading bar, then three lines).</summary>
    public const float CreditsBar1H = 12f, CreditsBar1W = 120f, CreditsBar2H = 14f, CreditsBar2W = 220f, CreditsBar3W = 190f, CreditsBar4W = 210f;
}

/// <summary>What one window tier decides for Zune's overlays and dialogs.</summary>
public sealed record ZuneOverlayLayout
{
    /// <summary>The window tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>What the core hosts read (the dialog card's fit, the toast dock, the vacancy, the selection bar, context sheets).</summary>
    public required OverlayLayout Core { get; init; }

    /// <summary>The title row the sheet's scrim starts under and a panel keeps clear (the frame record's).</summary>
    public required float ChromeRow { get; init; }

    // ── the bare modal cards (log out, play a link) ───────────────────────────────────────────────────────────────────

    /// <summary>The card fills the window less its margin (Compact), instead of its own width.</summary>
    public required bool CardFills { get; init; }

    /// <summary>The window margin the card keeps on each side.</summary>
    public required float CardMargin { get; init; }

    /// <summary>The card's padding.</summary>
    public required Edges4 CardPadding { get; init; }

    /// <summary>A single-line field's height (the stock 32; Compact 40).</summary>
    public required float FieldHeight { get; init; }

    // ── flyout panels (the account menu, the notification panel, the runtime banner) ──────────────────────────────

    /// <summary>The window margin a panel keeps on each side.</summary>
    public required float PanelMargin { get; init; }

    // ── the account chip ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chip's height (the stock 32; Compact 40).</summary>
    public required float ChipHeight { get; init; }

    // ── the notification panel ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row's least height (52; Compact 56) and a word's least height (the filter words, the row actions: Compact 40, else 32).</summary>
    public required float RowMinHeight { get; init; }

    /// <summary>A word's least height.</summary>
    public required float WordHeight { get; init; }

    // ── the offline strip and vacancy ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's height (36; Compact 40) and side padding.</summary>
    public required float StripHeight { get; init; }

    /// <summary>The strip's side padding.</summary>
    public required float StripPadX { get; init; }

    /// <summary>The strip says the board's short notice ("you're offline · cached music still plays", Compact) and not the long one.</summary>
    public required bool StripShortNotice { get; init; }

    /// <summary>The vacancy's headline is the group voice (22/28) and not the page title (Compact).</summary>
    public required bool VacancyQuietTitle { get; init; }

    /// <summary>The vacancy's side padding.</summary>
    public required float VacancyPadX { get; init; }

    /// <summary>The vacancy's actions stack (a column of 40-tall targets) instead of sitting in one row.</summary>
    public required bool VacancyActionsStack { get; init; }

    /// <summary>The vacancy's lift above the page's centre at a Regular height.</summary>
    public required float VacancyLift { get; init; }

    // ── the pickers ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A destination row's height with art and without (Compact keeps both at 44 or more).</summary>
    public required float PickerRowArtHeight { get; init; }

    /// <summary>A destination row's height without art.</summary>
    public required float PickerRowHeight { get; init; }

    // ── the arithmetic (a view passes the live viewport and takes the answer) ────────────────────────────────────

    /// <summary>The width of a bare card whose own width is <paramref name="nominal"/> (bounded by <paramref name="max"/>) in a window <paramref name="viewportWidth"/> wide.</summary>
    public float CardWidth(float nominal, float max, float viewportWidth)
    {
        float room = MathF.Max(1f, viewportWidth - 2f * CardMargin);
        float want = CardFills ? room : nominal;
        return MathF.Min(MathF.Min(want, max), room);
    }

    /// <summary>The widest a flyout panel built for <paramref name="nominal"/> may be in a window <paramref name="viewportWidth"/> wide.</summary>
    public float PanelWidth(float nominal, float viewportWidth) => MathF.Min(nominal, MathF.Max(1f, viewportWidth - 2f * PanelMargin));

    /// <summary>The tallest a flyout panel may be in a window <paramref name="viewportHeight"/> tall: bounded by the window less the title row and the margins above and below.</summary>
    public float PanelHeight(float nominal, float viewportHeight) => MathF.Min(nominal, MathF.Max(1f, viewportHeight - ChromeRow - 2f * PanelMargin));

    /// <summary>The tallest the scrolling feed of a panel may be: <paramref name="nominalBody"/>, bounded by the panel's height less <paramref name="fixedParts"/>, never under a row.</summary>
    public float PanelBodyHeight(float nominalBody, float nominalPanel, float fixedParts, float viewportHeight)
        => MathF.Max(ZuneOverlayRules.FeedFloor, MathF.Min(nominalBody, PanelHeight(nominalPanel, viewportHeight) - fixedParts));

    /// <summary>The download bar's width in a window <paramref name="viewportWidth"/> wide: the roomy lane's 240, never wider than the lane's card less the toast frame's padding.</summary>
    public float UpdateBarWidth(float viewportWidth)
    {
        var lane = Core.Toast.Lane ?? ToastLaneStyle.Default;
        float card = lane.FillWidth ? viewportWidth - 2f * lane.SideMargin : lane.CardMaxWidth;
        return MathF.Min(ZuneOverlayRules.UpdateCardBarWidth, MathF.Max(1f, card - 2f * ZuneOverlayRules.ToastFramePad));
    }
}

/// <summary>Zune's overlay tier records.</summary>
public static class ZuneOverlayTiers
{
    /// <summary>Compact (boards Z-Overlays-Compact, Z-Dialogs-Compact): the context sheet, the full-width toast above the 64 mini bar, cards that fill the window less 16, 40-DIP targets,
    /// the quiet vacancy.</summary>
    public static ZuneOverlayLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact,
        Core = new OverlayLayout(
            // Every dialog's commands are the engine's buttons at the 40-DIP target; Zune's commands are words, so the floor is a word's.
            Dialog: new DialogFit(Fill: true, Inset: 16f, MaxWidth: 548f, EdgeY: 16f,
                ContentPadding: new Edges4(16f, 20f, 16f, 12f), CommandPadding: new Edges4(16f, 4f, 16f, 16f),
                CommandSize: ControlSize.Large, CommandMinWidth: 48f, UniformCommands: false),
            // Z-Overlays-Compact: the slab is the window less 16 a side, 12 above the 64 mini bar, its action a 40-DIP word.
            Toast: new ToastDock(ToastPlacement.BottomCenter, GapAboveBar: 12f, BarHeight: ZuneTiers.Frame(WidthTier.Compact).BarH,
                Lane: new ToastLaneStyle { SideMargin = 16f, FillWidth = true, ActionSize = ControlSize.Large }),
            Vacancy: new VacancyFit(QuietTitle: true, Padding: 16f, ActionHeight: 40f),
            Selection: new SelectionFit(MinHeight: 48f, SideInset: 8f, Padding: new Edges4(4f, 4f, 4f, 4f)),
            ContextSheet: true, MenuSize: ControlSize.Large),
        ChromeRow = ZuneTiers.Frame(WidthTier.Compact).TitleBarH,
        CardFills = true, CardMargin = 16f, CardPadding = new Edges4(16f, 20f, 16f, 16f), FieldHeight = 40f,
        PanelMargin = 8f, ChipHeight = ZuneOverlayRules.ChipTouchHeight,
        RowMinHeight = 56f, WordHeight = 40f,
        StripHeight = 40f, StripPadX = 16f, StripShortNotice = true,
        VacancyQuietTitle = true, VacancyPadX = 16f, VacancyActionsStack = true, VacancyLift = ZuneOverlayRules.OfflineVacancyLift,
        PickerRowArtHeight = 48f, PickerRowHeight = 44f,
    };

    /// <summary>Small and Standard (boards Z-Overlays, Z-Overlays-Small, Z-Dialogs): the toast bottom right 12 above the bar, the 548 plates.</summary>
    public static ZuneOverlayLayout Standard { get; } = new()
    {
        Tier = WidthTier.Standard,
        Core = new OverlayLayout(
            DialogFit.Stock with { CommandMinWidth = 48f },
            new ToastDock(ToastPlacement.BottomRight, GapAboveBar: 12f, Lane: ZuneOverlayRules.DesktopToastLane),
            VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false),
        ChromeRow = ZuneTiers.Frame(WidthTier.Standard).TitleBarH,
        CardFills = false, CardMargin = 16f, CardPadding = Edges4.All(24f), FieldHeight = 32f,
        PanelMargin = 16f, ChipHeight = ZuneOverlayRules.ChipHeight,
        RowMinHeight = ZuneOverlayRules.RowMinHeight, WordHeight = 32f,
        StripHeight = 36f, StripPadX = 16f, StripShortNotice = false,
        VacancyQuietTitle = false, VacancyPadX = 0f, VacancyActionsStack = false, VacancyLift = ZuneOverlayRules.OfflineVacancyLift,
        PickerRowArtHeight = ZuneOverlayRules.PickerRowArtHeight, PickerRowHeight = ZuneOverlayRules.PickerRowHeight,
    };

    /// <summary>Huge and Huge 4K (board Z-Dialogs-Huge): the standard overlays; a dialog never stretches (its width caps at 548), except the two FORM dialogs, whose card is the
    /// two-pane 1040 (Z8b's `ZuneDialogTiers`).</summary>
    public static ZuneOverlayLayout Huge { get; } = Standard with
    {
        Tier = WidthTier.Huge,
        Core = new OverlayLayout(
            DialogFit.Stock with { CommandMinWidth = 48f, FormWidth = ZuneDialogRules.HugeCardWidth },
            new ToastDock(ToastPlacement.BottomRight, GapAboveBar: 12f, Lane: ZuneOverlayRules.DesktopToastLane),
            VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false),
        ChromeRow = ZuneTiers.Frame(WidthTier.Huge).TitleBarH,
    };

    /// <summary>The record of <paramref name="tier"/>: Compact, Huge (and Huge 4K), and the standard record for the tiers between.</summary>
    public static ZuneOverlayLayout For(WidthTier tier) => tier == WidthTier.Compact ? Compact : tier >= WidthTier.Huge ? Huge : Standard;

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneOverlayLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The policy the structure publishes for the core hosts.</summary>
    public static OverlayPolicy Policy { get; } = new(Compact.Core, Standard.Core, Huge.Core);

    // ── what a Short window gives up (a height rule: a 330-tall window leaves the page 210) ───────────────────────

    /// <summary>The vacancy's lift: the board's 120 at a Regular height, none in a Short window.</summary>
    public static float VacancyLiftAt(this ZuneOverlayLayout layout, HeightTier height) => height == HeightTier.Short ? 0f : layout.VacancyLift;

    /// <summary>The vacancy draws its 40-DIP glyph: not in a Short window, where the decoration gives way so the words and the actions fit.</summary>
    public static bool VacancyShowsGlyph(HeightTier height) => height != HeightTier.Short;
}
