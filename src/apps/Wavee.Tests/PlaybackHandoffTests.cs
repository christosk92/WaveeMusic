// -- Wavee.Tests/PlaybackHandoffTests.cs -- the audio/video hand-off's STAGE SEQUENCING (F151) -----------------------
//
// `MediaSwitchHandoffTests` (PlaybackRulesTests.cs) pins the pure decisions; these pin the ORDER of the calls the executor
// (`Playback.ParkAndCut`) makes on the two hosts, through a recording `IHandoffHosts` -- no device, no player, no clock:
//
//   * turning the video on under a playing song never stops or pauses the song before the video's first frame;
//   * a video fault, a retry or an undone toggle before the cut keeps the song and never loads audio cold;
//   * the cut parks the song (never disposes it), and the way back resumes it WITHOUT an audio load;
//   * nothing parked outlives its TTL, a different row's load, or a stop.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackHandoffTests : IDisposable
{
    static readonly EntityRef RowA = new(EntityKind.Track, 7);
    static readonly EntityRef RowB = new(EntityKind.Track, 8);
    static readonly EntityId NoId = default;

    const Playback.PlayableKind Audio = Playback.PlayableKind.Audio;
    const Playback.PlayableKind Video = Playback.PlayableKind.Video;
    const Playback.LoadOrigin Toggle = Playback.LoadOrigin.MediaKindRefresh;
    const Playback.LoadOrigin Recovery = Playback.LoadOrigin.VideoRecovery;

    readonly Rec _hosts = new();
    long _now;

    public PlaybackHandoffTests()
    {
        TestScope.Fresh();
        Playback.ToUi = static a => a();
        Playback.ResetForTests();
        _now = 1_000;
        Playback.FrameNowMs = () => _now;
        Playback.ParkAndCut.Hosts = _hosts;
    }

    public void Dispose()
    {
        Playback.ResetForTests();
        Playback.FrameNowMs = static () => Environment.TickCount64;
    }

    /// <summary>Records every call in order and answers from plain fields.</summary>
    sealed class Rec : Playback.ParkAndCut.IHandoffHosts
    {
        public readonly List<string> Calls = [];
        public bool Audible = true, Alive = true, AdoptOk = true, ParkOk = true, UnparkOk = true;
        public uint Epoch = 1;
        public long Clock = 83_000;
        public int Rate = 48_000;

        public bool AudioAudible => Audible;
        public bool AudioAlive => Alive;
        public uint AudioEpoch => Epoch;
        public long AudioClockMs => Clock;
        public int AudioSampleRate => Rate;
        public bool AudioAdopt(uint fromEpoch, uint toEpoch)
        {
            Calls.Add($"audio.adopt {fromEpoch}->{toEpoch}");
            if (!AdoptOk || fromEpoch != Epoch) return false;
            Epoch = toEpoch;
            return true;
        }
        public void AudioReannounce() => Calls.Add("audio.reannounce");
        public void AudioLoad(EntityRef row, EntityId id, Playback.PlayableKind kind, uint epoch, int fromMs, uint seekGen)
        {
            Calls.Add($"audio.load {epoch} @{fromMs}");
            Epoch = epoch;
        }
        public void AudioPause() => Calls.Add("audio.pause");
        public void AudioStop() => Calls.Add("audio.stop");
        public bool AudioPark() { Calls.Add("audio.park"); return ParkOk; }
        public bool AudioUnpark(uint fromEpoch, uint toEpoch, int atMs, uint gen, bool play)
        {
            Calls.Add($"audio.unpark {fromEpoch}->{toEpoch} @{atMs} play={play}");
            if (!UnparkOk) return false;
            Epoch = toEpoch;
            return true;
        }

        public void VideoLoad(EntityRef row, EntityId id, uint epoch, int fromMs, bool paused, uint seekGen, bool muted)
            => Calls.Add($"video.load {epoch} @{fromMs} paused={paused} muted={muted}");
        public void VideoHold(bool hold) => Calls.Add($"video.hold {hold}");
        public void VideoReleaseMute() => Calls.Add("video.releasemute");
        public void VideoSilence() => Calls.Add("video.silence");
        public void VideoPause() => Calls.Add("video.pause");
        public void VideoStop() => Calls.Add("video.stop");
        public void VideoGo(long atMs) => Calls.Add($"video.go @{atMs}");
        public void VideoSeek(long atMs) => Calls.Add($"video.seek @{atMs}");
        public void VideoSyncSeekGen(uint gen) => Calls.Add("video.syncgen");
        public void VideoArmCutProbe(long pcMs, long songPosMs) => Calls.Add($"video.armprobe pc={pcMs} song={songPosMs}");
    }

    int IndexOf(string call)
    {
        int i = _hosts.Calls.FindIndex(c => c.StartsWith(call, StringComparison.Ordinal));
        Assert.True(i >= 0, $"expected a '{call}' call; got [{string.Join(" | ", _hosts.Calls)}]");
        return i;
    }

    void Expect(params string[] calls) => Assert.Equal(calls, _hosts.Calls.ToArray());

    bool Called(string call) => _hosts.Calls.Exists(c => c.StartsWith(call, StringComparison.Ordinal));

    static void Load(Playback.PlayableKind kind, uint epoch, Playback.LoadOrigin origin, EntityRef? row = null, int fromMs = 83_000, bool paused = false)
        => Playback.ParkAndCut.Load(row ?? RowA, NoId, kind, epoch, fromMs, paused, seekGen: 1, origin);

    /// <summary>The song plays; the toggle turns the video on (epoch 2); the window is open.</summary>
    void BeginAttach()
    {
        Load(Audio, 1, Playback.LoadOrigin.Claim);                    // the song, playing under epoch 1
        _hosts.Calls.Clear();
        Load(Video, 2, Toggle);
        Assert.True(Playback.ParkAndCut.Attaching);
    }

    [Fact]
    public void Turning_the_video_on_keeps_the_song_playing_until_the_first_frame()
    {
        BeginAttach();

        // The song is adopted under the video's epoch and re-announced; the video opens MUTED and PAUSED and its reports are held.
        Expect("audio.adopt 1->2", "audio.reannounce", "video.hold True", "video.load 2 @83000 paused=True muted=True");
        Assert.False(Called("audio.stop"));       // no Audio.Stop / dispose, no pause: the song is audible the whole attach
        Assert.False(Called("audio.pause"));
        Assert.False(Called("audio.load"));
        Assert.False(Called("video.stop"));
    }

    [Fact]
    public void The_cut_parks_the_song_then_starts_the_video_at_the_audio_clock_plus_the_fade()
    {
        BeginAttach();
        _hosts.Calls.Clear();

        Playback.ParkAndCut.Cut(2);

        Assert.False(Playback.ParkAndCut.Attaching);
        Assert.True(IndexOf("audio.park") < IndexOf("video.releasemute"));
        Assert.True(IndexOf("video.releasemute") < IndexOf("video.go"));
        Assert.Equal("video.go @83080", _hosts.Calls[IndexOf("video.go")]);      // 83 000 + the 80 ms fade, on a whole sample
        Assert.True(IndexOf("video.go") < IndexOf("video.hold False"));
        // The gap is measured later, on the video's tick: the probe is armed with the cut position and the song's, BEFORE the start.
        Assert.Equal("video.armprobe pc=83080 song=83000", _hosts.Calls[IndexOf("video.armprobe")]);
        Assert.True(IndexOf("video.armprobe") < IndexOf("video.go"));
        Assert.False(Called("audio.stop"));        // parked, never disposed
    }

    [Fact]
    public void A_cut_for_a_window_that_already_ended_does_nothing()
    {
        BeginAttach();
        Load(Audio, 3, Playback.LoadOrigin.Advance, RowB);             // a Next: the window ends
        _hosts.Calls.Clear();

        Playback.ParkAndCut.Cut(2);

        Assert.Empty(_hosts.Calls);
    }

    [Fact]
    public void A_listener_who_paused_during_the_attach_gets_a_paused_video()
    {
        BeginAttach();
        Playback.ParkAndCut.OnTransport(play: false);                  // the pause went to the song
        _hosts.Calls.Clear();

        Playback.ParkAndCut.Cut(2);

        Assert.False(Called("video.go"));
        Assert.False(Called("video.armprobe"));                        // a video that waits with the listener has no gap to measure
        Assert.True(IndexOf("video.seek @83080") < IndexOf("video.pause"));
    }

    [Fact]
    public void A_video_fault_before_the_cut_retries_and_then_keeps_the_song_without_a_cold_audio_load()
    {
        BeginAttach();
        _hosts.Calls.Clear();

        Load(Video, 3, Recovery);                                      // the reducer's one retry
        Expect("audio.adopt 2->3", "audio.reannounce", "video.hold True", "video.load 3 @83000 paused=True muted=True");
        _hosts.Calls.Clear();

        Load(Audio, 4, Recovery);                                      // the retry failed: demoted to audio
        Assert.False(Playback.ParkAndCut.Attaching);
        Expect("audio.adopt 3->4", "video.hold False", "video.stop", "audio.reannounce");
        Assert.False(Called("audio.stop"));
        Assert.False(Called("audio.load"));       // the song was never stopped, so nothing is reloaded: no silence
        Assert.False(Called("audio.pause"));
    }

    [Fact]
    public void Turning_the_video_off_again_before_the_cut_keeps_the_song_too()
    {
        BeginAttach();
        _hosts.Calls.Clear();

        Load(Audio, 3, Toggle);

        Assert.False(Playback.ParkAndCut.Attaching);
        Assert.False(Called("audio.stop"));
        Assert.False(Called("audio.load"));
        Assert.True(Called("video.stop"));
    }

    [Fact]
    public void A_song_that_dies_during_the_attach_ends_the_window_and_the_video_loads_on_its_own()
    {
        BeginAttach();
        _hosts.Alive = false;                                          // the audio fault under the adopted epoch
        _hosts.Calls.Clear();

        Load(Video, 3, Recovery);                                      // the reducer folded it as a video fault

        Assert.False(Playback.ParkAndCut.Attaching);
        Assert.True(Called("audio.stop"));
        Assert.True(Called("video.stop"));
        Assert.Equal("video.load 3 @83000 paused=False muted=False", _hosts.Calls[^1]);   // no hold, no mute: the video is the row now
    }

    [Fact]
    public void Another_load_while_attaching_stops_both_hosts_and_takes_the_ordinary_path()
    {
        BeginAttach();
        _hosts.Calls.Clear();

        Load(Audio, 3, Playback.LoadOrigin.Advance, RowB, fromMs: 0);   // a natural end of the song

        Assert.False(Playback.ParkAndCut.Attaching);
        Assert.True(IndexOf("video.hold False") < IndexOf("audio.load 3"));
        Assert.True(Called("audio.stop"));
        Assert.True(Called("video.stop"));
        Assert.Equal("audio.load 3 @0", _hosts.Calls[^1]);
    }

    [Fact]
    public void A_song_that_cannot_be_adopted_falls_back_to_the_ordinary_stop_first_path()
    {
        Load(Audio, 1, Playback.LoadOrigin.Claim);
        _hosts.Calls.Clear();
        _hosts.AdoptOk = false;

        Load(Video, 2, Toggle);

        Assert.False(Playback.ParkAndCut.Attaching);
        Expect("audio.adopt 1->2", "audio.stop", "video.load 2 @83000 paused=False muted=False");
    }

    [Fact]
    public void A_toggle_while_nothing_is_audible_stops_the_song_first()
    {
        Load(Audio, 1, Playback.LoadOrigin.Claim);
        _hosts.Audible = false;
        _hosts.Calls.Clear();

        Load(Video, 2, Toggle, paused: true);

        Assert.False(Playback.ParkAndCut.Attaching);
        Expect("audio.stop", "video.load 2 @83000 paused=True muted=False");
    }

    [Fact]
    public void The_way_back_resumes_the_parked_song_without_an_audio_load()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        _hosts.Calls.Clear();
        _now += 5_000;

        Load(Audio, 5, Toggle, fromMs: 88_000);

        // Silent first, the video only paused (its player and session stay bound), then the parked song resumes at the video position.
        Expect("video.silence", "video.pause", "audio.unpark 2->5 @88000 play=True");
        Assert.False(Called("audio.load"));
        Assert.False(Called("audio.stop"));
        Assert.False(Called("video.stop"));
    }

    [Fact]
    public void A_parked_song_that_cannot_resume_is_loaded_cold_and_the_video_is_torn_down()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        _hosts.Calls.Clear();
        _hosts.UnparkOk = false;

        Load(Audio, 5, Toggle, fromMs: 88_000);

        Assert.True(IndexOf("audio.unpark") < IndexOf("video.stop"));
        Assert.True(IndexOf("audio.stop") < IndexOf("audio.load 5 @88000"));
    }

    [Fact]
    public void A_song_parked_past_its_ttl_is_not_resumed()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        _hosts.Calls.Clear();
        _now += Playback.Video.AudioHandoff.ParkTtlMs + 1;

        Load(Audio, 5, Toggle, fromMs: 88_000);

        Assert.False(Called("audio.unpark"));
        Assert.True(IndexOf("video.stop") < IndexOf("audio.stop"));    // the outgoing host first, then the parked song, then the cold load
        Assert.True(IndexOf("audio.stop") < IndexOf("audio.load 5 @88000"));
    }

    [Fact]
    public void A_video_after_a_recovery_demotion_is_stopped_not_kept_when_the_parked_song_resumes()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        _hosts.Calls.Clear();

        Load(Audio, 5, Recovery, fromMs: 88_000);                      // the video faulted mid-play and was demoted

        Expect("video.silence", "video.stop", "audio.unpark 2->5 @88000 play=True");
    }

    [Fact]
    public void The_kept_video_is_torn_down_before_a_different_row_opens_and_kept_for_the_same_rows_retoggle()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        Load(Audio, 5, Toggle, fromMs: 88_000);                        // way back: the video stays paused and bound

        _hosts.Calls.Clear();
        Load(Video, 6, Toggle, fromMs: 88_000);                        // quick re-toggle: the same row's video is re-attached, never stopped
        Assert.False(Called("video.stop"));
        Assert.True(Playback.ParkAndCut.Attaching);

        Playback.ParkAndCut.Cut(6);
        Load(Audio, 7, Toggle, fromMs: 90_000);
        _hosts.Calls.Clear();
        Load(Audio, 8, Playback.LoadOrigin.Advance, RowB, fromMs: 0);  // a different row: the paused secure session must be gone first

        Assert.True(IndexOf("video.stop") < IndexOf("audio.load 8"));
    }

    [Fact]
    public void The_ttl_timer_expires_what_is_parked()
    {
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        Load(Audio, 5, Toggle, fromMs: 88_000);                        // video parked (short ttl)
        _hosts.Calls.Clear();
        _now += Playback.ParkAndCut.ParkedVideoTtlMs;

        Playback.ParkAndCut.Expire();

        Expect("video.stop");
    }

    [Fact]
    public void A_stop_releases_the_window_the_parked_song_and_the_parked_video()
    {
        BeginAttach();
        _hosts.Calls.Clear();
        Playback.ParkAndCut.Stop(Video);                               // a stop while attaching
        Assert.False(Playback.ParkAndCut.Attaching);
        Expect("video.hold False", "video.stop", "audio.stop");

        Playback.ResetForTests();
        Playback.ParkAndCut.Hosts = _hosts;
        BeginAttach();
        Playback.ParkAndCut.Cut(2);
        _hosts.Calls.Clear();
        Playback.ParkAndCut.Stop(Video);                               // a stop while the video plays over a parked song
        Expect("video.stop", "audio.stop");
    }

    [Fact]
    public void A_gapless_join_during_the_attach_ends_the_window()
    {
        BeginAttach();
        _hosts.Calls.Clear();

        Playback.ParkAndCut.OnAdopt();

        Assert.False(Playback.ParkAndCut.Attaching);
        Expect("video.hold False", "video.stop");
        _hosts.Calls.Clear();
        Playback.ParkAndCut.Cut(2);
        Assert.Empty(_hosts.Calls);
    }
}
