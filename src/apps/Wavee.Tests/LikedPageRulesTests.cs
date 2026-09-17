// ── Wavee.Tests/LikedPageRulesTests.cs — the two decisions 0.2.9 kept inside a component body ─────────────────────────
//
// `LikedFactsRules.Plan` (0.2.9 LikedFactsPanel.Cards: which cards and pills mount from one settled summary, and the
// per-page shape latch) and `LikedFactsRules.ChipSet` (0.2.9 DetailTracks.ContentFilterBar: curated-authoritative once
// known and non-empty, else the descriptor-derived fallback). Extracted so neither decision needs a mounted rail to test.

using System.Text;
using FluentGpu.Foundation;
using Xunit;
using Shape = Wavee.LikedFactsRules.FactShape;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LikedPageRulesTests
{
    public LikedPageRulesTests() => TestScope.Fresh();

    static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A settled summary with exactly the shapes a fact is being asked about.</summary>
    static LikedFactsRules.FactsSummary Summary(
        Shape years = Shape.Absent, Shape tempo = Shape.Absent, int tempoKnown = 0, Shape blend = Shape.Absent,
        int blendSlices = 0, int artists = 0, bool anyStamped = false, bool spread = false)
    {
        var shares = new LikedFactsRules.TagShare[blendSlices];
        for (int i = 0; i < shares.Length; i++) shares[i] = new LikedFactsRules.TagShare("tag" + i, 4, 0.1f);
        var counts = new LikedFactsRules.ArtistCount[artists];
        return new LikedFactsRules.FactsSummary
        {
            YearBuckets = Array.Empty<LikedFactsRules.YearBucket>(),
            YearsDominance = default,
            YearsShape = years,
            Tempo = new LikedFactsRules.TempoSummary(new LikedFactsRules.TempoStats(tempoKnown, tempoKnown, 120, 90, 150),
                                                     0, 0, tempoKnown, 0, default, tempo, default),
            BlendShares = shares,
            BlendDominance = default,
            BlendShape = blend,
            Artists = counts,
            AnyStamped = anyStamped,
            StampsSpread = spread,
        };
    }

    /// <summary>Twelve weeks, oldest first, with <paramref name="counts"/> in the newest buckets.</summary>
    static LikedFactsRules.WeekBucket[] Weeks(params int[] counts)
    {
        var weeks = new LikedFactsRules.WeekBucket[12];
        for (int i = 0; i < weeks.Length; i++)
        {
            int from = weeks.Length - counts.Length;
            weeks[i] = new LikedFactsRules.WeekBucket(Now.AddDays(-7 * (weeks.Length - i)), i >= from ? counts[i - from] : 0);
        }
        return weeks;
    }

    static readonly LikedFactsRules.WeekBucket[] Busy = Weeks(2, 0, 3);    // 5 adds over 2 buckets → Graph
    static readonly LikedFactsRules.WeekBucket[] Lonely = Weeks(1);        // 1 add → Label

    // ── the one time slot ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_shaped_week_takes_the_time_slot_and_the_years_fall_to_a_pill()
    {
        var plan = LikedFactsRules.Plan(Summary(years: Shape.Graph, anyStamped: true), Busy, liked: true, new LikedFactsRules.ShapeLatch());

        Assert.True(plan.WeekCard);
        Assert.False(plan.YearsCard);
        Assert.True(plan.YearsPill);
        Assert.False(plan.LastActivityClause);
    }

    [Fact]
    public void Without_a_week_the_years_card_takes_the_slot()
    {
        var plan = LikedFactsRules.Plan(Summary(years: Shape.Graph), Array.Empty<LikedFactsRules.WeekBucket>(), liked: true,
                                        new LikedFactsRules.ShapeLatch());

        Assert.False(plan.WeekCard);
        Assert.True(plan.YearsCard);
        Assert.False(plan.YearsPill);
    }

    [Fact]
    public void A_labelled_year_is_a_pill_and_an_absent_one_is_nothing()
    {
        var label = LikedFactsRules.Plan(Summary(years: Shape.Label), Array.Empty<LikedFactsRules.WeekBucket>(), true, new());
        Assert.False(label.YearsCard);
        Assert.True(label.YearsPill);

        var absent = LikedFactsRules.Plan(Summary(anyStamped: true), Busy, true, new());
        Assert.True(absent.WeekCard);
        Assert.False(absent.YearsPill);
    }

    [Fact]
    public void A_week_with_a_little_activity_becomes_the_since_line_clause()
    {
        var plan = LikedFactsRules.Plan(Summary(anyStamped: true), Lonely, true, new());
        Assert.False(plan.WeekCard);
        Assert.True(plan.LastActivityClause);
        Assert.Equal(Shape.Label, plan.Week);
    }

    /// <summary>Liked stamps are always activity; a playlist's single republish instant is not, so the time slot there
    /// needs the stamps to SPREAD.</summary>
    [Fact]
    public void Liked_needs_a_stamp_and_a_playlist_needs_stamps_that_spread()
    {
        var republished = Summary(anyStamped: true, spread: false);
        Assert.True(LikedFactsRules.Plan(republished, Busy, liked: true, new()).WeekCard);

        var playlist = LikedFactsRules.Plan(republished, Busy, liked: false, new());
        Assert.False(playlist.WeekCard);
        Assert.False(playlist.Stamped);
        Assert.Equal(Shape.Absent, playlist.Week);

        Assert.True(LikedFactsRules.Plan(Summary(anyStamped: true, spread: true), Busy, liked: false, new()).WeekCard);
        Assert.False(LikedFactsRules.Plan(Summary(), Busy, liked: true, new()).WeekCard);
    }

    // ── tempo, artists, blend ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tempo_mounts_only_with_known_tempos()
    {
        Assert.True(LikedFactsRules.Plan(Summary(tempo: Shape.Graph, tempoKnown: 30), Lonely, true, new()).TempoCard);
        Assert.True(LikedFactsRules.Plan(Summary(tempo: Shape.Label, tempoKnown: 30), Lonely, true, new()).TempoPill);

        var unknown = LikedFactsRules.Plan(Summary(tempo: Shape.Graph, tempoKnown: 0), Lonely, true, new());
        Assert.False(unknown.TempoCard);
        Assert.False(unknown.TempoPill);
    }

    [Fact]
    public void Artists_mount_on_any_credit_and_the_blend_needs_slices_to_be_a_bar()
    {
        Assert.True(LikedFactsRules.Plan(Summary(artists: 1), Lonely, true, new()).ArtistsCard);
        Assert.False(LikedFactsRules.Plan(Summary(), Lonely, true, new()).ArtistsCard);

        Assert.True(LikedFactsRules.Plan(Summary(blend: Shape.Graph, blendSlices: 3), Lonely, true, new()).BlendCard);
        Assert.False(LikedFactsRules.Plan(Summary(blend: Shape.Graph, blendSlices: 0), Lonely, true, new()).BlendCard);

        var label = LikedFactsRules.Plan(Summary(blend: Shape.Label, blendSlices: 3), Lonely, true, new());
        Assert.False(label.BlendCard);
        Assert.True(label.BlendPill);
    }

    // ── the latch ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A straggler landing while the page is open can UPGRADE a pill into a card, never fold a card back.</summary>
    [Fact]
    public void The_latch_upgrades_and_never_folds_back()
    {
        var latch = new LikedFactsRules.ShapeLatch();

        var first = LikedFactsRules.Plan(Summary(years: Shape.Label, tempo: Shape.Label, tempoKnown: 10, blend: Shape.Label, blendSlices: 2), Lonely, true, latch);
        Assert.True(first.YearsPill);
        Assert.True(first.TempoPill);
        Assert.True(first.BlendPill);

        var upgraded = LikedFactsRules.Plan(Summary(years: Shape.Graph, tempo: Shape.Graph, tempoKnown: 30, blend: Shape.Graph, blendSlices: 3), Lonely, true, latch);
        Assert.True(upgraded.YearsCard);
        Assert.True(upgraded.TempoCard);
        Assert.True(upgraded.BlendCard);
        Assert.Equal(Shape.Graph, latch.Years);
        Assert.Equal(Shape.Graph, latch.Tempo);
        Assert.Equal(Shape.Graph, latch.Blend);

        var straggler = LikedFactsRules.Plan(Summary(years: Shape.Label, tempo: Shape.Absent, tempoKnown: 30, blend: Shape.Label, blendSlices: 3), Lonely, true, latch);
        Assert.True(straggler.YearsCard);
        Assert.True(straggler.TempoCard);
        Assert.True(straggler.BlendCard);
        Assert.False(straggler.BlendPill);

        // A fresh latch (a new context) starts from nothing.
        Assert.True(LikedFactsRules.Plan(Summary(years: Shape.Label), Lonely, true, new()).YearsPill);
    }

    [Fact]
    public void The_week_latches_too()
    {
        var latch = new LikedFactsRules.ShapeLatch();
        Assert.True(LikedFactsRules.Plan(Summary(anyStamped: true), Busy, true, latch).WeekCard);
        Assert.True(LikedFactsRules.Plan(Summary(anyStamped: true), Lonely, true, latch).WeekCard);
        Assert.Equal(Shape.Graph, latch.Week);
    }

    // ── the chip set ────────────────────────────────────────────────────────────────────────────────────────────────

    int _serial;

    Track Tagged(params string[] tags)
    {
        string uri = "spotify:track:chip-" + (++_serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(uri);
        row.Known = (uint)(TrackFields.Identity | TrackFields.Tags);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        var track = Entities.Track(EntityUri.Parse(uri));
        var ids = new StringId[tags.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = Entities.Intern(Encoding.UTF8.GetBytes(tags[i]));
        Entities.Current.Edges.TrackTags.Replace(track.Slot, new int[tags.Length], ids, EdgeState.Complete, tags.Length);
        return track;
    }

    Track[] Library()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < 4; i++) tracks.Add(Tagged("Pop"));
        for (int i = 0; i < 3; i++) tracks.Add(Tagged("Rock", "Chill"));
        tracks.Add(Tagged("Indie"));                                        // one carrier: evidence, but below the derive floor
        return tracks.ToArray();
    }

    static readonly ContentFilterChip[] Curated =
    [
        new("Jazz", "jazz"), new("Pop", "pop"), new("Metal", "metal"), new("Chill", "chill"), new("Indie", "indie"),
    ];

    [Fact]
    public void A_known_curated_set_is_authoritative_and_ordered_evidenced_first()
    {
        var set = LikedFactsRules.ChipSet(true, Curated, Library());

        Assert.Equal(new[] { "Pop", "Chill", "Indie", "Jazz", "Metal" }, set.Titles.ToArray());
        Assert.Equal(3, set.EvidencedCount);
        Assert.True(set.IsEvidenced(2));
        Assert.False(set.IsEvidenced(3));
    }

    [Fact]
    public void A_known_empty_curated_set_hands_the_bar_to_the_derived_fallback()
    {
        var set = LikedFactsRules.ChipSet(true, Array.Empty<ContentFilterChip>(), Library());

        Assert.Equal(new[] { "Pop", "Chill", "Rock" }, set.Titles.ToArray());
        Assert.Equal(set.Count, set.EvidencedCount);                       // every derived chip is evidenced by construction
    }

    [Fact]
    public void An_unknown_curated_set_is_not_trusted_even_when_it_carries_chips()
    {
        var set = LikedFactsRules.ChipSet(false, Curated, Library());

        Assert.DoesNotContain("Jazz", set.Titles);
        Assert.Equal(set.Count, set.EvidencedCount);
    }

    [Fact]
    public void No_curated_set_and_no_evidence_is_no_bar()
    {
        Assert.Equal(ContentFilterChipSet.Empty, LikedFactsRules.ChipSet(false, Array.Empty<ContentFilterChip>(), [Tagged("Pop")]));
        Assert.Equal(ContentFilterChipSet.Empty, LikedFactsRules.ChipSet(true, Array.Empty<ContentFilterChip>(), ReadOnlySpan<Track>.Empty));
        Assert.Equal(0, LikedFactsRules.ChipSet(false, Curated, ReadOnlySpan<Track>.Empty).Count);
    }
}
