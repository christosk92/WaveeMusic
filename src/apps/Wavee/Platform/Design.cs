// ── Platform/Design.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// tokens, type ramp, colour, materials, motion, wash geometry, page-nav motion, reveal ramp, frame time, MorphKeys,
// image decode scale, the four cover-leaf components, WaveeAccentCtx, the focus insets (FocusInsetBordered 2f /
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
// motion, frame time) and the cover leaves.
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
// SECTIONS rather than as components — only FOUR nodes in this file are Components, and they are exactly the ones that
// subscribe to the cover-colour plane or write the shell-material signal.
//
// Ported from 0.2.9's `Design/*` (2,980 lines) plus six files that had no home of their own:
// `Features/Shell/PageNavMotion.cs`, `Features/Shell/ShellWashGeometry.cs`, `App/ShellMaterial.cs`,
// `Features/Detail/DetailRevealRamp.cs`, `Features/Player/FrameTime.cs`, `Components/MorphKeys.cs`, and
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
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FgMotion = FluentGpu.Dsl.Motion;
using static FluentGpu.Dsl.Ui;

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
        public const float RailCard = 180f, NavPaneW = 240f, NavCompactW = 56f;   // NavPaneW 240 = WinUI OpenPaneLength (flush, no inset gap)
        public const float ArtThumb = 40f, ArtNowPlaying = 64f, ArtPlayerBar = 48f;

        /// <summary>Detail-page left-rail widths (the shared playlist/album/single detail surface; liked is
        /// single-column, so it has no rail). `Platform.Keys.DetailAlbumRailWidth` / `DetailPlaylistRailWidth` carry the
        /// same two numbers as persisted DEFAULTS — a convergence test pins the pair, which is the
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

        /// <summary>A quiet identity hairline solved against the actual card surface of the ACTIVE theme (3.25:1).</summary>
        public static ColorF Hairline(ColorF seed)
            => ColorScience.Hairline(seed, R.Kind, CardGround(Tok.FillCardDefault), ColorScience.HairlineContrast);

        /// <summary>The hovered card's slightly stronger identity cue; 3.55:1 remains below the 4.5:1 text threshold —
        /// on purpose.</summary>
        public static ColorF HairlineHover(ColorF seed)
            => ColorScience.Hairline(seed, R.Kind, CardGround(Tok.FillCardSecondary), ColorScience.HairlineHoverContrast);

        /// <summary>Accent as TEXT on a card-like surface of the ACTIVE theme. Same hue as <see cref="Hairline(ColorF)"/>, solved to
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
    // because they are colour resolution, not an element recipe, and because the four cover leaves below need them.)

    /// <summary>The full graded roles behind a cover, for PAGE chrome. Null until the plane has a grading for this theme.
    /// <para>Callers that want the colour to appear the moment it lands must NOT read the palette's watch/changed
    /// signals at PAGE scope (that rebuilds the whole page); subscribe from a leaf tone/tint node or a bound Fill
    /// instead — see <see cref="CoverPageTonePlane"/> and its siblings.</para></summary>
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

    /// <summary>Arms once the pointer has demonstrably MOVED — never on the first sample. A card's hover-driven effects
    /// (scale, fill, FAB reveal) must not light from the engine's own stationary-cursor hover RE-RESOLVE: that path
    /// re-fires a node's move-within at the SAME on-screen point whenever new content lands under a cursor that never
    /// actually moved — exactly the back-navigation case, where a grid mounts fresh cards under a mouse that has been
    /// resting there the whole time. With nothing to distinguish it from a genuine hover-enter, the card used to scale
    /// up the instant it appeared, with no pointer edge behind it at all.
    /// <para>ONE gate per card INSTANCE (a field on a stateful <c>Component</c>). The FIRST sample it ever sees is
    /// always recorded as a baseline, never treated as a hover — only a LATER sample landing somewhere else proves the
    /// pointer is genuinely in motion. Once armed it stays armed for the component's lifetime: a one-shot "has real
    /// input happened since mount" latch, not a per-hover-cycle re-check — a page the user is already interacting with
    /// must not re-litigate every hover-out/in.</para></summary>
    public struct HoverMotionGate
    {
        Point2 _baseline;
        bool _hasBaseline;
        bool _armed;

        /// <summary>Feed every move-within sample. Returns whether the card may now treat itself as genuinely hovered —
        /// false while every sample so far has been a single stationary re-hover.</summary>
        public bool Observe(Point2 pos)
        {
            if (_armed) return true;
            // Sub-pixel jitter from re-hit-testing the same float geometry must not itself read as "moved".
            if (_hasBaseline && (MathF.Abs(pos.X - _baseline.X) > 0.5f || MathF.Abs(pos.Y - _baseline.Y) > 0.5f))
            { _armed = true; return true; }
            _baseline = pos;
            _hasBaseline = true;
            return false;
        }
    }

    // ── the ONE app-side motion clock ────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE app-side motion clock: sample per-frame animation against the frame's own PRESENT time, never
    /// <c>Environment.TickCount64</c>. TickCount64 only advances in ~15.6 ms system-timer quanta, so at the 120 Hz+ a
    /// modern panel produces frames at, its per-frame delta alternates 0 / 15.6 ms — which steps or freezes anything
    /// driven off it instead of gliding. Reference engines agree: Flutter hands every ticker the frame's vsync
    /// timestamp; Chromium animates on the begin-frame time.
    ///
    /// <para><see cref="NowQpc"/> is the engine's present clock — the predicted vblank the frame CURRENTLY being
    /// produced will land on — falling back to a live QPC read only before the first frame. Every wall-clock stamp two
    /// per-frame writers compare against each other must come from here, or the comparison silently spans two different
    /// epochs. <b>There are no exceptions to this rule in this layer.</b></para></summary>
    public static class FrameTime
    {
        /// <summary>The current frame's present time, in QPC units.</summary>
        public static long NowQpc => FrameClock.PresentQpc > 0
            ? FrameClock.PresentQpc
            : System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary><see cref="NowQpc"/> in milliseconds. Comparable with any other frame-time-derived stamp; NOT with
        /// <c>Environment.TickCount64</c> — the two are different epochs entirely.</summary>
        public static long NowMs => (long)(NowQpc * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
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

    /// <summary>WinUI surface class: top-level (sidebar / Home / Library), a content detail, or a module page that
    /// may host composited video.</summary>
    public enum NavSurface : byte { TopLevel, Detail, Module }

    /// <summary>The spatial relation of a keep-alive swap — WinUI Entrance / DrillIn / sibling slide, plus Neutral's
    /// fade. Connected (cover-fly) is deliberately not a member this round.</summary>
    public enum NavRelation : byte { Entrance, DrillIn, Sibling, Fade }

    /// <summary>The five page-motion PERSONALITIES a user can choose in Settings ▸ Appearance ▸ Page motion. Every
    /// member but <see cref="None"/> SEQUENCES the outgoing and incoming legs — the exit finishes before the enter
    /// starts — so two full-bleed pages are never both legible at once. That is the defect this enum exists to fix:
    /// two legs both starting at t=0 read as two full-bleed pages simultaneously visible for ~130ms (summed opacity
    /// peaking at 1.83), then the outgoing page popping out. Only the GEOMETRY differs per style; the CLOCK
    /// (<see cref="Nav.ExitDurationMs"/> / <see cref="Nav.EnterDelayMs"/> / <see cref="Nav.EnterDurationMs"/>) is
    /// shared by every sequenced style — Material's fade-through, verified clean.
    /// <para><see cref="Fluent"/>: a plain cross-fade, no geometry, on every relation. <see cref="Spatial"/>: Entrance
    /// fades, DrillIn is a semantic zoom, Sibling slides the INCOMING page only. <see cref="WinUi"/>:
    /// WinUI's own page-refresh Entrance (a 140-DIP rise, the outgoing side only fades), the same zoom as Spatial for
    /// DrillIn, and a Sibling where both pages move by different amounts. <see cref="Classic"/> (the DEFAULT): the 0.2.x
    /// fade-through, an 8-DIP badge-twitch translate, uniform across every relation. <see cref="None"/>: an instant
    /// cut — no motion at all.</para></summary>
    public enum PageMotionStyle : byte { Fluent, Spatial, WinUi, Classic, None }

    /// <summary>How many styles the enum defines — the count <c>Prefs.Appearance.PageMotionStyle</c> clamps against
    /// (the same "clamp at the reader" contract as <c>LikedCoverRules.StyleCount</c>).</summary>
    public const int PageMotionStyleCount = 5;

    /// <summary>The IDENTITY of a keep-alive page slot: which browser tab, which route, which argument. The nav
    /// DIRECTION is deliberately NOT part of it — direction decides how a swap animates, not which page is cached.
    /// Folding it in made a motion-only write on the already-active key look like an activation change, which re-seeded
    /// the entrance and re-faded the whole page with no content change at all.
    /// <para>Three plain fields rather than the shell's own route record: this section is CORE and must not depend on
    /// `Shell.cs`'s route type. Owner I builds one of these from a route at the call site.</para></summary>
    public readonly record struct PageSlot(int TabId, string Route, string? Arg);

    /// <summary>The page-swap policy of the content card: slot identity and the recipe a relation maps to. Pure — no
    /// pages, no controls, no GPU — so it is pinned by tests.
    /// <para>The route → VIDEO-SAFE classification stays in the shell (it knows module routes); this section only
    /// supplies the recipe families.</para></summary>
    public static class Nav
    {
        /// <summary>Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses
        /// the same page-slide language (the page moves; the content does not then cascade). Each committed search query
        /// is its own slot, so Back walks query history.
        /// <para>The separator is U+001F (unit separator) precisely because it cannot occur in a route name or an
        /// argument, so two slots can never collide by concatenation.</para></summary>
        public static string SlotKey(in PageSlot s)
            => s.TabId + "\u001F" + s.Route + "\u001F" + (s.Arg ?? "");

        /// <summary>Classify a swap by the two surfaces and whether they are the same kind / identity. Neutral is always
        /// a fade. Same-kind different identity (artist→artist, search query→query) is a sibling slide. Anything that
        /// touches a detail surface is DrillIn. Otherwise Entrance (sidebar / top-level refresh).</summary>
        public static NavRelation RelationOf(NavSurface from, NavSurface to, bool sameKind, bool sameIdentity,
                                            NavTransitionKind motion)
        {
            if (motion == NavTransitionKind.Neutral) return NavRelation.Fade;
            if (sameKind && !sameIdentity) return NavRelation.Sibling;
            if (from == NavSurface.Detail || to == NavSurface.Detail) return NavRelation.DrillIn;
            return NavRelation.Entrance;
        }

        // ── PAGE-MOTION STYLE: the sequenced clock + per-style geometry (the fix) ───────────────────────────────────

        /// <summary>The style Settings ▸ Appearance ▸ Page motion defaults to when nothing is persisted yet — and, since
        /// that row is DEVELOPER-ONLY, the style a normal build navigates with. <see cref="PageMotionStyle.Classic"/>: the
        /// 0.2.x fade-through, uniform across every relation. <c>Platform.Keys.PageMotionStyle</c> takes its persisted
        /// default FROM this constant, so a profile that never picked a style follows it and one that did keeps its
        /// pick.</summary>
        public const PageMotionStyle DefaultStyle = PageMotionStyle.Classic;

        /// <summary>The exit leg's duration for every SEQUENCED style (everything but <see cref="PageMotionStyle.None"/>)
        /// — Material's fade-through, verified to have zero overlap.</summary>
        public const float ExitDurationMs = 90f;
        /// <summary>The enter leg's start delay. Equal to <see cref="ExitDurationMs"/> EXACTLY — never less, or the two
        /// legs are briefly both on screen, which is the regression this file exists to fix (two full-bleed pages
        /// summing to 1.83 opacity for ~130ms when both legs started at t=0).</summary>
        public const float EnterDelayMs = ExitDurationMs;
        /// <summary>The enter leg's own duration. <see cref="ExitDurationMs"/> + this = 300ms total.</summary>
        public const float EnterDurationMs = 210f;

        /// <summary>The masthead band shares this window with the page swap (ch 18 §4's "one gesture" comment) so a
        /// drill-in reads as one motion — it IS the page's real exit window, not an independent constant that can
        /// drift from it.</summary>
        public const float FadeThroughExitMs = ExitDurationMs;

        /// <summary>WinUi's Entrance rise — its own page-refresh travel, distinct from Spatial's (which does not rise
        /// at all) and from a badge twitch (<see cref="Expressive.DistBase"/>).</summary>
        public const float WinUiEntranceDy = 140f;
        /// <summary>Spatial's Sibling travel (the incoming page only) — shared with the video-safe pair so a module
        /// page does not twitch <see cref="Expressive.DistBase"/> DIP when it must slide instead of cross-fade.</summary>
        public const float SiblingDx = 80f;
        /// <summary>WinUi's Sibling: the incoming page travels further than the outgoing one recedes.</summary>
        public const float WinUiSiblingInDx = 200f, WinUiSiblingOutDx = 150f;
        /// <summary>Classic's uniform badge-twitch travel — the 0.2.x fade-through, verbatim.</summary>
        public const float ClassicDx = Expressive.DistBase;
        /// <summary>Spatial's and WinUi's DrillIn scale terminals — a semantic zoom, not an eight-DIP fade.</summary>
        public const float DrillScaleIn = 0.94f, DrillScaleOut = 1.04f;

        static TransitionDynamics SeqEnter => TransitionDynamics.Tween(EnterDurationMs, Easing.FluentDecelerate);
        static TransitionDynamics SeqExit => TransitionDynamics.Tween(ExitDurationMs, Easing.FluentAccelerate);

        /// <summary>A plain sequenced cross-fade: Fluent's whole vocabulary, and Spatial's Entrance.</summary>
        static LayoutTransition SeqFade() => new(
            TransitionChannels.Opacity, SeqEnter,
            Enter: new EnterExit(Opacity: 0f, Active: true),
            Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: SeqExit, DelayMs: EnterDelayMs, ExitDelayMs: 0f);

        /// <summary>A sequenced translate + fade — the enter and exit legs each on their own vector (a magnitude of 0
        /// on either leg means that side only fades, e.g. Spatial's Sibling exit / WinUi's Entrance exit).</summary>
        static LayoutTransition SeqTranslate(float enterDx, float enterDy, float exitDx, float exitDy) => new(
            TransitionChannels.Position | TransitionChannels.Opacity, SeqEnter,
            Enter: new EnterExit(Dx: enterDx, Dy: enterDy, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: exitDx, Dy: exitDy, Opacity: 0f, Active: true),
            ExitDynamics: SeqExit, DelayMs: EnterDelayMs, ExitDelayMs: 0f);

        /// <summary>A sequenced semantic zoom: scale + fade, no translate.</summary>
        static LayoutTransition SeqScale(float enterScale, float exitScale) => new(
            TransitionChannels.Opacity, SeqEnter,
            Enter: new EnterExit(Sx: enterScale, Sy: enterScale, Opacity: 0f, Active: true),
            Exit: new EnterExit(Sx: exitScale, Sy: exitScale, Opacity: 0f, Active: true),
            ExitDynamics: SeqExit, DelayMs: EnterDelayMs, ExitDelayMs: 0f);

        /// <summary>The instant cut <see cref="PageMotionStyle.None"/> hands back for every relation: the same shape as
        /// <see cref="MotionRecipes.PageFade"/> — so <c>Exit.Active</c> stays true and the card never flashes empty —
        /// at a duration too short to read as motion at all.</summary>
        static readonly LayoutTransition NoneCut = MotionRecipes.PageFade with
        {
            Dynamics = TransitionDynamics.Tween(1f, Easing.Linear),
            ExitDynamics = TransitionDynamics.Tween(1f, Easing.Linear),
        };

        /// <summary>The recipe for a page swap, WITH its Exit half, at the <see cref="DefaultStyle"/>. Both halves are
        /// load-bearing: the reconciler only overlaps the outgoing page (ZStacked on the boundary, hit-test invisible,
        /// parked once its tracks settle) when <c>Exit.Active</c> is true — with a stripped Exit the outgoing page is
        /// detached in the same frame and the card flashes EMPTY before the incoming page arrives.
        /// <para>The one-argument overload is Entrance (the setup wizard, a first paint). The content host passes the
        /// classified <see cref="NavRelation"/>; <c>Shell.RecipeFor</c> passes the persisted style.</para></summary>
        public static LayoutTransition RecipeFor(NavTransitionKind motion)
            => RecipeFor(motion, motion == NavTransitionKind.Neutral ? NavRelation.Fade : NavRelation.Entrance);

        /// <inheritdoc cref="RecipeFor(NavTransitionKind)"/>
        public static LayoutTransition RecipeFor(NavTransitionKind motion, NavRelation relation)
            => RecipeFor(DefaultStyle, motion, relation);

        /// <summary>The PURE recipe lookup: style × direction × relation → <see cref="LayoutTransition"/>. No pages, no
        /// controls, no GPU — <c>DesignNavMotionTests</c> pins it one style at a time.
        /// <para>CRITICAL: every sequenced branch below passes <c>ExitDelayMs: 0f</c> EXPLICITLY. It is <c>float?</c>
        /// and null means "inherit <c>DelayMs</c>" — omitting it would push the exit out by <see cref="EnterDelayMs"/>
        /// too and flash the content card empty for that whole window (the bug a naive port reintroduces).</para></summary>
        public static LayoutTransition RecipeFor(PageMotionStyle style, NavTransitionKind motion, NavRelation relation)
        {
            if (motion == NavTransitionKind.Neutral || relation == NavRelation.Fade)
                return MotionRecipes.PageFade;
            if (style == PageMotionStyle.None)
                return NoneCut;
            bool back = motion == NavTransitionKind.Back;
            return style switch
            {
                PageMotionStyle.Fluent => SeqFade(),
                PageMotionStyle.Spatial => relation switch
                {
                    NavRelation.DrillIn => back ? SeqScale(DrillScaleOut, DrillScaleIn) : SeqScale(DrillScaleIn, DrillScaleOut),
                    // Incoming only: the outgoing page fades IN PLACE (Exit.Dx stays 0) rather than sliding away with it.
                    NavRelation.Sibling => back ? SeqTranslate(-SiblingDx, 0f, 0f, 0f) : SeqTranslate(SiblingDx, 0f, 0f, 0f),
                    _ => SeqFade(),
                },
                PageMotionStyle.WinUi => relation switch
                {
                    NavRelation.DrillIn => back ? SeqScale(DrillScaleOut, DrillScaleIn) : SeqScale(DrillScaleIn, DrillScaleOut),
                    NavRelation.Sibling => back
                        ? SeqTranslate(-WinUiSiblingInDx, 0f, WinUiSiblingOutDx, 0f)
                        : SeqTranslate(WinUiSiblingInDx, 0f, -WinUiSiblingOutDx, 0f),
                    // Incoming rises; the outgoing page only fades (no Dy) — WinUI's own page-refresh Entrance.
                    _ => back ? SeqTranslate(0f, -WinUiEntranceDy, 0f, 0f) : SeqTranslate(0f, WinUiEntranceDy, 0f, 0f),
                },
                // The 0.2.x fade-through, verbatim, uniform across every relation.
                _ => back ? SeqTranslate(-ClassicDx, 0f, ClassicDx, 0f) : SeqTranslate(ClassicDx, 0f, -ClassicDx, 0f),
            };
        }

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

// ── 16. the shell MATERIAL channel (the types the shell publishes, and the hand-over rule) ────────────────────────────

/// <summary>One shell wash resolved from its WINDOW-relative ellipse to the CLIPPED box the shell actually paints, plus
/// that ellipse re-expressed relative to the clipped box (which is the space a radial gradient's centre/radius live in).
/// <para><see cref="W"/>/<see cref="H"/> are window fractions; <see cref="AnchorRight"/>/<see cref="AnchorBottom"/> say
/// which window edge the box hangs off (a clamped ellipse always touches at least one), so the layer is placed by ZStack
/// self-alignment and never needs a transform. <see cref="Center"/>/<see cref="Radius"/> are node-relative CONSTANTS:
/// the box is a fraction of the window and the ellipse scales with it, so a resize only re-scales the box.</para></summary>
public readonly record struct ShellWashPlacement(
    float W, float H, bool AnchorRight, bool AnchorBottom, Point2 Center, Point2 Radius, float FadeOffset);

/// <summary>One artwork-derived radial wash: the colour at the wash's origin plus the ARTWORK KEY it was graded from.
/// The key is the wash's IDENTITY — two washes with the same resolved colour but different art are different layers, and
/// it is what the shell keys its layer nodes on so an artwork change REMOUNTS (and therefore cross-fades) the layer.
/// Gradients carry no brush-fade channel, so a wash can only cross-fade BY MOUNT.</summary>
public readonly record struct WashLayer(ColorF Color, string? ArtworkKey);

/// <summary>Home's three-wash composition, in STACKING order. Any leg may be null — a module whose artwork has not been
/// graded yet simply contributes no layer.</summary>
public readonly record struct HomeWash(WashLayer? Hero, WashLayer? Weekly, WashLayer? Mix);

/// <summary>The published shell-material state: an OWNER token plus the two mutually-exclusive material forms — a flat
/// <see cref="Tint"/> (detail pages) or a three-layer radial <see cref="Wash"/> (Home). Both null ⇒ the neutral ground.
/// <para>The owner makes nav transitions race-free: only the page that last CLAIMED the slot may refresh it.</para></summary>
public readonly record struct ShellMaterialState(object? Owner, ColorF? Tint, HomeWash? Wash);

/// <summary>The shell-owned, page-scoped MATERIAL channel. The shell publishes one signal at the root and paints it as
/// the layer directly above the ground that backs ALL chrome — title bar, toolbar, sidebar, player dock. The active page
/// CLAIMS it, so the window's chrome carries the album/playlist/Home colour; a page without a colour of its own is
/// claimed neutral by the content host.
/// <para>This IS a Mica scrim: the chrome is Mica-passthrough, so the material composites over the live window material
/// and carries the page's hue into it, while the content pane above keeps its own surface so the PAGE never depends on
/// the wallpaper. No native interop, no GPU pass — one flat rect, or up to three clipped radial-gradient rects.</para></summary>
public static class ShellMaterial
{
    /// <summary>Context slot — the shell provides its material signal here. Null when no shell is mounted (headless
    /// tests), in which case a consumer simply no-ops.</summary>
    public static readonly Context<Signal<ShellMaterialState>?> Slot = new(null);

    /// <summary>THE one way anything writes the material — a HAND-OVER, never a clear. A page CLAIMS the slot on its
    /// first publish and on a KeepAlive reactivation; any other publish is a refresh that lands only while that page is
    /// still the owner, so the exit tail of a page that was navigated away from can never paint over its successor. A
    /// page that parks or unmounts writes NOTHING: its material stays until the next page claims, and a claim whose
    /// colour is not graded yet KEEPS whatever is showing.
    /// <para>The consequence that matters visually: <b>the chrome never dips to neutral and back between two coloured
    /// pages.</b></para></summary>
    /// <param name="definite">The page has DECIDED it carries no colour (washes off, or this layout applies no tint) —
    /// as opposed to simply not having graded a cover YET, which is transient.</param>
    public static void Publish(Signal<ShellMaterialState>? slot, object owner, bool isClaim, bool definite,
                               ColorF? tint, HomeWash? wash)
    {
        if (slot is null) return;
        var cur = slot.Peek();
        // `hasColor` counts a WASH leg too, so Home's three-radial form participates in the same protocol as a detail
        // page's flat tint.
        bool hasColor = tint.HasValue
                        || (wash is { } w && (w.Hero.HasValue || w.Weekly.HasValue || w.Mix.HasValue));
        switch (TintOwnership.Resolve(cur.Owner, new TintOwnership.Request(owner, isClaim, definite, hasColor)))
        {
            case TintOwnership.Outcome.WriteKnownColor: slot.Value = new ShellMaterialState(owner, tint, wash); break;
            case TintOwnership.Outcome.WriteNeutral: slot.Value = new ShellMaterialState(owner, null, null); break;
            case TintOwnership.Outcome.WriteHeldColor: slot.Value = cur with { Owner = owner }; break;
        }
    }
}

/// <summary>The material hand-over protocol, as a pure decision table. Two shipped bugs are encoded here and neither is
/// re-derivable from the call sites:
/// <list type="number">
/// <item><b>The neutral dip.</b> A page that deactivated used to CLEAR the slot, and the page claiming it did not yet
/// know its own colour (the grading was still in flight) — so every navigation dipped to "no colour" and then popped to
/// the real one. The fix is a HAND-OVER: a deactivating page never writes at all (there is no "clear" request in this
/// API), and a page that is CLAIMING but does not yet know its colour keeps whatever is already there.</item>
/// <item><b>The two-publisher race.</b> KeepAlive keeps an outgoing page mounted and drawing for the length of its exit
/// transition, so its effects can still fire — and unconditionally re-publish its own colour — WHILE the incoming page
/// is already current. The fix is single ownership: once a page has CLAIMED, every other page's ordinary publish is
/// checked against the CURRENT owner and dropped if it does not match, regardless of that page's own parked/active
/// state.</item>
/// </list></summary>
public static class TintOwnership
{
    /// <summary>One publish attempt from a binder leaf.</summary>
    /// <param name="Owner">The publishing page's identity token (REFERENCE identity only — never compared by value).</param>
    /// <param name="IsClaim">True at the two edges that make a page "become current": its own first mount, and a
    /// KeepAlive reactivation. False for an ordinary re-render.</param>
    /// <param name="Definite">True when the page has DECIDED it carries no colour — as opposed to simply not having
    /// graded a cover YET, which is transient.</param>
    /// <param name="HasColor">True when the page currently has a real, graded colour to show.</param>
    public readonly record struct Request(object Owner, bool IsClaim, bool Definite, bool HasColor);

    /// <summary>What the caller should do with the slot. <see cref="NoWrite"/> means "touch nothing" — the write must
    /// not happen at all, not merely happen to reproduce the current value, because a no-op write would still need
    /// somewhere to source its colour from and this decision is precisely that a stray publisher gets none.</summary>
    public enum Outcome
    {
        /// <summary>Leave the slot exactly as it is.</summary>
        NoWrite,
        /// <summary>Write the caller's own known colour as this page's tint.</summary>
        WriteKnownColor,
        /// <summary>Write the neutral ground — this page has DEFINITELY decided it carries no colour.</summary>
        WriteNeutral,
        /// <summary>Take ownership, but keep painting whatever the slot already shows (the hand-over).</summary>
        WriteHeldColor,
    }

    /// <summary>The one decision every shell-tint publish goes through.</summary>
    public static Outcome Resolve(object? currentOwner, in Request req)
    {
        bool amOwner = req.Owner is not null && ReferenceEquals(currentOwner, req.Owner);
        // Not the current owner and not claiming ⇒ a superseded/stray publish. Never lands.
        if (!req.IsClaim && !amOwner) return Outcome.NoWrite;

        if (req.Definite) return req.HasColor ? Outcome.WriteKnownColor : Outcome.WriteNeutral;
        if (req.HasColor) return Outcome.WriteKnownColor;

        // Colour genuinely not known yet: a claim hands over silently; an ordinary refresh from the already-current
        // owner has nothing new to say, so it writes nothing rather than repeat itself.
        return req.IsClaim ? Outcome.WriteHeldColor : Outcome.NoWrite;
    }
}

// ── 17. the four cover-leaf COMPONENTS ────────────────────────────────────────────────────────────────────────────────
//
// The only four nodes in this file that are Components, and the only four that SUBSCRIBE. Cover-colour watches must NOT
// sit in a page's `Render()`: a graded batch would then re-render the whole Artist/Detail/Home tree, including every
// shelf in it. Here the subscription lives in a LEAF, and the paint-only tint lives in a bound `Fill`, so a landed
// grading re-renders one node (or marks one tile PaintDirty) and nothing else.
//
// All four take a re-pushed PROPS record (`Embed.Comp(props, factory)` + `UseProps<T>()`) rather than frozen constructor
// fields, because every one of their inputs changes while they stay mounted. Their MOUNT helpers live in
// `Entities/Palette.Host.cs`.

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
public sealed class CoverPageTonePlane : Component
{
    /// <summary>Re-pushed props. <paramref name="BackdropBand"/> is the hero band's own height in DIP, supplied by the
    /// CALLER (owner M's detail vertical layout) rather than imported here — this file must not depend on the detail
    /// frame's arithmetic (A1).</summary>
    public sealed record Props(string? Url, string? FallbackUrl, bool Disabled, float BackdropBand, float PageHeight,
                               bool HeroOnly, uint PayloadAccent = 0);

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
        var p = UseProps<Props>();

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
    static ColorF? Resolve(Props p)
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
    static Element? HeroOnlyVeil(Props p)
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

/// <summary>The pre-settlement Exit guard <see cref="CoverArtistBlendWash"/> and <see cref="CoverKeyedVeil"/> share,
/// pulled out of both Render methods as a pure decision so it is unit-testable without a render loop (repo rule:
/// no source-text tests — extract the decision, test the extraction).
/// <para><b>The defect:</b> <c>Palette.TryScheme</c> enqueues on a miss, so the render right after mount routinely
/// resolves to a Payload/Default GUESS before the real grading lands a frame or two later. Both leaves re-key on
/// rung + colour (a Gradient can't cross-fade through <c>BrushTransitionMs</c>), so that guess-to-graded swap is a
/// keyed node replacement. If the OUTGOING (guessed) node animates its Exit the same way a genuine LATER re-key
/// does, its fade-out overlaps the INCOMING (graded) node's fade-in — two differently-hued, semi-transparent veils
/// summing over the artist photo for one <c>ControlNormal</c>, which reads as the lighter, more opaque wash this
/// exists to prevent.</para>
/// <para><b>The fix:</b> every swap before the ladder has produced a DEFINITIVE first answer (a real grading hit,
/// or Definite because nothing better can ever arrive — no url, or a url the endpoint cannot grade) exits INSTANTLY
/// (no Exit spec ⇒ immediate structural removal, no deferred fade) instead of animating. The incoming node can
/// still fade its Enter in on its own — with the outgoing one already gone, there is nothing left for it to
/// composite against, so at most one veil is ever live. Once settled, later changes (a live re-grading, a theme
/// flip) cross-fade normally, same as before this fix.</para>
/// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Controls.cs</c>), and the
/// invariant is pinned by a fact in <c>Wavee.Tests</c>.</para></summary>
public static class VeilSettlement
{
    /// <summary>Advances the leaf's settled state for this frame and says whether the node this frame's re-key
    /// REPLACES may animate its Exit. <paramref name="alreadySettled"/> is the leaf's state entering the frame;
    /// the returned <c>Settled</c> is what the leaf should hold for the NEXT frame. Settlement is one-way — once
    /// true it stays true for the life of the component instance (a fresh mount, e.g. navigating to a different
    /// artist, gets a fresh instance and starts unsettled again).</summary>
    public static (bool Settled, bool ExitAnimates) Advance(bool alreadySettled, AccentLadder.Rung rung, bool definite)
    {
        bool settled = alreadySettled || rung == AccentLadder.Rung.Graded || definite;
        return (settled, settled);
    }
}

/// <summary>The artist page's blend wash. Height and boundary come from the CALLER (owner N's hero layout) so this file
/// carries no artist-page arithmetic.</summary>
public sealed class CoverArtistBlendWash : Component
{
    /// <inheritdoc cref="CoverArtistBlendWash"/>
    public sealed record Props(string? Url, float Height, float Boundary, bool Disabled, uint PayloadAccent = 0);

    bool _mounted;
    /// <summary>Has the ladder ever produced a DEFINITIVE first answer (a real grading hit, or Definite because
    /// nothing better can ever arrive)? See the Exit guard below — the whole point of this field.</summary>
    bool _settled;

    public override Element Render()
    {
        var p = UseProps<Props>();
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
public sealed class CoverKeyedVeil : Component
{
    /// <inheritdoc cref="CoverKeyedVeil"/>
    public sealed record Props(string? Url, bool Vertical, float Width, float Height, uint PayloadAccent = 0);

    static readonly Func<uint, ColorF> s_lift = static a => ColorScience.Lift(ColorScience.ToColor(a));
    bool _mounted;
    /// <summary>See the identical field on <see cref="CoverArtistBlendWash"/> and the Exit guard below — this leaf
    /// veils the same photo the wash sits behind, so it carries the same defect and the same fix.</summary>
    bool _settled;

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        var pagePal = Design.SchemeFor(p.Url);
        // The page palette's own lifted accent is the graded rung; the payload (header) accent and the neutral
        // FillLayerDefault (ArtistHeroVeil then lerps into the layer colour itself = no tint) fill the rest of the
        // ladder — the old chrome-scheme fallback is gone, subsumed by the shared ladder.
        ColorF? graded = pagePal is { } wp ? ColorScience.Lift(ColorScience.Accent(wp)) : null;
        bool definite = string.IsNullOrEmpty(p.Url) || !Palette.CanGrade(p.Url);
        var result = AccentLadder.Resolve(new(graded, p.PayloadAccent, definite), null, Tok.FillLayerDefault, s_lift);
        // Keyed swap: see CoverArtistBlendWash — a Gradient can't cross-fade through BrushTransitionMs, and a
        // component-root Key is inert (ReconcileSingleChild), so the keyed node is a CHILD.
        //
        // PRE-SETTLEMENT EXIT GUARD — see VeilSettlement's doc for the defect and the fix.
        var (settled, exitAnimates) = VeilSettlement.Advance(_settled, result.Rung, definite);
        Element veil = new BoxEl
        {
            Key = "artist-veil-tone:" + (byte)result.Rung + ":" + result.Color.GetHashCode().ToString("X8"),
            HitTestVisible = false, Gradient = Controls.ArtistHeroVeil(result.Color, p.Vertical),
            // Fade only a re-key, never the first paint - and never a PRE-SETTLEMENT re-key either: see the sibling
            // in CoverArtistBlendWash. An Enter with no Exit beneath it unveils the photo for the length of the fade.
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
public sealed class CoverShellTintBinder : Component
{
    /// <inheritdoc cref="CoverShellTintBinder"/>
    public sealed record Props(string? Url, string? FallbackUrl, bool Ready, bool Disabled, bool Apply, object Owner,
                               Signal<ShellMaterialState>? Slot, uint PayloadAccent = 0);

    // The two tint arms are the style's `R.Wash.ShellTintFromScheme` (Classic: LIGHT lifts the cover's TEXT role to a 5% whisper; DARK takes
    // the tinted background role at 14%: two different ROLES, not one role at two alphas) and `R.Wash.ShellTintAlpha` for a raw payload
    // colour. The tint WARMS the ground; it never replaces it.

    public override Element Render()
    {
        var p = UseProps<Props>();
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

        void Publish(bool isClaim) => ShellMaterial.Publish(p.Slot, p.Owner, isClaim, definite, known, wash: null);

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
