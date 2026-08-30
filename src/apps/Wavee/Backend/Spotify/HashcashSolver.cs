using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Wavee.Backend.Spotify;

// ── LIFTED protocol mechanic (a fixed PoW spec) ──────────────────────────────────────────────────────────────────────
// Spotify hashcash: find a 16-byte suffix such that SHA1(context || prefix || suffix) has at least `targetLength` LEADING
// zero BITS. Used by the client-token attestation (clienttoken.spotify.com) when the server answers a CLIENT_DATA request
// with a CHALLENGES response. Pure + property-testable (the returned suffix re-hashes to the target).
public static class HashcashSolver
{
    public static byte[] Solve(byte[] context, byte[] prefix, int targetLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetLength);

        var suffix = new byte[16];
        var input = new byte[context.Length + prefix.Length + 16];
        Buffer.BlockCopy(context, 0, input, 0, context.Length);
        Buffer.BlockCopy(prefix, 0, input, context.Length, prefix.Length);
        int suffixOffset = context.Length + prefix.Length;

        Span<byte> hash = stackalloc byte[20];   // SHA1 digest size
        while (true)
        {
            Random.Shared.NextBytes(suffix);     // a search, not secrecy — randomized so retries don't repeat the same path
            suffix.CopyTo(input.AsSpan(suffixOffset));
            SHA1.HashData(input, hash);
            if (CountLeadingZeroBits(hash) >= targetLength) return suffix;
        }
    }

    public static int CountLeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        int count = 0;
        foreach (var b in hash)
        {
            if (b == 0) { count += 8; continue; }
            count += BitOperations.LeadingZeroCount((uint)b) - 24;   // leading zeros within this single byte
            break;
        }
        return count;
    }
}
