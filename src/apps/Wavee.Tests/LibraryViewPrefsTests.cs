// ── Wavee.Tests/LibraryViewPrefsTests.cs — the library page's per-kind view state, typed (C16, X41) ───────────────────
//
// The persisted keys are `Platform.Keys.Library*(kind)` (`library.<kind>.*`); the page used to read each by hand in its
// constructor and write them in its `SaveState`. These facts pin the store traffic: the per-kind defaults, a round trip,
// that only the artists' reader persists its own three keys, and that a save never writes the widths (a column drag
// commits those on release).

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class LibraryViewPrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public LibraryViewPrefsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    static LibraryViewState Sample(string selected = "spotify:album:a") => new(
        LeftW: 1f, MidW: 2f, Sort: 3, Desc: true, View: 2, Size: 0, SelectedKey: selected,
        AlbumKey: "spotify:album:b", Scope: 1, AlbumSort: 4);

    [Fact]
    public void An_untouched_kind_reads_the_keys_own_defaults()
    {
        var albums = LibraryViewPrefs.Read("albums");
        Assert.Equal(340f, albums.LeftW);
        Assert.Equal(440f, albums.MidW);
        Assert.Equal(0, albums.Sort);
        Assert.False(albums.Desc);
        Assert.Equal(1, albums.View);
        Assert.Equal(1, albums.Size);
        Assert.Equal("", albums.SelectedKey);
        Assert.Equal("", albums.AlbumKey);
        Assert.Equal(0, albums.Scope);
        Assert.Equal(0, albums.AlbumSort);
    }

    [Fact]
    public void Artists_is_the_one_kind_with_a_narrow_nav_column_by_default()
    {
        Assert.Equal(280f, LibraryViewPrefs.Read("artists").LeftW);
        Assert.Equal(340f, LibraryViewPrefs.Read("podcasts").LeftW);
        Assert.Equal(340f, LibraryViewPrefs.Read("audiobooks").LeftW);
    }

    [Fact]
    public void The_artists_view_round_trips_every_key_except_the_widths()
    {
        LibraryViewPrefs.Save("artists", Sample(), artists: true);

        var read = LibraryViewPrefs.Read("artists");
        Assert.Equal(3, read.Sort);
        Assert.True(read.Desc);
        Assert.Equal(2, read.View);
        Assert.Equal(0, read.Size);
        Assert.Equal("spotify:album:a", read.SelectedKey);
        Assert.Equal("spotify:album:b", read.AlbumKey);
        Assert.Equal(1, read.Scope);
        Assert.Equal(4, read.AlbumSort);
        Assert.Equal(280f, read.LeftW);                                           // the sample's widths were not written
        Assert.Equal(440f, read.MidW);
        Assert.False(_store.WasWritten(new SettingKey<float>(ThemeState.SettingName(ThemeCatalog.FallbackId, LibraryViewPrefs.LeftWKey("artists").Id), 0f)));
        Assert.False(_store.WasWritten(new SettingKey<float>(ThemeState.SettingName(ThemeCatalog.FallbackId, LibraryViewPrefs.MidWKey("artists").Id), 0f)));
    }

    [Fact]
    public void Only_the_artists_reader_persists_the_album_key_scope_and_reader_sort()
    {
        LibraryViewPrefs.Save("albums", Sample(), artists: false);

        Assert.True(_store.WasWritten(Platform.Keys.LibrarySort("albums")));
        Assert.True(_store.WasWritten(Platform.Keys.LibraryDesc("albums")));
        Assert.True(_store.WasWritten(Platform.Keys.LibraryView("albums")));
        Assert.True(_store.WasWritten(Platform.Keys.LibrarySize("albums")));
        Assert.True(_store.WasWritten(Platform.Keys.LibrarySelected("albums")));
        Assert.False(_store.WasWritten(Platform.Keys.LibraryAlbumKey("albums")));
        Assert.False(_store.WasWritten(Platform.Keys.LibraryScope("albums")));
        Assert.False(_store.WasWritten(Platform.Keys.LibraryAlbumSort("albums")));
        Assert.Equal(5, _store.WrittenCount);
    }

    [Fact]
    public void A_save_for_one_kind_leaves_every_other_kind_alone()
    {
        LibraryViewPrefs.Save("podcasts", Sample("spotify:show:p"), artists: false);

        Assert.Equal("spotify:show:p", LibraryViewPrefs.Read("podcasts").SelectedKey);
        Assert.Equal("", LibraryViewPrefs.Read("albums").SelectedKey);
        Assert.Equal("", LibraryViewPrefs.Read("audiobooks").SelectedKey);
        Assert.Equal("", LibraryViewPrefs.Read("artists").SelectedKey);
    }

    [Fact]
    public void A_column_drag_commits_its_width_and_a_later_save_does_not_overwrite_it()
    {
        LibraryViewPrefs.SetLeftW("albums", 512f);
        LibraryViewPrefs.SetMidW("albums", 333f);

        Assert.Equal(512f, LibraryViewPrefs.Read("albums").LeftW);
        Assert.Equal(333f, LibraryViewPrefs.Read("albums").MidW);

        LibraryViewPrefs.Save("albums", Sample(), artists: false);               // the sample carries 1 / 2 as widths
        Assert.Equal(512f, LibraryViewPrefs.Read("albums").LeftW);
        Assert.Equal(333f, LibraryViewPrefs.Read("albums").MidW);
        Assert.Equal(512f, _store.Get(new SettingKey<float>(ThemeState.SettingName(ThemeCatalog.FallbackId, "library.albums.leftw"), 0f)));
    }

    [Fact]
    public void The_state_clamp_is_the_classic_grips_range()
    {
        Assert.Equal(Themes.Classic.ClassicLibraryRules.LeftMin, LibraryViewPrefs.LeftMin);
        Assert.Equal(Themes.Classic.ClassicLibraryRules.LeftMax, LibraryViewPrefs.LeftMax);
        Assert.Equal(Themes.Classic.ClassicLibraryRules.MidMin, LibraryViewPrefs.MidMin);
        Assert.Equal(Themes.Classic.ClassicLibraryRules.MidMax, LibraryViewPrefs.MidMax);
    }

    [Fact]
    public void A_column_width_is_clamped_to_the_grips_range_and_each_kind_has_its_own_key()
    {
        LibraryViewPrefs.SetLeftW("albums", 5000f);
        LibraryViewPrefs.SetMidW("albums", 1f);
        Assert.Equal(Themes.Classic.ClassicLibraryRules.LeftMax, LibraryViewPrefs.Read("albums").LeftW);
        Assert.Equal(Themes.Classic.ClassicLibraryRules.MidMin, LibraryViewPrefs.Read("albums").MidW);
        Assert.Equal(280f, LibraryViewPrefs.Read("artists").LeftW);
    }
}
