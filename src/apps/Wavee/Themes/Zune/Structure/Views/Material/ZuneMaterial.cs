// ── Themes/Zune/Structure/Views/Material/ZuneMaterial.cs — Zune's `Shell.Material` and its cover leaves (themes plan 6.3; P5 Z2b) ─────
//
// Zune's window has no Mica (the style's material is None): the material layer under the frame's column IS the window's ground. It draws:
//
//   GROUND   the flat ground, the style's shell ground (#161616 dark, #f5f5f5 light), always.
//   WASH     `background` = artwork wash: the cover tint the page published (`ShellMaterialState.Tint`, through Zune's tint binder below) as
//            a band from the top of the window that fades out by the foot of the band (the chrome's part of the wash; the page's own
//            part is the page-tone and artist-blend leaves, so the two meet instead of doubling under the page).
//   BLEED    `background` = artwork wash or mix: the page's bleed image (`PageChrome.Backdrop.Bleed`, an artist's header) behind the title
//            bar, the band and the head, faint and fading out at its foot (Z-Artist).
//   MIX      `background` = mix: the page's mix covers (`PageChrome.Backdrop.Mix`) faint behind the top of the window, side by side.
//   NONE     `background` = none: the flat ground alone (every page, the leaves too).
// Live: the option is read tracked, so a change re-renders the layer and the wash / bleed / mix fade in or out (keyed, Enter / Exit).
//
// THE COVER LEAVES (decided here, plan "Zune specifics"):
//   PageTone     OWN: under artwork wash, the page's tone as a band from the page's top fading out within its hero band; nothing otherwise.
//   ArtistBlend  OWN: under artwork wash, the graded blend wash over the artist's hero (the style's ladder); nothing otherwise.
//   ArtistVeil   OWN: a NEUTRAL veil (no hue at any option): Zune darkens the photo for the title and never tints it.
//   TintBinder   OWN: publishes the cover tint under artwork wash and a definite "no colour" otherwise, so mix and none keep the chrome flat.
// The hand-over is the core's (`ShellMaterial.Publish` / `TintOwnership`); nothing here decides who owns the slot.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
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

        if (background == ZuneBackground.ArtworkWash && state.Tint is { } tint)
        {
            var top = tint with { A = MathF.Min(1f, tint.A * ZuneBandRules.WashTopScale) };
            kids.Add(new BoxEl
            {
                // Keyed on the colour: a new tint mounts a fresh band that fades in over the old one (a gradient cannot brush-fade).
                Key = ZuneBandRules.WashKey + ":" + tint.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                Height = ZuneBandRules.WashH, AlignSelf = FlexAlign.Start, HitTestVisible = false,
                Gradient = GradientDown(new GradientStop(0f, top), new GradientStop(1f, tint with { A = 0f })),
                Enter = Fade, Exit = Fade,
            });
        }

        if (background != ZuneBackground.None && backdrop.Bleed is { Length: > 0 } bleed)
        {
            kids.Add(new BoxEl
            {
                Key = ZuneBandRules.BleedKey + (R.IsLight ? "-light:" : ":") + bleed,
                Height = ZuneBandRules.BleedH, AlignSelf = FlexAlign.Start, Direction = 1, ClipToBounds = true, HitTestVisible = false,
                Opacity = ZuneBandRules.BleedOpacityOf(R.IsLight),
                Enter = Fade, Exit = Fade,
                Children = [Backdrop(bleed, ZuneBandRules.BleedH, ZuneBandRules.BleedFade)],
            });
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

    /// <summary>A backdrop image: cover-fitted, <paramref name="height"/> tall, fading out over its lower <paramref name="fade"/>.</summary>
    static Element Backdrop(string url, float height, float fade) => new ImageEl
    {
        Source = url, Fit = ImageFit.Cover, Height = height, AlignSelf = FlexAlign.Stretch,
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

/// <summary>The detail page's tone (a page-root sibling): under artwork wash, a band of the page's tone from its top that fades out within
/// its hero band; nothing otherwise. One shape in every arm (the bound gradient's colour reads a transparent tone when there is none), so a
/// grading that lands after the mount is wired, and the option going to none clears it in place.</summary>
internal sealed class ZuneCoverTone : Component
{
    public override Element Render()
    {
        var p = UseProps<CoverToneProps>();
        bool wash = ZuneOptions.Background() == ZuneBackground.ArtworkWash;
        if (wash && p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        if (wash && p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal)) _ = Palette.Watch(fb).Value;

        var tone = wash ? Resolve(p) : null;
        Element[] kids = tone is { } t
            ? [new BoxEl
               {
                   Key = "zune.page-tone:" + t.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                   HitTestVisible = false,
                   Height = p.BackdropBand > 1f ? p.BackdropBand : float.NaN, Grow = p.BackdropBand > 1f ? 0f : 1f,
                   Gradient = GradientDown(
                       new GradientStop(0f, t with { A = R.Wash.PageTonePlaneAlphaAt(false) }),
                       new GradientStop(ZuneBandRules.ToneFadeStop, t with { A = 0f })),
                   Enter = Design.Reduced ? null : ZuneBandRules.MaterialFade,
                   Exit = Design.Reduced ? null : ZuneBandRules.MaterialFade,
               }]
            : [];
        return new BoxEl { Direction = 1, Grow = 1f, HitTestVisible = false, ClipToBounds = true, Children = kids };
    }

    /// <summary>The cover's grading as the page tone, else the payload accent's; null while neither is known.</summary>
    static ColorF? Resolve(CoverToneProps p)
    {
        var wash = R.Wash;
        if ((Design.SchemeFor(p.Url) ?? Design.SchemeFor(p.FallbackUrl)) is { } scheme && wash.PageToneFromHue(ColorScience.Accent(scheme)) is { } fromArt)
            return fromArt;
        return p.PayloadAccent == 0 ? null : wash.PageToneFromHue(ColorScience.ToColor(p.PayloadAccent));
    }
}

/// <summary>The artist page's blend wash: under artwork wash, the style's blend ladder (graded, then the payload, then neutral) from the
/// hero's top through the caller's boundary to nothing; nothing otherwise. A re-graded colour mounts a fresh keyed node that fades in after
/// the first paint (never on it).</summary>
internal sealed class ZuneCoverBlend : Component
{
    bool _mounted;

    public override Element Render()
    {
        var p = UseProps<CoverBlendProps>();
        if (ZuneOptions.Background() != ZuneBackground.ArtworkWash) { _mounted = false; return new BoxEl { HitTestVisible = false }; }
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        bool definite = string.IsNullOrEmpty(p.Url) || !Palette.CanGrade(p.Url);
        var roles = R.Wash;
        var color = roles.BlendWashLadder(Design.SchemeFor(p.Url), p.PayloadAccent, definite).Color;
        var fade = _mounted && !Design.Reduced ? ZuneBandRules.MaterialFade : (EnterExit?)null;
        _mounted = true;
        return new BoxEl
        {
            ZStack = true, Height = p.Height, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Key = "zune.artist-blend:" + color.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                    HitTestVisible = false,
                    Gradient = GradientDown(
                        new GradientStop(0f, color with { A = roles.BlendWashTop }),
                        new GradientStop(p.Boundary, color with { A = roles.BlendWashBoundary }),
                        new GradientStop(1f, color with { A = 0f })),
                    Enter = fade, Exit = fade,
                },
            ],
        };
    }
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
