// ── Screens/Diagnostics.Headless.cs ────────────────────────────────────────────────────────────────────────────────
// the headless command grammar, the wait-condition evaluator, the script runner, the JSON-lines shape, the exit
// codes, the settings overlay, the protected credential store, the context-queue builder
//
// Role: CORE
// Owner: X
// Wave: now (lands beside Waves 4-5; consumed by Diagnostics.Probe.cs's headless arm)
// Budget: 700 lines
// Spec: docs/plans/wavee/wavee-0.3-headless-implementation.md §2.4-2.8, §3.1
//
// Pure by construction: no engine type, no thread, no clock, no I/O. Every decision below is driven by a StatusSnapshot
// the SHELL samples and hands in, which is why HeadlessTests can replay a whole smoke script against a scripted
// sequence of snapshots and pin the verdict and the exit code.
//
// THE GRAMMAR (one line = one command; `#` at the start or after whitespace starts a comment; a line may also be a JSON
// object `{"cmd":"seek","args":["1:30"],"id":7}` — the id comes back on the reply):
//   play <spotify:track|episode|album|playlist:id> [from <pos>] [quality normal|high|veryhigh|lossless]
//   pause · resume · toggle · stop · next · prev · prepare · status · login
//   seek <pos>        pos = ms · m:ss · m:ss.fff · h:mm:ss · 90s · +5s · -10s · +1500ms · end-10s
//   volume <0..1> · shuffle on|off · repeat off|context|track · quality <rung> · queue <spotify:track|episode:id>
//   set crossfade <ms> · set normalization on|off · set cache on|off · set metered-cap <rung>
//                     (read at the audio boot or the next open: put `set` lines before the first volume/play)
//   stats [mark|reset] · wait[?] <cond> [timeout <ms>] · expect[?] <cond> · sleep <ms> · connect on|off
//   log <text> · quit [code]
// A condition is up to four clauses joined by `&&` (no `||` — write two steps):
//   playing|paused|loading|idle|ended · online|offline|failed|… · buffering · next · prefetched · seeked (each `!`-able)
//   phase==… · session==… · track==uri|track!= · format==flac24 · owner==us|foreign|nobody · seek.kind==ring|far|disk
//   position>=10s · position<1:00 · position>=+5s (relative to the wait's start) · cdn.requests<=4 (delta since the last
//   `stats mark`) · cdn.inflight<=1 · xruns==0 · gapless.exact>=1 (deltas since the mark)

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public static class Headless
    {
        // ── 1. exit codes (§2.5) ────────────────────────────────────────────────────────────────────────────────────
        public static class ExitCode
        {
            public const int Ok = 0, Fault = 1, Assertion = 2, HostError = 3,
                             Usage = 64, NoCredential = 67, NoEndpoint = 69, LoginTimeout = 75, CredentialRejected = 77, Config = 78;

            /// <summary>The table as text — the wrapper script and the verdict line print the same words.</summary>
            public static string Describe(int code) => code switch
            {
                Ok => "ok", Fault => "fault", Assertion => "assertion", HostError => "host error", Usage => "usage",
                NoCredential => "no stored credential", NoEndpoint => "no audio endpoint", LoginTimeout => "login timeout",
                CredentialRejected => "credential rejected", Config => "profile/config", _ => "quit " + code.ToString(CultureInfo.InvariantCulture),
            };
        }

        // ── 2. what a script sees: one flat snapshot per tick ───────────────────────────────────────────────────────
        /// <summary>Everything a condition may read. The SHELL fills it from Playback.Snap(), Spotify.Current,
        /// Stream.Stats and Audio.Metrics ONCE per tick on the loop thread; CORE never reaches for a live static. The
        /// members after <paramref name="FirstAudioMs"/> are the extra counters the `stats` line and `seeked` read.</summary>
        public readonly record struct StatusSnapshot(
            long NowMs,
            string SessionPhase, string SessionFault, string Tier, string Country,
            string Phase, bool Buffering, string Fault, string TrackUri, int PositionMs, int DurationMs,
            string Format, float Volume, string Owner, uint LoadEpoch, bool PrepareArmed,
            long CdnRequests, int CdnInFlight, int CdnCancelled, long CdnHeads, long CdnResolves, long CdnCacheHits,
            int Xruns, int GaplessExact, int GaplessDegraded, int FirstAudioMs,
            int CdnPeakInFlight = 0, int CdnPingMs = 0, long CdnBytesPerSecond = 0, int RingWaits = 0, int RingStarves = 0,
            int HeadBytes = 0, bool FirstAudioFromHead = false, int LastSeekMs = 0, int LastSeekLatencyMs = 0,
            byte LastSeekKind = 0, int GaplessAbandoned = 0, float DecodeXRealtime = 0f)
        {
            /// <summary>Nothing running, every counter zero — the first tick's "previous", and what `stats reset` marks.</summary>
            public static StatusSnapshot Empty => new(0, "Offline", "None", "Unknown", "", "Idle", false, "None", "", 0, 0,
                "", 1f, "Nobody", 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        // ── 3. the grammar (§2.4) ───────────────────────────────────────────────────────────────────────────────────
        public enum Verb : byte
        {
            None, Play, Pause, Resume, Toggle, Stop, Seek, Next, Prev, Volume, Shuffle, Repeat, Quality, Set, Queue,
            Prepare, Status, Stats, Wait, Expect, Sleep, Login, Connect, Log, Quit,
        }

        /// <summary>One parsed line, already validated. A Seek carries a resolved position (absolute, relative or from
        /// the end); a Wait carries a parsed Condition; a Quit carries a code (-1 = the verdict); a Play carries the from
        /// position in Int0 and the quality rung in Int1 (-1 = unchanged); Volume is thousandths; Soft marks `wait?`.</summary>
        public readonly record struct Command(Verb Verb, string Text, string Arg0 = "", int Int0 = 0, bool Relative = false,
                                              int TimeoutMs = 0, Condition Cond = default, int Id = -1,
                                              int Int1 = -1, bool FromEnd = false, bool Soft = false)
        {
            public bool IsNone => Verb == Verb.None;
        }

        const int MaxTokens = 32;

        /// <summary>Parse one line — text (`seek 1:30`) or JSON (`{"cmd":"seek","args":["1:30"],"id":7}`). Never
        /// throws: a bad line is (false, reason) so a script reports its line number before anything boots. A failed
        /// JSON line still carries its id, so the reply can name it.</summary>
        public static bool TryParse(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = default; error = "";
            line = line.Trim();
            if (line.IsEmpty || line[0] == '#') return true;                         // Verb.None: a comment or blank
            if (line[0] == '{') return TryParseJson(line, out cmd, out error);
            int hash = CommentAt(line);
            if (hash >= 0) line = line[..hash].TrimEnd();
            return line.IsEmpty || TryParseText(line, -1, out cmd, out error);
        }

        static int CommentAt(ReadOnlySpan<char> line)
        {
            for (int i = 1; i < line.Length; i++)
                if (line[i] == '#' && char.IsWhiteSpace(line[i - 1])) return i;
            return -1;
        }

        static bool TryParseJson(ReadOnlySpan<char> line, out Command cmd, out string error)
        {
            cmd = default; error = "";
            int id = -1;
            string text;
            try
            {
                using var doc = JsonDocument.Parse(line.ToString());
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "a JSON command is an object"; return false; }
                if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt32(out int parsed)) id = parsed;
                cmd = new Command(Verb.None, "", Id: id);
                if (!root.TryGetProperty("cmd", out JsonElement verb) || verb.ValueKind != JsonValueKind.String)
                { error = "a JSON command needs a \"cmd\" string"; return false; }
                var sb = new StringBuilder(verb.GetString());
                if (root.TryGetProperty("args", out JsonElement args))
                {
                    if (args.ValueKind != JsonValueKind.Array) { error = "\"args\" must be an array"; return false; }
                    foreach (JsonElement a in args.EnumerateArray())
                        sb.Append(' ').Append(a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText());
                }
                text = sb.ToString();
            }
            catch (JsonException ex) { error = "bad JSON: " + ex.Message; return false; }
            if (TryParseText(text.AsSpan().Trim(), id, out cmd, out error)) return true;
            cmd = new Command(Verb.None, text, Id: id);
            return false;
        }

        static Verb VerbOf(string word) => word switch
        {
            "play" => Verb.Play, "pause" => Verb.Pause, "resume" => Verb.Resume, "toggle" => Verb.Toggle, "stop" => Verb.Stop,
            "seek" => Verb.Seek, "next" => Verb.Next, "prev" => Verb.Prev, "volume" => Verb.Volume, "shuffle" => Verb.Shuffle,
            "repeat" => Verb.Repeat, "quality" => Verb.Quality, "set" => Verb.Set, "queue" => Verb.Queue,
            "prepare" => Verb.Prepare, "status" => Verb.Status, "stats" => Verb.Stats, "wait" => Verb.Wait,
            "expect" => Verb.Expect, "sleep" => Verb.Sleep, "login" => Verb.Login, "connect" => Verb.Connect,
            "log" => Verb.Log, "quit" => Verb.Quit, _ => Verb.None,
        };

        static bool TryParseText(ReadOnlySpan<char> line, int id, out Command cmd, out string error)
        {
            cmd = default; error = "";
            string text = line.ToString();
            Span<Range> parts = stackalloc Range[MaxTokens];
            int n = line.SplitAny(parts, " \t", StringSplitOptions.RemoveEmptyEntries);
            if (n == 0) return true;
            if (n == MaxTokens) { error = "too many words on one line"; return false; }
            string word = line[parts[0]].ToString();
            string lower = word.ToLowerInvariant();
            bool soft = lower.EndsWith('?');
            if (soft) lower = lower[..^1];
            Verb verb = VerbOf(lower);
            if (verb == Verb.None) { error = "unknown command '" + word + "'"; return false; }
            if (soft && verb is not (Verb.Wait or Verb.Expect)) { error = "only wait? and expect? can be soft"; return false; }
            int argc = n - 1;
            ReadOnlySpan<char> rest = line[parts[0].End.Value..].Trim();

            switch (verb)
            {
                case Verb.Pause: case Verb.Resume: case Verb.Toggle: case Verb.Stop: case Verb.Next: case Verb.Prev:
                case Verb.Prepare: case Verb.Status: case Verb.Login:
                    if (argc != 0) { error = "'" + lower + "' takes no arguments"; return false; }
                    cmd = new Command(verb, text, Id: id);
                    return true;

                case Verb.Play:
                    {
                        if (argc < 1) { error = "play wants a spotify uri"; return false; }
                        string uri = line[parts[1]].ToString();
                        if (!IsSpotifyUri(uri, containers: true)) { error = "play wants spotify:track|episode|album|playlist:<22-char id>"; return false; }
                        int from = 0, rung = -1;
                        for (int i = 2; i < n; i += 2)
                        {
                            string key = line[parts[i]].ToString().ToLowerInvariant();
                            if (i + 1 >= n) { error = "'" + key + "' wants a value"; return false; }
                            ReadOnlySpan<char> value = line[parts[i + 1]];
                            if (key == "from")
                            {
                                if (!TryParsePosition(value, out from, out bool rel, out bool end) || rel || end || from < 0)
                                { error = "bad from position '" + value.ToString() + "' (absolute: ms, m:ss, m:ss.fff)"; return false; }
                            }
                            else if (key == "quality")
                            {
                                if (!TryParseQuality(value, out rung)) { error = "bad quality '" + value.ToString() + "'"; return false; }
                            }
                            else { error = "unexpected '" + key + "' after play"; return false; }
                        }
                        cmd = new Command(Verb.Play, text, Arg0: uri, Int0: from, Id: id, Int1: rung);
                        return true;
                    }

                case Verb.Queue:
                    {
                        string uri = argc == 1 ? line[parts[1]].ToString() : "";
                        if (!IsSpotifyUri(uri, containers: false)) { error = "queue wants spotify:track|episode:<22-char id>"; return false; }
                        cmd = new Command(Verb.Queue, text, Arg0: uri, Id: id);
                        return true;
                    }

                case Verb.Seek:
                    {
                        if (argc != 1) { error = "seek wants one position"; return false; }
                        if (!TryParsePosition(line[parts[1]], out int ms, out bool rel, out bool end) || (!rel && !end && ms < 0))
                        { error = "bad position '" + line[parts[1]].ToString() + "' (ms, m:ss, m:ss.fff, h:mm:ss, +5s, -10s, end-10s)"; return false; }
                        cmd = new Command(Verb.Seek, text, Int0: ms, Relative: rel, Id: id, FromEnd: end);
                        return true;
                    }

                case Verb.Volume:
                    {
                        if (argc != 1 || !double.TryParse(line[parts[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                            || !(v >= 0 && v <= 1))
                        { error = "volume wants a number from 0 to 1"; return false; }
                        cmd = new Command(Verb.Volume, text, Int0: (int)Math.Round(v * 1000), Id: id);
                        return true;
                    }

                case Verb.Shuffle:
                case Verb.Connect:
                    {
                        if (argc != 1 || !TryOnOff(line[parts[1]], out bool on)) { error = lower + " wants on|off"; return false; }
                        cmd = new Command(verb, text, Int0: on ? 1 : 0, Id: id);
                        return true;
                    }

                case Verb.Repeat:
                    {
                        string mode = argc == 1 ? line[parts[1]].ToString().ToLowerInvariant() : "";
                        int value = mode switch { "off" => 0, "context" => 1, "track" => 2, _ => -1 };
                        if (value < 0) { error = "repeat wants off|context|track"; return false; }
                        cmd = new Command(Verb.Repeat, text, Int0: value, Id: id);
                        return true;
                    }

                case Verb.Quality:
                    {
                        if (argc != 1 || !TryParseQuality(line[parts[1]], out int rung)) { error = "quality wants normal|high|veryhigh|lossless"; return false; }
                        cmd = new Command(Verb.Quality, text, Int0: rung, Id: id);
                        return true;
                    }

                case Verb.Set:
                    {
                        if (argc != 2) { error = "set wants a name and a value"; return false; }
                        string name = line[parts[1]].ToString().ToLowerInvariant();
                        ReadOnlySpan<char> value = line[parts[2]];
                        int parsed;
                        bool ok = name switch
                        {
                            "crossfade" => TryParseDuration(value, out parsed),
                            "normalization" or "cache" => TryOnOffInt(value, out parsed),
                            "metered-cap" => TryParseQuality(value, out parsed),
                            _ => Fail(out parsed),
                        };
                        if (!ok)
                        {
                            error = name is "crossfade" or "normalization" or "cache" or "metered-cap"
                                ? "bad value '" + value.ToString() + "' for set " + name
                                : "set knows crossfade, normalization, cache, metered-cap";
                            return false;
                        }
                        cmd = new Command(Verb.Set, text, Arg0: name, Int0: parsed, Id: id);
                        return true;
                    }

                case Verb.Stats:
                    {
                        string mode = argc == 1 ? line[parts[1]].ToString().ToLowerInvariant() : "";
                        if (argc > 1 || mode is not ("" or "mark" or "reset")) { error = "stats takes nothing, mark or reset"; return false; }
                        cmd = new Command(Verb.Stats, text, Arg0: mode, Id: id);
                        return true;
                    }

                case Verb.Wait:
                case Verb.Expect:
                    {
                        int timeout = 0;
                        ReadOnlySpan<char> condition = rest;
                        if (n >= 4 && line[parts[n - 2]].Equals("timeout", StringComparison.OrdinalIgnoreCase))
                        {
                            if (verb == Verb.Expect) { error = "expect evaluates once and takes no timeout"; return false; }
                            if (!TryParseDuration(line[parts[n - 1]], out timeout) || timeout <= 0) { error = "bad timeout '" + line[parts[n - 1]].ToString() + "'"; return false; }
                            condition = line[parts[1].Start.Value..parts[n - 2].Start.Value].Trim();
                        }
                        if (!TryParseCondition(condition, out Condition cond, out error)) return false;
                        cmd = new Command(verb, text, Arg0: condition.ToString(), TimeoutMs: timeout, Cond: cond, Id: id, Soft: soft);
                        return true;
                    }

                case Verb.Sleep:
                    {
                        if (argc != 1 || !TryParseDuration(line[parts[1]], out int ms)) { error = "sleep wants a duration (ms, 5s, 1500ms)"; return false; }
                        cmd = new Command(Verb.Sleep, text, Int0: ms, Id: id);
                        return true;
                    }

                case Verb.Log:
                    if (rest.IsEmpty) { error = "log wants some text"; return false; }
                    cmd = new Command(Verb.Log, text, Arg0: rest.ToString(), Id: id);
                    return true;

                case Verb.Quit:
                    {
                        int code = -1;
                        if (argc > 1 || (argc == 1 && (!int.TryParse(line[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out code) || code > 255)))
                        { error = "quit takes an optional exit code 0-255"; return false; }
                        cmd = new Command(Verb.Quit, text, Int0: code, Id: id);
                        return true;
                    }
            }
            error = "unknown command '" + word + "'";
            return false;
        }

        static bool Fail(out int value) { value = 0; return false; }

        static bool TryOnOff(ReadOnlySpan<char> s, out bool on)
        {
            on = s.Equals("on", StringComparison.OrdinalIgnoreCase);
            return on || s.Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        static bool TryOnOffInt(ReadOnlySpan<char> s, out int value)
        {
            bool ok = TryOnOff(s, out bool on);
            value = on ? 1 : 0;
            return ok;
        }

        /// <summary>`spotify:&lt;kind&gt;:&lt;22 base62&gt;` (or the user-namespaced playlist spelling). Pure — the SHELL
        /// turns it into an EntityId on the loop thread, where interning is legal.</summary>
        static bool IsSpotifyUri(string uri, bool containers)
        {
            if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) return false;
            int last = uri.LastIndexOf(':');
            ReadOnlySpan<char> id = uri.AsSpan(last + 1);
            if (id.Length != 22) return false;
            foreach (char c in id) if (!char.IsAsciiLetterOrDigit(c)) return false;
            ReadOnlySpan<char> head = uri.AsSpan(0, last);
            ReadOnlySpan<char> kind = head[(head.LastIndexOf(':') + 1)..];
            return kind is "track" or "episode" || (containers && kind is "album" or "playlist");
        }

        /// <summary>`ms`, `m:ss`, `m:ss.fff`, `h:mm:ss`, `90s`, `+5s`, `-10s`, `+1500ms`. Relative forms set the flag; the
        /// SHELL adds them to the snapshot's position at post time. The `end-10s` form is refused here — see the overload.</summary>
        public static bool TryParsePosition(ReadOnlySpan<char> s, out int ms, out bool relative)
            => TryParsePosition(s, out ms, out relative, out bool fromEnd) && !fromEnd;

        /// <summary>The same, plus `end-10s` (§8 Q7): <paramref name="fromEnd"/> set and <paramref name="ms"/> ≤ 0, resolved
        /// against DurationMs at post time.</summary>
        public static bool TryParsePosition(ReadOnlySpan<char> s, out int ms, out bool relative, out bool fromEnd)
        {
            ms = 0; relative = false; fromEnd = false;
            s = s.Trim();
            if (s.IsEmpty) return false;
            if (s.StartsWith("end", StringComparison.OrdinalIgnoreCase))
            {
                fromEnd = true;
                s = s[3..];
                if (s.IsEmpty) return true;
                if (s[0] != '-' || !TryParseDuration(s[1..], out int back)) return false;
                ms = -back;
                return true;
            }
            int sign = s[0] == '+' ? 1 : s[0] == '-' ? -1 : 0;
            if (sign != 0) { relative = true; s = s[1..]; }
            if (!TryParseDuration(s, out int d)) return false;
            ms = sign < 0 ? -d : d;
            return true;
        }

        /// <summary>A non-negative duration: `1500` (ms), `1500ms`, `5s`, `1.5s`, `m:ss(.fff)`, `h:mm:ss(.fff)`.</summary>
        public static bool TryParseDuration(ReadOnlySpan<char> s, out int ms)
        {
            ms = 0;
            s = s.Trim();
            if (s.IsEmpty) return false;
            long total;
            if (s.IndexOf(':') >= 0)
            {
                Span<Range> f = stackalloc Range[4];
                int n = s.Split(f, ':');
                if (n is < 2 or > 3) return false;
                if (!long.TryParse(s[f[0]], NumberStyles.None, CultureInfo.InvariantCulture, out long minutes)) return false;
                if (n == 3)
                {
                    if (!long.TryParse(s[f[1]], NumberStyles.None, CultureInfo.InvariantCulture, out long m) || m >= 60) return false;
                    minutes = minutes * 60 + m;
                }
                if (!TrySeconds(s[f[n - 1]], out long secMs) || secMs >= 60_000) return false;
                total = minutes * 60_000 + secMs;
            }
            else if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(s[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
            }
            else if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySeconds(s[..^1], out total)) return false;
            }
            else if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
            if (total > int.MaxValue) return false;
            ms = (int)total;
            return true;
        }

        /// <summary>`30`, `05`, `30.250`, `1.5` → milliseconds (at most three fraction digits).</summary>
        static bool TrySeconds(ReadOnlySpan<char> s, out long ms)
        {
            ms = 0;
            int dot = s.IndexOf('.');
            ReadOnlySpan<char> whole = dot < 0 ? s : s[..dot];
            if (whole.IsEmpty || !long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) return false;
            long fraction = 0;
            if (dot >= 0)
            {
                ReadOnlySpan<char> frac = s[(dot + 1)..];
                if (frac.IsEmpty || frac.Length > 3) return false;
                foreach (char c in frac) { if (!char.IsAsciiDigit(c)) return false; fraction = fraction * 10 + (c - '0'); }
                for (int i = frac.Length; i < 3; i++) fraction *= 10;
            }
            if (seconds > int.MaxValue / 1000) return false;
            ms = seconds * 1000 + fraction;
            return true;
        }

        /// <summary>0 normal · 1 high · 2 veryhigh · 3 lossless — Spotify.Audio.Quality's values (and the digits themselves).</summary>
        public static bool TryParseQuality(ReadOnlySpan<char> s, out int rung)
        {
            string w = s.Trim().ToString().ToLowerInvariant();
            rung = w switch { "normal" or "0" => 0, "high" or "1" => 1, "veryhigh" or "2" => 2, "lossless" or "3" => 3, _ => -1 };
            return rung >= 0;
        }

        // ── 4. conditions (§2.6) ────────────────────────────────────────────────────────────────────────────────────
        public enum Field : byte
        {
            Phase, Session, Buffering, Position, Track, Format, Next, Prefetched, CdnRequests, CdnInFlight, Xruns, GaplessExact, Owner,
            Seeked, SeekKind,
        }
        public enum Op : byte { Eq, Ne, Ge, Le, Gt, Lt, Is, IsNot }

        /// <summary>One clause; `&&` chains up to four (MaxClauses). Value is an int for numeric fields, a string for
        /// identity fields; RelativeToStart marks `position>=+5s`.</summary>
        public readonly record struct Clause(Field Field, Op Op, int Int, string Str, bool RelativeToStart);
        public readonly record struct Condition(Clause C0, Clause C1, Clause C2, Clause C3, byte Count)
        {
            public const int MaxClauses = 4;
            public bool IsEmpty => Count == 0;
        }

        static Clause ClauseAt(in Condition c, int i) => i switch { 0 => c.C0, 1 => c.C1, 2 => c.C2, _ => c.C3 };

        public static bool TryParseCondition(ReadOnlySpan<char> s, out Condition cond, out string error)
        {
            cond = default; error = "";
            s = s.Trim();
            if (s.IsEmpty) { error = "a condition is required"; return false; }
            if (s.IndexOf("||") >= 0) { error = "no || in a condition - write two steps"; return false; }
            Clause c0 = default, c1 = default, c2 = default, c3 = default;
            int count = 0;
            while (true)
            {
                int amp = s.IndexOf("&&");
                ReadOnlySpan<char> part = (amp < 0 ? s : s[..amp]).Trim();
                if (count == Condition.MaxClauses) { error = "at most " + Condition.MaxClauses + " clauses joined by &&"; return false; }
                if (!TryParseClause(part, out Clause c, out error)) return false;
                switch (count) { case 0: c0 = c; break; case 1: c1 = c; break; case 2: c2 = c; break; default: c3 = c; break; }
                count++;
                if (amp < 0) break;
                s = s[(amp + 2)..];
            }
            cond = new Condition(c0, c1, c2, c3, (byte)count);
            return true;
        }

        static readonly string[] SessionWords =
            ["offline", "resolving", "connecting", "handshaking", "authenticating", "minting", "online", "reconnecting", "failed"];

        static bool TryParseClause(ReadOnlySpan<char> part, out Clause clause, out string error)
        {
            clause = default; error = "";
            if (part.IsEmpty) { error = "an empty clause"; return false; }
            int opAt = part.IndexOfAny("=<>");
            int bang = part.IndexOf('!');
            if (bang > 0 && (opAt < 0 || bang < opAt)) opAt = bang;                   // `track!=…`
            if (opAt <= 0)
            {
                bool negated = part[0] == '!';
                string word = (negated ? part[1..] : part).Trim().ToString().ToLowerInvariant();
                Op flag = negated ? Op.IsNot : Op.Is;
                switch (word)
                {
                    case "buffering": clause = new Clause(Field.Buffering, flag, 0, "", false); return true;
                    case "next": clause = new Clause(Field.Next, flag, 0, "", false); return true;
                    case "prefetched": clause = new Clause(Field.Prefetched, flag, 0, "", false); return true;
                    case "seeked": clause = new Clause(Field.Seeked, flag, 0, "", false); return true;
                }
                if (!negated && word is "playing" or "paused" or "loading" or "idle" or "ended")
                { clause = new Clause(Field.Phase, Op.Eq, 0, word, false); return true; }
                if (!negated && Array.IndexOf(SessionWords, word) >= 0)
                { clause = new Clause(Field.Session, Op.Eq, 0, word, false); return true; }
                error = "unknown condition '" + part.ToString() + "'";
                return false;
            }

            string name = part[..opAt].Trim().ToString().ToLowerInvariant();
            ReadOnlySpan<char> tail = part[opAt..];
            Op op;
            int opLength = 2;
            if (tail.StartsWith("==")) op = Op.Eq;
            else if (tail.StartsWith("!=")) op = Op.Ne;
            else if (tail.StartsWith(">=")) op = Op.Ge;
            else if (tail.StartsWith("<=")) op = Op.Le;
            else if (tail[0] == '>') { op = Op.Gt; opLength = 1; }
            else if (tail[0] == '<') { op = Op.Lt; opLength = 1; }
            else { error = "bad operator in '" + part.ToString() + "' (== != >= <= > <)"; return false; }
            ReadOnlySpan<char> value = tail[opLength..].Trim();

            Field field;
            switch (name)
            {
                case "phase": field = Field.Phase; break;
                case "session": field = Field.Session; break;
                case "track": field = Field.Track; break;
                case "format": field = Field.Format; break;
                case "owner": field = Field.Owner; break;
                case "seek.kind": field = Field.SeekKind; break;
                case "position":
                    {
                        if (!TryParsePosition(value, out int ms, out bool relative, out bool fromEnd) || fromEnd)
                        { error = "bad position '" + value.ToString() + "' in a condition (ms, m:ss, 10s, +5s)"; return false; }
                        clause = new Clause(Field.Position, op, ms, "", relative);
                        return true;
                    }
                case "cdn.requests": field = Field.CdnRequests; goto numeric;
                case "cdn.inflight": field = Field.CdnInFlight; goto numeric;
                case "xruns": field = Field.Xruns; goto numeric;
                case "gapless.exact": field = Field.GaplessExact; goto numeric;
                default: error = "unknown field '" + name + "'"; return false;
            }
            if (op is not (Op.Eq or Op.Ne)) { error = "'" + name + "' compares with == or != only"; return false; }
            clause = new Clause(field, op, 0, value.ToString(), false);
            return true;

        numeric:
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            { error = "'" + name + "' wants an integer, not '" + value.ToString() + "'"; return false; }
            clause = new Clause(field, op, number, "", false);
            return true;
        }

        /// <summary>The ONE evaluator. <paramref name="start"/> is the snapshot the wait began with — what makes
        /// `next`, `ended` and relative positions mean something; for `expect` it is the same as <paramref name="now"/>.
        /// Deltas (cdn.requests, xruns, gapless.exact, seeked) are against <paramref name="mark"/>, the last `stats mark`.</summary>
        public static bool Holds(in Condition cond, in StatusSnapshot now, in StatusSnapshot start, in StatusSnapshot mark)
        {
            for (int i = 0; i < cond.Count; i++)
            {
                Clause c = ClauseAt(in cond, i);
                if (!Holds(in c, in now, in start, in mark)) return false;
            }
            return true;
        }

        static bool Holds(in Clause c, in StatusSnapshot now, in StatusSnapshot start, in StatusSnapshot mark) => c.Field switch
        {
            Field.Phase => string.Equals(c.Str, "ended", StringComparison.OrdinalIgnoreCase)
                               ? (Same(now.Phase, "Ended") || (!string.IsNullOrEmpty(start.TrackUri) && !SameTrack(now.TrackUri, start.TrackUri))) == (c.Op == Op.Eq)
                               : Compare(now.Phase, c),
            Field.Session => Compare(now.SessionPhase, c),
            Field.Buffering => Flag(c, now.Buffering),
            Field.Position => Compare(now.PositionMs, c.RelativeToStart ? start.PositionMs + c.Int : c.Int, c.Op),
            Field.Track => (c.Op == Op.Eq) == SameTrack(now.TrackUri, c.Str),
            Field.Format => Compare(FoldFormat(now.Format), c),
            Field.Next => Flag(c, !string.IsNullOrEmpty(now.TrackUri) && !SameTrack(now.TrackUri, start.TrackUri)),
            Field.Prefetched => Flag(c, now.PrepareArmed),
            Field.CdnRequests => Compare((int)(now.CdnRequests - mark.CdnRequests), c.Int, c.Op),
            Field.CdnInFlight => Compare(now.CdnInFlight, c.Int, c.Op),
            Field.Xruns => Compare(now.Xruns - mark.Xruns, c.Int, c.Op),
            Field.GaplessExact => Compare(now.GaplessExact - mark.GaplessExact, c.Int, c.Op),
            Field.Owner => Compare(now.Owner, c),
            Field.Seeked => Flag(c, now.LastSeekMs != mark.LastSeekMs || now.LastSeekLatencyMs != mark.LastSeekLatencyMs),
            Field.SeekKind => Compare(SeekKindName(now.LastSeekKind), c),
            _ => false,
        };

        static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        static bool SameTrack(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);   // base62 is case-sensitive
        static bool Flag(in Clause c, bool value) => c.Op == Op.IsNot ? !value : value;
        static bool Compare(string? actual, in Clause c) => c.Op == Op.Ne ? !Same(actual, c.Str) : Same(actual, c.Str);

        static bool Compare(int actual, int expected, Op op) => op switch
        {
            Op.Eq => actual == expected, Op.Ne => actual != expected, Op.Ge => actual >= expected,
            Op.Le => actual <= expected, Op.Gt => actual > expected, Op.Lt => actual < expected, _ => false,
        };

        /// <summary>Audio.Metrics' seek kind: 0 ring · 1 far · 2 disk.</summary>
        public static string SeekKindName(byte kind) => kind switch { 0 => "ring", 1 => "far", 2 => "disk", _ => "unknown" };

        /// <summary>"OGG 320" → ogg320, "FLAC 24/44.1" → flac24, "FLAC 24-bit" → flac24, "FLAC 16/44.1" / "FLAC" → flac,
        /// "MP3" → mp3 — the badge the pump reports (`Playback.Audio.LabelFor`) folded to the script's vocabulary.</summary>
        public static string FoldFormat(string? badge)
        {
            if (string.IsNullOrWhiteSpace(badge)) return "";
            ReadOnlySpan<char> b = badge.AsSpan().Trim();
            if (b.StartsWith("flac", StringComparison.OrdinalIgnoreCase))
            {
                int run = 0;
                bool digits = false;
                for (int i = 4; i <= b.Length; i++)
                {
                    if (i < b.Length && char.IsAsciiDigit(b[i])) { run = run * 10 + (b[i] - '0'); digits = true; continue; }
                    if (digits && run == 24) return "flac24";                        // the bit depth, wherever it is written
                    run = 0;
                    digits = false;
                }
                return "flac";
            }
            return badge.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        }

        // ── 5. the script runner — a state machine advanced by the tick (§2.4) ──────────────────────────────────────
        public enum StepState : byte { Pending, Waiting, Sleeping, Done, Failed }

        /// <summary>What the runner asks the SHELL to do this tick. One value per tick, never a list: a script is
        /// sequential by definition.</summary>
        public readonly record struct ScriptAction(Verb Verb, Command Cmd, bool Finished, int ExitCode);

        /// <summary>One finished step — the `step` line (a script) or the `reply` line (stdin / pipe).</summary>
        public readonly record struct StepResult(int N, int Line, bool Ok, bool Soft, string Cmd, long ElapsedMs, int Code, string Reason, int Id);

        public sealed class Script
        {
            readonly List<Command> _steps;
            readonly List<int> _lines;
            readonly List<StepResult> _results = new(4);
            readonly bool _interactive;
            int _index, _lastAction = -1, _executed, _failed, _faulted, _softFailed, _quitCode = -1;
            StepState _state;
            long _deadlineMs, _sleepUntilMs, _startedMs;
            StatusSnapshot _start = StatusSnapshot.Empty, _mark = StatusSnapshot.Empty;
            bool _inputClosed;

            public int StepCount => _steps.Count;
            public int Index => _index;
            /// <summary>Hard failures (assertions + faults); a soft step's failure is <see cref="SoftFailed"/>.</summary>
            public int Failed => _failed + _faulted;
            public int Faulted => _faulted;
            public int SoftFailed => _softFailed;
            /// <summary>Steps that produced a result.</summary>
            public int Executed => _executed;
            public bool IsInteractive => _interactive;
            /// <summary>The snapshot the last `stats mark` took (<see cref="StatusSnapshot.Empty"/> until one did).</summary>
            public StatusSnapshot Mark => _mark;

            public Script(Command[] steps) : this(steps, null, interactive: false) { }

            Script(Command[] steps, int[]? lines, bool interactive)
            {
                _steps = new List<Command>(steps.Length);
                _lines = new List<int>(steps.Length);
                for (int i = 0; i < steps.Length; i++)
                {
                    if (steps[i].IsNone) continue;
                    _steps.Add(steps[i]);
                    _lines.Add(lines is null ? 0 : lines[i]);
                }
                _interactive = interactive;
            }

            /// <summary>A runner fed line by line (stdin, the named pipe): it never stops at a failure, it waits for
            /// more input at the end of the queue, and it finishes when <see cref="CloseInput"/> has been called and the
            /// queue is drained.</summary>
            public static Script Interactive() => new([], null, interactive: true);

            public void Append(in Command cmd)
            {
                if (cmd.IsNone) return;
                _steps.Add(cmd);
                _lines.Add(0);
            }

            public void CloseInput() => _inputClosed = true;

            /// <summary>Parse a whole file. A parse error anywhere is a usage error BEFORE anything boots (§2.5, 64).</summary>
            public static bool TryLoad(ReadOnlySpan<char> text, out Script script, out int badLine, out string error)
            {
                script = null!; badLine = 0; error = "";
                if (!text.IsEmpty && text[0] == '\uFEFF') text = text[1..];          // a BOM an editor left behind
                var steps = new List<Command>();
                var lines = new List<int>();
                int lineNo = 0;
                while (!text.IsEmpty)
                {
                    lineNo++;
                    int nl = text.IndexOf('\n');
                    ReadOnlySpan<char> line = (nl < 0 ? text : text[..nl]).TrimEnd('\r');
                    text = nl < 0 ? default : text[(nl + 1)..];
                    if (!TryParse(line, out Command cmd, out error)) { badLine = lineNo; return false; }
                    if (cmd.IsNone) continue;
                    steps.Add(cmd);
                    lines.Add(lineNo);
                }
                script = new Script(steps.ToArray(), lines.ToArray(), interactive: false);
                return true;
            }

            /// <summary>Advance by one tick. The SHELL executes the returned verb (a Play post, a Status print), then
            /// on the NEXT tick the runner sees the new snapshot. `wait`/`sleep` return Verb.None until satisfied.
            /// A fault in the snapshot (Playback.Fault != None, session Failed) fails a waiting step with code 1; a
            /// timeout or a false `expect` fails it with code 2. A script stops at the first failure — the verdict line
            /// names the step — unless the step was written `wait? …` / `expect? …` (soft: recorded, continues), which is
            /// how a public-only build's FLAC script records NoDeriver without aborting.</summary>
            public ScriptAction Tick(in StatusSnapshot now)
            {
                _lastAction = -1;
                if (_index >= _steps.Count)
                {
                    if (_interactive && !_inputClosed && _quitCode < 0) return NoAction;
                    return new ScriptAction(Verb.None, default, true, _quitCode >= 0 ? _quitCode : Verdict());
                }
                Command cmd = _steps[_index];
                switch (_state)
                {
                    case StepState.Pending:
                        _start = now;
                        _startedMs = now.NowMs;
                        switch (cmd.Verb)
                        {
                            case Verb.Wait:
                                _state = StepState.Waiting;
                                _deadlineMs = now.NowMs + (cmd.TimeoutMs > 0 ? cmd.TimeoutMs : DefaultWaitMs);
                                return Check(in now);
                            case Verb.Sleep:
                                _state = StepState.Sleeping;
                                _sleepUntilMs = now.NowMs + cmd.Int0;
                                if (cmd.Int0 <= 0) Finish(now.NowMs, true, 0, "");
                                return NoAction;
                            case Verb.Expect:
                                {
                                    bool holds = Holds(cmd.Cond, in now, in now, in _mark);
                                    Finish(now.NowMs, holds, holds ? 0 : ExitCode.Assertion, holds ? "" : "false: " + cmd.Arg0);
                                    return NoAction;
                                }
                            case Verb.Quit:
                                {
                                    Finish(now.NowMs, true, 0, "");
                                    _quitCode = cmd.Int0 >= 0 ? cmd.Int0 : Verdict();
                                    _index = _steps.Count;
                                    return new ScriptAction(Verb.Quit, cmd, true, _quitCode);
                                }
                            case Verb.Stats:
                                if (cmd.Arg0 == "mark") _mark = now;
                                else if (cmd.Arg0 == "reset") _mark = StatusSnapshot.Empty;
                                break;
                        }
                        int executing = _index;
                        Finish(now.NowMs, true, 0, "");
                        _lastAction = executing;
                        return new ScriptAction(cmd.Verb, cmd, false, 0);            // the SHELL executes it this tick

                    case StepState.Waiting:
                        return Check(in now);

                    case StepState.Sleeping:
                        if (now.NowMs >= _sleepUntilMs) Finish(now.NowMs, true, 0, "");
                        return NoAction;
                }
                return NoAction;
            }

            ScriptAction Check(in StatusSnapshot now)
            {
                Command cmd = _steps[_index];
                if (Holds(cmd.Cond, in now, in _start, in _mark)) { Finish(now.NowMs, true, 0, ""); return NoAction; }
                if (IsFault(in now)) { Finish(now.NowMs, false, ExitCode.Fault, FaultReason(in now)); return NoAction; }
                if (now.NowMs >= _deadlineMs)
                {
                    int timeout = cmd.TimeoutMs > 0 ? cmd.TimeoutMs : DefaultWaitMs;
                    Finish(now.NowMs, false, ExitCode.Assertion, "timed out after " + timeout.ToString(CultureInfo.InvariantCulture) + " ms: " + cmd.Arg0);
                }
                return NoAction;
            }

            static bool IsFault(in StatusSnapshot s)
                => (!string.IsNullOrEmpty(s.Fault) && s.Fault != "None") || s.SessionPhase == "Failed";

            static string FaultReason(in StatusSnapshot s)
                => !string.IsNullOrEmpty(s.Fault) && s.Fault != "None" ? "playback fault " + s.Fault : "session failed (" + s.SessionFault + ")";

            /// <summary>The SHELL could not execute the step this tick's action named (a uri with no row, a seek before
            /// the duration is known, a play while offline). Call it BEFORE draining the results: it turns that step's
            /// result into a failure, and a file script stops exactly as it does for a failed wait.</summary>
            public void Reject(string reason, int code = ExitCode.Assertion)
            {
                if (_lastAction < 0) return;
                int n = _lastAction + 1;
                if (code == ExitCode.Fault) _faulted++; else _failed++;
                bool replaced = false;
                for (int i = _results.Count - 1; i >= 0; i--)
                {
                    if (_results[i].N != n) continue;
                    _results[i] = _results[i] with { Ok = false, Code = code, Reason = reason };
                    replaced = true;
                    break;
                }
                if (!replaced)
                {
                    Command cmd = _steps[_lastAction];
                    _results.Add(new StepResult(n, _lines[_lastAction], false, false, cmd.Text ?? "", 0, code, reason, cmd.Id));
                }
                _lastAction = -1;
                if (!_interactive) { _index = _steps.Count; _state = StepState.Pending; }
            }

            /// <summary>The next finished step, oldest first. At most a couple per tick.</summary>
            public bool TryTakeResult(out StepResult result)
            {
                if (_results.Count == 0) { result = default; return false; }
                result = _results[0];
                _results.RemoveAt(0);
                return true;
            }

            void Finish(long nowMs, bool ok, int code, string reason)
            {
                Command cmd = _steps[_index];
                if (!ok)
                {
                    if (cmd.Soft) _softFailed++;
                    else if (code == ExitCode.Fault) _faulted++;
                    else _failed++;
                }
                _results.Add(new StepResult(_index + 1, _lines[_index], ok, cmd.Soft, cmd.Text ?? "", nowMs - _startedMs, ok ? 0 : code, reason, cmd.Id));
                _executed++;
                _state = StepState.Pending;
                _index++;
                if (!ok && !cmd.Soft && !_interactive) _index = _steps.Count;     // a file script stops at its first failure
            }

            public int Verdict() => _faulted > 0 ? ExitCode.Fault : _failed > 0 ? ExitCode.Assertion : ExitCode.Ok;

            public const int DefaultWaitMs = 30_000;
            static readonly ScriptAction NoAction = new(Verb.None, default, false, 0);
        }

        // ── 6. JSON lines (§2.5) — the shape is pinned by a test, the writer never builds a DOM ─────────────────────
        public static class JsonLine
        {
            static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            [ThreadStatic] static ArrayBufferWriter<byte>? t_buffer;
            [ThreadStatic] static Utf8JsonWriter? t_writer;

            static Utf8JsonWriter Begin(long t, string kind)
            {
                ArrayBufferWriter<byte> buffer = t_buffer ??= new ArrayBufferWriter<byte>(512);
                buffer.ResetWrittenCount();
                Utf8JsonWriter w = t_writer ??= new Utf8JsonWriter(buffer, WriterOptions);
                w.Reset(buffer);
                w.WriteStartObject();
                w.WriteNumber("t", t);
                w.WriteString("kind", kind);
                return w;
            }

            static string End(Utf8JsonWriter w)
            {
                w.WriteEndObject();
                w.Flush();
                return Encoding.UTF8.GetString(t_buffer!.WrittenSpan);
            }

            static float Finite(float v) => float.IsFinite(v) ? v : 0f;

            /// <summary>The account is REDACTED here, whatever the caller passed (`Platform.Redact`); the credential is its
            /// scheme tag only.</summary>
            public static string Boot(long t, string profile, string scheme, string account, bool store, string endpoint)
            {
                var w = Begin(t, "boot");
                w.WriteString("profile", profile ?? "");
                w.WriteString("credential", scheme ?? "");
                w.WriteString("account", Platform.Redact(account));
                w.WriteBoolean("store", store);
                w.WriteString("endpoint", endpoint ?? "");
                return End(w);
            }

            public static string Session(long t, string phase, string tier = "", string country = "", string fault = "")
            {
                var w = Begin(t, "session");
                w.WriteString("phase", phase ?? "");
                if (!string.IsNullOrEmpty(tier)) w.WriteString("tier", tier);
                if (!string.IsNullOrEmpty(country)) w.WriteString("country", country);
                if (!string.IsNullOrEmpty(fault) && fault != "None") w.WriteString("fault", fault);
                return End(w);
            }

            public static string State(long t, in StatusSnapshot s)
            {
                var w = Begin(t, "state");
                WritePlayback(w, in s);
                return End(w);
            }

            /// <summary>The `status` answer: the session and the playback in one line (go-librespot's `/status` shape).</summary>
            public static string Status(long t, in StatusSnapshot s, int id = -1)
            {
                var w = Begin(t, "status");
                if (id >= 0) w.WriteNumber("id", id);
                w.WriteString("session", s.SessionPhase ?? "");
                if (!string.IsNullOrEmpty(s.SessionFault) && s.SessionFault != "None") w.WriteString("sessionFault", s.SessionFault);
                w.WriteString("tier", s.Tier ?? "");
                w.WriteString("country", s.Country ?? "");
                WritePlayback(w, in s);
                w.WriteString("owner", s.Owner ?? "");
                w.WriteNumber("volume", Finite(s.Volume));
                w.WriteNumber("loadEpoch", s.LoadEpoch);
                w.WriteBoolean("prefetched", s.PrepareArmed);
                return End(w);
            }

            static void WritePlayback(Utf8JsonWriter w, in StatusSnapshot s)
            {
                w.WriteString("phase", s.Phase ?? "");
                w.WriteString("track", s.TrackUri ?? "");
                w.WriteNumber("pos", s.PositionMs);
                w.WriteNumber("dur", s.DurationMs);
                w.WriteString("format", s.Format ?? "");
                w.WriteBoolean("buffering", s.Buffering);
                if (!string.IsNullOrEmpty(s.Fault) && s.Fault != "None") w.WriteString("fault", s.Fault);
            }

            public static string Reply(long t, int id, bool ok, string cmd, string error = "")
            {
                var w = Begin(t, "reply");
                w.WriteNumber("id", id);
                w.WriteBoolean("ok", ok);
                w.WriteString("cmd", cmd ?? "");
                if (!string.IsNullOrEmpty(error)) w.WriteString("error", error);
                return End(w);
            }

            public static string Step(long t, int n, bool ok, string cmd, long elapsedMs, int code, string reason = "", bool soft = false, int line = 0)
            {
                var w = Begin(t, "step");
                w.WriteNumber("n", n);
                if (line > 0) w.WriteNumber("line", line);
                w.WriteBoolean("ok", ok);
                if (soft) w.WriteBoolean("soft", true);
                w.WriteString("cmd", cmd ?? "");
                w.WriteNumber("elapsedMs", elapsedMs);
                if (!ok) w.WriteNumber("code", code);
                if (!string.IsNullOrEmpty(reason)) w.WriteString("reason", reason);
                return End(w);
            }

            public static string Step(long t, in StepResult r) => Step(t, r.N, r.Ok, r.Cmd, r.ElapsedMs, r.Code, r.Reason, r.Soft, r.Line);

            /// <summary>The counters, as DELTAS against <paramref name="mark"/> where a delta is the honest number (requests,
            /// xruns, gapless joins) and as the live value where it is a level (in flight, ping, throughput).</summary>
            public static string Stats(long t, in StatusSnapshot s, in StatusSnapshot mark, int id = -1)
            {
                var w = Begin(t, "stats");
                if (id >= 0) w.WriteNumber("id", id);
                w.WriteStartObject("cdn");
                w.WriteNumber("requests", s.CdnRequests - mark.CdnRequests);
                w.WriteNumber("cancelled", s.CdnCancelled - mark.CdnCancelled);
                w.WriteNumber("inflight", s.CdnInFlight);
                w.WriteNumber("peakInFlight", s.CdnPeakInFlight);
                w.WriteNumber("pingMs", s.CdnPingMs);
                w.WriteNumber("kbps", s.CdnBytesPerSecond * 8 / 1000);
                w.WriteNumber("headBytes", s.HeadBytes);
                w.WriteNumber("heads", s.CdnHeads - mark.CdnHeads);
                w.WriteNumber("resolves", s.CdnResolves - mark.CdnResolves);
                w.WriteNumber("cacheHits", s.CdnCacheHits - mark.CdnCacheHits);
                w.WriteEndObject();
                w.WriteStartObject("ring");
                w.WriteNumber("waits", s.RingWaits - mark.RingWaits);
                w.WriteNumber("starves", s.RingStarves - mark.RingStarves);
                w.WriteEndObject();
                w.WriteStartObject("audio");
                w.WriteNumber("xruns", s.Xruns - mark.Xruns);
                w.WriteNumber("firstAudioMs", s.FirstAudioMs);
                w.WriteString("firstAudioFrom", s.FirstAudioFromHead ? "head" : "body");
                w.WriteStartObject("lastSeek");
                w.WriteNumber("toMs", s.LastSeekMs);
                w.WriteNumber("latencyMs", s.LastSeekLatencyMs);
                w.WriteString("kind", SeekKindName(s.LastSeekKind));
                w.WriteEndObject();
                w.WriteStartObject("gapless");
                w.WriteNumber("exact", s.GaplessExact - mark.GaplessExact);
                w.WriteNumber("degraded", s.GaplessDegraded - mark.GaplessDegraded);
                w.WriteNumber("abandoned", s.GaplessAbandoned - mark.GaplessAbandoned);
                w.WriteEndObject();
                w.WriteNumber("decodeXRealtime", Finite(s.DecodeXRealtime));
                w.WriteEndObject();
                return End(w);
            }

            public static string Seek(long t, in StatusSnapshot s)
            {
                var w = Begin(t, "seek");
                w.WriteNumber("to", s.LastSeekMs);
                w.WriteNumber("latencyMs", s.LastSeekLatencyMs);
                w.WriteString("seekKind", SeekKindName(s.LastSeekKind));
                w.WriteString("track", s.TrackUri ?? "");
                return End(w);
            }

            public static string Gapless(long t, in StatusSnapshot now, in StatusSnapshot previous)
            {
                var w = Begin(t, "gapless");
                w.WriteNumber("exact", now.GaplessExact - previous.GaplessExact);
                w.WriteNumber("degraded", now.GaplessDegraded - previous.GaplessDegraded);
                w.WriteString("track", now.TrackUri ?? "");
                return End(w);
            }

            public static string Underrun(long t, int total, int delta)
            {
                var w = Begin(t, "underrun");
                w.WriteNumber("xruns", total);
                w.WriteNumber("new", delta);
                return End(w);
            }

            public static string EndOfQueue(long t, string track)
            {
                var w = Begin(t, "end");
                w.WriteString("track", track ?? "");
                return End(w);
            }

            public static string Verdict(long t, bool ok, int steps, int failed, int code, int softFailed = 0)
            {
                var w = Begin(t, "verdict");
                w.WriteBoolean("ok", ok);
                w.WriteNumber("steps", steps);
                w.WriteNumber("failed", failed);
                if (softFailed > 0) w.WriteNumber("softFailed", softFailed);
                w.WriteNumber("code", code);
                w.WriteString("meaning", ExitCode.Describe(code));
                return End(w);
            }

            public static string Fault(long t, string reason, string hint)
            {
                var w = Begin(t, "fault");
                w.WriteString("reason", reason ?? "");
                if (!string.IsNullOrEmpty(hint)) w.WriteString("hint", hint);
                return End(w);
            }

            /// <summary>An echoed Log ring entry (`--echo-log`): the `audio.*` timeline lines are the measurements until the
            /// counter seams carry every number (§2.7).</summary>
            public static string Echo(long t, string category, string line)
            {
                var w = Begin(t, "echo");
                w.WriteString("category", category ?? "");
                w.WriteString("line", line ?? "");
                return End(w);
            }
        }

        // ── 7. the two store wrappers (§2.8) ───────────────────────────────────────────────────────────────────────
        /// <summary>Reads fall through; writes stay here. A headless `quality lossless` never reaches HKCU. Locked: the
        /// audio pump reads the quality rung on its own thread while the loop writes it.</summary>
        public sealed class OverlaySettings(IAppSettings inner) : IAppSettings
        {
            readonly Dictionary<string, object> _writes = new(StringComparer.Ordinal);
            readonly Lock _gate = new();

            public T Get<T>(SettingKey<T> key)
            {
                lock (_gate)
                    if (_writes.TryGetValue(key.Name, out object? v) && v is T t) return t;
                return inner.Get(key);
            }

            public void Set<T>(SettingKey<T> key, T value)
            {
                if (value is null) return;
                lock (_gate) _writes[key.Name] = value;
            }

            public int WriteCount { get { lock (_gate) return _writes.Count; } }
        }

        /// <summary>The credential blob may be read and refreshed but never removed; the device id is namespaced so the
        /// headless run is its own Connect device. `Platform.CredentialKey` is the one literal shared (Platform.cs:827).</summary>
        public sealed class ProtectedLocalStore(ILocalStore inner, string deviceIdSuffix, Action<string>? refused = null) : ILocalStore
        {
            const string DeviceIdKey = "device.id";
            string Map(string key) => key == DeviceIdKey ? DeviceIdKey + "." + deviceIdSuffix : key;
            public string? Get(string key) => inner.Get(Map(key));
            public void Set(string key, string value) => inner.Set(Map(key), value);
            public void Remove(string key)
            {
                if (key == Platform.CredentialKey) { refused?.Invoke(key); return; }   // never on a headless run (§2.8 rule 2)
                inner.Remove(Map(key));
            }
        }

        // ── 8. the context queue (§2.4 `play <album|playlist>`) ─────────────────────────────────────────────────────
        /// <summary>Rows in reading order for Queue.Replace: the chosen row as NowPlaying, the rest as NextUp — the
        /// bucket invariant Queue.Replace asserts (Queue.cs:337). Pure over spans; the SHELL fills `members` from the
        /// album/playlist edge once Entities says the edge is known. Owner Q's Wave-5 page will need this same fold and
        /// should take it into Queue.cs then; it is here so the smoke does not wait for Wave 5. Returns how many rows
        /// were written (bounded by both destination spans).</summary>
        public static int BuildContextQueue(ReadOnlySpan<EntityRef> members, int startIndex, Span<EntityRef> refs, Span<QueueEdge> rows)
        {
            if ((uint)startIndex >= (uint)members.Length) return 0;
            int n = 0;
            for (int i = startIndex; i < members.Length && n < refs.Length && n < rows.Length; i++)
            {
                refs[n] = members[i];
                rows[n] = new QueueEdge(0, (byte)QueueProvider.Context, (byte)(i == startIndex ? QueueBucket.NowPlaying : QueueBucket.NextUp));
                n++;
            }
            return n;
        }
    }
}
