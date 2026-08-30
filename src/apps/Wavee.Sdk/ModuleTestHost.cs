using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wavee.Sdk.Protocol;

namespace Wavee.Sdk;

/// <summary>
/// An in-process <see cref="IModuleHost"/> that drives a <see cref="WaveeModule"/> directly — no process, no pipes,
/// no JSON. Module tests use it so a fixture-driven <c>Match</c>/<c>Resolve</c> is a plain method call; everything
/// the module pushes (metadata, status, progress, logs) is recorded in a list, and every host service is a settable
/// delegate so a test decides what the "app" answers.
/// </summary>
public sealed class ModuleTestHost : IModuleHost
{
    /// <summary>Wraps a module and attaches itself as its host.</summary>
    /// <param name="module">The module under test.</param>
    /// <param name="dataDir">The directory to report as <see cref="DataDir"/>; defaults to a temp path.</param>
    public ModuleTestHost(WaveeModule module, string? dataDir = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        Module = module;
        DataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "wavee-module-test");
        module.AttachHost(this);
    }

    /// <summary>The module under test.</summary>
    public WaveeModule Module { get; }

    /// <inheritdoc/>
    public string DataDir { get; }

    /// <summary>Every <see cref="IModuleHost.PublishMetadata"/> the module pushed, in order.</summary>
    public List<MetadataUpdate> Metadata { get; } = [];

    /// <summary>Every playable id the module declared expired, in order.</summary>
    public List<string> Expired { get; } = [];

    /// <summary>Every <see cref="ModuleStatus"/> the module pushed, in order.</summary>
    public List<ModuleStatus> Status { get; } = [];

    /// <summary>Every progress update the module pushed, in order.</summary>
    public List<ProgressNotification> Progress { get; } = [];

    /// <summary>Every log line the module wrote, in order.</summary>
    public List<LogNotification> Logs { get; } = [];

    /// <summary>What <see cref="GetTokenAsync"/> answers. Unset means "the host does not offer tokens".</summary>
    public Func<string, bool, CancellationToken, ValueTask<AuthToken>>? TokenProvider { get; set; }

    /// <summary>What <see cref="GetAuthContextAsync"/> answers. Unset means "the host does not offer a context".</summary>
    public Func<string, CancellationToken, ValueTask<AuthContext>>? AuthContextProvider { get; set; }

    /// <summary>What <see cref="GetSecretAsync"/> answers. Unset falls back to <see cref="Secrets"/>.</summary>
    public Func<string, CancellationToken, ValueTask<byte[]?>>? SecretReader { get; set; }

    /// <summary>What <see cref="SetSecretAsync"/> does. Unset falls back to <see cref="Secrets"/>.</summary>
    public Func<string, byte[], CancellationToken, ValueTask>? SecretWriter { get; set; }

    /// <summary>The in-memory secret store used when <see cref="SecretReader"/> / <see cref="SecretWriter"/> are unset.</summary>
    public Dictionary<string, byte[]> Secrets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What <see cref="CallAsync{TParams,TResult}"/> answers, as JSON in and JSON out: <c>(method, paramsJson, ct)</c>
    /// returns the result JSON. Keeping it textual keeps the test host free of reflection.
    /// </summary>
    public Func<string, string, CancellationToken, ValueTask<string>>? CallHandler { get; set; }

    // ---- driving the module --------------------------------------------------------------------------------------

    /// <summary>Calls <see cref="WaveeModule.InitializeAsync"/> with a neutral context.</summary>
    /// <param name="context">An explicit context, or null for a neutral one built from <see cref="DataDir"/>.</param>
    /// <param name="ct">Cancels initialization.</param>
    public async Task InitializeAsync(ModuleContext? context = null, CancellationToken ct = default)
        => await Module.InitializeAsync(
            context ?? new ModuleContext("test", ModuleProtocol.Version, DataDir, "en-US",
                ResolvePreferences.Default, 0), ct).ConfigureAwait(false);

    /// <summary>Calls <see cref="WaveeModule.MatchAsync"/> directly.</summary>
    /// <param name="input">The pasted input.</param>
    /// <param name="ct">Cancels the match.</param>
    public async Task<MatchResult?> MatchAsync(string input, CancellationToken ct = default)
        => await Module.MatchAsync(input, ct).ConfigureAwait(false);

    /// <summary>Calls the module's resolve directly, with no preferences.</summary>
    /// <param name="playableId">The module-private playable id.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public async Task<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct = default)
        => await Module.ResolveAsync(playableId, null, ct).ConfigureAwait(false);

    /// <summary>Calls the module's resolve directly, with the preferences the app would have sent.</summary>
    /// <param name="playableId">The module-private playable id.</param>
    /// <param name="prefs">The preferences to pass down.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public async Task<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
        CancellationToken ct = default)
        => await Module.ResolveAsync(playableId, prefs, ct).ConfigureAwait(false);

    /// <summary>Calls <see cref="WaveeModule.WarmAsync"/> directly.</summary>
    /// <param name="playableId">The playable to warm.</param>
    /// <param name="ct">Cancels the warm-up.</param>
    public async Task WarmAsync(string playableId, CancellationToken ct = default)
        => await Module.WarmAsync(playableId, ct).ConfigureAwait(false);

    /// <summary>Calls <see cref="WaveeModule.OpenStreamAsync"/> directly.</summary>
    /// <param name="streamId">The stream id from a resolved locator.</param>
    /// <param name="ct">Cancels the open.</param>
    public async Task<IModuleStream?> OpenStreamAsync(string streamId, CancellationToken ct = default)
        => await Module.OpenStreamAsync(streamId, ct).ConfigureAwait(false);

    /// <summary>
    /// Calls <see cref="WaveeModule.GetPageAsync"/> directly and runs the returned document through
    /// <see cref="ModulePageBudget.Validate"/> — the same gate <see cref="ModuleRunner"/> applies on the wire, so a
    /// fixture test catches an over-budget page exactly where the app would.
    /// </summary>
    /// <param name="entityId">The module-namespaced entity id.</param>
    /// <param name="ct">Cancels the fetch.</param>
    public async Task<ModulePageDoc?> PageAsync(string entityId, CancellationToken ct = default)
    {
        ModulePageDoc? doc = await Module.GetPageAsync(entityId, ct).ConfigureAwait(false);
        if (doc is not null) ModulePageBudget.Validate(doc);
        return doc;
    }

    /// <summary>Calls <see cref="WaveeModule.GetDiagnosticsAsync"/> directly.</summary>
    /// <param name="ct">Cancels the call.</param>
    public async Task<DiagnosticsReport> GetDiagnosticsAsync(CancellationToken ct = default)
        => await Module.GetDiagnosticsAsync(ct).ConfigureAwait(false);

    /// <summary>Calls <see cref="WaveeModule.InvokeActionAsync"/> directly.</summary>
    /// <param name="actionId">The action the user pressed.</param>
    /// <param name="ct">Cancels the action.</param>
    public async Task InvokeActionAsync(string actionId, CancellationToken ct = default)
        => await Module.InvokeActionAsync(actionId, ct).ConfigureAwait(false);

    // ---- IModuleHost ---------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void PublishMetadata(MetadataUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (Metadata) Metadata.Add(update);
    }

    /// <inheritdoc/>
    public void PublishExpired(string playableId)
    {
        lock (Expired) Expired.Add(playableId);
    }

    /// <inheritdoc/>
    public void PublishStatus(ModuleStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (Status) Status.Add(status);
    }

    /// <inheritdoc/>
    public void PublishProgress(string stage, double percent)
    {
        lock (Progress) Progress.Add(new ProgressNotification(stage, percent));
    }

    /// <inheritdoc/>
    public void Log(ModuleLogLevel level, string message)
    {
        lock (Logs) Logs.Add(new LogNotification(level, message));
    }

    /// <inheritdoc/>
    public ValueTask<AuthToken> GetTokenAsync(string provider, bool force, CancellationToken ct)
        => TokenProvider is { } f
            ? f(provider, force, ct)
            : throw new ModuleException(ModuleErrorCode.NeedsAuth, $"No token for '{provider}' in the test host.");

    /// <inheritdoc/>
    public ValueTask<AuthContext> GetAuthContextAsync(string provider, CancellationToken ct)
        => AuthContextProvider is { } f
            ? f(provider, ct)
            : throw new ModuleException(ModuleErrorCode.NeedsAuth, $"No auth context for '{provider}' in the test host.");

    /// <inheritdoc/>
    public ValueTask<byte[]?> GetSecretAsync(string key, CancellationToken ct)
    {
        if (SecretReader is { } f) return f(key, ct);
        lock (Secrets) return new ValueTask<byte[]?>(Secrets.TryGetValue(key, out byte[]? v) ? v : null);
    }

    /// <inheritdoc/>
    public ValueTask SetSecretAsync(string key, byte[] value, CancellationToken ct)
    {
        if (SecretWriter is { } f) return f(key, value, ct);
        lock (Secrets) Secrets[key] = value;
        return default;
    }

    /// <inheritdoc/>
    public async ValueTask<TResult> CallAsync<TParams, TResult>(string method, TParams p,
        JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken ct)
    {
        if (CallHandler is not { } handler)
        {
            throw new ModuleException(ModuleErrorCode.Unsupported, $"'{method}' is not wired in the test host.");
        }

        string request = JsonSerializer.Serialize(p, paramsInfo);
        string response = await handler(method, request, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(response, resultInfo)!;
    }
}
