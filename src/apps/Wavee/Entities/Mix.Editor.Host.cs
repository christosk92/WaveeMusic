// ── Entities/Mix.Editor.Host.cs — #48 Mix M6, the transition editor's core host (full-support plan §2.5) ─────────────────────
//
// One pair, edited in the right panel ("Edit transition", opened from the pair's chip in the list) or full-page (the expand button; the
// `transition:` route). This host reads the pair and its draft (kept in MixEditSession, so expanding keeps every unsaved edit), wires the
// verbs, and hands each theme the picture as `MixEditorProps` (ViewKeys.MixEditor); one template draws both sizes.
//
//   head         "Edit transition", Undo · Redo, Save, expand / collapse, close (close drops an unsaved edit)
//   the songs    each with its tempo and key (a warning mark when the tempos are far apart)
//   timeline     both songs' waveforms on one axis around the join, beat ticks (downbeats taller), the volume curves (and the filter's
//                cutoff); drag the outgoing song to move the mix-out point, the incoming song to move its in-point, the band's end to
//                change the overlap, a curve's handle to bend it; every point snaps to the beat (the bar phase kept when beatmatched) and
//                the picture and the numbers follow the pointer on every move (MixTimelineModel.Preview), committed on release. Hear it
//                (the MixHearIt seam, Playback.MixPreview) sits between the two lanes.
//   presets      Auto · Fade · Rise · Blend · Wave · Melt · Slam · Dissolve · Surge · Afterglow · No transition, and "More" with the
//                thirteen Spotify uses internally (Auto picks them)
//   ingredients  Volume, EQ, Filter, Effects, Looping (always offered: owner 2026-11-25) and Beatmatching
//   length       in beats of the outgoing song, or in bars when beatmatched (recipe 1.8)
//   foot         M2's save line, Retry, Reset to auto
//
// Save keeps M2's rule: to Spotify when the user may write the list, else on this PC; offline or refused it stays as a pending local edit
// with Retry. On a mixed list the user can't edit (M8's Duplicate Mix is offered) Save first asks, inline as the official client does:
// Duplicate Mix (the copy carries this edit as an override and opens) or Save on this PC.

using System.Collections.Immutable;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

[CoreHost]
internal sealed class MixEditorHost(int playlistSlot, int index, bool expanded) : Component
{
    readonly int _playlist = playlistSlot, _index = index;
    readonly bool _expanded = expanded;

    // What the verbs act on: the render's own reads (the host re-renders on every change they depend on).
    MixEdit.Pair _pair;
    MixDraft _baseline, _current;

    // The waveform levels, re-read only when a song or its analysis changes (never per render).
    (int Slot, uint Version, float[] Levels) _levelsA = (-1, 0, []), _levelsB = (-1, 0, []);
    (MixEdit.Pair Pair, MixDraft Draft, MixTimelineModel Model)? _timeline;
    MixReadout _readout;

    Action? _undoVerb, _redoVerb, _save, _reset, _retry, _clearCustom, _preview, _less, _more, _saveHere, _duplicate, _endPreview;
    Func<MixReadout>? _readoutRead;
    Func<MixTimelineEdit, MixTimelineModel>? _previewModel;
    /// <summary>Save asks first (the list is mixed and the user can't edit it: Duplicate Mix is offered). The latest render's answer.</summary>
    bool _asks;
    Action<int>? _pickPreset;
    Action<bool>? _setBeatmatch;
    Action<long>? _commitOut, _commitIn, _commitOverlap;
    Action<bool, float>? _commitBend;
    Func<long, long>? _snapOut, _snapIn, _snapOverlap;
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
            _ = Entities.Current.Tracks.Changed.Value;
            return MixEdit.Read(_playlist, _index);
        }).Value;
        _ = MixEditSession.History.Value;
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
        Wire();
        if (!pair.Valid) return Slots.View(ViewKeys.MixEditor, Invalid());
        _baseline = MixEdit.DraftOf(in pair, _playlist);
        _current = MixEditSession.Edited.Value ?? _baseline;

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

        // The ingredients: each the style it plays and the ones it offers.
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

        // The length stepper: one beat of A, or one bar step when beatmatched.
        var beatsA = Entities.Current.Edges.TrackBeats.Payload(pair.A.Slot);
        bool timed = d.Mode != MixDraftMode.None;
        bool canLess, canMore;
        if (d.Beatmatched)
        {
            int at = Array.IndexOf(MixEdit.BarSteps, d.Bars);
            canLess = timed && at > 0;
            canMore = timed && at >= 0 && at + 1 < MixEdit.BarSteps.Length && MixEdit.BarsMs(in pair, MixEdit.BarSteps[at + 1]) <= MixEdit.MaxOverlapMs(in pair);
        }
        else
        {
            canLess = timed && StepOverlap(in pair, in d, beatsA, -1) < d.OverlapMs;
            canMore = timed && StepOverlap(in pair, in d, beatsA, 1) > d.OverlapMs;
        }
        _readout = ReadoutOf(in pair, in d);

        var save = MixSaves.StateOf(pair.PlaylistUri, pair.ItemA);
        bool copying = MixEditSession.Duplicating.Value;
        bool saving = save == MixSaveState.Saving || copying;
        // Save's prompt (M8 + M6): a mixed list the user can't edit offers Duplicate Mix with this edit in it. An Auto draft saves no recipe (Reset).
        _asks = !pair.Writes && d.Mode != MixDraftMode.Auto && Spotify.MixLists.CanDuplicate(new Playlist(_playlist));
        bool asking = _asks && MixEditSession.Prompt.Value && dirty;
        bool unsaved = pair.LocalPending && !saving;
        string saveLine = copying ? Loc.Get(Strings.Mix.Editor.Duplicating)
                        : saving ? Loc.Get(Strings.Mix.SavingSpotify)
                        : unsaved ? Loc.Get(save == MixSaveState.Offline ? Strings.Mix.NotSavedOffline : Strings.Mix.NotSaved)
                        : pair.Local && !pair.LocalPending && pair.LocalKind != MixOverrideKind.Auto ? Loc.Get(Strings.Mix.SavedThisPc)
                        : "";
        bool canBeatmatch = MixEdit.CanBeatmatch(in pair);
        bool previewOn = MixHearIt.Playing;
        bool apart = Playback.Mix.Tempo(pair.A.Tempo, pair.B.Tempo) == Playback.Mix.TempoRelation.Apart;
        var props = Frame() with
        {
            A = Facts(pair.A, apart), B = Facts(pair.B, apart), Compat = MixEdit.Compatibility(in pair),
            Presets = presets.ToImmutable(), MorePresets = more.ToImmutable(), MoreLabel = moreLabel, MoreOn = moreOn,
            State = state, Sentence = MixEdit.Sentence(in d),
            Timeline = Timeline(in pair, in d, in timing),
            Length = new MixLengthModel(Loc.Get(Strings.Mix.Overlap), canLess, canMore, _less!, _more!),
            Ingredients = ingredients.ToImmutable(),
            Beatmatched = d.Beatmatched, BeatmatchAvailable = canBeatmatch && timed,
            BeatmatchNote = canBeatmatch ? "" : Loc.Get(Strings.Mix.Editor.BeatmatchUnavailable),
            Custom = d.Custom,
            CanUndo = MixEditSession.Undo.Count > 0, CanRedo = MixEditSession.Redo.Count > 0,
            PreviewLabel = Loc.Get(previewOn ? Strings.Mix.Editor.Stop : Strings.Mix.Editor.HearIt), PreviewAvailable = MixHearIt.Available,
            Previewing = previewOn,
            SaveLine = saveLine, Saving = saving, ShowRetry = unsaved,
            Dirty = dirty, CanReset = !saving && (MixEdit.Edited(in pair) || d.Mode != MixDraftMode.Auto),
            SaveLabel = Loc.Get(Strings.Mix.Editor.Save), ResetLabel = Loc.Get(Strings.Mix.ResetToAuto),
            PromptTitle = asking ? Loc.Get(Strings.Mix.CantEdit) : "", PromptBody = asking ? Loc.Get(Strings.Mix.Editor.DuplicateBody) : "",
            DuplicateLabel = Loc.Get(Strings.Mix.Duplicate), CanDuplicate = asking && !copying && Spotify.MixLists.Online, Duplicate = _duplicate,
            SaveHereLabel = Loc.Get(Strings.Mix.Editor.SaveHere), SaveHere = _saveHere,
        };
        return Slots.View(ViewKeys.MixEditor, props);
    }

    /// <summary>The props every state shares: the head's words and verbs, every verb wired, nothing to edit.</summary>
    MixEditorProps Frame()
    {
        var none = new MixTrackFacts("", "", null, "", "", false);
        return new MixEditorProps
        {
            Title = Loc.Get(Strings.Mix.Editor.Title), Expanded = _expanded, ToggleExpand = MixEditSession.ToggleExpand,
            ExpandLabel = Loc.Get(_expanded ? Strings.Mix.Editor.Collapse : Strings.Mix.Editor.Expand),
            Close = MixEditSession.Close, CloseLabel = Loc.Get(Strings.Mix.Close),
            A = none, B = none, Compat = "", PresetsLabel = Loc.Get(Strings.Mix.Editor.Presets),
            Presets = [], MorePresets = [], MoreLabel = "", MoreOn = false, PickPreset = _pickPreset!, State = "", Sentence = "",
            Timeline = Empty(), Readout = _readoutRead!, Length = new MixLengthModel("", false, false, _less!, _more!),
            Ingredients = [], BeatmatchLabel = Loc.Get(Strings.Mix.Editor.Beatmatching), Beatmatched = false, BeatmatchAvailable = false,
            BeatmatchNote = "", SetBeatmatch = _setBeatmatch!, ClearCustom = _clearCustom!, ClearCustomLabel = Loc.Get(Strings.Mix.Editor.ClearCustom),
            Custom = false, CanUndo = false, CanRedo = false, Undo = _undoVerb!, Redo = _redoVerb!, PreviewLabel = "", PreviewAvailable = false,
            Previewing = false, TogglePreview = _preview!, SaveLine = "", Saving = false, ShowRetry = false, Retry = _retry!, Dirty = false,
            CanReset = false, Reset = _reset!, Save = _save!, UndoLabel = Loc.Get(Strings.Mix.Editor.Undo), RedoLabel = Loc.Get(Strings.Mix.Editor.Redo),
            ResetLabel = "", SaveLabel = "", RetryLabel = Loc.Get(Strings.Mix.Retry),
            Learn = MixLesson.Open, LearnLabel = Loc.Get(Strings.Mix.Lesson.Learn),
        };
    }

    /// <summary>A pair that cannot be edited (a local file, an episode): the head and the words only.</summary>
    MixEditorProps Invalid()
    {
        _readout = default;
        return Frame() with { Compat = Loc.Get(Strings.Mix.CardNoPair) };
    }

    MixTimelineModel Empty() => new()
    {
        LevelsA = [], LevelsB = [], StepMs = StepMs, BeatsA = [], DownA = [], BeatsB = [], DownB = [], DurationA = 0, DurationB = 0,
        OutStartMs = 0, OverlapMs = 0, InStartMs = 0, InWindowMs = 0, SpeedB = 1f, VolumeOut = [], VolumeIn = [], Editable = false, None = true,
        LabelA = "", LabelB = "", SnapOut = _snapOut!, SnapIn = _snapIn!, SnapOverlap = _snapOverlap!, Preview = _previewModel!, EndPreview = _endPreview!,
        CommitOut = _commitOut!, CommitIn = _commitIn!, CommitOverlap = _commitOverlap!, CommitBend = _commitBend!, BendOut = 0f, BendIn = 0f,
    };

    /// <summary>A song's head: its title and artists, its cover, its tempo and key.</summary>
    static MixTrackFacts Facts(Track t, bool apart)
    {
        string tempo = t.Tempo > 0 ? Strings.Mix.Editor.Tempo((t.Tempo / 10.0).ToString("0", CultureInfo.CurrentCulture)) : "";
        return new MixTrackFacts(t.Title, Entities.Strings.Resolve(t.ArtistLineId), Controls.ArtUrl(t.ImageId), tempo,
            Track.Format.CamelotLabel(t.Camelot) ?? "", apart && tempo.Length > 0);
    }

    /// <summary>The numbers of a draft: the out-point, the in-point and the length (beats of A, or bars when beatmatched).</summary>
    static MixReadout ReadoutOf(in MixEdit.Pair pair, in MixDraft d)
    {
        if (d.Mode == MixDraftMode.None)
            return new MixReadout(Strings.Mix.Editor.MixOut(MixEdit.Clock(pair.A.DurationMs)), Strings.Mix.Editor.In(MixEdit.Clock(0)), "—");
        return new MixReadout(Strings.Mix.Editor.MixOut(MixEdit.Clock(d.OutStartMs)), Strings.Mix.Editor.In(MixEdit.Clock(d.InStartMs)),
            LengthText(in pair, d.OutStartMs, d.OverlapMs, d.Beatmatched));
    }

    static string LengthText(in MixEdit.Pair pair, long outStart, long ms, bool beatmatched)
    {
        if (beatmatched)
            return Strings.Mix.Editor.LengthBars(MixEdit.Seconds(ms), MixEdit.NearestBars(in pair, ms).ToString(CultureInfo.CurrentCulture));
        string beats = MixEdit.BeatsText(Entities.Current.Edges.TrackBeats.Payload(pair.A.Slot), outStart, ms);
        return beats.Length > 0 ? Strings.Mix.Readout(MixEdit.Seconds(ms), beats) : Strings.Mix.ReadoutPlain(MixEdit.Seconds(ms));
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

    // ── the timeline's data ────────────────────────────────────────────────────────────────────────────────────────────

    MixTimelineModel Timeline(in MixEdit.Pair pair, in MixDraft d, in MixTiming timing)
    {
        if (_timeline is { } hit && hit.Pair == pair && hit.Draft == d) return hit.Model;
        var model = Model(in pair, in d, in timing);
        _timeline = (pair, d, model);
        return model;
    }

    MixTimelineModel Model(in MixEdit.Pair pair, in MixDraft d, in MixTiming timing)
    {
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
        return new MixTimelineModel
        {
            LevelsA = Levels(pair.A, ref _levelsA), LevelsB = Levels(pair.B, ref _levelsB), StepMs = StepMs,
            BeatsA = beatsA, DownA = downA, BeatsB = beatsB, DownB = downB, DurationA = pair.A.DurationMs, DurationB = pair.B.DurationMs,
            OutStartMs = none ? pair.A.DurationMs : timing.OutStartMs, OverlapMs = none ? 0 : timing.OutDurationMs,
            InStartMs = none ? 0 : timing.InStartMs, InWindowMs = none ? 0 : (long)Math.Round(timing.InDurationMs / speedB), SpeedB = speedB,
            VolumeOut = volOut, VolumeIn = volIn, CutoffOut = cutOut, CutoffIn = cutIn, Editable = !none, None = none,
            LabelA = pair.A.Title, LabelB = pair.B.Title,
            SnapOut = _snapOut!, SnapIn = _snapIn!, SnapOverlap = _snapOverlap!, Preview = _previewModel!, EndPreview = _endPreview!,
            CommitOut = _commitOut!, CommitIn = _commitIn!, CommitOverlap = _commitOverlap!, CommitBend = _commitBend!,
            BendOut = MixEdit.BendOf(in d, false), BendIn = MixEdit.BendOf(in d, true),
        };
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

    /// <summary>The draft one dragged value would make (the commit's own math, not yet applied).</summary>
    MixDraft Dragged(MixTimelineEdit e) => e.Kind switch
    {
        MixTimelineEditKind.Out => MixEdit.WithOut(in _current, (uint)Math.Max(0, e.Ms)),
        MixTimelineEditKind.In => MixEdit.WithIn(in _current, (uint)Math.Max(0, e.Ms)),
        MixTimelineEditKind.Overlap => MixEdit.WithOverlap(in _pair, in _current, (uint)Math.Max(1, e.Ms),
            _current.Beatmatched ? MixEdit.NearestBars(in _pair, e.Ms) : 0, keepEnd: false),
        _ => MixEdit.WithVolumeBend(in _current, e.Kind == MixTimelineEditKind.BendIn, e.Bend),
    };

    // ── the verbs ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every verb once: each reads the latest render's pair and draft.</summary>
    void Wire()
    {
        if (_save is not null) return;
        _readoutRead = () => MixEditSession.LiveReadout.Value ?? _readout;
        _pickPreset = id => Apply(id < 0 ? MixEdit.DraftOfAuto(in _pair) : MixEdit.WithPreset(in _current, id));
        for (int i = 0; i < 5; i++)
        {
            var ingredient = (MixIngredient)i;
            _picks[i] = id => Apply(MixEdit.WithStyle(in _current, ingredient, id));
        }
        _clearCustom = () => Apply(MixEdit.WithoutCustom(in _current));
        _setBeatmatch = on => Apply(MixEdit.WithBeatmatch(in _pair, in _current, on));
        _less = () => StepLength(-1);
        _more = () => StepLength(1);
        _undoVerb = () =>
        {
            var undo = MixEditSession.Undo;
            if (undo.Count == 0) return;
            MixEditSession.Redo.Add(_current);
            var back = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            Set(back);
        };
        _redoVerb = () =>
        {
            var redo = MixEditSession.Redo;
            if (redo.Count == 0) return;
            MixEditSession.Undo.Add(_current);
            var again = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            Set(again);
        };
        _save = () =>
        {
            // A list the user can't edit asks first: the prompt's Duplicate Mix or Save on this PC (Save rests while it asks).
            if (_asks && _current != _baseline) { MixEditSession.Prompt.Value = true; return; }
            SaveNow();
        };
        _saveHere = SaveNow;
        _duplicate = () =>
        {
            // Duplicate Mix with this edit as the copy's transition for the pair (the official editor's override: the from-item and the recipe,
            // base64, as Save would send it). Landed: the copy opens (this page goes); else the toast says why and the prompt stays.
            var pair = _pair;
            var d = _current;
            if (MixEditSession.Duplicating.Peek() || !MixEdit.Build(in pair, _playlist, in _baseline, in d, out var recipe, out byte[] raw)) return;
            string encoded = Convert.ToBase64String(MixRecipe.EncodeToArray(in recipe, raw));
            MixHearIt.StopIfPlaying();
            MixEditSession.Duplicating.Value = true;
            Spotify.MixLists.Duplicate(new Playlist(_playlist), [new Spotify.MixLists.Override(pair.ItemA, encoded)], outcome =>
            {
                MixEditSession.Duplicating.Value = false;
                if (outcome == Spotify.MixLists.Outcome.Landed) MixEditSession.Close();
            });
        };
        _reset = () =>
        {
            var pair = _pair;
            if (MixEdit.Edited(in pair)) MixEdit.Reset(in pair, _playlist, _ => MixEditSession.Forget());
            else MixEditSession.Forget();
        };
        _retry = () => MixEdit.Retry(in _pair, _playlist, state => { if (state == MixSaveState.None) MixEditSession.Forget(); });
        _preview = () =>
        {
            // The draft as it would save (an Auto draft has no recipe: the pair's own Auto plays), patched over the bytes it replaces.
            var pair = _pair;
            var d = _current;
            if (MixEdit.Build(in pair, _playlist, in _baseline, in d, out var recipe, out byte[] raw)) MixHearIt.Toggle(_playlist, _index, recipe, raw);
            else MixHearIt.Toggle(_playlist, _index, null, null);
        };

        // The timeline's snapping, live preview and commits (see MixTimelineModel).
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
        // A drag's picture before its release: the draft it would commit, drawn and read out (the editor's own props move on the release).
        _previewModel = e =>
        {
            var pair = _pair;
            var d = Dragged(e);
            var timing = MixEdit.TimingOf(in pair, _playlist, in _baseline, in d);
            MixEditSession.LiveReadout.Value = ReadoutOf(in pair, in d);
            return Model(in pair, in d, in timing);
        };
        _endPreview = () => MixEditSession.LiveReadout.Value = null;
        _commitOut = ms => Commit(new MixTimelineEdit(MixTimelineEditKind.Out, ms, 0f));
        _commitIn = ms => Commit(new MixTimelineEdit(MixTimelineEditKind.In, ms, 0f));
        _commitOverlap = ms => Commit(new MixTimelineEdit(MixTimelineEditKind.Overlap, ms, 0f));
        _commitBend = (incoming, bend) => Commit(new MixTimelineEdit(incoming ? MixTimelineEditKind.BendIn : MixTimelineEditKind.BendOut, 0, bend));
    }

    void Commit(MixTimelineEdit e)
    {
        MixEditSession.LiveReadout.Value = null;
        Apply(Dragged(e));
    }

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
        MixEditSession.Undo.Add(_current);
        MixEditSession.Redo.Clear();
        Set(next);
    }

    void Set(MixDraft d)
    {
        _current = d;
        MixEditSession.Edited.Value = d == _baseline ? null : d;
        MixEditSession.History.Value = MixEditSession.History.Peek() + 1;
    }

    /// <summary>Save as M2 does: to Spotify when the pair writes, else on this PC.</summary>
    void SaveNow()
    {
        var pair = _pair;
        var from = _baseline;
        var to = _current;
        MixEditSession.Prompt.Value = false;
        if (to == from) { MixEditSession.Close(); return; }
        // A save to Spotify keeps the editor open until the reply lands ("Saving to Spotify…"), then closes it; a refusal or no
        // connection leaves it open on the "not saved" line with Retry, the edit already playing here. A save on this PC closes at once.
        MixEdit.Save(in pair, _playlist, in from, in to, state =>
        {
            MixEditSession.Forget();
            if (state == MixSaveState.None) MixEditSession.Close();
        });
    }
}

/// <summary>The panel's Transition body: the editor on the session's pair, keyed by the pair (another pair mounts a fresh host). Its unmount
/// (the panel switched to another body) ends the session unless the editor moved to the full page.</summary>
internal sealed class MixEditorPanelBody : Component
{
    public override Element Render()
    {
        var t = MixEditSession.Open.Value;
        UseEffect(() => () => { if (!MixEditSession.Expanded.Peek()) MixEditSession.Ended(); }, FluentGpu.Hooks.DepKey.Empty);
        if (t.IsNone) return new BoxEl { Key = "mix:ed:none", Grow = 1f };
        return new BoxEl
        {
            Key = "mix:ed:panel", Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new MixEditorHost(t.Playlist, t.Index, expanded: false))
                    with { Key = "mix:ed:" + t.Playlist.ToString(CultureInfo.InvariantCulture) + ":" + t.Index.ToString(CultureInfo.InvariantCulture) },
            ],
        };
    }
}

/// <summary>The `transition:` route's page: the editor full-page on the session's pair. Leaving the page any other way than collapse or close
/// (Back, another destination) ends the session.</summary>
internal sealed class MixEditorPage : Component
{
    public override Element Render()
    {
        var t = MixEditSession.Open.Value;
        bool here = UseComputed(() => Shell.Current.Value.Kind == Shell.RouteKind.MixTransition).Value;
        UseEffect(() => { if (!here && MixEditSession.Expanded.Peek()) MixEditSession.Ended(); }, FluentGpu.Hooks.DepKey.From(here ? 1 : 0));
        UseEffect(() => () => { if (MixEditSession.Expanded.Peek()) MixEditSession.Ended(); }, FluentGpu.Hooks.DepKey.Empty);
        if (t.IsNone) return new BoxEl { Key = "mix:page:none", Grow = 1f };
        return new BoxEl
        {
            Key = "mix:page", Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new MixEditorHost(t.Playlist, t.Index, expanded: true))
                    with { Key = "mix:ed:" + t.Playlist.ToString(CultureInfo.InvariantCulture) + ":" + t.Index.ToString(CultureInfo.InvariantCulture) },
            ],
        };
    }
}
