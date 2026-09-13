// ── Wavee.Tests/PrefsTests.cs — the cross-surface preference epochs ──────────────────────────────────────────────────
//
// Wave 4's gate for `Platform/Prefs.cs` (owner L). The shape under test is the one the file header states: the store is
// the TRUTH, the epoch is only the update EDGE, every writer persists THEN bumps exactly once, and every persisted int is
// clamped at both ends. The lyrics secondary-line cycle is 0.2.9's `LyricsPrefs` rule ported with its facts.
//
// `Platform.Settings` is process state, so this class joins the platform collection and every fact installs its own
// in-memory store and puts the defaults-only facade back when it is done.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class PrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public PrefsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    // ── the shape: persist, then bump once ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_writer_persists_first_and_bumps_exactly_once()
    {
        int before = Prefs.Appearance.Epoch.Peek();
        Prefs.Appearance.Set(Platform.Keys.MarqueeEnabled, false);
        Assert.Equal(before + 1, Prefs.Appearance.Epoch.Peek());
        // The store has it by the time anyone reacts to the bump — a bump before the write makes every reader re-read
        // the OLD value.
        Assert.False(Prefs.Appearance.Marquee());
    }

    [Fact]
    public void A_read_answers_from_the_store_not_from_the_epoch()
    {
        // The epoch caches NOTHING. A value written behind the epoch's back (a settings import, a second window) is
        // still what the next read returns.
        _store.Set(Platform.Keys.ColorWashesEnabled, false);
        Assert.False(Prefs.Appearance.ColorWashes());
        _store.Set(Platform.Keys.ColorWashesEnabled, true);
        Assert.True(Prefs.Appearance.ColorWashes());
    }

    [Fact]
    public void An_absent_store_answers_every_key_with_its_default()
    {
        Platform.UseSettings(null);
        Assert.Equal(Platform.Keys.RowDensity.Default, Prefs.Appearance.RowDensity());
        Assert.Equal(Platform.Keys.HideTrackArtwork.Default, Prefs.Appearance.TrackArtworkHidden());
    }

    [Fact]
    public void The_epochs_are_independent_fan_out_boundaries()
    {
        // A row-density change must not re-render every mounted lyrics view.
        int lyrics = Prefs.Lyrics.Epoch.Peek();
        int hero = Prefs.DetailHero.Epoch.Peek();
        Prefs.Appearance.Set(Platform.Keys.RowDensity, 2);
        Assert.Equal(lyrics, Prefs.Lyrics.Epoch.Peek());
        Assert.Equal(hero, Prefs.DetailHero.Epoch.Peek());
    }

    [Fact]
    public void BumpAll_moves_every_epoch()
    {
        int a = Prefs.Appearance.Epoch.Peek(), l = Prefs.Lyrics.Epoch.Peek(), d = Prefs.DetailHero.Epoch.Peek(),
            n = Prefs.NpvPlayer.Epoch.Peek(), p = Prefs.PlayerBar.Epoch.Peek();
        Prefs.BumpAll();
        Assert.Equal(a + 1, Prefs.Appearance.Epoch.Peek());
        Assert.Equal(l + 1, Prefs.Lyrics.Epoch.Peek());
        Assert.Equal(d + 1, Prefs.DetailHero.Epoch.Peek());
        Assert.Equal(n + 1, Prefs.NpvPlayer.Epoch.Peek());
        Assert.Equal(p + 1, Prefs.PlayerBar.Epoch.Peek());
    }

    // ── the clamps ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(3, 4, 3)]
    [InlineData(4, 4, 0)]
    [InlineData(-1, 4, 0)]
    [InlineData(7, 2, 0)]
    public void An_out_of_range_int_reads_as_the_first_rung(int value, int count, int expected)
        => Assert.Equal(expected, Prefs.Clamp(value, count));

    [Fact]
    public void A_hand_edited_density_reads_as_compact_rather_than_as_nothing()
    {
        _store.Set(Platform.Keys.RowDensity, 99);
        Assert.Equal(0, Prefs.Appearance.RowDensity());
    }

    [Fact]
    public void A_downgraded_liked_cover_treatment_reads_as_stock()
    {
        // A build that shipped more treatments wrote a rung this one does not have.
        _store.Set(Platform.Keys.LikedCoverStyle, 12);
        Assert.Equal(0, Prefs.Appearance.LikedCover(treatmentCount: 9));
        _store.Set(Platform.Keys.LikedCoverStyle, 4);
        Assert.Equal(4, Prefs.Appearance.LikedCover(treatmentCount: 9));
    }

    // ── lyrics ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 0)]     // a stray 7 must show the ORIGINAL line, not nothing
    [InlineData(-3, 0)]
    public void A_secondary_line_mode_clamps_to_a_real_mode(int mode, int expected)
        => Assert.Equal(expected, Prefs.Lyrics.ClampMode(mode));

    [Fact]
    public void The_capability_bit_is_arithmetic_on_the_mode()
    {
        Assert.Equal(Prefs.Lyrics.HasTranslation, Prefs.Lyrics.BitFor(Prefs.Lyrics.Translation));
        Assert.Equal(Prefs.Lyrics.HasRomanization, Prefs.Lyrics.BitFor(Prefs.Lyrics.Romanization));
        Assert.Equal(0, Prefs.Lyrics.BitFor(Prefs.Lyrics.None));
    }

    [Fact]
    public void The_toggle_cycles_through_every_layer_a_document_has()
    {
        int both = Prefs.Lyrics.HasTranslation | Prefs.Lyrics.HasRomanization;
        Assert.Equal(Prefs.Lyrics.Translation, Prefs.Lyrics.Next(Prefs.Lyrics.None, both));
        Assert.Equal(Prefs.Lyrics.Romanization, Prefs.Lyrics.Next(Prefs.Lyrics.Translation, both));
        Assert.Equal(Prefs.Lyrics.None, Prefs.Lyrics.Next(Prefs.Lyrics.Romanization, both));
    }

    [Fact]
    public void The_toggle_skips_a_layer_the_document_does_not_have()
    {
        Assert.Equal(Prefs.Lyrics.Romanization, Prefs.Lyrics.Next(Prefs.Lyrics.None, Prefs.Lyrics.HasRomanization));
        Assert.Equal(Prefs.Lyrics.None, Prefs.Lyrics.Next(Prefs.Lyrics.Romanization, Prefs.Lyrics.HasRomanization));
    }

    [Fact]
    public void None_is_always_reachable_so_the_cycle_can_never_trap_the_user()
    {
        // Even a persisted mode the document cannot show steps back to None rather than staying stuck.
        Assert.Equal(Prefs.Lyrics.None, Prefs.Lyrics.Next(Prefs.Lyrics.Translation, available: 0));
        Assert.Equal(Prefs.Lyrics.None, Prefs.Lyrics.Next(Prefs.Lyrics.None, available: 0));
    }

    [Fact]
    public void Setting_the_secondary_line_persists_the_clamped_mode_and_bumps()
    {
        int before = Prefs.Lyrics.Epoch.Peek();
        Prefs.Lyrics.SetSecondaryLine(9);
        Assert.Equal(Prefs.Lyrics.None, _store.Get(Platform.Keys.LyricsSecondaryLine));
        Assert.Equal(before + 1, Prefs.Lyrics.Epoch.Peek());
    }

    [Fact]
    public void Blur_minus_one_is_AUTO_and_is_never_confused_with_off()
    {
        Prefs.Lyrics.SetBlurStrength(-40);
        Assert.Equal(-1, Prefs.Lyrics.BlurStrength());
        Prefs.Lyrics.SetBlurStrength(0);
        Assert.Equal(0, Prefs.Lyrics.BlurStrength());
        Prefs.Lyrics.SetBlurStrength(250);
        Assert.Equal(100, Prefs.Lyrics.BlurStrength());
    }

    // ── the detail layout ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_layout_write_bumps_the_detail_epoch()
    {
        int before = Prefs.DetailHero.Epoch.Peek();
        Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, Prefs.DetailHero.Hero);
        Assert.Equal(before + 1, Prefs.DetailHero.Epoch.Peek());
        Assert.Equal(Prefs.DetailHero.Hero, Prefs.DetailHero.PageLayout());
    }

    // ── the now-playing player ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Anything_that_is_not_Player_is_the_cover()
    {
        Assert.Equal(Prefs.NpvPlayer.Player, Prefs.NpvPlayer.ClampPresentation(Prefs.NpvPlayer.Player));
        Assert.Equal(Prefs.NpvPlayer.Cover, Prefs.NpvPlayer.ClampPresentation(5));
        Assert.Equal(Prefs.NpvPlayer.Cover, Prefs.NpvPlayer.ClampPresentation(-1));
    }

    [Fact]
    public void Picking_a_style_while_the_cover_is_up_also_SHOWS_the_player()
    {
        // Picking a player and then not seeing it is the report this answers.
        Prefs.NpvPlayer.SetPresentation(Prefs.NpvPlayer.Cover);
        int before = Prefs.NpvPlayer.Epoch.Peek();
        Prefs.NpvPlayer.SetStyle(3, presetCount: 12);
        Assert.Equal(3, Prefs.NpvPlayer.Style(12));
        Assert.Equal(Prefs.NpvPlayer.Player, Prefs.NpvPlayer.Presentation());
        Assert.Equal(before + 1, Prefs.NpvPlayer.Epoch.Peek());   // one gesture, ONE bump
    }

    [Fact]
    public void NextStyle_wraps_in_catalog_order()
    {
        Prefs.NpvPlayer.SetStyle(11, presetCount: 12);
        Prefs.NpvPlayer.NextStyle(12);
        Assert.Equal(0, Prefs.NpvPlayer.Style(12));
    }

    [Fact]
    public void A_style_id_the_catalog_no_longer_has_reads_as_the_first_preset()
    {
        _store.Set(Platform.Keys.NpvPlayerStyle, 40);
        Assert.Equal(0, Prefs.NpvPlayer.Style(presetCount: 12));
    }

    [Fact]
    public void A_preset_option_round_trips_through_its_own_key_and_clamps()
    {
        Prefs.NpvPlayer.SetChoice("record", "spin", 2, choiceCount: 3);
        Assert.Equal(2, Prefs.NpvPlayer.Choice("record", "spin", 3));
        Prefs.NpvPlayer.SetChoice("record", "spin", 9, choiceCount: 3);
        Assert.Equal(0, Prefs.NpvPlayer.Choice("record", "spin", 3));
        // A different preset's option is a different key.
        Assert.Equal(0, Prefs.NpvPlayer.Choice("cassette", "spin", 3));
    }

    [Fact]
    public void TogglePresentation_flips_both_ways()
    {
        Prefs.NpvPlayer.SetPresentation(Prefs.NpvPlayer.Cover);
        Prefs.NpvPlayer.TogglePresentation();
        Assert.Equal(Prefs.NpvPlayer.Player, Prefs.NpvPlayer.Presentation());
        Prefs.NpvPlayer.TogglePresentation();
        Assert.Equal(Prefs.NpvPlayer.Cover, Prefs.NpvPlayer.Presentation());
    }

    // ── the player bar ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_remaining_time_toggle_flips_and_bumps()
    {
        bool before = Prefs.PlayerBar.ShowRemaining();
        int epoch = Prefs.PlayerBar.Epoch.Peek();
        Prefs.PlayerBar.ToggleRemaining();
        Assert.Equal(!before, Prefs.PlayerBar.ShowRemaining());
        Assert.Equal(epoch + 1, Prefs.PlayerBar.Epoch.Peek());
    }
}
