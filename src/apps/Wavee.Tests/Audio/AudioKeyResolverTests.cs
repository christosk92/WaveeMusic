using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Backend.Persistence;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class AudioKeyResolverTests
{
    static AudioKeyResolver Make(FakeApKeySource ap, FakeDeriver? der, FakeLicense? lic, Func<RuntimeAsset?> runtime, AudioRuntimeStatusService status, LicenseKeyDiskCache? licenseDisk = null)
        => new(ap, () => der, runtime, lic, status, A.Ctx, licenseDisk: licenseDisk);

    [Fact]
    public async Task ApSuccess_ReturnsApKey_WithoutPlayPlay()
    {
        var ap = new FakeApKeySource { Key = A.Key16(1) };
        var der = new FakeDeriver(); var lic = new FakeLicense();
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var key = await r.GetKeyAsync(A.File20(), A.Gid16());

        Assert.Equal(A.Key16(1), key.ToArray());
        Assert.Equal(1, ap.Calls);
        Assert.Equal(0, lic.Calls);   // PlayPlay never consulted when AP works
        Assert.Equal(0, der.Calls);
    }

    [Fact]
    public async Task ApRefused_FallsBackToPlayPlay()
    {
        var ap = new FakeApKeySource { Throw = new IOException("ap refused") };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var key = await r.GetKeyAsync(A.File20(), A.Gid16());

        Assert.Equal(A.Key16(7), key.ToArray());
        Assert.Equal(1, lic.Calls);
        Assert.Equal(1, der.Calls);
    }

    [Fact]
    public async Task PlayPlayFallback_PassesAuxiliaryField_ToDeriver()
    {
        var ap = new FakeApKeySource { Throw = new IOException("ap refused") };
        var aux = new byte[] { 0x52, 0xe5, 0xc8, 0x2e };
        var lic = new FakeLicense { Obf = new byte[16], Aux = aux };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        await r.GetKeyAsync(A.File20(), A.Gid16());

        Assert.Equal(aux, der.LastAux);
    }

    [Fact]
    public async Task NoDeriver_ThrowsNeverProvisioned()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var r = Make(ap, der: null, lic: null, A.Asset, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.NeverProvisioned, ex.Reason);
    }

    [Fact]
    public async Task DeriverButNoPack_ThrowsProvisioningUnavailable()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var r = Make(ap, new FakeDeriver(), new FakeLicense(), runtime: () => null, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.ProvisioningUnavailable, ex.Reason);
    }

    [Fact]
    public async Task License403_IsTyped_AndSetsStatus()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Reason = AudioKeyFailureReason.License403 };
        var status = new AudioRuntimeStatusService();
        var r = Make(ap, new FakeDeriver(), lic, A.Asset, status);

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));

        Assert.Equal(AudioKeyFailureReason.License403, ex.Reason);
        Assert.Equal(AudioKeyFailureReason.License403, status.Reason);   // surfaced, not silent
        Assert.True(status.HasIssue);
    }

    [Fact]
    public async Task DeriveEmulationFault_IsTyped()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Reason = AudioKeyFailureReason.EmulationFault };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(AudioKeyFailureReason.EmulationFault, ex.Reason);
    }

    [Fact]
    public async Task Latch_SkipsPlayPlay_AfterAFailure_UntilProbeWindow()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Reason = AudioKeyFailureReason.License403 };
        var r = Make(ap, new FakeDeriver(), lic, A.Asset, new AudioRuntimeStatusService());

        await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(1, lic.Calls);

        // Second immediate attempt: the per-file latch is engaged → PlayPlay is skipped and the LAST real reason surfaces
        // (License403), not a generic code. (AP is also disabled session-wide after its first failure.)
        var ex2 = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.GetKeyAsync(A.File20(), A.Gid16()));
        Assert.Equal(1, lic.Calls);   // NOT consulted again
        Assert.Equal(AudioKeyFailureReason.License403, ex2.Reason);
    }

    [Fact]
    public async Task ApDisabledForSession_AfterFirstFailure_SkipsApOnLaterTracks()
    {
        var ap = new FakeApKeySource { Throw = new IOException("audio key error (code 1)") };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        // Track 1: AP is tried once (fails) → PlayPlay.
        var k1 = await r.GetKeyAsync(A.Bytes(1, 20), A.Bytes(1, 16));
        Assert.Equal(A.Key16(7), k1.ToArray());
        Assert.Equal(1, ap.Calls);

        // Track 2 (different file): AP is latched off for the session → NOT re-probed; straight to PlayPlay.
        var k2 = await r.GetKeyAsync(A.Bytes(2, 20), A.Bytes(2, 16));
        Assert.Equal(A.Key16(7), k2.ToArray());
        Assert.Equal(1, ap.Calls);    // AP not consulted again this session
        Assert.Equal(2, der.Calls);   // PlayPlay derived for both tracks
    }

    [Fact]
    public async Task ConcurrentDifferentFiles_ShareTheFailingApProbe()
    {
        var ap = new FakeApKeySource
        {
            Throw = new IOException("audio key error (code 1)"),
            DelayMs = 80,
        };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var keys = await Task.WhenAll(
            r.GetKeyAsync(A.Bytes(1, 20), A.Bytes(1, 16)),
            r.GetKeyAsync(A.Bytes(2, 20), A.Bytes(2, 16)));

        Assert.Equal(1, ap.Calls);
        Assert.All(keys, key => Assert.Equal(A.Key16(7), key.ToArray()));
    }

    [Fact]
    public async Task ConcurrentRequests_ForSameFile_Coalesce_ToOneApCall()
    {
        var ap = new FakeApKeySource { Key = A.Key16(3), DelayMs = 80 };
        var r = Make(ap, new FakeDeriver(), new FakeLicense(), A.Asset, new AudioRuntimeStatusService());

        var file = A.File20(); var gid = A.Gid16();
        var tasks = Enumerable.Range(0, 6).Select(_ => r.GetKeyAsync(file, gid)).ToArray();
        var keys = await Task.WhenAll(tasks);

        Assert.Equal(1, ap.Calls);   // Resource in-flight dedup
        Assert.All(keys, k => Assert.Equal(A.Key16(3), k.ToArray()));
    }

    [Fact]
    public async Task SequentialRequests_SameFile_ServeCachedKey()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        var file = A.File20();
        var gid = A.Gid16();
        var k1 = await r.GetKeyAsync(file, gid);
        var k2 = await r.GetKeyAsync(file, gid);

        Assert.Equal(k1.ToArray(), k2.ToArray());
        Assert.Equal(1, lic.Calls);
        Assert.Equal(1, der.Calls);
    }

    [Fact]
    public async Task SequentialRequests_DifferentFiles_FetchEach()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());

        await r.GetKeyAsync(A.Bytes(1, 20), A.Bytes(1, 16));
        await r.GetKeyAsync(A.Bytes(2, 20), A.Bytes(2, 16));

        Assert.Equal(2, der.Calls);
    }

    [Fact]
    public async Task NativeSeed_SurvivesKeyCacheHit()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var seed = new byte[] { 1, 2, 3, 4 };
        var der = new FakeDeriver { Key = A.Key16(7), NativeCdnSeed = seed };
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService());
        var file = A.File20();
        var fileHex = Convert.ToHexStringLower(file);

        await r.GetKeyAsync(file, A.Gid16());
        await r.GetKeyAsync(file, A.Gid16());

        Assert.Equal(1, der.Calls);
        Assert.Equal(seed, r.GetNativeCdnSeed(fileHex).ToArray());
    }

    [Fact]
    public async Task DiskHit_SecondResolver_SkipsLicense_DerivesOnly()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16], Raw = A.Bytes(1, 4), Request = A.Bytes(2, 8) };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var db = Path.Combine(Path.GetTempPath(), "wavee-key-res-" + Guid.NewGuid() + ".db");
        using var disk = new LicenseKeyDiskCache(db, new NoOpProtector());
        var file = A.File20();
        var fileHex = Convert.ToHexStringLower(file);

        var warm = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService(), disk);
        await warm.GetKeyAsync(file, A.Gid16());
        Assert.Equal(1, lic.Calls);

        lic.Calls = 0;
        der.Calls = 0;
        using var disk2 = new LicenseKeyDiskCache(db, new NoOpProtector());
        var cold = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService(), disk2);
        await cold.GetKeyAsync(file, A.Gid16());

        Assert.Equal(0, lic.Calls);
        Assert.Equal(1, der.Calls);
        Assert.True(disk2.TryLoad(fileHex, out _));
    }

    [Fact]
    public async Task StaleDiskPayload_DeriveFail_SelfHealsWithOneLicenseFetch()
    {
        var ap = new FakeApKeySource { Throw = new IOException() };
        var lic = new FakeLicense { Obf = new byte[16] };
        var der = new FakeDeriver { Key = A.Key16(7) };
        var db = Path.Combine(Path.GetTempPath(), "wavee-key-heal-" + Guid.NewGuid() + ".db");
        using var disk = new LicenseKeyDiskCache(db, new NoOpProtector());
        var file = A.File20();
        disk.Save(Convert.ToHexStringLower(file), new PlayPlayLicenseResult(A.Key16(9), default,
            AudioKeyFailureReason.None, "", default, default));

        der.FailUntilCalls = 1;
        der.Reason = AudioKeyFailureReason.None;
        der.Key = A.Key16(7);
        var r = Make(ap, der, lic, A.Asset, new AudioRuntimeStatusService(), disk);

        var key = await r.GetKeyAsync(file, A.Gid16());

        Assert.Equal(A.Key16(7), key.ToArray());
        Assert.Equal(1, lic.Calls);
        Assert.Equal(2, der.Calls);
    }
}
