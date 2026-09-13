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
//     captures the palette at type-init and never re-themes. The only sanctioned `static readonly` colours here are the
//     on-media literals, which are theme-INVARIANT by contract.
//  2. THE TWO GRADING HALVES STAY TWO FUNCTIONS (§7). Collapsing them is invisible in dark and glaring in light.
//  3. THE 22-ITERATION BISECTION IN `Palette.Hairline` STAYS 22 ITERATIONS. It is the contrast solve; a cheaper
//     approximation changes every identity hairline and every accent-ink digit in the app.
//  4. THE CLAMP CONSTANTS ARE NOT TUNING KNOBS. If a page should take more colour, raise the PLANE ALPHA pair, never
//     the page-tone clamp.
//
// ── THE TWO NAME COLLISIONS, STATED ONCE ─────────────────────────────────────────────────────────────────────────────
//
// Inside `class Design` the nested names win over the outer ones, so two references must be spelled the long way and are
// spelled that way everywhere below:
//   · `FgMotion` (the alias at the top of the file) = `FluentGpu.Dsl.Motion`, shadowed by `Design.Motion`.
//   · `Wavee.Palette` (the TABLE — the per-image graded schemes, the TTL, `Watch`, `Ensure`) is shadowed by
//     `Design.Palette` (the ARGB MATHS). Nothing in `Design.Palette` touches a table, a fetch or a readiness bit (A5).

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

    /// <summary>Fixed control / surface dimensions.</summary>
    public static class Size
    {
        public const float ControlH = 32f, NavItemH = 44f, TrackRowH = 56f, PlayerBarH = 72f;   // taller dock: room for the seek row
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

        /// <summary>The vertical rhythm BETWEEN page sections / feed modules — the wide rung at desktop widths and the
        /// narrow rung below the layout's own breakpoint. Home's module gap and the artist page's section stack read the
        /// SAME two constants, so a section boundary is the same distance in both places.</summary>
        public const float SectionGap = 32f, SectionGapWide = 40f;

        /// <summary>The widest a page's content column grows before it stops tracking the window. The detail frame, the
        /// artist page and Home all cap their rows here, so the three line up at the same measure on an ultra-wide
        /// display instead of one of them running edge to edge.</summary>
        public const float PageMaxW = 1600f;

        /// <summary>The zoom design box's two numbers, PROMOTED (ch 00 DATA GAPS). <see cref="DesignW"/> IS
        /// <see cref="PageMaxW"/>; <see cref="DesignH"/> is the detail frame's tall-hero gate (winH ≥ 900), which in
        /// 0.2.9 was a bare literal inside the zoom policy with no token twin at all.
        /// <para>`ZoomAutoPolicy` keeps its own BCL-only literals so it stays source-includable; the convergence test
        /// that pins the two pairs is this wave's.</para></summary>
        public const float DesignW = PageMaxW, DesignH = 900f;

        /// <summary>THE horizontal shelf's card-width band and the grid gap (ch 00 §12.2 magic-number row 4). Three
        /// surfaces authored 148/188 by hand and a fourth invented 200; one name here is what stops a fifth.</summary>
        public const float ShelfCardMin = 148f, ShelfCardMax = 188f, GridGap = 12f;

        /// <summary>The edge-fade ladder (ch 00 §12.2 magic-number row 2). A scroll viewport feathers its clipped edge by
        /// exactly one of these: a chip rail 16, a card shelf 24, the right rail's own 36 (the engine default).</summary>
        public const float FadeChip = 16f, FadeShelf = 24f, FadeRail = 36f;

        /// <summary>The "…and N more" attribution flyout hung off a face pile (ch 00 §12.2 magic-number row 1). Four
        /// hand copies of a nine-number shape is how one of them silently became 320 instead of 344.</summary>
        public const float FacePileFlyoutW = 280f, FacePileFlyoutH = 360f,
                           FacePileListW = 264f, FacePileListH = 344f;
        /// <inheritdoc cref="FacePileFlyoutW"/>
        public const float FacePileRowH = NavItemH;

        /// <summary>The content region's ONE rounded corner, facing the nav pane. The shell reads it; so does the page
        /// tone plane, which is a page-root SIBLING of the scrolling page and must clip to the same shape.</summary>
        public static readonly CornerRadius4 ContentPaneCorners = new(Radii.Card, 0f, 0f, 0f);
    }

    /// <summary>The bottom player-bar dock geometry. Pages reserve this height so their last row clears the transport.
    /// <see cref="Reserve"/> is also the wash host's bottom margin — the Mix wash's PEAK used to land across the dock
    /// band, which is what read as "the dock has a pastel gradient" (ch 00 W8). It never had one; it had the shell's.</summary>
    public static class Dock
    {
        public const float BarH = 72f;
        public const float Margin = 0f;
        public const float Reserve = BarH;
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

    /// <summary>THE ACCENT BUDGET — the three roles accent colour is allowed to play, and the rules that keep them apart.
    ///
    /// <para>Wavee is deliberately NOT a monochrome Fluent app: the artwork-derived accent is identity, and it stays.
    /// What it stopped being is AMBIENT. An accent that appears in four unrelated jobs on one page stops meaning any of
    /// them — the reader can no longer tell "click this" from "you are here" from "this is a section". So every accent
    /// paint site declares which of exactly THREE roles it is:</para>
    ///
    /// <list type="number">
    /// <item><b>Action</b> — "clicking this does the page's primary thing." A SOLID accent plate behind on-accent ink.
    /// AT MOST ONE per screenful — if a surface has two, one of them is a secondary and takes the stock ramp. This is
    /// the scarcest role and the reason an empty state's action is quiet: an empty page's "Browse" must not outrank the
    /// page's real primary.</item>
    /// <item><b>Selection</b> — "you are here." Geometry is the tell and it is RESERVED: a short accent bar/pill against
    /// the edge of a control means selection and means nothing else.</item>
    /// <item><b>Decor</b> — "this content has a colour." Section spines, hero washes, eyebrows, accent facts. Accent as
    /// TEXT or as a wash, never as a plate behind ink and never as a selection-shaped bar. The largest role by site
    /// count, and it is the identity.</item>
    /// </list>
    ///
    /// <para>THE TWO HARD RULES, both enforced by construction:</para>
    /// <list type="bullet">
    /// <item>A decorative ornament may not take SELECTION geometry. The artist section header's old 3×22 r1.5 accent
    /// capsule was pixel-for-pixel the selection-indicator shape doing a decorative job; it is now the 20×2 underline
    /// <see cref="Controls.AccentRule"/> draws, which is unmistakably a rule.</item>
    /// <item>Accent is never STRUCTURE. A border, a divider, a chevron or a disclosure glyph is chrome; it takes
    /// <c>Tok.StrokeDividerDefault</c> / <c>Tok.TextSecondary</c>. Accent on structure was the single largest source of
    /// ambient accent in the app.</item>
    /// </list></summary>
    public static class Accent
    {
        /// <summary>Role 1 — the SOLID plate behind on-accent ink for the page's ONE primary action. Artwork-derived
        /// surfaces pass their own graded fill to the CTA instead of reading this.</summary>
        public static ColorF Action => Tok.AccentDefault;

        /// <summary>Role 2 — "you are here". The bar/pill geometry that carries it is reserved to selection.</summary>
        public static ColorF Selection => Tok.AccentDefault;

        /// <summary>Role 3 — accent as CONTENT colour: text, spines, washes. The contrast-corrected accent INK, never
        /// the raw fill, because this role always paints on the page surface rather than under on-accent ink.</summary>
        public static ColorF Decor => Tok.AccentTextPrimary;
    }

    // ══ 3. COLOUR ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wavee's app-shell colours. The authenticated shell is the STOCK Windows 11 Mica stack:
    /// <list type="number">
    /// <item>BASE LAYER — Mica passthrough. The shell root paints NOTHING; every chrome band (merged title row, sidebar,
    /// player dock) is a paint-site OMISSION over the live window material. <see cref="ShellGround"/> survives as the
    /// no-Mica FALLBACK and as the flatten base for opaque floating surfaces.</item>
    /// <item>MATERIAL — the page-published layer above the backdrop (<see cref="ShellMaterialState"/>): a low-alpha flat
    /// tint, or Home's three clipped radial washes — a scrim over Mica, so the material carries the page's hue.</item>
    /// <item>CONTENT LAYER — <see cref="FileArea"/> (the stock layer fill), TRANSLUCENT, PAIRED with a 1-px
    /// <c>Tok.StrokeCardDefault</c> stroke on its LEFT+TOP edges only, ONE rounded corner, and NO shadow. That pairing
    /// is the whole separation model — a translucent fill alone is too small a step to read as an edge, and stock never
    /// adds elevation to this layer.</item>
    /// </list>
    /// <para>EVERY MEMBER IS A PROPERTY (rule 1 in the file header). A `static readonly ColorF` here is a bug.</para></summary>
    public static class Colors
    {
        static ShellPalette ActiveShell => Tok.Theme == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;

        // The translucent MUX rungs. The authenticated shell no longer PAINTS them — every chrome band is a paint-site
        // omission over the ground — but they stay published verbatim: the palette contrast gates measure the tint
        // through them, and the palette recipes are defined in terms of them.
        public static ColorF Toolbar => ActiveShell.Toolbar;
        public static ColorF Sidebar => ActiveShell.Sidebar;
        public static ColorF PlayerBar => ActiveShell.PlayerBar;
        public static ColorF FileArea => ActiveShell.FileArea;
        public static ColorF Content => ActiveShell.Content;
        public static ColorF ContentAlt => ActiveShell.ContentAlt;

        /// <summary>Premium green. Light takes the system success token; dark keeps the brand value, which the system
        /// token is too dim for over a #202020 ground.</summary>
        public static ColorF PremiumText =>
            Tok.Theme == ThemeKind.Light ? Tok.SystemFillSuccess : ColorF.FromRgba(0x1D, 0xB9, 0x54);

        /// <summary>The chrome GROUND, as a value — for a floating pane that stands in for a docked CHROME band (the
        /// narrow nav drawer, the sidebar preview well, the floating right-rail backing). Identical to what the shell
        /// paints under the docked band, and opaque by construction: the page it covers must not read through it.</summary>
        public static ColorF FloatingChrome => ShellGround;

        /// <summary>The CONTENT surface, as a value — for a floating surface that stands in for a docked content band,
        /// and for the login view, which has no shell under it at all. Same rung as <see cref="ContentSurface"/> by
        /// definition; the two names mark intent (docked surface vs floating stand-in), not different colours.</summary>
        public static ColorF FloatingPane => ContentSurface;

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

        /// <summary>The chrome GROUND: the full-bleed opaque rect the whole authenticated shell sits on. Light is the
        /// stock solid base #F3F3F3 dropped to #EDEDED — #F3F3F3 read as PAGE, not chrome. Dark keeps #202020 verbatim:
        /// it is already that band.</summary>
        public static ColorF ShellGround => ShellGroundFor(
            Tok.Theme == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark, Tok.Theme);

        /// <summary>Pure overload of <see cref="ShellGround"/> for an arbitrary token set — the gates resolve a palette
        /// that is not the active one without mutating global theme state.</summary>
        public static ColorF ShellGroundFor(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
            ? ColorRamp.Darken(set.FillSolidBase, LightGroundDrop)
            : set.FillSolidBase;

        /// <summary>The OPAQUE content rung — ONE step above the ground. NOT what the docked shell paints any more: this
        /// is the wallpaper-independent stand-in every FLOATING content surface takes, and the value the palette gates
        /// flatten the translucent ladder against. Light resolves to #F9F9F9 over the #EDEDED ground; dark is a
        /// deliberate #282828 over the #202020 ground (the dark solid-tertiary rung lifts 6% to #2D2D2D, which reads as
        /// a second card rather than as the page).</summary>
        public static ColorF ContentSurface => ContentSurfaceFor(
            Tok.Theme == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark, Tok.Theme);

        /// <summary>Pure overload of <see cref="ContentSurface"/>, so the gates pin both themes without mutating the
        /// active one.</summary>
        public static ColorF ContentSurfaceFor(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
            ? set.FillSolidTertiary
            : ColorRamp.Lighten(set.FillSolidBase, DarkContentLift);

        /// <summary>The content step expressed as a translucent white LAYER. It composites to
        /// <see cref="ContentSurface"/> over the bare ground AND lands on the TINTED equivalent over a page's published
        /// shell tint, which an opaque fill cannot (it would punch a neutral hole through the wash).
        /// <para>Nothing in the shell paints this today. It stays published as the LAYER-equivalent of the opaque rung:
        /// the identity <c>Over(ContentLayer, ShellGround) == ContentSurface</c> is what a fallback or a gate reasons
        /// with in either space, and it is pinned by test. If a chrome-band-scale plate is ever wanted again, this is
        /// the value it takes — not a hand-mixed grey.</para></summary>
        public static ColorF ContentLayer => ContentLayerFor(Tok.Theme);

        /// <inheritdoc cref="ContentLayer"/>
        public static ColorF ContentLayerFor(ThemeKind theme) => ColorF.FromRgba(0xFF, 0xFF, 0xFF,
            (byte)((theme == ThemeKind.Light ? LightContentLayerA : DarkContentLift) * 255f + 0.5f));

        // ── the LIST row ladder ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>THE zebra stripe — the resting plate on an odd row of a LONG tracklist, and nothing else. Zebra is a
        /// scanning aid for lists long enough to lose your place in; a 6-row queue or a friends rail gets plain rows
        /// plus the standard hover, because striping a short list only adds noise.
        /// <para>DARK derives it from the engine's SUBTLE-FILL ink at its quietest rung because the shell's own dark
        /// zebra is LITERALLY the hover fill (both 0x0F), so hovering a striped row moved the surface by five alpha
        /// steps and read as nothing at all. A stripe MUST be quieter than hover or the row has no hover. LIGHT takes
        /// the shell's own row ladder, which is the black-alpha stripe solved TOGETHER with the light hover/press rungs
        /// against the art-derived page tone.</para></summary>
        public static ColorF RowZebra => Tok.Theme == ThemeKind.Light ? ActiveShell.RowZebra : Tok.FillSubtleTertiary;

        public static ColorF RowHover => ActiveShell.RowHover;
        public static ColorF RowPressed => ActiveShell.RowPressed;

        /// <summary>Hover/press ON a striped row: the row state SOURCE-OVER the stripe, collapsed to ONE translucent
        /// fill (a row paints a single <c>Fill</c>, never two stacked plates). <c>ColorContrast.Over</c> is associative,
        /// so the merged rung composites pixel-identically to painting the two rungs in sequence.
        /// <para>THE PRESS DIRECTION INVERTS BY THEME and BOTH arms are correct: LIGHT pressed (7.1%) is ABOVE hover
        /// (5.1%) — black ink deepening; DARK pressed (0x0A) is BELOW hover (0x0F) — the white subtle-fill ladder's own
        /// rest→hover(up)→pressed(down-but-above-rest) shape. Do NOT "fix" one to match the other.</para></summary>
        public static ColorF RowHoverZebra => ColorContrast.Over(RowHover, RowZebra);
        /// <inheritdoc cref="RowHoverZebra"/>
        public static ColorF RowPressedZebra => ColorContrast.Over(RowPressed, RowZebra);

        // ── THE SELECTION LADDER ─────────────────────────────────────────────────────────────────────────────────────
        //
        // "You are here" for a NAV row (accent role 2), and the ordering law that goes with it.
        //
        // THE BUG THIS REPLACES WAS AN INVERSION, not a tuning miss. Every sidebar row in every design painted the same
        // trio: selected-at-rest = the subtle-secondary fill, and hovering that selected row swapped DOWN to
        // subtle-tertiary. So pointing at the row you are already on made the app look like it had deselected it, while
        // an unselected row hovering to the higher rung was simultaneously the loudest thing in the pane.
        //
        // THE FIX IS THREE RUNGS THAT ONLY EVER GO UP, and it COMPOSES rather than swaps:
        //   rest    = Tok.AccentSubtle (accent @ 14% light / 16% dark) — WinUI ships it for exactly this job.
        //   hover   = the standard hover veil composed OVER that plate, so hovered-selected is strictly stronger.
        //   pressed = the quieter press veil over the same plate: above rest, below hover — WinUI's own shape.
        //
        // The accent is COLOUR only: the reserved selection GEOMETRY is still the 3-DIP pill, and this plate never
        // appears without it. BROWSE/LIST selection is deliberately NOT this — a track row, a library hit and a
        // suggestion row keep the neutral ladder above, because spending the page's accent on a row that is not an
        // action makes the same state look like two different states depending on which list you are in.
        public static ColorF SelectedRest => Tok.AccentSubtle;
        public static ColorF SelectedHover => ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle);
        public static ColorF SelectedPressed => ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle);

        public static ColorF ChromeHover => Tok.FillSubtleSecondary;
        public static ColorF ChromePressed => Tok.FillSubtleTertiary;
        public static ColorF Badge => Tok.AccentDefault;

        // TWO TOMBSTONES, kept so neither is re-invented:
        //  · The palette PICKER (Settings + the profile menu) and its swatch preview are gone — Wavee always renders the
        //    neutral palette. `Platform.cs`'s theme resolver survives as the ONE place a future preset would be named.
        //  · The sticky context band has NO material here. It used to flatten the translucent content rung onto a
        //    neutral no-wallpaper reference tone, and that was still wrong: live Mica takes its colour from the USER'S
        //    DESKTOP and a constant cannot follow it (on a dark wallpaper it read as a solid black slab across the
        //    page). The band now paints NOTHING and scrolled content is CLIPPED at its lower edge, so the band region
        //    shows the page's real ground whatever that is, and there is no constant left to drift.
    }

    // ══ 4. THE ON-MEDIA LADDER ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE on-media colour ladder: everything that paints ON TOP of artwork or video — the scrim plates
    /// behind a floating "…"/FAB/chip, the hairline that rings them, the ink on them, the dim under a blurred stand-in
    /// backdrop, and the light-theme on-media button ramp.
    ///
    /// <para><b>Why it exists.</b> The card layer alone hand-rolled 36 colour literals for this one job, including THREE
    /// near-duplicate scrim ladders that disagreed by 10-50 alpha steps for visually identical affordances
    /// (185/225/245 · 132/190/220 · 120/184/218). They are collapsed here into ONE ladder built from the MIDDLE of the
    /// three, except that the REST rung takes the engine's own <c>Tok.MediaScrim</c> rather than minting an app-local
    /// near-duplicate 8 alpha steps away — the engine already owns "the chip/pill/FAB scrim plate floated over media".
    /// The visible consequence is that the corner "…" and the cover FAB got LIGHTER and the save button slightly darker:
    /// three affordances that sit on the same artwork now share one plate instead of advertising three.</para>
    ///
    /// <para><b>Theme invariance is the point, not an oversight.</b> Ink over artwork is theme-INVARIANT, so these
    /// plates stay dark and this ink stays white in light mode too. The one place a THEME does change the answer — the
    /// immersive stage — is <see cref="StageArm"/>'s, and it is the only surface that gets a polarity.</para></summary>
    public static class OnMedia
    {
        // ── scrim plates (a small dark surface floated over art) ─────────────────────────────────────────────────────

        /// <summary>The resting plate: the engine's canonical on-media plate (black @ 0.55).</summary>
        public static ColorF ScrimRest => Tok.MediaScrim;

        /// <summary>Hover — black @ 190/255. BLACK, not the old (20,20,20): a 20-per-channel lift under a 0.75 alpha is
        /// invisible over artwork and existed in exactly one of the three ladders.</summary>
        public static readonly ColorF ScrimHover = ColorF.FromRgba(0, 0, 0, 190);

        /// <summary>Pressed — the middle of 245 · 220 · 218.</summary>
        public static readonly ColorF ScrimPressed = ColorF.FromRgba(0, 0, 0, 220);

        /// <summary>The full-cover hover veil — a whole square of artwork dimmed so a centred FAB reads on it. A
        /// different ROLE from <see cref="ScrimRest"/> (which is a small plate), so it keeps its own, LIGHTER value:
        /// black @ 110/255.</summary>
        public static readonly ColorF CoverScrim = ColorF.FromRgba(0, 0, 0, 110);

        /// <summary>The hairline that rings every on-media plate: white @ 58/255 — the middle of the old 70/58/55, and
        /// already what two of the three sites used. Doubles as the faint on-media RULE (the countdown ring's
        /// track).</summary>
        public static readonly ColorF Stroke = ColorF.FromRgba(255, 255, 255, 58);

        // ── ink (the engine's on-media tiers, reused verbatim) ───────────────────────────────────────────────────────

        /// <summary>Primary on-media ink: titles, chip labels, glyphs, the swept countdown arc. Collapses the old
        /// 224/225/230/235 near-whites, which were four ways of writing "white" over a dark scrim.</summary>
        public static ColorF Ink => Tok.OnMediaPrimary;
        /// <summary>Secondary on-media ink: eyebrows and subtitles over art. White @ 0.80.</summary>
        public static ColorF InkSecondary => Tok.OnMediaSecondary;
        /// <summary>Tertiary on-media ink: captions / meta over art. White @ 0.60.</summary>
        public static ColorF InkTertiary => Tok.OnMediaTertiary;

        // ── glass: the on-media INTERACTION ramp for a control carrying NO resting plate ──────────────────────────────
        // The scrim ladder above is a PLATE — a small dark surface a control sits on permanently. Glass is its opposite:
        // nothing at rest, a breath of the on-media INK on hover, one rung more on press. It is what the immersive stage
        // paints, where the rule is "everything is ink on the scrim" and a dark plate under every hovered row would
        // re-plate a surface whose whole premise is that it has none.

        /// <summary>Rest: nothing. Stated as a rung so a call site never spells Transparent and reads as "no state
        /// model".</summary>
        public static ColorF GlassRest => ColorF.Transparent;
        /// <summary>Hover — the on-media ink at 10%: the lightest fill that still reads as a surface over artwork.</summary>
        public static ColorF GlassHover => Tok.OnMediaPrimary with { A = 0.10f };
        /// <summary>Pressed — one rung on, at 16%: the ratio the subtle ladder keeps between its two rungs.</summary>
        public static ColorF GlassPressed => Tok.OnMediaPrimary with { A = 0.16f };

        // ── the INK PLATE: a resting ground made of ink rather than of scrim ─────────────────────────────────────────
        // For the one control that must be findable without hunting — the immersive stage's way out. The scrim ladder is
        // the wrong tool THERE, and the reason is arithmetic rather than taste: the stage's top band is already deepened
        // to 0.76 black on every cover, so a 55%-black plate on a 76%-black ground has no edge at all — only the
        // hairline ring and the glyph survive, which is precisely the "I cannot find the way out" report. On a ground
        // that is already dark, separation has to come from LIGHT.

        /// <summary>The resting ground of an ink-plated on-media control. Deliberately ABOVE <see cref="GlassHover"/> —
        /// this is a REST state, not a hover breath, and it has to hold its own edge before the pointer arrives.</summary>
        public static ColorF GlassPlate => Tok.OnMediaPrimary with { A = 0.14f };
        /// <inheritdoc cref="GlassPlate"/>
        public static ColorF GlassPlateHover => Tok.OnMediaPrimary with { A = 0.22f };
        /// <inheritdoc cref="GlassPlate"/>
        public static ColorF GlassPlatePressed => Tok.OnMediaPrimary with { A = 0.28f };

        // ── backdrop treatments ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The dim laid over a BAKED-BLUR derivative so it reads as a backdrop rather than as a stretched
        /// square. Two surfaces disagreed by 0.03 alpha for no stated reason; one value now.</summary>
        public static readonly ColorF BackdropDim = ColorF.FromRgba(8, 8, 10) with { A = 0.45f };

        /// <summary>The hover spotlight's inner stop — a soft white bloom under the pointer on an editorial cover.</summary>
        public static readonly ColorF SpotlightInner = ColorF.FromRgba(255, 255, 255, 46);
        /// <summary>The hover spotlight's mid stop (it falls to transparent at the edge).</summary>
        public static readonly ColorF SpotlightMid = ColorF.FromRgba(255, 255, 255, 20);

        // ── the LIGHT on-media button ramp ───────────────────────────────────────────────────────────────────────────
        // Derived from the on-media ink rather than hardcoded greys, so a palette preset that ever re-tints the on-media
        // whites carries the button's whole ramp with it. The old literals were 255/235/215 with (12,12,14) ink; the
        // fractions below reproduce them to within one 8-bit step.

        internal const float LightHoverDrop = 0.08f, LightPressedDrop = 0.157f;

        /// <summary>Rest fill of a light-on-media button: the on-media ink itself.</summary>
        public static ColorF LightButton => Tok.OnMediaPrimary;
        /// <summary>Hover fill (≈ #EBEBEB).</summary>
        public static ColorF LightButtonHover => ColorRamp.Darken(Tok.OnMediaPrimary, LightHoverDrop);
        /// <summary>Pressed fill (≈ #D7D7D7).</summary>
        public static ColorF LightButtonPressed => ColorRamp.Darken(Tok.OnMediaPrimary, LightPressedDrop);
        /// <summary>The glyph ON a light on-media button: the engine's opaque media stage (#0A0A0A) — the old literal
        /// was (12,12,14), two steps away and one more hand-mixed near-black.</summary>
        public static ColorF LightButtonInk => Tok.MediaStage;
    }

    // ══ 5. THE IMMERSIVE STAGE'S POLARITY ════════════════════════════════════════════════════════════════════════════

    /// <summary>THE IMMERSIVE STAGE'S INK — the one place in the app that knows which POLARITY the stage is painted in,
    /// as a PURE value over <see cref="ThemeKind"/>.
    ///
    /// <para>The stage used to be single-theme: always art-dark, in both themes, on the argument that "the room is lit
    /// by the playing track". That is a defensible design and it is not the one this product wants — in light theme it
    /// read as a black slab bolted under light chrome. The stage now flips with the theme, and the whole of that
    /// decision lives HERE so the four renderers stay provably theme-blind.</para>
    ///
    /// <para><b>The dark arm delegates to <see cref="OnMedia"/> VERBATIM.</b> That is deliberate and it is pinned by a
    /// test: "dark theme is byte-identical to what shipped" is then an executable claim rather than a promise. It also
    /// keeps <see cref="OnMedia"/> exactly what it is — a THEME-INVARIANT ladder for everything that paints on top of
    /// ACTUAL artwork. That ladder must NOT become theme-aware; on a cover thumbnail white-on-scrim is right in both
    /// themes. The stage is the one surface whose "media" is a full-bleed blurred backdrop it also owns the scrim of,
    /// which is exactly why it — and only it — gets a polarity.</para>
    ///
    /// <para>The scrim ALPHAS need no light arm: the sRGB transfer curve does the work — mixing toward black at a
    /// partial alpha destroys far more perceptual luminance than mixing toward white, so the light arm's alpha'd ink
    /// clears a HIGHER contrast ratio than the dark arm already ships.</para></summary>
    public readonly record struct StageArm(bool Dark)
    {
        /// <summary>The pure entry point: a theme in, an arm out, with no global read — so value tests drive both arms
        /// without mutating global theme state.</summary>
        public static StageArm For(ThemeKind theme) => new(theme == ThemeKind.Dark);

        // ── the ground ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The colour every scrim alpha is mixed FROM, and the opaque floor under the backdrop. Light takes the
        /// app's own canon achromatic light ground rather than minting a near-duplicate; achromatic on purpose, so the
        /// cover's own colour comes through the veil instead of fighting a tint underneath it.</summary>
        public ColorF Veil => Dark ? Tok.MediaStage : Palette.PageToneNeutralLight;

        /// <summary>The opaque plate beneath the (possibly missing, still-decoding) cover. It IS the veil — which is
        /// what every comment on the surface already claimed it was, while the code actually painted the solid base and
        /// so flashed near-white under the dark scrim in light theme.</summary>
        public ColorF Floor => Veil;

        // ── ink ──────────────────────────────────────────────────────────────────────────────────────────────────────
        // Light ink is Tok.MediaStage — an ON-MEDIA token, deliberately NOT Tok.TextPrimary. Two reasons: the stage's
        // ink must stay opaque (the theme text rung is black @ 0.894, which is a second, quieter ladder), and the
        // surface's "no theme-flipping text rung" guard stays true by construction.

        /// <summary>The loudest thing on the surface: the title, a sung glyph, the active line.</summary>
        public ColorF Ink => Dark ? OnMedia.Ink : Tok.MediaStage;
        /// <inheritdoc cref="Ink"/>
        public ColorF InkSecondary => Dark ? OnMedia.InkSecondary : Tok.MediaStage with { A = Tok.OnMediaSecondary.A };
        /// <inheritdoc cref="Ink"/>
        public ColorF InkTertiary => Dark ? OnMedia.InkTertiary : Tok.MediaStage with { A = Tok.OnMediaTertiary.A };

        // ── glass: the stage's interaction ramp for a control carrying NO resting plate ───────────────────────────────

        /// <summary>Rest: nothing.</summary>
        public ColorF GlassRest => ColorF.Transparent;
        /// <inheritdoc cref="GlassRest"/>
        public ColorF GlassHover => Ink with { A = OnMedia.GlassHover.A };
        /// <inheritdoc cref="GlassRest"/>
        public ColorF GlassPressed => Ink with { A = OnMedia.GlassPressed.A };

        // ── the ink PLATE: a resting ground for the one control that must be found (the way out) ──────────────────────

        /// <inheritdoc cref="OnMedia.GlassPlate"/>
        public ColorF GlassPlate => Ink with { A = OnMedia.GlassPlate.A };
        /// <inheritdoc cref="OnMedia.GlassPlate"/>
        public ColorF GlassPlateHover => Ink with { A = OnMedia.GlassPlateHover.A };
        /// <inheritdoc cref="OnMedia.GlassPlate"/>
        public ColorF GlassPlatePressed => Ink with { A = OnMedia.GlassPlatePressed.A };

        // ── the scrim plate + its hairline ───────────────────────────────────────────────────────────────────────────

        /// <summary>A small plate floated over ARTWORK (the secondary-line toggle). Same alphas, mirrored ground.</summary>
        public ColorF ScrimRest => Dark ? OnMedia.ScrimRest : Veil with { A = OnMedia.ScrimRest.A };
        /// <inheritdoc cref="ScrimRest"/>
        public ColorF ScrimHover => Dark ? OnMedia.ScrimHover : Veil with { A = OnMedia.ScrimHover.A };
        /// <inheritdoc cref="ScrimRest"/>
        public ColorF ScrimPressed => Dark ? OnMedia.ScrimPressed : Veil with { A = OnMedia.ScrimPressed.A };

        /// <summary>The hairline that rings a plate. It inverts WITH the ink — a white hairline on a light plate is not
        /// a quiet ring, it is an absent one.</summary>
        public ColorF Stroke => Dark ? OnMedia.Stroke : Ink with { A = OnMedia.Stroke.A };

        // ── the ONE filled control (play/pause) ──────────────────────────────────────────────────────────────────────
        // Named ButtonFill rather than the dark arm's "LightButton": on a light stage the loudest affordance is a DARK
        // disc, so the old name would be a lie in half the product. Same 0.08 / 0.157 ramp, mirrored direction.

        /// <summary>The stage's play/pause — the only plate on the surface that is not a hover state.</summary>
        public ColorF ButtonFill => Dark ? OnMedia.LightButton : Ink;
        /// <inheritdoc cref="ButtonFill"/>
        public ColorF ButtonFillHover => Dark ? OnMedia.LightButtonHover : ColorRamp.Lighten(Ink, OnMedia.LightHoverDrop);
        /// <inheritdoc cref="ButtonFill"/>
        public ColorF ButtonFillPressed => Dark ? OnMedia.LightButtonPressed : ColorRamp.Lighten(Ink, OnMedia.LightPressedDrop);
        /// <summary>The glyph ON that disc — the ground it stands on, so the two can never collide.</summary>
        public ColorF ButtonInk => Dark ? OnMedia.LightButtonInk : Veil;

        /// <summary>The stage's plateau scrim alpha — the ONE number both this file and owner K's stage layout read.
        /// Declared here because <see cref="AccentFrom"/> solves against it; `Shell/Stage.cs` must REFERENCE this
        /// constant rather than restate 0.46 (ch 00 §4.4).</summary>
        public const float ScrimBaseA = 0.46f;

        // ── the art-derived accent ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The stage spends its one art-derived colour on exactly four jobs: a latched satellite, the saved
        /// heart, the pivot underline + section rule, and the ∞ when autoplay is on.
        ///
        /// <para>Dark takes the CHROME accent — the lifted, saturation-FLOORED role, correct for a colour that must hold
        /// its own as a lit mark on a dark ground. Light cannot: a lifted accent on a pale veil is a highlighter. So the
        /// light arm re-solves the SAME hue as INK against the brightest ground this stage can produce, which is the
        /// app's existing role-3 answer rather than a fourth accent solve invented here. The honest cost is chroma —
        /// <see cref="Palette.TextInk(ColorF,ThemeKind,ColorF)"/> caps saturation below the chrome accent's floor, so
        /// the light stage's accent is a quieter version of the same hue, and some mid-blues fall through to a
        /// near-neutral. Both are properties of accent-as-ink.</para></summary>
        public ColorF AccentFrom(ColorF chrome) =>
            Dark ? chrome : Palette.TextInk(chrome, ThemeKind.Light, AccentGround);

        /// <summary>The BRIGHTEST ground the light stage can put an accent on: the plateau veil over a white cover.
        /// Solving against the worst case is what stops the accent going illegible on a pale sleeve.</summary>
        ColorF AccentGround => ColorContrast.Over(Veil with { A = ScrimBaseA }, Tok.OnMediaPrimary);

        // ── two non-colour helpers, so the renderers make ZERO polarity reads of their own ────────────────────────────

        /// <summary>The lyrics loading shimmer. It used to paint a THEME fill straight onto the stage, which is a light
        /// bar on a dark surface in dark theme.</summary>
        public ColorF SkeletonBar => Ink with { A = SkeletonA };

        const float SkeletonA = 0.12f;
    }

    /// <summary>The LIVE facade over <see cref="StageArm"/>. <see cref="Arm"/> is the pure entry point; everything else
    /// re-reads the active theme AT THE POINT OF CONSUMPTION, which is the whole contract — a <c>ColorF</c> frozen into
    /// a component's constructor does not follow a live theme flip, because component props freeze at mount.</summary>
    public static class StageInk
    {
        /// <inheritdoc cref="StageArm"/>
        public static StageArm Arm(ThemeKind theme) => StageArm.For(theme);

        /// <summary>The arm for the ACTIVE theme. This — and only this — is the stage's theme branch.</summary>
        static StageArm Live => Arm(Tok.Theme);

        /// <summary>Which arm is live, for the handful of decisions that are a POLARITY rather than a colour (the lyrics
        /// bloom's direction and weight). Everything else takes a rung instead of asking this.</summary>
        public static bool IsDark => Live.Dark;

        public static ColorF Veil => Live.Veil;
        public static ColorF Floor => Live.Floor;
        public static ColorF Ink => Live.Ink;
        public static ColorF InkSecondary => Live.InkSecondary;
        public static ColorF InkTertiary => Live.InkTertiary;
        public static ColorF GlassRest => Live.GlassRest;
        public static ColorF GlassHover => Live.GlassHover;
        public static ColorF GlassPressed => Live.GlassPressed;
        public static ColorF GlassPlate => Live.GlassPlate;
        public static ColorF GlassPlateHover => Live.GlassPlateHover;
        public static ColorF GlassPlatePressed => Live.GlassPlatePressed;
        public static ColorF ScrimRest => Live.ScrimRest;
        public static ColorF ScrimHover => Live.ScrimHover;
        public static ColorF ScrimPressed => Live.ScrimPressed;
        public static ColorF Stroke => Live.Stroke;
        public static ColorF ButtonFill => Live.ButtonFill;
        public static ColorF ButtonFillHover => Live.ButtonFillHover;
        public static ColorF ButtonFillPressed => Live.ButtonFillPressed;
        public static ColorF ButtonInk => Live.ButtonInk;
        public static ColorF SkeletonBar => Live.SkeletonBar;

        /// <summary>The stage's accent for a cover url. The cover LOOKUP lives here rather than on
        /// <see cref="StageArm"/> so the arm stays a pure value type over the token layer — which is what lets value
        /// tests drive both arms with no cover plane, no theme and no window.</summary>
        public static ColorF Accent(ReadOnlySpan<char> coverUrl)
            => ChromeSchemeFor(coverUrl) is { } scheme
                ? Live.AccentFrom(Palette.ChromeAccent(scheme))
                : Tok.AccentDefault;

        /// <summary>The backdrop's stand-in while the cover is missing or still decoding, in the STAGE's polarity rather
        /// than the page's. The cover TINT survives (it is what stops the slot reading as a hole); only the neutral it
        /// is blended toward follows the stage.</summary>
        public static ColorF ArtStandIn(ReadOnlySpan<char> url) => PlaceholderFor(url, light: !IsDark);
    }

    // ══ 6. THE COVER PALETTE → RENDERER COLOUR ═══════════════════════════════════════════════════════════════════════

    /// <summary>The boundary mapper: the framework-neutral cover-colour roles (<see cref="Scheme"/>, ARGB) → engine
    /// <see cref="ColorF"/>. This is the ONLY place a cover colour becomes a renderer colour. A page asks the plane for
    /// its cover's scheme and maps the roles it needs here; nothing carries a per-entity palette.
    /// <para>NOTE THE NAME: this is <c>Design.Palette</c>, the ARGB MATHS. The TABLE — the per-image graded schemes, the
    /// TTL, <c>Watch</c>, <c>Ensure</c> — is <c>Wavee.Palette</c> (A5), and nothing in this section touches it.</para></summary>
    public static class Palette
    {
        const float HairlineSaturationCeiling = 0.50f;
        const float HairlineContrast = 3.25f;
        const float HairlineHoverContrast = 3.55f;

        /// <summary>Below this HSV saturation a colour has no hue worth amplifying — grading a genuinely greyscale cover
        /// would just produce a random tint. Callers fall back to the semantic accent instead.</summary>
        public const float NeutralS = 0.08f;

        /// <summary>WCAG AA text contrast. <see cref="Hairline(ColorF)"/> is the quieter 3.25:1 rule; this is the same
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
        /// binary-solved to a 3.25:1 contrast ratio in the theme-appropriate direction. Deliberately separate from
        /// <see cref="Lift"/>/<see cref="Vivid"/>: chrome fills want brightness and chroma; a 2-DIP rule does not.</summary>
        public static ColorF Hairline(ColorF seed)
            => Hairline(seed, Tok.Theme,
                ColorContrast.Flatten(Tok.FillCardDefault, Colors.FloatingPane), HairlineContrast);

        /// <summary>The hovered card's slightly stronger identity cue; 3.55:1 remains below the 4.5:1 text threshold —
        /// on purpose.</summary>
        public static ColorF HairlineHover(ColorF seed)
            => Hairline(seed, Tok.Theme,
                ColorContrast.Flatten(Tok.FillCardSecondary, Colors.FloatingPane), HairlineHoverContrast);

        /// <summary>Pure overload used by the contrast tests for both themes without mutating global theme state.
        /// <para>THE 22 ITERATIONS ARE THE CONTRAST SOLVE (file-header rule 3). Contrast is monotonic along V for a
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

        /// <summary>Accent as TEXT on a card-like surface. Same hue as <see cref="Hairline(ColorF)"/>, solved to
        /// <see cref="TextContrast"/>.</summary>
        public static ColorF TextInk(ColorF seed)
            => TextInk(seed, Tok.Theme, ColorContrast.Flatten(Tok.FillCardDefault, Colors.FloatingPane));

        /// <summary>Pure overload for both themes. Some hues cannot hit AA at the hairline saturation cap (a mid-blue on
        /// a dark card tops out ≈4.4:1); those fall back to the contrast picker so the digits stay readable.</summary>
        public static ColorF TextInk(ColorF seed, ThemeKind theme, ColorF cardBackground)
        {
            var ink = Hairline(seed, theme, cardBackground, TextContrast);
            return ColorContrast.MeetsAaText(ink, cardBackground) ? ink : ColorContrast.PickContrast(cardBackground);
        }

        /// <summary>Chrome fill from a raw wire colour (a pathfinder extracted colour before the plane has graded the
        /// art). Same <see cref="Lift"/> the hero Play button uses, so a card and its detail page agree. 0 (no payload)
        /// is the semantic accent, not a fabricated hue.</summary>
        public static ColorF ChromeFromPayload(uint argb)
            => argb == 0 ? Tok.AccentDefault : Lift(ToColor(argb));

        /// <summary>THE page's chrome accent — the one derivation every accent-filled control on a media surface uses:
        /// the cover's most-saturated graded role (<see cref="Accent(in Scheme)"/>), brightness-lifted then
        /// saturation-floored. Greyscale/near-monochrome art has no hue to amplify, so it falls back to the system
        /// accent rather than shipping a grey Play button.
        /// <para>Deliberately NOT used for the hero WASHES: those keep the plain <see cref="Lift"/>ed accent, so wash
        /// strength (alpha) and chrome chroma (saturation) stay independent axes.</para></summary>
        public static ColorF ChromeAccent(in Scheme s)
        {
            var lifted = Lift(Accent(s));
            var (_, sat, _) = lifted.ToHsv();
            return sat <= NeutralS ? Tok.AccentDefault : Vivid(lifted);
        }

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
        {
            if (scheme is not { } s) return null;
            var dominant = Accent(s);                        // the cover's HUE role (see Accent for why not TextBrightAccent)
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

        /// <summary>The player bar's neutral dark base — the surface the album hue is only faintly lifted from. WinUI's
        /// subtlety came from acrylic over a real blurred desktop; we have neither, so the bar is a flat neutral fill
        /// with a capped hue instead of a saturated tint.</summary>
        public static ColorF BarSurface { get; } = ColorF.FromRgba(0x1A, 0x1B, 0x1E, 0xFF);
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
        => Wavee.Palette.TryScheme(url, Tok.Theme == ThemeKind.Light, out var s) ? s : null;

    /// <inheritdoc cref="SchemeFor(ReadOnlySpan{char})"/>
    public static Scheme? SchemeFor(StringId imageRef)
        => Wavee.Palette.TryScheme(imageRef, Tok.Theme == ThemeKind.Light, out var s) ? s : null;

    /// <summary>The artwork grading used by SOLID chrome. The contrast branch is intentionally OPPOSITE
    /// <see cref="SchemeFor(ReadOnlySpan{char})"/>'s — see the block comment above.</summary>
    public static Scheme? ChromeSchemeFor(ReadOnlySpan<char> url)
    {
        bool pageIsLight = Tok.Theme == ThemeKind.Light;
        if (Wavee.Palette.TryScheme(url, lightTheme: !pageIsLight, out var opposite)) return opposite;
        return Wavee.Palette.TryScheme(url, lightTheme: pageIsLight, out var same) ? same : null;
    }

    // ── the art placeholder ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // NON-NEGOTIABLE 9: every art slot is a SOLID, cover-tinted tile before its bitmap lands — never a hole, never a
    // grey wall. The tile is the neutral placeholder lerped toward the cover's own graded colour, resolved through an
    // image-keyed plane that ENQUEUES ON A MISS, so rendering the art IS the request and no surface has to remember to
    // prefetch a colour.

    /// <summary>Artwork is OPAQUE content. Theme card brushes are intentionally translucent, so placeholders use
    /// explicit opaque neutrals instead of letting the surface below wash through the image slot — a small thumb sits
    /// over an UNPAINTED chrome band, so a see-through placeholder lets the desktop read through and the cover becomes a
    /// washed smear.</summary>
    public static readonly ColorF ArtworkPlaceholderDark = ColorF.FromRgba(0x2A, 0x2A, 0x2A);
    /// <inheritdoc cref="ArtworkPlaceholderDark"/>
    public static readonly ColorF ArtworkPlaceholderLight = ColorF.FromRgba(0xF2, 0xF2, 0xF2);

    /// <summary>How far a cover's own colour pulls the placeholder away from the neutral tile. Full strength would make
    /// a long list read as a wall of saturated blocks; this keeps the slot legible as "art loading" while still being
    /// THAT cover's colour. It is the difference between a list of grey squares and one that paints its covers at
    /// once.</summary>
    public const float TintStrength = 0.55f;

    /// <summary>Below this edge the breathe is imperceptible, so a slot takes a cheap STATIC tile: no component, no hook
    /// cells, no image-epoch subscription — a 50k-row virtualized list pays nothing per item.</summary>
    public const float ShimmerMinEdge = 80f;

    /// <inheritdoc cref="ArtworkPlaceholderDark"/>
    public static ColorF ArtworkPlaceholder =>
        Tok.Theme == ThemeKind.Dark ? ArtworkPlaceholderDark : ArtworkPlaceholderLight;

    /// <summary>THE art placeholder resolver, for the ACTIVE theme's polarity. Light theme only accepts a LIGHT grading;
    /// a dark-only entry keeps the neutral tile rather than dropping a dark slab onto a pale page.</summary>
    public static ColorF PlaceholderFor(ReadOnlySpan<char> url) => PlaceholderFor(url, Tok.Theme == ThemeKind.Light);

    /// <summary>The same resolver with the polarity SUPPLIED rather than read. A surface whose ground does not follow
    /// the page — the immersive stage, which owns its own veil — needs the tile graded for ITS polarity, or a
    /// still-decoding cover flashes the wrong end of the ramp under its scrim. The tint still comes from the same
    /// image-keyed plane and a miss still enqueues; only the neutral it is blended toward changes.</summary>
    public static ColorF PlaceholderFor(ReadOnlySpan<char> url, bool light)
    {
        ColorF neutral = light ? ArtworkPlaceholderLight : ArtworkPlaceholderDark;
        if (url.IsEmpty) return neutral;
        return Wavee.Palette.TryTint(url, light, out uint argb)
            ? ColorF.Lerp(neutral, Palette.ToColor(argb), TintStrength)
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
    public static Prop<ColorF> WatchedPlaceholder(string? url, bool? light = null) => Prop.Of(() =>
    {
        if (url is { Length: > 0 } u) _ = Wavee.Palette.Watch(u).Value;
        return light is { } l ? PlaceholderFor(url, l) : PlaceholderFor(url);
    });

    // ══ 8. THE TYPE RAMP ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Semantic type aliases so call sites read INTENT, not raw sizes. Every alias maps to the engine's WinUI
    /// type ramp. NEVER author a raw <c>TextEl { Size = … }</c>.
    ///
    /// <para><b>THE THREE-PART CONTRACT.</b> Every alias resolves a SIZE, a LINE HEIGHT and a WEIGHT — never a size
    /// alone. That is the whole reason to go through an alias rather than <c>with { Size = 13f }</c>: a bare size
    /// override keeps whatever line height the previous rung published, and a page of hand-picked sizes therefore has
    /// no vertical rhythm at all. The engine ramp carries the pair (12/16 · 14/20 · 14/20-600 · 18/24 · 20/28-600 ·
    /// 28/36-600 · 40/52-600 · 68/92-600), so repointing a call site at an alias brings the line height with it.</para>
    ///
    /// <para><b>WEIGHT POLICY: 400 and 600 only, with SIX documented divergences.</b> Three DISPLAY-FACE identity
    /// aliases keep 700 (<see cref="ArtistDisplay"/> / <see cref="ArtistTitle"/> / <see cref="ArtistCompactTitle"/> —
    /// the masthead voice, not UI labels) and three keep SemiLight 350 (<see cref="PivotLabel"/> /
    /// <see cref="NpvLyric"/> / <see cref="StatHero"/> — the same WinUI SemiLight the engine's own pivot control uses
    /// for its header row). A SEVENTH is a regression.</para>
    ///
    /// <para><b>ONE default is not <c>TextPrimary</c>.</b> <c>Ui.Caption</c> ships bound to <c>Tok.TextSecondary</c>, so
    /// <see cref="Eyebrow"/> and every "Caption at 600" rung are SECONDARY unless the call site sets <c>Color</c>, and
    /// <see cref="TrackMeta"/>'s secondary read is a restatement rather than a change.</para></summary>
    public static class Type
    {
        const string DisplayFace = "Segoe UI Variable Display";

        /// <summary>Track / album / playlist titles in lists. → <c>Ui.BodyStrong</c> (14 / 20 / 600).</summary>
        public static TextEl TrackTitle(string s) => Ui.BodyStrong(s);

        /// <summary>A media CARD's headline (a playlist tile, a mix card, a feed card, a shelf cell) — the same rung as
        /// <see cref="TrackTitle"/>, named for the surface it sits on so a card body does not have to claim it is
        /// rendering a track.</summary>
        public static TextEl CardTitle(string s) => Ui.BodyStrong(s);

        /// <summary>Artist · duration · metadata. → <c>Ui.Caption</c> secondary (12 / 16 / 400).</summary>
        public static TextEl TrackMeta(string s) => Ui.Caption(s).Secondary();

        /// <summary>THE tracking every eyebrow carries — 30/1000 em, owned by <see cref="Eyebrow"/> and authored nowhere
        /// else. Before convergence the app carried NINE tracking values on this one role (10, 20, 30, 32, 40, 50, 60,
        /// 70, 80, 120): a letterspacing ladder nobody designed, spread over 58 call sites, which is why two eyebrows
        /// stacked on the same page never looked like the same label. 30 is the value that survives SENTENCE case — the
        /// old 60-120 rungs were compensating for ALL-CAPS, and caps is exactly what this role gave up.</summary>
        public const float EyebrowTracking = 30f;

        /// <summary>An EYEBROW — the small label that names what a card/section IS ("Editorial", "Daily Mix", "Video",
        /// the hero's greeting). One rung, one weight, ONE tracking, everywhere.
        /// <para>CASE IS NOT PART OF THE VOICE. The role used to be ALL-CAPS + heavy letterspacing, which is neither
        /// Fluent (sentence case everywhere) nor the editorial register the rest of the app aims at — and a
        /// <c>.ToUpper()</c> on a LOCALIZED string is worse than a style mistake: it mangles Turkish dotted i, expands
        /// German ß, and shouts a user's own display name back at them. So the alias takes the string's OWN casing and
        /// no call site may caps-transform it.</para>
        /// <para>COLOUR belongs to the call site — an accent reason, a tertiary kind tag and an on-accent badge are the
        /// same type doing three different jobs. Metrics and tracking are the alias's; colour is not. The DEFAULT, when
        /// the call site sets nothing, is <c>Tok.TextSecondary</c>.</para>
        /// <para>Text that merely wants this RUNG without being an eyebrow — a rank numeral, a podium tile's artist
        /// name, a day heading — reads <c>Ui.Caption(x) with { Weight = 600 }</c> straight off the factory (same
        /// metrics, no tracking) rather than claiming to be a label it is not. One alias per ROLE, not per rung.</para></summary>
        public static TextEl Eyebrow(string s) => Ui.Caption(s) with { Weight = 600, CharSpacing = EyebrowTracking };

        /// <summary>"Because you played…" section / rail headers. → <c>Ui.Subtitle</c> (20 / 28 / 600), UI face.</summary>
        public static TextEl RailHeader(string s) => Ui.Subtitle(s);

        /// <summary>A Home MODULE header — the same 20/28 Semibold metrics as <see cref="RailHeader(string)"/> but set
        /// in the DISPLAY face with a hair of negative tracking. That is the whole difference between a shelf label and
        /// a module title: at 20 px the display face's tighter fit and optical sizing make a stack of thirteen headings
        /// read as typography rather than as thirteen repeated UI labels.</summary>
        public static TextEl ModuleHeader(string s) => Ui.Subtitle(s) with
        {
            FontFamily = DisplayFace,
            CharSpacing = -6f,
        };

        /// <summary>A module header plus its subdued fact ("Radio · 20 stations"), shaped as ONE paragraph so the 12 px
        /// run sits on the 20 px run's real BASELINE. Two separate text nodes cannot do this — the engine's
        /// <c>FlexAlign</c> has no Baseline member, so side-by-side nodes can only be bottom-aligned, which puts the
        /// small run a couple of pixels low and reads as a mistake at this size.
        /// <para>The gap IS two literal spaces: a run break cannot carry margin.</para></summary>
        public static SpanTextEl ModuleHeader(string title, string meta) => Paired(title, meta, DisplayFace, -6f);

        /// <summary>A rail heading plus baseline-aligned compact metadata, in the UI face. Same construction as
        /// <see cref="ModuleHeader(string,string)"/>.</summary>
        public static SpanTextEl RailHeader(string title, string meta) => Paired(title, meta, null, 0f);

        // The ONE baseline-paired-span construction. Both callers ship NoWrap + CharacterEllipsis + MaxLines 1 +
        // MinWidth 0 + Shrink 1: a baseline-paired span must never wrap, because the small run would land alone on line
        // 2 with no heading to sit on.
        static SpanTextEl Paired(string title, string meta, string? face, float tracking)
        {
            var heading = Ui.Subtitle("");
            var caption = Ui.Caption("");
            return new SpanTextEl(
            [
                new TextSpan(title),
                new TextSpan("  " + meta, Weight: caption.ResolvedWeight, Color: Tok.TextTertiary, Size: caption.Size),
            ])
            {
                FontFamily = face,
                CharSpacing = tracking,
                Size = heading.Size,
                Weight = heading.ResolvedWeight,
                LineHeight = heading.LineHeight,
                LineStacking = heading.LineStacking,
                LineBounds = heading.LineBounds,
                Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis,
                MaxLines = 1,
                MinWidth = 0f,
                Shrink = 1f,
            };
        }

        /// <summary>Page hero (playlist / album name), UI face. → <c>Ui.Title</c> (28 / 36 / 600).</summary>
        public static TextEl PageHero(string s) => Ui.Title(s);

        /// <summary>The two-column detail rail's identity title — and the vertical hero's. <see cref="PageHero"/>'s
        /// engine-ramp metrics in the DISPLAY face with −20/1000 em tracking. One alias so playlist, liked and album
        /// rails share a voice; the frame still swaps Size/LineHeight to 40/52 when the window is tall
        /// (<see cref="Size.DesignH"/>).</summary>
        public static TextEl DetailHero(string s) => Ui.Title(s) with
        {
            FontFamily = DisplayFace,
            CharSpacing = -20f,
        };

        /// <summary>A LIBRARY SURFACE's masthead — the name of a PLACE rather than of a record ("Recents"). One rung
        /// above <see cref="PageHero"/> on the SAME engine ramp (40 / 52), set in the display face at the LIGHT weight so
        /// the word reads as typography over a Mica wash instead of as one more bold UI label.
        /// <para>Deliberately NOT a fourth display-face divergence: it keeps the ramp's size/line-height pair and stays
        /// inside the 400/600 weight policy — the face and the hair of negative tracking are the same two liberties
        /// <see cref="ModuleHeader(string)"/> already takes, and nothing more.</para></summary>
        public static TextEl SurfaceDisplay(string s) => Ui.TitleLarge(s) with
        {
            FontFamily = DisplayFace,
            Weight = 400,
            CharSpacing = -12f,
        };

        /// <summary>Wide artist identity display. 84 / 96 / <b>700</b> — one of the three sanctioned display-face
        /// divergences. The <c>MinSize</c> floor is the point: the engine shrinks the glyph run toward it before it wraps
        /// or ellipsises, so a long artist name steps DOWN inside one rung rather than breaking to a second line.</summary>
        public static TextEl ArtistDisplay(string s) => Ui.Display(s) with
        {
            FontFamily = DisplayFace, Size = 84f, LineHeight = 96f, Weight = 700, CharSpacing = -28f, MinSize = 68f,
        };

        /// <summary>Medium artist identity title. 48 / 60 / <b>700</b> — sanctioned display-face divergence.</summary>
        public static TextEl ArtistTitle(string s) => Ui.TitleLarge(s) with
        {
            FontFamily = DisplayFace, Size = 48f, LineHeight = 60f, Weight = 700, CharSpacing = -20f, MinSize = 40f,
        };

        /// <summary>Compact artist identity title. 32 / 40 / <b>700</b> — sanctioned display-face divergence.</summary>
        public static TextEl ArtistCompactTitle(string s) => Ui.Title(s) with
        {
            FontFamily = DisplayFace, Size = 32f, LineHeight = 40f, Weight = 700, CharSpacing = -12f, MinSize = 28f,
        };

        /// <summary>Now-playing track title. → <c>Ui.Subtitle</c> (20 / 28 / 600).</summary>
        public static TextEl NowPlayingTitle(string s) => Ui.Subtitle(s);

        /// <summary>The artist SPEAKING — the pick card's quote, set as editorial typography rather than a UI label.
        /// → <c>Ui.Title</c>'s ramp pair (28 / 36) in the display face at REGULAR 400: on the engine ramp and inside the
        /// weight policy, taking only the same two liberties <see cref="SurfaceDisplay"/> takes. Regular at 28 px is
        /// what makes a first-person sentence read as a voice instead of a heading; the string keeps the artist's OWN
        /// casing.</summary>
        public static TextEl PickQuote(string s) => Ui.Title(s) with
        {
            FontFamily = DisplayFace,
            Weight = 400,
            CharSpacing = -12f,
        };

        /// <summary>The Fold tile's crop — the SAME cut as <see cref="PickQuote"/>, named for the surface it sits on
        /// rather than for the voice it carries: a Fold tile's title is a card headline, not an artist speaking. The
        /// prototype's own cut for this role (300 weight at 32 px) is deliberately NOT copied — 300 is off the 400/600
        /// policy and 32 px has no rung on the engine ramp.</summary>
        public static TextEl FoldTitle(string s) => PickQuote(s);

        /// <summary>A pivot tab header (All / Music / Podcasts / Artists) — the display face at SemiLight, one rung
        /// under <see cref="RailHeader(string)"/>. 19 / 25 / <b>350</b>, and deliberately OFF the eight-rung engine
        /// ramp: the second and last sanctioned off-ramp (the other is <c>Controls.Picker.Label</c>'s <c>size + 4</c>
        /// line box, which at its default 12 lands exactly on the 12/16 rung). A THIRD off-ramp is a regression.</summary>
        public static TextEl PivotLabel(string s) => Ui.BodyLarge(s) with
        {
            FontFamily = DisplayFace, Size = 19f, LineHeight = 25f, Weight = 350, CharSpacing = -6f,
        };

        /// <summary>NPV lyrics-peek reel — Subtitle's 20/28 pair in the display face at SemiLight 350.</summary>
        public static TextEl NpvLyric(string s) => Ui.Subtitle(s) with
        {
            FontFamily = DisplayFace, Weight = 350, CharSpacing = -6f,
        };

        /// <summary>A GLANCEABLE STAT — one number the reader takes in without reading: a play count, a tempo, a key, a
        /// duration. → <c>Ui.Title</c>'s ramp pair (28 / 36) in the display face at SemiLight <b>350</b>, the third and
        /// last use of that sanctioned cut.
        ///
        /// <para>WHY THE TITLE RUNG AND NOT SUBTITLE. The stat has no heading above it: it IS the heading, sitting over
        /// its own sentence-case caption, and the whole point of dropping the tiles that used to box these values is
        /// that the numerals must now carry the section by themselves. At 20/28 a play count and the version rows below
        /// it are close enough to read as one undifferentiated column of text — which is the wall the tiles were there
        /// to break up.</para>
        ///
        /// <para><paramref name="unit"/> is the small run that shares the numeral's BASELINE — built as ONE paragraph
        /// for the same reason <see cref="ModuleHeader(string,string)"/> is. Null (the common case) yields the numeral
        /// alone.</para></summary>
        public static SpanTextEl StatHero(string value, string? unit)
        {
            var heading = Ui.Title("");
            var caption = Ui.Caption("");
            TextSpan[] spans = unit is { Length: > 0 }
                ? [new TextSpan(value),
                   new TextSpan("  " + unit, Weight: caption.ResolvedWeight, Color: Tok.TextSecondary, Size: caption.Size)]
                : [new TextSpan(value)];
            return new SpanTextEl(spans)
            {
                FontFamily = DisplayFace,
                CharSpacing = -6f,
                Size = heading.Size,
                Weight = 350,
                LineHeight = heading.LineHeight,
                LineStacking = heading.LineStacking,
                LineBounds = heading.LineBounds,
                Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis,
                MaxLines = 1,
                MinWidth = 0f,
                Shrink = 1f,
            };
        }
    }

    // ══ 9. MOTION ════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Wavee's ONE motion vocabulary. Hover/press motion everywhere is Wavee's identity — a media app should feel alive
    // under the pointer where WinUI is deliberately still — but it is a SYSTEM, not 20 hand-picked numbers. This section
    // owns both halves: the three interaction SCALE tiers (every HoverScale/PressScale in the app resolves to one) and
    // the Fluent duration ladder (every hover/press/brush duration snaps to one of its rungs).
    //
    // ── WHY THE TIERS READ ReducedMotion AND THE ENGINE DOES NOT ─────────────────────────────────────────────────────
    // The hover/press SCALE path is the ONLY animated channel in the engine that carries no reduced-motion policy.
    // Structural motion goes through the seeding paths that consult the reduced-snap policy and place the channel at its
    // end value; the declarative While*/Transition surface carries a motion token whose Reduced policy is honoured the
    // same way. But the interaction fade is seeded with no policy parameter and the recorder composites the scale
    // unconditionally — so a reduced-motion user WOULD still get every scale cue, fully eased. The values are authored
    // app-side, so the correct minimal fix is app-side and lives HERE: the tier accessors return 1f under reduced
    // motion, which makes the recorder's |s − 1| > 0.0008 test fail and skips the transform entirely. This is
    // reduced-motion-AS-A-VALUE (the canon rule) — a value read during Render, never a hook-order-breaking branch. If
    // the engine ever grows a policy on the interaction channels, delete the flag reads here and keep the tiers.

    /// <summary>The three interaction scale tiers and the Fluent duration ladder.</summary>
    public static class Motion
    {
        /// <summary>Chips, small toggles, settings rows, inline pickers — a surface the pointer crosses often.
        /// Barely-there, so a scrolling list does not shimmer.
        /// <para>NOT a near-full-width track/list row: even this "subtle" 2% swing moves each edge several DIP in
        /// opposite directions on a ~1000 px row, which reads as the whole row shrinking and springing back and visibly
        /// blurs the title mid-scale. A wide row's press acknowledgement is its <c>PressedFill</c> alone — no
        /// <c>PressScale</c>.</para></summary>
        public static readonly ScaleTier ScaleSubtle = new(1.02f, 0.98f);

        /// <summary>Buttons, CTAs, pills, secondary circles — a deliberate, discrete target. The media pill's tier.</summary>
        public static readonly ScaleTier ScaleStandard = new(1.04f, 0.96f);

        /// <summary>Media FABs, transport + primary play, on-artwork circles, the "…" corner — a round affordance that
        /// floats over media and is the page's loudest interactive object. Absorbs the old 1.06/1.07/1.10/1.16 and
        /// 0.86/0.90/0.92/0.94 family.</summary>
        public static readonly ScaleTier ScaleEmphatic = new(1.07f, 0.92f);

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

        /// <summary>List/shelf entrance stagger per item. Reached through <see cref="Entrance"/>, never multiplied by
        /// hand at a call site (that is what produced the uncapped, reduced-motion-blind entrances this rung
        /// replaced).</summary>
        public const float StaggerMs = 40f;

        /// <summary>A drill-in surface's MASTHEAD stagger: the offset between the display line and the metadata line
        /// under it, assigned to the two-child container's <c>Element.Stagger</c>.
        /// <para>A separate rung from <see cref="StaggerMs"/> on purpose, and the distinction is MECHANICAL rather than
        /// aesthetic: that one is the per-ITEM offset of an unbounded list and is therefore capped by
        /// <see cref="Entrance.StaggerCap"/>; this one offsets exactly TWO authored lines, so the engine's uncapped
        /// <c>index × ms</c> spelling is safe and the cascade is bounded by construction.</para>
        /// <para>Reduced motion is a VALUE at the call site (<c>Design.Reduced ? 0f : MastheadStaggerMs</c>), never a
        /// branch — <c>Element.Stagger</c> is a plain float and takes no motion token.</para></summary>
        public const float MastheadStaggerMs = 45f;
    }

    /// <summary>The process-wide reduced-motion flag, read as a VALUE. Named here so no app file has to import the
    /// engine's <c>Motion</c> (which <see cref="Design.Motion"/> shadows inside this class) just to ask.</summary>
    public static bool Reduced => FgMotion.ReducedMotion;

    /// <summary>THE list/shelf entrance recipe — the one place a Wavee surface says "my items arrive in sequence".
    ///
    /// <para>A short rise + fade + a hair of blur, offset by <see cref="Motion.StaggerMs"/> per item and <b>capped</b>
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
            => Reduced ? 0f : Math.Clamp(index, 0, StaggerCap) * Motion.StaggerMs;

        /// <summary>Assign to the item's own wrapper box (never to a node whose Opacity is already bound — a bound
        /// channel and an Enter opacity track fight over the same row).</summary>
        public static LayoutTransition Row(int index) => Rise with { DelayMs = DelayMs(index) };
    }

    /// <summary>One interaction scale tier: the authored hover/press targets plus the reduced-motion-safe accessors
    /// every call site must use. Construct only through <see cref="Design.Motion"/>'s three tiers — there is no
    /// fourth.</summary>
    public readonly struct ScaleTier
    {
        /// <summary>The authored target, BEFORE the reduced-motion read. For tests/diagnostics; call sites use
        /// <see cref="Hover"/>.</summary>
        public readonly float HoverTarget;
        /// <inheritdoc cref="HoverTarget"/>
        public readonly float PressTarget;

        internal ScaleTier(float hoverTarget, float pressTarget)
        {
            HoverTarget = hoverTarget;
            PressTarget = pressTarget;
        }

        /// <summary>Assign to <c>BoxEl.HoverScale</c>. Collapses to 1f under reduced motion.</summary>
        public float Hover => Reduced ? 1f : HoverTarget;
        /// <summary>Assign to <c>BoxEl.PressScale</c>. Collapses to 1f under reduced motion.</summary>
        public float Press => Reduced ? 1f : PressTarget;
        /// <summary>Hover scale for an affordance that can be DEAD (a disabled transport button, an unavailable filter):
        /// a surface that cannot be clicked must not answer the pointer.</summary>
        public float HoverIf(bool enabled) => enabled ? Hover : 1f;
        /// <inheritdoc cref="HoverIf"/>
        public float PressIf(bool enabled) => enabled ? Press : 1f;
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

    /// <summary>The three frame-cadence knobs the ambient power policy applies, and the RULE that says which animation
    /// rows they reach. Named here so the surfaces that must NOT be capped can cite the reason rather than rediscover it.
    ///
    /// <para>WHAT READS THE KNOB: every cadence-less LOOPING animation row — the buffering spinner, the skeleton
    /// shimmer, the now-playing equalizer, the seek playhead, the karaoke lyrics wipe, the deck drift channels, the
    /// concert ground and its two arcs, the browse tile wobble, the liked cover wall drift and its three marquee
    /// bands.</para>
    ///
    /// <para>WHAT DOES NOT: a row with an explicit display cadence (springs, live drags), a row with an explicit
    /// per-row Hz, and every interval-driven source — the deck tick, the lyrics clock, the analyser. Those are
    /// SELF-PACED TIMERS, not animation rows, and the engine already pauses them under a parked/minimized window
    /// through the activation fold.</para>
    ///
    /// <para>The POLICY itself (the 2 s poll, the two-read hold window, "energy saver counts as NOT plugged", "a desktop
    /// with no battery — or a FAILED read — resolves PLUGGED, or every desktop would run permanently half-capped") is
    /// `Platform.cs`'s ambient power section, owner S. These are the values it writes.</para></summary>
    public static class Cadence
    {
        /// <summary>The default loop rate while the machine is on mains power.</summary>
        public const int PluggedLoopHz = 30;
        /// <summary>…and on battery (or with energy saver on). A ~20% cut nobody can see on a shimmer and a real saving
        /// over a five-minute listen.</summary>
        public const int BatteryLoopHz = 24;
        /// <summary>The floor the ENGINE puts between ANIMATION-ONLY frames whenever the window is not foreground — a
        /// ~30 fps ceiling. This is the engine's throttle: the policy does not track focus.</summary>
        public const int InactiveFrameIntervalMs = 33;
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

    /// <summary>The page-swap policy of the content card: slot identity and the recipe a direction maps to. Pure — no
    /// pages, no controls, no GPU — so it is pinned by tests.
    /// <para>The route → VIDEO-SAFE classification stays in the shell (it knows module routes); this section only
    /// supplies the two recipe families.</para></summary>
    public static class Nav
    {
        /// <summary>Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses
        /// the same page-slide language (the page moves; the content does not then cascade). Each committed search query
        /// is its own slot, so Back walks query history.
        /// <para>The separator is U+001F (unit separator) precisely because it cannot occur in a route name or an
        /// argument, so two slots can never collide by concatenation.</para></summary>
        public static string SlotKey(in PageSlot s)
            => s.TabId + "\u001F" + s.Route + "\u001F" + (s.Arg ?? "");

        /// <summary>The recipe for a page swap, WITH its Exit half. Both halves are load-bearing: the reconciler only
        /// overlaps the outgoing page (ZStacked on the boundary, hit-test invisible, parked once its tracks settle) when
        /// <c>Exit.Active</c> is true — with a stripped Exit the outgoing page is detached in the same frame and the
        /// card flashes EMPTY before the incoming page arrives.
        /// <para>FADE-THROUGH, not a symmetric slide: exit fades IN PLACE over 120 ms on a fast-out ease so it is ~94%
        /// gone when enter starts at 90 ms — two full-bleed pages never mix at readable opacity (an ACCELERATE curve
        /// would hold the old page near 1 until the end, which is exactly the superimposed-text frame). Enter slides
        /// <see cref="Expressive.DistBase"/>: a long slide covers half its travel in the first presented frame after a
        /// heavy mount.</para></summary>
        public static LayoutTransition RecipeFor(NavTransitionKind motion) => motion switch
        {
            NavTransitionKind.Back => PageFadeThroughBack,
            NavTransitionKind.Neutral => MotionRecipes.PageFade,
            _ => PageFadeThroughForward,
        };

        /// <summary>The exit window. The masthead band's own fade shares it, so the two halves of a drill-in read as one
        /// gesture.</summary>
        public const float FadeThroughExitMs = 120f;
        const float FadeThroughEnterDelayMs = 90f;

        /// <inheritdoc cref="RecipeFor"/>
        public static LayoutTransition PageFadeThroughForward => new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Dx: Expressive.DistBase, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(FadeThroughExitMs, Easing.EaseOut),
            DelayMs: FadeThroughEnterDelayMs,
            ExitDelayMs: 0f);

        /// <inheritdoc cref="RecipeFor"/>
        public static LayoutTransition PageFadeThroughBack => new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Dx: -Expressive.DistBase, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(FadeThroughExitMs, Easing.EaseOut),
            DelayMs: FadeThroughEnterDelayMs,
            ExitDelayMs: 0f);

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
        /// with the same dynamics on both halves and <c>Exit.Active</c> still TRUE.</para>
        ///
        /// <para><b>The honest degradation:</b> a module-page swap SLIDES instead of cross-fading, so two full-bleed
        /// pages share the card at full opacity for the length of the travel — which is the double exposure
        /// fade-through was introduced to shrink, and still the better trade against a video that disappears
        /// mid-navigation.</para></summary>
        /// <returns>The slide for Forward/Back; <b>null</b> for <see cref="NavTransitionKind.Neutral"/> — an honest CUT.
        /// Neutral's only recipe is opacity and nothing else, so there is no video-safe form of it to hand back, and a
        /// hard cut is what "no motion this page can survive" looks like.</returns>
        public static LayoutTransition? RecipeForVideoSafe(NavTransitionKind motion) => motion switch
        {
            NavTransitionKind.Back => PageSlideSafeBack,
            NavTransitionKind.Neutral => null,
            _ => PageSlideSafeForward,
        };

        /// <inheritdoc cref="RecipeForVideoSafe"/>
        public static LayoutTransition PageSlideSafeForward => new(
            TransitionChannels.Position,
            TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Dx: Expressive.DistBase, Active: true),
            Exit: new EnterExit(Dx: -Expressive.DistBase, Active: true),
            ExitDynamics: TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            DelayMs: 0f,
            ExitDelayMs: 0f);

        /// <inheritdoc cref="RecipeForVideoSafe"/>
        public static LayoutTransition PageSlideSafeBack => new(
            TransitionChannels.Position,
            TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Dx: -Expressive.DistBase, Active: true),
            Exit: new EnterExit(Dx: Expressive.DistBase, Active: true),
            ExitDynamics: TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            DelayMs: 0f,
            ExitDelayMs: 0f);
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
        /// <see cref="Done"/> once a chunk reaches or exceeds the realized band.</summary>
        public static int Next(int reveal, int visible)
        {
            int target = Math.Min(visible, Cap);
            int next = reveal + Chunk;
            return next >= target ? Done : next;
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

        /// <summary>Alpha at the wash origin (offset 0). DARK carries roughly TWICE the light strength: the same colour
        /// reads far weaker over the dark ground, and the light ground has less headroom before a wash turns into a
        /// smudge.</summary>
        public const float HeroAlphaLight = 0.055f, ShelfAlphaLight = 0.05f;
        /// <inheritdoc cref="HeroAlphaLight"/>
        public const float HeroAlphaDark = 0.10f, ShelfAlphaDark = 0.085f;

        public static float HeroAlpha(bool light) => light ? HeroAlphaLight : HeroAlphaDark;
        public static float ShelfAlpha(bool light) => light ? ShelfAlphaLight : ShelfAlphaDark;

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

        /// <summary>The neutral ground the material layer paints when no page has a colour. <b>Never
        /// <c>ColorF.Transparent</c></b> — transparent is premultiplied BLACK, and cross-fading into or out of it drags
        /// the interpolated colour toward black for the whole ramp. That is what read as "the shell tint goes neutral
        /// AND DARKER" at almost every navigation. A correctness fix disguised as a constant.</summary>
        public static ColorF NeutralGround => Colors.ShellGround with { A = 0.03f };

        /// <summary>The wash host's bottom inset is a MARGIN, not a re-anchored ellipse. The placements' centre/radius
        /// are node-relative CONSTANTS precisely because the box is a fraction of the WINDOW; subtracting a fixed
        /// <see cref="Dock.Reserve"/> from the box HEIGHT would make those two ratios viewport-dependent and they would
        /// go stale on the next resize.</summary>
        public const float HostBottomInset = Dock.Reserve;

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
/// <see cref="Design.Palette.PageTone"/>'s first clause is <i>complementary, not sampled</i>, and a blurred copy of the
/// cover at page scale is exactly the forbidden move, sitting directly on the plane that forbids it. Do not reintroduce
/// it; the tone tests assert its absence.</para></summary>
public sealed class CoverPageTonePlane : Component
{
    /// <summary>Re-pushed props. <paramref name="BackdropBand"/> is the hero band's own height in DIP, supplied by the
    /// CALLER (owner M's detail vertical layout) rather than imported here — this file must not depend on the detail
    /// frame's arithmetic (A1).</summary>
    public sealed record Props(string? Url, string? FallbackUrl, bool Disabled, float BackdropBand, float PageHeight,
                               bool HeroOnly);

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
    public const float PlaneAlphaDark = 0.20f, PlaneAlphaLight = 0.30f;

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
                ? t with { A = Tok.Theme == ThemeKind.Light ? PlaneAlphaLight : PlaneAlphaDark }
                : ColorF.Transparent),
            BrushTransitionMs = Design.Motion.Standard,
            Children = kids,
        };
    }

    /// <summary>Cover grading → the page's ground. Cheap: two table probes and the clamp; no subscription (the caller
    /// owns that), so it is safe on the paint path.</summary>
    static ColorF? Resolve(Props p)
        => Design.Palette.PageTone(Design.SchemeFor(p.Url) ?? Design.SchemeFor(p.FallbackUrl), Tok.Theme);

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
        float alpha = Tok.Theme == ThemeKind.Light ? PlaneAlphaLight : PlaneAlphaDark;
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
public sealed class CoverArtistBlendWash : Component
{
    /// <inheritdoc cref="CoverArtistBlendWash"/>
    public sealed record Props(string? Url, float Height, float Boundary, bool Disabled);

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Disabled) return new BoxEl();

        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        var pagePal = Design.SchemeFor(p.Url);
        bool light = Tok.Theme == ThemeKind.Light;
        // LIGHT falls back to the APP accent when the hero is ungraded; DARK falls back to the neutral scheme's grey.
        // Recorded rather than harmonised: the two are different answers on purpose, because a grey wash in light would
        // read as a dirty page and an invented accent wash in dark would read as a colour the record does not have.
        ColorF wash = light
            ? (pagePal is { } wp ? Design.Palette.Lift(Design.Palette.Accent(wp)) : Tok.AccentDefault)
            : Design.Palette.BackgroundDark(pagePal ?? Design.Palette.Neutral);
        return new BoxEl
        {
            Height = p.Height, HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, wash with { A = light ? 0.20f : 0.30f }),
                new GradientStop(p.Boundary, wash with { A = light ? 0.06f : 0.08f }),
                new GradientStop(1f, wash with { A = 0f })),
        };
    }
}

/// <summary>The artist hero's photography veil — cover-keyed so a late grading does not rebuild the banner.</summary>
public sealed class CoverKeyedVeil : Component
{
    /// <inheritdoc cref="CoverKeyedVeil"/>
    public sealed record Props(string? Url, bool Vertical, float Width, float Height);

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Url is { Length: > 0 } url) _ = Palette.Watch(url).Value;
        var pagePal = Design.SchemeFor(p.Url);
        var chromePal = Design.ChromeSchemeFor(p.Url);
        ColorF accent = chromePal is { } pal ? Design.Palette.ChromeAccent(pal) : Tok.AccentDefault;
        ColorF washAccent = pagePal is { } wp ? Design.Palette.Lift(Design.Palette.Accent(wp)) : accent;
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, HitTestVisible = false,
            Gradient = Controls.ArtistHeroVeil(washAccent, p.Vertical),
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
                               Signal<ShellMaterialState>? Slot);

    /// <summary>The two tint arms. LIGHT lifts the cover's TEXT role to a 5% whisper; DARK takes the tinted background
    /// role at 14%. Two different ROLES, not one role at two alphas — a lifted background role in light is a pastel
    /// smear across the chrome. It WARMS the ground; it never replaces it.</summary>
    public const float TintAlphaLight = 0.05f, TintAlphaDark = 0.14f;

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
        ColorF? known = !definite && artPalette is { } artScheme
            ? Tok.Theme == ThemeKind.Light
                ? Design.Palette.Lift(Design.Palette.ToColor(artScheme.TextBase)) with { A = TintAlphaLight }
                : Design.Palette.TintedDark(artScheme) with { A = TintAlphaDark }
            : null;

        // "Have I EVER published": the first publish is the mount's claim (neither activation callback fires at mount);
        // a reactivation claims through onActivated explicitly.
        var claimedOnce = UseRef(false);

        void Publish(bool isClaim) => ShellMaterial.Publish(p.Slot, p.Owner, isClaim, definite, known, wash: null);

        // `Tok.Theme` is IN THE KEY on purpose: a live theme flip re-derives the tint ARM (light Lift(TextBase)@0.05 vs
        // dark TintedDark@0.14) and re-publishes it. Without it the chrome keeps the OLD arm's tint until the next
        // navigation.
        UseEffect(() =>
        {
            Publish(isClaim: !claimedOnce.Value);
            claimedOnce.Value = true;
        }, DepKey.From(HashCode.Combine(p.Url, known.HasValue, known.GetValueOrDefault(), Tok.Theme,
                                        p.Ready, p.Disabled, p.Apply)));
        // Reactivation (KeepAlive Back/forward) is ALWAYS a claim, regardless of claimedOnce — the whole point is to
        // retake the slot from whatever deactivated in between, even if that never cleared it either.
        UseActivation(onActivated: () => Publish(isClaim: true));

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
