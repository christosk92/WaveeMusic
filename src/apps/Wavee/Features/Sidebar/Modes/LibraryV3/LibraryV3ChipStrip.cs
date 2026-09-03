using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>What one rendered position of the Library V3 filter rail IS — a pure function of (filter, qualifier,
/// whether the data evidences a qualifier), so the "the ✕ clears, the other kinds leave, a fused pill is the shared
/// primitive" grammar is pinned and unit-tested away from the renderer. Generalises <see cref="Wavee.HomeFacetStrip"/>
/// (<c>Features/Home/HomeFacetStrip.cs</c>) for a rail with exactly ONE selectable facet at a time — a leading Clear
/// slot stands in for that model's "All" tab, because clearing here is a distinct GESTURE (a ✕ that pops in), not
/// another tab in the row.</summary>
public enum V3ChipKind : byte { Clear, Facet, Fused, Option }

/// <summary>One rendered position of the rail. <see cref="SelectFilter"/>/<see cref="SelectQualifier"/> is exactly
/// what a tap — or its keyboard equivalent, Space/Enter on the roved chip — WRITES back to the persisted filter and
/// qualifier (<c>All</c>/<c>Any</c> for the slots that clear something), so the renderer needs exactly one write path
/// for every kind instead of one per shape.
///
/// <para><see cref="Key"/> is the node key the renderer must use. A <see cref="V3ChipKind.Facet"/> slot and the
/// <see cref="V3ChipKind.Fused"/> slot of the SAME code share it ("v3f{code}") — that shared identity is the whole
/// loose-pill ⇄ fused-pill morph; a distinct key per shape would unmount one and mount the other with nothing left
/// to reflow from.</para>
/// <para><see cref="Route"/> — issue #85 (H4, approach 3): the destination page this chip's KIND has, or null when
/// it has none. A plain tap always writes <see cref="SelectFilter"/>/<see cref="SelectQualifier"/> (that is the
/// chips' one job); a non-null route is only a SECONDARY affordance the renderer may offer (Library V3 chose a
/// double-click — see <c>LibraryV3Chips.Pill</c>). Playlists has no "all playlists" page, so its Facet/Fused slots
/// carry a null route like Clear/Option always do.</para></summary>
public readonly record struct V3ChipSlot(
    V3ChipKind Kind, int Code, bool Selected, string Key, int SelectFilter, int SelectQualifier, string? Route = null);

/// <summary>The Library V3 filter rail's layout, as a pure function of (persisted filter, persisted qualifier,
/// whether the data evidences ≥2 qualifier flavors). Engine-free — System plus the two persisted-code enums in
/// <c>SidebarDesign.cs</c> (<see cref="SidebarV3Filter"/>/<see cref="SidebarV3Qualifier"/>) — so the ordering and
/// selection rules the eye actually reads are unit-testable without a window, a renderer or a preferences object.
///
/// <para>THREE SHAPES, ONE MODEL. <b>Idle</b> (no filter): the four facets, unselected, nothing else — no ✕, because
/// there is nothing to clear. <b>Filtered</b> (a facet picked, no qualifier fused yet): a leading Clear slot, then
/// the selected facet — plus, ONLY under Playlists with the data evidencing ≥2 provenance flavors, the three
/// qualifier options spilled right after it. <b>Fused</b> (a qualifier is ALSO picked, which can only happen under
/// Playlists with qualifiers available): Clear, then one Fused slot sharing the facet's own key — the loose facet
/// pill and the fused pill are the SAME node across that transition, which is the entire point of the shared key.
/// </para></summary>
public static class LibraryV3ChipStrip
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Any = (int)SidebarV3Qualifier.Any;

    public static readonly int[] Facets =
    [
        (int)SidebarV3Filter.Playlists, (int)SidebarV3Filter.Podcasts,
        (int)SidebarV3Filter.Albums, (int)SidebarV3Filter.Artists,
    ];

    public static readonly int[] Qualifiers =
    [
        (int)SidebarV3Qualifier.ByYou, (int)SidebarV3Qualifier.BySpotify, (int)SidebarV3Qualifier.Mixed,
    ];

    /// <summary>idle: F F F F. filtered: X [F*] (+ its options — Playlists only, and only when the data evidences
    /// them). fused: X [F*│Q] — the fused pill and the loose facet share key "v3f{code}" (the morph).</summary>
    public static List<V3ChipSlot> Slots(int filter, int qualifier, bool qualifiersAvailable)
    {
        var slots = new List<V3ChipSlot>(8);
        if (filter == All)
        {
            foreach (var f in Facets) slots.Add(Facet(f, false));
            return slots;
        }

        slots.Add(new V3ChipSlot(V3ChipKind.Clear, 0, false, "v3-clear", All, Any));

        bool owns = filter == Playlists && qualifiersAvailable;
        if (owns && qualifier != Any)
        {
            // Fused: tapping the compound pill is ONE step back (drop the qualifier), not all the way to All.
            // Route is always null here in practice — only Playlists can fuse a qualifier, and Playlists has no
            // route (RouteFor) — but it is threaded through anyway so a future fuseable facet is not silently
            // dropped from the route contract.
            slots.Add(new V3ChipSlot(V3ChipKind.Fused, filter, true, "v3f" + filter, filter, Any, RouteFor(filter)));
        }
        else
        {
            slots.Add(Facet(filter, true));
            if (owns)
                foreach (var q in Qualifiers)
                    slots.Add(new V3ChipSlot(V3ChipKind.Option, q, false, "v3q" + q, filter, q));
        }
        return slots;
    }

    /// <summary>Roving focus survives relayout by CODE, not index: the index of the slot whose key matches
    /// <paramref name="focusedKey"/>, or 0 (the leading slot) when nothing matches — the chip that was focused left
    /// the rail entirely (its facet lost its options, say), so focus falls back to the leading position rather than
    /// vanishing.</summary>
    public static int FocusIndex(List<V3ChipSlot> slots, string? focusedKey)
    {
        if (focusedKey is not null)
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Key == focusedKey) return i;
        return 0;
    }

    /// <summary>A facet slot. Unselected (idle): tapping SELECTS it. Selected (filtered, not yet fused): tapping is
    /// the toggle — it CLEARS back to All, because the pill itself IS the "remove this filter" affordance while only
    /// one thing is active (no leading ✕ needed until there is something the ✕ could also clear a sub-filter off
    /// of).</summary>
    static V3ChipSlot Facet(int f, bool selected)
        => new(V3ChipKind.Facet, f, selected, "v3f" + f, selected ? All : f, Any, RouteFor(f));

    /// <summary>Issue #85 (H4) — the chip's "open this as a page" destination. Only Albums/Artists/Podcasts have an
    /// actual library page (<c>ShellRoutes</c>); Playlists has no "all playlists" destination, so its chip stays
    /// filter-only, exactly like Clear and every Option slot.</summary>
    public static string? RouteFor(int filter) => filter switch
    {
        (int)SidebarV3Filter.Albums => "albums",
        (int)SidebarV3Filter.Artists => "artists",
        (int)SidebarV3Filter.Podcasts => "podcasts",
        _ => null,
    };
}
