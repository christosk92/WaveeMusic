using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.MediaSources;

// ── The SOURCE-AGNOSTIC playable seam ─────────────────────────────────────────────────────────────────────────────────
// Playable identity is an OPAQUE uri on a Track record — `spotify:track:`, `spotify:episode:`, and (later) local/generic
// namespaces all ride the same queue/controller/projection. Nothing between play-intent and the media host may inspect the
// scheme: per-scheme knowledge lives ONLY inside an IPlayableMediaProvider, and MediaProviderRegistry routes to it by
// ownership. Engine-free (Wavee.Core + BCL) so the whole seam stays testable headlessly.

/// <summary>What a source can do BEYOND the mandatory fast-first resolve. Absent capabilities are not failures — they
/// select the proven simpler path (no prepared-next hand-off, a masked Connect uri, no wire-meta enrichment).</summary>
[Flags]
public enum MediaProviderCaps
{
    None = 0,
    /// <summary>The source can resolve the NEXT playable ahead of time, so the controller may schedule a gapless/crossfaded
    /// hand-off. Without it the track boundary is the proven Ended→AutoAdvance hard cut.</summary>
    PreparedNext = 1,
    /// <summary>The playable's uri is meaningful to Spotify Connect and publishes VERBATIM to the cluster. Without it the
    /// publisher masks the uri (remote controllers must never receive a uri they cannot resolve).</summary>
    ConnectPublish = 2,
    /// <summary>The source can supply the per-playable wire metadata (media/file ids, bitrate, format label, duration) the
    /// Connect/telemetry payloads carry.</summary>
    WireMeta = 4,
}

/// <summary>One media source: it OWNS a uri namespace and resolves its playables for the audio host. The fast-first shape
/// (<see cref="ResolveFastAsync"/>) is the only required audio contract — a plain source returns an empty head plus an
/// already-completed body, exactly like the external-episode path.</summary>
public interface IPlayableMediaProvider
{
    /// <summary>Stable short id for logs/diagnostics (e.g. "spotify").</summary>
    string Id { get; }

    /// <summary>True when this source owns the playable. Must be a cheap, allocation-free prefix test — it runs on the
    /// resolve hot path for every provider until one claims the uri.</summary>
    bool Owns(string playableUri);

    MediaProviderCaps Caps { get; }

    Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default);

    /// <summary>The plain (non-instant-start) resolve, used by the cold ghost-resume path. The default derives it from the
    /// fast-first plan, so a source only overrides it when it has a cheaper direct route.</summary>
    async Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        var plan = await ResolveFastAsync(track, ct).ConfigureAwait(false);
        return await plan.Body.ConfigureAwait(false);
    }

    /// <summary>The per-playable wire metadata. Sources without <see cref="MediaProviderCaps.WireMeta"/> keep the default
    /// null (the controller then publishes the Track's own duration and no file ids).</summary>
    Task<PlaybackTrackMeta?> ResolveWireMetaAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<PlaybackTrackMeta?>(null);

    /// <summary>Best-effort pre-resolve of an upcoming playable. Never throws to the caller.</summary>
    void Warm(Track track, string reason = "") { }
}
