using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DiscussionLayoutTests
{
    [Fact]
    public void Captured_vc4_comments_page_is_composer_eligible_at_the_top_level()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "podcast", "vc4-275-comments.json"));
        var page = Spotify.Podcasts.DecodeComments(bytes, false);

        Assert.True(page.Ok);
        Assert.Equal("ELIGIBILITY_STATUS_UNRESTRICTED", page.Eligibility);
        Assert.True(DiscussionLayout.ShowComposer(isReplyLevel: false, fake: false, eligibility: page.Eligibility));
        Assert.False(DiscussionLayout.AlreadyCommented(page.Eligibility));
        Assert.False(DiscussionLayout.CommentsDisabled(page.Eligibility));

        // Neither fixture comment is itself indented — they are top-level rows, not replies.
        Assert.Equal(2, page.Items.Length);
        Assert.Equal(0, DiscussionLayout.IndentOf(isReplyLevel: false));
    }

    [Theory]
    [InlineData(false, false, "ELIGIBILITY_STATUS_UNRESTRICTED", true)]
    [InlineData(false, false, "ELIGIBILITY_STATUS_ALREADY_COMMENTED", false)]
    [InlineData(false, false, "ELIGIBILITY_STATUS_DISABLED", false)]
    [InlineData(false, true, "ELIGIBILITY_STATUS_UNRESTRICTED", false)]     // Fake demo data never opens the composer
    [InlineData(true, false, "ELIGIBILITY_STATUS_ALREADY_COMMENTED", true)] // a reply thread is open regardless of the top-level status
    [InlineData(true, true, "ELIGIBILITY_STATUS_UNRESTRICTED", false)]
    public void ShowComposer_matches_reply_level_fake_and_eligibility(bool isReplyLevel, bool fake, string eligibility, bool expected)
        => Assert.Equal(expected, DiscussionLayout.ShowComposer(isReplyLevel, fake, eligibility));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void ShowConsent_only_while_the_composer_shows_and_consent_is_not_yet_given(bool showComposer, bool consented, bool expected)
        => Assert.Equal(expected, DiscussionLayout.ShowConsent(showComposer, consented));

    [Theory]
    [InlineData(true, false, "hello", true)]
    [InlineData(false, false, "hello", false)]   // no consent
    [InlineData(true, true, "hello", false)]     // a write is already in flight
    [InlineData(true, false, "", false)]         // nothing to post
    [InlineData(true, false, "   ", false)]      // whitespace only
    public void CanSubmit_requires_consent_no_busy_write_and_real_text(bool consented, bool busy, string text, bool expected)
        => Assert.Equal(expected, DiscussionLayout.CanSubmit(consented, busy, text));

    [Fact]
    public void AlreadyCommented_and_CommentsDisabled_are_mutually_exclusive_reasons()
    {
        Assert.True(DiscussionLayout.AlreadyCommented("ELIGIBILITY_STATUS_ALREADY_COMMENTED"));
        Assert.False(DiscussionLayout.CommentsDisabled("ELIGIBILITY_STATUS_ALREADY_COMMENTED"));

        Assert.True(DiscussionLayout.CommentsDisabled("ELIGIBILITY_STATUS_DISABLED"));
        Assert.True(DiscussionLayout.CommentsDisabled("ELIGIBILITY_STATUS_COMMENTS_DISABLED"));
        Assert.False(DiscussionLayout.AlreadyCommented("ELIGIBILITY_STATUS_DISABLED"));

        Assert.False(DiscussionLayout.AlreadyCommented(""));
        Assert.False(DiscussionLayout.CommentsDisabled(""));
    }

    [Fact]
    public void IndentOf_is_flat_never_deeper_than_one_avatar_column()
    {
        Assert.Equal(0, DiscussionLayout.IndentOf(isReplyLevel: false));
        Assert.Equal(1, DiscussionLayout.IndentOf(isReplyLevel: true));
    }

    // ── the replies disclosure (podcast-episode-peek §3) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, 4, true)]
    [InlineData(false, 0, false)]   // no replies ⇒ no affordance at all
    [InlineData(true, 4, false)]    // a reply's own replies render flat under it, never a second disclosure
    [InlineData(true, 0, false)]
    public void ShowReplies_only_on_a_top_level_comment_that_has_them(bool isReplyLevel, int replies, bool expected)
        => Assert.Equal(expected, DiscussionLayout.ShowReplies(isReplyLevel, replies));

    [Theory]
    [InlineData(3, 4, 3)]   // the stack caps at three even when more replied
    [InlineData(9, 40, 3)]
    [InlineData(2, 4, 2)]   // never more faces than the answer actually gave us
    [InlineData(3, 1, 1)]   // …nor more than there are replies, whatever the wire over-listed
    [InlineData(0, 4, 0)]   // nothing decoded ⇒ the count alone, no invented faces
    [InlineData(3, 0, 0)]
    [InlineData(-1, 4, 0)]
    public void ReplyAvatarCount_is_capped_by_three_by_the_answer_and_by_the_reply_count(int available, int replies, int expected)
        => Assert.Equal(expected, DiscussionLayout.ReplyAvatarCount(available, replies));

    [Fact]
    public void Captured_vc4_comments_carry_no_replier_avatars_so_no_stack_is_drawn()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "podcast", "vc4-275-comments.json"));
        var page = Spotify.Podcasts.DecodeComments(bytes, false);
        Assert.NotEmpty(page.Items);
        foreach (var comment in page.Items)
        {
            Assert.Empty(comment.ReplyAvatars);
            Assert.Equal(0, DiscussionLayout.ReplyAvatarCount(comment.ReplyAvatars.Length, comment.Replies));
            Assert.False(DiscussionLayout.ShowReplies(isReplyLevel: false, comment.Replies));
        }
    }
}

/// <summary>The reaction PILL's composition (podcast-episode-peek §3): which emoji it stacks, in what order, what the
/// label reads and when it is tinted. A real product decision, so it lives in a pure rule with these tests rather
/// than as branches inside the element builder.</summary>
public class ReactionPillRulesTests
{
    static Spotify.Podcasts.ReactionCount C(string emoji, int count) => new(emoji, count);

    const string Heart = "💗", Laugh = "😂", Wow = "😮", Clap = "👏";

    [Fact]
    public void No_reactions_at_all_is_the_call_to_action_with_one_neutral_face()
    {
        var pill = PodcastReactionRules.Pill(counts: null, myReaction: "", total: 0);
        Assert.True(pill.CallToAction);
        Assert.False(pill.Pressed);
        Assert.Equal(0, pill.Total);
        Assert.Equal([PodcastReactionRules.NeutralFace], pill.Emoji);
    }

    [Fact]
    public void A_known_breakdown_stacks_the_three_most_used_emoji_most_used_first()
    {
        var pill = PodcastReactionRules.Pill(
            [C(Laugh, 9), C(Heart, 30), C(Clap, 2), C(Wow, 12)], myReaction: "", total: 53);

        Assert.Equal<string[]>([Heart, Wow, Laugh], pill.Emoji);
        Assert.Equal(53, pill.Total);
        Assert.False(pill.Pressed);
        Assert.False(pill.CallToAction);
    }

    [Fact]
    public void Ties_keep_the_answers_own_order_and_empty_or_zero_entries_never_appear()
    {
        var pill = PodcastReactionRules.Pill(
            [C(Laugh, 4), C("", 99), C(Heart, 4), C(Clap, 0), C(Wow, 4)], myReaction: "", total: 12);

        Assert.Equal<string[]>([Laugh, Heart, Wow], pill.Emoji);
    }

    [Fact]
    public void Your_own_reaction_tints_the_pill_without_reordering_the_stack()
    {
        var pill = PodcastReactionRules.Pill([C(Heart, 30), C(Laugh, 9)], myReaction: Laugh, total: 39);
        Assert.True(pill.Pressed);
        Assert.False(pill.CallToAction);
        Assert.Equal<string[]>([Heart, Laugh], pill.Emoji);
        Assert.Equal(39, pill.Total);
    }

    [Fact]
    public void Without_a_breakdown_the_pill_shows_your_own_emoji_and_never_invents_one()
    {
        var mine = PodcastReactionRules.Pill(counts: [], myReaction: Heart, total: 7);
        Assert.Equal<string[]>([Heart], mine.Emoji);
        Assert.True(mine.Pressed);
        Assert.Equal(7, mine.Total);

        // Reactions exist but we do not know WHICH and none of them is yours: the neutral face stands in rather than
        // the pill claiming an emoji nobody sent.
        var theirs = PodcastReactionRules.Pill(counts: null, myReaction: "", total: 7);
        Assert.Equal<string[]>([PodcastReactionRules.NeutralFace], theirs.Emoji);
        Assert.False(theirs.Pressed);
        Assert.False(theirs.CallToAction);
        Assert.Equal(7, theirs.Total);
    }

    [Fact]
    public void An_optimistic_own_reaction_keeps_the_total_at_least_one_and_never_reads_as_React()
    {
        var pill = PodcastReactionRules.Pill(counts: [], myReaction: Heart, total: 0);
        Assert.False(pill.CallToAction);
        Assert.True(pill.Pressed);
        Assert.Equal(1, pill.Total);
    }

    [Fact]
    public void A_negative_total_is_clamped_rather_than_rendered()
        => Assert.Equal(0, PodcastReactionRules.Pill(counts: null, myReaction: null, total: -3).Total);
}
