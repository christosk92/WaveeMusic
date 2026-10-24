// ── Themes/Classic/Structure/Views/Show/ClassicShowReader.cs — Classic's show reader pictures (themes plan 6.1, T9c) ────────────────
//
// From `Entities\Show.UI.cs`: the show READER's pieces (W1-W3): the sticky word rail's body (the toolbar), the "episodes N" header and its
// empty arm, the month headers, the sticky month and the year strip, the foot, the cold seed the skeletons derive from, and the section-head
// type the visit head shares (`Pivot`, `SecHead`). `ShowReaderStyle` is the one registered contract (`StyleKeys.ShowReader`); the visit head is
// `ClassicShowHead.cs`, the rail's slots, the satellites and the library pane's column `ClassicShowRail.cs`.
//
// WHAT THE CORE KEEPS (`Show.ReaderHost` and its slots): which toolbar stage, whether the find is open, the filter / sort / find signals, the
// measure plumbing, the snapshot, the extents and `CheckExtent`, the sticky month and the date index, the reveal gates. The pictures here are
// pure builders over their args, so the bound list, the skeleton deriver and the rail's slot callbacks can call them anywhere.
//
// CLASSIC'S VOICE: semibold sentence case, like every other Classic page (owner, 2026-10-24; the prototype's light lowercase heads were Zune's
// voice). No case transform: the loc values are written in sentence case and Classic draws them as written (Zune lowers the same strings through
// its case roles); a month name is the culture's own text (`Show.MonthWordOf`), drawn as written.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Classic.ClassicShowRules;

namespace Wavee.Themes.Classic;

/// <summary>Classic's style for the show page.</summary>
internal static partial class ClassicShow
{
    /// <summary>The style Classic registers under <c>StyleKeys.ShowReader</c>. Stateless: every host and a test share it.</summary>
    public static readonly ShowReaderStyle Style = new(Primary, Ledger, Rating, Badges, Topics, Publisher, Satellites, Toolbar,
        HeadShell, Head, EpisodesHeader, Group, StickyMonth, YearStrip, YearReserve, Foot, Seed)
    {
        NarrowToolbarGlyph = NarrowControlEdge,
    };

    /// <summary>Registers the show page's style.</summary>
    internal static void Register(FluentGpu.Theming.ViewRegistryBuilder views)
        => views.AddStyle(StyleKeys.ShowReader, Style);

    static readonly Func<bool> s_never = static () => false;

    // ══ 1. TYPE ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A placeholder a localized template is formatted around and split at, so the small count keeps the
    /// translator's word order ("episodes {count}", "{left} left of {total}").</summary>
    internal const char TemplateMark = (char)1;
    internal static readonly string TemplateMarkText = TemplateMark.ToString();

    /// <summary>A section head (the prototype's <c>.sechead h3</c>): Display 24/30 in Classic's <see cref="VoiceWeight"/>, with an optional
    /// small count 13/400 tertiary on the same baseline.</summary>
    static SpanTextEl Pivot(string title, string? small = null)
        => PivotOf(small is { Length: > 0 } ? [new TextSpan(title), SmallSpan("  " + small)] : [new TextSpan(title)]);

    /// <summary>A section head from a template formatted around <see cref="TemplateMark"/>: the mark becomes the small
    /// count, the rest stays the translator's words.</summary>
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
        var d = R.Type.DenseMeta("");
        return new(text, Weight: 400, Color: Tok.TextTertiary, Size: d.Size);
    }

    static SpanTextEl PivotOf(TextSpan[] spans) => new(spans)
    {
        FontFamily = DisplayFace, Size = PivotSize, LineHeight = ShowReaderRules.HeaderLine, Weight = VoiceWeight, CharSpacing = -20f,
        Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>A section head row: the pivot, its tertiary sub line, and an optional trailing action pushed right.
    /// <paramref name="stack"/> puts the subtitle under the pivot so a narrow reader never ellipsizes "Continue"
    /// to "conti…" against "up next from your progress · …".</summary>
    static Element SecHead(Element title, string? sub = null, Element? trailing = null, bool stack = false)
    {
        var kids = new List<Element>(4) { title };
        if (sub is { Length: > 0 })
            kids.Add(R.Type.DenseMeta(sub) with
            {
                Color = Tok.TextTertiary, MaxLines = stack ? 2 : 1,
                Wrap = stack ? TextWrap.Wrap : TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Shrink = 1f, Margin = new Edges4(0f, 0f, 0f, stack ? 0f : SubLineLift),
            });
        if (trailing is not null)
        {
            kids.Add(new BoxEl { Grow = 1f, MinWidth = Spacing.M });
            kids.Add(trailing);
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

    /// <summary>The prototype's <c>repeat(auto-fit, minmax(N, 1fr))</c> as COMPUTED equal columns
    /// (<see cref="ShowReaderRules.Columns"/>): rows of <paramref name="cols"/> tiles, each tile an equal share of its row,
    /// a short last row padded with empty shares so its tiles keep the others' width. Never a wrapping row — the head's
    /// height is then the arithmetic of its rows, and nothing can overflow into a line a cached measure never counted.</summary>
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

    // ══ 2. THE TOOLBAR (item 0's content — the sticky root itself is a RAW element in the core host) ═════════════════════════

    /// <summary>The reader's toolbar — ONE row, <see cref="ToolbarHeight"/> DIP, at EVERY width. Never a horizontal
    /// <c>ScrollView</c> (the scrollbar the owner reported) and never a squeezed chip:
    /// <code>
    ///   Full         All 326  Unplayed 320  In progress 5  Played 1   [⌕ Find in this show ]  │  Newest Oldest  [✓]
    ///   FindIcon     All 326  Unplayed 320  In progress 5  Played 1   ⌕  │  Newest Oldest  [✓]
    ///   CompactSort  All 326  Unplayed 320  In progress 5  Played 1   ⌕  ⇅  [✓]
    ///   FilterMenu   [Unplayed 320 ▾]                                 ⌕  ⇅  [✓]
    ///   (find open at a collapsed stage)  the chips stay; [⌕ why ger ........ ✕] takes the sort/select area
    /// </code>
    /// The stage is the core's (<see cref="ShowToolbarLayout.For"/> over THIS row's own arranged width and what its rails
    /// measured, with 16 DIP of hysteresis), so the host re-renders only when the STAGE moves (or the find toggles). Opening the
    /// collapsed find swaps the right group for the field plus a close affordance (which clears the query), so the plane's height
    /// never moves, and no arm may wrap: a second line would resize the list's mount-time clip inset. Every word, count and the
    /// box itself binds the page's own signals.</summary>
    static Element Toolbar(ShowToolbarArgs a)
    {
        var stage = a.Stage;
        bool finding = a.Finding;
        float pad = a.Compact ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        // The glyph controls' edge: the Compact 40 target under the narrow arm, the tuned 30 elsewhere (the stage rule sums the same number).
        float edge = ShowToolbarLayout.GlyphEdge(a.Compact, NarrowControlEdge);
        // LEFT: the chips at their natural width — or, at the last stage only, the menu that folds them.
        Element left = stage == ToolbarStage.FilterMenu
            ? FilterMenu(a, edge) with { Key = "tb:left" }
            : FilterWords(a) with { Key = "tb:left" };
        // RIGHT: find · divider · sort · select, as far as the stage keeps them. An open find at a collapsed stage
        // takes this very area over (the chips stay).
        var right = new List<Element>(4);
        bool takeover = stage != ToolbarStage.Full && finding;
        if (takeover)
        {
            right.Add(FindField(a, fill: true));
            right.Add(RailToggle(Icons.Cancel, Loc.Get(Strings.Detail.Filter.Clear), s_never, a.CloseFind, a.Tone, edge));
        }
        else
        {
            right.Add(stage == ToolbarStage.Full
                ? FindField(a, fill: false)
                : RailToggle(Icons.Search, Loc.Get(Strings.Podcast.Find), s_never, a.OpenFind, a.Tone, edge));
            if (stage is ToolbarStage.Full or ToolbarStage.FindIcon)
            {
                right.Add(RailDivider());
                right.Add(Controls.Words.Rail(a.SortWords, a.Order, a.Tone, onMeasured: a.OnSortMeasured));
            }
            else
            {
                right.Add(SortMenu(a, edge));
            }
            right.Add(RailToggle(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), a.IsSelecting, a.ToggleSelecting, a.Tone, edge));
        }
        var group = new BoxEl
        {
            Key = "tb:right", Direction = 0, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            // A crossing that folds the sort words into a glyph (the reader's narrow arm) re-lays the group: it slides and reveals to its new box (audit B9).
            Shrink = 0f, Layout = Design.Reduced ? null : ClassicTierMotion.Reflow, Children = right.ToArray(),
        };
        // The open field fills the whole area (the chips stay at their natural width on its left).
        if (takeover) group = group with { Grow = 1f, Shrink = 1f, Basis = 0f };
        // The toolbar measures ITSELF (its own arranged width, gutters included) against what its rails measured.
        return new BoxEl
        {
            Direction = 0, Height = ToolbarHeight, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.SpaceBetween, MinWidth = 0f, OnBoundsChanged = a.OnToolbarBounds,
            Padding = new Edges4(pad, 0f, pad, 0f), Children = [left, group],
        };
    }

    /// <summary>The filter chips at their NATURAL width — a rail that never shrinks and never scrolls (the toolbar picks a
    /// stage only when they fit; the last stage folds them into <see cref="FilterMenu"/>). It reports its measure to the
    /// toolbar's stage rule.</summary>
    static Element FilterWords(ShowToolbarArgs a)
        => Controls.Words.Rail(a.FilterWords, a.Status, a.Tone, onMeasured: a.OnFiltersMeasured);

    /// <summary>The last stage's chips: ONE button saying the selected filter ("Unplayed 320 ▾") whose menu holds the four
    /// as radio rows. The label and its count are binds over the model — the toolbar re-renders on stages, never on a
    /// selection or a count — and the accessible name is the control's, since the label alone cannot say what it is.</summary>
    static Element FilterMenu(ShowToolbarArgs a, float edge)
    {
        var tone = a.Tone;
        var overlay = a.Overlay;
        var box = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children =
            [
                R.Type.DenseTitle("") with { Text = Prop.Of(() => FilterLabelOf(a, a.Status.Value)), MaxLines = 1 },
                Icon(Icons.ChevronDown, FilterChevron) with { Color = Prop.Of(() => tone()) },
            ],
        }.Interactive(Interaction.Subtle);
        if (!Controls.IsNullOverlay(overlay)) box = box.WithContextMenu(overlay, () => FilterMenuOf(a));
        return Controls.Named(box, Loc.Get(Strings.Detail.Filter.Short));
    }

    static readonly string[] s_filterKeys =
        [Strings.Podcast.Filter.All, Strings.Podcast.Filter.Unplayed, Strings.Podcast.Filter.InProgress, Strings.Podcast.Filter.Played];

    /// <summary>"Unplayed 320": the chip's word and, once progress is trusted, its count.</summary>
    static string FilterLabelOf(ShowToolbarArgs a, int status)
    {
        string word = Loc.Get(s_filterKeys[Math.Clamp(status, 0, s_filterKeys.Length - 1)]);
        string count = a.StatusCount(status);
        return count.Length == 0 ? word : word + " " + count;
    }

    static ContextMenuModel FilterMenuOf(ShowToolbarArgs a)
    {
        var rows = new MenuFlyoutItem[s_filterKeys.Length];
        int current = a.Status.Peek();
        for (int i = 0; i < rows.Length; i++)
        {
            int status = i;
            rows[i] = MenuFlyoutItem.RadioItem(FilterLabelOf(a, status), current == status, () => a.Status.Value = status);
        }
        return new ContextMenuModel(rows);
    }

    /// <summary>The hairline between the find box and the sort words.</summary>
    static Element RailDivider() => new BoxEl
    {
        Width = 1f, Height = DividerHeight, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
    };

    static readonly string[] s_noSuggest = [];

    /// <summary>The find box. At rest it sits at its own <see cref="Controls.FindBoxWidth"/> measure past the row's
    /// flexible spacer; the collapsed row's OPENED arm lets the same control fill the middle, so the query survives
    /// either transition and the row's height never changes.</summary>
    static Element FindField(ShowToolbarArgs a, bool fill)
        => AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Find), width: Controls.FindBoxWidth, text: a.Find,
                                 queryIcon: Icons.Search, minHeight: FindMinHeight, cornerRadius: Radii.Control,
                                 grow: fill ? 1f : 0f, maxFillWidth: fill ? FindFillMax : 0f);

    /// <summary>The ladder's last rung: the two sort words folded into ONE 30-DIP glyph whose menu holds them as radio
    /// rows. A menu is the narrow FALLBACK here, never the default — at <see cref="ToolbarStage.Full"/> and
    /// <see cref="ToolbarStage.FindIcon"/> the words are still words. Its accessible name is the sort in force, since
    /// the glyph alone cannot say it.</summary>
    static Element SortMenu(ShowToolbarArgs a, float edge)
    {
        var order = a.Order;
        var tone = a.Tone;
        var overlay = a.Overlay;
        int current = order.Value;                                  // subscribe: the button is named after the sort in force
        var box = new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children = [Icon(Icons.Sort, ToggleGlyph) with { Color = Prop.Of(() => tone()) }],
        }.Interactive(Interaction.Subtle);
        if (!Controls.IsNullOverlay(overlay)) box = box.WithContextMenu(overlay, () => SortMenuOf(order));
        return Controls.Named(box, SortWordOf(current));
    }

    static ContextMenuModel SortMenuOf(Signal<int> order) => new(new[]
    {
        MenuFlyoutItem.RadioItem(SortWordOf(0), order.Peek() == 0, () => order.Value = 0),
        MenuFlyoutItem.RadioItem(SortWordOf(1), order.Peek() == 1, () => order.Value = 1),
    });

    static string SortWordOf(int order) => Loc.Get(order == 1 ? Strings.Podcast.Sort.Oldest : Strings.Podcast.Sort.Newest);

    /// <summary>A toolbar glyph toggle (the search collapse, the select arm): <paramref name="edge"/> square (30, or the Compact 40 under the narrow
    /// arm), the tone while ON.</summary>
    static Element RailToggle(string glyph, string name, Func<bool> on, Action click, Func<ColorF> tone, float edge)
        => Controls.Named(new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
            Children = [Icon(glyph, ToggleGlyph) with { Color = Prop.Of(() => on() ? tone() : Tok.TextSecondary) }],
        }.Interactive(Interaction.Subtle), name);

    // ══ 3. THE HEADER, THE MONTHS, THE FOOT ══════════════════════════════════════════════════════════════════════════════

    /// <summary>"Episodes 128" (or "Episodes 3 of 128" under a filter or a find) and the empty arm under it: the show's
    /// own words for a show with no episodes, "Nothing matches" (a tap resets the view) for an empty filter.</summary>
    static Element EpisodesHeader(ShowEpisodesHeaderArgs a)
    {
        string count = a.Filtered ? Strings.Podcast.CountOf(FormatCache.Int(a.View), FormatCache.Int(a.Total)) : FormatCache.Int(a.Total);
        // The pivot's line is STATED (30): the header's extent is arithmetic (ShowReaderRules.HeaderExtent) until it
        // carries the empty arm, and ReaderHost.CheckExtent holds the two numbers to each other.
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
            Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = new Edges4(Spacing.S, Spacing.XL, Spacing.S, Spacing.XL),
            Children =
            [
                new TextEl(Loc.Get(Strings.Podcast.NothingMatches))
                {
                    FontFamily = DisplayFace, Size = NothingSize, LineHeight = NothingLineHeight, Weight = VoiceWeight, Color = Tok.TextPrimary, MaxLines = 1,
                },
                new BoxEl
                {
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = reset, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Podcast.NothingMatchesHint))
                        {
                            Size = QuietSize, LineHeight = QuietLineHeight, Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary,
                            Wrap = TextWrap.Wrap, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>A month header (the prototype's <c>.group</c>): Display 18 in <see cref="VoiceWeight"/>, the culture's month name as written
    /// ("August 2026"), 14 above it — none right under the "Episodes" head.</summary>
    static Element Group(ShowGroupArgs a) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            new BoxEl { Height = a.Air },
            new TextEl(a.Month)
            {
                FontFamily = DisplayFace, Size = GroupSize, LineHeight = ShowReaderRules.GroupLine, Height = ShowReaderRules.GroupLine,
                Weight = VoiceWeight, CharSpacing = -10f, Color = Tok.TextTertiary, MaxLines = 1,
            },
            new BoxEl { Height = ShowReaderRules.GroupFoot },
        ],
    };

    // ── the date rail (report 4b): the sticky month and the year strip ──
    //
    // The Albums list's A–Z navigation, for dates — the same two shells (`ClassicLetterKit.StickyLetter` + `ClassicLetterKit.JumpStrip`) over the same kernel
    // (`JumpIndex`, through `ShowDateIndex`). Months are the headers; the STRIP shows years, because a decade of months is a scrollbar,
    // not a strip.

    static readonly FormatCache<int> s_years = new();
    static readonly Func<int, string> s_yearFormat = static y => y <= 0 ? "" : y.ToString(CultureInfo.InvariantCulture);

    /// <summary>The month pinned at the list's top — the twin of <c>ClassicLetterKit.StickyLetter</c>: always mounted, transparent
    /// until a month is actually under the top edge (a presence flip would relayout the overlay), and inset by the
    /// rail's own height so it sits UNDER the sticky toolbar, never behind it.
    /// <para><paramref name="monthKey"/> is the item's <see cref="DateKeys"/> month key — THE same key the group header
    /// and the year strip read. It is transparent for anything that is not one, and prints "" rather than throwing.</para></summary>
    static Element StickyMonth(IReadSignal<int> monthKey, bool narrow)
    {
        float pad = narrow ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        return new BoxEl
        {
            // Shrink-wrap like ClassicLetterKit.StickyLetter: a ZStack sibling without a height stretches, and the inner
            // FillCardDefault then paints a white sheet over the episode list (the "September 2026" overlay).
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
                    Padding = new Edges4(8f, 2f, 8f, 2f), Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Children =
                    [
                        new TextEl(Prop.Of(() => global::Wavee.Show.MonthWordOf(monthKey.Value)))
                        {
                            Size = R.Type.MicroMeta("").Size, LineHeight = R.Type.MicroMeta("").LineHeight,
                            Weight = 600, Color = Tok.TextTertiary, MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The year strip on the reader's right edge — the twin of <c>ClassicLetterKit.JumpStrip</c>: one 26 × 15 row per year
    /// the view holds, the year under the viewport top in accent/700 (two stacked runs: <c>Weight</c> is not bindable),
    /// a tap = <c>StartBringItemIntoView</c> on that year's first month. Not a tab stop (27 letters must not become 27
    /// stops, and neither must 20 years) — it is a pointer affordance beside a list that already has keyboard nav.
    /// <para><paramref name="currentMonth"/> is the SAME <see cref="DateKeys"/> month key <see cref="StickyMonth"/>
    /// reads; its year is one total decode away. (It used to be read as <c>year*100 + month</c> while the sticky signal
    /// carried <c>year*12 + month-1</c>, so no year ever lit.)</para></summary>
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
                Width = ShowReaderRules.YearStripWidth, Height = YearRowHeight, ZStack = true, Corners = CornerRadius4.All(3f),
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                OnClick = () => jump(year),
                Children =
                [
                    YearInk(text, 400, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    YearInk(text, 700, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
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

    /// <summary>The strip's column when the view holds no more than one year (nothing to jump between): the SAME 30 DIP,
    /// empty. Same key as the strip, so the two reconcile in place and the list never changes width with the filter.</summary>
    static Element YearReserve() => new BoxEl { Key = "rd:years", Direction = 1, Width = ShowReaderRules.YearStripWidth, Shrink = 0f };

    static TextEl YearInk(string text, ushort weight, Prop<ColorF> ink) => R.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    /// <summary>The list's end: the load-more pill above the dock reserve while the membership pages (a pill, never an
    /// infinite-scroll sentinel), else the reserve alone. The "More like this" shelf is the core's component over the
    /// reader's own request.</summary>
    static Element Foot(ShowFootArgs a)
    {
        var children = new List<Element>();
        if (a.CanLoadMore) children.Add(LoadMorePill(a.Paging, a.LoadMore));
        children.Add(a.Similar);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0, Gap = Spacing.L,
            Padding = new Edges4(Episode.RowPadX, Spacing.M, 0, global::Wavee.Show.BottomReserve), Children = children.ToArray(),
        };
    }

    static readonly Action s_noop = static () => { };

    /// <summary>The standard (never accent) 32/r4 button. While a page is out the label reads "Loading…" and a tap is a
    /// no-op. <c>// Workstream B</c>: was <c>Controls.Pill</c>'s capsule.</summary>
    static Element LoadMorePill(bool paging, Action page)
        => Button.Create(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore), paging ? s_noop : page,
                         ButtonAppearance.Standard);

    // ══ 4. THE SEED ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cold skeleton's SOURCE: the real filter words, the "episodes" head and eight seed rows — the list's
    /// region derives its shimmer from this one tree. No head (it has its own), no pill, no progress.</summary>
    static Element Seed(EpisodeRowStyle? style, bool narrow)
    {
        float pad = narrow ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        var kids = new Element[SeedRows + 2];
        kids[0] = new BoxEl
        {
            Direction = 0, Height = ShowReaderRules.RailExtent, Gap = Controls.Words.Gap, AlignItems = FlexAlign.Center,
            Children = [SeedWord(Strings.Podcast.Filter.All), SeedWord(Strings.Podcast.Filter.Unplayed),
                        SeedWord(Strings.Podcast.Filter.InProgress), SeedWord(Strings.Podcast.Filter.Played)],
        };
        kids[1] = new BoxEl { Direction = 1, Padding = new Edges4(0f, HeadTop, 0f, Spacing.M), Children = [Pivot(Loc.Get(Strings.Podcast.Episodes))] };
        for (int i = 0; i < SeedRows; i++) kids[i + 2] = Episode.SeedReaderRow(style, narrow);
        return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, global::Wavee.Show.BottomReserve), Children = kids };
    }

    static TextEl SeedWord(string key) => new(Loc.Get(key))
    {
        Size = Controls.Words.Size, LineHeight = Controls.Words.Line, Color = Tok.TextSecondary, MaxLines = 1,
    };
}
