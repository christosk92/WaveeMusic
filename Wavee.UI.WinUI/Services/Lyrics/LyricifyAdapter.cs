using System.Collections.Generic;
using Wavee.Core.Http.Lyrics;
using LyricifyData = Lyricify.Lyrics.Models.LyricsData;
using LyricifyLineInfo = Lyricify.Lyrics.Models.ILineInfo;
using LyricifySyllableLine = Lyricify.Lyrics.Models.SyllableLineInfo;
using LyricifySyncTypes = Lyricify.Lyrics.Models.SyncTypes;

namespace Wavee.UI.WinUI.Services.Lyrics;

/// <summary>
/// Converts Lyricify's lyrics data to Wavee's <see cref="LyricsResponse"/> and word timing dictionary.
/// </summary>
public static class LyricifyAdapter
{
    public static (LyricsResponse Response, Dictionary<int, List<LrcWordTiming>>? WordTimings)
        ToWaveeResponse(LyricifyData lyricsData, string providerName)
    {
        var lines = new List<LyricsLine>();
        Dictionary<int, List<LrcWordTiming>>? wordTimings = null;
        var hasSyllables = false;

        if (lyricsData.Lines != null)
        {
            for (int i = 0; i < lyricsData.Lines.Count; i++)
            {
                var line = lyricsData.Lines[i];

                var startMs = line.StartTime ?? 0;
                var endMs = line.EndTime ?? 0;

                // If no end time, try to infer from next line
                if (endMs <= startMs && i + 1 < lyricsData.Lines.Count)
                {
                    var nextStart = lyricsData.Lines[i + 1].StartTime;
                    if (nextStart.HasValue && nextStart.Value > startMs)
                        endMs = nextStart.Value;
                }

                lines.Add(new LyricsLine
                {
                    StartTimeMs = startMs.ToString(),
                    EndTimeMs = endMs.ToString(),
                    Words = line.Text ?? "",
                });

                // Extract syllable-level timing if available
                if (line is LyricifySyllableLine syllableLine && syllableLine.IsSyllable)
                {
                    hasSyllables = true;
                    wordTimings ??= new Dictionary<int, List<LrcWordTiming>>();

                    var timings = new List<LrcWordTiming>(syllableLine.Syllables.Count);
                    foreach (var syl in syllableLine.Syllables)
                    {
                        timings.Add(new LrcWordTiming(syl.StartTime, syl.EndTime, syl.Text));
                    }
                    wordTimings[i] = timings;
                }
            }
        }

        var syncType = lyricsData.File?.SyncTypes switch
        {
            LyricifySyncTypes.SyllableSynced => "LINE_SYNCED",
            LyricifySyncTypes.MixedSynced => "LINE_SYNCED",
            LyricifySyncTypes.LineSynced => "LINE_SYNCED",
            LyricifySyncTypes.Unsynced => "UNSYNCED",
            _ => hasSyllables ? "LINE_SYNCED" : (lines.Count > 0 ? "LINE_SYNCED" : "UNSYNCED"),
        };

        var response = new LyricsResponse
        {
            Lyrics = new Core.Http.Lyrics.LyricsData
            {
                SyncType = syncType,
                Lines = lines,
                Provider = providerName,
                ProviderDisplayName = providerName,
                Language = "",
                IsRtlLanguage = false,
                IsDenseTypeface = false,
            },
            Colors = null,
            HasVocalRemoval = false,
        };

        return (response, wordTimings);
    }
}
