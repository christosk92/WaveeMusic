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
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using L = Wavee.Themes.Common.CommonLocalCollectionRules;
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee.Themes.Common;

/// <summary>The body parts of the collection page (see the file header).</summary>
internal static class CommonLocalCollectionParts
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

    // ── words ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static TextEl Ell(TextEl t) => t with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };

    static TextEl Heading(string s) => R.Type.Heading(s) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary };

    static TextEl Meta(string s) => R.Type.TrackMeta(s) with { Color = Tok.TextSecondary };

    /// <summary>"6,016 songs · 512 albums · 287 artists · 312 hours · 57% lossless", the numbers bold (board 18).</summary>
    public static SpanTextEl StatsLine(LocalCollectionView v) => StatsSpans(v, withSongs: true, R.Type.TrackMeta(""), Tok.TextSecondary);

    /// <summary>The hero's stats line, at body size (board 18: the collection's own numbers right under the title, in bold).</summary>
    public static SpanTextEl HeroStats(LocalCollectionView v) => StatsSpans(v, withSongs: true, R.Type.TrackTitle(""), Tok.TextPrimary);

    /// <summary>The stats as one span paragraph on one line in <paramref name="role"/>'s face, each count in the semibold weight: the
    /// localised phrase is kept whole and its number (as the culture writes it) is found in it.</summary>
    public static SpanTextEl StatsSpans(LocalCollectionView v, bool withSongs, TextEl role, ColorF color)
    {
        var spans = new List<TextSpan>(16);
        if (withSongs) Part(spans, P.Songs(v.Songs), v.Songs);
        Part(spans, P.Albums(v.AlbumCount), v.AlbumCount);
        Part(spans, P.Artists(v.ArtistCount), v.ArtistCount);
        long minutes = v.DurationMs / 60_000;
        Part(spans, Length(v.DurationMs), (int)(minutes >= 60 ? minutes / 60 : minutes));
        if (v.Songs > 0) Part(spans, P.Lossless(v.LosslessPct.ToString(CultureInfo.CurrentCulture)), v.LosslessPct);
        return new SpanTextEl(spans.ToArray())
        {
            Size = role.Size, Weight = role.Weight, FontFamily = role.FontFamily, LineHeight = role.LineHeight, Color = color,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };

        static void Part(List<TextSpan> spans, string text, int number)
        {
            if (spans.Count > 0) spans.Add(new TextSpan(" · "));
            string n = number.ToString("N0", CultureInfo.CurrentCulture);
            int at = text.IndexOf(n, StringComparison.Ordinal);
            if (at < 0) { n = number.ToString(CultureInfo.InvariantCulture); at = text.IndexOf(n, StringComparison.Ordinal); }
            if (at < 0) { spans.Add(new TextSpan(text)); return; }
            if (at > 0) spans.Add(new TextSpan(text[..at]));
            spans.Add(new TextSpan(n, Weight: L.StatsNumberWeight));
            if (at + n.Length < text.Length) spans.Add(new TextSpan(text[(at + n.Length)..]));
        }
    }

    public static string StatsText(LocalCollectionView v, bool withSongs)
    {
        var parts = new List<string>(5);
        if (withSongs) parts.Add(P.Songs(v.Songs));
        parts.Add(P.Albums(v.AlbumCount));
        parts.Add(P.Artists(v.ArtistCount));
        parts.Add(Length(v.DurationMs));
        if (v.Songs > 0) parts.Add(P.Lossless(v.LosslessPct.ToString(CultureInfo.CurrentCulture)));
        return string.Join(" · ", parts);
    }

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

    /// <summary>The library's activity in one line: reading (with the count), or up to date with when the last scan ended ("Up to date ·
    /// scanned 2 min ago"; S1 and the page agree). Bind it with <see cref="ActivityProp"/> so the "ago" stays true.</summary>
    public static string ActivityText(LocalCollectionView v) => v.Phase switch
    {
        LocalLibraryPhase.Scanning => P.Reading(v.Songs),
        LocalLibraryPhase.Offline => Loc.Get(P.AllOffline),
        _ => v.LastScanTicks > 0 ? P.UpToDateScanned(LibraryActivity.Ago(v.LastScanTicks)) : Loc.Get(P.UpToDate),
    };

    /// <summary><see cref="ActivityText"/> as a bound text that re-reads every 30 s (<see cref="LibraryActivity.Minute"/>): only this text node relays out.</summary>
    public static Prop<string> ActivityProp(LocalCollectionView v) => Prop.Of(() => { _ = LibraryActivity.Minute.Value; return ActivityText(v); });

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

    /// <summary>The small mosaic of the compact head: four covers, or the calm tile with the music glyph.</summary>
    public static Element Thumb(LocalCollectionView v, float size)
    {
        if (v.Mosaic.Count == 0)
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), Fill = Tok.AccentSubtle,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Children = [Icon(Icons.MusicNote, L.MusicGlyph, Tok.AccentTextPrimary)],
            };
        float half = size / 2f;
        var cells = new Element[4];
        for (int i = 0; i < 4; i++) cells[i] = new BoxEl { Width = half, Height = half, Children = [Controls.CoverFill(v.Mosaic[i % v.Mosaic.Count], 0f, L.MosaicDecode)] };
        return new BoxEl { Width = size, Height = size, Shrink = 0f, Wrap = true, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover), Children = cells };
    }

    // ── folder chips and the activity (S1 in the hero) ─────────────────────────────────────────────────────────────

    /// <param name="scanLine">A first scan's line and bar (the shared `LocalFiles.Scan` view) in place of the activity line, or null.</param>
    public static Element Chips(LocalCollectionView v, LocalCollectionVerbs verbs, float chipH, Element? scanLine)
    {
        var kids = new List<Element>(v.Chips.Count + 3);
        foreach (var c in v.Chips) kids.Add(Chip(c, verbs.AddMusic, chipH));
        kids.Add(new BoxEl
        {
            Key = "lc-chip:add", Direction = 0, Height = chipH, Layout = L.SizeReflow, Gap = L.ChipInner, AlignItems = FlexAlign.Center, Padding = new Edges4(L.ChipPadX, 0f, L.ChipPadX, 0f),
            Corners = CornerRadius4.All(chipH / 2f), BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeControlDefault, OnClick = verbs.AddMusic, Cursor = CursorId.Hand,
            Children = [Icon(Icons.Add, L.ChipGlyph, Tok.TextSecondary), R.Type.DenseMeta(Loc.Get(P.AddFolder)) with { Color = Tok.TextPrimary }],
        });
        kids.Add(new BoxEl
        {
            Key = "lc-activity", Direction = 0, Gap = L.ChipInner, AlignItems = FlexAlign.Center, Height = chipH, Layout = L.SizeReflow, MinWidth = 0f, Shrink = 1f,
            Margin = new Edges4(L.StatusGap - L.ChipGap, 0f, 0f, 0f),
            Children = scanLine is not null
                ? [scanLine]
                :
                [
                    v.Phase == LocalLibraryPhase.Scanning
                        ? ProgressRing.Create(isActive: true, size: L.ChipGlyph)
                        : Icon(v.Phase == LocalLibraryPhase.Offline ? Icons.Warning : Icons.Accept, L.ChipGlyph, v.Phase == LocalLibraryPhase.Offline ? Tok.SystemFillAttention : Tok.AccentTextPrimary),
                    Ell(R.Type.DenseMeta("") with { Text = ActivityProp(v), Color = Tok.TextSecondary }),
                ],
        });
        // The row wraps onto a second line where the column is narrow (the activity line "Up to date · scanned 2 min ago" is the long one): a width
        // that re-wraps it (a tier crossing, the nav pane docking) eases the row's height, so the pivot under the hero glides instead of jumping.
        return new BoxEl { Direction = 0, Wrap = true, Gap = L.ChipGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Layout = L.HeightReflow, Children = kids.ToArray() };
    }

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
        Element mark = c.State == LocalChipState.Ok ? Icon(Icons.Check, L.ChipGlyph, Tok.AccentTextPrimary)
            : new BoxEl { Width = L.Dot, Height = L.Dot, Corners = CornerRadius4.All(L.Dot / 2f), Fill = bad ? Tok.SystemFillAttention : Tok.AccentDefault };
        return new BoxEl
        {
            Key = "lc-chip:" + c.Path, Direction = 0, Height = chipH, Layout = L.SizeReflow, Gap = L.ChipInner, AlignItems = FlexAlign.Center, Padding = new Edges4(L.ChipPadX, 0f, L.ChipPadX, 0f),
            Corners = CornerRadius4.All(chipH / 2f), BorderWidth = L.ChipBorder, BorderColor = bad ? Tok.SystemFillAttention : Tok.StrokeControlDefault,
            Fill = Tok.FillControlDefault, OnClick = open, Cursor = CursorId.Hand, MinWidth = 0f, Shrink = 1f,
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
    static Element Art(string? cover, float size, string seed, string name, bool round = false)
    {
        var corners = round ? CornerRadius4.All(size / 2f) : R.Shape.All(ShapeRole.Cover);
        if (cover is not null)
            return new BoxEl { Width = size, Height = size, Shrink = 0f, ClipToBounds = true, Corners = corners, Layout = L.SizeReflow, Children = [Controls.CoverFill(cover, 0f, L.CardDecode)] };
        var (top, bottom) = L.TintOf(seed);
        string initial = InitialOf(name);
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, Corners = corners, ClipToBounds = true, Layout = L.SizeReflow,
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
        var cover = new List<Element>(4) { Art(a.Cover, w, a.Key, a.Title) };
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
                    Children = [Ell(R.Type.CardTitle(a.Name) with { Color = Tok.TextPrimary }), Ell(Meta(P.Albums(a.Albums) + " · " + P.Songs(a.Tracks)))],
                },
            ],
        };
    }

    // ── Home (board 18) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Home's shelves, in the user's order (<paramref name="shelves"/>; "Edit home"), the hidden ones left out.</summary>
    public static Element[] HomeBody(LocalCollectionView v, LocalCollectionVerbs verbs, Action<LocalPivot> setPivot, CommonLocalCollectionLayout lay, float g,
                                     IReadOnlyList<LocalShelfSlot> shelves)
    {
        var kids = new List<Element>(3);
        Action seeAll = () => setPivot(LocalPivot.Albums);
        int shown = 0;
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
                Children = shelf is null ? [] : [new BoxEl { Key = "lc-home-reveal:" + id, Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = L.ShelfReveal, Children = [shelf] }],
            });
        }
        if (shown == 0 && v.Songs > 0)
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
                var cards = new Element[grp.Albums.Count];
                for (int i = 0; i < cards.Length; i++) cards[i] = AlbumCard(grp.Albums[i], lay.Card, verbs, pill: false, year: false);
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
            return Section("recent", Loc.Get(P.RecentlyAdded), v.RecentThisMonth > 0 ? P.ThisMonth(v.RecentThisMonth) : null, seeAll,
                ShelfRow("recent", groups.ToArray(), L.GroupGap, g), g, lay.ChipH);
        }

        Element? LosslessShelf()
        {
            if (v.Lossless.Count == 0) return null;
            var cards = new Element[v.Lossless.Count];
            for (int i = 0; i < cards.Length; i++) cards[i] = AlbumCard(v.Lossless[i], lay.Card, verbs, pill: true, year: false);
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
                    Children = [Ell(R.Type.CardTitle(P.Decade(decade.ToString(CultureInfo.InvariantCulture))) with { Color = Tok.TextPrimary }), Ell(Meta(P.Albums(d.Albums)))],
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

    public static Element FolderBrowser(LocalCollectionView v, string folder, Action<string> open, LocalCollectionVerbs verbs, CommonLocalCollectionLayout lay,
        float g, string title, string routeKey)
    {
        LocalFolderNode? node = folder.Length > 0 && v.Folders.TryGetValue(folder, out var n) ? n : null;
        IReadOnlyList<string> childKeys = node?.Children ?? v.RootKeys;
        var children = new List<LocalFolderNode>(childKeys.Count);
        foreach (string k in childKeys) if (v.Folders.TryGetValue(k, out var c)) children.Add(c);
        var files = node?.Files ?? [];

        // The breadcrumb: On this PC › the music folder › … › this folder.
        var trail = new List<(string Label, string Key)> { (title, "") };
        var chain = new List<LocalFolderNode>();
        for (var at = node; at is not null; at = at.ParentKey is { } pk && v.Folders.TryGetValue(pk, out var up) ? up : null) chain.Add(at);
        for (int i = chain.Count - 1; i >= 0; i--) trail.Add((chain[i].Name, chain[i].Key));
        var labels = new string[trail.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = trail[i].Label;
        Element crumbs = BreadcrumbBar.Create(labels, i => { if ((uint)i < (uint)trail.Count) open(trail[i].Key); }, size: ControlSize.Large);

        int songs = node?.Songs ?? v.Songs;
        long length = node?.DurationMs ?? v.DurationMs;
        string meta = P.Folders(children.Count) + " · " + P.Songs(songs) + " · " + Length(length);
        var headKids = new List<Element>(5)
        {
            new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, Children = [crumbs] },
            Ell(Meta(meta)),
            Spacer(),
        };
        if (node is not null)
        {
            string key = node.Key;
            headKids.Add(Button.Create(Loc.Get(P.PlayFolder), () => verbs.PlayFolder(key), ButtonAppearance.Accent, glyph: Icons.Play, isEnabled: songs > 0));
            headKids.Add(Button.Create(Loc.Get(P.Shuffle), () => verbs.ShuffleFolder(key), ButtonAppearance.Standard, glyph: Icons.Shuffle, isEnabled: songs > 0));
        }
        Element head = Gut("lc-folder-head", g, new BoxEl
        {
            Key = "lc-folder-head", Direction = 0, Gap = L.ThumbGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f,

            Children = headKids.ToArray(),
        }, top: L.CompactTop, bottom: L.CompactTop);
        Element songsHead = Gut("lc-songs-head", g, new BoxEl
        {
            Direction = 0, Gap = L.ThumbGap, AlignItems = FlexAlign.Center, Height = L.SortRowH, MinWidth = 0f,
            Children = [Heading(Loc.Get(P.SongsHere)), Ell(Meta(P.Songs(files.Count) + " · " + Length(node?.DurationHereMs ?? 0)))],
        });
        return Measured(L.FoldersKey + ":" + v.Version + ":" + (folder.Length == 0 ? "top" : folder), w =>
        {
            var (cols, cell) = Columns(w, g, lay.WallMin, lay.CardGap);
            int tileRows = (children.Count + cols - 1) / cols;
            float tileH = cell + L.CardTextGap + L.CardTextH + lay.CardGap;
            int songsAt = 1 + tileRows;
            int rows = songsAt + (files.Count > 0 ? 1 + files.Count : 0);
            return (rows,
                i => i == 0 ? L.SortRowH + 2f * L.CompactTop : i < songsAt ? tileH : i == songsAt ? L.SortRowH : L.SongRowH,
                i => i == 0 ? head
                    : i < songsAt ? CardRow((i - 1) * cols, children.Count, cols, lay.CardGap, g, k => FolderTile(children[k], cell, open, verbs))
                    : i == songsAt ? songsHead
                    : SongRow(files[i - songsAt - 1], g, lay.FileColumn, verbs));
        }, routeKey + ":folders:" + folder);
    }

    static Element FolderTile(LocalFolderNode f, float w, Action<string> open, LocalCollectionVerbs verbs)
    {
        string key = f.Key;
        Element art;
        if (f.Cover is not null) art = Art(f.Cover, w, f.Key, f.Name);
        else if (f.Mosaic.Count >= 2)
        {
            float half = w / 2f;
            var cells = new Element[4];
            for (int i = 0; i < 4; i++) cells[i] = new BoxEl { Width = half, Height = half, Children = [Controls.CoverFill(f.Mosaic[i % f.Mosaic.Count], 0f, L.MosaicDecode)] };
            art = new BoxEl { Width = w, Height = w, Wrap = true, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover), Children = cells };
        }
        else if (f.Mosaic.Count == 1) art = Art(f.Mosaic[0], w, f.Key, f.Name);
        else
            art = new BoxEl
            {
                Width = w, Height = w, Corners = R.Shape.All(ShapeRole.Cover), Fill = Design.WatchedPlaceholder(null), BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeCardDefault,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Children = [Icon(Icons.Folder, L.FolderGlyph, Tok.TextTertiary)],
            };
        var meta = new List<Element>(2) { Ell(Meta(P.Songs(f.Songs) + " · " + Length(f.DurationMs))) };
        if (f.Quality.Length > 0)
            meta.Add(new BoxEl
            {
                Height = L.PillH, Padding = new Edges4(L.PillPadX, 0f, L.PillPadX, 0f), AlignItems = FlexAlign.Center, Shrink = 0f,
                Corners = R.Shape.All(ShapeRole.Control), BorderWidth = L.ChipBorder, BorderColor = Tok.StrokeControlDefault,
                Children = [R.Type.MicroMeta(f.Quality) with { Color = Tok.TextSecondary }],
            });
        return new BoxEl
        {
            Key = "lc-folder:" + key, Direction = 1, Width = w, Shrink = 0f, Gap = L.CardTextGap, Cursor = CursorId.Hand, OnClick = () => open(key), Layout = L.SizeReflow,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, Width = w, Height = w, Layout = L.SizeReflow,
                    Children =
                    [
                        art,
                        Corner(new BoxEl
                        {
                            Width = L.ChipH, Height = L.ChipH, Shrink = 0f,
                            Corners = R.Shape.All(ShapeRole.Control), Fill = Tok.FillSolidBase with { A = 0.82f }, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                            Children = [Icon(Icons.Folder, L.ChipGlyph, Tok.TextPrimary)],
                        }, bottom: true, end: false, L.PillInset),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Height = L.CardTextH, MinWidth = 0f,
                    Children = [Ell(R.Type.CardTitle(f.Name) with { Color = Tok.TextPrimary }), new BoxEl { Direction = 0, Gap = L.ChipInner, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = meta.ToArray() }],
                },
            ],
        };
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
