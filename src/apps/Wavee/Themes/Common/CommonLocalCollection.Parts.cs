// ── Themes/Common/CommonLocalCollection.Parts.cs — the "On this PC" page's body parts (#240 P2, boards 18-21) ───────────────────────
//
// Shared by the WinUI/Classic page (`CommonLocalCollection`) and Zune's (`ZuneLocalCollection`): every word goes through the active
// style's type and case roles, so Zune's lowercase headings and its type come from its own style. Long lists (the album wall, the
// artists, a folder's tiles and songs) are ONE measured virtual list each: a row is a head, a row of cards, or a song; the list
// measures its width once (`UseMeasuredWidth`) to cut the card rows. Names (albums, artists, folders, files) are never re-cased.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using L = Wavee.Themes.Common.CommonLocalCollectionRules;
using K = Wavee.Themes.Common.CommonLocalCollectionRules;   // inside a Component, where `L` is the component's own method
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee.Themes.Common;

/// <summary>The body parts of the collection page (see the file header).</summary>
internal static partial class CommonLocalCollectionParts
{
    // ── the seed the shimmer draws (one real UI over a representative shape) ────────────────────────────────────────

    public static LocalCollectionView Seed { get; } = MakeSeed();

    static LocalCollectionView MakeSeed()
    {
        var cards = new LocalAlbumCard[8];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new LocalAlbumCard("seed" + i, "Album title", "Artist name", 2000, 10, 0, "", false, false, null, 0, LocalAddedWhen.ThisWeek, "");
        return new LocalCollectionView
        {
            Version = 0, Phase = LocalLibraryPhase.Ready, Songs = 1000, AlbumCount = 100, ArtistCount = 50, DurationMs = 3_600_000L * 60, LosslessPct = 50,
            Chips = [new LocalFolderChip("C:\\Music", "C:\\Music", LocalChipState.Ok, 1000)],
            Recent = [new LocalShelfGroup(LocalAddedWhen.ThisWeek, cards)],
            Lossless = cards,
            Albums = cards, AlbumsByArtist = cards, AlbumsByTitle = cards, AlbumsByYear = cards,
        };
    }

    // ── the page gutter: spacers that ease (a window-tier crossing changes the gutter; padding would snap) ─────────────

    /// <summary>A gutter spacer <paramref name="w"/> wide whose width eases through real layout, so what follows it rides it.</summary>
    public static BoxEl Air(string key, float w)
        => new BoxEl { Key = key, Width = w, MinWidth = 0f, Shrink = 0f, HitTestVisible = false, Layout = L.GutterEase }.Skeletonized(false);

    /// <summary><paramref name="body"/> between two easing gutters <paramref name="g"/> wide, <paramref name="top"/> over it and <paramref name="bottom"/>
    /// under it; <paramref name="grow"/> lets it take its column's height (a list that scrolls itself).</summary>
    public static BoxEl Gut(string key, float g, Element body, float top = 0f, float bottom = 0f, bool grow = false) => new()
    {
        Key = key + ":gut", Direction = 0, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Grow = grow ? 1f : 0f,
        Padding = new Edges4(0f, top, 0f, bottom),
        Children =
        [
            Air(key + ":l", g),
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Children = [body] },
            Air(key + ":r", g),
        ],
    };

    // ── "Your files and Spotify" (#240 phase 1) ─────────────────────────────────────────────────────────────────────

    /// <summary>The offer's slot on Home, under the pivot (board 28 ①): its own component (the offer is tracked), revealed as it comes and
    /// closing its height as it goes ("Not now", matching turned on), so the shelves below glide.</summary>
    public static Element LinkOffer(LocalLinkPageProps? link, float g)
        => link is null ? new BoxEl { Key = "ll-offer-slot" } : Embed.Comp(new OfferSlotProps(link, g), static () => new OfferSlot()) with { Key = "ll-offer-slot" };

    sealed record OfferSlotProps(LocalLinkPageProps Link, float Gutter);

    sealed class OfferSlot : Component
    {
        public override Element Render()
        {
            var sp = UseProps<OfferSlotProps>();
            var offer = sp.Link.Offer();
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = offer is null
                    ? []
                    :
                    [
                        new BoxEl
                        {
                            Key = "ll-offer-box", Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
                            Children = [Gut("ll-offer", sp.Gutter, FluentGpu.Theming.Slots.View(ViewKeys.LocalLinkOffer, offer), top: CommonLocalCollectionRules.LinkOfferTop)],
                        },
                    ],
            };
        }
    }

    /// <summary>The review in the page's place (board 29), on the page gutter, in its own scroll.</summary>
    public static Element LinkReview(LocalLinkPageProps link, float g, string routeKey)
        => ScrollView(Gut("ll-review", g, FluentGpu.Theming.Slots.View(ViewKeys.LocalLinkReview, link.Review)))
            with { Key = L.ScrollKey + ":link-review", Grow = 1f, MinWidth = 0f, ScrollKey = routeKey + ":link-review" };

    // ── words ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static TextEl Ell(TextEl t) => t with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };

    static TextEl Heading(string s) => R.Type.Heading(s) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary };

    static TextEl Meta(string s) => R.Type.TrackMeta(s) with { Color = Tok.TextSecondary };

    /// <summary>The collection's meta line, as the library words a record's ("2 songs · 1 artist · 4 minutes · 57% lossless"): a count that is 0 is
    /// left out (never "0 albums · 1 song"), and so is a lossless share of 0.</summary>
    public static string StatsText(LocalCollectionView v, bool withSongs)
    {
        var parts = new List<string>(5);
        if (withSongs) parts.Add(P.Songs(v.Songs));
        if (v.AlbumCount > 0) parts.Add(P.Albums(v.AlbumCount));
        if (v.ArtistCount > 0) parts.Add(P.Artists(v.ArtistCount));
        if (v.DurationMs > 0) parts.Add(Length(v.DurationMs));
        if (v.Songs > 0 && v.LosslessPct > 0) parts.Add(P.Lossless(v.LosslessPct.ToString(CultureInfo.CurrentCulture)));
        return string.Join(" · ", parts);
    }

    /// <summary>"2 albums · 16 songs", "1 song": an artist's or a decade's counts, a 0 left out.</summary>
    public static string CountsText(int albums, int songs)
        => albums > 0 && songs > 0 ? P.Albums(albums) + " · " + P.Songs(songs) : albums > 0 ? P.Albums(albums) : P.Songs(songs);

    public static string Length(long ms)
    {
        long minutes = ms / 60_000;
        return minutes >= 60 ? P.Hours((int)(minutes / 60)) : P.Minutes((int)minutes);
    }

    static string Clock(int ms)
    {
        int s = Math.Max(0, ms / 1000);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>The library's state line: its mark (<paramref name="mark"/> of the state) and its words, both from
    /// <see cref="LibraryActivity.PageLine"/>, the facts the sidebar's chip reads, so the page and the chip flip on the same frame. Its own
    /// component: the scan's moves re-render this line alone, and the "ago" stays true (<see cref="LibraryActivity.Minute"/>).</summary>
    public static Element Activity(Func<LibraryActivity.LineKind, Element> mark, TextEl words) => Embed.Comp(new ActivityProps(mark, words), static () => new ActivityHost());

    sealed record ActivityProps(Func<LibraryActivity.LineKind, Element> Mark, TextEl Words);

    sealed class ActivityHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<ActivityProps>();
            LibraryActivity.Track();
            _ = LibraryActivity.Minute.Value;
            var (kind, text) = LibraryActivity.PageLine();
            return new BoxEl
            {
                Direction = 0, Gap = CommonLocalCollectionRules.ChipInner, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
                Children = [a.Mark(kind), Ell(a.Words with { Text = text })],
            };
        }
    }

    // ── the hero's backdrop: the cover mosaic, or a calm gradient ──────────────────────────────────────────────────

    /// <param name="leftFade">WinUI/Classic: the columns fade out towards the words (none at the left edge, full at the right); Zune: every
    /// column at full strength (board 21's full-bleed mosaic).</param>
    /// <param name="veil">Zune: its flat ground with the board's three veil strengths (top, middle, foot); the mosaic is then drawn at one strength
    /// under the veil, which does the fading. Null: each cell fades by itself (WinUI/Classic, whose ground is a translucent layer).</param>
    public static Element Backdrop(LocalCollectionView v, float tile, bool leftFade, (ColorF Ground, float Top, float Mid, float Strength)? veil = null)
    {
        if (v.Mosaic.Count == 0)
            return new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, Opacity = L.CalmOpacity, HitTestVisible = false,
                Gradient = GradientDown(new GradientStop(0f, Tok.AccentSubtle), new GradientStop(1f, Tok.AccentSubtle with { A = 0f })),
            };
        // The mosaic spans the hero (anchored right, clipped at the hero's edges) and every cell carries its own strength: by row it fades down
        // into the page ground, and on WinUI/Classic by column towards the words, so there is no hard edge and no veil of a guessed ground colour.
        var rows = L.MosaicRowFade;
        var cells = new Element[L.MosaicColumns * rows.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            int row = i / L.MosaicColumns, col = i % L.MosaicColumns;
            float across = leftFade ? col / (L.MosaicColumns - 1f) : 1f;
            float strength = veil is { } vl ? vl.Strength : L.MosaicOpacity * rows[row] * across * across;
            cells[i] = new BoxEl
            {
                Width = tile, Height = tile, Shrink = 0f, Opacity = strength,
                Children = [Controls.CoverFill(v.Mosaic[i % v.Mosaic.Count], 0f, L.MosaicDecode)],
            };
        }
        Element art = new BoxEl
        {
            Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Grow = 1f, MinWidth = 0f, MaxHeight = tile * rows.Length,
            HitTestVisible = false,
            Children = [new BoxEl { Direction = 0, Wrap = true, Width = tile * L.MosaicColumns, Shrink = 0f, Children = cells }],
        };
        if (veil is not { } v2) return art;
        return new BoxEl
        {
            ZStack = true, Grow = 1f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, HitTestVisible = false,
            Children =
            [
                art,
                new BoxEl
                {
                    Grow = 1f, AlignSelf = FlexAlign.Stretch,
                    Gradient = GradientDown(new GradientStop(0f, v2.Ground with { A = v2.Top }), new GradientStop(0.55f, v2.Ground with { A = v2.Mid }), new GradientStop(1f, v2.Ground)),
                },
            ],
        };
    }

    // ── folder chips and the activity (S1 in the hero) ─────────────────────────────────────────────────────────────

    /// <summary>The head's folders and the library's state (S1): a chip per music folder (a small control: its state, its name; a click opens the
    /// music folders), then the state line, or a first scan's line and bar (<paramref name="scanLine"/>, the shared `LocalFiles.Scan` view) in its
    /// place. Every part glides to its place when one before it comes or changes width (a folder chip landing with the first fold, a chip's
    /// "· scanning" leaving it), and the row eases its height when it re-wraps: nothing in the head moves at once.</summary>
    public static Element Chips(LocalCollectionView v, LocalCollectionVerbs verbs, float chipH, Element? scanLine)
    {
        var kids = new List<Element>(v.Chips.Count + 1);
        foreach (var c in v.Chips) kids.Add(Chip(c, verbs.AddMusic, chipH));
        kids.Add(new BoxEl
        {
            Key = "lc-activity", Direction = 0, AlignItems = FlexAlign.Center, Height = chipH, Layout = L.CardReflow, MinWidth = 0f, Shrink = 1f,
            Children = [scanLine ?? Activity(s_headMark, R.Type.DenseMeta("") with { Color = Tok.TextSecondary })],
        });
        return new BoxEl { Direction = 0, Wrap = true, Gap = L.ChipGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Layout = L.HeightReflow, Children = kids.ToArray() };
    }

    /// <summary>The state line's mark in the WinUI/Classic head: a ring while reading, the caution mark when every folder is away, the pause mark
    /// while held, else the tick.</summary>
    static readonly Func<LibraryActivity.LineKind, Element> s_headMark = static kind => kind switch
    {
        LibraryActivity.LineKind.Scanning => ProgressRing.Create(isActive: true, size: L.ChipGlyph),
        LibraryActivity.LineKind.Offline => Icon(Icons.Warning, L.ChipGlyph, Tok.SystemFillAttention),
        LibraryActivity.LineKind.Paused => Icon(Icons.Pause, L.ChipGlyph, Tok.TextSecondary),
        _ => Icon(Icons.Accept, L.ChipGlyph, Tok.AccentTextPrimary),
    };

    static Element Chip(LocalFolderChip c, Action open, float chipH)
    {
        bool bad = c.State is LocalChipState.Offline or LocalChipState.NoAccess;
        string? word = c.State switch
        {
            LocalChipState.Offline => Loc.Get(P.ChipOffline),
            LocalChipState.Scanning => Loc.Get(P.ChipScanning),
            LocalChipState.NoAccess => Loc.Get(P.ChipNoAccess),
            LocalChipState.Waiting => Loc.Get(P.ChipWaiting),
            _ => null,
        };
        // A folder is a folder: its glyph, in the attention ink while it is away or unreadable.
        Element mark = Icon(Icons.Folder, L.ChipGlyph, bad ? Tok.SystemFillAttention : Tok.TextSecondary);
        // The house control's plate (a button's fill, stroke and corners) at the tier's chip height; it glides to its place and width.
        return new BoxEl
        {
            Key = "lc-chip:" + c.Path, Direction = 0, Height = chipH, Layout = L.CardReflow, Gap = L.ChipInner, AlignItems = FlexAlign.Center, Padding = new Edges4(L.ChipPadX, 0f, L.ChipPadX, 0f),
            Corners = Radii.ControlAll, BorderWidth = L.ChipBorder, BorderColor = bad ? Tok.SystemFillAttention : Tok.StrokeControlDefault,
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary, BrushTransitionMs = Design.Motion.Faster,
            OnClick = open, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, MinWidth = 0f, Shrink = 1f,
            Children =
            [
                mark,
                Ell(R.Type.DenseMeta(word is null ? c.Label : c.Label + " · " + word) with { Color = bad ? Tok.TextSecondary : Tok.TextPrimary }),
            ],
        };
    }

    // ── cards ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A small quality pill ("FLAC", "MP3 320") on a cover's foot.</summary>
    static Element Pill(string text) => Corner(new BoxEl
    {
        Height = L.PillH, Padding = new Edges4(L.PillPadX, 0f, L.PillPadX, 0f), AlignItems = FlexAlign.Center, Shrink = 0f,
        Corners = R.Shape.All(ShapeRole.Control), Fill = Tok.FillSolidBase with { A = 0.82f },
        Children = [R.Type.MicroMeta(text) with { Color = Tok.TextPrimary }],
    }, bottom: true, end: false, L.PillInset);

    static Element NewTag() => Corner(new BoxEl
    {
        Height = L.PillH, Padding = new Edges4(L.PillPadX, 0f, L.PillPadX, 0f), AlignItems = FlexAlign.Center, Shrink = 0f,
        Corners = R.Shape.All(ShapeRole.Control), Fill = Tok.AccentDefault,
        Children = [R.Type.MicroMeta(Loc.Get(P.New)) with { Color = Tok.TextOnAccentPrimary }],
    }, bottom: false, end: false, L.NewInset);

    /// <summary>A ZStack layer that fills the cover and parks <paramref name="child"/> in one corner at its own size.</summary>
    static Element Corner(Element child, bool bottom, bool end, float inset) => new BoxEl
    {
        Direction = 1, Grow = 1f, AlignSelf = FlexAlign.Stretch, Padding = Edges4.All(inset), HitTestVisible = false,
        Justify = bottom ? FlexJustify.End : FlexJustify.Start, AlignItems = end ? FlexAlign.End : FlexAlign.Start,
        Children = [child],
    };

    /// <summary>A cover; with none (not extracted yet, or the files carry none) a tile of the name's own colour with its initial, so a library
    /// without art still reads as a wall of albums rather than a grid of grey boxes. The colour is stable per name (<see cref="L.TintOf"/>).</summary>
    /// <param name="flight">The connected flight's key (<see cref="LocalOpened.FolderTile"/>, <see cref="LocalOpened.AlbumTile"/>): a cover is the
    /// picture end, a tile with none flies whole (<see cref="Design.MorphKeys.Whole"/>). Null: untagged.</param>
    static Element Art(string? cover, float size, string seed, string name, bool round = false, string? flight = null)
    {
        var corners = round ? CornerRadius4.All(size / 2f) : R.Shape.All(ShapeRole.Cover);
        if (cover is not null)
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f, ClipToBounds = true, Corners = corners, Layout = L.SizeReflow, MorphId = flight,
                Children = [Controls.CoverFill(cover, 0f, L.CardDecode)],
            };
        var (top, bottom) = L.TintOf(seed);
        string initial = InitialOf(name);
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, Corners = corners, ClipToBounds = true, Layout = L.SizeReflow, MorphId = Design.MorphKeys.Whole(flight),
            Gradient = GradientDown(new GradientStop(0f, top), new GradientStop(1f, bottom)),
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = initial.Length == 0 ? [] : [(size >= L.InitialBigFrom ? R.Type.PageHero(initial) : R.Type.CardTitle(initial)) with { Color = L.InitialInk }],
        };
    }

    /// <summary>The first letter or digit of a name ("" when it has none), never re-cased.</summary>
    static string InitialOf(string name)
    {
        foreach (var rune in name.EnumerateRunes())
            if (System.Text.Rune.IsLetterOrDigit(rune)) return rune.ToString();
        return "";
    }

    /// <summary>An album card: the cover (quality pill, "New" tag, a play button on hover), the title, "artist · year".</summary>
    public static Element AlbumCard(LocalAlbumCard a, float w, LocalCollectionVerbs v, bool pill, bool year)
    {
        var hover = new Signal<bool>(false);
        string key = a.Key;
        // The cover flies into the header of the album's folder when the card opens it (LocalOpened.AlbumTile).
        var cover = new List<Element>(4) { Art(a.Cover, w, a.Key, a.Title, flight: LocalOpened.AlbumTile(key)) };
        if (a.IsNew) cover.Add(NewTag());
        if (pill && a.Quality.Length > 0) cover.Add(Pill(a.Quality));
        cover.Add(new BoxEl
        {
            Width = L.PlayButton, Height = L.PlayButton, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = Edges4.All(L.PlayInset), Shrink = 0f,
            Corners = CornerRadius4.All(L.PlayButton / 2f), Fill = Tok.AccentDefault, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Opacity = Prop.Of(() => hover.Value ? 1f : 0f), HitTestVisible = Prop.Of(() => hover.Value), Cursor = CursorId.Hand,
            OnClick = () => v.PlayAlbum(key), Children = [Icon(Icons.Play, L.PlayGlyph, Tok.TextOnAccentPrimary)],
        });
        string meta = year && a.Year > 0 ? a.Artist + " · " + a.Year.ToString(CultureInfo.InvariantCulture) : a.Artist;
        return new BoxEl
        {
            Key = "lc-album:" + key, Direction = 1, Width = w, Shrink = 0f, Gap = L.CardTextGap, Cursor = CursorId.Hand, Layout = L.SizeReflow,
            OnClick = () => v.OpenAlbum(key), OnHoverMove = _ => hover.Value = true, OnPointerExit = () => hover.Value = false,
            Children =
            [
                new BoxEl { ZStack = true, Width = w, Height = w, Layout = L.SizeReflow, Children = cover.ToArray() },
                new BoxEl
                {
                    Direction = 1, Height = L.CardTextH, MinWidth = 0f,
                    Children = [Ell(R.Type.CardTitle(a.Title) with { Color = Tok.TextPrimary }), Ell(Meta(meta))],
                },
            ],
        };
    }

    /// <summary>A song with no album tag as a card (Home's shelves): its cover (quality pill, "New" tag, a play button on hover), the title, the
    /// artist. A click plays it.</summary>
    public static Element SongCard(LocalSongCard s, float w, LocalCollectionVerbs v, bool pill)
    {
        var hover = new Signal<bool>(false);
        long id = s.Id;
        string seed = "song:" + id.ToString(CultureInfo.InvariantCulture);
        var cover = new List<Element>(4) { Art(s.Cover, w, seed, s.Title) };
        if (s.IsNew) cover.Add(NewTag());
        if (pill && s.Quality.Length > 0) cover.Add(Pill(s.Quality));
        cover.Add(new BoxEl
        {
            Width = L.PlayButton, Height = L.PlayButton, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = Edges4.All(L.PlayInset), Shrink = 0f,
            Corners = CornerRadius4.All(L.PlayButton / 2f), Fill = Tok.AccentDefault, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Opacity = Prop.Of(() => hover.Value ? 1f : 0f), HitTestVisible = false, Children = [Icon(Icons.Play, L.PlayGlyph, Tok.TextOnAccentPrimary)],
        });
        return new BoxEl
        {
            Key = "lc-song:" + id.ToString(CultureInfo.InvariantCulture), Direction = 1, Width = w, Shrink = 0f, Gap = L.CardTextGap, Cursor = CursorId.Hand,
            Layout = L.SizeReflow, OnClick = () => v.PlayFile(id), OnHoverMove = _ => hover.Value = true, OnPointerExit = () => hover.Value = false,
            Children =
            [
                new BoxEl { ZStack = true, Width = w, Height = w, Layout = L.SizeReflow, Children = cover.ToArray() },
                new BoxEl
                {
                    Direction = 1, Height = L.CardTextH, MinWidth = 0f,
                    Children = [Ell(R.Type.CardTitle(s.Title) with { Color = Tok.TextPrimary }), Ell(Meta(s.Artist))],
                },
            ],
        };
    }

    static Element ArtistCard(LocalArtistCard a, float w, LocalCollectionVerbs v)
    {
        string key = a.Key;
        return new BoxEl
        {
            Key = "lc-artist:" + key, Direction = 1, Width = w, Shrink = 0f, Gap = L.CardTextGap, AlignItems = FlexAlign.Center, Cursor = CursorId.Hand, Layout = L.SizeReflow,
            OnClick = () => v.ShuffleArtist(key),
            Children =
            [
                Art(a.Cover, w, a.Key, a.Name, round: true),
                new BoxEl
                {
                    Direction = 1, Height = L.CardTextH, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center,
                    Children = [Ell(R.Type.CardTitle(a.Name) with { Color = Tok.TextPrimary }), Ell(Meta(CountsText(a.Albums, a.Tracks)))],
                },
            ],
        };
    }

    // ── Home (board 18) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Home's shelves, in the user's order (<paramref name="shelves"/>; "Edit home"), the hidden ones left out.</summary>
    /// <param name="reveal">True only on the render "Edit home" showed or hid a shelf: that shelf reveals or folds its height. A shelf that mounts with
    /// Home itself (a pivot switch, Back from a detail opened over Home) arrives at its height: replaying every shelf's reveal pushed the
    /// page open under the user, and kept the card Back flies home to under half visible.</param>
    public static Element[] HomeBody(LocalCollectionView v, LocalCollectionVerbs verbs, Action<LocalPivot> setPivot, CommonLocalCollectionLayout lay, float g,
                                     IReadOnlyList<LocalShelfSlot> shelves, bool reveal, bool house = false)
    {
        // WinUI/Classic (house): the house media card every Home and library shelf wears (Controls.Surface); Zune: its own cover cards.
        Element Album(LocalAlbumCard a, bool pill) => house ? ShelfAlbum(a, lay.Card, verbs, pill) : AlbumCard(a, lay.Card, verbs, pill, year: false);
        Element Song(LocalSongCard s, bool pill) => house ? ShelfSong(s, lay.Card, verbs, pill) : SongCard(s, lay.Card, verbs, pill);
        LayoutTransition shelfReveal = reveal ? L.ShelfReveal : L.ShelfReveal with { Enter = default };
        var kids = new List<Element>(3);
        // "See all" goes to the albums; a library of loose songs has none, so to its songs.
        Action seeAll = v.AlbumCount > 0 ? () => setPivot(LocalPivot.Albums) : () => setPivot(LocalPivot.Songs);
        int shown = 0;
        bool anyOn = false;
        foreach (var slot in shelves) anyOn |= slot.Shown;
        foreach (var slot in shelves)
        {
            // Each shelf in two boxes. The outer one stays in the user's order whether the shelf shows or not, and glides to a new place when
            // "Edit home" moves it (L.ShelfMove); the inner one mounts and unmounts with the shelf, revealing or folding its height, the shelves
            // below riding it (L.ShelfReveal). Kept apart, the two motions never fight over one box.
            string id = slot.Shelf.ToString().ToLowerInvariant();
            Element? shelf = slot.Shown ? Shelf(slot.Shelf) : null;
            if (shelf is not null) shown++;
            kids.Add(new BoxEl
            {
                Key = "lc-home:" + id, Direction = 1, MinWidth = 0f, Layout = L.ShelfMove,
                Children = shelf is null ? [] : [new BoxEl { Key = "lc-home-reveal:" + id, Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = shelfReveal, Children = [shelf] }],
            });
        }
        // "Hidden" only when the user hid every shelf in Edit home: a shelf that is on but has nothing to show (no lossless songs, no year tags) is
        // simply absent, never called hidden.
        if (shown == 0 && !anyOn && v.Songs > 0)
            kids.Add(Gut("lc-home-none", g, Meta(Loc.Get(P.HomeAllHidden)) with { Wrap = TextWrap.Wrap }, top: L.SectionTop));
        return kids.ToArray();

        Element? Shelf(LocalShelf which) => which switch
        {
            LocalShelf.Recent => Recent(),
            LocalShelf.Lossless => LosslessShelf(),
            _ => Decades(),
        };

        Element? Recent()
        {
            if (v.Recent.Count == 0) return null;
            var groups = new List<Element>(v.Recent.Count);
            foreach (var grp in v.Recent)
            {
                // The group's albums, then its songs with no album tag.
                var songs = grp.SongCards;
                var cards = new Element[grp.Albums.Count + songs.Count];
                for (int i = 0; i < grp.Albums.Count; i++) cards[i] = Album(grp.Albums[i], pill: false);
                for (int i = 0; i < songs.Count; i++) cards[grp.Albums.Count + i] = Song(songs[i], pill: false);
                groups.Add(new BoxEl
                {
                    Key = "lc-recent:" + grp.When, Direction = 1, Shrink = 0f, Gap = L.GroupLabelGap,
                    Children =
                    [
                        R.Type.DenseTitle(WhenWord(grp.When)) with { Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary },
                        new BoxEl { Direction = 0, Gap = lay.CardGap, Children = cards },
                    ],
                });
            }
            string? sub = v.RecentThisMonth > 0 && v.RecentSongsThisMonth > 0 ? P.ThisMonthBoth(P.Albums(v.RecentThisMonth), P.Songs(v.RecentSongsThisMonth))
                : v.RecentThisMonth > 0 ? P.ThisMonth(v.RecentThisMonth)
                : v.RecentSongsThisMonth > 0 ? P.ThisMonthSongs(v.RecentSongsThisMonth) : null;
            return Section("recent", Loc.Get(P.RecentlyAdded), sub, seeAll, ShelfRow("recent", groups.ToArray(), L.GroupGap, g), g, lay.ChipH);
        }

        Element? LosslessShelf()
        {
            if (v.Lossless.Count + v.LosslessLoose.Count == 0) return null;
            // The lossless albums, then the lossless songs with no album tag.
            var cards = new Element[v.Lossless.Count + v.LosslessLoose.Count];
            for (int i = 0; i < v.Lossless.Count; i++) cards[i] = Album(v.Lossless[i], pill: true);
            for (int i = 0; i < v.LosslessLoose.Count; i++) cards[v.Lossless.Count + i] = Song(v.LosslessLoose[i], pill: true);
            string sub = v.HiResSongs > 0 ? P.LosslessSongs(v.LosslessSongs) + " · " + P.HiResSongs(v.HiResSongs) : P.LosslessSongs(v.LosslessSongs);
            return Section("lossless", Loc.Get(P.LosslessShelf), sub, seeAll, ShelfRow("lossless", cards, lay.CardGap, g), g, lay.ChipH);
        }

        Element? Decades()
        {
            if (v.Decades.Count == 0) return null;
            var tiles = new Element[v.Decades.Count];
            for (int i = 0; i < tiles.Length; i++) tiles[i] = DecadeTile(v.Decades[i], verbs, lay);
            Element grid = Gut("lc-decades", g, new GridEl { MinColWidth = lay.DecadeMin, ColGap = lay.CardGap, RowGap = lay.CardGap, Children = tiles }) with { Layout = L.HeightReflow };   // another column count eases to its new height
            return Section("decades", Loc.Get(P.Decades), Loc.Get(P.DecadesSub), null, grid, g, lay.ChipH);
        }
    }

    /// <summary>A shelf's name, as "Edit home" lists it.</summary>
    public static string ShelfName(LocalShelf s) => s switch
    {
        LocalShelf.Recent => Loc.Get(P.RecentlyAdded),
        LocalShelf.Lossless => Loc.Get(P.LosslessShelf),
        _ => Loc.Get(P.Decades),
    };

    static string WhenWord(LocalAddedWhen w) => w switch
    {
        LocalAddedWhen.Today => Loc.Get(P.Today),
        LocalAddedWhen.ThisWeek => Loc.Get(P.ThisWeek),
        LocalAddedWhen.ThisMonth => Loc.Get(P.EarlierThisMonth),
        _ => Loc.Get(P.Earlier),
    };

    static Element Section(string id, string title, string? sub, Action? seeAll, Element body, float g, float chipH)
    {
        var head = new List<Element>(4) { Heading(title) };
        if (sub is not null) head.Add(Ell(Meta(sub)));
        head.Add(Spacer());
        if (seeAll is not null)
            head.Add(new BoxEl
            {
                Key = "lc-seeall:" + id, Direction = 0, Gap = L.ChipInner, AlignItems = FlexAlign.Center, Height = chipH, Layout = L.SizeReflow, OnClick = seeAll, Cursor = CursorId.Hand,
                Children = [R.Type.DenseMeta(Loc.Get(P.SeeAll)) with { Color = Tok.AccentTextPrimary }, Icon(Icons.ChevronRight, L.SeeAllGlyph, Tok.AccentTextPrimary)],
            });
        return new BoxEl
        {
            Key = L.ShelfKey + ":" + id, Direction = 1, MinWidth = 0f, Gap = L.HeadGap, Padding = new Edges4(0f, L.SectionTop, 0f, 0f),
            Children =
            [
                Gut("lc-head:" + id, g, new BoxEl { Direction = 0, Gap = L.ThumbGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() }),
                body,
            ],
        };
    }

    static Element ShelfRow(string id, Element[] items, float gap, float g) => new ScrollEl
    {
        Key = "lc-shelf-scroll:" + id, Horizontal = true, ContentSized = true, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
        Content = new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Start,
            Children = [Air("lc-shelf-air:" + id + ":l", g), new BoxEl { Direction = 0, Gap = gap, Shrink = 0f, AlignItems = FlexAlign.Start, Children = items }, Air("lc-shelf-air:" + id + ":r", g)],
        },
    };

    static Element DecadeTile(LocalDecade d, LocalCollectionVerbs v, CommonLocalCollectionLayout lay)
    {
        int decade = d.Decade;
        var covers = new List<Element>(L.DecadeCovers);
        for (int i = 0; i < Math.Min(L.DecadeCovers, Math.Max(1, d.Covers.Count)); i++)
            covers.Add(Art(i < d.Covers.Count ? d.Covers[i] : null, L.DecadeCover, "decade" + decade + ":" + i, "") is var art && i > 0 ? new BoxEl { Margin = new Edges4(-L.DecadeOverlap, 0f, 0f, 0f), Children = [art] } : art);
        return new BoxEl
        {
            Key = "lc-decade:" + decade, Direction = 0, Height = L.DecadeH, MinWidth = 0f, Gap = L.DecadeGap,
            AlignItems = FlexAlign.Center, Padding = new Edges4(L.DecadePad, 0f, L.DecadePad, 0f), Corners = R.Shape.All(ShapeRole.Card),
            Fill = Tok.FillCardDefault, BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeCardDefault, OnClick = () => v.ShuffleDecade(decade), Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl { Direction = 0, Shrink = 0f, Children = covers.ToArray() },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Ell(R.Type.CardTitle(P.Decade(decade.ToString(CultureInfo.InvariantCulture))) with { Color = Tok.TextPrimary }),
                        Ell(Meta(CountsText(d.Albums, d.Songs))),
                    ],
                },
                Icon(Icons.Shuffle, L.SeeAllGlyph, Tok.TextSecondary),
            ],
        };
    }

    // ── the measured virtual page (wall, artists, folders) ─────────────────────────────────────────────────────────

    /// <summary>A virtual list cut to its measured width: <paramref name="cut"/>(width, version) gives the rows.</summary>
    public static Element Measured(string key, Func<float, (int Rows, Func<int, float> Extent, Func<int, Element> Row)> cut, string scrollKey)
        => Embed.Comp(new MeasuredProps(key, cut, scrollKey), static () => new MeasuredHost()) with { Key = key };

    sealed record MeasuredProps(string Key, Func<float, (int Rows, Func<int, float> Extent, Func<int, Element> Row)> Cut, string ScrollKey)
    {
        public bool Equals(MeasuredProps? o) => o is not null && Key == o.Key && ReferenceEquals(Cut, o.Cut);
        public override int GetHashCode() => Key.GetHashCode();
    }

    sealed class MeasuredHost : Component
    {
        MeasuredStackVirtualLayout? _layout;
        float _cutW = -1f;
        object? _cutFor;

        public override Element Render()
        {
            var p = UseProps<MeasuredProps>();
            float w = UseMeasuredWidth(1f).Value;
            if (w <= 0f) return new BoxEl { Grow = 1f, MinWidth = 0f, MinHeight = 0f };
            var (rows, extent, row) = p.Cut(w);
            if (_layout is null || _cutW != w || !ReferenceEquals(_cutFor, p.Cut))
            {
                _layout = new MeasuredStackVirtualLayout(extent(Math.Min(1, rows - 1)), extentOf: extent);
                _cutW = w;
                _cutFor = p.Cut;
            }
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [Virtual.Measured(rows, _layout, row, i => p.Key + ":" + i.ToString(CultureInfo.InvariantCulture)) with { ScrollKey = p.ScrollKey }],
            };
        }
    }

    /// <summary>A keyed virtual grid of square-covered cards cut to its measured width: cells of least width <paramref name="min"/>, the card's words under
    /// the cover. Each card glides to its new place when the column count changes (the reflow FLIP), never remounts.</summary>
    public static Element Grid(string key, int count, float min, float gap, Func<int, float, Element> card, Func<int, string> keyOf, string scrollKey)
        => Embed.Comp(new GridProps(key, count, min, gap, card, keyOf, scrollKey), static () => new GridHost()) with { Key = key };

    sealed record GridProps(string Key, int Count, float Min, float Gap, Func<int, float, Element> Card, Func<int, string> KeyOf, string ScrollKey)
    {
        public bool Equals(GridProps? o) => o is not null && Key == o.Key && Count == o.Count && Min == o.Min && Gap == o.Gap && ReferenceEquals(Card, o.Card);
        public override int GetHashCode() => Key.GetHashCode();
    }

    sealed class GridHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<GridProps>();
            float w = UseMeasuredWidth(1f).Value;
            if (w <= 0f) return new BoxEl { Grow = 1f, MinWidth = 0f, MinHeight = 0f };
            var (cols, cell) = Columns(w, 0f, p.Min, p.Gap);
            float itemH = cell + CommonLocalCollectionRules.CardTextGap + CommonLocalCollectionRules.CardTextH;
            var card = p.Card;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [Virtual.Grid(p.Count, cols, itemH, p.Gap, i => (card(i, cell) is BoxEl b ? b with { Layout = CommonLocalCollectionRules.CardReflow } : card(i, cell)), p.KeyOf) with { ScrollKey = p.ScrollKey }],
            };
        }
    }

    /// <summary>How many cells of least width <paramref name="min"/> fit, and their width.</summary>
    public static (int Columns, float Cell) Columns(float width, float g, float min, float gap)
    {
        float inner = Math.Max(1f, width - 2f * g);
        int cols = Math.Max(1, (int)((inner + gap) / (min + gap)));
        return (cols, (inner - gap * (cols - 1)) / cols);
    }

    static Element CardRow(int first, int count, int cols, float gap, float g, Func<int, Element> cell)
    {
        var kids = new Element[Math.Min(cols, count - first)];
        for (int i = 0; i < kids.Length; i++) kids[i] = cell(first + i);
        return Gut("lc-row", g, new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = kids }, bottom: gap);
    }

    // ── Albums (board 19) ───────────────────────────────────────────────────────────────────────────────────────────

    public static Element AlbumWall(LocalCollectionView v, LocalAlbumSort sort, Action<LocalAlbumSort> setSort, LocalCollectionVerbs verbs,
        CommonLocalCollectionLayout lay, float g, string routeKey)
    {
        var albums = v.AlbumsBy(sort);
        Element head = SortRow(v, sort, setSort, g, lay.ChipH);
        if (albums.Count == 0)
            return new BoxEl { Direction = 1, Grow = 1f, Children = [head, Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page, Loc.Get(P.NoAlbums), "")] };
        // The wall is ONE keyed virtual grid (a card per album, keyed by it), so a column change moves each card in place under the reflow FLIP
        // instead of re-cutting rows (a card that changed rows would remount and jump).
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                head,
                Gut(L.WallKey, g, Grid(L.WallKey + ":" + v.Version + ":" + sort, albums.Count, lay.WallMin, lay.WallGap,
                    (k, cell) => AlbumCard(albums[k], cell, verbs, pill: true, year: true), k => "lc-album:" + albums[k].Key, routeKey + ":albums:" + sort), grow: true),
            ],
        };
    }

    static Element SortRow(LocalCollectionView v, LocalAlbumSort sort, Action<LocalAlbumSort> setSort, float g, float chipH)
    {
        var kids = new List<Element>(7) { R.Type.DenseMeta(Loc.Get(P.SortBy)) with { Color = Tok.TextSecondary } };
        foreach (var s in (ReadOnlySpan<LocalAlbumSort>)[LocalAlbumSort.Added, LocalAlbumSort.Artist, LocalAlbumSort.Title, LocalAlbumSort.Year])
        {
            var to = s;
            bool on = s == sort;
            kids.Add(new BoxEl
            {
                Key = "lc-sort:" + s, Direction = 0, Gap = L.ChipInner, Height = chipH, Layout = L.SizeReflow, AlignItems = FlexAlign.Center, Padding = new Edges4(L.ChipPadX, 0f, L.ChipPadX, 0f),
                Corners = CornerRadius4.All(chipH / 2f), BorderWidth = on ? 0f : L.ChipBorder, BorderColor = Tok.StrokeControlDefault,
                Fill = on ? Tok.AccentDefault : Tok.FillControlDefault, OnClick = () => setSort(to), Cursor = CursorId.Hand,
                Children = on
                    ? [Icon(Icons.Check, L.ChipGlyph, Tok.TextOnAccentPrimary), R.Type.DenseMeta(SortWord(s)) with { Color = Tok.TextOnAccentPrimary }]
                    : [R.Type.DenseMeta(SortWord(s)) with { Color = Tok.TextPrimary }],
            });
        }
        // The chips scroll sideways where the row is narrower than they are (Compact); the count keeps the end.
        Element chips = new ScrollEl
        {
            Horizontal = true, ContentSized = true, Grow = 1f, Basis = 0f, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
            Content = new BoxEl { Direction = 0, Gap = L.SortGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() },
        };
        return Gut("lc-sort", g, new BoxEl
        {
            Direction = 0, Gap = L.SortGap, AlignItems = FlexAlign.Center, Height = L.SortRowH, MinWidth = 0f,
            Children = [chips, R.Type.DenseMeta(P.Albums(v.AlbumCount)) with { Color = Tok.TextSecondary, Shrink = 0f }],
        });
    }

    static string SortWord(LocalAlbumSort s) => s switch
    {
        LocalAlbumSort.Artist => Loc.Get(P.SortArtist),
        LocalAlbumSort.Title => Loc.Get(P.SortTitle),
        LocalAlbumSort.Year => Loc.Get(P.SortYear),
        _ => Loc.Get(P.SortAdded),
    };

    // ── Artists ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public static Element ArtistGrid(LocalCollectionView v, LocalCollectionVerbs verbs, CommonLocalCollectionLayout lay, float g, string routeKey)
    {
        var artists = v.Artists;
        if (artists.Count == 0) return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page, Loc.Get(P.NoAlbums), "");
        return Gut(L.ArtistsKey, g, Grid(L.ArtistsKey + ":" + v.Version, artists.Count, lay.ArtistMin, lay.WallGap,
            (k, cell) => ArtistCard(artists[k], cell, verbs), k => "lc-artist:" + artists[k].Key, routeKey + ":artists"), top: L.SectionTop, grow: true);
    }

    // ── Folders (board 20) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Folders browser (<paramref name="opened"/> null: the music folders) or an open folder, an album's folder included (board 20): the
    /// breadcrumb from <paramref name="title"/> ("On this PC", or the pivot the album was opened from), the header (the folder's or the album's cover,
    /// tagged for the flight from its tile, its name and counts), the folders inside as house cards (or rows), its own songs. With the page's
    /// <see cref="LocalCollectionPageProps.FolderMenus"/> every folder card, row and the open header's "⋯" carry the folder's menu, and the empty area
    /// and the breadcrumb the browser's. <paramref name="views"/>: the grid | list pair (the Albums and Artists view code) at the head's end; false
    /// (Zune) lists the folders as cards only. The pair is the page's own two cells, never the structure's view control with its "⋯" of sizes: an
    /// open folder's "⋯" stands in the same row, and two "⋯" side by side read as one menu twice.</summary>
    public static Element FolderBrowser(LocalCollectionView v, LocalOpened? opened, LocalCollectionPageProps p, CommonLocalCollectionLayout lay, float g, string title,
        bool views = false)
        => Embed.Comp(new FolderProps(v, opened, p, lay, g, title, views), static () => new FolderHost()) with { Key = "lc-folders-host" };

    sealed record FolderProps(LocalCollectionView View, LocalOpened? Opened, LocalCollectionPageProps Page, CommonLocalCollectionLayout Lay, float Gutter, string Title,
        bool Views);

    /// <summary>The browser's host: reads the view code (a grid | list pick re-renders the browser alone), holds the folder a detail shows while a
    /// rename or a delete re-keys it (the fold lists the new key a moment later: the detail never falls back to the music folders meanwhile), and
    /// attaches the empty area's menu.</summary>
    sealed class FolderHost : Component
    {
        LocalFolderNode? _held;

        public override Element Render()
        {
            var fp = UseProps<FolderProps>();
            var overlay = UseContext(Overlay.Service);
            var p = fp.Page;
            var v = fp.View;
            var opened = fp.Opened;
            var lay = fp.Lay;
            float g = fp.Gutter;
            int code = fp.Views ? Math.Clamp(p.ListView().Value, 0, 3) : 3;
            int size = fp.Views ? Math.Clamp(p.ListSize().Value, 0, K.GridScale.Length - 1) : 1;
            var menus = Controls.IsNullOverlay(overlay) ? null : p.FolderMenus;
            Action<string> open = p.OpenFolder;
            var verbs = p.Verbs;

            string folder = opened?.Folder ?? "";
            string keyed = opened?.ViewKey ?? "";   // the list and its scroll: a rename keeps them (LocalOpened.ViewKey)
            LocalFolderNode? node = folder.Length > 0 && v.Folders.TryGetValue(folder, out var n) ? n : null;
            if (node is null && folder.Length > 0 && _held is { } held && held.Key == folder) node = held;
            _held = node;
            IReadOnlyList<string> childKeys = node?.Children ?? v.RootKeys;
            var children = new List<LocalFolderNode>(childKeys.Count);
            foreach (string k in childKeys) if (v.Folders.TryGetValue(k, out var c)) children.Add(c);
            var files = node?.Files ?? [];

            // The breadcrumb: On this PC › the music folder › … › this folder; an album's folder starts at the pivot it was opened from (Albums ›
            // the album's folder › …).
            var trail = new List<(string Label, string Key)> { (fp.Title, "") };
            var chain = new List<LocalFolderNode>();
            string root = opened?.Root ?? "";
            for (var at = node; at is not null; at = at.Key != root && at.ParentKey is { } pk && v.Folders.TryGetValue(pk, out var up) ? up : null) chain.Add(at);
            for (int i = chain.Count - 1; i >= 0; i--) trail.Add((chain[i].Name, chain[i].Key));
            var labels = new string[trail.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = trail[i].Label;
            Element crumbs = BreadcrumbBar.Create(labels, i => { if ((uint)i < (uint)trail.Count) open(trail[i].Key); }, size: ControlSize.Large);

            int songs = node?.Songs ?? v.Songs;
            long length = node?.DurationMs ?? v.DurationMs;
            // An open folder with no folders inside leaves the "0 folders" out; the music folders' row always counts them.
            string meta = (node is null || children.Count > 0 ? P.Folders(children.Count) + " · " : "") + P.Songs(songs) + " · " + Length(length);

            // The head: the breadcrumb (and the music folders' counts), then an open folder's Play, Shuffle and "⋯", then the view pair. Beside the
            // breadcrumb where the head docks its actions beside the title; under it at Compact (a row of its own, so nothing wraps over the list).
            var lead = new List<Element>(2) { new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, Children = [crumbs] } };
            if (node is null) lead.Add(Ell(Meta(meta)));
            var acts = new List<Element>(4);
            float h = lay.ControlH;
            var buttonSize = lay.Tier == WidthTier.Compact ? ControlSize.Large : ControlSize.Medium;
            if (node is not null)
            {
                string key = node.Key;
                acts.Add(Button.Create(Loc.Get(P.PlayFolder), () => verbs.PlayFolder(key), ButtonAppearance.Accent, buttonSize, glyph: Icons.Play, isEnabled: songs > 0));
                acts.Add(Button.Create(Loc.Get(P.Shuffle), () => verbs.ShuffleFolder(key), ButtonAppearance.Standard, buttonSize, glyph: Icons.Shuffle, isEnabled: songs > 0));
                if (menus is not null) acts.Add(HeadMore(overlay!, () => menus.Folder(key, true), h));
            }
            if (fp.Views) acts.Add(Embed.Comp(new ViewPairProps(p.ListView(), h), static () => new ViewPair()) with { Key = "lc-folder-view" });
            bool stacked = !lay.ActionsBeside && acts.Count > 0;
            BoxEl headBox = stacked
                ? new BoxEl
                {
                    Key = "lc-folder-head", Direction = 1, Gap = K.ToolbarGap, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = K.ThumbGap, AlignItems = FlexAlign.Center, Height = K.SortRowH, MinWidth = 0f, Children = lead.ToArray() },
                        new BoxEl { Direction = 0, Gap = K.ActionGap, AlignItems = FlexAlign.Center, Height = h, MinWidth = 0f, Children = node is null ? [Spacer(), .. acts] : acts.ToArray() },
                    ],
                }
                : new BoxEl
                {
                    Key = "lc-folder-head", Direction = 0, Gap = K.ThumbGap, AlignItems = FlexAlign.Center, Height = K.SortRowH, MinWidth = 0f,
                    Children = [.. lead, Spacer(), new BoxEl { Direction = 0, Gap = K.ActionGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = acts.ToArray() }],
                };
            // The breadcrumb's menu is the empty area's (Add a music folder, New folder inside, Rescan, Show in File Explorer, the view).
            if (menus is not null) headBox = ContextSheets.Attach(headBox, overlay!, () => menus.Area(folder, fp.Views));
            Element head = Gut("lc-folder-head", g, headBox, top: K.CompactTop, bottom: K.CompactTop);
            float headH = 2f * K.CompactTop + (stacked ? K.SortRowH + K.ToolbarGap + h : K.SortRowH);
            Element? header = node is null ? null : DetailHeader(v, opened!, node, meta, lay, g);
            Element songsHead = Gut("lc-songs-head", g, new BoxEl
            {
                Direction = 0, Gap = K.ThumbGap, AlignItems = FlexAlign.Center, Height = K.SortRowH, MinWidth = 0f,
                Children = [Heading(Loc.Get(P.SongsHere)), Ell(Meta(P.Songs(files.Count) + " · " + Length(node?.DurationHereMs ?? 0)))],
            });
            bool grid = code >= 2, compact = code is 0 or 2;
            // Keyed by what it shows, never by the fold's version: a fold that lands while a card is pressed (a rescan after a disk verb, the watcher)
            // re-renders the rows in place, so the press and its release meet the same card (a remount between them lost the click or the menu).
            Element list = Measured(K.FoldersKey + ":" + (keyed.Length == 0 ? "top" : keyed) + ":" + code + ":" + size + ":" + (stacked ? "s" : "w"), w =>
            {
                int cols = 1;
                float cell = 0f, itemH;
                if (grid)
                {
                    (cols, cell) = Columns(w, g, lay.WallMin * K.GridScale[size], lay.CardGap);
                    itemH = SurfaceGeometry.ShelfHeight(cell, 1f, compact ? 0 : 1, false, flush: true) + lay.CardGap;
                }
                else itemH = compact ? K.CompactRowH : K.RowH;
                int tileRows = grid ? (children.Count + cols - 1) / cols : children.Count;
                int tilesAt = header is null ? 1 : 2;
                int songsAt = tilesAt + tileRows;
                int rows = songsAt + (files.Count > 0 ? 1 + files.Count : 0);
                return (rows,
                    i => i == 0 ? headH : i < tilesAt ? lay.DetailCover + K.DetailBottom : i < songsAt ? itemH : i == songsAt ? K.SortRowH : K.SongRowH,
                    i => i == 0 ? head
                        : i < tilesAt ? header!
                        : i < songsAt
                            ? grid ? CardRow((i - tilesAt) * cols, children.Count, cols, lay.CardGap, g, k => FolderCard(children[k], cell, open, verbs, menus, compact))
                                   : Gut("lc-folder-row", MathF.Max(0f, g - SurfaceGeometry.RowPad), FolderRow(children[i - tilesAt], open, verbs, menus, compact))
                        : i == songsAt ? songsHead
                        : SongRow(files[i - songsAt - 1], g, lay.FileColumn, verbs));
            }, p.RouteKey + ":folders:" + keyed);
            var body = new BoxEl { Key = "lc-folders-area", Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Children = [list] };
            // The empty area (between and under the cards, beside the songs) opens the browser's menu; a card's own menu wins where it stands.
            return menus is null ? body : ContextSheets.Attach(body, overlay!, () => menus.Area(folder, fp.Views));
        }
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    /// <summary>The open folder's "⋯" beside its Play and Shuffle: the house icon action at the head's control height, its flyout the folder's menu.</summary>
    static Element HeadMore(IOverlayService overlay, Func<ContextMenuModel?> menu, float h)
        => ToolTip.Wrap(ContextSheets.Attach(Controls.IconAction(Icons.More, null, requestsContext: true, size: h) with { Key = "lc-folder-more" }, overlay, menu, s_moreMenu),
            Loc.Get(Strings.Common.More));

    /// <summary>The header of an open folder (under the breadcrumb): its picture (the album's cover at the root an album card opened), tagged with
    /// the flight key of the tile that opens it (<see cref="LocalOpened.HeaderTile"/>), so that cover flies in here and back; its name over its
    /// counts (an album: its artist and year first). One fixed row of the list: the cover's height and the air under it.</summary>
    static Element DetailHeader(LocalCollectionView v, LocalOpened opened, LocalFolderNode node, string meta, CommonLocalCollectionLayout lay, float g)
    {
        float size = lay.DetailCover;
        string tag = opened.HeaderTile;
        LocalAlbumCard? album = null;
        if (opened.Album.Length > 0 && node.Key == opened.Root)
            foreach (var a in v.Albums) if (a.Key == opened.Album) { album = a; break; }
        Element art = album is not null ? Art(album.Cover, size, album.Key, album.Title, flight: tag) : FolderArt(node, size, tag, out _);
        string name = album?.Title ?? node.Name;
        string sub = album is null ? meta
            : (album.Year > 0 ? album.Artist + " · " + album.Year.ToString(CultureInfo.InvariantCulture) : album.Artist) + " · " + meta;
        return Gut("lc-detail-head", g, new BoxEl
        {
            Key = "lc-detail-head", Direction = 0, Gap = L.DetailGap, AlignItems = FlexAlign.Center, Height = size, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "lc-detail-cover", Width = size, Height = size, Shrink = 0f, Layout = L.SizeReflow, Children = [art] },
                new BoxEl
                {
                    Direction = 1, Gap = L.DetailMetaGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        R.Type.HeroTitle(name) with { Key = "lc-detail-title", Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Meta(sub) with { MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
            ],
        }, bottom: L.DetailBottom);
    }

    /// <summary>A folder's picture (board 20): its own cover or folder image, else a 2 x 2 mosaic of the covers inside, else ONE placeholder glyph,
    /// tagged <paramref name="flight"/> (a mosaic or the glyph flies whole). <paramref name="pictured"/>: not the glyph tile.</summary>
    static Element FolderArt(LocalFolderNode f, float w, string flight, out bool pictured)
    {
        pictured = true;
        if (f.Cover is not null) return Art(f.Cover, w, f.Key, f.Name, flight: flight);
        if (f.Mosaic.Count == 1) return Art(f.Mosaic[0], w, f.Key, f.Name, flight: flight);
        if (f.Mosaic.Count >= 2)
        {
            float half = w / 2f;
            var cells = new Element[4];
            for (int i = 0; i < 4; i++) cells[i] = new BoxEl { Width = half, Height = half, Layout = L.SizeReflow, Children = [Controls.CoverFill(f.Mosaic[i % f.Mosaic.Count], 0f, L.MosaicDecode)] };
            return new BoxEl
            {
                Width = w, Height = w, Wrap = true, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover), Layout = L.SizeReflow,
                MorphId = Design.MorphKeys.Whole(flight), Children = cells,
            };
        }
        pictured = false;
        return new BoxEl
        {
            Width = w, Height = w, Corners = R.Shape.All(ShapeRole.Cover), Fill = Design.WatchedPlaceholder(null), BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeCardDefault,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Layout = L.SizeReflow, MorphId = Design.MorphKeys.Whole(flight),
            Children = [Icon(Icons.Folder, L.FolderGlyph, Tok.TextTertiary)],
        };
    }

    /// <summary>A folder card or row's surface data: its name over its counts, it opens on a click (Enter on the keyboard), its play disc plays it, and
    /// its "⋯" and a right-click open the folder's menu (<see cref="LocalFolderMenus.Folder"/>).</summary>
    static Controls.CardData FolderData(LocalFolderNode f, Action<string> open, LocalCollectionVerbs v, LocalFolderMenus? menus, bool second, Element cover)
    {
        string key = f.Key;
        string line = f.Songs > 0
            ? P.Songs(f.Songs) + " · " + Length(f.DurationMs) + (f.Quality.Length > 0 ? " · " + f.Quality : "")
            : f.Children.Count > 0 ? P.Folders(f.Children.Count) : Loc.Get(Strings.LocalFiles.Folders.EmptyFolder);
        return new Controls.CardData("wavee:local:folder:" + key, f.Name, second ? Second(line) : null, null, () => open(key),
            f.Songs > 0 ? () => v.PlayFolder(key) : null, ShowMenu: menus is not null)
        {
            CoverOverride = cover,
            Menu = menus is null ? null : () => menus.Folder(key, false),
        };
    }

    /// <summary>A folder as the house card (the album wall's card: the plate, its hover and press, the play disc, the "⋯"): its picture filling the
    /// cover (<see cref="FolderCover"/>), its full path the tooltip.</summary>
    static Element FolderCard(LocalFolderNode f, float w, Action<string> open, LocalCollectionVerbs v, LocalFolderMenus? menus, bool compact)
    {
        // A tier crossing changes the cell: the card eases to its new size and place (the wall's reflow), so the row under it rides it.
        Element card = Controls.Surface(FolderData(f, open, v, menus, !compact, FolderCover(f, w)), Shape.Shelf(compact ? 0 : 1) with { Flush = true }, w)
            with { Key = "lc-folder:" + f.Key, Layout = L.CardReflow };
        return f.Path.Length > 0 ? ToolTip.Wrap(card, f.Path) with { Key = "lc-folder-tip:" + f.Key } : card;
    }

    /// <summary>A folder as the house media row (the library's list row): the folder's picture at the row's art.</summary>
    static Element FolderRow(LocalFolderNode f, Action<string> open, LocalCollectionVerbs v, LocalFolderMenus? menus, bool compact)
    {
        float edge = compact ? L.CompactRowArt : L.RowArt;
        Element art = new BoxEl { Width = edge, Height = edge, Shrink = 0f, Children = [FolderArt(f, edge, LocalOpened.FolderTile(f.Key), out _)] };
        return Controls.Surface(FolderData(f, open, v, menus, !compact, art), RowShape(compact)) with { Key = "lc-folder:" + f.Key };
    }

    /// <summary>A folder card's picture, filling the cover box the structure gives it: its own cover or folder image, else a 2 x 2 mosaic of the covers
    /// inside, else the folder glyph; a picture wears the small folder badge (the glyph already is one). Tagged for the flight into the folder's
    /// header (<see cref="LocalOpened.FolderTile"/>: a cover is the picture end, a mosaic or the glyph flies whole).</summary>
    static Element FolderCover(LocalFolderNode f, float edge)
    {
        string flight = LocalOpened.FolderTile(f.Key);
        string? picture = f.Cover ?? (f.Mosaic.Count == 1 ? f.Mosaic[0] : null);
        var kids = new List<Element>(3);
        bool pictured = true;
        if (picture is not null) kids.AddRange(CardArt(picture, edge, Radii.Card, flight));
        else if (f.Mosaic.Count >= 2)
        {
            var cells = new Element[2];
            for (int r = 0; r < 2; r++)
                cells[r] = new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 0f,
                    Children =
                    [
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [Controls.CoverFill(f.Mosaic[(2 * r) % f.Mosaic.Count], 0f, L.MosaicDecode)] },
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [Controls.CoverFill(f.Mosaic[(2 * r + 1) % f.Mosaic.Count], 0f, L.MosaicDecode)] },
                    ],
                };
            kids.Add(new BoxEl { Direction = 1, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card), Children = cells });
        }
        else
        {
            pictured = false;
            kids.Add(new BoxEl
            {
                Corners = CornerRadius4.All(Radii.Card), Fill = Design.WatchedPlaceholder(null), BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeCardDefault,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Children = [Icon(Icons.Folder, L.FolderGlyph, Tok.TextTertiary)],
            });
        }
        if (pictured)
            kids.Add(Corner(new BoxEl
            {
                Width = L.ChipH, Height = L.ChipH, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Control), Fill = Tok.FillSolidBase with { A = 0.82f },
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Children = [Icon(Icons.Folder, L.ChipGlyph, Tok.TextPrimary)],
            }, bottom: true, end: false, L.PillInset));
        return new BoxEl { ZStack = true, MorphId = picture is null ? Design.MorphKeys.Whole(flight) : null, Children = kids.ToArray() };
    }

    static Element SongRow(LocalSongItem s, float g, bool fileColumn, LocalCollectionVerbs verbs)
    {
        long id = s.Id;
        var kids = new List<Element>(6)
        {
            new BoxEl { Width = L.SongNumberW, Shrink = 0f, Children = [Meta(s.Number.ToString(CultureInfo.CurrentCulture))] },
            new BoxEl { Grow = 2f, Basis = 0f, MinWidth = 0f, Children = [Ell(R.Type.TrackTitle(s.Title) with { Color = s.Playable ? Tok.TextPrimary : Tok.TextDisabled })] },
            new BoxEl { Grow = 2f, Basis = 0f, MinWidth = 0f, Children = [Ell(Meta(s.Album))] },
        };
        if (fileColumn) kids.Add(new BoxEl { Grow = 2f, Basis = 0f, MinWidth = 0f, Children = [Ell(Meta(s.File))] });
        kids.Add(new BoxEl
        {
            Width = L.SongQualityW, Shrink = 0f,
            Children = s.Quality.Length == 0 ? [] :
            [
                new BoxEl
                {
                    Height = L.PillH, Padding = new Edges4(L.PillPadX, 0f, L.PillPadX, 0f), AlignItems = FlexAlign.Center, Corners = R.Shape.All(ShapeRole.Control),
                    BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeControlDefault, Children = [R.Type.MicroMeta(s.Quality) with { Color = Tok.TextSecondary }],
                },
            ],
        });
        kids.Add(new BoxEl { Width = L.SongTimeW, Shrink = 0f, Justify = FlexJustify.End, Children = [Meta(Clock(s.DurationMs))] });
        return Gut("lc-song-gut", g, new BoxEl
        {
            Key = "lc-song:" + id, Direction = 0, Height = L.SongRowH, Gap = L.SongColGap, AlignItems = FlexAlign.Center,
            Padding = new Edges4(L.ChipPadX, 0f, L.ChipPadX, 0f), MinWidth = 0f, Corners = R.Shape.All(ShapeRole.Control),
            OnClick = s.Playable ? () => verbs.PlayFile(id) : null, Children = kids.ToArray(),
        }.Interactive(Interaction.ListRow, isEnabled: s.Playable));
    }

    // ── list -> detail motion (LocalOpened) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The body under a pivot, list or open detail, keyed so a step in or out swaps it: <paramref name="enter"/> plays only on the step's own
    /// render (null on a pivot switch, whose body the pivot's recipe moves, and on the page's first), the exit leg always (a node leaves with the leg
    /// it last rendered: the list by drilling in, a detail by stepping back).</summary>
    public static Element DetailSlot(string key, Element body, LayoutTransition? enter, LayoutTransition? exit) => new BoxEl
    {
        Key = key, Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Children = [body],
        Layout = enter is { } e ? e with { Exit = exit?.Exit ?? default, ExitDynamics = exit?.ExitDynamics ?? exit?.Dynamics, ExitDelayMs = exit?.ExitDelayMs }
               : exit is { } x ? x with { Enter = default } : null,
    };

    /// <summary>The structure's drill (<see cref="PageSwap.DrillIn"/> / <see cref="PageSwap.DrillBack"/>: WinUI's, its reduced fade under reduced motion),
    /// else the core's quick same-kind fade; under a flight (<paramref name="flown"/>) only its fades (<c>Design.Nav.Quiet</c>), so the cover is the
    /// one thing that travels, as a page swap under a flight is. Its legs only: never a FLIP of the body's own moves.</summary>
    public static LayoutTransition? Drill(bool back, bool flown, ThemeFacts facts)
    {
        var motion = facts.Policies.PageMotion;
        LayoutTransition? r = FluentGpu.Dsl.Motion.ReducedMotion ? motion.ReducedFade
            : motion.Recipe?.Invoke(back ? PageSwap.DrillBack : PageSwap.DrillIn) ?? Design.Nav.SameKindSwap;
        if (r is not { } t) return null;
        if (flown && motion.ConnectedQuietPage) t = Design.Nav.Quiet(in t);
        return t with { Channels = TransitionChannels.Opacity };
    }

    /// <summary>Does this structure fly a tile's cover into the detail (its connected spec)? A leaving body expects the flight's quiet leg then.</summary>
    public static bool Flies(ThemeFacts facts) => facts.Policies.PageMotion.Connected is not null;

    /// <summary>Which body a pivot shows and how it got there: tracks the last pivot and body key a host rendered, and answers the step's enter leg
    /// when the body changed under the same pivot (a step in or out).</summary>
    public sealed class DetailMotion
    {
        LocalPivot _pivot;
        string _key = "";
        bool _seen;

        /// <summary>The body key for <paramref name="pivot"/> (its list, or the open detail) and the enter leg of this render (null: no step).</summary>
        public (string Key, LayoutTransition? Enter) Step(string bodyKey, LocalPivot pivot, LocalOpened? opened, ThemeFacts facts)
        {
            bool detail = opened?.IsOpenOver(pivot) == true;
            string key = bodyKey + ":" + pivot.ToString() + (detail ? ":open:" + opened!.ViewKey : ":list");
            LayoutTransition? enter = null;
            if (_seen && pivot == _pivot && key != _key && opened is { } o) enter = Drill(o.Back, o.Flown, facts);
            _seen = true;
            _pivot = pivot;
            _key = key;
            return (key, enter);
        }

        /// <summary>The leg a body leaves with: a list by drilling in, a detail by stepping back (both quiet where the structure flies).</summary>
        public static LayoutTransition? Exit(LocalPivot pivot, LocalOpened? opened, ThemeFacts facts)
            => Drill(back: opened?.IsOpenOver(pivot) == true, Flies(facts), facts);
    }

    // ── Insights (S10) ──────────────────────────────────────────────────────────────────────────────────────────────

    public static Element Insights(LocalCollectionView v, CommonLocalCollectionLayout lay, float g, string routeKey)
    {
        var ins = v.Insights;
        var cards = new List<Element>(6);
        if (ins is not null)
        {
            cards.Add(Card("formats", Loc.Get(P.InsightsFormats), [Bars(ins.Formats), Meta(P.Lossless(v.LosslessPct.ToString(CultureInfo.CurrentCulture)))]));
            if (ins.Bitrates.Count > 0) cards.Add(Card("bitrates", Loc.Get(P.InsightsBitrates), [Bars(ins.Bitrates)]));
            int pct = ins.AlbumsKnownCover > 0 ? (int)Math.Round(100.0 * ins.AlbumsWithCover / ins.AlbumsKnownCover) : 0;
            cards.Add(Card("tags", Loc.Get(P.InsightsTags),
            [
                Meta(P.InsightsCovers(pct.ToString(CultureInfo.CurrentCulture))),
                Stat(Loc.Get(P.InsightsUntagged), ins.Untagged), Stat(Loc.Get(P.InsightsNoAlbumArtist), ins.NoAlbumArtist),
                Stat(Loc.Get(P.InsightsNoTrack), ins.NoTrackNumber), Stat(Loc.Get(P.InsightsDuplicates), ins.Duplicates),
            ]));
            var cant = new List<Element>(ins.Unplayable.Count + 2);
            foreach (var u in ins.Unplayable) cant.Add(Stat(u.Label, u.Count));
            if (ins.Missing > 0) cant.Add(Stat(Loc.Get(P.InsightsMissing), ins.Missing));
            if (ins.CloudOnly > 0) cant.Add(Stat(Loc.Get(P.InsightsCloud), ins.CloudOnly));
            if (cant.Count == 0) cant.Add(Meta(Loc.Get(P.InsightsNothing)));
            cards.Add(Card("cantplay", Loc.Get(P.InsightsCantPlay), cant.ToArray()));
            var folders = new List<Element>(ins.Folders.Count + 1);
            foreach (var f in ins.Folders) folders.Add(Stat(f.Label, f.Songs));
            folders.Add(Meta(P.InsightsSize(Bytes(v.Bytes))));
            cards.Add(Card("folders", Loc.Get(P.InsightsFolders), folders.ToArray()));
        }
        Element column = Gut("lc-insights-col", g, new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = L.InsightGap,
            Children =
            [
                new BoxEl { Direction = 0, Wrap = true, Gap = L.InsightGap, MinWidth = 0f, Children = cards.ToArray() },
                Meta(Loc.Get(P.InsightsPrivate)) with { Key = "lc-private", Wrap = TextWrap.Wrap },
            ],
        }, top: L.SectionTop, bottom: L.PageBottom);
        return ScrollView(column) with { Key = L.InsightsKey, Grow = 1f, MinWidth = 0f, ScrollKey = routeKey + ":insights" };
    }

    static Element Card(string id, string title, Element[] body) => new BoxEl
    {
        Key = "lc-insight:" + id, Direction = 1, Gap = L.HeadGap, Padding = Edges4.All(L.InsightPad), Grow = 1f, Basis = L.InsightColumnMin, MinWidth = 0f,
        Corners = R.Shape.All(ShapeRole.Card), Fill = Tok.FillCardDefault, BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeCardDefault,
        Children = [Heading(title), .. body],
    };

    static Element Stat(string label, int count) => new BoxEl
    {
        Direction = 0, Height = L.StatRowH, AlignItems = FlexAlign.Center, Gap = L.ThumbGap, MinWidth = 0f,
        Children = [Ell(R.Type.DenseTitle(label) with { Color = Tok.TextPrimary }), Spacer(), Meta(count.ToString("N0", CultureInfo.CurrentCulture))],
    };

    /// <summary>A share chart: one bar split by count, then one line per share.</summary>
    static Element Bars(IReadOnlyList<LocalShare> shares)
    {
        int total = 0;
        foreach (var s in shares) total += s.Count;
        if (total == 0) return Meta(Loc.Get(P.InsightsNothing));
        ColorF[] inks = [Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.TextTertiary, Tok.FillControlStrong];
        var segs = new Element[shares.Count];
        var rows = new Element[shares.Count];
        for (int i = 0; i < shares.Count; i++)
        {
            var ink = inks[Math.Min(i, inks.Length - 1)];
            segs[i] = new BoxEl { Grow = shares[i].Count, Basis = 0f, Fill = ink };
            rows[i] = new BoxEl
            {
                Direction = 0, Height = L.StatRowH, Gap = L.ChipInner, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = L.Dot, Height = L.Dot, Corners = CornerRadius4.All(L.Dot / 2f), Fill = ink },
                    Ell(R.Type.DenseTitle(shares[i].Label) with { Color = Tok.TextPrimary }), Spacer(),
                    Meta(shares[i].Count.ToString("N0", CultureInfo.CurrentCulture) + " · " + (100 * shares[i].Count / total).ToString(CultureInfo.CurrentCulture) + "%"),
                ],
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = L.ChipInner, MinWidth = 0f,
            Children = [new BoxEl { Direction = 0, Height = L.ShareBarH, ClipToBounds = true, Corners = CornerRadius4.All(L.ShareBarH / 2f), Children = segs }, .. rows],
        };
    }

    static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : (b / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";
}
