using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Wavee.Backend.Audio;

/// <summary>On-disk manifest beside a bare Spotify-signed <c>Spotify.dll</c>. Pins for one build/arch — never committed to git.</summary>
public sealed record PlayPlayRuntimeManifest(
    int SchemaVersion,
    string PackId,
    string SpotifyVersion,
    string? ClientVersion,
    string AppVersion,
    int PlayPlayRequestVersion,
    string Arch,
    string AlgorithmVersion,
    string DllSha256,
    string PlayPlayTokenHex,
    PlayPlayRuntimeManifestConfig Config)
{
    public string? Validate()
    {
        if (SchemaVersion != 1) return "schemaVersion must be 1";
        if (string.IsNullOrWhiteSpace(PackId)) return "packId is required";
        if (string.IsNullOrWhiteSpace(SpotifyVersion)) return "spotifyVersion is required";
        if (string.IsNullOrWhiteSpace(AppVersion)) return "appVersion is required";
        if (string.IsNullOrWhiteSpace(Arch)) return "arch is required";
        if (!TryParseArch(Arch, out var arch)) return $"unsupported arch '{Arch}'";
        if (!PackId.EndsWith("-" + ArchLabel(arch), StringComparison.OrdinalIgnoreCase))
            return $"packId '{PackId}' does not match arch '{Arch}'";
        if (string.IsNullOrWhiteSpace(AlgorithmVersion)) return "algorithmVersion is required";
        if (DllSha256.Length != 64 || !IsHex(DllSha256)) return "dllSha256 must be 64 hex chars";
        if (string.IsNullOrWhiteSpace(PlayPlayTokenHex) || !IsHex(PlayPlayTokenHex)) return "playPlayTokenHex must be hex";
        if (Config is null) return "config is required";
        return Config.Validate();
    }

    public PlayPlayConfig ToPlayPlayConfig(byte[] dllSha256)
    {
        if (!TryParseArch(Arch, out var arch))
            throw new InvalidOperationException($"unsupported arch '{Arch}'");
        var c = Config;
        return new PlayPlayConfig(
            Version: SpotifyVersion,
            Arch: arch,
            Sha256: dllSha256,
            PlayPlayToken: Convert.FromHexString(PlayPlayTokenHex),
            VmInitValue: Convert.FromHexString(c.VmInitValueHex),
            AnalysisBase: ParseUlong(c.AnalysisBaseHex),
            VmRuntimeInitVa: ParseUlong(c.VmRuntimeInitVaHex),
            VmObjectTransformVa: ParseUlong(c.VmObjectTransformVaHex),
            RuntimeContextVa: ParseUlong(c.RuntimeContextVaHex),
            RuntimeContextSecondaryVa: ParseUlong(c.RuntimeContextSecondaryVaHex),
            InitVtableLabs: c.InitVtableLabsHex.Select(ParseUlong).ToArray(),
            TransformFourthArgTemplateVa: ParseUlong(c.TransformFourthArgTemplateVaHex),
            TransformFourthArgBuildVa: ParseUlong(c.TransformFourthArgBuildVaHex),
            FillRandomBytesVa: ParseUlong(c.FillRandomBytesVaHex),
            AesKey: c.AesKey,
            VmObjectSize: c.VmObjectSize,
            RtContextSize: c.RtContextSize,
            DerivedKeySize: c.DerivedKeySize,
            ObfuscatedKeySize: c.ObfuscatedKeySize,
            InitValueSize: c.InitValueSize,
            ContentIdSize: c.ContentIdSize,
            KeySize: c.KeySize);
    }

    public static bool TryParseArch(string arch, out Architecture parsed) =>
        Enum.TryParse(arch, ignoreCase: true, out parsed);

    static string ArchLabel(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => arch.ToString().ToLowerInvariant(),
    };

    static ulong ParseUlong(string hex) => Convert.ToUInt64(hex, 16);

    static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}

public sealed record PlayPlayRuntimeManifestConfig(
    string AnalysisBaseHex,
    string VmRuntimeInitVaHex,
    string VmObjectTransformVaHex,
    string RuntimeContextVaHex,
    string RuntimeContextSecondaryVaHex,
    string[] InitVtableLabsHex,
    string TransformFourthArgTemplateVaHex,
    string TransformFourthArgBuildVaHex,
    string FillRandomBytesVaHex,
    string VmInitValueHex,
    AesKeyExtraction AesKey,
    int VmObjectSize,
    int RtContextSize,
    int DerivedKeySize,
    int ObfuscatedKeySize,
    int InitValueSize,
    int ContentIdSize,
    int KeySize)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(AnalysisBaseHex)) return "config.analysisBaseHex is required";
        if (AesKey is null) return "config.aesKey is required";
        if (VmObjectSize <= 0) return "config.vmObjectSize must be positive";
        if (DerivedKeySize <= 0) return "config.derivedKeySize must be positive";
        return null;
    }
}

[JsonSerializable(typeof(PlayPlayRuntimeManifest))]
[JsonSerializable(typeof(PlayPlayRuntimeManifestConfig))]
[JsonSerializable(typeof(AesKeyExtraction))]
[JsonSerializable(typeof(AesKeyExtraction.TriggerRipBreakpoint))]
[JsonSerializable(typeof(AesKeyExtraction.OutputBufferSlice))]
[JsonSerializable(typeof(AesKeyExtraction.PostProcessCall))]
internal sealed partial class PlayPlayManifestJson : JsonSerializerContext { }
