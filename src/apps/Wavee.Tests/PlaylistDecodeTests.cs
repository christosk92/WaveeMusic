// ── Wavee.Tests/PlaylistDecodeTests.cs — WP-5.O stream A ────────────────────────────────────────────────────────────
// Spotify.Decode.Playlist.cs: the liked-songs content filters (0.2.9 ContentFilterParserTests' facts, staged), the
// playlist4 format_attributes fold (0.2.9 PlaylistFetcher.DaylistWindowOf / ChartInfoOf / TuningOf — the answer to "what
// did 0.2.9 use instead of a fetchPlaylist hash") landed through the real commit, and the extender's JSON → track rows.
// No captured fixtures exist in _old for these three answers, so the inputs are built here from the wire shapes.

using System;
using System.Collections.Generic;
using System.Text;
using FluentGpu.Foundation;
using Google.Protobuf;
using Wavee;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistDecodeTests
{
    const string Me = "spotify:user:me";

    static string Gid(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 11), buf);
        return new string(buf);
    }

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    // ══ 1. content filters ═══════════════════════════════════════════════════════════════════════════════════════════

    static List<(string Title, string Token)> Chips(string json)
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.LikedContentFilters(U(json), U(Me), s);
        var list = new List<(string, string)>();
        for (int i = 0; i < s.ContentFilters.Count; i++)
        {
            ref var row = ref s.ContentFilters[i];
            Assert.False(row.Owner.IsEmpty);
            list.Add((Encoding.UTF8.GetString(s.Utf8(row.Title)), Encoding.UTF8.GetString(s.Utf8(row.Token))));
        }
        Staging.Return(s);
        return list;
    }

    [Fact]
    public void ContentFilters_StageTitleAndToken_InServerOrder()
    {
        var chips = Chips("""
        { "contentFilters": [
            { "title": "Mellow",     "query": "tags contains mellow" },
            { "title": "K-Pop",      "query": "tags contains k-pop" },
            { "title": "Energetic",  "query": "tags contains energetic" }
          ] }
        """);
        Assert.Equal([("Mellow", "mellow"), ("K-Pop", "k-pop"), ("Energetic", "energetic")], chips);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"contentFilters": null}""")]
    [InlineData("""{"contentFilters": "nope"}""")]
    public void AnAnswerWithNoUsableChip_IsTheKnownEmptySet(string json)
        => Assert.Equal([("", "")], Chips(json));

    [Fact]
    public void UnsupportedQueryForms_AndIncompleteEntries_AreDropped()
    {
        var chips = Chips("""
        { "contentFilters": [
            { "title": "Good",  "query": "tags contains good" },
            { "title": "Weird", "query": "popularity > 50" },
            { "title": "OnlyTitle" },
            { "query": "tags contains orphan" }
          ] }
        """);
        Assert.Equal([("Good", "good")], chips);
    }

    [Fact]
    public void DuplicateTokensCollapse_AndAQuotedTokenIsUnquoted()
    {
        var chips = Chips("""
        { "contentFilters": [
            { "title": "Chill",  "query": "tags contains chill" },
            { "title": "Chill!", "query": "tags contains CHILL" },
            { "title": "Lo-fi",  "query": "  tags contains \"lo-fi\"  " }
          ] }
        """);
        Assert.Equal([("Chill", "chill"), ("Lo-fi", "lo-fi")], chips);
    }

    // ══ 2. format_attributes ═════════════════════════════════════════════════════════════════════════════════════════

    static byte[] Revision24(byte fill)
    {
        var r = new byte[24];
        r[3] = 7;                                   // counter 7
        for (int i = 4; i < 24; i++) r[i] = fill;
        return r;
    }

    static Pl.FormatListAttribute Attr(string key, string value) => new() { Key = key, Value = value };

    static Playlist Land(Pl.SelectedListContent content, string uri)
    {
        var s = Staging.Rent();
        Spotify.Decode.PlaylistFormatAttributes(content.ToByteArray(), U(uri), s);
        TestScope.CommitAndPublish(s);
        return Entities.Playlist(EntityId.Parse(uri.AsSpan()));
    }

    [Fact]
    public void Daylist_TheWindowLands_InUnixSeconds_WhateverSpellingTheWireUsed()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:" + Gid(1);
        var p = Land(new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(1)),
            Attributes = new Pl.ListAttributes
            {
                Format = "daylist",
                FormatAttributes = { Attr("expires", "1700003600000"), Attr("created", "2023-11-14T22:13:20Z") },
            },
        }, uri);

        Assert.True(p.Knows(PlaylistFields.Daylist | PlaylistFields.Chart | PlaylistFields.Tuning));
        Assert.Equal(1_700_003_600, p.DaylistExpiresAt);        // epoch ms → seconds
        Assert.Equal(1_700_000_000, p.DaylistCreatedAt);        // ISO-8601 → seconds
        Assert.Equal(0, p.ChartNewEntries);
        Assert.True(p.TuningOptions.IsEmpty);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.PlaylistTuning.State(p.Slot));   // "not tunable" is an answer
    }

    [Fact]
    public void Chart_TheHeaderFactsLand_AndAnotherFormatIsZeroedNotUnknown()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:" + Gid(2);
        var p = Land(new Pl.SelectedListContent
        {
            Attributes = new Pl.ListAttributes
            {
                Format = "chart",
                FormatAttributes = { Attr("new_entries_count", "7"), Attr("last_updated", "2023-11-14T22:13:20Z"), Attr("rank_type", "weekly"),
                                     Attr("expires", "1700003600") },   // a daylist key on a chart is not a daylist
            },
        }, uri);

        Assert.Equal(7, p.ChartNewEntries);
        Assert.Equal(1_700_000_000, p.ChartUpdatedAt);
        Assert.Equal("weekly", Entities.Strings.Resolve(p.ChartRankTypeId));
        Assert.Equal(0, p.DaylistExpiresAt);
        Assert.True(p.Knows(PlaylistFields.Daylist));
    }

    [Fact]
    public void Tuning_OptionsLabelsSelectionAndRevision_Land_AndAReplyWithoutSignalsClearsThem()
    {
        const string choiceA = "session_control_display$mix$more_discovery";
        const string choiceB = "session_control_display$mix$soft_pop:nl_genre";
        const string reset = "session-control-reset";
        TestScope.Fresh();
        string uri = "spotify:playlist:" + Gid(3);
        var content = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(0xAB)),
            Attributes = new Pl.ListAttributes
            {
                Format = "inspiredby-mix",
                FormatAttributes =
                {
                    Attr("session_control.selected_signals", choiceB),
                    Attr("session_control_display.displayName.more_discovery", "More discovery tracks"),
                    Attr("session_control_display.displayName.soft_pop:nl_genre", "Make it more soft pop"),
                },
            },
            Contents = new Pl.ListItems
            {
                Pos = 0, Truncated = false,
                AvailableSignals = { new Pl.AvailableSignal { Identifier = choiceA }, new Pl.AvailableSignal { Identifier = choiceB },
                                     new Pl.AvailableSignal { Identifier = reset }, new Pl.AvailableSignal { Identifier = "  " } },
            },
        };
        var p = Land(content, uri);

        var options = p.TuningOptions.ToArray();
        Assert.Equal(3, options.Length);
        Assert.Equal("More discovery tracks", Entities.Strings.Resolve(options[0].DisplayName));
        Assert.Equal("Make it more soft pop", Entities.Strings.Resolve(options[1].DisplayName));
        Assert.Equal((byte)TuningOptionKind.Reset, options[2].Kind);
        Assert.True(options[2].DisplayName.IsEmpty);
        Assert.Equal(choiceB, Entities.Strings.Resolve(p.TuningSelectedId));

        Span<char> text = stackalloc char[128];
        int n = Spotify.Api.FormatRevision(Revision24(0xAB), text);
        Assert.Equal(new string(text[..n]), Entities.Strings.Resolve(p.RevisionId));
        Assert.Equal(Playlist.RevisionHash(text[..n]), p.TuningRevision);
        Assert.True(p.TuningCurrent);

        // A later read with no signals: the list is known-EMPTY and the selection is gone.
        content.Contents.AvailableSignals.Clear();
        p = Land(content, uri);
        Assert.True(p.TuningOptions.IsEmpty);
        Assert.True(p.TuningSelectedId.IsEmpty);
    }

    [Fact]
    public void ADiffWithNoAttributes_SaysNothingAboutTheseGroups()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:" + Gid(4);
        var p = Land(new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Revision24(2)) }, uri);
        Assert.False(p.Knows(PlaylistFields.Daylist));
        Assert.False(p.Knows(PlaylistFields.Tuning));
    }

    [Theory]
    [InlineData("1700000000", 1_700_000_000)]
    [InlineData("1700000000123", 1_700_000_000)]
    [InlineData("2023-11-14T22:13:20+00:00", 1_700_000_000)]
    [InlineData("soon", 0)]
    [InlineData("", 0)]
    public void Instants_ReadEpochSecondsMillisecondsOrIso(string wire, int seconds)
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:" + Gid(5);
        var p = Land(new Pl.SelectedListContent
        {
            Attributes = new Pl.ListAttributes { Format = "daylist", FormatAttributes = { Attr("expires", wire.Length == 0 ? " " : wire) } },
        }, uri);
        Assert.Equal(seconds, p.DaylistExpiresAt);
    }

    // ══ 3. the extender ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Extender_StagesTrackRowsArtistsAndAlbums_AndAnswersTheUrisInOrder()
    {
        TestScope.Fresh();
        string t1 = Gid(10), t2 = Gid(11), artist = Gid(12), album = Gid(13);
        string json = "{ \"recommendedTracks\": [ "
            + "{ \"id\": \"" + t1 + "\", \"name\": \"First\", \"duration\": 183000, \"explicit\": true, "
            + "  \"artists\": [ { \"id\": \"" + artist + "\", \"name\": \"Artist\" } ], "
            + "  \"album\": { \"id\": \"" + album + "\", \"name\": \"Album\", \"imageUrl\": \"https://i.scdn.co/image/ab67\" } }, "
            + "{ \"id\": \"not-a-gid\", \"name\": \"Dropped\" }, "
            + "{ \"id\": \"" + t2 + "\", \"name\": \"Second\", \"duration\": 90000 } ] }";
        var uris = new List<string>();
        var s = Staging.Rent();
        Spotify.Decode.PlaylistExtender(U(json), s, uris);
        TestScope.CommitAndPublish(s);

        Assert.Equal(["spotify:track:" + t1, "spotify:track:" + t2], uris);
        var first = Entities.Track(EntityId.Parse(uris[0].AsSpan()));
        Assert.Equal("First", first.Title);
        Assert.Equal(183000, first.DurationMs);
        Assert.True(first.IsExplicit);
        Assert.Equal("Artist", Entities.Strings.Resolve(first.ArtistLineId));
        Assert.True(first.Album.IsValid);
        Assert.Single(first.ArtistSlots.ToArray());
    }

    [Fact]
    public void Extender_AMalformedBody_StagesNothing()
    {
        TestScope.Fresh();
        var uris = new List<string>();
        var s = Staging.Rent();
        Spotify.Decode.PlaylistExtender(U("{ \"recommendedTracks\": [ { \"id\": "), s, uris);
        Staging.Return(s);
        Assert.Empty(uris);
    }
}
