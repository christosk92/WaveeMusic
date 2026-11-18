// ── Themes/Zune/Structure/Views/Podcast/ZunePodcastKit.cs — the parts Zune's podcast pages v2 share (show, episode, audiobook) ──────────────────────
//
// Concept `winui-podcast-v2.html`, theme=zune: no chrome, no cards, flat colour, light lowercase display type.
//
//   Page(...)        ONE scroller (keyed per route) over the page column (the frame's gutters, capped and centred as every Zune page), ending past the dock.
//   Title(...)       the name: the tier's display size (120/130; 80/84 at Compact, where it wraps), weight 200, lowercase, on ONE line that
//                    runs past the column to the window's edge and dissolves under a fade there. Never fitted, never shrunk (owner rule).
//   Pivots(...)      the pivot words on ONE line (28/36 light, the lit one in the primary ink, a small count beside a word), bleeding under the same
//                    fade; a change slides the strip like a Windows Phone pivot (`ZunePivotCarousel`: the lit word leads, the rest follow round).
//   Body(...)        a pivot's body, keyed per pivot: the new one fades in where it lands.
//   Button(...)      a Zune command: square, a 1-DIP ring, lowercase words (the accent fill for the one primary), hover / pressed washes, a hand.
//   Split(...)       the primary as a split: the main half plays, the chevron opens "Other ways to play" (the menu's items, the default checked).
//   Menu(...)        a command that opens a radio menu (the speed, the sleep timer).
//   More(...)        the "..." command over a lazily built context menu.
//   Label / Meta     the accent label over the primary ("continue"); a meta line whose parts carry their own "·" (a wrapped line never ends on one).
//   Line(...)        the 2-DIP Zune progress line, its fill bound.
//   EpisodeRow(...)  an episode row: the play glyph (no plate), the 18/24 light title (white unplayed, grey played), the meta (the square accent "new",
//                    the date, the length or the accent time left), one line of description where the tier writes it, the "..." at the end; a click
//                    expands it in place (Open episode at its top, the description through the house renderer, Play next); right-click is its menu.
//   Description(...) HTML or plain text through the house rich-text renderer (never a tag on screen).
//   Cards(...)       "More like this" on the house media surface (`Controls.Surface`: hover, play, pressed, focus, menu, hand).
//
// No numbers here (WVTH003): `ZunePodcastRules`. No Entities / Playback (WVTH002): every verb comes in as a delegate.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZunePodcastRules;

namespace Wavee.Themes.Zune;

/// <summary>The verbs and live cells a <see cref="ZunePodcastKit.EpisodeRow"/> reads. ONE per page (the rows compare it by reference).</summary>
internal sealed class ZunePodcastRowVerbs
{
    public required IReadSignal<string?> Expanded { get; init; }
    public required IReadSignal<string?> Playing { get; init; }
    public required Action<string> Play { get; init; }
    public required Action<string> Open { get; init; }
    public required Action<string> PlayNext { get; init; }
    /// <summary>A click: expand in place (the show), or null to open the episode (the episode page's "next in this show").</summary>
    public required Action<string?>? Expand { get; init; }
    public required Func<string, ContextMenuModel?> Menu { get; init; }
    /// <summary>A row's drag payload (the page props' <c>RowDrag</c>; null: the rows carry no drag).</summary>
    public Func<string, object?>? Drag { get; init; }
}

/// <summary>Zune's podcast kit (see the file header).</summary>
internal static class ZunePodcastKit
{
    // ══ 1. THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page: the scroller over the page column. <paramref name="content"/> is the column's body (no gutters of its own).</summary>
    public static Element Page(string routeKey, Element content, float gutter, ScrollHandle? scroll = null, Action<NodeHandle>? viewport = null)
    {
        var column = new BoxEl
        {
            Key = "zune-podcast-page", Direction = 1, MinWidth = 0f, Animate = ZuneMotion.GroupResize,
            Padding = new Edges4(0f, Z.PageTop, 0f, Z.PageFoot + R.Space.DockReserve),
            Children = [content],
        };
        return ScrollView(ZuneFrameRules.PageRow(column, gutter)) with
        {
            Key = "podcast-scroll:" + routeKey, Grow = 1f, MinHeight = 0f, ScrollKey = "podcast-scroll:" + routeKey, Handle = scroll,
            EdgeCues = ScrollEdgeCues.None, OnRealized = viewport,
        };
    }

    // ══ 2. THE NAME ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page's name (see the file header). <paramref name="gutter"/> is how far it runs past the column (the window's edge).</summary>
    public static Element Title(string text, ZunePodcastLayout lay, float gutter)
    {
        var voice = R.Type.Panorama(text) with
        {
            Size = lay.TitleSize, LineHeight = lay.TitleLine, LineStacking = LineStacking.BlockLineHeight, Color = Tok.TextPrimary,
            Case = R.Case.For(LabelKind.Heading), MinWidth = 0f,
        };
        float nudge = ZuneDetailTiers.For(lay.Tier).PanoramaNudgeX;
        TextEl title = lay.TitleWraps
            ? voice with { Wrap = TextWrap.WrapWholeWords, MaxLines = 0, Shrink = 1f }
            : voice with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f };
        return new BoxEl
        {
            Key = ZuneDetailRules.PanoramaKey, Direction = 0, MinWidth = 0f, ClipToBounds = !lay.TitleWraps, Opacity = Z.TitleOpacity,
            // The descenders hang a few DIP under the exact line: the box holds them (the pivots' air above them is that much less).
            Padding = new Edges4(0f, 0f, 0f, Z.TitleDescent),
            Margin = new Edges4(nudge, 0f, lay.TitleWraps ? 0f : -gutter, Z.TitleBottom),
            EdgeFade = lay.TitleWraps ? default : new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, Z.TitleFade, 0f),
            Animate = ZuneMotion.TitleScale,
            Children = [title],
        };
    }

    /// <summary>The book's subtitle under the name: the pivot voice (28/36 light) in the tertiary ink, lowercase, on one line bleeding under the fade.</summary>
    public static Element Sub(string text, float gutter) => new BoxEl
    {
        Key = "zp-sub", Direction = 0, MinWidth = 0f, ClipToBounds = true, Margin = new Edges4(0f, 0f, -gutter, Z.SubBottom),
        EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, Z.PivotFade, 0f),
        Children = [R.Type.Pivot(text) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Heading), Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f }],
    };

    // ══ 3. THE PIVOTS ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One pivot: its word and its small count ("" = none; a pending count keeps its room with a blank).</summary>
    public readonly record struct PivotItem(string Word, string Count);

    /// <summary>The pivot row (see the file header): <paramref name="items"/> in their order, <paramref name="selected"/> lit and leading.</summary>
    public static Element Pivots(string key, ImmutableArray<PivotItem> items, int selected, Action<int> select, ZunePodcastLayout lay, float gutter)
        => Embed.Comp(new PivotProps(key, items, selected, select, lay.PivotGap, gutter), static () => new PivotHost()) with { Key = ZuneFrameRules.PageTitleKey };

    sealed record PivotProps(string Key, ImmutableArray<PivotItem> Items, int Selected, Action<int> Select, float Gap, float Gutter)
    {
        public bool Equals(PivotProps? o) => o is not null && Key == o.Key && Selected == o.Selected && Gap == o.Gap && Gutter == o.Gutter
            && Items.SequenceEqual(o.Items);
        public override int GetHashCode() => HashCode.Combine(Key, Selected, Gap, Gutter, Items.Length);
    }

    sealed class PivotHost : Component
    {
        readonly ZunePivotCarousel<int> _carousel = new();

        public override Element Render()
        {
            var p = UseProps<PivotProps>();
            int n = p.Items.Length;
            int lit = Math.Clamp(p.Selected, 0, Math.Max(0, n - 1));
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = (lit + i) % n;
            _carousel.Order = order;
            _carousel.Show = p.Select;
            _carousel.Gap = p.Gap;
            _carousel.Gutter = 0f;
            int orderKey = 0;
            foreach (int i in order) orderKey = orderKey * 8 + i + 1;
            UseLayoutEffect(() =>
            {
                if (Context.Scene is { } scene && Context.Anim is { } anim) _carousel.Slide(scene, anim, true, Design.Reduced);
            }, DepKey.From(orderKey, n));

            var words = new Element[n];
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                var it = p.Items[i];
                bool on = i == lit;
                var kids = new List<Element>(2)
                {
                    R.Type.Pivot(it.Word) with
                    {
                        Color = on ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Navigation),
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f, BrushTransitionMs = ZuneMotion.PivotSlideMs,
                    },
                };
                if (it.Count.Length > 0)
                    kids.Add(new TextEl(it.Count)
                    {
                        Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f,
                        AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, Z.CountLift, 0f, 0f),
                    });
                words[k] = new BoxEl
                {
                    Key = p.Key + ":" + i, Direction = 0, Gap = Z.CountGap, Shrink = 0f, MinHeight = Z.PivotH, AlignItems = FlexAlign.Center,
                    Role = AutomationRole.Tab, Focusable = true, TabStop = on, AllowFocusOnInteraction = false,
                    Cursor = on ? CursorId.Arrow : CursorId.Hand, OnClick = on ? null : _carousel.ClickOf(i), OnRealized = _carousel.RealizedOf(i),
                    Children = kids.ToArray(),
                };
            }
            return new BoxEl
            {
                Key = p.Key, Direction = 0, Gap = p.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
                Margin = new Edges4(0f, Z.PivotTop, -p.Gutter, Z.PivotBottom),
                EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, Z.PivotFade, 0f),
                OnKeyDown = _carousel.OnKey, OnRealized = _carousel.OnStrip,
                Children = words,
            };
        }
    }

    /// <summary>A pivot's body, keyed per pivot under a plain anchor (a slot's lone child ignores its key): the new body fades in where it lands.</summary>
    public static Element Body(string key, Element body, bool fill = false) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Grow = fill ? 1f : 0f,
        Children = [new BoxEl { Key = key, Direction = 1, MinWidth = 0f, Grow = fill ? 1f : 0f, Enter = Z.BodySwap, Children = [body] }],
    };

    // ══ 4. COMMANDS ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A Zune command (see the file header). <paramref name="label"/> null or empty: the glyph alone, named for its tooltip by
    /// <paramref name="name"/>. <paramref name="fill"/>: it takes the row's width.</summary>
    public static BoxEl Button(string key, string? label, string? glyph, Action? onClick, float height, bool accent = false, bool fill = false,
        string? name = null, bool on = false)
    {
        bool words = label is { Length: > 0 };
        var ink = accent ? (Prop<ColorF>)Prop.Of(static () => ColorContrast.PickContrast(Tok.AccentDefault)) : (Prop<ColorF>)Tok.TextPrimary;
        var kids = new List<Element>(2);
        if (glyph is not null)
            kids.Add(new TextEl(glyph) { Size = Z.ButtonGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = on && !accent ? (Prop<ColorF>)Tok.AccentTextPrimary : ink, Shrink = 0f });
        if (words)
            kids.Add(new TextEl(label!)
            {
                Size = Z.ButtonTextSize, LineHeight = Z.ButtonTextLine, Color = ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            });
        return new BoxEl
        {
            Key = key, Direction = 0, Gap = Z.ButtonGap, AlignItems = FlexAlign.Center, Justify = words ? FlexJustify.Start : FlexJustify.Center,
            Height = height, MinWidth = words ? 0f : height, Width = words ? float.NaN : height, Shrink = fill ? 1f : 0f, Grow = fill ? 1f : 0f,
            Padding = words ? new Edges4(Z.ButtonPadX, 0f, Z.ButtonPadX, 0f) : default,
            Fill = accent ? Prop.Of(static () => Tok.AccentDefault) : (Prop<ColorF>)ColorF.Transparent,
            HoverFill = accent ? Prop.Of(static () => Tok.AccentSecondary) : Prop.Of(static () => Tok.TextPrimary with { A = Z.ButtonHoverAlpha }),
            PressedFill = accent ? Prop.Of(static () => Tok.AccentTertiary) : Prop.Of(static () => Tok.TextPrimary with { A = Z.ButtonPressedAlpha }),
            BorderWidth = accent ? 0f : 1f, BorderColor = Prop.Of(static () => Tok.TextPrimary with { A = Z.ButtonRingAlpha }),
            Role = AutomationRole.Button, Focusable = onClick is not null, IsEnabled = onClick is not null, Cursor = onClick is null ? CursorId.Arrow : CursorId.Hand,
            AllowFocusOnInteraction = false, BlocksDragArm = true, Opacity = onClick is null ? ZuneBandRules.DisabledOpacity : 1f,
            OnClick = onClick, Children = kids.ToArray(),
        };
    }

    /// <summary>A command named for its tooltip where it shows no word (one wrapper at every tier, keyed as the command, so a crossing keeps its node).</summary>
    public static Element Named(BoxEl command, string name, bool words) => ZuneDetailKit.StableNamed(command, name, words);

    /// <summary>The primary as a split (see the file header). <paramref name="pausing"/>: the main half reads Pause.</summary>
    public static Element Split(string key, string label, bool accent, bool pausing, Action play, ImmutableArray<PodcastMenuItem> menu, float height, bool fill)
        => Embed.Comp(new SplitProps(label, accent, pausing, play, menu, height, fill), static () => new SplitHost()) with { Key = key };

    sealed record SplitProps(string Label, bool Accent, bool Pausing, Action Play, ImmutableArray<PodcastMenuItem> Menu, float Height, bool Fill)
    {
        public bool Equals(SplitProps? o) => o is not null && Label == o.Label && Accent == o.Accent && Pausing == o.Pausing && Height == o.Height
            && Fill == o.Fill && Menu.Length == o.Menu.Length && (Menu.IsDefaultOrEmpty || Menu[0].Label == o.Menu[0].Label && Menu[0].Checked == o.Menu[0].Checked);
        public override int GetHashCode() => HashCode.Combine(Label, Accent, Pausing, Height, Fill, Menu.Length);
    }

    sealed class SplitHost : Component, IPropsHost
    {
        SplitProps? _p;
        readonly Signal<SplitProps?> _props = new(null);
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        readonly Action _toggle, _play;
        readonly Action<NodeHandle> _capture;

        public SplitHost()
        {
            _toggle = Toggle;
            _play = () => _p?.Play();
            _capture = h => _anchor = h;
        }

        public void ApplyProps(object props)
        {
            _p = (SplitProps)props;
            _props.Value = _p;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _p!;
            _overlay = UseContext(Overlay.Service);
            string label = p.Pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : p.Label;
            string glyph = p.Pausing ? ZunePlayerLayout.PauseSolid : ZunePlayerLayout.PlaySolid;
            var main = Button("split:play", label, glyph, _play, p.Height, p.Accent, fill: p.Fill);
            var kids = new List<Element>(2) { main };
            if (!p.Menu.IsDefaultOrEmpty)
            {
                var chevron = Button("split:menu", null, Icons.ChevronDown, _toggle, p.Height, p.Accent) with
                {
                    BorderWidth = p.Accent ? 0f : 1f,
                };
                kids.Add(new BoxEl
                {
                    Width = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, HitTestVisible = false,
                    Fill = p.Accent ? Prop.Of(static () => Tok.WindowBackground with { A = Z.SeamAlpha }) : (Prop<ColorF>)ColorF.Transparent,
                });
                kids.Add(Controls.Named(chevron, Loc.Get(Strings.Podcast.Pages.OtherWays)));
            }
            return new BoxEl
            {
                Direction = 0, Height = p.Height, AlignItems = FlexAlign.Stretch, Shrink = p.Fill ? 1f : 0f, Grow = p.Fill ? 1f : 0f, MinWidth = 0f,
                OnRealized = _capture, Children = kids.ToArray(),
            };
        }

        void Toggle()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (_p is not { } p || Controls.IsNullOverlay(_overlay)) return;
            var items = new List<MenuFlyoutItem>(p.Menu.Length + 1);
            bool sep = false;
            foreach (var m in p.Menu)
            {
                if (!sep && (m.Glyph == Icons.Video || m.Glyph == Icons.Volume)) { items.Add(MenuFlyoutItem.Separator); sep = true; }
                items.Add(MenuFlyoutItem.Toggle(m.Label, m.Checked, m.Invoke, m.Glyph) with { AcceleratorText = m.Sub });
            }
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
        }
    }

    /// <summary>A command that opens a radio menu (<paramref name="items"/> built when it opens): the speed, the sleep timer. <paramref name="label"/>
    /// is live; <paramref name="words"/> false draws the glyph alone unless <paramref name="on"/>.</summary>
    public static Element Menu(string key, IReadSignal<string> label, IReadSignal<bool>? on, string? glyph, Func<ImmutableArray<PodcastMenuItem>> items,
        float height, bool words, string name)
        => Embed.Comp(new MenuProps(label, on, glyph, items, height, words, name), static () => new MenuHost()) with { Key = key };

    sealed record MenuProps(IReadSignal<string> Label, IReadSignal<bool>? On, string? Glyph, Func<ImmutableArray<PodcastMenuItem>> Items, float Height, bool Words,
        string Name);

    sealed class MenuHost : Component
    {
        MenuProps _p = null!;
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        Action? _toggle;
        Action<NodeHandle>? _capture;

        public override Element Render()
        {
            _p = UseProps<MenuProps>();
            _overlay = UseContext(Overlay.Service);
            _toggle ??= Toggle;
            _capture ??= h => _anchor = h;
            string label = _p.Label.Value;
            bool on = _p.On?.Value ?? false;
            bool words = _p.Words || on || _p.Glyph is null;
            var b = Button("menu", words ? label : null, _p.Glyph, _toggle, _p.Height, on: on) with { OnRealized = _capture };
            return Controls.Named(b, _p.Name);
        }

        void Toggle()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (Controls.IsNullOverlay(_overlay)) return;
            var items = new List<MenuFlyoutItem>();
            foreach (var m in _p.Items()) items.Add(MenuFlyoutItem.RadioItem(m.Label, m.Checked, m.Invoke));
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
        }
    }

    /// <summary>The "..." command over <paramref name="menu"/> (built when it opens), opened at the command.</summary>
    public static Element More(string key, Func<ContextMenuModel?> menu, float height, bool ring = true)
        => Embed.Comp(new MoreProps(menu, height, ring), static () => new MoreHost()) with { Key = key };

    sealed record MoreProps(Func<ContextMenuModel?> Menu, float Height, bool Ring)
    {
        public bool Equals(MoreProps? o) => o is not null && Height == o.Height && Ring == o.Ring;
        public override int GetHashCode() => HashCode.Combine(Height, Ring);
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight, KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    sealed class MoreHost : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreHost() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var overlay = UseContext(Overlay.Service);
            BoxEl b = Button("more", null, Icons.More, s_noop, _latest!.Height) with
            {
                ClickRequestsContext = true, OnClick = null, BorderWidth = _latest.Ring ? 1f : 0f,
            };
            if (!Controls.IsNullOverlay(overlay)) b = ContextSheets.Attach(b, overlay, _menu, s_moreMenu);
            return Controls.Named(b, Loc.Get(Strings.Common.More));
        }
    }

    static readonly Action s_noop = static () => { };

    // ══ 5. WORDS ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The accent label over the primary ("continue", "new", "audiobook · included in premium"): 13, lowercase.</summary>
    public static TextEl Label(string text) => new(text)
    {
        Size = Z.LabelSize, LineHeight = Z.LabelLine, Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label),
        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>A meta line on ONE line: each part after the first carries its own "·"; what does not fit runs off under the right edge's fade.</summary>
    public static Element Meta(string key, IReadOnlyList<Element> parts)
    {
        var kids = new Element[parts.Count];
        for (int i = 0; i < kids.Length; i++)
        {
            // Nothing shrinks (a squeezed part would slide under the next one's dot): the line's overflow fades instead. The one exception is a
            // part that gives way by itself, whole words at a time (WordLinks): it takes what is left and carries its own dots, so a line whose
            // words all left never ends on one.
            float shrink = parts[i] is BoxEl { Shrink: > 0f } ? 1f : 0f;
            kids[i] = i == 0 || shrink > 0f ? new BoxEl { Direction = 0, Shrink = shrink, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = [parts[i]] } : new BoxEl
            {
                Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Shrink = shrink, MinWidth = 0f,
                Children = [MetaWord("·") with { Color = Tok.TextTertiary }, parts[i]],
            };
        }
        // ONE line, never wrapped (a wrapped line would start on its part's dot, owner 2026-11-18): what does not fit runs off under the right edge's
        // fade, as the name and the pivots do, so no line starts or ends on a dot.
        return new BoxEl
        {
            // The clip keeps a little room for the light face's ink left of the first glyph (its side bearing), taken back by the margin.
            Key = key, Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Padding = new Edges4(Z.MetaInk, 0f, 0f, 0f), Margin = new Edges4(-Z.MetaInk, 0f, 0f, 0f),
            EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, Z.MetaFade, 0f), Children = kids,
        };
    }

    /// <summary>The topic / genre words of a meta line as ONE part, each word a link of its own (the accent ink at rest as the kind word's link, the
    /// primary ink on hover, focus, the hand; a name keeps its casing) that opens <paramref name="open"/> with its words, plain tertiary words when
    /// <paramref name="open"/> is null. Each word carries the "·" before it (<paramref name="leadDot"/> false: not the first, which starts the line).
    /// The part gives way by itself, a whole word at a time: the words that do not fit wrap onto a second line it clips away, so no word is cut or
    /// half a target, and the line never ends on a dot. Each word's hit rect reaches <paramref name="target"/> (its slop, inside the clip): the type
    /// keeps its size.</summary>
    public static BoxEl WordLinks(string key, ImmutableArray<string> words, Action<string>? open, float target, bool leadDot = true)
    {
        var face = R.Type.DenseMeta("");
        float line = face.LineHeight;
        float slop = MathF.Max(0f, (target - line) / 2f);
        // A wrapping line places its first item even where it does not fit: an empty first item (its gap taken back) lets the first word leave the
        // line too, so the clip never cuts a word.
        var units = new Element[words.Length + 1];
        units[0] = new BoxEl { Width = 0f, Height = line, Shrink = 0f, Margin = new Edges4(0f, 0f, -Z.MetaGap, slop) };
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            Element w = open is null
                ? face with { Text = word, Color = Tok.TextTertiary, Case = TextCase.AsWritten, MaxLines = 1, Wrap = TextWrap.NoWrap }
                : new BoxEl
                {
                    Key = key + ":" + i, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, HitSlop = new Edges4(0f, slop, 0f, slop),
                    Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => open(word),
                    Children = [face with
                    {
                        Text = word, Color = Prop.Of(static () => R.Ink.AccentDecor), HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,
                        Case = TextCase.AsWritten, MaxLines = 1, Wrap = TextWrap.NoWrap,
                    }],
                };
            units[i + 1] = new BoxEl
            {
                Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Shrink = 0f, Height = line, Margin = new Edges4(0f, 0f, 0f, slop),
                Children = i == 0 && !leadDot ? [w] : [MetaWord("·") with { Color = Tok.TextTertiary }, w],
            };
        }
        return new BoxEl
        {
            Key = key, Direction = 0, Wrap = true, Gap = Z.MetaGap, AlignItems = FlexAlign.Start, Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
            // The trailing room is the meta line's right-edge fade: a word that stays never sits under it, half faded.
            Height = line + slop + slop, Padding = new Edges4(0f, slop, Z.MetaFade, 0f), Margin = new Edges4(0f, -slop, 0f, -slop), Children = units,
        };
    }

    /// <summary>The separator that travels with both its neighbours (no-break spaces either side): a wrapping text never starts or ends a line on it.</summary>
    public const string Dot = " · ";

    /// <summary><paramref name="text"/> with every " · " glued to its neighbours (<see cref="Dot"/>), for a host's text that wraps.</summary>
    public static string Glue(string text) => text.Replace(" · ", Dot, StringComparison.Ordinal);

    /// <summary>A meta word: the 13 meta voice in the secondary ink, lowercased as a label.</summary>
    public static TextEl MetaWord(string text) => R.Type.TrackMeta(text) with
    {
        Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
    };

    /// <summary>A section heading (26/32 light, lowercased).</summary>
    public static TextEl Heading(string text) => ZuneDetailKit.Heading(text) with { Color = Tok.TextTertiary };

    /// <summary>A link word in the accent ink (lowercase, 13), with a hand, a hyperlink role and the hover ink.</summary>
    public static Element Link(string key, string text, Action go, float minHeight) => new BoxEl
    {
        Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, MinHeight = minHeight,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
        Children =
        [
            R.Type.DenseMeta(text) with
            {
                Color = Prop.Of(static () => R.Ink.AccentDecor), HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,
                Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    /// <summary>The 2-DIP Zune line: the track, the accent fill bound to <paramref name="value"/> (0..1) through a compositor scale.</summary>
    public static BoxEl Line(Func<float> value, float width = float.NaN) => new()
    {
        Height = Z.BarH, Width = width, MinWidth = 0f, Direction = 0, Shrink = 0f,
        Fill = Prop.Of(static () => Tok.TextPrimary with { A = Z.BarTrackAlpha }),
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = Prop.Of(static () => Tok.AccentDefault), TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Math.Clamp(value(), 0f, 1f)), 1f)),
            },
        ],
    };

    /// <summary>A description through the house rich-text renderer (paragraphs, lists, bold, links through the link policy). <paramref name="maxLines"/>
    /// 0 = all.</summary>
    public static Element Description(string html, int maxLines = 0, float size = Z.RowDescSize)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = [Controls.RichTextFlex(html, size, Tok.TextSecondary, R.Ink.AccentDecor, maxLines, DetailVerbs.NavigateKey)],
        };

    /// <summary>The play-next glyph the app uses for the verb (the Wavee icon face).</summary>
    public static Element PlayNextIcon(float size, ColorF color) => Icon(WaveeIcons.PlayNext, size, color, WaveeIcons.Font);

    // ══ 6. THE EPISODE ROW ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One episode row (see the file header), keyed <c>ep:</c> + the id.</summary>
    public static Element EpisodeRow(PodcastEpisodeVm vm, bool description, bool compact, ZunePodcastRowVerbs verbs)
        => Embed.Comp(new RowProps(vm, description, compact, verbs), static () => new RowHost()) with { Key = "ep:" + vm.Id };

    sealed record RowProps(PodcastEpisodeVm Vm, bool Description, bool Compact, ZunePodcastRowVerbs Verbs)
    {
        public bool Equals(RowProps? o) => o is not null && Vm.Equals(o.Vm) && Description == o.Description && Compact == o.Compact && ReferenceEquals(Verbs, o.Verbs);
        public override int GetHashCode() => HashCode.Combine(Vm.Id, Description, Compact);
    }

    sealed class RowHost : Component
    {
        readonly Signal<bool> _hover = new(false);
        RowProps _p = null!;
        Action? _click, _play, _next, _open, _exit;
        Action<Point2>? _move;
        Action<KeyEventArgs>? _key;
        Func<ContextMenuModel?>? _menu;
        DragSource? _drag;

        public override Element Render()
        {
            _p = UseProps<RowProps>();
            // The row drags as its episode (onto a playlist, the queue, the player bar); the payload is built when the drag promotes.
            if (_drag is null && _p.Verbs.Drag is not null) _drag = Wavee.Drag.Source(() => _p.Verbs.Drag?.Invoke(_p.Vm.Id));
            _click ??= () => { if (_p.Verbs.Expand is { } x) x(_p.Vm.Id); else _p.Verbs.Open(_p.Vm.Id); };
            _play ??= () => _p.Verbs.Play(_p.Vm.Id);
            _next ??= () => _p.Verbs.PlayNext(_p.Vm.Id);
            _open ??= () => _p.Verbs.Open(_p.Vm.Id);
            _move ??= _ => _hover.SetIfChanged(true);
            _exit ??= () => _hover.SetIfChanged(false);
            _key ??= OnKey;
            _menu ??= () => _p.Verbs.Menu(_p.Vm.Id);
            var overlay = UseContext(Overlay.Service);
            var p = _p;
            var vm = p.Vm;
            var v = p.Verbs;
            bool expandable = v.Expand is not null;
            bool expanded = expandable && v.Expanded.Value == vm.Id;
            bool playing = v.Playing.Value == vm.Id;
            bool played = vm.State == PodcastRowState.Played;
            bool hover = _hover.Value;
            float pb = p.Compact ? Z.RowPlayCompact : Z.RowPlay;
            float act = p.Compact ? Z.RowActionCompact : Z.RowAction;

            var title = new TextEl(vm.Title)
            {
                FontFamily = R.Type.Pivot("").FontFamily, Size = Z.RowTitleSize, LineHeight = Z.RowTitleLine, Weight = Z.Light,
                Color = played ? Tok.TextTertiary : Tok.TextPrimary, MaxLines = p.Compact ? 2 : 1, Wrap = p.Compact ? TextWrap.Wrap : TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            var text = new List<Element>(3) { title, RowMeta(vm) };
            if (p.Description && vm.Description.Length > 0 && !expanded)
                text.Add(new TextEl(vm.Description)
                {
                    Size = Z.RowDescSize, LineHeight = Z.RowDescLine, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });

            bool off = !vm.Playable;
            string glyph = playing ? ZunePlayerLayout.PauseSolid : played ? (hover && !off ? Icons.Refresh : Icons.Check) : ZunePlayerLayout.PlaySolid;
            string playName = Loc.Get(playing ? Strings.Podcast.Pages.Pause : vm.State == PodcastRowState.InProgress ? Strings.Podcast.Pages.Resume
                : played ? Strings.Podcast.Pages.PlayAgain : Strings.Podcast.Pages.Play);
            BoxEl play = new()
            {
                Key = "row:play", Width = pb, Height = pb, Shrink = 0f, AlignSelf = FlexAlign.Start, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                // An episode that cannot be played: the button is off (the disabled glyph, no plate, no click); the meta's lock tag says why.
                Fill = ColorF.Transparent, HoverFill = off ? ColorF.Transparent : Prop.Of(static () => Tok.TextPrimary with { A = Z.ButtonHoverAlpha }),
                PressedFill = off ? ColorF.Transparent : Prop.Of(static () => Tok.TextPrimary with { A = Z.ButtonPressedAlpha }),
                Role = AutomationRole.Button, Focusable = !off, Cursor = off ? CursorId.Arrow : CursorId.Hand, AllowFocusOnInteraction = false,
                OnClick = off ? null : _play,
                Children = [new TextEl(glyph)
                {
                    Size = Z.RowGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = off ? Tok.TextDisabled
                        : playing || vm.State == PodcastRowState.InProgress ? Prop.Of(static () => R.Ink.AccentDecor) : played && !hover ? (Prop<ColorF>)Tok.TextTertiary : Tok.TextPrimary,
                }],
            };
            var line = new BoxEl
            {
                Direction = 0, Gap = Z.RowGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    Controls.Named(play, playName),
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = text.ToArray() },
                    new BoxEl { Shrink = 0f, AlignSelf = FlexAlign.Start, Children = [More("row:more", _menu, act, ring: false)] },
                ],
            };
            var kids = new List<Element>(2) { line };
            if (expandable)
                kids.Add(new BoxEl
                {
                    Key = "row:x", Direction = 1, ClipToBounds = true, Height = expanded ? float.NaN : 0f, Animate = ZuneDetailKit.GrowShove,
                    Padding = new Edges4(pb + Z.RowGap, 0f, act, 0f),
                    Children = expanded ? [ExpandBody(vm, _open, _next, p.Compact)] : [],
                });
            BoxEl row = new()
            {
                Direction = 1, MinWidth = 0f, Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, AllowFocusOnInteraction = false,
                Padding = new Edges4(0f, Z.RowPadY, 0f, Z.RowPadY),
                Fill = ColorF.Transparent, HoverFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }),
                PressedFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowPressedAlpha }), BrushTransitionMs = Design.Motion.Faster,
                OnClick = _click, OnHoverMove = _move, OnPointerExit = _exit, OnKeyDown = _key, Draggable = _drag,
                Children = kids.ToArray(),
            };
            if (!Controls.IsNullOverlay(overlay)) row = ContextSheets.Attach(row, overlay, _menu);
            return row;
        }

        void OnKey(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            _p.Verbs.Play(_p.Vm.Id);
            e.Handled = true;
        }
    }

    /// <summary>The row's meta: the square accent "new", the date, the length or the accent time left, "played", the video word.</summary>
    public static Element RowMeta(PodcastEpisodeVm vm)
    {
        var parts = new List<Element>(6);
        if (vm.State == PodcastRowState.New)
            parts.Add(new BoxEl
            {
                Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    new BoxEl { Width = Z.NewDot, Height = Z.NewDot, Fill = Prop.Of(static () => Tok.AccentDefault) },
                    MetaWord(Loc.Get(Strings.Podcast.Pages.Fresh)) with { Color = Prop.Of(static () => R.Ink.AccentDecor), Weight = Z.Strong },
                ],
            });
        // The tags lead the words (after "new"): the line runs off under its right-edge fade (nothing in it shrinks), so a tag at its start is
        // never the part that fades.
        if (vm.Lock.Length > 0) parts.Add(Tag(vm.Lock, Icons.Lock));
        if (vm.Date.Length > 0) parts.Add(MetaWord(vm.Date) with { Color = Tok.TextTertiary });
        if (vm.State == PodcastRowState.InProgress && vm.Left.Length > 0) parts.Add(MetaWord(vm.Left) with { Color = Prop.Of(static () => R.Ink.AccentDecor) });
        else if (vm.Length.Length > 0) parts.Add(MetaWord(vm.Length) with { Color = Tok.TextTertiary });
        if (vm.State == PodcastRowState.Played) parts.Add(MetaWord(Loc.Get(Strings.Podcast.Pages.Played)) with { Color = Tok.TextTertiary });
        if (vm.Video) parts.Add(MetaWord(Loc.Get(Strings.Podcast.Pages.Video)) with { Color = Tok.TextTertiary });
        return Meta("row:meta", parts);
    }

    /// <summary>A row tag at the head of the meta (the lock): the glyph and the word in the tertiary ink, lowercased as a label.</summary>
    static Element Tag(string word, string? glyph)
    {
        var kids = new List<Element>(2);
        if (glyph is not null) kids.Add(Icon(glyph, Z.MetaStar, Tok.TextTertiary));
        kids.Add(MetaWord(word) with { Color = Tok.TextTertiary });
        return new BoxEl { Direction = 0, Gap = Z.MetaGap / 2f, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
    }

    /// <summary>The blocked state of an episode that cannot be played, in place of its play button and as tall: the lock and why, in the secondary
    /// ink (a sentence: written as the host wrote it).</summary>
    public static Element Blocked(string key, string why, float height) => new BoxEl
    {
        Key = key, Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, MinHeight = height, MinWidth = 0f, Shrink = 1f,
        Children =
        [
            Icon(Icons.Lock, Z.RowGlyph, Tok.TextSecondary),
            new TextEl(Glue(why)) { Size = Z.ButtonTextSize, LineHeight = Z.ButtonTextLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>The expanded row: Open episode at its top (the verb's one home), then the description through the house renderer, then Play next.</summary>
    static Element ExpandBody(PodcastEpisodeVm vm, Action open, Action next, bool compact)
    {
        float h = compact ? Z.RowActionCompact : Z.RowAction;
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 0, Gap = Z.CommandGap, Wrap = true,
                Children =
                [
                    Button("row:open", Loc.Get(Strings.Podcast.Pages.OpenEpisode), Icons.OpenInNewWindow, open, h),
                    Button("row:next", Loc.Get(Strings.Podcast.Pages.PlayNext), null, next, h) with
                    {
                        Children = [PlayNextIcon(Z.ButtonGlyph, Tok.TextPrimary), new TextEl(Loc.Get(Strings.Podcast.Pages.PlayNext))
                        {
                            Size = Z.ButtonTextSize, LineHeight = Z.ButtonTextLine, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                        }],
                    },
                ],
            },
        };
        if (vm.Description.Length > 0 || vm.DescriptionHtml.Length > 0)
            kids.Add(Description(vm.DescriptionHtml.Length > 0 ? vm.DescriptionHtml : vm.Description, 0, Z.BodySize));
        return new BoxEl { Direction = 1, Gap = Z.ExpandGap, Padding = new Edges4(0f, Z.ExpandTop, 0f, 0f), MinWidth = 0f, Children = kids.ToArray() };
    }

    // ══ 7. THE CARDS ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"More like this" on the house media surface: shelf cards (hover plate, play, pressed, focus, the menu, the hand cursor), or the house rows
    /// where <paramref name="rows"/>.</summary>
    public static Element Cards(string key, ImmutableArray<PodcastCardVm> cards, Action<string> open, bool rows, Func<string, ContextMenuModel?>? menu,
        Action<string>? play)
    {
        var kids = new Element[cards.Length];
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            string uri = c.Uri;
            var data = new Controls.CardData(uri, c.Title, c.Subtitle.Length > 0 ? R.Type.TrackMeta(c.Subtitle) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis } : null,
                c.ArtUrl, () => open(uri), play is null ? null : () => play(uri), ShowMenu: menu is not null)
            {
                Menu = menu is null ? null : () => menu(uri),
            };
            kids[i] = new BoxEl
            {
                Key = "card:" + uri, Direction = 1, MinWidth = 0f, Width = rows ? float.NaN : Z.CardW, Shrink = 0f,
                Children = [rows ? Controls.Surface(data, Shape.Row()) : Controls.Surface(data, Shape.Shelf(), Z.CardW)],
            };
        }
        return rows
            ? new BoxEl { Key = key, Direction = 1, MinWidth = 0f, Children = kids }
            : new BoxEl { Key = key, Direction = 0, Wrap = true, Gap = Z.CardGap, MinWidth = 0f, Children = kids };
    }

    /// <summary>A fact list (label · value), the labels lowercase and tertiary.</summary>
    public static Element Facts(string key, ImmutableArray<PodcastFact> facts)
    {
        var rows = new Element[facts.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = Z.FactsColGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = Z.FactLabelW, Shrink = 0f, Children = [R.Type.TrackTitle(facts[i].Label) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) }] },
                    new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [R.Type.TrackTitle(facts[i].Value) with { Wrap = TextWrap.Wrap, MinWidth = 0f }] },
                ],
            };
        return new BoxEl { Key = key, Direction = 1, Gap = Z.FactsRowGap, MinWidth = 0f, Children = rows };
    }

    /// <summary>A section: its heading (and an optional trailing link) over its body.</summary>
    public static Element Section(string key, string title, Element body, Element? trailing = null) => new BoxEl
    {
        Key = key, Direction = 1, Gap = Z.SectionHeadGap, MinWidth = 0f,
        Children =
        [
            trailing is null
                ? Heading(title)
                : new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionHeadGap, MinWidth = 0f, Children = [Heading(title), new BoxEl { Grow = 1f }, trailing.Value] },
            body,
        ],
    };
}
