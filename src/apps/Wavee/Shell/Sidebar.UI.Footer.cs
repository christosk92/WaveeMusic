// ── Shell/Sidebar.UI.Footer.cs ─────────────────────────────────────────────────────────────────────────────────────
// the pane footer: the ⋯ pane menu, outside the scroller, in the expanded pane and in the rail
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
    /// <summary>THE PANE FOOTER (design V.2): outside the scroller, bottom-anchored, margin 0,0,0,4. It holds the ⋯ pane menu
    /// and nothing else: the Settings row is gone (owner decision G3; Ctrl+, , the profile menu and the palette open Settings).
    /// Expanded: the ⋯ pane button (28×28 with a 16 glyph, tooltip "Sidebar options", always visible) right-aligned in a
    /// Glyph-pitch row. Compact: a ⋯ tile. The ⋯ is never hidden: it is the layout-independent entry to the pane menu.</summary>
    internal sealed class PaneFooter(PaneView owner) : Component
    {
        NodeHandle _more;
        Action<NodeHandle>? _realize;
        Func<NodeHandle>? _anchor;
        Action? _open;
        Func<ContextMenuModel?>? _menu;

        public override Element Render()
        {
            bool compact = !owner.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !Sidebar.DragPeek.Value;
            Func<ContextMenuModel?> menu = _menu ??= owner.PaneMenu;
            var svc = owner.MenuOverlay;
            _realize ??= h => { _more = h; owner._footerMore = h; };
            _anchor ??= () => _more;
            _open ??= () => owner.OpenPaneMenu(_anchor);

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

            Element body = compact
                ? new BoxEl { Direction = 1, Children = [more] }
                : new BoxEl
                {
                    Direction = 0, Height = SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph), AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.End, Padding = new Edges4(0f, 0f, SidebarRowGeometry.HeaderTrailingPad, 0f),
                    Children = [more],
                };

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
