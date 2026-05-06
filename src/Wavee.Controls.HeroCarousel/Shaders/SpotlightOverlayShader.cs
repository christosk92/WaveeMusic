using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.Controls.HeroCarousel.Shaders;

[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct SpotlightOverlayShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float2 cursorPx;
    private readonly float opacity;

    public SpotlightOverlayShader(float width, float height, float cursorX, float cursorY, float opacity)
    {
        this.width = width;
        this.height = height;
        this.cursorPx = new float2(cursorX, cursorY);
        this.opacity = opacity;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float distance = Hlsl.Length(position - cursorPx);
        float primary = 1.0f - Hlsl.SmoothStep(0.0f, 360.0f, distance);
        float falloff = 1.0f - Hlsl.SmoothStep(170.0f, 620.0f, distance);

        float2 uv = position / new float2(width, height);
        float2 centered = uv - new float2(0.5f, 0.5f);
        float vignette = Hlsl.Saturate(Hlsl.Dot(centered, centered) * 1.15f);

        float grain = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(position, new float2(27.619f, 57.583f))) * 43758.5453f);
        float alpha = Hlsl.Saturate((primary * 0.30f + falloff * 0.08f + vignette * 0.08f + (grain - 0.5f) * 0.045f) * opacity);
        float3 tint = new(1.0f, 0.88f, 0.64f);

        return new float4(tint * alpha, alpha);
    }
}
