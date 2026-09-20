using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>The ONE video-source resolution point behind <c>PlaybackBridge.ResolveVideoSource</c>: a tiered walk that
/// answers "what video, if any, should this playable show?".
/// <list type="number">
/// <item>the user's attached local file — it ALWAYS wins, including over a source's own official video;</item>
/// <item>a playback MODULE's own resolved video locator (a YouTube/Twitch HLS master) — it comes before the source
/// tier because a module playable is not a Spotify catalogue entry at all, so the source tier could only ever answer
/// null for it, and the answer is already in the module cache (no await, no RPC);</item>
/// <item>the SOURCE's own video resolver (Spotify's manifest → a playable <see cref="PopOutVideoSource"/>);</item>
/// <item>null → the controller's existing audio fallback, which is tier 4 and lives there.</item>
/// </list>
/// The whole of tier 1 is the pure, engine-free <see cref="VideoOverrideService.Decide"/> — this class is only the shell
/// that maps a decision onto a <see cref="PopOutVideoSource"/> and does the branch's logging/notification. With no
/// override service attached the walk is exactly the single source tier it replaced (the feature's kill switch).</summary>
public sealed class CompositeVideoResolver
{
    readonly Func<string, CancellationToken, Task<PopOutVideoSource?>> _sourceTier;
    readonly VideoOverrideService? _overrides;

    public CompositeVideoResolver(
        Func<string, CancellationToken, Task<PopOutVideoSource?>> sourceTier,
        VideoOverrideService? overrides = null)
    {
        _sourceTier = sourceTier ?? throw new ArgumentNullException(nameof(sourceTier));
        _overrides = overrides;
    }

    /// <summary>A resolver with NO source tier — every playable's video comes from the user's own attachments. This is the
    /// pre-login / fake bootstrap shape: overrides must work without Spotify, and there is simply nothing behind them.</summary>
    public static CompositeVideoResolver OverridesOnly(VideoOverrideService? overrides)
        => new((_, _) => Task.FromResult<PopOutVideoSource?>(null), overrides);

    public async Task<PopOutVideoSource?> ResolveAsync(string playableUri, CancellationToken ct = default)
    {
        if (_overrides is { } svc)
        {
            // Decide() runs a File.Exists probe on the attachment path, which can block for seconds against a
            // UNC/offline share — off the calling thread (originally the UI thread for the primed-intent caller),
            // never inline. The decision body itself is UNCHANGED; only where it runs moved.
            var decision = await Task.Run(() => svc.Decide(playableUri), ct).ConfigureAwait(false);
            switch (decision.Tier)
            {
                case VideoOverrideTier.UseOverride:
                    svc.NoteResolved(playableUri, decision.Override);
                    return PopOutVideoSource.LocalFile(decision.Override);
                case VideoOverrideTier.Broken:
                    // The file moved / the drive is offline. Keep the link (it is repairable), warn once, and fall through
                    // to the original — a bad attachment must never block the music. The File.Exists probe inside Decide
                    // IS the fallback gate; there is no second existence check anywhere downstream.
                    svc.NoteBroken(playableUri, decision.Override);
                    break;
                case VideoOverrideTier.Quarantined:
                    break;   // already failed to open this session — skip tier 1 silently (the anti-loop latch)
            }
        }

        // Tier 2 — a playback module's own video locator. Read from the resolve cache (no await, no RPC: the router
        // already resolved this playable before it ever reached the queue), and keyed on the module PLAYABLE ID rather
        // than the url, so the host's same-Key no-op survives a re-resolve that hands back a fresh, differently-signed
        // url for the very same broadcast.
        if (ModuleVideoSource(playableUri) is { } moduleSource) return moduleSource;

        return await _sourceTier(playableUri, ct).ConfigureAwait(false);
    }

    /// <summary>The module tier as a pure lookup: a cached, video-form module playable with a <c>url</c> locator, or
    /// null. Public so the tier's rule is testable without a resolver or a host.
    /// <para>The module's <c>isLive</c> travels onto the source here, at the ONE point that has both facts in hand. It
    /// has to: the media backend cannot tell a live HLS master from a VOD one (it reports the DVR window as a finite
    /// duration), so the answer must arrive with the locator or not at all.</para></summary>
    /// <param name="playableUri">The playable uri.</param>
    public static PopOutVideoSource? ModuleVideoSource(string? playableUri)
    {
        if (Wavee.Backend.Modules.ModulePlayables.Get(playableUri) is not { } resolved) return null;
        if (resolved.Form != Wavee.Sdk.MediaForm.Video) return null;
        if (resolved.Media is not { Kind: "url", Url: { Length: > 0 } url }) return null;
        return PopOutVideoSource.Clear(url) with { Key = playableUri!, IsLive = resolved.IsLive };
    }
}
