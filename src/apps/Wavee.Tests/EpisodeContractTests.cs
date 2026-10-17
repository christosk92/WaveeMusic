// ── Wavee.Tests/EpisodeContractTests.cs — the episode family's published keys and Classic's registration (themes plan 6.0, T9c) ──────────
//
// The episode page's slots, the reader, the discussion and the show rating popover are the structure's pictures over core hosts (`Episode`'s
// page components, `PodcastReaderUI`). What is pure about the contract: the ids (a published id never changes), Classic registering every key
// under its own template id, and the discussion's pure decisions the views read (`DiscussionLayout`, `PodcastReactionRules`) staying core.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class EpisodeContractTests
{
    static readonly (string Id, string Template)[] Expected =
    [
        ("Episode.Primary", "classic.episode.primary"),
        ("Episode.Attribution", "classic.episode.attribution"),
        ("Episode.Actions", "classic.episode.actions"),
        ("Episode.Save", "classic.episode.save"),
        ("Episode.RailAbout", "classic.episode.rail-about"),
        ("Episode.Reader", "classic.episode.reader"),
        ("Episode.About", "classic.episode.about"),
        ("Episode.Chapters", "classic.episode.chapters"),
        ("Episode.Discussion", "classic.episode.discussion"),
        ("Episode.CommentCard", "classic.episode.comment-card"),
        ("Episode.CommentSeed", "classic.episode.comment-seed"),
        ("Episode.LoadMore", "classic.episode.load-more"),
        ("Episode.ReactionFlyout", "classic.episode.reaction-flyout"),
        ("Episode.ReactionRoster", "classic.episode.reaction-roster"),
        ("Episode.ReactionPeople", "classic.episode.reaction-people"),
        ("Episode.Rating", "classic.episode.rating"),
    ];

    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Episode.Primary", ViewKeys.EpisodePrimary.Id);
        Assert.Equal("Episode.Attribution", ViewKeys.EpisodeAttribution.Id);
        Assert.Equal("Episode.Actions", ViewKeys.EpisodeActions.Id);
        Assert.Equal("Episode.Save", ViewKeys.EpisodeSave.Id);
        Assert.Equal("Episode.RailAbout", ViewKeys.EpisodeRailAbout.Id);
        Assert.Equal("Episode.Reader", ViewKeys.EpisodeReader.Id);
        Assert.Equal("Episode.About", ViewKeys.EpisodeAbout.Id);
        Assert.Equal("Episode.Chapters", ViewKeys.EpisodeChapters.Id);
        Assert.Equal("Episode.Discussion", ViewKeys.EpisodeDiscussion.Id);
        Assert.Equal("Episode.CommentCard", ViewKeys.EpisodeCommentCard.Id);
        Assert.Equal("Episode.CommentSeed", ViewKeys.EpisodeCommentSeed.Id);
        Assert.Equal("Episode.LoadMore", ViewKeys.EpisodeLoadMore.Id);
        Assert.Equal("Episode.ReactionFlyout", ViewKeys.EpisodeReactionFlyout.Id);
        Assert.Equal("Episode.ReactionRoster", ViewKeys.EpisodeReactionRoster.Id);
        Assert.Equal("Episode.ReactionPeople", ViewKeys.EpisodeReactionPeople.Id);
        Assert.Equal("Episode.Rating", ViewKeys.EpisodeRating.Id);
    }

    [Fact]
    public void Classic_registers_every_episode_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, template) in Expected)
        {
            Assert.Equal(template, classic.Registry.TemplateIdOf(id));
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_reply_never_nests_past_its_one_indent_and_the_disclosure_needs_replies()
    {
        // the decisions the comment card's view reads stay core (`DiscussionLayout`): a reply's own replies render flat
        Assert.False(DiscussionLayout.ShowReplies(true, 4));
        Assert.False(DiscussionLayout.ShowReplies(false, 0));
        Assert.True(DiscussionLayout.ShowReplies(false, 2));
        Assert.Equal(3, DiscussionLayout.ReplyAvatarCount(9, 9));
        Assert.Equal(1, DiscussionLayout.ReplyAvatarCount(5, 1));
    }
}
