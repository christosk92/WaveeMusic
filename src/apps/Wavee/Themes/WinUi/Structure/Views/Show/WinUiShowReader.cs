// ── Themes/WinUi/Structure/Views/Show/WinUiShowReader.cs — WinUI's show reader pictures (W6b, winui menus/podcasts) ───────────────────────────
//
// WinUI's `ShowReaderStyle` (`StyleKeys.ShowReader`): until this lane WinUI borrowed Classic's (the stand-in), so a WinUI show page and the library's
// podcast pane drew Zune's word rails ("All 14  Unplayed 5 ... Newest Oldest"), Display-face lowercase-weight heads and the cover's orange tone. Boards:
// W-Show (and -Compact, -Small, -Huge).
//
//   · the TOOLBAR: the filter is a SelectorBar ("All · 14 | Unplayed · 10 | In progress · 1 | Played · 3", the accent pill), the find box, the sort a
//     DropDownButton ("Newest ⌄" over a MenuFlyout of RadioMenuFlyoutItems) and the select toggle. The core's stage rule folds them as before (the
//     find to a glyph, the sort to a glyph with the same menu, the filter to a DropDownButton "Unplayed · 10 ⌄"), measured off THESE controls.
//   · the HEADS are WinUI's Subtitle (20/28 semibold), a month header Body Strong (14, "September 2026"), the counts and metas Caption; the accent, never
//     the cover tone, marks what is lit.
//
// WHAT THE CORE KEEPS (`Show.ReaderHost` and its slots): which toolbar stage, whether the find is open, the filter / sort / find signals, the measure
// plumbing, the snapshot, the extents and `CheckExtent`, the sticky month and the date index, the reveal gates. The pictures here are pure builders over
// their args. The visit head is `WinUiShowHead.cs`, the header's slots `WinUiShowRail.cs`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.WinUi.WinUiShowRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's style for the show page.</summary>
internal static partial class WinUiShow
{
    /// <summary>The style WinUI registers under <c>StyleKeys.ShowReader</c>. Stateless: every host shares it.</summary>
    public static readonly ShowReaderStyle Style = new(Primary, Ledger, Rating, Badges, Topics, Publisher, Satellites, Toolbar,
        HeadShell, Head, EpisodesHeader, Group, StickyMonth, YearStrip, YearReserve, Foot, Seed)
    {
        NarrowToolbarGlyph = NarrowControlEdge,
    };

    static readonly Func<bool> s_never = static () => false;

    // ══ 1. TYPE ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A placeholder a localized template is formatted around and split at, so the small count keeps the translator's word order.</summary>
    internal const char TemplateMark = (char)1;
    internal static readonly string TemplateMarkText = TemplateMark.ToString();

    /// <summary>A section head: WinUI's Subtitle (20 semibold, the Display face) on the core's 30 line, with an optional Caption count after it.</summary>
    static SpanTextEl Pivot(string title, string? small = null)
        => PivotOf(small is { Length: > 0 } ? [new TextSpan(title), SmallSpan("  " + small)] : [new TextSpan(title)]);

    /// <summary>A section head from a template formatted around <see cref="TemplateMark"/>: the mark becomes the small count.</summary>
    static SpanTextEl PivotTemplate(string formatted, string small)
    {
        int at = formatted.IndexOf(TemplateMark);
        if (at < 0) return Pivot(formatted);
        var spans = new List<TextSpan>(3);
        if (at > 0) spans.Add(new TextSpan(formatted[..at]));
        spans.Add(SmallSpan(small));
        if (at + 1 < formatted.Length) spans.Add(new TextSpan(formatted[(at + 1)..]));
        return PivotOf(spans.ToArray());
    }

    static TextSpan SmallSpan(string text)
    {
        var d = Caption("");
        return new(text, Weight: 400, Color: Tok.TextTertiary, Size: d.Size);
    }

    static SpanTextEl PivotOf(TextSpan[] spans) => new(spans)
    {
        FontFamily = DisplayFace, Size = PivotSize, LineHeight = ShowReaderRules.HeaderLine, Weight = VoiceWeight,
        Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>A section head row: the pivot, its Caption sub line, and an optional trailing action pushed right. <paramref name="stack"/> puts the
    /// subtitle under the pivot at a narrow reader.</summary>
    static Element SecHead(Element title, string? sub = null, Element? trailing = null, bool stack = false)
    {
        var kids = new List<Element>(4) { title };
        if (sub is { Length: > 0 })
            kids.Add(Caption(sub) with
            {
                Color = Tok.TextTertiary, MaxLines = stack ? 2 : 1,
                Wrap = stack ? TextWrap.Wrap : TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Shrink = 1f, Margin = new Edges4(0f, 0f, 0f, stack ? 0f : SubLineLift),
            });
        if (trailing is not null)
        {
            kids.Add(new BoxEl { Grow = 1f, MinWidth = Spacing.M });
            kids.Add(trailing.Value);
        }
        return new BoxEl
        {
            Direction = (byte)(stack ? 1 : 0), Gap = stack ? StackedGap : Spacing.M,
            AlignItems = stack ? FlexAlign.Start : FlexAlign.End, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    static Element QuietLine(string text) => new TextEl(text)
    {
        Size = QuietSize, LineHeight = QuietLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    static Element Column(List<Element> kids, float gap)
        => new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Children = kids.ToArray() };

    /// <summary>Rows of <paramref name="cols"/> equal tiles (the core's <see cref="ShowReaderRules.Columns"/>), a short last row padded with empty shares.</summary>
    static Element EqualRows(List<Element> tiles, int cols)
    {
        cols = Math.Max(1, cols);
        var rows = new List<Element>((tiles.Count + cols - 1) / cols);
        for (int at = 0; at < tiles.Count; at += cols)
        {
            var cells = new Element[cols];
            for (int c = 0; c < cols; c++)
                cells[c] = new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = at + c < tiles.Count ? new[] { tiles[at + c] } : Array.Empty<Element>(),
                };
            rows.Add(new BoxEl { Direction = 0, Gap = ShowReaderRules.TileGap, MinWidth = 0f, Children = cells });
        }
        return rows.Count == 1 ? rows[0] : new BoxEl { Direction = 1, Gap = ShowReaderRules.TileGap, MinWidth = 0f, Children = rows.ToArray() };
    }

    static string Joined(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    // ══ 2. THE TOOLBAR ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The reader's toolbar — ONE row at every width (the core's plane):
    /// <code>
    ///   Full         All · 326 | Unplayed · 320 | In progress · 5 | Played · 1      [⌕ Find in this show ]   [Newest ⌄]  [✓]
    ///   FindIcon     All · 326 | Unplayed · 320 | In progress · 5 | Played · 1      ⌕   [Newest ⌄]  [✓]
    ///   CompactSort  All · 326 | Unplayed · 320 | In progress · 5 | Played · 1      ⌕  ⇅  [✓]
    ///   FilterMenu   [Unplayed · 320 ⌄]                                             ⌕  ⇅  [✓]
    /// </code>
    /// The stage is the core's over THIS row's arranged width and what the pivot and the sort button measured. Opening the collapsed find swaps the right
    /// group for the field plus a close button, so the row's height never moves.</summary>
    static Element Toolbar(ShowToolbarArgs a)
    {
        var stage = a.Stage;
        bool finding = a.Finding;
        float pad = a.Compact ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        float edge = ShowToolbarLayout.GlyphEdge(a.Compact, NarrowControlEdge);
        Element left = stage == ToolbarStage.FilterMenu
            ? FilterMenu(a, edge) with { Key = "tb:left" }
            : FilterPivot(a) with { Key = "tb:left" };
        var right = new List<Element>(4);
        bool takeover = stage != ToolbarStage.Full && finding;
        if (takeover)
        {
            right.Add(FindField(a, fill: true));
            right.Add(GlyphToggle(Icons.Cancel, Loc.Get(Strings.Detail.Filter.Clear), s_never, a.CloseFind, edge));
        }
        else
        {
            right.Add(stage == ToolbarStage.Full
                ? FindField(a, fill: false)
                : GlyphToggle(Icons.Search, Loc.Get(Strings.Podcast.Find), s_never, a.OpenFind, edge));
            if (stage is ToolbarStage.Full or ToolbarStage.FindIcon)
            {
                // The stage rule budgets a hairline between the find and the sort; WinUI draws none (W-Show), the slot stays empty.
                right.Add(new BoxEl { Width = ShowToolbarLayout.Divider, Shrink = 0f });
                right.Add(SortButton(a, edge));
            }
            else
            {
                right.Add(SortGlyph(a, edge));
            }
            right.Add(GlyphToggle(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), a.IsSelecting, a.ToggleSelecting, edge));
        }
        var group = new BoxEl
        {
            Key = "tb:right", Direction = 0, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Shrink = 0f, Layout = Design.Reduced ? null : WinUiDiscoveryMotion.Reflow, Children = right.ToArray(),
        };
        if (takeover) group = group with { Grow = 1f, Shrink = 1f, Basis = 0f };
        return new BoxEl
        {
            Direction = 0, Height = ToolbarHeight, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.SpaceBetween, MinWidth = 0f, OnBoundsChanged = a.OnToolbarBounds,
            Padding = new Edges4(pad, 0f, a.Trailing ? pad : 0f, 0f), Children = [left, group],
        };
    }

    static readonly string[] s_filterKeys =
        [Strings.Podcast.Filter.All, Strings.Podcast.Filter.Unplayed, Strings.Podcast.Filter.InProgress, Strings.Podcast.Filter.Played];

    static readonly string?[] s_filterItemKeys = ["tb:filter:0", "tb:filter:1", "tb:filter:2", "tb:filter:3"];

    /// <summary>"All · 14": the filter's word and, once progress is trusted, its count.</summary>
    static string FilterLabelOf(ShowToolbarArgs a, int status)
        => WinUiChoice.Counted(Loc.Get(s_filterKeys[Math.Clamp(status, 0, s_filterKeys.Length - 1)]), a.StatusCount(status));

    /// <summary>The filter as a SelectorBar at its natural width, reporting its measure to the stage rule (the whole bar, and each item's share).</summary>
    static Element FilterPivot(ShowToolbarArgs a) => Embed.Comp(new ToolbarProps(a), static () => new FilterPivotHost());

    /// <summary>The toolbar's args as re-pushed props (the host re-renders the toolbar on a stage or a find toggle; the pivot follows the counts itself).</summary>
    sealed record ToolbarProps(ShowToolbarArgs Args);

    sealed class FilterPivotHost : Component
    {
        readonly float[] _words = new float[4];
        Action<RectF>? _measured;
        ShowToolbarArgs? _args;

        public override Element Render()
        {
            var a = _args = UseProps<ToolbarProps>().Args;
            var labels = new string[s_filterKeys.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = FilterLabelOf(a, i);   // reads the snapshot: a count landing re-renders this pivot
            _measured ??= r =>
            {
                if (_args is not { } live || !(r.W > 0f)) return;
                for (int i = 0; i < _words.Length; i++) _words[i] = r.W / _words.Length;
                live.OnFiltersMeasured(r.W, _words);
            };
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, OnBoundsChanged = _measured,
                Children = [WinUiChoice.Pivot(labels, a.Status, ToolbarHeight, keys: s_filterItemKeys)],
            };
        }
    }

    /// <summary>The last stage's filter: a DropDownButton saying the filter in force ("Unplayed · 320 ⌄") over the four as radio rows.</summary>
    static Element FilterMenu(ShowToolbarArgs a, float edge) => Embed.Comp(new MenuProps(a, edge), static () => new FilterMenuHost());

    sealed record MenuProps(ShowToolbarArgs Args, float Edge);

    sealed class FilterMenuHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<MenuProps>();
            var a = p.Args;
            int current = Math.Clamp(a.Status.Value, 0, s_filterKeys.Length - 1);
            var status = a.Status;
            var rows = new List<MenuFlyoutItem>(s_filterKeys.Length);
            for (int i = 0; i < s_filterKeys.Length; i++)
            {
                int code = i;
                rows.Add(MenuFlyoutItem.RadioItem(FilterLabelOf(a, code), current == code, () => status.Value = code));
            }
            string label = FilterLabelOf(a, current);
            return WinUiChoice.Sort("tb:filter-menu", label, label, rows, p.Edge, Loc.Get(Strings.Theme.Winui.Library.ShowFilter));
        }
    }

    static readonly string[] s_sortKeys = [Strings.Podcast.Sort.Newest, Strings.Podcast.Sort.Oldest];

    static IReadOnlyList<MenuFlyoutItem> SortItems(Signal<int> order, int current)
    {
        var items = new List<MenuFlyoutItem>(s_sortKeys.Length + 1) { MenuFlyoutItem.Header(Loc.Get(Strings.Theme.Winui.Library.SortBy)) };
        for (int i = 0; i < s_sortKeys.Length; i++)
        {
            int code = i;
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(s_sortKeys[i]), current == code, () => order.Value = code));
        }
        return items;
    }

    /// <summary>The sort at the two rich stages: a DropDownButton "Newest ⌄" (W-Show), reporting its width to the stage rule.</summary>
    static Element SortButton(ShowToolbarArgs a, float edge) => Embed.Comp(new MenuProps(a, edge), static () => new SortButtonHost());

    sealed class SortButtonHost : Component
    {
        static readonly float[] s_one = new float[2];
        Action<RectF>? _measured;
        ShowToolbarArgs? _args;

        public override Element Render()
        {
            var p = UseProps<MenuProps>();
            var a = _args = p.Args;
            int current = Math.Clamp(a.Order.Value, 0, s_sortKeys.Length - 1);
            _measured ??= r =>
            {
                if (_args is not { } live || !(r.W > 0f)) return;
                s_one[0] = s_one[1] = r.W / 2f;
                live.OnSortMeasured(r.W, s_one);
            };
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, OnBoundsChanged = _measured,
                Children = [WinUiChoice.Sort("tb:sort", FormatCache.Int(current), Loc.Get(s_sortKeys[current]), SortItems(a.Order, current),
                                             MathF.Max(p.Edge, WinUiChoiceRules.ControlH), Loc.Get(Strings.Theme.Winui.Library.ShowSort))],
            };
        }
    }

    /// <summary>The narrow stages' sort: the sort glyph opening the same menu (W-Show-Small). Named after the sort in force.</summary>
    static Element SortGlyph(ShowToolbarArgs a, float edge)
    {
        var order = a.Order;
        return IconButton.Menu(Icons.Sort, () => SortItems(order, Math.Clamp(order.Peek(), 0, s_sortKeys.Length - 1)),
                               toolTip: Loc.Get(Strings.Theme.Winui.Library.ShowSort),
                               size: edge > WinUiChoiceRules.ControlH ? ControlSize.Large : ControlSize.Medium);
    }

    static readonly string[] s_noSuggest = [];

    /// <summary>The find box (WinUI's AutoSuggestBox, "Find in this show"); the collapsed row's opened arm lets it fill the middle.</summary>
    static Element FindField(ShowToolbarArgs a, bool fill)
        => AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Find), width: Controls.FindBoxWidth, text: a.Find,
                                 queryIcon: Icons.Search, minHeight: FindMinHeight, cornerRadius: Radii.Control,
                                 grow: fill ? 1f : 0f, maxFillWidth: fill ? FindFillMax : 0f);

    /// <summary>A toolbar glyph toggle (the search collapse, the select arm): a subtle <paramref name="edge"/> square, the accent ink while ON.</summary>
    static Element GlyphToggle(string glyph, string name, Func<bool> on, Action click, float edge)
        => Controls.Named(new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Fill = Prop.Of(() => on() ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
            Children = [Icon(glyph, ToggleGlyph) with { Color = Prop.Of(() => on() ? Tok.AccentTextPrimary : Tok.TextPrimary) }],
        }, name);

    // ══ 3. THE HEADER, THE MONTHS, THE FOOT ══════════════════════════════════════════════════════════════════════════════

    /// <summary>"Episodes 128" (or "Episodes 3 of 128") and the empty arm under it.</summary>
    static Element EpisodesHeader(ShowEpisodesHeaderArgs a)
    {
        string count = a.Filtered ? Strings.Podcast.CountOf(FormatCache.Int(a.View), FormatCache.Int(a.Total)) : FormatCache.Int(a.Total);
        var kids = new List<Element>(2)
        {
            PivotTemplate(Strings.Podcast.EpisodesCount(TemplateMarkText), count) with { Height = ShowReaderRules.HeaderLine },
        };
        if (a.IsEmpty) kids.Add(EmptyArm(a.EmptyShow, a.Reset));
        return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, ShowReaderRules.HeaderFoot), Children = kids.ToArray() };
    }

    static Element EmptyArm(bool emptyShow, Action reset)
    {
        if (emptyShow) return QuietLine(Loc.Get(Strings.Podcast.Empty));
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = new Edges4(0f, Spacing.XL, 0f, Spacing.XL),
            Children =
            [
                new TextEl(Loc.Get(Strings.Podcast.NothingMatches))
                {
                    FontFamily = DisplayFace, Size = NothingSize, LineHeight = NothingLineHeight, Weight = VoiceWeight, Color = Tok.TextPrimary, MaxLines = 1,
                },
                HyperlinkButton.Create(Loc.Get(Strings.Podcast.NothingMatchesHint), reset, size: ControlSize.Small),
            ],
        };
    }

    /// <summary>A month header: Body Strong 14 in the primary ink, the culture's month as written ("September 2026", W-Show).</summary>
    static Element Group(ShowGroupArgs a) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            new BoxEl { Height = a.Air },
            new BoxEl
            {
                Direction = 0, Height = ShowReaderRules.GroupLine, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [new TextEl(a.Month) { Size = GroupSize, Weight = VoiceWeight, Color = Tok.TextPrimary, MaxLines = 1 }],
            },
            new BoxEl { Height = ShowReaderRules.GroupFoot },
        ],
    };

    static readonly FormatCache<int> s_years = new();
    static readonly Func<int, string> s_yearFormat = static y => y <= 0 ? "" : y.ToString(CultureInfo.InvariantCulture);

    /// <summary>The month pinned at the list's top: a Caption on the card plate, transparent until a month is under the top edge, under the toolbar.</summary>
    static Element StickyMonth(IReadSignal<int> monthKey, bool narrow)
    {
        float pad = narrow ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        return new BoxEl
        {
            Key = "rd:sticky-month",
            Height = ShowReaderRules.RailExtent + Spacing.XS + StickyMonthPlateH,
            Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false, Shrink = 0f,
            Padding = new Edges4(pad, ShowReaderRules.RailExtent + Spacing.XS, pad, 0f),
            Opacity = Prop.Of(() => DateKeys.IsMonthKey(monthKey.Value) ? 1f : 0f),
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, AlignSelf = FlexAlign.Start,
                    Padding = new Edges4(StickyPadX, StickyPadY, StickyPadX, StickyPadY), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault,
                    BorderWidth = WinUiChoiceRules.ViewStroke, BorderColor = Tok.StrokeCardDefault,
                    Children =
                    [
                        new TextEl(Prop.Of(() => global::Wavee.Show.MonthWordOf(monthKey.Value)))
                        {
                            Size = Caption("").Size, LineHeight = Caption("").LineHeight, Weight = VoiceWeight, Color = Tok.TextSecondary, MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The year strip on the reader's right edge: one Caption row per year the view holds, the year under the viewport top in the accent; a tap
    /// brings that year's first month to the top. A pointer affordance beside a list with its own keyboard navigation (not a tab stop).</summary>
    static Element YearStrip(int[] years, IReadSignal<int> currentMonth, Action<int> jump)
    {
        var rows = new Element[years.Length];
        for (int i = 0; i < years.Length; i++)
        {
            int year = years[i];
            Func<bool> now = () => DateKeys.YearOfMonth(currentMonth.Value) == year;
            string text = s_years.Get(year, s_yearFormat);
            rows[i] = new BoxEl
            {
                Width = ShowReaderRules.YearStripWidth, Height = YearRowHeight, ZStack = true, Corners = CornerRadius4.All(YearCorner),
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                OnClick = () => jump(year),
                Children =
                [
                    YearInk(text, 400, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    YearInk(text, VoiceWeight, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
                ],
            };
        }
        return new BoxEl
        {
            Key = "rd:years",
            Direction = 1, Width = ShowReaderRules.YearStripWidth, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, ShowReaderRules.RailExtent, Spacing.XS, global::Wavee.Show.BottomReserve), Children = rows,
        };
    }

    /// <summary>The strip's column when the view holds no more than one year: the same width, empty, under the same key.</summary>
    static Element YearReserve() => new BoxEl { Key = "rd:years", Direction = 1, Width = ShowReaderRules.YearStripWidth, Shrink = 0f };

    static TextEl YearInk(string text, ushort weight, Prop<ColorF> ink) => Caption(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    /// <summary>The list's end: the "Load more" standard button above the dock reserve while the membership pages, then "More like this".</summary>
    static Element Foot(ShowFootArgs a)
    {
        var children = new List<Element>();
        if (a.CanLoadMore) children.Add(LoadMore(a.Paging, a.LoadMore));
        children.Add(a.Similar);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0, Gap = Spacing.L,
            Padding = new Edges4(Episode.RowPadX, Spacing.M, 0, global::Wavee.Show.BottomReserve), Children = children.ToArray(),
        };
    }

    static readonly Action s_noop = static () => { };

    static Element LoadMore(bool paging, Action page)
        => Button.Create(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore), paging ? s_noop : page, ButtonAppearance.Standard);

    // ══ 4. THE SEED ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cold skeleton's SOURCE: the filter's words, the "Episodes" head and eight seed rows.</summary>
    static Element Seed(EpisodeRowStyle? style, bool narrow)
    {
        float pad = narrow ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        var kids = new Element[SeedRows + 2];
        kids[0] = new BoxEl
        {
            Direction = 0, Height = ShowReaderRules.RailExtent, Gap = Spacing.XL, AlignItems = FlexAlign.Center,
            Children = [SeedWord(Strings.Podcast.Filter.All), SeedWord(Strings.Podcast.Filter.Unplayed),
                        SeedWord(Strings.Podcast.Filter.InProgress), SeedWord(Strings.Podcast.Filter.Played)],
        };
        kids[1] = new BoxEl { Direction = 1, Padding = new Edges4(0f, HeadTop, 0f, Spacing.M), Children = [Pivot(Loc.Get(Strings.Podcast.Episodes))] };
        for (int i = 0; i < SeedRows; i++) kids[i + 2] = Episode.SeedReaderRow(style, narrow);
        return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, global::Wavee.Show.BottomReserve), Children = kids };
    }

    static TextEl SeedWord(string key) => Body(Loc.Get(key)) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap };
}
