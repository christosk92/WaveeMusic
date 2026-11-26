// ── Entities/Mix.Lesson.Audio.cs — #48 Mix, the lesson's sound: the demo pair, its takes, the A/B player ──────────────────────
//
// THE PAIR. Two short songs made on this PC when the lesson opens (a worker, ~0.1 s): Night Drive (124 BPM, A minor: kick, off-beat
// hats, a sawtooth bassline, a pad, a two-note hook) and Sunrise (128 BPM, C major: kick, clap, sixteenth hats, a square bass, an arpeggio).
// They are the concept's own scores, made to show each layer: a bassline in each song (the swap), bright tops (the filter), a hook on
// Night Drive's last beat (the echo), two tempos four BPM apart (the beatmatch).
//
// A TAKE is one view of the lesson (which layers play, how the volume plays, lows only or not, the values) rendered through the ENGINE's
// mixer and voice chain (FluentGpu.Media.OfflineMixer): Night Drive under its gain envelope with its automation (the 3-band EQ killing
// its lows after the swap, the sweep low-pass closing over the overlap), its last beat sent pre-fader into the echo (a second voice of
// Night Drive gated to that half beat, the echo's wet only, feedback set by the repeats), Sunrise from bar 5 under its envelope with its
// lows coming in at the swap and, beatmatched, its rate plan (WSOLA at 124/128, gliding back to 1 over two bars after the overlap) — the
// stages the pump's MixRender feeds. A take is cached by its view; a render runs on a worker and lands on the UI thread.
//
// THE PLAYER is the engine's ClipSwitchPlayer on an aux output (the music's endpoint; the paced silent one headless): a stretch plays
// once or loops (a comparison), a flip switches takes at the playhead with a 50 ms crossfade, an edit while it plays re-renders and
// crossfades into the new take where it is. The music pauses while the lesson plays and comes back when the lesson closes.

using FluentGpu.Media;

namespace Wavee;

/// <summary>The lesson's sound (see the file header). UI thread unless noted.</summary>
public static class MixLessonAudio
{
    public const int Rate = 48_000, Channels = 2;
    const int TakeSlotWith = 0, TakeSlotOther = 1;
    const double Tail = 0.25;

    static float[]? s_a, s_b;
    static Task? s_making;
    static ClipSwitchPlayer? s_player;
    static AuxAudioOutput? s_output;
    static readonly Dictionary<string, Task<AudioClip>> s_jobs = new(StringComparer.Ordinal);
    static readonly Queue<string> s_order = new();
    static string s_act = "";
    /// <summary>The comparison started on the version with the layer (slot 0 holds it).</summary>
    static bool s_startedWith;
    static bool s_pausedMusic;
    static int s_request, s_pending;

    /// <summary>A play waits for its take (tracked by the ticker through <see cref="Sounding"/>).</summary>
    public static bool Pending => s_pending > 0;
    /// <summary>The player sounds (any thread).</summary>
    public static bool Sounding => s_player?.Playing ?? false;
    /// <summary>The playhead on the lesson's timeline (s).</summary>
    public static double Position => s_player?.PositionSeconds ?? 0;

    /// <summary>Make the pair (once) and open the output, off the UI thread.</summary>
    public static void Prepare()
    {
        s_making ??= Task.Run(static () =>
        {
            var a = MixLessonSongs.NightDrive(Rate);
            var b = MixLessonSongs.Sunrise(Rate);
            Volatile.Write(ref s_a, a);
            Volatile.Write(ref s_b, b);
        });
        if (s_player is not null) return;
        s_player = new ClipSwitchPlayer { CrossfadeMs = MixLessonRules.CompareCrossfadeMs };
        try
        {
            s_output = Playback.Audio.OpenLessonOutput(s_player);
            s_output?.Start();
        }
        catch (Exception e)
        {
            Log.Warn("mix", "[lesson] no output: " + e.Message);
            s_output = null;
        }
    }

    /// <summary>The lesson closed: stop, give the output back, put the music back as it was.</summary>
    public static void Release()
    {
        Stop();
        var output = s_output;
        s_output = null;
        s_player = null;
        if (output is not null) Task.Run(() => { try { output.Dispose(); } catch (Exception) { } });
        if (s_pausedMusic) { s_pausedMusic = false; Playback.Resume("mix-lesson"); }
    }

    /// <summary>Play <paramref name="view"/>'s take from <paramref name="from"/> to <paramref name="to"/> (s), looping or once. A comparison
    /// passes the other version as <paramref name="other"/> (rendered into the second slot, flipped to with <see cref="Flip"/>).</summary>
    public static void Play(string act, MixLessonView view, MixLessonView other, double from, double to, bool loop)
    {
        Prepare();
        if (Playback.IsPlaying.Peek()) { Playback.Pause("mix-lesson"); s_pausedMusic = true; }
        s_act = act;
        s_startedWith = act == "cmp" && MixLesson.Comparing == MixLesson.Compare.With;
        MixLesson.Playing.Value = act;
        int request = ++s_request;
        s_pending++;
        bool compare = act == "cmp";
        Render(view, other, compare, (main, second) =>
        {
            s_pending--;
            if (request != s_request || s_player is not { } player) return;
            player.SetTake(TakeSlotWith, main);
            if (second is not null) player.SetTake(TakeSlotOther, second);
            player.Play(TakeSlotWith, from, to, loop);
        });
    }

    /// <summary>A comparison's flip at the playhead: slot 0 holds the version the comparison started on, slot 1 the other; the player keeps
    /// the position and crossfades.</summary>
    public static void Flip(bool with)
    {
        if (s_player is not { } player || s_act != "cmp") return;
        player.Select(with == s_startedWith ? TakeSlotWith : TakeSlotOther);
    }

    /// <summary>A value changed while something plays: render the take (both, in a comparison) again and crossfade into it at the playhead.</summary>
    public static void Edited()
    {
        if (s_player is not { Playing: true } || s_act.Length == 0) return;
        var step = (MixLessonStep)MixLesson.Step.Peek();
        var values = MixLesson.Values.Peek();
        bool compare = s_act == "cmp";
        MixLessonView view, other = default;
        if (compare)
        {
            view = MixLesson.ViewOf(step, in values, s_startedWith ? MixLesson.Compare.With : MixLesson.Compare.Original);
            other = MixLesson.ViewOf(step, in values, s_startedWith ? MixLesson.Compare.Original : MixLesson.Compare.With);
        }
        else view = MixLesson.ViewOf(step, in values, MixLesson.Compare.None);
        int request = s_request;
        Render(view, other, compare, (main, second) =>
        {
            if (request != s_request || s_player is not { } player) return;
            player.SetTake(TakeSlotWith, main);
            if (second is not null) player.SetTake(TakeSlotOther, second);
        });
    }

    public static void Stop()
    {
        s_request++;
        s_act = "";
        s_player?.Stop();
        if (MixLesson.Playing.Peek().Length > 0) MixLesson.Playing.Value = "";
        MixLesson.Playhead.Value = float.NaN;
    }

    // ── takes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One take as the inspector's `lesson` verb reports it.</summary>
    public readonly record struct TakeState(string Key, string State, double Seconds, double Start, double Peak, long RenderMs, string Error);

    static readonly Dictionary<string, long> s_renderMs = new(StringComparer.Ordinal);

    /// <summary>The takes known now (the inspector): pending, rendered (length, start, peak, ms) or failed (why). UI thread.</summary>
    public static List<TakeState> Takes()
    {
        var list = new List<TakeState>(s_jobs.Count);
        foreach (var (key, job) in s_jobs)
        {
            if (job.IsCompletedSuccessfully)
            {
                var c = job.Result;
                float peak = 0f;
                foreach (float x in c.Samples) peak = MathF.Max(peak, MathF.Abs(x));
                long ms;
                lock (s_renderMs) ms = s_renderMs.GetValueOrDefault(key);
                list.Add(new TakeState(key, "rendered", c.Seconds, c.StartSeconds, peak, ms, ""));
            }
            else if (job.IsFaulted) list.Add(new TakeState(key, "failed", 0, 0, 0, 0, job.Exception?.GetBaseException().ToString() ?? ""));
            else list.Add(new TakeState(key, "pending", 0, 0, 0, 0, ""));
        }
        return list;
    }

    /// <summary>Block (wall time) until every take in flight is done or <paramref name="ms"/> passed (the inspector's `lesson wait=`).</summary>
    public static void WaitForTakes(int ms)
    {
        var tasks = new List<Task>(s_jobs.Values);
        if (s_making is { } making) tasks.Add(making);
        try { Task.WaitAll(tasks.ToArray(), ms); }
        catch (AggregateException) { }
    }

    /// <summary>Render a view's take ahead (a step's listens and comparison, before the learner presses them).</summary>
    public static void Prefetch(MixLessonView view) => _ = Job(view);

    /// <summary>The take of <paramref name="view"/>: cached by its key, rendered once on a worker (a failed render is tried again next time).</summary>
    static Task<AudioClip> Job(MixLessonView view)
    {
        string key = view.TakeKey;
        if (s_jobs.TryGetValue(key, out var job) && !job.IsFaulted && !job.IsCanceled) return job;
        var making = s_making ?? Task.CompletedTask;
        job = Task.Run(async () =>
        {
            await making.ConfigureAwait(false);
            if (Volatile.Read(ref s_a) is not { } a || Volatile.Read(ref s_b) is not { } b) throw new InvalidOperationException("the songs were not made");
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var clip = Take(view, a, b);
            long took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lock (s_renderMs) s_renderMs[key] = took;
            Log.Info("mix", $"[lesson] take {key} rendered in {took} ms");
            return clip;
        });
        if (!s_jobs.ContainsKey(key)) s_order.Enqueue(key);
        s_jobs[key] = job;
        while (s_order.Count > MixLessonRules.CachedTakes) s_jobs.Remove(s_order.Dequeue());
        return job;
    }

    static void Render(MixLessonView view, MixLessonView other, bool both, Action<AudioClip, AudioClip?> done)
    {
        var first = Job(view);
        var second = both ? Job(other) : null;
        if (first.IsCompletedSuccessfully && (second is null || second.IsCompletedSuccessfully))
        {
            done(first.Result, second?.Result);
            return;
        }
        Task all = second is null ? first : Task.WhenAll(first, second);
        all.ContinueWith(t => Playback.ToUi(() =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                // A take that cannot be made plays nothing: the play ends as if it had played (the ticker clears it).
                Log.Warn("mix", "[lesson] take failed: " + t.Exception?.GetBaseException().Message);
                s_pending = Math.Max(0, s_pending - 1);
                return;
            }
            done(first.Result, second?.Result);
        }), TaskScheduler.Default);
    }

    static long F(double seconds) => (long)Math.Round(seconds * Rate);

    /// <summary>One take: the view rendered through the engine's mixer over the stretch every act plays from (bar 4 less a second of pre-roll,
    /// so the filters and the echo are settled, to the end of the view), as a clip placed at that time (worker thread).</summary>
    internal static AudioClip Take(MixLessonView v, float[] songA, float[] songB)
    {
        var mix = new OfflineMixer(Rate, Channels);
        double origin = MixLesson.V0 - MixLessonRules.PreRoll, end = MixLesson.V1 + Tail;
        long R(double t) => F(t - origin);
        const int n = MixLessonRules.TablePoints;

        // Night Drive: its gain over its own length (it is over at bar 9), its EQ (the swap; lows only) and its closing filter.
        double aEnd = MixLesson.Ov1;
        var gainA = Table(n, origin, aEnd, t => GainA(t, in v));
        var autoA = Automation(v, outgoing: true, origin, end, origin, echo: false);
        var a = new MemoryAudioSource(songA, Channels);
        a.SeekFrame(F(origin));
        mix.AddVoice(a, GainEnvelope.FromCurve(FadeKind.Out, 0, (int)R(aEnd), gainA), 0, autoA);

        // The echo: Night Drive's last half beat, pre-fader (filtered as the song is), into the echo's wet only.
        if (v.Has(MixLessonLayer.Echo))
        {
            var gate = Table(n, origin, aEnd, t => t >= MixLesson.EchoAt && t < MixLesson.EchoAt + MixLesson.Beat * 0.5 ? 1f : 0f);
            var autoE = Automation(v, outgoing: true, origin, end, origin, echo: true);
            var e = new MemoryAudioSource(songA, Channels);
            e.SeekFrame(F(origin));
            mix.AddVoice(e, GainEnvelope.FromCurve(FadeKind.In, 0, (int)R(aEnd), gate), 0, autoE);
        }

        // Sunrise: from bar 5, its gain, its lows coming in at the swap, and (beatmatched) Night Drive's tempo until the overlap ends.
        if (v.Volume != MixLessonVolume.AOnly)
        {
            var gainB = Table(n, MixLesson.Ov0, end, t => GainB(t, in v));
            var autoB = Automation(v, outgoing: false, MixLesson.Ov0, end, origin, echo: false);
            VoiceRatePlan? rate = null;
            if (v.Has(MixLessonLayer.Tempo) && Math.Abs(v.Values.Bpm - MixLesson.BpmB) > 1e-3)
            {
                double r = v.Values.Bpm / MixLesson.BpmB;
                rate = VoiceRatePlan.Glide(r, R(MixLesson.Ov1), 1.0, Math.Abs(1 - r) / MixLessonRules.GlideSteps, F(MixLesson.Glide / MixLessonRules.GlideSteps));
            }
            mix.AddVoice(new MemoryAudioSource(songB, Channels), GainEnvelope.FromCurve(FadeKind.In, R(MixLesson.Ov0), (int)F(end - MixLesson.Ov0), gainB),
                R(MixLesson.Ov0), autoB, rate);
        }

        var pcm = mix.Render((int)R(end));
        // The master: a little headroom and a soft knee, so two songs at full level never clip.
        for (int i = 0; i < pcm.Length; i++) pcm[i] = MathF.Tanh(pcm[i] * MixLessonRules.MasterGain);
        return new AudioClip(pcm, Channels, Rate, origin);
    }

    static float[] Table(int points, double from, double to, Func<double, float> f)
    {
        var t = new float[points];
        for (int i = 0; i < points; i++) t[i] = f(from + (to - from) * i / (points - 1));
        return t;
    }

    /// <summary>A layer's automation on one side (null when nothing moves): the bass swap and "lows only" on the 3-band EQ, the closing
    /// low-pass (Night Drive), the echo (its own voice).</summary>
    static VoiceAutomation? Automation(MixLessonView v, bool outgoing, double from, double to, double origin, bool echo)
    {
        const int n = MixLessonRules.TablePoints;
        var view = v;
        EqLaneSpec? eq = null;
        bool swap = v.Has(MixLessonLayer.Bass);
        if (swap || v.LowsOnly)
        {
            var low = swap ? Table(n, from, to, t => Db(outgoing ? 1f - MixLessonPicture.Swap(t, in view) : MixLessonPicture.Swap(t, in view))) : [];
            float[] kill = v.LowsOnly ? [float.NegativeInfinity, float.NegativeInfinity] : [];
            eq = new EqLaneSpec(low, kill, kill) { Slope = MixLessonRules.EqSlope };
        }
        SweepPoint[]? sweep = null;
        if (outgoing && v.Has(MixLessonLayer.Filter))
        {
            sweep = new SweepPoint[n];
            for (int i = 0; i < n; i++)
            {
                double t = from + (to - from) * i / (n - 1);
                sweep[i] = t <= MixLesson.Ov0 ? new SweepPoint(SweepPoint.Bypass, (float)MixLesson.FilterHigh, 0.707f)
                    : new SweepPoint(SweepPoint.LowPass, (float)MixLessonPicture.Cutoff(t, in view), MixLessonRules.FilterQ);
            }
        }
        DelayLaneSpec? delay = null;
        if (echo)
        {
            float feedback = (float)Math.Pow(MixLessonRules.EchoFloor, 1.0 / Math.Max(1, v.Values.Repeats));
            delay = new DelayLaneSpec((int)F(MixLesson.Delay), new float[] { 1f, 1f })
            {
                Feedback = Math.Min(DelayLaneSpec.MaxFeedback, feedback), HighCutHz = MixLessonRules.EchoHighCutHz, Law = DryWetLaw.EqualPower,
            };
        }
        if (eq is null && sweep is null && delay is null) return null;
        return VoiceAutomation.Build(new VoiceAutomationSpec(Rate, F(from - origin), (int)F(to - from))
        {
            Eq = eq, Sweep = sweep ?? ReadOnlyMemory<SweepPoint>.Empty, Echo = delay, Channels = Channels,
        });
    }

    static float Db(float g) => g <= 1e-4f ? float.NegativeInfinity : 20f * MathF.Log10(g);

    /// <summary>Night Drive's level at <paramref name="t"/>: the fade, a hard cut, full (a solo).</summary>
    internal static float GainA(double t, in MixLessonView v)
    {
        if (t >= MixLesson.Ov1) return 0f;
        switch (v.Volume)
        {
            case MixLessonVolume.Cut: return t < MixLesson.Mid ? 1f : 0f;
            case MixLessonVolume.Flat or MixLessonVolume.AOnly: return 1f;
        }
        double s = v.Values.FadeStart, e = 2 * MixLesson.Mid - s;
        if (e - s < 1e-3) return t < MixLesson.Mid ? 1f : 0f;
        return t <= s ? 1f : t >= e ? 0f : (float)Math.Cos((t - s) / (e - s) * Math.PI / 2);
    }

    /// <summary>Sunrise's level at <paramref name="t"/>.</summary>
    internal static float GainB(double t, in MixLessonView v)
    {
        if (t < MixLesson.Ov0) return 0f;
        switch (v.Volume)
        {
            case MixLessonVolume.Cut: return t >= MixLesson.Mid ? 1f : 0f;
            case MixLessonVolume.Flat: return 1f;
            case MixLessonVolume.AOnly: return 0f;
        }
        double s = v.Values.FadeStart, e = 2 * MixLesson.Mid - s;
        if (e - s < 1e-3) return t >= MixLesson.Mid ? 1f : 0f;
        return t <= s ? 0f : t >= e ? 1f : (float)Math.Sin((t - s) / (e - s) * Math.PI / 2);
    }
}

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>The lesson's aux output (Entities/Mix.Lesson.Audio.cs): on the music's endpoint (the Windows default when the music plays on
        /// none, as under `--fake`); the paced silent endpoint headless. Null when no endpoint is known. Not started.</summary>
        internal static AuxAudioOutput? OpenLessonOutput(IAuxAudioGenerator generator)
        {
            if (s_forceSilent) return OpenAux("", generator);
            string? id = MusicEndpointId();
            if (id is null)
                foreach (LocalAudioDevice d in Devices.Peek())
                    if (d.IsDefault) { id = d.Id; break; }
            return id is null ? null : OpenAux(id, generator);
        }
    }
}
