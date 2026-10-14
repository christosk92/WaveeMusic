// ── Themes/Classic/Structure/Views/Liked/ClassicLikedChipBar.cs — Classic's Liked chip rail (themes plan 6.1, T9c) ──────────────────────
//
// From `Entities\User.Page.Liked.cs` (`LikedChipBar.Render`, the chip rail of ch 07 §0.13, W18; ch 04 items 32-33): ONE line that scrolls
// (the rail's own edge fade), "All" first, EXCLUSIVE selection (All + at most one), re-tapping the active chip clears it, and an
// un-evidenced curated chip rendered DISABLED as one contiguous tail (the evidence is a prefix, so the order IS the state). 0 DIP when there
// is no set (W19).
//
// The core host keeps the table's live filter (`Track.TableLiveSlot`: it is read under the table's provider), the selected tag and the
// two verbs; the view draws what it is handed.

using FluentGpu.Dsl;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Liked chip rail.</summary>
internal static class ClassicLikedChipBar
{
    /// <summary>The registered template of <see cref="ViewKeys.LikedChipBar"/>.</summary>
    public static Element Template(LikedChipBarProps p, in ViewContext ctx)
    {
        if (p.Titles.Length == 0) return new BoxEl { Key = "chips:off", HitTestVisible = false };

        var selectAll = p.SelectAll;
        var select = p.Select;
        string? selected = p.Selected;
        var kids = new Element[p.Titles.Length + 1];
        // AllChip, not AllTracks: a chip beside "Pop" and "K-Pop", where the short word reads as one of the set.
        kids[0] = Controls.Chip(Loc.Get(Strings.Detail.Filter.AllChip), selected is null, true, selectAll) with { Key = "chip:all" };
        for (int i = 0; i < p.Titles.Length; i++)
        {
            string tag = p.Titles[i];
            bool on = string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase);
            bool available = i < p.Evidenced;
            kids[i + 1] = Controls.Chip(tag, on, available, available ? () => select(on ? null : tag) : null)
                with { Key = "chip:" + tag };
        }
        return Controls.ChipRail(kids, p.ScrollKey) with { Key = "chips" };
    }
}
