using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>AP-first + PlayPlay-fallback key resolver with Resource-backed coalescing and a decaying per-file latch.</summary>
public sealed class AudioKeyResolver : IAudioKeySource, IPlayPlayNativeSeedSource
{
    readonly IAudioKeySource _ap;
    readonly Func<IPlayPlayKeyDeriver?> _playPlay;
    readonly Func<RuntimeAsset?> _runtime;
    readonly ILicenseClient? _license;
    readonly AudioRuntimeStatusService _status;
    readonly Resource<string, CachedKey> _cache;
    readonly ConcurrentDictionary<string, PendingGid> _pendingGids = new();
    readonly ConcurrentDictionary<string, byte[]> _nativeCdnSeeds = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, LatchState> _latch = new();
    readonly LicenseKeyDiskCache? _licenseDisk;
    readonly WaveeLogger _log;
    readonly SemaphoreSlim _apProbeGate = new(1, 1);
    volatile bool _apDisabled;   // AP audio-key is account-wide: one failure means it won't serve any track this session

    sealed record CachedKey(byte[] Key);
    sealed record PendingGid(byte[] TrackGid);
    sealed class LatchState { public int FailCount; public DateTime NextProbe = DateTime.MinValue; public AudioKeyFailureReason LastReason = AudioKeyFailureReason.Network; }

    public AudioKeyResolver(
        IAudioKeySource ap,
        Func<IPlayPlayKeyDeriver?> playPlay,
        Func<RuntimeAsset?> runtime,
        ILicenseClient? license,
        AudioRuntimeStatusService status,
        Func<SessionContext> ctx,
        WaveeLogger log = default,
        LicenseKeyDiskCache? licenseDisk = null)
    {
        _ap = ap;
        _playPlay = playPlay;
        _runtime = runtime;
        _license = license;
        _status = status;
        _log = log;
        _licenseDisk = licenseDisk;
        _cache = new Resource<string, CachedKey>(FetchKeyAsync, new FreshnessPolicy.Immutable(), ctx,
            name: "audio.key", debugLog: log);
    }

    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        string fileHex = Convert.ToHexStringLower(fileId.Span);
        string cacheKey = fileHex + ":" + Convert.ToHexStringLower(trackGid.Span);
        _pendingGids[cacheKey] = new PendingGid(trackGid.ToArray());
        Event(WaveeLogLevel.Debug, "key.request", "Audio key requested",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("trackGidBytes", trackGid.Length),
            WaveeLogField.Of("apDisabled", _apDisabled));

        if (_latch.TryGetValue(fileHex, out var latch) && DateTime.UtcNow < latch.NextProbe)
        {
            var peek = _cache.Peek(cacheKey);
            if (peek.IsReady)
            {
                Event(WaveeLogLevel.Debug, "key.cache.hit", "Audio key cache hit under latch",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                    WaveeLogField.Of("reason", latch.LastReason.ToString()));
                return peek.Value!.Key;
            }
        }

        var loaded = await _cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
        if (loaded.IsReady) return loaded.Value!.Key;

        var reason = loaded.Error is { Length: > 0 } err && Enum.TryParse<AudioKeyFailureReason>(err, out var r)
            ? r : AudioKeyFailureReason.Network;
        Event(WaveeLogLevel.Warning, "key.request.failed", "Audio key request failed",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("reason", reason.ToString()),
            WaveeLogField.Of("detail", loaded.Error ?? ""));
        _status.SetKeyFailure(reason, loaded.Error);
        throw new AudioPlaybackException(reason, loaded.Error);
    }

    async Task<CachedKey> FetchKeyAsync(string cacheKey, SessionContext ctx)
    {
        try
        {
            return await FetchKeyCoreAsync(cacheKey, ctx).ConfigureAwait(false);
        }
        finally
        {
            _pendingGids.TryRemove(cacheKey, out _);
        }
    }

    async Task<CachedKey> FetchKeyCoreAsync(string cacheKey, SessionContext ctx)
    {
        var sep = cacheKey.IndexOf(':');
        var fileHex = sep > 0 ? cacheKey[..sep] : cacheKey;
        var fileId = Convert.FromHexString(fileHex);
        var trackGid = _pendingGids.TryGetValue(cacheKey, out var pg) ? pg.TrackGid : fileId.AsSpan(0, Math.Min(16, fileId.Length)).ToArray();

        // AP audio-key is account-wide: if it can't serve keys for this account it won't for ANY track, so try it ONCE per
        // session and, on the first failure/empty, latch it off — every later track goes straight to PlayPlay (no re-probe).
        if (!_apDisabled)
        {
            await _apProbeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_apDisabled) goto PlayPlay;
                var key = await _ap.GetKeyAsync(fileId, trackGid, CancellationToken.None).ConfigureAwait(false);
                if (!key.IsEmpty)
                {
                    _log.Info($"key {fileHex}: AP path OK");
                    Event(WaveeLogLevel.Info, "key.ap.ok", "AP audio-key path returned a key",
                        WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                        WaveeLogField.Of("keyBytes", key.Length));
                    _status.ClearKeyFailure();
                    return new CachedKey(key.ToArray());
                }
                _log.Info($"key {fileHex}: AP returned empty → AP disabled for this session; PlayPlay from now on");
                Event(WaveeLogLevel.Warning, "key.ap.empty", "AP audio-key path returned empty; disabling AP for this session",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)));
                _apDisabled = true;
            }
            catch (Exception ex)
            {
                _log.Info($"key {fileHex}: AP failed ({ex.Message}) → AP disabled for this session; PlayPlay from now on");
                Event(WaveeLogLevel.Warning, "key.ap.failed", "AP audio-key path failed; disabling AP for this session",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                    WaveeLogField.Of("error", ex.GetType().Name),
                    WaveeLogField.Of("detail", ex.Message));
                _apDisabled = true;
            }
            finally { _apProbeGate.Release(); }
        }

    PlayPlay:
        var asset = _runtime();
        var deriver = _playPlay();
        if (asset is null || deriver is null || _license is null)
        {
            var r = deriver is null ? AudioKeyFailureReason.NeverProvisioned : AudioKeyFailureReason.ProvisioningUnavailable;
            _log.Info($"key {fileHex}: PlayPlay unavailable → {r} (pack={(asset is not null)}, deriver={(deriver is not null)}, license={(_license is not null)})");
            Event(WaveeLogLevel.Error, "key.playplay.unavailable", "PlayPlay cannot derive this key because a dependency is missing",
                WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                WaveeLogField.Of("reason", r.ToString()),
                WaveeLogField.Of("pack", asset is not null),
                WaveeLogField.Of("deriver", deriver is not null),
                WaveeLogField.Of("license", _license is not null));
            NoteLatch(fileHex, r);
            throw new InvalidOperationException(r.ToString());
        }

        // Per-file decaying latch: after repeated PlayPlay failures for THIS file, back off (surface the last reason).
        if (!ShouldRetryPlayPlay(fileHex))
        {
            var last = _latch.TryGetValue(fileHex, out var s0) ? s0.LastReason : AudioKeyFailureReason.Network;
            _log.Info($"key {fileHex}: PlayPlay backing off (recent failure) → {last}");
            Event(WaveeLogLevel.Warning, "key.playplay.backoff", "PlayPlay key derivation is backing off for this file",
                WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                WaveeLogField.Of("reason", last.ToString()));
            throw new InvalidOperationException(last.ToString());
        }

        if (_licenseDisk?.TryLoad(fileHex, out var diskLic) == true)
        {
            _log.Info($"key {fileHex}: license disk hit — derive without network");
            try { return await DeriveFromLicenseAsync(fileHex, fileId, diskLic, asset, deriver).ConfigureAwait(false); }
            catch { _licenseDisk.Invalidate(fileHex); }
        }

        _log.Info($"key {fileHex}: PlayPlay step A — fetching obfuscated key from spclient");
        Event(WaveeLogLevel.Info, "key.playplay.license.start", "Fetching PlayPlay license",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("pack", asset.PackId),
            WaveeLogField.Of("arch", asset.Config.Arch.ToString()));
        var lic = await _license.FetchLicenseAsync(fileHex, asset.Config, CancellationToken.None).ConfigureAwait(false);
        if (lic.Reason != AudioKeyFailureReason.None)
        {
            _log.Info($"key {fileHex}: obfuscated-key fetch FAILED → {lic.Reason}");
            Event(WaveeLogLevel.Error, "key.playplay.license.failed", "PlayPlay license fetch failed",
                WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                WaveeLogField.Of("reason", lic.Reason.ToString()));
            NoteLatch(fileHex, lic.Reason);
            throw new InvalidOperationException(lic.Reason.ToString());
        }
        _licenseDisk?.Save(fileHex, lic);
        return await DeriveFromLicenseAsync(fileHex, fileId, lic, asset, deriver).ConfigureAwait(false);
    }

    async Task<CachedKey> DeriveFromLicenseAsync(string fileHex, byte[] fileId, PlayPlayLicenseResult lic,
        RuntimeAsset asset, IPlayPlayKeyDeriver deriver)
    {
        var contentId = fileId.AsSpan(0, Math.Min(16, fileId.Length)).ToArray();
        var auxHex = lic.Auxiliary.IsEmpty ? "" : $" aux={lic.Auxiliary.Length}B";
        _log.Info($"key {fileHex}: PlayPlay step B — deriving in native host ({asset.Config.Arch}){auxHex}");
        Event(WaveeLogLevel.Info, "key.playplay.derive.start", "Deriving PlayPlay AES key",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("obf", lic.Key.Length),
            WaveeLogField.Of("aux", lic.Auxiliary.Length),
            WaveeLogField.Of("raw", lic.RawBody.Length));
        var derive = await deriver.DeriveAsync(lic.Key, contentId, asset.Config, asset.PackPath,
            correlationId: fileHex, lic.Auxiliary, lic.RawBody, lic.RequestBody, CancellationToken.None).ConfigureAwait(false);
        if (!derive.Ok)
        {
            _log.Info($"key {fileHex}: derive FAILED → {derive.Reason} {derive.Detail}");
            Event(WaveeLogLevel.Error, "key.playplay.derive.failed", "PlayPlay AES derivation failed",
                WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
                WaveeLogField.Of("reason", derive.Reason.ToString()),
                WaveeLogField.Of("detail", derive.Detail ?? ""));
            NoteLatch(fileHex, derive.Reason);
            throw new InvalidOperationException(derive.Reason.ToString());
        }
        if (!derive.NativeCdnSeed.IsEmpty)
            _nativeCdnSeeds[fileHex] = derive.NativeCdnSeed.ToArray();

        _log.Info($"key {fileHex}: PlayPlay path OK (aes={derive.Key.Length}B redacted, nativeSeed={derive.NativeCdnSeed.Length}B)");
        Event(WaveeLogLevel.Info, "key.playplay.ok", "PlayPlay produced an AES key",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("keyBytes", derive.Key.Length),
            WaveeLogField.Of("nativeSeedBytes", derive.NativeCdnSeed.Length));
        _status.ClearKeyFailure();
        return new CachedKey(derive.Key.ToArray());
    }

    public ReadOnlyMemory<byte> GetNativeCdnSeed(string fileIdHex) =>
        _nativeCdnSeeds.TryGetValue(fileIdHex, out var seed) ? seed : default;

    bool ShouldRetryPlayPlay(string fileHex) =>
        !_latch.TryGetValue(fileHex, out var s) || DateTime.UtcNow >= s.NextProbe;

    void NoteLatch(string fileHex, AudioKeyFailureReason reason)
    {
        var s = _latch.GetOrAdd(fileHex, _ => new LatchState());
        s.LastReason = reason;
        var fails = Interlocked.Increment(ref s.FailCount);
        s.NextProbe = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * fails));
        Event(WaveeLogLevel.Debug, "key.latch.set", "Audio-key failure latch updated",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileHex)),
            WaveeLogField.Of("reason", reason.ToString()),
            WaveeLogField.Of("failures", fails),
            WaveeLogField.Of("backoffSeconds", Math.Min(300, 5 * fails)));
    }

    void Event(WaveeLogLevel level, string eventId, string message, params WaveeLogField[] fields) =>
        _log.Event(level, eventId, message, fields: fields);
}
