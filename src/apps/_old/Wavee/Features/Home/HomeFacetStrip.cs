using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>What one rendered position of the home facet strip IS. <see cref="FacetSlotKind.All"/> is the app's own
/// "clear the facet" position; <see cref="FacetSlotKind.Tab"/> is a server chip at rest; <see cref="FacetSlotKind.Fused"/>
/// is a selected parent showing its selected sub-chip docked inside it; <see cref="FacetSlotKind.Sub"/> is a second-level
/// option spilled inline under its selected parent.</summary>
internal enum FacetSlotKind : byte { All, Tab, Fused, Sub }

/// <summary>One rendered position of the strip. <see cref="Select"/> is what a tap writes to
/// <c>Services.HomeFacet</c> (null = unfiltered); <see cref="Key"/> is the node key the renderer must use — the
/// <see cref="FacetSlotKind.Tab"/> and <see cref="FacetSlotKind.Fused"/> slots of the SAME chip share it, and that
/// shared key is the morph.</summary>
internal readonly record struct FacetSlot(
    FacetSlotKind Kind, HomeChip? Chip, HomeChip? Sub, bool Selected, string Key, string? Select);

/// <summary>The home facet strip's layout, as a pure function of (server chips, current selection). Engine-free on
/// purpose — System + <see cref="HomeChip"/> only — so the ordering rules that the eye actually reads (where does
/// "Following" appear, what does tapping the fused pill do) are unit-testable without a page, a service or a window.
/// The component owns pixels; this owns the sequence.</summary>
internal static class HomeFacetStrip
{
    /// <summary>Which top-level chip owns the current selection: either it IS the selection, or one of its sub-chips
    /// is. A sub-selection keeps its PARENT active — the strip states which facet you are in, not which option.</summary>
    public static (HomeChip? Parent, HomeChip? Sub) Resolve(IReadOnlyList<HomeChip> chips, string? selected)
    {
        HomeChip? parent = null, sub = null;
        for (int i = 0; i < chips.Count && parent is null; i++)
        {
            var chip = chips[i];
            if (string.Equals(chip.Id, selected, StringComparison.Ordinal)) parent = chip;
            else
                foreach (var candidate in chip.SubChips)
                    if (string.Equals(candidate.Id, selected, StringComparison.Ordinal))
                    { parent = chip; sub = candidate; break; }
        }
        return (parent, sub);
    }

    /// <summary>All, then every server chip in feed order; a selected parent that owns sub-chips spills them RIGHT
    /// AFTER itself (never after the last tab, which is where the divider-and-append shape used to put "Following");
    /// a selected sub folds its parent into one Fused slot.
    /// <para>A Fused slot's <see cref="FacetSlot.Select"/> is the PARENT id — tapping the fused pill is one step back,
    /// not all the way to unfiltered, which is what All is for. A Tab's Select is its own id: re-tapping the selected
    /// tab is a no-op, because a tab strip has exactly one selection and there is nothing to clear except via All.</para></summary>
    public static List<FacetSlot> Slots(IReadOnlyList<HomeChip> chips, string? selected)
    {
        var (parent, sub) = Resolve(chips, selected);
        var slots = new List<FacetSlot>(chips.Count + 3)
        {
            new(FacetSlotKind.All, null, null, parent is null, "facet-all", null),
        };
        foreach (var chip in chips)
        {
            bool on = ReferenceEquals(chip, parent);
            string key = "facet-pill:" + chip.Id;
            if (on && sub is not null)
            {
                slots.Add(new(FacetSlotKind.Fused, chip, sub, true, key, chip.Id));
            }
            else
            {
                slots.Add(new(FacetSlotKind.Tab, chip, null, on, key, chip.Id));
                if (on)
                    foreach (var child in chip.SubChips)
                        slots.Add(new(FacetSlotKind.Sub, chip, child, false, "facet-sub:" + child.Id, child.Id));
            }
        }
        return slots;
    }
}
