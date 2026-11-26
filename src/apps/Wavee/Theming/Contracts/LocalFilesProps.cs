// ── Theming/Contracts/LocalFilesProps.cs — the props of the Local Files list's own views (#240 UX S4, S5, S6) ──────────────────────────
//
// The Local Files page is the playlist page's local arm; these are the pieces of it no other list has. The core (`Entities\LocalFiles.List.cs`)
// decides WHEN each shows and words it from `LocalLibrary`'s facts; a structure only draws what the props carry and calls the verbs it is handed.
//
//   LocalFiles.Empty    the list's body while there is no music folder and nothing in the list (S4): the title, the line, "Add my Music folder"
//                       (only while the user's Music folder exists and is not added) with its path, "Choose a folder…", the drop zone's words.
//   LocalFiles.Scan     the first scan's line under the page head's facts (S4): "Reading your music · 1,930 of 4,812 · 212 albums so far", the
//                       time left beside it, a determinate bar (indeterminate while the first listing has no total).
//   LocalFiles.Pill     the "4 new songs · 1 moved" pill over the list (S5), mounted while the list is scrolled away from a change; a click goes to
//                       the top of the list.
//   LocalFiles.Review   the body of the missing songs' dialog (S6): one row per missing file (title · artist, the last-known path, "Not found since
//                       12 Nov · removed on 11 Jan", Locate…), the note on what Locate does, the keep-days footer.
//
//   LocalFiles.Find     "Find music on this PC" (#240, 0dm): the user's Music and Downloads folders with their counts and a tick each (Music ticked,
//                       Downloads not), "Add selected" and "Choose another folder…". The empty state carries it as its `Find` element in place of
//                       its buttons; Settings' empty Music folders group shows it too (without "Choose another folder…": its header has "Add a folder…").
//
// Additive (contract 2.x minor): five keys, their props here.

using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LocalFilesEmpty"/>.</summary>
public sealed record LocalFilesEmptyProps
{
    /// <summary>"Your music on this PC".</summary>
    public required string Title { get; init; }

    /// <summary>"Add a folder and Wavee plays everything in it, offline too. …"</summary>
    public required string Body { get; init; }

    /// <summary>"Add my Music folder", or "" while the user's Music folder is missing or already a music folder (the button is absent then).</summary>
    public string AddMusicLabel { get; init; } = "";

    /// <summary>Adds the user's Music folder; null with <see cref="AddMusicLabel"/> empty.</summary>
    public Action? AddMusic { get; init; }

    /// <summary>"Your Music folder is C:\Users\…\Music" under the buttons, or "".</summary>
    public string MusicFolderCaption { get; init; } = "";

    /// <summary>The path inside <see cref="MusicFolderCaption"/> (a structure may set it in its monospace face), or "".</summary>
    public string MusicFolderPath { get; init; } = "";

    /// <summary>"Choose a folder…".</summary>
    public required string ChooseLabel { get; init; }

    /// <summary>The "Find music on this PC" card (<see cref="ViewKeys.LocalFilesFind"/>, already resolved), drawn where the buttons and the caption
    /// stand when set; null keeps the buttons (a library whose folders hold no song yet).</summary>
    public Element? Find { get; init; }

    /// <summary>Opens the folder picker and adds the pick.</summary>
    public required Action Choose { get; init; }

    /// <summary>The drop zone's line ("Or drop files here") and the one under it ("Folders or single songs"). The drop itself is the page's.</summary>
    public required string DropTitle { get; init; }

    public required string DropSub { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.LocalFilesScan"/>.</summary>
public sealed record LocalFilesScanProps
{
    /// <summary>"Reading your music · 1,930 of 4,812 · 212 albums so far", or "Looking for your music…" before the total is known.</summary>
    public required string Text { get; init; }

    /// <summary>"about 40 s left" beside the line, or "".</summary>
    public string Aside { get; init; } = "";

    /// <summary>The bar: 0-1, or -1 while there is no total (an indeterminate bar).</summary>
    public float Fraction { get; init; } = -1f;

    /// <summary>The bar's accessible name ("Reading your music").</summary>
    public required string Label { get; init; }

    /// <summary>The width the page head gives its words (the line and the bar take at most this), or NaN where it states none.</summary>
    public float Width { get; init; } = float.NaN;

    /// <summary>The head draws the line in its one-line facts slot (WinUI's hero, board FirstScan: the line stands where the facts line stands): the words
    /// never wrap (they trim), the time left stays beside them, and the bar hangs under the line without growing the slot.</summary>
    public bool OneLine { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.LocalFilesPill"/>.</summary>
public sealed record LocalFilesPillProps
{
    /// <summary>The pill is up: the list is scrolled away from a change it has not shown yet.</summary>
    public required bool Shown { get; init; }

    /// <summary>"4 new songs · 1 moved" (the last words stay while the pill leaves).</summary>
    public required string Text { get; init; }

    /// <summary>The pill's accessible name ("Go to the top of the list").</summary>
    public required string Label { get; init; }

    /// <summary>Goes to the top of the list (the pill then leaves).</summary>
    public required Action Go { get; init; }

    /// <summary>The air above the pill: the band the list pins at its top while scrolled (the hero list's sticky bar and chrome), 0 where none.</summary>
    public float Top { get; init; }
}

/// <summary>One missing file in <see cref="LocalFilesReviewProps.Rows"/>.</summary>
/// <param name="Id">A stable key (the file's id).</param>
/// <param name="Title">The song's title (its file name when untagged).</param>
/// <param name="Artist">Its artists, or "".</param>
/// <param name="Path">Where it was last seen.</param>
/// <param name="Line">"Not found since 12 Nov · removed on 11 Jan".</param>
/// <param name="LocateLabel">"Locate…".</param>
/// <param name="Locate">Opens the file picker for it (the pick is re-paired with this row).</param>
/// <param name="RemoveLabel">"Remove".</param>
/// <param name="Remove">Drops it from the library (its row leaves the list).</param>
public sealed record LocalFilesReviewRow(string Id, string Title, string Artist, string Path, string Line, string LocateLabel, Action Locate,
    string RemoveLabel, Action Remove);

/// <summary>The props of <see cref="ViewKeys.LocalFilesReview"/> (the dialog's title and its Close are the dialog's own).</summary>
public sealed record LocalFilesReviewProps
{
    /// <summary>"Wavee can’t find these files where it last saw them."</summary>
    public required string Body { get; init; }

    /// <summary>The missing files, the longest gone first.</summary>
    public required IReadOnlyList<LocalFilesReviewRow> Rows { get; init; }

    /// <summary>What Locate does ("Point Locate at the file …").</summary>
    public required string Note { get; init; }

    /// <summary>"Missing songs are removed after 60 days unless a playlist uses them".</summary>
    public required string Footer { get; init; }

    /// <summary>"Nothing is missing any more." (the list once every file is back).</summary>
    public required string Empty { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.LocalFilesFind"/>: "Find music on this PC".</summary>
public sealed record LocalFindProps
{
    /// <summary>"Find music on this PC".</summary>
    public required string Title { get; init; }

    /// <summary>"Wavee counted the songs in your usual folders. Nothing is added until you choose."</summary>
    public required string Line { get; init; }

    /// <summary>One row per folder looked at (Music, Downloads; none for a folder that does not exist).</summary>
    public required IReadOnlyList<LocalFindRow> Rows { get; init; }

    /// <summary>"Add selected": adds the ticked folders that hold songs.</summary>
    public required string AddLabel { get; init; }

    public required Action Add { get; init; }

    /// <summary>A ticked folder holds songs (or is still being counted): "Add selected" is enabled.</summary>
    public bool CanAdd { get; init; }

    /// <summary>"Choose another folder…", or "" where the surface offers that verb elsewhere (Settings' header): no button then.</summary>
    public string ChooseLabel { get; init; } = "";

    public Action? Choose { get; init; }

    /// <summary>Drawn on its own plate (the empty page); false inside a surface that is already a card (Settings' Music folders group).</summary>
    public bool Framed { get; init; } = true;
}

/// <summary>One folder of the "Find music on this PC" card.</summary>
public sealed record LocalFindRow
{
    /// <summary>"music", "downloads": the row's key part.</summary>
    public required string Key { get; init; }

    /// <summary>"Music", "Downloads".</summary>
    public required string Name { get; init; }

    /// <summary>The folder's path (its tooltip, and the line under its name).</summary>
    public required string Path { get; init; }

    /// <summary>"1,204 songs (238 in OneDrive)", "37 songs · 12 videos", "Counting…" while the count runs, "No songs here".</summary>
    public required string Count { get; init; }

    /// <summary>The tick (the core's; the checkbox writes it).</summary>
    public required Signal<bool> Ticked { get; init; }

    /// <summary>The folder holds no song: the row is greyed and cannot be ticked.</summary>
    public bool Empty { get; init; }

    /// <summary>The count is still running ("Counting…" holds the count's line).</summary>
    public bool Counting { get; init; }
}
