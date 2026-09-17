// ── Wavee.Tests/HomeWashLocaleTests.cs — the wash on a Korean account (Wave 5, owner P; ported from 0.2.9) ───────────────
//
// The shell wash on a KOREAN account must be the same three colours as on an English one. Nothing in the pipeline may read
// copy: the decode routes on `__typename` + the `format` token, the composer on the section kind and the card content,
// and HomeWashSource selects by kind + ordinal and resolves from the payload accent / the graded cover. Both halves are
// driven end to end — two documents byte-identical except for every human-readable string — through the real 0.3 path:
// `Spotify.Decode.HomeFeed` → `Entities.Commit` → `HomeComposer.Compose` → `HomeWashSource`.
//
// Each locale is composed in its OWN scope (the cards are handles over the tables, and the two documents name the same
// entities), and the comparison is over VALUES captured from each: the picks, the uris, the titles.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeWashLocaleTests
{
    // Three distinct 40-char Spotify image ids (16-char size prefix + 24-char artwork identity).
    const string HeroArt = "ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa";
    const string WeeklyArt = "ab67616d0000b273bbbbbbbbbbbbbbbbbbbbbbbb";
    const string MixArt = "ab67616d0000b273cccccccccccccccccccccccc";

    const string HeroAccent = "#1E3A5F";
    const string WeeklyAccent = "#7A2E12";
    // The mix card carries NO server accent, so its slot falls through to the graded cover (tier 2, keyed on the url).
    static Scheme Graded => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    // ── the fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One home document whose STRUCTURE is fixed and whose COPY is entirely parameterized.</summary>
    static string Feed(
        string heroSection, string weeklySection, string mixSection, string mixBaseText,
        string heroName, string weeklyName, string radarName, string mix1Name, string mix2Name)
        => "{ \"sectionContainer\": { \"sections\": { \"items\": ["
            // Spotlight is the Hero source by SECTION TYPE — its title is never consulted.
            + "{ \"uri\": \"spotify:section:locale-hero\", \"data\": { \"__typename\": \"HomeSpotlightSectionData\","
            + " \"title\": { \"transformedLabel\": \"" + heroSection + "\" } },"
            + " \"sectionItems\": { \"items\": ["
            + Item("spotify:playlist:HERO", heroName, "", HeroArt, HeroAccent)
            + "] } },"
            // …the weekly 2-up and the mix band are routed by the `format` token on each card.
            + Generic("spotify:section:locale-weekly", weeklySection, null,
                Item("spotify:playlist:DW", weeklyName, "discover-weekly", WeeklyArt, WeeklyAccent),
                Item("spotify:playlist:RR", radarName, "release-radar", MixArt, WeeklyAccent))
            + ","
            + Generic("spotify:section:locale-mix", mixSection, mixBaseText,
                Item("spotify:playlist:M1", mix1Name, "daily-mix", MixArt, null),
                Item("spotify:playlist:M2", mix2Name, "daily-mix", HeroArt, null))
            + "] } } }";

    static string Generic(string uri, string title, string? baseText, params string[] items)
        => "{ \"uri\": \"" + uri + "\", \"data\": { \"__typename\": \"HomeGenericSectionData\", \"title\": { \"transformedLabel\": \"" + title + "\""
            + (baseText is null ? "" : ", \"translatedBaseText\": \"" + baseText + "\"")
            + " } }, \"sectionItems\": { \"items\": [" + string.Join(",", items) + "] } }";

    static string Item(string uri, string name, string format, string artId, string? accentHex)
        => "{ \"content\": { \"data\": {"
            + "\"__typename\": \"Playlist\","
            + "\"uri\": \"" + uri + "\","
            + "\"name\": \"" + name + "\","
            + "\"format\": \"" + format + "\","
            + "\"content\": { \"totalCount\": 50 },"
            + "\"images\": { \"items\": [ { \"sources\": [ { \"url\": \"https://i.scdn.co/image/" + artId + "\", \"width\": 640 } ]"
            + (accentHex is null ? "" : ", \"extractedColors\": { \"colorDark\": { \"hex\": \"" + accentHex + "\", \"isFallback\": false } }")
            + " } ] }"
            + "} } }";

    /// <summary>Everything a comparison needs, captured as values while the locale's scope is current.</summary>
    readonly record struct Observed(
        HomeWashPicks Picks, string HeroUri, string WeeklyUri, string MixUri, string HeroTitle, string? MixPlaneUrl,
        string GroupTitles, string CardTitles);

    static Scheme? Plane(string? url) => url is not null && url.Contains(MixArt, StringComparison.Ordinal) ? Graded : null;

    static Observed Observe(string homeJson)
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.HomeFeed(Encoding.UTF8.GetBytes("{\"data\":{\"home\":" + homeJson + "}}"), "wavee:home"u8, s);
        TestScope.CommitAndPublish(s);
        var feed = HomeComposer.Compose(Entities.HomeFeed(), HomeModuleTitles.Default);

        var sources = HomeWashSource.Sources(feed);
        return new Observed(
            HomeWashSource.Select(feed, Plane),
            sources.Hero!.Value.Uri, sources.Weekly!.Value.Uri, sources.Mix!.Value.Uri,
            sources.Hero!.Value.Title,
            HomeWashSource.PlaneUrl(sources.Mix),
            string.Join("|", feed.Groups.Select(g => g.Title ?? "")),
            string.Join("|", feed.Groups.SelectMany(g => g.Cards).Select(c => c.Title)));
    }

    static Observed English() => Observe(Feed(
        heroSection: "Spotlight for you", weeklySection: "New music every week",
        mixSection: "Made For Christos", mixBaseText: "Made For {0}",
        heroName: "Daily drive", weeklyName: "Discover Weekly", radarName: "Release Radar",
        mix1Name: "Daily Mix 1", mix2Name: "Daily Mix 2"));

    static Observed Korean() => Observe(Feed(
        heroSection: "당신을 위한 스포트라이트", weeklySection: "매주 만나는 새로운 음악",
        mixSection: "나를 위한 데일리 믹스", mixBaseText: "{0}님을 위한 믹스",
        heroName: "데일리 드라이브", weeklyName: "디스커버 위클리", radarName: "릴리스 레이더",
        mix1Name: "믹스 1", mix2Name: "믹스 2"));

    // ── the contract ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWash_IsIdenticalOnAKoreanAccount()
    {
        var en = English();
        var ko = Korean();

        // Guard the fixture first: if the two documents ever stopped differing in copy, everything below would pass for
        // the wrong reason.
        Assert.NotEqual(en.GroupTitles, ko.GroupTitles);
        Assert.NotEqual(en.CardTitles, ko.CardTitles);

        Assert.Equal(en.Picks, ko.Picks);
        Assert.Equal(HomeWashSource.Fingerprint(en.Picks), HomeWashSource.Fingerprint(ko.Picks));

        var pe = en.Picks;
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(0xFF1E3A5Fu)) with { A = 1f }, pe.Hero!.Value.Color);
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(0xFF7A2E12u)) with { A = 1f }, pe.Weekly!.Value.Color);
        Assert.Equal(Design.Palette.ChromeAccent(Graded) with { A = 1f }, pe.Mix!.Value.Color);   // tier 2: the graded cover
        Assert.Equal(Palette.KeyOf("https://i.scdn.co/image/" + HeroArt).ToString(), pe.Hero!.Value.Key);
        Assert.Equal(Palette.KeyOf("https://i.scdn.co/image/" + WeeklyArt).ToString(), pe.Weekly!.Value.Key);
        Assert.Equal(Palette.KeyOf("https://i.scdn.co/image/" + MixArt).ToString(), pe.Mix!.Value.Key);
    }

    [Fact]
    public void TheSourceCards_AreTheSameEntities_WhateverTheSectionIsCalled()
    {
        var en = English();
        var ko = Korean();

        Assert.Equal("spotify:playlist:HERO", en.HeroUri);
        Assert.Equal("spotify:playlist:DW", en.WeeklyUri);
        Assert.Equal("spotify:playlist:M1", en.MixUri);
        Assert.Equal((en.HeroUri, en.WeeklyUri, en.MixUri), (ko.HeroUri, ko.WeeklyUri, ko.MixUri));

        // The cards genuinely carry the localized copy — the selector simply never reads it.
        Assert.NotEqual(en.HeroTitle, ko.HeroTitle);
        Assert.Equal(en.MixPlaneUrl, ko.MixPlaneUrl);
        Assert.Equal("https://i.scdn.co/image/" + MixArt, en.MixPlaneUrl);
    }
}
