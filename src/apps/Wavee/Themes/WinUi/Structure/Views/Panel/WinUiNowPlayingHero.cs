// ── Themes/WinUi/Structure/Views/Panel/WinUiNowPlayingHero.cs — WinUI's `Player.NowPlayingHero` (P6 W4a; the player style redesign 2026-11-08) ──
//
// Board W-PaneNowPlaying and the approved player-style design (canvas-design `Main.dc.html`, `Styles.dc.html`): the pane's "Now playing" tab.
// The panel's frame places this tile at the top of the tab and "Next up" under it (`WinUiPanel.cs`), so the tile carries the track itself:
//
//   the head       "Now playing" (Subtitle 20/28 semibold) and the STYLE CHIP naming the player style ("Cover ⌄": a 32 pill, 13/20, a
//                  hairline). The chip opens the in-place picker: the head becomes "Player style" with Cancel and Done (Escape is Cancel).
//   the stage      the core's `props.Stage` (the cover, the canvas or a deck; the picker's carousel), in the column; the neighbours peek into
//                  the tile's padding.
//   the meta       the title (Subtitle 20/28 semibold) and the heart (40); the artists as accent links (Body 14), "from" and the album as an
//                  accent link (Caption 12). PICKING, the strip takes its place: the 40 round arrows either side of the focused style's name
//                  and its one-line description, the finish swatches (Vinyl, Pocket; the row is there for every style) and the dots. The block
//                  eases its height as one cross-fades into the other.
//   the peek       the lyrics card (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), keyed per track.
//
// No pivot, no gear, no flyout, no option dial (owner, 2026-11-08: "4000 different dials"). The demand for the playing track and its first
// artist is asked here (the panel is not a page and never mounts on a route).

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

/// <summary>WinUI's now-playing hero.</summary>
internal static class WinUiNowPlayingHero
{
    /// <summary>The node keys `find` reads: the stage's slot and the meta block.</summary>
    public const string SlotKey = WinUiPanelBodies.HeroSlotKey, MetaKey = WinUiPanelBodies.HeroMetaKey;

    /// <summary>The registered template of <see cref="ViewKeys.NowPlayingHero"/>.</summary>
    public static Element Template(NowPlayingHeroProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiHeroTile());

    /// <summary>The playing track's artists as links (each opens its artist), or null with no credited artist.</summary>
    internal static Element? ArtistLinks(NowPlayingHeroProps props, Track track, CanvasInk ink = default)
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
            Size = P.HeroArtistSize, LineHeight = P.HeroArtistLine, Color = ink.Accent, Wrap = TextWrap.NoWrap, MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    /// <summary>One of two cross-fading layers (the head's rest and picking rows; the meta and the strip).</summary>
    internal static BoxEl Layer(string key, Element body) => new()
    {
        Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch,
        Enter = Fade, Exit = Fade, Transition = MotionTok.ControlFast, Children = [body],
    };

    /// <summary>The head: "Now playing" and the chip at rest (the frosted chip alone over the edge-to-edge canvas: the tabs already say "Now
    /// playing"); "Player style", Cancel and Done while picking. A fixed 36 row.</summary>
    internal static Element Head(IPlayerStyleVerbs verbs, bool open, int shown, bool bleeds)
    {
        Element body = open
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.HeadGap, Height = P.HeadH,
                Children =
                [
                    Heading(Loc.Get(Strings.Player.PlayerStyle)),
                    Button.Subtle(Loc.Get(Strings.Player.Picker.Cancel), verbs.Cancel) with { Key = P.CancelKey, Shrink = 0f },
                    Button.Accent(Loc.Get(Strings.Player.Picker.Done), verbs.Done) with { Key = P.DoneKey, Shrink = 0f },
                ],
            }
            : bleeds
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = P.HeadH,
                Children = [Chip(verbs, shown, frosted: true)],
            }
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.HeadGap, Height = P.HeadH,
                Children = [Heading(Loc.Get(Strings.Player.NowPlaying)), Chip(verbs, shown, frosted: false)],
            };
        return new BoxEl
        {
            Key = P.HeadKey, Height = P.HeadH, Shrink = 0f, ZStack = true,
            Children = [Layer(open ? "head:pick" : bleeds ? "head:bleed" : "head:rest", body)],
        };
    }

    static TextEl Heading(string text) => new(text)
    {
        Size = P.HeroTitleSize, LineHeight = P.HeroTitleLine, Weight = P.HeroTitleWeight, Color = Tok.TextPrimary,
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The style chip: the style's name and a chevron in a hairline pill (<paramref name="frosted"/>: over the canvas, the media scope's
    /// shade at 55 % under a hairline of its ink, the label in that ink).</summary>
    static Element Chip(IPlayerStyleVerbs verbs, int shown, bool frosted)
    {
        var ink = new CanvasInk(frosted);
        ColorF frost = Deck.BleedShade(Tok.FillSolidBase) with { A = P.BleedChipFillA };
        return ToolTip.Wrap(new BoxEl
        {
            Key = P.ChipKey, Height = P.ChipH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = P.ChipGap, Padding = P.ChipPadding,
            Corners = Radii.Circle(P.ChipH),
            Fill = frosted ? frost : Tok.FillControlDefault,
            HoverFill = frosted ? frost : Tok.FillControlSecondary,
            PressedFill = frosted ? frost : Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = frosted ? ink.Primary with { A = P.BleedChipStrokeA } : Tok.StrokeControlDefault,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = verbs.OpenPicker,
            Children =
            [
                new TextEl(Loc.Get(Rail.PlayerCatalog.ById(shown).NameKey))
                {
                    Size = P.ChipTextSize, LineHeight = P.ChipTextLine, Color = ink.Primary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                Ui.Icon(Icons.ChevronDown, P.ChipGlyph, ink.Secondary),
            ],
        }, Loc.Get(Strings.Player.PlayerStyle));
    }

    /// <summary>The picker's strip: the arrows and the focused style's name and line, the finish swatches, the dots.</summary>
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
            Key = P.StripKey, Direction = 1, Gap = P.StripGap,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.NameGap, Height = P.NameBlockH, Justify = FlexJustify.Center,
                            Children =
                            [
                                new TextEl(Loc.Get(style.NameKey))
                                {
                                    Size = P.HeroTitleSize, LineHeight = P.HeroTitleLine, Weight = P.HeroTitleWeight, Color = Tok.TextPrimary,
                                    TextAlign = TextAlign.Center, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                new TextEl(Loc.Get(style.LineKey))
                                {
                                    Size = P.LineSize, LineHeight = P.LineLine, Color = Tok.TextSecondary, TextAlign = TextAlign.Center,
                                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                            ],
                        },
                    ],
                },
                // Only a style with finishes has swatches: the row opens to their height and closes to nothing, eased (no jump).
                new BoxEl
                {
                    Height = style.HasFinish ? P.SwatchRowH : 0f, ClipToBounds = true, Direction = 0, AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center, Gap = P.SwatchGap, Animate = Design.Reduced ? null : P.SwatchRowMotion, Children = swatches,
                },
                // The arrows flank the dots, clear of the name and line above.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = P.HeadGap,
                    Children =
                    [
                        Arrow(P.PrevKey, Icons.ChevronLeft, Strings.Player.Picker.Previous, () => verbs.Step(-1)),
                        Dots(verbs, dots),
                        Arrow(P.NextKey, Icons.ChevronRight, Strings.Player.Picker.Next, () => verbs.Step(+1)),
                    ],
                },
            ],
        };
    }

    static Element Arrow(string key, string glyph, string labelKey, Action step) => ToolTip.Wrap(new BoxEl
    {
        Key = key, Width = P.ArrowBox, Height = P.ArrowBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(P.ArrowBox), Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = step,
        Children = [Ui.Icon(glyph, P.ArrowGlyph, Tok.TextPrimary)],
    }, Loc.Get(labelKey));

    static Element Swatch(IPlayerStyleVerbs verbs, Rail.PlayerCatalog.Finish finish, int index, bool on, Func<string?> artUrl)
    {
        Prop<ColorF> fill = finish.Swatch == Rail.PlayerCatalog.FromCover
            ? Prop.Of(() => WinUiHeroWash.Of(artUrl()))
            : ColorScience.ToColor(finish.Swatch);
        return ToolTip.Wrap(new BoxEl
        {
            Key = P.SwatchPrefix + finish.Slug, Width = P.SwatchHit, Height = P.SwatchHit, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = () => verbs.SetFinish(index),
            Children =
            [
                new BoxEl
                {
                    Width = P.Swatch, Height = P.Swatch, Shrink = 0f, Corners = Radii.Circle(P.Swatch), Fill = fill,
                    BorderWidth = P.SwatchRing, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlStrongDefault, BrushTransitionMs = Design.Motion.Faster,
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
            Key = P.DotsKey, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = P.DotBox * dots.Length, Height = P.DotHitH, Direction = 0,
            AlignItems = FlexAlign.Center, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Role = AutomationRole.Slider, Focusable = true, Cursor = CursorId.Hand,
            OnBoundsChanged = s_noteDotsWidth,
            OnPointerDown = p => verbs.FocusOn(Math.Clamp((int)(p.X * dots.Length / MathF.Max(1f, s_dotsWidth)), 0, dots.Length - 1)),
            Children = dots,
        };
    }

    static Element Dot(int id, bool on) => new BoxEl
    {
        Key = P.DotPrefix + Rail.PlayerCatalog.ById(id).Slug, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = P.DotHitH,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = on ? P.DotOnW : P.DotW, Height = P.DotH, Shrink = 0f,
                Corners = Radii.Circle(P.DotH), Fill = on ? Tok.AccentDefault : Tok.TextTertiary,
                BrushTransitionMs = Design.Motion.Faster, Animate = Design.Reduced ? null : P.DotMotion,
            },
        ],
    };
}

/// <summary>The cover-derived wash: the hero's placeholder and the picture disc's swatch (one function, so the two never grade the artwork
/// differently). The palette watch is read inside, so a late grading repaints a bound fill without a render.</summary>
internal static class WinUiHeroWash
{
    public static ColorF Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}

/// <summary>The tile: the head, the stage, the meta (or the picker's strip) and the peek. A component (its bound cover thunks are built once).</summary>
internal sealed class WinUiHeroTile : Component
{
    Func<string>? _source;
    Func<ColorF>? _placeholder;
    Func<Element>? _cover;
    Func<float, float>? _bleedRoom;

    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var track = props.NowTrack();
        var nowArtist = Rail.NowPlayingArtist(track);
        int trackSlot = track.Slot, artistSlot = nowArtist.Slot;
        // The panel is not a page and never mounts on a route: its demand is keyed on the PLAYING track.
        UseEffect(() => props.DemandNowPlaying(trackSlot, artistSlot), DepKey.From(trackSlot, artistSlot));
        bool open = props.Picking();
        int shown = props.ShownStyle();
        if (!track.IsValid) return new BoxEl();
        props.SubscribeEntities();

        _source ??= () => props.NowArtUrl() ?? "";
        _placeholder ??= () => WinUiHeroWash.Of(props.NowArtUrl());
        _cover ??= () => new ImageEl
        {
            Key = "cover", Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = P.HeroDecodePx,
            Corners = R.Shape.All(ShapeRole.Cover), AlignSelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder),
        };

        // Over the edge-to-edge canvas the stage holds the room that puts the meta's top 196 above where the copy ends (Deck.CopyFoot: the canvas's
        // foot in the dark kind, its light foot's top in the light kind; never below the panel's foot).
        _bleedRoom ??= panelW => Deck.CopyFoot(MathF.Round(panelW * P.BleedAspect), props.RailHeight.Value) - P.BleedMetaLift - P.BleedStageTop - P.HeroGap;
        bool bleeds = props.Bleeds();

        var verbs = props.Style;
        Element slot = new BoxEl
        {
            Key = WinUiNowPlayingHero.SlotKey, Direction = 1, Shrink = 0f,
            Children = [props.Stage(P.HeroPadding.Left + P.HeroPadding.Right, _cover, R.Shape.All(ShapeRole.Cover), _bleedRoom)],
        };

        string uri = track.Uri.Text, title = track.Title;
        // Over the canvas the meta and the lyrics card are drawn in the media scope (Deck.Bleed.cs): its ink, and OverCanvas for the card.
        var ink = new CanvasInk(bleeds);
        Element swap = open ? WinUiNowPlayingHero.Layer("npv-strip", WinUiNowPlayingHero.Strip(verbs, shown, props.NowArtUrl))
                     : bleeds ? WinUiNowPlayingHero.Layer("npv-meta:bleed", Meta(props, track, uri, title, BleedCover(ink), ink))
                     : WinUiNowPlayingHero.Layer("npv-meta", Meta(props, track, uri, title, null, ink));

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = P.HeroGap, Padding = P.HeroPadding,
            Children =
            [
                WinUiNowPlayingHero.Head(verbs, open, shown, bleeds),
                slot,
                // The meta and the lyrics: over the canvas in the light kind they hold the room down to the canvas's foot (the copy, then the
                // foot's dissolve into the light ground), so what follows ("Next up") starts on the light ground, never over the canvas.
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, Gap = P.HeroGap, MinHeight = bleeds && R.IsLight ? P.BleedMetaLift + Deck.LightFoot : 0f,
                    Animate = Design.Reduced ? null : P.SwapMotion,
                    Children =
                    [
                        new BoxEl { Shrink = 0f, ZStack = true, Animate = Design.Reduced ? null : P.SwapMotion, Children = [swap] },
                        Ctx.Provide(Deck.OverCanvas, bleeds, props.LyricsPeek()) with { Key = "npv-lyrics:" + uri },
                    ],
                },
            ],
        };
    }

    /// <summary>The meta's 56 cover over the edge-to-edge canvas: the art under a hairline of the media scope's ink.</summary>
    Element BleedCover(CanvasInk ink) => new BoxEl
    {
        Width = P.BleedCover, Height = P.BleedCover, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(P.BleedCoverRadius), ClipToBounds = true,
        Children =
        [
            new ImageEl
            {
                Source = Prop.Of(_source!), Fit = ImageFit.Cover, DecodePx = P.HeroDecodePx, Corners = CornerRadius4.All(P.BleedCoverRadius),
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder!),
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Corners = CornerRadius4.All(P.BleedCoverRadius), HitTestVisible = false,
                BorderWidth = 1f, BorderColor = ink.Primary with { A = P.BleedCoverStrokeA },
            },
        ],
    };

    /// <summary>The title, the artists and "from" the album beside the heart; <paramref name="lead"/> (the 56 cover over the canvas) before them;
    /// in <paramref name="ink"/> (over the canvas, the media scope's).</summary>
    static Element Meta(NowPlayingHeroProps props, Track track, string uri, string title, Element? lead, CanvasInk ink)
    {
        var lines = new List<Element>(3)
        {
            new TextEl(title)
            {
                Size = P.HeroTitleSize, LineHeight = P.HeroTitleLine, Weight = P.HeroTitleWeight, Color = ink.Primary,
                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (WinUiNowPlayingHero.ArtistLinks(props, track, ink) is { } artists) lines.Add(artists);
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
                    new TextEl(Loc.Get(Strings.Theme.Winui.Player.From)) { Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = ink.Secondary, Shrink = 0f },
                    new SpanTextEl([new TextSpan(albumTitle, OnClick: () => props.GoTo(albumUri, albumTitle))])
                    {
                        Size = P.HeroAlbumSize, LineHeight = P.HeroAlbumLine, Color = ink.Accent, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            });
        }
        var text = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.HeroMetaGap, Children = lines.ToArray() };
        Func<ColorF>? accent = ink.Over ? () => ink.Accent : null, rest = ink.Over ? () => ink.Secondary : null;
        var heart = Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = P.HeroHeartGlyph, Box = P.HeroHeart, Accent = accent, Rest = rest })
            with { Key = "save:" + uri };
        return new BoxEl
        {
            Key = WinUiNowPlayingHero.MetaKey, Direction = 0, AlignItems = lead is null ? FlexAlign.Start : FlexAlign.Center,
            Gap = lead is null ? P.HeroMetaGap : P.BleedMetaGap,
            Children = lead is null ? [text, heart] : [lead, text, heart],
        };
    }
}
