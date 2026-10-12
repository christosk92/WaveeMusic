// ── Themes/Classic/Structure/Views/Masthead/ClassicPageHead.cs — Classic's `Page.Head` (themes plan 6.1, T4c) ───────────────
//
// From `Shell.Masthead.UI.cs`: the trail-as-title row. Every crumb but the last is dimmed and clickable, a chevron follows each,
// then the CURRENT title under a stable key (Browse-home -> category does not remount it). No parent means no prefix child at all
// (a zero-width first sibling still padded the row a rung right). Classic places it inside the masthead band; a theme that draws its
// head in the page mounts the same key from the page.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's page head.</summary>
internal static class ClassicPageHead
{
    /// <summary>The registered template of <see cref="ViewKeys.PageHead"/>.</summary>
    public static Element Template(PageHeadProps props, in ViewContext ctx) => TitleRow(props.Chrome.Crumbs, props.Chrome.Title);

    static Element TitleRow(IReadOnlyList<PageCrumb> trail, string title)
    {
        int last = trail.Count - 1;
        var segs = new List<Element>(Math.Max(0, last) * 2);
        for (int i = 0; i < last; i++)
        {
            var crumb = trail[i];
            segs.Add(new BoxEl
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = crumb.Go,
                Children =
                [
                    R.Type.SurfaceDisplay(crumb.Label) with
                    {
                        Color = Tok.TextTertiary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                    },
                ],
            });
            segs.Add(R.Type.SurfaceDisplay("›") with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        var current = R.Type.SurfaceDisplay(title) with
        {
            Key = "masthead-current", MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = 1f, Basis = 0f, Shrink = 1f,
        };
        if (segs.Count == 0)
            return new BoxEl { Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = [current] };
        var prefix = new BoxEl
        {
            Key = "masthead-prefix:" + trail[0].Label,
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, Shrink = 0f,
            Children = segs.ToArray(),
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = Spacing.S,
            Children = [prefix, current],
        };
    }
}
