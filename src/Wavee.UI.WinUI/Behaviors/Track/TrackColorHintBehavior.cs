using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Wavee.UI.Helpers;
using Wavee.UI.Services;

namespace Wavee.UI.WinUI.Behaviors.Track;

/// <summary>
/// Attached behavior that resolves per-track dominant-color placeholder hints via
/// <see cref="ITrackColorHintService"/> and applies them to the host
/// <see cref="TrackItem"/>'s art-placeholder borders.
///
/// Encapsulates the recycling-safe latch (each invocation bumps a version counter
/// and only the matching async continuation applies its result) so a row that
/// scrolls into a new track while a fetch is in-flight can't paint a stale color.
///
/// Lifecycle: state lives in a per-element <see cref="State"/> record kept in a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/>, so behavior teardown happens
/// automatically when the host control is collected. Recycle safety is reset
/// explicitly by <see cref="Reset"/> (called from the host's <c>OnUnloaded</c>).
/// </summary>
public static class TrackColorHintBehavior
{
    private static readonly ConditionalWeakTable<DependencyObject, State> _states = new();
    private static readonly ITrackColorHintService? _service =
        Ioc.Default.GetService<ITrackColorHintService>();
    private static readonly ILogger? _logger =
        Ioc.Default.GetService<ILogger<object>>();

    private sealed class State
    {
        // Guards stale color-hint applies after a virtualized row is recycled.
        // Incremented on every Resolve invocation; an awaiting continuation
        // only applies its result if this counter hasn't advanced since it started.
        public int Version;
        public string? BoundUrl;
    }

    /// <summary>
    /// Resolves and applies a placeholder color for <paramref name="rawImageUrl"/> on
    /// <paramref name="host"/>. Idempotent across re-invocations with the same URL —
    /// only triggers a fresh fetch when the URL actually changes.
    /// </summary>
    /// <param name="host">The host element (typically a <c>TrackItem</c>) whose
    /// <c>ApplyPlaceholderColor</c> hook receives the resolved hex.</param>
    /// <param name="rawImageUrl">The track's raw cover-art URL (Spotify or http(s)).</param>
    /// <param name="explicitPlaceholderHex">When set by the page (e.g. a single
    /// album tint), suppresses the per-track hint so the page's color wins.</param>
    /// <param name="useImageColorHint">Whether the host opted into image-based hinting.</param>
    /// <param name="applyColor">Invoked with the resolved hex (or null for
    /// neutral). Always called on the dispatcher thread.</param>
    public static void Resolve(
        DependencyObject host,
        string? rawImageUrl,
        string? explicitPlaceholderHex,
        bool useImageColorHint,
        Action<string?> applyColor)
    {
        if (host is null) return;

        // Explicit PlaceholderColorHex wins — if a page set it (e.g. an album page
        // using a single album tint), don't override with a per-track hint.
        if (!string.IsNullOrEmpty(explicitPlaceholderHex)) return;
        if (!useImageColorHint) return;
        if (_service == null) return;

        var state = _states.GetValue(host, static _ => new State());

        if (string.IsNullOrWhiteSpace(rawImageUrl))
        {
            state.BoundUrl = null;
            applyColor(null);
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(rawImageUrl);
        if (string.IsNullOrWhiteSpace(httpsUrl))
        {
            state.BoundUrl = null;
            applyColor(null);
            return;
        }

        if (string.Equals(state.BoundUrl, httpsUrl, StringComparison.Ordinal))
            return;
        state.BoundUrl = httpsUrl;

        var version = Interlocked.Increment(ref state.Version);

        // Fast path: synchronous cache hit — apply inline, no async hop.
        if (_service.TryGet(httpsUrl, out var cachedHex))
        {
            applyColor(cachedHex);
            return;
        }

        // Apply neutral immediately so the row doesn't flash a stale previous color
        // while the background worker resolves this URL's color.
        applyColor(null);

        _ = ResolveAsync(host, httpsUrl, version, applyColor);
    }

    private static async Task ResolveAsync(
        DependencyObject host,
        string httpsUrl,
        int version,
        Action<string?> applyColor)
    {
        try
        {
            var hex = await _service!.GetOrResolveAsync(httpsUrl).ConfigureAwait(true);
            if (!_states.TryGetValue(host, out var state) || state.Version != version)
                return;
            applyColor(hex);
        }
        catch (OperationCanceledException)
        {
            // Row was unloaded or cancelled — fine, nothing to do.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Color-hint resolution failed for {Url}", httpsUrl);
        }
    }

    /// <summary>
    /// Drops the cached bound URL for <paramref name="host"/> and bumps the version
    /// so any in-flight async fetch ignores its result. Called from the host's
    /// <c>OnUnloaded</c> hook so a recycled row starts clean.
    /// </summary>
    public static void Reset(DependencyObject host)
    {
        if (host is null) return;
        if (!_states.TryGetValue(host, out var state)) return;
        state.BoundUrl = null;
        Interlocked.Increment(ref state.Version);
    }
}
