// ── Themes/Classic/Structure/Views/Masthead/ClassicMasthead.cs — Classic's `Shell.Masthead` (themes plan 6.1, T4c) ───────────
//
// From `Shell.Masthead.UI.cs`: the band's picture. The ONE masthead ("Browse > Category") is mounted by the content host as an
// overlay on its page-swap boundary, never a page's own copy, so a page swap cannot double-expose it and crossing out of the
// family never changes the boundary's height. The core host (`Shell.MastheadBand`) keeps the state machine: which route's
// publication is live, the trail it HELD while an unknown family fades (a trail never collapses to nothing mid-swap), and the
// "Show all" delegate resolved at click time. This template draws what it was handed:
//
//   held == null   nothing has ever been shown: an invisible, hit-test-free box
//   held           the page head (`Page.Head`, `ClassicPageHead.cs`: the title, the ancestors' trail, or both by the route's form) and
//                  the "Show all" tool on the title's baseline, in one row on the browse surfaces' own frame; live -> opaque and
//                  hit-testable with the Show motion, not live -> transparent and inert with the Hide motion

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's masthead band.</summary>
internal static class ClassicMasthead
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellMasthead"/>.</summary>
    public static Element Template(MastheadProps props, in ViewContext ctx)
    {
        if (props.Held is not { } chrome) return new BoxEl { MinWidth = 0f, Opacity = 0f, HitTestVisible = false };

        // "Show all" resolves the LATEST delegate at click time (the host's cached verb): a re-publish that changes only the
        // delegate never re-renders the band.
        Element tools = chrome.ToolsVisible
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll), props.RunTools, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !chrome.ToolsLoading)
            : new BoxEl();

        bool live = props.Live;
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
            Padding = ClassicFrameBitsRules.MastheadPadding,
            Opacity = live ? 1f : 0f,
            HitTestVisible = live,
            Transition = live ? ClassicFrameBitsRules.MastheadShow : ClassicFrameBitsRules.MastheadHide,
            Children = [ctx.View(ViewKeys.PageHead, new PageHeadProps(chrome)), tools],
        };
    }
}
