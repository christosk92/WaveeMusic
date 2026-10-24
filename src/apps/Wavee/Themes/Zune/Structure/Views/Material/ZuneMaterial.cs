// ── Themes/Zune/Structure/Views/Material/ZuneMaterial.cs — Zune's `Shell.Material` and its cover leaves (themes plan 6.3; P5 Z2b) ─────
//
// Zune's window has no Mica (the style's material is None): the material layer under the frame's column IS the window's ground. It draws:
//
//   GROUND   the flat ground, the style's shell ground (#161616 dark, #f5f5f5 light), always.
//   WASH     `background` = artwork wash: the cover tint the page published (`ShellMaterialState.Tint`, through Zune's tint binder below) as
//            a band from the top of the window that fades out by the foot of the band (the chrome's part of the wash; the page's own
//            part is the page-tone and artist-blend leaves, so the two meet instead of doubling under the page).
//   BLEED    `background` = artwork wash or mix: the page's bleed image (`PageChrome.Backdrop.Bleed`, an artist's header) behind the title
//            bar, the band and the head, faint and fading out at its foot (Z-Artist). The photograph is cropped around its SUBJECT (lane B10,
//            the owner: "the hero image is kinda cut off"): `Palette.Subject` (the faces the OS finds, or Spotify's safe zone) placed midway between
//            the band's foot and the fade's middle (`ZuneBandRules.BleedSubjectAt`), so the faces stand in the clear band above the hero's title.
//            The bleed mounts once the subject is measured, so its fade-in plays once, at the final crop.
//   MIX      `background` = mix: the page's mix covers (`PageChrome.Backdrop.Mix`) faint behind the top of the window, side by side.
//   NONE     `background` = none: the flat ground alone (every page, the leaves too).
// Live: the option is read tracked, so a change re-renders the layer and the wash / bleed / mix fade in or out (keyed, Enter / Exit).
//
// THE COVER LEAVES (decided here, plan "Zune specifics"):
//   PageTone     OWN: nothing at any option (the shell wash is the one gradient through the hero; a page-top band drew a seam at the band foot).
//   ArtistBlend  OWN: nothing at any option: the shell layer's bleed and wash are the one continuous gradient through the hero (no seam at the band's foot).
//   ArtistVeil   OWN: a NEUTRAL veil (no hue at any option): Zune darkens the photo for the title and never tints it.
//   TintBinder   OWN: publishes the cover tint under artwork wash and a definite "no colour" otherwise, so mix and none keep the chrome flat.
// The hand-over is the core's (`ShellMaterial.Publish` / `TintOwnership`); nothing here decides who owns the slot.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using FluentGpu.Signals;

namespace Wavee.Themes.Zune;

/// <summary>Zune's shell material layer and its four cover leaves.</summary>
internal static class ZuneMaterial
{
    static EnterExit? Fade => Design.Reduced ? null : ZuneBandRules.MaterialFade;

    /// <summary>The registered template of <see cref="ViewKeys.ShellMaterialLayer"/>. Re-renders at navigation rate (the published state,
    /// the page's chrome, the option), never on a resize.</summary>
    public static Element Template(MaterialProps props, in ViewContext ctx)
    {
        var state = props.State.Value;
        var background = ZuneOptions.Background();
        var chrome = ctx.Hooks.UseContext(ZuneFrameContext.PageChrome)?.Value;
        var backdrop = chrome?.Backdrop ?? PageBackdrop.None;

        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Key = ZuneBandRules.GroundKey, Grow = 1f, HitTestVisible = false,
                Fill = Prop.Of(static () => R.Layer.ShellGround),
            },
        };

        if (ZunePageWash.Of(state, backdrop, ctx.Tier().Window) is { } wash)
        {
            kids.Add(new BoxEl
            {
                // Keyed on the colour (and the height): a new tint mounts a fresh band that fades in over the old one (a gradient cannot brush-fade).
                Key = ZuneBandRules.WashKey + (wash.Tall ? "-tall:" : ":") + wash.Tint.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                Height = wash.Height, AlignSelf = FlexAlign.Start, HitTestVisible = false,
                Gradient = GradientDown(wash.Stops),
                Enter = Fade, Exit = Fade,
            });
        }

        if (background != ZuneBackground.None && backdrop.Bleed is { Length: > 0 } bleed)
        {
            float bandFoot = BandFoot(ctx.Tier());
            float frameY = ZuneBandRules.BleedSubjectAt(bandFoot);
            // The photograph waits for its subject (it always settles: faces, or the safe zone), so it is never re-cropped after it shows.
            if (Palette.Subject(bleed) is { } subject)
            {
                kids.Add(new BoxEl
                {
                    Key = ZuneBandRules.BleedKey + (R.IsLight ? "-light:" : ":") + bleed,
                    Height = ZuneBandRules.BleedH, AlignSelf = FlexAlign.Start, Direction = 1, ClipToBounds = true, HitTestVisible = false,
                    Opacity = ZuneBandRules.BleedOpacityOf(R.IsLight),
                    Enter = Fade, Exit = Fade,
                    Children = [Backdrop(bleed, ZuneBandRules.BleedH, ZuneBandRules.BleedFade, subject.X, subject.Y, frameY)],
                });
            }
            // Over the photo, under the chrome: the adaptive scrim that keeps the band's words legible over a bright photograph (V14).
            kids.Add(Embed.Comp(new BleedScrimProps(bleed, bandFoot, frameY), static () => new ZuneBleedScrim())
                with { Key = ZuneBandRules.BleedKey + "-scrim:" + bleed });
        }

        if (background == ZuneBackground.Mix && backdrop.Mix is { Count: > 0 } mix)
        {
            int n = Math.Min(mix.Count, ZuneBandRules.MixMax);
            var covers = new Element[n];
            string mixKey = ZuneBandRules.MixKey + (R.IsLight ? "-light" : "");
            for (int i = 0; i < n; i++) mixKey += ":" + mix[i];
            for (int i = 0; i < n; i++)
                covers[i] = new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, ClipToBounds = true,
                    Children = [Backdrop(mix[i], ZuneBandRules.MixH, ZuneBandRules.MixFade)],
                };
            kids.Add(new BoxEl
            {
                Key = mixKey,
                Height = ZuneBandRules.MixH, AlignSelf = FlexAlign.Start, Direction = 0, ClipToBounds = true, HitTestVisible = false,
                Opacity = ZuneBandRules.MixOpacityOf(R.IsLight),
                Enter = Fade, Exit = Fade,
                Children = covers,
            });
        }

        return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = kids.ToArray() };
    }

    /// <summary>Whether the page stands on a PHOTO BLEED: the option draws one and the page's chrome publishes its photograph (the condition the
    /// layer mounts the bleed and its scrim on). The band strip's small grey words read one step brighter over it (<see cref="StripInk"/>).</summary>
    public static bool PhotoBleed(PageChrome? chrome)
        => ZuneOptions.Background() != ZuneBackground.None && chrome?.Backdrop.Bleed is { Length: > 0 };

    /// <summary>The band strip's small grey words (the crumbs and their separators, the unlit pivots, "more", "pinned"): the secondary ink over a photo
    /// bleed, whose scrim is solved for it (a shade about half as dark as the tertiary needed: the owner's variant D), Zune's tertiary everywhere else.</summary>
    public static ColorF StripInk(PageChrome? chrome) => PhotoBleed(chrome) ? Tok.TextSecondary : Tok.TextTertiary;

    /// <summary>The foot of the band strip (the title bar and the band, row 2 included unless a Short window folds it), from the window's top.</summary>
    static float BandFoot(in TierFacts facts)
        => ZuneTiers.FrameOf(facts).TitleBarH + ZuneBandRules.BandOf(facts).HeightWith(!ZuneBandTiers.FoldsRow2AtRest(in facts));

    /// <summary>A backdrop image: cover-fitted, <paramref name="height"/> tall, fading out over its lower <paramref name="fade"/>; its focal point
    /// (<paramref name="focusX"/>, <paramref name="focusY"/>, centred by default) stands at the box's <paramref name="frameY"/> (centred across).</summary>
    static Element Backdrop(string url, float height, float fade, float focusX = 0.5f, float focusY = 0.5f, float frameY = 0.5f) => new ImageEl
    {
        Source = url, Fit = ImageFit.Cover, FocusX = focusX, FocusY = focusY, FocusFrameY = frameY, Height = height, AlignSelf = FlexAlign.Stretch,
        DecodePx = ZuneBandRules.BackdropDecodePx, Placeholder = ColorF.Transparent,
        Mask = new ImageMaskSpec(EdgeMask.Bottom, 0f, 0f, 0f, fade),
        RevealTransition = Design.Reduced ? null : ImageTransition.Fade(ZuneBandRules.MaterialFadeMs),
    };

    // ── the cover leaves ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTone"/>.</summary>
    public static Element CoverTone(CoverToneProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneCoverTone());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverBlend"/>.</summary>
    public static Element CoverBlend(CoverBlendProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneCoverBlend());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverVeil"/>.</summary>
    public static Element CoverVeil(CoverVeilProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneCoverVeil());

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverTint"/>.</summary>
    public static Element CoverTint(CoverTintProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneCoverTint());
}

/// <summary>What the bleed's scrim reads: the photograph, the band strip's foot and where the bleed places the photograph's subject.</summary>
internal sealed record BleedScrimProps(string Url, float BandFoot, float SubjectAt);

/// <summary>The bleed's ADAPTIVE scrim (V14): over the photo, under the chrome, in the ground's direction (black on the dark kind, the ground on
/// the light one), full over the title bar and the band and clear <see cref="ZuneBandRules.BleedScrimFade"/> below their foot along an eased curve
/// (<see cref="ZuneBandRules.BleedScrimLevel"/>; three stacked boxes of four stops each, the most a gradient carries). Its opacity is solved per
/// photograph (<see cref="ZuneBandRules.BleedScrimAlpha"/>, from the photo's measured luminance, <see cref="Palette.Tone"/>) so the band's dimmest
/// words, in the secondary ink over a bleed (<see cref="ZuneMaterial.StripInk"/>), read over it; 0 until the photo is measured, and every change fades
/// (<see cref="MediaScrim.FadeMs"/>), never snaps.</summary>
internal sealed class ZuneBleedScrim : Component
{
    public override Element Render()
    {
        var p = UseProps<BleedScrimProps>();
        float width = UseContextSignal(Viewport.Size).Value.Width;
        bool light = R.IsLight;
        ColorF ground = R.Layer.ShellGround;
        ColorF scrim = light ? ground : ZuneBandRules.BleedScrimDark;
        MediaScrim.LumaMap? map = Palette.Tone(p.Url);
        PhotoSubject? subject = Palette.Subject(p.Url);
        float target = map is null || subject is not { } s ? 0f : ZuneBandRules.BleedScrimAlpha(map, width, ZuneBandRules.BleedH, p.BandFoot, s, p.SubjectAt,
            ZuneBandRules.BleedOpacityOf(light), ground.R, scrim.R, Tok.TextSecondary);
        var shown = UseRef(target);
        float from = shown.Value;
        shown.Value = target;
        UseTransition(AnimChannel.Opacity, from, target, Motion.ReducedMotion ? 0f : MediaScrim.FadeMs, Easing.SmoothOut, DepKey.From(target));
        float h = ZuneBandRules.BleedH;
        float full = MathF.Round(Math.Clamp(p.BandFoot, 0f, h)), clear = MathF.Round(Math.Clamp(p.BandFoot + ZuneBandRules.BleedScrimFade, full, h));
        // The eased fade's steps 0..8 lie on three boxes, whole DIPs tall where they meet (no seam): [top .. step 2] (with the full stretch above the
        // fade), [2 .. 5], [5 .. 8]; below the last the photo is clear.
        float At(int i) => full + (clear - full) * i / ZuneBandRules.BleedScrimSteps;
        ColorF Level(int i) => scrim with { A = scrim.A * ZuneBandRules.BleedScrimLevel(i) };
        float y2 = MathF.Round(At(2)), y5 = MathF.Round(At(5));
        return new BoxEl
        {
            Height = h, AlignSelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false, Opacity = target,
            Children =
            [
                Shade(y2, GradientDown(new GradientStop(0f, scrim), new GradientStop(full / y2, scrim), new GradientStop(At(1) / y2, Level(1)),
                    new GradientStop(1f, Level(2)))),
                Shade(y5 - y2, GradientDown(new GradientStop(0f, Level(2)), new GradientStop((At(3) - y2) / (y5 - y2), Level(3)),
                    new GradientStop((At(4) - y2) / (y5 - y2), Level(4)), new GradientStop(1f, Level(5)))),
                Shade(clear - y5, GradientDown(new GradientStop(0f, Level(5)), new GradientStop((At(6) - y5) / (clear - y5), Level(6)),
                    new GradientStop((At(7) - y5) / (clear - y5), Level(7)), new GradientStop(1f, Level(8)))),
            ],
        };
    }

    static BoxEl Shade(float height, GradientSpec gradient) => new()
    {
        Height = height, Shrink = 0f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = gradient,
    };
}

/// <summary>The detail page's tone (a page-root sibling): NOTHING under Zune, for the artist blend wash's reason. A band of the page's tone that starts at the page's
/// top starts at the band's foot, where it composites over a different colour than the shell wash above it: a hard horizontal edge there, and, while the page
/// scrolls under the band, wherever the page's own gradient meets the band. The shell material layer's wash (the cover tint, fading out by 180) already tints the top
/// of the window through the hero as ONE gradient. The leaf keeps its registration; it draws no pixel.</summary>
internal sealed class ZuneCoverTone : Component
{
    public override Element Render() => new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
}

/// <summary>The artist page's blend wash: NOTHING under Zune. The shell material layer already draws one continuous photo bleed and one wash band from the
/// window's top through the hero (behind the title bar, the band and the page); a second, page-scoped gradient that starts at the page's top drew a hard
/// horizontal edge at the band's foot (and, while the page scrolled, wherever it met the band), because it composited over a different colour above and below
/// that line. The leaf keeps its registration (the core mounts it) and its key shape; it draws no pixel.</summary>
internal sealed class ZuneCoverBlend : Component
{
    public override Element Render() => new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
}

/// <summary>The photography veil: neutral (the layer colour, no hue), so the title stays legible on the photo at every option. Sized like
/// the core asks (a finite extent, or NaN for the stretch arm).</summary>
internal sealed class ZuneCoverVeil : Component
{
    public override Element Render()
    {
        var p = UseProps<CoverVeilProps>();
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, Grow = float.IsNaN(p.Height) ? 1f : 0f,
            ZStack = true, HitTestVisible = false,
            Children = [new BoxEl { HitTestVisible = false, Gradient = Controls.ArtistHeroVeil(Tok.FillLayerDefault, p.Vertical, p.Hold) }],
        };
    }
}

/// <summary>The shell tint publisher: watches the page's cover and publishes its tint under artwork wash; under mix and none it publishes a
/// DEFINITE "no colour", so the chrome stays the flat ground. Renders a 0 x 0 box.</summary>
internal sealed class ZuneCoverTint : Component
{
    public override Element Render()
    {
        var p = UseProps<CoverTintProps>();
        bool wash = ZuneOptions.Background() == ZuneBackground.ArtworkWash;
        if (wash && p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        if (wash && p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal)) _ = Palette.Watch(fb).Value;

        bool definite = !wash || !p.Apply;
        ColorF? known = null;
        if (!definite)
        {
            var art = p.Ready ? Design.SchemeFor(p.Url) ?? Design.SchemeFor(p.FallbackUrl) : null;
            if (art is { } scheme) known = R.Wash.ShellTintFromScheme(scheme, rich: false);
            else if (p.PayloadAccent != 0) known = ColorScience.Lift(ColorScience.ToColor(p.PayloadAccent)) with { A = R.Wash.ShellTintAlphaAt(false) };
        }

        var claimedOnce = UseRef(false);
        void Publish(bool isClaim) => p.Port.Publish(p.Owner, isClaim, definite, known, wash: null);
        // The theme epoch is in the key: a light/dark flip re-derives the tint and re-publishes it.
        UseEffect(() =>
        {
            Publish(isClaim: !claimedOnce.Value);
            claimedOnce.Value = true;
        }, DepKey.From(HashCode.Combine(p.Url, known.HasValue, known.GetValueOrDefault(), Tok.Epoch, p.Ready, wash, p.Apply, p.PayloadAccent)));
        // A KeepAlive reactivation always claims the slot back.
        UseActivation(onActivated: () => Publish(isClaim: true));

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}

/// <summary>The page's artwork wash as data: the window-fixed gradient the material layer draws from the window's top (the published cover tint;
/// the tall three-stop one on a cover page), so the side panel can continue it under its scrim where it is a card over the page (floating, or
/// sliding out) instead of drawing a plate of its own.</summary>
/// <param name="Tint">The published tint.</param>
/// <param name="Tall">The cover pages' tall wash.</param>
/// <param name="Height">The gradient's height from the window's top.</param>
/// <param name="Stops">Its stops over that height (top to clear).</param>
internal sealed record ZunePageWash(ColorF Tint, bool Tall, float Height, GradientStop[] Stops)
{
    /// <summary>The wash under the artwork-wash background with a published tint; null otherwise (the ground alone).</summary>
    public static ZunePageWash? Of(ShellMaterialState state, PageBackdrop backdrop, WidthTier tier)
    {
        if (ZuneOptions.Background() != ZuneBackground.ArtworkWash || state.Tint is not { } tint) return null;
        var top = tint with { A = MathF.Min(1f, tint.A * ZuneBandRules.WashTopScale) };
        // A cover page (album, playlist, liked) asks for the boards' tall wash: ONE window-fixed gradient, three stops, per-tier height.
        // Anything else (Home, the artist) keeps the band-height one.
        bool tall = backdrop.TallWash;
        var clear = tint with { A = 0f };
        return tall
            ? new ZunePageWash(tint, true, ZuneBandRules.CoverWashH(tier),
                [new GradientStop(0f, top), new GradientStop(ZuneBandRules.CoverWashMidAt, tint with { A = top.A * ZuneBandRules.CoverWashMidShare }), new GradientStop(1f, clear)])
            : new ZunePageWash(tint, false, ZuneBandRules.WashH, [new GradientStop(0f, top), new GradientStop(1f, clear)]);
    }

    /// <summary>The wash's colour <paramref name="y"/> DIP below the window's top (clear past its height).</summary>
    public ColorF At(float y)
    {
        float t = Height > 0f ? y / Height : 1f;
        if (t <= Stops[0].Offset) return Stops[0].Color;
        for (int i = 1; i < Stops.Length; i++)
        {
            if (t > Stops[i].Offset) continue;
            var (a, b) = (Stops[i - 1], Stops[i]);
            float span = b.Offset - a.Offset;
            return ColorF.Lerp(a.Color, b.Color, span > 0f ? (t - a.Offset) / span : 1f);
        }
        return Stops[^1].Color;
    }

    /// <summary>The part of the wash below <paramref name="top"/> DIP, as a gradient for a box whose top edge lies there (its height
    /// <see cref="Height"/> less <paramref name="top"/>); null when the wash is spent above it.</summary>
    public (float Height, GradientStop[] Stops)? Below(float top)
    {
        float h = Height - top;
        if (!(h > 1f)) return null;
        var stops = new List<GradientStop>(Stops.Length) { new(0f, At(top)) };
        foreach (var s in Stops)
        {
            float y = s.Offset * Height - top;
            if (y > 0f) stops.Add(new GradientStop(y / h, s.Color));
        }
        return (h, stops.ToArray());
    }
}
