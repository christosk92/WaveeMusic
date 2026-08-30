using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// The evidence bundle: the inspector's "Save bundle…" and the WAVEE_LYRICS_DUMP auto-capture both land here. What these
// tests pin is that the bundle is USABLE as evidence — the payloads come back out byte-for-byte (a diff or a replay
// through the parser is worthless otherwise), the losing candidates are written too, and a failure to write can never
// escape into a lyrics fetch. Every test injects its own temp root; the real %LOCALAPPDATA% is never touched.
public class LyricsInspectionExportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-lyrics-export-tests", Guid.NewGuid().ToString("n"));

    public LyricsInspectionExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    const string RichsyncBody = "[{\"ts\":27.98,\"te\":28.329,\"x\":\"and all heads turned\",\"l\":[" +
        "{\"c\":\"and \",\"o\":0.0},{\"c\":\"all \",\"o\":0.025},{\"c\":\"heads \",\"o\":0.067},{\"c\":\"turned\",\"o\":0.117}]}]";

    static LyricsDocument Doc(string provider, LyricsSyncKind sync, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text, Array.Empty<LyricSyllable>(), l.ms + 3000)).ToList(),
            sync, provider);

    static LyricsInspection Inspection() => new(
        "t1", 1753000000000L, "complete",
        new[]
        {
            new LyricsRawPayload("musixmatch", "https://apic-desktop.musixmatch.com/…?usertoken=***redacted***", "json", RichsyncBody, RichsyncBody.Length),
            new LyricsRawPayload("lrclib", "https://lrclib.net/api/get?…", "json", "{\"syncedLyrics\":\"[00:01.00]hi\"}", 31),
        },
        new[]
        {
            new LyricsParsedCandidate("musixmatch", MatchBasis.Isrc, 0.7, Doc("musixmatch", LyricsSyncKind.Syllable, (1000, "and all heads turned"))),
            new LyricsParsedCandidate("lrclib", MatchBasis.MetadataSearch, 0.45, Doc("lrclib", LyricsSyncKind.Line, (1000, "and all heads turned"))),
        },
        Doc("musixmatch", LyricsSyncKind.Line, (1000, "and all heads turned")));

    static LyricsSearchReport Report() => new(
        "t1", "Caribbean Queen", "Billy Ocean", "The Very Best of Billy Ocean", 244426L, "GBAHK9700109",
        1753000000000L, "3/4 returned; winner=musixmatch",
        new[]
        {
            new LyricsSourceTrace("musixmatch", LyricsOutcome.Hit, 1053, "Syllable, 39 lines", LyricsSyncKind.Syllable, 39, 0.948, true, "ref-align lcs=39/39"),
            new LyricsSourceTrace("lrclib", LyricsOutcome.Hit, 84, "exact /api/get hit", LyricsSyncKind.Line, 49, 0.750, false, "ref-align lcs=48/49"),
            new LyricsSourceTrace("amll", LyricsOutcome.Miss, 201, "no match", LyricsSyncKind.None, 0, 0d, false, ""),
        });

    [Fact]
    public void WriteBundle_RoundTripsEveryPayloadByteForByte()
    {
        string? folder = LyricsInspectionExport.WriteBundle("t1", Report(), Inspection(), _dir, stamp: "stamp");

        Assert.NotNull(folder);
        // BYTES, not text. File.ReadAllText silently swallows a UTF-8 BOM, so a text comparison passes even when the
        // export has prepended three bytes to every payload — which really happened, and which breaks a diff, a
        // checksum, and any strict JSON reader. Only this assertion catches it.
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(RichsyncBody),
            File.ReadAllBytes(Path.Combine(folder!, "raw-musixmatch-1.json")));
        Assert.True(File.Exists(Path.Combine(folder!, "raw-lrclib-1.json")));
    }

    [Fact]
    public void WriteBundle_WritesNoByteOrderMarkAnywhere()
    {
        string? folder = LyricsInspectionExport.WriteBundle("t1", Report(), Inspection(), _dir, stamp: "stamp");

        foreach (string file in Directory.EnumerateFiles(folder!))
        {
            byte[] head = File.ReadAllBytes(file);
            Assert.False(head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
                $"{Path.GetFileName(file)} starts with a UTF-8 BOM");
        }
    }

    [Fact]
    public void WriteBundle_KeepsTheLOSINGCandidatesParse()
    {
        string? folder = LyricsInspectionExport.WriteBundle("t1", Report(), Inspection(), _dir, stamp: "stamp");

        // lrclib lost the rerank; its parse is exactly what a comparison needs, so it must not be dropped.
        Assert.True(File.Exists(Path.Combine(folder!, "parsed-lrclib.tsv")));
        Assert.True(File.Exists(Path.Combine(folder!, "parsed-musixmatch.tsv")));
        Assert.True(File.Exists(Path.Combine(folder!, "parsed-final.tsv")));
        Assert.True(File.Exists(Path.Combine(folder!, "report.txt")));
    }

    [Fact]
    public void WriteBundle_ReportCarriesTheWhyNotForEverySource()
    {
        string report = LyricsInspectionExport.BuildReport("t1", Report(), Inspection());

        Assert.Contains("★ CHOSEN", report);
        Assert.Contains("lost the rerank: score 0.75 against the winner's 0.95", report);
        Assert.Contains("the provider had nothing for this track", report);
        Assert.Contains("usertoken=***redacted***", report);   // the credential never reaches a bug report
    }

    [Fact]
    public void BuildParsed_CarriesTheDerivedColumnsAnInvestigationNeeds()
    {
        var doc = LyricsWordFormats.ParseRichsync(RichsyncBody, "t1");
        string tsv = LyricsInspectionExport.BuildParsed("musixmatch", doc);

        Assert.Contains("idx\tstart\tend\tdurMs\tgapToNextMs\twordsPerSec\ttext", tsv);
        Assert.Contains("words/second", tsv);   // 4 words in 349ms — the timing verdict rides along with the data
    }

    [Fact]
    public void WriteBundle_NothingToSave_IsNullNotAnEmptyFolder()
        => Assert.Null(LyricsInspectionExport.WriteBundle("t1", null, null, _dir, stamp: "stamp"));

    [Fact]
    public void WriteBundle_UnwritableRoot_DoesNotThrow()
    {
        // A diagnostics write must never be able to fail a lyrics fetch (the auto-capture path runs inside one).
        string bad = Path.Combine(_dir, "file-not-a-directory");
        File.WriteAllText(bad, "x");
        Assert.Null(LyricsInspectionExport.WriteBundle("t1", Report(), Inspection(), bad, stamp: "stamp"));
    }
}
