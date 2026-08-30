using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Wavee.Backend.Audio;

/// <summary>The remote delivery index served at the catalog URL: a list of runtime packs, each a superset of the local
/// <see cref="PlayPlayRuntimeManifest"/> (all pins inline) plus the download fields (urls, compression, size). The app
/// synthesizes the local manifest from the chosen entry after verifying the downloaded DLL — it never trusts the wire.</summary>
public sealed record PlayPlayRuntimeCatalog(
    int SchemaVersion,
    PlayPlayRuntimeCatalogEntry[] Packs)
{
    public string? Validate()
    {
        if (SchemaVersion != 1) return "schemaVersion must be 1";
        if (Packs is null) return "packs is required";
        return null;
    }
}

/// <summary>One deliverable runtime pack: the manifest pins (identical shape to <see cref="PlayPlayRuntimeManifest"/>)
/// plus delivery metadata. The URL(s) point at a raw or Brotli-compressed <c>Spotify.dll</c>; <see cref="DllSha256"/> is
/// the hash of the <em>decompressed</em> bytes and is the hard integrity gate.</summary>
public sealed record PlayPlayRuntimeCatalogEntry(
    string PackId,
    string SpotifyVersion,
    string? ClientVersion,
    string AppVersion,
    int PlayPlayRequestVersion,
    string Arch,
    string AlgorithmVersion,
    string DllSha256,
    string PlayPlayTokenHex,
    PlayPlayRuntimeManifestConfig Config,
    string[] Urls,
    string Compression,
    long? DownloadSize)
{
    public const string CompressionBrotli = "br";
    public const string CompressionNone = "none";

    /// <summary>True when the delivered <c>Spotify.dll</c> is Brotli-compressed and must be inflated before hashing.</summary>
    public bool IsBrotli => string.Equals(Compression, CompressionBrotli, StringComparison.OrdinalIgnoreCase);

    /// <summary>Project the delivery entry down to the exact record the existing verifier / <c>ToPlayPlayConfig</c>
    /// consume — dropping the wire-only fields (urls, compression, size).</summary>
    public PlayPlayRuntimeManifest ToLocalManifest() => new(
        SchemaVersion: 1,
        PackId: PackId,
        SpotifyVersion: SpotifyVersion,
        ClientVersion: ClientVersion,
        AppVersion: AppVersion,
        PlayPlayRequestVersion: PlayPlayRequestVersion,
        Arch: Arch,
        AlgorithmVersion: AlgorithmVersion,
        DllSha256: DllSha256,
        PlayPlayTokenHex: PlayPlayTokenHex,
        Config: Config);

    public string? Validate()
    {
        if (Urls is null || Urls.Length == 0) return "pack urls are required";
        if (!string.Equals(Compression, CompressionBrotli, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Compression, CompressionNone, StringComparison.OrdinalIgnoreCase))
            return $"unsupported compression '{Compression}'";
        // The pins must satisfy the same rules a local manifest does.
        return ToLocalManifest().Validate();
    }

    /// <summary>True when this pack's architecture matches the running process (algorithm support is decided by the
    /// selector, which owns the supported-algorithm list).</summary>
    public bool MatchesProcessArch() =>
        PlayPlayRuntimeManifest.TryParseArch(Arch, out var arch) && arch == RuntimeInformation.ProcessArchitecture;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlayPlayRuntimeCatalog))]
[JsonSerializable(typeof(PlayPlayRuntimeCatalogEntry))]
[JsonSerializable(typeof(PlayPlayRuntimeManifestConfig))]
[JsonSerializable(typeof(AesKeyExtraction))]
[JsonSerializable(typeof(AesKeyExtraction.TriggerRipBreakpoint))]
[JsonSerializable(typeof(AesKeyExtraction.OutputBufferSlice))]
[JsonSerializable(typeof(AesKeyExtraction.PostProcessCall))]
internal sealed partial class PlayPlayCatalogJson : JsonSerializerContext { }
