using System;
using Wavee.Backend.Playlists;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

/// <summary>The daylist rollover window off the playlist4 header (<see cref="PlaylistFetcher.DaylistWindowOf"/>):
/// only the <c>daylist</c> format yields a window, and the instant parse accepts every plausible wire shape (the
/// Pathfinder home feed states these attributes as ISO-8601; the playlist4 shape for this format is unpinned by any
/// capture, so an epoch in seconds or ms must come out as the same window).</summary>
public class DaylistWindowTests
{
    static Pl.ListAttributes Attrs(string? format, params (string Key, string Value)[] pairs)
    {
        var a = new Pl.ListAttributes();
        if (format is not null) a.Format = format;
        foreach (var (k, v) in pairs)
            a.FormatAttributes.Add(new Pl.FormatListAttribute { Key = k, Value = v });
        return a;
    }

    static readonly long ExpiresMs = new DateTimeOffset(2026, 8, 12, 1, 58, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    static readonly long CreatedMs = new DateTimeOffset(2026, 8, 11, 3, 58, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Daylist_IsoInstants_MapToUnixMs()
    {
        var (expires, created) = PlaylistFetcher.DaylistWindowOf(Attrs("daylist",
            ("expires", "2026-08-12T01:58:00Z"), ("created", "2026-08-11T03:58:00Z")));
        Assert.Equal(ExpiresMs, expires);
        Assert.Equal(CreatedMs, created);
    }

    [Fact]
    public void Daylist_EpochSecondsAndMs_MapToTheSameWindow()
    {
        var fromSeconds = PlaylistFetcher.DaylistWindowOf(Attrs("daylist",
            ("expires", (ExpiresMs / 1000L).ToString()), ("created", (CreatedMs / 1000L).ToString())));
        var fromMs = PlaylistFetcher.DaylistWindowOf(Attrs("daylist",
            ("expires", ExpiresMs.ToString()), ("created", CreatedMs.ToString())));
        Assert.Equal((ExpiresMs, CreatedMs), fromSeconds);
        Assert.Equal((ExpiresMs, CreatedMs), fromMs);
    }

    [Fact]
    public void NonDaylistFormats_YieldNoWindow()
    {
        // The keys may exist on other algotorial formats — only the daylist owns this UI, so only it maps.
        Assert.Equal((0L, 0L), PlaylistFetcher.DaylistWindowOf(Attrs("daily-mix",
            ("expires", "2026-08-12T01:58:00Z"))));
        Assert.Equal((0L, 0L), PlaylistFetcher.DaylistWindowOf(Attrs(null,
            ("expires", "2026-08-12T01:58:00Z"))));
    }

    [Fact]
    public void MissingOrUnparsableValues_YieldZero_NotAThrow()
    {
        Assert.Equal((0L, 0L), PlaylistFetcher.DaylistWindowOf(Attrs("daylist")));
        var (expires, created) = PlaylistFetcher.DaylistWindowOf(Attrs("daylist",
            ("expires", "not-a-time"), ("created", "")));
        Assert.Equal(0L, expires);
        Assert.Equal(0L, created);
    }

    /// <summary>The header mapper's output shape: the window lands on the <c>Playlist</c> record's daylist fields and
    /// defaults to zero — the store's JSON codec round-trips the record, so an old persisted header (no fields) reads
    /// back as "no window" rather than failing.</summary>
    [Fact]
    public void PlaylistRecord_CarriesTheWindow_AndDefaultsToZero()
    {
        var p = new Wavee.Core.Playlist("id", "spotify:playlist:id", "daylist", null, "spotify", null, 0,
            DaylistExpiresAtMs: ExpiresMs, DaylistCreatedAtMs: CreatedMs);
        Assert.Equal(ExpiresMs, p.DaylistExpiresAtMs);
        Assert.Equal(CreatedMs, p.DaylistCreatedAtMs);
        var bare = new Wavee.Core.Playlist("id", "spotify:playlist:id", "x", null, "o", null, 0);
        Assert.Equal(0L, bare.DaylistExpiresAtMs);
        Assert.Equal(0L, bare.DaylistCreatedAtMs);
    }

    [Fact]
    public void PlaylistSummary_CarriesTheHomePayloadAccent_AndDefaultsToZero()
    {
        var p = new Wavee.Core.PlaylistSummary("spotify:playlist:id", "daylist", "Spotify", 50, null,
            DaylistExpiresAtMs: ExpiresMs, DaylistCreatedAtMs: CreatedMs, Accent: 0xFFE6B42Au);
        Assert.Equal(0xFFE6B42Au, p.Accent);
        Assert.Equal(0u, new Wavee.Core.PlaylistSummary("spotify:playlist:id", "x", "o", 0, null).Accent);
    }
}
