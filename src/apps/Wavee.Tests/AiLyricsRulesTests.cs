// ── Wavee.Tests/AiLyricsRulesTests.cs — every pure decision of the on-device AI lyrics feature ──────────────────────
//
// `AiLyrics.Rules` is CORE: no I/O, no clock, no engine loop. Nothing here boots `Entities` (no TestScope needed): the
// rules take plain values, the NPU list is hand-built `ComputeAdapterInfo`s and the speed meter gets a synthetic clock.

using FluentGpu.WindowsApi.Devices;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsRulesTests
{
    const uint Qualcomm = 0x4D4F4351, Intel = 0x8086, Amd = 0x1002, Nvidia = 0x10DE;

    static ComputeAdapterInfo Npu(uint vendor, bool hardware = true)
        => new(ComputeAdapterKind.Npu, vendor, 0x1, 0, 0, "Test NPU", 0x001F_0000_00D2_0005UL, hardware, true, 0);

    static Lyrics.Doc LineDoc(params string[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        for (int i = 0; i < lines.Length; i++) l.Add(new Lyrics.Line(i * 4000L, lines[i], []));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Line, "spotify");
    }

    static Lyrics.Doc PlainDoc(params string[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var t in lines) l.Add(new Lyrics.Line(0, t, []));
        return new Lyrics.Doc("t1", false, l, Lyrics.SyncKind.Unsynced, "lrclib");
    }

    static Lyrics.Doc WordDoc(params string[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            long ms = i * 4000L;
            l.Add(new Lyrics.Line(ms, lines[i], [new Lyrics.Syllable(ms, ms + 500, lines[i])], ms + 500, null, null, true));
        }
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Syllable, "musixmatch");
    }

    static readonly string[] SpanishLines = ["Sí, sabes que ya llevo un rato mirándote", "Tengo que bailar contigo hoy", "Vi que tu mirada ya estaba llamándome"];
    static readonly string[] English = ["hello darkness my old friend", "ive come to talk with you again", "because a vision softly creeping"];
    static readonly string[] OnlyEn = ["en"];

    static AiLyrics.SkipReason Elig(Lyrics.Doc? doc, EntityKind kind = EntityKind.Track, bool spotifyUri = true,
        long durationMs = 200_000, bool wordSync = true, bool plainText = true, IReadOnlyList<string>? languages = null,
        bool energySaver = false, bool keepOnSaver = false, AiLyrics.SetupPhase phase = AiLyrics.SetupPhase.Ready)
        => AiLyrics.Rules.Eligibility(doc, kind, spotifyUri, durationMs, wordSync, plainText, languages ?? OnlyEn,
            energySaver, keepOnSaver, phase);

    // ── availability ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, 26100, Qualcomm, AiLyrics.Availability.NotArm64)]
    [InlineData(false, 22631, Qualcomm, AiLyrics.Availability.NotArm64)]
    [InlineData(true, 26099, Qualcomm, AiLyrics.Availability.OsTooOld)]
    [InlineData(true, 22631, Qualcomm, AiLyrics.Availability.OsTooOld)]
    [InlineData(true, 26100, Qualcomm, AiLyrics.Availability.Available)]
    [InlineData(true, 26340, Qualcomm, AiLyrics.Availability.Available)]
    [InlineData(true, 26100, Intel, AiLyrics.Availability.NpuNotSupported)]
    [InlineData(true, 26100, Amd, AiLyrics.Availability.NpuNotSupported)]
    [InlineData(true, 26100, Nvidia, AiLyrics.Availability.NpuNotSupported)]
    [InlineData(true, 26100, 0u, AiLyrics.Availability.NpuNotSupported)]
    public void Availability_matrix(bool arm64, int build, uint vendor, AiLyrics.Availability expected)
        => Assert.Equal(expected, AiLyrics.Rules.Availability(arm64, build, [Npu(vendor)]));

    [Fact]
    public void No_npu_or_only_software_adapters_is_NoNpu()
    {
        Assert.Equal(AiLyrics.Availability.NoNpu, AiLyrics.Rules.Availability(true, 26100, []));
        Assert.Equal(AiLyrics.Availability.NoNpu, AiLyrics.Rules.Availability(true, 26100, [Npu(Qualcomm, hardware: false)]));
    }

    [Fact]
    public void A_qualcomm_npu_anywhere_in_the_list_is_enough()
    {
        Assert.Equal(AiLyrics.Availability.Available, AiLyrics.Rules.Availability(true, 26100, [Npu(Intel), Npu(Qualcomm)]));
        var npu = AiLyrics.Rules.SupportedNpu([Npu(Intel), Npu(Qualcomm)]);
        Assert.NotNull(npu);
        Assert.Equal(Qualcomm, npu?.VendorId);
        Assert.Null(AiLyrics.Rules.SupportedNpu([Npu(Intel), Npu(Qualcomm, hardware: false)]));
    }

    [Theory]
    [InlineData(AiLyrics.Availability.NotArm64, "settings.lyrics.ai.unavailable.arm64")]
    [InlineData(AiLyrics.Availability.OsTooOld, "settings.lyrics.ai.unavailable.os")]
    [InlineData(AiLyrics.Availability.NoNpu, "settings.lyrics.ai.unavailable.noNpu")]
    [InlineData(AiLyrics.Availability.NpuNotSupported, "settings.lyrics.ai.unavailable.npuVendor")]
    [InlineData(AiLyrics.Availability.Available, "settings.lyrics.ai.sub")]
    public void Unavailable_reason_keys(AiLyrics.Availability a, string key) => Assert.Equal(key, AiLyrics.Rules.UnavailableKey(a));

    [Fact]
    public void Initial_phase_and_toggle_gate()
    {
        Assert.Equal(AiLyrics.SetupPhase.Unavailable, AiLyrics.Rules.InitialPhase(AiLyrics.Availability.NoNpu, true, true));
        Assert.Equal(AiLyrics.SetupPhase.Off, AiLyrics.Rules.InitialPhase(AiLyrics.Availability.Available, false, true));
        Assert.Equal(AiLyrics.SetupPhase.NeedsSetup, AiLyrics.Rules.InitialPhase(AiLyrics.Availability.Available, true, false));
        Assert.Equal(AiLyrics.SetupPhase.Ready, AiLyrics.Rules.InitialPhase(AiLyrics.Availability.Available, true, true));

        Assert.True(AiLyrics.Rules.CanToggleOn(AiLyrics.Availability.Available));
        Assert.False(AiLyrics.Rules.CanToggleOn(AiLyrics.Availability.OsTooOld));
        Assert.False(AiLyrics.Rules.CanToggleOn(AiLyrics.Availability.NpuNotSupported));
    }

    [Fact]
    public void Metered_confirm_only_above_50_MB_on_a_metered_link()
    {
        const long Mb50 = 50L * 1024 * 1024;
        Assert.False(AiLyrics.Rules.NeedsMeteredConfirm(false, 700L * 1024 * 1024));
        Assert.False(AiLyrics.Rules.NeedsMeteredConfirm(true, Mb50));
        Assert.True(AiLyrics.Rules.NeedsMeteredConfirm(true, Mb50 + 1));
    }

    // ── eligibility ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_line_synced_english_song_is_eligible()
    {
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English)));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(PlainDoc(English)));
    }

    [Theory]
    [InlineData(AiLyrics.SetupPhase.NeedsSetup)]
    [InlineData(AiLyrics.SetupPhase.Downloading)]
    [InlineData(AiLyrics.SetupPhase.Preparing)]
    [InlineData(AiLyrics.SetupPhase.Error)]
    public void Not_ready_is_NeedsSetup_before_anything_else(AiLyrics.SetupPhase phase)
        => Assert.Equal(AiLyrics.SkipReason.NeedsSetup, Elig(null, EntityKind.Episode, spotifyUri: false, phase: phase));

    [Fact]
    public void Podcasts_and_non_spotify_audio_are_refused_before_the_doc_is_looked_at()
    {
        Assert.Equal(AiLyrics.SkipReason.Podcast, Elig(LineDoc(English), EntityKind.Episode));
        Assert.Equal(AiLyrics.SkipReason.Podcast, Elig(null, EntityKind.Show, spotifyUri: false));
        Assert.Equal(AiLyrics.SkipReason.NotSpotifyAudio, Elig(null, spotifyUri: false));
    }

    [Fact]
    public void Missing_or_empty_lyrics_are_NoLyrics()
    {
        Assert.Equal(AiLyrics.SkipReason.NoLyrics, Elig(null));
        Assert.Equal(AiLyrics.SkipReason.NoLyrics, Elig(Lyrics.Doc.Empty("t1")));
        Assert.Equal(AiLyrics.SkipReason.NoLyrics, Elig(LineDoc("", "   ")));
        Assert.Equal(AiLyrics.SkipReason.NoLyrics, Elig(LineDoc(English) with { Sync = Lyrics.SyncKind.None }));
    }

    [Fact]
    public void A_doc_with_real_word_timing_is_left_alone()
    {
        Assert.Equal(AiLyrics.SkipReason.AlreadyWordByWord, Elig(WordDoc(English)));
        // a line-synced row that merely carries syllables (IsWordByWord false) still gets timed
        var doc = LineDoc(English);
        var lines = new List<Lyrics.Line>(doc.Lines) { [0] = doc.Lines[0] with { Syllables = [new Lyrics.Syllable(0, 100, "hello")] } };
        Assert.Equal(AiLyrics.SkipReason.None, Elig(doc with { Lines = lines }));
    }

    [Fact]
    public void Toggles_gate_by_doc_kind()
    {
        Assert.Equal(AiLyrics.SkipReason.WordSyncOff, Elig(LineDoc(English), wordSync: false));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English), plainText: false));
        Assert.Equal(AiLyrics.SkipReason.PlainTextOff, Elig(PlainDoc(English), plainText: false));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(PlainDoc(English), wordSync: false));
    }

    [Fact]
    public void Language_must_be_installed()
    {
        var spanish = LineDoc(SpanishLines) with { Language = "es" };
        Assert.Equal(AiLyrics.SkipReason.LanguageNotInstalled, Elig(spanish));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(spanish, languages: ["en", "es"]));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English) with { Language = "EN-us" }));
        Assert.Equal(AiLyrics.SkipReason.LanguageNotInstalled, Elig(LineDoc(Portuguese) with { Language = "fr" }, languages: ["en", "es"]));
    }

    [Fact]
    public void Too_long_then_battery_saver()
    {
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English), durationMs: 20L * 60 * 1000));
        Assert.Equal(AiLyrics.SkipReason.TooLong, Elig(LineDoc(English), durationMs: 20L * 60 * 1000 + 1));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English), durationMs: 0));
        Assert.Equal(AiLyrics.SkipReason.BatterySaver, Elig(LineDoc(English), energySaver: true));
        Assert.Equal(AiLyrics.SkipReason.None, Elig(LineDoc(English), energySaver: true, keepOnSaver: true));
        // order: TooLong wins over BatterySaver, LanguageNotInstalled over both
        Assert.Equal(AiLyrics.SkipReason.TooLong, Elig(LineDoc(English), durationMs: 30L * 60 * 1000, energySaver: true));
        Assert.Equal(AiLyrics.SkipReason.LanguageNotInstalled,
            Elig(LineDoc(SpanishLines) with { Language = "es" }, durationMs: 30L * 60 * 1000, energySaver: true));
    }

    // ── language, keys, hash ────────────────────────────────────────────────────────────────────────────────────────

    // words that read as neither English nor Spanish: only the tag can tell
    static readonly string[] Portuguese = ["eu sei que vou amar você", "minha vida inteira", "saudade do seu olhar"];

    [Theory]
    [InlineData("es", "es")]
    [InlineData("ES-mx", "es")]
    [InlineData("pt_BR", "pt")]
    [InlineData(" fr ", "fr")]
    public void LanguageOf_trusts_the_source_tag_when_the_words_cannot_tell(string tag, string expected)
        => Assert.Equal(expected, AiLyrics.Rules.LanguageOf(LineDoc(Portuguese) with { Language = tag }));

    [Fact]
    public void LanguageOf_recognises_korean_and_dutch_lyrics()
    {
        Assert.Equal("ko", AiLyrics.Rules.LanguageOf(LineDoc("너를 처음 만난 그날", "Baby 내 맘을 몰라", "사랑해 오늘 밤")));
        // decomposed Hangul (jamo) counts once composed
        Assert.Equal("ko", AiLyrics.Rules.LanguageOf(LineDoc("사랑해 너만을 바라봐".Normalize(System.Text.NormalizationForm.FormD), "오늘 밤 함께해")));
        // a single Korean word in an English song does not make it Korean
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc([.. English, "you are my love 사랑", "and the vision that was planted in my brain"])));
        Assert.Equal("nl", AiLyrics.Rules.LanguageOf(LineDoc("Ik wil je niet kwijt", "Want jij bent alles voor mij", "Zonder jou is het nacht")));
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc(English) with { Language = "nl" }));
    }

    [Theory]
    [InlineData("sv")]
    [InlineData("pt-BR")]
    [InlineData("es")]
    [InlineData("z1")]
    public void LanguageOf_lets_clearly_english_words_overrule_a_wrong_tag(string tag)
        => Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc(
            "Everybody loves you, baby", "You should trademark your face", "Linin' down the block to be around you",
            "But, baby, I'm first in place") with { Language = tag }));

    [Fact]
    public void LanguageOf_guesses_between_english_and_spanish()
    {
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc(English)));
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc(English) with { Language = "und" }));
        Assert.Equal("es", AiLyrics.Rules.LanguageOf(LineDoc("Dónde estás", "¿Por qué?")));
        Assert.Equal("es", AiLyrics.Rules.LanguageOf(LineDoc("Mañana")));
        Assert.Equal("es", AiLyrics.Rules.LanguageOf(LineDoc(
            "Sí, sabes que ya llevo un rato mirándote",
            "Tengo que bailar contigo hoy",
            "Vi que tu mirada ya estaba llamándome")));
        // a "la la la" chorus in an english song is not spanish
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(LineDoc([.. English, "la la la la", "and the vision that was planted in my brain"])));
        Assert.Equal("en", AiLyrics.Rules.LanguageOf(Lyrics.Doc.Empty("t1")));
    }

    [Fact]
    public void ResultsKey_is_file_name_safe_and_versioned()
    {
        Assert.Equal("spotify_track_4uLU6hMCjMI75M1A2tKUQC.v1", AiLyrics.Rules.ResultsKey("spotify:track:4uLU6hMCjMI75M1A2tKUQC", 1));
        Assert.NotEqual(AiLyrics.Rules.ResultsKey("spotify:track:x", 1), AiLyrics.Rules.ResultsKey("spotify:track:x", 2));
    }

    [Fact]
    public void SourceHash_follows_the_words_not_the_timing()
    {
        ulong a = AiLyrics.Rules.SourceHash(LineDoc(English).Lines);
        Assert.Equal(a, AiLyrics.Rules.SourceHash(LineDoc(English).Lines));
        Assert.Equal(a, AiLyrics.Rules.SourceHash(WordDoc(English).Lines));
        Assert.NotEqual(a, AiLyrics.Rules.SourceHash(LineDoc([.. English, "one more line"]).Lines));
        Assert.NotEqual(AiLyrics.Rules.SourceHash(LineDoc("ab", "c").Lines), AiLyrics.Rules.SourceHash(LineDoc("a", "bc").Lines));
        Assert.Equal(14695981039346656037UL, AiLyrics.Rules.SourceHash([]));
    }

    // ── progress and ETA ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpeedMeter_reports_at_most_every_100_ms_and_smooths_over_seconds()
    {
        var m = new AiLyrics.Rules.SpeedMeter();
        Assert.True(m.Sample(0, 0));
        Assert.False(m.Sample(10_000, 50));
        Assert.True(m.Sample(25_000, 120));
        Assert.Equal(0.0, m.BytesPerSecond);               // no 250 ms window yet

        Assert.True(m.Sample(250_000, 250));             // first window: 1 MB/s straight
        Assert.Equal(1_000_000.0, m.BytesPerSecond, 3);
        long done = 250_000, t = 250;
        for (int i = 0; i < 40; i++) { t += 250; done += 250_000; m.Sample(done, t); }
        Assert.Equal(1_000_000.0, m.BytesPerSecond, 3);    // steady stays steady

        t += 250; done += 500_000; m.Sample(done, t);    // speed doubles: the average moves ~8 % of the way
        Assert.InRange(m.BytesPerSecond, 1_050_000, 1_100_000);
        for (int i = 0; i < 120; i++) { t += 250; done += 500_000; m.Sample(done, t); }
        Assert.InRange(m.BytesPerSecond, 1_980_000, 2_000_001);
    }

    [Fact]
    public void Eta_seconds()
    {
        Assert.Equal(10.0, AiLyrics.Rules.EtaSeconds(500, 1500, 100));
        Assert.Equal(0.0, AiLyrics.Rules.EtaSeconds(2000, 1500, 100));
        Assert.Null(AiLyrics.Rules.EtaSeconds(0, 0, 100));
        Assert.Null(AiLyrics.Rules.EtaSeconds(0, 100, 0));
        Assert.Null(AiLyrics.Rules.EtaSeconds(0, 100, double.NaN));
    }

    [Theory]
    [InlineData(0.2, "settings.lyrics.ai.eta.seconds", 1)]
    [InlineData(42.1, "settings.lyrics.ai.eta.seconds", 43)]
    [InlineData(59.2, "settings.lyrics.ai.eta.minutes", 1)]
    [InlineData(125.0, "settings.lyrics.ai.eta.minutes", 3)]
    [InlineData(3599.0, "settings.lyrics.ai.eta.hours", 1)]
    [InlineData(5400.0, "settings.lyrics.ai.eta.hours", 2)]
    [InlineData(8640.0, "settings.lyrics.ai.eta.hours", 2)]
    public void Eta_key(double seconds, string key, int n)
    {
        Assert.Equal(key, AiLyrics.Rules.EtaKey(seconds, out int got));
        Assert.Equal(n, got);
    }

    [Fact]
    public void Eta_key_unknown()
    {
        Assert.Equal("settings.lyrics.ai.eta.unknown", AiLyrics.Rules.EtaKey(null, out int n));
        Assert.Equal(0, n);
        Assert.Equal("settings.lyrics.ai.eta.unknown", AiLyrics.Rules.EtaKey(double.PositiveInfinity, out _));
    }

    [Fact]
    public void Prepare_weights_floor_small_graphs_at_16_MiB()
    {
        var sizes = new Dictionary<string, long> { ["separator"] = 100L << 20, ["conv0"] = 17_000, ["layers_a"] = 16L << 20 };
        long[] w = AiLyrics.Rules.PrepareWeights(["separator", "conv0", "layers_a"], g => sizes[g]);
        Assert.Equal(new[] { 100L << 20, 16L << 20, 16L << 20 }, w);
        Assert.Empty(AiLyrics.Rules.PrepareWeights([], _ => 0));
    }

    [Fact]
    public void Prepare_fraction_by_weight_else_count()
    {
        Assert.Equal(0.25, AiLyrics.Rules.PrepareFraction(50, 200, 3, 4));
        Assert.Equal(0.25, AiLyrics.Rules.PrepareFraction(0, 0, 1, 4));
        Assert.Equal(0.0, AiLyrics.Rules.PrepareFraction(0, 0, 0, 0));
        Assert.Equal(1.0, AiLyrics.Rules.PrepareFraction(300, 200, 4, 4));
    }

    // ── errors ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AiLyrics.SetupError.Offline, "settings.lyrics.ai.error.offline", true, false)]
    [InlineData(AiLyrics.SetupError.Network, "settings.lyrics.ai.error.network", true, false)]
    [InlineData(AiLyrics.SetupError.NotFound, "settings.lyrics.ai.error.notFound", true, false)]
    [InlineData(AiLyrics.SetupError.HashMismatch, "settings.lyrics.ai.error.hash", true, false)]
    [InlineData(AiLyrics.SetupError.DiskFull, "settings.lyrics.ai.error.diskFull", true, false)]
    [InlineData(AiLyrics.SetupError.RuntimeLoad, "settings.lyrics.ai.error.runtime", true, true)]
    [InlineData(AiLyrics.SetupError.NoNpuDevice, "settings.lyrics.ai.error.noNpuDevice", true, false)]
    [InlineData(AiLyrics.SetupError.Compile, "settings.lyrics.ai.error.compile", true, true)]
    [InlineData(AiLyrics.SetupError.Unknown, "settings.lyrics.ai.error.unknown", true, false)]
    [InlineData(AiLyrics.SetupError.Cancelled, "settings.lyrics.ai.error.unknown", false, false)]
    public void Error_key_and_recovery(AiLyrics.SetupError e, string key, bool retry, bool remove)
    {
        Assert.Equal(key, AiLyrics.Rules.ErrorKey(e));
        Assert.Equal(retry, AiLyrics.Rules.ErrorOffersRetry(e));
        Assert.Equal(remove, AiLyrics.Rules.ErrorOffersRemove(e));
    }

    [Fact]
    public void Toasts_only_when_the_card_is_off_screen()
    {
        Assert.True(AiLyrics.Rules.ToastOnReady(false));
        Assert.False(AiLyrics.Rules.ToastOnReady(true));
        Assert.True(AiLyrics.Rules.ToastOnError(false, AiLyrics.SetupError.Network));
        Assert.False(AiLyrics.Rules.ToastOnError(true, AiLyrics.SetupError.Network));
        Assert.False(AiLyrics.Rules.ToastOnError(false, AiLyrics.SetupError.Cancelled));
    }

    // ── surfaces ────────────────────────────────────────────────────────────────────────────────────────────────────

    static AiLyrics.Status Status(AiLyrics.SetupPhase phase, bool enabled = true)
        => AiLyrics.Status.Unknown with { Phase = phase, Availability = AiLyrics.Availability.Available, Enabled = enabled };

    static AiLyrics.TrackStatus Track(AiLyrics.TrackPhase phase, AiLyrics.SkipReason reason = AiLyrics.SkipReason.None)
        => new("spotify:track:x", phase, reason, "en", 3, 40, 12.5, false);

    [Fact]
    public void Header_hidden_while_off_or_unavailable()
    {
        var working = Track(AiLyrics.TrackPhase.Working);
        Assert.False(AiLyrics.Rules.Header(AiLyrics.Status.Unknown, working, EntityKind.Track).Visible);
        Assert.False(AiLyrics.Rules.Header(Status(AiLyrics.SetupPhase.Off, enabled: false), working, EntityKind.Track).Visible);
        Assert.False(AiLyrics.Rules.Header(Status(AiLyrics.SetupPhase.Unavailable), working, EntityKind.Track).Visible);
        Assert.False(AiLyrics.Rules.Header(Status(AiLyrics.SetupPhase.Ready, enabled: false), working, EntityKind.Track).Visible);
    }

    [Theory]
    [InlineData(AiLyrics.SetupPhase.NeedsSetup)]
    [InlineData(AiLyrics.SetupPhase.Downloading)]
    [InlineData(AiLyrics.SetupPhase.Paused)]
    [InlineData(AiLyrics.SetupPhase.Preparing)]
    [InlineData(AiLyrics.SetupPhase.Error)]
    public void Header_asks_to_finish_setup_before_ready(AiLyrics.SetupPhase phase)
        => Assert.Equal(new AiLyrics.HeaderState(true, false, "lyrics.ai.header.setup", true),
            AiLyrics.Rules.Header(Status(phase), Track(AiLyrics.TrackPhase.Idle), EntityKind.Track));

    [Fact]
    public void Header_on_a_podcast_says_so()
        => Assert.Equal(new AiLyrics.HeaderState(true, false, "lyrics.ai.header.skipped.podcast", false),
            AiLyrics.Rules.Header(Status(AiLyrics.SetupPhase.Ready), Track(AiLyrics.TrackPhase.Done), EntityKind.Episode));

    [Theory]
    [InlineData(AiLyrics.TrackPhase.Idle, AiLyrics.SkipReason.None, false, "lyrics.ai.header.waiting", false)]
    [InlineData(AiLyrics.TrackPhase.Waiting, AiLyrics.SkipReason.None, false, "lyrics.ai.header.waiting", false)]
    [InlineData(AiLyrics.TrackPhase.Working, AiLyrics.SkipReason.None, false, "lyrics.ai.header.working", false)]
    [InlineData(AiLyrics.TrackPhase.Done, AiLyrics.SkipReason.None, true, "lyrics.ai.header.on", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.NoLyrics, false, "lyrics.ai.header.skipped.noLyrics", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.AlreadyWordByWord, false, "lyrics.ai.header.skipped.alreadyWordByWord", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.LanguageNotInstalled, false, "lyrics.ai.header.skipped.language", true)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.PlainTextOff, false, "lyrics.ai.header.skipped.plainOff", true)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.WordSyncOff, false, "lyrics.ai.header.skipped.wordSyncOff", true)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.BatterySaver, false, "lyrics.ai.header.skipped.batterySaver", true)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.NotSpotifyAudio, false, "lyrics.ai.header.skipped.audio", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.AudioUnavailable, false, "lyrics.ai.header.skipped.audio", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.TooLong, false, "lyrics.ai.header.skipped.tooLong", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.Failed, false, "lyrics.ai.header.skipped.failed", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.Podcast, false, "lyrics.ai.header.skipped.podcast", false)]
    [InlineData(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.NeedsSetup, false, "lyrics.ai.header.setup", true)]
    public void Header_when_ready_follows_the_track(AiLyrics.TrackPhase phase, AiLyrics.SkipReason reason, bool active, string tip, bool opens)
        => Assert.Equal(new AiLyrics.HeaderState(true, active, tip, opens, TogglesPreference: reason == AiLyrics.SkipReason.AlreadyWordByWord),
            AiLyrics.Rules.Header(Status(AiLyrics.SetupPhase.Ready), Track(phase, reason), EntityKind.Track));

    [Fact]
    public void Header_offers_ai_timing_over_people_made_word_timing_and_the_way_back()
    {
        var ready = Status(AiLyrics.SetupPhase.Ready);
        // a provider has word timing: a click asks for AI timing for this song
        var offer = AiLyrics.Rules.Header(ready, Track(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.AlreadyWordByWord), EntityKind.Track);
        Assert.True(offer.TogglesPreference);
        Assert.False(offer.OpensSettings);
        // chosen: working, then lit, and a click goes back to the provider
        var working = AiLyrics.Rules.Header(ready, Track(AiLyrics.TrackPhase.Working), EntityKind.Track, prefersAi: true);
        Assert.Equal(new AiLyrics.HeaderState(true, false, "lyrics.ai.header.workingOverride", false, true), working);
        var done = AiLyrics.Rules.Header(ready, Track(AiLyrics.TrackPhase.Done), EntityKind.Track, prefersAi: true);
        Assert.Equal(new AiLyrics.HeaderState(true, true, "lyrics.ai.header.overriding", false, true), done);
        // without the preference a done track is the plain "on" state
        Assert.False(AiLyrics.Rules.Header(ready, Track(AiLyrics.TrackPhase.Done), EntityKind.Track).TogglesPreference);
    }

    [Fact]
    public void Footer_shows_while_working_and_when_done()
    {
        Assert.True(AiLyrics.Rules.ShowsFooter(Track(AiLyrics.TrackPhase.Working)));
        Assert.True(AiLyrics.Rules.ShowsFooter(Track(AiLyrics.TrackPhase.Done)));
        Assert.False(AiLyrics.Rules.ShowsFooter(Track(AiLyrics.TrackPhase.Waiting)));
        Assert.False(AiLyrics.Rules.ShowsFooter(Track(AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.Failed)));
        Assert.Equal("lyrics.ai.footer.progress", AiLyrics.Rules.FooterKey(Track(AiLyrics.TrackPhase.Working)));
        Assert.Equal("lyrics.ai.footer.generated", AiLyrics.Rules.FooterKey(Track(AiLyrics.TrackPhase.Done)));
    }

    [Fact]
    public void Footer_shows_progress_only_while_a_live_job_runs()
    {
        Assert.True(AiLyrics.Rules.FooterShowsProgress(Track(AiLyrics.TrackPhase.Working)));
        Assert.False(AiLyrics.Rules.FooterShowsProgress(Track(AiLyrics.TrackPhase.Done)));
        var cached = Track(AiLyrics.TrackPhase.Working) with { FromCache = true };
        Assert.False(AiLyrics.Rules.FooterShowsProgress(cached));
        Assert.Equal("lyrics.ai.footer.generated", AiLyrics.Rules.FooterKey(cached));
        Assert.Equal("lyrics.ai.footer.generated", AiLyrics.Rules.FooterKey(Track(AiLyrics.TrackPhase.Done) with { FromCache = true }));
    }

    [Theory]
    [InlineData(30.0, 120.0, 0.25f)]
    [InlineData(0.0, 120.0, 0f)]
    [InlineData(-5.0, 120.0, 0f)]
    [InlineData(130.0, 120.0, 1f)]
    [InlineData(30.0, 0.0, 0f)]
    [InlineData(30.0, -1.0, 0f)]
    public void Footer_bar_fraction_is_clamped_and_zero_without_a_duration(double part, double whole, float expected)
        => Assert.Equal(expected, AiLyrics.Rules.Fraction(part, whole), 4);

    [Fact]
    public void Models_unload_after_ten_idle_minutes()
    {
        Assert.False(AiLyrics.Rules.UnloadAfterIdle(0, 599_999));
        Assert.True(AiLyrics.Rules.UnloadAfterIdle(0, 600_000));
        Assert.False(AiLyrics.Rules.UnloadAfterIdle(1_000_000, 1_500_000));
        Assert.True(AiLyrics.Rules.UnloadAfterIdle(1_000_000, 1_700_000));
    }
}
