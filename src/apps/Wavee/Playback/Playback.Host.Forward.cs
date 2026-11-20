// ── Playback/Playback.Host.Forward.cs ────────────────────────────────────────────────────────────────────────────────
// A local play forwarded to the device that owns playback, under the Liked rule; the refusal any forward earns, which the
// shell says as one toast per verb kind; and the paced volume PUT (remote-play plan 1.1-1.4, 2.1, 2.3, 2026-11-20)
//
// Role: SHELL
// Owner: G
// Wave: remote-play fixes (2026-11-20)
// Budget: 300 lines
// Spec: the remote-play gap analysis of 2026-11-20 (§2.1, §2.8, §3, §5 phases 1-2) — a named partial of Playback.Host.cs
//
// THE SAME SHELL RULES AS THE PARENT: the UI thread owns the deck and the interner (C1), a send blocks and so runs on an api
// thread (`Spotify.Api.Run`), and what it learns comes back through `ToUi` (C9).
//
// THE PLAY FORWARD, ONE SEND FOR BOTH INTAKES:
//
//   a row click (DoPlay → ForwardPlay → SendRemote's PlayContext arm) ─┐
//   PlayContext(uri) while another device owns playback (ForwardPlayToOwner)  ─┴─▶ SendPlay ─▶ ForwardPlayEnvelope.Liked:
//       NotLiked  ─▶ `play` with the context as it is
//       Playlist  ─▶ `play` with the account's Liked Songs playlist, skip_to the clicked track, feature your_library
//       Unknown   ─▶ api thread: kind 246 on the clicked track (the newest liked one when it names none) ─▶ learned: kept
//                    (UI thread), then that `play` ─▶ none: NOTHING is sent (a collection context stops the phone) and the
//                    user is told
//
// A REFUSED FORWARD (2.1). Every forward's answer is used: a non-2xx (or a send that could not be queued) is noted on the UI
// thread as LastRemoteRefusal — its verb kind, the device, a sequence — and the shell turns it into one toast per kind,
// rate-limited (`Shell.RemoteRefusalToasts`). Nothing optimistic is invented or rolled back: the bar follows the owner's next
// cluster. Before, every caller dropped the answer and a refusal was silent (gap analysis 2.8).
//
// THE VOLUME PUT (2.3): leading + trailing, one gap (VolumePutGapMs) apart, the newest value always sent last; and none at all
// to an owner whose volume cannot be set (the reducer refuses it: `State.VolumeFixed`, 2.2).

using FluentGpu.Signals;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;

namespace Wavee;

public static partial class Playback
{
    // ── 1. a refused forward ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The kind of verb a refused forward was: what its toast says (one message per kind).</summary>
    public enum RemoteVerb : byte
    {
        None,
        /// <summary>A play the owner refused, or that could not be sent.</summary>
        Play,
        /// <summary>A Liked Songs play that was NOT sent: the account's Liked Songs playlist could not be learned (1.4).</summary>
        LikedUnknown,
        Pause,
        Resume,
        /// <summary>Next (a plain skip or a queue row) or previous.</summary>
        Skip,
        Seek,
        Shuffle,
        Repeat,
        /// <summary>Add to queue / play next.</summary>
        Queue,
        /// <summary>A volume PUT the owner refused.</summary>
        Volume,
        /// <summary>A volume key, a wheel or a mute for an owner whose volume cannot be set (<see cref="Playback.VolumeFixed"/>):
        /// nothing was sent.</summary>
        VolumeFixed,
        /// <summary>The audio ⇄ video switch.</summary>
        Video,
    }

    /// <summary>The verb kind of a player command (<see cref="Endpoint"/>'s verbs). PURE.</summary>
    static RemoteVerb VerbOf(RemoteCmd cmd) => cmd switch
    {
        RemoteCmd.Pause => RemoteVerb.Pause,
        RemoteCmd.Resume => RemoteVerb.Resume,
        RemoteCmd.SeekTo => RemoteVerb.Seek,
        RemoteCmd.SkipPrev or RemoteCmd.SkipNext or RemoteCmd.NextTrack => RemoteVerb.Skip,
        RemoteCmd.SetShufflingContext => RemoteVerb.Shuffle,
        RemoteCmd.SetRepeatingContext or RemoteCmd.SetRepeatingTrack => RemoteVerb.Repeat,
        _ => RemoteVerb.Play,
    };

    /// <summary>A forward that the owner refused or that never left: its verb kind, the device it was for (a roster hash) and
    /// a sequence that bumps with every refusal, so a watcher fires once per refusal — the same kind twice included.</summary>
    public readonly record struct RemoteRefusal(RemoteVerb Verb, ulong Device, uint Seq);

    /// <summary>The newest refused forward. The shell's toast watcher reads it (<c>Shell.RemoteRefusalToasts</c>).</summary>
    public static readonly Signal<RemoteRefusal> LastRemoteRefusal = new(default) { DebugName = "playback.remoteRefusal" };

    /// <summary>Note a refused forward. UI thread.</summary>
    static void NoteRemoteRefusal(RemoteVerb verb, ulong device)
    {
        Log.Warn("playback", "remote " + verb + " not done: the owner refused it or it was never sent");
        LastRemoteRefusal.Value = new RemoteRefusal(verb, device, LastRemoteRefusal.Peek().Seq + 1);
    }

    /// <summary>A forward's answer, on the api thread that sent it: a refusal goes to the UI thread (C9); a 2xx is nothing more
    /// (its <c>connect.cmd</c> line is written).</summary>
    static void Answered(RemoteVerb verb, ulong device, bool ok)
    {
        if (!ok) ToUi(() => NoteRemoteRefusal(verb, device));
    }

    /// <summary>Queue a forward on an api thread; a full queue sends nothing, and that is a refusal too. UI thread.</summary>
    static void RunForward(RemoteVerb verb, ulong device, Action send)
    {
        if (!Spotify.Api.Run(send)) NoteRemoteRefusal(verb, device);
    }

    // ── 2. the play forward ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE play forward, for both intakes: the reducer's row click (SendRemote's PlayContext arm) and
    /// <see cref="ForwardPlayToOwner"/>. <paramref name="context"/> is already the <see cref="ForwardPlayEnvelope.For"/> answer;
    /// the Liked rule (<see cref="ForwardPlayEnvelope.Liked"/>) decides what is sent. UI thread (the texts are the interner's,
    /// the known playlist is the scope's).</summary>
    static void SendPlay(string target, ulong device, EntityId context, EntityId track, bool shuffle, int fromMs, string? skipUid,
        bool video)
    {
        string trackUri = track.IsEmpty ? "" : track.Text;
        string liked = Spotify.Connect.KnownLikedPlaylist;
        switch (ForwardPlayEnvelope.Liked(context, liked))
        {
            case ForwardPlayEnvelope.LikedVerdict.NotLiked:
                {
                    string contextUri = context.Text;
                    Log.Info("playback", "play forwarded to the owner: context=" + contextUri + " track=" + trackUri + (video ? " video" : ""));
                    RunForward(RemoteVerb.Play, device, () => Answered(RemoteVerb.Play, device,
                        Spotify.Connect.PlayContext(target, contextUri, trackUri, shuffle, CancellationToken.None, fromMs, skipUid, video: video)));
                    return;
                }
            case ForwardPlayEnvelope.LikedVerdict.Playlist:
                Log.Info("playback", "play forwarded to the owner: context=" + liked + " (Liked Songs) track=" + trackUri);
                RunForward(RemoteVerb.Play, device, () => Answered(RemoteVerb.Play, device,
                    PlayLiked(target, liked, trackUri, shuffle, fromMs, skipUid, video)));
                return;
            default:
                LearnLikedThenPlay(target, device, track, trackUri, shuffle, fromMs, skipUid, video);
                return;
        }
    }

    /// <summary>A Liked Songs play as the owner is sent it (1.3): the account's Liked Songs playlist as the context,
    /// <c>skip_to.track_uri</c> the clicked track, the play_origin feature <see cref="Spotify.Connect.LikedFeature"/> (bro.saz
    /// 352). The playlist's order is the service's, not this view's sort (plan step 4.3 sends that). BLOCKS: an api thread.</summary>
    static bool PlayLiked(string target, string playlist, string trackUri, bool shuffle, int fromMs, string? skipUid, bool video)
        => Spotify.Connect.PlayContext(target, playlist, trackUri, shuffle, CancellationToken.None, fromMs, skipUid, video: video,
            feature: Spotify.Connect.LikedFeature);

    /// <summary>The Liked rule's Unknown arm (1.3, 1.4): learn the account's Liked Songs playlist from kind 246 on the clicked
    /// TRACK — on the newest liked track when the play names none (the page's own Play) or names an episode, whose first
    /// curation list is another one — keep it, then send the play. When the answer names none, NOTHING is sent: the collection
    /// spelling is what stopped the phone (gap analysis §3), and the user is told instead. UI thread; the ask and the send are
    /// one api item.</summary>
    static void LearnLikedThenPlay(string target, ulong device, EntityId track, string trackUri, bool shuffle, int fromMs,
        string? skipUid, bool video)
    {
        string sample = track.Kind == EntityKind.Track ? trackUri : NewestLikedTrack();
        if (sample.Length == 0)
        {
            Log.Warn("playback", "liked play not forwarded: no track to learn the Liked Songs playlist from");
            NoteRemoteRefusal(RemoteVerb.LikedUnknown, device);
            return;
        }
        Log.Info("playback", "liked play to the owner waits for the Liked Songs playlist: asking kind 246");
        RunForward(RemoteVerb.LikedUnknown, device, () =>
        {
            string? learned = Spotify.Connect.AskLikedPlaylist(sample, CancellationToken.None);
            if (learned is null)
            {
                ToUi(() => NoteRemoteRefusal(RemoteVerb.LikedUnknown, device));
                return;
            }
            ToUi(() => Spotify.Connect.RememberLikedPlaylist(learned, "kind246"));
            Answered(RemoteVerb.Play, device, PlayLiked(target, learned, trackUri, shuffle, fromMs, skipUid, video));
        });
    }

    /// <summary>The newest liked track's uri, or "": the track a Liked play that names none asks kind 246 on. UI thread.</summary>
    static string NewestLikedTrack()
    {
        Scope? scope = Entities.Current;
        if (scope is null || scope.MeSlot <= Table.None) return "";
        ReadOnlySpan<int> liked = scope.Edges.Liked.Targets(scope.MeSlot);
        return liked.IsEmpty ? "" : new Track(liked[0]).Uri.Text;
    }

    // ── 3. the paced volume PUT (2.3) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The least gap between two volume PUTs to the owner: about three a second. The official desktop's drag sent 12
    /// PUTs in 4.0 s, never closer than 218 ms (all.saz 2243-2256); Wavee sent one per slider change, 36 in 0.49 s
    /// (wavee-20261120-073951.log lines 7078-7149).</summary>
    public const int VolumePutGapMs = 330;

    static readonly Lock s_volumeGate = new();
    static string s_volumeTarget = "";
    static ulong s_volumeDevice;
    static int s_volumeWanted;
    static long s_volumeSentAtMs;
    /// <summary>A move has not been sent yet. A flag, not a compare with the last value sent: the owner may have moved its own
    /// volume since, and a move back to that value must still go.</summary>
    static bool s_volumeDirty;
    /// <summary>A send is queued or out, or the trailing timer is armed: a move meanwhile only updates the wanted value.</summary>
    static bool s_volumeBusy;
    static Timer? s_volumeTimer;
    static readonly Action s_volumeSend = SendVolume;
    static readonly TimerCallback s_volumeDue = static _ =>
    {
        if (!Spotify.Api.Run(s_volumeSend)) lock (s_volumeGate) s_volumeBusy = false;
    };

    /// <summary>A slider move's volume for the owner, PACED (2.3): the first move of a drag goes at once (leading edge), a move
    /// inside the gap only replaces the value the next send reads, and once the gap since the last send ends the newest value
    /// goes (trailing edge) — about three PUTs a second, and the last value of a drag always lands. Sent off the UI thread as
    /// before. UI thread, once per drain that moved the slider: no allocation (the send and the timer callback are made once,
    /// the timer on the first trailing edge).</summary>
    static void PaceVolume(string target, ulong device, int volume)
    {
        lock (s_volumeGate)
        {
            s_volumeTarget = target;
            s_volumeDevice = device;
            s_volumeWanted = volume;
            s_volumeDirty = true;
            if (s_volumeBusy) return;
            s_volumeBusy = true;
            ArmVolumeSend(Environment.TickCount64);
        }
    }

    /// <summary>Queue the send now, or arm the timer for the end of the gap since the last one. Under the gate.</summary>
    static void ArmVolumeSend(long nowMs)
    {
        long wait = s_volumeSentAtMs + VolumePutGapMs - nowMs;
        if (wait <= 0)
        {
            if (!Spotify.Api.Run(s_volumeSend)) s_volumeBusy = false;
            return;
        }
        (s_volumeTimer ??= new Timer(s_volumeDue)).Change(wait, Timeout.Infinite);
    }

    /// <summary>The send, on an api thread: the newest value, when one is waiting; then, when the slider moved while it was
    /// out, the trailing send is armed for the end of the gap. A refusal is said (2.1, rate-limited per kind).</summary>
    static void SendVolume()
    {
        string target;
        ulong device;
        int volume;
        lock (s_volumeGate)
        {
            if (!s_volumeDirty) { s_volumeBusy = false; return; }
            s_volumeDirty = false;
            target = s_volumeTarget;
            device = s_volumeDevice;
            volume = s_volumeWanted;
            s_volumeSentAtMs = Environment.TickCount64;
        }
        Answered(RemoteVerb.Volume, device, Spotify.Connect.Volume(target, volume, CancellationToken.None));
        lock (s_volumeGate)
        {
            if (s_volumeDirty) ArmVolumeSend(Environment.TickCount64);
            else s_volumeBusy = false;
        }
    }
}
