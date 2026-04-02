using Lyricify.Lyrics.Searchers.Helpers;

namespace Wavee.UI.WinUI.Services.Lyrics;

/// <summary>
/// Maps Lyricify's <see cref="CompareHelper.MatchType"/> enum to a 0-100 integer score.
/// </summary>
public static class MatchScoreHelper
{
    public static int ToScore(CompareHelper.MatchType? matchType) => matchType switch
    {
        CompareHelper.MatchType.Perfect => 100,
        CompareHelper.MatchType.VeryHigh => 99,
        CompareHelper.MatchType.High => 95,
        CompareHelper.MatchType.PrettyHigh => 90,
        CompareHelper.MatchType.Medium => 70,
        CompareHelper.MatchType.Low => 30,
        CompareHelper.MatchType.VeryLow => 10,
        _ => 0,
    };
}
