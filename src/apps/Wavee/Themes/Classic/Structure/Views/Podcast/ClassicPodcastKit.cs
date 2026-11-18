// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastKit.cs — the parts Classic's podcast pages v2 share (show, episode, audiobook) ───────────────
//
// Concept `winui-podcast-v2.html` (theme=classic) "Classic: the cover column": every page is an album page.
//
//   Page(...)        the cover-tone ground under the page; from Small up a row of [the cover column (the rail's layer fill, its own scroller, as Classic's
//                    detail rail) | the list column (the page's scroller)]; at Compact one scroller, the column stacked over the list. The two forms are a
//                    declared swap (`ClassicTierMotion.SwapFade`).
//   Kind / Title / Sub / Line   the column's words: the kind line (a link where it has a hub) with its tags, the 28/36 title (22/28 for an episode),
//                    the 14/20 secondary sub, the 12/18 lines.
//   Ledger(...)      the listened bar: played, in progress, to go, new, each a segment drawn to its count, and its words under it.
//   Primary(...)     the full-width button in the cover's own colour (the page accent, Classic's artwork policy), its menu on the chevron.
//   PinnedBar(...)   a row that pins in the list column (the list bar, a group label): nothing painted at rest; while pinned it takes the page ground
//                    and a hairline (owner 2026-11-18: no bands at rest).
//   EpisodeRow(...)  the row: its words (title semibold while unplayed, the meta line, one line of description), the saved heart at rest, the ⋯ always,
//                    the play button at its end (the ring while in progress, a check once played); a click expands it in place (Open episode and Play
//                    next at the top, the description under them) or, at Compact, opens the episode; right-click opens its menu.
//   SectionHeader / MoreLikeThis   the 20/28 section head with its link; the house media surface (`Controls.Surface`: hover, pressed, focus, menu,
//                    drag, the hand) as shelf cards or, narrow, the house rows.
//
// No numbers here: `ClassicPodcastRules` and the tier record `ClassicPodcastLayout`. No Entities / Playback: every verb comes in as a delegate.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

/// <summary>The verbs and live cells a <see cref="ClassicPodcastKit.EpisodeRow"/> reads. Build ONE per page (rows compare it by reference).</summary>
internal sealed class ClassicRowVerbs
{
    public required IReadSignal<string?> Expanded { get; init; }
    public required IReadSignal<string?> Playing { get; init; }
    public required Action<string> Play { get; init; }
    public required Action<string> Open { get; init; }
    public required Action<string> ToggleSaved { get; init; }
    public required Action<string> PlayNext { get; init; }
    public required Action<string?> Expand { get; init; }
    public required Func<string, ContextMenuModel?> Menu { get; init; }
    /// <summary>A row's drag payload (the page props' <c>RowDrag</c>; null: the rows carry no drag).</summary>
    public Func<string, object?>? Drag { get; init; }
}

/// <summary>Classic's podcast kit (see the file header).</summary>
internal static class ClassicPodcastKit
{
    // ══ 1. THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page accent: the cover's own colour lifted to a chrome accent with Classic's contrast floor, or the system accent when the structure's
    /// "Accent from artwork" is off or the cover gave none.</summary>
    public static ColorF Accent(uint tone)
        => tone == 0u || !ClassicOptions.AccentFromArtwork() ? Tok.AccentDefault : Design.Palette.Floor(Design.Palette.ChromeFromPayload(tone));

    /// <summary>The ground the page stands on: the cover's hue sunk to the dark background (dark), a whisper of it (light); transparent without one.</summary>
    public static ColorF Ground(uint tone)
    {
        if (tone == 0u) return ColorF.Transparent;
        return R.Kind == ThemeKind.Light
            ? ColorScience.ToColor(tone) with { A = P.GroundLight }
            : ColorScience.DarkFromPayload(tone) with { A = P.GroundDark };
    }

    /// <summary>The page (see the file header). <paramref name="column"/> is the cover column's content (no padding), <paramref name="main"/> the list
    /// column's (no padding). <paramref name="scroll"/> is the list scroller's handle (at Compact: the one scroller's). ONE row at every tier: the list
    /// scroller keeps its place (and its scroll) across a crossing; at Compact the column frame is empty and hidden and the column stands at the top of
    /// the list scroller instead (a declared swap).</summary>
    public static Element Page(string key, ClassicPodcastLayout lay, uint tone, Element column, Element main, ScrollHandle? scroll = null,
        Action<NodeHandle>? viewport = null)
    {
        Element mainBox = new BoxEl
        {
            Key = "cp-main", Direction = 1, MinWidth = 0f,
            Padding = new Edges4(lay.MainPad, 0f, lay.MainPad, P.MainFoot + R.Space.DockReserve), Children = [main],
        };
        Element body = lay.Stacked
            ? new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "cp-column-stacked", Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Fill = Tok.FillLayerDefault, Padding = Edges4.All(P.ColumnPadStacked),
                        Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Children = [column],
                    },
                    mainBox,
                ],
            }
            : new BoxEl { Direction = 1, MinWidth = 0f, Children = [mainBox] };
        return ListPage(key, lay, tone, lay.Stacked ? null : new BoxEl
            {
                Key = "cp-column-wide", Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade,
                Children = [column],
            },
            ScrollView(body) with
            {
                Key = "cp-scroll:" + key, ScrollKey = "cp-scroll:" + key, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Handle = scroll,
                EdgeCues = ScrollEdgeCues.None, OnRealized = viewport,
            });
    }

    /// <summary>The show page's form of <see cref="Page"/>: the list column is ONE virtualised list (the page's scroller at every tier). From Small up the
    /// cover column stands beside it in its frame; at Compact the frame is empty and hidden and the list's first item carries the column. One row at
    /// every tier, so a crossing never re-parents the list (its scroll and its mounted rows survive).</summary>
    public static Element ListPage(string key, ClassicPodcastLayout lay, uint tone, Element? column, Element list)
        => new BoxEl
        {
            Key = "cp-page:" + key, Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Fill = Ground(tone), AlignItems = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Key = "cp-column", Direction = 1, Width = lay.Stacked ? 0f : lay.ColumnW, Visible = !lay.Stacked, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                    Fill = Tok.FillLayerDefault,
                    Children = column is null ? [] :
                    [
                        ScrollView(new BoxEl
                        {
                            Direction = 1, Gap = P.ColumnGap, MinWidth = 0f,
                            Padding = new Edges4(P.ColumnPadStart, P.ColumnPadTop, P.ColumnPadEnd, P.ColumnPadBottom + R.Space.DockReserve),
                            Children = [column],
                        }) with { Key = "cp-column-scroll:" + key, Grow = 1f, MinHeight = 0f, EdgeCues = ScrollEdgeCues.None },
                    ],
                },
                list,
            ],
        };

    // ══ 2. THE COLUMN'S WORDS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The kind line: the kind word (the house hyperlink to its hub when <paramref name="open"/> is set), then the tags (Video, Serial, Included in Premium).</summary>
    public static Element Kind(string kind, Action? open, params Element[] tags)
    {
        var kids = new List<Element>(tags.Length + 1);
        if (kind.Length > 0)
            kids.Add(open is not null
                // The house hyperlink (rest, hover, pressed, focus), its own side padding pulled back so the word stands on the column edge.
                ? HyperlinkButton.Create(kind, open, size: ControlSize.Small) with { Key = "cp-kind", Shrink = 0f, Margin = new Edges4(-P.LinkPadX, 0f, -P.LinkPadX, 0f) }
                : Caption(kind) with { Weight = P.Strong, Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f });
        kids.AddRange(tags);
        return new BoxEl { Direction = 0, Wrap = true, Gap = P.KindGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The blocked state of an episode that cannot be played, in place of its play button and as tall: the lock and why, in the secondary
    /// ink.</summary>
    public static Element Blocked(string key, string why, float height) => new BoxEl
    {
        Key = key, Direction = 0, Gap = P.TagGap + P.TagGap, AlignItems = FlexAlign.Center, MinHeight = height, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Lock, P.PlayGlyph, Tok.TextSecondary),
            new TextEl(why) { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>A tag of the kind line (Video, Serial, Included in Premium): a 1-DIP outline, 11/14 semibold.</summary>
    public static Element Tag(string word, string? glyph, ColorF? ink = null)
    {
        ColorF c = ink ?? Tok.TextSecondary;
        var kids = new List<Element>(2);
        if (glyph is not null) kids.Add(Icon(glyph, P.TagGlyph, c));
        kids.Add(new TextEl(word) { Size = P.TagSize, LineHeight = P.TagLine, Weight = P.Strong, Color = c, Wrap = TextWrap.NoWrap });
        return new BoxEl
        {
            Direction = 0, Gap = P.TagGap, Height = P.TagH, AlignItems = FlexAlign.Center, Padding = new Edges4(P.TagPadX, 0f, P.TagPadX, 0f), Shrink = 0f,
            Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = c with { A = c.A * P.TagStrokeAlpha }, Children = kids.ToArray(),
        };
    }

    /// <summary>The column's title: 28/36 semibold (22/28 for an episode), wrapping.</summary>
    public static TextEl Title(string text, bool small = false) => new(text)
    {
        Size = small ? P.TitleSmallSize : P.TitleSize, LineHeight = small ? P.TitleSmallLine : P.TitleLine, Weight = P.Strong, Color = Tok.TextPrimary,
        Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    /// <summary>The 14/20 secondary line under the title (the publisher, the subtitle).</summary>
    public static TextEl Sub(string text) => new(text)
    {
        Size = P.SubSize, LineHeight = P.SubLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    /// <summary>A 12/18 line of runs (the rating, the counts, the credits).</summary>
    public static SpanTextEl Line(params TextSpan[] spans) => new(spans)
    {
        Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    /// <summary>Words as links ("Science, History"), wrapping: each the house hyperlink (rest, hover, pressed, focus; the kind word's look) that opens
    /// <paramref name="open"/> with its words, its comma glued after it (no line starts on one). Each word's hit rect reaches
    /// <paramref name="target"/> (its slop: the type keeps its size).</summary>
    public static Element Links(string key, ImmutableArray<string> words, Action<string> open, float target)
    {
        float slop = MathF.Max(0f, (target - P.LineHeight) / 2f);
        var units = new Element[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            var link = HyperlinkButton.Create(word, () => open(word), size: ControlSize.Small) with
            {
                // No side padding: the comma after it and the next word sit where the text would, nothing overlaps.
                Key = key + ":" + i, Shrink = 0f, Padding = default, HitSlop = new Edges4(0f, slop, 0f, slop),
            };
            units[i] = i + 1 < words.Length
                ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [link, new TextEl(",") { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextSecondary }] }
                : link;
        }
        return new BoxEl { Key = key, Direction = 0, Wrap = true, Gap = P.TagGap + P.TagGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = units };
    }

    /// <summary>A strong run of a <see cref="Line"/>.</summary>
    public static TextSpan Strong(string text, Action? click = null)
        => new(text, Weight: P.Strong, Color: Tok.TextPrimary, OnClick: click, IsLink: click is not null);

    /// <summary>A description through the house rich-text renderer (paragraphs, lists, bold, links), 12/18, with the house "Read more" past
    /// <paramref name="lines"/> lines (0 = all, no fold).</summary>
    public static Element Description(string html, string key, int lines = P.DescLines, float size = P.DescSize)
        => new BoxEl
        {
            Key = key, Direction = 1, MinWidth = 0f,
            Children = [lines > 0
                ? Controls.ExpandableRichTextFlex(html, size, Tok.TextSecondary, Tok.AccentTextPrimary, lines, key, DetailVerbs.NavigateKey)
                : Controls.RichTextFlex(html, size, Tok.TextSecondary, Tok.AccentTextPrimary, 0, DetailVerbs.NavigateKey)],
        };

    /// <summary>A label / value list (Topics, Order, Released): 12/18, the label tertiary. <paramref name="values"/>[i], when set, draws row i's value
    /// instead of its text (the topics as links).</summary>
    public static Element Facts(ImmutableArray<PodcastFact> facts, string key, Element?[]? values = null)
    {
        var rows = new Element[facts.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = P.FactsColGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = P.FactLabelW, Shrink = 0f, Children = [new TextEl(facts[i].Label) { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextTertiary }] },
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [values is not null && i < values.Length && values[i] is { } own ? own
                            : new TextEl(facts[i].Value) { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                    },
                ],
            };
        return new BoxEl { Key = key, Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = rows };
    }

    // ══ 3. THE LISTENED BAR ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The listened bar: a 4-DIP band of segments drawn to their counts (played, in progress, to go, new) and its words.</summary>
    public static Element Ledger(PodcastLedger l, ColorF accent)
    {
        int all = l.Played + l.InProgress + l.ToGo + l.New;
        var segs = new List<Element>(4);
        void Seg(int n, ColorF c) { if (n > 0) segs.Add(new BoxEl { Grow = n, Basis = 0f, MinWidth = P.LedgerMin, Fill = c }); }
        Seg(l.Played, Tok.TextPrimary with { A = P.LedgerPlayedAlpha });
        Seg(l.InProgress, accent);
        Seg(l.ToGo, Tok.TextPrimary with { A = P.LedgerTrackAlpha });
        Seg(l.New, accent);
        if (all == 0) segs.Add(new BoxEl { Grow = 1f, Fill = Tok.TextPrimary with { A = P.LedgerTrackAlpha } });
        var spans = l.NewText.Length > 0
            ? new[] { new TextSpan(l.Text + P.Dot), new TextSpan(l.NewText, Weight: P.Strong, Color: Design.Palette.ReadableInk(accent)) }
            : new[] { new TextSpan(l.Text) };
        return new BoxEl
        {
            Key = "cp-ledger", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = P.LedgerGap, Height = P.LedgerH, Corners = CornerRadius4.All(P.LedgerRadius), ClipToBounds = true, MinWidth = 0f, Children = segs.ToArray() },
                Line(spans),
            ],
        };
    }

    // ══ 4. THE PRIMARY ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The full-width primary in the page accent: <paramref name="label"/> with its glyph on the main half; the chevron opens
    /// <paramref name="menu"/> (none: no chevron). <paramref name="accent"/> false: a standard button (caught up, played).</summary>
    public static Element Primary(string key, string label, string glyph, ColorF accentFill, bool accent, Action play, ImmutableArray<PodcastMenuItem> menu,
        float height)
        => Embed.Comp(new SplitProps(label, glyph, accentFill, accent, play, menu, height), static () => new SplitHost()) with { Key = key };

    sealed record SplitProps(string Label, string Glyph, ColorF Fill, bool Accent, Action Play, ImmutableArray<PodcastMenuItem> Menu, float Height)
    {
        public bool Equals(SplitProps? o) => o is not null && Label == o.Label && Glyph == o.Glyph && Fill.Equals(o.Fill) && Accent == o.Accent
            && Height == o.Height && Menu.Length == o.Menu.Length && (Menu.IsDefaultOrEmpty || Menu[0].Label == o.Menu[0].Label && Menu[0].Checked == o.Menu[0].Checked);
        public override int GetHashCode() => HashCode.Combine(Label, Glyph, Accent, Height, Menu.Length);
    }

    sealed class SplitHost : Component, IPropsHost
    {
        SplitProps? _p;
        readonly Signal<SplitProps?> _props = new(null);
        readonly Signal<bool> _open = new(false);
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
            bool open = _open.Value;
            float h = p.Height, r = Radii.Control;
            bool chevron = !p.Menu.IsDefaultOrEmpty;
            BoxEl main;
            BoxEl? menuHalf = null;
            Element? divider = null;
            if (p.Accent)
            {
                ColorF fill = p.Fill;
                ColorF ink = ColorContrast.PickContrast(fill);
                var palette = Controls.AccentFillPalette(fill);
                main = Controls.PrimaryButton(p.Label, _play, fill, p.Glyph) with
                {
                    Key = "split:play", Height = h, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Start,
                    Corners = chevron ? new CornerRadius4(r, 0f, 0f, r) : Radii.ControlAll,
                    BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
                };
                if (chevron)
                {
                    menuHalf = Button.Create("", _toggle, ButtonAppearance.Accent, style: Button.DefaultStyle(ButtonAppearance.Accent, palette: palette) with { MinHeight = h }) with
                    {
                        Key = "split:menu", Width = MathF.Max(P.SplitMenuW, h), Height = h, MinWidth = 0f, Shrink = 0f, Padding = default, Gap = 0f,
                        Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Corners = new CornerRadius4(0f, r, r, 0f),
                        BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
                        Fill = open ? palette.Background.Pressed : fill,
                        Children = [Icon(Icons.ChevronDown, P.SplitChevron, ink with { A = P.SplitChevronInk })],
                    };
                    divider = new BoxEl
                    {
                        Width = P.SplitDivider, Height = h, Shrink = 0f, Fill = fill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [new BoxEl { Width = P.SplitDivider, Height = P.SplitDividerH, Fill = ink with { A = P.SplitDividerInk } }],
                    };
                }
            }
            else
            {
                main = Button.Create(p.Label, _play, ButtonAppearance.Standard, glyph: p.Glyph) with
                {
                    Key = "split:play", Height = h, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Start,
                    Corners = chevron ? new CornerRadius4(r, 0f, 0f, r) : Radii.ControlAll,
                };
                if (chevron)
                    menuHalf = Button.Create("", _toggle, ButtonAppearance.Standard) with
                    {
                        Key = "split:menu", Width = MathF.Max(P.SplitMenuW, h), Height = h, MinWidth = 0f, Shrink = 0f, Padding = default, Gap = 0f,
                        Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Corners = new CornerRadius4(0f, r, r, 0f),
                        Margin = new Edges4(P.SplitDivider, 0f, 0f, 0f),
                        Children = [Icon(Icons.ChevronDown, P.SplitChevron, Tok.TextPrimary)],
                    };
            }
            var kids = new List<Element>(3) { main };
            if (divider is not null) kids.Add(divider);
            if (menuHalf is not null) kids.Add(ToolTip.Wrap(menuHalf, Loc.Get(Strings.Podcast.Pages.OtherWays)));
            return new BoxEl
            {
                ZStack = true, Height = h, MinWidth = 0f, OnRealized = _capture,
                Children =
                [
                    new BoxEl { Key = "split:halves", Direction = 0, AlignItems = FlexAlign.Center, Height = h, Corners = Radii.ControlAll, ClipToBounds = true, MinWidth = 0f, Children = kids.ToArray() },
                    new BoxEl
                    {
                        Key = "split:border", HitTestVisible = false, Corners = Radii.ControlAll, BorderWidth = p.Accent ? 1f : 0f,
                        BorderBrush = p.Accent ? Tok.AccentControlElevationBorder : null,
                    },
                ],
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
            opened.ClosedAction = () =>
            {
                if (!ReferenceEquals(_menu, opened)) return;
                _menu = null;
                _open.Value = false;
            };
            _open.Value = true;
        }
    }

    /// <summary>"What it plays" under the primary: <paramref name="why"/> · <paramref name="title"/> (strong), then a small bar and <paramref name="sub"/>.</summary>
    public static Element UpLabel(string why, string title, string sub, float progress, ColorF accent)
    {
        var line = why.Length > 0
            ? new SpanTextEl(new TextSpans([new TextSpan(why + P.Dot, Color: Tok.TextSecondary), new TextSpan(title, Weight: P.Strong, Color: Tok.TextPrimary)], 2))
                { Size = P.SubSize, LineHeight = P.SubLine, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : new SpanTextEl(new TextSpans([new TextSpan(title, Weight: P.Strong, Color: Tok.TextPrimary)], 1))
                { Size = P.SubSize, LineHeight = P.SubLine, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        var sub2 = new List<Element>(2);
        if (progress >= 0f) sub2.Add(MiniBar(progress, accent, P.UpBarW));
        if (sub.Length > 0)
            sub2.Add(new TextEl(sub)
            {
                // Beside a bar the words take one line; alone (a caught-up or finished line) they wrap to two rather than being cut.
                Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextSecondary, Wrap = progress >= 0f ? TextWrap.NoWrap : TextWrap.Wrap,
                MaxLines = progress >= 0f ? 1 : 2, Trim = TextTrim.CharacterEllipsis, Grow = progress >= 0f ? 0f : 1f, Shrink = 1f, MinWidth = 0f,
            });
        return new BoxEl
        {
            Key = "cp-up-label", Direction = 1, MinWidth = 0f,
            Children = [line, new BoxEl { Direction = 0, Gap = P.UpSubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub2.ToArray() }],
        };
    }

    /// <summary>A small bar: a 3-DIP fill on a 1-DIP track, <paramref name="value"/> 0..1 (static).</summary>
    public static Element MiniBar(float value, ColorF accent, float width) => new BoxEl
    {
        ZStack = true, Width = width, Height = P.MiniBarH, Shrink = 0f,
        Children =
        [
            new BoxEl { Height = P.MiniBarTrack, AlignSelf = FlexAlign.Center, Fill = Tok.StrokeControlStrongDefault },
            new BoxEl { Width = MathF.Max(P.MiniBarH, Math.Clamp(value, 0f, 1f) * width), Height = P.MiniBarH, JustifySelf = FlexAlign.Start, Corners = CornerRadius4.All(P.MiniBarRadius), Fill = accent },
        ],
    };

    /// <summary>A live bar (the fill follows <paramref name="value"/>, 0..1, through a compositor scale).</summary>
    public static BoxEl LiveBar(Func<float> value, ColorF accent) => new()
    {
        Height = P.MiniBarH, ZStack = true, MinWidth = 0f,
        Children =
        [
            new BoxEl { Height = P.MiniBarTrack, AlignSelf = FlexAlign.Center, Fill = Tok.StrokeControlStrongDefault },
            new BoxEl
            {
                Height = P.MiniBarH, Corners = CornerRadius4.All(P.MiniBarRadius), Fill = accent, TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Math.Clamp(value(), 0f, 1f)), 1f)),
            },
        ],
    };

    /// <summary>A bordered 32 (40 at Compact) glyph button named by its tooltip.</summary>
    public static Element Glyph(string key, string glyph, string name, Action? click, float size)
        => Controls.Named(Controls.IconAction(glyph, click, size: size, isEnabled: click is not null) with
        {
            Key = key, Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        }, name);

    /// <summary>The app's play-next glyph (<c>WaveeIcons.PlayNext</c>, the menus' and the queue's), never <c>Icons.Queue</c>.</summary>
    public static Element PlayNextIcon(float size, ColorF color) => Icon(WaveeIcons.PlayNext, size, color, WaveeIcons.Font);

    /// <summary>"Play next" as a standard button with the app's play-next glyph.</summary>
    public static Element PlayNextButton(Action next, string key)
    {
        // The house button with a glyph (its glyph slot and gap), the glyph swapped for the app's play-next one.
        var b = Button.Create(Loc.Get(Strings.Podcast.Pages.PlayNext), next, glyph: Icons.Play);
        var kids = b.Children.ToArray();
        if (kids.Length > 0) kids[0] = PlayNextIcon(P.PlayGlyph, Tok.TextPrimary);
        return b with { Key = key, Children = kids };
    }

    // ══ 5. A PINNED ROW ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A row that pins <paramref name="top"/> below the list scroller's edge: nothing painted at rest; while pinned (<paramref name="stuck"/>) the
    /// page ground behind it (<paramref name="tone"/>, read live) and a hairline at its foot, so what scrolls under it is covered by the colour it scrolls
    /// over. The row must be the sticky root itself (a sticky root returned from a component never pins).</summary>
    public static BoxEl PinnedBar(string key, Element content, float height, float top, Signal<bool> stuck, Func<uint> tone, string? scope = null)
        => new BoxEl
        {
            Key = key, ZStack = true, Direction = 1, Height = height, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = key + ":back", HitTestVisible = false, Fill = Prop.Of(() => stuck.Value ? Backing(tone()) : ColorF.Transparent),
                },
                content,
                new BoxEl
                {
                    Key = key + ":line", AlignSelf = FlexAlign.End, Height = 1f, MinWidth = 0f, HitTestVisible = false,
                    Fill = Tok.StrokeDividerDefault, Opacity = Prop.Of(() => stuck.Value ? 1f : 0f),
                },
            ],
        }.Sticky(top, scope, stuck);

    /// <summary>A pinned row's backing: the page ground over the shell's content surface, opaque.</summary>
    public static ColorF Backing(uint tone) => Flatten(Ground(tone));

    /// <summary>The ground over the shell's content surface, opaque (a pinned row's backing must hide what scrolls under it).</summary>
    static ColorF Flatten(ColorF ground) => ColorContrast.Flatten(ground, R.Layer.ContentSurface);

    // ══ 6. THE EPISODE ROW ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One episode row (see the file header), keyed <c>ep:</c> + the id.</summary>
    public static Element EpisodeRow(PodcastEpisodeVm vm, ClassicPodcastLayout lay, bool expands, bool description, ColorF accent, ClassicRowVerbs verbs)
        => Embed.Comp(new RowProps(vm, lay.Stacked, expands, description, accent, verbs), static () => new RowHost()) with { Key = "ep:" + vm.Id };

    sealed record RowProps(PodcastEpisodeVm Vm, bool Compact, bool Expands, bool Description, ColorF Accent, ClassicRowVerbs Verbs)
    {
        public bool Equals(RowProps? o) => o is not null && Vm.Equals(o.Vm) && Compact == o.Compact && Expands == o.Expands && Description == o.Description
            && Accent.Equals(o.Accent) && ReferenceEquals(Verbs, o.Verbs);
        public override int GetHashCode() => HashCode.Combine(Vm.Id, Compact, Expands, Description);
    }

    sealed class RowHost : Component
    {
        readonly Signal<bool> _hover = new(false);
        RowProps _p = null!;
        Action? _click, _play, _save, _next, _open, _exit;
        Action<Point2>? _move;
        Action<KeyEventArgs>? _key;
        Func<ContextMenuModel?>? _menu;
        DragSource? _drag;

        public override Element Render()
        {
            _p = UseProps<RowProps>();
            // The row drags as its episode (onto a playlist, the queue, the player bar): ONE source per row, the payload built when the drag promotes.
            if (_drag is null && _p.Verbs.Drag is not null) _drag = Wavee.Drag.Source(() => _p.Verbs.Drag?.Invoke(_p.Vm.Id));
            _click ??= () => { if (_p.Expands) _p.Verbs.Expand(_p.Vm.Id); else _p.Verbs.Open(_p.Vm.Id); };
            _play ??= () => _p.Verbs.Play(_p.Vm.Id);
            _save ??= () => _p.Verbs.ToggleSaved(_p.Vm.Id);
            _next ??= () => _p.Verbs.PlayNext(_p.Vm.Id);
            _open ??= () => _p.Verbs.Open(_p.Vm.Id);
            _move ??= _ => _hover.SetIfChanged(true);
            _exit ??= () => _hover.SetIfChanged(false);
            _menu ??= () => _p.Verbs.Menu(_p.Vm.Id);
            _key ??= e =>
            {
                if (e.KeyCode != Keys.Enter) return;
                _p.Verbs.Play(_p.Vm.Id);
                e.Handled = true;
            };
            var overlay = UseContext(Overlay.Service);
            var p = _p;
            var vm = p.Vm;
            var v = p.Verbs;
            string id = vm.Id;
            bool expanded = p.Expands && v.Expanded.Value == id;
            bool playing = v.Playing.Value == id;
            bool played = vm.State == PodcastRowState.Played;
            bool hover = _hover.Value;
            float pb = p.Compact ? P.PlayButtonCompact : P.PlayButton;
            float act = p.Compact ? P.RowActionCompact : P.RowAction;

            var title = Body(vm.Title) with
            {
                MaxLines = p.Compact ? 2 : 1, Trim = TextTrim.CharacterEllipsis, Wrap = p.Compact ? TextWrap.Wrap : TextWrap.NoWrap, MinWidth = 0f,
                Weight = played ? (ushort)400 : P.Strong, Color = played ? Tok.TextSecondary : Tok.TextPrimary,
            };
            var text = new List<Element>(3) { title, MetaLine(vm, accent: p.Accent) };
            if (p.Description && vm.Description.Length > 0 && !expanded)
                text.Add(new TextEl(vm.Description)
                {
                    Size = P.LineSize, LineHeight = P.LineHeight, MaxLines = P.RowDescLines, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextSecondary, MinWidth = 0f,
                });

            bool showSave = vm.Saved || hover || expanded || p.Compact;
            // The heart's slot is always reserved; the button itself is mounted only while it shows (saved at rest, hover, expanded, Compact), so a
            // freshly realised row in a fast scroll carries no hidden button.
            Element save = new BoxEl
            {
                Key = "row:save-slot", Width = act, Height = act, Shrink = 0f,
                Children = showSave ? [Controls.Named(Controls.IconAction(vm.Saved ? Icons.HeartFill : Icons.Heart, _save, size: act) with { Key = "row:save" },
                    Loc.Get(vm.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save))] : [],
            };
            Element more = ClassicDetailKit.MoreButton(_menu, act);
            Element line = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.RowColGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = text.ToArray() },
                    new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [save, more] },
                    PlayButton(vm, pb, playing, hover, p.Accent, _play),
                ],
            };
            var kids = new List<Element>(2) { line };
            if (p.Expands)
                kids.Add(new BoxEl
                {
                    Key = "row:x", Direction = 1, ClipToBounds = true, Height = expanded ? float.NaN : 0f, Animate = P.Expand, MinWidth = 0f,
                    Children = expanded ? [ExpandBody(vm, _open, _next)] : [],
                });

            // Keyed `ep` (one family): the row is the instance its verbs act on (the lint's subject: each row's More acts on its own episode, never
            // on the page's show), and the inspector's click and drag target.
            BoxEl row = new()
            {
                Key = "ep", Direction = 1, MinWidth = 0f, Corners = Radii.ControlAll, Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand,
                // The plate bleeds into the gutter: the row's words stand on the column line the list bar and the group labels stand on.
                Margin = p.Compact ? new Edges4(-P.RowPadCompact, 0f, -P.RowPadCompact, 0f) : new Edges4(-P.RowPadStart, 0f, -P.RowPadEnd, 0f),
                Padding = p.Compact ? Edges4.All(P.RowPadCompact) : new Edges4(P.RowPadStart, P.RowPadTop, P.RowPadEnd, P.RowPadBottom),
                Fill = expanded ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                OnClick = _click, OnHoverMove = _move, OnPointerExit = _exit, OnKeyDown = _key, Draggable = _drag,
                Children = kids.ToArray(),
            };
            return Controls.IsNullOverlay(overlay) ? row : ContextSheets.Attach(row, overlay!, _menu);
        }
    }

    /// <summary>The row's play button at its end: a circle with the ring while in progress, a check once played (replay on hover).</summary>
    static Element PlayButton(PodcastEpisodeVm vm, float size, bool playing, bool hover, ColorF accent, Action play)
    {
        bool prog = vm.State == PodcastRowState.InProgress;
        bool played = vm.State == PodcastRowState.Played;
        bool off = !vm.Playable;
        string glyph = playing ? Icons.Pause : played ? (hover && !off ? Icons.Refresh : Icons.Check) : Icons.Play;
        string name = Loc.Get(playing ? Strings.Podcast.Pages.Pause : prog ? Strings.Podcast.Pages.Resume : played ? Strings.Podcast.Pages.PlayAgain
            : Strings.Podcast.Pages.Play);
        var layers = new List<Element>(2);
        if (prog)
            layers.Add(new BoxEl
            {
                Width = size, Height = size, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, HitTestVisible = false,
                Children = [ProgressRing.Determinate(vm.Progress, size, foreground: accent, track: Tok.StrokeControlStrongDefault with { A = P.RingTrackAlpha })
                    with { Key = "ring:" + vm.Progress.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) }],
            });
        layers.Add(new BoxEl
        {
            Width = size, Height = size, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, HitTestVisible = false,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [Icon(glyph, P.PlayGlyph, off ? Tok.TextDisabled : played && !hover && !playing ? Tok.TextTertiary : Tok.TextPrimary)],
        });
        bool plate = !prog && !played;
        // An episode that cannot be played: the button is off (the disabled plate and glyph, no click); the meta's lock tag says why.
        ColorF fill = off ? (plate ? Tok.FillControlDisabled : Tok.FillSubtleTransparent) : plate ? Tok.FillControlDefault : Tok.FillSubtleTransparent;
        return Controls.Named(new BoxEl
        {
            Key = "row:play", ZStack = true, Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f),
            Fill = fill, HoverFill = off ? fill : Tok.FillControlSecondary, PressedFill = off ? fill : Tok.FillControlTertiary,
            BorderWidth = played ? 1f : 0f, BorderBrush = played ? Tok.ControlElevationBorder : null,
            Role = AutomationRole.Button, Focusable = !off, Cursor = off ? CursorId.Arrow : CursorId.Hand, OnClick = off ? null : play, Children = layers.ToArray(),
        }, name);
    }

    /// <summary>The meta line: New (a dot and the word in the accent), the date, the length or the accent time left, Played, the Video tag.</summary>
    public static Element MetaLine(PodcastEpisodeVm vm, bool showNew = true, ColorF? accent = null)
    {
        // ONE run of spans (a recycled row realises one text node, not a box per part): the separators ride inside the run, and the run never wraps,
        // so no line starts or ends on a "·". The Video tag is its own box only on a video episode.
        ColorF ink = Design.Palette.ReadableInk(accent ?? Tok.AccentDefault);
        var spans = new List<TextSpan>(8);
        void Part(string text, ColorF? color = null, ushort weight = 0)
        {
            if (text.Length == 0) return;
            if (spans.Count > 0) spans.Add(new TextSpan(P.Dot));
            spans.Add(color is { } c ? new TextSpan(text, Weight: weight, Color: c) : new TextSpan(text, Weight: weight));
        }
        if (showNew && vm.State == PodcastRowState.New) Part(P.NewMark + Loc.Get(Strings.Podcast.Pages.Fresh), ink, P.Strong);
        Part(vm.Date);
        if (vm.State == PodcastRowState.InProgress && vm.Left.Length > 0) Part(vm.Left, ink);
        else Part(vm.Length);
        if (vm.State == PodcastRowState.Played) Part(Loc.Get(Strings.Podcast.Pages.Played));
        var run = new SpanTextEl(spans.ToArray())
        {
            Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            Shrink = 1f, MinWidth = 0f,
        };
        // The tags (Video, the lock, the "E", Bonus, Trailer, the transcript mark) are boxes after the run: they keep their width, the run gives way
        // with an ellipsis.
        if (!vm.Video && vm.Lock.Length == 0 && !vm.Explicit && !vm.Bonus && !vm.Trailer && !vm.HasTranscript) return run;
        var kids = new List<Element>(7) { run };
        if (vm.Video) kids.Add(Tag(Loc.Get(Strings.Podcast.Pages.Video), Icons.Video, Tok.TextTertiary));
        if (vm.Lock.Length > 0) kids.Add(Tag(vm.Lock, Icons.Lock, Tok.TextTertiary));
        if (vm.Explicit) kids.Add(Controls.ExplicitBadge(P.TagH));
        if (vm.Bonus) kids.Add(Tag(Loc.Get(Strings.Podcast.Pages.Bonus), null, Tok.TextTertiary));
        if (vm.Trailer) kids.Add(Tag(Loc.Get(Strings.Podcast.Pages.Trailer), null, Tok.TextTertiary));
        if (vm.HasTranscript)
            kids.Add(Controls.Named(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Document, P.TagGlyph, Tok.TextTertiary)] }, Loc.Get(Strings.Podcast.Pages.TranscriptMark)));
        return new BoxEl { Direction = 0, Gap = P.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    static TextEl Meta(string text) => new(text) { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f, Wrap = TextWrap.NoWrap };

    /// <summary>The expanded row's body: Open episode and Play next at its top (one home for the verb), the description under them.</summary>
    static Element ExpandBody(PodcastEpisodeVm vm, Action open, Action next) => new BoxEl
    {
        Direction = 1, Gap = P.ExpandGap, Padding = new Edges4(0f, P.ExpandTop, 0f, 0f), MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = P.SectionGap, Wrap = true,
                Children =
                [
                    Button.Create(Loc.Get(Strings.Podcast.Pages.OpenEpisode), open, glyph: Icons.OpenInNewWindow) with { Key = "row:open" },
                    PlayNextButton(next, "row:next"),
                ],
            },
            Description(vm.DescriptionHtml.Length > 0 ? vm.DescriptionHtml : vm.Description, "row:desc:" + vm.Id, 0, P.SubSize),
        ],
    };

    // ══ 7. SECTIONS AND CARDS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A section head: 20/28 semibold, a trailing link.</summary>
    public static Element SectionHeader(string key, string title, string? link = null, Action? onLink = null)
    {
        var kids = new List<Element>(3) { Subtitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f } };
        kids.Add(new BoxEl { Grow = 1f });
        if (link is { Length: > 0 } && onLink is not null) kids.Add(HyperlinkButton.Create(link, onLink) with { Shrink = 0f });
        return new BoxEl
        {
            Key = key, Direction = 0, Gap = P.SectionGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Margin = new Edges4(0f, P.SectionTop, 0f, P.SectionBottom), Children = kids.ToArray(),
        };
    }

    /// <summary>"More like this" on the house media surface: shelf cards, or the house rows where the column is narrow (<paramref name="rows"/>).</summary>
    public static Element MoreLikeThis(ImmutableArray<PodcastCardVm> cards, Action<string> open, bool rows, Func<string, ContextMenuModel?>? menu,
        Action<string>? play)
    {
        var kids = new Element[cards.Length];
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            string uri = c.Uri;
            var data = new Controls.CardData(uri, c.Title, c.Subtitle.Length > 0 ? Caption(c.Subtitle) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis } : null,
                c.ArtUrl, () => open(uri), play is null ? null : () => play(uri), ShowMenu: menu is not null)
            {
                Menu = menu is null ? null : () => menu(uri),
            };
            kids[i] = new BoxEl
            {
                Key = "card:" + uri, Direction = 1, MinWidth = 0f, Width = rows ? float.NaN : P.CardCover, Shrink = 0f,
                Children = [rows ? Controls.Surface(data, Shape.Row(P.RowCardCover)) : Controls.Surface(data, Shape.Shelf(), P.CardCover)],
            };
        }
        // The two forms are a declared swap (a crossing at the Compact edge trades the house rows for the shelf cards).
        return rows
            ? new BoxEl { Key = "more-like-this:rows", Direction = 1, MinWidth = 0f, Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Children = kids }
            : new BoxEl
            {
                Key = "more-like-this:cards", Direction = 0, Wrap = true, Gap = P.CardGap, MinWidth = 0f, Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade,
                Children = kids,
            };
    }
}
