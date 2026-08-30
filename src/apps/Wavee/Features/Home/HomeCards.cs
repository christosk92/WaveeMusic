using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The Home card vocabulary ─────────────────────────────────────────────────────────────────────────────────────────
// One skin per CONTENT SHAPE, rather than one square card reused for twelve kinds of thing. Every number here is quoted
// from the design prototype (wavee-home-fluent.html) — its CSS is the spec, and the class name each skin implements is
// named in its doc comment so the two can be diffed.
//
// Authored on engine primitives (BoxEl / Ui.* / Tok.*) plus the app's own plumbing (Surfaces.Artwork for the
// decode+mosaic+placeholder pipeline, Interaction recipes for the state ramps) — deliberately NOT wrappers over
// MediaCard, whose shelf/grid cards are the look this replaces.
//
// Two conventions that are load-bearing:
//   • THE SPINE. A 2px hairline of the item's own colour along the card's bottom edge, and the ONLY place a per-item
//     colour touches a card. It belongs to exactly seven skins (quick tile, weekly, mix segment, chip card, feature,
//     crowd row, feed card) and to none of the tabular rows — see Spine().
//   • NO TABULAR FIGURES. The text seam has no font-variant-numeric, so every numeric column reserves a fixed-width
//     cell instead of relying on font features to align digits.
//
// EVERY skin must survive a null Meta: FakeData.HomeSeed renders this whole tree with blank cards to DERIVE the loading
// shimmer, so a dereference here is a crash on the loading path, not a display bug.
static class HomeCards
{
    // ── shared bits ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A card's identity colour, and the ONLY source of one: the section payload's own
    /// <c>extractedColors.colorDark</c> when it shipped one, else the cover's GRADED accent out of
    /// <c>CoverColorPlane</c> — the same image-keyed table every art placeholder, hero wash and shell material tint
    /// reads. Lifted
    /// either way, so a near-black grading still reads on a dark card and cannot bruise a light one.
    ///
    /// <para>NO FALLBACK. Null means "not graded yet", and the caller paints nothing until it is. A hash-derived palette
    /// was the wrong answer twice over: it is a fiction that looks like data, and it hid the actual defect — the
    /// store-backed sections (jump-back-in and recents) ship no <c>extractedColors</c> at all, so their
    /// colour has to come from the plane. Reading the plane on a miss ENQUEUES the grading (rendering the art is the
    /// request), and <see cref="AccentLeaf"/> watches its own image so the colour lands without touching anything
    /// else.</para></summary>
    static ColorF? RawAccent(HomeCard c)
    {
        if (c.Meta is { Accent: not 0u } m) return WaveePalette.ToColor(m.Accent);
        // backgroundTintedBase, NOT textBrightAccent: the payload path above is extractedColors.colorDark — a dark,
        // saturated tone — and the two sources have to speak the same language or a section with payload colours and one
        // without read as two different design systems. textBrightAccent is graded for TEXT on the cover, and as a 2px
        // hairline it came out near-white on most covers, which is exactly the undifferentiated look this was fixing.
        var s = Surfaces.SchemeFor(c.Image?.Url);
        if (s is not { } g) return null;
        uint tone = g.BackgroundTintedBase != 0u ? g.BackgroundTintedBase : g.BackgroundBase;
        return tone != 0u ? WaveePalette.ToColor(tone) : null;
    }

    internal static ColorF? Accent(HomeCard c)
        => RawAccent(c) is { } seed ? WaveePalette.Lift(seed) : null;

    static ColorF SpineFallback(HomeCard c)
    {
        // Semantic severity/accent roles form a theme-aware deterministic palette. A neutral cover still gets an
        // intentional identity cue, but never a fabricated grey rule or a frozen literal that fails a theme switch.
        uint h = unchecked((uint)SpotifyExportMapper.Hash(c.Uri));
        return (h & 3u) switch
        {
            0u => Tok.AccentDefault,
            1u => Tok.SystemFillSuccess,
            2u => Tok.SystemFillCaution,
            _ => Tok.SystemFillCritical,
        };
    }

    internal static ColorF? SpineAccent(HomeCard c, bool hovered = false)
    {
        if (RawAccent(c) is not { } seed) return null;
        var (_, saturation, _) = seed.ToHsv();
        if (saturation <= WaveePalette.NeutralS) seed = SpineFallback(c);
        return hovered ? WaveePalette.HairlineHover(seed) : WaveePalette.Hairline(seed);
    }

    /// <summary>The accent for CHROME that must paint something regardless — the hero's wash and its Play button. The app
    /// accent is the honest colour for chrome with no art colour behind it; it is not a per-item identity, which is
    /// exactly why the spines do not get this treatment.</summary>
    internal static ColorF AccentOrChrome(HomeCard c) => Accent(c) ?? Tok.AccentDefault;

    /// <summary>A title line with the now-playing equalizer trailing it. One helper rather than the same three-line row at
    /// ten call sites; the mark collapses to zero width when the card is not the sounding context, so the line is
    /// byte-identical to a bare title in the overwhelmingly common case.</summary>
    static Element Titled(Element title, string uri, float mark = 11f) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children = [title, HomeNowPlaying.Mark(uri, mark)],
    };

    /// <summary>`.spine` — `position:absolute; left:0; right:0; bottom:0; height:2px`. A ZStack overlay, so it sits on the
    /// card's bottom edge whatever direction the content flows and never occupies a layout slot. A LEAF component because
    /// it resolves its own colour: it watches its one cover, so a landing colour repaints this hairline and nothing else.
    /// (A page-scope <c>Epoch</c> subscription would re-render every shelf on Home each time a scrolling grid finished
    /// another grading batch — CoverColorPlane says so itself.)</summary>
    /// <para>The POSITIONING stays on a plain box. A ComponentEl carries no layout props at all — no AlignSelf, no
    /// JustifySelf, no Height — so a bare component as a ZStack layer lands wherever the default puts it, which is how
    /// every spine on the page silently vanished the first time this became a component.</para>
    static Element Spine(HomeCard c) => new BoxEl
    {
        Direction = 0, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 2f, HitTestVisible = false,
        Children = [HomeAccentLeaf.Of(c, HomeAccentLeaf.Kind.Spine)],
    };

    // A card surface: contour + the state ramp, no shadow and no footprint scale. ZStack so the spine can overlay the
    // bottom edge. `radius` is the prototype's per-skin choice — r-card (8) for the big surfaces, r-ctrl (4) for the
    // dense tiles and rows.
    static BoxEl Card(Element content, Action onClick, float radius, HomeCard? spine = null, float height = 0f)
    {
        Element[] kids = spine is { } s ? [content, Spine(s)] : [content];
        return MediaCard.ApplyCardPhysics(new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Height = height > 0f ? height : float.NaN,
            Corners = CornerRadius4.All(radius),
            ClipToBounds = true,
            OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            Children = kids,
        }.Interactive(Interaction.Card));
    }

    // A tabular ROW: no contour, no spine, transparent → subtle ramp. `.drow`, `.qrow`, `.brow`, `.trk`, `.tlrow`.
    static BoxEl Row(Element content, Action onClick, float height = 0f)
        => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Height = height > 0f ? height : float.NaN,
            Corners = CornerRadius4.All(Radii.Control),
            OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            Children = [content],
        }.Interactive(Interaction.ListRow);

    // Art, always through the app's one artwork slot — never a hand-rolled ImageEl. decodePx is the SQUARE decode target
    // so several sizes of one cover share a texture.
    static Element Art(HomeCard c, float w, float h, float corners, int decodePx = 0)
        // The liked card gets the DYNAMIC cover — the treatment the user picked, built from their own newest likes —
        // and never the picker: Home shows the collection, the collection's page owns its settings. The gate lives
        // here, at the render site, and not in StoreLibrarySource: a catalog source can neither read the appearance
        // setting cleanly nor react to AppearancePrefs.Bump, so its tiles would be stale until the next Home fetch.
        // The URI is the whole gate — a card that arrived WITH the provider's stock liked artwork must still take the
        // user's treatment (LikedSongsArtwork.For), and a letterbox slot centre-crops it rather than falling back.
        => LikedSongsArtwork.For(c.Uri, w, h, corners)
            ?? Surfaces.Artwork(c.Image, SpotifyExportMapper.Hash(c.Uri), w, h, corners,
                decodePx: decodePx > 0 ? decodePx : (int)MathF.Max(w, h));

    /// <summary>`.chip` — a FILLED seed chip: `--subtle-2`, control radius, Caption 12/16, 8-by-2 padding. Used for a
    /// mix's seed artists.</summary>
    /// <remarks>The prototype's radius 3 / 11-on-16 / 1-by-6 padding now reads as the CONTROL radius rung, Caption
    /// (12/16) and the 4-grid. A chip is the smallest thing on Home that carries type, which is exactly where an
    /// off-ramp size shows up as "this page has two type systems".</remarks>
    static Element Chip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), MinWidth = 0f,
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children = [Caption(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>`.tag2` — a BORDERED tag: ctrl-stroke outline, control radius, Caption 12/16. Deliberately different from
    /// <see cref="Chip"/>: the hero's daylist terms are labels ON a washed surface, where a filled chip would read as a
    /// second material.</summary>
    /// <remarks>Identical metrics to <see cref="Chip"/> — the two differed only by one DIP of side padding, which is
    /// not a distinction anybody can see and not one the design intended.</remarks>
    static Element Tag(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), MinWidth = 0f,
        Corners = Radii.ControlAll,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [Caption(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    static Element ChipRun(IReadOnlyList<string>? seeds, int max)
    {
        if (seeds is not { Count: > 0 }) return new BoxEl();
        int n = Math.Min(seeds.Count, max);
        var kids = new Element[n];
        for (int i = 0; i < n; i++) kids[i] = Chip(seeds[i]);
        return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f, Children = kids };
    }

    /// <summary>`.count` — small 12px tertiary. A fixed width where it heads a column, because proportional digits cannot
    /// column-align without one.</summary>
    static Element Count(string text, float width = 0f) => width > 0f
        ? new BoxEl
        {
            Width = width, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Children = [Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1 }],
        }
        : Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };

    /// <summary>The hover-revealed play affordance. Opacity-only (0 → 1), so it costs no layout and no per-frame work:
    /// the engine services HoverOpacity itself. `.qplay` also scales .86 → 1, which a composited HoverScale gives free.</summary>
    static Element HoverPlay(Action onPlay, float size = 28f, bool solid = true) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(size),
        Fill = solid ? Tok.AccentDefault : ColorF.Transparent,
        Opacity = 0f, HoverOpacity = 1f,
        // `.qplay` scales .86 -> 1 on hover — the web prototype's +16%. Systematized onto the Emphatic tier (a round
        // play affordance over media is exactly what that tier is for); a composited transform, so nothing re-lays-out.
        HoverScale = WaveeMotion.ScaleEmphatic.Hover,
        HoverDurationMs = MotionTok.ControlFast.DurationMs, HoverEasing = MotionTok.ControlFast.Easing,
        OnClick = onPlay, Cursor = CursorId.Hand, Role = AutomationRole.Button,
        Children = [Icon(Icons.Play, size <= 20f ? 11f : 13f, solid ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
    }.Skeletonized(false);   // a hover-only affordance is not skeleton content (the MediaCard.MoreCorner rule)

    // A one-line title / secondary line pair — the shape every tabular row shares. This helper and Sub() below are
    // where Home's rogue ramp actually lived: the prototype's per-row 13/17 + 11/14 was authored as the DEFAULT here, so
    // nearly every tabular row on the page inherited two sizes that exist nowhere in the engine ramp. Both now resolve to
    // real rungs — BodyStrong 14/20 over Caption 12/16 — and those two edits fix most of Home in one place.
    static Element TwoLine(string title, Element? second)
    {
        Element head = BodyStrong(title) with
        {
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = second is null ? [head] : [head, second],
        };
    }

    static TextEl Sub(string text) => Caption(text) with
    {
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>A description as RICH TEXT. Spotify blurbs are HTML fragments — `<a href=spotify:playlist:…>Tophyun</a>,
    /// … and more` — and Home rendered them through a plain TextEl, so the markup showed literally on the card.
    /// <see cref="RichText"/> is what every other description site in the app already uses: it drops the tags, keeps
    /// their text, and turns a routable anchor into an accent hyperlink that navigates on its own.</summary>
    static Element Desc(string? html, int maxLines, float size = 12f, Action<string>? onNavUri = null)
        => RichText.OfFlex(html, size, Tok.TextSecondary, Tok.AccentTextPrimary, maxLines, onNavUri);

    /// <summary>The navigate-on-anchor-click callback every rich description shares: route the href, or ignore it.</summary>
    internal static Action<string> NavUri(Action<string, string?> go)
        => u => { if (RichText.RouteForUri(u) is { } key) go(key, null); };

    internal static string Duration(long ms)
    {
        if (ms <= 0) return "";
        int totalMin = (int)Math.Round(ms / 60000d);
        int h = totalMin / 60, m = totalMin % 60;
        return h > 0 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
    }

    // `hrs()` in the prototype: "1.3 h" past the hour, "45 m" under it. Audiobook lengths only.
    internal static string Hours(long ms)
    {
        if (ms <= 0) return "";
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return ms >= 3600000
            ? (ms / 3600000d).ToString("0.0", c) + " h"
            : Math.Round(ms / 60000d).ToString("0", c) + " m";
    }

    // ── A · the daylist hero ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>The Daylist identity as a compact relative of the artist masthead: the complete square cover is integrated
    /// into the trailing material under the same semantic copy veil, never stretched into a banner or repeated as a
    /// separate thumbnail and ghost — unless Pathfinder authored a desktop <c>header_image_url</c>, in which case the
    /// surface becomes a full-bleed photo masthead (media → veil → copy) and the square cover is dropped.
    /// <para>The eyebrow carries the GREETING ("Good morning, Christos · your daylist"): the prototype has no standalone
    /// greeting block, because a page that opens with two stacked text blocks before any content wastes its best row.</para>
    /// <para>HomeHeroLayout flattens the former 16-DIP SectionBand inset into the copy padding, so the renderer and
    /// virtual estimator retain the same geometry while the artwork can touch the clipped surface. The pulse row
    /// (<see cref="FlipCountdown"/>) is reserved in that geometry for every hero; non-daylist mounts collapse it.</para></summary>
    public static Element HeroBand(HomeCard c, string eyebrow, string meta, Action onPlay, Action onShuffle,
                                   Action onNav, Action onLike, MenuAttach? menu, float width)
    {
        var accent = AccentOrChrome(c);
        var metrics = HomeHeroLayout.For(width);
        TextEl title = metrics.Tier switch
        {
            HomeHeroTier.Wide => WaveeType.ArtistTitle(c.Title),
            HomeHeroTier.Medium => WaveeType.ArtistCompactTitle(c.Title),
            _ => WaveeType.PageHero(c.Title),
        };

        // Pulse slot always present so ContentHeight's PulseBlock matches the children list; empty when no daylist window.
        Element pulse = c.Meta is { ExpiresAtMs: > 0 } m
            ? Embed.Comp(() => new FlipCountdown
              {
                  // Chrome fill — FlipCountdown contrast-grades it to TextInk so the digits stay the daylist hue
                  // without disappearing into the peach/yellow wash (the Play capsule keeps this fill as a plate).
                  ExpiresAtMs = m.ExpiresAtMs, Accent = () => accent, BottomMargin = Spacing.M,
              }) with { Key = c.Uri + ":" + m.ExpiresAtMs }
            : new BoxEl();

        var copy = new BoxEl
        {
            Direction = 1, Width = MathF.Max(1f, width - 2f * metrics.CopyPaddingX), Gap = 0f, MinWidth = 0f,
            Children =
            [
                // The prototype's `.hero-eyebrow { text-transform: uppercase }` is NOT honoured, and this is the site
                // that proves the rule: this string is "Good morning, {user} · your daylist" — localized copy carrying
                // the USER'S OWN DISPLAY NAME. Upper-casing it shouted a person's name back at them and mangled it in
                // any locale with casing rules Invariant does not model. The eyebrow's rung + weight + tracking already
                // make it read as a label; case never had to.
                WaveeType.Eyebrow(eyebrow) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                },
                title with
                {
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                },
                c.Meta?.Seeds is { Count: > 0 } seeds
                    ? new BoxEl
                    {
                        Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                        Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                        Children = [.. Tags(seeds, 6)],
                    }
                    : new BoxEl(),
                meta.Length > 0
                    // Body (14/20), not a bespoke 13/19. HomeHeroLayout.MetaBlock tracks this pair exactly.
                    ? Body(meta) with
                    {
                        Color = Tok.TextSecondary, MaxLines = 2,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        Margin = new Edges4(0f, 0f, 0f, Spacing.L),
                    }
                    : new BoxEl(),
                pulse,
                // `.hero-actions` — the app's ONE primary-action grammar: an accent Play capsule on the daylist's own
                // graded colour, a standard Shuffle capsule beside it, then the icon-only arm of the same capsule for
                // like and overflow. This used to be a private 32px/13px cluster whose own doc comment declared the
                // divergence ("deliberately NOT WaveeCta"), which is the definition of a second grammar: the hero of
                // the app's landing page was the one primary action that did not look like the app's primary action.
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        WaveeCta.Accent(Loc.Get(Strings.Home.Play), accent, onPlay) with { Shrink = 0f },
                        WaveeCta.Pill(Loc.Get(Strings.Detail.Shuffle), onShuffle, ButtonAppearance.Standard,
                            glyph: Icons.Shuffle) with { Shrink = 0f },
                        WaveeCta.Icon(Icons.Heart, onLike) with { Shrink = 0f },
                        // The "…" carries no handler of its own: ClickRequestsContext re-enters the engine's context
                        // funnel, which walks up and finds the band's attached menu — the same mechanism MediaCard's
                        // corner "…" uses, so the hero's overflow is the card's real menu rather than a second one.
                        menu is null ? new BoxEl()
                            : WaveeCta.Icon(Icons.More, null, requestsContext: true) with { Shrink = 0f },
                    ],
                }.Skeletonized(false),
            ],
        };

        var foreground = new BoxEl
        {
            Width = width, Height = metrics.Height,
            Direction = 1, AlignItems = FlexAlign.Start,
            Justify = metrics.Stacked ? FlexJustify.End : FlexJustify.Center,
            Padding = new Edges4(metrics.CopyPaddingX, metrics.CopyPaddingY,
                metrics.CopyPaddingX, metrics.CopyPaddingY),
            Children = [copy],
        };

        var veilAxis = metrics.Stacked
            ? Wavee.Features.Detail.ArtistHeroVeilAxis.Vertical
            : Wavee.Features.Detail.ArtistHeroVeilAxis.Horizontal;
        var veil = new BoxEl
        {
            Width = width, Height = metrics.Height, HitTestVisible = false,
            Gradient = Surfaces.ArtistHeroVeil(accent, veilAxis),
        };

        // Authored desktop header → full-bleed photo ground (no HomeHeroBackdrop, no trailing square cover).
        // Else: square cover + graded wash, unchanged.
        BoxEl surface;
        if (c.Meta?.HeaderImageUrl is { Length: > 0 } headerUrl)
        {
            int decodePx = Math.Clamp((int)MathF.Round(width), 320, 1920);
            float aspect = width / MathF.Max(1f, metrics.Height);
            var media = new BoxEl
            {
                Width = width, Height = metrics.Height,
                OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, HomeHeroLayout.ArtworkFade),
                Children =
                [
                    Image(headerUrl, ImageFit.Cover, aspect: aspect, decodePx: decodePx, corners: 0f,
                          placeholder: Surfaces.ArtworkPlaceholder,
                          transition: ImageTransition.Fade(MotionTok.StandardEnter.DurationMs)),
                ],
            };
            surface = new BoxEl
            {
                ZStack = true, MinWidth = 0f, Height = metrics.Height, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [media, veil, foreground],
            };
        }
        else
        {
            var artwork = new BoxEl
            {
                Width = metrics.ArtworkSize, Height = metrics.ArtworkSize, Shrink = 0f,
                AlignSelf = FlexAlign.Center,
                JustifySelf = metrics.Stacked ? FlexAlign.Center : FlexAlign.End,
                OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                EdgeFade = metrics.Stacked ? null : new EdgeFadeSpec(EdgeMask.Left, HomeHeroLayout.ArtworkFade),
                Children = [Art(c, metrics.ArtworkSize, metrics.ArtworkSize, 0f, decodePx: 512)],
            };
            surface = new BoxEl
            {
                ZStack = true, MinWidth = 0f, Height = metrics.Height, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Gradient = Surfaces.HomeHeroBackdrop(accent),
                Children = [artwork, veil, foreground],
            };
        }
        return menu is null ? surface : surface.WithMenu(menu);
    }

    static IEnumerable<Element> Tags(IReadOnlyList<string> seeds, int max)
    {
        int n = Math.Min(seeds.Count, max);
        for (int i = 0; i < n; i++) yield return Tag(seeds[i]);
    }

    // ── A2 · the weekly pair ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>`.wcard` — padding 16, art 56 r-ctrl, title in the display face at 18/24/600, 2-line description,
    /// trailing count, + spine.
    /// Discover Weekly and Release Radar are the only two playlists on home with a real editorial description worth two
    /// lines, so they get the room and sit as a deliberate 2-up.</summary>
    public static Element WeeklyCard(HomeCard c, Action onNav)
    {
        int count = c.Meta?.TrackCount ?? 0;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.L),
            Children =
            [
                Art(c, WaveeSize.Thumb56, WaveeSize.Thumb56, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        // 17/22 was a rung of its own. 18/24 at 600 is the nearest real step and keeps the card's
                        // two-line blurb budget unchanged.
                        Titled(BodyLarge(c.Title) with
                        {
                            Weight = 600, FontFamily = "Segoe UI Variable Display", CharSpacing = -8f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, c.Uri, 12f),
                        Caption(FirstSentence(c.Subtitle)) with
                        {
                            Color = Tok.TextSecondary,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                count > 0 ? Count(count.ToString(System.Globalization.CultureInfo.CurrentCulture)) : new BoxEl(),
            ],
        };
        return Card(body, onNav, Radii.Card, spine: c);
    }

    // The prototype takes only the first sentence of a weekly's blurb — the second sentence is always a restatement, and
    // two lines is the card's budget. STRIP FIRST: splitting a raw HTML fragment on the first '.' cuts inside
    // `spotify:playlist:…`. Split on ". " with a length guard, matching ArtistPage.Hero's FirstSentence.
    static string FirstSentence(string? html)
    {
        var plain = SpotifyExportMapper.ToPlainText(html);
        if (string.IsNullOrWhiteSpace(plain)) return "";
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return end > 20 ? plain[..(end + 1)] : plain;
    }

    // ── B · the jump-back-in tile ──────────────────────────────────────────────────────────────────────────────
    /// <summary>`.qtile` — height 56 (its cover exactly), r-ctrl, art 56 flush to the leading edge, a 2-line
    /// BodyStrong 14/20 title, hover play, + spine. The densest card in the vocabulary: four across, and the only text
    /// is the name.</summary>
    public static Element QuickTile(HomeCard c, Action onNav, Action onPlay)
    {
        var body = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Children =
            [
                // Flush left, square outer corners: the tile's own ClipToBounds rounds them to r-ctrl.
                Art(c, WaveeSize.Thumb56, WaveeSize.Thumb56, Radii.Control, decodePx: 128),
                BodyStrong(c.Title) with
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Margin = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
                // The now-playing mark sits BEFORE the hover play, and collapses to zero when this is not the sounding
                // context — so a tile that is playing says so, exactly as a MediaCard does.
                HomeNowPlaying.Mark(c.Uri, 12f),
                new BoxEl { Padding = new Edges4(0f, 0f, Spacing.M, 0f), Shrink = 0f, Children = [HoverPlay(onPlay)] },
            ],
        };
        // 56, not 58: the tile is exactly its own 56-DIP cover, so the art now sits flush instead of leaving a 2-DIP
        // sliver of card under it.
        return Card(body, onNav, Radii.Control, spine: c, height: WaveeSize.Thumb56);
    }

    // ── D · one cell of the daily-mix band ─────────────────────────────────────────────────────────────────────
    /// <summary>`.bseg` — padding 16, a leading divider, a display-face Title 28/36 numeral tinted with the mix's own
    /// colour, a Caption 12/16 caps label, the seed artists on a 3-line clamp, a full-bleed wash at opacity .09,
    /// and the spine. Six of these are ONE surface divided into cells, not six cards: the numeral carries the identity
    /// (it is what "Daily Mix 3" means) and the seeds are the only differentiator worth reading.
    /// <para><paramref name="ordinal"/> is 1-based and comes from the card's POSITION, never from parsing the title —
    /// "Daily Mix 3" is localized, and a parse would number the band wrongly in any other language.</para></summary>
    public static Element MixSegment(HomeCard c, int ordinal, Action onNav, bool leading, bool above)
    {
        var content = new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L),
            Children =
            [
                HomeAccentLeaf.Numeral(c, ordinal.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                Titled(WaveeType.Eyebrow(Loc.Get(Strings.Home.DailyMix)) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
                }, c.Uri, 10f),
                c.Meta?.Seeds is { Count: > 0 } seeds
                    ? Caption(string.Join(" · ", seeds)) with
                    {
                        Color = Tok.TextSecondary,
                        Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        // 2px of air under the last line so the colour spine never touches a descender.
                        Margin = new Edges4(0f, 0f, 0f, Spacing.XXS),
                    }
                    : new BoxEl(),
            ],
        };
        // No Grow/Basis: the cell is a GRID cell now, sized by its star track. Dividers are drawn inside it on both
        // axes — a leading rule on every cell but the first in its row, a top rule on every row but the first — because
        // a grid has no place to put a separator element between its tracks.
        var layers = new List<Element>(5)
        {
            // `.wash` — the mix's own colour at .09 across the whole cell, under the content.
            // Same ComponentEl-has-no-layout-props rule as Spine: the stretch lives on this box, not on the leaf.
            new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Children = [HomeAccentLeaf.Of(c, HomeAccentLeaf.Kind.Wash)],
            },
            content,
            Spine(c),
        };
        if (leading)
            layers.Add(new BoxEl
            {
                Width = 1f, JustifySelf = FlexAlign.Start, Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
            });
        if (above)
            layers.Add(new BoxEl
            {
                Height = 1f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch,
                Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
            });
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true,
            OnClick = onNav, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
            BrushTransitionMs = MotionTok.ControlFast.DurationMs,
            Children = [.. layers],
        };
    }

    // ── F · the chip card ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>`.ccard` — padding 12, r-ctrl, art 64, BodyStrong 14/20 title, a filled chip run, a "{n} songs" count, + spine. A
    /// mix whose description IS its seed list; the chips are the content, and each is a real entity name the mapper
    /// extracted from an anchor rather than guessed.</summary>
    public static Element ChipCard(HomeCard c, Action onNav, Action<string>? onNavUri = null)
    {
        int count = c.Meta?.TrackCount ?? 0;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, MinWidth = 0f, Grow = 1f, AlignItems = FlexAlign.Start,
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                Art(c, WaveeSize.Thumb64, WaveeSize.Thumb64, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Titled(BodyStrong(c.Title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, c.Uri),
                        c.Meta?.Seeds is { Count: > 0 } seeds
                            ? ChipRun(seeds, 3)
                            : Desc(c.Subtitle, 2, onNavUri: onNavUri),
                        count > 0 ? Count(Strings.Detail.SongCount(count)) : new BoxEl(),
                    ],
                },
            ],
        };
        return Card(body, onNav, Radii.Control, spine: c);
    }

    // ── G · the radio dial row ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>`.drow` — `32px 1fr auto`, height 48, ROUND art 32 (a station stands for an artist), a BodyStrong
    /// 14/20 name, seeds joined `", "`, hover play. The tightest row in the vocabulary because stations are the highest-count thing on
    /// home: twenty of them, and each is a name plus who seeded it.</summary>
    public static Element RadioRow(HomeCard c, Action onNav, Action onPlay)
    {
        // Seeds are already plain artist names (the mapper pulled them out of the anchors). The fallback is a raw
        // description, so it has to be flattened before it goes into a one-line caption.
        string seeds = c.Meta?.Seeds is { Count: > 0 } s
            ? string.Join(", ", s)
            : SpotifyExportMapper.ToPlainText(c.Subtitle) ?? "";
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                Art(c, WaveeSize.Thumb32, WaveeSize.Thumb32, Radii.Full, decodePx: 64),
                TwoLine(c.Title, Sub(seeds) with { Color = Tok.TextSecondary }),
                HomeNowPlaying.Mark(c.Uri, 11f),
                HoverPlay(onPlay, 24f, solid: false),
            ],
        };
        return Row(body, onNav, 48f);   // 48, not 46 — the station row lands on the 4-grid
    }

    // ── H1 · the episode queue row ─────────────────────────────────────────────────────────────────────────────
    /// <summary>`.qrow` — `auto 1fr auto`, padding 8, a bottom divider on all but the last, and ARTWORK THAT ENCODES
    /// THE MEDIUM: 56×32 (16:9) when the show ships video for this episode, 32×32 square otherwise. So the reader can see
    /// what they are about to get before the badge tells them. A partly-played episode carries a resume hairline across
    /// the bottom of its art — the only place progress can live without a second row of chrome.</summary>
    public static Element QueueRow(HomeCard c, Action onNav, bool last)
    {
        var m = c.Meta;
        bool video = m?.HasVideo == true;
        // 56, not 57: the 16:9 lane is now a real ladder rung (and 57 was one DIP off it for no reason at all).
        float artW = video ? WaveeSize.Thumb56 : WaveeSize.Thumb32;
        float progress = m is { DurationMs: > 0, ResumeMs: > 0 }
            ? Math.Clamp((float)m.ResumeMs / m.DurationMs, 0f, 1f) : 0f;

        Element art = new BoxEl
        {
            Width = artW, Height = WaveeSize.Thumb32, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Corners = Radii.ControlAll,
            Children = progress > 0f
                ? [Art(c, artW, WaveeSize.Thumb32, Radii.Control, decodePx: 64), ResumeHairline(artW, progress)]
                : [Art(c, artW, WaveeSize.Thumb32, Radii.Control, decodePx: 64)],
        };

        var showLine = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = video
                ? [Sub(Loc.Get(Strings.Home.Video)) with { Color = Tok.TextTertiary, Shrink = 0f },
                   Sub(c.Subtitle ?? "") with { Color = Tok.TextSecondary }]
                : [Sub(c.Subtitle ?? "") with { Color = Tok.TextSecondary }],
        };

        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.S),
            Children =
            [
                art,
                TwoLine(c.Title, showLine),
                // `.qtime` — "{n} min" over a state word, both Caption 12/16. Fixed width: proportional digits, no
                // tabular figures.
                new BoxEl
                {
                    Width = 56f, Shrink = 0f, Direction = 1, Gap = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Sub(Duration(m?.DurationMs ?? 0)) with { Color = Tok.TextSecondary },
                        Sub(Loc.Get(progress > 0f ? Strings.Home.Resume : Strings.Home.Unplayed))
                            with { Color = Tok.TextTertiary },
                    ],
                },
            ],
        };
        var row = Row(body, onNav);
        return last ? row : row with
        {
            // A bottom hairline instead of a gap: the queue is a table, and a divider is what makes it read as one.
            Children = [.. row.Children!, new BoxEl
            {
                AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
                Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
            }],
            ZStack = true,
        };
    }

    static Element ResumeHairline(float width, float progress) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 0, HitTestVisible = false,
        Children = [new BoxEl { Width = MathF.Max(2f, width * progress), Height = 2f, Fill = Tok.AccentDefault }],
    };

    // ── H2 · the audiobook row ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>`.brow` — `48px 1fr auto`, padding 8, art 48 r-ctrl, and a rating cluster: the stock read-only
    /// <see cref="RatingControl"/> star strip, the value to 2dp, and the length in a fixed cell. Two decimals because
    /// that is the precision the server sends (4.57, not 4.5), and a five-star strip cannot express it — the strip says
    /// "this is a rating", the numeral says which one.
    /// <para>THE METER IS GONE. It was <c>ProgressBar.Determinate(rating / 5)</c>: a control whose entire meaning is
    /// "how far through a task are we", pressed into service as a rating gauge in a list where the row NEXT to it uses
    /// a real progress hairline for real resume progress. The old comment rejected the stock control on allocation
    /// grounds — "a virtualized list of these would allocate one signal per row realization" — and that was measured
    /// against a workload this surface does not have: <c>HomeModuleLayout.BooksShown</c> caps the shelf at a handful of
    /// rows (expandable to the group, still bounded), the signal is allocated ONCE per mount inside the factory closure
    /// (not per render), and the neighbouring art tile already mounts a whole <c>CoverShimmer</c> component per cover.
    /// Read-only mode also parks the strip on the PLACEHOLDER brush (TextPrimary, not accent), so restoring the real
    /// control costs the accent budget nothing.</para></summary>
    public static Element BookRow(HomeCard c, Action onNav)
    {
        var m = c.Meta;
        Element rating = m is { Rating: > 0 }
            ? new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    RatingControl.Create(placeholder: (float)m.Rating, readOnly: true),
                    Caption(m.Rating.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture)) with
                    {
                        Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    },
                    Count(Hours(m.DurationMs), WaveeSize.Thumb32),
                ],
            }
            : Count(Hours(m?.DurationMs ?? 0), WaveeSize.Thumb32);

        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.S),
            Children =
            [
                Art(c, WaveeSize.Thumb48, WaveeSize.Thumb48, Radii.Control, decodePx: 128),
                TwoLine(c.Title, Sub(m?.Author ?? c.Subtitle ?? "") with { Color = Tok.TextSecondary }),
                HomeNowPlaying.Mark(c.Uri, 11f),
                rating,
            ],
        };
        return Row(body, onNav);
    }

    // ── J · the editorial feature + its compact companions ─────────────────────────────────────────────────────
    /// <summary>`.feature` — padding 20, gap 20, art 148 r-card, an "Editorial" tag, a display-face Subtitle 20/28 title, the blurb, and a
    /// footer row pinned to the bottom (Play + "{n} songs · by {owner}"), + spine. The one editorial card that gets to
    /// state its case; its three companions are <see cref="CrowdRow"/>.</summary>
    public static Element FeatureCard(HomeCard c, string meta, Action onNav, Action onPlay,
                                      Action<string>? onNavUri = null)
    {
        var accent = AccentOrChrome(c);
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.XL, MinWidth = 0f, Grow = 1f, AlignItems = FlexAlign.Start,
            Padding = Edges4.All(Spacing.XL),
            Children =
            [
                // A 148-DIP cover is a CARD-scale surface, so it takes the card radius rung, not the control one.
                Art(c, 148f, 148f, Radii.Card, decodePx: 256),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        // `.tag` — accent-tinted outline, unlike the neutral `.tag2`. It names the module's voice.
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, Shrink = 0f,
                            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                            Corners = Radii.ControlAll,
                            BorderWidth = 1f, BorderColor = ColorF.Lerp(Tok.StrokeControlDefault, accent, 0.40f),
                            Children =
                            [
                                // AccentDecor (see the accent-roles section in WaveeTokens): accent as CONTENT colour,
                                // naming the module's voice. Deliberate and kept — only the case and the tracking moved.
                                WaveeType.Eyebrow(Loc.Get(Strings.Home.Editorial)) with
                                {
                                    Color = WaveeAccent.Decor, MaxLines = 1,
                                },
                            ],
                        },
                        Titled(Subtitle(c.Title) with
                        {
                            FontFamily = "Segoe UI Variable Display", CharSpacing = -12f,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, c.Uri, 13f),
                        // 12, not 13: RichText only pins an explicit 16-DIP line height at or below 12, so the nearest
                        // ramp step DOWN is the one that keeps this blurb on the vertical rhythm.
                        Desc(c.Subtitle, 3, onNavUri: onNavUri),
                        // Pinned to the bottom of the card, so the feature's height is set by its art and the footer
                        // always sits on the same line as the companion column's last row.
                        new BoxEl { Grow = 1f, MinHeight = 0f },
                        new BoxEl
                        {
                            Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Padding = new Edges4(0f, Spacing.M, 0f, 0f),
                            Children = [WaveeCta.Play(accent, onPlay, Loc.Get(Strings.Home.Play)), Count(meta)],
                        }.Skeletonized(false),
                    ],
                },
            ],
        };
        return Card(body, onNav, Radii.Card, spine: c);
    }

    /// <summary>`.crowd` — padding 12, art 48, BodyStrong 14/20 title, one-line blurb, a hover-revealed ghost play, + spine.
    /// The feature's companions: same voice, a third of the height, and they stretch to fill the column beside it.</summary>
    public static Element CrowdRow(HomeCard c, Action onNav, Action onPlay, Action<string>? onNavUri = null)
    {
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                // 52 sat exactly between two ladder rungs; the tie breaks DOWN to 48 because 48 + the row's 2x12
                // padding reproduces the old 72-DIP companion row, which is what keeps three of them level with the
                // feature card beside them.
                Art(c, WaveeSize.Thumb48, WaveeSize.Thumb48, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(c.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Desc(c.Subtitle, 1, onNavUri: onNavUri),
                    ],
                },
                HoverPlay(onPlay, 30f, solid: false),
            ],
        };
        return Card(body, onNav, Radii.Control, spine: c) with { Grow = 1f };
    }

    // ── K · the discover feed card ─────────────────────────────────────────────────────────────────────────────
    /// <summary>`.fcard` — `auto 1fr`, padding 16, art 64 r-ctrl, a caps accent REASON, BodyStrong 14/20 title, 2-line blurb, a meta
    /// line pinned to the bottom, + spine. The reason is the whole point: these arrive as ~20 separate single-item
    /// sections whose titles ("For fans of IU", "Based on your recent listening") are the only explanation of why the
    /// thing is being suggested, and the old composer discarded every one of them.</summary>
    public static Element FeedCard(HomeCard c, string meta, Action onNav, Action<string>? onNavUri = null)
    {
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, MinWidth = 0f, Grow = 1f, AlignItems = FlexAlign.Start,
            Padding = Edges4.All(Spacing.L),
            Children =
            [
                Art(c, WaveeSize.Thumb64, WaveeSize.Thumb64, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        c.Eyebrow is { Length: > 0 } e
                            ? WaveeType.Eyebrow(e) with
                            {
                                Color = WaveeAccent.Decor,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            }
                            : new BoxEl(),
                        Titled(BodyStrong(c.Title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }, c.Uri),
                        Desc(c.Subtitle, 2, onNavUri: onNavUri),
                        new BoxEl { Grow = 1f, MinHeight = 0f },
                        meta.Length > 0 ? Sub(meta) with { Color = Tok.TextTertiary, Margin = new Edges4(0f, Spacing.XS, 0f, 0f) } : new BoxEl(),
                    ],
                },
            ],
        };
        return Card(body, onNav, Radii.Control, spine: c);
    }

    // ── I · the what's-new timeline row ────────────────────────────────────────────────────────────────────────
    /// <summary>`.tlrow` — padding 8 with a 16 leading inset, a 7px pip straddling the day column's rule at `left:-4px`, art 40, a
    /// bordered kind tag, and a trailing "New" pill or a "Seen" count. The pip on a rule is what makes a list of
    /// releases read as a chronology rather than another shelf: filled accent for unread, hollow once seen.
    /// <para>Deliberately takes PLAIN FIELDS rather than a notification record: the timeline carries two sources (the
    /// what's-new feed and the Spotify category's concert announcements) through this one anatomy, and the row must not
    /// learn to type-test. <paramref name="artRadius"/> is the only shape that varies — an act's avatar is round where a
    /// sleeve is square, exactly as the notification center draws the same two rows.</para></summary>
    public static Element TimelineRow(string id, string? imageUrl, string title, string kindLabel, string meta,
                                      bool unread, Action onNav, float artRadius = 0f)
    {
        var cover = imageUrl is { Length: > 0 } url ? new Image(url) : null;
        float corners = artRadius > 0f ? artRadius : Radii.Control;
        var body = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.S, Spacing.S),
            Children =
            [
                Surfaces.Artwork(cover, SpotifyExportMapper.Hash(id), WaveeSize.Thumb40, WaveeSize.Thumb40,
                    corners, decodePx: 64),
                new BoxEl
                {
                    Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children = [KindTag(kindLabel), Sub(meta) with { Color = Tok.TextSecondary }],
                        },
                    ],
                },
                unread ? NewPill() : Count(Loc.Get(Strings.Home.Seen)),
            ],
        };
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Children =
            [
                Row(body, onNav),
                // The pip straddles the day column's rule — hence the negative inset and the row's 18px leading padding.
                new BoxEl
                {
                    // A 7-DIP dot with a 1.5-DIP ring: decorative punctuation on a rule, deliberately BELOW the
                    // spacing grid's smallest rung — an 8-DIP pip with a 2-DIP ring reads as a bullet, not a pin.
                    Width = 7f, Height = 7f, Shrink = 0f,
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(-4f, 0f, 0f, 0f),
                    Corners = Radii.Circle(7f),
                    Fill = unread ? Tok.AccentDefault : Tok.FillLayerDefault,
                    BorderWidth = 1.5f,
                    BorderColor = unread ? Tok.AccentDefault : Tok.StrokeControlStrongDefault,
                    HitTestVisible = false,
                },
            ],
        };
    }

    /// <summary>`.kindtag` — a bordered eyebrow type label ("Releases" / "Podcast" / "Concert").</summary>
    static Element KindTag(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children =
        [
            WaveeType.Eyebrow(text) with { Color = Tok.TextTertiary, MaxLines = 1 },
        ],
    };

    /// <summary>`.newpill` — a solid accent eyebrow badge. The one place on home an accent PLATE appears behind text.</summary>
    static Element NewPill() => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
        Children =
        [
            WaveeType.Eyebrow(Loc.Get(Strings.Home.NewBadge)) with
            {
                Color = Tok.TextOnAccentPrimary, MaxLines = 1,
            },
        ],
    };

    // ── E · the top-artist podium tile ─────────────────────────────────────────────────────────────────────────
    /// <summary>`.pod` — a column of [rank pill, round art `--a`, a centred 2-line Caption 12/16 label] at width `max(a+8, 60)`,
    /// with the art at 76/60/46 by rank and a 3px accent ring when selected.
    /// <para>The tile reserves <paramref name="slotHeight"/> for its art regardless of its own size, so every label in
    /// the strip lands on ONE line. The prototype gets that from `align-items:flex-end` on a wrapping flex row; our flex
    /// engine does not reproduce bottom-alignment under wrap, and the result was a staircase of labels. Reserving the
    /// slot makes the alignment structural instead of depending on the container.</para></summary>
    public static Element RankedAvatar(RelatedArtist a, int rank, bool selected, float artSize, float slotHeight,
                                       Action onSelect)
    {
        float w = MathF.Max(artSize + Spacing.S, 60f);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center, Width = w,
            Padding = new Edges4(Spacing.XXS, Spacing.S, Spacing.XXS, Spacing.S),
            Corners = Radii.ControlAll,
            OnClick = onSelect, Cursor = CursorId.Hand, Role = AutomationRole.Tab,
            Children =
            [
                // The reserved slot: art bottom-aligned inside a box as tall as the LARGEST avatar in the strip.
                new BoxEl
                {
                    Height = slotHeight, Width = w, ZStack = true,
                    AlignItems = FlexAlign.End, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = artSize, Height = artSize, ZStack = true,
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center,
                            Corners = Radii.Circle(artSize),
                            BorderWidth = selected ? 3f : 0f, BorderColor = Tok.AccentDefault,
                            // BorderWidth has NO layout effect in this engine, so the ring would paint over the art's
                            // outer 3px. Inset the artwork by hand to get WinUI's border-insets-child behaviour.
                            Padding = selected ? new Edges4(3f, 3f, 3f, 3f) : default,
                            Children =
                            [
                                Surfaces.Artwork(a.Image, SpotifyExportMapper.Hash(a.Uri),
                                    selected ? artSize - 6f : artSize, selected ? artSize - 6f : artSize,
                                    Radii.Full, decodePx: 128),
                                // `.rk` — the rank plate on the ARTWORK's leading corner, which is why it lives inside the
                                // art box and not beside it in the slot: the slot is as tall as the LARGEST avatar (so
                                // every name lands on one baseline), so a pill anchored to the slot floated 30px above a
                                // 46px avatar in its own band. Anchored to the art, it rides each avatar's own top-left.
                                //
                                // Width is explicit because a ZStack stretches its AUTO-sized children: MinWidth = 16 did
                                // not stop it, and the plate came out as a column-wide capsule. (Two earlier bugs, for the
                                // record: Edges4 is positional (L,T,R,B) so `Edges4(0,0,artSize,0)` was a RIGHT margin that
                                // collapsed the box to ~8px, and Tok.FillLayerAlt is byte-identical to FillCardDefault in
                                // dark — an invisible plate on the card it sits on, opaque white in light.)
                                new BoxEl
                                {
                                    Width = rank >= 10 ? 28f : 20f, Height = Spacing.XL,
                                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    Corners = Radii.Circle(Spacing.XL),
                                    Fill = selected ? Tok.AccentDefault : Tok.FillControlSolid,
                                    BorderWidth = selected ? 0f : 1f, BorderColor = Tok.StrokeCardDefault,
                                    HitTestVisible = false,
                                    Children =
                                    [
                                        // Caption at 600 — the same rung WaveeType.Eyebrow resolves, read straight off
                                        // the factory because a rank numeral is a VALUE, not an eyebrow.
                                        Caption(rank.ToString(System.Globalization.CultureInfo.CurrentCulture)) with
                                        {
                                            Weight = 600, MaxLines = 1,
                                            Color = selected ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
                Caption(a.Name) with
                {
                    Weight = 600, Color = Tok.TextPrimary,
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    MaxWidth = w, MinWidth = 0f,
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>Play counts and listener counts run to nine figures; the full number is noise at caption size. Culture-aware
    /// on the truncated value, so a Dutch build reads its own decimal separator.</summary>
    internal static string CompactNumber(long n)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.#", c) + "B"
             : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#", c) + "M"
             : n >= 1_000 ? (n / 1_000d).ToString("0.#", c) + "K"
             : n.ToString("N0", c);
    }
}

/// <summary>The now-playing equalizer for a Home card — the same three-bar mark every <c>MediaCard</c> carries, so a
/// playlist that is playing reads as playing on Home too. Animates while playback is running and freezes (bars held) when
/// it is paused, which is what distinguishes "this is the open context" from "this is the sounding context".
///
/// <para>A COMPONENT rather than an inline signal read, for the reason <c>NowPlayingOverlay</c> documents at length:
/// reading the hot <c>Identity</c> signal inside a card body would re-render EVERY visible card on any track skip. The
/// effect below bridges those hot signals into one coarse <c>(here, playing)</c> pair whose setter suppresses on
/// equality, so an unrelated skip re-runs one cheap comparison per card and schedules no render at all. It also reads the
/// coarse <c>HasActiveContext</c> first and bails before touching <c>Identity</c>, so an idle page never joins that
/// fanout.</para></summary>
static class HomeNowPlaying
{
    internal static Element Mark(string uri, float height = 12f)
        => Embed.Comp(new Props(uri, height), () => new Host());

    sealed record Props(string Uri, float Height);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<Props>();
            var bridge = UseContext(PlaybackBridge.Slot);
            var vis = UseSignal((here: false, playing: false));

            UseSignalEffect(() =>
            {
                if (p is null || bridge is not { } b || !b.HasActiveContext.Value) { vis.Value = (false, false); return; }
                var id = b.Identity.Value;
                bool here = NowPlayingMatch.RelatesToPlaying(p.Uri, id.ContextUri, id.Track);
                vis.Value = (here, here && b.IsPlaying.Value);
            });

            var (here, playing) = vis.Value;
            if (!here) return new BoxEl { Width = 0f, Height = 0f };

            return new BoxEl
            {
                Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [WaveeEqualizer.Of(playing, static () => Tok.AccentTextPrimary, p!.Height)],
            };
        }
    }
}

/// <summary>The leaf that resolves a card's identity colour and paints with it — the spine hairline, the mix cell's wash,
/// the mix cell's numeral.
///
/// <para>A component, and a LEAF one, for two reasons. It must subscribe: the colour usually is not known at first
/// render (a store-backed card has no <c>extractedColors</c>, so the grading is fetched on demand), and without a
/// subscription the hairline would stay blank until something unrelated re-rendered the card. And it must subscribe
/// NARROWLY: <c>CoverColorPlane.Watch(image)</c> fires only when THIS cover is graded, where the plane-wide
/// <c>Epoch</c> would re-render every shelf on Home each time a scrolling grid finished another batch.</para>
///
/// <para>Nothing is painted until the colour exists. There is no fallback tone — see <c>HomeCards.Accent</c>.</para></summary>
static class HomeAccentLeaf
{
    internal enum Kind { Spine, Wash, Numeral }

    internal static Element Of(HomeCard c, Kind kind)
        => Embed.Comp(new Props(c, kind, null), () => new Host());

    internal static Element Numeral(HomeCard c, string text)
        => Embed.Comp(new Props(c, Kind.Numeral, text), () => new Host());

    sealed record Props(HomeCard Card, Kind Which, string? Text);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<Props>();
            if (p is null) return new BoxEl();

            // Reading the watch signal IS the subscription; the plane bumps it once, when this image lands.
            _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Card.Image?.Url).Value;
            var accent = HomeCards.Accent(p.Card);

            switch (p.Which)
            {
                case Kind.Spine:
                    // Grow, not AlignSelf/JustifySelf: the wrapper box owns the ZStack placement (a component cannot).
                    return new BoxEl
                    {
                        Grow = 1f, Height = 2f, HitTestVisible = false,
                        Fill = HomeCards.SpineAccent(p.Card) ?? ColorF.Transparent,
                        HoverFill = HomeCards.SpineAccent(p.Card, hovered: true) ?? ColorF.Transparent,
                    };

                case Kind.Wash:
                    return new BoxEl
                    {
                        Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
                        Fill = accent is { } wash ? wash with { A = 0.09f } : ColorF.Transparent,
                    };

                default:
                    // The numeral keeps its slot whether or not the colour has landed — it is content, not decoration, so
                    // it renders in the primary ink until the grading arrives rather than popping in.
                    // Ui.Title IS 28/36 — the old override kept the same 36-DIP line box and only pushed the glyph
                    // size two steps off the ramp, so dropping it changes the numeral's weight of presence, not the
                    // cell's height.
                    return Ui.Title(p.Text ?? "") with
                    {
                        FontFamily = "Segoe UI Variable Display", CharSpacing = -40f,
                        Color = accent ?? Tok.TextPrimary, MaxLines = 1, Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
                    };
            }
        }
    }
}
