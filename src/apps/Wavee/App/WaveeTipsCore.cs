using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The stable ids of every teaching tip the app can show. <b>APPEND-ONLY:</b> an id is PERSISTED (it lands in
/// <c>WaveeSettings.TipsSeen</c> the moment a user acknowledges that tip), so renaming or reusing one would silently
/// re-show a tip an existing install already dismissed. Add a new const; never edit an old one.
///
/// <para>Ids are dotted, feature-first (<c>detail.tuning</c>, later <c>sidebar.customizer</c>…) and deliberately do NOT
/// include the loc-key suffix: a tip's copy keys are passed to <c>WaveeTips.TryShow</c> at the call site, so a wording
/// change never touches this table. (Plain <c>c</c>, not a cref: this file is source-included by Wavee.Tests, where the
/// engine-bound <c>WaveeTips</c> service is deliberately absent.)</para></summary>
static class WaveeTipIds
{
    /// <summary>The playlist detail command bar's <c>Tune ▾</c> command (Features/Detail/DetailTracks.cs).</summary>
    public const string DetailTuning = "detail.tuning";
}

/// <summary>The PURE half of the teaching-tip service (WaveeTipsCoreTests): the acknowledged-id SET codec and the gating
/// decision. Engine-free by construction — System + BCL only, no <c>IAppSettings</c>, no FluentGpu — so the rules that
/// decide whether a tip may appear are unit-tested without a GPU, a window, or a settings store.
///
/// <para>The set is newline-joined (the <c>WaveeSettings.SavedLibrary</c> precedent: <c>AppDataStore</c> round-trips
/// scalars only, and a newline cannot occur inside a tip id). Empty segments are ignored on read and never produced on
/// write, so a hand-edited or older value can't wedge the codec.</para></summary>
static class WaveeTipsCore
{
    /// <summary>The set separator. A tip id may never contain it (ids are dotted ASCII — see <see cref="WaveeTipIds"/>).</summary>
    public const char Separator = '\n';

    /// <summary>True when <paramref name="tipId"/> is one of the acknowledged ids in <paramref name="seen"/>. Scans in
    /// place (no split, no allocation) — this runs on every eligible render of every tip's host.</summary>
    public static bool Contains(string? seen, string? tipId)
    {
        if (string.IsNullOrEmpty(seen) || string.IsNullOrEmpty(tipId)) return false;
        int i = 0;
        while (i <= seen.Length)
        {
            int end = seen.IndexOf(Separator, i);
            if (end < 0) end = seen.Length;
            if (end - i == tipId.Length && string.CompareOrdinal(seen, i, tipId, 0, tipId.Length) == 0) return true;
            i = end + 1;
        }
        return false;
    }

    /// <summary>The set with <paramref name="tipId"/> added — IDEMPOTENT (an already-present id returns the input
    /// unchanged, so re-acknowledging never grows the string) and order-preserving (append at the end).</summary>
    public static string Add(string? seen, string? tipId)
    {
        if (string.IsNullOrEmpty(tipId)) return seen ?? "";
        if (Contains(seen, tipId)) return seen!;
        return string.IsNullOrEmpty(seen) ? tipId : seen + Separator + tipId;
    }

    /// <summary>The acknowledged ids in stored order, empty segments dropped. For tests and a future diagnostics view —
    /// the hot paths use <see cref="Contains"/>.</summary>
    public static List<string> Parse(string? seen)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(seen)) return ids;
        int i = 0;
        while (i <= seen.Length)
        {
            int end = seen.IndexOf(Separator, i);
            if (end < 0) end = seen.Length;
            if (end > i) ids.Add(seen.Substring(i, end - i));
            i = end + 1;
        }
        return ids;
    }

    /// <summary>Serialize an id set back to storage form (dedup + drop empties, order preserved).</summary>
    public static string Serialize(IEnumerable<string>? ids)
    {
        if (ids is null) return "";
        string acc = "";
        foreach (var id in ids) acc = Add(acc, id);
        return acc;
    }

    /// <summary>The one gating decision, in one place. A tip may be armed only when:
    /// <list type="bullet">
    /// <item><paramref name="canPresent"/> — the caller has everything it needs to actually SHOW it (an overlay service,
    /// a persistence seam, a realized anchor). A tip whose acknowledgement cannot be persisted is never shown at all:
    /// it would return on every page forever, and nagging is worse than never teaching.</item>
    /// <item>the id is not in <paramref name="seen"/> — the durable "don't show again".</item>
    /// <item><paramref name="armedThisSession"/> is false — at most one appearance per launch per tip, so walking away
    /// without acknowledging does not re-open it on the next page that hosts the same tip.</item>
    /// <item><paramref name="anotherTipActive"/> is false — ONE tip at a time, process-wide. Two callouts up together
    /// read as an error state, and the second would fight the first for the user's attention.</item>
    /// </list></summary>
    public static bool ShouldShow(string? seen, string? tipId, bool armedThisSession, bool anotherTipActive, bool canPresent)
        => canPresent
        && !string.IsNullOrEmpty(tipId)
        && !armedThisSession
        && !anotherTipActive
        && !Contains(seen, tipId);
}
