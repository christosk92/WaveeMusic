// ── Themes/Zune/Structure/Views/Panel/ZuneNowPlayingHero.cs — Zune's `Player.NowPlayingHero` and `Player.DeckStyleFlyout` (P5 Z4a) ────
//
// The panel's "now playing" pivot (built from Zune's parts, owner Q18: Z-Part-Panel has no now-playing column). The panel's frame pins this
// tile at the top of the arm and draws "up next" under it (`ZunePanelFrame.cs`), so the tile carries the track itself:
//
//   the DECK SLOT   the cover, square at the column's width with the cover radius, or (developer mode, the stored presentation) the core's
//                   deck under the keyed slot "<preset>@<side>" (a preset change remounts it; the hero hosts the deck and never builds it).
//                   Its context menu is the Cover / ‹player› choice and "player style…" (developer-only, as everywhere), which opens the ONE
//                   style flyout anchored on the slot.
//   the meta        the title in the heading voice (26/32 light), the artists as links (13/18), the album as a link (12/16), the heart.
//   the peek        the lyrics peek (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), keyed per track.
//
// The demand for the playing track and its first artist is asked here (the panel is not a page and never mounts on a route).
//
// THE STYLE FLYOUT (no board): Zune's words in place of Classic's thumbnail cards: "player style", then each group's label over its presets as
// words (the current one in the accent), then the current preset's options, each a label over its choices as words or, for a colour, square
// swatches with the accent ring, then "reset options" at the end of the options' label (disabled while every option is at its default). It
// stays open across picks and re-renders off the preferences' epoch.

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

using P = Wavee.Themes.Zune.ZunePanelBodies;

namespace Wavee.Themes.Zune;

/// <summary>Zune's now-playing hero and the deck style flyout.</summary>
internal static class ZuneNowPlayingHero
{
    /// <summary>The node keys the mounted tests find: the deck slot and the meta block.</summary>
    public const string SlotKey = ZunePlayerLayout.HeroSlotKey, MetaKey = ZunePlayerLayout.HeroMetaKey;

    /// <summary>The style flyout's keys: a style ("npv-style:record"), an option's choice ("npv-choice:size:7"), the options block, the reset.</summary>
    public const string FlyoutStylePrefix = "npv-style:", FlyoutChoicePrefix = "npv-choice:", FlyoutOptionsKey = "npv-style-options",
        FlyoutResetKey = "npv-style-reset";

    /// <summary>The registered template of <see cref="ViewKeys.NowPlayingHero"/>.</summary>
    public static Element Template(NowPlayingHeroProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneHeroTile());

    /// <summary>The registered template of <see cref="ViewKeys.DeckStyleFlyout"/>.</summary>
    public static Element FlyoutTemplate(DeckStyleFlyoutProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneStyleFlyout());

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
            Size = P.HeroArtistSize, LineHeight = P.HeroArtistLine, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }
}

/// <summary>The cover-derived wash: the hero's placeholder and the flyout's "from the cover" swatch (one function, so the two never grade the
/// artwork differently). The palette watch is read inside, so a late grading repaints a bound fill without a render.</summary>
internal static class ZuneHeroWash
{
    public static ColorF Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}

/// <summary>The tile: the deck slot, the meta and the peek. A component (its bound cover thunks are built once).</summary>
internal sealed class ZuneHeroTile : Component
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
        _placeholder ??= () => ZuneHeroWash.Of(props.NowArtUrl());

        Element face = presentation == Rail.PlayerPrefs.Player
            ? new BoxEl { Key = "deck-face:" + preset.Slug, Direction = 1, Shrink = 0f, Children = [props.Deck(P.HeroPadding.Left + P.HeroPadding.Right, _column)] }
            : CanvasVideo.OverArt(new ImageEl
            {
                Key = "cover", Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = P.HeroDecodePx,
                Corners = R.Shape.All(ShapeRole.Cover), AlignSelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder),
            }, CanvasVideo.Surface.Hero, R.Shape.All(ShapeRole.Cover));
        var p = preset;
        Element slot = new BoxEl { Key = ZuneNowPlayingHero.SlotKey, Direction = 1, Shrink = 0f, AspectRatio = 1f, OnBoundsChanged = _onSlotBounds, Children = [face] }
            .WithContextMenu(overlay, () => ZuneNowPlayingHero.ArtMenu(props, presentation, p, styleVisible));
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
            R.Type.NowPlayingTitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (ZuneNowPlayingHero.ArtistLinks(props, track) is { } artists) lines.Add(artists);
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            var albumUri = album.Uri;
            lines.Add(new SpanTextEl([new TextSpan(albumTitle, OnClick: () => props.GoTo(albumUri, albumTitle))])
            {
                Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }
        var meta = new BoxEl
        {
            Key = ZuneNowPlayingHero.MetaKey, Direction = 0, AlignItems = FlexAlign.Start, Gap = P.HeroMetaGap,
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
                slot,
                meta,
                props.LyricsPeek() with { Key = "npv-lyrics:" + uri },
            ],
        };
    }
}

/// <summary>The style flyout's body.</summary>
internal sealed class ZuneStyleFlyout : Component
{
    static readonly Rail.PlayerCatalog.Preset[] s_group = new Rail.PlayerCatalog.Preset[Rail.PlayerCatalog.PerGroup];

    /// <summary>A word's or swatch's hit row this render: 32 tall, 40 at Compact (the touch floor).</summary>
    float _hit;

    public override Element Render()
    {
        var props = UseProps<DeckStyleFlyoutProps>();
        var verbs = props.Verbs;
        var preset = Rail.PlayerPrefs.CurrentPreset();                      // epoch-subscribed
        bool atDefaults = Rail.PlayerPrefs.AtDefaults(in preset);            // epoch-subscribed: a pick lights the reset, a reset dims it
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        _hit = tiers.Policy is not null && tiers.Window == WidthTier.Compact ? P.DeckTouchH : P.DeckResetH;
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
            // The options block, its own keyed box: its height eases between styles and the rows cross-fade per style (never a snap).
            new BoxEl
            {
                Key = ZuneNowPlayingHero.FlyoutOptionsKey, Direction = 1, Gap = P.DeckGap, ClipToBounds = true,
                Animate = Design.Reduced ? null : P.DeckOptionsMotion,
                Children = [OptionsHead(verbs, preset, atDefaults), ..options],
            },
        };
        return new BoxEl { Width = P.DeckW, Direction = 1, Gap = P.DeckGap, Padding = Edges4.All(P.DeckPad), Children = kids.ToArray() };
    }

    static TextEl Label(string text) => new(text)
    {
        Size = P.DeckLabelSize, LineHeight = P.DeckLabelLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>"options", and at its end "reset options": always there, disabled (the disabled ink, inert) while every option of the style is at
    /// its default.</summary>
    Element OptionsHead(IDeckStyleVerbs verbs, Rail.PlayerCatalog.Preset preset, bool atDefaults) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Height = _hit, Gap = P.DeckWordGap,
        Children =
        [
            Label(Loc.Get(Strings.Player.StyleOptions)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
            new BoxEl
            {
                Key = ZuneNowPlayingHero.FlyoutResetKey, Height = _hit, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Role = AutomationRole.Button, Focusable = !atDefaults, IsEnabled = !atDefaults, Cursor = atDefaults ? null : CursorId.Hand,
                OnClick = atDefaults ? null : () => verbs.ResetChoices(in preset),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.ResetOptions))
                    {
                        Size = P.DeckWordSize, LineHeight = P.DeckWordLine, Color = atDefaults ? Tok.TextDisabled : Tok.AccentTextPrimary,
                        HoverColor = atDefaults ? Tok.TextDisabled : Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
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
        Direction = 0, Wrap = true, Gap = P.DeckWordGap, AlignItems = FlexAlign.Center, Children = words,
    };

    Element Group(IDeckStyleVerbs verbs, string labelKey, Rail.PlayerGroup group, int selectedId)
    {
        int n = Rail.PlayerCatalog.Group(group, s_group);
        var words = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int id = s_group[i].Id;
            words[i] = Choice(ZuneNowPlayingHero.FlyoutStylePrefix + s_group[i].Slug, Loc.Get(s_group[i].ShortLabelKey), id == selectedId, () => verbs.SetStyle(id));
        }
        return new BoxEl { Direction = 1, Gap = P.DeckWordRowGap, Children = [Label(Loc.Get(labelKey)), Words(words)] };
    }

    Element Option(DeckStyleFlyoutProps props, Rail.PlayerCatalog.Preset preset, Rail.PlayerCatalog.OptionDef option)
    {
        int current = Rail.PlayerPrefs.Choice(preset, option);
        var verbs = props.Verbs;
        var choices = new Element[option.Choices.Length];
        string keyStem = ZuneNowPlayingHero.FlyoutChoicePrefix + option.Slug + ":";
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
                ? Prop.Of(() => ZuneHeroWash.Of(props.NowArtUrl()))
                : ColorScience.ToColor(choice.Swatch);
            // A hairline on the dot: the black finish is the flyout's own ground in the dark theme and vanished without one.
            choices[i] = ToolTip.Wrap(new BoxEl
            {
                Key = keyStem + choice.Slug,
                Width = MathF.Max(_hit, P.DeckSwatchRing), Height = _hit, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = on ? P.DeckSwatchStroke : 0f, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = pick,
                Children =
                [
                    new BoxEl
                    {
                        Width = P.DeckSwatchDot, Height = P.DeckSwatchDot, Shrink = 0f, Fill = fill, BorderWidth = P.DeckSwatchHairline,
                        BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
                    },
                ],
            }, Loc.Get(choice.LabelKey));
        }
        return new BoxEl
        {
            Key = preset.Slug + "." + option.Slug, Direction = 1, Gap = P.DeckWordRowGap,
            Enter = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
            Children = [Label(Loc.Get(option.LabelKey)), Words(choices)],
        };
    }
}
