using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins the one table behind the Player-style flyout, the Settings rows, the art context menu and the deck
/// faces (docs/plans/wavee/npv-player-styles-implementation.md, Part 1). Ids and slugs are PERSISTED — a change here
/// is a wire-format change, so these assertions exist to make renumbering/renaming a loud, deliberate act.</summary>
public class NpvPlayerCatalogTests
{
    [Fact]
    public void Ids_AreContiguousAndEqualTheirIndex()
    {
        for (int i = 0; i < NpvPlayerCatalog.Presets.Length; i++)
            Assert.Equal(i, NpvPlayerCatalog.Presets[i].Id);
    }

    [Fact]
    public void Slugs_AreUniqueLowercaseAsciiAndPinned()
    {
        string[] expected =
        [
            "record", "cassette", "reel", "cd", "turntable", "ipod", "winamp", "vu", "zune", "wmp", "canvas", "picture",
        ];
        Assert.Equal(expected, NpvPlayerCatalog.Presets.Select(p => p.Slug).ToArray());
        Assert.Equal(expected.Length, expected.Distinct().Count());
        foreach (var slug in expected)
            Assert.True(slug.All(c => c is >= 'a' and <= 'z'), $"'{slug}' is not lowercase ASCII.");
    }

    [Fact]
    public void ExactlyTwelveRows_FourPerGroup()
    {
        Assert.Equal(12, NpvPlayerCatalog.Presets.Length);
        foreach (var group in new[] { NpvPlayerGroup.Media, NpvPlayerGroup.Devices, NpvPlayerGroup.Software })
        {
            int count = NpvPlayerCatalog.Presets.Count(p => p.Group == group);
            Assert.Equal(NpvPlayerCatalog.PerGroup, count);
        }
    }

    [Fact]
    public void EveryPreset_HasAtLeastOneOption()
    {
        foreach (var p in NpvPlayerCatalog.Presets)
            Assert.True(p.Options.Length >= 1, $"{p.Slug} has no options.");
    }

    [Fact]
    public void EveryOption_HasAtLeastTwoChoicesWithUniqueSlugs()
    {
        foreach (var p in NpvPlayerCatalog.Presets)
            foreach (var o in p.Options)
            {
                Assert.True(o.Choices.Length >= 2, $"{p.Slug}.{o.Slug} has fewer than 2 choices.");
                Assert.Equal(o.Choices.Length, o.Choices.Select(c => c.Slug).Distinct().Count());
            }
    }

    [Fact]
    public void EveryLabelKey_StartsWithPlayerDot()
    {
        foreach (var p in NpvPlayerCatalog.Presets)
        {
            Assert.StartsWith("player.", p.LabelKey);
            Assert.StartsWith("player.", p.ShortLabelKey);
            foreach (var o in p.Options)
            {
                Assert.StartsWith("player.", o.LabelKey);
                foreach (var c in o.Choices) Assert.StartsWith("player.", c.LabelKey);
            }
        }
    }

    [Fact]
    public void SwatchOptions_HaveAColourOrFromCover()
    {
        foreach (var p in NpvPlayerCatalog.Presets)
            foreach (var o in p.Options)
                if (o.Kind == NpvOptionKind.Swatch)
                    foreach (var c in o.Choices)
                        Assert.True(c.Swatch != 0 || c.Swatch == NpvPlayerCatalog.FromCover);
    }

    [Fact]
    public void ById_UnknownFallsBackToRecord()
    {
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerCatalog.ById(-1).Id);
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerCatalog.ById(99).Id);
        Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerCatalog.ById(NpvPlayerCatalog.Presets.Length).Id);
    }

    [Fact]
    public void DefaultPresetId_IsRecord() => Assert.Equal(NpvPlayerCatalog.Record, NpvPlayerCatalog.DefaultPresetId);

    [Fact]
    public void IsPresetId_TrueOnlyForRealIds()
    {
        for (int i = 0; i < NpvPlayerCatalog.Presets.Length; i++) Assert.True(NpvPlayerCatalog.IsPresetId(i));
        Assert.False(NpvPlayerCatalog.IsPresetId(-1));
        Assert.False(NpvPlayerCatalog.IsPresetId(NpvPlayerCatalog.Presets.Length));
    }

    [Fact]
    public void Group_ReturnsOnlyPresetsInThatGroup()
    {
        var dest = new NpvPlayerCatalog.Preset[NpvPlayerCatalog.Presets.Length];
        int n = NpvPlayerCatalog.Group(NpvPlayerGroup.Devices, dest);
        Assert.Equal(NpvPlayerCatalog.PerGroup, n);
        for (int i = 0; i < n; i++) Assert.Equal(NpvPlayerGroup.Devices, dest[i].Group);
    }

    [Fact]
    public void Option_FindsBySlug_OrFallsBackToFirst()
    {
        var record = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var rpm = NpvPlayerCatalog.Option(record, "rpm");
        Assert.Equal("rpm", rpm.Slug);
        var missing = NpvPlayerCatalog.Option(record, "does-not-exist");
        Assert.Equal(record.Options[0].Slug, missing.Slug);
    }

    [Fact]
    public void RecordAndTurntable_ShareOptionRowsButAreDistinctPresets()
    {
        var record = NpvPlayerCatalog.ById(NpvPlayerCatalog.Record);
        var turntable = NpvPlayerCatalog.ById(NpvPlayerCatalog.Turntable);
        Assert.Equal(record.Options.Select(o => o.Slug), turntable.Options.Select(o => o.Slug));
        Assert.NotEqual(record.Slug, turntable.Slug);
    }
}
