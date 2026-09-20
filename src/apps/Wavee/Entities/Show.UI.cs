// ── Entities/Show.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the show READER's pieces (W1-W3): the sticky word rail's body, the visit head (start here + about │ continue + up
// next + new since │ caught up), the "episodes N" header and its empty arm, the month headers, the foot, the seeds the
// skeletons derive from, the rail's publisher line and meta — plus PaneHeader, the library pane's 128-cover show
// header (the twin of Album.PaneHeader, library rework §5.4)
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
    /// <summary>The reader's bottom reserve: the last row clears the player dock by 24 (ch 09 item 11).</summary>
    public const float BottomReserve = Design.Dock.Reserve + Spacing.XXL;

    // ── the reader's rhythm (the prototype's .reader / .sec / .wordrail) ──
    const float ReaderPad = Spacing.XXL, ReaderPadNarrow = Spacing.L;
    /// <summary>The sticky rail's plane (W1: 40 + the underline's air) — also the list's shared top clip
    /// (<c>ScrollOptions.ItemClipTopInset</c>, a MOUNT-TIME value). The toolbar is ONE row at every rung of
    /// <see cref="ShowToolbarLayout"/>'s collapse ladder, so these two numbers never move: nothing inside the plane
    /// may open a second line.</summary>
    const float RailHeight = 48f, RailBodyHeight = 40f;
    const float SectionGap = 26f, HeadTop = 12f, GroupTopPad = 14f, ItemEstimate = 96f;
    const float HeroArt = 96f, HeroArtNarrow = 64f, HeroTrackMax = 420f, HeroSeedHeight = 124f, MiniMin = 220f;
    const float AboutMeasure = 620f, PivotSize = 24f, PivotLine = 30f;
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

    /// <summary>The prototype's <c>repeat(auto-fit, minmax(N, 1fr))</c>: a wrapping row whose tiles grow from N.</summary>
    static Element Tiles(List<Element> tiles, float min)
    {
        var kids = new Element[tiles.Count];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = new BoxEl { Direction = 1, Grow = 1f, Basis = min, MinWidth = min, Children = [tiles[i]] };
        return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.M, MinWidth = 0f, Children = kids };
    }

    static string Joined(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    // ══ 3. THE RAIL BODY (item 0's content — the sticky root itself is a RAW element, Show.Page.cs) ═════════════════

    /// <summary>The reader's toolbar — ONE row, <see cref="RailHeight"/> DIP, at EVERY width. Never a horizontal
    /// <c>ScrollView</c> (the scrollbar the owner reported) and never a measure-and-classify pass over the labels:
    /// <code>
    ///   Full         all 128  unplayed 41  in progress 3  played 84  ······  [find …]  │  newest oldest  [select]
    ///   FindIcon     all 128  unplayed 41  in progress 3  played 84  ······  [search]  │  newest oldest  [select]
    ///   CompactSort  all 128  unplayed 41  in progress 3  played 84  ······  [search] [sort ▾] [select]
    ///   (find open)  [ find … ..................................................................]  [close]
    /// </code>
    /// The stage is <see cref="ShowToolbarLayout.Of"/> over the rail's measured width — two named breakpoints, nothing
    /// else. Opening the collapsed find swaps the row's MIDDLE for the field plus a close affordance (which clears the
    /// query), so the plane's height never moves, and no arm may wrap: a second line would resize the list's mount-time
    /// clip inset. Re-renders on the stage and the find toggle (and, at <see cref="ToolbarStage.CompactSort"/> only, the
    /// sort order that names its menu button); every word, count and the box itself binds the page's own signals.</summary>
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
            var kids = new List<Element>(6);
            if (stage != ToolbarStage.Full && finding)
            {
                kids.Add(FindField(m, fill: true));
                kids.Add(RailToggle(Icons.Cancel, Loc.Get(Strings.Detail.Filter.Clear), s_never, host.CloseFind, host.Tone));
            }
            else
            {
                kids.Add(FilterWords(host, m));
                kids.Add(new BoxEl { Grow = 1f, Shrink = 0f, MinWidth = Spacing.S });
                kids.Add(stage == ToolbarStage.Full
                    ? FindField(m, fill: false)
                    : RailToggle(Icons.Search, Loc.Get(Strings.Podcast.Find), s_never, host.OpenFind, host.Tone));
                if (stage == ToolbarStage.CompactSort)
                {
                    kids.Add(SortMenu(m, host.Tone, overlay));
                }
                else
                {
                    kids.Add(RailDivider());
                    kids.Add(Controls.Words.Rail(host.SortWords!, m.Order, host.Tone));
                }
                kids.Add(RailToggle(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), host.IsSelecting, host.ToggleSelecting, host.Tone));
            }
            return new BoxEl
            {
                Direction = 0, Height = RailBodyHeight, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(pad, 0f, pad, 0f), Children = kids.ToArray(),
            };
        }
    }

    /// <summary>The filter words at their natural measure. The rail itself NEVER wraps (a second line would grow the
    /// sticky plane, which is the list's mount-time clip inset), so the box around it shrinks and clips instead —
    /// a window narrower than the ladder's last rung loses the tail of the counts, never the trailing controls.</summary>
    static Element FilterWords(ReaderHost host, ReaderModel m) => new BoxEl
    {
        Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, AlignItems = FlexAlign.Center,
        Children = [Controls.Words.Rail(host.FilterWords!, m.Status, host.Tone)],
    };

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

    readonly record struct HeadStamp(ShowReaderRules.Head Kind, bool Narrow, ulong Fold, ColorF Tone);

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
                return new HeadStamp(s.Head, host.Narrow?.Value ?? false, s.HeadFold, host.Tone());
            };
            _pending = () => host.Model.Read().Head == ShowReaderRules.Head.Pending;
            _content = () => HeadBody(host);
        }

        public override Element Render()
        {
            _ = UseComputed(_stamp).Value;
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
                Tiles(doors, Controls.DoorMinWidth),
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
            Play: () => Episode.Invoke(e, () => model.PlayInShow(e)), Open: () => Episode.OpenPage(e), Tone: h.Tone));
    }

    /// <summary>The trailer door: its own row (outside the membership), played alone — not as the show context.</summary>
    static Element TrailerDoor(Episode t, ReaderHost h)
        => Controls.Door(new DoorData(Loc.Get(Strings.Podcast.Trailer), t.Title, DescriptionOf(t),
            t.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(t.DurationMs) : null, Lead: false, Numeral: null,
            Play: () => Episode.Invoke(t, () => Playback.PlayContext(t.Id)), Open: () => Episode.OpenPage(t), Tone: h.Tone));

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
            var minis = new List<Element>(s.UpNext.Length);
            for (int i = 0; i < s.UpNext.Length; i++) minis.Add(Mini(new Episode(s.UpNext[i])));
            cont.Add(Tiles(minis, MiniMin));
        }
        var sections = new List<Element>(2) { Column(cont, Spacing.M) };
        if (s.Fresh.Length > 0) sections.Add(NewSince(s, h, narrow));
        return Column(sections, SectionGap);
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
        string leftWords = Episode.DurationWords(Episode.LeftMinutes(e.ProgressMs, e.DurationMs));
        int at = line.IndexOf(TemplateMark);
        var spans = new List<TextSpan>(3);
        if (at < 0) spans.Add(new TextSpan(line));
        else
        {
            if (at > 0) spans.Add(new TextSpan(line[..at], Weight: 400, Color: Tok.TextSecondary, Size: 12.5f));
            spans.Add(new TextSpan(leftWords));
            if (at + 1 < line.Length) spans.Add(new TextSpan(line[(at + 1)..], Weight: 400, Color: Tok.TextSecondary, Size: 12.5f));
        }
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
                new SpanTextEl(spans.ToArray())
                {
                    FontFamily = DisplayFace, Size = 30f, LineHeight = 34f, Weight = 300, CharSpacing = -20f, Color = t,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl { Direction = 1, MaxWidth = HeroTrackMax, MinWidth = 0f, Children = [Episode.ProgressRule(Episode.ReaderPctOf(e), tone, 4f)] },
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
            Children = [Detail.PlayPill(tone, () => Episode.Invoke(e, () => model.PlayInShow(e)), Loc.Get(Strings.Podcast.Resume))],
        };
        Element body = narrow
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy] }, pill],
            }
            : new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy, pill] };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = Edges4.All(14f), Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Gradient = new GradientSpec(GradientShape.Linear, 10f, [new GradientStop(0f, ToneAt(t, 0.30f)), new GradientStop(0.75f, Tok.FillCardDefault)]),
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Episode.OpenPage(e),
            WhileHover = s_lift, Transition = MotionTok.ControlFast,
            Children = [body],
        };
    }

    /// <summary>An up-next mini card behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once
    /// the episode's identity is there, a chip-shaped shimmer while it is coming, "unavailable · retry" when its ask
    /// failed — an unresolved chip is never an empty plate.</summary>
    static Element Mini(Episode e) => Episode.Reveal(e, () => MiniCard(e), s_miniSeed, () => MiniFailed(e));

    /// <summary>U+2007 FIGURE SPACE runs: the derived shimmer draws a bar only for a run that MEASURES (the row's own
    /// seed idiom, Episode.UI.cs).</summary>
    static readonly string s_miniSeedTitle = new((char)0x2007, 18), s_miniSeedMeta = new((char)0x2007, 10);

    static readonly Func<Element> s_miniSeed = static () => MiniPlate(numeral: "", title: s_miniSeedTitle, meta: s_miniSeedMeta, trailing: null, onClick: null);

    static Element MiniFailed(Episode e)
        => MiniPlate(numeral: "", title: Loc.Get(Strings.Podcast.Reader.Unavailable), meta: "",
                     trailing: Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)), onClick: null);

    /// <summary>The up-next mini card: the numeral 22/300 · the title 13/600 on one line · "28 min · Sep 15" (or "N min
    /// left" when started). Opens the episode.</summary>
    static Element MiniCard(Episode e)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        bool started = Episode.Rules.InProgress(Episode.ReaderPctOf(e));
        string length = started ? Episode.LeftLabel(e.ProgressMs, e.DurationMs)
                      : e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = Joined(length, e.Knows(EpisodeFields.Published) ? Episode.DateLabel(e.PublishedAt) : "");
        return MiniPlate(n > 0 ? FormatCache.Int(n) : "", title, meta, trailing: null, onClick: () => Episode.OpenPage(e));
    }

    /// <summary>The mini's ONE plate — the card, its seed twin and its failure arm are the same geometry, so the reveal
    /// cross-dissolves in place. A plate with no <paramref name="onClick"/> is not a link.</summary>
    static Element MiniPlate(string numeral, string title, string meta, Element? trailing, Action? onClick)
    {
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Width = 34f, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                Children = [new TextEl(numeral) { FontFamily = DisplayFace, Size = 22f, LineHeight = 28f, Weight = 300, Color = Tok.TextTertiary, MaxLines = 1 }],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    Design.Type.DenseTitle(title) with
                    {
                        Color = onClick is null ? Tok.TextSecondary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    Design.Type.MicroMeta(meta) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        };
        if (trailing is not null) kids.Add(trailing);
        var plate = new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(10f, 8f, 10f, 8f),
            Corners = CornerRadius4.All(6f), Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children = kids.ToArray(),
        };
        return onClick is null ? plate : plate with
        {
            HoverFill = Tok.FillSubtleSecondary, Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        };
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
            var kids = new List<Element>(2) { PivotTemplate(Strings.Podcast.EpisodesCount(TemplateMarkText), count) };
            if (st.IsEmpty) kids.Add(EmptyArm(st.EmptyShow, _host.Model.ResetView));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M), Children = kids.ToArray() };
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
                FontFamily = DisplayFace, Size = 18f, LineHeight = 24f, Weight = 300, CharSpacing = -10f,
                Color = Tok.TextTertiary, MaxLines = 1,
            },
            new BoxEl { Height = Spacing.XS },
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
                Width = 30f, Height = 15f, ZStack = true, Corners = CornerRadius4.All(3f),
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
            Direction = 1, Width = 30f, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, RailHeight, Spacing.XS, BottomReserve), Children = rows,
        };
    }

    static TextEl YearInk(string text, ushort weight, Prop<ColorF> ink) => Design.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    // ══ 5c. THE RAIL'S PRIMARY PILL (reports 11c/11d) ════════════════════════════════════════════════════════════════

    /// <summary>The rail's primary CTA. TWO divergences from <c>Detail.PlayPill</c>, both owner reports:
    /// <list type="bullet">
    /// <item>11c — it is the APP ACCENT, never the show's tone. The tone is the page's reading colour (the hero, the
    /// rule, the badges); the one button that means "press this" stays the accent the whole app agrees on.</item>
    /// <item>11d — the label WRAPS to two lines instead of clipping. The engine's button never wraps (a WinUI
    /// ContentPresenter is <c>TextWrapping=NoWrap</c> and ellipsizes), and "Resume · 2 hr 43 min left" is longer than a
    /// 280-DIP rail, so the pill is composed here on the stock CTA ramp: the same radius, the same 36 floor, the same
    /// 18/6/18/7 padding, the same hover/press alpha ladder (<c>Controls.CtaPalette</c>'s 0.90 / 0.80) and the same
    /// scale tier.</item>
    /// </list></summary>
    static Element PrimaryPill(Action onClick, string label, string? glyph = null)
    {
        Prop<ColorF> ink = Prop.Of(static () => ColorContrast.PickContrast(Tok.AccentDefault));
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            MinHeight = Controls.PillHeight, MinWidth = 0f, Shrink = 1f,
            Padding = new Edges4(18f, 6f, 18f, 7f), Corners = Radii.FullAll,
            Fill = Prop.Of(static () => Tok.AccentDefault),
            HoverFill = Prop.Of(static () => Tok.AccentDefault with { A = 0.90f }),
            PressedFill = Prop.Of(static () => Tok.AccentDefault with { A = 0.80f }),
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
            Children =
            [
                Icon(glyph ?? Icons.Play, 14f) with { Color = ink },
                // No Trim: an ellipsis is the clip the owner reported (24.png). Two lines, and nothing is cut.
                new TextEl(label)
                {
                    Size = 14f, LineHeight = 19f, Weight = 600, Color = ink, MaxLines = 2, Wrap = TextWrap.Wrap,
                    MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }

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
            var show = _host.Model.Read().Show;
            var children = new List<Element>();
            if (st.CanLoadMore) children.Add(LoadMorePill(st.Paging, _host.Model.LoadMore));
            children.Add(Embed.Comp(new PodcastReaderUI.RecommendationProps(show.Uri.Text, true), static () => new PodcastReaderUI.Recommendations()));
            return new BoxEl
            {
                Direction = 1, MinWidth = 0, Gap = Spacing.L,
                Padding = new Edges4(Episode.RowPadX, Spacing.M, 0, BottomReserve), Children = children.ToArray(),
            };
        }
    }

    /// <summary>The standard (never accent) pill. While a page is out the label reads "Loading…" and a tap is a no-op.</summary>
    static Element LoadMorePill(bool paging, Action page)
        => Controls.Pill(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore), paging ? s_noop : page,
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

    // ══ 7. THE LIBRARY PANE'S HEADER ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The library pane's show header — the twin of <see cref="Album.PaneHeader"/>: the same 128 cover (r 6,
    /// <c>Elevation.Card</c>), the same title link (28/34/600, 2 lines) and the same meta rung, with the publisher line
    /// standing where an album bills its artists (<paramref name="attribution"/>). ONE geometry for both kinds, so a
    /// selection crossing album → show moves nothing but the words (library rework §5.4).
    /// <para>A forward, not a copy: it stays a named seam so the day a show wants its own stance, THIS body changes and
    /// the album's is left alone. The signature is byte-identical to the album's on purpose — <c>LibraryShowPane</c> only
    /// re-points its call.</para></summary>
    public static Element PaneHeader(string? cover, string eyebrow, string title, Action open, Element attribution, string meta)
        => new BoxEl
        {
            // Report 2c: ONE target. The cover, the eyebrow, the title, the publisher and the meta rung are a single
            // focusable Button that opens the show — not a title-only hyperlink with four inert neighbours, which is
            // both a smaller pointer target and a keyboard stop that skips most of what it names. The geometry is the
            // album pane's, rung for rung (Album.PaneHeader), so a selection crossing album -> show moves only words.
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Shrink = 0f,
            Height = Album.PaneHeaderHeight, ClipToBounds = true,
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
            Corners = Radii.CardAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
            Children =
            [
                new BoxEl
                {
                    Width = Album.PaneCover, Height = Album.PaneCover, Shrink = 0f, Corners = Radii.CardAll,
                    ClipToBounds = true, Shadow = Elevation.Card,
                    Children = [Controls.Artwork(cover, Album.PaneCover, Album.PaneCover, Radii.Card, decodePx: 256)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = Album.PaneHeaderGap, MinWidth = 0f, MinHeight = 0f,
                    ClipToBounds = true,
                    Children =
                    [
                        Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary },
                        new TextEl(title)
                        {
                            Size = Album.PaneTitleSize, LineHeight = Album.PaneTitleLine, Weight = 600,
                            Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                            BrushTransitionMs = Design.Motion.Faster, MaxLines = Album.PaneTitleMaxLines,
                            Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        attribution,
                        new TextEl(meta)
                        {
                            Size = Album.PaneHeaderMetaSize, LineHeight = Album.PaneHeaderMetaLine, Color = Tok.TextTertiary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
}
