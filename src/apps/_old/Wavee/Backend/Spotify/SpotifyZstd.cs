using System;
using System.IO;
using ZstdSharp;

namespace Wavee.Backend.Spotify;

// ── §2.7 — the zstd response guard ────────────────────────────────────────────────────────────────────────────────────
// The /changes and rootlist /changes (and /diff) playlist-v2 responses can come back Content-Encoding: zstd. Our
// ITransport.Resp surfaces only the buffered body — no response headers — so magic-sniffing the 4-byte zstd frame magic
// (28 B5 2F FD) IS the guard. Decode manually (pure-managed ZstdSharp, AOT-safe): .NET's automatic zstd HTTP decode is
// known to truncate multi-frame bodies, so we never lean on it. A non-zstd body is returned unchanged.
public static class SpotifyZstd
{
    public static byte[] MaybeDecompressZstd(byte[] body)
    {
        if (body is null || body.Length < 4) return body ?? Array.Empty<byte>();
        if (!(body[0] == 0x28 && body[1] == 0xB5 && body[2] == 0x2F && body[3] == 0xFD)) return body;   // not a zstd frame

        // Decompressor.Unwrap needs the frame's content-size header; some server responses (e.g. the artist
        // popular-release-segments list) omit it and Unwrap silently returns 0 bytes. Fall back to the streaming
        // decoder, which reads frame-by-frame and doesn't depend on that header being present.
        using (var d = new Decompressor())
        {
            var unwrapped = d.Unwrap(body);
            if (unwrapped.Length > 0) return unwrapped.ToArray();
        }

        using var src = new MemoryStream(body);
        using var zs = new DecompressionStream(src);
        using var dst = new MemoryStream();
        zs.CopyTo(dst);
        return dst.ToArray();
    }
}
