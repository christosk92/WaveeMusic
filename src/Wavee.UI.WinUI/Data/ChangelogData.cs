using System.Collections.Generic;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Data;

public static class ChangelogData
{
    public static readonly IReadOnlyList<ChangelogRelease> Releases =
    [
        new ChangelogRelease
        {
            Version = "1.0.0",
            ReleaseTitle = AppLocalization.GetString("Changelog_1_0_0_Title"),
            Announcement = AppLocalization.GetString("Changelog_1_0_0_Announcement"),
            ReleaseUrl = "https://github.com/christosk92/WaveeMusic/releases/tag/v1.0.0",
            Features =
            [
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_1_0_0_Feature_CanvasLyrics_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_CanvasLyrics_Short"),
                    Glyph = "\uE8D6",
                    DetailTitle = AppLocalization.GetString("Changelog_1_0_0_Feature_CanvasLyrics_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_CanvasLyrics_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_1_0_0_Feature_Equalizer_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_Equalizer_Short"),
                    Glyph = "\uE9E9",
                    DetailTitle = AppLocalization.GetString("Changelog_1_0_0_Feature_Equalizer_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_Equalizer_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_1_0_0_Feature_HomeFeed_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_HomeFeed_Short"),
                    Glyph = "\uE80F",
                    DetailTitle = AppLocalization.GetString("Changelog_1_0_0_Feature_HomeFeed_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_HomeFeed_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_1_0_0_Feature_SmartReconnect_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_SmartReconnect_Short"),
                    Glyph = "\uE701",
                    DetailTitle = AppLocalization.GetString("Changelog_1_0_0_Feature_SmartReconnect_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_SmartReconnect_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_1_0_0_Feature_AudioCaching_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_AudioCaching_Short"),
                    Glyph = "\uE896",
                    DetailTitle = AppLocalization.GetString("Changelog_1_0_0_Feature_AudioCaching_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_1_0_0_Feature_AudioCaching_Detail"),
                },
            ]
        }
    ];
}
