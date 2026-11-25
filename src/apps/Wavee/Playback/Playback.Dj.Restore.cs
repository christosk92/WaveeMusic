// ── Playback/Playback.Dj.Restore.cs ──────────────────────────────────────────────────────────────────────────────────
// AI DJ at launch (plan-dj D4): a deck the session document left on a DJ list resumes from a fresh lexicon session.
//
// Role: SHELL (the fetch on an api thread, the landing on the UI thread)
// Spec: the AI DJ plan (plan-dj) §2.1, §2.7; the protocol's open capture C5 (a resume's reason)
//
// A NAMED PARTIAL OF `Playback.Host.cs`, beside the launch restore's context seed (`Playback.Host.Context.cs`):
//
//   Restore(point) ─▶ NoteSeedDj: the url the document kept (RestorePoint.ContextUrl), else the list's lexicon_context_url
//                     (the list's attributes are not persisted: at boot, before the rootlist, only the document knows it)
//                  ─▶ the document's rows are NOT laid back (an old session's segments: no page holds them, nothing pages them)
//                  ─▶ the seed (now, or at the session's next Online) ─▶ ResolveSeedContext ─▶ ResolveDjSeed (api thread):
//                     the session + its first pages, reason=interactive ─▶ LandDjSeed (UI thread):
//                     the saved track stays on the deck (paused, at its position): at its row when the session lists it (its earlier
//                     rows history), else alone before the session · the session's rows follow, each once · Dj.Begin(Restore) · the
//                     session's toggle locks · its pages
//
// WHY THE SAVED TRACK AND NOT THE SESSION'S FIRST ROW: the restore already painted the saved track paused at its position; landing
// on another track would change what the bar shows at boot and drop where the listener stopped. A fresh session rarely lists the old
// track, so it plays to its end (or the listener skips it) and the DJ continues with the session's first segment, exactly as a
// transfer into Wavee lands (StartContext).

using System.Buffers;
using System.Text;

using FluentGpu.Foundation;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;

namespace Wavee;

public static partial class Playback
{
    static EntityId s_seedDjContext;
    static string s_seedDjUrl = "";

    /// <summary>The launch restore names its deck's context: remember the lexicon url a DJ deck resumes from (the document's, else the
    /// list's attribute). True when the deck is a DJ's. UI thread, at boot.</summary>
    static bool NoteSeedDj(EntityId context, string? documentUrl)
    {
        bool fromDocument = !string.IsNullOrEmpty(documentUrl) && Dj.IsLexicon(documentUrl);
        string url = fromDocument ? documentUrl! : Dj.ListUrl(context);
        if (!Dj.IsLexicon(url)) { s_seedDjContext = default; s_seedDjUrl = ""; return false; }
        s_seedDjContext = context;
        s_seedDjUrl = url;
        Log.Info("dj", "launch restore: the deck is a DJ's (" + (fromDocument ? "the session document's url" : "the list's attribute")
            + "): it resumes from a fresh lexicon session, the document's queue rows are not laid back");
        return true;
    }

    /// <summary>The lexicon url a seed of <paramref name="context"/> resolves, or "" for a context that is no DJ's. UI thread.</summary>
    static string SeedDjUrl(EntityId context)
    {
        if (context.IsEmpty) return "";
        if (context.Equals(s_seedDjContext)) return s_seedDjUrl;
        string url = Dj.ListUrl(context);
        return Dj.IsLexicon(url) ? url : "";
    }

    /// <summary>The seed of a DJ deck (<see cref="ResolveSeedContext"/>'s DJ branch): the session and its first pages on an api thread,
    /// landed by <see cref="LandDjSeed"/>. The reason a resume names is not captured (C5): <c>interactive</c>, as a start, and said so
    /// in the log. False when the api queue is full (the caller falls back to the bare row).</summary>
    static bool ResolveDjSeed(long seq, EntityId context, EntityId currentId, string url)
    {
        const string reason = Dj.DefaultReason;
        string path = Dj.SessionRoute(url, reason);
        Spotify.Api.DeviceContext = Dj.DeviceContext(OutputDeviceName());
        Log.Info("dj", "launch restore: resolving the lexicon session reason=" + reason + " (the official client's resume reason is not captured)");
        ClusterBuffer buffer = ClusterBuffer.Rent();
        bool queued = Spotify.Api.Run(() =>
        {
            int start = buffer.TrackCount, count = 0;
            string next = "";
            Dj.Resolve? resolve = null;
            try
            {
                using Bodies.LendScope lent = Bodies.Lend();
                resolve = FetchSession(path, url, reason, buffer, out _, out next);
                if (resolve is not null) count = buffer.TrackCount - start;
            }
            catch (Exception ex) { Log.Warn("dj", "launch restore: the lexicon resolve failed", ex); }
            ToUi(() =>
            {
                try { LandDjSeed(seq, context, currentId, buffer, start, count, next, resolve); }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (queued) return true;
        Log.Warn("dj", "launch restore: the lexicon resolve was refused: the api queue is full");
        ClusterBuffer.Return(buffer);
        return false;
    }

    /// <summary>UI thread: the DJ seed answered. Dropped as <see cref="LandSeedContext"/> drops a seed (a newer seed, a queue that
    /// already answered, a deck that moved on). Otherwise the saved track stays the deck and the session follows it (the file header).</summary>
    static void LandDjSeed(long seq, EntityId context, EntityId currentId, ClusterBuffer buffer, int start, int count, string next,
        Dj.Resolve? resolve)
    {
        if (seq != s_seedSeq || Entities.Current is null || Queue.State != EdgeState.Unknown || !s_state.CurrentId.Equals(currentId)) return;
        EntityRef current = Entities.Ref(currentId);
        if (current.IsNone) return;
        if (resolve is null || count == 0)
        {
            SeedCurrentRow(current);
            s_state.Cursor = Queue.CursorOf(0);
            Log.Warn("dj", "launch restore: the lexicon session gave no rows; the saved track stays alone");
            RequestDrain();
            return;
        }

        ReadOnlySpan<ClusterTrack> tracks = buffer.Tracks(start, count);
        Span<byte> text = stackalloc byte[512];
        TextRef deckUri = buffer.AddText(text[..currentId.Format(text)]);
        // The saved track where the session lists it (its row, with its DJ keys), else alone before the session: as a transfer lands.
        int at = RemotePlan.StartIndex(buffer, tracks, default, deckUri, -1);
        int cap = RemotePlan.MaxRows, deck = 0;
        EntityRef[] refs = ArrayPool<EntityRef>.Shared.Rent(cap);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
        int n = 0;
        try
        {
            if (at > 0)
                for (int k = RemotePlan.HistoryFrom(at, tracks.Length); k < at; k++)
                    AddRow(buffer, in tracks[k], QueueBucket.History, refs, rows, ref n);
            deck = n;
            if (at >= 0) AddRow(buffer, in tracks[at], QueueBucket.NowPlaying, refs, rows, ref n);
            if (n == deck)
            {
                refs[n] = current;
                rows[n++] = new QueueEdge(Queue.MintItemIds(1), (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying);
            }
            for (int k = at + 1; k < tracks.Length && n < cap; k++)                // each row once: nothing plays twice
                AddRow(buffer, in tracks[k], QueueBucket.NextUp, refs, rows, ref n);
            int[] packed = ArrayPool<int>.Shared.Rent(n);
            try
            {
                Queue.Pack(refs.AsSpan(0, n), packed);
                FillQueueIds(context, packed.AsSpan(0, n), rows.AsSpan(0, n));
                Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
            }
            finally { ArrayPool<int>.Shared.Return(packed); }
            s_uids.Retain(Queue.Rows);
        }
        finally
        {
            ArrayPool<EntityRef>.Shared.Return(refs);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
        s_state.Cursor = Queue.CursorOf(deck);
        long now = FrameNowMs();
        s_pageContext = context;
        s_pageUrl = next;
        Dj.Begin(context, resolve, next, Dj.DjStart.Restore);                // before ContextPages: its refill reads the session
        Apply(Input.ContextLocks(context, LocksOf(resolve.Facts), ReasonOf(resolve.Facts), now));
        Apply(Input.ContextPages(context, next.Length > 0, now));
        Log.Info("dj", "launch restore: the saved track stays on the deck (" + (at >= 0 ? "row " + at + " of the session" : "no row of the session")
            + "); " + (n - deck - 1) + " session row(s) follow it, pages=" + resolve.Pages.Count);
        RequestDrain();
    }
}
