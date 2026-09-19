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
        Rig(bool converged, bool migrated, System.Func<string, bool>? hasPending = null,
            System.Func<string, string>? routeTitle = null)
    {
        var store = new InMemoryStore();
        var pins = new SidebarPinStore();
        var mutations = new RecordingPinMutations();
        var settings = new MemoryAppSettings();
        settings.Set(SidebarKeys.PinsMigratedToServer, migrated);
        var sync = new SidebarPinSync(store, pins, mutations, settings,
            () => User, () => converged, hasPending ?? (_ => false),
            routeTitle ?? (id => id == "liked" ? "Liked Songs" : ""));
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
    public void LocalPin_OfAFolder_IsPushedOnce()
    {
        var (_, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        Assert.True(pins.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));

        Assert.Equal(new[] { ("spotify:folder:x", true) }, mutations.Calls);
    }

    [Fact]
    public void RemoteAdd_OfAFolder_PinsLocally_AsAFolderKind()
    {
        var (store, pins, mutations, _, _) = Rig(converged: false, migrated: false);
        store.SetSaved("pins", "spotify:folder:abc", true, SyncState.Confirmed, 1000);

        Assert.True(pins.IsPinned("folder:abc"));
        var pin = pins[pins.IndexOf("folder:abc")];
        Assert.Equal(SidebarEntryKind.Folder, pin.Kind);
        Assert.Equal("", pin.Uri);
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
        Assert.Equal(3, mutations.Calls.Count);   // folders are syncable too, so the folder pin migrates as well
        Assert.Contains(("spotify:playlist:a", true), mutations.Calls);
        Assert.Contains(("spotify:playlist:b", true), mutations.Calls);
        Assert.Contains(("spotify:folder:x", true), mutations.Calls);
        Assert.True(settings.Get(SidebarKeys.PinsMigratedToServer));
    }

    [Fact]
    public void Migrated_AMissingSyncablePinIsRemoved_WhileAPendingOneIsKept()
    {
        var (store, pins, mutations, _, _) = Rig(converged: true, migrated: true,
            hasPending: uri => uri == "spotify:playlist:pending");
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:missing", SidebarEntryKind.Playlist, "spotify:playlist:missing")));
        Assert.True(pins.Pin(Pin("pl:spotify:playlist:pending", SidebarEntryKind.Playlist, "spotify:playlist:pending")));
        Assert.True(pins.Pin(Pin("home", SidebarEntryKind.AppRoute, "", "Home")));
        mutations.Calls.Clear();

        store.Bump("anything", CollectionKind.Pins);   // empty server mirror; both playlist pins are "missing"

        Assert.False(pins.IsPinned("pl:spotify:playlist:missing"));   // syncable + missing + not pending → removed
        Assert.True(pins.IsPinned("pl:spotify:playlist:pending"));    // shielded by the pending-op check → kept
        Assert.True(pins.IsPinned("home"));                           // never syncable (app route) → always kept
        Assert.Empty(mutations.Calls);                                 // ApplyRemote never writes back
    }

    [Fact]
    public void RemoteAdd_OfTheLikedRoute_CarriesTheRouteTitle()
    {
        var (store, pins, _, _, _) = Rig(converged: false, migrated: false);
        store.SetSaved("pins", "spotify:collection", true, SyncState.Confirmed, 1000);

        Assert.True(pins.IsPinned("liked"));
        var pin = pins[pins.IndexOf("liked")];
        Assert.Equal(SidebarEntryKind.AppRoute, pin.Kind);
        Assert.Equal("Liked Songs", pin.Name);
    }

    [Fact]
    public void RemoteAdd_OfAnEntity_LeavesNameEmpty_ForTouchToFill()
    {
        var (store, pins, _, _, _) = Rig(converged: false, migrated: false);
        store.SetSaved("pins", "spotify:playlist:new", true, SyncState.Confirmed, 1000);

        Assert.True(pins.IsPinned("pl:spotify:playlist:new"));
        var pin = pins[pins.IndexOf("pl:spotify:playlist:new")];
        Assert.Equal("", pin.Name);
    }

    // §3 — the preservation invariant: a shape this client cannot represent as a local pin must never become one, and
    // must never trigger a write, so a foreign pin (added by another Spotify client) survives on the server untouched
    // across both an initial convergence AND the one-time local-pin migration.
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
        var (store, pins, mutations, _, _) = Rig(converged: true, migrated: false);
        // InMemoryStore bypasses CollectionSets.AcceptsUri, so seeding the raw "pins" set directly also proves
        // PinSyncRules.TryPinId is a second, independent fence — not merely relying on the write-side filter.
        store.SetSaved("pins", uri, true, SyncState.Confirmed, 1000);

        Assert.Equal(0, pins.Count);
        Assert.Empty(mutations.Calls);

        store.Bump("anything", CollectionKind.Pins);   // a second bump, now past the first-converged migration walk

        Assert.Equal(0, pins.Count);
        Assert.Empty(mutations.Calls);
    }
}
