using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Playlist;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

/// <summary>Response decompression + protobuf/JSON pretty-printing for <see cref="ApiConsolePage"/>.</summary>
static class ApiDebugProto
{
    /// <summary>Max chars passed to TextEl/EditableText for layout — huge protobuf JSON can crash DirectWrite GetGlyphs.</summary>
    public const int MaxDisplayChars = 32_000;

    public static readonly string[] DecodeLabels =
    [
        "Auto", "Hex", "UTF-8 text", "JSON pretty",
        "ext-metadata: BatchedExtensionResponse",
        "ext-metadata: BatchedEntityRequest",
        "playlist: SelectedListContent",
        "collection: PageResponse",
    ];

    public static byte[] Decompress(byte[] body)
    {
        if (body is null or { Length: 0 }) return Array.Empty<byte>();
        body = SpotifyZstd.MaybeDecompressZstd(body);
        if (body.Length >= 2 && body[0] == 0x1F && body[1] == 0x8B) body = HttpCompression.Gunzip(body);
        else if (body.Length >= 1 && body[0] == 0x78) body = HttpCompression.Gunzip(body);   // zlib/gzip family
        return body;
    }

    public static string Format(byte[] body, int decodeIndex, string? contentType)
        => ForDisplay(FormatRaw(body, decodeIndex, contentType));

    /// <summary>Full export JSON — unpacks Any payloads in extended-metadata responses when possible.</summary>
    public static string ExportJson(byte[] body, int decodeIndex, string? contentType)
    {
        body = Decompress(body);
        if (body.Length == 0) return "(empty body)";
        int idx = decodeIndex;
        if (idx == 0) idx = GuessDecodeIndex(contentType);
        if (idx == 4)
        {
            var decomposed = ApiDebugProtoDecomposer.TryDecompose(body);
            if (decomposed is not null) return decomposed;
        }
        return FormatRaw(body, decodeIndex, contentType);
    }

    /// <summary>Full decoded body for clipboard / file export — not capped for display.</summary>
    public static string FormatRaw(byte[] body, int decodeIndex, string? contentType)
    {
        body = Decompress(body);
        if (body.Length == 0) return "(empty body)";

        int idx = decodeIndex;
        if (idx == 0) idx = GuessDecodeIndex(contentType);

        return idx switch
        {
            1 => ToHex(body, int.MaxValue),
            2 => Encoding.UTF8.GetString(body),
            3 => ToJsonPretty(body),
            4 => TryProto(body, () => Xm.BatchedExtensionResponse.Parser.ParseFrom(body)),
            5 => TryProto(body, () => Xm.BatchedEntityRequest.Parser.ParseFrom(body)),
            6 => TryProto(body, () => SelectedListContent.Parser.ParseFrom(body)),
            7 => TryProto(body, () => Wavee.Protocol.Collection.PageResponse.Parser.ParseFrom(body)),
            _ => TryAuto(body, contentType),
        };
    }

    public static string SuggestExportFileName(int decodeIndex, string? urlOrPath)
    {
        string stem = decodeIndex switch
        {
            4 => "batched-extension-response",
            5 => "batched-entity-request",
            6 => "selected-list-content",
            7 => "page-response",
            3 => "response",
            2 => "response",
            1 => "response-hex",
            _ => "api-response",
        };
        if (urlOrPath is { Length: > 0 })
        {
            int slash = urlOrPath.LastIndexOf('/');
            string tail = slash >= 0 ? urlOrPath[(slash + 1)..] : urlOrPath;
            foreach (char c in Path.GetInvalidFileNameChars()) tail = tail.Replace(c, '_');
            if (tail.Length > 0) stem = tail + "-" + stem;
        }
        return stem + ".json";
    }

    public static string SuggestRawFileName(string? urlOrPath)
    {
        string stem = "response";
        if (urlOrPath is { Length: > 0 })
        {
            int slash = urlOrPath.LastIndexOf('/');
            string tail = slash >= 0 ? urlOrPath[(slash + 1)..] : urlOrPath;
            foreach (char c in Path.GetInvalidFileNameChars()) tail = tail.Replace(c, '_');
            if (tail.Length > 0) stem = tail;
        }
        return stem + ".bin";
    }

    public static bool LooksExportable(string formatted)
        => formatted.Length > 0
        && !formatted.StartsWith("(empty body)", StringComparison.Ordinal)
        && !formatted.StartsWith("(proto parse failed", StringComparison.Ordinal)
        && !formatted.StartsWith("(not JSON:", StringComparison.Ordinal);

    /// <summary>Strip chars that break DirectWrite shaping and cap length for on-screen layout. Copy buttons use raw bytes.</summary>
    public static string ForDisplay(string text)
    {
        text = SanitizeLayoutText(text);
        if (text.Length <= MaxDisplayChars) return text;
        return text[..MaxDisplayChars] + $"\n… ({text.Length:N0} chars total, truncated for display — use Copy response for full body)";
    }

    static string SanitizeLayoutText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\0') continue;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(s[++i]);
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static int GuessDecodeIndex(string? contentType)
    {
        if (contentType is { } ct)
        {
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return 3;
            if (ct.Contains("protobuf", StringComparison.OrdinalIgnoreCase)) return 4;
        }
        return 2;
    }

    static string TryAuto(byte[] body, string? contentType)
    {
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var json = ToJsonPretty(body);
            if (!json.StartsWith('(')) return json;
        }
        foreach (var attempt in new Func<string>[]
        {
            () => TryProto(body, () => Xm.BatchedExtensionResponse.Parser.ParseFrom(body)),
            () => TryProto(body, () => SelectedListContent.Parser.ParseFrom(body)),
            () => TryProto(body, () => Wavee.Protocol.Collection.PageResponse.Parser.ParseFrom(body)),
        })
        {
            var s = attempt();
            if (!s.StartsWith("(proto parse failed", StringComparison.Ordinal)) return s;
        }
        return LooksText(body) ? Encoding.UTF8.GetString(body) : ToHex(body, int.MaxValue);
    }

    static string TryProto<T>(byte[] body, Func<T> parse) where T : IMessage
    {
        try
        {
            var msg = parse();
            return JsonFormatter.ToDiagnosticString(msg);
        }
        catch (Exception ex)
        {
            return $"(proto parse failed: {ex.Message})\n\n{ToHex(body, max: 512)}";
        }
    }

    static string ToJsonPretty(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Re-emit the parsed document with a writer instead of round-tripping it through the (reflection-based,
            // trim/AOT-unsafe) serializer — the document already holds the shape we want to indent.
            var buf = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true })) doc.WriteTo(w);
            return Encoding.UTF8.GetString(buf.WrittenSpan);
        }
        catch (Exception ex) { return $"(not JSON: {ex.Message})\n\n{Encoding.UTF8.GetString(body)}"; }
    }

    static bool LooksText(byte[] body)
    {
        int sample = Math.Min(body.Length, 256);
        int weird = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = body[i];
            if (b is 9 or 10 or 13 or >= 32 and < 127) continue;
            weird++;
        }
        return weird < sample / 8;
    }

    public static string ToHex(byte[] body, int max = 8192)
    {
        int n = Math.Min(body.Length, max);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(i % 16 == 0 ? '\n' : ' ');
            sb.Append(body[i].ToString("X2"));
        }
        if (body.Length > max) sb.Append($"\n… ({body.Length:N0} bytes total, truncated)");
        return sb.ToString();
    }

    /// <summary>Parse an <see cref="Xm.ExtensionKind"/> from a numeric string, C# name (<c>ArtistV4</c>), or proto wire name (<c>ARTIST_V4</c>).</summary>
    public static bool TryParseExtensionKind(string? text, out Xm.ExtensionKind kind)
    {
        kind = Xm.ExtensionKind.UnknownExtension;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (int.TryParse(text, out int n) && Enum.IsDefined(typeof(Xm.ExtensionKind), n))
        {
            kind = (Xm.ExtensionKind)n;
            return true;
        }
        if (TryParseExtensionKindName(text, out kind)) return true;
        if (text.Contains('_', StringComparison.Ordinal))
            return TryParseExtensionKindName(ProtoSnakeToPascal(text), out kind);
        return false;
    }

    static bool TryParseExtensionKindName(string name, out Xm.ExtensionKind kind)
        => Enum.TryParse(name, ignoreCase: true, out kind) && kind != Xm.ExtensionKind.UnknownExtension;

    static string ProtoSnakeToPascal(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return snake;
        var sb = new StringBuilder(snake.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1) sb.Append(part.AsSpan(1).ToString().ToLowerInvariant());
        }
        return sb.ToString();
    }
}
