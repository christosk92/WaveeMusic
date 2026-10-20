// ── Screens/Diagnostics.Inspect.Protocol.cs ────────────────────────────────────────────────────────────────────────────
// the `--inspect` server's command grammar, its argv parse and the JSON reply writer (pure: no engine, no thread, no I/O)
//
// Role: CORE
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2; docs/guide/agent-inspect.md is the user-facing reference.
//
// ONE LINE = ONE COMMAND, the `--headless` shape: a JSON object `{"cmd":"mount","args":{"w":400,"h":860},"id":1}` (the args
// may also sit at the top level beside cmd and id, and a headless-style `"args":[...]` array fills the verb's positional
// names), or a text line `mount w=400 h=860 theme=zune` (bare words are positional: `goto home`, `find zune-band-pivot:home`;
// `k="a b"` quotes, `k=[a,"b c",3]` is a list). `#` starts a comment line. Every command gets exactly ONE reply line:
// `{"id":1,"ok":true,"cmd":"mount",...}` or `{"id":1,"ok":false,"cmd":"mount","error":"..."}`. The server also prints one
// `{"kind":"ready",...}` line at boot and one `{"kind":"bye"}` line at exit.
//
// THE VERB TABLE (`Specs`) declares every verb's args once; `Validate` refuses an arg it does not declare, and `help` /
// `help schema=json` print it.
//
// AOT: JsonDocument (no reflection) in, Utf8JsonWriter (no DOM) out, the same pair the headless grammar uses.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The `--inspect` server's pure half: options, grammar, key names and the reply writer. The host is
    /// <see cref="InspectHost"/> (Diagnostics.Inspect.cs).</summary>
    public static class Inspect
    {
        // ── the argv ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`--inspect [--pipe name] [--profile dir] [--text dwrite|headless]`.</summary>
        public readonly record struct Options(string Pipe, string Profile, bool HeadlessText)
        {
            public const string Usage =
                "usage: Wavee.exe --inspect [--pipe <name>] [--profile <scratch dir>] [--text dwrite|headless]\n" +
                "  --pipe     read commands from \\\\.\\pipe\\<name> and reply on it (one client); default: stdin / stdout\n" +
                "  --profile  the scratch profile folder (default: a new folder under %TEMP%\\wavee-inspect); never the user's\n" +
                "  --text     dwrite (default): real DirectWrite text metrics; headless: the engine's 0.55 x size model\n" +
                "Commands are JSON lines {\"cmd\",\"args\",\"id\"}; see docs/guide/agent-inspect.md.";

            public static bool TryParse(string[] args, out Options options, out string usage)
            {
                options = new Options("", "", false);
                usage = "";
                string pipe = "", profile = "";
                bool headlessText = false;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--inspect": break;
                        case "--pipe": if (!Value(args, ref i, a, out pipe, ref usage)) return false; break;
                        case "--profile": if (!Value(args, ref i, a, out profile, ref usage)) return false; break;
                        case "--text":
                            {
                                if (!Value(args, ref i, a, out string mode, ref usage)) return false;
                                if (mode is not ("dwrite" or "headless")) { usage = "--text wants dwrite or headless\n" + Usage; return false; }
                                headlessText = mode == "headless";
                                break;
                            }
                        case "--fg":   // engine switches (EngineSwitches.ApplyList), e.g. `--fg motion` logs the projected-motion decisions to stderr
                            {
                                if (!Value(args, ref i, a, out string list, ref usage)) return false;
                                FluentGpu.Hosting.EngineSwitches.ApplyList(list);
                                break;
                            }
                        default:
                            usage = "unknown argument '" + a + "'\n" + Usage;
                            return false;
                    }
                }
                options = new Options(pipe, profile, headlessText);
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
        }

        // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────

        public enum Verb : byte
        {
            None, Help, Status, Mount, Unmount, GoTo, Resize, Frames, Settle, Reset,
            Click, Hover, Key, Type, Scroll, Play, Option, Theme, Find, Dump, Render, Tiers, Lint, Crossing, Trace, State, Dialog, Quit,
        }

        // ── the verb table ──────────────────────────────────────────────────────────────────────────────────────────
        // Every verb and every arg it reads, declared once: the strict-arg check (`Validate`), `help` and `help schema=json` all
        // read this table, so an arg the code reads but the table lacks is refused at the door (add it here when you add it to a
        // verb). Names are case-sensitive; an alias is rewritten to its canonical name before the verb runs.

        /// <summary>What an arg holds (for `help`; the verbs parse the values themselves).</summary>
        public enum ArgType : byte { String, Int, Float, Bool, Size, Key, Path, Enum, List }

        /// <summary>One declared arg: its name, type, default (null: none), choices (an enum's words), aliases and a one-line doc.</summary>
        public sealed record ArgSpec(string Name, ArgType Type, string Doc)
        {
            public string? Default { get; init; }
            public string[] Choices { get; init; } = [];
            public string[] Aliases { get; init; } = [];
        }

        /// <summary>One verb: its canonical name, aliases, a one-line summary, one example, its positional names (bare words fill
        /// them in order) and every arg it reads.</summary>
        public sealed record VerbSpec(Verb Verb, string Name, string Summary, string Example)
        {
            public string[] Aliases { get; init; } = [];
            public string[] Positional { get; init; } = [];
            public ArgSpec[] Args { get; init; } = [];
            /// <summary>Fails with "nothing is mounted" unless a mount is up.</summary>
            public bool NeedsMount { get; init; }
            /// <summary>Replies `after:{route, focused, overlays, hit}`.</summary>
            public bool Acts { get; init; }
        }

        /// <summary>An arg row: <paramref name="choices"/> and <paramref name="aliases"/> are `|`-separated.</summary>
        static ArgSpec Arg(string name, ArgType type, string doc, string? def = null, string choices = "", string aliases = "") => new(name, type, doc)
        {
            Default = def,
            Choices = choices.Length == 0 ? [] : choices.Split('|'),
            Aliases = aliases.Length == 0 ? [] : aliases.Split('|'),
        };

        /// <summary>The groups in order; a name a group repeats keeps its first row.</summary>
        static ArgSpec[] Join(params ArgSpec[][] groups)
        {
            var all = new List<ArgSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArgSpec[] g in groups)
                foreach (ArgSpec a in g)
                    if (seen.Add(a.Name)) all.Add(a);
            return all.ToArray();
        }

        /// <summary>The trace options: the `trace` verb's, and any settling verb's after `trace=N`.</summary>
        static readonly ArgSpec[] s_traceOptions =
        [
            Arg("keys", ArgType.List, "exact keys to watch (comma list or [a,b])"),
            Arg("prefix", ArgType.List, "key prefixes to watch (comma list or [a,b])"),
            Arg("scroll", ArgType.String, "the scroller to trace: main, a node key, or none", "none"),
            Arg("every", ArgType.Int, "emit every Nth row (the summary covers every frame)", "1"),
            Arg("rows", ArgType.Bool, "false: the summary only", "true"),
            Arg("census", ArgType.Bool, "a slow frame's row names the components that made it slow", "false"),
            Arg("jump", ArgType.Float, "a presented move over this many DIP in one frame under a track is a jump", "2"),
            Arg("snap", ArgType.Float, "a change over this many DIP in one frame with no track is a snap", "0.5"),
            Arg("wheel", ArgType.List, "wheel notches per event, cycled (3 or 3,3,-6)"),
            Arg("wheelEvery", ArgType.Int, "one wheel event every N frames", "1"),
            Arg("wheelCount", ArgType.Int, "how many wheel events", "1"),
            Arg("wheelKey", ArgType.Key, "aim the wheel (or pan) at this node"),
            Arg("wheelX", ArgType.Float, "aim the wheel (or pan) at this x (window DIP)"),
            Arg("wheelY", ArgType.Float, "aim the wheel (or pan) at this y (window DIP)"),
            Arg("pan", ArgType.List, "a touchpad/touch contact: DIP per frame, cycled"),
            Arg("panFrames", ArgType.Int, "frames the contact stays down", "8"),
            Arg("panStart", ArgType.Int, "the frame the contact lands on", "0"),
            Arg("panEvery", ArgType.Int, "repeat the gesture every N frames (0: once)", "0"),
            Arg("panCount", ArgType.Int, "how many gestures", "1"),
            Arg("panKind", ArgType.Enum, "the contact's device", "touchpad", "touchpad|touch"),
        ];

        /// <summary>A settling verb's own args (plus <see cref="s_traceOptions"/> after `trace=N`).</summary>
        static readonly ArgSpec[] s_settleArgs = Join(
        [
            Arg("settle", ArgType.Bool, "false: reply at once (frames:0, settled:false) and step with frames yourself", "true"),
            Arg("max", ArgType.Int, "the settle's frame cap (with trace=N: the most nodes watched)", "600"),
            Arg("trace", ArgType.Int, "trace N frames from the action instead of settling (the trace options apply)"),
        ], s_traceOptions);

        /// <summary>Where `goto` (and `mount`, `crossing`) navigate: deeplink wins over uri, uri over route.</summary>
        static readonly ArgSpec[] s_navArgs =
        [
            Arg("route", ArgType.String, "a route key: home, browse, search (+arg), liked, settings, album:<uri>..."),
            Arg("arg", ArgType.String, "the route's argument (search: the query)"),
            Arg("uri", ArgType.String, "an entity uri: its page (spotify:album:al2)"),
            Arg("deeplink", ArgType.String, "a wavee:// payload through the OS door (never validated)"),
        ];

        /// <summary>The mount's own args (`mount`, and `crossing` when it mounts).</summary>
        static readonly ArgSpec[] s_mountArgs = Join(
        [
            Arg("scale", ArgType.Float, "DPI scale, 0 < scale <= 4", "1"),
            Arg("theme", ArgType.Enum, "the theme (default: the active one; a fresh server is classic)", null, "classic|zune|winui"),
            Arg("kind", ArgType.Enum, "light or dark (default: the current kind)", null, "dark|light"),
            Arg("gpu", ArgType.Enum, "warp/hardware: the offscreen D3D12 surface (render works); none: no pixels", "warp", "warp|hardware|none"),
            Arg("keepRoute", ArgType.Bool, "keep the current navigation instead of a fresh Home", "false"),
            Arg("setup", ArgType.Enum, "mount a first run: the setup wizard's plate", null, "true|signin"),
            Arg("feedback", ArgType.Enum, "open the report form over the page", null, "bug|feature"),
            Arg("video", ArgType.Enum, "put the video surface up with a fake track playing", null, "fullscreen|docked|floating|pip"),
            Arg("player", ArgType.Enum, "with video: bind an idle engine player", null, "fake"),
            Arg("chrome", ArgType.Enum, "Zune's video stage: the hover chrome at rest", null, "shown"),
            Arg("fromMs", ArgType.Int, "with video: the fake track's position", "74000"),
            Arg("stage", ArgType.Enum, "the fullscreen stage's mode (key f11 opens it)", null, "lyrics|visualizer|queue|artist"),
            Arg("layout", ArgType.Enum, "the fullscreen stage's layout", null, "card|large|centered|artist"),
            Arg("gallery", ArgType.Enum, "the fullscreen stage's gallery", null, "on|off"),
            Arg("grade", ArgType.Enum, "cover grading: a hue per uri, all grey, or the playing one grey", null, "hue|grey|gray|greynow"),
        ], s_navArgs);

        // ── the lint args: ONE LINE PER ARG (Diagnostics.Inspect.Lint.Scene.cs `TryOptions` reads them; `lint` and `crossing` take them) ──
        static readonly ArgSpec[] s_lintArgs =
        [
            Arg("rules", ArgType.List, "the rules to run (default: all)", null, "gutter|right-edge|clip|trim|type|target|duplicate|rhythm|contrast"),
            Arg("min", ArgType.Enum, "leave out findings below this severity", "info", "info|warn|error"),
            Arg("limit", ArgType.Int, "the most findings one rule reports", "100"),
            Arg("typeFloor", ArgType.Float, "type: text under this size (DIP) is an error", "11"),
            Arg("targetFloor", ArgType.Float, "target: the smallest hit target outside Compact (DIP)", "32"),
            Arg("targetFloorCompact", ArgType.Float, "target: the smallest hit target at Compact (DIP)", "40"),
            Arg("gapMin", ArgType.Float, "rhythm: stacked text blocks closer than this are cramped (DIP)", "2"),
            Arg("allowTrim", ArgType.List, "trim: keys or key prefixes whose text may be trimmed"),
            Arg("allowDuplicate", ArgType.List, "duplicate: labels that may appear twice"),
            Arg("ignoreUnder", ArgType.List, "duplicate: key prefixes it does not look inside", "shell.titlebar,shell.playerbar"),
        ];
        // ── end of the lint args ──

        /// <summary>A pointer verb's target: a key (refused unless actionable) or a point.</summary>
        static readonly ArgSpec[] s_aimArgs =
        [
            Arg("key", ArgType.Key, "the node (exact key); refused unless shown, >= 50% visible, enabled and hit at its visible centre"),
            Arg("nth", ArgType.Int, "which actionable match, in reading order (top, then left)", "0"),
            Arg("x", ArgType.Float, "a point instead of a key (window DIP; no checks)"),
            Arg("y", ArgType.Float, "a point instead of a key (window DIP; no checks)"),
            Arg("force", ArgType.Bool, "skip the actionability checks (nth then counts every match)", "false"),
        ];

        /// <summary>Every verb, in the order `help` lists them.</summary>
        public static readonly VerbSpec[] Specs =
        [
            new(Verb.Help, "help", "List the verbs; help verb=<name> gives one verb's args, defaults, choices and example; help schema=json the whole table", "help verb=lint")
            {
                Positional = ["verb"],
                Args = [Arg("verb", ArgType.String, "the verb to describe (an alias works too)"), Arg("schema", ArgType.Enum, "the whole table as JSON", null, "json")],
            },
            new(Verb.Status, "status", "The session: mounted, frames, mounts so far, theme, kind, route, text backend; the mount facts when mounted", "status") { Aliases = ["ping"] },
            new(Verb.Mount, "mount", "Mount the whole app at w x h DIP (theme, kind, route...); replaces any mount and clears the state vacancy, playback and queue", "mount w=400 h=860 theme=zune kind=dark route=home")
            {
                Positional = ["w", "h"],
                Args = Join([Arg("w", ArgType.Float, "window width (DIP)", "1180"), Arg("h", ArgType.Float, "window height (DIP)", "760")], s_mountArgs, s_settleArgs),
            },
            new(Verb.Unmount, "unmount", "Unmount (the server stays booted)", "unmount"),
            new(Verb.GoTo, "goto", "Navigate: a route key (+arg), an entity uri or a wavee:// deeplink; then settle", "goto uri=spotify:album:al2")
            {
                Aliases = ["go", "navigate"], Positional = ["route", "arg"], NeedsMount = true, Acts = true,
                Args = Join(s_navArgs, s_settleArgs),
            },
            new(Verb.Resize, "resize", "Resize the window to w x h DIP and settle (loop=true: through the OS modal size loop)", "resize 800 600")
            {
                Positional = ["w", "h"], NeedsMount = true,
                Args = Join([Arg("w", ArgType.Float, "window width (DIP)"), Arg("h", ArgType.Float, "window height (DIP)"),
                             Arg("loop", ArgType.Bool, "commit the size through the OS modal size loop, as crossing narrows", "false")], s_settleArgs),
            },
            new(Verb.Frames, "frames", "Run exactly n frames on the 16 ms fake clock", "frames 60")
            {
                Positional = ["n"], NeedsMount = true, Args = [Arg("n", ArgType.Int, "frames to run (0..100000)", "1")],
            },
            new(Verb.Settle, "settle", "Run frames until nothing moves (tracks, user scroll, wake reasons) for 2 frames; busy names what still moves at the cap", "settle max=900")
            {
                Positional = ["max"], NeedsMount = true, Args = [Arg("max", ArgType.Int, "the frame cap", "600")],
            },
            new(Verb.Reset, "reset", "Unmount, reset navigation to Home, clear the state vacancy and playback, activate Classic dark", "reset"),
            new(Verb.Click, "click", "Click a node's visible centre (refused unless actionable; force=true skips) or a point; then settle", "click key=zune-band-pivot:library")
            {
                Positional = ["key"], NeedsMount = true, Acts = true,
                Args = Join(s_aimArgs, [Arg("button", ArgType.Enum, "the button", "left", "left|right"),
                                        Arg("mods", ArgType.String, "modifiers held: ctrl+shift (ctrl, shift, alt, win)")], s_settleArgs),
            },
            new(Verb.Hover, "hover", "Move the pointer to a node's visible centre (refused unless actionable; force=true skips) or a point; then settle", "hover key=zune-band-pivot:home")
            {
                Positional = ["key"], NeedsMount = true, Acts = true, Args = Join(s_aimArgs, s_settleArgs),
            },
            new(Verb.Key, "key", "Press and release a key (ctrl+, shift+, alt+, win+ prefixes) repeat times; then settle", "key ctrl+f")
            {
                Positional = ["key"], NeedsMount = true, Acts = true,
                Args = Join([Arg("key", ArgType.String, "enter, esc, tab, space, backspace, delete, arrows, home, end, pageup, pagedown, apps, f1..f12, a..z, 0..9 or a VK code"),
                             Arg("repeat", ArgType.Int, "presses (1..100)", "1")], s_settleArgs),
            },
            new(Verb.Type, "type", "Type text into the focused node (text form: the rest of the line, spaces kept); then settle", "type daft punk")
            {
                Positional = ["text"], NeedsMount = true, Acts = true, Args = Join([Arg("text", ArgType.String, "the characters to type")], s_settleArgs),
            },
            new(Verb.Scroll, "scroll", "Turn the wheel dy notches at a node (refused unless actionable; force=true skips) or a point; settle waits for the scroller to stop", "scroll key=zune-home-scroll: dy=10")
            {
                Positional = ["key", "dy"], NeedsMount = true, Acts = true,
                Args = Join(s_aimArgs, [Arg("dy", ArgType.Float, "notches, positive toward the end", "3"), Arg("dx", ArgType.Float, "horizontal notches", "0")], s_settleArgs),
            },
            new(Verb.Play, "play", "Play a fake track or episode as its own context (next=N queues the tracks after it); then settle", "play spotify:track:tr3 next=4")
            {
                Positional = ["uri", "fromMs"], NeedsMount = true, Acts = true,
                Args = Join([Arg("uri", ArgType.String, "a spotify:track: or spotify:episode: uri"), Arg("fromMs", ArgType.Int, "the start position", "0"),
                             Arg("next", ArgType.Int, "queue this many tracks after a spotify:track:tr<k> (0..64)", "0"),
                             Arg("ago", ArgType.Int, "backdate the play in the play log by this many minutes", "0")], s_settleArgs),
            },
            new(Verb.Option, "option", "List the active theme's user options, or set one (id, value) the way Settings does", "option style.density Compact")
            {
                Positional = ["id", "value"], Acts = true,
                Args = Join([Arg("themeId", ArgType.String, "the theme whose options (default: the active one)", null, "", "theme"),
                             Arg("id", ArgType.String, "the option (qualified, or plain)"), Arg("value", ArgType.String, "a choice name or index, a number, true/false or #RRGGBB[AA]")], s_settleArgs),
            },
            new(Verb.Theme, "theme", "Switch the theme: mounted, the live theme transaction; unmounted, the next mount's theme", "theme zune")
            {
                Positional = ["id"], Acts = true,
                Args = Join([Arg("id", ArgType.Enum, "the theme", null, "classic|zune|winui")], s_settleArgs),
            },
            new(Verb.Find, "find", "Nodes keyed exactly key (or starting with prefix=), in reading order, with shown, onScreen and actionable", "find see-all:home:made-for-you")
            {
                Positional = ["key"], NeedsMount = true,
                Args = [Arg("key", ArgType.Key, "an exact key"), Arg("prefix", ArgType.String, "a key prefix instead")],
            },
            new(Verb.Dump, "dump", "The scene (or the subtree root names) as a flat node array: rects, paint, text metrics, flags", "dump root=zune-daylist-play depth=2")
            {
                Positional = ["root"], NeedsMount = true,
                Args =
                [
                    Arg("root", ArgType.Key, "start at every node with this exact key (default: the whole tree and the overlays)"),
                    Arg("nth", ArgType.Int, "pick one of the root's matches"),
                    Arg("depth", ArgType.Int, "how deep the walk goes (0: the root only)"),
                    Arg("keysOnly", ArgType.Bool, "keyed nodes only", "false"),
                    Arg("visibleOnly", ArgType.Bool, "skip hidden, parked, transparent and empty-box nodes", "true"),
                    Arg("text", ArgType.Bool, "write the text blocks", "true"),
                    Arg("max", ArgType.Int, "the most nodes (up to 200000)", "4000"),
                ],
            },
            new(Verb.Render, "render", "Write the offscreen frame to a PNG (an absolute path; folders are created)", "render path=C:/WAVEE/scratch/x/home.png")
            {
                Positional = ["path"], NeedsMount = true,
                Args =
                [
                    Arg("path", ArgType.Path, "the .png to write"),
                    Arg("scale", ArgType.Float, "render at this scale, then restore the mount's (0 < scale <= 4)"),
                    Arg("alpha", ArgType.Bool, "keep translucency instead of flattening over the window background", "false"),
                    Arg("max", ArgType.Int, "the settle cap after a rescale", "600"),
                ],
            },
            new(Verb.Tiers, "tiers", "The mounted window's tier facts: window, height and content tiers, sizes, rung ids", "tiers") { NeedsMount = true },
            new(Verb.Lint, "lint", "Check the mounted scene for design flaws (clip, trim, type, target, duplicate, rhythm, contrast, theme edges)", "lint min=warn allowDuplicate=\"See all\"")
            {
                Positional = ["rules"], NeedsMount = true,
                Args = Join([Arg("root", ArgType.Key, "lint every node with this exact key (default: the whole tree and the overlays)"),
                             Arg("nth", ArgType.Int, "pick one of the root's matches")], s_lintArgs),
            },
            new(Verb.Crossing, "crossing", "Resize across a tier edge the way a user does; report remounts, vanished keys, snaps and jumps; lint the result", "crossing from=1440x960 to=1440x400 theme=zune route=home gpu=none")
            {
                Args = Join(
                [
                    Arg("from", ArgType.Size, "the start size: WxH (text), {w,h} or [w,h] (JSON)"),
                    Arg("to", ArgType.Size, "the end size: WxH (text), {w,h} or [w,h] (JSON)"),
                    Arg("fromW", ArgType.Float, "the start width (instead of from)"), Arg("fromH", ArgType.Float, "the start height (instead of from)"),
                    Arg("toW", ArgType.Float, "the end width (instead of to)"), Arg("toH", ArgType.Float, "the end height (instead of to)"),
                    Arg("frames", ArgType.Int, "keep-alive paints at the new size before the commit (1..60)", "1"),
                    Arg("items", ArgType.Int, "entries per reply list", "40"),
                    Arg("max", ArgType.Int, "the settle cap", "600"),
                    Arg("remount", ArgType.Bool, "mount afresh even when a mount is up (the mount args apply)", "false"),
                ], s_mountArgs, s_lintArgs),
            },
            new(Verb.Trace, "trace", "Run N frames; per-frame engine timings, watched nodes and one scroller, plus a summary of snaps, jumps and scroll faults", "trace 90 prefix=zune- scroll=main")
            {
                Positional = ["frames"], NeedsMount = true,
                Args = Join([Arg("frames", ArgType.Int, "frames to trace (1..5000)", "60"), Arg("max", ArgType.Int, "the most nodes watched (up to 500)", "20")], s_traceOptions),
            },
            new(Verb.State, "state", "Show a page state in place of the page (empty, nomatch, error, offline...), or put the page back (none)", "state kind=empty")
            {
                Positional = ["kind"], NeedsMount = true, Acts = true,
                Args = Join([Arg("kind", ArgType.Enum, "the state", null, "empty|nomatch|error|offline|offline-rows|loading|cold-list|cold-rows|cold-faces|none")], s_settleArgs),
            },
            new(Verb.Dialog, "dialog", "Open the crash prompt (crash, hang) or Report a problem (report) over the current page; key esc closes it", "dialog kind=report")
            {
                Positional = ["kind"], NeedsMount = true, Acts = true,
                Args = Join([Arg("kind", ArgType.Enum, "the dialog", null, "crash|hang|report")], s_settleArgs),
            },
            new(Verb.Quit, "quit", "Unmount and end the server", "quit") { Aliases = ["exit"] },
        ];

        static readonly VerbSpec?[] s_byVerb = BuildByVerb();

        static VerbSpec?[] BuildByVerb()
        {
            var byVerb = new VerbSpec?[(int)Verb.Quit + 1];
            foreach (VerbSpec s in Specs) byVerb[(int)s.Verb] = s;
            return byVerb;
        }

        /// <summary>The spec of <paramref name="verb"/> (null for <see cref="Verb.None"/>).</summary>
        public static VerbSpec? SpecOf(Verb verb) => (uint)verb < (uint)s_byVerb.Length ? s_byVerb[(int)verb] : null;

        /// <summary>Every verb's name, in the order `help` lists them.</summary>
        public static readonly string[] VerbNames = Array.ConvertAll(Specs, static s => s.Name);

        public static Verb VerbOf(string word) => word.ToLowerInvariant() switch
        {
            "help" => Verb.Help, "status" or "ping" => Verb.Status, "mount" => Verb.Mount, "unmount" => Verb.Unmount,
            "goto" or "go" or "navigate" => Verb.GoTo, "resize" => Verb.Resize, "frames" => Verb.Frames, "settle" => Verb.Settle,
            "reset" => Verb.Reset, "click" => Verb.Click, "hover" => Verb.Hover, "key" => Verb.Key, "type" => Verb.Type,
            "scroll" => Verb.Scroll, "play" => Verb.Play, "option" => Verb.Option, "theme" => Verb.Theme, "find" => Verb.Find,
            "dump" => Verb.Dump, "render" => Verb.Render, "tiers" => Verb.Tiers, "lint" => Verb.Lint, "crossing" => Verb.Crossing, "trace" => Verb.Trace, "state" => Verb.State, "dialog" => Verb.Dialog,
            "quit" or "exit" => Verb.Quit,
            _ => Verb.None,
        };

        /// <summary>The names a verb's bare (positional) words fill, in order: `goto home` is `goto route=home`.</summary>
        public static ReadOnlySpan<string> PositionalNames(Verb verb) => SpecOf(verb)?.Positional ?? default(ReadOnlySpan<string>);

        // ── the strict-arg check ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every arg of <paramref name="cmd"/> must be one its verb declares (exact case). An alias is rewritten to its
        /// canonical name. False with "unknown arg 'rule' for lint (did you mean 'rules'?)" otherwise.</summary>
        public static bool Validate(in Command cmd, out string error)
        {
            error = "";
            if (SpecOf(cmd.Verb) is not { } spec) return true;
            List<(string From, string To)>? renames = null;
            foreach (string name in cmd.Args.Names)
            {
                if (TryDeclared(spec, name, out string canonical))
                {
                    if (!ReferenceEquals(canonical, name) && canonical != name) (renames ??= []).Add((name, canonical));
                    continue;
                }
                error = "unknown arg '" + name + "' for " + spec.Name + (spec.Args.Length == 0
                    ? " (" + spec.Name + " takes no args)"
                    : Suggest(spec, name) is { } near ? " (did you mean '" + near + "'?)" : " (try help verb=" + spec.Name + ")");
                return false;
            }
            if (renames is not null)
                foreach (var (from, to) in renames)
                {
                    if (cmd.Args.Has(to)) { error = "'" + from + "' and '" + to + "' are the same arg of " + spec.Name; return false; }
                    if (cmd.Args.Remove(from, out Value v)) cmd.Args.Set(to, v);
                }
            return true;
        }

        static bool TryDeclared(VerbSpec spec, string name, out string canonical)
        {
            foreach (ArgSpec a in spec.Args)
            {
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) { canonical = a.Name; return true; }
                foreach (string alias in a.Aliases)
                    if (string.Equals(alias, name, StringComparison.Ordinal)) { canonical = a.Name; return true; }
            }
            canonical = "";
            return false;
        }

        /// <summary>The declared arg <paramref name="name"/> was probably meant as: the same letters in another case, else the
        /// nearest name within an edit distance of 2 (1 for names under 5 letters); null when nothing is close.</summary>
        static string? Suggest(VerbSpec spec, string name)
        {
            foreach (ArgSpec a in spec.Args)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a.Name;
            string? best = null;
            int bestDistance = name.Length < 5 ? 1 : 2;
            foreach (ArgSpec a in spec.Args)
            {
                int d = EditDistance(name.ToLowerInvariant(), a.Name.ToLowerInvariant());
                if (d < bestDistance || (d == bestDistance && best is null)) { best = a.Name; bestDistance = d; }
            }
            return best;
        }

        /// <summary>Levenshtein distance (inserts, deletes and substitutions cost 1).</summary>
        public static int EditDistance(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) row[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        // ── help ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The spec a `help verb=` word names (a verb or an alias, any case), or null.</summary>
        public static VerbSpec? SpecNamed(string word) => SpecOf(VerbOf(word));

        /// <summary>`help`: `verbs` (the names, as the ready line lists them), `doc`, one-line `summaries` by name and how to ask for more.</summary>
        public static void WriteHelpIndex(Utf8JsonWriter w)
        {
            w.WriteStartArray("verbs");
            foreach (string v in VerbNames) w.WriteStringValue(v);
            w.WriteEndArray();
            w.WriteString("doc", "docs/guide/agent-inspect.md");
            w.WriteStartObject("summaries");
            foreach (VerbSpec s in Specs) w.WriteString(s.Name, s.Summary);
            w.WriteEndObject();
            w.WriteString("more", "help verb=<name>: its args, defaults, choices and an example; help schema=json: the whole table");
        }

        /// <summary>`help verb=<name>`: the verb's whole row into the open object.</summary>
        public static void WriteVerb(Utf8JsonWriter w, VerbSpec s)
        {
            w.WriteString("verb", s.Name);
            if (s.Aliases.Length > 0) WriteStrings(w, "aliases", s.Aliases);
            w.WriteString("summary", s.Summary);
            w.WriteString("example", s.Example);
            WriteStrings(w, "positional", s.Positional);
            w.WriteBoolean("needsMount", s.NeedsMount);
            w.WriteBoolean("acts", s.Acts);
            w.WriteStartArray("args");
            foreach (ArgSpec a in s.Args)
            {
                w.WriteStartObject();
                w.WriteString("name", a.Name);
                w.WriteString("type", TypeName(a.Type));
                if (a.Default is { } d) w.WriteString("default", d);
                if (a.Choices.Length > 0) WriteStrings(w, "choices", a.Choices);
                if (a.Aliases.Length > 0) WriteStrings(w, "aliases", a.Aliases);
                w.WriteString("doc", a.Doc);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>`help schema=json`: `schema {version, grammar, verbs:[every verb's row]}`.</summary>
        public static void WriteSchema(Utf8JsonWriter w)
        {
            w.WriteStartObject("schema");
            w.WriteNumber("version", 1);
            w.WriteString("grammar", "one command per line: {\"cmd\",\"args\",\"id\"} or `verb k=v k=\"a b\" k=[a,\"b c\"] bare`; bare words fill `positional` in order; arg names are case-sensitive and an undeclared one is refused");
            w.WriteStartArray("verbs");
            foreach (VerbSpec s in Specs)
            {
                w.WriteStartObject();
                WriteVerb(w, s);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteStrings(Utf8JsonWriter w, string name, string[] values)
        {
            w.WriteStartArray(name);
            foreach (string v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        static string TypeName(ArgType t) => t switch
        {
            ArgType.Int => "int", ArgType.Float => "number", ArgType.Bool => "bool", ArgType.Size => "size", ArgType.Key => "key",
            ArgType.Path => "path", ArgType.Enum => "enum", ArgType.List => "list", _ => "string",
        };

        // ── values and args ─────────────────────────────────────────────────────────────────────────────────────────

        public enum ValueKind : byte { Null, String, Number, Bool, Json }

        /// <summary>One argument value. <see cref="ValueKind.Json"/> keeps a nested object or array as its raw text.</summary>
        public readonly record struct Value(ValueKind Kind, string Str = "", double Num = 0, bool Bool = false)
        {
            public override string ToString() => Kind switch
            {
                ValueKind.String or ValueKind.Json => Str,
                ValueKind.Number => Num.ToString(CultureInfo.InvariantCulture),
                ValueKind.Bool => Bool ? "true" : "false",
                _ => "null",
            };

            /// <summary>A text-line word: a number, true/false, null, or a string.</summary>
            public static Value FromWord(string word)
            {
                if (word == "true") return new Value(ValueKind.Bool, Bool: true);
                if (word == "false") return new Value(ValueKind.Bool, Bool: false);
                if (word == "null") return new Value(ValueKind.Null);
                if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
                    return new Value(ValueKind.Number, Num: d);
                return new Value(ValueKind.String, word);
            }
        }

        /// <summary>A command's named arguments (case-sensitive names).</summary>
        public sealed class Args
        {
            readonly Dictionary<string, Value> _values = new(StringComparer.Ordinal);

            public static readonly Args Empty = new();

            public int Count => _values.Count;
            public IEnumerable<string> Names => _values.Keys;

            public void Set(string name, Value value) => _values[name] = value;
            public bool Remove(string name, out Value value) => _values.Remove(name, out value);
            public bool Has(string name) => _values.TryGetValue(name, out Value v) && v.Kind != ValueKind.Null;
            public bool TryGet(string name, out Value value) => _values.TryGetValue(name, out value) && value.Kind != ValueKind.Null;

            public string Str(string name, string fallback = "")
                => TryGet(name, out Value v) ? v.ToString() : fallback;

            public bool TryNum(string name, out double number)
            {
                number = 0;
                if (!TryGet(name, out Value v)) return false;
                if (v.Kind == ValueKind.Number) { number = v.Num; return true; }
                return v.Kind == ValueKind.String
                    && double.TryParse(v.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
            }

            public float Float(string name, float fallback) => TryNum(name, out double d) ? (float)d : fallback;
            public int Int(string name, int fallback) => TryNum(name, out double d) ? (int)Math.Round(d) : fallback;

            /// <summary>A list arg as strings: a JSON array (`[3,3,-6]`, `["a","b c"]`; numbers in their invariant text) or a
            /// comma-separated word (`3,3,-6`). Empty when absent.</summary>
            public List<string> List(string name)
            {
                var list = new List<string>();
                if (!TryGet(name, out Value v)) return list;
                if (v.Kind == ValueKind.Json)
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(v.Str);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                        foreach (JsonElement e in doc.RootElement.EnumerateArray())
                        {
                            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) list.Add(s);
                            else if (e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) list.Add(e.GetRawText());
                        }
                    }
                    catch (JsonException) { }
                    return list;
                }
                foreach (string part in v.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) list.Add(part);
                return list;
            }

            public bool Bool(string name, bool fallback)
            {
                if (!TryGet(name, out Value v)) return fallback;
                return v.Kind switch
                {
                    ValueKind.Bool => v.Bool,
                    ValueKind.Number => v.Num != 0,
                    ValueKind.String => v.Str is "true" or "on" or "yes" or "1" || (v.Str is not ("false" or "off" or "no" or "0") && fallback),
                    _ => fallback,
                };
            }
        }

        /// <summary>One parsed line. <see cref="Name"/> is the verb as written (echoed on the reply).</summary>
        public readonly record struct Command(Verb Verb, string Name, Args Args, int Id = -1)
        {
            public bool IsNone => Verb == Verb.None;
        }

        // ── the grammar ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Parse one line. Never throws. Blank and `#` lines are (true, Verb.None). A failed JSON line still carries
        /// its id, so the reply can name it.</summary>
        public static bool TryParse(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = new Command(Verb.None, "", Args.Empty);
            error = "";
            line = line.Trim();
            if (!line.IsEmpty && line[0] == '﻿') line = line[1..].TrimStart();
            if (line.IsEmpty || line[0] == '#') return true;
            return line[0] is '{' or '[' ? TryParseJson(line, out cmd, out error) : TryParseText(line, out cmd, out error);
        }

        static bool TryParseJson(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = new Command(Verb.None, "", Args.Empty);
            error = "";
            try
            {
                using var doc = JsonDocument.Parse(line.ToString());
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "a JSON command is an object"; return false; }
                int id = -1;
                if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt32(out int parsedId)) id = parsedId;
                cmd = new Command(Verb.None, "", Args.Empty, id);
                if (!root.TryGetProperty("cmd", out JsonElement verbElement) || verbElement.ValueKind != JsonValueKind.String)
                { error = "a JSON command needs a \"cmd\" string"; return false; }
                string name = verbElement.GetString() ?? "";
                Verb verb = VerbOf(name);
                cmd = new Command(Verb.None, name, Args.Empty, id);
                if (verb == Verb.None) { error = "unknown command '" + name + "' (try help)"; return false; }

                var args = new Args();
                foreach (JsonProperty p in root.EnumerateObject())
                {
                    if (p.NameEquals("cmd") || p.NameEquals("id")) continue;
                    if (p.NameEquals("args"))
                    {
                        if (p.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty a in p.Value.EnumerateObject()) args.Set(a.Name, ValueOf(a.Value));
                            continue;
                        }
                        if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            ReadOnlySpan<string> names = PositionalNames(verb);
                            int i = 0;
                            foreach (JsonElement a in p.Value.EnumerateArray())
                            {
                                if (i >= names.Length) { error = "too many positional args for '" + name + "'"; return false; }
                                args.Set(names[i++], ValueOf(a));
                            }
                            continue;
                        }
                        if (p.Value.ValueKind == JsonValueKind.Null) continue;
                        error = "\"args\" must be an object or an array";
                        return false;
                    }
                    args.Set(p.Name, ValueOf(p.Value));
                }
                cmd = new Command(verb, name, args, id);
                return true;
            }
            catch (JsonException ex) { error = "bad JSON: " + ex.Message; return false; }
        }

        static Value ValueOf(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => new Value(ValueKind.String, e.GetString() ?? ""),
            JsonValueKind.Number => new Value(ValueKind.Number, Num: e.GetDouble()),
            JsonValueKind.True => new Value(ValueKind.Bool, Bool: true),
            JsonValueKind.False => new Value(ValueKind.Bool, Bool: false),
            JsonValueKind.Object or JsonValueKind.Array => new Value(ValueKind.Json, e.GetRawText()),
            _ => new Value(ValueKind.Null),
        };

        /// <summary>`verb a=1 b=two k="a b" l=[x,"y z",3] bare words`: named `k=v` pairs, and bare words filling the verb's
        /// positional names. A value in double quotes keeps its blanks and is always a string (`\"` and `\\` escape; any other
        /// backslash stays, so Windows paths need nothing). A value in brackets is a list, kept as a JSON array exactly as the
        /// JSON form would carry it (unquoted numbers, true and false as JSON literals, everything else a string). The `type`
        /// verb takes the whole rest of the line as its text (spaces kept).</summary>
        static bool TryParseText(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = new Command(Verb.None, "", Args.Empty);
            error = "";
            int sp = line.IndexOfAny(' ', '\t');
            string name = (sp < 0 ? line : line[..sp]).ToString();
            Verb verb = VerbOf(name);
            cmd = new Command(Verb.None, name, Args.Empty);   // a failure below still names its verb on the reply
            if (verb == Verb.None) { error = "unknown command '" + name + "' (try help)"; return false; }
            ReadOnlySpan<char> rest = sp < 0 ? default : line[(sp + 1)..].Trim();
            var args = new Args();
            if (verb == Verb.Type && !rest.IsEmpty && !rest.StartsWith("text=", StringComparison.Ordinal))
            {
                args.Set("text", new Value(ValueKind.String, rest.ToString()));
                cmd = new Command(verb, name, args);
                return true;
            }
            ReadOnlySpan<string> names = PositionalNames(verb);
            int positional = 0, i = 0;
            while (true)
            {
                while (i < rest.Length && IsBlank(rest[i])) i++;
                if (i >= rest.Length) break;
                int start = i, j = i;
                while (j < rest.Length && !IsBlank(rest[j]) && rest[j] is not ('=' or '"' or '[')) j++;
                string? argName = j > i && j < rest.Length && rest[j] == '=' ? rest[i..j].ToString() : null;
                if (argName is not null) i = j + 1;
                if (!TryValue(rest, ref i, out Value value, out error)) { error += " in '" + rest[start..].ToString() + "'"; return false; }
                if (i < rest.Length && !IsBlank(rest[i])) { error = "'" + rest[start..].ToString() + "': a quoted value or a list ends at a blank"; return false; }
                if (argName is not null) { args.Set(argName, value); continue; }
                if (positional >= names.Length) { error = "unexpected '" + rest[start..i].ToString() + "' after " + name; return false; }
                args.Set(names[positional++], value);
            }
            cmd = new Command(verb, name, args);
            return true;
        }

        static bool IsBlank(char c) => c is ' ' or '\t';

        /// <summary>One value at <paramref name="i"/>: a quoted string, a bracketed list, or a bare word up to the next blank.</summary>
        static bool TryValue(ReadOnlySpan<char> s, ref int i, out Value value, out string error)
        {
            error = "";
            value = default;
            if (i < s.Length && s[i] == '"')
            {
                if (!TryQuoted(s, ref i, out string text, out error)) return false;
                value = new Value(ValueKind.String, text);
                return true;
            }
            if (i < s.Length && s[i] == '[') return TryList(s, ref i, out value, out error);
            int start = i;
            while (i < s.Length && !IsBlank(s[i])) i++;
            value = Value.FromWord(s[start..i].ToString());
            return true;
        }

        /// <summary>`"..."` at <paramref name="i"/> (on the opening quote); <paramref name="i"/> ends past the closing one.</summary>
        static bool TryQuoted(ReadOnlySpan<char> s, ref int i, out string text, out string error)
        {
            error = "";
            var sb = new StringBuilder();
            for (int k = i + 1; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '\\' && k + 1 < s.Length && s[k + 1] is '"' or '\\') { sb.Append(s[++k]); continue; }
                if (c == '"') { i = k + 1; text = sb.ToString(); return true; }
                sb.Append(c);
            }
            text = "";
            error = "unterminated quote";
            return false;
        }

        /// <summary>`[a, "b c", 3]` at <paramref name="i"/> (on the bracket), as a JSON array value; <paramref name="i"/> ends past `]`.</summary>
        static bool TryList(ReadOnlySpan<char> s, ref int i, out Value value, out string error)
        {
            error = "";
            value = default;
            var buffer = new ArrayBufferWriter<byte>(64);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartArray();
                int k = i + 1;
                while (true)
                {
                    while (k < s.Length && IsBlank(s[k])) k++;
                    if (k >= s.Length) { error = "unterminated list (no ])"; return false; }
                    if (s[k] == ']') { k++; break; }
                    if (s[k] == ',') { k++; continue; }
                    if (s[k] == '[') { error = "a list holds words, not lists"; return false; }
                    if (s[k] == '"')
                    {
                        if (!TryQuoted(s, ref k, out string text, out error)) return false;
                        w.WriteStringValue(text);
                        continue;
                    }
                    int start = k;
                    while (k < s.Length && s[k] is not (',' or ']') && !IsBlank(s[k])) k++;
                    string word = s[start..k].ToString();
                    if (word is "true" or "false") w.WriteBooleanValue(word == "true");
                    else if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) w.WriteNumberValue(d);
                    else w.WriteStringValue(word);
                }
                w.WriteEndArray();
                i = k;
            }
            value = new Value(ValueKind.Json, Encoding.UTF8.GetString(buffer.WrittenSpan));
            return true;
        }

        // ── key names ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`enter`, `esc`, `tab`, `space`, `backspace`, `delete`, `left|up|right|down`, `home`, `end`, `pageup`,
        /// `pagedown`, `f1`..`f12`, `a`..`z`, `0`..`9`, or a decimal virtual-key code; with `ctrl+`, `shift+`, `alt+`, `win+`
        /// prefixes (`ctrl+shift+f`).</summary>
        public static bool TryKey(string spec, out int vk, out KeyModifiers mods)
        {
            vk = 0; mods = KeyModifiers.None;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            string[] parts = spec.Trim().ToLowerInvariant().Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i])
                {
                    case "ctrl" or "control": mods |= KeyModifiers.Ctrl; break;
                    case "shift": mods |= KeyModifiers.Shift; break;
                    case "alt": mods |= KeyModifiers.Alt; break;
                    case "win" or "meta": mods |= KeyModifiers.Win; break;
                    default: return false;
                }
            }
            string k = parts[^1];
            vk = k switch
            {
                "enter" or "return" => 0x0D, "esc" or "escape" => 0x1B, "tab" => 0x09, "space" => 0x20, "backspace" => 0x08,
                "delete" or "del" => 0x2E, "insert" => 0x2D, "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
                "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22, "apps" or "menu" => 0x5D,
                _ => 0,
            };
            if (vk != 0) return true;
            if (k.Length == 1 && char.IsAsciiLetterLower(k[0])) { vk = 'A' + (k[0] - 'a'); return true; }
            if (k.Length == 1 && char.IsAsciiDigit(k[0])) { vk = k[0]; return true; }
            if (k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 12)
            { vk = 0x70 + f - 1; return true; }
            if (int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code is > 0 and < 256) { vk = code; return true; }
            return false;
        }

        // ── the reply writer ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The JSON-lines writer: one reusable Utf8JsonWriter per thread, no DOM, no reflection (the headless
        /// `JsonLine` pattern).</summary>
        public static class Reply
        {
            static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            [ThreadStatic] static ArrayBufferWriter<byte>? t_buffer;
            [ThreadStatic] static Utf8JsonWriter? t_writer;

            static Utf8JsonWriter Begin()
            {
                ArrayBufferWriter<byte> buffer = t_buffer ??= new ArrayBufferWriter<byte>(4096);
                buffer.ResetWrittenCount();
                Utf8JsonWriter w = t_writer ??= new Utf8JsonWriter(buffer, WriterOptions);
                w.Reset(buffer);
                w.WriteStartObject();
                return w;
            }

            static string End(Utf8JsonWriter w)
            {
                w.WriteEndObject();
                w.Flush();
                return Encoding.UTF8.GetString(t_buffer!.WrittenSpan);
            }

            /// <summary>A success: `{"id","ok":true,"cmd",...body}`.</summary>
            public static string Ok(int id, string cmd, Action<Utf8JsonWriter>? body = null)
            {
                var w = Begin();
                w.WriteNumber("id", id);
                w.WriteBoolean("ok", true);
                w.WriteString("cmd", cmd ?? "");
                body?.Invoke(w);
                return End(w);
            }

            /// <summary>A failure: `{"id","ok":false,"cmd","error",...body}`.</summary>
            public static string Fail(int id, string cmd, string error, Action<Utf8JsonWriter>? body = null)
            {
                var w = Begin();
                w.WriteNumber("id", id);
                w.WriteBoolean("ok", false);
                w.WriteString("cmd", cmd ?? "");
                w.WriteString("error", error ?? "");
                body?.Invoke(w);
                return End(w);
            }

            /// <summary>An unsolicited event line: `{"kind",...body}` (`ready`, `bye`, `fault`).</summary>
            public static string Event(string kind, Action<Utf8JsonWriter>? body = null)
            {
                var w = Begin();
                w.WriteString("kind", kind);
                body?.Invoke(w);
                return End(w);
            }

            /// <summary>A finite number rounded to 0.01 (NaN and infinities write 0: JSON has neither).</summary>
            public static void Num(Utf8JsonWriter w, string name, float v)
                => w.WriteNumber(name, float.IsFinite(v) ? Math.Round(v, 2) : 0d);

            /// <summary>`{"x","y","w","h"}`.</summary>
            public static void Rect(Utf8JsonWriter w, string name, in RectF r)
            {
                w.WriteStartObject(name);
                Num(w, "x", r.X);
                Num(w, "y", r.Y);
                Num(w, "w", r.W);
                Num(w, "h", r.H);
                w.WriteEndObject();
            }

            /// <summary>`#RRGGBBAA` (sRGB bytes).</summary>
            public static string Hex(in ColorF c)
            {
                static int B(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
                return string.Create(CultureInfo.InvariantCulture, $"#{B(c.R):X2}{B(c.G):X2}{B(c.B):X2}{B(c.A):X2}");
            }
        }
    }
}
