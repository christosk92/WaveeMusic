using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The four derived facts a projection publish carries besides its rows. A record STRUCT so the compare is a
/// value compare with no allocation; <see cref="Error"/> compares by REFERENCE (Exception does not override Equals),
/// which is the honest test — a re-thrown-but-equal exception is a new failure.</summary>
/// <param name="State">The publish's <c>LoadState</c>, as an int so this file stays engine-free.</param>
public readonly record struct SidebarEntriesMeta(
    int State,
    Exception? Error,
    bool AnyContributingKindPending,
    bool QualifiersAvailable,
    int PinCount);

/// <summary>
/// The publish gate behind <c>SidebarEntries.Publish</c>: a SHADOW SNAPSHOT of the last published projection, compared
/// exactly, so a rebuild that produced byte-identical content does not bump the version.
///
/// <para><b>Why a shadow copy and not a fingerprint.</b> The binder pump re-projects on every QueueRevision /
/// CurrentTrack / PlayLog / History move, and the overwhelming majority of those rebuilds land on the SAME rows — yet
/// each one bumped the version, re-planned every sidebar pane and (before the per-row epoch diff could help) re-rendered
/// the whole realized window: 2 full storms per track boundary, 3 per navigation. A hash would collapse that too, but a
/// collision would FREEZE the sidebar on stale content with no way back, so the compare is exact: meta + count +
/// elementwise. Memory cost is one extra entry list (~10k structs worst case) — deliberately paid for exactness.</para>
///
/// <para><b>Allocation.</b> The shadow list is reused (<c>Clear</c> + re-add keeps capacity), and the compare itself
/// allocates nothing. This runs at PUBLISH time (a projection rebuild), never per frame.</para>
///
/// <para>Engine-free by construction (System + the Data\ entry record), so <c>Wavee.Tests</c> drives the REAL gate.</para>
/// </summary>
public sealed class SidebarEntriesShadow
{
    readonly List<SidebarLibraryEntry> _published = new();
    SidebarEntriesMeta _meta;
    bool _seeded;

    /// <summary>The last published rows, as captured. Diagnostic/test surface — consumers read the live cell.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Published => _published;

    /// <summary>Record a completed rebuild. Returns <c>true</c> when it DIFFERS from the last one (the caller must bump
    /// its version), <c>false</c> when it is identical (the caller must not). The very first publish always counts as a
    /// change — consumers have never seen a projection at that point.</summary>
    public bool Publish(IReadOnlyList<SidebarLibraryEntry> entries, in SidebarEntriesMeta meta)
    {
        bool sameRows = SameRows(entries);
        if (_seeded && sameRows && _meta.Equals(meta)) return false;
        _seeded = true;
        _meta = meta;
        if (!sameRows) Capture(entries);
        return true;
    }

    void Capture(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        _published.Clear();
        if (entries is null) return;
        if (_published.Capacity < entries.Count) _published.Capacity = entries.Count;
        for (int i = 0; i < entries.Count; i++) _published.Add(entries[i]);
    }

    bool SameRows(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        if (entries is null) return _published.Count == 0;
        if (entries.Count != _published.Count) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            var a = _published[i];
            var b = entries[i];
            if (!SameEntry(in a, in b)) return false;
        }
        return true;
    }

    /// <summary>Exact entry equality. <see cref="SidebarLibraryEntry"/> is a readonly record struct, so its generated
    /// <c>Equals</c> is already a member-wise value compare — EXCEPT <c>MosaicTiles</c>, an <c>IReadOnlyList&lt;string&gt;</c>
    /// compared by reference. The projection materializes a folder's tile list FRESH on every rebuild
    /// (<c>SidebarProjection.FolderTiles</c>), so a reference compare would report every folder row as changed and the
    /// gate would never fire for a library that has folders. The sequence compare below is strictly MORE precise than
    /// the reference compare it replaces — never less — so it cannot mask a real change.</summary>
    public static bool SameEntry(in SidebarLibraryEntry a, in SidebarLibraryEntry b)
    {
        if (ReferenceEquals(a.MosaicTiles, b.MosaicTiles)) return a.Equals(b);
        if (!SameTiles(a.MosaicTiles, b.MosaicTiles)) return false;
        // Tiles agree by value; normalize that one member so the generated record compare decides the rest.
        return (a with { MosaicTiles = b.MosaicTiles }).Equals(b);
    }

    static bool SameTiles(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null) return false;   // the ReferenceEquals caller already handled null == null
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
