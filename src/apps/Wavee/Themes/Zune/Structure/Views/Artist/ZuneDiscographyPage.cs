// ── Themes/Zune/Structure/Views/Artist/ZuneDiscographyPage.cs — Zune's `Pages.Discography` (themes plan 6.3; P5 Z6a2; no board: built from Zune's parts, Q18) ──
//
// `disco:<kind>:<artist>`: the artist as the one ancestor (a name link, kept as written), the facet's title as a 40/48 light page title (lowercased by
// the heading case role), the release-type words (albums · singles & eps · compilations) as a sub-pivot row (the 14/20 voice, the current one in
// 600 under a 2-dip accent line, 20 apart, as the band's row 2 words), then the SAME grid and album drawer the artist page's facets use, at the
// page's reveal inset. Everything stands in the system page column (`ZuneFrameRules.PageColumn`: the frame's gutter, the 2400 cap, centred: the
// band's x, audit A1) and scrolls as one column; the player dock's reserve ends it.
//
// Deliberately NO cover palette (the system accent) and NO shell tint (ch 08 W22). The route parse, the demand, the scroll plumbing and the
// navigation the crumbs and the words cause stay core (`Artist.DiscoPageHost`). The words are a fresh selection per render: the ROUTE is the truth.
//
//   Pages.Discography
//   └─ ScrollView  ScrollKey = the tab's scope + the route key, Handle = the host's (the host provides LazyScroll above the slot)
//      └─ Column  `zune-discography-page`  (the gutter, the dock's reserve)
//         ├─ head: the artist crumb · the page title · the release-type words
//         └─ the core's grid at the reveal inset

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's standalone discography page.</summary>
internal static class ZuneDiscographyPage
{
    static readonly string[] s_facetLabelKeys = [Strings.Artist.Albums, Strings.Artist.SinglesEps, Strings.Artist.Compilations];

    /// <summary>The registered template of <see cref="ViewKeys.Discography"/>.</summary>
    public static Element Template(DiscographyPageProps p, in ViewContext ctx)
    {
        var lay = ZuneArtistTiers.Of(ctx.Tier());
        if (!p.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

        Element head = new BoxEl
        {
            Key = Rules.DiscoHeadKey, Direction = 1, Gap = Rules.DiscoHeadGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Rules.DiscoTrailGap, MinWidth = 0f,
                    Children = [Crumb(p), R.Type.PageHero(p.Title) with
                    {
                        Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    }],
                },
                Tabs(p, lay),
            ],
        };

        var content = new BoxEl
        {
            Key = Rules.DiscoPageKey, Direction = 1, Gap = lay.ZoneGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(0f, lay.BodyTop, 0f, R.Space.DockReserve + Rules.PageBottom),
            Children = [head, p.Grid is { } grid ? grid(Rules.GridRevealInset) : new BoxEl()],
        };
        return ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, Children = [ZuneDetailKit.CentredPage(content, lay.Gutter)] }) with
        {
            Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey, Handle = p.Scroll,
        };
    }

    /// <summary>The artist as the one ancestor: its name as written (never re-cased) in the secondary ink, the primary on hover.</summary>
    static Element Crumb(DiscographyPageProps p) => new BoxEl
    {
        Key = "zune-discography-crumb", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => p.OnCrumb(0),
        Children =
        [
            R.Type.DenseMeta(p.Name) with
            {
                Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The release-type words: a sub-pivot row, the current one heavier under the accent line. A touch target tall at Compact.</summary>
    static Element Tabs(DiscographyPageProps p, ZuneArtistLayout lay)
    {
        var words = new Element[s_facetLabelKeys.Length];
        for (int i = 0; i < words.Length; i++)
        {
            int index = i;
            bool current = (int)p.Facet == i;
            words[i] = new BoxEl
            {
                Key = "zune-discography-tab:" + i.ToString(CultureInfo.InvariantCulture), Direction = 1, Shrink = 0f, MinHeight = lay.Target,
                Justify = FlexJustify.Center, MinWidth = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => p.OnFacet(index),
                Children =
                [
                    R.Type.SubPivot(Loc.Get(s_facetLabelKeys[i])) with
                    {
                        Color = current ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary,
                        Weight = current ? Rules.TabSelectedWeight : Rules.TabRestWeight,
                        Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                    new BoxEl
                    {
                        Height = Rules.TabLine, Fill = current ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent, HitTestVisible = false,
                    },
                ],
            };
        }
        return new BoxEl { Direction = 0, Gap = Rules.DiscoTabGap, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = words };
    }
}
