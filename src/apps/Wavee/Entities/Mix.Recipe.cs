// ── Entities/Mix.Recipe.cs — CORE (#48 Mix, plan §3.2) ────────────────────────────────────────────────────────────────
//
// Spotify's stored transition: the base64 protobuf in a playlist item's `automix.auto_transition_recipe` attribute, on
// the OUTGOING item, present only for an edited pair (an Auto pair has none: the client computes it). Field numbers are
// the capture report's §4.2; the confidence of each is in the comment ("?" = meaning not proven). The decoder keeps the
// raw bytes beside the struct so a later write can carry every field this struct does not model verbatim.

using System.Text;

namespace Wavee;

/// <summary>Spotify's <c>TransitionRecipe</c> (capture report §4.2). Plain values; the track uris and analysis ids are
/// strings because the recipe is read rarely (a list read) and written rarely (a Save), never per frame.</summary>
public struct MixRecipe
{
    /// <summary>1.1 / 1.2: the from and to item ids (hex strings on the wire, 16 chars); 0 in the "previous" recipe of a Save.</summary>
    public ulong FromItemId, ToItemId;
    /// <summary>1.3: <c>fade_out_start_time</c>, ms into A (matches the Connect metadata). [inferred]</summary>
    public uint OutStartMs;
    /// <summary>1.4?: <c>fade_in_start_time</c>, ms into B; absent = 0. [guess]</summary>
    public uint InStartMs;
    /// <summary>1.5: the overlap, ms. [inferred]</summary>
    public uint OverlapMs;
    /// <summary>1.6? / 1.7?: playback rates (1.0 in every sample). [guess]</summary>
    public float RateA, RateB;
    /// <summary>1.8?: beatmatching (0 in every sample). [guess]</summary>
    public bool Beatmatch;
    /// <summary>1.10 / 1.11: the two tracks' uris (1.12 / 1.13 repeat them). [wire]</summary>
    public string? TrackA, TrackB;
    /// <summary>1.14 / 1.15: kind 217's analysis id (32 hex) of A and B, the grid the times refer to. [wire-matched]</summary>
    public string? BeatsIdA, BeatsIdB;
    /// <summary>1.16 / 1.17: the BPMs frozen at save time. [inferred]</summary>
    public float BpmA, BpmB;
    /// <summary>2.1: the preset id (1 = Fade, 3 = Rise). [inferred]</summary>
    public byte Preset;
    /// <summary>2.2?: 1 in every sample.</summary>
    public byte StyleVersion;

    public readonly bool IsEmpty => OverlapMs == 0 && OutStartMs == 0;

    /// <summary>Decode the raw protobuf (already un-base64'd). False when it carries no timing at all.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> proto, out MixRecipe recipe)
    {
        recipe = default;
        recipe.RateA = recipe.RateB = 1f;
        bool timing = false;
        for (var r = new Spotify.Decode.ProtoReader(proto); r.Next();)
        {
            if (r.Field == 1 && r.Wire == 2)
            {
                timing = true;
                for (var t = r.Message(); t.Next();)
                {
                    switch (t.Field)
                    {
                        case 1 when t.Wire == 2: recipe.FromItemId = Hex64(t.Bytes()); break;
                        case 2 when t.Wire == 2: recipe.ToItemId = Hex64(t.Bytes()); break;
                        case 3 when t.Wire == 0: recipe.OutStartMs = (uint)t.Varint(); break;
                        case 4 when t.Wire == 0: recipe.InStartMs = (uint)t.Varint(); break;
                        case 5 when t.Wire == 0: recipe.OverlapMs = (uint)t.Varint(); break;
                        case 6 when t.Wire == 5: recipe.RateA = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                        case 7 when t.Wire == 5: recipe.RateB = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                        case 8 when t.Wire == 0: recipe.Beatmatch = t.Varint() != 0; break;
                        case 10 when t.Wire == 2: recipe.TrackA = Encoding.UTF8.GetString(t.Bytes()); break;
                        case 11 when t.Wire == 2: recipe.TrackB = Encoding.UTF8.GetString(t.Bytes()); break;
                        case 14 when t.Wire == 2: recipe.BeatsIdA = Encoding.UTF8.GetString(t.Bytes()); break;
                        case 15 when t.Wire == 2: recipe.BeatsIdB = Encoding.UTF8.GetString(t.Bytes()); break;
                        case 16 when t.Wire == 5: recipe.BpmA = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                        case 17 when t.Wire == 5: recipe.BpmB = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                        default: t.Skip(); break;
                    }
                }
            }
            else if (r.Field == 2 && r.Wire == 2)
            {
                for (var st = r.Message(); st.Next();)
                {
                    if (st.Field == 1 && st.Wire == 0) recipe.Preset = (byte)st.Varint();
                    else if (st.Field == 2 && st.Wire == 0) recipe.StyleVersion = (byte)st.Varint();
                    else st.Skip();
                }
            }
            else r.Skip();
        }
        return timing && !recipe.IsEmpty;
    }

    /// <summary>Decode the attribute's base64 value. False on bad base64 or a recipe without timing.</summary>
    public static bool TryDecodeBase64(ReadOnlySpan<byte> base64Utf8, out MixRecipe recipe, out byte[] raw)
    {
        recipe = default;
        raw = [];
        if (base64Utf8.IsEmpty) return false;
        var buffer = new byte[base64Utf8.Length];
        if (System.Buffers.Text.Base64.DecodeFromUtf8(base64Utf8, buffer, out _, out int written) != System.Buffers.OperationStatus.Done)
            return false;
        raw = buffer.AsSpan(0, written).ToArray();
        return TryDecode(raw, out recipe);
    }

    /// <summary>Encode. Fields this struct does not model (1.12, 1.13, 1.18, 1.19, f3, f4) are copied from
    /// <paramref name="carry"/> (the recipe being replaced) when it has them, else written with the captured
    /// constants (1.12 = 1.10, 1.13 = 1.11, 1.18 = 1.19 = 1.0, f3 = 1, f4 = 1). Returns the byte count.</summary>
    public static int Encode(in MixRecipe recipe, ReadOnlySpan<byte> carry, Span<byte> into)
    {
        double f18 = 1.0, f19 = 1.0;
        ulong f3 = 1, f4 = 1;
        for (var r = new Spotify.Decode.ProtoReader(carry); r.Next();)
        {
            if (r.Field == 1 && r.Wire == 2)
            {
                for (var t = r.Message(); t.Next();)
                {
                    if (t.Field == 18 && t.Wire == 1) f18 = t.Double();
                    else if (t.Field == 19 && t.Wire == 1) f19 = t.Double();
                    else t.Skip();
                }
            }
            else if (r.Field == 3 && r.Wire == 0) f3 = r.Varint();
            else if (r.Field == 4 && r.Wire == 0) f4 = r.Varint();
            else r.Skip();
        }

        var w = new Spotify.Decode.ProtoWriter(into);
        int timing = w.Open(1);
        Span<byte> hex = stackalloc byte[16];
        if (recipe.FromItemId != 0) { WriteHex64(recipe.FromItemId, hex); w.Utf8(1, hex); }
        if (recipe.ToItemId != 0) { WriteHex64(recipe.ToItemId, hex); w.Utf8(2, hex); }
        w.U(3, recipe.OutStartMs);
        w.U(4, recipe.InStartMs);
        w.U(5, recipe.OverlapMs);
        w.F32(6, recipe.RateA == 0 ? 1f : recipe.RateA);
        w.F32(7, recipe.RateB == 0 ? 1f : recipe.RateB);
        w.B(8, recipe.Beatmatch);
        w.Str(10, recipe.TrackA);
        w.Str(11, recipe.TrackB);
        w.Str(12, recipe.TrackA);
        w.Str(13, recipe.TrackB);
        w.Str(14, recipe.BeatsIdA);
        w.Str(15, recipe.BeatsIdB);
        w.F32(16, recipe.BpmA);
        w.F32(17, recipe.BpmB);
        w.F64(18, f18);
        w.F64(19, f19);
        w.Close(timing);
        int style = w.Open(2);
        w.U(1, recipe.Preset);
        w.U(2, recipe.StyleVersion == 0 ? 1u : recipe.StyleVersion);
        w.Close(style);
        w.U(3, f3);
        w.U(4, f4);
        return w.Length;
    }

    /// <summary>A 16-hex item id → its 64-bit value; 0 when malformed.</summary>
    public static ulong Hex64(ReadOnlySpan<byte> hex)
    {
        if (hex.IsEmpty || hex.Length > 16) return 0;
        ulong v = 0;
        foreach (byte c in hex)
        {
            int d = c is >= (byte)'0' and <= (byte)'9' ? c - '0'
                  : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10
                  : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;
            if (d < 0) return 0;
            v = (v << 4) | (uint)d;
        }
        return v;
    }

    /// <summary>A hex item id held as a .NET string (the edge's interned item id) → its 64-bit value.</summary>
    public static ulong Hex64(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length > 16) return 0;
        Span<byte> b = stackalloc byte[hex.Length];
        for (int i = 0; i < hex.Length; i++) b[i] = (byte)hex[i];
        return Hex64(b);
    }

    static void WriteHex64(ulong v, Span<byte> into)
    {
        for (int i = 15; i >= 0; i--) { int d = (int)(v & 0xF); into[i] = (byte)(d < 10 ? '0' + d : 'a' + d - 10); v >>= 4; }
    }

    /// <summary>One line for diagnostics and logs: the same fields the capture's recipe decoder prints.</summary>
    public readonly override string ToString()
        => $"from={FromItemId:x16} to={ToItemId:x16} out={OutStartMs} in={InStartMs} overlap={OverlapMs} rate={RateA:0.###}/{RateB:0.###} "
         + $"beatmatch={(Beatmatch ? 1 : 0)} bpm={BpmA:0.###}/{BpmB:0.###} preset={Preset} v={StyleVersion} "
         + $"beats={BeatsIdA ?? "-"}/{BeatsIdB ?? "-"} a={TrackA ?? "-"} b={TrackB ?? "-"}";
}
