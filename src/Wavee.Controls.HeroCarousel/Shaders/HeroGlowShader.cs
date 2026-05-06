using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.Controls.HeroCarousel.Shaders;

[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct HeroGlowShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float3 color;
    private readonly float strength;
    private readonly float bleed;

    public HeroGlowShader(float width, float height, float red, float green, float blue, float strength, float bleed)
    {
        this.width = width;
        this.height = height;
        this.color = new float3(red, green, blue);
        this.strength = strength;
        this.bleed = bleed;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 center = new(width * 0.5f, height * 0.5f);
        float2 stageHalf = new((width - bleed) * 0.5f, (height - bleed) * 0.5f);
        float radius = 10.0f;
        float2 delta = Hlsl.Abs(position - center);
        float2 q = delta - (stageHalf - new float2(radius, radius));
        float outside = Hlsl.Length(Hlsl.Max(q, new float2(0.0f, 0.0f)));
        float inside = Hlsl.Min(Hlsl.Max(q.X, q.Y), 0.0f);
        float signedDistance = outside + inside - radius;

        float outsideDistance = Hlsl.Max(signedDistance, 0.0f);
        float2 uv = position / new float2(width, height);

        float outsideMask = Hlsl.SmoothStep(0.0f, 4.0f, signedDistance);
        float contact = 1.0f - Hlsl.SmoothStep(0.0f, bleed * 0.18f, outsideDistance);
        float softBloom = 1.0f - Hlsl.SmoothStep(0.0f, bleed * 0.56f, outsideDistance);
        float longBloom = 1.0f - Hlsl.SmoothStep(bleed * 0.18f, bleed * 0.96f, outsideDistance);
        float leftBias = 0.66f + 0.34f * (1.0f - Hlsl.SmoothStep(0.10f, 0.82f, uv.X));
        float bottomBias = 0.78f + 0.22f * Hlsl.SmoothStep(0.36f, 1.0f, uv.Y);
        float topGuard = Hlsl.SmoothStep(0.00f, 0.16f, uv.Y);
        float alpha = Hlsl.Saturate(
            outsideMask *
            topGuard *
            leftBias *
            bottomBias *
            (contact * 0.070f + softBloom * 0.105f + longBloom * 0.070f) *
            strength);

        return new float4(color * alpha, alpha);
    }
}
