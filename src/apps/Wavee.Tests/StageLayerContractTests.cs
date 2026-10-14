// ── Wavee.Tests/StageLayerContractTests.cs — the stage's layout layers' published keys and props (themes plan 6.0, T7a2) ──────────
//
// The four layouts' layers are the structure's since T7a2: the backdrop, the Artist layout's hero photo and name, the face and its scrim, the
// spectrum, the now-playing card, the identity (the morphing cover and its titles) and the Artist pane. What is pure about the contract: the keys'
// ids (a published id never changes), Classic registering all nine under its template ids, and the facts' value equality (a render that asked the
// same question and got the same answer is the same fact). The mounted facts are `Wavee.ShellTests\StageLayersMountedTests.cs`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class StageLayerContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Stage.Backdrop", ViewKeys.StageBackdrop.Id);
        Assert.Equal("Stage.Hero", ViewKeys.StageHero.Id);
        Assert.Equal("Stage.ArtistName", ViewKeys.StageArtistName.Id);
        Assert.Equal("Stage.Face", ViewKeys.StageFace.Id);
        Assert.Equal("Stage.FaceScrim", ViewKeys.StageFaceScrim.Id);
        Assert.Equal("Stage.Spectrum", ViewKeys.StageSpectrum.Id);
        Assert.Equal("Stage.NowPlayingCard", ViewKeys.StageNowPlayingCard.Id);
        Assert.Equal("Stage.Identity", ViewKeys.StageIdentity.Id);
        Assert.Equal("Stage.ArtistPane", ViewKeys.StageArtistPane.Id);
    }

    [Fact]
    public void Classic_registers_the_nine_layer_keys_under_its_template_ids()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.stage.backdrop", classic.Registry.TemplateIdOf(ViewKeys.StageBackdrop.Id));
        Assert.Equal("classic.stage.hero", classic.Registry.TemplateIdOf(ViewKeys.StageHero.Id));
        Assert.Equal("classic.stage.artist-name", classic.Registry.TemplateIdOf(ViewKeys.StageArtistName.Id));
        Assert.Equal("classic.stage.face", classic.Registry.TemplateIdOf(ViewKeys.StageFace.Id));
        Assert.Equal("classic.stage.face-scrim", classic.Registry.TemplateIdOf(ViewKeys.StageFaceScrim.Id));
        Assert.Equal("classic.stage.spectrum", classic.Registry.TemplateIdOf(ViewKeys.StageSpectrum.Id));
        Assert.Equal("classic.stage.now-playing-card", classic.Registry.TemplateIdOf(ViewKeys.StageNowPlayingCard.Id));
        Assert.Equal("classic.stage.identity", classic.Registry.TemplateIdOf(ViewKeys.StageIdentity.Id));
        Assert.Equal("classic.stage.artist-pane", classic.Registry.TemplateIdOf(ViewKeys.StageArtistPane.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_identity_facts_compare_by_value()
    {
        Assert.Equal(new StageMetaFacts("Artist · Album", true), new StageMetaFacts("Artist · Album", true));
        Assert.NotEqual(new StageMetaFacts("Artist · Album", true), new StageMetaFacts("Artist · Album", false));
        Assert.Equal(new StageChipFact("g", "FLAC"), new StageChipFact("g", "FLAC"));
        Assert.NotEqual(new StageChipFact(null, "120 BPM"), new StageChipFact("g", "120 BPM"));
    }

    [Fact]
    public void The_artist_target_names_the_artist_and_the_track_it_was_resolved_for()
    {
        var target = new StageArtistTarget(true, 4, 9, 2u);
        Assert.Equal(target, target with { });
        Assert.NotEqual(target, target with { Epoch = 3u });
        Assert.False(new StageArtistTarget(false, 0, 0, 0u).Valid);
    }

    [Fact]
    public void The_artist_rows_compare_by_value()
    {
        Assert.Equal(new StageArtistCityRow("Amsterdam", "1.2M", 0.5f), new StageArtistCityRow("Amsterdam", "1.2M", 0.5f));
        Assert.NotEqual(new StageArtistCityRow("Amsterdam", "1.2M", 0.5f), new StageArtistCityRow("Amsterdam", "1.2M", 1f));
        Assert.Equal(new StageArtistFan(3, "Name", null), new StageArtistFan(3, "Name", null));
    }
}
