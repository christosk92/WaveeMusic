// ── Wavee.Tests/ShowViewPrefsStoreTests.cs — the per-show view prefs' typed settings door (C16, X41) ─────────────────
//
// The blob FORMAT (`id:status:order;…`, most recent first, capped) is pinned in ShowViewPrefsTests; these facts pin the
// door the show page seeds from and persists to: one key, written only when the blob actually changed, and an invalid
// id writes nothing at all.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ShowViewPrefsStoreTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public ShowViewPrefsStoreTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    [Fact]
    public void A_show_nobody_filtered_reads_the_defaults()
    {
        Assert.Equal("", ShowViewPrefs.Blob());
        Assert.Equal((0, 0), ShowViewPrefs.Stored("4rOoJ6Egrf8K2IrywzwOMk"));
    }

    [Fact]
    public void Saving_writes_the_one_blob_and_reads_back()
    {
        Assert.True(ShowViewPrefs.Save("abc", status: 2, order: 1));

        Assert.Equal("abc:2:1", _store.Get(Platform.Keys.PodcastViews));
        Assert.Equal("abc:2:1", ShowViewPrefs.Blob());
        Assert.Equal((2, 1), ShowViewPrefs.Stored("abc"));
    }

    [Fact]
    public void Saving_an_unchanged_view_costs_no_settings_write()
    {
        ShowViewPrefs.Save("abc", 2, 1);
        uint before = Platform.SettingsEpoch;

        Assert.False(ShowViewPrefs.Save("abc", 2, 1));

        Assert.Equal(before, Platform.SettingsEpoch);
    }

    [Fact]
    public void The_most_recently_saved_show_moves_to_the_front()
    {
        ShowViewPrefs.Save("abc", 1, 0);
        ShowViewPrefs.Save("xyz", 3, 1);

        Assert.Equal("xyz:3:1;abc:1:0", ShowViewPrefs.Blob());
        Assert.Equal((1, 0), ShowViewPrefs.Stored("abc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("a;b")]
    public void An_invalid_show_id_writes_nothing(string id)
    {
        Assert.False(ShowViewPrefs.Save(id, 3, 1));
        Assert.False(_store.WasWritten(Platform.Keys.PodcastViews));
        Assert.Equal((0, 0), ShowViewPrefs.Stored(id));
    }
}
