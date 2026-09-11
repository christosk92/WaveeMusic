using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The REEL-TO-REEL face: two open flanges over their tape packs, the threaded tape path, the head block and a
/// mechanical counter. Driven by <c>TapeModel</c> with <c>TapeKind.Reel</c> — the same physics as the cassette at
/// ten times the linear speed, so the supply reel visibly empties while the take-up fills.
///
/// <para><b>What moves.</b> Four bound channels, all <c>Transform</c> on a <c>BoxEl</c>: two flange rotations
/// (<c>Angle0</c>/<c>Angle1</c>) and two pack scales (<c>Aux0</c>/<c>Aux1</c>, already normalized by the model to
/// the max pack radius, so a scale of 1 is a full reel). The counter is a BOUND TEXT channel (the player bar's
/// own <c>TimeText</c> pattern): the playhead never re-renders a node here, it re-evaluates one string.</para>
/// </summary>
static class ReelDeck
{
    /// <summary>The threaded tape, authored once in a 100x100 view box: down off the supply reel, around the two
    /// guides, across the heads and back up to the take-up reel. Static geometry — the tape does not move, the
    /// reels do — so it is minted exactly once for this content.</summary>
    static readonly PathData TapeGeometry = PathDataParser.Parse(
        "M26 44 L28 74 L36 80 L64 80 L72 74 L74 44", PathContentEpoch.Mint(), FillRule.NonZero, 100f, 100f);

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        // Read every option BEFORE the first thunk: `preset` is an `in` parameter and cannot be captured.
        string reelStyle = NpvPlayerPrefs.ChoiceSlug(settings, preset, "reel");

        float s = side;
        float reel = 0.40f * s;
        float reelY = 0.07f * s;
        float leftX = 0.06f * s, rightX = s - 0.06f * s - reel;

        var flangeTint = reelStyle switch
        {
            "black" => Hex(0x2A2C33),
            "clear" => Hex(0xC8D7F0, 0.45f),
            _ => Hex(0xC9CDD4),
        };

        float guide = 0.05f * s, guideY = s - 0.24f * s - guide * 0.5f;
        float headW = 0.36f * s, headH = 0.18f * s;
        float counterW = 0.20f * s, counterH = 0.072f * s;

        return Canvas.Create(s, s,
        [
            new CanvasChild(0f, 0f, new BoxEl
            {
                Width = s,
                Height = s,
                Shrink = 0f,
                Gradient = GradientSpec.Vertical(Hex(0x2A2D33), Hex(0x1B1D22)),
            }),
            new CanvasChild(leftX, reelY, Reel(sig, reel, s, flangeTint, left: true)),
            new CanvasChild(rightX, reelY, Reel(sig, reel, s, flangeTint, left: false)),
            new CanvasChild(0.26f * s - guide * 0.5f, guideY, Guide(guide)),
            new CanvasChild(0.74f * s - guide * 0.5f, guideY, Guide(guide)),
            new CanvasChild((s - headW) * 0.5f, s - 0.09f * s - headH, HeadBlock(headW, headH)),
            // The threaded tape runs IN FRONT of the guides and the head faces (that is what "threaded" means) —
            // one static hairline stroke authored in the deck's own 100-unit space.
            new CanvasChild(0f, 0f, new PathEl
            {
                Geometry = TapeGeometry,
                Width = s,
                Height = s,
                Shrink = 0f,
                ViewBoxW = 100f,
                ViewBoxH = 100f,
                Fill = ColorF.Transparent,
                StrokeColor = Hex(0x5A4028),
                Stroke = new StrokeStyle(1.2f, LineCap.Round, LineJoin.Round),
            }),
            new CanvasChild(s - 0.05f * s - counterW, s - 0.055f * s - counterH, Counter(bridge, counterW, counterH)),
        ]);
    }

    /// <summary>One reel: the tape pack (scale-bound), the rotating flange with its three cutouts, the rim ring and
    /// the machined hub. Both reels share the same max pack width, so the scale IS the model's radius fraction.</summary>
    static Element Reel(DeckSignals sig, float d, float s, ColorF flangeTint, bool left)
    {
        float packD = 0.92f * d, hubD = 0.22f * d;

        var pack = new BoxEl
        {
            Width = packD,
            Height = packD,
            Shrink = 0f,
            Corners = Radii.Circle(packD),
            Fill = Hex(0x4A3520),
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = left
                ? Prop.Of(() => Affine2D.Scale(sig.Aux0.Value, sig.Aux0.Value))
                : Prop.Of(() => Affine2D.Scale(sig.Aux1.Value, sig.Aux1.Value)),
            Children = [DeckArt.Texture("grooves-1024.png", packD, packD, Radii.Circle(packD), Hex(0x2A1D11, 0.55f))],
        };

        // The flange turns; the PNG inside it is a plain static child, so the only per-frame write is this transform.
        var flange = new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = left
                ? Prop.Of(() => Affine2D.Rotation(sig.Angle0.Value * DeckArt.Deg2Rad))
                : Prop.Of(() => Affine2D.Rotation(sig.Angle1.Value * DeckArt.Deg2Rad)),
            Children = [DeckArt.Texture("reel-flange-512.png", d, d, Radii.Circle(d), flangeTint)],
        };

        return Canvas.Create(d, d,
        [
            new CanvasChild((d - packD) * 0.5f, (d - packD) * 0.5f, pack),
            new CanvasChild(0f, 0f, flange),
            // Fallback rim: a missing flange PNG still reads as a reel rather than as a brown disc.
            new CanvasChild(0f, 0f, DeckArt.Ring(d, 0.012f * s, Hex(0xFFFFFF, 0.22f))),
            new CanvasChild((d - hubD) * 0.5f, (d - hubD) * 0.5f, new BoxEl
            {
                Width = hubD,
                Height = hubD,
                Shrink = 0f,
                Corners = Radii.Circle(hubD),
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0f, Hex(0xE9EBEF)),
                    new GradientStop(1f, Hex(0x8A8F99)),
                ]),
            }),
        ]);
    }

    /// <summary>A tape guide roller: a dark pin inside a bright collar.</summary>
    static Element Guide(float d) => new BoxEl
    {
        Width = d,
        Height = d,
        Shrink = 0f,
        Corners = Radii.Circle(d),
        Fill = Hex(0x1E2026),
        BorderWidth = d * 0.16f,
        BorderColor = Hex(0xA9AEB8),
    };

    /// <summary>The head block: erase / record / play, three faces in a machined housing.</summary>
    static Element HeadBlock(float w, float h)
    {
        float headW = w * 0.14f, headH = h * 0.52f, y = (h - headH) * 0.5f;
        float gap = (w - headW * 3f) / 4f;
        return new BoxEl
        {
            Width = w,
            Height = h,
            Shrink = 0f,
            Corners = CornerRadius4.All(6f),
            Gradient = GradientSpec.Vertical(Hex(0x3B3F47), Hex(0x23262C)),
            Shadow = new ShadowSpec(18f, 8f, 0f, Hex(0x000000, 0.45f)),
            Children =
            [
                Canvas.Create(w, h,
                [
                    new CanvasChild(gap, y, Head(headW, headH)),
                    new CanvasChild(gap * 2f + headW, y, Head(headW, headH)),
                    new CanvasChild(gap * 3f + headW * 2f, y, Head(headW, headH)),
                ]),
            ],
        };
    }

    static Element Head(float w, float h) => new BoxEl
    {
        Width = w,
        Height = h,
        Shrink = 0f,
        Corners = CornerRadius4.All(2f),
        Gradient = GradientSpec.Vertical(Hex(0xB9BEC8), Hex(0x6B7079)),
        BorderWidth = 1f,
        BorderColor = Hex(0x000000, 0.35f),
    };

    static Element Counter(PlaybackBridge bridge, float w, float h) => new BoxEl
    {
        Width = w,
        Height = h,
        Shrink = 0f,
        Corners = CornerRadius4.All(3f),
        Fill = Hex(0x1A0D00),
        BorderWidth = 1f,
        BorderColor = Hex(0x000000, 0.6f),
        Justify = FlexJustify.Center,
        AlignItems = FlexAlign.Center,
        Children = [Embed.Comp(() => new ReelCounter { Bridge = bridge, H = h }) with { Key = "reel-counter" }],
    };

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    /// <summary>The four-digit mechanical counter. It renders ONCE: the playhead arrives through a bound text
    /// channel, and a one-entry format cache means the second that has not changed costs no string at all.</summary>
    sealed class ReelCounter : Component
    {
        public required PlaybackBridge Bridge;
        public required float H;

        int _lastSec = -1;
        string _lastText = "0000";

        public override Element Render()
        {
            var b = Bridge;
            // Read the playhead INSIDE the thunk (never in Render): a component that subscribed to PositionMs would
            // rebuild this node on every playback tick for a string that changes once a second.
            Prop<string> digits = Prop.Of(() =>
            {
                long ms = b.ScrubTargetMs.Value ?? b.SeekTargetMs.Value ?? b.PositionMs.Value;
                int sec = (int)((ms / 1000L) % 10000L);
                if (sec < 0) sec = 0;
                if (sec != _lastSec)
                {
                    _lastSec = sec;
                    _lastText = sec.ToString("D4");
                }
                return _lastText;
            });
            return new TextEl(digits)
            {
                Height = H * 0.72f,
                Size = H * 0.56f,
                Weight = 600,
                FontFamily = "Consolas",
                Color = Hex(0xFFB000),
                CharSpacing = 120f,
                MaxLines = 1,
                Shrink = 0f,
            };
        }
    }
}
