// ── Theming/Contracts/PodcastPageProps.cs — the props of the podcast show, episode and audiobook pages (podcast pages v2) ──────────────────────
//
// One snapshot record per page (`PodcastShowView`, `PodcastEpisodeView`, `AudiobookView`) carried as an `IReadSignal`: the host rebuilds it when its
// inputs publish, never per frame, and a template binds it (or reads it in the region that needs it). Every text a theme shows is already worked out
// and localised by the host (labels, dates, lengths, "N min left"); a theme picks its casing with `R.Case` / `TextEl.Case` and never formats a number.
// The page's own cells (filter, search, sort, selection, expansion, tabs) are the host's signals, read through `IReadSignal`, written through verbs; the
// search text travels as `Func<Signal<string>>` (a writable cell, WVTH006). Live playback values (the position the transcript and the map follow) are
// separate signals, bound only in the leaves that move.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Whether a page's data is still on its way, landed, or failed.</summary>
public enum PodcastLoad : byte { Pending, Ready, Failed }

/// <summary>An episode's state at rest (concept note 3): New (an accent dot and the word), Unplayed (a semibold title), InProgress (a ring and the time
/// left), Played (a check, a quiet title and "Played").</summary>
public enum PodcastRowState : byte { Unplayed, New, InProgress, Played }

/// <summary>The SelectorBar's filters (concept note 4). Unplayed excludes episodes you have started.</summary>
public enum PodcastFilter : byte { All, Unplayed, InProgress, Saved }

/// <summary>What the one smart action plays ("The smart action, state by state").</summary>
public enum PodcastSmartKind : byte { Resume, PlayNewest, PlayPart, CaughtUp }

/// <summary>The details pane's / About's tabs on the show page.</summary>
public enum PodcastPaneTab : byte { Episode, About }

/// <summary>The episode page's sections (the transcript is a tab only where the window is too narrow to keep it beside the chapters).</summary>
public enum PodcastEpisodeTab : byte { Chapters, About, Transcript, Comments }

/// <summary>Where a listener is in an audiobook.</summary>
public enum AudiobookState : byte { NotStarted, InProgress, Finished }

/// <summary>One episode as every theme reads it. <see cref="Number"/> is the part number of a serial (by position) or the episode's own number, 0 = none.
/// <see cref="Progress"/> is 0..1 (a ring for <see cref="PodcastRowState.InProgress"/> only).</summary>
public sealed record PodcastEpisodeVm(
    string Id, string Title, string Description, int Number, bool Serial,
    string Date, int Year, string Length, string Left, float Progress,
    PodcastRowState State, bool Saved, bool Video, bool Trailer, bool Playable,
    int ChapterCount, bool HasTranscript, string? ArtUrl);

/// <summary>One item of the smart action's menu ("Other ways to play").</summary>
public sealed record PodcastMenuItem(string Label, string? Sub, string Glyph, bool Checked, Action Invoke);

/// <summary>The one smart action (concept note 1): the label names what it does, <see cref="EpisodeTitle"/> the episode, <see cref="Why"/> the prefix
/// ("Up next", "New"; empty when caught up), <see cref="Sub"/> the line under it (the time left and chapter, the date and length, the part count, the
/// caught-up line). <see cref="Accent"/> false = caught up: a standard button, no accent. <see cref="Progress"/> &lt; 0: no bar.</summary>
public sealed record PodcastSmart(
    PodcastSmartKind Kind, string EpisodeId, string Label, string Why, string EpisodeTitle, string Sub, float Progress, bool Accent,
    ImmutableArray<PodcastMenuItem> Menu);

/// <summary>One group of the episode list: "New since your last visit" (<see cref="Fresh"/>, offers Mark all as played), a year, or a serial's "In order".
/// <see cref="Count"/> is the group's stated size (the year's whole count when known). <see cref="Rows"/> index <see cref="PodcastShowView.Episodes"/>.</summary>
public sealed record PodcastGroup(string Key, string Title, int Count, bool Fresh, ImmutableArray<int> Rows);

/// <summary>One cell of the jump list: a year and how many episodes it holds.</summary>
public sealed record PodcastYear(int Year, string Label, string Count);

/// <summary>A filter of the SelectorBar with its count (0 shows no count).</summary>
public sealed record PodcastFilterVm(PodcastFilter Filter, string Label, int Count);

/// <summary>A "More like this" card: a show or a book.</summary>
public sealed record PodcastCardVm(string Uri, string Title, string Subtitle, string? ArtUrl);

/// <summary>A fact of the About list ("Publisher · Halden Audio").</summary>
public sealed record PodcastFact(string Label, string Value);

/// <summary>The show's identity and the About facts. <see cref="Tone"/> is the cover's dark colour (the wash), 0 = none. <see cref="RatingX100"/> 0 = no
/// public rating.</summary>
public sealed record PodcastShowHead(
    string Uri, string Title, string? ArtUrl, uint Tone, string Kind, ImmutableArray<string> Topics, bool Video, bool Serial,
    string Publisher, int RatingX100, string Ratings, string Cadence, string Count, string About, bool Following,
    string? TrailerId, string TrailerSub, ImmutableArray<PodcastFact> Facts);

/// <summary>The show page's snapshot.</summary>
public sealed record PodcastShowView(
    PodcastLoad Load, PodcastShowHead Head, PodcastSmart? Smart,
    ImmutableArray<PodcastEpisodeVm> Episodes, ImmutableArray<PodcastGroup> Groups, ImmutableArray<PodcastFilterVm> Filters,
    ImmutableArray<PodcastYear> Years, bool HasMore, bool Filtered, ImmutableArray<PodcastCardVm> Similar)
{
    /// <summary>The empty snapshot a page shows while nothing is known (the skeleton is derived from the loaded layout over it).</summary>
    public static PodcastShowView Empty { get; } = new(PodcastLoad.Pending,
        new PodcastShowHead("", "", null, 0u, "", [], false, false, "", 0, "", "", "", "", false, null, "", []),
        null, [], [], [], [], false, false, []);
}

/// <summary>The props of <see cref="ViewKeys.PodcastShowPage"/>.</summary>
public sealed record PodcastShowPageProps
{
    /// <summary>The page's route key (stable per show): key the scroller and the page's lint root under it.</summary>
    public required string RouteKey { get; init; }
    public required IReadSignal<PodcastShowView> View { get; init; }
    public required IReadSignal<PodcastFilter> Filter { get; init; }
    public required Action<PodcastFilter> SetFilter { get; init; }
    public required Func<Signal<string>> Query { get; init; }
    public required IReadSignal<bool> Oldest { get; init; }
    public required Action ToggleSort { get; init; }
    /// <summary>The selected episode (the details pane follows it); null shows the pane's hint / About.</summary>
    public required IReadSignal<string?> Selected { get; init; }
    public required Action<string?> Select { get; init; }
    /// <summary>The row expanded in place (the medium width's disclosure).</summary>
    public required IReadSignal<string?> Expanded { get; init; }
    public required Action<string?> Expand { get; init; }
    public required IReadSignal<PodcastPaneTab> PaneTab { get; init; }
    public required Action<PodcastPaneTab> SetPaneTab { get; init; }
    /// <summary>The episode on the deck and playing (its button shows Pause), else null.</summary>
    public required IReadSignal<string?> Playing { get; init; }
    /// <summary>Play the episode (Resume where it stopped), or pause it when it is the one playing.</summary>
    public required Action<string> Play { get; init; }
    public required Action<string> Open { get; init; }
    public required Action<string> ToggleSaved { get; init; }
    public required Action<string> PlayNext { get; init; }
    public required Action MarkFreshPlayed { get; init; }
    public required Action ClearFilter { get; init; }
    public required Action LoadMore { get; init; }
    public required Action ToggleFollow { get; init; }
    public required Action PlayTrailer { get; init; }
    public required Action<string> OpenUri { get; init; }
    public required Func<string, ContextMenuModel?> RowMenu { get; init; }
    public required Func<ContextMenuModel?> ShowMenu { get; init; }
    public required Action? Share { get; init; }
}

/// <summary>One chapter of an episode: where it starts, its length and how far the listener is through it (0..1).</summary>
public sealed record PodcastChapterVm(int Index, string Title, int StartMs, int EndMs, string Start, string Length, float Fill, bool Current, bool Played);

/// <summary>One line of the transcript, with the chapter heading it opens (null when it opens none).</summary>
public sealed record PodcastLineVm(int StartMs, string Time, string Text, string? Heading);

/// <summary>The episode page's snapshot. <see cref="State"/> picks the button (Play, Resume, Play again); <see cref="Next"/> is the card a played episode
/// shows above the chapters ("Up next in this show"); <see cref="More"/> the rows of "Next in this show".</summary>
public sealed record PodcastEpisodeView(
    PodcastLoad Load, PodcastEpisodeVm Episode, string ShowUri, string ShowTitle, string? ShowArtUrl, uint Tone,
    string Crumb, ImmutableArray<string> Meta, string PrimaryLabel, bool PrimaryAccent, ImmutableArray<PodcastMenuItem> Menu,
    ImmutableArray<PodcastChapterVm> Chapters, string MapHead, string MapSub, string MapTime, int DurationMs,
    ImmutableArray<PodcastLineVm> Transcript, PodcastLoad TranscriptLoad, string TranscriptSub,
    string About, int CommentCount, PodcastEpisodeVm? Next, string MoreSub, ImmutableArray<PodcastEpisodeVm> More,
    ImmutableArray<PodcastCardVm> Similar)
{
    /// <summary>The map head's order: true draws <see cref="MapSub"/> (quiet) before <see cref="MapHead"/> (strong): "Chapter 7 of 12 · " then the
    /// chapter's name; false draws the head first ("Played" then " · finished 9 Nov", "12 chapters" then " · select one to start there").</summary>
    public bool MapQuietFirst { get; init; }
    /// <summary>The still's corner clock ("37:00").</summary>
    public string StillTime { get; init; } = "";
    /// <summary>The episode's show-level facts the hero shows beside the title: the show is a video show and this episode carries video.</summary>
    public string VideoLabel { get; init; } = "";
    /// <summary>The section tabs' words and the transcript's empty line.</summary>
    public string NoChapters { get; init; } = "";
    public string NoTranscript { get; init; } = "";

    public static PodcastEpisodeView Empty { get; } = new(PodcastLoad.Pending,
        new PodcastEpisodeVm("", "", "", 0, false, "", 0, "", "", 0f, PodcastRowState.Unplayed, false, false, false, false, 0, false, null),
        "", "", null, 0u, "", [], "", true, [], [], "", "", "", 0, [], PodcastLoad.Pending, "", "", 0, null, "", [], []);
}

/// <summary>The props of <see cref="ViewKeys.PodcastEpisodePage"/>.</summary>
public sealed record PodcastEpisodePageProps
{
    public required string RouteKey { get; init; }
    /// <summary>The page scroller's scroll key (the shell's per-tab scope composed with the route).</summary>
    public required string ScrollKey { get; init; }
    public required IReadSignal<PodcastEpisodeView> View { get; init; }
    /// <summary>The chapter the position is in while the episode is under way (-1: not started, played, or no chapters): the map's thumb, the list's
    /// current row. Changes only when the position crosses a chapter.</summary>
    public required IReadSignal<int> CurrentChapter { get; init; }
    /// <summary>The transcript line the position is in (index into <see cref="PodcastEpisodeView.Transcript"/>; -1 none). Changes only on a new line.</summary>
    public required IReadSignal<int> CurrentLine { get; init; }
    /// <summary>The map's clock: "12:10 / 37:00" while under way, the length otherwise.</summary>
    public required IReadSignal<string> MapClock { get; init; }
    /// <summary>"21 min left" in the episode (empty unless under way).</summary>
    public required IReadSignal<string> Left { get; init; }
    /// <summary>" · 2 min left" in the current chapter (empty unless under way).</summary>
    public required IReadSignal<string> ChapterLeft { get; init; }
    public required IReadSignal<PodcastEpisodeTab> Tab { get; init; }
    public required Action<PodcastEpisodeTab> SetTab { get; init; }
    /// <summary>The playback position of THIS episode in ms (its resume point while it is not on the deck): the map, the chapters and the transcript bind it.</summary>
    public required IReadSignal<int> PositionMs { get; init; }
    public required IReadSignal<bool> Playing { get; init; }
    /// <summary>Whether the transcript keeps the current line in view; a scroll turns it off, "Back to current line" back on.</summary>
    public required IReadSignal<bool> Follow { get; init; }
    public required Action<bool> SetFollow { get; init; }
    public required Func<Signal<string>> Find { get; init; }
    public required Action Play { get; init; }
    public required Action<int> SeekTo { get; init; }
    public required Action ToggleSaved { get; init; }
    public required Action ToggleCompleted { get; init; }
    public required Action OpenShow { get; init; }
    public required Action<string> Open { get; init; }
    public required Action<string> PlayEpisode { get; init; }
    public required Action<string> PlayNext { get; init; }
    public required Action<string> OpenUri { get; init; }
    public required Func<ContextMenuModel?> Menu { get; init; }
    public required Action? Share { get; init; }
    /// <summary>The comments section, mounted by the host (null: none). The view places it.</summary>
    public required Func<FluentGpu.Dsl.Element>? Comments { get; init; }
}

/// <summary>One chapter of a book's contents. <see cref="Name"/> is "Chapter N" when the publisher's title is empty or generic ("Track 5", "01");
/// <see cref="Matter"/> marks front and back matter (credits, introduction, epilogue, acknowledgements), set quieter. <see cref="Fill"/> 0..1.</summary>
public sealed record AudiobookChapterVm(int Index, string Number, string Name, bool Matter, int StartMs, int LengthMs, string StartsAt, string Length,
    string Left, float Fill, bool Current, bool Done, string EpisodeId);

/// <summary>The audiobook page's snapshot. Credits are separate lists (authors, narrators, publishers) and stay empty when the data lacks them: a row the
/// data cannot fill is left off. <see cref="Price"/> / <see cref="ListPrice"/> are set when the book is for sale (Buy audiobook) rather than included.</summary>
public sealed record AudiobookView(
    PodcastLoad Load, string Uri, string Title, string Subtitle, string? ArtUrl, uint Tone,
    ImmutableArray<PodcastCardVm> Authors, ImmutableArray<PodcastCardVm> Narrators, ImmutableArray<string> Publishers, string Edition,
    ImmutableArray<string> Genres, int RatingX100, string Ratings, string Released, string Language, string Length, string ChapterCount,
    AudiobookState State, float Progress, string Percent, string BookLeft, string ChapterLine, string FinishedLine,
    string PrimaryLabel, bool Included, string PremiumLine, string? Price, string? ListPrice, bool InLibrary, string Speed,
    ImmutableArray<AudiobookChapterVm> Chapters, bool NamesMissing, string ContentsSub, string LineHead, string LineSub,
    string? SampleId, string SampleLength, ImmutableArray<string> About, ImmutableArray<PodcastFact> Facts, ImmutableArray<PodcastCardVm> Similar,
    string SimilarTitle)
{
    public static AudiobookView Empty { get; } = new(PodcastLoad.Pending, "", "", "", null, 0u, [], [], [], "", [], 0, "", "", "", "", "",
        AudiobookState.NotStarted, 0f, "", "", "", "", "", true, "", null, null, false, "", [], false, "", "", "", null, "", [], [], [], "");
}

/// <summary>The props of <see cref="ViewKeys.AudiobookPage"/>.</summary>
public sealed record AudiobookPageProps
{
    public required string RouteKey { get; init; }
    public required IReadSignal<AudiobookView> View { get; init; }
    /// <summary>The book's position in ms from its start (the line's thumb, the current chapter's bar, the percentage).</summary>
    public required IReadSignal<int> BookPositionMs { get; init; }
    public required IReadSignal<bool> Playing { get; init; }
    /// <summary>Start, Continue or Listen again; pauses while the book plays.</summary>
    public required Action Play { get; init; }
    public required Action PlaySample { get; init; }
    public required Action<int> PlayChapter { get; init; }
    public required Action ToggleLibrary { get; init; }
    public required ImmutableArray<float> Speeds { get; init; }
    public required IReadSignal<float> Speed { get; init; }
    public required Action<float> SetSpeed { get; init; }
    public required Action SleepTimer { get; init; }
    /// <summary>Open a link in the browser (the hours page, the top-up, the purchase).</summary>
    public required Action CheckHours { get; init; }
    public required Action TopUp { get; init; }
    public required Action? Buy { get; init; }
    public required Action<string> OpenUri { get; init; }
    public required Func<ContextMenuModel?> Menu { get; init; }
    public required Action? Share { get; init; }
}
