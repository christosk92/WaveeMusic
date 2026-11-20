// ── Theming/Contracts/EpisodeProps.cs — the props of the episode discussion's views (themes plan 6.0, T9c) ────────────────────────
//
// The core hosts (`PodcastReaderUI.Discussion` and its kin) keep the
// data and the decisions and build ONE record per render they make; a record that holds a delegate, an Element or a signal never compares equal
// to another instance, so a view re-runs exactly when the host re-rendered (the tree used to be rebuilt then). Every member is read-only
// (WVTH006); a live cell travels as a signal or a Func the view binds.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One comment as a template reads it (the wire row without its reaction bookkeeping).</summary>
public sealed record DiscussionComment(
    string Uri, string Text, string Author, string Avatar, string Created, bool Pending, bool Sensitive, bool Pinned,
    int Replies, string[] ReplyAvatars, bool ReplyLimit);

/// <summary>The composer's cells and verbs (the thread's host owns the text, the consent and the submit).</summary>
public sealed record DiscussionComposerProps(
    Signal<string> Text, string Placeholder, bool ShowConsent, Signal<bool> Consent, Action<bool> OnConsent,
    string SubmitLabel, bool CanSubmit, Action Submit);

/// <summary>The props of <see cref="ViewKeys.EpisodeDiscussion"/>. A continuation page (<paramref name="Continuation"/>) is the list alone: no width cap.</summary>
public sealed record DiscussionThreadProps(
    string? Heading, Element[] Comments, string? Empty, Element? More, DiscussionComposerProps? Composer,
    string? Closed, string? Error, bool Continuation);

/// <summary>The props of <see cref="ViewKeys.EpisodeCommentCard"/>. <paramref name="Pill"/> is <see cref="PodcastReactionRules.Pill"/>'s decision;
/// <paramref name="Thread"/> is the host's mounted reply thread (null when closed), wrapped in the rail by the view.</summary>
public sealed record CommentCardProps(
    DiscussionComment Comment, bool Reply, bool Revealed, Action Reveal, ReactionPillModel Pill, Action OpenReactions,
    Action<NodeHandle> PillRealized, Func<bool> RepliesOpen, Action OpenReplies, Action ToggleReplies, Element? Thread);

/// <summary>The props of <see cref="ViewKeys.EpisodeCommentSeed"/>.</summary>
public sealed record CommentSeedProps(bool Reply);

/// <summary>The props of <see cref="ViewKeys.EpisodeLoadMore"/>.</summary>
public sealed record LoadMoreProps(Action Click, bool AlignStart);

/// <summary>One emoji of the palette: whether it is the viewer's current reaction (a second pick removes it) and whether it can be picked.</summary>
public sealed record ReactionOption(string Emoji, string Name, bool Chosen, bool Enabled);

/// <summary>The props of <see cref="ViewKeys.EpisodeReactionFlyout"/>: the palette, what a pick calls, the error line, the roster element and the window the flyout is clamped to.</summary>
public sealed record ReactionFlyoutProps(
    ReactionOption[] Palette, Action<string> React, string Error, Element Roster, float ViewportWidth, float ViewportHeight);

/// <summary>One roster filter chip ("All 12", "heart 4").</summary>
public sealed record ReactionFilter(string Label, string Emoji, bool Selected);

/// <summary>The props of <see cref="ViewKeys.EpisodeReactionRoster"/>.</summary>
public sealed record ReactionRosterProps(
    ReactionFilter[] Filters, Action<string> Select, Element People, string ScrollKey, string PeopleKey);

/// <summary>One person who reacted.</summary>
public sealed record ReactionPerson(string Emoji, string Author, string Avatar);

/// <summary>The props of <see cref="ViewKeys.EpisodeReactionPeople"/>: the people, the "load more" row (null at the end).</summary>
public sealed record ReactionPeopleProps(ReactionPerson[] People, Element? More);

