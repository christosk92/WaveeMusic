using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Provider-aware shuffle (PlaybackSession.SetShuffle) — the autoplay-anchor dispatch. The bug guarded against: shuffling
// while an AUTOPLAY-station row plays used to re-pool the WHOLE natural order behind the anchor, so the already-finished
// playlist (a,b,c) resurfaced in Upcoming — "shuffle during radio replays the album you just heard". The fix splits the
// reshuffle on the anchor's provider:
//   • Autoplay anchor → only the REMAINING autoplay playables after the anchor shuffle; everything consumed (original
//     context rows AND earlier autoplay rows) stays BEFORE the cursor in natural order; cursor sits ON the anchor.
//   • Context anchor → unchanged legacy behavior: anchor at logical 0, ALL Context playables (played included) re-pool
//     after it, the autoplay tail keeps natural relative order at the END, cursor = 0.
// The shuffle permutation comes from a deterministic per-session LCG but is treated as order-OPAQUE here: shuffled
// regions are asserted by SET membership, exact order only where natural order is the contract.
public class ShuffleAutoplayTests
{
    static Track T(string id) => new(id, "spotify:track:" + id, "T-" + id,
        System.Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static QueuedTrack Q(string id, string uid = "", string provider = "context",
        QueueRowKind kind = QueueRowKind.Playable) => new(T(id), uid, provider, null, kind);

    static IReadOnlyList<QueuedTrack> Ctx(params string[] ids) => ids.Select(x => Q(x, "u-" + x)).ToList();

    static bool IsTrack(QueueEntry e, string id) => e.Track.Uri == "spotify:track:" + id;

    static HashSet<string> Uris(IEnumerable<QueueEntry> es) => es.Select(e => e.Track.Uri).ToHashSet();

    // Set-wise comparison for the shuffled regions: sort both sides so the assertion is order-OPAQUE (the LCG permutation
    // is deliberately not pinned) while still producing a readable diff on failure.
    static string[] SortedIds(IEnumerable<QueueEntry> es) =>
        es.Select(e => e.Track.Id).OrderBy(x => x, System.StringComparer.Ordinal).ToArray();

    // playlist a,b,c fully played; autoplay tail s1..s4 appended; current = s1 (Autoplay); history [a,b,c]
    static PlaybackSession AutoplaySession()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c"), 0);
        s.AppendContextPage(new[]
        {
            Q("s1", "us1", "autoplay"), Q("s2", "us2", "autoplay"),
            Q("s3", "us3", "autoplay"), Q("s4", "us4", "autoplay"),
        }, QueueProvider.Autoplay, "spotify:station:x");
        s.Next(); s.Next(); s.Next();   // a→b→c→s1
        return s;
    }

    // ── the core fix — autoplay anchor shuffles ONLY the station remainder; the played playlist never comes back ───────
    [Fact]
    public void ShuffleOn_AutoplayAnchor_UpcomingIsOnlyRemainingAutoplay()
    {
        var s = AutoplaySession();

        var on = s.SetShuffle(true);

        Assert.True(on.Shuffle);
        Assert.True(IsTrack(on.Current!, "s1"));                                    // anchor unmoved (no audio reload)
        Assert.Equal(3, on.Upcoming.Length);                                        // the station remainder, nothing else
        Assert.Equal(new[] { "s2", "s3", "s4" }, SortedIds(on.Upcoming));           // set — the order is LCG-opaque
        Assert.All(on.Upcoming, e => Assert.Equal(QueueProvider.Autoplay, e.Provider));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "a"));                   // ← the regression: played context rows
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "b"));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "c"));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "s1"));                  // the anchor is not its own up-next
        Assert.Equal(new[] { "a", "b", "c" }, on.History.Select(e => e.Track.Id));  // history untouched, order-exact
    }

    // ── OFF is a pure restore: natural order back, cursor re-found on the current row ─────────────────────────────────
    [Fact]
    public void ShuffleOff_AfterAutoplayAnchor_RestoresNaturalOrder_CursorStaysOnCurrent()
    {
        var s = AutoplaySession();
        s.SetShuffle(true);

        var off = s.SetShuffle(false);

        Assert.False(off.Shuffle);
        Assert.True(IsTrack(off.Current!, "s1"));
        Assert.Equal(new[] { "s2", "s3", "s4" }, off.Upcoming.Select(e => e.Track.Id));   // exact — natural order
        Assert.DoesNotContain(off.Upcoming, e => e.Provider == QueueProvider.Context);    // no context row resurfaced
    }

    // ── legacy parity — a CONTEXT anchor still re-pools every context row (played included), autoplay tail last ───────
    [Fact]
    public void ShuffleOn_ContextAnchor_WithAutoplayTail_KeepsRepooledContextAndNaturalTail()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c", "d", "e"), 2);   // current = c
        s.AppendContextPage(new[] { Q("s1", "us1", "autoplay"), Q("s2", "us2", "autoplay") },
            QueueProvider.Autoplay, "spotify:station:x");

        var on = s.SetShuffle(true);

        Assert.True(IsTrack(on.Current!, "c"));                                // anchor unmoved
        Assert.Equal(6, on.Upcoming.Length);                                   // a,b,d,e re-pooled + s1,s2
        var up = Uris(on.Upcoming);
        Assert.Contains("spotify:track:a", up);                                // played rows DO re-pool (legacy parity)
        Assert.Contains("spotify:track:b", up);
        Assert.Contains("spotify:track:d", up);
        Assert.Contains("spotify:track:e", up);
        Assert.Equal(new[] { "s1", "s2" },                                     // the autoplay tail keeps natural order
            on.Upcoming.TakeLast(2).Select(e => e.Track.Id));
    }

    // ── a station page appended WHILE shuffled lands at the END of the play order (never inside the shuffled window) ──
    [Fact]
    public void AppendAutoplayPage_WhileShuffledOnAutoplayAnchor_LandsAfterShuffledRemainder()
    {
        var s = AutoplaySession();
        s.SetShuffle(true);

        var appended = s.AppendContextPage(new[] { Q("s5", "us5", "autoplay"), Q("s6", "us6", "autoplay") },
            QueueProvider.Autoplay, "spotify:station:x");

        Assert.Equal(5, appended.Upcoming.Length);
        Assert.Equal(new[] { "s2", "s3", "s4" }, SortedIds(appended.Upcoming.Take(3)));   // shuffled window, set-wise
        Assert.Equal(new[] { "s5", "s6" }, appended.Upcoming.TakeLast(2).Select(e => e.Track.Id));   // appended, in order
        Assert.DoesNotContain(appended.Upcoming, e => e.Provider == QueueProvider.Context);

        var off = s.SetShuffle(false);
        Assert.Equal(new[] { "s2", "s3", "s4", "s5", "s6" }, off.Upcoming.Select(e => e.Track.Id));
        Assert.True(IsTrack(off.Current!, "s1"));
    }

    // ── the invariant across a full ON→advance→OFF→ON cycle: nothing already played may ever resurface ────────────────
    [Fact]
    public void ShuffleOnOffOn_DuringAutoplay_NeverResurfacesPlayedRows()
    {
        var s = AutoplaySession();

        var on1 = s.SetShuffle(true);
        AssertNoPlayedRows(on1);

        var advanced = s.Next()!;                                   // current becomes some shuffled row X in {s2..s4}
        AssertNoPlayedRows(advanced);
        string x = advanced.Current!.Track.Uri;
        Assert.Contains(x, new[] { "spotify:track:s2", "spotify:track:s3", "spotify:track:s4" });

        var off = s.SetShuffle(false);
        AssertNoPlayedRows(off);
        Assert.Equal(x, off.Current!.Track.Uri);

        var on2 = s.SetShuffle(true);
        AssertNoPlayedRows(on2);
        Assert.Equal(x, on2.Current!.Track.Uri);                     // identity stable across OFF→ON (no reload)
        Assert.All(on2.Upcoming, e => Assert.Equal(QueueProvider.Autoplay, e.Provider));

        static void AssertNoPlayedRows(QueueSnapshot snap)
        {
            foreach (var id in new[] { "a", "b", "c", "s1" })
                Assert.DoesNotContain(snap.Upcoming, e => IsTrack(e, id));
        }
    }

    // ── a user-queue row is playing: the anchor is the RESIDENT autoplay row at the cursor, not the queue row ─────────
    [Fact]
    public void ShuffleOn_WhileQueueRowPlays_AnchorsResidentAutoplayRow()
    {
        var s = AutoplaySession();
        s.EnqueueUser(new[] { Q("q1") });
        s.Next();                                                   // current = q1; the context cursor stays on s1

        var on = s.SetShuffle(true);

        Assert.True(IsTrack(on.Current!, "q1"));                    // the queue row keeps playing
        Assert.Equal(QueueProvider.Queue, on.Current!.Provider);
        Assert.Equal(new[] { "s2", "s3", "s4" }, SortedIds(on.Upcoming));   // shuffled from the resident autoplay anchor
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "a"));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "b"));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "c"));
        Assert.DoesNotContain(on.Upcoming, e => IsTrack(e, "s1"));   // the anchor itself stays before the cursor
    }
}
