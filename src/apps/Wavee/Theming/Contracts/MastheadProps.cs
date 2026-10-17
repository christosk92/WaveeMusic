// ── Theming/Contracts/MastheadProps.cs — the props of `Shell.Masthead` and `Page.Head` (themes plan 6.0, T4c) ──────────────
//
// `Shell.Masthead` is the band the content host overlays on its page-swap boundary. The core host (`Shell.MastheadBand`) keeps
// the state machine: which route's publication is live, the last trail it HELD while an unknown family fades (a trail never
// collapses to nothing mid-swap), the "Show all" delegate resolved at click time. It hands the theme the held `PageChrome`, whether
// it is live, and the tool verb; the theme draws the band. `Page.Head` is the trail-as-title row the band places (Classic) and
// that a page may place itself (a theme that draws its head in-page).

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellMasthead"/>.</summary>
public sealed record MastheadProps
{
    private static readonly Action s_nothing = static () => { };

    /// <summary>What the band shows: the live publication, or the last one it held. Null until a masthead family has ever been
    /// shown (the band is an invisible, hit-test-free box).</summary>
    public PageChrome? Held { get; init; }

    /// <summary>Does the current route belong to a masthead family? False while the held chrome fades out.</summary>
    public bool Live { get; init; }

    /// <summary>The "Show all" tool: resolves the LATEST published delegate when it is invoked. A cached delegate of the host, so an
    /// equal record skips the template.</summary>
    public Action RunTools { get; init; } = s_nothing;
}

/// <summary>The props of <see cref="ViewKeys.PageHead"/>: the head of a page, drawn from its chrome.</summary>
/// <param name="Chrome">The title and the trail.</param>
public sealed record PageHeadProps(PageChrome Chrome);
