// ── Wavee.Tests/LyricsParserTests.cs — the document pipeline ──────────────────────────────────────────────────────
//
// Ported suites: `Lyrics/LyricsCoreTests` (the parse + timing-gate half), `Lyrics/LyricsWordFormatTests`,
// `Lyrics/LyricsCleanTests`, `Lyrics/LyricsQueryTests`, `Lyrics/MusixmatchRichsyncFixtureTests`.
//
// FIXTURES (copied verbatim into `Fixtures/lyrics/`, all real bytes captured live through the inspector's evidence
// bundle for ONE track, ISRC GBAHK9700109):
//   spotify-colorlyrics-caribbean-queen.json  the reference (line-synced, correct)
//   musixmatch-subtitle-caribbean-queen.lrc   the same provider's LRC in the same response (line-synced, correct)
//   musixmatch-richsync-caribbean-queen.json  …and its richsync body (word-synced, physically impossible)
//   kugou-krc-caribbean-queen.krc             genuine word timing, from a metadata-matched provider
//   musixmatch-decoy-sorry-seems.lrc          the anti-scraping decoy: uniform steps, running past the track
//
// The finding those five encode: the richsync's line STARTS are perfect and its line ENDS are impossible, so the
// repair keeps the starts and strips the word timing — and the reranker then prefers Kugou's REAL karaoke over
// Musixmatch's correct paragraph.

using Wavee;
using Xunit;

namespace Wavee.Tests;

static class LyricsFixture
{
    public static string Read(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lyrics", name));

    public const string TrackId = "4JEylZNW8SbO4zUyfVrpb7";
    public const string Title = "Caribbean Queen (No More Love On the Run)";
    public const string Artist = "Billy Ocean";

    public static Lyrics.Doc Richsync() => Lyrics.WordFormats.ParseRichsync(
        Read("musixmatch-richsync-caribbean-queen.json"), TrackId);

    public static Lyrics.Doc Subtitle() => Lyrics.Text.ParseLrc(
        Read("musixmatch-subtitle-caribbean-queen.lrc"), TrackId, "musixmatch");

    public static Lyrics.Doc Reference() => Lyrics.Sources.SpotifyNative.Parse(
        Read("spotify-colorlyrics-caribbean-queen.json"), TrackId)!;

    public static Lyrics.Doc Kugou() => Lyrics.WordFormats.ParseKrc(
        Read("kugou-krc-caribbean-queen.krc"), TrackId);

    public static Lyrics.Doc Decoy() => Lyrics.Text.ParseLrc(
        Read("musixmatch-decoy-sorry-seems.lrc"), TrackId, "musixmatch");
}

public class LyricsLrcParserTests
{
    [Fact]
    public void Metadata_tags_never_become_lines_and_the_times_parse()
    {
        var doc = Lyrics.Text.ParseLrc("[ti:Song]\n[ar:Someone]\n[00:01.50]one\n[01:02.25]two\n", "t");
        Assert.Equal(Lyrics.SyncKind.Line, doc.Sync);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(1500, doc.Lines[0].StartMs);
        Assert.Equal("one", doc.Lines[0].Text);
        Assert.Equal(62_250, doc.Lines[1].StartMs);
    }

    [Fact]
    public void One_text_under_several_stamps_becomes_several_lines()
    {
        var doc = Lyrics.Text.ParseLrc("[00:01.00][00:05.00]chorus\n", "t");
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("chorus", doc.Lines[0].Text);
        Assert.Equal("chorus", doc.Lines[1].Text);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(5000, doc.Lines[1].StartMs);
    }

    [Fact]
    public void Enhanced_word_stamps_produce_syllables_and_a_syllable_document()
    {
        var doc = Lyrics.Text.ParseLrc("[00:01.00]<00:01.00>Hel<00:01.50>lo\n", "t");
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.True(doc.Lines[0].IsWordByWord);
        Assert.Equal(2, doc.Lines[0].Syllables.Count);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);   // word stamps are ABSOLUTE in enhanced LRC
    }

    [Fact]
    public void The_offset_tag_shifts_every_timestamp_earlier()
    {
        var doc = Lyrics.Text.ParseLrc("[offset:500]\n[00:02.00]one\n", "t");
        Assert.Equal(1500, doc.Lines[0].StartMs);
    }

    [Fact]
    public void A_shift_can_never_produce_a_negative_time()
    {
        var doc = Lyrics.Text.ParseLrc("[offset:9000]\n[00:02.00]one\n", "t");
        Assert.Equal(0, doc.Lines[0].StartMs);
    }

    [Fact]
    public void Ends_are_derived_from_the_next_start_and_the_last_one_gets_a_bound()
    {
        var doc = Lyrics.Text.ParseLrc("[00:01.00]one\n[00:03.00]two\n", "t");
        Assert.Equal(3000, doc.Lines[0].EndMs);
        Assert.Equal(3000 + 4000, doc.Lines[1].EndMs);
    }

    [Fact]
    public void A_timed_credit_row_is_KEPT_by_the_parser_the_cleaner_decides()
    {
        // The parsers hand every timed row over, positionally-blind. Deciding by word list from ANYWHERE in a document
        // is the mid-song false positive `Clean` exists to make impossible.
        var doc = Lyrics.Text.ParseLrc("[00:00.10]Lyrics by: Someone Else\n[00:05.00]a real lyric\n", "t");
        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void An_untimed_payload_is_unsynced_with_no_lines()
    {
        var doc = Lyrics.Text.ParseLrc("just words\nand more\n", "t");
        Assert.Equal(Lyrics.SyncKind.Unsynced, doc.Sync);
        Assert.Empty(doc.Lines);
        Assert.False(doc.IsSynced);
    }
}

public class LyricsTtmlParserTests
{
    const string WordSynced = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body><div>
            <p begin="0:00:01.000" end="0:00:04.000">
              <span begin="0:00:01.000" end="0:00:02.000">Hel</span><span begin="0:00:02.000" end="0:00:04.000">lo</span>
              <span ttm:role="x-translation">Hallo</span>
              <span ttm:role="x-roman">Herro</span>
            </p>
          </div></body>
        </tt>
        """;

    [Fact]
    public void Timed_spans_become_syllables_and_the_role_spans_become_the_secondary_layers()
    {
        var doc = Lyrics.Text.ParseTtml(WordSynced, "t");
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        var l = Assert.Single(doc.Lines);
        Assert.Equal(1000, l.StartMs);
        Assert.Equal(4000, l.EndMs);
        Assert.Equal(2, l.Syllables.Count);
        Assert.Equal("Hallo", l.Translation);
        Assert.Equal("Herro", l.Romanization);
        Assert.DoesNotContain("Hallo", l.Text, StringComparison.Ordinal);   // a translation is never sung text
    }

    [Fact]
    public void The_capability_bits_fall_out_of_the_parsed_document()
    {
        var doc = Lyrics.Text.ParseTtml(WordSynced, "t");
        Assert.True(doc.HasTranslation);
        Assert.True(doc.HasRomanization);
        Assert.Equal(Lyrics.Prefs.HasTranslation | Lyrics.Prefs.HasRomanization, doc.SecondaryAvailable);
    }

    [Fact]
    public void A_paragraph_with_only_text_is_line_synced()
    {
        var doc = Lyrics.Text.ParseTtml(
            """<tt><body><div><p begin="1s" end="4s">plain</p></div></body></tt>""", "t");
        Assert.Equal(Lyrics.SyncKind.Line, doc.Sync);
        Assert.Equal("plain", doc.Lines[0].Text);
    }

    [Fact]
    public void Malformed_markup_is_an_empty_unsynced_document_never_a_throw()
    {
        var doc = Lyrics.Text.ParseTtml("<not xml", "t", "amll");
        Assert.Equal(Lyrics.SyncKind.Unsynced, doc.Sync);
        Assert.Empty(doc.Lines);
        Assert.Equal("amll", doc.Provider);
    }

    [Theory]
    [InlineData("900ms", 900L)]
    [InlineData("12.5s", 12_500L)]
    [InlineData("0:01.5", 1500L)]
    [InlineData("1:00:00", 3_600_000L)]
    public void Every_ttml_time_form_parses(string raw, long ms)
        => Assert.Equal(ms, Lyrics.Text.ParseTtmlTime(raw));

    [Fact]
    public void An_unparseable_time_is_null_not_zero()
    {
        Assert.Null(Lyrics.Text.ParseTtmlTime(null));
        Assert.Null(Lyrics.Text.ParseTtmlTime("   "));
        Assert.Null(Lyrics.Text.ParseTtmlTime("later"));
    }
}

public class LyricsWordFormatTests
{
    [Fact]
    public void Krc_word_times_are_RELATIVE_to_the_line_start()
    {
        var doc = Lyrics.WordFormats.ParseKrc("[1000,2000]<0,500,0>Hel<500,500,0>lo\n", "t");
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        var l = Assert.Single(doc.Lines);
        Assert.Equal(1000, l.StartMs);
        Assert.Equal("Hello", l.Text);
        Assert.Equal(1000, l.Syllables[0].StartMs);
        Assert.Equal(1500, l.Syllables[1].StartMs);
    }

    [Fact]
    public void Yrc_word_times_are_ABSOLUTE_and_its_json_credit_rows_are_skipped()
    {
        var doc = Lyrics.WordFormats.ParseYrc(
            "{\"t\":1000,\"c\":[]}\n[1000,2000](1000,500,0)Hel(1500,500,0)lo\n", "t");
        var l = Assert.Single(doc.Lines);
        Assert.Equal(1000, l.Syllables[0].StartMs);
        Assert.Equal(1500, l.Syllables[1].StartMs);
    }

    [Fact]
    public void Qrc_puts_the_timing_AFTER_the_word()
    {
        var doc = Lyrics.WordFormats.ParseQrc("[1000,2000]Hel(1000,500)lo(1500,500)\n", "t");
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);
    }

    [Fact]
    public void Qrc_is_extracted_from_its_xml_wrapper()
    {
        var doc = Lyrics.WordFormats.ParseQrc(
            "<QrcInfos><LyricInfo><Lyric_1 LyricContent=\"[1000,500]Hi(1000,500)\"/></LyricInfo></QrcInfos>", "t");
        Assert.Equal("Hi", Assert.Single(doc.Lines).Text);
    }

    [Fact]
    public void Richsync_char_offsets_are_SECONDS_from_the_line_start()
    {
        var doc = Lyrics.WordFormats.ParseRichsync(
            "[{\"ts\":1.0,\"te\":3.0,\"x\":\"Hello\",\"l\":[{\"c\":\"Hel\",\"o\":0.0},{\"c\":\"lo\",\"o\":0.5}]}]", "t");
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(3000, doc.Lines[0].EndMs);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);
    }

    [Fact]
    public void Richsync_folds_a_whitespace_token_into_the_syllable_before_it()
    {
        var doc = Lyrics.WordFormats.ParseRichsync(
            "[{\"ts\":1.0,\"te\":3.0,\"x\":\"Hello world\",\"l\":["
            + "{\"c\":\"Hello\",\"o\":0.0},{\"c\":\" \",\"o\":0.5},{\"c\":\"world\",\"o\":0.8}]}]", "t");
        var l = doc.Lines[0];
        Assert.Equal(2, l.Syllables.Count);
        Assert.Equal("Hello ", l.Syllables[0].Text);
        Assert.Equal(1000, l.Syllables[0].StartMs);
        Assert.Equal(1800, l.Syllables[0].EndMs);
        Assert.Equal("world", l.Syllables[1].Text);
        Assert.DoesNotContain(l.Syllables, s => string.IsNullOrWhiteSpace(s.Text));
    }

    [Fact]
    public void Malformed_richsync_is_an_empty_document_never_a_throw()
        => Assert.Empty(Lyrics.WordFormats.ParseRichsync("{not an array}", "t").Lines);
}

public class LyricsTimingGateTests
{
    static Lyrics.Doc Fast(int lines)
    {
        var l = new List<Lyrics.Line>(lines);
        for (int i = 0; i < lines; i++)
        {
            long start = i * 4000;
            l.Add(new Lyrics.Line(start, "one two three four five six", [
                new Lyrics.Syllable(start, start + 80, "one two three"),
                new Lyrics.Syllable(start + 80, start + 167, "four five six"),
            ], start + 167, null, null, true));
        }
        return new Lyrics.Doc("t", true, l, Lyrics.SyncKind.Syllable, "p");
    }

    [Fact]
    public void A_body_that_fires_twelve_words_in_167ms_is_rejected()
    {
        Assert.True(Lyrics.Timing.HasImplausibleWordTiming(Fast(10), out int impossible, out int judged));
        Assert.Equal(10, judged);
        Assert.Equal(10, impossible);
    }

    [Fact]
    public void A_three_line_fragment_can_never_condemn_a_provider()
        => Assert.False(Lyrics.Timing.HasImplausibleWordTiming(Fast(3), out _, out _));

    [Fact]
    public void A_short_line_says_nothing_about_its_rate()
    {
        var line = new Lyrics.Line(0, "Ooh, ah", [new Lyrics.Syllable(0, 40, "Ooh, ah")], 40, null, null, true);
        Assert.False(Lyrics.Timing.IsImpossiblyFast(line));
    }

    [Fact]
    public void A_line_synced_document_has_no_word_timing_to_judge()
    {
        var doc = new Lyrics.Doc("t", true, [new Lyrics.Line(0, "a b c d", [], 100)], Lyrics.SyncKind.Line, "p");
        Assert.False(Lyrics.Timing.HasImplausibleWordTiming(doc, out _, out int judged));
        Assert.Equal(0, judged);
    }

    [Fact]
    public void The_repair_keeps_the_starts_and_drops_the_word_timing()
    {
        var stripped = Lyrics.Timing.StripWordTiming(Fast(10));
        Assert.Equal(Lyrics.SyncKind.Line, stripped.Sync);
        for (int i = 0; i < stripped.Lines.Count; i++)
        {
            Assert.Empty(stripped.Lines[i].Syllables);
            Assert.False(stripped.Lines[i].IsWordByWord);
            Assert.Null(stripped.Lines[i].EndMs);
            Assert.Equal(i * 4000, stripped.Lines[i].StartMs);   // the part that was RIGHT survives
        }
    }

    [Fact]
    public void The_two_decoy_tells_need_a_track_duration_and_twenty_lines()
    {
        var decoy = LyricsFixture.Decoy();
        Assert.True(Lyrics.Timing.ExceedsTrackDuration(decoy, 210_000));
        Assert.False(Lyrics.Timing.ExceedsTrackDuration(decoy, 0));       // an unknown length never flags
        Assert.True(Lyrics.Timing.HasUniformLineDurations(decoy));
    }

    [Fact]
    public void A_real_four_line_document_trips_neither_tell()
    {
        var doc = Lyrics.Text.ParseLrc("[00:01.00]a\n[00:07.40]b\n[00:12.10]c\n[00:19.90]d\n", "t");
        Assert.False(Lyrics.Timing.ExceedsTrackDuration(doc, 210_000));
        Assert.False(Lyrics.Timing.HasUniformLineDurations(doc));
    }

    [Fact]
    public void Describe_names_both_decoy_tells_when_the_duration_is_in_scope()
    {
        string verdict = Lyrics.Timing.Describe(LyricsFixture.Decoy(), 210_000);
        Assert.Contains("uniform decoy timing", verdict, StringComparison.Ordinal);
        Assert.Contains("track", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_says_so_plainly_when_there_are_no_timings_at_all()
        => Assert.Contains("unsynced", Lyrics.Timing.Describe(
            new Lyrics.Doc("t", false, [], Lyrics.SyncKind.Unsynced, "p")), StringComparison.Ordinal);

    [Fact]
    public void A_synthetic_clean_document_says_clean()
        => Assert.StartsWith("clean", Lyrics.Timing.Describe(
            Lyrics.Text.ParseLrc("[00:01.00]one two\n[00:05.00]three four\n[00:09.00]five six\n", "t")), StringComparison.Ordinal);

    [Fact]
    public void A_usable_real_subtitle_is_never_given_the_rejection_verdict()
    {
        // "Usable", not "spotless": the real LRC may carry a wart or two (a cut-short closing line), which Describe is
        // allowed to REPORT — but the word-timing VERDICT and the decoy tells must never fire on it.
        string verdict = Lyrics.Timing.Describe(LyricsFixture.Subtitle(), 210_000);
        Assert.DoesNotContain("VERDICT", verdict, StringComparison.Ordinal);
        Assert.DoesNotContain("uniform decoy timing", verdict, StringComparison.Ordinal);
    }
}

public class LyricsCreditRuleTests
{
    [Theory]
    [InlineData("[ti:Song]", true)]
    [InlineData("[offset:-200]", true)]
    [InlineData("{\"t\":0,\"c\":[]}", true)]
    [InlineData("<Lyric_1 LyricContent=\"x\"/>", true)]
    [InlineData("[00:07.41]sung", false)]      // a timed row's key is DIGITS
    [InlineData("[7416,2087]sung", false)]
    [InlineData("[ti:X][00:01.00]sung", false)]   // a timed row with a tag glued on is still a timed row
    public void Structural_metadata_is_language_free_and_never_wrong(string line, bool expected)
        => Assert.Equal(expected, Lyrics.CreditRules.IsStructuralMetadata(line));

    [Theory]
    [InlineData("作曲：Someone")]
    [InlineData("Lyrics by: Someone Else")]
    [InlineData("Produced by Another Person")]
    [InlineData("℗ 2020 A Label")]
    public void The_credit_shapes_are_recognised(string text)
        => Assert.True(Lyrics.CreditRules.LooksLikeCreditLine(text, out _, out _));

    [Theory]
    [InlineData("Girl: I told you")]           // a clause, not a name list
    [InlineData("Is this a credit?")]
    [InlineData("just a lyric")]
    public void A_lyric_is_not_a_credit(string text)
        => Assert.False(Lyrics.CreditRules.LooksLikeCreditLine(text, out _, out _));

    [Fact]
    public void A_known_key_with_a_full_width_colon_is_the_only_mid_document_verdict()
    {
        Assert.True(Lyrics.CreditRules.LooksLikeCreditLine("作曲：Someone", out bool known, out bool full));
        Assert.True(known);
        Assert.True(full);

        Assert.True(Lyrics.CreditRules.LooksLikeCreditLine("Mixer: Someone Else", out bool known2, out bool full2));
        Assert.False(known2);   // "Mixer" is not in the vocabulary — it passed on SHAPE alone
        Assert.False(full2);
    }

    [Theory]
    [InlineData("纯音乐，请欣赏")]
    [InlineData("This song is instrumental")]
    [InlineData("Instrumental")]
    public void An_instrumental_notice_is_recognised(string text)
        => Assert.True(Lyrics.CreditRules.IsInstrumentalNotice(text));

    [Fact]
    public void Boilerplate_is_a_superset_of_the_instrumental_notice()
    {
        Assert.True(Lyrics.CreditRules.IsProviderBoilerplate("纯音乐，请欣赏"));
        Assert.True(Lyrics.CreditRules.IsProviderBoilerplate("未经许可不得翻唱或使用"));
        Assert.False(Lyrics.CreditRules.IsProviderBoilerplate("a real lyric"));
    }
}

public class LyricsCleanTests
{
    static Lyrics.Doc Doc(params Lyrics.Line[] lines)
        => new("t", true, lines, Lyrics.SyncKind.Line, "p");

    static Lyrics.Line L(long start, string text, long? end = null) => new(start, text, [], end);

    [Fact]
    public void Symbol_only_rows_go_and_the_dropped_stamp_becomes_the_line_above_its_end()
    {
        var doc = Doc(L(1000, "a lyric"), L(4000, "♪"), L(9000, "another"));
        var cleaned = Lyrics.Clean.Apply(doc);
        Assert.Equal(2, cleaned.Lines.Count);
        Assert.Equal(4000, cleaned.Lines[0].EndMs);   // the gap the interlude dots are detected FROM survives
    }

    [Fact]
    public void An_authored_end_is_never_overwritten()
    {
        var doc = Doc(L(1000, "a lyric", 2500), L(4000, "♪"), L(9000, "another"));
        Assert.Equal(2500, Lyrics.Clean.Apply(doc).Lines[0].EndMs);
    }

    [Fact]
    public void Leading_and_trailing_credit_runs_are_dropped()
    {
        var doc = Doc(
            L(0, "作词：A"), L(200, "作曲：B"),
            L(5000, "a real lyric"), L(9000, "another"),
            L(20_000, "Produced by Someone Else"));
        var cleaned = Lyrics.Clean.Apply(doc);
        Assert.Equal(2, cleaned.Lines.Count);
        Assert.Equal("a real lyric", cleaned.Lines[0].Text);
    }

    [Fact]
    public void A_mid_song_credit_shaped_line_survives_unless_it_is_a_known_key_with_a_full_width_colon()
    {
        var survives = Doc(L(0, "first"), L(5000, "Girl: I told you"), L(9000, "last"));
        Assert.Equal(3, Lyrics.Clean.Apply(survives).Lines.Count);

        var dropped = Doc(L(0, "first"), L(5000, "作曲：Someone"), L(9000, "last"));
        Assert.Equal(2, Lyrics.Clean.Apply(dropped).Lines.Count);
    }

    [Fact]
    public void A_document_that_is_ENTIRELY_credit_shaped_keeps_its_lines()
    {
        // If every surviving line is credit-shaped, the shape is the document's IDIOM and the grammar is what is wrong.
        var doc = Doc(L(0, "作词：A"), L(200, "作曲：B"), L(400, "编曲：C"));
        Assert.Equal(3, Lyrics.Clean.Apply(doc).Lines.Count);
    }

    [Fact]
    public void An_instrumental_notice_empties_the_document_credits_included()
    {
        var doc = Doc(L(0, "作词：A"), L(200, "纯音乐，请欣赏"));
        Assert.Empty(Lyrics.Clean.Apply(doc).Lines);
    }

    [Fact]
    public void A_leading_title_header_is_dropped_and_a_chorus_line_that_IS_the_title_survives()
    {
        var header = Doc(L(0, LyricsFixture.Title + " - " + LyricsFixture.Artist), L(9000, "a real lyric"));
        Assert.Single(Lyrics.Clean.Apply(header, LyricsFixture.Title, LyricsFixture.Artist).Lines);

        // The same words, mid-song, with no separator and no pre-roll gap: a hook, not a header.
        var chorus = Doc(L(0, "first"), L(4000, LyricsFixture.Title), L(8000, "last"));
        Assert.Equal(3, Lyrics.Clean.Apply(chorus, LyricsFixture.Title, LyricsFixture.Artist).Lines.Count);
    }

    [Fact]
    public void A_bracketed_annotation_never_dilutes_the_header_overlap()
    {
        var doc = Doc(L(0, "The Night We Met (《十三个原因 第一季》电视剧插曲) - Lord Huron"), L(9000, "I am not the only traveller"));
        Assert.Single(Lyrics.Clean.Apply(doc, "The Night We Met", "Lord Huron").Lines);
    }

    [Theory]
    [InlineData("a (b) c", "a  c")]
    [InlineData("a （b） c", "a  c")]
    [InlineData("a 《b》 c", "a  c")]
    [InlineData("a 【b】 c", "a  c")]
    [InlineData("a [b] c", "a  c")]
    public void Every_bracket_family_is_removed_WHOLE(string input, string expected)
        => Assert.Equal(expected, Lyrics.Clean.StripBracketedAnnotations(input));

    [Fact]
    public void An_UNBALANCED_bracket_leaves_the_text_untouched()
        => Assert.Equal("a (b c", Lyrics.Clean.StripBracketedAnnotations("a (b c"));

    [Fact]
    public void A_clean_document_is_returned_UNCHANGED_by_reference()
    {
        var doc = Doc(L(0, "one"), L(4000, "two"));
        Assert.Same(doc, Lyrics.Clean.Apply(doc));
    }

    [Fact]
    public void The_real_reference_loses_its_music_notes_and_blanks()
    {
        var raw = LyricsFixture.Reference();
        var cleaned = Lyrics.Clean.Apply(raw, LyricsFixture.Title, LyricsFixture.Artist);
        Assert.True(cleaned.Lines.Count < raw.Lines.Count, "the reference padding survived the cleaner");
        foreach (var l in cleaned.Lines) Assert.False(Lyrics.Clean.IsSymbolOnly(l.Text));
    }
}

public class LyricsQueryTests
{
    static Lyrics.Request Req(string title, params string[] artists)
        => new("id", "spotify:track:id", title, artists, "Album", 200_000);

    [Fact]
    public void The_ladder_is_most_specific_first()
    {
        var v = Lyrics.Query.Variants(Req("Song (feat. Guest)", "Main"));
        Assert.Equal(3, v.Count);
        Assert.Equal("Song (feat. Guest) Main", v[0]);
        Assert.Equal("Song Main", v[1]);
        Assert.Equal("Song", v[2]);
    }

    [Fact]
    public void A_no_feat_title_collapses_the_first_two_levels()
    {
        var v = Lyrics.Query.Variants(Req("Song", "Main"));
        Assert.Equal(2, v.Count);
        Assert.Equal("Song Main", v[0]);
        Assert.Equal("Song", v[1]);
    }

    [Theory]
    [InlineData("Song (feat. X)", "Song")]
    [InlineData("Song [ft X]", "Song")]
    [InlineData("Song (featuring X and Y)", "Song")]
    [InlineData("Song - feat. X", "Song")]
    [InlineData("Song – ft X", "Song")]
    [InlineData("Feature Creature", "Feature Creature")]   // \b after the keyword: "feature" is NOT a feat clause
    public void The_feat_clauses_are_stripped_and_nothing_else_is(string title, string expected)
        => Assert.Equal(expected, Lyrics.Query.StripFeat(title));

    [Fact]
    public void Search_normalization_folds_full_width_and_curly_forms_and_keeps_case()
    {
        Assert.Equal("Song", Lyrics.Query.Normalize("Ｓｏｎｇ"));
        Assert.Equal("don't", Lyrics.Query.Normalize("don’t"));
        Assert.Equal("a b", Lyrics.Query.Normalize("a　b"));
        Assert.Equal("Title Artist", Lyrics.Query.Normalize("Title - Artist"));
    }

    [Fact]
    public void The_pair_ladder_deduplicates_on_the_pair()
    {
        var pairs = Lyrics.Query.TitleArtistVariants(Req("Song", "Main"));
        Assert.Equal(2, pairs.Count);
        Assert.Equal(("Song", "Main"), pairs[0]);
        Assert.Equal(("Song", ""), pairs[1]);
    }

    [Fact]
    public void Comparison_normalization_is_a_DIFFERENT_function_from_the_search_one()
    {
        // The reranker aligns on THIS: lowercase, each punctuation mark becomes a space (as in 0.2.9), whitespace collapsed.
        Assert.Equal("don t stop", Lyrics.Text.Normalize("Don't  Stop!"));
        Assert.Equal("", Lyrics.Text.Normalize("♪ ... —"));
        Assert.Equal("Don't Stop!", Lyrics.Query.Normalize("Don't Stop!"));
    }
}

public class LyricsFixtureTests
{
    [Fact]
    public void The_parser_reproduces_the_richsync_payload_faithfully()
    {
        var doc = LyricsFixture.Richsync();
        Assert.Equal(39, doc.Lines.Count);
        var l = doc.Lines[2];
        Assert.Equal("She dashed by me in painted on jeans", l.Text);
        Assert.Equal(19_480, l.StartMs);
        Assert.Equal(20_297, l.EndMs);   // ts=19.48 / te=20.2978, read straight off the file
    }

    [Fact]
    public void The_richsync_line_STARTS_are_correct_and_only_its_ENDS_are_not()
    {
        var rich = LyricsFixture.Richsync();
        var lrc = LyricsFixture.Subtitle();
        int matched = 0;
        foreach (var r in rich.Lines)
            foreach (var s in lrc.Lines)
                if (Math.Abs(s.StartMs - r.StartMs) <= 100) { matched++; break; }
        Assert.True(matched >= rich.Lines.Count - 2, $"only {matched}/{rich.Lines.Count} starts line up");
    }

    [Fact]
    public void The_richsync_is_rejected_and_the_LRC_in_the_same_response_is_clean()
    {
        Assert.True(Lyrics.Timing.HasImplausibleWordTiming(LyricsFixture.Richsync(), out int impossible, out int judged));
        Assert.True(impossible * 2 >= judged, $"{impossible}/{judged} lines impossibly fast");

        var subtitle = LyricsFixture.Subtitle();
        Assert.Equal(Lyrics.SyncKind.Line, subtitle.Sync);
        Assert.False(Lyrics.Timing.HasImplausibleWordTiming(subtitle, out _, out int subJudged));
        Assert.Equal(0, subJudged);
        // "Usable", not "spotless": real provider data has warts, which is exactly why the rule is a MAJORITY one.
        int fast = 0;
        foreach (var l in subtitle.Lines) if (Lyrics.Timing.IsImpossiblyFast(l)) fast++;
        Assert.True(fast <= 1, $"{fast} of {subtitle.Lines.Count} subtitle lines are impossibly fast");
    }

    [Fact]
    public void The_KRC_carries_GENUINE_word_timing()
    {
        var doc = LyricsFixture.Kugou();
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        Assert.False(Lyrics.Timing.HasImplausibleWordTiming(doc, out _, out _));
        foreach (var l in doc.Lines)
            if (l.Text.StartsWith("She dashed by me", StringComparison.Ordinal))
            {
                // Musixmatch claimed this line took 818 ms; the KRC says ~3800, which is what a human would sing.
                Assert.InRange(Lyrics.Timing.WordSpanMs(l), 3000, 4200);
                return;
            }
        Assert.Fail("the KRC fixture no longer contains the reference line");
    }

    [Fact]
    public void The_colour_lyrics_reference_parses_line_synced()
    {
        var doc = LyricsFixture.Reference();
        Assert.Equal(Lyrics.SyncKind.Line, doc.Sync);
        Assert.True(doc.Lines.Count > 20);
        Assert.Equal("spotify", doc.Provider);
    }

    [Fact]
    public void The_decoy_fixture_trips_BOTH_decoy_checks()
    {
        var decoy = LyricsFixture.Decoy();
        Assert.True(decoy.Lines.Count >= 20);
        Assert.True(Lyrics.Timing.ExceedsTrackDuration(decoy, 210_000));
        Assert.True(Lyrics.Timing.HasUniformLineDurations(decoy));
    }
}
