using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wavee.Sdk.Protocol;

namespace Wavee.Sdk;

/// <summary>
/// The whole of a module's <c>Main</c>. It owns the stdio loop, the JSON-RPC dispatch, per-request cancellation,
/// the open-stream handle table and shutdown — a module author writes a <see cref="WaveeModule"/> subclass and
/// <c>ModuleRunner.RunAsync&lt;T&gt;(args)</c>, nothing else.
/// <para>
/// The very first thing it does is point <see cref="Console.Out"/> at stderr: stdout is the protocol channel, so a
/// stray <c>Console.WriteLine</c> anywhere in module (or library) code cannot corrupt framing.
/// </para>
/// <para>
/// Without <c>--wavee-module</c> it behaves as a small CLI for manual testing — <c>match &lt;input&gt;</c> and
/// <c>resolve &lt;playableId&gt;</c> print the JSON answer to stdout and exit 0 (found) or 1 (not found / failed).
/// </para>
/// </summary>
public static class ModuleRunner
{
    /// <summary>The argument that tells a module it was launched by the app rather than by a human.</summary>
    public const string ModuleSwitch = "--wavee-module";

    private const int MaxReadBytes = 4 * 1024 * 1024;

    private static readonly string[] DefaultCapabilities = ["playback"];

    /// <summary>
    /// Runs <typeparamref name="T"/> as a module: JSON-RPC over stdin/stdout when the app launched it, the CLI
    /// subcommands otherwise.
    /// </summary>
    /// <typeparam name="T">The module class.</typeparam>
    /// <param name="args">The process arguments.</param>
    /// <param name="ct">Stops the module.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync<T>(string[] args, CancellationToken ct = default) where T : WaveeModule, new()
    {
        ArgumentNullException.ThrowIfNull(args);

        // Capture the REAL stdout before anything can write to it, then send Console.Out to stderr.
        Stream stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        var module = new T();
        if (Array.IndexOf(args, ModuleSwitch) < 0) return RunCliAsync(module, args, stdout, ct);

        Stream stdin = Console.OpenStandardInput();
        return RunAsync(module, stdin, stdout, ct);
    }

    /// <summary>
    /// Runs a module over an explicit duplex stream pair. This is the transport-agnostic entry point: the stdio
    /// overload is just this one wired to the standard handles, and tests drive it over in-memory pipes.
    /// </summary>
    /// <param name="module">The module instance (not yet attached to a host).</param>
    /// <param name="input">Where requests arrive.</param>
    /// <param name="output">Where responses are written.</param>
    /// <param name="ct">Stops the module.</param>
    /// <returns>0 on a clean end, 1 when the loop failed.</returns>
    public static async Task<int> RunAsync(WaveeModule module, Stream input, Stream output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        await using var connection = new JsonRpcConnection(input, output, negativeIds: true);
        var host = new RpcModuleHost(connection);
        module.AttachHost(host);
        using var streams = new StreamTable();
        Register(module, connection, host, streams);

        try
        {
            await connection.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR module loop failed: {ex}");
            return 1;
        }
    }

    // ---- wiring --------------------------------------------------------------------------------------------------

    private static void Register(WaveeModule module, JsonRpcConnection connection, RpcModuleHost host,
        StreamTable streams)
    {
        connection.OnRequest(ModuleMethods.Initialize, SdkJsonContext.Default.InitializeParams,
            SdkJsonContext.Default.InitializeResult, async (InitializeParams p, CancellationToken ct) =>
            {
                if (p is null) throw new ModuleException(ModuleErrorCode.Unsupported, "initialize params are missing.");

                int? negotiated = ModuleProtocol.Negotiate(p.MinProtocol, p.MaxProtocol);
                if (negotiated is not { } version)
                {
                    throw new ModuleException(ModuleErrorCode.Unsupported,
                        $"This module speaks protocol {ModuleProtocol.MinSupported}..{ModuleProtocol.Version}; " +
                        $"the host asked for {p.MinProtocol}..{p.MaxProtocol}.");
                }

                ModuleManifest? manifest = TryLoadManifest();
                if (!string.IsNullOrEmpty(p.DataDir)) host.DataDir = p.DataDir;
                var context = new ModuleContext(p.HostVersion, version, host.DataDir, p.Locale, p.Prefs,
                    p.CacheBudgetBytes);
                await module.InitializeAsync(context, ct).ConfigureAwait(false);
                return new InitializeResult(version, manifest?.Capabilities ?? DefaultCapabilities, manifest);
            });

        connection.OnRequest(ModuleMethods.Shutdown, SdkJsonContext.Default.RpcUnit, SdkJsonContext.Default.RpcUnit,
            async (RpcUnit _, CancellationToken ct) =>
            {
                connection.StopAfterCurrentResponse();
                try
                {
                    await module.ShutdownAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"WARN shutdown failed: {ex.Message}");
                }

                streams.Dispose();
                return RpcUnit.Value;
            });

        connection.OnRequest(ModuleMethods.Match, SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult,
            async (MatchParams p, CancellationToken ct) =>
                (await module.MatchAsync(p?.Input ?? string.Empty, ct).ConfigureAwait(false))!);

        connection.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams,
            SdkJsonContext.Default.ResolvedPlayable,
            (ResolveParams p, CancellationToken ct) => module.ResolveAsync(p.PlayableId, p.Prefs, ct));

        connection.OnRequest(ModuleMethods.Warm, SdkJsonContext.Default.WarmParams, SdkJsonContext.Default.RpcUnit,
            async (WarmParams p, CancellationToken ct) =>
            {
                await module.WarmAsync(p.PlayableId, ct).ConfigureAwait(false);
                return RpcUnit.Value;
            });

        connection.OnNotification(ModuleMethods.Warm, SdkJsonContext.Default.WarmParams,
            p => _ = WarmQuietlyAsync(module, p?.PlayableId));

        connection.OnRequest(ModuleMethods.StreamOpen, SdkJsonContext.Default.StreamOpenParams,
            SdkJsonContext.Default.StreamOpenResult, async (StreamOpenParams p, CancellationToken ct) =>
            {
                IModuleStream? stream = await module.OpenStreamAsync(p.StreamId, ct).ConfigureAwait(false);
                if (stream is null)
                {
                    throw new ModuleException(ModuleErrorCode.Unavailable,
                        $"The module does not serve a stream '{p.StreamId}'.");
                }

                string handle = streams.Add(stream);
                return new StreamOpenResult(handle, stream.Length, stream.Seekable, stream.ContentType);
            });

        // Answered with a raw binary frame: no base64, no JSON parse on the audio path.
        connection.OnBinaryRequest(ModuleMethods.StreamRead, SdkJsonContext.Default.StreamReadParams,
            async (StreamReadParams p, CancellationToken ct) =>
            {
                IModuleStream stream = streams.Get(p.Handle);
                int count = Math.Clamp(p.Count, 0, MaxReadBytes);
                if (count == 0) return new BinaryPayload(ReadOnlyMemory<byte>.Empty, false);

                var buffer = new byte[count];
                int n = await stream.ReadAsync(p.Offset, buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                if (n < 0) n = 0;
                bool eof = n == 0 || (stream.Length is { } length && p.Offset + n >= length);
                return new BinaryPayload(buffer.AsMemory(0, n), eof);
            });

        connection.OnRequest(ModuleMethods.StreamClose, SdkJsonContext.Default.StreamCloseParams,
            SdkJsonContext.Default.RpcUnit, (StreamCloseParams p, CancellationToken _) =>
            {
                streams.Remove(p.Handle);
                return new ValueTask<RpcUnit>(RpcUnit.Value);
            });

        connection.OnRequest(ModuleMethods.Page, SdkJsonContext.Default.PageParams,
            SdkJsonContext.Default.ModulePageDoc, async (PageParams p, CancellationToken ct) =>
            {
                ModulePageDoc? doc = await module.GetPageAsync(p?.EntityId ?? string.Empty, ct).ConfigureAwait(false);
                // Rejecting here is the point: an over-budget page must never reach the wire half-rendered.
                if (doc is not null) ModulePageBudget.Validate(doc);
                return doc!;
            });

        connection.OnRequest(ModuleMethods.Diagnostics, SdkJsonContext.Default.RpcUnit,
            SdkJsonContext.Default.DiagnosticsReport,
            (RpcUnit _, CancellationToken ct) => module.GetDiagnosticsAsync(ct));

        connection.OnRequest(ModuleMethods.Action, SdkJsonContext.Default.ModuleActionParams,
            SdkJsonContext.Default.ModuleActionResult, async (ModuleActionParams p, CancellationToken ct) =>
            {
                await module.InvokeActionAsync(p.Id, ct).ConfigureAwait(false);
                return new ModuleActionResult(true, null);
            });
    }

    private static async Task WarmQuietlyAsync(WaveeModule module, string? playableId)
    {
        if (string.IsNullOrEmpty(playableId)) return;
        try
        {
            await module.WarmAsync(playableId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN warm failed: {ex.Message}");
        }
    }

    /// <summary>Reads <c>wavee-module.json</c> from next to the entry point, when it is there.</summary>
    private static ModuleManifest? TryLoadManifest()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "wavee-module.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), SdkJsonContext.Default.ModuleManifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- CLI -----------------------------------------------------------------------------------------------------

    private static async Task<int> RunCliAsync(WaveeModule module, string[] args, Stream stdout, CancellationToken ct)
    {
        var host = new CliModuleHost();
        module.AttachHost(host);

        string command = args.Length > 0 ? args[0] : string.Empty;
        string argument = args.Length > 1 ? args[1] : string.Empty;
        if (command is not ("match" or "resolve" or "page") || argument.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: <module> match <input> | <module> resolve <playableId> | <module> page <entityId>");
            Console.Error.WriteLine($"       <module> {ModuleSwitch}   (spoken by the Wavee host over stdio)");
            return 2;
        }

        try
        {
            var context = new ModuleContext("cli", ModuleProtocol.Version, host.DataDir, "en-US",
                ResolvePreferences.Default, 0);
            await module.InitializeAsync(context, ct).ConfigureAwait(false);

            if (command == "match")
            {
                MatchResult? match = await module.MatchAsync(argument, ct).ConfigureAwait(false);
                WriteJsonLine(stdout, match!, SdkJsonContext.Default.MatchResult);
                return match is null ? 1 : 0;
            }

            if (command == "page")
            {
                ModulePageDoc? doc = await module.GetPageAsync(argument, ct).ConfigureAwait(false);
                if (doc is not null) ModulePageBudget.Validate(doc);
                WriteJsonLine(stdout, doc!, SdkJsonContext.Default.ModulePageDoc);
                return doc is null ? 1 : 0;
            }

            ResolvedPlayable resolved = await module.ResolveAsync(argument, ct).ConfigureAwait(false);
            WriteJsonLine(stdout, resolved, SdkJsonContext.Default.ResolvedPlayable);
            return 0;
        }
        catch (ModuleException ex)
        {
            Console.Error.WriteLine($"ERROR {ex.Code}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR {ex}");
            return 1;
        }
    }

    private static void WriteJsonLine<T>(Stream stdout, T value, JsonTypeInfo<T> info)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, info);
        stdout.Write(json);
        stdout.Write("\n"u8);
        stdout.Flush();
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>The open <see cref="IModuleStream"/> handles a module is serving right now.</summary>
    private sealed class StreamTable : IDisposable
    {
        private readonly ConcurrentDictionary<string, IModuleStream> _streams = new(StringComparer.Ordinal);
        private long _next;

        public string Add(IModuleStream stream)
        {
            string handle = string.Concat("s", Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture));
            _streams[handle] = stream;
            return handle;
        }

        public IModuleStream Get(string handle)
            => _streams.TryGetValue(handle ?? string.Empty, out IModuleStream? stream)
                ? stream
                : throw new ModuleException(ModuleErrorCode.Unsupported, $"Unknown stream handle '{handle}'.");

        public void Remove(string handle)
        {
            if (_streams.TryRemove(handle ?? string.Empty, out IModuleStream? stream)) Dispose(stream);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, IModuleStream> entry in _streams) Dispose(entry.Value);
            _streams.Clear();
        }

        private static void Dispose(IModuleStream stream)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN stream dispose failed: {ex.Message}");
            }
        }
    }

    /// <summary>The <see cref="IModuleHost"/> a running module sees: every member is a JSON-RPC message.</summary>
    private sealed class RpcModuleHost(JsonRpcConnection connection) : IModuleHost
    {
        public string DataDir { get; set; } =
            Environment.GetEnvironmentVariable("WAVEE_MODULE_DATA_DIR") ?? AppContext.BaseDirectory;

        public void PublishMetadata(MetadataUpdate update)
            => Send(ModuleMethods.Metadata, update, SdkJsonContext.Default.MetadataUpdate);

        public void PublishExpired(string playableId)
            => Send(ModuleMethods.Expired, new ExpiredNotification(playableId),
                SdkJsonContext.Default.ExpiredNotification);

        public void PublishStatus(ModuleStatus status)
            => Send(ModuleMethods.Status, status, SdkJsonContext.Default.ModuleStatus);

        public void PublishProgress(string stage, double percent)
            => Send(ModuleMethods.Progress, new ProgressNotification(stage, percent),
                SdkJsonContext.Default.ProgressNotification);

        public void Log(ModuleLogLevel level, string message)
            => Send(ModuleMethods.Log, new LogNotification(level, message), SdkJsonContext.Default.LogNotification);

        public ValueTask<AuthToken> GetTokenAsync(string provider, bool force, CancellationToken ct)
            => new(connection.RequestAsync(ModuleMethods.AuthToken, new AuthTokenParams(provider, force),
                SdkJsonContext.Default.AuthTokenParams, SdkJsonContext.Default.AuthToken, ct));

        public ValueTask<AuthContext> GetAuthContextAsync(string provider, CancellationToken ct)
            => new(connection.RequestAsync(ModuleMethods.AuthContext, new AuthContextParams(provider),
                SdkJsonContext.Default.AuthContextParams, SdkJsonContext.Default.AuthContext, ct));

        public async ValueTask<byte[]?> GetSecretAsync(string key, CancellationToken ct)
        {
            SecretGetResult result = await connection.RequestAsync(ModuleMethods.SecretsGet, new SecretGetParams(key),
                SdkJsonContext.Default.SecretGetParams, SdkJsonContext.Default.SecretGetResult, ct)
                .ConfigureAwait(false);
            return result?.Value;
        }

        public async ValueTask SetSecretAsync(string key, byte[] value, CancellationToken ct)
            => await connection.RequestAsync(ModuleMethods.SecretsSet, new SecretSetParams(key, value),
                SdkJsonContext.Default.SecretSetParams, SdkJsonContext.Default.RpcUnit, ct).ConfigureAwait(false);

        public ValueTask<TResult> CallAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
            JsonTypeInfo<TResult> resultInfo, CancellationToken ct)
            => new(connection.RequestAsync(method, p, paramsInfo, resultInfo, ct));

        private void Send<T>(string method, T payload, JsonTypeInfo<T> info)
        {
            try
            {
                connection.Notify(method, payload, info);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the host went away; a notification is best-effort by definition
            }
        }
    }

    /// <summary>The <see cref="IModuleHost"/> the CLI subcommands see: logs to stderr, no host services.</summary>
    private sealed class CliModuleHost : IModuleHost
    {
        public string DataDir { get; } =
            Environment.GetEnvironmentVariable("WAVEE_MODULE_DATA_DIR") ?? AppContext.BaseDirectory;

        public void PublishMetadata(MetadataUpdate update)
            => Console.Error.WriteLine($"metadata {update.PlayableId} {update.Title}");

        public void PublishExpired(string playableId) => Console.Error.WriteLine($"expired {playableId}");

        public void PublishStatus(ModuleStatus status)
            => Console.Error.WriteLine($"status {status.State} {status.Message}");

        public void PublishProgress(string stage, double percent)
            => Console.Error.WriteLine($"progress {stage} {percent.ToString("0.#", CultureInfo.InvariantCulture)}%");

        public void Log(ModuleLogLevel level, string message)
            => Console.Error.WriteLine($"{Prefix(level)}{message}");

        public ValueTask<AuthToken> GetTokenAsync(string provider, bool force, CancellationToken ct)
            => throw Unsupported(ModuleMethods.AuthToken);

        public ValueTask<AuthContext> GetAuthContextAsync(string provider, CancellationToken ct)
            => throw Unsupported(ModuleMethods.AuthContext);

        public ValueTask<byte[]?> GetSecretAsync(string key, CancellationToken ct)
            => throw Unsupported(ModuleMethods.SecretsGet);

        public ValueTask SetSecretAsync(string key, byte[] value, CancellationToken ct)
            => throw Unsupported(ModuleMethods.SecretsSet);

        public ValueTask<TResult> CallAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
            JsonTypeInfo<TResult> resultInfo, CancellationToken ct)
            => throw Unsupported(method);

        private static string Prefix(ModuleLogLevel level) => level switch
        {
            ModuleLogLevel.Warn => "WARN ",
            ModuleLogLevel.Error => "ERROR ",
            _ => string.Empty,
        };

        private static ModuleException Unsupported(string method)
            => new(ModuleErrorCode.Unsupported, $"'{method}' is not available outside the Wavee host.");
    }
}
