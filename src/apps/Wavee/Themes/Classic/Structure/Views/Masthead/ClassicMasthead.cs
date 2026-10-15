// ── Themes/Classic/Structure/Views/Masthead/ClassicMasthead.cs — Classic's `Shell.Masthead` (themes plan 6.1, T4c) ───────────
//
// From `Shell.Masthead.UI.cs`: the band's picture. The ONE masthead ("Browse > Category") is mounted by the content host as an
// overlay on its page-swap boundary, never a page's own copy, so a page swap cannot double-expose it and crossing out of the
// family never changes the boundary's height. The core host (`Shell.MastheadBand`) keeps the state machine: which route's
// publication is live, the trail it HELD while an unknown family fades, and the "Show all" delegate resolved at click time. This
// template draws what it was handed:
//
//   live + held    the band: the title row (`Page.Head`, `ClassicPageHead.cs`) and the "Show all" tool in one row on the browse
//                  surfaces' own frame, keyed on the head it shows. A NEW head swaps with the page swap's own clock (tuning F7,
//                  `ClassicFrameBitsRules.MastheadSwap`): the old band leaves on the 90 ms exit leg with the leaving body, the new one
//                  waits out that leg and arrives on the 210 ms enter leg with the new body, so a new title never paints over the
//                  old body. A re-publish of the same head (the tool's state) updates it in place
//   otherwise      no band: a live one leaving finishes its exit leg as an orphan, the old trail still drawn

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
        // One stable host; the band is its only child while live, so a band mounts (enters) and unmounts (exits) with its page.
        if (!props.Live || props.Held is not { } chrome)
            return new BoxEl { MinWidth = 0f, Direction = 1, HitTestPassThrough = true };

        // "Show all" resolves the LATEST delegate at click time (the host's cached verb): a re-publish that changes only the
        // delegate never re-renders the band.
        Element tools = chrome.ToolsVisible
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll), props.RunTools, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !chrome.ToolsLoading)
            : new BoxEl();

        return new BoxEl
        {
            MinWidth = 0f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Key = BandKey(chrome),
                    Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
                    Padding = ClassicFrameBitsRules.MastheadPadding,
                    Animate = ClassicFrameBitsRules.MastheadSwap,
                    Children = [ctx.View(ViewKeys.PageHead, new PageHeadProps(chrome)), tools],
                },
            ],
        };
    }

    /// <summary>The band's identity: the head it shows (the trail and the title). A different head is a different band, so it swaps
    /// with the page; the same head re-published stays the same node.</summary>
    internal static string BandKey(PageChrome chrome)
    {
        var key = new System.Text.StringBuilder("shell.masthead:");
        foreach (var crumb in chrome.Crumbs) key.Append(crumb.Label).Append('›');
        return key.Append(chrome.Title).ToString();
    }
}
