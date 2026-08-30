namespace Wavee.Core;

/// <summary>The signed-in user's OWN top artists (Spotify <c>userTopContent</c>, affinity over a 4-week window) — the
/// data behind Home's top-artist row.
///
/// Deliberately its own service rather than a rung of the artist hydration ladder: this is a ME query with no artist
/// argument and a different cache lifetime, and folding it in would give every artist-page open a reason to touch the
/// user's affinity data.
/// <para>The row's EXPANDER pane needs no second service: selecting an artist asks the façade for
/// <c>HydrationLevel.Rich</c>, which already serves related artists, top tracks with play counts, monthly listeners
/// and world rank behind the ledger's TTL.</para></summary>
public interface IUserTopService
{
    /// <summary>The user's top artists in affinity order (highest first), or empty offline / on failure. Never null, so
    /// no call site needs a null branch to decide whether to render the row.</summary>
    Task<IReadOnlyList<RelatedArtist>> GetTopArtistsAsync(CancellationToken ct = default);

    /// <summary>The user's top tracks from the same persisted document and time window as the artist ranking.</summary>
    Task<IReadOnlyList<Track>> GetTopTracksAsync(CancellationToken ct = default);
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding the UI tree.</summary>
public sealed class SwitchableUserTopService : IUserTopService
{
    IUserTopService _inner;
    public SwitchableUserTopService(IUserTopService inner) => _inner = inner;
    public void SetInner(IUserTopService inner)
        => System.Threading.Volatile.Write(ref _inner, inner ?? throw new ArgumentNullException(nameof(inner)));

    IUserTopService Current => System.Threading.Volatile.Read(ref _inner);
    public Task<IReadOnlyList<RelatedArtist>> GetTopArtistsAsync(CancellationToken ct = default)
        => Current.GetTopArtistsAsync(ct);
    public Task<IReadOnlyList<Track>> GetTopTracksAsync(CancellationToken ct = default)
        => Current.GetTopTracksAsync(ct);
}

/// <summary>Offline/fake fallback: no provider → the row simply does not render (an empty list, not a null).</summary>
public sealed class NullUserTopService : IUserTopService
{
    public Task<IReadOnlyList<RelatedArtist>> GetTopArtistsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RelatedArtist>>(System.Array.Empty<RelatedArtist>());
    public Task<IReadOnlyList<Track>> GetTopTracksAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Track>>(System.Array.Empty<Track>());
}
