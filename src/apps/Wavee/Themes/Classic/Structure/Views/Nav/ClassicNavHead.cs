// ── Themes/Classic/Structure/Views/Nav/ClassicNavHead.cs — Classic's section head, "+" button and counts (themes plan 6.1, T5b) ──
//
// From `Shell/Sidebar.UI.Rows.cs` (`Sidebar.SectionHeader.Header` / `Separator`, `Sidebar.CreateButton`, `Sidebar.Counts`): the 40-px
// section header (the title, the hover-revealed ⋯, the "+", the chevron slot), the 8-px separator, THE "+" create affordance
// (the section header's, a folder row's and Your Library's toolbar's) and the quiet count. They are pure builders over what the
// row slot hands them; the collapse chevron is the core's shared `Sidebar.Chevron` (the episode discussion draws with it too), the
// disclosure state machine is the pane's (`INavRowHost.ToggleSection`, `DisclosureOpen`).
//
// The Edit outline's rows (T5d, `ClassicNavEdit.cs`) draw their ⋯ and Unpin buttons with the same `InlineButton` below. Numbers are `Rules\SidebarGeometry.cs` and `Rules\NavHeadLayout.cs`
// -- WVTH003 allows no literal look here.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The 40-px section header (design V.6): the title at slot x 10 (pane 14, the covers' left edge), 14 SemiBold TextSecondary, brightening
/// to TextPrimary on hover (no plate); then the ⋯ (revealed on hover, permanent after a touch), the "+" (always, Playlists /
/// Your Library), and the chevron (24×24, always visible, ending at pane W − 4). A click anywhere collapses; the slot root
/// is the roving focus stop (ToggleButton). <paramref name="onToggle"/> null ⇒ a heading with no chevron.</summary>
internal static class ClassicNavHead
{
    public static Element Header(string title, bool open, Action<bool>? onToggle, Element? create, Element? more,
                                 Element? chevron)
    {
        var kids = new List<Element>(5)
        {
            new TextEl(title)
            {
                Size = ClassicNavHeadLayout.SectionTitleSize, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary,
                Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap,
            },
            new BoxEl { Grow = 1f },
        };
        // The hover-revealed ⋯ goes BEFORE the "+": at rest an invisible ⋯ between them read as a hole beside the chevron.
        if (more is not null) kids.Add(more.Value);
        if (create is not null) kids.Add(create.Value);
        if (onToggle is not null) kids.Add(chevron ?? Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 12f, Tok.TextSecondary));
        Action? click = null;
        if (onToggle is { } toggle) click = () => toggle(!open);
        return new BoxEl
        {
            Direction = 0, Height = SidebarGeometry.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 0f,
            Padding = new Edges4(SidebarGeometry.HeaderTextX, 0f, 0f, 0f),
            Role = onToggle is null ? AutomationRole.None : AutomationRole.ToggleButton,
            Cursor = onToggle is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = click,
            Children = [.. kids],
        };
    }

    /// <summary>A header's 24×24 inline button (⋯ / +): consumes its own click, hover plate FillSubtleSecondary.
    /// <paramref name="reveal"/> = hover-revealed (the ⋯ off touch). The result is an OUTER, handler-less reveal box
    /// (Opacity / HoverOpacity follow the header's hover) around the INNER interactive button: the plate belongs to the
    /// inner node alone, so hovering the header text reveals the glyph at its rest look and only hovering the button
    /// paints the plate (one node carrying both the reveal and <c>Interactive</c> let the container hover drive the
    /// fill). A <see cref="BoxEl"/> so the caller can attach a context menu to the outer box (a context request walks up
    /// from the button); <paramref name="requestsContext"/> makes the click re-enter that funnel, anchored at the
    /// button. <paramref name="onRealized"/> / <paramref name="focusable"/> apply to the INNER button, the real focus
    /// stop and anchor. The caller wraps the result in <c>ToolTip.Wrap</c> for its tooltip and accessible name.</summary>
    public static BoxEl InlineButton(string glyph, Action? onClick, bool reveal, Action<NodeHandle>? onRealized = null,
                                     bool focusable = false, bool requestsContext = false)
        => new BoxEl
        {
            Width = SidebarGeometry.HeaderButton, Height = SidebarGeometry.HeaderButton, Shrink = 0f,
            Opacity = reveal ? 0f : 1f, HoverOpacity = 1f,
            Children =
            [
                new BoxEl
                {
                    Width = SidebarGeometry.HeaderButton, Height = SidebarGeometry.HeaderButton,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = requestsContext ? null : onClick, ClickRequestsContext = requestsContext,
                    Focusable = focusable, OnRealized = onRealized,
                    BlocksDragArm = true,
                    Children = [Icon(glyph, 12f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle),
            ],
        };

    /// <summary>The 8-px separator (TR:223, TR:247): a 1-px StrokeDividerDefault rule at y 3, bleeding over the list's
    /// 4-px inset so it spans the full pane width.</summary>
    public static Element Separator() => new BoxEl
    {
        Direction = 1, Height = SidebarGeometry.SeparatorHeight, Shrink = 0f,
        Margin = new Edges4(-SidebarGeometry.PaneEdge, 0f, -SidebarGeometry.PaneEdge, 0f),
        Padding = new Edges4(0f, SidebarGeometry.SeparatorLineTop, 0f, 0f),
        HitTestVisible = false,
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
    };
}

/// <summary>
/// THE "+" create affordance for every surface that offers it (the PlaylistTree header, a folder row, the rail
/// footer, V3's header), so "what does + do" cannot drift per design.
///
/// <para>With a <c>menu</c> it opens a flyout built AT OPEN TIME (labels resolve then, never at render); a factory
/// that answers nothing keeps the one direct verb — an affordance that opens nothing reads as broken. It is also a
/// DROP destination whose spec and <c>dropActive</c> probe are the CALLER's (only the pane knows what a drop there
/// means); the cue is a BOUND accent plate because it runs while a drag is live. <c>revealOpacity</c> is the folder
/// row's reveal: hover flags freeze mid-drag, so the bound base opacity is what shows the "+" during a gesture.</para>
///
/// <para><c>BlocksDragArm</c> stops the drag-arm walk here, so pressing "+" on a draggable row neither lifts that row
/// nor toggles the folder under it.</para>
/// </summary>
internal sealed class ClassicNavCreateButton : Component
{
    readonly Action _onPlaylist;
    readonly Func<ContextMenuModel?>? _menu;
    readonly DropTargetSpec? _drop;
    readonly Func<bool>? _dropActive;
    readonly Func<float>? _revealOpacity;
    readonly float _box, _glyph;

    public ClassicNavCreateButton(Action onPlaylist, Func<ContextMenuModel?>? menu = null, DropTargetSpec? drop = null,
                        Func<bool>? dropActive = null, Func<float>? revealOpacity = null, float box = 24f,
                        float glyph = 14f)
    {
        _onPlaylist = onPlaylist; _menu = menu; _drop = drop; _dropActive = dropActive;
        _revealOpacity = revealOpacity; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Activate()
        {
            if (_menu is null || svc is null) { _onPlaylist(); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            if (_menu() is not { } model || model.Rows.Count == 0) { _onPlaylist(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(model.Rows, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                 Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        var box = new BoxEl
        {
            Width = _box, Height = _box, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            BlocksDragArm = true,
            OnRealized = h => anchor.Value = h,
            OnClick = Activate,
            DropTarget = _drop,
            Children = [Icon(Icons.Add, _glyph, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);

        // AFTER Interactive, which rewrites Fill/BorderColor wholesale — the drag cue has to land on top of it.
        if (_dropActive is { } cue)
            box = box with
            {
                Fill = Prop.Of(() => cue() ? Tok.AccentDefault with { A = 0.18f } : ColorF.Transparent),
                BorderColor = Prop.Of(() => cue() ? Tok.AccentDefault : ColorF.Transparent),
                BorderWidth = 1f,
            };
        // The reveal sits on an OUTER handler-less box (as in ClassicNavHead.InlineButton): on the interactive button
        // itself the container hover would drive its plate, so hovering the row alone painted the "+" plate.
        Element shown = box;
        if (_revealOpacity is { } reveal)
            shown = new BoxEl
            {
                Width = _box, Height = _box, Shrink = 0f,
                Opacity = Prop.Of(reveal), HoverOpacity = 1f,
                Children = [box],
            };

        return ToolTip.Wrap(shown, Loc.Get(_menu is null
            ? Strings.Sidebar.CreatePlaylistTooltip
            : Strings.Sidebar.CreateTooltip));
    }
}

/// <summary>THE one count renderer. A library shortcut's count is ambient, not a notification: an 11-DIP tertiary
/// number, never WinUI's accent InfoBadge pill (five of those read as five alerts).</summary>
internal static class ClassicNavCounts
{
    /// <summary>The pending plate (20×12) — narrower and shorter than a badge, so it reads "a number is coming".</summary>
    public const float PlateW = 20f, PlateH = 12f;

    /// <summary>The number, or the pending plate while <paramref name="count"/> is unknown.</summary>
    public static Element Badge(int? count) => count is { } n ? Number(n) : Pending();

    public static Element Number(int count) => R.Type.MicroMeta(FormatCache.Int(count)) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
    };

    public static Element Pending() => new BoxEl
    {
        Width = PlateW, Height = PlateH, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>A library shortcut's LIVE count as its own 11-DIP component (W3-A2): the relation's publish signal
    /// is subscribed inside a <c>UseComputed</c> whose value is <see cref="Sidebar.ShortcutCount.Stamp"/> — scope epoch,
    /// edge state, total — so a membership answer that left the number alone resolves the memo EQUAL and renders
    /// nothing; only a moved count (a like, a save, a page landing) re-renders this badge, and never its row. Keyed by
    /// kind: the route row it sits in is keyed by its route, so a slot recycling onto another shortcut remounts the
    /// row and this with it — the kind captured in the factory can never go stale.</summary>
    public static Element Live(NavModel model, LibraryEdgeKind kind)
        => Embed.Comp(() => new CountHost(model, kind)) with { Key = Sidebar.ShortcutCount.KeyOf(kind) };

    sealed class CountHost : Component
    {
        readonly Func<Sidebar.ShortcutCount.Stamp> _stamp;

        public CountHost(NavModel model, LibraryEdgeKind kind)
            => _stamp = () => model.ReadShortcutCount(kind);   // the scope epoch first, then the relation's wake: the model's read

        public override Element Render() => Badge(Sidebar.ShortcutCount.Shown(UseComputed(_stamp).Value));
    }
}
