// ── Wavee.Tests/DeckPrefsTests.cs — the deck's content-preference verbs (themes plan C3, X10) ─────────────────────────
// (Shell/DeckPrefs.cs over `Rail.PlayerPrefs` / `Prefs.NpvPlayer`.) `Platform.Settings` is process state, so this class joins
// the platform collection and installs its own in-memory store.
//
//   ONE WRITE, ONE BUMP   every verb persists THEN bumps the one epoch exactly once.
//   ATTRIBUTION           every write names the surface that made it, with the strings the `npv` log has always carried.
//   PICKING SHOWS         a style chosen while the cover was up flips the presentation to Player in the same write.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class DeckPrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public DeckPrefsTests() => Platform.UseSettings(_store);

    public void Dispose()
    {
        Rail.PlayerPrefs.StyleFlyoutOpen.Value = false;
        Platform.UseSettings(null);
    }

    static Rail.PlayerCatalog.Preset P(int id) => Rail.PlayerCatalog.ById(id);

    [Fact]
    public void Every_surface_has_the_attribution_string_the_log_has_always_carried()
    {
        Assert.Equal(Rail.NpvDiagnostics.SourceHeader, Shell.DeckPrefs.SourceName(Shell.DeckPrefSource.Header));
        Assert.Equal(Rail.NpvDiagnostics.SourceFlyout, Shell.DeckPrefs.SourceName(Shell.DeckPrefSource.Flyout));
        Assert.Equal(Rail.NpvDiagnostics.SourceArtMenu, Shell.DeckPrefs.SourceName(Shell.DeckPrefSource.ArtMenu));
        Assert.Equal(Rail.NpvDiagnostics.SourceSettings, Shell.DeckPrefs.SourceName(Shell.DeckPrefSource.Settings));
        Assert.Equal(Rail.NpvDiagnostics.SourcePalette, Shell.DeckPrefs.SourceName(Shell.DeckPrefSource.Palette));
    }

    [Fact]
    public void Picking_a_style_persists_it_shows_it_and_bumps_the_epoch_once()
    {
        int before = Prefs.NpvPlayer.Epoch.Peek();
        Shell.DeckPrefs.SetStyle(Rail.PlayerCatalog.Turntable, Shell.DeckPrefSource.Flyout);
        Assert.Equal(Rail.PlayerCatalog.Turntable, Rail.PlayerPrefs.Style());
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Assert.Equal(before + 1, Prefs.NpvPlayer.Epoch.Peek());
    }

    [Fact]
    public void A_stray_style_id_lands_on_record()
    {
        Shell.DeckPrefs.SetStyle(999, Shell.DeckPrefSource.Settings);
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.Style());
    }

    [Fact]
    public void Setting_the_presentation_round_trips_and_toggling_alternates()
    {
        Shell.DeckPrefs.SetPresentation(Rail.PlayerPrefs.Player, Shell.DeckPrefSource.Header);
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Shell.DeckPrefs.SetPresentation(Rail.PlayerPrefs.Cover, Shell.DeckPrefSource.ArtMenu);
        Assert.Equal(Rail.PlayerPrefs.Cover, Rail.PlayerPrefs.Presentation());
        Shell.DeckPrefs.TogglePresentation(Shell.DeckPrefSource.Palette);
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Shell.DeckPrefs.TogglePresentation(Shell.DeckPrefSource.Palette);
        Assert.Equal(Rail.PlayerPrefs.Cover, Rail.PlayerPrefs.Presentation());
    }

    [Fact]
    public void An_option_choice_is_clamped_and_belongs_to_its_own_style()
    {
        var record = P(Rail.PlayerCatalog.Record);
        var turntable = P(Rail.PlayerCatalog.Turntable);
        var rpm = Rail.PlayerCatalog.Option(record, "rpm");

        Shell.DeckPrefs.SetChoice(record, rpm, 1, Shell.DeckPrefSource.Flyout);
        Assert.Equal(1, Rail.PlayerPrefs.Choice(record, "rpm"));
        Assert.Equal(0, Rail.PlayerPrefs.Choice(turntable, "rpm"));

        Shell.DeckPrefs.SetChoice(record, rpm, 999, Shell.DeckPrefSource.Flyout);    // a stray index lands on the default
        Assert.Equal(0, Rail.PlayerPrefs.Choice(record, "rpm"));
    }

    [Fact]
    public void The_segmented_rows_mirror_starts_at_the_stored_choice_and_is_a_fresh_signal_each_time()
    {
        var record = P(Rail.PlayerCatalog.Record);
        var rpm = Rail.PlayerCatalog.Option(record, "rpm");
        Shell.DeckPrefs.SetChoice(record, rpm, 1, Shell.DeckPrefSource.Flyout);
        var first = Shell.DeckPrefs.ChoiceMirror(record, rpm);
        Assert.Equal(1, first.Peek());
        first.Value = 0;                                       // a click moves the pill; the STORE is still the truth
        Assert.Equal(1, Rail.PlayerPrefs.Choice(record, "rpm"));
        Assert.Equal(1, Shell.DeckPrefs.ChoiceMirror(record, rpm).Peek());
    }

    [Fact]
    public void The_next_style_wraps_at_the_end_of_the_table()
    {
        Shell.DeckPrefs.SetStyle(Rail.PlayerCatalog.Presets.Length - 1, Shell.DeckPrefSource.Settings);
        Shell.DeckPrefs.NextStyle(Shell.DeckPrefSource.Palette);
        Assert.Equal(Rail.PlayerPrefs.ClampStyle(0), Rail.PlayerPrefs.Style());
    }

    [Fact]
    public void The_style_flyout_opens_from_anywhere_and_the_gear_toggles_it()
    {
        Rail.PlayerPrefs.StyleFlyoutOpen.Value = false;        // process state: start from closed
        Shell.DeckPrefs.OpenStyleFlyout();
        Assert.True(Rail.PlayerPrefs.StyleFlyoutOpen.Peek());
        Shell.DeckPrefs.OpenStyleFlyout();                     // the art menu asking while it is open keeps the one popup
        Assert.True(Rail.PlayerPrefs.StyleFlyoutOpen.Peek());
        Shell.DeckPrefs.ToggleStyleFlyout();
        Assert.False(Rail.PlayerPrefs.StyleFlyoutOpen.Peek());
        Shell.DeckPrefs.ToggleStyleFlyout();
        Assert.True(Rail.PlayerPrefs.StyleFlyoutOpen.Peek());
    }
}
