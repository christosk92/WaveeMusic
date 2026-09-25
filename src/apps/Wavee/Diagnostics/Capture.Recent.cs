// ── Wavee/Diagnostics/Capture.Recent.cs — the in-app reader's data half (unit 6, realtime-capture-implementation.md §5.5) ──
//
// The in-app Diagnostics page (Screens/Diagnostics.Capture.UI.cs) needs a cheap, in-memory view of "the last N
// causal roots" without re-reading the .idx segment off disk on every 750 ms poll (§3.1's crash-safety story is a
// DISK concern; the live view is a memory concern). Unit 2's writer (`Capture.Host.cs`) does not expose one, so
// this file adds the small, bounded, engine-free pieces the plan asks for:
//
//   CaptureRecentStore   a fixed-capacity ring of HEADER-ONLY CaptureEvents (no payload bytes — §5.5: "bodies stay
//                        opaque to the in-app view by design"), fed by a tee `ICaptureSink` installed alongside the
//                        real writer (RealtimeCaptureHost.Apply, one line — see that file's own comment at the
//                        call site) so the page never touches disk.
//   TeeCaptureSink       forwards one CaptureEvent to both the real writer and the store; the store's own Record
//                        is a short lock + array write, paid on the calling (hot) thread as an ADDITION to what the
//                        writer already pays there — never on its own drain thread.
//   CaptureRootStatus/
//   CaptureRootRollup    PURE: the green/amber/red roll-up dot (§5.5) — folds over one root's own events.
//   CaptureAnomalyKind/
//   CaptureAnomaly/
//   CaptureAnomalyScanner PURE: the Anomalies card's rows (§5.5) — a scan over a flat event snapshot.
//   CaptureRootSummary/
//   CaptureRootGrouping  PURE: groups a flat, oldest-first event snapshot into per-root summaries, most-recent first.
//   CaptureTreeRow/
//   CaptureTreeBuilder   PURE: the depth-tagged causal walk under one root — input → decisions → requests → echoes →
//                        UI outcomes → errors, exactly the shape a Begin/CauseId/RootId chain already encodes.
//
// Every PURE class here takes/returns plain data (CaptureEvent, primitives) — no FluentGpu, no disk, so
// Wavee.Tests exercises them directly per CLAUDE.md's "no source-text tests" rule.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Wavee;

// ── the tee + the store ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Forwards one <see cref="CaptureEvent"/> to both the real sink (the disk writer) and the in-process
/// recent-events store. Installed by <see cref="RealtimeCaptureHost"/> in place of the writer alone (§3.4's Apply).
/// Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Wavee/Platform/Controls.cs</c>), and
/// the forwarding fact (both sinks see every event) is exactly the kind of thing a fact should pin directly.</summary>
public sealed class TeeCaptureSink(ICaptureSink primary, CaptureRecentStore recent) : ICaptureSink
{
    public void Emit(in CaptureEvent evt, ReadOnlyMemory<byte> payload)
    {
        primary.Emit(in evt, payload);
        recent.Record(in evt);
    }
}

/// <summary>A fixed-capacity ring of the most recent <see cref="CaptureEvent"/> HEADERS (payload bytes are never
/// retained here — the in-app tree view only ever shows kind/timing/short A-B-C labels, §5.5). Bounded memory
/// regardless of session length: the oldest event is silently overwritten once the ring is full, exactly like
/// unit 1's own root table (`Capture.cs`'s `s_rootTableIds`).</summary>
public sealed class CaptureRecentStore
{
    /// <summary>The one instance the tee writes to and the Diagnostics page reads from.</summary>
    public static readonly CaptureRecentStore Instance = new();

    const int Capacity = 4096;

    readonly CaptureEvent[] _ring = new CaptureEvent[Capacity];
    readonly Lock _gate = new();
    long _nextIndex;
    int _count;

    public void Record(in CaptureEvent evt)
    {
        lock (_gate)
        {
            _ring[(int)(_nextIndex % Capacity)] = evt;
            _nextIndex++;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Everything currently retained, OLDEST first — the shape every pure reader below expects.</summary>
    public CaptureEvent[] Snapshot()
    {
        lock (_gate)
        {
            var result = new CaptureEvent[_count];
            long start = _nextIndex - _count;
            for (int i = 0; i < _count; i++) result[i] = _ring[(int)((start + i) % Capacity)];
            return result;
        }
    }

    /// <summary>Test/diagnostic reset — never called from production code (the store simply keeps whatever the last
    /// session wrote; capture going off does not erase what a developer just captured).</summary>
    public void Clear()
    {
        lock (_gate) { _count = 0; _nextIndex = 0; }
    }
}

// ── the roll-up dot ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>§5.5's coloured dot: green (everything under this root completed cleanly), amber (a soft failure —
/// a non-2xx, a decode failure, an ignored frame — but the story otherwise completed), red (a Begin with no
/// matching End anywhere under this root — `EchoMissing`, §2.5).</summary>
public enum CaptureRootStatus : byte { Green, Amber, Red }

/// <summary>PURE: folds a root's own events (any order, root's own record optionally included — it is never a
/// Begin/End pair candidate against itself) into one <see cref="CaptureRootStatus"/>. §5.5: "a fold over the
/// header fields already in hand while building the tree, not a new pass."</summary>
public static class CaptureRootRollup
{
    public static CaptureRootStatus Compute(IReadOnlyList<CaptureEvent> eventsUnderRoot)
    {
        var openBegins = new HashSet<long>();
        var closedIds = new HashSet<long>();
        bool amber = false;

        foreach (var e in eventsUnderRoot)
        {
            if (e.Phase == CapturePhase.Begin) openBegins.Add(e.Id);
            else if (e.Phase == CapturePhase.End) closedIds.Add(e.Id);

            if (e.Kind is CaptureKind.DecodeFailed or CaptureKind.FrameIgnored) amber = true;
            if (e.Phase == CapturePhase.End && e.Kind is CaptureKind.HttpCall or CaptureKind.ConnectStatePut
                && (e.Fields.N0 < 200 || e.Fields.N0 > 299)) amber = true;
        }

        foreach (long id in openBegins)
            if (!closedIds.Contains(id)) return CaptureRootStatus.Red;

        return amber ? CaptureRootStatus.Amber : CaptureRootStatus.Green;
    }
}

// ── the Anomalies card ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The kinds of anomaly the Anomalies card (and `query.py --anomalies`) surface — an enum, never a
/// formatted string, per CLAUDE.md's "typed decision records" discipline.</summary>
public enum CaptureAnomalyKind : byte { EchoMissing, NonSuccessStatus, DecodeFailed, FrameIgnored }

/// <summary>One row of the Anomalies card. PURE DATA.</summary>
public readonly record struct CaptureAnomaly(long RootId, long EventId, CaptureAnomalyKind Kind, long UnixMs, string? Summary);

/// <summary>PURE: scans a flat event snapshot (any order) for every anomaly §5.5/§5's `--anomalies` describes,
/// newest first. A Begin with no matching End ANYWHERE in the snapshot is `EchoMissing` — the same read-time
/// inference §2.5 documents (never written by the recorder itself).</summary>
public static class CaptureAnomalyScanner
{
    public static IReadOnlyList<CaptureAnomaly> Scan(IReadOnlyList<CaptureEvent> events)
    {
        var endIds = new HashSet<long>();
        foreach (var e in events)
            if (e.Phase == CapturePhase.End) endIds.Add(e.Id);

        var result = new List<CaptureAnomaly>();
        foreach (var e in events)
        {
            if (e.Phase == CapturePhase.Begin && !endIds.Contains(e.Id))
                result.Add(new CaptureAnomaly(e.RootId, e.Id, CaptureAnomalyKind.EchoMissing, e.UnixMs, e.Fields.A));
            else if (e.Kind == CaptureKind.DecodeFailed)
                result.Add(new CaptureAnomaly(e.RootId, e.Id, CaptureAnomalyKind.DecodeFailed, e.UnixMs, e.Fields.A));
            else if (e.Kind == CaptureKind.FrameIgnored)
                result.Add(new CaptureAnomaly(e.RootId, e.Id, CaptureAnomalyKind.FrameIgnored, e.UnixMs, e.Fields.A));
            else if (e.Phase == CapturePhase.End && e.Kind is CaptureKind.HttpCall or CaptureKind.ConnectStatePut
                     && (e.Fields.N0 < 200 || e.Fields.N0 > 299))
                result.Add(new CaptureAnomaly(e.RootId, e.Id, CaptureAnomalyKind.NonSuccessStatus, e.UnixMs,
                    e.Kind + " " + e.Fields.N0.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        result.Sort(static (a, b) => b.UnixMs.CompareTo(a.UnixMs));
        return result;
    }
}

// ── the recent-roots list ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One row of the "Recent causal roots" card. PURE DATA.</summary>
public readonly record struct CaptureRootSummary(long RootId, CaptureEvent Root, int EffectCount, CaptureRootStatus Status);

/// <summary>PURE: groups a flat, OLDEST-first event snapshot into per-root summaries, MOST-RECENT root first,
/// capped at <paramref name="max"/> — "the last N causal roots" the page and `query.py --story --last N` both
/// describe.</summary>
public static class CaptureRootGrouping
{
    public static IReadOnlyList<CaptureRootSummary> RecentRoots(IReadOnlyList<CaptureEvent> events, int max)
    {
        var byRoot = new Dictionary<long, List<CaptureEvent>>();
        var order = new List<long>(); // first-seen order == chronological, since `events` is oldest-first

        foreach (var e in events)
        {
            if (!byRoot.TryGetValue(e.RootId, out var list))
            {
                list = new List<CaptureEvent>();
                byRoot[e.RootId] = list;
                order.Add(e.RootId);
            }
            list.Add(e);
        }

        var result = new List<CaptureRootSummary>(Math.Min(max, order.Count));
        for (int i = order.Count - 1; i >= 0 && result.Count < max; i--)
        {
            long rootId = order[i];
            var list = byRoot[rootId];
            CaptureEvent rootEvt = list[0];
            bool foundRoot = false;
            foreach (var e in list)
                if (e.Id == rootId) { rootEvt = e; foundRoot = true; break; }

            int effectCount = foundRoot ? list.Count - 1 : list.Count;
            result.Add(new CaptureRootSummary(rootId, rootEvt, effectCount, CaptureRootRollup.Compute(list)));
        }
        return result;
    }
}

// ── the causal tree ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One row of an expanded root's causal tree: the event, and its depth under the root (0 = a direct
/// child). PURE DATA.</summary>
public readonly record struct CaptureTreeRow(CaptureEvent Event, int Depth);

/// <summary>PURE: walks every event under one root into causal + time order (§2.5's exact story shape: input →
/// decisions → requests → echoes → UI outcomes → errors, indented by how many causes deep it sits). An event whose
/// `CauseId` is 0 or unresolved is treated as a direct child of the root — the same degrade-gracefully rule
/// <see cref="CausalityRules.NewEvent"/> already documents for a cause whose own root fell out of the ring.</summary>
public static class CaptureTreeBuilder
{
    const int MaxDepth = 64; // defensive only — a real causal chain never nests anywhere near this deep

    public static IReadOnlyList<CaptureTreeRow> Build(long rootId, IReadOnlyList<CaptureEvent> eventsUnderRoot)
    {
        // Every id this root actually knows about (including its own) — a CauseId that names anything else is
        // UNRESOLVED (the cause's own record fell out of the ring, or belongs to a different snapshot window) and
        // degrades to a direct child of the root, same as CausalityRules.NewEvent's own fallback.
        var knownIds = new HashSet<long> { rootId };
        foreach (var e in eventsUnderRoot) knownIds.Add(e.Id);

        var byCause = new Dictionary<long, List<CaptureEvent>>();
        foreach (var e in eventsUnderRoot)
        {
            if (e.Id == rootId) continue; // the root itself is not a row in its own tree
            long cause = e.CauseId == 0 || e.CauseId == e.Id || !knownIds.Contains(e.CauseId) ? rootId : e.CauseId;
            if (!byCause.TryGetValue(cause, out var list)) { list = new List<CaptureEvent>(); byCause[cause] = list; }
            list.Add(e);
        }
        foreach (var list in byCause.Values) list.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));

        // A Begin and its End SHARE one Id (§2.4's `End` doc comment), so both can appear as bucket entries whose
        // own Id is the key a THIRD event's CauseId points at (e.g. an echo caused by the Begin's id). Walking that
        // id's subtree once per sibling would double the echo under it — `visitedParents` walks each id's own
        // children exactly once, the first time either sibling is reached.
        var visitedParents = new HashSet<long>();
        var rows = new List<CaptureTreeRow>();
        void Walk(long parent, int depth)
        {
            if (depth >= MaxDepth || !byCause.TryGetValue(parent, out var kids)) return;
            foreach (var k in kids)
            {
                rows.Add(new CaptureTreeRow(k, depth));
                if (k.Id != parent && visitedParents.Add(k.Id)) Walk(k.Id, depth + 1);
            }
        }
        Walk(rootId, 0);
        return rows;
    }
}
