// ── Themes/Zune/Structure/Views/Browse/ZuneBrowseDirectory.cs — Zune's `Pages.Browse` and `Browse.Charts` (themes plan 6.3; P5 Z5) ──────────────
//
// Board Z-Browse (its own row 2 is superseded: the band's row 2 is Browse's bands, ZC1, route level, so it stands on the first frame and the
// host hands this view the groups already FILTERED to the band: "for you" and "genres" show their one group, "top" all of them). No masthead,
// no page title: the band is the head. The page: the frame's gutter, 8 under the band, the sections 28 apart (`ZuneBrowseRules`), each its
// 26/32 light heading (lowercase) 10 over its body:
//
//   top              the Top categories as flat 120 plates (undrawn: the board's row 2 held them)
//   charts | for you the chart categories (Classic swallows them into its Fold deck) as two 120 plates in a 420 column beside For you, 48 apart;
//                    For you a three-column list of 36 rows, the category's colour as a 10 square before its name
//   genres           a six-column list of 32 rows of names in the secondary ink
//   mood & activity  six-across flat 96 plates in the category's colour
//   more             a six-column list of 32 rows
//
// Every column count is a live prop of ONE keyed grid (a crossing re-fits the same grid), and the charts | for you row keeps its two nodes in
// every tier (only its direction is a live prop). The loading directory is the SAME body over the seed categories, skeletonized; a directory
// that answered with nothing is the "unavailable" vacancy; a failure is the error vacancy with the host's Retry.
// `Browse.Charts` (the chart deck's core host) is not mounted by Zune's directory: its chart categories are plates. Zune still registers a
// picture for the key (the deck as a numbered list), so any host that mounts it gets Zune's voice.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneBrowseRules;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Browse directory.</summary>
internal static class ZuneBrowseDirectory
{
    /// <summary>The registered template of <see cref="ViewKeys.BrowseDirectory"/>.</summary>
    public static Element Template(BrowseDirectoryProps props, in ViewContext ctx)
    {
        var lay = ZuneBrowseTiers.Of(ctx.Tier());
        var groups = props.Groups;
        Element region = new SkelRegionEl(
            Pending: () => props.Load == BrowseLoad.Pending, Failed: () => props.Load == BrowseLoad.Failed,
            Content: () => groups is null || groups.Count == 0
                ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "")
                : Body(groups, live: true, lay),
            ShimmerSource: () => Body(BrowseTaxonomy.Grouped(BrowseDirectorySeeds.Categories), live: false, lay).Skeletonized(true),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);

        Element column = new BoxEl
        {
            Key = B.DirectoryKey, Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = R.Space.PageMaxW,
            AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(lay.Gutter, H.PageTop, lay.Gutter, H.PageBottom),
            Children = [region],
        };
        return Ui.ScrollView(new BoxEl { Direction = 0, MinWidth = 0f, Children = [column] })
            with { Grow = 1f, MinWidth = 0f, ScrollKey = props.ScrollKey };
    }

    /// <summary>The bands in the board's order: top, charts | for you, genres, mood &amp; activity, more (each only when the directory has it).</summary>
    static Element Body(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups, bool live, ZuneBrowseLayout lay)
    {
        var kids = new List<Element>(5);
        int band = 1;   // the first band's own entrance delay
        if (ItemsOf(groups, BrowseGroup.Top) is { Count: > 0 } top) kids.Add(Band(BrowseGroup.Top, band++, Plates(top, live, lay.TopCols, lay.BigPlateH, big: true, "top")));

        var charts = ItemsOf(groups, BrowseGroup.Charts);
        var forYou = ItemsOf(groups, BrowseGroup.ForYou);
        if (charts is { Count: > 0 } || forYou is { Count: > 0 })
        {
            bool beside = lay.ChartsBeside && charts is { Count: > 0 } && forYou is { Count: > 0 };
            var pair = new List<Element>(2);
            if (charts is { Count: > 0 })
                pair.Add(Section(BrowseGroup.Charts, Plates(charts, live, cols: 2, lay.BigPlateH, big: true, "charts")) with
                {
                    Key = B.BandPrefix + "charts", Width = beside ? lay.ChartsW : float.NaN, Shrink = 0f,
                });
            if (forYou is { Count: > 0 })
                pair.Add(Section(BrowseGroup.ForYou, ForYouList(forYou, live, lay)) with
                {
                    Key = B.BandPrefix + "foryou", Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN,
                });
            kids.Add(Band("charts+foryou", band++, new BoxEl
            {
                Direction = (byte)(beside ? 0 : 1), Gap = beside ? B.PairGap : ZuneHomeTiers.For(lay.Tier).ZoneGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Layout = ZuneMotion.ColumnCollapse, Children = pair.ToArray(),
            }));
        }

        if (ItemsOf(groups, BrowseGroup.Genres) is { Count: > 0 } genres) kids.Add(Band(BrowseGroup.Genres, band++, Links(genres, live, lay, "genres")));
        if (ItemsOf(groups, BrowseGroup.MoodActivity) is { Count: > 0 } moods)
            kids.Add(Band(BrowseGroup.MoodActivity, band++, Plates(moods, live, lay.MoodCols, lay.MoodH, big: false, "mood")));
        if (ItemsOf(groups, BrowseGroup.More) is { Count: > 0 } more) kids.Add(Band(BrowseGroup.More, band++, Links(more, live, lay, "more")));
        return new BoxEl { Direction = 1, Gap = ZuneHomeTiers.For(lay.Tier).ZoneGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    static IReadOnlyList<BrowseCategory>? ItemsOf(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups, BrowseGroup g)
    {
        for (int i = 0; i < groups.Count; i++) if (groups[i].Group == g) return groups[i].Items;
        return null;
    }

    /// <summary>A band with its heading (the group's word, lowercase).</summary>
    static Element Band(BrowseGroup group, int index, Element body)
        => Band(GroupId(group), index, Section(group, body));

    /// <summary>A band's keyed box (the same node at every tier: a crossing reflows it), its entrance on the inner box.</summary>
    static Element Band(string id, int index, Element content) => new BoxEl
    {
        Key = B.BandPrefix + id + ":band", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Layout = ZuneMotion.ColumnCollapse,
        Children = [new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Animate = Design.Entrance.Row(index), Children = [content] }],
    };

    static BoxEl Section(BrowseGroup group, Element body) => K.Section(K.Heading(GroupLabel(group), null, null), body);

    static string GroupId(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => "top",
        BrowseGroup.ForYou => "foryou",
        BrowseGroup.Genres => "genres",
        BrowseGroup.MoodActivity => "mood",
        BrowseGroup.Charts => "charts",
        _ => "more",
    };

    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        BrowseGroup.Charts => Loc.Get(Strings.Browse.Charts),
        _ => Loc.Get(Strings.Browse.More),
    };

    // ── the cells ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Flat plates in the categories' colours, <paramref name="cols"/> across (ONE keyed grid: the count is a live prop).</summary>
    internal static Element Plates(IReadOnlyList<BrowseCategory> items, bool live, int cols, float height, bool big, string id)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = BrowseTiles.ModelOf(items[i], live);
            cells[i] = K.Plate("zune-plate:" + m.Uri, m.Title, m.Color, m.Artwork, height, m.Open, big);
        }
        return new GridEl
        {
            Key = "zune-browse-plates:" + id, MinColWidth = B.PlateMinCol, MaxColumns = cols, ColGap = B.PlateGap, RowGap = B.PlateGap, Children = cells,
        };
    }

    /// <summary>For you: a list of 36 rows, the category's colour as a 10 square before its name.</summary>
    static Element ForYouList(IReadOnlyList<BrowseCategory> items, bool live, ZuneBrowseLayout lay)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = BrowseTiles.ModelOf(items[i], live);
            bool colour = m.Color is { } c && c != 0u;
            cells[i] = new BoxEl
            {
                Key = "zune-foryou:" + m.Uri, Height = B.ForYouRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = B.DotGap, MinWidth = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = m.Open,
                Children =
                [
                    new BoxEl
                    {
                        Width = B.DotSize, Height = B.DotSize, Shrink = 0f,
                        Fill = colour ? ColorScience.ToColor(m.Color!.Value) : Prop.Of(static () => Tok.AccentDefault),
                    },
                    R.Type.TrackTitle(m.Title) with
                    {
                        Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    },
                ],
            };
        }
        return new GridEl
        {
            Key = "zune-browse-list:foryou", MinColWidth = B.ListMinCol, MaxColumns = lay.ForYouCols, ColGap = B.ListColGap, RowHeight = B.ForYouRowH,
            Children = cells,
        };
    }

    /// <summary>The genres and More: a list of 32 rows of names in the secondary ink, the tier's columns across.</summary>
    internal static Element Links(IReadOnlyList<BrowseCategory> items, bool live, ZuneBrowseLayout lay, string id)
    {
        var models = new BrowseTileModel[items.Count];
        for (int i = 0; i < models.Length; i++) models[i] = BrowseTiles.ModelOf(items[i], live);
        return LinkGrid(models, lay.LinkCols, id);
    }

    /// <summary>A list of link names (the genres, More, a search's genre links).</summary>
    internal static Element LinkGrid(IReadOnlyList<BrowseTileModel> models, int cols, string id)
    {
        var cells = new Element[models.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = models[i];
            cells[i] = new BoxEl
            {
                Key = "zune-link:" + m.Uri, Height = B.LinkRowH, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = m.Open,
                Children =
                [
                    R.Type.TrackTitle(m.Title) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    },
                ],
            };
        }
        return new GridEl
        {
            Key = "zune-browse-list:" + id, MinColWidth = B.ListMinCol, MaxColumns = cols, ColGap = B.ListColGap, RowHeight = B.LinkRowH, Children = cells,
        };
    }

    // ── Browse.Charts ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.BrowseCharts"/>: the chart deck as a numbered list (the Home charts' voice: 40 rows,
    /// the 22 light rank), the heading opening the Charts page; pending the seed deck shimmers, an empty deck is the compact empty, a failure the
    /// compact error with its own Retry.</summary>
    public static Element ChartsTemplate(BrowseChartsProps props, in ViewContext ctx) => new SkelRegionEl(
        Pending: () => props.State == HomeLoad.Pending, Failed: () => props.State == HomeLoad.Failed,
        Content: () => props.Deck.Count == 0
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Home.ChartsEmpty), subtitle: "")
            : Deck(props.Deck, props.OpenTile, props.OpenHeader),
        ShimmerSource: () => Deck(HomeBrowseCards.ChartDeckSeed, static _ => { }, static () => { }),
        OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: props.Retry),
        Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);

    static Element Deck(IReadOnlyList<HomeSectionView> deck, Action<HomeSectionView> open, Action openHeader)
    {
        var rows = new Element[deck.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var section = deck[i];
            rows[i] = new BoxEl
            {
                Key = "zune-browse-chart:" + (section.Uri ?? i.ToString(CultureInfo.InvariantCulture)),
                Height = H.ChartRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.ChartGap, MinWidth = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => open(section),
                Children =
                [
                    new BoxEl
                    {
                        Width = H.ChartRankW, Shrink = 0f,
                        Children = [R.Type.Numeral((i + 1).ToString(CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, MaxLines = 1 }],
                    },
                    R.Type.TrackTitle(section.Title ?? "") with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
        }
        return K.Section(K.Heading(Loc.Get(Strings.Browse.Charts), null, K.Link("zune-browse-charts-open", Loc.Get(Strings.Home.SeeAll), openHeader)),
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
    }
}
