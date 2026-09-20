using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Wavee.Backend.Audio;

/// <summary>Per-build configuration for the audio runtime support pack. Parsed once UI-side from the signed manifest;
/// the parsed record crosses IPC — the host never hand-parses manifest JSON.</summary>
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
    ulong RuntimeContextSecondaryVa,
    ulong[] InitVtableLabs,
    ulong TransformFourthArgTemplateVa,
    ulong TransformFourthArgBuildVa,
    ulong FillRandomBytesVa,
    AesKeyExtraction AesKey,
    int VmObjectSize,
    int RtContextSize,
    int DerivedKeySize,
    int ObfuscatedKeySize,
    int InitValueSize,
    int ContentIdSize,
    int KeySize);

/// <summary>Strategy for extracting the 16-byte audio AES key after the VM transform has run.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "strategy")]
[JsonDerivedType(typeof(AesKeyExtraction.TriggerRipBreakpoint), "trigger_rip")]
[JsonDerivedType(typeof(AesKeyExtraction.OutputBufferSlice), "buffer_slice")]
[JsonDerivedType(typeof(AesKeyExtraction.PostProcessCall), "post_process")]
public abstract record AesKeyExtraction
{
    public sealed record TriggerRipBreakpoint(ulong RipVa, int ContextRegOffset) : AesKeyExtraction;
    public sealed record OutputBufferSlice(int OffsetBytes, int LengthBytes) : AesKeyExtraction;
    public sealed record PostProcessCall(ulong FunctionVa, int OutputOffsetBytes) : AesKeyExtraction;
}

public sealed record RuntimeAsset(string PackPath, PlayPlayConfig Config, string PackId);
