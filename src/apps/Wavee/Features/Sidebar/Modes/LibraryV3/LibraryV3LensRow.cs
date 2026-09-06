using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// W2 — the 32-DIP row under the chips: which slice of the library you are looking at (a plain label, or — for
/// Albums/Artists/Podcasts — a link to that facet's full page, the <c>HomeModules.ModuleHeader</c> idiom), the row
/// count, and the Sort/View controls that used to live in the header's "…" overflow (<see cref="V3SortViewMenu"/>).
///
/// <para>Hidden while drilled into a folder — <c>LibraryV3Chrome</c> mounts the drill-in breadcrumb in this row's
/// place for that state instead (<see cref="LibraryV3LensRules"/> decides the shape; this component only draws
/// it). The count is <see cref="LibraryV3Session.VisibleRowCount"/> — the SAME expression <c>LibraryV3Chrome</c>
/// gates its own empty state on, so the two can never disagree about how many rows are on screen.</para>
/// </summary>
sealed class LibraryV3LensRow : Component
{
    readonly LibraryV3Session _session;

    public LibraryV3LensRow(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var svc = UseContext(Overlay.Service);

        // Two independent flyouts (Sort / View), so each gets its own anchor + handle pair — the exact shape
        // LibraryV3Header.ToggleOverflow uses for its one overflow menu.
        var sortAnchor = UseRef<NodeHandle>(default);
        var sortHandle = UseRef<OverlayHandle?>(null);
        var viewAnchor = UseRef<NodeHandle>(default);
        var viewHandle = UseRef<OverlayHandle?>(null);

        // The state read subscribes this row to every signal LibraryV3Chrome's document is a function of, so the
        // label/count/Sort/View here can never disagree with the rows below them.
        var state = _session.ReadState();
        float width = _session.Width.Value;
        var shape = LibraryV3LensRules.Resolve(state.Filter, state.Qualifier, state.Searching, state.Drilled, width);

        // Drilled: LibraryV3Chrome mounts the breadcrumb in this component's place instead — an empty box here
        // costs nothing (BoxEl.Height/Padding default to 0) and keeps the Key stable if a caller ever forgets to swap it.
        if (!shape.Visible || prefs is null) return new BoxEl();

        int sort = LibraryV3Metrics.NormalizeSort(prefs.V3Sort.Value);
        int view = LibraryV3Metrics.NormalizeView(prefs.V3View.Value);
        int count = _session.VisibleRowCount(in state);

        void ToggleSort()
        {
            if (svc is null) return;
            if (sortHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            sortHandle.Value = svc.Open(
                () => sortAnchor.Value,
                () => MenuFlyout.Create(V3SortViewMenu.SortRows(prefs), () => sortHandle.Value?.Close(), minWidth: 200f),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            sortHandle.Value.ClosedAction = () => sortHandle.Value = null;
        }

        void ToggleView()
        {
            if (svc is null) return;
            if (viewHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            viewHandle.Value = svc.Open(
                () => viewAnchor.Value,
                () => MenuFlyout.Create(V3SortViewMenu.ViewRows(prefs), () => viewHandle.Value?.Close(), minWidth: 200f),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            viewHandle.Value.ClosedAction = () => viewHandle.Value = null;
        }

        // W5's IconButton.Create anchor trick (LibraryV3Header.ToggleOverflow): a Parts modifier is the one thing
        // that rides through the control's own re-assertion of OnClick/Role/Children on its returned root, so
        // OnRealized survives. Memoised once — mutating a TemplateParts bumps its Epoch, which must not happen per
        // render. Only the VIEW control needs this; the sort button below is hand-rolled, so it sets OnRealized
        // directly.
        var viewParts = UseMemo(() =>
        {
            var m = new TemplateParts();
            m[IconButton.PartRoot] = b => b with { OnRealized = h => viewAnchor.Value = h };
            return m;
        }, DepKey.Empty);

        var rightInner = new List<Element>(2) { SortButton(sort, sortAnchor, ToggleSort) };
        if (shape.ShowsView)
            rightInner.Add(ToolTip.Wrap(
                IconButton.Create(LibraryV3Labels.ViewGlyph(view), ToggleView, parts: viewParts, size: ControlSize.Small)
                    with { Key = "v3-lens-view" },
                Loc.Get(Strings.Library.ViewAs)));

        return new BoxEl
        {
            Direction = 0, Height = LibraryV3Metrics.LensRowHeight, AlignItems = FlexAlign.Center, Gap = 8f,
            Padding = SidebarPaneMetrics.LeadBandInset, Shrink = 0f,
            Children =
            [
                Left(shape, count),
                new BoxEl { Grow = 1f },
                // The a11y group label sits in its own UNGAPPED wrapper alongside the actually-gapped button pair
                // (the LibraryV3Chips.GroupLabel pattern): a zero-size sibling INSIDE this row's own Gap=8 would
                // still collect that gap and shove the cluster off its trailing edge.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        GroupLabel(),
                        new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = [.. rightInner] },
                    ],
                },
            ],
        };
    }

    // ── left: what you're looking at ──────────────────────────────────────────────────────────────────────────────

    Element Left(LibraryV3LensRules.Shape shape, int count)
    {
        string labelText = shape.Filter == (int)SidebarV3Filter.All
            ? Loc.Get(Strings.Sidebar.V3.Lens.All)
            : FacetLabel(shape.Filter);
        if (shape.Qualifier != (int)SidebarV3Qualifier.Any)
            labelText = labelText + " · " + LibraryV3Labels.Qualifier(shape.Qualifier);

        Element labelEl = shape.PageRoute is { Length: > 0 } route
            // HomeModules.ModuleHeader's idiom: a heading + a trailing ChevronRight IS the "this opens a page" link.
            ? ToolTip.Wrap(
                new BoxEl
                {
                    Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Height = 24f,
                    Padding = new Edges4(6f, 0f, 4f, 0f), Corners = Radii.ControlAll,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = () => _session.Go(route, LibraryV3Labels.Filter(shape.Filter)),
                    Children =
                    [
                        new TextEl(labelText)
                        {
                            Size = 12f, Weight = 600, Color = Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                        },
                        Icon(Icons.ChevronRight, 10f, Tok.TextTertiary),
                    ],
                }.Interactive(Interaction.Subtle),
                Strings.Sidebar.V3.Lens.OpenPage(ShellNav.Dest(route).Title))
            : new TextEl(labelText)
            {
                Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            };

        // Searching reads as "N matches" (a search result count), never the bare quiet number every other count in
        // the sidebar uses — the distinction the empty-state banner beneath this row also makes.
        Element countEl = shape.Searching
            ? new TextEl(Strings.Sidebar.V3.Lens.Matches(count)) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 }
            : SidebarCounts.Number(count);

        return new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f,
            Children = [labelEl, countEl],
        };
    }

    /// <summary>The facet's OWN lens label ("All albums", not "Albums" — <c>LibraryV3Labels.Filter</c> is the CHIP
    /// label, a distinct grammar this row deliberately does not reuse).</summary>
    static string FacetLabel(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => Loc.Get(Strings.Sidebar.V3.Lens.Playlists),
        (int)SidebarV3Filter.Albums => Loc.Get(Strings.Sidebar.V3.Lens.Albums),
        (int)SidebarV3Filter.Artists => Loc.Get(Strings.Sidebar.V3.Lens.Artists),
        (int)SidebarV3Filter.Podcasts => Loc.Get(Strings.Sidebar.V3.Lens.Podcasts),
        _ => Loc.Get(Strings.Sidebar.V3.Lens.All),
    };

    // ── right: Sort / View ────────────────────────────────────────────────────────────────────────────────────────

    static Element SortButton(int sort, Ref<NodeHandle> anchor, Action onClick) => ToolTip.Wrap(
        new BoxEl
        {
            Key = "v3-lens-sort",
            Direction = 0, Gap = 3f, AlignItems = FlexAlign.Center, Height = 24f,
            Padding = new Edges4(6f, 0f, 4f, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = onClick,
            OnRealized = h => anchor.Value = h,
            Children =
            [
                new TextEl(LibraryV3Labels.Sort(sort)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
                Icon(Icons.ChevronDown, 10f, Tok.TextSecondary),
            ],
        }.Interactive(Interaction.Subtle),
        Loc.Get(Strings.Library.SortBy));

    /// <summary>The rail's a11y group label, painted at ZERO SIZE — the <c>LibraryV3Chips.GroupLabel</c> pattern —
    /// so it names the Sort/View cluster for a screen reader without costing it a phantom layout inch.</summary>
    static Element GroupLabel() => new BoxEl
    {
        Key = "v3-lens-group",
        Width = 0f, Height = 0f, ClipToBounds = true, HitTestVisible = false,
        Children = [new TextEl(Loc.Get(Strings.Sidebar.A11y.SortView)) { Size = 1f, MaxLines = 1 }],
    };
}
