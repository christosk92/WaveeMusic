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
            Version = "0.1.0-alpha.1",
            ReleaseTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Title"),
            Announcement = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Announcement"),
            ReleaseUrl = "https://github.com/christosk92/WaveeMusic/releases/tag/v0.1.0-alpha.1",
            Features =
            [
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_Short"),
                    Glyph = "\uE946",
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Short"),
                    Glyph = "\uE8EF",
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Short"),
                    Glyph = "\uE7BA",
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Short"),
                    Glyph = "\uE8BD",
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Short"),
                    Glyph = "\uE895",
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Detail"),
                },
            ]
        }
    ];
}
