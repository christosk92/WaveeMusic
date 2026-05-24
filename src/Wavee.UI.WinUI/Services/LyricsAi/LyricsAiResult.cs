using System.Collections.Generic;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// One contiguous run of the lyrics-meaning paragraph. <see cref="CitationId"/>
/// is 0 for uncited bridge text or the matching <see cref="LyricsAiCitation.Id"/>.
/// </summary>
public readonly record struct LyricsAiTextSegment(string Text, int CitationId);

/// <summary>
/// A single evidence reference into the source lyrics. Line numbers are 1-based
/// and inclusive; <see cref="Summary"/> is a paraphrased note, never a verbatim
/// quote of lyric text.
/// </summary>
public readonly record struct LyricsAiCitation(int Id, int StartLine, int EndLine, string Summary);

/// <summary>
/// Result envelope for <see cref="LyricsAiService"/>. Distinguishes the five
/// outcomes the UI cares about: success / not available / empty / filtered / error.
/// When <see cref="HasCitations"/> is true, <see cref="Segments"/> concatenates
/// to <see cref="Text"/> exactly and every cited segment points at a citation in
/// <see cref="Citations"/>.
/// </summary>
public readonly record struct LyricsAiResult(
    LyricsAiResultKind Kind,
    string Text,
    bool FromCache,
    string? ErrorMessage,
    IReadOnlyList<LyricsAiTextSegment>? Segments,
    IReadOnlyList<LyricsAiCitation>? Citations)
{
    public static readonly LyricsAiResult Unavailable = new(LyricsAiResultKind.Unavailable, string.Empty, false, null, null, null);
    public static readonly LyricsAiResult Empty = new(LyricsAiResultKind.Empty, string.Empty, false, null, null, null);
    public static readonly LyricsAiResult Filtered = new(LyricsAiResultKind.Filtered, string.Empty, false, null, null, null);

    public static LyricsAiResult Ok(string text, bool fromCache)
        => new(LyricsAiResultKind.Ok, text, fromCache, null, null, null);

    public static LyricsAiResult Ok(
        string text,
        bool fromCache,
        IReadOnlyList<LyricsAiTextSegment> segments,
        IReadOnlyList<LyricsAiCitation> citations)
        => new(LyricsAiResultKind.Ok, text, fromCache, null, segments, citations);

    public static LyricsAiResult Error(string message)
        => new(LyricsAiResultKind.Error, string.Empty, false, message, null, null);

    public LyricsAiResult WithCacheState(bool fromCache)
        => new(Kind, Text, fromCache, ErrorMessage, Segments, Citations);

    public bool IsSuccess => Kind == LyricsAiResultKind.Ok;

    public bool HasCitations => IsSuccess
                                && Segments is { Count: > 0 }
                                && Citations is { Count: > 0 };
}

public enum LyricsAiResultKind
{
    /// <summary>Generation succeeded (text in <see cref="LyricsAiResult.Text"/>).</summary>
    Ok,
    /// <summary>Feature gated off (no Copilot+ PC, region, or user opted out).</summary>
    Unavailable,
    /// <summary>Input was empty / whitespace.</summary>
    Empty,
    /// <summary>Content filter blocked the prompt or response.</summary>
    Filtered,
    /// <summary>Model invocation threw.</summary>
    Error,
}
