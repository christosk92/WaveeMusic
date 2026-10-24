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
            Click, Hover, Key, Type, Scroll, Play, Option, Theme, Find, Dump, Render, Tiers, Lint, Crossing, Trace, State, Dialog,
            Views, Crossings, Setting, Drag, Skeleton, Quit,
        }

        // ── the verb table ──────────────────────────────────────────────────────────────────────────────────────────
        // Every verb and every arg it reads, declared once: the strict-arg check (`Validate`), `help` and `help schema=json` all
        // read this table, so an arg the code reads but the table lacks is refused at the door (add it here when you add it to a
        // verb). Names are case-sensitive; an alias is rewritten to its canonical name before the verb runs.

        /// <summary>What an arg holds. `Validate` checks the value against it (an int, a number, true/false, one of an enum's or a
        /// list's choices) and writes it back in canonical form; the verbs then parse the values themselves.</summary>
        public enum ArgType : byte { String, Int, Float, Bool, Size, Key, Path, Enum, List }

        /// <summary>One declared arg: its name, type, default (null: none), choices (an enum's or a list's words), aliases, a one-line
        /// doc, <see cref="Requires"/>: the arg it only applies beside (a trace option needs `trace`, a video option `video`),
        /// <see cref="Excludes"/>: the arg it cannot stand beside (a point's `x` and a `key`), and an int's or a number's range.</summary>
        public sealed record ArgSpec(string Name, ArgType Type, string Doc)
        {
            public string? Default { get; init; }
            public string[] Choices { get; init; } = [];
            public string[] Aliases { get; init; } = [];
            public string? Requires { get; init; }
            public string? Excludes { get; init; }
            /// <summary>The least value an int or a number takes (inclusive), or null.</summary>
            public double? Min { get; init; }
            /// <summary>The greatest value an int or a number takes (inclusive), or null.</summary>
            public double? Max { get; init; }
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
            /// <summary>The arg that lifts <see cref="NeedsMount"/> (lint's replay= reads a saved snapshot with nothing mounted).</summary>
            public string? MountUnless { get; init; }
            /// <summary>Replies `after:{route, focused, overlays, hit}`.</summary>
            public bool Acts { get; init; }
        }

        /// <summary>An arg row: <paramref name="choices"/> and <paramref name="aliases"/> are `|`-separated.</summary>
        static ArgSpec Arg(string name, ArgType type, string doc, string? def = null, string choices = "", string aliases = "", string? requires = null,
                           string? excludes = null) => new(name, type, doc)
        {
            Default = def,
            Choices = choices.Length == 0 ? [] : choices.Split('|'),
            Aliases = aliases.Length == 0 ? [] : aliases.Split('|'),
            Requires = requires,
            Excludes = excludes,
        };

        /// <summary>An int row taking <paramref name="min"/>..<paramref name="max"/> (a value outside is refused, not clamped).</summary>
        static ArgSpec IntArg(string name, string doc, string? def, double min, double max) => Arg(name, ArgType.Int, doc, def) with { Min = min, Max = max };

        /// <summary>The theme ids as an enum's choices: App.ShippedThemeIds, the list the verbs' own check (InspectKit.IsShipped) reads.</summary>
        static string ThemeChoices => string.Join("|", App.ShippedThemeIds);

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
            Arg("instant", ArgType.Float, "a run of tracked moves whose largest frame moves this many DIP and most of the run landed at once", "4"),
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
            Arg("drag", ArgType.List, "a mouse drag: DIP per frame along x while the button is down, cycled"),
            Arg("dragFrames", ArgType.Int, "frames the pointer moves with the button down", "8"),
            Arg("dragStart", ArgType.Int, "the frame the button goes down on", "0"),
            Arg("dragKey", ArgType.Key, "press on this node's centre (or dragX, dragY)"),
            Arg("dragX", ArgType.Float, "press at this x (window DIP)"),
            Arg("dragY", ArgType.Float, "press at this y (window DIP)"),
            Arg("dragDy", ArgType.Float, "DIP per frame down while dragging", "0"),
        ];

        /// <summary>A settling verb's own args, plus the trace options, which only apply beside `trace=N` with N > 0 (anywhere else
        /// they would do nothing, so they are refused).</summary>
        static readonly ArgSpec[] s_settleArgs = Join(
        [
            Arg("settle", ArgType.Bool, "false: reply at once (frames:0, settled:false) and step with frames yourself", "true"),
            Arg("max", ArgType.Int, "the settle's frame cap (with trace=N: the most nodes watched)", "600"),
            Arg("loops", ArgType.Enum, "looping tracks: ignore (they do not hold the settle, but a loading indicator does: a skeleton's shimmer, a progress bar or ring), wait (every loop holds it), skeletons (none holds it, so it can end on a skeleton or a spinner)", "ignore", "ignore|wait|skeletons"),
            Arg("trace", ArgType.Int, "trace N frames from the action instead of settling (the trace options apply)"),
        ], Array.ConvertAll(s_traceOptions, static a => a with { Requires = "trace" }));

        /// <summary>`theme` and `option`: one `max` caps the transaction (the apply) and, when given, the settle after it.</summary>
        static readonly ArgSpec[] s_transactionMax =
        [
            Arg("max", ArgType.Int, "the frame cap of the theme transaction (option: the apply), and of the settle after it when given (else 600)", "900"),
        ];

        /// <summary>Where `goto` (and `mount`, `crossing`) navigate: deeplink wins over uri, uri over route.</summary>
        static readonly ArgSpec[] s_navArgs =
        [
            Arg("route", ArgType.String, "a route key: home, browse, search (+arg), liked, settings, album:<uri>..."),
            Arg("arg", ArgType.String, "the route's argument (search: the query)"),
            Arg("uri", ArgType.String, "an entity uri: its page (spotify:album:al2)"),
            Arg("deeplink", ArgType.String, "a wavee:// payload through the OS door (never validated)"),
        ];

        /// <summary>The mount's own args, without the navigation (`mount`, and `crossing` only when it mounts).</summary>
        static readonly ArgSpec[] s_mountOwnArgs =
        [
            Arg("scale", ArgType.Float, "DPI scale, 0 < scale <= 4", "1"),
            Arg("theme", ArgType.Enum, "the theme (default: the active one; a fresh server is classic)", null, ThemeChoices),
            Arg("kind", ArgType.Enum, "light or dark (default: the current kind)", null, "dark|light"),
            Arg("gpu", ArgType.Enum, "hardware (the default: the hardware adapter, WARP when the machine has none) or warp (WARP forced): the offscreen D3D12 surface (render works); none: no pixels", "hardware", "hardware|warp|none"),
            Arg("keepRoute", ArgType.Bool, "keep the current navigation (and tabs) instead of a fresh Home", "false"),
            Arg("setup", ArgType.Enum, "mount a first run: the setup wizard's plate (signin: ask for the sign-in page)", null, "true|false|signin"),
            Arg("feedback", ArgType.Enum, "open the report form over the page", null, "bug|feature"),
            Arg("video", ArgType.Enum, "put the video surface up with a fake track playing", null, "fullscreen|docked|floating|pip"),
            Arg("player", ArgType.Enum, "bind an idle engine player to the video surface", null, "fake", requires: "video"),
            Arg("chrome", ArgType.Enum, "shown: the chrome at rest with no auto-hide (Zune's hover band, the video presenter's transport, the fullscreen stage's controls)", null, "shown|hidden"),
            Arg("join", ArgType.Enum, "the video join poster's state: poster (the artwork alone: a player bound, no picture yet), working (the ring and Loading..., the default offline), failed (the fault line)", null, "poster|working|failed", requires: "video"),
            Arg("fromMs", ArgType.Int, "the fake video track's position", "74000", requires: "video"),
            Arg("stage", ArgType.Enum, "the fullscreen stage's mode (key f11 opens it)", null, "lyrics|visualizer|queue|artist"),
            Arg("layout", ArgType.Enum, "the fullscreen stage's layout", null, "card|large|centered|artist"),
            Arg("gallery", ArgType.Enum, "the fullscreen stage's gallery", null, "on|off|true|false"),
            Arg("heroLyrics", ArgType.Enum, "the Artist layout's lyrics over the photo", null, "on|off|true|false"),
            Arg("grade", ArgType.Enum, "cover grading: a hue per uri, all grey, or the playing one grey", null, "hue|grey|gray|greynow"),
            Arg("images", ArgType.Enum, "fake: a gradient per image; real: the app's own decoder paints the bundled photographs", "fake", "fake|real"),
            Arg("marquee", ArgType.Enum, "the app's marquee setting (Settings > Appearance), set before the mount", null, "on|off|true|false"),
            Arg("remote", ArgType.String, "the play bar plays on a remote output of this name (--fake has no Connect cluster)"),
            Arg("panelW", ArgType.Float, "the side panel's stored width (DIP; clamped to the theme's drag range)"),
        ];

        /// <summary>The mount's args: its own and the navigation.</summary>
        static readonly ArgSpec[] s_mountArgs = Join(s_mountOwnArgs, s_navArgs);

        /// <summary>True for a lint option `crossing` takes (`s_lintArgs`): `crossings` passes them on to each of its crossings.</summary>
        public static bool IsCrossingLintArg(string name)
        {
            foreach (ArgSpec a in s_lintArgs)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>True for an arg only a mount reads (not the navigation): a `crossing` over a mount that is already up refuses them.</summary>
        public static bool IsMountOnly(string name)
        {
            foreach (ArgSpec a in s_mountOwnArgs)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── the lint args: ONE LINE PER ARG. `s_lintArgs` are the options Diagnostics.Inspect.Lint.Scene.cs `TryOptions` reads, which
        // `lint` and `crossing` (its lint of the settled scene) both take; `s_lintOnlyArgs` are the `lint` verb's own (a file it saves or
        // replays), which `crossing` refuses. With root and nth, the two lists are InspectLintScene.ArgNames. ──
        static readonly ArgSpec[] s_lintOnlyArgs =
        [
            Arg("save", ArgType.Path, "write the snapshot the rules saw to this JSON file"),
            Arg("replay", ArgType.Path, "lint a saved snapshot instead of the mount (needs no mount)"),
            Arg("frame", ArgType.Path, "with replay: a PNG render of the same frame, the pixels the contrast and picture rules read"),
        ];
        static readonly ArgSpec[] s_lintArgs =
        [
            Arg("rules", ArgType.List, "the rules to run: ids (text.clip), today's names (clip), families (align or align.*) or all (default: all)", null, string.Join("|", InspectLint.RuleWords)),
            Arg("scope", ArgType.Enum, "screen: nodes at least half on screen; content: below the fold too; all: every node", "screen", "screen|content|all"),
            Arg("min", ArgType.Enum, "leave out findings below this severity", "info", "info|warn|warning|error"),
            Arg("limit", ArgType.Int, "the most findings one rule lists", "100"),
            Arg("outcomes", ArgType.List, "the outcomes LISTED (every one is counted)", "fail,review,report", "fail|review|suppressed|report|all"),
            Arg("baseline", ArgType.Path, "accepted findings: a JSON-lines file of {fp, rule, reason}; a match is suppressed"),
            Arg("typeFloor", ArgType.Float, "type: text under this size (DIP) is an error", "11"),
            Arg("targetFloor", ArgType.Float, "target: the smallest hit target outside Compact (DIP)", "32"),
            Arg("targetFloorCompact", ArgType.Float, "target: the smallest hit target at Compact (DIP)", "40"),
            Arg("gapMin", ArgType.Float, "rhythm: stacked text blocks closer than this are cramped (DIP)", "2"),
            Arg("allowTrim", ArgType.List, "trim: keys or key prefixes whose text may be trimmed"),
            Arg("allowDuplicate", ArgType.List, "duplicate: labels that may appear twice"),
            Arg("ignoreUnder", ArgType.List, "duplicate: key prefixes it does not look inside", "shell.titlebar,shell.playerbar"),
            Arg("moving", ArgType.Enum, "review: a node in an animating subtree is a review (\"lint again once it settles\"); judge: this frame is the verdict (looping text: lyrics, stage captions)", "review", "review|judge"),
        ];
        // ── end of the lint args ──

        /// <summary>A pointer verb's target: a key (refused unless actionable) or a point.</summary>
        static readonly ArgSpec[] s_aimArgs =
        [
            Arg("key", ArgType.Key, "the node (exact key); refused unless shown, >= 50% visible, enabled and hit at its visible centre"),
            Arg("nth", ArgType.Int, "which actionable match, in reading order (top, then left)", "0", requires: "key") with { Min = 0 },
            Arg("x", ArgType.Float, "a point instead of a key (window DIP; no checks)", excludes: "key"),
            Arg("y", ArgType.Float, "a point instead of a key (window DIP; no checks)", excludes: "key"),
            Arg("force", ArgType.Bool, "skip the actionability checks (nth then counts every match)", "false", requires: "key"),
        ];

        /// <summary>`click` and `hover` only: how long a key target's rect may take to hold still (Playwright's "stable": the same presented
        /// rect on two consecutive frames) before the verb refuses it as `unstable`. The wheel (`scroll`) needs no still target.</summary>
        static readonly ArgSpec[] s_stableArgs =
        [
            Arg("stableMax", ArgType.Int, "the most frames to wait for the target's rect to hold still for 2 frames in a row (0: one look, no wait)", "30", requires: "key") with { Min = 0, Max = 600 },
        ];

        /// <summary>The words `state kind=` takes: the ones Shell.SetInspectState (Shell.UI.cs) switches on, which answers false for any
        /// other (the verb then names these). Declared before <see cref="Specs"/>, which reads it.</summary>
        public static readonly string[] StateKinds = ["empty", "nomatch", "error", "offline", "offline-rows", "loading", "cold-list", "cold-rows", "cold-warm", "cold-faces", "none"];

        /// <summary>Every verb, in the order `help` lists them.</summary>
        public static readonly VerbSpec[] Specs =
        [
            new(Verb.Help, "help", "List the verbs; help verb=<name> gives one verb's args, defaults, choices and example; help schema=json the whole table", "help verb=lint")
            {
                Positional = ["verb"],
                Args = [Arg("verb", ArgType.String, "the verb to describe (an alias works too)"), Arg("schema", ArgType.Enum, "the whole table as JSON", null, "json", excludes: "verb")],
            },
            new(Verb.Status, "status", "The session: mounted, frames, mounts so far, theme, kind, route, text backend; the mount facts and the video surface when mounted", "status") { Aliases = ["ping"] },
            new(Verb.Mount, "mount", "Mount the whole app at w x h DIP (theme, kind, route...); replaces any mount and puts back what the last one changed: state vacancy, cold playlist, stage, tabs, playback, queue, video surface, theme options", "mount w=400 h=860 theme=zune kind=dark route=home")
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
                Positional = ["n"], NeedsMount = true, Args = [IntArg("n", "frames to run", "1", 0, 100_000)],
            },
            new(Verb.Settle, "settle", "Run frames until nothing moves (tracks, user scroll, wake reasons) for 2 frames; busy names what still moves at the cap", "settle max=900")
            {
                Positional = ["max"], NeedsMount = true, Args = [IntArg("max", "the frame cap", "600", 0, 100_000), Arg("loops", ArgType.Enum, "looping tracks: ignore (they do not hold the settle, but a loading indicator does: a skeleton's shimmer, a progress bar or ring), wait (every loop holds it), skeletons (none holds it, so it can end on a skeleton or a spinner)", "ignore", "ignore|wait|skeletons")],
            },
            new(Verb.Reset, "reset", "Unmount, put back what the mount changed (as mount does), reset navigation to Home, activate Classic dark", "reset"),
            new(Verb.Click, "click", "Click a node's visible centre (refused unless actionable and its rect holds still for 2 frames; force=true skips) or a point; then settle", "click key=zune-band-pivot:library")
            {
                Positional = ["key"], NeedsMount = true, Acts = true,
                Args = Join(s_aimArgs, s_stableArgs, [Arg("button", ArgType.Enum, "the button", "left", "left|right"),
                                        Arg("mods", ArgType.String, "modifiers held: ctrl+shift (ctrl, shift, alt, win)")], s_settleArgs),
            },
            new(Verb.Hover, "hover", "Move the pointer to a node's visible centre (refused unless actionable and its rect holds still for 2 frames; force=true skips) or a point; then settle", "hover key=zune-band-pivot:home")
            {
                Positional = ["key"], NeedsMount = true, Acts = true, Args = Join(s_aimArgs, s_stableArgs, s_settleArgs),
            },
            new(Verb.Key, "key", "Press and release a key (ctrl+, shift+, alt+, win+ prefixes) repeat times; then settle", "key ctrl+f")
            {
                Positional = ["key"], NeedsMount = true, Acts = true,
                Args = Join([Arg("key", ArgType.String, "enter, esc, tab, space, backspace, delete, arrows, home, end, pageup, pagedown, apps, f1..f12, a..z, 0..9 or a VK code"),
                             IntArg("repeat", "presses", "1", 1, 100)], s_settleArgs),
            },
            new(Verb.Type, "type", "Type text into the focused node (text form: the rest of the line, spaces kept; quote it to add args); then settle", "type \"daft punk\" settle=false")
            {
                Positional = ["text"], NeedsMount = true, Acts = true, Args = Join([Arg("text", ArgType.String, "the characters to type")], s_settleArgs),
            },
            new(Verb.Scroll, "scroll", "Turn the wheel dy notches at a node (refused unless actionable; force=true skips) or a point; settle waits for the scroller to stop", "scroll key=zune-home-scroll: dy=10")
            {
                Positional = ["key", "dy"], NeedsMount = true, Acts = true,
                Args = Join(s_aimArgs, [Arg("dy", ArgType.Float, "notches, positive toward the end", "3"), Arg("dx", ArgType.Float, "horizontal notches", "0")], s_settleArgs),
            },
            new(Verb.Play, "play", "Play a fake track or episode as its own context (next=N queues the tracks after it), or turn the output remote (remote=); then settle", "play spotify:track:tr3 next=4")
            {
                Positional = ["uri", "fromMs"], NeedsMount = true, Acts = true,
                Args = Join([Arg("uri", ArgType.String, "a spotify:track: or spotify:episode: uri (optional with remote=)"), Arg("fromMs", ArgType.Int, "the start position", "0"),
                             IntArg("next", "queue this many tracks after a spotify:track:tr<k>", "0", 0, 64),
                             Arg("ago", ArgType.Int, "backdate the play in the play log by this many minutes", "0"),
                             Arg("context", ArgType.String, "play the rows in this context (a spotify:playlist: or spotify:album: uri, as a row click on its page does)"),
                             Arg("remote", ArgType.String, "turn the output remote under this name on the live mount (off: local again)")], s_settleArgs),
            },
            new(Verb.Option, "option", "List the active theme's user options, or set one (id, value) the way Settings does", "option style.density Compact")
            {
                Positional = ["id", "value"], Acts = true,
                Args = Join([Arg("themeId", ArgType.String, "the theme whose options (default: the active one)", null, "", "theme"),
                             Arg("id", ArgType.String, "the option (qualified, or plain)"), Arg("value", ArgType.String, "a choice name or index, a number, true/false or #RRGGBB[AA]")],
                            s_transactionMax, s_settleArgs),
            },
            new(Verb.Theme, "theme", "Switch the theme: mounted, the live theme transaction; unmounted, the next mount's theme", "theme zune")
            {
                Positional = ["id"], Acts = true,
                Args = Join([Arg("id", ArgType.Enum, "the theme", null, ThemeChoices)], s_transactionMax, s_settleArgs),
            },
            new(Verb.Find, "find", "Nodes keyed exactly key (or starting with prefix=), in reading order, with shown, onScreen and actionable", "find see-all:home:made-for-you")
            {
                Positional = ["key"], NeedsMount = true,
                Args = [Arg("key", ArgType.Key, "an exact key"), Arg("prefix", ArgType.String, "a key prefix instead", excludes: "key"),
                        Arg("visibleOnly", ArgType.Bool, "only nodes that are shown and at least partly on screen", "false"),
                        Arg("depth", ArgType.Int, "leave out a match nested inside more than this many other matches (0: the outermost only)") with { Min = 0 }],
            },
            new(Verb.Dump, "dump", "The scene (or the subtree root names) as a flat node array: rects, paint, text metrics, flags", "dump root=zune-daylist-play depth=2")
            {
                Positional = ["root"], NeedsMount = true,
                Args =
                [
                    Arg("root", ArgType.Key, "start at every node with this exact key (default: the whole tree and the overlays)"),
                    Arg("nth", ArgType.Int, "pick one of the root's matches (0 first, in slot order)", requires: "root") with { Min = 0 },
                    Arg("depth", ArgType.Int, "how deep the walk goes (0: the root only)") with { Min = 0 },
                    Arg("keyDepth", ArgType.Int, "how many KEYED levels below the root the walk goes, whatever the unkeyed boxes between (lyric lines sit 16 boxes under lyrics:stage, 2 keyed levels)") with { Min = 0 },
                    Arg("keysOnly", ArgType.Bool, "keyed nodes only", "false"),
                    Arg("visibleOnly", ArgType.Bool, "skip hidden, parked, transparent and empty-box nodes", "true"),
                    Arg("text", ArgType.Bool, "write the text blocks", "true"),
                    IntArg("max", "the most nodes", "4000", 1, 200_000),
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
            new(Verb.Tiers, "tiers", "The mounted window's tier facts (window, height and content tiers, rung ids, the live sizes and the published ones the page reads) and the theme's policy: every edge with its holds, the rungs, the named ladders and the sizes crossings uses per edge", "tiers")
            {
                Args = [Arg("theme", ArgType.Enum, "the theme whose policy to list (default: the active one; another theme lists its policy alone)", null, ThemeChoices)],
            },
            new(Verb.Lint, "lint", "Check the mounted scene (or a saved snapshot: replay=) for design flaws: gate rules (clip, trim, type, target, duplicate, rhythm, contrast, theme edges) and todo rules (align, space, placeholder, keys)", "lint min=warn allowDuplicate=\"See all\"")
            {
                Positional = ["rules"], NeedsMount = true, MountUnless = "replay",
                Args = Join([Arg("root", ArgType.Key, "lint every node with this exact key (default: the whole tree and the overlays)"),
                             Arg("nth", ArgType.Int, "pick one of the root's matches (0 first, in slot order)", requires: "root") with { Min = 0 }],
                            s_lintOnlyArgs, s_lintArgs),
            },
            new(Verb.Crossing, "crossing", "Resize across a tier edge the way a user does; report remounts, vanished keys, snaps and jumps; lint the result", "crossing from=1440x960 to=1440x400 theme=zune route=home gpu=none")
            {
                Args = Join(
                [
                    Arg("from", ArgType.Size, "the start size: WxH (text), {w,h} or [w,h] (JSON)"),
                    Arg("to", ArgType.Size, "the end size: WxH (text), {w,h} or [w,h] (JSON)"),
                    Arg("fromW", ArgType.Float, "the start width (instead of from)"), Arg("fromH", ArgType.Float, "the start height (instead of from)"),
                    Arg("toW", ArgType.Float, "the end width (instead of to)"), Arg("toH", ArgType.Float, "the end height (instead of to)"),
                    IntArg("frames", "keep-alive paints at the new size before the commit", "1", 1, 60),
                    Arg("items", ArgType.Int, "entries per reply list", "40"),
                    Arg("max", ArgType.Int, "the settle cap", "600"),
                    Arg("remount", ArgType.Bool, "mount afresh even when a mount is up (the mount args apply)", "false"),
                    Arg("control", ArgType.Bool, "first step the same distance toward `from` inside the old tier and count what that moves: the crossing's excess counts what the edge adds", "true"),
                ], s_mountArgs, s_lintArgs),
            },
            new(Verb.Trace, "trace", "Run N frames; per-frame engine timings, watched nodes and one scroller, plus a summary of snaps, jumps and scroll faults", "trace 90 prefix=zune- scroll=main")
            {
                Positional = ["frames"], NeedsMount = true,
                Args = Join([IntArg("frames", "frames to trace", "60", 1, 5000), IntArg("max", "the most nodes watched", "20", 1, 500)], s_traceOptions),
            },
            new(Verb.State, "state", "Show a page state in place of the page (empty, nomatch, error, offline...), or put the page back (none); loading and cold-* settle with loops=skeletons unless loops is given", "state kind=empty")
            {
                Positional = ["kind"], NeedsMount = true, Acts = true,
                // `loops` has no one default here: a loading or cold state's skeleton is the picture (Diagnostics.Inspect.cs, Verb.State).
                Args = Join([Arg("kind", ArgType.Enum, "the state", null, string.Join("|", StateKinds)),
                    Arg("page", ArgType.Enum, "with kind=loading|empty: that page's OWN state instead of the shared vacancy (a library tab's relations staged; show/episode: a fixture in that state)", null, "library|albums|artists|podcasts|audiobooks|show|episode"),
                    Arg("loops", ArgType.Enum, "looping tracks, as on every settling verb; unset: skeletons for loading and cold-* (their skeleton is the picture), ignore for the rest", null, "ignore|wait|skeletons")], s_settleArgs),
            },
            new(Verb.Dialog, "dialog", "Open the crash prompt (crash, hang), Report a problem (report) or the artist gallery's lightbox over bundled photographs (lightbox) over the current page; key esc closes it", "dialog kind=report")
            {
                Positional = ["kind"], NeedsMount = true, Acts = true,
                Args = Join([Arg("kind", ArgType.Enum, "the dialog", null, "crash|hang|report|lightbox")], s_settleArgs),
            },
            new(Verb.Skeleton, "skeleton", "Compare a page's loading skeleton with the page loaded: the pending fixture (it never loads) against the loaded uri, every shown key's top and height and every Skel.Region's height, in DIP; firstMoved is the jump a person sees when the data lands", "skeleton loaded=spotify:artist:ar3")
            {
                NeedsMount = true, Acts = true,
                Args =
                [
                    Arg("loaded", ArgType.String, "the loaded page's spotify uri"),
                    Arg("pending", ArgType.String, "a uri of the same kind the fake never answers (default: spotify:artist:arzzqx, spotify:show:shzzqx, spotify:episode:epzzqx by kind)"),
                    Arg("max", ArgType.Int, "each settle's frame cap", "600"),
                    Arg("items", ArgType.Int, "the most moved keys and regions listed", "20"),
                    Arg("tolerance", ArgType.Float, "a move or a height change under this (DIP) is none", "1"),
                ],
            },
            new(Verb.Views, "views", "Which template draws every view key of a theme (the active one) and which layer registered every view and style key; standins counts the keys a stand-in fills", "views filter=standins")
            {
                Args =
                [
                    Arg("theme", ArgType.Enum, "the theme to report (default: the active one)", null, ThemeChoices),
                    Arg("filter", ArgType.Enum, "list every key, only the stand-ins, or only the theme's own (the counts cover every key)", "all", "all|standins|own"),
                    Arg("prefix", ArgType.String, "list only keys starting with this"),
                ],
            },
            new(Verb.Crossings, "crossings", "Cross every window and height edge of the theme both ways over each route, in this one server: a fresh mount inside the old tier per row, then one crossing (old side to new side, frames 1); every crossing reply kept whole, with a row and a table line", "crossings theme=winui routes=[home,album,artist,playlist]")
            {
                Args = Join(
                [
                    Arg("routes", ArgType.List, "the pages: a route key, a spotify: uri, or album|artist|playlist|show|episode (the fixtures al2, ar3, pl0, sh0, ep0)", "home,album,artist,playlist"),
                    Arg("edges", ArgType.List, "only these edges, by name (W1008, H720; tiers lists them)"),
                    Arg("dirs", ArgType.Enum, "narrowing, widening or both", "both", "both|narrow|widen"),
                    Arg("control", ArgType.Bool, "each crossing's own control step (crossing's control: the excess counts what the edge adds over a plain resize)", "true"),
                    Arg("w", ArgType.Float, "the width a height edge is crossed at", "1440"),
                    Arg("h", ArgType.Float, "the height a width edge is crossed at", "960"),
                    Arg("theme", ArgType.Enum, "the theme (default: the active one)", null, ThemeChoices),
                    Arg("kind", ArgType.Enum, "light or dark (default: the current kind)", null, "dark|light"),
                    Arg("gpu", ArgType.Enum, "each row's mount devices: none by default (a crossing reads rects and the scene, never a frame's pixels), hardware or warp to keep pixels for a render after", "none", "warp|hardware|none"),
                    Arg("scale", ArgType.Float, "DPI scale of each row's mount", "1"),
                    Arg("images", ArgType.Enum, "fake or real images", "fake", "fake|real"),
                    Arg("grade", ArgType.Enum, "cover grading", null, "hue|grey|gray|greynow"),
                    Arg("items", ArgType.Int, "entries per list in each crossing reply", "8"),
                    Arg("max", ArgType.Int, "the settle cap", "600"),
                ], s_lintArgs),
            },
            new(Verb.Drag, "drag", "A mouse drag: press on a node's visible centre (refused unless actionable and still; force=true skips) or a point, move dx,dy over steps frames with the button down, release; then settle", "drag key=zune-panel-edge dx=-120 steps=12")
            {
                Positional = ["key"], NeedsMount = true, Acts = true,
                Args = Join(s_aimArgs, s_stableArgs,
                [
                    Arg("dx", ArgType.Float, "DIP the pointer travels along x (negative: left)", "0"),
                    Arg("dy", ArgType.Float, "DIP the pointer travels along y (negative: up)", "0"),
                    IntArg("steps", "frames the pointer moves over, one equal move per frame", "8", 1, 600),
                    Arg("button", ArgType.Enum, "the button", "left", "left|right"),
                    Arg("mods", ArgType.String, "modifiers held: ctrl+shift (ctrl, shift, alt, win)"),
                ], s_settleArgs),
            },
            new(Verb.Setting, "setting", "List the app's Settings rows (tab, section, id, kind, value, writer), or set one through the row's own writer as a Settings view does (the theme's options are `option`)", "setting id=lyricsBlur value=40")
            {
                Positional = ["id", "value"],
                Args = Join([Arg("tab", ArgType.String, "only this tab's rows (general|appearance|playback|notifications|storage|privacy|about|account|devices|keyboard|feedback); needed when an id is on several tabs"),
                             Arg("id", ArgType.String, "the row (any case)"),
                             Arg("value", ArgType.String, "a toggle's true|false, a slider's number, a choice's index; an action takes none")], s_settleArgs),
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
            "views" => Verb.Views, "skeleton" => Verb.Skeleton, "crossings" => Verb.Crossings, "setting" => Verb.Setting, "drag" => Verb.Drag,
            "quit" or "exit" => Verb.Quit,
            _ => Verb.None,
        };

        /// <summary>The names a verb's bare (positional) words fill, in order: `goto home` is `goto route=home`.</summary>
        public static ReadOnlySpan<string> PositionalNames(Verb verb) => SpecOf(verb)?.Positional ?? default(ReadOnlySpan<string>);

        // ── the strict-arg check ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every arg of <paramref name="cmd"/> must be one its verb declares (exact case), hold a value of its type (and
        /// range), stand beside the arg it <see cref="ArgSpec.Requires"/> (given, and when a number, above 0: `trace=0` traces
        /// nothing) and not beside the one it <see cref="ArgSpec.Excludes"/>. An alias is rewritten to its canonical name, and a
        /// value to its canonical form (an enum word to its declared spelling, a bool word to a bool, a list to a JSON array of
        /// strings). False with the reason otherwise: "unknown arg 'rule' for lint (did you mean 'rules'?)", "bad value 'middle'
        /// for button (left|right)", "'keys' applies only beside trace=N with N > 0", "'x' and 'key' do not go together".</summary>
        public static bool Validate(in Command cmd, out string error)
        {
            error = "";
            if (SpecOf(cmd.Verb) is not { } spec) return true;
            Args args = cmd.Args;
            List<(string From, string To)>? renames = null;
            foreach (string name in args.Names)
            {
                if (ArgNamed(spec, name) is { } declared)
                {
                    if (declared.Name != name) (renames ??= []).Add((name, declared.Name));
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
                    if (args.Contains(to)) { error = "'" + from + "' and '" + to + "' are the same arg of " + spec.Name; return false; }
                    if (args.Remove(from, out Value v)) args.Set(to, v);
                }
            foreach (ArgSpec arg in spec.Args)
            {
                if (!args.TryGet(arg.Name, out Value value)) continue;   // absent (or JSON null: absent too)
                if (arg.Requires is { } need && !Engaged(spec, args, need))
                {
                    error = "'" + arg.Name + "' applies only beside " + need + (args.Has(need) ? "=N with N > 0" : "=") + " (" + spec.Name + " ignores it otherwise)";
                    return false;
                }
                if (arg.Excludes is { } other && args.Has(other))
                {
                    error = "'" + arg.Name + "' and '" + other + "' do not go together (" + spec.Name + " takes one or the other)";
                    return false;
                }
                if (!TryCanonical(arg, args, value, out error)) return false;
            }
            return true;
        }

        /// <summary>The arg a Requires names is in effect: given, and (an int or a number) above 0 (`trace=0` traces nothing), or (a bool) true.</summary>
        static bool Engaged(VerbSpec spec, Args args, string name)
        {
            if (!args.TryGet(name, out Value v)) return false;
            return ArgNamed(spec, name)?.Type switch
            {
                ArgType.Int or ArgType.Float => args.TryNum(name, out double n) && n > 0,
                ArgType.Bool => BoolOf(v) is true,
                _ => true,
            };
        }

        /// <summary>A bool a value spells: true/false, on/off, yes/no, 1/0 (any case), or null.</summary>
        static bool? BoolOf(Value value) => value.Kind switch
        {
            ValueKind.Bool => value.Bool,
            ValueKind.Number => value.Num == 1 ? true : value.Num == 0 ? false : null,
            ValueKind.String => value.Str.ToLowerInvariant() switch
            {
                "true" or "on" or "yes" or "1" => true,
                "false" or "off" or "no" or "0" => false,
                _ => null,
            },
            _ => null,
        };

        /// <summary>Check one present value against its row and write it back canonical. False with "bad value ..." otherwise.</summary>
        static bool TryCanonical(ArgSpec arg, Args args, Value value, out string error)
        {
            error = "";
            switch (arg.Type)
            {
                case ArgType.Int:
                    if (args.TryNum(arg.Name, out double n) && n == Math.Floor(n) && Math.Abs(n) <= int.MaxValue) return InRange(arg, value, n, out error);
                    error = BadValue(arg, value, "a whole number" + RangeText(arg));
                    return false;
                case ArgType.Float:
                    if (args.TryNum(arg.Name, out double f)) return InRange(arg, value, f, out error);
                    error = BadValue(arg, value, "a number" + RangeText(arg));
                    return false;
                case ArgType.Bool:
                    {
                        if (BoolOf(value) is not { } flag) { error = BadValue(arg, value, "true or false"); return false; }
                        args.Set(arg.Name, new Value(ValueKind.Bool, Bool: flag));
                        return true;
                    }
                case ArgType.Enum:
                    {
                        if (arg.Choices.Length == 0) return true;
                        // An enum with a yes and a no among its choices (setup's true|false|signin, gallery's on|off) takes a bool's spellings for them too.
                        string? choice = value.Kind == ValueKind.Json ? null : ChoiceOf(arg, value.ToString());
                        if (choice is null && BoolOf(value) is { } yes) choice = ChoiceOf(arg, yes ? "true" : "false") ?? ChoiceOf(arg, yes ? "on" : "off");
                        if (choice is not null)
                        {
                            args.Set(arg.Name, new Value(ValueKind.String, choice));
                            return true;
                        }
                        error = BadValue(arg, value, string.Join("|", arg.Choices));
                        return false;
                    }
                case ArgType.List:
                    {
                        if (value.Kind == ValueKind.Json && !value.Str.AsSpan().TrimStart().StartsWith("[", StringComparison.Ordinal))
                        {
                            error = BadValue(arg, value, "a list: a,b or [a,\"b c\"]");
                            return false;
                        }
                        List<string> items = args.List(arg.Name);
                        if (arg.Choices.Length > 0)
                        {
                            if (items.Count == 0) { error = "'" + arg.Name + "' names nothing (" + string.Join("|", arg.Choices) + ")"; return false; }
                            for (int i = 0; i < items.Count; i++)
                            {
                                if (ChoiceOf(arg, items[i]) is not { } choice)
                                {
                                    error = "bad value '" + items[i] + "' in " + arg.Name + " (" + string.Join("|", arg.Choices) + ")";
                                    return false;
                                }
                                items[i] = choice;
                            }
                        }
                        // One shape for every reader: a JSON array of strings (a bracketed number or true/false is its text, so `[2024,Play]` keeps 2024).
                        if (value.Kind == ValueKind.Json || arg.Choices.Length > 0) args.Set(arg.Name, new Value(ValueKind.Json, JsonList(items)));
                        return true;
                    }
                case ArgType.String or ArgType.Key or ArgType.Path:
                    if (value.Kind != ValueKind.Json) return true;
                    error = BadValue(arg, value, "a single value, not a list or an object");
                    return false;
            }
            return true;   // Size: the verb parses WxH, {w,h} or [w,h] itself
        }

        static string BadValue(ArgSpec arg, Value value, string want) => "bad value '" + value + "' for " + arg.Name + " (" + want + ")";

        /// <summary>True when <paramref name="n"/> lies in the row's range; else false with "bad value '-5' for n (a whole number, 0..100000)".</summary>
        static bool InRange(ArgSpec arg, Value value, double n, out string error)
        {
            error = "";
            if ((arg.Min is not { } min || n >= min) && (arg.Max is not { } max || n <= max)) return true;
            error = BadValue(arg, value, (arg.Type == ArgType.Int ? "a whole number" : "a number") + RangeText(arg));
            return false;
        }

        /// <summary>", 0..100000", ", 0 or more", ", at most 64", or nothing for a row with no range.</summary>
        static string RangeText(ArgSpec arg) => (arg.Min, arg.Max) switch
        {
            ({ } min, { } max) => ", " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture),
            ({ } min, null) => ", " + min.ToString(CultureInfo.InvariantCulture) + " or more",
            (null, { } max) => ", at most " + max.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };

        /// <summary>The declared choice <paramref name="word"/> names (any case), or null.</summary>
        static string? ChoiceOf(ArgSpec arg, string word)
        {
            foreach (string c in arg.Choices)
                if (string.Equals(c, word, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary><paramref name="items"/> as a JSON array of strings.</summary>
        static string JsonList(IEnumerable<string> items)
        {
            var buffer = new ArrayBufferWriter<byte>(64);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartArray();
                foreach (string item in items) w.WriteStringValue(item);
                w.WriteEndArray();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>The row <paramref name="name"/> names on <paramref name="spec"/> (its name or an alias, exact case), or null.</summary>
        static ArgSpec? ArgNamed(VerbSpec spec, string name)
        {
            foreach (ArgSpec a in spec.Args)
            {
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
                foreach (string alias in a.Aliases)
                    if (string.Equals(alias, name, StringComparison.Ordinal)) return a;
            }
            return null;
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
            if (s.MountUnless is { } unless) w.WriteString("needsMountUnless", unless);
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
                if (a.Requires is { } need) w.WriteString("requires", need);
                if (a.Excludes is { } other) w.WriteString("excludes", other);
                if (a.Min is { } min) w.WriteNumber("min", min);
                if (a.Max is { } max) w.WriteNumber("max", max);
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
            w.WriteString("grammar", "one command per line: {\"cmd\",\"args\",\"id\"} or `verb k=v k=\"a b\" k=[a,\"b c\"] bare`; bare words fill the `positional` names not given by name, in order; arg names are case-sensitive, and an undeclared name, a name given twice, a value not of the arg's type, choices or `min`..`max` range, an arg given without the one it `requires` (a number: above 0) or beside the one it `excludes` is refused");
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
            /// <summary>Given at all, even as JSON null (<see cref="Has"/> reads null as absent).</summary>
            public bool Contains(string name) => _values.ContainsKey(name);
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
                            foreach (JsonProperty a in p.Value.EnumerateObject())
                                if (!TryPut(args, a.Name, ValueOf(a.Value), out error)) return false;
                            continue;
                        }
                        if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            ReadOnlySpan<string> names = PositionalNames(verb);
                            int i = 0;
                            foreach (JsonElement a in p.Value.EnumerateArray())
                            {
                                if (i >= names.Length) { error = "too many positional args for '" + name + "'"; return false; }
                                if (!TryPut(args, names[i++], ValueOf(a), out error)) return false;
                            }
                            continue;
                        }
                        if (p.Value.ValueKind == JsonValueKind.Null) continue;
                        error = "\"args\" must be an object or an array";
                        return false;
                    }
                    if (!TryPut(args, p.Name, ValueOf(p.Value), out error)) return false;
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
        /// positional names that no `k=v` has filled yet, in order. A name given twice is refused. A value in double quotes keeps
        /// its blanks and is always a string (`\"` and `\\` escape; any other backslash stays, so Windows paths need nothing); a
        /// quoted value of a list arg is a one-item list, commas and all. A value in brackets is a list, kept as a JSON array
        /// exactly as the JSON form would carry it (unquoted numbers, true and false as JSON literals, everything else a string).
        /// The `type` verb takes the whole rest of the line as its text (spaces kept) unless it starts with a quote or `text=`.</summary>
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
            if (verb == Verb.Type && !rest.IsEmpty && rest[0] != '"' && !rest.StartsWith("text=", StringComparison.Ordinal))
            {
                args.Set("text", new Value(ValueKind.String, rest.ToString()));
                cmd = new Command(verb, name, args);
                return true;
            }
            VerbSpec? spec = SpecOf(verb);
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
                bool quoted = i < rest.Length && rest[i] == '"';
                if (!TryValue(rest, ref i, out Value value, out error)) { error += " in '" + rest[start..].ToString() + "'"; return false; }
                if (i < rest.Length && !IsBlank(rest[i])) { error = "'" + rest[start..].ToString() + "': a quoted value or a list ends at a blank"; return false; }
                if (argName is null)
                {
                    while (positional < names.Length && args.Contains(names[positional])) positional++;   // `scroll key=x 10`: 10 is dy
                    if (positional >= names.Length) { error = "unexpected '" + rest[start..i].ToString() + "' after " + name; return false; }
                    argName = names[positional++];
                }
                if (quoted && spec is not null && ArgNamed(spec, argName) is { Type: ArgType.List })
                    value = new Value(ValueKind.Json, JsonList([value.Str]));   // `allowDuplicate="Hello, world"` is one label, not two
                if (!TryPut(args, argName, value, out error)) return false;
            }
            cmd = new Command(verb, name, args);
            return true;
        }

        /// <summary><paramref name="value"/> as a text command writes it: bare when it can be (`chip:Pop`), else in double quotes with
        /// `"` and `\` escaped (`"chip:Hip Hop"`), so a hint's command runs as printed.</summary>
        public static string Quote(string value)
        {
            if (value.Length > 0 && value.AsSpan().IndexOfAny(" \t\"[]=,\\") < 0) return value;
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        /// <summary>Add one arg; refuses a name the line already gave ("'rules' given twice": which one was meant is not ours to guess).</summary>
        static bool TryPut(Args args, string name, Value value, out string error)
        {
            error = "";
            if (args.Contains(name)) { error = "'" + name + "' given twice"; return false; }
            args.Set(name, value);
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
