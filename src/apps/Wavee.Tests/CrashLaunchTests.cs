// ── Wavee.Tests/CrashLaunchTests.cs — Platform/Crash.Launch.cs ────────────────────────────────────────────────────
//
// Helper children must not outlive Wavee (0.3.4): the environment scrub (a tracing tool's `DOTNET_DiagnosticPorts=…,suspend`
// once parked every handler child before Main for 11+ hours), the `R` handshake, the watchdog, and the kill-on-close job.
// The pure parts are tables; the integration facts spawn the REAL handler (`Wavee.exe --crash-handler`, deployed next to
// the test binary) against a dummy `ping.exe` parent, and every process they start is killed in a finally.

using System.Diagnostics;
using Xunit;

namespace Wavee.Tests;

public class CrashLaunchEnvTests
{
    static Dictionary<string, string?> Env(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "x", ["WAVEE_KEEP"] = "1" };
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Helper_mode_removes_every_suspend_port_event_pipe_and_profiler_variable_and_disables_diagnostics()
    {
        var env = Env(
            ("DOTNET_DiagnosticPorts", @"\\.\pipe\p,connect,suspend"), ("DOTNET_DefaultDiagnosticPortSuspend", "1"),
            ("DOTNET_EnableEventPipe", "1"), ("DOTNET_EventPipeConfig", "a"), ("DOTNET_EventPipeOutputPath", "b"),
            ("CORECLR_ENABLE_PROFILING", "1"), ("CORECLR_PROFILER", "{x}"), ("CORECLR_PROFILER_PATH_64", "p"),
            ("COMPlus_DiagnosticPorts", "q"), ("COMPlus_EnableEventPipe", "1"), ("COMPlus_EnableDiagnostics", "1"),
            ("COR_ENABLE_PROFILING", "1"), ("dotnet_diagnosticports", "lower"), ("DOTNET_EnableDiagnostics_IPC", "1"));

        Crash.Launch.ScrubEnvironment(env, Crash.Launch.EnvScrub.Helper);

        Assert.Equal("0", env["DOTNET_EnableDiagnostics"]);
        Assert.Equal("x", env["PATH"]);
        Assert.Equal("1", env["WAVEE_KEEP"]);
        Assert.Equal(["DOTNET_EnableDiagnostics", "PATH", "WAVEE_KEEP"], env.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Strip_mode_removes_the_same_but_leaves_diagnostics_as_they_are()
    {
        var env = Env(("DOTNET_DiagnosticPorts", "p,suspend"), ("CORECLR_PROFILER", "{x}"), ("DOTNET_EnableDiagnostics", "1"),
            ("DOTNET_EventPipeConfig", "a"));

        Crash.Launch.ScrubEnvironment(env, Crash.Launch.EnvScrub.Strip);

        Assert.Equal(["DOTNET_EnableDiagnostics", "PATH", "WAVEE_KEEP"], env.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("1", env["DOTNET_EnableDiagnostics"]);
    }

    [Fact]
    public void Strip_mode_never_invents_an_EnableDiagnostics_value()
    {
        var env = Env(("DOTNET_DiagnosticPorts", "p"));
        Crash.Launch.ScrubEnvironment(env, Crash.Launch.EnvScrub.Strip);
        Assert.False(env.ContainsKey("DOTNET_EnableDiagnostics"));
    }

    [Theory]
    [InlineData("R", true)]
    [InlineData("OK 123", false)]
    [InlineData("ERR x", false)]
    [InlineData("R ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_bare_R_is_the_ready_line(string? line, bool ready) => Assert.Equal(ready, Crash.Launch.IsReadyLine(line));

    [Fact]
    public void The_reply_pump_swallows_R_and_keeps_D_replies_in_order()
    {
        var pump = new Crash.Launch.ReplyPump(new StringReader("R\nOK 5\nERR boom\n"));
        Assert.True(pump.WaitReady(5000));
        Assert.True(pump.TryTakeReply(5000, out string? a)); Assert.Equal("OK 5", a);
        Assert.True(pump.TryTakeReply(5000, out string? b)); Assert.Equal("ERR boom", b);
        Assert.True(pump.TryTakeReply(5000, out string? eof)); Assert.Null(eof);        // end of stream
        Assert.True(pump.TryTakeReply(5000, out string? eof2)); Assert.Null(eof2);      // ...and stays at the end
    }

    [Fact]
    public void A_stream_that_ends_without_R_is_not_ready()
    {
        var pump = new Crash.Launch.ReplyPump(new StringReader("OK 1\n"));
        Assert.False(pump.WaitReady(5000));
    }

    [Fact]
    public void A_reply_that_never_comes_times_out_instead_of_blocking()
    {
        var gate = new BlockingReader();
        try
        {
            var pump = new Crash.Launch.ReplyPump(gate);
            Assert.False(pump.TryTakeReply(100, out _));
            Assert.False(pump.WaitReady(100));
        }
        finally { gate.Release(); }
    }

    sealed class BlockingReader : TextReader
    {
        readonly ManualResetEventSlim _go = new(false);
        public void Release() => _go.Set();
        public override string? ReadLine() { _go.Wait(); return null; }
    }
}

/// <summary>Mutates this process's environment (the inherited-suspend-port facts): no other test class runs beside it.</summary>
[CollectionDefinition("CrashLaunchProcesses", DisableParallelization = true)]
public sealed class CrashLaunchProcessesCollection;

/// <summary>Spawns real processes and mutates this process's environment: serialised, never parallel with itself.</summary>
[Collection("CrashLaunchProcesses")]
[Trait("Category", "Integration")]
public sealed class CrashLaunchProcessTests : IDisposable
{
    const string SuspendPort = @"\\.\pipe\wavee-test-nonexistent-port,connect,suspend";

    readonly List<Process> _procs = [];
    readonly List<Crash.Launch.HandlerChild> _children = [];
    readonly string _logFolder = Path.Combine(Path.GetTempPath(), "wavee-crashlaunch-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        foreach (var c in _children) c.Kill();
        foreach (var p in _procs) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } p.Dispose(); }
        try { Directory.Delete(_logFolder, recursive: true); } catch { }
    }

    static string? HandlerExe()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Wavee.exe");
        return File.Exists(exe) && File.Exists(Path.Combine(AppContext.BaseDirectory, "Wavee.dll")) ? exe : null;
    }

    Process DummyParent()
    {
        var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        _procs.Add(p);
        _ = p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);   // never fill the pipe
        return p;
    }

    Crash.Launch.HandlerChild StartHandler(Process parent, string exe, Action<ProcessStartInfo>? tweak = null)
    {
        var psi = Crash.Launch.CreateHandlerStartInfo(exe, null, parent.Id, _logFolder, null);
        tweak?.Invoke(psi);
        var child = Crash.Launch.HandlerChild.Start(psi)!;
        Assert.NotNull(child);
        _children.Add(child);
        _procs.Add(child.Process);
        return child;
    }

    string HandlerLogText()
    {
        try
        {
            using var fs = new FileStream(Path.Combine(Crash.Files.Root(_logFolder), Crash.Files.HandlerLog), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    static bool ExitsWithin(Process p, int ms) { try { return p.WaitForExit(ms); } catch { return true; } }

    static T WithEnv<T>(string name, string value, Func<T> body)
    {
        string? old = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(name, old); }
    }

    // (a) THE REGRESSION: with a suspend port in the parent's environment the child must still start and say ready.
    // Without the scrub the child's runtime parks before Main (no handler.log, no R) — the 11-hour orphan.
    [Fact]
    public void A_suspend_port_in_the_parents_environment_does_not_stop_the_handler_starting()
    {
        string? exe = HandlerExe();
        if (exe is null) { Assert.Skip("Wavee.exe is not deployed next to the test binary"); return; }
        Process parent = DummyParent();

        var child = WithEnv("DOTNET_DiagnosticPorts", SuspendPort, () => StartHandler(parent, exe));

        Assert.True(child.AwaitReady(30_000), "the handler never became ready; log: " + HandlerLogText());
        Assert.Contains("handler.ready", HandlerLogText());
        Assert.Equal("shared", child.JobState);

        parent.Kill();
        Assert.True(ExitsWithin(child.Process, 15_000), "the handler outlived its parent");
    }

    // CONTROL for (a): the SAME spawn with the scrub undone must hang before Main. If a runtime ever stops honouring the
    // suspend port (a NativeAOT apphost never did), this fails and tells us (a) has gone vacuous instead of passing silently.
    [Fact]
    public void Control_without_the_scrub_the_suspend_port_parks_the_handler_before_main()
    {
        string? exe = HandlerExe();
        if (exe is null) { Assert.Skip("Wavee.exe is not deployed next to the test binary"); return; }
        Process parent = DummyParent();

        var child = StartHandler(parent, exe, psi =>
        {
            psi.Environment["DOTNET_DiagnosticPorts"] = SuspendPort;
            psi.Environment.Remove("DOTNET_EnableDiagnostics");
        });

        Assert.False(child.AwaitReady(3000), "an unscrubbed child started: the suspend regression test no longer proves anything");
        Assert.False(child.HasExited);
        Assert.Equal("", HandlerLogText());
    }

    // The deadline: with a 1 ms budget the handler terminates itself (exit code 3) before it finishes its exit bundle.
    [Fact]
    public void The_shutdown_deadline_terminates_a_handler_that_is_still_busy_after_the_parent_exits()
    {
        string? exe = HandlerExe();
        if (exe is null) { Assert.Skip("Wavee.exe is not deployed next to the test binary"); return; }
        Process parent = DummyParent();
        var child = StartHandler(parent, exe, psi => psi.Environment[Crash.Handler.DeadlineEnvVar] = "1");
        Assert.True(child.AwaitReady(30_000), "the handler never became ready; log: " + HandlerLogText());

        parent.Kill();   // exit code 1: the handler starts writing an UncleanExit bundle, which takes far longer than 1 ms

        Assert.True(ExitsWithin(child.Process, 20_000), "the handler outlived its deadline");
        Assert.Equal(3, child.Process.ExitCode);
        Assert.Contains("handler.shutdown.deadline", HandlerLogText());
    }

    // (b) A child that never said ready holds no job handle: when the owner's handle goes, the kernel kills it.
    // (ReleaseJob closes the last handle: the same kernel path as the owner process dying, without killing the test host.)
    [Fact]
    public void A_handler_that_never_became_ready_dies_with_the_owners_job_handle()
    {
        string? exe = HandlerExe();
        if (exe is null) { Assert.Skip("Wavee.exe is not deployed next to the test binary"); return; }
        Process parent = DummyParent();
        var child = StartHandler(parent, exe);   // no AwaitReady: never handed its own job handle
        Assert.True(child.JobAssigned);
        Assert.False(child.JobShared);

        child.ReleaseJob();                      // what the kernel does when Wavee dies, however it dies

        Assert.True(ExitsWithin(child.Process, 15_000), "an un-ready handler survived the job closing");
        Assert.False(parent.HasExited);          // the kill came from the job, not from the parent dying
    }

    // (c) A ready child keeps its own job handle: it outlives the owner, writes its bundle for a parent that died
    // badly, and exits by itself.
    [Fact]
    public void A_ready_handler_outlives_the_owner_writes_its_bundle_and_exits()
    {
        string? exe = HandlerExe();
        if (exe is null) { Assert.Skip("Wavee.exe is not deployed next to the test binary"); return; }
        Process parent = DummyParent();
        var child = StartHandler(parent, exe);
        Assert.True(child.AwaitReady(30_000), "the handler never became ready; log: " + HandlerLogText());
        Assert.True(child.JobShared);

        child.ReleaseJob();                      // Wavee "dies": the job lives on in the child's own handle
        Assert.False(ExitsWithin(child.Process, 1500), "a ready handler was killed with the owner");

        parent.Kill();                           // exit code 1: an UncleanExit bundle
        Assert.True(ExitsWithin(child.Process, 20_000), "the handler never exited after its parent died");
        Assert.Contains("parent exited", HandlerLogText());
        Assert.Single(Crash.Bundles.List(_logFolder));
    }

    // (d) The watchdog: a child that never says ready is killed, retried once, then given up on.
    [Fact]
    public void The_watchdog_kills_children_that_never_say_ready_retries_once_then_gives_up()
    {
        int spawned = 0;
        var log = new List<string>();
        var abandoned = new List<Crash.Launch.HandlerChild>();
        Crash.Launch.HandlerChild? Silent()
        {
            var psi = new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            var c = Crash.Launch.HandlerChild.Start(psi)!;
            _children.Add(c); _procs.Add(c.Process);
            spawned++;
            return c;
        }

        var first = Silent()!;
        var result = Crash.Launch.Supervise(first, Silent, static _ => { }, abandoned.Add, log.Add, readyMs: 400);

        Assert.Null(result);
        Assert.Equal(2, spawned);
        Assert.Equal(2, abandoned.Count);
        Assert.Equal(2, log.Count(l => l.StartsWith("crash.handler.unresponsive", StringComparison.Ordinal)));
        Assert.Contains(log, l => l.StartsWith("crash.handler.unavailable", StringComparison.Ordinal));
        foreach (var c in abandoned) Assert.True(ExitsWithin(c.Process, 10_000), "an abandoned child is still running");
    }

    [Fact]
    public void The_watchdog_keeps_a_child_that_says_ready_and_does_not_respawn()
    {
        int respawns = 0;
        var psi = new ProcessStartInfo("cmd.exe", "/c echo R& ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        var c = Crash.Launch.HandlerChild.Start(psi)!;
        _children.Add(c); _procs.Add(c.Process);

        var result = Crash.Launch.Supervise(c, () => { respawns++; return null; }, static _ => { }, static _ => { }, static _ => { }, readyMs: 10_000);

        Assert.Same(c, result);
        Assert.Equal(0, respawns);
    }
}
