using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Google.Protobuf;
using Wavee.Backend.Spotify;

namespace Wavee.Backend.Hydration;

// ── Kind 15 (USER_PROFILE) payload decode ────────────────────────────────────────────────────────────────────────────
// The body is NOT JSON. Captured out of the cold extension cache (2026-08-30, five accounts) it is a protobuf whose
// shape mirrors the /user-profile-view/v3/profile JSON, with wrapper messages around every scalar:
//   1: { 1: string }             username       ("spotify", "qmusicnl", "1158957764")
//   2: { 1: string }             display name   ("Spotify", "#Catherine🌞")
//   3: { 1: w, 2: h, 3: url }    image, REPEATED (64×64 then 300×300 in every capture)
//   4/6/9/10: { 1: bool }        flags · 11: { 1: varint } followers · 24: { 1: string } — none rendered here
// The JSON assumption was inherited from the WinUI-era client, which fed these bytes to a JSON parser, swallowed the
// exception and fell back to REST for EVERY owner — the batch arm never answered once. We sniff instead of assuming:
// ExtensionEtagCache hands over the raw extension_data bytes with no header to say how they are encoded, so a
// compressed frame is unwrapped first, a JSON object still parses (should the server ever send one), and anything
// else throws with the head bytes so the reader's log line says what we actually got.

/// <summary>What a kind-15 body carries that the app renders. URI-INDEPENDENT on purpose: the reader caches this, and
/// the canonical id is always the one the caller asked with (see <c>SpotifyUserProfileFetch.ToOwner</c>).</summary>
public sealed record UserProfilePayload(string? Username, string? Name, string? ImageUrl)
{
    /// <summary>A name or an avatar — anything the owner row can show beyond the bare id.</summary>
    public bool IsRenderable => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(ImageUrl);
}

/// <summary>Pure, engine-free decode of the kind-15 body. Null only for a genuinely empty body; throws
/// <see cref="FormatException"/> (with the byte count and the first 8 bytes as hex) for anything it cannot decode.</summary>
public static class UserProfilePayloadDecoder
{
    const int HeadBytes = 8;

    public static UserProfilePayload? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return null;
        if (IsZstd(payload)) return Decode(Unwrap(payload, SpotifyZstd.MaybeDecompressZstd));
        if (IsGzip(payload)) return Decode(Unwrap(payload, Gunzip));

        // JSON first, judged on the first non-whitespace byte: 0x0A is BOTH a JSON newline and the protobuf tag of
        // field 1, so "starts with 0x0A" alone cannot tell the two apart — "\n{" is JSON, "0A 09 0A 07 spotify" is not.
        int i = 0;
        while (i < payload.Length && IsJsonWhitespace(payload[i])) i++;
        if (i < payload.Length && payload[i] is (byte)'{' or (byte)'[')
        {
            try
            {
                using var doc = JsonDocument.Parse(payload.ToArray());
                return FromJson(doc.RootElement);
            }
            catch (JsonException ex) { throw Undecodable(payload, ex); }
        }

        // A single-byte length-delimited tag (fields 1..15, wire type 2) is how every captured body starts.
        if ((payload[0] & 0x07) == 2 && payload[0] < 0x80)
        {
            try { return FromProto(payload.ToArray()) ?? throw Undecodable(payload, null); }
            catch (InvalidProtocolBufferException ex) { throw Undecodable(payload, ex); }
        }

        throw Undecodable(payload, null);
    }

    /// <summary>The JSON shape, shared with the REST arm (<c>/user-profile-view/v3/profile</c>): spclient says
    /// <c>name</c>/<c>image_url</c>, the Web API says <c>display_name</c>/<c>images[0].url</c>.</summary>
    public static UserProfilePayload FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new UserProfilePayload(null, null, null);
        return new UserProfilePayload(
            StringValue(root, "username"),
            StringValue(root, "name") ?? StringValue(root, "display_name"),
            StringValue(root, "image_url") ?? FirstImage(root));
    }

    /// <summary>The captured protobuf. Null when the walk found none of the three fields we know — bytes that merely
    /// happen to start with a valid tag are undecodable, not an anonymous profile.</summary>
    static UserProfilePayload? FromProto(byte[] bytes)
    {
        string? username = null, name = null, url = null;
        long bestArea = -1;
        bool any = false;
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited) { input.SkipLastField(); continue; }
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: username = ReadWrappedString(input); any = true; break;
                case 2: name = ReadWrappedString(input); any = true; break;
                case 3:
                {
                    // Several sizes ride along; the owner row wants the largest (the 300×300 one in every capture).
                    var (w, h, u) = ReadImage(input);
                    any = true;
                    long area = (long)w * h;
                    if (u is { Length: > 0 } && area > bestArea) { bestArea = area; url = u; }
                    break;
                }
                default: input.SkipLastField(); break;
            }
        }
        return any ? new UserProfilePayload(username, name, url) : null;
    }

    /// <summary>A <c>{ 1: string }</c> wrapper (google.protobuf.StringValue's shape), read in place under a limit.</summary>
    static string? ReadWrappedString(CodedInputStream outer)
    {
        var input = new CodedInputStream(outer.ReadBytes().ToByteArray());   // PushLimit/PopLimit are internal to Google.Protobuf: walk the body on its own stream
        string? value = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited)) value = input.ReadString();
            else input.SkipLastField();
        }
        return value;
    }

    static (int Width, int Height, string? Url) ReadImage(CodedInputStream outer)
    {
        var input = new CodedInputStream(outer.ReadBytes().ToByteArray());   // PushLimit/PopLimit are internal to Google.Protobuf: walk the body on its own stream
        int w = 0, h = 0;
        string? url = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == WireFormat.MakeTag(1, WireFormat.WireType.Varint)) w = input.ReadInt32();
            else if (tag == WireFormat.MakeTag(2, WireFormat.WireType.Varint)) h = input.ReadInt32();
            else if (tag == WireFormat.MakeTag(3, WireFormat.WireType.LengthDelimited)) url = input.ReadString();
            else input.SkipLastField();
        }
        return (w, h, url);
    }

    static bool IsZstd(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD;
    static bool IsGzip(ReadOnlySpan<byte> b) => b.Length >= 2 && b[0] == 0x1F && b[1] == 0x8B;
    static bool IsJsonWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>A frame that will not inflate is as undecodable as garbage — report it with the same head.</summary>
    static byte[] Unwrap(ReadOnlySpan<byte> payload, Func<byte[], byte[]> inflate)
    {
        try { return inflate(payload.ToArray()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw Undecodable(payload, ex); }
    }

    static byte[] Gunzip(byte[] body)
    {
        using var src = new MemoryStream(body);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    static FormatException Undecodable(ReadOnlySpan<byte> payload, Exception? inner)
        => new("undecodable USER_PROFILE payload: " + payload.Length + " bytes, head " + HeadHex(payload), inner);

    /// <summary>The first 8 bytes as upper-case hex — the one thing a log line needs to name an unknown encoding.</summary>
    public static string HeadHex(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(payload[..Math.Min(HeadBytes, payload.Length)]);

    static string? StringValue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    static string? FirstImage(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
            if (image.ValueKind == JsonValueKind.Object && StringValue(image, "url") is { Length: > 0 } url) return url;
        return null;
    }
}
