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
//   COLUMN the rows sit in the pages' column (the T1 audit's A1, A2): a leading inset (the gutter), the rows capped at 2400 and a trailing
//          inset (the record's `Row2End`), centred together, so the first pivot stands on the page content's x at every width and the pins and
//          the last action end on its right edge. The insets are spacers whose widths ease on a crossing (size channels, never a slide).
//   FADE   at Compact and Small rows 1 and 2 scroll under an edge fade of max(48, 16 % of the band's width) (A7, the board's 84 % mask).
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
        var tiers = facts.Value.Tiers;
        var rec = ZuneBandRules.BandOf(tiers);
        var type = ZuneBandTiers.TypeOf(tiers);
        var hooks = UseContext(InputHooks.Current);
        var overlay = UseContext(Overlay.Service);
        // The frame provides the core's chrome signal; a band mounted alone reads the band record's own.
        var frameChrome = UseContext(ZuneFrameContext.PageChrome);
        var chromeSig = frameChrome ?? band.Chrome;
        // In the frame, the band slot eases the height (ZuneFrame.BandSlot) and clips this root's foot; mounted alone the root does it itself.
        bool inFrame = frameChrome is not null;
        // Navigation rate: the memo flips only when the page's last direction crosses down / not down.
        var scrolledDown = UseComputed(() => _scroll is { } s && s.Value.Direction == PageScrollDirection.Down);
        // At rest (the top, no direction yet): a SHORT window starts with row 2 folded away (Z-Shell-Short) and gets it back as the page scrolls up.
        var atRest = UseComputed(() => _scroll is not { } s || s.Value.Direction == PageScrollDirection.None);
        // The fade tiers' edge fade follows the band's width (A7; at Compact and Small the panel floats, so the band spans the window). A memo
        // in 4-DIP steps: a live resize re-renders the band per step of fade, and never at the tiers that do not fade.
        var viewport = UseContextSignal(Viewport.Size);
        var factsSignal = facts;
        var fade = UseComputed(() => ZuneBandRules.EdgeFade(ZuneBandRules.BandOf(factsSignal.Value.Tiers), viewport.Value.Width));

        var chrome = chromeSig.Value;
        float edgeFade = fade.Value;
        var destinations = band.Destinations();
        int current = band.Current();
        bool pinsShown = ZuneBandRules.ShowPins(rec, ZuneOptions.BandPins());
        var pins = pinsShown ? band.Pins() : s_noPins;
        var overflow = ZuneOptions.Overflow();
        // The record's tiers collapse, and a Short window at any width (Z-Shell-Short, ZT1).
        // A Short window (below the Regular height rung) rests collapsed: row 2 returns on a scroll up, as it does after any scroll down.
        bool collapsed = ZuneBandTiers.Collapses(rec, tiers) && ZuneOptions.CollapseBand()
            && (scrolledDown.Value || (ZuneBandTiers.RestsCollapsed(tiers) && atRest.Value));

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
            words.Add(Pivot(destinations[shown], lit: shown == current, tabStop: true, slot: 0, rec, type, OnTabKey));
            words.Add(MoreWord(() => OpenMore(rest), slot: 1, rec, type, OnTabKey));
        }
        else
        {
            int stop = ZuneBandRules.TabStop(destinations.Count, current);
            for (int i = 0; i < order.Length; i++)
            {
                int index = order[i];
                words.Add(Pivot(destinations[index], lit: index == current, tabStop: index == stop, slot: i, rec, type, OnTabKey));
            }
        }

        var run = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = rec.PivotGap, Height = rec.Row1H, Shrink = 0f,
            Children = words.ToArray(),
        };
        // ONE element type at every tier (ZT1): a crossing that turns the fade on or off keeps the run's node and the pivots in it (a
        // ScrollEl / BoxEl swap under one key would remount them). Where nothing overflows the scroller is inert; the fade is the tier's.
        Element pivotRun = new ScrollEl
        {
            Key = ZuneBandRules.PivotRunKey, Horizontal = true, Content = run, Height = rec.Row1H,
            Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            AutoEdgeFade = overflow == PivotOverflow.EdgeFadeScroll || rec.Fade, AutoEdgeFadeBand = edgeFade,
            SuppressScrollBar = true,
        };

        var row1Kids = new List<Element>(2) { pivotRun };
        if (pins.Count > 0) row1Kids.Add(Pins(pins, rec));

        var row1 = new BoxEl
        {
            Key = ZuneBandRules.Row1Key, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row1H, Shrink = 0f, MinWidth = 0f,
            Gap = rec.RowGap,
            Children = row1Kids.ToArray(),
        };

        // ── row 2 (mounted while shown: it leaves whole, fading, before the root's height folds; see Row2) ─────────────────────
        var kids = new List<Element>(2) { row1 };
        _views.Begin();
        if (row2) kids.Add(Row2(views, actions, title, rec, type, edgeFade, OnTabKey));

        // THE INSETS (lead 16 / 24 / 48 / 48 / 64, trailing 0 / 24 / 48 / 48 / 64) are spacers, not the root's padding (ZT1): a crossing eases
        // their WIDTHS (a reflow, a size channel), so the rows ride the re-solve. A position FLIP of the rows would be snapped by the engine's
        // reflow-shove rule whenever the page beside the band reflows or reveals on the same crossing. THE COLUMN (A1): the rows grow up to the
        // page cap between the two insets and the three are centred, so the rows' x is the page content's, max(gutter, (W - 2400) / 2); where
        // the cap holds (Huge, Huge 4K) the insets are equal, so as they ease together the rows stay put.
        return new BoxEl
        {
            Key = ZuneBandRules.BandKey, Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = !inFrame,
            Height = rec.HeightWith(row2),
            Animate = inFrame || Design.Reduced ? null : ZuneBandRules.HeightMotion,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Start, Justify = FlexJustify.Center,
                    Children =
                    [
                        Inset(rec.Inset),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = ZuneFrameRules.PageCap,
                            Children = kids.ToArray(),
                        },
                        Inset(rec.Row2End),
                    ],
                },
            ],
        };
    }

    /// <summary>One of the band's insets: a fixed width that eases through real layout when the tier changes it (a size channel).</summary>
    static Element Inset(float width) => new BoxEl
    {
        Width = width, Shrink = 0f, HitTestVisible = false,
        Animate = Design.Reduced ? null : ZuneBandTiers.InsetMotion,
    };

    // ── row 1's parts ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One pivot word: a tab (the lit one is the row's tab stop), a click goes to its destination.</summary>
    Element Pivot(BandDestination d, bool lit, bool tabStop, int slot, ZuneBandLayout rec, ZuneBandType type,
        Action<BandTabRow, int, KeyEventArgs> key)
    {
        // The library pivot says "library" (Z-Part-Band), not the core's "Your Library"; the other words are the core's.
        var text = PivotText(R.Type.Pivot(string.Equals(d.Id, LibraryId, StringComparison.Ordinal) ? Loc.Get(Strings.Theme.Zune.Band.Library) : d.Label), type);
        var bold = text with { Weight = (ushort)R.Type.FacetTitleStyle.SelectedWeight, Color = Tok.TextPrimary };
        text = lit
            ? bold
            : text with { Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary };
        text = text with { Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f };
        // The word's width is the SELECTED weight's whether it is lit or not: an unlit word carries its bold twin, unseen, as the width it
        // keeps, so lighting it never widens the word by 4 DIP and the pivots beside it never shift.
        Element word = lit ? text : new BoxEl
        {
            Direction = 0, ZStack = true, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children = [new BoxEl { Opacity = 0f, HitTestVisible = false, Shrink = 0f, Children = [bold with { Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f }] }, text],
        };
        var nodes = _pivots;
        string id = d.Id;
        nodes.Add(id);
        return new BoxEl
        {
            Key = ZuneBandRules.PivotKeyPrefix + d.Id, Shrink = 0f, Height = rec.Row1H, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Tab, Focusable = true, TabStop = tabStop, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            // The lit pivot moves to the front at Compact, and a crossing changes the gap and (at Huge 4K) the size: the words slide to their
            // new places (the pivot slide, parent-relative), never jump.
            Animate = Design.Reduced ? null : ZuneMotion.PivotSlide,
            OnClick = d.Go,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => nodes.Realized(id, h),
            Children = [word],
        };
    }

    /// <summary>The "more" word (MoreMenu at Compact and Small): opens the other destinations as a menu.</summary>
    Element MoreWord(Action open, int slot, ZuneBandLayout rec, ZuneBandType type, Action<BandTabRow, int, KeyEventArgs> key)
    {
        var nodes = _pivots;
        nodes.Add(ZuneBandRules.MoreKey);
        return new BoxEl
        {
            // Row 1's height, like a pivot: the Compact target floor (40) holds.
            Key = ZuneBandRules.MoreKey, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row1H,
            Role = AutomationRole.Button, Focusable = true, TabStop = false, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = open,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => { _moreAnchor = h; nodes.Realized(ZuneBandRules.MoreKey, h); },
            Children =
            [
                PivotText(R.Type.Pivot(Loc.Get(Strings.Common.More)), type) with
                {
                    Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f,
                },
            ],
        };
    }

    /// <summary>The pivot voice at the tier's size (Huge 4K's 40 / 48; the role's own elsewhere).</summary>
    static TextEl PivotText(TextEl text, ZuneBandType type)
        => type.PivotIsRole ? text : text with { Size = type.PivotSize, LineHeight = type.PivotLine };

    /// <summary>Row 2's voice at the tier's size (Huge 4K's 15 / 20; the role's own elsewhere).</summary>
    static TextEl SubText(TextEl text, ZuneBandType type)
        => type.SubIsRole ? text : text with { Size = type.SubSize, LineHeight = type.SubLine };

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
            // The pins come and go with the tier (Standard and Huge show them): they fade and open their width (a reflow), so the pivot run beside
            // them glides to its new width instead of snapping.
            Animate = Design.Reduced ? null : ZuneBandRules.PinsMotion, ClipToBounds = true,
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
                            new TextEl(ZunePlayerLayout.PlaySolid)
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
    Element Row2(IReadOnlyList<PageView> views, IReadOnlyList<PageAction> actions, string? title, ZuneBandLayout rec, ZuneBandType type,
        float edgeFade, Action<BandTabRow, int, KeyEventArgs> key)
    {
        bool tabStyle = title is not null;
        var run = new List<Element>(views.Count + 2);
        if (title is not null)
        {
            // A10 (contract 1.30): the title is the entity's name once the page has published it, the kind ("Album") while it loads. The slot
            // keeps its height and stacks its words (ZStack), each in a layer keyed by its text: the old one fades out as the new one fades in,
            // in place, so the swap never relayouts; the views and the hairline beside it slide to the new width (the pivot slide).
            run.Add(new BoxEl
            {
                Key = ZuneBandRules.Row2TitleKey, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row2H,
                MaxWidth = ZuneBandRules.TitleMaxW, Shrink = 1f, MinWidth = 0f, ZStack = true,
                Animate = Design.Reduced ? null : ZuneMotion.PivotSlide,
                Children =
                [
                    new BoxEl
                    {
                        Key = ZuneBandRules.Row2TitleKey + ":" + title, Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                        Children =
                        [
                            SubText(R.Type.SubPivot(title), type) with
                            {
                                // The entity's name in its own casing (A10: names are never re-cased), ellipsised at 360.
                                Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight, Color = Tok.TextPrimary, Case = TextCase.AsWritten,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                            },
                        ],
                    },
                ],
            });
            run.Add(new BoxEl
            {
                Width = ZuneBandRules.HairlineW, Height = ZuneBandRules.HairlineH, Shrink = 0f, HitTestVisible = false,
                Animate = Design.Reduced ? null : ZuneMotion.PivotSlide,
                Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
            });
        }

        int stop = 0;
        for (int i = 0; i < views.Count; i++) if (views[i].Current) { stop = i; break; }
        var nodes = _views;
        for (int i = 0; i < views.Count; i++)
        {
            nodes.Add(views[i].Id);
            run.Add(View(views[i], tabStyle, i == stop, i, rec, type, nodes, key));
        }

        Element viewsRun = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = rec.Row2Gap, Height = rec.Row2H, Shrink = 0f,
            Children = run.ToArray(),
        };
        // One element type at every tier (ZT1), as row 1's run: a crossing keeps the views' nodes.
        viewsRun = new ScrollEl
        {
            Horizontal = true, Content = viewsRun, Height = rec.Row2H, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            AutoEdgeFade = rec.Fade, AutoEdgeFadeBand = edgeFade, SuppressScrollBar = true,
        };

        var kids = new List<Element>(2) { viewsRun };
        if (actions.Count > 0) kids.Add(Actions(actions));
        // The row's words are the page's: when the page swaps (the chrome moves to the incoming page as its enter starts, Shell.PageChromeChannel.
        // PublishCurrent) the set of views and actions changes, and the old row fades out where it stands as the new one fades in, in step with
        // the page's own enter, instead of the words popping. The slot is a ZStack keyed by the row's own makeup (never by its title, which a
        // loading page republishes in place), so the row keeps its height and nothing relayouts.
        return new BoxEl
        {
            Key = ZuneBandRules.Row2Key, Direction = 0, AlignItems = FlexAlign.Center, Height = rec.Row2H, Shrink = 0f, MinWidth = 0f, ZStack = true,
            // The row is whole or gone: it fades and slides out before the band's height eases away and fades in after it settles, so the clip
            // never cuts it half way (the artist fidelity lane); inside, the page-synced swap layers cross-fade the views and actions.
            Enter = Design.Reduced ? default : ZuneBandRules.Row2Enter, Exit = Design.Reduced ? default : ZuneBandRules.Row2Exit,
            Children =
            [
                new BoxEl
                {
                    Key = ZuneBandRules.Row2Key + ":" + RowSignature(views, actions), Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center,
                    Height = rec.Row2H, MinWidth = 0f, Gap = rec.Row2Gap,
                    Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                    Children = kids.ToArray(),
                },
            ],
        };
    }

    /// <summary>What makes one row 2 a different row from another: the ids of its views and of its actions, in order.</summary>
    static string RowSignature(IReadOnlyList<PageView> views, IReadOnlyList<PageAction> actions)
    {
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < views.Count; i++) sb.Append(views[i].Id).Append(',');
        sb.Append('|');
        for (int i = 0; i < actions.Count; i++) sb.Append(actions[i].Id).Append(',');
        return sb.ToString();
    }

    /// <summary>One view of row 2: a tab; the current one is the weight (or, in the tab style, the accent underline).</summary>
    static Element View(PageView v, bool tabStyle, bool tabStop, int slot, ZuneBandLayout rec, ZuneBandType type, BandTabRow nodes,
        Action<BandTabRow, int, KeyEventArgs> key)
    {
        var text = SubText(R.Type.SubPivot(v.Label), type) with
        {
            Color = v.Current ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary,
            Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f,
        };
        // As row 1's pivots: the word keeps the SELECTED weight's width lit or not (an unlit one carries its bold twin, unseen), so lighting a view
        // never widens it and the views beside it never shift.
        Element word = text;
        if (!tabStyle)
        {
            var bold = text with { Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight };
            if (v.Current) word = bold;
            else word = new BoxEl
            {
                Direction = 0, ZStack = true, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = [new BoxEl { Opacity = 0f, HitTestVisible = false, Shrink = 0f, Children = [bold] }, text],
            };
        }
        return new BoxEl
        {
            Key = ZuneBandRules.ViewKeyPrefix + v.Id, Direction = 1, Height = rec.Row2H, MinWidth = rec.ViewMinW, Shrink = 0f,
            Role = AutomationRole.Tab, Focusable = true, TabStop = tabStop, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = v.Go,
            OnKeyDown = e => key(nodes, slot, e),
            OnRealized = h => nodes.Realized(v.Id, h),
            Children =
            [
                // Centred: at Compact the hit box is 40 wide even for a one-word "all" (ViewMinW).
                new BoxEl { Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [word] },
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
        // Compact draws no actions: crossing 640 they fade, never pop.
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneBandRules.ActionGap, Shrink = 0f, Children = kids,
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
        };
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
                    new TextEl(ZunePlayerLayout.PlaySolid)
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
