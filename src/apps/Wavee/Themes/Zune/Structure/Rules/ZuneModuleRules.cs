// ── Themes/Zune/Structure/Rules/ZuneModuleRules.cs — the module page's layout on Zune's page column (themes plan 6.5, 6.3; P5 ZT7) ───────────────
//
// The module page is a Common block (`Themes\Common\CommonModulePage.cs`: the picture of `Pages.Module`, drawn from the module's declarative document with the style's
// roles, so its type is Zune's scale at every tier already). No board draws it (owner Q18); Zune places it on its page column: the frame's gutter (16 / 24 / 48 / 48 / 64
// by tier), the column capped at 2400 and CENTRED so its content starts at the band's x, the Compact hit target 40, and no masthead reserve above it (the band is outside the
// page; Classic's masthead reserve is 84).
//
// Public, so `Wavee.Tests` pins the record per tier. Literal numbers are allowed in `Rules\**` (WVTH003).

using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's module page layout per CONTENT tier.</summary>
public static class ZuneModuleTiers
{
    /// <summary>The masthead reserve above the page: none (the band sits outside the page).</summary>
    public const float TopReserve = 0f;

    /// <summary>The record of content tier <paramref name="content"/> (Huge 4K reads the same 2400 column; its gutter is the frame's 64).</summary>
    public static CommonModuleLayout For(WidthTier content)
        => new(ZuneTiers.Frame(content).Gutter, ZuneFrameRules.PageCap, CommonTiers.Target(content)) { Centered = true };

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share
    /// one left edge, so a docked panel that narrows the content never moves it).</summary>
    public static CommonModuleLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static CommonModuleLayout OfContent(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
