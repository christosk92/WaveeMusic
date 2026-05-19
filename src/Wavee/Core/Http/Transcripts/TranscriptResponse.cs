using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Transcripts;

/// <summary>
/// JSON serialization context for podcast transcript types (AOT compatible).
/// Mirrors the LyricsJsonContext pattern; source-generated so the strict-AOT
/// build in <c>Wavee</c> doesn't trip on reflection.
/// </summary>
[JsonSerializable(typeof(TranscriptResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class TranscriptJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Response from Spotify's <c>transcript-read-along/v2/episode/{id}</c> endpoint.
/// Each <see cref="Section"/> entry can carry either a chapter title (the
/// <c>title.title</c> string) or a transcribed sentence (the
/// <c>text.sentence</c> object) — sometimes both at the same <c>startMs</c>
/// when a chapter boundary aligns with a sentence start. <c>TimeSyncedStatus</c>
/// is <c>"SYLLABLE_SYNCED"</c> for fully karaoke-able transcripts (per-syllable
/// highlights) and <c>"LINE_SYNCED"</c> for sentence-only timing.
/// </summary>
public sealed record TranscriptResponse
{
    public string Version { get; init; } = "";
    public string TranscriptUri { get; init; } = "";
    public string Language { get; init; } = "";

    [JsonPropertyName("section")]
    public IReadOnlyList<TranscriptSection> Sections { get; init; } = [];

    public string? ShowName { get; init; }
    public string? EpisodeName { get; init; }
    public string TimeSyncedStatus { get; init; } = "";
}

public sealed record TranscriptSection
{
    public int StartMs { get; init; }
    public TranscriptTitle? Title { get; init; }
    public TranscriptText? Text { get; init; }
}

public sealed record TranscriptTitle
{
    [JsonPropertyName("title")]
    public string? Value { get; init; }
}

public sealed record TranscriptText
{
    public TranscriptSentence Sentence { get; init; } = new();
}

public sealed record TranscriptSentence
{
    public int StartMs { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<TranscriptHighlight> Highlight { get; init; } = [];
}

public sealed record TranscriptHighlight
{
    public int StartMs { get; init; }
    public int NumChars { get; init; }
}
