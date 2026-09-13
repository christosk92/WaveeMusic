using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

/// <summary>Structured request-body builders for <see cref="ApiConsolePage"/> — no raw-byte editing.</summary>
static class ApiDebugBodyBuilder
{
    public static readonly string[] BodyModeLabels = ["none", "text", "extended-metadata"];

    public readonly record struct EntityLine(string Uri, Xm.ExtensionKind? Kind, string? Etag);

    /// <summary>Parse extended-metadata entity lines. Format: <c>uri [| KIND [| etag]]</c> — kind omitted ⇒ inferred from URI.</summary>
    public static (List<EntityLine> Lines, string? Error) ParseEntityLines(string text)
    {
        var lines = new List<EntityLine>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var parts = raw.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts[0].Length == 0)
                return (lines, "Empty entity line.");
            string uri = parts[0];
            Xm.ExtensionKind? kind = null;
            string? etag = null;
            if (parts.Length >= 2 && parts[1].Length > 0)
            {
                if (!ApiDebugProto.TryParseExtensionKind(parts[1], out var k))
                    return (lines, $"Unknown extension kind '{parts[1]}' on line: {raw}");
                kind = k;
            }
            else kind = InferKind(uri);
            if (kind == Xm.ExtensionKind.UnknownExtension)
                return (lines, $"Cannot infer extension kind for '{uri}' — add '| TRACK_V4' etc.");
            if (parts.Length >= 3 && parts[2].Length > 0) etag = parts[2];
            lines.Add(new EntityLine(uri, kind, etag));
        }
        if (lines.Count == 0) return (lines, "Add at least one entity line.");
        return (lines, null);
    }

    // THE routing-kind → catalogue-kind map, the same one every ladder uses. A playlist infers LIST_METADATA_V2 here
    // too, which the old EntityRef-based copy could not express.
    static Xm.ExtensionKind InferKind(string uri)
    {
        try { return XmKinds.CatalogKindOf(EntityUri.KindOf(uri)); }
        catch { return Xm.ExtensionKind.UnknownExtension; }
    }

    public static (byte[] Gzipped, Xm.BatchedEntityRequest Plain, string? Error) BuildExtendedMetadata(
        IReadOnlyList<EntityLine> lines, SessionContext session)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var request = new Xm.BatchedEntityRequest
        {
            Header = new Xm.BatchedEntityRequestHeader
            {
                Country = session.Market,
                Catalogue = session.Catalogue,
                TaskId = ByteString.CopyFrom(taskId),
            },
        };
        var byUri = new Dictionary<string, Xm.EntityRequest>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!byUri.TryGetValue(line.Uri, out var er))
            {
                er = new Xm.EntityRequest { EntityUri = line.Uri };
                byUri[line.Uri] = er;
                request.EntityRequest.Add(er);
            }
            var q = new Xm.ExtensionQuery { ExtensionKind = line.Kind!.Value };
            if (!string.IsNullOrEmpty(line.Etag)) q.Etag = line.Etag;
            er.Query.Add(q);
        }
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            request.WriteTo(gz);
        return (ms.ToArray(), request, null);
    }

    /// <summary>Bulk-hydration batch: one entity per line, kinds inferred from URI (TrackV4/AlbumV4/...). Built through
    /// the SAME envelope writer as the explicit-kind mode - the hand-rolled EntityRef/GzipRequest copy died with the
    /// unconditional bulk arm (hydration-facade-plan.md 1.6), so the console can no longer send a shape the app cannot.</summary>
    public static (byte[]? Gzipped, string? Error) BuildBulkHydration(string text, SessionContext session)
    {
        var lines = new List<EntityLine>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            string uri = raw.Split('|', 2)[0].Trim();
            if (uri.Length == 0) continue;
            var kind = InferKind(uri);
            if (kind == Xm.ExtensionKind.UnknownExtension) continue;   // no catalogue facts exist for this kind
            lines.Add(new EntityLine(uri, kind, null));
        }
        if (lines.Count == 0) return (null, "No supported entity kinds in list.");
        var (gz, _, err) = BuildExtendedMetadata(lines, session);
        return err is not null ? (null, err) : (gz, null);
    }

    public static Dictionary<string, string> ParseHeaders(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = raw.IndexOf(':');
            if (i <= 0) continue;
            string key = raw[..i].Trim();
            string val = raw[(i + 1)..].Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    public static void ApplyExtendedMetadataHeaders(Dictionary<string, string> headers)
    {
        headers["Content-Type"] = "application/protobuf";
        headers["Content-Encoding"] = "gzip";
        headers["Accept"] = "application/protobuf";
        headers["Accept-Encoding"] = "gzip, deflate, br";
    }

    public static string DefaultExtendedMetadataHeaders() =>
        "Content-Type: application/protobuf\nContent-Encoding: gzip\nAccept: application/protobuf\nAccept-Encoding: gzip, deflate, br";

    public static string DefaultEntityLinesExample() =>
        "# uri | EXTENSION_KIND | optional_etag\nspotify:track:4cOdK2wGLETKBW3PvgPWoT | TRACK_V4";

    public static byte[]? BuildTextBody(string text, bool gzip, out string? error)
    {
        error = null;
        text = text.Trim();
        if (text.Length == 0) return Array.Empty<byte>();
        var raw = Encoding.UTF8.GetBytes(text);
        if (gzip) raw = HttpCompression.Gzip(raw);
        return raw;
    }

    public static string FormatHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0) return "(no headers)";
        var sb = new StringBuilder();
        foreach (var kv in headers)
            sb.Append(kv.Key).Append(": ").AppendLine(kv.Value);
        return sb.ToString().TrimEnd();
    }
}
