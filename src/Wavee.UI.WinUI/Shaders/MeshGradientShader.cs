using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.UI.WinUI.Shaders;

[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct MeshGradientShader(
    float time,
    int2 dispatchSize,
    float4 primary,
    float4 accent) : ID2D1PixelShader
{
    public float4 Execute()
    {
        int2 xy = (int2)D2D.GetScenePosition().XY;
        float2 uv = (float2)xy / (float2)dispatchSize;

        float aspect = (float)dispatchSize.X / dispatchSize.Y;
        float2 pos = (uv * 2.0f) - new float2(1.0f, 1.0f);
        pos.X *= aspect;

        float t = time * 0.12f;
        float2 c1 = new float2(Hlsl.Sin(t * 0.70f) * 0.65f, Hlsl.Cos(t * 0.50f) * 0.45f);
        float2 c2 = new float2(Hlsl.Sin((t * 0.93f) + 2.0f) * 0.85f, Hlsl.Cos((t * 1.13f) + 1.0f) * 0.50f);
        float2 c3 = new float2(Hlsl.Cos((t * 0.60f) - 1.2f) * 0.55f, Hlsl.Sin((t * 0.80f) + 0.6f) * 0.35f);

        float d1 = Hlsl.SmoothStep(0.0f, 1.0f, Hlsl.Saturate(1.0f - (Hlsl.Length(pos - c1) * 0.85f)));
        float d2 = Hlsl.SmoothStep(0.0f, 1.0f, Hlsl.Saturate(1.0f - (Hlsl.Length(pos - c2) * 0.95f)));
        float d3 = Hlsl.SmoothStep(0.0f, 1.0f, Hlsl.Saturate(1.0f - (Hlsl.Length(pos - c3) * 1.05f)));

        float3 baseCol = (primary.XYZ * 0.30f) + (accent.XYZ * 0.05f);
        float3 col = baseCol;
        col = Hlsl.Lerp(col, primary.XYZ * 1.12f, d1 * 0.95f);
        col = Hlsl.Lerp(col, accent.XYZ * 1.20f, d2 * 0.85f);
        col = Hlsl.Lerp(col, (primary.XYZ * 0.55f) + (accent.XYZ * 0.75f), d3 * 0.65f);

        float vignette = 1.0f - Hlsl.Saturate(Hlsl.Length(pos) * 0.42f);
        col *= 0.62f + (0.50f * vignette);
        col *= 1.12f;

        return new float4(
            Hlsl.Saturate(col.X),
            Hlsl.Saturate(col.Y),
            Hlsl.Saturate(col.Z),
            1.0f);
    }
}
