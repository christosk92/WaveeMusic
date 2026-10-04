// ── Wavee.Tests/VerseRulesTests.cs — the Verse face's pure rules (verse-plan.md §5.6) ─────────────────────────────────
//
// Words (syllables → words, CJK provider syllables, the overrun trim), Sections (chorus detection, echo), Memory (ordinals,
// the most-sung words against a brute-force count), Emphasis (held, repetition, density, two held per line), Timing (word /
// line / static, density, landing length), Flow (rows that never split a word and never overrun, the shrink, the give-up),
// the envelopes (Land, Hold, Echo, CountIn, Coupling), Lanes (motion demand) and Geometry (per aspect class, the Portrait
// gallery shrink, narrowing never adds).
//
// All pure: `Verse.*` reads only the lyric model records and the stage allocator, so these drive the real arithmetic.
// (`ModeRules.ShowsCaption(kind)` and the catalog's `Kind.Verse` rows are pinned with Stage.cs / Visualizer.cs.)

using Wavee;
using Xunit;

using A = Wavee.Stage.Aspect;
using L = Wavee.Stage.Layout;

namespace Wavee.Tests;

static class VerseKit
{
    public static Lyrics.Syllable S(long start, long end, string text) => new(start, end, text);

    /// <summary>A word-timed line, TTML-shaped: one syllable per word, the spaces only in the line text.</summary>
    public static Lyrics.Line WordLine(long start, long msPerWord, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var syl = new Lyrics.Syllable[parts.Length];
        for (int i = 0; i < parts.Length; i++) syl[i] = new(start + i * msPerWord, start + (i + 1) * msPerWord, parts[i]);
        return new Lyrics.Line(start, text, syl, start + parts.Length * msPerWord, IsWordByWord: true);
    }

    public static Lyrics.Line Plain(long start, string text, long? end = null) => new(start, text, [], end);

    public static Lyrics.Doc Doc(Lyrics.SyncKind sync, params Lyrics.Line[] lines)
        => new("t", sync is Lyrics.SyncKind.Line or Lyrics.SyncKind.Syllable, lines, sync, "test");

    public static Lyrics.Line[] Texts(params string[] texts)
    {
        var lines = new Lyrics.Line[texts.Length];
        for (int i = 0; i < texts.Length; i++) lines[i] = Plain(i * 1000L, texts[i]);
        return lines;
    }

    public static Verse.Word W(long heldMs = 0, int ordinal = 0, long start = 0, string text = "word")
        => new(text, start, start + 500, 0, 1, heldMs, -1, ordinal, null);
}

public class VerseRulesTests
{
    // ── words ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ttml_word_spans_are_words()
    {
        var w = Verse.Words.Build(VerseKit.WordLine(1000, 400, "Hold on tight"));
        Assert.Equal(["Hold", "on", "tight"], w.Select(x => x.Text).ToArray());
        Assert.Equal(1000, w[0].StartMs);
        Assert.Equal(1800, w[2].StartMs);
        Assert.All(w, x => Assert.Equal(1, x.SylCount));
    }

    [Fact]
    public void Cjk_provider_syllables_are_each_a_word()
    {
        var line = new Lyrics.Line(0, "我爱你", [VerseKit.S(0, 300, "我"), VerseKit.S(300, 600, "爱"), VerseKit.S(600, 1400, "你")], 1400, IsWordByWord: true);
        var w = Verse.Words.Build(line);
        Assert.Equal(["我", "爱", "你"], w.Select(x => x.Text).ToArray());
        Assert.Equal(800, w[2].HeldMs);
    }

    [Fact]
    public void Enhanced_lrc_syllables_without_a_space_join_into_one_word()
    {
        // `<00:01.50>Hel<00:01.90>lo <00:02.30>world` → syllables "Hel", "lo ", "world"
        var line = new Lyrics.Line(1500, "Hello world", [VerseKit.S(1500, 1900, "Hel"), VerseKit.S(1900, 2300, "lo "), VerseKit.S(2300, 2800, "world")], 2800, IsWordByWord: true);
        var w = Verse.Words.Build(line);
        Assert.Equal(["Hello", "world"], w.Select(x => x.Text).ToArray());
        Assert.Equal(2, w[0].SylCount);
        Assert.Equal(0, w[0].FirstSyl);
        Assert.Equal(2, w[1].FirstSyl);
        Assert.Equal(1500, w[0].StartMs);
        Assert.Equal(2300, w[0].EndMs);
        // the within-word wipe is the lyrics split over the word's own syllables: half of "Hel" sung = 1.5 of 6 chars
        Assert.Equal(0f, Verse.Words.Split(in w[0], 1400));
        Assert.InRange(Verse.Words.Split(in w[0], 1700), 0.2f, 0.3f);
        Assert.Equal(1f, Verse.Words.Split(in w[0], 2300));
    }

    [Fact]
    public void A_pure_space_syllable_is_a_boundary_never_a_word()
    {
        var line = new Lyrics.Line(0, "Hello world", [VerseKit.S(0, 400, "Hello"), VerseKit.S(400, 450, " "), VerseKit.S(450, 900, "world")], 900, IsWordByWord: true);
        var w = Verse.Words.Build(line);
        Assert.Equal(["Hello", "world"], w.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void A_line_timed_line_splits_on_whitespace_and_shares_the_line_start()
    {
        var w = Verse.Words.Build(VerseKit.Plain(3000, "  Don't let   go ", 5000));
        Assert.Equal(["Don't", "let", "go"], w.Select(x => x.Text).ToArray());
        Assert.All(w, x => { Assert.Equal(3000, x.StartMs); Assert.Null(x.Timed); Assert.Equal(-1, x.FirstSyl); });
    }

    [Fact]
    public void An_overrunning_syllable_is_not_a_held_note()
    {
        // the first syllable claims 1500 ms but the next starts at 800: its held span is trimmed to 800
        var syl = new[] { VerseKit.S(0, 1500, "la"), VerseKit.S(800, 1000, "da") };
        Assert.Equal(800, Verse.Words.HeldSpan(syl, 0));
        Assert.Equal(200, Verse.Words.HeldSpan(syl, 1));
        // within the 50 ms tolerance it stands
        var tight = new[] { VerseKit.S(0, 840, "la"), VerseKit.S(800, 1000, "da") };
        Assert.Equal(840, Verse.Words.HeldSpan(tight, 0));
    }

    [Fact]
    public void A_forty_word_line_falls_back_to_line_mode()
    {
        string text = string.Join(' ', Enumerable.Range(0, 40).Select(i => "w" + i));
        var line = VerseKit.WordLine(0, 100, text);
        var words = Verse.Words.Build(line);
        Assert.Equal(40, words.Length);
        Assert.Equal(Verse.TimingMode.Line, Verse.Timing.LineMode(Verse.TimingMode.Word, line, words));
        var short_ = VerseKit.WordLine(0, 300, "a short line");
        Assert.Equal(Verse.TimingMode.Word, Verse.Timing.LineMode(Verse.TimingMode.Word, short_, Verse.Words.Build(short_)));
    }

    [Fact]
    public void Script_and_direction_detection()
    {
        Assert.True(Verse.Words.HasUnbrokenScript("ฉันรักเธอ"));
        Assert.False(Verse.Words.HasUnbrokenScript("I love you"));
        Assert.True(Verse.Words.IsRtl("שלום עולם"));
        Assert.True(Verse.Words.IsRtl("  مرحبا"));
        Assert.False(Verse.Words.IsRtl("hello שלום"));
    }

    // ── sections ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly string[] s_ababcb =
    [
        "Walking down the road", "Sun is in my eyes",          // A1  0-1
        "Hold on, hold on!", "Don't let go",                   // B   2-3
        "City lights are calling", "Nothing left to hide",     // A2  4-5
        "hold on hold on", "Dont let go (yeah)",               // B   6-7  (case, punctuation, an aside)
        "And if the sky falls down", "We will rise",           // C   8-9
        "Hold on hold on", "Don't let go, oh yeah",            // B   10-11 (a vocable tail)
    ];

    [Fact]
    public void Ababcb_form_has_three_choruses_and_a_bridge()
    {
        var m = Verse.Sections.Detect(VerseKit.Texts(s_ababcb));
        int[] chorus = [2, 3, 6, 7, 10, 11];
        foreach (int i in chorus) Assert.Equal(Verse.Part.Chorus, m.Parts[i]);
        Assert.Equal([1, 1, 2, 2, 3, 3], chorus.Select(i => m.Instance[i]).ToArray());
        Assert.Equal(Verse.Part.Bridge, m.Parts[8]);
        Assert.Equal(Verse.Part.Bridge, m.Parts[9]);
        foreach (int i in new[] { 0, 1, 4, 5 }) Assert.Equal(Verse.Part.Verse, m.Parts[i]);
        Assert.True(m.IsChorusEntry(2));
        Assert.True(m.IsChorusEntry(6));
        Assert.False(m.IsChorusEntry(7));
        Assert.False(m.IsChorusEntry(0));
    }

    [Fact]
    public void Echo_points_at_the_first_occurrence()
    {
        var m = Verse.Sections.Detect(VerseKit.Texts(s_ababcb));
        Assert.Equal(-1, m.EchoOf[2]);
        Assert.Equal(2, m.EchoOf[6]);
        Assert.Equal(3, m.EchoOf[7]);
        Assert.Equal(2, m.EchoOf[10]);
        Assert.Equal(3, m.EchoOf[11]);
        Assert.Equal(-1, m.EchoOf[0]);
    }

    [Fact]
    public void No_repeats_is_all_verse()
    {
        var m = Verse.Sections.Detect(VerseKit.Texts("one line", "two lines", "three lines", "four lines"));
        Assert.All(m.Parts, p => Assert.Equal(Verse.Part.Verse, p));
        Assert.All(m.EchoOf, e => Assert.Equal(-1, e));
    }

    [Fact]
    public void A_repeated_single_line_needs_four_occurrences()
    {
        var thrice = Verse.Sections.Detect(VerseKit.Texts("na na", "verse a", "na na", "verse b", "na na", "verse c"));
        Assert.DoesNotContain(Verse.Part.Chorus, thrice.Parts);
        var four = Verse.Sections.Detect(VerseKit.Texts("hey now", "verse a", "hey now", "verse b", "hey now", "verse c", "hey now"));
        Assert.Equal(Verse.Part.Chorus, four.Parts[0]);
        Assert.Equal(Verse.Part.Chorus, four.Parts[6]);
        Assert.Equal(4, four.Instance[6]);
        Assert.Equal(0, four.EchoOf[2]);
    }

    [Fact]
    public void Empty_lines_never_match()
    {
        var m = Verse.Sections.Detect(VerseKit.Texts("", "", "", "", "", ""));
        Assert.All(m.Parts, p => Assert.Equal(Verse.Part.Verse, p));
        Assert.All(m.Keys, k => Assert.True(k < 0));
    }

    [Fact]
    public void Normalise_drops_case_punctuation_asides_and_vocable_tails()
    {
        Assert.Equal("dont let go", Verse.Sections.Normalise("Don't let go (yeah), oh yeah!"));
        Assert.Equal("hold on hold on", Verse.Sections.Normalise("  Hold on — hold   on  "));
        Assert.Equal("ooh ooh", Verse.Sections.Normalise("Ooh, ooh"));   // all vocables: kept, so such lines still match each other
    }

    [Fact]
    public void Echo_ghost_is_the_nearest_identical_line_in_the_stack()
    {
        var m = Verse.Sections.Detect(VerseKit.Texts("hold on", "dont let the night run out", "Hold on!", "home now"));
        Assert.Equal(2, Verse.Sections.EchoGhost(m, 2, 4));
        Assert.Equal(0, Verse.Sections.EchoGhost(m, 2, 1));   // out of the visible depth
        Assert.Equal(0, Verse.Sections.EchoGhost(m, 3, 4));
        Assert.Equal(0, Verse.Sections.EchoGhost(m, 99, 4));
    }

    // ── memory ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Verse.Song Song(params string[] lines)
    {
        var l = new Lyrics.Line[lines.Length];
        for (int i = 0; i < lines.Length; i++) l[i] = VerseKit.WordLine(i * 4000L, 400, lines[i]);
        return Verse.Song.Build(VerseKit.Doc(Lyrics.SyncKind.Syllable, l));
    }

    [Fact]
    public void Ordinals_count_up_and_skip_stop_words()
    {
        var song = Song("the night is young", "love the night", "love love");
        var w0 = song.Words[0];
        Assert.Equal(-1, w0[0].Token);                  // "the"
        Assert.Equal(0, w0[0].Ordinal);
        Assert.Equal(-1, w0[2].Token);                  // "is": too short
        Assert.Equal(1, w0[1].Ordinal);                 // night #1
        Assert.Equal(2, song.Words[1][2].Ordinal);      // night #2
        Assert.Equal(1, song.Words[1][0].Ordinal);      // love #1
        Assert.Equal(3, song.Words[2][1].Ordinal);      // love #3
        Assert.Equal(song.Words[1][0].Token, song.Words[2][1].Token);
        Assert.Contains("night", song.Tokens);
        Assert.DoesNotContain("the", song.Tokens);
    }

    [Fact]
    public void Normalise_keeps_inner_apostrophes_and_drops_short_words()
    {
        Assert.Equal("nothin", Verse.Memory.Normalise("Nothin'"));
        Assert.Equal("rock'n'roll", Verse.Memory.Normalise("Rock’n’roll!"));
        Assert.Null(Verse.Memory.Normalise("oh"));
        Assert.Null(Verse.Memory.Normalise("Yeah"));
    }

    [Fact]
    public void Top_words_at_matches_a_brute_force_count()
    {
        var song = Song("fire fire burning", "burning down the house", "fire in the house tonight", "house house fire", "tonight tonight");
        var tokens = new int[4];
        var counts = new int[4];
        foreach (long at in new long[] { -1, 0, 1200, 4000, 9000, 13000, 30000 })
        {
            int n = Verse.Memory.TopWordsAt(song, at, tokens, counts);

            var brute = new Dictionary<int, int>();
            foreach (var line in song.Words)
                foreach (var w in line)
                    if (w.StartMs <= at && w.Token >= 0) brute[w.Token] = brute.GetValueOrDefault(w.Token) + 1;
            var expected = brute.Values.OrderByDescending(c => c).Take(4).ToArray();

            Assert.Equal(expected.Length, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(expected[i], counts[i]);
                Assert.Equal(brute[tokens[i]], counts[i]);
            }
        }
    }

    // ── emphasis ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_held_word_is_set_larger_and_heavier()
    {
        var held = Verse.Emphasis.For(VerseKit.W(heldMs: 700), dense: false, heldAllowed: true, Verse.Emphasis.HeldScale);
        Assert.True(held.Held);
        Assert.Equal(1.3f, held.Scale, 3);
        Assert.Equal((ushort)700, held.Weight);
        var shortNote = Verse.Emphasis.For(VerseKit.W(heldMs: 699), dense: false, heldAllowed: true, Verse.Emphasis.HeldScale);
        Assert.False(shortNote.Held);
        Assert.Equal(1f, shortNote.Scale, 3);
        Assert.Equal((ushort)600, shortNote.Weight);
    }

    [Fact]
    public void Repetition_grows_seven_percent_a_repeat_capped_at_forty()
    {
        Assert.Equal(0f, Verse.Emphasis.Repetition(0));
        Assert.Equal(0f, Verse.Emphasis.Repetition(1));
        Assert.Equal(0.07f, Verse.Emphasis.Repetition(2), 4);
        Assert.Equal(0.35f, Verse.Emphasis.Repetition(6), 4);
        Assert.Equal(0.40f, Verse.Emphasis.Repetition(7), 4);
        Assert.Equal(0.40f, Verse.Emphasis.Repetition(50), 4);
        var hook = Verse.Emphasis.For(VerseKit.W(heldMs: 900, ordinal: 30), dense: false, heldAllowed: true, Verse.Emphasis.HeldScale);
        Assert.Equal(1.7f, hook.Scale, 3);
    }

    [Fact]
    public void A_dense_line_is_neutral()
    {
        var r = Verse.Emphasis.For(VerseKit.W(heldMs: 2000, ordinal: 9), dense: true, heldAllowed: true, Verse.Emphasis.HeldScale);
        Assert.Equal(new Verse.Role(1f, 600, false), r);
    }

    [Fact]
    public void Only_the_two_longest_held_words_of_a_line_read_as_held()
    {
        Verse.Word[] words = [VerseKit.W(heldMs: 900), VerseKit.W(heldMs: 1200), VerseKit.W(heldMs: 300), VerseKit.W(heldMs: 800)];
        var roles = new Verse.Role[words.Length];
        Verse.Emphasis.Line(words, dense: false, Verse.Emphasis.HeldScale, roles);
        Assert.Equal([true, true, false, false], roles.Select(r => r.Held).ToArray());
        Verse.Emphasis.Line(words, dense: true, Verse.Emphasis.HeldScale, roles);
        Assert.All(roles, r => Assert.False(r.Held));
    }

    // ── timing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timing_mode_follows_the_document()
    {
        var word = VerseKit.Doc(Lyrics.SyncKind.Syllable, VerseKit.WordLine(0, 300, "a b c"));
        Assert.Equal(Verse.TimingMode.Word, Verse.Timing.Mode(word));
        var line = VerseKit.Doc(Lyrics.SyncKind.Line, VerseKit.Plain(0, "a b c"), VerseKit.Plain(2000, "d e"));
        Assert.Equal(Verse.TimingMode.Line, Verse.Timing.Mode(line));
        var unsynced = new Lyrics.Doc("t", false, [VerseKit.Plain(0, "a b c")], Lyrics.SyncKind.Unsynced);
        Assert.Equal(Verse.TimingMode.Static, Verse.Timing.Mode(unsynced));
        Assert.Equal(Verse.TimingMode.Static, Verse.Timing.Mode(Lyrics.Doc.Empty("t")));
    }

    [Fact]
    public void A_transcript_with_gap_fillers_is_word_timed_and_its_fillers_land_whole()
    {
        var filler = VerseKit.Plain(3000, "…", 6000);
        var doc = VerseKit.Doc(Lyrics.SyncKind.Syllable, VerseKit.WordLine(0, 250, "so we talked about it"), filler, VerseKit.WordLine(6000, 250, "and then"));
        Assert.Equal(Verse.TimingMode.Word, Verse.Timing.Mode(doc));
        Assert.Equal(Verse.TimingMode.Line, Verse.Timing.LineMode(Verse.TimingMode.Word, filler, Verse.Words.Build(filler)));
        Assert.Equal(Verse.TimingMode.Static, Verse.Timing.LineMode(Verse.TimingMode.Static, filler, Verse.Words.Build(filler)));
    }

    [Fact]
    public void Density_and_landing_length()
    {
        var rap = Verse.Words.Build(VerseKit.WordLine(0, 100, "one two three four five six seven eight"));
        Assert.True(Verse.Timing.IsDense(rap));
        Assert.Equal(Verse.Land.DenseMs, Verse.Timing.LandMsFor(rap, dense: true));
        var pop = Verse.Words.Build(VerseKit.WordLine(0, 400, "one two three four"));
        Assert.False(Verse.Timing.IsDense(pop));
        Assert.Equal(Verse.Land.Ms, Verse.Timing.LandMsFor(pop, dense: false));
        var ballad = Verse.Words.Build(VerseKit.WordLine(0, 1600, "one two three"));
        Assert.Equal(Verse.Land.SlowMs, Verse.Timing.LandMsFor(ballad, dense: false));
        Assert.False(Verse.Timing.IsDense(Verse.Words.Build(VerseKit.WordLine(0, 50, "two words"))));   // too few to judge
    }

    // ── flow ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static float RowWidth(ReadOnlySpan<float> widths, ReadOnlySpan<int> rowOf, int row, float gap, float scale)
    {
        float x = 0f;
        bool empty = true;
        for (int i = 0; i < widths.Length; i++)
        {
            if (rowOf[i] != row) continue;
            x += (empty ? 0f : gap * scale) + widths[i] * scale;
            empty = false;
        }
        return x;
    }

    [Fact]
    public void Flow_never_overruns_and_never_splits_a_word()
    {
        float[] widths = [100f, 200f, 150f, 300f];
        var rowOf = new int[widths.Length];
        var f = Verse.Flow.Layout(widths, 10f, 400f, 3, rowOf);
        Assert.True(f.Fits);
        Assert.Equal(1f, f.Scale);
        Assert.Equal(3, f.Rows);
        Assert.Equal([0, 0, 1, 2], rowOf);
        for (int r = 0; r < f.Rows; r++) Assert.True(RowWidth(widths, rowOf, r, 10f, f.Scale) <= 400f);
        for (int i = 1; i < rowOf.Length; i++) Assert.True(rowOf[i] >= rowOf[i - 1]);   // words keep their order
    }

    [Fact]
    public void Flow_shrinks_to_fit_the_row_budget()
    {
        float[] widths = [100f, 200f, 150f, 300f];
        var rowOf = new int[widths.Length];
        var f = Verse.Flow.Layout(widths, 10f, 400f, 2, rowOf);
        Assert.True(f.Fits);
        Assert.Equal(2, f.Rows);
        Assert.Equal(0.85f, f.Scale, 3);
        Assert.Equal([0, 0, 0, 1], rowOf);
        for (int r = 0; r < f.Rows; r++) Assert.True(RowWidth(widths, rowOf, r, 10f, f.Scale) <= 400f + 1e-3f);
        Assert.True(f.WidestRow <= 400f + 1e-3f);
    }

    [Fact]
    public void Flow_gives_up_when_a_word_is_wider_than_the_column_at_the_floor()
    {
        var rowOf = new int[1];
        var f = Verse.Flow.Layout([1000f], 10f, 400f, 3, rowOf);
        Assert.False(f.Fits);
        Assert.Equal(Verse.Flow.MinScale, f.Scale);
    }

    [Fact]
    public void Flow_is_deterministic()
    {
        float[] widths = [120f, 80f, 260f, 40f, 310f, 90f, 200f];
        var a = new int[widths.Length];
        var b = new int[widths.Length];
        var fa = Verse.Flow.Layout(widths, 12f, 500f, 3, a);
        var fb = Verse.Flow.Layout(widths, 12f, 500f, 3, b);
        Assert.Equal(fa, fb);
        Assert.Equal(a, b);
        Assert.Equal(new Verse.Flowed(0, 1f, true, 0f), Verse.Flow.Layout([], 12f, 500f, 3, []));
    }

    // ── envelopes ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Landing_runs_from_minus_land_ms_to_the_word_start()
    {
        const long start = 10_000;
        Assert.Equal(0f, Verse.Land.Progress(start - 350, start, 350f));
        Assert.Equal(0f, Verse.Land.Progress(start - 5000, start, 350f));
        Assert.Equal(1f, Verse.Land.Progress(start, start, 350f));
        Assert.Equal(1f, Verse.Land.Progress(start + 5000, start, 350f));
        float prev = -1f;
        for (long t = start - 400; t <= start + 50; t += 10)
        {
            float p = Verse.Land.Progress(t, start, 350f);
            Assert.True(p >= prev);
            Assert.True(Verse.Land.Alpha(p) >= Verse.Land.WaitAlpha && Verse.Land.Alpha(p) <= 1f);
            prev = p;
        }
        Assert.Equal(Verse.Land.WaitAlpha, Verse.Land.Alpha(0f));
        Assert.Equal(1f, Verse.Land.Alpha(1f));
    }

    [Fact]
    public void Reduced_motion_zeroes_the_drop_and_the_blur_but_keeps_the_ink_step()
    {
        Assert.True(Verse.Land.Drop(0.3f, reduced: false) < 0f);
        Assert.Equal(0f, Verse.Land.Drop(0.3f, reduced: true));
        Assert.Equal(0f, Verse.Land.Blur(0.3f, reduced: true, enabled: true));
        Assert.Equal(0f, Verse.Land.Blur(0.3f, reduced: false, enabled: false));   // a weak GPU
        Assert.True(Verse.Land.Blur(0.3f, reduced: false, enabled: true) > 0f);
        Assert.True(Verse.Land.Alpha(0.5f) > Verse.Land.WaitAlpha);                 // the alpha does not read Reduced at all
    }

    [Fact]
    public void A_waiting_or_landed_word_is_never_a_blur_layer()
    {
        Assert.Equal(0f, Verse.Land.Blur(0f, reduced: false, enabled: true));
        Assert.Equal(0f, Verse.Land.Blur(1f, reduced: false, enabled: true));
        Assert.Equal(0f, Verse.Land.Drop(0f, reduced: false));
        Assert.Equal(0f, Verse.Land.Drop(1f, reduced: false));
    }

    [Fact]
    public void At_most_two_words_land_at_once()
    {
        Assert.Equal(0.4f, Verse.Land.Capped(0.4f, 0));
        Assert.Equal(0.4f, Verse.Land.Capped(0.4f, 1));
        Assert.Equal(1f, Verse.Land.Capped(0.4f, 2));
        Assert.Equal(0f, Verse.Land.Capped(0f, 5));   // a waiting word stays waiting
    }

    [Fact]
    public void Hold_swells_and_blooms_within_bounds()
    {
        Assert.Equal(1f, Verse.Hold.Swell(1f, 1f, reduced: true, calm: false));
        Assert.Equal(1f, Verse.Hold.Swell(0f, 1f, reduced: false, calm: false));
        float s = Verse.Hold.Swell(1f, 1f, reduced: false, calm: false);
        Assert.InRange(s, 1.05f, 1.08f + 1e-4f);
        Assert.True(Verse.Hold.Swell(1f, 1f, reduced: false, calm: true) < s);
        Assert.Equal(0f, Verse.Hold.Bloom(0f, 1f, playing: true));
        Assert.InRange(Verse.Hold.Bloom(1f, 1f, playing: true), 0f, 1f);
        Assert.Equal(Verse.Hold.PausedBloom, Verse.Hold.Bloom(0.8f, 0f, playing: false));
        Assert.Equal(0f, Verse.Hold.Bloom(0f, 0f, playing: false));
    }

    [Fact]
    public void A_held_word_glows_from_the_lyrics_envelope()
    {
        var line = new Lyrics.Line(0, "Night", [VerseKit.S(0, 2000, "Night")], 2000, IsWordByWord: true);
        var w = Verse.Words.Build(line)[0];
        Assert.Equal(2000, w.HeldMs);
        Assert.Equal(0f, Verse.Hold.Glow(in w, -10));
        Assert.True(Verse.Hold.Glow(in w, 1000) > 0.9f);
        Assert.Equal(Lyrics.Wipe.HeldSyllableGlow(line, 1000), Verse.Hold.Glow(in w, 1000));
        Assert.Equal(0f, Verse.Hold.Glow(VerseKit.W(heldMs: 2000), 100));   // a line-timed word has no envelope
    }

    [Fact]
    public void Echo_lights_a_beat_before_and_fades_over_two()
    {
        const long start = 20_000;
        Assert.Equal(0f, Verse.Echo.Alpha(start - 600, start, 500f, reduced: false));
        Assert.Equal(0f, Verse.Echo.Alpha(start - 500, start, 500f, reduced: false), 3);
        Assert.Equal(0.5f, Verse.Echo.Alpha(start - 250, start, 500f, reduced: false), 3);
        Assert.Equal(1f, Verse.Echo.Alpha(start, start, 500f, reduced: false), 3);
        Assert.Equal(0.5f, Verse.Echo.Alpha(start + 500, start, 500f, reduced: false), 3);
        Assert.Equal(0f, Verse.Echo.Alpha(start + 1001, start, 500f, reduced: false));
        Assert.Equal(1f, Verse.Echo.Alpha(start + 700, start, 500f, reduced: true));   // reduced: a colour step over the window
        Assert.Equal(0f, Verse.Echo.Alpha(start + 1200, start, 500f, reduced: true));
        Assert.Equal(start + 1000, Verse.Echo.EndMs(start, 500f));
        Assert.Equal(start + 1000, Verse.Echo.EndMs(start, 0f));                       // no grid: the 500 ms fallback
    }

    [Fact]
    public void The_count_in_lights_one_dot_every_two_beats()
    {
        const long next = 10_000;
        Assert.Equal(0, Verse.CountIn.Lit(next - 4000, next, 500f));
        Assert.Equal(1, Verse.CountIn.Lit(next - 3000, next, 500f));
        Assert.Equal(1, Verse.CountIn.Lit(next - 2100, next, 500f));
        Assert.Equal(2, Verse.CountIn.Lit(next - 2000, next, 500f));
        Assert.Equal(3, Verse.CountIn.Lit(next - 1000, next, 500f));
        Assert.Equal(3, Verse.CountIn.Lit(next, next, 500f));
        Assert.Equal(next - 3000, Verse.CountIn.NextEdgeMs(0, next, 500f));
        Assert.Equal(next - 2000, Verse.CountIn.NextEdgeMs(next - 3000, next, 500f));
        Assert.Equal(next - 1000, Verse.CountIn.NextEdgeMs(next - 1500, next, 500f));
        Assert.Equal(Verse.Lanes.None, Verse.CountIn.NextEdgeMs(next - 1000, next, 500f));
        Assert.Equal(1, Verse.CountIn.Lit(next - 3000, next, 0f));                     // no grid: the 500 ms beat
    }

    [Fact]
    public void Coupling_is_bounded_calmed_and_zero_under_reduced_motion()
    {
        Assert.Equal(Verse.Coupling.KickDip, Verse.Coupling.Kick(5f, calm: false, reduced: false));
        Assert.True(Verse.Coupling.KickDip <= 2f);                                     // the live line moves ≤ 2 DIP
        Assert.Equal(Verse.Coupling.KickDip * 0.5f, Verse.Coupling.Kick(1f, calm: true, reduced: false));
        Assert.Equal(0f, Verse.Coupling.Kick(1f, calm: false, reduced: true));
        Assert.Equal(6f, Verse.Coupling.BassSink(1f, calm: false, reduced: false));
        Assert.Equal(0f, Verse.Coupling.BassSink(1f, calm: false, reduced: true));
        Assert.Equal(0f, Verse.Coupling.BassSink(-1f, calm: false, reduced: false));
    }

    // ── lanes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_moving_means_no_ticks_and_a_wake_before_the_next_event()
    {
        var quiet = new Verse.LaneState(Playing: true, Voice: false, Landing: false, Held: false, Echo: false, NowMs: 0, NextEventMs: 5000);
        var d = Verse.Lanes.Decide(in quiet);
        Assert.False(d.NeedsTicks);
        Assert.Equal(5000 - Lyrics.MotionDemand.ArmLeadMs, d.WakeAtMs);
        var soon = quiet with { NowMs = 4990 };
        Assert.True(Verse.Lanes.Decide(in soon).NeedsTicks);                          // inside the arm lead: stay live
        var none = quiet with { NextEventMs = Verse.Lanes.None };
        Assert.Equal(new Lyrics.MotionDecision(false, Verse.Lanes.None), Verse.Lanes.Decide(in none));
    }

    [Fact]
    public void Paused_never_ticks_and_a_moving_lane_always_does()
    {
        var paused = new Verse.LaneState(Playing: false, Voice: true, Landing: true, Held: true, Echo: true, NowMs: 0, NextEventMs: 100);
        Assert.Equal(new Lyrics.MotionDecision(false, Verse.Lanes.None), Verse.Lanes.Decide(in paused));
        foreach (var l in new[]
                 {
                     paused with { Playing = true, Landing = false, Held = false, Echo = false },
                     paused with { Playing = true, Voice = false, Held = false, Echo = false },
                     paused with { Playing = true, Voice = false, Landing = false, Echo = false },
                     paused with { Playing = true, Voice = false, Landing = false, Held = false },
                 })
            Assert.True(Verse.Lanes.Decide(in l).NeedsTicks);
    }

    [Fact]
    public void The_next_event_is_the_next_hand_off_or_a_count_in_dot()
    {
        var lines = new[] { VerseKit.Plain(1000, "a"), VerseKit.Plain(5000, "b") };
        Assert.Equal(1000 - Lyrics.LeadMs, Verse.Lanes.NextEventMs(lines, 0, 0, Verse.Lanes.None));
        Assert.Equal(1000, Verse.Lanes.NextEventMs(lines, 0, 900, Verse.Lanes.None));
        Assert.Equal(5000 - Lyrics.LeadMs, Verse.Lanes.NextEventMs(lines, 0, 1500, Verse.Lanes.None));
        Assert.Equal(700, Verse.Lanes.NextEventMs(lines, 0, 0, 700));
        Assert.Equal(Verse.Lanes.None, Verse.Lanes.NextEventMs(lines, 1, 6000, Verse.Lanes.None));
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────────────────────────

    static Verse.Region Region(float w, float h, bool galleryOpen = true, bool chrome = true)
    {
        var layout = L.Seed(w, h);
        return Verse.Geometry.For(in layout, w - layout.FaceRight(galleryOpen), h, galleryOpen, chrome);
    }

    [Fact]
    public void Desktop_sets_a_larger_line_in_a_centred_column_at_56_percent()
    {
        var layout = L.Seed(1920f, 1080f);
        float faceW = 1920f - layout.FaceRight(true);
        var r = Verse.Geometry.For(in layout, faceW, 1080f, galleryOpen: true, chrome: true);
        Assert.Equal(A.Desktop, r.Aspect);
        Assert.Equal(MathF.Round(layout.CaptionFont.Size * 1.15f * 0.5f) * 2f, r.Base);
        Assert.True(r.Base > layout.CaptionFont.Size);
        Assert.True(r.ColumnW <= 0.72f * faceW + 1f);
        Assert.True(r.ColumnW <= 1000f * layout.LyricsTypeScale);
        Assert.Equal(MathF.Round((faceW - r.ColumnW) * 0.5f), r.ColumnX);
        Assert.Equal(MathF.Round(0.56f * 1080f), r.AnchorY);
        Assert.Equal(4, r.MaxGhosts);
        Assert.True(r.ReadAhead && r.Translation && r.Cloud && r.LandBlur);
        Assert.Equal(Verse.Emphasis.HeldScale, r.HeldScale);
        Assert.Equal(layout.TransportTop - Verse.Geometry.BottomGap, r.Bottom);
        Assert.Equal(L.NowPlayingCardY + L.NowPlayingCardH + L.Pad, r.GhostTop);       // below the now-playing card
    }

    [Fact]
    public void A_tall_ultrawide_keeps_five_ghosts_and_a_64_dip_line()
    {
        var r = Region(3440f, 1440f);
        Assert.Equal(A.Ultrawide, r.Aspect);
        Assert.Equal(5, r.MaxGhosts);
        Assert.Equal(64f, r.Base);
        Assert.Equal(4, Region(2560f, 1080f).MaxGhosts);
    }

    [Fact]
    public void Portrait_with_the_gallery_open_keeps_the_top_one_ghost_and_no_read_ahead()
    {
        var closed = Region(1080f, 1920f, galleryOpen: false);
        var open = Region(1080f, 1920f, galleryOpen: true);
        var layout = L.Seed(1080f, 1920f);
        Assert.Equal(A.Portrait, open.Aspect);
        Assert.Equal(Verse.Emphasis.HeldScalePortrait, open.HeldScale);
        Assert.Equal(layout.CaptionFont.Size, open.Base);
        Assert.Equal(3, closed.MaxGhosts);
        Assert.True(closed.ReadAhead);
        Assert.Equal(1, open.MaxGhosts);
        Assert.False(open.ReadAhead);
        Assert.False(open.Translation);
        Assert.True(open.AnchorY < closed.AnchorY);
        Assert.True(open.Bottom <= 1920f - layout.GalleryH);                            // above the gallery sheet
    }

    [Fact]
    public void Compact_is_one_ghost_and_the_live_line_beside_the_art()
    {
        var layout = L.Seed(1100f, 440f);
        var r = Verse.Geometry.For(in layout, 1100f, 440f, galleryOpen: false, chrome: true);
        Assert.Equal(A.Compact, r.Aspect);
        Assert.Equal(1, r.MaxGhosts);
        Assert.Equal(layout.TransportLeft, r.ColumnX);
        Assert.Equal(layout.TransportW, r.ColumnW);
        Assert.False(r.ReadAhead || r.Translation || r.Cloud || r.LandBlur);
        Assert.True(r.AnchorY < layout.TransportTop);
    }

    [Fact]
    public void Narrowing_never_adds()
    {
        foreach (bool gallery in new[] { true, false })
        {
            L? prev = null;
            int last = int.MaxValue;
            for (float w = 3400f; w >= 480f; w -= 20f)
            {
                var layout = L.Resolve(w, 1080f, prev);
                prev = layout;
                var r = Verse.Geometry.For(in layout, w - layout.FaceRight(gallery), 1080f, gallery, chrome: true);
                Assert.True(r.Richness <= last, $"w={w} gallery={gallery}: {r.Richness} > {last}");
                last = r.Richness;
            }
        }
    }

    [Fact]
    public void Ghosts_fit_between_the_card_and_the_live_line()
    {
        var shown = Region(1920f, 1080f, chrome: true);
        var hidden = Region(1920f, 1080f, chrome: false);
        float liveTop = shown.AnchorY - 40f;
        int n = Verse.Geometry.GhostsThatFit(in shown, liveTop);
        Assert.InRange(n, 0, shown.MaxGhosts);
        float y = liveTop - Verse.Geometry.BlockAir;
        for (int d = 1; d <= n; d++) y -= Verse.Geometry.GhostPitch(in shown, d);
        Assert.True(y >= shown.GhostTop);
        Assert.True(Verse.Geometry.GhostsThatFit(in hidden, liveTop) >= n);             // idle chrome gives the stack room
        Assert.Equal(0, Verse.Geometry.GhostsThatFit(in shown, shown.GhostTop));
    }

    [Fact]
    public void Ghost_depth_recedes_by_size_alpha_and_weight()
    {
        for (int d = 1; d < 5; d++)
        {
            Assert.True(Verse.Geometry.GhostScale(d + 1) < Verse.Geometry.GhostScale(d));
            Assert.True(Verse.Geometry.GhostAlpha(d + 1, dark: true) < Verse.Geometry.GhostAlpha(d, dark: true));
            Assert.True(Verse.Geometry.GhostAlpha(d + 1, dark: false) < Verse.Geometry.GhostAlpha(d, dark: false));
            Assert.True(Verse.Geometry.GhostWeight(d + 1) <= Verse.Geometry.GhostWeight(d));
        }
        Assert.True(Verse.Geometry.GhostAlpha(1, dark: true) <= 0.4f);
    }
}
