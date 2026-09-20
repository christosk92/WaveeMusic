using System;
using System.Text;

namespace Wavee.Backend.MediaSources;

// ── The non-Spotify playable URI namespaces ──────────────────────────────────────────────────────────────────────────
// A playable's identity is an OPAQUE uri (see PlayableMediaProvider.cs). The two namespaces P4 introduces both carry
// their whole payload — an absolute path, or a path/URL — base64url-encoded INSIDE the uri, because:
//   • a playable uri is a dictionary key, a Connect wire field and a SQLite primary key, so it must be one flat token
//     with no colons, spaces or backslashes of its own (a raw "C:\music\a b.mp3" would fork every ':'-split parser);
//   • it must round-trip byte-exactly (the file it names is the only thing that can play it);
//   • it must be URL-safe so it can ride an uri without a second escaping layer.
// base64url (RFC 4648 §5: '+'→'-', '/'→'_', no padding) is the smallest encoding with all three properties.
//
// Ownership: LocalFileMediaProvider owns wavee:local:file: and GenericMediaProvider owns wavee:media:. Both live inside
// the `wavee:local:` / `wavee:` space Wavee.Core's LocalSource already claims for its catalog, so no Spotify uri can
// ever collide with one.

/// <summary>Build/parse the two local playable uri namespaces. Engine-free and allocation-modest: encoding happens at
/// human rate (a pick / a drop), decoding once per resolve — never per frame.</summary>
public static class PlayableUri
{
    /// <summary>A file on THIS device, played directly by the audio host: <c>wavee:local:file:&lt;b64url(abs path)&gt;</c>.</summary>
    public const string LocalFilePrefix = "wavee:local:file:";

    /// <summary>A generic "play this thing" playable: <c>wavee:media:&lt;b64url(path-or-url)&gt;</c>. The payload decides
    /// which shape it resolves to (a local file handle, or the plain-HTTP one external podcast episodes already use).</summary>
    public const string MediaPrefix = "wavee:media:";

    /// <summary>The playable uri for a local audio file. The path is NOT normalized here — the caller passes the
    /// absolute path it actually resolved, so the uri and the file agree exactly.</summary>
    public static string ForLocalFile(string absolutePath) => LocalFilePrefix + Encode(absolutePath);

    /// <summary>The playable uri for a generic path-or-URL.</summary>
    public static string ForMedia(string pathOrUrl) => MediaPrefix + Encode(pathOrUrl);

    /// <summary>Decode the payload of a uri in <paramref name="prefix"/>'s namespace. False (with an empty payload) for
    /// any uri that is not in the namespace or whose payload is not valid base64url — a malformed uri is a resolve
    /// failure, never an exception thrown at a prefix test.</summary>
    public static bool TryDecode(string? uri, string prefix, out string payload)
    {
        payload = "";
        if (uri is null || prefix.Length == 0) return false;
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return TryDecodeBase64Url(uri.AsSpan(prefix.Length), out payload);
    }

    /// <summary>The absolute path behind a <c>wavee:local:file:</c> uri.</summary>
    public static bool TryDecodeLocalFile(string? uri, out string path) => TryDecode(uri, LocalFilePrefix, out path);

    /// <summary>The path-or-URL behind a <c>wavee:media:</c> uri.</summary>
    public static bool TryDecodeMedia(string? uri, out string pathOrUrl) => TryDecode(uri, MediaPrefix, out pathOrUrl);

    /// <summary>Is this an <c>http(s)</c> payload (⇒ the plain-HTTP body shape) rather than a filesystem path?</summary>
    public static bool IsHttpUrl(string? payload)
        => payload is { Length: > 0 }
           && (payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>UTF-8 → base64url, unpadded.</summary>
    public static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        // Trim the '=' padding first (it is redundant), then swap the two non-url-safe alphabet characters.
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == '=') end--;
        return string.Create(end, raw, static (dst, src) =>
        {
            for (int i = 0; i < dst.Length; i++)
                dst[i] = src[i] switch { '+' => '-', '/' => '_', var c => c };
        });
    }

    /// <summary>base64url → UTF-8. False on any malformed payload (never throws).</summary>
    public static bool TryDecodeBase64Url(ReadOnlySpan<char> encoded, out string value)
    {
        value = "";
        if (encoded.Length == 0) return false;
        int padded = (encoded.Length + 3) & ~3;
        Span<char> buf = padded <= 512 ? stackalloc char[padded] : new char[padded];
        for (int i = 0; i < encoded.Length; i++)
            buf[i] = encoded[i] switch { '-' => '+', '_' => '/', var c => c };
        for (int i = encoded.Length; i < padded; i++) buf[i] = '=';
        Span<byte> bytes = padded <= 512 ? stackalloc byte[padded] : new byte[padded];
        if (!Convert.TryFromBase64Chars(buf, bytes, out int written) || written == 0) return false;
        try { value = Encoding.UTF8.GetString(bytes[..written]); }
        catch { return false; }
        return value.Length > 0;
    }
}
