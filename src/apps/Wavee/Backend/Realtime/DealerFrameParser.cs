using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Wavee.Backend.Realtime;

public enum DealerFrameType { Unknown, Ping, Pong, Message, Request }

/// <summary>A decoded dealer WebSocket frame: the <see cref="Type"/>, the message <see cref="Uri"/> (the hm:// topic) or
/// request <see cref="MessageIdent"/>, the <see cref="Headers"/> (the <c>Spotify-Connection-Id</c> lives here), the reply
/// <see cref="Key"/> (REQUEST frames), and the decoded <see cref="Payload"/> (base64 → bytes, gunzipped if the frame is
/// Transfer-Encoding: gzip, or — for a REQUEST — the singular <c>payload.compressed</c> base64 → gunzip → JSON).</summary>
public readonly record struct DealerFrame(
    DealerFrameType Type, string? Uri, byte[] Payload,
    IReadOnlyDictionary<string, string>? Headers = null, string? Key = null, string? MessageIdent = null);

// Reflection-free (AOT-safe) Utf8JsonReader parse of a dealer frame — ported from the reference protocol. MESSAGE frames
// carry a `payloads` array (base64 chunks, concatenated, gunzipped if the gzip header is set); REQUEST frames carry a
// SINGULAR `payload` object whose `compressed` field is base64 → gzip → the command JSON (NOT the `payloads` array).
public static class DealerFrameParser
{
    public static DealerFrame Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty) return new DealerFrame(DealerFrameType.Unknown, null, Array.Empty<byte>());
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
            var type = DealerFrameType.Unknown;
            string? uri = null, key = null, ident = null;
            byte[]? payload = null;
            bool gzip = false;
            Dictionary<string, string>? headers = null;

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    type = reader.ValueTextEquals("ping"u8) ? DealerFrameType.Ping
                         : reader.ValueTextEquals("pong"u8) ? DealerFrameType.Pong
                         : reader.ValueTextEquals("message"u8) ? DealerFrameType.Message
                         : reader.ValueTextEquals("request"u8) ? DealerFrameType.Request
                         : DealerFrameType.Unknown;
                }
                else if (reader.ValueTextEquals("uri"u8)) { reader.Read(); uri = reader.GetString(); }
                else if (reader.ValueTextEquals("key"u8)) { reader.Read(); key = reader.GetString(); }
                else if (reader.ValueTextEquals("message_ident"u8)) { reader.Read(); ident = reader.GetString(); }
                else if (reader.ValueTextEquals("headers"u8)) { reader.Read(); headers = ReadHeaders(ref reader, out gzip); }
                else if (reader.ValueTextEquals("payloads"u8)) { reader.Read(); payload = ReadPayloads(ref reader); }
                else if (reader.ValueTextEquals("payload"u8)) { reader.Read(); payload = ReadRequestPayload(ref reader, ref gzip); }
            }

            if (payload is not null && gzip) payload = Gunzip(payload);
            return new DealerFrame(type, uri, payload ?? Array.Empty<byte>(), headers, key, ident);
        }
        catch
        {
            return new DealerFrame(DealerFrameType.Unknown, null, Array.Empty<byte>());
        }
    }

    // Read the full headers object (case-insensitive); also report whether Transfer-Encoding: gzip is set.
    static Dictionary<string, string>? ReadHeaders(ref Utf8JsonReader reader, out bool gzip)
    {
        gzip = false;
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string name = reader.GetString() ?? "";
            reader.Read();
            string value = reader.TokenType == JsonTokenType.String ? (reader.GetString() ?? "") : "";
            map[name] = value;
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                gzip = true;
        }
        return map.Count == 0 ? null : map;
    }

    // MESSAGE frames: a `payloads` array of base64 chunks → decode each → concatenate the BYTES (multi-chunk cluster
    // pushes). NOT every payloads[] entry is a base64 chunk, though: social-connect/v2/broadcast_status_update ships a
    // plain JSON OBJECT there instead. Letting either shape throw out of here used to propagate to Parse's outer catch
    // (:59), which discards the type/uri already read off the earlier "type"/"uri" properties and reports the WHOLE
    // frame as Unknown with no uri — visible in the dealer archive as a broadcast push with its topic lost. Skip just
    // the offending entry so the router still gets something to classify the frame by, even with an empty payload.
    static byte[]? ReadPayloads(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return null;
        using var ms = new MemoryStream();
        bool any = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType == JsonTokenType.String)
            {
                try
                {
                    var chunk = reader.GetBytesFromBase64();
                    ms.Write(chunk, 0, chunk.Length);
                    any = true;
                }
                catch (FormatException) { /* a non-base64 string entry — skip it, keep scanning the array */ }
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();   // an object/array entry (broadcast_status_update) — nothing to decode, skip the value
            }
        }
        return any ? ms.ToArray() : null;
    }

    // REQUEST frames: a singular `payload` OBJECT whose `compressed` field is base64 → gzip → the command JSON. Always gzip.
    static byte[]? ReadRequestPayload(ref Utf8JsonReader reader, ref bool gzip)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        byte[]? compressed = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            bool isCompressed = reader.ValueTextEquals("compressed"u8);
            reader.Read();
            if (isCompressed && reader.TokenType == JsonTokenType.String) compressed = reader.GetBytesFromBase64();
        }
        if (compressed is not null) gzip = true;   // the compressed request payload is always gzip
        return compressed;
    }

    static byte[] Gunzip(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }
}
