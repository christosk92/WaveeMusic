using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace Wavee.Core.Http;

/// <summary>
/// Solves hashcash proof-of-work challenges for login5.
/// </summary>
/// <remarks>
/// Hashcash is a proof-of-work system used to prevent abuse.
/// The challenge is to find a suffix that, when combined with context and prefix,
/// produces a SHA-1 hash with a specified number of leading zero bits.
/// </remarks>
internal static class HashcashSolver
{
    /// <summary>
    /// Solves a hashcash challenge.
    /// </summary>
    /// <param name="context">Login context from the server.</param>
    /// <param name="prefix">Challenge prefix from the server.</param>
    /// <param name="targetLength">Required number of leading zero bits in the hash.</param>
    /// <returns>A tuple containing the solution suffix and the time taken to solve.</returns>
    public static (byte[] suffix, TimeSpan duration) Solve(
        byte[] context,
        byte[] prefix,
        int targetLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetLength);

        var stopwatch = Stopwatch.StartNew();
        var suffix = new byte[0x10]; // 16 bytes
        var random = Random.Shared;

        using var sha1 = SHA1.Create();

        // Pre-allocate buffer for hash input
        var hashInput = new byte[context.Length + prefix.Length + suffix.Length];
        Buffer.BlockCopy(context, 0, hashInput, 0, context.Length);
        Buffer.BlockCopy(prefix, 0, hashInput, context.Length, prefix.Length);

        var suffixOffset = context.Length + prefix.Length;

        while (true)
        {
            // Generate random suffix
            random.NextBytes(suffix);

            // Copy suffix into hash input buffer
            Buffer.BlockCopy(suffix, 0, hashInput, suffixOffset, suffix.Length);

            // Compute hash: SHA1(context || prefix || suffix)
            var hash = sha1.ComputeHash(hashInput);

            // Check if hash has enough leading zero bits
            if (CountLeadingZeroBits(hash) >= targetLength)
            {
                stopwatch.Stop();
                return (suffix, stopwatch.Elapsed);
            }
        }
    }

    /// <summary>
    /// Counts the number of leading zero bits in a byte array.
    /// </summary>
    /// <param name="hash">The hash to check.</param>
    /// <returns>Number of leading zero bits.</returns>
    private static int CountLeadingZeroBits(byte[] hash)
    {
        int count = 0;

        foreach (var b in hash)
        {
            if (b == 0)
            {
                // All 8 bits are zero
                count += 8;
            }
            else
            {
                // Count leading zeros in this byte
                // BitOperations.LeadingZeroCount works on uint, returns count for 32 bits
                // So we need to subtract 24 to get the count for the single byte
                count += BitOperations.LeadingZeroCount((uint)b) - 24;
                break; // Stop at first non-zero byte
            }
        }

        return count;
    }
}
