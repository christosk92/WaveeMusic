// ── Wavee.Tests/AiLyricsResultsTests.cs — the AI lyrics results codec (plan §3.3) ────────────────────────────────────
//
// Pure: string in, string out. The "final" document each test saves is built the way the job builds it
// (`Align.Words` + `Align.WordSyncedLine` over the provider's lines), so the overlay is checked against the real shape.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsResultsTests
{
    const ulong Hash = 0xF00DCAFE12345678UL;
    const long Saved = 1_790_000_000_000L;

    static Lyrics.Doc Provider()
        => new("t1", true,
        [
            new Lyrics.Line(1000, "Nobody pray for me", [], Translation: "Niemand bidt voor mij"),
            new Lyrics.Line(5000, "♪", []),
            new Lyrics.Line(9000, "(Oh) yeah, it's 'bout time…", [], Translation: "(Oh) ja, het werd tijd"),
            new Lyrics.Line(13000, "사랑해 my love", [], Romanization: "saranghae my love"),
        ], Lyrics.SyncKind.Line, "spotify", Language: "en");

    /// <summary>What the job's final BuildDoc yields for the provider: every line with words word-synced.</summary>
    static Lyrics.Doc Final(Lyrics.Doc provider)
    {
        var lines = new List<Lyrics.Line>(provider.Lines.Count);
        foreach (var src in provider.Lines)
        {
            var words = AiLyrics.Align.Words(src.Text);
            if (words.Count == 0) { lines.Add(src); continue; }
            var times = new AiLyrics.Align.WordTime[words.Count];
            double t = src.StartMs / 1000.0 + 0.05;
            for (int i = 0; i < words.Count; i++) { times[i] = new AiLyrics.Align.WordTime(t, t + 0.3); t += 0.35; }
            lines.Add(AiLyrics.Align.WordSyncedLine(src, words, times, null));
        }
        return provider with
        {
            Lines = lines,
            IsSynced = true,
            Sync = Lyrics.SyncKind.Syllable,
            Provider = AiLyrics.ProviderId,
            Origin = provider.Provider,
            Language = "en",
            Generated = true,
        };
    }

    static AiLyrics.Results.ResultDoc Save(Lyrics.Doc final)
    {
        string json = AiLyrics.Results.Encode(final, Hash, Saved);
        Assert.True(AiLyrics.Results.TryDecode(json, out var r));
        return r;
    }

    // ── codec ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Round_trip_keeps_every_field_and_word()
    {
        var final = Final(Provider());
        var r = Save(final);

        Assert.Equal("t1", r.TrackId);
        Assert.Equal(AiLyrics.PackVersion, r.Pack);
        Assert.Equal("en", r.Language);
        Assert.Equal("spotify", r.Origin);
        Assert.Equal(Hash, r.SourceHash);
        Assert.Equal(Saved, r.SavedUnixMs);
        Assert.Equal(final.Lines.Count, r.Lines.Count);
        for (int li = 0; li < final.Lines.Count; li++)
        {
            var line = final.Lines[li];
            var saved = r.Lines[li];
            Assert.Equal(line.StartMs, saved.StartMs);
            Assert.Equal(line.EndMs, saved.EndMs);
            if (!line.IsWordByWord) { Assert.Empty(saved.Words); continue; }
            Assert.Equal(line.Syllables.Count, saved.Words.Count);
            for (int i = 0; i < line.Syllables.Count; i++)
            {
                Assert.Equal(line.Syllables[i].StartMs, saved.Words[i].StartMs);
                Assert.Equal(line.Syllables[i].EndMs, saved.Words[i].EndMs);
                Assert.Equal(line.Syllables[i].Text, saved.Words[i].Text);
            }
        }
        Assert.Empty(r.Lines[1].Words);                                    // "♪" has no words
    }

    [Fact]
    public void Null_language_and_origin_round_trip_as_null()
    {
        var final = Final(Provider()) with { Language = null, Origin = null };
        var r = Save(final);
        Assert.Null(r.Language);
        Assert.Null(r.Origin);
    }

    [Fact]
    public void Malformed_or_foreign_files_do_not_decode()
    {
        string good = AiLyrics.Results.Encode(Final(Provider()), Hash, Saved);
        Assert.True(AiLyrics.Results.TryDecode(good, out _));

        Assert.False(AiLyrics.Results.TryDecode("", out _));
        Assert.False(AiLyrics.Results.TryDecode("not json", out _));
        Assert.False(AiLyrics.Results.TryDecode("[]", out _));
        Assert.False(AiLyrics.Results.TryDecode(good.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal), out _));
        Assert.False(AiLyrics.Results.TryDecode(good.Replace("\"sourceHash\":\"", "\"sourceHash\":\"zz", StringComparison.Ordinal), out _));
        Assert.False(AiLyrics.Results.TryDecode(good[..(good.Length / 2)], out _));
        Assert.False(AiLyrics.Results.TryDecode(
            "{\"v\":1,\"trackId\":\"t1\",\"pack\":1,\"sourceHash\":\"01\",\"savedUnixMs\":0,\"lines\":[{\"s\":10,\"w\":[[500,400,\"a\"]]}]}",
            out _));                                                         // a word that ends before it starts
    }

    // ── overlay ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Overlay_rebuilds_the_saved_timing_as_a_generated_doc()
    {
        var provider = Provider();
        var final = Final(provider);
        var doc = AiLyrics.Results.Overlay(Save(final), provider, Hash);

        Assert.NotNull(doc);
        Assert.Equal("t1", doc.TrackId);
        Assert.Equal(AiLyrics.ProviderId, doc.Provider);
        Assert.Equal("spotify", doc.Origin);
        Assert.Equal("en", doc.Language);
        Assert.True(doc.Generated);
        Assert.True(doc.IsSynced);
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        Assert.Equal(0L, doc.OffsetMsApplied);
        Assert.Equal(final.Lines.Count, doc.Lines.Count);
        for (int li = 0; li < final.Lines.Count; li++)
        {
            var want = final.Lines[li];
            var got = doc.Lines[li];
            Assert.Equal(want.StartMs, got.StartMs);
            Assert.Equal(want.EndMs, got.EndMs);
            Assert.Equal(want.IsWordByWord, got.IsWordByWord);
            Assert.Equal(want.Syllables.Count, got.Syllables.Count);
            for (int i = 0; i < want.Syllables.Count; i++) Assert.Equal(want.Syllables[i], got.Syllables[i]);
        }
        Assert.Same(provider.Lines[1], doc.Lines[1]);                     // no saved words: the provider's line
    }

    [Fact]
    public void Overlay_keeps_the_providers_translation_and_romanization()
    {
        var provider = Provider();
        var r = Save(Final(provider));
        // The provider's secondary lines changed since the save; the timing (and the hash the host keys it by) did not.
        var newer = provider with
        {
            Lines = provider.Lines.Select(l => l with
            {
                Translation = l.Translation is null ? null : l.Translation + " (v2)",
                Romanization = l.Romanization is null ? null : l.Romanization + " (v2)",
            }).ToList(),
        };

        var doc = AiLyrics.Results.Overlay(r, newer, Hash);

        Assert.NotNull(doc);
        for (int li = 0; li < newer.Lines.Count; li++)
        {
            Assert.Equal(newer.Lines[li].Text, doc.Lines[li].Text);
            Assert.Equal(newer.Lines[li].Translation, doc.Lines[li].Translation);
            Assert.Equal(newer.Lines[li].Romanization, doc.Lines[li].Romanization);
        }
        Assert.Equal("Niemand bidt voor mij (v2)", doc.Lines[0].Translation);
        Assert.Equal("saranghae my love (v2)", doc.Lines[3].Romanization);
        Assert.True(doc.HasTranslation);
        Assert.True(doc.HasRomanization);
    }

    [Fact]
    public void Overlay_syllables_join_back_to_the_line_text()
    {
        var provider = Provider();
        var doc = AiLyrics.Results.Overlay(Save(Final(provider)), provider, Hash);

        Assert.NotNull(doc);
        foreach (var line in doc.Lines)
        {
            if (!line.IsWordByWord) continue;
            Assert.Equal(line.Text, string.Concat(line.Syllables.Select(s => s.Text)));
        }
        Assert.Equal(["Nobody ", "pray ", "for ", "me"], doc.Lines[0].Syllables.Select(s => s.Text).ToArray());
        Assert.Equal(["(Oh) ", "yeah, ", "it's ", "'bout ", "time…"], doc.Lines[2].Syllables.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Overlay_misses_on_a_source_hash_mismatch()
    {
        var provider = Provider();
        Assert.Null(AiLyrics.Results.Overlay(Save(Final(provider)), provider, Hash ^ 1));
    }

    [Fact]
    public void Overlay_misses_on_a_line_count_mismatch()
    {
        var provider = Provider();
        var r = Save(Final(provider));
        var longer = provider with { Lines = [.. provider.Lines, new Lyrics.Line(17000, "one more line", [])] };
        var shorter = provider with { Lines = provider.Lines.Take(3).ToList() };

        Assert.Null(AiLyrics.Results.Overlay(r, longer, Hash));
        Assert.Null(AiLyrics.Results.Overlay(r, shorter, Hash));
    }

    [Fact]
    public void Overlay_misses_for_another_track_or_nothing_timed()
    {
        var provider = Provider();
        var r = Save(Final(provider));
        Assert.Null(AiLyrics.Results.Overlay(r, provider with { TrackId = "T1" }, Hash));

        var untimed = Save(provider with { Generated = true, Provider = AiLyrics.ProviderId, Origin = "spotify" });
        Assert.Null(AiLyrics.Results.Overlay(untimed, provider, Hash));
    }

    [Fact]
    public void Overlay_keeps_the_provider_line_when_its_words_no_longer_match()
    {
        var provider = Provider();
        var r = Save(Final(provider));
        var edited = provider with
        {
            Lines = [provider.Lines[0] with { Text = "Nobody pray for me now" }, .. provider.Lines.Skip(1)],
        };

        var doc = AiLyrics.Results.Overlay(r, edited, Hash);

        Assert.NotNull(doc);
        Assert.Same(edited.Lines[0], doc.Lines[0]);
        Assert.True(doc.Lines[2].IsWordByWord);
    }

    // ── file name ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void File_name_is_track_id_and_pack_version()
    {
        Assert.Equal("4uLU6hMCjMI75M1A2tKUQC.v1.json", AiLyrics.Results.FileName("4uLU6hMCjMI75M1A2tKUQC", 1));
        Assert.Equal("4uLU6hMCjMI75M1A2tKUQC.v7.json", AiLyrics.Results.FileName("4uLU6hMCjMI75M1A2tKUQC", 7));

        string local = AiLyrics.Results.FileName("spotify:local:artist:album:title:180", 1);
        Assert.EndsWith(".v1.json", local, StringComparison.Ordinal);
        Assert.Equal(64 + ".v1.json".Length, local.Length);
        Assert.DoesNotContain(":", local, StringComparison.Ordinal);
        Assert.Equal(local, AiLyrics.Results.FileName("spotify:local:artist:album:title:180", 1));
    }

    // ── sweep ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly (string Path, long Bytes, long LastWriteUnixMs)[] Four =
    [
        ("a", 100, 3),
        ("b", 100, 1),
        ("c", 100, 2),
        ("d", 100, 4),
    ];

    [Fact]
    public void Sweep_within_limits_deletes_nothing()
    {
        Assert.Empty(AiLyrics.Results.SweepVictims(Four, maxFiles: 4, maxBytes: 400));
        Assert.Empty(AiLyrics.Results.SweepVictims(Four));
        Assert.Empty(AiLyrics.Results.SweepVictims([]));
    }

    [Fact]
    public void Sweep_deletes_oldest_first_down_to_the_file_limit()
        => Assert.Equal(["b", "c"], AiLyrics.Results.SweepVictims(Four, maxFiles: 2, maxBytes: 10_000));

    [Fact]
    public void Sweep_deletes_oldest_first_down_to_the_byte_limit()
    {
        Assert.Equal(["b"], AiLyrics.Results.SweepVictims(Four, maxFiles: 10, maxBytes: 350));
        Assert.Equal(["b", "c"], AiLyrics.Results.SweepVictims(Four, maxFiles: 10, maxBytes: 250));
    }

    [Fact]
    public void Sweep_honours_both_limits_at_once()
        => Assert.Equal(["b", "c", "a"], AiLyrics.Results.SweepVictims(Four, maxFiles: 3, maxBytes: 150));

    [Fact]
    public void Sweep_breaks_ties_by_path()
    {
        (string, long, long)[] tied = [("z", 10, 5), ("m", 10, 5), ("a", 10, 9)];
        Assert.Equal(["m", "z"], AiLyrics.Results.SweepVictims(tied, maxFiles: 1, maxBytes: 10_000));
    }

    [Fact]
    public void Sweep_defaults_are_500_files_and_20_MB()
    {
        var many = new (string, long, long)[501];
        for (int i = 0; i < many.Length; i++) many[i] = ("f" + i, 1, 1000 - i);
        Assert.Equal(["f500"], AiLyrics.Results.SweepVictims(many));

        const long mb = 1024 * 1024;
        (string, long, long)[] big = [("x", 8 * mb, 2), ("y", 8 * mb, 1), ("z", 8 * mb, 3)];
        Assert.Equal(["y"], AiLyrics.Results.SweepVictims(big));
    }
}
