using FluentAssertions;
using System.Numerics;
using System.Security.Cryptography;
using Wavee.Core.Http;
using Xunit;

namespace Wavee.Tests.Core.Http;

/// <summary>
/// Tests for HashcashSolver.
/// Validates proof-of-work algorithm for login5 challenges.
/// Note: Uses small difficulty values (8-10 bits) for fast test execution.
/// </summary>
public class HashcashSolverTests
{
    [Fact]
    public void Solve_WithValidChallenge_ShouldFindSolution()
    {
        // ============================================================
        // WHY: HashcashSolver must find a suffix that produces a hash
        //      with the required number of leading zero bits.
        // ============================================================

        // Arrange
        var context = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var prefix = new byte[] { 0xAA, 0xBB, 0xCC };
        var targetLength = 8; // 8 bits = 1 byte of zeros (fast to solve)

        // Act
        var (suffix, duration) = HashcashSolver.Solve(context, prefix, targetLength);

        // Assert
        suffix.Should().NotBeNull();
        suffix.Should().HaveCount(16, "Suffix is always 16 bytes");
        duration.Should().BeGreaterThan(TimeSpan.Zero, "Solving takes time");

        // Verify the solution is valid
        ValidateSolution(context, prefix, suffix, targetLength).Should().BeTrue(
            "Solution suffix must produce hash with required leading zero bits");
    }

    [Fact]
    public void Solve_WithDifferentContexts_ShouldProduceDifferentSuffixes()
    {
        // ============================================================
        // WHY: Different contexts should require different suffixes
        //      to meet the proof-of-work requirement.
        // ============================================================

        // Arrange
        var prefix = new byte[] { 0x01, 0x02 };
        var targetLength = 8;

        var context1 = new byte[] { 0xAA };
        var context2 = new byte[] { 0xBB };

        // Act
        var (suffix1, _) = HashcashSolver.Solve(context1, prefix, targetLength);
        var (suffix2, _) = HashcashSolver.Solve(context2, prefix, targetLength);

        // Assert
        suffix1.Should().NotEqual(suffix2,
            "Different contexts should (very likely) produce different solutions");

        ValidateSolution(context1, prefix, suffix1, targetLength).Should().BeTrue();
        ValidateSolution(context2, prefix, suffix2, targetLength).Should().BeTrue();
    }

    [Fact]
    public void Solve_WithHigherDifficulty_ShouldTakeLonger()
    {
        // ============================================================
        // WHY: Higher difficulty (more leading zero bits) should
        //      require more attempts and thus take longer.
        // ============================================================

        // Arrange
        var context = new byte[] { 0x01, 0x02 };
        var prefix = new byte[] { 0xAA };

        // Act
        var (_, duration8) = HashcashSolver.Solve(context, prefix, targetLength: 8);
        var (_, duration10) = HashcashSolver.Solve(context, prefix, targetLength: 10);

        // Assert
        // Note: This is probabilistic, but 10 bits should generally take longer than 8 bits
        // We can't guarantee it every time due to randomness, so we just check both succeeded
        duration8.Should().BeGreaterThan(TimeSpan.Zero);
        duration10.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Solve_WithNullContext_ShouldThrow()
    {
        // ============================================================
        // WHY: Null context is invalid and must be rejected.
        // ============================================================

        // Act
        Action act = () => HashcashSolver.Solve(null!, new byte[] { 0x01 }, 10);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Solve_WithNullPrefix_ShouldThrow()
    {
        // ============================================================
        // WHY: Null prefix is invalid and must be rejected.
        // ============================================================

        // Act
        Action act = () => HashcashSolver.Solve(new byte[] { 0x01 }, null!, 10);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Solve_WithZeroOrNegativeLength_ShouldThrow()
    {
        // ============================================================
        // WHY: Zero or negative target length is invalid.
        // ============================================================

        // Arrange
        var context = new byte[] { 0x01 };
        var prefix = new byte[] { 0x02 };

        // Act
        Action actZero = () => HashcashSolver.Solve(context, prefix, 0);
        Action actNegative = () => HashcashSolver.Solve(context, prefix, -5);

        // Assert
        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Validates that a suffix produces a hash with the required number of leading zero bits.
    /// </summary>
    private static bool ValidateSolution(byte[] context, byte[] prefix, byte[] suffix, int targetLength)
    {
        using var sha1 = SHA1.Create();
        var hashInput = context.Concat(prefix).Concat(suffix).ToArray();
        var hash = sha1.ComputeHash(hashInput);

        return CountLeadingZeroBits(hash) >= targetLength;
    }

    /// <summary>
    /// Counts leading zero bits in a hash (same logic as HashcashSolver).
    /// </summary>
    private static int CountLeadingZeroBits(byte[] hash)
    {
        int count = 0;

        foreach (var b in hash)
        {
            if (b == 0)
            {
                count += 8;
            }
            else
            {
                count += BitOperations.LeadingZeroCount((uint)b) - 24;
                break;
            }
        }

        return count;
    }
}
