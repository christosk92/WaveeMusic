using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.Controls.HeroCarousel.Shaders;

[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct LeftColorWashShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float3 baseColor;
    private readonly float3 accentColor;
    private readonly float strength;
    private readonly float warmth;

    public LeftColorWashShader(
        float width,
        float height,
        float baseRed,
        float baseGreen,
        float baseBlue,
        float accentRed,
        float accentGreen,
        float accentBlue,
        float strength,
        float warmth)
    {
        this.width = width;
        this.height = height;
        this.baseColor = new float3(baseRed, baseGreen, baseBlue);
        this.accentColor = new float3(accentRed, accentGreen, accentBlue);
        this.strength = strength;
        this.warmth = warmth;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 uv = position / new float2(width, height);

        // Store-style editorial plate. The upper and lower fields travel
        // farther across the image, while the middle collapses earlier to
        // create the sharp "cliff" instead of a single flat veil.
        float plate = 1.0f - Hlsl.SmoothStep(0.18f, 0.54f, uv.X);
        float shoulder = 1.0f - Hlsl.SmoothStep(0.36f, 0.76f, uv.X);
        float tail = 1.0f - Hlsl.SmoothStep(0.58f, 1.0f, uv.X);
        float centerCliff = 1.0f - Hlsl.SmoothStep(0.18f, 0.48f, uv.X);

        float centerBand = 1.0f - Hlsl.SmoothStep(0.00f, 0.18f, Hlsl.Abs(uv.Y - 0.50f));
        float topBand = 1.0f - Hlsl.SmoothStep(0.18f, 0.54f, uv.Y);
        float bottomBand = Hlsl.SmoothStep(0.46f, 0.82f, uv.Y);
        float edgeExtension = Hlsl.Saturate(topBand + bottomBand);
        float vertical = 0.86f + edgeExtension * 0.22f - centerBand * 0.20f;
        float grain = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(position, new float2(27.619f, 57.583f))) * 43758.5453f);
        float matte = centerCliff * centerBand * 0.46f + plate * 0.34f + shoulder * 0.17f + tail * edgeExtension * 0.10f;
        float alpha = Hlsl.Saturate(matte * vertical * (0.985f + grain * 0.025f) * strength);

        float blend = Hlsl.Saturate(warmth * (0.08f + uv.Y * 0.14f) + tail * edgeExtension * 0.035f);
        float3 color = Hlsl.Lerp(baseColor, accentColor, blend);

        return new float4(color * alpha, alpha);
    }
}
