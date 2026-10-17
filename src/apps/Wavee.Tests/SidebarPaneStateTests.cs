// ── Wavee.Tests/SidebarPaneStateTests.cs — the sidebar host's theme state (M7b) ──────────────────────────────────────────
//
// The pane's width and collapse and Your Library's list/grid view are the ACTIVE THEME's state (`theme.<id>.state.paneWidth|paneCollapsed|libraryView`): the
// host writes them, never a view. Pinned here: where each verb writes, that the view is one undoable ring entry and part of "Reset layout" (and counts for
// "· modified"), and that a theme's scope brings its own values (`FollowThemeState`).
//
// The `Sidebar` statics and `Platform.Settings` are process state: the class joins the platform collection, installs its own store and puts back what it changes.

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class SidebarPaneStateTests : IDisposable
{
    static SettingKey<float> StoredWidth(string theme) => new(ThemeState.SettingName(theme, Sidebar.PaneWidthKey.Id), 0f);

    readonly SidebarLayoutId _layout = Sidebar.Layout.Peek();

    public SidebarPaneStateTests()
    {
        Platform.UseSettings(new MemoryAppSettings());
        Reset();
        Sidebar.SwitchLayout(SidebarLayoutId.Library);          // only the Library layout carries the view in its resolved document
    }

    public void Dispose()
    {
        Reset();
        Sidebar.SwitchLayout(_layout);
        Platform.UseSettings(null);
    }

    static void Reset()
    {
        Sidebar.ResetLayoutRecorded(SidebarLayoutId.Library, null);
        Sidebar.SetExpandedWidth(SidebarPaneBounds.DefaultWidth);
        if (Sidebar.UserCollapsed.Peek()) Sidebar.TogglePane();
    }

    [Fact]
    public void A_width_is_written_to_the_active_themes_state_and_clamped()
    {
        Sidebar.SetExpandedWidth(250f);
        Assert.Equal(250f, ThemeState.Get(Sidebar.PaneWidthKey));
        Assert.Equal(250f, Sidebar.Width.Peek());

        Sidebar.SetExpandedWidth(5000f);
        Assert.Equal(SidebarPaneBounds.NavPaneMaxW, ThemeState.Get(Sidebar.PaneWidthKey));
    }

    [Fact]
    public void The_collapse_is_written_to_the_active_themes_state()
    {
        Sidebar.TogglePane();
        Assert.True(ThemeState.Get(Sidebar.PaneCollapsedKey));
        Assert.True(Sidebar.UserCollapsed.Peek());

        Sidebar.TogglePane();
        Assert.False(ThemeState.Get(Sidebar.PaneCollapsedKey));
    }

    [Fact]
    public void The_view_is_written_to_the_state_and_undo_and_redo_restore_it()
    {
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);
        Assert.Equal(1, ThemeState.Get(Sidebar.LibraryViewKey));
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.Doc.Library.View);

        Sidebar.Undo();
        Assert.Equal(0, ThemeState.Get(Sidebar.LibraryViewKey));
        Assert.Equal(SidebarLibraryView.List, Sidebar.Doc.Library.View);

        Sidebar.Redo();
        Assert.Equal(1, ThemeState.Get(Sidebar.LibraryViewKey));
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.Doc.Library.View);
    }

    [Fact]
    public void The_view_counts_as_modified_in_the_library_layout_and_Reset_layout_puts_it_back_as_one_entry()
    {
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);
        Assert.True(SidebarLayoutRules.IsModified(Sidebar.State.Library, Sidebar.LibraryView.Peek()));
        Assert.False(SidebarLayoutRules.IsModified(Sidebar.State.Classic, Sidebar.LibraryView.Peek()));

        Sidebar.ResetLayoutRecorded(SidebarLayoutId.Library, null);
        Assert.Equal(SidebarLibraryView.List, Sidebar.LibraryView.Peek());
        Assert.Equal(0, ThemeState.Get(Sidebar.LibraryViewKey));

        Sidebar.Undo();                                                // ONE entry: the view comes back
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.LibraryView.Peek());
        Assert.Equal(1, ThemeState.Get(Sidebar.LibraryViewKey));
    }

    [Fact]
    public void Reset_layout_of_Classic_leaves_the_view_alone()
    {
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);
        Sidebar.ResetLayoutRecorded(SidebarLayoutId.Classic, null);
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.LibraryView.Peek());
    }

    [Fact]
    public void Another_themes_scope_brings_its_own_values()
    {
        Sidebar.SetExpandedWidth(250f);
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);

        // A value another theme stored under its own scope is not the active theme's: the active scope still reads Classic's.
        ThemeState.Set(Sidebar.PaneWidthKey, 400f, Platform.Settings, "other");
        Sidebar.FollowThemeState();
        Assert.Equal(250f, Sidebar.Width.Peek());
        Assert.Equal(400f, Platform.Settings.Get(StoredWidth("other")));
        Assert.Equal(250f, Platform.Settings.Get(StoredWidth(ThemeCatalog.FallbackId)));
    }

    [Fact]
    public void Following_the_state_rereads_a_value_the_store_moved()
    {
        Platform.Settings.Set(StoredWidth(ThemeCatalog.FallbackId), 300f);
        Platform.Settings.Set(new SettingKey<int>(ThemeState.SettingName(ThemeCatalog.FallbackId, Sidebar.LibraryViewKey.Id), 0), 1);

        Sidebar.FollowThemeState();

        Assert.Equal(300f, Sidebar.Width.Peek());
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.Doc.Library.View);
    }
}
