using System.Collections.Generic;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Data;

public static class ChangelogData
{
    private const string Alpha011AssetRoot = "ms-appx:///Assets/Changelog/0.1.1-alpha/";

    public static readonly IReadOnlyList<ChangelogRelease> Releases =
    [
        new ChangelogRelease
        {
            Version = "0.1.1-alpha.1",
            ReleaseTitle = AppLocalization.GetString("Changelog_0_1_1_Alpha_Title"),
            Announcement = AppLocalization.GetString("Changelog_0_1_1_Alpha_Announcement"),
            ReleaseUrl = "https://github.com/christosk92/WaveeMusic/releases/tag/v0.1.1-alpha.1",
            Features =
            [
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Refresh_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Refresh_Short"),
                    Glyph = FluentGlyphs.RefreshSwipe,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Refresh_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Refresh_Detail"),
                    NavigationHint = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Refresh_Nav"),
                    ImageAssetPath = Alpha011AssetRoot + "refresh-swipes.png",
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Resume_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Resume_Short"),
                    Glyph = FluentGlyphs.Undo,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Resume_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Resume_Detail"),
                    NavigationHint = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Resume_Nav"),
                    ImageAssetPath = Alpha011AssetRoot + "resume-reconcile.png",
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Spotlight_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Spotlight_Short"),
                    Glyph = FluentGlyphs.Canvas,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Spotlight_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Spotlight_Detail"),
                    NavigationHint = AppLocalization.GetString("Changelog_0_1_1_Alpha_Section_Spotlight_Nav"),
                    ImageAssetPath = Alpha011AssetRoot + "artist-spotlight-canvas.png",
                },
            ]
        },
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
                    Glyph = FluentGlyphs.Info,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Start_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Short"),
                    Glyph = FluentGlyphs.Play,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Test_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Short"),
                    Glyph = FluentGlyphs.Warning,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Limitations_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Short"),
                    Glyph = FluentGlyphs.Report,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Report_Detail"),
                },
                new ChangelogFeature
                {
                    Title = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Title"),
                    ShortDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Short"),
                    Glyph = FluentGlyphs.Library,
                    DetailTitle = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_DetailTitle"),
                    DetailDescription = AppLocalization.GetString("Changelog_0_1_0_Alpha_1_Section_Channel_Detail"),
                },
            ]
        }
    ];
}
