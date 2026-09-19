// ── Wavee.Tests/HeadlessTests.cs — the headless grammar, conditions, script runner, JSON lines, store wrappers ──────
//
// Pins `Screens/Diagnostics.Headless.cs` (CORE) and the two pure pieces of `Screens/Diagnostics.Probe.cs`
// (`HeadlessOptions.TryParse`, `HeadlessLoop`) — docs/plans/wavee/wavee-0.3-headless-implementation.md §5.
//
// No network, no engine loop, no profile: every store is an in-memory fake, every snapshot is a value built here, and
// the ogg320 smoke script is replayed verbatim against a scripted player (`HeadlessFakePlayer`) so the whole runner —
// waits, marks, deltas, seeks, the verdict — is decided by the same code the real host ticks.

using System.Text.Json;
using Xunit;
using H = Wavee.Diagnostics.Headless;

namespace Wavee.Tests;

static class HeadlessFixtures
{
    public const string Track = "spotify:track:11dFghVXANMlKmJXsNCbNl";
    public const string Album = "spotify:album:4aawyAB9vmqN3uQ7FjRGTy";

    /// <summary>ops/headless/ogg320.wh, verbatim.</summary>
    public const string Ogg320 = """
        # ogg320.wh - the Wave 3 gate (plan section 5) and the vorbis plan section 8.3 lines.
        # 10 s of a track through the real pump at the 320 rung, then a far seek, a ring seek and a seek back into what
        # has played, each with its request budget. Track: "Cut To The Feeling" (3:27), the Web API docs example.
        #
        # `set` and `quality` lines come BEFORE `volume`: the audio pump reads them when it boots, and volume boots it.
        # The reducer moves the position the moment a seek is posted, so a seek is proven by `wait seeked` (the pump's
        # own report, compared with the last `stats mark`) and by the position moving PAST the seek point.
        set cache on
        quality veryhigh
        volume 0.2
        stats mark
        play spotify:track:11dFghVXANMlKmJXsNCbNl
        wait playing timeout 20000
        expect cdn.requests<=4                          # cold start = head || resolve || key ; range 1 || tail (vorbis 5.4)
        wait position>=10s timeout 15000
        stats mark
        seek 2:00                                       # far: the first time in this region
        wait seeked timeout 5000
        expect cdn.requests<=2                          # <= 2 typical (vorbis 4.5 row 1)
        wait position>=2:02 && !buffering timeout 6000
        stats mark
        seek +5s                                        # inside the ring
        wait seeked timeout 3000
        expect cdn.requests==0
        stats mark
        seek 0:05                                       # back into what has played: at most one probe
        wait seeked timeout 5000
        expect cdn.requests<=1
        wait position>=0:07 && !buffering timeout 6000
        stats reset
        expect xruns==0                                 # the whole run
        stats
        quit
        """;

    /// <summary>ops/headless/library-sync.wh, verbatim (G-239: the headless smoke that a --script run now installs
    /// Spotify.Library and Lyrics.Boot the same way App.cs does for the GUI).</summary>
    public const string LibrarySync = """
        # library-sync.wh - G-239 smoke: proves the headless host actually installs the library host and the lyrics stack
        # (Spotify.Library.Install / Lyrics.Boot, wired in Diagnostics.Probe.cs's HeadlessHost.Run) instead of silently
        # skipping them the way it did before this fix - a headless run that never syncs the library cannot confirm
        # G-042 (sync)/G-043/G-049 (rootlist writes) or G-008 (lyrics) live at all.
        #
        # CAVEAT (read before trusting "it printed nothing, so nothing happened"): the .wh grammar (Diagnostics.Headless.cs)
        # has no rootlist/pin/collection field - `wait`/`expect`/`status` only see playback and session state. This script
        # cannot print counts itself. Run it with -EchoLog (Invoke-WaveeHeadless.ps1 already forwards -EchoLog to
        # --echo-log): the library host's own Log.Info("library", ...) lines ("library sync asked: rootlist, pins, liked +
        # albums, artists, shows, recents" on the first sync after Online; "reconnect resync skipped: ..." on a second run
        # inside 30 s) now come back as {"kind":"echo","category":"library",...} JSON lines in the capture, which is the
        # proof the sync fired - not a count. A `library` verb + JSON status line in Diagnostics.Headless.cs (CORE, owner
        # X/F) is the follow-up that would let a script print the rootlist folder count, the pin count and the collection
        # counts directly; out of this batch's file list (R4-5 gap register report names it).
        #
        # Usage: powershell -File ops\headless\Invoke-WaveeHeadless.ps1 library-sync.wh -EchoLog -Profile <dir>
        wait online timeout 60000
        sleep 20000
        log library sync window elapsed
        status
        quit
        """;

    public static H.StatusSnapshot Snap(long now = 0, string phase = "Idle", string track = "", int pos = 0, string session = "Online")
        => H.StatusSnapshot.Empty with { NowMs = now, Phase = phase, TrackUri = track, PositionMs = pos, SessionPhase = session };

    public static H.Command Parse(string line)
    {
        Assert.True(H.TryParse(line, out H.Command cmd, out string error), "'" + line + "' did not parse: " + error);
        return cmd;
    }

    public static H.Script Load(string text)
    {
        Assert.True(H.Script.TryLoad(text, out H.Script script, out int line, out string error), "line " + line + ": " + error);
        return script;
    }

    public static bool Holds(string condition, in H.StatusSnapshot now, H.StatusSnapshot? start = null, H.StatusSnapshot? mark = null)
    {
        Assert.True(H.TryParseCondition(condition, out H.Condition cond, out string error), "'" + condition + "': " + error);
        return H.Holds(cond, now, start ?? now, mark ?? H.StatusSnapshot.Empty);
    }

    public static JsonElement Json(string line)
    {
        Assert.DoesNotContain("\n", line);
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }
}

/// <summary>A scripted player the runner can drive: a play loads in 300 ms and costs four CDN requests; a seek moves the
/// position at once (the reducer's optimism) and the pump's report lands 150 ms later; a forward seek within 15 s is a
/// ring seek (no request), anything else costs one probe.</summary>
sealed class HeadlessFakePlayer
{
    public const int Duration = 207_959;
    public long Now;
    public string Phase = "Idle", Track = "", Format = "";
    public long Cdn;
    public int Xruns, SeekMs, SeekLatency;
    public byte SeekKind;
    int _posBase, _pendingSeek;
    long _posStamp, _loadDoneAt = -1, _seekLandsAt = -1;
    byte _pendingKind;

    public int Position => Phase == "Playing" ? (int)Math.Min(Duration, _posBase + (Now - _posStamp)) : _posBase;

    public H.StatusSnapshot Snap() => H.StatusSnapshot.Empty with
    {
        NowMs = Now, SessionPhase = "Online", Phase = Phase, TrackUri = Track, PositionMs = Position,
        DurationMs = Phase == "Idle" ? 0 : Duration, Format = Format, CdnRequests = Cdn, Xruns = Xruns,
        LastSeekMs = SeekMs, LastSeekLatencyMs = SeekLatency, LastSeekKind = SeekKind,
    };

    public void Apply(in H.ScriptAction action)
    {
        H.Command c = action.Cmd;
        switch (action.Verb)
        {
            case H.Verb.Play:
                Track = c.Arg0; Phase = "Loading"; _posBase = 0; _loadDoneAt = Now + 300; Cdn += 4;
                break;
            case H.Verb.Seek:
                {
                    int from = Position;
                    int target = c.FromEnd ? Duration + c.Int0 : c.Relative ? from + c.Int0 : c.Int0;
                    bool ring = target >= from && target - from <= 15_000;
                    _posBase = target; _posStamp = Now;
                    if (!ring) Cdn++;
                    _pendingSeek = target; _pendingKind = ring ? (byte)0 : (byte)1; _seekLandsAt = Now + 150;
                    break;
                }
        }
    }

    public void Advance(int ms)
    {
        Now += ms;
        if (Phase == "Loading" && Now >= _loadDoneAt) { Phase = "Playing"; _posStamp = Now; Format = "OGG 320"; }
        if (_seekLandsAt >= 0 && Now >= _seekLandsAt) { SeekMs = _pendingSeek; SeekLatency = 150; SeekKind = _pendingKind; _seekLandsAt = -1; }
    }
}

sealed class HeadlessMemoryStore : ILocalStore
{
    public readonly Dictionary<string, string> Values = new();
    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => Values[key] = value;
    public void Remove(string key) => Values.Remove(key);
}

sealed class HeadlessRecordingSettings : IAppSettings
{
    public readonly Dictionary<string, object> Values = new();
    public int Writes;
    public T Get<T>(SettingKey<T> key) => Values.TryGetValue(key.Name, out var v) && v is T t ? t : key.Default;
    public void Set<T>(SettingKey<T> key, T value) { Writes++; if (value is not null) Values[key.Name] = value; }
}

// ── the grammar ──────────────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessGrammarTests
{
    [Theory]
    [InlineData("play " + HeadlessFixtures.Track, H.Verb.Play)]
    [InlineData("pause", H.Verb.Pause)]
    [InlineData("resume", H.Verb.Resume)]
    [InlineData("toggle", H.Verb.Toggle)]
    [InlineData("stop", H.Verb.Stop)]
    [InlineData("seek 1:30", H.Verb.Seek)]
    [InlineData("next", H.Verb.Next)]
    [InlineData("prev", H.Verb.Prev)]
    [InlineData("volume 0.2", H.Verb.Volume)]
    [InlineData("shuffle on", H.Verb.Shuffle)]
    [InlineData("repeat context", H.Verb.Repeat)]
    [InlineData("quality high", H.Verb.Quality)]
    [InlineData("set crossfade 0", H.Verb.Set)]
    [InlineData("queue " + HeadlessFixtures.Track, H.Verb.Queue)]
    [InlineData("prepare", H.Verb.Prepare)]
    [InlineData("status", H.Verb.Status)]
    [InlineData("stats mark", H.Verb.Stats)]
    [InlineData("wait playing timeout 5000", H.Verb.Wait)]
    [InlineData("expect xruns==0", H.Verb.Expect)]
    [InlineData("sleep 250", H.Verb.Sleep)]
    [InlineData("login", H.Verb.Login)]
    [InlineData("connect off", H.Verb.Connect)]
    [InlineData("log a marker line", H.Verb.Log)]
    [InlineData("quit", H.Verb.Quit)]
    public void EveryVerb_Parses(string line, H.Verb verb)
    {
        H.Command cmd = HeadlessFixtures.Parse(line);
        Assert.Equal(verb, cmd.Verb);
        Assert.Equal(line, cmd.Text);
        Assert.Equal(-1, cmd.Id);
    }

    [Fact]
    public void Seek_MinutesSecondsMillis_IsAbsolute()
    {
        H.Command cmd = HeadlessFixtures.Parse("seek 1:30.250");
        Assert.Equal(90_250, cmd.Int0);
        Assert.False(cmd.Relative);
        Assert.False(cmd.FromEnd);
    }

    [Fact]
    public void Seek_PlusFiveSeconds_IsRelative()
    {
        H.Command cmd = HeadlessFixtures.Parse("seek +5s");
        Assert.Equal(5_000, cmd.Int0);
        Assert.True(cmd.Relative);
    }

    [Fact]
    public void Seek_MinusTenSeconds_IsRelativeBack()
    {
        H.Command cmd = HeadlessFixtures.Parse("seek -10s");
        Assert.Equal(-10_000, cmd.Int0);
        Assert.True(cmd.Relative);
    }

    [Fact]
    public void Seek_EndMinusTen_IsFromTheEnd()
    {
        H.Command cmd = HeadlessFixtures.Parse("seek end-10s");
        Assert.Equal(-10_000, cmd.Int0);
        Assert.True(cmd.FromEnd);
        Assert.False(cmd.Relative);
    }

    [Theory]
    [InlineData("1500", 1_500)]
    [InlineData("1500ms", 1_500)]
    [InlineData("90s", 90_000)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2:00", 120_000)]
    [InlineData("0:05", 5_000)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("1:02:03.5", 3_723_500)]
    public void Position_AbsoluteForms(string text, int expected)
    {
        Assert.True(H.TryParsePosition(text, out int ms, out bool relative));
        Assert.Equal(expected, ms);
        Assert.False(relative);
    }

    [Theory]
    [InlineData("1:60")]
    [InlineData("1:2:60")]
    [InlineData("abc")]
    [InlineData("1.2345s")]
    [InlineData("")]
    public void Position_BadForms_AreRefused(string text) => Assert.False(H.TryParsePosition(text, out _, out _));

    [Fact]
    public void Position_EndForm_IsRefusedByTheTwoFlagOverload()
    {
        Assert.False(H.TryParsePosition("end-10s", out _, out _));
        Assert.True(H.TryParsePosition("end-10s", out int ms, out _, out bool fromEnd));
        Assert.Equal(-10_000, ms);
        Assert.True(fromEnd);
    }

    [Theory]
    [InlineData("normal", 0)]
    [InlineData("high", 1)]
    [InlineData("veryhigh", 2)]
    [InlineData("lossless", 3)]
    [InlineData("LOSSLESS", 3)]
    public void Quality_WordsAreTheRungs(string word, int rung)
    {
        Assert.True(H.TryParseQuality(word, out int parsed));
        Assert.Equal(rung, parsed);
        Assert.Equal(rung, HeadlessFixtures.Parse("quality " + word).Int0);
    }

    [Fact]
    public void UnknownVerb_NamesTheWord()
    {
        Assert.False(H.TryParse("foo 1 2", out _, out string error));
        Assert.Equal("unknown command 'foo'", error);
    }

    [Fact]
    public void Json_WithId_RoundTripsTheId()
    {
        H.Command cmd = HeadlessFixtures.Parse("""{"cmd":"seek","args":["1:30"],"id":7}""");
        Assert.Equal(H.Verb.Seek, cmd.Verb);
        Assert.Equal(90_000, cmd.Int0);
        Assert.Equal(7, cmd.Id);
    }

    [Fact]
    public void Json_NumberArguments_AreTextForTheGrammar()
    {
        H.Command cmd = HeadlessFixtures.Parse("""{"cmd":"volume","args":[0.5],"id":2}""");
        Assert.Equal(H.Verb.Volume, cmd.Verb);
        Assert.Equal(500, cmd.Int0);
    }

    [Fact]
    public void Json_BadCommand_KeepsItsIdForTheReply()
    {
        Assert.False(H.TryParse("""{"cmd":"seek","args":["nowhere"],"id":9}""", out H.Command cmd, out string error));
        Assert.Equal(9, cmd.Id);
        Assert.StartsWith("bad position", error);
    }

    [Fact]
    public void Json_Malformed_IsAnError() => Assert.False(H.TryParse("{\"cmd\":", out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("   # an indented comment")]
    public void CommentsAndBlanks_AreNone(string line)
    {
        Assert.True(H.TryParse(line, out H.Command cmd, out _));
        Assert.True(cmd.IsNone);
    }

    [Fact]
    public void InlineComment_IsStripped()
    {
        H.Command cmd = HeadlessFixtures.Parse("expect cdn.requests<=4   # head || resolve || key");
        Assert.Equal(H.Verb.Expect, cmd.Verb);
        Assert.Equal("expect cdn.requests<=4", cmd.Text);
    }

    [Fact]
    public void Play_CarriesFromAndQuality()
    {
        H.Command cmd = HeadlessFixtures.Parse("play " + HeadlessFixtures.Album + " from 1:00 quality lossless");
        Assert.Equal(HeadlessFixtures.Album, cmd.Arg0);
        Assert.Equal(60_000, cmd.Int0);
        Assert.Equal(3, cmd.Int1);
        Assert.Equal(-1, HeadlessFixtures.Parse("play " + HeadlessFixtures.Track).Int1);
    }

    [Theory]
    [InlineData("play https://open.spotify.com/track/11dFghVXANMlKmJXsNCbNl")]
    [InlineData("play spotify:track:short")]
    [InlineData("play spotify:artist:0TnOYISbd1XYRBk9myaseg")]
    [InlineData("play")]
    [InlineData("queue " + HeadlessFixtures.Album)]
    [InlineData("play " + HeadlessFixtures.Track + " from +5s")]
    public void Play_And_Queue_RefuseWhatTheyCannotPlay(string line) => Assert.False(H.TryParse(line, out _, out _));

    [Fact]
    public void Play_AcceptsTheUserNamespacedPlaylistSpelling()
        => Assert.Equal(H.Verb.Play, HeadlessFixtures.Parse("play spotify:user:someone:playlist:37i9dQZF1DXcBWIGoYBM5M").Verb);

    [Fact]
    public void Quit_WithoutCode_IsTheVerdict_WithCode_IsTheCode()
    {
        Assert.Equal(-1, HeadlessFixtures.Parse("quit").Int0);
        Assert.Equal(7, HeadlessFixtures.Parse("quit 7").Int0);
        Assert.False(H.TryParse("quit 300", out _, out _));
    }

    [Theory]
    [InlineData("volume 1.5")]
    [InlineData("volume -0.1")]
    [InlineData("volume loud")]
    [InlineData("shuffle maybe")]
    [InlineData("repeat all")]
    [InlineData("set crossfade soon")]
    [InlineData("set unknown 1")]
    [InlineData("stats everything")]
    [InlineData("pause now")]
    [InlineData("sleep")]
    [InlineData("log")]
    [InlineData("expect playing timeout 500")]
    [InlineData("play? " + HeadlessFixtures.Track)]
    public void BadArguments_AreRefused(string line) => Assert.False(H.TryParse(line, out _, out _));

    [Fact]
    public void Wait_CarriesConditionAndTimeout()
    {
        H.Command cmd = HeadlessFixtures.Parse("wait playing && position>=10s timeout 15000");
        Assert.Equal(15_000, cmd.TimeoutMs);
        Assert.Equal(2, cmd.Cond.Count);
        Assert.Equal("playing && position>=10s", cmd.Arg0);
        Assert.Equal(0, HeadlessFixtures.Parse("wait playing").TimeoutMs);
    }

    [Fact]
    public void SoftSteps_AreMarkedSoft()
    {
        Assert.True(HeadlessFixtures.Parse("expect? format==flac24").Soft);
        Assert.True(HeadlessFixtures.Parse("wait? playing timeout 1s").Soft);
        Assert.False(HeadlessFixtures.Parse("expect format==flac24").Soft);
    }

    [Fact]
    public void Set_CarriesNameAndValue()
    {
        H.Command crossfade = HeadlessFixtures.Parse("set crossfade 5s");
        Assert.Equal("crossfade", crossfade.Arg0);
        Assert.Equal(5_000, crossfade.Int0);
        Assert.Equal(1, HeadlessFixtures.Parse("set normalization on").Int0);
        Assert.Equal(0, HeadlessFixtures.Parse("set cache off").Int0);
        Assert.Equal(1, HeadlessFixtures.Parse("set metered-cap high").Int0);
    }

    [Fact]
    public void Log_KeepsTheWholeText() => Assert.Equal("a marker   with spaces", HeadlessFixtures.Parse("log a marker   with spaces").Arg0);
}

// ── the conditions ───────────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessConditionTests
{
    [Theory]
    [InlineData("Playing", "playing", true)]
    [InlineData("Paused", "paused", true)]
    [InlineData("Loading", "loading", true)]
    [InlineData("Idle", "idle", true)]
    [InlineData("Playing", "paused", false)]
    [InlineData("Playing", "phase==playing", true)]
    [InlineData("Playing", "phase!=playing", false)]
    public void Phase_Clauses(string phase, string condition, bool expected)
        => Assert.Equal(expected, HeadlessFixtures.Holds(condition, HeadlessFixtures.Snap(phase: phase)));

    [Fact]
    public void Ended_ViaThePhase() => Assert.True(HeadlessFixtures.Holds("ended", HeadlessFixtures.Snap(phase: "Ended")));

    [Fact]
    public void Ended_ViaATrackChangeSinceTheWaitBegan()
    {
        var start = HeadlessFixtures.Snap(phase: "Playing", track: "spotify:track:a");
        Assert.False(HeadlessFixtures.Holds("ended", start, start));
        Assert.True(HeadlessFixtures.Holds("ended", HeadlessFixtures.Snap(phase: "Playing", track: "spotify:track:b"), start));
    }

    [Theory]
    [InlineData("Online", "online", true)]
    [InlineData("Minting", "online", false)]
    [InlineData("Offline", "offline", true)]
    [InlineData("Failed", "failed", true)]
    [InlineData("Reconnecting", "session==reconnecting", true)]
    public void Session_Clauses(string phase, string condition, bool expected)
        => Assert.Equal(expected, HeadlessFixtures.Holds(condition, HeadlessFixtures.Snap(session: phase)));

    [Fact]
    public void Buffering_AndItsNegation()
    {
        var buffering = H.StatusSnapshot.Empty with { Buffering = true };
        Assert.True(HeadlessFixtures.Holds("buffering", buffering));
        Assert.False(HeadlessFixtures.Holds("!buffering", buffering));
        Assert.True(HeadlessFixtures.Holds("!buffering", H.StatusSnapshot.Empty));
    }

    [Theory]
    [InlineData(10_000, "position>=10s", true)]
    [InlineData(9_999, "position>=10s", false)]
    [InlineData(59_000, "position<1:00", true)]
    [InlineData(60_000, "position<1:00", false)]
    [InlineData(60_000, "position<=1:00", true)]
    [InlineData(60_001, "position>1:00", true)]
    [InlineData(5_000, "position==5000", true)]
    [InlineData(5_000, "position!=5000", false)]
    public void Position_Operators(int pos, string condition, bool expected)
        => Assert.Equal(expected, HeadlessFixtures.Holds(condition, HeadlessFixtures.Snap(pos: pos)));

    [Fact]
    public void Position_RelativeToTheWaitsStart()
    {
        var start = HeadlessFixtures.Snap(pos: 120_000);
        Assert.False(HeadlessFixtures.Holds("position>=+5s", HeadlessFixtures.Snap(pos: 124_999), start));
        Assert.True(HeadlessFixtures.Holds("position>=+5s", HeadlessFixtures.Snap(pos: 125_000), start));
    }

    [Fact]
    public void Track_EqualNotEqual_AndThePresenceForm()
    {
        var playing = HeadlessFixtures.Snap(track: HeadlessFixtures.Track);
        Assert.True(HeadlessFixtures.Holds("track==" + HeadlessFixtures.Track, playing));
        Assert.False(HeadlessFixtures.Holds("track!=" + HeadlessFixtures.Track, playing));
        Assert.True(HeadlessFixtures.Holds("track!=", playing));
        Assert.False(HeadlessFixtures.Holds("track!=", HeadlessFixtures.Snap()));
        Assert.False(HeadlessFixtures.Holds("track==" + HeadlessFixtures.Track.ToUpperInvariant(), playing));   // base62 is case-sensitive
    }

    [Fact]
    public void Format_Flac24_IsFoldedFromTheBadge()
    {
        Assert.True(HeadlessFixtures.Holds("format==flac24", H.StatusSnapshot.Empty with { Format = "FLAC 24/44.1" }));
        Assert.False(HeadlessFixtures.Holds("format==flac24", H.StatusSnapshot.Empty with { Format = "OGG 320" }));
        Assert.True(HeadlessFixtures.Holds("format!=flac24", H.StatusSnapshot.Empty with { Format = "FLAC 16/44.1" }));
    }

    [Theory]
    [InlineData("OGG 320", "ogg320")]
    [InlineData("Ogg 320", "ogg320")]
    [InlineData("OGG 96", "ogg96")]
    [InlineData("FLAC 24/44.1", "flac24")]
    [InlineData("FLAC 24/96", "flac24")]
    [InlineData("FLAC 24-bit", "flac24")]
    [InlineData("FLAC 16/44.1", "flac")]
    [InlineData("FLAC", "flac")]
    [InlineData("MP3", "mp3")]
    [InlineData("", "")]
    public void FoldFormat_Table(string badge, string folded) => Assert.Equal(folded, H.FoldFormat(badge));

    [Fact]
    public void Next_IsATrackChangeSinceTheStart()
    {
        var start = HeadlessFixtures.Snap(track: "spotify:track:a");
        Assert.False(HeadlessFixtures.Holds("next", start, start));
        Assert.True(HeadlessFixtures.Holds("next", HeadlessFixtures.Snap(track: "spotify:track:b"), start));
        Assert.False(HeadlessFixtures.Holds("next", HeadlessFixtures.Snap(track: ""), start));
    }

    [Fact]
    public void Prefetched_ReadsThePrepareArm()
    {
        Assert.True(HeadlessFixtures.Holds("prefetched", H.StatusSnapshot.Empty with { PrepareArmed = true }));
        Assert.False(HeadlessFixtures.Holds("prefetched", H.StatusSnapshot.Empty));
    }

    [Fact]
    public void CdnRequests_IsADeltaAgainstTheMark()
    {
        var mark = H.StatusSnapshot.Empty with { CdnRequests = 40 };
        Assert.True(HeadlessFixtures.Holds("cdn.requests<=4", H.StatusSnapshot.Empty with { CdnRequests = 44 }, mark: mark));
        Assert.False(HeadlessFixtures.Holds("cdn.requests<=4", H.StatusSnapshot.Empty with { CdnRequests = 45 }, mark: mark));
        Assert.True(HeadlessFixtures.Holds("cdn.requests==0", H.StatusSnapshot.Empty with { CdnRequests = 40 }, mark: mark));
    }

    [Fact]
    public void CdnInFlight_IsTheLiveLevel()
    {
        var mark = H.StatusSnapshot.Empty with { CdnInFlight = 5 };
        Assert.True(HeadlessFixtures.Holds("cdn.inflight<=1", H.StatusSnapshot.Empty with { CdnInFlight = 1 }, mark: mark));
        Assert.False(HeadlessFixtures.Holds("cdn.inflight<=1", H.StatusSnapshot.Empty with { CdnInFlight = 2 }));
    }

    [Fact]
    public void Xruns_And_GaplessExact_AreDeltas()
    {
        var mark = H.StatusSnapshot.Empty with { Xruns = 3, GaplessExact = 2 };
        var now = H.StatusSnapshot.Empty with { Xruns = 3, GaplessExact = 3 };
        Assert.True(HeadlessFixtures.Holds("xruns==0", now, mark: mark));
        Assert.True(HeadlessFixtures.Holds("gapless.exact>=1", now, mark: mark));
        Assert.False(HeadlessFixtures.Holds("gapless.exact>=2", now, mark: mark));
    }

    [Fact]
    public void Owner_IsCaseInsensitive()
        => Assert.True(HeadlessFixtures.Holds("owner==foreign", H.StatusSnapshot.Empty with { Owner = "Foreign" }));

    [Fact]
    public void Seeked_IsANewSeekReportSinceTheMark_AndSeekKindNamesIt()
    {
        var mark = H.StatusSnapshot.Empty with { LastSeekMs = 120_000, LastSeekLatencyMs = 90 };
        Assert.False(HeadlessFixtures.Holds("seeked", mark, mark: mark));
        Assert.True(HeadlessFixtures.Holds("seeked", mark with { SeekCount = 1 }, mark: mark));          // a second seek to the same place
        Assert.True(HeadlessFixtures.Holds("!seeked", mark, mark: mark));
        var landed = mark with { LastSeekMs = 125_000, LastSeekLatencyMs = 4, LastSeekKind = 0 };
        Assert.True(HeadlessFixtures.Holds("seeked", landed, mark: mark));
        Assert.True(HeadlessFixtures.Holds("seek.kind==ring", landed, mark: mark));
        Assert.True(HeadlessFixtures.Holds("seek.kind==disk", landed with { LastSeekKind = 2 }, mark: mark));
    }

    [Fact]
    public void FourClauses_AreAccepted_AFifthIsRefused()
    {
        var now = H.StatusSnapshot.Empty with { Phase = "Playing", PositionMs = 12_000, TrackUri = HeadlessFixtures.Track };
        Assert.True(HeadlessFixtures.Holds("playing && !buffering && position>=10s && track!=", now));
        Assert.False(HeadlessFixtures.Holds("playing && !buffering && position>=10s && track==spotify:track:x", now));
        Assert.False(H.TryParseCondition("playing && !buffering && position>=10s && track!= && online", out _, out string error));
        Assert.Contains("at most 4", error);
    }

    [Theory]
    [InlineData("playing || paused")]
    [InlineData("track>=x")]
    [InlineData("format<flac")]
    [InlineData("volume==1")]
    [InlineData("dancing")]
    [InlineData("position>=end-10s")]
    [InlineData("xruns==many")]
    [InlineData("playing &&")]
    [InlineData("position=10")]
    [InlineData("")]
    public void BadConditions_AreRefused(string condition) => Assert.False(H.TryParseCondition(condition, out _, out _));
}

// ── the script runner ────────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessScriptTests
{
    [Fact]
    public void Ogg320Script_Verbatim_DrivesToOk()
    {
        H.Script script = HeadlessFixtures.Load(HeadlessFixtures.Ogg320);
        var player = new HeadlessFakePlayer();
        var verbs = new List<H.Verb>();
        H.ScriptAction action = default;
        for (int tick = 0; tick < 2_000 && !action.Finished; tick++)
        {
            action = script.Tick(player.Snap());
            if (action.Verb != H.Verb.None) { verbs.Add(action.Verb); player.Apply(action); }
            while (script.TryTakeResult(out H.StepResult r)) Assert.True(r.Ok, "step " + r.N + " (line " + r.Line + ") '" + r.Cmd + "': " + r.Reason);
            player.Advance(100);
        }
        Assert.True(action.Finished);
        Assert.Equal(H.ExitCode.Ok, action.ExitCode);
        Assert.Equal(0, script.Failed);
        Assert.Equal(script.StepCount, script.Executed);
        H.Verb[] expected =
        [
            H.Verb.Set, H.Verb.Quality, H.Verb.Volume, H.Verb.Stats, H.Verb.Play, H.Verb.Stats, H.Verb.Seek, H.Verb.Stats,
            H.Verb.Seek, H.Verb.Stats, H.Verb.Seek, H.Verb.Stats, H.Verb.Stats, H.Verb.Quit,
        ];
        Assert.Equal(expected, verbs.ToArray());
    }

    [Fact]
    public void Ogg320Script_ARingSeekThatCostsARequest_FailsWithAssertion()
    {
        H.Script script = HeadlessFixtures.Load(HeadlessFixtures.Ogg320);
        var player = new HeadlessFakePlayer();
        H.ScriptAction action = default;
        int seeks = 0;
        for (int tick = 0; tick < 2_000 && !action.Finished; tick++)
        {
            action = script.Tick(player.Snap());
            if (action.Verb != H.Verb.None) player.Apply(action);
            if (action.Verb == H.Verb.Seek && ++seeks == 2) player.Cdn++;      // the "ring" seek went to the network
            player.Advance(100);
        }
        Assert.Equal(H.ExitCode.Assertion, action.ExitCode);
        Assert.Equal(1, script.Failed);
    }

    [Fact]
    public void Ogg320Script_AnUnderrunAnywhere_FailsTheWholeRunCheck()
    {
        H.Script script = HeadlessFixtures.Load(HeadlessFixtures.Ogg320);
        var player = new HeadlessFakePlayer();
        H.ScriptAction action = default;
        H.StepResult failed = default;
        for (int tick = 0; tick < 2_000 && !action.Finished; tick++)
        {
            action = script.Tick(player.Snap());
            if (action.Verb != H.Verb.None) player.Apply(action);
            if (action.Verb == H.Verb.Play) player.Xruns++;                     // one xrun during the first seconds
            while (script.TryTakeResult(out H.StepResult r)) if (!r.Ok) failed = r;
            player.Advance(100);
        }
        Assert.Equal(H.ExitCode.Assertion, action.ExitCode);
        Assert.Equal("expect xruns==0", failed.Cmd);                             // the marks in between never hid it
    }

    [Fact]
    public void FaultDuringAWait_YieldsOne()
    {
        H.Script script = HeadlessFixtures.Load("wait playing timeout 5000\nquit");
        Assert.False(script.Tick(HeadlessFixtures.Snap(0, "Loading")).Finished);
        Assert.False(script.Tick(HeadlessFixtures.Snap(100, "Loading") with { Fault = "Network" }).Finished);
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.False(r.Ok);
        Assert.Equal(H.ExitCode.Fault, r.Code);
        Assert.Contains("Network", r.Reason);
        H.ScriptAction done = script.Tick(HeadlessFixtures.Snap(200, "Loading"));
        Assert.True(done.Finished);
        Assert.Equal(H.ExitCode.Fault, done.ExitCode);
    }

    [Fact]
    public void ASessionFailure_IsAFaultToo()
    {
        H.Script script = HeadlessFixtures.Load("wait playing");
        script.Tick(HeadlessFixtures.Snap(0, "Idle", session: "Failed"));
        Assert.Equal(H.ExitCode.Fault, script.Tick(HeadlessFixtures.Snap(100)).ExitCode);
    }

    [Fact]
    public void ANeverSatisfiedWait_YieldsTwo_AtTheDeadline_AndNotBefore()
    {
        H.Script script = HeadlessFixtures.Load("wait playing timeout 1000");
        for (long now = 0; now < 1_000; now += 100) Assert.False(script.Tick(HeadlessFixtures.Snap(now)).Finished);
        Assert.False(script.TryTakeResult(out _));
        script.Tick(HeadlessFixtures.Snap(1_000));
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.Equal(H.ExitCode.Assertion, r.Code);
        Assert.Equal(1_000, r.ElapsedMs);
        Assert.Equal(H.ExitCode.Assertion, script.Tick(HeadlessFixtures.Snap(1_100)).ExitCode);
    }

    [Fact]
    public void ASoftExpect_ThatIsFalse_Continues_AndTheVerdictIsStillZero()
    {
        H.Script script = HeadlessFixtures.Load("expect? format==flac24\npause\nquit");
        var ogg = H.StatusSnapshot.Empty with { Format = "OGG 320" };
        Assert.Equal(H.Verb.None, script.Tick(ogg).Verb);
        Assert.Equal(H.Verb.Pause, script.Tick(ogg).Verb);
        H.ScriptAction quit = script.Tick(ogg);
        Assert.Equal(H.Verb.Quit, quit.Verb);
        Assert.Equal(H.ExitCode.Ok, quit.ExitCode);
        Assert.Equal(1, script.SoftFailed);
        Assert.Equal(0, script.Failed);
        Assert.True(script.TryTakeResult(out H.StepResult soft));
        Assert.True(soft.Soft);
        Assert.False(soft.Ok);
    }

    [Fact]
    public void QuitSeven_ExitsSeven()
    {
        H.ScriptAction action = HeadlessFixtures.Load("quit 7").Tick(H.StatusSnapshot.Empty);
        Assert.True(action.Finished);
        Assert.Equal(H.Verb.Quit, action.Verb);
        Assert.Equal(7, action.ExitCode);
    }

    [Fact]
    public void Tick_ReturnsExactlyOneActionPerTick()
    {
        H.Script script = HeadlessFixtures.Load("pause\nresume\nstatus\nquit");
        Assert.Equal(H.Verb.Pause, script.Tick(H.StatusSnapshot.Empty).Verb);
        Assert.Equal(H.Verb.Resume, script.Tick(H.StatusSnapshot.Empty).Verb);
        Assert.Equal(H.Verb.Status, script.Tick(H.StatusSnapshot.Empty).Verb);
        H.ScriptAction last = script.Tick(H.StatusSnapshot.Empty);
        Assert.Equal(H.Verb.Quit, last.Verb);
        Assert.True(last.Finished);
        int results = 0;
        while (script.TryTakeResult(out _)) results++;
        Assert.Equal(4, results);
    }

    [Fact]
    public void AHardFailure_StopsTheScript()
    {
        H.Script script = HeadlessFixtures.Load("expect playing\npause\nquit 0");
        Assert.Equal(H.Verb.None, script.Tick(HeadlessFixtures.Snap()).Verb);
        H.ScriptAction next = script.Tick(HeadlessFixtures.Snap());
        Assert.True(next.Finished);
        Assert.Equal(H.Verb.None, next.Verb);                                   // neither the pause nor the quit ran
        Assert.Equal(H.ExitCode.Assertion, next.ExitCode);
        Assert.Equal(1, script.Executed);
    }

    [Fact]
    public void Sleep_HoldsUntilItsDeadline()
    {
        H.Script script = HeadlessFixtures.Load("sleep 500\npause");
        Assert.Equal(H.Verb.None, script.Tick(HeadlessFixtures.Snap(0)).Verb);
        Assert.Equal(H.Verb.None, script.Tick(HeadlessFixtures.Snap(400)).Verb);
        Assert.Equal(H.Verb.None, script.Tick(HeadlessFixtures.Snap(500)).Verb);
        Assert.Equal(H.Verb.Pause, script.Tick(HeadlessFixtures.Snap(600)).Verb);
    }

    [Fact]
    public void StatsMark_IsWhatDeltasAreMeasuredFrom()
    {
        H.Script script = HeadlessFixtures.Load("stats mark\nexpect cdn.requests<=1\nexpect cdn.requests<=1");
        Assert.Equal(H.Verb.Stats, script.Tick(H.StatusSnapshot.Empty with { CdnRequests = 10 }).Verb);
        Assert.Equal(10, script.Mark.CdnRequests);
        script.Tick(H.StatusSnapshot.Empty with { CdnRequests = 11 });
        script.Tick(H.StatusSnapshot.Empty with { CdnRequests = 13 });
        Assert.Equal(1, script.Failed);
    }

    [Fact]
    public void StatsReset_MarksTheEmptySnapshot()
    {
        H.Script script = HeadlessFixtures.Load("stats mark\nstats reset\nexpect xruns==0");
        script.Tick(H.StatusSnapshot.Empty with { Xruns = 2 });
        script.Tick(H.StatusSnapshot.Empty with { Xruns = 2 });
        script.Tick(H.StatusSnapshot.Empty with { Xruns = 2 });
        Assert.Equal(1, script.Failed);                                          // two xruns over the whole run
    }

    [Fact]
    public void Reject_FailsTheExecutedStep_AndStopsAFileScript()
    {
        H.Script script = HeadlessFixtures.Load("play " + HeadlessFixtures.Track + "\nquit");
        Assert.Equal(H.Verb.Play, script.Tick(H.StatusSnapshot.Empty).Verb);
        script.Reject("the session is not online", H.ExitCode.Fault);
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.False(r.Ok);
        Assert.Equal(H.ExitCode.Fault, r.Code);
        Assert.Equal("the session is not online", r.Reason);
        Assert.False(script.TryTakeResult(out _));
        H.ScriptAction done = script.Tick(H.StatusSnapshot.Empty);
        Assert.True(done.Finished);
        Assert.Equal(H.ExitCode.Fault, done.ExitCode);
    }

    [Fact]
    public void Reject_WithNoActionThisTick_IsANoOp()
    {
        H.Script script = HeadlessFixtures.Load("wait playing");
        script.Tick(H.StatusSnapshot.Empty);
        script.Reject("nothing to reject");
        Assert.Equal(0, script.Failed);
    }

    [Fact]
    public void TryLoad_ReportsTheBadLineNumber()
    {
        Assert.False(H.Script.TryLoad("pause\n\nfoo bar\nquit\n", out _, out int line, out string error));
        Assert.Equal(3, line);
        Assert.Equal("unknown command 'foo'", error);
    }

    [Fact]
    public void TryLoad_SkipsCommentsAndKeepsLineNumbers()
    {
        H.Script script = HeadlessFixtures.Load("\uFEFF# header\r\n\r\npause\r\n  # note\r\nquit\r\n");
        Assert.Equal(2, script.StepCount);
        script.Tick(H.StatusSnapshot.Empty);
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.Equal(3, r.Line);
    }

    [Fact]
    public void LibrarySyncScript_Verbatim_Parses()
    {
        // G-239: ops/headless/library-sync.wh parses under the same grammar every other .wh script does - `wait
        // online`, `sleep`, `log <text>`, `status`, `quit`, five steps, none of them a library/rootlist condition
        // (the grammar has none; see the script's own header for why it can only prove the wiring did not fault).
        H.Script script = HeadlessFixtures.Load(HeadlessFixtures.LibrarySync);
        Assert.Equal(5, script.StepCount);
    }

    [Fact]
    public void AnEmptyScript_FinishesOk()
    {
        H.ScriptAction action = HeadlessFixtures.Load("# nothing to do\n").Tick(H.StatusSnapshot.Empty);
        Assert.True(action.Finished);
        Assert.Equal(H.ExitCode.Ok, action.ExitCode);
    }

    [Fact]
    public void AWait_RecordsItsElapsedTime()
    {
        H.Script script = HeadlessFixtures.Load("wait playing timeout 5000");
        for (long now = 0; now < 700; now += 100) script.Tick(HeadlessFixtures.Snap(now, "Loading"));
        script.Tick(HeadlessFixtures.Snap(700, "Playing"));
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.True(r.Ok);
        Assert.Equal(700, r.ElapsedMs);
        Assert.Equal(1, r.N);
        Assert.Equal("wait playing timeout 5000", r.Cmd);
    }

    [Fact]
    public void AWait_WithNoTimeout_UsesTheDefault()
    {
        H.Script script = HeadlessFixtures.Load("wait playing");
        script.Tick(HeadlessFixtures.Snap(0));
        script.Tick(HeadlessFixtures.Snap(H.Script.DefaultWaitMs - 1));
        Assert.False(script.TryTakeResult(out _));
        script.Tick(HeadlessFixtures.Snap(H.Script.DefaultWaitMs));
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.Equal(H.ExitCode.Assertion, r.Code);
    }

    [Fact]
    public void Interactive_WaitsForInput_AndFinishesWhenTheInputCloses()
    {
        H.Script script = H.Script.Interactive();
        Assert.False(script.Tick(H.StatusSnapshot.Empty).Finished);
        script.Append(HeadlessFixtures.Parse("""{"cmd":"pause","id":4}"""));
        Assert.Equal(H.Verb.Pause, script.Tick(H.StatusSnapshot.Empty).Verb);
        Assert.True(script.TryTakeResult(out H.StepResult r));
        Assert.Equal(4, r.Id);
        Assert.False(script.Tick(H.StatusSnapshot.Empty).Finished);
        script.CloseInput();
        H.ScriptAction done = script.Tick(H.StatusSnapshot.Empty);
        Assert.True(done.Finished);
        Assert.Equal(H.ExitCode.Ok, done.ExitCode);
    }

    [Fact]
    public void Interactive_AFailureDoesNotStop_ButCountsInTheVerdict()
    {
        H.Script script = H.Script.Interactive();
        script.Append(HeadlessFixtures.Parse("expect playing"));
        script.Append(HeadlessFixtures.Parse("pause"));
        Assert.Equal(H.Verb.None, script.Tick(HeadlessFixtures.Snap()).Verb);
        Assert.Equal(H.Verb.Pause, script.Tick(HeadlessFixtures.Snap()).Verb);
        script.CloseInput();
        Assert.Equal(H.ExitCode.Assertion, script.Tick(HeadlessFixtures.Snap()).ExitCode);
    }

    [Fact]
    public void Interactive_QuitEndsTheRunWithItsCode()
    {
        H.Script script = H.Script.Interactive();
        script.Append(HeadlessFixtures.Parse("quit 3"));
        H.ScriptAction quit = script.Tick(H.StatusSnapshot.Empty);
        Assert.True(quit.Finished);
        Assert.Equal(3, quit.ExitCode);
        Assert.Equal(3, script.Tick(H.StatusSnapshot.Empty).ExitCode);
    }

    [Fact]
    public void Verdict_AFaultOutranksAnAssertion()
    {
        H.Script script = H.Script.Interactive();
        script.Append(HeadlessFixtures.Parse("expect playing"));
        script.Append(HeadlessFixtures.Parse("wait playing"));
        script.Tick(HeadlessFixtures.Snap());
        script.Tick(HeadlessFixtures.Snap() with { Fault = "Unavailable" });
        Assert.Equal(H.ExitCode.Fault, script.Verdict());
    }
}

// ── the JSON lines ───────────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessJsonTests
{
    static void HasKeys(JsonElement e, string kind, params string[] keys)
    {
        Assert.Equal(kind, e.GetProperty("kind").GetString());
        Assert.True(e.TryGetProperty("t", out _));
        foreach (string key in keys) Assert.True(e.TryGetProperty(key, out _), kind + " lacks '" + key + "'");
    }

    static H.StatusSnapshot Playing => H.StatusSnapshot.Empty with
    {
        NowMs = 1492, SessionPhase = "Online", Tier = "Premium", Country = "NL", Phase = "Playing",
        TrackUri = HeadlessFixtures.Track, PositionMs = 12_000, DurationMs = 207_959, Format = "OGG 320",
        CdnRequests = 10, Xruns = 1, GaplessExact = 2, LastSeekMs = 120_000, LastSeekLatencyMs = 143, LastSeekKind = 1,
        DecodeXRealtime = float.NaN,
    };

    [Fact]
    public void Boot() => HasKeys(HeadlessFixtures.Json(H.JsonLine.Boot(12, @"C:\Users\x\AppData\Local\Wavee", "dpapi", "someone", false, "wasapi")),
        "boot", "profile", "credential", "account", "store", "endpoint");

    [Fact]
    public void Boot_NeverCarriesTheAccountUnredacted()
    {
        const string account = "christos.example.account";
        string line = H.JsonLine.Boot(1, "p", "dpapi", account, false, "silent");
        Assert.DoesNotContain(account, line);
        Assert.Equal(Platform.Redact(account), HeadlessFixtures.Json(line).GetProperty("account").GetString());
    }

    [Fact]
    public void Session()
    {
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Session(842, "Online", "Premium", "NL"));
        HasKeys(e, "session", "phase", "tier", "country");
        Assert.False(e.TryGetProperty("fault", out _));
    }

    [Fact]
    public void State() => HasKeys(HeadlessFixtures.Json(H.JsonLine.State(1, Playing)), "state", "phase", "track", "pos", "dur", "format", "buffering");

    [Fact]
    public void Status()
    {
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Status(1, Playing, 5));
        HasKeys(e, "status", "id", "session", "tier", "country", "phase", "track", "pos", "dur", "owner", "volume");
        Assert.Equal(5, e.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Reply()
    {
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Reply(1201, 1, false, "play x", "the session is not online"));
        HasKeys(e, "reply", "id", "ok", "cmd", "error");
        Assert.False(e.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Step()
    {
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Step(11_500, new H.StepResult(3, 7, false, false, "wait position>=10s", 10_008, 2, "timed out", -1)));
        HasKeys(e, "step", "n", "line", "ok", "cmd", "elapsedMs", "code", "reason");
        Assert.Equal(10_008L, e.GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public void Stats_PrintsDeltasAgainstTheMark()
    {
        var mark = H.StatusSnapshot.Empty with { CdnRequests = 6, Xruns = 1 };
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Stats(30_011, Playing, mark));
        HasKeys(e, "stats", "cdn", "ring", "audio");
        Assert.Equal(4L, e.GetProperty("cdn").GetProperty("requests").GetInt64());
        Assert.Equal(0, e.GetProperty("audio").GetProperty("xruns").GetInt32());
        Assert.Equal(2, e.GetProperty("audio").GetProperty("gapless").GetProperty("exact").GetInt32());
        Assert.Equal("far", e.GetProperty("audio").GetProperty("lastSeek").GetProperty("kind").GetString());
        Assert.True(e.GetProperty("audio").GetProperty("decodeXRealtime").GetDouble() == 0.0);   // NaN is written as 0, never thrown
    }

    [Fact]
    public void Verdict()
    {
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Verdict(30_020, true, 9, 0, 0));
        HasKeys(e, "verdict", "ok", "steps", "failed", "code", "meaning");
        Assert.Equal("ok", e.GetProperty("meaning").GetString());
    }

    [Fact]
    public void Fault() => HasKeys(HeadlessFixtures.Json(H.JsonLine.Fault(0, "no-credential", "sign in once")), "fault", "reason", "hint");

    [Fact]
    public void Echo_QuotesAndBackslashesStayValidJson()
    {
        string raw = "I [audio] audio.open fmt=OggVorbis320 path=\"C:\\x\" head=81920";
        JsonElement e = HeadlessFixtures.Json(H.JsonLine.Echo(5, "audio", raw));
        HasKeys(e, "echo", "category", "line");
        Assert.Equal(raw, e.GetProperty("line").GetString());
    }

    [Fact]
    public void Events()
    {
        HasKeys(HeadlessFixtures.Json(H.JsonLine.Seek(1, Playing)), "seek", "to", "latencyMs", "seekKind");
        HasKeys(HeadlessFixtures.Json(H.JsonLine.Gapless(1, Playing, H.StatusSnapshot.Empty)), "gapless", "exact", "degraded");
        HasKeys(HeadlessFixtures.Json(H.JsonLine.Underrun(1, 3, 1)), "underrun", "xruns", "new");
        HasKeys(HeadlessFixtures.Json(H.JsonLine.EndOfQueue(1, HeadlessFixtures.Track)), "end", "track");
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(2, "assertion")]
    [InlineData(67, "no stored credential")]
    [InlineData(77, "credential rejected")]
    [InlineData(7, "quit 7")]
    public void ExitCodes_Describe(int code, string meaning) => Assert.Equal(meaning, H.ExitCode.Describe(code));
}

// ── the store wrappers ───────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessStoreTests
{
    [Fact]
    public void Overlay_AWriteIsVisible_AndNeverReachesTheInnerStore()
    {
        var inner = new HeadlessRecordingSettings();
        var overlay = new H.OverlaySettings(inner);
        overlay.Set(Platform.Keys.PlaybackQuality, 3);
        Assert.Equal(3, overlay.Get(Platform.Keys.PlaybackQuality));
        Assert.Equal(0, inner.Writes);
        Assert.Equal(Platform.Keys.PlaybackQuality.Default, inner.Get(Platform.Keys.PlaybackQuality));
        Assert.Equal(1, overlay.WriteCount);
    }

    [Fact]
    public void Overlay_ReadsFallThrough()
    {
        var inner = new HeadlessRecordingSettings();
        inner.Values[Platform.Keys.SavedVolume.Name] = 0.35f;
        var overlay = new H.OverlaySettings(inner);
        Assert.True(overlay.Get(Platform.Keys.SavedVolume) == 0.35f);
        Assert.Equal(Platform.Keys.CrossfadeMs.Default, overlay.Get(Platform.Keys.CrossfadeMs));
    }

    [Fact]
    public void Protected_RemovingTheCredential_IsRefusedAndReported()
    {
        var inner = new HeadlessMemoryStore();
        inner.Values[Platform.CredentialKey] = "dpapi:blob";
        string? refused = null;
        var store = new H.ProtectedLocalStore(inner, "headless", k => refused = k);
        Platform.ClearCredential(store);                                        // what SessionEffects.ClearCredential reaches
        Assert.Equal("dpapi:blob", inner.Get(Platform.CredentialKey));
        Assert.Equal(Platform.CredentialKey, refused);
    }

    [Fact]
    public void Protected_RemovingAnythingElse_Passes()
    {
        var inner = new HeadlessMemoryStore();
        inner.Values["other"] = "x";
        new H.ProtectedLocalStore(inner, "headless").Remove("other");
        Assert.Null(inner.Get("other"));
    }

    [Fact]
    public void Protected_TheDeviceId_IsNamespacedBothWays()
    {
        var inner = new HeadlessMemoryStore();
        inner.Values["device.id"] = "gui-device";
        var store = new H.ProtectedLocalStore(inner, "headless");
        Assert.Null(store.Get("device.id"));
        store.Set("device.id", "headless-device");
        Assert.Equal("headless-device", inner.Get("device.id.headless"));
        Assert.Equal("gui-device", inner.Get("device.id"));
        Assert.Equal("headless-device", store.Get("device.id"));
    }

    [Fact]
    public void Protected_TheWelcomeRefresh_IsSaved()
    {
        var inner = new HeadlessMemoryStore();
        var store = new H.ProtectedLocalStore(inner, "headless");
        var protector = new NoOpProtector();
        Platform.SaveCredential(store, protector, new Credential(CredentialKind.ReusableBlob, "someone", "c2VjcmV0", null));
        Assert.True(inner.Values.ContainsKey(Platform.CredentialKey));
        Assert.True(Platform.TryLoadCredential(store, protector, out Credential loaded));
        Assert.Equal("someone", loaded.Username);
    }
}

// ── the context queue ────────────────────────────────────────────────────────────────────────────────────────────────

public class HeadlessContextQueueTests
{
    static EntityRef[] Members(int n)
    {
        var members = new EntityRef[n];
        for (int i = 0; i < n; i++) members[i] = new EntityRef(EntityKind.Track, 10 + i);
        return members;
    }

    [Fact]
    public void NowPlayingThenNextUp_FromTheStart()
    {
        var refs = new EntityRef[8];
        var rows = new QueueEdge[8];
        int n = H.BuildContextQueue(Members(5), 2, refs, rows);
        Assert.Equal(3, n);
        Assert.Equal(new EntityRef(EntityKind.Track, 12), refs[0]);
        Assert.Equal((byte)QueueBucket.NowPlaying, rows[0].Bucket);
        Assert.Equal((byte)QueueBucket.NextUp, rows[1].Bucket);
        Assert.Equal((byte)QueueBucket.NextUp, rows[2].Bucket);
        Assert.All(rows[..n], r => Assert.Equal((byte)QueueProvider.Context, r.Provider));
    }

    [Fact]
    public void TheRows_SatisfyTheQueueInvariant()
    {
        var refs = new EntityRef[12];
        var rows = new QueueEdge[12];
        int n = H.BuildContextQueue(Members(12), 0, refs, rows);
        Assert.True(Queue.IsOrdered(rows.AsSpan(0, n)));
    }

    [Fact]
    public void BoundedByTheDestinations_AndAnOutOfRangeStartWritesNothing()
    {
        var refs = new EntityRef[2];
        var rows = new QueueEdge[4];
        Assert.Equal(2, H.BuildContextQueue(Members(5), 0, refs, rows));
        Assert.Equal(0, H.BuildContextQueue(Members(5), 5, refs, rows));
        Assert.Equal(0, H.BuildContextQueue(Members(5), -1, refs, rows));
    }
}

// ── the options and the loop (Diagnostics.Probe.cs's pure pieces) ───────────────────────────────────────────────────

public class HeadlessOptionsTests
{
    [Fact]
    public void EveryFlag()
    {
        string[] args = ["--headless", "--script", "ops/headless/ogg320.wh", "--profile", @"C:\temp\p", "--store", "--silent",
                         "--no-login", "--connect", "--login-timeout", "5000", "--timeout", "90000", "--echo-log"];
        Assert.True(Diagnostics.HeadlessOptions.TryParse(args, out Diagnostics.HeadlessOptions o, out string usage), usage);
        Assert.Equal("ops/headless/ogg320.wh", o.Script);
        Assert.Equal(@"C:\temp\p", o.Profile);
        Assert.True(o.Store && o.Silent && o.NoLogin && o.Connect && o.EchoLog);
        Assert.Equal(5_000, o.LoginTimeoutMs);
        Assert.Equal(90_000, o.RunTimeoutMs);
        Assert.Equal("", o.Pipe);
    }

    [Fact]
    public void Defaults()
    {
        Assert.True(Diagnostics.HeadlessOptions.TryParse(["--headless"], out Diagnostics.HeadlessOptions o, out _));
        Assert.Equal("", o.Script);
        Assert.Equal(Diagnostics.HeadlessOptions.DefaultLoginTimeoutMs, o.LoginTimeoutMs);
        Assert.Equal(0, o.RunTimeoutMs);
        Assert.False(o.Silent || o.Store || o.NoLogin || o.Connect || o.EchoLog);
    }

    [Fact]
    public void ScriptAndPipeTogether_IsUsage()
    {
        Assert.False(Diagnostics.HeadlessOptions.TryParse(["--headless", "--script", "a.wh", "--pipe", "wavee-hl"], out _, out string usage));
        Assert.Contains("cannot be combined", usage);
    }

    [Theory]
    [InlineData("--script")]
    [InlineData("--pipe")]
    [InlineData("--profile")]
    [InlineData("--timeout")]
    public void AMissingValue_IsUsage(string flag)
        => Assert.False(Diagnostics.HeadlessOptions.TryParse(["--headless", flag], out _, out _));

    [Fact]
    public void AFlagWhereAValueBelongs_IsUsage()
        => Assert.False(Diagnostics.HeadlessOptions.TryParse(["--headless", "--script", "--silent"], out _, out _));

    [Theory]
    [InlineData("--fake")]
    [InlineData("--frames")]
    [InlineData("script.wh")]
    public void AnUnknownArgument_IsUsage(string arg)
    {
        Assert.False(Diagnostics.HeadlessOptions.TryParse(["--headless", arg], out _, out string usage));
        Assert.Contains(arg, usage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    public void ANonPositiveTimeout_IsUsage(string value)
        => Assert.False(Diagnostics.HeadlessOptions.TryParse(["--headless", "--login-timeout", value], out _, out _));

    [Fact]
    public void ProfileArg_ReadsTheValueOrNothing()
    {
        Assert.Equal(@"D:\p", Diagnostics.Probe.ProfileArg(["--headless", "--profile", @"D:\p"]));
        Assert.Equal("", Diagnostics.Probe.ProfileArg(["--headless"]));
        Assert.Equal("", Diagnostics.Probe.ProfileArg(["--profile"]));
    }
}

public class HeadlessLoopTests
{
    [Fact]
    public void PostsFromThreeThreads_RunOnTheOneConsumerThread_InOrder()
    {
        var loop = new Diagnostics.HeadlessLoop();
        const int perThread = 500;
        var seen = new List<(int Producer, int Seq, int Thread)>();
        var producers = new Thread[3];
        for (int p = 0; p < producers.Length; p++)
        {
            int producer = p;
            producers[p] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    int seq = i;
                    loop.Post(() => seen.Add((producer, seq, Environment.CurrentManagedThreadId)));
                }
            });
        }
        var consumer = new Thread(loop.Run);
        consumer.Start();
        foreach (Thread t in producers) t.Start();
        foreach (Thread t in producers) t.Join();
        loop.Stop();
        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(3 * perThread, seen.Count);
        Assert.Single(seen.Select(s => s.Thread).Distinct());
        Assert.Equal(consumer.ManagedThreadId, seen[0].Thread);
        for (int p = 0; p < 3; p++)
        {
            int[] order = seen.Where(s => s.Producer == p).Select(s => s.Seq).ToArray();
            Assert.Equal(Enumerable.Range(0, perThread), order);
        }
    }

    [Fact]
    public void Stop_DrainsWhatIsQueued_AndRunReturns()
    {
        var loop = new Diagnostics.HeadlessLoop();
        int ran = 0;
        for (int i = 0; i < 10; i++) loop.Post(() => ran++);
        loop.Stop();
        loop.Run();
        Assert.Equal(10, ran);
        Assert.True(loop.IsStopped);
    }

    [Fact]
    public void APostAfterTheStop_IsDropped_NotThrown()
    {
        var loop = new Diagnostics.HeadlessLoop();
        loop.Stop();
        int ran = 0;
        loop.Post(() => ran++);
        loop.Run();
        Assert.Equal(0, ran);
    }

    [Fact]
    public void ThePostsOfTheLoopItself_RunOnTheLoop()
    {
        var loop = new Diagnostics.HeadlessLoop();
        var order = new List<int>();
        loop.Post(() => { order.Add(1); loop.Post(() => { order.Add(3); loop.Stop(); }); order.Add(2); });
        loop.Run();
        Assert.Equal(new[] { 1, 2, 3 }, order.ToArray());
        Assert.True(loop.NowMs >= 0);
    }
}
