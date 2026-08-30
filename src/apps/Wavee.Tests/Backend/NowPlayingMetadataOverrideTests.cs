using System;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The NOW-PLAYING override: the ICY <c>StreamTitle</c> a station pushes mid-stream, or a module's
/// <c>playback/metadata</c>. Folded onto <see cref="IPlaybackState.CurrentTrack"/> on READ (it is a fact about the
/// BROADCAST, not about a catalogue entity, so it is never written into the store), scoped to one playable uri, and
/// dropped the moment the track changes.
/// </summary>
public class NowPlayingMetadataOverrideTests
{
    const string RadioUri = "wavee:module:wavee.radio:aGVsbG8";
    const string SongUri = "spotify:track:abc";

    static NowPlayingProjection New() => new("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());

    static Track TrackFor(string uri, string title = "Some station") => new(
        Id: uri, Uri: uri, Title: title, Artists: [new ArtistRef("", "", "Original artist")],
        Album: new AlbumRef("", "", ""), DurationMs: 0, IsExplicit: false, Image: null);

    static void Start(NowPlayingProjection p, string uri, string title = "Some station")
        => p.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(uri, title), 0));

    [Fact]
    public void Override_ReplacesTheTitleAndArtistOfItsOwnPlayable()
    {
        using var p = New();
        Start(p, RadioUri);

        p.SetMetadataOverride(RadioUri, "A Song", "A Band");

        Assert.Equal("A Song", p.CurrentTrack?.Title);
        Assert.Equal("A Band", p.CurrentTrack?.Artists[0].Name);
        Assert.Equal(("A Song", "A Band"), p.MetadataOverride);
    }

    [Fact]
    public void Override_KeepsTheUri_SoNothingDownstreamReidentifiesTheRow()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");
        Assert.Equal(RadioUri, p.CurrentTrack?.Uri);
    }

    [Fact]
    public void Override_ForAnotherPlayable_DoesNotApply()
    {
        using var p = New();
        Start(p, SongUri, "Real title");
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");
        Assert.Equal("Real title", p.CurrentTrack?.Title);
    }

    [Fact]
    public void Override_IsDroppedAtTheNextTrackChange()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");

        Start(p, SongUri, "Real title");

        Assert.Equal("Real title", p.CurrentTrack?.Title);
        Assert.Equal((null, null), p.MetadataOverride);
    }

    [Fact]
    public void NullTitle_LeavesTheCatalogueTitleAlone_ButStillSetsTheArtist()
    {
        using var p = New();
        Start(p, RadioUri, "Some station");

        p.SetMetadataOverride(RadioUri, null, "A Band");

        Assert.Equal("Some station", p.CurrentTrack?.Title);
        Assert.Equal("A Band", p.CurrentTrack?.Artists[0].Name);
    }

    [Fact]
    public void ClearingIt_RestoresTheCatalogueRow()
    {
        using var p = New();
        Start(p, RadioUri, "Some station");
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");
        p.SetMetadataOverride(RadioUri, null, null);

        Assert.Equal("Some station", p.CurrentTrack?.Title);
        Assert.Equal("Original artist", p.CurrentTrack?.Artists[0].Name);
    }

    [Fact]
    public void SettingTheSameValueTwice_FiresOnce()
    {
        using var p = New();
        Start(p, RadioUri);
        int changes = 0;
        using IDisposable sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(_ => changes++));

        p.SetMetadataOverride(RadioUri, "A Song", "A Band");
        int afterFirst = changes;
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");

        Assert.Equal(afterFirst, changes);
    }

    [Fact]
    public void Override_SurvivesAPositionFold()
    {
        using var p = New();
        Start(p, RadioUri);
        p.SetMetadataOverride(RadioUri, "A Song", "A Band");

        p.OnEvent(new PlaybackEvent(EvKind.Resumed, null, 4321));

        Assert.Equal("A Song", p.CurrentTrack?.Title);
    }
}
