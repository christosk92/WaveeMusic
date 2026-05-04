using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using ControlsLyricsData = Wavee.Controls.Lyrics.Models.Lyrics.LyricsData;
using ControlsLyricsLine = Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine;
using ControlsBaseLyrics = Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics;

namespace Wavee.UI.WinUI.Services;

internal sealed record CachedLyricsDto(
    List<CachedLyricsLineDto> Lines,
    string? LanguageCode);

internal sealed record CachedLyricsLineDto(
    int StartMs,
    int? EndMs,
    string PrimaryText,
    string? SecondaryText,
    string? TertiaryText,
    bool HasSyllableSync,
    List<CachedSyllableDto>? PrimarySyllables,
    List<CachedSyllableDto>? SecondarySyllables);

internal sealed record CachedSyllableDto(
    int StartMs,
    int? EndMs,
    string Text,
    int StartIndex);

[JsonSerializable(typeof(CachedLyricsDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LyricsCacheJsonContext : JsonSerializerContext { }

internal static class LyricsCacheConverter
{
    public static CachedLyricsDto ToDto(ControlsLyricsData data)
    {
        var lines = data.LyricsLines.Select(line => new CachedLyricsLineDto(
            StartMs: line.StartMs,
            EndMs: line.EndMs,
            PrimaryText: line.PrimaryText,
            SecondaryText: string.IsNullOrEmpty(line.SecondaryText) ? null : line.SecondaryText,
            TertiaryText: string.IsNullOrEmpty(line.TertiaryText) ? null : line.TertiaryText,
            HasSyllableSync: line.IsPrimaryHasRealSyllableInfo,
            PrimarySyllables: line.PrimarySyllables.Count > 0
                ? line.PrimarySyllables.Select(s => new CachedSyllableDto(s.StartMs, s.EndMs, s.Text, s.StartIndex)).ToList()
                : null,
            SecondarySyllables: line.SecondarySyllables.Count > 0
                ? line.SecondarySyllables.Select(s => new CachedSyllableDto(s.StartMs, s.EndMs, s.Text, s.StartIndex)).ToList()
                : null
        )).ToList();

        return new CachedLyricsDto(lines, data.LanguageCode);
    }

    public static ControlsLyricsData FromDto(CachedLyricsDto dto)
    {
        var lines = dto.Lines.Select(l =>
        {
            var line = new ControlsLyricsLine
            {
                StartMs = l.StartMs,
                EndMs = l.EndMs,
                PrimaryText = l.PrimaryText,
                SecondaryText = l.SecondaryText ?? "",
                TertiaryText = l.TertiaryText ?? "",
                IsPrimaryHasRealSyllableInfo = l.HasSyllableSync,
            };

            if (l.PrimarySyllables != null)
            {
                line.PrimarySyllables = l.PrimarySyllables.Select(s => new ControlsBaseLyrics
                {
                    StartMs = s.StartMs,
                    EndMs = s.EndMs,
                    Text = s.Text,
                    StartIndex = s.StartIndex,
                }).ToList();
            }

            if (l.SecondarySyllables != null)
            {
                line.SecondarySyllables = l.SecondarySyllables.Select(s => new ControlsBaseLyrics
                {
                    StartMs = s.StartMs,
                    EndMs = s.EndMs,
                    Text = s.Text,
                    StartIndex = s.StartIndex,
                }).ToList();
            }

            return line;
        }).ToList();

        return new ControlsLyricsData(lines) { LanguageCode = dto.LanguageCode };
    }
}
