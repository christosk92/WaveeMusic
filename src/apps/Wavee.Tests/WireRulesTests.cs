// ── Wavee.Tests/WireRulesTests.cs — the outbound-request log's pure half ────────────────────────────────────────────
//
// 2026-09-18: `/collection/v2/delta` looped at hundreds of successful calls a minute and the log said nothing, because
// only failures were logged. `WireRules` names an endpoint for counting and decides when a run of calls is a storm.

using Wavee;

using Xunit;

namespace Wavee.Tests;

public class WireRulesTests
{
    [Theory]
    [InlineData("POST", "gew4-spclient.spotify.com", "/collection/v2/delta", "POST gew4-spclient.spotify.com/collection/v2/delta")]
    [InlineData("GET", "spclient.wg.spotify.com", "/metadata/4/track/0123456789abcdef0123456789abcdef", "GET spclient.wg.spotify.com/metadata/4/track/{id}")]
    [InlineData("GET", "audio-fa.spotifycdn.com", "/audio/dda38684833edaad028fbead1a75730e9c56777b", "GET audio-fa.spotifycdn.com/audio/{id}")]
    [InlineData("PUT", "h", "/connect-state/v1/devices/9cf0d968aabbccddeeff00112233445566778899", "PUT h/connect-state/v1/devices/{id}")]
    [InlineData("GET", "h", "/", "GET h/")]
    public void An_endpoint_is_its_path_with_the_ids_folded(string method, string host, string path, string expected)
        => Assert.Equal(expected, WireRules.EndpointOf(method, host, path));

    [Fact]
    public void Short_words_and_version_numbers_are_not_ids()
        => Assert.Equal("GET h/pathfinder/v2/query", WireRules.EndpointOf("GET", "h", "/pathfinder/v2/query"));

    [Fact]
    public void Forty_calls_inside_a_minute_is_a_storm_and_says_so_once_per_forty()
    {
        var w = new WireRules.Window();
        int storms = 0;
        for (int i = 0; i < 120; i++) if (w.Note(1_000 + i * 100) > 0) storms++;      // 10 a second
        Assert.Equal(3, storms);                                                       // at 40, 80, 120 — never per call
        Assert.Equal(120, w.Total);
    }

    [Fact]
    public void The_same_forty_calls_spread_over_an_hour_are_not()
    {
        var w = new WireRules.Window();
        for (int i = 0; i < 200; i++) Assert.Equal(0, w.Note(i * 90_000L));            // one every 90 s
    }

    [Fact]
    public void Fewer_than_forty_calls_is_never_a_storm_however_fast()
    {
        var w = new WireRules.Window();
        for (int i = 0; i < WireRules.StormCalls - 1; i++) Assert.Equal(0, w.Note(i));
    }
}
