// ── Themes/Classic/Structure/Lists/ClassicTrackDensity.cs — the track table's row-size control (themes plan 6.1, M3) ─────────────
//
// From `Entities/Track.Table.Chrome.cs`: the "…" menu's "Row size" radio rows. Tuned Classic D3 moved Row size off the list bar into "…", so the inline button
// and the stepped-slider panel it opened are gone. The density is the style's option (`ClassicDensity`); a pick is a request the app applies
// (`ThemeHost.Apply`), so the rows re-size live.

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>Classic's row-size control for the track table.</summary>
internal static class ClassicTrackDensity
{
    /// <summary>The word for a density in the table's chrome.</summary>
    public static string Label(RowDensity density) => Loc.Get(density switch
    {
        RowDensity.Compact => Strings.Detail.Density.Compact,
        RowDensity.Cozy => Strings.Detail.Density.Cozy,
        RowDensity.Comfortable => Strings.Detail.Density.Comfortable,
        _ => Strings.Detail.Density.Default,
    });

    /// <summary>The overflow menu's rows: one radio per density, the current one checked (read now, when the menu opens).</summary>
    public static IReadOnlyList<MenuFlyoutItem> Items()
    {
        var current = ClassicDensity.Current(ThemeHost.Options);
        var items = new List<MenuFlyoutItem>(4);
        for (int i = 0; i < 4; i++)
        {
            var value = (RowDensity)i;
            items.Add(MenuFlyoutItem.RadioItem(Label(value), current == value, () => Set(value)));
        }
        return items;
    }

    static void Set(RowDensity density) => ClassicDensity.Request(ThemeHost.Options.RequestForwarder, density);
}
