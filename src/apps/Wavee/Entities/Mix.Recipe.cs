// ── Entities/Mix.Recipe.cs — CORE (#48 Mix, plan §2.1.1) ─────────────────────────────────────────────────────────────────
//
// Spotify's stored transition: the base64 protobuf in a playlist item's `automix.auto_transition_recipe` attribute, on
// the OUTGOING item, present only for an edited pair (an Auto pair has none: the client computes it). The schema is the
// official client's own codec (its TransitionRecipe / Overlap / Preset messages):
//
//   TransitionRecipe { 1 overlap Overlap, 2 preset Preset, 3 isOverlapOverride, 4 isPresetIdOverride, 5 beatmatchPreference }
//   Overlap { 1/2 trackA/BRowId (16 hex), 3 startAMs, 4 startBMs, 5 durationMs, 6/7 speedA/B float, 8 durationBars (always
//             written), 9 isBeatmatched, 10/11 trackA/BUri, 12/13 trackA/BPlayableUri, 14/15 playable beats hash (kind 217's
//             analysis id), 16/17 bpmA/B float, 18/19 itemSpeedA/B double }
//   Preset { 1 id (MixPresetId), 2 type (MixPresetType), 3/4/5/12/15/16 volume/eq/filterFx/fx/jogwheel/looping style override
//            {1 id}, 6-11, 13, 14, 17 curve and block overrides }
//
// LOSSLESS BY PATCHING. The struct models the scalar fields and the six style-override ids; the curve and block overrides
// and every field it does not know stay in the raw bytes. Encode walks the raw message the recipe was decoded from, at
// every level: a field whose model value equals what the raw holds is copied byte for byte, a changed one is rewritten in
// its place, an unknown one is copied in its place, and a field the model sets but the raw lacks is inserted in
// field-number order. So decode → encode with no change gives back the same bytes, and a change touches only its own
// field (and the length prefixes around it). With no raw (a recipe Wavee builds itself) it writes the official shape:
// 1.8 always, every other zero-valued field left out.

using System.Text;

namespace Wavee;

/// <summary>Recipe 2.2: the kind of preset the transition is.</summary>
public enum MixPresetType : byte { None = 0, Fade = 1, Cut = 2, BeatmatchedFade = 3, BeatmatchedCut = 4 }

/// <summary>Recipe top-level 5: the beatmatch choice the user made for the pair.</summary>
public enum MixBeatmatchPreference : byte { Auto = 0, Off = 1, On = 2 }

/// <summary>Spotify's <c>TransitionRecipe</c>, field names from the official client's codec. Plain values; the track uris and
/// analysis ids are strings because the recipe is read rarely (a list read) and written rarely (a save), never per frame. A
/// field the raw lacks reads as proto3's default (0, false, null).</summary>
public partial struct MixRecipe
{
    // ── 1 Overlap ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>1.1 / 1.2 <c>trackA/BRowId</c>: the from and to item ids (16 hex chars on the wire); absent in a save's baseline.</summary>
    public ulong FromItemId, ToItemId;
    /// <summary>1.3 <c>startAMs</c>: where the transition starts in A (<c>audio.fade_out_start_time</c>); absent for "no transition".</summary>
    public uint OutStartMs;
    /// <summary>1.4 <c>startBMs</c>: where B enters (<c>audio.fade_in_start_time</c>).</summary>
    public uint InStartMs;
    /// <summary>1.5 <c>durationMs</c>: the overlap; 1 for "no transition".</summary>
    public uint OverlapMs;
    /// <summary>1.6 / 1.7 <c>speedA/B</c>: the beatmatch playback speeds (B stretched to A's tempo; A stays 1.0).</summary>
    public float SpeedA, SpeedB;
    /// <summary>1.8 <c>durationBars</c>: the overlap in bars (0 when not beatmatched). The official encoder always writes it.</summary>
    public int DurationBars;
    /// <summary>1.9 <c>isBeatmatched</c>.</summary>
    public bool IsBeatmatched;
    /// <summary>1.10 / 1.11 <c>trackA/BUri</c>.</summary>
    public string? TrackA, TrackB;
    /// <summary>1.12 / 1.13 <c>trackA/BPlayableUri</c>: the uris that actually play (a relinked track's differs from 1.10 / 1.11).</summary>
    public string? PlayableA, PlayableB;
    /// <summary>1.14 / 1.15 <c>trackA/BPlayableBeatsHash</c>: kind 217's analysis id (32 hex), the grid the times refer to.</summary>
    public string? BeatsIdA, BeatsIdB;
    /// <summary>1.16 / 1.17 <c>bpmA/B</c>: the tempos frozen at save time.</summary>
    public float BpmA, BpmB;
    /// <summary>1.18 / 1.19 <c>itemSpeedA/B</c> (1.0 in every sample).</summary>
    public double ItemSpeedA, ItemSpeedB;

    // ── 2 Preset ───────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>2.1: the preset id (<see cref="MixPresetId"/>); 0 (absent) = no transition.</summary>
    public byte Preset;
    /// <summary>2.2: the preset's type.</summary>
    public MixPresetType PresetType;
    /// <summary>2.3 / 2.4 / 2.5 / 2.12 / 2.15 / 2.16: a style override <c>{1 id}</c> per ingredient (volume, EQ, filter/FX,
    /// effects, jog wheel, looping); null = the preset's own style. An override to style 0 ("None") is present with id 0.</summary>
    public int? VolumeStyle, EqStyle, FilterFxStyle, FxStyle, JogwheelStyle, LoopingStyle;
    /// <summary>READ-ONLY: which curve and block overrides (Preset fields 6-11, 13, 14, 17; bit n = field n) the raw holds. The
    /// encoder never reads this.</summary>
    public uint CurveOverrides;
    /// <summary>The curve overrides (Preset fields 6-11, 13, 14) decoded (Mix.Recipe.Curves.cs): what the planner plays in place of the
    /// styles' curves on those lanes, and what the editor edits. A field whose value the model leaves as decoded is copied byte for
    /// byte; an edited one is written from the model. The loop blocks (2.17) stay in the raw only. Null = none.</summary>
    public MixCurveOverrides? Curves;

    // ── top level ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>3 <c>isOverlapOverride</c>: the timing was set by hand (absent on "no transition").</summary>
    public bool IsOverlapOverride;
    /// <summary>4 <c>isPresetIdOverride</c>: the preset was chosen by hand.</summary>
    public bool IsPresetIdOverride;
    /// <summary>5 <c>beatmatchPreference</c>.</summary>
    public MixBeatmatchPreference BeatmatchPreference;

    public readonly bool IsEmpty => OverlapMs == 0 && OutStartMs == 0;

    /// <summary>The official "no transition": no preset id (the enum's None). It also carries a 1 ms overlap and no out-point
    /// (A plays to its end, B from its start); the preset id alone decides, a recipe with no preset is never played as a fade.</summary>
    public readonly bool IsNoTransition => Preset == (byte)MixPresetId.None;

    /// <summary>The six style overrides as the style table takes them (<see cref="MixStyles.Resolve"/>): an absent field is null
    /// (the preset's own style holds), an override to id 0 is that ingredient's None.</summary>
    public readonly MixStyleOverrides StyleOverrides() => new(
        VolumeStyle is { } v ? (MixVolumeStyle)(sbyte)v : null,
        EqStyle is { } e ? (MixEqStyle)(sbyte)e : null,
        FilterFxStyle is { } f ? (MixFilterStyle)(sbyte)f : null,
        FxStyle is { } x ? (MixFxStyle)(sbyte)x : null,
        JogwheelStyle,
        LoopingStyle is { } l ? (MixLoopingStyle)(sbyte)l : null);

    /// <summary>Decode the raw protobuf (already un-base64'd). False when it carries no timing at all.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> proto, out MixRecipe recipe)
    {
        recipe = Decode(proto, out bool timing);
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

    /// <summary>Everything the raw holds that the model knows. Repeated messages merge and a repeated scalar's last value wins,
    /// as in any protobuf reader; a field with an unexpected wire type is not the model's (it stays an unknown field).</summary>
    static MixRecipe Decode(ReadOnlySpan<byte> proto, out bool timing)
    {
        var r = default(MixRecipe);
        timing = false;
        for (var top = new Spotify.Decode.ProtoReader(proto); top.Next();)
        {
            if (!Modeled(Level.Top, top.Field, top.Wire)) { top.Skip(); continue; }
            switch (top.Field)
            {
                case 1: timing = true; DecodeOverlap(top.Bytes(), ref r); break;
                case 2: DecodePreset(top.Bytes(), ref r); break;
                case 3: r.IsOverlapOverride = top.Varint() != 0; break;
                case 4: r.IsPresetIdOverride = top.Varint() != 0; break;
                case 5: r.BeatmatchPreference = (MixBeatmatchPreference)(byte)top.Varint(); break;
            }
        }
        return r;
    }

    static void DecodeOverlap(ReadOnlySpan<byte> proto, ref MixRecipe r)
    {
        for (var t = new Spotify.Decode.ProtoReader(proto); t.Next();)
        {
            if (!Modeled(Level.Overlap, t.Field, t.Wire)) { t.Skip(); continue; }
            switch (t.Field)
            {
                case 1: r.FromItemId = Hex64(t.Bytes()); break;
                case 2: r.ToItemId = Hex64(t.Bytes()); break;
                case 3: r.OutStartMs = (uint)t.Varint(); break;
                case 4: r.InStartMs = (uint)t.Varint(); break;
                case 5: r.OverlapMs = (uint)t.Varint(); break;
                case 6: r.SpeedA = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                case 7: r.SpeedB = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                case 8: r.DurationBars = t.Int32(); break;
                case 9: r.IsBeatmatched = t.Varint() != 0; break;
                case 10: r.TrackA = Encoding.UTF8.GetString(t.Bytes()); break;
                case 11: r.TrackB = Encoding.UTF8.GetString(t.Bytes()); break;
                case 12: r.PlayableA = Encoding.UTF8.GetString(t.Bytes()); break;
                case 13: r.PlayableB = Encoding.UTF8.GetString(t.Bytes()); break;
                case 14: r.BeatsIdA = Encoding.UTF8.GetString(t.Bytes()); break;
                case 15: r.BeatsIdB = Encoding.UTF8.GetString(t.Bytes()); break;
                case 16: r.BpmA = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                case 17: r.BpmB = BitConverter.UInt32BitsToSingle(t.Fixed32()); break;
                case 18: r.ItemSpeedA = t.Double(); break;
                case 19: r.ItemSpeedB = t.Double(); break;
            }
        }
    }

    static void DecodePreset(ReadOnlySpan<byte> proto, ref MixRecipe r)
    {
        for (var p = new Spotify.Decode.ProtoReader(proto); p.Next();)
        {
            if (p.Field is >= 6 and <= 17 && p.Field != 12 && p.Wire == 2) r.CurveOverrides |= 1u << p.Field;
            if (!Modeled(Level.Preset, p.Field, p.Wire)) { p.Skip(); continue; }   // the loop blocks (2.17) and unknown fields: raw only
            if (MixCurveOverrides.IsCurveField(p.Field)) { r.Curves = MixCurveOverrides.Decode(p.Field, p.Bytes(), r.Curves); continue; }
            switch (p.Field)
            {
                case 1: r.Preset = (byte)p.Varint(); break;
                case 2: r.PresetType = (MixPresetType)(byte)p.Varint(); break;
                default: Style(ref r, StyleIndex(p.Field)) = DecodeStyle(p.Bytes(), Style(ref r, StyleIndex(p.Field))); break;
            }
        }
    }

    /// <summary>A style override message <c>{1 id}</c>, merged onto what an earlier occurrence said.</summary>
    static int? DecodeStyle(ReadOnlySpan<byte> proto, int? prior)
    {
        int id = prior ?? 0;
        for (var s = new Spotify.Decode.ProtoReader(proto); s.Next();)
        {
            if (Modeled(Level.Style, s.Field, s.Wire)) id = s.Int32();
            else s.Skip();
        }
        return id;
    }

    // ── the patcher ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The message levels the model knows; <see cref="Level.Style"/> is one style override <c>{1 id}</c>.</summary>
    enum Level : byte { Top, Overlap, Preset, Style }

    static readonly byte[] s_topFields = [1, 2, 3, 4, 5];
    static readonly byte[] s_overlapFields = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
    static readonly byte[] s_presetFields = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    static readonly byte[] s_styleFields = [1];
    /// <summary>The Preset fields of the six style overrides, in <see cref="StyleIndex"/> order.</summary>
    static readonly byte[] s_styleOverrideFields = [3, 4, 5, 12, 15, 16];

    static ReadOnlySpan<byte> FieldsOf(Level level) => level switch
    {
        Level.Top => s_topFields,
        Level.Overlap => s_overlapFields,
        Level.Preset => s_presetFields,
        _ => s_styleFields,
    };

    /// <summary>Is (field, wire) a field the model holds at this level? The decoder and the patcher both ask this, so a field
    /// with an unexpected wire type is an unknown field to both.</summary>
    static bool Modeled(Level level, int field, int wire) => level switch
    {
        Level.Top => field switch { 1 or 2 => wire == 2, 3 or 4 or 5 => wire == 0, _ => false },
        Level.Overlap => field switch
        {
            1 or 2 or 10 or 11 or 12 or 13 or 14 or 15 => wire == 2,
            3 or 4 or 5 or 8 or 9 => wire == 0,
            6 or 7 or 16 or 17 => wire == 5,
            18 or 19 => wire == 1,
            _ => false,
        },
        Level.Preset => field switch { 1 or 2 => wire == 0, >= 3 and <= 16 => wire == 2, _ => false },
        _ => field == 1 && wire == 0,
    };

    static bool Nested(Level level, int field) => level switch
    {
        Level.Top => field is 1 or 2,
        Level.Preset => field is 3 or 4 or 5 or 12 or 15 or 16,
        _ => false,
    };

    static Level Child(Level level, int field) => level == Level.Top ? (field == 1 ? Level.Overlap : Level.Preset) : Level.Style;

    static int StyleIndex(int presetField) => presetField switch { 3 => 0, 4 => 1, 5 => 2, 12 => 3, 15 => 4, _ => 5 };

    static ref int? Style(ref MixRecipe r, int index)
    {
        switch (index)
        {
            case 0: return ref r.VolumeStyle;
            case 1: return ref r.EqStyle;
            case 2: return ref r.FilterFxStyle;
            case 3: return ref r.FxStyle;
            case 4: return ref r.JogwheelStyle;
            default: return ref r.LoopingStyle;
        }
    }

    static int? StyleAt(in MixRecipe r, int index) => index switch
    {
        0 => r.VolumeStyle, 1 => r.EqStyle, 2 => r.FilterFxStyle, 3 => r.FxStyle, 4 => r.JogwheelStyle, _ => r.LoopingStyle,
    };

    static bool SameText(string? a, string? b) => string.IsNullOrEmpty(a) ? string.IsNullOrEmpty(b) : string.Equals(a, b, StringComparison.Ordinal);
    static bool SameF(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);
    static bool SameD(double a, double b) => BitConverter.DoubleToUInt64Bits(a) == BitConverter.DoubleToUInt64Bits(b);

    /// <summary>Does the model hold the same value for this field as the raw it was decoded from?</summary>
    static bool Same(Level level, int style, int field, in MixRecipe a, in MixRecipe b)
    {
        switch (level)
        {
            case Level.Top:
                return field switch
                {
                    1 => SameOverlap(in a, in b),
                    2 => SamePreset(in a, in b),
                    3 => a.IsOverlapOverride == b.IsOverlapOverride,
                    4 => a.IsPresetIdOverride == b.IsPresetIdOverride,
                    _ => a.BeatmatchPreference == b.BeatmatchPreference,
                };
            case Level.Overlap:
                return field switch
                {
                    1 => a.FromItemId == b.FromItemId,
                    2 => a.ToItemId == b.ToItemId,
                    3 => a.OutStartMs == b.OutStartMs,
                    4 => a.InStartMs == b.InStartMs,
                    5 => a.OverlapMs == b.OverlapMs,
                    6 => SameF(a.SpeedA, b.SpeedA),
                    7 => SameF(a.SpeedB, b.SpeedB),
                    8 => a.DurationBars == b.DurationBars,
                    9 => a.IsBeatmatched == b.IsBeatmatched,
                    10 => SameText(a.TrackA, b.TrackA),
                    11 => SameText(a.TrackB, b.TrackB),
                    12 => SameText(a.PlayableA, b.PlayableA),
                    13 => SameText(a.PlayableB, b.PlayableB),
                    14 => SameText(a.BeatsIdA, b.BeatsIdA),
                    15 => SameText(a.BeatsIdB, b.BeatsIdB),
                    16 => SameF(a.BpmA, b.BpmA),
                    17 => SameF(a.BpmB, b.BpmB),
                    18 => SameD(a.ItemSpeedA, b.ItemSpeedA),
                    _ => SameD(a.ItemSpeedB, b.ItemSpeedB),
                };
            case Level.Preset:
                return field switch
                {
                    1 => a.Preset == b.Preset,
                    2 => a.PresetType == b.PresetType,
                    _ when MixCurveOverrides.IsCurveField(field) => MixCurveOverrides.SameField(a.Curves, b.Curves, field),
                    _ => StyleAt(in a, StyleIndex(field)) == StyleAt(in b, StyleIndex(field)),
                };
            default:
                return StyleAt(in a, style) == StyleAt(in b, style);
        }
    }

    static bool SameOverlap(in MixRecipe a, in MixRecipe b)
    {
        foreach (byte f in s_overlapFields) if (!Same(Level.Overlap, 0, f, in a, in b)) return false;
        return true;
    }

    static bool SamePreset(in MixRecipe a, in MixRecipe b)
    {
        foreach (byte f in s_presetFields) if (!Same(Level.Preset, 0, f, in a, in b)) return false;
        return true;
    }

    /// <summary>Does the model set this field (so a rewrite writes it and an insert adds it)? Zero values are left out, as the
    /// official encoder does, except 1.8, which it always writes; a nested message is set when anything in it is.</summary>
    static bool Present(Level level, int style, int field, in MixRecipe r)
    {
        switch (level)
        {
            case Level.Top:
                return field switch
                {
                    1 => true,                                   // 1.8 is always written: the Overlap message never is empty
                    2 => r.Preset != 0 || r.PresetType != 0 || r.VolumeStyle.HasValue || r.EqStyle.HasValue || r.FilterFxStyle.HasValue
                         || r.FxStyle.HasValue || r.JogwheelStyle.HasValue || r.LoopingStyle.HasValue || r.Curves is { IsEmpty: false },
                    3 => r.IsOverlapOverride,
                    4 => r.IsPresetIdOverride,
                    _ => r.BeatmatchPreference != 0,
                };
            case Level.Overlap:
                return field switch
                {
                    1 => r.FromItemId != 0,
                    2 => r.ToItemId != 0,
                    3 => r.OutStartMs != 0,
                    4 => r.InStartMs != 0,
                    5 => r.OverlapMs != 0,
                    6 => BitConverter.SingleToUInt32Bits(r.SpeedA) != 0,
                    7 => BitConverter.SingleToUInt32Bits(r.SpeedB) != 0,
                    8 => true,
                    9 => r.IsBeatmatched,
                    10 => !string.IsNullOrEmpty(r.TrackA),
                    11 => !string.IsNullOrEmpty(r.TrackB),
                    12 => !string.IsNullOrEmpty(r.PlayableA),
                    13 => !string.IsNullOrEmpty(r.PlayableB),
                    14 => !string.IsNullOrEmpty(r.BeatsIdA),
                    15 => !string.IsNullOrEmpty(r.BeatsIdB),
                    16 => BitConverter.SingleToUInt32Bits(r.BpmA) != 0,
                    17 => BitConverter.SingleToUInt32Bits(r.BpmB) != 0,
                    18 => BitConverter.DoubleToUInt64Bits(r.ItemSpeedA) != 0,
                    _ => BitConverter.DoubleToUInt64Bits(r.ItemSpeedB) != 0,
                };
            case Level.Preset:
                return field switch
                {
                    1 => r.Preset != 0,
                    2 => r.PresetType != 0,
                    _ when MixCurveOverrides.IsCurveField(field) => r.Curves?.HasField(field) == true,
                    _ => StyleAt(in r, StyleIndex(field)).HasValue,
                };
            default:
                return StyleAt(in r, style) is { } id && id != 0;
        }
    }

    /// <summary>One scalar field's model value, tag included.</summary>
    static void WriteScalar(ref Spotify.Decode.ProtoWriter w, Level level, int style, int field, in MixRecipe r)
    {
        switch (level)
        {
            case Level.Top:
                if (field == 5) w.UAlways(5, (ulong)r.BeatmatchPreference);
                else w.UAlways(field, 1);                        // 3 / 4: a true bool (only a present one is written)
                return;
            case Level.Overlap:
                switch (field)
                {
                    case 1 or 2:
                        Span<byte> hex = stackalloc byte[16];
                        WriteHex64(field == 1 ? r.FromItemId : r.ToItemId, hex);
                        w.Utf8Always(field, hex);
                        return;
                    case 3: w.UAlways(3, r.OutStartMs); return;
                    case 4: w.UAlways(4, r.InStartMs); return;
                    case 5: w.UAlways(5, r.OverlapMs); return;
                    case 6: w.F32(6, r.SpeedA); return;
                    case 7: w.F32(7, r.SpeedB); return;
                    case 8: w.UAlways(8, (ulong)(long)r.DurationBars); return;
                    case 9: w.UAlways(9, 1); return;
                    case 10: w.StrAlways(10, r.TrackA); return;
                    case 11: w.StrAlways(11, r.TrackB); return;
                    case 12: w.StrAlways(12, r.PlayableA); return;
                    case 13: w.StrAlways(13, r.PlayableB); return;
                    case 14: w.StrAlways(14, r.BeatsIdA); return;
                    case 15: w.StrAlways(15, r.BeatsIdB); return;
                    case 16: w.F32(16, r.BpmA); return;
                    case 17: w.F32(17, r.BpmB); return;
                    case 18: w.F64(18, r.ItemSpeedA); return;
                    default: w.F64(19, r.ItemSpeedB); return;
                }
            case Level.Preset:
                // A curve override field is one message written whole from the model (only an edited one gets here).
                if (MixCurveOverrides.IsCurveField(field)) { r.Curves?.WriteField(ref w, field); return; }
                w.UAlways(field, field == 1 ? r.Preset : (ulong)r.PresetType);
                return;
            default:
                w.UAlways(1, (ulong)(long)(StyleAt(in r, style) ?? 0));
                return;
        }
    }

    /// <summary>Per level instance, which model fields were already written (or dropped): a field that occurs twice in the
    /// raw is rewritten once, at its first occurrence. Bit n = field n; the six style overrides keep one bit each.</summary>
    struct PatchState
    {
        public ulong Top, Overlap, Preset;
        public byte Styles;

        public readonly bool Has(Level level, int style, int field) => level switch
        {
            Level.Top => (Top & (1UL << field)) != 0,
            Level.Overlap => (Overlap & (1UL << field)) != 0,
            Level.Preset => (Preset & (1UL << field)) != 0,
            _ => (Styles & (1 << style)) != 0,
        };

        public void Mark(Level level, int style, int field)
        {
            switch (level)
            {
                case Level.Top: Top |= 1UL << field; break;
                case Level.Overlap: Overlap |= 1UL << field; break;
                case Level.Preset: Preset |= 1UL << field; break;
                default: Styles |= (byte)(1 << style); break;
            }
        }
    }

    /// <summary>Encode <paramref name="recipe"/> by patching <paramref name="raw"/>, the bytes it was decoded from (empty for a
    /// recipe Wavee built: the official shape). Returns the byte count; size <paramref name="into"/> with
    /// <see cref="MaxEncodedBytes"/>.</summary>
    public static int Encode(in MixRecipe recipe, ReadOnlySpan<byte> raw, Span<byte> into)
    {
        var was = Decode(raw, out _);
        var w = new Spotify.Decode.ProtoWriter(into);
        var state = new PatchState();
        Patch(ref w, Level.Top, 0, raw, fresh: raw.IsEmpty, first: true, in was, in recipe, ref state);
        return w.Length;
    }

    /// <summary><see cref="Encode"/> into a new array of the exact size.</summary>
    public static byte[] EncodeToArray(in MixRecipe recipe, ReadOnlySpan<byte> raw)
    {
        var buffer = new byte[MaxEncodedBytes(in recipe, raw)];
        int n = Encode(in recipe, raw, buffer);
        return buffer.AsSpan(0, n).ToArray();
    }

    /// <summary>An upper bound on <see cref="Encode"/>'s size: the raw, every model field rewritten once, and the reserve the
    /// writer keeps for each open length prefix.</summary>
    public static int MaxEncodedBytes(in MixRecipe r, ReadOnlySpan<byte> raw)
    {
        int text = 0;
        foreach (var s in (ReadOnlySpan<string?>)[r.TrackA, r.TrackB, r.PlayableA, r.PlayableB, r.BeatsIdA, r.BeatsIdB])
            if (s is not null) text += Encoding.UTF8.GetMaxByteCount(s.Length) + 8;
        return raw.Length + text + 512 + (r.Curves?.MaxBytes() ?? 0);
    }

    /// <summary>One level of the patch: copy, rewrite or drop each raw field in place, and insert the model's missing fields in
    /// field-number order. <paramref name="fresh"/>: the raw has no such message at all (build it as the official encoder
    /// would, 1.8 always). <paramref name="first"/>: the first occurrence of this message in its parent (only it takes inserts).</summary>
    static void Patch(ref Spotify.Decode.ProtoWriter w, Level level, int style, ReadOnlySpan<byte> raw, bool fresh, bool first,
        in MixRecipe was, in MixRecipe now, ref PatchState state)
    {
        var fields = FieldsOf(level);
        // The model fields this occurrence holds, then the ones to insert: set in the model, missing here, not written yet, and
        // either new (a changed value) or part of a message being built from nothing.
        ulong seen = 0;
        for (var scan = new Spotify.Decode.ProtoReader(raw); scan.Next();)
        {
            if (Modeled(level, scan.Field, scan.Wire)) seen |= 1UL << scan.Field;
            scan.Skip();
        }
        ulong pending = 0;
        if (first)
            foreach (byte f in fields)
                if ((seen & (1UL << f)) == 0 && !state.Has(level, style, f) && Present(level, style, f, in now)
                    && (fresh || !Same(level, style, f, in was, in now)))
                    pending |= 1UL << f;

        var r = new Spotify.Decode.ProtoReader(raw);
        while (true)
        {
            int start = r.Position;
            if (!r.Next())
            {
                Insert(ref w, level, style, ref pending, int.MaxValue, in was, in now, ref state);
                if (start < raw.Length) w.Raw(raw[start..]);     // a malformed tail: carried as it is
                return;
            }
            int field = r.Field;
            Insert(ref w, level, style, ref pending, field, in was, in now, ref state);
            if (!Modeled(level, field, r.Wire)) { r.Skip(); w.Raw(raw[start..r.Position]); continue; }
            bool nested = Nested(level, field);
            ReadOnlySpan<byte> payload = default;
            if (nested) payload = r.Bytes(); else r.Skip();
            if (Same(level, style, field, in was, in now)) { w.Raw(raw[start..r.Position]); continue; }

            bool firstOccurrence = !state.Has(level, style, field);
            state.Mark(level, style, field);
            if (!nested)
            {
                // A changed scalar: the new value at its first occurrence (nothing when it went back to the default); a repeat is dropped.
                if (firstOccurrence && Present(level, style, field, in now)) WriteScalar(ref w, level, style, field, in now);
                continue;
            }
            Level child = Child(level, field);
            int childStyle = child == Level.Style ? StyleIndex(field) : 0;
            if (child == Level.Style && !StyleAt(in now, childStyle).HasValue) continue;   // the override was removed
            int m = w.Open(field);
            Patch(ref w, child, childStyle, payload, fresh: false, firstOccurrence, in was, in now, ref state);
            w.Close(m);
        }
    }

    /// <summary>Write every pending insert numbered below <paramref name="before"/>; a nested one is built from nothing.</summary>
    static void Insert(ref Spotify.Decode.ProtoWriter w, Level level, int style, ref ulong pending, int before,
        in MixRecipe was, in MixRecipe now, ref PatchState state)
    {
        while (pending != 0)
        {
            int f = System.Numerics.BitOperations.TrailingZeroCount(pending);
            if (f >= before) return;
            pending &= pending - 1;
            state.Mark(level, style, f);
            if (!Nested(level, f)) { WriteScalar(ref w, level, style, f, in now); continue; }
            Level child = Child(level, f);
            int m = w.Open(f);
            Patch(ref w, child, child == Level.Style ? StyleIndex(f) : 0, default, fresh: true, first: true, in was, in now, ref state);
            w.Close(m);
        }
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

    /// <summary>One line for diagnostics and logs: every modelled field, named as the official codec names it.</summary>
    public readonly override string ToString()
        => $"from={FromItemId:x16} to={ToItemId:x16} out={OutStartMs} in={InStartMs} overlap={OverlapMs} speed={SpeedA:0.######}/{SpeedB:0.######} "
         + $"bars={DurationBars} beatmatched={(IsBeatmatched ? 1 : 0)} bpm={BpmA:0.###}/{BpmB:0.###} itemSpeed={ItemSpeedA:0.###}/{ItemSpeedB:0.###} "
         + $"preset={Preset} type={PresetType} styles={Styles()} curves=0x{CurveOverrides:x} curveLanes={Curves?.ToString() ?? "-"} overlapOverride={(IsOverlapOverride ? 1 : 0)} "
         + $"presetOverride={(IsPresetIdOverride ? 1 : 0)} beatmatch={BeatmatchPreference} beats={BeatsIdA ?? "-"}/{BeatsIdB ?? "-"} "
         + $"a={TrackA ?? "-"} b={TrackB ?? "-"} playA={PlayableA ?? "-"} playB={PlayableB ?? "-"}";

    /// <summary>The style overrides as <c>volume:5,eq:15</c> ("-" when none).</summary>
    public readonly string Styles()
    {
        var sb = new StringBuilder();
        ReadOnlySpan<string> names = ["volume", "eq", "filterFx", "fx", "jogwheel", "looping"];
        for (int i = 0; i < names.Length; i++)
            if (StyleAt(in this, i) is { } id) sb.Append(sb.Length > 0 ? "," : "").Append(names[i]).Append(':').Append(id);
        return sb.Length == 0 ? "-" : sb.ToString();
    }
}
