using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.Controls.HeroCarousel.Shaders;

// Single-pass cozy mosaic compositor. Samples the rasterized wall (input 0)
// and does the entire prototype pipeline in HLSL:
//
//   1. multiply tint   (CSS mix-blend-mode: multiply, opacity 0.55)
//   2. overlay tint    (CSS mix-blend-mode: overlay, opacity 0.25)
//   3. left fade       (CSS .wash.mode-all::before, 100° linear gradient)
//   4. top + bottom    (CSS .wash.mode-all::after, vertical gradient masked
//      bars              horizontally so they decay toward the right)
//   5. vignette        (CSS .vignette — radial bottom + linear bottom dim)
//
// Color-space contract: the input is sampled as 8-bit sRGB encoded (Win2D
// CanvasRenderTarget default), tint constants arrive as 8-bit sRGB encoded
// from OklchHelpers (gamma-applied per CSS Color Module 4), and the multiply
// runs in sRGB space. This matches CSS byte-for-byte. Do NOT switch to a
// linear pipeline without re-tuning the constants.
[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct MosaicWashShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float4 tintColor;       // multiply pass color (sRGB encoded)
    private readonly float4 tintHighlight;   // overlay pass color (sRGB encoded), .a = opacity
    private readonly float tintAmount;       // 0.55 in prototype
    private readonly float washStop1;
    private readonly float washStop2;
    private readonly float barSize;
    private readonly float barFeather;
    private readonly float barDecayStart;
    private readonly float barDecayEnd;

    public MosaicWashShader(
        float width,
        float height,
        float4 tintColor,
        float4 tintHighlight,
        float tintAmount,
        float washStop1,
        float washStop2,
        float barSize,
        float barFeather,
        float barDecayStart,
        float barDecayEnd)
    {
        this.width = width;
        this.height = height;
        this.tintColor = tintColor;
        this.tintHighlight = tintHighlight;
        this.tintAmount = tintAmount;
        this.washStop1 = washStop1;
        this.washStop2 = washStop2;
        this.barSize = barSize;
        this.barFeather = barFeather;
        this.barDecayStart = barDecayStart;
        this.barDecayEnd = barDecayEnd;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 uv = position / new float2(width, height);

        float4 wall = D2D.GetInput(0);
        float3 c = wall.XYZ;

        // multiply tint pass: result = c * lerp(1, tint, amount)
        // (CSS mix-blend-mode: multiply with opacity = amount)
        float3 multiplyMask = Hlsl.Lerp(new float3(1.0f, 1.0f, 1.0f), tintColor.XYZ, tintAmount);
        c = c * multiplyMask;

        // overlay tint pass: per-channel where below=2*b*h, above=1-2*(1-b)*(1-h),
        // selected by step(0.5, c). Composited at tintHighlight.a opacity.
        float3 step5 = Hlsl.Step(new float3(0.5f, 0.5f, 0.5f), c);
        float3 below = 2.0f * c * tintHighlight.XYZ;
        float3 above = new float3(1.0f, 1.0f, 1.0f) - 2.0f * (new float3(1.0f, 1.0f, 1.0f) - c) * (new float3(1.0f, 1.0f, 1.0f) - tintHighlight.XYZ);
        float3 overlay = Hlsl.Lerp(below, above, step5);
        c = Hlsl.Lerp(c, overlay, tintHighlight.W);

        // Layer A: 100° left fade. tan(10°) ≈ 0.176.
        float tA = Hlsl.Saturate(uv.X + 0.176f * (uv.Y - 0.5f));
        float4 A = LeftFade(tA);
        c = A.XYZ + c * (1.0f - A.W);

        // Layer B: top + bottom bars (vertical), masked horizontally.
        float4 B = TopBottomBars(uv.Y);
        float maskRange = Hlsl.Max(0.0001f, barDecayEnd - barDecayStart);
        float mask = Hlsl.Saturate((barDecayEnd - uv.X) / maskRange);
        B *= mask;
        c = B.XYZ + c * (1.0f - B.W);

        // Layer V: bottom radial + linear vignette (hardcoded constants).
        float4 V = Vignette(uv);
        c = V.XYZ + c * (1.0f - V.W);

        return new float4(c, 1.0f);
    }

    private float4 LeftFade(float t)
    {
        // Prototype warm-brown stops. Per-slide hue tinting happens in the
        // multiply/overlay tint passes above, not here — keeping the wash
        // hardcoded avoids constant-buffer packing pitfalls when adding a
        // float4 mid-list and produces a stable cliff regardless of artwork.
        float3 c0 = new float3(8.0f, 4.0f, 2.0f) / 255.0f;
        float3 c1 = new float3(20.0f, 10.0f, 4.0f) / 255.0f;
        float3 c2 = new float3(40.0f, 18.0f, 8.0f) / 255.0f;

        float a0 = 0.96f;
        float a1 = 0.88f;
        float a2 = 0.45f;
        float a3 = 0.0f;

        float s1 = washStop1;
        float s2 = washStop1 + 0.12f;
        float s3 = washStop2;

        float3 rgb;
        float a;
        if (t <= s1)
        {
            float u = t / Hlsl.Max(0.0001f, s1);
            rgb = Hlsl.Lerp(c0, c1, u);
            a = Hlsl.Lerp(a0, a1, u);
        }
        else if (t <= s2)
        {
            float u = (t - s1) / Hlsl.Max(0.0001f, s2 - s1);
            rgb = Hlsl.Lerp(c1, c2, u);
            a = Hlsl.Lerp(a1, a2, u);
        }
        else if (t <= s3)
        {
            float u = (t - s2) / Hlsl.Max(0.0001f, s3 - s2);
            rgb = c2;
            a = Hlsl.Lerp(a2, a3, u);
        }
        else
        {
            rgb = c2;
            a = 0.0f;
        }

        return new float4(rgb * a, a);
    }

    private float4 TopBottomBars(float t)
    {
        float bs = barSize;
        float bf = barFeather;
        float p1 = bs - bf;
        float p2 = bs;
        float p3 = bs + bf;
        float p4 = 1.0f - bs - bf;
        float p5 = 1.0f - bs;
        float p6 = 1.0f - bs + bf;

        // Prototype warm-brown bar stops (28:40 ratio against LeftFade).
        float3 c0 = new float3(6.0f, 3.0f, 2.0f) / 255.0f;
        float3 c1 = new float3(14.0f, 7.0f, 3.0f) / 255.0f;
        float3 c2 = new float3(28.0f, 14.0f, 6.0f) / 255.0f;

        float a0 = 0.92f;
        float a1 = 0.85f;
        float a2 = 0.50f;
        float a3 = 0.22f;

        float3 rgb;
        float a;
        if (t <= p1)
        {
            float u = t / Hlsl.Max(0.0001f, p1);
            rgb = Hlsl.Lerp(c0, c1, u);
            a = Hlsl.Lerp(a0, a1, u);
        }
        else if (t <= p2)
        {
            float u = (t - p1) / Hlsl.Max(0.0001f, p2 - p1);
            rgb = Hlsl.Lerp(c1, c2, u);
            a = Hlsl.Lerp(a1, a2, u);
        }
        else if (t <= p3)
        {
            float u = (t - p2) / Hlsl.Max(0.0001f, p3 - p2);
            rgb = c2;
            a = Hlsl.Lerp(a2, a3, u);
        }
        else if (t <= p4)
        {
            rgb = c2;
            a = a3;
        }
        else if (t <= p5)
        {
            float u = (t - p4) / Hlsl.Max(0.0001f, p5 - p4);
            rgb = c2;
            a = Hlsl.Lerp(a3, a2, u);
        }
        else if (t <= p6)
        {
            float u = (t - p5) / Hlsl.Max(0.0001f, p6 - p5);
            rgb = Hlsl.Lerp(c2, c1, u);
            a = Hlsl.Lerp(a2, a1, u);
        }
        else
        {
            float u = (t - p6) / Hlsl.Max(0.0001f, 1.0f - p6);
            rgb = Hlsl.Lerp(c1, c0, u);
            a = Hlsl.Lerp(a1, a0, u);
        }

        return new float4(rgb * a, a);
    }

    private static float4 Vignette(float2 uv)
    {
        // Hardcoded to match prototype CSS vignette:
        //   radial-gradient(120% 80% at 50% 110%, rgba(0,0,0,0.45), transparent 60%)
        //   linear-gradient(180deg, transparent 60%, rgba(0,0,0,0.35) 100%)
        float2 d = (uv - new float2(0.5f, 1.1f)) / new float2(1.2f, 0.8f);
        float dist = Hlsl.Length(d);
        float radialAlpha = (1.0f - Hlsl.SmoothStep(0.0f, 0.6f, dist)) * 0.45f;

        float linAlpha = Hlsl.SmoothStep(0.6f, 1.0f, uv.Y) * 0.35f;

        float a = radialAlpha + linAlpha * (1.0f - radialAlpha);
        return new float4(0.0f, 0.0f, 0.0f, a);
    }
}
