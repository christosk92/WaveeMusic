using Wavee;
using Xunit;

namespace Wavee.Tests;

// The schema-generated config rows (ch 26 §1.1's field-kind table): the whole of what an extension can ask the
// customizer to draw, as data.
public class SidebarConfigFieldRulesTests
{
    [Fact]
    public void AnIntWithNoUsableMaxGetsTheDefaultCeiling()
    {
        Assert.Equal((1, 500), SidebarConfigFieldRules.IntRange(new SidebarConfigField("n", SidebarConfigFieldKind.Int, "k", Min: 1)));
        Assert.Equal((2, 500), SidebarConfigFieldRules.IntRange(new SidebarConfigField("n", SidebarConfigFieldKind.Int, "k", Min: 2, Max: 2)));
        Assert.Equal((0, 50), SidebarConfigFieldRules.IntRange(new SidebarConfigField("n", SidebarConfigFieldKind.Int, "k", Max: 50)));
    }

    [Fact]
    public void DefaultsParseInvariantAndDegradeToTheFloor()
    {
        Assert.Equal(12, SidebarConfigFieldRules.DefaultInt(new SidebarConfigField("n", SidebarConfigFieldKind.Int, "k", DefaultJson: "12")));
        Assert.Equal(3, SidebarConfigFieldRules.DefaultInt(new SidebarConfigField("n", SidebarConfigFieldKind.Int, "k", DefaultJson: "x", Min: 3)));
        Assert.True(SidebarConfigFieldRules.DefaultBool(new SidebarConfigField("b", SidebarConfigFieldKind.Bool, "k", DefaultJson: "TRUE")));
        Assert.False(SidebarConfigFieldRules.DefaultBool(new SidebarConfigField("b", SidebarConfigFieldKind.Bool, "k")));
    }

    [Fact]
    public void AnEnumWithNoValuesFallsThroughToText()
    {
        Assert.False(SidebarConfigFieldRules.RendersAsEnum(new SidebarConfigField("e", SidebarConfigFieldKind.Enum, "k")));
        Assert.True(SidebarConfigFieldRules.RendersAsEnum(
            new SidebarConfigField("e", SidebarConfigFieldKind.Enum, "k", EnumValues: ["a"])));
    }

    [Fact]
    public void TheChoiceIndexPrefersTheStoredValueThenTheQuotedDefault()
    {
        var field = new SidebarConfigField("sort", SidebarConfigFieldKind.Enum, "k", DefaultJson: "\"creator\"",
                                           EnumValues: ["recents", "alphabetical", "creator"]);
        Assert.Equal(1, SidebarConfigFieldRules.ChoiceIndex(field, "alphabetical"));
        Assert.Equal(2, SidebarConfigFieldRules.ChoiceIndex(field, null));
        Assert.Equal(0, SidebarConfigFieldRules.ChoiceIndex(field, "nope"));
    }

    [Fact]
    public void AnArtistKeyPicksArtists()
    {
        Assert.True(SidebarConfigFieldRules.PicksArtist(new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "k")));
        Assert.False(SidebarConfigFieldRules.PicksArtist(new SidebarConfigField("playlist", SidebarConfigFieldKind.EntityUri, "k")));
    }

    [Fact]
    public void KnownVocabularyReusesCatalogWordsAndTheRestShowsRaw()
    {
        Assert.Equal("sidebar.v3.filter.podcasts", SidebarConfigFieldRules.EnumLabelLocKey("shows"));
        Assert.Equal("sidebar.option.qualifierBySpotify", SidebarConfigFieldRules.EnumLabelLocKey("bySpotify"));
        Assert.Null(SidebarConfigFieldRules.EnumLabelLocKey("weekly"));
    }

    [Fact]
    public void TheExtensionBlockDegradesHonestly()
    {
        var xref = new SidebarExtensionRef("wavee", "wavee.queue", 2, SidebarJson.EmptyObject);
        Assert.Equal(SidebarExtensionNote.None, SidebarConfigFieldRules.NoteFor(xref, sourceRegistered: true, sourceSchemaVersion: 2));
        Assert.Equal(SidebarExtensionNote.ManageExtension, SidebarConfigFieldRules.NoteFor(xref, sourceRegistered: false, sourceSchemaVersion: 2));
        Assert.Equal(SidebarExtensionNote.ManageExtension, SidebarConfigFieldRules.NoteFor(xref, sourceRegistered: true, sourceSchemaVersion: 1));
        Assert.Equal(SidebarExtensionNote.PickContribution, SidebarConfigFieldRules.NoteFor(null, true, 1));
        Assert.Equal(SidebarExtensionNote.PickContribution,
            SidebarConfigFieldRules.NoteFor(new SidebarExtensionRef("", "", 1, SidebarJson.EmptyObject), true, 1));
    }
}
