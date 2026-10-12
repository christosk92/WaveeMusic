// ── Themes/Classic/Structure/Views/Material/ClassicCoverLeaves.cs — Classic's four cover leaves (themes plan 6.1, T4c) ─────
//
// From `Platform/Design.cs` §17: the only four nodes of the old design layer that were Components, and the only four that SUBSCRIBE.
// Cover-colour watches must NOT sit in a page's `Render()`: a graded batch would then re-render the whole Artist/Detail/Home tree,
// including every shelf in it. Here the subscription lives in a LEAF, and the paint-only tint lives in a bound `Fill`, so a landed
// grading re-renders one node (or marks one tile PaintDirty) and nothing else.
//
// All four take a re-pushed PROPS record (`Embed.Comp(props, factory)` + `UseProps<T>()`; the records are the contract's,
// `Theming\Contracts\MaterialProps.cs`) rather than frozen constructor fields, because every one of their inputs changes while they
// stay mounted. The pages mount them through the helpers in `Entities/Palette.Host.cs`, which mount the view keys
// (`Shell.Material.PageTone`, `.ArtistBlend`, `.ArtistVeil`, `.TintBinder`) with the page's own `Key`.
//
// The classes are moved verbatim; the only edits are the props types and the tint binder's write, which goes through the
// `MaterialPort` of its props (`ShellMaterial.Publish` on the slot the page read from its context).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>THE detail page's flat art-derived ground: one translucent art-derived plane behind both hero arms.
///
/// <para>This plane is a PAGE-ROOT SIBLING of the scrolling page, never a child of it, which is what makes the sticky
/// context band's offset model work: the band paints nothing, content is clipped at its lower edge, and what shows in
/// that gap is this node — the record's own tone.</para>
///
/// <para><b>ONE FLAT TONE, AND NOTHING SAMPLED FROM THE COVER. There is no blurred-artwork band here any more, and this
/// paragraph is the tombstone.</b> A "background extension" used to sit on top of this plane — a scaled,
/// 1.35×-saturated, 72σ baked-blur copy of the cover across the hero band, masked away at its lower edge — and it was
/// the one layer in the app whose loudness depended on the ARTWORK rather than on the theme. Its alpha was tuned three
/// times chasing that (a flat 0.40, then a flat 0.32 in light, then a luminance-adaptive 0.34→0.14 in dark) and it
/// still bloomed: a dark sleeve made it invisible and a bright one made it a haze across the top of the page, so two
/// records rendered as two different DESIGNS — reported as "the pages are inconsistent by kind" when what actually
/// differed was cover brightness. It also contradicted the rule the rest of the system is built on:
/// <see cref="ColorScience.PageTone"/>'s first clause is <i>complementary, not sampled</i>, and a blurred copy of the
/// cover at page scale is exactly the forbidden move, sitting directly on the plane that forbids it. Do not reintroduce
/// it; the tone tests assert its absence.</para></summary>
internal sealed class ClassicCoverTone : Component
{
    /// <summary>How much of the tone plane covers the Mica stack beneath it. The dial between "the page reads as the
    /// record's colour" and "the page is part of a Mica window", and the answer is firmly the SECOND: the standing
    /// direction is <i>mostly Mica</i>.
    ///
    /// <para><b>The ratchet, in order, because every step of it was a user report and the direction never reversed.</b>
    /// 1.0 (opaque) read as a dead slab. 0.72 still read as a slab — it passed only ~28% of Mica's wallpaper signal.
    /// 0.45 dark / 0.90 light passed ~55% / ~10%, which survived the "slab" objection but was still described as
    /// looking "solid" once the blurred artwork band was deleted and the flat tone was all that remained. So the plane
    /// is now a WHISPER over the material rather than a surface on top of it: at 0.20 it passes ~80% of dark Mica, i.e.
    /// the detail pages are deliberately MORE Mica than Home is.</para>
    ///
    /// <para>LIGHT no longer stays high. It was 0.90 to stop a loud wallpaper shifting the page's read, and that
    /// argument is real but subordinate: a near-white whisper tone (L 0.94, S ≤ 0.16) over near-white Mica has almost no
    /// hue to protect, so what 0.90 was mostly buying was flatness. THE HONEST CONSEQUENCE, stated so it is not
    /// rediscovered as a bug: <b>in light theme the record's hue is close to imperceptible</b> and a busy wallpaper
    /// shows through. Light identity is carried by the CHROME accent (the Play capsule, the accent rule, row chrome),
    /// not by the page ground. Raise THIS pair, never the clamp, if the page should take more colour again.</para></summary>
    // The pair itself is `R.Wash.PageTonePlaneAlpha` (themes plan T2b).

    public override Element Render()
    {
        var p = UseProps<CoverToneProps>();

        // The watch subscriptions, resolved ONCE per render and read here. Hoisting them out of the Fill closure
        // matters: a bound brush is re-evaluated on the PAINT path. Render still has to subscribe (not just the brush)
        // because the ARRIVAL of a grading is what decides whether this node exists at all — a page with no tone paints
        // nothing.
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = Palette.Watch(fb).Value;

        ColorF? resolved = p.Disabled ? null : Resolve(p);
        if (p.Disabled || resolved is null)
            return new BoxEl { Grow = 1f, HitTestVisible = false };

        // At most ONE child, and only in hero-only mode: the tone BAND that fades back to the neutral surface. The
        // default arm paints the plane's own flat fill and nothing else.
        Element[] kids = p.HeroOnly && HeroOnlyVeil(p) is { } veil ? [veil] : [];

        return new BoxEl
        {
            ZStack = true, Grow = 1f, HitTestVisible = false,
            ClipToBounds = true, Corners = Design.Size.ContentPaneCorners,
            // BOUND: the brush stays a compositor value, so a theme/preset re-fire lands without this subtree being
            // rebuilt, and the 250 ms ramp CROSS-FADES a grading arrival instead of snapping to it.
            Fill = Prop.Of(() => !p.HeroOnly && Resolve(p) is { } t
                ? t with { A = R.Wash.PageTonePlaneAlpha }
                : ColorF.Transparent),
            BrushTransitionMs = Design.Motion.Standard,
            Children = kids,
        };
    }

    /// <summary>Cover grading → the page's ground. Cheap: two table probes and the clamp; no subscription (the caller
    /// owns that), so it is safe on the paint path.</summary>
    static ColorF? Resolve(CoverToneProps p)
    {
        var wash = R.Wash;
        if ((Design.SchemeFor(p.Url) ?? Design.SchemeFor(p.FallbackUrl)) is { } scheme
            && wash.PageToneFromHue(ColorScience.Accent(scheme)) is { } fromArt)
            return fromArt;
        if (p.PayloadAccent == 0) return null;
        return wash.PageToneFromHue(ColorScience.ToColor(p.PayloadAccent));
    }

    /// <summary>Hero-only mode: the tone paints ONLY the hero band and fades to nothing below it. Under the
    /// translucent-plane model this is a tone BAND, not a ground-overpaint: below the fade the page is simply the
    /// unpainted content stack, the same breathing surface every other page has.
    /// <para>The Settings row that turned this on ("Limit page color to the hero") is GONE and is not to be reinstated;
    /// the arm survives because a caller may still compose it.</para></summary>
    static Element? HeroOnlyVeil(CoverToneProps p)
    {
        float pageH = p.PageHeight > 1f ? p.PageHeight : 0f;
        if (pageH <= 1f) return null;
        if (Resolve(p) is not { } tone) return null;
        float alpha = R.Wash.PageTonePlaneAlpha;
        float start = Math.Clamp(p.BackdropBand / pageH, 0.12f, 0.80f);
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


/// <summary>The artist page's blend wash. Height and boundary come from the CALLER (owner N's hero layout) so this file
/// carries no artist-page arithmetic.</summary>
internal sealed class ClassicCoverBlend : Component
{
    bool _mounted;
    /// <summary>Has the ladder ever produced a DEFINITIVE first answer (a real grading hit, or Definite because
    /// nothing better can ever arrive)? See the Exit guard below — the whole point of this field.</summary>
    bool _settled;

    public override Element Render()
    {
        var p = UseProps<CoverBlendProps>();
        if (p.Disabled) return new BoxEl();

        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        var pagePal = Design.SchemeFor(p.Url);
        // Both arms follow the ladder (graded → payload → neutral): the style's `R.Wash.BlendWashLadder`. Classic's LIGHT arm lifts the hue
        // toward the accent; its DARK arm sinks it to the dark background role, so the first frame already carries the record's hue at
        // the brightness the grading will settle on instead of a grey that brightens a beat later.
        bool definite = string.IsNullOrEmpty(p.Url) || !Palette.CanGrade(p.Url);
        var roles = R.Wash;
        AccentLadder.Result result = roles.BlendWashLadder(pagePal, p.PayloadAccent, definite);
        ColorF wash = result.Color;
        AccentLadder.Rung rung = result.Rung;
        GradientSpec gradient = GradientDown(
            new GradientStop(0f, wash with { A = roles.BlendWashTop }),
            new GradientStop(p.Boundary, wash with { A = roles.BlendWashBoundary }),
            new GradientStop(1f, wash with { A = 0f }));
        // Keyed swap, not a bound Fill: the recorder writes a Gradient directly (Reconciler.cs SetGradient), so
        // BrushTransitionMs cannot cross-fade it. Re-keying on the rung + colour makes a grading arrival mount a fresh
        // node that fades in over the old one instead of snapping. The key lives on a CHILD, not this component's
        // returned root — ReconcileSingleChild treats a component-root key as inert (Reconciler.cs), so only a real
        // Children-list slot honours it.
        // The fade is for a RE-KEY (a grading landing over the first tone), never for the first paint: the hero
        // must not brighten in over its first frames.
        //
        // PRE-SETTLEMENT EXIT GUARD — see VeilSettlement's doc for the defect and the fix.
        var (settled, exitAnimates) = VeilSettlement.Advance(_settled, rung, definite);
        Element tone = new BoxEl
        {
            Key = "artist-wash-tone:" + (byte)rung + ":" + wash.GetHashCode().ToString("X8"),
            HitTestVisible = false, Gradient = gradient,
            // …AND THE ENTER WITH IT. Suppressing only the Exit still leaves the incoming node fading 0->1 over a
            // gap where the outgoing one has already gone - the photo shows through UNVEILED for the length of
            // the fade, which is a second flash in place of the first (lighter, and the title's contrast drops
            // with it). A pre-settlement re-key is a CORRECTION, not a transition: it swaps in one frame and is
            // meant to be imperceptible. Once settled, a genuine later change cross-fades exactly as before.
            Enter = _mounted && exitAnimates ? new EnterExit(Opacity: 0f, Active: true) : null,
            Exit = exitAnimates ? new EnterExit(Opacity: 0f, Active: true) : null,
            Transition = MotionTok.ControlNormal,
        };
        _mounted = true;
        _settled = settled;
        return new BoxEl { ZStack = true, Height = p.Height, HitTestVisible = false, Children = [tone] };
    }
}

/// <summary>The artist hero's photography veil — cover-keyed so a late grading does not rebuild the banner. Also the
/// Home daylist card's veil over its full-bleed header art (`Daylist.UI.cs`).
/// <para><b>Two sizing arms.</b> A finite <c>Props.Width</c>/<c>Props.Height</c> is the SIZED arm (the
/// artist hero hands it its banner's extent). <c>float.NaN</c> for both is the STRETCH arm, for a surface whose height is
/// its content's (the daylist card): the component anchor mirrors the NaN extent, so a ZStack parent hands it the whole
/// slot, and the root's <c>Grow 1</c> fills the anchor's column on the main axis (its cross axis stretches by default).
/// The veil measures nothing in either arm, so it never sizes its parent.</para></summary>
internal sealed class ClassicCoverVeil : Component
{
    static readonly Func<uint, ColorF> s_lift = static a => ColorScience.Lift(ColorScience.ToColor(a));
    bool _mounted;
    /// <summary>See the identical field on <see cref="ClassicCoverBlend"/> and the Exit guard below — this leaf
    /// veils the same photo the wash sits behind, so it carries the same defect and the same fix.</summary>
    bool _settled;

    public override Element Render()
    {
        var p = UseProps<CoverVeilProps>();
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        var pagePal = Design.SchemeFor(p.Url);
        // The page palette's own lifted accent is the graded rung; the payload (header) accent and the neutral
        // FillLayerDefault (ArtistHeroVeil then lerps into the layer colour itself = no tint) fill the rest of the
        // ladder — the old chrome-scheme fallback is gone, subsumed by the shared ladder.
        ColorF? graded = pagePal is { } wp ? ColorScience.Lift(ColorScience.Accent(wp)) : null;
        bool definite = string.IsNullOrEmpty(p.Url) || !Palette.CanGrade(p.Url);
        var result = AccentLadder.Resolve(new(graded, p.PayloadAccent, definite), null, Tok.FillLayerDefault, s_lift);
        // Keyed swap: see ClassicCoverBlend — a Gradient can't cross-fade through BrushTransitionMs, and a
        // component-root Key is inert (ReconcileSingleChild), so the keyed node is a CHILD.
        //
        // PRE-SETTLEMENT EXIT GUARD — see VeilSettlement's doc for the defect and the fix.
        var (settled, exitAnimates) = VeilSettlement.Advance(_settled, result.Rung, definite);
        Element veil = new BoxEl
        {
            Key = "artist-veil-tone:" + (byte)result.Rung + ":" + result.Color.GetHashCode().ToString("X8"),
            HitTestVisible = false, Gradient = Controls.ArtistHeroVeil(result.Color, p.Vertical),
            // Fade only a re-key, never the first paint - and never a PRE-SETTLEMENT re-key either: see the sibling
            // in ClassicCoverBlend. An Enter with no Exit beneath it unveils the photo for the length of the fade.
            Enter = _mounted && exitAnimates ? new EnterExit(Opacity: 0f, Active: true) : null,
            Exit = exitAnimates ? new EnterExit(Opacity: 0f, Active: true) : null,
            Transition = MotionTok.ControlNormal,
        };
        _mounted = true;
        _settled = settled;
        // Grow only in the STRETCH arm (NaN height): the sized arm keeps Grow 0, exactly as before.
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, Grow = float.IsNaN(p.Height) ? 1f : 0f,
            ZStack = true, HitTestVisible = false, Children = [veil],
        };
    }
}

/// <summary>The shell material TINT publisher. Watches one cover; writes <see cref="ShellMaterialState"/> without the
/// page's own Render subscribing to that watch. Flat arm only (<c>Wash: null</c>) — detail/artist pages never publish
/// the radial three-layer wash, which belongs to Home.
/// <para>Its own node is a 0 × 0, hit-test-free box: it renders NOTHING. It exists to own a subscription and an effect
/// at LEAF scope.</para></summary>
internal sealed class ClassicCoverTint : Component
{
    // The two tint arms are the style's `R.Wash.ShellTintFromScheme` (Classic: LIGHT lifts the cover's TEXT role to a 5% whisper; DARK takes
    // the tinted background role at 14%: two different ROLES, not one role at two alphas) and `R.Wash.ShellTintAlpha` for a raw payload
    // colour. The tint WARMS the ground; it never replaces it.

    public override Element Render()
    {
        var p = UseProps<CoverTintProps>();
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = Palette.Watch(fb).Value;

        var coverArt = p.Ready ? Design.SchemeFor(p.Url) : null;
        var artPalette = coverArt ?? (p.Ready ? Design.SchemeFor(p.FallbackUrl) : null);
        // DEFINITE "no colour": the page has DECIDED to opt out (colour washes off in Settings, or this layout applies
        // no tint) — as opposed to simply not having a grading YET, which is transient and HOLDS the current colour
        // rather than dipping to neutral and back.
        bool definite = p.Disabled || !p.Apply;
        ColorF? known = null;
        if (!definite)
        {
            var wash = R.Wash;
            if (artPalette is { } artScheme)
            {
                known = wash.ShellTintFromScheme(artScheme);
            }
            else if (p.PayloadAccent != 0)
            {
                var lifted = ColorScience.Lift(ColorScience.ToColor(p.PayloadAccent));
                known = lifted with { A = wash.ShellTintAlpha };
            }
        }

        // "Have I EVER published": the first publish is the mount's claim (neither activation callback fires at mount);
        // a reactivation claims through onActivated explicitly.
        var claimedOnce = UseRef(false);

        void Publish(bool isClaim) => p.Port.Publish(p.Owner, isClaim, definite, known, wash: null);

        // `Tok.Epoch` is IN THE KEY on purpose: a live theme flip (kind) or a definition swap that moves a wash role re-derives the tint
        // and re-publishes it. Without it the chrome keeps the OLD tint until the next navigation. (The epoch, not the light/dark kind:
        // themes plan X44.)
        UseEffect(() =>
        {
            Publish(isClaim: !claimedOnce.Value);
            claimedOnce.Value = true;
        }, DepKey.From(HashCode.Combine(p.Url, known.HasValue, known.GetValueOrDefault(), Tok.Epoch,
                                        p.Ready, p.Disabled, p.Apply, p.PayloadAccent)));
        // Reactivation (KeepAlive Back/forward) is ALWAYS a claim, regardless of claimedOnce — the whole point is to
        // retake the slot from whatever deactivated in between, even if that never cleared it either.
        UseActivation(onActivated: () => Publish(isClaim: true));

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
