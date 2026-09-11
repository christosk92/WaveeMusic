using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Popular-track rows read live artist queries. Only configured sections retain query handles.</summary>
public sealed class SidebarArtistTopTracksSource : SidebarDataSourceBase,
    ISidebarDataSourceLifecycle, ISidebarDataSourceDemandLifecycle
{
    readonly IQueryService? _queries;
    readonly Func<CatalogScope>? _readScope;
    readonly Dictionary<string, ArtistView> _artists = new(StringComparer.Ordinal);
    readonly List<string> _removed = [];
    Action<Action>? _post;
    CatalogScope? _scope;
    bool _active;

    sealed class ArtistView(QuerySignalBinding<Artist> binding)
    {
        public readonly QuerySignalBinding<Artist> Binding = binding;
        public int Wanted;
    }

    readonly TimeProvider _time;

    public SidebarArtistTopTracksSource(IQueryService? queries, Func<CatalogScope>? scope, TimeProvider? time = null)
        : base(SidebarContributions.ArtistTopTracks)
    { _queries = queries; _readScope = scope; _time = time ?? TimeProvider.System; }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;
    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "sidebar.source.artistTopTracks.artist",
            Required: true),
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.artistTopTracks.maxItems",
            DefaultJson: "5", Min: 1, Max: 50),
    ]);

    public void Attach(Action<Action> post) { _post = post; _active = true; }

    public void Detach()
    {
        _active = false; _post = null;
        Clear();
    }

    public void BeginDemandPass()
    {
        var scope = _readScope?.Invoke();
        if (_scope != scope) { Clear(); _scope = scope; }
        foreach (var view in _artists.Values) view.Wanted = 0;
    }

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        string? uri = request.Config.Str("artistUri");
        if (string.IsNullOrEmpty(uri) || _queries is null || _scope is null || _post is null) return;
        if (!_artists.TryGetValue(uri, out var view))
        {
            // Acquire is passive; EndDemandPass applies the union of section demands after the pure fill.
            view = new(new QuerySignalBinding<Artist>(_queries.Acquire(new ArtistDetailQuery(_scope, uri)), _post,
                _ => Raise(), time: _time));
            _artists.Add(uri, view);
        }
        view.Wanted = Math.Max(view.Wanted, Max(request));
    }

    public void EndDemandPass()
    {
        _removed.Clear();
        foreach (var pair in _artists)
        {
            if (pair.Value.Wanted == 0) { pair.Value.Binding.Dispose(); _removed.Add(pair.Key); continue; }
            pair.Value.Binding.SetDemand(new QueryDemand(true, QueryPriority.Visible, [FacetKind.ArtistPopular]));
            pair.Value.Binding.SetActive(_active);
        }
        foreach (string uri in _removed) _artists.Remove(uri);
    }

    public void SetActive(bool active)
    {
        _active = active;
        foreach (var view in _artists.Values) view.Binding.SetActive(active);
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        string? uri = request.Config.Str("artistUri");
        if (string.IsNullOrEmpty(uri))
        { SetHealthQuiet(SidebarSourceState.Ready, "sidebar.source.artistTopTracks.unset"); return 0; }
        if (_queries is null || _readScope is null) { SetHealthQuiet(SidebarSourceState.Ready); return 0; }
        if (!_artists.TryGetValue(uri, out var view)) { SetHealthQuiet(SidebarSourceState.Pending); return 0; }
        var snapshot = view.Binding.Snapshot.Peek();
        SetHealthQuiet(snapshot.Status.HasPrimaryData ? SidebarSourceState.Ready
            : !snapshot.Status.IsRefreshing && snapshot.Problems.Count > 0 ? SidebarSourceState.Error
            : SidebarSourceState.Pending);
        return snapshot.Status.HasPrimaryData
            ? SidebarSourceMap.Tracks(snapshot.Value.TopTracks, into, Max(request)) : 0;
    }

    static int Max(in SidebarSourceRequest request)
        => Math.Clamp(request.MaxItems > 0 ? request.MaxItems : request.Config.Int("maxItems", 5), 1, 50);

    void Clear()
    {
        foreach (var view in _artists.Values) view.Binding.Dispose();
        _artists.Clear(); _removed.Clear();
    }
}
