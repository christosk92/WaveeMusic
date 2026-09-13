using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The twelve MINI-ARTS the player-style flyout paints on its thumbnail cards — one 3-to-5 box composition per
/// preset, transliterated from the mockup's <c>.m-*</c> CSS (<c>docs/plans/wavee/npv-player-styles-mica.html</c>,
/// the <c>.mini</c> block). Each is a fixed-size <c>ZStack</c> plate with absolutely-placed layers: a ZStack child's
/// <c>JustifySelf</c>/<c>AlignSelf</c> picks the anchor edge and its leading <c>Margin</c> is the offset, so
/// <c>left:8%; top:18%</c> becomes <c>At(.08s, .18s, …)</c> and <c>right:10%</c> becomes <c>AtRight(.10s, …)</c>.
///
/// <para>These are ICONS, not decks: every colour is a literal from the mockup (the CSS has no theme tokens inside a
/// mini either — only the plate does, and that is <see cref="Tok.FillCardSecondary"/> here, standing in for the
/// mockup's <c>--deck-bg</c>). Nothing here animates, nothing reads playback, nothing binds a signal — a flyout that
/// paints twelve live decks would run twelve tickers to preview twelve tickers.</para>
///
/// <para>Two CSS constructs have no engine analogue and are approximated deliberately: a <c>conic-gradient</c> (the CD
/// rainbow, the marble swatch) becomes a multi-stop LINEAR gradient, and a <c>clip-path</c> skyline (Winamp / WMP
/// analysers) becomes a row of literal bars whose heights trace the same polygon.</para>
/// </summary>
static class NpvThumbnails
{
    /// <summary>The mini-art for <paramref name="presetId"/> (an <see cref="NpvPlayerCatalog"/> id), drawn
    /// <paramref name="size"/> square. An unknown id falls back to the default preset's art, so a stored-but-retired
    /// id can never paint a hole.</summary>
    public static Element For(int presetId, float size) => presetId switch
    {
        NpvPlayerCatalog.Cassette => Cassette(size),
        NpvPlayerCatalog.Reel => Reel(size),
        NpvPlayerCatalog.Cd => Cd(size),
        NpvPlayerCatalog.Turntable => Turntable(size),
        NpvPlayerCatalog.Ipod => Ipod(size),
        NpvPlayerCatalog.Winamp => Winamp(size),
        NpvPlayerCatalog.Vu => Vu(size),
        NpvPlayerCatalog.Zune => Zune(size),
        NpvPlayerCatalog.Wmp => Wmp(size),
        NpvPlayerCatalog.Canvas => Canvas(size),
        NpvPlayerCatalog.Picture => Picture(size),
        _ => Record(size),
    };

    // ── the twelve ────────────────────────────────────────────────────────────────────────────────────────────────

    // .m-record — pale tilted sleeve at the back, black disc with a pale label over it.
    static Element Record(float s) => Plate(s,
    [
        At(.08f * s, .18f * s, .50f * s, .50f * s) with { Fill = Hex(0xBCD6FF), Corners = CornerRadius4.All(2f), Rotation = -3f },
        Disc(AtRight(.10f * s, .22f * s, .56f * s, .56f * s), .56f * s, Hex(0x111111), Hex(0xBCD6FF)),
    ]);

    // .m-cassette — cream label over a slate shell, two white-ringed hubs.
    static Element Cassette(float s) => Plate(s, Hex(0x2B2F3A),
    [
        At(.10f * s, .24f * s, .80f * s, .52f * s) with { Fill = Hex(0xE9E2D0), Corners = CornerRadius4.All(3f) },
        Hub(At(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
        Hub(AtRight(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
    ]);

    // .m-reel — two aluminium flanges with a brown tape pack showing through the rim.
    static Element Reel(float s) => Plate(s, Hex(0x22252B),
    [
        Flange(At(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
        Flange(AtRight(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
    ]);

    // .m-cd — rainbow disc with the plate showing through the hole (conic → 5-stop diagonal linear).
    static Element Cd(float s)
    {
        float d = .64f * s;
        return Plate(s,
        [
            At(.18f * s, .18f * s, d, d) with
            {
                Corners = Radii.Circle(d), ZStack = true,
                Gradient = Ui.LinearGradient(45f,
                    new GradientStop(0f, Hex(0xFF99CC)), new GradientStop(.25f, Hex(0x99CCFF)),
                    new GradientStop(.5f, Hex(0xCCFFCC)), new GradientStop(.75f, Hex(0xFFFFCC)),
                    new GradientStop(1f, Hex(0xFF99CC))),
                Children = [Centered(d * .32f, Tok.FillCardSecondary)],
            },
        ]);
    }

    // .m-turntable — wood plinth, black platter inside a grey ring, pale label.
    static Element Turntable(float s) => Plate(s,
        Ui.GradientDown(new GradientStop(0f, Hex(0x6B4A2E)), new GradientStop(1f, Hex(0x4A301B))),
    [
        Disc(AtRight(.08f * s, .22f * s, .60f * s, .60f * s), .60f * s, Hex(0x111111), Hex(0xBCD6FF)) with
        {
            BorderWidth = 3f, BorderColor = Hex(0x3A3D44),
        },
    ]);

    // .m-ipod — silver body, dark LCD, white click wheel.
    static Element Ipod(float s) => Plate(s, Hex(0x3A3F4A),
    [
        At(.24f * s, .06f * s, .52f * s, .88f * s) with
        {
            Corners = CornerRadius4.All(6f),
            Gradient = Ui.GradientDown(new GradientStop(0f, Hex(0xF4F4F6)), new GradientStop(1f, Hex(0xC9CCD3))),
        },
        At(.32f * s, .12f * s, .36f * s, .30f * s) with { Fill = Hex(0x1D2A3A), Corners = CornerRadius4.All(2f) },
        At(.33f * s, .50f * s, .34f * s, .34f * s) with
        {
            Corners = Radii.Circle(.34f * s), Fill = Hex(0xFFFFFF), BorderWidth = 1f, BorderColor = Hex(0xAAAAAA),
        },
    ]);

    // .m-winamp — dark frame, a title band with its lit top edge, a black LCD chip, a green analyser skyline.
    static Element Winamp(float s) => Plate(s, Hex(0x232A37),
    [
        At(.08f * s, .14f * s, .84f * s, .30f * s) with { Fill = Hex(0x3B4459) },
        At(.08f * s, .14f * s, .84f * s, 3f) with { Fill = Hex(0x6E7A97) },
        At(.14f * s, .20f * s, .34f * s, .16f * s) with { Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x00FF00) },
        Skyline(.08f * s, .14f * s, .84f * s, .36f * s, Hex(0x00FF00), [.40f, .60f, .20f, .55f, .95f, .45f, .70f, 1f]),
    ]);

    // .m-vu — two ivory meter faces over an amber-ruled LCD strip.
    static Element Vu(float s)
    {
        float d = .38f * s;
        var face = new CornerRadius4(6f, 6f, d * .5f, d * .5f);
        return Plate(s, Hex(0x15161A),
        [
            At(.08f * s, .22f * s, d, d) with { Fill = Hex(0xEFE6CF), Corners = face },
            AtRight(.08f * s, .22f * s, d, d) with { Fill = Hex(0xEFE6CF), Corners = face },
            AtBottom(.08f * s, .14f * s, .84f * s, .14f * s) with
            {
                Fill = Hex(0x1A0D00), BorderWidth = 1f, BorderColor = Hex(0xFFB000),
            },
        ]);
    }

    // .m-zune — black slab, disc with a white centre, a pink accent tab.
    static Element Zune(float s) => Plate(s, Hex(0x000000),
    [
        Disc(At(.10f * s, .24f * s, .52f * s, .52f * s), .52f * s, Hex(0x111111), Hex(0xFFFFFF)),
        AtRight(.08f * s, .10f * s, .28f * s, 6f) with { Fill = Hex(0xF0568C) },
    ]);

    // .m-wmp — near-black, an accent-coloured analyser skyline. The one mini that reads a THEME colour, because the
    // WMP deck's default palette is the app accent (the preset's "colour: accent | from cover" option row).
    static Element Wmp(float s) => Plate(s, Hex(0x04060C),
    [
        Skyline(.08f * s, .14f * s, .84f * s, .60f * s, Prop.Of(() => Tok.AccentDefault),
            [.55f, .25f, .70f, .45f, .95f, .35f, .60f, .30f, .75f, .40f]),
    ]);

    // .m-canvas — a soft blue wash with a tilted pale card floating on it.
    static Element Canvas(float s) => Plate(s,
        Ui.RadialGradient(new Point2(.40f, .40f), new Point2(.85f, .85f),
            new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.55f, Hex(0xB9D0FB)), new GradientStop(1f, Hex(0x7F9BE0))),
    [
        At(.14f * s, .14f * s, .72f * s, .72f * s) with { Fill = Hex(0xE9F1FF), Corners = CornerRadius4.All(3f), Rotation = 3f },
    ]);

    // .m-picture — a picture disc: no label, the art IS the record.
    static Element Picture(float s)
    {
        float d = .64f * s;
        return Plate(s,
        [
            AtRight(.14f * s, .18f * s, d, d) with
            {
                Corners = Radii.Circle(d),
                Gradient = Ui.RadialGradient(new Point2(.50f, .40f), new Point2(.75f, .75f),
                    new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.70f, Hex(0xBCD6FF)), new GradientStop(1f, Hex(0x9FBEF0))),
            },
        ]);
    }

    // ── plate + placement helpers ─────────────────────────────────────────────────────────────────────────────────

    // The .mini box itself: a square, clipped, rounded ZStack. HitTestVisible=false throughout — the thumbnail is
    // decoration inside a card that owns the click, and a layer that swallowed the press would make the card dead in
    // its own middle.
    static BoxEl Plate(float s, Element[] kids) => Plate(s, Tok.FillCardSecondary, kids);

    static BoxEl Plate(float s, Prop<ColorF> fill, Element[] kids) => new()
    {
        Width = s, Height = s, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Corners = CornerRadius4.All(4f), Fill = fill, Children = kids,
    };

    static BoxEl Plate(float s, GradientSpec gradient, Element[] kids) => new()
    {
        Width = s, Height = s, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Corners = CornerRadius4.All(4f), Gradient = gradient, Children = kids,
    };

    /// <summary>CSS <c>left:x; top:y</c> — a ZStack layer anchored to the plate's top-left.</summary>
    static BoxEl At(float x, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, y, 0f, 0f),
    };

    /// <summary>CSS <c>right:r; top:y</c> — anchored to the plate's top-RIGHT.</summary>
    static BoxEl AtRight(float r, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, y, r, 0f),
    };

    /// <summary>CSS <c>left:x; bottom:b</c> — anchored to the plate's bottom-left.</summary>
    static BoxEl AtBottom(float x, float b, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, 0f, 0f, b),
    };

    // .m-record i / .m-turntable i / .m-zune i — a circular body with the ::after label pinned dead centre at 32% of
    // the diameter (the CSS inset:34% leaves exactly that).
    static BoxEl Disc(BoxEl box, float d, ColorF body, ColorF label) => box with
    {
        Corners = Radii.Circle(d), Fill = body, ZStack = true, Children = [Centered(d * .32f, label)],
    };

    static BoxEl Centered(float d, Prop<ColorF> fill) => new()
    {
        Width = d, Height = d, Shrink = 0f, HitTestVisible = false, Corners = Radii.Circle(d), Fill = fill,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    // .m-cassette i — a dark hub inside a white ring (CSS inset box-shadow ⇒ an inside border here).
    static BoxEl Hub(BoxEl box, float d) => box with
    {
        Corners = Radii.Circle(d), Fill = Hex(0x222222), BorderWidth = 2f, BorderColor = Hex(0xFFFFFF),
    };

    // .m-reel i — an aluminium flange with the brown pack showing as the inner ring.
    static BoxEl Flange(BoxEl box, float d) => box with
    {
        Corners = Radii.Circle(d), Fill = Hex(0xC8CCD3), BorderWidth = 4f, BorderColor = Hex(0x3A2A1A),
    };

    // The clip-path skyline (Winamp / WMP): literal bars whose heights trace the same polygon, bottom-aligned.
    static BoxEl Skyline(float x, float bottom, float w, float h, Prop<ColorF> ink, float[] heights)
    {
        int n = heights.Length;
        float gap = w / (n * 3f);
        float bw = MathF.Max(1f, (w - gap * (n - 1)) / n);
        var bars = new Element[n];
        for (int i = 0; i < n; i++)
            bars[i] = new BoxEl
            {
                Width = bw, Height = MathF.Max(2f, h * heights[i]), Shrink = 0f, HitTestVisible = false, Fill = ink,
            };
        return AtBottom(x, bottom, w, h) with { Direction = 0, Gap = gap, AlignItems = FlexAlign.End, Children = bars };
    }

    static ColorF Hex(uint rgb) => ColorF.FromRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
