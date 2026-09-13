using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Wavee.Backend.Metadata;
using ZstdSharp;

namespace Wavee.Backend.Persistence;

// ── library.db cache-tier payload framing (schema v5) ─────────────────────────────────────────────────────────────────
// Every cache-tier blob (`entity.payload`, `artist_overview.payload`) carries a 1-BYTE FORMAT PREFIX so the stored bytes
// are self-describing: a reader never has to trust the `fmt` column (which is kept in sync purely for SQL-side stats and
// for a future re-encode sweep). 0 = raw STJ JSON, 1 = zstd(JSON) — 2 is reserved for zstd + a trained dictionary.
//
// This is the ONLY place that knows the framing: SqliteColdStore routes every payload read and write through it, so
// callers (CachedStore/Replay/ColdFallback) keep seeing plain UTF-8 JSON exactly as they did pre-v5.
public static class PayloadCodec
{
    public const int FmtRawJson = 0;
    public const int FmtZstd = 1;

    /// <summary>zstd level 3 — the same level the transport guard uses; single-digit-µs decode for ~1 KB rows.</summary>
    public const int ZstdLevel = 3;

    // ZstdSharp's Compressor/Decompressor are stateful and NOT thread-safe. One instance per thread (the writer thread,
    // the migration/ctor thread, whichever thread reads) beats allocating a fresh context per row during a 30k-row migrate.
    [ThreadStatic] static Compressor? _comp;
    [ThreadStatic] static Decompressor? _decomp;

    /// <summary>Frame <paramref name="json"/> for storage under <paramref name="fmt"/> (prefix byte + body).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> json, int fmt)
    {
        if (fmt == FmtZstd && json.Length > 0)
        {
            var comp = _comp ??= new Compressor(ZstdLevel);
            var body = comp.Wrap(json);
            var packed = new byte[body.Length + 1];
            packed[0] = FmtZstd;
            body.CopyTo(packed.AsSpan(1));
            return packed;
        }
        var raw = new byte[json.Length + 1];
        raw[0] = FmtRawJson;
        json.CopyTo(raw.AsSpan(1));
        return raw;
    }

    /// <summary>The format byte of a stored blob (raw for an empty/absent payload).</summary>
    public static int FormatOf(byte[]? stored) => stored is { Length: > 0 } ? stored[0] : FmtRawJson;

    /// <summary>Unframe a stored blob back to the raw UTF-8 JSON bytes the callers deserialize.</summary>
    public static byte[] Decode(byte[]? stored)
    {
        if (stored is null || stored.Length == 0) return Array.Empty<byte>();
        int fmt = stored[0];
        if (stored.Length == 1) return Array.Empty<byte>();
        if (fmt != FmtZstd) return stored.AsSpan(1).ToArray();   // 0 (and any unknown future fmt) = pass the body through

        try
        {
            var d = _decomp ??= new Decompressor();
            var un = d.Unwrap(stored.AsSpan(1));
            if (un.Length > 0) return un.ToArray();
        }
        catch (Exception)
        {
            _decomp = null;   // a faulted context is not reusable
        }
        // Frames written without a content-size header (or a faulted one-shot) decode frame-by-frame instead.
        using var src = new MemoryStream(stored, 1, stored.Length - 1, writable: false);
        using var zs = new DecompressionStream(src);
        using var dst = new MemoryStream();
        zs.CopyTo(dst);
        return dst.ToArray();
    }
}

/// <summary>The display-critical scalars lifted out of an entity payload into `entity`'s thin columns, plus the pin-closure
/// child uris (tracks only: its album + artists). <c>Refs</c> is null for every other kind.</summary>
public readonly record struct EntityThin(
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    long? DurationMs,
    long Flags,
    string? AlbumUri,
    IReadOnlyList<string>? Refs);

// One-pass JsonDocument extraction of the thin columns. Deliberately NOT a full STJ deserialize: the migration walks tens
// of thousands of rows and the write path runs per upsert on the writer thread, and neither needs the record graph.
//
// PROPERTY CASING: `EntityJson` (CachedStore.cs) is a source-gen context with only DefaultIgnoreCondition set — no
// PropertyNamingPolicy — so members serialize under their DECLARED PascalCase names ("Title", "DurationMs", "IsExplicit",
// …). ColdStoreThinColumnTests pins that against a real EntityJson payload. The camelCase probe below is a cheap
// belt-and-braces so a future naming-policy flip degrades to "thin columns unpopulated", never to wrong data.
public static class EntityThinExtractor
{
    public const long FlagExplicit = 1L << 0;
    public const long FlagHasVideo = 1L << 1;

    /// <summary>Extract the thin columns for <paramref name="kind"/>. Returns false for an unparseable / non-object
    /// payload — the caller then stores the row with null thin columns (never drops it).</summary>
    public static bool TryExtract(byte[]? json, EntityKind kind, out EntityThin thin)
    {
        thin = default;
        if (json is null || json.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            switch (kind)
            {
                case EntityKind.Track:
                {
                    var refs = new List<string>(4);
                    string? albumUri = null;
                    if (TryProp(root, "Album", out var album) && album.ValueKind == JsonValueKind.Object)
                    {
                        albumUri = NullIfEmpty(Str(album, "Uri"));
                        if (albumUri is not null) refs.Add(albumUri);
                    }
                    long flags = 0;
                    if (Flag(root, "IsExplicit")) flags |= FlagExplicit;
                    if (Flag(root, "HasVideo")) flags |= FlagHasVideo;
                    thin = new EntityThin(
                        NullIfEmpty(Str(root, "Title")), ArtistNames(root, refs), ImageUrl(root, "Image"),
                        Num(root, "DurationMs"), flags, albumUri, refs);
                    return true;
                }
                case EntityKind.Album:
                    thin = new EntityThin(
                        NullIfEmpty(Str(root, "Name")), ArtistNames(root, null), ImageUrl(root, "Cover"),
                        null, 0, null, null);
                    return true;
                case EntityKind.Artist:
                    thin = new EntityThin(NullIfEmpty(Str(root, "Name")), null, ImageUrl(root, "Image"), null, 0, null, null);
                    return true;
                case EntityKind.Playlist:
                    thin = new EntityThin(
                        NullIfEmpty(Str(root, "Name")), NullIfEmpty(Str(root, "OwnerName")), ImageUrl(root, "Cover"),
                        null, 0, null, null);
                    return true;
                case EntityKind.Show:
                    thin = new EntityThin(
                        NullIfEmpty(Str(root, "Name")), NullIfEmpty(Str(root, "Publisher")), ImageUrl(root, "Cover"),
                        null, 0, null, null);
                    return true;
                case EntityKind.Episode:
                    thin = new EntityThin(
                        NullIfEmpty(Str(root, "Title")), NullIfEmpty(Str(root, "ShowName")), ImageUrl(root, "Image"),
                        Num(root, "DurationMs"), 0, null, null);
                    return true;
                // An owner row: the display name is the title column, the avatar the image. It owns no outgoing refs —
                // an Owner points at nothing, which is exactly why it needs no pin closure of its own.
                case EntityKind.User:
                    thin = new EntityThin(NullIfEmpty(Str(root, "Name")), null, ImageUrl(root, "Avatar"), null, 0, null, null);
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }   // non-UTF8 / malformed byte sequence
    }

    static string? ArtistNames(JsonElement o, List<string>? uris)
    {
        if (!TryProp(o, "Artists", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        StringBuilder? sb = null;
        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var name = NullIfEmpty(Str(a, "Name"));
            if (name is not null)
            {
                sb ??= new StringBuilder(48);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name);
            }
            if (uris is not null && NullIfEmpty(Str(a, "Uri")) is { } u) uris.Add(u);
        }
        return sb?.ToString();
    }

    static string? ImageUrl(JsonElement o, string property)
    {
        if (!TryProp(o, property, out var img) || img.ValueKind != JsonValueKind.Object) return null;
        if (NullIfEmpty(Str(img, "Url")) is { } url) return url;
        // A cover-less playlist carries only mosaic tiles — the first tile is its list-render image.
        if (TryProp(img, "MosaicTiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array)
            foreach (var t in tiles.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && NullIfEmpty(t.GetString()) is { } tile) return tile;
        return null;
    }

    static bool TryProp(JsonElement o, string name, out JsonElement value)
    {
        if (o.TryGetProperty(name, out value)) return true;
        Span<char> alt = stackalloc char[name.Length];
        name.AsSpan().CopyTo(alt);
        alt[0] = char.ToLowerInvariant(alt[0]);
        return o.TryGetProperty(alt, out value);
    }

    static string? Str(JsonElement o, string name)
        => TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? Num(JsonElement o, string name)
        => TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static bool Flag(JsonElement o, string name) => TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
