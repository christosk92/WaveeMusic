using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The restore/identity-miss ladder and the queue-content fold — the two pure pieces of the playback-restore rework
// (docs/plans/wavee/playback-restore-findings.md §6 and test 23/24). No engine, no host, no cluster: these pin the
// decision rules the controller's three restore callers (transfer, recovery heal, local snapshot) all share.
public class PlaybackRestoreTests
{
    static Track T(string uri) =>
        new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri, Array.Empty<ArtistRef>(),
            new AlbumRef("", "spotify:album:al", "Al"), 200000, false, null);

    static QueuedTrack Row(string uri, string uid = "", QueueRowKind kind = QueueRowKind.Playable) =>
        new(T(uri), uid, "context", null, kind);

    // ── §6 ladder: uid → uri → saved index (in range AND playable) → context head (opt-in only) ──────────────────────

    [Fact]
    public void ResolveRestoreIndex_UidWins_OverADivergentSavedIndex()
    {
        // The context came back reordered (a regenerated mix / an edited playlist): the uid is the only trustworthy
        // identity, so a saved index that now points at a different track must NOT win (F2).
        var tracks = new[] { Row("spotify:track:c", "u3"), Row("spotify:track:a", "u1"), Row("spotify:track:b", "u2") };

        int i = ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:a", "u1", savedIndex: 0, allowContextHead: false);

        Assert.Equal(1, i);
    }

    [Fact]
    public void ResolveRestoreIndex_UriMatches_WhenTheUidIsGone()
    {
        // Playlist re-add mints a new uid for the same track — the uri rung catches it before the index rung.
        var tracks = new[] { Row("spotify:track:a", "fresh-uid"), Row("spotify:track:b", "u2") };

        int i = ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:a", "stale-uid", savedIndex: 1, allowContextHead: false);

        Assert.Equal(0, i);
    }

    [Fact]
    public void ResolveRestoreIndex_FallsBackToTheSavedIndex_OnlyWhenItIsInRangeAndPlayable()
    {
        var tracks = new[] { Row("spotify:track:x", "ux"), Row("spotify:track:y", "uy") };

        // In range → taken (the row is playable; identity is simply gone from this context).
        Assert.Equal(1, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", 1, allowContextHead: false));
        // Out of range (the context shrank) → refused rather than clamped.
        Assert.Equal(-1, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", 9, allowContextHead: false));
        // Negative (no cursor was ever saved) → refused.
        Assert.Equal(-1, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", -1, allowContextHead: false));
    }

    [Fact]
    public void ResolveRestoreIndex_NeverLandsOnANonPlayableRow()
    {
        // A delimiter / page marker sitting at the saved index is not a playable destination.
        var tracks = new[] { Row("spotify:track:a", "ua"), Row("spotify:delimiter", "d", QueueRowKind.Delimiter) };

        Assert.Equal(-1, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", 1, allowContextHead: false));
        // …and the head rung skips it too, landing on the first PLAYABLE row.
        var markerFirst = new[] { Row("spotify:meta:page:1", "p", QueueRowKind.PageMarker), Row("spotify:track:a", "ua") };
        Assert.Equal(1, ContextResolve.ResolveRestoreIndex(markerFirst, "spotify:track:gone", "gone", -1, allowContextHead: true));
    }

    [Fact]
    public void ResolveRestoreIndex_TakesTheContextHead_OnlyWhenTheCallerOptedIn()
    {
        var tracks = new[] { Row("spotify:track:a", "ua"), Row("spotify:track:b", "ub") };

        // Launch recovery / always_play_something opts in ("play something paused").
        Assert.Equal(0, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", -1, allowContextHead: true));
        // A plain transfer does not — a full miss must patch the saved current in outside the spine instead of silently
        // starting a different track.
        Assert.Equal(-1, ContextResolve.ResolveRestoreIndex(tracks, "spotify:track:gone", "gone", -1, allowContextHead: false));
    }

    [Fact]
    public void ResolveRestoreIndex_EmptyContext_MissesEveryRung()
    {
        Assert.Equal(-1, ContextResolve.ResolveRestoreIndex(
            Array.Empty<QueuedTrack>(), "spotify:track:a", "ua", 0, allowContextHead: true));
    }

    // ── test 24: the queue-content fold behind QueueRevision ─────────────────────────────────────────────────────────

    [Fact]
    public void QueueContentFold_IsStableAcrossARepublishOfTheSameEntries()
    {
        var session = new PlaybackSession();
        var snapA = session.SetContext("spotify:playlist:p",
            [Row("spotify:track:a", "u1"), Row("spotify:track:b", "u2"), Row("spotify:track:c", "u3")], 0);
        var snapB = session.Snapshot();   // a pause/seek/volume republish re-windows the SAME rows

        Assert.Equal(QueueContentFold.Fold(snapA.Upcoming), QueueContentFold.Fold(snapB.Upcoming));
    }

    [Fact]
    public void QueueContentFold_ChangesWhenTheQueueContentChanges()
    {
        var session = new PlaybackSession();
        var seeded = session.SetContext("spotify:playlist:p",
            [Row("spotify:track:a", "u1"), Row("spotify:track:b", "u2")], 0);
        ulong before = QueueContentFold.Fold(seeded.Upcoming);

        // A ghost/recovery seed produces new rows — the revision must bump (an empty queue and a filled one never fold
        // to the same value, which is what made "now playing with an empty queue panel" invisible to consumers).
        var enqueued = session.EnqueueUser([Row("spotify:track:q", "q1")]);

        Assert.NotEqual(before, QueueContentFold.Fold(enqueued.UserQueue));
        Assert.NotEqual(QueueContentFold.Fold([]), before);
    }

    [Fact]
    public void QueueContentFold_IsOrderSensitive()
    {
        // The fold keys on the session's MINTED item ids (not uris), so order-sensitivity has to be shown inside one
        // session's id space: enqueue-at-tail and enqueue-next mint the same two ids in the opposite row order.
        var tail = new PlaybackSession();
        tail.SetContext("spotify:playlist:p", [Row("spotify:track:a", "u1")], 0);
        tail.EnqueueUser([Row("spotify:track:q1", "q1")]);
        var tailSnap = tail.EnqueueUser([Row("spotify:track:q2", "q2")]);          // → [q1, q2]

        var head = new PlaybackSession();
        head.SetContext("spotify:playlist:p", [Row("spotify:track:a", "u1")], 0);
        head.EnqueueUser([Row("spotify:track:q1", "q1")]);
        var headSnap = head.EnqueueNextUser([Row("spotify:track:q2", "q2")]);      // → [q2, q1]

        Assert.Equal(tailSnap.UserQueue.Length, headSnap.UserQueue.Length);
        Assert.NotEqual(QueueContentFold.Fold(tailSnap.UserQueue), QueueContentFold.Fold(headSnap.UserQueue));
    }
}
