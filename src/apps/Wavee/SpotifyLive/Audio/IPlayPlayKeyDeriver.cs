using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Derives a plaintext AES key from an obfuscated PlayPlay key through the local PlayPlay runtime.</summary>
public interface IPlayPlayKeyDeriver
{
    Task<PlayPlayDeriveResult> DeriveAsync(
        ReadOnlyMemory<byte> obfuscatedKey,
        ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config,
        string spotifyDllPath,
        string correlationId,
        CancellationToken ct = default);

    Task<PlayPlayDeriveResult> DeriveAsync(
        ReadOnlyMemory<byte> obfuscatedKey,
        ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config,
        string spotifyDllPath,
        string correlationId,
        ReadOnlyMemory<byte> playPlayAux,
        ReadOnlyMemory<byte> licenseRaw = default,
        ReadOnlyMemory<byte> licenseRequest = default,
        CancellationToken ct = default);
}

/// <summary>Headless stub: always reports NeverProvisioned.</summary>
public sealed class NullPlayPlayKeyDeriver : IPlayPlayKeyDeriver
{
    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
        => Task.FromResult(new PlayPlayDeriveResult(default, AudioKeyFailureReason.NeverProvisioned));

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux, CancellationToken ct = default)
        => DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, playPlayAux, default, default, ct);

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux, ReadOnlyMemory<byte> licenseRaw, ReadOnlyMemory<byte> licenseRequest, CancellationToken ct = default)
        => Task.FromResult(new PlayPlayDeriveResult(default, AudioKeyFailureReason.NeverProvisioned));
}
