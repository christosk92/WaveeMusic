using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PodcastReactionTests
{
    [Theory]
    [InlineData("", "\u2764\uFE0F", false)]
    [InlineData("\u2764\uFE0F", "\u2764\uFE0F", true)]
    [InlineData("\u2764", "\u2764\uFE0F", true)]
    [InlineData("\u2764\uFE0F", "\uD83D\uDC4D", false)]
    public void Picking_the_current_reaction_removes_while_another_replaces(string current, string picked, bool remove)
        => Assert.Equal(remove, PodcastReactionRules.Remove(current, picked));

    [Fact]
    public void Decoder_preserves_counts_for_filters_and_pagination_not_only_loaded_people()
    {
        var page = Spotify.Podcasts.DecodeReactions("""
            {"data":{"commentReactions":[{
              "nextPageToken":"next-page",
              "reactionCounts":[{"reactionUnicode":"heart","numberOfReactions":30},{"reactionUnicode":"laugh","numberOfReactions":8}],
              "items":[{"reactionUnicode":"heart","author":{"data":{"name":"Listener","avatar":{"sources":[{"url":"https://example.test/avatar"}]}}},"createDate":{"isoString":"2026-09-19T10:00:00Z"}}]
            }]}}
            """u8.ToArray());
        Assert.True(page.Ok);
        Assert.Equal(38, page.Total);
        Assert.Equal("next-page", page.NextToken);
        Assert.Equal(new[] { new Spotify.Podcasts.ReactionCount("heart", 30), new Spotify.Podcasts.ReactionCount("laugh", 8) }, page.ReactionCounts);
        var person = Assert.Single(page.Items);
        Assert.Equal("Listener", person.Author);
        Assert.Equal("https://example.test/avatar", person.Avatar);
    }

    [Fact]
    public void Missing_author_preserves_anonymous_participant_instead_of_dropping_the_row()
    {
        var page = Spotify.Podcasts.DecodeReactions("""
            {"data":{"commentReactions":[{"reactionCounts":[],"items":[{"reactionUnicode":"heart","author":{"data":null}}]}]}}
            """u8.ToArray());
        var person = Assert.Single(page.Items);
        Assert.Empty(person.Author);
        Assert.Empty(person.Avatar);
        Assert.Equal("heart", person.Emoji);
    }

    [Theory]
    [InlineData("""{"data":{"putCommentReaction":{"__typename":"ErrorPutCommentReaction","success":false}}}""")]
    [InlineData("""{"data":{"putCommentReaction":{"__typename":"SuccessPutCommentReaction","success":false}}}""")]
    [InlineData("""{"errors":[{"message":"Unsupported reaction"}],"data":{"putCommentReaction":{"__typename":"SuccessPutCommentReaction","success":true}}}""")]
    public void Rejected_emoji_never_reports_success(string json)
    {
        var result = Spotify.Podcasts.ReactionMutationOf(new Spotify.Api.Result(200, System.Text.Encoding.UTF8.GetBytes(json)), false);
        Assert.False(result.Success);
        Assert.Equal(422, result.Status);
    }

    [Fact]
    public void Error_page_is_unavailable_not_an_empty_participant_list()
    {
        var page = Spotify.Podcasts.DecodeReactions("""{"errors":[{"message":"Denied"}],"data":{"commentReactions":[{"items":[]}]}}"""u8.ToArray());
        Assert.False(page.Ok);
    }
}
