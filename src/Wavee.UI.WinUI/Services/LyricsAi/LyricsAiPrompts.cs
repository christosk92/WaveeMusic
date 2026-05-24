using System;
using System.Text;

namespace Wavee.UI.WinUI.Services;

internal static class LyricsAiPrompts
{
    internal const int MaxFallbackLyricsCharacters = 3200;

    // The explain path uses the default text+disposition schema, so the
    // per-prompt builders here own the "put prose in text, set disposition"
    // hint that used to live in PhiSilicaStructuredTextGenerator.BuildStructuredPrompt.
    private const string TextAndDispositionHint =
        " Put the final user-facing prose in the text property. Use disposition=\"clear\" " +
        "for a normal answer, \"ambiguous\" when the supplied evidence is unclear, and " +
        "\"insufficient_context\" when there is not enough supplied evidence.";

    // Title/artist are used only by whole-song meaning; per-line explanations
    // stay lyric-only so a single short line is not overfit to track metadata.
    internal static string BuildExplainPrompt(string line, int lineIndex, string? fullLyric)
    {
        var markedLyrics = BuildMarkedLyricsContext(fullLyric, lineIndex, line);

        return
            "You interpret song lyrics as evidence. Read only the lyrics provided — " +
            "do not use outside knowledge of any song, artist, or title.\n\n" +
            "Explain the marked lyric line " +
            "(between >>> and <<<) in 2 to 4 plain sentences. Connect it to other parts " +
            "of the lyrics when the connection is supported. Name the speaker/addressee " +
            "dynamic, emotion, image, wordplay, or conflict, but do not summarize the " +
            "whole song. Do not use bullets, headings, markdown, or generic phrases like " +
            "\"emotional depth\". For Korean or other non-English lyrics, interpret the " +
            "meaning in English when possible. Do not quote or repeat lyric text verbatim; " +
            "paraphrase instead. If the marked line is too short or ambiguous even with " +
            "context, say that plainly." +
            TextAndDispositionHint + "\n\n" +
            "EXAMPLE\n" +
            "LYRICS:\n" +
            "i wake up and i still feel tired\n" +
            ">>> the kind of tired sleep cannot fix <<<\n" +
            "i wonder if i am hard to love\n" +
            "The speaker describes an exhaustion that is emotional, not physical — sleep " +
            "does not reach it. Following the line about being hard to love, this points " +
            "to depression and self-doubt rather than a busy schedule. The marked line " +
            "names that distinction in one phrase.\n\n" +
            "LYRICS:\n" +
            markedLyrics;
    }

    internal static string BuildExplainFallbackPrompt(string line, int lineIndex, string? fullLyric)
    {
        var markedLyrics = BuildNearbyLyricsContext(fullLyric, lineIndex, line);

        return
            "Explain the marked song lyric line in English in 1 to 2 plain sentences. " +
            "Use only the lyrics shown here. The lyrics may be Korean or another " +
            "non-English language. Do not quote, copy, romanize, or repeat any lyric text; " +
            "paraphrase only. If the meaning is unclear, say so plainly." +
            TextAndDispositionHint + "\n\n" +
            "LYRICS:\n" +
            markedLyrics;
    }

    // The prompt treats lyrics as primary evidence and track/background knowledge
    // as context only. Citations still point to lyric line numbers, never model
    // memory. The paragraph is emitted as the concatenation of segments[].text —
    // there is no separate top-level paragraph field, so the model never emits the
    // same text twice. citationLine on each segment is the startLine value of
    // the citation it references (or 0 for uncited bridge text).
    internal static string BuildLyricsMeaningPrompt(string numberedLyrics, string trackContext)
    {
        return
            "You interpret song lyrics as evidence. Read only the lyrics provided - " +
            "the numbered lyrics are the primary evidence. You may use the supplied " +
            "track context and any well-known background you already know about the " +
            "track only when it helps disambiguate the lyrics. Do not invent facts, " +
            "and do not let background override the lyrics.\n\n" +
            "Compose one paragraph (3 to 5 sentences, around 80 to 120 words) " +
            "describing who is speaking, to whom, what they feel, and what they want, " +
            "using only the lyrics as evidence. Do not use bullets, numbered lists, " +
            "headings, markdown, or line breaks. For Korean or other non-English " +
            "lyrics, interpret the meaning in English when possible. Do not quote or " +
            "repeat lyric text verbatim; paraphrase instead.\n\n" +
            "Split the paragraph into 4 to 8 segments. Each segment.text is a " +
            "contiguous slice of the paragraph; concatenating every segment.text " +
            "in order, joined by a single space where needed, produces the full " +
            "paragraph. Aim for shorter cited segments (5 to 20 words each) " +
            "surrounded by uncited bridge segments.\n\n" +
            "Create 2 to 3 citations. Each citation uses 1-based startLine and " +
            "endLine values from the numbered lyrics below. The citation must be " +
            "narrow: endLine minus startLine must be 2 or less, so each citation " +
            "covers 1 to 3 consecutive lyric lines, not a whole section. summary " +
            "is a 5-to-8-word phrase that paraphrases the cited lines without " +
            "quoting lyric text. Citations must refer only to lyric lines, not to " +
            "track context or model background knowledge.\n\n" +
            "Each cited segment sets citationLine to the startLine value of the " +
            "matching citation. Uncited bridge segments set citationLine to 0.\n\n" +
            trackContext +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildLyricsMeaningFallbackPrompt(string numberedLyrics, string trackContext)
    {
        return
            "Interpret these numbered song lyrics in English in one short paragraph. " +
            "Use the lyrics as primary evidence. You may use supplied track context " +
            "or well-known background about the track only to disambiguate the lyrics. " +
            "Do not invent facts. The lyrics may be Korean or another non-English " +
            "language. Do not quote, copy, romanize, or repeat any lyric text; " +
            "paraphrase only. If there is not enough understandable context, say so " +
            "plainly.\n\n" +
            "Split the paragraph into 4 to 8 segments; concatenating segment.text " +
            "values in order (joined by single spaces where needed) must produce " +
            "the full paragraph. Create 2 to 3 citations with 1-based startLine " +
            "and endLine where endLine minus startLine is 2 or less (cover 1 to 3 " +
            "consecutive lyric lines). summary is a 5-to-8-word phrase that " +
            "paraphrases the cited lines without quoting lyrics. Each cited " +
            "segment sets citationLine to the matching citation's startLine value; " +
            "uncited bridge segments set citationLine to 0.\n\n" +
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

    private static string BuildMarkedLyricsContext(string? fullLyric, int lineIndex, string line)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return $">>> {line.Trim()} <<<";

        var lines = fullLyric
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (lineIndex >= 0 && lineIndex < lines.Length)
            lines[lineIndex] = $">>> {lines[lineIndex].Trim()} <<<";
        else
            return $">>> {line.Trim()} <<<\n\n{fullLyric.Trim()}";

        return string.Join("\n", lines).Trim();
    }

    private static string BuildNearbyLyricsContext(string? fullLyric, int lineIndex, string line)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return $">>> {line.Trim()} <<<";

        var lines = fullLyric
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (lineIndex < 0 || lineIndex >= lines.Length)
            return $">>> {line.Trim()} <<<";

        var start = Math.Max(0, lineIndex - 4);
        var end = Math.Min(lines.Length - 1, lineIndex + 4);
        var window = new string[end - start + 1];
        for (var source = start; source <= end; source++)
        {
            var target = source - start;
            window[target] = source == lineIndex
                ? $">>> {lines[source].Trim()} <<<"
                : lines[source];
        }

        return string.Join("\n", window).Trim();
    }

    private static string NormalizePromptMetadata(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
}

internal readonly record struct NumberedLyricsContext(string Text, int LineCount);
