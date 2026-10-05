// ── Wavee.Tests/AiLyricsPackTests.cs — the download's per-part verification and the runtime keep-loaded check ────────
//
// PartHashes over synthetic parts fed in odd chunk sizes (a part boundary inside a chunk, a chunk inside a part), and
// SameContent over temp files. No network.

using System.Security.Cryptography;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsPackTests
{
    static (byte[] File, AiLyrics.PackPart[] Parts) ThreeParts()
    {
        var rng = new Random(7);
        int[] sizes = [10, 7, 5];
        var file = new byte[sizes.Sum()];
        rng.NextBytes(file);
        var parts = new AiLyrics.PackPart[sizes.Length];
        int at = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            parts[i] = new AiLyrics.PackPart("p" + i, sizes[i], Convert.ToHexStringLower(SHA256.HashData(file.AsSpan(at, sizes[i]))));
            at += sizes[i];
        }
        return (file, parts);
    }

    static long Feed(AiLyrics.PartHashes check, byte[] file, int chunk)
    {
        for (int at = 0; at < file.Length; at += chunk)
        {
            long bad = check.Append(file.AsSpan(at, Math.Min(chunk, file.Length - at)));
            if (bad >= 0) return bad;
        }
        return -1;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(64)]
    public void Every_part_verifies_whatever_the_chunking(int chunk)
    {
        var (file, parts) = ThreeParts();
        using var check = new AiLyrics.PartHashes(parts);
        Assert.Equal(-1, Feed(check, file, chunk));
        Assert.Equal(3, check.Verified);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(64)]
    public void A_corrupt_part_reports_where_it_starts(int chunk)
    {
        var (file, parts) = ThreeParts();
        file[12] ^= 0xFF;                                                       // inside the second part (bytes 10..16)
        using var check = new AiLyrics.PartHashes(parts);
        Assert.Equal(10, Feed(check, file, chunk));
        Assert.Equal(1, check.Verified);
    }

    [Fact]
    public void An_unfinished_part_is_not_judged_yet()
    {
        var (file, parts) = ThreeParts();
        file[20] ^= 0xFF;                                                       // the third part, which never completes
        using var check = new AiLyrics.PartHashes(parts);
        Assert.Equal(-1, check.Append(file.AsSpan(0, 21)));
        Assert.Equal(2, check.Verified);
    }

    [Fact]
    public void Manifest_hashes_compare_without_regard_to_case()
    {
        var (file, parts) = ThreeParts();
        var upper = parts.Select(p => p with { Sha256 = p.Sha256.ToUpperInvariant() }).ToArray();
        using var check = new AiLyrics.PartHashes(upper);
        Assert.Equal(-1, check.Append(file));
    }

    [Fact]
    public void SameContent_needs_equal_bytes_not_just_equal_lengths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.dll"), b = Path.Combine(dir, "b.dll"), c = Path.Combine(dir, "c.dll");
            File.WriteAllBytes(a, [1, 2, 3, 4]);
            File.WriteAllBytes(b, [1, 2, 3, 4]);
            File.WriteAllBytes(c, [1, 2, 3, 5]);
            Assert.True(AiLyrics.Pack.SameContent(a, b));
            Assert.False(AiLyrics.Pack.SameContent(a, c));
            Assert.False(AiLyrics.Pack.SameContent(a, Path.Combine(dir, "missing.dll")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
