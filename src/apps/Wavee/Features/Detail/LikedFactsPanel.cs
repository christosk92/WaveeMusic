using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Liked Songs rail's facts bento — the mount seam. <see cref="Has"/> answers whether there is anything
/// honest to say; <see cref="Panel"/> hands back the stack. Both are the ONLY surface the detail rail sees, so the
/// component below can change shape without touching the rail.</summary>
internal static class LikedFacts
{
    /// <summary>True when this collection is Liked or a playlist (<see cref="BadgeStyle.OwnerRow"/>) AND at least one
    /// card would actually mount. Albums stay on <c>ReleasePanel</c> (<see cref="BadgeStyle.TypeYear"/>).
    ///
    /// <para>Honesty, not occupancy: an editorial list with one bulk stamp and no years yet mounts nothing; Liked
    /// with a stamp always has the week card; artists and blend count every track, stamps or not.</para></summary>
    internal static bool Has(DetailModel m, BadgeStyle badges = BadgeStyle.None)
    {
        if (m is null) return false;
        bool liked = LikedSongsArtwork.IsLikedUri(m.ContextUri);
        if (!liked && badges != BadgeStyle.OwnerRow) return false;
        var tracks = m.Tracks;
        if (tracks is null || tracks.Count == 0) return false;

        // Runs before Panel on EVERY rail render, so it must stay allocation-free early-exit scans — the artists card
        // (any keyed credit) is what practically every list has; the rest are the same floors the cards use. The
        // rankings/partitions themselves are computed once per list inside the panel (LikedFactsRules.Summarize).
        if (LikedFactsRules.AnyArtistCredit(tracks)) return true;
        if (AnyStamped(tracks)) return true;
        if (LikedFactsRules.HasReleaseYears(tracks)) return true;
        return LikedFactsRules.OldestRelease(tracks) is not null;
    }

    internal static bool AnyStamped(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (LikedFactsRules.TryStamp(tracks[i], out _)) return true;
        return false;
    }

    /// <summary>The panel. Props are RE-PUSHED (Embed.Comp's props overload), so a like/unlike — which refreshes
    /// <c>LibraryStore.Liked</c> in place and hands the page a new track list — flows straight through to a recompute,
    /// while a parent re-render carrying the SAME list is coalesced to nothing (E10).
    ///
    /// <para><paramref name="outerPadding"/> false is the rail's arm: the rail owns the column's side padding, exactly
    /// as it does for <c>AlbumTrailing.ReleasePanel</c>.</para></summary>
    internal static Element Panel(DetailModel m, DetailHandlers h, bool outerPadding = true)
        => Embed.Comp(new LikedFactsPanel.Props(m.Tracks, m.ContextUri, h, outerPadding),
                      static () => new LikedFactsPanel()) with { Key = "liked-facts" };
}

/// <summary>The facts stack itself: This week (the +N and a 12-week bar strip), Most liked (a face pile and the names
/// behind it), Your blend (the primary-descriptor partition as one bar plus its legend), Rediscover (what you liked
/// this week last year, and a way to play it), and the since-line.
///
/// <para>ALL arithmetic lives in <see cref="LikedFactsRules"/> — this file decides only what to draw and when not to.
/// The one thing the rules cannot supply is the clock: <c>now</c> is read ONCE here, at the panel boundary, and passed
/// down, which is what keeps every rule a pure function of (tracks, now) and testable without a page.</para>
///
/// <para>The panel OWNS its entrance (the <c>ReleasePanel</c> contract, DetailTrailing.cs:229-231): one fade-up as it
/// appears, a stagger down the cards, and a FLIP when a sibling shoves it. The caller mounts it with <c>Row</c>, never
/// <c>LateRow</c> — a second entrance on top of this one double-fades the whole column.</para>
///
/// <para>Honesty rules that are load-bearing, not decoration: a card that has no evidence is NOT MOUNTED (no zeroed
/// bars, no invented percentages, no disabled button); the blend bar is drawn in THEME ink rather than cover-palette
/// colour, because this column sits on the page surface and palette belongs on imagery; and there is no play-recency
/// card at all, because the play log is a 200-entry ring that cannot answer one.</para></summary>
sealed class LikedFactsPanel : Component
{
    /// <summary>What the rail re-pushes each render. A record so an unchanged list is an equal Props and the child
    /// never re-renders; <c>DetailHandlers</c> is the shell's ONE mount-stable instance, so it compares equal too.</summary>
    internal sealed record Props(IReadOnlyList<Track> Tracks, string? ContextUri, DetailHandlers Handlers, bool OuterPadding);

    // ── The prototype's numbers (docs/plans/wavee/liked-songs-cover-mica.html, `.fact` / `.spark` / `.bar`) ──────────
    const int SparkWeeks = 12;
    /// <summary>The strips are the kit's <see cref="SparkBars"/> (38 DIP, gap 3, 3 DIP floor — the prototype's numbers
    /// are its defaults); the only local wish is the facts' immediate tooltip.</summary>
    static SparkBars.Style SparkStyle => SparkBars.DefaultStyle with { TipDelayMs = LikedLens.TipDelayMs };
    internal const int TopArtistCount = 5;
    /// <summary>How many ranked credits the artists flyout will name. The card itself still shows
    /// <see cref="TopArtistCount"/>; this is the "see more" census, not a second ranking.</summary>
    internal const int ArtistFlyoutCap = 40;
    const int BlendSlices = 5;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        var tracks = p.Tracks ?? Array.Empty<Track>();
        var culture = CultureInfo.CurrentCulture;
        // THE ONE CLOCK READ. Local (not UTC): the week the user is living through is the local one, and the since-line
        // names a local month. Every rule below takes this as a parameter and reads no clock of its own.
        //
        // FLOORED TO THE HOUR (LikedFactsRules.BucketClock): the rolling windows have to be the same twelve intervals
        // on every render of the same hour, or a bar's window — which the LENS stores as two absolute instants — would
        // stop matching the bar the moment the panel re-rendered, and the clicked bar would never read as lit.
        var now = LikedFactsRules.BucketClock(DateTimeOffset.Now);

        // The rail facts are LENSES, so the panel has to know which one is lit: this READ is the subscription that
        // repaints the selected bar / face / slice when the list's filter changes from anywhere — the chip bar, the
        // filter flyout's Clear all, the list header's clear affordance, or a click in this very panel.
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;
        bool liked = LikedSongsArtwork.IsLikedUri(p.ContextUri);

        // NAVIGATION FIRST. The detail page hands the rail a NEW track list on every hydration pass (up to 20/s while a
        // playlist opens), and the facts are decisions over the WHOLE list. So this render computes nothing: the list
        // goes into a mount-stable box, a timer restarts on every list change, and 250 ms after the LAST change — five
        // refresh cooldowns of quiet, i.e. the list is complete — the summary is produced OFF the render path and
        // published as a Loadable. Until then the region below shows a shimmer derived from the cards' own shells; on
        // Ready the real cards blur-reveal ONCE. A straggler batch republishes Ready→Ready (no shimmer), and the shape
        // latch keeps a card from folding back into a pill.
        var box = UseRef<IReadOnlyList<Track>>(tracks);
        box.Value = tracks;
        var facts = UseLoadable<LikedFactsRules.FactsSummary>();
        var latch = UseRef(new ShapeLatch(p.ContextUri));
        if (!string.Equals(latch.Value.ContextUri, p.ContextUri, StringComparison.Ordinal)) latch.Value = new ShapeLatch(p.ContextUri);
        UseTimeout(() => facts.SetReady(LikedFactsRules.Summarize(box.Value, SparkWeeks, BlendSlices, ArtistFlyoutCap)),
                   SettleMs, DepKey.FromRef(tracks));

        // The Content thunk is rebuilt on every panel render, so the lit lens states (filters, read above) stay live
        // without re-summarising; the shimmer source is the SAME card shells with placeholder rows — one UI, not two.
        var handlers = p.Handlers;
        var contextUri = p.ContextUri;
        bool outerPadding = p.OuterPadding;
        var shapes = latch.Value;
        var f = filters;
        return Skel.Region(facts,
            shimmerSource: () => Stack(SkeletonCards(liked), outerPadding),
            content: s => Stack(Cards(s, box.Value, now, culture, in f, handlers, liked, svc, contextUri, shapes), outerPadding));
    }

    /// <summary>Five refresh cooldowns (<c>DetailLiveRefresh.SettleMs</c> = 50) of quiet before the facts are decided —
    /// every hydration batch of a normal open lands well inside it.</summary>
    const float SettleMs = 250f;

    /// <summary>Per-page shape memory: a fact may UPGRADE while the page is open (Absent → Label → Graph), never
    /// downgrade — a straggler batch after the settle window cannot fold a card back into a pill.</summary>
    sealed class ShapeLatch
    {
        public readonly string? ContextUri;
        public LikedFactsRules.FactShape Week, Years, Tempo, Blend;
        public ShapeLatch(string? contextUri) => ContextUri = contextUri;
    }

    static Element Stack(Element[] cards, bool outerPadding)
    {
        if (cards.Length == 0) return new BoxEl { Key = "liked-facts-panel" };
        // Reduced motion is a VALUE (DetailTrailing.cs:214), never a branch that changes what is authored: the stagger
        // goes to zero, the cards still fade through the engine's KeepFade policy.
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;
        return new BoxEl
        {
            Key = "liked-facts-panel", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, Stagger = stagger,
            Padding = outerPadding ? new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L) : Edges4.All(0f),
            Children = cards,
        };
    }

    /// <summary>The real cards, all decided from ONE settled summary.</summary>
    static Element[] Cards(LikedFactsRules.FactsSummary s, IReadOnlyList<Track> tracks, DateTimeOffset now, CultureInfo culture,
        in TrackFilterState filters, DetailHandlers h, bool liked, Services? svc, string? contextUri, ShapeLatch latch)
    {
        var cards = new List<Element>(7);
        bool spread = s.StampsSpread;

        // (a) The time slot: the week card on Liked (stamps exist) and on playlists whose adds actually spread — but
        // only when the last twelve weeks have a SHAPE (LikedFactsRules.WeekShape): "+0 songs added" over a flat strip
        // is not a fact, it is an empty card. Else the years card, when the years have a shape (YearsShape) — a list
        // that is 41/50 one year collapses to a pill below instead of spending a card on one tall bar. Never both.
        bool stamped = liked ? s.AnyStamped : spread;
        var weeks = stamped ? LikedFactsRules.LikesPerWeek(tracks, now, SparkWeeks) : Array.Empty<LikedFactsRules.WeekBucket>();
        var weekShape = latch.Week = LikedFactsRules.Latch(latch.Week, stamped ? LikedFactsRules.WeekShape(weeks) : LikedFactsRules.FactShape.Absent);
        var yearsShape = latch.Years = LikedFactsRules.Latch(latch.Years, s.YearsShape);
        var tempoShape = latch.Tempo = LikedFactsRules.Latch(latch.Tempo, s.Tempo.Shape);
        var blendShape = latch.Blend = LikedFactsRules.Latch(latch.Blend, s.BlendShape);
        bool weekSlot = weekShape == LikedFactsRules.FactShape.Graph;
        if (weekSlot)
            cards.Add(ThisWeekCard(weeks, culture, in filters, h, liked));
        else if (yearsShape == LikedFactsRules.FactShape.Graph)
            cards.Add(YearCard(tracks, s.YearBuckets, culture, in filters, h));
        // Years the slot could not show — a pill-worthy dominance, or the week card holding the slot — fall to a pill.
        bool yearsPill = yearsShape == LikedFactsRules.FactShape.Label
            || (weekSlot && yearsShape != LikedFactsRules.FactShape.Absent);
        // A week that had a little activity but no shape becomes a since-line clause ("last add Jul 12"), not a pill.
        var lastActivity = weekShape == LikedFactsRules.FactShape.Label ? LikedFactsRules.LatestStamp(tracks) : null;

        // (a′) Tempo — its own slot. GRAPH when the four filter bands have a shape (kind-222 tempo on ≥ 60 % of the rows
        // and no band holding ≥ 70 %); LABEL falls to a pill; ABSENT says nothing.
        if (tempoShape == LikedFactsRules.FactShape.Graph && s.Tempo.Stats.Known > 0)
            cards.Add(TempoCard.Create(s.Tempo, tracks, h));

        // (b) Most liked / top artists — every credit counts, stamps or not. Own component: ArtistV4 Identity for
        // portraits is a hook, and a hook under this `if` would shift every later slot the first time credits land.
        if (s.Artists.Count > 0) cards.Add(LikedArtistsCard.Create(s.Artists, h, liked));

        // (c) Your blend — the shares self-gate on ContentFilterTags.MinTrackCount and are empty when the descriptors
        // were never fetched, so "empty ⇒ no card" is the whole of E14 (no fabricated percentages). Its OWN component:
        // the card owns an open/closed state. A blend one descriptor owns ("K-Pop 98 %") — or one that no descriptor
        // leads — is a pill, not a bar with one colour.
        if (s.BlendShares.Count > 0 && blendShape == LikedFactsRules.FactShape.Graph)
            cards.Add(LikedBlendCard.Create(tracks, s.BlendShares, h));

        // (d) Rediscover — Liked URI only. The same seven weekdays a year ago; mounted only when that window holds.
        if (liked)
        {
            var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(now);
            var lastYear = LikedFactsRules.LikedInWindow(tracks, start, end);
            if (lastYear.Count > 0) cards.Add(RediscoverCard(lastYear, svc, contextUri));
        }

        // (e) The pills — the facts that did not earn a card, still lenses (a pill names exactly the filter it applies).
        if (FactPills.Row(s, yearsPill, tempoShape == LikedFactsRules.FactShape.Label && s.Tempo.Stats.Known > 0,
                          blendShape == LikedFactsRules.FactShape.Label, culture, in filters, h) is { } pills)
            cards.Add(pills);

        // (f) The since-line. Its decade clause yields to a years pill, which says the same thing more precisely; a
        // week without a shape adds its last stamp here instead of a card.
        if (SinceLine(tracks, culture, liked, spread, suppressDecade: yearsPill, lastActivity) is { } since) cards.Add(since);

        return cards.ToArray();
    }

    // ── The shimmer source: the cards' own shells with placeholder rows (the deriver turns the fills into shimmer) ──

    static Element[] SkeletonCards(bool liked)
    {
        static Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(3f), Fill = Tok.FillControlDefault, Shrink = 0f };
        static Element Column(float h) => new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.End, Height = 38f, Children = [new BoxEl { Height = h, Corners = new CornerRadius4(2f, 2f, 1f, 1f), Fill = Tok.FillControlDefault }] };

        // Time / tempo shaped: eyebrow, a numeral block, a twelve-column strip at plausible heights.
        float[] heights = [10f, 18f, 8f, 26f, 14f, 6f, 22f, 12f, 30f, 16f, 24f, 38f];
        var columns = new Element[heights.Length];
        for (int i = 0; i < heights.Length; i++) columns[i] = Column(heights[i]);
        var time = Card("fact:week", Head(" ", " "), new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0f, Children = [Bar(56f, 30f), Bar(72f, 12f)] },
                new BoxEl { Direction = 0, Gap = 3f, Height = 38f, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = columns },
            ],
        });

        // Artists shaped: a face row and five name rows.
        var faces = new Element[6];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = new BoxEl { Width = 28f, Height = 28f, Corners = CornerRadius4.All(14f), Fill = Tok.FillControlDefault, Shrink = 0f, Margin = new Edges4(i == 0 ? 0f : -12f, 0f, 0f, 0f) };
        var names = new Element[5];
        float[] widths = [120f, 96f, 132f, 88f, 150f];
        for (int i = 0; i < names.Length; i++) names[i] = Bar(widths[i], 12f);
        var artists = Card("fact:artists", Head(" ", null), new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children = [new BoxEl { Direction = 0, Children = faces }, new BoxEl { Direction = 1, Gap = 6f, Children = names }],
        });

        // Blend shaped: the 8 px bar and a legend row.
        var blend = Card("fact:blend", Head(" ", " "), new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl { Height = 8f, Corners = CornerRadius4.All(4f), Fill = Tok.FillControlDefault },
                new BoxEl { Direction = 0, Gap = Spacing.M, Children = [Bar(64f, 12f), Bar(52f, 12f), Bar(70f, 12f)] },
            ],
        });
        // Liked and playlists share the same three silhouettes (Rediscover is too data-bound to fake honestly).
        _ = liked;
        return [time, artists, blend];
    }

    // ── This week ───────────────────────────────────────────────────────────────────────────────────────────────────

    static Element ThisWeekCard(IReadOnlyList<LikedFactsRules.WeekBucket> weeks, CultureInfo culture,
        in TrackFilterState filters, DetailHandlers h, bool liked)
    {
        int thisWeek = weeks.Count > 0 ? weeks[weeks.Count - 1].Count : 0;
        // The kit strip scales to the tallest bucket (floor 1): twelve bars against a fixed ceiling would render most
        // libraries as a flat line, and the strip's job is the SHAPE of the last twelve weeks, not their absolute rate.
        var bars = new SparkBar[weeks.Count];
        for (int i = 0; i < weeks.Count; i++)
        {
            var week = weeks[i];
            bool newest = i == weeks.Count - 1;
            bool lit = LikedFactsRules.IsWeekLens(filters, week);
            void Toggle()
            {
                var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                var (after, before) = LikedFactsRules.WeekWindowMs(week);
                h.SetFilters?.Invoke(LikedFactsRules.IsWeekLens(live, week)
                    ? live.WithAddedWindow(0L, 0L)
                    : live.WithAddedWindow(after, before));
            }
            bars[i] = new SparkBar(week.Count, WeekTip(week, culture, liked), Lit: lit, Accent: newest || lit, OnClick: Toggle);
        }

        var spark = SparkBars.Create(new SparkBarsModel(bars), SparkStyle, key: "spark");
        string delta = Strings.Detail.LikedFacts.LikedDelta(thisWeek);
        string caption = liked
            ? Strings.Detail.LikedFacts.SongsLiked(thisWeek)
            : Strings.Detail.LikedFacts.SongsAdded(thisWeek);

        var big = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f,
            Children =
            [
                ZStack(new BoxEl
                {
                    Key = "v:" + thisWeek,
                    Animate = MotionRecipes.TextSwap,
                    Children = [Title(delta) with { MaxLines = 1 }],
                }),
                Caption(caption) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };

        return Card("fact:week",
            Head(Loc.Get(Strings.Detail.LikedFacts.ThisWeek), Strings.Detail.LikedFacts.WindowCaption(weeks.Count)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [big, spark],
            });
    }

    static string WeekTip(in LikedFactsRules.WeekBucket week, CultureInfo culture, bool liked)
    {
        var (after, before) = LikedFactsRules.WeekWindowMs(week);
        var (start, end) = LikedLens.RangeParts(after, before, culture);
        return liked
            ? Strings.Detail.LikedFacts.WeekTip(start, end, week.Count)
            : Strings.Detail.LikedFacts.WeekTipAdded(start, end, week.Count);
    }

    // ── The years ───────────────────────────────────────────────────────────────────────────────────────────────────

    static Element YearCard(IReadOnlyList<Track> tracks, IReadOnlyList<LikedFactsRules.YearBucket> years,
        CultureInfo culture, in TrackFilterState filters, DetailHandlers h)
    {
        int peak = 0, peakIdx = 0;
        for (int i = 0; i < years.Count; i++)
            if (years[i].Count > peak || (years[i].Count == peak && years[i].YearMax >= years[peakIdx].YearMax))
            { peak = years[i].Count; peakIdx = i; }
        if (peak < 1) peak = 1;

        int modeYear = years.Count > 0 ? LikedFactsRules.PeakYear(tracks, years[peakIdx]) : 0;
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
            bool accent = i == peakIdx || lit;
            void Toggle()
            {
                var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                h.SetFilters?.Invoke(LikedFactsRules.IsYearLens(live, bucket)
                    ? live.WithReleaseYear(0, 0)
                    : live.WithReleaseYear(bucket.YearMin, bucket.YearMax));
            }
            bars[i] = new SparkBar(bucket.Count, YearTip(bucket, culture), Lit: lit, Accent: accent, OnClick: Toggle);
        }

            string trailing = spanLo > 0 && spanHi > 0 && spanLo != spanHi
                ? Strings.Detail.LikedFacts.YearRange(spanLo.ToString(culture), spanHi.ToString(culture))
                : (spanLo > 0 ? spanLo.ToString(culture) : "");

        var big = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f,
            Children =
            [
                ZStack(new BoxEl
                {
                    Key = "v:" + modeYear,
                    Animate = MotionRecipes.TextSwap,
                    Children = [Title(modeYear.ToString(culture)) with { MaxLines = 1 }],
                }),
                Caption(Loc.Get(Strings.Detail.LikedFacts.MostTracks)) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };

        return Card("fact:years",
            Head(Loc.Get(Strings.Detail.LikedFacts.TheYears), trailing),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [big, SparkBars.Create(new SparkBarsModel(bars), SparkStyle, key: "spark")],
            });
    }

    static string YearTip(in LikedFactsRules.YearBucket bucket, CultureInfo culture)
    {
        if (bucket.YearMin == bucket.YearMax)
            return Strings.Detail.LikedFacts.YearTipOne(bucket.YearMin.ToString(culture), bucket.Count);
        return Strings.Detail.LikedFacts.YearTip(
            bucket.YearMin.ToString(culture), bucket.YearMax.ToString(culture), bucket.Count);
    }

    // ── Rediscover ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element RediscoverCard(IReadOnlyList<Track> window, Services? svc, string? contextUri)
    {
        var body = new List<Element>(2)
        {
            Caption(Strings.Detail.LikedFacts.LastYear(window.Count)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
            },
        };

        // The button exists only when there is a real player and a real context to play into. PlayOrderedAsync IS the
        // subset seam (PlaybackController.cs:737): it carries the embedded page, so both the local host and a remote
        // device receive THESE tracks in THIS order rather than the whole collection. No player ⇒ no button; the fact
        // still reads on its own, and a dead control would be worse than none.
        if (svc?.Player is { } player && contextUri is { Length: > 0 } uri)
        {
            void PlayThem()
            {
                // Built on the click, not on every render: the window can be hundreds of tracks and this is a cold path.
                var ordered = new PlaybackContextTrack[window.Count];
                for (int i = 0; i < window.Count; i++)
                    ordered[i] = new PlaybackContextTrack(window[i].Uri, window[i].ContextUid ?? string.Empty);
                _ = player.PlayOrderedAsync(uri, ordered, 0);
            }

            body.Add(Button.Create(Loc.Get(Strings.Detail.LikedFacts.PlayThem), PlayThem,
                ButtonAppearance.Standard, ControlSize.Small) with { Shrink = 0f });
        }

        return Card("fact:lastyear",
            Head(Loc.Get(Strings.Detail.LikedFacts.Rediscover), null),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = body.ToArray(),
            });
    }

    // ── The since-line ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Present-only clauses. Liked = save since / save decade / oldest like. Playlist with spread =
    /// collecting since / release decade / oldest add. Editorial = release decade / oldest track with year. Never
    /// "collecting since yesterday" off a bulk stamp.</summary>
    static Element? SinceLine(IReadOnlyList<Track> tracks, CultureInfo culture, bool liked, bool spread, bool suppressDecade,
                              DateTimeOffset? lastActivity)
    {
        var clauses = new List<string>(3);
        if (liked)
        {
            if (LikedFactsRules.LikingSince(tracks) is { } since)
                clauses.Add(Strings.Detail.LikedFacts.Since(since.ToLocalTime().ToString("MMMM yyyy", culture)));
            if (!suppressDecade && LikedFactsRules.DominantDecade(tracks) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestLike(tracks) is { Title.Length: > 0 } oldest)
                clauses.Add(Strings.Detail.LikedFacts.Oldest(oldest.Title));
        }
        else if (spread)
        {
            if (LikedFactsRules.LikingSince(tracks) is { } since)
                clauses.Add(Strings.Detail.LikedFacts.CollectingSince(since.ToLocalTime().ToString("MMMM yyyy", culture)));
            if (!suppressDecade && LikedFactsRules.DominantReleaseDecade(tracks) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestLike(tracks) is { Title.Length: > 0 } oldest)
                clauses.Add(Strings.Detail.LikedFacts.OldestAdd(oldest.Title));
        }
        else
        {
            if (!suppressDecade && LikedFactsRules.DominantReleaseDecade(tracks) is { } decade)
                clauses.Add(Strings.Detail.LikedFacts.Decade(decade.ToString(culture)));
            if (LikedFactsRules.OldestRelease(tracks) is { Title.Length: > 0 } oldest)
                clauses.Add(oldest.Year > 0
                    ? Strings.Detail.LikedFacts.OldestTrackYear(oldest.Title, oldest.Year)
                    : Strings.Detail.LikedFacts.OldestTrack(oldest.Title));
        }
        // The week card's LABEL form: a little activity, no shape — "last add Jul 12" says what the strip would have.
        if (lastActivity is { } last)
        {
            string date = last.ToLocalTime().ToString("MMM d", culture);
            clauses.Add(liked ? Strings.Detail.LikedFacts.LastLike(date) : Strings.Detail.LikedFacts.LastAdd(date));
        }
        if (clauses.Count == 0) return null;

        return new BoxEl
        {
            Key = "caption:since", Direction = 1, MinWidth = 0f,
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Padding = new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XXS, 0f),
            Children =
            [
                Caption(string.Join(Sep, clauses)) with
                {
                    Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    // ── Shared card shell ───────────────────────────────────────────────────────────────────────────────────────────

    const string Sep = " · ";

    /// <summary>The prototype's `.fact`: card fill, one-pixel card stroke, card radius, card elevation. Keyed and
    /// self-entering — a card that arrives late (the descriptors landing, the first like of the week) fades up and
    /// FLIPs its siblings down rather than snapping the column.</summary>
    internal static Element Card(string key, Element head, Element body) => new BoxEl
    {
        Key = key, Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
        Direction = 1, Gap = 6f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, 10f, Spacing.M, 11f),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
        Children = [head, body],
    };

    /// <summary>A card header: the eyebrow, and an optional right-hand fact. The eyebrow GROWS so the fact is pushed to
    /// the trailing edge (the prototype's `margin-left:auto`) without a spacer node.</summary>
    internal static Element Head(string title, string? trailing)
    {
        var label = WaveeType.Eyebrow(title) with
        {
            Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        var kids = trailing is { Length: > 0 } fact
            ? new Element[] { label, Caption(fact) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 } }
            : new Element[] { label };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = kids,
        };
    }
}


/// <summary>Most liked / top artists. Own component for the same reason <see cref="LikedBlendCard"/> is: ArtistV4
/// Identity (portraits) and the overflow flyout are hooks, and a hook under the panel's <c>if (ranked.Count > 0)</c>
/// would shift every later slot the first time credits land. The face and the name are the SAME affordance: they lens
/// the list to that artist; a second click clears it. They do not navigate — a statistic about this collection should
/// answer itself here.</summary>
sealed class LikedArtistsCard : Component
{
    internal sealed record Props(IReadOnlyList<LikedFactsRules.ArtistCount> Ranked, DetailHandlers Handlers, bool Liked);

    internal static Element Create(IReadOnlyList<LikedFactsRules.ArtistCount> ranked, DetailHandlers h, bool liked)
        => Embed.Comp(new Props(ranked, h, liked), static () => new LikedArtistsCard()) with { Key = "fact:artists" };

    public override Element Render()
    {
        var p = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var moreOpen = UseSignal(false);
        var measuredW = UseMeasuredWidth(FacePiles.Step);
        var ranked = p.Ranked ?? Array.Empty<LikedFactsRules.ArtistCount>();
        var culture = CultureInfo.CurrentCulture;
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;
        var store = svc?.RealStore;

        string faceKey = UriKey(ranked);
        var portraits = UseResource(async ct =>
        {
            var seed = Resolve(ranked, store);
            if (svc is null) return 0;
            var uris = UrisNeedingPortrait(seed);
            if (uris.Count == 0) return 0;
            // A name-only artist stub already satisfies Identity, so a default Ensure no-ops and the pile stays on
            // initials. Revalidate is the designed "ignore the seal" path — ArtistV4 is where PortraitGroup lives.
            await svc.Hydrator.EnsureManyAsync(uris, HydrationLevel.Identity,
                    new HydrationOptions(Revalidate: true), ct)
                .ConfigureAwait(false);
            return 1;
        }, 0, faceKey);
        _ = portraits.Loadable.Value.Value;

        var resolved = Resolve(ranked, store);
        int nameCount = Math.Min(LikedFactsPanel.TopArtistCount, ranked.Count);
        float inner = measuredW.Value > 0f
            ? MathF.Max(0f, measuredW.Value - Spacing.M * 2f)
            : 0f;
        // First frame is unmeasured (width 0); keep the named five so the pile doesn't flash a single face.
        int faceCount = inner >= FacePiles.Outer
            ? FacePiles.VisibleFaces(inner, ranked.Count)
            : nameCount;
        int pileExtra = Math.Max(0, ranked.Count - faceCount);
        int nameExtra = Math.Max(0, ranked.Count - nameCount);

        void Toggle(ArtistRef who)
        {
            var live = p.Handlers.Filters?.Peek() ?? TrackFilterState.Default;
            p.Handlers.SetFilters?.Invoke(LikedFactsRules.IsArtistLens(live, who)
                ? live.WithArtist(null)
                : live.WithArtist(LikedFactsRules.ArtistKey(who), who.Name));
        }

        void ToggleMore()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            moreOpen.Value = true;
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => Flyout(ranked, Resolve(ranked, svc?.RealStore), culture,
                    p.Handlers.Filters?.Peek() ?? TrackFilterState.Default, p.Handlers,
                    () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => { handle.Value = null; moreOpen.Value = false; };
        }

        void MoreKey(KeyEventArgs e)
        {
            if (e.KeyCode is not (Keys.Down or Keys.F4)) return;
            ToggleMore();
            e.Handled = true;
        }

        var faces = new FacePiles.Face[faceCount];
        var rows = new List<Element>(nameCount + (nameExtra > 0 ? 1 : 0));
        int painted = Math.Max(faceCount, nameCount);
        for (int i = 0; i < painted; i++)
        {
            var artist = ranked[i].Artist;
            string name = resolved[i].Name.Length > 0 ? resolved[i].Name : artist.Name;
            bool lensable = LikedFactsRules.ArtistKey(artist).Length > 0;
            bool lit = LikedFactsRules.IsArtistLens(filters, artist);
            Action? click = lensable ? () => Toggle(artist) : null;
            string tip = p.Liked
                ? Strings.Detail.LikedFacts.ArtistTip(name, ranked[i].Count)
                : Strings.Detail.LikedFacts.ArtistTipAdded(name, ranked[i].Count);
            if (i < faceCount) faces[i] = new FacePiles.Face(name, resolved[i].Image?.Url, click, lit, tip);
            if (i < nameCount) rows.Add(NameRow(name, ranked[i].Count.ToString(culture), lit, click, tip, "who:" + i));
        }
        if (nameExtra > 0) rows.Add(MoreRow(nameExtra, () => moreOpen.Value, ToggleMore, MoreKey));

        return LikedFactsPanel.Card("fact:artists",
            LikedFactsPanel.Head(Loc.Get(p.Liked ? Strings.Detail.LikedFacts.MostLiked : Strings.Detail.LikedFacts.TopArtists), null),
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Shrink = 0f, OnRealized = h => anchor.Value = h,
                        Children =
                        [
                            FacePiles.Strip(faces, faceCount, pileExtra,
                                pileExtra > 0 ? ToggleMore : null,
                                pileExtra > 0 ? Strings.Detail.LikedFacts.MoreArtists(pileExtra) : null),
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
                        Children = rows.ToArray(),
                    },
                ],
            });
    }

    static Element NameRow(string name, string count, bool lit, Action? onClick, string tip, string key)
    {
        bool live = onClick is not null;
        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live, Cursor = live ? CursorId.Hand : CursorId.Arrow,
            FocusVisualMargin = live ? new Edges4(1f, 1f, 1f, 1f) : default,
            Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = !live ? ColorF.Transparent : lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverScale = live ? WaveeMotion.ScaleSubtle.Hover : 1f,
            PressScale = live ? WaveeMotion.ScaleSubtle.Press : 1f,
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
        return ToolTip.Wrap(row, tip, showDelayMs: LikedLens.TipDelayMs, grow: 1f) with { Key = key };
    }

    static Element MoreRow(int extra, Func<bool> open, Action toggle, Action<KeyEventArgs> key)
        => new BoxEl
        {
            Key = "who:more", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = toggle, OnKeyDown = key,
            Children =
            [
                SidebarChevron.Disclosure(open),
                Caption(Strings.Detail.LikedFacts.MoreArtists(extra)) with
                {
                    Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

    static Element Flyout(IReadOnlyList<LikedFactsRules.ArtistCount> ranked, IReadOnlyList<Artist> resolved,
        CultureInfo culture, in TrackFilterState filters, DetailHandlers h, Action close)
    {
        var rows = new Element[ranked.Count];
        for (int i = 0; i < ranked.Count; i++)
        {
            var artist = ranked[i].Artist;
            var a = resolved[i];
            string name = a.Name.Length > 0 ? a.Name : artist.Name;
            bool lensable = LikedFactsRules.ArtistKey(artist).Length > 0;
            bool lit = LikedFactsRules.IsArtistLens(filters, artist);
            int count = ranked[i].Count;
            Action? click = lensable
                ? () =>
                {
                    var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                    h.SetFilters?.Invoke(LikedFactsRules.IsArtistLens(live, artist)
                        ? live.WithArtist(null)
                        : live.WithArtist(LikedFactsRules.ArtistKey(artist), artist.Name));
                    close();
                }
                : null;
            BoxEl row = new BoxEl
            {
                Key = "all:" + i, Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Corners = CornerRadius4.All(6f),
                Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
                Role = lensable ? AutomationRole.MenuItem : AutomationRole.None,
                Focusable = lensable, Cursor = lensable ? CursorId.Hand : (CursorId?)null,
                OnClick = click,
                Children =
                [
                    PersonPicture.Create("", 32f, displayName: name, imageSourcePath: a.Image?.Url),
                    Caption(name) with
                    {
                        Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                        Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    Caption(count.ToString(culture)) with { Color = Tok.TextTertiary, Shrink = 0f },
                ],
            };
            rows[i] = lensable ? row.Interactive(Interaction.Subtle) : row;
        }

        var list = new BoxEl { Direction = 1, Gap = Spacing.XXS, Width = 264f, Children = rows };
        return new BoxEl
        {
            Direction = 1, Width = 280f, MaxHeight = 360f,
            Padding = Edges4.All(Spacing.S), Gap = Spacing.S,
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.Detail.LikedFacts.AllArtists)) with { Color = Tok.TextTertiary },
                ScrollView(list) with { Width = 264f, MaxHeight = 320f, ContentSized = true, AutoEdgeFade = true, Grow = 0f },
            ],
        };
    }

    static IReadOnlyList<Artist> Resolve(IReadOnlyList<LikedFactsRules.ArtistCount> ranked, IStore? store)
    {
        var result = new Artist[ranked.Count];
        for (int i = 0; i < ranked.Count; i++)
        {
            var ar = ranked[i].Artist;
            var fromStore = ar.Uri.Length > 0 ? store?.GetArtist(ar.Uri) : null;
            string name = ar.Name.Length > 0 ? ar.Name : fromStore?.Name ?? "";
            result[i] = fromStore is not null
                ? fromStore with { Name = name.Length > 0 ? name : fromStore.Name }
                : new Artist(ar.Id, ar.Uri, name, null);
        }
        return result;
    }

    static List<string> UrisNeedingPortrait(IReadOnlyList<Artist> billed)
    {
        var uris = new List<string>(billed.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < billed.Count; i++)
        {
            string uri = billed[i].Uri;
            if (billed[i].Image is null && uri.Length > 0 && seen.Add(uri)) uris.Add(uri);
        }
        return uris;
    }

    static string UriKey(IReadOnlyList<LikedFactsRules.ArtistCount> ranked)
    {
        if (ranked.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ranked.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            string uri = ranked[i].Artist.Uri;
            sb.Append(uri.Length > 0 ? uri : ranked[i].Artist.Name);
        }
        return sb.ToString();
    }
}


/// <summary>"Your blend" — the primary-descriptor partition as one stacked bar, and what the bar's remainder is
/// actually made of.
///
/// <para>THE PROBLEM THIS SHAPE SOLVES (docs/plans/wavee/liked-blend-card-mica.html, card B′). The partition is
/// correct — every tagged like counted once, by its primary descriptor, so the slices stack to one bar — but on a real
/// library the five named slices cover barely half of it and the rest used to be ONE flat grey slab with a tooltip.
/// Half the card said "other" and did nothing. So the remainder is now drawn as WHAT IT IS: one thin proportional TICK
/// per descriptor, and every tick is a real tag — it names itself on hover and lenses the list on click. No dead
/// pixels, and the texture itself says "many small things" where the slab said "one big unknown".</para>
///
/// <para>AND IT OPENS. The legend ends in a disclosure button ("44 more · 47 %") that cracks the card open IN PLACE:
/// the body reveals below with the same tail redrawn at FULL WIDTH — so a descriptor worth 3 % of the library is a
/// segment you can actually hit instead of six pixels — plus a legend of the ones above 1 % and an honest count of the
/// ones below. The reveal is the WinUI Expander's motion, borrowed structurally rather than by reference
/// (<c>Expander.cs:242-256</c>): an always-mounted clip host whose declared height toggles 0 ↔ auto under a
/// <see cref="SizeMode.Reflow"/> transition, so the cards BELOW this one reflow down through real layout rather than
/// being overlapped by a revealing panel.</para>
///
/// <para>WHY A COMPONENT. <c>open</c> is a hook, and the panel mounts this card under an <c>if (shares.Count > 0)</c>
/// gate — a hook declared there would shift every later hook slot the first time the descriptors land. Its own
/// component also scopes the re-render: a lens click repaints the bar, not the whole bento.</para></summary>
sealed class LikedBlendCard : Component
{
    /// <summary>Re-pushed by the panel every render. <c>Shares</c> is rebuilt each time so the props never compare
    /// equal — which is what we want here: the card must recompute when a like/unlike changes the partition, and the
    /// panel itself only re-renders when ITS props or the filter signal changed.</summary>
    internal sealed record Props(IReadOnlyList<Track> Tracks, IReadOnlyList<LikedFactsRules.TagShare> Shares,
                                 DetailHandlers Handlers);

    internal static Element Create(IReadOnlyList<Track> tracks, IReadOnlyList<LikedFactsRules.TagShare> shares,
                                   DetailHandlers h)
        => Embed.Comp(new Props(tracks, shares, h), static () => new LikedBlendCard()) with { Key = "fact:blend" };

    // ── The prototype's numbers (`.bar` / `.seg` / `.tk` / `.legend`) ────────────────────────────────────────────────
    const float BlendBarHeight = 8f;
    const float BlendBarRadius = 4f;
    const float BlendSegGap = 2f;
    const float LegendDot = 8f;
    /// <summary>Gap and floor for the tail ticks. A tick narrower than this is not a pointer target at all, so the floor
    /// is a hit-target minimum rather than a style: below it the strip would be a texture you cannot touch. When the
    /// floors stop fitting (a very long tail in a 240-DIP rail) the sub-floor ticks paint at the floor and eat their
    /// gap — see <see cref="TailTicks"/> — and the strip clips its own overflow so the five named slices keep their
    /// share whatever the tail's length.</summary>
    const float TickGap = 1f;
    const float TickMinWidth = 2f;
    /// <summary>A descriptor under this share of the tagged likes gets a COUNT in the tail legend instead of a row —
    /// see <c>LikedFactsRules.TailSplit</c>, which owns the rule.</summary>
    const float TailLegendFloor = 0.01f;

    /// <summary>The blend bar's ink: ONE accent stepped down in alpha, not five hues. Five distinct colours here would
    /// read as five categories with meanings; five weights of the same accent read as what this actually is — a ranked
    /// share of one quantity.</summary>
    static readonly float[] SliceAlpha = [1f, 0.8f, 0.6f, 0.45f, 0.3f];

    /// <summary>The tail's ink: THEME text ink at three low alphas, cycled by index (the prototype's .16/.22/.30). Not
    /// accent — the tail is deliberately not one of the named slices — and not literal white, because this card sits on
    /// the page surface in both themes and <c>Tok.TextPrimary</c> is the ink that flips with it. The cycle is what makes
    /// forty adjacent ticks read as forty things rather than one dithered block.</summary>
    static readonly float[] TickAlpha = [0.16f, 0.22f, 0.30f];
    /// <summary>The OPENED tail bar's ink: the same theme ink, alternating, at the weights a full-width bar can carry
    /// (the prototype's .55/.38). Brighter than the ticks because at full width these are segments, not texture.</summary>
    static readonly float[] TailSegAlpha = [0.55f, 0.38f];

    /// <summary>The body host's motion — the WinUI Expander open/close, applied to the host's LAYOUT height.
    /// <see cref="SizeMode.Reflow"/> (not Reveal) so the facts cards BELOW this one glide down through real layout;
    /// <see cref="SizeAnchor.Leading"/> so the body wipes downward from the legend instead of sliding up from under it.
    /// Expand 333 ms / collapse 167 ms are the Disclosure tokens, which also carry the reduced-motion policy.</summary>
    static readonly LayoutTransition BodyReveal = new(
        TransitionChannels.Size,
        MotionTok.DisclosureExpand.ToDynamics(),
        Size: SizeMode.Reflow,
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(),
        Anchor: SizeAnchor.Leading);

    /// <summary>The opened tail bar's entrance: it wipes out from its left edge (scaleX .55 → 1) as it fades in, the
    /// prototype's `.exp .bar{transform-origin:left;transform:scaleX(.55)}`. The dynamics live IN the LayoutTransition
    /// rather than in <c>Element.Transition</c> because a node that declares <c>Layout</c> has its <c>Transition</c>
    /// IGNORED (Reconciler.SynthesizeDeclarative) — the RecentsPage `DrawerReveal` shape.</summary>
    static readonly LayoutTransition TailBarReveal = new(
        TransitionChannels.Opacity,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Sx: 0.55f, Opacity: 0f, Active: true));

    /// <summary>One tail-legend row's entrance. Delayed per row by <c>WaveeEntrance.DelayMs</c> — the app's ONE stagger
    /// rung, which is capped at <c>WaveeEntrance.StaggerCap</c> and returns 0 under reduced motion. (The engine's
    /// parent-side <c>Element.Stagger</c> spelling is index × ms with NO ceiling, so a fifteen-row tail authored that
    /// way would still be arriving 600 ms after the card opened.)</summary>
    static readonly LayoutTransition TailRowReveal = new(
        TransitionChannels.Opacity,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: Spacing.XS, Opacity: 0f, Active: true));

    public override Element Render()
    {
        var p = UseProps<Props>();
        var culture = CultureInfo.CurrentCulture;
        // The lens READ, done here rather than inherited as a prop: it is the subscription that re-inks the lit slice /
        // tick / legend row when the filter changes from anywhere (the chip bar, the flyout's Clear all, the list
        // header's per-facet clear, or a click in this very card).
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;

        var open = UseSignal(false);
        // The body's MOUNT lags `open` on collapse: the clip has to shrink OVER real content, exactly as WinUI keyframes
        // Visibility=Collapsed at t=167ms (Expander.xaml:81-83). The watcher below flips this off at settle.
        var shown = UseSignal(false);
        var hostRef = UseRef<NodeHandle>(default);

        bool isOpen = open.Value;              // subscribe
        bool showBody = shown.Value;           // subscribe: the watcher's write re-renders us
        bool closing = showBody && !isOpen;    // mid collapse-reflow: body mounted, host shrinking, watcher armed

        void Toggle()
        {
            bool next = !open.Peek();
            open.Value = next;
            if (next) shown.Value = true;      // mounting FIRST is what lets the reflow seed from a real content height
        }

        // The blend drives the CHIPS' facet, not one of its own: "Pop" in the bar and "Pop" in the chip bar are the
        // same question, so clicking the slice lights the chip and clicking the chip lights the slice. Exclusive, and a
        // second click clears — the chip bar's grammar, inherited rather than re-invented.
        void ToggleTag(string title)
        {
            // The LIVE state, not this render's snapshot: the filter can have moved under us (the list header's clear,
            // the flyout's Clear all) between the render that drew this tick and the click.
            var live = p.Handlers.Filters?.Peek() ?? TrackFilterState.Default;
            p.Handlers.SetFilters?.Invoke(live with { Tag = LikedFactsRules.IsTagLens(live, title) ? null : title });
        }

        var shares = p.Shares;
        float listed = 0f;
        for (int i = 0; i < shares.Count; i++) listed += shares[i].Fraction;
        float other = MathF.Max(0f, 1f - listed);

        // The FULL tail: same partition, same denominator, every remaining descriptor named (detail unbounded ⇒
        // MoreTags == 0), so the ticks account for the whole remainder with nothing pooled behind them.
        var tail = LikedFactsRules.BlendOther(p.Tracks, shares.Count, int.MaxValue);
        // The tail is drawn only when it is actually there: at five slices covering everything, a phantom sliver would
        // be a lie the bar tells at one pixel wide.
        bool hasTail = other > 0.005f && tail.Named.Count > 0;

        var segments = new List<Element>(shares.Count + 1);
        var legend = new List<Element>(shares.Count + 1);
        for (int i = 0; i < shares.Count; i++)
        {
            string title = shares[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            // A lensed slice goes to FULL accent whatever its rank: the bar's alpha ladder ranks shares, and the one the
            // list is actually showing has to read as chosen rather than as merely first.
            var ink = lit ? Tok.AccentDefault : Tok.AccentDefault with { A = SliceAlpha[Math.Min(i, SliceAlpha.Length - 1)] };
            // ONE string per slice, shown by BOTH the segment and its legend row: a bar this thin is hard to hit and a
            // legend row is not, so either affordance has to answer the same question, and answering it twice from two
            // formatters is how the two drift apart.
            string tip = Strings.Detail.LikedFacts.ShareTip(title, shares[i].Count,
                                                            shares[i].Fraction.ToString("P0", culture));
            void Click() => ToggleTag(title);
            segments.Add(Segment("seg:" + title, shares[i].Fraction, ink, Tok.AccentTextPrimary, tip, Click));
            legend.Add(LegendEntry("leg:" + title, ink, title, shares[i].Fraction.ToString("P0", culture), tip, lit, Click));
        }
        if (hasTail)
        {
            segments.Add(TailTicks(tail.Named, other, culture, in filters, ToggleTag));
            legend.Add(MoreButton(tail, other, isOpen, culture, Toggle, () => open.Value));
        }

        var bar = new BoxEl
        {
            Direction = 0, Height = BlendBarHeight, Gap = BlendSegGap, MinWidth = 0f,
            Corners = CornerRadius4.All(BlendBarRadius), ClipToBounds = true, HitTestPassThrough = true,
            Children = segments.ToArray(),
        };

        // The body host — ALWAYS MOUNTED, the transition's node. The declared Height toggle 0 ↔ NaN(auto) IS the whole
        // trigger: the commit snap-solves the new target, the host's FLIP projection diffs old vs new size, and the
        // Reflow track eases the LAYOUT height while the cards below reflow each tick.
        var host = new BoxEl
        {
            Key = "blend-body-host",
            Direction = 1, ClipToBounds = true, MinWidth = 0f,
            Height = isOpen ? float.NaN : 0f,
            Animate = BodyReveal,
            OnRealized = h => hostRef.Value = h,
            Children = showBody && hasTail
                ? [Body(tail, culture, in filters, ToggleTag)]
                : [],
        };

        // The COLLAPSED card — bar over legend, on the column's own rhythm.
        Element collapsed = new BoxEl
        {
            Key = "blend-top", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                bar,
                new BoxEl { Key = "blend-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                            Children = legend.ToArray() },
            ],
        };
        // …and the body host hangs off an UNGAPPED outer column. A flex gap is charged per child BOUNDARY, not per
        // painted pixel (FlexLayout: gap × (n-1)), so a zero-height host inside the gapped column would leave 8 DIP of
        // dead air under the legend while the card is shut — and the collapse watcher below, which is a real child for
        // the ~167 ms it exists, would add 8 more at exactly the moment the fold has to read as smooth. The open
        // state's breathing room lives INSIDE the body instead (its top padding), which is also what makes that space
        // part of the revealed height rather than a step that appears before the reveal starts.
        var stack = new List<Element>(3) { collapsed, host };
        // The watcher is mounted ONLY while the collapse reflow runs (the ~167 ms the body stays mounted under a
        // shrinking clip); the rest of the time this card has no per-frame subscriber at all.
        if (closing) stack.Add(Embed.Comp(() => new BlendCollapseWatcher { Host = () => hostRef.Value, Shown = shown })
                               with { Key = "blend-collapse-watch" });

        return LikedFactsPanel.Card("fact:blend",
            // The header count is the TAGGED population, not the library: the bar partitions the likes that carry a
            // descriptor, and labelling it with the whole collection would silently claim coverage it does not have.
            LikedFactsPanel.Head(Loc.Get(Strings.Detail.LikedFacts.Blend),
                                 Strings.Detail.SongCount(LikedFactsRules.TaggedTotal(shares))),
            new BoxEl { Direction = 1, MinWidth = 0f, Children = stack.ToArray() });
    }

    // ── The collapsed bar ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One stacked slice, wrapped in its own tooltip. The FILL lives on a non-hit-testable child rather than on
    /// the tooltip's trigger box because the trigger is the kit's node, not ours to paint — and because that is the same
    /// ancestor-hover-drives-descendant shape the sparkline's bars use, so the two data surfaces on this card respond
    /// identically.</summary>
    static Element Segment(string key, float fraction, ColorF ink, ColorF hoverInk, string tip, Action onClick)
    {
        Element fill = new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = ink, HoverFill = hoverInk,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            HitTestVisible = false,
        };
        // The hit box is a SEPARATE node from the painted slice for the same reason the sparkline's column is: the slice
        // is 8 DIP tall and its own paint must not also be the thing that answers the pointer (the fill is a cross-fade
        // target, and a click plate on it would fight that).
        Element target = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = onClick,
            Children = [fill],
        };
        // grow: the slice's share — the tooltip wrapper is the bar's real flex child, so it is the node that has to
        // carry the proportion. Its target is grown to match (ToolTip.Fill), which is what keeps the painted slice
        // exactly as wide as the share it is drawn from.
        return ToolTip.Wrap(target, tip, grow: fraction, showDelayMs: LikedLens.TipDelayMs) with { Key = key };
    }

    /// <summary>The tail region of the collapsed bar: ONE tick per remaining descriptor, each proportional to its own
    /// count, each a real tag (instant bubble, lens click, hand cursor, hover lift to the bright accent).
    ///
    /// <para>The strip takes the bar's whole remainder (<paramref name="other"/>) and divides it by count, so the ticks
    /// together are exactly as wide as the slab they replaced and each one is exactly as wide as it deserves — down to
    /// the 2-DIP hit floor. HONESTLY: on a long tail in a 240-DIP rail the rarest ticks' shares fall UNDER that floor,
    /// so they are painted at the floor and crowd into their neighbours' 1-DIP gap; the last stretch of the strip reads
    /// as a dense band rather than as separable ticks. That is the prototype's behaviour too, and it is the right
    /// trade: the strip clips its OWN overflow rather than the bar's, so a fifty-descriptor tail never squeezes the five
    /// named slices out of shape, and a descriptor worth one song is exactly what the "more" button exists to make
    /// reachable — at full width, where it is a segment instead of a floor.</para></summary>
    static Element TailTicks(IReadOnlyList<LikedFactsRules.TagShare> tail, float other, CultureInfo culture,
                             in TrackFilterState filters, Action<string> toggleTag)
    {
        var ticks = new Element[tail.Count];
        for (int i = 0; i < tail.Count; i++)
        {
            string title = tail[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TickAlpha[i % TickAlpha.Length] };
            string tip = Strings.Detail.LikedFacts.ShareTip(title, tail[i].Count,
                                                            tail[i].Fraction.ToString("P0", culture));
            void Click() => toggleTag(title);

            Element fill = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = ink, HoverFill = Tok.AccentTextPrimary,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                HitTestVisible = false,
            };
            Element target = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = TickMinWidth,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = Click,
                Children = [fill],
            };
            ticks[i] = ToolTip.Wrap(target, tip, grow: tail[i].Count, showDelayMs: LikedLens.TipDelayMs)
                       with { Key = "tick:" + title };
        }

        return new BoxEl
        {
            Key = "seg:tail", Direction = 0, Gap = TickGap,
            Grow = other, Basis = 0f, MinWidth = 0f,
            ClipToBounds = true, HitTestPassThrough = true,
            Children = ticks,
        };
    }

    /// <summary>The legend's last entry: the disclosure. Chevron (ChevronRight → 90° on the Disclosure token) + how many
    /// descriptors the tail holds + what share of the bar they are.
    ///
    /// <para>The LABEL is what flips when the card opens ("44 more" → "Show less"), not a hidden automation string: the
    /// engine has no separate accessible-name channel, so a name that flipped invisibly would be a name that flipped for
    /// nobody. The share stays put on the right through both states, which is also what keeps the legend from reflowing
    /// as the card opens.</para></summary>
    static Element MoreButton(in LikedFactsRules.BlendTail tail, float other, bool isOpen, CultureInfo culture,
                              Action toggle, Func<bool> open)
    {
        int count = tail.Named.Count + tail.MoreTags;
        string label = isOpen ? Loc.Get(Strings.Detail.LikedFacts.ShowLess)
                              : Strings.Detail.LikedFacts.MoreTags(count);
        return new BoxEl
        {
            Key = "leg:more", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = toggle,
            Children =
            [
                // The chevron is its own component (it owns an AnimEngine rotation track and therefore hooks); `open` is
                // a Func so the delegate's signal read happens inside ITS render — props freeze at mount.
                SidebarChevron.Disclosure(open),
                Caption(label) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Caption(other.ToString("P0", culture)) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
    }

    /// <summary>A legend row — the same fact as its segment, at a size a pointer can actually land on. It carries the
    /// tooltip too (and its own subtle hover plate), because a 2-DIP slice of a 5-slice bar is not a realistic target and
    /// the legend is where a mouse naturally goes.</summary>
    static Element LegendEntry(string key, ColorF ink, string label, string share, string tip, bool lit, Action onClick)
    {
        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            // The 4/2 pad is the hover plate's inset AND the row's hit padding, so the rhythm between entries
            // (container gap 4 + 4 + 4) is what it was before the plate existed.
            Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = onClick,
            Children =
            [
                new BoxEl { Width = LegendDot, Height = LegendDot, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = ink },
                Caption(label) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Caption(share) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
        return ToolTip.Wrap(row, tip, showDelayMs: LikedLens.TipDelayMs) with { Key = key };
    }

    // ── The opened body ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the card reveals: a hairline, an eyebrow naming the tail, the tail redrawn at full width, its
    /// legend down to <see cref="TailLegendFloor"/>, and a count of everything under it.
    ///
    /// <para>Every percentage on the full-width bar is "of the tail" and SAYS SO — the bar's own 100 % is the tail, so
    /// quoting library shares on it would put "3 %" on a segment occupying a tenth of the width. The legend rows below
    /// go back to library shares (matching the top five), which is why the two carry different tooltip wordings rather
    /// than one shared one.</para></summary>
    static Element Body(in LikedFactsRules.BlendTail tail, CultureInfo culture, in TrackFilterState filters,
                        Action<string> toggleTag)
    {
        var named = tail.Named;
        var segs = new Element[named.Count];
        for (int i = 0; i < named.Count; i++)
        {
            string title = named[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TailSegAlpha[i % TailSegAlpha.Length] };
            // Share OF THE TAIL: this bar's whole width is the tail, so its segments have to be quoted against it.
            string tip = Strings.Detail.LikedFacts.TailShareTip(title, named[i].Count,
                (named[i].Count / (float)Math.Max(1, tail.Count)).ToString("P0", culture));
            void Click() => toggleTag(title);
            segs[i] = Segment("tseg:" + title, named[i].Count, ink, Tok.AccentTextPrimary, tip, Click);
        }

        var tailBar = new BoxEl
        {
            Key = "tail-bar", Direction = 0, Height = BlendBarHeight, Gap = BlendSegGap, MinWidth = 0f,
            Corners = CornerRadius4.All(BlendBarRadius), ClipToBounds = true, HitTestPassThrough = true,
            TransformOriginX = 0f,                       // …so the wipe grows FROM the left edge, not from the middle
            Enter = TailBarReveal.Enter, Layout = TailBarReveal,
            Children = segs,
        };

        var (rows, underFloor) = LikedFactsRules.TailSplit(named, TailLegendFloor);
        var legendRows = new List<Element>(rows.Count + 1);
        for (int i = 0; i < rows.Count; i++)
        {
            string title = rows[i].Title;
            bool lit = LikedFactsRules.IsTagLens(filters, title);
            var ink = lit ? Tok.AccentDefault : Tok.TextPrimary with { A = TailSegAlpha[i % TailSegAlpha.Length] };
            string share = rows[i].Fraction.ToString("P0", culture);
            string tip = Strings.Detail.LikedFacts.ShareTip(title, rows[i].Count, share);
            void Click() => toggleTag(title);
            // The Enter terminal lives on a BoxEl wrapper because the legend row itself is a ToolTip component element,
            // and the declarative enter/exit fields bake onto NODES.
            legendRows.Add(new BoxEl
            {
                Key = "tlw:" + title, Direction = 0, Shrink = 0f,
                Animate = TailRowReveal with { DelayMs = WaveeEntrance.DelayMs(i) },
                Children = [LegendEntry("tl:" + title, ink, title, share, tip, lit, Click)],
            });
        }
        if (underFloor > 0)
            legendRows.Add(new BoxEl
            {
                Key = "tl:underfloor", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Padding = new Edges4(4f, 2f, 4f, 2f),
                Animate = TailRowReveal with { DelayMs = WaveeEntrance.DelayMs(rows.Count) },
                Children =
                [
                    Caption(Strings.Detail.LikedFacts.UnderFloor(underFloor)) with
                    { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            });

        return new BoxEl
        {
            Key = "blend-body", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            // The gap that separates the body from the legend above it — INSIDE the body, so it is revealed by the same
            // clip that reveals the hairline (the prototype's `.fact.open .exp{margin-top:8px}`, which is likewise a
            // property of the OPEN state). Hung outside as a flex gap it would be permanent dead air while shut.
            Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                // The hairline is the prototype's `border-top` on the eyebrow — a real 1-DIP rule rather than a border,
                // so the gap above and below it stays the column's own rhythm.
                new BoxEl { Key = "tail-rule", Height = 1f, Fill = Tok.StrokeCardDefault, HitTestVisible = false },
                LikedFactsPanel.Head(Strings.Detail.LikedFacts.TailHeader(tail.Named.Count + tail.MoreTags),
                                     Strings.Detail.LikedFacts.TailSongs(tail.Count)),
                tailBar,
                new BoxEl
                {
                    Key = "tail-legend", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                    Children = legendRows.ToArray(),
                },
            ],
        };
    }
}

/// <summary>Per-frame poller, mounted ONLY while the body's collapse reflow runs: the moment the host's
/// <see cref="SizeMode.Reflow"/> track settles (the AnimEngine reclaims it), flip the mount signal off. The host is
/// already at its declared 0 height by then, so the unmount itself moves nothing.
///
/// <para>REPLICATED, not reused: the kit's own <c>ExpanderCollapseWatcher</c> is <c>internal</c> to
/// <c>FluentGpu.Controls</c> (InternalsVisibleTo names only FluentGpu.VerticalSlice and FluentGpu.Windows.Tests), so it
/// is not reachable from the app. Twenty lines of poller is a smaller cost than widening a kit assembly's internals for
/// one app surface — and the shape is the kit's, so the two stay honest about being the same idiom.</para></summary>
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
            // Settled (the reflow track completed and was reclaimed) — or the node vanished: unmount now.
            if (anim is null || scene is null || node.IsNull || !scene.IsLive(node) || !anim.HasTracks(node))
                Shown.Value = false;
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}

/// <summary>The rail facts, seen from the TRACK LIST: what a lens is called, and the header row that says which one is
/// on and lets you take it off again.
///
/// <para>It lives beside the panel that SETS the lenses so the two halves of one feature stay in one file — the panel
/// writes <c>TrackFilterState</c>, this reads it back — and so there is exactly one place the week wording is built.
/// <see cref="RangeParts"/> is called by the sparkline bar's tooltip and by <see cref="Header"/>: a bubble that says
/// "Jul 27 – Aug 3" and a header that says something else would be two bugs wearing one feature.</para>
///
/// <para>The arithmetic behind all of it is <c>LikedFactsRules</c> (pure, tested); this file owns only the localized
/// wording and the elements.</para></summary>
internal static class LikedLens
{
    /// <summary>The facts' tooltips open IMMEDIATELY rather than after <c>ToolTip.MouseShowDelayMs</c> (800ms).
    ///
    /// <para>That delay is right for a toolbar button, whose glyph already says what it does and whose tooltip is a
    /// reminder. It is wrong here: a sparkline column is ~10 DIP wide and carries no label at all, so the bubble IS the
    /// label — and a pointer sweeping the strip leaves each column long before 800ms, which cancels the pending open
    /// (ToolTip.OnLeave) and starts the next one from zero. Twelve bars, twelve restarts, no bubble ever: the strip
    /// read as inert, which is exactly what was reported.</para>
    ///
    /// <para>0, not a small number: the countdown is the frame-driven <c>ToolTipClock</c> (seed on one frame, poll on
    /// the next), so 0 already means "the frame after the pointer arrives" rather than "inside the event". The
    /// per-element override is WinUI's own shape (<c>ToolTipService.InitialShowDelay</c>) and touches only these call
    /// sites — every other tooltip in the app keeps the service delay.</para></summary>
    internal const float TipDelayMs = 0f;

    /// <summary>The lens header's fixed vertical extent (pill height + its bottom gap). A CONSTANT because the vertical
    /// hero layout clips its rows beneath the sticky chrome by a computed inset — a header whose height depended on how
    /// many pills wrapped would desync that cut. Hence the row does not wrap and its pills ellipsise instead.</summary>
    internal const float HeaderExtent = PillHeight + Spacing.S;

    const float PillHeight = 28f;

    /// <summary>The two ends of a saved-date window, formatted in the culture's own month/day pattern. An absent
    /// endpoint prints as an ellipsis rather than as 1970: a half-open window is unreachable from the UI (the rail
    /// always sets both ends), but a filter state is a value anyone can construct and a lie is worse than a gap.</summary>
    internal static (string Start, string End) RangeParts(long afterMs, long beforeMs, CultureInfo culture)
        => (Stamp(afterMs, culture), Stamp(beforeMs, culture));

    static string Stamp(long unixMs, CultureInfo culture)
        => unixMs == 0L ? "\u2026"
         : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MMM d", culture);

    /// <summary>"Liked Jul 27 – Aug 3" — the week lens, named the same way its bar's tooltip names it.</summary>
    internal static string WeekLabel(long afterMs, long beforeMs, CultureInfo culture)
    {
        var (start, end) = RangeParts(afterMs, beforeMs, culture);
        return Strings.Detail.LikedFacts.LensWeek(Strings.Detail.LikedFacts.WeekRange(start, end));
    }

    /// <summary>"Added Jul 27 – Aug 3" — the playlist week lens, named the same way its bar's tooltip names it.</summary>
    internal static string AddedLabel(long afterMs, long beforeMs, CultureInfo culture)
    {
        var (start, end) = RangeParts(afterMs, beforeMs, culture);
        return Strings.Detail.LikedFacts.LensAdded(Strings.Detail.LikedFacts.WeekRange(start, end));
    }

    internal static string YearLabel(int min, int max, CultureInfo culture)
    {
        if (min != 0 && min == max) return Strings.Detail.LikedFacts.LensYear(min.ToString(culture));
        string a = min == 0 ? "\u2026" : min.ToString(culture);
        string b = max == 0 ? "\u2026" : max.ToString(culture);
        return Strings.Detail.LikedFacts.LensYears(a, b);
    }

    /// <summary>The group header the track list shows while a rail fact is lensing it: one pill per active lens, each
    /// with its own clear, and the count of what survived.
    ///
    /// <para>Null when no lens is on — the row is absent, not empty, so an unfiltered list keeps every pixel it had.</para>
    ///
    /// <para>One pill PER lens rather than one sentence, because the lenses are independent facets that genuinely
    /// combine ("this week" ∧ "vaultboy" is a real question). A single "clear" would then throw away a facet the user
    /// did not ask to lose; a per-pill clear retires exactly one.</para>
    ///
    /// <para>The Tag pill appears for a chip click too, and that is deliberate: the blend slice and the chip write the
    /// SAME facet, so a header that appeared for one and not the other would be describing the click rather than the
    /// state.</para></summary>
    internal static Element? Header(in TrackFilterState filter, int visibleCount, DetailHandlers h, CultureInfo culture,
        bool liked = true)
    {
        var lenses = LikedFactsRules.ActiveLenses(filter);
        if (lenses == LikedFactsRules.LikedLens.None) return null;

        var kids = new List<Element>(5);
        if ((lenses & LikedFactsRules.LikedLens.Week) != 0)
            kids.Add(Pill("lens:week",
                liked ? WeekLabel(filter.AddedAfterMs, filter.AddedBeforeMs, culture)
                      : AddedLabel(filter.AddedAfterMs, filter.AddedBeforeMs, culture),
                LikedFactsRules.LikedLens.Week, h));
        if ((lenses & LikedFactsRules.LikedLens.Artist) != 0)
            // The display name when we have it, the id when we do not — a lens must always be able to say what it is.
            kids.Add(Pill("lens:artist", filter.ArtistName is { Length: > 0 } n ? n : filter.ArtistId ?? "",
                          LikedFactsRules.LikedLens.Artist, h));
        if ((lenses & LikedFactsRules.LikedLens.Tag) != 0)
            kids.Add(Pill("lens:tag", filter.Tag ?? "", LikedFactsRules.LikedLens.Tag, h));
        if ((lenses & LikedFactsRules.LikedLens.Year) != 0)
            kids.Add(Pill("lens:year", YearLabel(filter.ReleaseYearMin, filter.ReleaseYearMax, culture),
                          LikedFactsRules.LikedLens.Year, h));
        if ((lenses & LikedFactsRules.LikedLens.Tempo) != 0)
            // Named by the same table the tempo card's bands and the tempo pill use, so the header cannot disagree.
            kids.Add(Pill("lens:tempo", TempoBandText.Range(filter.Tempo), LikedFactsRules.LikedLens.Tempo, h));

        // The count is the VISIBLE row count, so it answers "and how much is that?" for whatever combination of lenses
        // is on — including zero, which is a real and useful answer ("that week, nothing by this artist").
        kids.Add(Caption(Strings.Detail.SongCount(visibleCount)) with
        {
            Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
        });

        return new BoxEl
        {
            Key = "lens-header", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Height = PillHeight, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, 0f, Spacing.S),
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One lens, named, with its own clear. The label ellipsises rather than wrapping (see
    /// <see cref="HeaderExtent"/>), and the clear is a separate focusable button so keyboard users can drop one facet
    /// without touching the others.</summary>
    static Element Pill(string key, string label, LikedFactsRules.LikedLens lens, DetailHandlers h)
    {
        void Clear()
        {
            var live = h.Filters?.Peek() ?? TrackFilterState.Default;
            h.SetFilters?.Invoke(LikedFactsRules.ClearLens(live, lens));
        }

        Element close = new BoxEl
        {
            Key = "clear", Width = 22f, Height = 22f, Shrink = 0f, Corners = CornerRadius4.All(11f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = Clear,
            Children = [Icon(Icons.ChromeClose, 10f, Tok.TextSecondary)],
        };

        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            Shrink = 1f, MinWidth = 0f, Height = PillHeight,
            Padding = new Edges4(Spacing.M, 0f, 3f, 0f), Corners = CornerRadius4.All(999f),
            Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentSecondary,
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Children =
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary,
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                ToolTip.Wrap(close, Loc.Get(Strings.Detail.LikedFacts.LensClear), showDelayMs: TipDelayMs),
            ],
        };
    }
}

/// <summary>The ONE wording table for the four tempo bands — the card's band tooltips, the tempo pill and the list
/// header all read it, so a pill can never name a band the rows it lenses do not match. Boundaries come from
/// <see cref="TrackFilterModel.BandOf"/>; these are only their words and their domain intervals on the plot.</summary>
internal static class TempoBandText
{
    internal static string Range(TrackTempoBand band) => Loc.Get(band switch
    {
        TrackTempoBand.Under90 => Strings.Detail.LikedFacts.BandUnder90,
        TrackTempoBand.From90To119 => Strings.Detail.LikedFacts.Band90,
        TrackTempoBand.From120To139 => Strings.Detail.LikedFacts.Band120,
        _ => Strings.Detail.LikedFacts.Band140,
    });

    internal static string Name(TrackTempoBand band) => Loc.Get(band switch
    {
        TrackTempoBand.Under90 => Strings.Detail.LikedFacts.BandNameUnder90,
        TrackTempoBand.From90To119 => Strings.Detail.LikedFacts.BandName90,
        TrackTempoBand.From120To139 => Strings.Detail.LikedFacts.BandName120,
        _ => Strings.Detail.LikedFacts.BandName140,
    });

    internal static string Tip(TrackTempoBand band, int count)
        => Strings.Detail.LikedFacts.TempoBandTip(Range(band), Name(band), count);

    /// <summary>The band's interval on the 60–200 bpm plot (the open ends clamp to the plot's edges).</summary>
    internal static (float Lo, float Hi) Domain(TrackTempoBand band) => band switch
    {
        TrackTempoBand.Under90 => (TempoCard.DomainMin, 90f),
        TrackTempoBand.From90To119 => (90f, 120f),
        TrackTempoBand.From120To139 => (120f, 140f),
        _ => (140f, TempoCard.DomainMax),
    };
}

/// <summary>"Tempo" — the kind-222 BPM distribution of the list as the kit's <see cref="DensityPlot"/>: a KDE ridge
/// over 60–200 bpm, a rug of the actual tempos coloured by their Camelot key (the row's swatch convention,
/// <c>WaveePalette.DataDotInk</c>), the median as the numeral and a marker, and the FOUR FILTER BANDS as the plot's
/// lenses — a band click writes <c>TrackFilterState.Tempo</c>, the facet the filter flyout already owns, so the list,
/// the flyout and the header agree without a new filter. Mounted only when <c>LikedFactsRules.TempoShape</c> is
/// GRAPH (see the pills for LABEL).
///
/// <para>Its own component so the lit band follows the filter signal without re-rendering the whole bento, and so the
/// plot's value arrays stay reference-stable across a lit change (the plot's geometry memo keys on them).</para></summary>
sealed class TempoCard : Component
{
    /// <summary>The list's tempo summary comes in PRE-COMPUTED (one pass per list, in the panel); the tracks are here
    /// only to fill the plot's value array when the tempos actually change.</summary>
    internal sealed record Props(LikedFactsRules.TempoSummary Tempo, IReadOnlyList<Track> Tracks, DetailHandlers Handlers);

    internal static Element Create(in LikedFactsRules.TempoSummary tempo, IReadOnlyList<Track> tracks, DetailHandlers h)
        => Embed.Comp(new Props(tempo, tracks, h), static () => new TempoCard()) with { Key = "fact:tempo" };

    internal const float DomainMin = 60f, DomainMax = 200f;
    /// <summary>Fixed so the plot's width — and therefore its measured geometry — does not move when the numeral
    /// changes from "98" to "128".</summary>
    const float NumeralColumnWidth = 72f;
    // Three captions, not six: at 140 DIP of plot every second label was touching its neighbour.
    static readonly PlotTick[] Ticks = [new(80f, "80"), new(120f, "120"), new(160f, "160")];
    static readonly TrackTempoBand[] Bands = [TrackTempoBand.Under90, TrackTempoBand.From90To119, TrackTempoBand.From120To139, TrackTempoBand.From140AndUp];

    /// <summary>The rail's calm variant of the kit plot: ridge + line, the median marker behind the line, captions —
    /// no rug (the row's BPM · Key column already carries each track's tempo and key colour; on a 38 DIP ridge the
    /// dots read as noise) and no hairlines (the band you hover washes itself). Four things, one hue.</summary>
    static DensityPlot.Style PlotStyle => DensityPlot.DefaultStyle with
    {
        TipDelayMs = LikedLens.TipDelayMs, LineWidth = 1.25f, AxisFontSize = 10f,
        Marker = Tok.AccentTextPrimary with { A = 0.45f },
    };

    public override Element Render()
    {
        var p = UseProps<Props>();
        var tracks = p.Tracks;
        var t = p.Tempo;
        var stats = t.Stats;
        var culture = CultureInfo.CurrentCulture;
        // Subscribe: the lit band is the list's filter, wherever it was set from (the flyout, the header's ×, a pill).
        var filters = p.Handlers.Filters?.Value ?? TrackFilterState.Default;

        // The plot's value array, memoised on the tempo CONTENT (the fingerprint), never on the list instance: the
        // detail page rebuilds its list on every hydration pass (descriptors, play counts, identity…), and the plot's
        // own geometry memo is keyed on this array — a new array per pass would re-mint and re-tessellate the ridge up
        // to twenty times a second while a playlist opens. Same tempos ⇒ same array ⇒ same paths.
        var bpm = UseMemo(() =>
        {
            var b = new float[stats.Known];
            Span<uint> unused = stats.Known <= 512 ? stackalloc uint[stats.Known] : new uint[stats.Known];
            LikedFactsRules.TempoValues(tracks, b, unused);
            return b;
        }, DepKey.From(t.Fingerprint.Hash, (long)t.Fingerprint.Known));

        var bands = new PlotBand[Bands.Length];
        for (int i = 0; i < Bands.Length; i++)
        {
            var band = Bands[i];
            var (lo, hi) = TempoBandText.Domain(band);
            bool lit = LikedFactsRules.IsTempoLens(filters, band);
            void Toggle()
            {
                var live = p.Handlers.Filters?.Peek() ?? TrackFilterState.Default;
                p.Handlers.SetFilters?.Invoke(live with { Tempo = LikedFactsRules.IsTempoLens(live, band) ? TrackTempoBand.Any : band });
            }
            bands[i] = new PlotBand(lo, hi, TempoBandText.Tip(band, t.Count(i)), lit, Toggle);
        }

        var model = new DensityPlotModel(bpm, DomainMin, DomainMax)
        {
            Bands = bands, Ticks = Ticks, Marker = (float)stats.Median, RugDotMax = 0,
        };

        string median = Math.Round(stats.Median).ToString(culture);
        string trailing = stats.Known < stats.Total
            ? Strings.Detail.LikedFacts.TempoCoverage(stats.Known, stats.Total)
            : Strings.Detail.LikedFacts.TempoRange(Math.Round(stats.Min).ToString(culture), Math.Round(stats.Max).ToString(culture));

        var big = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f, Width = NumeralColumnWidth,
            Children =
            [
                ZStack(new BoxEl
                {
                    Key = "v:" + median,
                    Animate = MotionRecipes.TextSwap,
                    Children = [Title(median) with { MaxLines = 1 }],
                }),
                Caption(Loc.Get(Strings.Detail.LikedFacts.BpmMedian)) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };

        return LikedFactsPanel.Card("fact:tempo",
            LikedFactsPanel.Head(Loc.Get(Strings.Detail.LikedFacts.Tempo), trailing),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [big, DensityPlot.Create(model, PlotStyle, key: "tempo-plot")],
            });
    }
}

/// <summary>The pill row — the distribution facts that did NOT earn a card (<c>LikedFactsRules.FactShape.Label</c>),
/// each a one-line label that is still a LENS: "mostly 2024 · 41 of 50" filters to 2024 exactly as the years bar would
/// have, "mostly 120 – 139 bpm" writes the same tempo band the card's band would, "K-Pop 98 %" is the blend slice's
/// tag lens. A lit pill takes the accent-subtle wash a lit bar takes; an idle pill sits on the card surface with a fact
/// glyph so it never reads as an active filter chip. A flat blend ("12 styles, none over 15 %") has no one tag to
/// open and is the one plain (non-button) pill. Wording: a pill names the BAND/RANGE it filters — the median tempo is
/// in its tooltip, and there is no "steady"/"varied" prose (half/double-time reporting would make it a lie).</summary>
internal static class FactPills
{
    // 24-unit stroke glyphs (interned once — the string is the identity).
    static readonly PathData CalendarGlyph = PathDataParser.Parse("M4 5h16v15H4zM4 10h16M8 3v4M16 3v4", PathContentEpoch.Mint(), FillRule.NonZero);
    static readonly PathData MetronomeGlyph = PathDataParser.Parse("M9 4h6l3 16H6zM12 15l5-9", PathContentEpoch.Mint(), FillRule.NonZero);
    static readonly PathData TagGlyph = PathDataParser.Parse("M4 4h7l9 9-7 7-9-9zM8.5 8.5h.01", PathContentEpoch.Mint(), FillRule.NonZero);

    internal static Element? Row(LikedFactsRules.FactsSummary s, bool yearsPill, bool tempoPill, bool blendPill,
        CultureInfo culture, in TrackFilterState filters, DetailHandlers h)
    {
        var pills = new List<Element>(3);
        string mostly = Loc.Get(Strings.Detail.LikedFacts.PillMostly);

        if (yearsPill && s.YearBuckets.Count > 0)
        {
            var d = s.YearsDominance;
            if (d.Known > 0 && d.TopIndex >= 0)
            {
                var b = s.YearBuckets[d.TopIndex];
                string range = b.YearMin == b.YearMax
                    ? b.YearMin.ToString(culture)
                    : Strings.Detail.LikedFacts.YearRange(b.YearMin.ToString(culture), b.YearMax.ToString(culture));
                bool lit = LikedFactsRules.IsYearLens(filters, b);
                void Toggle()
                {
                    var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                    h.SetFilters?.Invoke(LikedFactsRules.IsYearLens(live, b)
                        ? live.WithReleaseYear(0, 0)
                        : live.WithReleaseYear(b.YearMin, b.YearMax));
                }
                pills.Add(Pill("pill:years", CalendarGlyph, mostly, range, Strings.Detail.LikedFacts.PillCount(b.Count, d.Known),
                               lit, Toggle, Strings.Detail.LikedFacts.PillYearsTip(range, b.Count, d.Known)));
            }
        }

        if (tempoPill)
        {
            var d = s.Tempo.Dominance;
            if (d.Known > 0 && d.TopIndex >= 0)
            {
                var band = (TrackTempoBand)(d.TopIndex + 1);
                var stats = s.Tempo.Stats;
                int count = s.Tempo.Count(d.TopIndex);
                bool lit = LikedFactsRules.IsTempoLens(filters, band);
                void Toggle()
                {
                    var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                    h.SetFilters?.Invoke(live with { Tempo = LikedFactsRules.IsTempoLens(live, band) ? TrackTempoBand.Any : band });
                }
                pills.Add(Pill("pill:tempo", MetronomeGlyph, mostly, TempoBandText.Range(band),
                               Strings.Detail.LikedFacts.PillCount(count, d.Known), lit, Toggle,
                               Strings.Detail.LikedFacts.PillTempoTip(Math.Round(stats.Median).ToString(culture), stats.Known, stats.Total)));
            }
        }

        if (blendPill)
        {
            var d = s.BlendDominance;
            if (d.AboveFloor > 0 && d.TopTitle is { } title)
            {
                string share = d.TopShare.ToString("P0", culture);
                if (d.Flat)
                {
                    string flatShare = LikedFactsRules.BlendFlat.ToString("P0", culture);
                    pills.Add(Pill("pill:blend", TagGlyph, "", Strings.Detail.LikedFacts.PillBlendFlat(d.Styles, flatShare), null,
                                   lit: false, toggle: null, Strings.Detail.LikedFacts.PillBlendFlat(d.Styles, flatShare)));
                }
                else
                {
                    bool lit = LikedFactsRules.IsTagLens(filters, title);
                    void Toggle()
                    {
                        var live = h.Filters?.Peek() ?? TrackFilterState.Default;
                        h.SetFilters?.Invoke(live with { Tag = LikedFactsRules.IsTagLens(live, title) ? null : title });
                    }
                    pills.Add(Pill("pill:blend", TagGlyph, "", title, share, lit, Toggle,
                                   Strings.Detail.LikedFacts.ShareTip(title, d.TopCount, share)));
                }
            }
        }

        if (pills.Count == 0) return null;
        return new BoxEl
        {
            Key = "fact:pills", Direction = 0, Wrap = true, Gap = 6f, MinWidth = 0f,
            Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Children = pills.ToArray(),
        };
    }

    static Element Pill(string key, PathData glyph, string lead, string strong, string? trailing, bool lit, Action? toggle, string tip)
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
        kids.Add(Caption(strong) with { Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
        if (trailing is { Length: > 0 }) kids.Add(Caption(trailing) with { Color = Tok.TextTertiary, MaxLines = 1 });

        var pill = new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 26f, MinWidth = 0f,
            Padding = new Edges4(8f, 0f, 10f, 0f), Corners = CornerRadius4.All(13f),
            Fill = lit ? Tok.AccentSubtle : Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = lit ? Tok.AccentSecondary : Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
            Cursor = live ? CursorId.Hand : null, FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = live ? (lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary) : (lit ? Tok.AccentSubtle : Tok.FillCardDefault),
            PressedFill = live ? Tok.FillSubtleTertiary : (lit ? Tok.AccentSubtle : Tok.FillCardDefault),
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = toggle,
            Children = kids.ToArray(),
        };
        return ToolTip.Wrap(pill, tip, showDelayMs: LikedLens.TipDelayMs) with { Key = key };
    }
}
