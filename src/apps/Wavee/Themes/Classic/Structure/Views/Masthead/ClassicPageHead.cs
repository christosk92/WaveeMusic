// ── Themes/Classic/Structure/Views/Masthead/ClassicPageHead.cs — Classic's `Page.Head` (themes plan 6.1, T4c; breadcrumb B) ──────
//
// Breadcrumb B: ONE page title (40/52 display, `R.Type.SurfaceDisplay`) under a small trail of only its ANCESTORS (12/16/600 links in the
// secondary ink, a tertiary `›` between two, 6 apart, on the bottom of a 28-DIP slot, 4 above the title). The current page is never a crumb,
// and a trail two deep has no ancestors to show (the sidebar's selected parent already says where the page lives). What the head holds is the
// route's `PageHeadForm`, never the trail's length, so a head keeps its height on its route:
//
//   Title   the title alone (Browse)
//   Drill   the trail's slot (empty or not) over the title (a Browse category or section, a Home section)
//   Hero    the trail's slot alone: the page's own hero is its title, so the title is never drawn twice (the Concerts pages)
//
// The title stands under a stable key (`masthead-current`), so Browse-home -> category does not remount it. Classic places the head inside the
// masthead band; a page that draws its own head (the discography page) builds the same trail with `Trail`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Rules = Wavee.Themes.Classic.ClassicPageHeadRules;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's page head.</summary>
internal static class ClassicPageHead
{
    /// <summary>The registered template of <see cref="ViewKeys.PageHead"/>.</summary>
    public static Element Template(PageHeadProps props, in ViewContext ctx) => Head(props.Chrome);

    static Element Head(PageChrome chrome)
    {
        switch (chrome.Form)
        {
            case PageHeadForm.Hero:
                return Trail(Ancestors(chrome.Crumbs));
            case PageHeadForm.Drill:
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.TrailTitleGap,
                    Children = [Trail(Ancestors(chrome.Crumbs)), Title(chrome.Title)],
                };
            default:
                return new BoxEl { Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = [Title(chrome.Title)] };
        }
    }

    /// <summary>The ancestors a trail draws: every crumb but the current page, and none while the trail is shorter than
    /// <see cref="ClassicPageHeadRules.TrailMinDepth"/>.</summary>
    public static IReadOnlyList<PageCrumb> Ancestors(IReadOnlyList<PageCrumb> crumbs)
    {
        if (crumbs.Count < Rules.TrailMinDepth) return [];
        var ancestors = new PageCrumb[crumbs.Count - 1];
        for (int i = 0; i < ancestors.Length; i++) ancestors[i] = crumbs[i];
        return ancestors;
    }

    /// <summary>The page title: the display face at 40/52, regular weight, one line.</summary>
    static Element Title(string title) => R.Type.SurfaceDisplay(title) with
    {
        Key = "masthead-current", MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>The trail's 28-DIP slot holding <paramref name="ancestors"/> (each a link, a <c>›</c> between two) on the slot's bottom
    /// edge. An empty trail keeps its slot, so the head's height never depends on it. The last ancestor gives way first.</summary>
    public static Element Trail(IReadOnlyList<PageCrumb> ancestors)
    {
        var links = new Element[Math.Max(0, ancestors.Count * 2 - 1)];
        for (int i = 0; i < ancestors.Count; i++)
        {
            var crumb = ancestors[i];
            if (i > 0) links[i * 2 - 1] = Ui.Caption("›") with { Weight = Rules.TrailWeight, Color = Tok.TextTertiary, Shrink = 0f };
            links[i * 2] = new BoxEl
            {
                Key = "masthead-crumb:" + crumb.Label,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = crumb.Go,
                MinWidth = 0f, Shrink = i == ancestors.Count - 1 ? 1f : 0f,
                Children =
                [
                    Ui.Caption(crumb.Label) with
                    {
                        Weight = Rules.TrailWeight, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
        return new BoxEl
        {
            Key = "masthead-trail", Direction = 0, Height = Rules.TrailSlotHeight, Shrink = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.End, Gap = Rules.TrailLinkGap, Children = links,
        };
    }
}
