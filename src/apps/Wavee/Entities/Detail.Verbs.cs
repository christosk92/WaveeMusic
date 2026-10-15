// ── Entities/Detail.Verbs.cs — the core verbs the detail frame's views call (themes plan 6.1, T9a) ────────────────────────────────────────────
//
// A structure's views may not name `Shell`, `Playback`, `Entities`, `Store` or `Prefs` (WVTH002): what a detail view must DO that touches them is a
// static verb here, the way `HomeVerbs` serves Home. The pictures call them as handlers; the decisions (which route, which context, which date
// label) are core.
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §6.1 (T9a)

using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The navigation, playback and text verbs the structure's detail views call.</summary>
public static class DetailVerbs
{
    /// <summary>A rich-text anchor's route key → navigation (the description's links).</summary>
    public static readonly Action<string> NavigateKey = static key => Shell.GoTo(Shell.Parse(key));

    /// <summary>The page owner's profile link (#161): the verb that opens it, or null when the page carries no owner uri (a local playlist's
    /// "On this device") — the view then draws the owner inert.</summary>
    public static Action? OpenOwner(EntityUri owner, string name)
    {
        var profile = ProfileRoute.For(owner, name);
        return profile.IsNone ? null : () => Shell.GoTo(profile);
    }

    /// <summary>The page subject's default play: pause / resume the context when it is already the one playing, else start it.</summary>
    public static Action PlayContext(EntityUri subject) => () => Playback.PlayOrToggleContext(subject.Id);

    /// <summary>The Play split's "More ways to play" menu (tuned Classic D2), always these three on the page subject: Add to queue,
    /// Play next (its members, through the container verbs' path) and Start radio (seeded by its first song).</summary>
    public static IReadOnlyList<MenuFlyoutItem> PlayMenu(EntityUri subject) =>
    [
        new(Loc.Get(Strings.Detail.AddToQueue), ActionIcons.Resolve(ActionIcons.Queue), true, () => Queue.QueueContext(subject, next: false)),
        new(Loc.Get(Strings.Detail.PlayNext), ActionIcons.Resolve(ActionIcons.PlayNext), true, () => Queue.QueueContext(subject, next: true)),
        new(Loc.Get(Strings.Detail.StartRadio), ActionIcons.Resolve(ActionIcons.Radio), true, () => Queue.StartContextRadio(subject)),
    ];

    /// <summary>The chart playlist's default caption: "N new entries · date" (W28).</summary>
    public static string ChartCaptionText(Detail.Identity id)
        => Strings.Detail.ChartNewEntries(id.ChartNewEntries,
            Track.Format.ChartUpdatedDateLabel(id.ChartUpdatedAtMs, Store.ToUnix(Entities.Now)));
}
