using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Step A seam: fetch the obfuscated PlayPlay key. Interface (protobuf-free) so the key resolver is unit-testable
/// with a fake, and so the resolver + tests can be source-included without pulling in the protobuf license client.</summary>
public interface ILicenseClient
{
    Task<PlayPlayLicenseResult> FetchLicenseAsync(
        string fileIdHex, PlayPlayConfig config, CancellationToken ct);

    Task<(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason)> FetchObfuscatedKeyAsync(
        string fileIdHex, PlayPlayConfig config, CancellationToken ct);
}

public readonly record struct PlayPlayLicenseResult(
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Auxiliary,
    AudioKeyFailureReason Reason,
    string FieldSummary,
    ReadOnlyMemory<byte> RawBody = default,
    ReadOnlyMemory<byte> RequestBody = default)
{
    public static PlayPlayLicenseResult Fail(AudioKeyFailureReason reason) => new(default, default, reason, "");
}
