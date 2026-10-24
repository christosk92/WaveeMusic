// ── Screens/Diagnostics.Inspect.cs ─────────────────────────────────────────────────────────────────────────────────────
// `Wavee.exe --inspect`: the agent-facing server that drives the REAL app headless (no window), one JSON line in, one out
//
// Role: SHELL
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2; the reference is docs/guide/agent-inspect.md, the client
//       tools/inspect.py.
//
// THE PROCESS. `App.Main` hands `--inspect` here BEFORE `Platform.Boot`, so the launch never boots the owner's profile: the
// host boots `Shell.Headless.Boot` (BootProfile.HeadlessFake: `--fake` data, memory settings, no log file, no crash writers,
// no single-instance gate, `Wire.GoOffline`) against a SCRATCH profile on a dedicated STA thread it owns (the UI thread of
// every mount, `Platform.UiThreadId`). The main thread reads commands (stdin, or one client on `\\.\pipe\<name>`) and runs
// each on the UI thread, then writes its one reply line. Nothing runs between commands: frames advance only through
// `frames`, `settle` and the verbs that settle after themselves, so every answer is reproducible.
//
// THE SESSION (`InspectSession`) is the UI-thread half and owns no thread, so a shell test drives the very same verbs on its
// own booted host (Wavee.ShellTests/InspectSessionMountedTests.cs). The grammar and the reply writer are pure
// (Diagnostics.Inspect.Protocol.cs); the dump and the A1 seams are Diagnostics.Inspect.Dump.cs; the frame/theme/play helpers
// are Diagnostics.Inspect.Kit.cs. Lane A3 adds `lint` (the pure rules Diagnostics.Inspect.Lint.cs, the scene capture and report
// Diagnostics.Inspect.Lint.Scene.cs) and `crossing` (Diagnostics.Inspect.Crossing.cs); both are partial members of this class.
//
// DEVICES (per mount, `Shell.HeadlessDevices`). Unless `--text headless`, a mount runs over lane A1's `OffscreenSurface`: the
// real D3D12 device (`gpu=hardware` by default: the machine's hardware adapter, WARP when it has none; `gpu=warp` forces WARP) presenting
// into a hidden, never-activated off-monitor tool window, with a DirectWrite
// font system, so text metrics are true and `render` writes a PNG. When the surface cannot be created (no D3D12/WARP) the
// mount falls back to the recording `HeadlessGpuDevice` with DirectWrite: the dump stays truthful, `render` answers ok:false
// with the reason. `--text headless` mounts the shell tests' devices (the 0.55 x size font model). Trimming is the engine's
// `AppHost.QueryTextOverflow`.

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>`--inspect ...` (App.Main, before Platform.Boot): false when the flag is absent. Exit 0 after `quit` or
        /// the end of input, 64 on a usage error, 3 when the host could not boot.</summary>
        public static bool TryRunInspect(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--inspect") < 0) return false;
            AttachParentConsole();
            if (!Inspect.Options.TryParse(args, out Inspect.Options options, out string usage))
            {
                Console.Error.WriteLine(usage);
                code = Headless.ExitCode.Usage;
                return true;
            }
            code = InspectHost.Run(options);
            return true;
        }
    }

    /// <summary>The `--inspect` process: the UI thread, the boot, the line loop.</summary>
    public sealed class InspectHost : IDisposable
    {
        readonly BlockingCollection<Action> _work = new();
        readonly Thread _thread;
        readonly bool _ownsProfile;
        InspectSession? _session;

        /// <summary>The scratch profile the host booted against.</summary>
        public string ProfileDir { get; }

        /// <summary>The text backend mounts use (`dwrite` or `headless`), for the ready line.</summary>
        public string TextBackend { get; private set; } = "headless";

        InspectHost(string profileDir, bool ownsProfile)
        {
            ProfileDir = profileDir;
            _ownsProfile = ownsProfile;
            // The scene recorder recurses per scene depth: the windowed app's 32 MB stack, not the 1 MB default (on 1 MB it ran out at depth
            // 32 and painted only the chrome).
            _thread = new Thread(Loop, FluentGpu.Hosting.OffscreenSurface.UiThreadStackBytes) { Name = "wavee-inspect-ui", IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>The scratch profile <paramref name="requested"/> names, or a new temp folder. Refuses the user's own
        /// profile (%LOCALAPPDATA%\Wavee and anything under it): an inspect run never reads or writes it.</summary>
        public static bool TryProfile(string requested, out string dir, out bool owned, out string why)
        {
            why = "";
            owned = requested.Length == 0;
            dir = owned ? Path.Combine(Path.GetTempPath(), "wavee-inspect", Guid.NewGuid().ToString("n")) : Path.GetFullPath(requested);
            string user = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee"));
            string a = dir.TrimEnd('\\', '/'), b = user.TrimEnd('\\', '/');
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                why = "--profile must be a scratch folder, not the user's profile (" + user + ")";
                return false;
            }
            return true;
        }

        /// <summary>Boot the shell on a new UI thread. Once per process (<c>Shell.Headless.Boot</c>'s rule).</summary>
        public static InspectHost Boot(string profileDir, bool ownsProfile, bool realText)
        {
            var host = new InspectHost(profileDir, ownsProfile);
            host.Invoke(() =>
            {
                Shell.Headless.Boot(new Shell.ShellHostOptions(profileDir));
                InspectKit.InstallFixtureGrader();
                InspectKit.InstallVideoJoinHold();
                InspectKit.RememberBootQueue();   // what a mount after the first puts back (ClearLeaks)
                InspectKit.RememberBootVideo();
                InspectKit.RememberColdPlaylist();
                host._session = new InspectSession(realText);
                host.TextBackend = host._session.TextBackend;
            });
            return host;
        }

        void Loop()
        {
            foreach (var work in _work.GetConsumingEnumerable()) work();
        }

        /// <summary>Run <paramref name="body"/> on the UI thread and wait; its exception is rethrown here.</summary>
        public void Invoke(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            using var done = new ManualResetEventSlim();
            _work.Add(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                finally { done.Set(); }
            });
            done.Wait();
            failure?.Throw();
        }

        /// <summary>One line in, one reply out (empty for a blank or comment line). <paramref name="quit"/> is set by `quit`.</summary>
        public string Execute(string line, out bool quit)
        {
            string reply = "";
            bool q = false;
            Invoke(() => reply = _session!.Execute(line, out q));
            quit = q;
            return reply;
        }

        public void Dispose()
        {
            try { Invoke(() => _session?.Dispose()); } catch (Exception ex) { Log.Warn("inspect", "the session did not close cleanly", ex); }
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
            if (_ownsProfile) { try { Directory.Delete(ProfileDir, recursive: true); } catch (Exception) { } }
        }

        /// <summary>The server: boot, the ready line, then one reply per command until `quit` or the end of input.</summary>
        public static int Run(in Inspect.Options o)
        {
            if (!TryProfile(o.Profile, out string profile, out bool owned, out string why))
            {
                Console.Error.WriteLine(why);
                return Headless.ExitCode.Usage;
            }
            InspectHost host;
            try { host = Boot(profile, owned, realText: !o.HeadlessText); }
            catch (Exception ex)
            {
                Console.Out.WriteLine(Inspect.Reply.Event("fault", w => w.WriteString("reason", "boot failed: " + ex.GetType().Name + ": " + ex.Message)));
                return Headless.ExitCode.HostError;
            }
            int code = Headless.ExitCode.Ok;
            try
            {
                TextReader reader;
                TextWriter writer;
                NamedPipeServerStream? server = null;
                if (o.Pipe.Length == 0)
                {
                    reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    writer = Console.Out;
                }
                else
                {
                    const string prefix = @"\\.\pipe\";
                    string name = o.Pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? o.Pipe[prefix.Length..] : o.Pipe;
                    // The pipe is announced on stdout before the wait, so a client knows when to connect.
                    Console.Out.WriteLine(Inspect.Reply.Event("listening", w => w.WriteString("pipe", prefix + name)));
                    // CurrentUserOnly: only a process of the user who started the server may connect (the pipe drives the app and reads its scene).
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                }
                writer.WriteLine(Inspect.Reply.Event("ready", w =>
                {
                    w.WriteString("profile", host.ProfileDir);
                    w.WriteString("text", host.TextBackend);
                    w.WriteString("trim", host.TextBackend == "headless" ? "headless-model" : "dwrite");
                    w.WriteStartArray("verbs");
                    foreach (string v in Inspect.VerbNames) w.WriteStringValue(v);
                    w.WriteEndArray();
                }));
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    string reply;
                    bool quit;
                    try { reply = host.Execute(line, out quit); }
                    catch (Exception ex)
                    {
                        // The session catches per command; this is the UI thread itself failing. Report and keep serving.
                        reply = Inspect.Reply.Fail(-1, "", "host error: " + ex.GetType().Name + ": " + ex.Message);
                        quit = false;
                    }
                    if (reply.Length > 0) writer.WriteLine(reply);
                    if (quit) break;
                }
                writer.WriteLine(Inspect.Reply.Event("bye"));
                writer.Flush();
                server?.Dispose();
            }
            catch (IOException ex)
            {
                Log.Warn("inspect", "the command stream closed", ex);
                code = Headless.ExitCode.HostError;
            }
            finally
            {
                host.Dispose();
                Platform.Shutdown();
            }
            return code;
        }
    }

    /// <summary>The `--inspect` verbs over the process's booted headless shell. UI THREAD ONLY, no thread of its own: the
    /// server's host and a shell test construct it on the thread that ran <c>Shell.Headless.Boot</c>. Dispose unmounts
    /// (releasing the mount's devices).</summary>
    public sealed partial class InspectSession : IDisposable
    {
        readonly bool _realText;
        Shell.HeadlessShell? _shell;
        FluentGpu.Hosting.OffscreenSurface? _surface;
        string _devices = "";
        uint _ms = 1000;
        long _frames;
        int _mounts;
        bool _acts;                 // the running verb acts (the verb table's Acts): its reply carries `after`
        bool _quit;                 // `quit` ran (set by the verb itself, so a refused `quit x=1` does not end the server)
        string? _hit;               // the key the pointer resolved to on a pointer verb, for `after.hit`
        Point2? _pointer;           // where the pointer last went on this mount (pointer verbs), for `after.hit` of the others
        FluentGpu.Media.MediaPlayer? _fakePlayer;   // the idle player `mount video=... player=fake` bound (ClearLeaks unbinds and disposes it)
        bool _optionsForNext;       // `option` set a value with nothing mounted: it is for the next mount, which keeps the options

        /// <summary>`dwrite` (real DirectWrite metrics on every mount) or `headless` (the shell tests' 0.55 x size model).</summary>
        public string TextBackend => _realText ? "dwrite" : "headless";

        /// <summary>The mounted shell, or null.</summary>
        public Shell.HeadlessShell? Mounted => _shell;

        /// <summary>What the current mount runs on: `offscreen (D3D12 (WARP))`, `headless-gpu+dwrite (why the surface failed)`,
        /// or `headless`.</summary>
        public string Devices => _devices;

        public InspectSession(bool realText = true)
        {
            Wavee.Shell.Headless.RequireUiThread();
            _realText = realText;
        }

        public void Dispose()
        {
            Unmount();
            InspectKit.EndVideoJoins();   // no resolve stays parked past the session (the api threads it holds would outlive it)
        }

        void Unmount()
        {
            _pointer = null;   // the next mount's pointer has gone nowhere yet
            _hit = null;
            if (_shell is null) return;
            var shell = _shell;
            _shell = null;
            _surface = null;   // released by the shell's dispose (HeadlessDevices.Release), after its host
            shell.Dispose();
        }

        /// <summary>Mount the frame on the best devices this machine has (see the file header): the offscreen surface (real
        /// pixels and text), else the recording device with DirectWrite (true text, no pixels), else (`--text headless`) the
        /// shell tests' devices.</summary>
        Shell.HeadlessShell MountOn(float widthDip, float heightDip, float scale, string gpu, bool realImages = false)
        {
            // Without `images=real` the engine's fake decoder paints every image: the palette's luminance maps measure what it paints.
            Palette.FakePixels = !realImages;
            if (!_realText)
            {
                _devices = "headless";
                return Wavee.Shell.Headless.Mount(widthDip, heightDip, scale);
            }
            StringTable strings = Entities.Strings;
            string why = "gpu=none";
            if (gpu != "none")
            {
                var options = new FluentGpu.Hosting.OffscreenSurfaceOptions
                {
                    SizeDip = new Size2(widthDip, heightDip),
                    Scale = scale,
                    UseHardwareAdapter = gpu == "hardware",
                };
                if (FluentGpu.Hosting.OffscreenSurface.TryCreate(strings, options, out var created, out string? reason))
                {
                    FluentGpu.Hosting.OffscreenSurface surface = created;   // non-null from here on (the lambda below captures it)
                    var fonts = surface.CreateFontSystem();
                    // `images=real`: the app's own pipeline (WIC behind the disk-first fetcher) instead of the engine's fake decoder, so a photograph
                    // renders as itself: what `lint`'s contrast rule needs to judge text over a REAL bright or dark photograph.
                    FluentGpu.Media.DefaultImageFetcher? fetcher = realImages ? new FluentGpu.Media.DefaultImageFetcher() : null;
                    FluentGpu.Media.DecodeScheduler? decoder = fetcher is null ? null
                        : new FluentGpu.Media.DecodeScheduler(new FluentGpu.Media.Codecs.Wic.WicImageCodec(), fetcher, new FluentGpu.Media.DecodeOptions());
                    var devices = new Wavee.Shell.HeadlessDevices(surface.Window, surface.Input, surface.Device, fonts,
                        Attach: surface.Attach,
                        Release: () => { fonts.Dispose(); surface.Dispose(); decoder?.Dispose(); fetcher?.Dispose(); })
                    {
                        Images = decoder is null ? null : new FluentGpu.Scene.ImageCache(decoder),
                    };
                    _surface = surface;
                    _devices = "offscreen (" + surface.BackendName + ")";
                    return Wavee.Shell.Headless.Mount(devices);
                }
                why = reason ?? "the offscreen surface could not be created";
            }
            var dwrite = new FluentGpu.Text.DirectWrite.DirectWriteFontSystem(strings);
            var window = new FluentGpu.Pal.Headless.HeadlessWindow(new WindowDesc("wavee-inspect", new Size2(widthDip * scale, heightDip * scale), scale));
            var fallback = new Wavee.Shell.HeadlessDevices(window, window, new FluentGpu.Rhi.Headless.HeadlessGpuDevice(), dwrite,
                Release: dwrite.Dispose);
            _devices = "headless-gpu+dwrite (" + why + ")";
            return Wavee.Shell.Headless.Mount(fallback);
        }

        // ── the dispatcher ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Parse and run one line; the reply line (empty for a blank or comment line).</summary>
        public string Execute(string line, out bool quit)
        {
            quit = false;
            if (!Inspect.TryParse(line, out Inspect.Command cmd, out string error))
                return Inspect.Reply.Fail(cmd.Id, cmd.Name.Length > 0 ? cmd.Name : line.Trim(), error);
            if (cmd.IsNone) return "";
            string reply = Execute(in cmd);
            quit = _quit;
            _quit = false;
            return reply;
        }

        /// <summary>Run one parsed command; never throws (a failure is an ok:false reply naming the exception). An arg the verb
        /// does not declare (Diagnostics.Inspect.Protocol.cs, the verb table) is refused before anything runs.</summary>
        public string Execute(in Inspect.Command cmd)
        {
            if (!Inspect.Validate(in cmd, out string invalid)) return Inspect.Reply.Fail(cmd.Id, cmd.Name, invalid);
            _acts = Inspect.SpecOf(cmd.Verb)?.Acts ?? false;
            _hit = null;
            try { return Dispatch(in cmd); }
            catch (Exception ex)
            {
                Log.Warn("inspect", "command '" + cmd.Name + "' failed", ex);
                return Inspect.Reply.Fail(cmd.Id, cmd.Name, ex.GetType().Name + ": " + ex.Message);
            }
            finally { _acts = false; }
        }

        string Dispatch(in Inspect.Command cmd)
        {
            Inspect.Args a = cmd.Args;
            int id = cmd.Id;
            string name = cmd.Name;
            if (cmd.Verb == Inspect.Verb.Lint && a.Has("replay")) return Lint(id, name, a, _shell);   // a saved snapshot needs no mount
            if (Inspect.SpecOf(cmd.Verb) is { NeedsMount: true } && _shell is null) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first)");
            switch (cmd.Verb)
            {
                case Inspect.Verb.Help:
                    return Help(id, name, a);
                case Inspect.Verb.Quit:
                    Unmount();
                    _quit = true;
                    return Inspect.Reply.Ok(id, name);
                case Inspect.Verb.Status:
                    return Inspect.Reply.Ok(id, name, WriteStatus);
                case Inspect.Verb.Mount:
                    return Mount(id, name, a);
                case Inspect.Verb.Unmount:
                    Unmount();
                    return Inspect.Reply.Ok(id, name);
                case Inspect.Verb.Reset:
                    Unmount();
                    ClearLeaks(keepRoute: false, keepOptions: false);
                    Wavee.Shell.Headless.ResetNavigation();
                    InspectKit.ActivateTheme(InspectKit.DefaultThemeId, ThemeKind.Dark);
                    return Inspect.Reply.Ok(id, name, w => w.WriteString("theme", InspectKit.ActiveThemeId));
                case Inspect.Verb.Theme:
                    return Theme(id, name, a);
                case Inspect.Verb.Option:
                    return Option(id, name, a);
                case Inspect.Verb.Crossing:
                    return Crossing(id, name, a);   // mounts by itself when nothing is
                case Inspect.Verb.Views:
                    return Views(id, name, a);
                case Inspect.Verb.Setting:
                    return Setting(id, name, a);   // the store needs no mount; a mounted one settles after the write
                case Inspect.Verb.Tiers:
                    return Tiers(id, name, a);   // the policy needs no mount; the facts do
                case Inspect.Verb.Crossings:
                    return Crossings(id, name, a);   // mounts a row at a time
            }

            // Everything below needs a mount (the verb table says so: NeedsMount).
            Shell.HeadlessShell shell = _shell!;
            switch (cmd.Verb)
            {
                case Inspect.Verb.Skeleton:
                    return Skeleton(id, name, a, shell);
                case Inspect.Verb.GoTo:
                    {
                        if (!Navigate(a, out string error)) return Inspect.Reply.Fail(id, name, error);
                        return Settled(id, name, a);
                    }
                case Inspect.Verb.Resize:
                    {
                        float w0 = a.Float("w", 0f), h0 = a.Float("h", 0f);
                        if (!(w0 >= 1f && h0 >= 1f)) return Inspect.Reply.Fail(id, name, "resize wants w and h (DIP)");
                        if (a.Bool("loop", false)) ResizeThroughModalLoop(shell, w0, h0);
                        else shell.Resize(w0, h0);
                        if (_pointer is { } p && !InspectKit.WindowRect(shell).Contains(p)) _pointer = null;   // the window shrank past it
                        return Settled(id, name, a);
                    }
                case Inspect.Verb.Frames:
                    {
                        int n = Math.Clamp(a.Int("n", 1), 0, 100_000);
                        _hotAlloc = 0; _paintedFrames = 0;
                        RunFrames(Math.Max(0, n - 1));
                        int epoch = Tok.Epoch;
                        RunFrames(Math.Min(n, 1));
                        bool epochMoved = n > 0 && Tok.Epoch != epoch;   // the last frame wrote a theme token (a FrameNeeded term)
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteNumber("frames", n);
                            w.WriteNumber("painted", _paintedFrames);
                            w.WriteNumber("hotAllocBytes", _hotAlloc);
                            w.WriteBoolean("animating", shell.Host.Animation.HasActive);
                            w.WriteBoolean("scrolling", shell.Host.AnyUserScrollMoving);
                            FluentGpu.Hosting.WakeReasons wake = shell.Host.CurrentWakeReasons;
                            InspectKit.WriteWake(w, wake);   // every reason the loop would stay awake (settle ignores the ambient ones)
                            if ((wake & FluentGpu.Hosting.WakeReasons.FrameNeeded) != 0) InspectKit.WriteFrameNeeded(w, "frameNeeded", shell, epochMoved);
                        });
                    }
                case Inspect.Verb.Settle:
                    {
                        InspectKit.SettleResult r = Settle(a.Int("max", 600), InspectKit.LoopsArg(a));
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteNumber("frames", r.Frames);
                            w.WriteBoolean("settled", r.Settled);
                            if (r.Frames == 0) w.WriteString("note", "no frame ran (max=0): nothing was waited for");
                            else if (!r.Settled) InspectKit.WriteBusy(w, shell, in r);
                            InspectKit.WriteAmbient(w, in r);
                        });
                    }
                case Inspect.Verb.Click:
                case Inspect.Verb.Hover:
                    {
                        if (!TryMods(a, out KeyModifiers mods)) return Inspect.Reply.Fail(id, name, "bad value '" + a.Str("mods") + "' for mods (ctrl, shift, alt or win, joined by +: ctrl+shift)");
                        if (!TryAim(id, name, a, wheel: false, out Aim aim, out string refusal)) return refusal;
                        Point2 p = aim.Point;
                        Move(p);
                        RunFrames(1);
                        _hit = HitKey(p);   // what the press lands on (a hover can raise a part over the target)
                        if (cmd.Verb == Inspect.Verb.Click)
                        {
                            int button = a.Str("button", "left") == "right" ? 1 : 0;
                            Queue(new InputEvent(InputKind.PointerDown, p, button, 0, mods, TimestampMs: _ms));
                            RunFrames(1);
                            Queue(new InputEvent(InputKind.PointerUp, p, button, 0, mods, TimestampMs: _ms + 50));
                            if (!Tracing(a)) RunFrames(1);   // `trace=N` starts at the frame the release lands on
                        }
                        return Settled(id, name, a, w => WriteAim(w, in aim));
                    }
                case Inspect.Verb.Key:
                    {
                        string spec = a.Str("key");
                        if (!Inspect.TryKey(spec, out int vk, out KeyModifiers mods)) return Inspect.Reply.Fail(id, name, "unknown key '" + spec + "'");
                        int repeat = Math.Clamp(a.Int("repeat", 1), 1, 100);
                        for (int i = 0; i < repeat; i++)
                        {
                            Queue(new InputEvent(InputKind.Key, default, 0, vk, mods, TimestampMs: _ms));
                            Queue(new InputEvent(InputKind.KeyUp, default, 0, vk, mods, TimestampMs: _ms));
                            RunFrames(1);
                        }
                        return Settled(id, name, a);
                    }
                case Inspect.Verb.Type:
                    {
                        string text = a.Str("text");
                        foreach (char c in text) Queue(new InputEvent(InputKind.Char, default, 0, c, TimestampMs: _ms));
                        RunFrames(1);
                        return Settled(id, name, a, w => w.WriteNumber("chars", text.Length));
                    }
                case Inspect.Verb.Scroll:
                    {
                        if (!TryAim(id, name, a, wheel: true, out Aim aim, out string refusal)) return refusal;
                        Point2 p = aim.Point;
                        float dy = a.Float("dy", 3f), dx = a.Float("dx", 0f);
                        Move(p);
                        RunFrames(1);
                        _hit = WheelKey(p, horizontal: dy == 0f && dx != 0f);   // what the wheel goes to: a scroller, not a click target
                        var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, p, dx, dy, 0, KeyModifiers.None);
                        Queue(InputEvent.ForScroll(in wheel, timestampMs: _ms));
                        if (!Tracing(a)) RunFrames(1);   // `trace=N` starts at the frame the notch lands on
                        return Settled(id, name, a, traceScroll: aim.Key ?? "main", body: w =>
                        {
                            WriteAim(w, in aim);
                            if (aim.Node.IsNull || !shell.Host.Scene.IsLive(aim.Node)) return;
                            NodeHandle viewport = ScrollUnder(shell.Host.Scene, aim.Node);
                            if (viewport.IsNull || !shell.Host.Scene.TryGetScroll(viewport, out ScrollState s)) return;
                            w.WriteStartObject("scroll");
                            Inspect.Reply.Num(w, "offset", (float)s.Offset);
                            Inspect.Reply.Num(w, "max", s.MaxOffset);
                            w.WriteEndObject();
                        });
                    }
                case Inspect.Verb.Play:
                    {
                        string uri = a.Str("uri");
                        // `remote=<name>|off`: the output turns remote (or local again) on the live mount, as a Connect transfer does while playing;
                        // without a uri only the output changes.
                        if (a.Has("remote"))
                        {
                            string remote = a.Str("remote");
                            Wavee.Theming.InspectSwitches.RemoteDevice = remote.Length == 0 || string.Equals(remote, "off", StringComparison.OrdinalIgnoreCase) ? null : remote;
                            if (uri.Length == 0)
                            {
                                RunFrames(2);
                                return Settled(id, name, a, w => w.WriteString("remote", Wavee.Theming.InspectSwitches.RemoteDevice ?? "off"));
                            }
                        }
                        // `context=<uri>`: the rows play IN that context (a playlist, an album), as a row click on its page does, so what names the
                        // context (the stage's "playing from …" line) has one
                        EntityId contextId = default;
                        if (a.Has("context") && !EntityId.TryParse(a.Str("context").AsSpan(), out contextId)) return Inspect.Reply.Fail(id, name, "context wants a spotify: uri, not '" + a.Str("context") + "'");
                        if (!InspectKit.Play(uri, Math.Max(0, a.Int("fromMs", 0)), a.Int("next", 0), agoMin: a.Int("ago", 0), context: contextId)) return Inspect.Reply.Fail(id, name, "play wants a spotify:track|episode uri, not '" + uri + "'");
                        RunFrames(2);
                        // The load opens on the audio chain on the wall clock, which a frame-counted settle can outrun (the reply then read the bar and the
                        // rows before the track played: no playing row, no lyrics button). Wait for it to land first; `phase` says where it stands.
                        long before = _frames;
                        string phase = AwaitPlaybackLoad(PlayLoadBudgetMs);
                        long loadFrames = _frames - before;
                        return Settled(id, name, a, w => { w.WriteString("uri", uri); w.WriteString("phase", phase); w.WriteNumber("loadFrames", loadFrames); });
                    }
                case Inspect.Verb.Dialog:
                    {
                        // `dialog {kind}`: opens over the CURRENT page, never navigating: crash | hang (the crash prompt over a made-up bundle) and lightbox (the artist
                        // gallery's lightbox over bundled photographs) through the shell's own overlay host (the one App.BindOverlayServices hands Track.DrawerOverlay
                        // on every mount), report (Report a problem) through its own chrome. A developer's probe; `esc` closes it. The reply's `over` names the page
                        // underneath.
                        string kind = a.Str("kind").ToLowerInvariant();
                        if (kind is not ("crash" or "hang" or "report" or "lightbox")) return Inspect.Reply.Fail(id, name, "dialog wants kind=crash|hang|report|lightbox, not '" + kind + "'");
                        if (kind == "report") Feedback.Open(Feedback.ReportKind.Bug);
                        else
                        {
                            var overlay = Track.DrawerOverlay;
                            if (Controls.IsNullOverlay(overlay)) return Inspect.Reply.Fail(id, name, "the shell's overlay host is not bound yet (run frames 30 and try again)");
                            // lightbox: the artist gallery's lightbox over the bundled photographs, the bright one first (text and chrome over a photograph)
                            if (kind == "lightbox") Artist.GalleryOpen(overlay, LightboxPhotos(), 0);
                            else Crash.OpenFakePrompt(overlay, kind == "hang" ? Crash.Kind.Hang : Crash.Kind.Managed);
                        }
                        RunFrames(40);
                        string over = RouteText();
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("kind", kind);
                            w.WriteString("over", over);
                        });
                    }
                case Inspect.Verb.State:
                    {
                        // `state {kind}`: the page region shows a state of its own in place of the page (empty, nomatch, error, offline; `none` puts the page back; `loading` opens a
                        // search for a query the fake catalogue never answers, so its skeleton stays). The states are the shared vacancy, so a theme's picture of it can be rendered.
                        string kind = a.Str("kind").ToLowerInvariant();
                        BeforeState(shell);
                        if (a.Has("page")) return PageState(id, name, a, kind, shell);
                        // A detail page's header unknown in place, then its landing (Entities.Fake.Cold.cs, audit B7 r2).
                        if (kind == "head")
                        {
                            string uri = a.Str("uri");
                            if (!Entities.StageHead(uri)) return Inspect.Reply.Fail(id, name, "state kind=head wants uri=<an album, playlist, show or episode the fake holds>, not '" + uri + "'");
                            InspectKit.ColdStaged = true;   // the next mount puts the header back
                            Wavee.Shell.GoTo(Wavee.Shell.For(EntityUri.Parse(uri)));
                            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
                            return Settled(id, name, a, w => { w.WriteString("kind", kind); w.WriteString("uri", uri); AfterState(w, shell, kind); },
                                loops: InspectKit.LoopMode.Skeletons);
                        }
                        if (kind == "head-land")
                        {
                            Entities.RestoreHead();
                            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
                            return Settled(id, name, a, w => { w.WriteString("kind", kind); AfterState(w, shell, kind); });
                        }
                        if (!Shell.SetInspectState(kind)) return Inspect.Reply.Fail(id, name, "state wants kind=" + string.Join("|", Inspect.StateKinds) + ", not '" + kind + "'");
                        if (kind.StartsWith("cold-", StringComparison.Ordinal)) InspectKit.ColdStaged = true;   // the next mount puts the cold playlist back
                        // A traced (or unsettled) state shows its own frames from the first: 30 frames run first hid a staged landing from its trace (audit B7 r2).
                        if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
                        // A loading or cold state's skeleton IS the picture: its settle stops on the shimmer unless `loops` says otherwise.
                        bool skeleton = kind == "loading" || kind.StartsWith("cold-", StringComparison.Ordinal);
                        return Settled(id, name, a, w => { w.WriteString("kind", kind); AfterState(w, shell, kind); },
                            loops: skeleton ? InspectKit.LoopMode.Skeletons : InspectKit.LoopMode.Ignore);
                    }
                case Inspect.Verb.Find:
                    {
                        // `find <key>` is exact; a prefix says so (`find prefix=zune-band-`).
                        string key = a.Str("key"), prefix = a.Str("prefix");
                        if (key.Length == 0 && !a.Has("prefix")) return Inspect.Reply.Fail(id, name, "find wants a key (exact) or prefix=");
                        bool visibleOnly = a.Bool("visibleOnly", false);
                        int depth = a.Int("depth", int.MaxValue);
                        return Inspect.Reply.Ok(id, name, w => InspectDump.WriteFind(w, shell, key, prefix, visibleOnly, depth));
                    }
                case Inspect.Verb.Dump:
                    return Dump(id, name, a, shell);
                case Inspect.Verb.Render:
                    return Render(id, name, a, shell);
                case Inspect.Verb.Lint:
                    return Lint(id, name, a, shell);
                case Inspect.Verb.Trace:
                    return Trace(id, name, a, shell);
                case Inspect.Verb.Drag:
                    return Drag(id, name, a);
            }
            return Inspect.Reply.Fail(id, name, "verb not handled");
        }

        /// <summary>`state kind=loading|empty page=<p>`: a page's OWN loading or empty state, not the shared vacancy. A library tab (`library` is
        /// albums) or Liked Songs (`liked`) stages the account's relations behind it (Entities.Fake.Shelves.cs: Unknown, or Complete with no rows) and opens the tab; `show`
        /// and `episode` open a fixture in that state: the pending show and episode (`shzzqx`, `epzzqx`: the fake never answers them) and the show
        /// with no episodes (`sh8`). The next mount puts the shelves back.</summary>
        string PageState(int id, string name, Inspect.Args a, string kind, Shell.HeadlessShell shell)
        {
            if (kind is not ("loading" or "empty")) return Inspect.Reply.Fail(id, name, "page= takes kind=loading or kind=empty, not '" + kind + "'");
            bool loading = kind == "loading";
            string page = a.Str("page").ToLowerInvariant();
            if (page == "library") page = "albums";
            Shell.SetInspectState("none");   // the shared vacancy off: the page draws its own state
            Shell.Route route;
            switch (page)
            {
                case "albums" or "artists" or "podcasts" or "audiobooks" or "liked":
                    if (!Entities.StageLibraryShelf(page, loading)) return Inspect.Reply.Fail(id, name, "the account has no library rows to stage");
                    route = Wavee.Shell.Parse(page);
                    break;
                case "show":
                    route = Wavee.Shell.For(EntityUri.Parse(loading ? "spotify:show:shzzqx" : "spotify:show:sh8"));
                    break;
                case "episode":
                    if (!loading) return Inspect.Reply.Fail(id, name, "an episode page has no empty state (kind=loading, or the shared kind=error)");
                    route = Wavee.Shell.For(EntityUri.Parse("spotify:episode:epzzqx"));
                    break;
                default:
                    return Inspect.Reply.Fail(id, name, "page is library|albums|artists|podcasts|audiobooks|liked|show|episode, not '" + page + "'");
            }
            Wavee.Shell.GoTo(route);
            RunFrames(30);
            return Settled(id, name, a, w => { w.WriteString("kind", kind); w.WriteString("page", page); AfterState(w, shell, kind); },
                loops: loading ? InspectKit.LoopMode.Skeletons : InspectKit.LoopMode.Ignore);
        }

        /// <summary>`help` (the verbs and their summaries), `help verb=<name>` (one verb's args, defaults, choices and example) or
        /// `help schema=json` (the whole verb table): all from the table in Diagnostics.Inspect.Protocol.cs.</summary>
        static string Help(int id, string name, Inspect.Args a)
        {
            if (a.Has("schema"))
            {
                string schema = a.Str("schema");
                if (schema != "json") return Inspect.Reply.Fail(id, name, "schema is json, not '" + schema + "'");
                return Inspect.Reply.Ok(id, name, Inspect.WriteSchema);
            }
            if (!a.Has("verb")) return Inspect.Reply.Ok(id, name, Inspect.WriteHelpIndex);
            string verb = a.Str("verb");
            if (Inspect.SpecNamed(verb) is not { } spec)
            {
                string? near = null;
                int best = 3;
                foreach (string v in Inspect.VerbNames)
                {
                    int d = Inspect.EditDistance(verb.ToLowerInvariant(), v);
                    if (d < best) { best = d; near = v; }
                }
                return Inspect.Reply.Fail(id, name, "unknown verb '" + verb + "'" + (near is null ? " (help lists them)" : " (did you mean '" + near + "'?)"));
            }
            return Inspect.Reply.Ok(id, name, w => Inspect.WriteVerb(w, spec));
        }

        /// <summary>`render {path, scale?, alpha?}`: the offscreen surface's frame as a PNG (flattened over the window background
        /// unless `alpha=true`). A `scale` other than the mount's re-renders at it and puts the mount's back after.</summary>
        string Render(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            string path = a.Str("path");
            if (path.Length == 0) return Inspect.Reply.Fail(id, name, "render wants a path (.png)");
            if (_surface is not { } surface)
            {
                string why = "offscreen capture unavailable on this mount: " + _devices;
                return Inspect.Reply.Fail(id, name, why, w => w.WriteString("why", why));
            }
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            float zoom = Math.Max(0.01f, shell.Window.Zoom);
            float mountScale = shell.Window.Scale / zoom;
            float scale = a.Float("scale", mountScale);
            if (!(scale > 0f && scale <= 4f)) return Inspect.Reply.Fail(id, name, "0 < scale <= 4");
            var sizeDip = new Size2(shell.Window.ClientSizePx.Width / mountScale, shell.Window.ClientSizePx.Height / mountScale);
            bool rescaled = MathF.Abs(scale - mountScale) > 0.001f;
            if (rescaled) { surface.Resize(sizeDip, scale); Settle(a.Int("max", 600)); }
            Size2 px = surface.SizePx;
            try
            {
                RunFrames(1);   // a frame presented at rest: the capture reads the last presented frame
                if (a.Bool("alpha", false)) surface.WritePng(full);
                else surface.WritePng(full, Tok.T.WindowBackground);
            }
            finally
            {
                if (rescaled) { surface.Resize(sizeDip, mountScale); Settle(a.Int("max", 600)); }
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("path", full);
                w.WriteNumber("widthPx", (int)px.Width);
                w.WriteNumber("heightPx", (int)px.Height);
                Inspect.Reply.Num(w, "scale", scale);
                w.WriteString("devices", _devices);
            });
        }

        // ── mount, navigation, theme, options ───────────────────────────────────────────────────────────────────────

        string Mount(int id, string name, Inspect.Args a)
        {
            float w0 = a.Float("w", 1180f), h0 = a.Float("h", 760f);
            if (!MountShell(a, w0, h0, out string error, out bool mounted)) return Inspect.Reply.Fail(id, name, error, mounted ? WriteMountFacts : null);
            return Settled(id, name, a, WriteMountFacts);
        }

        /// <summary>The body of `mount` (and of a `crossing` that has nothing mounted): validate the args, replace the mount, navigate. False with
        /// <paramref name="error"/> on a bad arg or an unknown route; <paramref name="mounted"/> says whether a mount is up after the failure.</summary>
        bool MountShell(Inspect.Args a, float w0, float h0, out string error, out bool mounted)
        {
            error = "";
            mounted = false;
            float scale = a.Float("scale", 1f);
            if (!(w0 >= 1f && h0 >= 1f && scale > 0f && scale <= 4f)) { error = "mount wants w, h (DIP) and 0 < scale <= 4"; return false; }
            ThemeKind? kind = null;
            if (a.Has("kind"))
            {
                string k = a.Str("kind").ToLowerInvariant();
                if (k is not ("dark" or "light")) { error = "kind is dark or light"; return false; }
                kind = k == "light" ? ThemeKind.Light : ThemeKind.Dark;
            }
            string theme = a.Str("theme", InspectKit.ActiveThemeId).ToLowerInvariant();
            if (!InspectKit.IsShipped(theme)) { error = "unknown theme '" + theme + "' (" + string.Join("|", InspectKit.ThemeIds) + ")"; return false; }

            Unmount();
            ClearLeaks(a.Bool("keepRoute", false));
            if (!a.Bool("keepRoute", false)) Wavee.Shell.Headless.ResetNavigation();
            // `setup=true` mounts a first run: the pending marker is armed in the (in-memory, `--fake`) store and the wizard's boot decision is taken again, so its plate opens on the mount.
            if (a.Bool("setup", false) || a.Str("setup") == "signin")
            {
                Platform.Settings.Set(Platform.Keys.SetupPending, true);
                Setup.Wizard.ResetForTests();
                Setup.Wizard.Boot(Platform.Settings, false);
                // `setup=signin` starts on the sign-in page (step 2) instead of the terms.
                if (a.Str("setup") == "signin" && Setup.Wizard.Due is { } due) due.Page.Value = Setup.WizardPage.SignIn;
            }
            InspectKit.ActivateTheme(theme, kind ?? Tok.Theme);
            string gpu = a.Str("gpu", "hardware").ToLowerInvariant();   // hardware falls back to WARP on a machine without an adapter (D3D12Device.InitDevice)
            if (gpu is not ("warp" or "hardware" or "none")) { error = "gpu is warp, hardware or none"; return false; }
            if (!StagePrefs(a, out error)) return false;
            if (!BarPrefs(a, out error)) return false;
            // `grade=hue`: every cover grades to a hue taken from its uri (the fake catalogue's covers are local files the provider cannot grade), so a surface that tints
            // from the playing cover renders in colour offline
            // `grade=grey`: every cover grades to a hueless grey (a black-and-white cover), so the surfaces that tint from it show their no-hue arm
            Palette.SchemeSeam = a.Str("grade").ToLowerInvariant() switch
            {
                "hue" => static (uri, light) => { var g = InspectKit.GradeOf(uri); return light ? g.Light : g.Dark; },
                "grey" or "gray" => static (_, light) => light ? InspectKit.GreyScheme.Light : InspectKit.GreyScheme.Dark,
                // `grade=greynow`: the playing track's cover is grey, every other cover takes its hue (a tinted page beside a black-and-white cover)
                "greynow" => static (uri, light) =>
                {
                    var g = string.Equals(uri, Wavee.Rail.NowArtUrl(), StringComparison.Ordinal) ? InspectKit.GreyScheme : InspectKit.GradeOf(uri);
                    return light ? g.Light : g.Dark;
                },
                _ => null,
            };
            string images = a.Str("images", "fake").ToLowerInvariant();
            if (images is not ("fake" or "real")) { error = "images is fake or real"; return false; }
            _shell = MountOn(w0, h0, scale, gpu, images == "real");
            _frames++;
            _mounts++;
            mounted = true;
            if (a.Has("route") || a.Has("uri") || a.Has("deeplink"))
            {
                if (!Navigate(a, out error)) return false;
            }
            // `video=fullscreen|docked|floating` puts the video SURFACE up with a fake track playing: the placement is committed as if the track had a video (no frames are decoded offline: the
            // chrome, the controls and the placement menu are what draw, over the empty picture).
            Wavee.Theming.InspectSwitches.HoverChromeAtRest = string.Equals(a.Str("chrome"), "shown", StringComparison.OrdinalIgnoreCase);
            if (a.Has("video"))
            {
                // The rich album's first row (tr26) has a music video (`TrackFlags.HasVideo`), so the play bar offers its video split and placement menu.
                InspectKit.Play(InspectKit.VideoTrackUri, a.Int("fromMs", 74_000));
                RunFrames(30);
                Wavee.Video.State.FoldAvailability(true);
                Wavee.Video.State.OpenAt(a.Str("video").ToLowerInvariant() switch
                {
                    "docked" => Wavee.Video.SurfacePlacement.Docked,
                    "floating" or "pip" => Wavee.Video.SurfacePlacement.Floating,
                    _ => Wavee.Video.SurfacePlacement.Fullscreen,
                });
                // `player=fake` binds an idle engine player (no source, no frames) so the presenter and its transport mount as they do while a video plays.
                string join = a.Str("join").ToLowerInvariant();
                if (string.Equals(a.Str("player"), "fake", StringComparison.OrdinalIgnoreCase) || join == "poster")
                {
                    _fakePlayer = FluentGpu.Media.MediaPlayer.Create();
                    Playback.Video.Player.Value = new Playback.Video.Binding(_fakePlayer, 1);
                }
                // `join=`: the host's switch phase as its events would leave it (`Video.JoinNow` folds it): poster = a player bound and no join in
                // flight (the artwork alone), working = a join still in flight past the budget (the ring), failed = the join failed (the fault line).
                if (join.Length > 0)
                    Playback.Video.Phase.Value = join switch
                    {
                        "failed" => Playback.Video.SwitchPhase.Failed,
                        "working" => Playback.Video.SwitchPhase.Resolving,
                        _ => Playback.Video.SwitchPhase.Idle,
                    };
            }
            // `feedback=bug|feature` opens the report form over the page (the surface the settings' About links open).
            if (a.Has("feedback"))
                Feedback.Open(string.Equals(a.Str("feedback"), "feature", StringComparison.OrdinalIgnoreCase) ? Feedback.ReportKind.Feature : Feedback.ReportKind.Bug);
            return true;
        }

        /// <summary>What one mount must not inherit from the last (`mount` and `reset`, once a mount has run: before that the boot's own
        /// state is the fresh one): the `state` vacancy (a static probe signal, Shell.UI.cs), the cold playlist a `state kind=cold-*`
        /// staged, the fullscreen stage (a static signal too: left up, its looping art kept the next mount from ever settling), the video
        /// surface a `video=` mount opened and the fake player it bound (`InspectKit.ResetVideo`), the tab workspace (unless <paramref name="keepRoute"/>), playback as the boot left it (`InspectKit.ResetPlayback`: the boot's deck and
        /// queue, the play log emptied), and every theme's option values (unless `option` set one with nothing mounted, for this mount;
        /// <paramref name="keepOptions"/> false forgets them anyway: `reset`). Not every static: a fresh process is the only full reset,
        /// and `status` counts `mounts` so a reused server shows.</summary>
        void ClearLeaks(bool keepRoute, bool keepOptions = true)
        {
            bool optionsForNext = _optionsForNext && keepOptions;
            _optionsForNext = false;
            RestoreSettings();   // the app settings `setting` wrote on the last mount (kept when written for this one)
            if (_mounts == 0) return;
            Wavee.Shell.SetInspectState("none");
            InspectKit.RestoreColdPlaylist();
            Entities.RestoreStagedShelves();   // a `state page=<library tab>` staged the account's relations
            Stage.Close(null, "inspect-mount");
            if (!keepRoute) InspectKit.ResetTabs();
            InspectKit.ResetVideo(_fakePlayer);
            _fakePlayer = null;
            InspectKit.ResetPlayback();
            InspectKit.EndVideoJoins();   // after the reset: the parked resolves answer a reducer that plays no video any more
            if (!optionsForNext) InspectKit.ForgetThemeOptions();   // the mount's ActivateTheme then reads the defaults
        }

        /// <summary>`mount marquee=on|off remote=<name> panelW=<dip>`, set before the mount: the app's marquee setting (Settings > Appearance), a remote output
        /// the play bar says it is playing on (`--fake` has no Connect cluster; without `remote` the real roster decides again), and the side panel's stored
        /// width (clamped to the theme's drag range when the panel opens).</summary>
        static bool BarPrefs(Inspect.Args a, out string error)
        {
            error = "";
            if (a.Has("marquee"))
            {
                string m = a.Str("marquee").ToLowerInvariant();
                if (m is not ("on" or "off" or "true" or "false")) { error = "marquee is on or off"; return false; }
                Platform.Settings.Set(Platform.Keys.MarqueeEnabled, m is "on" or "true");
                Prefs.Appearance.Bump();
            }
            Wavee.Theming.InspectSwitches.RemoteDevice = a.Has("remote") ? a.Str("remote") : null;
            if (a.Has("panelW"))
            {
                float w = a.Float("panelW", 0f);
                if (!(w > 0f)) { error = "panelW wants a width in DIP"; return false; }
                Shell.PanelSizes.Shared.SaveWidth(w);
            }
            return true;
        }

        /// <summary>`mount stage=lyrics|visualizer|queue|artist layout=card|large|centered|artist gallery=on|off heroLyrics=on|off`: the fullscreen stage's
        /// preferences (heroLyrics: the Artist layout's "lyrics over the photo"), set before the mount (the stage reads them as it opens: `key f11` then opens it on that mode, layout and gallery, with the first-run tip already seen).</summary>
        static bool StagePrefs(Inspect.Args a, out string error)
        {
            error = "";
            if (!(a.Has("stage") || a.Has("layout") || a.Has("gallery") || a.Has("heroLyrics"))) return true;
            if (a.Has("stage"))
            {
                int mode = a.Str("stage").ToLowerInvariant() switch { "lyrics" => 0, "visualizer" => 1, "queue" => 2, "artist" => 3, _ => -1 };
                if (mode < 0) { error = "stage is lyrics, visualizer, queue or artist"; return false; }
                Prefs.Stage.SetMode(mode);
            }
            if (a.Has("layout"))
            {
                int layout = a.Str("layout").ToLowerInvariant() switch { "card" => 0, "large" => 1, "centered" => 2, "artist" => 3, _ => -1 };
                if (layout < 0) { error = "layout is card, large, centered or artist"; return false; }
                Prefs.Stage.SetLayout(layout);
            }
            if (a.Has("gallery")) Prefs.Stage.SetGalleryOpen(a.Str("gallery").ToLowerInvariant() is "on" or "true" or "1");
            if (a.Has("heroLyrics")) Prefs.Stage.SetHeroLyrics(a.Str("heroLyrics").ToLowerInvariant() is "on" or "true" or "1");
            Prefs.Stage.SetTipSeen();
            return true;
        }

        /// <summary>`video{resolved, requested, preferred, transport, player}`: the one video surface's placement (process statics a
        /// `mount video=` writes and a mount puts back, ClearLeaks) and whether a player is bound, so a reused server can be compared
        /// with a fresh one.</summary>
        static void WriteVideoFacts(Utf8JsonWriter w)
        {
            Video.PlacementState s = Video.State.Surface.Peek();
            w.WriteStartObject("video");
            w.WriteString("resolved", Video.State.Resolved.ToString());
            w.WriteString("requested", s.Requested.ToString());
            w.WriteString("preferred", s.Preferred.ToString());
            w.WriteString("transport", Video.State.Transport.Peek().ToString());
            w.WriteBoolean("player", Playback.Video.Player.Peek().Player is not null);
            w.WriteEndObject();
        }

        void WriteMountFacts(Utf8JsonWriter w)
        {
            if (_shell is not { } shell) return;
            w.WriteStartObject("size");
            Inspect.Reply.Num(w, "w", shell.Window.ClientSizePx.Width / Math.Max(0.01f, shell.Window.Scale));
            Inspect.Reply.Num(w, "h", shell.Window.ClientSizePx.Height / Math.Max(0.01f, shell.Window.Scale));
            Inspect.Reply.Num(w, "scale", shell.Window.Scale);
            w.WriteEndObject();
            w.WriteString("route", RouteText());
            w.WriteString("text", TextBackend);
            w.WriteString("devices", _devices);
            w.WriteBoolean("render", _surface is not null);
            InspectDump.WriteTiers(w);
            int leaks = NetworkLeaks();
            if (leaks > 0) w.WriteNumber("networkLeaks", leaks);
        }

        /// <summary>`route` (+`arg`), `uri` (an entity uri: its page) or `deeplink` (a wavee:// payload, the OS door).</summary>
        bool Navigate(Inspect.Args a, out string error)
        {
            error = "";
            if (a.Has("deeplink"))
            {
                Wavee.Shell.ApplyDeepLink(a.Str("deeplink"));
                return true;
            }
            Shell.Route route;
            if (a.Has("uri"))
            {
                string uri = a.Str("uri");
                route = Wavee.Shell.For(EntityUri.Parse(uri));
                if (route.Kind == Wavee.Shell.RouteKind.NotFound) { error = "no page for uri '" + uri + "'"; return false; }
            }
            else
            {
                string key = a.Str("route");
                route = Wavee.Shell.Parse(key, a.Str("arg"));
                if (route.Kind == Wavee.Shell.RouteKind.NotFound) { error = "unknown route '" + key + "'"; return false; }
            }
            Wavee.Shell.GoTo(route);
            return true;
        }

        static string RouteText()
        {
            var r = Wavee.Shell.Current.Peek();
            string name = Wavee.Shell.NameOf(r);
            string? arg = Wavee.Shell.ArgOf(r);
            // A prefix route whose arg rides its key (`disco:0:spotify:artist:ar3`, `people:...`) names it once.
            if (arg is not null && name.EndsWith(arg, StringComparison.Ordinal)) arg = null;
            return name + (arg is null ? "" : " " + arg);
        }

        string Theme(int id, string name, Inspect.Args a)
        {
            string theme = a.Str("id").ToLowerInvariant();
            if (!InspectKit.IsShipped(theme)) return Inspect.Reply.Fail(id, name, "unknown theme '" + theme + "' (" + string.Join("|", InspectKit.ThemeIds) + ")");
            if (_shell is null)
            {
                InspectKit.ActivateTheme(theme);
                return Inspect.Reply.Ok(id, name, w => w.WriteString("theme", InspectKit.ActiveThemeId));
            }
            // Mounted: the user's path, a live theme transaction (prewarm, freeze, swap, remount, thaw). Zune is Developer-only.
            if (ThemeInspection.IsDeveloperOnly(theme)) Platform.Developer.Enabled.Value = true;
            Task<FluentGpu.Hosting.ThemeTransactionResult> t = ThemeHost.Switch(theme);
            int max = a.Int("max", 900), ran = 0;
            if (Tracing(a))
            {
                // `trace=N` measures the switch itself: the traced frames are the ones that run the transaction (prewarm, freeze, swap,
                // remount, thaw), from the frame after the request. `transactionFrames` counts to the frame it completed on.
                long start = _frames, doneAt = -1;
                _ = t.ContinueWith(_ => doneAt = _frames, TaskContinuationOptions.ExecuteSynchronously);
                return Settled(id, name, a, w =>
                {
                    w.WriteString("theme", InspectKit.ActiveThemeId);
                    w.WriteString("outcome", !t.IsCompleted ? "incomplete" : t.IsFaulted ? "faulted" : t.Result.Outcome.ToString());
                    w.WriteNumber("transactionFrames", doneAt < 0 ? -1 : doneAt - start + 1);
                }, afterTrace: () => { while (!t.IsCompleted && ran < max) { RunFrames(1); ran++; } }, themeSwitch: true);
            }
            while (!t.IsCompleted && ran < max) { RunFrames(1); ran++; }
            if (!t.IsCompleted) return Inspect.Reply.Fail(id, name, "the theme transaction did not complete within " + max + " frames", WriteAfter);
            string outcome = t.IsFaulted ? "faulted" : t.Result.Outcome.ToString();
            return Settled(id, name, a, w =>
            {
                w.WriteString("theme", InspectKit.ActiveThemeId);
                w.WriteString("outcome", outcome);
                w.WriteNumber("transactionFrames", ran);
            });
        }

        /// <summary>`option` alone lists the theme's user options with their values; `option id=… value=…` sets one through
        /// <c>ThemeHost.Apply</c> (the Settings path). A select takes its choice name (or index), a toggle a bool, a slider a
        /// number, a colour `#RRGGBB[AA]`.</summary>
        string Option(int id, string name, Inspect.Args a)
        {
            string themeId = a.Str("themeId", InspectKit.ActiveThemeId);
            if (ThemeInspection.Find(themeId) is not { } theme) return Inspect.Reply.Fail(id, name, "no theme '" + themeId + "' is installed");
            bool active = string.Equals(themeId, InspectKit.ActiveThemeId, StringComparison.OrdinalIgnoreCase);
            if (!a.Has("id"))
            {
                return Inspect.Reply.Ok(id, name, w =>
                {
                    w.WriteString("theme", ThemeInspection.IdOf(theme));
                    w.WriteStartArray("options");
                    foreach (LayerOption lo in theme.AllOptions)
                    {
                        if (!theme.IsUserOption(lo)) continue;
                        w.WriteStartObject();
                        w.WriteString("id", lo.QualifiedId);
                        w.WriteString("kind", lo.Option.GetType().Name);
                        if (active) WriteBoxed(w, "value", lo.Option.Peek(ThemeHost.Options));
                        IReadOnlyList<string> choices = lo.Option.ChoiceTitleKeys;
                        if (choices.Count > 0)
                        {
                            w.WriteStartArray("choices");
                            for (int i = 0; i < choices.Count; i++) WriteBoxedValue(w, lo.Option.ChoiceAt(i));
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    WriteAfter(w);   // an acting verb's reply ends with `after`, the listing too (nothing changed: it reads what is)
                });
            }
            string optionId = a.Str("id");
            LayerOption? found = theme.FindQualified(optionId) ?? theme.Find(optionId);
            if (found is not { } option) return Inspect.Reply.Fail(id, name, "theme '" + ThemeInspection.IdOf(theme) + "' has no option '" + optionId + "'");
            if (!a.TryGet("value", out Inspect.Value raw)) return Inspect.Reply.Fail(id, name, "option wants a value");
            if (!TryCoerce(option.Option, raw, out object? value)) return Inspect.Reply.Fail(id, name, "'" + raw + "' is not a value of " + option.QualifiedId);
            Task applied = ThemeHost.Apply(new OptionChange(ThemeInspection.IdOf(theme), option.QualifiedId, value!));
            if (_shell is null)
            {
                _optionsForNext = true;   // for the next mount: its ClearLeaks keeps the options
                return Inspect.Reply.Ok(id, name, w => w.WriteString("id", option.QualifiedId));
            }
            int max = a.Int("max", 900), ran = 0;
            while (!applied.IsCompleted && ran < max) { RunFrames(1); ran++; }
            return Settled(id, name, a, w =>
            {
                w.WriteString("id", option.QualifiedId);
                if (active) WriteBoxed(w, "value", option.Option.Peek(ThemeHost.Options));
            });
        }

        static bool TryCoerce(ThemeOption option, Inspect.Value raw, out object? value)
        {
            value = null;
            switch (option)
            {
                case ToggleOption:
                    if (raw.Kind == Inspect.ValueKind.Bool) value = raw.Bool;
                    else if (raw.Kind == Inspect.ValueKind.String && raw.Str is "true" or "false" or "on" or "off")
                        value = raw.Str is "true" or "on";
                    break;
                case SliderOption:
                    if (raw.Kind == Inspect.ValueKind.Number) value = (float)raw.Num;
                    else if (raw.Kind == Inspect.ValueKind.String && float.TryParse(raw.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) value = f;
                    break;
                case ColorOption:
                    if (raw.Kind == Inspect.ValueKind.String && TryHex(raw.Str, out ColorF c)) value = c;
                    break;
                default:
                    {
                        int count = option.ChoiceTitleKeys.Count;
                        if (count == 0) break;
                        if (raw.Kind == Inspect.ValueKind.Number && raw.Num == Math.Floor(raw.Num)) { value = option.ChoiceAt((int)raw.Num); break; }
                        string want = raw.ToString();
                        for (int i = 0; i < count; i++)
                        {
                            object? choice = option.ChoiceAt(i);
                            if (choice is not null && string.Equals(choice.ToString(), want, StringComparison.OrdinalIgnoreCase)) { value = choice; break; }
                        }
                        break;
                    }
            }
            return value is not null;
        }

        static bool TryHex(string s, out ColorF c)
        {
            c = default;
            ReadOnlySpan<char> h = s.AsSpan().TrimStart('#');
            if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            if (h.Length == 6) v = (v << 8) | 0xFFu;
            c = ColorF.FromRgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        static void WriteBoxed(Utf8JsonWriter w, string name, object? value)
        {
            w.WritePropertyName(name);
            WriteBoxedValue(w, value);
        }

        static void WriteBoxedValue(Utf8JsonWriter w, object? value)
        {
            switch (value)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case float f: w.WriteNumberValue(float.IsFinite(f) ? Math.Round(f, 3) : 0d); break;
                case ColorF c: w.WriteStringValue(Inspect.Reply.Hex(c)); break;
                default: w.WriteStringValue(value.ToString()); break;
            }
        }

        // ── dump ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The refusal for a key no mounted node carries. A finding's PATH key (<c>zune-search-pill/0/1</c>: a keyless node under its
        /// nearest key) is no key: the hint names the key before the first <c>/</c> (and the finding's rects for x and y); any other key gets
        /// the <c>find prefix=</c> hint.</summary>
        static string NoNodeKeyed(string key)
        {
            int slash = key.IndexOf('/');
            return slash > 0
                ? "no mounted node keyed '" + key + "': a finding's path key names a keyless node under '" + key[..slash] + "'; use the key before the first '/' (" + key[..slash] + "), or x and y from the finding's rects"
                : "no mounted node keyed '" + key + "' (find prefix=" + Inspect.Quote(key) + " lists near keys)";
        }

        /// <summary>The nodes a `dump` or `lint` covers: `root` (every node keyed so; `nth` picks one), else the whole tree plus the overlays that do
        /// not hang under the root (flyouts, dialogs), dumped after it.</summary>
        bool TryRoots(Inspect.Args a, Shell.HeadlessShell shell, out List<NodeHandle> roots, out string error)
        {
            error = "";
            SceneStore scene = shell.Host.Scene;
            roots = [];
            if (a.Has("root"))
            {
                string key = a.Str("root");
                foreach (var (node, k) in shell.FindPrefix(key))
                    if (string.Equals(k, key, StringComparison.Ordinal)) roots.Add(node);
                if (roots.Count == 0) { error = NoNodeKeyed(key); return false; }
                int nth = a.Int("nth", -1);
                if (nth >= 0)
                {
                    if (nth >= roots.Count) { error = "only " + roots.Count + " nodes keyed '" + key + "'"; return false; }
                    var one = roots[nth];
                    roots.Clear();
                    roots.Add(one);
                }
                return true;
            }
            roots.Add(scene.Root);
            for (int i = 0; i < scene.OverlayCount; i++)
            {
                NodeHandle overlay = scene.OverlayAt(i);
                if (!overlay.IsNull && !InspectKit.IsUnder(scene, overlay, scene.Root)) roots.Add(overlay);
            }
            return true;
        }

        string Dump(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            if (!TryRoots(a, shell, out List<NodeHandle> roots, out string error)) return Inspect.Reply.Fail(id, name, error);
            var request = new InspectDump.Request(
                MaxDepth: a.Int("depth", int.MaxValue),
                KeysOnly: a.Bool("keysOnly", false),
                VisibleOnly: a.Bool("visibleOnly", true),
                MaxNodes: Math.Clamp(a.Int("max", 4000), 1, 200_000),
                Text: a.Bool("text", true),
                MaxKeyDepth: a.Int("keyDepth", int.MaxValue));
            return Inspect.Reply.Ok(id, name, w => InspectDump.Write(w, shell, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(roots), in request));
        }

        // ── input and frames ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Where a pointer verb aims and at what: <see cref="Node"/> and <see cref="Key"/> for a `key` target (null for
        /// a point), its index among the counted matches and how many there were.</summary>
        readonly record struct Aim(Point2 Point, NodeHandle Node, string? Key, int Nth, int Counted, int Matches, bool Forced, float Fraction, string Note)
        {
            /// <summary>How the target's rect was found still (click and hover; default for a point, a forced target and the wheel).</summary>
            public Stability Stable { get; init; }
        }

        /// <summary>The stable check of a pointer target: <see cref="Held"/> when its presented rect was the same on two consecutive frames,
        /// after <see cref="Frames"/> frames of waiting (0: the host was idle, so nothing could move it, or <see cref="How"/> is `skipped`);
        /// otherwise the rect before the last frame and after it.</summary>
        readonly record struct Stability(bool Held, int Frames, string How, RectF Before, RectF Last);

        /// <summary>Wait, at most <paramref name="max"/> frames, until <paramref name="node"/>'s presented rect is the same on two consecutive
        /// frames. An idle host (no animation track, no scroller in motion, no wake reason but the caret's and the warm cadence's) owes no
        /// frame that could move it: held at once, with no frame run, so a target after a settled verb costs nothing. <paramref name="max"/>
        /// 0 skips the check.</summary>
        Stability WaitStable(NodeHandle node, int max)
        {
            Shell.HeadlessShell shell = _shell!;
            SceneStore scene = shell.Host.Scene;
            RectF before = scene.PresentedAbsoluteRect(node);
            if (max <= 0) return new Stability(true, 0, "skipped", before, before);
            const FluentGpu.Hosting.WakeReasons still = FluentGpu.Hosting.WakeReasons.Caret | FluentGpu.Hosting.WakeReasons.WarmCadence;
            if (!shell.Host.Animation.HasActive && !shell.Host.AnyUserScrollMoving && (shell.Host.CurrentWakeReasons & ~still) == 0)
                return new Stability(true, 0, "idle", before, before);
            for (int i = 1; i <= max; i++)
            {
                RunFrames(1);
                if (!scene.IsLive(node)) return new Stability(false, i, "gone", before, default);
                RectF now = scene.PresentedAbsoluteRect(node);
                if (Same(before, now)) return new Stability(true, i, "held", before, now);
                if (i == max) return new Stability(false, i, "moving", before, now);
                before = now;
            }
            return new Stability(false, max, "moving", before, before);

            static bool Same(in RectF a, in RectF b)
                => MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Y - b.Y) < 0.01f && MathF.Abs(a.W - b.W) < 0.01f && MathF.Abs(a.H - b.H) < 0.01f;
        }

        /// <summary>The point a pointer verb aims at: `key` (the visible centre of an ACTIONABLE node keyed so: shown, at least half
        /// visible, enabled, and hit at that centre by itself or a part of it; `nth` counts the actionable matches in reading order) or
        /// `x`,`y` (window DIP, no checks). `force=true` skips the checks and counts every match. <paramref name="wheel"/>: the
        /// `scroll` verb (no handler needed at the centre). On a refusal <paramref name="refusal"/> is the ok:false reply: the reason,
        /// and every match with its own.</summary>
        bool TryAim(int id, string name, Inspect.Args a, bool wheel, out Aim aim, out string refusal)
        {
            aim = default;
            refusal = "";
            Shell.HeadlessShell shell = _shell!;
            SceneStore scene = shell.Host.Scene;
            if (!a.Has("key"))
            {
                if (a.TryNum("x", out double x) && a.TryNum("y", out double y))
                {
                    aim = new Aim(new Point2((float)x, (float)y), NodeHandle.Null, null, 0, 0, 0, false, 1f, "");
                    return true;
                }
                refusal = Inspect.Reply.Fail(id, name, "give key (a node key) or x and y (window DIP)");
                return false;
            }
            string key = a.Str("key");
            List<(NodeHandle Node, string Key)> found = InspectDump.Exact(shell, key);
            if (found.Count == 0)
            {
                refusal = Inspect.Reply.Fail(id, name, NoNodeKeyed(key));
                return false;
            }
            bool force = a.Bool("force", false);
            int nth = a.Int("nth", 0);
            RectF window = InspectKit.WindowRect(shell);
            Actionability.Verdict[] verdicts = [];
            int counted = 0, pick = -1;
            void Resolve()
            {
                InspectDump.InReadingOrder(scene, found);
                verdicts = new Actionability.Verdict[found.Count];
                counted = 0;
                pick = -1;
                for (int i = 0; i < found.Count; i++)
                {
                    verdicts[i] = InspectKit.Assess(shell, found[i].Node, window, wheel);
                    if (!force && !verdicts[i].Ok) continue;
                    if (counted == nth) pick = i;
                    counted++;
                }
            }
            Resolve();
            // Stable (click and hover; Playwright's check): the picked target's presented rect is the same on two consecutive frames. A
            // target still moving (an entrance, a FLIP, a scroll glide carrying it) is waited for, at most `stableMax` frames, and refused
            // as `unstable` when it never holds; the frames the wait ran can change what is actionable, so the matches are judged again.
            Stability stable = default;
            if (pick >= 0 && !force && !wheel)
            {
                NodeHandle target = found[pick].Node;
                stable = WaitStable(target, a.Int("stableMax", 30));
                if (!stable.Held)
                {
                    string why = stable.How == "gone"
                        ? "'" + key + "' unmounted while its rect was waited for (" + stable.Frames + " frames)"
                        : "'" + key + "' is not stable: its rect moved on every one of the " + stable.Frames + " frames waited; settle first, raise stableMax, or force=true aims at it anyway";
                    refusal = Inspect.Reply.Fail(id, name, why, w =>
                    {
                        w.WriteString("reason", "unstable");
                        w.WriteNumber("frames", stable.Frames);
                        Inspect.Reply.Rect(w, "rect", stable.Before);
                        if (stable.How != "gone") Inspect.Reply.Rect(w, "now", stable.Last);
                    });
                    return false;
                }
                if (stable.Frames > 0)
                {
                    found = InspectDump.Exact(shell, key);
                    if (found.Count == 0)
                    {
                        refusal = Inspect.Reply.Fail(id, name, "no mounted node keyed '" + key + "' after the " + stable.Frames + " frames its rect was waited for");
                        return false;
                    }
                    Resolve();
                }
            }
            if (pick >= 0)
            {
                Actionability.Verdict v = verdicts[pick];
                // Forced onto a node with nothing visible: its presented centre, as before the checks existed.
                Point2 at = force && v.Visible.IsEmpty ? InspectKit.CentreOf(scene.PresentedAbsoluteRect(found[pick].Node)) : v.Aim;
                aim = new Aim(at, found[pick].Node, key, nth, counted, found.Count, force, v.Fraction, force ? "" : v.Note) { Stable = stable };
                return true;
            }
            string error = counted == 0
                ? "'" + key + "' is not actionable: " + verdicts[0].Why
                    + (found.Count > 1 ? " (and " + (found.Count - 1) + " more matches, none actionable)" : "") + "; force=true aims at it anyway"
                : "only " + counted + (force ? " nodes keyed '" : " actionable nodes keyed '") + key + "' (nth counts from 0, in reading order)";
            string reason = counted == 0 ? verdicts[0].Reason : "nth";
            refusal = Inspect.Reply.Fail(id, name, error, w =>
            {
                w.WriteString("reason", reason);
                w.WriteStartArray("matches");
                for (int i = 0; i < found.Count; i++)
                {
                    Actionability.Verdict v = verdicts[i];
                    w.WriteStartObject();
                    Inspect.Reply.Rect(w, "rect", scene.PresentedAbsoluteRect(found[i].Node));
                    Inspect.Reply.Num(w, "onScreen", v.Fraction);
                    w.WriteBoolean("actionable", v.Ok);
                    if (!v.Ok)
                    {
                        w.WriteString("reason", v.Reason);
                        w.WriteString("why", v.Why);
                    }
                    if (v.HitKey is not null) w.WriteString("hit", v.HitKey);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            return false;
        }

        /// <summary>A pointer verb's reply: `at` and, for a key target, `key` and `target {nth, of, matches, onScreen, order, forced?, note?, stable?}`.</summary>
        static void WriteAim(Utf8JsonWriter w, in Aim aim)
        {
            w.WriteStartObject("at");
            Inspect.Reply.Num(w, "x", aim.Point.X);
            Inspect.Reply.Num(w, "y", aim.Point.Y);
            w.WriteEndObject();
            if (aim.Key is null) return;
            w.WriteString("key", aim.Key);
            w.WriteStartObject("target");
            w.WriteNumber("nth", aim.Nth);
            w.WriteNumber("of", aim.Counted);
            w.WriteNumber("matches", aim.Matches);
            Inspect.Reply.Num(w, "onScreen", aim.Fraction);
            w.WriteString("order", aim.Forced ? "reading order (top, then left), every match (force=true: no checks)" : "reading order (top, then left), actionable matches only");
            if (aim.Forced) w.WriteBoolean("forced", true);
            if (aim.Note.Length > 0) w.WriteString("note", aim.Note);
            if (aim.Stable.How is { Length: > 0 })   // default (a point, force=true, the wheel): no check ran, no field
            {
                w.WriteStartObject("stable");
                w.WriteString("how", aim.Stable.How);
                w.WriteNumber("frames", aim.Stable.Frames);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        /// <summary>The key of the node a pointer at <paramref name="p"/> resolves to (the dispatcher's hit test, named by its nearest
        /// keyed ancestor), or null over nothing that takes input. Remembers <paramref name="p"/> as where the pointer is.</summary>
        string? HitKey(Point2 p)
        {
            _pointer = p;
            NodeHandle hit = _shell!.Host.Input.HitTest(p);
            return hit.IsNull ? null : InspectKit.KeyOf(_shell.Host.Scene, hit);
        }

        /// <summary>The key of the scroll viewport a wheel at <paramref name="p"/> drives (the engine's resolution: the nearest scroller
        /// of the wheel's axis with room to move, else of the other axis, else the nearest scroller), or null over none. Remembers
        /// <paramref name="p"/> as where the pointer is.</summary>
        string? WheelKey(Point2 p, bool horizontal)
        {
            _pointer = p;
            var input = _shell!.Host.Input;
            NodeHandle vp = input.ScrollableUnderForAxis(p, horizontal);
            if (vp.IsNull) vp = input.ScrollableUnderForAxis(p, !horizontal);
            if (vp.IsNull) vp = input.ScrollableUnder(p);
            return vp.IsNull ? null : InspectKit.KeyOf(_shell.Host.Scene, vp);
        }

        /// <summary>`after {route, focused, overlays, hit}`: where an acting verb left the app. `focused` and `hit` are keys (the
        /// nearest keyed ancestor); `overlays` the open overlay layers (`overlay:<id>`, with `first`, the first key inside each);
        /// `hit` is what a pointer verb's press landed on (`scroll`: the scroller its wheel drives), else what lies under the pointer now
        /// (null before any pointer verb).</summary>
        void WriteAfter(Utf8JsonWriter w)
        {
            if (_shell is not { } shell) return;
            SceneStore scene = shell.Host.Scene;
            w.WriteStartObject("after");
            w.WriteString("route", RouteText());
            NodeHandle focused = shell.Host.Input.Focused;
            if (!focused.IsNull && scene.IsLive(focused)) w.WriteString("focused", InspectKit.KeyOf(scene, focused));
            else w.WriteNull("focused");
            WriteOverlays(w, shell);   // Diagnostics.Inspect.Picture.cs
            string? hit = _hit ?? (_pointer is { } p ? HitKey(p) : null);
            if (hit is not null) w.WriteString("hit", hit);
            else w.WriteNull("hit");
            w.WriteEndObject();
        }

        /// <summary>The number in an overlay layer's key (`overlay:12` is 12), -1 when there is none.</summary>
        static int LayerId(string key)
            => int.TryParse(key.AsSpan("overlay:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;

        /// <summary>`mods=ctrl+shift`: the modifiers a click holds (None when absent). False for a word that is not one (`crtl`), so a
        /// typo is refused instead of clicking with nothing held.</summary>
        static bool TryMods(Inspect.Args a, out KeyModifiers mods)
        {
            mods = KeyModifiers.None;
            string m = a.Str("mods");
            return m.Length == 0 || Inspect.TryKey(m + "+a", out _, out mods);
        }

        void Move(Point2 p) => Queue(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: _ms));

        void Queue(in InputEvent e) => _shell!.Window.QueueInput(in e);

        /// <summary>`dialog lightbox`'s photographs: the bright header, then three bundled covers.</summary>
        static string[] LightboxPhotos()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "assets", "covers");
            return [Path.Combine(dir, "header-bright.jpg"), Path.Combine(dir, "cover01.jpg"), Path.Combine(dir, "cover05.jpg"), Path.Combine(dir, "cover09.jpg")];
        }

        /// <summary>How long `play` waits, on the wall clock, for its load to land.</summary>
        const int PlayLoadBudgetMs = 5_000;

        /// <summary>Step frames until the playback phase leaves Loading (the load landed, or failed) or <paramref name="budgetMs"/> of wall
        /// clock runs out; the phase, lower case ("loading" when it ran out).</summary>
        string AwaitPlaybackLoad(int budgetMs)
        {
            long until = Environment.TickCount64 + budgetMs;
            while (Playback.PhaseSignal.Peek() == Playback.Phase.Loading && Environment.TickCount64 < until)
            {
                RunFrames(1);
                if (Playback.PhaseSignal.Peek() == Playback.Phase.Loading) Thread.Sleep(2);
            }
            return Playback.PhaseSignal.Peek().ToString().ToLowerInvariant();
        }

        void RunFrames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                _shell!.Host.RunFrame();
                _ms += 16;
                _frames++;
                var stats = _shell.Host.LastStats;
                _hotAlloc += stats.HotPhaseAllocBytes;
                if (stats.Rendered) _paintedFrames++;
            }
        }

        // The `frames` verb's allocation census: the engine's hot-phase bytes (frame phases 6-13, FrameStats.HotPhaseAllocBytes) summed over
        // the frames it ran, and how many of them painted (a steady animation must read 0 bytes over many painted frames).
        long _hotAlloc;
        int _paintedFrames;

        InspectKit.SettleResult Settle(int max, InspectKit.LoopMode loops = InspectKit.LoopMode.Ignore)
        {
            InspectKit.SettleResult r = InspectKit.Settle(_shell!, Math.Clamp(max, 0, 100_000), loops);
            _ms += (uint)(16 * r.Frames);
            _frames += r.Frames;
            return r;
        }

        /// <summary>The reply of a verb that settles after itself (unless `settle=false`): its frames and whether it settled (with
        /// `busy` when the cap stopped it), the verb's own <paramref name="body"/>, and `after` when the verb acts.</summary>
        string Settled(int id, string name, Inspect.Args a, Action<Utf8JsonWriter>? body = null, string traceScroll = "",
            InspectKit.LoopMode loops = InspectKit.LoopMode.Ignore, Action? afterTrace = null, bool themeSwitch = false)
        {
            InspectKit.SettleResult settle = default;
            InspectTrace.Result? trace = null;
            if (Tracing(a) && _shell is { } shell)
            {
                // `trace=N` on any settling verb: no settle, N traced frames from the frame after the action (docs: agent-inspect.md)
                if (!InspectTrace.TryParse(a, a.Int("trace", 0), traceScroll, out InspectTrace.Options o, out string error)) return Inspect.Reply.Fail(id, name, error);
                o.ThemeSwitch = themeSwitch;   // `theme`: the frames its transaction freezes are the switch's own (not gated)
                trace = RunTrace(shell, o);
                afterTrace?.Invoke();   // what the verb still owes once the traced frames ran (`theme`: the transaction's end)
            }
            else if (a.Bool("settle", true)) settle = Settle(a.Int("max", 600), InspectKit.LoopsArg(a, loops));
            bool acts = _acts;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("frames", settle.Frames);
                w.WriteBoolean("settled", settle.Settled);
                if (settle.Frames > 0 && !settle.Settled && _shell is { } mounted) InspectKit.WriteBusy(w, mounted, in settle);
                InspectKit.WriteAmbient(w, in settle);
                body?.Invoke(w);
                if (trace is not null)
                {
                    w.WriteStartObject("trace");
                    InspectTrace.Write(w, trace);
                    w.WriteEndObject();
                }
                if (acts) WriteAfter(w);
            });
        }

        static NodeHandle ScrollUnder(SceneStore scene, NodeHandle root)
        {
            if (root.IsNull) return NodeHandle.Null;
            foreach (var n in InspectKit.Subtree(scene, root))
                if (scene.HasScroll(n)) return n;
            return NodeHandle.Null;
        }

        void WriteStatus(Utf8JsonWriter w)
        {
            w.WriteBoolean("mounted", _shell is not null);
            w.WriteNumber("frames", _frames);
            w.WriteNumber("mounts", _mounts);   // above 1: a reused server, so state a mount does not clear may carry over
            WriteHealth(w);   // overlays, memory and GC (Diagnostics.Inspect.Picture.cs)
            WriteVideoFacts(w);
            if (_shell is { } shell)
            {
                WriteMountFacts(w);   // size, route, text, devices, render, tiers, theme, kind (and networkLeaks when any)
                w.WriteBoolean("animating", shell.Host.Animation.HasActive);
                w.WriteString("trimSource", InspectDump.TrimSource(shell));
                return;
            }
            w.WriteString("theme", InspectKit.ActiveThemeId);
            w.WriteString("kind", Tok.Theme == ThemeKind.Light ? "light" : "dark");
            w.WriteString("route", RouteText());
            w.WriteString("text", TextBackend);
            w.WriteNumber("networkLeaks", NetworkLeaks());
        }

        /// <summary>Requests that left the process: the host refuses every request (`Wire.GoOffline`), so the `wire` log holds
        /// refusals only and any other line is a request that was sent (the shell tests' AssertNoNetwork rule).</summary>
        static int NetworkLeaks()
        {
            int n = 0;
            foreach (var entry in Log.Snapshot())
                if (entry.Category == "wire" && !entry.Message.StartsWith("wire.refused ", StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
