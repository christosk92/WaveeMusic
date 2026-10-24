// ── Theming/Contracts/EpisodeProps.cs — the props of the episode family's views (themes plan 6.0, T9c) ────────────────────────────
//
// The core hosts (`Episode.EpisodePage`'s slot components, `Episode.EpisodeReader`, `PodcastReaderUI.Discussion` and its kin) keep the
// data and the decisions and build ONE record per render they make; a record that holds a delegate, an Element or a signal never compares equal
// to another instance, so a view re-runs exactly when the host re-rendered (the tree used to be rebuilt then). Every member is read-only
// (WVTH006); a live cell travels as a signal or a Func the view binds.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.EpisodePrimary"/>. <paramref name="Blocked"/> is the quiet line of an unplayable episode (null: draw the button).</summary>
public sealed record EpisodePrimaryProps(string? Blocked, string Label, bool Playing, Action Play);

/// <summary>The props of <see cref="ViewKeys.EpisodeAttribution"/>: the link row's markup (the show name escaped, anchored to its route) and the verb a link opens.</summary>
public sealed record EpisodeAttributionProps(string Html, Action<string> Open);

/// <summary>The props of <see cref="ViewKeys.EpisodeActions"/>: the Save satellite (its own key, mounted by the host), whether the episode is
/// completed (the mark's glyph and name), the mark, the link copy and the lazily built menu.</summary>
public sealed record EpisodeActionsProps(Element Save, bool Completed, Action ToggleCompleted, Action CopyLink, Func<ContextMenuModel?> Menu);

/// <summary>The props of <see cref="ViewKeys.EpisodeSave"/>: the glyph, the accessible name and the click (null draws it disabled).</summary>
public sealed record EpisodeSaveProps(string Glyph, string Name, Action? Click);

/// <summary>The props of <see cref="ViewKeys.EpisodeRailAbout"/>: the show-notes prose, the clamp's key and the verb a link opens.</summary>
public sealed record EpisodeRailAboutProps(string Prose, string Key, Action<string> Open);

/// <summary>The props of <see cref="ViewKeys.EpisodeReader"/>. <paramref name="SectionClicks"/> are the four pivot clicks (About, Chapters,
/// Transcript, Comments), <paramref name="Anchors"/> the four sections' <c>OnRealized</c>; the spy, the viewport and the scroll handle are
/// the host's.</summary>
public sealed record EpisodeReaderProps(
    Action[] SectionClicks, IReadSignal<int> Active, Action<NodeHandle>[] Anchors,
    Element About, Element Related, Element Chapters, Element Transcript, Element Comments,
    string Key, string ScrollKey, Action<NodeHandle> CaptureViewport, ScrollHandle Scroll);

/// <summary>One chapter row.</summary>
public sealed record EpisodeChapterRow(string Key, int StartMs, string Title, bool Active);

/// <summary>The props of <see cref="ViewKeys.EpisodeChapters"/>: the rows, the seek verb and the playing flag the active row's equalizer binds.</summary>
public sealed record EpisodeChaptersProps(EpisodeChapterRow[] Rows, Action<int> SeekTo, IReadSignal<bool> Playing);

/// <summary>The props of <see cref="ViewKeys.EpisodeAbout"/>: the description's load state (the host's decision) and the loading text its shimmer is derived from, what the quiet notes say, the
/// retry, the link verb, the seek verb of a recognised chapter run, and the signal the equalizer binds.</summary>
public sealed record EpisodeAboutProps(
    Loadable<string> Description, string Seed, string Uri, bool PreviewOnly, bool Paywalled,
    Action Retry, Action<string> Open, Action<int> SeekTo, IReadSignal<bool> Playing);

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

/// <summary>The props of <see cref="ViewKeys.EpisodeRating"/>: the popover's title, the viewer's stars, what a pick calls (null while busy or offline), the tone and the error line.</summary>
public sealed record ShowRatingProps(string Title, int Rating, Action<int>? Rate, Func<ColorF>? Tone, string Error);
