// ── Theming/Contracts/PageChrome.cs — what a page publishes for the frame to draw (themes plan 6.0, T4c; row 2: P5 ZK) ───────
//
// THE PAGE-CHROME CHANNEL. Some of a page's head is drawn by the frame, not the page: the masthead band ("Browse > Category")
// is one overlay above the keep-alive boundary, never a page's own copy. The core owns the channel (`Shell.Mastheads`, the nav
// origins and `Shell.TryMasthead`, which decide the title and the trail for a route) and hands the active theme ONE value per
// route: this record. A theme's frame-level view decides whether and where to draw it (Classic: the masthead band; a theme that
// puts the trail in its title bar registers nothing for `Shell.Masthead`), and the same theme's page view decides whether the page
// draws its own head.
//
// ROW 2 (P5 ZK, contract 1.26). A theme with a band (Zune: pivots in row 1, the page's own words in row 2) draws more of the page's
// chrome than its title: the page's VIEWS (in-page tabs or sibling routes, one current), its ACTIONS (play, shuffle, the page's
// words) and its BACKDROP (the bleed image the material paints under the frame, or the covers a "mix" background blends). They are
// additive members with an empty default, so a theme written against the record keeps compiling, and Classic reads none of them.
// They come from two places, merged by the core (`Shell.PageChromeChannel`):
//   · ROUTE LEVEL: a route family whose row 2 is a set of sibling routes (the Library kinds) publishes it once, with no page
//     mounted, so the band shows it on the navigation's first frame (`Shell.PageChromeChannel.RouteViews`);
//   · IN PAGE: a page (its core host, or its theme template) publishes through the `PageChromeSink` the content host provides
//     around every page, without naming the shell. Its views win over the route's; its actions and backdrop are its own.

using FluentGpu.Signals;

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

/// <summary>One view of a page's row 2: an in-page tab or a sibling route (contract 1.26).</summary>
/// <param name="Id">A stable id, unique in its row (a view's node key: never the label, which is localised).</param>
/// <param name="Label">The view's word, as the core spells it (the theme's role decides the casing).</param>
/// <param name="Current">This is the view on screen (one per row at most).</param>
/// <param name="Go">Shows the view (navigates to its route, or selects the in-page tab).</param>
/// <param name="Count">How many items the view holds, when the page knows it (the band may draw it beside the word); null = none shown.</param>
public sealed record PageView(string Id, string Label, bool Current, Action Go, int? Count = null);

/// <summary>What kind of action a row-2 action is, so a theme can draw the primary verbs as its own glyph buttons.</summary>
public enum PageActionKind : byte
{
    /// <summary>A word action (the page's own verbs: "follow", "edit details", ...).</summary>
    Word,

    /// <summary>Play the page's context.</summary>
    Play,

    /// <summary>Shuffle-play the page's context.</summary>
    Shuffle,
}

/// <summary>One action of a page's row 2 (contract 1.26).</summary>
/// <param name="Id">A stable id, unique in its row.</param>
/// <param name="Label">The action's word (the tooltip of a glyph action).</param>
/// <param name="Invoke">The verb.</param>
/// <param name="Kind">Play, shuffle or a word.</param>
/// <param name="Enabled">The verb can run now (drawn disabled otherwise).</param>
public sealed record PageAction(string Id, string Label, Action Invoke, PageActionKind Kind = PageActionKind.Word, bool Enabled = true);

/// <summary>What the page claims for the material behind the frame (contract 1.26): the image a theme bleeds under its band and page
/// head, or the covers a "mix" background blends. Image references are the same uri strings every other props record carries.</summary>
/// <param name="Bleed">The one image the page bleeds (an artist's header, an album's cover); null = none.</param>
/// <param name="Mix">The images a "mix" background blends (a playlist's or a home zone's covers); null or empty = none.</param>
public sealed record PageBackdrop(string? Bleed, IReadOnlyList<string>? Mix = null)
{
    /// <summary>Claims nothing.</summary>
    public static PageBackdrop None { get; } = new(Bleed: null);

    /// <summary>Nothing is claimed.</summary>
    public bool IsNone => Bleed is null && (Mix is null || Mix.Count == 0);
}

/// <summary>What a page publishes for row 2 through its <see cref="PageChromeSink"/> (contract 1.26).</summary>
/// <param name="Views">The page's views (empty = the route's own row stands).</param>
/// <param name="Actions">The page's actions.</param>
/// <param name="Backdrop">The page's backdrop claim; null = none.</param>
public sealed record PageChromeRow(IReadOnlyList<PageView> Views, IReadOnlyList<PageAction> Actions, PageBackdrop? Backdrop = null)
{
    /// <summary>Publishes nothing.</summary>
    public static PageChromeRow Empty { get; } = new([], []);

    /// <summary>Holds nothing (no view, no action, no backdrop).</summary>
    public bool IsEmpty => Views.Count == 0 && Actions.Count == 0 && (Backdrop is null || Backdrop.IsNone);
}

/// <summary>What a page publishes for the frame to draw above it: its title and trail, and the state of its "Show all" tool. Since
/// contract 1.26 also row 2: its views, its actions and its backdrop (empty unless the route or the page published them).</summary>
/// <param name="Title">The current page's title (the trail's last crumb is the same page).</param>
/// <param name="Crumbs">The whole trail, the current page last. Every crumb but the last is a clickable parent.</param>
/// <param name="ToolsVisible">The page offers a "Show all" tool.</param>
/// <param name="ToolsLoading">The tool is not ready yet (drawn disabled).</param>
/// <param name="Form">What the route's head holds (<see cref="PageHeadForm"/>).</param>
public sealed record PageChrome(string Title, IReadOnlyList<PageCrumb> Crumbs, bool ToolsVisible = false, bool ToolsLoading = false,
    PageHeadForm Form = PageHeadForm.Title)
{
    /// <summary>Row 2's views, in order, at most one <see cref="PageView.Current"/> (contract 1.26). Empty: the page has no views (a
    /// detail page), and a band collapses row 2 or draws the page's title and actions there instead.</summary>
    public IReadOnlyList<PageView> Views { get; init; } = [];

    /// <summary>Row 2's actions, in order (contract 1.26).</summary>
    public IReadOnlyList<PageAction> Actions { get; init; } = [];

    /// <summary>The page's backdrop claim (contract 1.26); <see cref="PageBackdrop.None"/> when it claims none.</summary>
    public PageBackdrop Backdrop { get; init; } = PageBackdrop.None;

    /// <summary>The current view, or null.</summary>
    public PageView? CurrentView
    {
        get
        {
            for (int i = 0; i < Views.Count; i++) if (Views[i].Current) return Views[i];
            return null;
        }
    }
}

/// <summary>The in-page half of the row-2 channel (contract 1.26): the content host provides one per page (its route), so a page's core
/// host or its theme template publishes its views, actions and backdrop without naming the shell. Publish from an effect (never from
/// a render: a publication is a signal write), and withdraw (<c>null</c>) from the effect's cleanup when the row stops being true. A
/// parked page's publication stays under its route, so its row is back on the frame the page is.
/// <para>The sink also takes the page's main scroller (<see cref="ReportScroll"/>): the frame derives the page-scroll signal a band
/// collapses on from the current page's scroller.</para></summary>
public sealed class PageChromeSink
{
    /// <summary>The sink that drops every publication (where no content host provides one: a view mounted alone).</summary>
    public static PageChromeSink None { get; } = new(static _ => { }, static _ => { });

    /// <summary>The context the content host provides around each page.</summary>
    public static readonly FluentGpu.Hooks.Context<PageChromeSink> Context = new(None);

    private readonly Action<PageChromeRow?> _publish;
    private readonly Action<IReadSignal<double>?> _scroll;

    /// <summary>A sink over the core's publication verbs (the content host builds one per page route).</summary>
    public PageChromeSink(Action<PageChromeRow?> publish, Action<IReadSignal<double>?> scroll)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _scroll = scroll ?? throw new ArgumentNullException(nameof(scroll));
    }

    /// <summary>Publishes the page's row 2 (null or an empty row withdraws it).</summary>
    public void Publish(PageChromeRow? row) => _publish(row);

    /// <summary>Names the page's main vertical scroller's offset (a <c>ScrollHandle.Offset</c>); null withdraws it.</summary>
    public void ReportScroll(IReadSignal<double>? offset) => _scroll(offset);
}
