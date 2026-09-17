// ── Wavee.Tests/SidebarPinSyncTests.cs — the ylpin ↔ sidebar pin bridge (restored from 0.2.9) ──────────────────────
//
// Gap batch B2 (G-049). 0.2.9's `SidebarPinSyncTests` drove `App/SidebarPinSync.cs` over an `InMemoryStore` and a
// recording `IPinMutations`. The 0.3 bridge is `LibraryPinSync` (Spotify/Spotify.Encode.cs): the server set arrives as
// `PinWire`s read off the `Pins` edge instead of a store change, "converged" is the edge having landed whole, and the
// write seam is a delegate. The facts are the same twelve, restated over that shape: remote adds pin locally with zero
// writes, a local pin of a syncable kind is pushed once, the first converged walk migrates instead of sweeping, a pending
// write shields its pin, and a shape the sidebar cannot represent never becomes a pin and never causes a write.
// UI-thread-only by contract, so every call here is synchronous. `EntitiesCollection`: the pin-id map parses uris, which
// interns into the process-wide table.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SidebarPinSyncTests
{
    sealed class Rig
    {
        public readonly SidebarPinStore Pins = new();
        public readonly MemoryAppSettings Settings = new();
        public readonly List<(string Uri, bool Pinned)> Writes = new();
        public readonly LibraryPinSync Sync;

        public Rig(bool migrated, Func<string, bool>? hasPending = null)
        {
            Settings.Set(Platform.Keys.PinsMigratedToServer, migrated);
            Sync = new LibraryPinSync(Pins, Settings, (uri, pinned) => Writes.Add((uri, pinned)), hasPending ?? (_ => false),
                id => id == "liked" ? "Liked Songs" : "");
        }

        public void Server(bool converged, params string[] uris)
        {
            var wires = new PinWire[uris.Length];
            for (int i = 0; i < uris.Length; i++) wires[i] = new PinWire(uris[i], 1000 + i);
            Sync.ApplyServer(wires, converged);
        }
    }

    static SidebarPin Pin(string id, SidebarEntryKind kind, string uri = "", string name = "") => new(id, kind, uri, name, 0);

    [Fact]
    public void RemoteAdd_PinsLocally_WithZeroWrites()
    {
        var rig = new Rig(migrated: false);
        rig.Server(converged: false, "spotify:playlist:new");

        Assert.True(rig.Pins.IsPinned("pl:spotify:playlist:new"));
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void RemoteAdd_OfATrackUri_IsIgnored()
    {
        TestScope.Fresh();
        var rig = new Rig(migrated: false);
        rig.Server(converged: false, "spotify:track:t1");

        Assert.Empty(rig.Pins);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void LocalPin_OfASpotifyPlaylist_IsPushedOnce()
    {
        var rig = new Rig(migrated: false);
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x")));

        Assert.Equal(new[] { ("spotify:playlist:x", true) }, rig.Writes);
    }

    [Fact]
    public void LocalPin_OfAFolder_IsPushedOnce()
    {
        var rig = new Rig(migrated: false);
        Assert.True(rig.Pins.Pin(Pin("folder:ab12", SidebarEntryKind.Folder, "", "F")));

        Assert.Equal(new[] { ("spotify:folder:ab12", true) }, rig.Writes);
    }

    [Fact]
    public void RemoteAdd_OfAFolder_PinsLocally_AsAFolderKind()
    {
        var rig = new Rig(migrated: false);
        rig.Server(converged: false, "spotify:folder:abc");

        Assert.True(rig.Pins.IsPinned("folder:abc"));
        var pin = rig.Pins[rig.Pins.IndexOf("folder:abc")];
        Assert.Equal(SidebarEntryKind.Folder, pin.Kind);
        Assert.Equal("", pin.Uri);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void UnpinThenUndo_IssuesTwoWrites_EndingPinned()
    {
        var rig = new Rig(migrated: false);
        var pin = Pin("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x");
        Assert.True(rig.Pins.Pin(pin));
        rig.Writes.Clear();

        int at = rig.Pins.Unpin("pl:spotify:playlist:x");
        Assert.True(rig.Pins.Insert(pin, at));                         // the toast's undo

        Assert.Equal(new[] { ("spotify:playlist:x", false), ("spotify:playlist:x", true) }, rig.Writes);
    }

    [Fact]
    public void NotConverged_WithAnEmptyMirror_NeverRemovesLocalPins()
    {
        var rig = new Rig(migrated: true);
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a")));
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:b", SidebarEntryKind.Playlist, "spotify:playlist:b")));
        rig.Writes.Clear();

        rig.Server(converged: false);                                  // an empty, not-yet-walked server set

        Assert.Equal(2, rig.Pins.Count);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void Converged_NotYetMigrated_PushesEveryLocalSyncablePin_ButRemovesNothing()
    {
        var rig = new Rig(migrated: false);
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a")));
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:b", SidebarEntryKind.Playlist, "spotify:playlist:b")));
        Assert.True(rig.Pins.Pin(Pin("folder:ab12", SidebarEntryKind.Folder, "", "F")));
        rig.Writes.Clear();

        rig.Server(converged: true);                                   // the first converged walk → migration

        Assert.Equal(3, rig.Pins.Count);                               // migration only ever ADDS to the server
        Assert.Equal(3, rig.Writes.Count);                             // folders are syncable too
        Assert.Contains(("spotify:playlist:a", true), rig.Writes);
        Assert.Contains(("spotify:playlist:b", true), rig.Writes);
        Assert.Contains(("spotify:folder:ab12", true), rig.Writes);
        Assert.True(rig.Settings.Get(Platform.Keys.PinsMigratedToServer));
        Assert.True(rig.Sync.Migrated);
    }

    [Fact]
    public void Migrated_AMissingSyncablePinIsRemoved_WhileAPendingOneIsKept()
    {
        var rig = new Rig(migrated: true, hasPending: uri => uri == "spotify:playlist:pending");
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:missing", SidebarEntryKind.Playlist, "spotify:playlist:missing")));
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:pending", SidebarEntryKind.Playlist, "spotify:playlist:pending")));
        Assert.True(rig.Pins.Pin(Pin("home", SidebarEntryKind.AppRoute, "", "Home")));
        rig.Writes.Clear();

        rig.Server(converged: true);                                   // both playlist pins are "missing"

        Assert.False(rig.Pins.IsPinned("pl:spotify:playlist:missing"));
        Assert.True(rig.Pins.IsPinned("pl:spotify:playlist:pending")); // shielded by the in-flight write
        Assert.True(rig.Pins.IsPinned("home"));                        // never syncable — always kept
        Assert.Empty(rig.Writes);                                      // ApplyServer never writes back
    }

    [Fact]
    public void RemoteAdd_OfTheLikedRoute_CarriesTheRouteTitle()
    {
        var rig = new Rig(migrated: false);
        rig.Server(converged: false, "spotify:collection");

        Assert.True(rig.Pins.IsPinned("liked"));
        var pin = rig.Pins[rig.Pins.IndexOf("liked")];
        Assert.Equal(SidebarEntryKind.AppRoute, pin.Kind);
        Assert.Equal("Liked Songs", pin.Name);
    }

    [Fact]
    public void RemoteAdd_OfAnEntity_LeavesNameEmpty_ForTouchToFill()
    {
        var rig = new Rig(migrated: false);
        rig.Server(converged: false, "spotify:playlist:new");

        Assert.Equal("", rig.Pins[rig.Pins.IndexOf("pl:spotify:playlist:new")].Name);
    }

    // The preservation invariant: a shape this client cannot represent as a local pin never becomes one and never causes
    // a write, so a foreign client's pin survives on the server — across an initial convergence AND the one-time
    // migration walk.
    [Theory]
    [InlineData("spotify:collection:your-episodes")]
    [InlineData("spotify:user:bob:collection:your-episodes")]
    [InlineData("spotify:local-files")]
    [InlineData("spotify:audiobook:x")]
    [InlineData("spotify:track:x")]
    [InlineData("spotify:episode:x")]
    [InlineData("spotify:prerelease:x")]
    [InlineData("spotify:station:x")]
    public void UnrepresentableRemotePins_ProduceNoLocalPinAndNoWrite_EvenAfterMigrationAndConvergence(string uri)
    {
        TestScope.Fresh();
        var rig = new Rig(migrated: false);
        rig.Server(converged: true, uri);

        Assert.Empty(rig.Pins);
        Assert.Empty(rig.Writes);

        rig.Server(converged: true, uri);                              // again, now past the migration walk

        Assert.Empty(rig.Pins);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void ServerPins_AreAppendedOldestFirst()
    {
        var rig = new Rig(migrated: false);
        rig.Sync.ApplyServer([new PinWire("spotify:playlist:new", 3000), new PinWire("spotify:playlist:old", 1000)], converged: false);

        Assert.Equal(new[] { "pl:spotify:playlist:old", "pl:spotify:playlist:new" }, rig.Pins.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Dispose_HandsTheLocalChangeHookBack()
    {
        var rig = new Rig(migrated: false);
        rig.Sync.Dispose();
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x")));
        Assert.Empty(rig.Writes);
    }
}
