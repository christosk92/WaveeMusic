using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE COLLAPSED RAIL'S FOLDER FLYOUT.
//
// THE PROBLEM IT SOLVES. A 56-DIP strip has no indent lane and no disclosure, so the rail used to draw a folder's
// children as tiles indistinguishable from top-level ones — the strip's order was unexplainable and an expanded folder
// silently doubled its length. `SidebarRowPlanner.RailTree` now keeps the tiles to depth 0, and this is where a folder's
// contents went: a side panel anchored to the tile's right edge. The tile used to EXPAND THE PANE instead, which
// answered "show me this folder" by throwing away the collapsed layout the user had chosen.
//
// THE NAVIGATION MODEL IS THE CONCERT DATE FLYOUT'S (`Features/Concerts/ConcertDateFlyout.cs`), verbatim in shape: ONE
// panel, one page at a time, a `Key`ed body carrying `MotionRecipes.PageSlideForward` / `PageSlideBack` (the same recipe
// `ContentHost` uses for real page navigation) and a back chevron in the header on any level but the first. What is
// generalised is the stack: the concert flyout has two levels and a fixed root, so its whole model is one `Signal<int>`;
// a folder chain is unbounded, so the rules live in the pure `SidebarFolderFlyoutNav` where a test can reach them and
// this component only renders what they decide.
//
// LIVENESS (props freeze at mount, so this matters). The flyout is NOT a snapshot: `Render` re-reads
// `prefs.Binder.CurrentInput.PlaylistTree` — the live projection — every pass, and subscribes to it by reading
// `prefs.Entries.Version` (the signal the binder bumps on every rebuild) plus `prefs.FolderVersion` (the folder-menu's
// expand/collapse verb). A push therefore re-reads the tree for the new level rather than indexing a frozen copy, and a
// playlist created, renamed, moved or deleted while the flyout is open shows up in it.
//
// THE ROWS ARE `SidebarEntityRow` SPECS — art, name, subtitle, the 4-state selection ramp, the context menu, the "…"
// overflow, a typed drag source and a typed drop target — so a row here and a row in the expanded pane cannot look or
// behave differently. The drop specs are the pane's own (`SidebarPane.ResourceDropSpec` for a track deposit into an
// editable playlist, `SidebarPane.RailFolderDropSpec` for an Into-only filing on a sub-folder), so there is exactly one
// implementation of "what does dropping here do".

/// <summary>The rail folder flyout's content. Mounted fresh by <see cref="SidebarPane.OpenRailFolderFlyout"/> on every
/// open, so the stack always starts at the folder whose tile was clicked.</summary>
sealed class SidebarRailFolderFlyout : Component
{
    /// <summary>The pane that owns the rail. A reference-stable service-ish object (it lives as long as the mount), which
    /// is what makes it legal as a frozen prop — everything time-varying is read THROUGH it at render time.</summary>
    public required SidebarPane Owner;

    /// <summary>The section the tile came from — only ever handed back to the pane's drop spec, which uses it for
    /// reorder identity (and ignores it entirely at <c>slot &lt; 0</c>, which is what every row here passes).</summary>
    public required string SectionId;

    public required string RootFolderId;
    public required string RootFolderName;
    public required Action Close;

    /// <summary>The panel width. Wider than the concert flyout's 320 would crowd a narrow window beside a rail that is
    /// already flush to the window edge; narrower would truncate a playlist name at the density the rows use.</summary>
    const float PanelW = 300f;
    const float MaxListH = 420f;
    const float HeaderH = 40f;

    SidebarFolderFlyoutNav? _nav;
    readonly Signal<int> _epoch = new(0);      // bumped on push/pop — the ONE thing that re-renders the panel structurally
    readonly Signal<int> _cursor = new(-1);    // the keyboard cursor; -1 = nothing highlighted (pointer-only so far)
    bool _forward = true;                      // last drill direction → picks the page-slide recipe
    readonly List<SidebarLibraryEntry> _children = new(16);

    public override Element Render()
    {
        var nav = _nav ??= new SidebarFolderFlyoutNav(RootFolderId, RootFolderName);
        _ = _epoch.Value;                      // subscribe: a push/pop re-renders the panel

        var owner = Owner;
        var prefs = owner.Prefs;
        // THE LIVE SUBSCRIPTION (see the file header). Read, never peeked: these reads ARE what re-renders the panel
        // when the projection rebuilds or a folder verb fires.
        _ = prefs is null ? 0 : prefs.Entries.Version.Value + prefs.FolderVersion.Value;
        var tree = prefs?.Binder?.CurrentInput.PlaylistTree;

        var level = nav.Current;
        int count = SidebarFolderTree.Children(tree, level.FolderId, _children);
        int cursor = count == 0 ? -1 : Math.Clamp(_cursor.Value, -1, count - 1);

        var body = new BoxEl
        {
            // KEYED per level: this is what makes the drill-in a page transition rather than a content swap, exactly as
            // `ConcertDateFlyout` keys its root/month views.
            Key = "rail-folder:" + nav.PageKey,
            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
            Direction = 1, MinWidth = 0f,
            Children = [count == 0 ? EmptyHint() : List(cursor)],
        };

        return new BoxEl
        {
            Direction = 1, Width = PanelW, ClipToBounds = true, MinWidth = 0f,
            Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
            // The panel is the focus stop and the key handler: `InputDispatcher` routes keys from the focused node
            // UPWARD, so one handler here serves every row without making 200 rows into 200 tab stops. The popup opens
            // with `FocusTrap: true`, which focuses the first focusable in the subtree — this element.
            Focusable = true,
            OnKeyDown = OnKey,
            Children = [Header(nav, count), body],
        };
    }

    // ── header ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Folder name + item count, with a BACK chevron on any level but the first. The chevron is absent (not
    /// disabled) at the root: there is nowhere to go, and a dead affordance in a 40-DIP header is pure noise.</summary>
    Element Header(SidebarFolderFlyoutNav nav, int count)
    {
        var kids = new List<Element>(3);
        if (nav.CanGoBack) kids.Add(BackButton(nav.Parent.Name));
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                BodyStrong(nav.Current.Name.Length > 0 ? nav.Current.Name : Loc.Get(SidebarRailFolderLoc.Folder)) with
                {
                    Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Caption(Strings.Sidebar.V3.ItemCount(count)) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinHeight = HeaderH, MinWidth = 0f,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, Spacing.XS),
            Children = kids.ToArray(),
        };
    }

    Element BackButton(string parentName) => ToolTip.Wrap(new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Cursor = CursorId.Hand,
        OnClick = GoBack,
        Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle),
        parentName.Length > 0 ? parentName : Loc.Get(SidebarRailFolderLoc.Back));

    // ── list ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    Element List(int cursor)
    {
        var rows = new Element[_children.Count];
        string sel = Owner.SelectedRoute;   // subscribe: the selected row's ramp must follow navigation
        for (int i = 0; i < _children.Count; i++) rows[i] = Row(_children[i], sel, i == cursor);
        return new ScrollEl
        {
            ContentSized = true, MaxHeight = MaxListH,
            Content = new BoxEl { Direction = 1, Gap = 1f, MinWidth = 0f, Children = rows },
        };
    }

    Element Row(in SidebarLibraryEntry entry, string sel, bool cursored)
    {
        var tree = Owner.Prefs?.Binder?.CurrentInput.PlaylistTree;
        var owner = Owner;
        var snapshot = entry;                       // an `in` parameter cannot be captured by the lazy closures
        bool folder = entry.Kind == SidebarEntryKind.Folder;
        string? route = entry.RouteKey;
        // The ROUTE selection and the KEYBOARD cursor share the row's one highlight channel deliberately: both mean
        // "this is the row in play", and two competing plates in a 300-DIP panel would read as a bug.
        bool selected = cursored || (route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal));

        Action activate = () => Activate(in snapshot);

        Func<ContextMenuModel?>? menu = null;
        if (owner.Acts is { } acts)
        {
            // The SAME row menu the expanded pane builds. A folder's expand/collapse verb still means the pane's
            // disclosure, not this flyout's drill-in: the two are different navigations, and folding them together would
            // make "Expand" do nothing visible while the pane is collapsed.
            Action? toggle = folder ? () => owner.ExpandFolderInPane(snapshot.FolderId) : null;
            bool expanded = folder && (owner.Prefs?.IsFolderExpanded(snapshot.FolderId) ?? false);
            menu = () => Menus.SidebarEntry(acts, in snapshot, toggle, expanded);
        }

        // A rootlist member either way, so a playlist can be dragged OUT of the flyout onto any sidebar/rootlist target
        // and a sub-folder can be filed elsewhere.
        var resource = WaveeResourceDragPayload.FromEntry(snapshot, owner.Acts?.Svc, rootlistItem: true);
        bool playlist = entry.Kind == SidebarEntryKind.Playlist;
        // The pane's OWN drop specs, never a second implementation. A sub-folder takes the rail's Into-only filing
        // spec (this panel has no insertion bands, exactly like a 56-DIP tile); a playlist takes the ordinary resource
        // spec at `slot: -1`, which is the whole-row track deposit and nothing else.
        DropTargetSpec? drop = folder
            ? (snapshot.FolderId.Length > 0 && resource is not null
                ? owner.RailFolderDropSpec(snapshot, resource)
                : null)
            : playlist
                ? owner.ResourceDropSpec(SectionId, slot: -1, snapshot.CanEdit ? snapshot.Uri : null, snapshot.Name,
                                         railCueUri: snapshot.Uri, isPlaylistRow: true)
                : null;
        string cueKey = folder ? snapshot.Id : snapshot.Uri;

        var spec = new SidebarRowSpec
        {
            Key = snapshot.Id,
            Label = snapshot.Name.Length > 0 ? snapshot.Name : SidebarPaneText.ShortUri(snapshot.Id),
            // A SUB-FOLDER's "N items" is the count of the very list this panel would drill into — the same
            // `ParentFolderId` containment its rows come from. Reading the projection's own `ChildCount` here instead
            // was the second definition that let a folder full of playlists render "0 items" (F1).
            Subtitle = folder
                ? Strings.Sidebar.V3.ItemCount(SidebarFolderTree.ChildCount(tree, snapshot.FolderId))
                : SidebarPaneText.SubtitleOf(in snapshot),
            Selected = selected,
            Density = SidebarDensity.Cozy,
            Leading = SidebarCover.ForEntry(in snapshot, SidebarRowMetrics.ArtFor(SidebarDensity.Cozy)),
            // A sub-folder announces that it drills IN — the panel's only structural affordance, and the pointer
            // equivalent of the → key.
            Trailing = folder ? Icon(Icons.ChevronRight, 12f, Tok.TextTertiary) with { Shrink = 0f } : null,
            OnClick = activate,
            Overflow = owner.Acts is not null && owner.MenuOverlay is not null,
            MenuOverlay = owner.MenuOverlay,
            Menu = menu,
            Drag = resource,
            DropTarget = drop,
            // COLD cue (the rail tile's channel, not the tree's three-outcome slot): every destination in this panel has
            // exactly one outcome — Into — so one bound boolean says all there is to say.
            DropActive = drop is null ? null : () => owner.IsRailDropActive(cueKey),
        };
        return SidebarEntityRow.Create(spec);
    }

    Element EmptyHint() => new BoxEl
    {
        MinHeight = 44f, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
        Children =
        [
            Body(Loc.Get(SidebarRailFolderLoc.SectionEmpty)) with
            {
                Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    // ── navigation ───────────────────────────────────────────────────────────────────────────────────────────────────

    void Activate(in SidebarLibraryEntry entry)
    {
        if (entry.Kind == SidebarEntryKind.Folder) { Drill(entry.FolderId, entry.Name); return; }
        if (entry.RouteKey is not { Length: > 0 } route) return;
        Owner.Navigate(route, entry.Name);
        Close();
    }

    void Drill(string folderId, string name)
    {
        if (_nav is not { } nav || !nav.Push(folderId, name)) return;
        _forward = true;
        _cursor.Value = -1;      // a fresh level starts un-cursored; the first ↓ lands on its first row
        _epoch.Value++;
    }

    void GoBack()
    {
        if (_nav is not { } nav || !nav.Pop()) return;
        _forward = false;
        _cursor.Value = -1;
        _epoch.Value++;
    }

    // ── keyboard ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>↑/↓ rove · Enter activates · → drills into a folder · ←/Backspace goes back. Escape is deliberately NOT
    /// handled: the popup's own <c>DismissBehavior.LightDismiss</c> owns it, and claiming it here would give the panel a
    /// second close path that could drift from click-away's.</summary>
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
                // Swallowed even at the root: a back gesture that fell through to the pane behind a focus-trapped popup
                // would navigate the app out from under a flyout the user is still reading.
                e.Handled = true;
                GoBack();
                return;
        }
    }
}

/// <summary>This surface's loc KEYS as literals, in one place — the `SidebarPaneLoc` precedent. Every key below exists
/// in <c>assets/loc/*.json</c>; a typo renders loudly as <c>[key]</c> rather than silently.</summary>
static class SidebarRailFolderLoc
{
    /// <summary>The back chevron's tooltip when the parent level has no name.</summary>
    public const string Back = "sidebar.rail.folderFlyoutBack";
    /// <summary>The kind word, used only when a folder somehow carries no name at all.</summary>
    public const string Folder = "sidebar.v3.kind.folder";
    /// <summary>The generic "Nothing here yet" — the SAME copy an empty section shows in the expanded pane, so an empty
    /// folder does not grow a second wording (see <c>SidebarPaneLoc.SectionEmpty</c>).</summary>
    public const string SectionEmpty = "sidebar.section.empty";
}
