using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Shared concert compositions built exclusively from existing FluentGpu primitives. None of these factories
/// owns routing or playback; callers provide the one navigation action explicitly.</summary>
public static class ConcertUi
{
    // Quiet Fluent date identity (user-reviewed ConcertStub look): a layered NEUTRAL fill with a small accent month and a
    // primary day numeral. Accent is small emphasis only — never a large saturated block.
    public static Element DateBlock(DateTimeOffset date, bool compact = false)
    {
        var culture = CultureInfo.CurrentCulture;
        float width = compact ? 48f : 56f;
        float height = compact ? 52f : 60f;
        string day = date.Day.ToString(culture);
        string month = date.ToString("MMM", culture);

        return new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, Gap = 1f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                WaveeType.Eyebrow(month) with { Color = WaveeAccent.Decor, MaxLines = 1 },
                BodyStrong(day) with { Color = Tok.TextPrimary, MaxLines = 1 },
            ],
        };
    }

    // The artist-schedule row (component-allocation table): a clean list line — date column, venue (primary), city + an
    // optional "Near you" pill (secondary), chevron. On an artist-owned surface the concert title is usually the artist
    // name repeated, so venue leads. Every text column is Grow/Basis/MinWidth-clamped with CharacterEllipsis so nothing
    // wraps mid-word in a cramped column.
    public static Element ScheduleRow(Concert concert, Action onClick, bool wide = true, bool showNearPill = true)
    {
        string venue = string.IsNullOrWhiteSpace(concert.Venue)
            ? (string.IsNullOrWhiteSpace(concert.Title) ? "Concert" : concert.Title!)
            : concert.Venue;
        string city = CityLine(concert);
        bool pill = showNearPill && concert.IsNearUser;

        var lines = new List<Element>(2)
        {
            BodyStrong(venue) with
            {
                Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (city.Length > 0 || pill)
        {
            var secondary = new List<Element>(2);
            if (city.Length > 0)
                // Grow to fill AND Shrink (weight = content width, since Basis is left at its measured content size — a
                // Basis=0 item has zero shrink weight and CANNOT give space back, so under a "Near you" pill it collapses
                // to x=0 and the pill paints over it). MinWidth 0 lets it ellipsize to exactly the leftover width.
                secondary.Add(Body(city) with
                {
                    Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                });
            if (pill) secondary.Add(StatusPill("Near you"));
            lines.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = secondary.ToArray(),
            });
        }

        return new BoxEl
        {
            Key = concert.Uri,
            Direction = 0, MinHeight = wide ? 76f : 84f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                DateBlock(concert.Date, compact: !wide),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children = lines.ToArray(),
                },
                Icon(Icons.ChevronRight, 16f, Tok.TextSecondary) with { Shrink = 0f },
            ],
        };
    }

    // The vertical event card (hub shelves + grid, detail related shelf). Date leads as an accent CAPTION above the
    // title — never a chip over the artwork. A cover-cropped square fills the rounded top (card ClipToBounds); a
    // concert with no artwork shows a softly accent-tinted pane with the neutral DateBlock centred instead. Every text
    // column is MinWidth-clamped + CharacterEllipsis on ONE line, so nothing ever paints outside its box.
    public static Element EventTile(Concert concert, Action onClick)
    {
        string title = string.IsNullOrWhiteSpace(concert.Title) ? Loc.Get(Strings.Concerts.Detail.Concert) : concert.Title!;
        string place = PlaceLine(concert);

        var text = new List<Element>(3)
        {
            // AccentDecor - the concert date caption is the deliberate accent identity; kept, on the eyebrow rung.
            WaveeType.Eyebrow(DateCaption(concert.Date)) with
            {
                Color = WaveeAccent.Decor, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            WaveeType.TrackTitle(title) with
            {
                MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (place.Length > 0)
            text.Add(WaveeType.TrackMeta(place) with
            {
                MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        return new BoxEl
        {
            Key = concert.Uri,
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                CardArtwork(concert),
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = 3f,
                    Children = text.ToArray(),
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    // Compatibility while the schedule checkpoint lands; Hub/Detail migrate to EventTile in the second pass.
    public static Element VerticalCard(Concert concert, Action onClick) => EventTile(concert, onClick);

    /// <summary>The related-shelf's lead navigation cell (Spotify's "Browse more concerts" idea in the Wavee voice):
    /// the SAME tile footprint as <see cref="EventTile"/> — square pane + caption/title column — but the pane is a quiet
    /// layered surface of faint scattered date glyphs instead of artwork, and the tap routes to the Concert Hub.</summary>
    public static Element BrowseAllCard(string caption, string title, Action onClick)
    {
        ColorF baseFill = Tok.Theme == ThemeKind.Dark ? ColorF.FromRgba(0x1C, 0x1C, 0x1E) : Tok.FillCardSecondary;
        ColorF glyph = Tok.TextSecondary with { A = 0.30f };
        return new BoxEl
        {
            Key = "browse-all-concerts",
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Card),
                    Children =
                    [
                        new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Placeholder = baseFill },
                        new BoxEl
                        {
                            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
                            AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
                            Children = [ Icon(Icons.Calendar, 30f, glyph) ],
                        },
                        new BoxEl
                        {
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [ Icon(Icons.MapPin, 44f, glyph) ],
                        },
                        new BoxEl
                        {
                            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
                            AlignItems = FlexAlign.End, Justify = FlexJustify.End,
                            Children = [ Icon(Icons.Calendar, 22f, glyph) ],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = 3f,
                    Children =
                    [
                        WaveeType.Eyebrow(caption) with
                        {
                            Color = WaveeAccent.Decor, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        WaveeType.TrackTitle(title) with
                        {
                            MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    // The card's rounded-top media: a fluid square cover (fills the engine-laid-out card width, derives its height via
    // aspect-ratio 1), or — with no artwork — a softly accent-tinted pane carrying the neutral DateBlock.
    static Element CardArtwork(Concert concert)
    {
        if (concert.Image is { Url.Length: > 0 })
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                Children = [ Surfaces.ArtworkFill(concert.Image, corners: Radii.Card, decodePx: 320) ],
            };

        ColorF baseFill = Tok.Theme == ThemeKind.Dark ? ColorF.FromRgba(0x1C, 0x1C, 0x1E) : Tok.FillCardSecondary;
        ColorF pane = concert.AccentColor is { } argb
            ? ColorF.Lerp(baseFill, WaveePalette.ToColor(argb), 0.32f)
            : baseFill;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card),
            Children =
            [
                new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Placeholder = pane },
                new BoxEl
                {
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [ Icon(Icons.Calendar, 38f, Tok.TextSecondary with { A = 0.72f }) ],
                },
            ],
        };
    }

    /// <summary>A single elevated editorial surface shared by schedule/detail/featured-event heroes. Copy always lives
    /// on the grounded card surface; artwork is an adjacent clipped pane with only a short blend at the seam. Wide mode
    /// is a 56/44 row, narrow mode stacks a 180-DIP media pane above the copy. Missing media remains intentional.</summary>
    public static Element SplitEditorialHero(Image? artwork, uint? accent, Element copy, bool wide,
        string? fallbackGlyph = null)
    {
        var metrics = ConcertLayout.EditorialHero(wide);
        bool dark = Tok.Theme == ThemeKind.Dark;
        ColorF baseFill = dark ? ColorF.FromRgba(0x1B, 0x1B, 0x1D) : Tok.FillCardSecondary;
        ColorF mediaFill = accent is { } argb
            ? ColorF.Lerp(baseFill, WaveePalette.ToColor(argb), dark ? 0.30f : 0.18f)
            : baseFill;

        Element media = SplitHeroMedia(artwork, mediaFill, fallbackGlyph ?? Icons.Calendar, wide, metrics.MediaHeight);
        Element groundedCopy = new BoxEl
        {
            Direction = 1, Grow = wide ? 0.56f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
            Padding = new Edges4(metrics.Padding, metrics.Padding, metrics.Padding, metrics.Padding),
            Justify = FlexJustify.Center,
            Children = [ copy ],
        };

        return new BoxEl
        {
            Direction = (byte)(wide ? 0 : 1), MinWidth = 0f,
            Height = wide ? metrics.Height : float.NaN,
            ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,

            Children = wide ? [ groundedCopy, media ] : [ media, groundedCopy ],
        };
    }

    static Element SplitHeroMedia(Image? artwork, ColorF fill, string fallbackGlyph, bool wide, float height)
    {
        var layers = new List<Element>(2);
        if (artwork?.Url is { Length: > 0 } url)
        {
            layers.Add(new ImageEl
            {
                // The provider ships no focal point, and these banners are far wider than the pane — a centred Cover
                // window regularly decapitates the subject. Bias the crop to the upper third, where faces live.
                Source = url, Fit = ImageFit.Cover, FocusY = 0.30f, DecodePx = 1024f, Placeholder = fill,
                RevealTransition = ImageTransition.Fade(220f),
            });
        }
        else
        {
            layers.Add(new BoxEl
            {
                Fill = fill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ Icon(fallbackGlyph, 52f, Tok.TextSecondary with { A = 0.72f }) ],
            });
        }

        layers.Add(new BoxEl
        {
            HitTestPassThrough = true,
            Gradient = wide
                ? LinearGradient(0f,
                    new GradientStop(0f, Tok.FillCardDefault),
                    new GradientStop(0.22f, Tok.FillCardDefault with { A = 0f }))
                : GradientDown(
                    new GradientStop(0.68f, Tok.FillCardDefault with { A = 0f }),
                    new GradientStop(1f, Tok.FillCardDefault)),
        });

        return new BoxEl
        {
            Grow = wide ? 0.44f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
            Height = height, ZStack = true, ClipToBounds = true,
            Children = layers.ToArray(),
        };
    }

    // "Fri, Aug 21 · 19:00" (current culture) — the accent eyebrow voice the pages already use, in the format's OWN
    // casing. The old .ToUpper(culture) was a caps transform over a CULTURE-FORMATTED date, i.e. over localized text.
    static string DateCaption(DateTimeOffset date)
    {
        var culture = CultureInfo.CurrentCulture;
        return date.ToString("ddd, MMM d", culture) + " · " + date.ToString("t", culture);
    }

    // A full-width page hero, restructured (R3.1 — the copy comes OFF the photo): the image is a pure ATMOSPHERE BAND —
    // cover-cropped, its bottom dissolving completely into the page ground via the EdgeFade on the media box, so the
    // photo AND the low-alpha extracted-colour wash layered inside it fade together. NO text, NO buttons, NO black
    // legibility scrim ever sits on the photo (the old white-ink-over-arbitrary-photo + black-scrim-into-white-page
    // combination was the root of the "disconnected" feel). The copy (accent eyebrow + PageHero title + optional stats
    // line) renders BELOW the band in normal theme ink — legible in both themes with zero scrim — and `trailing` (the
    // schedule page's location control) rides that title row as standard page chrome. The band's faded lower third means
    // plain adjacency at zero gap already reads as the copy sitting in the dissolve; no negative-margin overlap is used.
    // Callers gate on a non-null image and keep their plain header as the fallback — which is now this same copy block
    // minus the band (deliberate cohesion).
    public static Element Hero(Image image, string eyebrow, string title, uint? accent, Element? trailing = null,
        Element? stats = null) =>
        Responsive.Of(width => BuildHero(image, eyebrow, title, accent, trailing, stats, width > 0f ? width : 900f),
            fallback: 900f);

    static Element BuildHero(Image image, string eyebrow, string title, uint? accent, Element? trailing, Element? stats, float width)
    {
        const float bandH = 192f;
        bool dark = Tok.Theme == ThemeKind.Dark;
        ColorF baseFill = dark ? ColorF.FromRgba(0x14, 0x14, 0x16) : Tok.FillCardSecondary;
        // The pull toward the event's accent, per theme. It was 0.5 in BOTH, which is the usual light-arm mistake: half
        // a saturated wire hue mixed into a near-black plate is a dark tint, and half of the same hue mixed into
        // #F6F6F6 is a full-strength pastel band 192 DIP tall behind the title. Light pulls a third as far — enough to
        // say "this event has a colour", not enough to become the page.
        float pull = dark ? 0.5f : 0.18f;
        ColorF wash = accent is { } argb ? ColorF.Lerp(baseFill, WaveePalette.ToColor(argb), pull) : baseFill;

        Element photo = image.Url is { Length: > 0 } url
            ? Ui.Image(url, ImageFit.Cover, aspect: width / bandH, decodePx: width, corners: 0f,
                placeholder: wash, blurHash: image.BlurHash)
            : new BoxEl { Width = width, Height = bandH, Fill = wash };

        var bandLayers = new List<Element>(2) { photo };
        // The extracted colour carried toward the page seam (low alpha, bottom only) — INSIDE the fading box, so the
        // tint dissolves with the photo instead of outliving it as a hard edge.
        if (accent is { } tintArgb)
        {
            ColorF tint = WaveePalette.ToColor(tintArgb);
            bandLayers.Add(new BoxEl
            {
                Width = width, Height = bandH, HitTestPassThrough = true,
                Gradient = GradientDown(
                    new GradientStop(0.45f, tint with { A = 0f }),
                    new GradientStop(1f, tint with { A = dark ? 0.24f : 0.14f })),
            });
        }
        Element band = new BoxEl
        {
            Width = width, Height = bandH, ZStack = true, ClipToBounds = true,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 112f),
            Corners = CornerRadius4.All(Radii.Card),
            Children = bandLayers.ToArray(),
        };

        // The copy, off the photo: the plain-header voice (accent eyebrow, primary title, secondary stats), so
        // hero and no-hero pages read as the same surface.
        var copyLines = new List<Element>(3)
        {
            WaveeType.Eyebrow(eyebrow) with
            {
                Color = WaveeAccent.Decor, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            WaveeType.PageHero(title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
        };
        if (stats is not null) copyLines.Add(stats);
        Element copy = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
            Children = copyLines.ToArray(),
        };
        Element titleRow = trailing is null
            ? copy
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.M, MinWidth = 0f,
                Children = [ copy, new BoxEl { Width = 260f, Shrink = 0f, Children = [ trailing ] } ],
            };

        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = 0f,   // tight: the title row sits directly against the band's faded zone
            Children = [ band, titleRow ],
        };
    }

    // The hero tour-stats line ("N shows · M cities · MMM – MMM yyyy") — secondary theme ink under the title now that
    // the copy lives on the page instead of the photo. Kept on ONE ellipsized line. (No tabular-numerals seam exists on
    // TextEl today, so the figures use the body font's default digits.)
    public static Element HeroStatsLine(string text) => Body(text) with
    {
        Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // ── next-show spotlight ─────────────────────────────────────────────────────────────────────────────────────────
    // The first upcoming concert, rendered prominently right under the title row. A larger date block, a
    // "NEXT SHOW · IN {relative}" accent caption, venue (primary), "City · HH:mm" meta, and a trailing accent "Tickets"
    // button — BOTH the card and the button navigate to the detail route (offers live on the detail page; the schedule
    // response carries no offer data, so none is invented here). `accent` is the artist/cover extracted colour: lerped
    // faintly over the card fill (the CardArtwork idiom) it ties the card to the artist instead of floating detached.
    public static Element SpotlightCard(Concert concert, DateTimeOffset now, Action onOpen, uint? accent = null)
    {
        var culture = CultureInfo.CurrentCulture;
        // Venue-less events (the ArtistConcerts norm) lead with the CITY, never the title (usually the artist's own
        // name repeated); the meta then carries only the time so the city is not printed twice.
        bool cityPrimary = string.IsNullOrWhiteSpace(concert.Venue) && !string.IsNullOrWhiteSpace(concert.City);
        string venue = !string.IsNullOrWhiteSpace(concert.Venue) ? concert.Venue
            : cityPrimary ? concert.City
            : string.IsNullOrWhiteSpace(concert.Title) ? Loc.Get(Strings.Concerts.Detail.Concert) : concert.Title!;
        string meta = cityPrimary ? concert.Date.ToString("t", culture) : TimeMeta(concert);
        string caption = Strings.Concerts.Schedule.NextShow(ConcertScheduleShaping.RelativeTime(concert.Date, now));
        ColorF fill = accent is { } argb
            ? ColorF.Lerp(Tok.FillCardDefault, WaveePalette.ToColor(argb), 0.11f)
            : Tok.FillCardDefault;

        var copy = new List<Element>(3)
        {
            WaveeType.Eyebrow(caption) with
            {
                Color = WaveeAccent.Decor, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            WaveeType.TrackTitle(venue) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (meta.Length > 0)
            copy.Add(WaveeType.TrackMeta(meta) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Key = "spotlight:" + concert.Uri,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = fill, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onOpen,
            Children =
            [
                BigDateBlock(concert.Date),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 3f, Justify = FlexJustify.Center,
                    Children = copy.ToArray(),
                },
                Button.Accent("Tickets", onOpen) with { Shrink = 0f },
            ],
        };
    }

    // The spotlight's oversized date identity: accent month caption / big day numeral / weekday abbreviation.
    public static Element BigDateBlock(DateTimeOffset date)
    {
        var c = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = 84f, Height = 92f, Shrink = 0f, Gap = 2f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                WaveeType.Eyebrow(date.ToString("MMM", c)) with { Color = WaveeAccent.Decor, MaxLines = 1 },
                new TextEl(date.Day.ToString(c)) { Size = 34f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1 },
                WaveeType.Eyebrow(date.ToString("ddd", c)) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };
    }

    // "City · 19:00" (locale short time) — the spotlight/tile secondary meta. Time only when the city is empty.
    public static string TimeMeta(Concert concert)
    {
        string time = concert.Date.ToString("t", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(concert.City) ? time : concert.City + " · " + time;
    }

    // Returns the concrete BoxEl so an anchored caller can attach `with { OnRealized = … }` to capture its node.
    public static BoxEl LocationButton(string label, Action onClick) => new BoxEl
    {
        Direction = 0, Grow = 1f, Basis = 0f, MinHeight = WaveeSize.ControlH, MinWidth = 0f,
        AlignSelf = FlexAlign.Stretch,
        AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
        Corners = CornerRadius4.All(Radii.Control),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Width = 20f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ Icon(Icons.MapPin, 16f, Tok.TextSecondary) ],
            },
            BodyStrong(label) with
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Width = 16f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ Icon(Icons.ChevronDown, 10f, Tok.TextSecondary) ],
            },
        ],
    };

    /// <summary>A compact Fluent filter token — a MULTI-SELECT query facet, which is precisely why it is not a
    /// <c>SelectorBar</c>: a SelectorBar has exactly ONE selected item, and this strip routinely has none or five.
    /// Selected is a filled accent pill (AccentSelection, the sanctioned role); unselected is a bordered control surface.
    /// <para>NO LABEL SHIFT. The selected arm used to INSERT a 14-DIP check plus a 6-DIP gap ahead of the label, so
    /// selecting a token slid its own text sideways and reflowed every token to its right — the strip visibly walked
    /// under the cursor. The filled accent plate and the on-accent ink already say "selected" (and the ToggleButton role
    /// says it to a screen reader), so the redundant glyph is gone and a token is now the same width in both
    /// states.</para></summary>
    public static BoxEl FilterToken(string label, bool selected, Action onClick) => new BoxEl
    {
        Key = "filter-token:" + label,
        Animate = new LayoutTransition(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(220f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width),
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(14f, 5f, 14f, 5f),
        Corners = CornerRadius4.All(Radii.Full),
        Fill = selected ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
        PressedFill = selected ? Tok.AccentTertiary : Tok.FillControlTertiary,
        BorderWidth = 1f,
        BorderColor = selected ? Tok.AccentDefault : Tok.StrokeControlDefault,
        BrushTransitionMs = WaveeMotion.Fast,
        Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [ Body(label) with { Color = selected ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1 } ],
    };

    // ── hub filter-bar pills (concerts v2, rev-7 segmented-pill fusion) ──────────────────────────────────────────────

    /// <summary>The hub's "where" pill — a bordered control-surface pill with a MapPin lead, the location label, and a
    /// chevron. Returns the concrete <see cref="BoxEl"/> so the caller can attach <c>with { OnRealized = … }</c> to
    /// capture the flyout anchor node (the LocationButton idiom).</summary>
    public static BoxEl WherePill(string label, Action onClick) => new BoxEl
    {
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(12f, 5f, 12f, 5f),
        Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.MapPin, 14f, Tok.TextSecondary) with { Shrink = 0f },
            BodyStrong(label) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Icon(Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The when-area REST pill (no date chosen): a neutral bordered pill — Calendar, "Dates", chevron. Keyed
    /// <c>"when-pill"</c> (the SAME key as <see cref="SegmentedDatePill"/>) with the width-reflow recipe so the node is
    /// REUSED and its width animates when it fuses into the active segmented state. Returns <see cref="BoxEl"/> so the
    /// caller can attach the anchor capture.</summary>
    public static BoxEl RestDatePill(Action onClick) => new BoxEl
    {
        Key = "when-pill",
        Animate = new LayoutTransition(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(260f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width),
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(12f, 5f, 12f, 5f),
        Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.Calendar, 14f, Tok.TextSecondary) with { Shrink = 0f },
            Body(Loc.Get(Strings.Concerts.Filter.Dates)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            Icon(Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The FUSED when-pill: an accent surface carrying a raised inner "segment" capsule (the chip's visual
    /// survivor, <c>⟨✓ This weekend⟩</c>) followed by the range text (<c>Jul 17 – 19</c>) + chevron. Outer rides the
    /// reused <c>"when-pill"</c> node so its width change animates via the FilterToken width-reflow recipe; the segment
    /// ENTERS from the chip's direction (right) as the chip EXITS toward the pill (left) — the two legs overlap and read
    /// as the dock. Returns <see cref="BoxEl"/> so the caller can attach the anchor capture.</summary>
    public static BoxEl SegmentedDatePill(string name, string rangeText, Action onClick)
        => SegmentedPill(name, rangeText, onClick);

    /// <summary>The FUSED two-segment pill: a docked capsule carrying the chosen facet's name, then its value, then a
    /// chevron to reopen. This is the visual survivor of a loose chip that has flown into the pill — the second half
    /// of the Concerts filter grammar, generalised so the home facet row speaks the same language.
    ///
    /// <paramref name="name"/> is the facet ("Music", "This weekend"); <paramref name="valueText"/> is what it
    /// resolved to ("Following", a date range).</summary>
    public static BoxEl SegmentedPill(string name, string valueText, Action onClick) => new BoxEl
    {
        Key = "when-pill",
        Animate = new LayoutTransition(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(260f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width),
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 8f,
        Padding = new Edges4(3f, 3f, 12f, 3f),
        Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,

        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl   // the docked segment — raised card capsule, the chip's visual survivor
            {
                Key = "when-seg",
                Animate = new LayoutTransition(
                    TransitionChannels.Position | TransitionChannels.Opacity,
                    TransitionDynamics.Tween(300f, Easing.SmoothOut),
                    Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true)),   // arrives FROM the chip's side
                Direction = 0, Height = 26f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 5f,
                Padding = new Edges4(11f, 0f, 11f, 0f), Corners = CornerRadius4.All(13f),
                Fill = Tok.FillCardDefault, Shadow = Elevation.Flyout,
                Children =
                [
                    Icon(Icons.Check, 12f, Tok.AccentTextPrimary) with { Shrink = 0f },
                    Body(name) with { Color = Tok.AccentTextPrimary, Weight = 600, MaxLines = 1 },
                ],
            },
            Body(valueText) with { Color = Tok.TextOnAccentPrimary, Weight = 600, MaxLines = 1 },
            Icon(Icons.ChevronDown, 10f, Tok.TextOnAccentPrimary) with { Shrink = 0f },
        ],
    };

    /// <summary>A dashed-outline "more/less" affordance for the genre-token strip — an accent LABEL over a NEUTRAL
    /// dashed border (the engine's <c>BorderDashOn/Off</c> stroke, the DropZone look). Toggles the strip between the
    /// capped top-3 head and the full concept list.
    /// <para>The border and the chevron used to be accent too. A border is STRUCTURE and a chevron is a disclosure
    /// glyph — hard rule 2 of the accent budget (see <c>WaveeAccent</c>): accent names content and actions, never
    /// chrome. The label keeps its accent, because THAT is the affordance.</para></summary>
    public static Element MoreToken(string label, bool expanded, Action onClick) => new BoxEl
    {
        Key = "genre-more",
        Animate = new LayoutTransition(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(220f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width),
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(14f, 5f, 14f, 5f),
        Corners = CornerRadius4.All(Radii.Full),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BorderDashOn = 5f, BorderDashOff = 4f,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Body(label) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
            Icon(expanded ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    public static Element WideEditorialDestination(
        Image? artwork,
        string eyebrow,
        string title,
        string subtitle,
        string actionLabel,
        Action onClick,
        float fallbackWidth = 1000f) =>
        Responsive.Of(width => BuildWideEditorial(
            artwork, eyebrow, title, subtitle, actionLabel, onClick,
            width > 0f ? width : fallbackWidth), fallback: fallbackWidth);

    static Element BuildWideEditorial(
        Image? artwork,
        string eyebrow,
        string title,
        string subtitle,
        string actionLabel,
        Action onClick,
        float width)
    {
        var metrics = ConcertLayout.WideEditorial(width);
        float artWidth = metrics.ArtworkWidth(width);
        float copyWidth = MathF.Max(180f, width - artWidth + MathF.Min(96f, artWidth * 0.42f));
        Element art = artwork?.Url is { Length: > 0 } url
            ? new ImageEl
            {
                Source = url, Width = artWidth, Height = metrics.Height, Fit = ImageFit.Cover,
                Placeholder = Tok.FillCardSecondary,
            }
            : new BoxEl { Width = artWidth, Height = metrics.Height, Fill = Tok.FillCardSecondary };

        return new BoxEl
        {
            Height = metrics.Height, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.End,
                    Children = [ new BoxEl { Grow = 1f }, art ],
                },
                new BoxEl
                {
                    Gradient = LinearGradient(0f,
                        new GradientStop(0f, Tok.FillCardDefault),
                        new GradientStop(0.52f, Tok.FillCardDefault),
                        new GradientStop(1f, Tok.FillCardDefault with { A = 0f })),
                },
                new BoxEl
                {
                    Direction = 1, Width = copyWidth, Padding = new Edges4(metrics.Padding, metrics.Padding, metrics.Padding, metrics.Padding),
                    Gap = Spacing.S, Justify = FlexJustify.Center,
                    Children =
                    [
                        WaveeType.Eyebrow(eyebrow) with
                        {
                            Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        WaveeType.PageHero(title) with
                        {
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                        },
                        Body(subtitle) with
                        {
                            Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = metrics.SubtitleLines,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                            Children =
                            [
                                BodyStrong(actionLabel) with { Color = WaveeAccent.Decor },
                                // The chevron is chrome, not the action - accent stops at the label (budget rule 2).
                                Icon(Icons.ChevronRight, 14f, Tok.TextSecondary),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // Shrink 0 so a growing/ellipsizing sibling (the city line) yields the space instead — the pill keeps its size and
    // is placed AFTER the city, never over it.
    static Element StatusPill(string text) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Shrink = 0f, Padding = new Edges4(Spacing.S, 2f, Spacing.S, 2f),
        Corners = CornerRadius4.All(Radii.Full), Fill = Tok.AccentSubtle,
        Children = [ Caption(text) with { Color = Tok.AccentTextPrimary, MaxLines = 1 } ],
    };

    static string PlaceLine(Concert concert)
    {
        if (concert.Venue.Length == 0) return concert.City;
        if (concert.City.Length == 0) return concert.Venue;
        return concert.Venue + " · " + concert.City;
    }

    // City with its region (or country) appended when it adds information — the schedule row's secondary line.
    static string CityLine(Concert concert)
    {
        string city = concert.City;
        string? extra = !string.IsNullOrWhiteSpace(concert.Region) ? concert.Region : concert.Country;
        if (city.Length == 0) return extra ?? "";
        return extra is { Length: > 0 } e && !string.Equals(e, city, StringComparison.OrdinalIgnoreCase)
            ? city + ", " + e
            : city;
    }
}

/// <summary>Presentational location-picker content for an anchored overlay. Query/results/loading/error are live signals,
/// so the panel mounts once per open and never depends on changing constructor values.</summary>
public sealed class ConcertLocationPickerPanel : Component
{
    public required Signal<string> Query;
    public required IReadSignal<IReadOnlyList<ConcertPlace>> Results;
    public required IReadSignal<bool> Loading;
    public required IReadSignal<string?> Error;
    public required Action<ConcertPlace> Select;
    public required Action UseMyLocation;
    public string SearchPlaceholder = Loc.Get(Strings.Concerts.Location.SearchCities);
    public string UseMyLocationLabel = Loc.Get(Strings.Concerts.Location.UseMine);
    public string NoResultsLabel = Loc.Get(Strings.Concerts.Location.NoneFound);

    public override Element Render()
    {
        var results = Results.Value;
        bool loading = Loading.Value;
        string? error = Error.Value;
        var rows = new Element[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            var place = results[i];
            rows[i] = LocationRow(place, () => Select(place));
        }

        Element body = rows.Length > 0
            ? new ScrollEl
            {
                ContentSized = true, MaxHeight = 248f,
                Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows },
            }
            : new BoxEl
            {
                Height = 48f, AlignItems = FlexAlign.Center,
                Children = [ WaveeType.TrackMeta(loading ? Loc.Get(Strings.Concerts.Location.Searching) : NoResultsLabel) ],
            };

        var children = new List<Element>(5)
        {
            Embed.Comp(() => new EditableText
            {
                Placeholder = SearchPlaceholder, Width = 340f, Height = WaveeSize.ControlH, Text = Query,
            }),
            Button.Standard(loading ? Loc.Get(Strings.Concerts.Location.Locating) : UseMyLocationLabel,
                UseMyLocation, isEnabled: !loading),
        };
        if (!string.IsNullOrWhiteSpace(error))
            children.Add(Body(error!) with { Color = Tok.SystemFillCritical, Wrap = TextWrap.Wrap, MaxLines = 3 });
        children.Add(body);

        return new BoxEl
        {
            Direction = 1, Width = 360f, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
            Children = children.ToArray(),
        };
    }

    static Element LocationRow(ConcertPlace place, Action onClick)
    {
        string detail = !string.IsNullOrWhiteSpace(place.Region) && !string.IsNullOrWhiteSpace(place.Country)
            ? place.Region + " - " + place.Country
            : place.Region ?? place.Country ?? "";
        var text = detail.Length > 0
            ? new Element[] { WaveeType.TrackTitle(place.Name), WaveeType.TrackMeta(detail) }
            : new Element[] { WaveeType.TrackTitle(place.Name) };
        return new BoxEl
        {
            Key = place.Id, Direction = 0, MinHeight = 48f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                Icon(Icons.MapPin, 18f, Tok.TextSecondary),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f, Children = text },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
