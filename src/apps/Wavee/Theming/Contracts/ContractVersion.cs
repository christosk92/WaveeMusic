// ── Theming/Contracts/ContractVersion.cs — the version of the contract between core and a theme ───────────────────────
//
// A theme is built against ONE contract version (`ThemeManifest.ContractMajor/ContractMinor`). The contract is the set of
// published view keys and style keys with their props records, the verbs and state enums those props carry, and the
// role vocabulary (`Wavee.Theming.Roles.WaveeStyleRoles`). Version 1.0 is the key list of themes plan section 6.0.
//
// 1.1 (tuned Classic CT4): `LyricsState.Offline` and the optional `LyricsLinesStyle.Offline` picture; `PlayerBarSlots.VideoSplit`
// (the video split button's own level bit; it used to ride `Queue`).
//
// 1.2 (tuned Classic CT3a): `SidebarRowShape.EntityText` (the covers-off text row), the optional `NavPanePolicy.ShowCovers`, and
// the nav row host's `UnpinAction` (the row's Unpin button).
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
    public const int Minor = 2;

    /// <summary>True when a theme built against <paramref name="major"/>.<paramref name="minor"/> can run on this core.</summary>
    public static bool IsCompatible(int major, int minor) => major == Major && minor >= 0 && minor <= Minor;

    /// <summary>The version as text (<c>1.0</c>), for logs and the feedback line.</summary>
    public static string Text => Major + "." + Minor;
}
