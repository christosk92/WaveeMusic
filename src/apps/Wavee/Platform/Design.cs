// ── Platform/Design.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// tokens, type ramp, colour, materials, motion, wash geometry, page-nav motion, reveal ramp, MorphKeys,
// image decode scale, WaveeAccentCtx, the focus insets (FocusInsetBordered 2f /
// FocusInsetRow 1f), the ambient cadence block. Not ContextBandLayout (A1) and not the palette plane (A5)
//
// Role: CORE
// Owner: L
// Wave: 4
// Budget: 3000 lines
// Spec: ch 00 §9.4 (2,600-3,000) + ch 29 §9.10 (200)
//
// THEMES PLAN T2a. The TYPE ramp, the page-rhythm / shelf / flyout SPACE and the interaction SCALE tiers + stagger rungs are
// not here any more: they are the active style's roles, read as `R.Type.*`, `R.Space.*` and `R.Motion.*` (Theming\Roles), with
// Classic's values in Themes\Classic\Style\ClassicRoles.cs. What this file keeps is the structural geometry that needs
// compile-time numbers, the Fluent duration ladder, the colour ladders (T2b), the behaviour (entrance, reveal, page-nav
// motion). The shell material channel and the four cover leaves left in T4c (see the pointer at the end of the file); the frame
// clock is `FrameTime` (`Platform\FrameTime.cs`) since the leftovers lane P3: a clock, not a token.
//
// THEMES PLAN T2b. The COLOUR ladders are not here any more either: the surfaces and row ladders (`Design.Colors`), the accent budget
// (`Design.Accent`), the on-media ladder (`Design.OnMedia`), the immersive stage's polarity (`Design.StageArm` / `Design.StageInk`),
// the wash strengths and the lyrics ink are the active style's COLOUR ROLES, read as `R.Layer.*`, `R.Ink.*`, `R.OnMedia.*`,
// `StageInk.*` / `R.StageArm`, `R.Wash.*` and `R.Lyrics.*` (Theming\Roles), with Classic's recipes in
// Themes\Classic\Style\ClassicRoles.cs. The colour MATHS (lift, vivid, the hairline and text-ink solves, the page-tone clamp, the
// data-hue ink, the shell ground and the placeholder blend) is `Platform\ColorScience.cs`, a pure class that takes the theme kind
// as an argument. What this file keeps of colour is the part that reads a cover's grading for the active polarity (`SchemeFor`,
// `ChromeSchemeFor`, `PlaceholderFor`), the Tok-bound face of the maths (`Design.Palette`) and the shell material channel.
//
// ── WHAT THIS FILE IS ────────────────────────────────────────────────────────────────────────────────────────────────
//
// Wavee's design system, as ONE static partial class over the engine's token layer. Everything here is static, pure and
// engine-only: it reads `Tok.*` and the reduced-motion flag and never an entity. That is why it ports as `static class`
// SECTIONS rather than as components (the four that were Components, the cover leaves, are Classic's material views since T4c).
//
// Ported from 0.2.9's `Design/*` (2,980 lines) plus five files that had no home of their own:
// `Features/Shell/PageNavMotion.cs`, `Features/Shell/ShellWashGeometry.cs`, `App/ShellMaterial.cs`,
// `Features/Detail/DetailRevealRamp.cs`, `Components/MorphKeys.cs`, and
// `SpotifyLive/CoverColorPlane.cs`'s `ShellTintOwnership` half.
//
// ── THE FOUR RULES THIS LAYER IS WRITTEN UNDER (ch 00 §9.1) ──────────────────────────────────────────────────────────
//
//  1. EVERY COLOUR MEMBER IS A `get =>` PROPERTY, NEVER A `static readonly` FIELD. `Tok.*` are LIVE theme reads; a field
//     captures the palette at type-init and never re-themes. (The same holds for a role: `R.*` is read where the element is
//     built, never into a field.)
//  2. THE TWO GRADING HALVES STAY TWO FUNCTIONS (§7). Collapsing them is invisible in dark and glaring in light.
//  3. THE 22-ITERATION BISECTION IN `ColorScience.Hairline` STAYS 22 ITERATIONS. It is the contrast solve; a cheaper
//     approximation changes every identity hairline and every accent-ink digit in the app.
//  4. THE CLAMP CONSTANTS ARE NOT TUNING KNOBS. If a page should take more colour, raise the PLANE ALPHA pair
//     (`R.Wash.PageTonePlaneAlpha`), never the page-tone clamp.
//
// ── THE TWO NAME COLLISIONS, STATED ONCE ─────────────────────────────────────────────────────────────────────────────
//
// Inside `class Design` the nested names win over the outer ones, so two references must be spelled the long way and are
// spelled that way everywhere below:
//   · `FgMotion` (the alias at the top of the file) = `FluentGpu.Dsl.Motion`, shadowed by `Design.Motion`.
//   · `Wavee.Palette` (the TABLE — the per-image graded schemes, the TTL, `Watch`, `Ensure`) is shadowed by
//     `Design.Palette` (the Tok-bound face of `ColorScience`). Nothing in `Design.Palette` touches a table, a fetch or a
//     readiness bit (A5).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FgMotion = FluentGpu.Dsl.Motion;

namespace Wavee;

public static partial class Design
{
    // ══ 1. GEOMETRY ══════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The fixed sizing ladder the engine's `Spacing.*` / `Radii.*` supersets do not carry. The 4-px grid is the native
    // tell; every value here is a multiple of 4 (the thumb ladder is an 8-step ON that grid).
    //
    // WHAT IS HERE, AND WHAT IS A ROLE (themes plan T2a). The page-rhythm, shelf and flyout geometry (the section gaps, the
    // page measure, the shelf and wide-tile card bands, the edge-fade ladder, the face-pile flyout, the dock reserve) is the
    // STYLE's: it is read as `R.Space.*` (Theming\Roles\SpaceRoles.cs) and Classic's table carries the values it had here.
    // What stays below is geometry whose consumers need a COMPILE-TIME number (a default parameter, a persisted-key default,
    // a `const` alias, a `static readonly` table, the zoom policy's BCL-only twin), so it cannot be a role yet. It is control,
    // row, pane, rail, thumb and player-bar geometry, and each member leaves with the family that owns it: T3 policies and
    // metrics, T5 nav, T6 player, T8 rows, T9 rails.

    /// <summary>Fixed control / surface dimensions that are not style roles (see the section header).</summary>
    public static class Size
    {
        public const float ControlH = 32f, NavItemH = 44f, TrackRowH = 56f, PlayerBarH = 72f;   // taller dock: room for the seek row. Classic's bar height and the core fallback's (`PlayerBarPolicy.Default`); the ACTIVE height is `PlayerBarPolicy.Height`
        public const float NavPaneW = 240f, NavCompactW = 56f;   // NavPaneW 240 = WinUI OpenPaneLength (flush, no inset gap)
        public const float ArtThumb = 40f, ArtNowPlaying = 64f, ArtPlayerBar = 48f;

        /// <summary>Detail-page left-rail widths (the shared playlist/album/single detail surface; liked is
        /// single-column, so it has no rail). `DetailRailPrefs.WidthKey` (the remembered-size state of the album and
        /// playlist rails) carries the same two numbers as DEFAULTS — a convergence test pins the pair, which is the
        /// `ZoomAutoPolicy.DesignW` precedent restated for the rails.</summary>
        public const float RailAlbum = 280f, RailPlaylist = 240f;

        /// <summary>The in-page thumbnail ladder — the ONLY sizes an in-content cover/avatar may take. An 8-px ladder on
        /// the 4-grid: anything between two rungs (36, 44, 52, 57 …) snapped to its nearest rung, which is what stopped
        /// a page from carrying five almost-identical art sizes that read as sloppiness rather than as hierarchy.</summary>
        public const float Thumb32 = 32f, Thumb40 = 40f, Thumb48 = 48f, Thumb56 = 56f, Thumb64 = 64f;

        /// <summary>The zoom design box's two numbers, PROMOTED (ch 00 DATA GAPS). <see cref="DesignW"/> is the page
        /// measure (Classic's <c>R.Space.PageMaxW</c>, pinned equal by a test); <see cref="DesignH"/> is the detail
        /// frame's tall-hero gate (winH ≥ 900), which in 0.2.9 was a bare literal inside the zoom policy with no token
        /// twin at all.
        /// <para>`ZoomAutoPolicy` keeps its own BCL-only literals so it stays source-includable; the convergence test
        /// that pins the two pairs is this wave's.</para></summary>
        public const float DesignW = 1600f, DesignH = 900f;

        /// <summary>One row of the "…and N more" attribution flyout (<c>R.Space.FacePile*</c>): a nav row tall.</summary>
        public const float FacePileRowH = NavItemH;

        /// <summary>The content region's ONE rounded corner, facing the nav pane. The shell reads it; so does the page
        /// tone plane, which is a page-root SIBLING of the scrolling page and must clip to the same shape.</summary>
        public static CornerRadius4 ContentPaneCorners => new(Radii.Card, 0f, 0f, 0f);
    }

    // ── the focus insets (ch 29 §1.2, W1) ────────────────────────────────────────────────────────────────────────────
    //
    // NO APP NODE PAINTS A FOCUS RING. The ENGINE draws WinUI's dual visual (a 2-DIP `Tok.FocusOuter` outer band + a
    // 1-DIP `Tok.FocusInner` inner band, concentric with the control's own corner) and only when focus arrived from the
    // KEYBOARD. The app owns exactly one knob, `Element.FocusVisualMargin`, and its DEFAULT IS NOT "nothing": null means
    // the WinUI template value of −3 all around, so a bare `BoxEl` with `Focusable = true` that declares nothing draws
    // its ring 3 DIP OUTSIDE itself, over its neighbour (or, on a cover FAB, over the artwork).
    //
    // 0.2.9 carried 36 explicit call sites at FOUR values and none of them was named. Two of the four are the rule and
    // are named here; the other two are normalised away (ch 29 §9.9.4: the flyout row's `−2` becomes
    // `FocusInsetBordered`; the one `−3` is a bare box deliberately wearing the STOCK look, and it keeps that by
    // declaring nothing at all, which is what "stock" means).

    /// <summary>A bare clickable CARD / TILE / CHIP: +2 keeps the ring inside the card's own rounded corner instead of
    /// 3 DIP out into its neighbour. 22 call sites in 0.2.9, all unnamed.</summary>
    public static readonly Edges4 FocusInsetBordered = Edges4.All(2f);

    /// <summary>ROW scale: at 40-64 DIP a 2-DIP inset eats the zebra stripe's edge, so a row sits at 1, just inside its
    /// own 1-px card stroke. 11 app sites in 0.2.9 — and TWO of them are conditional (`live ? inset : default`), which
    /// is why this stays a VALUE a call site may decline rather than a style baked into the row.</summary>
    public static readonly Edges4 FocusInsetRow = Edges4.All(1f);

    // ══ 2. THE ACCENT BUDGET ═════════════════════════════════════════════════════════════════════════════════════════
    //
    // MOVED (themes plan T2b). The three roles accent colour is allowed to play (Action, Selection, Decor) and the rules that keep
    // them apart are the style's INK roles: `R.Ink.AccentAction`, `R.Ink.AccentSelection`, `R.Ink.AccentDecor`
    // (Theming\Roles\InkRoles.cs carries the budget's full text).

    // ══ 3. COLOUR ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which of the engine's built-in ramps Wavee themes over (G-196). 0.2.9 called
    /// <c>Tok.Use(Tok.NeutralPalette, kind)</c> explicitly; 0.3 went through an optional shell seam nobody installed, so
    /// every theme flip fell back to <c>Tok.Use(kind)</c> — the engine's own default (<c>WarmPalette</c>), not Wavee's.
    /// <see cref="Appearance"/> (the one owner of the mode) calls this for every kind change.
    /// <para>The definition is the ACTIVE composition's (it carries the style's roles, so a light/dark flip keeps them);
    /// <see cref="Definition"/> itself, which has none, is only the fallback before <c>ThemeHost.Install</c>.</para></summary>
    public static void SeedPalette(ThemeKind kind) => Tok.Use(Wavee.Theming.ThemeHost.ActiveDefinition ?? Definition, kind);

    /// <summary>Wavee's theme definition: the engine's Neutral palette with Wavee's fonts — the BUNDLED icon face
    /// (<see cref="WaveeFonts.Icons"/>), never the system family of the same name, which only Windows 11 ships.</summary>
    public static readonly FluentGpu.Theming.ThemeDefinition Definition = Tok.NeutralPalette with
    {
        Fonts = new FluentGpu.Theming.FontSet(Tok.NeutralPalette.Fonts.Body, WaveeFonts.Icons),
    };

    // MOVED (themes plan T2b). `Design.Colors` (the shell ground, the content step, the translucent MUX rungs, the list-row ladder, the
    // selection ladder, the premium green) is the style's LAYER roles, read as `R.Layer.*` (Theming\Roles\LayerRoles.cs). Classic's
    // recipes are Themes\Classic\Style\ClassicRoles.cs and the ground / content-step maths is `ColorScience.ShellGround` /
    // `ContentSurface` / `ContentLayer`.
    //
    // TWO TOMBSTONES, kept so neither is re-invented:
    //  · The palette PICKER (Settings + the profile menu) and its swatch preview are gone — Wavee always renders the
    //    neutral palette. `Platform.cs`'s theme resolver survives as the ONE place a future preset would be named.
    //  · The sticky context band has NO material here. It used to flatten the translucent content rung onto a
    //    neutral no-wallpaper reference tone, and that was still wrong: live Mica takes its colour from the USER'S
    //    DESKTOP and a constant cannot follow it (on a dark wallpaper it read as a solid black slab across the
    //    page). The band now paints NOTHING and scrolled content is CLIPPED at its lower edge, so the band region
    //    shows the page's real ground whatever that is, and there is no constant left to drift.

    // ══ 4. THE ON-MEDIA LADDER, AND 5. THE IMMERSIVE STAGE'S POLARITY ═════════════════════════════════════════════════
    //
    // MOVED (themes plan T2b). The on-media ladder (the scrim plates, the hairline, the ink, the glass, the light-button ramp) is the
    // style's `R.OnMedia.*` (Theming\Roles\OnMediaLadder.cs): theme-INVARIANT in Classic by contract. The immersive stage's polarity
    // (`Design.StageArm`, the one surface that flips with the theme) is the style's `R.StageArm` table and its live facade
    // `StageInk.*` (Theming\Roles\StageArmTable.cs); a face reads `Ink.Plate` and never asks which theme it is in.

    // ══ 6. THE COVER PALETTE → RENDERER COLOUR ═══════════════════════════════════════════════════════════════════════

    /// <summary>The Tok-bound face of <see cref="ColorScience"/>, the boundary mapper from the cover-colour roles
    /// (<see cref="Scheme"/>, ARGB) to engine <see cref="ColorF"/>: the members that need the ACTIVE theme kind, the active accent or the
    /// active layer roles read them here and hand them to the pure maths as arguments. Everything that needs none of them is
    /// <c>ColorScience</c> itself (<c>ColorScience.Lift</c>, <c>ColorScience.PageTone</c>, <c>ColorScience.DataDotInk</c>, …).
    /// <para>NOTE THE NAME: this is <c>Design.Palette</c>. The TABLE — the per-image graded schemes, the TTL, <c>Watch</c>,
    /// <c>Ensure</c> — is <c>Wavee.Palette</c> (A5), and nothing in this section touches it.</para></summary>
    public static class Palette
    {
        /// <summary>The card surface an identity cue is solved against: the card fill flattened onto the floating pane.</summary>
        static ColorF CardGround(ColorF cardFill) => ColorContrast.Flatten(cardFill, R.Layer.FloatingPane);

        /// <summary>Accent as TEXT on a card-like surface of the ACTIVE theme: the identity-hairline hue
        /// (<see cref="ColorScience.Hairline(ColorF, ThemeKind, ColorF, float)"/>), solved to
        /// <see cref="ColorScience.TextContrast"/>.</summary>
        public static ColorF TextInk(ColorF seed)
            => ColorScience.TextInk(seed, R.Kind, CardGround(Tok.FillCardDefault));

        /// <summary>Chrome fill from a raw wire colour (see <see cref="ColorScience.ChromeFromPayload"/>); 0 (no payload) and a
        /// greyscale hue are the active semantic accent.</summary>
        public static ColorF ChromeFromPayload(uint argb) => ColorScience.ChromeFromPayload(argb, Tok.AccentDefault);

        /// <summary>THE page's chrome accent (see <see cref="ColorScience.ChromeAccent"/>); greyscale art falls back to the active
        /// semantic accent rather than shipping a grey Play button.</summary>
        public static ColorF ChromeAccent(in Scheme s) => ColorScience.ChromeAccent(s, Tok.AccentDefault);
    }

    // ══ 7. THE TWO GRADING HALVES, AND THE ART PLACEHOLDER ═══════════════════════════════════════════════════════════
    //
    // Every cover is graded TWICE by the provider — a light half and a dark half — and the app reads BOTH, in opposite
    // directions, from two functions that sit next to each other and differ by one boolean. That is not a leftover: it
    // is the policy, and it is worth one paragraph because reading the wrong one is invisible in dark and glaring in
    // light.
    //
    //   · SchemeFor — the PAGE half. Follows the active theme. Everything that paints a SURFACE the page's own ink then
    //     sits on takes this: the page tone, the hero/blend washes, the shell material tint, section spines. These are
    //     BACKGROUNDS, so they must be graded for the polarity of the theme they are painted in, or the page's Tok ink
    //     tokens stop being correct on them.
    //
    //   · ChromeSchemeFor — the CHROME half, and it takes the OPPOSITE theme's grading ON PURPOSE. Everything that
    //     paints a SOLID PLATE carrying on-accent ink takes this: the Play capsule, the Verified/Following pills, the
    //     stage's filled transport. A CTA is not a background — it is a foreground object that has to hold its own
    //     against the surface around it — and the provider's light grading is the softer, lower-chroma treatment
    //     (median HSV S ≈ 0.45) while its dark grading carries the stronger chroma (median S ≈ 0.73). So a LIGHT page
    //     wants the DARK grading's chroma for its one solid CTA, and a dark page wants the light grading's softness so
    //     the plate does not glow. Both fall back to the other half when only one exists, so a dark-only kind-179 entry
    //     still colours the CTA.
    //
    // THE RULE IN ONE LINE: if the app's own ink lands ON it, grade it FOR the theme; if it carries on-accent ink and
    // has to be seen, grade it AGAINST the theme.
    //
    // (These were `Surfaces.SchemeFor` / `.ChromeSchemeFor` in 0.2.9. They live in Design rather than in Controls
    // because they are colour resolution, not an element recipe, and because the cover leaves (Classic's material views) need them.)

    /// <summary>The full graded roles behind a cover, for PAGE chrome. Null until the plane has a grading for this theme.
    /// <para>Callers that want the colour to appear the moment it lands must NOT read the palette's watch/changed
    /// signals at PAGE scope (that rebuilds the whole page); subscribe from a leaf tone/tint node or a bound Fill
    /// instead — see the cover leaves (Classic's material views, <c>ClassicCoverLeaves.cs</c>).</para></summary>
    public static Scheme? SchemeFor(ReadOnlySpan<char> url)
        => Wavee.Palette.TryScheme(url, R.IsLight, out var s) ? s : null;

    /// <inheritdoc cref="SchemeFor(ReadOnlySpan{char})"/>
    public static Scheme? SchemeFor(StringId imageRef)
        => Wavee.Palette.TryScheme(imageRef, R.IsLight, out var s) ? s : null;

    /// <summary>The artwork grading used by SOLID chrome. The contrast branch is intentionally OPPOSITE
    /// <see cref="SchemeFor(ReadOnlySpan{char})"/>'s — see the block comment above.</summary>
    public static Scheme? ChromeSchemeFor(ReadOnlySpan<char> url)
    {
        bool pageIsLight = R.IsLight;
        if (Wavee.Palette.TryScheme(url, lightTheme: !pageIsLight, out var opposite)) return opposite;
        return Wavee.Palette.TryScheme(url, lightTheme: pageIsLight, out var same) ? same : null;
    }

    // ── the art placeholder ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // NON-NEGOTIABLE 9: every art slot is a SOLID, cover-tinted tile before its bitmap lands — never a hole, never a
    // grey wall. The tile is the neutral placeholder lerped toward the cover's own graded colour, resolved through an
    // image-keyed plane that ENQUEUES ON A MISS, so rendering the art IS the request and no surface has to remember to
    // prefetch a colour.

    // The neutral tile itself (an OPAQUE explicit neutral, never a translucent theme brush) is the style's `R.Layer.ArtPlaceholder`, and
    // the blend toward the cover's own colour is `ColorScience.Placeholder` at `ColorScience.TintStrength`.

    /// <summary>Below this edge the breathe is imperceptible, so a slot takes a cheap STATIC tile: no component, no hook
    /// cells, no image-epoch subscription — a 50k-row virtualized list pays nothing per item.</summary>
    public const float ShimmerMinEdge = 80f;

    /// <summary>THE art placeholder resolver, for the ACTIVE theme's polarity. Light theme only accepts a LIGHT grading;
    /// a dark-only entry keeps the neutral tile rather than dropping a dark slab onto a pale page.</summary>
    public static ColorF PlaceholderFor(ReadOnlySpan<char> url) => PlaceholderFor(url, R.IsLight);

    /// <summary>The same resolver with the polarity SUPPLIED rather than read. A surface whose ground does not follow
    /// the page — the immersive stage, which owns its own veil — needs the tile graded for ITS polarity, or a
    /// still-decoding cover flashes the wrong end of the ramp under its scrim. The tint still comes from the same
    /// image-keyed plane and a miss still enqueues; only the neutral it is blended toward changes.</summary>
    public static ColorF PlaceholderFor(ReadOnlySpan<char> url, bool light)
    {
        ColorF neutral = R.Current.Layer.ArtPlaceholder.Resolve(light ? ThemeKind.Light : ThemeKind.Dark);
        if (url.IsEmpty) return neutral;
        return Wavee.Palette.TryTint(url, light, out uint argb)
            ? ColorScience.Placeholder(neutral, argb)
            : neutral;
    }

    /// <summary>A LIVE-BOUND placeholder colour for an art slot that paints its own neutral tile directly as a
    /// <c>Fill</c>/<c>Placeholder</c> prop rather than stacking a shimmer component under the real image.
    ///
    /// <para>A raw <see cref="PlaceholderFor(ReadOnlySpan{char})"/> VALUE is computed once, at the render that first
    /// mounted the slot, and then FROZEN into the record — component factory fields freeze at mount. The
    /// enqueue-on-miss is real, but nothing then repaints the slot when that grading LANDS a moment later: every
    /// sidebar/pin thumb (always under <see cref="ShimmerMinEdge"/>) and every fill-grid cell took exactly this frozen
    /// path, which is why they stayed grey forever despite the plane correctly grading their covers in the
    /// background.</para>
    ///
    /// <para>The bind is PAINT-ONLY: the thunk reads the per-key watch signal, so a landed grading marks PaintDirty on
    /// exactly this tile — never a component re-render, never the global epoch fan-out.</para></summary>
    public static Prop<ColorF> WatchedPlaceholder(string? url, bool? light = null)
    {
        if (light is { } l)
            return Prop.Of(() =>
            {
                if (url is { Length: > 0 } u) _ = Wavee.Palette.Watch(u).Value;
                return PlaceholderFor(url, l);
            });

        // The theme-following arm (every call site today) is CACHED per url: the thunk is a pure function of its url
        // (the theme is read at evaluation), so one instance serves every slot showing that cover, and the per-render
        // callers — a shimmer tile, a fill-grid cell, a 50k-row list's thumbs — allocate no closure. Sharing a bound
        // `Prop` across elements is the engine's own idiom (`Ui.SecondaryTextBrush`). Bounded by a wholesale reset at
        // the cap: entries are tiny and rebuilt on demand, and a reset only costs the next render one closure per url.
        string key = url ?? "";
        lock (s_watchedPlaceholders)
        {
            if (s_watchedPlaceholders.TryGetValue(key, out var cached)) return cached;
            if (s_watchedPlaceholders.Count >= WatchedPlaceholderCap) s_watchedPlaceholders.Clear();
            var prop = Prop.Of(() =>
            {
                if (key.Length > 0) _ = Wavee.Palette.Watch(key).Value;
                return PlaceholderFor(key);
            });
            s_watchedPlaceholders[key] = prop;
            return prop;
        }
    }

    /// <inheritdoc cref="WatchedPlaceholder"/>
    const int WatchedPlaceholderCap = 4096;
    static readonly Dictionary<string, Prop<ColorF>> s_watchedPlaceholders = new(StringComparer.Ordinal);

    // ══ 8. THE TYPE RAMP ═════════════════════════════════════════════════════════════════════════════════════════════
    //
    // MOVED (themes plan T2a). The semantic type aliases (`Design.Type.TrackTitle` … `StatHero`), the display face, the eyebrow
    // tracking and the Home facet row's `SelectorBarStyle`s are the STYLE's type roles: they are read as `R.Type.*`
    // (Theming\Roles\TypeRoles.cs) and Classic's table (Themes\Classic\Style\ClassicRoles.cs) carries the rungs and deviations
    // they had here. The three-part contract (every role resolves a SIZE, a LINE HEIGHT and a WEIGHT, never a size alone), the
    // 400/600 weight policy with its six documented divergences and the rule that no call site authors a raw
    // `TextEl { Size = … }` moved with them.

    // ══ 9. MOTION ════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Wavee's ONE motion vocabulary. Hover/press motion everywhere is Wavee's identity — a media app should feel alive
    // under the pointer where WinUI is deliberately still — but it is a SYSTEM, not 20 hand-picked numbers. It has two halves:
    //   · the three interaction SCALE tiers (every HoverScale/PressScale in the app resolves to one) and the two stagger
    //     rungs are the STYLE's, read as `R.Motion.*` (Theming\Roles\MotionRoles.cs, themes plan T2a). The tiers read
    //     ReducedMotion themselves: the hover/press SCALE path is the ONLY animated channel in the engine that carries no
    //     reduced-motion policy (the recorder composites the scale unconditionally), so they return 1f under reduced motion,
    //     which makes the recorder's |s − 1| > 0.0008 test fail and skips the transform. That is reduced-motion-AS-A-VALUE
    //     (the canon rule): a value read during Render, never a hook-order-breaking branch;
    //   · the Fluent duration ladder below (every hover/press/brush duration snaps to one of its rungs) is a PLATFORM
    //     constant, WinUI's Control*AnimationDuration, and stays here as `const`.

    /// <summary>The Fluent duration ladder.</summary>
    public static class Motion
    {
        // ── the Fluent duration ladder (ms) ──────────────────────────────────────────────────────────────────────────
        // INTERACTION durations only. Structural enter/exit choreography stays authored per surface: those are
        // asymmetric by design (enter decelerates long, exit accelerates short) and are not on this ladder.

        /// <summary>WinUI ControlFasterAnimationDuration — brush cross-fades and press acknowledgement.</summary>
        public const float Faster = 83f;
        /// <summary>WinUI ControlFastAnimationDuration — hover reveals, small state changes.
        /// <para>NOTE the collision: this is <b>167</b>; the engine's own <c>MotionTok.ControlFast</c> is <b>150</b>.
        /// Two rungs, near-identical names, different files. <see cref="Faster"/> and <see cref="Standard"/> DO agree
        /// across the two layers — only the middle rung diverges.</para></summary>
        public const float Fast = 167f;
        /// <summary>WinUI ControlNormalAnimationDuration — the workhorse: recolor, icon swap, material cross-fade.</summary>
        public const float Standard = 250f;
        /// <summary>WinUI ControlSlowAnimationDuration — a slow, decelerating drift: the wide-cover hover zoom.</summary>
        public const float Slow = 333f;
    }

    /// <summary>THE disclosure motion (fluent-gpu docs/plans/smooth-reveal-implementation.md): layout lands once, the
    /// presented height springs under <c>MotionTok.Reveal</c> — critically damped, the same both ways, interruptible from
    /// where it stands — and everything below rides it in lockstep. A clip reveal, never a fade: nothing pops and nothing
    /// re-lays out per frame. Use these, never SizeMode.Reflow, for anything that opens and closes.</summary>
    public static class Reveal
    {
        /// <summary>An inline drawer that MOUNTS to open and unmounts to close (a list row's details, a reply thread): the
        /// edge wipes over content that stays put. A reopen mid-close continues from where the closing copy stands.</summary>
        public static readonly LayoutTransition Drawer = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
            Size: SizeMode.FlowReveal, Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
            Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

        /// <summary>A card body that unfolds: the content trails the moving edge by a damped share of what is still hidden.</summary>
        public static readonly LayoutTransition Card = Drawer with { Anchor = SizeAnchor.Parallax };

        /// <summary>A box whose content height changes in place ("Show all", a wrapping line): the new height reveals.</summary>
        public static readonly LayoutTransition Resize = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
            Size: SizeMode.FlowReveal);
    }

    /// <summary>The process-wide reduced-motion flag, read as a VALUE. Named here so no app file has to import the
    /// engine's <c>Motion</c> (which <see cref="Design.Motion"/> shadows inside this class) just to ask.</summary>
    public static bool Reduced => FgMotion.ReducedMotion;

    /// <summary>THE list/shelf entrance recipe — the one place a Wavee surface says "my items arrive in sequence".
    ///
    /// <para>A short rise + fade + a hair of blur, offset by <see cref="MotionRoles.StaggerMs"/> per item and <b>capped</b>
    /// at <see cref="StaggerCap"/>: items 0-8 cascade, everything after shares item 8's delay and lands together. The
    /// cap is not a nicety — <c>Element.Stagger</c> is <i>index × ms</i> with no ceiling, so a 50-row list authored that
    /// way takes two seconds to finish arriving, and the rows nobody is looking at are the ones still animating.</para>
    ///
    /// <para>REDUCED MOTION IS A VALUE, NEVER A BRANCH, and this recipe never changes SHAPE under it: it returns the
    /// same transition with <c>DelayMs = 0</c>, and the engine's own reduced-snap parks the rise and the blur at their
    /// end state while still cross-fading opacity (a fade aids orientation; it is not motion). Gating an entrance HOOK
    /// on the flag changes the hook COUNT between renders and crashes the reconciler the moment the flag flips
    /// mid-session — a resize grip flips it.</para>
    ///
    /// <para>WHERE IT MAY BE USED. A surface qualifies only if its items mount ONCE. A virtualized list mounts items as
    /// they scroll in, and an entrance replayed mid-scroll reads as flicker — so the recipe belongs on eager stacks and
    /// on the engine's BOUND recycler path, where a slot is re-bound rather than re-mounted. On a per-item-render
    /// virtual list it must NOT be used; those surfaces get their initial-mount cascade from the skeleton reveal's
    /// staggered-rows arm, which fires once on the shimmer→real swap and never again on scroll.</para></summary>
    public static class Entrance
    {
        /// <summary>The last item index that gets its own rung. The whole entrance is bounded at
        /// <c>StaggerCap × StaggerMs</c> = <b>320 ms</b> no matter how long the list is. (0.2.9's comment said 360; the
        /// arithmetic and its test both say 320 — ch 00 §9.6 drift 1. The code was right; the comment was not.)</summary>
        public const int StaggerCap = 8;

        /// <summary>How far an entering item rises, in DIP. Deliberately small — the same 8 the engine's own skeleton
        /// reveal uses, so a staggered list and a skeleton-revealed one arrive with the same gesture.</summary>
        public const float RiseDip = Expressive.DistBase;

        /// <summary>The un-delayed spec. Tween (not spring) because a staggered cascade wants every item to take the
        /// same time regardless of when it starts; <c>Channels = Opacity</c> so the node takes NO layout FLIP from this
        /// — the recipe is an entrance, not a layout animation.</summary>
        static readonly LayoutTransition Rise = new(
            TransitionChannels.Opacity,
            TransitionDynamics.Tween(Expressive.Slow, Easing.SmoothOut),
            Enter: new EnterExit(Dy: RiseDip, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));

        /// <summary>Item <paramref name="index"/>'s entrance delay in ms: capped, and 0 under reduced motion.</summary>
        public static float DelayMs(int index)
            => Reduced ? 0f : Math.Clamp(index, 0, StaggerCap) * R.Motion.StaggerMs;

        /// <summary>Assign to the item's own wrapper box (never to a node whose Opacity is already bound — a bound
        /// channel and an Enter opacity track fight over the same row).</summary>
        public static LayoutTransition Row(int index) => Rise with { DelayMs = DelayMs(index) };
    }

    // ── the ambient cadence block (ch 29 W25) ────────────────────────────────────────────────────────────────────────

    /// <summary>The app's frame-cadence constants. There is no ambient loop-rate knob any more: the engine's DefaultLoopHz
    /// (which the power policy set to 30 Hz plugged / 24 on battery for every cadence-less LOOPING row — the spinner, the
    /// shimmer, the equalizer, the deck drift, the concert ground, the tile wobble, the cover wall and its marquees) was
    /// deleted on 2026-10-03, so those loops run at the display rate. The power verdict itself (`Platform.cs`'s ambient
    /// power section) still polls, logs and drives the Energy Saver material fallback.
    ///
    /// <para>The self-paced clocks (the deck, the stage visualizer, the stage caption) tick once per produced FRAME
    /// (<see cref="Controls.FrameTicker"/>, mounted only while each clock runs, paused while parked) — they were fixed-rate
    /// <c>UseInterval</c> timers (30 Hz, then 60), which sample continuous motion below a 120 Hz panel's rate. What remains
    /// here: their nominal first step and the one power ceiling. There is no focus throttle.</para></summary>
    public static class Cadence
    {
        /// <summary>The per-frame clocks' NOMINAL rate: the dt their FIRST tick assumes (there is no previous tick to diff).
        /// Every later tick integrates the real elapsed time, and their models are dt-based (the visualizer's per-tick
        /// constants scale by <c>Visualizer.Bands.RefHz</c>), so this is not a look and not a cap.</summary>
        public const int ClockHz = 60;
        /// <summary>The host's frame-rate ceiling (<c>AppHost.PowerCapFps</c>) while Windows Energy Saver is on — the user
        /// asking the OS for less work, the one case Wavee trades smoothness for power (Chromium's Energy Saver caps at
        /// the same 30). Applied uniformly to motion, never to scroll/drag/touch; off otherwise, battery included. There
        /// is no focus throttle: a visible window paces the same focused or not.</summary>
        public const int EnergySaverMaxFps = 30;
    }

    // ══ 10. PAGE-NAV MOTION ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which direction the NEXT page swap travels. The shell's navigation verbs write this BEFORE the route
    /// signal in the same flush, so the reconciler can <c>Peek</c> it (an untracked read — a motion-only write must
    /// never re-run the keep-alive boundary) and get the direction that belongs to the route it is about to
    /// activate.</summary>
    public enum NavTransitionKind : byte { Forward, Back, Neutral }

    /// <summary>The IDENTITY of a keep-alive page slot: which browser tab, which route, which argument. The nav
    /// DIRECTION is deliberately NOT part of it — direction decides how a swap animates, not which page is cached.
    /// Folding it in made a motion-only write on the already-active key look like an activation change, which re-seeded
    /// the entrance and re-faded the whole page with no content change at all.
    /// <para>Three plain fields rather than the shell's own route record: this section is CORE and must not depend on
    /// `Shell.cs`'s route type. Owner I builds one of these from a route at the call site.</para></summary>
    public readonly record struct PageSlot(int TabId, string Route, string? Arg);

    /// <summary>The page-swap policy of the content card: slot identity and the recipe a swap maps to. Pure — no
    /// pages, no controls, no GPU — so it is pinned by tests.
    /// <para>The route → VIDEO-SAFE classification stays in the shell (it knows module routes); this section only
    /// supplies the recipe.</para></summary>
    public static class Nav
    {
        /// <summary>Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses
        /// the same page-slide language (the page moves; the content does not then cascade). Each committed search query
        /// is its own slot, so Back walks query history.
        /// <para>The separator is U+001F (unit separator) precisely because it cannot occur in a route name or an
        /// argument, so two slots can never collide by concatenation.</para></summary>
        public static string SlotKey(in PageSlot s)
            => s.TabId + "\u001F" + s.Route + "\u001F" + (s.Arg ?? "");

        // ── PAGE MOTION: the sequenced clock + the one geometry ─────────────────────────────────────────────────────

        /// <summary>The exit leg's duration of the page swap — Material's
        /// fade-through, verified to have zero overlap.</summary>
        public const float ExitDurationMs = 90f;
        /// <summary>The enter leg's start delay. Equal to <see cref="ExitDurationMs"/> EXACTLY — never less, or the two
        /// legs are briefly both on screen, which is the regression this file exists to fix (two full-bleed pages
        /// summing to 1.83 opacity for ~130ms when both legs started at t=0).</summary>
        public const float EnterDelayMs = ExitDurationMs;
        /// <summary>The enter leg's own duration. <see cref="ExitDurationMs"/> + this = 300ms total.</summary>
        public const float EnterDurationMs = 210f;

        /// <summary>The video-safe pair's travel, so a module page does not twitch <see cref="Expressive.DistBase"/> DIP
        /// when it must slide instead of cross-fade.</summary>
        public const float SiblingDx = 80f;
        /// <summary>The page swap's badge-twitch travel — the 0.2.x fade-through, verbatim.</summary>
        public const float ClassicDx = Expressive.DistBase;

        static TransitionDynamics SeqEnter => TransitionDynamics.Tween(EnterDurationMs, Easing.FluentDecelerate);
        static TransitionDynamics SeqExit => TransitionDynamics.Tween(ExitDurationMs, Easing.FluentAccelerate);

        /// <summary>A sequenced translate + fade — the enter and exit legs each on their own horizontal travel.</summary>
        static LayoutTransition SeqTranslate(float enterDx, float exitDx) => new(
            TransitionChannels.Position | TransitionChannels.Opacity, SeqEnter,
            Enter: new EnterExit(Dx: enterDx, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: exitDx, Opacity: 0f, Active: true),
            ExitDynamics: SeqExit, DelayMs: EnterDelayMs, ExitDelayMs: 0f);

        /// <summary>The recipe for a page swap, WITH its Exit half. Both halves are load-bearing: the reconciler only
        /// overlaps the outgoing page (ZStacked on the boundary, hit-test invisible, parked once its tracks settle) when
        /// <c>Exit.Active</c> is true — with a stripped Exit the outgoing page is detached in the same frame and the card
        /// flashes EMPTY before the incoming page arrives. Neutral is a plain fade; Forward/Back are the 0.2.x
        /// fade-through (a sequenced 8-DIP translate + fade), the same for every pair of pages.
        /// <para>CRITICAL: the translate passes <c>ExitDelayMs: 0f</c> EXPLICITLY. It is <c>float?</c> and null means
        /// "inherit <c>DelayMs</c>" — omitting it would push the exit out by <see cref="EnterDelayMs"/> too and flash the
        /// content card empty for that whole window (the bug a naive port reintroduces).</para></summary>
        public static LayoutTransition RecipeFor(NavTransitionKind motion) => motion switch
        {
            NavTransitionKind.Neutral => MotionRecipes.PageFade,
            NavTransitionKind.Back => SeqTranslate(-ClassicDx, ClassicDx),
            _ => SeqTranslate(ClassicDx, -ClassicDx),
        };

        // ── the VIDEO-SAFE pair ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The recipe for a page swap where either side can be hosting a live composited video. <b>Position
        /// only.</b>
        ///
        /// <para>A composited video is a DestOut hole punched into the real back buffer by a descendant. An ancestor
        /// OPACITY channel multiplies straight into the video command's own opacity (a washed-out, see-through video),
        /// and an opacity GROUP pushes an offscreen render target the punch can never reach the back buffer from — so
        /// the hole vanishes entirely and silently. Since the reconciler keeps the OUTGOING page's root attached and
        /// drawing for the whole exit, the page being navigated AWAY from is just as exposed as the one being navigated
        /// to, which is why the caller classifies BOTH sides.</para>
        ///
        /// <para>A TRANSLATE is the one ancestor motion a hole rides correctly: it composes on the absolute rect the
        /// punch already reads from, so nobody has to animate the hole for the hole to move. Hence a symmetric slide,
        /// with the same dynamics on both halves and <c>Exit.Active</c> still TRUE. Travel matches
        /// <see cref="SiblingDx"/> so a module page does not twitch 8 DIP.</para>
        ///
        /// <para><b>The honest degradation:</b> a module-page swap SLIDES instead of cross-fading, so two full-bleed
        /// pages share the card at full opacity for the length of the travel — which is the double exposure a fade
        /// would shrink, and still the better trade against a video that disappears mid-navigation.</para></summary>
        /// <returns>The slide for Forward/Back; <b>null</b> for <see cref="NavTransitionKind.Neutral"/> — an honest CUT.
        /// Neutral's only recipe is opacity and nothing else, so there is no video-safe form of it to hand back, and a
        /// hard cut is what "no motion this page can survive" looks like.</returns>
        public static LayoutTransition? RecipeForVideoSafe(NavTransitionKind motion) => motion switch
        {
            NavTransitionKind.Back => PageSlideSafeBack,
            NavTransitionKind.Neutral => null,
            _ => PageSlideSafeForward,
        };

        /// <summary>Both halves must finish together (they are one physical hole moving), so this is the ONE dynamics
        /// value for both <c>Dynamics</c> and <c>ExitDynamics</c> below — <see cref="MotionTok.StandardEnter"/> and
        /// <see cref="MotionTok.StandardExit"/> used to disagree (300ms vs 200ms), which is inconsistent with the "same
        /// dynamics on both halves" this recipe's own doc claims.</summary>
        static TransitionDynamics VideoSafeDynamics => TransitionDynamics.Tween(EnterDurationMs, Easing.FluentDecelerate);

        /// <inheritdoc cref="RecipeForVideoSafe"/>
        public static LayoutTransition PageSlideSafeForward => new(
            TransitionChannels.Position,
            VideoSafeDynamics,
            Enter: new EnterExit(Dx: SiblingDx, Active: true),
            Exit: new EnterExit(Dx: -SiblingDx, Active: true),
            ExitDynamics: VideoSafeDynamics);

        /// <inheritdoc cref="RecipeForVideoSafe"/>
        public static LayoutTransition PageSlideSafeBack => new(
            TransitionChannels.Position,
            VideoSafeDynamics,
            Enter: new EnterExit(Dx: -SiblingDx, Active: true),
            Exit: new EnterExit(Dx: SiblingDx, Active: true),
            ExitDynamics: VideoSafeDynamics);
    }

    // ══ 11. THE DETAIL REVEAL RAMP ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Pure progression math for a cold list's progressive shimmer→real reveal. BCL-only, so it is
    /// unit-testable in isolation.
    /// <para>Measured: the un-ramped swap mounted the whole visible band in one ~80 ms UI frame (694 spans re-recorded,
    /// record = 72.4 ms, a gen2 GC). The ramp reveals REAL rows a chunk at a time over a few frames instead, and
    /// <see cref="Chunk"/> 12 is about one 20 ms record slice of that measured 72.</para></summary>
    public static class RevealRamp
    {
        /// <summary>Real rows swapped shimmer→real per frame.</summary>
        public const int Chunk = 12;
        /// <summary>The ramp only needs to cover the realized viewport band (~44 rows measured); past it, snap to
        /// all-real.</summary>
        public const int Cap = 60;
        /// <summary>Sentinel: the ramp finished — every row is real, forever (rows scrolled in later never
        /// re-shimmer).</summary>
        public const int Done = int.MaxValue;

        /// <summary>The next reveal count, given the current count and the visible row count. Returns
        /// <see cref="Done"/> once a chunk reaches or exceeds the realized band.
        /// <para>TOTAL over every input (G-259). <see cref="Done"/> stays <see cref="Done"/>: a step that lands after the
        /// ramp finished must never add a chunk to the sentinel — <c>int.MaxValue + 12</c> wrapped to −2147483637 and
        /// blanked every row of a short list. The sum is taken in <see cref="long"/>, and a negative count (nothing
        /// revealed) saturates to 0, so any start reaches <see cref="Done"/> within <c>Cap / Chunk</c> steps and the
        /// result is never negative.</para></summary>
        public static int Next(int reveal, int visible)
        {
            if (reveal == Done) return Done;
            int target = Math.Min(visible, Cap);
            long next = (long)Math.Max(reveal, 0) + Chunk;
            return next >= target ? Done : (int)next;   // next < target ≤ Cap, so the narrowing is exact
        }

        /// <summary>Is the row at this display position a REAL row yet (vs a shimmer placeholder)? True once the ramp
        /// count passes it; always true at <see cref="Done"/>, so a fully-revealed list pays nothing.</summary>
        public static bool Revealed(int displayIndex, int reveal) => displayIndex < reveal;
    }

    // ══ 12. IMAGE DECODE SCALE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Routes a DIP decode-budget edge through the ambient device scale (OS DPI × app zoom). A DIP-sized decode
    /// target left unscaled decodes at 1/scale the resolution the cover is actually painted at, the moment the effective
    /// scale exceeds 1 — and every zoom step past 100% makes it worse.</summary>
    public static class ImageDecodeScale
    {
        /// <summary>A device-pixel CEILING so an extreme zoom+DPI stack (250% zoom on a 4K panel) never turns a
        /// DIP-small thumbnail into a multi-megapixel decode request.</summary>
        public const int Ceiling = 2048;

        /// <summary>Round UP to this device-pixel grid. A zoom change is a rare, deliberate, LADDER-STEPPED event, but
        /// the ambient scale is still a float — rounding to a coarse bucket keeps the decode cache keyed on a handful of
        /// stable sizes instead of thrashing on float noise or a fractional-pixel difference between two renders of the
        /// same nominal zoom.</summary>
        public const int BucketPx = 8;

        /// <summary>The scaled, bucketed, ceiling-clamped device-pixel decode edge for a DIP quantity. Returns 0 for a
        /// non-positive/non-finite <paramref name="dipEdge"/> (nothing to decode); a non-finite/non-positive
        /// <paramref name="scale"/> falls back to 1× rather than propagating a corrupt ambient read.</summary>
        public static int For(float dipEdge, float scale)
        {
            if (!float.IsFinite(dipEdge) || dipEdge <= 0f) return 0;
            if (!float.IsFinite(scale) || scale <= 0f) scale = 1f;
            float px = dipEdge * scale;
            int bucketed = (int)(MathF.Ceiling(px / BucketPx) * BucketPx);
            return Math.Clamp(bucketed, BucketPx, Ceiling);
        }

        /// <summary>Rounds an already-device-pixel edge UP to the same <see cref="BucketPx"/> grid <see cref="For"/>
        /// bakes into its own ambient-scale read (A2, G-259 follow-up) — for a caller that already has its final decode
        /// edge (Controls.cs's <c>Artwork</c> unscaled branch, which takes the laid-out DIP size literally) and only needs
        /// the cache KEY to land on the same handful of stable sizes a scaled decode would, so a one-pixel layout jitter
        /// stops minting a fresh (source, W, H) cache entry, a fresh fetch and a fresh GPU texture. Pure integer math —
        /// safe on the render path. A non-positive <paramref name="px"/> floors to one bucket rather than 0, so a
        /// degenerate zero-sized layout never reads as "the same size" as a real one.</summary>
        public static int Bucket(int px) => px <= 0 ? BucketPx : ((px + BucketPx - 1) / BucketPx) * BucketPx;
    }

    // ══ 13. THE SHARED-ELEMENT (HERO) KEY CONVENTION — DORMANT ═══════════════════════════════════════════════════════

    /// <summary>The connected-animation (shared-element / Hero) key for a cover IS the route key the card navigates
    /// with — already unique and already present on both the card and the detail page, so source and destination agree
    /// with zero extra plumbing. One place for the convention so the two sides never drift. Null ⇒ no Hero (the liked
    /// collection has no uri; artist covers are circular and were deferred) ⇒ today's instant swap.
    ///
    /// <para><b>NOTHING FLIES TODAY, AND THE CONVENTION STILL PORTS.</b> The forward CAPTURE seam was removed in the
    /// same commit that made the detail rail's cover pass a null key, so re-arming it means restoring the whole capture
    /// seam AND deciding how a cover fly composes with the page slide (the page's own absolute rect moves every frame of
    /// the slide, so the flight would chase a fading page). Dropping the key parameters is not a simplification; it is
    /// the deletion of the only agreement the two halves have — and the nav probe ASSERTS the source half is still
    /// minted, so the dormancy is a measured state rather than a hope.</para>
    ///
    /// <para>NOT to be folded together with the content card's stable-frame FLIP anchor. That rides the same
    /// <c>MorphId</c> column and has nothing to do with connected animation.</para>
    ///
    /// <para>ONE TAG PER URI. Uris repeat down a recents list (~1,388 times on a real account), the engine's tagged
    /// registry is last-writer-wins, and hiding a flying key hides EVERY node carrying it — so a second tagged row would
    /// blank itself mid-fly. A minting surface tags only the FIRST occurrence of each uri.</para></summary>
    public static class MorphKeys
    {
        /// <inheritdoc cref="MorphKeys"/>
        public static string? For(EntityKind kind, string? id) => id is { Length: > 0 }
            ? kind switch
            {
                EntityKind.Album => "album:" + id,
                EntityKind.Playlist => "pl:" + id,
                _ => null,
            }
            : null;
    }

    // ══ 14. THE PAGE-SCOPED AMBIENT ACCENT ═══════════════════════════════════════════════════════════════════════════

    /// <summary>One page's ambient, content-derived accent — an ink/fill pair a page resolves from whatever sits at the
    /// top of its viewport (a cover, a hero image) and shares with the small set of components that colour themselves
    /// off "whatever this page is currently about".
    /// <para><see cref="Key"/> identifies the SOURCE the pair was resolved from (an item id) so a consumer can key its
    /// own transition/memo on PROVENANCE and not merely on the resolved colour — two different sources that happen to
    /// land on the same colour are still a different accent.</para></summary>
    public readonly record struct PageAccent(ColorF Ink, ColorF Fill, string Key);

    /// <summary>The shared, page-scoped ACCENT channel, modelled on the shell material: a page that derives a live
    /// accent from its own content provides one signal at its root; any shared component that wants to tint itself with
    /// "this page's accent" reads it through this slot instead of knowing which page it is embedded in.
    /// <para>Default is <see langword="null"/> — a page that publishes no accent leaves every consumer rendering its
    /// ordinary token colour. A consumer reads the context LIVE inside <c>Render</c> (never captures it at
    /// construction), so a later provider lighting up repaints every mounted consumer for free.</para></summary>
    public static class AccentCtx
    {
        /// <inheritdoc cref="AccentCtx"/>
        public static readonly Context<IReadSignal<PageAccent>?> Slot = new(null);
    }

    // ══ 15. WASH GEOMETRY ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The three Home washes as pure geometry — the ONE place the window-relative constants live.
    /// <para>Clipping is a PAINT BUDGET, not a look: an unclipped wash is a full-screen blended pass, and three of them
    /// cost three. Each layer is sized to the bounding box of its own ellipse AT the transparent stop (outside which it
    /// contributes nothing), clamped to the window — so the same pixels are produced from about a quarter of the fill
    /// rate.</para></summary>
    public static class Wash
    {
        // Window-relative source geometry, in stacking order (Hero paints first, Mix last). Centres/radii are fractions
        // of the window; the fade offset is the gradient stop at which the wash reaches alpha 0.
        public static readonly ShellWashPlacement Hero =
            Resolve(new Point2(0.06f, 0.00f), new Point2(0.74f, 0.92f), 0.62f);
        public static readonly ShellWashPlacement Weekly =
            Resolve(new Point2(0.92f, 0.10f), new Point2(0.58f, 0.78f), 0.64f);
        public static readonly ShellWashPlacement Mix =
            Resolve(new Point2(0.58f, 1.00f), new Point2(0.90f, 0.70f), 0.66f);

        // The wash STRENGTHS (alpha at the origin, light and dark) are the style's `R.Wash.HeroAlpha` / `R.Wash.ShelfAlpha`: DARK carries roughly
        // TWICE the light strength, since the same colour reads far weaker over the dark ground and the light ground has less headroom before
        // a wash turns into a smudge. What stays here is GEOMETRY and the paint budget.

        /// <summary>Clip a window-relative radial wash to the bounds of its own ellipse at
        /// <paramref name="fadeOffset"/> and re-express the ellipse relative to that box. Pure — no theme, no viewport,
        /// no engine state.
        /// <para>THE ANCHOR RULE: a box whose LEADING edge left the window edge hangs off the TRAILING one (and vice
        /// versa); a full-span axis touches both, and LEADING wins, because Start is also the fill-the-slot arm of the
        /// ZStack arranger.</para></summary>
        public static ShellWashPlacement Resolve(Point2 center, Point2 radius, float fadeOffset)
        {
            float x0 = Math.Clamp(center.X - radius.X * fadeOffset, 0f, 1f);
            float x1 = Math.Clamp(center.X + radius.X * fadeOffset, 0f, 1f);
            float y0 = Math.Clamp(center.Y - radius.Y * fadeOffset, 0f, 1f);
            float y1 = Math.Clamp(center.Y + radius.Y * fadeOffset, 0f, 1f);
            float w = MathF.Max(x1 - x0, 1e-4f), h = MathF.Max(y1 - y0, 1e-4f);
            return new ShellWashPlacement(w, h, AnchorRight: x0 > 0f, AnchorBottom: y0 > 0f,
                new Point2((center.X - x0) / w, (center.Y - y0) / h),
                new Point2(radius.X / w, radius.Y / h),
                fadeOffset);
        }

        // The neutral ground the material layer paints when no page has a colour is `R.Wash.NeutralGround` (NEVER Transparent: transparent is
        // premultiplied BLACK, and cross-fading into or out of it drags the interpolated colour toward black for the whole ramp).

        /// <summary>The wash host's bottom inset is a MARGIN, not a re-anchored ellipse. The placements' centre/radius
        /// are node-relative CONSTANTS precisely because the box is a fraction of the WINDOW; subtracting a fixed
        /// <see cref="SpaceRoles.DockReserve"/> from the box HEIGHT would make those two ratios viewport-dependent and they would
        /// go stale on the next resize.</summary>
        public static float HostBottomInset => R.Space.DockReserve;

        /// <summary>Every wash stop carries the wash's own RGB at α 0, never <c>ColorF.Transparent</c>: stop
        /// interpolation is STRAIGHT-alpha, and premultiplied black would drag the whole falloff toward black.</summary>
        public static ColorF Vanish(ColorF c) => c with { A = 0f };
    }
}

// ── 16-17. the shell material channel and the cover leaves are not here any more (themes plan T4c) ───────────────────────
//
// The channel (`ShellMaterialState`, `ShellMaterial`, `TintOwnership`, `MaterialPort`) and the veil settlement are core: `Platform/ShellMaterial.cs`.
// The four cover-leaf components are the structure's material views (`Shell.Material.PageTone` / `.ArtistBlend` / `.ArtistVeil` / `.TintBinder`),
// Classic's in `Themes/Classic/Structure/Views/Material/ClassicMaterial.cs`; the pages mount them through `Entities/Palette.Host.cs`.
