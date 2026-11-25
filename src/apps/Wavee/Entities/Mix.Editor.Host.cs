// ── Entities/Mix.Editor.Host.cs — #48 Mix M6, the transition editor's core host (full-support plan §2.5) ─────────────────────
//
// One pair, edited in place under the Transitions list (a seam's click, a Transitions row, the Mix menu's Transitions): this host keeps
// the draft (Entities/Mix.Editor.cs), its undo and redo, Customize's disclosure, the save and its states, and hands each theme the picture
// as `MixEditorProps` (ViewKeys.MixEditor). It replaces the small seam card (Smooth / Lift / Quick / No mix): the presets are Spotify's
// own now, every one by name.
//
//   presets      Auto · Fade · Rise · Blend · Wave · Melt · Slam · Dissolve · Surge · Afterglow · No transition, and "More" with the
//                thirteen Spotify uses internally (Auto picks them)
//   timeline     both songs' waveforms on one axis around the join, beat ticks (downbeats taller), the volume curves (and the filter's
//                cutoff); drag the outgoing song to move the mix-out point, the incoming song to move its in-point, the band's end to
//                change the overlap, a curve's handle to bend it; every point snaps to the beat (the bar phase kept when beatmatched)
//   length       in beats of the outgoing song, or in bars when beatmatched (recipe 1.8)
//   Customize    Volume, EQ, Filter, Effects, Looping (always offered: owner 2026-11-25) and Beatmatching
//   actions      Undo · Redo · Hear it (the MixHearIt seam) · Reset to auto · Cancel · Save; M2's save line and Retry
//
// Save keeps M2's rule: to Spotify when the user may write the list, else on this PC; offline or refused it stays as a pending local edit
// with Retry. Cancel restores what the pair played and closes; ‹ › move to the neighbouring pair while nothing is unsaved.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

[CoreHost]
internal sealed class MixEditorHost(int playlistSlot, int index, int pairs, Action<int> move, Action close) : Component
{
    readonly int _playlist = playlistSlot, _index = index, _pairs = pairs;
    readonly Action<int> _move = move;
    readonly Action _close = close;

    // What the verbs act on: the render's own reads (the host re-renders on every change they depend on).
    MixEdit.Pair _pair;
    MixDraft _baseline, _current;
    FluentGpu.Signals.Signal<MixDraft?>? _edited;
    FluentGpu.Signals.Signal<int>? _history;
    readonly List<MixDraft> _undo = [], _redo = [];

    // The waveform levels, re-read only when a song or its analysis changes (never per render).
    (int Slot, uint Version, float[] Levels) _levelsA = (-1, 0, []), _levelsB = (-1, 0, []);
    (MixEdit.Pair Pair, MixDraft Draft, MixTimelineModel Model)? _timeline;

    Action? _prev, _next, _undoVerb, _redoVerb, _cancel, _save, _reset, _retry, _toggleCustomize, _clearCustom, _preview, _less, _more;
    Action<int>? _pickPreset;
    Action<bool>? _setBeatmatch;
    Action<long>? _commitOut, _commitIn, _commitOverlap;
    Action<bool, float>? _commitBend;
    Func<long, long>? _snapOut, _snapIn, _snapOverlap;
    Func<long, string>? _formatOut, _formatIn, _formatOverlap;
    readonly Action<int>?[] _picks = new Action<int>?[5];

    const int StepMs = 100, CurvePoints = 65;

    public override Element Render()
    {
        var pair = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            _ = Entities.Current.Edges.TrackBeats.Changed.Value;
            _ = Entities.Current.Edges.TrackCues.Changed.Value;
            _ = Entities.Current.Edges.TrackWaveform.Changed.Value;
            _ = MixPrefs.Changed.Value;
            return MixEdit.Read(_playlist, _index);
        }).Value;
        var edited = UseSignal<MixDraft?>(null);
        var customize = UseSignal(false);
        var history = UseSignal(0);
        _edited = edited;
        _history = history;
        _customize = customize;
        _ = history.Value;
        _ = MixSaves.Changed.Value;
        _ = MixHearIt.Changed.Value;

        // The analysis the timeline and the snapping read, fetched for both songs when the editor opens on them.
        int slotA = pair.A.Slot, slotB = pair.B.Slot;
        UseEffect(() =>
        {
            foreach (int s in (ReadOnlySpan<int>)[slotA, slotB])
            {
                if (s <= 0) continue;
                Entities.EnsureEdge(FetchEdge.TrackWaveform, s);
                Entities.EnsureEdge(FetchEdge.TrackMixBeats, s);
                Entities.EnsureEdge(FetchEdge.TrackCues, s);
            }
        }, FluentGpu.Hooks.DepKey.From(slotA, slotB));

        _pair = pair;
        if (!pair.Valid) return Slots.View(ViewKeys.MixEditor, Invalid());
        _baseline = MixEdit.DraftOf(in pair, _playlist);
        _current = edited.Value ?? _baseline;
        Wire();

        var d = _current;
        bool dirty = d != _baseline;
        var timing = MixEdit.TimingOf(in pair, _playlist, in _baseline, in d);
        var styles = d.Mode == MixDraftMode.None ? default : timing.Mix ? timing.Styles : d.Styles;

        // The presets: Auto, Spotify's nine in its order, No transition; the internal thirteen under More.
        var presets = ImmutableArray.CreateBuilder<MixChoice>(MixStyles.EditorOrder.Length + 2);
        presets.Add(new MixChoice(-1, Loc.Get(Strings.Mix.Auto), d.Mode == MixDraftMode.Auto));
        foreach (var id in MixStyles.EditorOrder)
            presets.Add(new MixChoice((int)id, MixEdit.PresetName((int)id), d.Mode == MixDraftMode.Preset && d.Preset == (byte)id));
        presets.Add(new MixChoice(0, Loc.Get(Strings.Mix.Editor.NoTransition), d.Mode == MixDraftMode.None));
        var more = ImmutableArray.CreateBuilder<MixChoice>(MixEdit.InternalOrder.Length);
        bool moreOn = false;
        foreach (var id in MixEdit.InternalOrder)
        {
            bool on = d.Mode == MixDraftMode.Preset && d.Preset == (byte)id;
            moreOn |= on;
            more.Add(new MixChoice((int)id, MixEdit.PresetName((int)id), on));
        }
        string moreLabel = moreOn ? MixEdit.PresetName(d.Preset) : Loc.Get(Strings.Mix.Editor.More);
        string state = d.Mode == MixDraftMode.Auto ? Strings.Mix.AutoChose(MixEdit.PresetName(pair.Auto.Mix ? pair.Auto.Preset : 1))
                     : d.Custom ? Loc.Get(Strings.Mix.Editor.Custom) : "";

        // Customize: the five ingredients, each the style it plays and the ones it offers.
        var ingredients = ImmutableArray.CreateBuilder<MixIngredientModel>(5);
        for (int i = 0; i < 5; i++)
        {
            var ingredient = (MixIngredient)i;
            int playing = MixEdit.StyleOf(in styles, ingredient);
            var offered = MixEdit.Offered(ingredient);
            var choices = ImmutableArray.CreateBuilder<MixChoice>(offered.Length + 1);
            bool listed = false;
            foreach (int id in offered)
            {
                listed |= id == playing;
                choices.Add(new MixChoice(id, MixEdit.StyleName(ingredient, id), id == playing));
            }
            // A preset's internal style (never offered) still shows as what plays.
            if (!listed) choices.Insert(0, new MixChoice(playing, MixEdit.StyleName(ingredient, playing), true));
            ingredients.Add(new MixIngredientModel("mix:ed:" + Key(ingredient), MixEdit.IngredientName(ingredient), MixEdit.StyleName(ingredient, playing),
                MixEdit.OverrideOf(in d, ingredient).HasValue, choices.ToImmutable(), _picks[i]!));
        }

        // The length: beats of A, or bars when beatmatched.
        var beatsA = Entities.Current.Edges.TrackBeats.Payload(pair.A.Slot);
        bool timed = d.Mode != MixDraftMode.None;
        string lengthValue;
        bool canLess, canMore;
        if (d.Beatmatched)
        {
            int at = Array.IndexOf(MixEdit.BarSteps, d.Bars);
            lengthValue = Strings.Mix.Editor.LengthBars(MixEdit.Seconds(d.OverlapMs), d.Bars.ToString(System.Globalization.CultureInfo.CurrentCulture));
            canLess = timed && at > 0;
            canMore = timed && at >= 0 && at + 1 < MixEdit.BarSteps.Length && MixEdit.BarsMs(in pair, MixEdit.BarSteps[at + 1]) <= MixEdit.MaxOverlapMs(in pair);
        }
        else
        {
            string beats = MixEdit.BeatsText(beatsA, d.OutStartMs, d.OverlapMs);
            lengthValue = beats.Length > 0 ? Strings.Mix.Readout(MixEdit.Seconds(d.OverlapMs), beats) : Strings.Mix.ReadoutPlain(MixEdit.Seconds(d.OverlapMs));
            canLess = timed && StepOverlap(in pair, in d, beatsA, -1) < d.OverlapMs;
            canMore = timed && StepOverlap(in pair, in d, beatsA, 1) > d.OverlapMs;
        }

        var save = MixSaves.StateOf(pair.PlaylistUri, pair.ItemA);
        bool saving = save == MixSaveState.Saving;
        bool unsaved = pair.LocalPending && !saving;
        string saveLine = saving ? Loc.Get(Strings.Mix.SavingSpotify)
                        : unsaved ? Loc.Get(save == MixSaveState.Offline ? Strings.Mix.NotSavedOffline : Strings.Mix.NotSaved)
                        : pair.Local && !pair.LocalPending && pair.LocalKind != MixOverrideKind.Auto ? Loc.Get(Strings.Mix.SavedThisPc)
                        : "";
        bool canBeatmatch = MixEdit.CanBeatmatch(in pair);
        bool previewOn = MixHearIt.Playing;
        var props = new MixEditorProps
        {
            From = pair.A.Title, To = pair.B.Title, Compat = MixEdit.Compatibility(in pair),
            Position = Strings.Mix.PairOf((_index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), _pairs.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            CanPrev = !dirty && _index > 0, CanNext = !dirty && _index + 1 < _pairs, Prev = _prev!, Next = _next!,
            Presets = presets.ToImmutable(), MorePresets = more.ToImmutable(), MoreLabel = moreLabel, MoreOn = moreOn, PickPreset = _pickPreset!,
            State = state, Sentence = MixEdit.Sentence(in d),
            Timeline = Timeline(in pair, in d, in timing),
            OutReadout = timed ? Strings.Mix.Editor.MixOut(MixEdit.Clock(d.OutStartMs)) : Strings.Mix.Editor.MixOut(MixEdit.Clock(pair.A.DurationMs)),
            InReadout = Strings.Mix.Editor.In(MixEdit.Clock(timed ? d.InStartMs : 0)),
            Length = new MixLengthModel(Loc.Get(Strings.Mix.Overlap), timed ? lengthValue : "—", canLess, canMore, _less!, _more!),
            CustomizeLabel = Loc.Get(Strings.Mix.Editor.Customize), CustomizeOpen = customize.Value, ToggleCustomize = _toggleCustomize!,
            Ingredients = ingredients.ToImmutable(),
            BeatmatchLabel = Loc.Get(Strings.Mix.Editor.Beatmatching), Beatmatched = d.Beatmatched, BeatmatchAvailable = canBeatmatch && timed,
            BeatmatchNote = canBeatmatch ? "" : Loc.Get(Strings.Mix.Editor.BeatmatchUnavailable), SetBeatmatch = _setBeatmatch!,
            ClearCustom = _clearCustom!, ClearCustomLabel = Loc.Get(Strings.Mix.Editor.ClearCustom), Custom = d.Custom,
            CanUndo = _undo.Count > 0, CanRedo = _redo.Count > 0, Undo = _undoVerb!, Redo = _redoVerb!,
            PreviewLabel = Loc.Get(previewOn ? Strings.Mix.Editor.Stop : Strings.Mix.Editor.HearIt), PreviewAvailable = MixHearIt.Available,
            Previewing = previewOn, TogglePreview = _preview!,
            SaveLine = saveLine, Saving = saving, ShowRetry = unsaved, Retry = _retry!,
            Dirty = dirty, CanReset = !saving && (MixEdit.Edited(in pair) || d.Mode != MixDraftMode.Auto), Reset = _reset!, Cancel = _cancel!,
            Save = _save!,
            UndoLabel = Loc.Get(Strings.Mix.Editor.Undo), RedoLabel = Loc.Get(Strings.Mix.Editor.Redo), ResetLabel = Loc.Get(Strings.Mix.ResetToAuto),
            CancelLabel = Loc.Get(Strings.Mix.Editor.Cancel), SaveLabel = Loc.Get(Strings.Mix.Editor.Save), RetryLabel = Loc.Get(Strings.Mix.Retry),
            PrevLabel = Loc.Get(Strings.Mix.Editor.Previous), NextLabel = Loc.Get(Strings.Mix.Editor.Next),
        };
        return Slots.View(ViewKeys.MixEditor, props);

    }

    /// <summary>The overlap one beat of A longer or shorter (the stepper), snapped to A's grid and the editor's cap.</summary>
    static uint StepOverlap(in MixEdit.Pair pair, in MixDraft d, ReadOnlySpan<uint> beatsA, int dir)
    {
        float period = beatsA.Length >= 2 ? Playback.MixPlanner.PeriodNear(beatsA, d.OutStartMs + d.OverlapMs / 2) : 500f;
        long target = (long)d.OverlapMs + (long)Math.Round(dir * period);
        long snapped = beatsA.Length >= 2 ? MixEdit.SnapOverlapBeats(beatsA, d.OutStartMs, target) : target;
        return (uint)Math.Clamp(snapped, 500, MixEdit.MaxOverlapMs(in pair));
    }

    static string Key(MixIngredient i) => i switch
    {
        MixIngredient.Volume => "volume", MixIngredient.Eq => "eq", MixIngredient.Filter => "filter", MixIngredient.Fx => "fx", _ => "looping",
    };

    /// <summary>A pair that cannot be edited (a local file, an episode): the editor's words only.</summary>
    MixEditorProps Invalid()
    {
        Wire();
        var empty = new MixTimelineModel
        {
            LevelsA = [], LevelsB = [], StepMs = StepMs, BeatsA = [], DownA = [], BeatsB = [], DownB = [], DurationA = 0, DurationB = 0,
            OutStartMs = 0, OverlapMs = 0, InStartMs = 0, InWindowMs = 0, SpeedB = 1f, VolumeOut = [], VolumeIn = [], Editable = false, None = true,
            LabelA = "", LabelB = "", SnapOut = _snapOut!, SnapIn = _snapIn!, SnapOverlap = _snapOverlap!, FormatOut = _formatOut!, FormatIn = _formatIn!,
            FormatOverlap = _formatOverlap!, CommitOut = _commitOut!, CommitIn = _commitIn!, CommitOverlap = _commitOverlap!, CommitBend = _commitBend!,
            BendOut = 0f, BendIn = 0f,
        };
        string none = Loc.Get(Strings.Mix.CardNoPair);
        return new MixEditorProps
        {
            From = "", To = "", Compat = none, Position = "", CanPrev = false, CanNext = false, Prev = _prev!, Next = _next!,
            Presets = [], MorePresets = [], MoreLabel = "", MoreOn = false, PickPreset = _pickPreset!, State = "", Sentence = "",
            Timeline = empty, OutReadout = "", InReadout = "", Length = new MixLengthModel("", "", false, false, _less!, _more!),
            CustomizeLabel = "", CustomizeOpen = false, ToggleCustomize = _toggleCustomize!, Ingredients = [], BeatmatchLabel = "",
            Beatmatched = false, BeatmatchAvailable = false, BeatmatchNote = "", SetBeatmatch = _setBeatmatch!, ClearCustom = _clearCustom!, ClearCustomLabel = "",
            Custom = false, CanUndo = false, CanRedo = false, Undo = _undoVerb!, Redo = _redoVerb!, PreviewLabel = "", PreviewAvailable = false,
            Previewing = false, TogglePreview = _preview!, SaveLine = "", Saving = false, ShowRetry = false, Retry = _retry!, Dirty = false,
            CanReset = false, Reset = _reset!, Cancel = _cancel!, Save = _save!, UndoLabel = "", RedoLabel = "", ResetLabel = "",
            CancelLabel = Loc.Get(Strings.Mix.Close), SaveLabel = "", RetryLabel = "", PrevLabel = "", NextLabel = "",
        };
    }

    // ── the timeline's data ────────────────────────────────────────────────────────────────────────────────────────────

    MixTimelineModel Timeline(in MixEdit.Pair pair, in MixDraft d, in MixTiming timing)
    {
        if (_timeline is { } hit && hit.Pair == pair && hit.Draft == d) return hit.Model;
        var edges = Entities.Current.Edges;
        var (beatsA, downA) = Grid(edges.TrackBeats.Payload(pair.A.Slot));
        var (beatsB, downB) = Grid(edges.TrackBeats.Payload(pair.B.Slot));
        bool none = d.Mode == MixDraftMode.None || !timing.Mix;
        var volOut = new float[CurvePoints];
        var volIn = new float[CurvePoints];
        float[]? cutOut = null, cutIn = null;
        if (!none)
        {
            if (!timing.Sample(MixCurveLane.Volume, false, volOut)) volOut.AsSpan().Fill(1f);
            if (!timing.Sample(MixCurveLane.Volume, true, volIn)) volIn.AsSpan().Fill(1f);
            var c = new float[CurvePoints];
            if (timing.Sample(MixCurveLane.FilterCutoff, false, c)) cutOut = c;
            c = new float[CurvePoints];
            if (timing.Sample(MixCurveLane.FilterCutoff, true, c)) cutIn = c;
        }
        // B's curves run over B's own window (its media time), mapped to mixer time by its speed.
        float speedB = timing.Mix && timing.SpeedB > 0f ? timing.SpeedB : 1f;
        var model = new MixTimelineModel
        {
            LevelsA = Levels(pair.A, ref _levelsA), LevelsB = Levels(pair.B, ref _levelsB), StepMs = StepMs,
            BeatsA = beatsA, DownA = downA, BeatsB = beatsB, DownB = downB, DurationA = pair.A.DurationMs, DurationB = pair.B.DurationMs,
            OutStartMs = none ? pair.A.DurationMs : timing.OutStartMs, OverlapMs = none ? 0 : timing.OutDurationMs,
            InStartMs = none ? 0 : timing.InStartMs, InWindowMs = none ? 0 : (long)Math.Round(timing.InDurationMs / speedB), SpeedB = speedB,
            VolumeOut = volOut, VolumeIn = volIn, CutoffOut = cutOut, CutoffIn = cutIn, Editable = !none, None = none,
            LabelA = pair.A.Title, LabelB = pair.B.Title,
            SnapOut = _snapOut!, SnapIn = _snapIn!, SnapOverlap = _snapOverlap!, FormatOut = _formatOut!, FormatIn = _formatIn!, FormatOverlap = _formatOverlap!,
            CommitOut = _commitOut!, CommitIn = _commitIn!, CommitOverlap = _commitOverlap!, CommitBend = _commitBend!,
            BendOut = MixEdit.BendOf(in d, false), BendIn = MixEdit.BendOf(in d, true),
        };
        _timeline = (pair, d, model);
        return model;
    }

    static (uint[] Ms, bool[] Down) Grid(ReadOnlySpan<uint> beats)
    {
        var ms = new uint[beats.Length];
        var down = new bool[beats.Length];
        for (int i = 0; i < beats.Length; i++) { ms[i] = BeatGrid.StartMs(beats[i]); down[i] = BeatGrid.IsDownbeat(beats[i]); }
        return (ms, down);
    }

    /// <summary>A song's waveform as 0..1 levels every <see cref="StepMs"/> (kind 237's three bands summed, the loudest 1); empty without one.</summary>
    static float[] Levels(Track t, ref (int Slot, uint Version, float[] Levels) cache)
    {
        var edges = Entities.Current.Edges.TrackWaveform;
        uint version = edges.Version(t.Slot);
        if (cache.Slot == t.Slot && cache.Version == version) return cache.Levels;
        var samples = edges.Payload(t.Slot);
        float[] levels = [];
        if (!samples.IsEmpty && t.DurationMs > 0)
        {
            levels = new float[Math.Max(1, t.DurationMs / StepMs)];
            float peak = 0f;
            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = WaveformBands.LevelAt(samples, (long)i * StepMs, t.DurationMs);
                peak = MathF.Max(peak, levels[i]);
            }
            if (peak > 0f) for (int i = 0; i < levels.Length; i++) levels[i] /= peak;
        }
        cache = (t.Slot, version, levels);
        return levels;
    }

    // ── the verbs ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every verb once: each reads the latest render's pair and draft.</summary>
    void Wire()
    {
        if (_save is not null) return;
        _prev = () => _move(_index - 1);
        _next = () => _move(_index + 1);
        _pickPreset = id => Apply(id < 0 ? MixEdit.DraftOfAuto(in _pair) : MixEdit.WithPreset(in _current, id));
        for (int i = 0; i < 5; i++)
        {
            var ingredient = (MixIngredient)i;
            _picks[i] = id => Apply(MixEdit.WithStyle(in _current, ingredient, id));
        }
        _clearCustom = () => Apply(MixEdit.WithoutCustom(in _current));
        _setBeatmatch = on => Apply(MixEdit.WithBeatmatch(in _pair, in _current, on));
        _toggleCustomize = () => { var c = CustomizeSignal(); c.Value = !c.Peek(); };
        _less = () => StepLength(-1);
        _more = () => StepLength(1);
        _undoVerb = () =>
        {
            if (_undo.Count == 0) return;
            _redo.Add(_current);
            Set(_undo[^1]);
            _undo.RemoveAt(_undo.Count - 1);
        };
        _redoVerb = () =>
        {
            if (_redo.Count == 0) return;
            _undo.Add(_current);
            Set(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
        };
        _cancel = () =>
        {
            MixHearIt.StopIfPlaying();
            Forget();
            _close();
        };
        _save = () =>
        {
            var pair = _pair;
            var from = _baseline;
            var to = _current;
            if (to == from) { MixHearIt.StopIfPlaying(); _close(); return; }
            // A save to Spotify keeps the editor open until the reply lands ("Saving to Spotify…"), then closes it; a refusal or no
            // connection leaves it open on the "not saved" line with Retry, the edit already playing here. A save on this PC closes at once.
            MixEdit.Save(in pair, _playlist, in from, in to, state =>
            {
                Forget();
                if (state == MixSaveState.None) { MixHearIt.StopIfPlaying(); _close(); }
            });
        };
        _reset = () =>
        {
            var pair = _pair;
            if (MixEdit.Edited(in pair)) MixEdit.Reset(in pair, _playlist, _ => Forget());
            else Forget();
        };
        _retry = () => MixEdit.Retry(in _pair, _playlist, state => { if (state == MixSaveState.None) Forget(); });
        _preview = () =>
        {
            // The draft as it would save (an Auto draft has no recipe: the pair's own Auto plays), patched over the bytes it replaces.
            var pair = _pair;
            var d = _current;
            if (MixEdit.Build(in pair, _playlist, in _baseline, in d, out var recipe, out byte[] raw)) MixHearIt.Toggle(_playlist, _index, recipe, raw);
            else MixHearIt.Toggle(_playlist, _index, null, null);
        };

        // The timeline's snapping, readouts and commits (see MixTimelineModel).
        _snapOut = ms =>
        {
            var beats = Entities.Current.Edges.TrackBeats.Payload(_pair.A.Slot);
            long max = Math.Max(0, _pair.A.DurationMs - (long)_current.OverlapMs);
            long snapped = MixEdit.SnapToBeat(beats, Math.Clamp(ms, 0, max), _current.Beatmatched ? _current.OutStartMs : -1);
            return Math.Clamp(snapped < 0 ? ms / 10 * 10 : snapped, 0, max);
        };
        _snapIn = ms =>
        {
            var beats = Entities.Current.Edges.TrackBeats.Payload(_pair.B.Slot);
            long max = Math.Max(0, _pair.B.DurationMs - 1_000);
            long snapped = MixEdit.SnapToBeat(beats, Math.Clamp(ms, 0, max), _current.Beatmatched ? _current.InStartMs : -1);
            return Math.Clamp(snapped < 0 ? ms / 10 * 10 : snapped, 0, max);
        };
        _snapOverlap = ms =>
        {
            uint max = MixEdit.MaxOverlapMs(in _pair);
            if (_current.Beatmatched) return MixEdit.BarsMs(in _pair, MixEdit.NearestBars(in _pair, ms));
            // A's grid can end before A does: past its last beat the overlap follows the drag (to 10 ms) up to A's end.
            var beats = Entities.Current.Edges.TrackBeats.Payload(_pair.A.Slot);
            long limit = Math.Min(max, Math.Max(500, _pair.A.DurationMs - (long)_current.OutStartMs));
            long target = Math.Clamp(ms, 500, limit);
            long snapped = MixEdit.SnapOverlapBeats(beats, _current.OutStartMs, target);
            if (Math.Abs(snapped - target) > MixEdit.BarMs(in _pair) / 4) snapped = target / 10 * 10;
            return Math.Clamp(snapped, 500, limit);
        };
        _formatOut = ms => Strings.Mix.Editor.MixOut(MixEdit.Clock(ms));
        _formatIn = ms => Strings.Mix.Editor.In(MixEdit.Clock(ms));
        _formatOverlap = ms =>
        {
            if (_current.Beatmatched)
                return Strings.Mix.Editor.LengthBars(MixEdit.Seconds(ms), MixEdit.NearestBars(in _pair, ms).ToString(System.Globalization.CultureInfo.CurrentCulture));
            string beats = MixEdit.BeatsText(Entities.Current.Edges.TrackBeats.Payload(_pair.A.Slot), _current.OutStartMs, ms);
            return beats.Length > 0 ? Strings.Mix.Readout(MixEdit.Seconds(ms), beats) : Strings.Mix.ReadoutPlain(MixEdit.Seconds(ms));
        };
        _commitOut = ms => Apply(MixEdit.WithOut(in _current, (uint)Math.Max(0, ms)));
        _commitIn = ms => Apply(MixEdit.WithIn(in _current, (uint)Math.Max(0, ms)));
        _commitOverlap = ms => Apply(MixEdit.WithOverlap(in _pair, in _current, (uint)Math.Max(1, ms), _current.Beatmatched ? MixEdit.NearestBars(in _pair, ms) : 0, keepEnd: false));
        _commitBend = (incoming, bend) => Apply(MixEdit.WithVolumeBend(in _current, incoming, bend));
    }

    FluentGpu.Signals.Signal<bool>? _customize;
    FluentGpu.Signals.Signal<bool> CustomizeSignal() => _customize!;

    /// <summary>The length stepper: one beat of A (or one bar step when beatmatched), the transition's end kept.</summary>
    void StepLength(int dir)
    {
        var d = _current;
        var pair = _pair;
        if (d.Beatmatched)
        {
            int at = Array.IndexOf(MixEdit.BarSteps, d.Bars);
            int next = Math.Clamp((at < 0 ? 1 : at) + dir, 0, MixEdit.BarSteps.Length - 1);
            int bars = MixEdit.BarSteps[next];
            if (MixEdit.BarsMs(in pair, bars) > MixEdit.MaxOverlapMs(in pair)) return;
            Apply(MixEdit.WithOverlap(in pair, in d, MixEdit.BarsMs(in pair, bars), bars, keepEnd: true));
            return;
        }
        uint ms = StepOverlap(in pair, in d, Entities.Current.Edges.TrackBeats.Payload(pair.A.Slot), dir);
        if (ms == d.OverlapMs) return;
        Apply(MixEdit.WithOverlap(in pair, in d, ms, 0, keepEnd: true));
    }

    /// <summary>One edit: the draft before it goes on the undo stack, redo empties.</summary>
    void Apply(MixDraft next)
    {
        if (next == _current) return;
        _undo.Add(_current);
        _redo.Clear();
        Set(next);
    }

    void Set(MixDraft d)
    {
        _edited!.Value = d == _baseline ? null : d;
        _history!.Value = _history.Peek() + 1;
    }

    /// <summary>The draft is spent (saved, reset or cancelled): the pair's own state shows again.</summary>
    void Forget()
    {
        _undo.Clear();
        _redo.Clear();
        _edited!.Value = null;
        _history!.Value = _history.Peek() + 1;
    }
}
