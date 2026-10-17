// ── Themes/Zune/Structure/Rules/ZunePolicies.Artist.cs — what Zune decides for the core's artist hosts (themes plan 3.2, 6.3; P5 Z6a2) ──
//
// The artist hook of `ZunePolicies`: the hero's metrics (its height and gutter per tier, no veil, no copy cap), the chart's columns and
// rows per tier. The records are `ZuneArtistTiers` (`Rules\ZuneArtistRules.cs`); the people pages (the profile hero, the list's narrowest
// card) keep the core's values until ZT6.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void ArtistPolicy(ZuneParameters parameters, ref ArtistTierPolicy artistTiers) => artistTiers = ZuneArtistTiers.Policy;
}
