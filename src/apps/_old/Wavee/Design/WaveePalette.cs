using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.SpotifyLive;

namespace Wavee;

// The boundary mapper: the framework-neutral cover-colour roles (uint ARGB, from CoverColorPlane) → engine ColorF.
// This is the ONLY place a cover colour becomes a renderer colour. A page asks the plane for its cover's Scheme and
// maps the roles it needs here; nothing carries a per-entity palette any more.
public static class WaveePalette
{
    const float HairlineSaturationCeiling = 0.50f;
    const float HairlineContrast = 3.25f;
    const float HairlineHoverContrast = 3.55f;

    public static ColorF ToColor(uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        return ColorF.FromRgba(r, g, b, a);
    }

    /// <summary>Brighten a color so its strongest channel reaches <paramref name="targetMax"/> (0–255), scaling RGB
    /// uniformly to preserve hue — only ever lifts, never darkens. Spotify's extracted <c>colorDark</c> is often
    /// near-black and collapses to nothing as a faint tint/bar; this keeps it legible. (Port of WaveeMusic's BrightenForTint.)</summary>
    public static ColorF Lift(ColorF c, byte targetMax = 210)
    {
        float target = targetMax / 255f;
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        if (max <= 0.001f) { float v = target; return new ColorF(v, v, v, c.A); }   // pure black → neutral grey at the target
        if (max >= target) return c;                                                // already bright enough — don't darken
        float k = target / max;
        return new ColorF(MathF.Min(1f, c.R * k), MathF.Min(1f, c.G * k), MathF.Min(1f, c.B * k), c.A);
    }

    /// <summary>Below this HSV saturation a colour has no hue worth amplifying — grading a genuinely greyscale cover
    /// would just produce a random tint. Callers fall back to the semantic accent instead.</summary>
    public const float NeutralS = 0.08f;

    /// <summary>Saturation FLOOR for a colour used as a CHROME FILL (the Play capsule, the Verified pill, the accent
    /// bars). <see cref="Lift"/> only raises brightness, so a cover graded to a washed pastel stayed washed and the CTA
    /// read as a grey plate. This pushes S up to <paramref name="minS"/> at constant HUE and keeps V at or above
    /// <paramref name="targetMax"/> — the same 210 ceiling Lift targets, so after Lift the V clamp is a no-op and only
    /// S moves. Near-neutrals (S ≤ <see cref="NeutralS"/>) are returned UNCHANGED; see <see cref="ChromeAccent"/> for
    /// the greyscale-art fallback.
    ///
    /// Raising S at constant V LOWERS relative luminance, so an accent used as TEXT on a light card gets slightly
    /// darker (better), and <see cref="ColorContrast.PickContrast"/> keeps picking the legible ink on fills.</summary>
    public static ColorF Vivid(ColorF c, float minS = 0.55f, byte targetMax = 210)
    {
        var (h, s, v) = c.ToHsv();
        if (s <= NeutralS) return c;
        return ColorF.FromHsv(h, MathF.Max(s, minS), MathF.Max(v, targetMax / 255f), c.A);
    }

    /// <summary>A quiet identity hairline solved against the actual card surface. Saturation is capped, then value is
    /// binary-solved to a 3.25:1 contrast ratio in the theme-appropriate direction. This is deliberately separate from
    /// <see cref="Lift"/>/<see cref="Vivid"/>: chrome fills want brightness and chroma; a 2-DIP rule does not.</summary>
    public static ColorF Hairline(ColorF seed)
        => Hairline(seed, Tok.Theme,
            ColorContrast.Flatten(Tok.FillCardDefault, WaveeColors.FloatingPane), HairlineContrast);

    /// <summary>The hovered card's slightly stronger identity cue; 3.55:1 remains below the 4.5:1 text threshold.</summary>
    public static ColorF HairlineHover(ColorF seed)
        => Hairline(seed, Tok.Theme,
            ColorContrast.Flatten(Tok.FillCardSecondary, WaveeColors.FloatingPane), HairlineHoverContrast);

    /// <summary>WCAG AA text contrast. <see cref="Hairline"/> is the quieter 3.25:1 rule; this is the same hue
    /// solve at the text threshold. Chrome fills (<see cref="Lift"/>, <see cref="ChromeAccent"/>) are plates — painting
    /// them as ink on a wash mixed from the same hue is what made the Home daylist countdown unreadable.</summary>
    public const float TextContrast = 4.5f;

    /// <summary>Accent as TEXT on a card-like surface. Same hue as <see cref="Hairline"/>, solved to
    /// <see cref="TextContrast"/>.</summary>
    public static ColorF TextInk(ColorF seed)
        => TextInk(seed, Tok.Theme, ColorContrast.Flatten(Tok.FillCardDefault, WaveeColors.FloatingPane));

    /// <summary>Pure overload used by contrast tests for both themes without mutating global theme state.
    /// Some hues cannot hit AA at the hairline saturation cap (a mid-blue on a dark card tops out ~4.4:1);
    /// those fall back to <see cref="ColorContrast.PickContrast"/> so the digits stay readable.</summary>
    public static ColorF TextInk(ColorF seed, ThemeKind theme, ColorF cardBackground)
    {
        var ink = Hairline(seed, theme, cardBackground, TextContrast);
        return ColorContrast.MeetsAaText(ink, cardBackground) ? ink : ColorContrast.PickContrast(cardBackground);
    }

    /// <summary>Chrome fill from a Pathfinder <c>extractedColors.colorDark</c> payload (opaque ARGB). Same
    /// <see cref="Lift"/> the Home hero Play button uses, so a daylist card and its detail page agree before
    /// <c>CoverColorPlane</c> grades the art. 0 (no payload) is the semantic accent, not a fabricated hue.</summary>
    public static ColorF ChromeFromPayload(uint argb)
        => argb == 0 ? Tok.AccentDefault : Lift(ToColor(argb));

    /// <summary>Pure overload used by contrast tests for both themes without mutating global theme state.</summary>
    public static ColorF Hairline(ColorF seed, ThemeKind theme, ColorF cardBackground, float targetContrast = HairlineContrast)
    {
        var (h, s, _) = seed.ToHsv();
        s = MathF.Min(s, HairlineSaturationCeiling);

        // Contrast is monotonic along V for a fixed hue/saturation. Dark cards search from black to white and retain
        // the lighter solution; light cards search the same interval and retain the darker solution.
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
                // On a light card, contrast falls as V rises.
                if (ratio < targetContrast) hi = mid; else lo = mid;
            }
        }
        float v = theme == ThemeKind.Dark ? hi : lo;
        return ColorF.FromHsv(h, s, v, seed.A);
    }

    /// <summary>THE page's chrome accent — the one derivation every accent-filled control on a media surface uses: the
    /// cover's most-saturated graded role (<see cref="Accent"/>), brightness-lifted then saturation-floored.
    /// Greyscale/near-monochrome art has no hue to amplify, so it falls back to <see cref="Tok.AccentDefault"/> rather
    /// than shipping a grey Play button.
    ///
    /// Deliberately NOT used for the hero WASHES: those keep the plain <see cref="Lift"/>ed accent, so wash strength
    /// (alpha) and chrome chroma (saturation) stay independent axes.</summary>
    public static ColorF ChromeAccent(in CoverColorPlane.Scheme s)
    {
        var lifted = Lift(Accent(s));
        var (_, sat, _) = lifted.ToHsv();
        return sat <= NeutralS ? Tok.AccentDefault : Vivid(lifted);
    }

    // The roles the app's chrome asks for, over the plane's five-role scheme. backgroundBase is the dominant tone;
    // backgroundTintedBase is the slightly-lifted band tone the page washes use.

    /// <summary>The cover's HUE — the most saturated of the graded roles, in preference order
    /// <c>backgroundTintedBase → backgroundBase → textSubdued → textBrightAccent</c> (ties keep the earlier one).
    ///
    /// Why not simply <c>textBrightAccent</c>, which its name promises: in the real <c>getDynamicColorsByUris</c>
    /// payloads that role is the CONTRAST-GRADED INK, not a hue — it is pure <c>#FFFFFF</c> in every <c>highContrast</c>
    /// dark half and pure <c>#000000</c> in every light half (verified over 9,316 cached gradings: 100%). Reading it as
    /// "the accent" therefore made <see cref="ChromeAccent"/>'s <see cref="NeutralS"/> guard fire on EVERY cover, so
    /// every Play CTA in the app rendered <see cref="Tok.AccentDefault"/> blue. The cover's actual chroma lives in the
    /// BACKGROUND roles (dark <c>backgroundTintedBase</c>: median HSV S≈0.73) and, in the light half, in
    /// <c>textSubdued</c> (median S≈0.45).
    ///
    /// <c>textBrightAccent</c> stays in the list — LAST — so a future feed that does grade it chromatically still wins
    /// wherever it is the most saturated role, without this needing to change. Genuinely greyscale art leaves every role
    /// below <see cref="NeutralS"/> and still falls through <see cref="ChromeAccent"/>'s guard to the semantic accent,
    /// so a black-and-white cover keeps the system blue instead of inventing a random tint.</summary>
    public static ColorF Accent(in CoverColorPlane.Scheme s)
    {
        // A Span collection expression: stack-allocated, so this stays allocation-free on the render path.
        Span<uint> roles = [s.BackgroundTintedBase, s.BackgroundBase, s.TextSubdued, s.TextBrightAccent];
        ColorF best = default;
        float bestS = -1f;
        for (int i = 0; i < roles.Length; i++)
        {
            var c = ToColor(roles[i]);
            var (_, sat, _) = c.ToHsv();
            if (sat > bestS) { bestS = sat; best = c; }   // strict > ⇒ a tie keeps the earlier (higher-preference) role
        }
        return best;
    }

    public static ColorF BackgroundDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundBase);
    public static ColorF TintedDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundTintedBase);

    // ── THE PAGE TONE ───────────────────────────────────────────────────────────────────────────────────────────
    // The detail pages' ONE art-derived surface: an opaque plane the whole page sits on, replacing the stack of
    // top-anchored alpha washes that used to tint it. Two rules, and they are the whole contract:
    //
    //   1. COMPLEMENTARY, NOT SAMPLED. The tone takes the cover's HUE and nothing else. It is not the cover's dominant
    //      colour re-painted at page scale — that is what produced pages the artwork could not be told apart from.
    //      Saturation and lightness are the page's, not the record's, so two albums by the same artist read as the
    //      same PAGE in two different colours rather than as two different apps.
    //   2. THE CLAMP IS THE POINT. Apple Music's unclamped version of this is its single loudest complaint: a
    //      saturated cover produces a page that is genuinely hard to read, and a dark cover produces one that is
    //      indistinguishable from every other dark cover's. So lightness is FORCED (never sampled) to
    //      <see cref="PageToneDarkL"/> / <see cref="PageToneLightL"/> and saturation is CAPPED at
    //      <see cref="PageToneDarkSMax"/> / <see cref="PageToneLightSMax"/>. The forced lightness is also what makes
    //      the standard Tok ink tokens correct on this plane in both themes — there is no on-media ladder on these
    //      pages, because polarity is guaranteed by construction rather than measured per cover.
    //
    // A cover with no hue worth using (greyscale art, a mosaic of monochrome tiles) gets the NEUTRAL tone instead of
    // an invented tint — the same decision <see cref="ChromeAccent"/> makes for the Play button, for the same reason.

    /// <summary>Below this HSV saturation the dominant graded role has no hue to build a page tone from.</summary>
    public const float PageToneChromaFloor = 0.12f;

    /// <summary>The forced HSL lightness of the dark tone, and the cap on its HSL saturation.</summary>
    public const float PageToneDarkL = 0.15f, PageToneDarkSMax = 0.30f;

    /// <summary>The forced HSL lightness of the light tone, and the cap on its HSL saturation.
    ///
    /// <para>THE LIGHT ARM IS A WHISPER, and the numbers say so. The first light clamp (L 0.89 / S ≤ 0.42) was the dark
    /// arm's clamp mirrored, and mirroring is exactly the mistake: the dark tone paints a hue at 15 % lightness where
    /// 30 % saturation is a suggestion, while 42 % saturation at 89 % lightness is a full pastel — a green cover landed
    /// on ≈<c>#D7EFD7</c> and REPLACED the page's ground with a coloured plane. In light there is no headroom to spend:
    /// the ink is dark, the surfaces are near-white, and every chromatic step the ground takes is a step the ink and
    /// every card on top of it has to survive. So the light tone is the same idea at a tenth of the volume — L 0.94
    /// (four points above Mica Alt's own #EDEDED, so the page still reads as a PAGE and not as chrome) with saturation
    /// capped at 0.16, which is enough for "this record is green" and not enough for "this app is green".</para></summary>
    public const float PageToneLightL = 0.94f, PageToneLightSMax = 0.16f;

    /// <summary>The hue-less answer: a near-black in dark, the canon neutral off-white in light.
    ///
    /// <para>The light value used to be a WARM off-white (<c>#F6F4F1</c>) on the argument that a neutral grey page reads
    /// as "unfinished" beside every tinted one. That argument was written against the old 0.42 saturation cap, where the
    /// tinted pages really were pastel and a neutral one really did look unfinished next to them. Under the whisper
    /// clamp above the tinted pages are barely tinted, so the warm tone stopped being the quiet member of a family and
    /// became the app's one un-asked-for colour cast — a warm paper default in an app whose light identity is stock
    /// Fluent. It is now <c>#F5F5F5</c>: achromatic, and within 5/255 of the clamp's own achromatic point
    /// (L 0.94 ⇒ #F0F0F0), so a greyscale sleeve and a hued one produce pages of the same brightness.</para></summary>
    public static ColorF PageToneNeutralDark { get; } = ColorF.FromRgba(0x15, 0x15, 0x15);
    public static ColorF PageToneNeutralLight { get; } = ColorF.FromRgba(0xF5, 0xF5, 0xF5);

    // THE BLURRED-BACKDROP ALPHAS ARE GONE, and this note is the tombstone. BackdropAlphaDark + BackdropDarkAMax/AMin
    // (0.34→0.14, lerped over the dominant role's WCAG relative luminance) existed to keep the detail page's blurred
    // cover band level across sleeves, because that band's loudness was roughly its own luminance × its alpha. It is
    // the only quantity in this file that was a function of the ARTWORK rather than of the theme, and that is exactly
    // why it never converged: three tunings (flat 0.40, flat 0.32 in light, this adaptive pair in dark) and a bright
    // sleeve still bloomed while a dark one showed nothing, so the same page read as two different designs depending on
    // the record. The band itself is deleted (see CoverPageTonePlane's tombstone) — the page is now the clamped PageTone
    // alone, whose whole point is that lightness and saturation are the PAGE'S and only the hue is the record's.

    /// <summary>THE detail page's ground tone for a cover's grading, or null when there is no grading to build one
    /// from (the caller then paints nothing and the page keeps its neutral surface). See the contract above.</summary>
    public static ColorF? PageTone(CoverColorPlane.Scheme? scheme, ThemeKind theme)
    {
        if (scheme is not { } s) return null;
        var dominant = Accent(s);                        // the cover's HUE role (see Accent's doc for why not textBrightAccent)
        var (_, hsvSat, _) = dominant.ToHsv();
        if (hsvSat < PageToneChromaFloor)
            return theme == ThemeKind.Dark ? PageToneNeutralDark : PageToneNeutralLight;
        var (h, sat, _) = ToHsl(dominant);
        return theme == ThemeKind.Dark
            ? FromHsl(h, MathF.Min(sat, PageToneDarkSMax), PageToneDarkL)
            : FromHsl(h, MathF.Min(sat, PageToneLightSMax), PageToneLightL);
    }

    // HSL, not HSV: the page tone's contract is stated in LIGHTNESS ("dark: L ≈ 15 %"), and HSV's V is not lightness —
    // a fully saturated hue at V=0.15 and a grey at V=0.15 have very different perceived brightness. The engine's
    // ColorF publishes HSV only, so the two conversions live here, where the one caller that needs them is.
    internal static (float H, float S, float L) ToHsl(in ColorF c)
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
        return (h * 60f, s, l);
    }

    internal static ColorF FromHsl(float hDeg, float s, float l, float a = 1f)
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

    // ── DATA-ENCODING INK ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Saturation and the two lightness rungs a data hue is forced to in LIGHT. See <see cref="DataDotInk"/>.</summary>
    const float DataDotLightS = 0.65f, DataDotLightLDim = 0.30f, DataDotLightLBright = 0.40f;

    /// <summary>The hue band whose members are intrinsically LIGHT — yellow through cyan. Fluent's shared-colour rule is
    /// that darkening for a light surface is HUE-DEPENDENT, and this is the band that needs the extra rungs.</summary>
    const float DataDotBandLo = 40f, DataDotBandHi = 200f;

    /// <summary>A SERVER-SUPPLIED data hue (the Camelot wheel's key colours) rendered as ink for the CURRENT theme.
    ///
    /// <para>The wheel's colours arrive fully saturated and mid-to-high lightness — they were authored for a dark
    /// surface, where a 6-DIP saturated dot at 0.85 opacity is a quiet identity mark. Painted UNCHANGED on a near-white
    /// row the same dot is the loudest thing in the tracklist: a saturated yellow or cyan at L ≈ 0.5 has almost no
    /// contrast against #FCFCFC, so it reads as a smear of colour rather than as a token, and the saturated reds and
    /// magentas out-shout the title beside them.</para>
    ///
    /// <para>The correction is Fluent's own shared-colour principle and its awkward part is the part that matters:
    /// darkening for a light surface is HUE-DEPENDENT. Yellow and cyan sit near the top of the luminance curve and need
    /// roughly three rungs of darkening before they read as ink; red, blue and magenta sit near the bottom and need
    /// about one, and darkening them as far as yellow turns the wheel into twelve browns. So the band
    /// [<see cref="DataDotBandLo"/>°, <see cref="DataDotBandHi"/>°] — yellow through cyan — is forced to L 0.30 and
    /// everything else to L 0.40, at a common S 0.65 that keeps adjacent keys distinguishable (the wheel's whole point
    /// is that harmonically adjacent keys are adjacent hues).</para>
    ///
    /// <para>DARK IS A PASSTHROUGH, deliberately: the wire colours already are the dark-surface answer, and re-grading
    /// them would break the one property the wheel guarantees.</para>
    ///
    /// <para>An input with no hue worth keeping (S ≤ <see cref="NeutralS"/>) returns a NEUTRAL at the same lightness
    /// rung rather than being pushed to S 0.65, which would invent a red for a grey.</para></summary>
    public static ColorF DataDotInk(uint argb, ThemeKind theme)
    {
        var c = ToColor(argb);
        if (theme == ThemeKind.Dark) return c;
        var (h, s, _) = ToHsl(c);
        float l = h >= DataDotBandLo && h <= DataDotBandHi ? DataDotLightLDim : DataDotLightLBright;
        // A greyscale swatch has no hue to preserve; forcing S here would fabricate one from HSL's h == 0 fallback.
        return s <= NeutralS ? FromHsl(0f, 0f, l, c.A) : FromHsl(h, DataDotLightS, l, c.A);
    }

    /// <summary>Neutral fallback when the plane has no grading yet (no current track / not fetched). Every role is
    /// greyscale ON PURPOSE — including <c>textBrightAccent</c>, which the real wire always grades to pure white in the
    /// dark half (see <see cref="Accent"/>). A fabricated blue here made the fallback scheme the one "cover" in the app
    /// with a hue, which is precisely the wrong shape to test chrome against.</summary>
    public static CoverColorPlane.Scheme Neutral { get; } =
        new(BackgroundBase: 0xFF1C1C1C, BackgroundTintedBase: 0xFF2A2A2A, TextBase: 0xFFFFFFFF,
            TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    /// <summary>The player bar's neutral dark base — the surface the album hue is only faintly lifted from. WinUI's
    /// subtlety came from acrylic over a real blurred desktop; we have neither, so the bar is a flat neutral fill with a
    /// capped hue instead of a saturated tint.</summary>
    public static ColorF BarSurface { get; } = ColorF.FromRgba(0x1A, 0x1B, 0x1E, 0xFF);
}
