using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// Cover-colour leaves: Watch subscriptions that must NOT sit in a page <c>Render()</c>. A graded batch then
/// re-renders only these nodes (or paints via a Fill bind) — never the whole Artist/Detail/NowPlaying tree.
/// </summary>
static class CoverPaletteLeaves
{
    /// <summary>THE detail page's ground: one flat, translucent art-derived plane (<see cref="WaveePalette.PageTone"/>)
    /// behind both hero arms, and — in hero-only mode — the fade back to the neutral surface below the hero. It replaces
    /// the stack of top-anchored alpha washes the two arms used to paint.
    ///
    /// <para>The tone never leaves this leaf. It briefly had to: the sticky context band flattened its opaque material
    /// over the page's ground and needed to know what that ground was. The band paints NOTHING now — scrolled content
    /// is clipped at its lower edge and this plane simply shows through it — so the publication, and the signal it
    /// wrote, are gone.</para></summary>
    public static Element PageTonePlane(string? url, string? fallbackUrl, bool disabled, float heroBand,
                                        float pageHeight, bool heroOnly, string key)
        => Embed.Comp(new CoverPageTonePlane.Props(url, fallbackUrl, disabled, heroBand, pageHeight, heroOnly),
                      () => new CoverPageTonePlane()) with { Key = key };

    /// <summary>Artist page blend wash (height follows the hero layout for the current width).</summary>
    public static Element ArtistBlendWash(string? url, float heroWidth, bool disabled, string key)
        => Embed.Comp(new CoverArtistBlendWash.Props(url, heroWidth, disabled),
                      () => new CoverArtistBlendWash()) with { Key = key };

    /// <summary>Full-bleed artist hero veil over photography.</summary>
    public static Element ArtistHeroVeil(string? url, ArtistHeroVeilAxis axis, float width, float height, string key)
        => Embed.Comp(new CoverKeyedVeil.Props(url, axis, width, height),
                      () => new CoverKeyedVeil()) with { Key = key };

    /// <summary>Publishes the page-scoped shell material tint when THIS cover is graded — no page re-render.</summary>
    public static Element ShellTint(string? url, bool ready, bool disabled, bool apply, object owner,
                                    Signal<ShellMaterialState>? slot, string key, string? fallbackUrl = null)
        => Embed.Comp(new CoverShellTintBinder.Props(url, fallbackUrl, ready, disabled, apply, owner, slot),
                      () => new CoverShellTintBinder()) with { Key = key };
}

/// <summary>The detail page's flat art-derived ground. The <c>CoverColorPlane.Watch</c> subscription lives HERE, in
/// a leaf, never in a page <c>Render</c> — so a grading arriving mid-scroll re-renders this one node and nothing else;
/// the rail, the hero and the virtualized track list are untouched. The bound <c>Fill</c> keeps the brush itself on
/// the compositor, and <c>BrushTransitionMs</c> is what turns a grading arrival into a cross-fade rather than a snap.
///
/// <para>This plane is a PAGE-ROOT sibling of the scrolling page, never a child of it, which is what makes the sticky
/// context band's offset model work: the band paints nothing, content is clipped at its lower edge, and what shows in
/// that gap is this node — the record's own tone.</para>
///
/// <para><b>ONE FLAT TONE, AND NOTHING SAMPLED FROM THE COVER. There is no blurred-artwork band here any more, and this
/// paragraph is the tombstone.</b> A "background extension" used to sit on top of this plane — a scaled, 1.35×-saturated,
/// 72σ baked-blur copy of the cover across the hero band, masked away at its lower edge — and it was the one layer in
/// the app whose loudness depended on the ARTWORK rather than on the theme. Its alpha was tuned three times chasing
/// that (a flat 0.40, then a flat 0.32 in light, then a luminance-adaptive 0.34→0.14 in dark) and it still bloomed: a
/// dark sleeve made it invisible and a bright one made it a haze across the top of the page, so two records rendered as
/// two different DESIGNS. The user reported the pages as inconsistent by kind — an album reading calm beside a playlist
/// reading blown-out — when what actually differed was cover brightness, since both kinds run this identical code.</para>
///
/// <para>It also contradicted the rule the rest of the system is built on. <see cref="WaveePalette.PageTone"/>'s first
/// clause is <i>complementary, not sampled</i> — "not the cover's dominant colour re-painted at page scale, that is what
/// produced pages the artwork could not be told apart from" — and a blurred copy of the cover at page scale is exactly
/// the forbidden move, sitting directly on the plane that forbids it. Deleting it (constants included, rather than
/// leaving a dormant recipe to be re-tuned a fourth time) leaves every page the clamped tone alone: hue from the record,
/// lightness and saturation from the page. Do not reintroduce it — <c>DetailPageToneTests</c> asserts the absence.</para></summary>
sealed class CoverPageTonePlane : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Disabled, float HeroBand, float PageHeight,
                                 bool HeroOnly);

    public override Element Render()
    {
        var p = UseProps<Props>();

        // The Watch subscriptions, resolved ONCE per render and read here. Hoisting them out of the Fill closure
        // matters: Watch takes the plane's lock, and a bound brush is re-evaluated on the paint path. Render still has
        // to subscribe (not just the brush) because the ARRIVAL of a grading is what decides whether this node exists
        // at all — a page with no tone paints nothing.
        var plane = SpotifyLive.CoverColorPlane.Current;
        _ = plane.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = plane.Watch(fb).Value;

        ColorF? resolved = p.Disabled ? null : Resolve(p);
        if (p.Disabled || resolved is null)
            return new BoxEl { Grow = 1f, HitTestVisible = false };

        // At most ONE child, and only in hero-only mode: the tone BAND that fades back to the neutral surface. The
        // default arm paints the plane's own flat fill and nothing else.
        var kids = new List<Element>(1);
        if (p.HeroOnly && HeroOnlyVeil(p) is { } veil) kids.Add(veil);

        return new BoxEl
        {
            ZStack = true, Grow = 1f, HitTestVisible = false,
            ClipToBounds = true, Corners = WaveeShell.ContentPaneCorners,
            // BOUND: the brush stays a compositor value, so a theme/preset re-fire lands without this subtree being
            // rebuilt, and the 250ms ramp cross-fades a grading arrival instead of snapping to it.
            // TRANSLUCENT, deliberately (user report: an opaque tone made detail pages a dead slab beside Home's
            // breathing surface). The tone rides OVER the standard content stack — FileArea over live Mica — so the
            // wallpaper's life shows through it exactly as it does on every other page; the clamped tone still names
            // the record's hue. Dark can afford more transparency (dark Mica is near-black, so the composite barely
            // moves); light stays high so a loud wallpaper cannot blow through the whisper clamp.
            Fill = Prop.Of(() => !p.HeroOnly && Resolve(p) is { } t
                ? t with { A = Tok.Theme == ThemeKind.Light ? PlaneAlphaLight : PlaneAlphaDark }
                : ColorF.Transparent),   // hero-only: the tone BAND child paints; the page below breathes
            BrushTransitionMs = WaveeMotion.Standard,
            Children = kids.ToArray(),
        };
    }

    /// <summary>How much of the tone plane covers the Mica stack beneath it. The dial between "the page reads as the
    /// record's colour" and "the page is part of a Mica window", and the answer is now firmly the SECOND: the user's
    /// standing direction is <i>mostly Mica</i>.
    ///
    /// <para><b>The ratchet, in order, because every step of it was a user report and the direction never reversed.</b>
    /// 1.0 (opaque) read as a dead slab. 0.72 still read as a slab — it passed only ~28% of Mica's wallpaper signal.
    /// 0.45 dark / 0.90 light passed ~55% / ~10%, which survived the "slab" objection but was still described as
    /// looking "solid" once the blurred artwork band was deleted and the flat tone was all that remained. So the plane
    /// is now a WHISPER over the material rather than a surface on top of it. The reference is Home, whose ~30% smoke
    /// passes ~70% of dark Mica and is the approved "alive" look: at 0.20 this plane passes ~80%, i.e. the detail pages
    /// are deliberately MORE Mica than Home is.</para>
    ///
    /// <para>LIGHT no longer stays high. It was 0.90 to stop a loud wallpaper shifting the page's read, and that
    /// argument is real but it is subordinate to the direction — a near-white whisper tone (L 0.94, S ≤ 0.16) over
    /// near-white Mica has almost no hue to protect in the first place, so what 0.90 was mostly buying was flatness.
    /// The honest consequence, stated so it is not rediscovered as a bug: <b>in light theme the record's hue is now
    /// close to imperceptible</b> and a busy wallpaper shows through. Light identity is carried by the chrome accent
    /// (the Play capsule, the accent rule, row chrome), not by the page ground. Raise this pair, never the clamp, if
    /// the page should take more colour again.</para></summary>
    const float PlaneAlphaDark = 0.20f, PlaneAlphaLight = 0.30f;

    /// <summary>Cover grading → the page's ground. Cheap: two dictionary probes and the clamp; no subscription (the
    /// caller owns that), so it is safe on the paint path.</summary>
    static ColorF? Resolve(Props p)
        => WaveePalette.PageTone(Surfaces.SchemeFor(p.Url) ?? Surfaces.SchemeFor(p.FallbackUrl), Tok.Theme);

    /// <summary>Hero-only mode: the tone paints ONLY the hero band and fades to nothing below it. Under the
    /// translucent-plane model this is a tone BAND, not a ground-overpaint: below the fade the page is simply the
    /// unpainted content stack (FileArea over live Mica), the same breathing surface every other page has — which is
    /// exactly what "limit page color to the hero" should mean on a Mica window.</summary>
    static Element? HeroOnlyVeil(Props p)
    {
        float pageH = p.PageHeight > 1f ? p.PageHeight : 0f;
        if (pageH <= 1f) return null;
        if (Resolve(p) is not { } tone) return null;
        float alpha = Tok.Theme == ThemeKind.Light ? PlaneAlphaLight : PlaneAlphaDark;
        float band = DetailVerticalLayout.BackdropBandFor(p.HeroBand);
        float start = Math.Clamp(band / pageH, 0.12f, 0.80f);
        float end = MathF.Min(1f, start + 0.22f);
        return new BoxEl
        {
            HitTestVisible = false,   // sized by the ZStack (no explicit extent ⇒ full bleed)
            Gradient = GradientDown(
                new GradientStop(0f, tone with { A = alpha }),
                new GradientStop(start, tone with { A = alpha }),
                new GradientStop(end, tone with { A = 0f }),
                new GradientStop(1f, tone with { A = 0f })),
        };
    }
}

sealed class CoverArtistBlendWash : Component
{
    internal sealed record Props(string? Url, float HeroWidth, bool Disabled);

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Disabled) return new BoxEl();

        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        var pagePal = Surfaces.SchemeFor(p.Url);
        bool light = Tok.Theme == ThemeKind.Light;
        ColorF wash = light
            ? (pagePal is { } wp ? WaveePalette.Lift(WaveePalette.Accent(wp)) : Tok.AccentDefault)
            : WaveePalette.BackgroundDark(pagePal ?? WaveePalette.Neutral);
        float heroWidth = p.HeroWidth;
        return new BoxEl
        {
            Height = ArtistHeroLayout.BlendBackdropHeightFor(heroWidth), HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, wash with { A = light ? 0.20f : 0.30f }),
                new GradientStop(ArtistHeroLayout.BlendBoundaryFor(heroWidth), wash with { A = light ? 0.06f : 0.08f }),
                new GradientStop(1f, wash with { A = 0f })),
        };
    }
}

/// <summary>Artist hero photography veil — cover-keyed so a late grading does not rebuild Banner.</summary>
sealed class CoverKeyedVeil : Component
{
    internal sealed record Props(string? Url, ArtistHeroVeilAxis Axis, float Width, float Height);

    public override Element Render()
    {
        var p = UseProps<Props>();
        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        var pagePal = Surfaces.SchemeFor(p.Url);
        var chromePal = Surfaces.ChromeSchemeFor(p.Url);
        ColorF accent = chromePal is { } pal ? WaveePalette.ChromeAccent(pal) : Tok.AccentDefault;
        ColorF washAccent = pagePal is { } wp ? WaveePalette.Lift(WaveePalette.Accent(wp)) : accent;
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, HitTestVisible = false,
            Gradient = Surfaces.ArtistHeroVeil(washAccent, p.Axis),
        };
    }
}

/// <summary>Shell material tint publisher. Watches one cover; writes <see cref="ShellMaterialState"/> without the page
/// Render subscribing to that Watch. Flat arm only (<c>Wash: null</c>) — detail/artist pages never publish the radial
/// three-layer wash, which belongs to Home.</summary>
sealed class CoverShellTintBinder : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Ready, bool Disabled, bool Apply, object Owner,
                                 Signal<ShellMaterialState>? Slot);

    public override Element Render()
    {
        var p = UseProps<Props>();
        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = SpotifyLive.CoverColorPlane.Current.Watch(fb).Value;
        var coverArt = p.Ready ? Surfaces.SchemeFor(p.Url) : null;
        var artPalette = coverArt ?? (p.Ready ? Surfaces.SchemeFor(p.FallbackUrl) : null);
        // A low-alpha art tone published over the shell's deterministic opaque ground (not a backdrop scrim): it warms
        // the ground, never replaces it. Null ⇒ the bare ground.
        ColorF? shellTint = p.Disabled || !p.Apply || artPalette is not { } artScheme ? null
            : Tok.Theme == ThemeKind.Light
                ? WaveePalette.Lift(WaveePalette.ToColor(artScheme.TextBase)) with { A = 0.05f }
                : WaveePalette.TintedDark(artScheme) with { A = 0.14f };

        void SetTint(ColorF? color)
        {
            if (p.Slot is not null) p.Slot.Value = new ShellMaterialState(p.Owner, color, null);
        }
        void ClearTint()
        {
            if (p.Slot is not null && ReferenceEquals(p.Slot.Peek().Owner, p.Owner)) p.Slot.Value = default;
        }

        UseEffect(() => SetTint(shellTint),
            DepKey.From(HashCode.Combine(p.Url, shellTint.HasValue, shellTint.GetValueOrDefault(), Tok.Theme, p.Ready, p.Disabled, p.Apply)));
        UseActivation(onActivated: () => SetTint(shellTint), onDeactivated: ClearTint);
        UseEffect(() => (Action?)ClearTint, DepKey.Empty);

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
