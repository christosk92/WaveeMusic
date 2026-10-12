// ── Theming/Contracts/ContentStateProps.cs — the props of `Shell.ContentState` (themes plan 6.0, T4a) ──────────────────────
//
// One view key, four kinds (`ContentStateKind`). The core content host decides the state (`Shell.FrameRules.ContentFor`),
// builds ONE of these per mounted state and mounts `Slots.View(ViewKeys.ShellContentState, props)`; the theme's template
// switches on `Kind` and draws it. A props record freezes at mount: every live value reaches the template through the page
// element it hosts or a verb, never through a changing member.
//
//   Page          the registered page, built by the core (`Page`), in a body box keyed `Key` (`page:<route>`)
//   EmptyBody     an empty body box keyed `Key` (`page-empty:<route>`)
//   NotFound      the route's own glyph (`Glyph`), the not-found line and the one action that always works (`GoHome`), keyed
//                 `Key` (`page-notfound:<route>|<arg>`)
//   OfflineLane   the strip over the kept content; `OfflineStrip` builds the strip itself (the offline strip is its own
//                 surface and gets its own key in the overlays PR, T11a)
//
// Delegates are cached statics of the core (equal props compare equal by reference), so an unchanged state skips the template.

using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellContentState"/>.</summary>
public sealed record ContentStateProps
{
    private static readonly Action s_nothing = static () => { };
    private static readonly Func<Element> s_nothingToShow = static () => new BoxEl();

    /// <summary>Which of the four states this mount draws.</summary>
    public ContentStateKind Kind { get; init; }

    /// <summary>The node key of the state's root box (<c>page:Home</c>, <c>page-empty:…</c>, <c>page-notfound:…</c>); empty for the lane.</summary>
    public string Key { get; init; } = "";

    /// <summary>The page the core built for a <see cref="ContentStateKind.Page"/>; null for the other kinds.</summary>
    public Element? Page { get; init; }

    /// <summary>The route's own glyph, drawn by the not-found page.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>The not-found page's action: go to Home.</summary>
    public Action GoHome { get; init; } = s_nothing;

    /// <summary>Builds the offline strip the lane floats.</summary>
    public Func<Element> OfflineStrip { get; init; } = s_nothingToShow;

    /// <summary>A page fills the body.</summary>
    public static ContentStateProps ForPage(string key, Element page)
        => new() { Kind = ContentStateKind.Page, Key = key, Page = page };

    /// <summary>An empty body.</summary>
    public static ContentStateProps ForEmptyBody(string key)
        => new() { Kind = ContentStateKind.EmptyBody, Key = key };

    /// <summary>The not-found page.</summary>
    public static ContentStateProps ForNotFound(string key, string glyph, Action goHome)
        => new() { Kind = ContentStateKind.NotFound, Key = key, Glyph = glyph, GoHome = goHome };

    /// <summary>The offline lane.</summary>
    public static ContentStateProps ForOfflineLane(Func<Element> offlineStrip)
        => new() { Kind = ContentStateKind.OfflineLane, OfflineStrip = offlineStrip };
}
