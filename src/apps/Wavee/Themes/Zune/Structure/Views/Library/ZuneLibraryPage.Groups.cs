// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.Groups.cs — the albums library's "all artists" column (Z-Library-Albums*) ─────────────
//
// Boards Z-Library-Albums (220), -Small (200) and -Huge (280): under the head, left of the albums, a column that groups the library by artist. "all artists" and its count lead,
// then one row per artist with a saved album: the first letter of each run in the 20 light numeral lane, the name, the count at the end; the picked artist on a faint plate with
// its name semibold. A pick narrows the albums beside it to that artist's (the host does the narrowing: `LibraryPageProps.Groups`) and the middle column opens with the artist's
// name (26 light) and how many of their albums the library holds.
//
// The rows are plain keyed children of a scroller (an artist list is a few hundred rows at most). The component reads the host's reactive set itself, so a name landing or a pick
// re-renders this column alone, never the page.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryPage
{
    /// <summary>The page shows the groups column: the host published groups (the split albums page), and no search or Your Episodes body replaces the columns.</summary>
    static bool HasGroups(LibraryPageProps p) => p.Groups is not null && !p.Collapsed && !p.FullSearch && !p.SavedEpisodesMode;

    /// <summary>The column itself: keyed (the arm's children match by key), its width the tier record's.</summary>
    static Element GroupColumn(LibraryPageProps p, ZuneLibraryLayout l)
        => new BoxEl
        {
            Key = LR.GroupsKey, Direction = 1, Width = l.GroupW, MinWidth = l.GroupW, Shrink = 0f, ClipToBounds = true, Animate = LR.ColumnResize,
            Children = [Embed.Comp(l, () => new GroupsView(p.Groups!)) with { Key = "lib:groups:list" }],
        };

    /// <summary>The middle column's heading: the picked artist (or "all albums") in the 26 light voice, with how many of their albums the library holds, in the meta voice.</summary>
    static Element GroupHeading(LibraryPageProps p)
        => Embed.Comp(p.Groups!, static () => new GroupHeadingView()) with { Key = "lib:groups:heading" };

    /// <summary>Huge's artist column (Z-Library-Albums-Huge, 460): the picked artist's plate (tinted by their picture; the kind and counts line, the name in the display voice over two lines,
    /// the verbs as words), then "in your library" with the albums and songs held as numerals.</summary>
    static Element GroupHeroColumn(LibraryPageProps p, ZuneLibraryLayout l)
        => new BoxEl
        {
            Key = "lib:groups:hero", Direction = 1, Width = l.HeroColW, MinWidth = l.HeroColW, Shrink = 0f, ClipToBounds = true, Animate = Design.Reveal.Resize,
            Children = [Embed.Comp(p.Groups!, static () => new GroupHeroView()) with { Key = "lib:groups:hero:view" }],
        };

    sealed class GroupHeroView : Component
    {
        public override Element Render()
        {
            var groups = UseProps<LibraryGroupsProps>();
            var set = groups.Read();
            bool all = set.Selected.Length == 0;
            ColorF ink = Design.PlaceholderFor(set.SelectedArt.Length > 0 ? set.SelectedArt : set.Selected);
            ColorF tint = LR.HeroTint(ink, Tok.FillSolidBase, R.IsLight);
            string name = all ? Loc.Get(Strings.Theme.Zune.Collection.Library.AllArtists) : set.SelectedName;
            string line = Loc.Get(Strings.Theme.Zune.Collection.Library.ArtistKind)
                          + (set.Known ? " · " + Strings.Theme.Zune.Collection.Library.CountAlbums(set.SelectedAlbums) + " " + Loc.Get(Strings.Theme.Zune.Collection.Library.InYourLibrary) : "");
            Element plate = new BoxEl
            {
                ZStack = true, Height = LR.HeroColPlateH, Shrink = 0f, ClipToBounds = true,
                Children =
                [
                    new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = GradientDown(new GradientStop(0f, tint), new GradientStop(1f, Tok.FillSolidBase)) },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, HitTestVisible = false, Width = LR.HeroBlob, Height = LR.HeroBlob,
                        Margin = new Edges4(0f, -LR.HeroBlobBleed, -LR.HeroBlobBleed, 0f), Corners = Radii.Circle(LR.HeroBlob), Fill = tint, Opacity = LR.HeroBlobOpacity,
                    },
                    new BoxEl
                    {
                        Direction = 1, Justify = FlexJustify.SpaceBetween, MinWidth = 0f, Padding = new Edges4(LR.HeroPadX, LR.HeroPadTop, LR.HeroPadX, LR.HeroPadBottom),
                        Children =
                        [
                            R.Type.DenseMeta(line) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Case = R.Case.For(LabelKind.Label) },
                            new BoxEl
                            {
                                Direction = 1, Gap = LR.HeroNameGap, MinWidth = 0f,
                                Children =
                                [
                                    new TextEl(name)
                                    {
                                        Size = LR.HeroColNameSize, LineHeight = LR.HeroColNameLine, Weight = LR.HeroNameWeight, FontFamily = R.Type.DisplayFace,
                                        Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                    },
                                    all ? new BoxEl() : Verbs(groups),
                                ],
                            },
                        ],
                    },
                ],
            };
            return new BoxEl
            {
                Direction = 1, Gap = LR.HeroColGap, MinWidth = 0f,
                Children = [plate, all ? new BoxEl() : Stats(set)],
            };
        }

        static Element Verbs(LibraryGroupsProps g) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Artist.ReaderShape.ActionGap, MinWidth = 0f, Shrink = 0f, Height = Artist.ReaderShape.ActionH,
            Children =
            [
                ZuneDiscoveryKit.Ring("lib-hero-play", Icons.Play, Loc.Get(Strings.Library.PlayAll), showLabel: true, primary: true, g.Play),
                ZuneDiscoveryKit.Ring("lib-hero-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: true, primary: false, g.Shuffle),
                ZuneDiscoveryKit.Ring("lib-hero-go", Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), showLabel: true, primary: false, g.Open),
            ],
        };

        static Element Stats(LibraryGroupSet set) => new BoxEl
        {
            Direction = 1, Gap = LR.HeroColStatGap, MinWidth = 0f, Shrink = 0f,
            Children =
            [
                R.Type.QuickplayHead(Loc.Get(Strings.Theme.Zune.Collection.Library.InYourLibrary)) with
                {
                    Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
                },
                new BoxEl
                {
                    Direction = 0, Gap = LR.HeroColStatGap, MinWidth = 0f,
                    Children =
                    [
                        Stat(set.SelectedAlbums, Loc.Get(Strings.Theme.Zune.Collection.Library.HeroAlbums)),
                        Stat(set.SelectedSongs, Loc.Get(Strings.Theme.Zune.Collection.Library.HeroSongs)),
                    ],
                },
            ],
        };

        static Element Stat(int n, string word) => new BoxEl
        {
            Direction = 1, Gap = LR.HeroColStatLabelGap, MinWidth = 0f, Grow = 1f, Basis = 0f,
            Children =
            [
                new TextEl(FormatCache.Int(n))
                {
                    Size = LR.HeroColStatSize, LineHeight = LR.HeroColStatLine, Weight = LR.HeroNameWeight, FontFamily = R.Type.DisplayFace, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                R.Type.DenseMeta(word) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label) },
            ],
        };
    }

    sealed class GroupHeadingView : Component
    {
        public override Element Render()
        {
            var groups = UseProps<LibraryGroupsProps>();
            var set = groups.Read();
            bool all = set.Selected.Length == 0;
            string title = all ? Loc.Get(Strings.Theme.Zune.Collection.Library.AllAlbums) : set.SelectedName;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.End, Gap = LR.GroupHeadingGap, MinWidth = 0f, Shrink = 0f, Height = LR.GroupHeadingH,
                Padding = new Edges4(0f, 0f, 0f, LR.GroupHeadingPadBottom),
                Children =
                [
                    R.Type.QuickplayHead(title) with
                    {
                        Size = LR.BoardHeadSize, LineHeight = LR.BoardHeadLine,
                        Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        Case = all ? R.Case.For(LabelKind.Label) : TextCase.AsWritten,
                    },
                    R.Type.DenseMeta(set.Known ? Strings.Theme.Zune.Collection.Library.AlbumsInLibrary(set.SelectedAlbums) : "") with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f, Case = R.Case.For(LabelKind.Label),
                    },
                ],
            };
        }
    }

    sealed class GroupsView(LibraryGroupsProps groups) : Component
    {
        public override Element Render()
        {
            var l = UseProps<ZuneLibraryLayout>();
            var set = groups.Read();
            var rows = new Element[set.Rows.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var r = set.Rows[i];
                string key = r.Key;
                rows[i] = Row(r, key == set.Selected, () => groups.Pick(key), l);
            }
            Element all = new BoxEl
            {
                Key = "lib:group:all", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Height = l.GroupHeadH, Shrink = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => groups.Pick(""),
                Children =
                [
                    R.Type.TrackTitle(Loc.Get(Strings.Theme.Zune.Collection.Library.AllArtists)) with
                    {
                        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 1f, MinWidth = 0f,
                    },
                    R.Type.MicroMeta(set.Known ? FormatCache.Int(set.Rows.Count) : "") with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
                ],
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
                Children =
                [
                    all,
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, SuppressScrollBar = true, ScrollKey = "lib:groups:scroll",
                        Content = new BoxEl { Direction = 1, Children = rows },
                    },
                ],
            };
        }

        static Element Row(LibraryGroupRow r, bool selected, Action pick, ZuneLibraryLayout l) => new BoxEl
        {
            Key = "lib:group:" + r.Key, Direction = 0, AlignItems = FlexAlign.Center, Height = LR.GroupRowH, Shrink = 0f, MinWidth = 0f,
            Fill = selected ? Tok.FillSubtleTertiary : ColorF.Transparent, HoverFill = R.Layer.ChromeHover, HoverDurationMs = 0f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = pick,
            Children =
            [
                new BoxEl
                {
                    Width = LR.GroupLetterLane, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(LR.GroupLetterPad, 0f, 0f, 0f),
                    Children = [r.Letter.Length > 0 ? ZuneProfileLetters.Glyph(r.Letter) : new BoxEl()],
                },
                R.Type.TrackTitle(r.Name) with
                {
                    Color = selected ? Tok.TextPrimary : Tok.TextSecondary, Weight = selected ? LR.GroupPickedWeight : LR.GroupRestWeight,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Basis = 0f, MinWidth = 0f,
                },
                new BoxEl
                {
                    Width = l.GroupCountLane, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 0f, LR.GroupCountPad, 0f),
                    Children = [R.Type.MicroMeta(FormatCache.Int(r.Albums)) with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
                },
            ],
        };
    }
}
