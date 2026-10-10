// ── Home/Customize.UI.cs — the Home customizer, rebuilt from scratch over home-layout.json v2 ─────────────────────────
//
// Owner rule: NO reference to `HomeCustomizerPage`/`Home.Customizer.cs`/`HomeLayoutStore`/`HomePreferences` — read
// only to learn the shape of the problem (a plain scrolling column of rows, a Reorderable over a fixed row extent,
// a per-row signal kept in sync with the live document because a ToggleSwitch's controlled signal freezes its
// INSTANCE at mount). Every type here rides the fresh v2 store instead: `Home/LayoutFile.cs`'s `LayoutZone`,
// `LayoutDoc`, `LayoutEntry`, `LayoutCommands` and the `HomeLayout` service (Wave 2, F29) — All facet only, per the
// plan. No local `LayoutStore`/document copy here: this screen and `HomeScreen` (`Home/Screen.UI.cs`) both read the
// ONE `HomeLayout` instance through `UseRequiredContext(HomeLayout.Slot)`, so a toggle/reorder here is visible on
// Home the same frame it commits (F29 — the old customizer's private `_layoutDoc` never synced with Home's).
//
// Role: UI
// Owner: B5
// Wave: 2
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H, New files table row `Customize.UI.cs`
//   ("CustomizeScreen (ToggleSwitch visibility + Reorderable), entry: 'Customize Home' link at page end + palette
//   command"); "Customizer: fresh home-layout.json v2 … All facet only" (Removals section);
//   docs/plans/wavee/home-redesign-remediation.md §3.7 (the `HomeLayout` service replacing the two private copies).

using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>A list of <see cref="LayoutZone"/> rows for the All facet: a <see cref="ToggleSwitch"/> for visibility,
/// drag-reorder, and Up/Down fallback buttons for keyboard use. Every mutation goes through
/// <see cref="LayoutCommands"/> and lands via <see cref="HomeLayout.Dispatch"/> (the .bak rotation is
/// <see cref="LayoutStore"/>'s, not this file's).</summary>
public sealed class CustomizeScreen : Component
{
    const float RowHeight = 48f;
    const string DragKind = "wavee.home-zone-layout";

    /// <summary>Route-constructible factory — C1 (Wave 4) wires <c>Shell.SetPage(RouteKind.HomeCustomize, …)</c> to
    /// this; this file does not touch <c>Shell.cs</c>.</summary>
    public static Element For() => Embed.Comp(static () => new CustomizeScreen());

    readonly Reorderable _reorder;
    readonly Action<int, int> _onReorder;

    /// <summary>The context-resolved service, refreshed every render (hooks run unconditionally at the top of
    /// <see cref="Render"/>) — <see cref="Toggle"/>/<see cref="Move"/>/<see cref="Reset"/> read this FIELD rather
    /// than re-resolving the context, since they run from a later frame's click/drop callback, not from render.</summary>
    HomeLayout? _layout;

    public CustomizeScreen()
    {
        _onReorder = Move;
        // The row dims in place (opacity, below) — no custom drag chip; the engine's own ghost lift is enough for a
        // 9-row settings list (unlike the old customizer's module chip, which existed for a much longer list).
        _reorder = new Reorderable(DragKind) { ItemExtent = RowHeight, Spacing = 0f, RequireDropOnList = true, OnReorder = _onReorder };
    }

    void Toggle(LayoutZone zone) => _layout!.Dispatch(doc => LayoutCommands.Toggle(doc, zone));
    void Move(int from, int to) => _layout!.Dispatch(doc => LayoutCommands.Move(doc, from, to));
    void Reset() => _layout!.Dispatch(_ => LayoutCommands.Reset());

    public override Element Render()
    {
        var layout = UseRequiredContext(HomeLayout.Slot);
        _layout = layout;
        var doc = layout.Doc.Value;
        int n = doc.Zones.Count;
        _reorder.Scene = Context.Scene;
        _reorder.RequestRender = Context.RequestRerender;
        _reorder.ItemCount = n;

        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int item = _reorder.ItemAt(i);
            if ((uint)item >= (uint)n) item = i;
            var entry = doc.Zones[item];
            rows[i] = _reorder.Item(item, RowFor(entry, item, n), key: entry.Kind);
        }

        float g = Shell.Ui.PageGutter.Value;
        return new BoxEl
        {
            Key = "home-customize", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                Header(g),
                new BoxEl
                {
                    // The reorderable list is the scroller, and its bottom clears the player bar (PageGeometry.BottomReserve).
                    Key = "home-customize-list", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                    Padding = new Edges4(g, 0f, g, PageGeometry.BottomReserve),
                    Children =
                    [
                        // The explainer is the body's first paragraph, not a second meta line: the head stays one fixed height.
                        Ui.Body(Loc.Get(Strings.Home.CustomizeBody)) with
                        {
                            Key = "home-customize-explainer", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 0f,
                            Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                        },
                        _reorder.List(new BoxEl { Direction = 1, MinWidth = 0f, Grow = 1f, Shrink = 1f, Children = rows }),
                    ],
                },
            ],
        };
    }

    /// <summary>The shared page head, kind Title (<c>PageHeadRules.Extent</c> 120): Reset is the title row's action. No
    /// meta, so the reserved meta line stays empty.</summary>
    Element Header(float gutter) => PageHead.Create(new PageHeadSpec(Loc.Get(Strings.Home.Customizer.Title))
    {
        Actions = Button.Standard(Loc.Get(Strings.Home.ResetDefault), Reset) with { Shrink = 0f },
        Gutter = gutter, Key = "home-customize-header",
    });

    Element RowFor(LayoutEntry entry, int index, int count)
        => Embed.Comp(new RowProps(this, entry, index, count), static () => new ZoneRow());

    internal sealed record RowProps(CustomizeScreen Owner, LayoutEntry Entry, int Index, int Count);

    /// <summary>Gripper · label · Up/Down (keyboard fallback for drag) · the visibility toggle. The toggle's own
    /// <see cref="Signal{T}"/> instance freezes at mount (<c>ToggleSwitch.Create</c>'s contract), so it is kept in
    /// sync with the live document through a layout effect rather than rebuilt — the same shape the old customizer's
    /// row used, over the new document.</summary>
    sealed class ZoneRow : Component
    {
        readonly Signal<bool> _visible = new(true);
        bool _wantVisible;
        Action? _sync;
        Action<bool>? _onToggle;

        public override Element Render()
        {
            var p = UseProps<RowProps>();
            bool visible = p.Entry.Visible;
            _wantVisible = visible;
            UseLayoutEffect(_sync ??= SyncToggle, DepKey.From(visible ? 1 : 0, p.Index));

            LayoutZones.TryParse(p.Entry.Kind, out var zone);
            string label = ZoneLabel(zone);
            var owner = p.Owner;
            int index = p.Index, count = p.Count;

            return new BoxEl
            {
                Direction = 0, Height = RowHeight, AlignItems = FlexAlign.Center, Gap = 8f,
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Opacity = visible ? 1f : 0.55f,
                Children =
                [
                    Ui.Icon(Icons.GripperBar, 12f, Tok.TextTertiary),
                    new TextEl(label)
                    {
                        Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary,
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    },
                    // loc: "Move up" / "Move down" — reuses the app's existing move-row vocabulary (Strings.Menu.*).
                    Controls.Named(Controls.IconAction(Icons.ChevronUp, index > 0 ? () => owner.Move(index, index - 1) : null),
                        Loc.Get(Strings.Menu.MoveUp)),
                    Controls.Named(Controls.IconAction(Icons.ChevronDown, index < count - 1 ? () => owner.Move(index, index + 1) : null),
                        Loc.Get(Strings.Menu.MoveDown)),
                    ToggleSwitch.Create(_visible, onChange: _onToggle ??= _ => owner.Toggle(zone)),
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

    // ── the "Customize Home" link (end of the All page) + the palette command ─────────────────────────────────────

    /// <summary>The "Customize Home" <see cref="HyperlinkButton"/> shown at the end of the All page (plan: "a
    /// 'Customize Home' HyperlinkButton at the end of the All page plus a palette command"). Navigates to
    /// <see cref="Shell.RouteKind.HomeCustomize"/> — that route kind is already registered in <c>Shell.cs</c>'s
    /// route table, so this needs no routing help from C1. <paramref name="onClick"/> overrides the navigation
    /// (tests, or a caller that wants to compose it with something else); leave it null for the default.</summary>
    public static Element CustomizeLink(Action? onClick = null)
        => HyperlinkButton.Create(Loc.Get(Strings.Home.Customizer.Title), onClick ?? GoToCustomize);

    /// <summary>The palette command's action ("Home: Customize" — C1 registers the command itself in Wave 4's
    /// palette wiring; this is the one line it calls).</summary>
    public static void GoToCustomize() => Shell.GoTo(new Shell.Route(Shell.RouteKind.HomeCustomize));
}
