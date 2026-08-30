using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Wavee.Backend.Persistence;

namespace Wavee;

// macOS Keychain / Linux libsecret credential protector — the non-Windows swap behind ICredentialProtector. Keeps a per-app
// random AES-256 key in the OS keystore (via the `security` / `secret-tool` CLI) and AES-GCM-encrypts the credential blob
// with it, so the at-rest blob is ciphertext (not plaintext). Best-effort: IsAvailable() gates selection; if the keystore
// CLI is absent the caller falls back to NoOp (the file is still owner-only via 0600). Untested on Windows (OS-guarded);
// the actual keystore round-trip is verified on the target OS.
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
public sealed class KeyringProtector : ICredentialProtector
{
    const string Service = "wavee", Account = "credkey";

    public string Scheme => "keyring";

    /// <summary>True when the OS keystore CLI is present, so the caller can prefer this over NoOp.</summary>
    public static bool IsAvailable()
        => OperatingSystem.IsMacOS() ? Which("security") : OperatingSystem.IsLinux() && Which("secret-tool");

    public byte[] Protect(byte[] plaintext)
    {
        var key = GetOrCreateKey();
        Span<byte> nonce = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plaintext, cipher, tag);
        var result = new byte[12 + 16 + cipher.Length];   // [nonce | tag | ciphertext]
        nonce.CopyTo(result);
        tag.CopyTo(result.AsSpan(12));
        cipher.CopyTo(result.AsSpan(28));
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("keyring blob too short");
        var key = GetOrCreateKey();
        var plain = new byte[data.Length - 28];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }

    static byte[] GetOrCreateKey()
    {
        if (ReadKey() is { } existing) return existing;
        var key = RandomNumberGenerator.GetBytes(32);
        WriteKey(Convert.ToBase64String(key));
        return key;
    }

    static byte[]? ReadKey()
    {
        string? b64 = OperatingSystem.IsMacOS()
            ? Run("security", $"find-generic-password -s {Service} -a {Account} -w")
            : Run("secret-tool", $"lookup service {Service} account {Account}");
        b64 = b64?.Trim();
        try { return string.IsNullOrEmpty(b64) ? null : Convert.FromBase64String(b64); }
        catch { return null; }
    }

    static void WriteKey(string b64)
    {
        if (OperatingSystem.IsMacOS())
            Run("security", $"add-generic-password -U -s {Service} -a {Account} -w {b64}");
        else
            RunWithStdin("secret-tool", $"store --label=wavee service {Service} account {Account}", b64);
    }

    static bool Which(string tool)
    {
        try { return !string.IsNullOrWhiteSpace(Run("/usr/bin/which", tool)); } catch { return false; }
    }

    static string? Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? outp : null;
        }
        catch { return null; }
    }

    static void RunWithStdin(string file, string args, string stdin)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return;
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            p.WaitForExit(5000);
        }
        catch { }
    }
}
