// ── Theming/Contracts/LibraryPaneProps.cs — the props of the library's album pane, show pane and Your Episodes page (themes plan 6.0, T10c1) ──
//
// The core hosts (`Album.Pane`, `Show.Pane`, `SavedEpisodesReader`) keep the selection, the demands, the readiness verdicts, the keys and every verb; the
// views draw the pane. The pane's models are untouched: `AlbumPaneReadiness` decides the state, `Album.Pane` demands the rows and the credits, `Show.Pane`
// keeps the memo pair. Every record compares by reference (the host rebuilds one per render). Every member is read-only (WVTH006).

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The "Also by ... in your library" strip's model: the artist's name and one tile per other saved album (capped by the host).</summary>
public sealed record LibraryAlsoByModel(string ArtistName, IReadOnlyList<LibraryAlsoByTile> Tiles);

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

    /// <summary>The billed-artist line (a mount of `Album.ArtistLine`); null while the header is a skeleton.</summary>
    public Element? Attribution { get; init; }

    /// <summary>The album's uri text (the save button's subject).</summary>
    public string Uri { get; init; } = "";

    /// <summary>The save button's key.</summary>
    public string SaveKey { get; init; } = "";

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

    /// <summary>The shimmer row's height (the table's row height at the current density).</summary>
    public required float RowHeight { get; init; }

    /// <summary>The embedded table over this album; the argument is the table's trailing band (the "Also by" strip), or null for none.</summary>
    public required Func<Func<Element>?, Element> Table { get; init; }

    /// <summary>The "Also by" model, read inside the table host's render (it reads its scope signals itself); null when there is nothing to show.</summary>
    public required Func<LibraryAlsoByModel?> AlsoBy { get; init; }

    /// <inheritdoc />
    public bool Equals(LibraryAlbumPaneProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.LibraryShowPane"/>.</summary>
public sealed record LibraryShowPaneProps
{
    /// <summary>The header may paint (the show's title is known): otherwise the header skeleton.</summary>
    public required bool HeaderReady { get; init; }

    /// <summary>The show's cover art url.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>The header's eyebrow.</summary>
    public string Eyebrow { get; init; } = "";

    /// <summary>The show's title.</summary>
    public string Title { get; init; } = "";

    /// <summary>The publisher's name (the attribution line).</summary>
    public string Publisher { get; init; } = "";

    /// <summary>The header's meta line.</summary>
    public string Meta { get; init; } = "";

    /// <summary>The show's uri text.</summary>
    public string Uri { get; init; } = "";

    /// <summary>Open the show's page.</summary>
    public required Action Open { get; init; }

    /// <summary>The primary slot: a core component keyed "showpane:primary", built by the host over the accent the view hands it (the app accent: a live read); null while the header is a skeleton.</summary>
    public Func<Func<ColorF>, Element>? Primary { get; init; }

    /// <summary>The heart's presence bind (shown while the primary is not "Follow").</summary>
    public required Prop<bool> HeartShown { get; init; }

    /// <summary>The ⋯ menu, built lazily at open.</summary>
    public required Func<ContextMenuModel?> More { get; init; }

    /// <summary>The reader (the SAME one the route page mounts), keyed per show by the pane; null while the header is a skeleton.</summary>
    public Element? Reader { get; init; }

    /// <inheritdoc />
    public bool Equals(LibraryShowPaneProps? other) => ReferenceEquals(this, other);

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

    /// <inheritdoc />
    public bool Equals(LibrarySavedEpisodesProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
