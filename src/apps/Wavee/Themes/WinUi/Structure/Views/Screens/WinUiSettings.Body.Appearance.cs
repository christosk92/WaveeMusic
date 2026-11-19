// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Appearance.cs — Appearance's WinUI cards: App mode and Zoom (W-Settings) ──────────────────────
//
// The Theme card is `WinUiSettings.Theme.cs`; every other Appearance dial is the generic card of its kind. These two carry their glyph (the guide's rule:
// only Theme, App mode and Zoom do) and a ComboBox: App mode's three words (the board draws a combo, not segments), Zoom's "Auto (N%)" head and the twelve
// rungs. Every write goes through the dial's writer; the page re-reads when the writer asks.
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, its signal AND its onChange at mount: the zoom combo is a tiny child component that keeps one stable
// signal in step through an effect, and re-keys when its head label changes plateau.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    /// <summary>App mode: System · Light · Dark in a combo. The writer swaps the installed palette IN PLACE (System re-reads the OS theme and accent), persists,
    /// and asks the host for the re-theme cross-fade; the page then re-reads its rows.</summary>
    static Element AppModeCard(RowCtx x) => AppModeCard(x.Row, BaseCard(x, null));

    /// <summary>The App mode card over <paramref name="card"/> (the page's base card, or a card outside the page: <see cref="AppModeRow"/>).</summary>
    static Element AppModeCard(SettingsBodyRow row, Card card)
    {
        var dial = row.Dial;
        string[] labels = row.Choices.Length > 0 ? [.. row.Choices] : [Loc.Get(Strings.Settings.Choice.System), Loc.Get(Strings.Settings.Choice.Light), Loc.Get(Strings.Settings.Choice.Dark)];
        Element combo = Combo(labels, new Signal<int>(row.Value is int at ? at : 0), width: WinUiSettingsRules.OptionComboWidth, onChange: m =>
        {
            if (dial.Writer.Choose is { } choose && choose(m)) Settings.Bump();
        });
        return DialCard(card with { Control = combo, Glyph = Glyph(row.Glyph) });
    }

    /// <summary>Zoom: a ComboBox, not segments (thirteen items would be thirteen segments wide), with its glyph.</summary>
    static Element ZoomCard(RowCtx x) => DialCard(BaseCard(x, Embed.Comp(new ZoomProps(x.Row.Dial), static () => new ZoomPicker())) with { Glyph = Glyph(x.Row.Glyph) });

    sealed record ZoomProps(SettingsDial Dial);

    /// <summary>The ladder rungs as labels, hoisted once — digits and '%' never change with culture.</summary>
    static readonly string[] s_zoomRungLabels = BuildZoomRungLabels();

    /// <summary>The zoom combo's selection, kept in step with the LIVE zoom by <see cref="ZoomPicker"/>.</summary>
    static readonly Signal<int> s_zoomIndex = new(0);

    static string[] BuildZoomRungLabels()
    {
        var labels = new string[ZoomLadder.Steps.Length];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = ZoomLadder.Percent(ZoomLadder.Steps[i]).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return labels;
    }

    /// <summary>The Zoom row's combo. Index 0 is "Auto (N%)" with the LIVE resolved percent and is the selection for BOTH Auto and Dense; only Manual selects a
    /// rung (ch 27 W2). Its own component because it needs the live zoom, the viewport and one effect: a chord or the Ctrl+wheel moves the selection with no
    /// settings write.</summary>
    sealed class ZoomPicker : SettingsComponent
    {
        float _auto = 1f;
        int _labelsPercent = -1;
        string[] _labels = [];

        public override Element Render()
        {
            UseLayout();
            var dial = UseProps<ZoomProps>().Dial;
            float zoom = UseContext(Viewport.Zoom);
            var viewport = UseContext(Viewport.Size);
            // baseDip = the live (already zoomed) viewport × zoom — ZoomAutoPolicy takes BASE dips, never live ones.
            _auto = ZoomService.AutoSuggestion(viewport.Width, viewport.Height, zoom);
            SettingsFacts.PublishAutoZoom(_auto);   // what the head item ("Auto (N%)") applies when picked
            var mode = ZoomService.Mode;
            int index = mode == ZoomAutoMode.Manual
                ? 1 + Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(zoom)))
                : 0;
            UseEffect(() => s_zoomIndex.Value = index, DepKey.From(index));

            int percent = ZoomLadder.Percent(_auto);
            if (percent != _labelsPercent)
            {
                _labelsPercent = percent;
                _labels = new string[s_zoomRungLabels.Length + 1];
                _labels[0] = Strings.Settings.Appearance.ZoomAuto(percent);
                Array.Copy(s_zoomRungLabels, 0, _labels, 1, s_zoomRungLabels.Length);
            }
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that. A deliberate pick writes at once (the
            // chords' debounce exists for key repeats): the head item writes mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.
            return Combo(_labels, s_zoomIndex, width: WinUiSettingsRules.OptionComboWidth, onChange: i =>
            {
                if (dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
            }) with { Key = "appearance.zoom:" + percent };
        }
    }
}
