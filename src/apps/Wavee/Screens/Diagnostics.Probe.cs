// ── Screens/Diagnostics.Probe.cs ───────────────────────────────────────────────────────────────────────────────────
// the CLI probe arms: --headless (the headless host, below), and — owner S, Wave 6 — --perf-bench, --startup-bench,
// --crash-probe, --lyrics-advance-probe (ch 22 (a) — the env-var switch is deleted), --qr-dump (ch 28 §1.5),
// NotificationSimulator (ch 14, +195), the process receipts
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
// removed; the Connect device id is namespaced (`device.id.headless`); no library.db unless `--store`; `--profile`
// redirects the whole profile (App.cs sets `Platform.ProfileRoot` from `ProfileArg` before `Platform.Boot`).

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
            code = 0;
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
        /// BEFORE `Platform.Boot` — every path (store.json, logs, library.db, cache/) hangs off it.</summary>
        public static string ProfileArg(string[] args)
        {
            int i = Array.IndexOf(args, "--profile");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
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
            "  --store         open <profile>\\library.db (default: memory-only catalog)\n" +
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
            //    facade, whose Get reads the backing store, which would be this overlay — an infinite recursion.
            Platform.UseSettings(new Headless.OverlaySettings(RegistryAppSettings.Open("Wavee", "Wavee")));
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

            // 4. boot (no Shell, no Modules, no Store unless asked)
            Store.Use(o.Store ? Path.Combine(profile, "library.db") : null);
            Entities.Boot(Platform.Scope);
            Spotify.Boot();
            Playback.Boot();
            Spotify.Api.Boot();                                       // Fetch.Register(Transport); idempotent
            if (o.Silent) Playback.Audio.UseSilentEndpoint();
            s_connect = o.Connect;

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

            if (o.Store) { try { Store.Shutdown(); } catch (Exception ex) { Log.Warn("headless", "store shutdown failed", ex); } }
            return Finish(s_exit);
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
        /// `Welcome` effect (Spotify.Session.Apply) — this host does not repeat them; it only sends the optional Connect
        /// hello (§2.9).</summary>
        static void OnOnline()
        {
            s_wasOnline = true;
            if (s_connect) Hello();
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
                CdnRequests: (long)st.Requests, CdnInFlight: (int)st.InFlight, CdnCancelled: (int)st.Cancelled,
                CdnHeads: (long)st.Heads, CdnResolves: (long)st.Resolves, CdnCacheHits: (long)st.CacheHits,
                Xruns: (int)m.Xruns, GaplessExact: (int)m.GaplessExact, GaplessDegraded: (int)m.GaplessDegraded,
                FirstAudioMs: (int)m.FirstAudioMs,
                CdnPeakInFlight: (int)st.PeakInFlight, CdnPingMs: (int)st.PingMs, CdnBytesPerSecond: (long)st.BytesPerSecond,
                RingWaits: (int)st.RingWaits, RingStarves: (int)st.RingStarves, HeadBytes: (int)st.HeadBytes,
                FirstAudioFromHead: m.FirstAudioFromHead, LastSeekMs: (int)m.LastSeekMs, LastSeekLatencyMs: (int)m.LastSeekLatencyMs,
                LastSeekKind: (byte)m.LastSeekKind, GaplessAbandoned: (int)m.GaplessAbandoned, DecodeXRealtime: (float)m.DecodeXRealtime);
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
            if (now.LastSeekMs != prev.LastSeekMs || now.LastSeekLatencyMs != prev.LastSeekLatencyMs)
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
                if (e.Category is "audio" or "spotify" or "playback" or "connect")
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
                case Headless.Verb.Pause: Playback.Pause(); return true;
                case Headless.Verb.Resume: Playback.Resume(); return true;
                case Headless.Verb.Toggle: Playback.TogglePlay(); return true;
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
                    if (s_connect && s_wasOnline) Hello();
                    return true;
                case Headless.Verb.Log: Log.Info("headless", c.Arg0); return true;
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
}
