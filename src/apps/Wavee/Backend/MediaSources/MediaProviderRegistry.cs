using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Backend.MediaSources;

/// <summary>The ONE dispatch point between play-intent and a media source: an ordered provider set where the first
/// <see cref="IPlayableMediaProvider.Owns"/> wins (the SourceRegistry.OwnerOf convention). It implements the three
/// resolver seams the <see cref="PlaybackController"/> already consumes (<see cref="ITrackResolver"/>,
/// <see cref="IFastTrackResolver"/>, <see cref="IFastTrackWarmer"/>), so registering it changes nothing but WHERE the
/// per-scheme knowledge lives. A uri no provider owns is a typed <see cref="AudioPlaybackException"/> — the existing
/// ReportPlaybackError path surfaces it, never a silent drop.</summary>
public sealed class MediaProviderRegistry : ITrackResolver, IFastTrackResolver, IFastTrackWarmer
{
    readonly IPlayableMediaProvider[] _providers;

    public MediaProviderRegistry(params IPlayableMediaProvider[] providers)
        => _providers = providers ?? Array.Empty<IPlayableMediaProvider>();

    public int Count => _providers.Length;

    /// <summary>The provider that owns the uri, or null. Allocation-free: a straight scan over the fixed array.</summary>
    public IPlayableMediaProvider? OwnerOf(string playableUri)
    {
        var providers = _providers;
        for (int i = 0; i < providers.Length; i++)
            if (providers[i].Owns(playableUri)) return providers[i];
        return null;
    }

    public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
        => Require(track.Uri).ResolveFastAsync(track, ct);

    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        => Require(track.Uri).ResolveAsync(track, ct);

    public void Warm(Track track, string reason = "") => OwnerOf(track.Uri)?.Warm(track, reason);

    public Task<PlaybackTrackMeta?> ResolveWireMetaAsync(Track track, CancellationToken ct = default)
    {
        var owner = OwnerOf(track.Uri);
        return owner is null || (owner.Caps & MediaProviderCaps.WireMeta) == 0
            ? Task.FromResult<PlaybackTrackMeta?>(null)
            : owner.ResolveWireMetaAsync(track, ct);
    }

    /// <summary>May the controller schedule a prepared (gapless/crossfaded) hand-off INTO this playable? An unowned uri
    /// answers false: the hard cut is always the safe boundary.</summary>
    public bool SupportsPreparedNext(string playableUri) => Has(playableUri, MediaProviderCaps.PreparedNext);

    /// <summary>May this playable's uri go on the Connect wire verbatim?</summary>
    public bool IsConnectPublishable(string playableUri) => Has(playableUri, MediaProviderCaps.ConnectPublish);

    bool Has(string playableUri, MediaProviderCaps cap)
    {
        var owner = OwnerOf(playableUri);
        return owner is not null && (owner.Caps & cap) != 0;
    }

    IPlayableMediaProvider Require(string playableUri)
        => OwnerOf(playableUri)
           ?? throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "no media source owns " + playableUri);
}
