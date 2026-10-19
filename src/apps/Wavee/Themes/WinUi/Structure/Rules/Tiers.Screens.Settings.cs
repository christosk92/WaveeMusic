// ── Themes/WinUi/Structure/Rules/Tiers.Screens.Settings.cs — WinUI's Settings page per window tier (themes plan 6.2; P6 W8b) ─────────────────────────
//
// ONE record per WINDOW tier (`ctx.Tier().Window`: the frame's insets follow the window, so the page's title and the cards ride the same edge), read as
// `WinUiSettingsTiers.Of(ctx.Tier())`. A view never compares a width with a number (WVTH013). Boards: W-Settings (-Compact, -Small, -Huge), W-SettingsSections,
// W-ThemeOptions (-Compact, -Small, -Huge). The page is the title, a SelectorBar of the seven tabs, and the SettingsCards of the tab's sections:
//
//   tier      inset x / top   title        tab strip       cards cap  picker (theme cards)                       aside
//   Compact   16 / 16         28 / 36      44, scrolls     fill       2-up grid, thumbnail over the name         none
//   Small     24 / 20         28 / 36      40, scrolls     1000       one row: a 48 x 32 thumbnail, the name     none
//   Standard  36 / 24         40 / 52      stock           1000       four columns, 16:9 preview, name, caption  none
//   Huge      48 / 32         40 / 52      stock           1200       four columns, as Standard                  the preview column (Appearance)
//
// THE STACK. A SettingsCard puts its control UNDER its label below 560 CONTENT width (the named rung `WinUiScreenRules.StackRung`, W-Tiers: "SettingsCards
// stack their control under the label below 560 content width"): `Of` reads the rung from the facts, so a window of 600 that opens the 280 pane stacks, and
// the Compact boards (400 wide) stack. A toggle keeps its label | state | switch row. TOUCH: Compact asks every control for a 40 hit target (`Target`).
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>How the theme picker's cards are laid out in a tier.</summary>
public enum WinUiThemePickerForm : byte
{
    /// <summary>Standard and Huge (W-Settings, W-Settings-Huge): four equal fluid columns, each a 16:9 preview over the name, its check and a caption.</summary>
    Cards,
    /// <summary>Compact (W-Settings-Compact): a two-column grid of fluid cards, the preview over the name (no caption).</summary>
    Grid,
    /// <summary>Small (W-Settings-Small): one row of equal cards, a small thumbnail beside the name.</summary>
    Row,
}

/// <summary>What the Settings page lays out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="InsetX">The page inset left and right (the frame's).</param>
/// <param name="InsetTop">The page inset above the title (the frame's).</param>
/// <param name="TitleSize">The page title's size: 28 at Compact and Small, 40 (Title Large) at Standard and Huge.</param>
/// <param name="TitleLine">The title's line height (36 / 52).</param>
/// <param name="PageGap">The gap between the title, the tab strip and the body (the board's 16, 12 at Small).</param>
/// <param name="Target">The hit target of the page's controls (Compact 40; 0 keeps each control's own size).</param>
/// <param name="StacksControls">A row's control sits UNDER its label (full width), except a toggle. Always false in the static record: <c>Of</c> sets it from the 560 rung.</param>
/// <param name="TabHeight">The tab strip's item height (0 keeps the stock item).</param>
/// <param name="TabsScroll">The tab strip scrolls sideways (its end fades where more wait).</param>
/// <param name="ColumnMaxW">The cards' column cap (1000; 1200 at Huge; NaN at Compact where the column fills).</param>
/// <param name="Picker">The theme picker's form.</param>
/// <param name="PickerThumbH">The preview's height for the grid form (the fluid forms keep 16:9).</param>
/// <param name="PickerShowsText">The card carries the theme's description under its name.</param>
/// <param name="PickerGap">The gap between picker cards.</param>
/// <param name="PickerPad">The padding of the expander body the picker sits in.</param>
/// <param name="PickerIndent">The extra inset of the expander body's left edge (the board's 52 = 16 + the header glyph's 36; none at Compact).</param>
/// <param name="Aside">The Appearance tab draws the preview column beside the cards (Huge).</param>
/// <param name="SectionTop">The space above a section header (none above the tab's first).</param>
/// <param name="SectionBottom">The space between a section header and its first card.</param>
public sealed record WinUiSettingsLayout(
    WidthTier Tier, float InsetX, float InsetTop, float TitleSize, float TitleLine, float PageGap, float Target, bool StacksControls, float TabHeight, bool TabsScroll,
    float ColumnMaxW, WinUiThemePickerForm Picker, float PickerThumbH, bool PickerShowsText, float PickerGap, float PickerPad, float PickerIndent, bool Aside,
    float SectionTop, float SectionBottom)
{
    /// <summary>The page lays its controls out for touch (the Compact rule: every hit target is at least 40).</summary>
    public bool Touch => Target > 0f;

    /// <summary>The toggle's own cell: 48 x 40 at a hit target, the engine's switch otherwise. 0 is the engine's width.</summary>
    public float ToggleMinWidth => Touch ? 48f : 0f;

    /// <summary>The toggle's least height.</summary>
    public float ToggleMinHeight => Touch ? Target : 32f;

    /// <summary>The cards' column has a cap (every tier but Compact).</summary>
    public bool CapsColumn => !float.IsNaN(ColumnMaxW);
}

/// <summary>WinUI's Settings tier records.</summary>
public static class WinUiSettingsTiers
{
    private static readonly WinUiSettingsLayout[] s_records =
    [
        // Compact (W-Settings-Compact, W-ThemeOptions-Compact): 16 inset, a 28 title, 44-tall scrolling tabs, the rows stack, the picker is a 2-up grid.
        new(WidthTier.Compact, InsetX: 16f, InsetTop: 16f, TitleSize: 28f, TitleLine: 36f, PageGap: 16f, Target: 40f, StacksControls: false, TabHeight: 44f, TabsScroll: true,
            ColumnMaxW: float.NaN, WinUiThemePickerForm.Grid, PickerThumbH: 72f, PickerShowsText: false, PickerGap: 12f, PickerPad: 12f, PickerIndent: 0f, Aside: false,
            SectionTop: 20f, SectionBottom: 8f),
        // Small (W-Settings-Small): 24 / 20, the 28 title, 40-tall tabs, the picker one row of thumbnail + name cards.
        new(WidthTier.Small, InsetX: 24f, InsetTop: 20f, TitleSize: 28f, TitleLine: 36f, PageGap: 12f, Target: 0f, StacksControls: false, TabHeight: 40f, TabsScroll: true,
            ColumnMaxW: 1000f, WinUiThemePickerForm.Row, PickerThumbH: 32f, PickerShowsText: false, PickerGap: 12f, PickerPad: 16f, PickerIndent: 36f, Aside: false,
            SectionTop: 20f, SectionBottom: 8f),
        // Standard (W-Settings, W-SettingsSections): 36 / 24, the 40 title, the 1000 column, four preview cards.
        new(WidthTier.Standard, InsetX: 36f, InsetTop: 24f, TitleSize: 40f, TitleLine: 52f, PageGap: 16f, Target: 0f, StacksControls: false, TabHeight: 0f, TabsScroll: false,
            ColumnMaxW: 1000f, WinUiThemePickerForm.Cards, PickerThumbH: 0f, PickerShowsText: true, PickerGap: 12f, PickerPad: 16f, PickerIndent: 36f, Aside: false,
            SectionTop: 24f, SectionBottom: 8f),
        // Huge (W-Settings-Huge): 48 / 32, the 1200 column, the preview column beside the Appearance cards.
        new(WidthTier.Huge, InsetX: 48f, InsetTop: 32f, TitleSize: 40f, TitleLine: 52f, PageGap: 16f, Target: 0f, StacksControls: false, TabHeight: 0f, TabsScroll: false,
            ColumnMaxW: 1200f, WinUiThemePickerForm.Cards, PickerThumbH: 0f, PickerShowsText: true, PickerGap: 12f, PickerPad: 16f, PickerIndent: 36f, Aside: true,
            SectionTop: 24f, SectionBottom: 8f),
    ];

    private static readonly WinUiSettingsLayout[] s_stacked = Array.ConvertAll(s_records, r => r with { StacksControls = true });

    /// <summary>The record of window tier <paramref name="window"/>, its controls beside their labels (Huge 4K reads Huge's: WinUI has no Huge 4K record).</summary>
    public static WinUiSettingsLayout For(WidthTier window) => s_records[Math.Clamp((int)window, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>: the window tier's, its controls stacked where the content is under 560 wide. Standard's where no frame
    /// resolved the tiers (a view mounted alone).</summary>
    public static WinUiSettingsLayout Of(in TierFacts facts)
    {
        var tier = facts.Policy is null ? WidthTier.Standard : facts.Window;
        return WinUiScreenRules.CardsStack(facts) ? s_stacked[Math.Clamp((int)tier, 0, s_stacked.Length - 1)] : For(tier);
    }

    // ── the theme picker ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The columns of the Compact grid: two.</summary>
    public const int GridColumns = 2;

    /// <summary>The cards of the Standard and Huge row: the board's four columns (three themes and the room for one more).</summary>
    public const int CardColumns = 4;

    /// <summary>The picker card's border (the settings card's 1).</summary>
    public const float PickerBorder = 1f;

    /// <summary>The narrowest a grid card goes (a 300-wide window).</summary>
    public const float GridMinCardW = 96f;

    /// <summary>A grid card's width in a window <paramref name="viewportWidth"/> wide: the window less the page insets, the expander body's padding and borders, and the
    /// gaps between the columns, over the columns. Never under <see cref="GridMinCardW"/>.</summary>
    public static float GridCardWidth(this WinUiSettingsLayout layout, float viewportWidth)
    {
        float room = viewportWidth - 2f * layout.InsetX - 2f * layout.PickerPad - 2f * PickerBorder - (GridColumns - 1) * layout.PickerGap;
        return MathF.Max(GridMinCardW, MathF.Floor(room / GridColumns));
    }

    /// <summary>The name row of a Compact grid card: at least this tall, so the card is a 40+ target (72 thumb + 36 name = 108).</summary>
    public const float GridNameRowH = 36f;
}
