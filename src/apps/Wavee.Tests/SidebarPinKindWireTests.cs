using System;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The exhaustiveness guard the pre-unification design lacked (2026-08-19): there used to be TWO enums for "what a pin
/// points at" — <c>SidebarPinKind</c> (persisted) and <see cref="SidebarEntryKind"/> (the projection's) — bridged by two
/// hand-written, catch-all-terminated mappings (<c>SidebarBinderPipeline.KindOfPin</c> /
/// <c>SidebarPinId.KindOfEntry</c>). Both enums and both mappings are now DELETED: <see cref="SidebarPin.Kind"/> IS a
/// <see cref="SidebarEntryKind"/>, and the wire freezes the OLD numbering in <c>SidebarLayoutWire</c>'s legacy table
/// instead of duplicating the domain type (<c>Features/Sidebar/Persistence/SidebarLayoutDoc.cs</c>).
///
/// This class asserts BEHAVIOUR, not source text: it drives <c>SidebarLayoutWire.PinKindName</c> /
/// <c>TryParsePinKind</c> / <c>TryLegacyPinKind</c> and <c>SidebarPinId.IsPinnable</c> directly over
/// <c>Enum.GetValues&lt;SidebarEntryKind&gt;()</c>, so a future member added to <see cref="SidebarEntryKind"/> without
/// a matching wire arm fails HERE — as a broken round trip — rather than silently degrading to a route pin the way the
/// old catch-all defaults did.
/// </summary>
public class SidebarPinKindWireTests
{
    static readonly SidebarEntryKind[] AllKinds = Enum.GetValues<SidebarEntryKind>();

    [Fact]
    public void EveryEntryKind_HasANonEmptyWireString()
    {
        foreach (var kind in AllKinds)
        {
            string name = SidebarLayoutWire.PinKindName(kind);
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact]
    public void EveryEntryKind_RoundTripsThroughItsWireStringExactly()
    {
        // PinKindName(kind) -> TryParsePinKind(...) must come back to the SAME kind for every member. A member left off
        // PinKindName's switch falls through to its "appRoute" default, which round-trips to SidebarEntryKind.AppRoute
        // instead of the member itself — exactly the failure this test exists to catch.
        foreach (var kind in AllKinds)
        {
            string name = SidebarLayoutWire.PinKindName(kind);
            Assert.True(SidebarLayoutWire.TryParsePinKind(name, out var parsed),
                $"'{name}' (written for {kind}) does not parse back at all.");
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void EveryWireString_RoundTripsThroughTheEnumExactly()
    {
        // The inverse direction: every string PinKindName can produce, fed back through TryParsePinKind and out through
        // PinKindName again, must reproduce the SAME string — the other half of "the vocabulary has exactly one
        // spelling per kind".
        foreach (var kind in AllKinds)
        {
            string name = SidebarLayoutWire.PinKindName(kind);
            Assert.True(SidebarLayoutWire.TryParsePinKind(name, out var parsed));
            Assert.Equal(name, SidebarLayoutWire.PinKindName(parsed));
        }
    }

    [Fact]
    public void UnrecognizedWireString_FailsExplicitly_NeverGuessesAKind()
    {
        Assert.False(SidebarLayoutWire.TryParsePinKind("madeUpKind", out _));
        Assert.False(SidebarLayoutWire.TryParsePinKind("", out _));
        Assert.False(SidebarLayoutWire.TryParsePinKind(null, out _));
    }

    // ── the frozen legacy table (the pre-unification SidebarPinKind byte numbering) ────────────────────────────────────

    [Theory]
    [InlineData(0, SidebarEntryKind.AppRoute)]  // the old SidebarPinKind.Route
    [InlineData(1, SidebarEntryKind.Playlist)]
    [InlineData(2, SidebarEntryKind.Album)]
    [InlineData(3, SidebarEntryKind.Artist)]
    [InlineData(4, SidebarEntryKind.Show)]
    [InlineData(5, SidebarEntryKind.Folder)]
    public void LegacyInt_MapsToTheKindItHistoricallyMeant(int legacy, SidebarEntryKind expected)
    {
        Assert.True(SidebarLayoutWire.TryLegacyPinKind(legacy, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]     // one past the old enum's last member (Folder = 5) — SidebarEntryKind.Track has NO legacy slot
    [InlineData(255)]
    public void LegacyInt_OutsideTheFrozenRange_FailsExplicitly(int legacy)
        => Assert.False(SidebarLayoutWire.TryLegacyPinKind(legacy, out _));

    [Fact]
    public void LegacyIntWrite_AndRead_RoundTrip_ForEveryPinnableKind()
    {
        // LegacyPinKindInt is the inverse of TryLegacyPinKind, written for a downgrade. Every kind that COULD have
        // existed under the old scheme (i.e. every kind IsPinnable admits) must round-trip through it exactly.
        foreach (var kind in AllKinds)
        {
            if (!SidebarPinId.IsPinnable(kind)) continue;   // Track: never written as a real pin either way
            int legacy = SidebarLayoutWire.LegacyPinKindInt(kind);
            Assert.True(SidebarLayoutWire.TryLegacyPinKind(legacy, out var back));
            Assert.Equal(kind, back);
        }
    }

    // ── pinnability is a predicate, not an enum boundary (locked decision 4) ───────────────────────────────────────────

    [Fact]
    public void Track_IsNeverPinnable()
    {
        Assert.False(SidebarPinId.IsPinnable(SidebarEntryKind.Track));

        // The pin-creation path itself: SidebarPinId.FromEntry is the ONE screen every RowForEntry/menu/drag call site
        // funnels through (Actions/PinActions.cs, Features/DragDrop/WaveeResourceDrag.cs — not source-included here,
        // since they are engine-bound — both route pin creation through SidebarPinId, which this exercises directly).
        var track = new SidebarLibraryEntry("queue:1", SidebarEntryKind.Track, "spotify:track:x", "A Song", "",
            null, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0,
            Circular: false, Flavor: SidebarPlaylistFlavor.None);
        Assert.Null(SidebarPinId.FromEntry(in track));
    }

    [Theory]
    [InlineData(SidebarEntryKind.AppRoute)]
    [InlineData(SidebarEntryKind.Playlist)]
    [InlineData(SidebarEntryKind.Album)]
    [InlineData(SidebarEntryKind.Artist)]
    [InlineData(SidebarEntryKind.Show)]
    [InlineData(SidebarEntryKind.Folder)]
    public void EveryOtherKind_IsPinnable(SidebarEntryKind kind)
        => Assert.True(SidebarPinId.IsPinnable(kind));
}
