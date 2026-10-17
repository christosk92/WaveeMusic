// ── Theming/Contracts/PageChrome.cs — what a page publishes for the frame to draw (themes plan 6.0, T4c) ───────────────────
//
// THE PAGE-CHROME CHANNEL. Some of a page's head is drawn by the frame, not the page: the masthead band ("Browse > Category")
// is one overlay above the keep-alive boundary, never a page's own copy. The core owns the channel (`Shell.Mastheads`, the nav
// origins and `Shell.TryMasthead`, which decide the title and the trail for a route) and hands the active theme ONE value per
// route: this record. A theme's frame-level view decides whether and where to draw it (Classic: the masthead band; a theme that
// puts the trail in its title bar registers nothing for `Shell.Masthead`), and the same theme's page view decides whether the page
// draws its own head.
//
// WHAT IT CARRIES TODAY: the masthead's data (the title, the trail, the "Show all" tool's state, the head's form). The record is the home of the
// rest of the page-chrome plan (views, band pivots, actions, material claim, backdrop); each of those arrives with the lane that
// has data for it, as an additive member with a default, so a theme written against this record keeps compiling.

namespace Wavee.Theming.Contracts;

/// <summary>One crumb of the trail the frame draws above a page. <see cref="Go"/> is null for the CURRENT page (the last crumb,
/// never clickable).</summary>
/// <param name="Label">The crumb's text.</param>
/// <param name="Go">What clicking the crumb does (navigate to its route); null when it is not clickable.</param>
public sealed record PageCrumb(string Label, Action? Go);

/// <summary>What a route's head holds, decided per route (never per trail), so the head keeps one height on its route whatever the
/// trail turns out to be.</summary>
public enum PageHeadForm : byte
{
    /// <summary>A root page: its title and nothing above it.</summary>
    Title,

    /// <summary>A drill-in page: its title under its ancestors' trail (the trail's place is kept while it has nothing to show).</summary>
    Drill,

    /// <summary>A page whose own hero carries its title: the head holds only the ancestors' trail, so the title is never drawn twice.</summary>
    Hero,
}

/// <summary>What a page publishes for the frame to draw above it: its title and trail, and the state of its "Show all" tool.</summary>
/// <param name="Title">The current page's title (the trail's last crumb is the same page).</param>
/// <param name="Crumbs">The whole trail, the current page last. Every crumb but the last is a clickable parent.</param>
/// <param name="ToolsVisible">The page offers a "Show all" tool.</param>
/// <param name="ToolsLoading">The tool is not ready yet (drawn disabled).</param>
/// <param name="Form">What the route's head holds (<see cref="PageHeadForm"/>).</param>
public sealed record PageChrome(string Title, IReadOnlyList<PageCrumb> Crumbs, bool ToolsVisible = false, bool ToolsLoading = false,
    PageHeadForm Form = PageHeadForm.Title);
