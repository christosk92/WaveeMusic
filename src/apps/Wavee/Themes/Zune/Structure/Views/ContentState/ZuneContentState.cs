// ── Themes/Zune/Structure/Views/ContentState/ZuneContentState.cs — Zune's `Shell.ContentState` and `Page.Head` (themes plan 6.3; P5 Z2a) ──
//
// THE CONTENT STATES. The core content host decides which state the page region is in (a page, an empty body, the not-found page, the offline
// lane) and builds the page; this template draws the state it was handed, keyed as the core keys it:
//
//   Page / EmptyBody   one body box keyed `props.Key` (`page:<route>` / `page-empty:<route>`) that fills the page region.
//   NotFound           the Common not-found block (`CommonNotFound`): the destination's own glyph, the not-found line and Go home.
//   OfflineLane        the strip in the flow above the kept page: the window tier's gutter either side (16 / 24 / 48 / 48 / 64), 16 from the
//                      top, so the page opens under it.
//
// THE PAGE HEAD (#205-era list, plan 6.3; the canvas wins and nothing in it disagrees). Zune's band carries a page's words (row 2) and the
// title bar carries its trail, so a page head is small:
//
//   · a page whose head is HOISTED collapses to nothing: a hero page (its hero is its title: `PageHeadForm.Hero`) and a page with row 2 (its
//     views are the band's second row, which never repeats the title) draw an empty, zero-size box;
//   · every other page draws ONE title (the page-title voice, 40/48/300) under a stable key, so Browse home -> a category does not remount
//     it; the ancestors are the title bar's trail, never a second line here. The title keeps the casing it was written in.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Zune;

/// <summary>Zune's content states.</summary>
internal static class ZuneContentState
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellContentState"/>.</summary>
    public static Element Template(ContentStateProps props, in ViewContext ctx)
    {
        float gutter = ZuneTiers.FrameOf(ctx.Tier()).Gutter;   // read unconditionally: the facts read is a hook
        return props.Kind switch
        {
            ContentStateKind.NotFound => CommonNotFound.Block(props.Key, props.Glyph, props.GoHome),
            ContentStateKind.OfflineLane => OfflineLane(props, gutter),
            _ => PageBox(props.Key, props.Page),
        };
    }

    private static BoxEl PageBox(string key, Element? page) => new()
    {
        Key = key, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = page is null ? [] : [page],
    };

    private static BoxEl OfflineLane(ContentStateProps props, float gutter) => new()
    {
        Key = "offline-lane",
        Direction = 1, AlignItems = FlexAlign.Stretch, MinWidth = 0f, Shrink = 0f,
        Padding = ZuneFrameRules.OfflineLanePadding(gutter),
        Children = [Slots.View(ViewKeys.OverlayOfflineStrip, props.Strip)],
    };
}

/// <summary>Zune's page head.</summary>
internal static class ZunePageHead
{
    /// <summary>The registered template of <see cref="ViewKeys.PageHead"/>.</summary>
    public static Element Template(PageHeadProps props, in ViewContext ctx) => Head(props.Chrome);

    /// <summary>True when the head is hoisted (the hero or the band's row 2 carries the page's words) and collapses to nothing.</summary>
    public static bool Hoisted(PageChrome chrome) => chrome.Form == PageHeadForm.Hero || chrome.Views.Count > 0;

    private static Element Head(PageChrome chrome)
        => Hoisted(chrome)
            ? new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false }
            : R.Type.PageHero(chrome.Title) with
            {
                Key = ZuneFrameRules.PageTitleKey, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
}
