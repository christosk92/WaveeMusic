// ── Playback/Playback.Host.Forward.cs ────────────────────────────────────────────────────────────────────────────────
// A local play forwarded to the device that owns playback, under the Liked rule; the refusal any forward earns, which the
// shell says as one toast per verb kind; the context and row a play names (a playlist's page inline, an album row by index,
// an artist's Popular list); and the paced volume PUT (remote-play plan 1.1-1.4, 2.1, 2.3, 4.1, 4.2, 4.4, 2026-11-20)
//
// Role: SHELL
// Owner: G
// Wave: remote-play fixes (2026-11-20)
// Budget: 450 lines
// Spec: the remote-play gap analysis of 2026-11-20 (§2.1, §2.5, §2.8, §3, §5 phases 1-2 and 4) — a named partial of Playback.Host.cs
//
// THE SAME SHELL RULES AS THE PARENT: the UI thread owns the deck and the interner (C1), a send blocks and so runs on an api
// thread (`Spotify.Api.Run`), and what it learns comes back through `ToUi` (C9).
//
// THE PLAY FORWARD, ONE SEND FOR BOTH INTAKES:
//
//   a row click (DoPlay → ForwardPlay → SendRemote's PlayContext arm) ─┐
//   PlayContext(uri) while another device owns playback (ForwardPlayToOwner)  ─┴─▶ SendPlay ─▶ ForwardPlayEnvelope.Liked:
//       NotLiked  ─▶ ShapePlay: an artist ─▶ its Popular list · an album row ─▶ uri + its album index · a playlist page's
//                    row ─▶ the page inline in the order shown, the row by uri + list uid + page index · else the context as is
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

using FluentGpu.Foundation;
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

    /// <summary>Does a local play go to another device right now — it owns playback and the roster names it? The test
    /// <see cref="ForwardPlayToOwner"/> makes, for a page that builds <see cref="PageFacts"/> only then. UI thread; a peek, for
    /// a click.</summary>
    public static bool ForwardsPlays => !Queue.LocalQueueWritable(s_state.Owner) && s_state.Own.Device != 0;

    /// <summary>What a list page knows about the rows it hands <see cref="PlayRows"/> beyond their identities, read ONLY when the
    /// play goes to another device (remote-play plan 4.1, 4.2): per row, aligned with the rows, the uid the LIST gave it (a
    /// playlist's own item id as hex — the uid every official play names it by: 16 hex for a user's list, bigone.saz 1391; 20 for
    /// the Liked list; 32 for a generated one, bigone.saz 578 — <see cref="StringId.Empty"/> where the list has none, as an
    /// album's) and its index in the list's own order; and the view: its sort, its trimmed text query, and whether any other
    /// filter narrows it. Built on a click (<c>Track.Table</c>), never per frame.</summary>
    public sealed class PageFacts(StringId[] uids, int[] origins, Track.SortSpec sort, string query, bool otherFilters)
    {
        public StringId[] Uids { get; } = uids;
        public int[] Origins { get; } = origins;
        public Track.SortSpec Sort { get; } = sort;
        public string Query { get; } = query;
        public bool OtherFilters { get; } = otherFilters;
    }

    /// <summary>A NotLiked play as the owner is sent it: the context uri, the play_origin feature (null = from the uri), the
    /// start row's uid and index, and the page inline.</summary>
    readonly record struct RemotePlay(string ContextUri, string? Feature, string? SkipUid, int SkipIndex, Spotify.Connect.PlayPage? Page);

    /// <summary>THE play forward, for both intakes: the reducer's row click (SendRemote's PlayContext arm) and
    /// <see cref="ForwardPlayToOwner"/>. <paramref name="context"/> is already the <see cref="ForwardPlayEnvelope.For"/> answer;
    /// the Liked rule (<see cref="ForwardPlayEnvelope.Liked"/>) decides what is sent, and <see cref="ShapePlay"/> how a play that
    /// is not Liked names its context and row (4.1, 4.2, 4.4). <paramref name="rows"/> / <paramref name="start"/> /
    /// <paramref name="facts"/> are a page's held rows (<see cref="PlayRows"/>), empty for a play that names none. UI thread
    /// (the texts are the interner's, the known playlist is the scope's).</summary>
    static void SendPlay(string target, ulong device, EntityId context, EntityId track, bool shuffle, int fromMs, string? skipUid,
        bool video, ReadOnlySpan<EntityRef> rows = default, int start = -1, PageFacts? facts = null)
    {
        string trackUri = track.IsEmpty ? "" : track.Text;
        string liked = Spotify.Connect.KnownLikedPlaylist;
        switch (ForwardPlayEnvelope.Liked(context, liked))
        {
            case ForwardPlayEnvelope.LikedVerdict.NotLiked:
                {
                    RemotePlay play = ShapePlay(context, skipUid, rows, start, facts);
                    Log.Info("playback", "play forwarded to the owner: context=" + play.ContextUri + " track=" + trackUri
                        + (play.SkipIndex >= 0 ? " index=" + play.SkipIndex : "")
                        + (play.Page is { } page ? " page=" + page.Uris.Length + (page.Sorting.Length > 0 ? " sorted" : "")
                            + (page.Filter.Length > 0 ? " filtered" : "") : "")
                        + (video ? " video" : ""));
                    RunForward(RemoteVerb.Play, device, () => Answered(RemoteVerb.Play, device,
                        Spotify.Connect.PlayContext(target, play.ContextUri, trackUri, shuffle, CancellationToken.None, fromMs, play.SkipUid,
                            play.SkipIndex, video, play.Feature, play.Page)));
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

    /// <summary>How a play that is not Liked names its context and its row (remote-play plan phase 4). UI thread.
    /// <list type="bullet">
    /// <item>An ARTIST plays from its Popular list, <c>spotify:list:popular-release-segments-main-roles:artist_&lt;id&gt;</c>,
    /// feature <c>artist</c>, as the official client plays the artist page's Popular rows and its Play (4.4, bigone.saz 425,
    /// playback_remote.saz 439).</item>
    /// <item>An ALBUM row: the album gives its rows no uid Wavee holds (the resolve answer has none, bigone.saz 892), so the row
    /// goes by uri and its index in the album's own order (4.1; skip_to <c>track_uri</c> + <c>track_index</c>, as all.saz 1294).</item>
    /// <item>A PLAYLIST row (4.1, 4.2): the page inline (<see cref="PagePlay"/>), the row by uri, its list uid and its index in
    /// that page. A list longer than <see cref="Spotify.Connect.MaxPageRows"/> goes uri-only, the row by uri and list uid: the
    /// owner's own resolve names its rows by the same item ids (sppp.saz 159).</item>
    /// <item>Anything else, and a play that holds no rows (a row of the mirrored queue: <paramref name="skipUid"/>, a uid the
    /// owner booked), as before.</item>
    /// </list></summary>
    static RemotePlay ShapePlay(EntityId context, string? skipUid, ReadOnlySpan<EntityRef> rows, int start, PageFacts? facts)
    {
        string contextUri = context.Text;
        if (context.Kind == EntityKind.Artist && Spotify.Connect.PopularListOf(contextUri) is { } popular)
            return new RemotePlay(popular, "artist", skipUid, -1, null);
        if (facts is null || (uint)start >= (uint)rows.Length || facts.Uids.Length != rows.Length || facts.Origins.Length != rows.Length)
            return new RemotePlay(contextUri, null, skipUid, -1, null);
        if (context.Kind == EntityKind.Album) return new RemotePlay(contextUri, null, null, facts.Origins[start], null);
        if (context.Kind != EntityKind.Playlist) return new RemotePlay(contextUri, null, skipUid, -1, null);
        if (rows.Length > Spotify.Connect.MaxPageRows) return new RemotePlay(contextUri, null, ListUid(facts.Uids[start]), -1, null);
        return PagePlay(contextUri, rows, start, facts);
    }

    /// <summary>A playlist play with its page inline (4.2, all.saz 1169, bigone.saz 1391, 1628): every row Wavee shows that can
    /// play on another device (a local file cannot, and is left out), in the order shown, with its list uid; each row's index in
    /// the list's own order when the view's sort is one the owner is told (<see cref="Spotify.Connect.SortingCriteria"/>,
    /// bigone.saz 784); the sort and the text filter when they can be said; a fresh session id; the clicked row by uri, uid and
    /// its index in this page. A clicked row that cannot go leaves the page out (the send refuses that row anyway). UI thread;
    /// one string per row, on a click.</summary>
    static RemotePlay PagePlay(string contextUri, ReadOnlySpan<EntityRef> rows, int start, PageFacts facts)
    {
        string sorting = Spotify.Connect.SortingCriteria(facts.Sort.Column, facts.Sort.Descending);
        string filter = facts.OtherFilters ? "" : Spotify.Connect.FilterPredicate(facts.Query) ?? "";
        var uris = new string[rows.Length];
        var uids = new string[rows.Length];
        int[]? original = sorting.Length > 0 ? new int[rows.Length] : null;
        int n = 0, at = -1;
        for (int i = 0; i < rows.Length; i++)
        {
            EntityId id = rows[i].Id;
            if (!id.IsPlayable || !Spotify.Egress.Admits(id)) continue;
            if (i == start) at = n;
            uris[n] = id.Text;
            uids[n] = ListUid(facts.Uids[i]) ?? "";
            if (original is not null) original[n] = facts.Origins[i];
            n++;
        }
        if (at < 0) return new RemotePlay(contextUri, null, ListUid(facts.Uids[start]), -1, null);
        if (n < rows.Length)
        {
            Array.Resize(ref uris, n);
            Array.Resize(ref uids, n);
            if (original is not null) Array.Resize(ref original, n);
        }
        var page = new Spotify.Connect.PlayPage(uris, uids, original, sorting, filter, Spotify.Connect.NewSessionId());
        return new RemotePlay(contextUri, null, uids[at].Length > 0 ? uids[at] : null, at, page);
    }

    /// <summary>A list's own uid for a row (a playlist item id, hex) when it has the shape a play may name
    /// (<see cref="Spotify.Connect.SkipUidOf"/>), else null. UI thread (the interner's).</summary>
    static string? ListUid(StringId uid) => uid.IsEmpty ? null : Spotify.Connect.SkipUidOf(Entities.Strings.Resolve(uid));

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
