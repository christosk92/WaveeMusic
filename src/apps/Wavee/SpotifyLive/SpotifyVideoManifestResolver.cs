using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using M = Wavee.Protocol.Metadata;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

// ── What is left of SpotifyVideoService (hydration façade P2, plan §1.6) ─────────────────────────────────────────────
// The DATA half of the old service — detect/get/fold/recover — is now the trait pipeline's VideoProjector: kind 99/182
// arrives on the ONE trait POST every list surface already sends, and the projection lands in the same VideoAssociation
// plane it always did. What could NOT move is this: resolving a PLAYABLE source. That is not a trait (nothing is
// projected into the store), it is a per-play, on-demand walk down four tiers to a manifest id, and it is the only
// reason a Spotify-specific video type still exists at all.
//
// It deliberately does NOT live under SpotifyLive/Hydration/: that folder is engine-free by contract (Wavee.Tests
// source-globs it) and the playable half returns FluentGpu types (PopOutVideoSource / DashSourceDescriptor). Keeping the
// manifest-id walk next to that in ONE file is worth more than a partial-class split across two folders — nothing tests
// the walk in isolation, and both consumers (go-live's CompositeVideoResolver tier and the `--spotify-video-manifest`
// probe) are app-side.

/// <summary>Track uri → a playable music-video source. Tier order (the ONE definition, shared by playback and the
/// <c>--spotify-video-manifest</c> probe): the track's OWN TrackV4 (<c>OriginalVideo[0].Gid</c>, hex) → its
/// <c>VIDEO_ASSOCIATIONS</c> counterpart's TrackV4 → the two facts the STORED association already holds.</summary>
sealed class SpotifyVideoManifestResolver
{
    readonly ExtendedMetadataSource _metadata;
    readonly IStore _store;
    readonly WaveeLogger _log;

    /// <param name="metadata">The shared extended-metadata transport. This resolver is deliberately STATELESS (no
    /// <c>ExtensionEtagCache</c>, no per-uri memo of its own): every call re-walks the tiers and re-fetches. Caching now
    /// lives ABOVE it, in <c>PlaybackBridge</c>'s <see cref="SingleFlightMemo{T}"/> (10-min TTL, single-flight per uri)
    /// which fronts <c>PlaybackBridge.ResolveVideoSource</c> for every consumer — the playback path, the prefetch, and
    /// the pop-out. That is the one place a repeated resolve for the same playable is actually avoided; the row-facing
    /// kind-99 read that IS worth caching independently of a play still goes through the trait pipeline.</param>
    /// <param name="store">Read-only here — the last two tiers read the VideoAssociation the projector wrote. Required:
    /// without it a relinked (alias) track resolves to nothing and plays as audio while showing a video badge.</param>
    public SpotifyVideoManifestResolver(ExtendedMetadataSource metadata, IStore store, WaveeLogger log = default)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>Resolve a PLAYABLE video source for <paramref name="trackUri"/> (the pop-out / inline surface consumes it):
    /// manifest id → GET the v9 manifest → if it offers a PlayReady mp4 profile, a <see cref="PopOutVideoSource.PlayReady"/>
    /// (descriptor + license relay); null otherwise (no video, or Widevine-only — FluentGpu ships PlayReady only, so there
    /// is no lane). Runtime success additionally depends on the account actually being served PlayReady (confirm with
    /// WAVEE_AUDIO_FORMAT_PROBE=1).</summary>
    public async Task<PopOutVideoSource?> ResolvePlayableAsync(string trackUri, ITransport transport, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || transport is null) return null;
        var sw = Stopwatch.StartNew();
        _log.Debug($"[video] resolve begin track={trackUri}");

        string? manifestId;
        try { (manifestId, _) = await ResolveManifestIdAsync(trackUri, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info($"video resolve TrackV4: {ex.Message} elapsed={sw.ElapsedMilliseconds}ms");
            return null;
        }
        if (string.IsNullOrEmpty(manifestId))
        {
            _log.Debug($"[video] resolve no-manifest track={trackUri} elapsed={sw.ElapsedMilliseconds}ms");
            return null;
        }

        var manifestSw = Stopwatch.StartNew();
        var manifest = await SpotifyVideoResolver.ResolveManifestAsync(transport, manifestId, ct).ConfigureAwait(false);
        _log.Debug($"[video] manifest GET id={manifestId} elapsed={manifestSw.ElapsedMilliseconds}ms");
        if (manifest is null || !manifest.HasPlayReadyMp4)
        {
            _log.Info($"video resolve {trackUri}: no PlayReady mp4 (widevine={manifest?.HasWidevine == true}) elapsed={sw.ElapsedMilliseconds}ms");
            return null;
        }
        var descriptor = manifest.ToDashDescriptor();
        if (descriptor is null) return null;
        string initHost = ""; try { initHost = new Uri(descriptor.InitUrl).Host; } catch { }
        var (naturalWidth, naturalHeight) = HighestVideoProfileSize(manifest);
        _log.Debug($"[video] resolved PlayReady manifest={manifestId} isDrm=true initHost={initHost} " +
                   $"segs={descriptor.SegmentCount} stride={descriptor.SegmentStride} kid={descriptor.DefaultKid ?? "-"} " +
                   $"codecs={descriptor.Codecs ?? "-"} licSrv={manifest.LicenseServerEndpoint ?? "-"} " +
                   $"natural={naturalWidth}x{naturalHeight} elapsed={sw.ElapsedMilliseconds}ms");
        var relay = SpotifyLicenseRelay.Create(transport, manifest.LicenseServerEndpoint);
        return PopOutVideoSource.PlayReady(manifestId, descriptor, relay, manifest.LicenseServerEndpoint)
            with { NaturalWidth = naturalWidth, NaturalHeight = naturalHeight };
    }

    /// <summary>The natural pixel size to seed <see cref="PopOutVideoSource.NaturalWidth"/>/<c>NaturalHeight</c> with, so
    /// the docked/PiP surfaces can size themselves AT MOUNT instead of re-laying-out when the decoder reports
    /// <c>NaturalSize</c> seconds later. Picks the HIGHEST video profile the manifest offered (not the conservative
    /// ≤480p one <see cref="SpotifyVideoManifest.ToDashDescriptor"/> selected as the initial representation — the
    /// player can switch up to a higher rung, and the card should already be sized for that), falling back to the
    /// selected profile's own size when the manifest carried no profile list.</summary>
    static (int Width, int Height) HighestVideoProfileSize(SpotifyVideoManifest manifest)
    {
        var profiles = manifest.VideoProfiles;
        if (profiles.Count > 0)
        {
            var best = profiles[0];
            for (int i = 1; i < profiles.Count; i++)
            {
                var p = profiles[i];
                if (p.Height > best.Height || (p.Height == best.Height && p.Bandwidth > best.Bandwidth)) best = p;
            }
            if (best.Width > 0 && best.Height > 0) return (best.Width, best.Height);
        }
        return (manifest.Width, manifest.Height);
    }

    /// <summary>The manifest-id resolution ORDER — the ONE definition, shared by playback above and the
    /// <c>--spotify-video-manifest</c> probe: the track's OWN TrackV4 (<c>OriginalVideo[0].Gid</c>, hex) first, else its
    /// <c>VIDEO_ASSOCIATIONS</c> counterpart's TrackV4, else the two facts the STORED association already holds.
    /// <c>Source</c> names the path that produced it (<c>track-v4</c> / <c>video-associations</c> /
    /// <c>assoc-counterpart</c> / <c>assoc-gid</c> / <c>none</c>) so a diagnostic can report it. Throws only what the
    /// metadata chain throws — callers guard.
    ///
    /// <para>The last two tiers exist because of RELINKED (alias) track ids. Both live tiers above ask the wire about the
    /// uri we were handed, and for an alias BOTH 404: kind 99 is keyed by the canonical id, and the alias's own TrackV4
    /// carries no <c>original_video</c>. The video projector's canonical recovery already resolved that — it stored the
    /// canonical entity's counterpart uri and the kind-212 video gid UNDER the alias, which is what lit the row's
    /// indicator and what Connect publishes as <c>associated_video_id</c>. Playback then re-derived from scratch and threw
    /// that away, so a recovered alias showed a video badge and still played as audio. These tiers are reached ONLY when
    /// the live pair already produced nothing, so they can turn a null into an answer and never the reverse.</para></summary>
    internal async Task<(string? ManifestId, string Source)> ResolveManifestIdAsync(string trackUri, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Tiers 1+2 collapse into ONE batched request: the track's own TrackV4 (self-contained video) and its
        // VIDEO_ASSOCIATIONS counterpart pointer used to be two sequential RTTs — they travel over the wire together
        // now, because GetExtensionsWithHeadersAsync already batches any number of (uri, kind) pairs into one POST.
        var reqs = new (string Uri, Xm.ExtensionKind Kind, string? Etag)[]
        {
            (trackUri, Xm.ExtensionKind.TrackV4, null),
            (trackUri, Xm.ExtensionKind.VideoAssociations, null),
        };
        var results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        _log.Debug($"[video] manifest tier1+2 fetch track={trackUri} elapsed={sw.ElapsedMilliseconds}ms");

        // Self-contained first: the track's OWN TrackV4 → OriginalVideo[0].Gid.
        if (TrackV4Gid(results, trackUri) is { Length: > 0 } own)
        {
            _log.Debug($"[video] manifest via track-v4 track={trackUri} manifest={own} elapsed={sw.ElapsedMilliseconds}ms");
            return (own, "track-v4");
        }
        // Fallback: the VIDEO_ASSOCIATIONS counterpart (an audio track linking out to its paired video track). The
        // pointer came back with tier 1 above; resolving the LINKED track's own TrackV4 → manifest_id is necessarily
        // a second request (we don't know the counterpart uri until this payload is parsed).
        if (VideoAssociationTarget(results, trackUri) is { Length: > 0 } linkedUri)
        {
            _log.Info($"video resolve {trackUri}: VIDEO_ASSOCIATIONS linked video {linkedUri}");
            if (await ResolveManifestIdFromTrackV4Async(linkedUri, ct).ConfigureAwait(false) is { Length: > 0 } linked)
            {
                _log.Debug($"[video] manifest via video-associations track={trackUri} linked={linkedUri} manifest={linked} elapsed={sw.ElapsedMilliseconds}ms");
                return (linked, "video-associations");
            }
        }

        // The relink tiers. Read the plane ONCE; a record only exists here if some fetch already landed one.
        var stored = _store.GetVideoAssociation(trackUri);
        if (stored is not { HasVideo: true })
        {
            _log.Debug($"[video] manifest none track={trackUri} stored={(stored is null ? "no-row" : "no-video")} elapsed={sw.ElapsedMilliseconds}ms");
            return (null, "none");
        }
        // The canonical entity's paired video track, as recorded under this (possibly alias) uri.
        if (stored.CounterpartUri is { Length: > 0 } counterpart
            && !string.Equals(counterpart, trackUri, StringComparison.Ordinal)
            && await ResolveManifestIdFromTrackV4Async(counterpart, ct).ConfigureAwait(false) is { Length: > 0 } viaStore)
        {
            _log.Debug($"[video] manifest via stored counterpart track={trackUri} counterpart={counterpart} manifest={viaStore} elapsed={sw.ElapsedMilliseconds}ms");
            return (viaStore, "assoc-counterpart");
        }
        // Last: the kind-212 gid IS the manifest id (same value Connect publishes as associated_video_id).
        if (stored.VideoGidHex is { Length: > 0 } gid)
        {
            _log.Debug($"[video] manifest via stored gid track={trackUri} manifest={gid} elapsed={sw.ElapsedMilliseconds}ms");
            return (gid, "assoc-gid");
        }
        _log.Debug($"[video] manifest none track={trackUri} stored=hasVideo-but-no-counterpart-or-gid elapsed={sw.ElapsedMilliseconds}ms");
        return (null, "none");
    }

    /// <summary>Read <c>OriginalVideo[0].Gid</c> (hex) = manifest_id out of a batched TrackV4 result for <paramref name="uri"/>;
    /// null when the track carries no self-contained video (or the extension was not in the batch / unavailable).</summary>
    static string? TrackV4Gid(IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results, string uri)
    {
        if (!results.TryGetValue((uri, Xm.ExtensionKind.TrackV4), out var res) || res.Payload is not { } payload) return null;
        var track = M.Track.Parser.ParseFrom(payload);
        return track.OriginalVideo.Count > 0 && track.OriginalVideo[0].Gid.Length > 0
            ? Convert.ToHexStringLower(track.OriginalVideo[0].Gid.Span)
            : null;
    }

    /// <summary>Read <c>Association.AssociatedUri</c> (the paired video track) out of a batched VIDEO_ASSOCIATIONS
    /// result for <paramref name="uri"/>; null when there is no association in the batch.</summary>
    static string? VideoAssociationTarget(IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results, string uri)
    {
        if (!results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res) || res.Payload is not { } payload) return null;
        var assoc = Xm.VideoAssociations.Parser.ParseFrom(payload);
        var a = assoc.Association;
        return a is not null && a.HasAssociatedUri && !string.IsNullOrEmpty(a.AssociatedUri) ? a.AssociatedUri : null;
    }

    /// <summary>Fetch <paramref name="uri"/>'s TrackV4 and read <c>OriginalVideo[0].Gid</c> (hex) = manifest_id; null when
    /// the track carries no self-contained video (or the extension is unavailable). Used for the relink/counterpart tiers,
    /// which don't know their target uri until an earlier fetch names it — those cannot join the tier-1+2 batch above.</summary>
    async Task<string?> ResolveManifestIdFromTrackV4Async(string uri, CancellationToken ct)
    {
        var reqs = new (string, Xm.ExtensionKind, string?)[] { (uri, Xm.ExtensionKind.TrackV4, null) };
        var results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        return TrackV4Gid(results, uri);
    }
}
