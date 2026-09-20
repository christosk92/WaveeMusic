using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>JSON POST for <c>/playlistextender/extendp/</c> (the "recommended songs" playlist extender). The server
/// infers the playlist's own tracks from <c>playlistURI</c>; <c>trackSkipIDs</c> carries ONLY the previously-shown
/// recommendation ids (starts empty, ACCUMULATES across Refresh so each batch is fresh). AOT-safe: <see cref="JsonDocument"/>
/// parse, no reflection. The response arrives zstd on the wire (the transport advertises only gzip/br), so the body runs
/// through <see cref="SpotifyZstd.MaybeDecompressZstd"/> first (a non-zstd body passes through unchanged). Fail-soft:
/// returns an empty batch on any non-OK status or parse failure.</summary>
public sealed class PlaylistExtenderClient
{
    readonly ITransport _transport;

    public PlaylistExtenderClient(ITransport transport) => _transport = transport;

    static Dictionary<string, string> JsonHeaders => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "application/json",
        ["Accept"] = "application/json",
    };

    public async Task<IReadOnlyList<Track>> ExtendAsync(
        string playlistUri, IReadOnlyList<string> skipTrackIds, int numResults, CancellationToken ct = default)
    {
        var body = BuildBody(playlistUri, skipTrackIds, numResults);
        var r = await _transport.Request(Channel.SpclientWg, "/playlistextender/extendp/", body, ct, "POST", JsonHeaders)
            .ConfigureAwait(false);
        if (!r.Ok)
        {
            PlaylistMutationDiagnostics.ExtendFailed(playlistUri, r.Status);
            return Array.Empty<Track>();
        }
        return Parse(SpotifyZstd.MaybeDecompressZstd(r.Body));
    }

    // {"playlistURI":"spotify:playlist:<id>","trackSkipIDs":[<base62 id>,…],"numResults":20}
    static ReadOnlyMemory<byte> BuildBody(string playlistUri, IReadOnlyList<string> skipTrackIds, int numResults)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("playlistURI", playlistUri);
            w.WriteStartArray("trackSkipIDs");
            for (int i = 0; i < skipTrackIds.Count; i++) w.WriteStringValue(skipTrackIds[i]);
            w.WriteEndArray();
            w.WriteNumber("numResults", numResults);
            w.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    static IReadOnlyList<Track> Parse(byte[] json)
    {
        if (json is null || json.Length == 0) return Array.Empty<Track>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recommendedTracks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<Track>();
            var list = new List<Track>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
                if (MapTrack(e) is { } t) list.Add(t);
            return list;
        }
        catch { return Array.Empty<Track>(); }
    }

    // recommendedTracks[i]: { id, originalId:"spotify:track:<id>", name, artists:[{id,name}],
    //                         album:{id,name,imageUrl,largeImageUrl}, duration(ms), explicit, popularity, score, contentRating[] }
    static Track? MapTrack(JsonElement e)
    {
        string id = GetStr(e, "id");
        string uri = GetStr(e, "originalId");
        if (uri.Length == 0 && id.Length > 0) uri = "spotify:track:" + id;
        if (id.Length == 0) id = EntityUri.IdOf(uri);
        if (id.Length == 0 && uri.Length == 0) return null;
        string name = GetStr(e, "name");

        IReadOnlyList<ArtistRef> artists;
        if (e.TryGetProperty("artists", out var ja) && ja.ValueKind == JsonValueKind.Array)
        {
            var al = new List<ArtistRef>(ja.GetArrayLength());
            foreach (var a in ja.EnumerateArray())
            {
                string aid = GetStr(a, "id");
                al.Add(new ArtistRef(aid, aid.Length > 0 ? "spotify:artist:" + aid : "", GetStr(a, "name")));
            }
            artists = al;
        }
        else artists = Array.Empty<ArtistRef>();

        AlbumRef album;
        Image? cover = null;
        if (e.TryGetProperty("album", out var jalbum) && jalbum.ValueKind == JsonValueKind.Object)
        {
            string alid = GetStr(jalbum, "id");
            album = new AlbumRef(alid, alid.Length > 0 ? "spotify:album:" + alid : "", GetStr(jalbum, "name"));
            string img = GetStr(jalbum, "largeImageUrl");
            if (img.Length == 0) img = GetStr(jalbum, "imageUrl");
            if (img.Length > 0) cover = new Image(img);
        }
        else album = new AlbumRef("", "", "");

        long duration = e.TryGetProperty("duration", out var jd) && jd.TryGetInt64(out var dv) ? dv : 0L;
        bool isExplicit = e.TryGetProperty("explicit", out var je) && je.ValueKind == JsonValueKind.True;

        return new Track(id, uri, name, artists, album, duration, isExplicit, cover);
    }

    static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

}
