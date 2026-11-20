// ── Playback/Playback.Host.Forward.cs ────────────────────────────────────────────────────────────────────────────────
// A local play forwarded to the device that owns playback, under the Liked rule; and the refusal a forward earns, which the
// shell says as one toast per verb kind (remote-play plan 1.1-1.4, 2026-11-20)
//
// Role: SHELL
// Owner: G
// Wave: remote-play fixes (2026-11-20)
// Budget: 300 lines
// Spec: the remote-play gap analysis of 2026-11-20 (§2.1, §2.8, §3, §5 phase 1) — a named partial of Playback.Host.cs
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
// A REFUSED FORWARD. A non-2xx answer (or a send that could not be queued) is noted on the UI thread as LastRemoteRefusal —
// its verb kind, the device, a sequence — and the shell turns it into one toast per kind, rate-limited
// (`Shell.RemoteRefusalToasts`). Nothing optimistic is invented or rolled back: the bar follows the owner's next cluster.

using FluentGpu.Signals;

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
    }

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
}
