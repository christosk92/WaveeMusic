// ── Shell/LyricsModel.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// the lyrics surface's MODEL: what is on screen (state, document, the held upgrade), where the song is (the media
// clock, the active line and the voice line), whether the follow is attached (the follow / resync state machine), the
// instrumental break (the dots and the reserved band), the second line, the AI state, and the verbs a view calls
//
// Role: CORE (engine-free: signals, the playback host's samples, settings; no element, component, scene or scroll type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C5 (X15)
//
// WHAT MOVED. `Lyrics.UI.cs`'s `ViewCore` used to hold, beside the nodes it drives, the whole of the surface's STATE and
// the decisions over it: the document slot and the upgrade authority gate, the media clock and its sample feed, the
// frame's line resolution (lead-shifted active line, true-time voice line, the interlude advance), the follow modes and
// the resync countdown, the interlude reserve bookkeeping, the second-line preference and its capability, and the verbs
// (seek a line, cycle the second line, resync, prefer AI timing, open AI settings, expand to the stage). They live here
// now, as plain methods over those fields that RETURN what the view must do about the scene:
//
//   · the scroll viewport, the staggered hand-off cascade, the depth-of-field ramp, the wipe and halo layers, the dots'
//     transform, the measured layout and every node handle stay in the view (they are drawing, not deciding);
//   · the ink mapping (`Lyrics.Ink`) stays in the view until T7 turns it into style roles;
//   · a view reacts to an edge by reading the small records below (`FollowChange`, `ScrollEdge`, `FollowTick`,
//     `LyricsFrame`, `UpgradeVerdict`) and by being the `ILyricsSceneSink` the model calls back through.
//
// ONE MODEL PER MOUNTED SURFACE (the rail's body, the stage's column, a podcast reader's transcript): `Surface` says which.
// A theme's lyrics view reads the signals (`State`, `ActiveLine`, `Follow`, `ResyncNeeded`, `DotsShown`, `SecondLine`),
// the pure facts (`WordProgress`) and calls `Verbs`; it never reaches into `Playback` or the stores itself.

using System.Diagnostics;
using FluentGpu.Signals;
using FrameTime = Wavee.Design.FrameTime;

namespace Wavee;

/// <summary>What the reading surface shows: nothing playing, a document on its way, none to be had, a failed fetch, a
/// static block of text, or a timed document that follows playback.</summary>
public enum LyricsState : byte { Nothing, Loading, NoLyrics, Failed, Unsynced, Synced }

/// <summary>Which surface a model belongs to (mount config, frozen for the model's life).</summary>
/// <param name="Large">Stage metrics (type, gutter, band, σ ladder) rather than the rail's.</param>
/// <param name="OnMedia">Painted on media ink (the stage, or the rail's on-media mode) rather than theme ink.</param>
/// <param name="OnStage">The fullscreen stage's view — an EXPLICIT flag: <see cref="OnMedia"/> is also true for the rail's
/// on-media mode, which keeps its blur. The stage resolves blur strength 0.</param>
/// <param name="TypeScale">The stage's type scale over the authored metrics (1 on the rail and the reader).</param>
public readonly record struct LyricsSurface(bool Large, bool OnMedia, bool OnStage, float TypeScale = 1f);

/// <summary>What a document offered mid-song should do: nothing (not richer), land now, or wait for the next hand-off.</summary>
public enum UpgradeVerdict : byte { Ignore, Apply, Hold }

/// <summary>What a user-scroll edge asks of the view: nothing, detach (stop the cascade, then DetachedActive), or start
/// the idle countdown (DetachedIdle).</summary>
public enum ScrollEdge : byte { None, Detach, Idle }

/// <summary>What a frame's follow tick asks of the view: nothing, begin the resync, or drive the one in flight.</summary>
public enum FollowTick : byte { None, BeginResync, DriveResync }

/// <summary>The result of a follow-mode write: whether it changed, whether it LEFT Following (the cascade, the dots and the
/// reserved band retire at that edge, so their exits animate) and whether the σ suppression flipped.</summary>
public readonly record struct FollowChange(bool Changed, bool LeftFollowing, bool SuppressionFlipped);

/// <summary>One frame's resolved lines: the media time, the lead-shifted ACTIVE line, the true-time VOICE line (-1 once
/// a line is sung out), the interlude gap (equal bounds ⇒ not in one) and what changed.</summary>
public readonly record struct LyricsFrame(long NowMs, int Active, int Voice, int PreviousVoice, long GapStart, long GapEnd,
                                          bool ActiveChanged, bool VoiceChanged);

/// <summary>The verbs a lyrics view calls. The model implements the ones that need its state; the header's verbs work with
/// no model at all (<see cref="LyricsVerbs.Global"/>).</summary>
public interface ILyricsVerbs
{
    /// <summary>A line was tapped: back to live, commit the seek, re-anchor the clock, hard-latch the follow.</summary>
    void SeekToLine(int index);
    /// <summary>The globe: none → translation → romanization → none, skipping any layer the document lacks.</summary>
    void ToggleSecondLine();
    /// <summary>The Resync pill: re-attach the follow now.</summary>
    void Resync();
    /// <summary>The AI sparkle: switch the playing song between AI timing and the provider's word timing.</summary>
    void ToggleAiPreferred();
    /// <summary>Open the settings page that can fix what the sparkle's tooltip says.</summary>
    void OpenAiSettings();
    /// <summary>The ⛶ button: open the fullscreen stage. <paramref name="begin"/> is the caller's shared-transition door
    /// (null headless); <paramref name="cause"/> names the entry for the diagnostics.</summary>
    void ExpandToStage(Action<string>? begin, string cause);
}

/// <summary>What the model calls back on the view for the work only the scene can do. Every member is a no-op-able edge.</summary>
public interface ILyricsSceneSink
{
    /// <summary>Re-attach the follow state (the countdown, the cascade, the mode).</summary>
    void ResetFollow();
    /// <summary>A seek was committed: the next follow is a hard latch, the cascade retires, motion wakes.</summary>
    void SeekLanded();
    /// <summary>The user asked for the resync (the pill): begin it against the live scene.</summary>
    void ResyncRequested();
}

public sealed class LyricsModel
{
    public LyricsModel(LyricsSurface surface, EntityId readerEpisode = default)
    {
        Surface = surface;
        ReaderEpisode = readerEpisode;
        DotAlpha = new FloatSignal[Lyrics.Interlude.DotCount];
        for (int k = 0; k < DotAlpha.Length; k++) DotAlpha[k] = new FloatSignal(0f);
        ActiveLineProp = Prop.Bind(ActiveLine);
        VoiceWordProgress = Prop.Of(WordProgressNow);
        Verbs = new LyricsVerbs(this);
    }

    public LyricsSurface Surface { get; }

    /// <summary>The podcast reader's episode (valid ⇒ the reader, which shows ITS transcript whatever plays); default ⇒ the
    /// surface follows whatever is playing.</summary>
    public EntityId ReaderEpisode { get; }

    public ILyricsSceneSink? Sink { get; set; }

    public ILyricsVerbs Verbs { get; }

    // ══ 1. WHAT IS ON SCREEN ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The surface's state as of the last publication (see <see cref="Classify"/>).</summary>
    public readonly Signal<LyricsState> State = new(LyricsState.Nothing);

    /// <summary>PURE: the state of a surface. Nothing playing ⇒ <see cref="LyricsState.Nothing"/>; no lyrics id ⇒
    /// NoLyrics; a document still on its way ⇒ Loading; a failed fetch ⇒ Failed; a ready document with no lines ⇒ NoLyrics;
    /// an untimed one — or a timed one while a video suppresses sync — ⇒ Unsynced; else Synced.</summary>
    public static LyricsState Classify(bool anything, bool hasTrack, bool pending, bool failed, Lyrics.Doc? doc, bool syncSuppressed)
    {
        if (!anything) return LyricsState.Nothing;
        if (!hasTrack) return LyricsState.NoLyrics;
        if (pending) return LyricsState.Loading;
        if (failed) return LyricsState.Failed;
        if (doc is not { Lines.Count: > 0 }) return LyricsState.NoLyrics;
        return !Lyrics.IsTimed(doc) || syncSuppressed ? LyricsState.Unsynced : LyricsState.Synced;
    }

    /// <summary>The state of a document that has landed (the load answered).</summary>
    public static LyricsState ClassifyReady(Lyrics.Doc? doc, bool podcast)
        => Classify(anything: true, hasTrack: true, pending: false, failed: false, doc,
            Lyrics.SyncGate.SyncSuppressed(Playback.VideoActive.Peek(), podcast));

    public void PublishState(LyricsState state) => State.SetIfChanged(state);

    // ── identity ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playable the surface shows: the reader's episode, else whatever plays.</summary>
    public EntityId Subject { get; set; }

    public uint SubjectEpoch { get; set; }

    /// <summary>The lyrics-store id of the song on screen ("" for an episode, nothing, or no id yet).</summary>
    public string TrackId { get; set; } = "";

    public bool IsPodcast => Subject.Kind == EntityKind.Episode;
    public bool OwnsPlayback => Subject == Playback.CurrentId.Peek();
    public bool ObservedPlaybackOwner => Subject == Playback.CurrentId.Value;

    /// <summary>The lyrics-store id of the song that plays right now ("" when none): what the AI sparkle and its verbs ask
    /// for. Subscribing when read in a render.</summary>
    public static string PlayingTrackId()
    {
        var current = Playback.Current.Value;
        return current.Kind == EntityKind.Track && !current.IsNone ? Lyrics.Store.IdOf(new Track(current.Slot)) : "";
    }

    // ── the document slot and the upgrade gate ────────────────────────────────────────────────────────────────────────

    public Lyrics.Doc? Doc { get; set; }

    /// <summary>A richer document that landed mid-line, HELD until the next hand-off.</summary>
    public Lyrics.Doc? PendingUpgrade { get; set; }

    /// <summary>A replacement document arrived while one is held or shown. Not richer (<see cref="Lyrics.Authority.IsRicher"/>)
    /// ⇒ ignored; richer and the moment is right (<see cref="Lyrics.Authority.ApplyImmediately"/>: paused, or no line lit) ⇒
    /// apply now; otherwise it is HELD (<see cref="PendingUpgrade"/>) until the next hand-off.</summary>
    /// <param name="current">The document on the loadable (else <see cref="Doc"/>).</param>
    public UpgradeVerdict OfferUpgrade(Lyrics.Doc upgrade, Lyrics.Doc? current)
    {
        if (upgrade.Lines.Count == 0) return UpgradeVerdict.Ignore;
        current ??= Doc;
        if (current is not null && !Lyrics.Authority.IsRicher(upgrade, current)) return UpgradeVerdict.Ignore;
        if (Lyrics.Authority.ApplyImmediately(Playback.IsPlaying.Peek(), Doc, ActiveLine.Peek())) return UpgradeVerdict.Apply;
        PendingUpgrade = upgrade;
        return UpgradeVerdict.Hold;
    }

    // ══ 2. WHERE THE SONG IS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Emphasis + follow target (lead-shifted).</summary>
    public readonly Signal<int> ActiveLine = new(-1);

    /// <summary>The line being sung on TRUE time (wipe + glow).</summary>
    public readonly Signal<int> VoiceLine = new(-1);

    /// <summary><see cref="ActiveLine"/> as a bound value (a view binds the index without re-rendering).</summary>
    public Prop<int> ActiveLineProp { get; }

    /// <summary>The media time of the last step.</summary>
    public readonly FloatSignal NowMs = new(0f);

    /// <summary>The voice line's word progress (0..1, <see cref="Lyrics.Wipe.ComputeSplit"/>) at <see cref="NowMs"/>, as a bound
    /// value: it re-evaluates when the step publishes a new time and is 0 with no voice line.</summary>
    public Prop<float> VoiceWordProgress { get; }

    float WordProgressNow()
    {
        int voice = VoiceLine.Value;
        var doc = Doc;
        long now = (long)NowMs.Value;
        return doc is null || (uint)voice >= (uint)doc.Lines.Count ? 0f : WordProgress(doc.Lines[voice], now);
    }

    /// <summary>PURE: a line's word progress (0..1) at a media time.</summary>
    public static float WordProgress(Lyrics.Line line, long nowMs) => Lyrics.Wipe.ComputeSplit(line, nowMs);

    /// <summary>The media clock: a (position, stamp) sample from the playback host, mapped onto the frame clock.</summary>
    public Lyrics.MediaClock Clock { get; } = new();

    long _lastSampleStamp = long.MinValue;   // the host sample (PosQpc ms stamp) already fed to the clock
    int _lastSamplePos = int.MinValue;
    long _lastClockLogMs;                    // FrameTime.NowMs of the last `lyrics.clock` line (0 = not baselined)

    /// <summary>Seed the media clock from an authoritative position, and absorb the host's CURRENT sample so the next step
    /// does not re-treat our own re-anchor as a disagreement.</summary>
    public void RebaseClock(long positionMs)
    {
        var snap = Playback.Snap();
        Clock.Reset(positionMs, FrameTime.NowQpc, OwnsPlayback && Playback.IsPlaying.Peek(), snap.ContentRate);
        _lastSampleStamp = snap.PosQpc;
        _lastSamplePos = snap.PosMs;
    }

    /// <summary>A clear resets the clock too: a port that keeps it across a track change re-treats the next document's first
    /// sample as a &gt;250 ms disagreement.</summary>
    public void ResetClock()
    {
        Clock.Reset(0L, FrameTime.NowQpc, false);
        _lastSampleStamp = long.MinValue;
        _lastSamplePos = int.MinValue;
    }

    /// <summary>The media time of THIS frame. The host's authoritative (position, stamp) sample is fed only when it changes
    /// and mapped onto QPC through its AGE; then the clock is queried at the frame's present time. A paused or unowned
    /// surface reads the authoritative position. <paramref name="probeNowMs"/> (the advance probe) drives the clock
    /// synchronously instead. <paramref name="jumped"/> is true when the sample SNAPPED the clock (the first sample, a real
    /// seek / transfer / track change): the view then treats the next follow as a fresh landing.</summary>
    public long ReadMediaTime(long probeNowMs, out bool playing, out bool jumped)
    {
        jumped = false;
        long auth = OwnsPlayback ? Playback.PositionMs.Peek() : 0;
        playing = OwnsPlayback && Playback.IsPlaying.Peek();
        if (probeNowMs != long.MinValue)
        {
            playing = true;
            Clock.Reset(probeNowMs, FrameTime.NowQpc, true);
            return probeNowMs;
        }
        var snap = Playback.Snap();
        if (snap.PosQpc != _lastSampleStamp || snap.PosMs != _lastSamplePos || snap.ContentRate != Clock.ContentRate)
        {
            _lastSampleStamp = snap.PosQpc;
            _lastSamplePos = snap.PosMs;
            long sampleQpc = Lyrics.SampleClock.SampleQpc(snap.PosQpc, Playback.FrameNowMs(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            if (Clock.OnSample(snap.PosMs, sampleQpc, playing, snap.ContentRate)) jumped = true;
        }
        else if (playing != Clock.Playing)
        {
            // The play state flipped with no fresh sample: feed the EDGE at the authoritative position NOW.
            if (Clock.OnSample(auth, Stopwatch.GetTimestamp(), playing, snap.ContentRate)) jumped = true;
        }
        return playing ? Clock.At(FrameTime.NowQpc) : auth;
    }

    /// <summary>Always-on (never an env switch): one line per 30 s of wall time while a timed document plays.</summary>
    public void NoteClock(bool playing, long wallMs)
    {
        if (!playing) return;
        if (_lastClockLogMs == 0L) { _lastClockLogMs = wallMs; return; }
        if (wallMs - _lastClockLogMs < 30_000) return;
        _lastClockLogMs = wallMs;
        var diag = Clock.ReadAndResetDiagnostics();
        Log.Event(WaveeLogLevel.Info, Lyrics.Diag.Category, "lyrics.clock",
            "media clock: " + diag.Frames + " frames, " + diag.Snaps + " snap(s)",
            fields:
            [
                WaveeLogField.Of("frames", diag.Frames),
                WaveeLogField.Of("zeroAdvanceFrames", diag.ZeroAdvanceFrames),
                WaveeLogField.Of("maxStepMs", diag.MaxStepMs),
                WaveeLogField.Of("slewMs", diag.SlewMs),
                WaveeLogField.Of("snaps", diag.Snaps),
            ]);
    }

    /// <summary>The document's TRUE opening line (through the same interlude advance the frame uses): -1 for an untimed
    /// document and for a podcast reader that does not own playback.</summary>
    public int SeedActive(Lyrics.Doc doc, long posMs)
        => Lyrics.IsTimed(doc) && (!IsPodcast || OwnsPlayback)
            ? Lyrics.AdvancePastInterlude(doc, Lyrics.ResolveLine(doc.Lines, posMs), posMs, out _, out _)
            : -1;

    /// <summary>Publish a seeded opening: the active line, and the voice line retired for an untimed or non-owning surface.
    /// (The caller writes <see cref="NowMs"/> after it repacks its rows, as the surface always did.)</summary>
    public void PublishSeed(Lyrics.Doc doc, int seedActive)
    {
        ActiveLine.Value = seedActive;
        if (!Lyrics.IsTimed(doc) || (IsPodcast && !OwnsPlayback)) VoiceLine.Value = -1;
    }

    /// <summary>Resolve one frame at <paramref name="nowMs"/>: eye leads voice (emphasis + follow on the LEAD-shifted clock,
    /// wipe + glow on TRUE time); a line stops being the voice at its own SUNG-OUT point, not when the next one starts; a real
    /// break RETIRES the finished line (applied BEFORE the changed test, so the onset is a normal hand-off). Writes the
    /// active line, the voice line and the time.</summary>
    public LyricsFrame Step(Lyrics.Doc doc, long nowMs)
    {
        int active = Lyrics.ResolveLine(doc.Lines, nowMs + (IsPodcast ? 0 : Lyrics.LeadMs));
        int voiceLine = Lyrics.ResolveLine(doc.Lines, nowMs);
        if (voiceLine >= 0 && nowMs >= Lyrics.SungOutMs(doc, voiceLine)) voiceLine = -1;
        active = Lyrics.AdvancePastInterlude(doc, active, nowMs, out long gapStart, out long gapEnd);
        bool activeChanged = active != ActiveLine.Peek();
        if (activeChanged) ActiveLine.Value = active;
        int prevVoice = VoiceLine.Peek();
        bool voiceChanged = voiceLine != prevVoice;
        if (voiceChanged) VoiceLine.Value = voiceLine;
        NowMs.Value = nowMs;
        return new LyricsFrame(nowMs, active, voiceLine, prevVoice, gapStart, gapEnd, activeChanged, voiceChanged);
    }

    /// <summary>The surface has nothing to follow right now (a video suppresses sync): no lit line, no voice.</summary>
    public void RetireLines()
    {
        if (ActiveLine.Peek() != -1) ActiveLine.Value = -1;
        if (VoiceLine.Peek() != -1) VoiceLine.Value = -1;
    }

    // ══ 3. THE FOLLOW AND THE RESYNC ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The follow scroll's mode: Following, DetachedActive (the user holds the scroll), DetachedIdle (let go; the
    /// countdown runs), Resyncing (the glide back).</summary>
    public readonly Signal<Lyrics.FollowMode> Follow = new(Lyrics.FollowMode.Following);

    /// <summary>The resync pill's countdown ring (1 = full).</summary>
    public readonly FloatSignal ResyncProgress = new(1f);

    long _resyncDeadlineMs;

    /// <summary>Is the Resync pill up? The follow is detached and this surface tracks playback. Subscribing when read.</summary>
    public bool ResyncNeeded
        => (!IsPodcast || ObservedPlaybackOwner) && Follow.Value is Lyrics.FollowMode.DetachedActive or Lyrics.FollowMode.DetachedIdle;

    /// <summary>A detached follow draws no depth of field (the user owns the scroll).</summary>
    public static bool SuppressesDof(Lyrics.FollowMode mode) => mode != Lyrics.FollowMode.Following;

    /// <summary>Write the follow mode. Leaving Following retires the cascade, the dots and their reserved band — at the
    /// detach EDGE, so the exits animate (<see cref="FollowChange.LeftFollowing"/>).</summary>
    public FollowChange SetFollowMode(Lyrics.FollowMode next)
    {
        var previous = Follow.Peek();
        if (previous == next) return default;
        bool flipped = SuppressesDof(previous) != SuppressesDof(next);
        Follow.Value = next;
        return new FollowChange(true, next != Lyrics.FollowMode.Following, flipped);
    }

    /// <summary>Stop the resync countdown.</summary>
    public void ClearCountdown()
    {
        _resyncDeadlineMs = 0L;
        ResyncProgress.Value = 1f;
    }

    /// <summary>The user-scroll EDGE. A reader that does not own playback has no follow to detach. A grab detaches (the view
    /// retires the cascade, then writes DetachedActive); a release from DetachedActive starts the idle countdown (the view
    /// writes DetachedIdle).</summary>
    public ScrollEdge OnScrollActivity(bool userScrollActive, long nowMs)
    {
        if (IsPodcast && !OwnsPlayback) return ScrollEdge.None;
        if (userScrollActive)
        {
            ClearCountdown();
            return ScrollEdge.Detach;
        }
        if (Follow.Peek() != Lyrics.FollowMode.DetachedActive) return ScrollEdge.None;
        _resyncDeadlineMs = nowMs + Lyrics.Follow.ResyncIdleMs;
        ResyncProgress.Value = 1f;
        return ScrollEdge.Idle;
    }

    /// <summary>One frame of the countdown: DetachedIdle publishes the ring and, once it runs out, asks to begin the resync;
    /// Resyncing asks to keep driving the glide.</summary>
    public FollowTick TickFollow(long nowMs)
    {
        var mode = Follow.Peek();
        if (mode == Lyrics.FollowMode.DetachedIdle)
        {
            ResyncProgress.SetIfChanged(Lyrics.Follow.ResyncProgress(_resyncDeadlineMs, nowMs));
            if (Lyrics.Follow.ResyncDue(_resyncDeadlineMs, nowMs)) return FollowTick.BeginResync;
        }
        return mode == Lyrics.FollowMode.Resyncing ? FollowTick.DriveResync : FollowTick.None;
    }

    // ══ 4. THE INSTRUMENTAL BREAK ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The three breathing dots are up (an overlay, not a row).</summary>
    public readonly Signal<bool> DotsShown = new(false);

    /// <summary>The dots' fills (value-gated by the view's frame drive).</summary>
    public FloatSignal[] DotAlpha { get; }

    /// <summary>The line whose top reserves the dots' band (-1 = none).</summary>
    public int InterludeReserveLine { get; set; } = -1;

    /// <summary>Frames the follow re-evaluates after a reserve edge (the height reaches the extent table next arrange).</summary>
    public int ReserveRelatchFrames { get; set; }

    /// <summary>The height reserved on a line (the dots' band) for the follow's target.</summary>
    public float ReserveOf(int index) => index >= 0 && index == InterludeReserveLine ? Lyrics.Interlude.ReserveDip(Surface.Large) : 0f;

    /// <summary>Move the reserved band to <paramref name="line"/>; false when it is already there. On a change the relatch
    /// frames are armed and <paramref name="previous"/> is the line it left (the view repacks both lines' emphasis).</summary>
    public bool SetReserve(int line, out int previous)
    {
        previous = InterludeReserveLine;
        if (previous == line) return false;
        InterludeReserveLine = line;
        ReserveRelatchFrames = Lyrics.Follow.ReserveRelatchFrames;
        return true;
    }

    /// <summary>The dots' edge: up while following through a real break (<see cref="Lyrics.Interlude.DotsUp"/>).</summary>
    public bool DotsUp(long gapStart, long gapEnd, long nowMs)
        => Lyrics.Interlude.DotsUp(Follow.Peek() == Lyrics.FollowMode.Following, gapStart, gapEnd, nowMs);

    /// <summary>Release the band when the line it was armed on is no longer the active one (release-then-arm).</summary>
    public bool ShouldReleaseReserve(bool activeChanged, int active)
        => activeChanged && InterludeReserveLine >= 0 && InterludeReserveLine != active;

    /// <summary>Arm the band on the active line while the dots are up.</summary>
    public bool ShouldArmReserve(bool dotsUp, int active) => dotsUp && InterludeReserveLine != active;

    /// <summary>Spend one relatch frame; true when this frame owes the follow a re-evaluation.</summary>
    public bool TakeRelatchFrame()
    {
        if (ReserveRelatchFrames <= 0) return false;
        ReserveRelatchFrames--;
        return true;
    }

    // ══ 5. THE SECOND LINE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The secondary-line mode, republished for the rows (a SIGNAL: a ctor int would never reach a mounted row).</summary>
    public readonly Signal<int> SecondLine = new(Lyrics.Prefs.None);

    /// <summary>ONE read of the session's secondary-line mode under the lyrics epoch, republished to the rows.</summary>
    public int SyncSecondLine()
    {
        int mode = Lyrics.Prefs.SecondaryLine();
        SecondLine.Value = mode;
        return mode;
    }

    /// <summary>The header toggle's capability. Gated on a TIMED document: an unsynced block has no rows to render a second
    /// line in, so offering the toggle there would be a control that visibly does nothing. A reader never publishes it.</summary>
    public void PublishSecondaryAvailability(Lyrics.Doc? doc)
    {
        if (!ReaderEpisode.IsValid) Lyrics.Prefs.Available.Value = doc is not null && Lyrics.IsTimed(doc) ? doc.SecondaryAvailable : 0;
    }

    // ══ 7. THE VERBS THAT NEED THIS MODEL ════════════════════════════════════════════════════════════════════════════

    /// <summary>A line was tapped: back to live, commit the seek (the playing episode seeks; a reader's other episode starts
    /// at the line), re-anchor the clock, and hard-latch the follow. A line tap is a commit, never a scrub preview.</summary>
    public void SeekToLine(int index)
    {
        var doc = Doc;
        if (doc is null || (uint)index >= (uint)doc.Lines.Count) return;
        Sink?.ResetFollow();
        long ms = doc.Lines[index].StartMs;
        int target = (int)Math.Clamp(ms, 0L, int.MaxValue);
        if (IsPodcast && !OwnsPlayback)
            Episode.StartAt(Entities.Episode(Subject), Playback.EpisodeStartKind.Position, target);
        else Playback.SeekTo(target);
        RebaseClock(ms);
        Sink?.SeekLanded();
    }
}

/// <summary>The verbs, bound to a model (line seek, resync) or to none (the header's: the second-line cycle, the AI
/// sparkle, settings and the stage work from the preferences and the playing song alone).</summary>
public sealed class LyricsVerbs(LyricsModel? model) : ILyricsVerbs
{
    /// <summary>The model-less verbs, for headers that have no view model (the rail's title row).</summary>
    public static readonly LyricsVerbs Global = new(null);

    public void SeekToLine(int index) => model?.SeekToLine(index);

    public void ToggleSecondLine()
    {
        int available = Lyrics.Prefs.Available.Peek();
        int secondary = Lyrics.Prefs.SecondaryLine();
        Lyrics.Prefs.SetSecondaryLine(Lyrics.Prefs.Next(secondary, available));
    }

    public void Resync() => model?.Sink?.ResyncRequested();

    public void ToggleAiPreferred()
    {
        string id = model is { } m ? (m.ReaderEpisode.IsValid ? "" : LyricsModel.PlayingTrackId()) : LyricsModel.PlayingTrackId();
        if (id.Length == 0) return;
        AiLyrics.SetPreferAi(id, !AiLyrics.PrefersAi(id));
    }

    public void OpenAiSettings() => Settings.Open(Settings.Tab.Appearance);

    public void ExpandToStage(Action<string>? begin, string cause) => Stage.Open(begin, cause);
}
