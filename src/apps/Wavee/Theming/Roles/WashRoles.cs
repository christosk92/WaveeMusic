// ── Theming/Roles/WashRoles.cs — how strongly a style lets artwork colour its surfaces (themes plan 5.4, T2b) ──────────
//
// The wash roles are every number and recipe by which a cover's colour is MIXED INTO a surface: the Home shell washes' strength,
// the detail page's tone plane, the artist page's blend wash and hero veil, the shell material tint, the section band, the cover
// placeholder tint. Dark carries roughly TWICE the light strength (the same colour reads far weaker over the dark ground, and the
// light ground has less headroom before a wash turns into a smudge), which is why each strength is a `ModePair`.
//
// What stays OUT of the roles: the wash GEOMETRY (the window-relative ellipses and their clipping, `Design.Wash.Hero/Weekly/Mix`) is
// a paint budget and not a look, and the cover-colour MATHS (lift, vivid, the page-tone clamp, the hairline solve) is
// `Platform\ColorScience.cs`. A role here picks WHICH of those a style runs, and at what strength.
//
// THE KEYS. A wash or tone plane is keyed on its SURFACE (and a radial wash on its artwork), never on the theme: a light/dark flip
// or a definition swap that moves a role re-runs the plane's colour in place inside the live cross-fade, which fades every colour
// consumer, bound fills and gradients included (EP20, Classic tuning F10). A key on `Tok.Epoch` would remount the plane and snap it.
// `Entities\Palette.Host.cs` builds the keys.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The wash roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="NeutralGround">The ground the material layer paints when no page has a colour. NEVER <c>ColorF.Transparent</c>: transparent is premultiplied BLACK, and cross-fading into or out of it drags the interpolated colour toward black for the whole ramp.</param>
/// <param name="HeroAlpha">Alpha at the origin of Home's hero wash.</param>
/// <param name="ShelfAlpha">Alpha at the origin of Home's two shelf washes.</param>
/// <param name="HeroVeilPull">How far the artist hero veil's layer colour is pulled toward the accent.</param>
/// <param name="HeroVeilPeak">The vertical hero veil's peak alpha, where copy stacks at a photo's bottom seam (the softened peak).</param>
/// <param name="CoverMix">How far the card ground is lerped toward a cover's lifted accent in a hero cover wash (the deck faces' art, the flyout swatch, every undecoded cover slot).</param>
/// <param name="ConcertMediaMix">How far a concert card's base fill is lerped toward the event's accent.</param>
/// <param name="PageTonePlaneAlpha">How much of the detail page's tone plane covers the Mica stack beneath it: a WHISPER over the material, never a surface on top of it.</param>
/// <param name="BlendWashTop">The artist blend wash's alpha at its top.</param>
/// <param name="BlendWashBoundary">The artist blend wash's alpha at its boundary.</param>
/// <param name="ShellTintAlpha">The alpha of the shell material tint a detail page publishes.</param>
/// <param name="PageToneFromHue">The detail page's ground tone from a cover's dominant hue: the hue only, with lightness FORCED and saturation CAPPED, or the neutral tone for a hue-less cover. Null never comes back for a style that defines a neutral tone.</param>
/// <param name="ShellTintFromScheme">The shell material tint a graded cover publishes. Two different ROLES of the grading, not one role at two alphas (a lifted background role in light is a pastel smear across the chrome).</param>
/// <param name="BlendWashLadder">The artist blend wash's accent ladder (graded, payload, held, default) for a theme kind: how a graded cover and a raw wire colour become the wash colour BEFORE the first grading lands.</param>
public sealed record WashRoles(
    ColorRole NeutralGround,
    ModePair<float> HeroAlpha, ModePair<float> ShelfAlpha,
    ModePair<float> HeroVeilPull, ModePair<float> HeroVeilPeak,
    ModePair<float> CoverMix, ModePair<float> ConcertMediaMix,
    ModePair<float> PageTonePlaneAlpha, ModePair<float> BlendWashTop, ModePair<float> BlendWashBoundary,
    ModePair<float> ShellTintAlpha,
    Func<ThemeKind, ColorF, ColorF?> PageToneFromHue,
    Func<ThemeKind, Scheme, ColorF> ShellTintFromScheme,
    Func<ThemeKind, Scheme?, uint, bool, AccentLadder.Result> BlendWashLadder);

/// <summary>The wash roles as the call syntax a view mixes with (<c>R.Wash.HeroAlpha</c>). Every member evaluates its role for the
/// ACTIVE theme kind at the call: nothing is cached.</summary>
public readonly struct WashReads
{
    private readonly WashRoles _roles;

    internal WashReads(WashRoles roles) => _roles = roles;

    /// <summary>The role table behind these reads.</summary>
    public WashRoles Roles => _roles;

    /// <inheritdoc cref="WashRoles.NeutralGround"/>
    public ColorF NeutralGround => _roles.NeutralGround.Now;
    /// <inheritdoc cref="WashRoles.HeroAlpha"/>
    public float HeroAlpha => _roles.HeroAlpha.Now;
    /// <inheritdoc cref="WashRoles.ShelfAlpha"/>
    public float ShelfAlpha => _roles.ShelfAlpha.Now;
    /// <inheritdoc cref="WashRoles.HeroVeilPull"/>
    public float HeroVeilPull => _roles.HeroVeilPull.Now;
    /// <inheritdoc cref="WashRoles.HeroVeilPeak"/>
    public float HeroVeilPeak => _roles.HeroVeilPeak.Now;
    /// <inheritdoc cref="WashRoles.CoverMix"/>
    public float CoverMix => _roles.CoverMix.Now;
    /// <inheritdoc cref="WashRoles.ConcertMediaMix"/>
    public float ConcertMediaMix => _roles.ConcertMediaMix.Now;
    /// <inheritdoc cref="WashRoles.PageTonePlaneAlpha"/>
    public float PageTonePlaneAlpha => _roles.PageTonePlaneAlpha.Now;
    /// <inheritdoc cref="WashRoles.BlendWashTop"/>
    public float BlendWashTop => _roles.BlendWashTop.Now;
    /// <inheritdoc cref="WashRoles.BlendWashBoundary"/>
    public float BlendWashBoundary => _roles.BlendWashBoundary.Now;
    /// <inheritdoc cref="WashRoles.ShellTintAlpha"/>
    public float ShellTintAlpha => _roles.ShellTintAlpha.Now;

    /// <summary>The page ground tone for a cover's dominant hue under the ACTIVE theme kind.</summary>
    public ColorF? PageToneFromHue(ColorF dominant) => _roles.PageToneFromHue(Tok.Theme, dominant);

    /// <summary>The shell material tint a graded cover publishes under the ACTIVE theme kind.</summary>
    public ColorF ShellTintFromScheme(in Scheme scheme) => _roles.ShellTintFromScheme(Tok.Theme, scheme);

    /// <summary>The artist blend wash's accent ladder under the ACTIVE theme kind.</summary>
    public AccentLadder.Result BlendWashLadder(Scheme? pageScheme, uint payloadAccent, bool definite)
        => _roles.BlendWashLadder(Tok.Theme, pageScheme, payloadAccent, definite);
}
