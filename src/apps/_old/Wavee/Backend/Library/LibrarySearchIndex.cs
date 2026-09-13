using System;
using System.Collections.Generic;
using System.Threading;
using Wavee.Backend.Persistence;
using Wavee.Core;

namespace Wavee.Backend.Library;

// ── Offline library full-text search ─────────────────────────────────────────────────────────────────────────────────
// A cache-only, HIERARCHICAL search over the user's library — NO network, NO on-demand hydration (per-keystroke fetches
// would starve the UI). It produces a drill-down tree the library page fans across its master-detail columns:
//   Artists scope → artist ▸ matching albums ▸ matching tracks. An artist is INCLUDED when its name matches OR it owns a
//     matching album/track (so e.g. Jukjae surfaces because his album matched, even though "jukjae" wasn't typed).
//   Albums scope  → matching album ▸ matching tracks.
// Inclusion cascades: if the artist name matched, ALL its albums show (browse the artist); if an album name (or its
// artist) matched, ALL its tracks show; otherwise only the entities whose own name/title matched. Matched names/titles
// carry a highlight span; entities present only because a child matched carry MatchLen == 0 (no highlight).
//
// THE CORPUS (Addendum A4 / locked decision 7). The hot tier is a bounded cache over cold now, so walking residency is
// no longer a correct corpus. Two sources are UNIONED per level:
//   • the COLD candidates — thin `entity` rows (uri/title/subtitle/image_url/album_uri, no payload) joined out of
//     collection_items + entity_refs by SqliteColdStore.LoadLibraryCandidates. This is what makes a saved entity that is
//     on disk but NOT resident findable, and it is what supplies album TRACKS at all (a persisted album blob carries no
//     tracklist — PersistAlbum strips it).
//   • the RESIDENT records (store.GetArtist / store.GetAlbum, both of which cold-fall-back). They take PRECEDENCE: a hot
//     record is at worst as fresh as its cold row and may be fresher (the cold write-behind lane is asynchronous), and
//     it carries the fields the thin columns cannot (Year, AlbumKind, an ordered tracklist).
// MATCHING IS ALWAYS C#-SIDE `IndexOf(q, OrdinalIgnoreCase)` — never SQL. The UI needs the match OFFSET, not a boolean,
// and SQLite `NOCASE` folds ASCII only, so any `LIKE` pre-pass would be a strict SUBSET (it would drop Ω/ω, İ, ı …).
// There is therefore no pre-filter at all: SQL bounds the SCOPE, C# decides the MATCH.
//
// Runs off the UI thread (StoreLibrarySource wraps it in Task.Run); Store reads are lock-safe and the cold reads ride the
// dedicated read-only SQLite connection.
//
// ── MATCH THIN FIRST, HYDRATE ONLY SURVIVORS (stage O5) ──────────────────────────────────────────────────────────────
// The walk above used to hydrate the WHOLE corpus before it knew what matched: one `store.GetArtist` per followed artist
// (a hot miss is 2 SQLite reads + 2 JSON deserializes + ArtistDiscography.Assemble, which itself does one GetAlbum per
// discography card) plus one `store.GetAlbum` per album — per KEYSTROKE. Matching needs none of that: the thin rows
// already carry every title the matcher reads. So the walk is now two phases:
//
//   PHASE 1 (selection) reads ONLY the thin corpus. An artist/album the cold rows cannot match is dropped here, at zero
//     SQLite point-reads and zero deserializes. `ArtistCap` is applied to the SELECTED set (ranked by the same key the
//     emission uses), so it now bounds the HYDRATION and not just the output.
//   PHASE 2 (build) is byte-for-byte the old body — `store.GetArtist` / `store.GetAlbum`, resident record first, thin row
//     as the fallback — run only over the survivors. Everything a hit renders with (Year, AlbumKind, the ordered
//     tracklist, a fresher name/cover) therefore still comes from the RESIDENT record: resident-takes-precedence is
//     unchanged for every EMITTED entity, because every emitted entity is still fully hydrated.
//
// WHERE THE CORPUS CANNOT ANSWER, THE OLD WALK STILL RUNS. Phase 1 never drops an entity the thin rows do not describe:
//   • a saved artist with no `entity` row, or with no artist→album edges, is handed to phase 2 unconditionally (this is
//     the case SqliteColdStore.LoadLibraryCandidates documents as "the caller still walks it through the store"), and
//   • an album with no thin row (a resident-only overview card) is hydrated rather than skipped.
// A store with NO candidate seam at all (a fake backend / a plain InMemoryStore) therefore takes the pre-SQL path
// verbatim: every saved uri goes to phase 2 and nothing is pre-filtered.
//
// THE HONEST RESIDUAL. Phase 1 matches the thin title, which is `EntityThinExtractor`'s projection of the payload that
// was last PERSISTED. A hot record that is newer than its cold row (the write-behind lane is asynchronous) can therefore
// carry a name the thin row does not, and an entity that is resident but was never persisted at all is invisible to
// phase 1. In both cases the hit is missed until the lane catches up — a transient staleness window, not a lost row.
// This is the one behavior the "hydrate everything first" walk bought, and it cost a full library hydration per keystroke.
public static class LibrarySearchIndex
{
    const int ArtistCap = 200;          // matched-artist ceiling (bounded by the followed set anyway)
    const int TracksPerAlbumCap = 500;  // per-album track ceiling — a guard against a pathological tracklist, never hit
                                        // by a real album (the longest catalog albums are ~100 rows)

    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query, CancellationToken ct = default)
        => Run(store, scope, query, store as ILibraryCandidateStore, ct);

    /// <summary>The testable seam: pass <paramref name="candidates"/> = null to search the RESIDENT graph only (the
    /// pre-SQL path, still the whole story for a fake/offline backend or a plain <c>InMemoryStore</c>).</summary>
    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query, ILibraryCandidateStore? candidates, CancellationToken ct = default)
        => Run(store, scope, query, LibrarySearchCorpus.Load(candidates, scope), ct);

    /// <summary>The hot path: search against an ALREADY-LOADED corpus. <see cref="StoreLibrarySource"/> caches one per
    /// scope across keystrokes, so a burst of as-you-type queries streams the cold candidate rows once, not once per
    /// character. <paramref name="ct"/> is honored inside the walk (both loops), so a superseded keystroke stops holding
    /// the store's read lock the moment the next one arrives.</summary>
    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query, LibrarySearchCorpus corpus, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0) return LibrarySearchResults.Empty;
        return scope == LibrarySearchScope.Artists
            ? new LibrarySearchResults(RunArtists(store, corpus, q, ct), Array.Empty<LibraryAlbumGroup>())
            : new LibrarySearchResults(Array.Empty<LibraryArtistGroup>(), RunAlbums(store, corpus, q, ct));
    }

    // ── Artists scope ────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>One selected artist, carried from phase 1 to phase 2. <c>Walk</c> marks the artists the thin corpus
    /// cannot describe (no root row, or no artist→album edges) — those are hydrated unconditionally, exactly as the
    /// pre-O5 walk did, and are never subject to the pre-hydration cap.</summary>
    readonly record struct Cand(string Uri, string Name, int Primary, bool Walk);

    static IReadOnlyList<LibraryArtistGroup> RunArtists(IStore store, LibrarySearchCorpus corpus, string q, CancellationToken ct)
    {
        // PHASE 1 — thin selection. No store reads at all: every decision here is made from the cold thin columns.
        var order = Union(store.SavedUris("artists"), corpus.RootRows);
        var cands = new List<Cand>(Math.Min(order.Count, 64));
        int thinCount = 0;
        for (int i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var artistUri = order[i];
            var coldAlbums = corpus.AlbumsOf(artistUri);
            var row = corpus.Root(artistUri);
            if (row is null || coldAlbums.Count == 0)
            {
                cands.Add(new Cand(artistUri, "", 1, Walk: true));   // the corpus cannot answer → the resident walk
                continue;
            }
            var name = row.Value.Title ?? "";
            bool nameMatched = Match(name, q) is not null;
            if (!nameMatched && !AnyColdChildMatches(corpus, coldAlbums, q)) continue;   // nothing under it can match
            cands.Add(new Cand(artistUri, name, nameMatched ? 0 : 1, Walk: false));
            thinCount++;
        }
        if (thinCount > ArtistCap) cands = CapThin(cands);

        // PHASE 2 — hydrate the survivors and build. Kept in the phase-1 (Union) order so equal-ranked ties stay
        // deterministic through the unstable List.Sort below, exactly as before.
        var artists = new List<Ranked<LibraryArtistGroup>>(cands.Count);
        for (int i = 0; i < cands.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (BuildArtist(store, corpus, cands[i].Uri, q, ct) is { } ranked) artists.Add(ranked);
        }
        return SortArtists(artists);
    }

    // Bound the HYDRATION, not just the emission: rank the thin-selected artists by the very key SortArtists uses
    // (name-match first, then the name tiebreak) and keep the top ArtistCap of them. Exact whenever the resident name
    // equals the thin title — which is what the thin columns are projected from — and it turns a "type one letter in a
    // 5000-artist library" query from 5000 hydrations into 200.
    static List<Cand> CapThin(List<Cand> cands)
    {
        var thin = new List<Cand>(cands.Count);
        for (int i = 0; i < cands.Count; i++) if (!cands[i].Walk) thin.Add(cands[i]);
        thin.Sort(static (a, b) => a.Primary != b.Primary
            ? a.Primary.CompareTo(b.Primary)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var keep = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ArtistCap && i < thin.Count; i++) keep.Add(thin[i].Uri);
        var result = new List<Cand>(keep.Count + 8);
        for (int i = 0; i < cands.Count; i++) if (cands[i].Walk || keep.Contains(cands[i].Uri)) result.Add(cands[i]);
        return result;
    }

    // Phase 1's child test: does ANY of this artist's cold albums match by its own thin title or by one of its cold
    // track titles? (`AlbumsOf` only yields edges whose child has a thin album row, so `CanMatchThin` never falls into
    // its "the corpus does not know this album" escape here.)
    static bool AnyColdChildMatches(LibrarySearchCorpus corpus, IReadOnlyList<string> albumUris, string q)
    {
        for (int i = 0; i < albumUris.Count; i++) if (CanMatchThin(corpus, albumUris[i], null, q)) return true;
        return false;
    }

    // PHASE 2 for one artist — the pre-O5 body verbatim: the resident record first (Name/Image/TopAlbums), the thin row
    // as the fallback, the cold refs closure unioned in.
    static Ranked<LibraryArtistGroup>? BuildArtist(IStore store, LibrarySearchCorpus corpus, string artistUri, string q, CancellationToken ct)
    {
        var artist = store.GetArtist(artistUri);
        var row = corpus.Root(artistUri);
        var name = artist?.Name is { Length: > 0 } n ? n : row?.Title ?? "";
        if (artist is null && name.Length == 0) return null;   // neither resident nor on disk → nothing to match
        var aSpan = Match(name, q);

        var albums = new List<Ranked<LibraryAlbumGroup>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The resident/overview cards first (they carry Year + AlbumKind), then any album the cold refs closure knows
        // about that the overview didn't list — the two together are the artist's offline discography.
        foreach (var card in artist?.TopAlbums ?? Array.Empty<Album>())
            AddAlbum(card.Uri, card);
        foreach (var albumUri in corpus.AlbumsOf(artistUri))
            AddAlbum(albumUri, null);

        if (aSpan is null && albums.Count == 0) return null;   // no match anywhere under this artist
        var sortedAlbums = Sort(albums);
        // Why the artist surfaced when its OWN name didn't match: attribute the child that pulled it in (a name-matched
        // album, else a title-matched track). One of these always exists here (we only keep a name-unmatched artist when
        // albums.Count > 0), so the "why" caption is certain — never a guess.
        var reason = aSpan is not null ? MatchReason.None : ArtistReason(sortedAlbums);
        return new Ranked<LibraryArtistGroup>(
            new LibraryArtistGroup(artistUri, name, artist?.Image ?? ImageOf(row?.ImageUrl),
                aSpan?.Start ?? -1, aSpan?.Len ?? 0, sortedAlbums, reason),
            aSpan is not null ? 0 : 1, 0, name);

        void AddAlbum(string uri, Album? card)
        {
            if (uri.Length == 0 || !seen.Add(uri)) return;
            ct.ThrowIfCancellationRequested();
            // A MATCHED artist shows all its albums (browse), so each of them is emitted and must be hydrated for its
            // Year/Kind. Under a name-unmatched artist the album is only emitted if it can match — so ask the thin rows
            // first and skip the point-read entirely when they say no.
            if (aSpan is null && !CanMatchThin(corpus, uri, card, q)) return;
            var album = store.GetAlbum(uri) ?? card ?? AlbumStub(corpus, uri);
            if (album is null) return;
            if (BuildAlbum(album, TracksOf(corpus, album), aSpan is not null, q) is { } g)
                albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
        }
    }

    // ── Albums scope ─────────────────────────────────────────────────────────────────────────────────────────────────
    static IReadOnlyList<LibraryAlbumGroup> RunAlbums(IStore store, LibrarySearchCorpus corpus, string q, CancellationToken ct)
    {
        var albums = new List<Ranked<LibraryAlbumGroup>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = Union(store.SavedUris("albums"), corpus.AlbumRows);
        for (int i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var albumUri = order[i];
            if (!seen.Add(albumUri)) continue;
            if (!CanMatchThin(corpus, albumUri, null, q)) continue;   // thin-first: no point-read for a non-matching album
            var album = store.GetAlbum(albumUri) ?? AlbumStub(corpus, albumUri);
            if (album is null) continue;
            if (BuildAlbum(album, TracksOf(corpus, album), false, q) is { } g)
                albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
        }
        // NOT capped. Unlike artists, the album ranking's secondary key is `-Year`, which only the HYDRATED record
        // carries — so there is no pre-hydration key to cap on without changing the emitted order. A one-character query
        // over a huge saved-album set therefore still hydrates every album whose thin row matches; it no longer hydrates
        // the ones that don't, which is the 95% case.
        return Sort(albums);
    }

    /// <summary>Can this album contribute WITHOUT being hydrated? Its own thin title, the resident overview card's name
    /// when the caller has one, or any of its cold track titles. An album the corpus does not know is answered "yes" —
    /// a resident-only card must still be hydrated, never silently dropped.</summary>
    static bool CanMatchThin(LibrarySearchCorpus corpus, string albumUri, Album? card, string q)
    {
        if (corpus.AlbumRow(albumUri) is not { } row) return true;
        if (card is not null && Match(card.Name, q) is not null) return true;
        if (Match(row.Title ?? "", q) is not null) return true;
        var tracks = corpus.ColdTracksOf(albumUri);
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Title is { Length: > 0 } t && Match(t, q) is not null) return true;
        return false;
    }

    // The saved set (the identity tier — always resident, and the ORDER the pre-SQL walk used) first, then any cold row
    // the saved set didn't name. De-duplicated, order-stable: ranking is what decides the output order, but a stable
    // input keeps equal-ranked ties deterministic.
    static List<string> Union(IReadOnlyList<string> savedUris, IReadOnlyList<ColdThinRow> rows)
    {
        var list = new List<string>(savedUris.Count + rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < savedUris.Count; i++) if (savedUris[i].Length > 0 && seen.Add(savedUris[i])) list.Add(savedUris[i]);
        for (int i = 0; i < rows.Count; i++) if (rows[i].Uri.Length > 0 && seen.Add(rows[i].Uri)) list.Add(rows[i].Uri);
        return list;
    }

    /// <summary>A minimal Album for a row that exists on disk but nowhere in memory and in no overview: name + cover
    /// only. Year/Kind are unknowable from the thin columns, so it ranks as year 0 / AlbumKind.Album.</summary>
    static Album? AlbumStub(LibrarySearchCorpus corpus, string uri)
        => corpus.AlbumRow(uri) is { } r ? new Album("", uri, r.Title ?? "", ImageOf(r.ImageUrl), Array.Empty<ArtistRef>(), 0, 0) : null;

    /// <summary>The album's tracklist for search: the RESIDENT list first (ordered, and its index is the playback index
    /// the row click uses), then every cold track row the resident list didn't carry. A cold-only album has no persisted
    /// track ORDER — its blob never stored one — so those rows keep the SQL's `uri` order and are indexed after the
    /// resident ones.</summary>
    static List<TrackCand> TracksOf(LibrarySearchCorpus corpus, Album album)
    {
        var tl = album.Tracks;
        var cold = corpus.ColdTracksOf(album.Uri);
        var list = new List<TrackCand>((tl?.Count ?? 0) + cold.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (tl is not null)
            for (int i = 0; i < tl.Count; i++)
            {
                var t = tl[i];
                if (t.Title.Length == 0 || !seen.Add(t.Uri)) continue;
                list.Add(new TrackCand(t.Uri, t.Title, t.Image ?? album.Cover, i));
            }
        int next = tl?.Count ?? 0;
        for (int i = 0; i < cold.Count; i++)
        {
            var r = cold[i];
            if (r.Title is not { Length: > 0 } title || r.Uri.Length == 0 || !seen.Add(r.Uri)) continue;
            list.Add(new TrackCand(r.Uri, title, ImageOf(r.ImageUrl) ?? album.Cover, next++));
        }
        return list;
    }

    /// <summary>One searchable track: the thin fields plus its playback index inside the album.</summary>
    readonly record struct TrackCand(string Uri, string Title, Image? Cover, int Index);

    internal static Image? ImageOf(string? url) => string.IsNullOrEmpty(url) ? null : new Image(url);

    // Build an album group if it should be included: the album name matched, or its artist matched (parentMatched), or
    // it has ≥1 matching track. Tracks shown = ALL when the album/artist matched, else only the title-matching ones.
    static LibraryAlbumGroup? BuildAlbum(Album album, List<TrackCand> candidates, bool parentMatched, string q)
    {
        var alSpan = Match(album.Name, q);
        bool albumMatched = alSpan is not null || parentMatched;

        var tracks = new List<LibraryTrackHit>();
        for (int i = 0; i < candidates.Count && tracks.Count < TracksPerAlbumCap; i++)
        {
            var t = candidates[i];
            var tSpan = Match(t.Title, q);
            if (albumMatched)
                tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Cover, t.Index, tSpan?.Start ?? -1, tSpan?.Len ?? 0));
            else if (tSpan is not null)
                tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Cover, t.Index, tSpan.Value.Start, tSpan.Value.Len));
        }

        if (!albumMatched && tracks.Count == 0) return null;
        // The album's own "why": its name matched → None (the highlight explains it). Else, when it was NOT pulled in by
        // a matching parent artist (i.e. it stands as a top-level album result), it is here through a title-matched
        // track → attribute that track. Under a matched artist (parentMatched) it is browse context → no reason.
        var reason = MatchReason.None;
        if (alSpan is null && !parentMatched)
            foreach (var th in tracks)
                if (th.MatchLen > 0) { reason = new MatchReason(LibraryMatchKind.Track, th.Title); break; }
        return new LibraryAlbumGroup(album.Uri, album.Name, album.Cover, album.Year, album.Kind, alSpan?.Start ?? -1, alSpan?.Len ?? 0, tracks, reason);
    }

    // The reason a name-unmatched artist surfaced: prefer a child album whose NAME matched (the most specific, useful
    // attribution), else the first child track whose TITLE matched. Returns None only if neither exists (defensive — the
    // caller guarantees at least one child matched), so a hit is never captioned with a fabricated reason.
    static MatchReason ArtistReason(IReadOnlyList<LibraryAlbumGroup> albums)
    {
        foreach (var a in albums)
            if (a.MatchLen > 0) return new MatchReason(LibraryMatchKind.Album, a.Name);
        foreach (var a in albums)
            foreach (var t in a.Tracks)
                if (t.MatchLen > 0) return new MatchReason(LibraryMatchKind.Track, t.Title);
        return MatchReason.None;
    }

    readonly record struct MatchSpan(int Start, int Len);

    static MatchSpan? Match(string text, string q)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : new MatchSpan(i, q.Length);
    }

    // Rank tuple: primary (name-match beats child-only), secondary (e.g. year desc), then a name tiebreak.
    readonly record struct Ranked<T>(T Value, int Primary, int Secondary, string Name = "");

    static IReadOnlyList<LibraryAlbumGroup> Sort(List<Ranked<LibraryAlbumGroup>> list)
    {
        list.Sort(Compare);
        var arr = new LibraryAlbumGroup[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = list[i].Value;
        return arr;
    }

    static IReadOnlyList<LibraryArtistGroup> SortArtists(List<Ranked<LibraryArtistGroup>> list)
    {
        list.Sort(Compare);
        int n = Math.Min(list.Count, ArtistCap);
        var arr = new LibraryArtistGroup[n];
        for (int i = 0; i < n; i++) arr[i] = list[i].Value;
        return arr;
    }

    static int Compare<T>(Ranked<T> a, Ranked<T> b)
    {
        if (a.Primary != b.Primary) return a.Primary.CompareTo(b.Primary);
        if (a.Secondary != b.Secondary) return a.Secondary.CompareTo(b.Secondary);
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}

// ── the cold corpus, indexed ─────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The loaded-and-indexed cold candidate rows for ONE <see cref="LibrarySearchScope"/> — the read-only substrate
/// phase 1 of <see cref="LibrarySearchIndex"/> matches against. IMMUTABLE once constructed, which is what lets
/// <see cref="StoreLibrarySource"/> hand the same instance to concurrent threadpool searches and swap it by reference.
/// </summary>
public sealed class LibrarySearchCorpus
{
    /// <summary>The corpus of a store with no candidate seam (or one whose cold read failed): matches nothing, describes
    /// nothing, so every saved uri falls through to the resident walk — the pre-SQL behavior, verbatim.</summary>
    public static readonly LibrarySearchCorpus Empty = new(ColdCandidates.Empty);

    readonly Dictionary<string, ColdThinRow> _roots = new(StringComparer.Ordinal);
    readonly Dictionary<string, ColdThinRow> _albums = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<ColdThinRow>> _tracksByAlbum = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _albumsByArtist = new(StringComparer.Ordinal);

    LibrarySearchCorpus(ColdCandidates c)
    {
        RootRows = c.Roots;
        AlbumRows = c.Albums;
        for (int i = 0; i < c.Roots.Count; i++) _roots[c.Roots[i].Uri] = c.Roots[i];
        for (int i = 0; i < c.Albums.Count; i++) _albums[c.Albums[i].Uri] = c.Albums[i];
        for (int i = 0; i < c.Tracks.Count; i++)
        {
            var t = c.Tracks[i];
            if (t.AlbumUri is not { Length: > 0 } album) continue;
            if (!_tracksByAlbum.TryGetValue(album, out var list)) _tracksByAlbum[album] = list = new List<ColdThinRow>(16);
            list.Add(t);
        }
        for (int i = 0; i < c.Edges.Count; i++)
        {
            var e = c.Edges[i];
            if (!_albums.ContainsKey(e.ChildUri)) continue;   // the edge table also carries track→artist edges
            if (!_albumsByArtist.TryGetValue(e.ParentUri, out var list)) _albumsByArtist[e.ParentUri] = list = new List<string>(16);
            list.Add(e.ChildUri);
        }
    }

    /// <summary>The scope's root rows (followed artists / saved albums) in SQL order — the second leg of the walk's
    /// UNION, after the saved set.</summary>
    public IReadOnlyList<ColdThinRow> RootRows { get; }
    public IReadOnlyList<ColdThinRow> AlbumRows { get; }

    /// <summary>True when the cold tier described nothing — the search then runs as a pure resident walk.</summary>
    public bool IsEmpty => _roots.Count == 0 && _albums.Count == 0 && _tracksByAlbum.Count == 0;

    /// <summary>Stream the scope's candidate rows and index them. A cold read must never break search: an I/O failure
    /// degrades to <see cref="Empty"/>, i.e. to the resident-graph walk, which is exactly the pre-SQL behavior.</summary>
    public static LibrarySearchCorpus Load(ILibraryCandidateStore? candidates, LibrarySearchScope scope)
    {
        if (candidates is null) return Empty;
        try
        {
            var cold = candidates.LoadLibraryCandidates(
                scope == LibrarySearchScope.Artists ? ColdCandidateScope.SavedArtists : ColdCandidateScope.SavedAlbums)
                ?? ColdCandidates.Empty;
            return cold.IsEmpty ? Empty : new LibrarySearchCorpus(cold);
        }
        catch (Exception) { return Empty; }
    }

    internal ColdThinRow? Root(string uri) => _roots.TryGetValue(uri, out var r) ? r : null;
    internal ColdThinRow? AlbumRow(string uri) => _albums.TryGetValue(uri, out var r) ? r : null;

    internal IReadOnlyList<string> AlbumsOf(string artistUri)
        => _albumsByArtist.TryGetValue(artistUri, out var list) ? list : Array.Empty<string>();

    internal IReadOnlyList<ColdThinRow> ColdTracksOf(string albumUri)
        => _tracksByAlbum.TryGetValue(albumUri, out var rows) ? rows : Array.Empty<ColdThinRow>();
}
