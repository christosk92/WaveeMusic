using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>NpvPlayerPrefs (docs/plans/wavee/npv-player-styles-implementation.md, Part 1): clamps, round-trips, the
/// Epoch bump contract, and the "picking a style also shows it" rule — over a real <see cref="MemoryAppSettings"/> so
/// the option-key naming is asserted against the exact persisted string, not a description of it. Epoch is a
/// process-wide static, so every assertion here is a DELTA (before/after), never an absolute value.</summary>
public class NpvPlayerPrefsTests
{
    [Theory]
    [InlineData(-1, NpvPlayerPrefs.Cover)]
    [InlineData(0, NpvPlayerPrefs.Cover)]
    [InlineData(1, NpvPlayerPrefs.Player)]
    [InlineData(2, NpvPlayerPrefs.Cover)]
    [InlineData(99, NpvPlayerPrefs.Cover)]
    public void ClampPresentation_CoercesAnythingNotPlayerToCover(int stray, int expected)
        => Assert.Equal(expected, NpvPlayerPrefs.ClampPresentation(stray));

    [Fact]
    public void ClampStyle_StrayFallsBackToRecord()
    {
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerPrefs.ClampStyle(-1));
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerPrefs.ClampStyle(999));
        Assert.Equal(NpvPlayerCatalog.Turntable, NpvPlayerPrefs.ClampStyle(NpvPlayerCatalog.Turntable));
    }

    [Fact]
    public void ClampChoice_OutOfRangeFallsBackToZero()
    {
        var preset = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var rpm = NpvPlayerCatalog.Option(preset, "rpm");
        Assert.Equal(0, NpvPlayerPrefs.ClampChoice(rpm, -1));
        Assert.Equal(0, NpvPlayerPrefs.ClampChoice(rpm, 999));
        Assert.Equal(1, NpvPlayerPrefs.ClampChoice(rpm, 1));
    }

    [Fact]
    public void Presentation_DefaultsToCover_NullSettingsIsANoOpRead()
    {
        Assert.Equal(NpvPlayerPrefs.Cover, NpvPlayerPrefs.Presentation(null));
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerPrefs.Style(null));
    }

    [Fact]
    public void SetPresentation_RoundTripsAndBumpsEpochOnce()
    {
        var settings = new MemoryAppSettings();
        long before = NpvPlayerPrefs.Epoch.Peek();
        NpvPlayerPrefs.SetPresentation(settings, NpvPlayerPrefs.Player, "test");
        Assert.Equal(NpvPlayerPrefs.Player, NpvPlayerPrefs.Presentation(settings));
        Assert.Equal(before + 1, NpvPlayerPrefs.Epoch.Peek());
    }

    [Fact]
    public void TogglePresentation_Alternates()
    {
        var settings = new MemoryAppSettings();
        Assert.Equal(NpvPlayerPrefs.Cover, NpvPlayerPrefs.Presentation(settings));
        NpvPlayerPrefs.TogglePresentation(settings, "test");
        Assert.Equal(NpvPlayerPrefs.Player, NpvPlayerPrefs.Presentation(settings));
        NpvPlayerPrefs.TogglePresentation(settings, "test");
        Assert.Equal(NpvPlayerPrefs.Cover, NpvPlayerPrefs.Presentation(settings));
    }

    [Fact]
    public void SetStyle_FlipsToPlayer_WhenNotAlreadyPlayer()
    {
        var settings = new MemoryAppSettings();
        NpvPlayerPrefs.SetStyle(settings, NpvPlayerCatalog.Turntable, "test");
        Assert.Equal(NpvPlayerCatalog.Turntable, NpvPlayerPrefs.Style(settings));
        Assert.Equal(NpvPlayerPrefs.Player, NpvPlayerPrefs.Presentation(settings));
    }

    [Fact]
    public void SetStyle_DoesNotRewritePresentation_WhenAlreadyPlayer()
    {
        var settings = new MemoryAppSettings();
        NpvPlayerPrefs.SetPresentation(settings, NpvPlayerPrefs.Player, "test");
        // Overwrite with the SAME value so a later write would be observable as a distinct MemoryAppSettings entry
        // if it happened; there's no "write count" probe, so we assert the value stays exactly Player.
        NpvPlayerPrefs.SetStyle(settings, NpvPlayerCatalog.Cassette, "test");
        Assert.Equal(NpvPlayerPrefs.Player, settings.Get(WaveeSettings.NpvPresentation));
    }

    [Fact]
    public void OptionKeyNames_MatchThePersistedConvention()
    {
        var key = NpvPlayerKeys.Option("turntable", "finish");
        Assert.Equal("npv.player.turntable.finish", key.Name);
    }

    [Fact]
    public void RecordAndTurntable_UseSeparateOptionKeys()
    {
        var settings = new MemoryAppSettings();
        var record = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var turntable = NpvPlayerCatalog.ById(NpvPlayerCatalog.Turntable);
        var rpm = NpvPlayerCatalog.Option(record, "rpm");
        NpvPlayerPrefs.SetChoice(settings, record, rpm, 1, "test");
        Assert.Equal(1, NpvPlayerPrefs.Choice(settings, record, "rpm"));
        Assert.Equal(0, NpvPlayerPrefs.Choice(settings, turntable, "rpm"));
        Assert.False(settings.WasWritten(NpvPlayerKeys.Option(turntable.Slug, "rpm")));
    }

    [Fact]
    public void NextStyle_WrapsPictureToRecord()
    {
        var settings = new MemoryAppSettings();
        NpvPlayerPrefs.SetStyle(settings, NpvPlayerCatalog.Picture, "test");
        NpvPlayerPrefs.NextStyle(settings, "test");
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerPrefs.Style(settings));
    }

    [Fact]
    public void ChoiceSlug_ReturnsTheSelectedChoicesSlug()
    {
        var settings = new MemoryAppSettings();
        var record = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var rpm = NpvPlayerCatalog.Option(record, "rpm");
        NpvPlayerPrefs.SetChoice(settings, record, rpm, 1, "test");
        Assert.Equal("45", NpvPlayerPrefs.ChoiceSlug(settings, record, "rpm"));
    }

    [Fact]
    public void Choice_OutOfRangeStoredValueClampsToZero()
    {
        var settings = new MemoryAppSettings();
        var record = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var rpm = NpvPlayerCatalog.Option(record, "rpm");
        settings.Set(NpvPlayerKeys.Option(record.Slug, "rpm"), 999);
        Assert.Equal(0, NpvPlayerPrefs.Choice(settings, record, rpm));
    }
}
