using System.IO;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class LocalStoreTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Set_Get_Remove()
    {
        var f = TempFile();
        try
        {
            var s = new FileLocalStore(f);
            Assert.Null(s.Get("k"));
            s.Set("k", "v");
            Assert.Equal("v", s.Get("k"));
            s.Remove("k");
            Assert.Null(s.Get("k"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Persists_AcrossInstances()
    {
        var f = TempFile();
        try
        {
            new FileLocalStore(f).Set("token", "abc123");
            Assert.Equal("abc123", new FileLocalStore(f).Get("token"));   // a fresh instance reads the same file
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void CorruptFile_DoesNotThrow_AndStartsEmpty()
    {
        var f = TempFile();
        try
        {
            File.WriteAllText(f, "{ this is not valid json");
            var s = new FileLocalStore(f);
            Assert.Null(s.Get("k"));
            s.Set("k", "v");   // recovers
            Assert.Equal("v", s.Get("k"));
        }
        finally { File.Delete(f); }
    }
}

public class CredentialStoreTests
{
    sealed class XorProtector : ICredentialProtector
    {
        public string Scheme => "xor";
        public byte[] Protect(byte[] p) { var r = (byte[])p.Clone(); for (int i = 0; i < r.Length; i++) r[i] ^= 0x5A; return r; }
        public byte[] Unprotect(byte[] c) => Protect(c);
    }

    sealed class MemStore : ILocalStore
    {
        readonly Dictionary<string, string> _d = new();
        public string? Get(string k) => _d.TryGetValue(k, out var v) ? v : null;
        public void Set(string k, string v) => _d[k] = v;
        public void Remove(string k) => _d.Remove(k);
    }

    static Credential Sample => new(CredentialKind.ReusableBlob, "31testuser000000000000000000", Convert.ToBase64String([1, 2, 3, 4]), null, "rt");

    [Fact]
    public void Save_Load_RoundTrips()
    {
        var store = new LocalCredentialStore(new MemStore(), new NoOpProtector());
        Assert.Null(store.Load());
        store.Save(Sample);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(Sample.Username, loaded!.Username);
        Assert.Equal(CredentialKind.ReusableBlob, loaded.Kind);
        Assert.Equal(Sample.Secret, loaded.Secret);
        Assert.Equal("rt", loaded.Refresh);
    }

    [Fact]
    public void Clear_Removes()
    {
        var store = new LocalCredentialStore(new MemStore(), new NoOpProtector());
        store.Save(Sample);
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Protector_IsApplied_NotPlaintextOnDisk()
    {
        var mem = new MemStore();
        new LocalCredentialStore(mem, new XorProtector()).Save(Sample);
        var raw = mem.Get("spotify.credential");
        Assert.NotNull(raw);
        Assert.StartsWith("xor:", raw);
        Assert.DoesNotContain(Sample.Username, raw);   // username isn't visible in the persisted blob
    }

    [Fact]
    public void Protected_RoundTrips_ThroughSameProtector()
    {
        var mem = new MemStore();
        new LocalCredentialStore(mem, new XorProtector()).Save(Sample);
        var loaded = new LocalCredentialStore(mem, new XorProtector()).Load();
        Assert.Equal(Sample.Username, loaded!.Username);
    }

    [Fact]
    public void DifferentProtectorScheme_LoadReturnsNull_SoReAuth()
    {
        var mem = new MemStore();
        new LocalCredentialStore(mem, new XorProtector()).Save(Sample);
        Assert.Null(new LocalCredentialStore(mem, new NoOpProtector()).Load());   // can't read another scheme's blob → re-auth
    }

    [Fact]
    public void EndToEnd_OverFileStore_IsPortable()
    {
        var f = Path.Combine(Path.GetTempPath(), "wavee-cred-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new LocalCredentialStore(new FileLocalStore(f), new NoOpProtector()).Save(Sample);
            var loaded = new LocalCredentialStore(new FileLocalStore(f), new NoOpProtector()).Load();   // survives a restart
            Assert.Equal(Sample.Username, loaded!.Username);
        }
        finally { File.Delete(f); }
    }
}
