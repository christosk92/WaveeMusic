using Xunit;

namespace Wavee.Tests;

/// <summary>The page's twelve areas (`Settings.Areas.cs`): every document section is drawn by exactly one area, and every old deep link still lands somewhere.</summary>
public sealed class SettingsAreaTests
{
    [Fact]
    public void Slugs_AreOneToOneWithTheAreas()
    {
        Assert.Equal(Enum.GetValues<Settings.Area>().Length, Settings.AreaSlugs.Length);
        Assert.Equal(Settings.AreaSlugs.Length, Settings.AreaTitleKeys.Length);
        foreach (var area in Enum.GetValues<Settings.Area>())
            Assert.Equal(area, Settings.AreaFromSlug(Settings.AreaSlugs[(int)area]));
    }

    [Fact]
    public void EveryDocumentSection_IsDrawnByExactlyOneArea()
    {
        foreach (var tab in SettingsDoc.Tabs)
            foreach (var section in tab.Sections)
            {
                int homes = 0;
                foreach (var area in Enum.GetValues<Settings.Area>())
                    foreach (var part in Settings.PartsOf(area))
                        if (part.Tab == tab.Tab && part.Section == section.Section) homes++;
                Assert.True(homes == 1, tab.Tab + "/" + section.Section + " is drawn by " + homes + " areas");
            }
    }

    [Fact]
    public void EveryPart_NamesARealSection()
    {
        foreach (var area in Enum.GetValues<Settings.Area>())
            foreach (var part in Settings.PartsOf(area))
                Assert.NotNull(SettingsDoc.Section(part.Tab, part.Section));
    }

    [Theory]
    [InlineData("general", Settings.Area.Appearance)]
    [InlineData("appearance", Settings.Area.Appearance)]
    [InlineData("playback", Settings.Area.Playback)]
    [InlineData("notifications", Settings.Area.Notifications)]
    [InlineData("storage", Settings.Area.Storage)]
    [InlineData("privacy", Settings.Area.Privacy)]
    [InlineData("about", Settings.Area.About)]
    [InlineData("logs", Settings.Area.Feedback)]
    [InlineData("FEEDBACK", Settings.Area.Feedback)]
    [InlineData("downloads", Settings.Area.Storage)]
    [InlineData("no-such-area", Settings.Area.Appearance)]
    [InlineData(null, Settings.Area.Appearance)]
    public void DeepLinks_LandOnTheAreaThatNowHoldsWhatTheyOpened(string? slug, Settings.Area expected)
        => Assert.Equal(expected, Settings.AreaFromSlug(slug));

    [Fact]
    public void TheNewSections_HaveRealWriters()
    {
        foreach (var id in new[] { "reportProblem", "suggestFeature", "askQuestion", "copyDiagnostics" })
            Assert.NotNull(SettingsDoc.Row(Settings.Tab.Feedback, id).Writer.Run);
        Assert.NotNull(SettingsDoc.Row(Settings.Tab.Account, "signOut").Writer.Run);
        Assert.NotNull(SettingsDoc.Row(Settings.Tab.Devices, "connectDiagnostics").Writer.Run);
        Assert.All(SettingsDoc.Section(Settings.Tab.Keyboard, "Shortcuts").Rows, r => Assert.True(r.Writer.IsReadOnly, r.Id));
    }
}
