// ── Screens/Diagnostics.Inspect.Protocol.cs ────────────────────────────────────────────────────────────────────────────
// the `--inspect` server's command grammar, its argv parse and the JSON reply writer (pure: no engine, no thread, no I/O)
//
// Role: CORE
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2; docs/guide/agent-inspect.md is the user-facing reference.
//
// ONE LINE = ONE COMMAND, the `--headless` shape: a JSON object `{"cmd":"mount","args":{"w":400,"h":860},"id":1}` (the args
// may also sit at the top level beside cmd and id, and a headless-style `"args":[...]` array fills the verb's positional
// names), or a text line `mount w=400 h=860 theme=zune` (bare words are positional: `goto home`, `find zune.band.pivot.`).
// `#` starts a comment line. Every command gets exactly ONE reply line: `{"id":1,"ok":true,"cmd":"mount",...}` or
// `{"id":1,"ok":false,"cmd":"mount","error":"..."}`. The server also prints one `{"kind":"ready",...}` line at boot and one
// `{"kind":"bye"}` line at exit.
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
            Click, Hover, Key, Type, Scroll, Play, Option, Theme, Find, Dump, Render, Tiers, Lint, Crossing, Quit,
        }

        /// <summary>Every verb's name, in the order `help` lists them.</summary>
        public static readonly string[] VerbNames =
        [
            "help", "status", "mount", "unmount", "goto", "resize", "frames", "settle", "reset",
            "click", "hover", "key", "type", "scroll", "play", "option", "theme", "find", "dump", "render", "tiers", "lint", "crossing", "quit",
        ];

        public static Verb VerbOf(string word) => word.ToLowerInvariant() switch
        {
            "help" => Verb.Help, "status" or "ping" => Verb.Status, "mount" => Verb.Mount, "unmount" => Verb.Unmount,
            "goto" or "go" or "navigate" => Verb.GoTo, "resize" => Verb.Resize, "frames" => Verb.Frames, "settle" => Verb.Settle,
            "reset" => Verb.Reset, "click" => Verb.Click, "hover" => Verb.Hover, "key" => Verb.Key, "type" => Verb.Type,
            "scroll" => Verb.Scroll, "play" => Verb.Play, "option" => Verb.Option, "theme" => Verb.Theme, "find" => Verb.Find,
            "dump" => Verb.Dump, "render" => Verb.Render, "tiers" => Verb.Tiers, "lint" => Verb.Lint, "crossing" => Verb.Crossing,
            "quit" or "exit" => Verb.Quit,
            _ => Verb.None,
        };

        /// <summary>The names a verb's bare (positional) words fill, in order: `goto home` is `goto route=home`.</summary>
        public static ReadOnlySpan<string> PositionalNames(Verb verb) => verb switch
        {
            Verb.Mount => s_mountPos,
            Verb.Resize => s_whPos,
            Verb.GoTo => s_routePos,
            Verb.Frames => s_nPos,
            Verb.Settle => s_maxPos,
            Verb.Click or Verb.Hover => s_keyPos,
            Verb.Key => s_keyPos,
            Verb.Type => s_textPos,
            Verb.Scroll => s_scrollPos,
            Verb.Play => s_playPos,
            Verb.Option => s_optionPos,
            Verb.Theme => s_idPos,
            Verb.Find => s_prefixPos,
            Verb.Dump => s_rootPos,
            Verb.Render => s_pathPos,
            Verb.Lint => s_rulesPos,
            _ => default,
        };

        static readonly string[] s_mountPos = ["w", "h"], s_whPos = ["w", "h"], s_routePos = ["route", "arg"], s_nPos = ["n"],
            s_maxPos = ["max"], s_keyPos = ["key"], s_textPos = ["text"], s_scrollPos = ["key", "dy"], s_playPos = ["uri", "fromMs"],
            s_optionPos = ["id", "value"], s_idPos = ["id"], s_prefixPos = ["prefix"], s_rootPos = ["root"], s_pathPos = ["path"], s_rulesPos = ["rules"];

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
            return line[0] == '{' ? TryParseJson(line, out cmd, out error) : TryParseText(line, out cmd, out error);
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

        /// <summary>`verb a=1 b=two bare words` — named `k=v` pairs, and bare words filling the verb's positional names. The
        /// `type` verb takes the whole rest of the line as its text (spaces kept).</summary>
        static readonly char[] s_blanks = [' ', '\t'];

        static bool TryParseText(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = new Command(Verb.None, "", Args.Empty);
            error = "";
            int sp = line.IndexOfAny(' ', '\t');
            string name = (sp < 0 ? line : line[..sp]).ToString();
            Verb verb = VerbOf(name);
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
            int positional = 0;
            foreach (string word in rest.ToString().Split(s_blanks, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = word.IndexOf('=');
                if (eq > 0)
                {
                    args.Set(word[..eq], Value.FromWord(word[(eq + 1)..]));
                    continue;
                }
                if (positional >= names.Length) { error = "unexpected '" + word + "' after " + name; return false; }
                args.Set(names[positional++], Value.FromWord(word));
            }
            cmd = new Command(verb, name, args);
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
