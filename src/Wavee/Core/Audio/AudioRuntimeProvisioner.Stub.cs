using Microsoft.Extensions.Logging;

namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. The real first-run runtime provisioning logic
// is not bundled in the public repository — this stub keeps the rest of the
// codebase compiling. With this stub in place, EnsureAvailableAsync always
// returns null and PlayPlay key derivation stays disabled at runtime
// (AudioKeyManager falls back to AP-only key resolution).
public sealed record RuntimeAsset(string Path, PlayPlayConfig Config, string PackJson);

public sealed class AudioRuntimeProvisioner
{
    public AudioRuntimeProvisioner(HttpClient http, ILogger? logger = null)
    {
        _ = http;
        _ = logger;
    }

    public Task<RuntimeAsset?> EnsureAvailableAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<RuntimeAsset?>(null);
    }
}
