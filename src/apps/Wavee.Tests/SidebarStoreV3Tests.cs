// ── Wavee.Tests/SidebarStoreV3Tests.cs — sidebar.json v3 (the device file) ⇄ SidebarLayoutState ────────────────────────
//
// The wire is the only thing under test here: what a layout state looks like on disk, what comes back, and what the reader
// refuses. The rules that produce the state (SidebarLayoutRules) are tested in SidebarLayoutRulesTests.cs; the file's bytes
// are written and read through SidebarStoreV3 and SidebarStoreJsonCtx, never through a copy of the mapping.

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarStoreV3Tests
{
    static SidebarDeviceFileDto DtoOf(byte[] bytes)
        => JsonSerializer.Deserialize(bytes, SidebarStoreJsonCtx.Default.SidebarDeviceFileDto)!;

    static SidebarOverlayDto? OverlayDto(SidebarDeviceFileDto dto, SidebarLayoutId layout)
        => layout == SidebarLayoutId.Library ? dto.Layouts?.Library : dto.Layouts?.Classic;

    static SidebarSectionStateDto SectionDto(SidebarDeviceFileDto dto, SidebarLayoutId layout, string id)
        => OverlayDto(dto, layout)!.Sections!.Single(s => s!.Id == id)!;

    static string[] IdsOf(LayoutOverlay overlay) => overlay.Sections.Select(s => s.Id).ToArray();

    static SidebarLayoutState Edited()
    {
        var s = SidebarLayoutState.Default;
        s = Apply(s, new SetItemShown(SidebarLayoutId.Classic, "collections", "albums", false));
        s = Apply(s, new MoveSection(SidebarLayoutId.Classic, "newReleases", 0));
        s = Apply(s, new SetSectionLimit(SidebarLayoutId.Classic, "recent", 20));
        s = Apply(s, new SetSectionShown(SidebarLayoutId.Classic, "recent", true));
        s = Apply(s, new SetLibrarySort(SidebarLibrarySort.Alphabetical, true));
        s = Apply(s, new SetLibraryView(SidebarLibraryView.Grid));
        s = Apply(s, new SetShowLiked(false));
        return s;
    }

    static SidebarLayoutState Apply(SidebarLayoutState state, SidebarOp op)
    {
        var result = SidebarLayoutRules.Apply(state, op, pinnedLocked: false);
        Assert.True(result.Changed, $"{op.GetType().Name} was refused: {result.Reject}");
        return result.State;
    }

    [Fact]
    public void Default_RoundTrips()
    {
        var bytes = SidebarStoreV3.Serialize(SidebarLayoutState.Default);

        Assert.True(SidebarStoreV3.TryParse(bytes, out var back));
        Assert.Equal(SidebarLayoutState.Default, back);
    }

    [Fact]
    public void DefaultSection_WritesItsIdOnly()
    {
        // A section at its catalogue defaults is just its id: the file stays small and a new default reaches every user
        // who never changed the section.
        var dto = DtoOf(SidebarStoreV3.Serialize(SidebarLayoutState.Default));

        Assert.Equal(SidebarStoreV3.Version, dto.Version);
        var pinned = SectionDto(dto, SidebarLayoutId.Classic, "pinned");
        Assert.Null(pinned.Hidden);
        Assert.Null(pinned.Collapsed);
        Assert.Null(pinned.Limit);
        Assert.Null(pinned.HiddenItems);
        Assert.Null(pinned.ItemOrder);
        Assert.Null(pinned.Sort);
        Assert.Null(pinned.Descending);
        Assert.Null(pinned.View);
        Assert.Null(pinned.ShowLiked);
    }

    [Fact]
    public void EditedState_RoundTrips()
    {
        // A hidden collections item, a moved section, the recent limit, a shown feed, and the library sort, view and liked.
        var edited = Edited();
        Assert.Equal(SidebarCatalogue.IdOf(SidebarSectionKind.Home), edited.Classic.Sections[0].Id);   // locked in place
        Assert.True(Array.IndexOf(IdsOf(edited.Classic), "newReleases") < Array.IndexOf(IdsOf(edited.Classic), "pinned"));
        Assert.Contains("albums", edited.Classic.Find("collections")!.HiddenList);

        var bytes = SidebarStoreV3.Serialize(edited);
        Assert.True(SidebarStoreV3.TryParse(bytes, out var back));

        Assert.Equal(edited, back);
        Assert.Equal(20, back.Classic.Find("recent")!.Limit);
        Assert.Equal(SidebarLibrarySort.Alphabetical, back.Library.Find("library")!.Sort);
        Assert.Equal(true, back.Library.Find("library")!.Descending);
        Assert.Equal(SidebarLibraryView.Grid, back.Library.Find("library")!.View);
        Assert.Equal(false, back.Library.Find("library")!.ShowLiked);
    }

    [Fact]
    public void Sort_and_view_are_written_by_name()
    {
        var dto = DtoOf(SidebarStoreV3.Serialize(Edited()));
        var library = SectionDto(dto, SidebarLayoutId.Library, "library");
        Assert.Equal("alphabetical", library.Sort);
        Assert.Equal("grid", library.View);
        Assert.True(library.Descending == true);
    }

    [Fact]
    public void UnknownSection_Dropped_Reported()
    {
        const string json = "{\"v\":3,\"layouts\":{\"classic\":{\"sections\":[{\"id\":\"pinned\"},{\"id\":\"spotlight\"}]}}}";

        var dropped = new List<string>();
        Assert.True(SidebarStoreV3.TryParse(Encoding.UTF8.GetBytes(json), out var state, dropped));

        Assert.Contains("spotlight", dropped);
        Assert.DoesNotContain("spotlight", IdsOf(state.Classic));
    }

    [Fact]
    public void MissingSection_Merged()
    {
        // Only one Classic section is on disk: the rest come back from the catalogue, Home first.
        const string json = "{\"v\":3,\"layouts\":{\"classic\":{\"sections\":[{\"id\":\"playlists\",\"collapsed\":true}]}}}";

        Assert.True(SidebarStoreV3.TryParse(Encoding.UTF8.GetBytes(json), out var state));

        Assert.Equal(SidebarCatalogue.KindsOf(SidebarLayoutId.Classic).Select(SidebarCatalogue.IdOf).ToArray(),
                     IdsOf(state.Classic));
        Assert.True(state.Classic.Find("playlists")!.Collapsed);
        Assert.Equal(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library).Sections.Count,
                     state.Library.Sections.Count);
    }

    [Fact]
    public void WrongVersion_NotParsed()
    {
        const string json = "{\"v\":2,\"layouts\":{}}";
        Assert.False(SidebarStoreV3.TryParse(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void Garbage_NotParsed()
    {
        Assert.False(SidebarStoreV3.TryParse("not json at all {"u8.ToArray(), out _));
        Assert.False(SidebarStoreV3.TryParse([], out _));
    }
}
