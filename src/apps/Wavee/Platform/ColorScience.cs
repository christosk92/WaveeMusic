// ── Platform/ColorScience.cs — the colour maths every style shares, with explicit kind and alpha parameters (themes plan P1) ──
//
// Role: CORE (pure)
//
// THEMES PLAN T2b. This is what was `Design.Palette` (the ARGB maths), the shell-ground and content-step maths of `Design.Colors`,
// and the art-placeholder blend of `Design`. Every member here is a PURE function: a theme KIND, a card surface, a fallback colour
// and a token set arrive as ARGUMENTS, and nothing here reads `Tok.*` or an entity. That is what lets the value tests drive both
// kinds without mutating global theme state, and what lets a style (`R.Wash.PageToneFromHue`, `R.Layer.ShellGround`) choose a recipe
// without the core knowing which theme runs it. The members that bind the maths to the ACTIVE theme (`Design.Palette.TextInk(seed)`,
// `ChromeAccent(scheme)`) are thin and stay in `Design.Palette`.
//
// A style that wants another answer gives another ROLE (`WashRoles.PageToneFromHue`, a `ColorRole` recipe); it does not edit
// the maths below. The clamp constants here are the Classic recipe's constants, not tuning knobs: if a page should take more colour,
// raise the PLANE ALPHA pair (`R.Wash.PageTonePlaneAlphaAt`), never the page-tone clamp.
//
// THE RULE THAT SURVIVES THE MOVE: the 22-iteration bisection in `Hairline` stays 22 iterations. It is the contrast solve; a
// cheaper approximation changes every identity hairline and every accent-ink digit in the app.
//
// `AccentLadder` and `AccentHold` (the graded, payload, held, default ladder) live in this file as well: they are the same kind of
// code (pure, theme-free) and their names are unchanged.
//
// ── THE TWO GRADING HALVES, AND WHICH ONE A SURFACE TAKES ────────────────────────────────────────────────────────────────
// Every cover is graded TWICE by the provider, a light half and a dark half, and `Design.SchemeFor` / `Design.ChromeSchemeFor` read
// them in opposite directions. THE RULE IN ONE LINE: if the app's own ink lands ON it, grade it FOR the theme; if it carries on-accent
// ink and has to be seen, grade it AGAINST the theme.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Pure colour maths: the shell ground and content step, the art-placeholder blend, and the cover-colour roles (lift, vivid,
/// the hairline and text-ink contrast solves, the page-tone clamp, the data-hue ink). See the file header.</summary>
public static class ColorScience
{
    // ── the shell ground and the content step ──────────────────────────────────────────────────────────────────────────────

    // The dark ground's lightness (the palette builder's Tinted(0.125, …) canvas) and the lift that takes it to
    // #282828. Expressed as a mix-toward-white FRACTION rather than a literal grey, so a tinted preset canvas would
    // take the same perceptual step instead of being flattened back to neutral; at chroma 0 it lands on exactly
    // 40/255 per channel.
    const float DarkGroundL = 0.125f;
    const float DarkContentLift = (40f / 255f - DarkGroundL) / (1f - DarkGroundL);

    // The LIGHT ground's DROP off the stock canvas, in the same idiom mirrored to the other mix endpoint: the lift
    // above is (target − L)/(1 − L) because Lighten mixes toward WHITE; Darken mixes toward BLACK, so the matching
    // fraction is (L − target)/L. The neutral light canvas is #F3F3F3 (243/255) and the target is #EDEDED (237/255)
    // — the bare Mica Alt tone the reference shell carries as its DARKEST chrome band. So the drop is 6/243 ≈ 2.469%.
    const float LightGroundL = 243f / 255f;
    const float LightGroundDrop = (LightGroundL - 237f / 255f) / LightGroundL;

    // The content step as a TRANSLUCENT white layer: source-over of white@α onto a ground G lands on G + α(1−G) —
    // the same mix-toward-white the opaque rung uses, so α IS the lift fraction. Dark reuses DarkContentLift
    // verbatim; light solves (249−237)/(255−237) = 2/3 against the #EDEDED ground.
    const float LightContentLayerA = (249f / 255f - 237f / 255f) / (1f - 237f / 255f);

    /// <summary>The chrome GROUND for an arbitrary token set: the full-bleed opaque rect the whole authenticated shell sits on. Light is
    /// the stock solid base #F3F3F3 dropped to #EDEDED — #F3F3F3 read as PAGE, not chrome. Dark keeps #202020 verbatim: it is already
    /// that band. The gates resolve a palette that is not the active one without mutating global theme state.</summary>
    public static ColorF ShellGround(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
        ? ColorRamp.Darken(set.FillSolidBase, LightGroundDrop)
        : set.FillSolidBase;

    /// <summary>The OPAQUE content rung — ONE step above the ground. NOT what the docked shell paints any more: this is the
    /// wallpaper-independent stand-in every FLOATING content surface takes, and the value the palette gates flatten the translucent
    /// ladder against. Light resolves to #F9F9F9 over the #EDEDED ground; dark is a deliberate #282828 over the #202020 ground (the dark
    /// solid-tertiary rung lifts 6% to #2D2D2D, which reads as a second card rather than as the page).</summary>
    public static ColorF ContentSurface(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
        ? set.FillSolidTertiary
        : ColorRamp.Lighten(set.FillSolidBase, DarkContentLift);

    /// <summary>The content step expressed as a translucent white LAYER. It composites to <see cref="ContentSurface"/> over the bare ground
    /// AND lands on the TINTED equivalent over a page's published shell tint, which an opaque fill cannot (it would punch a neutral hole
    /// through the wash). The identity <c>Over(ContentLayer, ShellGround) == ContentSurface</c> is what a fallback or a gate reasons
    /// with in either space, and it is pinned by test.</summary>
    public static ColorF ContentLayer(ThemeKind theme) => ColorF.FromRgba(0xFF, 0xFF, 0xFF,
        (byte)((theme == ThemeKind.Light ? LightContentLayerA : DarkContentLift) * 255f + 0.5f));

    // ── the art placeholder ────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // NON-NEGOTIABLE 9: every art slot is a SOLID, cover-tinted tile before its bitmap lands — never a hole, never a grey wall. The
    // tile is the neutral placeholder (a layer role, `R.Layer.ArtPlaceholder`) lerped toward the cover's own graded colour, resolved
    // through an image-keyed plane that ENQUEUES ON A MISS (`Design.PlaceholderFor`), so rendering the art IS the request.

    /// <summary>How far a cover's own colour pulls the placeholder away from the neutral tile. Full strength would make a long list read
    /// as a wall of saturated blocks; this keeps the slot legible as "art loading" while still being THAT cover's colour. It is the
    /// difference between a list of grey squares and one that paints its covers at once.</summary>
    public const float TintStrength = 0.55f;

    /// <summary>The placeholder tile: <paramref name="neutral"/> lerped toward a cover's graded tint (<paramref name="tintArgb"/>).</summary>
    public static ColorF Placeholder(ColorF neutral, uint tintArgb) => ColorF.Lerp(neutral, ToColor(tintArgb), TintStrength);

    // ── the cover-colour maths (was Design.Palette) ──────────────────────────────────────────────────────────────────────────

    const float HairlineSaturationCeiling = 0.50f;

    /// <summary>The quiet identity hairline's contrast against its card (the 22-iteration solve's target).</summary>
    public const float HairlineContrast = 3.25f;

    /// <summary>Below this HSV saturation a colour has no hue worth amplifying — grading a genuinely greyscale cover
    /// would just produce a random tint. Callers fall back to the semantic accent instead.</summary>
    public const float NeutralS = 0.08f;

    /// <summary>WCAG AA text contrast. <see cref="Hairline(ColorF, ThemeKind, ColorF, float)"/> at <see cref="HairlineContrast"/> is the quieter 3.25:1 rule; this is the same
    /// hue solve at the text threshold. Chrome FILLS are plates — painting them as ink on a wash mixed from the same
    /// hue is what made a countdown unreadable.</summary>
    public const float TextContrast = 4.5f;

    public static ColorF ToColor(uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        return ColorF.FromRgba(r, g, b, a);
    }

    /// <summary>Brighten a colour so its strongest channel reaches <paramref name="targetMax"/> (0-255), scaling RGB
    /// uniformly to preserve hue — only ever LIFTS, never darkens. The provider's dark background role is often
    /// near-black and collapses to nothing as a faint tint; this keeps it legible.</summary>
    public static ColorF Lift(ColorF c, byte targetMax = 210)
    {
        float target = targetMax / 255f;
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        if (max <= 0.001f) { float v = target; return new ColorF(v, v, v, c.A); }   // pure black → neutral grey at the target
        if (max >= target) return c;                                                // already bright enough — don't darken
        float k = target / max;
        return new ColorF(MathF.Min(1f, c.R * k), MathF.Min(1f, c.G * k), MathF.Min(1f, c.B * k), c.A);
    }

    /// <summary>Saturation FLOOR for a colour used as a CHROME FILL (the Play capsule, the Verified pill, the accent
    /// bars). <see cref="Lift"/> only raises brightness, so a cover graded to a washed pastel stayed washed and the
    /// CTA read as a grey plate. This pushes S up at constant HUE and keeps V at or above the same 210 ceiling Lift
    /// targets — so after a Lift the V clamp is a no-op and only S moves. Near-neutrals are returned UNCHANGED.
    /// <para>Raising S at constant V LOWERS relative luminance, so an accent used as TEXT on a light card gets
    /// slightly darker (better), and the contrast picker keeps picking the legible ink on fills.</para></summary>
    public static ColorF Vivid(ColorF c, float minS = 0.55f, byte targetMax = 210)
    {
        var (h, s, v) = c.ToHsv();
        if (s <= NeutralS) return c;
        return ColorF.FromHsv(h, MathF.Max(s, minS), MathF.Max(v, targetMax / 255f), c.A);
    }

    /// <summary>A quiet identity hairline solved against the actual card surface. Saturation is capped, then value is
    /// binary-solved to a contrast ratio in the theme-appropriate direction. Deliberately separate from
    /// <see cref="Lift"/>/<see cref="Vivid"/>: chrome fills want brightness and chroma; a 2-DIP rule does not.
    /// <para>The caller supplies the kind and the flattened card surface (<c>Design.Palette.TextInk</c> reads them from the active
    /// theme and its layer roles); the contrast tests drive both kinds without mutating global theme state.</para>
    /// <para>THE 22 ITERATIONS ARE THE CONTRAST SOLVE (rule 3 of the old Design header). Contrast is monotonic along V for a
    /// fixed hue/saturation, so a bisection converges; a cheaper approximation changes every identity hairline and
    /// every accent-ink digit in the app.</para></summary>
    public static ColorF Hairline(ColorF seed, ThemeKind theme, ColorF cardBackground,
                                  float targetContrast = HairlineContrast)
    {
        var (h, s, _) = seed.ToHsv();
        s = MathF.Min(s, HairlineSaturationCeiling);

        // Dark cards search from black to white and retain the LIGHTER solution; light cards search the same
        // interval and retain the DARKER one.
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 22; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float ratio = ColorContrast.Ratio(ColorF.FromHsv(h, s, mid, seed.A), cardBackground);
            if (theme == ThemeKind.Dark)
            {
                if (ratio < targetContrast) lo = mid; else hi = mid;
            }
            else
            {
                // On a light card, contrast FALLS as V rises.
                if (ratio < targetContrast) hi = mid; else lo = mid;
            }
        }
        float v = theme == ThemeKind.Dark ? hi : lo;
        return ColorF.FromHsv(h, s, v, seed.A);
    }

    /// <summary>Accent as TEXT on a card-like surface: the same hue as <see cref="Hairline(ColorF, ThemeKind, ColorF, float)"/>, solved to
    /// <see cref="TextContrast"/>. Some hues cannot hit AA at the hairline saturation cap (a mid-blue on a dark card tops out
    /// about 4.4:1); those fall back to the contrast picker so the digits stay readable.</summary>
    public static ColorF TextInk(ColorF seed, ThemeKind theme, ColorF cardBackground)
    {
        var ink = Hairline(seed, theme, cardBackground, TextContrast);
        return ColorContrast.MeetsAaText(ink, cardBackground) ? ink : ColorContrast.PickContrast(cardBackground);
    }

    /// <summary>Chrome fill from a raw wire colour (a pathfinder extracted colour before the plane has graded the
    /// art). Same <see cref="Lift"/> THEN THE SAME <see cref="Vivid"/> saturation floor <see cref="ChromeAccent"/>
    /// applies — this rung and the graded rung are consumed by the SAME control (<c>Detail.AccentFor</c>'s Play
    /// pill), and the header payload accent routinely paints for a beat before the async grading lands. Without
    /// the floor here, that beat is a WASHED payload hue snapping to a saturation-floored graded hue the moment
    /// grading lands — the muted-salmon-then-salmon flash this fixes. 0 (no payload) is the semantic accent
    /// (<paramref name="fallback"/>), not a fabricated hue.</summary>
    public static ColorF ChromeFromPayload(uint argb, ColorF fallback)
    {
        if (argb == 0) return fallback;
        var lifted = Lift(ToColor(argb));
        var (_, sat, _) = lifted.ToHsv();
        return sat <= NeutralS ? fallback : Vivid(lifted);
    }

    /// <summary>Dark-theme wash from a raw wire colour: the payload's hue sunk to the brightness of the neutral
    /// scheme's dark background role, so a first paint before the grading lands sits where the graded
    /// <see cref="BackgroundDark"/> will. 0 (no payload) is the neutral dark base itself.</summary>
    public static ColorF DarkFromPayload(uint argb)
    {
        ColorF ground = BackgroundDark(Neutral);
        if (argb == 0) return ground;
        ColorF c = ToColor(argb);
        float target = MathF.Max(ground.R, MathF.Max(ground.G, ground.B));
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        if (max <= 0.001f) return ground;
        float k = target / max;
        return new ColorF(c.R * k, c.G * k, c.B * k, c.A);
    }

    /// <summary>THE page's chrome accent — the one derivation every accent-filled control on a media surface uses:
    /// the cover's most-saturated graded role (<see cref="Accent(in Scheme)"/>), brightness-lifted then
    /// saturation-floored. Greyscale/near-monochrome art has no hue to amplify, so it falls back to the system
    /// accent rather than shipping a grey Play button.
    /// <para>Deliberately NOT used for the hero WASHES: those keep the plain <see cref="Lift"/>ed accent, so wash
    /// strength (alpha) and chrome chroma (saturation) stay independent axes.</para></summary>
    public static ColorF ChromeAccent(in Scheme s, ColorF fallback)
    {
        var lifted = Lift(Accent(s));
        var (_, sat, _) = lifted.ToHsv();
        return sat <= NeutralS ? fallback : Vivid(lifted);
    }

    /// <summary>The page accent's CONTRAST FLOOR (tuned Classic D13): an artwork accent is the ink of the hearts and the
    /// accent text AND the fill of the Play button, so it must pass <see cref="TextContrast"/> both ways: as ink against
    /// the page <paramref name="ground"/>, and under its own legible label ink (<see cref="ColorContrast.PickContrast(in ColorF)"/>).
    /// A failing accent steps along its own ramp (<see cref="AccentRamp"/>: Light1-3 on a dark ground, Dark1-3 on a light
    /// one, the direction that raises both ratios) and the first rung that passes wins; when none does, the
    /// <paramref name="system"/> accent stands in. A passing accent is returned unchanged. Pure.</summary>
    public static ColorF AccentFloor(ColorF accent, ThemeKind theme, ColorF ground, ColorF system)
    {
        if (PassesFloor(accent, ground)) return accent;
        var ramp = AccentRamp.Derive(accent);
        ReadOnlySpan<ColorF> rungs = theme == ThemeKind.Dark
            ? [ramp.Light1, ramp.Light2, ramp.Light3]
            : [ramp.Dark1, ramp.Dark2, ramp.Dark3];
        foreach (var rung in rungs)
        {
            var c = rung with { A = accent.A };
            if (PassesFloor(c, ground)) return c;
        }
        return system;
    }

    /// <summary><see cref="AccentFloor"/>'s test: ink on the ground and label on the fill, both at <see cref="TextContrast"/>.</summary>
    public static bool PassesFloor(ColorF accent, ColorF ground)
        => ColorContrast.MeetsAaText(accent, ground, TextContrast)
           && ColorContrast.MeetsAaText(ColorContrast.PickContrast(accent), accent, TextContrast);

    /// <summary>The cover's HUE — the most saturated of the graded roles, in preference order
    /// <c>BackgroundTintedBase → BackgroundBase → TextSubdued → TextBrightAccent</c> (strict <c>&gt;</c>, so a tie
    /// keeps the earlier, higher-preference role).
    ///
    /// <para><b>Why not simply <c>TextBrightAccent</c>, which its name promises:</b> in the real payloads that role
    /// is the CONTRAST-GRADED INK, not a hue — pure <c>#FFFFFF</c> in every dark half and pure <c>#000000</c> in
    /// every light half, verified over 9,316 cached gradings (100%). Reading it as "the accent" made
    /// <see cref="ChromeAccent"/>'s <see cref="NeutralS"/> guard fire on EVERY cover, so every Play CTA in the app
    /// rendered system blue. The cover's actual chroma lives in the BACKGROUND roles (dark tinted base: median HSV
    /// S ≈ 0.73) and, in the light half, in <c>TextSubdued</c> (median S ≈ 0.45). It stays in the list — LAST — so a
    /// future feed that does grade it chromatically still wins wherever it is the most saturated role.</para>
    ///
    /// <para>A <c>Span</c> collection expression: stack-allocated, so the hottest palette function stays
    /// allocation-free on the render path (P1).</para></summary>
    public static ColorF Accent(in Scheme s)
    {
        Span<uint> roles = [s.BackgroundTintedBase, s.BackgroundBase, s.TextSubdued, s.TextBrightAccent];
        ColorF best = default;
        float bestS = -1f;
        for (int i = 0; i < roles.Length; i++)
        {
            var c = ToColor(roles[i]);
            var (_, sat, _) = c.ToHsv();
            if (sat > bestS) { bestS = sat; best = c; }
        }
        return best;
    }

    /// <summary>The two RAW role reads — no lift, no clamp. The artist blend wash's dark arm and the shell tint's
    /// dark arm are the only callers; everything else goes through <see cref="Accent(in Scheme)"/> /
    /// <see cref="PageTone"/> / <see cref="ChromeAccent"/>.</summary>
    public static ColorF BackgroundDark(in Scheme s) => ToColor(s.BackgroundBase);
    /// <inheritdoc cref="BackgroundDark"/>
    public static ColorF TintedDark(in Scheme s) => ToColor(s.BackgroundTintedBase);

    // ── THE PAGE TONE ────────────────────────────────────────────────────────────────────────────────────────────
    //
    // The detail pages' ONE art-derived surface. Two rules, and they are the whole contract:
    //
    //   1. COMPLEMENTARY, NOT SAMPLED. The tone takes the cover's HUE and nothing else. It is not the cover's
    //      dominant colour re-painted at page scale — that is what produced pages the artwork could not be told
    //      apart from. Saturation and lightness are the PAGE'S, not the record's, so two albums by the same artist
    //      read as the same PAGE in two different colours rather than as two different apps.
    //   2. THE CLAMP IS THE POINT. Apple Music's unclamped version of this is its single loudest complaint: a
    //      saturated cover produces a page that is genuinely hard to read, and a dark cover produces one that is
    //      indistinguishable from every other dark cover's. So lightness is FORCED (never sampled) and saturation is
    //      CAPPED. The forced lightness is also what makes the standard `Tok` ink tokens correct on this plane in
    //      BOTH themes — there is no on-media ladder on these pages, because polarity is guaranteed by construction
    //      rather than measured per cover.
    //
    // A cover with no hue worth using (greyscale art, a mosaic of monochrome tiles) gets the NEUTRAL tone instead of
    // an invented tint — the same decision ChromeAccent makes for the Play button, for the same reason.

    /// <summary>Below this HSV saturation the dominant graded role has no hue to build a page tone from.</summary>
    public const float PageToneChromaFloor = 0.12f;

    /// <summary>The forced HSL lightness of the dark tone, and the cap on its HSL saturation.</summary>
    public const float PageToneDarkL = 0.15f, PageToneDarkSMax = 0.30f;

    /// <summary>The forced HSL lightness of the light tone, and the cap on its HSL saturation.
    /// <para>THE LIGHT ARM IS A WHISPER, and the numbers say so. The first light clamp (L 0.89 / S ≤ 0.42) was the
    /// dark arm's clamp MIRRORED, and mirroring is exactly the mistake: the dark tone paints a hue at 15% lightness
    /// where 30% saturation is a suggestion, while 42% saturation at 89% lightness is a full pastel — a green cover
    /// landed on ≈#D7EFD7 and REPLACED the page's ground with a coloured plane. In light there is no headroom to
    /// spend: the ink is dark, the surfaces are near-white, and every chromatic step the ground takes is a step the
    /// ink and every card on top of it has to survive. So the light tone is the same idea at a tenth of the volume —
    /// L 0.94 (four points above Mica Alt's own #EDEDED, so the page still reads as a PAGE and not as chrome) with
    /// saturation capped at 0.16, which is enough for "this record is green" and not enough for "this app is
    /// green".</para></summary>
    public const float PageToneLightL = 0.94f, PageToneLightSMax = 0.16f;

    /// <summary>The hue-less answer: a near-black in dark, the canon neutral off-white in light.
    /// <para>The light value used to be a WARM off-white (#F6F4F1) on the argument that a neutral grey page reads as
    /// "unfinished" beside every tinted one. That argument was written against the old 0.42 saturation cap, where
    /// the tinted pages really were pastel. Under the whisper clamp above the tinted pages are barely tinted, so the
    /// warm tone stopped being the quiet member of a family and became the app's one un-asked-for colour cast. It is
    /// now #F5F5F5: achromatic, and within 5/255 of the clamp's own achromatic point (L 0.94 ⇒ #F0F0F0), so a
    /// greyscale sleeve and a hued one produce pages of the same brightness.</para></summary>
    public static ColorF PageToneNeutralDark { get; } = ColorF.FromRgba(0x15, 0x15, 0x15);
    /// <inheritdoc cref="PageToneNeutralDark"/>
    public static ColorF PageToneNeutralLight { get; } = ColorF.FromRgba(0xF5, 0xF5, 0xF5);

    // THE BLURRED-BACKDROP ALPHAS ARE GONE, and this note is the tombstone. A luminance-adaptive alpha pair existed
    // to keep the detail page's blurred cover band level across sleeves, because that band's loudness was roughly
    // its own luminance × its alpha. It was the only quantity in this file that was a function of the ARTWORK rather
    // than of the theme, and that is exactly why it never converged: three tunings and a bright sleeve still bloomed
    // while a dark one showed nothing, so the same page read as two different designs depending on the record. The
    // band itself is deleted; the page is now the clamped page tone alone.

    /// <summary>THE detail page's ground tone for a cover's grading, or null when there is no grading to build one
    /// from (the caller then paints NOTHING and the page keeps its neutral surface — a miss is a designed state, not
    /// a skeleton).</summary>
    public static ColorF? PageTone(Scheme? scheme, ThemeKind theme)
        => scheme is { } s ? PageToneFromHue(Accent(s), theme) : null;

    /// <summary>Page ground from a raw hue (payload <c>extractedColors</c>, a coverless mosaic's hex) using the same
    /// lightness/saturation clamp as a graded cover.</summary>
    public static ColorF? PageToneFromHue(ColorF dominant, ThemeKind theme)
    {
        var (_, hsvSat, _) = dominant.ToHsv();
        if (hsvSat < PageToneChromaFloor)
            return theme == ThemeKind.Dark ? PageToneNeutralDark : PageToneNeutralLight;
        var (h, sat, _) = ToHsl(dominant);
        return theme == ThemeKind.Dark
            ? FromHsl(h, MathF.Min(sat, PageToneDarkSMax), PageToneDarkL)
            : FromHsl(h, MathF.Min(sat, PageToneLightSMax), PageToneLightL);
    }

    // HSL, not HSV: the page tone's contract is stated in LIGHTNESS ("dark: L ≈ 15%"), and HSV's V is not lightness —
    // a fully saturated hue at V = 0.15 and a grey at V = 0.15 have very different perceived brightness. The
    // engine's ColorF publishes HSV only, so the two conversions live here, where the callers that need them are.

    /// <summary>RGB → HSL (hue in degrees).</summary>
    public static (float H, float S, float L) ToHsl(in ColorF c)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        float min = MathF.Min(c.R, MathF.Min(c.G, c.B));
        float l = (max + min) * 0.5f;
        float d = max - min;
        if (d <= 1e-6f) return (0f, 0f, l);
        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h = max == c.R ? (c.G - c.B) / d + (c.G < c.B ? 6f : 0f)
            : max == c.G ? (c.B - c.R) / d + 2f
            : (c.R - c.G) / d + 4f;
        h *= 60f;
        if (h >= 360f - 1e-3f) h = 0f;   // float noise just under a full turn is hue 0
        return (h, s, l);
    }

    /// <summary>HSL (hue in degrees) → RGB.</summary>
    public static ColorF FromHsl(float hDeg, float s, float l, float a = 1f)
    {
        s = Math.Clamp(s, 0f, 1f);
        l = Math.Clamp(l, 0f, 1f);
        if (s <= 0f) return new ColorF(l, l, l, a);
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float h = hDeg / 360f;
        h -= MathF.Floor(h);
        return new ColorF(Hue(p, q, h + 1f / 3f), Hue(p, q, h), Hue(p, q, h - 1f / 3f), a);

        static float Hue(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
    }

    // ── DATA-ENCODING INK ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Saturation and the two lightness rungs a data hue is forced to in LIGHT.</summary>
    const float DataDotLightS = 0.65f, DataDotLightLDim = 0.30f, DataDotLightLBright = 0.40f;

    /// <summary>The hue band whose members are intrinsically LIGHT — yellow through cyan.</summary>
    const float DataDotBandLo = 40f, DataDotBandHi = 200f;

    /// <summary>A SERVER-SUPPLIED data hue (the Camelot key wheel) rendered as ink for the CURRENT theme.
    ///
    /// <para>The wheel's colours arrive fully saturated and mid-to-high lightness — authored for a dark surface,
    /// where a 6-DIP saturated dot at 0.85 opacity is a quiet identity mark. Painted UNCHANGED on a near-white row
    /// the same dot is the loudest thing in the tracklist: a saturated yellow or cyan at L ≈ 0.5 has almost no
    /// contrast against #FCFCFC, so it reads as a smear of colour rather than as a token.</para>
    ///
    /// <para>The correction is Fluent's own shared-colour principle, and its awkward part is the part that matters:
    /// darkening for a light surface is HUE-DEPENDENT. Yellow and cyan sit near the top of the luminance curve and
    /// need roughly three rungs of darkening before they read as ink; red, blue and magenta need about one, and
    /// darkening them as far as yellow turns the wheel into twelve browns. So the band [40°, 200°] is forced to
    /// L 0.30 and everything else to L 0.40, at a common S 0.65 that keeps adjacent keys distinguishable (the
    /// wheel's whole point is that harmonically adjacent keys are adjacent hues).</para>
    ///
    /// <para>DARK IS A PASSTHROUGH, deliberately: the wire colours already ARE the dark-surface answer, and
    /// re-grading them would break the one property the wheel guarantees. An input with no hue worth keeping
    /// returns a NEUTRAL at the same lightness rung rather than being pushed to S 0.65, which would invent a red for
    /// a grey.</para></summary>
    public static ColorF DataDotInk(uint argb, ThemeKind theme)
    {
        var c = ToColor(argb);
        if (theme == ThemeKind.Dark) return c;
        var (h, s, _) = ToHsl(c);
        float l = h >= DataDotBandLo && h <= DataDotBandHi ? DataDotLightLDim : DataDotLightLBright;
        return s <= NeutralS ? FromHsl(0f, 0f, l, c.A) : FromHsl(h, DataDotLightS, l, c.A);
    }

    /// <summary>Neutral fallback when the plane has no grading yet. Every role is greyscale ON PURPOSE — including
    /// the bright-accent role, which the real wire always grades to pure white in the dark half. A fabricated blue
    /// here made the fallback scheme the one "cover" in the app with a hue, which is precisely the wrong shape to
    /// test chrome against.</summary>
    public static Scheme Neutral { get; } =
        new(BackgroundBase: 0xFF1C1C1C, BackgroundTintedBase: 0xFF2A2A2A, TextBase: 0xFFFFFFFF,
            TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);
}

// ── the accent ladder (moved here from Platform/AccentLadder.cs; the types are unchanged) ─────────────────────────────────

public static class AccentLadder
{
    public enum Rung : byte { Graded, Payload, Held, Default }

    /// <summary><paramref name="Definite"/>: the page already knows nothing better will arrive (no payload and a url
    /// that cannot be graded, or a fresh negative), so a held colour must not stand in.</summary>
    public readonly record struct Input(ColorF? Graded, uint Payload, bool Definite);

    public readonly record struct Result(ColorF Color, Rung Rung)
    {
        public bool Remember => Rung is Rung.Graded or Rung.Payload;
    }

    public static Result Resolve(in Input now, ColorF? held, ColorF fallback, Func<uint, ColorF> lift)
    {
        if (now.Graded is { } graded) return new Result(graded, Rung.Graded);
        if (now.Payload != 0) return new Result(lift(now.Payload), Rung.Payload);
        if (!now.Definite && held is { } h) return new Result(h, Rung.Held);
        return new Result(fallback, Rung.Default);
    }
}

/// <summary>The last remembered accent, process-wide. UI-thread state; only Graded/Payload answers are kept.</summary>
public static class AccentHold
{
    public static ColorF? Last { get; private set; }

    public static void Remember(in AccentLadder.Result r)
    {
        if (r.Remember) Last = r.Color;
    }

    public static void Reset() => Last = null;
}
