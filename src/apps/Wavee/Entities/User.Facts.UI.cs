// ── Entities/User.Facts.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the facts bento: panel, week / years / tempo / artists / blend / rediscover cards, pills, since-line, the all-artists
// flyout, TempoBandText, the lens header — and the §2.2 bridge (LensCell, LensExtent, FactsHas, FactsPanel, LensHeader)
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 1800 lines
// Spec: ch 07 §0 items 7-10, §2 W1/W16-W18/W21-W24, §3 (fact card … lens header rows), §5 M-7…M-17, §6.2, §10 36-58
//
// ── THE BRIDGE (contract §2.2) ───────────────────────────────────────────────────────────────────────────────────────
//
// The rail's facts sit OUTSIDE the table, but every fact is a LENS that writes the table's filters, and the table only
// provides `Track.TableLiveSlot` around itself. So a page owns ONE `LensCell` (a field), hands it to both halves, and the
// lens-header component — which the table builds UNDER its provider — writes the `TableLive` it can see into the cell from
// an effect. The panel reads `cell.Live` (a signal), so it re-renders the moment the bridge lands and every card lights
// from the table's own filter state; a click writes back through `TableLive.SetFilters`. The header renders 0 DIP while no
// lens is on (W19: "no lens header") and LensExtent while one is — pages report `LensExtentFor(cell)` as the profile's
// extent so the vertical arm's clip inset never reserves a band nothing paints.
//
// ── READINESS (ch 07 §0.10, §7) ──────────────────────────────────────────────────────────────────────────────────────
//
// The panel computes nothing on the render path: the membership must be COMPLETE, and 250 ms of quiet after the last
// relevant publication (the list edge, the tracks, the credits, the descriptors, the artist rows) it summarises ONCE into
// a Loadable. Until then it shows the shimmer derived from the cards' own shells; a straggler republishes Ready→Ready with
// no shimmer; the shape latch only upgrades. The bucket clock is read once per summary, floored to the hour.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct User
{
    // ══ 1. THE BRIDGE ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Page-owned, created ONCE per page component instance (a field), passed to both halves: the lens header
    /// (under the table's provider) writes <see cref="Live"/>; the rail panel reads it.</summary>
    public sealed class LensCell
    {
        public readonly Signal<Track.TableLive?> Live = new(null);
    }

    /// <summary>ch 07 §0.9: the lens header is a constant 36 DIP (pill 28 + its 8 gap) while a lens is on.</summary>
    public const float LensExtent = 36f;

    /// <summary>Is any rail lens on in the table behind <paramref name="lens"/>? SUBSCRIBES (the bridge, then the view) —
    /// read it from a computed memo so a page re-renders only when the answer flips.</summary>
    public static bool LensActive(LensCell lens)
        => lens.Live.Value is { } live && LikedFactsRules.ActiveLenses(live.View.Value.Filters) != LikedFactsRules.LikedLens.None;

    /// <summary>The value for <c>TableProfile.LensExtent</c>: <see cref="LensExtent"/> while a lens is on, 0 otherwise —
    /// exactly what the header paints. Subscribes like <see cref="LensActive"/>.</summary>
    public static float LensExtentFor(LensCell lens) => LensActive(lens) ? LensExtent : 0f;

    /// <summary>ch 06 <c>LikedFacts.Has</c>: true when at least one card would mount — an ALLOCATION-FREE early-exit scan
    /// (a keyed credit, a usable stamp or a release year is enough). Liked or a playlist; any other kind ⇒ false.</summary>
    public static bool FactsHas(Track.TableSource source, DetailKind kind)
    {
        if (source is null || (kind != DetailKind.Liked && kind != DetailKind.Playlist)) return false;
        int n = source.Count;
        for (int i = 0; i < n; i++)
        {
            if (source.AddedAt(i) > 0) return true;
            var t = source.At(i);
            if (!t.IsValid) continue;
            if (t.Year > 0 || LikedFactsRules.HasKeyedCredit(t)) return true;
        }
        return false;
    }

    /// <summary>The bento (rail arm: <paramref name="outerPadding"/> false; vertical list footer: true). Reads rows through
    /// <c>source.At(i)</c> / <c>source.AddedAt(i)</c>; writes filters through <c>lens.Live.Value?.SetFilters</c>.</summary>
    public static Element FactsPanel(Track.TableSource source, DetailKind kind, LensCell lens, float width, bool outerPadding)
    {
        Element panel = Embed.Comp(new FactsProps(source, kind, lens, outerPadding), static () => new FactsPanelHost())
            with { Key = "liked-facts" };
        // The width wraps the component rather than riding its props, so a rail drag re-lays the box and never
        // re-renders the bento.
        return width > 0f
            ? new BoxEl { Key = "liked-facts-box", Direction = 1, Width = width, MinWidth = 0f, Children = [panel] }
            : panel;
    }

    /// <summary>The value for <c>TableProfile.LensHeader</c>: a component under <c>Track.TableLiveSlot</c> that bridges the
    /// live table into <paramref name="lens"/> and renders one pill per active facet with its own ×.</summary>
    public static Element LensHeader(LensCell lens, DetailKind kind)
        => Embed.Comp(new LensHeaderProps(lens, kind), static () => new LensHeaderHost()) with { Key = "lens-header-host" };

    static Track.FilterState LensFilters(LensCell lens) => lens.Live.Peek()?.View.Peek().Filters ?? default;

    /// <summary>Every lens reads the LIVE filter at click time — the header's × or the flyout's Clear all can have moved
    /// it since the render that drew the control (ch 07 §6.2).</summary>
    static void LensWrite(LensCell lens, in Track.FilterState next) => lens.Live.Peek()?.SetFilters(next);

    // ══ 2. SHARED CHROME ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The facts' tooltips open IMMEDIATELY: a spark column is ~10 DIP and carries no label, so the bubble IS the
    /// label (ch 07 §6.2). The one instant rung the facts surface shares with the face pile.</summary>
    const float FactTipDelayMs = Controls.FaceTipDelayMs;

    /// <summary>The rail's late-row recipe (Detail.UI.cs keeps its own private copy): fade up on insert, FLIP on a shove.</summary>
    static readonly EnterExit FactFadeUp = new(Opacity: 0f, Active: true);
    static readonly LayoutTransition FactShove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    const int FactSparkWeeks = 12;
    const int FactTopArtistCount = 5;
    /// <summary>How many ranked credits the all-artists flyout names (so "N more" tops out at 35).</summary>
    const int FactArtistFlyoutCap = 40;
    const int FactBlendSlices = 5;
    const float FactsSettleMs = 250f;
    const string FactsSep = " · ";

    static SparkBars.Style FactSparkStyle => SparkBars.DefaultStyle with { TipDelayMs = FactTipDelayMs };

    /// <summary>ch 07 §3 "fact card": pad 12/10/12/11, gap 6, radius 8, card fill + stroke + elevation, self-entering.</summary>
    static Element FactCard(string key, Element head, Element body) => new BoxEl
    {
        Key = key, Enter = FactFadeUp, Layout = FactShove,
        Direction = 1, Gap = 6f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, 10f, Spacing.M, 11f),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
        Children = [head, body],
    };

    /// <summary>The card header: the eyebrow GROWS so the fact is pushed to the trailing edge without a spacer node.</summary>
    static Element CardHead(string title, string? trailing)
    {
        var label = Design.Type.Eyebrow(title) with
        {
            Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        Element[] kids = trailing is { Length: > 0 } fact
            ? [label, Caption(fact) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }]
            : [label];
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = kids };
    }

    /// <summary>The big numeral over its caption: Title 28/36/600 in a ZStack keyed on the value (M-9 TextSwap).</summary>
    static Element FactNumeral(string value, string caption, float width = float.NaN)
    {
        var box = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f,
            Children =
            [
                ZStack(new BoxEl { Key = "v:" + value, Animate = MotionRecipes.TextSwap, Children = [Title(value) with { MaxLines = 1 }] }),
                Caption(caption) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
        return float.IsNaN(width) ? box : box with { Width = width };
    }

    // ══ 3. THE PANEL ═════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record FactsProps(Track.TableSource Source, DetailKind Kind, LensCell Lens, bool OuterPadding);

    /// <summary>One settled summary of one membership: the rows it was computed over (the cards that need the rows keep
    /// this array's identity as their gate), the summary, the week buckets (drawn from the clock read at settle) and the
    /// "this week last year" window.</summary>
    sealed class FactsModel(LikedRow[] rows, LikedFactsRules.FactsSummary summary, IReadOnlyList<LikedFactsRules.WeekBucket> weeks,
                            IReadOnlyList<LikedRow> lastYear, EntityUri context)
    {
        public readonly LikedRow[] Rows = rows;
        public readonly LikedFactsRules.FactsSummary Summary = summary;
        public readonly IReadOnlyList<LikedFactsRules.WeekBucket> Weeks = weeks;
        public readonly IReadOnlyList<LikedRow> LastYear = lastYear;
        public readonly EntityUri Context = context;
    }

    /// <summary>What one settle summarised (W3-A3): the membership's context, the fold of every input
    /// <see cref="LikedFactsRules.Summarize"/> reads (<see cref="FactsInputFold"/>) and the hour bucket the week
    /// windows were drawn against. A settle whose gate equals the last one's is a republication of the SAME facts —
    /// it must not mint a new <see cref="FactsModel"/>, because a new model is what re-pushes every card's props and
    /// re-renders the whole bento.</summary>
    public readonly record struct FactsSettleGate(EntityUri Context, ulong Fold, long HourTicks);

    /// <summary>The fold of everything a facts summary is a function of, row by row and in order: the track row (year,
    /// title, tempo, camelot — its own version), the added-at stamp, the credits edge and the tags edge (their versions
    /// for that track), and each credited artist's row (<see cref="LikedFactsRules.Summarize"/> ranks ties by artist
    /// NAME). One pass over the membership, no allocation. Public so <c>Wavee.Tests</c> can pin what moves it.</summary>
    public static ulong FactsInputFold(Track.TableSource src)
    {
        var scope = Entities.Current;
        var e = scope.Edges;
        int n = src.Count;
        ulong fold = RowFold.Add(RowFold.Add(RowFold.Seed, n), (uint)src.State);
        for (int i = 0; i < n; i++)
        {
            var t = src.At(i);
            fold = RowFold.Row(fold, scope.Tracks, t.Slot);
            fold = RowFold.Add(fold, src.AddedAt(i));
            if (!t.IsValid) continue;
            fold = RowFold.Add(fold, e.TrackArtists.Version(t.Slot));
            fold = RowFold.Add(fold, e.TrackTags.Version(t.Slot));
            var credits = t.ArtistSlots;
            for (int a = 0; a < credits.Length; a++) fold = RowFold.Add(fold, RowFold.Version(scope.Artists, credits[a]));
        }
        return fold;
    }

    sealed class FactsPanelHost : Component
    {
        FactsProps? _props;
        Loadable<FactsModel>? _facts;
        EntityUri _latchFor;
        LikedFactsRules.ShapeLatch _latch = new();
        // The settle one-shot: armed at mount by UseTimeout, RESTARTED by the watcher effect on every publication.
        TimerHandle _settleTimer;
        // The gate of the last settle that published a model (default until one has).
        FactsSettleGate _settledOver;
        bool _settledOnce;
        // The region's inputs, written by Render and read by the two branch factories at INVOCATION (the reconciler
        // re-invokes Content on every render of this host and on every loadable edge — so the fields are always the
        // latest render's, and the factories themselves never change identity).
        Track.FilterState _filters;
        IReadSignal<Track.FilterState>? _live;
        LensCell? _lens;
        bool _liked, _outerPadding;

        readonly Action _settle, _rearm;
        readonly Func<Track.FilterState> _readFilters;
        readonly Func<Element> _shimmer;
        readonly Func<FactsModel, Element> _content;

        public FactsPanelHost()
        {
            _settle = Settle;
            _rearm = Rearm;
            _readFilters = () => _props?.Lens.Live.Value?.View.Value.Filters ?? default;
            _shimmer = () => FactsStack(FactSkeletonCards(), _outerPadding);
            _content = m => FactsStack(FactCards(m, in _filters, _live!, _lens!, _liked, _latch, CultureInfo.CurrentCulture), _outerPadding);
        }

        public override Element Render()
        {
            var p = UseProps<FactsProps>();
            _props = p;
            var facts = UseLoadable<FactsModel>();
            _facts = facts;
            var filters = UseComputed(_readFilters);

            // THE SETTLE (ch 07 §0.10): every publication that can move a fact restarts the 250 ms quiet window. The
            // counters are read in the WATCHER EFFECT below, never here (W3-A3): this render used to subscribe to five
            // tables and re-run on every publication, and each run made the reconciler re-invoke Content — the whole
            // bento, every card's props re-pushed, every tooltip re-rendered — while scrolling a playlist in real mode.
            // Now the render runs on a props change, a scope switch or a lens flip, and nothing else.
            _settleTimer = UseTimeout(_settle, FactsSettleMs);
            UseEffect(_rearm, DepKey.FromRef(p.Source));

            // A new context (a different playlist reusing the slot) starts a fresh shape memory.
            var src = p.Source;
            if (!_latchFor.Equals(src.Context))
            {
                _latchFor = src.Context;
                _latch = new LikedFactsRules.ShapeLatch();
            }

            _liked = p.Kind == DetailKind.Liked;
            _outerPadding = p.OuterPadding;
            _filters = filters.Value;                  // subscribe: the lit lenses repaint wherever the filter moved
            _live = filters;
            _lens = p.Lens;
            return Skel.Region(facts, shimmerSource: _shimmer, content: _content, reveal: SkelReveal.Soft, smoothResize: false);
        }

        /// <summary>The watcher (a tracked effect, not a render): subscribes to the source's own edges and the four
        /// tables a fact can move on, and re-arms the settle one-shot each time any of them publishes. Runs once at
        /// mount too — a harmless restart of the timer the hook just armed.</summary>
        void Rearm()
        {
            var p = _props;
            if (p is null) return;
            p.Source.Subscribe();
            var scope = Entities.Current;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Edges.TrackArtists.Changed.Value;
            _ = scope.Edges.TrackTags.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _settleTimer.Restart();
        }

        void Settle()
        {
            var p = _props;
            if (p is null || _facts is null) return;
            var src = p.Source;
            // The honest gate (ch 07 §7): a partial membership is not the list the facts describe.
            if (src.State != EdgeState.Complete) return;

            // THE ONE CLOCK READ, local, floored to the hour (LikedFactsRules.BucketClock).
            var now = LikedFactsRules.BucketClock(
                DateTimeOffset.FromUnixTimeSeconds(Store.ToUnix(Entities.Now)).ToLocalTime());

            // The value gate (W3-A3): a quiet window that closed over the SAME inputs as the last settle is not news.
            var gate = new FactsSettleGate(src.Context, FactsInputFold(src), now.UtcTicks);
            if (_settledOnce && gate == _settledOver) return;
            _settledOver = gate;
            _settledOnce = true;

            int n = src.Count;
            var rows = new LikedRow[n];
            for (int i = 0; i < n; i++) rows[i] = new LikedRow(src.At(i), src.AddedAt(i));

            var summary = LikedFactsRules.Summarize(rows, 12, FactBlendSlices, FactArtistFlyoutCap);
            bool liked = p.Kind == DetailKind.Liked;
            bool stamped = liked ? summary.AnyStamped : summary.StampsSpread;
            var weeks = stamped ? LikedFactsRules.LikesPerWeek(rows, now, FactSparkWeeks) : Array.Empty<LikedFactsRules.WeekBucket>();
            IReadOnlyList<LikedRow> lastYear = Array.Empty<LikedRow>();
            if (liked)
            {
                var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(now);
                lastYear = LikedFactsRules.LikedInWindow(rows, start, end);
            }
            _facts.SetReady(new FactsModel(rows, summary, weeks, lastYear, src.Context));
        }
    }

    static Element FactsStack(Element[] cards, bool outerPadding)
    {
        if (cards.Length == 0) return new BoxEl { Key = "liked-facts-panel" };
        return new BoxEl
        {
            Key = "liked-facts-panel", Enter = FactFadeUp, Layout = FactShove,
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            // Reduced motion is a VALUE: the stagger goes to zero, the cards still fade.
            Stagger = Design.Reduced ? 0f : Design.Motion.MastheadStaggerMs,
            Padding = outerPadding ? new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L) : Edges4.All(0f),
            Children = cards,
        };
    }

    /// <summary>The real cards, decided by <see cref="LikedFactsRules.Plan"/> from ONE settled summary.</summary>
    static Element[] FactCards(FactsModel m, in Track.FilterState filters, IReadSignal<Track.FilterState> live, LensCell lens,
                           bool liked, LikedFactsRules.ShapeLatch latch, CultureInfo culture)
    {
        var s = m.Summary;
        var plan = LikedFactsRules.Plan(s, m.Weeks, liked, latch);
        var cards = new List<Element>(7);

        // (a) the time slot — the week card or the years card, never both.
        if (plan.WeekCard) cards.Add(FactWeekCard(m.Weeks, culture, in filters, lens, liked));
        else if (plan.YearsCard) cards.Add(FactYearsCard(m.Rows, s.YearBuckets, culture, in filters, lens));
        DateTimeOffset? lastActivity = plan.LastActivityClause ? LikedFactsRules.LatestStamp(m.Rows) : null;

        // (a′) tempo, (b) artists, (c) blend — each its own component (hooks under an `if`).
        if (plan.TempoCard)
            cards.Add(Embed.Comp(new FactTempoProps(s.Tempo, m.Rows, lens, live), static () => new TempoCardHost()) with { Key = "fact:tempo" });
        if (plan.ArtistsCard)
            cards.Add(Embed.Comp(new FactArtistsProps(s.Artists, lens, live, liked), static () => new ArtistsCardHost()) with { Key = "fact:artists" });
        if (plan.BlendCard)
            cards.Add(Embed.Comp(new FactBlendProps(m.Rows, s.BlendShares, lens, live), static () => new BlendCardHost()) with { Key = "fact:blend" });

        // (d) rediscover — Liked only, mounted only when that window holds.
        if (liked && m.LastYear.Count > 0) cards.Add(FactRediscoverCard(m.LastYear, m.Context));

        // (e) the facts that did not earn a card — still lenses.
        if (FactPillRow(s, plan.YearsPill, plan.TempoPill, plan.BlendPill, culture, in filters, lens) is { } pills) cards.Add(pills);

        // (f) the since-line; its decade clause yields to a years pill.
        if (FactSinceLine(m.Rows, culture, liked, s.StampsSpread, suppressDecade: plan.YearsPill, lastActivity) is { } since)
            cards.Add(since);

        return cards.ToArray();
    }

    // ── the shimmer source: the cards' own shells with placeholder rows (W16) ──

    static readonly float[] s_factSkelHeights = [10f, 18f, 8f, 26f, 14f, 6f, 22f, 12f, 30f, 16f, 24f, 38f];
    static readonly float[] s_factSkelNameWidths = [120f, 96f, 132f, 88f, 150f];

    static Element[] FactSkeletonCards()
    {
        static Element Bar(float w, float h) => new BoxEl
        { Width = w, Height = h, Corners = CornerRadius4.All(3f), Fill = Tok.FillControlDefault, Shrink = 0f };
        static Element Column(float h) => new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.End, Height = 38f,
            Children = [new BoxEl { Height = h, Corners = new CornerRadius4(2f, 2f, 1f, 1f), Fill = Tok.FillControlDefault }],
        };

        var columns = new Element[s_factSkelHeights.Length];
        for (int i = 0; i < columns.Length; i++) columns[i] = Column(s_factSkelHeights[i]);
        var time = FactCard("fact:week", CardHead(" ", " "), new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0f, Children = [Bar(56f, 30f), Bar(72f, 12f)] },
                new BoxEl { Direction = 0, Gap = 3f, Height = 38f, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = columns },
            ],
        });

        var faces = new Element[6];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = new BoxEl
            {
                Width = 28f, Height = 28f, Corners = CornerRadius4.All(14f), Fill = Tok.FillControlDefault, Shrink = 0f,
                Margin = new Edges4(i == 0 ? 0f : -12f, 0f, 0f, 0f),
            };
        var names = new Element[s_factSkelNameWidths.Length];
        for (int i = 0; i < names.Length; i++) names[i] = Bar(s_factSkelNameWidths[i], 12f);
        var artists = FactCard("fact:artists", CardHead(" ", null), new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children = [new BoxEl { Direction = 0, Children = faces }, new BoxEl { Direction = 1, Gap = 6f, Children = names }],
        });

        var blend = FactCard("fact:blend", CardHead(" ", " "), new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl { Height = 8f, Corners = CornerRadius4.All(4f), Fill = Tok.FillControlDefault },
                new BoxEl { Direction = 0, Gap = Spacing.M, Children = [Bar(64f, 12f), Bar(52f, 12f), Bar(70f, 12f)] },
            ],
        });
        // Liked and playlists share the same three silhouettes (Rediscover is too data-bound to fake honestly).
        return [time, artists, blend];
    }

    // ══ 4. THE TIME SLOT: THIS WEEK / THE YEARS ═════════════════════════════════════════════════════════════════════

    static Element FactWeekCard(IReadOnlyList<LikedFactsRules.WeekBucket> weeks, CultureInfo culture, in Track.FilterState filters,
                            LensCell lens, bool liked)
    {
        int thisWeek = weeks.Count > 0 ? weeks[weeks.Count - 1].Count : 0;
        var bars = new SparkBar[weeks.Count];
        for (int i = 0; i < weeks.Count; i++)
        {
            var week = weeks[i];
            bool newest = i == weeks.Count - 1;
            bool lit = LikedFactsRules.IsWeekLens(filters, week);
            void Toggle()
            {
                var current = LensFilters(lens);
                var (after, before) = LikedFactsRules.WeekWindowMs(week);
                LensWrite(lens, LikedFactsRules.IsWeekLens(current, week)
                    ? current.WithAddedWindow(0L, 0L)
                    : current.WithAddedWindow(after, before));
            }
            bars[i] = new SparkBar(week.Count, FactWeekTip(week, culture, liked), Lit: lit, Accent: newest || lit, OnClick: Toggle);
        }

        string caption = liked ? Strings.Detail.LikedFacts.SongsLiked(thisWeek) : Strings.Detail.LikedFacts.SongsAdded(thisWeek);
        return FactCard("fact:week",
            CardHead(Loc.Get(Strings.Detail.LikedFacts.ThisWeek), Strings.Detail.LikedFacts.WindowCaption(weeks.Count)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [FactNumeral(Strings.Detail.LikedFacts.LikedDelta(thisWeek), caption),
                            SparkBars.Create(new SparkBarsModel(bars), FactSparkStyle, key: "spark")],
            });
    }

    static string FactWeekTip(in LikedFactsRules.WeekBucket week, CultureInfo culture, bool liked)
    {
        var (after, before) = LikedFactsRules.WeekWindowMs(week);
        var (start, end) = LensText.RangeParts(after, before, culture);
        return liked
            ? Strings.Detail.LikedFacts.WeekTip(start, end, week.Count)
            : Strings.Detail.LikedFacts.WeekTipAdded(start, end, week.Count);
    }

    static Element FactYearsCard(LikedRow[] rows, IReadOnlyList<LikedFactsRules.YearBucket> years, CultureInfo culture,
                             in Track.FilterState filters, LensCell lens)
    {
        int peak = 0, peakIdx = 0;
        for (int i = 0; i < years.Count; i++)
            if (years[i].Count > peak || (years[i].Count == peak && years[i].YearMax >= years[peakIdx].YearMax))
            { peak = years[i].Count; peakIdx = i; }

        int modeYear = years.Count > 0 ? LikedFactsRules.PeakYear(rows, years[peakIdx]) : 0;
        int spanLo = 0, spanHi = 0;
        for (int i = 0; i < years.Count; i++)
        {
            if (years[i].Count <= 0) continue;
            if (spanLo == 0) spanLo = years[i].YearMin;
            spanHi = years[i].YearMax;
        }

        var bars = new SparkBar[years.Count];
        for (int i = 0; i < years.Count; i++)
        {
            var bucket = years[i];
            bool lit = LikedFactsRules.IsYearLens(filters, bucket);
            void Toggle()
            {
                var current = LensFilters(lens);
                LensWrite(lens, LikedFactsRules.IsYearLens(current, bucket)
                    ? current.WithReleaseYear(0, 0)
                    : current.WithReleaseYear(bucket.YearMin, bucket.YearMax));
            }
            bars[i] = new SparkBar(bucket.Count, FactYearTip(bucket, culture), Lit: lit, Accent: i == peakIdx || lit, OnClick: Toggle);
        }

        string trailing = spanLo > 0 && spanHi > 0 && spanLo != spanHi
            ? Strings.Detail.LikedFacts.YearRange(spanLo.ToString(culture), spanHi.ToString(culture))
            : (spanLo > 0 ? spanLo.ToString(culture) : "");

        return FactCard("fact:years",
            CardHead(Loc.Get(Strings.Detail.LikedFacts.TheYears), trailing),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [FactNumeral(modeYear.ToString(culture), Loc.Get(Strings.Detail.LikedFacts.MostTracks)),
                            SparkBars.Create(new SparkBarsModel(bars), FactSparkStyle, key: "spark")],
            });
    }

    static string FactYearTip(in LikedFactsRules.YearBucket bucket, CultureInfo culture)
        => bucket.YearMin == bucket.YearMax
            ? Strings.Detail.LikedFacts.YearTipOne(bucket.YearMin.ToString(culture), bucket.Count)
            : Strings.Detail.LikedFacts.YearTip(bucket.YearMin.ToString(culture), bucket.YearMax.ToString(culture), bucket.Count);

    // ══ 5. REDISCOVER + THE SINCE-LINE ══════════════════════════════════════════════════════════════════════════════

    static Element FactRediscoverCard(IReadOnlyList<LikedRow> window, EntityUri context)
    {
        var body = new List<Element>(2)
        {
            Caption(Strings.Detail.LikedFacts.LastYear(window.Count)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
            },
        };
        // "Play them" plays THESE rows in THIS order as a held row set of the collection (the subset seam). No context ⇒
        // no button: the fact still reads on its own rather than offering a dead control.
        if (context.IsValid)
        {
            void PlayThem()
            {
                // Built on the click, never per render: the window can be hundreds of rows and this is a cold path.
                var refs = new EntityRef[window.Count];
                for (int i = 0; i < refs.Length; i++) refs[i] = new EntityRef(EntityKind.Track, window[i].Track.Slot);
                Playback.PlayRows(refs, 0, context.Id);
            }
            body.Add(Button.Create(Loc.Get(Strings.Detail.LikedFacts.PlayThem), PlayThem, ButtonAppearance.Standard, ControlSize.Small)
                with { Shrink = 0f });
        }
        return FactCard("fact:lastyear",
            CardHead(Loc.Get(Strings.Detail.LikedFacts.Rediscover), null),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = body.ToArray() });
    }

    /// <summary>Present-only clauses. Liked = save since / save decade / oldest like. Playlist with spread = collecting
    /// since / release decade / oldest add. Editorial = release decade / oldest track with year.</summary>
    static Element? FactSinceLine(LikedRow[] rows, CultureInfo culture, bool liked, bool spread, bool suppressDecade,
                              DateTimeOffset? lastActivity)
    {
        var clauses = new List<string>(4);
        if (liked)
        {
            if (LikedFactsRules.LikingSince(rows) is { } since)
                clauses.Add(Strings.Detail.LikedFacts.Since(since.ToLocalTime().ToString("MMMM yyyy", culture)));
            if (!suppressDecade && LikedFactsRules.DominantDecade(rows) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestLike(rows) is { } oldest && oldest.Track.Title is { Length: > 0 } title)
                clauses.Add(Strings.Detail.LikedFacts.Oldest(title));
        }
        else if (spread)
        {
            if (LikedFactsRules.LikingSince(rows) is { } since)
                clauses.Add(Strings.Detail.LikedFacts.CollectingSince(since.ToLocalTime().ToString("MMMM yyyy", culture)));
            if (!suppressDecade && LikedFactsRules.DominantReleaseDecade(rows) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestLike(rows) is { } oldest && oldest.Track.Title is { Length: > 0 } title)
                clauses.Add(Strings.Detail.LikedFacts.OldestAdd(title));
        }
        else
        {
            if (!suppressDecade && LikedFactsRules.DominantReleaseDecade(rows) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestRelease(rows) is { } oldest && oldest.Track.Title is { Length: > 0 } title)
                clauses.Add(oldest.Track.Year > 0
                    ? Strings.Detail.LikedFacts.OldestTrackYear(title, oldest.Track.Year)
                    : Strings.Detail.LikedFacts.OldestTrack(title));
        }
        if (lastActivity is { } last)
        {
            string date = last.ToLocalTime().ToString("MMM d", culture);
            clauses.Add(liked ? Strings.Detail.LikedFacts.LastLike(date) : Strings.Detail.LikedFacts.LastAdd(date));
        }
        if (clauses.Count == 0) return null;

        return new BoxEl
        {
            Key = "caption:since", Direction = 1, MinWidth = 0f,
            Enter = FactFadeUp, Layout = FactShove,
            Padding = new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XXS, 0f),
            Children =
            [
                Caption(string.Join(FactsSep, clauses)) with
                { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    // ══ 6. TEMPO ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE wording + domain table for the four tempo bands — read by the card's bands, the tempo pill and the
    /// lens header. Boundaries come from <c>Track.FilterModel.BandOf</c>; these are only their words and plot domains.</summary>
    public static class TempoBandText
    {
        public const float DomainMin = 60f, DomainMax = 200f;

        public static string Range(Track.TempoBand band) => Loc.Get(band switch
        {
            Track.TempoBand.Under90 => Strings.Detail.LikedFacts.BandUnder90,
            Track.TempoBand.From90To119 => Strings.Detail.LikedFacts.Band90,
            Track.TempoBand.From120To139 => Strings.Detail.LikedFacts.Band120,
            _ => Strings.Detail.LikedFacts.Band140,
        });

        public static string Name(Track.TempoBand band) => Loc.Get(band switch
        {
            Track.TempoBand.Under90 => Strings.Detail.LikedFacts.BandNameUnder90,
            Track.TempoBand.From90To119 => Strings.Detail.LikedFacts.BandName90,
            Track.TempoBand.From120To139 => Strings.Detail.LikedFacts.BandName120,
            _ => Strings.Detail.LikedFacts.BandName140,
        });

        public static string Tip(Track.TempoBand band, int count)
            => Strings.Detail.LikedFacts.TempoBandTip(Range(band), Name(band), count);

        /// <summary>The band's interval on the 60–200 bpm plot (the open ends clamp to the plot's edges). MUST agree with
        /// <c>Track.FilterModel.BandOf</c>'s 90 / 120 / 140 boundaries.</summary>
        public static (float Lo, float Hi) Domain(Track.TempoBand band) => band switch
        {
            Track.TempoBand.Under90 => (DomainMin, 90f),
            Track.TempoBand.From90To119 => (90f, 120f),
            Track.TempoBand.From120To139 => (120f, 140f),
            _ => (140f, DomainMax),
        };
    }

    sealed record FactTempoProps(LikedFactsRules.TempoSummary Tempo, LikedRow[] Rows, LensCell Lens, IReadSignal<Track.FilterState> Filters);

    /// <summary>"Tempo": the kind-222 BPM distribution as the kit's DensityPlot — ridge + line + median marker + three
    /// captions, NO rug and NO hairlines (the rail's calm variant), four bands as lenses.</summary>
    sealed class TempoCardHost : Component
    {
        const float NumeralColumnWidth = 72f;
        static readonly PlotTick[] s_ticks = [new(80f, "80"), new(120f, "120"), new(160f, "160")];
        static readonly Track.TempoBand[] s_bands =
            [Track.TempoBand.Under90, Track.TempoBand.From90To119, Track.TempoBand.From120To139, Track.TempoBand.From140AndUp];

        static DensityPlot.Style PlotStyle => DensityPlot.DefaultStyle with
        {
            TipDelayMs = FactTipDelayMs, LineWidth = 1.25f, AxisFontSize = 10f, Marker = Tok.AccentTextPrimary with { A = 0.45f },
        };

        FactTempoProps? _latest;
        // The card's strings, minted once per tempo SUMMARY (a record struct: value equality) — the four band tips, the
        // median numeral and the header's coverage / range (W3-A3: no per-render string building).
        LikedFactsRules.TempoSummary _labelsFor;
        bool _hasLabels;
        readonly string[] _bandTip = new string[4];
        string _median = "", _trailing = "";
        // Mount-stable per-band toggles and the bpm memo's factory (no closure per render).
        readonly Action[] _bandToggle = new Action[4];
        readonly Func<float[]> _bpm;

        public TempoCardHost()
        {
            _bpm = ComputeBpm;
            for (int i = 0; i < s_bands.Length; i++)
            {
                var band = s_bands[i];
                _bandToggle[i] = () =>
                {
                    if (_latest is not { } p) return;
                    var current = LensFilters(p.Lens);
                    LensWrite(p.Lens, current with { Tempo = LikedFactsRules.IsTempoLens(current, band) ? Track.TempoBand.Any : band });
                };
            }
        }

        /// <summary>The value array memoised on the tempo CONTENT (the fingerprint), never on the list instance: same
        /// tempos ⇒ same array ⇒ the plot's geometry memo hits and nothing re-tessellates.</summary>
        float[] ComputeBpm()
        {
            var p = _latest!;
            int known = p.Tempo.Stats.Known;
            var values = new float[known];
            var unused = new uint[known];
            LikedFactsRules.TempoValues(p.Rows, values, unused);
            return values;
        }

        public override Element Render()
        {
            var p = UseProps<FactTempoProps>();
            _latest = p;
            var t = p.Tempo;
            var stats = t.Stats;
            var filters = p.Filters.Value;             // subscribe: the lit band is the list's filter
            var bpm = UseMemo(_bpm, DepKey.From(t.Fingerprint.Hash, (long)t.Fingerprint.Known));

            if (!_hasLabels || t != _labelsFor)
            {
                _hasLabels = true;
                _labelsFor = t;
                var culture = CultureInfo.CurrentCulture;
                for (int i = 0; i < s_bands.Length; i++) _bandTip[i] = TempoBandText.Tip(s_bands[i], t.Count(i));
                _median = Math.Round(stats.Median).ToString(culture);
                // "N of M" while coverage is partial; the range only when EVERY row is known (ch 07 W1 note, item 74).
                _trailing = stats.Known < stats.Total
                    ? Strings.Detail.LikedFacts.TempoCoverage(stats.Known, stats.Total)
                    : Strings.Detail.LikedFacts.TempoRange(Math.Round(stats.Min).ToString(culture), Math.Round(stats.Max).ToString(culture));
            }

            var bands = new PlotBand[s_bands.Length];
            for (int i = 0; i < s_bands.Length; i++)
            {
                var band = s_bands[i];
                var (lo, hi) = TempoBandText.Domain(band);
                bands[i] = new PlotBand(lo, hi, _bandTip[i], LikedFactsRules.IsTempoLens(filters, band), _bandToggle[i]);
            }

            var model = new DensityPlotModel(bpm, TempoBandText.DomainMin, TempoBandText.DomainMax)
            {
                Bands = bands, Ticks = s_ticks, Marker = (float)stats.Median, RugDotMax = 0,
            };

            return FactCard("fact:tempo",
                CardHead(Loc.Get(Strings.Detail.LikedFacts.Tempo), _trailing),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                    Children = [FactNumeral(_median, Loc.Get(Strings.Detail.LikedFacts.BpmMedian), NumeralColumnWidth),
                                DensityPlot.Create(model, PlotStyle, key: "tempo-plot")],
                });
        }
    }

    // ══ 7. MOST LIKED / TOP ARTISTS ═════════════════════════════════════════════════════════════════════════════════

    sealed record FactArtistsProps(IReadOnlyList<LikedFactsRules.ArtistCount> Ranked, LensCell Lens,
                               IReadSignal<Track.FilterState> Filters, bool Liked);

    /// <summary>The face pile and the five names behind it — ONE affordance each (face and name both lens the list to
    /// that artist; a second click clears). The pile's face count is RESPONSIVE to the card's own measured width on the
    /// 20-DIP face grid; the pile's "+N" and the row's "N more" are different numbers opening the same flyout.</summary>
    sealed class ArtistsCardHost : Component
    {
        // The five name rows' keys, by slot (FactTopArtistCount).
        static readonly string[] s_rowKeys = ["who:0", "who:1", "who:2", "who:3", "who:4"];

        FactArtistsProps? _latest;
        readonly Action _demandPortraits;
        readonly Action _toggleMore;
        readonly Action<KeyEventArgs> _moreKey;
        readonly Func<bool> _moreOpenRead;
        readonly Func<ArtistsStamp> _stamp;
        Signal<bool>? _moreOpen;
        Ref<NodeHandle>? _anchor;
        Ref<OverlayHandle?>? _handle;
        IOverlayService? _overlay;

        // Per-slot state for the five name rows (W3-A3): written by Render, read by the MOUNT-STABLE row factories
        // ToolTip.WrapStable invokes inside each tooltip's own render — so a row's tooltip re-renders only when its tip
        // text moved (name or count) or the live filter flipped (read in the factory), never because this card did.
        readonly Artist[] _rowArtist = new Artist[FactTopArtistCount];
        readonly string[] _rowName = new string[FactTopArtistCount];
        readonly string[] _rowCount = new string[FactTopArtistCount];
        readonly Func<Element>[] _rowFactory = new Func<Element>[FactTopArtistCount];
        readonly Action[] _rowClick = new Action[FactTopArtistCount];

        /// <summary>What the card paints off the tables: every ranked artist's row (name, portrait), in order. The
        /// ranking itself is the summary's (props); only the rows' versions move here.</summary>
        readonly record struct ArtistsStamp(uint Epoch, ulong Ranked);

        public ArtistsCardHost()
        {
            _demandPortraits = DemandPortraits;
            _toggleMore = ToggleMore;
            _moreKey = MoreKey;
            _moreOpenRead = () => _moreOpen?.Value ?? false;
            _stamp = Stamp;
            for (int i = 0; i < FactTopArtistCount; i++)
            {
                int slot = i;
                _rowName[i] = _rowCount[i] = "";
                // The CURRENT artist of the slot at click time, never one captured at build time.
                _rowClick[i] = () => { if (_latest is { } p && _rowArtist[slot].IsValid) ToggleArtist(p.Lens, _rowArtist[slot]); };
                _rowFactory[i] = () => RowFor(slot);
            }
        }

        ArtistsStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Artists.Changed.Value;           // names + portraits land here
            ulong fold = RowFold.Seed;
            if (_latest?.Ranked is { } ranked)
                for (int i = 0; i < ranked.Count; i++) fold = RowFold.Row(fold, scope.Artists, ranked[i].Artist.Slot);
            return new ArtistsStamp(epoch, fold);
        }

        /// <summary>The name row of slot <paramref name="i"/>, built INSIDE its tooltip's render: the live filter is read
        /// here, so a lens flip relights the row with no re-push from the card.</summary>
        Element RowFor(int i)
        {
            var artist = _rowArtist[i];
            bool lensable = LikedFactsRules.ArtistKey(artist) != 0;
            bool lit = lensable && _latest is { } p && LikedFactsRules.IsArtistLens(p.Filters.Value, artist);
            return NameRow(_rowName[i], _rowCount[i], lit, lensable ? _rowClick[i] : null);
        }

        public override Element Render()
        {
            var p = UseProps<FactArtistsProps>();
            _latest = p;
            _overlay = UseContext(Overlay.Service);
            _anchor = UseRef<NodeHandle>(default);
            _handle = UseRef<OverlayHandle?>(null);
            _moreOpen = UseSignal(false);
            var measuredW = UseMeasuredWidth(Controls.FaceStep);
            // The gate (W3-A3): the Artists counter is subscribed inside the memo; a publication that leaves every
            // ranked artist's version where it was resolves this render clean.
            _ = UseComputed(_stamp).Value;
            var filters = p.Filters.Value;
            var ranked = p.Ranked;

            // G9: portraits need ArtistFields.Identity; the Ensure is once per ranked set.
            int rankedKey = 17;
            for (int i = 0; i < ranked.Count; i++) rankedKey = rankedKey * 31 + ranked[i].Artist.Slot;
            UseEffect(_demandPortraits, DepKey.From(rankedKey, ranked.Count));

            int nameCount = Math.Min(FactTopArtistCount, ranked.Count);
            float inner = measuredW.Value > 0f ? MathF.Max(0f, measuredW.Value - Spacing.M * 2f) : 0f;
            // First frame is unmeasured: keep the named five so the pile doesn't flash a single face.
            int faceCount = inner >= Controls.FaceOuter ? Controls.VisibleFaces(inner, ranked.Count) : nameCount;
            int pileExtra = Math.Max(0, ranked.Count - faceCount);
            int nameExtra = Math.Max(0, ranked.Count - nameCount);

            var lens = p.Lens;
            var faces = new Controls.Face[Math.Min(faceCount, ranked.Count)];
            var rows = new List<Element>(nameCount + 1);
            int painted = Math.Max(faces.Length, nameCount);
            for (int i = 0; i < painted; i++)
            {
                var artist = ranked[i].Artist;
                int count = ranked[i].Count;
                string name = artist.Knows(ArtistFields.Name) ? artist.Name : "";
                bool lensable = LikedFactsRules.ArtistKey(artist) != 0;
                string tip = p.Liked
                    ? Strings.Detail.LikedFacts.ArtistTip(name, count)
                    : Strings.Detail.LikedFacts.ArtistTipAdded(name, count);
                if (i < nameCount)
                {
                    _rowArtist[i] = artist;
                    _rowName[i] = name;
                    _rowCount[i] = FormatCache.Int(count);
                    rows.Add(ToolTip.WrapStable(_rowFactory[i], tip, grow: 1f, showDelayMs: FactTipDelayMs) with { Key = s_rowKeys[i] });
                }
                if (i < faces.Length)
                {
                    // A face in a named slot shares that slot's mount-stable click; the responsive extras beyond the five
                    // are the pile's own (rare: this render runs only when a ranked row or the width moved).
                    Action? click = null;
                    if (lensable) click = i < nameCount ? _rowClick[i] : () => ToggleArtist(lens, artist);
                    faces[i] = new Controls.Face(name, Controls.ArtUrl(artist.ImageId), click, LikedFactsRules.IsArtistLens(filters, artist), tip);
                }
            }
            if (nameExtra > 0) rows.Add(MoreRow(nameExtra));

            return FactCard("fact:artists",
                CardHead(Loc.Get(p.Liked ? Strings.Detail.LikedFacts.MostLiked : Strings.Detail.LikedFacts.TopArtists), null),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Shrink = 0f, OnRealized = h => { if (_anchor is { } a) a.Value = h; },
                            Children =
                            [
                                Controls.FacePile(faces, maxVisible: faces.Length, overflow: pileExtra,
                                    onOverflow: pileExtra > 0 ? _toggleMore : null,
                                    overflowTip: pileExtra > 0 ? Strings.Detail.LikedFacts.MoreArtists(pileExtra) : null),
                            ],
                        },
                        new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0f, Children = rows.ToArray() },
                    ],
                });
        }

        static void ToggleArtist(LensCell lens, Artist artist)
        {
            var current = LensFilters(lens);
            LensWrite(lens, LikedFactsRules.IsArtistLens(current, artist)
                ? current.WithArtist(0)
                : current.WithArtist(LikedFactsRules.ArtistKey(artist), artist.Knows(ArtistFields.Name) ? artist.Name : null));
        }

        void DemandPortraits()
        {
            var ranked = _latest?.Ranked;
            if (ranked is not { Count: > 0 }) return;
            var artists = new Artist[ranked.Count];
            for (int i = 0; i < artists.Length; i++) artists[i] = ranked[i].Artist;
            Entities.Ensure(artists, ArtistFields.Identity);
        }

        void ToggleMore()
        {
            var overlay = _overlay;
            var handle = _handle;
            var p = _latest;
            if (Controls.IsNullOverlay(overlay) || handle is null || p is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            if (_moreOpen is { } flag) flag.Value = true;
            var lens = p.Lens;
            var ranked = p.Ranked;
            handle.Value = overlay.Open(
                () => _anchor?.Value ?? default,
                () => Flyout(ranked, LensFilters(lens), lens, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => { handle.Value = null; if (_moreOpen is { } f) f.Value = false; };
        }

        void MoreKey(KeyEventArgs e)
        {
            if (e.KeyCode is not (Keys.Down or Keys.F4)) return;
            ToggleMore();
            e.Handled = true;
        }

        /// <summary>The bare row (its tooltip wrap is the caller's: <c>ToolTip.WrapStable</c> over <see cref="RowFor"/>).</summary>
        static Element NameRow(string name, string count, bool lit, Action? onClick)
        {
            bool live = onClick is not null;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                Padding = new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
                Role = live ? AutomationRole.Button : AutomationRole.None,
                Focusable = live, Cursor = live ? CursorId.Hand : CursorId.Arrow,
                FocusVisualMargin = live ? Design.FocusInsetRow : null,
                Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
                HoverFill = !live ? ColorF.Transparent : lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
                HoverScale = live ? Design.Motion.ScaleSubtle.Hover : 1f,
                PressScale = live ? Design.Motion.ScaleSubtle.Press : 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = onClick,
                Children =
                [
                    Caption(name) with
                    {
                        Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                        Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    Caption("·") with { Color = Tok.TextTertiary, Shrink = 0f },
                    Caption(count) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
                ],
            };
        }

        Element MoreRow(int extra) => new BoxEl
        {
            Key = "who:more", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetRow,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = _toggleMore, OnKeyDown = _moreKey,
            Children =
            [
                Sidebar.Chevron.Disclosure(_moreOpenRead),
                Caption(Strings.Detail.LikedFacts.MoreArtists(extra)) with
                { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };

        static Element Flyout(IReadOnlyList<LikedFactsRules.ArtistCount> ranked, Track.FilterState filters, LensCell lens, Action close)
        {
            var culture = CultureInfo.CurrentCulture;
            var rows = new Element[ranked.Count];
            for (int i = 0; i < ranked.Count; i++)
            {
                var artist = ranked[i].Artist;
                string name = artist.Knows(ArtistFields.Name) ? artist.Name : "";
                bool lensable = LikedFactsRules.ArtistKey(artist) != 0;
                bool lit = LikedFactsRules.IsArtistLens(filters, artist);
                Action? click = lensable ? () => { ToggleArtist(lens, artist); close(); } : null;
                var row = new BoxEl
                {
                    Key = "all:" + i, Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = CornerRadius4.All(6f),
                    Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
                    Role = lensable ? AutomationRole.MenuItem : AutomationRole.None,
                    Focusable = lensable, Cursor = lensable ? CursorId.Hand : CursorId.Arrow,
                    OnClick = click,
                    Children =
                    [
                        PersonPicture.Create("", 32f, displayName: name, imageSourcePath: Controls.ArtUrl(artist.ImageId)),
                        Caption(name) with
                        {
                            Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        Caption(ranked[i].Count.ToString(culture)) with { Color = Tok.TextTertiary, Shrink = 0f },
                    ],
                };
                rows[i] = lensable ? row.Interactive(Interaction.Subtle) : row;
            }

            var list = new BoxEl { Direction = 1, Gap = Spacing.XXS, Width = 264f, Children = rows };
            return new BoxEl
            {
                Direction = 1, Width = 280f, MaxHeight = 360f, Padding = Edges4.All(Spacing.S), Gap = Spacing.S,
                Children =
                [
                    Design.Type.Eyebrow(Loc.Get(Strings.Detail.LikedFacts.AllArtists)) with { Color = Tok.TextTertiary },
                    ScrollView(list) with { Width = 264f, MaxHeight = 320f, ContentSized = true, AutoEdgeFade = true, Grow = 0f },
                ],
            };
        }
    }

    // ══ 8. YOUR BLEND ════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record FactBlendProps(LikedRow[] Rows, IReadOnlyList<LikedFactsRules.TagShare> Shares, LensCell Lens,
                             IReadSignal<Track.FilterState> Filters);

    /// <summary>The blend card's strings for one list of shares, minted ONCE per list (W3-A3: the card used to format
    /// every share and interpolate every tooltip on each render). <see cref="Share"/> is the slice's fraction as a
    /// percentage; <see cref="Tip"/> its bubble ("title · N songs · share"); for a tail (<see cref="ForTail"/>)
    /// <see cref="TailTip"/> is the opened bar's bubble, whose share is OF THE TAIL. Pure: a function of the list, the
    /// culture and the localisation table.</summary>
    public sealed class BlendLabels
    {
        public readonly string[] Share, Tip, TailTip;
        /// <summary><see cref="ForShares"/>: the header's tagged population ("N songs"); <see cref="ForTail"/>: the
        /// disclosure's "N more" label and the tail's own share of the tagged likes.</summary>
        public readonly string Total;

        BlendLabels(string[] share, string[] tip, string[] tailTip, string total)
        {
            Share = share;
            Tip = tip;
            TailTip = tailTip;
            Total = total;
        }

        /// <summary>The bar's slices: each share is of the TAGGED likes; <see cref="Total"/> is the tagged population.</summary>
        public static BlendLabels ForShares(IReadOnlyList<LikedFactsRules.TagShare> shares, CultureInfo culture)
        {
            int n = shares.Count;
            var share = new string[n];
            var tip = new string[n];
            for (int i = 0; i < n; i++)
            {
                share[i] = shares[i].Fraction.ToString("P0", culture);
                tip[i] = Strings.Detail.LikedFacts.ShareTip(shares[i].Title, shares[i].Count, share[i]);
            }
            return new BlendLabels(share, tip, Array.Empty<string>(), Strings.Detail.SongCount(LikedFactsRules.TaggedTotal(shares)));
        }

        /// <summary>The tail's named descriptors: <see cref="Share"/>/<see cref="Tip"/> as for a slice (of the tagged
        /// likes), <see cref="TailTip"/> with the descriptor's share of the tail itself, and <see cref="Total"/> the
        /// disclosure's "N more" over the named count plus the unnamed remainder.</summary>
        public static BlendLabels ForTail(in LikedFactsRules.BlendTail tail, CultureInfo culture)
        {
            var named = tail.Named;
            int n = named.Count;
            var share = new string[n];
            var tip = new string[n];
            var tailTip = new string[n];
            float denominator = Math.Max(1, tail.Count);
            for (int i = 0; i < n; i++)
            {
                share[i] = named[i].Fraction.ToString("P0", culture);
                tip[i] = Strings.Detail.LikedFacts.ShareTip(named[i].Title, named[i].Count, share[i]);
                tailTip[i] = Strings.Detail.LikedFacts.TailShareTip(named[i].Title, named[i].Count, (named[i].Count / denominator).ToString("P0", culture));
            }
            return new BlendLabels(share, tip, tailTip, Strings.Detail.LikedFacts.MoreTags(n + tail.MoreTags));
        }
    }

    /// <summary>"Your blend": the primary-descriptor partition as one stacked bar, the remainder drawn as WHAT IT IS (one
    /// thin proportional tick per descriptor, each a real lens), and a disclosure that opens the tail IN PLACE at full
    /// width — the WinUI Expander motion on the host's LAYOUT height, so the cards below reflow.</summary>
    sealed class BlendCardHost : Component
    {
        const float BarHeight = 8f, BarRadius = 4f, SegGap = 2f, LegendDot = 8f, TickGap = 1f, TickMinWidth = 2f;
        const float TailLegendFloor = 0.01f;
        static readonly float[] s_sliceAlpha = [1f, 0.8f, 0.6f, 0.45f, 0.3f];
        static readonly float[] s_tickAlpha = [0.16f, 0.22f, 0.30f];
        static readonly float[] s_tailSegAlpha = [0.55f, 0.38f];

        static readonly LayoutTransition s_bodyReveal = new(TransitionChannels.Size, MotionTok.DisclosureExpand.ToDynamics(),
            Size: SizeMode.Reflow, ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(), Anchor: SizeAnchor.Leading);
        static readonly LayoutTransition s_tailBarReveal = new(TransitionChannels.Opacity, MotionTok.DisclosureExpand.ToDynamics(),
            Enter: new EnterExit(Sx: 0.55f, Opacity: 0f, Active: true));
        static readonly LayoutTransition s_tailRowReveal = new(TransitionChannels.Opacity, MotionTok.DisclosureExpand.ToDynamics(),
            Enter: new EnterExit(Dy: Spacing.XS, Opacity: 0f, Active: true));

        FactBlendProps? _latest;
        Signal<bool>? _open, _shown;
        Ref<NodeHandle>? _host;
        readonly Action _toggle;
        readonly Func<bool> _openRead;
        readonly Action<string> _toggleTag;
        readonly Func<LikedFactsRules.BlendTail> _tail;

        // The strings, per list identity (W3-A3): the summary hands the same Shares instance until it re-settles, and
        // the tail is a memo over (Rows, Shares) — so both label sets are minted once per model, not per render.
        BlendLabels? _labels, _tailLabels;
        IReadOnlyList<LikedFactsRules.TagShare>? _labelsFor, _tailLabelsFor;
        readonly string[] _segKey = new string[FactBlendSlices], _legKey = new string[FactBlendSlices];
        string[] _tickKey = [];
        float _otherFor = -1f;
        string _otherText = "";

        // Per-slot state for the primary slices: written by Render, read by the MOUNT-STABLE segment / legend factories
        // that ToolTip.WrapStable invokes inside each tooltip's own render (the live filter is read THERE, so a lens
        // flip relights a slice with no re-push from the card).
        readonly string[] _sliceTitle = new string[FactBlendSlices];
        readonly Action[] _sliceClick = new Action[FactBlendSlices];
        readonly Func<Element>[] _segFactory = new Func<Element>[FactBlendSlices], _legFactory = new Func<Element>[FactBlendSlices];

        public BlendCardHost()
        {
            _toggle = Toggle;
            _openRead = () => _open?.Value ?? false;
            _toggleTag = ToggleTag;
            _tail = ComputeTail;
            for (int i = 0; i < FactBlendSlices; i++)
            {
                int slot = i;
                _sliceTitle[i] = "";
                _sliceClick[i] = () => _toggleTag(_sliceTitle[slot]);   // the CURRENT title of the slot, at click time
                _segFactory[i] = () => SegmentFor(slot);
                _legFactory[i] = () => LegendFor(slot);
            }
        }

        /// <summary>The "Other" segment opened up — <see cref="LikedFactsRules.BlendOther"/> ranks every descriptor of
        /// every row, so it runs once per (rows, shares), never per render.</summary>
        LikedFactsRules.BlendTail ComputeTail()
        {
            var p = _latest!;
            return LikedFactsRules.BlendOther(p.Rows, p.Shares.Count, int.MaxValue);
        }

        bool LitNow(int i) => _latest is { } p && LikedFactsRules.IsTagLens(p.Filters.Value, _sliceTitle[i]);

        /// <summary>A lensed slice goes to FULL accent whatever its rank.</summary>
        static ColorF SliceInk(int i, bool lit)
            => lit ? Tok.AccentDefault : Tok.AccentDefault with { A = s_sliceAlpha[Math.Min(i, s_sliceAlpha.Length - 1)] };

        Element SegmentFor(int i) => Segment(SliceInk(i, LitNow(i)), _sliceClick[i]);

        Element LegendFor(int i)
        {
            bool lit = LitNow(i);
            return LegendEntry(SliceInk(i, lit), _sliceTitle[i], _labels?.Share[i] ?? "", lit, _sliceClick[i]);
        }

        public override Element Render()
        {
            var p = UseProps<FactBlendProps>();
            _latest = p;
            var culture = CultureInfo.CurrentCulture;
            var filters = p.Filters.Value;
            _open = UseSignal(false);
            // The body's MOUNT lags `open` on collapse: the clip shrinks OVER real content; the watcher flips this at settle.
            _shown = UseSignal(false);
            _host = UseRef<NodeHandle>(default);
            var tail = UseMemo(_tail, DepKey.FromRef(p.Rows, p.Shares));
            bool isOpen = _open.Value;
            bool showBody = _shown.Value;
            bool closing = showBody && !isOpen;

            var shares = p.Shares;
            float listed = 0f;
            for (int i = 0; i < shares.Count; i++) listed += shares[i].Fraction;
            float other = MathF.Max(0f, 1f - listed);
            // The tail is drawn only when it is actually there — a phantom sliver would be a lie at one pixel wide.
            bool hasTail = other > 0.005f && tail.Named.Count > 0;

            if (!ReferenceEquals(_labelsFor, shares))
            {
                _labelsFor = shares;
                _labels = BlendLabels.ForShares(shares, culture);
                for (int i = 0; i < shares.Count && i < FactBlendSlices; i++)
                {
                    _segKey[i] = "seg:" + shares[i].Title;
                    _legKey[i] = "leg:" + shares[i].Title;
                }
            }
            if (hasTail && !ReferenceEquals(_tailLabelsFor, tail.Named))
            {
                _tailLabelsFor = tail.Named;
                _tailLabels = BlendLabels.ForTail(in tail, culture);
                _tickKey = new string[tail.Named.Count];
                for (int i = 0; i < _tickKey.Length; i++) _tickKey[i] = "tick:" + tail.Named[i].Title;
            }
            if (_otherFor != other)
            {
                _otherFor = other;
                _otherText = other.ToString("P0", culture);
            }
            var labels = _labels!;

            int slices = Math.Min(shares.Count, FactBlendSlices);
            var segments = new List<Element>(slices + 1);
            var legend = new List<Element>(slices + 1);
            for (int i = 0; i < slices; i++)
            {
                _sliceTitle[i] = shares[i].Title;
                segments.Add(ToolTip.WrapStable(_segFactory[i], labels.Tip[i], grow: shares[i].Fraction, showDelayMs: FactTipDelayMs) with { Key = _segKey[i] });
                legend.Add(ToolTip.WrapStable(_legFactory[i], labels.Tip[i], showDelayMs: FactTipDelayMs) with { Key = _legKey[i] });
            }
            if (hasTail)
            {
                segments.Add(TailTicks(tail.Named, _tailLabels!, other, in filters));
                legend.Add(MoreButton(_tailLabels!, isOpen));
            }

            var bar = new BoxEl
            {
                Direction = 0, Height = BarHeight, Gap = SegGap, MinWidth = 0f,
                Corners = CornerRadius4.All(BarRadius), ClipToBounds = true, HitTestPassThrough = true,
                Children = segments.ToArray(),
            };

            var hostRef = _host;
            var host = new BoxEl
            {
                Key = "blend-body-host", Direction = 1, ClipToBounds = true, MinWidth = 0f,
                Height = isOpen ? float.NaN : 0f,
                Animate = s_bodyReveal,
                OnRealized = h => hostRef.Value = h,
                Children = showBody && hasTail ? [Body(in tail, _tailLabels!, culture, in filters)] : [],
            };

            Element collapsed = new BoxEl
            {
                Key = "blend-top", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    bar,
                    new BoxEl { Key = "blend-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f, Children = legend.ToArray() },
                ],
            };
            // The body host hangs off an UNGAPPED outer column: a zero-height host inside the gapped card column would
            // leave 8 DIP of dead air under the legend while the card is shut (ch 07 §9, parity 80).
            var stack = new List<Element>(3) { collapsed, host };
            if (closing)
            {
                var shown = _shown;
                stack.Add(Embed.Comp(() => new BlendCollapseWatcher { Host = () => hostRef.Value, Shown = shown })
                          with { Key = "blend-collapse-watch" });
            }

            return FactCard("fact:blend",
                // The header count is the TAGGED population, not the library.
                CardHead(Loc.Get(Strings.Detail.LikedFacts.Blend), labels.Total),
                new BoxEl { Direction = 1, MinWidth = 0f, Children = stack.ToArray() });
        }

        void Toggle()
        {
            if (_open is null || _shown is null) return;
            bool next = !_open.Peek();
            _open.Value = next;
            if (next) _shown.Value = true;      // mounting FIRST is what lets the reflow seed from a real content height
        }

        /// <summary>The blend drives the CHIPS' facet: "Pop" in the bar and "Pop" in the chip bar are the same question.</summary>
        void ToggleTag(string title)
        {
            if (_latest is not { } p) return;
            var current = LensFilters(p.Lens);
            LensWrite(p.Lens, current with { Tag = LikedFactsRules.IsTagLens(current, title) ? null : title });
        }

        /// <summary>The bare segment: its tooltip wrap is the caller's (<c>ToolTip.WrapStable</c> over
        /// <see cref="SegmentFor"/> for a primary slice, <c>ToolTip.Wrap</c> in the opened tail).</summary>
        static Element Segment(ColorF ink, Action onClick, float minWidth = 0f)
        {
            Element fill = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = ink, HoverFill = Tok.AccentTextPrimary,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                HitTestVisible = false,
            };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = minWidth,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = Design.FocusInsetBordered,
                OnClick = onClick,
                Children = [fill],
            };
        }

        Element TailTicks(IReadOnlyList<LikedFactsRules.TagShare> tail, BlendLabels labels, float other, in Track.FilterState filters)
        {
            var ticks = new Element[tail.Count];
            for (int i = 0; i < tail.Count; i++)
            {
                string title = tail[i].Title;
                bool lit = LikedFactsRules.IsTagLens(filters, title);
                var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = s_tickAlpha[i % s_tickAlpha.Length] };
                void Click() => _toggleTag(title);
                ticks[i] = ToolTip.Wrap(Segment(ink, Click, TickMinWidth), labels.Tip[i], grow: tail[i].Count, showDelayMs: FactTipDelayMs)
                    with { Key = _tickKey[i] };
            }
            return new BoxEl
            {
                Key = "seg:tail", Direction = 0, Gap = TickGap, Grow = other, Basis = 0f, MinWidth = 0f,
                ClipToBounds = true, HitTestPassThrough = true,
                Children = ticks,
            };
        }

        /// <summary>The legend's last entry: chevron + "44 more" ⇄ "Show less" + the share, which stays put.</summary>
        Element MoreButton(BlendLabels tailLabels, bool isOpen)
        {
            string label = isOpen ? Loc.Get(Strings.Detail.LikedFacts.ShowLess) : tailLabels.Total;
            return new BoxEl
            {
                Key = "leg:more", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
                Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = Design.FocusInsetRow,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = _toggle,
                Children =
                [
                    Sidebar.Chevron.Disclosure(_openRead),
                    Caption(label) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Caption(_otherText) with { Color = Tok.TextTertiary, MaxLines = 1 },
                ],
            };
        }

        /// <summary>The bare legend row: its tooltip wrap is the caller's (<c>ToolTip.WrapStable</c> over
        /// <see cref="LegendFor"/> for a primary slice, <c>ToolTip.Wrap</c> in the opened tail).</summary>
        static Element LegendEntry(ColorF ink, string label, string share, bool lit, Action onClick)
        {
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
                Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = Design.FocusInsetRow,
                Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
                HoverFill = lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = onClick,
                Children =
                [
                    new BoxEl { Width = LegendDot, Height = LegendDot, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = ink },
                    Caption(label) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Caption(share) with { Color = Tok.TextTertiary, MaxLines = 1 },
                ],
            };
        }

        /// <summary>The opened body: a hairline, "The other N", the tail at FULL width (its percentages are OF THE TAIL and
        /// say so), a legend down to 1 % and a count of the rest. Built only while the disclosure is open (a click), so
        /// its per-row strings that are not in <paramref name="labels"/> are minted here.</summary>
        Element Body(in LikedFactsRules.BlendTail tail, BlendLabels labels, CultureInfo culture, in Track.FilterState filters)
        {
            var named = tail.Named;
            var segs = new Element[named.Count];
            for (int i = 0; i < named.Count; i++)
            {
                string title = named[i].Title;
                bool lit = LikedFactsRules.IsTagLens(filters, title);
                var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = s_tailSegAlpha[i % s_tailSegAlpha.Length] };
                void Click() => _toggleTag(title);
                segs[i] = ToolTip.Wrap(Segment(ink, Click), labels.TailTip[i], grow: named[i].Count, showDelayMs: FactTipDelayMs)
                    with { Key = "tseg:" + title };
            }

            var tailBar = new BoxEl
            {
                Key = "tail-bar", Direction = 0, Height = BarHeight, Gap = SegGap, MinWidth = 0f,
                Corners = CornerRadius4.All(BarRadius), ClipToBounds = true, HitTestPassThrough = true,
                TransformOriginX = 0f,
                Enter = s_tailBarReveal.Enter, Layout = s_tailBarReveal,
                Children = segs,
            };

            var (rows, underFloor) = LikedFactsRules.TailSplit(named, TailLegendFloor);
            var legendRows = new List<Element>(rows.Count + 1);
            for (int i = 0; i < rows.Count; i++)
            {
                string title = rows[i].Title;
                bool lit = LikedFactsRules.IsTagLens(filters, title);
                var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = s_tailSegAlpha[i % s_tailSegAlpha.Length] };
                string share = rows[i].Fraction.ToString("P0", culture);
                string tip = Strings.Detail.LikedFacts.ShareTip(title, rows[i].Count, share);
                void Click() => _toggleTag(title);
                legendRows.Add(new BoxEl
                {
                    Key = "tlw:" + title, Direction = 0, Shrink = 0f,
                    Animate = s_tailRowReveal with { DelayMs = Design.Entrance.DelayMs(i) },
                    Children = [ToolTip.Wrap(LegendEntry(ink, title, share, lit, Click), tip, showDelayMs: FactTipDelayMs) with { Key = "tl:" + title }],
                });
            }
            if (underFloor > 0)
                legendRows.Add(new BoxEl
                {
                    Key = "tl:underfloor", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Padding = new Edges4(4f, 2f, 4f, 2f),
                    Animate = s_tailRowReveal with { DelayMs = Design.Entrance.DelayMs(rows.Count) },
                    Children = [Caption(Strings.Detail.LikedFacts.UnderFloor(underFloor)) with
                                { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                });

            return new BoxEl
            {
                Key = "blend-body", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                // The open state's breathing room lives INSIDE the body, so it is revealed by the same clip.
                Padding = new Edges4(0f, Spacing.S, 0f, 0f),
                Children =
                [
                    new BoxEl { Key = "tail-rule", Height = 1f, Fill = Tok.StrokeCardDefault, HitTestVisible = false },
                    CardHead(Strings.Detail.LikedFacts.TailHeader(tail.Named.Count + tail.MoreTags), Strings.Detail.LikedFacts.TailSongs(tail.Count)),
                    tailBar,
                    new BoxEl { Key = "tail-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f, Children = legendRows.ToArray() },
                ],
            };
        }
    }

    /// <summary>Per-frame poller mounted ONLY while the body's collapse reflow runs: the moment the host's reflow track
    /// settles, flip the mount signal off (the kit's own watcher is internal to FluentGpu.Controls).</summary>
    sealed class BlendCollapseWatcher : Component
    {
        public required Func<NodeHandle> Host;
        public required Signal<bool> Shown;

        public override Element Render()
        {
            var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (only during the ~167 ms reflow)
            UseEffect(() =>
            {
                if (!Shown.Peek()) return;
                var anim = Context.Anim;
                var scene = Context.Scene;
                var node = Host();
                if (anim is null || scene is null || node.IsNull || !scene.IsLive(node) || !anim.HasTracks(node))
                    Shown.Value = false;
            }, tick);
            return new BoxEl { HitTestVisible = false };
        }
    }

    // ══ 9. THE PILLS ═════════════════════════════════════════════════════════════════════════════════════════════════

    // 24-unit stroke glyphs, parsed ONCE (static readonly — a per-render parse would re-tessellate every frame).
    static readonly PathData s_factCalendarGlyph = PathDataParser.Parse("M4 5h16v15H4zM4 10h16M8 3v4M16 3v4", PathContentEpoch.Mint(), FillRule.NonZero);
    static readonly PathData s_factMetronomeGlyph = PathDataParser.Parse("M9 4h6l3 16H6zM12 15l5-9", PathContentEpoch.Mint(), FillRule.NonZero);
    static readonly PathData s_factTagGlyph = PathDataParser.Parse("M4 4h7l9 9-7 7-9-9zM8.5 8.5h.01", PathContentEpoch.Mint(), FillRule.NonZero);

    /// <summary>The distribution facts that did NOT earn a card — each a one-line label that is still a LENS. A flat
    /// blend has no one tag to open and is the one plain (non-button) pill.</summary>
    static Element? FactPillRow(LikedFactsRules.FactsSummary s, bool yearsPill, bool tempoPill, bool blendPill, CultureInfo culture,
                            in Track.FilterState filters, LensCell lens)
    {
        var pills = new List<Element>(3);
        string mostly = Loc.Get(Strings.Detail.LikedFacts.PillMostly);

        if (yearsPill && s.YearBuckets.Count > 0 && s.YearsDominance is { Known: > 0, TopIndex: >= 0 } d)
        {
            var b = s.YearBuckets[d.TopIndex];
            string range = b.YearMin == b.YearMax
                ? b.YearMin.ToString(culture)
                : Strings.Detail.LikedFacts.YearRange(b.YearMin.ToString(culture), b.YearMax.ToString(culture));
            void ToggleYears()
            {
                var current = LensFilters(lens);
                LensWrite(lens, LikedFactsRules.IsYearLens(current, b) ? current.WithReleaseYear(0, 0) : current.WithReleaseYear(b.YearMin, b.YearMax));
            }
            pills.Add(FactPill("pill:years", s_factCalendarGlyph, mostly, range, Strings.Detail.LikedFacts.PillCount(b.Count, d.Known),
                           LikedFactsRules.IsYearLens(filters, b), ToggleYears, Strings.Detail.LikedFacts.PillYearsTip(range, b.Count, d.Known)));
        }

        if (tempoPill && s.Tempo.Dominance is { Known: > 0, TopIndex: >= 0 } td)
        {
            var band = (Track.TempoBand)(td.TopIndex + 1);
            var stats = s.Tempo.Stats;
            void ToggleTempo()
            {
                var current = LensFilters(lens);
                LensWrite(lens, current with { Tempo = LikedFactsRules.IsTempoLens(current, band) ? Track.TempoBand.Any : band });
            }
            pills.Add(FactPill("pill:tempo", s_factMetronomeGlyph, mostly, TempoBandText.Range(band),
                           Strings.Detail.LikedFacts.PillCount(s.Tempo.Count(td.TopIndex), td.Known),
                           LikedFactsRules.IsTempoLens(filters, band), ToggleTempo,
                           Strings.Detail.LikedFacts.PillTempoTip(Math.Round(stats.Median).ToString(culture), stats.Known, stats.Total)));
        }

        if (blendPill && s.BlendDominance is { AboveFloor: > 0, TopTitle: { } title } bd)
        {
            string share = bd.TopShare.ToString("P0", culture);
            if (bd.Flat)
            {
                string flat = Strings.Detail.LikedFacts.PillBlendFlat(bd.Styles, LikedFactsRules.BlendFlat.ToString("P0", culture));
                pills.Add(FactPill("pill:blend", s_factTagGlyph, "", flat, null, lit: false, toggle: null, flat));
            }
            else
            {
                void ToggleTag()
                {
                    var current = LensFilters(lens);
                    LensWrite(lens, current with { Tag = LikedFactsRules.IsTagLens(current, title) ? null : title });
                }
                pills.Add(FactPill("pill:blend", s_factTagGlyph, "", title, share, LikedFactsRules.IsTagLens(filters, title), ToggleTag,
                               Strings.Detail.LikedFacts.ShareTip(title, bd.TopCount, share)));
            }
        }

        if (pills.Count == 0) return null;
        return new BoxEl
        {
            Key = "fact:pills", Direction = 0, Wrap = true, Gap = 6f, MinWidth = 0f,
            Enter = FactFadeUp, Layout = FactShove,
            Children = pills.ToArray(),
        };
    }

    static Element FactPill(string key, PathData glyph, string lead, string strong, string? trailing, bool lit, Action? toggle, string tip)
    {
        bool live = toggle is not null;
        var ink = lit ? Tok.AccentTextPrimary : Tok.TextTertiary;
        var kids = new List<Element>(4)
        {
            new PathEl
            {
                Geometry = glyph, Width = 13f, Height = 13f, ViewBoxW = 24f, ViewBoxH = 24f, Shrink = 0f,
                StrokeColor = ink, Stroke = new StrokeStyle(2.4f, LineCap.Round, LineJoin.Round),
            },
        };
        if (lead.Length > 0) kids.Add(Caption(lead) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1 });
        kids.Add(Caption(strong) with
        {
            Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            Shrink = 1f, MinWidth = 0f,
        });
        if (trailing is { Length: > 0 }) kids.Add(Caption(trailing) with { Color = Tok.TextTertiary, MaxLines = 1 });

        var rest = lit ? Tok.AccentSubtle : Tok.FillCardDefault;
        var pill = new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 26f, MinWidth = 0f,
            Padding = new Edges4(8f, 0f, 10f, 0f), Corners = CornerRadius4.All(13f),
            Fill = rest, BorderWidth = 1f, BorderColor = lit ? Tok.AccentSecondary : Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow, FocusVisualMargin = live ? Design.FocusInsetRow : null,
            // A non-live pill pins hover AND pressed to its own idle fill: a plain pill must not animate (W23 h).
            HoverFill = live ? (lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary) : rest,
            PressedFill = live ? Tok.FillSubtleTertiary : rest,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = toggle,
            Children = kids.ToArray(),
        };
        return ToolTip.Wrap(pill, tip, showDelayMs: FactTipDelayMs) with { Key = key };
    }

    // ══ 10. THE LENS HEADER ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lens wording — ONE place the week range is formatted, so the bar's tooltip and the header's pill
    /// cannot name two different weeks.</summary>
    static class LensText
    {
        public static (string Start, string End) RangeParts(long afterMs, long beforeMs, CultureInfo culture)
            => (Stamp(afterMs, culture), Stamp(beforeMs, culture));

        static string Stamp(long unixMs, CultureInfo culture)
            => unixMs == 0L ? "…" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MMM d", culture);

        public static string WeekLabel(long afterMs, long beforeMs, CultureInfo culture, bool liked)
        {
            var (start, end) = RangeParts(afterMs, beforeMs, culture);
            string range = Strings.Detail.LikedFacts.WeekRange(start, end);
            return liked ? Strings.Detail.LikedFacts.LensWeek(range) : Strings.Detail.LikedFacts.LensAdded(range);
        }

        public static string YearLabel(int min, int max, CultureInfo culture)
        {
            if (min != 0 && min == max) return Strings.Detail.LikedFacts.LensYear(min.ToString(culture));
            return Strings.Detail.LikedFacts.LensYears(min == 0 ? "…" : min.ToString(culture),
                                                       max == 0 ? "…" : max.ToString(culture));
        }
    }

    sealed record LensHeaderProps(LensCell Lens, DetailKind Kind);

    /// <summary>The group header the track list shows while a rail fact is lensing it: one pill per active lens (Week ·
    /// Artist · Tag · Year · Tempo), each with its own ×, and the visible count. 0 DIP while no lens is on.</summary>
    sealed class LensHeaderHost : Component
    {
        const float PillHeight = 28f;

        LensCell? _lens;
        Track.TableLive? _live;
        readonly Func<Action?> _bridge;

        public LensHeaderHost() => _bridge = Bridge;

        public override Element Render()
        {
            var p = UseProps<LensHeaderProps>();
            var live = UseContext(Track.TableLiveSlot);
            _lens = p.Lens;
            _live = live;
            UseEffect(_bridge, DepKey.FromRef(live, p.Lens));

            if (live is null) return new BoxEl { Key = "lens-header:off", HitTestVisible = false };
            var filter = live.View.Value.Filters;
            int visible = live.Visible.Value;
            var lenses = LikedFactsRules.ActiveLenses(filter);
            if (lenses == LikedFactsRules.LikedLens.None)
                return new BoxEl { Key = "lens-header:off", HitTestVisible = false };

            var culture = CultureInfo.CurrentCulture;
            bool liked = p.Kind == DetailKind.Liked;
            var kids = new List<Element>(6);
            if ((lenses & LikedFactsRules.LikedLens.Week) != 0)
                kids.Add(LensPill("lens:week", LensText.WeekLabel(filter.AddedAfterMs, filter.AddedBeforeMs, culture, liked),
                                  LikedFactsRules.LikedLens.Week, p.Lens));
            if ((lenses & LikedFactsRules.LikedLens.Artist) != 0)
            {
                // The display name when the filter carries it, else the credited row's — a lens must always say what it is.
                var artist = new Artist(filter.ArtistSlot);
                string name = filter.ArtistName is { Length: > 0 } n ? n
                    : artist.IsValid && artist.Knows(ArtistFields.Name) ? artist.Name : "";
                kids.Add(LensPill("lens:artist", name, LikedFactsRules.LikedLens.Artist, p.Lens));
            }
            if ((lenses & LikedFactsRules.LikedLens.Tag) != 0)
                kids.Add(LensPill("lens:tag", filter.Tag ?? "", LikedFactsRules.LikedLens.Tag, p.Lens));
            if ((lenses & LikedFactsRules.LikedLens.Year) != 0)
                kids.Add(LensPill("lens:year", LensText.YearLabel(filter.ReleaseYearMin, filter.ReleaseYearMax, culture),
                                  LikedFactsRules.LikedLens.Year, p.Lens));
            if ((lenses & LikedFactsRules.LikedLens.Tempo) != 0)
                kids.Add(LensPill("lens:tempo", TempoBandText.Range(filter.Tempo), LikedFactsRules.LikedLens.Tempo, p.Lens));
            return new BoxEl
            {
                Key = "lens-header", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Height = PillHeight, MinWidth = 0f,
                Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                Enter = FactFadeUp, Layout = FactShove,
                Children =
                [
                    // The pills take the row (and shrink, ellipsising, before it could wrap)…
                    new BoxEl
                    {
                        Key = "lens-pills", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Children = kids.ToArray(),
                    },
                    // …and the VISIBLE row count sits on the right — including zero, which is a real answer (ch 04 item 34).
                    Caption(Strings.Detail.SongCount(visible)) with { Key = "lens-count", Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
                ],
            };
        }

        /// <summary>The bridge: publish the table this header lives under into the page's cell; give it back on unmount
        /// only if nobody replaced it (a re-keyed table mounts its new header before the old one's cleanup runs).</summary>
        Action? Bridge()
        {
            var lens = _lens;
            var live = _live;
            if (lens is null) return null;
            if (!ReferenceEquals(lens.Live.Peek(), live)) lens.Live.Value = live;
            return () => { if (ReferenceEquals(lens.Live.Peek(), live)) lens.Live.Value = null; };
        }

        static Element LensPill(string key, string label, LikedFactsRules.LikedLens lensKind, LensCell lens)
        {
            void Clear() => LensWrite(lens, LikedFactsRules.ClearLens(LensFilters(lens), lensKind));

            Element close = new BoxEl
            {
                Key = "clear", Width = 22f, Height = 22f, Shrink = 0f, Corners = CornerRadius4.All(11f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = Design.FocusInsetRow,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = Clear,
                Children = [Icon(Icons.ChromeClose, 10f, Tok.TextSecondary)],
            };

            return new BoxEl
            {
                Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
                Shrink = 1f, MinWidth = 0f, Height = PillHeight,
                Padding = new Edges4(Spacing.M, 0f, 3f, 0f), Corners = CornerRadius4.All(Radii.Full),
                Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentSecondary,
                Enter = FactFadeUp, Layout = FactShove,
                Children =
                [
                    new TextEl(label)
                    {
                        Size = 12f, Weight = 600, Color = Tok.TextPrimary,
                        Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    ToolTip.Wrap(close, Loc.Get(Strings.Detail.LikedFacts.LensClear), showDelayMs: FactTipDelayMs),
                ],
            };
        }
    }
}
