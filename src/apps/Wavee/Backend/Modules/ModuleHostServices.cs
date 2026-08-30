using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Persistence;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.Backend.Modules;

// ── HOST SERVICES — the module→host half of the protocol, permission-gated ───────────────────────────────────────────
// A module may ask the host for things only the host has: a per-module secret, the app's bearer token for a provider it
// owns, an audio key off the AP socket. Every one of them is DECLARED in the manifest and CHECKED on every call — a
// module that did not declare a permission gets a typed refusal, never the data.
//
// The SDK's ModuleManifest carries no separate `permissions` array, so the permission vocabulary rides the capability
// list under the `permission:` prefix (see ModuleCapabilities.PermissionPrefix): one declared list, one gate, no second
// schema for a publisher to keep in step. `host/secrets/get|set` is registered here; the Spotify-shaped services
// (`host/auth/token`, `host/auth/context`, `spotify/audioKey`) are registered by the live session at go-live, through
// the same Register call, so nothing Spotify-named lives in this file.

/// <summary>Per-module secret storage. The app's credential protector encrypts at rest; keys are namespaced per module
/// so one module can never read another's.</summary>
public interface IModuleSecretStore
{
    /// <summary>Read a module's secret, or null when nothing is stored.</summary>
    /// <param name="moduleId">The module id.</param>
    /// <param name="key">The module-private key.</param>
    byte[]? Get(string moduleId, string key);

    /// <summary>Write a module's secret.</summary>
    /// <param name="moduleId">The module id.</param>
    /// <param name="key">The module-private key.</param>
    /// <param name="value">The bytes to protect.</param>
    void Set(string moduleId, string key, byte[] value);
}

/// <summary>The real secret store: the app's <see cref="ILocalStore"/> under a per-module key prefix, encrypted at rest
/// by the same <see cref="ICredentialProtector"/> the Spotify credential uses (DPAPI on Windows, no-op elsewhere).</summary>
public sealed class ProtectedModuleSecretStore : IModuleSecretStore
{
    const string Prefix = "module.secret.";

    readonly ILocalStore _store;
    readonly ICredentialProtector _protector;

    /// <summary>Build the store.</summary>
    /// <param name="store">The app's local key/value store.</param>
    /// <param name="protector">The at-rest protector.</param>
    public ProtectedModuleSecretStore(ILocalStore store, ICredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        _store = store;
        _protector = protector;
    }

    /// <inheritdoc/>
    public byte[]? Get(string moduleId, string key)
    {
        string? raw = _store.Get(KeyFor(moduleId, key));
        if (string.IsNullOrEmpty(raw)) return null;
        int idx = raw.IndexOf(':');
        if (idx < 0 || raw[..idx] != _protector.Scheme) return null;   // written by another scheme/machine → treat as absent
        try { return _protector.Unprotect(Convert.FromBase64String(raw[(idx + 1)..])); }
        catch { return null; }
    }

    /// <inheritdoc/>
    public void Set(string moduleId, string key, byte[] value)
        => _store.Set(KeyFor(moduleId, key),
            _protector.Scheme + ":" + Convert.ToBase64String(_protector.Protect(value ?? [])));

    static string KeyFor(string moduleId, string key) => Prefix + moduleId + "." + key;
}

/// <summary>The registry of module→host services, and the permission gate in front of them.</summary>
public sealed class ModuleHostServices
{
    /// <summary>The permission a module must declare to use <c>host/secrets/get|set</c>.</summary>
    public const string StoragePrivatePermission = "storage.private";

    readonly Lock _gate = new();
    readonly Dictionary<string, Registration> _services = new(StringComparer.Ordinal);

    /// <summary>Build the registry.</summary>
    /// <param name="secrets">The secret store backing <c>host/secrets/*</c>; null leaves those methods unregistered
    /// (a module then sees <c>-32601</c>, which the SDK reads as "the host does not offer this").</param>
    public ModuleHostServices(IModuleSecretStore? secrets = null)
    {
        if (secrets is null) return;
        Register(ModuleMethods.SecretsGet, StoragePrivatePermission,
            SdkJsonContext.Default.SecretGetParams, SdkJsonContext.Default.SecretGetResult,
            (moduleId, p, _) => ValueTask.FromResult(new SecretGetResult(secrets.Get(moduleId, p.Key))));
        Register(ModuleMethods.SecretsSet, StoragePrivatePermission,
            SdkJsonContext.Default.SecretSetParams, SdkJsonContext.Default.RpcUnit,
            (moduleId, p, _) => { secrets.Set(moduleId, p.Key, p.Value); return ValueTask.FromResult(RpcUnit.Value); });
    }

    /// <summary>Raised when the set of services changes, so live connections can be re-installed.</summary>
    public event Action? Changed;

    /// <summary>Register (or replace) one host service.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">The wire method, e.g. <c>host/auth/token</c>.</param>
    /// <param name="permission">The permission a manifest must declare; "" means "no permission needed".</param>
    /// <param name="paramsInfo">Source-generated params type info.</param>
    /// <param name="resultInfo">Source-generated result type info.</param>
    /// <param name="handler">The implementation; the first argument is the CALLING module's id.</param>
    public void Register<TParams, TResult>(string method, string permission,
        JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo,
        Func<string, TParams, CancellationToken, ValueTask<TResult>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _services[method] = new Registration(permission ?? "", (module, conn, perm) =>
                conn.OnRequest(method, paramsInfo, resultInfo, (p, ct) =>
                {
                    if (perm.Length > 0 && !ModuleCapabilities.HasPermission(module.Manifest, perm))
                        throw new ModuleException(ModuleErrorCode.Unsupported,
                            module.Id + " did not declare the '" + perm + "' permission");
                    return handler(module.Id, p, ct);
                }));
        }

        Changed?.Invoke();
    }

    /// <summary>Remove a host service (a live session tearing its own services down at logout).</summary>
    /// <param name="method">The wire method to drop.</param>
    public void Unregister(string method)
    {
        bool removed;
        lock (_gate) removed = _services.Remove(method);
        if (removed) Changed?.Invoke();
    }

    /// <summary>Install every registered service onto one module's connection. Re-runnable: registering a handler for a
    /// method that already has one replaces it, which is how a go-live install reaches a module that is already up.</summary>
    /// <param name="module">The module the connection belongs to.</param>
    /// <param name="connection">The peer.</param>
    public void Install(InstalledModule module, JsonRpcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(connection);
        Registration[] snapshot;
        lock (_gate)
        {
            snapshot = new Registration[_services.Count];
            _services.Values.CopyTo(snapshot, 0);
        }

        for (int i = 0; i < snapshot.Length; i++) snapshot[i].Install(module, connection, snapshot[i].Permission);
    }

    /// <summary>The registered method names (diagnostics).</summary>
    public IReadOnlyList<string> Methods
    {
        get
        {
            lock (_gate)
            {
                var names = new string[_services.Count];
                _services.Keys.CopyTo(names, 0);
                Array.Sort(names, StringComparer.Ordinal);
                return names;
            }
        }
    }

    readonly record struct Registration(string Permission, Action<InstalledModule, JsonRpcConnection, string> Install);
}
