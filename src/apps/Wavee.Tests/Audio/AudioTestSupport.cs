using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;

namespace Wavee.Tests.Audio;

/// <summary>Fakes + helpers for the audio resilience/error/UX tests.</summary>
static class A
{
    public static byte[] Bytes(byte seed, int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)(seed + i); return b; }
    public static byte[] Key16(byte seed) { var b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = (byte)(seed * 31 + i); return b; }
    public static byte[] File20() => Bytes(5, 20);
    public static byte[] Gid16() => Bytes(9, 16);

    public static SessionContext Ctx() => new("acct", "US", "premium", "en", Tier.Premium, false);

    public static PlayPlayConfig Config(byte[]? playPlayToken = null, string version = "1.2.93.667", Architecture arch = Architecture.X64) => new(
        Version: version,
        Arch: arch,
        Sha256: new byte[32],
        PlayPlayToken: playPlayToken ?? new byte[16],
        VmInitValue: new byte[16],
        AnalysisBase: 0x180000000,
        VmRuntimeInitVa: 0x180001000,
        VmObjectTransformVa: 0x180002000,
        RuntimeContextVa: 0x180003000,
        RuntimeContextSecondaryVa: 0x180003100,
        InitVtableLabs: Array.Empty<ulong>(),
        TransformFourthArgTemplateVa: 0,
        TransformFourthArgBuildVa: 0,
        FillRandomBytesVa: 0x180004000,
        AesKey: new AesKeyExtraction.OutputBufferSlice(0, 16),
        VmObjectSize: 144,
        RtContextSize: 16,
        DerivedKeySize: 32,
        ObfuscatedKeySize: 16,
        InitValueSize: 16,
        ContentIdSize: 16,
        KeySize: 16);

    public static RuntimeAsset Asset() => new("C:\\fake\\Spotify.dll", Config(), "test");
}

sealed class FakeApKeySource : IAudioKeySource
{
    public int Calls;
    public byte[]? Key;          // non-null → success
    public Exception? Throw;     // non-null → throw (permanent-ish AP failure)
    public int DelayMs;
    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
        if (Throw is not null) throw Throw;
        return Key is not null ? Key : ReadOnlyMemory<byte>.Empty;   // empty → resolver falls through to PlayPlay
    }
}

sealed class FakeLicense : ILicenseClient
{
    public int Calls;
    public byte[]? Obf;
    public byte[]? Aux;
    public byte[]? Request { get; set; }
    public byte[]? Raw { get; set; }
    public AudioKeyFailureReason Reason = AudioKeyFailureReason.None;

    public async Task<(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason)> FetchObfuscatedKeyAsync(string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        var result = await FetchLicenseAsync(fileIdHex, config, ct).ConfigureAwait(false);
        return (result.Key, result.Reason);
    }

    public Task<PlayPlayLicenseResult> FetchLicenseAsync(string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new PlayPlayLicenseResult(Obf ?? Array.Empty<byte>(), Aux ?? Array.Empty<byte>(), Reason, "",
            Raw ?? Array.Empty<byte>(), Request ?? Array.Empty<byte>()));
    }
}

sealed class FakeDeriver : IPlayPlayKeyDeriver
{
    public int Calls;
    public int FailUntilCalls;
    public byte[]? Key;
    public byte[]? NativeCdnSeed;
    public byte[]? LastAux;
    public byte[]? LastLicenseRaw;
    public byte[]? LastLicenseRequest;
    public AudioKeyFailureReason Reason = AudioKeyFailureReason.None;
    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
        => DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, default, ct);

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux, CancellationToken ct = default)
        => DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, playPlayAux, default, default, ct);

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux, ReadOnlyMemory<byte> licenseRaw, ReadOnlyMemory<byte> licenseRequest, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        LastAux = playPlayAux.ToArray();
        LastLicenseRaw = licenseRaw.ToArray();
        LastLicenseRequest = licenseRequest.ToArray();
        if (Calls <= FailUntilCalls)
            return Task.FromResult(new PlayPlayDeriveResult(default, Reason, "fail-until"));
        return Task.FromResult(new PlayPlayDeriveResult(Key ?? default, Reason, NativeCdnSeed: NativeCdnSeed ?? default));
    }
}

sealed class RecordingAudioHost : IAudioHost
{
    public bool LoadFastStartCalled, PlayCalled, SupplyBodyCalled, StopCalled;
    public int LoadCalls;
    public readonly List<long> Seeks = [];
    public readonly TaskCompletionSource SupplyBodySignaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource StopSignaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Wavee.Core.SimpleSubject<AudioHostSignal> _sig = new();

    public void Load(in AudioStreamHandle s) { LoadCalls++; }
    public void LoadFastStart(in AudioFastStart s) => LoadFastStartCalled = true;
    public void SupplyBody(in AudioStreamHandle b) { SupplyBodyCalled = true; SupplyBodySignaled.TrySetResult(); }
    public void Play() => PlayCalled = true;
    public void Pause() { }
    public void Stop() { StopCalled = true; PlayCalled = false; StopSignaled.TrySetResult(); }
    public void Seek(long ms, SeekMode mode) { PositionMs = ms; Seeks.Add(ms); }
    public void SetVolume(double v) { }
    public long PositionMs { get; set; }
    public bool IsPlaying => PlayCalled;
    public bool IsBuffering => false;
    public bool ClockValid => true;
    public IObservable<AudioHostSignal> Signals => _sig;
    public void Emit(AudioHostSignal signal) => _sig.OnNext(signal);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class SuccessfulResolver : ITrackResolver
{
    public int Calls;
    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new AudioStreamHandle(track.Uri, "file", "https://cdn.test/audio", A.Key16(1),
            AudioFormat.OggVorbis160, track.DurationMs, 0));
    }
}

sealed class FakeFastResolver : IFastTrackResolver
{
    readonly FastStartPlan _plan;
    public int Calls;
    public FakeFastResolver(FastStartPlan plan) => _plan = plan;
    public Task<FastStartPlan> ResolveFastAsync(Wavee.Core.Track track, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(_plan);
    }
}

sealed class ThrowingResolver : ITrackResolver
{
    readonly AudioKeyFailureReason _reason;
    public int Calls;
    public ThrowingResolver(AudioKeyFailureReason reason) => _reason = reason;
    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        throw new AudioPlaybackException(_reason, "boom");
    }
}

sealed class FakeHttpExchange : IHttpExchange
{
    public Func<HttpReq, HttpResp> Responder = _ => new HttpResp(404, new Dictionary<string, string>(), Array.Empty<byte>());
    public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct) => Task.FromResult(Responder(req));
}

sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<string, (HttpStatusCode Status, byte[] Body)> Responder = _ => (HttpStatusCode.NotFound, Array.Empty<byte>());
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body) = Responder(request.RequestUri!.ToString());
        var resp = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return Task.FromResult(resp);
    }
}
