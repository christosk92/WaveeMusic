using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Backend.Spotify;
using Wavee.Core.Catalog;

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

    /// <summary>The backing key/value store. Exposed so the remembered-session-scope store
    /// (<see cref="LocalSessionScopeStore"/>) writes into the SAME instance: FileLocalStore rewrites the whole file on
    /// every Set, so a second instance over the same path would clobber this one's keys.</summary>
    public ILocalStore Store => _store;

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

// ── The remembered session context ───────────────────────────────────────────────────────────────────────────────────
// The reusable credential answers "who was signed in". This answers "under WHICH catalog context their library was
// cached" — and a cold start needs both, before any network call. The durable replicas are keyed by (account, storage
// account), and every cached catalog row is keyed by the WHOLE CatalogScope: locale, market, catalogue, tier, explicit
// filter and the context-known flag are all part of its storage identity. Recalling the exact scope the last session
// installed is therefore what lets a launch serve the user's own cached library — playlist names and covers included —
// instead of skeleton rows until the AP welcome lands a second later, and what lets that welcome CONFIRM the scope the
// app is already serving rather than replace it.
//
// Written by the live session host the moment a session installs, forgotten with the credential on logout, and
// protected by the SAME at-rest protector the credential uses (it names an account).

public interface ISessionScopeStore
{
    void Remember(CatalogScope scope);
    CatalogScope? Recall();
    void Forget();
}

public sealed class LocalSessionScopeStore : ISessionScopeStore
{
    /// <summary>The ILocalStore key the remembered scope is stored under — public for the same reason
    /// <see cref="LocalCredentialStore.CredentialKey"/> is: probes must not duplicate the literal.</summary>
    public const string ScopeKey = "spotify.session.scope";

    readonly ILocalStore _store;
    readonly ICredentialProtector _protector;

    public LocalSessionScopeStore(ILocalStore store, ICredentialProtector protector)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public void Remember(CatalogScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var dto = new SessionScopeDto(scope.Provider, scope.ProviderAccount, scope.Locale, scope.Market,
            scope.Catalogue, scope.Tier, scope.ExplicitFilter, scope.ContextKnown, scope.StorageAccount);
        var json = JsonSerializer.Serialize(dto, CredentialJson.Default.SessionScopeDto);
        var blob = _protector.Protect(Encoding.UTF8.GetBytes(json));
        _store.Set(ScopeKey, _protector.Scheme + ":" + Convert.ToBase64String(blob));   // scheme-tagged, exactly like the credential
    }

    /// <summary>The last installed scope, or null when there is none, it was written by a different at-rest protector
    /// (a moved machine/platform), it does not decode, or it never named an account. Never throws: an unreadable
    /// memory is a launch without a provisional scope, not a failed launch.</summary>
    public CatalogScope? Recall()
    {
        var raw = _store.Get(ScopeKey);
        if (string.IsNullOrEmpty(raw)) return null;
        int idx = raw.IndexOf(':');
        if (idx < 0 || raw[..idx] != _protector.Scheme) return null;
        try
        {
            var json = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(raw[(idx + 1)..])));
            var dto = JsonSerializer.Deserialize(json, CredentialJson.Default.SessionScopeDto);
            if (dto is null || dto.Provider.Length == 0 || dto.ProviderAccount.Length == 0) return null;
            return new CatalogScope(dto.Provider, dto.ProviderAccount, dto.Locale, dto.Market, dto.Catalogue,
                dto.Tier, dto.ExplicitFilter, dto.ContextKnown, dto.StorageAccount);
        }
        catch { return null; }
    }

    public void Forget() => _store.Remove(ScopeKey);
}

internal sealed record SessionScopeDto(string Provider, string ProviderAccount, string Locale, string Market,
    string Catalogue, int Tier, bool ExplicitFilter, bool ContextKnown, string StorageAccount);

[JsonSerializable(typeof(CredentialDto))]
[JsonSerializable(typeof(SessionScopeDto))]
internal sealed partial class CredentialJson : JsonSerializerContext { }
