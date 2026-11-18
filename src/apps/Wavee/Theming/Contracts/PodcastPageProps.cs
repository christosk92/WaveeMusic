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
    int ChapterCount, bool HasTranscript, string? ArtUrl)
{
    /// <summary>The details pane's eyebrow ("Episode 103 · 12 Nov 2026", "Part 3 · 4 Mar 2025"); empty when nothing is known.</summary>
    public string Eyebrow { get; init; } = "";
    /// <summary>The date with its year ("12 Nov 2026"), for the pane's meta line.</summary>
    public string LongDate { get; init; } = "";
    /// <summary>The description as HTML when the provider sent it ("" = only <see cref="Description"/>); render it with the house rich text.</summary>
    public string DescriptionHtml { get; init; } = "";
    /// <summary>The row is filtered out (a filter or a search): it stays in the list, mounted and hidden, so a filter switch never remounts the rows.</summary>
    public bool Hidden { get; init; }
}

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
public sealed record PodcastGroup(string Key, string Title, int Count, bool Fresh, ImmutableArray<int> Rows)
{
    /// <summary><see cref="Count"/> as the header shows it ("" = none).</summary>
    public string CountText { get; init; } = "";
    /// <summary>No row of the group passes the filter: the group stays, hidden.</summary>
    public bool Hidden { get; init; }
}

/// <summary>One cell of the jump list: a year and how many episodes it holds.</summary>
public sealed record PodcastYear(int Year, string Label, string Count)
{
    /// <summary>How many episodes the year holds (the group header's count).</summary>
    public int Episodes { get; init; }
}

/// <summary>A filter of the SelectorBar with its count (0 shows no count).</summary>
public sealed record PodcastFilterVm(PodcastFilter Filter, string Label, int Count)
{
    /// <summary><see cref="Count"/> as the SelectorBar shows it ("" = none).</summary>
    public string CountText { get; init; } = "";
}

/// <summary>How far through the show the listener is, over the episodes known so far (Classic's listened bar): played, in progress, still to go
/// (unplayed, not new) and new since the last visit. <see cref="Text"/> is "39 played · 2 in progress · 62 to go", <see cref="NewText"/> "1 new"
/// ("" when nothing is new).</summary>
public sealed record PodcastLedger(int Played, int InProgress, int ToGo, int New, string Text, string NewText)
{
    public static PodcastLedger None { get; } = new(0, 0, 0, 0, "", "");
}

/// <summary>A "More like this" card: a show or a book.</summary>
public sealed record PodcastCardVm(string Uri, string Title, string Subtitle, string? ArtUrl);

/// <summary>A fact of the About list ("Publisher · Halden Audio").</summary>
public sealed record PodcastFact(string Label, string Value);

/// <summary>The show's identity and the About facts. <see cref="Tone"/> is the cover's dark colour (the wash), 0 = none. <see cref="RatingX100"/> 0 = no
/// public rating; <see cref="Rating"/> is its text ("4.8"), <see cref="RatingCount"/> the hero's "(12,431)" and <see cref="Ratings"/> the card's
/// "12,431 ratings". <see cref="CanRate"/>: the listener may rate the show (the card's Rate button).</summary>
public sealed record PodcastShowHead(
    string Uri, string Title, string? ArtUrl, uint Tone, string Kind, ImmutableArray<string> Topics, bool Video, bool Serial,
    string Publisher, int RatingX100, string Rating, string RatingCount, string Ratings, bool CanRate, string Cadence, string Count, string About,
    bool Following, string? TrailerId, string TrailerSub, ImmutableArray<PodcastFact> Facts)
{
    /// <summary>The About text as HTML when the provider sent it ("" = only <see cref="About"/>).</summary>
    public string AboutHtml { get; init; } = "";
}

/// <summary>The details pane's chapters of the selected episode: up to five around the current one (<see cref="Shown"/>), how many there are in all
/// (<see cref="Total"/>; 0 with <see cref="Pending"/> false = none), and whether they are still on their way.</summary>
public sealed record PodcastPaneChapters(int Total, ImmutableArray<PodcastChapterVm> Shown, bool Pending)
{
    public static PodcastPaneChapters None { get; } = new(0, [], false);
    public static PodcastPaneChapters Loading { get; } = new(0, [], true);
}

/// <summary>What one item of the show page's episode LIST is (podcast pages list model): a group's header, an episode row, the "Load more" foot or
/// the "No episodes match" line. A theme virtualises the list over <see cref="PodcastShowView.Items"/> (only the items on screen are mounted, so a
/// show of 500 episodes scrolls and filters at the cost of 14), and puts its own prefix items (the hero, the toolbar) before them.</summary>
public enum PodcastItemKind : byte { Group, Row, More, Empty }

/// <summary>One item of <see cref="PodcastShowView.Items"/>: its kind, its group (an index into <see cref="PodcastShowView.Groups"/>, -1 for the foot
/// and the empty line) and, for a row, its episode (an index into <see cref="PodcastShowView.Episodes"/>, else -1). <see cref="First"/>: the row opens
/// its group (no rule above it).</summary>
public readonly record struct PodcastListItem(PodcastItemKind Kind, int Group, int Episode, bool First = false);

/// <summary>The show page's snapshot.</summary>
public sealed record PodcastShowView(
    PodcastLoad Load, PodcastShowHead Head, PodcastSmart? Smart,
    ImmutableArray<PodcastEpisodeVm> Episodes, ImmutableArray<PodcastGroup> Groups, ImmutableArray<PodcastFilterVm> Filters,
    ImmutableArray<PodcastYear> Years, bool HasMore, bool Filtered, ImmutableArray<PodcastCardVm> Similar)
{
    /// <summary>How many rows pass the filter and the search (0 = "No episodes match").</summary>
    public int ShownCount { get; init; }
    /// <summary>The episode list, flattened for a virtualised list: per group its header then its rows, then the foot ("Load more") or the empty line.
    /// <see cref="Episodes"/> and <see cref="Groups"/> hold only what passes the filter and the search.</summary>
    public ImmutableArray<PodcastListItem> Items { get; init; } = [];
    /// <summary>The listened ledger (whatever the filter: it counts every known episode). Reused while it holds, like the head.</summary>
    public PodcastLedger Ledger { get; init; } = PodcastLedger.None;

    /// <summary>The empty snapshot a page shows while nothing is known (the skeleton is derived from the loaded layout over it).</summary>
    public static PodcastShowView Empty { get; } = new(PodcastLoad.Pending,
        new PodcastShowHead("", "", null, 0u, "", [], false, false, "", 0, "", "", "", false, "", "", "", false, null, "", []),
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
    /// <summary>The smart action's own click (what <see cref="PodcastShowView.Smart"/> names: Resume, Play newest, Play part N, Play latest again from the
    /// start), or pause while that episode plays.</summary>
    public required Action PlaySmart { get; init; }
    /// <summary>The selected episode's chapters for the details pane.</summary>
    public required IReadSignal<PodcastPaneChapters> PaneChapters { get; init; }
    /// <summary>Rate the show (null: the listener cannot).</summary>
    public required Action? Rate { get; init; }
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
    /// <summary>The eyebrow's kind word ("Podcast"): its hub. Null: the word is plain text.</summary>
    public Action? OpenKind { get; init; }
    /// <summary>"More like this": a card's context menu and its play button (null: none).</summary>
    public Func<string, ContextMenuModel?>? CardMenu { get; init; }
    public Action<string>? PlayCard { get; init; }
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
    /// <summary>The chapters / the comments have not answered yet: a tab keeps its count's room (no tab moves when the count lands).</summary>
    public bool ChaptersPending { get; init; }
    public bool CommentsPending { get; init; }

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
    /// <summary>The episode on the deck and playing (any episode; the rows' Pause), else null.</summary>
    public required IReadSignal<string?> OnDeck { get; init; }
    /// <summary>Save or unsave another episode (a row's glyph) and that row's menu.</summary>
    public required Action<string> ToggleSavedEpisode { get; init; }
    public required Func<string, ContextMenuModel?> RowMenu { get; init; }
    /// <summary>A "More like this" card's menu and its play (the card's show, by uri).</summary>
    public required Func<string, ContextMenuModel?> CardMenu { get; init; }
    public required Action<string> PlayCard { get; init; }
}

/// <summary>One chapter of a book's contents. <see cref="Name"/> is "Chapter N" when the publisher's title is empty or generic ("Track 5", "01");
/// <see cref="Matter"/> marks front and back matter (credits, introduction, epilogue, acknowledgements), set quieter. <see cref="StartsAt"/> is where the
/// chapter starts in the book (the contents page's "page number"); <see cref="Done"/> the chapter is finished (at rest).</summary>
public sealed record AudiobookChapterVm(int Index, string Number, string Name, bool Matter, int StartMs, int LengthMs, string StartsAt, string Length,
    bool Done, string Label);

/// <summary>A credited person (an author, a narrator). <see cref="Uri"/> empty: no page to open (the name is drawn, not linked).</summary>
public sealed record AudiobookPersonVm(string Name, string Uri);

/// <summary>Where the listener is in the book, LIVE (it follows the playing chapter's clock). <see cref="Current"/> is the chapter the primary continues
/// (-1 when the book is not started or finished); <see cref="CurrentFill"/> how far into it (0..1); <see cref="Fraction"/> the whole book (0..1).
/// Texts: <see cref="BookLeft"/> "48% · 9 hr 2 min left", <see cref="BookLeftLong"/> the same "… left in the book", <see cref="InLeft"/>
/// "3 hr 35 min in · 9 hr left", <see cref="CurrentLeft"/> "12 min left" (the current chapter's own).</summary>
public readonly record struct AudiobookPlace(int PositionMs, float Fraction, int Current, float CurrentFill, string BookLeft, string BookLeftLong,
    string InLeft, string CurrentLeft);

/// <summary>The audiobook page's snapshot. Credits are separate lists (authors, narrators) and the facts are labelled rows; a row the data cannot fill is
/// left off. <see cref="Included"/> false = the book is for sale: <see cref="Price"/> / <see cref="ListPrice"/> and <see cref="BuyLabel"/> replace the
/// Premium line. The hero's label: <see cref="UpTitle"/> (semibold, "Chapter 9 of 19 · " in the secondary ink when in progress) + <see cref="UpName"/>,
/// <see cref="UpSub"/> under it (the place binds over it while in progress).</summary>
public sealed record AudiobookView(
    PodcastLoad Load, string Uri, string Title, string Subtitle, string? ArtUrl, uint Tone,
    string Kind, ImmutableArray<string> Genres, bool Included, string IncludedTag,
    ImmutableArray<AudiobookPersonVm> Authors, ImmutableArray<AudiobookPersonVm> Narrators, string ByLabel, string NarratedByLabel,
    string Rating, string RatingCount, ImmutableArray<string> Meta,
    AudiobookState State, string PrimaryLabel, bool PrimaryAccent, string UpTitle, string UpName, string UpSub, string BarTitle,
    string? SampleId, string SampleButton, string SampleTitle, string SampleSub,
    bool InLibrary, string LibraryLabel,
    string PremiumLine, string CheckHoursLabel, string TopUpLabel, string Price, string ListPrice, string BuyLabel,
    ImmutableArray<AudiobookChapterVm> Chapters, bool NamesMissing, string ContentsTitle, string ContentsSub, string StartsAtLabel,
    string LineTitle, string LineSub, string LineRight,
    string AboutTitle, ImmutableArray<PodcastFact> Facts,
    string RatingsLine, string RateLabel, string FinishedTitle, string FinishedSub,
    ImmutableArray<PodcastCardVm> Similar, string SimilarTitle, string SeeAllLabel)
{
    public static AudiobookView Empty { get; } = new(PodcastLoad.Pending, "", "", "", null, 0u, "", [], true, "", [], [], "", "", "", "", [],
        AudiobookState.NotStarted, "", true, "", "", "", "", null, "", "", "", false, "", "", "", "", "", "", "",
        [], false, "", "", "", "", "", "", "", [], "", "", "", "", [], "", "");

    /// <summary>The chapter the primary continues (in progress), else -1.</summary>
    public int Current { get; init; } = -1;

    /// <summary>The route key the kind word ("Audiobook") opens (the Browse category of audiobooks), or null when Wavee routes none: the word is then
    /// drawn, not linked.</summary>
    public string? KindRoute { get; init; }

    /// <summary>About this book as the provider's HTML (paragraphs, bold, links), or plain text; rendered by the house rich-text renderer.</summary>
    public string AboutHtml { get; init; } = "";

    /// <summary>A representative book the loading state is derived from (its words are never shown: the skeleton fills them).</summary>
    public static AudiobookView Placeholder { get; } = Empty with
    {
        Title = "Placeholder title", Subtitle = "A placeholder subtitle of a typical length", Kind = "Audiobook", Genres = ["Genre", "Genre"],
        Authors = [new("Author name", "")], Narrators = [new("Narrator name", "")], ByLabel = "By", NarratedByLabel = "Narrated by",
        Rating = "4.5", RatingCount = "(1,000)", Meta = ["8 hr 30 min", "19 chapters", "2024"], PrimaryLabel = "Start listening",
        UpTitle = "8 hr 30 min", UpSub = "19 chapters", LibraryLabel = "Add to Your Library", PremiumLine = "Included in Premium, 12 h a month",
        CheckHoursLabel = "Check hours left", TopUpLabel = "Top up hours", ContentsTitle = "Contents", ContentsSub = "19 chapters, 8 hr 30 min",
        StartsAtLabel = "starts at", LineTitle = "The whole book", LineSub = "select one to start there", LineRight = "8 hr 30 min",
        AboutTitle = "About this book", AboutHtml = "A placeholder paragraph about the book, long enough to wrap over three or four lines in the card beside the contents page.",
        Facts = [new("Author", "Author name"), new("Narrator", "Narrator name"), new("Released", "1 January 2024"), new("Length", "8 hr 30 min")],
        Chapters = [.. Enumerable.Range(0, 12).Select(static i => new AudiobookChapterVm(i, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Chapter title", false, i * 1_800_000, 1_800_000, "0:00:00", "30 min", false, ""))],
    };
}

/// <summary>The props of <see cref="ViewKeys.AudiobookPage"/>.</summary>
public sealed record AudiobookPageProps
{
    public required string RouteKey { get; init; }
    public required IReadSignal<AudiobookView> View { get; init; }
    /// <summary>Where the listener is, live (the hero's bar and words, the line's thumb, the current row's time left).</summary>
    public required IReadSignal<AudiobookPlace> Place { get; init; }
    /// <summary>The book is on the deck and playing (the primary reads Pause).</summary>
    public required IReadSignal<bool> Playing { get; init; }
    /// <summary>Start, Continue or Listen again; pauses while the book plays.</summary>
    public required Action Play { get; init; }
    public required Action PlaySample { get; init; }
    /// <summary>Play chapter i (a contents row, a segment of the line) from where it stopped, or from its start when finished.</summary>
    public required Action<int> PlayChapter { get; init; }
    public required Action ToggleLibrary { get; init; }
    /// <summary>The speed button's word ("1×", "1.2×"), live.</summary>
    public required IReadSignal<string> SpeedLabel { get; init; }
    /// <summary>The speed menu's items (0.8×, 1×, 1.2×, 1.5×, 2×; the current one checked), built when it opens.</summary>
    public required Func<ImmutableArray<PodcastMenuItem>> SpeedMenu { get; init; }
    /// <summary>The sleep timer's word ("Sleep timer", "Sleep · 14 min", "Sleep · end of chapter"), live.</summary>
    public required IReadSignal<string> SleepLabel { get; init; }
    public required IReadSignal<bool> SleepOn { get; init; }
    /// <summary>The sleep timer's items (15, 30, 45, 60 min, end of chapter, off), built when it opens.</summary>
    public required Func<ImmutableArray<PodcastMenuItem>> SleepMenu { get; init; }
    /// <summary>Open the hours page / the top-up / the purchase in the browser.</summary>
    public required Action CheckHours { get; init; }
    public required Action TopUp { get; init; }
    public required Action? Buy { get; init; }
    public required Action? Download { get; init; }
    /// <summary>Open a uri (an author, a similar book).</summary>
    public required Action<string> OpenUri { get; init; }
    public required Func<ContextMenuModel?> Menu { get; init; }
    public required Action? Share { get; init; }
    /// <summary>Navigate to an in-app route key (the kind word's hub, a link inside About).</summary>
    public required Action<string> OpenRoute { get; init; }
    /// <summary>A similar book's context menu (the container menu of a show) and its play verb, for the house cards.</summary>
    public required Func<string, ContextMenuModel?> SimilarMenu { get; init; }
    public required Action<string> PlaySimilar { get; init; }
    /// <summary>Rate the book (1..5 stars), null when the account cannot rate it.</summary>
    public required Action<int>? Rate { get; init; }
}
