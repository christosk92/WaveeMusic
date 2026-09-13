using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Features.Concerts;

namespace Wavee;

// The three FETCHING first-party sources — wavee.newReleases, wavee.concerts, wavee.artist.topTracks.
//
// DEGRADATION IS THE CONTRACT: a null/offline service, a non-2xx, a parse failure and an outright throw all resolve to a
// state the sidebar can draw (Ready+empty, Pending, an actionable prompt, or Error) — never an exception out of Fill and
// never a permanent skeleton. Offline the switchable services are their Null implementations, which return null/empty, so
// "no live session" is just an empty feed.
//
// THREADING: every fetch completes on a pool thread and marshals through the injected `post` before touching state; Fill
// itself only reads already-published rows.

/// <summary>A source that owns async work and therefore needs the binder's UI-thread marshaller. The binder calls
/// <see cref="Attach"/> from <c>Start</c> and <see cref="Detach"/> on teardown.</summary>
public interface ISidebarDataSourceLifecycle
{
    void Attach(Action<Action> post);
    void Detach();
}

/// <summary><c>wavee.newReleases</c> — new releases/episodes from followed artists (the What's New feed). A null service
/// (or an offline one) is an EMPTY section, not an error: <c>SidebarSourceMap.FromFeedState</c> maps Offline → Ready.</summary>
public sealed class SidebarNewReleasesSource : SidebarDataSourceBase, ISidebarDataSourceLifecycle
{
    readonly IWhatsNewService? _service;
    readonly ISidebarProjectionSnapshot _snapshot;
    IDisposable? _sub;

    public SidebarNewReleasesSource(IWhatsNewService? service, ISidebarProjectionSnapshot snapshot)
        : base(SidebarContributions.NewReleases)
    {
        _service = service;
        _snapshot = snapshot;
        if (service is null) SetHealthQuiet(SidebarSourceState.Ready);   // no provider ⇒ permanently empty, never pending
    }

    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.newReleases.maxItems",
            DefaultJson: "4", Min: 1, Max: 20),
    ]);

    public void Attach(Action<Action> post)
    {
        if (_service is null || _sub is not null) return;
        // The feed's Changed is an IObservable that fires off the fetch thread — marshal before raising.
        _sub = _service.Changed.Subscribe(new FeedObserver(() => post(Publish)));
    }

    public void Detach()
    {
        _sub?.Dispose();
        _sub = null;
    }

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (_service is null) return;
        // Quiet on purpose: EnsureFresh runs INSIDE the binder's rebuild, and raising Changed there would re-enter it.
        // The Fill that follows publishes the verdict; the observer wired in Attach handles the async arrival.
        try { _service.EnsureFresh(); } catch (Exception) { /* a feed refresh is never fatal */ }
    }

    void Publish()
    {
        if (_service is null) return;
        SetHealth(SidebarSourceMap.FromFeedState(_service.State));
        Raise();
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        if (_service is null) { SetHealthQuiet(SidebarSourceState.Ready); return 0; }
        IReadOnlyList<NewReleaseNotification>? snapshot = null;
        var state = SidebarSourceState.Error;
        try
        {
            snapshot = _service.Snapshot;
            state = SidebarSourceMap.FromFeedState(_service.State);
        }
        catch (Exception) { /* a misbehaving provider degrades to Error + whatever the cache holds */ }
        SetHealthQuiet(state);
        return SidebarSourceMap.NewReleases(snapshot, _snapshot.Index, into,
            SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 4));
    }

    sealed class FeedObserver(Action onNext) : IObserver<int>
    {
        public void OnNext(int value) => onNext();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>
/// <c>wavee.concerts</c> — upcoming events near the user, cached in the source (there is no ambient concert hub cache: the
/// hub page fetches per mount, so a sidebar section must own its own small, TTL'd snapshot).
///
/// <para>NO LOCATION IS AN ACTIONABLE STATE, not an empty one: <c>NeedsPrompt</c> goes true and the planner draws one
/// <c>PromptRow</c> ("Set your location"). That is also the offline path — <c>NullConcertService.GetUserLocationAsync</c>
/// returns null — so a logged-out sidebar shows the prompt rather than a dead empty section.</para>
/// </summary>
public sealed class SidebarConcertsSource : SidebarDataSourceBase, ISidebarDataSourceLifecycle
{
    /// <summary>Refresh window. Concert feeds move on the scale of days; a sidebar strip re-asking more often than this is
    /// pure network noise.</summary>
    public const int RefreshMinutes = 30;

    /// <summary>Hard bound on the cached snapshot — a sidebar section is a top-N surface.</summary>
    public const int SnapshotCap = 20;

    readonly IConcertService? _service;
    readonly List<SidebarLibraryEntry> _rows = new(SnapshotCap);
    Action<Action>? _post;
    long _fetchedAtTicks;
    bool _inFlight;
    CancellationTokenSource? _cts;

    public SidebarConcertsSource(IConcertService? service) : base(SidebarContributions.Concerts)
    {
        _service = service;
        SetHealthQuiet(service is null
            ? SidebarSourceState.Ready               // no provider at all ⇒ an empty section, honestly
            : SidebarSourceState.Pending);           // we have not asked yet
    }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Event;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.concerts.maxItems",
            DefaultJson: "3", Min: 1, Max: 20),
        new SidebarConfigField("radiusKm", SidebarConfigFieldKind.Int, "sidebar.source.concerts.radiusKm",
            DefaultJson: "100", Min: 1, Max: 500),
    ]);

    public void Attach(Action<Action> post) => _post = post;

    public void Detach()
    {
        _cts?.Cancel();
        _cts = null;
        _post = null;
    }

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (_service is null || _inFlight) return;
        long now = Environment.TickCount64;
        if (_fetchedAtTicks != 0 && now - _fetchedAtTicks < RefreshMinutes * 60_000L) return;

        int radius = request.Config.Int("radiusKm", 100);
        _inFlight = true;
        _fetchedAtTicks = now;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = FetchAsync(radius, _cts.Token);
    }

    async Task FetchAsync(int radiusKm, CancellationToken ct)
    {
        ConcertPlace? place = null;
        List<SidebarLibraryEntry>? rows = null;
        var state = SidebarSourceState.Ready;
        try
        {
            place = await _service!.GetUserLocationAsync(ct).ConfigureAwait(false);
            if (place is not null)
            {
                var page = await _service!.GetFeedAsync(new ConcertFeedQuery(place, null, radiusKm), ct)
                                          .ConfigureAwait(false);
                rows = Map(page);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { state = SidebarSourceState.Error; }

        Post(() =>
        {
            _inFlight = false;
            _rows.Clear();
            if (rows is not null) for (int i = 0; i < rows.Count; i++) _rows.Add(rows[i]);
            // Location unset is the ONE actionable degraded state; everything else is Ready/Error with whatever we have.
            if (place is null && state != SidebarSourceState.Error)
                SetHealth(SidebarSourceState.Ready, "sidebar.concerts.setLocation", needsPrompt: true);
            else
                SetHealth(state);
            Raise();
        });
    }

    // Sections in provider order (Nearby ▸ Recommended ▸ All events), deduped by canonical uri, then soonest first.
    static List<SidebarLibraryEntry> Map(ConcertFeedPage? page)
    {
        var rows = new List<SidebarLibraryEntry>(SnapshotCap);
        if (page is null) return rows;
        var sections = page.Sections;
        for (int i = 0; i < sections.Count && rows.Count < SnapshotCap; i++)
        {
            var concerts = sections[i].Concerts;
            for (int j = 0; j < concerts.Count && rows.Count < SnapshotCap; j++)
            {
                var c = concerts[j];
                if (string.IsNullOrWhiteSpace(c.Uri)) continue;
                string route = ConcertRoutes.Detail(c.Uri);
                if (Has(rows, route)) continue;
                rows.Add(SidebarSourceMap.FromEvent(route, c.Title ?? c.Venue, c.Venue,
                    c.Date.ToUnixTimeMilliseconds(), c.Image, rows.Count));
            }
        }
        rows.Sort(static (a, b) => a.SortStamp.CompareTo(b.SortStamp));   // soonest first
        return rows;
    }

    static bool Has(List<SidebarLibraryEntry> rows, string id)
    {
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 3);
        int n = _rows.Count < max ? _rows.Count : max;
        for (int i = 0; i < n; i++) into.Add(_rows[i]);
        return n;
    }

    void Post(Action action)
    {
        var post = _post;
        if (post is null) action();          // no marshaller attached (a headless/host-less run): run inline
        else post(action);
    }
}

/// <summary>
/// <c>wavee.artist.topTracks</c> — an artist's popular tracks, over the existing artist-popular backend (the overview
/// seed merged with the spclient extensions). Config: <c>{ artistUri, maxItems }</c>.
///
/// <para>KEYED BY ARTIST: one registered source serves any number of sections, each configured for a different artist, so
/// the rows AND the health verdict are per-artist. <see cref="SidebarDataSourceBase.State"/> reports the artist of the most
/// recent <c>Fill</c> — which is exactly the artist whose slice the resolver is about to record.</para>
/// </summary>
public sealed class SidebarArtistTopTracksSource : SidebarDataSourceBase, ISidebarDataSourceLifecycle
{
    /// <summary>How many artists' charts stay resident. A sidebar with more spotlighted artists than this is not a
    /// sidebar; the oldest simply re-fetches.</summary>
    public const int ArtistCacheCap = 8;

    readonly IMusicLibrary? _library;
    readonly Dictionary<string, List<SidebarLibraryEntry>> _byArtist = new(StringComparer.Ordinal);
    readonly Dictionary<string, SidebarSourceState> _stateByArtist = new(StringComparer.Ordinal);
    readonly List<string> _lru = new();
    Action<Action>? _post;

    public SidebarArtistTopTracksSource(IMusicLibrary? library)
        : base(SidebarContributions.ArtistTopTracks)
        => _library = library;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "sidebar.source.artistTopTracks.artist",
            Required: true),
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.artistTopTracks.maxItems",
            DefaultJson: "5", Min: 1, Max: 50),
    ]);

    public void Attach(Action<Action> post) => _post = post;
    public void Detach() => _post = null;

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        string? artist = request.Config.Str("artistUri");
        if (string.IsNullOrEmpty(artist) || _library is null) return;
        if (_stateByArtist.ContainsKey(artist)) return;      // already resolved or in flight
        _stateByArtist[artist] = SidebarSourceState.Pending;
        Touch(artist);
        _ = FetchAsync(artist, request.Config.Int("maxItems", 5));
    }

    async Task FetchAsync(string artistUri, int max)
    {
        IReadOnlyList<Track>? tracks = null;
        var state = SidebarSourceState.Ready;
        try
        {
            // The FULL rung IS the extended chart (overview seed ∪ artist-top-tracks-extensions, with counts) —
            // one ask through the catalog replaces the seed-then-extend two-service dance. Offline the ladder stops at
            // whatever is resident, so the pane renders the seed rather than nothing.
            var artist = await _library!.GetArtistAsync(artistUri, HydrationLevel.Full).ConfigureAwait(false);
            tracks = artist?.TopTracks ?? Array.Empty<Track>();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { state = SidebarSourceState.Error; }

        Post(() =>
        {
            if (!_byArtist.TryGetValue(artistUri, out var rows)) _byArtist[artistUri] = rows = new List<SidebarLibraryEntry>(max);
            rows.Clear();
            SidebarSourceMap.Tracks(tracks, rows, ArtistPopularTracks.ExtendedCap);
            _stateByArtist[artistUri] = state;
            Touch(artistUri);
            SetHealth(state);
            Raise();
        });
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        string? artist = request.Config.Str("artistUri");
        if (string.IsNullOrEmpty(artist))
        {
            // An unconfigured section is not broken — it is waiting for the customizer to pick an artist.
            SetHealthQuiet(SidebarSourceState.Ready, "sidebar.source.artistTopTracks.unset");
            return 0;
        }

        var state = _stateByArtist.TryGetValue(artist, out var s) ? s : SidebarSourceState.Pending;
        SetHealthQuiet(state);
        if (!_byArtist.TryGetValue(artist, out var rows) || rows.Count == 0) return 0;

        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5);
        int n = rows.Count < max ? rows.Count : max;
        for (int i = 0; i < n; i++) into.Add(rows[i]);
        return n;
    }

    void Touch(string artistUri)
    {
        _lru.Remove(artistUri);
        _lru.Add(artistUri);
        while (_lru.Count > ArtistCacheCap)
        {
            string oldest = _lru[0];
            _lru.RemoveAt(0);
            _byArtist.Remove(oldest);
            _stateByArtist.Remove(oldest);
        }
    }

    void Post(Action action)
    {
        var post = _post;
        if (post is null) action();
        else post(action);
    }
}
