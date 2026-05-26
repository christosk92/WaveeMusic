using System;
using System.Collections.Generic;
using System.Text;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services;

internal static class LyricsAiPrompts
{
    internal const int MaxFallbackLyricsCharacters = 3200;

    internal static string BuildLyricsMeaningPlainTextPrompt(
        string numberedLyrics,
        string trackContext,
        IReadOnlyList<MusicGroundingSource>? groundingSources)
    {
        return
            "Interpret song lyrics with the numbered lyrics as primary evidence. " +
            "Use trained music-domain knowledge about the artist, genre, song, idioms, " +
            "references, and cultural context when it helps, but never override the lyrics. " +
            "Music-specific external metadata is provided as supporting background to ground references, " +
            "idioms, and cultural context — repeat web facts only when they're clearly about the same song. " +
            "Do not invent facts.\n\n" +
            "Write one tight paragraph in English: 2 sentences, 35 to 55 words, naming " +
            "who speaks, to whom, what they feel, and what they want. No bullets, lists, " +
            "headings, markdown, line breaks, citations, or quoted lyric text. Paraphrase only.\n\n" +
            trackContext +
            BuildMusicGroundingBlock(groundingSources) +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildLyricsMeaningPlainTextFallbackPrompt(
        string numberedLyrics,
        string trackContext,
        IReadOnlyList<MusicGroundingSource>? groundingSources)
    {
        return
            "Interpret these numbered lyrics in English in one concise paragraph " +
            "(2 sentences, 35 to 50 words). Lyrics are the primary evidence; trained " +
            "music-domain knowledge, track context, and music grounding may disambiguate only. " +
            "Do not invent facts, quote lyrics, use markdown, or add line breaks. " +
            "If context is too thin, say so plainly.\n\n" +
            trackContext +
            BuildMusicGroundingBlock(groundingSources) +
            "LYRICS WITH LINE NUMBERS:\n" +
            numberedLyrics;
    }

    internal static string BuildMusicGroundingBlock(IReadOnlyList<MusicGroundingSource>? sources)
    {
        if (sources is null || sources.Count == 0)
            return string.Empty;

        var sb = new StringBuilder("MUSIC_GROUNDING:\n");
        var emitted = 0;
        foreach (var result in sources)
        {
            if (emitted >= 5) break;
            if (string.IsNullOrWhiteSpace(result.Title)) continue;

            var snippet = (result.Snippet ?? string.Empty).Trim();
            if (snippet.Length > 280) snippet = snippet[..280];

            sb.Append("- ").Append(result.Title.Trim());
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.Append(" — ").Append(snippet);
            if (!string.IsNullOrWhiteSpace(result.SourceName))
                sb.Append(" (").Append(result.SourceName).Append(')');
            sb.AppendLine();
            emitted++;
        }

        if (emitted == 0) return string.Empty;
        sb.AppendLine();
        return sb.ToString();
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
