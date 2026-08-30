using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The unified sidebar projection (F.7.1/F.7.2/F.7.5–F.7.9): per-kind field derivation, the flavor mask + chip-visibility
// rule, SortStamp resolution (including the playlist first-seen fallback), folder recursion + flattening, the
// diacritics-insensitive library-only search, recency, and the pins-first partition.
//
// Driven against the REAL SidebarProjection / SidebarSearch / SidebarRecency / SidebarFirstSeen (source-included,
// engine-free), so these are not a copy of the production rules.
public class SidebarProjectionTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────
    static PlaylistSummary Pl(string id, string name, string owner = "Christos", int tracks = 3,
                              Image? cover = null, IReadOnlyList<string>? mosaic = null,
                              bool canEdit = false, bool isOwner = false) =>
        new("spotify:playlist:" + id, name, owner, tracks, cover, mosaic, canEdit, isOwner);

    static Album Al(string id, string name, params string[] artists)
    {
        var refs = new ArtistRef[artists.Length];
        for (int i = 0; i < artists.Length; i++) refs[i] = new ArtistRef("ar" + i, "spotify:artist:ar" + i, artists[i]);
        return new Album("al" + id, "spotify:album:" + id, name, null, refs, 2020, 11);
    }

    static Artist Ar(string id, string name) => new("ar" + id, "spotify:artist:" + id, name, null);
    static Show Sh(string id, string name, string publisher) => new("sh" + id, "spotify:show:" + id, name, publisher, null);

    static readonly IReadOnlyList<Album> NoAlbums = Array.Empty<Album>();
    static readonly IReadOnlyList<Artist> NoArtists = Array.Empty<Artist>();
    static readonly IReadOnlyList<Show> NoShows = Array.Empty<Show>();
    static readonly IReadOnlyList<PlaylistNode> NoTree = Array.Empty<PlaylistNode>();

    static SidebarFirstSeen Seen(long now = 1_000_000L) => new(() => now);

    static (List<SidebarLibraryEntry> Rows, SidebarProjectionResult Result) Build(
        SidebarEntryKindMask kinds,
        IReadOnlyList<PlaylistNode>? tree = null,
        IReadOnlyList<Album>? albums = null,
        IReadOnlyList<Artist>? artists = null,
        IReadOnlyList<Show>? shows = null,
        IReadOnlyDictionary<string, long>? addedAt = null,
        SidebarRecency? recency = null,
        SidebarFirstSeen? firstSeen = null,
        bool flatten = true,
        Func<string, bool>? expanded = null)
    {
        var into = new List<SidebarLibraryEntry>();
        var r = SidebarProjection.Build(into, kinds, tree ?? NoTree, albums ?? NoAlbums, artists ?? NoArtists,
                                       shows ?? NoShows, addedAt, recency, firstSeen ?? Seen(), flatten, expanded);
        return (into, r);
    }

    static string[] Names(IReadOnlyList<SidebarLibraryEntry> l)
    {
        var a = new string[l.Count];
        for (int i = 0; i < l.Count; i++) a[i] = l[i].Name;
        return a;
    }

    // ── per-kind field derivation (F.7.1) ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Playlist_DerivesIdOwnerCountAndMosaic()
    {
        var mosaic = new[] { "u1", "u2", "u3", "u4" };
        var tree = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "Focus", "Christos", 42, cover: null, mosaic: mosaic, isOwner: true)) };
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, tree);

        var e = Assert.Single(rows);
        Assert.Equal("pl:spotify:playlist:p1", e.Id);
        Assert.Equal(SidebarEntryKind.Playlist, e.Kind);
        Assert.Equal("spotify:playlist:p1", e.Uri);
        Assert.Equal("Focus", e.Name);
        Assert.Equal("Christos", e.Creator);
        Assert.Equal("Christos", e.OwnerName);
        Assert.Equal(42, e.ChildCount);
        Assert.Equal(42, e.TrackCount);
        Assert.Equal(mosaic, e.MosaicTiles);                     // no cover ⇒ the 2×2 mosaic tiles ride along
        Assert.False(e.Circular);
        Assert.True(e.IsPlayable);
        Assert.True(e.IsOwner);
        Assert.Equal("pl:spotify:playlist:p1", e.RouteKey);       // the id IS the route key (F.5.4)
        Assert.Equal(0L, e.AddedAtMs);                             // playlists have no server add-date — stated honestly
    }

    [Fact]
    public void PlaylistWithACover_CarriesNoMosaic()
    {
        var tree = new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("p1", "Has cover", cover: new Image("https://x/cover.jpg"), mosaic: new[] { "a", "b" })),
        };
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, tree);
        Assert.Null(Assert.Single(rows).MosaicTiles);
    }

    [Fact]
    public void Album_JoinsUpToThreeArtists_AndKeepsTheFirstOne()
    {
        var (rows, _) = Build(SidebarEntryKindMask.Album, albums: new[] { Al("a1", "Discovery", "Daft Punk") });
        var one = Assert.Single(rows);
        Assert.Equal("album:spotify:album:a1", one.Id);
        Assert.Equal("Daft Punk", one.Creator);
        Assert.Equal("Daft Punk", one.FirstArtistName);
        Assert.Equal(11, one.TrackCount);

        var (many, _) = Build(SidebarEntryKindMask.Album, albums: new[] { Al("a2", "Split", "A", "B", "C", "D") });
        Assert.Equal("A, B, C…", Assert.Single(many).Creator);
        Assert.Equal("A", many[0].FirstArtistName);
    }

    [Fact]
    public void Artist_IsCircular_AndHasNoCreator()
    {
        var (rows, _) = Build(SidebarEntryKindMask.Artist, artists: new[] { Ar("x", "Radiohead") });
        var e = Assert.Single(rows);
        Assert.Equal("artist:spotify:artist:x", e.Id);
        Assert.True(e.Circular);
        Assert.Equal("", e.Creator);
        Assert.Equal(0, e.ChildCount);
        Assert.False(e.IsPlayable);
    }

    [Fact]
    public void Shows_ProjectPublisherAsSubtitle()
    {
        var (rows, _) = Build(SidebarEntryKindMask.Show, shows: new[] { Sh("s1", "The Daily", "The New York Times") });
        var e = Assert.Single(rows);
        Assert.Equal("show:spotify:show:s1", e.Id);
        Assert.Equal("The New York Times", e.Creator);
        Assert.Equal("The New York Times", e.Publisher);
        Assert.Equal("", e.OwnerName);                            // an owner-name reader must not see a publisher
        Assert.True(e.IsPlayable);
    }

    [Fact]
    public void KindMask_SelectsExactlyTheRequestedFamilies()
    {
        var tree = new PlaylistNode[] { new PlaylistLeaf(Pl("p", "P")) };
        var albums = new[] { Al("a", "A", "Artist") };
        var artists = new[] { Ar("r", "R") };
        var shows = new[] { Sh("s", "S", "Pub") };

        var (all, _) = Build(SidebarEntryKindMask.All, tree, albums, artists, shows);
        Assert.Equal(4, all.Count);

        var (onlyShows, _) = Build(SidebarEntryKindMask.Show, tree, albums, artists, shows);
        Assert.Equal(SidebarEntryKind.Show, Assert.Single(onlyShows).Kind);

        Assert.Equal(SidebarEntryKindMask.PlaylistTree, SidebarEntryKinds.From(SidebarV3Filter.Playlists));
        Assert.Equal(SidebarEntryKindMask.Show, SidebarEntryKinds.From(SidebarV3Filter.Podcasts));
        Assert.Equal(SidebarEntryKindMask.All, SidebarEntryKinds.From(SidebarV3Filter.All));
        Assert.Equal(SidebarEntryKindMask.PlaylistTree | SidebarEntryKindMask.Album,
                     SidebarEntryKinds.From(Wavee.Core.Sidebar.SidebarEntityKinds.Playlists | Wavee.Core.Sidebar.SidebarEntityKinds.Albums));
    }

    // ── folder recursion (F.7.3 + §3.0 obligation 3) ──────────────────────────────────────────────────────────────────
    static IReadOnlyList<PlaylistNode> NestedTree() => new PlaylistNode[]
    {
        new PlaylistLeaf(Pl("top", "Top")),
        new PlaylistFolder("cafe", "Cafe & chill", new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("in1", "Inner one")),
            new PlaylistFolder("night", "Late night", new PlaylistNode[]
            {
                new PlaylistLeaf(Pl("deep", "Deep")),
            }),
        }),
    };

    [Fact]
    public void FlattenedTree_StampsDepthAndContainingFolder()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true);

        Assert.Equal(new[] { "Top", "Cafe & chill", "Inner one", "Late night", "Deep" }, Names(rows));

        Assert.Equal(0, rows[0].Depth);
        Assert.Equal("", rows[0].FolderId);

        var cafe = rows[1];
        Assert.True(cafe.IsFolder);
        Assert.Equal("folder:cafe", cafe.Id);
        Assert.Equal("cafe", cafe.FolderId);                       // a folder row carries its OWN group id
        Assert.Equal(2, cafe.ChildCount);                          // DIRECT children only
        Assert.Null(cafe.RouteKey);                                // a folder never navigates
        Assert.Equal("", cafe.Uri);

        Assert.Equal(1, rows[2].Depth);
        Assert.Equal("cafe", rows[2].FolderId);
        Assert.Equal("Cafe & chill", rows[2].FolderName);

        Assert.Equal(2, rows[4].Depth);                            // Deep, inside Late night, inside Cafe
        Assert.Equal("night", rows[4].FolderId);
    }

    /// <summary>Every row carries the folder it SITS IN, folders included — which <c>FolderId</c> cannot say for a
    /// folder row (there it is the row's OWN group id). It is what "Move out of {folder}" is built from, and the only
    /// way a row can tell nested from top-level at all.</summary>
    [Fact]
    public void NestedEntries_CarryTheirParentFolder()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true);
        // Top, Cafe & chill, Inner one, Late night, Deep

        Assert.Equal("", rows[0].ParentFolderId);                   // a top-level playlist has nothing to move out of
        Assert.Equal("", rows[1].ParentFolderId);                   // …and neither has a top-level FOLDER
        Assert.Equal("cafe", rows[1].FolderId);                     // whose own id still lives in FolderId

        Assert.Equal("cafe", rows[2].ParentFolderId);               // Inner one
        Assert.Equal("Cafe & chill", rows[2].ParentFolderName);

        var night = rows[3];                                        // a NESTED folder: parent ≠ own id
        Assert.True(night.IsFolder);
        Assert.Equal("night", night.FolderId);
        Assert.Equal("cafe", night.ParentFolderId);
        Assert.Equal("Cafe & chill", night.ParentFolderName);

        Assert.Equal("night", rows[4].ParentFolderId);              // Deep
        Assert.Equal("Late night", rows[4].ParentFolderName);
    }

    /// <summary>A RENAME is a name change and nothing else: the entry id and the group id are the client-minted
    /// rootlist groupId, which is exactly why folder expansion state and folder pins survive a rename with no
    /// migration at all.</summary>
    [Fact]
    public void RenamedFolder_KeepsItsEntryIdAndGroupId()
    {
        var before = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true).Rows[1];

        var renamed = new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("top", "Top")),
            new PlaylistFolder("cafe", "Mornings", new PlaylistNode[]
            {
                new PlaylistLeaf(Pl("in1", "Inner one")),
                new PlaylistFolder("night", "Late night", new PlaylistNode[] { new PlaylistLeaf(Pl("deep", "Deep")) }),
            }),
        };
        var after = Build(SidebarEntryKindMask.PlaylistTree, renamed, flatten: true).Rows[1];

        Assert.Equal("Cafe & chill", before.Name);
        Assert.Equal("Mornings", after.Name);
        Assert.Equal(before.Id, after.Id);                          // "folder:cafe" — the pin key
        Assert.Equal(before.FolderId, after.FolderId);              // "cafe" — the expansion key
    }

    /// <summary>A folder DELETE removes the marker pair and nothing else — the playlists inside move up one level. At
    /// the projection that is: the folder row is gone, and each child re-parents onto the deleted folder's parent, one
    /// depth shallower. (The wire half is <c>RootlistFolderOpsTests</c>; this pins what the sidebar then shows.)</summary>
    [Fact]
    public void DeletedFolder_ReparentsItsChildrenAtTheParentDepth()
    {
        var deep = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true).Rows[4];
        Assert.Equal(2, deep.Depth);
        Assert.Equal("night", deep.FolderId);

        // "Late night" deleted: its one playlist stays, now a direct child of "Cafe & chill".
        var afterDelete = new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("top", "Top")),
            new PlaylistFolder("cafe", "Cafe & chill", new PlaylistNode[]
            {
                new PlaylistLeaf(Pl("in1", "Inner one")),
                new PlaylistLeaf(Pl("deep", "Deep")),
            }),
        };
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, afterDelete, flatten: true);

        Assert.Equal(new[] { "Top", "Cafe & chill", "Inner one", "Deep" }, Names(rows));
        var moved = rows[3];
        Assert.Equal(1, moved.Depth);                               // one level up
        Assert.Equal("cafe", moved.FolderId);
        Assert.Equal("cafe", moved.ParentFolderId);
        Assert.Equal("Cafe & chill", moved.ParentFolderName);
    }

    [Fact]
    public void CollapsedFolder_IsOpaque_AndAnExpandedOneRevealsItsChildren()
    {
        var (collapsed, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: false);
        Assert.Equal(new[] { "Top", "Cafe & chill" }, Names(collapsed));

        var (expanded, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: false,
                                  expanded: id => id == "cafe");
        Assert.Equal(new[] { "Top", "Cafe & chill", "Inner one", "Late night" }, Names(expanded));
    }

    [Fact]
    public void PlaylistsOnlyMask_HidesFoldersButKeepsEveryLeaf()
    {
        // The projection-level twin of FlatConsumers_StillSeeEveryPlaylist: dropping the Folder bit must never drop the
        // playlists inside a folder.
        var (rows, _) = Build(SidebarEntryKindMask.Playlist, NestedTree(), flatten: false);
        Assert.Equal(new[] { "Top", "Inner one", "Deep" }, Names(rows));
        Assert.Equal(3, SidebarTree.CountLeaves(NestedTree()));
    }

    [Fact]
    public void SourceOrder_FollowsRootlistOrderAcrossFolders()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true);
        for (int i = 0; i < rows.Count; i++) Assert.Equal(i, rows[i].SourceOrder);
    }

    // ── flavor mask + chip visibility (F.7.2) ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Flavor_PartitionsByYou_BySpotify_AndMixed()
    {
        Assert.Equal(SidebarPlaylistFlavor.ByYou, SidebarProjection.FlavorOf(Pl("a", "A", "Christos", isOwner: true)));
        Assert.Equal(SidebarPlaylistFlavor.BySpotify, SidebarProjection.FlavorOf(Pl("b", "B", "spotify")));
        Assert.Equal(SidebarPlaylistFlavor.Mixed, SidebarProjection.FlavorOf(Pl("c", "C", "Someone", canEdit: true)));
        Assert.Equal(SidebarPlaylistFlavor.Mixed, SidebarProjection.FlavorOf(Pl("d", "D", "Someone else")));
        Assert.Equal(SidebarPlaylistFlavor.None, SidebarProjection.FlavorOf(Pl("e", "E", "")));
    }

    [Fact]
    public void QualifierChips_HiddenUntilTwoDistinctKnownFlavorsExist()
    {
        var onlyMine = new PlaylistNode[] { new PlaylistLeaf(Pl("a", "A", "Me", isOwner: true)) };
        var (_, r1) = Build(SidebarEntryKindMask.PlaylistTree, onlyMine);
        Assert.False(SidebarProjection.QualifiersAvailable(r1.FlavorMask));

        var mixed = new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("a", "A", "Me", isOwner: true)),
            new PlaylistLeaf(Pl("b", "B", "Spotify")),
            new PlaylistLeaf(Pl("c", "C", "")),                      // unknown never counts toward the two
        };
        var (_, r2) = Build(SidebarEntryKindMask.PlaylistTree, mixed);
        Assert.True(SidebarProjection.QualifiersAvailable(r2.FlavorMask));

        var unknownOnly = new PlaylistNode[] { new PlaylistLeaf(Pl("a", "A", "")), new PlaylistLeaf(Pl("b", "B", "")) };
        var (_, r3) = Build(SidebarEntryKindMask.PlaylistTree, unknownOnly);
        Assert.False(SidebarProjection.QualifiersAvailable(r3.FlavorMask));
    }

    [Fact]
    public void MatchesQualifier_TreatsAnyAsEverything()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree,
                              new PlaylistNode[] { new PlaylistLeaf(Pl("a", "A", "Me", isOwner: true)) });
        var e = Assert.Single(rows);
        Assert.True(e.MatchesQualifier(SidebarV3Qualifier.Any));
        Assert.True(e.MatchesQualifier(SidebarV3Qualifier.ByYou));
        Assert.False(e.MatchesQualifier(SidebarV3Qualifier.BySpotify));
    }

    // ── SortStamp resolution (F.7.5) ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SortStamp_UsesTheRealServerTimestamp_WhenTheStoreHasOne()
    {
        var addedAt = new Dictionary<string, long>(StringComparer.Ordinal) { ["spotify:album:a1"] = 555L };
        var (rows, _) = Build(SidebarEntryKindMask.Album, albums: new[] { Al("a1", "A", "X") }, addedAt: addedAt);
        var e = Assert.Single(rows);
        Assert.Equal(555L, e.AddedAtMs);
        Assert.Equal(555L, e.SortStamp);
    }

    [Fact]
    public void SortStamp_FallsBackToTheFirstSeenStamp_ForPlaylistsAndUndatedSaves()
    {
        var seen = Seen(now: 9_000L);
        var tree = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "P1")) };
        var (rows, r) = Build(SidebarEntryKindMask.PlaylistTree, tree, firstSeen: seen);
        Assert.Equal(0L, rows[0].AddedAtMs);
        Assert.Equal(9_000L, rows[0].SortStamp);
        Assert.Equal(1, r.NewFirstSeenStamps);                     // a fresh stamp ⇒ the owner must persist
    }

    [Fact]
    public void FirstSeen_IsStableAcrossRebuilds_AndOnlyNewIdsCountAsNew()
    {
        var clock = 100L;
        var seen = new SidebarFirstSeen(() => clock);
        var tree1 = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "P1")) };
        var (rows1, r1) = Build(SidebarEntryKindMask.PlaylistTree, tree1, firstSeen: seen);
        Assert.Equal(1, r1.NewFirstSeenStamps);

        clock = 500L;
        var tree2 = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "P1")), new PlaylistLeaf(Pl("p2", "P2")) };
        var (rows2, r2) = Build(SidebarEntryKindMask.PlaylistTree, tree2, firstSeen: seen);

        Assert.Equal(rows1[0].SortStamp, rows2[0].SortStamp);      // an existing playlist keeps its original stamp
        Assert.Equal(100L, rows2[0].SortStamp);
        Assert.Equal(500L, rows2[1].SortStamp);                    // the newly added one is genuinely newer
        Assert.Equal(1, r2.NewFirstSeenStamps);
    }

    [Fact]
    public void FirstSeen_PrunesIdsThatLeftTheLibrary_AndSurvivesARoundTrip()
    {
        var seen = new SidebarFirstSeen(() => 42L);
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, seen.PruneTo(new[] { "pl:a" }));
        Assert.Equal(42L, seen.Peek("pl:a"));
        Assert.Equal(0L, seen.Peek("pl:b"));

        var snapshot = new List<KeyValuePair<string, long>>();
        seen.CopyTo(snapshot);
        var reloaded = new SidebarFirstSeen(() => 999L);
        reloaded.Load(snapshot);
        Assert.Equal(42L, reloaded.Peek("pl:a"));
        Assert.Equal(0, reloaded.NewStamps);
        Assert.Equal(42L, reloaded.Stamp("pl:a"));                 // a known id never re-stamps
        Assert.Equal(0, reloaded.NewStamps);
    }

    [Fact]
    public void FrozenFirstSeen_NeverRecords()
    {
        var tree = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "P1")) };
        var (rows, r) = Build(SidebarEntryKindMask.PlaylistTree, tree, firstSeen: SidebarFirstSeen.Frozen);
        Assert.Equal(0, r.NewFirstSeenStamps);
        Assert.True(rows[0].SortStamp > 0);                        // still sortable ("just seen"), just not persisted
        Assert.Equal(0, SidebarFirstSeen.Frozen.Count);
    }

    // ── recency (F.7.6) ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Recency_IsAnIdentityLookupOnTheEntryId_NewestVisitWins()
    {
        var visits = new List<SidebarVisit>
        {
            new("pl:spotify:playlist:p1", 100),
            new("album:spotify:album:a1", 150),
            new("pl:spotify:playlist:p1", 300),                     // later visit to the same route
        };
        var recency = SidebarRecency.Build(visits);
        Assert.Equal(300L, recency.LastVisitedTicks("pl:spotify:playlist:p1"));
        Assert.Equal(150L, recency.LastVisitedTicks("album:spotify:album:a1"));
        Assert.Equal(0L, recency.LastVisitedTicks("show:spotify:show:s1"));

        var tree = new PlaylistNode[] { new PlaylistLeaf(Pl("p1", "P1")) };
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, tree, recency: recency);
        Assert.Equal(300L, rows[0].LastVisitedTicksUtc);
    }

    [Fact]
    public void Recency_BuildsFromAnyRowShape_ViaTheAccessorOverload()
    {
        var rows = new[] { ("home", 10L), ("pl:x", 20L), ("pl:x", 40L) };
        var recency = SidebarRecency.Build(rows, static r => r.Item1, static r => r.Item2);
        Assert.Equal(40L, recency.LastVisitedTicks("pl:x"));
        Assert.Equal(10L, recency.LastVisitedTicks("home"));
        Assert.Same(SidebarRecency.Empty, SidebarRecency.Build(Array.Empty<SidebarVisit>()));
    }

    // ── search (F.7.8) ────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Search_MatchesNameCaseAndDiacriticsInsensitively()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree,
                              new PlaylistNode[] { new PlaylistLeaf(Pl("p", "Café Crème", "Björk")) });
        var e = Assert.Single(rows);
        Assert.True(SidebarSearch.Matches(in e, "cafe"));
        Assert.True(SidebarSearch.Matches(in e, "CRÈME"));
        Assert.True(SidebarSearch.Matches(in e, "creme"));
        Assert.True(SidebarSearch.Matches(in e, ""));               // an empty query matches everything
        Assert.False(SidebarSearch.Matches(in e, "zzz"));
    }

    [Fact]
    public void Search_MatchesCreatorOnlyForTwoOrMoreCharacters()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree,
                              new PlaylistNode[] { new PlaylistLeaf(Pl("p", "Zzz", "Bjork")) });
        var e = Assert.Single(rows);
        Assert.False(SidebarSearch.Matches(in e, "b"));             // one letter must not match every creator
        Assert.True(SidebarSearch.Matches(in e, "bj"));
    }

    [Fact]
    public void Search_NormalizesTheQueryOnce()
    {
        Assert.Equal("cafe", SidebarSearch.Normalize("  cafe \n"));
        Assert.Equal("", SidebarSearch.Normalize(null));
    }

    // ── pins-first (F.7.9) ────────────────────────────────────────────────────────────────────────────────────────────
    static SidebarPin Pin(string id, string name) =>
        new(id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), name, 0);

    [Fact]
    public void Pins_LeadInPinOrder_RegardlessOfTheSortOrder()
    {
        var tree = new PlaylistNode[]
        {
            new PlaylistLeaf(Pl("a", "Alpha")),
            new PlaylistLeaf(Pl("b", "Bravo")),
            new PlaylistLeaf(Pl("c", "Charlie")),
        };
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, tree);
        SidebarSort.Apply(rows, SidebarV3Sort.Alphabetical, desc: false);

        var pins = new[] { Pin("pl:spotify:playlist:c", "Charlie"), Pin("pl:spotify:playlist:a", "Alpha") };
        int band = SidebarProjection.PinsFirst(rows, pins);

        Assert.Equal(2, band);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(rows));   // PIN order, not sort order
        Assert.True(rows[0].IsPinned);
        Assert.True(rows[1].IsPinned);
        Assert.False(rows[2].IsPinned);
    }

    [Fact]
    public void Pins_OutsideTheCurrentFilterAreSimplyAbsent()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree,
                              new PlaylistNode[] { new PlaylistLeaf(Pl("a", "Alpha")) });
        var pins = new[] { Pin("album:spotify:album:zz", "A pinned album"), Pin("pl:spotify:playlist:a", "Alpha") };
        int band = SidebarProjection.PinsFirst(rows, pins);
        Assert.Equal(1, band);
        Assert.Equal(new[] { "Alpha" }, Names(rows));
    }

    [Fact]
    public void Pins_NoPins_IsANoOp()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree,
                              new PlaylistNode[] { new PlaylistLeaf(Pl("a", "Alpha")), new PlaylistLeaf(Pl("b", "Bravo")) });
        Assert.Equal(0, SidebarProjection.PinsFirst(rows, Array.Empty<SidebarPin>()));
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(rows));
        Assert.False(rows[0].IsPinned);
    }

    // ── the pin id scheme (F.5.4) ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PinId_MapsUrisAndRefusesTracksAndEpisodes()
    {
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.FromUri("spotify:playlist:x"));
        Assert.Equal("pl:wavee:playlist:x", SidebarPinId.FromUri("wavee:playlist:x"));
        Assert.Equal("album:spotify:album:x", SidebarPinId.FromUri("spotify:album:x"));
        Assert.Equal("artist:spotify:artist:x", SidebarPinId.FromUri("spotify:artist:x"));
        Assert.Equal("show:spotify:show:x", SidebarPinId.FromUri("spotify:show:x"));
        Assert.Equal("liked", SidebarPinId.FromUri("spotify:collection:tracks"));   // Liked Songs is a ROUTE pin
        Assert.Null(SidebarPinId.FromUri("spotify:track:x"));
        Assert.Null(SidebarPinId.FromUri("spotify:episode:x"));
        Assert.Null(SidebarPinId.FromUri(""));
        Assert.Null(SidebarPinId.FromUri(null));
    }

    [Fact]
    public void PinId_KindAndRouteRoundTrip()
    {
        Assert.Equal(SidebarEntryKind.Playlist, SidebarPinId.KindOf("pl:spotify:playlist:x"));
        Assert.Equal(SidebarEntryKind.Album, SidebarPinId.KindOf("album:spotify:album:x"));
        Assert.Equal(SidebarEntryKind.Artist, SidebarPinId.KindOf("artist:spotify:artist:x"));
        Assert.Equal(SidebarEntryKind.Show, SidebarPinId.KindOf("show:spotify:show:x"));
        Assert.Equal(SidebarEntryKind.Folder, SidebarPinId.KindOf("folder:6a1f2c"));
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf("liked"));

        Assert.Equal("show:spotify:show:x", SidebarPinId.RouteOf("show:spotify:show:x"));
        Assert.Null(SidebarPinId.RouteOf("folder:6a1f2c"));         // a folder expands in place; it never navigates
        Assert.Equal("6a1f2c", SidebarPinId.FolderIdOf(SidebarPinId.ForFolder("6a1f2c")));
        Assert.Equal("spotify:show:x", SidebarPinId.UriOf("show:spotify:show:x"));
        Assert.Equal("spotify:collection:tracks", SidebarPinId.UriOf("liked"));
        Assert.Equal("", SidebarPinId.UriOf("folder:6a1f2c"));
    }

    [Fact]
    public void PinId_AcceptsDurableDestinations_ButRejectsShellInternals()
    {
        Assert.Equal("home", SidebarPinId.FromRoute("home"));
        Assert.Equal("history", SidebarPinId.FromRoute("history"));
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.FromRoute("pl:spotify:playlist:x"));
        Assert.Equal("browse:spotify:page:music", SidebarPinId.FromRoute("browse:spotify:page:music"));
        Assert.Null(SidebarPinId.FromRoute("settings"));
        Assert.Null(SidebarPinId.FromRoute("api-console"));
        Assert.Null(SidebarPinId.FromRoute(""));
    }

    [Fact]
    public void PinId_EntryIdIsThePinId()
    {
        var (rows, _) = Build(SidebarEntryKindMask.PlaylistTree, NestedTree(), flatten: true);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];   // explicit `in` needs an lvalue — a list indexer result is not one
            Assert.Equal(row.Id, row.PinKey);
            Assert.Equal(row.Id, SidebarPinId.FromEntry(in row));
        }
        var route = SidebarLibraryEntry.ForRoute("liked", "Liked Songs");
        Assert.Equal("liked", SidebarPinId.FromEntry(in route));
        var unpinnable = SidebarLibraryEntry.ForRoute("settings", "Settings");
        Assert.Null(SidebarPinId.FromEntry(in unpinnable));
    }

    [Fact]
    public void Build_ReusesTheCallersList_AndReportsItsCount()
    {
        var into = new List<SidebarLibraryEntry> { SidebarLibraryEntry.ForRoute("stale", "Stale") };
        var r = SidebarProjection.Build(into, SidebarEntryKindMask.All, NestedTree(), NoAlbums, NoArtists, NoShows,
                                       null, null, Seen(), includeFolderChildren: true);
        Assert.Equal(into.Count, r.Count);
        Assert.DoesNotContain("Stale", Names(into));                // the list is cleared, never appended to
    }
}
