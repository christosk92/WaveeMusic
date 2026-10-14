// ── Themes/Classic/Structure/Lists/ClassicTrackDensity.cs — the track table's row-size control (themes plan 6.1, M3) ─────────────
//
// From `Entities/Track.Table.Chrome.cs`: what the "Row size" button wears, the panel it opens (the current density's name over a stepped slider) and the overflow
// menu's radio rows. The density is the style's option (`ClassicDensity`); the writes are requests the app applies (`ThemeHost.Apply`), so a slider step re-sizes
// the rows live exactly as the old preference write did. The core keeps the button's overlay (open, close, the label it holds while the panel is open).
//
// The panel is a popup of its own, mounted under the overlay root: it reads the option cell through `ThemeHost.Options` (the same store the frame provides).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;

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

    /// <summary>The current density's word (a tracked read of the option cell).</summary>
    public static string CurrentLabel() => Label(ClassicDensity.Current(ThemeHost.Options));

    /// <summary>The panel the "Row size" button opens.</summary>
    public static Element Panel() => Embed.Comp(static () => new PanelComponent());

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

    sealed class PanelComponent : Component
    {
        public override Element Render()
        {
            int d = (int)ClassicDensity.Current(ThemeHost.Options);
            // The slider rides a FloatSignal; mirror the option into it so an external change moves the thumb.
            var dv = UseFloatSignal(d);
            UseEffect(() => { dv.Value = d; }, DepKey.From(d));
            return Layer(Edges4.All(Spacing.M), new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = ClassicTrackChromeLayout.DensityPanelMinWidth,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children = [Ui.BodyStrong(Loc.Get(Strings.Detail.Density.RowSize)) with { Grow = 1f }, Ui.Caption(Label((RowDensity)d))],
                    },
                    Slider.Create(dv, v => Set((RowDensity)Math.Clamp((int)MathF.Round(v), 0, 3)),
                        new Slider.SliderOptions
                        {
                            Min = 0f, Max = 3f, Step = 1f, TickFrequency = 1f,
                            ThumbToolTipValueConverter = v => Label((RowDensity)Math.Clamp((int)MathF.Round(v), 0, 3)),
                        },
                        length: ClassicTrackChromeLayout.DensitySliderLength),
                ],
            });
        }
    }
}
