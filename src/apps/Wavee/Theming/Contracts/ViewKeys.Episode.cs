// ── Theming/Contracts/ViewKeys.Episode.cs — the episode page, reader and discussion keys (themes plan 6.0, T9c) ──────────────────
//
// T9c publishes the episode page's pictures over the T9a detail frame: the frame's slots (the primary, the show attribution, the action
// satellites and their Save heart, the rail's short About block), the reader (the pivot band over the stacked sections, the About section,
// the chapters list), the discussion (the thread, a comment card, the first-render seed, "Load more", the reaction flyout with its roster and
// people) and the show rating popover the show page also embeds.
//
// What stays core: the page host and its demand effects, the saved / played / resume decisions (`Episode.IsNowPlaying`, `Invoke`, `StartAt`,
// `MarkEpisode`, `Spotify.Podcasts.*`), the scroll spy (anchors, the viewport, the active section), the chapter lease, the transcript view,
// the discussion's signals, resources, composer verbs and reaction session, the overlay the reaction flyout opens in, and the pure rules
// (`PodcastReaderRules`, `DiscussionLayout`, `PodcastReactionRules`). A template draws and never names `Playback`, `Entities`, `Store`,
// `Shell` or `Spotify` (WVTH002): those arrive as props, signals and verbs.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The episode page's primary: the Play / Resume / Pause button, or the quiet line of a blocked episode. Props: <see cref="EpisodePrimaryProps"/>.</summary>
    public static readonly ViewKey<EpisodePrimaryProps> EpisodePrimary = new("Episode.Primary");

    /// <summary>The line under the title that names the show and links to it. Props: <see cref="EpisodeAttributionProps"/>.</summary>
    public static readonly ViewKey<EpisodeAttributionProps> EpisodeAttribution = new("Episode.Attribution");

    /// <summary>The rail's action satellites: Save, mark played, copy link, the menu. Props: <see cref="EpisodeActionsProps"/>.</summary>
    public static readonly ViewKey<EpisodeActionsProps> EpisodeActions = new("Episode.Actions");

    /// <summary>The Save heart satellite (its state is the host's). Props: <see cref="EpisodeSaveProps"/>.</summary>
    public static readonly ViewKey<EpisodeSaveProps> EpisodeSave = new("Episode.Save");

    /// <summary>The rail's short About block. Props: <see cref="EpisodeRailAboutProps"/>.</summary>
    public static readonly ViewKey<EpisodeRailAboutProps> EpisodeRailAbout = new("Episode.RailAbout");

    /// <summary>The reader: the pivot band over the scroll of stacked sections. Props: <see cref="EpisodeReaderProps"/>.</summary>
    public static readonly ViewKey<EpisodeReaderProps> EpisodeReader = new("Episode.Reader");

    /// <summary>The About section's body: the description region and the recognised chapter-seek list. Props: <see cref="EpisodeAboutProps"/>.</summary>
    public static readonly ViewKey<EpisodeAboutProps> EpisodeAbout = new("Episode.About");

    /// <summary>The chapters list. Props: <see cref="EpisodeChaptersProps"/>.</summary>
    public static readonly ViewKey<EpisodeChaptersProps> EpisodeChapters = new("Episode.Chapters");

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

    /// <summary>The show rating popover (the show page's rating slot opens it). Props: <see cref="ShowRatingProps"/>.</summary>
    public static readonly ViewKey<ShowRatingProps> EpisodeRating = new("Episode.Rating");
}
