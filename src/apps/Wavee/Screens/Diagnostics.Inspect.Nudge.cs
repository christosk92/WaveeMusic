// ── Screens/Diagnostics.Inspect.Nudge.cs ───────────────────────────────────────────────────────────────────────────────
// the `nudge` verb: read or drive the rate/star nudge. Load a named fixture (a ledger, a clock, a channel), pin the four facts a
// headless run cannot have (the calendar, the window being in front, the Store dialog, the browser), fire a track boundary, run
// the once-a-second check, and read back the decision with every check, the ledger, the card and what was launched. `expect*`
// turns a mismatch into ok:false, so `inspect.py --gate error` fails the run as `lint` and `crossing` do.
//
// Role: SHELL (UI thread; decides nothing: every verdict is RateNudge.Decide's)
// Owner: R
// Budget: 300 lines
// Spec: docs/plans/wavee/rate-nudge-implementation.md §8 (grammar, seams, fixtures, reply) and §9 (the scenarios)
//
// THE PATH IS PRODUCTION'S. `fire` calls RateNudge.OnTrackBoundary (the playback effect's own entry), `tick` calls RateNudge.Tick
// (the layer's interval), buttons are `click`ed, doors run through `setting`. The only fakes are the Seams record below.
// ORDER inside one command: raw, fixture, channel/session/policy/sessionAge/starsCache, clock, trouble, window/store/link/stars,
// reload, refresh, fire, tick, then the reply and the expect* gates. `mount` ends by putting the `fresh` fixture back.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        // What the verb pins. The Seams closures read these fields, so a later command changes them with no re-seed.
        TimeSpan _nClock;                                                   // calendar time = the seed epoch + this
        RateNudge.WindowFact _nWindow = RateNudge.WindowFact.Front;
        RateNudge.StoreAnswer _nStore = RateNudge.StoreAnswer.Succeeded;
        bool _nLinkOk = true;
        int? _nStarsAnswer;                                                 // null: the fetch fails
        int _nFetches;
        readonly List<string> _nLaunched = [];
        RateNudge.Policy _nPolicy = RateNudge.Policy.Default;
        InstallChannel _nChannel = InstallChannel.Store;
        bool _nUpdated;
        long _nBootMono;                                                    // MonoMs at the session's start, for a re-seed that keeps its age
        RateNudge.Seams? _nSeams;

        static DateTimeOffset NudgeBase => DateTimeOffset.FromUnixTimeSeconds(Platform.Clock.SeedEpoch);
        DateTimeOffset NudgeNow() => NudgeBase + _nClock;

        RateNudge.Seams NudgeSeams() => _nSeams ??= new RateNudge.Seams(
            Now: NudgeNow,
            MonoMs: static () => FrameTime.NowMs,
            // The session's own host: a headless mount is not FluentApp's window, so FluentApp's relay would always say "no input".
            // Its clock is the OS uptime clock plus `window advance=` (the cold-maintenance clock), so a scenario advances it there.
            IdleMs: () => _shell?.Host.MsSinceInteraction ?? -1,
            Window: () => _nWindow,
            StoreDialog: (_, _) => Task.FromResult(_nStore),
            Launch: url => { lock (_nLaunched) _nLaunched.Add(url); return _nLinkOk; },
            FetchStars: _ => { Interlocked.Increment(ref _nFetches); return Task.FromResult(_nStarsAnswer); });

        void NudgePins()
        {
            _nClock = TimeSpan.Zero; _nWindow = RateNudge.WindowFact.Front; _nStore = RateNudge.StoreAnswer.Succeeded; _nLinkOk = true;
            _nStarsAnswer = null; Volatile.Write(ref _nFetches, 0);
            lock (_nLaunched) _nLaunched.Clear();
        }

        /// <summary>The one door into the host: seams, policy, ledger, channel, session and star cache, all at once.</summary>
        void NudgeReseed(RateNudge.Ledger ledger, long ageMs, RateNudge.Stars? stars)
        {
            long mono = FrameTime.NowMs;
            _nBootMono = mono - ageMs;
            Platform.Settings.Set(Platform.Keys.RateNudgeStars, stars is { } s ? RateNudge.EncodeStars(s) : "");
            RateNudge.ResetForInspect(NudgeSeams(), _nPolicy, ledger, _nChannel, _nUpdated, ageMs, stars);
        }

        /// <summary>`mount` (the end of MountShell): the `fresh` fixture on the inspector seams, so no scenario inherits the last one.</summary>
        void NudgeResetForMount()
        {
            NudgePins();
            _nPolicy = RateNudge.Policy.Default; _nChannel = InstallChannel.Store; _nUpdated = false;
            NudgeReseed(RateNudge.Seed(RateNudge.Ledger.Fresh, NudgeNow(), false, _nPolicy), 0, null);
        }

        // ── fixtures (§8.3) ─────────────────────────────────────────────────────────────────────────────────────────

        static readonly string[] s_nudgeFixtures =
            ["fresh", "day3", "eligible", "short-days", "short-hours", "asked-60", "asked-130", "asked-130-quiet", "capped", "rated", "starred",
             "opted-out", "troubled", "troubled-old", "milestone-99", "milestone-100", "existing", "existing-lapsed", "existing-later", "existing-capped"];

        static RateNudge.Ledger NudgeFixture(string f, DateTimeOffset now, RateNudge.Policy p, out InstallChannel channel, out bool updated)
        {
            channel = f == "starred" ? InstallChannel.Sideload : InstallChannel.Store;
            updated = f == "existing";
            static RateNudge.Ledger Played(DateTimeOffset now, double clockDays, double hours, int days) => new()
            {
                ClockStart = now.AddDays(-clockDays), CountedSince = now.AddDays(-clockDays), ListenSeconds = hours * 3600, ListenDays = days,
            };
            RateNudge.Ledger Asked(double clockDays, double lastAskDays, double atAsk, double hours, RateNudge.Ledger? over = null)
                => (over ?? Played(now, clockDays, hours, 30)) with
                {
                    Asks = 1, Last = RateNudge.Outcome.NotNow, LastAsk = now.AddDays(-lastAskDays), HoursAtLastAsk = atAsk,
                };
            var eligible = Played(now, 30, 26, 18);
            return f switch
            {
                "fresh" => RateNudge.Seed(RateNudge.Ledger.Fresh, now, false, p),
                "day3" => Played(now, 3, 4, 3),
                "eligible" => eligible,
                "short-days" => eligible with { ListenDays = 5 },
                "short-hours" => eligible with { ListenSeconds = 6 * 3600 },
                "asked-60" => Asked(200, 60, 66, 106),
                "asked-130" => Asked(200, 130, 66, 106),
                "asked-130-quiet" => Asked(200, 130, 96, 100.5),
                "capped" => Asked(400, 130, 100, 130) with { Asks = 2 },
                "rated" or "starred" => eligible with { Done = true, Last = f == "rated" ? RateNudge.Outcome.Rated : RateNudge.Outcome.LinkOpened },
                "opted-out" => eligible with { Enabled = false, Last = RateNudge.Outcome.NeverAgain },
                "troubled" => eligible with { LastTrouble = now.AddDays(-2) },
                "troubled-old" => eligible with { LastTrouble = now.AddDays(-8) },
                "milestone-99" => Asked(200, 130, 59.9, 99.9),
                "milestone-100" => Asked(200, 130, 59.9, 100.1),
                "existing" => RateNudge.Seed(RateNudge.Ledger.Fresh, now, true, p),
                "existing-lapsed" => Played(now, 31, 6, 4) with { Launch = RateNudge.LaunchState.Pending, LaunchBy = now.AddDays(-1) },
                "existing-later" => Asked(131, 130, 0, 30, Played(now, 131, 30, 20)) with
                    { Launch = RateNudge.LaunchState.Shown, LaunchBy = now.AddDays(-101) },
                "existing-capped" => Asked(400, 130, 100, 130, Played(now, 400, 130, 40)) with
                    { Asks = 3, Launch = RateNudge.LaunchState.Shown, LaunchBy = now.AddDays(-370) },
                _ => RateNudge.Ledger.Fresh,
            };
        }

        // ── argument parsing ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`+130d`, `-2d`, `6h`, `-30m`, `45s`: a signed span.</summary>
        static bool TryNudgeSpan(string s, out TimeSpan span)
        {
            span = default;
            if (s.Length < 2) return false;
            if (!double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)) return false;
            span = s[^1] switch { 'd' => TimeSpan.FromDays(n), 'h' => TimeSpan.FromHours(n), 'm' => TimeSpan.FromMinutes(n), 's' => TimeSpan.FromSeconds(n), _ => TimeSpan.MinValue };
            return span != TimeSpan.MinValue;
        }

        bool TryNudgeStarsCache(string s, out RateNudge.Stars? stars)
        {
            stars = null;
            if (s == "none") return true;
            int at = s.IndexOf('@');
            if (at < 1 || !int.TryParse(s.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || !TryNudgeSpan(s[(at + 1)..], out var span)) return false;
            stars = new RateNudge.Stars(count, NudgeNow() + span);
            return true;
        }

        // ── the verb ────────────────────────────────────────────────────────────────────────────────────────────────

        string Nudge(int id, string name, Inspect.Args a)
        {
            bool fire = a.Bool("fire", false), tick = a.Bool("tick", false), refresh = a.Bool("refresh", false);
            if (_shell is null && (fire || tick || refresh || a.Has("expectCard")))
                return Inspect.Reply.Fail(id, name, "nudge fire, tick, refresh and expectCard need a mount (mount first)");

            if (a.Has("raw")) Platform.Settings.Set(Platform.Keys.RateNudgeLedger, a.Str("raw"));

            // 1. state args: re-seed the host once (this clears the shown card and the armed track change)
            string fixture = a.Str("fixture").ToLowerInvariant();
            bool state = a.Has("fixture") || a.Has("channel") || a.Has("session") || a.Has("policy") || a.Has("sessionAge") || a.Has("starsCache") || a.Has("trouble");
            if (a.Has("fixture") && Array.IndexOf(s_nudgeFixtures, fixture) < 0)
                return Inspect.Reply.Fail(id, name, "unknown fixture '" + fixture + "' (" + string.Join("|", s_nudgeFixtures) + ")");
            if (a.Has("clock") && a.Str("clock") != "reset" && !TryNudgeSpan(a.Str("clock"), out _))
                return Inspect.Reply.Fail(id, name, "clock is +130d, -2d, +6h, -30m or reset");
            RateNudge.Flush();
            if (state)
            {
                bool loading = a.Has("fixture");
                RateNudge.Ledger ledger = RateNudge.Current;
                RateNudge.Stars? stars = RateNudge.DecodeStars(Platform.Settings.Get(Platform.Keys.RateNudgeStars));
                long age = FrameTime.NowMs - _nBootMono;
                if (loading) { NudgePins(); _nPolicy = RateNudge.Policy.Default; stars = null; age = 0; }
                if (a.Has("policy")) _nPolicy = a.Str("policy") == "plain" ? RateNudge.Policy.Plain : RateNudge.Policy.Default;
                if (loading) ledger = NudgeFixture(fixture, NudgeNow(), _nPolicy, out _nChannel, out _nUpdated);
                if (a.Has("channel")) _nChannel = a.Str("channel") switch { "github" => InstallChannel.Sideload, "source" => InstallChannel.Source, _ => InstallChannel.Store };
                if (a.Has("session")) _nUpdated = a.Str("session") == "after-update";
                if (a.Has("sessionAge")) age = a.Int("sessionAge", 0) * 60_000L;
                if (a.Has("clock")) _nClock = a.Str("clock") == "reset" ? TimeSpan.Zero : TryNudgeSpan(a.Str("clock"), out var span) ? span : _nClock;
                if (a.Has("starsCache"))
                {
                    if (!TryNudgeStarsCache(a.Str("starsCache"), out stars)) return Inspect.Reply.Fail(id, name, "starsCache is 86@-2h, 86@-8d or none");
                }
                if (a.Has("trouble"))
                {
                    string t = a.Str("trouble");
                    if (t == "none") ledger = ledger with { LastTrouble = null };
                    else if (t == "now") ledger = RateNudge.Troubled(ledger with { LastTrouble = null }, NudgeNow());
                    else if (TryNudgeSpan(t, out var ago)) ledger = RateNudge.Troubled(ledger with { LastTrouble = null }, NudgeNow() + ago);
                    else return Inspect.Reply.Fail(id, name, "trouble is now, -8d or none");
                }
                NudgeReseed(ledger, age, stars);
            }
            else if (a.Has("clock")) _nClock = a.Str("clock") == "reset" ? TimeSpan.Zero : TryNudgeSpan(a.Str("clock"), out var span) ? span : _nClock;

            // 2. pins that need no re-seed
            if (a.Has("window"))
                _nWindow = a.Str("window") switch { "behind" => RateNudge.WindowFact.Behind, "minimized" => RateNudge.WindowFact.Minimized, "tray" => RateNudge.WindowFact.Tray, _ => RateNudge.WindowFact.Front };
            if (a.Has("store"))
                _nStore = a.Str("store") switch
                {
                    "canceled" => RateNudge.StoreAnswer.CanceledByUser, "network" => RateNudge.StoreAnswer.NetworkError,
                    "error" => RateNudge.StoreAnswer.Error, "unavailable" => RateNudge.StoreAnswer.Unavailable, _ => RateNudge.StoreAnswer.Succeeded,
                };
            if (a.Has("link")) _nLinkOk = a.Str("link") != "fail";
            if (a.Has("stars")) _nStarsAnswer = a.Str("stars") == "fail" ? null : a.Int("stars", 0);

            // 3. act
            if (a.Bool("reload", false)) RateNudge.ReloadForInspect(Platform.Settings);
            bool overlay = NudgeOverlayOpen();
            if (refresh)
            {
                bool due = RateNudge.StarsDue(RateNudge.Channel, RateNudge.DecodeStars(Platform.Settings.Get(Platform.Keys.RateNudgeStars)), NudgeNow(), RateNudge.Rules);
                int before = Volatile.Read(ref _nFetches);
                RateNudge.RefreshStarsIfDue();
                if (due) SpinWait.SpinUntil(() => Volatile.Read(ref _nFetches) > before, 2000);   // the fetch runs on the pool; its answer lands on `frames`
            }
            if (fire) RateNudge.OnTrackBoundary();
            if (tick) RateNudge.Tick(overlay);
            RateNudge.Flush();

            // 4. the reply, and the gates
            RateNudge.Decision d = RateNudge.LastDecision ?? RateNudge.Decide(RateNudge.Gather(overlay), RateNudge.Rules);
            string? error = NudgeExpect(a, d);
            void Body(Utf8JsonWriter w) => WriteNudge(w, d);
            return error is null ? Inspect.Reply.Ok(id, name, Body) : Inspect.Reply.Fail(id, name, error, Body);
        }

        bool NudgeOverlayOpen() => _shell is { } shell && OpenOverlays(shell).Count > 0;

        // ── expect* ─────────────────────────────────────────────────────────────────────────────────────────────────

        static string NudgeGot(RateNudge.Decision d)
            => d.Show ? $"show:{d.Ask.ToString().ToLowerInvariant()}:{d.AskNumber}:{d.Kind.ToString().ToLowerInvariant()}"
                      : $"quiet:{d.Blocker?.Rule}{(d.Closed ? " (closed)" : "")}";

        string? NudgeExpect(Inspect.Args a, RateNudge.Decision d)
        {
            if (a.Has("expect"))
            {
                foreach (string want in a.Str("expect").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] t = want.Split(':');
                    bool ok = t[0] switch
                    {
                        "closed" => d.Closed,
                        "show" => d.Show && (t.Length < 2 || string.Equals(t[1], d.Ask.ToString(), StringComparison.OrdinalIgnoreCase))
                                         && (t.Length < 3 || t[2] == d.AskNumber.ToString(CultureInfo.InvariantCulture))
                                         && (t.Length < 4 || string.Equals(t[3], d.Kind.ToString(), StringComparison.OrdinalIgnoreCase)),
                        "quiet" => !d.Show && (t.Length < 2 || string.Equals(t[1], d.Blocker?.Rule.ToString(), StringComparison.OrdinalIgnoreCase)),
                        _ => false,
                    };
                    if (!ok) return $"expected {want}, got {NudgeGot(d)}";
                }
            }
            if (a.Has("expectLedger"))
            {
                var have = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string tok in RateNudge.Describe(RateNudge.Current).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0) have[tok[..eq]] = tok[(eq + 1)..];
                }
                foreach (string pair in a.Str("expectLedger").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = pair.IndexOf('=');
                    string key = eq > 0 ? pair[..eq] : pair, want = eq > 0 ? pair[(eq + 1)..] : "";
                    if (!have.TryGetValue(key, out string? got)) return $"expectLedger: unknown field '{key}' (" + string.Join(", ", have.Keys) + ")";
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) return $"expectLedger: {key} is '{got}', expected '{want}'";
                }
            }
            if (a.Has("expectLaunched"))
            {
                string want = a.Str("expectLaunched");
                lock (_nLaunched)
                {
                    string? last = _nLaunched.Count > 0 ? _nLaunched[^1] : null;
                    if (want == "none" ? last is not null : last is null || !last.StartsWith(want, StringComparison.Ordinal))
                        return $"expectLaunched: expected {want}, launched [{string.Join(", ", _nLaunched)}]";
                }
            }
            if (a.Has("expectCard"))
            {
                bool shown = RateNudge.Shown.Peek() is not null;
                if (shown != (a.Str("expectCard") == "shown")) return $"expectCard: expected {a.Str("expectCard")}, the card is {(shown ? "shown" : "hidden")}";
            }
            return null;
        }

        // ── the reply (§8.4) ────────────────────────────────────────────────────────────────────────────────────────

        void WriteNudge(Utf8JsonWriter w, RateNudge.Decision d)
        {
            var inv = CultureInfo.InvariantCulture;
            w.WriteString("channel", RateNudge.Channel.ToString());
            w.WriteString("now", NudgeNow().ToString("yyyy-MM-ddTHH:mm:sszzz", inv));
            w.WriteStartObject("decision");
            w.WriteBoolean("show", d.Show);
            w.WriteString("ask", d.Ask.ToString());
            w.WriteString("kind", d.Kind.ToString());
            w.WriteNumber("askNumber", d.AskNumber);
            w.WriteBoolean("closed", d.Closed);
            if (d.Blocker is { } b) WriteNudgeCheck(w, "blocker", b);
            else w.WriteNull("blocker");
            if (d.Earliest is { } e) w.WriteString("earliest", e.ToString("yyyy-MM-dd", inv));
            else w.WriteNull("earliest");
            w.WriteStartArray("checks");
            foreach (var c in d.Checks) WriteNudgeCheck(w, null, c);
            w.WriteEndArray();
            w.WriteEndObject();

            var ledger = RateNudge.Current;
            w.WriteStartObject("ledger");
            w.WriteString("text", RateNudge.Describe(ledger));
            w.WriteString("raw", RateNudge.Encode(ledger));
            w.WriteEndObject();

            w.WriteStartObject("card");
            if (RateNudge.Shown.Peek() is { } card)
            {
                w.WriteBoolean("shown", true);
                w.WriteString("ask", card.Ask.ToString());
                w.WriteNumber("askNumber", card.AskNumber);
                w.WriteBoolean("held", card.HoldSinceMs is not null);
            }
            else w.WriteBoolean("shown", false);
            w.WriteEndObject();

            w.WriteStartArray("launched");
            lock (_nLaunched) foreach (string url in _nLaunched) w.WriteStringValue(url);
            w.WriteEndArray();

            w.WriteStartObject("stars");
            if (RateNudge.StarCount is { } shownStars) w.WriteNumber("shown", shownStars);
            else w.WriteNull("shown");
            if (RateNudge.DecodeStars(Platform.Settings.Get(Platform.Keys.RateNudgeStars)) is { } cached) w.WriteNumber("cached", cached.Count);
            else w.WriteNull("cached");
            w.WriteNumber("fetches", Volatile.Read(ref _nFetches));
            w.WriteEndObject();

            if (_acts) WriteAfter(w);
        }

        static void WriteNudgeCheck(Utf8JsonWriter w, string? property, RateNudge.Check c)
        {
            if (property is null) w.WriteStartObject();
            else w.WriteStartObject(property);
            w.WriteString("rule", c.Rule.ToString());
            w.WriteString("stage", c.Stage.ToString());
            w.WriteBoolean("pass", c.Pass);
            w.WriteString("detail", c.Detail);
            w.WriteEndObject();
        }
    }
}
