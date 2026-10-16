// ── Themes/Common/CommonCustomize.cs — the Home customizer, one copy for every theme (`Pages.HomeCustomize`, themes plan 6.5, T11c) ───
//
// From `Home\Customize.UI.cs` (T10a1 drew it in Classic; T11c makes it a Common block, registered by every theme): the Home customizer — a plain scrolling column of rows (the header with Reset, then a `Reorderable` over a fixed
// row extent), each row a gripper · label · Up/Down (keyboard fallback for drag) · the visibility `ToggleSwitch`. The layout document, the ONE
// `HomeLayout` instance Home reads too (a toggle/reorder here is visible on Home the same frame it commits), and the three verbs are the core
// host's (`CustomizeScreen`); this view draws the document it is handed.
//
// TIERS (themes plan 3.2, TC10): the page reads `ctx.Tier()` into a `CommonCustomizeLayout` (Rules\Tiers.cs); a row (a component) reads the same fact through
// `CommonTierComponent`. Compact stacks Reset under the title, gives a row the 40-DIP hit target (Up, Down), narrows the gutter, and scrolls the head with the list; Short
// scrolls them too; every tier is one column capped at the page's max width (the Huge fill rule).
//
// WIRING THE REORDERABLE. A drag asks for a re-render of the component that BUILT the rows, so this view (the slot's own component) gives the
// reorderable its scene, its render request and the row extent from its render context — the host only owns the behaviour object. A row's
// `ToggleSwitch` keeps its OWN `Signal<bool>`, which freezes its INSTANCE at mount (`ToggleSwitch.Create`'s contract), so each row component
// keeps that cell in sync with the live document through a layout effect rather than rebuilding it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The Home customizer.</summary>
internal static class CommonCustomize
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeCustomize"/>.</summary>
    public static Element Template(HomeCustomizeProps props, in ViewContext ctx)
    {
        var doc = props.Doc;
        var reorder = props.Reorder;
        int n = doc.Zones.Count;
        reorder.Scene = ctx.Hooks.Scene;
        reorder.RequestRender = ctx.Hooks.RequestRerender;
        reorder.ItemCount = n;
        var layout = CommonCustomizeLayout.For(ctx.Tier());
        reorder.ItemExtent = layout.RowHeight;

        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int item = reorder.ItemAt(i);
            if ((uint)item >= (uint)n) item = i;
            var entry = doc.Zones[item];
            rows[i] = reorder.Item(item, RowFor(props, entry, item, n), key: entry.Kind);
        }

        Element head = Header(props, layout);
        Element list = new BoxEl
        {
            Key = "home-customize-list", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
            Padding = new Edges4(Left: layout.Gutter, Top: CommonCustomizeRules.CustomizeListTop, Right: layout.Gutter, Bottom: Spacing.XXL),
            Children = [reorder.List(new BoxEl { Direction = 1, MinWidth = 0f, Grow = 1f, Shrink = 1f, Children = rows })],
        };
        // The head and the list scroll as one page where the list alone would not fit (Compact, Short).
        Element[] page = layout.Scrolls
            ? [Ui.ScrollView(new BoxEl { Direction = 1, Children = [head, list] }) with { Grow = 1f, ScrollKey = "home-customize" }]
            : [head, list];
        // A row holding ONE column: the column stops growing at the tier's max width and keeps the page's left edge (the Huge fill rule).
        return new BoxEl
        {
            Key = "home-customize", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.MaxWidth, Children = page },
            ],
        };
    }

    static Element Header(HomeCustomizeProps props, CommonCustomizeLayout layout)
    {
        bool stack = layout.StackHeader;
        var titleBlock = new BoxEl
        {
            Direction = 1, Shrink = 1f, MinWidth = 0f, Gap = CommonCustomizeRules.CustomizeTitleGap,
            Children =
            [
                R.Type.PageHero(Loc.Get(Strings.Home.Customizer.Title)) with { Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                R.Type.TrackMeta(Loc.Get(Strings.Home.CustomizeBody)) with { Wrap = TextWrap.Wrap, MaxLines = 2 },
            ],
        };
        // Beside Reset the title block is the one flexible child; over it (Compact) both are just a column.
        return new BoxEl
        {
            Key = "home-customize-header", Direction = stack ? (byte)1 : (byte)0, AlignItems = stack ? FlexAlign.Start : FlexAlign.Center, Gap = CommonCustomizeRules.CustomizeHeaderGap,
            Padding = new Edges4(Left: layout.Gutter, Top: Spacing.XXL, Right: layout.Gutter, Bottom: 0f),
            Children =
            [
                stack ? titleBlock : titleBlock with { Grow = 1f },
                CommonTouch.Button(Loc.Get(Strings.Home.ResetDefault), props.Reset, layout.Target) with { Shrink = 0f },
            ],
        };
    }

    static Element RowFor(HomeCustomizeProps props, LayoutEntry entry, int index, int count)
        => Embed.Comp(new RowProps(entry, index, count, props.Toggle, props.Move), static () => new ZoneRow());

    internal sealed record RowProps(LayoutEntry Entry, int Index, int Count, Action<LayoutZone> Toggle, Action<int, int> Move);

    /// <summary>Gripper · label · Up/Down (keyboard fallback for drag) · the visibility toggle. The toggle's own
    /// <see cref="Signal{T}"/> instance freezes at mount (<c>ToggleSwitch.Create</c>'s contract), so it is kept in
    /// sync with the live document through a layout effect rather than rebuilt — the same shape the old customizer's
    /// row used, over the new document.</summary>
    sealed class ZoneRow : CommonTierComponent
    {
        readonly Signal<bool> _visible = new(true);
        bool _wantVisible;
        Action? _sync;
        Action<bool>? _onToggle;

        public override Element Render()
        {
            var layout = CommonCustomizeLayout.For(ReadTier());
            var p = UseProps<RowProps>();
            bool visible = p.Entry.Visible;
            _wantVisible = visible;
            UseLayoutEffect(_sync ??= SyncToggle, DepKey.From(visible ? 1 : 0, p.Index));

            LayoutZones.TryParse(p.Entry.Kind, out var zone);
            string label = ZoneLabel(zone);
            int index = p.Index, count = p.Count;
            var move = p.Move;

            return new BoxEl
            {
                Direction = 0, Height = layout.RowHeight, AlignItems = FlexAlign.Center, Gap = layout.RowGap,
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Opacity = visible ? 1f : CommonCustomizeRules.CustomizeHiddenOpacity,
                Children =
                [
                    Ui.Icon(Icons.GripperBar, CommonCustomizeRules.CustomizeGripSize, Tok.TextTertiary),
                    new TextEl(label)
                    {
                        Size = CommonCustomizeRules.CustomizeLabelSize, LineHeight = CommonCustomizeRules.CustomizeLabelLine, Color = Tok.TextPrimary,
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    },
                    // loc: "Move up" / "Move down" — reuses the app's existing move-row vocabulary (Strings.Menu.*).
                    Controls.Named(Controls.IconAction(Icons.ChevronUp, index > 0 ? () => move(index, index - 1) : null, size: layout.Target > 0f ? layout.Target : Controls.IconButtonSize),
                        Loc.Get(Strings.Menu.MoveUp)),
                    Controls.Named(Controls.IconAction(Icons.ChevronDown, index < count - 1 ? () => move(index, index + 1) : null, size: layout.Target > 0f ? layout.Target : Controls.IconButtonSize),
                        Loc.Get(Strings.Menu.MoveDown)),
                    ToggleSwitch.Create(_visible, onChange: _onToggle ??= _ => p.Toggle(zone)),
                ],
            };
        }

        /// <summary>Cached once (<c>_sync ??=</c>) and re-run by the effect on every (visible, index) change — reads
        /// the FIELD, never a captured render-local, so the cached delegate stays correct across every re-render.</summary>
        void SyncToggle()
        {
            if (_visible.Peek() != _wantVisible) _visible.Value = _wantVisible;
        }
    }

    static string ZoneLabel(LayoutZone zone) => zone switch
    {
        LayoutZone.Daylist => Loc.Get(Strings.Home.Layout.Daylist),
        LayoutZone.Recents => Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
        LayoutZone.MadeForYou => Loc.Get(Strings.Home.MadeForYou),
        LayoutZone.NewMusic => Loc.Get(Strings.Home.Layout.NewMusic),
        LayoutZone.Releases => Loc.Get(Strings.Home.NewReleases),
        LayoutZone.BecauseYouLike => Loc.Get(Strings.Home.Layout.BecauseYouLike),
        LayoutZone.JumpBackIn => Loc.Get(Strings.Home.JumpBackIn),
        LayoutZone.Radio => Loc.Get(Strings.Home.Zone.RadioAndMixes),
        LayoutZone.Browse => Loc.Get(Strings.Home.Zone.Browse),
        _ => "",
    };
}
