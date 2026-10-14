// ── Themes/Classic/Structure/Views/Nav/ClassicNavFlyouts.cs — Classic's compact-rail flyouts (themes plan 6.1, T5c) ────────────
//
// From `Shell/Sidebar.UI.Flyout.cs` (`PaneSectionFlyout`, `PaneFolderFlyout`): the panel a collapsed section's tile and a folder's
// tile open beside the compact rail. The core pane keeps what DECIDES and mounts these through `ViewKeys.ShellNavSectionFlyout` /
// `ShellNavFolderFlyout` inside the popup it opens: which flyout is open, where it anchors, the focus trap and the light dismiss, the
// one-section re-plan (`INavRowHost.PlanSectionExpanded`), the menus' rows, the drop specs, what a row's activation navigates to and
// the drill-in stack (`SidebarFolderFlyoutNav`, pure). This file draws the panels and owns the folder flyout's keyboard: the panel is
// the one focus stop and the one key handler (up/down rove, Enter activates, Right drills in, Left/Backspace goes back; Escape is the
// popup's light dismiss). Numbers are `Rules\SidebarGeometry.cs` and `Rules\NavFlyoutLayout.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The registered templates of the rail's flyout keys.</summary>
internal static class ClassicNavFlyouts
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellNavSectionFlyout"/>.</summary>
    public static Element SectionTemplate(NavSectionFlyoutProps props, in FluentGpu.Theming.ViewContext ctx)
        => Embed.Comp(() => new ClassicSectionFlyout(props.Host, props.SectionId, props.Close));

    /// <summary>The registered template of <see cref="ViewKeys.ShellNavFolderFlyout"/>.</summary>
    public static Element FolderTemplate(NavFolderFlyoutProps props, in FluentGpu.Theming.ViewContext ctx)
        => Embed.Comp(() => new ClassicFolderFlyout(props.Host, props.SectionId, props.RootFolderId, props.RootFolderName, props.Close));
}

/// <summary>A collapsed section's rows in a 300-DIP flyout: planned by the ONE planner from a one-section document with
/// the section expanded, rendered with the pane's own row primitive. Virtualized past 50 rows. Live: it re-plans on the
/// binder's cells. A row's click navigates and closes; a folder row's click expands or collapses it in place.</summary>
internal sealed class ClassicSectionFlyout(INavRowHost owner, string sectionId, Action close) : Component
{
    readonly SidebarPlanBuffers _buffers = new();
    readonly List<int> _drawn = new(64);      // the plan's row indices FlyoutRow draws, in order

    public override Element Render()
    {
        var model = owner.Model;
        int ver = model.LibraryItems.Version.Value + model.PinsVersion.Value + model.FolderVersion.Value;
        var plan = owner.PlanSectionExpanded(sectionId, _buffers);
        // SectionHeader, TreeEnd and Empty rows draw nothing (FlyoutRow's 0-DIP box), so they take no slot and no height.
        _drawn.Clear();
        float h = 0f;
        for (int i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            if (row.EntryIndex < 0 ? row.Kind != SidebarRowKind.IconRow : (uint)row.EntryIndex >= (uint)plan.Entries.Count) continue;
            _drawn.Add(i);
            h += SidebarGeometry.PitchOf(row.EntryIndex < 0 ? SidebarRowShape.Glyph : SidebarRowShape.EntityTwoLine);
        }
        int n = _drawn.Count;
        h = MathF.Min(ClassicNavFlyoutLayout.MaxListHeight, h);
        // ItemsView freezes its count and template at mount, so the virtualized list is KEYED on the plan it reads: a
        // re-plan (a folder toggled in place, a playlist added) remounts it, and no template call indexes a stale _drawn.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Element list = n > ClassicNavFlyoutLayout.VirtualizeAfter
            ? ItemsView.Create(n, i => FlyoutRow(plan, _drawn[i]), RepeatLayout.Stack(SidebarGeometry.PitchOf(SidebarRowShape.EntityTwoLine)),
                new ListOptions { SelectionMode = ItemsSelectionMode.None })
                with { Key = "flyout-list:" + ver.ToString(inv) + ":" + n.ToString(inv) }
            : new ScrollEl { Grow = 1f, Content = new BoxEl { Direction = 1, Children = BuildRows(plan) } };
        list = new BoxEl { Direction = 1, Height = h, Children = [list] };
        return new BoxEl
        {
            Direction = 1, Width = ClassicNavFlyoutLayout.PanelWidth, ClipToBounds = true, Corners = Radii.OverlayAll,
            Padding = new Edges4(SidebarGeometry.PaneEdge, ClassicNavFlyoutLayout.SectionPadY, SidebarGeometry.PaneEdge, ClassicNavFlyoutLayout.SectionPadY),
            Children = [list],
        };
    }

    Element[] BuildRows(SidebarRowPlan plan)
    {
        var rows = new Element[_drawn.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = FlyoutRow(plan, _drawn[i]);
        return rows;
    }

    Element FlyoutRow(SidebarRowPlan plan, int i)
    {
        var row = plan.Rows[i];
        var host = owner;
        var model = owner.Model;
        Action closeFlyout = close;
        // A route shortcut or link has no entry: its glyph row navigates to the route key itself.
        if (row.EntryIndex < 0 && row.Kind == SidebarRowKind.IconRow)
        {
            var dest = host.DestOf(row.Key);
            return Sidebar.EntityRow.Create(new Sidebar.RowSpec
            {
                Key = row.Key, Label = dest.Title, Shape = SidebarRowShape.Glyph, Glyph = dest.Glyph,
                Selected = string.Equals(row.Key, host.SelectedRoutePeek, StringComparison.Ordinal),
                OnClick = () => { host.Navigate(row.Key, null); closeFlyout(); },
            });
        }
        var e = plan.Entries[row.EntryIndex];
        // A folder nests in place (V.9): its click toggles the shared expansion and Render re-plans on FolderVersion,
        // so the children appear under it; the flyout stays open. Any other row navigates and closes.
        string folderId = e.FolderId;
        Action? click = e.IsFolder && folderId.Length > 0 ? () => model.ToggleFolder(folderId)
            : e.RouteKey is { Length: > 0 } r ? () => { host.Navigate(r, e.Name); closeFlyout(); }
            : null;
        return Sidebar.EntityRow.Create(new Sidebar.RowSpec
        {
            Key = row.Key, Label = e.Name.Length > 0 ? e.Name : NavRowFacts.ShortUri(e.Uri),
            Subtitle = Sidebar.PaneText.SubtitleOf(in e), Shape = SidebarRowShape.EntityTwoLine, Depth = row.Depth,
            Leading = Sidebar.Cover.ForEntry(in e, SidebarGeometry.ArtOf(SidebarRowShape.EntityTwoLine)),
            Selected = string.Equals(e.RouteKey, host.SelectedRoutePeek, StringComparison.Ordinal),
            OnClick = click,
        });
    }
}

/// <summary>A compact folder tile's children panel (W6): ONE panel, one page at a time, a <c>Key</c>ed body carrying
/// <c>MotionRecipes.PageSlideForward/Back</c> and a back chevron above level 1 — the concert date flyout's model, with
/// the unbounded stack rules in the pure <see cref="SidebarFolderFlyoutNav"/>. Mounted fresh by the pane's
/// <c>OpenFolderFlyout</c> on every open, so the stack always starts at the clicked folder.
/// <para>LIVE, not a snapshot: <c>Render</c> re-reads <c>Model.Rootlist</c> every pass and subscribes
/// to <c>Model.LibraryItems.Version</c> + <c>FolderVersion</c>, so a push indexes the current tree and a playlist created, renamed,
/// moved or deleted while the panel is open shows up in it. The props are reference-stable mount seeds.</para>
/// <para>Rows are <c>EntityRow</c> specs with the pane's own menus and drop specs, so a row here and a row in
/// the expanded pane cannot look or behave differently.</para></summary>
internal sealed class ClassicFolderFlyout(INavRowHost owner, string sectionId, string rootFolderId, string rootFolderName, Action close) : Component
{
    const string BackKey = "sidebar.rail.folderFlyoutBack";
    const string FolderKey = "sidebar.v3.kind.folder";
    const string EmptyKey = "sidebar.section.empty";   // the pane's own empty copy — no second wording

    SidebarFolderFlyoutNav? _nav;
    readonly Signal<int> _epoch = new(0);      // bumped on push/pop — the one structural re-render
    readonly Signal<int> _cursor = new(-1);    // the keyboard cursor; -1 = nothing highlighted
    bool _forward = true;                      // last drill direction → which page-slide recipe
    readonly List<SidebarLibraryEntry> _children = new(16);

    public override Element Render()
    {
        var nav = _nav ??= new SidebarFolderFlyoutNav(rootFolderId, rootFolderName);
        _ = _epoch.Value;
        var model = owner.Model;
        _ = model.LibraryItems.Version.Value + model.FolderVersion.Value;   // THE live subscription: the model's library cell, which is the one that publishes (read, never peeked)
        var tree = model.Rootlist;

        int count = SidebarFolderTree.Children(tree, nav.Current.FolderId, _children);
        int cursor = count == 0 ? -1 : Math.Clamp(_cursor.Value, -1, count - 1);

        var body = new BoxEl
        {
            // KEYED per level: the drill-in is a page transition, not a content swap.
            Key = "rail-folder:" + nav.PageKey,
            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
            Direction = 1, MinWidth = 0f,
            Children = [count == 0 ? EmptyHint() : ListOf(tree, cursor)],
        };

        return new BoxEl
        {
            Direction = 1, Width = ClassicNavFlyoutLayout.PanelWidth, ClipToBounds = true, MinWidth = 0f,
            Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
            // The panel is the focus stop and the ONE key handler (keys route from the focused node upward); the popup's
            // FocusTrap focuses it on open, so rows never become N tab stops.
            Focusable = true,
            OnKeyDown = OnKey,
            Children = [Header(nav, count), body],
        };
    }

    /// <summary>Name + item count, with a back chevron only above the root — absent, not disabled, at level 1.</summary>
    Element Header(SidebarFolderFlyoutNav nav, int count)
    {
        var title = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                Ui.BodyStrong(nav.Current.Name.Length > 0 ? nav.Current.Name : Loc.Get(FolderKey)) with
                {
                    Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Ui.Caption(Strings.Sidebar.V3.ItemCount(count)) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinHeight = ClassicNavFlyoutLayout.FolderHeaderHeight, MinWidth = 0f,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, Spacing.XS),
            Children = nav.CanGoBack ? new Element[] { BackButton(nav.Parent.Name), title } : new Element[] { title },
        };
    }

    Element BackButton(string parentName) => ToolTip.Wrap(new BoxEl
    {
        Width = ClassicNavFlyoutLayout.BackButton, Height = ClassicNavFlyoutLayout.BackButton, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Cursor = CursorId.Hand,
        OnClick = GoBack,
        Children = [Ui.Icon(Icons.ChevronLeft, ClassicNavFlyoutLayout.BackGlyph, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle),
        parentName.Length > 0 ? parentName : Loc.Get(BackKey));

    Element ListOf(IReadOnlyList<SidebarLibraryEntry>? tree, int cursor)
    {
        string sel = owner.Model.Selected;   // subscribes: the selected row's ramp follows navigation
        var section = owner.SectionOf(sectionId);
        var rows = new Element[_children.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(tree, section, _children[i], sel, i == cursor);
        return new ScrollEl
        {
            ContentSized = true, MaxHeight = ClassicNavFlyoutLayout.MaxListHeight,
            Content = new BoxEl { Direction = 1, Gap = ClassicNavFlyoutLayout.FolderRowGap, MinWidth = 0f, Children = rows },
        };
    }

    Element Row(IReadOnlyList<SidebarLibraryEntry>? tree, SidebarSection? section, SidebarLibraryEntry entry,
                string sel, bool cursored)
    {
        var host = owner;
        bool folder = entry.Kind == SidebarEntryKind.Folder;
        string? route = entry.RouteKey;
        // The route selection and the keyboard cursor share the row's one highlight: both mean "the row in play".
        bool selected = cursored || (route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal));

        // The SAME menus the pane builds. A folder's Expand verb still means the PANE's disclosure, not this drill-in.
        Func<ContextMenuModel?>? menu = section is null ? null
            : folder ? host.FolderMenu(section, -1, in entry, () => host.ExpandFolderInPane(entry.FolderId),
                                       host.Model.IsFolderExpanded(entry.FolderId), entry.Id)
            : host.EntryMenu(section, -1, in entry, entry.Id);

        // A rootlist member either way: a playlist drags out onto any sidebar/rootlist target, a sub-folder files
        // elsewhere. Drops are the pane's own specs: Into-only filing on a sub-folder, the whole-row track deposit on a
        // playlist (slot -1) — exactly one outcome each, so one bound boolean is the whole cue.
        var payload = host.PayloadOf(in entry, rootlistItem: true);
        DropTargetSpec? drop = folder
            ? (entry.FolderId.Length > 0 ? host.RailFolderDropSpec(in entry, payload) : null)
            : entry.Kind == SidebarEntryKind.Playlist
                ? host.ResourceDropSpec(sectionId, slot: -1, entry.CanEdit ? entry.Uri : null, entry.Name,
                                        railCueUri: entry.Uri, isPlaylistRow: true)
                : null;
        string cueKey = folder ? entry.Id : entry.Uri;

        return Sidebar.EntityRow.Create(new Sidebar.RowSpec
        {
            Key = entry.Id,
            // Same gate as EntryRow's (Trap 5). A FOLDER has no entity identity to wait for, so it keeps the
            // honest short form; an entity row that has not resolved shows nothing.
            Label = entry.Name.Length > 0 ? entry.Name
                : folder || SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown)
                    ? NavRowFacts.ShortUri(entry.Id)
                    : "",
            // A sub-folder's count is the count of the very list a drill-in shows (ParentFolderId containment), never
            // the projection's ChildCount — the second definition that once rendered a full folder as "0 items".
            Subtitle = folder
                ? Strings.Sidebar.V3.ItemCount(SidebarFolderTree.ChildCount(tree, entry.FolderId))
                : Sidebar.PaneText.SubtitleOf(in entry),
            Selected = selected,
            Shape = SidebarRowShape.EntityTwoLine,
            Leading = Sidebar.Cover.ForEntry(in entry, SidebarGeometry.ArtOf(SidebarRowShape.EntityTwoLine)),
            // A sub-folder announces that it drills IN — the pointer twin of the → key.
            Trailing = folder ? Ui.Icon(Icons.ChevronRight, ClassicNavFlyoutLayout.DrillGlyph, Tok.TextTertiary) with { Shrink = 0f } : null,
            OnClick = () => Activate(in entry),
            Overflow = menu is not null,
            MenuOverlay = host.MenuOverlay,
            Menu = menu,
            Drag = payload,
            DropTarget = drop,
            DropCue = drop is null ? null : new Sidebar.RowDropCue(
                () => host.IsRailDropActive(cueKey), s_cueFill, s_cueBorder, SidebarGeometry.DropPlateBorderWidth),
        });
    }

    // The folder flyout row's whole-row cue: the same accent plate the tree's "into" cue paints (`ClassicNavRow.DropPlate`).
    static readonly Func<ColorF> s_cueFill = static () => Tok.AccentDefault with { A = SidebarGeometry.DropPlateAlpha };
    static readonly Func<ColorF> s_cueBorder = static () => Tok.AccentDefault;

    static Element EmptyHint() => new BoxEl
    {
        MinHeight = ClassicNavFlyoutLayout.EmptyMinHeight, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
        Children =
        [
            Ui.Body(Loc.Get(EmptyKey)) with
            {
                Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    void Activate(in SidebarLibraryEntry entry)
    {
        if (entry.Kind == SidebarEntryKind.Folder) { Drill(entry.FolderId, entry.Name); return; }
        if (entry.RouteKey is not { Length: > 0 } route) return;
        owner.Navigate(route, entry.Name, in entry);
        close();
    }

    void Drill(string folderId, string name)
    {
        if (_nav is not { } nav || !nav.Push(folderId, name)) return;
        _forward = true;
        _cursor.Value = -1;   // a fresh level starts un-cursored; the first ↓ lands on its first row
        _epoch.Value = _epoch.Peek() + 1;
    }

    void GoBack()
    {
        if (_nav is not { } nav || !nav.Pop()) return;
        _forward = false;
        _cursor.Value = -1;
        _epoch.Value = _epoch.Peek() + 1;
    }

    /// <summary>↑/↓ rove (wrapping) · Enter activates · → drills into a folder · ←/Backspace goes back. Escape is NOT
    /// handled: the popup's light dismiss owns it, and a second close path could drift from click-away's.</summary>
    void OnKey(KeyEventArgs e)
    {
        int count = _children.Count;
        int cursor = _cursor.Peek();
        switch (e.KeyCode)
        {
            case Keys.Down:
                e.Handled = true;
                if (count > 0) _cursor.Value = cursor < 0 ? 0 : (cursor + 1) % count;
                return;
            case Keys.Up:
                e.Handled = true;
                if (count > 0) _cursor.Value = cursor < 0 ? count - 1 : (cursor - 1 + count) % count;
                return;
            case Keys.Enter:
                e.Handled = true;
                if ((uint)cursor < (uint)count) Activate(_children[cursor]);
                return;
            case Keys.Right:
                e.Handled = true;
                if ((uint)cursor < (uint)count && _children[cursor] is { Kind: SidebarEntryKind.Folder } f)
                    Drill(f.FolderId, f.Name);
                return;
            case Keys.Left:
            case Keys.Back:
                // Swallowed even at the root: a back gesture falling through a focus-trapped popup would navigate
                // the app out from under a flyout the user is still reading.
                e.Handled = true;
                GoBack();
                return;
        }
    }
}
