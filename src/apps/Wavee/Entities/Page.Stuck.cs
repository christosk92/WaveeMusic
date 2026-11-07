// ── Entities/Page.Stuck.cs — THE STUCK-PAGE WATCH: no page sits on its skeleton forever ─────────────────────────────────
//
// A page's reveal gate is a pure function of the marks (Known / Asked / Inflight, an edge's state), and a page re-reads it
// only when something it subscribes to publishes. Two classes of bug leave that pending for the life of the route:
//
//   · a MISSED WAKE: the data landed, but in a table or a row the page's render does not subscribe to, so the gate is
//     never read again (the owner's Fly By Midnight artist, 2026-11-07: the charted rows were the last answer, and the page
//     folded only the popular EDGE's publication);
//   · a group NOTHING IS COMING FOR: asked, an answer settled without filling it, nothing in flight, and the page's gate
//     still reads "pending" (a seal it does not model, a leaked in-flight stamp).
//
// Each gate has its own fix where the cause is known. This watch is the net under all of them: while a page shows its
// pending state it fires every `WaitMs`, re-reads the gate FRESH from the tables (never the last render's verdict), and
//
//   · gate open (the data is all there)      → wakes the page (one signal write re-renders it) and logs `stage=wake`; a page
//                                               a wake did not land (twice) walks on as if nothing were coming;
//   · still pending, a request in flight      → waits another `WaitMs` (at most `MaxInflightWaits` times: a leaked stamp
//                                               must not be a new forever);
//   · still pending, nothing coming, 1st time → re-asks what the page waits on (past any seal) and logs `stage=reask`;
//   · still pending after the re-ask          → gives up: the page shows its error vacancy with Retry, logs `stage=fail`.
//
// Every fire that acts writes one always-on `page.stuck route= stage= waiting= inflight= waitedMs=` line, so the next
// report names the page, the stage and the bits it waited on. The decision is pure (`Decide`); the host owns the probe,
// the re-ask and the timer (`UseTimeout` keyed on the watch's own stage, so each stage re-arms it).

using System.Globalization;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>What a page's watch reads at a fire, FRESH from the tables: is the page's gate still closed, is a request for
/// what it waits on out, and what it waits on (the <c>page.stuck</c> line's <c>waiting=</c>).</summary>
public readonly record struct StuckProbe(bool Pending, bool Inflight, string Waiting);

/// <summary>The stuck-page watch (file header). One per page host; its stage is KEYED (the host's subject and scope), so a
/// host that survives a subject change starts the new subject clean without a signal write in render.</summary>
public sealed class PageStuckWatch
{
    /// <summary>How long a page may show its pending state before the watch reads its gate again (ms). Longer than any
    /// healthy first load (an overview and its chart land in well under a second), short enough that a stuck page is
    /// named and recovered before the owner gives up on it.</summary>
    public const int WaitMs = 6000;

    /// <summary>How many wakes a page gets before a gate that reads open but still shows pending is treated as stuck.</summary>
    public const int MaxWakes = 2;

    /// <summary>How many times, in all, a request in flight extends the wait (so ~30 s before the re-ask and ~36 s before
    /// the give-up with one out the whole time) before the watch treats the page as stuck anyway: a leaked in-flight
    /// stamp, or a request the session holds, must not be a new forever.</summary>
    public const int MaxInflightWaits = 4;

    /// <summary>The stages, in the order a stuck page walks them.</summary>
    public const byte Watching = 0, Reasked = 1, GaveUp = 2;

    /// <summary>What a fire does (<see cref="Decide"/>).</summary>
    public enum Verdict : byte { None, Wait, Wake, Reask, GiveUp }

    readonly Signal<byte> _stage = new(Watching) { DebugName = "page.stuck.stage" };
    readonly Signal<uint> _wake = new(0) { DebugName = "page.stuck.wake" };
    long _key = long.MinValue;
    int _inflightWaits, _wakes;
    long _since;

    /// <summary>The stage for <paramref name="key"/>, TRACKED (a render that reads it re-renders on each step).
    /// <see cref="Watching"/> for any other key: a new subject starts clean.</summary>
    public byte StageOf(long key)
    {
        byte stage = _stage.Value;
        return _key == key ? stage : Watching;
    }

    /// <summary>Has the watch given up on <paramref name="key"/>? Tracked; the page ORs it into its failed state.</summary>
    public bool GaveUpOn(long key) => StageOf(key) == GaveUp;

    /// <summary>The wake counter, TRACKED: a page reads it in render, so a <see cref="Verdict.Wake"/> fire re-renders it.</summary>
    public uint Wake => _wake.Value;

    /// <summary>A user's Retry: watch again from the start. Never from render (a signal write).</summary>
    public void Reset()
    {
        _inflightWaits = _wakes = 0;
        _since = 0;
        if (_stage.Peek() != Watching) _stage.Value = Watching;
    }

    /// <summary>THE decision, pure: <paramref name="shown"/> is what the page shows (its last render's pending state),
    /// <paramref name="probe"/> the gate read fresh. Nothing shown pending ⇒ nothing to do; the gate open ⇒ a missed wake;
    /// a request out ⇒ wait (bounded); else re-ask once, then give up.</summary>
    public static Verdict Decide(bool shown, in StuckProbe probe, byte stage, int inflightWaits, int wakes)
    {
        if (!shown || stage == GaveUp) return Verdict.None;
        if (!probe.Pending && wakes < MaxWakes) return Verdict.Wake;
        if (probe.Inflight && inflightWaits < MaxInflightWaits) return Verdict.Wait;
        return stage == Watching ? Verdict.Reask : Verdict.GiveUp;
    }

    /// <summary>The timer's fire. <paramref name="key"/> names the subject (a key change starts the watch over),
    /// <paramref name="reask"/> asks again for everything the page waits on, past any seal.</summary>
    public void Fire(long key, bool shown, in StuckProbe probe, string route, Action reask, TimerHandle timer)
    {
        // The timer's own clock: the headless harness runs a virtual frame clock that Environment.TickCount64 never sees.
        long now = (long)timer.NowMs;
        if (_key != key)
        {
            _key = key;
            _inflightWaits = _wakes = 0;
            _since = 0;
            if (_stage.Peek() != Watching) _stage.Value = Watching;
        }
        if (!shown) { _inflightWaits = _wakes = 0; _since = 0; return; }
        if (_since == 0) _since = now - WaitMs;
        byte stage = _stage.Peek();
        switch (Decide(shown, in probe, stage, _inflightWaits, _wakes))
        {
            case Verdict.Wait:
                _inflightWaits++;
                timer.RestartIn(WaitMs);
                return;
            case Verdict.Wake:
                Note(route, "wake", in probe, now);
                _wakes++;
                _wake.Value = _wake.Peek() + 1;
                return;
            case Verdict.Reask:
                Note(route, "reask", in probe, now);
                _stage.Value = Reasked;
                reask();
                return;
            case Verdict.GiveUp:
                Note(route, "fail", in probe, now);
                _stage.Value = GaveUp;
                return;
        }
    }

    /// <summary>`page.stuck route= stage= waiting= inflight= waitedMs=`: always on (a Warning), one line per acting fire.</summary>
    void Note(string route, string stage, in StuckProbe probe, long now)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Warning)) return;
        Log.Event(WaveeLogLevel.Warning, "ui", "page.stuck", "a page stayed on its pending state", null, -1, null,
            WaveeLogField.Of("route", route),
            WaveeLogField.Of("stage", stage),
            WaveeLogField.Of("waiting", probe.Waiting),
            WaveeLogField.Of("inflight", probe.Inflight ? 1 : 0),
            WaveeLogField.Of("waitedMs", (now - _since).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>A watch key from a subject's slot and its scope epoch.</summary>
    public static long KeyOf(int slot, uint epoch) => ((long)epoch << 32) | (uint)slot;

    // ── the detail pages' header gate (album, playlist, show, episode: Detail.FrameHost) ─────────────────────────────

    /// <summary>The group a detail page's HEADER waits on (what <c>Identity.HeaderPending</c> tests) and what its re-ask
    /// asks: the kind's identity group. 0 for a kind with no detail header.</summary>
    public static uint HeaderGroup(EntityKind kind) => kind switch
    {
        EntityKind.Album => (uint)AlbumFields.Identity,
        EntityKind.Playlist or EntityKind.Collection => (uint)PlaylistFields.Identity,
        EntityKind.Show => (uint)ShowFields.Identity,
        EntityKind.Episode => (uint)EpisodeFields.Identity,
        _ => 0u,
    };

    /// <summary>The bit of <see cref="HeaderGroup"/> the header's pending test reads (the title, or a playlist's whole identity).</summary>
    public static uint HeaderTitle(EntityKind kind) => kind switch
    {
        EntityKind.Album => (uint)AlbumFields.Title,
        EntityKind.Playlist or EntityKind.Collection => (uint)PlaylistFields.Identity,
        EntityKind.Show => (uint)ShowFields.Title,
        EntityKind.Episode => (uint)EpisodeFields.Title,
        _ => 0u,
    };

    /// <summary>The fresh probe of a detail page's header: the subject's row, its title bit, its in-flight mark.</summary>
    public static StuckProbe HeaderProbe(EntityUri subject)
    {
        var kind = subject.Kind;
        uint title = HeaderTitle(kind);
        if (title == 0 || Entities.TableFor(kind) is not { } table || !table.TryGetSlot(subject.Id, out int slot))
            return new StuckProbe(false, false, "-");
        uint known = table.Known[slot];
        bool pending = (known & title) != title;
        string waiting = "header=0x" + (HeaderGroup(kind) & ~known).ToString("x", CultureInfo.InvariantCulture)
                         + " asked=0x" + (table.Asked[slot] & HeaderGroup(kind)).ToString("x", CultureInfo.InvariantCulture);
        return new StuckProbe(pending, table.Inflight[slot] != 0, waiting);
    }

    /// <summary>The detail header's re-ask: its identity group again, past any seal.</summary>
    public static void ReaskHeader(EntityUri subject)
    {
        uint group = HeaderGroup(subject.Kind);
        if (group == 0 || Entities.TableFor(subject.Kind) is not { } table || !table.TryGetSlot(subject.Id, out int slot)) return;
        Entities.Refresh(table, new ReadOnlySpan<int>(in slot), group);
    }
}
