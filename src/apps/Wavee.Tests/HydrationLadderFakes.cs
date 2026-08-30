using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;

namespace Wavee.Tests;

// Shared doubles for the per-kind ladders (AlbumHydrationTests / ArtistHydrationTests). Every one of them RECORDS what
// it was asked for, because the interesting properties of a ladder are request-shaped: "one repair batch, not N",
// "traits asked ONCE with this set and this surface", "a fresh artist asks for no overview at all". They are also all
// projectors: a ladder step is only meaningful if the next step can see what the previous one landed, so the fakes
// write into the same InMemoryStore the ladder reads.

/// <summary>The façade the ladders recurse through (<c>ctx.Hydrator</c>). Records every batch and lets a test decide
/// what "the transport landed" means by writing rows in <see cref="OnEnsureMany"/>.</summary>
sealed class RecordingHydrator : IEntityHydrator
{
    readonly OfflineEntityHydrator _levels;
    public RecordingHydrator(IStore store) => _levels = new OfflineEntityHydrator(store);

    public List<(IReadOnlyList<string> Uris, HydrationLevel Level, TraitSurface Surface)> Batches { get; } = new();
    /// <summary>The FULL options of each batch, parallel to <see cref="Batches"/>. Blocking-vs-background is a policy
    /// decision (OpenPolicy) that callers make, so a caller test has to be able to see it.</summary>
    public List<HydrationOptions> Options { get; } = new();
    /// <summary>Every <c>EnsureTraitsAsync</c> pass: (uris, surface). A surface asking for its traits is an assertable
    /// contract in its own right (the queue, the Plays toggle, search).</summary>
    public List<(IReadOnlyList<string> Uris, TraitSurface Surface)> TraitCalls { get; } = new();
    /// <summary>What the batch resolves to, applied before the call returns (the ladder reads the store right after).</summary>
    public Action<IReadOnlyList<string>>? OnEnsureMany { get; set; }
    public bool Throw { get; set; }

    public HydrationLevel LevelOf(string uri) => _levels.LevelOf(uri);

    public async Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level, HydrationOptions opts = default,
        CancellationToken ct = default)
    {
        await EnsureManyAsync(new[] { uri }, level, opts, ct).ConfigureAwait(false);
        var reached = LevelOf(uri);
        return new HydrationOutcome(reached, reached >= level ? HydrationStatus.Reached : HydrationStatus.Partial);
    }

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        Batches.Add((new List<string>(uris), level, opts.Surface));
        Options.Add(opts);
        if (Throw) throw new InvalidOperationException("transport down");
        OnEnsureMany?.Invoke(uris);
        return Task.FromResult(new HydrationBatchOutcome(uris, Array.Empty<string>(), HydrationStatus.Reached));
    }

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
    { TraitCalls.Add((new List<string>(uris), surface)); return Task.CompletedTask; }
    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
    { TraitCalls.Add((new List<string>(uris), surface)); return Task.CompletedTask; }
    public void Invalidate(string uri) { }
}

/// <summary>The ONE trait door (<c>ctx.Traits</c>). P2 replaces the implementation; the ladders' contract with it — one
/// call, this set, this surface, these uris — is what these tests pin.</summary>
sealed class RecordingTraitPipeline : ITraitPipeline
{
    public List<(IReadOnlyList<string> Uris, TraitSet Traits, TraitSurface Surface)> Calls { get; } = new();
    /// <summary>Stands in for the kind-185 projection: the counts the pipeline writes onto the shared track rows.</summary>
    public Action<IReadOnlyList<string>>? OnEnsure { get; set; }
    public bool Throw { get; set; }

    public Task EnsureAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
    {
        Calls.Add((new List<string>(uris), traits, surface));
        if (Throw) throw new InvalidOperationException("traits down");
        OnEnsure?.Invoke(uris);
        return Task.CompletedTask;
    }
}

/// <summary>The Pathfinder port. Counts calls per uri so "getAlbum fired once, and only for the album V4 could not
/// open" is directly assertable.</summary>
sealed class FakeEnvelopeFetch : IEnvelopeFetch
{
    public Func<string, Album?>? OnAlbum { get; set; }
    public Func<string, Track?>? OnTrack { get; set; }
    public Func<string, Artist?>? OnOverview { get; set; }
    public bool Throw { get; set; }

    public List<string> AlbumCalls { get; } = new();
    public List<string> TrackCalls { get; } = new();
    public List<string> OverviewCalls { get; } = new();

    public Task<Album?> AlbumAsync(string albumUri, CancellationToken ct)
    {
        AlbumCalls.Add(albumUri);
        if (Throw) throw new InvalidOperationException("getAlbum down");
        return Task.FromResult(OnAlbum?.Invoke(albumUri));
    }

    public Task<Track?> TrackAsync(string trackUri, CancellationToken ct)
    {
        TrackCalls.Add(trackUri);
        if (Throw) throw new InvalidOperationException("getTrack down");
        return Task.FromResult(OnTrack?.Invoke(trackUri));
    }

    public Task<Artist?> ArtistOverviewAsync(string artistUri, CancellationToken ct)
    {
        OverviewCalls.Add(artistUri);
        if (Throw) throw new InvalidOperationException("queryArtistOverview down");
        return Task.FromResult(OnOverview?.Invoke(artistUri));
    }
}

/// <summary>The spclient chart port.</summary>
sealed class FakeArtistChartFetch : IArtistChartFetch
{
    public Func<string, IReadOnlyList<string>>? OnUris { get; set; }
    public bool Throw { get; set; }
    public List<string> Calls { get; } = new();

    public Task<IReadOnlyList<string>> TopTrackUrisAsync(string artistUri, CancellationToken ct)
    {
        Calls.Add(artistUri);
        if (Throw) throw new InvalidOperationException("chart down");
        return Task.FromResult(OnUris?.Invoke(artistUri) ?? Array.Empty<string>());
    }
}

/// <summary>A real <see cref="HydrationContext"/> over the doubles — a real pump (so a post-step really is asynchronous)
/// and the real default policy (so the artist TTL under test is the shipping one).</summary>
sealed class LadderHarness : IDisposable
{
    readonly CancellationTokenSource _cts = new();

    public LadderHarness(HydrationPolicy? policy = null)
    {
        Store = new InMemoryStore();
        Hydrator = new RecordingHydrator(Store);
        Traits = new RecordingTraitPipeline();
        Envelopes = new FakeEnvelopeFetch();
        Chart = new FakeArtistChartFetch();
        Pump = new HydrationPump(_cts.Token);
        Ctx = new HydrationContext(Store, Hydrator, Traits, Pump, policy ?? HydrationPolicy.Default, default);
    }

    public InMemoryStore Store { get; }
    public RecordingHydrator Hydrator { get; }
    public RecordingTraitPipeline Traits { get; }
    public FakeEnvelopeFetch Envelopes { get; }
    public FakeArtistChartFetch Chart { get; }
    public HydrationPump Pump { get; }
    public HydrationContext Ctx { get; }

    public static IReadOnlyList<EntityUri> Batch(params string[] uris)
    {
        var list = new List<EntityUri>(uris.Length);
        foreach (var u in uris) list.Add(EntityUri.Parse(u));
        return list;
    }

    /// <summary>Drain the pump: post-steps are enqueued, not awaited, so a test that asserts on one has to wait for it.</summary>
    public async Task DrainAsync()
    {
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(5).ConfigureAwait(false);
            if (Pump.Pending == 0 && Pump.Running == 0) return;
        }
    }

    public void Dispose() { Pump.Dispose(); _cts.Dispose(); }
}
