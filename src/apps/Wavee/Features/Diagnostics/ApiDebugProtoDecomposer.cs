using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend.Spotify;
using Wavee.Protocol.Collection;
using Wavee.Protocol.Lean;
using Wavee.Protocol.Metadata;
using Wavee.Protocol.Playlist;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

/// <summary>Unpacks <see cref="Xm.BatchedExtensionResponse"/> <c>google.protobuf.Any</c> payloads into full diagnostic JSON.</summary>
static class ApiDebugProtoDecomposer
{
    /// <summary>Try to fully decompose an extended-metadata response; returns null if the body is not a batch response.</summary>
    public static string? TryDecompose(byte[] body)
    {
        body = ApiDebugProto.Decompress(body);
        if (body.Length == 0) return null;
        try
        {
            var resp = Xm.BatchedExtensionResponse.Parser.ParseFrom(body);
            return Decompose(resp);
        }
        catch { return null; }
    }

    /// <summary>Decompose diagnostic JSON (protobuf JSON with <c>@type</c>/<c>@value</c> Any fields) into unpacked messages.</summary>
    public static string? TryDecomposeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
            var resp = parser.Parse<Xm.BatchedExtensionResponse>(json);
            return Decompose(resp);
        }
        catch { return null; }
    }

    public static string Decompose(Xm.BatchedExtensionResponse resp)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        if (resp.Header is not null)
        {
            w.WritePropertyName("header");
            WriteDiagnostic(w, resp.Header);
        }
        w.WritePropertyName("extendedMetadata");
        w.WriteStartArray();
        foreach (var arr in resp.ExtendedMetadata)
        {
            w.WriteStartObject();
            if (arr.Header is not null)
            {
                w.WritePropertyName("header");
                WriteDiagnostic(w, arr.Header);
            }
            w.WritePropertyName("extensionKind");
            w.WriteStringValue(arr.ExtensionKind.ToString());
            w.WritePropertyName("extensionData");
            w.WriteStartArray();
            foreach (var ent in arr.ExtensionData)
            {
                w.WriteStartObject();
                if (ent.Header is not null)
                {
                    w.WritePropertyName("header");
                    WriteDiagnostic(w, ent.Header);
                }
                if (ent.HasEntityUri)
                {
                    w.WritePropertyName("entityUri");
                    w.WriteStringValue(ent.EntityUri);
                }
                WriteDecodedPayload(w, ent.ExtensionData, arr.ExtensionKind);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void WriteDecodedPayload(Utf8JsonWriter w, Any? any, Xm.ExtensionKind kind)
    {
        if (any is null || any.Value.IsEmpty)
        {
            w.WritePropertyName("decoded");
            w.WriteNullValue();
            return;
        }
        var msg = Unpack(any, kind);
        if (msg is null)
        {
            w.WritePropertyName("decoded");
            w.WriteStartObject();
            w.WriteString("@type", any.TypeUrl);
            w.WriteString("@value", Convert.ToBase64String(any.Value.ToByteArray()));
            w.WriteString("_error", "unknown Any type — not unpacked");
            w.WriteEndObject();
            return;
        }
        w.WritePropertyName("decoded");
        WriteDiagnostic(w, msg);
        WriteUriHints(w, msg);
    }

    static void WriteUriHints(Utf8JsonWriter w, IMessage msg)
    {
        string? uri = msg switch
        {
            Artist a when a.Gid.Length == 16 => "spotify:artist:" + Base62.Encode(a.Gid.Span),
            Track t when t.Gid.Length == 16 => "spotify:track:" + Base62.Encode(t.Gid.Span),
            Album al when al.Gid.Length == 16 => "spotify:album:" + Base62.Encode(al.Gid.Span),
            LeanArtist la when la.Gid.Length == 16 => "spotify:artist:" + Base62.Encode(la.Gid.Span),
            LeanTrack lt when lt.Gid.Length == 16 => "spotify:track:" + Base62.Encode(lt.Gid.Span),
            LeanAlbum lal when lal.Gid.Length == 16 => "spotify:album:" + Base62.Encode(lal.Gid.Span),
            LeanShow ls when ls.Gid.Length == 16 => "spotify:show:" + Base62.Encode(ls.Gid.Span),
            LeanEpisode le when le.Gid.Length == 16 => "spotify:episode:" + Base62.Encode(le.Gid.Span),
            _ => null,
        };
        if (uri is null) return;
        w.WritePropertyName("_uri");
        w.WriteStringValue(uri);
    }

    static IMessage? Unpack(Any any, Xm.ExtensionKind kind)
    {
        var bytes = any.Value;
        try
        {
            return kind switch
            {
                Xm.ExtensionKind.ArtistV4 => Artist.Parser.ParseFrom(bytes),
                Xm.ExtensionKind.TrackV4 => Track.Parser.ParseFrom(bytes),
                Xm.ExtensionKind.AlbumV4 => Album.Parser.ParseFrom(bytes),
                Xm.ExtensionKind.ShowV4 => LeanShow.Parser.ParseFrom(bytes),
                Xm.ExtensionKind.EpisodeV4 => LeanEpisode.Parser.ParseFrom(bytes),
                _ => UnpackByTypeUrl(any),
            };
        }
        catch
        {
            try { return UnpackByTypeUrl(any); }
            catch { return null; }
        }
    }

    static IMessage? UnpackByTypeUrl(Any any)
    {
        string url = any.TypeUrl;
        int dot = url.LastIndexOf('/');
        string name = dot >= 0 ? url[(dot + 1)..] : url;
        return name switch
        {
            "spotify.metadata.Artist" => Artist.Parser.ParseFrom(any.Value),
            "spotify.metadata.Track" => Track.Parser.ParseFrom(any.Value),
            "spotify.metadata.Album" => Album.Parser.ParseFrom(any.Value),
            "spotify.playlist4.external.proto.SelectedListContent" => SelectedListContent.Parser.ParseFrom(any.Value),
            "spotify.collection2.v2.proto.PageResponse" => PageResponse.Parser.ParseFrom(any.Value),
            _ => null,
        };
    }

    static void WriteDiagnostic(Utf8JsonWriter w, IMessage msg)
    {
        using var doc = JsonDocument.Parse(JsonFormatter.ToDiagnosticString(msg));
        doc.RootElement.WriteTo(w);
    }
}
