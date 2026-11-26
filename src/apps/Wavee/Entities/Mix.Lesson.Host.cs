// ── Entities/Mix.Lesson.Host.cs — #48 Mix, the lesson's page and its editor: the core hosts that hand each theme its props ───────
//
//   MixLessonPage     the `learn-mix` route: the guide's props (the hero, the steps, the open step's body) for ViewKeys.MixLesson, with
//                     the editor placed as an element; the take-apart's beat and the playhead's ticker; the sound released on leaving
//   MixLessonEditor   the editor in its teaching mode: MixEditorProps with `Teach`, the timeline's layer view (what you hear, the five
//                     layer lanes with their handles and targets, the ruler, the cut, the playhead) for ViewKeys.MixEditor — the theme's
//                     own Edit transition template. It re-renders on a value (a drag) without the guide; the guide's live value is bound.

using System.Collections.Immutable;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

[CoreHost]
internal sealed class MixLessonPage : Component
{
    Element? _editor;

    public override Element Render()
    {
        UseEffect(() =>
        {
            MixLesson.Mounted();
            MixLesson.Prefetch();
            return static () => MixLesson.Closed();
        }, DepKey.Empty);
        int step = MixLesson.Step.Value;
        _ = MixLesson.Epoch.Value;
        string playing = MixLesson.Playing.Value;
        UseInterval(static () => MixLesson.PeelStep(), MixLessonRules.PeelStepMs, enabled: MixLesson.Peeling);
        UseInterval(static () => MixLesson.Tick(), MixLessonRules.TickMs, enabled: playing.Length > 0 || MixLesson.JustHit >= 0);
        _editor ??= Embed.Comp(static () => new MixLessonEditor()) with { Key = "mix:lesson:editor" };
        return Slots.View(ViewKeys.MixLesson, Props(step, playing, _editor));
    }

    static readonly MixLessonStep[] Listed =
        [MixLessonStep.Cut, MixLessonStep.Volume, MixLessonStep.Bass, MixLessonStep.Filter, MixLessonStep.Echo, MixLessonStep.Tempo, MixLessonStep.Final];

    static MixLessonProps Props(int step, string playing, Element editor)
    {
        var steps = ImmutableArray.CreateBuilder<MixLessonStepModel>(Listed.Length);
        foreach (var s in Listed)
        {
            int i = (int)s;
            int layer = MixLesson.LayerOf(s);
            bool current = i == step, done = MixLesson.StepDone(i);
            bool filled = !done && layer >= 0 && MixLesson.IsFilled((MixLessonLayer)layer) && !current;
            var state = current ? StepState.Current : done ? StepState.Done : filled ? StepState.Skipped : StepState.Upcoming;
            string marker = s == MixLessonStep.Cut ? Icons.Cut : s == MixLessonStep.Final ? Icons.Flag : (layer + 1).ToString(CultureInfo.CurrentCulture);
            steps.Add(new MixLessonStepModel(Key(s), Title(s), filled ? Loc.Get(Strings.Mix.Lesson.SetForYou) : Sub(s), state, marker,
                MarkerIsGlyph: layer < 0, Passed: i < step, Hue: layer >= 0 ? MixLessonRules.Hue((MixLessonLayer)layer) : 0));
        }
        return new MixLessonProps
        {
            Editor = editor,
            Eyebrow = Loc.Get(Strings.Mix.Lesson.Eyebrow), Title = Loc.Get(Strings.Mix.Lesson.Title), Lead = Loc.Get(Strings.Mix.Lesson.Lead),
            ListenLabel = Loc.Get(playing == "listen-first" ? Strings.Mix.Lesson.Stop : Strings.Mix.Lesson.Listen), ListenPlaying = playing == "listen-first",
            Listen = static () => MixLesson.Press("listen-first"),
            ShowMeLabel = Loc.Get(Strings.Mix.Lesson.ShowMe), ShowMe = static () => MixLesson.Go((int)MixLessonStep.Cut),
            SoundNote = Loc.Get(Strings.Mix.Lesson.SoundNote), AtIntro = step == 0,
            StepsTitle = Loc.Get(Strings.Mix.Lesson.StepsTitle),
            StepsCount = step == 0 ? Strings.Mix.Lesson.StepsCount(Num(MixLesson.ListedSteps))
                : Strings.Mix.Lesson.StepOf(Num(step), Num(MixLesson.ListedSteps)),
            Progress = step / (float)MixLesson.ListedSteps,
            Steps = steps.ToImmutable(), Open = step - 1, Body = step == 0 ? null : Body((MixLessonStep)step, playing),
            Select = static i => MixLesson.Go(i + 1), Close = Shell.GoBack, CloseLabel = Loc.Get(Strings.Mix.Lesson.Close),
            TargetHue = MixLessonRules.HueTarget,
        };
    }

    static string Num(int n) => n.ToString(CultureInfo.CurrentCulture);

    static string Key(MixLessonStep s) => s switch
    {
        MixLessonStep.Cut => "cut", MixLessonStep.Final => "final", _ => MixLesson.Id((MixLessonLayer)MixLesson.LayerOf(s)),
    };

    internal static string Title(MixLessonStep s) => Loc.Get(s switch
    {
        MixLessonStep.Cut => Strings.Mix.Lesson.Step.Cut.Title, MixLessonStep.Volume => Strings.Mix.Lesson.Step.Vol.Title,
        MixLessonStep.Bass => Strings.Mix.Lesson.Step.Eq.Title, MixLessonStep.Filter => Strings.Mix.Lesson.Step.Filter.Title,
        MixLessonStep.Echo => Strings.Mix.Lesson.Step.Echo.Title, MixLessonStep.Tempo => Strings.Mix.Lesson.Step.Tempo.Title,
        _ => Strings.Mix.Lesson.Step.Final.Title,
    });

    static string Sub(MixLessonStep s) => Loc.Get(s switch
    {
        MixLessonStep.Cut => Strings.Mix.Lesson.Step.Cut.Sub, MixLessonStep.Volume => Strings.Mix.Lesson.Step.Vol.Sub,
        MixLessonStep.Bass => Strings.Mix.Lesson.Step.Eq.Sub, MixLessonStep.Filter => Strings.Mix.Lesson.Step.Filter.Sub,
        MixLessonStep.Echo => Strings.Mix.Lesson.Step.Echo.Sub, MixLessonStep.Tempo => Strings.Mix.Lesson.Step.Tempo.Sub,
        _ => Strings.Mix.Lesson.Step.Final.Sub,
    });

    /// <summary>A layer step's words: (concept, concept, task, alone, alone-for, together, together-for, done). Volume's "alone" is its one listen.</summary>
    static (string Say1, string Say2, string Task, string Alone, string AloneFor, string Together, string TogetherFor, string Done) Words(MixLessonLayer l) => l switch
    {
        MixLessonLayer.Volume => (Strings.Mix.Lesson.Step.Vol.Say1, Strings.Mix.Lesson.Step.Vol.Say2, Strings.Mix.Lesson.Step.Vol.Task,
            Strings.Mix.Lesson.Step.Vol.Listen, Strings.Mix.Lesson.Step.Vol.ListenFor, "", "", Strings.Mix.Lesson.Step.Vol.Done),
        MixLessonLayer.Bass => (Strings.Mix.Lesson.Step.Eq.Say1, Strings.Mix.Lesson.Step.Eq.Say2, Strings.Mix.Lesson.Step.Eq.Task,
            Strings.Mix.Lesson.Step.Eq.Alone, Strings.Mix.Lesson.Step.Eq.AloneFor, Strings.Mix.Lesson.Step.Eq.Together, Strings.Mix.Lesson.Step.Eq.TogetherFor,
            Strings.Mix.Lesson.Step.Eq.Done),
        MixLessonLayer.Filter => (Strings.Mix.Lesson.Step.Filter.Say1, Strings.Mix.Lesson.Step.Filter.Say2, Strings.Mix.Lesson.Step.Filter.Task,
            Strings.Mix.Lesson.Step.Filter.Alone, Strings.Mix.Lesson.Step.Filter.AloneFor, Strings.Mix.Lesson.Step.Filter.Together,
            Strings.Mix.Lesson.Step.Filter.TogetherFor, Strings.Mix.Lesson.Step.Filter.Done),
        MixLessonLayer.Echo => (Strings.Mix.Lesson.Step.Echo.Say1, Strings.Mix.Lesson.Step.Echo.Say2, Strings.Mix.Lesson.Step.Echo.Task,
            Strings.Mix.Lesson.Step.Echo.Alone, Strings.Mix.Lesson.Step.Echo.AloneFor, Strings.Mix.Lesson.Step.Echo.Together,
            Strings.Mix.Lesson.Step.Echo.TogetherFor, Strings.Mix.Lesson.Step.Echo.Done),
        _ => (Strings.Mix.Lesson.Step.Tempo.Say1, Strings.Mix.Lesson.Step.Tempo.Say2, Strings.Mix.Lesson.Step.Tempo.Task,
            Strings.Mix.Lesson.Step.Tempo.Alone, Strings.Mix.Lesson.Step.Tempo.AloneFor, Strings.Mix.Lesson.Step.Tempo.Together,
            Strings.Mix.Lesson.Step.Tempo.TogetherFor, Strings.Mix.Lesson.Step.Tempo.Done),
    };

    static (string Without, string Seg, string Hint) CompareWords(MixLessonLayer? l) => l switch
    {
        MixLessonLayer.Volume => (Strings.Mix.Lesson.Compare.Vol.Without, Strings.Mix.Lesson.Compare.Vol.Seg, Strings.Mix.Lesson.Compare.Vol.Hint),
        MixLessonLayer.Bass => (Strings.Mix.Lesson.Compare.Eq.Without, Strings.Mix.Lesson.Compare.Eq.Seg, Strings.Mix.Lesson.Compare.Eq.Hint),
        MixLessonLayer.Filter => (Strings.Mix.Lesson.Compare.Filter.Without, Strings.Mix.Lesson.Compare.Filter.Seg, Strings.Mix.Lesson.Compare.Filter.Hint),
        MixLessonLayer.Echo => (Strings.Mix.Lesson.Compare.Echo.Without, Strings.Mix.Lesson.Compare.Echo.Seg, Strings.Mix.Lesson.Compare.Echo.Hint),
        MixLessonLayer.Tempo => (Strings.Mix.Lesson.Compare.Tempo.Without, Strings.Mix.Lesson.Compare.Tempo.Seg, Strings.Mix.Lesson.Compare.Tempo.Hint),
        _ => (Strings.Mix.Lesson.Compare.Final.Without, Strings.Mix.Lesson.Compare.Final.Seg, Strings.Mix.Lesson.Compare.Final.Hint),
    };

    static MixLessonTask Listen(string act, string label, string hint, MixLessonTaskState state, string playing)
        => new("mix:lesson:task:" + act, MixLessonTaskKind.Listen, playing == act ? MixLessonTaskState.Playing : state,
            playing == act ? Loc.Get(Strings.Mix.Lesson.Stop) : Loc.Get(label), Loc.Get(hint), null, () => MixLesson.Press(act));

    static MixLessonCompare Compare(MixLessonLayer? l, bool enabled, string playing)
    {
        var (without, seg, hint) = CompareWords(l);
        var cmp = MixLesson.Comparing;
        return new MixLessonCompare(Loc.Get(Strings.Mix.Lesson.Compare.Title), Loc.Get(hint) + " " + Loc.Get(Strings.Mix.Lesson.Compare.Flip),
            Loc.Get(Strings.Mix.Lesson.Compare.Original), Loc.Get(seg), enabled, playing == "cmp",
            cmp == MixLesson.Compare.Original ? 0 : cmp == MixLesson.Compare.With ? 1 : -1,
            static () => MixLesson.CompareTo(MixLesson.Compare.None),
            static i => MixLesson.CompareTo(i == 0 ? MixLesson.Compare.Original : MixLesson.Compare.With),
            Loc.Get(Strings.Mix.Lesson.Compare.Play), Loc.Get(Strings.Mix.Lesson.Compare.Stop));
    }

    static MixLessonBody Body(MixLessonStep s, string playing)
    {
        int i = (int)s;
        bool done = MixLesson.StepDone(i);
        bool last = s == MixLessonStep.Final;
        string next = last ? "" : Strings.Mix.Lesson.Next(Title((MixLessonStep)(i + 1)));
        Action? goNext = last ? null : () => MixLesson.Go(i + 1);
        string nextHint = done ? "" : Loc.Get(Strings.Mix.Lesson.NextHint);
        var empty = ImmutableArray<(int, uint, string, string)>.Empty;
        if (s == MixLessonStep.Cut)
        {
            var tasks = ImmutableArray.Create(Listen("cut", Strings.Mix.Lesson.Step.Cut.Listen, Strings.Mix.Lesson.Step.Cut.ListenFor,
                MixLesson.Heard("cut") ? MixLessonTaskState.Done : MixLessonTaskState.Now, playing));
            return new MixLessonBody([Loc.Get(Strings.Mix.Lesson.Step.Cut.Say)], tasks, null, done ? Loc.Get(Strings.Mix.Lesson.Step.Cut.Done) : "",
                next, done, nextHint, goNext, empty, "", "", null);
        }
        if (last)
        {
            var recap = ImmutableArray.CreateBuilder<(int, uint, string, string)>(MixLesson.LayerCount);
            for (int l = 0; l < MixLesson.LayerCount; l++)
            {
                var layer = (MixLessonLayer)l;
                string job = Loc.Get(layer switch
                {
                    MixLessonLayer.Volume => Strings.Mix.Lesson.Step.Final.Vol, MixLessonLayer.Bass => Strings.Mix.Lesson.Step.Final.Eq,
                    MixLessonLayer.Filter => Strings.Mix.Lesson.Step.Final.Filter, MixLessonLayer.Echo => Strings.Mix.Lesson.Step.Final.Echo,
                    _ => Strings.Mix.Lesson.Step.Final.Tempo,
                });
                recap.Add((l + 1, MixLessonRules.Hue(layer), MixLesson.LayerName(layer), job));
            }
            var tasks = ImmutableArray.Create(Listen("final", Strings.Mix.Lesson.Step.Final.Play, Strings.Mix.Lesson.Step.Final.PlayFor,
                MixLesson.Heard("final") ? MixLessonTaskState.Done : MixLessonTaskState.Now, playing));
            return new MixLessonBody([Loc.Get(Strings.Mix.Lesson.Step.Final.Say)], tasks, Compare(null, true, playing), "", "", false, "", null,
                recap.ToImmutable(), Loc.Get(Strings.Mix.Lesson.Step.Final.Note), Loc.Get(Strings.Mix.Lesson.Restart), MixLesson.Restart);
        }
        var l2 = (MixLessonLayer)MixLesson.LayerOf(s);
        var w = Words(l2);
        bool hit = MixLesson.IsHit(l2);
        var list = ImmutableArray.CreateBuilder<MixLessonTask>(3);
        list.Add(new MixLessonTask("mix:lesson:task:" + MixLesson.Id(l2) + ":drag", MixLessonTaskKind.Drag,
            hit ? MixLessonTaskState.Done : MixLessonTaskState.Now, Loc.Get(w.Task), "",
            () => MixLesson.TaskValue(l2, MixLesson.Values.Value), null));
        if (l2 == MixLessonLayer.Volume)
        {
            string act = MixLesson.Act(l2, "listen");
            list.Add(Listen(act, w.Alone, w.AloneFor, MixLesson.Heard(act) ? MixLessonTaskState.Done : hit ? MixLessonTaskState.Now : MixLessonTaskState.Wait, playing));
        }
        else
        {
            string alone = MixLesson.Act(l2, "alone"), together = MixLesson.Act(l2, "together");
            bool heardAlone = MixLesson.Heard(alone);
            list.Add(Listen(alone, w.Alone, w.AloneFor, heardAlone ? MixLessonTaskState.Done : hit ? MixLessonTaskState.Now : MixLessonTaskState.Wait, playing));
            list.Add(Listen(together, w.Together, w.TogetherFor,
                MixLesson.Heard(together) ? MixLessonTaskState.Done : heardAlone ? MixLessonTaskState.Now : MixLessonTaskState.Wait, playing));
        }
        return new MixLessonBody([Loc.Get(w.Say1), Loc.Get(w.Say2)], list.ToImmutable(), Compare(l2, hit, playing), done ? Loc.Get(w.Done) : "",
            next, done, nextHint, goNext, empty, "", "", null);
    }
}

/// <summary>The lesson's editor: the theme's Edit transition in its teaching mode, from the lesson's view.</summary>
[CoreHost]
internal sealed class MixLessonEditor : Component
{
    (string Key, MixHeardLevels Heard, uint[] A, bool[] DownA, uint[] B, bool[] DownB)? _picture;
    static readonly Action<long> NoMs = static _ => { };
    static readonly Func<long, long> Same = static ms => ms;

    public override Element Render()
    {
        var view = MixLesson.View();
        int step = MixLesson.Step.Value;
        string playing = MixLesson.Playing.Value;
        int justHit = MixLesson.JustHit;
        string key = view.TakeKey;
        if (_picture is not { } pic || pic.Key != key)
        {
            var (a, da, b, db) = MixLessonPicture.Beats(in view);
            _picture = pic = (key, MixLessonPicture.Heard(in view), a, da, b, db);
        }
        int focusStep = MixLesson.LayerOf((MixLessonStep)step);
        var layers = ImmutableArray.CreateBuilder<MixLayerLane>(MixLesson.LayerCount);
        for (int i = 0; i < MixLesson.LayerCount; i++)
        {
            var l = (MixLessonLayer)i;
            bool on = view.Shows(l);
            bool active = focusStep == i && on && !MixLesson.Peeling;
            MixLayerHandle? handle = null;
            MixLayerTarget? target = null;
            if (active)
            {
                var (ms, y, axis) = MixLessonPicture.HandleAt(l, view.Values);
                handle = new MixLayerHandle(ms, y, axis, HandleName(l),
                    (pms, py) => MixLesson.Set(l, MixLessonPicture.FromPoint(l, MixLesson.Values.Peek(), pms, py)),
                    (dir, big) => MixLesson.Set(l, MixLessonPicture.Stepped(l, MixLesson.Values.Peek(), dir, big)),
                    Nudge: !MixLesson.IsTouched(l));
                bool hit = MixLesson.IsHit(l);
                if (!hit || justHit == i)
                {
                    var (tms, ty, _) = MixLessonPicture.HandleAt(l, MixLesson.Auto);
                    target = new MixLayerTarget(tms, ty, Loc.Get(hit ? Strings.Mix.Lesson.Done : Strings.Mix.Lesson.DragHere), hit, MixLessonRules.HueTarget);
                }
            }
            bool dim = view.Dims(l) || view.Focus >= 0 && !on;
            layers.Add(new MixLayerLane(MixLesson.Id(l), i + 1, MixLesson.LayerName(l), MixLesson.Readout(l, in view), MixLessonRules.Hue(l), on,
                view.Focus == i, dim, MixLessonPicture.Marks(l, in view), handle, target, MixLessonRules.Share(l)));
        }
        var ruler = ImmutableArray.CreateBuilder<MixRulerMark>(8);
        for (int bar = 4; bar <= 11; bar++)
            ruler.Add(new MixRulerMark((long)Math.Round((bar - 1) * MixLesson.Bar * 1000),
                bar == 4 ? Strings.Mix.Lesson.Bar(bar.ToString(CultureInfo.CurrentCulture)) : bar.ToString(CultureInfo.CurrentCulture)));
        var timeline = new MixTimelineModel
        {
            LevelsA = [], LevelsB = [], StepMs = MixLessonRules.LevelStepMs, BeatsA = pic.A, DownA = pic.DownA, BeatsB = pic.B, DownB = pic.DownB,
            DurationA = (long)(MixLesson.Ov1 * 1000), DurationB = (long)(MixLesson.V1 * 1000), OutStartMs = (long)(MixLesson.Ov0 * 1000),
            OverlapMs = (long)((MixLesson.Ov1 - MixLesson.Ov0) * 1000), InStartMs = 0, InWindowMs = 0, SpeedB = 1f, VolumeOut = [], VolumeIn = [],
            Editable = false, None = false, LabelA = Loc.Get(Strings.Mix.Lesson.SongA), LabelB = Loc.Get(Strings.Mix.Lesson.SongB),
            SnapOut = Same, SnapIn = Same, SnapOverlap = Same, Preview = static _ => throw new InvalidOperationException("The lesson's timeline has no band."),
            EndPreview = static () => { }, CommitOut = NoMs, CommitIn = NoMs, CommitOverlap = NoMs, CommitBend = static (_, _) => { }, BendOut = 0f, BendIn = 0f,
            Window = new MixTimelineWindow((long)(MixLesson.V0 * 1000), (long)(MixLesson.V1 * 1000)), Heard = pic.Heard, Layers = layers.ToImmutable(),
            Ruler = ruler.ToImmutable(), CutAtMs = view.Volume == MixLessonVolume.Cut ? (long)(MixLesson.Mid * 1000) : -1,
            CutLabel = Loc.Get(Strings.Mix.Lesson.HardCut), PlayheadMs = MixLesson.Playhead,
            HeardCaption = Loc.Get(Strings.Mix.Lesson.HeardCaption), LayersCaption = Loc.Get(Strings.Mix.Lesson.LayersCaption),
            FactsA = Loc.Get(Strings.Mix.Lesson.FactsA), FactsB = Loc.Get(Strings.Mix.Lesson.FactsB),
        };
        string songA = Loc.Get(Strings.Mix.Lesson.SongA), songB = Loc.Get(Strings.Mix.Lesson.SongB);
        var teach = new MixTeachModel(songA, songB, MixLessonRules.HueA, MixLessonRules.HueB, view.Status,
            view.Focus >= 0 ? MixLessonRules.Hue((MixLessonLayer)view.Focus) : 0, playing.Length > 0);
        return Slots.View(ViewKeys.MixEditor, MixEditorTeach.Props(timeline, teach));
    }

    static string HandleName(MixLessonLayer l) => Loc.Get(l switch
    {
        MixLessonLayer.Volume => Strings.Mix.Lesson.Handle.Vol, MixLessonLayer.Bass => Strings.Mix.Lesson.Handle.Eq,
        MixLessonLayer.Filter => Strings.Mix.Lesson.Handle.Filter, MixLessonLayer.Echo => Strings.Mix.Lesson.Handle.Echo, _ => Strings.Mix.Lesson.Handle.Tempo,
    });
}

/// <summary>The editor's props in its teaching mode: the head's title and nothing to edit beyond the timeline's layer lanes.</summary>
internal static class MixEditorTeach
{
    static readonly Action Nothing = static () => { };
    static readonly Action<int> NoPick = static _ => { };
    static readonly Action<bool> NoFlag = static _ => { };

    public static MixEditorProps Props(MixTimelineModel timeline, MixTeachModel teach)
    {
        var none = new MixTrackFacts("", "", null, "", "", false);
        return new MixEditorProps
        {
            Title = Loc.Get(Strings.Mix.Editor.Title), Expanded = true, ToggleExpand = Nothing, ExpandLabel = "", Close = Nothing, CloseLabel = "",
            A = none, B = none, Compat = "", PresetsLabel = "", Presets = [], MorePresets = [], MoreLabel = "", MoreOn = false, PickPreset = NoPick,
            State = "", Sentence = "", Timeline = timeline, Readout = static () => default, Length = new MixLengthModel("", false, false, Nothing, Nothing),
            Ingredients = [], BeatmatchLabel = "", Beatmatched = false, BeatmatchAvailable = false, BeatmatchNote = "", SetBeatmatch = NoFlag,
            ClearCustom = Nothing, ClearCustomLabel = "", Custom = false, CanUndo = false, CanRedo = false, Undo = Nothing, Redo = Nothing,
            PreviewLabel = "", PreviewAvailable = false, Previewing = false, TogglePreview = Nothing, SaveLine = "", Saving = false, ShowRetry = false,
            Retry = Nothing, Dirty = false, CanReset = false, Reset = Nothing, Save = Nothing, UndoLabel = "", RedoLabel = "", ResetLabel = "",
            SaveLabel = "", RetryLabel = "", Teach = teach,
        };
    }
}
