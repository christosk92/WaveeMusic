// ── Themes/WinUi/Structure/Views/Detail/WinUiDetailFrame.cs — WinUI's detail frame (themes plan 6.2; P6 W6a) ────────────────────────────────────────────
//
// The scaffold of every album / playlist / Liked / show / episode page under WinUI: ONE composition at every width, the hero over the table (the core's
// "vertical arm", forced by the Hero page layout and an always-vertical mode ladder, `Tiers.Detail.cs`):
//
//   Detail.Frame
//   └─ ZStack  [tint, tone, arm "vertical"]
//      └─ Row  [the page column | the aside slot]
//         ├─ the page column  [notice · (a show's fixed header) · the content column: the table, which hosts the hero as its item 0]
//         └─ the aside slot   0 wide where no aside docks; at Huge the 440 (480 from 3200) column with its own scroller, on the page's inset (W-Album-Huge)
//
// What the host decided (core, `Detail.FrameHost`): the page mode and the Hero override, the accent, the tint and tone claim leaves, the notice, the table, the
// drop target, the insights toggle with its facts latch and the aside's presence under `DetailTierLayout.AsideInHero`. The view arranges what it is handed and mounts
// the show header, the sheet and the aside's body through their own keys. The page inset reaches the list through the tier record (`PageInsetX`: the table pads
// its list and trailing band so the scroller keeps the card's edge); the hero insets itself; the notice and the aside take the same inset here.
//
// IDENTICAL TREE across tiers: the aside slot is in the row at every tier (width 0 where none docks), so a crossing never re-parents the table (it would lose
// its scroll): the slot eases its width open and closed and the list yields to it.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiDetailFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailFrame"/>.</summary>
    public static Element Template(DetailFrameProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var tier = WinUiDetailTiers.Of(in facts);
        // The content column. Its key is the host's (`right:tracks` / `right:eps`).
        BoxEl right = new()
        {
            Key = p.ContentKey, Grow = 1f, Shrink = 1f, MinWidth = p.ContentMinWidth, MinHeight = 0f, Direction = 1,
            Children = [p.Content],
        };
        // A show's fixed header sits above its episodes (never the hero system), on the page's inset.
        Element content = new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true, DropTarget = p.Drop,
            Children = p.ShowHeader is { } header ? [ctx.View(ViewKeys.DetailShowHeader, header), right] : [right],
        };
        Element page = new BoxEl
        {
            Key = "detail:vertical", Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [p.Notice, content],
        };
        // A TRACK page ALWAYS composes the insights overlay, open or not and facts or not, so its tree never changes under the list (the facts arriving must
        // not remount the table and lose its scroll).
        Element arm = p.VerticalTracks && p.Sheet is { } sheet ? ctx.View(ViewKeys.DetailInsights, sheet with { Page = page }) : page;
        Element row = new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl { Key = "detail:column", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Children = [arm] },
                AsideSlot(p, tier, in facts),
            ],
        };
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent, new BoxEl
        {
            ZStack = true, Grow = 1f, OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
            Children = [p.Tint, p.Tone, Arm("vertical", row)],
        });
    }

    /// <summary>One arm of the frame under its own key, entering and leaving on opacity alone.</summary>
    static Element Arm(string arm, Element root) => new BoxEl
    {
        Key = "detail:arm:" + arm, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
        Enter = WinUiDetailRules.ArmFade, Exit = WinUiDetailRules.ArmFade, Transition = WinUiDetailRules.ArmFadeMotion,
        Children = [root],
    };

    // ══ THE ASIDE ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The aside's slot, the row's last child at every tier: width 0 where none docks; else the column (440, 480 from 3200) on the page's inset with its own
    /// scroller. An aside that holds only the insights follows their toggle (0 closed), the slot easing its width.</summary>
    static Element AsideSlot(DetailFrameProps p, WinUiDetailTier tier, in TierFacts facts)
    {
        if (p.Aside is not { } aside)
            return new BoxEl { Key = "detail:aside", Width = 0f, Shrink = 0f, MinHeight = 0f, Animate = WinUiDetailRules.DockReveal };
        float columnW = WinUiDetailTiers.AsideWidthOf(in facts);
        float slotW = columnW + tier.InsetX;
        var insights = p.Sheet is { Facts: not null } sheet ? sheet : null;
        var kids = new List<Element>(2);
        if (aside.Body is { } body) kids.Add(body);
        if (insights is { } i) kids.Add(WinUiInsights.AsideSection(i.Toggle, i.Facts!, columnW));
        Prop<float> width = slotW;
        if (aside.Body is null && insights is { } only)
        {
            var open = only.Toggle.Open;
            width = Prop.Of(() => open.Value ? slotW : 0f);
        }
        return new BoxEl
        {
            Key = "detail:aside", Direction = 0, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
            Width = width, Animate = WinUiDetailRules.DockReveal,
            Children =
            [
                Ui.ScrollView(new BoxEl
                {
                    Direction = 1, Gap = WinUiDetailRules.AsideSectionGap,
                    Padding = new Edges4(0f, tier.InsetTop + WinUiDetailRules.AsideTop, tier.InsetX, R.Space.DockReserve),
                    Children = kids.ToArray(),
                }) with { Key = "aside:scroll:" + aside.RouteKey, Width = slotW, Shrink = 0f, MinHeight = 0f },
            ],
        };
    }
}
