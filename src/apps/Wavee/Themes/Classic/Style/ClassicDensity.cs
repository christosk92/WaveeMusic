// ── Themes/Classic/Style/ClassicDensity.cs — the row density of Wavee Classic (themes plan 6.1, M3) ───────────────────────
//
// `style.density`: how much music fits on screen. It replaces the core preference `detail.rowdensity` (0 Compact · 1 Default · 2 Cozy · 3 Comfortable, the same
// numbers, so a stored value carries over unchanged: `ThemeBootstrap` step "TrackRowDensity"). A style option is a metrics preset: `ClassicStyle.Metrics`
// publishes `Rows(density)` as `StyleMetrics.TrackRows`, and the core's track table sizes its rows, art and episode rows from that fact
// (`Track.RowMetrics.Current`). The ladders are the ones the table always had: Modern 40/48/56/64 with a 32/32/40/48 art edge, Classic 36/40/44/48 with a 32/32/32/40
// edge, episode rows 88/96/104/112; both skins' ladders are published because the row style (M2) is a structure option the style cannot read; the core picks by `ListPolicy.RowStyle`.
//
// WHO READS AND WRITES IT. An option belongs to its layer (WVTH005, WVTH009), so the structure's density controls (the track table's "Row size" button, panel and
// submenu, the Settings picker's cards) go through the accessors here: `Current` is the tracked read, `Request` a request that lands in `ThemeHost.Apply`.

using FluentGpu.Theming;
using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The track-row densities of Wavee Classic. The values are persisted (and are the legacy preference's numbers): never renumber, append.</summary>
public enum RowDensity : byte
{
    /// <summary>The tightest rows.</summary>
    Compact = 0,
    /// <summary>What the app shipped with.</summary>
    Default = 1,
    /// <summary>A little more air.</summary>
    Cozy = 2,
    /// <summary>The roomiest rows.</summary>
    Comfortable = 3,
}

/// <summary>The density option and the ladders behind it.</summary>
public static class ClassicDensity
{
    /// <summary>The scale the Settings row-density MINIATURE draws the real row geometry at: here, not in the picker, so "the preview mirrors the real row" is a tested number.</summary>
    public const float PreviewScale = 0.25f;

    /// <summary>The layer-qualified id Settings and the palette know the option by (<c>style.density</c>).</summary>
    public static string QualifiedId => "style." + Option.Id;

    /// <summary>The option: a Cards picker in the track lists group of Settings › Appearance.</summary>
    public static SelectOption<RowDensity> Option { get; } = new(
        Id: "density",
        TitleKey: Strings.Settings.Appearance.RowDensity,
        DescriptionKey: Strings.Settings.Appearance.RowDensitySub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists,
        Default: RowDensity.Default,
        Choices: ImmutableArray.Create(
            new SelectChoice<RowDensity>(RowDensity.Compact, Strings.Settings.Choice.Compact),
            new SelectChoice<RowDensity>(RowDensity.Default, Strings.Settings.Choice.Default),
            new SelectChoice<RowDensity>(RowDensity.Cozy, Strings.Settings.Choice.Cozy),
            new SelectChoice<RowDensity>(RowDensity.Comfortable, Strings.Settings.Choice.Comfortable)),
        Picker: PickerStyle.Cards);

    /// <summary>The track rows at <paramref name="density"/>.</summary>
    public static TrackRowMetrics Rows(RowDensity density) => density switch
    {
        RowDensity.Compact => new(Rung: 0, ModernRow: 40f, ModernArt: 32f, ClassicRow: 36f, ClassicArt: 32f, EpisodeRow: 88f, NarrowEpisode: true),
        RowDensity.Cozy => new(Rung: 2, ModernRow: 56f, ModernArt: 40f, ClassicRow: 44f, ClassicArt: 32f, EpisodeRow: 104f, NarrowEpisode: false),
        RowDensity.Comfortable => new(Rung: 3, ModernRow: 64f, ModernArt: 48f, ClassicRow: 48f, ClassicArt: 40f, EpisodeRow: 112f, NarrowEpisode: false),
        _ => new(Rung: 1, ModernRow: 48f, ModernArt: 32f, ClassicRow: 40f, ClassicArt: 32f, EpisodeRow: 96f, NarrowEpisode: true),
    };

    /// <summary>The track rows for a snapshot of the style's option values (what <c>ClassicStyle.Metrics</c> publishes).</summary>
    public static TrackRowMetrics Rows(OptionValues style) => Rows(style.Get(Option.Key));

    /// <summary>The density now. A tracked read: a component or binding that calls it re-runs when the density changes.</summary>
    public static RowDensity Current(IOptionReader options) => options.Get(Option.Key);

    /// <summary>Asks for <paramref name="density"/> (lands in <c>ThemeHost.Apply</c>; an unchanged value writes nothing).</summary>
    public static void Request(IOptionRequests requests, RowDensity density) => requests.Set(Option.Key, density);
}
