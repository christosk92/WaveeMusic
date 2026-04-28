using Microsoft.Extensions.Logging;
using Wavee.AudioIpc;
using Wavee.Core.Http;
using Wavee.Core.Storage;

namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. Wavee follows strict guidelines (proper
// user-agents, playback events, no abuse). Tracks are chunked per user request
// and not stored to disk. PlayPlay keys are only cached in obfuscated form and
// still require the same algorithm to read.
public sealed class AudioHostPlayPlayKeyDeriver : IPlayPlayKeyDeriver
{
    private readonly ISpClient _spClient;
    private readonly Func<AudioPipelineProxy?> _proxyResolver;
    private readonly string _spotifyDllPath;
    private readonly ICacheService? _cacheService;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AudioHostPlayPlayKeyDeriver(
        ISpClient spClient,
        Func<AudioPipelineProxy?> proxyResolver,
        string spotifyDllPath,
        ICacheService? cacheService = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spClient);
        ArgumentNullException.ThrowIfNull(proxyResolver);
        if (string.IsNullOrWhiteSpace(spotifyDllPath))
            throw new ArgumentException("spotifyDllPath is required", nameof(spotifyDllPath));

        _spClient = spClient;
        _proxyResolver = proxyResolver;
        _spotifyDllPath = spotifyDllPath;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task<byte[]> DeriveAsync(SpotifyId trackId, FileId fileId, CancellationToken cancellationToken = default)
    {
        if (!fileId.IsValid)
            throw new ArgumentException("FileId is not valid", nameof(fileId));

        // Cache only the obfuscated key — useless without the cipher.
        byte[]? obfuscatedKey = null;
        if (_cacheService is not null)
        {
            try
            {
                obfuscatedKey = await _cacheService
                    .GetPlayPlayObfuscatedKeyAsync(fileId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Obfuscated-key cache read failed");
            }
        }

        if (obfuscatedKey is null || obfuscatedKey.Length != 16)
        {
            obfuscatedKey = await _spClient.ResolvePlayPlayObfuscatedKeyAsync(fileId, cancellationToken)
                .ConfigureAwait(false);
            if (obfuscatedKey.Length != 16)
                throw new InvalidOperationException(
                    $"PlayPlay license returned obfuscated_key with length {obfuscatedKey.Length}, expected 16");

            if (_cacheService is not null)
            {
                _ = _cacheService
                    .SetPlayPlayObfuscatedKeyAsync(fileId, obfuscatedKey, CancellationToken.None);
            }
        }

        var contentId16 = fileId.Raw.AsSpan(0, 16).ToArray();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var proxy = _proxyResolver()
                ?? throw new InvalidOperationException(
                    "AudioHost is not connected; PlayPlay derivation unavailable until the audio process is up");

            _logger?.LogDebug("Requesting PlayPlay derivation from AudioHost for {FileId}", fileId);
            var aes = await proxy.DerivePlayPlayKeyAsync(
                obfuscatedKey, contentId16, _spotifyDllPath, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("PlayPlay key derived via AudioHost for {FileId}", fileId);
            return aes;
        }
        finally
        {
            _gate.Release();
        }
    }
}
