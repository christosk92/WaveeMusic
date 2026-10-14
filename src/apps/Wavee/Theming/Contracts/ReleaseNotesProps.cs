// ── Theming/Contracts/ReleaseNotesProps.cs — the props of the release-notes views (themes plan 6.0, T11c) ────────────────────────────────────
//
// The core hosts keep everything that decides (the load, last-seen, the gates, the stepping rule, the overlays) and hand the views the VALUES and VERBS they draw.
// A view's own state (the section's "Show all N", a card's overflow answer, the viewer's index and pager mirror, the plate's checkbox) is the view's: it lives in
// the slot's hook cells or in a component the view embeds. The release-notes records (`ReleaseNotes.ReleaseNotesView`, `HighlightItem`) are the core's immutable
// view model; the loader assembles them off the UI thread, so a view never touches the disk or the network.

namespace Wavee.Theming.Contracts;

/// <summary>Which body the What's-new page shows.</summary>
public enum WhatsNewState : byte
{
    /// <summary>The load has not answered: a ring.</summary>
    Loading,
    /// <summary>The load answered with nothing: the vacancy with "Open on GitHub".</summary>
    Empty,
    /// <summary>The page has its <see cref="WhatsNewPageProps.View"/>.</summary>
    Loaded,
}

/// <summary>The props of <see cref="ViewKeys.WhatsNewPage"/>. The host builds one per render it makes (a state change re-renders it), so it is never the same
/// instance twice.</summary>
public sealed record WhatsNewPageProps
{
    /// <summary>Loading, empty or loaded.</summary>
    public required WhatsNewState State { get; init; }

    /// <summary>The whole view (null unless <see cref="State"/> is <see cref="WhatsNewState.Loaded"/>).</summary>
    public required ReleaseNotes.ReleaseNotesView? View { get; init; }

    /// <summary>The since banner's "Only the latest" was pressed: the stacked releases collapse to the newest.</summary>
    public required bool OnlyLatest { get; init; }

    /// <summary>The running build's core version (the rail's YOU marker).</summary>
    public required string RunningVersion { get; init; }

    /// <summary>The since banner's "Only the latest".</summary>
    public required Action ShowOnlyLatest { get; init; }

    /// <summary>The hero's "Copy link": the clipboard and its toast.</summary>
    public required Action<string> CopyLink { get; init; }

    /// <summary>A strip card was invoked: open the viewer on merged highlight <c>i</c>.</summary>
    public required Action<int> OpenHighlight { get; init; }

    /// <summary>Open a URL in the browser.</summary>
    public required Action<string> OpenUrl { get; init; }

    /// <summary>The Store card's "Get it from the Microsoft Store".</summary>
    public required Action OpenStoreListing { get; init; }

    /// <summary>A rail row: navigate to that release's page (a new keep-alive slot keyed by the arg).</summary>
    public required Action<string> OpenRelease { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ReleaseNotesViewer"/>. Frozen at mount: the items and the first index are final.</summary>
public sealed record HighlightViewerProps
{
    /// <summary>The highlights, in strip order.</summary>
    public required IReadOnlyList<ReleaseNotes.HighlightItem> Items { get; init; }

    /// <summary>The first slide.</summary>
    public required int Initial { get; init; }

    /// <summary>Closes the viewer (the overlay handle's close).</summary>
    public required Action Close { get; init; }

    /// <summary>The action a highlight's "Try it" runs (navigate, then the host plate's close, then this viewer's), or null when the highlight has no in-app
    /// route (no button).</summary>
    public required Func<ReleaseNotes.ReleaseHighlight, Action?> TryItFor { get; init; }

    /// <summary>Open a URL in the browser (the watch pill, the body's links).</summary>
    public required Action<string> OpenUrl { get; init; }

    /// <summary>The Store card's "Get it from the Microsoft Store".</summary>
    public required Action OpenStoreListing { get; init; }
}
/// <summary>The props of <see cref="ViewKeys.ReleaseNotesAfterUpdatePlate"/>. Every input is final at mount (the load already landed).</summary>
public sealed record AfterUpdatePlateProps
{
    /// <summary>The "from → to" mono pill: the previous run's quad with its fourth part dropped, then the running quad verbatim.</summary>
    public required string VersionRange { get; init; }

    /// <summary>The build as a localized sentence (<c>Wavee 0.3.0 “Crest”</c>), for the welcome line.</summary>
    public required string VersionText { get; init; }

    /// <summary>The release's tagline ("" when the notes resolved to nothing).</summary>
    public required string Tagline { get; init; }

    /// <summary>The visible highlight cards (at most <see cref="ReleaseNotes.HighlightMax"/>).</summary>
    public required ReleaseNotes.HighlightItem[] Cards { get; init; }

    /// <summary>A card was invoked: open the viewer on card <c>i</c> (the plate is the viewer's close-host).</summary>
    public required Action<int> OpenCard { get; init; }

    /// <summary>"Don't show again": written IMMEDIATELY, not on close.</summary>
    public required Action<bool> SetDontShowAgain { get; init; }

    /// <summary>"Full release notes": close, THEN navigate (the reverse of the viewer's "Try it": this plate is the thing being replaced).</summary>
    public required Action ShowFull { get; init; }

    /// <summary>"Got it" and Escape: close the plate.</summary>
    public required Action Close { get; init; }

    /// <summary>The Store card's "Get it from the Microsoft Store".</summary>
    public required Action OpenStoreListing { get; init; }
}
