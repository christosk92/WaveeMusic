// The in-memory module every module test drives: a real JsonRpcConnection pair over Modules.MemoryPipe with a SCRIPTED
// module peer on the far side (ported from 0.2.9 Wavee.Tests/Modules/ModuleTestSupport.cs). The handshake, match,
// resolve, page, the binary stream frames, cancellation and a crash all go over the real wire; nothing spawns a process.

using System.Globalization;
using System.Text.Json;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using static Wavee.Modules;

namespace Wavee.Tests;

/// <summary>A channel whose "process" is a scripted <see cref="FakeModule"/> behind two in-memory pipes.</summary>
internal sealed class FakeModuleChannel : IModuleChannel
{
    readonly MemoryPipe _hostToModule = new();
    readonly MemoryPipe _moduleToHost = new();
    readonly CancellationTokenSource _cts = new();
    readonly JsonRpcConnection _module;
    Task _moduleLoop = Task.CompletedTask;

    public FakeModuleChannel(FakeModule script, int processId = 4242)
    {
        Script = script;
        ProcessId = processId;
        Connection = new JsonRpcConnection(_moduleToHost, _hostToModule);
        _module = new JsonRpcConnection(_hostToModule, _moduleToHost, negativeIds: true);
        script.Install(_module);
    }

    public FakeModule Script { get; }

    /// <summary>The module peer, for a test that pushes a notification host-ward.</summary>
    public JsonRpcConnection Module => _module;

    public JsonRpcConnection Connection { get; }
    public int ProcessId { get; }
    public bool HasExited { get; private set; }
    public bool Killed { get; private set; }

    public void Start() => _moduleLoop = _module.RunAsync(_cts.Token);

    /// <summary>The module process dying mid-session (a crash, a broken pipe).</summary>
    public void Crash()
    {
        HasExited = true;
        _cts.Cancel();
        _moduleToHost.Complete();
        _hostToModule.Complete();
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
        catch (Exception) { /* torn down; a cancellation race is not a failure */ }
    }
}

/// <summary>What the fake module answers. Every hook is optional; an unset one is "not implemented" / a typed refusal.</summary>
internal sealed class FakeModule
{
    public Func<InitializeParams, InitializeResult>? Initialize { get; set; } =
        _ => new InitializeResult(1, ["playback", "match", "metadata"], null);

    public Func<MatchParams, MatchResult>? Match { get; set; }
    public Func<ResolveParams, ResolvedPlayable>? Resolve { get; set; }
    public Func<PageParams, ModulePageDoc?>? Page { get; set; }

    public Dictionary<string, byte[]> Streams { get; } = new(StringComparer.Ordinal);
    public int MaxReadBytes { get; set; }
    public bool ReportLength { get; set; } = true;
    public bool Seekable { get; set; } = true;
    public string? ContentType { get; set; }
    public TimeSpan ResolveDelay { get; set; }

    public int ResolveCalls, InitializeCalls, PageCalls;
    public readonly HashSet<string> OpenHandles = new(StringComparer.Ordinal);
    public ResolvePreferences? LastPrefs;

    readonly Dictionary<string, byte[]> _byHandle = new(StringComparer.Ordinal);
    int _nextHandle;

    public void Install(JsonRpcConnection module)
    {
        module.OnRequest(ModuleMethods.Initialize, SdkJsonContext.Default.InitializeParams, SdkJsonContext.Default.InitializeResult,
            (InitializeParams p, CancellationToken _) =>
            {
                Interlocked.Increment(ref InitializeCalls);
                if (Initialize is not { } make) throw new ModuleException(ModuleErrorCode.Unsupported, "this module refuses to start");
                return new ValueTask<InitializeResult>(make(p));
            });

        module.OnRequest(ModuleMethods.Shutdown, SdkJsonContext.Default.RpcUnit, SdkJsonContext.Default.RpcUnit,
            (RpcUnit _, CancellationToken _) => new ValueTask<RpcUnit>(RpcUnit.Value));

        module.OnRequest(ModuleMethods.Match, SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult,
            (MatchParams p, CancellationToken _) => Match is { } m
                ? new ValueTask<MatchResult>(m(p))
                : throw new ModuleException(ModuleErrorCode.NotOwned, "not mine"));

        module.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
            async (ResolveParams p, CancellationToken ct) =>
            {
                Interlocked.Increment(ref ResolveCalls);
                LastPrefs = p.Prefs;
                if (ResolveDelay > TimeSpan.Zero) await Task.Delay(ResolveDelay, ct);
                return Resolve is { } r ? r(p) : throw new ModuleException(ModuleErrorCode.Unavailable, "nothing to resolve");
            });

        module.OnRequest(ModuleMethods.Page, SdkJsonContext.Default.PageParams, SdkJsonContext.Default.ModulePageDoc,
            (PageParams p, CancellationToken _) =>
            {
                Interlocked.Increment(ref PageCalls);
                return Page is { } page
                    ? new ValueTask<ModulePageDoc>(page(p)!)
                    : throw new ModuleException(ModuleErrorCode.Unavailable, "no page");
            });

        module.OnRequest(ModuleMethods.StreamOpen, SdkJsonContext.Default.StreamOpenParams, SdkJsonContext.Default.StreamOpenResult,
            (StreamOpenParams p, CancellationToken _) =>
            {
                if (!Streams.TryGetValue(p.StreamId, out byte[]? bytes))
                    throw new ModuleException(ModuleErrorCode.Unavailable, "no such stream: " + p.StreamId);
                string handle = "h" + Interlocked.Increment(ref _nextHandle).ToString(CultureInfo.InvariantCulture);
                lock (OpenHandles) OpenHandles.Add(handle);
                lock (_byHandle) _byHandle[handle] = bytes;
                return new ValueTask<StreamOpenResult>(new StreamOpenResult(handle, ReportLength ? bytes.Length : null, Seekable, ContentType));
            });

        module.OnBinaryRequest(ModuleMethods.StreamRead, SdkJsonContext.Default.StreamReadParams,
            (StreamReadParams p, CancellationToken _) =>
            {
                byte[]? bytes;
                lock (_byHandle) _byHandle.TryGetValue(p.Handle, out bytes);
                if (bytes is null) throw new ModuleException(ModuleErrorCode.Unavailable, "no such handle: " + p.Handle);
                if (p.Offset >= bytes.Length) return new ValueTask<BinaryPayload>(new BinaryPayload(default, true));
                int want = MaxReadBytes > 0 ? Math.Min(p.Count, MaxReadBytes) : p.Count;
                int n = (int)Math.Min(want, bytes.Length - p.Offset);
                return new ValueTask<BinaryPayload>(new BinaryPayload(bytes.AsMemory((int)p.Offset, n), p.Offset + n >= bytes.Length));
            });

        module.OnRequest(ModuleMethods.StreamClose, SdkJsonContext.Default.StreamCloseParams, SdkJsonContext.Default.RpcUnit,
            (StreamCloseParams p, CancellationToken _) =>
            {
                lock (OpenHandles) OpenHandles.Remove(p.Handle);
                lock (_byHandle) _byHandle.Remove(p.Handle);
                return new ValueTask<RpcUnit>(RpcUnit.Value);
            });
    }
}

/// <summary>Builders for the manifests, catalogs and fake file systems every module test needs.</summary>
internal static class ModuleFixtures
{
    public const string RootDir = @"C:\app\modules";

    public static ModuleManifest Manifest(string id = "wavee.fake", string version = "1.0.0", int protocolVersion = 1,
        string[]? capabilities = null, string[]? urlPatterns = null, string entry = "Wavee.Module.Fake.dll",
        string publisher = "wavee", int schemaVersion = 1)
        => new(schemaVersion, id, version, id, publisher, protocolVersion, entry, capabilities ?? ["playback", "match"], urlPatterns ?? [], null);

    public static InstalledModule Installed(ModuleManifest manifest, string? dir = null, bool bundled = true)
        => new(manifest.Id, manifest.Version, dir ?? Path.Combine(RootDir, manifest.Id), manifest, bundled);

    public static ResolvedPlayable Resolved(string playableId = "p1", MediaLocator? media = null, MediaForm form = MediaForm.Audio,
        bool isLive = false, long durationMs = 1000, long? expiresAtUnixMs = null, string[]? caps = null, string title = "A title",
        string[]? artists = null, string? pageEntityId = null, string? subtitleEntityId = null)
        => new(playableId, title, artists ?? ["An artist"], null, durationMs, isLive, form,
            media ?? MediaLocator.FromUrl("https://example.test/a.mp3", MediaLocator.ContainerProgressive, "audio/mpeg"),
            expiresAtUnixMs, caps ?? [], 0f, null, pageEntityId, subtitleEntityId);

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
                    if (string.Equals(Path.GetDirectoryName(candidate), d, StringComparison.OrdinalIgnoreCase)) kids.Add(candidate);
                kids.Sort(StringComparer.OrdinalIgnoreCase);
                return kids.ToArray();
            },
            f => files.ContainsKey(f),
            f => files.TryGetValue(f, out string? c) ? c : throw new FileNotFoundException(f));
    }

    public static string ManifestJson(ModuleManifest m) => JsonSerializer.Serialize(m, SdkJsonContext.Default.ModuleManifest);

    /// <summary>A host over one fake module, with the process spawn replaced by an in-memory channel.</summary>
    public static (ModuleHost Host, Func<FakeModuleChannel?> Channel) HostOver(FakeModule script, ModuleManifest? manifest = null,
        Func<ResolvePreferences>? prefs = null, ModuleHostServices? services = null, Func<long>? nowUnixMs = null)
    {
        manifest ??= Manifest();
        ModuleCatalog catalog = TestCatalog.With(Installed(manifest));
        FakeModuleChannel? channel = null;
        var host = new ModuleHost(catalog, prefs, services,
            (_, _, _) =>
            {
                channel = new FakeModuleChannel(script);
                channel.Start();
                return Task.FromResult<IModuleChannel>(channel);
            }, "1.0.0-test", "en-US", startIdleTimer: false, nowUnixMs: nowUnixMs);
        return (host, () => channel);
    }
}

/// <summary>A <see cref="ModuleCatalog"/> around hand-made modules, discovered through the real gate over a fake disk.</summary>
internal static class TestCatalog
{
    public static ModuleCatalog With(params InstalledModule[] modules)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledModule m in modules)
            files[Path.Combine(m.Dir, ModuleCatalog.ManifestFileName)] = ModuleFixtures.ManifestJson(m.Manifest);
        string root = modules.Length > 0 ? Path.GetDirectoryName(modules[0].Dir)! : ModuleFixtures.RootDir;
        return ModuleCatalog.Discover(root, @"C:\nope", ModuleFixtures.FileSystem(files));
    }
}
