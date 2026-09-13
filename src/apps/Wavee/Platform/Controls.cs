// ── Platform/Controls.cs ───────────────────────────────────────────────────────────────────────────────────────────
// Surfaces, SearchHighlight, Equalizer, SaveButton/PreSaveButton/FollowButton, SelectionBar, RowSwipe,
// ExplicitBadge, MoreButton, ExpandChevron, the three dialog helpers, the vacancy grammar, Notify.Say. The envelope
// for the whole file family is ch 00's 4,500-6,000; chapters 02's and 01's shares sit INSIDE it — do not double
// count
//
// Role: UI
// Owner: L
// Wave: 4
// Budget: 2000 lines
// Spec: DERIVED split of ch 00 §9.4's 5,250 midpoint
//
// ── THE FILE FAMILY ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// `Controls` is ONE static partial class across four files, named on day one (A16):
//   · Controls.cs         — this file: the art surfaces, the row primitives, the states, the dialogs, the toasts.
//   · Controls.Cta.cs     — the media pill, its icon arm, the standard icon button, the text action, the CTA ramps.
//   · Controls.Art.cs     — the card plate, the shelf/grid/row skins, the now-playing overlay, chips, stat tiles,
//                           countdowns, face piles, rich text, the shimmer component.
//   · Controls.Picker.cs  — the preview-card radio strip and the equalizer curve.
//
// ── THE SEAMS, AND WHY THEY ARE SEAMS ────────────────────────────────────────────────────────────────────────────────
//
// Three things this file's controls need are owned by other waves, and every one of them is a delegate installed ONCE by
// the composition root rather than an import:
//   · `Library`   — "is this uri saved" + "toggle it". The library IS an edge in 0.3 (owner O's `Entities/User.cs`), and
//                   a shared control must not reach into a page's model.
//   · `RouteForUri` — an entity uri → the app's route key. The route TABLE is the shell's (owner I).
//   · `PreSave`   — the album→prerelease resolve. One wire hop, owner M's `Album.Page.cs`.
// A null seam renders NOTHING rather than a dead affordance — the capability gate, verbatim from 0.2.9: an affordance
// is gated on the declared capability, never hardcoded.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Controls
{
    /// <summary>True when <paramref name="service"/> is no real overlay host: null, or the engine's context default (its
    /// internal no-op service, which app code cannot name). A surface with no host skips menus and flyouts.</summary>
    public static bool IsNullOverlay([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] IOverlayService? service)
        => service is null || ReferenceEquals(service, Overlay.Service.Default);

    // ══ 0. THE SEAMS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The library-mutation seam. <c>IsSaved</c> is READ INSIDE RENDER, so it must subscribe (the 0.3 shape is
    /// a `LibraryEdge` probe, which is a column read on a table whose Version is a signal) — that is what re-skins a
    /// heart the instant the optimistic flip lands. Null ⇒ no Mutations source ⇒ every save/follow affordance renders
    /// NOTHING.</summary>
    public sealed record LibrarySeam(Func<string, bool> IsSaved, Action<string, string?> ToggleSaved);

    /// <inheritdoc cref="LibrarySeam"/>
    public static LibrarySeam? Library { get; set; }

    /// <summary>An entity uri → the app's route key ("pl:…", "album:…", "artist:…", "liked", "module:…"). Owner I's
    /// route table installs it; null leaves every rich-text anchor STYLED but inert, which is the honest degradation —
    /// a link to a route nothing renders is worse than a link that does not click.</summary>
    public static Func<string, string?>? RouteForUri { get; set; }

    /// <summary>The album → prerelease hop (extension kind 138). The collection write only accepts the
    /// <c>spotify:prerelease:</c> entity and a card usually only knows the album, so <see cref="PreSaveButton"/> costs
    /// one resolve. Returns the prerelease uri, or null when the release is already out / unresolvable.</summary>
    public static Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<string?>>? PreSave { get; set; }

    /// <summary>The ONE cover-url resolver for the whole UI. Image columns hold the full CDN url already (the decoder
    /// writes it), so this is a resolve plus two cheap shapes: a bare content-file id becomes a url, and a local file
    /// path or an already-formed url passes through untouched, which is what makes `--fake` and local imports work with
    /// no second path.</summary>
    public static string? ArtUrl(StringId image)
    {
        if (image.IsEmpty) return null;
        string id = Entities.Strings.Resolve(image);
        if (id.Length == 0) return null;
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || id.Contains(":\\", StringComparison.Ordinal)) return id;
        return CdnPrefix + id;
    }

    const string CdnPrefix = "https://i.scdn.co/image/";

    // ══ 1. THE ART SURFACES ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // The COLOUR half of this (the two grading halves, the tinted placeholder, the bound watch) is `Design.cs`'s §7.
    // These are the ELEMENT recipes over it.

    /// <summary>A neutral cover tile that BREATHES while the art at <paramref name="url"/> is still loading and settles
    /// to a calm static tile once it is ready / failed / absent — so an art slot reads as "loading", never as a coloured
    /// hole, and the pulse STOPS (a forever-loop would pin the frame loop awake).
    ///
    /// <para><paramref name="decodeW"/>/<paramref name="decodeH"/> MUST equal the decode target of the real image
    /// stacked over this tile, so the load-state read shares its exact cache handle and forks no second decode.</para>
    ///
    /// <para>Small thumbnails (rows, sidebar, chips) get the CHEAP static arm — no component, no hook cells, no
    /// image-epoch subscription — so a 50k-row virtualized list pays nothing per item. That tile is OPAQUE by
    /// construction (the fill forces A = 1): a small thumb sits over an UNPAINTED chrome band, which is live Mica, so a
    /// see-through placeholder lets the desktop read through and the cover becomes a washed smear while it
    /// loads.</para></summary>
    public static Element Shimmer(string? url, int decodeW, int decodeH, float width, float height, float corners)
    {
        if (url is not { Length: > 0 } u || MathF.Min(width, height) < Design.ShimmerMinEdge)
            return new BoxEl
            {
                Width = width, Height = height, Corners = CornerRadius4.All(corners),
                Fill = Design.WatchedPlaceholder(url),
            };
        // Keyed by url AND the decode bucket, because a Component freezes its ctor args at mount: a virtualized card
        // that REBINDS to a new cover — or to the SAME cover at a new decode target (a hero's unmeasured→measured
        // bucket jump, a shelf↔grid mismatch) — must remount, or the stale instance keeps reading the load state of its
        // FIRST decode handle and the breathe/settle never tracks the size the new real image asked for.
        //
        // Skeletonized(false): inside a derived skeleton this opaque component would otherwise map to the deriver's
        // default BAR — a stray stripe across the cover. Dropping it lets the paired image's own derived placeholder BE
        // the cover square, so every loading cover reads the same.
        return (Embed.Comp(() => new CoverShimmer(u, decodeW, decodeH, width, height, corners))
                with { Key = "shim:" + u + ":" + decodeW + "x" + decodeH })
            .Skeletonized(false);
    }

    /// <summary>An ARTWORK slot: a neutral <see cref="Shimmer"/> tile under the async image, which cross-fades in over
    /// it once decoded. The tile shares ONE decode handle with the image (matched W×H, any aspect).
    ///
    /// <para>DECODE BRANCHES, and all three matter: with <paramref name="decodePx"/> 0 and <paramref name="scale"/> 1
    /// the image decodes at its laid-out DIP size and keeps the slot's aspect; with <paramref name="decodePx"/> &gt; 0
    /// it decodes at that literal as a SQUARE and cover-fits — which is the hand-off path, because a card and a detail
    /// cover that pass the SAME literal resolve to ONE cached texture and neither re-decodes; with a real ambient
    /// <paramref name="scale"/> the budget is routed through <see cref="Design.ImageDecodeScale"/>'s bucket and ceiling.
    /// An unscaled explicit <paramref name="decodePx"/> keeps its EXACT literal, never silently rounded to the 8-px
    /// grid.</para>
    ///
    /// <para><paramref name="morphKey"/> tags the image as a shared-element participant. A morph-tagged slot mounts NO
    /// shimmer sibling (culling only the tagged image must not leave a separate tile painting the old slot behind the
    /// flying overlay) and therefore carries a FROZEN placeholder — the one art path in the app with no live tint. It is
    /// dormant today; if 0.3 ever re-arms the fly, this branch owes itself a watched placeholder.</para></summary>
    public static Element Artwork(string? url, float width, float height, float corners, string? morphKey = null,
                                  int decodePx = 0, float saturation = 1f, float scale = 1f, string? blurHash = null)
    {
        if (url is { Length: 0 }) url = null;

        bool scaleRequested = scale > 0f && MathF.Abs(scale - 1f) > 0.01f;
        bool useScaledDecode = decodePx > 0 || scaleRequested;
        int scaledDecodePx = scaleRequested
            ? Design.ImageDecodeScale.For(decodePx > 0 ? decodePx : MathF.Max(width, height), scale)
            : decodePx;
        int dw = useScaledDecode ? scaledDecodePx : (int)width;
        int dh = useScaledDecode ? scaledDecodePx : (int)height;

        // Un-tagged art keeps a TRANSPARENT placeholder because the shimmer tile below already fills the slot (and
        // carries the tint). A morph participant owns its own placeholder — resolved directly, since it has no sibling
        // to inherit from.
        ColorF placeholder = morphKey is null ? ColorF.Transparent : Design.PlaceholderFor(url);
        // The decodePx branch has always cover-fit a SQUARE decode into a possibly non-square slot; the scale-only
        // branch preserves the slot's REAL aspect instead, since nothing asked for a square crop there.
        float aspect = decodePx > 0 ? 1f : width / MathF.Max(1f, height);

        Element img = url is null
            ? new BoxEl()
            : useScaledDecode
                ? Ui.Image(url, ImageFit.Cover, aspect, scaledDecodePx, corners, placeholder, blurHash)
                    with { MorphId = morphKey, Saturation = saturation }
                : Ui.Image(url, width, height, corners, placeholder, blurHash)
                    with { MorphId = morphKey, Saturation = saturation };

        return new BoxEl
        {
            ZStack = true, Width = width, Height = height, ClipToBounds = true,
            Corners = CornerRadius4.All(corners),
            Children = morphKey is null ? [Shimmer(url, dw, dh, width, height, corners), img] : [img],
        };
    }

    /// <summary>A square cover that FILLS the width its layout hands it (aspect-ratio 1) — for responsive grid cells
    /// whose exact width is not known at template time. Pass <c>Radii.Full</c> for a layout-derived circular tile.
    /// <para>It NEVER mosaics: a fill cell has no known width, so a cover-less container collapses to its FIRST tile and
    /// shows one square. That is a deliberate difference from <see cref="Mosaic"/>, not a gap.</para></summary>
    public static Element ArtworkFill(string? url, float corners, int decodePx = 256, string? blurHash = null)
        => Ui.Image(url ?? "", ImageFit.Cover, 1f, decodePx, corners, placeholder: (ColorF?)null, blurHash)
            with { Placeholder = Design.WatchedPlaceholder(url) };

    /// <summary>A 2×2 mosaic of FOUR covers at an EXPLICIT size — how a cover-less playlist renders. Each quadrant is
    /// url-keyed, so when the membership changes the changed tile re-decodes and the rest stay.
    /// <para>Fewer than four distinct tiles is the CALLER's branch: show the first as a single cover. A three-tile
    /// mosaic is a broken mosaic.</para></summary>
    public static Element Mosaic(ReadOnlySpan<string> tiles, float width, float height, float corners)
    {
        int cell = (int)(width / 2);
        Element Cell(string u) => new BoxEl
        {
            Grow = 1f, ClipToBounds = true,
            Children = [Ui.Image(u, ImageFit.Cover, 1f, cell, 0f, placeholder: (ColorF?)null)
                            with { Placeholder = Design.WatchedPlaceholder(u) }],
        };
        return new BoxEl
        {
            Width = width, Height = height, ClipToBounds = true, Corners = CornerRadius4.All(corners), Direction = 1,
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1f, Children = [Cell(tiles[0]), Cell(tiles[1])] },
                new BoxEl { Direction = 0, Grow = 1f, Children = [Cell(tiles[2]), Cell(tiles[3])] },
            ],
        };
    }

    // ── the section ornaments ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The section rule's geometry — ONE definition, so a counted page header and the shared shelf header
    /// cannot drift.
    /// <para><see cref="AccentRuleGap"/> is the rule's TOP margin and stacks on top of the header column's own 2-DIP
    /// gap, so it is half of a two-part distance. At 6 that distance was 8 DIP below a 28-DIP line box — far enough that
    /// the mark floated free of the text and read as a separate object, and inside a paged shelf's header row it grew
    /// the row ~10 DIP past the 32-DIP chevrons beside it. At 2 the total is 4: a typographic rule attached to its
    /// title.</para></summary>
    public const float AccentRuleWidth = 20f, AccentRuleHeight = 2f, AccentRuleGap = 2f;

    /// <summary>THE section ornament: a 20 × 2 accent RULE under a section header's text.
    ///
    /// <para>It replaced a 3 × 22 capsule with a 1.5 radius parked to the LEFT of the header — which was, pixel for
    /// pixel, the SELECTION-indicator geometry doing a decorative job. Reusing selection geometry for decoration is the
    /// one thing the accent budget's first hard rule forbids: with it, every artist-page section read as "you are here",
    /// eight times down one page. A horizontal rule UNDER the text cannot be confused for a selection marker, and it is
    /// the older and quieter editorial idiom besides.</para>
    ///
    /// <para><c>AlignSelf.Start</c> is EXPLICIT: in the header's COLUMN the cross axis is horizontal, and a stretched
    /// rule would run the full section width instead of being a 20-DIP mark.</para></summary>
    public static BoxEl AccentRule(ColorF accent) => new()
    {
        Width = AccentRuleWidth, Height = AccentRuleHeight, Shrink = 0f, AlignSelf = FlexAlign.Start,
        Fill = accent, HitTestVisible = false,
        Margin = new Edges4(0f, AccentRuleGap, 0f, 0f),
    };

    /// <summary>A section header: an optional eyebrow, the title, and the <see cref="AccentRule"/> under them. The rule
    /// and the eyebrow take <paramref name="accent"/> — e.g. a lifted cover-extracted colour, so a shelf's rule matches
    /// its content.</summary>
    public static BoxEl AccentHeader(string title, ColorF accent, string? eyebrow = null)
    {
        var head = Design.Type.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        Element[] lines = eyebrow is { Length: > 0 }
            ? [Design.Type.Eyebrow(eyebrow) with { Color = accent, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
               head, AccentRule(accent)]
            : [head, AccentRule(accent)];
        return new BoxEl { Direction = 1, Gap = 2f, MinWidth = 0f, Children = lines };
    }

    /// <summary>A MODULE header — a title, an optional subdued subtitle on the same BASELINE, and an optional trailing
    /// tools slot. No accent bar: the accent-bar variant reads as a REGION marker, which is right for a handful of
    /// distinct page sections and wrong for a dozen stacked modules, where it turns the page into a column of coloured
    /// rules.
    ///
    /// <para>The title carries <c>Grow = 1f</c> and NO <c>Basis</c>. That is not a style choice — a <c>Basis = 0f</c>
    /// here collapsed every header inside a paged shelf to a single ellipsised LETTER: a shelf inserts a custom header
    /// raw into a row whose only growable child is a trailing spacer, and in a definite-width row <c>Basis = 0</c>
    /// suppresses intrinsic width entirely. With Basis left at NaN the intrinsic width is the real text width, and Grow
    /// still lets it fill and ellipsise when the row is genuinely tight.</para></summary>
    public static BoxEl SectionHeader(string title, string? subtitle = null, Element? tools = null)
        => new()
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                // Title + subtitle as ONE paragraph so the small run shares the heading's baseline. Shrink, never
                // Grow: a SPACER — not the heading — pushes the tools to the trailing edge, which is what keeps the
                // subtitle sitting right next to the title.
                subtitle is { Length: > 0 } s
                    ? Design.Type.ModuleHeader(title, s)
                    : Design.Type.ModuleHeader(title) with
                        { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                tools ?? new BoxEl(),
            ],
        };

    /// <summary>A "section band": a quiet rounded WASH behind a page section, carrying the accent as a soft tint that
    /// kisses the top edge and fades into the material by ~45% down.
    /// <para>NOT a card. It used to be one — a card fill plus a hairline — which put a bordered container around a page
    /// SECTION whose content was already a row of bordered cards: a box of boxes, and one more stroke than Fluent's
    /// grouped-content look actually draws. The shell's published material already carries the page's tint, so the
    /// band's only remaining job is to say "these belong together", and an unbordered wash says it without adding an
    /// edge.</para></summary>
    public static BoxEl SectionBand(Element content, ColorF accent)
        => new()
        {
            Direction = 1, Gap = Spacing.M,
            Padding = Edges4.All(Spacing.L),
            Corners = CornerRadius4.All(Radii.Card),
            Gradient = BandGradient(accent),
            Children = [content],
        };

    /// <summary>The material layer for an integrated-cover hero: the same gradient <see cref="SectionBand"/> paints,
    /// exposed on its own so a hero can apply it without the band's padding.</summary>
    public static GradientSpec HomeHeroBackdrop(ColorF accent) => BandGradient(accent);

    // Kiss the card fill toward the accent at the very top (heavier in dark, where a faint tint would vanish), but HOLD
    // the card's own alpha so the surface's translucency stays uniform — only the HUE shifts at the top.
    static GradientSpec BandGradient(ColorF accent)
    {
        ColorF card = Tok.FillCardDefault;
        ColorF top = ColorF.Lerp(card, accent, Tok.Theme == ThemeKind.Dark ? 0.10f : 0.06f) with { A = card.A };
        return GradientDown(
            new GradientStop(0f, top),
            new GradientStop(0.45f, card),
            new GradientStop(1f, card));
    }

    /// <summary>Semantic copy protection over full-bleed artist photography. Both axes use exactly FOUR stops — the
    /// recorder's limit, so this is a hard ceiling and not a preference — and both release to alpha 0 at the hero seam.
    ///
    /// <para>The HORIZONTAL arm is the near-opaque left plate (theme-invariant 0.96 / 0.92 / 0.35): the copy column sits
    /// on a real surface and the photography lives in the right half. A softened pass made the plate a whisper in light
    /// themes so the always-on bottom photo fade read as "the" fade; it was restored by explicit ruling. The VERTICAL
    /// arm keeps the softened peaks: it underlays copy stacked at a photo's bottom seam, where a 0.96 band flattened the
    /// image into a painted plate.</para></summary>
    public static GradientSpec ArtistHeroVeil(ColorF accent, bool vertical)
    {
        ColorF layer = Tok.FillLayerDefault;
        float pull = Tok.Theme == ThemeKind.Light ? 0.16f : 0.24f;
        ColorF veil = ColorF.Lerp(layer, accent, pull);
        if (vertical)
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

    // ══ 2. SEARCH HIGHLIGHT ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The library-search accent pill: [before] [matched run on the selection background] [after]. Shared by
    /// the library rows and the chart grid titles — ONE look.
    ///
    /// <para>TWO ARMS, and the wrapping one is what a grid title uses. <paramref name="maxLines"/> &gt; 1 lets the RUN
    /// ROW wrap — flex wrap, breaking BETWEEN runs, because a paragraph cannot carry the pill: a span paragraph shapes
    /// runs into one flow but has no per-span background fill, so a real paragraph would have to trade the pill for
    /// accent-coloured text.</para>
    ///
    /// <para>A trailing <c>Grow = 1</c> SPACER exists only to push a single-line row's runs left. A wrapping row must
    /// NOT carry one, or flex hands it the rest of line one and every following run breaks early. The wrap arm also
    /// drops <c>ClipToBounds</c>/<c>Basis 0</c> (they would clip the second line away and collapse the basis before wrap
    /// can be measured) and bounds itself with <c>MaxHeight</c> instead — a run ROW has no <c>MaxLines</c> of its own the
    /// way a single text node does.</para>
    ///
    /// <para>NO MATCH (or an out-of-range span) is its own arm: ONE plain ellipsised text node at the caller's
    /// size/weight/colour. No box, no pill, no 6-DIP gap where one used to be.</para></summary>
    public static Element SearchHighlight(string text, int matchStart, int matchLen, float size, ushort weight,
                                          ColorF baseColor, int maxLines = 1)
    {
        int lines = maxLines < 1 ? 1 : maxLines;
        bool wrap = lines > 1;
        if (matchLen <= 0 || matchStart < 0 || matchStart + matchLen > text.Length)
            return new TextEl(text)
            {
                Size = size, Weight = weight, Color = baseColor, MinWidth = 0f,
                Wrap = wrap ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines,
                Trim = TextTrim.CharacterEllipsis,
            };

        // A wrapping row lets the UNMATCHED runs break internally too, so a long tail is not forced to ellipsise just
        // because it could not fit beside the pill. Grow stays OFF when wrapping: a grown run would eat the whole first
        // line and push the pill down on its own.
        Element Seg(string s, bool grow) => new TextEl(s)
        {
            Size = size, Weight = weight, Color = baseColor,
            Grow = wrap ? 0f : (grow ? 1f : 0f), MinWidth = 0f,
            Wrap = wrap ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines, Trim = TextTrim.CharacterEllipsis,
        };

        var kids = new List<Element>(3);
        if (matchStart > 0) kids.Add(Seg(text[..matchStart], false));
        kids.Add(new BoxEl
        {
            Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.AccentSelectedTextBackground,
            Padding = new Edges4(3f, 1f, 3f, 1f),
            Children =
            [
                // The inner run is ALWAYS one line: a match never wraps inside its own pill.
                new TextEl(text.Substring(matchStart, matchLen))
                {
                    Size = size, Weight = weight, Color = Tok.TextOnAccentSelectedText,
                    MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        });
        int after = matchStart + matchLen;
        if (after < text.Length) kids.Add(Seg(text[after..], true));
        else if (!wrap) kids.Add(new BoxEl { Grow = 1f });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Wrap = wrap, Grow = wrap ? 0f : 1f, Basis = wrap ? float.NaN : 0f, ClipToBounds = !wrap,
            MaxHeight = wrap ? lines * LineBoxFor(size) : float.NaN,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The line box a run of <paramref name="size"/> occupies — the engine's own type ladder (14 → 20, 12 →
    /// 16), so the wrap cap matches what the shaper will lay. Anything off the ladder falls back to the WinUI-standard
    /// 1.43 ratio.</summary>
    public static float LineBoxFor(float size) => size switch
    {
        14f => 20f,
        12f => 16f,
        _ => MathF.Ceiling(size * 1.43f),
    };

    // ══ 3. THE NOW-PLAYING EQUALIZER ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The now-playing equalizer's motion DECISION, split out so it is testable without a window or a GPU:
    /// whether the tick should run at all, and — when it must not because motion is reduced — whether the bars settle
    /// into the "still playing" shape rather than the flat "not playing" rest height.
    ///
    /// <para>Two independent stop conditions compose in <see cref="ShouldTick"/>: the track is paused or hover-paused
    /// (the bars settle FLAT, the pre-existing "not playing" look), or motion is reduced (they settle to a fixed
    /// NON-UNIFORM shape instead, so a reduced-motion user can still tell "is this the one playing" from a mid-list
    /// glance without anything ever looping).</para>
    ///
    /// <para>Window-inactive/minimized is deliberately NOT an input: the engine's interval hook already folds the
    /// activation signal and auto-pauses every interval — this one included — while the window is minimized or power
    /// suspended. There is nothing for app code to decide for that half.</para></summary>
    public static bool ShouldTick(bool playing, bool hoverPaused, bool reducedMotion)
        => playing && !hoverPaused && !reducedMotion;

    /// <inheritdoc cref="ShouldTick"/>
    public static bool ShouldShowStillShape(bool playing, bool reducedMotion) => playing && reducedMotion;

    /// <summary>Three bottom-anchored bars, looping and phase-staggered while PLAYING, settled at a low static height
    /// when paused. Shared by the track rows' number cell and the cards' now-playing overlay.
    ///
    /// <para><paramref name="paused"/> is the row's HOVER signal wherever the bars sit under a hover-opacity reveal:
    /// they are invisible there but would still present a full window 30×/s. Pass the same signal that drives the fade;
    /// do NOT flip <paramref name="playing"/> or a Key on hover.</para></summary>
    public static Element Equalizer(IReadSignal<bool> playing, Func<ColorF> color, float height = 13f,
                                    IReadSignal<bool>? paused = null)
        => Embed.Comp(new EqHostProps(playing, color, height, paused), static () => new EqHost());

    /// <inheritdoc cref="Equalizer(IReadSignal{bool},Func{ColorF},float,IReadSignal{bool})"/>
    public static Element Equalizer(IReadSignal<bool> playing, ColorF color, float height = 13f,
                                    IReadSignal<bool>? paused = null)
        => Equalizer(playing, () => color, height, paused);

    /// <summary>The plain-bool arm for a non-virtualized row. Delegates through a constant signal — SAME host, same
    /// behaviour, no second implementation to keep in sync.</summary>
    public static Element Equalizer(bool playing, ColorF color, float height = 13f, IReadSignal<bool>? paused = null)
        => Equalizer(new ConstBool(playing), () => color, height, paused);

    /// <summary>A non-reactive read signal wrapping a fixed value — the adapter that lets the eager rows drive the SAME
    /// host the bound rows drive off a real per-row signal.</summary>
    readonly struct ConstBool(bool value) : IReadSignal<bool>
    {
        public bool Value => value;
        public bool Peek() => value;
    }

    sealed record EqHostProps(IReadSignal<bool> Playing, Func<ColorF> Color, float Height, IReadSignal<bool>? Paused);
    sealed record EqBarProps(FloatSignal ScaleY, Func<ColorF> Color, float Height);

    /// <summary>ONE ticker for all three bars, owned by the HOST.
    ///
    /// <para>Per-bar intervals were three independent timers with arbitrary phase — up to 45 distinct wake instants a
    /// second, each moving ONE bar, so every fire dirtied the scene and presented, and skip-submit could almost never
    /// see a byte-identical frame because a DIFFERENT bar moved each time. Measured at ~80% of the whole playing-state
    /// wake budget. Batching the three writes also collapses them into ONE frame request.</para>
    ///
    /// <para>The host is PERSISTENT across play↔pause: it reads a SIGNAL and toggles its interval, rather than being
    /// remounted by a Key. The old key flip tore the whole number-cell subtree down on every transition; the deps-gated
    /// effect below re-runs the phase/settle reset on exactly the transition that remount used to cover.</para></summary>
    sealed class EqHost : Component
    {
        const float LoopMs = 850f;
        // ~30 Hz. Be honest about the trade: there is no partial repaint, so every visible change costs a FULL-WINDOW
        // present — motion IS presents, and the tick rate IS the present rate for this widget. 15 Hz with 2-device-px
        // steps read as a visibly choppy meter; 30 Hz with 1-px steps is the smoothness floor that still costs a
        // quarter of a continuous per-frame track.
        const float TickMs = 1000f / 30f;

        static readonly float[][] Patterns =
        [
            [0.35f, 0.95f, 0.45f, 1.00f, 0.35f],
            [0.85f, 0.40f, 1.00f, 0.55f, 0.85f],
            [0.50f, 1.00f, 0.35f, 0.80f, 0.50f],
        ];

        // Stable for the host's lifetime — the bars BIND these, so nothing the host does disturbs the signals they read.
        readonly FloatSignal[] _scaleY = [new(0.4f), new(0.4f), new(0.4f)];
        long _startMs;

        public override Element Render()
        {
            var p = UsePropsOrDefault<EqHostProps>();
            if (p is null) return new BoxEl();
            bool animate = p.Playing.Value;           // subscribe — a bound row's play↔pause flip re-renders THIS host in place
            bool paused = p.Paused?.Value ?? false;   // subscribe — pause without a remount
            bool reduced = Design.Reduced;            // a VALUE, never a hook branch
            float scale = UseContext(Viewport.Scale);
            if (scale <= 0f) scale = 1f;

            UseEffect(() =>
            {
                if (!animate) { WriteAll(0.4f); return; }
                // A settled, NON-UNIFORM snapshot states "this is playing" without ever looping — flat 0.4 bars would
                // read identically to the paused branch above, and looping is exactly the continuous motion reduced
                // motion asks the app not to run.
                if (ShouldShowStillShape(animate, reduced)) { WriteStill(); return; }
                _startMs = Design.FrameTime.NowMs;
                Tick(p.Height, scale);
            }, animate);

            UseInterval(() => Tick(p.Height, scale), TickMs, enabled: ShouldTick(animate, paused, reduced));

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.End, Justify = FlexJustify.Center, Gap = 2f, Height = p.Height,
                Children =
                [
                    Embed.Comp(new EqBarProps(_scaleY[0], p.Color, p.Height), static () => new EqBar()),
                    Embed.Comp(new EqBarProps(_scaleY[1], p.Color, p.Height), static () => new EqBar()),
                    Embed.Comp(new EqBarProps(_scaleY[2], p.Color, p.Height), static () => new EqBar()),
                ],
            };
        }

        // One clock sample drives all three bars. FrameTime, never TickCount64 (ch 00 non-negotiable 14): TickCount64
        // advances in ~15.6 ms quanta, so a 30 Hz sampler reading it lands twice on the same instant and then jumps.
        void Tick(float heightDip, float scale)
        {
            float u = (Design.FrameTime.NowMs - _startMs) / LoopMs;
            u -= MathF.Floor(u);
            // Snap to WHOLE device pixels of the laid-out bar: crisper edges than a fractional height, and a tick that
            // lands on the same pixel for all three bars is a true no-op (below) so skip-submit can elide it.
            float hPx = heightDip * scale;
            Span<float> next = stackalloc float[3];
            bool anyChanged = false;
            for (int i = 0; i < 3; i++)
            {
                float sy = Sample(Patterns[i], u);
                float q = hPx > 1f ? MathF.Round(sy * hPx) / hPx : sy;
                next[i] = q;
                if (q != _scaleY[i].Peek()) anyChanged = true;
            }
            if (!anyChanged) return;   // nothing crossed a step this tick — leave the scene clean
            float n0 = next[0], n1 = next[1], n2 = next[2];
            Batch(() =>
            {
                if (n0 != _scaleY[0].Peek()) _scaleY[0].Value = n0;
                if (n1 != _scaleY[1].Peek()) _scaleY[1].Value = n1;
                if (n2 != _scaleY[2].Peek()) _scaleY[2].Value = n2;
            });
        }

        void WriteAll(float v) => Batch(() =>
        {
            for (int i = 0; i < 3; i++) if (v != _scaleY[i].Peek()) _scaleY[i].Value = v;
        });

        // Reduced motion's "playing" shape: each bar sampled from its own loop PATTERN at the same fixed instant, so
        // the three settle at different heights — legible as "an equalizer" at a glance — and then never move again.
        // A one-time write, not a frozen mid-loop frame: it never depends on the wall clock or on how long reduced
        // motion has been on.
        void WriteStill() => Batch(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                float v = Sample(Patterns[i], 0.3f);
                if (v != _scaleY[i].Peek()) _scaleY[i].Value = v;
            }
        });

        void Batch(Action write)
        {
            if (Context.Runtime is { } rt) rt.Batch(write); else write();
        }

        static float Sample(float[] keys, float u)
        {
            float t = u * 4f;
            int i = (int)MathF.Floor(t);
            if (i >= 4) return keys[4];
            float f = t - i;
            return keys[i] + (keys[i + 1] - keys[i]) * f;
        }
    }

    /// <summary>A pure consumer: it BINDS the signal the host ticks. No timer, no pattern, no phase of its own — and a
    /// bound transform is compositor-only, so a tick never re-renders anything.</summary>
    sealed class EqBar : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<EqBarProps>();
            if (p is null) return new BoxEl();
            var sig = p.ScaleY;
            return new BoxEl
            {
                Width = 2.5f, Height = p.Height, Corners = CornerRadius4.All(1.25f), Fill = p.Color(),
                AlignSelf = FlexAlign.End, TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, MathF.Max(sig.Value, 1e-3f))),
            };
        }
    }

    // ══ 4. THE LIBRARY AFFORDANCES ═══════════════════════════════════════════════════════════════════════════════════
    //
    // All three read the live saved-set through the ONE seam, so they re-skin the instant the optimistic flip lands and
    // survive a restart. They render NOTHING when no seam is connected — the affordance is GATED on the declared
    // capability, never hardcoded.
    //
    // PROPS FREEZE AT MOUNT: every one of these takes its uri as a constructor field, so EVERY call site must key the
    // embed on that uri (`Key = "save:" + uri`). A recycled row that did not would keep toggling the FIRST row's track.

    /// <summary>A like / save HEART — filled (accent) when the uri is saved, outline otherwise. Tracks (like) and albums
    /// (save).
    /// <para><see cref="Accent"/> is a THUNK, not a value: a detail hero derives its accent from art that lands AFTER
    /// the page mounts, and reading it inside Render subscribes — so the heart re-tints when the palette arrives instead
    /// of staying frozen at the mount-time default. Null falls back to the page's ambient accent, then to the semantic
    /// token.</para></summary>
    public sealed class SaveButton : Component
    {
        public required string Uri { get; init; }
        /// <summary>Display-only: names the item in the notification-centre activity entry.</summary>
        public string? Name { get; init; }
        public float Glyph { get; init; } = 16f;
        public float Box { get; init; } = 40f;
        /// <inheritdoc cref="SaveButton"/>
        public Func<ColorF>? Accent { get; init; }

        public override Element Render()
        {
            var lib = Library;
            var ctx = UseContext(Design.AccentCtx.Slot);
            if (lib is null) return new BoxEl();                 // no mutation source → no affordance
            bool saved = lib.IsSaved(Uri);                       // subscribe → re-skin on any saved-set change
            ColorF ink = Accent?.Invoke() ?? (ctx is { } a ? a.Value.Ink : Tok.AccentTextPrimary);
            return new BoxEl
            {
                Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Box / 2f),
                HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
                Role = AutomationRole.Button,
                OnClick = () => lib.ToggleSaved(Uri, Name),
                Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, Glyph, saved ? ink : Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>Pre-save / Pre-saved — the heart for something that is not out yet. Takes EITHER uri kind and resolves
    /// the prerelease entity itself, because that is the only entity the collection write accepts and a card usually
    /// only knows the album. Renders nothing until it resolves, nothing when no mutation source is connected, and
    /// nothing when the release has already dropped.
    ///
    /// <para>TWO STATES, the release-masthead action grammar verbatim: the call to action is the accent-FILLED pill (the
    /// Play slot), the engaged state is the bordered pill (the View slot) wearing the accent as INK.</para></summary>
    public sealed class PreSaveButton : Component
    {
        /// <summary>Either scheme: the album uri a card holds, or the prerelease uri the write needs.</summary>
        public required string Uri { get; init; }
        public string? Name { get; init; }
        /// <inheritdoc cref="SaveButton.Accent"/>
        public Func<ColorF>? Accent { get; init; }
        /// <summary>Label size; the glyph tracks it.</summary>
        public float TextSize { get; init; } = 12f;

        public override Element Render()
        {
            // Hooks first and UNCONDITIONALLY — every early return below is after the last hook call.
            bool direct = Uri.StartsWith("spotify:prerelease:", StringComparison.Ordinal);
            var resolved = UseResource(
                ct => direct || PreSave is null
                    ? System.Threading.Tasks.Task.FromResult<string?>(null)
                    : PreSave(Uri, ct),
                (string?)null, Uri).Loadable.Value.Value;

            string? target = direct ? Uri : resolved;
            var lib = Library;
            if (lib is null) return new BoxEl();                              // capability gate
            if (target is not { Length: > 0 }) return new BoxEl();            // resolving, unresolvable, or already out

            bool saved = lib.IsSaved(target);
            ColorF fill = Accent?.Invoke() ?? Tok.AccentDefault;   // read inside Render → a late palette re-tints it

            return new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(4f),
                Fill = saved ? ColorF.Transparent : fill,
                BorderWidth = saved ? 1f : 0f, BorderColor = saved ? fill : ColorF.Transparent,
                // Engaged: an EXPLICIT hover fill, because auto-lighten has nothing to lighten over a transparent pill.
                // Call to action: left at the default so the recorder auto-lightens the accent (the Play pill's
                // behaviour).
                HoverFill = saved ? Tok.FillSubtleSecondary : ColorF.Transparent,
                Cursor = CursorId.Hand, Role = AutomationRole.Button,
                OnClick = () => lib.ToggleSaved(target, Name),
                Children =
                [
                    Icon(saved ? Icons.HeartFill : Icons.Heart, TextSize + 1f,
                         saved ? fill : ColorContrast.PickContrast(fill)),
                    new TextEl(Loc.Get(saved ? Strings.Detail.PreSaved : Strings.Detail.PreSave))
                    {
                        Size = TextSize, Weight = 600, MaxLines = 1,
                        Color = saved ? fill : ColorContrast.PickContrast(fill),
                    },
                ],
            };
        }
    }

    /// <summary>A Follow / Following PILL — for artists and playlists (the "save" verb for a profile). Accent border +
    /// ink when followed: "you follow this" is a STATE, and the border carries it (accent role 2).
    /// <para>THE ONLY follow control. Geometry is the media capsule's (36 tall, fully rounded, the Standard hover/press
    /// rung) because a Follow pill stands beside a Play capsule on every artist hero — they have to be the same object
    /// at two jobs.</para></summary>
    public sealed class FollowButton : Component
    {
        public required string Uri { get; init; }
        public string? Name { get; init; }
        /// <summary>An on-media ink for a pill floated over photography; null takes the theme ink.</summary>
        public ColorF? Foreground { get; init; }

        public override Element Render()
        {
            var lib = Library;
            if (lib is null) return new BoxEl();                 // capability gate
            bool following = lib.IsSaved(Uri);                   // subscribe
            ColorF idleInk = Foreground ?? Tok.TextPrimary;
            return new BoxEl
            {
                Direction = 0, Height = PillHeight, Gap = Spacing.XS,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
                BorderWidth = 1f,
                BorderColor = following ? Tok.AccentDefault
                    : Foreground is { } fg ? fg with { A = 0.42f } : Tok.StrokeControlDefault,
                HoverFill = Foreground is { } hover ? hover with { A = 0.12f } : Tok.FillSubtleSecondary,
                PressedFill = Foreground is { } press ? press with { A = 0.18f } : Tok.FillSubtleTertiary,
                HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = () => lib.ToggleSaved(Uri, Name),
                Children =
                [
                    Icon(following ? Icons.HeartFill : Icons.Heart, 14f,
                         following ? Tok.AccentTextPrimary : idleInk),
                    Body(Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow)) with
                        { Weight = 600, Color = following ? Tok.AccentTextPrimary : idleInk },
                ],
            };
        }

        /// <summary>The skeleton SHAPE the deriver walks: the real pill, so it shimmers as a bordered pill rather than
        /// as a full-width default bar stretched across the actions row.</summary>
        public static Element SkeletonShape() => new BoxEl
        {
            Direction = 0, Height = PillHeight, Gap = Spacing.XS,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Children =
            [
                Icon(Icons.Heart, 14f, Tok.TextPrimary),
                Body(Loc.Get(Strings.Artist.Follow)) with { Weight = 600, Color = Tok.TextPrimary },
            ],
        };
    }

    /// <summary>The same follow toggle as a plateless TEXT ACTION, for the sticky context band — which has no plates in
    /// it at all, so the capsule above cannot go there. Same seam, same handler, same words; ON is accent INK instead of
    /// an accent border.</summary>
    public sealed class FollowTextAction : Component
    {
        public required string Uri { get; init; }
        public string? Name { get; init; }
        /// <summary>The band's own action height, supplied by owner M's context-band layout (A1 keeps that arithmetic
        /// out of this file).</summary>
        public float Height { get; init; } = 32f;
        /// <inheritdoc cref="Height"/>
        public float PadX { get; init; } = 10f;

        public override Element Render()
        {
            var lib = Library;
            if (lib is null) return new BoxEl();
            bool following = lib.IsSaved(Uri);
            return TextAction(Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow),
                              () => lib.ToggleSaved(Uri, Name), toggledOn: following,
                              height: Height, padX: PadX);
        }
    }

    // ══ 5. THE SMALL ROW PRIMITIVES ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The EXPLICIT badge: a 16 × 16 rounded square carrying an "E". Not a glyph and not a letter in the title
    /// — a mark, so it survives every locale and never joins the text run's shaping.</summary>
    public static Element ExplicitBadge(float size = 16f, ColorF? ink = null) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(2f), Fill = ink ?? Tok.TextTertiary,
        Children = [new TextEl("E") { Size = size * 0.68f, Weight = 700, Color = Tok.TextInverse }],
    };

    /// <summary>The overflow "…" — the STANDARD 32 × 32 icon button, and the one affordance that must not be
    /// invisible.
    /// <para><see cref="MoreRestOpacity"/> is 0.45 and it is the reported middle: 0 is undiscoverable (the defect this
    /// answers) and 1 is a real scanning cost on a 1,500-row list.</para>
    /// <para><c>BlocksDragArm</c>: forget it here and the "…" stops working the moment the row becomes a drag
    /// source.</para></summary>
    public const float MoreRestOpacity = 0.45f;

    /// <inheritdoc cref="MoreRestOpacity"/>
    public static BoxEl MoreButton(Action? onClick, bool requestsContext = true, float restOpacity = MoreRestOpacity)
        => IconAction(Icons.More, onClick, requestsContext) with
        {
            Opacity = restOpacity, HoverOpacity = 1f, BlocksDragArm = true,
        };

    /// <summary>The expand CHEVRON: a 32-square icon button whose glyph ROTATES rather than swapping, so the two states
    /// are one object at two angles. The disclosure curve is the engine's own, and expand/collapse are DIFFERENT curves
    /// — WinUI's disclosure pair, not one curve run backwards.</summary>
    public static BoxEl ExpandChevron(bool expanded, Action? onClick)
        => IconAction(Icons.ChevronDown, onClick, requestsContext: false) with
        {
            Transform = Prop.Of(() => Affine2D.Rotation(expanded ? MathF.PI : 0f)),
            TransformOriginX = 0.5f, TransformOriginY = 0.5f,
            BlocksDragArm = true,
        };

    // ══ 6. THE SWIPE BELT (touch only) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Is a touch swipe belt worth putting in the tree at all?
    ///
    /// <para>The belt is TOUCH-ONLY, so until a finger actually arrives every row's swipe control is inert weight — and
    /// it is not cheap weight: its props record carries the freshly-built row element, so the equality gate can never
    /// coalesce and the control re-renders 1:1 with every virtualized recycle (29-84 per scroll flush, measured) for
    /// something that cannot activate. TWO conditions gate it, cheapest first: the machine must HAVE a digitizer, and
    /// this session must have actually SEEN a touch contact. The second is what matters on a touch-capable laptop driven
    /// by mouse and trackpad, where the digitizer probe alone says "yes" forever and nothing ever pans.</para>
    ///
    /// <para>Reading it SUBSCRIBES the calling row render, so the first finger-down anywhere in the app re-renders the
    /// lists once and every row grows its wrapper from then on. Skipping the wrapper is look-identical — an untouchable
    /// touch-only swipe reveals nothing.</para>
    ///
    /// <para>The digitizer probe itself is `Platform.cs`'s (owner S, the Win32 seam) and fails SAFE: a probe error
    /// assumes touch is present, because dropping the belt is worse than carrying it.</para></summary>
    public static bool SwipeArmed => TouchCapable() && InputDispatcher.TouchObserved.Value;

    /// <summary>The digitizer probe, installed by `Platform.Host.cs`. Absent ⇒ assume touch IS present, which keeps the
    /// belt in the tree rather than silently deleting a gesture.</summary>
    public static Func<bool> TouchCapable { get; set; } = static () => true;

    // ══ 7. THE SELECTION BAR ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The command lane's fit tier from its MEASURED width: 0 = labels, 1 = glyphs, 2 = essentials only.
    /// <para>Measured, never inferred from the window: the bar lives inside a detail table whose own width is the rail's
    /// and the page's, so a window-width breakpoint would collapse the labels on a wide window with an open rail and
    /// keep them on a narrow one with none.</para></summary>
    public static int SelectionFitFor(float width) => width >= 760f ? 0 : width >= 390f ? 1 : 2;

    /// <summary>The contextual selection bar's CHROME and its fit-tier plumbing. The commands themselves are the
    /// caller's (owner M composes them from owner I's action table), because a shared control must not know what a track
    /// verb is.
    ///
    /// <para>It SELF-MEASURES the command lane and reads the count in the SAME render that builds the labels — never
    /// through a frozen responsive closure, which is what once left "1 selected" beside "Play 4 next".</para>
    ///
    /// <para><paramref name="standalone"/> is the overlay projection for a surface that has no persistent command bar of
    /// its own: the same content inside a floating acrylic card docked to the bottom of the list.</para></summary>
    public static Element SelectionBar(int count, Func<int, Element> commands, bool standalone = false,
                                       float bottomPadding = Spacing.XL, int minCount = 1)
        => Embed.Comp(new SelectionBarProps(count, commands, standalone, bottomPadding, minCount),
                      static () => new SelectionBarHost());

    sealed record SelectionBarProps(int Count, Func<int, Element> Commands, bool Standalone, float BottomPadding,
                                    int MinCount);

    sealed class SelectionBarHost : Component
    {
        readonly Signal<float> _laneW = new(0f);

        public override Element Render()
        {
            var p = UsePropsOrDefault<SelectionBarProps>();
            if (p is null || p.Count < p.MinCount) return new BoxEl();

            float lane = _laneW.Value;
            // 720 is the "before the first measure" stand-in: the LABEL tier, because a bar that starts at essentials
            // and grows to labels on its second frame reads as a glitch, while one that starts at labels and tightens
            // reads as a fit.
            float effective = lane > 0.5f ? lane : 720f;

            Element content = new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f,
                OnBoundsChanged = r =>
                {
                    if (r.W > 0f && MathF.Abs(r.W - _laneW.Peek()) > 0.5f) _laneW.Value = r.W;
                },
                Children = [p.Commands(SelectionFitFor(effective))],
            };
            if (!p.Standalone) return content;

            return new BoxEl
            {
                Direction = 1, Grow = 1f, HitTestPassThrough = true,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Padding = new Edges4(Spacing.L, 0f, Spacing.L, p.BottomPadding),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(8f, 6f, 8f, 6f),
                        Corners = CornerRadius4.All(Radii.Card),
                        Acrylic = Tok.AcrylicFlyout,
                        BorderWidth = 1f, BorderColor = Tok.StrokeFlyoutDefault,
                        Shadow = Elevation.Flyout,
                        Children = [content],
                    },
                ],
            };
        }
    }

    // ══ 8. THE VACANCY GRAMMAR ═══════════════════════════════════════════════════════════════════════════════════════
    //
    // FOUR VOICES × TWO SCALES = eight shapes, from ONE builder — and never eight builders, which is the shape that
    // drifted the first time (three types with three glyph sizes, three gaps and three heading rungs across six
    // surfaces).
    //
    // THE GRAMMAR, three parts in this order and nothing else:
    //   1. A display-face HEADLINE. Big type IS the empty state: it turns a hole in the page into a deliberate, composed
    //      thing. Page scale is the 28/36 page hero; RAIL scale is the 20/28 subtitle rung, because 28/36 wraps to three
    //      ragged lines at 240 DIP, which is not big type, it is a paragraph.
    //   2. ONE optional CAPTION line — what to do about it, in one sentence, at the metadata rung.
    //   3. AT MOST ONE QUIET action — the stock STANDARD button, never the accent one. This is the accent budget made
    //      structural: an empty page's "Browse" is a recovery route, not the app's most important verb, and accenting it
    //      meant an empty library shouted louder than a full one.
    //
    // NO GLYPH. A decorative pictogram above a heading adds no information the heading does not already carry, and it
    // was the part every rogue copy diverged on first.

    /// <summary>Which sentence a vacancy is speaking.</summary>
    public enum VacancyVoice
    {
        /// <summary>"There is nothing here yet." The steady state of a real, loaded, empty surface.</summary>
        Empty,
        /// <summary>"Something went wrong." An empty state WITH a reason; the technical detail goes to the log.</summary>
        Error,
        /// <summary>"You're offline." Degrade to cached content rather than blanking the surface.</summary>
        Offline,
        /// <summary>"Nothing matched." A filter or a query narrowed a non-empty surface to nothing — which is NOT the
        /// same sentence as <see cref="Empty"/>, because the remedy is to change the query, not to add content.</summary>
        NoMatch,
    }

    /// <summary>PAGE scale, or the RAIL scale for anything under ~340 DIP.</summary>
    public enum VacancyScale { Page, Compact }

    /// <summary>THE vacancy — every "there is nothing to show" surface in the app, at one of eight shapes. The app has
    /// no second way to say it.
    /// <para>The copy is VERBATIM and lives in the loc table; a caller overrides <paramref name="title"/> /
    /// <paramref name="subtitle"/> only where the surface genuinely knows something the grammar does not.</para></summary>
    public static Element Vacancy(VacancyVoice voice, VacancyScale scale = VacancyScale.Page,
                                  string? title = null, string? subtitle = null,
                                  string? actionLabel = null, Action? onAction = null)
    {
        (string t, string s) = voice switch
        {
            VacancyVoice.Error => (Loc.Get(Strings.Common.ErrorTitle), Loc.Get(Strings.Common.ErrorSubtitle)),
            VacancyVoice.Offline => (Loc.Get(Strings.Common.Offline), Loc.Get(Strings.Common.OfflineBanner)),
            VacancyVoice.NoMatch => (Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle)),
            _ => (Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle)),
        };
        // Error and Offline both offer Retry by default; Empty and NoMatch offer nothing, because there is nothing to
        // retry and a second "Browse" would be the accent budget's Action role spent on a recovery route.
        if (actionLabel is null && onAction is not null
            && voice is VacancyVoice.Error or VacancyVoice.Offline)
            actionLabel = Loc.Get(Strings.Common.Retry);

        TextEl headline = scale == VacancyScale.Page
            ? Design.Type.PageHero(title ?? t)
            : Ui.Subtitle(title ?? t);

        var kids = new List<Element>(4) { headline with { Wrap = TextWrap.Wrap } };
        string? caption = subtitle ?? s;
        if (caption is { Length: > 0 }) kids.Add(Design.Type.TrackMeta(caption) with { Wrap = TextWrap.Wrap });
        if (actionLabel is { Length: > 0 } && onAction is not null)
        {
            kids.Add(new BoxEl { Height = Spacing.L });
            kids.Add(Button.Standard(actionLabel, onAction));   // Standard, NEVER Accent — see the grammar note above
        }
        // Wrap, but no text alignment: a text node has no alignment knob, so centring is the CONTAINER's job and each
        // run is its own centred box.
        return new BoxEl
        {
            Direction = 1, Grow = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = Spacing.XS, Padding = Edges4.All(Spacing.XXL), Children = kids.ToArray(),
        };
    }

    /// <summary>The OFFLINE STRIP — the fourth voice's other shape. A notice over KEPT content, not a replacement of the
    /// page: an offline surface that still has cached rows shows them, with this above.</summary>
    public static Element OfflineStrip(string? message = null, Action? onRetry = null)
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.InfoBarBackgroundCircle, 16f, Tok.SystemFillCaution),
            Design.Type.TrackMeta(message ?? Loc.Get(Strings.Common.Offline)),
            new BoxEl { Grow = 1 },
        };
        if (onRetry is not null) kids.Add(Button.Standard(Loc.Get(Strings.Common.Retry), onRetry));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Fill = Tok.SystemFillCautionBackground, Corners = CornerRadius4.All(Radii.Control),
            Children = kids.ToArray(),
        };
    }

    // ══ 9. THE THREE DIALOG HELPERS ══════════════════════════════════════════════════════════════════════════════════
    //
    // THE WIDTH LADDER (ch 29 W22). 320 is the engine dialog's own minimum and the default whenever fewer than three
    // buttons are shown — a rename, a credits sheet, a picker, every destructive confirm. 480 is the implicit
    // three-button width and the item pickers'. 548 is the engine's maximum. A fourth width is a regression.

    /// <inheritdoc cref="Confirm"/>
    public const float DialogWidthCompact = 320f, DialogWidthWide = 480f, DialogWidthMax = 548f;

    /// <summary>THE destructive confirm — every "are you sure" in the app goes through this ONE helper.
    ///
    /// <para>BUTTON ORDER is primary, secondary, close, so the DESTRUCTIVE verb sits LEFT and Cancel sits RIGHT; the
    /// accent ring and the initial focus go to <b>Cancel</b>, so Enter cancels. A confirm whose default button is the
    /// destructive one is a confirm that does nothing.</para>
    ///
    /// <para>NO-OVERLAY FALLBACK: with nowhere to render, the action RUNS. A confirm that silently swallows the action
    /// in a headless path is worse than one that skips its own question — and note the deliberate asymmetry with the
    /// action platform, where a descriptor REFUSES in the same situation, because there the caller is a binding that can
    /// be re-armed.</para></summary>
    public static void Confirm(IOverlayService? overlay, string title, string body, string verb, Action onConfirm,
                               float width = DialogWidthCompact)
    {
        if (overlay is null) { onConfirm(); return; }
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.Message = body;
            d.PrimaryText = verb;
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.DialogWidth = width;
            d.PrimaryClick = onConfirm;
        });
    }

    /// <summary>A single-field PROMPT (rename a playlist, name a folder). Returns nothing: the caller's
    /// <paramref name="onCommit"/> is the result, because a dialog that awaits is a dialog the caller has to keep a task
    /// alive for.</summary>
    public static void Prompt(IOverlayService? overlay, string title, string commitLabel, string initial,
                              Action<string> onCommit, float width = DialogWidthCompact)
    {
        if (overlay is null) return;
        var text = new Signal<string>(initial);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.PrimaryText = commitLabel;
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DialogWidth = width;
            d.Content = TextBox.Create(text, v => text.Value = v);
            d.PrimaryClick = () => onCommit(text.Peek());
        });
    }

    /// <summary>A SHEET — a dialog whose body is the content and whose only button closes it (credits, a details card).
    /// The primary slot is left empty on purpose: the engine hides it, so the sheet has exactly one way out.</summary>
    public static void Sheet(IOverlayService? overlay, string title, Element body,
                             float width = DialogWidthCompact)
    {
        if (overlay is null) return;
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.PrimaryText = "";
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.DialogWidth = width;
            d.Content = body;
        });
    }
}

// ── 10. Notify.Say — the ONE toast door ───────────────────────────────────────────────────────────────────────────────
//
// A partial of owner I's `Platform/Notify.cs`, landed HERE because ch 29 §9.10 files it under `Platform/Controls.cs`'s
// share. Owner I owns every notification DECISION; this owns the one call shape the 107 toast sites take.

public static partial class Notify
{
    /// <summary>THE toast door. Every "say something happened" in the app goes through it, which is what makes the
    /// inventory a compile-time surface instead of a grep.
    ///
    /// <para><b>The announce rule lives here and nowhere else</b> (ch 29 §6.5), because 107 call sites cannot each be
    /// trusted to get it right:</para>
    /// <list type="bullet">
    /// <item>Error or Warning ⇒ announce <b>assertive</b>. Something went wrong and the user must be told now.</item>
    /// <item>Any severity WITH an action ⇒ announce POLITE. The action is time-boxed (the card dismisses itself), so a
    /// screen-reader user has to learn it exists before the window closes.</item>
    /// <item>Success with NO action ⇒ do <b>not</b> announce. Whatever produced it already announced at its own
    /// chokepoint (a library edit, a rootlist drop, a folder create), and announcing twice is worse than once.</item>
    /// <item>Informational ⇒ do not announce.</item>
    /// </list>
    ///
    /// <para>The MESSAGE is a resolved localized string, never an interpolation built at the call site for a toast that
    /// may be deduped away: the engine keys de-duplication on <paramref name="dedupeKey"/> ?? the message, so several
    /// lanes raising one card for one event pass a shared key rather than hoping their sentences match.</para>
    /// <para>D29: <paramref name="title"/> and <paramref name="customContent"/> pass straight through to
    /// <see cref="ToastOptions"/> — this is what let the update-lifecycle card (owner-I's progress bar, ch 19 §0 #11)
    /// move off a bare <see cref="Toast.Show"/> call and back through the ONE announce-rule door. Custom content still
    /// gets the severity/action/dedupe treatment; it only replaces the standard title/message body.</para></summary>
    public static ToastHandle Say(string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
                                  string? actionLabel = null, Action? onAction = null,
                                  string? dedupeKey = null, float durationMs = 5000f,
                                  string? title = null, Func<Element>? customContent = null)
    {
        bool hasAction = actionLabel is { Length: > 0 } && onAction is not null;
        bool assertive = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning;
        if (Announcer.IsAvailable && (assertive || hasAction)) Announcer.Say(message, assertive);

        return Toast.Show(message, new ToastOptions
        {
            Severity = severity,
            Title = title,
            ActionLabel = hasAction ? actionLabel : null,
            OnAction = hasAction ? onAction : null,
            DedupeKey = dedupeKey,
            DurationMs = durationMs,
            CustomContent = customContent,
        });
    }
}
