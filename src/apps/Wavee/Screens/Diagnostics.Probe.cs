// ── Screens/Diagnostics.Probe.cs ───────────────────────────────────────────────────────────────────────────────────
// the CLI probe arms: --headless (the headless host, below). Owner S's Wave-6 arms — --perf-bench, --startup-bench,
// --crash-probe, --lyrics-advance-probe (ch 22 (a) — the env-var switch is deleted), --qr-dump (ch 28 §1.5),
// --relaunch-after, NotificationSimulator (ch 14) — are the named partial `Diagnostics.Probe.Arms.cs` (G-016: this
// file's 800 is spent by the headless arm)
//
// Role: SHELL
// Owner: S (Wave 6) — the headless arm is an orchestrator-owned first cut (owner X), the Platform.cs precedent
// Wave: 6 (headless arm: now)
// Budget: 800 lines (headless arm ≈ 520)
// Spec: ch 27 §9.3 (400) + ch 14 §9 (195) + ch 28 (93) + ch 22 (DERIVED); headless: docs/plans/wavee/
//       wavee-0.3-headless-implementation.md §2.2-2.10, §3.3
//
// THE HEADLESS HOST is a second SHELL composition root: the same CORE, the same SHELL files, a different marshaller
// and no `Shell.Run()`. One thread writes (the main thread, blocked on `HeadlessLoop`); every other thread — AP,
// dealer, api ×4, the audio pump, the timers, the command reader — reaches CORE state only through `HeadlessLoop.Post`.
// One named 100 ms timer (`HeadlessTick`, P10) posts the tick that is this host's frame: `Fetch.Pump()`,
// `Entities.Publish()`, the session watch, the snapshot, the event diffs, the script step, the log echo. Every decision
// the tick makes is in `Diagnostics.Headless.cs` (CORE) and is a unit test there.
//
// SAFETY (§2.8): settings writes land in an overlay and never reach HKCU; the credential blob can be refreshed but never
// removed; the Connect device id is namespaced (`device.id.headless`); no library.<schema>.db unless `--store`;
// `--profile` redirects the whole profile (App.cs sets `Platform.ProfileRoot` from `ProfileArg` before `Platform.Boot`).
//
// `--stress-audio` (docs/plans/wavee/playback-smoothness-implementation.md §4.18, #167) is the same shape for the audio load
// test: a windowless host that plays a local file or a Spotify track through the REAL device while burner threads, an
// allocator, a minimized stand-in window and battery-saver throttling lean on the machine, then prints the glitch ledger.
// It is the LAST section of this file; its decisions are pure (`StressOptions`, `StressRun`, `StressReport`, `StressRules`)
// and pinned by `Wavee.Tests/AudioStressTests.cs`. The `Probe.Arms.cs` partial (G-016) is owned elsewhere: the arm is wired
// from `Probe.TryRun` here.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Windows.Wasapi;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>The arm table. First match runs; the rest of Main never does. Every arm attaches the parent console
        /// first (a WinExe has none) — 0.2.9's Program.cs:28-40, in spirit.</summary>
        public static bool TryRun(string[] args, out int code)
        {
            // The window-less Wave-6 arms first (`--relaunch-after`, `--qr-dump`: Diagnostics.Probe.Arms.cs). The GUI arms
            // (`--perf-bench`, `--startup-bench`, `--crash-probe`, `--lyrics-advance-probe`) need the window, so they ride
            // `FluentApp.DiagnosticRun` from `Diagnostics.Install` instead.
            if (TryRunCliArm(args, out code)) return true;
            code = 0;
            if (Array.IndexOf(args, "--stress-audio") >= 0) { code = StressAudio(args); return true; }   // the load-test runner (§ --stress-audio, below)
            if (Array.IndexOf(args, "--headless") < 0) return false;
            AttachParentConsole();
            if (!HeadlessOptions.TryParse(args, out HeadlessOptions options, out string usage))
            {
                Console.Error.WriteLine(usage);
                code = Headless.ExitCode.Usage;
                return true;
            }
            code = HeadlessHost.Run(options);
            return true;
        }

        /// <summary>`--profile &lt;dir&gt;`, or "" for the default profile. App.cs hands it to `Platform.ProfileRoot`
        /// BEFORE `Platform.Boot` — every path (store.json, logs, the library file, cache/) hangs off it.</summary>
        public static string ProfileArg(string[] args)
        {
            int i = Array.IndexOf(args, "--profile");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        }

        /// <summary>`--stress-audio ...` (playback-smoothness plan §4.18): the on-box load test. Windowless like `--headless`; the
        /// parse is pure (<see cref="StressOptions.TryParse"/>), the run is <see cref="StressAudioHost"/>. Exit 0 = no incidents
        /// (or one under `--allow-one`), 2 = incidents, 1 = the run could not be completed, 64 = usage.</summary>
        static int StressAudio(string[] args)
        {
            AttachParentConsole();
            if (!StressOptions.TryParse(args, out StressOptions options, out string usage))
            {
                Console.Error.WriteLine(usage);
                return Headless.ExitCode.Usage;
            }
            return StressAudioHost.Run(options);
        }

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AttachConsole(int dwProcessId);

        /// <summary>Attach the parent's console when there is one (-1 = ATTACH_PARENT_PROCESS), then re-point stdout and
        /// stderr at the process's standard handles as UTF-8 — whether they are that console, a redirected file or a pipe
        /// (`Start-Process -RedirectStandardOutput`, `| Tee-Object`). The JSON lines are ASCII-safe either way.</summary>
        static void AttachParentConsole()
        {
            if (OperatingSystem.IsWindows())
            {
                try { AttachConsole(-1); } catch { }
            }
            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
    }

    /// <summary>`--headless [--script f | --pipe name] [--profile dir] [--store] [--silent] [--no-login] [--connect]
    /// [--login-timeout ms] [--timeout ms] [--echo-log]`. Pure parse; tested.</summary>
    public readonly record struct HeadlessOptions(string Script, string Pipe, bool Store, bool Silent, bool NoLogin, bool Connect,
                                                  int LoginTimeoutMs, int RunTimeoutMs, bool EchoLog, string Profile = "")
    {
        public const int DefaultLoginTimeoutMs = 60_000;

        public const string Usage =
            "usage: Wavee.exe --headless [--script <file.wh> | --pipe <name>] [--profile <dir>] [--store] [--silent]\n" +
            "                 [--no-login] [--connect] [--login-timeout <ms>] [--timeout <ms>] [--echo-log]\n" +
            "  --script        run a command script; the exit code is its verdict (0 ok, 1 fault, 2 assertion)\n" +
            "  --pipe          read commands from \\\\.\\pipe\\<name> (one client); default: read commands from stdin\n" +
            "  --profile       use <dir> as the profile instead of %LOCALAPPDATA%\\Wavee\n" +
            "  --store         open <profile>\\library.<schema>.db (default: memory-only catalog)\n" +
            "  --silent        play through the silent endpoint instead of the default audio device\n" +
            "  --no-login      do not resume the stored credential (offline grammar runs)\n" +
            "  --connect       announce the Connect device once online\n" +
            "  --login-timeout exit 75 when the session is not online within <ms> (default 60000)\n" +
            "  --timeout       exit 2 when the whole run takes longer than <ms>\n" +
            "  --echo-log      echo audio/spotify/playback/connect log lines as {\"kind\":\"echo\"} events";

        public static bool TryParse(string[] args, out HeadlessOptions options, out string usage)
        {
            options = new HeadlessOptions("", "", false, false, false, false, DefaultLoginTimeoutMs, 0, false);
            usage = "";
            string script = "", pipe = "", profile = "";
            bool store = false, silent = false, noLogin = false, connect = false, echo = false;
            int loginTimeout = DefaultLoginTimeoutMs, runTimeout = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--headless": break;
                    case "--store": store = true; break;
                    case "--silent": silent = true; break;
                    case "--no-login": noLogin = true; break;
                    case "--connect": connect = true; break;
                    case "--echo-log": echo = true; break;
                    case "--script": if (!Value(args, ref i, a, out script, ref usage)) return false; break;
                    case "--pipe": if (!Value(args, ref i, a, out pipe, ref usage)) return false; break;
                    case "--profile": if (!Value(args, ref i, a, out profile, ref usage)) return false; break;
                    case "--login-timeout": if (!Millis(args, ref i, a, out loginTimeout, ref usage)) return false; break;
                    case "--timeout": if (!Millis(args, ref i, a, out runTimeout, ref usage)) return false; break;
                    default:
                        usage = "unknown argument '" + a + "'\n" + Usage;
                        return false;
                }
            }
            if (script.Length > 0 && pipe.Length > 0) { usage = "--script and --pipe cannot be combined\n" + Usage; return false; }
            options = new HeadlessOptions(script, pipe, store, silent, noLogin, connect, loginTimeout, runTimeout, echo, profile);
            return true;
        }

        static bool Value(string[] args, ref int i, string flag, out string value, ref string usage)
        {
            value = "";
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                usage = flag + " wants a value\n" + Usage;
                return false;
            }
            value = args[++i];
            return true;
        }

        static bool Millis(string[] args, ref int i, string flag, out int ms, ref string usage)
        {
            ms = 0;
            if (!Value(args, ref i, flag, out string text, ref usage)) return false;
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0) return true;
            usage = flag + " wants a positive number of milliseconds, not '" + text + "'\n" + Usage;
            return false;
        }
    }

    /// <summary>The one writer thread. <see cref="Post"/> from anywhere; <see cref="Run"/> on the main thread until
    /// <see cref="Stop"/>, which lets everything already queued run and then returns. A post after the stop is dropped
    /// (the late answer of a thread that has not noticed the exit yet), never thrown back into that thread.</summary>
    public sealed class HeadlessLoop
    {
        readonly BlockingCollection<Action> _posts = new(4096);            // bounded (C8); a full queue blocks the poster
        readonly Stopwatch _clock = Stopwatch.StartNew();
        int _owner = -1;

        public long NowMs => _clock.ElapsedMilliseconds;
        public bool IsStopped => _posts.IsAddingCompleted;

        public void Post(Action a)
        {
            try
            {
                // The loop posting to itself must never wait on its own queue: a full queue from the writer thread runs
                // the action inline, which is still the single writer.
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref _owner)) { if (!_posts.TryAdd(a)) a(); }
                else _posts.Add(a);
            }
            catch (InvalidOperationException) { }                            // stopped
        }

        public void Stop()
        {
            try { _posts.CompleteAdding(); } catch (ObjectDisposedException) { }
        }

        public void Run()
        {
            Volatile.Write(ref _owner, Environment.CurrentManagedThreadId);
            foreach (Action a in _posts.GetConsumingEnumerable()) a();
        }
    }

    public static class HeadlessHost
    {
        const int TickMs = 100;
        const int EndpointGraceMs = 5_000;
        const int ContextTimeoutMs = 30_000;

        static HeadlessLoop s_loop = null!;
        static HeadlessOptions s_options;
        static Headless.Script? s_runner;
        static Headless.StatusSnapshot s_mark = Headless.StatusSnapshot.Empty, s_last = Headless.StatusSnapshot.Empty;
        static int s_exit = Headless.ExitCode.Ok, s_tickQueued;
        static bool s_exiting, s_wasOnline, s_connect, s_endpointOk;
        static long s_loginDeadline = long.MaxValue, s_runDeadline = long.MaxValue, s_playPostedAt = -1;
        static long s_logVersion = -1, s_lastEchoedSeq;
        static Timer? s_tick;
        static StreamWriter? s_pipeOut;
        static PendingContext s_pending;
        static readonly Action s_tickAction = Tick;

        /// <summary>An album or playlist asked for, waiting for its members edge (the tick notices).</summary>
        struct PendingContext
        {
            public bool Active;
            public EntityId Id;
            public int FromMs;
            public long StartedMs;
            public uint ScopeEpoch;
        }

        public static int Run(in HeadlessOptions o)
        {
            s_options = o;
            s_loop = new HeadlessLoop();

            // 0. the script parses BEFORE anything else boots (64)
            if (o.Script.Length > 0)
            {
                string text;
                try { text = File.ReadAllText(o.Script); }
                catch (Exception ex) { Console.Error.WriteLine(o.Script + ": " + ex.Message); return Headless.ExitCode.Usage; }
                if (!Headless.Script.TryLoad(text, out Headless.Script script, out int line, out string err))
                {
                    Console.Error.WriteLine(o.Script + "(" + line + "): " + err);
                    return Headless.ExitCode.Usage;
                }
                s_runner = script;
            }
            else s_runner = Headless.Script.Interactive();

            // 1. the profile must be writable (78)
            string profile = Platform.LocalFolder;
            if (!ProfileWritable(profile, out string why))
            {
                Emit(Headless.JsonLine.Fault(s_loop.NowMs, "profile-not-writable", why));
                return Finish(Headless.ExitCode.Config);
            }

            // 2. the stores (§2.8) — before anything reads them. NOT `new OverlaySettings(Platform.Settings)`: that is the
            //    facade, whose Get reads the backing store, which would be this overlay — an infinite recursion. The base is
            //    the store Boot resolved (`Platform.SettingsBackingFor`): HKCU normally, the profile's settings.json under
            //    `--profile` — so an isolated headless run reads that profile's settings, and no run ever writes them.
            Platform.UseSettings(new Headless.OverlaySettings(Platform.BackingSettings));
            ICredentialProtector protector = OperatingSystem.IsWindows() ? new DpapiProtector() : new NoOpProtector();
            Platform.UseCredentialSlot(new Headless.ProtectedLocalStore(new FileLocalStore(Platform.StorePath), "headless",
                refused: static k => Log.Warn("headless", "refused to remove " + k + " (a headless run never clears the credential)")), protector);
            Emit(Headless.JsonLine.Boot(s_loop.NowMs, profile, Platform.CredentialScheme, Platform.Scope.Account, o.Store,
                o.Silent ? "silent" : "wasapi"));
            if (!o.NoLogin && !Platform.HasStoredCredential())
            {
                Emit(Headless.JsonLine.Fault(s_loop.NowMs, "no-credential",
                    "sign in once in the Wavee window on this machine, or copy a 0.2.x store.json into --profile"));
                return Finish(Headless.ExitCode.NoCredential);
            }

            // 3. the loop and the marshallers: ONE writer
            Playback.ToUi = s_loop.Post;
            Spotify.Post = s_loop.Post;
            Store.Post = s_loop.Post;
            Palette.Post = s_loop.Post;
            Playback.FrameNowMs = static () => s_loop.NowMs;
            WasapiAudioDevice.DiagSink = static s => Log.Info("audio", s);
            WasapiAudioDevice.FormatSink = static f => Log.Info("audio", "device format " + f);

            // 4. boot (no Shell, no Modules, no Store unless asked). The shapes first, unconditionally, exactly as the GUI
            // does: they ARE the schema, so `Store.FileName` read before them names a near-empty schema's file.
            App.RegisterShapes();
            Store.Use(o.Store ? Path.Combine(profile, Store.FileName) : null);
            Entities.Boot(Platform.Scope);
            Spotify.Boot();
            Playback.Boot();
            Spotify.Api.Boot();                                       // Fetch.Register(Transport); idempotent
            // G-239: the GUI installs both unconditionally (App.cs, unless --fake); a headless run has no --fake arm,
            // so the same two compose here - the library host (sync on Online, the pin bridge; G-042/043/049) and the
            // lyrics stack (G-008), whose resolveRequest posts through THIS host's loop, not Shell's UI marshaller
            // (there is no Shell here - see ResolveLyricsRequest below).
            Spotify.Library.Install();
            Lyrics.Boot(ResolveLyricsRequest, Spotify.Api.GetTextAsync, Spotify.SpclientBaseUrl);
            if (o.Silent) Playback.Audio.UseSilentEndpoint();
            s_connect = o.Connect;
            Spotify.Connect.AnnounceOnOnline = o.Connect;             // the session's own hello on Online, once per connection; off unless --connect

            // 5. the readers, login, the tick, then run
            if (o.Script.Length == 0) StartReader(o.Pipe);
            if (!o.NoLogin)
            {
                Spotify.Login();
                s_loginDeadline = s_loop.NowMs + o.LoginTimeoutMs;
            }
            s_runDeadline = o.RunTimeoutMs > 0 ? s_loop.NowMs + o.RunTimeoutMs : long.MaxValue;
            s_tick = new Timer(static _ =>
            {
                if (Interlocked.CompareExchange(ref s_tickQueued, 1, 0) == 0) s_loop.Post(s_tickAction);
            }, null, TickMs, TickMs);                                 // HeadlessTick — the one named timer

            try { s_loop.Run(); }
            catch (Exception ex)
            {
                Log.Error("headless", "the loop faulted", ex);
                Emit(Headless.JsonLine.Fault(s_loop.NowMs, "host-error", ex.GetType().Name + ": " + ex.Message));
                s_exit = Headless.ExitCode.HostError;
                Disconnect();
            }
            finally { s_tick?.Dispose(); }

            try { Spotify.Library.Shutdown(); } catch (Exception ex) { Log.Warn("headless", "library shutdown failed", ex); }
            if (o.Store) { try { Store.Shutdown(); } catch (Exception ex) { Log.Warn("headless", "store shutdown failed", ex); } }
            return Finish(s_exit);
        }

        const int LyricsResolveTimeoutMs = 4_000;    // mirrors Shell.ResolveLyricsRequest's bound (G-008)
        const int LyricsResolvePollMs = 50;

        /// <summary>The <c>resolveRequest</c> half of <c>Lyrics.Boot</c> for THIS host (G-239): the same mapping as
        /// <see cref="Shell.ResolveLyricsRequest"/> (base62 track id -&gt; <see cref="Lyrics.Request"/>, entity read
        /// through <see cref="Entities.Ensure"/>, bounded poll for a still-unknown row), but marshalled through
        /// <see cref="HeadlessLoop.Post"/> instead of Shell's UI dispatcher - there is no Shell composed here, and
        /// Entity columns are single-writer on the loop thread (C1) the same way they are single-writer on the UI
        /// thread in the GUI. The lyrics aggregator calls this on its own worker thread.</summary>
        static async Task<Lyrics.Request?> ResolveLyricsRequest(string trackId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(trackId) || !Base62.TryDecode(trackId.AsSpan(), out UInt128 gid)) return null;
            EntityId id = EntityId.ForGid(EntityKind.Track, gid);
            if (!id.IsValid) return null;

            long deadline = Environment.TickCount64 + LyricsResolveTimeoutMs;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var tcs = new TaskCompletionSource<Lyrics.Request?>(TaskCreationOptions.RunContinuationsAsynchronously);
                s_loop.Post(() =>
                {
                    Track track = Entities.Track(id);
                    if (!track.Knows(TrackFields.Identity)) Entities.Ensure(track, TrackFields.Identity, FetchPriority.Visible);
                    tcs.TrySetResult(Lyrics.RequestFrom(track, trackId));
                });
                Lyrics.Request? result = await tcs.Task.ConfigureAwait(false);
                if (result is not null || Environment.TickCount64 >= deadline) return result;
                try { await Task.Delay(LyricsResolvePollMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        static int Finish(int code)
        {
            Headless.Script? r = s_runner;
            Emit(Headless.JsonLine.Verdict(s_loop.NowMs, code == 0, r?.Executed ?? 0, r?.Failed ?? 0, code, r?.SoftFailed ?? 0));
            try { s_pipeOut?.Flush(); } catch (IOException) { }
            return code;
        }

        static bool ProfileWritable(string dir, out string why)
        {
            why = "";
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".headless-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                why = dir + ": " + ex.Message;
                return false;
            }
        }

        // ── the tick ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Loop thread, every 100 ms: the host's frame tick (Fetch.Pump + Entities.Publish), the session watch,
        /// the pending context, the snapshot, the event diffs, the script step, the log echo.</summary>
        static void Tick()
        {
            Volatile.Write(ref s_tickQueued, 0);
            if (s_exiting) return;
            long nowMs = s_loop.NowMs;

            // The planner's clock (app seconds): a backoff that expires is only noticed when Now moves.
            Entities.Now = Store.ToApp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Fetch.Pump();
            Entities.Publish();

            Spotify.Session session = Spotify.Current;
            if (session.IsOnline && !s_wasOnline) OnOnline();
            if (!s_wasOnline && !s_options.NoLogin)
            {
                if (session.Phase == Spotify.SessionPhase.Failed)
                {
                    bool rejected = session.Fault == Spotify.SessionFault.CredentialRejected;
                    Emit(Headless.JsonLine.Fault(nowMs, rejected ? "credential-rejected" : "login-failed",
                        rejected ? "the stored credential was kept; sign in again in the Wavee window" : "session fault " + session.Fault));
                    RequestExit(rejected ? Headless.ExitCode.CredentialRejected
                              : session.Fault == Spotify.SessionFault.NoCredential ? Headless.ExitCode.NoCredential
                              : Headless.ExitCode.LoginTimeout);
                    return;
                }
                if (nowMs > s_loginDeadline)
                {
                    Emit(Headless.JsonLine.Fault(nowMs, "login-timeout", "the session reached " + session.Phase + " and not Online"));
                    RequestExit(Headless.ExitCode.LoginTimeout);
                    return;
                }
            }
            if (nowMs > s_runDeadline)
            {
                Emit(Headless.JsonLine.Fault(nowMs, "run-timeout", "--timeout elapsed"));
                RequestExit(Headless.ExitCode.Assertion);
                return;
            }

            TryStartContext(nowMs);
            Headless.StatusSnapshot now = Snapshot(nowMs);
            EmitDiffs(in s_last, in now);
            s_last = now;
            if (!CheckEndpoint(in now)) return;

            // A file script starts once the session is online (its first `play` needs a session); stdin and the pipe run
            // at once, so an agent can ask for `status` while the session connects.
            Headless.Script? runner = s_runner;
            if (runner is not null && (runner.IsInteractive || s_options.NoLogin || s_wasOnline))
            {
                Headless.ScriptAction action = runner.Tick(in now);
                if (action.Verb is not (Headless.Verb.None or Headless.Verb.Quit))
                {
                    Headless.Command cmd = action.Cmd;
                    if (!Execute(in cmd, in now, out string error, out int code)) runner.Reject(error, code);
                }
                while (runner.TryTakeResult(out Headless.StepResult r))
                {
                    Emit(runner.IsInteractive
                        ? Headless.JsonLine.Reply(nowMs, r.Id, r.Ok, r.Cmd, r.Reason)
                        : Headless.JsonLine.Step(nowMs, in r));
                }
                if (action.Finished) { RequestExit(action.ExitCode); return; }
            }
            EchoLog(nowMs);
        }

        /// <summary>Once, the first tick the session is Online. The market and the scope switch are the session's own
        /// `Welcome` effect, and so is the Connect hello when `--connect` set `Spotify.Connect.AnnounceOnOnline` (§2.9):
        /// this host repeats neither.</summary>
        static void OnOnline()
        {
            s_wasOnline = true;
        }

        static void Hello()
        {
            if (Spotify.ConnectionId().Length == 0) return;
            Spotify.Connect.PublishNow(Playback.SnapshotForConnect(), Spotify.Connect.PutReason.NewDevice);
            Log.Info("headless", "connect hello sent (device " + Platform.Redact(Platform.DeviceId) + ")");
        }

        /// <summary>69: a play was posted, no silent endpoint was asked for, and the WASAPI leaf never came up.</summary>
        static bool CheckEndpoint(in Headless.StatusSnapshot now)
        {
            if (s_endpointOk || s_options.Silent || s_playPostedAt < 0) return true;
            if (Playback.Audio.Supported.Peek()) { s_endpointOk = true; return true; }
            if (now.NowMs - s_playPostedAt < EndpointGraceMs) return true;
            Emit(Headless.JsonLine.Fault(now.NowMs, "no-endpoint", "no audio render endpoint opened (WASAPI); pass --silent to run without a device"));
            RequestExit(Headless.ExitCode.NoEndpoint);
            return false;
        }

        /// <summary>Loop thread. Stop playback, close the session WITHOUT clearing the credential, and let the loop drain
        /// what is already queued (the stop's own drain included) before Run returns.</summary>
        static void RequestExit(int code)
        {
            if (s_exiting) return;
            s_exiting = true;
            s_exit = code;
            s_tick?.Change(Timeout.Infinite, Timeout.Infinite);
            try { Playback.Stop(); } catch (Exception ex) { Log.Warn("headless", "stop on exit failed", ex); }
            Disconnect();
            s_loop.Stop();
        }

        /// <summary>§8 Q3 (decided): `SessionEventKind.Disconnect` closes the sockets and keeps the credential. The loop
        /// thread IS the session's writer, so the fold runs here directly; the store wrapper still refuses a removal as the
        /// second guard.</summary>
        static void Disconnect()
        {
            try { Spotify.Apply(new Spotify.SessionEvent(Spotify.SessionEventKind.Disconnect)); }
            catch (Exception ex) { Log.Warn("headless", "disconnect on exit failed", ex); }
        }

        // ── the snapshot and the events ─────────────────────────────────────────────────────────────────────────────

        static Headless.StatusSnapshot Snapshot(long nowMs)
        {
            Playback.State p = Playback.Snap();
            Spotify.Session s = Spotify.Current;
            var st = Spotify.Audio.Stream.Stats.Read();               // F's counters (plan §3.4)
            var m = Playback.Audio.Metrics.Read();                    // H's metrics (plan §3.5)
            return new Headless.StatusSnapshot(
                NowMs: nowMs,
                SessionPhase: s.Phase.ToString(), SessionFault: s.Fault.ToString(), Tier: s.Tier.ToString(),
                Country: s.Country.IsEmpty ? "" : Entities.Strings.Resolve(s.Country),
                Phase: p.Phase.ToString(), Buffering: p.Buffering, Fault: p.Error.ToString(),
                TrackUri: p.CurrentId.IsEmpty ? "" : p.CurrentId.Text,
                PositionMs: p.Position(nowMs), DurationMs: p.DurationMs,
                Format: p.StreamFormat.IsEmpty ? "" : Entities.Strings.Resolve(p.StreamFormat),
                Volume: p.Volume, Owner: p.Owner.ToString(), LoadEpoch: p.LoadEpoch, PrepareArmed: m.PrepareArmed,
                // cdn.requests is every HTTP request the audio path made — ranges + heads + storage-resolves — which is the
                // unit vorbis plan §5.4's "cold start = 4" counts; the ranges alone are Stats.Requests.
                CdnRequests: (long)st.HttpRequests, CdnInFlight: (int)st.InFlight, CdnCancelled: (int)st.Cancelled,
                CdnHeads: (long)st.Heads, CdnResolves: (long)st.Resolves, CdnCacheHits: (long)st.CacheHits,
                Xruns: (int)m.Xruns, GaplessExact: (int)m.GaplessExact, GaplessDegraded: (int)m.GaplessDegraded,
                FirstAudioMs: (int)m.FirstAudioMs,
                CdnPeakInFlight: (int)st.PeakInFlight, CdnPingMs: (int)st.PingMs, CdnBytesPerSecond: (long)st.BytesPerSecond,
                RingWaits: (int)st.RingWaits, RingStarves: (int)st.RingStarves, HeadBytes: (int)st.HeadBytes,
                FirstAudioFromHead: m.FirstAudioFromHead, LastSeekMs: (int)m.LastSeekMs, LastSeekLatencyMs: (int)m.LastSeekLatencyMs,
                LastSeekKind: (byte)m.LastSeekKind, GaplessAbandoned: (int)m.GaplessAbandoned, DecodeXRealtime: (float)m.DecodeXRealtime,
                SeekCount: (int)m.Seeks);
        }

        /// <summary>The pushed events (§2.5): only on change, one line each.</summary>
        static void EmitDiffs(in Headless.StatusSnapshot prev, in Headless.StatusSnapshot now)
        {
            long t = now.NowMs;
            if (now.SessionPhase != prev.SessionPhase)
                Emit(Headless.JsonLine.Session(t, now.SessionPhase, now.SessionPhase == "Online" ? now.Tier : "", now.Country, now.SessionFault));
            if (now.Phase != prev.Phase || now.TrackUri != prev.TrackUri || now.Format != prev.Format
                || now.Buffering != prev.Buffering || now.Fault != prev.Fault || now.DurationMs != prev.DurationMs)
                Emit(Headless.JsonLine.State(t, in now));
            if (now.Phase == "Ended" && prev.Phase != "Ended") Emit(Headless.JsonLine.EndOfQueue(t, prev.TrackUri));
            if (now.Fault != prev.Fault && now.Fault != "None")
                Emit(Headless.JsonLine.Fault(t, "playback-" + now.Fault, now.TrackUri));
            if (now.Xruns > prev.Xruns) Emit(Headless.JsonLine.Underrun(t, now.Xruns, now.Xruns - prev.Xruns));
            if (now.GaplessExact > prev.GaplessExact || now.GaplessDegraded > prev.GaplessDegraded)
                Emit(Headless.JsonLine.Gapless(t, in now, in prev));
            if (now.SeekCount != prev.SeekCount || now.LastSeekMs != prev.LastSeekMs || now.LastSeekLatencyMs != prev.LastSeekLatencyMs)
                Emit(Headless.JsonLine.Seek(t, in now));
        }

        /// <summary>Echo new ring entries as `echo` events under `--echo-log`: the `audio.*` timeline lines carry `head=`,
        /// `firstAudioMs` and `requests=` until every number has a counter (§2.7).</summary>
        static void EchoLog(long nowMs)
        {
            if (!s_options.EchoLog) return;
            long version = Log.Version;
            if (version == s_logVersion) return;
            s_logVersion = version;
            foreach (WaveeLogEntry e in Log.Snapshot())
            {
                if (e.Sequence <= s_lastEchoedSeq) continue;
                s_lastEchoedSeq = e.Sequence;
                // "library" added for G-239: Spotify.Library's own Log.Info lines (sync asked/skipped, rootlist writes)
                // are the only signal a --script run has today that the library host installed above did anything -
                // the grammar has no library/rootlist field (see library-sync.wh's header comment).
                if (e.Category is "audio" or "spotify" or "playback" or "connect" or "library")
                    Emit(Headless.JsonLine.Echo(nowMs, e.Category, e.Format()));
            }
        }

        /// <summary>stdout, and the pipe client when there is one. Loop thread (and the main thread before and after it).</summary>
        static void Emit(string line)
        {
            Console.Out.WriteLine(line);
            StreamWriter? pipe = Volatile.Read(ref s_pipeOut);
            if (pipe is null) return;
            try { pipe.WriteLine(line); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Volatile.Write(ref s_pipeOut, null); }
        }

        // ── the commands ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One command the runner handed over this tick. Loop thread. False (with the reason and the exit-code
        /// class) when it could not be executed; the runner turns that into a failed step.</summary>
        static bool Execute(in Headless.Command c, in Headless.StatusSnapshot now, out string error, out int code)
        {
            error = "";
            code = Headless.ExitCode.Assertion;
            switch (c.Verb)
            {
                case Headless.Verb.Play: return Play(in c, now.NowMs, out error, out code);
                case Headless.Verb.Pause: Playback.Pause("probe"); return true;
                case Headless.Verb.Resume: Playback.Resume("probe"); return true;
                case Headless.Verb.Toggle: Playback.TogglePlay("probe"); return true;
                case Headless.Verb.Stop: Playback.Stop(); return true;
                case Headless.Verb.Next: Playback.Next(); return true;
                case Headless.Verb.Prev: Playback.Previous(); return true;
                case Headless.Verb.Seek:
                    {
                        int target;
                        if (c.FromEnd)
                        {
                            if (now.DurationMs <= 0) { error = "the duration is not known yet (wait playing first)"; return false; }
                            target = now.DurationMs + c.Int0;
                        }
                        else target = c.Relative ? now.PositionMs + c.Int0 : c.Int0;
                        if (target < 0) target = 0;
                        if (now.DurationMs > 0 && target > now.DurationMs) target = now.DurationMs;
                        Playback.SeekTo(target);
                        return true;
                    }
                case Headless.Verb.Volume: Playback.SetVolume(c.Int0 / 1000f); return true;
                case Headless.Verb.Shuffle: Playback.SetShuffle(c.Int0 != 0); return true;
                case Headless.Verb.Repeat: Playback.SetRepeat((Spotify.Decode.RepeatMode)c.Int0); return true;
                case Headless.Verb.Quality: Platform.Settings.Set(Platform.Keys.PlaybackQuality, c.Int0); return true;   // the overlay
                case Headless.Verb.Set:
                    switch (c.Arg0)
                    {
                        case "crossfade":
                            Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, c.Int0 > 0);
                            Platform.Settings.Set(Platform.Keys.CrossfadeMs, c.Int0);
                            return true;
                        case "normalization": Platform.Settings.Set(Platform.Keys.NormalizationEnabled, c.Int0 != 0); return true;
                        case "cache": Platform.Settings.Set(Platform.Keys.AudioBodyCacheEnabled, c.Int0 != 0); return true;
                        case "metered-cap": Platform.Settings.Set(Platform.Keys.MeteredQualityCap, c.Int0); return true;
                    }
                    error = "unknown setting " + c.Arg0;
                    return false;
                case Headless.Verb.Queue:
                    {
                        if (!EntityId.TryParse(c.Arg0.AsSpan(), out EntityId id) || !id.IsPlayable) { error = "not a track or episode uri"; return false; }
                        EntityRef row = Entities.Ref(id);
                        if (row.IsNone) { error = "no row for " + c.Arg0; return false; }
                        Queue.Enqueue(row);
                        return true;
                    }
                case Headless.Verb.Prepare:
                    Log.Info("headless", "prepare: the pump prepares the next row on its own; assert it with `wait prefetched`");
                    return true;
                case Headless.Verb.Status: Emit(Headless.JsonLine.Status(now.NowMs, in now, c.Id)); return true;
                case Headless.Verb.Stats:
                    if (c.Arg0 == "mark") s_mark = now;
                    else if (c.Arg0 == "reset") s_mark = Headless.StatusSnapshot.Empty;
                    Emit(Headless.JsonLine.Stats(now.NowMs, in now, in s_mark, c.Id));
                    return true;
                case Headless.Verb.Login:
                    Spotify.Login();
                    s_loginDeadline = s_loop.NowMs + s_options.LoginTimeoutMs;
                    return true;
                case Headless.Verb.Connect:
                    s_connect = c.Int0 != 0;
                    Spotify.Connect.AnnounceOnOnline = s_connect;      // later reconnects follow the switch
                    if (s_connect && s_wasOnline) Hello();              // already online: the session will not announce again until it reconnects
                    return true;
                case Headless.Verb.Log: Log.Info("headless", c.Arg0); return true;
                case Headless.Verb.Analysis:
                    {
                        // the audio-analysis endpoint probe (fullscreen-flagship-implementation.md §4.5.3). The HTTP call BLOCKS, so it
                        // runs on an api thread and posts its line back to the loop — Emit is loop-thread only (the RequestMembers shape).
                        string uri = c.Arg0;
                        bool queued = Spotify.Api.Run(() =>
                        {
                            string line = Spotify.Api.AudioAnalysisProbe(uri, CancellationToken.None);   // blocking: api thread
                            s_loop.Post(() => Emit(line));
                        });
                        if (!queued) { error = "api queue full — retry"; return false; }
                        return true;
                    }
            }
            error = "not executable here: " + c.Verb;
            return false;
        }

        /// <summary>`play`: a track or an episode posts at once as a one-row queue; an album asks Entities for its tracklist
        /// (Ensure → Fetch → Api → Decode → Commit) and a playlist reads its revision on an api thread, and both post when
        /// the members edge lands (the tick notices). Every id becomes a handle through the one factory (D10).</summary>
        static bool Play(in Headless.Command c, long nowMs, out string error, out int code)
        {
            error = "";
            code = Headless.ExitCode.Assertion;
            if (!Spotify.Current.IsOnline) { error = "the session is not online"; code = Headless.ExitCode.Fault; return false; }
            if (!EntityId.TryParse(c.Arg0.AsSpan(), out EntityId id)) { error = "not a spotify uri: " + c.Arg0; return false; }
            if (c.Int1 >= 0) Platform.Settings.Set(Platform.Keys.PlaybackQuality, c.Int1);
            EntityRef row = Entities.Ref(id);
            if (row.IsNone) { error = "no table for kind " + id.Kind; return false; }
            s_playPostedAt = nowMs;
            s_pending = default;

            if (id.IsPlayable)
            {
                Span<EntityRef> refs = [row];
                Span<QueueEdge> rows = stackalloc QueueEdge[1];
                int n = Headless.BuildContextQueue(refs, 0, refs, rows);
                Queue.Replace(refs[..n], rows[..n]);
                Playback.PlayNow(row, id, Queue.CursorOf(0), Playback.PlayableKind.Audio, c.Int0);
                return true;
            }
            if (id.Kind is not (EntityKind.Album or EntityKind.Playlist)) { error = "play knows tracks, episodes, albums and playlists"; return false; }

            s_pending = new PendingContext { Active = true, Id = id, FromMs = c.Int0, StartedMs = nowMs, ScopeEpoch = Entities.Current.Epoch };
            RequestMembers(id, row);
            TryStartContext(nowMs);
            return true;
        }

        static void RequestMembers(EntityId id, EntityRef row)
        {
            if (id.Kind == EntityKind.Album)
            {
                ReadOnlySpan<int> slot = [row.Slot];
                Entities.Ensure(Entities.Current.Albums, slot, (uint)AlbumFields.Identity, FetchPriority.Playback);
                return;
            }
            // A playlist's membership is not a metadata-planner group: read the revision (`/playlist/v2/playlist/<id>`)
            // and commit it through the same Staging → Commit path the pages will use.
            string uri = id.Text;
            string playlistId = uri[(uri.LastIndexOf(':') + 1)..];
            byte[] uriUtf8 = Encoding.UTF8.GetBytes(uri);
            uint epoch = Entities.Current.Epoch;
            bool queued = Spotify.Api.Run(() =>
            {
                Spotify.Api.Result result = Spotify.Api.Playlist(playlistId, CancellationToken.None);
                if (!result.Ok || result.Body.Length == 0)
                {
                    int status = result.Status;
                    s_loop.Post(() => Log.Warn("headless", "playlist read failed (status " + status + ")"));
                    return;
                }
                Staging staging = Staging.Rent();
                staging.Epoch = epoch;
                try { Spotify.Decode.PlaylistRevision(result.Body, uriUtf8, staging); }
                catch (Exception ex)
                {
                    Staging.Return(staging);
                    Log.Error("headless", "playlist decode faulted", ex);
                    return;
                }
                s_loop.Post(() =>
                {
                    Entities.Commit(staging);
                    Staging.Return(staging);
                    Entities.Publish();
                });
            });
            if (!queued) Log.Warn("headless", "playlist read refused: the api queue is full");
        }

        /// <summary>Loop thread, from the tick: when the pending album's or playlist's members are known, build the context
        /// queue, land it, and play its first row.</summary>
        static void TryStartContext(long nowMs)
        {
            if (!s_pending.Active) return;
            EntityRef row = Entities.Ref(s_pending.Id);
            if (row.IsNone) { s_pending = default; return; }
            if (Entities.Current.Epoch != s_pending.ScopeEpoch)
            {
                // The scope switched under the request (the welcome's scope switch): the old answer is dropped by its
                // epoch, so ask again in the new table set.
                s_pending.ScopeEpoch = Entities.Current.Epoch;
                RequestMembers(s_pending.Id, row);
            }

            ReadOnlySpan<int> slots;
            EdgeState state;
            if (s_pending.Id.Kind == EntityKind.Album)
            {
                slots = Entities.Current.Edges.AlbumTracks.Targets(row.Slot);
                state = Entities.Current.Edges.AlbumTracks.State(row.Slot);
            }
            else
            {
                slots = Entities.Current.Edges.PlaylistTracks.Targets(row.Slot);
                state = Entities.Current.Edges.PlaylistTracks.State(row.Slot);
            }

            int members = 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i] > Table.None) members++;
            if (members == 0)
            {
                if (state == EdgeState.Complete)
                {
                    Emit(Headless.JsonLine.Fault(nowMs, "context-empty", s_pending.Id.Text));
                    s_pending = default;
                }
                else if (nowMs - s_pending.StartedMs > ContextTimeoutMs)
                {
                    Emit(Headless.JsonLine.Fault(nowMs, "context-timeout", "no members for " + s_pending.Id.Text + " after " + ContextTimeoutMs + " ms"));
                    s_pending = default;
                }
                return;
            }

            var memberRefs = new EntityRef[members];
            int m = 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i] > Table.None) memberRefs[m++] = new EntityRef(EntityKind.Track, slots[i]);
            var refs = new EntityRef[members];
            var rows = new QueueEdge[members];
            int n = Headless.BuildContextQueue(memberRefs, 0, refs, rows);
            EntityId context = s_pending.Id;
            int fromMs = s_pending.FromMs;
            s_pending = default;
            Queue.Replace(refs.AsSpan(0, n), rows.AsSpan(0, n));
            Playback.PlayNow(refs[0], context, Queue.CursorOf(0), Playback.PlayableKind.Audio, fromMs);
            Log.Info("headless", "context " + context.Text + " queued " + n + " rows");
        }

        // ── the readers ─────────────────────────────────────────────────────────────────────────────────────────────

        static void StartReader(string pipe)
        {
            var t = new Thread(static state => ReaderLoop((string)state!)) { IsBackground = true, Name = "wavee-headless-reader" };
            t.Start(pipe);
        }

        /// <summary>stdin (or the named pipe) → parsed commands → the loop. EOF closes the input: the runner finishes once
        /// what was queued has run, so `Get-Content x.wh | Wavee.exe --headless` runs a whole script. A reader that dies
        /// is a host error (3).</summary>
        static void ReaderLoop(string pipe)
        {
            try
            {
                using TextReader reader = pipe.Length == 0
                    ? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))
                    : OpenPipe(pipe);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    string text = line;
                    if (!Headless.TryParse(text, out Headless.Command cmd, out string err))
                    {
                        int id = cmd.Id;
                        s_loop.Post(() => Emit(Headless.JsonLine.Reply(s_loop.NowMs, id, false, text.Trim(), err)));
                        continue;
                    }
                    if (cmd.IsNone) continue;
                    s_loop.Post(() => s_runner?.Append(in cmd));
                }
                s_loop.Post(static () => s_runner?.CloseInput());
            }
            catch (Exception ex)
            {
                Log.Error("headless", "the command reader died", ex);
                s_loop.Post(static () => RequestExit(Headless.ExitCode.HostError));
            }
        }

        /// <summary>`\\.\pipe\&lt;name&gt;`, one client, mpv's shape. ASYNCHRONOUS on purpose: a synchronous pipe handle
        /// serialises a blocked ReadLine on the reader thread with every event the loop writes back down it.</summary>
        static TextReader OpenPipe(string name)
        {
            const string prefix = @"\\.\pipe\";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..];
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Log.Info("headless", "waiting for a client on " + prefix + name);
            server.WaitForConnection();
            Volatile.Write(ref s_pipeOut, new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true });
            return new StreamReader(server, new UTF8Encoding(false));
        }
    }

    // ══ --stress-audio ═══════════════════════════════════════════════════════════════════════════════════════════════════
    // docs/plans/wavee/playback-smoothness-implementation.md §4.18 (V-PA39; #167). The on-box half of the load-test harness.
    //
    //   Wavee.exe --stress-audio --file <flac|ogg|mp3> --burners 2 --memory-mib 2048 --minimize --seconds 60
    //
    // THE GATE runs through the REAL WASAPI device: `--file` opens a local file through the normal `FileByteSource` path,
    // `--track spotify:track:<id>` plays a track on the stored credential. Without either, or under `--fake`, the run goes
    // through `PacedSilentEndpoint` and is labelled `smoke` in every line it prints: it proves the harness and the engine's
    // starvation path under load in CI, it is NOT the gate.
    //
    // THE LOAD, started the moment the pump reports Playing: `--burners n` spin threads at each of BelowNormal / Normal /
    // AboveNormal / Highest (4n in all), `--memory-mib n` of byte[] touched and churned every 50 ms (memory pressure + GC),
    // `--minimize` a minimized top-level stand-in window (this arm is windowless, so there is no Wavee window to minimize;
    // a minimized window is what makes Windows treat the process as background), `--battery-saver` the process opted INTO
    // execution-speed throttling (the inverse of F4's opt-out, applied after the device exists so it wins).
    //
    // THE VERDICT: the glitch ledger over the window (`Metrics.GlitchIncidents`, tallied across ledger resets) and the engine's
    // xrun count; exit 0 when the run had no incident (`--allow-one` tolerates one), 2 when it had more, 1 when it could not be
    // completed (the track ended first, no audio, a fault), 64 usage.
    //
    // Every decision is PURE and in `Wavee.Tests/AudioStressTests.cs`: `StressOptions` (the argv parse and the smoke label),
    // `StressRules` (the burner classes, the allocator plan, the pass rule), `StressRun` (the run's state machine and the
    // incident tally), `StressReport` (the printed ledger summary and its JSON line). `StressAudioHost` boots the headless
    // composition and ticks the machine; `StressLoad` is the OS half.

    /// <summary>`--stress-audio [--file &lt;path&gt; | --track &lt;spotify:track:id&gt;] [--burners &lt;n per class&gt;] [--memory-mib &lt;n&gt;]
    /// [--minimize] [--battery-saver] [--seconds &lt;n&gt;] [--allow-one] [--fake] [--profile &lt;dir&gt;]`. Pure parse; tested.</summary>
    public readonly record struct StressOptions(string FilePath, string TrackUri, int Burners, int MemoryMib, bool Minimize,
                                                bool BatterySaver, int Seconds, bool AllowOne, bool Fake, string Profile)
    {
        public const int DefaultSeconds = 60, MaxBurnersPerClass = 64, MaxMemoryMib = 16_384, MaxSeconds = 3_600;
        public const string TrackPrefix = "spotify:track:";

        public const string Usage =
            "usage: Wavee.exe --stress-audio [--file <path> | --track <spotify:track:id>] [--burners <n>] [--memory-mib <n>]\n" +
            "                 [--minimize] [--battery-saver] [--seconds <n>] [--allow-one] [--fake] [--profile <dir>]\n" +
            "  --file          play a local file through the real device (the gate)\n" +
            "  --track         play a Spotify track on the stored credential through the real device (the gate)\n" +
            "                  neither: the --fake silent voice through the paced silent endpoint, labelled `smoke` (CI only)\n" +
            "  --burners       spin threads at EACH of BelowNormal, Normal, AboveNormal and Highest (default 0, at most 64)\n" +
            "  --memory-mib    byte[] kept touched and churned every 50 ms (default 0, at most 16384)\n" +
            "  --minimize      a minimized stand-in window, so Windows treats the process as background\n" +
            "  --battery-saver apply execution-speed throttling to the process (the inverse of the audio leaf's opt-out)\n" +
            "  --seconds       the length of the measured window (default 60)\n" +
            "  --allow-one     tolerate one incident (exit 0); default: none\n" +
            "  --fake          silent endpoint: the run is a smoke run, whatever the source\n" +
            "  exit codes: 0 no incident, 2 incidents, 1 the run could not complete, 64 usage, 67/75/69 credential / login / device";

        /// <summary>The run goes through a real audio source (`--file` or `--track`).</summary>
        public bool HasSource => FilePath.Length > 0 || TrackUri.Length > 0;

        /// <summary>V-PA39: the paced silent endpoint — no source, or `--fake` — is a SMOKE run, never the gate.</summary>
        public bool Smoke => Fake || !HasSource;

        /// <summary>`smoke` or `device`: printed on every line of the report.</summary>
        public string Label => Smoke ? "smoke" : "device";

        /// <summary>`--track` needs a signed-in session before it can play.</summary>
        public bool NeedsLogin => TrackUri.Length > 0;

        public static bool TryParse(string[] args, out StressOptions options, out string usage)
        {
            options = new StressOptions("", "", 0, 0, false, false, DefaultSeconds, false, false, "");
            usage = "";
            string file = "", track = "", profile = "";
            int burners = 0, memory = 0, seconds = DefaultSeconds;
            bool minimize = false, battery = false, allowOne = false, fake = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--stress-audio": break;
                    case "--fake": fake = true; break;
                    case "--minimize": minimize = true; break;
                    case "--battery-saver": battery = true; break;
                    case "--allow-one": allowOne = true; break;
                    case "--file": if (!Text(args, ref i, a, out file, ref usage)) return false; break;
                    case "--track": if (!Text(args, ref i, a, out track, ref usage)) return false; break;
                    case "--profile": if (!Text(args, ref i, a, out profile, ref usage)) return false; break;
                    case "--burners": if (!Count(args, ref i, a, 0, MaxBurnersPerClass, out burners, ref usage)) return false; break;
                    case "--memory-mib": if (!Count(args, ref i, a, 0, MaxMemoryMib, out memory, ref usage)) return false; break;
                    case "--seconds": if (!Count(args, ref i, a, 1, MaxSeconds, out seconds, ref usage)) return false; break;
                    default:
                        usage = "unknown argument '" + a + "'\n" + Usage;
                        return false;
                }
            }
            if (file.Length > 0 && track.Length > 0) { usage = "--file and --track cannot be combined\n" + Usage; return false; }
            if (track.Length > 0 && (!track.StartsWith(TrackPrefix, StringComparison.Ordinal) || track.Length == TrackPrefix.Length))
            {
                usage = "--track wants a spotify:track:<id> uri, not '" + track + "'\n" + Usage;
                return false;
            }
            if (fake && track.Length > 0) { usage = "--fake has no Spotify session: use --file, or nothing, with it\n" + Usage; return false; }
            options = new StressOptions(file, track, burners, memory, minimize, battery, seconds, allowOne, fake, profile);
            return true;
        }

        static bool Text(string[] args, ref int i, string flag, out string value, ref string usage)
        {
            value = "";
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                usage = flag + " wants a value\n" + Usage;
                return false;
            }
            value = args[++i];
            return true;
        }

        static bool Count(string[] args, ref int i, string flag, int min, int max, out int count, ref string usage)
        {
            count = 0;
            if (!Text(args, ref i, flag, out string text, ref usage)) return false;
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)
                && count >= min && count <= max) return true;
            usage = flag + " wants a whole number from " + min + " to " + max + ", not '" + text + "'\n" + Usage;
            return false;
        }
    }

    /// <summary>The allocator's shape for `--memory-mib`: <see cref="ChunkCount"/> chunks of <see cref="ChunkBytes"/> kept resident
    /// and touched, <see cref="ReplacePerTick"/> of them replaced (a fresh LOH allocation, touched page by page) every
    /// <see cref="TickMs"/> — about one full turnover per four seconds, so the GC has real work for the whole run. PURE.</summary>
    public readonly record struct AllocatorPlan(int ChunkBytes, int ChunkCount, int ReplacePerTick, int TickMs)
    {
        public const int ChunkMib = 4, DefaultTickMs = 50;

        public static AllocatorPlan None => new(0, 0, 0, 0);
        public bool IsNone => ChunkCount == 0;
        public long ResidentBytes => (long)ChunkBytes * ChunkCount;

        public static AllocatorPlan For(int memoryMib)
        {
            if (memoryMib <= 0) return None;
            int chunkMib = Math.Min(ChunkMib, memoryMib);
            int count = (memoryMib + chunkMib - 1) / chunkMib;
            return new AllocatorPlan(chunkMib * 1024 * 1024, count, Math.Max(1, count / 80), DefaultTickMs);
        }
    }

    /// <summary>The stress arm's small pure rules.</summary>
    public static class StressRules
    {
        /// <summary>The burner priority classes, in the order the plan lists them (read-only).</summary>
        public static IReadOnlyList<ThreadPriority> BurnerClasses { get; } =
            Array.AsReadOnly(new[] { ThreadPriority.BelowNormal, ThreadPriority.Normal, ThreadPriority.AboveNormal, ThreadPriority.Highest });

        /// <summary>`--burners n` is n threads in EACH class.</summary>
        public static int BurnerThreads(int perClass) => Math.Max(0, perClass) * BurnerClasses.Count;

        public static AllocatorPlan Allocator(int memoryMib) => AllocatorPlan.For(memoryMib);

        /// <summary>The pass rule: no incident, or one under `--allow-one`.</summary>
        public static bool Passed(int incidents, bool allowOne) => incidents <= (allowOne ? 1 : 0);

        /// <summary>What the report names the source: the file's NAME (never its folder), the track uri, or the silent voice.</summary>
        public static string SourceLabel(in StressOptions o)
            => o.FilePath.Length > 0 ? "file:" + Path.GetFileName(o.FilePath)
             : o.TrackUri.Length > 0 ? "track:" + o.TrackUri
             : "silent-voice";
    }

    /// <summary>What the run's machine reads each tick (the SHELL fills it once per tick, on the loop thread).</summary>
    /// <param name="Phase">`Playback.Phase.ToString()`: Idle / Loading / Playing / Paused / Ended.</param>
    /// <param name="Fault">`Playback.Fault.ToString()`; "None" when healthy.</param>
    /// <param name="Xruns">`Metrics.Xruns`: the engine's RT incident count, every session this process.</param>
    /// <param name="GlitchIncidents">`Metrics.GlitchIncidents`: the session ledger's incident count (resets with the session).</param>
    /// <param name="LongestStallMs">`Metrics.LongestStallMs`: the session ledger's longest stall.</param>
    /// <param name="VerdictKey">`Metrics.GlitchVerdictKey`: the ledger's verdict key (clean, gcPauses, byteStarved, producerStarved, deviceLate).</param>
    public readonly record struct StressInput(long NowMs, bool Online, string Phase, bool Buffering, string Fault, int PositionMs,
                                              long Xruns, long XrunFramesLost, int GlitchIncidents, long LongestStallMs, string VerdictKey);

    public enum StressVerb : byte
    {
        None,
        /// <summary>Start the playback (import and play the file, or play the track).</summary>
        Play,
        /// <summary>Audio is flowing: start the burners, the allocator, the stand-in window, the throttling. The window opens now.</summary>
        BeginLoad,
        /// <summary>The window is over (or the track ended first): print <see cref="StressRun.Report"/> and exit with the code.</summary>
        Finish,
        /// <summary>The run cannot complete: print the reason and exit with the code.</summary>
        Fail,
    }

    public readonly record struct StressStep(StressVerb Verb, int Code = 0, string Reason = "");

    /// <summary>The printed ledger summary of one run. PURE: <see cref="ToText"/> is the human lines, <see cref="ToJsonLine"/> the
    /// machine line (`"kind":"stress"`, like the headless lines). <paramref name="LongestStallMs"/> is −1 when the source of the
    /// numbers does not measure it (the engine-level smoke run).</summary>
    public readonly record struct StressReport(
        string Label, string Source, int RequestedSeconds, int PlayedSeconds, int Burners, int MemoryMib, bool Minimized, bool BatterySaver,
        int Incidents, long LongestStallMs, string Verdict, long Xruns, long XrunFramesLost, int AllowedIncidents, bool EndedEarly)
    {
        public bool Passed => !EndedEarly && Incidents <= AllowedIncidents;

        /// <summary>0 pass, 2 incidents, 1 the track ended before the window did (inconclusive).</summary>
        public int ExitCode => EndedEarly ? Headless.ExitCode.Fault : Passed ? Headless.ExitCode.Ok : Headless.ExitCode.Assertion;

        public string ToText()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("stress-audio label=").Append(Label)
              .Append(" source=").Append(Source)
              .Append(" seconds=").Append(PlayedSeconds.ToString(inv)).Append('/').Append(RequestedSeconds.ToString(inv))
              .Append(" burners=").Append(Burners.ToString(inv)).Append('x').Append(StressRules.BurnerClasses.Count.ToString(inv))
              .Append(" memoryMiB=").Append(MemoryMib.ToString(inv))
              .Append(" minimized=").Append(Minimized ? "yes" : "no")
              .Append(" batterySaver=").Append(BatterySaver ? "yes" : "no")
              .Append('\n');
            sb.Append("ledger incidents=").Append(Incidents.ToString(inv))
              .Append(" longestStallMs=").Append(LongestStallMs < 0 ? "n/a" : LongestStallMs.ToString(inv))
              .Append(" verdict=").Append(Verdict)
              .Append(" xruns=").Append(Xruns.ToString(inv))
              .Append(" xrunFramesLost=").Append(XrunFramesLost.ToString(inv))
              .Append('\n');
            sb.Append("result ");
            if (EndedEarly)
                sb.Append("INCONCLUSIVE ended-early played=").Append(PlayedSeconds.ToString(inv)).Append("s of ").Append(RequestedSeconds.ToString(inv)).Append('s');
            else
                sb.Append(Passed ? "PASS" : "FAIL").Append(" incidents=").Append(Incidents.ToString(inv)).Append(" allowed=").Append(AllowedIncidents.ToString(inv));
            if (Label == "smoke")
                sb.Append("\nnote smoke run: the silent endpoint is paced to the wall clock, not a device; this is not the on-box gate");
            return sb.ToString();
        }

        public string ToJsonLine(long t)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
            using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("t", t);
                w.WriteString("kind", "stress");
                w.WriteString("label", Label);
                w.WriteString("source", Source);
                w.WriteNumber("seconds", RequestedSeconds);
                w.WriteNumber("played", PlayedSeconds);
                w.WriteNumber("burners", Burners);
                w.WriteNumber("memoryMib", MemoryMib);
                w.WriteBoolean("minimized", Minimized);
                w.WriteBoolean("batterySaver", BatterySaver);
                w.WriteNumber("incidents", Incidents);
                w.WriteNumber("longestStallMs", LongestStallMs);
                w.WriteString("verdict", Verdict);
                w.WriteNumber("xruns", Xruns);
                w.WriteNumber("xrunFramesLost", XrunFramesLost);
                w.WriteNumber("allowed", AllowedIncidents);
                w.WriteBoolean("endedEarly", EndedEarly);
                w.WriteBoolean("passed", Passed);
                w.WriteNumber("code", ExitCode);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>The run's state machine, one <see cref="Tick"/> per host tick: wait for the session (`--track`), post the play, wait
    /// for audio, open the measured window (the load starts), tally incidents across ledger resets until the window is over, then
    /// report. PURE: no clock, no engine, no thread — the host feeds it a <see cref="StressInput"/> and executes the verb.</summary>
    public sealed class StressRun
    {
        public const int LoginTimeoutMs = 60_000, AudioStartTimeoutMs = 30_000;

        public enum Stage : byte { Boot, WaitingForAudio, Running, Done }

        readonly StressOptions _o;
        Stage _stage;
        long _t0 = -1, _playAt, _windowAt;
        int _lastLedger, _ledgerIncidents;
        long _longestStallMs, _xrunBase, _frameBase, _xruns, _frames;
        string _verdict = "clean";
        bool _minimized;

        public StressRun(StressOptions options) => _o = options;

        public Stage Current => _stage;

        /// <summary>The ledger's incidents over the window, summed across ledger resets (a new session starts the count from zero),
        /// or the engine's xrun delta when that is larger: an xrun the RT counted is an incident even if the ledger's drain missed it.</summary>
        public int Incidents => Math.Max(_ledgerIncidents, (int)Math.Clamp(_xruns, 0L, int.MaxValue));

        public long LongestStallMs => _longestStallMs;

        /// <summary>Set once the run finished (<see cref="StressVerb.Finish"/>).</summary>
        public StressReport? Report { get; private set; }

        /// <summary>The host tells the machine what the load turned out to be (the stand-in window may fail to minimize).</summary>
        public void NoteLoad(bool minimized) => _minimized = minimized;

        public StressStep Tick(in StressInput i)
        {
            if (_t0 < 0) _t0 = i.NowMs;
            switch (_stage)
            {
                case Stage.Boot:
                    if (_o.NeedsLogin && !i.Online)
                        return i.NowMs - _t0 > LoginTimeoutMs
                            ? Fail(Headless.ExitCode.LoginTimeout, "the session was not online within " + LoginTimeoutMs / 1000 + " s")
                            : default;
                    _stage = Stage.WaitingForAudio;
                    _playAt = i.NowMs;
                    return new StressStep(StressVerb.Play);

                case Stage.WaitingForAudio:
                    if (i.Fault != "None") return Fail(Headless.ExitCode.Fault, "playback fault " + i.Fault);
                    if (i.Phase == "Playing" && !i.Buffering)
                    {
                        _stage = Stage.Running;
                        _windowAt = i.NowMs;
                        _lastLedger = i.GlitchIncidents;     // earlier incidents (the open itself) are not the window's
                        _xrunBase = i.Xruns;
                        _frameBase = i.XrunFramesLost;
                        return new StressStep(StressVerb.BeginLoad);
                    }
                    return i.NowMs - _playAt > AudioStartTimeoutMs
                        ? Fail(_o.Smoke ? Headless.ExitCode.Fault : Headless.ExitCode.NoEndpoint, "no audio within " + AudioStartTimeoutMs / 1000 + " s")
                        : default;

                case Stage.Running:
                    Observe(in i);
                    if (i.Fault != "None") return Fail(Headless.ExitCode.Fault, "playback fault " + i.Fault);
                    long elapsedMs = i.NowMs - _windowAt;
                    if (elapsedMs >= _o.Seconds * 1000L) return Finish(_o.Seconds, endedEarly: false);
                    if (i.Phase is "Ended" or "Idle") return Finish((int)(elapsedMs / 1000), endedEarly: true);
                    return default;

                default:
                    return default;
            }
        }

        void Observe(in StressInput i)
        {
            // A ledger count BELOW the last one is a new session's ledger: count from zero again.
            _ledgerIncidents += i.GlitchIncidents >= _lastLedger ? i.GlitchIncidents - _lastLedger : i.GlitchIncidents;
            _lastLedger = i.GlitchIncidents;
            if (i.LongestStallMs > _longestStallMs) _longestStallMs = i.LongestStallMs;
            if (i.VerdictKey != "clean") _verdict = i.VerdictKey;
            _xruns = Math.Max(0L, i.Xruns - _xrunBase);
            _frames = Math.Max(0L, i.XrunFramesLost - _frameBase);
        }

        StressStep Fail(int code, string reason)
        {
            _stage = Stage.Done;
            return new StressStep(StressVerb.Fail, code, reason);
        }

        StressStep Finish(int playedSeconds, bool endedEarly)
        {
            _stage = Stage.Done;
            var report = new StressReport(_o.Label, StressRules.SourceLabel(in _o), _o.Seconds, playedSeconds, _o.Burners, _o.MemoryMib,
                _minimized, _o.BatterySaver, Incidents, _longestStallMs, _verdict, _xruns, _frames, _o.AllowOne ? 1 : 0, endedEarly);
            Report = report;
            return new StressStep(StressVerb.Finish, report.ExitCode);
        }
    }

    /// <summary>The OS half of the load: burner threads, the allocator, the minimized stand-in window, the battery-saver
    /// throttling. Everything it starts is a background thread and is undone by <see cref="Dispose"/>.</summary>
    public sealed class StressLoad : IDisposable
    {
        const uint WsOverlappedWindow = 0x00CF0000;
        const int SwShowMinNoActive = 7;
        const uint WmQuit = 0x0012;

        readonly List<Thread> _threads = new();
        readonly ManualResetEventSlim _windowUp = new(false);
        volatile bool _stop;
        double _sink;
        uint _windowThreadId;
        bool _batterySaverOn;

        StressLoad() { }

        /// <summary>True when the stand-in window exists and is iconic.</summary>
        public bool Minimized { get; private set; }

        public int BurnerThreads { get; private set; }

        public AllocatorPlan Allocator { get; private set; }

        public static StressLoad Start(StressOptions o)
        {
            var load = new StressLoad();
            load.StartBurners(o.Burners);
            load.StartAllocator(StressRules.Allocator(o.MemoryMib));
            if (o.Minimize) load.StartMinimizedWindow();
            if (o.BatterySaver) load.ApplyBatterySaver();
            Log.Info("stress", "stress.begin burnerThreads=" + load.BurnerThreads + " residentMiB=" + (load.Allocator.ResidentBytes >> 20)
                               + " minimized=" + load.Minimized + " batterySaver=" + load._batterySaverOn);
            return load;
        }

        void StartBurners(int perClass)
        {
            for (int c = 0; c < StressRules.BurnerClasses.Count; c++)
            {
                for (int n = 0; n < perClass; n++)
                {
                    ThreadPriority priority = StressRules.BurnerClasses[c];
                    var thread = new Thread(Burn) { IsBackground = true, Priority = priority, Name = "wavee-stress-burn-" + priority };
                    thread.Start();
                    _threads.Add(thread);
                    BurnerThreads++;
                }
            }
        }

        /// <summary>A dependent sqrt chain: loop-carried, bounded, never folded away, and one core's worth of work per thread.</summary>
        void Burn()
        {
            double x = 1.000001;
            while (!_stop)
            {
                for (int i = 0; i < 50_000; i++) x = Math.Sqrt(x + 1.0) * 1.0001;
            }
            Interlocked.Exchange(ref _sink, x);
        }

        void StartAllocator(AllocatorPlan plan)
        {
            Allocator = plan;
            if (plan.IsNone) return;
            var thread = new Thread(() => Churn(plan)) { IsBackground = true, Name = "wavee-stress-alloc" };
            thread.Start();
            _threads.Add(thread);
        }

        void Churn(AllocatorPlan plan)
        {
            var chunks = new byte[plan.ChunkCount][];
            for (int i = 0; i < chunks.Length && !_stop; i++)
            {
                try { chunks[i] = Touch(plan.ChunkBytes); }
                catch (OutOfMemoryException)
                {
                    Log.Warn("stress", "memory pressure capped at " + i * (plan.ChunkBytes >> 20) + " MiB: out of memory");
                    break;
                }
            }
            int next = 0;
            while (!_stop)
            {
                Thread.Sleep(plan.TickMs);
                for (int k = 0; k < plan.ReplacePerTick && !_stop; k++)
                {
                    try { chunks[next] = Touch(plan.ChunkBytes); }
                    catch (OutOfMemoryException) { }
                    next = (next + 1) % chunks.Length;
                }
            }
            GC.KeepAlive(chunks);
        }

        /// <summary>A fresh array with every page written, so the memory is committed and not merely reserved.</summary>
        static byte[] Touch(int bytes)
        {
            var a = new byte[bytes];
            for (int i = 0; i < a.Length; i += 4096) a[i] = 1;
            return a;
        }

        void StartMinimizedWindow()
        {
            var thread = new Thread(WindowThread) { IsBackground = true, Name = "wavee-stress-window" };
            thread.Start();
            _threads.Add(thread);
            _windowUp.Wait(2_000);
        }

        void WindowThread()
        {
            nint hwnd = StressNative.CreateWindowExW(0, "STATIC", "Wavee stress stand-in (minimized)", WsOverlappedWindow, 100, 100, 360, 120, 0, 0, 0, 0);
            if (hwnd != 0)
            {
                StressNative.ShowWindow(hwnd, SwShowMinNoActive);
                Minimized = StressNative.IsIconic(hwnd) != 0;
            }
            _windowThreadId = StressNative.GetCurrentThreadId();
            _windowUp.Set();
            if (hwnd == 0) return;
            var msg = default(StressNative.Msg);
            while (StressNative.GetMessageW(ref msg, 0, 0, 0) > 0)
            {
                StressNative.TranslateMessage(ref msg);
                StressNative.DispatchMessageW(ref msg);
            }
            StressNative.DestroyWindow(hwnd);
        }

        void ApplyBatterySaver()
        {
            _batterySaverOn = StressNative.SetExecutionSpeedThrottling(on: true);
            if (!_batterySaverOn) Log.Warn("stress", "battery-saver emulation failed: SetProcessInformation(ProcessPowerThrottling) was refused");
        }

        public void Dispose()
        {
            _stop = true;
            if (_windowThreadId != 0) StressNative.PostThreadMessageW(_windowThreadId, WmQuit, 0, 0);
            foreach (Thread t in _threads) t.Join(2_000);
            if (_batterySaverOn)
            {
                StressNative.SetExecutionSpeedThrottling(on: false);   // back to the audio leaf's opt-out
                _batterySaverOn = false;
            }
            _windowUp.Dispose();
        }
    }

    internal static partial class StressNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            public nint HWnd;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int PtX;
            public int PtY;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
                                                     nint parent, nint menu, nint instance, nint param);

        [LibraryImport("user32.dll")] internal static partial int ShowWindow(nint hWnd, int command);
        [LibraryImport("user32.dll")] internal static partial int IsIconic(nint hWnd);
        [LibraryImport("user32.dll")] internal static partial int DestroyWindow(nint hWnd);
        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] internal static partial int GetMessageW(ref Msg msg, nint hWnd, uint filterMin, uint filterMax);
        [LibraryImport("user32.dll")] internal static partial int TranslateMessage(ref Msg msg);
        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessageW(ref Msg msg);
        [LibraryImport("user32.dll")] internal static partial int PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);
        [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
        [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();
        [LibraryImport("kernel32.dll")] internal static partial int SetProcessInformation(nint process, int infoClass, ref ProcessPowerThrottlingState info, uint size);

        /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED with the control bit set: <paramref name="on"/> = throttle (battery saver),
        /// off = never throttle (the audio leaf's own opt-out, `PowerThrottling.OptOut`). False when the call is refused.</summary>
        internal static bool SetExecutionSpeedThrottling(bool on)
        {
            const int ProcessPowerThrottling = 4;
            var state = new ProcessPowerThrottlingState { Version = 1, ControlMask = 0x1, StateMask = on ? 0x1u : 0u };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, 12) != 0;
        }
    }

    /// <summary>The `--stress-audio` host: the headless composition (one writer thread, the marshallers, the overlay settings, the
    /// protected credential) with the stress machine in place of the script runner. `--fake`/no-source smoke runs of the silent
    /// voice skip the pump altogether and load the engine session directly.</summary>
    public static class StressAudioHost
    {
        const int TickMs = 100;

        static HeadlessLoop s_loop = null!;
        static StressOptions s_options;
        static StressRun s_run = null!;
        static StressLoad? s_load;
        static Timer? s_tick;
        static int s_exit, s_tickQueued;
        static bool s_exiting;
        static readonly Action s_tickAction = Tick;

        public static int Run(StressOptions o)
        {
            s_options = o;
            if (o.FilePath.Length > 0 && !File.Exists(o.FilePath))
            {
                Console.Error.WriteLine("--file: no such file: " + o.FilePath);
                return Headless.ExitCode.Usage;
            }
            string profile = Platform.LocalFolder;
            if (!ProfileWritable(profile, out string why))
            {
                Console.Error.WriteLine(why);
                return Headless.ExitCode.Config;
            }
            if (!o.HasSource) return RunSilentVoice(o);

            s_loop = new HeadlessLoop();
            s_run = new StressRun(o);

            // the stores: the same safety as the headless host (settings never reach HKCU; the credential is never removed)
            Platform.UseSettings(new Headless.OverlaySettings(Platform.BackingSettings));
            ICredentialProtector protector = OperatingSystem.IsWindows() ? new DpapiProtector() : new NoOpProtector();
            Platform.UseCredentialSlot(new Headless.ProtectedLocalStore(new FileLocalStore(Platform.StorePath), "stress",
                refused: static k => Log.Warn("stress", "refused to remove " + k + " (a stress run never clears the credential)")), protector);
            if (o.NeedsLogin && !Platform.HasStoredCredential())
            {
                Console.Error.WriteLine("--track needs a stored credential: sign in once in the Wavee window on this machine");
                return Headless.ExitCode.NoCredential;
            }

            // the marshallers: ONE writer — this thread, blocked on the loop
            Playback.ToUi = s_loop.Post;
            Spotify.Post = s_loop.Post;
            Store.Post = s_loop.Post;
            Palette.Post = s_loop.Post;
            Playback.FrameNowMs = static () => s_loop.NowMs;
            WasapiAudioDevice.DiagSink = static s => Log.Info("audio", s);
            WasapiAudioDevice.FormatSink = static f => Log.Info("audio", "device format " + f);

            // `--fake --file`: the paced silent endpoint, labelled smoke. BEFORE `Playback.Boot()`, which queues the backend build on the
            // pump chain (the flag is read there); the headless host calls it after, and wins the race only by being quick.
            if (o.Smoke) Playback.Audio.UseSilentEndpoint();

            App.RegisterShapes();
            Store.Use(null);
            Entities.Boot(Platform.Scope);
            Spotify.Boot();
            Playback.Boot();
            Spotify.Api.Boot();
            Playback.Audio.LocalPath = Playlist.LocalPathOf;          // a local file's id → its path (Modules.InstallUi's seam, which this host does not run)
            if (o.NeedsLogin) Spotify.Login();

            s_tick = new Timer(static _ =>
            {
                if (Interlocked.CompareExchange(ref s_tickQueued, 1, 0) == 0) s_loop.Post(s_tickAction);
            }, null, TickMs, TickMs);

            try { s_loop.Run(); }
            catch (Exception ex)
            {
                Log.Error("stress", "the loop faulted", ex);
                Console.Error.WriteLine("stress-audio: the host faulted: " + ex.GetType().Name + ": " + ex.Message);
                s_exit = Headless.ExitCode.HostError;
            }
            finally
            {
                s_tick?.Dispose();
                s_load?.Dispose();
            }
            return s_exit;
        }

        /// <summary>The smoke run with no source: the `--fake` silent voice through <c>PacedSilentEndpoint</c>, loaded directly at the
        /// engine session (the pump and the catalog are never booted — the session is the unit under load).</summary>
        static int RunSilentVoice(StressOptions o)
        {
            var session = Playback.Audio.OpenSilentSession(Playback.Audio.SilentFormat, (o.Seconds + 5L) * 1000, effects: null, volume: 1f);
            StressLoad? load = null;
            try
            {
                session.PlayAsync().AsTask().GetAwaiter().GetResult();
                load = StressLoad.Start(o);
                Thread.Sleep(o.Seconds * 1000);
                long xruns = session.XrunCount, lost = session.XrunFramesLost;
                var report = new StressReport(o.Label, StressRules.SourceLabel(in o), o.Seconds, o.Seconds, o.Burners, o.MemoryMib, load.Minimized,
                    o.BatterySaver, (int)Math.Min(int.MaxValue, xruns), -1, xruns == 0 ? "clean" : "unknown", xruns, lost, o.AllowOne ? 1 : 0, false);
                Console.Out.WriteLine(report.ToText());
                Console.Out.WriteLine(report.ToJsonLine(Environment.TickCount64));
                return report.ExitCode;
            }
            finally
            {
                load?.Dispose();
                try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { Log.Warn("stress", "the silent session did not dispose cleanly", ex); }
            }
        }

        /// <summary>Loop thread, every 100 ms: the host's frame (Fetch.Pump + Entities.Publish), then one step of the machine.</summary>
        static void Tick()
        {
            Volatile.Write(ref s_tickQueued, 0);
            if (s_exiting) return;
            long nowMs = s_loop.NowMs;

            Entities.Now = Store.ToApp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Fetch.Pump();
            Entities.Publish();

            Playback.State p = Playback.Snap();
            var m = Playback.Audio.Metrics.Read();
            var input = new StressInput(nowMs, Spotify.Current.IsOnline, p.Phase.ToString(), p.Buffering, p.Error.ToString(), p.Position(nowMs),
                m.Xruns, m.XrunFramesLost, (int)m.GlitchIncidents, (long)m.LongestStallMs, m.GlitchVerdictKey ?? GlitchLedger.VerdictClean);

            StressStep step = s_run.Tick(in input);
            switch (step.Verb)
            {
                case StressVerb.Play:
                    if (!StartPlayback(out string error))
                    {
                        Console.Error.WriteLine("stress-audio: " + error);
                        RequestExit(Headless.ExitCode.Fault);
                    }
                    break;
                case StressVerb.BeginLoad:
                    s_load = StressLoad.Start(s_options);
                    s_run.NoteLoad(s_load.Minimized);
                    break;
                case StressVerb.Finish:
                    {
                        StressReport report = s_run.Report!.Value;
                        Console.Out.WriteLine(report.ToText());
                        Console.Out.WriteLine(report.ToJsonLine(nowMs));
                        RequestExit(step.Code);
                        break;
                    }
                case StressVerb.Fail:
                    Console.Error.WriteLine("stress-audio: " + step.Reason);
                    Console.Out.WriteLine(Headless.JsonLine.Fault(nowMs, "stress-failed", step.Reason));
                    RequestExit(step.Code);
                    break;
            }
        }

        /// <summary>Loop thread. `--file`: import the file as a local track and play it as a one-row queue; `--track`: the headless
        /// `play` of one track. Both the way the headless host starts a single playable.</summary>
        static bool StartPlayback(out string error)
        {
            error = "";
            if (s_options.FilePath.Length > 0)
            {
                Track track = Playlist.ImportLocalFile(Path.GetFullPath(s_options.FilePath));
                if (!track.IsValid) { error = "the file could not be imported as a local track: " + s_options.FilePath; return false; }
                var row = new EntityRef(EntityKind.Track, track.Slot);
                PlayRow(row, row.Id);
                return true;
            }
            if (!EntityId.TryParse(s_options.TrackUri.AsSpan(), out EntityId id) || !id.IsPlayable) { error = "not a track uri: " + s_options.TrackUri; return false; }
            EntityRef trackRow = Entities.Ref(id);
            if (trackRow.IsNone) { error = "no row for " + s_options.TrackUri; return false; }
            PlayRow(trackRow, id);
            return true;
        }

        static void PlayRow(EntityRef row, EntityId context)
        {
            Span<EntityRef> refs = [row];
            Span<QueueEdge> rows = stackalloc QueueEdge[1];
            int n = Headless.BuildContextQueue(refs, 0, refs, rows);
            Queue.Replace(refs[..n], rows[..n]);
            Playback.PlayNow(row, context, Queue.CursorOf(0), Playback.PlayableKind.Audio, 0);
        }

        /// <summary>Loop thread. Stop playback, close the session WITHOUT clearing the credential, let the queue drain, return.</summary>
        static void RequestExit(int code)
        {
            if (s_exiting) return;
            s_exiting = true;
            s_exit = code;
            s_tick?.Change(Timeout.Infinite, Timeout.Infinite);
            try { Playback.Stop(); } catch (Exception ex) { Log.Warn("stress", "stop on exit failed", ex); }
            try { Spotify.Apply(new Spotify.SessionEvent(Spotify.SessionEventKind.Disconnect)); }
            catch (Exception ex) { Log.Warn("stress", "disconnect on exit failed", ex); }
            s_loop.Stop();
        }

        static bool ProfileWritable(string dir, out string why)
        {
            why = "";
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".stress-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                why = dir + ": " + ex.Message;
                return false;
            }
        }
    }
}
