// ── Entities/Mix.Editor.cs — #48 Mix M6, the transition editor's model (full-support plan §2.5; M2's save path) ──────────────
//
// The editor edits one pair (playlist row i → i + 1) as a DRAFT: what the pair plays (Auto, a preset, "No transition"), its timing in the
// recipe's own terms (startAMs, startBMs, durationMs, durationBars, isBeatmatched), the Customize style overrides and the hand-drawn volume
// curves. The draft opens on what the pair plays now (its Spotify recipe, this PC's record, or Spotify's Auto) and saves through M2's path:
//
//   the user may write the list and the item carries the set-transition template      everyone else: this PC (MixOverrides)
//   ───────────────────────────────────────────────────────────────────────           ─────────────────────────────────────
//   a preset  → set-transition with the recipe patched over the one it replaces        the local record ("Saved on this PC only")
//   No transition → the "none" recipe; Auto → Reset (set-transition, no payload)
//   offline or refused: kept as a PENDING local record (it plays here), with Retry
//
// ONLY WHAT WAS EDITED CHANGES. A draft read from a recipe holds that recipe's own values, and the save writes a field only when the draft
// moved it (`Build(from, to)`), so the patching encoder copies every other field byte for byte (M2's changedFields check). A draft that
// did not come from a recipe (Auto, No transition) builds the official shape from nothing.
//
// Auto is a state, not a preset: touching anything (a preset, a point, Customize) makes the pair a preset with Auto's timing.

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>What a draft plays: Spotify's Auto (saving it is a reset), a preset, or Spotify's "No transition".</summary>
public enum MixDraftMode : byte { Auto, Preset, None }

/// <summary>The editor's draft of one pair (see the file header). Times are the recipe's: A's out-point, B's in-point and the overlap,
/// in ms of each song's own media; <see cref="Bars"/> is recipe 1.8 (0 when not beatmatched). A style override is null when the
/// preset's own style plays; <see cref="Curves"/> holds the hand-drawn curves (recipe 2.6 to 2.14).</summary>
public readonly record struct MixDraft(
    MixDraftMode Mode, byte Preset, uint OutStartMs, uint InStartMs, uint OverlapMs, int Bars, bool Beatmatched,
    MixBeatmatchPreference BeatmatchPreference, int? Volume, int? Eq, int? Filter, int? Fx, int? Looping, MixCurveOverrides? Curves)
{
    /// <summary>A preset with a Customize change (a style override or a hand-drawn curve): Spotify's "Custom".</summary>
    public bool Custom => Mode == MixDraftMode.Preset && (Volume.HasValue || Eq.HasValue || Filter.HasValue || Fx.HasValue || Looping.HasValue
                                                          || Curves is { IsEmpty: false });

    /// <summary>The styles the draft plays: its preset's with its overrides in place.</summary>
    public MixStyleSet Styles => MixStyles.Resolve(Preset, new MixStyleOverrides(
        Volume is { } v ? (MixVolumeStyle)v : null, Eq is { } e ? (MixEqStyle)e : null, Filter is { } f ? (MixFilterStyle)f : null,
        Fx is { } x ? (MixFxStyle)x : null, null, Looping is { } l ? (MixLoopingStyle)l : null));
}

/// <summary>The editor's model for one pair: what plays now, what Auto would play, the draft read from it, the recipe a draft saves, the
/// snapping and the writes. UI thread.</summary>
public static class MixEdit
{
    /// <summary>One pair's facts. <see cref="Valid"/> false: the pair cannot be edited (a local file, an episode, the last row).
    /// <see cref="Writes"/>: an edit is saved to Spotify (the user may write the list and its from-item carries the set-transition
    /// template); else it is saved on this PC. <see cref="LocalPending"/>: the pair's local record is an edit waiting for Spotify.</summary>
    public readonly record struct Pair(bool Valid, string PlaylistUri, ulong ItemA, ulong ItemB, Track A, Track B, MixTiming Now, MixTiming Auto,
        bool Local, MixOverrideKind LocalKind, bool Writes = false, bool LocalPending = false);

    /// <summary>The editor's lengths in bars (the client's tabled bar counts).</summary>
    public static readonly int[] BarSteps = [1, 2, 4, 8, 16, 32];
    /// <summary>The shortest overlap the editor sets by beats.</summary>
    public const int MinBeats = 2;
    /// <summary>The editor's length cap: 30 % of the shorter song (`desktop-ui.playlistMixingMaxDurationPercent`), and at most 35 s.</summary>
    public const int MaxDurationPercent = 30;
    /// <summary>How far apart two tempos may be for Wavee to offer beatmatching (B stretched by at most this share).</summary>
    public const float BeatmatchReach = 0.2f;

    public static Pair Read(int playlistSlot, int index)
    {
        var p = new Playlist(playlistSlot);
        var edges = Entities.Current.Edges;
        var targets = edges.PlaylistTracks.Targets(playlistSlot);
        var payload = edges.PlaylistTracks.Payload(playlistSlot);
        if (!p.IsValid || index < 0 || index + 1 >= targets.Length) return default;
        if (payload[index].Kind != PlaylistItemKind.Track || payload[index + 1].Kind != PlaylistItemKind.Track) return default;
        var a = new Track(targets[index]);
        var b = new Track(targets[index + 1]);
        if (!a.IsValid || !b.IsValid || a.IsLocal || b.IsLocal) return default;
        ulong itemA = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index].ItemId));
        ulong itemB = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index + 1].ItemId));
        var fa = Facts(a);
        var fb = Facts(b);
        // The pair's Auto with every analysis the beatmatched Auto reads (and the uris its preset pick seeds from), as the deck plans it.
        var auto = Playback.MixPlanner.Plan(in fa, Playback.BeatmatchFacts.Live(in fa, a.Slot, edges), in fb,
            Playback.BeatmatchFacts.Live(in fb, b.Slot, edges), false, default);
        var now = Playback.Mix.PlanListPair(playlistSlot, index, index + 1);
        bool local = MixOverrides.TryGet(p.Id.Text, itemA, itemB, out var rec);
        bool writes = Spotify.Mixes.CanWrite(p) && MixData.Signals(playlistSlot) is { } signals && !signals.TransitionTemplate(itemA).IsEmpty;
        return new Pair(true, p.Id.Text, itemA, itemB, a, b, now, auto, local, local ? rec.Kind : default, writes, local && rec.Pending);
    }

    static Playback.PairFacts Facts(Track t)
    {
        var cues = Entities.Current.Edges.TrackCues.Payload(t.Slot);
        return new Playback.PairFacts(t.DurationMs, t.Tempo, t.Camelot, cues.Length > 0 ? cues[0] : default, true, false, t.Uri.Text);
    }

    /// <summary>The pair is edited (a Spotify recipe, or a record on this PC that is not "Auto").</summary>
    public static bool Edited(in Pair pair)
        => pair.Local ? pair.LocalKind != MixOverrideKind.Auto : pair.Now.Source == MixSource.Saved || pair.Now.WhyNot == MixWhyNot.NoTransition;

    /// <summary>The pair plays without a transition (a local "No mix", or Spotify's "No transition").</summary>
    public static bool IsNoTransition(in Pair pair)
        => pair.Local && pair.LocalKind == MixOverrideKind.NoMix || pair.Now.WhyNot is MixWhyNot.NoTransition or MixWhyNot.PairOptOut;

    // ── the draft ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The draft of what the pair plays now: the recipe's own values (so an untouched save changes nothing), Spotify's Auto,
    /// or "No transition" (shown on Auto's timing).</summary>
    public static MixDraft DraftOf(in Pair pair, int playlistSlot)
    {
        if (!pair.Valid) return default;
        var auto = DraftOfAuto(in pair);
        if (IsNoTransition(in pair)) return auto with { Mode = MixDraftMode.None };
        if (pair.Now.Source != MixSource.Saved) return auto;
        var r = ReplacedRecipe(in pair, playlistSlot, out _);
        if (r.IsEmpty || r.IsNoTransition) return auto;
        return new MixDraft(MixDraftMode.Preset, r.Preset, r.OutStartMs, r.InStartMs, r.OverlapMs, r.DurationBars, r.IsBeatmatched, r.BeatmatchPreference,
            r.VolumeStyle, r.EqStyle, r.FilterFxStyle, r.FxStyle, r.LoopingStyle, r.Curves);
    }

    /// <summary>Spotify's Auto for the pair as a draft (preset 1, the planner's timing, not beatmatched).</summary>
    public static MixDraft DraftOfAuto(in Pair pair)
    {
        var t = pair.Auto;
        uint overlap = t.Mix ? t.OutDurationMs : (uint)Math.Min(Playback.MixPlanner.FallbackWindowMs, Math.Max(1, pair.A.DurationMs));
        uint outStart = t.Mix ? t.OutStartMs : (uint)Math.Max(0, pair.A.DurationMs - overlap);
        return new MixDraft(MixDraftMode.Auto, (byte)MixPresetId.Fade, outStart, t.Mix ? t.InStartMs : 0, overlap, 0, false,
            MixBeatmatchPreference.Auto, null, null, null, null, null, null);
    }

    /// <summary>A preset picked: the preset's own styles (Customize cleared), the draft's timing kept.</summary>
    public static MixDraft WithPreset(in MixDraft d, int presetId)
    {
        if (presetId <= 0) return d with { Mode = MixDraftMode.None };
        return d with
        {
            Mode = MixDraftMode.Preset, Preset = (byte)presetId, Volume = null, Eq = null, Filter = null, Fx = null, Looping = null,
            Curves = d.Curves is null ? null : MixCurveOverrides.Empty,
        };
    }

    /// <summary>Auto touched: the pair becomes Auto's preset with Auto's timing, ready for the change.</summary>
    static MixDraft Touched(in MixDraft d) => d.Mode == MixDraftMode.Preset ? d : d with { Mode = MixDraftMode.Preset, Preset = d.Preset == 0 ? (byte)1 : d.Preset };

    /// <summary>One ingredient's style set (Customize). The preset's own style clears the override; a new volume, EQ, filter or effect
    /// style drops that ingredient's hand-drawn curves (the style's curves play again).</summary>
    public static MixDraft WithStyle(in MixDraft d, MixIngredient ingredient, int id)
    {
        var t = Touched(in d);
        var own = MixStyles.Preset(t.Preset);
        switch (ingredient)
        {
            case MixIngredient.Volume:
                return t with { Volume = id == (int)own.Volume ? null : id, Curves = Drop(t.Curves, MixCurveLane.Volume) };
            case MixIngredient.Eq:
                return t with { Eq = id == (int)own.Eq ? null : id, Curves = Drop(t.Curves, MixCurveLane.EqLow, MixCurveLane.EqMid, MixCurveLane.EqHigh) };
            case MixIngredient.Filter:
                return t with { Filter = id == (int)own.Filter ? null : id,
                    Curves = Drop(t.Curves, MixCurveLane.FilterCutoff, MixCurveLane.FilterResonance, MixCurveLane.NoiseColor) };
            case MixIngredient.Fx:
                return t with { Fx = id == (int)own.Fx ? null : id,
                    Curves = Drop(t.Curves, MixCurveLane.EchoDryWet, MixCurveLane.ReverbDryWet, MixCurveLane.DelayDryWet) };
            default:
                return t with { Looping = id == (int)own.Looping ? null : id };
        }

        static MixCurveOverrides? Drop(MixCurveOverrides? c, params MixCurveLane[] lanes)
        {
            if (c is null) return null;
            foreach (var lane in lanes) { c = c.With(lane, false, null); c = c.With(lane, true, null); }
            return c;
        }
    }

    /// <summary>The preset's own styles and curves again.</summary>
    public static MixDraft WithoutCustom(in MixDraft d)
        => d with { Volume = null, Eq = null, Filter = null, Fx = null, Looping = null, Curves = d.Curves is null ? null : MixCurveOverrides.Empty };

    /// <summary>The volume curve of one side drawn by hand (reference §3.3): one segment from the side's start level to its end level,
    /// bent by <paramref name="bend"/> (−1..1); 0 puts the style's curve back.</summary>
    public static MixDraft WithVolumeBend(in MixDraft d, bool incoming, float bend)
    {
        var t = Touched(in d);
        bend = Math.Clamp(bend, -1f, 1f);
        var curves = t.Curves ?? MixCurveOverrides.Empty;
        MixCurve? curve = MathF.Abs(bend) < 0.02f ? null
            : new MixCurve([MixCurve.Segment(0f, 1f, incoming ? 0f : 1f, incoming ? 1f : 0f, bend, false)], 0, 1);
        return t with { Curves = curves.With(MixCurveLane.Volume, incoming, curve) };
    }

    /// <summary>The bend of a side's hand-drawn volume curve (0 when the style's curve plays).</summary>
    public static float BendOf(in MixDraft d, bool incoming)
    {
        if (d.Curves?.Get(MixCurveLane.Volume, incoming) is not { Segments: [{ Cubic: true } s] }) return 0f;
        // The first control sits a third of the way along the span, raised by bend × 2/3 (MixCurve.Segment).
        float straight = s.Y0 + (s.Y3 - s.Y0) / 3f;
        return Math.Clamp((s.Y1 - straight) * 1.5f, -1f, 1f);
    }

    public static MixDraft WithOut(in MixDraft d, uint ms) => Touched(in d) with { OutStartMs = ms };
    public static MixDraft WithIn(in MixDraft d, uint ms) => Touched(in d) with { InStartMs = ms };

    /// <summary>A new overlap that keeps the transition's END where it was when <paramref name="keepEnd"/> (the length stepper), else its start (a drag
    /// of the band's end).</summary>
    public static MixDraft WithOverlap(in Pair pair, in MixDraft d, uint ms, int bars, bool keepEnd)
    {
        var t = Touched(in d);
        long end = (long)t.OutStartMs + t.OverlapMs;
        long start = keepEnd ? Math.Max(0, end - ms) : t.OutStartMs;
        start = Math.Min(start, Math.Max(0, pair.A.DurationMs - ms));
        return t with { OverlapMs = ms, OutStartMs = (uint)start, Bars = t.Beatmatched ? bars : 0 };
    }

    /// <summary>Beatmatching on or off (the user's choice: recipe top-level 5). On: B at A's tempo (recipe 1.7 = bpmA / bpmB), the overlap whole
    /// bars of A (2 unless the draft has bars). Off: speed 1, bars 0, the overlap kept.</summary>
    public static MixDraft WithBeatmatch(in Pair pair, in MixDraft d, bool on)
    {
        var t = Touched(in d);
        if (!on) return t with { Beatmatched = false, Bars = 0, BeatmatchPreference = MixBeatmatchPreference.Off };
        int bars = t.Bars > 0 ? t.Bars : NearestBars(in pair, t.OverlapMs);
        uint overlap = BarsMs(in pair, bars);
        long end = (long)t.OutStartMs + t.OverlapMs;
        long start = Math.Max(0, Math.Min(end - overlap, pair.A.DurationMs - overlap));
        return t with { Beatmatched = true, Bars = bars, OverlapMs = overlap, OutStartMs = (uint)start, BeatmatchPreference = MixBeatmatchPreference.On };
    }

    // ── what a draft plays ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The draft's join as the planner places it (the playlist's own rules: a recipe built from the draft): Auto's plan, a refusal for
    /// "No transition".</summary>
    public static MixTiming TimingOf(in Pair pair, int playlistSlot, in MixDraft from, in MixDraft d)
    {
        if (!pair.Valid) return default;
        if (d.Mode == MixDraftMode.Auto) return pair.Auto;
        if (d.Mode == MixDraftMode.None) return MixTiming.Refused(MixWhyNot.NoTransition);
        Build(in pair, playlistSlot, in from, in d, out var recipe, out _);
        var fa = Facts(pair.A);
        var fb = Facts(pair.B);
        var edges = Entities.Current.Edges;
        return Playback.MixPlanner.Plan(in fa, Playback.BeatmatchFacts.Live(in fa, pair.A.Slot, edges), in fb,
            Playback.BeatmatchFacts.Live(in fb, pair.B.Slot, edges), true, in recipe);
    }

    /// <summary>The recipe a save of <paramref name="to"/> sends (opened as <paramref name="from"/>), and the bytes it is patched over (the
    /// replaced recipe's; empty for a new one). PURE over the pair. False for Auto (a reset, no recipe).</summary>
    public static bool Build(in Pair pair, int playlistSlot, in MixDraft from, in MixDraft to, out MixRecipe recipe, out byte[] raw)
    {
        recipe = default;
        raw = [];
        if (!pair.Valid || to.Mode == MixDraftMode.Auto) return false;
        if (to.Mode == MixDraftMode.None) { recipe = NoTransition(in pair, playlistSlot, out raw); return true; }
        recipe = ReplacedRecipe(in pair, playlistSlot, out raw);
        // A draft that did not open on this recipe (Auto, No transition) writes every field; one that did writes what it moved.
        bool all = from.Mode != MixDraftMode.Preset || recipe.IsEmpty || recipe.IsNoTransition;
        if (recipe.IsEmpty && raw.Length == 0) recipe = Fresh(in pair);
        recipe.FromItemId = pair.ItemA;
        recipe.ToItemId = pair.ItemB;
        bool timing = all || to.OutStartMs != from.OutStartMs || to.InStartMs != from.InStartMs || to.OverlapMs != from.OverlapMs;
        if (timing)
        {
            recipe.OutStartMs = to.OutStartMs;
            recipe.InStartMs = to.InStartMs;
            recipe.OverlapMs = Math.Max(1u, to.OverlapMs);
        }
        bool match = all || to.Beatmatched != from.Beatmatched || to.Bars != from.Bars || (to.Beatmatched && to.OverlapMs != from.OverlapMs);
        if (match)
        {
            recipe.DurationBars = to.Beatmatched ? to.Bars : 0;
            recipe.IsBeatmatched = to.Beatmatched;
            recipe.PresetType = to.Beatmatched ? MixPresetType.BeatmatchedFade : MixPresetType.Fade;
            if (to.Beatmatched)
            {
                // Both tempos are the one that makes durationBars × 4 beats fill the overlap (reference §5.2); B runs at bpmA / bpmB.
                float bpm = to.Bars > 0 && to.OverlapMs > 0 ? to.Bars * 4 * 60_000f / to.OverlapMs : pair.A.Tempo / 10f;
                recipe.BpmA = recipe.BpmB = bpm;
                recipe.SpeedA = 1f;
                recipe.SpeedB = pair.B.Tempo > 0 ? pair.A.Tempo / (float)pair.B.Tempo : 1f;
            }
            else if (all || from.Beatmatched)
            {
                recipe.BpmA = pair.A.Tempo / 10f;
                recipe.BpmB = pair.B.Tempo / 10f;
                recipe.SpeedA = recipe.SpeedB = 1f;
            }
        }
        if (all || to.BeatmatchPreference != from.BeatmatchPreference) recipe.BeatmatchPreference = to.BeatmatchPreference;
        if (all || to.Preset != from.Preset) recipe.Preset = to.Preset;
        if (recipe.PresetType == MixPresetType.None) recipe.PresetType = MixPresetType.Fade;
        if (all || to.Volume != from.Volume) recipe.VolumeStyle = to.Volume;
        if (all || to.Eq != from.Eq) recipe.EqStyle = to.Eq;
        if (all || to.Filter != from.Filter) recipe.FilterFxStyle = to.Filter;
        if (all || to.Fx != from.Fx) recipe.FxStyle = to.Fx;
        if (all || to.Looping != from.Looping) recipe.LoopingStyle = to.Looping;
        if (all || !ReferenceEquals(to.Curves, from.Curves))
        {
            recipe.Curves = to.Curves is { IsEmpty: false } ? to.Curves : null;
            // The curve fields the recipe now holds (the loop blocks, 2.17, travel in the raw untouched).
            recipe.CurveOverrides = (recipe.CurveOverrides & (1u << 17)) | (recipe.Curves?.FieldBits() ?? 0);
        }
        // The flags as the official client writes them on a saved recipe: the timing set by hand (when it was), the preset chosen by hand.
        if (timing) recipe.IsOverlapOverride = true;
        recipe.IsPresetIdOverride = true;
        return true;
    }

    /// <summary>"No transition" (reference §5.3): the replaced recipe (or a new one) with no out-point and no in-point, a 1 ms overlap, no tempos,
    /// no preset id and no style override, no timing override flag, the preset flag set.</summary>
    static MixRecipe NoTransition(in Pair pair, int playlistSlot, out byte[] raw)
    {
        var recipe = ReplacedRecipe(in pair, playlistSlot, out raw);
        if (recipe.IsEmpty && raw.Length == 0) recipe = Fresh(in pair);
        recipe.FromItemId = pair.ItemA;
        recipe.ToItemId = pair.ItemB;
        recipe.OutStartMs = recipe.InStartMs = 0;
        recipe.OverlapMs = 1;
        recipe.BpmA = recipe.BpmB = 0f;
        recipe.Preset = (byte)MixPresetId.None;
        recipe.VolumeStyle = recipe.EqStyle = recipe.FilterFxStyle = recipe.FxStyle = recipe.JogwheelStyle = recipe.LoopingStyle = null;
        recipe.Curves = null;
        recipe.CurveOverrides &= 1u << 17;
        recipe.IsOverlapOverride = false;
        recipe.IsPresetIdOverride = true;
        return recipe;
    }

    /// <summary>A new recipe's identity in the shape the official client saves one (reference §5.2).</summary>
    static MixRecipe Fresh(in Pair pair) => new()
    {
        TrackA = pair.A.Id.Text, PlayableA = pair.A.Id.Text, TrackB = pair.B.Id.Text, PlayableB = pair.B.Id.Text,
        BeatsIdA = MixData.BeatsId(pair.A.Slot), BeatsIdB = MixData.BeatsId(pair.B.Slot),
        BpmA = pair.A.Tempo / 10f, BpmB = pair.B.Tempo / 10f,
        SpeedA = 1f, SpeedB = 1f, ItemSpeedA = 1.0, ItemSpeedB = 1.0, PresetType = MixPresetType.Fade,
    };

    /// <summary>The pair's Auto transition as a save's f3 baseline (protocol §3.2): the planner's Auto timing, preset 1, the tempos, no
    /// item ids, both override flags. Null when the planner places no Auto transition for the pair.</summary>
    public static MixRecipe? AutoBaseline(in Pair pair)
    {
        var t = pair.Auto;
        if (!t.Mix) return null;
        var r = Fresh(in pair);
        r.OutStartMs = t.OutStartMs;
        r.InStartMs = t.InStartMs;
        r.OverlapMs = t.OutDurationMs;
        r.Preset = (byte)MixPresetId.Fade;
        r.IsOverlapOverride = r.IsPresetIdOverride = true;
        return r;
    }

    /// <summary>The recipe a save replaces, with its raw bytes: this PC's record for the pair, else the pair's Spotify recipe, else none.</summary>
    public static MixRecipe ReplacedRecipe(in Pair pair, int playlistSlot, out byte[] raw)
    {
        if (MixOverrides.TryGet(pair.PlaylistUri, pair.ItemA, pair.ItemB, out var local) && local.Kind == MixOverrideKind.Recipe)
        {
            raw = local.Raw;
            return local.Recipe;
        }
        if (MixData.TryGet(playlistSlot, pair.ItemA, out var stored) && (stored.Recipe.ToItemId == 0 || stored.Recipe.ToItemId == pair.ItemB))
        {
            raw = stored.Raw;
            return stored.Recipe;
        }
        raw = [];
        return default;
    }

    // ── the writes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Save <paramref name="to"/> (opened as <paramref name="from"/>): Auto is a reset; anything else is a recipe — to Spotify when the
    /// pair <see cref="Pair.Writes"/>, else a record on this PC. <paramref name="settled"/> hears how it ended (a local save: at once).</summary>
    public static void Save(in Pair pair, int playlistSlot, in MixDraft from, in MixDraft to, Action<MixSaveState>? settled = null)
    {
        if (!pair.Valid) return;
        if (to.Mode == MixDraftMode.Auto) { Reset(in pair, playlistSlot, settled); return; }
        if (!pair.Writes)
        {
            if (to.Mode == MixDraftMode.None) MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.NoMix);
            else if (Build(in pair, playlistSlot, in from, in to, out var local, out byte[] localRaw))
                MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.Recipe, in local, localRaw);
            else return;
            Repaint(playlistSlot);
            settled?.Invoke(MixSaveState.None);
            return;
        }
        if (!Build(in pair, playlistSlot, in from, in to, out var recipe, out byte[] raw)) return;
        SaveToSpotify(pair, playlistSlot, recipe, raw, settled);
    }

    /// <summary>The new recipe to Spotify (the patching encoder over <paramref name="raw"/>), with the pair's Auto baseline. Landed: any local
    /// record of the pair goes. Offline or refused: the edit stays as a PENDING local record, which plays here until a Retry lands.</summary>
    static void SaveToSpotify(Pair pair, int playlistSlot, MixRecipe recipe, byte[] raw, Action<MixSaveState>? settled)
    {
        recipe.FromItemId = pair.ItemA;
        recipe.ToItemId = pair.ItemB;
        byte[] encoded = MixRecipe.EncodeToArray(in recipe, raw);
        MixSaves.Set(pair.PlaylistUri, pair.ItemA, MixSaveState.Saving);
        var save = new Spotify.Mixes.TransitionSave(pair.ItemA, pair.ItemB, recipe, encoded, AutoBaseline(in pair));
        Spotify.Mixes.SaveTransition(new Playlist(playlistSlot), save, outcome => Settle(pair, playlistSlot, outcome,
            () => MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.Recipe, in recipe, raw, pending: true), settled));
    }

    static void Settle(Pair pair, int playlistSlot, Spotify.Mixes.Outcome outcome, Action keepLocally, Action<MixSaveState>? settled)
    {
        var state = outcome switch
        {
            Spotify.Mixes.Outcome.Landed => MixSaveState.None,
            Spotify.Mixes.Outcome.Offline => MixSaveState.Offline,
            _ => MixSaveState.Failed,
        };
        if (state == MixSaveState.None) MixOverrides.Clear(pair.PlaylistUri, pair.ItemA, pair.ItemB);
        else keepLocally();
        MixSaves.Set(pair.PlaylistUri, pair.ItemA, state);
        Repaint(playlistSlot);
        settled?.Invoke(state);
    }

    /// <summary>A local record changed: bump the playlist's row, which the list's transition chips, the meta line and the editor read.</summary>
    static void Repaint(int playlistSlot) => Entities.Current.Playlists.Bump(playlistSlot);

    /// <summary>Reset to Auto. A pair that <see cref="Pair.Writes"/>: `set-transition` with no payload when Spotify holds a recipe for it
    /// (kept as a pending "Auto" record when offline or refused), else its pending local edit is dropped. Any other pair: no record left when
    /// Spotify holds no recipe for it; over a Spotify recipe, the "Auto" record on this PC.</summary>
    public static void Reset(in Pair pair, int playlistSlot, Action<MixSaveState>? settled = null)
    {
        if (!pair.Valid) return;
        bool spotify = MixData.TryGet(playlistSlot, pair.ItemA, out var stored) && (stored.Recipe.ToItemId == 0 || stored.Recipe.ToItemId == pair.ItemB);
        if (pair.Writes && spotify)
        {
            var p = pair;
            MixSaves.Set(pair.PlaylistUri, pair.ItemA, MixSaveState.Saving);
            Spotify.Mixes.ResetToAuto(new Playlist(playlistSlot), pair.ItemA, outcome => Settle(p, playlistSlot, outcome,
                () => MixOverrides.Set(p.PlaylistUri, p.ItemA, p.ItemB, MixOverrideKind.Auto, pending: true), settled));
            return;
        }
        if (spotify && !pair.Writes) MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.Auto);
        else MixOverrides.Clear(pair.PlaylistUri, pair.ItemA, pair.ItemB);
        MixSaves.Set(pair.PlaylistUri, pair.ItemA, MixSaveState.None);
        Repaint(playlistSlot);
        settled?.Invoke(MixSaveState.None);
    }

    /// <summary>Retry: send the pair's pending local edit to Spotify again (a recipe, the "none" recipe, or the reset).</summary>
    public static void Retry(in Pair pair, int playlistSlot, Action<MixSaveState>? settled = null)
    {
        if (!pair.Valid || !pair.Writes || !MixOverrides.TryGet(pair.PlaylistUri, pair.ItemA, pair.ItemB, out var record) || !record.Pending) return;
        if (record.Kind == MixOverrideKind.Recipe) SaveToSpotify(pair, playlistSlot, record.Recipe, record.Raw, settled);
        else Reset(in pair, playlistSlot, settled);
    }

    // ── lengths and snapping ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The longest overlap the editor sets: 30 % of the shorter song, at most 35 s.</summary>
    public static uint MaxOverlapMs(in Pair pair)
        => (uint)Math.Max(1000, Math.Min(MixStyles.MaxTransitionMs, (long)Math.Min(pair.A.DurationMs, pair.B.DurationMs) * MaxDurationPercent / 100));

    /// <summary>A bar of A (4 beats at A's tempo; 2 s when the tempo is unknown).</summary>
    public static float BarMs(in Pair pair) => pair.A.Tempo > 0 ? 4 * 600_000f / pair.A.Tempo : 2_000f;

    public static uint BarsMs(in Pair pair, int bars) => (uint)Math.Round(bars * BarMs(in pair));

    /// <summary>The bar count of <see cref="BarSteps"/> nearest an overlap that the editor's cap allows.</summary>
    public static int NearestBars(in Pair pair, long overlapMs)
    {
        int best = BarSteps[0];
        uint max = MaxOverlapMs(in pair);
        foreach (int b in BarSteps)
        {
            if (BarsMs(in pair, b) > max) break;
            if (Math.Abs(BarsMs(in pair, b) - overlapMs) < Math.Abs(BarsMs(in pair, best) - overlapMs)) best = b;
        }
        return best;
    }

    /// <summary>Can the pair be beatmatched? Both tempos and both beat grids known, the tempos at most <see cref="BeatmatchReach"/> apart.</summary>
    public static bool CanBeatmatch(in Pair pair)
    {
        if (pair.A.Tempo == 0 || pair.B.Tempo == 0) return false;
        var edges = Entities.Current.Edges.TrackBeats;
        if (edges.Payload(pair.A.Slot).Length < 16 || edges.Payload(pair.B.Slot).Length < 16) return false;
        return MathF.Abs(pair.A.Tempo / (float)pair.B.Tempo - 1f) <= BeatmatchReach;
    }

    /// <summary>The beat of <paramref name="beats"/> nearest <paramref name="ms"/>; with <paramref name="phaseOf"/> ≥ 0, only beats in the
    /// same place of their bar as the beat nearest <paramref name="phaseOf"/> (the official editor keeps the bar phase). −1 without a grid.</summary>
    public static long SnapToBeat(ReadOnlySpan<uint> beats, long ms, long phaseOf = -1)
    {
        if (beats.Length < 2) return -1;
        int target = Nearest(beats, ms);
        if (phaseOf < 0) return BeatGrid.StartMs(beats[target]);
        int phase = PhaseOf(beats, Nearest(beats, phaseOf));
        int best = -1;
        for (int i = Math.Max(0, target - 4); i <= Math.Min(beats.Length - 1, target + 4); i++)
            if (PhaseOf(beats, i) == phase && (best < 0 || Math.Abs(BeatGrid.StartMs(beats[i]) - ms) < Math.Abs(BeatGrid.StartMs(beats[best]) - ms))) best = i;
        return BeatGrid.StartMs(beats[best < 0 ? target : best]);
    }

    static int Nearest(ReadOnlySpan<uint> beats, long ms)
    {
        int lo = 0, hi = beats.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (BeatGrid.StartMs(beats[mid]) < ms) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(BeatGrid.StartMs(beats[lo - 1]) - ms) <= Math.Abs(BeatGrid.StartMs(beats[lo]) - ms)) lo--;
        return lo;
    }

    /// <summary>A beat's place in its bar (beats since the last downbeat, 0..3); its index mod 4 on a grid without downbeats.</summary>
    static int PhaseOf(ReadOnlySpan<uint> beats, int i)
    {
        for (int k = 0; k < 8 && i - k >= 0; k++) if (BeatGrid.IsDownbeat(beats[i - k])) return k % 4;
        return i % 4;
    }

    /// <summary>The overlap in whole beats of A's grid nearest <paramref name="ms"/> from <paramref name="outStart"/> (at least
    /// <see cref="MinBeats"/>); the ms itself (to 10 ms) without a grid.</summary>
    public static long SnapOverlapBeats(ReadOnlySpan<uint> beatsA, long outStart, long ms)
    {
        if (beatsA.Length < 2) return Math.Max(500, (ms + 5) / 10 * 10);
        int first = Nearest(beatsA, outStart);
        long end = SnapToBeat(beatsA, outStart + ms);
        int last = Nearest(beatsA, end);
        if (last - first < MinBeats) last = Math.Min(beatsA.Length - 1, first + MinBeats);
        return Math.Max(1, BeatGrid.StartMs(beatsA[last]) - outStart);
    }

    /// <summary>The overlap in beats of A's grid (as many as fit, to one decimal: "8", "5.7").</summary>
    public static string BeatsText(ReadOnlySpan<uint> beatsA, long outStart, long overlapMs)
    {
        if (beatsA.Length < 2) return "";
        float period = Playback.MixPlanner.PeriodNear(beatsA, outStart + overlapMs / 2);
        return (overlapMs / period).ToString("0.#", CultureInfo.CurrentCulture);
    }

    // ── words ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"3:44.2": a point in a song.</summary>
    public static string Clock(long ms)
    {
        ms = Math.Max(0, ms);
        long tenths = (ms + 50) / 100;
        return (tenths / 600).ToString(CultureInfo.CurrentCulture) + ":" + (tenths / 10 % 60).ToString("00", CultureInfo.CurrentCulture)
             + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + (tenths % 10).ToString(CultureInfo.CurrentCulture);
    }

    public static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>"104 → 156 BPM · 9A → 2B — tempos far apart; a quick cut sounds best." Recommends, never warns; only known numbers.</summary>
    public static string Compatibility(in Pair pair)
    {
        var parts = new List<string>(2);
        if (pair.A.Tempo > 0 && pair.B.Tempo > 0)
            parts.Add(Strings.Mix.CompatTempo((pair.A.Tempo / 10.0).ToString("0", CultureInfo.CurrentCulture), (pair.B.Tempo / 10.0).ToString("0", CultureInfo.CurrentCulture)));
        string? ka = Track.Format.CamelotLabel(pair.A.Camelot), kb = Track.Format.CamelotLabel(pair.B.Camelot);
        if (ka is not null && kb is not null) parts.Add(ka + " → " + kb);
        string numbers = string.Join(" · ", parts);
        var tempo = Playback.Mix.Tempo(pair.A.Tempo, pair.B.Tempo);
        var key = Playback.Mix.Key(pair.A.Camelot, pair.B.Camelot);
        if (tempo == Playback.Mix.TempoRelation.Unknown || key == Playback.Mix.KeyRelation.Unknown) return numbers;
        string ending = tempo == Playback.Mix.TempoRelation.Apart ? Loc.Get(Strings.Mix.CompatApart)
                      : key == Playback.Mix.KeyRelation.Clash ? Loc.Get(Strings.Mix.CompatClash)
                      : Loc.Get(Strings.Mix.CompatGood);
        return numbers.Length == 0 ? ending : numbers + " — " + ending;
    }

    /// <summary>A preset by name: Spotify's nine by their UI names, the thirteen internal ones by what they do.</summary>
    public static string PresetName(int id) => (MixPresetId)id switch
    {
        MixPresetId.BassSwapFilterOut => Loc.Get(Strings.Mix.Internal.BassSwapFilterOut),
        MixPresetId.BassSwapEcho => Loc.Get(Strings.Mix.Internal.BassSwapEcho),
        MixPresetId.BassAndMidSwap => Loc.Get(Strings.Mix.Internal.BassAndMidSwap),
        MixPresetId.FilterFadeOut => Loc.Get(Strings.Mix.Internal.FilterFadeOut),
        MixPresetId.FilterFadeIn => Loc.Get(Strings.Mix.Internal.FilterFadeIn),
        MixPresetId.FilterFadeInOut => Loc.Get(Strings.Mix.Internal.FilterFadeInOut),
        MixPresetId.ReverbCut => Loc.Get(Strings.Mix.Internal.ReverbCut),
        MixPresetId.RiserCut => Loc.Get(Strings.Mix.Internal.RiserCut),
        MixPresetId.ReverbRiserCut => Loc.Get(Strings.Mix.Internal.ReverbRiserCut),
        MixPresetId.EchoCut => Loc.Get(Strings.Mix.Internal.EchoCut),
        MixPresetId.BassSwapAtEnd => Loc.Get(Strings.Mix.Internal.BassSwapAtEnd),
        MixPresetId.BassSwapLowPassFilterOut => Loc.Get(Strings.Mix.Internal.BassSwapLowPassFilterOut),
        MixPresetId.LowPassFilterFadeInOut => Loc.Get(Strings.Mix.Internal.LowPassFilterFadeInOut),
        _ => MixSeams.PresetName(id),
    };

    /// <summary>The thirteen presets the editor offers under "More" (Spotify's internal ones), in id order.</summary>
    public static readonly MixPresetId[] InternalOrder =
    [
        MixPresetId.BassSwapFilterOut, MixPresetId.BassSwapEcho, MixPresetId.BassAndMidSwap, MixPresetId.FilterFadeOut, MixPresetId.FilterFadeIn,
        MixPresetId.FilterFadeInOut, MixPresetId.ReverbCut, MixPresetId.RiserCut, MixPresetId.ReverbRiserCut, MixPresetId.EchoCut,
        MixPresetId.BassSwapAtEnd, MixPresetId.BassSwapLowPassFilterOut, MixPresetId.LowPassFilterFadeInOut,
    ];

    /// <summary>The one sentence under the presets for what the draft plays.</summary>
    public static string Sentence(in MixDraft d)
    {
        if (d.Mode == MixDraftMode.None) return Loc.Get(Strings.Mix.Sentence.None);
        return (MixPresetId)d.Preset switch
        {
            MixPresetId.Fade => Loc.Get(Strings.Mix.Sentence.Fade),
            MixPresetId.Rise => Loc.Get(Strings.Mix.Sentence.Rise),
            MixPresetId.Blend => Loc.Get(Strings.Mix.Sentence.Blend),
            MixPresetId.Wave => Loc.Get(Strings.Mix.Sentence.Wave),
            MixPresetId.Melt => Loc.Get(Strings.Mix.Sentence.Melt),
            MixPresetId.Slam => Loc.Get(Strings.Mix.Sentence.Slam),
            MixPresetId.Dissolve => Loc.Get(Strings.Mix.Sentence.Dissolve),
            MixPresetId.Surge => Loc.Get(Strings.Mix.Sentence.Surge),
            MixPresetId.Afterglow => Loc.Get(Strings.Mix.Sentence.Afterglow),
            _ => Strings.Mix.Sentence.Internal(PresetName(d.Preset)),
        };
    }

    /// <summary>The styles Customize offers per ingredient, in the client's order (reference §1.3); Effects and Looping always (owner,
    /// 2026-11-25).</summary>
    public static ReadOnlySpan<int> Offered(MixIngredient ingredient) => ingredient switch
    {
        MixIngredient.Volume => [6, 7, 5, 10, 9, 1, 2, 11],
        MixIngredient.Eq => [4, 10, 11, 1, 12, 13, 14, 15, 0],
        MixIngredient.Filter => [10, 9, 11, 13, 6, 5, 7, 12, 14, 15, 0],
        MixIngredient.Fx => [1, 2, 3, 4, 5, 6, 7, 10, 11, 8, 9, 0],
        _ => [8, 1, 2, 3, 4, 5, 0],
    };

    /// <summary>A style's label in Customize (the client's own labels where it has them; the filter styles by what they do).</summary>
    public static string StyleName(MixIngredient ingredient, int id) => ingredient switch
    {
        MixIngredient.Volume => (MixVolumeStyle)id switch
        {
            MixVolumeStyle.CrossShape => Loc.Get(Strings.Mix.Volume.CrossShape),
            MixVolumeStyle.FullOverlap => Loc.Get(Strings.Mix.Volume.FullOverlap),
            MixVolumeStyle.SlowInSlowOut => Loc.Get(Strings.Mix.Volume.SlowInSlowOut),
            MixVolumeStyle.FastInAtStartSlowOut => Loc.Get(Strings.Mix.Volume.FastInAtStartSlowOut),
            MixVolumeStyle.SlowInFastOutAtEnd => Loc.Get(Strings.Mix.Volume.SlowInFastOutAtEnd),
            MixVolumeStyle.Cut => Loc.Get(Strings.Mix.Volume.Cut),
            MixVolumeStyle.Crossfade => Loc.Get(Strings.Mix.Volume.Crossfade),
            MixVolumeStyle.SlowInSemiFastOutAtEnd => Loc.Get(Strings.Mix.Volume.SlowInSemiFastOutAtEnd),
            MixVolumeStyle.None => Loc.Get(Strings.Mix.Style.None),
            _ => Strings.Mix.Style.Id(id.ToString(CultureInfo.CurrentCulture)),
        },
        MixIngredient.Eq => (MixEqStyle)id switch
        {
            MixEqStyle.BassSwap => Loc.Get(Strings.Mix.Eq.BassSwap),
            MixEqStyle.BassSwapAtEnd => Loc.Get(Strings.Mix.Eq.BassSwapAtEnd),
            MixEqStyle.BassSwapAtStart => Loc.Get(Strings.Mix.Eq.BassSwapAtStart),
            MixEqStyle.EqSwap => Loc.Get(Strings.Mix.Eq.EqSwap),
            MixEqStyle.BassCuts => Loc.Get(Strings.Mix.Eq.BassCuts),
            MixEqStyle.NoBass => Loc.Get(Strings.Mix.Eq.NoBass),
            MixEqStyle.StartFadeOut => Loc.Get(Strings.Mix.Eq.StartFadeOut),
            MixEqStyle.BassFadeOut => Loc.Get(Strings.Mix.Eq.BassFadeOut),
            MixEqStyle.None => Loc.Get(Strings.Mix.Style.None),
            _ => Strings.Mix.Style.Id(id.ToString(CultureInfo.CurrentCulture)),
        },
        MixIngredient.Filter => (MixFilterStyle)id switch
        {
            MixFilterStyle.LowPassFilterOut => Loc.Get(Strings.Mix.Filter.LowPassOut),
            MixFilterStyle.LowPassFilterIn => Loc.Get(Strings.Mix.Filter.LowPassIn),
            MixFilterStyle.LowPassFilterInLowPassFilterOut => Loc.Get(Strings.Mix.Filter.LowPassInOut),
            MixFilterStyle.LowPassFilterInFilterOut => Loc.Get(Strings.Mix.Filter.LowPassInHighPassOut),
            MixFilterStyle.FilterOut => Loc.Get(Strings.Mix.Filter.HighPassOut),
            MixFilterStyle.FilterIn => Loc.Get(Strings.Mix.Filter.HighPassIn),
            MixFilterStyle.FilterInFilterOut => Loc.Get(Strings.Mix.Filter.HighPassInOut),
            MixFilterStyle.FilterInLowPassFilterOut => Loc.Get(Strings.Mix.Filter.HighPassInLowPassOut),
            MixFilterStyle.HighPassFilterHalfOut => Loc.Get(Strings.Mix.Filter.HighPassHalfOut),
            MixFilterStyle.NoiseOutEnd => Loc.Get(Strings.Mix.Filter.NoiseOutEnd),
            MixFilterStyle.None => Loc.Get(Strings.Mix.Style.None),
            _ => Strings.Mix.Style.Id(id.ToString(CultureInfo.CurrentCulture)),
        },
        MixIngredient.Fx => (MixFxStyle)id switch
        {
            MixFxStyle.ReverbOutCentre => Loc.Get(Strings.Mix.Fx.ReverbOutCentre),
            MixFxStyle.ReverbCutEnd => Loc.Get(Strings.Mix.Fx.ReverbCutEnd),
            MixFxStyle.ReverbOutEnd => Loc.Get(Strings.Mix.Fx.ReverbOutEnd),
            MixFxStyle.EchoHalfCutEnd => Loc.Get(Strings.Mix.Fx.EchoHalfCutEnd),
            MixFxStyle.EchoHalfOutEnd => Loc.Get(Strings.Mix.Fx.EchoHalfOutEnd),
            MixFxStyle.Echo3QCutEnd => Loc.Get(Strings.Mix.Fx.Echo3QCutEnd),
            MixFxStyle.Echo3QOutEnd => Loc.Get(Strings.Mix.Fx.Echo3QOutEnd),
            MixFxStyle.Echo1CutEnd => Loc.Get(Strings.Mix.Fx.Echo1CutEnd),
            MixFxStyle.Echo1OutEnd => Loc.Get(Strings.Mix.Fx.Echo1OutEnd),
            MixFxStyle.DelayHalfCutEnd => Loc.Get(Strings.Mix.Fx.DelayHalfCutEnd),
            MixFxStyle.Delay3QCutEnd => Loc.Get(Strings.Mix.Fx.Delay3QCutEnd),
            MixFxStyle.None => Loc.Get(Strings.Mix.Style.None),
            _ => Strings.Mix.Style.Id(id.ToString(CultureInfo.CurrentCulture)),
        },
        _ => (MixLoopingStyle)id switch
        {
            MixLoopingStyle.OneBeat => Loc.Get(Strings.Mix.Loop.OneBeat),
            MixLoopingStyle.TwoBeat => Loc.Get(Strings.Mix.Loop.TwoBeat),
            MixLoopingStyle.FourBeat => Loc.Get(Strings.Mix.Loop.FourBeat),
            MixLoopingStyle.EightBeat => Loc.Get(Strings.Mix.Loop.EightBeat),
            MixLoopingStyle.SixteenBeat => Loc.Get(Strings.Mix.Loop.SixteenBeat),
            MixLoopingStyle.HalvingEnd => Loc.Get(Strings.Mix.Loop.HalvingEnd),
            MixLoopingStyle.None => Loc.Get(Strings.Mix.Style.None),
            _ => Strings.Mix.Style.Id(id.ToString(CultureInfo.CurrentCulture)),
        },
    };

    public static string IngredientName(MixIngredient ingredient) => Loc.Get(ingredient switch
    {
        MixIngredient.Volume => Strings.Mix.Editor.Volume,
        MixIngredient.Eq => Strings.Mix.Editor.Eq,
        MixIngredient.Filter => Strings.Mix.Editor.Filter,
        MixIngredient.Fx => Strings.Mix.Editor.Effects,
        _ => Strings.Mix.Editor.Looping,
    });

    /// <summary>The style an ingredient plays in <paramref name="set"/>.</summary>
    public static int StyleOf(in MixStyleSet set, MixIngredient ingredient) => ingredient switch
    {
        MixIngredient.Volume => (int)set.Volume,
        MixIngredient.Eq => (int)set.Eq,
        MixIngredient.Filter => (int)set.Filter,
        MixIngredient.Fx => (int)set.Fx,
        _ => (int)set.Looping,
    };

    /// <summary>The override a draft holds for an ingredient (null: the preset's own style).</summary>
    public static int? OverrideOf(in MixDraft d, MixIngredient ingredient) => ingredient switch
    {
        MixIngredient.Volume => d.Volume,
        MixIngredient.Eq => d.Eq,
        MixIngredient.Filter => d.Filter,
        MixIngredient.Fx => d.Fx,
        _ => d.Looping,
    };
}

/// <summary>The five Customize ingredients (reference §1.3). The jog wheel has no known style list (open), so it is not offered.</summary>
public enum MixIngredient : byte { Volume, Eq, Filter, Fx, Looping }

/// <summary>Where a pair's Spotify save stands on this PC (the editor's save line).</summary>
public enum MixSaveState : byte { None, Saving, Failed, Offline }

/// <summary>The saves in flight and how the last one ended, per (playlist, from item). In memory: after a restart a pending local record reads
/// as <see cref="MixSaveState.Failed"/> ("Not saved to Spotify yet"). UI thread.</summary>
public static class MixSaves
{
    static readonly Dictionary<(string Playlist, ulong From), MixSaveState> s_state = new();

    /// <summary>Bumped on every change: the editor reads it.</summary>
    public static readonly FluentGpu.Signals.Signal<int> Changed = new(0);

    public static MixSaveState StateOf(string playlistUri, ulong fromItem) => s_state.GetValueOrDefault((playlistUri, fromItem));

    internal static void Set(string playlistUri, ulong fromItem, MixSaveState state)
    {
        if (StateOf(playlistUri, fromItem) == state) return;
        if (state == MixSaveState.None) s_state.Remove((playlistUri, fromItem));
        else s_state[(playlistUri, fromItem)] = state;
        Changed.Value++;
    }
}

/// <summary>THE SEAM for "Hear it" (full-support plan §2.3.4 local, §2.4 over Connect; M5, the Mix Connect lane). The editor's button calls
/// <see cref="Toggle"/>; the preview engine plugs in once with <see cref="Install"/>, and its signature is the engine's own
/// (`Playback.MixPreview.Start(playlistSlot, fromIndex, toIndex, recipe, raw, loop)`, `Stop(resume)`, `State`), so wiring it is one line:
/// <code>MixHearIt.Install((slot, from, to, recipe, raw) => Playback.MixPreview.Start(slot, from, to, recipe, raw, loop: true),
///     () => Playback.MixPreview.Stop(), () => Playback.MixPreview.State.Value != Playback.MixPreview.Phase.Idle);</code>
/// <c>playing</c> is read inside the editor's render, so reading the engine's state signal there re-renders the button when a preview starts
/// or ends. Until an engine is installed the button shows unavailable. UI thread.</summary>
public static class MixHearIt
{
    /// <summary>Start a preview of rows <c>fromIndex</c> → <c>toIndex</c> of a playlist: <c>recipe</c> is the unsaved edit (null: what the pair
    /// plays now, Auto included), <c>raw</c> the stored bytes it patches. False when the pair cannot be previewed.</summary>
    public delegate bool StartPreview(int playlistSlot, int fromIndex, int toIndex, MixRecipe? recipe, byte[]? raw);

    static StartPreview? s_start;
    static Action? s_stop;
    static Func<bool>? s_playing;

    /// <summary>Bumped on <see cref="Install"/> and on each toggle (the engine's own state signal covers the rest).</summary>
    public static readonly FluentGpu.Signals.Signal<int> Changed = new(0);

    public static void Install(StartPreview start, Action stop, Func<bool> playing)
    {
        (s_start, s_stop, s_playing) = (start, stop, playing);
        Changed.Value = Changed.Peek() + 1;
    }

    public static bool Available => s_start is not null;
    /// <summary>Is a preview playing? A tracked read when the engine's is (call it from a render).</summary>
    public static bool Playing => s_playing?.Invoke() ?? false;

    /// <summary>The button: stop a playing preview (what played before comes back), else start this pair's.</summary>
    public static void Toggle(int playlistSlot, int fromIndex, MixRecipe? recipe, byte[]? raw)
    {
        if (Playing) s_stop?.Invoke();
        else s_start?.Invoke(playlistSlot, fromIndex, fromIndex + 1, recipe, raw);
        Changed.Value = Changed.Peek() + 1;
    }

    /// <summary>The editor closed (Cancel, a save that landed): a preview it started stops.</summary>
    public static void StopIfPlaying()
    {
        if (!Playing) return;
        s_stop?.Invoke();
        Changed.Value = Changed.Peek() + 1;
    }
}
