// ── Wavee.Tests/HomeUi/FacetPivotTests.cs — Words ordering, Resolve, Target ─────────────────────────────────────────
//
// `Enabled` was cut (remediation F35: dead, 0 callers) — a word's selectability is decided at the call site now.
//
// ChipInput is the shared contract owned by Home/Model.cs (A1): record ChipInput(string Id, string Label,
// string? SubId, string? SubLabel).

using System.Collections.Generic;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetPivotTests
{
    static readonly ChipInput Music = new("music-chip", "Music", "music-following-chip", "Following");
    static readonly ChipInput Podcasts = new("podcasts-chip", "Podcasts", "podcasts-following-chip", "Following");
    static readonly ChipInput Audiobooks = new("audiobooks-chip", "Audiobooks", null, null);

    static FacetWord[] StandardWords() => FacetPivot.Words(new[] { Music, Podcasts, Audiobooks }, "All");

    [Fact]
    public void All_is_always_first_with_the_synthetic_empty_id()
    {
        var words = StandardWords();
        Assert.Equal("", words[0].Id);
        Assert.Equal("All", words[0].Label);
        Assert.False(words[0].HasSub);
    }

    [Fact]
    public void Words_after_All_follow_server_chip_order()
    {
        var words = StandardWords();
        Assert.Equal(4, words.Length);
        Assert.Equal("music-chip", words[1].Id);
        Assert.Equal("podcasts-chip", words[2].Id);
        Assert.Equal("audiobooks-chip", words[3].Id);
    }

    [Fact]
    public void Following_sub_chip_only_present_where_the_server_sent_one()
    {
        var words = StandardWords();
        Assert.True(words[1].HasSub); // Music
        Assert.Equal("music-following-chip", words[1].SubId);
        Assert.True(words[2].HasSub); // Podcasts
        Assert.Equal("podcasts-following-chip", words[2].SubId);
        Assert.False(words[3].HasSub); // Audiobooks: no sub chip
        Assert.Null(words[3].SubId);
    }

    [Fact]
    public void Null_chip_list_still_yields_the_All_word_alone()
    {
        var words = FacetPivot.Words(null, "All");
        Assert.Single(words);
        Assert.Equal("", words[0].Id);
    }

    [Fact]
    public void Empty_chip_list_still_yields_the_All_word_alone()
    {
        var words = FacetPivot.Words(new List<ChipInput>(), "All");
        Assert.Single(words);
    }

    // ── Resolve ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_plain_facet_id_finds_its_word_with_following_off()
    {
        var words = StandardWords();
        var (word, following) = FacetPivot.Resolve(words, "podcasts-chip");
        Assert.Equal(2, word);
        Assert.False(following);
    }

    [Fact]
    public void Resolve_on_sub_id_finds_the_parent_word_with_following_on()
    {
        var words = StandardWords();
        var (word, following) = FacetPivot.Resolve(words, "music-following-chip");
        Assert.Equal(1, word);
        Assert.True(following);
    }

    [Fact]
    public void Resolve_unknown_id_falls_back_to_All()
    {
        var words = StandardWords();
        var (word, following) = FacetPivot.Resolve(words, "unknown-chip");
        Assert.Equal(0, word);
        Assert.False(following);
    }

    [Fact]
    public void Resolve_empty_id_is_All()
    {
        var words = StandardWords();
        var (word, following) = FacetPivot.Resolve(words, "");
        Assert.Equal(0, word);
        Assert.False(following);
    }

    // ── Target ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Target_off_returns_the_words_own_id()
    {
        var words = StandardWords();
        Assert.Equal("music-chip", FacetPivot.Target(words[1], followingOn: false));
    }

    [Fact]
    public void Target_on_returns_the_sub_id_when_present()
    {
        var words = StandardWords();
        Assert.Equal("music-following-chip", FacetPivot.Target(words[1], followingOn: true));
    }

    [Fact]
    public void Target_on_for_a_word_with_no_sub_falls_back_to_its_own_id()
    {
        var words = StandardWords();
        Assert.Equal("audiobooks-chip", FacetPivot.Target(words[3], followingOn: true));
    }

    // ── the word the Following toggle drives (Classic Huge stands it on every word: C-Home-Huge) ──

    [Fact]
    public void FollowingWord_is_the_selected_word_when_it_has_a_sub_chip()
    {
        var words = StandardWords();
        Assert.Equal(1, FacetPivot.FollowingWord(words, 1, everyWord: false));
        Assert.Equal(2, FacetPivot.FollowingWord(words, 2, everyWord: true));
    }

    [Fact]
    public void FollowingWord_on_a_word_without_one_is_none_unless_the_toggle_stands_on_every_word()
    {
        var words = StandardWords();
        Assert.Equal(-1, FacetPivot.FollowingWord(words, 0, everyWord: false));   // All: the tuned row shows no toggle
        Assert.Equal(1, FacetPivot.FollowingWord(words, 0, everyWord: true));     // All at Huge: Music's Following
        Assert.Equal(1, FacetPivot.FollowingWord(words, 3, everyWord: true));     // Audiobooks has none: the first word that does
    }

    [Fact]
    public void FollowingWord_is_none_when_no_word_has_a_sub_chip()
    {
        var words = FacetPivot.Words(new[] { Audiobooks }, "All");
        Assert.Equal(-1, FacetPivot.FollowingWord(words, 0, everyWord: true));
        Assert.Equal(-1, FacetPivot.FollowingWord(System.Array.Empty<FacetWord>(), 0, everyWord: true));
    }
}
