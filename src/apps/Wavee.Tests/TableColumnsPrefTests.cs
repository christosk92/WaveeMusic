// ── Wavee.Tests/TableColumnsPrefTests.cs — the track table's Tempo and Plays opt-ins, as a typed pref (C16, X40) ──────
//
// Both columns are appearance prefs: a verb persists, then bumps `Prefs.Appearance.Epoch` exactly once; a read answers
// from the store (the epoch caches nothing). Whether a column is OFFERED is the page config's, so the presence rule is a
// pure function over (config, opt-in, pref).

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class TableColumnsPrefTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public TableColumnsPrefTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    [Fact]
    public void Both_columns_are_off_until_the_user_switches_them_on()
    {
        Assert.False(TableColumnsPref.Tempo());
        Assert.False(TableColumnsPref.Plays());
        Assert.False(TableColumnsPref.TempoNow());
        Assert.False(TableColumnsPref.PlaysNow());
    }

    [Fact]
    public void Switching_tempo_persists_then_bumps_the_appearance_epoch_once()
    {
        int appearance = Prefs.Appearance.Epoch.Peek();
        int rails = DetailRailPrefs.Epoch.Peek();

        TableColumnsPref.SetTempo(true);

        Assert.True(_store.Get(Platform.Keys.TempoColumn));
        Assert.True(TableColumnsPref.Tempo());
        Assert.True(TableColumnsPref.TempoNow());
        Assert.False(TableColumnsPref.Plays());                                  // the other column is untouched
        Assert.False(_store.WasWritten(Platform.Keys.PlaysColumn));
        Assert.Equal(appearance + 1, Prefs.Appearance.Epoch.Peek());
        Assert.Equal(rails, DetailRailPrefs.Epoch.Peek());                       // not the rail epoch
    }

    [Fact]
    public void Switching_plays_on_and_off_bumps_once_each()
    {
        int appearance = Prefs.Appearance.Epoch.Peek();

        TableColumnsPref.SetPlays(true);
        Assert.True(TableColumnsPref.Plays());
        TableColumnsPref.SetPlays(false);
        Assert.False(TableColumnsPref.Plays());

        Assert.Equal(appearance + 2, Prefs.Appearance.Epoch.Peek());
        Assert.False(_store.WasWritten(Platform.Keys.TempoColumn));
    }

    [Fact]
    public void A_read_answers_from_the_store_not_from_the_epoch()
    {
        // A value written behind the epoch's back (a settings import, a second window) is still what the next read returns.
        _store.Set(Platform.Keys.TempoColumn, true);
        _store.Set(Platform.Keys.PlaysColumn, true);
        Assert.True(TableColumnsPref.Tempo());
        Assert.True(TableColumnsPref.Plays());
        _store.Set(Platform.Keys.TempoColumn, false);
        Assert.False(TableColumnsPref.Tempo());
    }

    [Fact]
    public void A_structure_with_a_tempo_scope_reads_its_own_setting_off_by_default()
    {
        // WinUI: no board draws BPM · Key, so the column rests off there whatever the shared (Classic, Zune) setting says, and turning it on under WinUI
        // writes WinUI's own key.
        var scoped = new Wavee.Theming.ListPolicy(TrackArtwork: true) { TempoColumnScope = "winui" };
        _store.Set(Platform.Keys.TempoColumn, true);

        var key = TableColumnsPref.TempoKey(scoped);
        Assert.Equal("detail.tempoColumn.winui", key.Name);
        Assert.False(_store.Get(key));
        Assert.Equal(Platform.Keys.TempoColumn, TableColumnsPref.TempoKey(Wavee.Theming.ListPolicy.Default));   // no scope: the shared key, as before
        Assert.True(TableColumnsPref.Tempo());                                                              // the default policy keeps reading the shared key
    }

    [Theory]
    [InlineData(true, false, false, true)]       // a page that always shows plays
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]        // an opt-in page, switched on
    [InlineData(false, true, false, false)]      // an opt-in page, switched off
    [InlineData(false, false, true, false)]      // a pref switched on for a page that does not offer the column
    [InlineData(false, false, false, false)]
    public void Plays_is_visible_when_the_page_shows_it_or_offers_it_and_the_user_took_it(bool configShows, bool optIn, bool prefOn, bool expected)
        => Assert.Equal(expected, TableColumnsPref.PlaysVisible(configShows, optIn, prefOn));
}
