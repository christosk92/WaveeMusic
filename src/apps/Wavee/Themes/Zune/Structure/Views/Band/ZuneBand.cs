// ── Themes/Zune/Structure/Views/Band/ZuneBand.cs — Zune's `Shell.NavPane`: the pivot band (themes plan 6.3; P5 Z2b) ───────────────────
//
// Board Z-Part-Band (and every surface board's band: Z-Frame-Home, Z-Library-*, Z-Browse, Z-Artist). Zune has no sidebar: the band over
// the page column IS the navigation. The frame (Z2a) mounts `Shell.NavPane` in its band slot (`zune-band-slot`), which hugs whatever this
// view draws; the band sets and animates its own height. Everything it shows is the core's (`NavPaneProps.Band`, contract 1.26): the
// destinations and which is lit, the capped pins, the current page's chrome (read through `ZuneFrameContext.PageChrome`) and its scroll.
//
//   ROW 1  the destinations as lowercase pivot words (home · browse · library · recents; the Pivot role, the lit one at the selected weight
//          in the primary ink, the rest tertiary), then, at Standard and Huge with the `bandPins` option on, "pinned" and up to six pins
//          (art; a click opens, the play disc that shows on hover plays, a 4-dip dot under the art is lit while the pin plays).
//          OVERFLOW (`pivotOverflow`): EdgeFadeScroll scrolls the run under an edge fade (Compact and Small fade; Compact rotates the lit
//          word to the front, the words sliding); MoreMenu, at Compact and Small, keeps the lit word and folds the others (and the hub's
//          profile and settings) into a "more" menu.
//   ROW 2  the page's views (Library's kinds, Browse's bands, a page's in-page tabs), the current one lit (the weight; on a detail page,
//          which lights no pivot, the page's title and a hairline lead and the current view takes the accent underline), then the page's
//          actions (the play disc, the shuffle ring, word pills). Hidden when the page has none.
//   HEIGHT row 1 + row 2 + 8 (84 at Standard), row 1 + 8 (52) with no row 2 or collapsed: the root's height eases between them (a reflow
//          on the height axis, so the page under it follows frame by frame, never a jump); the rows keep their full height and are clipped
//          from the bottom. COLLAPSE (`collapseBand`, the tiers that collapse): row 2 folds while the page last scrolled down.
//   KEYS   each row is a tab list: one tab stop (the lit word), Left / Right / Home / End move the focus along it, Enter opens.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's band: the registered template of <see cref="ViewKeys.ShellNavPane"/>.</summary>
internal static class ZuneBandView
{
    private static readonly BoxEl s_none = new() { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>The registered template: the band, or nothing where the core hands no band data (a fixture).</summary>
    public static Element Template(NavPaneProps props, in ViewContext ctx)
        => props.Band is null ? s_none : Embed.Comp(props, static () => new ZuneBand());
}

/// <summary>The band itself. A component: it keeps the rows' node handles for the keyboard and derives the collapse from the page scroll
/// (a memo, so a scrolled frame re-renders nothing unless the collapse flips).</summary>
internal sealed class ZuneBand : Component
{
    static readonly IReadOnlyList<BandPin> s_noPins = [];
    static readonly IReadOnlyList<PageView> s_noViews = [];
    static readonly IReadOnlyList<PageAction> s_noActions = [];
    const string LibraryId = "library";

    readonly BandTabRow _pivots = new();
    readonly BandTabRow _views = new();
    IReadSignal<PageScroll>? _scroll;
    NodeHandle _moreAnchor;
    OverlayHandle? _moreMenu;

    public override Element Render()
    {
        var props = UseProps<NavPaneProps>();
        var band = props.Band!;
        _scroll = band.Scroll;
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        var rec = ZuneBandRules.BandOf(facts.Value.Tiers);
        var hooks = UseContext(InputHooks.Current);
        var overlay = UseContext(Overlay.Service);
        // The frame provides the core's chrome signal; a band mounted alone reads the band record's own.
        var chromeSig = UseContext(ZuneFrameContext.PageChrome) ?? band.Chrome;
        // Navigation rate: the memo flips only when the page's last direction crosses down / not down.
        var scrolledDown = UseComputed(() => _scroll is { } s && s.Value.Direction == PageScrollDirection.Down);

        var chrome = chromeSig.Value;
        var destinations = band.Destinations();
        int current = band.Current();
        bool pinsShown = ZuneBandRules.ShowPins(rec, ZuneOptions.BandPins());
        var pins = pinsShown ? band.Pins() : s_noPins;
        var overflow = ZuneOptions.Overflow();
        bool collapsed = rec.Collapses && ZuneOptions.CollapseBand() && scrolledDown.Value;

        var views = chrome?.Views ?? s_noViews;
        var actions = rec.TitleAndActions ? chrome?.Actions ?? s_noActions : s_noActions;
        bool hasRow2 = views.Count > 0 || actions.Count > 0;
        bool row2 = ZuneBandRules.Row2Shown(hasRow2, collapsed);
        // A detail page (no lit pivot) leads row 2 with its title; its current view takes the underline (the tab style).
        bool detail = current < 0;
        string? title = rec.TitleAndActions && detail && views.Count > 0 && chrome is { Title.Length: > 0 } c ? c.Title : null;

        void OnTabKey(BandTabRow row, int at, KeyEventArgs e)
        {
            if (e.Handled || e.Mods != KeyModifiers.None) return;
            var key = e.KeyCode switch
            {
                Keys.Left => ZuneBandRules.TabKey.Previous,
                Keys.Right => ZuneBandRules.TabKey.Next,
                Keys.Home => ZuneBandRules.TabKey.First,
                Keys.End => ZuneBandRules.TabKey.Last,
                _ => ZuneBandRules.TabKey.None,
            };
            if (key == ZuneBandRules.TabKey.None) return;
            e.Handled = true;
            int target = ZuneBandRules.Step(at, row.Count, key);
            if (target >= 0 && row.HandleAt(target) is { IsNull: false } node) (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(node);
        }

        void OpenMore(IReadOnlyList<BandDestination> rest)
        {
            if (_moreMenu is { IsOpen: true } open) { open.Close(); return; }
            var items = new MenuFlyoutItem[rest.Count];
            for (int i = 0; i < rest.Count; i++) items[i] = new MenuFlyoutItem(rest[i].Label, rest[i].Glyph, true, rest[i].Go);
            var opened = overlay.Open(() => _moreAnchor, () => MenuFlyout.Create(items, () => _moreMenu?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
            opened.ClosedAction = () => _moreMenu = null;
            _moreMenu = opened;
        }

        // ── row 1 ────────────────────────────────────────────────────────────────────────────────────────────────────────
        var order = ZuneBandRules.PivotOrder(destinations.Count, current, rec.Rotate);
        bool more = overflow == PivotOverflow.MoreMenu && rec.Narrow && destinations.Count > 1;
        var words = new List<Element>(order.Length + 1);
        _pivots.Begin();
        if (more)
        {
            // The lit word (the first when none is lit), then "more" holding the others and the hub's extras.
            int shown = ZuneBandRules.TabStop(destinations.Count, current);
            var rest = new List<BandDestination>(6);
            foreach (var d in band.Hub()) if (!string.Equals(d.Id, destinations[shown].Id, StringComparison.Ordinal)) rest.Add(d);
            words.Add(Pivot(destinations[shown], lit: shown == current, tabStop: true, slot: 0, rec, OnTabKey));
            words.Add(MoreWord(() => OpenMore(rest), slot: 1, OnTabKey));
        }
        else
        {
            int stop = ZuneBandRules.TabStop(destinations.Count, current);
            for (int i = 0; i < order.Length; i++)
            {
                int index = order[i];
                words.Add(Pivot(destinations[index], lit: index == current, tabStop: index == stop, slot: i, rec, OnTabKey));
            }
        }

        var run = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = rec.PivotGap, Height = rec.Row1H, Shrink = 0f,
            Children = words.ToArray(),
        };
        Element pivotRun = overflow == PivotOverflow.EdgeFadeScroll || rec.Fade
            ? new ScrollEl
            {
                Key = ZuneBandRules.PivotRunKey, Horizontal = true, Content = run, Height = rec.Row1H,
                Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = ZuneBandRules.EdgeFadeBand,
                SuppressScrollBar = true,
            }
            : new BoxEl
            {
                Key = ZuneBandRules.PivotRunKey, Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Height = rec.Row1H, Children = [run],
            };

        var row1Kids = new List<Element>(2) { pivotRun };
        if (pins.Count > 0) row1Kids.Add(Pins(pins, rec));

        var row1 = new BoxEl
        {
            Key = ZuneBandRules.Row1Key, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row1H, Shrink = 0f, MinWidth = 0f,
            Gap = rec.RowGap,
            Children = row1Kids.ToArray(),
        };

        // ── row 2 (laid out at its full height whenever the page has one: the root's height clips it while it folds) ───────────
        var kids = new List<Element>(2) { row1 };
        _views.Begin();
        if (hasRow2) kids.Add(Row2(views, actions, title, rec, OnTabKey));

        return new BoxEl
        {
            Key = ZuneBandRules.BandKey, Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
            Height = rec.HeightWith(row2),
            Padding = new Edges4(rec.Inset, 0f, 0f, 0f),
            Animate = Design.Reduced ? null : ZuneBandRules.HeightMotion,
            Children = kids.ToArray(),
        };
    }

    // ── row 1's parts ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One pivot word: a tab (the lit one is the row's tab stop), a click goes to its destination.</summary>
    Element Pivot(BandDestination d, bool lit, bool tabStop, int slot, ZuneBandLayout rec, Action<BandTabRow, int, KeyEventArgs> key)
    {
        // The library pivot says "library" (Z-Part-Band), not the core's "Your Library"; the other words are the core's.
        var text = R.Type.Pivot(string.Equals(d.Id, LibraryId, StringComparison.Ordinal) ? Loc.Get(Strings.Theme.Zune.Band.Library) : d.Label);
        text = lit
            ? text with { Weight = (ushort)R.Type.FacetTitleStyle.SelectedWeight, Color = Tok.TextPrimary }
            : text with { Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary };
        text = text with { Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f };
        var nodes = _pivots;
        string id = d.Id;
        nodes.Add(id);
        return new BoxEl
        {
            Key = ZuneBandRules.PivotKeyPrefix + d.Id, Shrink = 0f, Height = rec.Row1H, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Tab, Focusable = true, TabStop = tabStop, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            // The lit pivot moves to the front at Compact: the words slide to their new places (the pivot slide), never jump.
            Animate = rec.Rotate && !Design.Reduced ? ZuneMotion.PivotSlide : null,
            OnClick = d.Go,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => nodes.Realized(id, h),
            Children = [text],
        };
    }

    /// <summary>The "more" word (MoreMenu at Compact and Small): opens the other destinations as a menu.</summary>
    Element MoreWord(Action open, int slot, Action<BandTabRow, int, KeyEventArgs> key)
    {
        var nodes = _pivots;
        nodes.Add(ZuneBandRules.MoreKey);
        return new BoxEl
        {
            Key = ZuneBandRules.MoreKey, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Button, Focusable = true, TabStop = false, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = open,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => { _moreAnchor = h; nodes.Realized(ZuneBandRules.MoreKey, h); },
            Children =
            [
                R.Type.Pivot(Loc.Get(Strings.Common.More)) with
                {
                    Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f,
                },
            ],
        };
    }

    /// <summary>"pinned", then the pins (at most the core's cap, six).</summary>
    static Element Pins(IReadOnlyList<BandPin> pins, ZuneBandLayout rec)
    {
        var kids = new Element[pins.Count + 1];
        kids[0] = new BoxEl
        {
            Height = rec.PinArt, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Margin = ZuneBandRules.PinnedWordMargin,
            Children = [R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Band.Pinned)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 }],
        };
        for (int i = 0; i < pins.Count; i++) kids[i + 1] = Pin(pins[i], rec);
        return new BoxEl
        {
            Key = ZuneBandRules.PinsKey, Direction = 0, AlignItems = FlexAlign.Start, Gap = ZuneBandRules.PinGap, Shrink = 0f,
            Padding = new Edges4(0f, 0f, ZuneBandRules.PinsEnd, 0f),
            Children = kids,
        };
    }

    /// <summary>One pin: its art (a click opens it; the play disc over its corner, shown on hover, plays it), the playing dot under it.</summary>
    static Element Pin(BandPin pin, ZuneBandLayout rec)
    {
        float art = rec.PinArt;
        var corners = pin.Circular ? CornerRadius4.All(art / 2f) : R.Shape.All(ShapeRole.PinArt);
        var tileKids = new List<Element>(2)
        {
            Controls.Artwork(pin.Image, art, art, pin.Circular ? art / 2f : R.Shape.For(ShapeRole.PinArt), decodePx: ZuneBandRules.PinDecodePx),
        };
        if (pin.Play is { } play)
        {
            tileKids.Add(new BoxEl
            {
                Grow = 1f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                Children =
                [
                    ToolTip.Wrap(new BoxEl
                    {
                        Key = ZuneBandRules.PinPlayKeyPrefix + pin.Id,
                        Width = ZuneBandRules.PinPlay, Height = ZuneBandRules.PinPlay, Corners = CornerRadius4.All(ZuneBandRules.PinPlay / 2f),
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Fill = Prop.Of(static () => R.Ink.AccentAction),
                        // A reveal: the tile's hover shows it (the pointer is not on it yet).
                        Opacity = 0f, HoverOpacity = 1f,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand, TabStop = false,
                        OnClick = play,
                        Children =
                        [
                            new TextEl(Icons.Play)
                            {
                                Size = ZuneBandRules.PinPlayGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                                Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)),
                            },
                        ],
                    }, Strings.Theme.Zune.Band.PlayPin(pin.Label)),
                ],
            });
        }

        var tile = ToolTip.Wrap(new BoxEl
        {
            Width = art, Height = art, ZStack = true, Shrink = 0f, ClipToBounds = true, Corners = corners,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = pin.Go,
            Children = tileKids.ToArray(),
        }, pin.Label);

        var playing = pin.Playing;
        return new BoxEl
        {
            Key = ZuneBandRules.PinKeyPrefix + pin.Id, Direction = 1, AlignItems = FlexAlign.Center, Gap = ZuneBandRules.PinDotGap, Shrink = 0f,
            Children =
            [
                tile,
                new BoxEl
                {
                    Width = ZuneBandRules.PinDot, Height = ZuneBandRules.PinDot, Corners = CornerRadius4.All(ZuneBandRules.PinDot / 2f),
                    HitTestVisible = false,
                    Fill = Prop.Of(() => playing() ? Tok.AccentDefault : ColorF.Transparent),
                },
            ],
        };
    }

    // ── row 2 ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Row 2: a detail page's title and hairline, the views (a tab list), then the actions at the end.</summary>
    Element Row2(IReadOnlyList<PageView> views, IReadOnlyList<PageAction> actions, string? title, ZuneBandLayout rec,
        Action<BandTabRow, int, KeyEventArgs> key)
    {
        bool tabStyle = title is not null;
        var run = new List<Element>(views.Count + 2);
        if (title is not null)
        {
            run.Add(new BoxEl
            {
                Key = ZuneBandRules.Row2TitleKey, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row2H,
                MaxWidth = ZuneBandRules.TitleMaxW, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.SubPivot(title) with
                    {
                        Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                ],
            });
            run.Add(new BoxEl
            {
                Width = ZuneBandRules.HairlineW, Height = ZuneBandRules.HairlineH, Shrink = 0f, HitTestVisible = false,
                Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
            });
        }

        int stop = 0;
        for (int i = 0; i < views.Count; i++) if (views[i].Current) { stop = i; break; }
        var nodes = _views;
        for (int i = 0; i < views.Count; i++)
        {
            nodes.Add(views[i].Id);
            run.Add(View(views[i], tabStyle, i == stop, i, rec, nodes, key));
        }

        Element viewsRun = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = rec.Row2Gap, Height = rec.Row2H, Shrink = 0f,
            Children = run.ToArray(),
        };
        viewsRun = rec.Fade
            ? new ScrollEl
            {
                Horizontal = true, Content = viewsRun, Height = rec.Row2H, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                AutoEdgeFade = true, AutoEdgeFadeBand = ZuneBandRules.EdgeFadeBand, SuppressScrollBar = true,
            }
            : new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Height = rec.Row2H, Children = [viewsRun] };

        var kids = new List<Element>(2) { viewsRun };
        if (actions.Count > 0) kids.Add(Actions(actions));
        return new BoxEl
        {
            Key = ZuneBandRules.Row2Key, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row2H, Shrink = 0f, MinWidth = 0f,
            Gap = rec.Row2Gap, Padding = new Edges4(0f, 0f, rec.Row2End, 0f),
            Children = kids.ToArray(),
        };
    }

    /// <summary>One view of row 2: a tab; the current one is the weight (or, in the tab style, the accent underline).</summary>
    static Element View(PageView v, bool tabStyle, bool tabStop, int slot, ZuneBandLayout rec, BandTabRow nodes,
        Action<BandTabRow, int, KeyEventArgs> key)
    {
        var text = R.Type.SubPivot(v.Label) with
        {
            Color = v.Current ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary,
            Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f,
        };
        if (v.Current && !tabStyle) text = text with { Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight };
        return new BoxEl
        {
            Key = ZuneBandRules.ViewKeyPrefix + v.Id, Direction = 1, Height = rec.Row2H, Shrink = 0f,
            Role = AutomationRole.Tab, Focusable = true, TabStop = tabStop, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = v.Go,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => nodes.Realized(v.Id, h),
            Children =
            [
                new BoxEl { Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Children = [text] },
                // The underline's place is always kept, so the word sits at one height lit or not.
                new BoxEl
                {
                    Height = ZuneBandRules.UnderlineH, Shrink = 0f, HitTestVisible = false,
                    Fill = v.Current && tabStyle ? Prop.Of(static () => Tok.AccentDefault) : ColorF.Transparent,
                },
            ],
        };
    }

    /// <summary>The page's actions: the play disc, the shuffle ring and the word pills, 14 apart.</summary>
    static Element Actions(IReadOnlyList<PageAction> actions)
    {
        var kids = new Element[actions.Count];
        for (int i = 0; i < actions.Count; i++) kids[i] = ActionButton(actions[i]);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneBandRules.ActionGap, Shrink = 0f, Children = kids };
    }

    static Element ActionButton(PageAction a)
    {
        float h = ZuneBandRules.ActionH;
        var round = CornerRadius4.All(h / 2f);
        BoxEl box = a.Kind switch
        {
            PageActionKind.Play => new BoxEl
            {
                Width = h, Height = h, Corners = round, Fill = Prop.Of(static () => R.Ink.AccentAction),
                Children =
                [
                    new TextEl(Icons.Play)
                    {
                        Size = ZuneBandRules.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                        Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)),
                    },
                ],
            },
            PageActionKind.Shuffle => new BoxEl
            {
                Width = h, Height = h, Corners = round, BorderWidth = ZuneBandRules.RingBorder, BorderColor = Tok.TextPrimary,
                HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
                Children = [new TextEl(Icons.Shuffle) { Size = ZuneBandRules.ShuffleGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextPrimary }],
            },
            _ => new BoxEl
            {
                Height = h, Corners = round, Padding = ZuneBandRules.PillPadding,
                BorderWidth = ZuneBandRules.PillBorder, BorderColor = Tok.AccentDefault,
                HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
                Children =
                [
                    new TextEl(a.Label)
                    {
                        Size = ZuneBandRules.PillText, Color = Tok.AccentDefault, MaxLines = 1, Case = R.Case.For(LabelKind.Command),
                    },
                ],
            },
        };
        box = box with
        {
            Key = ZuneBandRules.ActionKeyPrefix + a.Id, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = a.Enabled, AllowFocusOnInteraction = false,
            Cursor = a.Enabled ? CursorId.Hand : CursorId.Arrow,
            Opacity = a.Enabled ? 1f : ZuneBandRules.DisabledOpacity,
            OnClick = a.Enabled ? a.Invoke : null,
        };
        return a.Kind == PageActionKind.Word ? box : ToolTip.Wrap(box, a.Label);
    }
}

/// <summary>One tab list of the band (row 1's words, row 2's views): the ids in their order this render and the node of each id as it was
/// realized (kept by id across renders: a node is realized once, and a rotation moves ids between slots).</summary>
internal sealed class BandTabRow
{
    readonly List<string> _ids = [];
    readonly Dictionary<string, NodeHandle> _nodes = new(StringComparer.Ordinal);

    /// <summary>How many tabs the row holds this render.</summary>
    public int Count => _ids.Count;

    /// <summary>A render starts the row over (the realized nodes are kept).</summary>
    public void Begin() => _ids.Clear();

    /// <summary>The next tab, by id.</summary>
    public void Add(string id) => _ids.Add(id);

    /// <summary>The node of the tab with <paramref name="id"/> was realized.</summary>
    public void Realized(string id, NodeHandle node) => _nodes[id] = node;

    /// <summary>The node of the tab at <paramref name="index"/>, or null while it is not realized.</summary>
    public NodeHandle HandleAt(int index)
        => index >= 0 && index < _ids.Count && _nodes.TryGetValue(_ids[index], out var node) ? node : NodeHandle.Null;
}
