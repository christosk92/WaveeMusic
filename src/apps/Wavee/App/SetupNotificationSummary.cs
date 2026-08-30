using System;
using Wavee.Core;

namespace Wavee;

/// <summary>Pure numbers for the setup wizard's Notifications page (page 7). Engine-free by construction (System +
/// <c>Wavee.Core</c> only — no <c>FluentGpu.*</c>, no <c>Loc</c>, no <c>Signal&lt;T&gt;</c>), exactly like
/// <c>SetupGating</c>/<c>SetupRuntimePresentation</c>: this file is source-included by <c>Wavee.Tests</c> so
/// <c>SetupNotificationSummaryTests</c> drives the REAL topic partition and quiet-hours arithmetic instead of a copy
/// of it.
///
/// <para><see cref="HeadlineTopics"/> (rendered directly on the decision column) and <see cref="MoreTopics"/>
/// (behind the "More topics &amp; quiet hours" flyout) are the ONE partition of <see cref="NotifyTopic"/> — a topic
/// added to the enum and forgotten in both lists fails <c>SetupNotificationSummaryTests</c> loudly instead of
/// silently missing a dial (the same "one list is the UI order" discipline <c>NotificationPrefs.AllTopics</c>
/// already uses). <see cref="MoreCount"/> is <em>computed</em> from <see cref="MoreTopics"/>.Length rather than a
/// literal, which is what actually fixes the shipped page's stale "{count} more" (it said 5 while listing 6 rows in
/// an earlier revision) — a literal can drift from the list beside it, a computed count cannot.</para></summary>
static class SetupNotificationSummary
{
    /// <summary>The three headline topics rendered directly on the decision column, each with its own row.</summary>
    public static readonly NotifyTopic[] HeadlineTopics =
    [
        NotifyTopic.NewAlbums,
        NotifyTopic.ReleaseDrops,
        NotifyTopic.Concerts,
    ];

    /// <summary>Every remaining topic, folded behind the "More topics &amp; quiet hours" flyout
    /// (<c>SetupMoreTopicsPanel</c>). Declaration order is the panel's own row order.</summary>
    public static readonly NotifyTopic[] MoreTopics =
    [
        NotifyTopic.NewEpisodes,
        NotifyTopic.Followers,
        NotifyTopic.DaylistRefresh,
        NotifyTopic.AppUpdates,
        NotifyTopic.LibraryActivity,
    ];

    /// <summary>How many topics live behind the flyout — the number the "More topics & quiet hours" row's own
    /// trailing text quotes. Computed from <see cref="MoreTopics"/> so the count can never drift from the list that
    /// actually backs it.</summary>
    public static int MoreCount => MoreTopics.Length;

    /// <summary>The setup wizard's three quiet-hours shortcuts (index 0 = off), moved verbatim from the shipped
    /// page's own <c>s_quietPresets</c> — a genuine simplification of the Settings tab's toggle + two independent
    /// hour combos for a first-run screen, not a data-model fiction: all three are exact, real
    /// <c>(Enabled, FromHour, ToHour)</c> triples a <see cref="QuietHours"/> could hold anyway.</summary>
    public static readonly (bool Enabled, int From, int To)[] QuietPresets =
    [
        (false, 22, 8),
        (true, 23, 8),
        (true, 0, 7),
    ];

    /// <summary>Which preset (if any) the stored quiet-hours triple matches. A settings file holding a triple no
    /// preset produces (hand-edited, or written by a future build with a fourth shortcut) resolves to 0 (Off) rather
    /// than throwing or picking an arbitrary nearby preset.</summary>
    public static int QuietPresetIndex(bool enabled, int fromHour, int toHour)
    {
        for (int i = 0; i < QuietPresets.Length; i++)
        {
            var p = QuietPresets[i];
            if (p.Enabled == enabled && (!enabled || (p.From == fromHour && p.To == toHour))) return i;
        }
        return 0;
    }

    /// <summary>A wall-clock hour as "HH:00", for the stage pill's "Quiet 23:00–08:00" and the flyout row's trailing
    /// text. Hours outside 0..23 wrap rather than produce a malformed string — the same defensive posture
    /// <see cref="QuietHours.Normalized"/> takes for a corrupt settings file.</summary>
    public static string Clock(int hour)
    {
        int h = ((hour % 24) + 24) % 24;
        return h < 10 ? "0" + h.ToString() + ":00" : h.ToString() + ":00";
    }

    /// <summary>How many of the given topic levels have actually reached <see cref="NotifyLevel.Windows"/> — the
    /// stage pill's "N topics can reach the Action Center" count when the master gate is on.</summary>
    public static int WindowsReachCount(ReadOnlySpan<NotifyLevel> levels)
    {
        int n = 0;
        for (int i = 0; i < levels.Length; i++)
            if (levels[i] == NotifyLevel.Windows) n++;
        return n;
    }

    /// <summary>The "More topics & quiet hours" row's trailing summary: how many topics live behind it, and whether
    /// (and to what window) quiet hours currently apply.</summary>
    public readonly record struct MoreSummary(int Count, bool QuietOn, string From, string To);

    public static MoreSummary Summarize(QuietHours quiet) => new(MoreCount, quiet.Enabled, Clock(quiet.FromHour), Clock(quiet.ToHour));
}
