// ── Themes/Zune/Structure/Views/Show/ZuneShowReader.cs — Zune's show reader pictures (themes plan 6.3; P5 Z6b2) ────────────────────────
//
// Board Z-Show (and -Compact, -Small, -Huge). The show page under Zune is the detail frame's two columns (the identity column with the show's
// slots, `ZuneShowRail.cs`) and, on the right, the episode READER: the word toolbar (all 812 · unplayed 690 · in progress 2 · played 120, a
// spring, find in this show · newest), the visit head (`ZuneShowHead.cs`), the 26/32 light "episodes 812" heading, the 22 light month words
// over the rows, and the foot. `ShowReaderStyle` is the one registered contract (`StyleKeys.ShowReader`).
//
// WHAT THE CORE KEEPS (`Show.ReaderHost` and its slots): which toolbar stage, whether the find is open, the filter / sort / find signals, the
// measure plumbing, the snapshot, the extents and `CheckExtent`, the sticky month and the date index, the reveal gates. The pictures here are
// pure builders over their args. The arithmetic the list agrees on row for row (the 48 rail, the 30 + 12 "episodes" header, the 14 + 24 + 4
// month header, the gutters and the year strip's 30) is the core's `ShowReaderRules`: every picture states those heights on its elements.
//
// CASING. The words this file writes are lowered by the style's case roles (`R.Case`), the month word too (the core hands the culture's text as
// written); a name (a show, an episode) is never re-cased.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Zune.ZuneShowRules;
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's style for the show page.</summary>
internal static partial class ZuneShow
{
    /// <summary>The style Zune registers under <c>StyleKeys.ShowReader</c>. Stateless: every host and a test share it.</summary>
    public static readonly ShowReaderStyle Style = new(Primary, Ledger, Rating, Badges, Topics, Publisher, Satellites, Toolbar,
        HeadShell, Head, EpisodesHeader, Group, StickyMonth, YearStrip, YearReserve, Foot, Seed)
    {
        NarrowToolbarGlyph = NarrowControlEdge,
        // V19 (the owner's call): the year strip stands in the frame's outer margin, so the rows run to the column edge and nothing reserves a column.
        YearsInMargin = true,
    };

    /// <summary>Registers the show page's style.</summary>
    internal static void Register(ViewRegistryBuilder views) => views.AddStyle(StyleKeys.ShowReader, Style);

    static readonly Func<bool> s_never = static () => false;

    /// <summary>The accent ink as a live read (a bind calls it; nothing captures the colour).</summary>
    static readonly Func<ColorF> s_accent = static () => R.Ink.AccentDecor;

    // ══ 1. TYPE ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A placeholder a localized template is formatted around and split at, so the small count keeps the translator's word order
    /// ("episodes {count}", "{left} left of {total}").</summary>
    internal const char TemplateMark = (char)1;
    internal static readonly string TemplateMarkText = TemplateMark.ToString();

    /// <summary>A section head word: the 26/32 light heading voice on the core's stated 30 line, lowered by the heading case role.</summary>
    static TextEl HeadWord(string text) => K.Heading(text) with { LineHeight = ShowReaderRules.HeaderLine };

    /// <summary>The small count after a head word (13, tertiary), lifted onto the word's baseline.</summary>
    static TextEl SmallCount(string text) => R.Type.DenseMeta(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, SubLineLift),
    };

    static BoxEl HeadRow(List<Element> kids) => new()
    {
        Direction = 0, Gap = HeadGap, AlignItems = FlexAlign.End, MinWidth = 0f, Children = kids.ToArray(),
    };

    /// <summary>A section head (Z-Show: "episodes 812"): the heading word and, beside it, an optional small count.</summary>
    static BoxEl Pivot(string title, string? small = null)
    {
        var kids = new List<Element>(2) { HeadWord(title) };
        if (small is { Length: > 0 }) kids.Add(SmallCount(small));
        return HeadRow(kids);
    }

    /// <summary>A section head from a template formatted around <see cref="TemplateMark"/>: the mark becomes the small count, the rest stays the
    /// translator's words.</summary>
    static BoxEl PivotTemplate(string formatted, string small)
    {
        int at = formatted.IndexOf(TemplateMark);
        if (at < 0) return Pivot(formatted);
        string before = formatted[..at].Trim(), after = formatted[(at + 1)..].Trim();
        var kids = new List<Element>(3);
        if (before.Length > 0) kids.Add(HeadWord(before));
        kids.Add(SmallCount(small));
        if (after.Length > 0) kids.Add(HeadWord(after));
        return HeadRow(kids);
    }

    /// <summary>A section head row: the head, its tertiary sub line, and an optional trailing action pushed right. <paramref name="stack"/> puts the
    /// subtitle under the head so a narrow reader never ellipsizes "continue" against "up next from your progress · …".</summary>
    static Element SecHead(Element title, string? sub = null, Element? trailing = null, bool stack = false)
    {
        var kids = new List<Element>(4) { title };
        if (sub is { Length: > 0 })
            kids.Add(R.Type.DenseMeta(sub) with
            {
                Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = stack ? 2 : 1,
                Wrap = stack ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
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

    /// <summary>A quiet sentence the theme writes (the unavailable head, the empty show, the hint): the meta voice, wrapping, lowered as a label.</summary>
    static Element QuietLine(string text) => R.Type.TrackMeta(text) with
    {
        Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    static Element Column(List<Element> kids, float gap)
        => new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Children = kids.ToArray() };

    /// <summary>The prototype's <c>repeat(auto-fit, minmax(N, 1fr))</c> as COMPUTED equal columns (<see cref="ShowReaderRules.Columns"/>): rows of
    /// <paramref name="cols"/> tiles, each tile an equal share of its row, a short last row padded with empty shares so its tiles keep the others'
    /// width. Never a wrapping row: the head's height is then the arithmetic of its rows.</summary>
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

    /// <summary>The reader's toolbar — ONE row, <see cref="ToolbarHeight"/> DIP, at EVERY width. Never a horizontal <c>ScrollView</c> and never a
    /// squeezed chip:
    /// <code>
    ///   Full         all 326  unplayed 320  in progress 5  played 1   [⌕ find in this show ]  │  newest oldest  [✓]
    ///   FindIcon     all 326  unplayed 320  in progress 5  played 1   ⌕  │  newest oldest  [✓]
    ///   CompactSort  all 326  unplayed 320  in progress 5  played 1   ⌕  ⇅  [✓]
    ///   FilterMenu   [unplayed 320 ▾]                                 ⌕  ⇅  [✓]
    ///   (find open at a collapsed stage)  the words stay; [⌕ why ger ........ ✕] takes the sort/select area
    /// </code>
    /// The stage is the core's (<see cref="ShowToolbarLayout.For"/> over THIS row's own arranged width and what its rails measured), so the host
    /// re-renders only when the STAGE moves (or the find toggles). Zune's words are the core word rail under the accent underline (Z-Show: the
    /// current word 600, the count after it), the find box the stock suggest box (the style makes it the round field).</summary>
    static Element Toolbar(ShowToolbarArgs a)
    {
        var stage = a.Stage;
        bool finding = a.Finding;
        float pad = a.Compact ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        // The glyph controls' edge: the Compact 40 target under the narrow arm, the 30 glyph elsewhere (the stage rule sums the same number).
        float edge = ShowToolbarLayout.GlyphEdge(a.Compact, NarrowControlEdge);
        // LEFT: the words at their natural width — or, at the last stage only, the menu that folds them.
        Element left = stage == ToolbarStage.FilterMenu
            ? FilterMenu(a, edge) with { Key = "tb:left" }
            : FilterWords(a) with { Key = "tb:left" };
        // RIGHT: find · divider · sort · select, as far as the stage keeps them. An open find at a collapsed stage takes this very area over.
        var right = new List<Element>(4);
        bool takeover = stage != ToolbarStage.Full && finding;
        if (takeover)
        {
            right.Add(FindField(a, fill: true));
            right.Add(RailToggle(Icons.Cancel, Loc.Get(Strings.Detail.Filter.Clear), s_never, a.CloseFind, edge));
        }
        else
        {
            right.Add(stage == ToolbarStage.Full
                ? FindField(a, fill: false)
                : RailToggle(Icons.Search, Loc.Get(Strings.Podcast.Find), s_never, a.OpenFind, edge));
            if (stage is ToolbarStage.Full or ToolbarStage.FindIcon)
            {
                right.Add(RailDivider());
                right.Add(Controls.Words.Rail(a.SortWords, a.Order, s_accent, onMeasured: a.OnSortMeasured, restInk: ZuneTokens.WordRestNow));
            }
            else
            {
                right.Add(SortMenu(a, edge));
            }
            right.Add(RailToggle(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), a.IsSelecting, a.ToggleSelecting, edge));
        }
        var group = new BoxEl
        {
            Key = "tb:right", Direction = 0, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Shrink = 0f, Children = right.ToArray(),
        };
        // The open field fills the whole area (the words stay at their natural width on its left).
        if (takeover) group = group with { Grow = 1f, Shrink = 1f, Basis = 0f };
        // The toolbar measures ITSELF (its own arranged width, gutters included) against what its rails measured.
        return new BoxEl
        {
            Direction = 0, Height = ToolbarHeight, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.SpaceBetween, MinWidth = 0f, OnBoundsChanged = a.OnToolbarBounds,
            Padding = new Edges4(pad, 0f, a.Trailing ? pad : 0f, 0f), Children = [left, group],
        };
    }

    /// <summary>The filter words at their NATURAL width — a rail that never shrinks and never scrolls (the toolbar picks a stage only when they fit;
    /// the last stage folds them into <see cref="FilterMenu"/>). It reports its measure to the toolbar's stage rule.</summary>
    static Element FilterWords(ShowToolbarArgs a)
        => Controls.Words.Rail(a.FilterWords, a.Status, s_accent, onMeasured: a.OnFiltersMeasured, restInk: ZuneTokens.WordRestNow,
                                  countRest: ZuneTokens.WordCountRestNow);

    /// <summary>The last stage's words: ONE button saying the selected filter ("unplayed 320 ▾") whose menu holds the four as radio rows. The label
    /// and its count are binds over the model — the toolbar re-renders on stages, never on a selection or a count — and the accessible name is the
    /// control's, since the label alone cannot say what it is.</summary>
    static Element FilterMenu(ShowToolbarArgs a, float edge)
    {
        var overlay = a.Overlay;
        var box = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = R.Shape.All(ShapeRole.Control), Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children =
            [
                R.Type.DenseTitle("") with
                {
                    Text = Prop.Of(() => FilterLabelOf(a, a.Status.Value)), Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                },
                Icon(Icons.ChevronDown, FilterChevron) with { Color = Prop.Of(s_accent) },
            ],
        }.Interactive(Interaction.Subtle);
        if (!Controls.IsNullOverlay(overlay)) box = box.WithContextMenu(overlay, () => FilterMenuOf(a));
        return Controls.Named(box, Loc.Get(Strings.Detail.Filter.Short));
    }

    static readonly string[] s_filterKeys =
        [Strings.Podcast.Filter.All, Strings.Podcast.Filter.Unplayed, Strings.Podcast.Filter.InProgress, Strings.Podcast.Filter.Played];

    /// <summary>"unplayed 320": the word and, once progress is trusted, its count.</summary>
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

    /// <summary>The find box. At rest it sits at its own <see cref="Controls.FindBoxWidth"/> measure past the row's flexible spacer; the collapsed
    /// row's OPENED arm lets the same control fill the middle, so the query survives either transition and the row's height never changes.</summary>
    static Element FindField(ShowToolbarArgs a, bool fill)
        => AutoSuggestBox.Create(s_noSuggest, ZuneText.Word(Loc.Get(Strings.Podcast.Find)), width: Controls.FindBoxWidth, text: a.Find,
                                 queryIcon: Icons.Search, minHeight: FindMinHeight, cornerRadius: R.Shape.For(ShapeRole.Control),
                                 grow: fill ? 1f : 0f, maxFillWidth: fill ? FindFillMax : 0f);

    /// <summary>The ladder's last rung: the two sort words folded into ONE glyph whose menu holds them as radio rows. A menu is the narrow FALLBACK
    /// here, never the default — at <see cref="ToolbarStage.Full"/> and <see cref="ToolbarStage.FindIcon"/> the words are still words. Its
    /// accessible name is the sort in force, since the glyph alone cannot say it.</summary>
    static Element SortMenu(ShowToolbarArgs a, float edge)
    {
        var order = a.Order;
        var overlay = a.Overlay;
        int current = order.Value;                                  // subscribe: the button is named after the sort in force
        var box = new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = R.Shape.All(ShapeRole.Control), Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children = [Icon(Icons.Sort, ToggleGlyph) with { Color = Prop.Of(s_accent) }],
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

    /// <summary>A toolbar glyph toggle (the search collapse, the select arm): <paramref name="edge"/> square (the core's glyph edge, the Compact
    /// target under the narrow arm), the accent while ON.</summary>
    static Element RailToggle(string glyph, string name, Func<bool> on, Action click, float edge)
        => Controls.Named(new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = R.Shape.All(ShapeRole.Control), Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
            Children = [Icon(glyph, ToggleGlyph) with { Color = Prop.Of(() => on() ? R.Ink.AccentDecor : Tok.TextSecondary) }],
        }.Interactive(Interaction.Subtle), name);

    // ══ 3. THE HEADER, THE MONTHS, THE FOOT ══════════════════════════════════════════════════════════════════════════════

    /// <summary>"episodes 128" (or "episodes 3 of 128" under a filter or a find) and the empty arm under it: the show's own words for a show with no
    /// episodes, "nothing matches" (a tap resets the view) for an empty filter.</summary>
    static Element EpisodesHeader(ShowEpisodesHeaderArgs a)
    {
        string count = a.Filtered ? Strings.Podcast.CountOf(FormatCache.Int(a.View), FormatCache.Int(a.Total)) : FormatCache.Int(a.Total);
        // The head's line is STATED (30): the header's extent is arithmetic (ShowReaderRules.HeaderExtent) until it carries the empty arm, and
        // ReaderHost.CheckExtent holds the two numbers to each other.
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
                R.Type.SheetTitle(Loc.Get(Strings.Podcast.NothingMatches)) with
                {
                    Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl
                {
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = reset, MinWidth = 0f,
                    Children =
                    [
                        R.Type.TrackMeta(Loc.Get(Strings.Podcast.NothingMatchesHint)) with
                        {
                            Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary, HoverColor = R.Ink.AccentDecor, Wrap = TextWrap.Wrap, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>A month header (Z-Show: "october" 22 light): the culture's month word (lowered by the heading case role) on the core's stated 24 line, 14 of air
    /// above it — none right under the "episodes" head.</summary>
    /// <summary>A month header: the month word on the group's 24 line, a button that opens the month jump list where the year strip does not stand
    /// (<see cref="ZuneMonthHeader"/>, `ZuneShowJumpList.cs`).</summary>
    static Element Group(ShowGroupArgs a) => Embed.Comp(new ZuneMonthHeaderProps(a), static () => new ZuneMonthHeader());

    // ── the date rail: the sticky month and the year strip (the Albums list's A–Z navigation, for dates) ──

    static readonly FormatCache<int> s_years = new();
    static readonly Func<int, string> s_yearFormat = static y => y <= 0 ? "" : y.ToString(CultureInfo.InvariantCulture);
    static readonly int[] s_noYears = [];

    /// <summary>The month pinned at the list's top — always mounted, transparent until a month is actually under the top edge (a presence flip would
    /// relayout the overlay), and inset by the rail's own height so it sits UNDER the sticky toolbar, never behind it. On the page's ground, so the
    /// rows beneath it never read through the word.</summary>
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
                    Shrink = 0f, AlignSelf = FlexAlign.Start, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                    Corners = R.Shape.All(ShapeRole.Control), Fill = Prop.Of(static () => R.Layer.Content),
                    Children =
                    [
                        new TextEl(Prop.Of(() => global::Wavee.Show.MonthWordOf(monthKey.Value)))
                        {
                            Size = R.Type.MicroMeta("").Size, LineHeight = R.Type.MicroMeta("").LineHeight,
                            Weight = 600, Case = R.Case.For(LabelKind.Label), Color = Tok.TextTertiary, MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The year strip, in the frame's OUTER MARGIN past the reader's trailing edge (V19, the owner's call: the rows run to the column edge and
    /// the strip overlaps nothing): one row per year the view holds, the year under the viewport top in the accent and 700 (two stacked runs:
    /// <c>Weight</c> is not bindable), a tap = <c>StartBringItemIntoView</c> on that year's first month. Not a tab stop (twenty years must not become
    /// twenty stops): a pointer affordance beside a list that already has keyboard nav. <see cref="MarginYears"/> places it.
    /// <para><paramref name="currentMonth"/> is the SAME <see cref="DateKeys"/> month key <see cref="StickyMonth"/> reads.</para></summary>
    static Element YearStrip(int[] years, IReadSignal<int> currentMonth, Action<int> jump)
        => Embed.Comp(new MarginYearsProps(years, currentMonth, jump), static () => new MarginYears()) with { Key = "rd:years" };

    /// <summary>The strip when the view holds no more than one year (nothing to jump between): the SAME component with no rows, so the two reconcile
    /// in place and the list never changes width with the filter.</summary>
    static Element YearReserve() => Embed.Comp(new MarginYearsProps(s_noYears, null, null), static () => new MarginYears()) with { Key = "rd:years" };

    sealed record MarginYearsProps(int[] Years, IReadSignal<int>? CurrentMonth, Action<int>? Jump);

    /// <summary>The strip's place: the outer margin on the reader's trailing side, on an anchor that takes no width (the list keeps the reader's whole
    /// width, so its clip, and the hit slop of a control on its edge, reach the margin). A reader that ends on the column edge
    /// (<see cref="global::Wavee.Show.ReaderOwnsEdge"/> false: the two-column form, the library's pane) has the strip stand PAST it, the gutter's width
    /// (<see cref="ZuneTiers.GutterOf"/>); one that runs to the window edge and pads its own trailing gutter (the stacked form, a Compact drill-in) has
    /// it stand INSIDE that gutter, from the edge in, the narrow gutter's width. The strip eases its width with the frame's gutters
    /// (<see cref="ZuneFrameRules.GutterMotion"/>), so a crossing slides it with the column edge instead of stepping it. The years stand only where the
    /// margin holds them readable and tappable (<see cref="ZuneShowRules.YearsInStrip"/>: Standard and up, past the column edge); everywhere else the
    /// month headers open the jump list instead (`ZuneShowJumpList.cs`) and the strip stays empty, so nothing reflows as it comes or goes.</summary>
    sealed class MarginYears : Component
    {
        public override Element Render()
        {
            var p = UseProps<MarginYearsProps>();
            bool inside = UseContext(global::Wavee.Show.ReaderOwnsEdge);
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            float margin = inside ? ShowReaderRules.PadNarrow : ZuneTiers.GutterOf(in tiers);
            bool shown = ZuneShowRules.YearsInStrip(tiers.Window, inside);
            LayoutTransition? motion = Design.Reduced ? null : ZuneFrameRules.GutterMotion;
            var years = shown ? p.Years : s_noYears;
            var rows = new Element[years.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                int year = years[i];
                var month = p.CurrentMonth;
                var jump = p.Jump;
                Func<bool> now = () => month is not null && DateKeys.YearOfMonth(month.Value) == year;
                string text = s_years.Get(year, s_yearFormat);
                rows[i] = new BoxEl
                {
                    Width = ShowReaderRules.YearStripWidth, Height = YearRowHeight, ZStack = true, Corners = R.Shape.All(ShapeRole.Control), Shrink = 0f,
                    Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                    OnClick = () => jump?.Invoke(year),
                    Children =
                    [
                        YearInk(text, 400, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                        YearInk(text, 700, Prop.Of(() => now() ? R.Ink.AccentDecor : ColorF.Transparent)),
                    ],
                };
            }
            return new BoxEl
            {
                Direction = 1, Width = 0f, Grow = 1f, Shrink = 0f, MinWidth = 0f, AlignItems = inside ? FlexAlign.End : FlexAlign.Start,
                Children =
                [
                    new BoxEl
                    {
                        Key = "rd:years:strip", Direction = 1, Width = margin, Grow = 1f, Basis = 0f, Shrink = 0f, Animate = motion,
                        Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                        Padding = new Edges4(0f, ShowReaderRules.RailExtent, 0f, global::Wavee.Show.BottomReserve),
                        // The rows come and go with the tier (the jump list takes over under Standard): one keyed column that fades.
                        Children = rows.Length == 0 ? [] :
                        [
                            new BoxEl
                            {
                                Key = "rd:years:rows", Direction = 1, AlignItems = FlexAlign.Center, Shrink = 0f,
                                Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Children = rows,
                            },
                        ],
                    },
                ],
            };
        }
    }

    static TextEl YearInk(string text, ushort weight, Prop<ColorF> ink) => R.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    /// <summary>The list's end: the load-more pill above the dock reserve while the membership pages (a pill, never an infinite-scroll sentinel),
    /// else the reserve alone. The "more like this" shelf is the core's component over the reader's own request.</summary>
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

    /// <summary>The standard button. While a page is out the label reads "Loading…" and a tap is a no-op.</summary>
    static Element LoadMorePill(bool paging, Action page)
        => Button.Create(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore), paging ? s_noop : page,
                         ButtonAppearance.Standard);

    // ══ 4. THE SEED ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cold skeleton's SOURCE: the real filter words, the "episodes" head and eight seed rows — the list's region derives its shimmer
    /// from this one tree. No head (it has its own), no pill, no progress.</summary>
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
        return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(pad, 0f, 0f, global::Wavee.Show.BottomReserve), Children = kids };
    }

    static TextEl SeedWord(string key) => new(Loc.Get(key))
    {
        Size = Controls.Words.Size, LineHeight = Controls.Words.Line, Color = Tok.TextSecondary, MaxLines = 1,
    };
}
