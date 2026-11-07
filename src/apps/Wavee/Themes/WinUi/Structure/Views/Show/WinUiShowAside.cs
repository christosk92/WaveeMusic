// ── Themes/WinUi/Structure/Views/Show/WinUiShowAside.cs — WinUI's show aside (W-Show, W-Show-Huge, W-Show-Compact; winui show pages) ───────────────────
//
// W-Show draws the show's About BESIDE the episodes: "About" (Subtitle 20/28), the description (Body 14/20, secondary) and the topic chips; W-Show-Huge adds
// "Show details", a card of label / value rows (Publisher, Episodes, Since, Format, Ratings). Below Standard the frame has no column for it: W-Show-Compact
// stands About under the episodes, at the list's end (the reader's foot), so the aside is drawn there instead, and only there.
//
// WHAT THE CORE KEEPS (`Show.AsideSlot`): the description's resolution, the topics slot, the facts. The tier decides only WHERE (the frame's column, the
// foot) and whether the details show (Huge).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.WinUi.WinUiShowRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiShow
{
    /// <summary>The aside: About, the description, the topics; the details at Huge.</summary>
    static Element Aside(ShowAsideArgs a) => Embed.Comp(new AsideProps(a), static () => new AsideView());

    sealed record AsideProps(ShowAsideArgs Args);

    sealed class AsideView : Component
    {
        public override Element Render()
        {
            var a = UseProps<AsideProps>().Args;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            bool huge = WinUiDetailTiers.Of(in facts).Tier >= WidthTier.Huge;
            var sections = new List<Element>(2);
            var about = new List<Element>(3) { AsideHead(Loc.Get(Strings.Theme.Winui.Detail.About)) };
            if (a.About is { Length: > 0 } text)
                about.Add(Controls.ExpandableRichTextFlex(text, AboutSize, Tok.TextSecondary, Tok.AccentTextPrimary, AsideAboutLines, "show:aside:about", a.Open));
            if (a.Topics is { } topics) about.Add(topics);
            if (about.Count > 1)
                sections.Add(new BoxEl { Key = "aside:about", Direction = 1, Gap = AsideHeadGap, MinWidth = 0f, Children = about.ToArray() });
            if (huge && a.Details.Length > 0) sections.Add(Details(a.Details));
            return new BoxEl { Key = "show:aside-body", Direction = 1, Gap = AsideSectionGap, MinWidth = 0f, Children = sections.ToArray() };
        }
    }

    static TextEl AsideHead(string title) => new(title)
    {
        FontFamily = DisplayFace, Size = PivotSize, LineHeight = NothingLineHeight, Weight = VoiceWeight, Color = Tok.TextPrimary,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>"Show details" (W-Show-Huge): a card of 32-tall rows, the label secondary at the start, the value primary at the end, a hairline between.</summary>
    static Element Details(ShowDetail[] details)
    {
        var rows = new Element[details.Length];
        for (int i = 0; i < details.Length; i++)
        {
            var d = details[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Height = DetailRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Padding = new Edges4(DetailPadX, 0f, DetailPadX, 0f),
                Children =
                [
                    Body(d.Label) with { Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                    Body(d.Value) with { Color = Tok.TextPrimary, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                ],
            };
        }
        return new BoxEl
        {
            Key = "aside:details", Direction = 1, Gap = AsideHeadGap, MinWidth = 0f,
            Children =
            [
                AsideHead(Loc.Get(Strings.Theme.Winui.Detail.ShowDetails)),
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Corners = Radii.CardAll, ClipToBounds = true,
                    Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows,
                },
            ],
        };
    }

    /// <summary>The aside under the episodes where the frame has no column for it (Compact, Small: W-Show-Compact's About after the list).</summary>
    static Element FootAside(Element aside) => Embed.Comp(new FootAsideProps(aside), static () => new FootAsideView());

    sealed record FootAsideProps(Element Aside);

    sealed class FootAsideView : Component
    {
        public override Element Render()
        {
            var p = UseProps<FootAsideProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            bool docked = WinUiDetailTiers.Of(in facts).Tier >= WidthTier.Standard;
            return new BoxEl
            {
                Key = "show:aside-foot", Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, docked ? 0f : AsideSectionGap, 0f, 0f),
                Children = docked ? [] : [p.Aside],
            };
        }
    }
}
