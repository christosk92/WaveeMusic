using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Backend.Spotify;

namespace Wavee.Backend.Persistence;

// ── Portable credential persistence ──────────────────────────────────────────────────────────────────────────────────
// The STORE is portable (over ILocalStore). At-rest encryption is a SWAPPABLE seam (ICredentialProtector): NoOp by default
// (cross-platform), with DPAPI (Windows) / Keychain (macOS) / Credential-Vault as platform swaps — never required. A blob
// carries its protector's scheme tag, so a credential protected on one machine/platform is cleanly rejected (→ re-auth)
// rather than mis-decrypted on another.

public interface ICredentialProtector
{
    string Scheme { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>The portable default — no OS keystore needed (the file lives in the user-only profile dir). Swap a platform
/// protector in for at-rest encryption.</summary>
public sealed class NoOpProtector : ICredentialProtector
{
    public string Scheme => "none";
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}

public interface ICredentialStore
{
    void Save(Credential credential);
    Credential? Load();
    void Clear();
}

public sealed class LocalCredentialStore : ICredentialStore
{
    /// <summary>The ILocalStore key the credential blob is stored under. PUBLIC so the fresh-install probe
    /// (<c>SidebarBootstrap.IsFreshInstall</c>) can ask "are there stored credentials?" without duplicating the literal.</summary>
    public const string CredentialKey = "spotify.credential";

    readonly ILocalStore _store;
    readonly ICredentialProtector _protector;

    public LocalCredentialStore(ILocalStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    /// <summary>The at-rest protector's scheme tag (e.g. "dpapi" / "none"), surfaced for logging + the LoginResult.</summary>
    public string Scheme => _protector.Scheme;

    /// <summary>The at-rest protector itself. Exposed so a second protected store (the per-module secret store the
    /// playback-module host hands to <c>host/secrets/*</c>) reuses the ONE platform selection rather than repeating
    /// the DPAPI / Keychain / NoOp ladder and drifting from it.</summary>
    public ICredentialProtector Protector => _protector;

    public void Save(Credential c)
    {
        var dto = new CredentialDto(c.Kind.ToString(), c.Username, c.Secret, c.Refresh);
        var json = JsonSerializer.Serialize(dto, CredentialJson.Default.CredentialDto);
        var blob = _protector.Protect(Encoding.UTF8.GetBytes(json));
        _store.Set(CredentialKey, _protector.Scheme + ":" + Convert.ToBase64String(blob));   // scheme-tagged
    }

    public Credential? Load()
    {
        var raw = _store.Get(CredentialKey);
        if (string.IsNullOrEmpty(raw)) return null;
        int idx = raw.IndexOf(':');
        if (idx < 0 || raw[..idx] != _protector.Scheme) return null;   // different scheme (moved machine/platform) → re-auth
        try
        {
            var json = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(raw[(idx + 1)..])));
            var dto = JsonSerializer.Deserialize(json, CredentialJson.Default.CredentialDto);
            if (dto is null) return null;
            return new Credential(Enum.Parse<CredentialKind>(dto.Kind), dto.Username, dto.Secret, null, dto.Refresh);
        }
        catch { return null; }
    }

    public void Clear() => _store.Remove(CredentialKey);
}

internal sealed record CredentialDto(string Kind, string Username, string Secret, string? Refresh);

[JsonSerializable(typeof(CredentialDto))]
internal sealed partial class CredentialJson : JsonSerializerContext { }
