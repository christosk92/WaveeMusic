using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Fetches the clear unencrypted head file (~128 KB) for instant-start playback.</summary>
public sealed class HeadFileClient
{
    const int MaxHeadBytes = 80 * 1024;   // keep below LOH; enough for container headers and early audio.

    readonly Resource<string, HeadBytes> _cache;
    readonly WaveeLogger _log;

    public sealed record HeadBytes(byte[] Data, float NormalizationGainDb);

    public HeadFileClient(IHttpExchange http, Func<SessionContext> ctx, WaveeLogger log = default)
    {
        _log = log;
        _cache = new Resource<string, HeadBytes>(FetchAsync, new FreshnessPolicy.Immutable(), ctx,
            maxEntries: 32, name: "audio.head", debugLog: log);
        _http = http;
    }

    readonly IHttpExchange _http;

    public async Task<HeadBytes> GetAsync(string fileIdHex, CancellationToken ct = default)
    {
        var loaded = await _cache.GetAsync(fileIdHex, ct).ConfigureAwait(false);
        if (loaded.IsReady) return loaded.Value!;
        throw new InvalidOperationException("head file fetch failed: " + (loaded.Error ?? "unknown"));
    }

    async Task<HeadBytes> FetchAsync(string fileIdHex, SessionContext ctx)
    {
        // heads-fa-tls13.spotifycdn.com/head/{fileId} — plain GET, no auth, no Range
        var sw = Stopwatch.StartNew();
        var url = $"https://heads-fa-tls13.spotifycdn.com/head/{fileIdHex.ToLowerInvariant()}";
        _log.Info($"head {fileIdHex}: fetch start max={MaxHeadBytes}B");
        try
        {
            var resp = await _http.SendAsync(new HttpReq("GET", url, new Dictionary<string, string>()), CancellationToken.None).ConfigureAwait(false);
            using (resp)
            {
                if (resp.Status != 200) throw new InvalidOperationException($"head fetch {resp.Status}");
                using var ms = new MemoryStream(MaxHeadBytes);
                var buf = new byte[16 * 1024];
                while (ms.Length < MaxHeadBytes)
                {
                    var want = (int)Math.Min(buf.Length, MaxHeadBytes - ms.Length);
                    var n = await resp.Body.ReadAsync(buf.AsMemory(0, want), CancellationToken.None).ConfigureAwait(false);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                var data = ms.ToArray();
                float gain = data.Length > 148 ? BitConverter.ToSingle(data, 144) : 0f;
                _log.Info($"head {fileIdHex}: fetch ok bytes={data.Length} gain={gain:0.0}dB elapsed={sw.ElapsedMilliseconds}ms");
                return new HeadBytes(data, gain);
            }
        }
        catch (Exception ex)
        {
            _log.Info($"head {fileIdHex}: fetch failed elapsed={sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
