using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;

namespace Wavee.Tests;

// Shared harness for the hydration façade tests (docs/plans/wavee/hydration-facade-design.md §4). Every fake records
// what it was ASKED for rather than scripting a reply, because the properties under test are all shape properties:
// how many POSTs a batch costs, which uris rode which one, and what a second ask does.

/// <summary>Records every catalogue pass and (optionally) writes the rows a real projection would have written.</summary>
public sealed class FakeCatalogFetch : ICatalogFetch
{
    readonly IStore _store;
    readonly Action<IReadOnlyList<EntityUri>, IStore>? _project;
    readonly object _gate = new();

    public FakeCatalogFetch(IStore store, Action<IReadOnlyList<EntityUri>, IStore>? project = null)
    {
        _store = store;
        _project = project;
    }

    public int Calls { get; private set; }
    public List<List<string>> Batches { get; } = new();
    public List<(string Uri, int Kind)> Extras { get; } = new();
    public List<TraitSurface> Surfaces { get; } = new();
    /// <summary>Every uri ever asked for, across all passes.</summary>
    public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);

    public List<bool> Revalidates { get; } = new();

    public Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
        IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
    {
        var landed = new List<string>(uris.Count);
        lock (_gate)
        {
            Calls++;
            var batch = new List<string>(uris.Count);
            for (int i = 0; i < uris.Count; i++) { batch.Add(uris[i].Uri); Asked.Add(uris[i].Uri); landed.Add(uris[i].Uri); }
            Batches.Add(batch);
            Surfaces.Add(surface);
            Revalidates.Add(revalidate);
            if (extraKinds is not null) Extras.AddRange(extraKinds);
        }
        _project?.Invoke(uris, _store);
        return Task.FromResult<IReadOnlyCollection<string>>(landed);
    }
}

// FakeEnvelopeFetch and RecordingTraitPipeline live in HydrationLadderFakes.cs — ONE double per port across the
// whole hydration suite, so a ladder test and a façade test cannot drift on what "the transport answered" means.

/// <summary>A ladder whose whole answer IS step 0 — the shared catalogue POST and nothing after it. Lets a façade
/// test register a KIND (so the batch is not answered Unsupported) without pulling in that kind's real ladder.</summary>
public sealed class CatalogOnlyHydration : IKindHydration
{
    readonly OfflineEntityHydrator _levels;
    public CatalogOnlyHydration(EntityKind kind, IStore store) { Kind = kind; _levels = new OfflineEntityHydrator(store); }
    public EntityKind Kind { get; }
    public HydrationLevel LevelOf(string uri) => _levels.LevelOf(uri);
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }
    public Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                              HydrationContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Counts the three playlist-plane operations without touching the plane.</summary>
public sealed class FakePlaylistOpener : IPlaylistOpener
{
    public Action<string>? OnOpen;
    public int OpenCalls, RevalidateCalls, HeaderCalls;

    public Task OpenAsync(string playlistUri, CancellationToken ct)
    { Interlocked.Increment(ref OpenCalls); OnOpen?.Invoke(playlistUri); return Task.CompletedTask; }

    public void Revalidate(string playlistUri) => Interlocked.Increment(ref RevalidateCalls);

    public Task HeaderAsync(string playlistUri, CancellationToken ct)
    { Interlocked.Increment(ref HeaderCalls); return Task.CompletedTask; }
}

public static class HydrationTestSupport
{
    public static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    public static Func<SessionContext> Session => () => Ctx;

    /// <summary>Row that reads back as exactly <paramref name="level"/> through <c>HydrationLevels.Of</c>.</summary>
    public static Track TrackAt(string uri, HydrationLevel level)
    {
        string id = EntityUri.IdOf(uri);
        if (level == HydrationLevel.None) return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
        if (level == HydrationLevel.Identity)
            return new Track(id, uri, "Song " + id, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
        var full = new Track(id, uri, "Song " + id,
            [new ArtistRef("a1", "spotify:artist:a1", "Artist One")],
            new AlbumRef("al1", "spotify:album:al1", "Album One"),
            210_000, false, new Image("https://i.scdn.co/image/" + id));
        return level >= HydrationLevel.Full ? full with { Availability = Availability.Playable } : full;
    }

    public static Episode EpisodeAt(string uri, HydrationLevel level)
    {
        string id = EntityUri.IdOf(uri);
        if (level == HydrationLevel.None) return new Episode(id, uri, uri, "", null, 0, DateTimeOffset.UnixEpoch);
        if (level == HydrationLevel.Identity) return new Episode(id, uri, "Ep " + id, "", null, 0, DateTimeOffset.UnixEpoch);
        return new Episode(id, uri, "Ep " + id, "The Show", new Image("https://i.scdn.co/image/" + id),
            1_800_000, DateTimeOffset.UnixEpoch);
    }

    /// <summary>Wait for the pump to go quiet. Jobs enqueue further jobs, so "quiet" has to hold twice in a row.</summary>
    public static async Task DrainAsync(HydrationPump pump, int timeoutMs = 5000)
    {
        int quiet = 0;
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (pump.Pending == 0 && pump.Running == 0) { if (++quiet >= 3) return; }
            else quiet = 0;
            await Task.Delay(10);
        }
    }

    /// <summary>A hydrator over whatever ladders the test registers, with a pump the test can drain.</summary>
    public static SpotifyProviderHydrator Hydrator(IStore store, ICatalogFetch catalog, ITraitPipeline traits,
        HydrationPump pump, IReadOnlyList<IKindHydration> ladders, HydrationPolicy? policy = null,
        TraitPolicy? traitPolicy = null)
        => new(store, Session, catalog, traits, traitPolicy ?? new TraitPolicy(),
               policy ?? HydrationPolicy.Default, ladders, pump);
}
