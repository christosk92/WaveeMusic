// ── Theming/Contracts/LibraryPaneProps.cs — the props of the library's album pane and Your Episodes page (themes plan 6.0, T10c1) ──────────────
//
// The core hosts (`Album.Pane`, `SavedEpisodesReader`) keep the selection, the demands, the readiness verdicts, the keys and every verb; the views draw
// the pane. The pane's models are untouched: `AlbumPaneReadiness` decides the state, `Album.Pane` demands the rows and the credits. Every record
// compares by reference (the host rebuilds one per render). Every member is read-only (WVTH006).

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The "Also by ... in your library" strip's model: the artist's name and one tile per other saved album (capped by the host).</summary>
public sealed record LibraryAlsoByModel(string ArtistName, IReadOnlyList<LibraryAlsoByTile> Tiles)
{
    /// <summary>The strip's heading in place of "Also by {artist} in your library" (null: that one). "On this PC"'s artist pane heads the artist's own
    /// albums "Albums" (contract addition, #240).</summary>
    public string? Heading { get; init; }
}

/// <summary>One "Also by" tile: the album's slot (its key) and the shared media surface's data without the plate height (the structure states it).</summary>
public sealed record LibraryAlsoByTile(int Slot, Controls.CardData Card);

/// <summary>The props of <see cref="ViewKeys.LibraryAlbumPane"/>.</summary>
public sealed record LibraryAlbumPaneProps
{
    /// <summary>The selection names no album: the pane draws nothing (the page owns the no-selection vacancy).</summary>
    public bool Vacant { get; init; }

    /// <summary>The pane's readiness (<see cref="AlbumPaneState"/>): only <see cref="AlbumPaneState.Header"/> paints the header skeleton.</summary>
    public required AlbumPaneState State { get; init; }

    /// <summary>The key of the keyed child for this state and selection ("pane:N" or "paneskel:N"): a key change is the crossfade.</summary>
    public required string BodyKey { get; init; }

    /// <summary>The cover art url.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>"ALBUM · 2013".</summary>
    public string Eyebrow { get; init; } = "";

    /// <summary>The album's title.</summary>
    public string Title { get; init; } = "";

    /// <summary>"13 songs · 45 min · saved 2024".</summary>
    public string Meta { get; init; } = "";

    /// <summary>The billed artists the attribution line draws (the structure builds the line); null while the header is a skeleton.</summary>
    public IReadOnlyList<Wavee.Controls.Face>? Artists { get; init; }

    /// <summary>The album's uri text (the save button's subject).</summary>
    public string Uri { get; init; } = "";

    /// <summary>The save button's key.</summary>
    public string SaveKey { get; init; } = "";

    /// <summary>The subject can be saved to the library (the save heart shows). False for "On this PC"'s albums and artists: names from the files' tags,
    /// nothing a library can hold (contract addition, #240).</summary>
    public bool Saveable { get; init; } = true;

    /// <summary>The subject has a page to open (the header opens it, the "Open album" link stands). False for "On this PC"'s artists, which have none
    /// (contract addition, #240).</summary>
    public bool Openable { get; init; } = true;

    /// <summary>Open the album's page.</summary>
    public required Action Open { get; init; }

    /// <summary>Play or toggle the album.</summary>
    public required Action Play { get; init; }

    /// <summary>Shuffle the album.</summary>
    public required Action Shuffle { get; init; }

    /// <summary>The ⋯ menu, built lazily at open.</summary>
    public required Func<ContextMenuModel?> More { get; init; }

    /// <summary>The unnamed-credit notice strip (a mount of `Detail.Notice`; zero-height while there is nothing to say); null while the header is a skeleton.</summary>
    public Element? Notice { get; init; }

    /// <summary>The Retry of the failed strip.</summary>
    public required Action Retry { get; init; }

    /// <summary>The rows region's loading state: one of the host's two cached instances (the region's thunks close over the instance it is handed).</summary>
    public required Loadable<int> Region { get; init; }

    /// <summary>The counted shimmer's row count (never zero).</summary>
    public required int ShimRows { get; init; }

    /// <summary>The shimmer row's height (the table's row height for the row style at the current density).</summary>
    public required float RowHeight { get; init; }

    /// <summary>The lanes the embedded table will lay out (<see cref="Track.EmbeddedColumns"/>: the row style's own set, with a Classic compilation's Artist
    /// lane): the counted shimmer and its header shim stand on these, so the rows land where the bars stood (tuned Classic X2). Modern's set when unset.</summary>
    public Track.ColumnSet Columns { get; init; } = Track.EmbeddedColumns(classic: false, showTrackArtist: false);

    /// <summary>The embedded table over this album; the argument is the table's trailing band (the "Also by" strip), or null for none.</summary>
    public required Func<Func<Element>?, Element> Table { get; init; }

    /// <summary>The "Also by" model, read inside the table host's render (it reads its scope signals itself); null when there is nothing to show.</summary>
    public required Func<LibraryAlsoByModel?> AlsoBy { get; init; }

    /// <inheritdoc />
    public bool Equals(LibraryAlbumPaneProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.LibrarySavedEpisodes"/>.</summary>
public sealed record LibrarySavedEpisodesProps
{
    /// <summary>The listen-later playlist has not been discovered yet.</summary>
    public required bool Pending { get; init; }

    /// <summary>Discovery failed.</summary>
    public required bool Failed { get; init; }

    /// <summary>The content once discovery answered: the embedded table over the listen-later playlist, or the quiet "empty" line.</summary>
    public required Func<Element> Content { get; init; }

    /// <summary>The failed arm (the quiet failure line with its Retry).</summary>
    public required Func<Element> OnFailed { get; init; }

    /// <summary>The listen-later episodes as plain rows (a reactive read), for a structure that draws its own feed instead of <see cref="Content"/>'s table (null: only the table). Contract
    /// addition after 1.38.</summary>
    public Func<LibraryEpisodesFeed>? Feed { get; init; }

    /// <summary>Play (or toggle) the episode with this uri; open its page. Null with no <see cref="Feed"/>.</summary>
    public Action<string>? PlayEpisode { get; init; }

    /// <inheritdoc cref="PlayEpisode"/>
    public Action<string, string>? OpenEpisode { get; init; }

    /// <inheritdoc />
    public bool Equals(LibrarySavedEpisodesProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The listen-later episodes, newest published first, and their total length.</summary>
public sealed record LibraryEpisodesFeed(IReadOnlyList<LibraryEpisodeRow> Rows, long TotalMs);

/// <summary>One saved episode: its uri, title, show, cover url ("" for none), the length and left-to-go words (empty when unknown), where it stands (<see cref="Status"/>: 0 unplayed, 1 in
/// progress, 2 played), whether it is the playing one, and the date bucket of its release (<see cref="Bucket"/>: 0 today, 1 this week, 2 earlier).</summary>
public readonly record struct LibraryEpisodeRow(string Uri, string Title, string Show, string Cover, string Length, string Left, byte Status, byte Bucket, bool Playing);
