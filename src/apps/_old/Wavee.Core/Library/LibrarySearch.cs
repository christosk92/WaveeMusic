using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>Which slice of the cached library a full-text search covers — mirrors the library page's kind. Artists →
/// followed artists ▸ their albums ▸ tracks in those albums. Albums → saved albums ▸ tracks in those albums.</summary>
public enum LibrarySearchScope { Artists, Albums }

/// <summary>Which field the query actually matched on a hit whose OWN name did not — i.e. the reason a non-exact result
/// surfaced. <see cref="Album"/>: a child album's name matched (an artist pulled in through one of its albums).
/// <see cref="Track"/>: a child track's title matched (an artist/album pulled in through one of its tracks).
/// <see cref="None"/>: the hit's own name matched (the inline highlight already explains it) OR the reason is not
/// attributable — either way no "why" caption is drawn.</summary>
public enum LibraryMatchKind { None, Album, Track }

/// <summary>Why a non-exact library-search hit appeared, when it was NOT the hit's own name that matched. Derived at
/// match time from the resident store (the matcher knows exactly which child caused inclusion), so it is a certain,
/// never-fabricated attribution. <see cref="Term"/> is the concrete matched value (the album name / track title) that
/// the "why" caption quotes. <see cref="LibraryMatchKind.None"/> (the default) → no caption.</summary>
public readonly record struct MatchReason(LibraryMatchKind Kind, string? Term = null)
{
    public static readonly MatchReason None = default;

    /// <summary>True only when there is an attributable, non-name reason worth captioning (honesty rule: an
    /// empty/None reason renders nothing rather than a guessed explanation).</summary>
    public bool ShouldExplain => Kind != LibraryMatchKind.None && !string.IsNullOrEmpty(Term);
}

/// <summary>One matching track inside an album group. <see cref="AlbumIndex"/> is its position in the album's full
/// tracklist (for playback). <see cref="MatchLen"/> == 0 means the title itself didn't match — the track is shown
/// because its album/artist matched — so no highlight is drawn.</summary>
public readonly record struct LibraryTrackHit(
    string Uri, string Title, Image? Cover, int AlbumIndex, int MatchStart, int MatchLen);

/// <summary>One matching album, grouped under its artist. Carries its matching tracks (all tracks when the album/artist
/// name matched; only the matching tracks otherwise). <see cref="MatchLen"/> == 0 → the album name didn't match (it's
/// here because a track/artist matched) → no highlight. <see cref="Match"/> attributes that non-name reason (e.g. the
/// title-matched track) for the "why" caption when this album is shown as a top-level result.</summary>
public sealed record LibraryAlbumGroup(
    string Uri, string Name, Image? Cover, int Year, AlbumKind Kind,
    int MatchStart, int MatchLen, IReadOnlyList<LibraryTrackHit> Tracks, MatchReason Match = default);

/// <summary>One matching artist and their matching albums. Present when the artist name matched OR any album/track under
/// them matched. <see cref="MatchLen"/> == 0 → the name didn't match (a child did) → no highlight. <see cref="Match"/>
/// attributes that non-name reason (the album/track that surfaced the artist) for the "why" caption.</summary>
public sealed record LibraryArtistGroup(
    string Uri, string Name, Image? Image,
    int MatchStart, int MatchLen, IReadOnlyList<LibraryAlbumGroup> Albums, MatchReason Match = default);

/// <summary>The hierarchical library-search result. Artists-scope populates <see cref="Artists"/> (artist ▸ album ▸
/// track drill-down); albums-scope populates <see cref="Albums"/> (album ▸ track). The UI fans these across the
/// master-detail columns and filters+highlights each level.</summary>
public sealed record LibrarySearchResults(
    IReadOnlyList<LibraryArtistGroup> Artists,
    IReadOnlyList<LibraryAlbumGroup> Albums)
{
    public static readonly LibrarySearchResults Empty =
        new(Array.Empty<LibraryArtistGroup>(), Array.Empty<LibraryAlbumGroup>());

    public bool IsEmpty => Artists.Count == 0 && Albums.Count == 0;
}
