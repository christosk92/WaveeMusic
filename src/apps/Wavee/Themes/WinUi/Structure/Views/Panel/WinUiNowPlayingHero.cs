// ── Themes/WinUi/Structure/Views/Panel/WinUiNowPlayingHero.cs — WinUI's `Player.NowPlayingHero` and `Player.DeckStyleFlyout` (P6 W4a) ──
//
// Board W-PaneNowPlaying: the pane's "Now playing" tab. The panel's frame places this tile at the top of the tab and "Next up" under it
// (`WinUiPanel.cs`), so the tile carries the track itself:
//
//   the DECK SLOT   the cover (the album art alone: a Canvas shows only where the user picked it, the Canvas player style or the
//                   stage), square at the column's width (r8), or (developer mode, the stored presentation) the core's deck under the keyed
//                   slot "<preset>@<side>" (a preset change remounts it; the hero hosts the deck and never builds it, and passes its padding so
//                   the deck is the column's width). Its context menu is the Cover / ‹player› choice and "player style…" (developer-only), which
//                   opens the ONE style flyout (the gear's).
//   the meta        the title (Subtitle 20/28 semibold), then its trailing commands: in developer mode the player style gear, then the heart
//                   (both 40); the artists as accent links (Body 14), "from" and the album as an accent link (Caption 12)
//   the peek        the lyrics card (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), keyed per track.
//
// The demand for the playing track and its first artist is asked here (the panel is not a page and never mounts on a route).
//
// NO HEAD ROW (owner, 2026-11-07). The board's "Now playing" caption over the stage repeated the selected tab's own word and spent a whole
// row on it and the gear. The gear joins the meta row's trailing commands beside the heart instead: it costs no row, it is visible at rest
// and touch-sized, and its flyout drops beneath it, so the stage stays in view while the flyout (open across picks) previews each style.
//
// THE STYLE FLYOUT (no WinUI board): a heading, then each group's label over its presets as text toggles (the current one in the accent),
// then the current preset's options, each a label over its choices or, for a colour, swatches with the accent ring, then "Reset options"
// (disabled while every option of the style is at its default). It stays open across picks.

using FluentGpu.Animation;
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
    /// <summary>The node keys `find` reads: the deck slot, the meta block and the style gear.</summary>
    public const string SlotKey = WinUiPanelBodies.HeroSlotKey, MetaKey = WinUiPanelBodies.HeroMetaKey, GearKey = WinUiPanelBodies.HeroGearKey;

    /// <summary>The style flyout's keys: a style ("npv-style:record"), an option's choice ("npv-choice:size:7"), the options block, the reset.</summary>
    public const string FlyoutStylePrefix = "npv-style:", FlyoutChoicePrefix = "npv-choice:", FlyoutOptionsKey = "npv-style-options",
        FlyoutResetKey = "npv-style-reset";

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
    Func<string>? _source;
    Func<ColorF>? _placeholder;
    FloatSignal? _column;
    Action<RectF>? _onSlotBounds;

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        // The deck slot's laid-out width: the deck's column (the docked pane's, or a Compact sheet's, which is the window's, not the rail's).
        _column = UseFloatSignal(float.NaN);
        // The slot is a square of the column from its first layout (the cover's own shape), so the meta under it never moves while the deck
        // takes the measured side.
        _onSlotBounds ??= r => { if (_column is { } c && !(MathF.Abs(c.Peek() - r.W) <= 0.25f)) c.Value = r.W; };
        var overlay = UseContext(Overlay.Service);
        var track = props.NowTrack();
        var nowArtist = Rail.NowPlayingArtist(track);
        int trackSlot = track.Slot, artistSlot = nowArtist.Slot;
        // The panel is not a page and never mounts on a route: its demand is keyed on the PLAYING track.
        UseEffect(() => props.DemandNowPlaying(trackSlot, artistSlot), DepKey.From(trackSlot, artistSlot));
        int presentation = Rail.PlayerPrefs.ResolvedPresentation();
        bool styleVisible = Rail.PlayerPrefs.StyleChoiceVisible();
        var preset = Rail.PlayerPrefs.CurrentPreset();
        if (!track.IsValid) return new BoxEl();
        props.SubscribeEntities();

        _source ??= () => props.NowArtUrl() ?? "";
        _placeholder ??= () => WinUiHeroWash.Of(props.NowArtUrl());

        Element face = presentation == Rail.PlayerPrefs.Player
            ? new BoxEl { Key = "deck-face:" + preset.Slug, Direction = 1, Shrink = 0f, Children = [props.Deck(P.HeroPadding.Left + P.HeroPadding.Right, _column)] }
            : new ImageEl
            {
                Key = "cover", Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = P.HeroDecodePx,
                Corners = R.Shape.All(ShapeRole.Cover), AlignSelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder),
            };
        var p = preset;
        Element slot = new BoxEl { Key = WinUiNowPlayingHero.SlotKey, Direction = 1, Shrink = 0f, AspectRatio = 1f, OnBoundsChanged = _onSlotBounds, Children = [face] }
            .WithContextMenu(overlay, () => WinUiNowPlayingHero.ArtMenu(props, presentation, p, styleVisible));

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
        var metaKids = new List<Element>(3)
        {
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.HeroMetaGap, Children = lines.ToArray() },
        };
        // The ONE style flyout hangs off this gear (developer-only, as all its doors are); the art menu's "player style…" opens the same one.
        if (styleVisible) metaKids.Add(Embed.Comp(props, static () => new WinUiStyleGear()) with { Key = WinUiNowPlayingHero.GearKey });
        metaKids.Add(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = P.HeroHeartGlyph, Box = P.HeroHeart }) with { Key = "save:" + uri });
        var meta = new BoxEl
        {
            Key = WinUiNowPlayingHero.MetaKey, Direction = 0, AlignItems = FlexAlign.Start, Gap = P.HeroMetaGap, Children = metaKids.ToArray(),
        };

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = P.HeroGap, Padding = P.HeroPadding,
            Children =
            [
                slot,
                meta,
                props.LyricsPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }
}

/// <summary>The player style gear and the popup it owns, attached to the gear (their right edges flush, dropping beneath it). Its own component: it
/// reads the flyout's open flag, so the gear's plate follows the flyout wherever it was opened from without re-rendering the tile.</summary>
internal sealed class WinUiStyleGear : Component
{
    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        bool open = Rail.PlayerPrefs.StyleFlyoutOpen.Value;
        var verbs = props.Style;
        var flyout = props.Flyout;
        Element gear = ToolTip.Wrap(WinUiPlayerBar.Button(Icons.Settings, () => verbs.ToggleStyleFlyout(), true, false, P.HeroHeart, P.HeroHeart,
            P.HeroHeartGlyph, plated: open), Loc.Get(Strings.Theme.Winui.Player.DeckStyle));
        return Popup.Create(gear, () => Slots.View(ViewKeys.DeckStyleFlyout, flyout), Rail.PlayerPrefs.StyleFlyoutOpen,
            onOpenChanged: static nowOpen => { if (!nowOpen) Rail.NpvDiagnostics.FlyoutClosed(); },
            placement: FlyoutPlacement.BottomEdgeAlignedRight,
            options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup));
    }
}

/// <summary>The style flyout's body.</summary>
internal sealed class WinUiStyleFlyout : Component
{
    static readonly Rail.PlayerCatalog.Preset[] s_group = new Rail.PlayerCatalog.Preset[Rail.PlayerCatalog.PerGroup];

    /// <summary>A choice's hit height this render (32; 40 at Compact).</summary>
    float _hit;

    public override Element Render()
    {
        var props = UseProps<DeckStyleFlyoutProps>();
        var verbs = props.Verbs;
        var preset = Rail.PlayerPrefs.CurrentPreset();                      // epoch-subscribed
        bool atDefaults = Rail.PlayerPrefs.AtDefaults(in preset);            // epoch-subscribed: a pick lights the reset, a reset dims it
        // Every word and swatch is a hit row of its own: 32 tall, 40 at Compact (the touch floor; the rows' gap stays 12).
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        _hit = tiers.Policy is not null && tiers.Window == WidthTier.Compact ? P.TouchButton : P.DeckChoiceH;
        var options = new Element[preset.Options.Length];
        for (int i = 0; i < options.Length; i++) options[i] = Option(props, preset, preset.Options[i]);
        var kids = new List<Element>(5)
        {
            new TextEl(Loc.Get(Strings.Player.PlayerStyle))
            {
                Size = P.DeckTitleSize, LineHeight = P.DeckTitleLine, Weight = P.DeckTitleWeight, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading),
            },
            Group(verbs, Strings.Player.StyleGroupMedia, Rail.PlayerGroup.Media, preset.Id),
            Group(verbs, Strings.Player.StyleGroupDevices, Rail.PlayerGroup.Devices, preset.Id),
            Group(verbs, Strings.Player.StyleGroupSoftware, Rail.PlayerGroup.Software, preset.Id),
            // The options block, its own keyed box: a style with more or fewer options eases the block's height (and the flyout with it), and
            // the rows cross-fade per style (keyed "<style>.<option>"), so a style pick never snaps the flyout's edge.
            new BoxEl
            {
                Key = WinUiNowPlayingHero.FlyoutOptionsKey, Direction = 1, Gap = P.DeckGap, ClipToBounds = true,
                Animate = Design.Reduced ? null : P.DeckOptionsMotion,
                Children =
                [
                    OptionsHead(verbs, preset, atDefaults),
                    ..options,
                ],
            },
        };
        return new BoxEl { Width = P.DeckW, Direction = 1, Gap = P.DeckGap, Padding = Edges4.All(P.DeckPad), Children = kids.ToArray() };
    }

    static TextEl Label(string text) => new(text)
    {
        Size = P.DeckLabelSize, LineHeight = P.DeckLabelLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>"Options", and at its end "Reset options": always there (its slot never comes or goes), disabled while every option of the style
    /// is at its default, so whether it can act reads at rest.</summary>
    Element OptionsHead(IDeckStyleVerbs verbs, Rail.PlayerCatalog.Preset preset, bool atDefaults) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Height = _hit, Gap = P.DeckWordGap,
        Children =
        [
            Label(Loc.Get(Strings.Player.StyleOptions)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
            new BoxEl
            {
                Key = WinUiNowPlayingHero.FlyoutResetKey, Height = _hit, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = P.DeckResetPadding, Corners = CornerRadius4.All(P.DeckResetCorner),
                Fill = ColorF.Transparent, HoverFill = atDefaults ? ColorF.Transparent : Tok.FillSubtleSecondary,
                PressedFill = atDefaults ? ColorF.Transparent : Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Focusable = !atDefaults, IsEnabled = !atDefaults, Cursor = atDefaults ? null : CursorId.Hand,
                OnClick = atDefaults ? null : () => verbs.ResetChoices(in preset),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.ResetOptions))
                    {
                        Size = P.DeckLabelSize, LineHeight = P.DeckLabelLine, Color = atDefaults ? Tok.TextDisabled : Tok.AccentTextPrimary,
                        BrushTransitionMs = Design.Motion.Faster, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            },
        ],
    };

    BoxEl Choice(string key, string text, bool on, Action pick) => new()
    {
        Key = key, Height = _hit, MinWidth = _hit, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = pick,
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
        Direction = 0, Wrap = true, Gap = P.DeckWordGap, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
        Children = words,
    };

    /// <summary>A label in its fixed column beside its choices (one line per group or option: the flyout stays short enough to drop beneath the
    /// gear at a 960 window instead of flipping over the stage).</summary>
    static BoxEl Row(string label, Element choices) => new()
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = P.DeckWordGap,
        Children = [Label(label) with { Width = P.DeckLabelW, Shrink = 0f }, choices],
    };

    Element Group(IDeckStyleVerbs verbs, string labelKey, Rail.PlayerGroup group, int selectedId)
    {
        int n = Rail.PlayerCatalog.Group(group, s_group);
        var words = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int id = s_group[i].Id;
            words[i] = Choice(WinUiNowPlayingHero.FlyoutStylePrefix + s_group[i].Slug, Loc.Get(s_group[i].ShortLabelKey), id == selectedId, () => verbs.SetStyle(id));
        }
        return Row(Loc.Get(labelKey), Words(words));
    }

    Element Option(DeckStyleFlyoutProps props, Rail.PlayerCatalog.Preset preset, Rail.PlayerCatalog.OptionDef option)
    {
        int current = Rail.PlayerPrefs.Choice(preset, option);
        var verbs = props.Verbs;
        var choices = new Element[option.Choices.Length];
        string keyStem = WinUiNowPlayingHero.FlyoutChoicePrefix + option.Slug + ":";
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            var choice = option.Choices[i];
            bool on = index == current;
            Action pick = () => verbs.SetChoice(in preset, in option, index);
            if (option.Kind != Rail.OptionKind.Swatch)
            {
                choices[i] = Choice(keyStem + choice.Slug, Loc.Get(choice.LabelKey), on, pick);
                continue;
            }
            Prop<ColorF> fill = choice.Swatch == Rail.PlayerCatalog.FromCover
                ? Prop.Of(() => WinUiHeroWash.Of(props.NowArtUrl()))
                : ColorScience.ToColor(choice.Swatch);
            // Round, with a hairline: the black finish is the flyout's own ground in the dark theme and vanished without one.
            choices[i] = ToolTip.Wrap(new BoxEl
            {
                Key = keyStem + choice.Slug,
                Width = MathF.Max(_hit, P.DeckSwatchRing), Height = _hit, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = pick,
                Children =
                [
                    new BoxEl
                    {
                        Width = P.DeckSwatchRing, Height = P.DeckSwatchRing, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = Radii.Circle(P.DeckSwatchRing), BorderWidth = on ? P.DeckSwatchStroke : 0f, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
                        HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = P.DeckSwatchDot, Height = P.DeckSwatchDot, Shrink = 0f, Corners = Radii.Circle(P.DeckSwatchDot), Fill = fill,
                                BorderWidth = P.DeckSwatchHairline, BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
                            },
                        ],
                    },
                ],
            }, Loc.Get(choice.LabelKey));
        }
        return Row(Loc.Get(option.LabelKey), Words(choices)) with
        {
            Key = preset.Slug + "." + option.Slug, Enter = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
        };
    }
}
