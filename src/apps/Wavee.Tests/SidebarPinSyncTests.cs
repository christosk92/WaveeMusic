using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The App/SidebarPinSync.cs bridge (§1.5/§1.6/§1.7 of
/// docs/plans/wavee/pin-spotify-sync-implementation.md): drives the real bridge over an <see cref="InMemoryStore"/> and
/// a recording <see cref="IPinMutations"/> fake, with a synchronous "post" so every assertion runs deterministically.</summary>
public class SidebarPinSyncTests
{
    sealed class RecordingPinMutations : IPinMutations
    {
        public readonly List<(string Uri, bool Pinned)> Calls = new();
        public Task SetPinnedAsync(string wireUri, bool pinned, CancellationToken ct = default)
        {
            Calls.Add((wireUri, pinned));
            return Task.CompletedTask;
        }
    }

    const string User = "bob";
    static SidebarPin Pin(string id, SidebarEntryKind kind, string uri = "", string name = "")
        => new(id, kind, uri, name, 0);

    static (InMemoryStore Store, SidebarPinStore Pins, RecordingPinMutations Mutations, MemoryAppSettings Settings, SidebarPinSync Sync)
        Rig(bool converged, bool migrated, System.Func<string, bool>? hasPending = null)
    {
        var store = new InMemoryStore();
        var pins = new SidebarPinStore();
        var mutations = new RecordingPinMutations();
        var settings = new MemoryAppSettings();
        settings.Set(SidebarKeys.PinsMigratedToServer, migrated);
        var sync = new SidebarPinSync(store, pins, mutations, settings,
            () => User, () => converged, hasPending ?? (_ => false));
        sync.Activate(a => a());   // synchronous post — every assertion below runs deterministically
        return (store, pins, mutations, settings, sync);
    }

    [Fact]
    public void RemoteAdd_PinsLocally_WithZeroWrites()
    {
        var (store, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        store.SetSaved("pins", "spotify:playlist:new", true, SyncState.Confirmed, 1000);

        Assert.True(pins.IsPinned("pl:spotify:playlist:new"));
        Assert.Empty(mutations.Calls);
    }

    [Fact]
    public void RemoteAdd_OfATrackUri_IsIgnored()
    {
        var (store, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        store.SetSaved("pins", "spotify:track:t1", true, SyncState.Confirmed, 1000);

        Assert.Empty(pins);
        Assert.Empty(mutations.Calls);
    }

    [Fact]
    public void LocalPin_OfASpotifyPlaylist_IsPushedOnce()
    {
        var (_, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x")));

        Assert.Equal(new[] { ("spotify:playlist:x", true) }, mutations.Calls);
    }

    [Fact]
    public void LocalPin_OfAFolder_IsNeverPushed()
    {
        var (_, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        Assert.True(pins.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));

        Assert.Empty(mutations.Calls);
    }

    [Fact]
    public void UnpinThenUndo_IssuesTwoWrites_EndingPinned()
    {
        var (_, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        var pin = Pin("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x");
        Assert.True(pins.Pin(pin));
        mutations.Calls.Clear();   // isolate the unpin/undo pair from the initial pin's write

        int at = pins.Unpin("pl:spotify:playlist:x");
        Assert.True(pins.Insert(pin, at));   // the toast's undo

        Assert.Equal(new[] { ("spotify:playlist:x", false), ("spotify:playlist:x", true) }, mutations.Calls);
    }

    [Fact]
    public void NotConverged_WithAnEmptyMirror_NeverRemovesLocalPins()
    {
        var (store, pins, mutations, _, _) = Rig(converged: false, migrated: true);
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a")));
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:b", SidebarEntryKind.Playlist, "spotify:playlist:b")));
        mutations.Calls.Clear();

        store.Bump("anything", CollectionKind.Pins);   // a Pins-kind change with an empty server mirror

        Assert.Equal(2, pins.Count);
        Assert.Empty(mutations.Calls);
    }

    [Fact]
    public void Converged_NotYetMigrated_PushesEveryLocalSyncablePin_ButRemovesNothing()
    {
        var (store, pins, mutations, settings, _) = Rig(converged: true, migrated: false);
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a")));
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:b", SidebarEntryKind.Playlist, "spotify:playlist:b")));
        Assert.True(pins.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));
        mutations.Calls.Clear();

        store.Bump("anything", CollectionKind.Pins);   // triggers the first converged walk → migration

        Assert.Equal(3, pins.Count);   // no removals — migration only ever ADDS to the server
        Assert.Equal(2, mutations.Calls.Count);
        Assert.Contains(("spotify:playlist:a", true), mutations.Calls);
        Assert.Contains(("spotify:playlist:b", true), mutations.Calls);
        Assert.True(settings.Get(SidebarKeys.PinsMigratedToServer));
    }

    [Fact]
    public void Migrated_AMissingSyncablePinIsRemoved_WhileAPendingOneIsKept()
    {
        var (store, pins, mutations, _, _) = Rig(converged: true, migrated: true,
            hasPending: uri => uri == "spotify:playlist:pending");
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:missing", SidebarEntryKind.Playlist, "spotify:playlist:missing")));
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:pending", SidebarEntryKind.Playlist, "spotify:playlist:pending")));
        Assert.True(pins.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));
        mutations.Calls.Clear();

        store.Bump("anything", CollectionKind.Pins);   // empty server mirror; both playlist pins are "missing"

        Assert.False(pins.IsPinned("pl:spotify:playlist:missing"));   // syncable + missing + not pending → removed
        Assert.True(pins.IsPinned("pl:spotify:playlist:pending"));    // shielded by the pending-op check → kept
        Assert.True(pins.IsPinned("folder:x"));                       // never syncable → always kept
        Assert.Empty(mutations.Calls);                                 // ApplyRemote never writes back
    }
}
