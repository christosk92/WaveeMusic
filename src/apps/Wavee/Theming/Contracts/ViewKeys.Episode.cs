// ── Theming/Contracts/ViewKeys.Episode.cs — the episode discussion's keys (themes plan 6.0, T9c) ──────────────────────────────────
//
// T9c publishes the discussion's pictures: the thread, a comment card, the first-render seed, "Load more", the reaction flyout with its roster
// and people. The episode page itself is `ViewKeys.PodcastEpisodePage` (`ViewKeys.PodcastPages.cs`), which mounts the discussion.
//
// What stays core: the discussion's signals, resources, composer verbs and reaction session, the overlay the reaction flyout opens in, and the
// pure rules (`PodcastReaderRules`, `DiscussionLayout`, `PodcastReactionRules`). A template draws and never names `Playback`, `Entities`,
// `Store`, `Shell` or `Spotify` (WVTH002): those arrive as props, signals and verbs.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>One discussion thread (or one continuation page of it): the heading, the comments, "no comments", "Load more", the composer or
    /// the closed note, the error. Props: <see cref="DiscussionThreadProps"/>.</summary>
    public static readonly ViewKey<DiscussionThreadProps> EpisodeDiscussion = new("Episode.Discussion");

    /// <summary>One comment: avatar column, name line, body, the reaction pill and Reply, the replies disclosure and the reply thread. Props:
    /// <see cref="CommentCardProps"/>.</summary>
    public static readonly ViewKey<CommentCardProps> EpisodeCommentCard = new("Episode.CommentCard");

    /// <summary>The thread's loading seed: placeholder comments in the live card's frame. Props: <see cref="CommentSeedProps"/>.</summary>
    public static readonly ViewKey<CommentSeedProps> EpisodeCommentSeed = new("Episode.CommentSeed");

    /// <summary>The "Load more" button of a comment or reaction page. Props: <see cref="LoadMoreProps"/>.</summary>
    public static readonly ViewKey<LoadMoreProps> EpisodeLoadMore = new("Episode.LoadMore");

    /// <summary>The reaction flyout: the emoji palette, the error and the roster. Props: <see cref="ReactionFlyoutProps"/>.</summary>
    public static readonly ViewKey<ReactionFlyoutProps> EpisodeReactionFlyout = new("Episode.ReactionFlyout");

    /// <summary>The flyout's roster: the filter chips over the scrolling people. Props: <see cref="ReactionRosterProps"/>.</summary>
    public static readonly ViewKey<ReactionRosterProps> EpisodeReactionRoster = new("Episode.ReactionRoster");

    /// <summary>The people who reacted: avatar, name, emoji. Props: <see cref="ReactionPeopleProps"/>.</summary>
    public static readonly ViewKey<ReactionPeopleProps> EpisodeReactionPeople = new("Episode.ReactionPeople");
}
