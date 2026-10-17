// ── Themes/Zune/Structure/Rules/ZunePolicies.Artist.cs — what Zune decides for the core's artist hosts (themes plan 3.2, 6.3; P5 Z6a2) ──
//
// The artist hook of `ZunePolicies`: the hero's metrics (its height and gutter per tier, no veil, no copy cap), the chart's columns and
// rows per tier, the section clearance under the band (Zune's band sits outside the page's viewport), and the people list's narrowest card (the
// profile record's). The records are `ZuneArtistTiers` (`Rules\ZuneArtistRules.cs`) and `ZuneProfileTiers` (`Rules\ZuneProfileRules.cs`); the
// profile hero's metrics keep the core's values (Zune's profile hero is its own record and reads none of them).

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void ArtistPolicy(ZuneParameters parameters, ref ArtistTierPolicy artistTiers) => artistTiers = ZuneArtistTiers.Policy;
}
