// A module playable as a TRACK ROW (0.3 has no provider registry: the row is the identity every surface binds), and
// the identity-slot routes the player bar reads for it (G-212's `Modules.IsModulePlayable` / `LinkRouteFor`).

using Wavee.Sdk;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ModuleRowsTests
{
    const string ModuleId = "wavee.youtube";

    static Track Commit(string uri, ResolvedPlayable resolved, string? title = null, string? artistLine = null)
    {
        Staging s = Staging.Rent();
        StageRow(s, uri, resolved, title, artistLine);
        TestScope.CommitAndPublish(s);
        return new Track(Entities.Current.Tracks.Slot(uri.AsSpan()));
    }

    [Fact]
    public void AResolveAnswerBecomesAFullIdentityRow()
    {
        TestScope.Fresh();
        string uri = ModuleUri.Encode(ModuleId, "vid1");
        ResolvedPlayable resolved = ModuleFixtures.Resolved("vid1", title: "Claude FM", artists: ["Anthropic", "  ", "Guest"], durationMs: 42_000) with
        {
            ArtworkUrl = "https://i.ytimg.com/vi/vid1/hq.jpg",
        };

        Track t = Commit(uri, resolved);

        Assert.True(t.IsValid);
        Assert.Equal(EntityProvider.Module, t.Id.Provider);
        Assert.Equal(uri, t.Id.Text);
        Assert.True(t.Knows(TrackFields.Identity));
        Assert.True(t.Knows(TrackFields.Availability));
        Assert.True(t.IsPlayable);
        Assert.Equal("Claude FM", t.Title);
        Assert.Equal("Anthropic, Guest", Entities.Strings.Resolve(t.ArtistLineId));
        Assert.Equal("https://i.ytimg.com/vi/vid1/hq.jpg", Controls.ArtUrl(t.ImageId));
        Assert.Equal(42_000, t.DurationMs);
        Assert.False(t.HasVideo);
        Assert.True(IsModulePlayable(t.Id));
    }

    [Fact]
    public void ALiveVideoAnswer_DeclaresNoDuration_AndCarriesTheVideoBit()
    {
        TestScope.Fresh();
        string uri = ModuleUri.Encode(ModuleId, "live1");
        Track t = Commit(uri, ModuleFixtures.Resolved("live1", form: MediaForm.Video, isLive: true, durationMs: 9_999));

        Assert.Equal(0, t.DurationMs);
        Assert.True(t.HasVideo);
        Assert.True(t.Knows(TrackFields.Video));
    }

    [Fact]
    public void ALiveTitleCorrection_OverwritesTheTitleAndCredit_AndKeepsTheArt()
    {
        TestScope.Fresh();
        string uri = ModuleUri.Encode("wavee.radio", "station:1");
        ResolvedPlayable station = ModuleFixtures.Resolved("station:1", title: "Test FM", isLive: true) with { ArtworkUrl = "https://radio.test/logo.png" };
        Commit(uri, station);

        (string title, string? artist) = Playback.Audio.SplitIcyTitle("Some Artist - Some Song");
        Track t = Commit(uri, station, title, artist);

        Assert.Equal("Some Song", t.Title);
        Assert.Equal("Some Artist", Entities.Strings.Resolve(t.ArtistLineId));
        Assert.Equal("https://radio.test/logo.png", Controls.ArtUrl(t.ImageId));
    }

    [Fact]
    public void ARowWithNoTitle_FallsBackToItsUri_NeverABlankRow()
    {
        TestScope.Fresh();
        string uri = ModuleUri.Encode(ModuleId, "blank");
        Track t = Commit(uri, ModuleFixtures.Resolved("blank", title: "   ", artists: []));
        Assert.Equal(uri, t.Title);
        Assert.True(t.ArtistLineId.IsEmpty);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { " A ", "", "B" }, "A, B")]
    public void JoinArtists_SkipsBlanks(string[]? artists, string? expected) => Assert.Equal(expected, JoinArtists(artists));

    [Fact]
    public void ANonModuleRow_IsNeverAModulePlayable()
    {
        TestScope.Fresh();
        Assert.False(IsModulePlayable(EntityId.Parse("spotify:track:4cOdK2wGLETKBW3PvgPWqT")));
        Assert.False(IsModulePlayable(default));
        Assert.Null(LinkRouteFor(default, Shell.LinkSlot.Title));
    }

    [Fact]
    public void LinkRouteFor_NavigatesTheSlotsTheModuleNamed_FromTheResolveCache()
    {
        TestScope.Fresh();
        var cache = new ModulePlayableCache(() => 0);
        Playables.Attach(cache);
        try
        {
            string uri = ModuleUri.Encode(ModuleId, "vid9");
            ResolvedPlayable resolved = ModuleFixtures.Resolved("vid9", title: "Claude FM", artists: ["Anthropic"],
                pageEntityId: "video:vid9", subtitleEntityId: "channel:UC1");
            Track t = Commit(uri, resolved);

            Assert.Null(LinkRouteFor(t, Shell.LinkSlot.Title));   // not resolved in this process yet: inert, never a guess

            cache.Put(uri, resolved);
            Shell.Route? title = LinkRouteFor(t, Shell.LinkSlot.Title);
            Shell.Route? artist = LinkRouteFor(t, Shell.LinkSlot.Artist);

            Assert.NotNull(title);
            Assert.Equal(Shell.RouteKind.Module, title.Value.Kind);
            Assert.Equal(Pages.RouteForEntity(ModuleId, "video:vid9"), Shell.NameOf(title.Value));
            Assert.Equal("Claude FM", Shell.ArgOf(title.Value));
            Assert.NotNull(artist);
            Assert.Equal(Pages.RouteForEntity(ModuleId, "channel:UC1"), Shell.NameOf(artist.Value));
            Assert.Equal("Anthropic", Shell.ArgOf(artist.Value));
        }
        finally { Playables.Attach(null); }
    }
}
