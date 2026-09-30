// ── Wavee.Tests/LyricsMatchTests.cs — stage-0 metadata match (Lyrics.Match.cs) ───────────────────────────────────────
//
// Pure: `MetadataMatch.Score/Pick/Describe`, `VersionMarkers`, `CjkFold`. The cases follow Lyricify/Unilyric's
// CompareHelper expectations (exact, remaster/feat suffixes, bracketed versions, artist order) plus the CJK folds and
// the duration rules of the reranker v2 plan (grain forgiveness, the 5 s gate, the no-duration cap).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsMatchTests
{
    static Lyrics.Request Req(string title, string[] artists, string album = "", long durationMs = 200_000)
        => new("t1", "spotify:track:t1", title, artists, album, durationMs);

    static Lyrics.Hit Hit(string title, string[] artists, string? album = null, long durationMs = 200_000,
                          int grainMs = 0, string id = "h1")
        => new(id, title, artists, album, durationMs, grainMs);

    static Lyrics.MatchScore Score(Lyrics.Request req, Lyrics.Hit hit) => Lyrics.MetadataMatch.Score(req, hit);

    // ── titles ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Exact_metadata_is_perfect()
    {
        var s = Score(Req("Song", ["Artist"], "Album"), Hit("Song", ["Artist"], "Album"));
        Assert.Equal(Lyrics.MatchBand.Perfect, s.Band);
        Assert.Equal(1.0, s.Confidence, 6);
        Assert.Equal(7, s.Title);
        Assert.Equal(7, s.Artist);
        Assert.Equal(7, s.Album);
        Assert.Equal(7, s.Duration);
    }

    [Fact]
    public void Case_and_punctuation_only_differences_stay_near_perfect()
    {
        var s = Score(Req("Don't Stop", ["Artist"]), Hit("DON'T STOP", ["artist"]));
        Assert.Equal(Lyrics.MatchBand.Perfect, s.Band);

        var p = Score(Req("Hello!", ["Artist"]), Hit("Hello", ["Artist"]));
        Assert.Equal(6, p.Title);
        Assert.True(p.Band >= Lyrics.MatchBand.VeryHigh);
    }

    [Fact]
    public void Remaster_dash_suffix_on_one_side_is_very_high()
    {
        var s = Score(Req("Song - Remastered 2011", ["Artist"], "Album"), Hit("Song", ["Artist"], "Album"));
        Assert.Equal(6, s.Title);
        Assert.Equal(Lyrics.MatchBand.VeryHigh, s.Band);
    }

    [Fact]
    public void Different_remaster_spellings_on_both_sides_are_very_high()
    {
        var s = Score(Req("Song - Remastered 2011", ["Artist"]), Hit("Song (2009 Remaster)", ["Artist"]));
        Assert.Equal(6, s.Title);
    }

    [Fact]
    public void Deluxe_and_explicit_suffixes_are_version_neutral()
    {
        Assert.Equal(6, Score(Req("Song", ["Artist"]), Hit("Song (Explicit)", ["Artist"])).Title);
        Assert.Equal(6, Score(Req("Song [Deluxe Edition]", ["Artist"]), Hit("Song", ["Artist"])).Title);
    }

    [Fact]
    public void Feat_clause_is_stripped()
    {
        var s = Score(Req("Song (feat. Guest)", ["Main", "Guest"]), Hit("Song", ["Main"]));
        Assert.Equal(6, s.Title);
        Assert.True(s.Band >= Lyrics.MatchBand.High, s.Band.ToString());
    }

    [Fact]
    public void Dash_form_equals_bracket_form()
    {
        var s = Score(Req("Song - Pt. 2", ["Artist"]), Hit("Song (Pt. 2)", ["Artist"]));
        Assert.Equal(6, s.Title);
        Assert.True(s.Band >= Lyrics.MatchBand.VeryHigh);
    }

    [Fact]
    public void Same_base_with_an_unknown_bracket_on_one_side_is_low_and_caps_at_medium()
    {
        var s = Score(Req("Song", ["Artist"]), Hit("Song (Taylor's Version)", ["Artist"]));
        Assert.Equal(2, s.Title);
        Assert.Equal(Lyrics.MatchBand.Medium, s.Band);
    }

    [Fact]
    public void A_different_title_never_reaches_medium()
    {
        var s = Score(Req("Song", ["Artist"]), Hit("Completely Different Tune", ["Artist"]));
        Assert.Equal(0, s.Title);
        Assert.True(s.Band <= Lyrics.MatchBand.Low, s.Band.ToString());
    }

    [Fact]
    public void A_one_letter_typo_at_the_same_length_is_high_title()
    {
        var s = Score(Req("Yesterday", ["Artist"]), Hit("Yesterdey", ["Artist"]));
        Assert.Equal(5, s.Title);
    }

    // ── version markers ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Live_hit_for_a_studio_request_is_gated()
    {
        var s = Score(Req("Song", ["Artist"]), Hit("Song - Live", ["Artist"]));
        Assert.Equal(Lyrics.MatchBand.None, s.Band);
        Assert.Equal(0, s.Confidence);
        Assert.Contains("live", s.Reason);
    }

    [Fact]
    public void Live_request_accepts_a_live_hit()
    {
        var s = Score(Req("Song - Live", ["Artist"]), Hit("Song (Live at Wembley)", ["Artist"]));
        Assert.NotEqual(Lyrics.MatchBand.None, s.Band);
    }

    [Fact]
    public void Accompaniment_hit_is_gated()
    {
        var s = Score(Req("爱情", ["歌手"]), Hit("爱情 (伴奏)", ["歌手"]));
        Assert.Equal(Lyrics.MatchBand.None, s.Band);
        Assert.Contains("instrumental", s.Reason);
    }

    [Fact]
    public void Katakana_cover_marker_is_gated()
    {
        Assert.True(Lyrics.VersionMarkers.Mismatch("Song", "Song (カバー)", out string marker));
        Assert.Equal("cover", marker);
        Assert.Equal(Lyrics.MatchBand.None, Score(Req("Song", ["Artist"]), Hit("Song (カバー)", ["Artist"])).Band);
    }

    [Fact]
    public void Marker_groups_match_across_languages()
    {
        Assert.False(Lyrics.VersionMarkers.Mismatch("Song (Instrumental)", "Song (伴奏)", out _));
        Assert.False(Lyrics.VersionMarkers.Mismatch("Song - Live", "Song (現場)", out _));   // Traditional spelling folds
        Assert.False(Lyrics.VersionMarkers.Mismatch("Song (Inst.)", "Song (Karaoke)", out _));
    }

    [Fact]
    public void Latin_markers_need_word_boundaries()
    {
        Assert.False(Lyrics.VersionMarkers.Mismatch("Song", "Deliver", out _));
        Assert.False(Lyrics.VersionMarkers.Mismatch("Alive", "Alive", out _));
        Assert.False(Lyrics.VersionMarkers.Mismatch("Song", "Discovery", out _));
        Assert.True(Lyrics.VersionMarkers.Mismatch("Song", "Song (Sped Up)", out string m));
        Assert.Equal("sped up", m);
    }

    // ── CJK fold ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Traditional_folds_to_simplified()
    {
        Assert.Equal("爱情", Lyrics.CjkFold.Fold("愛情"));
        Assert.Equal(Lyrics.CjkFold.Fold("後來"), Lyrics.CjkFold.Fold("后来"));
        Assert.Equal(Lyrics.CjkFold.Fold("開始"), Lyrics.CjkFold.Fold("开始"));

        var s = Score(Req("愛情", ["王菲"]), Hit("爱情", ["王菲"]));
        Assert.Equal(7, s.Title);
        Assert.Equal(Lyrics.MatchBand.Perfect, s.Band);
    }

    [Fact]
    public void Katakana_folds_to_hiragana_and_half_width_kana_composes()
    {
        Assert.Equal("ありがとう", Lyrics.CjkFold.Fold("アリガトウ"));
        Assert.Equal("ありがとう", Lyrics.CjkFold.Fold("ｱﾘｶﾞﾄｳ"));
        Assert.Equal("ぱん", Lyrics.CjkFold.Fold("ﾊﾟﾝ"));
        Assert.Equal(7, Score(Req("アリガトウ", ["Artist"]), Hit("ありがとう", ["Artist"])).Title);
    }

    [Fact]
    public void Full_width_forms_fold_to_ascii_lowercase()
    {
        Assert.Equal("hello world!", Lyrics.CjkFold.Fold("ＨＥＬＬＯ　Ｗｏｒｌｄ！"));
        Assert.Equal(7, Score(Req("Ｓｏｎｇ", ["Artist"]), Hit("song", ["Artist"])).Title);
    }

    [Fact]
    public void IsCjk_covers_han_kana_and_hangul()
    {
        Assert.True(Lyrics.CjkFold.IsCjk('爱'));
        Assert.True(Lyrics.CjkFold.IsCjk('㐀'));
        Assert.True(Lyrics.CjkFold.IsCjk('あ'));
        Assert.True(Lyrics.CjkFold.IsCjk('カ'));
        Assert.True(Lyrics.CjkFold.IsCjk('한'));
        Assert.False(Lyrics.CjkFold.IsCjk('a'));
        Assert.False(Lyrics.CjkFold.IsCjk('é'));
    }

    // ── duration ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Whole_second_grain_forgives_rounding()
    {
        var s = Score(Req("Song", ["Artist"], durationMs: 200_400), Hit("Song", ["Artist"], durationMs: 200_000, grainMs: 1000));
        Assert.Equal(7, s.Duration, 6);
        Assert.True(s.Band >= Lyrics.MatchBand.High);

        var ms = Score(Req("Song", ["Artist"], durationMs: 200_400), Hit("Song", ["Artist"], durationMs: 200_000));
        Assert.True(ms.Duration < 7);
    }

    [Fact]
    public void Missing_hit_duration_drops_the_dimension_and_caps_at_high()
    {
        var s = Score(Req("Song", ["Artist"]), Hit("Song", ["Artist"], durationMs: 0));
        Assert.Equal(-1, s.Duration);
        Assert.Equal(Lyrics.MatchBand.High, s.Band);
        Assert.Equal(1.0, s.Confidence, 6);
    }

    [Fact]
    public void Five_second_gate()
    {
        var over = Score(Req("Song", ["Artist"], durationMs: 200_000), Hit("Song", ["Artist"], durationMs: 205_001));
        Assert.Equal(Lyrics.MatchBand.None, over.Band);
        Assert.Equal(0, over.Confidence);

        var under = Score(Req("Song", ["Artist"], durationMs: 200_000), Hit("Song", ["Artist"], durationMs: 204_999));
        Assert.NotEqual(Lyrics.MatchBand.None, under.Band);
        Assert.True(under.Band < Lyrics.MatchBand.High);
    }

    // ── artists ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Artist_order_and_separators_do_not_matter()
    {
        var req = Req("Song", ["A Artist", "B Artist"]);
        Assert.Equal(7, Score(req, Hit("Song", ["B Artist", "A Artist"])).Artist);
        Assert.Equal(7, Score(req, Hit("Song", ["B Artist & A Artist"])).Artist);
        Assert.Equal(7, Score(req, Hit("Song", ["A Artist feat. B Artist"])).Artist);
        Assert.Equal(7, Score(req, Hit("Song", ["A Artist、B Artist"])).Artist);
        Assert.Equal(7, Score(Req("Song", ["Jay Chou"]), Hit("Song", ["JAYCHOU"])).Artist);
        Assert.Equal(7, Score(Req("Song", ["周杰倫"]), Hit("Song", ["周杰伦"])).Artist);
    }

    [Fact]
    public void Partial_artist_overlap_scores_between()
    {
        var s = Score(Req("Song", ["Main"]), Hit("Song", ["Main", "Guest"]));
        Assert.Equal(5, s.Artist);
    }

    [Fact]
    public void A_wrong_artist_caps_at_medium()
    {
        var s = Score(Req("Song", ["Artist"]), Hit("Song", ["Somebody Else"]));
        Assert.Equal(0, s.Artist);
        Assert.Equal(Lyrics.MatchBand.Medium, s.Band);
    }

    // ── pick + describe ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pick_returns_the_best_index()
    {
        var req = Req("Song", ["Artist"]);
        Lyrics.Hit[] hits =
        [
            Hit("Completely Different Tune", ["Artist"], id: "a"),
            Hit("Song - Live", ["Artist"], id: "b"),
            Hit("Song", ["Artist"], durationMs: 201_500, id: "c"),
            Hit("Song", ["Artist"], durationMs: 200_100, id: "d"),
        ];
        int i = Lyrics.MetadataMatch.Pick(req, hits, Lyrics.MatchBand.Medium, out var best);
        Assert.Equal(3, i);
        Assert.True(best.Band >= Lyrics.MatchBand.VeryHigh);
    }

    [Fact]
    public void Pick_returns_minus_one_below_the_floor()
    {
        var req = Req("Song", ["Artist"]);
        Lyrics.Hit[] hits = [Hit("Completely Different Tune", ["Artist"]), Hit("Song - Live", ["Artist"])];
        Assert.Equal(-1, Lyrics.MetadataMatch.Pick(req, hits, Lyrics.MatchBand.Medium, out var best));
        Assert.True(best.Band < Lyrics.MatchBand.Medium);

        Assert.Equal(-1, Lyrics.MetadataMatch.Pick(req, [], Lyrics.MatchBand.Medium, out _));

        // A gated hit never qualifies, not even at floor None.
        Assert.Equal(-1, Lyrics.MetadataMatch.Pick(req, [Hit("Song - Live", ["Artist"])], Lyrics.MatchBand.None, out _));
    }

    [Fact]
    public void Describe_is_a_compact_breadcrumb()
    {
        var req = Req("Song", ["Artist"], durationMs: 200_000);
        var hit = Hit("Song - Remastered", ["Artist"], durationMs: 200_320, grainMs: 1000, id: "a1b2");
        var s = Lyrics.MetadataMatch.Score(req, hit);
        string d = Lyrics.MetadataMatch.Describe(hit, s);
        Assert.StartsWith("a1b2 " + s.Band, d);
        Assert.Contains("title=6", d);
        Assert.Contains("artist=7", d);
        Assert.Contains("dur=+320ms", d);
    }
}
