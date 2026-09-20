using Wavee.LocPack;
using Xunit;

namespace Wavee.Tests;

public sealed class LocCasingTests
{
    static readonly string[] Acronyms =
    [
        "BPM", "ISRC", "SHA-256", "CD", "LCD", "VU", "PPM", "EP", "OK", "12\u2033 LP", "OR", "U2", "URI", "URL",
        "API", "EQ", "USB", "GPU", "CPU", "RAM", "JSON", "XML", "HTTP", "HTTPS", "ID", "UI", "OS", "GB", "MB", "KB",
        "MS", "NP", "FAQ",
    ];

    static readonly string[] LowercasePrefixes =
    [
        "library.rail.",
        "library.scope.",
        "library.readerSort.",
        "podcast.badge.",
        "podcast.cadence.",
        "podcast.tabs.",
        "whatsNew.issue.",
    ];

    [Theory]
    [InlineData("Play", LocVoice.Ambiguous)]
    [InlineData("Album", LocVoice.Ambiguous)]
    [InlineData("Daily mix", LocVoice.Sentence)]
    [InlineData("Now Playing", LocVoice.TitleCase)]
    [InlineData("WATCH THE OFFICIAL VIDEO", LocVoice.AllCaps)]
    [InlineData("BPM", LocVoice.Acronym)]
    [InlineData("recents", LocVoice.Lowercase)]
    public void Classify_Samples(string value, LocVoice expected)
        => Assert.Equal(expected, LocCasing.Classify(value));

    [Fact]
    public void Classify_IcuPlural_StripLeavesAmbiguousWhenEmpty()
    {
        Assert.Equal(LocVoice.Ambiguous,
            LocCasing.Classify("{count, plural, one {# Album} other {# Albums}}"));
    }

    [Fact]
    public void Classify_IcuPlaceholder_StripToSentenceOrLowercase()
    {
        Assert.Equal(LocVoice.Lowercase, LocCasing.Classify("{n} saves"));
    }

    [Fact]
    public void Violations_Flags_AllCaps_And_TitleCase()
    {
        var flat = new Dictionary<string, string>
        {
            ["detail.shout"] = "WATCH THE OFFICIAL VIDEO",
            ["sidebar.section.nowPlaying"] = "Now Playing",
        };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes);
        Assert.Contains(v, x => x.Key == "detail.shout" && x.Voice == LocVoice.AllCaps);
        Assert.Contains(v, x => x.Key == "sidebar.section.nowPlaying" && x.Voice == LocVoice.TitleCase);
    }

    [Fact]
    public void Violations_Flags_Lowercase_Strays()
    {
        var flat = new Dictionary<string, string> { ["library.sort.recents"] = "recents" };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes);
        Assert.Contains(v, x => x.Key == "library.sort.recents");
    }

    [Fact]
    public void Violations_Allows_Rail_Lowercase_And_Home_Eye()
    {
        var flat = new Dictionary<string, string>
        {
            ["library.rail.albums"] = "albums",
            ["home.chartsEye"] = "charts",
        };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes);
        Assert.DoesNotContain(v, x => x.Key == "library.rail.albums");
        Assert.DoesNotContain(v, x => x.Key == "home.chartsEye");
    }

    [Fact]
    public void Violations_Allows_ProperNoun_TitleCase()
    {
        var flat = new Dictionary<string, string> { ["nav.likedSongs"] = "Liked Songs" };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes, ["Liked Songs"]);
        Assert.DoesNotContain(v, x => x.Key == "nav.likedSongs");
    }

    [Fact]
    public void Violations_Skips_Icu_Template_Lowercase_Tail()
    {
        var flat = new Dictionary<string, string> { ["detail.saveCountCompact"] = "{value} saves" };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes);
        Assert.DoesNotContain(v, x => x.Key == "detail.saveCountCompact");
    }

    [Fact]
    public void Violations_Allows_Listed_Acronyms()
    {
        var flat = new Dictionary<string, string>
        {
            ["player.bpm"] = "BPM",
            ["detail.badge.ep"] = "EP",
        };
        var v = LocCasing.Violations(flat, Acronyms, LowercasePrefixes);
        Assert.DoesNotContain(v, x => x.Key == "player.bpm");
        Assert.DoesNotContain(v, x => x.Key == "detail.badge.ep");
    }

    [Fact]
    public void Contradictions_Flags_SameNamespace_Stray_Casing()
    {
        var flat = new Dictionary<string, string>
        {
            ["podcast.played"] = "played",
            ["podcast.filter.played"] = "Played",
        };
        var c = LocCasing.Contradictions(flat, LowercasePrefixes);
        Assert.Contains(c, p => p is { A: "podcast.filter.played", B: "podcast.played" });
    }

    [Fact]
    public void Contradictions_Skips_Rail_Explained_Pair()
    {
        var flat = new Dictionary<string, string>
        {
            ["library.rail.recents"] = "recents",
            ["library.sort.recents"] = "Recents",
        };
        var c = LocCasing.Contradictions(flat, LowercasePrefixes);
        Assert.DoesNotContain(c, p =>
            (p.A == "library.rail.recents" && p.B == "library.sort.recents")
            || (p.A == "library.sort.recents" && p.B == "library.rail.recents"));
    }

    [Fact]
    public void Contradictions_Skips_Different_TopLevel_Namespace()
    {
        var flat = new Dictionary<string, string>
        {
            ["library.rail.albums"] = "albums",
            ["nav.albums"] = "Albums",
        };
        var c = LocCasing.Contradictions(flat, LowercasePrefixes);
        Assert.Empty(c);
    }

    [Fact]
    public void SharedValueClusters_Groups_Exact_Value()
    {
        var flat = new Dictionary<string, string>
        {
            ["a.one"] = "Artist",
            ["b.two"] = "Artist",
            ["c.other"] = "Album",
        };
        var clusters = LocCasing.SharedValueClusters(flat);
        var artist = clusters.Single(c => c.Value == "Artist");
        Assert.Equal(2, artist.Keys.Count);
        Assert.Contains("a.one", artist.Keys);
        Assert.Contains("b.two", artist.Keys);
    }
}
