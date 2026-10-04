// ── Playback/Playback.ParkAndCut.cs ─────────────────────────────────────────────────────────────────────────────────
// The audio/video hand-off: the executor's half of F151 (plan §3.1.4, §6.2 G.3). `Playback.Host.cs`'s `LoadHost` hands
// every load to `ParkAndCut.Load`; the pure rules it follows are `MediaSwitch.PlanLoad` / `Handoff` / `JudgeAttach`.
//
// Role: SHELL
// Owner: H
// Wave: 3
// Budget: 420 lines
//
// THE SHAPE. Turning the video on for a PLAYING song used to stop the song at the click and leave the listener in silence
// for the whole resolve -> licence -> attach -> first frame. It no longer does:
//
//   Audio -> Video   BEGIN. The song is not stopped: the audio pump ADOPTS the video's load epoch (so its Position reports
//                    stay current), the video opens MUTED and PAUSED at the carried position, and its own transport reports
//                    are held back (`Video.HoldReports`). The window is open: `Attaching`.
//   the cut          The video's tick judges the window (`MediaSwitch.JudgeAttach`) and, on a presented frame, asks for
//                    `Cut`: the song is PARKED (paused and kept, never disposed), the video is unmuted and goes to the
//                    audio clock plus the fade (`AudioHandoff.CutAtMs`) and plays. One decoder is audible at a time, always:
//                    the video is silent until the cut, the song is paused by it.
//   a fault          Before the cut a video fault, an unavailable source or an attach that never presents a frame is the
//                    VIDEO's problem only: it retries once, then is torn down and the song carries on (`KeepAudio`), never
//                    silenced. An Ended or a new load while attaching ends the window and stops both hosts.
//   Video -> Audio   RESUME. The parked song resumes in place at the video's position (`Audio.Unpark`) and the video is
//                    only paused and silenced: its player stays bound (F152) and, for a short TTL, its session too, so a
//                    quick re-toggle is a seek and a play. Past `AudioHandoff.ParkTtlMs` (or a different row, or any other
//                    load) the parked song is disposed and the load takes the ordinary cold path.
//
// RESOURCE BOUNDS. A parked song holds a session and its byte source; a paused PlayReady video holds memory and a hardware
// secure decoder. Both are TTL-bounded (one timer, `s_ttl`) and both are released before any load that is not their own
// row's re-toggle, so two secure decoders never coexist. All state is UI-thread owned; the timer only marshals to it.
//
// THE SEAM. Every host call goes through `IHandoffHosts`; `LiveHosts` is the real Audio/Video pair and a test installs a
// recorder, which is how `PlaybackHandoffTests` pins the ORDER of the calls without a device.

namespace Wavee;

public static partial class Playback
{
    /// <summary>The hand-off executor: the attach window, the park, the cut and the way back (F151).</summary>
    public static class ParkAndCut
    {
        /// <summary>How long the paused video the way back leaves bound keeps its session before it is torn down. Short on
        /// purpose: it holds a secure decoder, and some GPUs allow very few.</summary>
        public const int ParkedVideoTtlMs = 10_000;

        /// <summary>The two hosts as the hand-off drives them.</summary>
        public interface IHandoffHosts
        {
            // ── the song ──
            /// <summary>A live session with play intent, not parked and not faulted.</summary>
            bool AudioAudible { get; }
            /// <summary>A session the hand-off can still keep: open, not parked, not faulted (paused counts).</summary>
            bool AudioAlive { get; }
            /// <summary>The load epoch the live song reports under.</summary>
            uint AudioEpoch { get; }
            /// <summary>The song's playhead on the pump's own clock.</summary>
            long AudioClockMs { get; }
            /// <summary>The live session's sample rate (0 when none).</summary>
            int AudioSampleRate { get; }
            /// <summary>Report the live song under <paramref name="toEpoch"/> from now on; false when it is not on <paramref name="fromEpoch"/>.</summary>
            bool AudioAdopt(uint fromEpoch, uint toEpoch);
            /// <summary>The adopted song announces itself again under its new epoch.</summary>
            void AudioReannounce();
            void AudioLoad(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, uint seekGen);
            void AudioPause();
            /// <summary>Stop and dispose the session (a parked one included).</summary>
            void AudioStop();
            /// <summary>Pause and KEEP the session; false when there was none.</summary>
            bool AudioPark();
            /// <summary>Resume the parked song for <paramref name="toEpoch"/> at <paramref name="atMs"/>; false when nothing is parked under <paramref name="fromEpoch"/>.</summary>
            bool AudioUnpark(uint fromEpoch, uint toEpoch, int atMs, uint gen, bool play);

            // ── the video ──
            /// <summary>Resolve and load the row's video; <paramref name="muted"/> and <paramref name="paused"/> are the attach's.</summary>
            void VideoLoad(EntityRef row, EntityId id, uint epoch, int fromMs, bool paused, uint seekGen, bool muted);
            /// <summary>Hold (or release) the attach window's report hold.</summary>
            void VideoHold(bool hold);
            /// <summary>The attach is over: the player returns to the user's own mute.</summary>
            void VideoReleaseMute();
            /// <summary>Silence the player now (the song is coming back).</summary>
            void VideoSilence();
            /// <summary>Pause without releasing the session: the player and its session stay bound.</summary>
            void VideoPause();
            /// <summary>Stop and release the session.</summary>
            void VideoStop();
            /// <summary>Seek to <paramref name="atMs"/> and play.</summary>
            void VideoGo(long atMs);
            /// <summary>Seek to <paramref name="atMs"/> and leave the transport alone.</summary>
            void VideoSeek(long atMs);
            /// <summary>Stamp the video's reports with the reducer's current seek generation.</summary>
            void VideoSyncSeekGen(uint gen);
            /// <summary>Arm the one-shot gap probe for the cut that is about to start the video at <paramref name="pcMs"/> (the
            /// song was at <paramref name="songPosMs"/>): the video's own tick measures the gap once it plays, and logs it.</summary>
            void VideoArmCutProbe(long pcMs, long songPosMs);
        }

        /// <summary>The real hosts. Replaced only by a test (<see cref="ResetForTests"/> puts it back).</summary>
        public static IHandoffHosts Hosts { get; set; } = new LiveHosts();

        // ── the state (UI thread) ───────────────────────────────────────────────────────────────────────────────────

        // The attach window: a video is attaching under the song that still plays, which is adopted under `s_attachEpoch`.
        static bool s_attaching;
        static EntityRef s_attachRow;
        static uint s_attachEpoch;
        // What the LISTENER wants for the video: a Pause while attaching goes to the song, and is remembered here so the cut
        // does not start a video the listener paused.
        static bool s_attachWantsPlay;

        // The parked song: kept paused while the video plays, resumable inside its TTL.
        static bool s_audioParked;
        static EntityRef s_parkedRow;
        static uint s_parkedEpoch;
        static long s_parkedAtMs;

        // The parked video: the way back left it paused and bound for a re-toggle.
        static bool s_videoParked;
        static EntityRef s_videoRow;
        static long s_videoParkedAtMs;

        static Timer? s_ttl;
        static readonly Action s_expire = Expire;

        /// <summary>A video is attaching under the song that still plays: transport belongs to the song.</summary>
        public static bool Attaching => s_attaching;

        // ── the decision and its executor ───────────────────────────────────────────────────────────────────────────

        /// <summary>Execute one load, the hand-off included. UI thread.</summary>
        public static void Load(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, bool paused, uint seekGen, LoadOrigin origin)
        {
            long now = FrameNowMs();
            bool attaching = s_attaching;
            bool songKept = attaching ? Hosts.AudioAlive : s_hostKind == PlayableKind.Audio && Hosts.AudioAudible;
            long parkedAge = s_audioParked && row == s_parkedRow ? now - s_parkedAtMs : -1;
            switch (MediaSwitch.PlanLoad(attaching, attaching && row == s_attachRow, s_hostKind, kind, origin, songKept, parkedAge))
            {
                case MediaSwitch.LoadPlan.BeginAttach when BeginAttach(row, id, epoch, fromMs, paused, seekGen): return;
                case MediaSwitch.LoadPlan.RetryAttach when RetryAttach(row, id, epoch, fromMs, seekGen): return;
                case MediaSwitch.LoadPlan.KeepAudio when KeepAudio(kind, epoch, paused): return;
                case MediaSwitch.LoadPlan.ResumeParked:
                    ResumeParked(row, id, kind, epoch, fromMs, paused, seekGen, origin);
                    return;
                case MediaSwitch.LoadPlan.AbortAttach:
                    AbortAttach(kind);
                    break;
            }
            Plain(row, id, kind, epoch, fromMs, paused, seekGen);
        }

        /// <summary>The ordinary path: the outgoing host stops first when the host changes, what the hand-off kept alive is
        /// released, then the load runs on its own host.</summary>
        static void Plain(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, bool paused, uint seekGen)
        {
            if (MediaSwitch.HostChanges(s_hostKind, kind))
            {
                if (s_hostKind == PlayableKind.Video) Hosts.VideoStop();
                else Hosts.AudioStop();
            }
            ReleaseParked(kind, row);
            s_hostKind = kind;
            if (kind == PlayableKind.Video) { Hosts.VideoLoad(row, id, epoch, fromMs, paused, seekGen, muted: false); return; }
            Hosts.AudioLoad(row, id, kind, epoch, fromMs, seekGen);
            if (paused) Hosts.AudioPause();
        }

        /// <summary>Audio to Video under a playing song: the song is adopted under the video's epoch and keeps playing; the
        /// video attaches muted and paused. False when the song could not be adopted (the ordinary path then stops it).</summary>
        static bool BeginAttach(EntityRef row, EntityId id, uint epoch, int fromMs, bool paused, uint seekGen)
        {
            if (!Hosts.AudioAdopt(Hosts.AudioEpoch, epoch)) return false;
            if (s_videoParked && row != s_videoRow) Hosts.VideoStop();       // a different row's paused video never rides along
            s_videoParked = false;                                           // the same row's re-attaches below (seek and play)
            s_attaching = true;
            s_attachRow = row;
            s_attachEpoch = epoch;
            s_attachWantsPlay = !paused;
            s_hostKind = PlayableKind.Video;
            Hosts.AudioReannounce();
            Hosts.VideoHold(true);
            Hosts.VideoLoad(row, id, epoch, fromMs, paused: true, seekGen, muted: true);
            return true;
        }

        /// <summary>The attach faulted and the reducer retries it: the song keeps playing under the new epoch and the video
        /// re-opens muted and paused. False when the song is gone (the window is then aborted).</summary>
        static bool RetryAttach(EntityRef row, EntityId id, uint epoch, int fromMs, uint seekGen)
        {
            if (!Hosts.AudioAdopt(Hosts.AudioEpoch, epoch)) { AbortAttach(PlayableKind.Video); return false; }
            s_attachEpoch = epoch;
            Hosts.AudioReannounce();
            Hosts.VideoHold(true);                                           // the window's clock restarts with the retry
            Hosts.VideoLoad(row, id, epoch, fromMs, paused: true, seekGen, muted: true);
            return true;
        }

        /// <summary>The attach was demoted to audio (or the toggle undone before the cut): the video goes, the song — never
        /// stopped — is the row on the deck. False when the song is gone (the window is then aborted).</summary>
        static bool KeepAudio(PlayableKind kind, uint epoch, bool paused)
        {
            if (!Hosts.AudioAdopt(Hosts.AudioEpoch, epoch)) { AbortAttach(kind); return false; }
            s_attaching = false;
            Hosts.VideoHold(false);
            Hosts.VideoStop();
            s_hostKind = kind;
            Hosts.AudioReannounce();
            if (paused) Hosts.AudioPause();
            return true;
        }

        /// <summary>Another load while attaching, or a song that died: the window ends and both hosts stop. The host the
        /// load is for is already the one the deck is on, so the ordinary path stops nothing a second time.</summary>
        static void AbortAttach(PlayableKind next)
        {
            s_attaching = false;
            Hosts.VideoHold(false);
            Hosts.AudioStop();
            Hosts.VideoStop();
            s_hostKind = next;
        }

        /// <summary>Video to Audio with the song parked inside its TTL: the video goes silent and is paused (its player, and
        /// for a toggle its session, stay bound), then the parked song resumes at the video's position. A song that cannot
        /// resume is loaded cold.</summary>
        static void ResumeParked(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, bool paused, uint seekGen, LoadOrigin origin)
        {
            uint parkedEpoch = s_parkedEpoch;
            s_audioParked = false;
            Hosts.VideoSilence();                                            // two decoders are never both audible
            bool keepVideo = origin == LoadOrigin.MediaKindRefresh;          // a faulted video (VideoRecovery) is not worth keeping
            if (keepVideo) Hosts.VideoPause();
            else Hosts.VideoStop();
            s_hostKind = kind;
            if (Hosts.AudioUnpark(parkedEpoch, epoch, fromMs, seekGen, play: !paused))
            {
                if (keepVideo)
                {
                    s_videoParked = true;
                    s_videoRow = row;
                    s_videoParkedAtMs = FrameNowMs();
                    ArmTtl();
                }
                return;
            }
            Hosts.VideoStop();                                               // the song could not resume: the way back is the cold path
            Hosts.AudioStop();
            Hosts.AudioLoad(row, id, kind, epoch, fromMs, seekGen);
            if (paused) Hosts.AudioPause();
        }

        /// <summary>Release what a load that is NOT the hand-off's own leaves behind: the parked song always, the parked
        /// video unless this load re-attaches that very row's video.</summary>
        static void ReleaseParked(PlayableKind kind, EntityRef row)
        {
            if (s_audioParked) { s_audioParked = false; Hosts.AudioStop(); }
            if (!s_videoParked) return;
            s_videoParked = false;
            if (kind != PlayableKind.Video || row != s_videoRow) Hosts.VideoStop();
        }

        // ── the cut ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The video presented a frame for <paramref name="epoch"/> (the video tick's verdict, marshalled to the UI
        /// thread): park the song and start the video at the audio clock plus the fade. A window that has since ended (a
        /// fault, a newer load, a stop) makes this a no-op.</summary>
        public static void Cut(uint epoch)
        {
            if (!s_attaching || s_attachEpoch != epoch) return;
            s_attaching = false;
            long songPos = Hosts.AudioClockMs;
            long pc = Video.AudioHandoff.CutAtMs(songPos, Hosts.AudioSampleRate);
            if (Hosts.AudioPark())
            {
                s_audioParked = true;
                s_parkedRow = s_attachRow;
                s_parkedEpoch = epoch;
                s_parkedAtMs = FrameNowMs();
                ArmTtl();
            }
            Hosts.VideoSyncSeekGen(s_state.SeekGen);                         // a seek the listener made while the song played moved it
            Hosts.VideoReleaseMute();
            if (s_attachWantsPlay)
            {
                Hosts.VideoArmCutProbe(pc, songPos);                         // the gap is measured on the video's tick once it plays: a seek publishes its target at once
                Hosts.VideoGo(pc);
            }
            else
            {
                Hosts.VideoSeek(pc);                                         // the listener paused during the attach: the video waits with them
                Hosts.VideoPause();
            }
            Hosts.VideoHold(false);
        }

        // ── the executor's hooks ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The listener paused or resumed: while attaching the song takes it, and the intent is kept for the cut.</summary>
        public static void OnTransport(bool play)
        {
            if (s_attaching) s_attachWantsPlay = play;
        }

        /// <summary>A gapless join advanced the deck onto the next AUDIO row without a load: a video that was attaching is
        /// moot, and a parked video of the row that just ended is released.</summary>
        public static void OnAdopt()
        {
            if (s_attaching)
            {
                s_attaching = false;
                s_hostKind = PlayableKind.Audio;
                Hosts.VideoHold(false);
                Hosts.VideoStop();
            }
            if (!s_videoParked) return;
            s_videoParked = false;
            Hosts.VideoStop();
        }

        /// <summary>Stop the host that holds the row, and with it whatever the hand-off kept alive: the song a video is still
        /// attaching under, the parked song, the parked video.</summary>
        public static void Stop(PlayableKind host)
        {
            bool attaching = s_attaching, audioParked = s_audioParked, videoParked = s_videoParked;
            s_attaching = false;
            s_audioParked = false;
            s_videoParked = false;
            if (attaching) Hosts.VideoHold(false);
            if (host == PlayableKind.Video || videoParked) Hosts.VideoStop();
            if (host != PlayableKind.Video || attaching || audioParked) Hosts.AudioStop();
        }

        // ── the TTL ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Arm the one timer for the soonest expiry of whatever is parked. It only marshals to the UI thread; a fire
        /// after the park was released finds nothing to do.</summary>
        static void ArmTtl()
        {
            long now = FrameNowMs(), due = long.MaxValue;
            if (s_audioParked) due = Math.Min(due, Video.AudioHandoff.ParkTtlMs - (now - s_parkedAtMs) + 1);
            if (s_videoParked) due = Math.Min(due, ParkedVideoTtlMs - (now - s_videoParkedAtMs) + 1);
            if (due == long.MaxValue) return;
            s_ttl ??= new Timer(static _ => ToUi(s_expire), null, Timeout.Infinite, Timeout.Infinite);
            try { s_ttl.Change(Math.Max(1, due), Timeout.Infinite); } catch { /* disposed with the process */ }
        }

        /// <summary>UI thread (the TTL timer's marshalled callback): dispose whatever outlived its TTL, then re-arm for what is left.</summary>
        public static void Expire()
        {
            long now = FrameNowMs();
            if (s_audioParked && !Video.AudioHandoff.CanResumeParked(now - s_parkedAtMs)) { s_audioParked = false; Hosts.AudioStop(); }
            if (s_videoParked && now - s_videoParkedAtMs >= ParkedVideoTtlMs) { s_videoParked = false; Hosts.VideoStop(); }
            ArmTtl();
        }

        /// <summary>Put the executor back to nothing attached and nothing parked, on the real hosts. TESTS ONLY.</summary>
        public static void ResetForTests()
        {
            s_attaching = false;
            s_attachRow = default;
            s_attachEpoch = 0;
            s_attachWantsPlay = false;
            s_audioParked = false;
            s_parkedRow = default;
            s_parkedEpoch = 0;
            s_parkedAtMs = 0;
            s_videoParked = false;
            s_videoRow = default;
            s_videoParkedAtMs = 0;
            try { s_ttl?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* disposed with the process */ }
            Hosts = new LiveHosts();
        }

        // ── the real hosts ──────────────────────────────────────────────────────────────────────────────────────────

        sealed class LiveHosts : IHandoffHosts
        {
            public bool AudioAudible => Audio.IsAudible;
            public bool AudioAlive => Audio.IsLive;
            public uint AudioEpoch => Audio.LiveEpoch;
            public long AudioClockMs => Audio.ClockPositionMs();
            public int AudioSampleRate => Audio.LiveSampleRate;
            public bool AudioAdopt(uint fromEpoch, uint toEpoch) => Audio.TryAdoptHandOff(fromEpoch, toEpoch);
            public void AudioReannounce() => Audio.ReannounceStarted();
            public void AudioLoad(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, uint seekGen)
                => Audio.Load(row, id, kind, epoch, fromMs, seekGen);
            public void AudioPause() => Audio.Pause();
            public void AudioStop() => Audio.Stop();
            public bool AudioPark() => Audio.Park();
            public bool AudioUnpark(uint fromEpoch, uint toEpoch, int atMs, uint gen, bool play)
                => Audio.Unpark(fromEpoch, toEpoch, atMs, gen, play);

            public void VideoLoad(EntityRef row, EntityId id, uint epoch, int fromMs, bool paused, uint seekGen, bool muted)
                => LoadVideo(row, id, epoch, fromMs, paused, seekGen, muted);
            public void VideoHold(bool hold) => Video.HoldReports(hold);
            public void VideoReleaseMute() => Video.ReleaseAttachMute();
            public void VideoSilence() => Video.Silence();
            public void VideoPause() => Video.Pause();
            public void VideoStop() => Video.Stop();
            public void VideoGo(long atMs) => Video.Go(atMs);
            public void VideoSeek(long atMs) => Video.Seek(atMs, accurate: true);
            public void VideoSyncSeekGen(uint gen) => Video.SyncSeekGen(gen);
            public void VideoArmCutProbe(long pcMs, long songPosMs) => Video.ArmCutProbe(pcMs, songPosMs);
        }
    }
}
