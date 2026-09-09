using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The expanded track drawer's VERSION ROW SET, decided in one engine-free place — the
/// <c>AlbumDrawerVerdict</c> discipline applied to the track drawer: the row set is what the drawer's height IS
/// (every entry is exactly one <c>TrackVersionsPanel.RowH</c> band), so the number the virtualized list reserves for
/// the row and the number the panel renders come from the SAME computation and cannot drift.
///
/// <para>WHY IT MATTERS HERE AND NOT ELSEWHERE. The drawer lives inside a MEASURED virtualized row
/// (<c>RepeatLayout.Measured</c>): the list reserves whatever the slot measured, and the drawer's height is driven by
/// a <c>SizeMode.Reflow</c> reveal whose target is re-derived from the solved child extent only WHILE the tween is in
/// flight. So a row set that changes shape across that window is the one thing that makes the reserved band and the
/// painted content disagree — content past the band in one direction, an empty band in the other. The contract this
/// class exists to keep is therefore "the first solved height is the final height": everything KNOWABLE before the
/// expansion fetch answers is reserved up front, at the same key the resolved row will carry, so data landing patches
/// each row IN PLACE instead of inserting one.</para>
///
/// <para>Split into its own file (Wavee.Core + BCL only — no FluentGpu) so <c>Wavee.Tests</c> can source-include it
/// and drive the real decision, exactly like <c>RowPresentation.cs</c> and <c>DetailTrackProjection.cs</c>
/// beside it.</para></summary>
internal static class TrackDrawerRows
{
    /// <summary>The stable identity of a version ROW — deliberately NOT always its uri.
    ///
    /// <para>The music-video row is RESERVED before the expansion fetch answers, and at that moment its target uri is
    /// precisely the thing not yet known. So the video row is keyed by its KIND instead: kind 99 yields at most ONE
    /// counterpart, so the kind is a complete identity for it. The placeholder and the hydrated row therefore mint the
    /// SAME key, and data landing reconciles that row in place — no remove+insert, no remount flicker, and no height
    /// step for the drawer's reflow to chase. Every other row keeps its uri key, which is what tells several
    /// alternate-audio entries apart.</para></summary>
    internal const string VideoRowKey = "v:video";

    internal static string KeyOf(TrackVersion v) => v.Kind == TrackVersionKind.Video ? VideoRowKey : v.Uri;

    /// <summary>The reserved music-video row while the fetch is in flight. The empty uri IS the placeholder flag: it is
    /// what the row factory routes on, and it keeps the reserved row out of the now-playing comparison.</summary>
    internal static readonly TrackVersion PendingVideo = new("", TrackVersionKind.Video, "", null);

    /// <summary>True for the reserved placeholder — a row that must render its geometry but offer no affordance,
    /// because a row that cannot say WHICH video it is must not offer to play one.</summary>
    internal static bool IsPlaceholder(TrackVersion v) => v.Uri.Length == 0;

    /// <summary>The row's own track, projected as a version so ONE row factory renders every entry.</summary>
    internal static TrackVersion SelfVersion(Track t) => new(
        t.Uri, TrackVersionKind.Original, t.Title, t.Image, t.DurationMs,
        t.TempoBpm, t.MusicalKey, t.CamelotCode, t.CamelotColor);

    /// <summary>Fill <paramref name="dst"/> with the drawer's rows, in render order.
    ///
    /// <para>Flat, in order: the GUARANTEED self row, then the music video, then any alternate audio. No group
    /// headings — each association kind yields at most one row and the thumbnail aspect says which is which.</para>
    ///
    /// <para><paramref name="hasVideo"/> is the kind-99 association plane's verdict (the SAME publication the row's
    /// trailing film lane projects from), so reserving the video row BEFORE the fetch answers repeats a verdict rather
    /// than guessing — that reservation is what makes the first solved height the final height for the overwhelmingly
    /// common case. Once the fetch HAS answered it is authoritative in both directions: it replaces the placeholder at
    /// the same key when it carries the video, and drops the band when it does not (a placeholder that can never
    /// resolve would shimmer forever, which is a worse lie than a one-band correction).</para>
    ///
    /// <para>AT MOST ONE video row, whatever the payload says. Kind 99 yields at most one counterpart, the reserved
    /// band accounts for exactly one, and <see cref="VideoRowKey"/> can only name one — appending every kind-99 entry
    /// (as the render path used to) would insert bands no reservation covered AND mint duplicate keys under the same
    /// parent, which is a keyed-reconcile collision.</para>
    ///
    /// <para>Alternate audio has no pre-fetch predicate at all, so those rows are the one genuinely late-arriving
    /// growth; they append after the video row and never reorder what is already reserved.</para></summary>
    internal static void Fill(List<TrackVersion> dst, Track track, TrackExpansion? expansion, bool hasVideo)
    {
        dst.Clear();
        dst.Add(SelfVersion(track));

        if (expansion is null)
        {
            if (hasVideo) dst.Add(PendingVideo);          // reserved band: same key, same height, no affordance
            return;
        }

        for (int i = 0; i < expansion.Versions.Count; i++)
        {
            var v = expansion.Versions[i];
            if (v.Kind != TrackVersionKind.Video) continue;
            dst.Add(v);
            break;                                       // at most ONE — see the note above
        }
        for (int i = 0; i < expansion.Versions.Count; i++)
            if (expansion.Versions[i].Kind == TrackVersionKind.Audio) dst.Add(expansion.Versions[i]);
    }

    /// <summary>Allocating convenience for callers that are not on a render path (and for the tests).</summary>
    internal static List<TrackVersion> For(Track track, TrackExpansion? expansion, bool hasVideo)
    {
        var rows = new List<TrackVersion>(3);
        Fill(rows, track, expansion, hasVideo);
        return rows;
    }
}
