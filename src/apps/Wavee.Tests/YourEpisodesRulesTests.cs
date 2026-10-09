// ── Wavee.Tests/YourEpisodesRulesTests.cs — "is this playlist Your Episodes" ──────────────────────────────────────────
//
// Spotify's Your Episodes is a playlist on the wire (format "listen-later"). A pinned one opened a playlist page instead
// of the Podcasts library's Your Episodes view, because nothing recognised it. The rule: the list's own format, or —
// before this session has read the list — the account's known listen-later uri.

using Xunit;

namespace Wavee.Tests;

public class YourEpisodesRulesTests
{
    const string Saved = "spotify:playlist:listenlater";

    [Fact]
    public void TheListenLaterFormat_IsYourEpisodes_WhateverTheUri()
        => Assert.True(YourEpisodesRules.Is(PlaylistFormat.ListenLater, "spotify:playlist:other", ""));

    [Fact]
    public void AnUnreadList_IsYourEpisodes_WhenItsUriIsTheKnownListenLaterUri()
        => Assert.True(YourEpisodesRules.Is(PlaylistFormat.None, Saved, Saved));

    [Theory]
    [InlineData(PlaylistFormat.None)]
    [InlineData(PlaylistFormat.Other)]
    [InlineData(PlaylistFormat.Editorial)]
    public void AnyOtherList_IsNot(PlaylistFormat format)
        => Assert.False(YourEpisodesRules.Is(format, "spotify:playlist:mine", Saved));

    [Fact]
    public void NoKnownUri_NeverMatchesAnEmptyUri()
        => Assert.False(YourEpisodesRules.Is(PlaylistFormat.None, "", ""));
}
