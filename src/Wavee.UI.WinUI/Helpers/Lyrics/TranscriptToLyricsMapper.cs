using System.Collections.Generic;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Core.Http.Transcripts;

namespace Wavee.UI.WinUI.Helpers.Lyrics;

/// <summary>
/// Maps Spotify's podcast read-along transcript wire shape into the existing
/// lyrics renderer model. A transcript's per-syllable <c>highlight[]</c> is
/// structurally identical to syllable-synced lyrics, so the same canvas /
/// breathing renderer pipeline drives both with zero code changes — the only
/// new model piece is <see cref="LyricsLine.IsSectionHeader"/> for chapter
/// titles, which the renderer typesets distinctly.
/// </summary>
internal static class TranscriptToLyricsMapper
{
    /// <summary>
    /// Materialise a <see cref="LyricsData"/> instance from a deserialised
    /// transcript response. Sentence lines carry per-syllable timings;
    /// chapter-title sections become standalone header lines (no syllables).
    /// EndMs on each line is computed lazily from the next line's StartMs so
    /// the renderer's per-line crossfade has correct boundaries.
    /// </summary>
    public static LyricsData ToLyricsData(TranscriptResponse response)
    {
        var lines = new List<LyricsLine>(response.Sections.Count);

        foreach (var section in response.Sections)
        {
            var titleText = section.Title?.Value;
            if (!string.IsNullOrWhiteSpace(titleText))
            {
                lines.Add(new LyricsLine
                {
                    StartMs = section.StartMs,
                    PrimaryText = titleText!.Trim(),
                    IsSectionHeader = true,
                    IsPrimaryHasRealSyllableInfo = false,
                });
            }

            if (section.Text is not { Sentence: { } sentence } || string.IsNullOrEmpty(sentence.Text))
                continue;

            var syllables = MapHighlights(sentence);

            lines.Add(new LyricsLine
            {
                StartMs = sentence.StartMs,
                PrimaryText = sentence.Text,
                PrimarySyllables = syllables,
                IsPrimaryHasRealSyllableInfo = syllables.Count > 0,
            });
        }

        // Populate EndMs from the next entry's StartMs. The last line keeps
        // EndMs = null so the renderer holds it through the end of audio.
        for (var i = 0; i < lines.Count - 1; i++)
            lines[i].EndMs = lines[i + 1].StartMs;

        return new LyricsData(lines)
        {
            LanguageCode = string.IsNullOrEmpty(response.Language) ? null : response.Language,
        };
    }

    /// <summary>
    /// Walk the highlight runs against the sentence text, producing one
    /// <see cref="BaseLyrics"/> per highlight that records its start position
    /// inside the sentence and the time window it occupies. Each highlight's
    /// EndMs comes from the next highlight's StartMs; the final one inherits
    /// the parent sentence's expected end (left null, the renderer derives it
    /// from the next line).
    /// </summary>
    private static List<BaseLyrics> MapHighlights(TranscriptSentence sentence)
    {
        var result = new List<BaseLyrics>(sentence.Highlight.Count);
        if (sentence.Highlight.Count == 0 || string.IsNullOrEmpty(sentence.Text))
            return result;

        var cursor = 0;
        for (var i = 0; i < sentence.Highlight.Count; i++)
        {
            var h = sentence.Highlight[i];
            if (h.NumChars <= 0) continue;

            // Clamp: occasional wire payloads have a stray highlight whose
            // cumulative numChars overshoots the sentence text by 1–2 chars.
            // Take what fits and stop walking.
            if (cursor >= sentence.Text.Length) break;
            var take = h.NumChars;
            if (cursor + take > sentence.Text.Length)
                take = sentence.Text.Length - cursor;

            var endMs = i + 1 < sentence.Highlight.Count
                ? sentence.Highlight[i + 1].StartMs
                : (int?)null;

            result.Add(new BaseLyrics
            {
                StartIndex = cursor,
                StartMs = h.StartMs,
                EndMs = endMs,
                Text = sentence.Text.Substring(cursor, take),
            });

            cursor += take;
        }

        return result;
    }
}
