namespace Wavee.Local.Enrichment;

/// <summary>
/// Per-user store for the TMDB v4 read-access bearer token.
///
/// <para>Replaces the compile-time <c>EnrichmentSecrets</c> pattern with a
/// runtime BYO-token model — every user pastes their own free TMDB token
/// into Settings. The token is sensitive, so the Windows implementation
/// (<c>DpapiTmdbTokenStore</c>) writes it as a DPAPI-encrypted blob next to
/// the existing Spotify credentials cache.</para>
///
/// <para>This interface lives in Wavee.Local so <see cref="LocalEnrichmentService"/>
/// can subscribe to <see cref="HasTokenChanged"/> without taking a Windows
/// dependency. Headless / non-Windows surfaces can supply a stub that always
/// reports <see cref="HasToken"/> = false.</para>
/// </summary>
public interface ITmdbTokenStore
{
    /// <summary>Returns the stored bearer token, or null when none is set.</summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the bearer token. Passing null clears the stored token and
    /// raises <see cref="HasTokenChanged"/> with false; the consumer is
    /// expected to drain its queue and stop work.
    /// </summary>
    Task SetTokenAsync(string? token, CancellationToken ct = default);

    /// <summary>
    /// Fast non-async accessor for UI gating — true when a token is present.
    /// Backed by an in-memory cache refreshed on <see cref="SetTokenAsync"/>
    /// and at construction time, so callsites can branch on it without
    /// hitting disk per binding evaluation.
    /// </summary>
    bool HasToken { get; }

    /// <summary>
    /// Fires with the new <see cref="HasToken"/> value every time the token
    /// is set or cleared. The enrichment service listens to this to spin its
    /// worker up / down without an explicit start / stop call.
    /// </summary>
    IObservable<bool> HasTokenChanged { get; }
}
