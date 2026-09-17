// ── Wavee.Tests/ArtistPopularTracksTests.cs — the chart's ORDER contract, and the commit that keeps it ───────────────
//
// Ported from 0.2.9 `Wavee.Tests/ArtistPopularTracksTests.cs` (ArtistPopularMergeTests, 7 facts) over SLOTS: the merge
// is now `Entities.CommitPopular`'s rewrite of `Edges.ArtistPopular`, and the rule it must keep is the same — the seed
// keeps its order at the head, extension-only tracks append, duplicates collapse with the seed winning, cap 50. The
// commit facts pin the reason the rule moved into the commit: the overview (seed) and the extended list (extension) can
// share one batch or land in either order, and `CommitEdges` rides after `CommitArtists`.
// `WithPlayCounts`' "returns the same instance" became "reports no change": a span has no identity to compare.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ArtistPopularTracksTests
{
    [Fact]
    public void Merge_keeps_the_seed_head()
    {
        Span<int> into = stackalloc int[ArtistPopularTracks.ExtendedCap];
        int n = ArtistPopularTracks.Merge([1, 2], [2, 1, 3], into);
        Assert.Equal(new[] { 1, 2, 3 }, into[..n].ToArray());
    }

    [Fact]
    public void Merge_appends_extension_only_tracks_in_extension_order()
    {
        Span<int> into = stackalloc int[ArtistPopularTracks.ExtendedCap];
        int n = ArtistPopularTracks.Merge([1], [26, 25], into);
        Assert.Equal(new[] { 1, 26, 25 }, into[..n].ToArray());
    }

    [Fact]
    public void Merge_with_an_empty_extension_copies_the_seed_untouched()
    {
        Span<int> into = stackalloc int[ArtistPopularTracks.ExtendedCap];
        int n = ArtistPopularTracks.Merge([9, 4, 9], [], into);
        Assert.Equal(new[] { 9, 4, 9 }, into[..n].ToArray());       // verbatim: a failed step two must never reorder the chart
    }

    [Fact]
    public void Merge_drops_duplicate_and_unidentified_entries()
    {
        Span<int> into = stackalloc int[ArtistPopularTracks.ExtendedCap];
        int n = ArtistPopularTracks.Merge([1], [1, 0, 1, 2], into);
        Assert.Equal(new[] { 1, 2 }, into[..n].ToArray());
    }

    [Fact]
    public void Merge_caps_at_the_extended_ceiling_and_the_seed_survives_the_cap()
    {
        var extension = new int[200];
        for (int i = 0; i < extension.Length; i++) extension[i] = 1000 + i;
        Span<int> into = stackalloc int[ArtistPopularTracks.ExtendedCap];
        int n = ArtistPopularTracks.Merge([7], extension, into);
        Assert.Equal(ArtistPopularTracks.ExtendedCap, n);
        Assert.Equal(7, into[0]);
    }

    [Fact]
    public void WithPlayCounts_fills_only_the_countless_rows_and_keeps_the_head()
    {
        uint[] chart = [500, 0, 0, 0];
        uint[] incoming = [1, 300, 0, 0];                     // a count for the head is ignored; 0 is never applied
        var into = new uint[4];
        Assert.True(ArtistPopularTracks.WithPlayCounts(chart, incoming, into));
        Assert.Equal(new uint[] { 500, 300, 0, 0 }, into);

        Span<int> need = stackalloc int[4];
        int n = ArtistPopularTracks.WithoutPlayCount([11, 12, 13, 14], into, need);
        Assert.Equal(new[] { 13, 14 }, need[..n].ToArray());
    }

    [Fact]
    public void WithPlayCounts_with_nothing_to_apply_reports_no_change()
    {
        var into = new uint[2];
        Assert.False(ArtistPopularTracks.WithPlayCounts([500, 0], [], into));
        Assert.False(ArtistPopularTracks.WithPlayCounts([500, 0], [9, 0], into));
    }

    [Fact]
    public void TopByPlays_ranks_by_plays_and_dedupes_by_title()
    {
        Span<int> into = stackalloc int[3];
        int n = ArtistPopularTracks.TopByPlays([10, 90, 50, 90], [1, 2, 3, 2], 3, into);
        Assert.Equal(new[] { 1, 2, 0 }, into[..n].ToArray());         // the second "title 2" (90) collapses into the first
    }
}

/// <summary>The merge at COMMIT: both answers, both orders, one batch or two.</summary>
[Collection(EntitiesCollection.Name)]
public class ArtistPopularCommitTests
{
    const string ArtistUri = "spotify:artist:popular-merge";

    static StagedId Id(Staging s, string uri) => new(s.Text(uri));
    static int SlotOf(string uri) => Entities.Current.Tracks.Slot(uri.AsSpan());
    static Artist ArtistOf() => Entities.Artist(EntityUri.Parse(ArtistUri.AsSpan()));

    static void StageList(Staging s, bool extension, params string[] tracks)
    {
        int mark = s.PopularMark;
        foreach (var t in tracks) s.PopularTracks.Add() = Id(s, t);
        s.EndPopular(Id(s, ArtistUri), mark, extension);
        if (extension) s.Artists.RowFor(Id(s, ArtistUri), Authority.Full, (uint)ArtistFields.Chart);
    }

    [Fact]
    public void A_seed_and_an_extension_in_ONE_batch_merge_seed_first()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageList(s, extension: true, "spotify:track:c", "spotify:track:a", "spotify:track:d");
        StageList(s, extension: false, "spotify:track:a", "spotify:track:b");
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf();
        Assert.Equal(new[] { SlotOf("spotify:track:a"), SlotOf("spotify:track:b"), SlotOf("spotify:track:c"), SlotOf("spotify:track:d") },
                     artist.PopularSlots.ToArray());
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistPopular.State(artist.Slot));
        Assert.True(artist.Knows(ArtistFields.Chart));
    }

    [Fact]
    public void An_extension_that_lands_AFTER_the_seed_appends_behind_the_committed_head()
    {
        TestScope.Fresh();
        var seed = Staging.Rent();
        StageList(seed, extension: false, "spotify:track:a", "spotify:track:b");
        TestScope.CommitAndPublish(seed);

        var ext = Staging.Rent();
        StageList(ext, extension: true, "spotify:track:b", "spotify:track:z");
        TestScope.CommitAndPublish(ext);

        Assert.Equal(new[] { SlotOf("spotify:track:a"), SlotOf("spotify:track:b"), SlotOf("spotify:track:z") },
                     ArtistOf().PopularSlots.ToArray());
    }

    [Fact]
    public void A_re_answered_overview_over_an_extended_chart_keeps_its_tail()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        StageList(first, extension: true, "spotify:track:a", "spotify:track:x", "spotify:track:y");
        TestScope.CommitAndPublish(first);

        var overview = Staging.Rent();
        StageList(overview, extension: false, "spotify:track:b", "spotify:track:a");
        TestScope.CommitAndPublish(overview);

        Assert.Equal(new[] { SlotOf("spotify:track:b"), SlotOf("spotify:track:a"), SlotOf("spotify:track:x"), SlotOf("spotify:track:y") },
                     ArtistOf().PopularSlots.ToArray());
    }

    [Fact]
    public void A_seed_over_an_UNEXTENDED_chart_replaces_it()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        StageList(first, extension: false, "spotify:track:old");
        TestScope.CommitAndPublish(first);

        var second = Staging.Rent();
        StageList(second, extension: false, "spotify:track:new");
        TestScope.CommitAndPublish(second);

        Assert.Equal(new[] { SlotOf("spotify:track:new") }, ArtistOf().PopularSlots.ToArray());
    }

    [Fact]
    public void The_extended_list_decoder_stages_an_extension_not_a_replacing_run()
    {
        // B1b's `Decode.ArtistTopTracks`, after its Decode.Entry patch: the extended list merges behind the seed.
        TestScope.Fresh();
        var seed = Staging.Rent();
        StageList(seed, extension: false, "spotify:track:1XGmzt0PVuFgQYYnV2It7A");
        TestScope.CommitAndPublish(seed);

        var s = Staging.Rent();
        string json = "{\"tracks\":[{\"uri\":\"spotify:track:2bL2gyO6kBdLkNSkxXNh6x\"},{\"uri\":\"spotify:track:1XGmzt0PVuFgQYYnV2It7A\"}]}";
        Spotify.Decode.ArtistTopTracks(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(ArtistUri), s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { SlotOf("spotify:track:1XGmzt0PVuFgQYYnV2It7A"), SlotOf("spotify:track:2bL2gyO6kBdLkNSkxXNh6x") },
                     ArtistOf().PopularSlots.ToArray());
    }
}
