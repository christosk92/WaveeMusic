// ── Entities/Mix.Lesson.cs — #48 Mix, "Build a transition together": the lesson's model, its steps and its verbs ────────────────
//
// An in-app lesson on the Mix editor (the owner's approved concept, 2026-11-26: lesson-v3/build-a-transition.html). It opens on a
// finished transition with five layers moving at once ("Let's build this transition together", Listen first), takes it apart layer by
// layer down to a hard cut, and builds it back one layer at a time; at every step the learner DOES the thing in the editor:
//
//   intro   the finished transition, Listen first                          the editor: every layer on, Auto's values
//   cut     "Take it apart": the layers peel off, last first, to a hard cut  hear the jolt on bar 7
//   1-5     Volume · Bass · Filter · Echo · Tempo                          drag the layer's one handle onto its target; hear it on its own,
//                                                                          then with everything; "Hear the original" (Original | With …)
//   final   "All together": the recap, play it, Original (a hard cut) | Your transition
//
// THE EDITOR IS THE APP'S. The lesson mounts the theme's own Edit transition template (ViewKeys.MixEditor) in its teaching mode
// (`MixEditorProps.Teach`): the same head, the same timeline (Themes/Common/CommonMixTimeline.cs) with its layer lanes, one per real
// ingredient row (Volume, EQ as Bass, Filter, Effects as Echo, Beatmatching as Tempo; Looping is left out of the lesson), each with its
// one handle. The guide (each theme's ViewKeys.MixLesson) holds every word and every button; the editor carries none.
//
// THE AUDIO is a built-in demo pair, always (Night Drive, 124 BPM, A minor → Sunrise, 128 BPM, C major), made on this PC for the lesson
// (Mix.Lesson.Audio.cs) and rendered through the engine's own mixer and voice chain (gain envelopes, the 3-band EQ's bass kill, the sweep
// filter, the echo, the beatmatch rate plan: the stages the pump's MixRender feeds) into takes a ClipSwitchPlayer plays and switches at
// the playhead. Why not the pair the learner opened: the lesson's targets, its copy and its "hear just the bass" need a pair made to show
// each layer (a real pair may not beatmatch, may bury the echo, has its own bars), it must work offline and without a Spotify stream, and
// it never touches the queue (the music pauses while the lesson plays and resumes when it closes).
//
// Every number here is the concept's: the view runs bar 4 to just past bar 11 of Night Drive's grid; both songs play from bar 5 to bar 9;
// bar 7 is the middle.

using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The lesson's five layers, each one row of the real editor.</summary>
public enum MixLessonLayer : byte { Volume, Bass, Filter, Echo, Tempo }

/// <summary>The lesson's steps: the intro, then the seven the guide lists.</summary>
public enum MixLessonStep : byte { Intro, Cut, Volume, Bass, Filter, Echo, Tempo, Final }

/// <summary>How the volume layer plays in a view: the fade, a hard cut, both songs flat (a solo of another layer), Night Drive only.</summary>
public enum MixLessonVolume : byte { Fade, Cut, Flat, AOnly }

/// <summary>The lesson's five values (the handles): where the fade starts (s), where the bass swaps (s), how far the filter closes (Hz), the
/// echo's repeats, Sunrise's tempo while both play.</summary>
public readonly record struct MixLessonValues(double FadeStart, double SwapAt, double Cutoff, int Repeats, double Bpm);

/// <summary>What is on right now: one description that drives the sound, the picture, the editor's status and the guide.</summary>
/// <param name="On">The layers that play (a bit per <see cref="MixLessonLayer"/>).</param>
/// <param name="Volume">How the volume layer plays.</param>
/// <param name="LowsOnly">Only the lows play (a solo of the bass or the beat).</param>
/// <param name="Values">The values in play (Auto's, or the learner's).</param>
/// <param name="Focus">The layer the step is about (−1: none).</param>
/// <param name="Shown">The layers drawn as on (a solo still draws the layers before it).</param>
/// <param name="Dim">The layers drawn dimmed.</param>
public readonly record struct MixLessonView(byte On, MixLessonVolume Volume, bool LowsOnly, MixLessonValues Values, int Focus, byte Shown, byte Dim,
    string Status)
{
    public bool Has(MixLessonLayer l) => (On & (1 << (int)l)) != 0;
    public bool Shows(MixLessonLayer l) => (Shown & (1 << (int)l)) != 0;
    public bool Dims(MixLessonLayer l) => (Dim & (1 << (int)l)) != 0;
    /// <summary>The key of the take this view plays (what the audio depends on).</summary>
    public string TakeKey => On.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)Volume + "|" + (LowsOnly ? 1 : 0) + "|" + Values;
}

/// <summary>The lesson (see the file header): its time, its steps, its state and its verbs. UI thread.</summary>
public static class MixLesson
{
    // ── musical time, on Night Drive's grid ──────────────────────────────────────────────────────────────────────────────

    public const double BpmA = 124, BpmB = 128;
    public const double Beat = 60.0 / BpmA, Bar = 4 * Beat;
    /// <summary>Both songs play from bar 5 (Ov0) to bar 9 (Ov1); bar 7 is the middle; Sunrise glides back to its own tempo over 2 bars.</summary>
    public const double Ov0 = 4 * Bar, Ov1 = 8 * Bar, Mid = 6 * Bar, Glide = 2 * Bar;
    /// <summary>What the editor shows: bar 4 to just past bar 11.</summary>
    public const double V0 = 3 * Bar, V1 = 10 * Bar + 0.3;
    /// <summary>The echo: a ¾-beat delay catching Night Drive's last beat.</summary>
    public const double Delay = 0.75 * Beat, EchoAt = Ov1 - Beat;
    public const double FilterHigh = 16_000, FilterLow = 200;

    public static readonly MixLessonValues Auto = new(Ov0, Mid, 500, 6, 124);
    public static readonly MixLessonValues Start = new(Mid, Ov1, 6000, 1, 128);

    public const int LayerCount = 5;
    public const byte AllLayers = 0b11111;
    static byte Bit(MixLessonLayer l) => (byte)(1 << (int)l);
    /// <summary>The layers up to and including <paramref name="l"/>.</summary>
    public static byte UpTo(MixLessonLayer l) => (byte)((1 << ((int)l + 1)) - 1);

    /// <summary>The bar number of a time on Night Drive's grid (bar 1 at 0 s).</summary>
    public static double BarOf(double t) => t / Bar + 1;

    /// <summary>Has the learner reached the layer's target?</summary>
    public static bool Hit(MixLessonLayer l, in MixLessonValues v) => l switch
    {
        MixLessonLayer.Volume => v.FadeStart <= Ov0 + 1e-3,
        MixLessonLayer.Bass => Math.Abs(v.SwapAt - Mid) < 1e-3,
        MixLessonLayer.Filter => v.Cutoff >= 400 && v.Cutoff <= 640,
        MixLessonLayer.Echo => v.Repeats == 6,
        _ => Math.Abs(v.Bpm - 124) < 1e-3,
    };

    /// <summary>The layer a step is about (−1 for the intro, the cut and the final step).</summary>
    public static int LayerOf(MixLessonStep s) => s is >= MixLessonStep.Volume and <= MixLessonStep.Tempo ? (int)s - (int)MixLessonStep.Volume : -1;

    public const int StepCount = 8;
    /// <summary>The steps the guide lists (the intro is the hero above them).</summary>
    public const int ListedSteps = 7;

    // ── the state ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A layer's listening task: on its own, then with everything.</summary>
    public enum Phase : byte { Alone, Together }

    /// <summary>The A/B comparison: none, the original (without the step's layer), or with it.</summary>
    public enum Compare : byte { None, Original, With }

    /// <summary>The step open now (tracked).</summary>
    public static readonly Signal<int> Step = new(0) { DebugName = "mix.lesson.step" };
    /// <summary>The learner's values (tracked: the editor redraws on every snapped change).</summary>
    public static readonly Signal<MixLessonValues> Values = new(Start) { DebugName = "mix.lesson.values" };
    /// <summary>Bumped on every change the guide shows (a target hit, a task heard, a phase, a comparison, a peel step).</summary>
    public static readonly Signal<int> Epoch = new(0) { DebugName = "mix.lesson.epoch" };
    /// <summary>What plays ("" when nothing): an act id ("listen-first", "cut", "eq:alone", "cmp", …).</summary>
    public static readonly Signal<string> Playing = new("") { DebugName = "mix.lesson.playing" };
    /// <summary>The playhead in ms on the lesson's timeline (NaN when nothing plays); written by the page's ticker while playing.</summary>
    public static readonly FloatSignal Playhead = new(float.NaN);

    static readonly Phase[] s_phase = new Phase[LayerCount];
    static readonly bool[] s_hit = new bool[LayerCount], s_filled = new bool[LayerCount], s_touched = new bool[LayerCount];
    static readonly HashSet<string> s_heard = new(StringComparer.Ordinal);
    static Compare s_cmp;
    /// <summary>The take-apart in progress: the layers still on (−1: not peeling).</summary>
    static int s_peel = -1;
    /// <summary>When the last target was hit (the marker shows "Done" for a moment): the step and the layer.</summary>
    static int s_hitLayer = -1;
    static long s_hitAtMs;

    public static Phase PhaseOf(MixLessonLayer l) => s_phase[(int)l];
    public static bool IsHit(MixLessonLayer l) => s_hit[(int)l];
    public static bool IsFilled(MixLessonLayer l) => s_filled[(int)l];
    public static bool IsTouched(MixLessonLayer l) => s_touched[(int)l];
    public static bool Heard(string act) => s_heard.Contains(act);
    public static Compare Comparing => s_cmp;
    public static bool Peeling => s_peel >= 0;
    /// <summary>The layer whose target was hit in the last moment (its marker shows "Done"), or −1.</summary>
    public static int JustHit => s_hitLayer >= 0 && Playback.FrameNowMs() - s_hitAtMs < MixLessonRules.DoneHoldMs ? s_hitLayer : -1;

    static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    /// <summary>The step <paramref name="i"/> is finished: the cut heard; a layer's target hit and both its listens heard (Volume: its one).</summary>
    public static bool StepDone(int i)
    {
        var s = (MixLessonStep)i;
        if (s == MixLessonStep.Cut) return Heard("cut");
        int l = LayerOf(s);
        if (l < 0) return false;
        var layer = (MixLessonLayer)l;
        if (layer == MixLessonLayer.Volume) return s_hit[l] && Heard("vol:listen");
        return s_hit[l] && Heard(Act(layer, "alone")) && Heard(Act(layer, "together"));
    }

    /// <summary>An act's id: "eq:alone", "filter:together", "vol:listen".</summary>
    public static string Act(MixLessonLayer l, string part) => Id(l) + ":" + part;

    /// <summary>A layer's short id (keys, acts): vol, eq, filter, echo, tempo.</summary>
    public static string Id(MixLessonLayer l) => l switch
    {
        MixLessonLayer.Volume => "vol", MixLessonLayer.Bass => "eq", MixLessonLayer.Filter => "filter", MixLessonLayer.Echo => "echo", _ => "tempo",
    };

    // ── what is on ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The view now (reads the step, the values and the epoch: tracked).</summary>
    public static MixLessonView View()
    {
        int step = Step.Value;
        var values = Values.Value;
        _ = Epoch.Value;
        return ViewOf((MixLessonStep)step, in values, s_cmp);
    }

    /// <summary>The view of <paramref name="step"/> with <paramref name="values"/> and the comparison <paramref name="cmp"/>.</summary>
    public static MixLessonView ViewOf(MixLessonStep step, in MixLessonValues values, Compare cmp)
    {
        int focus = LayerOf(step);
        if (s_peel >= 0)
            return new((byte)s_peel, MixLessonVolume.Fade, false, Auto, -1, (byte)s_peel, 0,
                s_peel == AllLayers ? Loc.Get(Strings.Mix.Lesson.Status.TakingApart) : Strings.Mix.Lesson.Status.LayerOff(LayerName((MixLessonLayer)PeeledLast())));
        if (cmp != Compare.None && step == MixLessonStep.Final)
            return cmp == Compare.With
                ? new(AllLayers, MixLessonVolume.Fade, false, values, -1, AllLayers, 0, Loc.Get(Strings.Mix.Lesson.Compare.Final.With))
                : new(0, MixLessonVolume.Cut, false, values, -1, AllLayers, AllLayers, Loc.Get(Strings.Mix.Lesson.Compare.Final.Without));
        if (cmp != Compare.None && focus >= 0)
        {
            var l = (MixLessonLayer)focus;
            byte upto = UpTo(l);
            if (cmp == Compare.With) return new(upto, MixLessonVolume.Fade, false, values, focus, upto, 0, CompareWords(l, with: true));
            return new((byte)(upto & ~Bit(l)), l == MixLessonLayer.Volume ? MixLessonVolume.Cut : MixLessonVolume.Fade, false, values, focus, upto, Bit(l),
                CompareWords(l, with: false));
        }
        switch (step)
        {
            case MixLessonStep.Intro: return new(AllLayers, MixLessonVolume.Fade, false, Auto, -1, AllLayers, 0, Loc.Get(Strings.Mix.Lesson.Status.Finished));
            case MixLessonStep.Cut: return new(0, MixLessonVolume.Cut, false, values, -1, 0, 0, Loc.Get(Strings.Mix.Lesson.Status.HardCut));
            case MixLessonStep.Final: return new(AllLayers, MixLessonVolume.Fade, false, values, -1, AllLayers, 0, Loc.Get(Strings.Mix.Lesson.Status.Yours));
        }
        var layer = (MixLessonLayer)focus;
        byte up = UpTo(layer);
        if (layer == MixLessonLayer.Volume)
            return new(up, MixLessonVolume.Fade, false, values, focus, up, 0, Strings.Mix.Lesson.Status.Layer("1", LayerName(layer)));
        if (s_phase[focus] == Phase.Together)
            return new(up, MixLessonVolume.Fade, false, values, focus, up, 0, Strings.Mix.Lesson.Status.Together((focus + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)));
        // On its own: the others muted, the stretch that shows it best (Alone's window), the layers before it drawn dimmed.
        var vol = layer is MixLessonLayer.Filter or MixLessonLayer.Echo ? MixLessonVolume.AOnly : MixLessonVolume.Flat;
        bool lows = layer is MixLessonLayer.Bass or MixLessonLayer.Tempo;
        return new(Bit(layer), vol, lows, values, focus, up, (byte)(up & ~Bit(layer)), Strings.Mix.Lesson.Status.Alone(LayerName(layer)));
    }

    /// <summary>The layer the take-apart turned off last: it peels from the top, so the layers still on are 0..n−1 and the last off is n.</summary>
    static int PeeledLast() => Math.Min(LayerCount - 1, System.Numerics.BitOperations.PopCount((uint)s_peel));

    static string CompareWords(MixLessonLayer l, bool with) => Loc.Get(l switch
    {
        MixLessonLayer.Volume => with ? Strings.Mix.Lesson.Compare.Vol.With : Strings.Mix.Lesson.Compare.Vol.Without,
        MixLessonLayer.Bass => with ? Strings.Mix.Lesson.Compare.Eq.With : Strings.Mix.Lesson.Compare.Eq.Without,
        MixLessonLayer.Filter => with ? Strings.Mix.Lesson.Compare.Filter.With : Strings.Mix.Lesson.Compare.Filter.Without,
        MixLessonLayer.Echo => with ? Strings.Mix.Lesson.Compare.Echo.With : Strings.Mix.Lesson.Compare.Echo.Without,
        _ => with ? Strings.Mix.Lesson.Compare.Tempo.With : Strings.Mix.Lesson.Compare.Tempo.Without,
    });

    public static string LayerName(MixLessonLayer l) => Loc.Get(l switch
    {
        MixLessonLayer.Volume => Strings.Mix.Lesson.Layer.Volume,
        MixLessonLayer.Bass => Strings.Mix.Lesson.Layer.Bass,
        MixLessonLayer.Filter => Strings.Mix.Lesson.Layer.Filter,
        MixLessonLayer.Echo => Strings.Mix.Lesson.Layer.Echo,
        _ => Strings.Mix.Lesson.Layer.Tempo,
    });

    // ── the stretches each act plays ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stretch (s) an act plays: the whole view, the cut around bar 7, a solo's own window, a comparison's.</summary>
    public static (double From, double To) WindowOf(string act, MixLessonStep step)
    {
        if (act == "cmp") return CompareWindow(step);
        if (act is "listen-first" or "final" or "vol:listen" || act.EndsWith(":together", StringComparison.Ordinal)) return (V0, V1);
        if (act == "cut") return (4.5 * Bar, 8 * Bar + 0.4);
        return act.Split(':')[0] switch
        {
            "eq" => (4.25 * Bar, 8 * Bar + 0.3),
            "filter" => (4 * Bar - 0.6, Ov1 + 0.5),
            "echo" => (6.5 * Bar, V1),
            _ => (4 * Bar - 0.4, 8 * Bar),
        };
    }

    static (double, double) CompareWindow(MixLessonStep step) => step switch
    {
        MixLessonStep.Bass or MixLessonStep.Tempo => (4 * Bar - 0.4, 8 * Bar + 0.4),
        MixLessonStep.Filter => (4 * Bar - 0.4, Ov1 + 0.6),
        MixLessonStep.Echo => (6.5 * Bar, V1),
        _ => (V0, V1),
    };

    // ── the verbs ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open the lesson (the editor's "?", Settings › Help, the Mix tip).</summary>
    /// <summary>The lesson was opened this session (the Mix tip has done its job).</summary>
    public static bool EverOpened { get; private set; }

    public static void Open()
    {
        EverOpened = true;
        if (Shell.Current.Peek().Kind != Shell.RouteKind.LearnMix) Shell.GoTo(new Shell.Route(Shell.RouteKind.LearnMix));
        MixLessonAudio.Prepare();
    }

    /// <summary>The lesson page is on screen (however it was reached: a door, Back, a deep link): its sound is made ready.</summary>
    internal static void Mounted()
    {
        EverOpened = true;
        MixLessonAudio.Prepare();
    }

    /// <summary>The lesson page left the screen: what plays stops and the music comes back as it was.</summary>
    public static void Closed()
    {
        StopSound();
        MixLessonAudio.Release();
    }

    /// <summary>Go to step <paramref name="n"/> (a head's click, Next). Earlier layers the learner skipped take Auto's value (the mix still
    /// sounds right; their nodes read "Set for you"). From the intro into the cut, the finished transition is taken apart, last layer first.</summary>
    public static void Go(int n)
    {
        n = Math.Clamp(n, 0, StepCount - 1);
        StopSound();
        int from = Step.Peek();
        s_peel = -1;
        var s = (MixLessonStep)n;
        int focus = LayerOf(s);
        var v = Values.Peek();
        int fillUpTo = s == MixLessonStep.Final ? LayerCount : focus;
        for (int l = 0; l < fillUpTo; l++)
            if (!s_hit[l]) { v = WithAuto(v, (MixLessonLayer)l); s_filled[l] = true; }
        Values.Value = v;
        Step.Value = n;
        if (from == (int)MixLessonStep.Intro && s == MixLessonStep.Cut && !Design.Reduced) s_peel = AllLayers;
        Bump();
        Prefetch();
    }

    /// <summary>Render the open step's takes ahead (its listens, its comparison), so a press plays at once.</summary>
    internal static void Prefetch()
    {
        var step = (MixLessonStep)Step.Peek();
        var v = Values.Peek();
        int l = LayerOf(step);
        if (l >= 0)
        {
            var keep = s_phase[l];
            s_phase[l] = Phase.Alone;
            MixLessonAudio.Prefetch(ViewOf(step, in v, Compare.None));
            s_phase[l] = Phase.Together;
            MixLessonAudio.Prefetch(ViewOf(step, in v, Compare.None));
            s_phase[l] = keep;
        }
        else MixLessonAudio.Prefetch(ViewOf(step, in v, Compare.None));
        if (l >= 0 && s_hit[l] || step == MixLessonStep.Final)
        {
            MixLessonAudio.Prefetch(ViewOf(step, in v, Compare.With));
            MixLessonAudio.Prefetch(ViewOf(step, in v, Compare.Original));
        }
    }

    static MixLessonValues WithAuto(MixLessonValues v, MixLessonLayer l) => l switch
    {
        MixLessonLayer.Volume => v with { FadeStart = Auto.FadeStart },
        MixLessonLayer.Bass => v with { SwapAt = Auto.SwapAt },
        MixLessonLayer.Filter => v with { Cutoff = Auto.Cutoff },
        MixLessonLayer.Echo => v with { Repeats = Auto.Repeats },
        _ => v with { Bpm = Auto.Bpm },
    };

    /// <summary>The take-apart's next beat (the page's ticker calls it every <see cref="MixLessonRules.PeelStepMs"/>): one more layer off,
    /// the last one first. True while it goes on.</summary>
    public static bool PeelStep()
    {
        if (s_peel < 0) return false;
        for (int l = LayerCount - 1; l >= 0; l--)
            if ((s_peel & (1 << l)) != 0) { s_peel &= ~(1 << l); break; }
        if (s_peel == 0) s_peel = -1;
        Bump();
        return s_peel >= 0;
    }

    /// <summary>Start over: every value back to where the lesson starts, every task undone, the intro.</summary>
    public static void Restart()
    {
        StopSound();
        Array.Clear(s_phase);
        Array.Clear(s_hit);
        Array.Clear(s_filled);
        Array.Clear(s_touched);
        s_heard.Clear();
        s_cmp = Compare.None;
        s_peel = -1;
        Values.Value = Start;
        Step.Value = 0;
        Bump();
    }

    /// <summary>A handle moved (a drag or a key): the value snapped as the editor's own handles snap; a target reached marks the layer done.</summary>
    public static void Set(MixLessonLayer l, MixLessonValues v)
    {
        var now = Values.Peek();
        s_touched[(int)l] = true;
        if (v == now) return;
        Values.Value = v;
        if (!s_hit[(int)l] && LayerOf((MixLessonStep)Step.Peek()) == (int)l && Hit(l, in v))
        {
            s_hit[(int)l] = true;
            s_hitLayer = (int)l;
            s_hitAtMs = Playback.FrameNowMs();
            Bump();
            Prefetch();
        }
        MixLessonAudio.Edited();
    }

    /// <summary>A guide button: play its act (again: stop it). A layer's "on its own" and "with everything" switch the view first.</summary>
    public static void Press(string act)
    {
        if (Playing.Peek() == act) { StopSound(); return; }
        var step = (MixLessonStep)Step.Peek();
        if (act == "listen-first" && step != MixLessonStep.Intro) Go(0);
        int colon = act.IndexOf(':');
        if (colon > 0)
        {
            string part = act[(colon + 1)..];
            int l = LayerIndex(act[..colon]);
            if (l >= 0 && part == "alone") s_phase[l] = Phase.Alone;
            if (l >= 0 && part == "together") s_phase[l] = Phase.Together;
        }
        s_cmp = Compare.None;
        if (act != "listen-first") s_heard.Add(act);
        Bump();
        var (from, to) = WindowOf(act, (MixLessonStep)Step.Peek());
        MixLessonAudio.Play(act, ViewOf((MixLessonStep)Step.Peek(), Values.Peek(), Compare.None), default, from, to, loop: false);
    }

    /// <summary>"Hear the original": play (or flip to) the original or the version with the layer; a flip while playing switches at the
    /// playhead. <paramref name="which"/> None toggles play and stop.</summary>
    public static void CompareTo(Compare which)
    {
        var step = (MixLessonStep)Step.Peek();
        if (LayerOf(step) < 0 && step != MixLessonStep.Final) return;
        bool playing = Playing.Peek() == "cmp";
        if (which == Compare.None)
        {
            if (playing) { StopSound(); return; }
            which = s_cmp == Compare.None ? Compare.Original : s_cmp;
        }
        if (playing && which == s_cmp) return;
        s_cmp = which;
        Bump();
        var values = Values.Peek();
        var with = ViewOf(step, in values, Compare.With);
        var orig = ViewOf(step, in values, Compare.Original);
        if (playing) { MixLessonAudio.Flip(which == Compare.With); return; }
        var (from, to) = CompareWindow(step);
        MixLessonAudio.Play("cmp", which == Compare.With ? with : orig, which == Compare.With ? orig : with, from, to, loop: true);
    }

    /// <summary>Stop what plays; a comparison ends (the editor shows the step's own view again).</summary>
    public static void StopSound()
    {
        MixLessonAudio.Stop();
        if (s_cmp != Compare.None) { s_cmp = Compare.None; Bump(); }
    }

    /// <summary>The page's ticker: the playhead (ms), the end of a stretch that played once, the end of a reached target's "Done".</summary>
    internal static void Tick()
    {
        if (s_hitLayer >= 0 && Playback.FrameNowMs() - s_hitAtMs >= MixLessonRules.DoneHoldMs) { s_hitLayer = -1; Bump(); }
        if (Playing.Peek().Length == 0) return;
        if (!MixLessonAudio.Sounding)
        {
            if (MixLessonAudio.Pending) return;
            Playing.Value = "";
            Playhead.Value = float.NaN;
            if (s_cmp != Compare.None) { s_cmp = Compare.None; Bump(); }
            return;
        }
        Playhead.Value = (float)(MixLessonAudio.Position * 1000);
    }

    static int LayerIndex(string id) => id switch { "vol" => 0, "eq" => 1, "filter" => 2, "echo" => 3, "tempo" => 4, _ => -1 };

    // ── the copy ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Swap on bar 9 → 7": the live value of a layer's task, and its target.</summary>
    public static string TaskValue(MixLessonLayer l, in MixLessonValues v) => l switch
    {
        MixLessonLayer.Volume => Strings.Mix.Lesson.Value.Vol(Bars(v.FadeStart)),
        MixLessonLayer.Bass => Strings.Mix.Lesson.Value.Eq(Bars(v.SwapAt)),
        MixLessonLayer.Filter => Strings.Mix.Lesson.Value.Filter(Hz(v.Cutoff)),
        MixLessonLayer.Echo => Strings.Mix.Lesson.Value.Echo(v.Repeats.ToString(System.Globalization.CultureInfo.CurrentCulture)),
        _ => Strings.Mix.Lesson.Value.Tempo(Num(v.Bpm)),
    };

    /// <summary>A layer's readout in its lane ("Fade, 4 bars", "Swap on bar 7", "Closes to 500 Hz", "6 repeats", "Sunrise at 124").</summary>
    public static string Readout(MixLessonLayer l, in MixLessonView view)
    {
        var v = view.Values;
        bool on = view.Shows(l);
        switch (l)
        {
            case MixLessonLayer.Volume:
                if (!on) return Loc.Get(Strings.Mix.Lesson.Readout.HardCut);
                double w = (2 * Mid - 2 * v.FadeStart) / Bar;
                return w < 0.01 ? Loc.Get(Strings.Mix.Lesson.Readout.NoFade) : Strings.Mix.Lesson.Readout.Fade(Num(w));
            case MixLessonLayer.Bass:
                return on ? Strings.Mix.Lesson.Readout.Swap(Bars(v.SwapAt)) : Loc.Get(Strings.Mix.Lesson.Readout.BothBasses);
            case MixLessonLayer.Filter:
                return on ? Strings.Mix.Lesson.Readout.Closes(Hz(v.Cutoff)) : Loc.Get(Strings.Mix.Lesson.Readout.Open);
            case MixLessonLayer.Echo:
                if (!on) return Loc.Get(Strings.Mix.Lesson.Readout.Off);
                return v.Repeats == 1 ? Loc.Get(Strings.Mix.Lesson.Readout.Repeat) : Strings.Mix.Lesson.Readout.Repeats(v.Repeats.ToString(System.Globalization.CultureInfo.CurrentCulture));
            default:
                return Strings.Mix.Lesson.Readout.Tempo(Num(on ? v.Bpm : BpmB));
        }
    }

    static string Bars(double t) => Num(BarOf(t));
    static string Num(double v) => (Math.Round(v * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
    public static string Hz(double f) => f >= 1000
        ? Strings.Mix.Lesson.Khz((Math.Round(f / 100) / 10).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
        : Strings.Mix.Lesson.HzWord((Math.Round(f / 10) * 10).ToString("0", System.Globalization.CultureInfo.CurrentCulture));
}
