// ── Entities/Track.LocalUri.cs — a playlist's local file, drawn from its own uri ──────────────────────────────────────────
//
// A playlist made in the official client can list local files: `spotify:local:<artist>:<album>:<title>:<seconds>`, each
// part form-encoded (`Spotify.Egress.LocalTrackUri` writes the same shape for a local track Wavee plays). Spotify's catalogue
// knows nothing about such an item, so a row that waits for it never lands: the playlist showed every local file as an
// empty placeholder. The uri IS the row's face, the way the official client draws it: the title, the artist as the credit
// line, the duration, and a confirmed "not playable here" (Wavee has no file to play it from; the full support is a
// local-files folder matched against these names).
//
// Staged wherever a playlist member is staged — the wire's full read (`Spotify.Decode.PlaylistRevision`) and the one list
// staging the disk read and a replayed diff share (`Store.StageList`) — so every landing of the list re-states the face.
// PURE apart from the staging it writes; safe on any thread (text goes to the staging's arena, nothing is interned).

using System.Text;

namespace Wavee;

/// <summary>A playlist's <c>spotify:local:</c> item: its parts, and the row they describe.</summary>
public static class LocalTrackUri
{
    static ReadOnlySpan<byte> Prefix => "spotify:local:"u8;

    /// <summary>Is <paramref name="uri"/> a <c>spotify:local:</c> item?</summary>
    public static bool Is(ReadOnlySpan<byte> uri) => uri.StartsWith(Prefix);

    /// <inheritdoc cref="Is(ReadOnlySpan{byte})"/>
    public static bool Is(string? uri) => uri is not null && uri.StartsWith("spotify:local:", StringComparison.Ordinal);

    /// <summary>The four parts of a <c>spotify:local:artist:album:title:seconds</c> uri, form-decoded (<c>+</c> is a space,
    /// <c>%XX</c> a byte). False for anything else, or an item with no title.</summary>
    public static bool TryParse(ReadOnlySpan<byte> uri, out string artist, out string album, out string title, out int seconds)
    {
        artist = album = title = "";
        seconds = 0;
        if (!Is(uri)) return false;
        // Exactly four parts: form-encoding leaves no ':' inside one.
        var rest = uri[Prefix.Length..];
        int a = rest.IndexOf((byte)':');
        if (a < 0) return false;
        var afterArtist = rest[(a + 1)..];
        int b = afterArtist.IndexOf((byte)':');
        if (b < 0) return false;
        var afterAlbum = afterArtist[(b + 1)..];
        int c = afterAlbum.IndexOf((byte)':');
        if (c < 0) return false;
        var secondsPart = afterAlbum[(c + 1)..];
        if (secondsPart.IndexOf((byte)':') >= 0) return false;
        artist = Decode(rest[..a]);
        album = Decode(afterArtist[..b]);
        title = Decode(afterAlbum[..c]);
        if (!System.Buffers.Text.Utf8Parser.TryParse(secondsPart, out seconds, out int used) || used != secondsPart.Length || seconds < 0) seconds = 0;
        return title.Length > 0;
    }

    /// <summary>Stage the row <paramref name="uri"/> describes under <paramref name="id"/> (the member's own target): the title,
    /// the artist as the credit line, the duration, and Availability = unavailable. The face groups at <see cref="Authority.Full"/>:
    /// nothing else will ever answer for this row. No album row (a local file's album is a name, not an entity). A uri that
    /// does not parse stages nothing.</summary>
    public static void StageFace(Staging s, in StagedId id, ReadOnlySpan<byte> uri)
    {
        if (id.IsEmpty || !TryParse(uri, out string artist, out _, out string title, out int seconds)) return;
        ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)(TrackFields.Face | TrackFields.Availability));
        row.Title = s.AddText(Encoding.UTF8.GetBytes(title));
        if (artist.Length > 0) row.ArtistLine = s.AddText(Encoding.UTF8.GetBytes(artist));
        row.DurationMs = (int)Math.Min((long)seconds * 1000, int.MaxValue);
        row.Flags |= (uint)TrackFlags.Unavailable;
    }

    /// <inheritdoc cref="StageFace(Staging, in StagedId, ReadOnlySpan{byte})"/>
    public static void StageFace(Staging s, in StagedId id, string uri) => StageFace(s, in id, Encoding.UTF8.GetBytes(uri));

    /// <summary>Form decoding: <c>+</c> → space, <c>%XX</c> → that byte; the bytes are UTF-8. A malformed escape is kept as written.</summary>
    static string Decode(ReadOnlySpan<byte> part)
    {
        if (part.IsEmpty) return "";
        Span<byte> buffer = part.Length <= 256 ? stackalloc byte[part.Length] : new byte[part.Length];
        int n = 0;
        for (int i = 0; i < part.Length; i++)
        {
            byte b = part[i];
            if (b == (byte)'+') b = (byte)' ';
            else if (b == (byte)'%' && i + 2 < part.Length && TryHex(part[i + 1], out int hi) && TryHex(part[i + 2], out int lo))
            {
                b = (byte)(hi << 4 | lo);
                i += 2;
            }
            buffer[n++] = b;
        }
        return Encoding.UTF8.GetString(buffer[..n]);

        static bool TryHex(byte c, out int value)
        {
            value = c switch { >= (byte)'0' and <= (byte)'9' => c - '0', >= (byte)'a' and <= (byte)'f' => c - 'a' + 10, >= (byte)'A' and <= (byte)'F' => c - 'A' + 10, _ => -1 };
            return value >= 0;
        }
    }
}
