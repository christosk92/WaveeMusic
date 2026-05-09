using System.Runtime.InteropServices;

namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. The audio runtime support pack is provisioned
// at runtime from a manifest; no static configuration is bundled here. This
// stub keeps public clones of the repo compiling.
public sealed record PlayPlayConfig(
    string Version,
    Architecture Arch,
    byte[] Sha256,
    byte[] PlayPlayToken,
    byte[] VmInitValue,
    ulong AnalysisBase,
    ulong VmRuntimeInitVa,
    ulong VmObjectTransformVa,
    ulong RuntimeContextVa,
    ulong FillRandomBytesVa,
    AesKeyExtraction AesKey,
    int VmObjectSize,
    int RtContextSize,
    int DerivedKeySize,
    int ObfuscatedKeySize,
    int InitValueSize,
    int ContentIdSize,
    int KeySize);

public abstract record AesKeyExtraction
{
    public sealed record TriggerRipBreakpoint(ulong RipVa, int ContextRegOffset) : AesKeyExtraction;
    public sealed record OutputBufferSlice(int OffsetBytes, int LengthBytes) : AesKeyExtraction;
    public sealed record PostProcessCall(ulong FunctionVa, int OutputOffsetBytes) : AesKeyExtraction;
}
