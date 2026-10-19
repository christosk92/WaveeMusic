// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Artist.cs — what WinUI decides for the artist and people pages (themes plan 6.2; P6 W6a) ──────────────
//
// The artist family's hook of `WinUiPolicies`: the card hero's metrics, the chart's columns and rows, the pivot's clearance and the split Details tabs
// (`WinUiArtistTiers.Policy`). The people pages' own answers (the profile hero, the list's card) are the core's defaults until W7.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void ArtistPolicy(WinUiParameters parameters, ref ArtistTierPolicy artistTiers) => artistTiers = WinUiArtistTiers.Policy;
}
