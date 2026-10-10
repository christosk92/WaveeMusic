// ── Entities/Show.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the show READER's pieces (W1-W3): the sticky word rail's body, the visit head (start here + about │ continue + up
// next + new since │ caught up), the "episodes N" header and its empty arm, the month headers, the foot, the seeds the
// skeletons derive from, the rail's publisher line and meta. The library pane's show header IS Album.PaneHeader (one
// geometry for both kinds, library rework §5.4) — the show pane calls it directly
//
// Role: UI
// Owner: P
// Wave: P3 (the podcast rework)
// Budget: 380 lines (plan §11) — landed ~650 with the visit head's three arms; the split is named in the P3 report
// Spec: podcast-show-rework-implementation.md §2 W1/W2 (the head's three first screens), §3.1, §4, §6.1 (the head waits
//       for the settled progress), §7 (doors/hero lift 150 ms, the head swap FadeOnly) · the prototype
//       podcast-show-episode-mica.html (.wordrail, .sechead h3, .doors/.door, .resume, .upnext/.mini, .group, .caught,
//       .about/.facts, .empty)
//
// ── HOW THE PIECES RE-RENDER ─────────────────────────────────────────────────────────────────────────────────────────
//
// The components here are slots of the reader's bound list, and each re-renders on a small STAMP memo of its own over
// the page's one snapshot (never on every snapshot): the head on (visit, width arm, a fold of every episode it shows,
// the tone), the header on (view, total, filtered, empty), the foot on (paging), the rail body on its width arm only —
// every word, count and the find box inside it are binds over the page's own Signal instances. Builders read the
// snapshot with Peek, so a render reads the state its stamp was computed from.
//
// NO CAPS TRANSFORM, NO LOWER-CASING OF A LOC STRING. The prototype's lowercase words are the loc values themselves; a
// month name is culture text (the topics precedent, Controls.Words.Links) and is lowered with the current culture.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>The reader's bottom reserve: the page-wide one (<see cref="PageGeometry.BottomReserve"/>), so the last row
    /// clears the player dock by the same air on every scrolling page.</summary>
    public const float BottomReserve = PageGeometry.BottomReserve;

    // ── the reader's rhythm (the prototype's .reader / .sec / .wordrail) ──
    const float ReaderPad = ShowReaderRules.Pad, ReaderPadNarrow = ShowReaderRules.PadNarrow;
    /// <summary>The sticky rail's plane (W1: 40 + the underline's air) — also the list's shared top clip
    /// (<c>ScrollOptions.ItemClipTopInset</c>, a MOUNT-TIME value). The toolbar is ONE row at every rung of
    /// <see cref="ShowToolbarLayout"/>'s collapse ladder, so these two numbers never move: nothing inside the plane
    /// may open a second line.</summary>
    const float RailHeight = ShowReaderRules.RailExtent, RailBodyHeight = 40f;
    const float SectionGap = 26f, HeadTop = 12f, GroupTopPad = ShowReaderRules.GroupAir, ItemEstimate = ShowReaderRules.RowSeed;
    const float HeroArt = 96f, HeroArtNarrow = 64f, HeroTrackMax = 420f, HeroSeedHeight = 124f;
    const float MiniArt = 40f, MiniNumeral = 34f;
    const float AboutMeasure = 620f, PivotSize = 24f, PivotLine = ShowReaderRules.HeaderLine, YearStripW = ShowReaderRules.YearStripWidth;
    const int SeedRows = 8;
    const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>A placeholder a localized template is formatted around and split at, so the small count keeps the
    /// translator's word order ("episodes {count}", "{left} left of {total}").</summary>
    const char TemplateMark = (char)1;
    static readonly string TemplateMarkText = TemplateMark.ToString();

    /// <summary>The ICU select keys <c>podcast.caughtUpNext</c> takes (lowercase English weekday).</summary>
    static readonly string[] s_dayWords = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    static readonly Action s_noop = static () => { };
    static readonly Func<bool> s_never = static () => false;
    static readonly Action<string> s_navRoute = static key => Shell.GoTo(Shell.Parse(key));
    static readonly MotionTarget s_lift = new() { OffsetY = -1f };
    /// <summary>ONE cache and ONE thunk for every month word the reader prints: the group header and the sticky month
    /// say the same thing about the same <see cref="DateKeys"/> month key, so they share the string.
    /// <para>The month name is culture text and is lowered with the CURRENT culture (the file header's rule). A key
    /// that is not a month key — the undated run's -1, a recycled slot's default, a key from anywhere else — reads "",
    /// because <see cref="DateKeys.MonthLabel"/> is TOTAL: this thunk runs on the render path and may not throw.</para></summary>
    static readonly FormatCache<int> s_monthWords = new();
    static readonly Func<int, string> s_monthWord = static key =>
        DateKeys.MonthLabel(key, CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);

    // ══ 1. THE TONE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The show's own tone when the provider stated one (<c>ShowFields.Rating</c>'s <c>Tone</c>, D-7), else
    /// the frame's cover-derived accent. Reads <paramref name="fallback"/> FIRST either way, so a consumer memo stays
    /// subscribed to the frame's accent (a theme flip re-derives it).</summary>
    static ColorF ToneOf(uint argb, ColorF fallback) => argb != 0 ? Design.Palette.ChromeFromPayload(argb) : fallback;

    /// <summary>The prototype's <c>color-mix(tone N%, transparent)</c>.</summary>
    static ColorF ToneAt(in ColorF c, float alpha) => c with { A = c.A * alpha };

    // ══ 2. TYPE ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A section head (the prototype's <c>.sechead h3</c>): Display 24/30/300, with an optional small count
    /// 13/400 tertiary on the same baseline.</summary>
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
        var d = Design.Type.DenseMeta("");
        return new(text, Weight: 400, Color: Tok.TextTertiary, Size: d.Size);
    }

    static SpanTextEl PivotOf(TextSpan[] spans) => new(spans)
    {
        FontFamily = DisplayFace, Size = PivotSize, LineHeight = PivotLine, Weight = 300, CharSpacing = -20f,
        Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>A section head row: the pivot, its tertiary sub line, and an optional trailing action pushed right.
    /// <paramref name="stack"/> puts the subtitle under the pivot so a narrow reader never ellipsizes "Continue"
    /// to "conti…" against "up next from your progress · …".</summary>
    static Element SecHead(Element title, string? sub = null, Element? trailing = null, bool stack = false)
    {
        var kids = new List<Element>(4) { title };
        if (sub is { Length: > 0 })
            kids.Add(Design.Type.DenseMeta(sub) with
            {
                Color = Tok.TextTertiary, MaxLines = stack ? 2 : 1,
                Wrap = stack ? TextWrap.Wrap : TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Shrink = 1f, Margin = new Edges4(0f, 0f, 0f, stack ? 0f : 4f),
            });
        if (trailing is not null)
        {
            kids.Add(new BoxEl { Grow = 1f, MinWidth = Spacing.M });
            kids.Add(trailing);
        }
        return new BoxEl
        {
            Direction = (byte)(stack ? 1 : 0), Gap = stack ? 2f : Spacing.M,
            AlignItems = stack ? FlexAlign.Start : FlexAlign.End, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    static Element QuietLine(string text) => new TextEl(text)
    {
        Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
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

    // ══ 3. THE RAIL BODY (item 0's content — the sticky root itself is a RAW element, Show.Page.cs) ═════════════════

    /// <summary>The reader's toolbar — ONE row, <see cref="RailHeight"/> DIP, at EVERY width. Never a horizontal
    /// <c>ScrollView</c> (the scrollbar the owner reported) and never a squeezed chip:
    /// <code>
    ///   Full         All 326  Unplayed 320  In progress 5  Played 1   [⌕ Find in this show ]  │  Newest Oldest  [✓]
    ///   FindIcon     All 326  Unplayed 320  In progress 5  Played 1   ⌕  │  Newest Oldest  [✓]
    ///   CompactSort  All 326  Unplayed 320  In progress 5  Played 1   ⌕  ⇅  [✓]
    ///   FilterMenu   [Unplayed 320 ▾]                                 ⌕  ⇅  [✓]
    ///   (find open at a collapsed stage)  the chips stay; [⌕ why ger ........ ✕] takes the sort/select area
    /// </code>
    /// The stage is <see cref="ShowToolbarLayout.For"/> over THIS row's own arranged width and what its rails measured
    /// (<c>Controls.Words.Rail</c>'s <c>onMeasured</c>), with 16 DIP of hysteresis — computed by the host's memo, so this
    /// component re-renders only when the STAGE moves (or the find toggles). Opening the collapsed find swaps the right
    /// group for the field plus a close affordance (which clears the query), so the plane's height never moves, and no arm
    /// may wrap: a second line would resize the list's mount-time clip inset. Every word, count and the box itself binds
    /// the page's own signals.</summary>
    sealed class RailBody(ReaderHost host) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var stage = host.Toolbar?.Value ?? ToolbarStage.Full;
            bool finding = host.FindOpen.Value;
            bool compact = host.Narrow?.Value ?? false;
            var m = host.Model;
            float pad = compact ? ReaderPadNarrow : ReaderPad;
            // LEFT: the chips at their natural width — or, at the last stage only, the menu that folds them.
            Element left = stage == ToolbarStage.FilterMenu
                ? FilterMenu(host, m, overlay) with { Key = "tb:left" }
                : FilterWords(host, m) with { Key = "tb:left" };
            // RIGHT: find · divider · sort · select, as far as the stage keeps them. An open find at a collapsed stage
            // takes this very area over (the chips stay).
            var right = new List<Element>(4);
            bool takeover = stage != ToolbarStage.Full && finding;
            if (takeover)
            {
                right.Add(FindField(m, fill: true));
                right.Add(RailToggle(Icons.Cancel, Loc.Get(Strings.Detail.Filter.Clear), s_never, host.CloseFind, host.Tone));
            }
            else
            {
                right.Add(stage == ToolbarStage.Full
                    ? FindField(m, fill: false)
                    : RailToggle(Icons.Search, Loc.Get(Strings.Podcast.Find), s_never, host.OpenFind, host.Tone));
                if (stage is ToolbarStage.Full or ToolbarStage.FindIcon)
                {
                    right.Add(RailDivider());
                    right.Add(Controls.Words.Rail(host.SortWords!, m.Order, host.Tone, onMeasured: host.OnSortMeasured));
                }
                else
                {
                    right.Add(SortMenu(m, host.Tone, overlay));
                }
                right.Add(RailToggle(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), host.IsSelecting, host.ToggleSelecting, host.Tone));
            }
            var group = new BoxEl
            {
                Key = "tb:right", Direction = 0, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Shrink = 0f, Children = right.ToArray(),
            };
            // The open field fills the whole area (the chips stay at their natural width on its left).
            if (takeover) group = group with { Grow = 1f, Shrink = 1f, Basis = 0f };
            // The toolbar measures ITSELF (its own arranged width, gutters included) against what its rails measured.
            return new BoxEl
            {
                Direction = 0, Height = RailBodyHeight, Gap = ShowToolbarLayout.Gap, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.SpaceBetween, MinWidth = 0f, OnBoundsChanged = host.OnToolbarBounds,
                Padding = new Edges4(pad, 0f, pad, 0f), Children = [left, group],
            };
        }
    }

    /// <summary>The filter chips at their NATURAL width — a rail that never shrinks and never scrolls (the toolbar picks a
    /// stage only when they fit; the last stage folds them into <see cref="FilterMenu"/>). It reports its measure to the
    /// toolbar's stage rule.</summary>
    static Element FilterWords(ReaderHost host, ReaderModel m)
        => Controls.Words.Rail(host.FilterWords!, m.Status, host.Tone, onMeasured: host.OnFiltersMeasured);

    /// <summary>The last stage's chips: ONE button saying the selected filter ("Unplayed 320 ▾") whose menu holds the four
    /// as radio rows. The label and its count are binds over the model — the toolbar re-renders on stages, never on a
    /// selection or a count — and the accessible name is the control's, since the label alone cannot say what it is.</summary>
    static Element FilterMenu(ReaderHost host, ReaderModel m, IOverlayService? overlay)
    {
        var tone = host.Tone;
        var box = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Height = 30f, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children =
            [
                Design.Type.DenseTitle("") with { Text = Prop.Of(() => FilterLabelOf(m, m.Status.Value)), MaxLines = 1 },
                Icon(Icons.ChevronDown, 10f) with { Color = Prop.Of(() => tone()) },
            ],
        }.Interactive(Interaction.Subtle);
        if (!Controls.IsNullOverlay(overlay)) box = box.WithContextMenu(overlay, () => FilterMenuOf(m));
        return Controls.Named(box, Loc.Get(Strings.Detail.Filter.Short));
    }

    static readonly string[] s_filterKeys =
        [Strings.Podcast.Filter.All, Strings.Podcast.Filter.Unplayed, Strings.Podcast.Filter.InProgress, Strings.Podcast.Filter.Played];

    /// <summary>"Unplayed 320": the chip's word and, once progress is trusted, its count.</summary>
    static string FilterLabelOf(ReaderModel m, int status)
    {
        string word = Loc.Get(s_filterKeys[Math.Clamp(status, 0, s_filterKeys.Length - 1)]);
        var s = m.Read();
        string count = status switch { 1 => s.CountUnplayed, 2 => s.CountProgress, 3 => s.CountPlayed, _ => s.CountAll };
        return count.Length == 0 ? word : word + " " + count;
    }

    static ContextMenuModel FilterMenuOf(ReaderModel m)
    {
        var rows = new MenuFlyoutItem[s_filterKeys.Length];
        int current = m.Status.Peek();
        for (int i = 0; i < rows.Length; i++)
        {
            int status = i;
            rows[i] = MenuFlyoutItem.RadioItem(FilterLabelOf(m, status), current == status, () => m.Status.Value = status);
        }
        return new ContextMenuModel(rows);
    }

    /// <summary>The hairline between the find box and the sort words.</summary>
    static Element RailDivider() => new BoxEl
    {
        Width = 1f, Height = 16f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
    };

    static readonly string[] s_noSuggest = [];

    /// <summary>The find box. At rest it sits at its own <see cref="Controls.FindBoxWidth"/> measure past the row's
    /// flexible spacer; the collapsed row's OPENED arm lets the same control fill the middle, so the query survives
    /// either transition and the row's height never changes.</summary>
    static Element FindField(ReaderModel m, bool fill)
        => AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Find), width: Controls.FindBoxWidth, text: m.Find,
                                 queryIcon: Icons.Search, minHeight: 32f, cornerRadius: Radii.Control,
                                 grow: fill ? 1f : 0f, maxFillWidth: fill ? 9999f : 0f);

    /// <summary>The ladder's last rung: the two sort words folded into ONE 30-DIP glyph whose menu holds them as radio
    /// rows. A menu is the narrow FALLBACK here, never the default — at <see cref="ToolbarStage.Full"/> and
    /// <see cref="ToolbarStage.FindIcon"/> the words are still words. Its accessible name is the sort in force, since
    /// the glyph alone cannot say it.</summary>
    static Element SortMenu(ReaderModel m, Func<ColorF> tone, IOverlayService? overlay)
    {
        var order = m.Order;
        int current = order.Value;                                  // subscribe: the button is named after the sort in force
        var box = new BoxEl
        {
            Width = 30f, Height = 30f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            ClickRequestsContext = true,
            Children = [Icon(Icons.Sort, 14f) with { Color = Prop.Of(() => tone()) }],
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

    /// <summary>A toolbar glyph toggle (the search collapse, the select arm): 30 square, the tone while ON.</summary>
    static Element RailToggle(string glyph, string name, Func<bool> on, Action click, Func<ColorF> tone)
        => Controls.Named(new BoxEl
        {
            Width = 30f, Height = 30f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
            Children = [Icon(glyph, 14f) with { Color = Prop.Of(() => on() ? tone() : Tok.TextSecondary) }],
        }.Interactive(Interaction.Subtle), name);

    // ══ 4. THE VISIT HEAD (item 1) ═══════════════════════════════════════════════════════════════════════════════════

    /// <param name="Shown">Is the head painted (false in results mode — the box is then empty, mounted at the same list
    /// position, and its height corrects to 0)?</param>
    /// <param name="Doors">The door and up-next column counts (<see cref="ShowReaderRules.HeadColumns"/>): the head
    /// re-renders when they change, never per resize frame.</param>
    readonly record struct HeadStamp(ShowReaderRules.Head Kind, bool Narrow, ulong Fold, ColorF Tone, bool Shown, int Doors, int Minis);

    /// <summary>The first screen the visitor needs (W1/W2): New → start-here doors + the full about; Returning → the
    /// continue hero, up next, new since you were here; CaughtUp → one line with the usual release day; Unavailable →
    /// the hydrate failed and nothing local says otherwise. Until the membership answered AND progress settled it is a
    /// hero-height shimmer (§6.1: never flash New at a Returning listener), revealed with a plain fade (§7).</summary>
    sealed class VisitHead : Component
    {
        readonly Func<HeadStamp> _stamp;
        readonly Func<bool> _pending;
        readonly Func<Element> _content;

        public VisitHead(ReaderHost host)
        {
            _stamp = () =>
            {
                var s = host.Model.Read();
                var (doors, minis) = ShowReaderRules.HeadColumns(host.MeasuredWidth(subscribe: true));
                return new HeadStamp(s.Head, host.Narrow?.Value ?? false, s.HeadFold, host.Tone(), s.HeadShown, doors, minis);
            };
            _pending = () => host.Model.Read().Head == ShowReaderRules.Head.Pending;
            _content = () => HeadBody(host);
        }

        public override Element Render()
        {
            var stamp = UseComputed(_stamp).Value;
            // Results mode: nothing painted, the slot stays mounted (and keeps its place in the list).
            if (!stamp.Shown) return new BoxEl();
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, HeadTop, 0f, SectionGap),
                Children =
                [
                    new SkelRegionEl(Pending: _pending, Failed: s_never, Content: _content, ShimmerSource: s_headSeed,
                        OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
                ],
            };
        }
    }

    static readonly Func<Element> s_headSeed = static () => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f,
        Children =
        [
            new BoxEl { Width = 160f, Height = PivotLine, Corners = CornerRadius4.All(4f) },
            new BoxEl { Height = HeroSeedHeight, Corners = Radii.CardAll },
        ],
    };

    static Element HeadBody(ReaderHost h)
    {
        var s = h.Model.Peek();
        bool narrow = h.Narrow?.Peek() ?? false;
        return s.Head switch
        {
            ShowReaderRules.Head.New => NewHead(s, h, narrow),
            ShowReaderRules.Head.Returning => ReturningHead(s, h, narrow),
            ShowReaderRules.Head.CaughtUp => CaughtUpHead(s),
            ShowReaderRules.Head.Unavailable => QuietLine(Loc.Get(Strings.Podcast.ProgressUnavailable)),
            _ => s_headSeed(),
        };
    }

    /// <summary>W2: start here (up to three doors — a serial leads with episode 1, anything else with the latest; the
    /// trailer joins when there is one) and the full about with the facts line.</summary>
    static Element NewHead(ReaderSnap s, ReaderHost h, bool narrow)
    {
        bool serial = s.Order == ConsumptionOrder.Sequential;
        // Episode 1 is a door only when the membership is COMPLETE — the oldest resident of a partial list is not it.
        Element? first = s.FirstSlot > 0 && s.FirstSlot != s.LatestSlot
            ? EpisodeDoor(new Episode(s.FirstSlot), Loc.Get(serial ? Strings.Podcast.BeginHere : Strings.Podcast.WhereItBegan),
                          lead: serial, dated: false, h)
            : null;
        Element? latest = s.LatestSlot > 0
            ? EpisodeDoor(new Episode(s.LatestSlot), Loc.Get(Strings.Podcast.Latest), lead: !serial || first is null, dated: true, h)
            : null;
        Element? trailer = s.TrailerSlot > 0 && new Episode(s.TrailerSlot).Knows(EpisodeFields.Title) ? TrailerDoor(new Episode(s.TrailerSlot), h) : null;

        Element?[] order = serial ? [first, trailer, latest] : [latest, trailer, first];
        var doors = new List<Element>(3);
        foreach (var door in order)
            if (door is not null) doors.Add(door);

        var sections = new List<Element>(2);
        if (doors.Count > 0)
            sections.Add(Column(
            [
                SecHead(Pivot(Loc.Get(Strings.Podcast.StartHere)), Loc.Get(serial ? Strings.Podcast.SerialHint : Strings.Podcast.EpisodicHint), stack: narrow),
                EqualRows(doors, ShowReaderRules.HeadColumns(h.MeasuredWidth(subscribe: false)).Doors),
            ], Spacing.M));
        sections.Add(About(s, h));
        return Column(sections, SectionGap);
    }

    static Element EpisodeDoor(Episode e, string label, bool lead, bool dated, ReaderHost h)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        string length = e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = dated && e.Knows(EpisodeFields.Published) ? Joined(Episode.DateLabel(e.PublishedAt), length) : length;
        var model = h.Model;
        return Controls.Door(new DoorData(label, title, DescriptionOf(e), meta, lead, n > 0 ? FormatCache.Int(n) : null,
            Play: () => Episode.Invoke(e, () => model.PlayInShow(e)), Open: () => Episode.OpenPage(e), Tone: h.Tone,
            Menu: MenuOf(h, e), PlayUri: e.Uri.Text), OverlayOf(h));
    }

    /// <summary>The trailer door: its own row (outside the membership), played alone — not as the show context.</summary>
    static Element TrailerDoor(Episode t, ReaderHost h)
        => Controls.Door(new DoorData(Loc.Get(Strings.Podcast.Trailer), t.Title, DescriptionOf(t),
            t.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(t.DurationMs) : null, Lead: false, Numeral: null,
            Play: () => Episode.Invoke(t, () => Playback.PlayContext(t.Id)), Open: () => Episode.OpenPage(t), Tone: h.Tone,
            Menu: MenuOf(h, t), PlayUri: t.Uri.Text), OverlayOf(h));

    /// <summary>The head's episode menu — the SAME one a list row attaches (<see cref="ReaderHost.HeadRowCtx"/>'s
    /// <c>Menu</c>, built at OPEN time), for a head surface that owns its own right-click (a door, the hero, an up-next
    /// card). Null without a menu seam.</summary>
    static Func<ContextMenuModel?>? MenuOf(ReaderHost h, Episode e)
        => h.HeadRowCtx is { Menu: { } menu } ? () => menu(e) : null;

    /// <summary>The overlay service the head's menus open through (null under the null overlay — then nothing attaches).</summary>
    static IOverlayService? OverlayOf(ReaderHost h) => h.HeadRowCtx?.Overlay;

    /// <summary><paramref name="el"/> with the head's episode menu attached to it (a right-click anywhere on the card).</summary>
    static Element WithMenu(ReaderHost h, Episode e, BoxEl el)
        => MenuOf(h, e) is { } menu && OverlayOf(h) is { } overlay ? ContextMenu.Attach(el, overlay, menu) : el;

    static string? DescriptionOf(Episode e)
        => e.IsValid && e.Knows(EpisodeFields.About) && !e.DescriptionId.IsEmpty ? Entities.Strings.Resolve(e.DescriptionId) : null;

    /// <summary>W2's about: the full description at a reading measure (links route in-app) and the facts line.</summary>
    static Element About(ReaderSnap s, ReaderHost h)
    {
        var show = s.Show;
        var kids = new List<Element>(3) { SecHead(Pivot(Loc.Get(Strings.Podcast.About))) };
        if (show.IsValid && show.Knows(ShowFields.About) && (!show.DescriptionId.IsEmpty || !show.HtmlDescriptionId.IsEmpty))
            kids.Add(new BoxEl
            {
                Direction = 1, MaxWidth = AboutMeasure, MinWidth = 0f,
                Children = [Controls.RichTextFlex(Entities.Strings.Resolve(!show.HtmlDescriptionId.IsEmpty ? show.HtmlDescriptionId : show.DescriptionId), 14f, Tok.TextSecondary, h.Tone(), 0, s_navRoute)],
            });
        kids.Add(Design.Type.DenseMeta(MetaOf(s.Total, s.Cadence, s.OldestYear)) with
        {
            Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
        });
        return Column(kids, Spacing.M);
    }

    /// <summary>W1: continue (the hero on listen-next's resume, the up-next minis) and new since you were here.</summary>
    static Element ReturningHead(ReaderSnap s, ReaderHost h, bool narrow)
    {
        var l = s.Ledger;
        var cont = new List<Element>(3)
        {
            SecHead(Pivot(Loc.Get(Strings.Podcast.Continue)),
                    Loc.Get(Strings.Podcast.UpNextFromProgress) + " · " + Strings.Podcast.Ledger(l.Played, l.InProgress, l.ToGo),
                    stack: narrow),
        };
        if (s.ResumeSlot > 0) cont.Add(Hero(new Episode(s.ResumeSlot), h, narrow));
        if (s.UpNext.Length > 0)
        {
            var episodes = new Episode[s.UpNext.Length];
            for (int i = 0; i < episodes.Length; i++) episodes[i] = new Episode(s.UpNext[i]);
            var lead = MiniLeadOf(episodes);
            var minis = new List<Element>(episodes.Length);
            for (int i = 0; i < episodes.Length; i++) minis.Add(Mini(episodes[i], lead, h));
            cont.Add(EqualRows(minis, ShowReaderRules.HeadColumns(h.MeasuredWidth(subscribe: false)).Minis));
        }
        var sections = new List<Element>(2);
        // New since can be all there is to come back to (up next never repeats it): then there is no "continue" section.
        if (cont.Count > 1) sections.Add(Column(cont, Spacing.M));
        if (s.Fresh.Length > 0) sections.Add(NewSince(s, h, narrow));
        return Column(sections, SectionGap);
    }

    /// <summary>The lead for the up-next set (<see cref="ShowReaderRules.MiniLead"/>), from what is KNOWN of its
    /// episodes: one that has not loaded yet is neutral, so a cold head does not decide on missing data (the head rebuilds
    /// as each lands — its fold carries every episode's version — and the placeholders reserve the lead decided so far).</summary>
    static ShowReaderRules.MiniLeadKind MiniLeadOf(Episode[] episodes)
    {
        bool numbered = true, art = true;
        foreach (var e in episodes)
        {
            if (!e.IsValid || !e.Knows(EpisodeFields.Title)) continue;
            numbered &= e.Number > 0;
            art &= e.Knows(EpisodeFields.Image) && Controls.ArtUrl(e.ImageId) is { Length: > 0 };
        }
        return ShowReaderRules.MiniLead(numbered, art);
    }

    /// <summary>The continue hero, behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once the
    /// episode's identity is there, a card-height shimmer while it is coming, "unavailable · retry" when its ask failed —
    /// never a card with an empty title. The owner rebuilds the head when the episode's version moves (HeadFold), so the
    /// fixed handle here is enough.</summary>
    static Element Hero(Episode e, ReaderHost h, bool narrow)
        => Episode.Reveal(e, () => HeroCard(e, h, narrow), s_heroSeed, () => HeroFailed(e));

    static readonly Func<Element> s_heroSeed = static () => new BoxEl { Height = HeroSeedHeight, Corners = Radii.CardAll };

    static Element HeroFailed(Episode e) => new BoxEl
    {
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = Edges4.All(14f),
        Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(Loc.Get(Strings.Podcast.Reader.Unavailable)) { Size = 14f, LineHeight = 19f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Basis = 0f },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)),
        ],
    };

    /// <summary>The continue hero's card: 96 art · the title · "17 min" Display 30/300 in the tone over "left of 31 min"
    /// · a 4-DIP track ≤ 420 · Resume. The card opens the episode (the row rule); the pill resumes it.</summary>
    static Element HeroCard(Episode e, ReaderHost h, bool narrow)
    {
        Func<ColorF> tone = h.Tone;
        ColorF t = tone();
        var model = h.Model;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, e.Number) : "";
        string line = Strings.Podcast.LeftOf(TemplateMarkText, Episode.DurationLabel(e.DurationMs));
        int at = line.IndexOf(TemplateMark);
        // "17 min left of 31 min": the minutes follow the PLAYBACK clock like a list row's (Episode.PlaybackProgressOf —
        // the playing episode reads the player's position), through ONE reused span buffer; a fire that says the same
        // thing re-shapes nothing. A template with no mark has nothing to count down.
        string before = at > 0 ? line[..at] : "", after = at >= 0 && at + 1 < line.Length ? line[(at + 1)..] : "";
        var buffer = new SpanBuffer();
        Prop<TextSpans> spans = at < 0
            ? (TextSpans)new[] { new TextSpan(line) }
            : Prop.Of(() =>
            {
                buffer.Clear();
                if (before.Length > 0) buffer.Add(new TextSpan(before, Weight: 400, Color: Tok.TextSecondary, Size: 12.5f));
                buffer.Add(new TextSpan(s_leftWords.Get(Episode.PlaybackProgressOf(e).LeftMinutes, s_leftWordsFormat)));
                if (after.Length > 0) buffer.Add(new TextSpan(after, Weight: 400, Color: Tok.TextSecondary, Size: 12.5f));
                return buffer.Current;
            });
        var copy = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                new TextEl(title)
                {
                    Size = 17f, LineHeight = 22f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new SpanTextEl(spans)
                {
                    FontFamily = DisplayFace, Size = 30f, LineHeight = 34f, Weight = 300, CharSpacing = -20f, Color = t,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl { Direction = 1, MaxWidth = HeroTrackMax, MinWidth = 0f, Children = [HeroRule(e, tone)] },
            ],
        };
        float edge = narrow ? HeroArtNarrow : HeroArt;
        var cover = new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(6f), ClipToBounds = true,
            Children = [Controls.Artwork(e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null, edge, edge, 6f)],
        };
        Element pill = new BoxEl
        {
            Shrink = 0f,
            Children = [Detail.PlayButton(tone, () => Episode.Invoke(e, () => model.PlayInShow(e)), Loc.Get(Strings.Podcast.Resume))],
        };
        Element body = narrow
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy] }, pill],
            }
            : new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy, pill] };
        return WithMenu(h, e, new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = Edges4.All(14f), Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Gradient = new GradientSpec(GradientShape.Linear, 10f, [new GradientStop(0f, ToneAt(t, 0.30f)), new GradientStop(0.75f, Tok.FillCardDefault)]),
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Episode.OpenPage(e),
            WhileHover = s_lift, Transition = MotionTok.ControlFast,
            Children = [body],
        });
    }

    /// <summary>The hero's 4-DIP track in the tone: a fill whose compositor scale follows the PLAYBACK clock
    /// (<see cref="Episode.PlaybackProgressOf"/>), like a list row's rule — the persisted progress it used to be built from
    /// stood still while the episode played.</summary>
    static Element HeroRule(Episode e, Func<ColorF> tone) => new BoxEl
    {
        Direction = 0, Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleTertiary,
        ClipToBounds = true, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = Prop.Of(tone), TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Episode.PlaybackProgressOf(e).Pct), 1f)),
            },
        ],
    };

    static readonly FormatCache<int> s_leftWords = new();
    static readonly Func<int, string> s_leftWordsFormat = static minutes => Episode.DurationWords(minutes);

    /// <summary>An up-next mini card behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once
    /// the episode's identity is there, a chip-shaped shimmer while it is coming, "unavailable · retry" when its ask
    /// failed — an unresolved chip is never an empty plate. All three reserve the SAME <paramref name="lead"/>
    /// (<see cref="ShowReaderRules.MiniLead"/>), so the reveal cross-dissolves in place.</summary>
    static Element Mini(Episode e, ShowReaderRules.MiniLeadKind lead, ReaderHost h)
        => Episode.Reveal(e, () => MiniCard(e, lead, h), () => MiniPlate(lead, "", null, s_miniSeedTitle, s_miniSeedMeta, trailing: null, onClick: null),
                          () => MiniFailed(e, lead));

    /// <summary>U+2007 FIGURE SPACE runs: the derived shimmer draws a bar only for a run that MEASURES (the row's own
    /// seed idiom, Episode.UI.cs).</summary>
    static readonly string s_miniSeedTitle = new((char)0x2007, 18), s_miniSeedMeta = new((char)0x2007, 10);

    static Element MiniFailed(Episode e, ShowReaderRules.MiniLeadKind lead)
        => MiniPlate(lead, "", null, Loc.Get(Strings.Podcast.Reader.Unavailable), "",
                     trailing: Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)), onClick: null);

    /// <summary>The up-next mini card: its lead (the numeral 22/300, or the 40 × 40 cover) · the title 13/600 on one line ·
    /// "28 min · Sep 15" (or "N min left" when started). Without a numeral lead the number rides the meta line
    /// ("#35 · 32 min · Sep 27"), like a list row's. Opens the episode; right-click is the episode menu.</summary>
    static Element MiniCard(Episode e, ShowReaderRules.MiniLeadKind lead, ReaderHost h)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        bool started = Episode.Rules.InProgress(Episode.ReaderPctOf(e));
        string length = started ? Episode.LeftLabel(e.ProgressMs, e.DurationMs)
                      : e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = Joined(length, e.Knows(EpisodeFields.Published) ? Episode.DateLabel(e.PublishedAt) : "");
        if (lead != ShowReaderRules.MiniLeadKind.Numeral && n > 0) meta = Joined("#" + FormatCache.Int(n), meta);
        string? art = e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
        return MiniPlate(lead, n > 0 ? FormatCache.Int(n) : "", art, title, meta, trailing: null, onClick: () => Episode.OpenPage(e), h, e);
    }

    /// <summary>The mini's ONE plate — the card, its seed twin and its failure arm are the same geometry, so the reveal
    /// cross-dissolves in place. A plate with no <paramref name="onClick"/> is not a link. A (B) surface (lead · title ·
    /// meta): it takes only the shared rules, the hand and the tab stop of the click owner it is, and a tooltip on the
    /// title only while that is cut. The lead column exists only when <paramref name="lead"/> says so — never a blank
    /// reserved gutter.</summary>
    static Element MiniPlate(ShowReaderRules.MiniLeadKind lead, string numeral, string? artUrl, string title, string meta,
                             Element? trailing, Action? onClick, ReaderHost? host = null, Episode episode = default)
    {
        var titleText = Design.Type.DenseTitle(title) with
        {
            Color = onClick is null ? Tok.TextSecondary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(3);
        if (lead == ShowReaderRules.MiniLeadKind.Numeral)
            kids.Add(new BoxEl
            {
                Width = MiniNumeral, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                Children = [new TextEl(numeral) { FontFamily = DisplayFace, Size = 22f, LineHeight = 28f, Weight = 300, Color = Tok.TextTertiary, MaxLines = 1 }],
            });
        else if (lead == ShowReaderRules.MiniLeadKind.Art)
            kids.Add(new BoxEl
            {
                Width = MiniArt, Height = MiniArt, Shrink = 0f, Corners = CornerRadius4.All(6f), ClipToBounds = true,
                Children = [Controls.Artwork(artUrl, MiniArt, MiniArt, 6f)],
            });
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                // Only a live card tips (the seed and the failure arm are not links, and their title is no name).
                onClick is null ? titleText : Controls.TrimmedTitle(titleText, title),
                Design.Type.MicroMeta(meta) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        });
        if (trailing is not null) kids.Add(trailing);
        var plate = new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(10f, 8f, 10f, 8f),
            Corners = CornerRadius4.All(6f), Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children = kids.ToArray(),
        };
        if (onClick is null) return plate;
        // The hand and the tab stop are the surface rule's; the role stays Hyperlink (the card navigates to the episode).
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        var link = plate with
        {
            HoverFill = Tok.FillSubtleSecondary, Role = AutomationRole.Hyperlink, Focusable = mode.OwnsFocus,
            Cursor = SurfaceRules.Cursor(in mode), OnClick = onClick,
        };
        return host is null ? link : WithMenu(host, episode, link);
    }

    /// <summary>"new since you were here  2" · mark all played — then the fresh episodes as reader rows (the SAME
    /// template over a fixed scope, so they are the list's pixels).</summary>
    static Element NewSince(ReaderSnap s, ReaderHost h, bool narrow)
    {
        var link = new BoxEl
        {
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = h.Model.MarkFreshPlayed,
            Children = [Design.Type.DenseTitle(Loc.Get(Strings.Podcast.MarkAllPlayed)) with { Color = h.Tone(), MaxLines = 1 }],
        };
        var kids = new List<Element>(s.Fresh.Length + 1)
        {
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M),
                Children = [SecHead(Pivot(Loc.Get(Strings.Podcast.NewSince), FormatCache.Int(s.Fresh.Length)), null, link)],
            },
        };
        for (int i = 0; i < s.Fresh.Length; i++)
            kids.Add(Episode.ReaderRow(Episode.Fixed(Episode.RowItem.Of(new Episode(s.Fresh[i]),
                Episode.RowMarks.Fresh | (i == 0 ? Episode.RowMarks.NoRule : Episode.RowMarks.None))), h.HeadRowCtx!, narrow));
        return Column(kids, 0f);
    }

    /// <summary>"you're all caught up" and, when the cadence names one, the day new episodes usually land.</summary>
    static Element CaughtUpHead(ReaderSnap s)
    {
        var kids = new List<Element>(2) { Pivot(Loc.Get(Strings.Podcast.CaughtUp)) };
        if (s.Day is { } day)
            kids.Add(Design.Type.DenseMeta(Strings.Podcast.CaughtUpNext(s_dayWords[(int)day])) with
            {
                Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
            });
        return Column(kids, Spacing.XS);
    }

    // ══ 5. THE HEADER (item 2), THE MONTHS, THE FOOT ═════════════════════════════════════════════════════════════════

    readonly record struct HeaderStamp(int View, int Total, bool Filtered, bool IsEmpty, bool EmptyShow);

    /// <summary>"episodes 128" (or "episodes 3 of 128" under a filter or a find) and the empty arm under it: the show's
    /// own words for a show with no episodes, "nothing matches" (a tap resets the view) for an empty filter.</summary>
    sealed class EpisodesHeader : Component
    {
        readonly ReaderHost _host;
        readonly Func<HeaderStamp> _stamp;

        public EpisodesHeader(ReaderHost host)
        {
            _host = host;
            _stamp = () =>
            {
                var s = host.Model.Read();
                return new HeaderStamp(s.ViewCount, s.Total, s.Filtered, s.IsEmpty, s.EmptyShow);
            };
        }

        public override Element Render()
        {
            var st = UseComputed(_stamp).Value;
            string count = st.Filtered ? Strings.Podcast.CountOf(FormatCache.Int(st.View), FormatCache.Int(st.Total)) : FormatCache.Int(st.Total);
            // The pivot's line is STATED (30): the header's extent is arithmetic (ShowReaderRules.HeaderExtent) until it
            // carries the empty arm, and ReaderHost.CheckExtent holds the two numbers to each other.
            var kids = new List<Element>(2) { PivotTemplate(Strings.Podcast.EpisodesCount(TemplateMarkText), count) with { Height = PivotLine } };
            if (st.IsEmpty) kids.Add(EmptyArm(st.EmptyShow, _host.ResetView));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, ShowReaderRules.HeaderFoot), Children = kids.ToArray() };
        }
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
                    FontFamily = DisplayFace, Size = 20f, LineHeight = 26f, Weight = 300, Color = Tok.TextPrimary, MaxLines = 1,
                },
                new BoxEl
                {
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = reset, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Podcast.NothingMatchesHint))
                        {
                            Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary,
                            Wrap = TextWrap.Wrap, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>A month header (the prototype's <c>.group</c>): Display 18/300, the culture's month name lowered,
    /// 14 above it — none right under the "episodes" head.</summary>
    static Element GroupLabel(BoundItemScope<ReaderItem> item) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            new BoxEl { Height = item.Value(static it => (it.Row.Marks & Episode.RowMarks.NoRule) != 0 ? 0f : GroupTopPad) },
            new TextEl(item.Text(static it => it.GroupKey, s_monthWords, s_monthWord))
            {
                FontFamily = DisplayFace, Size = 18f, LineHeight = ShowReaderRules.GroupLine, Height = ShowReaderRules.GroupLine,
                Weight = 300, CharSpacing = -10f, Color = Tok.TextTertiary, MaxLines = 1,
            },
            new BoxEl { Height = ShowReaderRules.GroupFoot },
        ],
    };

    // ══ 5b. THE DATE RAIL (report 4b): the sticky month and the year strip ═══════════════════════════════════════════
    //
    // The Albums list's A–Z navigation, for dates — the same two shells (`User.StickyLetter` + `User.JumpStrip`) over
    // the same kernel (`JumpIndex`, through `ShowDateIndex`). Months are the headers; the STRIP shows years, because a
    // decade of months is a scrollbar, not a strip.

    static readonly FormatCache<int> s_years = new();
    static readonly Func<int, string> s_yearFormat = static y => y <= 0 ? "" : y.ToString(CultureInfo.InvariantCulture);

    /// <summary>The month pinned at the list's top — the twin of <c>User.StickyLetter</c>: always mounted, transparent
    /// until a month is actually under the top edge (a presence flip would relayout the overlay), and inset by the
    /// rail's own height so it sits UNDER the sticky toolbar, never behind it.
    /// <para><paramref name="monthKey"/> is the item's <see cref="DateKeys"/> month key — THE same key the group header
    /// and the year strip read. It is transparent for anything that is not one, and prints "" rather than throwing.</para></summary>
    static Element StickyMonth(IReadSignal<int> monthKey, float pad) => new BoxEl
    {
        // Shrink-wrap like User.StickyLetter: a ZStack sibling without a height stretches, and the inner
        // FillCardDefault then paints a white sheet over the episode list (the "September 2026" overlay).
        Key = "rd:sticky-month",
        Height = RailHeight + Spacing.XS + StickyMonthPlateH,
        Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false, Shrink = 0f,
        Padding = new Edges4(pad, RailHeight + Spacing.XS, pad, 0f),
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
                    new TextEl(Prop.Of(() => s_monthWords.Get(monthKey.Value, s_monthWord)))
                    {
                        Size = Design.Type.MicroMeta("").Size, LineHeight = Design.Type.MicroMeta("").LineHeight,
                        Weight = 600, Color = Tok.TextTertiary, MaxLines = 1,
                    },
                ],
            },
        ],
    };

    const float StickyMonthPlateH = 20f;

    /// <summary>The year strip on the reader's right edge — the twin of <c>User.JumpStrip</c>: one 26 × 15 row per year
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
                Width = YearStripW, Height = 15f, ZStack = true, Corners = CornerRadius4.All(3f),
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
            Direction = 1, Width = YearStripW, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, RailHeight, Spacing.XS, BottomReserve), Children = rows,
        };
    }

    /// <summary>The strip's column when the view holds no more than one year (nothing to jump between): the SAME 30 DIP,
    /// empty. Same key as the strip, so the two reconcile in place and the list never changes width with the filter.</summary>
    static Element YearStripReserve() => new BoxEl { Key = "rd:years", Direction = 1, Width = YearStripW, Shrink = 0f };

    static TextEl YearInk(string text, ushort weight, Prop<ColorF> ink) => Design.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    // ══ 5c. THE RAIL'S PRIMARY BUTTON (reports 11c/11d) ══════════════════════════════════════════════════════════════
    //
    // Workstream B deleted the bespoke pill: both owner divergences from Detail.PlayButton are now plain arguments to
    // Controls.PrimaryButton —
    //   11c — the APP ACCENT, never the show's tone: callers pass Tok.AccentDefault explicitly instead of the page tone.
    //   11d — the label WRAPS to two lines instead of clipping ("Resume · 2 hr 43 min left" outruns a 280-DIP rail):
    //         Controls.PrimaryButton's `wrap: true` sets PartLabel's MaxLines to 2 on the SAME stock 32/r4 button every
    //         other primary uses, rather than a hand-rolled ramp that had to shadow its palette/geometry by hand.

    readonly record struct FootStamp(bool CanLoadMore, bool Paging);

    /// <summary>The list's end: the load-more pill above the dock reserve while the membership pages (a pill, never an
    /// infinite-scroll sentinel), else the reserve alone.</summary>
    sealed class ReaderFoot : Component
    {
        readonly ReaderHost _host;
        readonly Func<FootStamp> _stamp;

        public ReaderFoot(ReaderHost host)
        {
            _host = host;
            _stamp = () =>
            {
                var s = host.Model.Read();
                return new FootStamp(s.CanLoadMore, s.Paging);
            };
        }

        public override Element Render()
        {
            var st = UseComputed(_stamp).Value;
            var children = new List<Element>();
            if (st.CanLoadMore) children.Add(LoadMorePill(st.Paging, _host.Model.LoadMore));
            // The request is the READER's (ReaderHost.Similar): this foot is remounted whenever the list's items move, and
            // a request it owned went out again each time.
            children.Add(Embed.Comp(new PodcastReaderUI.SimilarProps(_host.Similar), static () => new PodcastReaderUI.SimilarShelf()));
            return new BoxEl
            {
                Direction = 1, MinWidth = 0, Gap = Spacing.L,
                Padding = new Edges4(Episode.RowPadX, Spacing.M, 0, BottomReserve), Children = children.ToArray(),
            };
        }
    }

    /// <summary>The standard (never accent) 32/r4 button. While a page is out the label reads "Loading…" and a tap is a
    /// no-op. <c>// Workstream B</c>: was <c>Controls.Pill</c>'s capsule.</summary>
    static Element LoadMorePill(bool paging, Action page)
        => Button.Create(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore), paging ? s_noop : page,
                         ButtonAppearance.Standard);

    // ══ 6. THE SEED, THE RAIL'S LINES ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cold skeleton's SOURCE: the real filter words, the "episodes" head and eight seed rows — the list's
    /// region derives its shimmer from this one tree. No head (it has its own), no pill, no progress.</summary>
    static Element SeedList(bool narrow)
    {
        float pad = narrow ? ReaderPadNarrow : ReaderPad;
        var kids = new Element[SeedRows + 2];
        kids[0] = new BoxEl
        {
            Direction = 0, Height = RailHeight, Gap = Controls.Words.Gap, AlignItems = FlexAlign.Center,
            Children = [SeedWord(Strings.Podcast.Filter.All), SeedWord(Strings.Podcast.Filter.Unplayed),
                        SeedWord(Strings.Podcast.Filter.InProgress), SeedWord(Strings.Podcast.Filter.Played)],
        };
        kids[1] = new BoxEl { Direction = 1, Padding = new Edges4(0f, HeadTop, 0f, Spacing.M), Children = [Pivot(Loc.Get(Strings.Podcast.Episodes))] };
        for (int i = 0; i < SeedRows; i++) kids[i + 2] = Episode.SeedReaderRow(narrow);
        return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, BottomReserve), Children = kids };
    }

    static TextEl SeedWord(string key) => new(Loc.Get(key))
    {
        Size = Controls.Words.Size, LineHeight = Controls.Words.Line, Color = Tok.TextSecondary, MaxLines = 1,
    };

    /// <summary>The rail's publisher line (the Attribution slot — the publisher left the meta line, P2-M): 13/18
    /// secondary, one line, at the rail's measure.</summary>
    static Element PublisherLine(Show show, float width)
    {
        string text = show.IsValid && show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty
            ? Entities.Strings.Resolve(show.PublisherId) : "";
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(width) ? width : AboutMeasure,
            Children = [Design.Type.DenseMeta(text) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
        };
    }

    /// <summary>The meta line: "128 episodes · weekly · since 2023" — the cadence only when the rule names one, the
    /// year only when the membership is complete (the oldest resident is then the first).</summary>
    internal static string MetaOf(int total, ShowCadence.Kind cadence, int sinceYear)
    {
        string meta = Strings.Podcast.EpisodeCount(total);
        string word = cadence switch
        {
            ShowCadence.Kind.Daily => Loc.Get(Strings.Podcast.Cadence.Daily),
            ShowCadence.Kind.Weekly => Loc.Get(Strings.Podcast.Cadence.Weekly),
            ShowCadence.Kind.Fortnightly => Loc.Get(Strings.Podcast.Cadence.Fortnightly),
            ShowCadence.Kind.Monthly => Loc.Get(Strings.Podcast.Cadence.Monthly),
            _ => "",
        };
        meta = Joined(meta, word);
        return sinceYear > 0 ? Joined(meta, Strings.Podcast.Since(sinceYear.ToString(CultureInfo.InvariantCulture))) : meta;
    }
}
