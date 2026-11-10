// ── Themes/Classic/Structure/Views/Panel/ClassicNowPlayingHero.cs — Classic's `Player.NowPlayingHero` (themes plan 6.1, T6b) ──
//
// From `Shell/Rail.UI.cs`: the pinned hero tile, its header strip, the art menu, and the now-playing body under it.
//
// THE HERO TILE (the player style redesign, 2026-11-08: canvas-design `Classic.dc.html`, `Styles.dc.html`). A header strip (the "Now
// playing" eyebrow and the style CHIP, a hairline pill naming the player style) over the STAGE (`props.Stage`, the core's: the cover, the
// canvas or a deck). The chip opens the in-place picker: the header reads "Player style" with Cancel and Done, the stage becomes the carousel
// and the strip (the arrows, the style's name and line, the finish swatches, the dots) unfolds under it. No pivot, no gear, no flyout, no
// option dial. The stage is hosted, never built here, so a mode switch or a theme switch re-mounts nothing a deck owns.
//
// IN THE COMPACT SHEET (`ClassicPanel.SheetArtSide` provided): the tile is the stage alone, centred at the side the window leaves it (no header
// strip: the sheet's head carries its own header, `ClassicSheetHeader`, which seats the chip, and the picker's Cancel and Done while picking);
// the identity, the transport and the rest are the sheet's own column (`ClassicNowPlayingSheet`).
//
// THE BODY (scrolls under the pinned hero) is the panel's now-playing arm: title · artist links · album link · heart, the lyrics
// peek (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), then About the artist, the cities, merch and Next up
// (the `Panel.NextUp` view). It subscribes the entity tables through `props.SubscribeEntities` and
// asks for the playing track's demand (the rail is not a page and never mounts on a route).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's now-playing hero and body.</summary>
internal static class ClassicNowPlayingHero
{
    /// <summary>The registered template of <see cref="ViewKeys.NowPlayingHero"/>: the pinned hero tile.</summary>
    public static Element Template(NowPlayingHeroProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicHeroTile());

    // ── the body: title · artists · album · heart, then the lyrics peek ──────────────────────────────────────────────

    /// <summary>Title · artist links · album link · heart, then the lyrics peek (the lyrics lane's, keyed per track).</summary>
    /// <summary>The playing track's artists as one line of links (each opens its artist), or null with no credited artist.</summary>
    internal static Element? ArtistLinks(NowPlayingHeroProps props, Track track, float size, float line)
    {
        var artists = track.ArtistSlots;
        if (artists.Length == 0) return null;
        var spans = new TextSpan[artists.Length * 2 - 1];
        for (int i = 0, n = 0; i < artists.Length; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = new Artist(artists[i]);
            string name = a.Name;
            var artistUri = a.Uri;
            spans[n++] = new TextSpan(name, OnClick: () => props.GoTo(artistUri, name));
        }
        return new SpanTextEl(spans)
        {
            Size = size, LineHeight = line, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    internal static Element HeroMeta(NowPlayingHeroProps props, Track track)
    {
        if (props.Bleeds()) return BleedMeta(props, track);
        string uri = track.Uri.Text;
        string title = track.Title;
        var meta = new List<Element>(2);
        if (ArtistLinks(props, track, ClassicHeroLayout.ArtistSize, ClassicHeroLayout.ArtistLine) is { } artistLine) meta.Add(artistLine);
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            var albumUri = album.Uri;
            meta.Add(new SpanTextEl([new TextSpan(albumTitle, OnClick: () => props.GoTo(albumUri, albumTitle))])
            {
                Size = ClassicHeroLayout.AlbumSize, LineHeight = ClassicHeroLayout.AlbumLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.L),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicHeroLayout.TitleColumnGap,
                            Children =
                            [
                                Ui.Subtitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis },
                                new BoxEl { Direction = 1, Gap = ClassicHeroLayout.MetaLinesGap, Children = meta.ToArray() },
                            ],
                        },
                        Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = ClassicHeroLayout.SaveGlyph, Box = ClassicHeroLayout.SaveBox }) with { Key = "save:" + uri },
                    ],
                },
                props.LyricsPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }

    /// <summary>The meta under the canvas's fade, on the plain panel: the 64 cover, the title,
    /// the artist in the art's hue (solved as text on the ground) and "album · year", the saved heart in that hue on a soft circle of it; the
    /// lyrics peek under them.</summary>
    static Element BleedMeta(NowPlayingHeroProps props, Track track)
    {
        string uri = track.Uri.Text, title = track.Title;
        var ink = ClassicBleedInk.Of(props.NowArtUrl());
        var lines = new List<Element>(3)
        {
            new TextEl(title)
            {
                Size = ClassicHeroLayout.BleedTitleSize, LineHeight = ClassicHeroLayout.BleedTitleLine, Weight = ClassicHeroLayout.BleedTitleWeight,
                Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (ArtistLinks(props, track, ClassicHeroLayout.ArtistSize, ClassicHeroLayout.ArtistLine) is SpanTextEl artists) lines.Add(artists with { Color = ink.Text });
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            var albumUri = album.Uri;
            string line = album.Year > 0 ? albumTitle + " · " + album.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : albumTitle;
            lines.Add(new SpanTextEl([new TextSpan(line, OnClick: () => props.GoTo(albumUri, albumTitle))])
            {
                Size = ClassicHeroLayout.AlbumSize, LineHeight = ClassicHeroLayout.AlbumLine, Color = Tok.TextPrimary with { A = ClassicHeroLayout.BleedYearA },
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }
        ColorF accent = ink.Text;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.L),
            Children =
            [
                new BoxEl
                {
                    Key = "npv-meta", Direction = 0, Gap = ClassicHeroLayout.BleedMetaGap, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = ClassicHeroLayout.BleedCover, Height = ClassicHeroLayout.BleedCover, Shrink = 0f,
                            Corners = CornerRadius4.All(ClassicHeroLayout.BleedCoverRadius),
                            Children =
                            [
                                new ImageEl
                                {
                                    Source = props.NowArtUrl() ?? "", Fit = ImageFit.Cover, DecodePx = ClassicHeroLayout.CoverDecodePx,
                                    Width = ClassicHeroLayout.BleedCover, Height = ClassicHeroLayout.BleedCover, Corners = CornerRadius4.All(ClassicHeroLayout.BleedCoverRadius),
                                },
                            ],
                        },
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicHeroLayout.BleedLinesGap, Children = lines.ToArray() },
                        new BoxEl
                        {
                            Width = ClassicHeroLayout.BleedHeart, Height = ClassicHeroLayout.BleedHeart, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                            Justify = FlexJustify.Center, Corners = Radii.Circle(ClassicHeroLayout.BleedHeart), Fill = accent with { A = ClassicHeroLayout.BleedHeartA },
                            Children =
                            [
                                Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = ClassicHeroLayout.SaveGlyph, Box = ClassicHeroLayout.BleedHeart, Accent = () => accent })
                                    with { Key = "save:" + uri },
                            ],
                        },
                    ],
                },
                props.LyricsPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }

    internal static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S,
        Children = [R.Type.RailHeader(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, body],
    };

    internal static Element AboutArtist(Artist artist)
        => Section(Loc.Get(Strings.Detail.AboutTheArtist),
            Controls.ArtistAboutCard(artist, AboutLayout.Rail) with { Key = "about:" + artist.Uri.Text });

    /// <summary>At most 5 cities; the longest bar is 260 and the shortest never below 8 % of it.</summary>
    internal static Element CitiesSection(NowPlayingHeroProps props, ReadOnlySpan<CityEdge> cities)
    {
        uint max = 1;
        int n = Math.Min(ClassicHeroLayout.CityRows, cities.Length);
        for (int i = 0; i < n; i++) max = Math.Max(max, cities[i].Listeners);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = cities[i];
            string city = props.ResolveString(c.City), country = props.ResolveString(c.Country);
            float frac = Math.Clamp(c.Listeners / (float)max, ClassicHeroLayout.CityBarMinFraction, 1f);
            rows[i] = new BoxEl
            {
                Direction = 1, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(country.Length > 0 ? city + ", " + country : city)
                                { Size = ClassicHeroLayout.CityTextSize, LineHeight = ClassicHeroLayout.CityTextLine, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(c.Listeners.ToString("N0")) { Size = ClassicHeroLayout.CityTextSize, LineHeight = ClassicHeroLayout.CityTextLine, Color = Tok.TextTertiary },
                        ],
                    },
                    new BoxEl
                    {
                        Height = ClassicHeroLayout.CityBarH, Corners = CornerRadius4.All(ClassicHeroLayout.CityBarRadius), Fill = Tok.FillSubtleSecondary, ClipToBounds = true,
                        Children = [new BoxEl { Width = ClassicHeroLayout.CityBarMax * frac, Height = ClassicHeroLayout.CityBarH, Corners = CornerRadius4.All(ClassicHeroLayout.CityBarRadius), Fill = Tok.AccentDefault }],
                    },
                ],
            };
        }
        return Section(Loc.Get(Strings.Artist.ListenedMostIn), new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows });
    }

    internal static Element MerchSection(NowPlayingHeroProps props, ReadOnlySpan<int> slots)
    {
        int n = Math.Min(ClassicHeroLayout.MerchRows, slots.Length);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            Merch m = Album.MerchAt(slots[i]);
            string name = props.ResolveString(m.Name), price = props.ResolveString(m.Price);
            string shop = props.ResolveString(m.ShopUrl);
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.S),
                Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                HoverFill = Tok.FillCardDefault,                          // an inert row keeps its hover plate
                Cursor = shop.Length > 0 ? CursorId.Hand : (CursorId?)null,
                OnClick = shop.Length > 0 ? () => InputHooks.Current.Default.OpenUri?.Invoke(shop) : null,
                Children =
                [
                    Controls.Artwork(Controls.ArtUrl(m.ImageId), ClassicHeroLayout.MerchArt, ClassicHeroLayout.MerchArt, Radii.Control),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicHeroLayout.MerchTextGap,
                        Children =
                        [
                            new TextEl(name) { Size = ClassicHeroLayout.MerchNameSize, LineHeight = ClassicHeroLayout.MerchNameLine, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(price.Length > 0 ? price : Loc.Get(Strings.Artist.Buy)) { Size = ClassicHeroLayout.MerchPriceSize, LineHeight = ClassicHeroLayout.MerchPriceLine, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
        }
        return Section(Loc.Get(Strings.Artist.Merch), new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows });
    }

    internal static Element LoadingSection() => Section(Loc.Get(Strings.Detail.AboutTheArtist), new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
        Children =
        [
            new BoxEl { Height = ClassicHeroLayout.LoadingTitleH, Width = ClassicHeroLayout.LoadingTitleW, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = ClassicHeroLayout.LoadingLineH, Width = ClassicHeroLayout.LoadingLineAW, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = ClassicHeroLayout.LoadingLineH, Width = ClassicHeroLayout.LoadingLineBW, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
        ],
    });
}

//// <summary>The hero tile: the header strip over the stage (the player style, or the picker's carousel) and, while picking, the picker's strip.
/// A component of the theme (it owns the bound cover thunks, built once per instance so a re-render does not re-register them).</summary>
internal sealed class ClassicHeroTile : Component
{
    Func<string>? _source;
    Func<ColorF>? _placeholder;
    Func<Element>? _cover;
    Func<float, float>? _bleedRoom;

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var track = props.NowTrack();
        bool open = props.Picking();
        int focus = props.ShownStyle();
        var sheetSide = UseContext(ClassicPanel.SheetArtSide);
        if (!track.IsValid) return new BoxEl();                        // nothing playing: no square, no strip

        _source ??= () => props.NowArtUrl() ?? "";
        _placeholder ??= () => ClassicHeroWash.Of(props.NowArtUrl());
        _cover ??= () => new ImageEl
        {
            Key = "cover",
            Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = ClassicHeroLayout.CoverDecodePx,
            Corners = Radii.CardAll, AlignSelf = FlexAlign.Stretch,
            Placeholder = Prop.Of(_placeholder),
        };
        var verbs = props.Style;
        // The strip under the stage while picking: a disclosure that mounts to open and unmounts to close (its height reveals, the body rides it).
        var strip = open
            ? new BoxEl { Key = "npv-strip", Direction = 1, Padding = ClassicStylePickerLayout.StripPadding, Animate = Design.Reduced ? null : Design.Reveal.Drawer, Children = [ClassicStylePicker.Strip(verbs, focus, props.NowArtUrl)] }
            : null;
        if (sheetSide is not null) return SheetTile(props, sheetSide, strip);

        // Over the edge-to-edge canvas the stage holds the room that puts the meta (in the body under this pinned hero) 24 under the fade's end.
        _bleedRoom ??= panelW =>
        {
            float vh = MathF.Round(panelW * ClassicHeroLayout.BleedAspect), ph = props.RailHeight.Value;
            return ClassicHeroLayout.BleedEnd(vh, ph) + ClassicHeroLayout.BleedMetaDrop - Rail.Hero.ArtTop - Spacing.M;
        };
        var stage = new BoxEl { Direction = 1, Shrink = 0f, Children = [props.Stage(2f * Rail.Hero.Inset, _cover, Radii.CardAll, _bleedRoom)] };
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Rail.Hero.Inset,
            Padding = new Edges4(Rail.Hero.Inset, Rail.Hero.Inset, Rail.Hero.Inset, 0f),
            Children = strip is null
                ? [Embed.Comp(props, static () => new ClassicHeroHeaderRow()), stage]
                : [Embed.Comp(props, static () => new ClassicHeroHeaderRow()), stage, strip],
        };
    }

    /// <summary>The tile inside the Compact sheet: the stage alone, centred, <paramref name="sheetSide"/> wide (bound: a resize re-fits it without a
    /// render); the stage measures that column and draws its style at it. The sheet's own header seats the chip (<see cref="ClassicSheetHeader"/>).</summary>
    Element SheetTile(NowPlayingHeroProps props, Func<float> sheetSide, Element? strip)
    {
        var heroBox = new BoxEl
        {
            Direction = 1, Shrink = 0f, Width = Prop.Of(sheetSide),
            Children = strip is null
                ? [props.Stage(0f, _cover!, Radii.CardAll)]
                : [props.Stage(0f, _cover!, Radii.CardAll), strip],
        };
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, ClassicPanelTiers.SheetArtTop, 0f, 0f),
            Children = [heroBox],
        };
    }
}

/// <summary>The header strip: the "Now playing" eyebrow and the style chip; while picking, "Player style" with Cancel and Done (Escape is Cancel,
/// the stage's). A fixed 36 row; the two faces cross-fade.</summary>
internal sealed class ClassicHeroHeaderRow : Component
{
    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        bool open = props.Picking();
        int shown = props.ShownStyle();
        bool bleeds = !open && props.Bleeds();
        var verbs = props.Style;
        Element body = open
            ? new BoxEl
            {
                Direction = 0, Height = Rail.Hero.HeaderRowH, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children = [Eyebrow(Loc.Get(Strings.Player.PlayerStyle)), .. ClassicStylePicker.Commit(verbs)],
            }
            : bleeds ? BleedFace(props.Source(), verbs, shown)
            : new BoxEl
            {
                Direction = 0, Height = Rail.Hero.HeaderRowH, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children = [Eyebrow(Loc.Get(Strings.Player.NowPlaying)), ClassicStylePicker.Chip(verbs, shown)],
            };
        return new BoxEl
        {
            Height = Rail.Hero.HeaderRowH, Shrink = 0f, ZStack = true,
            Children = [ClassicStylePicker.Layer(open ? "head:pick" : bleeds ? "head:bleed" : "head:rest", body)],
        };
    }

    /// <summary>The header over the canvas: "Playing from album" over the album's name, and the chip on a light plate, in the media
    /// scope's ink (Deck.Bleed.cs).</summary>
    static Element BleedFace(PanelSourceFacts source, IPlayerStyleVerbs verbs, int shown)
    {
        var ink = new CanvasInk(true);
        var words = new List<Element>(2);
        if (source.HasSource)
        {
            words.Add(R.Type.Eyebrow(source.PlayingFrom) with
            {
                Size = ClassicHeroLayout.BleedEyebrowSize, LineHeight = ClassicHeroLayout.BleedEyebrowLine, Color = ink.Primary with { A = ClassicHeroLayout.BleedEyebrowA },
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
            words.Add(new TextEl(source.Name)
            {
                Size = ClassicHeroLayout.BleedSourceSize, LineHeight = ClassicHeroLayout.BleedSourceLine, Weight = ClassicHeroLayout.BleedSourceWeight,
                Color = ink.Primary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }
        return new BoxEl
        {
            Direction = 0, Height = Rail.Hero.HeaderRowH, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = words.ToArray() },
                ClassicStylePicker.Chip(verbs, shown, frosted: true),
            ],
        };
    }

    // Sentence case, never .ToUpper() on a localized string.
    static Element Eyebrow(string text) => R.Type.Eyebrow(text) with
    {
        Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };
}

/// <summary>Classic's style chip and the picker's parts (pills: the chip, the arrows, the swatches and the dots).</summary>
internal static class ClassicStylePicker
{
    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    /// <summary>One of two cross-fading layers (the header's rest and picking faces).</summary>
    internal static BoxEl Layer(string key, Element body) => new()
    {
        Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch,
        Enter = Fade, Exit = Fade, Transition = MotionTok.ControlFast, Children = [body],
    };

    /// <summary>Cancel and Done.</summary>
    internal static Element[] Commit(IPlayerStyleVerbs verbs) =>
    [
        Button.Subtle(Loc.Get(Strings.Player.Picker.Cancel), verbs.Cancel) with { Key = ClassicStylePickerLayout.CancelKey, Shrink = 0f },
        Button.Accent(Loc.Get(Strings.Player.Picker.Done), verbs.Done) with { Key = ClassicStylePickerLayout.DoneKey, Shrink = 0f },
    ];

    /// <summary>The chip: the style's name and a chevron in a hairline pill (<paramref name="frosted"/>: over the canvas, a light plate under a
    /// light hairline).</summary>
    internal static Element Chip(IPlayerStyleVerbs verbs, int shown, bool frosted = false)
    {
        // Frosted: over the canvas, a light plate of the media scope's ink (Deck.Bleed.cs), the label in that ink.
        var ink = new CanvasInk(frosted);
        ColorF frost = ink.Primary with { A = ClassicHeroLayout.BleedChipFillA };
        return ToolTip.Wrap(new BoxEl
        {
            Key = ClassicStylePickerLayout.ChipKey, Height = ClassicStylePickerLayout.ChipH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Gap = ClassicStylePickerLayout.ChipGap, Padding = ClassicStylePickerLayout.ChipPadding, Corners = Radii.Circle(ClassicStylePickerLayout.ChipH),
            Fill = frosted ? frost : Tok.FillControlDefault,
            HoverFill = frosted ? frost : Tok.FillControlSecondary,
            PressedFill = frosted ? frost : Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = frosted ? ink.Primary with { A = ClassicHeroLayout.BleedChipStrokeA } : Tok.StrokeControlDefault,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = verbs.OpenPicker,
            Children =
            [
                new TextEl(Loc.Get(Rail.PlayerCatalog.ById(shown).NameKey))
                {
                    Size = ClassicStylePickerLayout.ChipTextSize, LineHeight = ClassicStylePickerLayout.ChipTextLine, Color = ink.Primary,
                    MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                Icon(Icons.ChevronDown, ClassicStylePickerLayout.ChipGlyph, ink.Secondary),
            ],
        }, Loc.Get(Strings.Player.PlayerStyle));
    }

    /// <summary>The strip: the arrows either side of the focused style's name and line, the finish swatches, the dots.</summary>
    internal static Element Strip(IPlayerStyleVerbs verbs, int focus, Func<string?> artUrl)
    {
        var style = Rail.PlayerCatalog.ById(focus);
        var swatches = new Element[style.Finishes.Length];
        if (style.HasFinish)
        {
            int current = Rail.PlayerPrefs.Finish(focus);
            for (int i = 0; i < swatches.Length; i++) swatches[i] = Swatch(verbs, style.Finishes[i], i, i == current, artUrl);
        }
        var dots = new Element[Rail.PlayerCatalog.Count];
        for (int i = 0; i < dots.Length; i++) dots[i] = Dot(i, i == focus);
        return new BoxEl
        {
            Key = ClassicStylePickerLayout.StripKey, Direction = 1, Gap = ClassicStylePickerLayout.StripGap,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicStylePickerLayout.NameGap,
                            Height = ClassicStylePickerLayout.NameBlockH, Justify = FlexJustify.Center,
                            Children =
                            [
                                new TextEl(Loc.Get(style.NameKey))
                                {
                                    Size = ClassicStylePickerLayout.NameSize, LineHeight = ClassicStylePickerLayout.NameLine, Weight = ClassicStylePickerLayout.NameWeight,
                                    Color = Tok.TextPrimary, TextAlign = TextAlign.Center, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                new TextEl(Loc.Get(style.LineKey))
                                {
                                    Size = ClassicStylePickerLayout.LineSize, LineHeight = ClassicStylePickerLayout.LineLine, Color = Tok.TextSecondary,
                                    TextAlign = TextAlign.Center, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                            ],
                        },
                    ],
                },
                // Only a style with finishes has swatches: the row opens to their height and closes to nothing, eased (no jump).
                new BoxEl
                {
                    Height = style.HasFinish ? ClassicStylePickerLayout.SwatchRowH : 0f, ClipToBounds = true, Direction = 0, AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center, Gap = ClassicStylePickerLayout.SwatchGap, Animate = Design.Reduced ? null : ClassicStylePickerLayout.SwatchRowMotion, Children = swatches,
                },
                // The arrows flank the dots, clear of the name and line above.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = ClassicStylePickerLayout.RowGap,
                    Children =
                    [
                        Arrow(ClassicStylePickerLayout.PrevKey, Icons.ChevronLeft, Strings.Player.Picker.Previous, () => verbs.Step(-1)),
                        Dots(verbs, dots),
                        Arrow(ClassicStylePickerLayout.NextKey, Icons.ChevronRight, Strings.Player.Picker.Next, () => verbs.Step(+1)),
                    ],
                },
            ],
        };
    }

    static Element Arrow(string key, string glyph, string labelKey, Action step) => ToolTip.Wrap(new BoxEl
    {
        Key = key, Width = ClassicStylePickerLayout.ArrowBox, Height = ClassicStylePickerLayout.ArrowBox, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(ClassicStylePickerLayout.ArrowBox),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = step,
        Children = [Icon(glyph, ClassicStylePickerLayout.ArrowGlyph, Tok.TextPrimary)],
    }, Loc.Get(labelKey));

    static Element Swatch(IPlayerStyleVerbs verbs, Rail.PlayerCatalog.Finish finish, int index, bool on, Func<string?> artUrl)
    {
        Prop<ColorF> fill = finish.Swatch == Rail.PlayerCatalog.FromCover
            ? Prop.Of(() => ClassicHeroWash.Of(artUrl()))
            : ColorScience.ToColor(finish.Swatch);
        return ToolTip.Wrap(new BoxEl
        {
            Key = ClassicStylePickerLayout.SwatchPrefix + finish.Slug, Width = ClassicStylePickerLayout.SwatchHit, Height = ClassicStylePickerLayout.SwatchHit, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = () => verbs.SetFinish(index),
            Children =
            [
                new BoxEl
                {
                    Width = ClassicStylePickerLayout.Swatch, Height = ClassicStylePickerLayout.Swatch,
                    Shrink = 0f, Corners = Radii.Circle(ClassicStylePickerLayout.Swatch), Fill = fill,
                    BorderWidth = ClassicStylePickerLayout.SwatchRing, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlStrongDefault,
                    BrushTransitionMs = Design.Motion.Faster,
                    HitTestVisible = false,
                },
            ],
        }, Loc.Get(finish.LabelKey));
    }

    /// <summary>The dots' laid-out width (one strip shows at a time), noted by its bounds callback: a re-render keeps the node and its bounds,
    /// so the width outlives the element that first reported it.</summary>
    static float s_dotsWidth;
    static readonly Action<RectF> s_noteDotsWidth = static r => s_dotsWidth = r.W;

    /// <summary>The dots: ONE target (40 tall, up to a dot's 24 each, narrower where the arrows beside it leave less) whose click picks the dot
    /// under the pointer.</summary>
    static Element Dots(IPlayerStyleVerbs verbs, Element[] dots)
    {
        return new BoxEl
        {
            Key = ClassicStylePickerLayout.DotsKey, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = ClassicStylePickerLayout.DotBox * dots.Length, Height = ClassicStylePickerLayout.DotHitH, Direction = 0,
            AlignItems = FlexAlign.Center, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Role = AutomationRole.Slider, Focusable = true, Cursor = CursorId.Hand,
            OnBoundsChanged = s_noteDotsWidth,
            OnPointerDown = p => verbs.FocusOn(Math.Clamp((int)(p.X * dots.Length / MathF.Max(1f, s_dotsWidth)), 0, dots.Length - 1)),
            Children = dots,
        };
    }

    static Element Dot(int id, bool on) => new BoxEl
    {
        Key = ClassicStylePickerLayout.DotPrefix + Rail.PlayerCatalog.ById(id).Slug, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = ClassicStylePickerLayout.DotHitH,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = on ? ClassicStylePickerLayout.DotOnW : ClassicStylePickerLayout.DotW, Height = ClassicStylePickerLayout.DotH, Shrink = 0f,
                Corners = Radii.Circle(ClassicStylePickerLayout.DotH), Fill = on ? Tok.AccentDefault : Tok.TextTertiary,
                BrushTransitionMs = Design.Motion.Faster, Animate = Design.Reduced ? null : ClassicStylePickerLayout.DotMotion,
            },
        ],
    };
}

// <summary>The now-playing body (scrolls under the pinned hero).</summary>
internal sealed class ClassicNowPlayingBody : Component
{
    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var track = props.NowTrack();
        int trackSlot = track.Slot;
        var nowArtist = Rail.NowPlayingArtist(track);
        int artistSlot = nowArtist.Slot;

        // The rail is not a page and never mounts on a route: its demand is keyed on the PLAYING track (§9.3 #6).
        UseEffect(() => props.DemandNowPlaying(trackSlot, artistSlot), DepKey.From(trackSlot, artistSlot));

        if (!track.IsValid)
            return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                    title: Loc.Get(Strings.Player.NothingPlaying), subtitle: "");

        props.SubscribeEntities();

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Children = [ClassicNowPlayingHero.HeroMeta(props, track), Sections(props, track, nowArtist)],
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    /// <summary>About the artist (or its loading face), the cities, merch and "Next up", in one padded column (the docked body's and the
    /// Compact sheet's); an empty box when there is none of them.</summary>
    internal static Element Sections(NowPlayingHeroProps props, Track track, Artist nowArtist)
    {
        var sections = new List<Element>(5);
        if (nowArtist.Slot > 0)
        {
            var artist = nowArtist;
            if (artist.Knows(ArtistFields.Stats))
            {
                sections.Add(ClassicNowPlayingHero.AboutArtist(artist));
                if (artist.TopCities.Length > 0) sections.Add(ClassicNowPlayingHero.CitiesSection(props, artist.TopCities));
            }
            else sections.Add(ClassicNowPlayingHero.LoadingSection());   // pending: under the SAME header, never alone
            var merch = track.Album.MerchSlots.Length > 0 ? track.Album.MerchSlots : artist.MerchSlots;
            if (merch.Length > 0) sections.Add(ClassicNowPlayingHero.MerchSection(props, merch));
        }
        if (props.HasNextUp()) sections.Add(ClassicNowPlayingHero.Section(Loc.Get(Strings.Player.NextUp), Slots.View(ViewKeys.PanelNextUp, props.NextUp)));
        return sections.Count == 0 ? new BoxEl()
            : new BoxEl { Direction = 1, Gap = Spacing.L, Padding = new Edges4(Spacing.M, 0f, Spacing.M, Spacing.M), Children = sections.ToArray() };
    }
}
