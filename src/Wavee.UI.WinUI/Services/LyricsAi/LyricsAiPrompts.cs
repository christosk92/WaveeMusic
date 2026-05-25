using System;
using System.Text;

namespace Wavee.UI.WinUI.Services;

internal static class LyricsAiPrompts
{
    internal const int MaxFallbackLyricsCharacters = 3200;

    // The prompt treats lyrics as primary evidence and track/background knowledge
    // as context only. Citations still point to lyric line numbers, never model
    // memory. The paragraph is emitted as the concatenation of segments[].text —
    // there is no separate top-level paragraph field, so the model never emits the
    // same text twice. citationLine on each segment is the startLine value of
    // the citation it references (or 0 for uncited bridge text).
    //
    // Output budget is tight on purpose: Phi Silica has no MaxGeneratedTokens cap,
    // so generation time scales with output length. 40-60 words + 3-4 segments +
    // 2 citations roughly halves the generated-token count vs the earlier 80-120
    // word / 4-8 segment / 2-3 citation budget.
    internal static string BuildLyricsMeaningPrompt(string numberedLyrics, string trackContext)
    {
        return
            "Interpret song lyrics using only the numbered lyrics as primary " +
            "evidence. Track context may disambiguate, never override. Do not " +
            "invent facts.\n\n" +
            "Write one paragraph: 2 to 3 sentences, 40 to 60 words, naming who " +
            "speaks, to whom, what they feel, what they want. No bullets, lists, " +
            "headings, markdown, or line breaks. Interpret non-English lyrics in " +
            "English. Paraphrase — never quote lyric text.\n\n" +
            "Split the paragraph into 3 to 4 segments. Concatenating segment.text " +
            "in order, joined by single spaces where needed, must equal the full " +
            "paragraph.\n\n" +
            "Emit exactly 2 citations. Use 1-based startLine and endLine; endLine " +
            "minus startLine must be 2 or less (1 to 3 consecutive lines). summary " +
            "is a 4-to-6-word phrase paraphrasing the cited lines without quoting.\n\n" +
            "Each cited segment sets citationLine to the matching citation's " +
            "startLine. Uncited bridge segments set citationLine to 0.\n\n" +
            trackContext +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildLyricsMeaningFallbackPrompt(string numberedLyrics, string trackContext)
    {
        return
            "Interpret these numbered lyrics in English in one short paragraph " +
            "(2 to 3 sentences, 40 to 60 words). Lyrics are the primary evidence; " +
            "track context may disambiguate only. Do not invent facts. Paraphrase " +
            "only — never quote, copy, or romanize lyric text. If there is not " +
            "enough understandable context, say so plainly.\n\n" +
            "Split the paragraph into 3 to 4 segments; concatenating segment.text " +
            "in order (joined by single spaces where needed) must equal the full " +
            "paragraph. Emit exactly 2 citations with 1-based startLine and endLine " +
            "where endLine minus startLine is 2 or less (1 to 3 consecutive lines). " +
            "summary is a 4-to-6-word phrase. Each cited segment sets citationLine " +
            "to the matching citation's startLine; uncited bridge segments set " +
            "citationLine to 0.\n\n" +
            trackContext +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildLyricsMeaningPlainTextPrompt(string numberedLyrics, string trackContext)
    {
        return
            "Interpret song lyrics with the numbered lyrics as primary evidence. " +
            "Use trained music-domain knowledge about the artist, genre, song, idioms, " +
            "references, and cultural context when it helps, but never override the lyrics. " +
            "Do not invent facts.\n\n" +
            "Write one tight paragraph in English: 2 sentences, 35 to 55 words, naming " +
            "who speaks, to whom, what they feel, and what they want. No bullets, lists, " +
            "headings, markdown, line breaks, citations, or quoted lyric text. Paraphrase only.\n\n" +
            trackContext +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildLyricsMeaningPlainTextFallbackPrompt(string numberedLyrics, string trackContext)
    {
        return
            "Interpret these numbered lyrics in English in one concise paragraph " +
            "(2 sentences, 35 to 50 words). Lyrics are the primary evidence; trained " +
            "music-domain knowledge and track context may disambiguate only. Do not invent facts, quote lyrics, use " +
            "markdown, or add line breaks. If context is too thin, say so plainly.\n\n" +
            trackContext +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildTrackContext(string? trackTitle, string? artistName)
    {
        trackTitle = NormalizePromptMetadata(trackTitle);
        artistName = NormalizePromptMetadata(artistName);

        if (string.IsNullOrWhiteSpace(trackTitle) && string.IsNullOrWhiteSpace(artistName))
            return string.Empty;

        var sb = new StringBuilder("TRACK CONTEXT:\n");
        if (!string.IsNullOrWhiteSpace(trackTitle))
            sb.Append("Title: ").AppendLine(trackTitle);
        if (!string.IsNullOrWhiteSpace(artistName))
            sb.Append("Artist: ").AppendLine(artistName);
        sb.AppendLine();
        return sb.ToString();
    }

    internal static NumberedLyricsContext BuildNumberedLyricsContext(string fullLyric)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return new NumberedLyricsContext(string.Empty, 0);

        var lines = fullLyric
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var sb = new StringBuilder(fullLyric.Length + lines.Length * 4);
        var lineNumber = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            lineNumber++;
            sb.Append(lineNumber).Append(". ").AppendLine(line);
        }

        return new NumberedLyricsContext(sb.ToString().TrimEnd(), lineNumber);
    }

    internal static string TrimLyricsForFallback(string fullLyric)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return string.Empty;

        var normalized = fullLyric.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= MaxFallbackLyricsCharacters)
            return normalized;

        var headLength = (int)(MaxFallbackLyricsCharacters * 0.65);
        var tailLength = MaxFallbackLyricsCharacters - headLength;
        return normalized[..headLength].TrimEnd() +
               "\n...\n" +
               normalized[^tailLength..].TrimStart();
    }

    private static string NormalizePromptMetadata(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
}

internal readonly record struct NumberedLyricsContext(string Text, int LineCount);
