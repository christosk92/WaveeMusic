using System;
using System.Collections.Generic;
using Wavee.Backend.Metadata;

namespace Wavee.Backend.Persistence;

// ── the SQL-backed offline-search corpus (Addendum A4 / locked decision 7) ────────────────────────────────────────────
// "Residency is NOT the search substrate": after the redesign the hot tier is a BOUNDED cache over cold, so walking it is
// no longer a correct corpus for the library search. The corpus is instead derived in SQL from the `entity` THIN COLUMNS
// (title/subtitle/image_url/album_uri — the payload is never read) joined to the identity tables, which is correct at any
// scale and needs no residency at all.
//
// What this seam deliberately does NOT do: it never matches. It answers "which rows are in scope" (a set-based graph
// query); the query itself is matched in C# with `IndexOf(q, OrdinalIgnoreCase)` because (a) the UI needs the match
// OFFSET, not a boolean, and (b) SQLite `NOCASE` is ASCII-only and therefore NARROWER than OrdinalIgnoreCase — a `LIKE`
// pre-pass would silently DROP non-ASCII matches (Ω/ω, İ, dotless ı), which is exactly what A4(b) forbids.

/// <summary>Which slice of the library one candidate query covers.</summary>
public enum ColdCandidateScope
{
    /// <summary>Followed artists ▸ their albums (entity_refs) ▸ those albums' tracks — the Artists-scope drill-down.</summary>
    SavedArtists,
    /// <summary>Saved albums ▸ their tracks — the Albums-scope drill-down.</summary>
    SavedAlbums,
    /// <summary>Every liked track ∪ every adopted-playlist member — the offline <c>QueryTracks</c> corpus.</summary>
    LibraryTracks,
}

/// <summary>One `entity` row WITHOUT its payload: the thin display columns plus the album back-reference. The unit of the
/// offline-search corpus — cheap enough to stream a whole library per keystroke because `payload` sits last in the row
/// and is never selected.</summary>
public readonly record struct ColdThinRow(
    string Uri, EntityKind Kind, string? Title, string? Subtitle, string? ImageUrl,
    long DurationMs, long Flags, string? AlbumUri);

/// <summary>One `entity_refs` edge, normalized so <paramref name="ParentUri"/> is always the ARTIST side for the
/// artist↔album edges the search cascade walks (the table itself carries both directions: artist→albums from the
/// overview projection, album→artists from every album persist).</summary>
public readonly record struct ColdRefEdge(string ParentUri, string ChildUri);

/// <summary>The candidate corpus for one <see cref="ColdCandidateScope"/>. Empty lists (never null) when the cold tier
/// has no SQL back end — the caller then falls through to the legacy in-memory walk.</summary>
public sealed record ColdCandidates(
    IReadOnlyList<ColdThinRow> Roots,
    IReadOnlyList<ColdThinRow> Albums,
    IReadOnlyList<ColdThinRow> Tracks,
    IReadOnlyList<ColdRefEdge> Edges)
{
    public static readonly ColdCandidates Empty = new(
        Array.Empty<ColdThinRow>(), Array.Empty<ColdThinRow>(), Array.Empty<ColdThinRow>(), Array.Empty<ColdRefEdge>());

    public bool IsEmpty => Roots.Count == 0 && Albums.Count == 0 && Tracks.Count == 0;
}

/// <summary>The store-side seam the offline library search binds to. Implemented by <see cref="CachedStore"/> (which
/// forwards to its cold tier); a store without one simply searches the resident graph exactly as before.</summary>
public interface ILibraryCandidateStore
{
    ColdCandidates LoadLibraryCandidates(ColdCandidateScope scope);
}
