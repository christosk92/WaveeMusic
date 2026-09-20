using System;
using System.IO;
using Wavee.Backend.Audio;
using Wavee.Backend.Persistence;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class LicenseKeyDiskCacheTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-lic-" + Guid.NewGuid() + ".db");

    [Fact]
    public void SaveAndLoad_RoundTripsPayload()
    {
        var db = TempDb();
        using (var cache = new LicenseKeyDiskCache(db, new NoOpProtector()))
        {
            var lic = new PlayPlayLicenseResult(A.Key16(1), A.Bytes(2, 4), AudioKeyFailureReason.None, "",
                A.Bytes(3, 8), A.Bytes(4, 12));
            cache.Save("abc123", lic);
            Assert.True(cache.TryLoad("abc123", out var loaded));
            Assert.Equal(lic.Key.ToArray(), loaded.Key.ToArray());
            Assert.Equal(lic.Auxiliary.ToArray(), loaded.Auxiliary.ToArray());
            Assert.Equal(lic.RawBody.ToArray(), loaded.RawBody.ToArray());
            Assert.Equal(lic.RequestBody.ToArray(), loaded.RequestBody.ToArray());
        }
    }

    [Fact]
    public void SchemeMismatch_IsMiss()
    {
        var db = TempDb();
        using (var cache = new LicenseKeyDiskCache(db, new NoOpProtector()))
        {
            cache.Save("x", new PlayPlayLicenseResult(A.Key16(1), default, AudioKeyFailureReason.None, "",
                default, default));
        }
        using var other = new LicenseKeyDiskCache(db, new WrongSchemeProtector());
        Assert.False(other.TryLoad("x", out _));
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var db = TempDb();
        using var cache = new LicenseKeyDiskCache(db, new NoOpProtector());
        cache.Save("y", new PlayPlayLicenseResult(A.Key16(2), default, AudioKeyFailureReason.None, "",
            default, default));
        cache.Invalidate("y");
        Assert.False(cache.TryLoad("y", out _));
    }

    sealed class WrongSchemeProtector : ICredentialProtector
    {
        public string Scheme => "wrong";
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] cipher) => cipher;
    }
}
