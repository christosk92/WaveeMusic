// ── Themes/Zune/Style/ZuneDensity.cs — the row size of the Zune style (themes plan 6.3, Z1) ──────────────────────────────────
//
// `style.density`: "row size · every song list" on Z-Theme-Options, three chips: compact 44, standard 52 (the default), comfortable 60
// (Z-Tokens: "row 52 (compact 44 · comfortable 60)"). A density is a METRICS preset: it moves the row heights, the pane item and the card
// gap together (`StyleMetrics`, contract 1.26) and the track rows the core's table sizes from (`StyleMetrics.TrackRows`). Changing it
// re-lays out every list, so it applies on the theme transaction (`ApplyMode.Transaction`), never live.
//
// Decision 3 of P5: the 4K boards' "rows 60" does not override the user's density; 4K keeps the option's row.

using System.Collections.Immutable;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

/// <summary>The row sizes of the Zune style. The values are persisted: never renumber, append.</summary>
public enum ZuneDensity : byte
{
    /// <summary>44-DIP rows.</summary>
    Compact = 0,
    /// <summary>52-DIP rows, the default.</summary>
    Standard = 1,
    /// <summary>60-DIP rows.</summary>
    Comfortable = 2,
}

/// <summary>The density option and the numbers behind each rung.</summary>
public static class ZuneDensityOption
{
    /// <summary>The option: three chips in the track lists group, applied on the theme transaction.</summary>
    public static SelectOption<ZuneDensity> Option { get; } = new(
        Id: "density",
        TitleKey: ZuneStyleKeys.Density,
        DescriptionKey: ZuneStyleKeys.DensitySub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists,
        Default: ZuneDensity.Standard,
        Choices: ImmutableArray.Create(
            new SelectChoice<ZuneDensity>(ZuneDensity.Compact, ZuneStyleKeys.DensityCompact),
            new SelectChoice<ZuneDensity>(ZuneDensity.Standard, ZuneStyleKeys.DensityStandard),
            new SelectChoice<ZuneDensity>(ZuneDensity.Comfortable, ZuneStyleKeys.DensityComfortable)),
        Picker: PickerStyle.Radio)
    {
        Apply = ApplyMode.Transaction,
    };

    /// <summary>The row height at <paramref name="density"/> (Z-Tokens: 44 / 52 / 60): a one-line row, a two-line row and a row with art.</summary>
    public static float Row(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => 44f,
        ZuneDensity.Comfortable => 60f,
        _ => 52f,
    };

    /// <summary>A pane item at <paramref name="density"/>: the settings section list's 40 (Z-Theme-Options) at standard, never under the
    /// 40 a Compact hit target needs, 48 when comfortable.</summary>
    public static float PaneItem(ZuneDensity density) => density == ZuneDensity.Comfortable ? 48f : 40f;

    /// <summary>The gap between cards at <paramref name="density"/>: the boards' 16 at standard (Z-Home quickplay, Z-Library-Albums,
    /// Z-Sizes-Breakpoints "2 columns, 16 gap"), a step tighter or looser around it.</summary>
    public static float CardGap(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => 12f,
        ZuneDensity.Comfortable => 20f,
        _ => 16f,
    };

    /// <summary>The track rows at <paramref name="density"/>. Zune draws one row skin, so both skins the core can pick carry the same row:
    /// 44 / 52 / 60 tall with a 32 / 36 / 40 cover (the panel's 36 art at standard, Z-Part-Panel), episode rows 88 / 96 / 112.</summary>
    public static TrackRowMetrics Rows(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => new(Rung: 0, ModernRow: 44f, ModernArt: 32f, ClassicRow: 44f, ClassicArt: 32f, EpisodeRow: 88f, NarrowEpisode: true),
        ZuneDensity.Comfortable => new(Rung: 2, ModernRow: 60f, ModernArt: 40f, ClassicRow: 60f, ClassicArt: 40f, EpisodeRow: 112f, NarrowEpisode: false),
        _ => new(Rung: 1, ModernRow: 52f, ModernArt: 36f, ClassicRow: 52f, ClassicArt: 36f, EpisodeRow: 96f, NarrowEpisode: true),
    };

    /// <summary>The density in a snapshot of the style's option values.</summary>
    public static ZuneDensity Of(OptionValues style) => style.Get(Option.Key);
}
