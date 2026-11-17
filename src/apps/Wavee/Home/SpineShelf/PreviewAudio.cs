// ── Home/SpineShelf/PreviewAudio.cs — the Spine Shelf's song previews: who owns one, how loud, and the duck on the user's music ────────────
//
// Owner spec (2026-11-17): hovering (or focusing) a song tile ALWAYS plays its 30 s preview, muted by default; the shelf's sound button
// turns the previews' sound on (a setting, Platform.Keys.PreviewSound). With sound on, the user's music on this device is ducked well under
// the preview (SpineShelfRules.DuckDb), never silenced. Every level change is a fade. A preview never outlives its owner.
//
// OWNERSHIP. One CLAIM at a time: the tile the pointer or focus is on (its shelf instance, its tile code, its key). The claim is not trusted
// to be released by events: on every tick (every frame while it is active, through the shelves' frame ticker (Clock) and Shell.FrameTick,
// AND a watchdog timer that keeps ticking while anything sounds or eases, so a minimized window, an unmounted shelf or an idle window that
// renders nothing still checks) the claim's shelf is asked whether it still
// holds the tile: mounted and shown, its live tile, the window active and not minimized, and the pointer inside the tile's visible rect
// (or keyboard focus on the tile). A no releases the claim and tells the shelf (the tile narrows as if the pointer had left). So a missed
// exit (the pointer leaving the window, a blur, a minimize, an unmount, a scroll, a navigation, a theme switch) ends the preview within one
// tick. A voice also dies on its player's failure or end, and none plays past SpineShelfRules.VoiceMaxMs (the hard bound).
//
// VOICES. Two players, so the old preview and the next can CROSS: moving to the next tile keeps the old voice at its level (HandoverMs at
// most) until the next one starts, then both cross over CrossMs (equal power). A voice that fades to silence is stopped.
//
// GAIN STAGES (all on this device; nothing is ever sent to Spotify Connect):
//   voice    sin(p·π/2) × sin(s·π/2) × taper(LocalVolume): p the voice's fade, s the sound button's fade; the preview plays at the loudness the
//            user's own music has here, so it is "mostly loud" over the ducked music and never louder than their music would be.
//   duck     10^(d/20) on Playback.SetPreviewDuck, d eased in dB between 0 and DuckDb: a multiplier on the local audio and video players,
//            never written into the user's volume, the slider or the saved setting. A volume change mid-duck is the new base (the product
//            follows it), a track change opens at the product (the new track is ducked the same), pause, play and stop do not touch it, and
//            nothing persists it: an exit mid-duck saves the user's own volume.
//   remote   music playing on ANOTHER Connect device (OwnerSignal Foreign, an iPhone): no duck at all (that device's volume is not ours to
//            touch, and no Connect command is ever sent); the preview plays at its normal level.
// Each fade is linear in time on its own position (p, s, d), re-aimed from where it stands when its target turns, so no curve ever steps.
// The local engine's master gain ramps each write over a few ms, so per-frame writes are heard as a continuous curve.

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>Whoever holds a preview claim (the Spine Shelf, the Release Spotlight): asked on every tick whether it still holds the tile it
/// claimed, and told when it lost it. Both are allocation-free.</summary>
public interface IPreviewOwner
{
    /// <summary>"" while the owner still holds tile <paramref name="code"/> (<paramref name="tile"/> its key), else why not.</summary>
    string HoldFails(int code, string tile);

    /// <summary>The ownership check found tile <paramref name="code"/> no longer held: the owner lets it go as if the pointer had left (never re-claims).</summary>
    void HoldLost(int code);
}

public static class PreviewAudio
{
    // ── the public surface ──

    /// <summary>The previews' sound is on (the shelf's sound button; Platform.Keys.PreviewSound). Off by default.</summary>
    public static readonly Signal<bool> Sound = new(Platform.Settings.Get(Platform.Keys.PreviewSound));

    /// <summary>Anything sounds, eases or is claimed: the frame tick runs the mixer and the watchdog timer runs.</summary>
    public static readonly Signal<bool> Active = new(false);

    /// <summary>A claimed tile's clip has started (UI thread): its progress line fills from here.</summary>
    public static event Action<string>? ClipStarted;

    /// <summary>Flip the previews' sound (the button). Persisted; the level eases (SoundMs) and the duck follows.</summary>
    public static void ToggleSound()
    {
        bool on = !Sound.Peek();
        Sound.Value = on;
        Platform.Settings.Set(Platform.Keys.PreviewSound, on);
        long now = Playback.FrameNowMs();
        s_sound.Aim(on ? 1f : 0f, SpineShelfRules.SoundMs, now);
        Wake();
    }

    // ── state ──

    sealed class Voice
    {
        public MediaPlayer? Player;
        public string Tile = "";
        public Ramp P;                // the fade's position 0..1 (heard as sin(p·π/2))
        public long BornMs;           // the open's time (the hard bound counts from it)
        public bool Opening, Started; // the open was asked / the player reported Playing
        public bool Handover;         // an old voice holding for the next one to start
        public long HandoverSinceMs;
        public float Applied = -1f;   // the last volume written to the player
        public int Index;
        public bool Busy => Tile.Length > 0;
    }

    /// <summary>A linear ramp on a position, re-aimed from where it stands (never a jump).</summary>
    struct Ramp
    {
        public float From, To;
        public long StartMs;
        public float Ms;

        public readonly float At(long now)
        {
            if (Ms <= 0f) return To;
            float t = (now - StartMs) / Ms;
            return t >= 1f ? To : t <= 0f ? From : From + (To - From) * t;
        }

        public readonly bool Done(long now) => Ms <= 0f || now - StartMs >= Ms;

        /// <summary>Aim at <paramref name="to"/>: from the current value, over <paramref name="fullMs"/> times the distance (a full 0↔1
        /// travel takes fullMs). <paramref name="span"/> is the position's full range (1 for p and s, |DuckDb| for the duck).</summary>
        public void Aim(float to, float fullMs, long now, float span = 1f)
        {
            if (to == To) return;   // there, or on its way there
            float cur = At(now);
            From = cur;
            To = to;
            StartMs = now;
            Ms = fullMs * MathF.Abs(to - cur) / span;
        }

        public void Set(float v) { From = To = v; Ms = 0f; }
    }

    static readonly Voice[] s_voices = [new() { Index = 0 }, new() { Index = 1 }];
    static Ramp s_sound, s_duck;           // s 0..1; the duck in dB (0 .. DuckDb)
    static bool s_media, s_fake;

    static PreviewAudio() => s_sound.Set(Sound.Peek() ? 1f : 0f);
    static IMediaBackend? s_builtOver;
    static readonly FluentGpu.Media.Windows.MfMediaPlayer?[] s_backends = new FluentGpu.Media.Windows.MfMediaPlayer?[2];

    // The claim: the tile the pointer or focus is on.
    static IPreviewOwner? s_owner;
    static int s_code = -1;
    static string s_tile = "";

    static long s_lastTickMs = long.MinValue;
    static float s_duckApplied = 1f;
    static System.Threading.Timer? s_watchdog;
    static int s_watchdogQueued;
    static bool s_watchdogOn;
    static readonly Action s_watchdogTick = WatchdogTick;

    // ── diagnostics (the inspector's `home do=preview`) ──

    /// <summary>One tick's levels, for the inspector's curve read: time, both voices' fade gains (heard = fade × sound × the user's local
    /// amplitude), the sound fade's gain, the duck gain, the user's local amplitude and what the local music player applies.</summary>
    public readonly record struct Sample(long Ms, float V0, float V1, float SoundGain, float Duck, float UserAmp, float Applied);

    const int Ring = 512;
    static readonly Sample[] s_ring = new Sample[Ring];
    static int s_ringHead, s_ringCount;
    /// <summary>Ticks, duck writes and voice steals since the mount (the inspector reads them).</summary>
    public static int Ticks, DuckWrites, Steals, Releases;
    /// <summary>Why the last claim ended ("leave", "lost:<reason>", "card", ...).</summary>
    public static string LastRelease = "";

    public static string ClaimTile => s_tile;
    public static bool Remote => MusicIsRemote();
    public static float SoundGain => Shape(s_sound.At(Playback.FrameNowMs()));
    public static float DuckNow => s_duckApplied;

    /// <summary>A voice's readout: its tile, its heard gain (shape × sound), whether its player plays, and its state.</summary>
    public static (string Tile, float Gain, bool Playing, string State) VoiceInfo(int i)
    {
        var v = s_voices[i];
        long now = Playback.FrameNowMs();
        string state = v.Player?.State.Peek().ToString() ?? "none";
        return (v.Tile, Shape(v.P.At(now)) * Shape(s_sound.At(now)), v.Player is { } p && p.IsPlaying.Peek(), state);
    }

    /// <summary>The recorded samples, oldest first (at most the ring's size), and clear them.</summary>
    public static int DrainSamples(Span<Sample> into)
    {
        int n = Math.Min(s_ringCount, into.Length);
        int start = (s_ringHead - s_ringCount + Ring) % Ring;
        for (int i = 0; i < n; i++) into[i] = s_ring[(start + i) % Ring];
        s_ringCount = 0;
        return n;
    }

    /// <summary>The inspector's mount (between mounts, never in a render): the sound as the mount's profile has it, no claim, no duck, and
    /// the counters and samples forgotten.</summary>
    public static void ResetForMount()
    {
        Release("mount", now: true);
        bool on = Platform.Settings.Get(Platform.Keys.PreviewSound);
        Sound.Value = on;
        s_sound.Set(on ? 1f : 0f);
        Ticks = DuckWrites = Steals = Releases = 0;
        LastRelease = "";
        s_ringCount = 0;
    }

    // ── building ──

    /// <summary>A shelf mounted: build the two voices once (over their own Media Foundation backends; over the inspector's fake backend under
    /// `mount media=fake`). The headless test host builds real players only under `media=real` (ShelfPreview.RealInTestHost).</summary>
    public static void Warm()
    {
        var fake = CanvasVideo.InspectBackend;
        if (s_media && ReferenceEquals(s_builtOver, fake)) return;
        if (fake is null && Shell.IsTestHost && !ShelfPreview.RealInTestHost) return;
        if (s_tile.Length > 0 || s_voices[0].Busy || s_voices[1].Busy) Release("rebuild", now: true);
        foreach (var v in s_voices)
        {
            if (v.Player is { } old) _ = old.DisposeAsync();
            v.Player = null;
        }
        s_builtOver = fake;
        s_fake = fake is not null;
        s_media = true;
        for (int i = 0; i < s_voices.Length; i++)
        {
            IMediaBackend backend = fake ?? (s_backends[i] ??= new FluentGpu.Media.Windows.MfMediaPlayer());
            var p = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
            p.SetVolume(0d);
            s_voices[i].Player = p;
        }
    }

    // ── the claim (the shelf calls these) ──

    /// <summary>The pointer or focus came onto tile <paramref name="code"/> of <paramref name="owner"/>: it owns the preview from now. An old
    /// voice holds (HandoverMs at most) for this one to start, then they cross.</summary>
    public static void Claim(IPreviewOwner owner, int code, string tile)
    {
        if (ReferenceEquals(s_owner, owner) && s_code == code && s_tile == tile) return;
        long now = Playback.FrameNowMs();
        s_owner = owner;
        s_code = code;
        s_tile = tile;
        foreach (var v in s_voices)
        {
            if (!v.Busy) continue;
            if (v.Tile == tile) { v.Handover = false; if (v.Started) v.P.Aim(1f, SpineShelfRules.FadeInMs, now); continue; }   // back onto a fading tile
            if (!v.Started) { StopVoice(v); continue; }   // never heard: nothing to cross
            v.Handover = true;
            v.HandoverSinceMs = now;
            v.P.Aim(v.P.At(now), 0f, now);               // hold where it stands
        }
        Wake();
    }

    /// <summary>The claimed tile has settled (it finished widening): its clip opens now, off the widening's frames.</summary>
    public static void Start(string tile, string url)
    {
        if (tile != s_tile || !s_media) return;
        foreach (var v in s_voices) if (v.Tile == tile) return;   // already sounding (a re-settle)
        Voice? free = null;
        foreach (var v in s_voices) if (!v.Busy) { free = v; break; }
        if (free is null)
        {
            // Both busy (three tiles inside a cross): take the quieter, which is the one fading out.
            long now = Playback.FrameNowMs();
            free = Shape(s_voices[0].P.At(now)) <= Shape(s_voices[1].P.At(now)) ? s_voices[0] : s_voices[1];
            StopVoice(free);
            Steals++;
        }
        Open(free, tile, url);
        Wake();
    }

    /// <summary>The claim on <paramref name="tile"/> ends (the pointer or focus left it, its card closed, its shelf unmounted): every voice
    /// fades out and the duck eases back up. A tile that is not the claim's is ignored ("" ends whatever is claimed).</summary>
    public static void Leave(string tile, string why = "leave")
    {
        if (s_tile.Length == 0 || tile.Length > 0 && tile != s_tile) return;
        Release(why, now: false);
    }

    /// <summary>An owner unmounted: a claim it held ends now.</summary>
    public static void Disown(IPreviewOwner owner)
    {
        if (ReferenceEquals(s_owner, owner)) Release("unmounted", now: false);
    }

    static void Release(string why, bool now)
    {
        bool had = s_tile.Length > 0;
        s_owner = null;
        s_code = -1;
        s_tile = "";
        long t = Playback.FrameNowMs();
        foreach (var v in s_voices)
        {
            if (!v.Busy) continue;
            v.Handover = false;
            if (now || !v.Started) StopVoice(v);
            else v.P.Aim(0f, SpineShelfRules.FadeOutMs, t);
        }
        if (now) { s_duck.Set(0f); ApplyDuck(1f); }
        if (had) { Releases++; LastRelease = why; }
        Wake();
    }

    // ── the voices ──

    static async void Open(Voice v, string tile, string url)
    {
        if (v.Player is not { } player) return;
        v.Tile = tile;
        v.Opening = true;
        v.Started = false;
        v.Handover = false;
        v.P.Set(0f);
        v.Applied = -1f;
        v.BornMs = Playback.FrameNowMs();
        try
        {
            player.SetVolume(0d);
            player.SetMuted(false);
            await player.OpenAsync(MediaSource.FromUri(url).WithKind(MediaKind.MfVideoOrFile), new MediaOpenOptions { StartPaused = false });
            // Back on the UI thread (the open may finish anywhere): play it if this voice still wants it (the tick fades it in once it plays).
            CanvasVideo.Files.Post(() => { if (v.Tile == tile && v.Opening) _ = player.PlayAsync(); });
        }
        catch (Exception ex)
        {
            // A failed open is a dead voice: the tick drops it (its gain was 0 and stays 0) and the duck eases back.
            Log.Warn("home", "shelf.preview open failed " + ex.GetType().Name + ": " + ex.Message);
            CanvasVideo.Files.Post(() => { if (v.Tile == tile && !v.Started) StopVoice(v); Wake(); });
        }
    }

    static void StopVoice(Voice v)
    {
        v.Tile = "";
        v.Opening = v.Started = v.Handover = false;
        v.P.Set(0f);
        if (v.Player is { } p)
        {
            try { p.SetVolume(0d); p.Stop(); } catch { /* a dead player: nothing to stop */ }
        }
        v.Applied = 0f;
    }

    // ── the tick ──

    /// <summary>The mixer's frame clock, mounted by every shelf: a frame ticker while <see cref="Active"/> (its subscription asks for
    /// frames, so the fades are written every frame and not only on the watchdog's beat), nothing otherwise (an idle shelf asks for none).</summary>
    public sealed class Clock : Component
    {
        public override Element Render()
            => Active.Value ? Embed.Comp(static () => new Ticker()) : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        sealed class Ticker() : Controls.FrameTicker(s_frameTick);
    }

    static readonly Action s_frameTick = FrameTick;

    /// <summary>Every rendered frame (Shell.FrameTick, UI thread): one bool when idle.</summary>
    public static void FrameTick()
    {
        if (!Active.Peek()) return;
        Tick();
    }

    static void WatchdogTick()
    {
        System.Threading.Interlocked.Exchange(ref s_watchdogQueued, 0);
        if (Active.Peek()) Tick();
    }

    /// <summary>The ownership check, the voices' lives, the fades and the duck. UI thread. Allocation-free.</summary>
    static void Tick()
    {
        long now = Playback.FrameNowMs();

        // 1. The claim: does its shelf still hold the tile? (Never trust that an exit event arrived.) Asked on every tick, also when the
        // clock has not moved (a parked window's frames, a frame and the watchdog in the same instant).
        if (s_tile.Length > 0)
        {
            var owner = s_owner;
            string why = owner is null ? "owner" : owner.HoldFails(s_code, s_tile);
            if (why.Length > 0)
            {
                int code = s_code;
                Release("lost:" + why, now: false);
                owner?.HoldLost(code);   // the shelf narrows the tile as if the pointer had left (never re-claims)
            }
        }
        if (now == s_lastTickMs) return;   // the levels are a function of the clock: nothing more to write
        s_lastTickMs = now;
        Ticks++;

        // 2. The voices: started, failed, ended, over the bound, handed over, faded out.
        bool claimStarted = false;
        foreach (var v in s_voices)
        {
            if (!v.Busy || v.Player is not { } p) continue;
            var state = p.State.Peek();
            if (state is PlaybackState.Failed || state is PlaybackState.Ended || now - v.BornMs > SpineShelfRules.VoiceMaxMs)
            {
                // A dead or finished player stops at once if it is silent, else it fades out (an ended one is already silent).
                if (state is PlaybackState.Failed or PlaybackState.Ended) { StopVoice(v); continue; }
                if (v.P.To > 0f) v.P.Aim(0f, SpineShelfRules.FadeOutMs, now);
            }
            if (v.Opening && state is PlaybackState.Playing)
            {
                v.Opening = false;
                v.Started = true;
                bool crossing = false;
                foreach (var o in s_voices) if (o != v && o.Busy && o.Handover) crossing = true;
                if (v.Tile == s_tile) v.P.Aim(1f, crossing ? SpineShelfRules.CrossMs : SpineShelfRules.FadeInMs, now);
                if (v.Tile == s_tile) { claimStarted = true; ClipStarted?.Invoke(v.Tile); }
            }
            if (v.Started && v.Tile != s_tile && v.P.To > 0f && !v.Handover) v.P.Aim(0f, SpineShelfRules.FadeOutMs, now);
            if (!v.Started && !v.Opening) StopVoice(v);
        }
        foreach (var v in s_voices)
        {
            if (!v.Busy || !v.Handover) continue;
            // The next voice started: cross. Or it never came in time: fade out alone.
            if (claimStarted) { v.Handover = false; v.P.Aim(0f, SpineShelfRules.CrossMs, now); }
            else if (now - v.HandoverSinceMs >= SpineShelfRules.HandoverMs) { v.Handover = false; v.P.Aim(0f, SpineShelfRules.FadeOutMs, now); }
        }

        // 3. The duck's target: down while a claimed voice sounds with sound on, on THIS device's music only.
        bool heard = false;
        foreach (var v in s_voices) if (v.Busy && v.Started && (v.P.To > 0f || v.Handover) && v.Tile.Length > 0) heard = true;
        bool duck = heard && s_tile.Length > 0 && Sound.Peek() && !MusicIsRemote();
        s_duck.Aim(duck ? SpineShelfRules.DuckDb : 0f, duck ? SpineShelfRules.DuckDownMs : SpineShelfRules.DuckUpMs, now, MathF.Abs(SpineShelfRules.DuckDb));

        // 4. Apply: each voice, the duck.
        float level = Playback.Audio.VolumeTaper.Amplitude(Playback.LocalVolume);
        float sound = Shape(s_sound.At(now));
        Span<float> fade = stackalloc float[2];
        for (int i = 0; i < s_voices.Length; i++)
        {
            var v = s_voices[i];
            float gp = Shape(v.P.At(now));
            fade[i] = v.Busy ? gp : 0f;
            if (!v.Busy || v.Player is not { } p) continue;
            float amp = gp * sound * level;
            if (MathF.Abs(amp - v.Applied) > 1e-5f) { v.Applied = amp; try { p.SetVolume(amp); } catch { } }
            if (v.Started && v.P.To <= 0f && v.P.Done(now) && !v.Handover) StopVoice(v);   // faded out: stop it
        }
        float duckDb = s_duck.At(now);
        ApplyDuck(duckDb >= -0.001f ? 1f : MathF.Pow(10f, duckDb / 20f));

        Record(now, fade[0], fade[1], sound);

        // 5. Idle? Nothing claimed, no voice, the duck home, the sound fade at rest.
        bool busy = s_tile.Length > 0 || s_voices[0].Busy || s_voices[1].Busy || !s_duck.Done(now) || s_duck.To != 0f || !s_sound.Done(now);
        if (!busy) { Active.Value = false; Watchdog(false); }
    }

    static void ApplyDuck(float gain)
    {
        if (MathF.Abs(gain - s_duckApplied) < 1e-5f) return;
        s_duckApplied = gain;
        DuckWrites++;
        Playback.SetPreviewDuck(gain);
    }

    static void Record(long now, float v0, float v1, float sound)
    {
        s_ring[s_ringHead] = new Sample(now, v0, v1, sound, s_duckApplied, Playback.Audio.VolumeTaper.Amplitude(Playback.LocalVolume), Playback.Audio.AppliedAmplitude);
        s_ringHead = (s_ringHead + 1) % Ring;
        if (s_ringCount < Ring) s_ringCount++;
    }

    /// <summary>The heard gain of a fade position: sin(p·π/2) (an equal-power pair when two cross; 0 and 1 exact).</summary>
    static float Shape(float p) => p <= 0f ? 0f : p >= 1f ? 1f : MathF.Sin(p * (MathF.PI / 2f));

    /// <summary>The music plays on another Connect device (an iPhone): its volume is not ours, so nothing is ducked.</summary>
    static bool MusicIsRemote() => Wavee.Theming.InspectSwitches.RemoteDevice is not null || Playback.OwnerSignal.Peek() == Playback.Owner.Foreign;

    /// <summary>Something changed: tick from the next frame on (the shelves' frame ticker, Shell.FrameTick) and on the watchdog.</summary>
    static void Wake()
    {
        if (!Active.Peek()) Active.Value = true;
        Watchdog(true);
    }

    static void Watchdog(bool on)
    {
        if (on == s_watchdogOn) return;
        s_watchdogOn = on;
        s_watchdog ??= new System.Threading.Timer(static _ =>
        {
            // One queued tick at a time: a UI thread that is busy for a while does not collect a backlog.
            if (System.Threading.Interlocked.Exchange(ref s_watchdogQueued, 1) == 0) Playback.ToUi(s_watchdogTick);
        });
        s_watchdog.Change(on ? SpineShelfRules.WatchdogMs : System.Threading.Timeout.Infinite, on ? SpineShelfRules.WatchdogMs : System.Threading.Timeout.Infinite);
    }
}
