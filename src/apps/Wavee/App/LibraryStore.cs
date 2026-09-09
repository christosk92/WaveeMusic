using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Root library presentation signals. The shared query owns freshness and catalog retention.</summary>
public sealed class LibraryStore : IDisposable
{
    public static readonly Context<LibraryStore?> Slot = new(null);
    readonly IQueryService _queries;
    CatalogScope _scope;
    Action<Action>? _post;
    QuerySignalBinding<LibraryQuerySnapshot>? _binding;

    public Loadable<IReadOnlyList<Album>> Albums { get; } = Loadable<IReadOnlyList<Album>>.Pending([]);
    public Loadable<IReadOnlyList<Artist>> Artists { get; } = Loadable<IReadOnlyList<Artist>>.Pending([]);
    public Loadable<IReadOnlyList<PlaylistSummary>> Playlists { get; } = Loadable<IReadOnlyList<PlaylistSummary>>.Pending([]);
    public Loadable<IReadOnlyList<Show>> Shows { get; } = Loadable<IReadOnlyList<Show>>.Pending([]);
    public Loadable<LibraryStats> Stats { get; } = Loadable<LibraryStats>.Pending(new(0, 0, 0, 0));
    public Loadable<IReadOnlyList<PlaylistNode>> PlaylistTree { get; } = Loadable<IReadOnlyList<PlaylistNode>>.Pending([]);
    public Loadable<IReadOnlyDictionary<string, long>> AddedAt { get; } =
        Loadable<IReadOnlyDictionary<string, long>>.Pending(EmptyAddedAt);
    public static IReadOnlyDictionary<string, long> EmptyAddedAt => SidebarTree.NoAddedAt;

    // The last library the callback actually published cells from — reference-compared, never deep-compared. A
    // publication that carries the SAME per-collection list instance as last time (an activity/error-only pass that
    // never touched Albums/Artists/Playlists/Shows/Tree/AddedAt) must not re-arm that cell's Signal: writing a
    // reference-different-but-content-identical list would still notify every subscriber (Signal's default equality
    // is reference equality for a list), which is exactly the "one publication fans into seven writes" cost this
    // guards against. Once LibraryQuerySnapshot's own equality is structural (agent B), the outer snapshot itself is
    // reused byte-for-byte on an unchanged pass, so the top-level ReferenceEquals below already catches it; until
    // then this per-cell compare is the fallback.
    LibraryQuerySnapshot? _lastPublished;

    // The startup-timeline mark: one line the first time this account's library actually reaches the sidebar with rows
    // in it. Reset only by an account switch (RebindScope), because that is the only thing that starts a new "how long
    // until this user sees their library?" question.
    bool _firstDataLogged;

    public LibraryStore(IQueryService queries, CatalogScope scope) { _queries = queries; _scope = scope; }

    public void Activate(Action<Action> post)
    {
        _post = post;
        if (_binding is not null) return;
        _binding = new(_queries.Acquire(new SidebarLibraryQuery(_scope)), post, snapshot =>
        {
            if (!snapshot.Status.HasPrimaryData)
            {
                if (snapshot.Status.IsRefreshing || snapshot.Problems.Count == 0 || Albums.IsReady) return;
                var error = new InvalidOperationException(snapshot.Problems[0].Error?.Message ?? "Your library is not available.");
                Albums.SetFailed(error); Artists.SetFailed(error); Playlists.SetFailed(error); Shows.SetFailed(error);
                Stats.SetFailed(error); PlaylistTree.SetFailed(error); AddedAt.SetFailed(error);
                return;
            }
            var library = snapshot.Value;
            if (ReferenceEquals(library, _lastPublished)) return;   // an activity/error-only pass — nothing moved
            _lastPublished = library;
            if (!_firstDataLogged && (library.Playlists.Count > 0 || library.Albums.Count > 0
                || library.Artists.Count > 0 || library.Shows.Count > 0))
            {
                _firstDataLogged = true;
                WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "library.first-data",
                    "the library query published its first rows", fields:
                    [
                        WaveeLogField.Of("sinceStartMs", WaveeLog.SinceStartMs),
                        WaveeLogField.Of("playlists", library.Playlists.Count),
                        WaveeLogField.Of("albums", library.Albums.Count),
                        WaveeLogField.Of("artists", library.Artists.Count),
                        WaveeLogField.Of("shows", library.Shows.Count),
                    ]);
            }
            SetIfMoved(Albums, library.Albums);
            SetIfMoved(Artists, library.Artists);
            SetIfMoved(Playlists, library.Playlists);
            SetIfMoved(Shows, library.Shows);
            SetIfMoved(Stats, library.Stats);
            SetIfMoved(PlaylistTree, library.Tree);
            SetIfMoved(AddedAt, library.AddedAt);
        });
        _binding.SetDemand(QueryDemand.Initial);
        _binding.SetActive(true);
    }

    // A cell write only when the NEW list looks different from what is already published: not the same reference,
    // and not merely a fresh instance wrapping the same content (the count-equal case — the closest cheap proxy
    // available before agent B's structural LibraryQuerySnapshot equality lands; see the class doc comment above).
    // A real content change almost always moves the count too (add/remove), so this still catches the common case
    // while a same-count reshuffle rides in with the outer ReferenceEquals guard once that lands. Overloaded (not
    // generic) per concrete cell type: IReadOnlyList<T> and IReadOnlyDictionary<K,V> share no common `Count`-bearing
    // interface a single generic constraint can name.
    // internal (not private): SidebarRebuildGatingTests exercises this decision directly — same compiled assembly
    // (source-included), no production behavior exposed beyond what Activate() above already calls.
    internal static void SetIfMoved(Loadable<IReadOnlyList<Album>> cell, IReadOnlyList<Album> value) { if (Moved(value, cell.Value.Peek())) cell.SetReady(value); }
    internal static void SetIfMoved(Loadable<IReadOnlyList<Artist>> cell, IReadOnlyList<Artist> value) { if (Moved(value, cell.Value.Peek())) cell.SetReady(value); }
    internal static void SetIfMoved(Loadable<IReadOnlyList<PlaylistSummary>> cell, IReadOnlyList<PlaylistSummary> value) { if (Moved(value, cell.Value.Peek())) cell.SetReady(value); }
    internal static void SetIfMoved(Loadable<IReadOnlyList<Show>> cell, IReadOnlyList<Show> value) { if (Moved(value, cell.Value.Peek())) cell.SetReady(value); }
    internal static void SetIfMoved(Loadable<IReadOnlyList<PlaylistNode>> cell, IReadOnlyList<PlaylistNode> value) { if (Moved(value, cell.Value.Peek())) cell.SetReady(value); }
    internal static void SetIfMoved(Loadable<IReadOnlyDictionary<string, long>> cell, IReadOnlyDictionary<string, long> value)
    {
        if (ReferenceEquals(value, cell.Value.Peek())) return;
        cell.SetReady(value);
    }
    internal static void SetIfMoved(Loadable<LibraryStats> cell, LibraryStats value)
    {
        if (value.Equals(cell.Value.Peek())) return;
        cell.SetReady(value);
    }

    // Reference identity only: the definition returns the SAME list instance when nothing in it moved, and a rename
    // or reorder keeps the count, so a count compare would swallow exactly the changes the sidebar must show.
    static bool Moved<T>(IReadOnlyCollection<T> value, IReadOnlyCollection<T>? current) => !ReferenceEquals(value, current);

    public void RebindScope(CatalogScope scope)
    {
        if (_scope == scope) return;
        // A locale/market/tier/explicit-filter flip re-scopes the query without changing WHO owns the library, so
        // the already-Ready cells stay put (no Pending flash) while the new scope's binding warms in the background
        // and SetIfMoved republishes once real data lands. Only an account switch clears them.
        bool sameAccount = _scope.Provider == scope.Provider && _scope.ProviderAccount == scope.ProviderAccount
            && _scope.StorageAccount == scope.StorageAccount;
        Dispose(); _scope = scope;
        if (!sameAccount)
        {
            _lastPublished = null;
            _firstDataLogged = false;
            Albums.SetPending([]); Artists.SetPending([]); Playlists.SetPending([]); Shows.SetPending([]);
            Stats.SetPending(new(0, 0, 0, 0)); PlaylistTree.SetPending([]); AddedAt.SetPending(EmptyAddedAt);
        }
        if (_post is { } post) Activate(post);
    }

    public void Dispose() { _binding?.Dispose(); _binding = null; }
}
