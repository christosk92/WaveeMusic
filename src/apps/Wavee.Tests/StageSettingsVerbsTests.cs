// ── Wavee.Tests/StageSettingsVerbsTests.cs — what the stage's option surfaces write (themes plan 6.0, T7a1) ──────────────────────
//
// The options popover and the gallery wrote `Prefs.Stage.*` straight from their pictures. A theme may not name `Prefs`, so each write is a
// verb of `IStageSettingsVerbs`; the production implementation is those same setters. What the facts pin: a verb persists its value (the
// store is the truth) and bumps the stage epoch exactly once (the surface's one preference effect re-mirrors the context from it), a face
// pick survives other writes (the gallery's selection persistence) and the stored values stay clamped by the setter, not the picture.
//
// `Platform.Settings` is process state, so this class joins the platform collection and installs its own in-memory store.

using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class StageSettingsVerbsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public StageSettingsVerbsTests() => Platform.UseSettings(_store);

    public void Dispose() => Platform.UseSettings(null);

    static IStageSettingsVerbs Verbs => StageSettingsVerbs.Shared;

    static int Epoch() => Prefs.Stage.Epoch.Peek();

    [Fact]
    public void The_layout_and_the_spectrum_persist_and_bump_the_epoch_once_each()
    {
        int before = Epoch();
        Verbs.SetLayout((int)Stage.VizLayout.Centered);
        Assert.Equal(before + 1, Epoch());
        Assert.Equal((int)Stage.VizLayout.Centered, Prefs.Stage.Layout());

        Verbs.SetSpectrum((int)Stage.SpectrumStyle.Ring);
        Assert.Equal(before + 2, Epoch());
        Assert.Equal((int)Stage.SpectrumStyle.Ring, Prefs.Stage.Spectrum());
    }

    [Fact]
    public void An_out_of_range_layout_or_spectrum_is_coerced_by_the_setter_not_the_picture()
    {
        Verbs.SetLayout(99);
        Assert.Equal(Stage.LayoutRules.Coerce(99), Prefs.Stage.Layout());
        Verbs.SetSpectrum(-4);
        Assert.Equal(Stage.LayoutRules.CoerceSpectrum(-4), Prefs.Stage.Spectrum());
    }

    [Fact]
    public void The_hero_options_persist()
    {
        Verbs.SetHeroLyrics(false);
        Verbs.SetHeroMotion(false);
        Verbs.SetHeroDim(65);
        Assert.False(Prefs.Stage.HeroLyrics());
        Assert.False(Prefs.Stage.HeroMotion());
        Assert.Equal(Stage.LayoutRules.ClampDim(65), Prefs.Stage.HeroDim());
        Verbs.SetHeroLyrics(true);
        Assert.True(Prefs.Stage.HeroLyrics());
    }

    [Fact]
    public void The_gallery_settings_persist()
    {
        Verbs.SetSensitivity(1.25f);
        Verbs.SetLyricsOverlay(false);
        Verbs.SetCalm(true);
        Verbs.SetMoments(false);
        Assert.Equal(Visualizer.Bands.ClampSensitivity(1.25f), Prefs.Stage.Sensitivity());
        Assert.False(Prefs.Stage.LyricsOverlay());
        Assert.True(Prefs.Stage.Calm());
        Assert.False(Prefs.Stage.Moments());
    }

    [Fact]
    public void Closing_the_gallery_persists_that_it_is_closed()
    {
        Verbs.SetGalleryOpen(false);
        Assert.False(Prefs.Stage.GalleryOpen());
        Verbs.SetGalleryOpen(true);
        Assert.True(Prefs.Stage.GalleryOpen());
    }

    [Fact]
    public void A_picked_face_is_the_stored_face_and_survives_other_stage_writes()
    {
        int before = Epoch();
        Verbs.PickFace(Visualizer.Kind.Bars);
        Assert.Equal(before + 1, Epoch());
        Assert.Equal((int)Visualizer.Kind.Bars, Prefs.Stage.Visualizer());

        // the options popover, the gallery, and the settings page all write the same epoch: none of them touches the face
        Verbs.SetCalm(true);
        Verbs.SetLayout((int)Stage.VizLayout.LargeArt);
        Verbs.SetGalleryOpen(false);
        Assert.Equal((int)Visualizer.Kind.Bars, Prefs.Stage.Visualizer());
    }
}
