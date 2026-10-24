// ── Themes/WinUi/Structure/Views/Nav/WinUiNavFooter.cs — the pane's Settings footer (P6 W2b) ─────────────────────────────────────────
//
// NavigationView's footer item: Settings, pinned to the foot of the pane under the (scrolling) list, in the same 36 + 2 row with the same pill. The core
// pane mounts it through `NavRowKit.Footer` (a seam W2b added; Classic and the rest return null). It is not a plan row: it does not scroll, and the pane's
// selection flight (the route travel between rows) does not reach it, so its pill fades in and out in place.
//
// Whether it shows is the layout document's Settings section (`Hidden`, the Outline's Show box): the component reads `Sidebar.LayoutVersion` and the
// document itself, so the Outline's checkbox lands live. The selected state is the model's tracked `Selected` route. The rail draws it as a 40 x 36 tile
// with the label as its tooltip.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The footer's props: the pane and whether it presents as the rail. One record per (pane, form), so an equal re-push skips the render.</summary>
internal sealed record WinUiNavFooterProps(INavRowHost Host, bool Compact, WinUiNavMetrics Metrics);

/// <summary>Builds the footer element for the pane.</summary>
internal static class WinUiNavFooter
{
    /// <summary>The Settings route key.</summary>
    public const string SettingsRoute = "settings";

    public static Element Build(INavRowHost host, bool compact, WinUiNavMetrics metrics)
        => Embed.Comp(new WinUiNavFooterProps(host, compact, metrics), static () => new WinUiNavFooterRow()) with { Key = "winui-nav-footer" };
}

/// <summary>The footer row: Settings, with the pill.</summary>
internal sealed class WinUiNavFooterRow : Component
{
    public override Element Render()
    {
        var props = UseProps<WinUiNavFooterProps>();
        var host = props.Host;
        _ = Sidebar.LayoutVersion.Value;                         // the Outline's Show box for Settings
        if (Sidebar.Doc.Find(SidebarSectionKind.Settings) is not { Hidden: false })
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

        bool selected = string.Equals(host.Model.Selected, WinUiNavFooter.SettingsRoute, StringComparison.Ordinal);   // tracked
        var dest = host.DestOf(WinUiNavFooter.SettingsRoute);
        var spec = new WinUiRowSpec
        {
            Key = "winui-nav-settings", Label = dest.Title, Glyph = dest.Glyph, Selected = selected, Tile = props.Compact,
            Shape = SidebarRowShape.EntityOneLine, Height = props.Metrics.RowHeight, MarginY = props.Metrics.MarginY, Focusable = true,
            OnActivate = _ => host.Navigate(WinUiNavFooter.SettingsRoute, null),
        };
        // One tooltip host in both pane forms (the rail tips its name), so the row keeps its node across the 1008 crossing.
        Element row = Sidebar.RowTip.Wrap(WinUiEntityRow.Create(in spec), props.Compact, tooltip: false, dest.Title);

        // The pill's opacity is BOUND to the live route (its own subscription), with the 83 ms cross-fade of the row ramp.
        var pill = new BoxEl
        {
            Width = WinUiNavGeometry.PillW, Height = WinUiNavGeometry.PillH,
            Margin = new Edges4(WinUiNavGeometry.PillX(0), props.Metrics.PillTop, 0f, 0f),
            Corners = CornerRadius4.All(WinUiNavGeometry.PillRadius), Fill = Tok.AccentDefault, HitTestVisible = false,
            Opacity = Prop.Of(() => string.Equals(host.Model.Selected, WinUiNavFooter.SettingsRoute, StringComparison.Ordinal) ? 1f : 0f),
            Transition = MotionTok.ControlFaster,
        };
        return new BoxEl
        {
            Key = "winui-nav-footer-box", Direction = 1, Shrink = 0f,
            Padding = new Edges4(WinUiNavGeometry.PaneEdge, 0f, WinUiNavGeometry.PaneEdge, WinUiNavGeometry.PaneEdge),
            Children = [Ui.ZStack(row, pill)],
        };
    }
}
