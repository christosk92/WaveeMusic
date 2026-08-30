using System;
using System.Text.Json;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The shell wash on a KOREAN account must be the same three colours as on an English one. Nothing in the pipeline is
// allowed to read copy: the composer routes on `__typename` + the `format` token, and HomeWashSource selects by
// kind + ordinal and resolves from the payload accent / the graded cover. This drives BOTH halves end to end — two
// fixtures that are byte-identical except for every human-readable string in them — because "selection never inspects a
// title" is exactly the kind of invariant that a single well-meaning `title.Contains("Mix")` quietly ends.
//
// The fixture shape is the composer harness from SpotifyHomeComposerTests: inline home JSON, composed for real, then
// wrapped as a HomeFeed and pushed through the real selector.
public sealed class HomeWashLocaleTests
{
    // Three distinct 40-char Spotify image ids (16-char size prefix + 24-char artwork identity).
    const string HeroArt = "ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa";
    const string WeeklyArt = "ab67616d0000b273bbbbbbbbbbbbbbbbbbbbbbbb";
    const string MixArt = "ab67616d0000b273cccccccccccccccccccccccc";

    const string HeroAccent = "#1E3A5F";
    const string WeeklyAccent = "#7A2E12";
    // The mix card deliberately carries NO server accent, so its slot has to fall through to the graded cover — the
    // tier-2 path is keyed on the artwork url, which is another thing a localized feed must not move.
    static CoverColorPlane.Scheme Graded => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    // ── the fixture ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>One home response whose STRUCTURE is fixed and whose COPY is entirely parameterized.</summary>
    static string Feed(
        string heroSection, string weeklySection, string mixSection, string mixBaseText,
        string heroName, string weeklyName, string radarName, string mix1Name, string mix2Name)
        => "{ \"sectionContainer\": { \"sections\": { \"items\": ["
            // Spotlight is the Hero source by SECTION TYPE — its title is never consulted.
            + "{ \"data\": { \"__typename\": \"HomeSpotlightSectionData\","
            + " \"title\": { \"transformedLabel\": \"" + heroSection + "\" } },"
            + " \"sectionItems\": { \"items\": ["
            + Item("spotify:playlist:HERO", heroName, "", HeroArt, HeroAccent)
            + "] } },"
            // …the weekly 2-up and the mix band are routed by the `format` token on each card.
            + Generic(weeklySection, null,
                Item("spotify:playlist:DW", weeklyName, "discover-weekly", WeeklyArt, WeeklyAccent),
                Item("spotify:playlist:RR", radarName, "release-radar", MixArt, WeeklyAccent))
            + ","
            + Generic(mixSection, mixBaseText,
                Item("spotify:playlist:M1", mix1Name, "daily-mix", MixArt, null),
                Item("spotify:playlist:M2", mix2Name, "daily-mix", HeroArt, null))
            + "] } } }";

    static string Generic(string title, string? baseText, params string[] items)
        => "{ \"data\": { \"__typename\": \"HomeGenericSectionData\", \"title\": { \"transformedLabel\": \"" + title + "\""
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

    static HomeFeed Compose(string json) =>
        new("", SpotifyHomeComposer.Compose(JsonDocument.Parse(json).RootElement, Array.Empty<PlaylistSummary>()).Groups);

    static HomeFeed English => Compose(Feed(
        heroSection: "Spotlight for you", weeklySection: "New music every week",
        mixSection: "Made For Christos", mixBaseText: "Made For {0}",
        heroName: "Daily drive", weeklyName: "Discover Weekly", radarName: "Release Radar",
        mix1Name: "Daily Mix 1", mix2Name: "Daily Mix 2"));

    static HomeFeed Korean => Compose(Feed(
        heroSection: "당신을 위한 스포트라이트", weeklySection: "매주 만나는 새로운 음악",
        mixSection: "나를 위한 데일리 믹스", mixBaseText: "{0}님을 위한 믹스",
        heroName: "데일리 드라이브", weeklyName: "디스커버 위클리", radarName: "릴리스 레이더",
        mix1Name: "믹스 1", mix2Name: "믹스 2"));

    // ── the contract ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TheWash_IsIdenticalOnAKoreanAccount()
    {
        var en = English;
        var ko = Korean;

        // Guard the fixture first: if the two feeds ever stopped differing in copy, everything below would pass for the
        // wrong reason.
        Assert.NotEqual(Titles(en), Titles(ko));
        Assert.NotEqual(CardTitles(en), CardTitles(ko));

        CoverColorPlane.Scheme? Plane(string? url) => url is not null && url.Contains(MixArt, StringComparison.Ordinal) ? Graded : null;

        var pe = HomeWashSource.Select(en, Plane);
        var pk = HomeWashSource.Select(ko, Plane);

        // Same three legs — same colours AND same artwork identities, so the shell would not even cross-fade between the
        // two accounts, let alone repaint.
        Assert.Equal(pe, pk);
        Assert.Equal(HomeWashSource.Fingerprint(pe), HomeWashSource.Fingerprint(pk));

        // …and the fixture really exercises all three slots and both resolution tiers.
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(0xFF1E3A5Fu)) with { A = 1f }, pe.Hero!.Value.Color);
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(0xFF7A2E12u)) with { A = 1f }, pe.Weekly!.Value.Color);
        Assert.Equal(WaveePalette.ChromeAccent(Graded) with { A = 1f }, pe.Mix!.Value.Color);   // tier 2: the graded cover
        Assert.Equal(CoverColorPlane.KeyForUrl("https://i.scdn.co/image/" + HeroArt), pe.Hero!.Value.Key);
        Assert.Equal(CoverColorPlane.KeyForUrl("https://i.scdn.co/image/" + WeeklyArt), pe.Weekly!.Value.Key);
        Assert.Equal(CoverColorPlane.KeyForUrl("https://i.scdn.co/image/" + MixArt), pe.Mix!.Value.Key);
    }

    [Fact]
    public void TheSourceCards_AreTheSameEntities_WhateverTheSectionIsCalled()
    {
        var en = HomeWashSource.Sources(English);
        var ko = HomeWashSource.Sources(Korean);

        Assert.Equal("spotify:playlist:HERO", en.Hero!.Uri);
        Assert.Equal("spotify:playlist:DW", en.Weekly!.Uri);
        Assert.Equal("spotify:playlist:M1", en.Mix!.Uri);
        Assert.Equal((en.Hero!.Uri, en.Weekly!.Uri, en.Mix!.Uri), (ko.Hero!.Uri, ko.Weekly!.Uri, ko.Mix!.Uri));

        // The cards genuinely carry the localized copy — the selector simply never reads it.
        Assert.NotEqual(en.Hero!.Title, ko.Hero!.Title);
        // The one slot still waiting on a grading is the same artwork in both locales.
        Assert.Equal(HomeWashSource.PlaneUrl(en.Mix), HomeWashSource.PlaneUrl(ko.Mix));
        Assert.Equal("https://i.scdn.co/image/" + MixArt, HomeWashSource.PlaneUrl(en.Mix));
    }

    static string Titles(HomeFeed feed) => string.Join("|", System.Linq.Enumerable.Select(feed.Groups, g => g.Title ?? ""));

    static string CardTitles(HomeFeed feed) => string.Join("|",
        System.Linq.Enumerable.Select(
            System.Linq.Enumerable.SelectMany(feed.Groups, g => g.Cards), c => c.Title));
}
