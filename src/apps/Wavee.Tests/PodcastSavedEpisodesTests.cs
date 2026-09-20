using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PodcastSavedEpisodesTests
{
    const string EpisodeUri = "spotify:episode:0Q86acNRm6V9GYx55SXKwf";

    [Fact]
    public void Discovery_uses_the_format_not_the_translated_title_or_the_first_playlist()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            {"data":{"me":{"libraryV3":{"totalCount":75,"items":[
              {"item":{"data":{"__typename":"Playlist","format":"other","name":"Your Episodes","uri":"spotify:playlist:wrong"}}},
              {"item":{"data":{"__typename":"Playlist","format":"listen-later","name":"Gespeicherte Folgen","uri":"spotify:playlist:canonical"}}}
            ]}}}}
            """);
        var result = Spotify.Podcasts.DecodeSavedDiscovery(json);
        Assert.Equal("spotify:playlist:canonical", result.Uri);
        Assert.Equal(2, result.Count);
        Assert.Equal(75, result.Total);
    }

    [Fact]
    public void A_full_page_without_the_special_list_keeps_the_total_for_further_paging()
    {
        var result = Spotify.Podcasts.DecodeSavedDiscovery(Encoding.UTF8.GetBytes("""
            {"data":{"me":{"libraryV3":{"totalCount":75,"items":[{"item":{"data":{"__typename":"Playlist","format":"other"}}}]}}}}
            """));
        Assert.Empty(result.Uri);
        Assert.True(result.Total > result.Count);
    }

    [Fact]
    public void Discovery_requests_all_library_pages_with_the_captured_feature()
    {
        using var document = JsonDocument.Parse(Spotify.Podcasts.SavedLibraryBody(50));
        var root = document.RootElement;
        Assert.Equal("libraryV3", root.GetProperty("operationName").GetString());
        var variables = root.GetProperty("variables");
        Assert.Equal(50, variables.GetProperty("offset").GetInt32());
        Assert.Equal(50, variables.GetProperty("limit").GetInt32());
        Assert.Contains("YOUR_EPISODES_V2", variables.GetProperty("features").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Remove_uses_the_readback_canonical_id_without_reusing_or_truncating_the_submitted_id()
    {
        const string submitted = "6bcb433ddc6b6536e20b";
        const string canonical = "6bcb433ddc6b6536";
        var ops = Spotify.Podcasts.SavedMutationOps(EpisodeUri, false,
            [new PlaylistOps.WireItem(EpisodeUri, canonical)], 1, submitted);
        var remove = Assert.Single(ops);
        Assert.True(remove.ItemsAsKey);
        Assert.Equal(canonical, Assert.Single(remove.Items!).ItemId);
    }

    [Fact]
    public void Remove_preserves_a_longer_canonical_id_when_the_server_returned_it()
    {
        const string canonical = "00112233445566778899";
        var ops = Spotify.Podcasts.SavedMutationOps(EpisodeUri, false,
            [new PlaylistOps.WireItem(EpisodeUri, canonical)], 1, "unused");
        Assert.Equal(canonical, Assert.Single(Assert.Single(ops).Items!).ItemId);
    }

    [Fact]
    public void Save_is_idempotent_when_readback_already_contains_the_episode()
        => Assert.Empty(Spotify.Podcasts.SavedMutationOps(EpisodeUri, true,
            [new PlaylistOps.WireItem(EpisodeUri, "canonical")], 1, "minted"));

    [Fact]
    public void Missing_remove_keys_cannot_fall_back_to_a_stale_positional_delete()
        => Assert.Throws<InvalidDataException>(() => Spotify.Podcasts.SavedMutationOps(EpisodeUri, false,
            [new PlaylistOps.WireItem(EpisodeUri)], 1, "unused"));

    [Fact]
    public void Add_goes_first_and_sends_the_supplied_millisecond_timestamp()
    {
        var add = Assert.Single(Spotify.Podcasts.SavedMutationOps(EpisodeUri, true, [], 1789823105123, "00112233445566778899"));
        Assert.True(add.AddFirst);
        Assert.Equal(1789823105123, Assert.Single(add.Items!).AddedAtMs);
    }

    // ── §D3.1: Empty/Failed split apart — a real 404 for the missing listen-later playlist, or a genuinely
    // empty read, must both land Ready (never "unavailable"); only a real transport failure is Failed, and only
    // when nothing has ever landed before. ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_zero_item_read_lands_ready_not_failed()
    {
        var (ready, failed) = Spotify.Podcasts.FoldSavedLanding(Spotify.Podcasts.SavedLandingOutcome.Data, previouslyReady: false);
        Assert.True(ready);
        Assert.False(failed);
    }

    [Fact]
    public void The_discovery_exhausted_404_lands_ready_not_failed()
    {
        var (ready, failed) = Spotify.Podcasts.FoldSavedLanding(Spotify.Podcasts.SavedLandingOutcome.ConfirmedEmpty, previouslyReady: false);
        Assert.True(ready);
        Assert.False(failed);
    }

    [Fact]
    public void A_first_read_transport_failure_is_failed_not_ready()
    {
        var (ready, failed) = Spotify.Podcasts.FoldSavedLanding(Spotify.Podcasts.SavedLandingOutcome.Failure, previouslyReady: false);
        Assert.False(ready);
        Assert.True(failed);
    }

    [Fact]
    public void A_later_transport_failure_keeps_already_landed_data_and_is_not_failed()
    {
        var (ready, failed) = Spotify.Podcasts.FoldSavedLanding(Spotify.Podcasts.SavedLandingOutcome.Failure, previouslyReady: true);
        Assert.True(ready);
        Assert.False(failed);
    }

    [Fact]
    public void A_failure_after_an_earlier_failure_stays_failed()
    {
        var (ready, failed) = Spotify.Podcasts.FoldSavedLanding(Spotify.Podcasts.SavedLandingOutcome.Failure, previouslyReady: false);
        Assert.False(ready);
        Assert.True(failed);
    }
}
