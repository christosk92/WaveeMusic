// ── Themes/WinUi/Style/WinUiDensity.cs — the density of the WinUI style (themes plan 6.2, W1) ─────────────────────────────────
//
// `style.density`: a ComboBox with two choices, "Default" and "Compact" (W-ThemeOptions: "Rows 40/48 → 32/40, pane items 36 → 32, card gaps
// 16 → 12. Nothing else moves."). A density is a METRICS preset (`StyleMetrics`): it moves the row heights, the pane item, the card gap and the
// track rows the core's table sizes from (`StyleMetrics.TrackRows`) together, so it applies on the theme transaction, never live.
//
// THE DENSITY CELL IS THE STYLE'S OWN (the Z1 trap). Every style that has a density names its option `density`, and the engine keeps ONE option
// cell per id with ONE value type (`OptionStore.Cell` throws on a second type). So a view that reads the cell typed as ANOTHER style's enum
// (Classic's `RowDensity`, Zune's `ZuneDensity`) while this style is active, or a view that reads it typed `WinUiDensity` while another style is,
// throws. Nothing outside `WinUiStyle` reads this option's cell: a structure reads the density from the PUBLISHED metrics
// (`StyleMetrics.TrackRows.Rung`, through `WinUiDensityOption.OfRung`) and asks for a change by the layer-qualified id (`QualifiedId`), which
// `ThemeHost.Apply` type-checks against the active option (`SelectOption.TryCoerce`), so a request typed for another style is dropped, not stored.

using System.Collections.Immutable;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>The densities of the WinUI style. The values are persisted: never renumber, append.</summary>
public enum WinUiDensity : byte
{
    /// <summary>The board's rows: 40 one-line, 48 two-line with art; pane items 36; cards 16 apart.</summary>
    Standard = 0,
    /// <summary>Rows 32 / 40, pane items 32, cards 12 apart.</summary>
    Compact = 1,
}

/// <summary>The density option and the numbers behind each choice.</summary>
public static class WinUiDensityOption
{
    /// <summary>The layer-qualified id a structure requests a density by (the style's <c>density</c> option).</summary>
    public const string QualifiedId = "style.density";

    /// <summary>The option: a ComboBox in the track lists group ("Default", "Compact"), applied on the theme transaction.</summary>
    public static SelectOption<WinUiDensity> Option { get; } = new(
        Id: "density",
        TitleKey: Strings.Theme.Winui.Style.Density,
        DescriptionKey: Strings.Theme.Winui.Style.DensitySub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists,
        Default: WinUiDensity.Standard,
        Choices: ImmutableArray.Create(
            new SelectChoice<WinUiDensity>(WinUiDensity.Standard, Strings.Theme.Winui.Style.DensityStandard),
            new SelectChoice<WinUiDensity>(WinUiDensity.Compact, Strings.Theme.Winui.Style.DensityCompact)),
        Picker: PickerStyle.Combo)
    {
        Apply = ApplyMode.Transaction,
    };

    /// <summary>A one-line row (W-Tokens: "Row 40 single-line"): 40, 32 when compact.</summary>
    public static float RowSingle(WinUiDensity density) => density == WinUiDensity.Compact ? 32f : 40f;

    /// <summary>A two-line row and a row with art (W-Tokens: "48 two-line with 32 art"): 48, 40 when compact.</summary>
    public static float RowDouble(WinUiDensity density) => density == WinUiDensity.Compact ? 40f : 48f;

    /// <summary>A navigation pane item (W-Tokens: "pane item 36 + 2 margin (40 pitch)"): 36, 32 when compact.</summary>
    public static float PaneItem(WinUiDensity density) => density == WinUiDensity.Compact ? 32f : 36f;

    /// <summary>The gap between cards: 16, 12 when compact.</summary>
    public static float CardGap(WinUiDensity density) => density == WinUiDensity.Compact ? 12f : 16f;

    /// <summary>The track rows at <paramref name="density"/>. The Modern skin is the two-line row with a 32 cover (48, compact 40); the Classic skin
    /// is the one-line row (40, compact 32) with a 32 (compact 24) cover; episode rows keep the core's 96 (compact 88) and narrow arm.</summary>
    public static TrackRowMetrics Rows(WinUiDensity density) => density == WinUiDensity.Compact
        ? new(Rung: 0, ModernRow: 40f, ModernArt: 32f, ClassicRow: 32f, ClassicArt: 24f, EpisodeRow: 88f, NarrowEpisode: true)
        : new(Rung: 1, ModernRow: 48f, ModernArt: 32f, ClassicRow: 40f, ClassicArt: 32f, EpisodeRow: 96f, NarrowEpisode: true);

    /// <summary>The density a published rung names (<c>TrackRowMetrics.Rung</c>: 0 compact, 1 standard); an unknown rung is the default. How a
    /// structure reads the density without touching the option cell.</summary>
    public static WinUiDensity OfRung(int rung) => rung == 0 ? WinUiDensity.Compact : WinUiDensity.Standard;

    /// <summary>The density in a snapshot of the style's option values (the style's pure functions only).</summary>
    internal static WinUiDensity Of(OptionValues style) => style.Get(Option.Key);
}
