// ── Wavee.Tests/LocalPlayablesTests.cs — WP-5.O stream A ────────────────────────────────────────────────────────────
// 0.2.9's LocalMediaProviderTests facts that belong to the PURE half (PlayableUri round trip, TitleOf, ClassifyDrop),
// ported against Entities/Playlist.cs §9, plus the 0.3 import: a dropped file becomes a COMPLETE local track row appended
// to the Local Files membership (G-110). The provider/stream facts stay with WP-6.T (Playback.Audio's LocalSource).

using System;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LocalPlayablesTests
{
    [Theory]
    [InlineData(@"C:\Music\Sigur Rós\01 Svefn-g-englar.flac")]
    [InlineData(@"\\nas\share\a b\c+d=e.mp3")]
    [InlineData("/home/user/музыка/трек.ogg")]
    public void LocalFileUri_RoundTripsAnyPath_ThroughOneColonFreeToken(string path)
    {
        TestScope.Fresh();
        var uri = Playlist.LocalFileUri(path);

        Assert.StartsWith(Playlist.LocalFilePrefix, uri, StringComparison.Ordinal);
        var payload = uri[Playlist.LocalFilePrefix.Length..];
        Assert.DoesNotContain(':', payload);
        Assert.DoesNotContain('/', payload);
        Assert.DoesNotContain('+', payload);
        Assert.DoesNotContain('=', payload);

        var id = EntityId.Parse(uri.AsSpan());
        Assert.Equal(EntityProvider.Local, id.Provider);
        Assert.Equal(EntityKind.Track, id.Kind);                  // a local file is a PLAYABLE: Track kind (Entities.cs)
        Assert.Equal(path, Playlist.LocalPathOf(id));
    }

    [Fact]
    public void LocalPathOf_RejectsForeignAndMalformedIdentities_WithoutThrowing()
    {
        TestScope.Fresh();
        Assert.Null(Playlist.LocalPathOf(EntityId.Parse("spotify:track:abc".AsSpan())));
        Assert.Null(Playlist.LocalPathOf(EntityId.Parse("wavee:local:track:3".AsSpan())));        // local, but not a file
        Assert.Null(Playlist.LocalPathOf(EntityId.Parse((Playlist.LocalFilePrefix + "!!!!").AsSpan())));
        Assert.Null(Playlist.LocalPathOf(default));
    }

    [Fact]
    public void TitleOf_IsTheFileNameWithoutItsExtension_OrAUrlsLastSegment()
    {
        Assert.Equal("Boards of Canada - Roygbiv", Playlist.LocalTitleOf(@"C:\Music\Boards of Canada - Roygbiv.flac"));
        Assert.Equal("live set", Playlist.LocalTitleOf(@"D:\clips\live set.mp4"));
        Assert.Equal("ep12.mp3", Playlist.LocalTitleOf("https://cdn.test/shows/ep12.mp3?token=abc"));
        Assert.Equal("", Playlist.LocalTitleOf("   "));
    }

    [Fact]
    public void DropClassification_PrefersAudio_ThenMp4_ThenRefuses()
    {
        Assert.Equal(Playlist.LocalDropAction.PlayAudio, Playlist.ClassifyDrop([@"C:\a.flac"], out var a));
        Assert.Equal(@"C:\a.flac", a);

        Assert.Equal(Playlist.LocalDropAction.PlayVideo, Playlist.ClassifyDrop([@"C:\readme.txt", @"C:\v.mp4"], out var v));
        Assert.Equal(@"C:\v.mp4", v);

        // A mixed drop is not an error — the unambiguous "play this song" gesture wins.
        Assert.Equal(Playlist.LocalDropAction.PlayAudio, Playlist.ClassifyDrop([@"C:\v.mp4", @"C:\a.mp3"], out var m));
        Assert.Equal(@"C:\a.mp3", m);

        Assert.Equal(Playlist.LocalDropAction.None, Playlist.ClassifyDrop([@"C:\a.m4a", @"C:\b.txt"], out _));
        Assert.Equal(Playlist.LocalDropAction.None, Playlist.ClassifyDrop(null, out _));
        Assert.Equal(Playlist.LocalDropAction.None, Playlist.ClassifyDrop(Array.Empty<string>(), out _));
    }

    [Theory]
    [InlineData(@"C:\a.MP3", true)]
    [InlineData(@"C:\a.ogg", true)]
    [InlineData(@"C:\a.FLAC", true)]
    [InlineData(@"C:\a.m4a", false)]
    [InlineData(@"C:\a.wav", false)]
    [InlineData(@"C:\a.mp4", false)]
    [InlineData(null, false)]
    public void TheAudioExtensionGate_IsCaseInsensitive(string? path, bool audio)
        => Assert.Equal(audio, Playlist.IsLocalAudioFile(path));

    /// <summary>G-110: a dropped file is imported as a COMPLETE row (stated availability, the file name as title, the
    /// Local flag) and appended to the Local Files membership, which a remote never has to answer. A second drop of the
    /// same file does not add a second row.</summary>
    [Fact]
    public void Import_AppendsACompleteLocalRow_ToTheLocalFilesMembership_Once()
    {
        TestScope.Fresh();
        var track = Playlist.ImportLocalFile(@"C:\Music\Intro.mp3");

        Assert.True(track.IsValid);
        Assert.True(track.IsLocal);
        Assert.True(track.Knows(TrackFields.Identity | TrackFields.Availability));
        Assert.Equal("Intro", track.Title);
        var local = Playlist.LocalFiles;
        Assert.Equal(EdgeState.Complete, local.MembershipState);
        Assert.Equal(track.Slot, Assert.Single(local.TrackSlots.ToArray()));
        Assert.True(local.Knows(PlaylistFields.Identity | PlaylistFields.Capabilities));
        Assert.True(local.Editable);
        Assert.True(local.TrackEdges[0].AddedAt > 0);

        Assert.Equal(track.Slot, Playlist.ImportLocalFile(@"C:\Music\Intro.mp3").Slot);
        Assert.Single(Playlist.LocalFiles.TrackSlots.ToArray());
    }
}
