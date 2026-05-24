using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Wavee.UI.WinUI.Services;

internal static class LyricsAiEvidenceParser
{
    // The paragraph is reconstructed from segments[].text by the parser, so the
    // model never emits it twice. `citationLine` references a citation by its
    // startLine value (the natural anchor) — Phi Silica conflated arbitrary `id`
    // fields with line numbers in practice, so we go with what the model
    // actually does and drop the extra indirection.
    internal const string LyricsMeaningEvidenceJsonSchema = """
    {
      "type": "object",
      "properties": {
        "segments": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "text": { "type": "string" },
              "citationLine": { "type": "integer" }
            },
            "required": [ "text", "citationLine" ]
          }
        },
        "citations": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "startLine": { "type": "integer" },
              "endLine": { "type": "integer" },
              "summary": { "type": "string" }
            },
            "required": [ "startLine", "endLine", "summary" ]
          }
        }
      },
      "required": [ "segments", "citations" ]
    }
    """;

    /// <summary>
    /// Entry point for whole-song meaning success cases. Parses citation metadata
    /// out of the raw structured-output JSON, reconstructs the paragraph from the
    /// segments, and produces a <see cref="LyricsAiResult"/> with citations.
    /// Degrades gracefully: if segments parse but no citation passes validation,
    /// returns the paragraph as plain text. Only fully unparseable payloads (no
    /// segments) become <see cref="LyricsAiResult.Error"/>.
    /// </summary>
    internal static LyricsAiResult BuildLyricsMeaningSuccessResult(
        PhiSilicaStructuredGenerationResult response,
        string extractedText,
        int lyricLineCount)
    {
        // extractedText is the pipeline's top-level `text` extraction; for the
        // citation schema there is no such property, so this is always empty —
        // we reconstruct the paragraph from segments instead.
        _ = extractedText;

        if (!TryParseLyricsMeaningEvidence(
                response.RawResponseText,
                lyricLineCount,
                out var segments,
                out var citations))
        {
            return LyricsAiResult.Error("Phi Silica returned an unparseable lyrics meaning.");
        }

        var reconstructedText = ReconstructTextFromSegments(segments);
        if (string.IsNullOrWhiteSpace(reconstructedText))
            return LyricsAiResult.Error("Phi Silica returned no usable lyrics meaning text.");

        // Segments OK but every citation failed validation → render the paragraph
        // without underlines rather than throwing the whole result away.
        return citations.Count == 0
            ? LyricsAiResult.Ok(reconstructedText, fromCache: false)
            : LyricsAiResult.Ok(reconstructedText, fromCache: false, segments, citations);
    }

    // Internal (not private) so Wavee.Tests can exercise the validation rules
    // directly — Wavee.UI.WinUI has InternalsVisibleTo Wavee.Tests already.
    internal static bool TryParseLyricsMeaningEvidence(
        string? json,
        int lyricLineCount,
        out IReadOnlyList<LyricsAiTextSegment> segments,
        out IReadOnlyList<LyricsAiCitation> citations)
    {
        segments = Array.Empty<LyricsAiTextSegment>();
        citations = Array.Empty<LyricsAiCitation>();

        if (string.IsNullOrWhiteSpace(json) || lyricLineCount <= 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("segments", out var segmentsElement)
                || segmentsElement.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("citations", out var citationsElement)
                || citationsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            // The CitationId stored on LyricsAiTextSegment is the citation's
            // startLine value — that's the natural anchor the model emits in
            // `citationLine`. We also accept the older `citationId` property
            // name in case the model emits that instead.
            var rawSegments = new List<LyricsAiTextSegment>();
            foreach (var segmentElement in segmentsElement.EnumerateArray())
            {
                if (segmentElement.ValueKind != JsonValueKind.Object
                    || !TryGetStringProperty(segmentElement, "text", out var segmentText))
                {
                    return false;
                }

                if (!TryGetInt32Property(segmentElement, "citationLine", out var citationLine)
                    && !TryGetInt32Property(segmentElement, "citationId", out citationLine))
                {
                    citationLine = 0;
                }

                rawSegments.Add(new LyricsAiTextSegment(segmentText, Math.Max(0, citationLine)));
            }

            if (rawSegments.Count == 0)
                return false;

            // Citations get sequential ids (1..N) for internal lookup. The model
            // doesn't reliably emit unique ids, so we own that ourselves. Segments
            // reference citations by `citationLine` — a lyric line number — and
            // we resolve that against citations via exact-startLine-match first,
            // then narrowest-containing-range fallback (Phi Silica often emits
            // broad citation ranges and segment.citationLine inside the range).
            var validCitations = new List<LyricsAiCitation>();
            var nextCitationId = 1;
            foreach (var citationElement in citationsElement.EnumerateArray())
            {
                if (citationElement.ValueKind != JsonValueKind.Object
                    || !TryGetInt32Property(citationElement, "startLine", out var startLine)
                    || !TryGetInt32Property(citationElement, "endLine", out var endLine)
                    || !TryGetStringProperty(citationElement, "summary", out var summary))
                {
                    continue;
                }

                if (startLine <= 0
                    || endLine < startLine
                    || startLine > lyricLineCount
                    || endLine > lyricLineCount)
                {
                    continue;
                }

                summary = NormalizeCitationSummary(summary);
                if (summary.Length == 0)
                    continue;

                validCitations.Add(new LyricsAiCitation(nextCitationId++, startLine, endLine, summary));
            }

            // No valid citations is a soft failure: caller falls back to plain
            // text rendering with the reconstructed paragraph. We still set
            // segments so the caller can call ReconstructTextFromSegments.
            if (validCitations.Count == 0)
            {
                segments = StripCitationIds(rawSegments);
                citations = Array.Empty<LyricsAiCitation>();
                return true;
            }

            var filteredSegments = new List<LyricsAiTextSegment>(rawSegments.Count);
            var orderedCitations = new List<LyricsAiCitation>();
            var usedCitationIds = new HashSet<int>();
            for (var i = 0; i < rawSegments.Count; i++)
            {
                var segment = rawSegments[i];
                var bestCitation = segment.CitationId > 0
                    ? FindBestCitationMatch(segment.CitationId, validCitations)
                    : null;

                if (bestCitation is { } citation)
                {
                    filteredSegments.Add(segment with { CitationId = citation.Id });
                    if (usedCitationIds.Add(citation.Id))
                        orderedCitations.Add(citation);
                }
                else
                {
                    filteredSegments.Add(segment with { CitationId = 0 });
                }
            }

            if (orderedCitations.Count == 0)
            {
                segments = StripCitationIds(rawSegments);
                citations = Array.Empty<LyricsAiCitation>();
                return true;
            }

            segments = filteredSegments;
            citations = orderedCitations;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt32Property(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt32(out value);

        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), out value);
    }

    // Summaries are flyout text shown on hover/tap. Short phrases (5-8 words,
    // ~60 chars) read cleanly there; anything longer becomes scrolling prose
    // and defeats the "quick evidence glance" UX. The prompt asks for 5-8 words
    // explicitly; this clamp is the backstop if the model overruns.
    private static string NormalizeCitationSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        var lines = summary
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return PhiSilicaStructuredTextPipeline.ClampLength(string.Join(" ", lines), 60);
    }

    // Maps a lyric line number (segment.citationLine, stored on the raw
    // LyricsAiTextSegment.CitationId) to the most specific matching citation:
    //   1. Exact startLine match wins (the natural anchor).
    //   2. Otherwise the narrowest [startLine, endLine] range that contains
    //      the line wins. The model often emits broad ranges (e.g. 19–41) and
    //      then references specific lines inside (citationLine: 41); without
    //      this fallback most references go uncited even though the citation
    //      clearly covers them.
    //   3. Tie-break on smaller startLine (earliest citation) for stability.
    private static LyricsAiCitation? FindBestCitationMatch(
        int citationLine,
        IReadOnlyList<LyricsAiCitation> citations)
    {
        for (var i = 0; i < citations.Count; i++)
        {
            if (citations[i].StartLine == citationLine)
                return citations[i];
        }

        LyricsAiCitation? best = null;
        var bestRange = int.MaxValue;
        var bestStartLine = int.MaxValue;
        for (var i = 0; i < citations.Count; i++)
        {
            var c = citations[i];
            if (citationLine < c.StartLine || citationLine > c.EndLine)
                continue;

            var range = c.EndLine - c.StartLine;
            if (range < bestRange || (range == bestRange && c.StartLine < bestStartLine))
            {
                best = c;
                bestRange = range;
                bestStartLine = c.StartLine;
            }
        }

        return best;
    }

    // Zeros out citationId on every segment. Used when citations all failed
    // validation but the segments themselves are usable — the renderer must not
    // try to look up citations that don't exist in the citation map.
    private static IReadOnlyList<LyricsAiTextSegment> StripCitationIds(
        IReadOnlyList<LyricsAiTextSegment> segments)
    {
        var result = new List<LyricsAiTextSegment>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
            result.Add(segments[i] with { CitationId = 0 });
        return result;
    }

    // Joins all segment text into the user-facing paragraph. The model often
    // emits self-contained segments that end mid-sentence ("...consciousness.")
    // and start mid-sentence ("Experiencing...") without any whitespace at the
    // boundary, so we insert a single space whenever a segment join would
    // otherwise glue two non-whitespace characters together. We also collapse
    // any internal whitespace runs to single spaces for a clean paragraph.
    private static string ReconstructTextFromSegments(IReadOnlyList<LyricsAiTextSegment> segments)
    {
        if (segments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var text = segments[i].Text;
            if (text.Length == 0)
                continue;

            if (sb.Length > 0
                && !char.IsWhiteSpace(sb[^1])
                && !char.IsWhiteSpace(text[0]))
            {
                sb.Append(' ');
            }

            sb.Append(text);
        }

        // Collapse runs of whitespace (including any newlines the model emitted)
        // into single spaces and trim outer whitespace.
        var raw = sb.ToString();
        var collapsed = new StringBuilder(raw.Length);
        var prevWhitespace = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsWhiteSpace(c))
            {
                if (!prevWhitespace && collapsed.Length > 0)
                    collapsed.Append(' ');
                prevWhitespace = true;
            }
            else
            {
                collapsed.Append(c);
                prevWhitespace = false;
            }
        }

        return collapsed.ToString().Trim();
    }
}
