// ── Themes/WinUi/Structure/Views/Panel/WinUiNowPlayingHero.cs — WinUI's `Player.NowPlayingHero` and `Player.DeckStyleFlyout` (P6 W4a) ──
//
// Board W-PaneNowPlaying: the pane's "Now playing" tab. The panel's frame places this tile at the top of the tab and "Next up" under it
// (`WinUiPanel.cs`), so the tile carries the track itself:
//
//   the head        "Now playing" (Caption, secondary) and, in developer mode, the gear that opens the player style flyout (32, 40 at Compact)
//   the DECK SLOT   the cover, square at the column's width (r8), or (developer mode, the stored presentation) the core's deck under the keyed
//                   slot "<preset>@<side>" (a preset change remounts it; the hero hosts the deck and never builds it). Its context menu is the
//                   Cover / ‹player› choice and "player style…" (developer-only), which opens the ONE style flyout anchored on the slot.
//   the meta        the title (Subtitle 20/28 semibold) with the heart at its end, the artists as accent links (Body 14), "from" and the album
//                   as an accent link (Caption 12)
//   the peek        the lyrics card (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), keyed per track.
//
// The demand for the playing track and its first artist is asked here (the panel is not a page and never mounts on a route).
//
// THE STYLE FLYOUT (no WinUI board): a heading, then each group's label over its presets as text toggles (the current one in the accent),
// then the current preset's options, each a label over its choices or, for a colour, swatches with the accent ring. It stays open across picks.

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

using P = Wavee.Themes.WinUi.WinUiPanelBodies;

namespace Wavee.Themes.WinUi;

/// <summary>Zune's now-playing hero and the deck style flyout.</summary>
internal static class WinUiNowPlayingHero
{
    /// <summary>The node keys the mounted tests find: the deck slot and the meta block.</summary>
    public const string SlotKey = WinUiPanelBodies.HeroSlotKey, MetaKey = WinUiPanelBodies.HeroMetaKey;

    /// <summary>The registered template of <see cref="ViewKeys.NowPlayingHero"/>.</summary>
    public static Element Template(NowPlayingHeroProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiHeroTile());

    /// <summary>The registered template of <see cref="ViewKeys.DeckStyleFlyout"/>.</summary>
    public static Element FlyoutTemplate(DeckStyleFlyoutProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiStyleFlyout());

    /// <summary>The slot's menu: Cover / ‹player› as one radio choice, then "player style…"; none outside developer mode (a style cannot change
    /// the face there).</summary>
    internal static ContextMenuModel? ArtMenu(NowPlayingHeroProps props, int presentation, in Rail.PlayerCatalog.Preset preset, bool styleVisible)
    {
        if (!styleVisible) return null;
        var verbs = props.Style;
        return new ContextMenuModel(
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.PresentationCover), presentation == Rail.PlayerPrefs.Cover,
                () => verbs.SetPresentation(Rail.PlayerPrefs.Cover, DeckStyleSource.ArtMenu), Icons.Picture),
            MenuFlyoutItem.RadioItem(Loc.Get(preset.ShortLabelKey), presentation == Rail.PlayerPrefs.Player,
                () => verbs.SetPresentation(Rail.PlayerPrefs.Player, DeckStyleSource.ArtMenu), Icons.Album),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(Strings.Player.PlayerStyleEllipsis), Icons.Settings, Invoke: () => verbs.OpenStyleFlyout()),
        ]);
    }

    /// <summary>The playing track's artists as links (each opens its artist), or null with no credited artist.</summary>
    internal static Element? ArtistLinks(NowPlayingHeroProps props, Track track)
    {
        var artists = track.ArtistSlots;
        if (artists.Length == 0) return null;
        var spans = new TextSpan[artists.Length * 2 - 1];
        for (int i = 0, n = 0; i < artists.Length; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = new Artist(artists[i]);
            string name = a.Name;
            var uri = a.Uri;
            spans[n++] = new TextSpan(name, OnClick: () => props.GoTo(uri, name));
        }
        return new SpanTextEl(spans)
        {
            Size = P.HeroArtistSize, LineHeight = P.HeroArtistLine, Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }
}

/// <summary>The cover-derived wash: the hero's placeholder and the flyout's "from the cover" swatch (one function, so the two never grade the
/// artwork differently). The palette watch is read inside, so a late grading repaints a bound fill without a render.</summary>
internal static class WinUiHeroWash
{
    public static ColorF Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}

/// <summary>The tile: the deck slot, the meta and the peek. A component (its bound cover thunks are built once).</summary>
internal sealed class WinUiHeroTile : Component
{
    /// <summary>"Now playing" and, where a style can be picked, the gear that opens the one style flyout.</summary>
    static Element Head(NowPlayingHeroProps props, bool styleVisible, bool compact)
    {
        float box = compact ? P.TouchButton : P.HeadButton;
        var kids = new List<Element>(2)
        {
            new TextEl(Loc.Get(Strings.Theme.Winui.Player.NowPlaying)) { Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f },
        };
        if (styleVisible)
            kids.Add(ToolTip.Wrap(WinUiPlayerBar.Button(Icons.Settings, () => props.Style.ToggleStyleFlyout(), true, false, box, box, P.MetaGlyph),
                Loc.Get(Strings.Theme.Winui.Player.DeckStyle)));
        return new BoxEl { Key = "npv-head", Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    Func<string>? _source;
    Func<ColorF>? _placeholder;

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var overlay = UseContext(Overlay.Service);
        var track = props.NowTrack();
        var nowArtist = Rail.NowPlayingArtist(track);
        int trackSlot = track.Slot, artistSlot = nowArtist.Slot;
        // The panel is not a page and never mounts on a route: its demand is keyed on the PLAYING track.
        UseEffect(() => props.DemandNowPlaying(trackSlot, artistSlot), DepKey.From(trackSlot, artistSlot));
        int presentation = Rail.PlayerPrefs.ResolvedPresentation();
        bool styleVisible = Rail.PlayerPrefs.StyleChoiceVisible();
        var preset = Rail.PlayerPrefs.CurrentPreset();
        float side = Rail.Hero.Side(props.RailWidth.Value);
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        bool compact = tiers.Policy is not null && tiers.Window == WidthTier.Compact;
        if (!track.IsValid) return new BoxEl();
        props.SubscribeEntities();

        _source ??= () => props.NowArtUrl() ?? "";
        _placeholder ??= () => WinUiHeroWash.Of(props.NowArtUrl());

        Element face = presentation == Rail.PlayerPrefs.Player
            ? new BoxEl { Key = preset.Slug + "@" + (int)side, Direction = 1, Shrink = 0f, Children = [props.Deck()] }
            : new ImageEl
            {
                Key = "cover", Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = P.HeroDecodePx,
                Corners = R.Shape.All(ShapeRole.Cover), AlignSelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder),
            };
        var p = preset;
        Element slot = new BoxEl { Key = WinUiNowPlayingHero.SlotKey, Direction = 1, Shrink = 0f, Children = [face] }
            .WithContextMenu(overlay, () => WinUiNowPlayingHero.ArtMenu(props, presentation, p, styleVisible));
        // The ONE style flyout, anchored on the slot (developer-only, as its doors are).
        if (styleVisible)
            slot = new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Children =
                [
                    Popup.Create(slot, () => Slots.View(ViewKeys.DeckStyleFlyout, props.Flyout), Rail.PlayerPrefs.StyleFlyoutOpen,
                        onOpenChanged: static nowOpen => { if (!nowOpen) Rail.NpvDiagnostics.FlyoutClosed(); },
                        placement: FlyoutPlacement.BottomEdgeAlignedLeft,
                        options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)),
                ],
            };

        string uri = track.Uri.Text, title = track.Title;
        var lines = new List<Element>(3)
        {
            new TextEl(title)
            {
                Size = P.HeroTitleSize, LineHeight = P.HeroTitleLine, Weight = P.HeroTitleWeight, Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (WinUiNowPlayingHero.ArtistLinks(props, track) is { } artists) lines.Add(artists);
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            var albumUri = album.Uri;
            lines.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.HeroFromGap, MinWidth = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Theme.Winui.Player.From)) { Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = Tok.TextSecondary, Shrink = 0f },
                    new SpanTextEl([new TextSpan(albumTitle, OnClick: () => props.GoTo(albumUri, albumTitle))])
                    {
                        Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            });
        }
        var meta = new BoxEl
        {
            Key = WinUiNowPlayingHero.MetaKey, Direction = 0, AlignItems = FlexAlign.Start, Gap = P.HeroMetaGap,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.HeroMetaGap, Children = lines.ToArray() },
                Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = P.HeroHeartGlyph, Box = P.HeroHeart }) with { Key = "save:" + uri },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = P.HeroGap, Padding = P.HeroPadding,
            Children =
            [
                Head(props, styleVisible, compact),
                slot,
                meta,
                props.LyricsPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }
}

/// <summary>The style flyout's body.</summary>
internal sealed class WinUiStyleFlyout : Component
{
    static readonly Rail.PlayerCatalog.Preset[] s_group = new Rail.PlayerCatalog.Preset[Rail.PlayerCatalog.PerGroup];

    public override Element Render()
    {
        var props = UseProps<DeckStyleFlyoutProps>();
        var verbs = props.Verbs;
        var preset = Rail.PlayerPrefs.CurrentPreset();                      // epoch-subscribed
        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Player.PlayerStyle))
            {
                Size = P.DeckTitleSize, LineHeight = P.DeckTitleLine, Weight = P.DeckTitleWeight, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading),
            },
            Group(verbs, Strings.Player.StyleGroupMedia, Rail.PlayerGroup.Media, preset.Id),
            Group(verbs, Strings.Player.StyleGroupDevices, Rail.PlayerGroup.Devices, preset.Id),
            Group(verbs, Strings.Player.StyleGroupSoftware, Rail.PlayerGroup.Software, preset.Id),
        };
        if (preset.Options.Length > 0) kids.Add(Label(Loc.Get(Strings.Player.StyleOptions)));
        for (int i = 0; i < preset.Options.Length; i++) kids.Add(Option(props, preset, preset.Options[i]));
        return new BoxEl { Width = P.DeckW, Direction = 1, Gap = P.DeckGap, Padding = Edges4.All(P.DeckPad), Children = kids.ToArray() };
    }

    static Element Label(string text) => new TextEl(text)
    {
        Size = P.DeckLabelSize, LineHeight = P.DeckLabelLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static BoxEl Choice(string text, bool on, Action pick) => new()
    {
        Shrink = 0f, Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = pick,
        Children =
        [
            new TextEl(text)
            {
                Size = P.DeckWordSize, LineHeight = P.DeckWordLine, Color = on ? Tok.AccentTextPrimary : Tok.TextSecondary, HoverColor = on ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Case = R.Case.For(LabelKind.Command), Wrap = TextWrap.NoWrap,
            },
        ],
    };

    static BoxEl Words(Element[] words) => new()
    {
        Direction = 0, Wrap = true, Gap = P.DeckWordGap, AlignItems = FlexAlign.Center, Children = words,
    };

    static Element Group(IDeckStyleVerbs verbs, string labelKey, Rail.PlayerGroup group, int selectedId)
    {
        int n = Rail.PlayerCatalog.Group(group, s_group);
        var words = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int id = s_group[i].Id;
            words[i] = Choice(Loc.Get(s_group[i].ShortLabelKey), id == selectedId, () => verbs.SetStyle(id));
        }
        return new BoxEl { Direction = 1, Gap = P.DeckWordRowGap, Children = [Label(Loc.Get(labelKey)), Words(words)] };
    }

    static Element Option(DeckStyleFlyoutProps props, Rail.PlayerCatalog.Preset preset, Rail.PlayerCatalog.OptionDef option)
    {
        int current = Rail.PlayerPrefs.Choice(preset, option);
        var verbs = props.Verbs;
        var choices = new Element[option.Choices.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            var choice = option.Choices[i];
            bool on = index == current;
            Action pick = () => verbs.SetChoice(in preset, in option, index);
            if (option.Kind != Rail.OptionKind.Swatch)
            {
                choices[i] = Choice(Loc.Get(choice.LabelKey), on, pick);
                continue;
            }
            Prop<ColorF> fill = choice.Swatch == Rail.PlayerCatalog.FromCover
                ? Prop.Of(() => WinUiHeroWash.Of(props.NowArtUrl()))
                : ColorScience.ToColor(choice.Swatch);
            choices[i] = ToolTip.Wrap(new BoxEl
            {
                Width = P.DeckSwatchRing, Height = P.DeckSwatchRing, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = on ? P.DeckSwatchStroke : 0f, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = pick,
                Children = [new BoxEl { Width = P.DeckSwatchDot, Height = P.DeckSwatchDot, Shrink = 0f, Fill = fill, HitTestVisible = false }],
            }, Loc.Get(choice.LabelKey));
        }
        return new BoxEl { Direction = 1, Gap = P.DeckWordRowGap, Children = [Label(Loc.Get(option.LabelKey)), Words(choices)] };
    }
}
