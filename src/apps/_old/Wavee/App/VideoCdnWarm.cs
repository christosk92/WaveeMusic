using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// Fire-and-forget CDN warm-up for a resolved video source: a bounded GET of the DRM descriptor's init-segment URLs
/// (or a HEAD of a clear source), body discarded, errors swallowed. This is NOT a prefetch of playable media — the
/// actual init-segment bytes the player will use come down again, normally, when <c>FluentVideoMediaHost</c> opens
/// the source. All this buys is a warm DNS resolution, a warm TLS session, and (for a CDN that maintains one) a warm
/// edge cache entry for the very URL the player is about to request, shaving the handshake/TTFB tail off the actual
/// open. Called from <see cref="PlaybackBridge.PrefetchVideoSource"/> right after a resolve succeeds, well before the
/// track boundary that will actually play it.
/// </summary>
static class VideoCdnWarm
{
    static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(10);

    // One shared client for the process: warming wants the SAME connection pool (and therefore the SAME warm
    // DNS/TLS state) the eventual real player request will reuse via the OS/WinHTTP connection cache.
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    {
        Timeout = WarmTimeout,
    };

    /// <summary>Kick off the warm request(s) for <paramref name="src"/> and return immediately — never awaited by the
    /// caller. Safe to call for any <see cref="PopOutVideoSource"/> shape (DRM, clear, local file); a local file or a
    /// source with nothing to warm is a silent no-op.</summary>
    public static void WarmInit(PopOutVideoSource src)
    {
        if (src is null) return;
        _ = WarmAsync(src);
    }

    static async Task WarmAsync(PopOutVideoSource src)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(WarmTimeout);
            if (src.DrmDescriptor is { } drm)
            {
                var video = WarmGetAsync(drm.InitUrl, cts.Token);
                var audio = string.IsNullOrEmpty(drm.AudioInitUrl) ? Task.CompletedTask : WarmGetAsync(drm.AudioInitUrl!, cts.Token);
                await Task.WhenAll(video, audio).ConfigureAwait(false);
                WaveeLog.Instance.Debug("video", $"video-cdn-warm key={src.Key} drm=true elapsed={sw.ElapsedMilliseconds}ms");
            }
            else if (!string.IsNullOrEmpty(src.ClearUrl))
            {
                await WarmHeadAsync(src.ClearUrl!, cts.Token).ConfigureAwait(false);
                WaveeLog.Instance.Debug("video", $"video-cdn-warm key={src.Key} drm=false elapsed={sw.ElapsedMilliseconds}ms");
            }
            // A local-file source (FilePath set) has no CDN to warm — silently nothing to do.
        }
        catch (Exception ex)
        {
            // Warming is purely advisory: any failure (timeout, DNS, TLS, 4xx/5xx) must never surface anywhere the
            // real open would notice — the real open re-requests the same URL and stands on its own merits.
            WaveeLog.Instance.Debug("video", $"video-cdn-warm key={src.Key} failed elapsed={sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static async Task WarmGetAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return;
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Drain (and discard) the body so the connection is genuinely warm end-to-end, not just headers.
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[4096];
        while (await body.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
    }

    static async Task WarmHeadAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        _ = resp.StatusCode;   // headers alone are enough to have warmed DNS/TLS for a HEAD
    }
}
