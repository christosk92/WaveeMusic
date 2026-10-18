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
// real D3D12 device (WARP by default) presenting into a hidden, never-activated off-monitor tool window, with a DirectWrite
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
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
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

        public void Dispose() => Unmount();

        void Unmount()
        {
            if (_shell is null) return;
            var shell = _shell;
            _shell = null;
            _surface = null;   // released by the shell's dispose (HeadlessDevices.Release), after its host
            shell.Dispose();
        }

        /// <summary>Mount the frame on the best devices this machine has (see the file header): the offscreen surface (real
        /// pixels and text), else the recording device with DirectWrite (true text, no pixels), else (`--text headless`) the
        /// shell tests' devices.</summary>
        Shell.HeadlessShell MountOn(float widthDip, float heightDip, float scale, string gpu)
        {
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
                    var devices = new Wavee.Shell.HeadlessDevices(surface.Window, surface.Input, surface.Device, fonts,
                        Attach: surface.Attach,
                        Release: () => { fonts.Dispose(); surface.Dispose(); });
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
            quit = cmd.Verb == Inspect.Verb.Quit;
            return Execute(in cmd);
        }

        /// <summary>Run one parsed command; never throws (a failure is an ok:false reply naming the exception).</summary>
        public string Execute(in Inspect.Command cmd)
        {
            try { return Dispatch(in cmd); }
            catch (Exception ex)
            {
                Log.Warn("inspect", "command '" + cmd.Name + "' failed", ex);
                return Inspect.Reply.Fail(cmd.Id, cmd.Name, ex.GetType().Name + ": " + ex.Message);
            }
        }

        string Dispatch(in Inspect.Command cmd)
        {
            Inspect.Args a = cmd.Args;
            int id = cmd.Id;
            string name = cmd.Name;
            switch (cmd.Verb)
            {
                case Inspect.Verb.Help:
                    return Inspect.Reply.Ok(id, name, w =>
                    {
                        w.WriteStartArray("verbs");
                        foreach (string v in Inspect.VerbNames) w.WriteStringValue(v);
                        w.WriteEndArray();
                        w.WriteString("doc", "docs/guide/agent-inspect.md");
                    });
                case Inspect.Verb.Quit:
                    Unmount();
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
                    Wavee.Shell.Headless.ResetNavigation();
                    InspectKit.ActivateTheme(InspectKit.DefaultThemeId, ThemeKind.Dark);
                    return Inspect.Reply.Ok(id, name, w => w.WriteString("theme", InspectKit.ActiveThemeId));
                case Inspect.Verb.Theme:
                    return Theme(id, name, a);
                case Inspect.Verb.Tiers:
                    return Inspect.Reply.Ok(id, name, InspectDump.WriteTiers);
                case Inspect.Verb.Option:
                    return Option(id, name, a);
                case Inspect.Verb.Crossing:
                    return Crossing(id, name, a);   // mounts by itself when nothing is
            }

            // Everything below needs a mount.
            if (_shell is not { } shell) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first)");
            switch (cmd.Verb)
            {
                case Inspect.Verb.GoTo:
                    {
                        if (!Navigate(a, out string error)) return Inspect.Reply.Fail(id, name, error);
                        return Settled(id, name, a);
                    }
                case Inspect.Verb.Resize:
                    {
                        float w0 = a.Float("w", 0f), h0 = a.Float("h", 0f);
                        if (!(w0 >= 1f && h0 >= 1f)) return Inspect.Reply.Fail(id, name, "resize wants w and h (DIP)");
                        shell.Resize(w0, h0);
                        return Settled(id, name, a);
                    }
                case Inspect.Verb.Frames:
                    {
                        int n = Math.Clamp(a.Int("n", 1), 0, 100_000);
                        RunFrames(n);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteNumber("frames", n);
                            w.WriteBoolean("animating", shell.Host.Animation.HasActive);
                        });
                    }
                case Inspect.Verb.Settle:
                    {
                        var (frames, settled) = Settle(a.Int("max", 600));
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteNumber("frames", frames);
                            w.WriteBoolean("settled", settled);
                        });
                    }
                case Inspect.Verb.Click:
                case Inspect.Verb.Hover:
                    {
                        if (!TryTarget(a, out Point2 p, out string? key, out string error)) return Inspect.Reply.Fail(id, name, error);
                        Move(p);
                        RunFrames(1);
                        if (cmd.Verb == Inspect.Verb.Click)
                        {
                            KeyModifiers mods = Mods(a);
                            int button = a.Str("button", "left") == "right" ? 1 : 0;
                            Queue(new InputEvent(InputKind.PointerDown, p, button, 0, mods, TimestampMs: _ms));
                            RunFrames(1);
                            Queue(new InputEvent(InputKind.PointerUp, p, button, 0, mods, TimestampMs: _ms + 50));
                            if (!Tracing(a)) RunFrames(1);   // `trace=N` starts at the frame the release lands on
                        }
                        return Settled(id, name, a, w =>
                        {
                            w.WriteStartObject("at");
                            Inspect.Reply.Num(w, "x", p.X);
                            Inspect.Reply.Num(w, "y", p.Y);
                            w.WriteEndObject();
                            if (key is not null) w.WriteString("key", key);
                        });
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
                        if (!TryTarget(a, out Point2 p, out string? key, out string error)) return Inspect.Reply.Fail(id, name, error);
                        float dy = a.Float("dy", 3f), dx = a.Float("dx", 0f);
                        Move(p);
                        RunFrames(1);
                        var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, p, dx, dy, 0, KeyModifiers.None);
                        Queue(InputEvent.ForScroll(in wheel, timestampMs: _ms));
                        if (!Tracing(a)) RunFrames(1);   // `trace=N` starts at the frame the notch lands on
                        return Settled(id, name, a, traceScroll: key ?? "main", body: w =>
                        {
                            if (key is null) return;
                            NodeHandle viewport = ScrollUnder(shell.Host.Scene, shell.Find(key));
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
                        if (!InspectKit.Play(uri, Math.Max(0, a.Int("fromMs", 0)), a.Int("next", 0))) return Inspect.Reply.Fail(id, name, "play wants a spotify:track|episode uri, not '" + uri + "'");
                        RunFrames(2);
                        return Settled(id, name, a, w => w.WriteString("uri", uri));
                    }
                case Inspect.Verb.State:
                    {
                        // `state {kind}`: the page region shows a state of its own in place of the page (empty, nomatch, error, offline; `none` puts the page back; `loading` opens a
                        // search for a query the fake catalogue never answers, so its skeleton stays). The states are the shared vacancy, so a theme's picture of it can be rendered.
                        string kind = a.Str("kind").ToLowerInvariant();
                        if (!Shell.SetInspectState(kind)) return Inspect.Reply.Fail(id, name, "state wants kind=empty|nomatch|error|offline|loading|none, not '" + kind + "'");
                        RunFrames(30);
                        return Settled(id, name, a, w => w.WriteString("kind", kind));
                    }
                case Inspect.Verb.Find:
                    {
                        string key = a.Str("key"), prefix = a.Str("prefix");
                        if (key.Length == 0 && prefix.Length == 0) return Inspect.Reply.Fail(id, name, "find wants key or prefix");
                        return Inspect.Reply.Ok(id, name, w => InspectDump.WriteFind(w, shell, key, prefix));
                    }
                case Inspect.Verb.Dump:
                    return Dump(id, name, a, shell);
                case Inspect.Verb.Render:
                    return Render(id, name, a, shell);
                case Inspect.Verb.Lint:
                    return Lint(id, name, a, shell);
                case Inspect.Verb.Trace:
                    return Trace(id, name, a, shell);
            }
            return Inspect.Reply.Fail(id, name, "verb not handled");
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
            string gpu = a.Str("gpu", "warp").ToLowerInvariant();
            if (gpu is not ("warp" or "hardware" or "none")) { error = "gpu is warp, hardware or none"; return false; }
            if (!StagePrefs(a, out error)) return false;
            _shell = MountOn(w0, h0, scale, gpu);
            _frames++;
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
                InspectKit.Play("spotify:track:tr0", a.Int("fromMs", 74_000));
                RunFrames(30);
                Wavee.Video.State.FoldAvailability(true);
                Wavee.Video.State.OpenAt(a.Str("video").ToLowerInvariant() switch
                {
                    "docked" => Wavee.Video.SurfacePlacement.Docked,
                    "floating" or "pip" => Wavee.Video.SurfacePlacement.Floating,
                    _ => Wavee.Video.SurfacePlacement.Fullscreen,
                });
            }
            // `feedback=bug|feature` opens the report form over the page (the surface the settings' About links open).
            if (a.Has("feedback"))
                Feedback.Open(string.Equals(a.Str("feedback"), "feature", StringComparison.OrdinalIgnoreCase) ? Feedback.ReportKind.Feature : Feedback.ReportKind.Bug);
            return true;
        }

        /// <summary>`mount stage=lyrics|visualizer|queue|artist layout=card|large|centered|artist gallery=on|off`: the fullscreen stage's preferences, set
        /// before the mount (the stage reads them as it opens: `key f11` then opens it on that mode, layout and gallery, with the first-run tip already seen).</summary>
        static bool StagePrefs(Inspect.Args a, out string error)
        {
            error = "";
            if (!(a.Has("stage") || a.Has("layout") || a.Has("gallery"))) return true;
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
            Prefs.Stage.SetTipSeen();
            return true;
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
            string? arg = Wavee.Shell.ArgOf(r);
            return Wavee.Shell.NameOf(r) + (arg is null ? "" : " " + arg);
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
            while (!t.IsCompleted && ran < max) { RunFrames(1); ran++; }
            if (!t.IsCompleted) return Inspect.Reply.Fail(id, name, "the theme transaction did not complete within " + max + " frames");
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
                });
            }
            string optionId = a.Str("id");
            LayerOption? found = theme.FindQualified(optionId) ?? theme.Find(optionId);
            if (found is not { } option) return Inspect.Reply.Fail(id, name, "theme '" + ThemeInspection.IdOf(theme) + "' has no option '" + optionId + "'");
            if (!a.TryGet("value", out Inspect.Value raw)) return Inspect.Reply.Fail(id, name, "option wants a value");
            if (!TryCoerce(option.Option, raw, out object? value)) return Inspect.Reply.Fail(id, name, "'" + raw + "' is not a value of " + option.QualifiedId);
            Task applied = ThemeHost.Apply(new OptionChange(ThemeInspection.IdOf(theme), option.QualifiedId, value!));
            if (_shell is null) return Inspect.Reply.Ok(id, name, w => w.WriteString("id", option.QualifiedId));
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
                if (roots.Count == 0) { error = "no mounted node keyed '" + key + "'"; return false; }
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
                if (!overlay.IsNull && !Under(scene, overlay, scene.Root)) roots.Add(overlay);
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
                Text: a.Bool("text", true));
            return Inspect.Reply.Ok(id, name, w => InspectDump.Write(w, shell, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(roots), in request));
        }

        static bool Under(SceneStore scene, NodeHandle node, NodeHandle ancestor)
        {
            for (var n = node; !n.IsNull; n = scene.Parent(n))
                if (n == ancestor) return true;
            return false;
        }

        // ── input and frames ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The point a pointer verb aims at: `key` (the centre of its presented rect; `nth` picks among duplicates)
        /// or `x`,`y` (window DIP).</summary>
        bool TryTarget(Inspect.Args a, out Point2 p, out string? key, out string error)
        {
            p = default; key = null; error = "";
            SceneStore scene = _shell!.Host.Scene;
            if (a.Has("key"))
            {
                string want = a.Str("key");
                key = want;
                var found = _shell.FindPrefix(want);
                found.RemoveAll(e => !string.Equals(e.Key, want, StringComparison.Ordinal));
                if (found.Count == 0) { error = "no mounted node keyed '" + want + "'"; return false; }
                int nth = a.Int("nth", 0);
                if ((uint)nth >= (uint)found.Count) { error = "only " + found.Count + " nodes keyed '" + key + "'"; return false; }
                p = InspectKit.CentreOf(scene, found[nth].Node);
                return true;
            }
            if (a.TryNum("x", out double x) && a.TryNum("y", out double y)) { p = new Point2((float)x, (float)y); return true; }
            error = "give key (a node key) or x and y (window DIP)";
            return false;
        }

        static KeyModifiers Mods(Inspect.Args a)
        {
            string m = a.Str("mods");
            if (m.Length == 0) return KeyModifiers.None;
            return Inspect.TryKey(m + "+a", out _, out KeyModifiers mods) ? mods : KeyModifiers.None;
        }

        void Move(Point2 p) => Queue(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: _ms));

        void Queue(in InputEvent e) => _shell!.Window.QueueInput(in e);

        void RunFrames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                _shell!.Host.RunFrame();
                _ms += 16;
                _frames++;
            }
        }

        (int Frames, bool Settled) Settle(int max)
        {
            var r = InspectKit.Settle(_shell!, Math.Clamp(max, 0, 100_000));
            _ms += (uint)(16 * r.Frames);
            _frames += r.Frames;
            return r;
        }

        /// <summary>The reply of a verb that settles after itself (unless `settle=false`): its frames and whether it settled.</summary>
        string Settled(int id, string name, Inspect.Args a, Action<Utf8JsonWriter>? body = null, string traceScroll = "")
        {
            int frames = 0;
            bool settled = false;
            InspectTrace.Result? trace = null;
            if (Tracing(a) && _shell is { } shell)
            {
                // `trace=N` on any settling verb: no settle, N traced frames from the frame after the action (docs: agent-inspect.md)
                if (!InspectTrace.TryParse(a, a.Int("trace", 0), traceScroll, out InspectTrace.Options o, out string error)) return Inspect.Reply.Fail(id, name, error);
                trace = RunTrace(shell, o);
            }
            else if (a.Bool("settle", true)) (frames, settled) = Settle(a.Int("max", 600));
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("frames", frames);
                w.WriteBoolean("settled", settled);
                body?.Invoke(w);
                if (trace is not null)
                {
                    w.WriteStartObject("trace");
                    InspectTrace.Write(w, trace);
                    w.WriteEndObject();
                }
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
