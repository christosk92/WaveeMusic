using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── MODULE FACTS → THE NOW-PLAYING PROJECTION ───────────────────────────────────────────────────────────────────────
// Two things only a module knows reach the player bar: whether what is playing is a LIVE broadcast, and what song a
// live broadcast is currently on. Both are per-playable overrides on the projection (the SetDurationOverride pattern),
// and both are wired identically pre-login and live — one relay, called from both composition points, so the two can
// never drift.

/// <summary>Wires a module host's live/metadata facts onto one <see cref="NowPlayingProjection"/>.</summary>
public sealed class ModuleProjectionRelay : IDisposable
{
    readonly NowPlayingProjection _projection;
    readonly ModuleHost? _host;
    readonly ModulePlayableCache? _cache;
    readonly IDisposable? _changesSub;
    readonly Action<string, MetadataUpdate>? _onMetadata;
    readonly Action<string>? _onExpired;
    int _disposed;

    ModuleProjectionRelay(NowPlayingProjection projection, ModuleHost? host, ModulePlayableCache? cache)
    {
        _projection = projection;
        _host = host;
        _cache = cache;

        // LIVE-ness follows the CURRENT track, and it is RE-ASSERTED on every projection change — no "has it answered
        // yet?" latch. Two defects came out of latching: the resolve lands AFTER the projection publishes the track
        // (so a one-shot probe reads an empty cache and answers "not live" forever), and any later fold that drops the
        // override — a momentary null current, a cluster echo — was never re-stated because the question was "already
        // answered". A probe is one ordinal dictionary hit and SetLiveOverride is equality-gated, so re-asserting
        // costs nothing and cannot loop: the re-entrant pass this fires finds nothing changed and does not fire again.
        _changesSub = projection.Changes.Subscribe(Observers.From<IPlaybackState>(OnProjectionChanged));

        if (host is null) return;
        _onMetadata = OnModuleMetadata;
        _onExpired = OnModuleExpired;
        host.MetadataChanged += _onMetadata;
        host.PlayableExpired += _onExpired;
    }

    /// <summary>Attach the relay. The caller owns the returned handle and disposes it with the session.</summary>
    /// <param name="projection">The projection to publish onto.</param>
    /// <param name="host">The module host whose notifications to relay; null wires the live-ness half only.</param>
    /// <param name="cache">The resolve cache to probe. Null (the composition roots) reads the host's own cache, or the
    /// process-wide <see cref="ModulePlayables.Cache"/> when no host was handed over — the pre-login relay is built with
    /// whatever host existed at that moment. Tests pass their own so the probe never rides process-wide static state.</param>
    public static ModuleProjectionRelay Attach(NowPlayingProjection projection, ModuleHost? host,
        ModulePlayableCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return new ModuleProjectionRelay(projection, host, cache);
    }

    /// <summary>The cache this relay probes: the explicit one, else the host's, else the process-wide attachment.</summary>
    ModulePlayableCache? Cache => _cache ?? _host?.Playables ?? ModulePlayables.Cache;

    /// <summary>Fired when a module says one of its locators expired AND that playable is the one playing — the app's
    /// cue to re-resolve and reload it (a YouTube url is IP-bound and dies after ~6 h; a Twitch token expires sooner).
    /// Argument: the playable uri.</summary>
    public event Action<string>? CurrentPlayableExpired;

    /// <summary>A live station's in-band "now playing" line, split and published for the CURRENT playable. Called by the
    /// audio host's <see cref="ILiveMetadataSource"/> relay; ignored unless the current playable really is live, so an
    /// ICY title can never overwrite the title of an ordinary track.</summary>
    /// <param name="rawStreamTitle">The station's raw <c>StreamTitle</c> value.</param>
    /// <param name="stationName">The station's own name, used as the attribution fallback.</param>
    public void OnLiveStreamTitle(string rawStreamTitle, string? stationName)
    {
        if (_projection.CurrentTrack?.Uri is not { Length: > 0 } uri) return;
        if (!_projection.IsLive && Cache?.IsLive(uri) is not true) return;
        (string title, string? artist) = Wavee.Backend.Audio.IcyMetadata.SplitStreamTitle(rawStreamTitle);
        _projection.SetMetadataOverride(uri, title, artist ?? stationName);
    }

    void OnProjectionChanged(IPlaybackState state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        string? uri = state.CurrentTrack?.Uri;
        // A pending answer is NOT "no" — but it must still publish something, and false is the only honest placeholder
        // (nothing has stated live-ness yet). The next projection change re-probes and turns it into the truth; the
        // projection's own equality gate makes the repeated answer free.
        _projection.SetLiveOverride(uri, LivenessOf(Cache, uri) ?? false);
    }

    /// <summary>Live-ness of one playable as the module cache knows it: <c>true</c>/<c>false</c> when the answer is IN,
    /// <c>null</c> while it is still pending (a module uri whose resolve has not landed). Non-module uris answer false
    /// immediately — no module will ever have an opinion about a Spotify track.
    /// <para>Deliberately reads the cache INCLUDING expired entries: a signed url dying does not make a broadcast stop
    /// being a broadcast, and dropping the LIVE chip while the app re-resolves would be a visible lie.</para></summary>
    /// <param name="cache">The module resolve cache (<c>ModulePlayables.Cache</c>); null = no module host is wired.</param>
    /// <param name="uri">The playable uri.</param>
    public static bool? LivenessOf(ModulePlayableCache? cache, string? uri)
    {
        if (uri is not { Length: > 0 }) return false;
        if (!uri.StartsWith(ModuleUri.Scheme, StringComparison.Ordinal)) return false;
        if (cache is null) return null;                            // no host wired yet — the question is still open
        return cache.GetIncludingExpired(uri) is { } resolved ? resolved.IsLive : null;
    }

    void OnModuleMetadata(string playableUri, MetadataUpdate update)
    {
        if (!string.Equals(_projection.CurrentTrack?.Uri, playableUri, StringComparison.Ordinal)) return;
        string? artist = update.Artists is { Length: > 0 } a ? string.Join(", ", a) : null;
        _projection.SetMetadataOverride(playableUri, update.Title, artist);
    }

    void OnModuleExpired(string playableUri)
    {
        if (!string.Equals(_projection.CurrentTrack?.Uri, playableUri, StringComparison.Ordinal)) return;
        // The locator died and the app is about to re-resolve. Nothing to re-open any more — the live question is
        // re-asked on every projection change — so this is purely the reload cue.
        CurrentPlayableExpired?.Invoke(playableUri);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_host is not null)
        {
            if (_onMetadata is { } m) _host.MetadataChanged -= m;
            if (_onExpired is { } e) _host.PlayableExpired -= e;
        }

        _changesSub?.Dispose();
    }
}

/// <summary>The bounded re-resolve policy for a module locator that died mid-play (an expired YouTube url, a rotated
/// Twitch token). Pure decision state so the retry ladder is testable without a media engine.</summary>
public sealed class ModuleReloadPolicy
{
    /// <summary>How many re-resolves one playable gets before the app stops trying and faults honestly.</summary>
    public const int MaxAttempts = 3;

    readonly Lock _gate = new();
    string? _uri;
    int _attempts;

    /// <summary>May this playable be re-resolved again right now? Counts the attempt when it answers true; a new uri
    /// resets the ladder (a different broadcast starts from zero).</summary>
    /// <param name="playableUri">The playable that failed.</param>
    public bool TryTakeAttempt(string? playableUri)
    {
        if (playableUri is not { Length: > 0 }) return false;
        lock (_gate)
        {
            if (!string.Equals(_uri, playableUri, StringComparison.Ordinal)) { _uri = playableUri; _attempts = 0; }
            if (_attempts >= MaxAttempts) return false;
            _attempts++;
            return true;
        }
    }

    /// <summary>How long to wait before the nth attempt: 0 s, 1 s, 4 s.</summary>
    /// <param name="attempt">1-based attempt number.</param>
    public static TimeSpan BackoffFor(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromSeconds(4),
    };

    /// <summary>Attempts taken for the playable currently being retried.</summary>
    public int Attempts { get { lock (_gate) return _attempts; } }

    /// <summary>Forget the ladder (the playable started cleanly, or playback moved on).</summary>
    public void Reset() { lock (_gate) { _uri = null; _attempts = 0; } }

    /// <summary>Re-resolve one module playable and drop its cached video source, bounded by this policy.</summary>
    /// <param name="host">The module host.</param>
    /// <param name="playableUri">The playable to re-resolve.</param>
    /// <param name="ct">Cancels the wait and the resolve.</param>
    /// <returns>True when a fresh locator was obtained and the caller should reload.</returns>
    public async Task<bool> TryReResolveAsync(ModuleHost host, string playableUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!TryTakeAttempt(playableUri)) return false;
        TimeSpan wait = BackoffFor(Attempts);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        try
        {
            await host.ResolveAsync(playableUri, force: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
