using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The full credits drawer for a track, over extended-metadata kind 186 (CREDITS_V2_TRAIT). Track-only by
/// construction: the kind 404s on albums and artists, and a 404 is the ordinary answer, not a failure.</summary>
public interface ITrackCreditsService
{
    /// <summary>The complete grouped credits for <paramref name="trackUri"/>, or <c>null</c> when this entity has none
    /// (a non-track uri, a 404, an empty or undecodable payload). Never throws for a network or parse failure — the
    /// caller falls back to the capped NPV credit list and the surface still paints.</summary>
    Task<TrackCredits?> GetAsync(string trackUri, CancellationToken ct = default);
}

public sealed class NullTrackCreditsService : ITrackCreditsService
{
    public static readonly NullTrackCreditsService Instance = new();
    public Task<TrackCredits?> GetAsync(string trackUri, CancellationToken ct = default)
        => Task.FromResult<TrackCredits?>(null);
}

/// <summary>Stable wrapper so the composition root can hand out one instance before login and swap the live provider
/// in on go-live (mirrors <see cref="SwitchablePreReleaseService"/>). Offline the inner is the null service and the
/// credits surfaces fall back to the NPV contributor list they already had.</summary>
public sealed class SwitchableTrackCreditsService : ITrackCreditsService
{
    volatile ITrackCreditsService _inner;
    public SwitchableTrackCreditsService(ITrackCreditsService inner) => _inner = inner;
    public void SetInner(ITrackCreditsService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public Task<TrackCredits?> GetAsync(string trackUri, CancellationToken ct = default)
        => _inner.GetAsync(trackUri, ct);
}
