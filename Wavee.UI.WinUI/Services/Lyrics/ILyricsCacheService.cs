using System.Collections.Generic;
using Wavee.Core.Http.Lyrics;

namespace Wavee.UI.WinUI.Services.Lyrics;

public interface ILyricsCacheService
{
    (LyricsResponse Response, Dictionary<int, List<LrcWordTiming>>? WordTimings)? TryGet(string key);
    void Set(string key, LyricsResponse response, Dictionary<int, List<LrcWordTiming>>? wordTimings);
}
