using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── The transport seams the engine talks to (design §2.2) ────────────────────────────────────────────────────────────
// The engine (router, ledger, pump, per-kind ladders) lives in Backend and must stay engine-free and provider-neutral in
// SHAPE; the Spotify transports (extended-metadata, Pathfinder, spclient REST, the LibrarySync writer loop) implement
// these ports from SpotifyLive/Hydration. Every port returns MAPPED domain objects or "what landed" — no GraphQL/proto
// type crosses (architecture.md §4.4). A second provider adds its own implementations; nothing here changes.

/// <summary>The catalogue arm: extended-metadata V4/205 for a mixed-kind uri batch, conditional (etag) and chunked
/// (<see cref="Wavee.Backend.Metadata.MetadataChunking"/> — 300 entities / body bytes) — ONE POST per chunk regardless
/// of how many kinds ride it. <paramref name="extraKinds"/> are (uri, ExtensionKind) pairs a ladder wants FUSED under
/// the same uri group in the same POST (album Rich adds kind 183). Returns the uris whose PROJECTION wrote an entity —
/// never merely "requested" — so the ledger seals on outcome (the contract the deleted IMetadataSource seam had).
/// <param name="revalidate">Mirrors <see cref="HydrationOptions.Revalidate"/>: a user-initiated open, not merely a
/// batch the ledger happened to miss. The ledger bypass alone is not enough — the etag cache underneath this call has
/// its own multi-hour TTL and would otherwise silently re-project a stale cached body (a rolling-identity playlist's
/// LIST_METADATA_V2 sitting inside its 6h window while the edition it names already rolled over server-side). True
/// forces every (uri, kind) this call touches through a real conditional GET instead.</param></summary>
public interface ICatalogFetch
{
    Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris, IReadOnlyList<(string Uri, int Kind)>? extraKinds,
                                                TraitSurface surface, CancellationToken ct, bool revalidate = false);
}

/// <summary>Pathfinder envelopes. Each returns the MAPPED domain object (or null on a miss); the ladder decides what
/// to write and how. TTL/dedup of the underlying GraphQL call belongs to the implementation (PathfinderResource).</summary>
public interface IEnvelopeFetch
{
    /// <summary>getAlbum — the Full envelope (label/©℗/OtherVersions/MoreBy/ArtistsDetailed/playability, first 50 rows).</summary>
    Task<Album?> AlbumAsync(string albumUri, CancellationToken ct);
    /// <summary>getTrack — the now-playing repair for a row TrackV4 left thin (album name / cover), plus the availability verdict.</summary>
    Task<Track?> TrackAsync(string trackUri, CancellationToken ct);
    /// <summary>queryArtistOverview — stats + popular releases + top ~10 with play counts + related. THE ONE caller.</summary>
    Task<Artist?> ArtistOverviewAsync(string artistUri, CancellationToken ct);
}

/// <summary>spclient <c>artist-top-tracks-extensions</c>: the artist's full popular list as bare uris (~50).</summary>
public interface IArtistChartFetch
{
    Task<IReadOnlyList<string>> TopTrackUrisAsync(string artistUri, CancellationToken ct);
}

/// <summary>The playlist plane is owned by the LibrarySync writer loop (dealer, diff, mutations); the playlist ladder
/// only ASKS it. <see cref="OpenAsync"/> = blocking first open (no baseline); <see cref="Revalidate"/> = enqueue the
/// loop's own SWR (its 5-minute window / dirty set decide whether anything is fetched); <see cref="HeaderAsync"/> =
/// the header-only GET a rootlist member uses for Identity.</summary>
public interface IPlaylistOpener
{
    Task OpenAsync(string playlistUri, CancellationToken ct);
    void Revalidate(string playlistUri);
    Task HeaderAsync(string playlistUri, CancellationToken ct);
}

/// <summary>User profiles (kind 15 batch + REST fallback) → mapped <see cref="Owner"/>s (null = not resolvable).</summary>
public interface IUserProfileFetch
{
    Task<IReadOnlyDictionary<string, Owner?>> ResolveAsync(IReadOnlyList<string> userIds, CancellationToken ct);
}

/// <summary>Per-playable traits (video / adornments / play counts / publishing …). P2 lands the real pipeline (design
/// §2.4); P1 ships an adapter over the existing trait services so the ladders can already ask through ONE door.</summary>
public interface ITraitPipeline
{
    Task EnsureAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default);
}

/// <summary>ONE kind's ladder: the steps that raise an entity of that kind to a rung. The provider hydrator runs step 0
/// (the shared catalogue POST for the whole mixed batch) and then hands each kind its uris. The ladder writes the store,
/// never mints rows it cannot fill, and reads back its own progress through <see cref="HydrationLevels"/>.</summary>
public interface IKindHydration
{
    EntityKind Kind { get; }

    /// <summary>Presence-only rung of the resident entity (store-backed <see cref="HydrationLevels.Of"/>).</summary>
    HydrationLevel LevelOf(string uri);

    /// <summary>Extra extension kinds to FUSE into the step-0 catalogue POST for this level (album Rich → 183). Empty
    /// for most kinds/levels; the batch stays one POST either way.</summary>
    void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into);

    /// <summary>Continue the ladder for THIS kind's uris after step 0 landed (or was skipped as fresh): repairs, second
    /// transports, assembles, awaited traits per <c>OpenPolicy</c>. Post-steps that the level does not wait on go on
    /// <see cref="HydrationContext.Pump"/>. Best-effort per uri; a transport failure is logged and leaves the entity at
    /// whatever rung it reached — the caller re-reads <see cref="LevelOf"/> and seals accordingly.</summary>
    Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts, HydrationContext ctx, CancellationToken ct);
}

/// <summary>The FAILURE CHANNEL for one ladder run — the thing a best-effort step has no other way to say.
///
/// <para>A ladder step is best-effort by contract: a getAlbum that 503s, a trait POST whose socket died, an overview
/// that timed out are all logged and swallowed so a renderable page still paints. But the hydrator then re-reads
/// <c>LevelOf</c>, sees the rung was not reached, and seals EXHAUSTED — and an exhausted seal means "we asked; this is
/// genuinely all there is", which for an album's Rich rung is a 24-hour verdict. A one-second blip therefore cost the
/// ©/℗ line and the row bundle for a day, and no later ask could tell the difference between "this release carries no
/// publishing facet" and "the network hiccuped once".</para>
///
/// <para>So a step that swallowed a TRANSPORT failure says so here, and the hydrator seals that uri on the SHORT
/// exhausted TTL instead. Absent = the ladder ran clean and the answer really is "nothing more exists".</para></summary>
public sealed class HydrationRunScope
{
    readonly object _gate = new();
    HashSet<string>? _transient;

    /// <summary>Note that a step for <paramref name="uri"/> swallowed a transport failure this run. Thread-safe: the
    /// per-kind continuations are sequential today but a ladder's own steps need not be.</summary>
    public void Report(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        lock (_gate) (_transient ??= new HashSet<string>(StringComparer.Ordinal)).Add(uri);
    }

    public bool WasTransient(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        lock (_gate) return _transient is not null && _transient.Contains(uri);
    }

    /// <summary>How many uris reported a transient failure (the diagnostic the batch log carries).</summary>
    public int Count { get { lock (_gate) return _transient?.Count ?? 0; } }
}

/// <summary>What every ladder step can reach: the store, the façade (for recursion — a ladder never calls another
/// ladder directly), the trait pipeline, the background pump, the log, and this run's failure channel. The seam-holding
/// instance is built once per provider hydrator; <see cref="ForRun"/> mints the cheap per-batch view the ladders
/// actually receive, so <see cref="ReportTransient"/> can never leak across runs.</summary>
public sealed class HydrationContext
{
    public HydrationContext(IStore store, IEntityHydrator hydrator, ITraitPipeline traits, HydrationPump pump, HydrationPolicy policy, WaveeLogger log)
        : this(store, hydrator, traits, pump, policy, log, new HydrationRunScope()) { }

    HydrationContext(IStore store, IEntityHydrator hydrator, ITraitPipeline traits, HydrationPump pump,
                     HydrationPolicy policy, WaveeLogger log, HydrationRunScope run)
    {
        Store = store; Hydrator = hydrator; Traits = traits; Pump = pump; Policy = policy; Log = log; Run = run;
    }

    public IStore Store { get; }
    public IEntityHydrator Hydrator { get; }
    public ITraitPipeline Traits { get; }
    public HydrationPump Pump { get; }
    public HydrationPolicy Policy { get; }
    public WaveeLogger Log { get; }
    /// <summary>This run's failure channel. Explicit rather than ambient (no <c>AsyncLocal</c>): a post-step the ladder
    /// pushed onto the pump outlives the run that queued it, and an ambient scope would let it report into a batch that
    /// was sealed minutes ago.</summary>
    public HydrationRunScope Run { get; }

    /// <summary>A view of this context bound to <paramref name="run"/> — everything else is shared by reference.</summary>
    public HydrationContext ForRun(HydrationRunScope run)
        => new(Store, Hydrator, Traits, Pump, Policy, Log, run ?? throw new ArgumentNullException(nameof(run)));

    /// <summary>Shorthand every ladder uses from its <c>catch</c>: "I swallowed a transport failure for this uri, so do
    /// not seal it as a genuine absence."</summary>
    public void ReportTransient(string uri) => Run.Report(uri);
}
