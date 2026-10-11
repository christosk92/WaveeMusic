// ── Theming/Contracts/ViewKeys.cs + StyleKeys.cs — the published keys (themes plan 6.0, contract version 1.0) ──────────
//
// The ONLY place a `ViewKey<TProps>` / `StyleKey<TStyle>` is declared (WVTH006): one static field per key, the id is the
// dotted family path (`Shell.Frame`, `Player.Bar`), the props type is a sealed record in this folder or next to the core
// model it carries. Keys land with the PR that extracts their surface (C-lane and T-lane PRs); version 1.0 starts empty
// on purpose: T1 ships the machinery, not a key.
//
//     public static readonly ViewKey<PlayerBarProps> PlayerBar = new("Player.Bar");
//
// A key's id never changes once published (it is persisted nowhere, but a loaded theme package names it).

namespace Wavee.Theming.Contracts;

/// <summary>Every published view key. Partial so each extraction PR adds its family in its own file.</summary>
public static partial class ViewKeys
{
}

/// <summary>Every published style key (the list styles: track table, eager track row, episode row, surface, lyrics lines,
/// artist chart). Partial for the same reason as <see cref="ViewKeys"/>.</summary>
public static partial class StyleKeys
{
}
