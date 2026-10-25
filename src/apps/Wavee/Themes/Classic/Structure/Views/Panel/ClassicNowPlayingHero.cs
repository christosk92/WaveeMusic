// ── Themes/Classic/Structure/Views/Panel/ClassicNowPlayingHero.cs — Classic's `Player.NowPlayingHero` (themes plan 6.1, T6b) ──
//
// From `Shell/Rail.UI.cs`: the pinned hero tile, its header strip, the art menu, and the now-playing body under it.
//
// THE HERO TILE. A header strip (eyebrow · Cover|‹Player› · gear) over a KEYED hero slot: "cover" or "<slug>@<side>". A preset
// change REMOUNTS (a Cassette must not inherit a Record's mounted deck), an option flip restyles in place off the epoch, and the
// side is quantised to 4 DIP because it is part of the key. The WRAPPER owns the art menu for both occupants. The deck itself is
// the core's (`props.Deck`): the hero HOSTS it under that key and never builds it, so a mode switch, a theme switch of the panel or a
// style flip re-mounts nothing the deck owns. Which face paints is the core's decision (`ResolvedPresentation`: the stored
// presentation while developer mode is on, the cover otherwise).
//
// IN THE COMPACT SHEET (`ClassicPanel.SheetArtSide` provided): the tile is the art alone, centred at the side the window leaves it (no header
// strip: the sheet's head carries its own header, `ClassicSheetHeader`, which seats the developer-only style gear, `ClassicHeroHeaderRow.StyleGear`),
// the deck scaled to that side; the identity, the transport and the rest are the sheet's own column (`ClassicNowPlayingSheet`).
//
// THE BODY (scrolls under the pinned hero) is the panel's now-playing arm: title · artist links · album link · heart, the lyrics
// peek (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), then About the artist, the cities, merch and Next up
// (the `Panel.NextUp` view). It subscribes the entity tables through `props.SubscribeEntities` and
// asks for the playing track's demand (the rail is not a page and never mounts on a route).

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

    // ── the art menu ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cover / ‹Player› as ONE mutually exclusive radio choice, a separator, then "Player style…" opening the SAME
    /// popup the gear owns (one controlled signal, one flyout instance — never a second copy).
    /// <para>The whole menu follows the developer-only gate (<paramref name="styleVisible"/>, <c>Rail.PlayerPrefs.StyleChoiceVisible</c>):
    /// outside developer mode a player style cannot change the face, so the art has no menu at all (#199) rather than a
    /// "Player style…" that does nothing.</para></summary>
    internal static ContextMenuModel? ArtMenu(NowPlayingHeroProps props, int presentation, in Rail.PlayerCatalog.Preset preset, bool styleVisible)
    {
        if (!styleVisible) return null;
        var verbs = props.Style;
        var style = new MenuFlyoutItem(Loc.Get(Strings.Player.PlayerStyleEllipsis), Icons.Settings,
            Invoke: () => verbs.OpenStyleFlyout());
        return new ContextMenuModel(
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.PresentationCover), presentation == Rail.PlayerPrefs.Cover,
                () => verbs.SetPresentation(Rail.PlayerPrefs.Cover, DeckStyleSource.ArtMenu), Icons.Picture),
            MenuFlyoutItem.RadioItem(Loc.Get(preset.ShortLabelKey), presentation == Rail.PlayerPrefs.Player,
                () => verbs.SetPresentation(Rail.PlayerPrefs.Player, DeckStyleSource.ArtMenu), Icons.Album),
            MenuFlyoutItem.Separator,
            style,
        ]);
    }

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

/// <summary>The hero tile: the header strip over a keyed hero slot. A component of the theme (it owns the overlay context and the
/// bound cover thunks, built once per instance so a re-render does not re-register them).</summary>
internal sealed class ClassicHeroTile : Component
{
    Func<string>? _source;
    Func<ColorF>? _placeholder;

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var overlay = UseContext(Overlay.Service);
        var track = props.NowTrack();
        // The RESOLVED face, not the stored one: the Cover/‹Player› switch is developer-only, so a normal build paints
        // the cover whatever is stored (and a stored ‹Player› is waiting, intact, for developer mode to come back).
        int presentation = Rail.PlayerPrefs.ResolvedPresentation();
        bool styleVisible = Rail.PlayerPrefs.StyleChoiceVisible();
        var preset = Rail.PlayerPrefs.CurrentPreset();
        float side = Rail.Hero.Side(props.RailWidth.Value);
        var sheetSide = UseContext(ClassicPanel.SheetArtSide);
        if (!track.IsValid) return new BoxEl();                        // nothing playing: no square, no strip

        _source ??= () => props.NowArtUrl() ?? "";
        _placeholder ??= () => ClassicHeroWash.Of(props.NowArtUrl());
        if (sheetSide is not null) return SheetTile(props, overlay, presentation, preset, styleVisible, side, sheetSide);

        Element hero = presentation == Rail.PlayerPrefs.Player
            ? new BoxEl { Key = "deck-face:" + preset.Slug, Direction = 1, Shrink = 0f, Children = [props.Deck()] }
            : new ImageEl
            {
                Key = "cover",
                Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = ClassicHeroLayout.CoverDecodePx,
                Corners = Radii.CardAll, AlignSelf = FlexAlign.Stretch,
                Placeholder = Prop.Of(_placeholder),
            };

        var heroBox = new BoxEl { Direction = 1, Shrink = 0f, Children = [hero] }
            .WithContextMenu(overlay, () => ClassicNowPlayingHero.ArtMenu(props, presentation, preset, styleVisible));

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Rail.Hero.Inset,
            Padding = new Edges4(Rail.Hero.Inset, Rail.Hero.Inset, Rail.Hero.Inset, 0f),
            Children = [Embed.Comp(props, static () => new ClassicHeroHeaderRow()), heroBox],
        };
    }

    /// <summary>The tile inside the Compact sheet: the art alone, centred, <paramref name="sheetSide"/> on a side (bound: a resize re-fits it
    /// without a render); the deck keeps its own layout at the rail's side and is scaled down to the sheet's.</summary>
    Element SheetTile(NowPlayingHeroProps props, IOverlayService overlay, int presentation, in Rail.PlayerCatalog.Preset preset, bool styleVisible,
        float deckSide, Func<float> sheetSide)
    {
        Element hero = presentation == Rail.PlayerPrefs.Player
            ? new BoxEl
            {
                Key = "deck-face:" + preset.Slug, Direction = 1, Shrink = 0f, AlignSelf = FlexAlign.Start, Width = deckSide, Height = deckSide,
                TransformOriginX = 0f, TransformOriginY = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(sheetSide() / MathF.Max(1f, deckSide), sheetSide() / MathF.Max(1f, deckSide))),
                Children = [props.Deck()],
            }
            : new ImageEl
            {
                Key = "cover",
                Source = Prop.Of(_source!), Fit = ImageFit.Cover, DecodePx = ClassicHeroLayout.CoverDecodePx,
                Corners = Radii.CardAll, AspectRatio = 1f, AlignSelf = FlexAlign.Stretch,
                Placeholder = Prop.Of(_placeholder!),
            };
        var p = preset;
        var heroBox = new BoxEl
        {
            Direction = 1, Shrink = 0f, Width = Prop.Of(sheetSide), Height = Prop.Of(sheetSide), Children = [hero],
        }.WithContextMenu(overlay, () => ClassicNowPlayingHero.ArtMenu(props, presentation, p, styleVisible));
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, ClassicPanelTiers.SheetArtTop, 0f, 0f),
            Children = [heroBox],
        };
    }
}

/// <summary>Eyebrow · Cover|‹Player› · gear. The SelectorBar's index is a component-owned MIRROR written by the bar on
/// click (the pill moves in the press frame) and re-synced by an effect when another surface moved the pref — never
/// written during Render.
/// <para>The switch is DEVELOPER-ONLY: outside developer mode it is not composed at all (never disabled-and-shown),
/// and <c>ResolvedPresentation</c> paints the cover. The gear keeps its place either way — the
/// eyebrow then sits beside it alone.</para></summary>
internal sealed class ClassicHeroHeaderRow : Component
{
    static readonly string?[] ModeIcons = [Icons.Picture, Icons.Album];

    /// <summary>WinUI's SelectorBarItem builds a 47-DIP bar; the strip is 36. Compress the ITEM's content box to the
    /// 30-DIP pill (shape-guarded: an item tree change makes this a no-op instead of a mangle).</summary>
    static readonly TemplateParts CompactBar = new()
    {
        [SelectorBar.PartItem] = static item =>
        {
            if (item.Children is not [BoxEl content, var pill]) return item;
            var kids = new Element[content.Children.Length];
            for (int i = 0; i < kids.Length; i++)
            {
                if (content.Children[i].Is(out TextEl t) && t.FontFamily is null)
                {
                    var rung = t.Weight is 600 or 650 or 700 or 540
                        ? R.Type.DenseTitle("")
                        : R.Type.DenseMeta("");
                    kids[i] = t with { Size = rung.Size, LineHeight = rung.LineHeight, Weight = rung.ResolvedWeight };
                }
                else kids[i] = content.Children[i];
            }
            return item with { Children = [content with { Padding = ClassicHeroLayout.SelectorItemPadding, Gap = ClassicHeroLayout.SelectorItemGap, Children = kids }, pill] };
        },
    };

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        int epoch = Rail.PlayerPrefs.Epoch.Value;
        int stored = Rail.PlayerPrefs.Presentation();
        var preset = Rail.PlayerPrefs.CurrentPreset();
        var presentation = UseSignal(stored);
        UseEffect(() => { presentation.SetIfChanged(stored); }, DepKey.From(epoch));
        bool styleVisible = Rail.PlayerPrefs.StyleChoiceVisible();     // the switch AND the gear: developer-only (#199)

        string[] items = [Loc.Get(Strings.Player.PresentationCover), Loc.Get(preset.ShortLabelKey)];
        var verbs = props.Style;

        // Sentence case, never .ToUpper() on a localized string.
        Element eyebrow = R.Type.Eyebrow(Loc.Get(Strings.Player.NowPlaying)) with
        {
            Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        Element gearBox = StyleGear(props);

        return new BoxEl
        {
            Direction = 0, Height = Rail.Hero.HeaderRowH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            // Outside developer mode the row is the eyebrow alone: no switch, and no gear for a style that cannot change the face (#199).
            Children = styleVisible
                ? [
                    eyebrow,
                    SelectorBar.Create(items, presentation,
                        onChange: i => verbs.SetPresentation(i, DeckStyleSource.Header),
                        parts: CompactBar, icons: ModeIcons),
                    gearBox,
                  ]
                : [eyebrow],
        };
    }

    /// <summary>The style gear and the popup it owns (the one flyout instance: the art menu's "Player style…" opens the same one). Developer-only:
    /// the caller composes it only while <c>Rail.PlayerPrefs.StyleChoiceVisible()</c>. Read inside a render: it subscribes the flyout's open flag
    /// (the gear lights wherever the flyout was opened from). The docked header row and the Compact sheet's header both seat it.</summary>
    internal static Element StyleGear(NowPlayingHeroProps props)
    {
        bool open = Rail.PlayerPrefs.StyleFlyoutOpen.Value;
        var verbs = props.Style;
        var flyout = props.Flyout;
        Element gear = ClassicPanel.HeaderButton(Icons.Settings, Loc.Get(Strings.Player.PlayerStyle),
            () => verbs.ToggleStyleFlyout(), active: open);
        // A plain wrapper takes the row's Center: the popup's own anchor wrapper is AlignSelf=Start.
        return new BoxEl
        {
            Direction = 0, Shrink = 0f,
            Children =
            [
                Popup.Create(gear, () => Slots.View(ViewKeys.DeckStyleFlyout, flyout), Rail.PlayerPrefs.StyleFlyoutOpen,
                    onOpenChanged: static nowOpen => { if (!nowOpen) Rail.NpvDiagnostics.FlyoutClosed(); },
                    placement: FlyoutPlacement.BottomEdgeAlignedRight,
                    options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)),
            ],
        };
    }
}

/// <summary>The now-playing body (scrolls under the pinned hero).</summary>
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
