using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PodcastFeatureReviewTests
{
    [Fact]
    public void Captured_comment_success_is_createComment_not_the_operation_name()
    {
        var result = new Spotify.Api.Result(200,
            """{"data":{"createComment":{"__typename":"SuccessCreateComment","success":true}}}"""u8.ToArray());
        Assert.True(Spotify.Podcasts.CommentMutationOf(result, reply: false).Success);
        Assert.False(Spotify.Podcasts.CommentMutationOf(result, reply: true).Success);
    }

    [Fact]
    public void Captured_reply_success_is_createCommentReply()
    {
        var result = new Spotify.Api.Result(200,
            """{"data":{"createCommentReply":{"__typename":"SuccessCreateCommentReply","hasUserReachedReplyLimit":false,"success":true}}}"""u8.ToArray());
        Assert.True(Spotify.Podcasts.CommentMutationOf(result, reply: true).Success);
    }

    [Fact]
    public void Captured_reaction_success_is_putCommentReaction()
    {
        var result = new Spotify.Api.Result(200,
            """{"data":{"putCommentReaction":{"__typename":"SuccessPutCommentReaction","success":true}}}"""u8.ToArray());
        Assert.True(Spotify.Podcasts.ReactionMutationOf(result, remove: false).Success);
    }

    [Fact]
    public void Deletion_union_does_not_require_an_uncaptured_success_boolean()
    {
        var result = new Spotify.Api.Result(200,
            """{"data":{"deleteCommentReaction":{"__typename":"SuccessDeleteCommentReaction"}}}"""u8.ToArray());
        Assert.True(Spotify.Podcasts.ReactionMutationOf(result, remove: true).Success);
    }

    [Fact]
    public void A_graphql_error_is_not_a_successful_empty_discussion_or_mutation()
    {
        byte[] bytes = """{"errors":[{"message":"Denied"}],"data":{"comments":[{"items":[]}],"createComment":{"__typename":"SuccessCreateComment","success":true}}}"""u8.ToArray();
        Assert.False(Spotify.Podcasts.DecodeComments(bytes, replies: false).Ok);
        Assert.False(Spotify.Podcasts.CommentMutationOf(new Spotify.Api.Result(200, bytes), reply: false).Success);
    }

    [Fact]
    public void Chapter_title_is_inside_the_Any_value()
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(1, WireFormat.WireType.LengthDelimited); writer.WriteString("Chapter");
            writer.WriteTag(2, WireFormat.WireType.LengthDelimited); writer.WriteString("The beginning");
        }
        Assert.Equal("The beginning", Spotify.Podcasts.DecodeChapterTitle(new Any
        { TypeUrl = "type.googleapis.com/fixture.IdentityTrait", Value = ByteString.CopyFrom(stream.ToArray()) }));
    }

    [Fact]
    public void Read_along_query_replaces_existing_parameters_without_losing_the_language_path()
    {
        var uri = new Uri(Spotify.Podcasts.TranscriptRequestUrl(
            "https://spclient.wg.spotify.com/transcript-read-along/v2/episode/fixture/de-de?format=other&maxSentenceLength=100&excludeCC=false&test=1", true));
        Assert.EndsWith("/de-de", uri.AbsolutePath);
        Assert.Contains("format=json", uri.Query);
        Assert.Contains("maxSentenceLength=500", uri.Query);
        Assert.Contains("excludeCC=true", uri.Query);
        Assert.Contains("test=1", uri.Query);
        Assert.DoesNotContain("format=other", uri.Query);
        Assert.DoesNotContain("excludeCC=false", uri.Query);
    }

    [Fact]
    public void An_error_document_cannot_be_cached_as_a_successful_empty_transcript()
        => Assert.False(Spotify.Podcasts.DecodeTranscript("""{"error":"temporarily unavailable"}"""u8.ToArray()).Ok);
}
