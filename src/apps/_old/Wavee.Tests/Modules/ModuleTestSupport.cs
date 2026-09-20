using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Modules;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Wavee.Tests.Sdk;

namespace Wavee.Tests.Modules;

/// <summary>
/// The in-memory module: a real <see cref="JsonRpcConnection"/> pair over <see cref="MemoryPipe"/>s with a SCRIPTED
/// module peer on the far side. Everything the host does — the handshake, match/resolve, the binary stream frames,
/// cancellation, a crash — goes over the real wire; nothing spawns a process.
/// </summary>
internal sealed class FakeModuleChannel : IModuleChannel
{
    private readonly MemoryPipe _hostToModule = new();
    private readonly MemoryPipe _moduleToHost = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly JsonRpcConnection _module;
    private Task _moduleLoop = Task.CompletedTask;

    public FakeModuleChannel(FakeModule script, int processId = 4242)
    {
        Script = script;
        ProcessId = processId;
        Connection = new JsonRpcConnection(_moduleToHost, _hostToModule);
        _module = new JsonRpcConnection(_hostToModule, _moduleToHost, negativeIds: true);
        script.Install(_module);
    }

    /// <summary>The scripted module behind this channel.</summary>
    public FakeModule Script { get; }

    /// <summary>The module peer, for a test that wants to push a notification host-ward.</summary>
    public JsonRpcConnection Module => _module;

    public JsonRpcConnection Connection { get; }

    public int ProcessId { get; }

    public bool HasExited { get; private set; }

    /// <summary>True once the host asked this channel to die.</summary>
    public bool Killed { get; private set; }

    public void Start() => _moduleLoop = _module.RunAsync(_cts.Token);

    /// <summary>Simulate the module process dying mid-session (a crash, a broken pipe).</summary>
    public void Crash()
    {
        HasExited = true;
        _cts.Cancel();
        _moduleToHost.CompleteWriting();
        _hostToModule.CompleteWriting();
    }

    public void Kill()
    {
        Killed = true;
        Crash();
    }

    public Task<bool> WaitForExitAsync(TimeSpan grace) => Task.FromResult(HasExited);

    public async ValueTask DisposeAsync()
    {
        Crash();
        await Connection.DisposeAsync();
        await _module.DisposeAsync();
        try { await _moduleLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* the loop is torn down; a cancellation race is not a failure */ }
    }
}

/// <summary>What the fake module answers. Every hook is optional; an unset one means "method not implemented".</summary>
internal sealed class FakeModule
{
    /// <summary>The handshake answer. Null makes <c>module/initialize</c> throw (a module that will not start).</summary>
    public Func<InitializeParams, InitializeResult>? Initialize { get; set; } =
        _ => new InitializeResult(1, ["playback", "match", "metadata"], null);

    /// <summary>The <c>playback/match</c> answer.</summary>
    public Func<MatchParams, MatchResult>? Match { get; set; }

    /// <summary>The <c>playback/resolve</c> answer.</summary>
    public Func<ResolveParams, ResolvedPlayable>? Resolve { get; set; }

    /// <summary>The bytes served over <c>stream/open|read|close</c>, keyed by stream id.</summary>
    public Dictionary<string, byte[]> Streams { get; } = new(StringComparer.Ordinal);

    /// <summary>Cap on a single <c>stream/read</c> answer, so short reads are exercised. 0 = no cap.</summary>
    public int MaxReadBytes { get; set; }

    /// <summary>Whether <c>stream/open</c> reports a length.</summary>
    public bool ReportLength { get; set; } = true;

    /// <summary>Whether <c>stream/open</c> reports seekability.</summary>
    public bool Seekable { get; set; } = true;

    /// <summary>The content type <c>stream/open</c> reports.</summary>
    public string? ContentType { get; set; }

    /// <summary>How many resolves the module was asked for (the dedupe test's evidence).</summary>
    public int ResolveCalls;

    /// <summary>How many initialize handshakes it saw (the restart tests' evidence).</summary>
    public int InitializeCalls;

    /// <summary>Open handles the module still holds.</summary>
    public readonly HashSet<string> OpenHandles = new(StringComparer.Ordinal);

    /// <summary>The last <c>playback/resolve</c> preferences the host sent.</summary>
    public ResolvePreferences? LastPrefs;

    /// <summary>Delay injected into every resolve, so a timeout can be exercised.</summary>
    public TimeSpan ResolveDelay { get; set; }

    private int _nextHandle;

    public void Install(JsonRpcConnection module)
    {
        module.OnRequest(ModuleMethods.Initialize, SdkJsonContext.Default.InitializeParams,
            SdkJsonContext.Default.InitializeResult, (InitializeParams p, CancellationToken _) =>
            {
                Interlocked.Increment(ref InitializeCalls);
                if (Initialize is not { } make)
                    throw new ModuleException(ModuleErrorCode.Unsupported, "this module refuses to start");
                return new ValueTask<InitializeResult>(make(p));
            });

        module.OnRequest(ModuleMethods.Shutdown, SdkJsonContext.Default.RpcUnit, SdkJsonContext.Default.RpcUnit,
            (RpcUnit _, CancellationToken _) => new ValueTask<RpcUnit>(RpcUnit.Value));

        module.OnRequest(ModuleMethods.Match, SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult,
            (MatchParams p, CancellationToken _) => Match is { } m
                ? new ValueTask<MatchResult>(m(p))
                : throw new ModuleException(ModuleErrorCode.NotOwned, "not mine"));

        module.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams,
            SdkJsonContext.Default.ResolvedPlayable, async (ResolveParams p, CancellationToken ct) =>
            {
                Interlocked.Increment(ref ResolveCalls);
                LastPrefs = p.Prefs;
                if (ResolveDelay > TimeSpan.Zero) await Task.Delay(ResolveDelay, ct);
                return Resolve is { } r
                    ? r(p)
                    : throw new ModuleException(ModuleErrorCode.Unavailable, "nothing to resolve");
            });

        module.OnRequest(ModuleMethods.StreamOpen, SdkJsonContext.Default.StreamOpenParams,
            SdkJsonContext.Default.StreamOpenResult, (StreamOpenParams p, CancellationToken _) =>
            {
                if (!Streams.TryGetValue(p.StreamId, out byte[]? bytes))
                    throw new ModuleException(ModuleErrorCode.Unavailable, "no such stream: " + p.StreamId);
                string handle = "h" + Interlocked.Increment(ref _nextHandle).ToString(System.Globalization.CultureInfo.InvariantCulture);
                lock (OpenHandles) OpenHandles.Add(handle);
                _byHandle[handle] = bytes;
                return new ValueTask<StreamOpenResult>(new StreamOpenResult(handle,
                    ReportLength ? bytes.Length : null, Seekable, ContentType));
            });

        module.OnBinaryRequest(ModuleMethods.StreamRead, SdkJsonContext.Default.StreamReadParams,
            (StreamReadParams p, CancellationToken _) =>
            {
                if (!_byHandle.TryGetValue(p.Handle, out byte[]? bytes))
                    throw new ModuleException(ModuleErrorCode.Unavailable, "no such handle: " + p.Handle);
                if (p.Offset >= bytes.Length) return new ValueTask<BinaryPayload>(new BinaryPayload(default, true));
                int want = p.Count;
                if (MaxReadBytes > 0) want = Math.Min(want, MaxReadBytes);
                int n = (int)Math.Min(want, bytes.Length - p.Offset);
                bool eof = p.Offset + n >= bytes.Length;
                return new ValueTask<BinaryPayload>(new BinaryPayload(bytes.AsMemory((int)p.Offset, n), eof));
            });

        module.OnRequest(ModuleMethods.StreamClose, SdkJsonContext.Default.StreamCloseParams,
            SdkJsonContext.Default.RpcUnit, (StreamCloseParams p, CancellationToken _) =>
            {
                lock (OpenHandles) OpenHandles.Remove(p.Handle);
                _byHandle.Remove(p.Handle);
                return new ValueTask<RpcUnit>(RpcUnit.Value);
            });
    }

    private readonly Dictionary<string, byte[]> _byHandle = new(StringComparer.Ordinal);
}

/// <summary>Builders for the manifests, catalogs and fake file systems every module test needs.</summary>
internal static class ModuleFixtures
{
    public static ModuleManifest Manifest(string id = "wavee.fake", string version = "1.0.0",
        int protocolVersion = 1, string[]? capabilities = null, string[]? urlPatterns = null,
        string entry = "Wavee.Module.Fake.dll", string publisher = "wavee", int schemaVersion = 1)
        => new(schemaVersion, id, version, id, publisher, protocolVersion, entry,
            capabilities ?? ["playback", "match"], urlPatterns ?? [], null);

    public static InstalledModule Installed(ModuleManifest manifest, string? dir = null, bool bundled = true)
        => new(manifest.Id, manifest.Version, dir ?? Path.Combine(RootDir, manifest.Id), manifest, bundled);

    /// <summary>The pretend bundled root every fixture module lives under.</summary>
    public const string RootDir = @"C:pp\modules";

    public static ResolvedPlayable Resolved(string playableId = "p1", MediaLocator? media = null,
        MediaForm form = MediaForm.Audio, bool isLive = false, long durationMs = 1000,
        long? expiresAtUnixMs = null, string[]? caps = null, string title = "A title", string[]? artists = null)
        => new(playableId, title, artists ?? ["An artist"], null, durationMs, isLive, form,
            media ?? MediaLocator.FromUrl("https://example.test/a.mp3", MediaLocator.ContainerProgressive, "audio/mpeg"),
            expiresAtUnixMs, caps ?? [], 0f, null);

    /// <summary>A file system over an in-memory path → content map. Directories are implied by their files.</summary>
    public static ModuleFileSystem FileSystem(Dictionary<string, string> files)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files.Keys)
        {
            string? dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                dirs.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        return new ModuleFileSystem(
            d => dirs.Contains(d),
            d =>
            {
                var kids = new List<string>();
                foreach (string candidate in dirs)
                    if (string.Equals(Path.GetDirectoryName(candidate), d, StringComparison.OrdinalIgnoreCase))
                        kids.Add(candidate);
                kids.Sort(StringComparer.OrdinalIgnoreCase);
                return kids.ToArray();
            },
            f => files.ContainsKey(f),
            f => files.TryGetValue(f, out string? c) ? c : throw new FileNotFoundException(f));
    }

    public static string ManifestJson(ModuleManifest m)
        => JsonSerializer.Serialize(m, SdkJsonContext.Default.ModuleManifest);

    /// <summary>A host over one fake module, with the process spawn replaced by an in-memory channel.</summary>
    public static (ModuleHost Host, Func<FakeModuleChannel?> Channel) HostOver(
        FakeModule script, ModuleManifest? manifest = null, Func<ResolvePreferences>? prefs = null,
        ModuleHostServices? services = null)
    {
        manifest ??= Manifest();
        var catalog = TestCatalog.With(Installed(manifest));
        FakeModuleChannel? channel = null;
        var host = new ModuleHost(catalog, default, prefs, services,
            (_, _, _) =>
            {
                channel = new FakeModuleChannel(script);
                channel.Start();
                return Task.FromResult<IModuleChannel>(channel);
            }, "1.0.0-test", "en-US", startIdleTimer: false);
        return (host, () => channel);
    }
}

/// <summary>Builds a <see cref="ModuleCatalog"/> around hand-made <see cref="InstalledModule"/>s, without a disk.</summary>
internal static class TestCatalog
{
    public static ModuleCatalog With(params InstalledModule[] modules)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledModule m in modules)
            files[Path.Combine(m.Dir, ModuleCatalog.ManifestFileName)] = ModuleFixtures.ManifestJson(m.Manifest);
        string root = modules.Length > 0 ? Path.GetDirectoryName(modules[0].Dir)! : ModuleFixtures.RootDir;
        return ModuleCatalog.Discover(root, @"C:
ope", ModuleFixtures.FileSystem(files));
    }
}
