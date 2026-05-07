using ComputeSharp;
using ComputeSharp.D2D1;

namespace Wavee.UI.WinUI.Effects.Editorial;

// Theme-aware noise shader — verbatim port from Klankhuis.Hero (Microsoft Store
// ComputeSharp case study §4.1.1). Produces a grayscale noise texture that
// modulates the RGB channels with a configurable alpha. Used to break up colour
// banding from heavy gaussian blur in the editorial hero backdrop.
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(
    D2D1CompileOptions.Default
    | D2D1CompileOptions.EnableLinking
    | D2D1CompileOptions.PartialPrecision)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct NoiseShader : ID2D1PixelShader
{
    private readonly float _alpha;
    private readonly float _minimum;
    private readonly float _maximum;

    public NoiseShader(float alpha, float minimum, float maximum)
    {
        _alpha = alpha;
        _minimum = minimum;
        _maximum = maximum;
    }

    public NoiseShader(byte alpha, byte minimum, byte maximum)
        : this(alpha / 255.0f, minimum / 255.0f, maximum / 255.0f)
    {
    }

    public float4 Execute()
    {
        int2 position = (int2)D2D.GetScenePosition().XY;

        // Pseudorandom value in [0, 1] per pixel
        float hash = Hash21(position);

        // Linear interpolation between the requested range
        float color = Hlsl.Lerp(_minimum, _maximum, hash);

        return new float4(color, color, color, _alpha);
    }

    // Pseudorandom hash — prime-based dot product + sin trick yields uniform
    // noise distribution. Numbers are arbitrary but produce visually appealing
    // noise without repeated patterns.
    private static float Hash21(float2 x) =>
        Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(x.XY, new float2(27.619f, 57.583f))) * 43758.5453f);
}
