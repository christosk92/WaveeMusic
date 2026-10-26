// ── Themes/Classic/Structure/Rules/DeckThumbnails.cs — the twelve mini-arts of the deck style flyout (themes plan 6.1, T6b) ──
//
// From `Shell/Rail.Styles.UI.cs` (`NpvThumbnails.cs`, verbatim geometry). The mini-arts are ICONS, not decks: literal colours
// transliterated from the mockup's `.m-*` CSS, nothing animates, nothing binds a signal (WMP's analyser is the one theme read: its
// deck's default palette is the accent). Every layer is `HitTestVisible = false`: the card owns the click. They are DATA drawn as
// pictures, so they live with the rules (WVTH003 allows literal looks only here): `Views\Panel\ClassicDeckFlyout.cs` calls
// `ClassicDeckThumbnails.Thumbnail`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Themes.Classic;

/// <summary>The mini-arts of the deck style flyout.</summary>
internal static class ClassicDeckThumbnails
{
    // ── the twelve mini-arts (NpvThumbnails.cs, verbatim geometry) ───────────────────────────────────────────────────

    /// <summary>The mini-art for a preset id, drawn <paramref name="s"/> square. An unknown id paints the default preset's
    /// art, so a retired id can never paint a hole. Every layer is <c>HitTestVisible = false</c>: the card owns the click.</summary>
    public static Element Thumbnail(int presetId, float s) => presetId switch
    {
        Rail.PlayerCatalog.Cassette => Plate(s, Hex(0x2B2F3A),
        [
            At(.10f * s, .24f * s, .80f * s, .52f * s) with { Fill = Hex(0xE9E2D0), Corners = CornerRadius4.All(3f) },
            Hub(At(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
            Hub(AtRight(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
        ]),
        Rail.PlayerCatalog.Reel => Plate(s, Hex(0x22252B),
        [
            Flange(At(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
            Flange(AtRight(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
        ]),
        Rail.PlayerCatalog.Cd => Plate(s, Tok.FillCardSecondary,
        [
            At(.18f * s, .18f * s, .64f * s, .64f * s) with
            {
                Corners = Radii.Circle(.64f * s), ZStack = true,
                Gradient = Ui.LinearGradient(45f,
                    new GradientStop(0f, Hex(0xFF99CC)), new GradientStop(.25f, Hex(0x99CCFF)), new GradientStop(.5f, Hex(0xCCFFCC)),
                    new GradientStop(.75f, Hex(0xFFFFCC)), new GradientStop(1f, Hex(0xFF99CC))),
                Children = [Centered(.64f * s * .32f, Tok.FillCardSecondary)],
            },
        ]),
        Rail.PlayerCatalog.Turntable => Plate(s, Ui.GradientDown(new GradientStop(0f, Hex(0x6B4A2E)), new GradientStop(1f, Hex(0x4A301B))),
        [
            Disc(AtRight(.08f * s, .22f * s, .60f * s, .60f * s), .60f * s, Hex(0x111111), Hex(0xBCD6FF)) with { BorderWidth = 3f, BorderColor = Hex(0x3A3D44) },
        ]),
        Rail.PlayerCatalog.Ipod => Plate(s, Hex(0x3A3F4A),
        [
            At(.24f * s, .06f * s, .52f * s, .88f * s) with
            {
                Corners = CornerRadius4.All(6f),
                Gradient = Ui.GradientDown(new GradientStop(0f, Hex(0xF4F4F6)), new GradientStop(1f, Hex(0xC9CCD3))),
            },
            At(.32f * s, .12f * s, .36f * s, .30f * s) with { Fill = Hex(0x1D2A3A), Corners = CornerRadius4.All(2f) },
            At(.33f * s, .50f * s, .34f * s, .34f * s) with { Corners = Radii.Circle(.34f * s), Fill = Hex(0xFFFFFF), BorderWidth = 1f, BorderColor = Hex(0xAAAAAA) },
        ]),
        Rail.PlayerCatalog.Winamp => Plate(s, Hex(0x232A37),
        [
            At(.08f * s, .14f * s, .84f * s, .30f * s) with { Fill = Hex(0x3B4459) },
            At(.08f * s, .14f * s, .84f * s, 3f) with { Fill = Hex(0x6E7A97) },
            At(.14f * s, .20f * s, .34f * s, .16f * s) with { Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x00FF00) },
            Skyline(.08f * s, .14f * s, .84f * s, .36f * s, Hex(0x00FF00), WinampBars),
        ]),
        Rail.PlayerCatalog.Vu => Plate(s, Hex(0x15161A),
        [
            At(.08f * s, .22f * s, .38f * s, .38f * s) with { Fill = Hex(0xEFE6CF), Corners = new CornerRadius4(6f, 6f, .19f * s, .19f * s) },
            AtRight(.08f * s, .22f * s, .38f * s, .38f * s) with { Fill = Hex(0xEFE6CF), Corners = new CornerRadius4(6f, 6f, .19f * s, .19f * s) },
            AtBottom(.08f * s, .14f * s, .84f * s, .14f * s) with { Fill = Hex(0x1A0D00), BorderWidth = 1f, BorderColor = Hex(0xFFB000) },
        ]),
        Rail.PlayerCatalog.Zune => Plate(s, Hex(0x000000),
        [
            Disc(At(.10f * s, .24f * s, .52f * s, .52f * s), .52f * s, Hex(0x111111), Hex(0xFFFFFF)),
            AtRight(.08f * s, .10f * s, .28f * s, 6f) with { Fill = Hex(0xF0568C) },
        ]),
        Rail.PlayerCatalog.Wmp => Plate(s, Hex(0x04060C),
        [
            Skyline(.08f * s, .14f * s, .84f * s, .60f * s, Prop.Of(static () => Tok.AccentDefault), WmpBars),
        ]),
        Rail.PlayerCatalog.Canvas => Plate(s,
            Ui.RadialGradient(new Point2(.40f, .40f), new Point2(.85f, .85f),
                new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.55f, Hex(0xB9D0FB)), new GradientStop(1f, Hex(0x7F9BE0))),
        [
            At(.14f * s, .14f * s, .72f * s, .72f * s) with { Fill = Hex(0xE9F1FF), Corners = CornerRadius4.All(3f), Rotation = 3f },
        ]),
        Rail.PlayerCatalog.Picture => Plate(s, Tok.FillCardSecondary,
        [
            AtRight(.14f * s, .18f * s, .64f * s, .64f * s) with
            {
                Corners = Radii.Circle(.64f * s),
                Gradient = Ui.RadialGradient(new Point2(.50f, .40f), new Point2(.75f, .75f),
                    new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.70f, Hex(0xBCD6FF)), new GradientStop(1f, Hex(0x9FBEF0))),
            },
        ]),
        // .m-record — a pale tilted sleeve at the back, a black disc with a pale label over it.
        _ => Plate(s, Tok.FillCardSecondary,
        [
            At(.08f * s, .18f * s, .50f * s, .50f * s) with { Fill = Hex(0xBCD6FF), Corners = CornerRadius4.All(2f), Rotation = -3f },
            Disc(AtRight(.10f * s, .22f * s, .56f * s, .56f * s), .56f * s, Hex(0x111111), Hex(0xBCD6FF)),
        ]),
    };

    static readonly float[] WinampBars = [.40f, .60f, .20f, .55f, .95f, .45f, .70f, 1f];
    static readonly float[] WmpBars = [.55f, .25f, .70f, .45f, .95f, .35f, .60f, .30f, .75f, .40f];

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

    /// <summary>CSS <c>left:x; top:y</c> — a ZStack layer anchored top-left; the leading Margin is the offset.</summary>
    static BoxEl At(float x, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, y, 0f, 0f),
    };

    /// <summary>CSS <c>right:r; top:y</c>.</summary>
    static BoxEl AtRight(float r, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, y, r, 0f),
    };

    /// <summary>CSS <c>left:x; bottom:b</c>.</summary>
    static BoxEl AtBottom(float x, float b, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, 0f, 0f, b),
    };

    /// <summary>A circular body with its label dead centre at 32 % of the diameter (the CSS inset:34%).</summary>
    static BoxEl Disc(BoxEl box, float d, ColorF body, ColorF label) => box with
    {
        Corners = Radii.Circle(d), Fill = body, ZStack = true, Children = [Centered(d * .32f, label)],
    };

    static BoxEl Centered(float d, Prop<ColorF> fill) => new()
    {
        Width = d, Height = d, Shrink = 0f, HitTestVisible = false, Corners = Radii.Circle(d), Fill = fill,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    /// <summary>A dark hub inside a white ring (the CSS inset box-shadow ⇒ an inside border).</summary>
    static BoxEl Hub(BoxEl box, float d) => box with { Corners = Radii.Circle(d), Fill = Hex(0x222222), BorderWidth = 2f, BorderColor = Hex(0xFFFFFF) };

    /// <summary>An aluminium flange with the brown pack showing as the inner ring.</summary>
    static BoxEl Flange(BoxEl box, float d) => box with { Corners = Radii.Circle(d), Fill = Hex(0xC8CCD3), BorderWidth = 4f, BorderColor = Hex(0x3A2A1A) };

    /// <summary>The clip-path skyline (Winamp / WMP): literal bottom-aligned bars tracing the same polygon.</summary>
    static BoxEl Skyline(float x, float bottom, float w, float h, Prop<ColorF> ink, float[] heights)
    {
        int n = heights.Length;
        float gap = w / (n * 3f);
        float bw = MathF.Max(1f, (w - gap * (n - 1)) / n);
        var bars = new Element[n];
        for (int i = 0; i < n; i++)
            bars[i] = new BoxEl { Width = bw, Height = MathF.Max(2f, h * heights[i]), Shrink = 0f, HitTestVisible = false, Fill = ink };
        return AtBottom(x, bottom, w, h) with { Direction = 0, Gap = gap, AlignItems = FlexAlign.End, Children = bars };
    }

    static ColorF Hex(uint rgb) => ColorF.FromRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
