using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Surface helpers. The shell is live Mica with a FLUSH content region over it — the stock Win11 recipe: the
// LayerFillColorDefault smoke, one rounded corner facing the nav pane, a 1px left+top stroke, and NO shadow (it is not
// a floating card, and it never was an opaque canvas). Over that sits a SUBTLE, edge-transparent accent band tint —
// the WaveeMusic look, not an edge-to-edge colour flood. Kept here: the
// accent hero wash for page headers, and the album-art SHIMMER placeholder (a neutral, breathing skeleton tile —
// identical to the app's other loading skeletons) so an art slot with no bitmap yet reads as "loading", not a coloured
// hole. Used by cards, rows, the rail cover and the bar.
public static class Surfaces
{
    // Artwork is opaque content. Theme card brushes are intentionally translucent, so placeholders use explicit opaque
    // neutrals instead of allowing the surface below to wash through the image slot.
    internal static readonly ColorF ArtworkPlaceholderDark = ColorF.FromRgba(0x2A, 0x2A, 0x2A);
    internal static readonly ColorF ArtworkPlaceholderLight = ColorF.FromRgba(0xF2, 0xF2, 0xF2);

    internal static ColorF ArtworkPlaceholder =>
        Tok.Theme == ThemeKind.Dark ? ArtworkPlaceholderDark : ArtworkPlaceholderLight;

    // How far a cover's own colour pulls the placeholder away from the neutral tile. Full strength would make a long
    // list read as a wall of saturated blocks; this keeps the slot legible as "art loading" while still being that
    // cover's colour. Same blend technique as ConcertUi's hero band.
    const float TintStrength = 0.55f;

    /// <summary>The art placeholder for an entity whose dominant cover colour is known. Falls back to the neutral tile
    /// when the colour has not been graded yet, so a slot is never a hole.</summary>
    internal static ColorF TintedPlaceholder(uint? tint) =>
        tint is { } argb ? ColorF.Lerp(ArtworkPlaceholder, WaveePalette.ToColor(argb), TintStrength) : ArtworkPlaceholder;

    /// <summary>THE art placeholder resolver. Every art slot in the app goes through here, so no call site has to
    /// know a colour exists, thread a <c>tint:</c> parameter, or remember to prefetch one: the plane is image-keyed
    /// (a cover's colour is a property of the cover), and a miss enqueues that image for grading — rendering the art
    /// IS the request. Light theme only accepts a light grading; a dark-only entry (all kind 179 ever ships) keeps the
    /// neutral tile rather than dropping a dark slab onto a pale page.</summary>
    internal static ColorF PlaceholderFor(string? url) => PlaceholderFor(url, Tok.Theme == ThemeKind.Light);

    /// <summary>The same resolver with the polarity supplied rather than read. A surface whose ground does NOT follow
    /// the page — the immersive stage, which owns its own veil — needs the tile graded for ITS polarity, not the
    /// page's, or a still-decoding cover flashes the wrong end of the ramp under its scrim. The tint still comes from
    /// the same image-keyed plane and a miss still enqueues that image for grading, so nothing about the caching
    /// contract changes; only which neutral the tint is blended toward.</summary>
    internal static ColorF PlaceholderFor(string? url, bool light)
    {
        ColorF neutral = light ? ArtworkPlaceholderLight : ArtworkPlaceholderDark;
        if (string.IsNullOrEmpty(url)) return neutral;
        return SpotifyLive.CoverColorPlane.Current.TryGetTint(url, light, out uint argb)
            ? ColorF.Lerp(neutral, WaveePalette.ToColor(argb), TintStrength)
            : neutral;
    }

    // ── THE TWO GRADING HALVES, AND WHICH JOB TAKES WHICH ────────────────────────────────────────────────────────────
    //
    // Every cover is graded TWICE by the provider — a light half and a dark half — and the app reads BOTH, in opposite
    // directions, from two functions that sit next to each other and differ by one boolean. That is not a leftover: it
    // is the policy, and it is worth one paragraph because reading the wrong one is invisible in dark and glaring in
    // light.
    //
    //   • SchemeFor  — the PAGE half. Follows the active theme (light theme ⇒ the light grading). Everything that
    //     paints a SURFACE the page's own ink then sits on takes this: the page tone, the hero/blend washes, the shell
    //     material tint, section spines. These are backgrounds, so they must be graded for the polarity of the theme
    //     they are painted in, or the page's Tok ink tokens stop being correct on them.
    //
    //   • ChromeSchemeFor — the CHROME half, and it takes the OPPOSITE theme's grading ON PURPOSE. Everything that
    //     paints a SOLID PLATE carrying on-accent ink takes this: the Play capsule, the Verified/Following pills, the
    //     stage's filled transport. A CTA is not a background — it is a foreground object that has to hold its own
    //     against the surface around it, and the provider's light grading is the softer, lower-chroma treatment (median
    //     HSV S ≈ 0.45) while its dark grading carries the stronger chroma (median S ≈ 0.73). So a LIGHT page wants the
    //     DARK grading's chroma for its one solid CTA, and a dark page wants the light grading's softness so the plate
    //     does not glow. Both fall back to the other half when only one exists.
    //
    // The rule in one line: if the app's own ink lands ON it, grade it for the theme (SchemeFor); if it carries
    // on-accent ink and has to be seen (WaveeAccent.Action), grade it against the theme (ChromeSchemeFor).

    /// <summary>The full graded roles behind a cover — for PAGE chrome (hero washes, accent bars, the Play button, the
    /// shell's published material tint) rather than a placeholder tile. Null until the plane has a grading for this theme.
    /// Callers that want the colour to appear the moment it lands must NOT read <c>Watch</c>/<c>Epoch</c> at page scope
    /// (that rebuilds the whole page); subscribe from a leaf tone/tint node or a bound Fill instead — see
    /// <c>CoverPageTonePlane</c> and its siblings in <c>CoverPaletteLeaves</c>.</summary>
    internal static SpotifyLive.CoverColorPlane.Scheme? SchemeFor(string? url) =>
        SpotifyLive.CoverColorPlane.Current.TryGetScheme(url, Tok.Theme == ThemeKind.Light);

    /// <summary>The artwork grading used by solid chrome such as Play and Verified pills. The contrast branch is
    /// intentionally opposite the page branch: light-theme grading is the softer/lower-chroma treatment that sits
    /// comfortably on a dark hero, while dark-theme grading carries the stronger chroma a solid CTA needs against a
    /// pale wash. This is the provider's paired treatment, not an invented HSV transform. A dark-only visual-identity
    /// entry falls back to its available branch.</summary>
    internal static SpotifyLive.CoverColorPlane.Scheme? ChromeSchemeFor(string? url)
    {
        var plane = SpotifyLive.CoverColorPlane.Current;
        bool pageIsLight = Tok.Theme == ThemeKind.Light;
        return plane.TryGetScheme(url, lightTheme: !pageIsLight)
            ?? plane.TryGetScheme(url, lightTheme: pageIsLight);
    }

    // HeroWash — the top-anchored accent fade, and its three alpha constants — is DELETED along with
    // WaveePalette.HeroBase / WaveePalette.HeroWashColor. All three lost their last call site when the detail pages
    // moved to the ONE opaque art-derived page tone (WaveePalette.PageTone, mounted by CoverPaletteLeaves), and a
    // tuned-but-unreachable wash is worse than no wash: it is a second, contradictory answer to "how much colour does a
    // light page take" sitting a few lines from the real one. NowPlayingPanel has its own local HeroWashColor and is
    // unaffected.

    /// <summary>Semantic copy protection over full-bleed artist photography. Both axes use exactly four stops (the
    /// recorder limit) and release to alpha zero at the hero seam.
    ///
    /// <para>The HORIZONTAL arm is the original near-opaque left plate (theme-invariant 0.96 / 0.92 / 0.35): the copy
    /// column sits on a real surface and the photography lives in the right half. A softened 0.42 pass (9 Aug) made
    /// the plate a whisper in light themes, so the always-on bottom photo EdgeFade read as "the" fade — restored by
    /// explicit user ruling ("like before, it was really beautiful"). The VERTICAL arm keeps the softened
    /// immersive-hero peaks: it underlays copy stacked at a photo's bottom seam (Home's stacked photography heroes),
    /// where a 0.96 band flattened the image into a painted plate.</para></summary>
    public static GradientSpec ArtistHeroVeil(ColorF accent, ArtistHeroVeilAxis axis)
    {
        ColorF layer = Tok.FillLayerDefault;
        float pull = Tok.Theme == ThemeKind.Light ? 0.16f : 0.24f;
        ColorF veil = ColorF.Lerp(layer, accent, pull);
        if (axis == ArtistHeroVeilAxis.Vertical)
        {
            float top = Tok.Theme == ThemeKind.Light ? 0.42f : 0.78f;
            return GradientDown(
                new GradientStop(0f, veil with { A = 0f }),
                new GradientStop(0.45f, veil with { A = 0.35f }),
                new GradientStop(0.82f, veil with { A = top }),
                new GradientStop(1f, veil with { A = 0f }));
        }
        return GradientRight(
            new GradientStop(0f, veil with { A = 0.96f }),
            new GradientStop(0.30f, veil with { A = 0.92f }),
            new GradientStop(0.62f, veil with { A = 0.35f }),
            new GradientStop(1f, veil with { A = 0f }));
    }

    // DetailHeroWash — the immersive detail hero's strong four-stop alpha wash — is DELETED with the immersive hero
    // arm itself. The detail pages no longer stack a wash over a neutral ground: they paint ONE opaque art-derived
    // page tone (WaveePalette.PageTone, mounted by CoverPaletteLeaves.PageTonePlane) that the whole page sits on.

    /// <summary>A neutral album-art placeholder: the app's skeleton tile (<see cref="Tok.FillCardDefault"/>) that
    /// BREATHES while the art at <paramref name="url"/> is still loading and settles to a calm static tile once it is
    /// ready / failed / absent — so an art slot reads as "loading", never a coloured hole, and the pulse stops (a
    /// forever-loop would pin the frame loop awake). <paramref name="decodePx"/> must match the decode size of the real
    /// image stacked over it, so the load-state read shares the image's cache handle (no second decode).</summary>
    const float ShimmerMinEdge = 80f;   // below this (row/sidebar thumbs) the breathe is imperceptible — use a static tile

    /// <summary><paramref name="decodeW"/>/<paramref name="decodeH"/> must equal the decode target of the real image
    /// stacked over this tile so the load-state read shares its exact cache handle (no forked decode).</summary>
    public static Element Shimmer(string? url, int decodeW, int decodeH, float width, float height, float corners)
    {
        // Small thumbnails (track rows, sidebar, chips) get a CHEAP static neutral tile — no component, no image-epoch
        // subscription, no breathe — so a 50k-row virtualized list pays nothing per item. A url-less slot is static too.
        if (url is not { Length: > 0 } u || MathF.Min(width, height) < ShimmerMinEdge)
            // OPAQUE tile (forced A=1, not the translucent card fill): a small thumb sits over the sidebar/chrome band,
            // which is an unpainted omission over the window's BASE LAYER (live Mica), so a see-through placeholder lets
            // the desktop read through — the cover becomes a washed, low-contrast smear while it loads (or a dark hole
            // when it has no art / fails). Album art is opaque content; back it with an opaque neutral so it always
            // reads as a solid tile regardless of what is behind the window.
            // Tinted from the cover's own graded colour when the plane has one, else the neutral opaque tile.
            // This is the difference between a track list of blank grey squares and one that paints its covers at once.
            return new BoxEl { Width = width, Height = height, Corners = CornerRadius4.All(corners), Fill = PlaceholderFor(url) };
        // Covers/cards: the breathing shimmer. Keyed by url AND the decode bucket so a virtualized card that REBINDS to
        // a new cover, OR the SAME cover at a new decode target (a detail hero's unmeasured→measured bucket jump, a
        // shelf↔grid decode-size mismatch), remounts the tile (a Component freezes its ctor args at mount) — otherwise
        // the stale Component instance keeps calling UseImage against its FIRST decode handle and its load-state read
        // (and hence the breathe/settle) never tracks the size the new real Image actually asked for.
        // Skeletonized(false): inside a Skel.Region's derived skeleton this opaque component would otherwise map to the
        // deriver's default bar (a stray stripe in the cover). Dropping it lets the paired Image's derived placeholder be
        // the cover square — identical to a grid card (ArtworkFill is a bare Image), so every loading cover reads the same.
        return (Embed.Comp(() => new CoverShimmer(u, decodeW, decodeH, width, height, corners)) with { Key = "shim:" + u + ":" + decodeW + "x" + decodeH })
            .Skeletonized(false);
    }

    /// <summary>Artwork slot: a neutral <see cref="Shimmer"/> tile under the async image (which cross-fades in over it
    /// once decoded). <paramref name="morphKey"/> tags the image as a connected-animation (Hero) participant so it flies
    /// to/from the like-tagged Home card. The tile shares ONE decode handle with the image (matched W×H, any aspect).</summary>
    public static Element Artwork(Image? image, int seed, float width, float height, float corners, string? morphKey = null,
                                  int decodePx = 0, float saturation = 1f, bool preferLargest = false)
    {
        if (image?.MosaicTiles is { Count: > 0 } tiles)
        {
            if (tiles.Count >= 4) return Mosaic(tiles, width, height, corners);
            image = new Image(tiles[0]);   // 1–3 distinct album covers → show the first as a single cover
        }
        string? url = ImageSource.UrlFor(image, preferLargest);
        if (url is { Length: 0 }) url = null;
        // Decode target: the display size by default; when decodePx>0 decode at THAT square size and COVER-fit it into the
        // slot instead. A connected-animation dest (the detail cover) passes the SAME decodePx as the Home card (256) so it
        // resolves to the SAME cached texture — the Hero fly hands off pixel-identically with NO fresh decode (killing the
        // cold first-visit cover-decode spike). The shimmer shares the chosen decode handle (matched W×H), so no fork.
        int dw = decodePx > 0 ? decodePx : (int)width, dh = decodePx > 0 ? decodePx : (int)height;
        // Shared-layout art owns its placeholder. Culling only the tagged ImageEl must not leave a separate shimmer
        // sibling painting the old large slot behind the flying overlay.
        // Un-tagged art keeps its transparent placeholder because the Shimmer tile below already fills the slot (and
        // carries the tint). A morph participant owns its own placeholder — resolve that one directly, since it has no
        // shimmer sibling to inherit from.
        ColorF placeholder = morphKey is null ? ColorF.Transparent : PlaceholderFor(url);
        Element img = url is null ? new BoxEl()
            : decodePx > 0
                ? Ui.Image(url, ImageFit.Cover, 1f, decodePx, corners, placeholder, image!.BlurHash) with { MorphId = morphKey, Saturation = saturation }
                : Ui.Image(url, width, height, corners, placeholder, image!.BlurHash) with { MorphId = morphKey, Saturation = saturation };
        return new BoxEl
        {
            ZStack = true, Width = width, Height = height, ClipToBounds = true,
            Corners = CornerRadius4.All(corners),
            Children = morphKey is null ? [Shimmer(url, dw, dh, width, height, corners), img] : [img],
        };
    }

    /// <summary>A square cover that FILLS the width its layout hands it (CSS aspect-ratio 1) — for responsive grid cells
    /// whose exact width isn't known at template time (ItemsView grid tiles). Same Cover-fit + blurhash as
    /// <see cref="Artwork"/>; pass <see cref="Radii.Full"/> for a layout-derived circular (artist) tile.</summary>
    public static Element ArtworkFill(Image? image, float corners, int decodePx = 256)
    {
        // A cover-less playlist in a fluid grid cell falls back to its first tile (the explicit-size Mosaic needs a known
        // width, which a fill cell doesn't have); the home/sidebar/detail cover-less cases use Artwork/Shelf which mosaic.
        if (image?.MosaicTiles is { Count: > 0 } tiles) image = new Image(tiles[0]);
        string? url = image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
        // Tinted from the cover's own graded colour exactly like Shimmer/Artwork. Without this a whole grid of albums
        // loads as identical grey squares while the track list beside it paints in colour — the placeholder is on
        // screen longest precisely where the most art is loading at once.
        return Ui.Image(url ?? "", ImageFit.Cover, 1f, decodePx, corners, PlaceholderFor(url), image?.BlurHash);
    }

    /// <summary>A 2×2 mosaic of 4 album covers at an EXPLICIT size — how Spotify renders a cover-less playlist. Each
    /// quadrant is url-keyed, so when the playlist's tracklist changes the changed tile re-decodes + the rest stay.</summary>
    public static Element Mosaic(System.Collections.Generic.IReadOnlyList<string> tiles, float width, float height, float corners)
    {
        int cell = (int)(width / 2);
        Element Cell(string u)
        {
            string? n = ImageSource.Normalize(u);
            return new BoxEl { Grow = 1f, ClipToBounds = true, Children = [ Ui.Image(n ?? "", ImageFit.Cover, 1f, cell, 0f, PlaceholderFor(n)) ] };
        }
        return new BoxEl
        {
            Width = width, Height = height, ClipToBounds = true, Corners = CornerRadius4.All(corners), Direction = 1,
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1f, Children = [ Cell(tiles[0]), Cell(tiles[1]) ] },
                new BoxEl { Direction = 0, Grow = 1f, Children = [ Cell(tiles[2]), Cell(tiles[3]) ] },
            ],
        };
    }

    // ── section accents (the WaveeMusic "region" look: an accent-ruled header + a faintly tinted band) ─────────

    /// <summary>THE section ornament: a 20×2 accent RULE that sits under a section header's text.
    ///
    /// <para>It replaces a 3 × 22 capsule with a 1.5 radius parked to the LEFT of the header — which was, pixel for
    /// pixel, the selection-indicator geometry (a short accent bar flush against a control's edge, exactly what
    /// SelectorBar's pill and the nav rail's marker mean) doing a decorative job. Reusing selection geometry for
    /// decoration is the one thing the accent budget's first hard rule forbids: with it, every artist-page section read
    /// as "you are here", eight times down one page. A horizontal rule UNDER the text cannot be confused for a
    /// selection marker, and it is the older and quieter editorial idiom besides.</para>
    ///
    /// <para>20 and 2 are both on the 4-grid's half-step ladder and deliberately fixed: a rule that tracked the title's
    /// width would make eight sections eight different lengths, which is the raggedness the constant avoids.</para></summary>
    public static BoxEl AccentRule(ColorF accent) => new()
    {
        // AlignSelf.Start, explicitly: in the header's COLUMN the cross axis is horizontal, and a stretched rule would
        // run the full width of the section instead of being a 20-DIP mark.
        Width = AccentRuleWidth, Height = AccentRuleHeight, Shrink = 0f, AlignSelf = FlexAlign.Start,
        Fill = accent, HitTestVisible = false,
        Margin = new Edges4(0f, AccentRuleGap, 0f, 0f),
    };

    /// <summary>The section rule's geometry — one definition, so the artist page's counted header and the shared shelf
    /// header cannot drift.
    ///
    /// <para><see cref="AccentRuleGap"/> is the rule's TOP margin and stacks on top of the header column's own 2-DIP
    /// gap, so it is half of a two-part distance. At 6 that distance was 8 DIP below a 28-DIP line box — far enough
    /// that the mark floated free of the text and read as a separate object under the header (and, inside a
    /// <c>PagedShelf</c> header row, grew the row ~10 DIP past the 32-DIP chevrons it sits beside). At 2 the total is
    /// 4 DIP: a typographic rule attached to its title, and a +2 DIP shelf header instead of +10.</para></summary>
    public const float AccentRuleWidth = 20f, AccentRuleHeight = 2f, AccentRuleGap = 2f;

    /// <summary>A section header: an optional eyebrow, the title, and the <see cref="AccentRule"/> under them. The rule
    /// and the eyebrow take <paramref name="accent"/> — e.g. a <see cref="WaveePalette.Lift"/>-ed cover-extracted color,
    /// so a shelf's rule matches its content. Returns a <see cref="BoxEl"/> so call sites can layout-tweak it via
    /// <c>with</c>.</summary>
    public static BoxEl AccentHeader(string title, ColorF accent, string? eyebrow = null)
    {
        var head = WaveeType.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        Element[] lines = eyebrow is { Length: > 0 }
            ? [WaveeType.Eyebrow(eyebrow) with { Color = accent, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, head, AccentRule(accent)]
            : [head, AccentRule(accent)];
        return new BoxEl { Direction = 1, Gap = 2f, MinWidth = 0f, Children = lines };
    }

    /// <summary>A module header — a title, an optional subdued subtitle on the same BASELINE, and an optional trailing
    /// tools slot. No accent bar: the accent-bar variant (<see cref="AccentHeader"/>) reads as a region marker, which is
    /// right for a handful of distinct page sections (ArtistPage) and wrong for a dozen stacked modules, where it turns
    /// the page into a column of coloured rules.
    ///
    /// <para>The title deliberately carries <c>Grow = 1f</c> and NO <c>Basis</c>. That is not a style choice — a
    /// <c>Basis = 0f</c> here collapsed every header inside a <c>PagedShelf</c> to a single ellipsised letter: a shelf
    /// inserts a custom header raw into a <c>Direction = 0</c> row whose only growable child is a trailing spacer, and in
    /// a definite-width row <c>Basis = 0</c> suppresses intrinsic width entirely (FlexLayout's flex-base rule). With
    /// Basis left at NaN the intrinsic width is the real text width, and Grow still lets it fill and ellipsise when the
    /// row is genuinely tight. <c>BrowsePage</c>'s shelf header is the same shape.</para>
    ///
    /// <para>Deliberately a separate method rather than a flag on <see cref="AccentHeader"/>: that one has live callers
    /// whose look must not change. Note the engine also has a <c>Ui.SectionHeader</c> — call this one qualified.</para></summary>
    public static BoxEl SectionHeader(string title, string? subtitle = null, Element? tools = null)
        => new()
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                // Title + subtitle as ONE paragraph so the small run shares the heading's baseline (the engine has no
                // FlexAlign.Baseline). Shrink, never Grow: a SPACER — not the heading — pushes the tools to the
                // trailing edge, which is what keeps the subtitle sitting right next to the title.
                subtitle is { Length: > 0 } s
                    ? WaveeType.ModuleHeader(title, s)
                    : WaveeType.ModuleHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                tools ?? new BoxEl(),
            ],
        };

    /// <summary>A "section band": a quiet rounded WASH behind a page section, carrying the accent as a soft tint that
    /// kisses the top edge and fades into the material by ~45% down.
    ///
    /// <para>NOT a card. It used to be one — <see cref="Tok.FillCardDefault"/> plus a <see cref="Tok.StrokeCardDefault"/>
    /// hairline — which put a bordered container around a PAGE SECTION whose content was already a row of bordered
    /// cards: a box of boxes, and one more stroke than Fluent's grouped-content look actually draws. The shell's
    /// published material already carries the page's tint (D19/D29), so the band's only remaining job is to say "these
    /// belong together", and an unbordered wash says it without adding an edge. The border is therefore GONE and the
    /// fill stays as the tint alone; one opaque top→bottom gradient keeps it a single, cheap node.</para>
    ///
    /// <para>Layout-neutral by construction: the dropped border was 1 DIP inside a <see cref="Spacing.L"/> padding box,
    /// so nothing shifts. (No live call site today — Home's hero flattened its 16-DIP inset into
    /// <c>HomeHeroLayout</c> — this is kept as the ONE recipe a future banded section must use, so the treatment cannot
    /// be re-invented with a stroke.)</para></summary>
    public static BoxEl SectionBand(Element content, ColorF accent)
    {
        ColorF card = Tok.FillCardDefault;
        // Kiss the card fill toward the accent at the very top (heavier in dark, where a faint tint would vanish), but
        // hold the card's own alpha so the surface's translucency stays uniform — only the HUE shifts at the top.
        ColorF top = ColorF.Lerp(card, accent, Tok.Theme == ThemeKind.Dark ? 0.10f : 0.06f) with { A = card.A };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
            Corners = CornerRadius4.All(Radii.Card),
            Gradient = GradientDown(
                new GradientStop(0f, top),
                new GradientStop(0.45f, card),
                new GradientStop(1f, card)),
            Children = [content],
        };
    }

    /// <summary>The material layer for Home's integrated-cover hero. It keeps the translucent card alpha intact while
    /// kissing only the upper hue toward the cover accent.</summary>
    public static GradientSpec HomeHeroBackdrop(ColorF accent)
    {
        ColorF card = Tok.FillCardDefault;
        ColorF top = ColorF.Lerp(card, accent, Tok.Theme == ThemeKind.Dark ? 0.10f : 0.06f) with { A = card.A };
        return GradientDown(
            new GradientStop(0f, top),
            new GradientStop(0.45f, card),
            new GradientStop(1f, card));
    }
}

// The neutral shimmer cover tile. A Component (granular re-render) so it can read the image load-state and START/STOP
// its own opacity breathe accordingly: the UseKeyframes layout-effect is keyed by `loading`, so when the art becomes
// ready the looping pulse is replaced by a finite flat track (opacity → 1) and the frame loop can quiesce (the engine's
// "no forever-loop" rule). The breathe mirrors the engine SkeletonPulse (1.0↔0.5 over 1s) and the fill matches the app's
// skeleton blocks (DetailSkeleton's reserved cover slot, Skel.Region's derived shimmer bars) so every placeholder reads identically.
sealed class CoverShimmer : Component
{
    static readonly Keyframe[] Breathe = [new(0f, 1f), new(0.5f, 0.5f), new(1f, 1f)];
    static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

    readonly string? _url;
    readonly int _decodeW, _decodeH;
    readonly float _w, _h, _corners;
    public CoverShimmer(string? url, int decodeW, int decodeH, float w, float h, float corners)
    { _url = url; _decodeW = decodeW; _decodeH = decodeH; _w = w; _h = h; _corners = corners; }

    public override Element Render()
    {
        // Breathe only WHILE the art is loading. Once it resolves we latch `settled` and stop calling UseImage, so the
        // tile unsubscribes from the global image epoch — a loaded cover then never re-renders on an unrelated image's
        // status change (no steady-state / scroll re-render storm across a grid of covers).
        var settled = UseRef(false);
        bool loading = false;
        if (!settled.Value && _url is { Length: > 0 } url)
        {
            // Share the displayed image's decode handle (same src + decode target) so this reads the SAME load-state and
            // forks no second decode. UseImage doesn't consume a hook cell, so the conditional call is safe.
            var binding = UseImage(url, _decodeW, _decodeH);
            var state = binding.State;
            var failure = binding.Failure;
            if (state == ImageState.Ready) settled.Value = true;
            else if (state == ImageState.Failed && failure != ImageFailureKind.Canceled) settled.Value = true;
            else loading = state is ImageState.None or ImageState.Pending;
        }
        // On the loading→settled edge `loading` flips, the dep changes, and the effect re-seeds a finite flat track
        // (loop:false) — the looping pulse is replaced in place and the loop-track count drops so the frame loop quiesces.
        UseKeyframes(AnimChannel.Opacity, loading ? Breathe : Flat, loading ? 1000f : 1f, loading, DepKey.From(loading));   // #9: DepKey, not a boxed object[]
        // Tint is paint-only: the Fill bind reads the per-key Watch signal, so a landed grading marks PaintDirty on
        // exactly this tile — never a CoverShimmer re-render, and never the global Epoch fan-out that used to re-render
        // every still-loading cover in the grid at once.
        return new BoxEl
        {
            Width = _w, Height = _h, Corners = CornerRadius4.All(_corners),
            Fill = Prop.Of(PlaceholderFill),
        };
    }

    ColorF PlaceholderFill()
    {
        if (_url is { Length: > 0 } url)
            _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
        return Surfaces.PlaceholderFor(_url);
    }
}
