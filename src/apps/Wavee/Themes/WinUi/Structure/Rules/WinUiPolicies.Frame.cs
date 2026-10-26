// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Frame.cs — the title row's budget per window tier (themes plan 6.2; P6 W2a) ───────────────
//
// WinUI's title bar is two rows (the 40 strip over the 48 toolbar), so most of the core's one-row allocator does not apply: the title bar draws
// from its own record (`WinUiTiers.TitleBar`). The allocator still decides three things a WinUI view reads, and this budget makes it agree with
// the board:
//   · the identity chip's NAME (the core host passes `ShowName` to the profile chip): only at Huge (Shell-Top: "Profile name next to the
//     avatar"), so the name's entry width is 0 at Huge and out of reach elsewhere;
//   · the ACTIONS in the row (bell, Friends, pin): always (WinUI never folds them into the profile menu; Small's ⋯ is the title bar's own);
//   · the Compact row keeps the strip and the avatar (no tab-count button, no account row in the drawer: W-Shell-Compact).
// The search widths and the tab ladder mirror the record (520 / 640; tabs 160 -> 110, 200, 240), so the allocator's focused width and the
// strip's pricing stay honest.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    /// <summary>A width the window never reaches: the allocator's gate for what WinUI does not draw at that tier.</summary>
    private const float Never = 1_000_000f;

    static partial void FramePolicy(WinUiParameters parameters, ref ChromePolicy chrome)
    {
        var standard = ChromeBudget.Default with
        {
            NameEnterW = Never, ActionsEnterW = 0f, ForwardEnterW = 0f,
            SearchMaxW = WinUiFrameRules.SearchW, TabMaxW = 200f,
        };
        chrome = new ChromePolicy(standard)
        {
            Compact = standard with { ProfileChipW = 40f, TabsAsCount = false, TrailingInDrawer = false, ForwardEnterW = Never, TabMaxW = 140f, TabMinW = 36f },
            Small = standard with { TabMaxW = 160f, TabMinW = 110f },
            Huge = standard with { NameEnterW = 0f, SearchMaxW = WinUiFrameRules.SearchHugeW, TabMaxW = 240f },
        };
    }
}
