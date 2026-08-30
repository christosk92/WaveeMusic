using System.Text.Json.Serialization.Metadata;

namespace Wavee.Sdk;

/// <summary>
/// The app, as a module sees it. Every member is transport-agnostic: <see cref="ModuleRunner"/> backs it with
/// JSON-RPC over stdio, <see cref="ModuleTestHost"/> backs it with plain in-process calls.
/// </summary>
public interface IModuleHost
{
    /// <summary>The module's private, writable directory.</summary>
    string DataDir { get; }

    /// <summary>Pushes a live "now playing" correction for a playable (ICY titles, the current show).</summary>
    /// <param name="update">The correction.</param>
    void PublishMetadata(MetadataUpdate update);

    /// <summary>Tells the host that a previously resolved locator has expired and must be resolved again.</summary>
    /// <param name="playableId">The playable whose locator died.</param>
    void PublishExpired(string playableId);

    /// <summary>Pushes the module's state, which drives the app's generic setup card.</summary>
    /// <param name="status">Ready / needs-setup / error, plus any offered actions.</param>
    void PublishStatus(ModuleStatus status);

    /// <summary>Pushes progress for a long-running setup step.</summary>
    /// <param name="stage">Short stage name, e.g. <c>"download"</c>.</param>
    /// <param name="percent">0..100; negative means indeterminate.</param>
    void PublishProgress(string stage, double percent);

    /// <summary>Writes a line to the app's log under the module's category.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="message">The line.</param>
    void Log(ModuleLogLevel level, string message);

    /// <summary>Asks the host for its current bearer token for a provider it owns (permission <c>auth.&lt;provider&gt;</c>).</summary>
    /// <param name="provider">Provider key, e.g. <c>"spotify"</c>.</param>
    /// <param name="force">True to force a refresh instead of taking the cached token.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<AuthToken> GetTokenAsync(string provider, bool force, CancellationToken ct);

    /// <summary>Asks the host for the non-secret session identity of a provider it owns.</summary>
    /// <param name="provider">Provider key, e.g. <c>"spotify"</c>.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<AuthContext> GetAuthContextAsync(string provider, CancellationToken ct);

    /// <summary>Reads a per-module secret from the app's credential protector (permission <c>storage.private</c>).</summary>
    /// <param name="key">Module-private key.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The stored bytes, or null when nothing is stored under that key.</returns>
    ValueTask<byte[]?> GetSecretAsync(string key, CancellationToken ct);

    /// <summary>Writes a per-module secret through the app's credential protector.</summary>
    /// <param name="key">Module-private key.</param>
    /// <param name="value">The bytes to protect.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask SetSecretAsync(string key, byte[] value, CancellationToken ct);

    /// <summary>
    /// Calls any other host service by method name (e.g. <c>"spotify/audioKey"</c>). AOT-safe: the caller supplies the
    /// source-generated type info for both sides.
    /// </summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">The host method name.</param>
    /// <param name="p">The params value.</param>
    /// <param name="paramsInfo">Source-generated type info for <typeparamref name="TParams"/>.</param>
    /// <param name="resultInfo">Source-generated type info for <typeparamref name="TResult"/>.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<TResult> CallAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo, CancellationToken ct);
}
