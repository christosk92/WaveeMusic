// ── Shell/Sidebar.UI.Footer.cs ─────────────────────────────────────────────────────────────────────────────────────
// the pane footer: Settings and the ⋯ pane menu, outside the scroller, in the expanded pane and in the rail
//
// Role: UI
// Owner: J
// Wave: 4
// Spec: ch 25 §0.9-11, §2 W5 · sidebar-rework-implementation §P2.6
// NAMED PARTIAL of Sidebar.UI.cs (J1): PaneFooter

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>THE PANE FOOTER (design V.2): outside the scroller, bottom-anchored, margin 0,0,0,4. Expanded: ONE 40-px
    /// slot — the Settings row (row A) whose chevron column holds the ⋯ pane button (24×24, tooltip "Sidebar options",
    /// always visible); with Settings hidden only the ⋯, right-aligned. Compact: a Settings tile over a ⋯ tile. The ⋯ is
    /// never hidden: it is the layout-independent entry to the pane menu.</summary>
    internal sealed class PaneFooter(PaneView owner) : Component
    {
        NodeHandle _more;
        Action<NodeHandle>? _realize;
        Func<NodeHandle>? _anchor;
        Action? _open, _settings;
        Func<ContextMenuModel?>? _menu;

        public override Element Render()
        {
            bool compact = !owner.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !Sidebar.DragPeek.Value;
            bool showSettings = owner.Config.ShowsSettings?.Invoke() ?? true;
            bool selected = string.Equals(owner.SelectedRoute, "settings", StringComparison.Ordinal);
            var dest = Shell.Dest(new Shell.Route(Shell.RouteKind.Settings));
            Func<ContextMenuModel?> menu = _menu ??= owner.PaneMenu;
            var svc = owner.MenuOverlay;
            _realize ??= h => _more = h;
            _anchor ??= () => _more;
            _open ??= () => owner.OpenPaneMenu(_anchor);
            _settings ??= () => owner.Navigate("settings", null);

            Element more = compact
                ? EntityRow.Create(new RowSpec
                {
                    Key = "footer-more", Label = Loc.Get("sidebar.pane.options"), Shape = SidebarRowShape.Glyph, Tile = true,
                    Glyph = Icons.More, OnRealized = _realize, MenuOverlay = svc, Menu = menu,
                    Focusable = true, OnClick = _open,
                })
                : SectionHeader.InlineButton(Icons.More, _open, reveal: false)
                    with { OnRealized = _realize, Focusable = true };
            more = ToolTip.Wrap(more, Loc.Get("sidebar.pane.options"));

            Element? settings = null;
            if (showSettings)
            {
                var spec = new RowSpec
                {
                    Key = "footer-settings", Label = dest.Title, Shape = SidebarRowShape.Glyph, Glyph = dest.Glyph,
                    Selected = selected, Tile = compact,
                    Focusable = true, OnClick = _settings,
                };
                settings = EntityRow.Create(in spec);
                if (compact) settings = ToolTip.Wrap(settings, dest.Title);
            }

            var pill = new BoxEl
            {
                Width = SidebarRowGeometry.PillW, Height = SidebarRowGeometry.PillH, HitTestVisible = false,
                Margin = new Edges4(0f, SidebarRowGeometry.PillTop(SidebarRowGeometry.RowHeight), 0f, 0f),
                Corners = CornerRadius4.All(SidebarRowGeometry.PillRadius), Fill = Tok.AccentDefault,
                Opacity = selected && showSettings ? 1f : 0f,
            };

            Element body;
            if (compact)
                body = new BoxEl
                {
                    Direction = 1,
                    Children = settings is null ? [more] : [ZStack(settings, pill), more],
                };
            else if (settings is null)
                body = new BoxEl
                {
                    Direction = 0, Height = SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph), AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.End, Padding = new Edges4(0f, 0f, (SidebarRowGeometry.ChevronColumn - SidebarRowGeometry.HeaderButton) * 0.5f, 0f),
                    Children = [more],
                };
            else
                body = ZStack(settings, pill, new BoxEl
                {
                    // The ⋯ sits centred in the Settings row's chevron column (pane W − 44..W − 4).
                    Width = SidebarRowGeometry.ChevronColumn, Height = SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
                    JustifySelf = FlexAlign.End, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [more],
                });

            return new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 4f),
                // The pane background's own menu answers a right-click on the footer's dead space too.
                Children = [body],
            };
        }
    }
}
