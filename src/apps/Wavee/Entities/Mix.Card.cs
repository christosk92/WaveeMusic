// ── Entities/Mix.Card.cs — #48 Mix phase 7, the seam card (plan §3.11, cut: saved on this PC) ─────────────────────────
//
// One pair, edited in place: opened from a Transitions row or a seam's click, it stands under the Transitions list (the list bar's
// eased slot, so the rows below slide and nothing jumps). Top to bottom:
//
//   ▣ Genesis ⟶ Oblivion                                        ‹ 2 of 10 ›
//   104 → 156 BPM · 9A → 2B — tempos far apart; a quick cut sounds best.
//   ┌ Smooth ┬ Lift ┬ Quick ┬ No mix ┐   Auto chose Smooth
//   Volume crossfades over 6.2 s; the bass hands over at the middle.
//   Overlap   −   6.2 s · 8 beats   +
//   Saved on this PC only                             Reset to auto   Done
//
// THE CUT. Plan §3.11 saves an owner's edit to Spotify (`set-transition`); that write waits for capture round 2 item 5 (the
// plan's own precondition) and its live check needs the owner, so every save here is the local override (MixOverrides, D11 (b)'s
// "Otherwise" column) and the card says "Saved on this PC only". No waveform strip yet: the overlap is a beat stepper (2 to 32 beats
// of the outgoing track, at most 16 s; three fixed lengths for a pair without a grid). No "Hear it" (the mid-session restore is
// unverified, plan §3.6). Auto is a state, not a fifth choice: picking any style, the lit one too, makes the pair edited.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The four styles the card offers (plan §3.11): Smooth = Spotify Fade, Lift = Spotify Rise, Quick = a Fade of the whole
/// beats nearest 2.5 s (Wavee's name, D17), No mix = a local opt-out (D16).</summary>
public enum MixStyle : byte { Smooth, Lift, Quick, NoMix }

/// <summary>The card's model for one pair: what plays now, what Auto would play, and the writes. UI thread.</summary>
public static class MixEdit
{
    /// <summary>A Fade saved at most this long reads back as Quick.</summary>
    public const uint QuickMaxMs = 2_600;
    public const int MinBeats = 2, MaxBeats = 32;
    static readonly uint[] s_fixedLengthsMs = [2_500, 6_000, 12_000];

    /// <summary>One pair's facts for the card. <see cref="Valid"/> false: the pair cannot be edited (a local file, an episode, the last row).</summary>
    public readonly record struct Pair(bool Valid, string PlaylistUri, ulong ItemA, ulong ItemB, Track A, Track B, MixTiming Now, MixTiming Auto,
        bool Local, MixOverrideKind LocalKind, float PeriodMs, bool Grid);

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
        var beatsA = edges.TrackBeats.Payload(a.Slot);
        var beatsB = edges.TrackBeats.Payload(b.Slot);
        var cuesA = edges.TrackCues.Payload(a.Slot);
        var cuesB = edges.TrackCues.Payload(b.Slot);
        var fa = new Playback.PairFacts(a.DurationMs, a.Tempo, a.Camelot, cuesA.Length > 0 ? cuesA[0] : default, true, false);
        var fb = new Playback.PairFacts(b.DurationMs, b.Tempo, b.Camelot, cuesB.Length > 0 ? cuesB[0] : default, true, false);
        var auto = Playback.MixPlanner.Plan(in fa, beatsA, in fb, beatsB, false, default);
        var now = Playback.Mix.PlanListPair(playlistSlot, index, index + 1);
        bool local = MixOverrides.TryGet(p.Id.Text, itemA, itemB, out var rec);
        bool grid = beatsA.Length >= 16;
        var anchor = now.Mix ? now : auto;
        float period = grid ? Playback.MixPlanner.PeriodNear(beatsA, anchor.OutStartMs + anchor.OutDurationMs / 2) : 0f;
        return new Pair(true, p.Id.Text, itemA, itemB, a, b, now, auto, local, local ? rec.Kind : default, period, grid);
    }

    /// <summary>The style the pair plays now; −1 for a Spotify preset Wavee does not know (or an Auto refusal).</summary>
    public static int StyleOf(in Pair pair)
    {
        if (pair.Local && pair.LocalKind == MixOverrideKind.NoMix) return (int)MixStyle.NoMix;
        if (pair.Now.WhyNot == MixWhyNot.NoTransition) return (int)MixStyle.NoMix;   // saved in Spotify as "no transition"
        return StyleOfTiming(pair.Now.Mix ? pair.Now : pair.Auto);
    }

    static int StyleOfTiming(in MixTiming t)
    {
        if (!t.Mix) return -1;
        return t.Preset switch
        {
            (byte)MixPresetId.Rise => (int)MixStyle.Lift,
            (byte)MixPresetId.Fade => t.OutDurationMs <= QuickMaxMs ? (int)MixStyle.Quick : (int)MixStyle.Smooth,
            _ => -1,
        };
    }

    /// <summary>The pair is edited (a Spotify recipe, or a record on this PC that is not "Auto").</summary>
    public static bool Edited(in Pair pair)
        => pair.Local ? pair.LocalKind != MixOverrideKind.Auto : pair.Now.Source == MixSource.Saved || pair.Now.WhyNot == MixWhyNot.NoTransition;

    /// <summary>The overlap in whole beats of the outgoing track (its grid), else the nearest fixed length's index.</summary>
    public static int BeatsOf(in Pair pair, uint overlapMs)
        => pair.Grid ? Math.Clamp((int)MathF.Round(overlapMs / pair.PeriodMs), MinBeats, MaxBeatsFor(in pair)) : NearestFixed(overlapMs);

    public static int MaxBeatsFor(in Pair pair) => pair.Grid ? Math.Clamp((int)(Playback.MixPlanner.MaxOverlapMs / pair.PeriodMs), MinBeats, MaxBeats) : s_fixedLengthsMs.Length - 1;

    public static int MinBeatsFor(in Pair pair) => pair.Grid ? MinBeats : 0;

    public static uint OverlapMsOf(in Pair pair, int beats)
        => pair.Grid ? (uint)Math.Min(Playback.MixPlanner.MaxOverlapMs, (uint)(beats * pair.PeriodMs + 0.5f)) : s_fixedLengthsMs[Math.Clamp(beats, 0, s_fixedLengthsMs.Length - 1)];

    static int NearestFixed(uint ms)
    {
        int best = 0;
        for (int i = 1; i < s_fixedLengthsMs.Length; i++)
            if (Math.Abs((long)s_fixedLengthsMs[i] - ms) < Math.Abs((long)s_fixedLengthsMs[best] - ms)) best = i;
        return best;
    }

    /// <summary>The beat count Quick uses: the whole beats nearest 2.5 s (at least 2).</summary>
    public static int QuickBeats(in Pair pair) => pair.Grid ? Math.Max(MinBeats, (int)MathF.Round(Playback.MixPlanner.ShortTargetMs / pair.PeriodMs)) : 0;

    /// <summary>"6.2 s · 8 beats" (or "2.5 s" for a pair without a grid).</summary>
    public static string Readout(in Pair pair, int beats)
    {
        string seconds = (OverlapMsOf(in pair, beats) / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);
        return pair.Grid ? Strings.Mix.Readout(seconds, beats.ToString(CultureInfo.CurrentCulture)) : Strings.Mix.ReadoutPlain(seconds);
    }

    /// <summary>The readout for an overlap as it plays now (not snapped yet): "2.9 s · 5.7 beats" — the beats as many as fit, to one decimal.</summary>
    public static string ReadoutMs(in Pair pair, uint overlapMs)
    {
        string seconds = (overlapMs / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);
        return pair.Grid ? Strings.Mix.Readout(seconds, (overlapMs / pair.PeriodMs).ToString("0.#", CultureInfo.CurrentCulture)) : Strings.Mix.ReadoutPlain(seconds);
    }

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

    public static string StyleName(int style) => style switch
    {
        (int)MixStyle.Smooth => Loc.Get(Strings.Mix.Smooth),
        (int)MixStyle.Lift => Loc.Get(Strings.Mix.Lift),
        (int)MixStyle.Quick => Loc.Get(Strings.Mix.Quick),
        (int)MixStyle.NoMix => Loc.Get(Strings.Mix.NoMix),
        _ => Loc.Get(Strings.Mix.EditedInSpotify),
    };

    /// <summary>The one sentence under the control for <paramref name="style"/>.</summary>
    public static string Sentence(in Pair pair, int style, int beats) => style switch
    {
        (int)MixStyle.Smooth => Strings.Mix.SentenceSmooth((OverlapMsOf(in pair, beats) / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)),
        (int)MixStyle.Lift => Loc.Get(Strings.Mix.SentenceLift),
        (int)MixStyle.Quick => Loc.Get(Strings.Mix.SentenceQuick),
        (int)MixStyle.NoMix => Loc.Get(Strings.Mix.SentenceNoMix),
        _ => Loc.Get(Strings.Mix.SentenceUnknown),
    };

    /// <summary>Done: the pair plays <paramref name="style"/> over <paramref name="beats"/> from now on, as a record on this PC. The mix-out
    /// END stays where the planner or the recipe put it (plan §3.11: no second handle); the in-point stays too.</summary>
    public static void Save(in Pair pair, int playlistSlot, int style, int beats)
    {
        if (!pair.Valid) return;
        if (style == (int)MixStyle.NoMix) { MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.NoMix); Repaint(playlistSlot); return; }
        if (style < 0) return;
        var basis = pair.Now.Mix ? pair.Now : pair.Auto;
        if (!basis.Mix && !pair.Auto.Mix) return;
        if (!basis.Mix) basis = pair.Auto;
        if (style == (int)MixStyle.Quick) beats = pair.Grid ? QuickBeats(in pair) : 0;
        uint overlap = OverlapMsOf(in pair, beats);
        long end = (long)basis.OutStartMs + basis.OutDurationMs;
        // The edit starts from the recipe it replaces (this PC's, else Spotify's) and keeps that recipe's bytes, so every field the
        // card does not edit (beatmatch, speeds, playable uris, style and curve overrides, unknown fields) is written back as it was.
        var recipe = ReplacedRecipe(in pair, playlistSlot, out byte[] raw);
        if (raw.Length == 0)
        {
            // A new recipe, in the shape the official client saves one.
            recipe.TrackA = recipe.PlayableA = pair.A.Id.Text;
            recipe.TrackB = recipe.PlayableB = pair.B.Id.Text;
            recipe.BpmA = pair.A.Tempo / 10f;
            recipe.BpmB = pair.B.Tempo / 10f;
            recipe.SpeedA = recipe.SpeedB = 1f;
            recipe.ItemSpeedA = recipe.ItemSpeedB = 1.0;
            recipe.PresetType = MixPresetType.Fade;
        }
        recipe.FromItemId = pair.ItemA;
        recipe.ToItemId = pair.ItemB;
        recipe.OutStartMs = (uint)Math.Max(0, end - overlap);
        recipe.InStartMs = basis.InStartMs;
        recipe.OverlapMs = overlap;
        recipe.Preset = style == (int)MixStyle.Lift ? (byte)MixPresetId.Rise : (byte)MixPresetId.Fade;
        recipe.IsOverlapOverride = recipe.IsPresetIdOverride = true;   // both picked by hand
        MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.Recipe, in recipe, raw);
        Repaint(playlistSlot);
    }

    /// <summary>The recipe a save replaces, with its raw bytes: this PC's record for the pair, else the pair's Spotify recipe, else
    /// a new one (no bytes).</summary>
    static MixRecipe ReplacedRecipe(in Pair pair, int playlistSlot, out byte[] raw)
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

    /// <summary>A local record changed: bump the playlist's row, which the seams, the Transitions rows, the meta line and this card read (the
    /// row menu's "Don't mix into the next song" does the same).</summary>
    static void Repaint(int playlistSlot) => Entities.Current.Playlists.Bump(playlistSlot);

    /// <summary>Reset to auto: no record left when Spotify holds no recipe for the pair; over a Spotify recipe, the "Auto" record (the reset
    /// request is not captured, O6).</summary>
    public static void Reset(in Pair pair, int playlistSlot)
    {
        if (!pair.Valid) return;
        bool spotify = MixData.TryGet(playlistSlot, pair.ItemA, out var stored) && (stored.Recipe.ToItemId == 0 || stored.Recipe.ToItemId == pair.ItemB);
        if (spotify) MixOverrides.Set(pair.PlaylistUri, pair.ItemA, pair.ItemB, MixOverrideKind.Auto);
        else MixOverrides.Clear(pair.PlaylistUri, pair.ItemA, pair.ItemB);
        Repaint(playlistSlot);
    }
}

/// <summary>The seam card for pair <c>index</c> (row index → index + 1). Keyed by the pair, so moving to another pair starts a fresh draft.</summary>
internal sealed class MixSeamCard(int playlistSlot, int index, int pairs, Action<int> move, Action close) : Component
{
    readonly int _playlist = playlistSlot, _index = index, _pairs = pairs;
    readonly Action<int> _move = move;
    readonly Action _close = close;
    IReadOnlyList<SegmentedItem>? _items;

    public override Element Render()
    {
        var pair = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            _ = Entities.Current.Edges.TrackBeats.Changed.Value;
            _ = MixPrefs.Changed.Value;
            return MixEdit.Read(_playlist, _index);
        }).Value;
        var style = UseSignal(MixEdit.StyleOf(in pair));
        var beats = UseSignal(MixEdit.BeatsOf(in pair, (pair.Now.Mix ? pair.Now : pair.Auto).OutDurationMs));
        var touched = UseSignal(false);
        if (!pair.Valid)
            return new BoxEl { Key = "mix:card", Children = [Ui.Caption(Loc.Get(Strings.Mix.CardNoPair)) with { Color = Tok.TextSecondary }] };

        _items ??=
        [
            new SegmentedItem(Loc.Get(Strings.Mix.Smooth)), new SegmentedItem(Loc.Get(Strings.Mix.Lift)),
            new SegmentedItem(Loc.Get(Strings.Mix.Quick)), new SegmentedItem(Loc.Get(Strings.Mix.NoMix)),
        ];
        int s = style.Value, b = beats.Value;
        bool edited = touched.Value || MixEdit.Edited(in pair);
        int shownBeats = s == (int)MixStyle.Quick && pair.Grid ? MixEdit.QuickBeats(in pair) : b;
        bool overlapOn = s is (int)MixStyle.Smooth or (int)MixStyle.Lift;
        string autoLine = edited ? "" : Strings.Mix.AutoChose(MixEdit.StyleName(MixEdit.StyleOf(in pair)));
        bool savedHere = pair.Local && pair.LocalKind != MixOverrideKind.Auto;
        var p = pair;

        var header = new BoxEl
        {
            Key = "mix:card:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                Ui.BodyStrong(pair.A.Title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                Ui.Caption("⟶"),
                Ui.BodyStrong(pair.B.Title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                new BoxEl { Grow = 1f },
                Button.Subtle("‹", () => _move(_index - 1), isEnabled: _index > 0) with { Key = "mix:card:prev" },
                Ui.Caption(Strings.Mix.PairOf((_index + 1).ToString(CultureInfo.CurrentCulture), _pairs.ToString(CultureInfo.CurrentCulture))) with { MaxLines = 1, Color = Tok.TextSecondary },
                Button.Subtle("›", () => _move(_index + 1), isEnabled: _index + 1 < _pairs) with { Key = "mix:card:next" },
            ],
        };
        var styleRow = new BoxEl
        {
            Key = "mix:card:style", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                Segmented.Create(_items, style, _ => touched.Value = true) with { Key = "mix:card:styles" },
                // The Auto line keeps its slot when it empties (no relayout of the row on the first pick).
                Ui.Caption(autoLine) with { Key = "mix:card:auto", Color = Tok.TextSecondary, MaxLines = 1, MinWidth = 0f, Shrink = 1f },
            ],
        };
        var overlapRow = new BoxEl
        {
            Key = "mix:card:overlap", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                Ui.Caption(Loc.Get(Strings.Mix.Overlap)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                Button.Subtle("−", () => { beats.Value = Math.Max(MixEdit.MinBeatsFor(in p), beats.Peek() - 1); touched.Value = true; },
                    isEnabled: overlapOn && b > MixEdit.MinBeatsFor(in pair)) with { Key = "mix:card:less" },
                // The readout reserves its widest form ("16.0 s · 32 beats"), so a step never moves the "+" button.
                new BoxEl
                {
                    Key = "mix:card:readout", ZStack = true, Shrink = 0f,
                    Children =
                    [
                        new BoxEl { Opacity = 0f, HitTestVisible = false, Children = [Ui.Body(Strings.Mix.Readout("16.0", "32")) with { MaxLines = 1 }] },
                        Ui.Body(s == (int)MixStyle.NoMix ? "—"
                            : touched.Value ? MixEdit.Readout(in pair, shownBeats)
                            : MixEdit.ReadoutMs(in pair, (pair.Now.Mix ? pair.Now : pair.Auto).OutDurationMs)) with { MaxLines = 1, Color = overlapOn ? Tok.TextPrimary : Tok.TextSecondary },
                    ],
                },
                Button.Subtle("+", () => { beats.Value = Math.Min(MixEdit.MaxBeatsFor(in p), beats.Peek() + 1); touched.Value = true; },
                    isEnabled: overlapOn && b < MixEdit.MaxBeatsFor(in pair)) with { Key = "mix:card:more" },
            ],
        };
        var actions = new BoxEl
        {
            Key = "mix:card:actions", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                Ui.Caption(savedHere ? Loc.Get(Strings.Mix.SavedThisPc) : "") with { Key = "mix:card:saved", Color = Tok.TextSecondary, MaxLines = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f },
                Button.Subtle(Loc.Get(Strings.Mix.ResetToAuto), () =>
                {
                    MixEdit.Reset(in p, _playlist);
                    var fresh = MixEdit.Read(_playlist, _index);
                    style.Value = MixEdit.StyleOf(in fresh);
                    beats.Value = MixEdit.BeatsOf(in fresh, (fresh.Now.Mix ? fresh.Now : fresh.Auto).OutDurationMs);
                    touched.Value = false;
                }, isEnabled: MixEdit.Edited(in pair) || touched.Value) with { Key = "mix:card:reset" },
                Button.Accent(Loc.Get(Strings.Mix.Done), () =>
                {
                    if (touched.Peek()) MixEdit.Save(in p, _playlist, style.Peek(), beats.Peek());
                    _close();
                }) with { Key = "mix:card:done" },
            ],
        };
        return new BoxEl
        {
            Key = "mix:card", Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(0f, Spacing.M, 0f, 0f),
            Children =
            [
                new BoxEl { Height = Spacing.XXS / 2f, Fill = Tok.StrokeDividerDefault, Shrink = 0f },
                header,
                Ui.Caption(MixEdit.Compatibility(in pair)) with { Key = "mix:card:compat", Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                styleRow,
                Ui.Body(touched.Value || s != (int)MixStyle.Smooth ? MixEdit.Sentence(in pair, s, shownBeats)
                    : Strings.Mix.SentenceSmooth(((pair.Now.Mix ? pair.Now : pair.Auto).OutDurationMs / 1000.0).ToString("0.#", CultureInfo.CurrentCulture))) with { Key = "mix:card:sentence", MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                overlapRow,
                actions,
            ],
        };
    }
}
