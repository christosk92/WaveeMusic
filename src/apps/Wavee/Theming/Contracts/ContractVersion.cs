// ── Theming/Contracts/ContractVersion.cs — the version of the contract between core and a theme ───────────────────────
//
// A theme is built against ONE contract version (`ThemeManifest.ContractMajor/ContractMinor`). The contract is the set of
// published view keys and style keys with their props records, the verbs and state enums those props carry, and the
// role vocabulary (`Wavee.Theming.Roles.WaveeStyleRoles`). Version 1.0 is the key list of themes plan section 6.0.
//
// 1.1 (tuned Classic CT4): `LyricsState.Offline` and the optional `LyricsLinesStyle.Offline` picture; `PlayerBarSlots.VideoSplit`
// (the video split button's own level bit; it used to ride `Queue`).
//
// 1.2 (tuned Classic CT1c): the omnibar model's `SuggestionsOpen`, `FieldExpanded`, `FieldSettled` and `ReportFieldWidth` (the field
// eases to its focused width and the title bar re-pushes its regions once it settles). Members nothing used any more were removed
// before any external theme existed: `ShellTitleBarProps.ToggleTheme`, `SearchSuggestionsProps.AllowNarrow`, `ShellSearchProps.Avail`,
// and `ShellSearchProps.FieldWidth` takes no measurement (`Func<float>`).
//
// 1.3 (tuned Classic CT3a): `SidebarRowShape.EntityText` (the covers-off text row), the optional `NavPanePolicy.ShowCovers`, and
// the nav row host's `UnpinAction` (the row's Unpin button).
//
// 1.4 (tuned Classic CT5c): the track row lanes lose the film lane — `Track.ColumnSet.Video`,
// `TrailingColumns.Video` and `CellKey.Video` are gone (a video is the glyph after the title in both row styles) — and a row
// template reads `Track.HasVersions()` for the versions chevron, `Track.DurationLabel` / `Track.WithheldReason` for the
// duration lane. `GridOptions.HeartRevealOnHover` / `EagerRowOptions.HeartRevealOnHover` are removed: every heart lane reveals.
// Removed before any external theme existed, as 1.2 did.
//
// 1.5 (tuned Classic CT5b): the optional `DetailRailProps.Shown` (the list's shown / total count for the rail's "(filtered)" line) and
// `DetailFrameProps.Sheet` on the two-column arm too (the insights sheet over the list on every facts page). The list bar's command kit
// (`ITrackCommandKit`, internal) takes `Bar(left, view, search, tail)` and lost `Separator`, `Trail`, `SortCaret`, `DensityLabel` and
// `DensityPanel` (Row size, Select and the sort caret left the bar); `TrackRowSkin.PadX` went with the Classic row divider's inset (the
// header rule, the plates and the dividers share one left edge, V10). No external theme exists yet.
//
// 1.6 (tuned Classic CT6b): the optional `CoverVeilProps.Hold` (where a horizontal veil's hold ends: the artist hero's copy edge).
// 1.7 (tuned Classic CT9): the optional `ThemePolicies.Artwork` (`ArtworkColorPolicy`: whether the page accent and the now-playing colours
// follow the artwork) and the optional `WashRoles.Rich` (`WashStrength`: a stronger wash level's hero, shelf, tone-plane and tint pairs). The
// wash reads take the level (`R.Wash.HeroAlphaAt(rich)` and its siblings, `ShellTintFromScheme(scheme, rich)`); the level-less reads
// `HeroAlpha`, `ShelfAlpha`, `PageTonePlaneAlpha`, `ShellTintAlpha` and `ShellTintFromScheme(scheme)` are removed before any external theme existed.
// 1.8 (tuned Classic CT8): the `Overlay.OfflineVacancy` key and its `OfflineVacancyProps` (the offline voice of a page with nothing
// saved: a page-scale error vacancy speaks it while the session is offline). The offline lane (`ContentStateKind.OfflineLane`) now
// stands in the flow above the page instead of floating over it.
//
// RULES (themes plan 1.2, 5.3). A MINOR bump only ADDS: a new key, a new optional member of a props record, a new role.
// A theme built against 1.N keeps working on any core that is 1.M with M >= N. A MAJOR bump may remove or change a
// member and is a deliberate, owner-visible event. `ThemeCatalog` refuses to install a composition whose major differs
// from `Major` or whose minor is newer than `Minor` (an older core cannot honour a newer theme's keys).

namespace Wavee.Theming.Contracts;

/// <summary>The contract version this core implements.</summary>
public static class ContractVersion
{
    /// <summary>Breaking changes to the published keys, props records or verbs.</summary>
    public const int Major = 1;

    /// <summary>Additive changes to the published keys, props records or verbs.</summary>
    public const int Minor = 8;

    /// <summary>True when a theme built against <paramref name="major"/>.<paramref name="minor"/> can run on this core.</summary>
    public static bool IsCompatible(int major, int minor) => major == Major && minor >= 0 && minor <= Minor;

    /// <summary>The version as text (<c>1.0</c>), for logs and the feedback line.</summary>
    public static string Text => Major + "." + Minor;
}
