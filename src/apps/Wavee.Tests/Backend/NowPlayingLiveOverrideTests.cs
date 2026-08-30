using System;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The LIVE-ness override: scoped to one playable, folded into <see cref="IPlaybackState.IsLive"/>, dropped at the next
/// track change, and — while it applies — it is what makes <see cref="IPlaybackState.CanSeek"/> false. Live-ness is a
/// fact the SOURCE stated; it is deliberately NOT inferred from a 0 duration, which is also what "unknown" looks like.
/// </summary>
public class NowPlayingLiveOverrideTests
{
    const string RadioUri = "wavee:module:wavee.radio:aGVsbG8";
    const string SongUri = "spotify:track:abc";

    static NowPlayingProjection New() => new("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());

    static Track TrackFor(string uri, long durationMs = 0) => new(
        Id: uri, Uri: uri, Title: "t", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: durationMs, IsExplicit: false, Image: null);

    static void Start(NowPlayingProjection p, string uri, long durationMs = 0)
        => p.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(uri, durationMs), 0));

    [Fact]
    public void Default_IsNotLive()
    {
        using var p = New();
        Start(p, SongUri, 200_000);
        Assert.False(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void LiveOverride_AppliesToItsOwnPlayable_AndDisablesSeeking()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetLiveOverride(RadioUri, true);

        Assert.True(p.IsLive);
        Assert.False(p.CanSeek);
    }

    [Fact]
    public void LiveOverride_ForAnotherPlayable_DoesNotApply()
    {
        using var p = New();
        Start(p, SongUri, 200_000);
        p.SetLiveOverride(RadioUri, true);

        Assert.False(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void LiveOverride_IsDroppedAtTheNextTrackChange()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetLiveOverride(RadioUri, true);
        Assert.True(p.IsLive);

        Start(p, SongUri, 200_000);

        Assert.False(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void LiveOverride_ClearsExplicitly()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetLiveOverride(RadioUri, true);
        p.SetLiveOverride(RadioUri, false);
        Assert.False(p.IsLive);
    }

    [Fact]
    public void LiveOverride_SurvivesAPositionFold()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetLiveOverride(RadioUri, true);

        p.OnEvent(new PlaybackEvent(EvKind.Resumed, null, 1234));

        Assert.True(p.IsLive);
        Assert.False(p.CanSeek);
    }

    [Fact]
    public void SettingTheSameValueTwice_FiresOnce()
    {
        using var p = New();
        Start(p, RadioUri);
        int changes = 0;
        using IDisposable sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(_ => changes++));

        p.SetLiveOverride(RadioUri, true);
        int afterFirst = changes;
        p.SetLiveOverride(RadioUri, true);

        Assert.Equal(afterFirst, changes);
    }
}
