using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>Resolves an upcoming release's (prerelease uri ↔ album uri ↔ release instant) triple over extended-metadata
/// kind 138. Answers to EITHER key — the wire serves the same payload whether it is asked with the prerelease uri or
/// the album uri (E2E-DIFF.md §5.4.1) — which is the only reason the two schemes are navigable at all: their ids
/// differ, so neither can be computed from the other (see <see cref="PreReleaseUris"/>).</summary>
public interface IPreReleaseService
{
    /// <summary>The link for <paramref name="uri"/>, or <c>null</c> when this entity has no upcoming release — the
    /// answer for almost every album (kind 138 404s on 3 of the 5 captured entities). Never throws for a network or
    /// parse failure: no link means the announce surfaces simply do not render, which is also the correct rendering
    /// for an album that is already out.</summary>
    Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default);
}

public sealed class NullPreReleaseService : IPreReleaseService
{
    public static readonly NullPreReleaseService Instance = new();
    public Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default)
        => Task.FromResult<PreReleaseLink?>(null);
}

/// <summary>Stable wrapper so the composition root can hand out one instance before login and swap the live provider
/// in on go-live (mirrors <see cref="SwitchablePlaylistPopcountService"/>). Offline the inner is the null service, and
/// every prerelease surface degrades to "announced, but not pre-savable / not click-through-resolvable".</summary>
public sealed class SwitchablePreReleaseService : IPreReleaseService
{
    volatile IPreReleaseService _inner;
    public SwitchablePreReleaseService(IPreReleaseService inner) => _inner = inner;
    public void SetInner(IPreReleaseService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default)
        => _inner.ResolveAsync(uri, ct);
}
