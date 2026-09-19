using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PodcastReaderContractTests
{
    [Fact]
    public void Replies_use_replyString_and_accept_an_author_without_avatar()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "podcast", "vc3-594-replies.json"));
        var page = Spotify.Podcasts.DecodeComments(bytes, replies: true);
        Assert.True(page.Ok);
        var reply = Assert.Single(page.Items);
        Assert.Equal("A fixture reply.", reply.Text);
        Assert.Equal("Listener", reply.Author);
        Assert.Equal("", reply.Avatar);
        Assert.Equal("spotify:comment:fixture-reply", reply.Uri);
    }

    [Fact]
    public void Transcript_title_and_sentence_are_alternatives_and_empty_titles_are_omitted()
    {
        var transcript = Spotify.Podcasts.DecodeTranscript("""
            {"language":"en-us","section":[
             {"startMs":0,"title":{"title":"Introduction"}},
             {"startMs":100,"text":{"sentence":{"startMs":100,"text":"Hello there.","highlight":[{"startMs":100,"numChars":6},{"startMs":400,"numChars":6}]}}},
             {"startMs":1000,"title":{}},
             {"startMs":1200,"text":{"sentence":{"startMs":1250,"text":"Next paragraph."}}}]}
            """u8.ToArray());
        Assert.Equal(3, transcript.Lines.Length);
        Assert.True(transcript.Lines[0].Heading);
        Assert.False(transcript.Lines[1].Heading);
        Assert.Equal("Next paragraph.", transcript.Lines[2].Text);
        Assert.False(transcript.Lines[2].Heading);
        Assert.Equal(1250, transcript.Lines[2].StartMs);
    }

    [Fact]
    public void A_200_mutation_requires_the_success_discriminator_and_ambiguous_failure_is_not_success()
    {
        var accepted = new Spotify.Api.Result(200, """{"data":{"deleteCommentReaction":{"__typename":"SuccessDeleteCommentReaction"}}}"""u8.ToArray());
        Assert.True(Spotify.Podcasts.MutationOf(accepted, "deleteCommentReaction", "SuccessDeleteCommentReaction", requireSuccess: false).Success);
        Assert.False(Spotify.Podcasts.MutationOf(new(200, """{"errors":[{"message":"denied"}]}"""u8.ToArray()), "deleteCommentReaction", "SuccessDeleteCommentReaction").Success);
        Assert.True(Spotify.Podcasts.MutationOf(new(0, []), "addComment", "SuccessAddComment").Ambiguous);
    }
}

[Collection(EntitiesCollection.Name)]
public class PodcastCatalogContractTests : IDisposable
{
    const string EpisodeUri = "spotify:episode:0000000000000000000001";
    const string ShowUri = "spotify:show:0000000000000000000002";
    public PodcastCatalogContractTests() { Fetch.Reset(); Store.Shutdown(); Store.Use(null); Entities.Boot(CatalogScope.Fake()); }
    public void Dispose() { Fetch.Reset(); Store.Shutdown(); Store.Use(null); }

    [Fact]
    public void Web_detail_does_not_claim_or_overwrite_progress_duration_or_transcripts()
    {
        var s = new Staging();
        Spotify.Decode.EpisodeDetail("""{"data":{"episodeUnionV2":{"__typename":"Episode","htmlDescription":"<p>Description</p>","restrictions":{"paywallContent":true},"playability":{"playable":false},"previewPlayback":{"audioPreview":{"cdnUrl":"https://fixture.invalid/preview"}},"playedState":{"playPositionMilliseconds":10}}}}"""u8.ToArray(), EpisodeUri, s);
        Entities.Commit(s);
        var episode = new Episode(Entities.Current.Episodes.Slot(EntityId.Parse(EpisodeUri)));
        Assert.True(episode.Knows(EpisodeFields.Detail));
        Assert.False(episode.Knows(EpisodeFields.Progress));
        Assert.False(episode.Knows(EpisodeFields.Duration));
        Assert.False(episode.Knows(EpisodeFields.Transcript));
        Assert.True(episode.Flags.HasFlag(EpisodeFlags.Paywalled));
        Assert.True(episode.Flags.HasFlag(EpisodeFlags.PreviewOnly));
    }

    [Fact]
    public void Show_list_retains_canonical_ids_and_is_a_replayable_baseline()
    {
        const string revision = "0,00112233445566778899aabbccddeeff00112233";
        var s = new Staging();
        ListRow[] rows = [ListAnswers.Member(EpisodeUri, "00112233445566778899", 123)];
        Assert.True(Store.StageList(s, EdgeRelation.ShowEpisodes, ShowUri, rows, revision));
        Entities.Commit(s);
        int show = Entities.Current.Shows.Slot(EntityId.Parse(ShowUri));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ShowEpisodes.State(show));
        Assert.Equal(revision, Entities.Strings.Resolve(Entities.Current.Shows.ListRevision[show]));
        var baseline = Assert.Single(Store.SnapshotList(Entities.Current, EdgeRelation.ShowEpisodes, show)!);
        Assert.Equal(EpisodeUri, baseline.Uri);
        Assert.Equal("00112233445566778899", baseline.ItemId);
    }

    [Fact]
    public void Packed_media_enums_do_not_confuse_varint_bytes_with_enum_values()
    {
        var s = new Staging();
        // 129 contains byte 1 but does not mean Explicit (enum 1).
        // 258 contains byte 2 but does not mean Video (enum 2).
        Spotify.Decode.EpisodeMedia([0x12, 2, 0x81, 1, 0x22, 2, 0x82, 2], Encoding.UTF8.GetBytes(EpisodeUri), s);
        Entities.Commit(s);
        var episode = new Episode(Entities.Current.Episodes.Slot(EntityId.Parse(EpisodeUri)));
        Assert.False(episode.Flags.HasFlag(EpisodeFlags.Explicit));
        Assert.False(episode.Flags.HasFlag(EpisodeFlags.Video));
    }

    [Fact]
    public void A_rating_refresh_does_not_erase_the_show_tone()
    {
        {
            var s = new Staging();
            ref var row = ref s.Shows.Add(); row.Id = EntityId.Parse(ShowUri);
            row.Known = (uint)ShowFields.Appearance; row.Authority = Authority.Full; row.Tone = 0xff123456;
            Entities.Commit(s);
        }
        var rating = new Staging();
        using var body = new MemoryStream();
        using (var w = new CodedOutputStream(body, leaveOpen: true)) { w.WriteTag(3, WireFormat.WireType.Varint); w.WriteBool(true); }
        Spotify.Decode.PodcastRating(body.ToArray(), Encoding.UTF8.GetBytes(ShowUri), rating);
        Entities.Commit(rating);
        var show = new Show(Entities.Current.Shows.Slot(EntityId.Parse(ShowUri)));
        Assert.Equal(0xff123456u, show.Tone);
        Assert.True(show.Flags.HasFlag(ShowFlags.CanRate));
    }
}
