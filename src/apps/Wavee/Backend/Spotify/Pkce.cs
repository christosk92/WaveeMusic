using System.Security.Cryptography;
using System.Text;

namespace Wavee.Backend.Spotify;

// ── LIFTED standard (RFC 7636 PKCE) — pure, deterministic, unit-tested against the RFC example vector ────────────────
// Used by the OAuth authorization-code provider. verifier = random [A-Za-z0-9-._~]{43..128}; challenge = base64url(SHA256(verifier)).
public static class Pkce
{
    const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public static string NewVerifier(int length = 64)
    {
        if (length is < 43 or > 128) throw new ArgumentOutOfRangeException(nameof(length));
        // GetItems is rejection-sampled → uniform over the 66-char set (the old `% 66` had a slight modulo bias).
        return new string(RandomNumberGenerator.GetItems<char>(Unreserved, length));
    }

    public static string Challenge(string verifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    static string Base64Url(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
