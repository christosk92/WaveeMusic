// ── Themes/Zune/Structure/Views/Library/ZuneLibraryArtistBoard.cs — the artists library as the boards draw it (Z-Library-Artists*) ────────────────
//
// Boards Z-Library-Artists (+ Small, Huge): right of the 220 list the reader column is the artist's HERO (a tinted plate, "artist · following · ..." over the name in the display
// voice and the verbs as words: play all, shuffle, go to artist), the "albums · in your library" heading over the releases as a 3-up grid of covers (the picked one ringed, its
// name in the accent), and, beside it, the PICKED album's songs (the host's own album pane). Small has no songs pane.
//
// It draws from the reader host's seam (`IArtistReaderHost`: the band, the releases, the verbs, the album pane); the block list, the spine and the sub-rail stay the Compact
// drill-in's (the old reader arm), where the board draws a list only.
//
// THE PANE'S RUNG. The page's layout ladder says whether the third column stands beside the middle one (`ZuneLibraryKit.PaneBeside`, the page's `PaneBeside`): under the
// rung the pane steps aside as Small's board draws it (the middle column eases to its width, the pane cross-fades out) and a cover opens its album's page instead of
// filling a pane nobody sees; at and over it the middle column never goes under `BoardMidMin`, so the hero's verbs stand as words and the covers stay board-sized.
//
// THE HERO. Its plate is the record's height for every artist (a selection never moves the grid). The name is a detail hero (owner exception 2026-10-21): the record's
// step on one line, else smaller on one line down to half the step, else two lines at that floor (the one-line block), then an ellipsis. The verbs are play all, shuffle,
// go to artist and the follow pill: a lane too narrow for their words keeps play all's word and names shuffle and go-to by their tooltips (the rings keep their nodes).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryArtistReader
{
    sealed record BoardProps(LibraryArtistReaderProps Reader, ZuneLibraryLayout Layout);

    /// <summary>The board's arm: one component per reader (its picked release is its own state; a new artist's first release is the default).</summary>
    static Element Board(LibraryArtistReaderProps props, ZuneLibraryLayout l)
        => new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true, AlignItems = FlexAlign.Stretch,
            Children = [Embed.Comp(new BoardProps(props, l), static () => new BoardView()) with { Key = "reader:board" }],
        };

    sealed class BoardView : Component
    {
        readonly Signal<int> _picked = new(-1);
        bool _drawn;

        public override Element Render()
        {
            var bp = UseProps<BoardProps>();
            bool beside = UseContext(ZuneLibraryKit.PaneBeside);
            var host = bp.Reader.Host;
            var l = bp.Layout;
            var band = host.ReadBand();
            var releases = host.ReadReleases();
            int picked = _picked.Value;
            bool has = false;
            for (int i = 0; i < releases.Count; i++) if (releases[i].AlbumSlot == picked) has = true;
            if (!has) picked = releases.Count > 0 ? releases[0].AlbumSlot : -1;

            // The flexible middle column: the pane's slot beside it carries the width motion (ZuneLibraryKit.PaneSlot), so it widens or narrows with it on every tick.
            var mid = new BoxEl
            {
                Key = "board:mid", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, SuppressScrollBar = true, ScrollKey = "lib:artist:board",
                        Content = new BoxEl
                        {
                            Direction = 1, Gap = LR.BoardGap, MinWidth = 0f,
                            Children =
                            [
                                Hero(host, in band, l), AlbumsHeading(),
                                // The boards' three covers a row. Standard and Huge hold them outright (the pane rung keeps the middle column wide enough once a crossing
                                // settles, so the count never flips while the column glides); Small measures its lane and stands two where three would go under the
                                // cover's floor.
                                Responsive.Of("board:grid", w => l.BoardFull ? LR.BoardCols : LR.BoardColumns(w), cols => Grid(releases, picked, _picked, beside, cols),
                                    fallback: LR.BoardCols * LR.BoardCellMin),
                            ],
                        },
                    },
                ],
            };
            // The picked album's pane in its slot: the pane's width and the gap while it stands beside (reserved before the first release lands, so the middle column
            // never jumps when it does), 0 while it steps aside; the album cross-fades in and out of it, and a pick cross-fades one album into the next.
            Element pane = K.PaneSlot("board:pane-slot", l.PaneW, l.ColGap, beside, _drawn, "board:pane:" + picked,
                beside && picked > 0 ? host.AlbumPane(picked) : null);
            _drawn = true;
            return new BoxEl { Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Children = [mid, pane] };
        }
    }

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Hero(IArtistReaderHost host, in ArtistReaderBand band, ZuneLibraryLayout l)
    {
        var a = band.Artist;
        bool named = band.Named;
        string uri = a.IsValid ? a.Uri.Text : "";
        string? name = named ? a.Name : null;
        string url = named ? Controls.ArtUrl(a.ImageId) ?? uri : uri;
        ColorF ink = Design.PlaceholderFor(url);
        var plate = Tok.FillSolidBase;
        ColorF tint = LR.HeroTint(ink, plate, R.IsLight);
        Element blob = new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, HitTestVisible = false,
            Width = LR.HeroBlob, Height = LR.HeroBlob, Margin = new Edges4(0f, -LR.HeroBlobBleed, -LR.HeroBlobBleed, 0f),
            Corners = Radii.Circle(LR.HeroBlob), Fill = tint, Opacity = LR.HeroBlobOpacity,
        };
        return new BoxEl
        {
            // The plate's height is the tier's (180 / 196 / 380): it eases on a crossing and the albums under it ride it.
            Key = "board:hero", ZStack = true, Height = l.HeroH, Shrink = 0f, MinWidth = 0f, ClipToBounds = true, Animate = ZuneMotion.HeightResize,
            Children =
            [
                new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Gradient = GradientDown(new GradientStop(0f, tint), new GradientStop(1f, plate)) },
                blob,
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.SpaceBetween, MinWidth = 0f, Padding = new Edges4(LR.HeroPadX, LR.HeroPadTop, LR.HeroPadX, LR.HeroPadBottom),
                    Children =
                    [
                        R.Type.DenseMeta(named ? HeroLine(band.Shape) : "") with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Case = R.Case.For(LabelKind.Label) },
                        new BoxEl
                        {
                            Direction = 1, Gap = LR.HeroNameGap, MinWidth = 0f,
                            Children =
                            [
                                // The detail hero's fit (owner exception 2026-10-21): the step on one line, else smaller on one line down to the floor, else two
                                // lines at the floor, whose block is the one full-size line's (the line scales with the size), then an ellipsis.
                                new TextEl(named ? a.Name : "…")
                                {
                                    Size = l.HeroNameSize, LineHeight = l.HeroNameSize * LR.HeroNameLine, Weight = LR.HeroNameWeight, FontFamily = R.Type.DisplayFace,
                                    Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                                    MinSize = l.HeroNameSize * LR.HeroNameFloorShare, FitMode = TextFitMode.OneLineFirst,
                                },
                                // The verbs as words (Standard and Huge hold them outright, as the grid holds its count); Small measures its lane: the words where they
                                // fit, else play all's word and the other rings named by their tooltips.
                                Responsive.Of("board:verbs", w => l.BoardFull || LR.HeroVerbWords(w), words => HeroVerbs(host, uri, name, words), fallback: LR.HeroVerbsWordsW),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>"artist · 3 albums · 35 songs in your library": the kind, then the host's counts.</summary>
    static string HeroLine(Artist.ReaderShapeKey shape)
    {
        string library = LibraryLine(shape);
        string kind = Loc.Get(Strings.Theme.Zune.Collection.Library.ArtistKind);
        return library.Length > 0 ? kind + " · " + library : kind;
    }

    /// <summary>Play all · shuffle · go to artist · the follow pill. <paramref name="words"/> false (a lane under <see cref="LR.HeroVerbsWordsW"/>): play all keeps its word and
    /// shuffle and go-to are rings named by their tooltips. Their wrapper is the same either way (<c>stableName</c>), so the lane crossing its width keeps every node.</summary>
    static Element HeroVerbs(IArtistReaderHost host, string uri, string? name, bool words) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Artist.ReaderShape.ActionGap, MinWidth = 0f, Shrink = 0f, Height = Artist.ReaderShape.ActionH,
        Children =
        [
            ZuneDiscoveryKit.Ring("reader-play-all", Icons.Play, Loc.Get(Strings.Library.PlayAll), showLabel: true, primary: true, host.PlayAll),
            ZuneDiscoveryKit.Ring("reader-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: words, primary: false, host.ShuffleAll, stableName: true),
            ZuneDiscoveryKit.Ring("reader-go-artist", Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), showLabel: words, primary: false, host.GoArtist, stableName: true),
            Follow(uri, name),
        ],
    };

    // ── the albums ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element AlbumsHeading() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.End, Gap = LR.GroupHeadingGap, MinWidth = 0f, Shrink = 0f,
        Children =
        [
            R.Type.QuickplayHead(Loc.Get(Strings.Theme.Zune.Collection.Library.ArtistAlbums)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label), Size = LR.BoardHeadSize, LineHeight = LR.BoardHeadLine,
            },
            R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Collection.Library.ArtistInLibrary)) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
            },
        ],
    };

    /// <summary>The releases, <paramref name="columns"/> covers a row (the cards keep their keys when the count changes). A cover picks its album for the pane while the pane stands
    /// <paramref name="beside"/>; with the pane aside it opens the album's page.</summary>
    static Element Grid(IReadOnlyList<ArtistReaderRelease> releases, int picked, Signal<int> pick, bool beside, int columns)
    {
        var cards = new Element[releases.Count];
        for (int i = 0; i < cards.Length; i++) cards[i] = Card(releases[i].AlbumSlot, releases[i].AlbumSlot == picked, pick, beside);
        var cols = new TrackSize[columns];
        for (int i = 0; i < cols.Length; i++) cols[i] = TrackSize.Star(1f);
        return new GridEl { Columns = cols, ColGap = LR.BoardGridGap, RowGap = LR.BoardGridGap, Shrink = 0f, Children = cards };
    }

    /// <summary>The cover's title on one line (the boards'); a name the cover's width trims names itself in a tooltip.</summary>
    static Element CardTitle(string title, bool selected)
    {
        TextEl text = R.Type.DenseMeta(title) with
        {
            Color = selected ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        return Controls.TrimTip(new BoxEl { Direction = 1, MinWidth = 0f, Children = [text] }, title, text);
    }

    static Element Card(int slot, bool selected, Signal<int> pick, bool beside)
    {
        var album = new Album(slot);
        bool known = album.IsValid && album.Knows(AlbumFields.Title);
        string url = Controls.ArtUrl(album.ImageId) ?? "";
        string title = known ? album.Title : "…";
        string kind = known ? Detail.Text.KindLabel(album.Kind) : "";
        string line = known && album.Year > 0 ? kind + " · " + FormatCache.Int(album.Year) : kind;
        Element art = new BoxEl
        {
            ZStack = true,
            Children =
            [
                new ImageEl
                {
                    Source = url, Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = LR.CardDecodePx, Corners = R.Shape.All(ShapeRole.Cover),
                    Placeholder = Design.PlaceholderFor(url),
                },
                new BoxEl
                {
                    Key = "ring", HitTestVisible = false, Corners = R.Shape.All(ShapeRole.Cover), BorderWidth = LR.CardRing,
                    BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                },
            ],
        };
        return new BoxEl
        {
            Key = "board:card:" + slot, Direction = 1, Gap = LR.CardNameGap, MinWidth = 0f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = beside ? () => pick.Value = slot : () => Track.GoToAlbum(album),
            Children =
            [
                art,
                new BoxEl
                {
                    Direction = 1, Gap = 1f, MinWidth = 0f,
                    Children =
                    [
                        CardTitle(title, selected),
                        R.Type.MicroMeta(line) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
            ],
        };
    }
}
