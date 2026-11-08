// ── Themes/Zune/Structure/Views/Panel/ZuneNowPlayingHero.cs — Zune's `Player.NowPlayingHero` (P5 Z4a; the player style redesign 2026-11-08) ────
//
// The panel's "now playing" pivot (built from Zune's parts, owner Q18: Z-Part-Panel has no now-playing column) and the approved player-style
// design (canvas-design `Zune.dc.html`, `Styles.dc.html`). The panel's frame pins this tile at the top of the arm and draws "up next" under it
// (`ZunePanelFrame.cs`), so the tile carries the track itself:
//
//   the head       "now playing" (14/20, the secondary ink) and the STYLE CHIP: the style's name in lowercase (15/20) and a chevron, flat (no
//                  plate, no stroke). The chip opens the in-place picker: the head becomes "player style" with "cancel" and "done" (the
//                  accent), and Escape is cancel.
//   the stage      the core's `props.Stage` (the cover, the canvas or a deck; the picker's carousel), square corners.
//   the meta       the title in the heading voice (26/32 light), the artists as links (13/18), the album as a link (12/16), the heart. PICKING,
//                  the strip takes its place: square arrows either side of the focused style's name (26/32 light: Zune's large type wraps,
//                  never shrinks) and its line, the finish swatches (Vinyl, Pocket; squares) and the dots (squares). The block eases its
//                  height as one cross-fades into the other.
//   the peek       the lyrics peek (the core host `props.LyricsPeek`, which mounts `Player.LyricsPeek`), keyed per track.
//
// No pivot of its own, no gear, no flyout, no option dial. The demand for the playing track and its first artist is asked here (the panel is
// not a page and never mounts on a route).

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

/// <summary>Zune's now-playing hero.</summary>
internal static class ZuneNowPlayingHero
{
    /// <summary>The node keys the mounted tests find: the stage's slot and the meta block.</summary>
    public const string SlotKey = ZunePlayerLayout.HeroSlotKey, MetaKey = ZunePlayerLayout.HeroMetaKey;

    /// <summary>The registered template of <see cref="ViewKeys.NowPlayingHero"/>.</summary>
    public static Element Template(NowPlayingHeroProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneHeroTile());

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

    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    /// <summary>One of two cross-fading layers (the head's rest and picking rows; the meta and the strip).</summary>
    internal static BoxEl Layer(string key, Element body) => new()
    {
        Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch,
        Enter = Fade, Exit = Fade, Transition = MotionTok.ControlFast, Children = [body],
    };

    /// <summary>The head: "now playing" and the chip at rest; "player style", cancel and done while picking. A fixed 36 row.</summary>
    internal static Element Head(IPlayerStyleVerbs verbs, bool open, int shown, bool bleeds)
    {
        Element body = open
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.WordGap, Height = P.HeadH,
                Children =
                [
                    Label(Loc.Get(Strings.Player.PlayerStyle)),
                    Word(P.CancelKey, Loc.Get(Strings.Player.Picker.Cancel), Tok.TextSecondary, verbs.Cancel),
                    Word(P.DoneKey, Loc.Get(Strings.Player.Picker.Done), Tok.AccentTextPrimary, verbs.Done),
                ],
            }
            : bleeds
            ? new BoxEl
            {
                // Over the canvas the pivot already says "now playing": the chip alone, at the end, in the media scope's ink.
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = P.HeadH,
                Children = [Chip(verbs, shown, new CanvasInk(true))],
            }
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = P.WordGap, Height = P.HeadH,
                Children = [Label(Loc.Get(Strings.Player.NowPlaying)), Chip(verbs, shown, default)],
            };
        return new BoxEl
        {
            Key = P.HeadKey, Height = P.HeadH, Shrink = 0f, ZStack = true,
            Children = [Layer(open ? "head:pick" : bleeds ? "head:bleed" : "head:rest", body)],
        };
    }

    static TextEl Label(string text) => new(text)
    {
        Size = P.HeadLabelSize, LineHeight = P.HeadLabelLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Heading),
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A flat lowercase command word (cancel, done).</summary>
    static Element Word(string key, string text, ColorF ink, Action act) => new BoxEl
    {
        Key = key, Height = P.ChipH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(P.WordPad, 0f, P.WordPad, 0f),
        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = act,
        Children =
        [
            new TextEl(text)
            {
                Size = P.ChipTextSize, LineHeight = P.ChipTextLine, Color = ink, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command),
                MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    /// <summary>The style chip: the style's name (lowercase) and a chevron, flat, in <paramref name="ink"/>.</summary>
    static Element Chip(IPlayerStyleVerbs verbs, int shown, CanvasInk ink) => ToolTip.Wrap(new BoxEl
    {
        Key = P.ChipKey, Height = P.ChipH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = P.ChipGap,
        Padding = new Edges4(P.WordPad, 0f, P.WordPad, 0f),
        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = verbs.OpenPicker,
        Children =
        [
            new TextEl(Loc.Get(Rail.PlayerCatalog.ById(shown).NameKey))
            {
                Size = P.ChipTextSize, LineHeight = P.ChipTextLine, Color = ink.Primary, HoverColor = ink.Accent,
                Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
            Ui.Icon(Icons.ChevronDown, P.ChipGlyph, ink.Primary),
        ],
    }, Loc.Get(Strings.Player.PlayerStyle));

    /// <summary>The picker's strip: square arrows either side of the focused style's name and line, the finish swatches, the dots.</summary>
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
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.NameGap, MinHeight = P.NameBlockMinH, Justify = FlexJustify.Center,
                            Children =
                            [
                                R.Type.NowPlayingTitle(Loc.Get(style.NameKey)) with
                                {
                                    Case = R.Case.For(LabelKind.Heading), TextAlign = TextAlign.Center, Wrap = TextWrap.Wrap, MaxLines = 2,
                                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                new TextEl(Loc.Get(style.LineKey))
                                {
                                    Size = P.LineSize, LineHeight = P.LineLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                                    TextAlign = TextAlign.Center, Wrap = TextWrap.Wrap, MaxLines = P.LineMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                            ],
                        },
                    ],
                },
                // Only a style with finishes has swatches: the row opens to their height and closes to nothing, eased (no jump).
                new BoxEl
                {
                    Height = style.HasFinish ? P.SwatchRowH : 0f, ClipToBounds = true, Direction = 0, AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center, Gap = P.SwatchGap, Animate = Design.Reduced ? null : P.SwapMotion, Children = swatches,
                },
                // The arrows flank the dots, clear of the name and line above.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = P.RowGap,
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
        Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = step,
        Children = [Ui.Icon(glyph, P.ArrowGlyph, Tok.TextPrimary)],
    }, Loc.Get(labelKey));

    static Element Swatch(IPlayerStyleVerbs verbs, Rail.PlayerCatalog.Finish finish, int index, bool on, Func<string?> artUrl)
    {
        Prop<ColorF> fill = finish.Swatch == Rail.PlayerCatalog.FromCover
            ? Prop.Of(() => ZuneHeroWash.Of(artUrl()))
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
                    Width = P.Swatch, Height = P.Swatch, Shrink = 0f, Fill = fill,
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
                Corners = default, Fill = on ? Tok.AccentDefault : Tok.TextTertiary,
                BrushTransitionMs = Design.Motion.Faster, Animate = Design.Reduced ? null : P.DotMotion,
            },
        ],
    };
}

/// <summary>The cover-derived wash: the hero's placeholder and the picture disc's swatch (one function, so the two never grade the artwork
/// differently). The palette watch is read inside, so a late grading repaints a bound fill without a render.</summary>
internal static class ZuneHeroWash
{
    public static ColorF Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}

/// <summary>The tile: the head, the stage, the meta (or the picker's strip) and the peek. A component (its bound cover thunks are built once).</summary>
internal sealed class ZuneHeroTile : Component
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
        _placeholder ??= () => ZuneHeroWash.Of(props.NowArtUrl());
        _cover ??= () => new ImageEl
        {
            Key = "cover", Source = Prop.Of(_source), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = P.HeroDecodePx,
            Corners = R.Shape.All(ShapeRole.Cover), AlignSelf = FlexAlign.Stretch, Placeholder = Prop.Of(_placeholder),
        };

        // Over the edge-to-edge canvas the stage holds the room that puts the meta's top 196 above the canvas's foot (or the panel's).
        _bleedRoom ??= panelW =>
        {
            float vh = MathF.Round(panelW * P.BleedAspect), ph = props.RailHeight.Value;
            return MathF.Min(vh, ph > 0f ? ph : vh) - P.BleedMetaLift - P.BleedStageTop - P.HeroGap;
        };
        bool bleeds = props.Bleeds();

        var verbs = props.Style;
        Element slot = new BoxEl
        {
            Key = ZuneNowPlayingHero.SlotKey, Direction = 1, Shrink = 0f,
            Children = [props.Stage(P.HeroPadding.Left + P.HeroPadding.Right, _cover, R.Shape.All(ShapeRole.Cover), _bleedRoom)],
        };

        string uri = track.Uri.Text, title = track.Title;
        Element swap = open ? ZuneNowPlayingHero.Layer("npv-strip", ZuneNowPlayingHero.Strip(verbs, shown, props.NowArtUrl))
                     : bleeds ? ZuneNowPlayingHero.Layer("npv-meta:bleed", BleedMeta(props, track, uri, title))
                     : ZuneNowPlayingHero.Layer("npv-meta", Meta(props, track, uri, title));

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = P.HeroGap, Padding = P.HeroPadding,
            Children =
            [
                ZuneNowPlayingHero.Head(verbs, open, shown, bleeds),
                slot,
                // The meta and the lyrics: over the canvas in the light kind they reach down to the canvas's foot and its ramp (the meta's top
                // sits BleedMetaLift above the foot), so what follows ("Next up", on the light ground) never starts over the dark media scope.
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, Gap = P.HeroGap, MinHeight = bleeds && R.IsLight ? P.BleedMetaLift + P.BleedFootRamp : 0f,
                    Animate = Design.Reduced ? null : P.SwapMotion,
                    Children =
                    [
                        new BoxEl { Shrink = 0f, ZStack = true, Animate = Design.Reduced ? null : P.SwapMotion, Children = [swap] },
                        // Over the canvas the lyric lines are drawn in the media scope (Deck.Bleed.cs).
                        Ctx.Provide(Deck.OverCanvas, bleeds, props.LyricsPeek()) with { Key = "npv-lyrics:" + uri },
                    ],
                },
            ],
        };
    }

    /// <summary>The meta over the canvas's fade (BleedZune): the 88 square cover, the title over the album in the accent, the heart in its ring, all
    /// on the cover's foot, in the media scope's ink. The artist is the giant name drifting over the canvas.</summary>
    Element BleedMeta(NowPlayingHeroProps props, Track track, string uri, string title)
    {
        var ink = new CanvasInk(true);
        var lines = new List<Element>(2)
        {
            R.Type.NowPlayingTitle(title) with { Color = ink.Primary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        var album = track.Album;
        if (album.IsValid && album.Title.Length > 0)
        {
            string albumTitle = album.Title;
            var albumUri = album.Uri;
            lines.Add(new SpanTextEl([new TextSpan(albumTitle, OnClick: () => props.GoTo(albumUri, albumTitle))])
            {
                Size = P.BleedAlbumSize, LineHeight = P.BleedAlbumLine, Color = ink.Accent,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }
        return new BoxEl
        {
            Key = ZuneNowPlayingHero.MetaKey, Direction = 0, AlignItems = FlexAlign.End, Gap = P.BleedMetaGap,
            Children =
            [
                new ImageEl
                {
                    Source = Prop.Of(_source!), Fit = ImageFit.Cover, DecodePx = P.HeroDecodePx, Width = P.BleedCover, Height = P.BleedCover,
                    Corners = R.Shape.All(ShapeRole.Cover), Placeholder = Prop.Of(_placeholder!),
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.HeroMetaGap, Children = lines.ToArray() },
                new BoxEl
                {
                    Width = P.HeroHeart, Height = P.HeroHeart, Shrink = 0f, ZStack = true, Corners = Radii.Circle(P.HeroHeart),
                    BorderWidth = P.BleedHeartRing, BorderColor = ink.Primary,
                    Children =
                    [
                        Embed.Comp(() => new Controls.SaveButton
                        {
                            Uri = uri, Name = title, Glyph = P.HeroHeartGlyph, Box = P.HeroHeart, Accent = () => ink.Accent, Rest = () => ink.Secondary,
                        }) with { Key = "save:" + uri },
                    ],
                },
            ],
        };
    }

    static Element Meta(NowPlayingHeroProps props, Track track, string uri, string title)
    {
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
        return new BoxEl
        {
            Key = ZuneNowPlayingHero.MetaKey, Direction = 0, AlignItems = FlexAlign.Start, Gap = P.HeroMetaGap,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = P.HeroMetaGap, Children = lines.ToArray() },
                Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = P.HeroHeartGlyph, Box = P.HeroHeart }) with { Key = "save:" + uri },
            ],
        };
    }
}
