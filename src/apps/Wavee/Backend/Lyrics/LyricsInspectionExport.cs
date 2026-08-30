using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>Turns one <see cref="LyricsInspection"/> into text, and onto disk.
///
/// <para>The inspector dialog answers "is this desync the provider's or ours?" for the track in front of you. This is the
/// other half: getting the evidence OUT — a folder holding every provider payload byte-for-byte, every candidate's parse
/// as a TSV, and the report. Copying a 36KB payload out of a 548-DIP dialog is not an investigation; a folder you can
/// diff, grep, replay through the parser in a unit test, or attach to a bug report is.</para>
///
/// <para>Two callers: the inspector's "Save bundle" button (this track, now) and the <c>WAVEE_LYRICS_DUMP=1</c>
/// auto-capture in <see cref="AggregatingLyricsProvider"/>, which writes a bundle for every track whose word timing
/// trips the gate. The second is what turns "this one song is broken" into "N of M richsync bodies are broken" without
/// anyone having to sit and reproduce it.</para>
/// </summary>
public static class LyricsInspectionExport
{
    /// <summary>%LOCALAPPDATA%\Wavee\diag\lyrics — beside the existing diag\ folder, not inside the lyrics CACHE (this is
    /// evidence, and a cache sweep must never delete it).</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "diag", "lyrics");

    /// <summary>Write the whole bundle and return the folder. Never throws — an export failure must not take down the
    /// dialog or, worse, a lyrics fetch. Returns null when there was nothing to write.</summary>
    public static string? WriteBundle(
        string trackId,
        LyricsSearchReport? report,
        LyricsInspection? inspection,
        string? rootDirectory = null,
        string? stamp = null)
    {
        if (inspection is null && report is null) return null;
        try
        {
            string root = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultDirectory() : rootDirectory!;
            string folder = Path.Combine(root, Safe(trackId) + "-" + (stamp ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "report.txt"), BuildReport(trackId, report, inspection), Utf8NoBom);

            if (inspection is not null)
            {
                // Payloads keep their real extension so an editor syntax-highlights them and a diff is readable. The
                // per-source counter keeps a query ladder's several responses distinct and in capture order.
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in inspection.Raw)
                {
                    seen.TryGetValue(p.SourceId, out int n);
                    seen[p.SourceId] = ++n;
                    string name = $"raw-{Safe(p.SourceId)}-{n}{Extension(p.Format)}";
                    File.WriteAllText(Path.Combine(folder, name), p.Text, Utf8NoBom);
                }

                foreach (var c in inspection.Candidates)
                    File.WriteAllText(Path.Combine(folder, $"parsed-{Safe(c.SourceId)}.tsv"),
                        BuildParsed(c.SourceId, c.Document), Utf8NoBom);

                if (inspection.Final is { } final)
                    File.WriteAllText(Path.Combine(folder, "parsed-final.tsv"), BuildParsed("final", final), Utf8NoBom);
            }
            return folder;
        }
        catch { return null; }
    }

    /// <summary>UTF-8 with NO byte-order mark. File.WriteAllText's default Encoding.UTF8 emits one, which prepends three
    /// bytes to every captured payload — enough to break a byte-for-byte diff, a checksum, and a strict JSON reader
    /// (System.Text.Json and Python's json both reject a leading BOM). A round-trip through File.ReadAllText hides it,
    /// so only a BYTE comparison catches this; see LyricsInspectionExportTests.</summary>
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    static string Extension(string format) => format switch
    {
        "json" => ".json",
        "ttml" => ".ttml",
        "xml" => ".xml",
        "lrc" => ".lrc",
        _ => ".txt",       // krc / qrc / yrc — decrypted plain text with no editor association
    };

    static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    // ── text ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string BuildParsed(string who, LyricsDocument doc)
    {
        var sb = new StringBuilder(doc.Lines.Count * 48);
        sb.Append("# parsed lyrics — ").Append(who).Append('\n');
        sb.Append("provider:  ").Append(Or(doc.Provider, who)).Append('\n');
        sb.Append("track:     ").Append(doc.TrackId).Append('\n');
        sb.Append("sync:      ").Append(doc.Sync).Append("   isSynced=").Append(doc.IsSynced)
          .Append("   lines=").Append(doc.Lines.Count).Append("   syllables=").Append(SyllableCount(doc))
          .Append("   offsetApplied=").Append(doc.OffsetMsApplied).Append("ms\n");
        sb.Append("timing:    ").Append(LyricsTiming.Describe(doc)).Append("\n\n");
        sb.Append("idx\tstart\tend\tdurMs\tgapToNextMs\twordsPerSec\ttext\n");

        for (int i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            long gap = i + 1 < doc.Lines.Count ? doc.Lines[i + 1].StartMs - l.StartMs : 0;
            long span = LyricsTiming.WordSpanMs(l);
            int words = LyricsTiming.WordCount(l.Text);
            sb.Append(i).Append('\t').Append(Ts(l.StartMs)).Append('\t')
              .Append(l.EndMs is { } e ? Ts(e) : "-").Append('\t')
              .Append(l.EndMs is { } e2 ? (e2 - l.StartMs).ToString() : "-").Append('\t')
              .Append(gap > 0 ? gap.ToString() : "-").Append('\t')
              .Append(span > 0 ? (words * 1000d / span).ToString("F1") : "-").Append('\t')
              .Append(l.Text);
            if (l.Translation is { Length: > 0 } tr) sb.Append("\t[tr] ").Append(tr);
            if (l.Romanization is { Length: > 0 } ro) sb.Append("\t[ro] ").Append(ro);
            sb.Append('\n');
            foreach (var s in l.Syllables)
                sb.Append("\t\t").Append(s.StartMs).Append('-').Append(s.EndMs).Append('\t').Append(s.Text).Append('\n');
        }
        return sb.ToString();
    }

    public static string BuildReport(string trackId, LyricsSearchReport? r, LyricsInspection? insp)
    {
        var sb = new StringBuilder(4096);
        sb.Append("# Wavee lyrics source report\n");
        sb.Append("track:   ").Append(trackId).Append('\n');
        if (r is not null)
        {
            sb.Append("title:   ").Append(Or(r.Title, "-")).Append("  —  ").Append(Or(r.Artist, "-")).Append('\n');
            sb.Append("album:   ").Append(Or(r.Album, "-")).Append("   duration=").Append(r.DurationMs)
              .Append("ms   isrc=").Append(Or(r.Isrc, "-")).Append('\n');
            sb.Append("summary: ").Append(r.Summary).Append('\n');
        }
        else sb.Append("summary: (no search recorded)\n");
        if (insp is not null) sb.Append("note:    ").Append(insp.Note).Append('\n');
        sb.Append('\n');

        double winnerScore = 0d;
        if (r is not null) foreach (var t in r.Sources) if (t.Winner) winnerScore = t.Score;

        sb.Append("## providers\n");
        if (r is null || r.Sources.Count == 0) sb.Append("(none)\n");
        else
            foreach (var t in r.Sources)
            {
                sb.Append("- ").Append(t.SourceId).Append("  ").Append(t.Outcome).Append("  ").Append(t.ElapsedMs).Append("ms")
                  .Append("  sync=").Append(t.Sync).Append("  lines=").Append(t.LineCount)
                  .Append("  score=").Append(t.Score.ToString("F3")).Append('\n');
                sb.Append("    verdict: ").Append(Verdict(t, winnerScore)).Append('\n');
                // The breakdown, not just the total: "0.885 vs 0.740" says who won, "sync 0.60 vs 1.00" says why.
                if (t.Score > 0d)
                    sb.Append("    score:   ").Append(t.Score.ToString("F3"))
                      .Append("  =  text ").Append(t.Text.ToString("F2"))
                      .Append(" × .40  +  sync ").Append(t.SyncScore.ToString("F2"))
                      .Append(" × .25  +  timing ").Append(t.Timing.ToString("F2"))
                      .Append(" × .20  +  coverage ").Append(t.Coverage.ToString("F2"))
                      .Append(" × .10  +  prior × .05\n");
                if (t.Detail.Length > 0) sb.Append("    detail:  ").Append(t.Detail).Append('\n');
            }

        sb.Append("\n## captured payloads\n");
        if (insp is null || insp.Raw.Count == 0)
            sb.Append("(none — the answer came from a cache, so no provider was contacted)\n");
        else
            foreach (var p in insp.Raw)
                sb.Append("- ").Append(p.SourceId).Append("  ").Append(p.Format).Append("  ")
                  .Append(p.OriginalLength).Append(" chars").Append(p.Truncated ? " (truncated)" : "")
                  .Append("  ").Append(p.Label).Append('\n');

        sb.Append("\n## parsed candidates\n");
        if (insp is null || insp.Candidates.Count == 0) sb.Append("(none)\n");
        else
            foreach (var c in insp.Candidates)
                sb.Append("- ").Append(c.SourceId).Append("  ").Append(c.Document.Sync).Append("  ")
                  .Append(c.Document.Lines.Count).Append(" lines  basis=").Append(c.Basis)
                  .Append("\n    timing: ").Append(LyricsTiming.Describe(c.Document)).Append('\n');

        if (insp?.Final is { } final) sb.Append('\n').Append(BuildParsed("final", final));
        return sb.ToString();
    }

    /// <summary>Why this provider is (not) the one you are listening to — the one line the whole report exists for.</summary>
    public static string Verdict(LyricsSourceTrace t, double winnerScore)
    {
        if (t.Winner)
            return $"★ CHOSEN — reranker score {t.Score:F2}" + (t.RerankReason.Length > 0 ? $" ({t.RerankReason})" : "");

        return t.Outcome switch
        {
            LyricsOutcome.Hit =>
                $"not chosen — it returned lyrics but lost the rerank: score {t.Score:F2} against the winner's {winnerScore:F2}"
                + (t.RerankReason.Length > 0 ? $" ({t.RerankReason})" : ""),
            LyricsOutcome.Miss => "not chosen — the provider had nothing for this track",
            LyricsOutcome.Timeout => "not chosen — it did not answer inside the per-source budget",
            LyricsOutcome.Error => "not chosen — the request failed",
            LyricsOutcome.Skipped => "not chosen — it never ran to completion (a faster match closed the window)",
            _ => "not chosen",
        };
    }

    public static string Ts(long ms)
    {
        if (ms < 0) ms = 0;
        long m = ms / 60000, rest = ms % 60000;
        return $"{m:00}:{rest / 1000:00}.{rest % 1000:000}";
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value!;
}
