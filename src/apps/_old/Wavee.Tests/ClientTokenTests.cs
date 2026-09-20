using System.Security.Cryptography;
using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class HashcashTests
{
    [Fact]
    public void Solve_ProducesSuffix_WhoseHashMeetsTheDifficulty()
    {
        var prefix = Convert.FromHexString("0123456789abcdef0123456789abcdef01234567");
        const int target = 8;   // 8 leading zero bits = first byte zero (fast: ~256 hashes on average)

        var suffix = HashcashSolver.Solve([], prefix, target);

        Assert.Equal(16, suffix.Length);
        // Re-derive and verify the property the server checks: SHA1(prefix || suffix) has >= target leading zero bits.
        var input = new byte[prefix.Length + suffix.Length];
        prefix.CopyTo(input, 0);
        suffix.CopyTo(input, prefix.Length);
        var hash = SHA1.HashData(input);
        Assert.True(HashcashSolver.CountLeadingZeroBits(hash) >= target);
    }

    [Fact]
    public void CountLeadingZeroBits_IsCorrect()
    {
        Assert.Equal(0, HashcashSolver.CountLeadingZeroBits(new byte[] { 0xFF }));
        Assert.Equal(8, HashcashSolver.CountLeadingZeroBits(new byte[] { 0x00, 0xFF }));
        Assert.Equal(12, HashcashSolver.CountLeadingZeroBits(new byte[] { 0x00, 0x0F }));   // 8 + 4
        Assert.Equal(1, HashcashSolver.CountLeadingZeroBits(new byte[] { 0x40 }));          // 0100_0000
    }
}
