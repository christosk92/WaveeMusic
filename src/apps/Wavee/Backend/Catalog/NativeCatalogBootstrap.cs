using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Native libraries submit owned snapshots once and after source changes. Query reads never enumerate sources.</summary>
public sealed class NativeCatalogBootstrap : IAsyncDisposable
{
    readonly SourceRegistry _registry;
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string, CatalogScope> _scopeForSubject;
    readonly IMutationSource? _savedSource;
    readonly List<IDisposable> _subscriptions = [];
    readonly CancellationTokenSource _lifetime = new();
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    readonly Channel<bool> _refresh = System.Threading.Channels.Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly SimpleEvent<Exception> _errors = new();
    Task? _worker;

    public NativeCatalogBootstrap(SourceRegistry registry, LibraryReplicaCoordinator replicas, Func<string, CatalogScope> scopeForSubject,
        IMutationSource? savedSource = null)
        => (_registry, _replicas, _scopeForSubject, _savedSource) = (registry, replicas, scopeForSubject, savedSource);
    public IObservable<Exception> Errors => _errors;

    /// <summary>Ask the pump for a refresh without waiting for it — go-live installs the protocol on the SetProtocolSessionAsync
    /// success path first and only then requests this, so "Connected" no longer sits behind a native-source walk (a
    /// LocalSource + UserPlaylistSource enumeration ending in an <see cref="LibraryReplicaCoordinator.AdoptNativeAsync"/>
    /// commit queued behind whatever else the single <c>DataCommitQueue</c> worker is doing). The bounded (capacity 1,
    /// <see cref="BoundedChannelFullMode.DropWrite"/>) channel keeps at most one pending token, so calling this before
    /// <see cref="StartAsync"/> has run is harmless: StartAsync's own <c>await RefreshAsync</c> covers the initial load,
    /// and the pump — started right after — drains the (at most one) request this queued while it wasn't listening yet,
    /// producing one redundant-but-safe extra pass rather than a lost refresh.</summary>
    public void RequestRefresh() => _refresh.Writer.TryWrite(true);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_worker is not null) throw new InvalidOperationException("Native source bootstrap was already started.");
        foreach (var source in _registry.All)
        {
            if (source is UserPlaylistSource playlists)
                _subscriptions.Add(playlists.PlaylistsChanged.Subscribe(new SourceObserver<int>(_ => _refresh.Writer.TryWrite(true))));
            if (source is ISourceCollectionEvents collections)
                _subscriptions.Add(collections.CollectionsChanged.Subscribe(new SourceObserver<CollectionKind>(_ => _refresh.Writer.TryWrite(true))));
        }
        if (_savedSource is not null)
            _subscriptions.Add(_savedSource.SavedChanged.Subscribe(new SourceObserver<IReadOnlySet<string>>(_ => _refresh.Writer.TryWrite(true))));
        await RefreshAsync(ct).ConfigureAwait(false);
        _worker = Task.Run(PumpAsync);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var scope = _replicas.Scope;
            var saved = _savedSource?.Saved;
            var trees = new Dictionary<string, IReadOnlyList<PlaylistNode>>(StringComparer.Ordinal);
            var allPlaylists = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in _registry.CatalogSources)
            {
                var tree = await source.GetPlaylistTreeAsync(ct).ConfigureAwait(false);
                trees[source.Id] = tree;
                foreach (var playlist in SidebarTree.Flatten(tree)) allPlaylists.Add(playlist.Uri);
                foreach (var playlist in await source.GetPlaylistsAsync(ct).ConfigureAwait(false)) allPlaylists.Add(playlist.Uri);
            }
            if (saved is not null)
                foreach (var uri in saved.Where(uri => EntityUri.KindOf(uri) == EntityKind.Playlist)) allPlaylists.Add(uri);
            for (int priority = 0; priority < _registry.All.Count; priority++)
            {
                var source = _registry.All[priority];
                if (source is not ICatalogSource && source is not IPodcastSource) continue;
                var seeds = new List<CatalogSeed>();
                var observations = new List<CatalogObservation>();
                var playlists = ImmutableArray.CreateBuilder<PlaylistReplicaBaseline>();
                var sets = ImmutableArray.CreateBuilder<CollectionReplicaBaseline>();
                var roots = new List<RootlistEntry>();
                if (source is ICatalogSource catalog)
                {
                    var added = await catalog.GetLibraryAddedAtAsync(ct).ConfigureAwait(false);
                    foreach (var uri in allPlaylists.Where(uri => _registry.OwnerOf(uri)?.Id == source.Id))
                    {
                        var playlist = await catalog.GetPlaylistAsync(uri, ct).ConfigureAwait(false);
                        if (playlist is null) continue;
                        playlist = playlist with { Uri = uri };
                        observations.Add(CatalogObservations.PlaylistHeader(_scopeForSubject(uri), playlist));
                        var members = (playlist.Tracks ?? []).Select((track, index) => new PlaylistMember(
                            string.IsNullOrEmpty(track.ContextUid) ? source.Id + ":" + uri + ":row:" + index : track.ContextUid,
                            track.Uri, track.AddedBy, track.AddedAt?.ToUnixTimeMilliseconds() ?? 0, track.Chart)).ToImmutableArray();
                        playlists.Add(new(uri, members, null, playlist));
                        foreach (var track in playlist.Tracks ?? []) CatalogDomainSeeds.Track(_scopeForSubject(track.Uri), track, seeds);
                    }
                    var albums = await catalog.GetAlbumsAsync(ct).ConfigureAwait(false);
                    foreach (var album in albums) CatalogDomainSeeds.Album(_scopeForSubject(album.Uri), album, seeds);
                    sets.Add(Collection("albums", Selected("albums", source.Id, albums.Select(album => album.Uri), saved), added));
                    var artists = await catalog.GetArtistsAsync(ct).ConfigureAwait(false);
                    foreach (var artist in artists) CatalogDomainSeeds.Artist(_scopeForSubject(artist.Uri), artist, seeds);
                    sets.Add(Collection("artists", Selected("artists", source.Id, artists.Select(artist => artist.Uri), saved), added));
                    var tracks = await catalog.GetLikedSongsAsync(ct).ConfigureAwait(false);
                    foreach (var track in tracks) CatalogDomainSeeds.Track(_scopeForSubject(track.Uri), track, seeds);
                    sets.Add(Collection("liked", Selected("liked", source.Id, tracks.Select(track => track.Uri), saved), added));
                    var tree = trees.GetValueOrDefault(source.Id) ?? [];
                    AppendTree(tree, source.Id, roots, 0,
                        uri => source is UserPlaylistSource || saved is null || saved.Contains(uri));
                    if (saved is not null)
                        foreach (var uri in saved.Where(uri => EntityUri.KindOf(uri) == EntityKind.Playlist
                            && _registry.OwnerOf(uri)?.Id == source.Id && roots.All(row => row.Kind != 0 || row.Uri != uri)))
                            roots.Add(new(roots.Count, 0, uri, null, 0));
                    sets.Add(Collection("playlists", roots.Where(row => row.Kind == 0).Select(row => row.Uri), added));
                }
                if (source is IPodcastSource podcast)
                {
                    var shows = await podcast.GetShowsAsync(ct).ConfigureAwait(false);
                    foreach (var show in shows) CatalogDomainSeeds.Show(_scopeForSubject(show.Uri), show, seeds);
                    sets.Add(Collection("shows", Selected("shows", source.Id, shows.Select(show => show.Uri), saved), SidebarTree.NoAddedAt));
                }
                // Inline children fill missing fields without certifying another provider's freshness.
                foreach (var seed in seeds)
                    observations.Add(new(seed.Key with { Scope = _scopeForSubject(seed.Key.Subject) }, seed.Patch, FillUnknownOnly: true));
                await _replicas.AdoptNativeAsync(new(source.Id, priority, playlists.ToImmutable(),
                    new(roots.ToImmutableArray(), null), sets.ToImmutable(), observations.ToImmutableArray()), scope, ct).ConfigureAwait(false);
            }
        }
        finally { _refreshGate.Release(); }
    }

    static CollectionReplicaBaseline Collection(string set, IEnumerable<string> uris, IReadOnlyDictionary<string, long> added)
        => new(set, uris.Distinct(StringComparer.Ordinal).Select(uri => new SavedItem(uri, added.GetValueOrDefault(uri))).ToImmutableArray());

    IEnumerable<string> Selected(string set, string sourceId, IEnumerable<string> declared, IReadOnlySet<string>? saved)
    {
        if (saved is null) return declared;
        return declared.Where(saved.Contains).Concat(saved.Where(uri => _registry.OwnerOf(uri)?.Id == sourceId && SetOf(uri) == set));
    }
    static string? SetOf(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Track => "liked", EntityKind.Album => "albums", EntityKind.Artist => "artists",
        EntityKind.Show => "shows", EntityKind.Episode => "episodes", EntityKind.Playlist => "playlists", _ => null,
    };

    static void AppendTree(IReadOnlyList<PlaylistNode> nodes, string sourceId, List<RootlistEntry> rows, int depth, Func<string, bool> include)
    {
        if (depth > SidebarTree.MaxDepth) throw new ArgumentException("Native playlist folders exceed the supported depth.");
        foreach (var node in nodes)
        {
            if (node is PlaylistLeaf leaf && include(leaf.Playlist.Uri))
                rows.Add(new(rows.Count, 0, leaf.Playlist.Uri, null, depth, leaf.AddedAtMs));
            else if (node is PlaylistFolder folder)
            {
                var id = sourceId + "-" + Uri.EscapeDataString(folder.Id);
                rows.Add(new(rows.Count, 1, "native:start-group:" + id, folder.Name, depth));
                AppendTree(folder.Items, sourceId, rows, depth + 1, include);
                rows.Add(new(rows.Count, 2, "native:end-group:" + id, null, depth));
            }
        }
    }

    async Task PumpAsync()
    {
        try
        {
            await foreach (var _ in _refresh.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                try { await RefreshAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error) { _errors.OnNext(error); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _lifetime.Cancel(); _refresh.Writer.TryComplete();
        if (_worker is { } worker) await worker.ConfigureAwait(false);
        _lifetime.Dispose(); _refreshGate.Dispose();
    }
    sealed class SourceObserver<T>(Action<T> next) : IObserver<T>
    { public void OnNext(T value) => next(value); public void OnError(Exception error) { } public void OnCompleted() { } }
}
