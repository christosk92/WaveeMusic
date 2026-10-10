// ── Wavee.Tests/SidebarDedupeMotionTests.cs — the key-matched motion of a section toggle and a pin move ─────────────────
//
// A Pinned collapse hands its pin back to Playlists, and a pin moves a row from Playlists to Pinned: rows are inserted and
// removed OUTSIDE the reveal band in the same publish. `SidebarDedupeMotion.ForPublish` turns that publish into the
// ordered splices (the extent table keeps every survivor's measured extent) and the per-row seeds (an inserted row fades
// in, a displaced row glides from where the user saw it, a band row is never seeded). The facts below run the REAL
// decision over planner-built Classic rows, so the old band range is proven to come from the OLD rows.

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarDedupeMotionTests
{
    const string Pinned = "pinned";
    const string Playlists = "playlists";

    static SidebarLayoutDoc Classic(bool pinnedCollapsed)
        => PlanFixture.Doc(SidebarLayoutId.Classic,
            pinnedCollapsed ? PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, Pinned, true)) : null);

    static SidebarProjectionInput Input(params string[] pinned)
    {
        var all = new[] { "pl:a", "pl:b", "pl:c" };
        var pins = pinned.Select(id => PlanFixture.Playlist(id)).ToArray();
        return new SidebarProjectionInput(Pins: pins, PlaylistTree: all.Select(id => PlanFixture.Playlist(id)).ToArray(),
                                          PinnedIds: new HashSet<string>(pinned, StringComparer.Ordinal));
    }

    static SidebarRowPlan Plan(SidebarLayoutDoc doc, SidebarProjectionInput input)
        => SidebarRowPlanner.Build(doc, in input, new SidebarPlanOptions(Mode: SidebarPaneMode.Expanded));

    static Func<int, float> Extents(SidebarRowPlan plan, SidebarLayoutDoc doc)
    {
        var rows = plan.Rows;
        return i => SidebarRowExtents.HeightOf(rows, i,
            doc.Sections.FirstOrDefault(s => string.Equals(s.Id, rows[i].SectionId, StringComparison.Ordinal)));
    }

    static int IndexOf(SidebarRowPlan plan, string section, string key)
    {
        for (int i = 0; i < plan.Rows.Count; i++)
            if (plan.Rows[i].SectionId == section && plan.Rows[i].Key == key) return i;
        return -1;
    }

    static Dictionary<int, SidebarDedupeMotion.Seed> Run(
        SidebarLayoutDoc oldDoc, SidebarRowPlan oldPlan, SidebarLayoutDoc newDoc, SidebarRowPlan newPlan,
        out List<(int At, int Removed, int Inserted)> splices, params string[] inFlight)
        => SidebarDedupeMotion.ForPublish(oldDoc, newDoc, oldPlan.Rows, newPlan.Rows, inFlight,
            Extents(oldPlan, oldDoc), Extents(newPlan, newDoc), out splices);

    static void AssertSplicesBalance(SidebarRowPlan oldPlan, SidebarRowPlan newPlan, List<(int At, int Removed, int Inserted)> splices)
    {
        int net = 0;
        foreach (var s in splices) net += s.Inserted - s.Removed;
        Assert.Equal(newPlan.Rows.Count - oldPlan.Rows.Count, net);
    }

    [Fact]
    public void Pinned_collapse_fades_the_returning_row_and_glides_only_the_rows_below_it()
    {
        var oldDoc = Classic(pinnedCollapsed: false);
        var newDoc = Classic(pinnedCollapsed: true);
        var input = Input("pl:a");
        var oldPlan = Plan(oldDoc, input);
        var newPlan = Plan(newDoc, input);

        var seeds = Run(oldDoc, oldPlan, newDoc, newPlan, out var splices);
        AssertSplicesBalance(oldPlan, newPlan, splices);

        // The pin returns to Playlists at the top of the tree: it fades in, in place.
        int a = IndexOf(newPlan, Playlists, "pl:a");
        Assert.True(a >= 0);
        Assert.True(seeds.TryGetValue(a, out var fade) && fade.Fade && fade.Dy == 0f);

        // The rows it displaces start one row higher (where the user saw them) and glide down.
        float rowH = SidebarRowExtents.HeightOf(newPlan.Rows, a, newDoc.Sections.First(s => s.Id == Playlists));
        Assert.True(rowH > 0f);
        foreach (var key in new[] { "pl:b", "pl:c" })
        {
            int i = IndexOf(newPlan, Playlists, key);
            Assert.True(seeds.TryGetValue(i, out var glide), key);
            Assert.Equal(-rowH, glide.Dy);
            Assert.False(glide.Fade);
        }

        // Nothing above the insertion moves: the dividers, Collections (header and rows), Home and the Pinned header.
        for (int i = 0; i < a; i++) Assert.False(seeds.ContainsKey(i), "row " + i + " " + newPlan.Rows[i].Key);
        // And a band row (the old Pinned body) is never seeded.
        Assert.DoesNotContain(seeds.Keys, i => newPlan.Rows[i].SectionId == Pinned);
    }

    [Fact]
    public void Pinned_expand_glides_the_playlists_survivors_back_down_and_seeds_no_band_row()
    {
        var oldDoc = Classic(pinnedCollapsed: true);
        var newDoc = Classic(pinnedCollapsed: false);
        var input = Input("pl:a");
        var oldPlan = Plan(oldDoc, input);
        var newPlan = Plan(newDoc, input);

        var seeds = Run(oldDoc, oldPlan, newDoc, newPlan, out var splices);
        AssertSplicesBalance(oldPlan, newPlan, splices);

        // The Pinned body is the band: whatever it inserted, the band presents it.
        Assert.True(SidebarRowGeometry.TrySectionBodyRange(newPlan.Rows, Pinned, out int first, out int count));
        for (int i = first; i < first + count; i++) Assert.False(seeds.ContainsKey(i), "band row " + i);

        // pl:a left Playlists: the survivors below it start one row lower than they now lie.
        int b = IndexOf(newPlan, Playlists, "pl:b");
        float rowH = SidebarRowExtents.HeightOf(oldPlan.Rows, IndexOf(oldPlan, Playlists, "pl:a"),
                                                oldDoc.Sections.First(s => s.Id == Playlists));
        Assert.True(seeds.TryGetValue(b, out var glide));
        Assert.Equal(rowH, glide.Dy);
        Assert.Equal(rowH, seeds[IndexOf(newPlan, Playlists, "pl:c")].Dy);

        // Collections, above the edits, stays put.
        Assert.DoesNotContain(seeds.Keys, i => newPlan.Rows[i].SectionId == "collections");
    }

    [Fact]
    public void A_pin_move_fades_the_pinned_row_and_glides_the_rows_between()
    {
        var doc = Classic(pinnedCollapsed: false);
        var oldPlan = Plan(doc, Input("pl:a"));
        var newPlan = Plan(doc, Input("pl:a", "pl:b"));

        Assert.True(SidebarDedupeMotion.PinnedKeysChanged(oldPlan.Rows, newPlan.Rows));

        var seeds = Run(doc, oldPlan, doc, newPlan, out var splices);
        AssertSplicesBalance(oldPlan, newPlan, splices);

        int pinnedB = IndexOf(newPlan, Pinned, "pl:b");
        Assert.True(pinnedB >= 0);
        Assert.True(seeds.TryGetValue(pinnedB, out var fade) && fade.Fade);

        // Everything between the inserted pinned row and the removed Playlists row starts one pinned row higher.
        float inserted = SidebarRowExtents.HeightOf(newPlan.Rows, pinnedB, doc.Sections.First(s => s.Id == Pinned));
        int c = IndexOf(newPlan, Playlists, "pl:c");
        int removedAt = IndexOf(oldPlan, Playlists, "pl:b");
        float removed = SidebarRowExtents.HeightOf(oldPlan.Rows, removedAt, doc.Sections.First(s => s.Id == Playlists));
        int collections = newPlan.Rows.ToList().FindIndex(r => r.SectionId == "collections" && r.Kind == SidebarRowKind.SectionHeader);
        Assert.True(collections > pinnedB);
        Assert.Equal(-inserted, seeds[collections].Dy);
        int playlistsHeader = newPlan.Rows.ToList().FindIndex(r => r.SectionId == Playlists && r.Kind == SidebarRowKind.SectionHeader);
        Assert.Equal(-inserted, seeds[playlistsHeader].Dy);

        // Past the removed row the two cancel: a Classic pinned row and a Playlists row have the same extent, so the net is 0
        // and the row after the removal gets no seed. The row above every edit never moves.
        Assert.Equal(inserted, removed);
        Assert.False(seeds.ContainsKey(c));
        Assert.False(seeds.ContainsKey(0));
    }

    [Fact]
    public void A_section_disclosure_in_flight_keeps_its_rows_out_of_the_seeds()
    {
        // Playlists is mid-disclosure while Pinned collapses: its rows (the survivors and the returning pin) belong to
        // Playlists' own band, so nothing in that body is seeded.
        var oldDoc = Classic(pinnedCollapsed: false);
        var newDoc = Classic(pinnedCollapsed: true);
        var input = Input("pl:a");
        var oldPlan = Plan(oldDoc, input);
        var newPlan = Plan(newDoc, input);

        var seeds = Run(oldDoc, oldPlan, newDoc, newPlan, out _, Playlists);
        Assert.True(SidebarRowGeometry.TrySectionBodyRange(newPlan.Rows, Playlists, out int first, out int count));
        for (int i = first; i < first + count; i++) Assert.False(seeds.ContainsKey(i), "band row " + i);
    }

    [Fact]
    public void A_publish_without_edits_has_no_splices_and_no_seeds()
    {
        var doc = Classic(pinnedCollapsed: false);
        var plan = Plan(doc, Input("pl:a"));
        var seeds = Run(doc, plan, doc, plan, out var splices);
        Assert.Empty(splices);
        Assert.Empty(seeds);
        Assert.False(SidebarDedupeMotion.PinnedKeysChanged(plan.Rows, plan.Rows));
    }

    [Fact]
    public void A_pure_pin_reorder_is_not_a_pin_move()
    {
        var doc = Classic(pinnedCollapsed: false);
        var before = Plan(doc, Input("pl:a", "pl:b"));
        var after = Plan(doc, Input("pl:b", "pl:a"));
        Assert.False(SidebarDedupeMotion.PinnedKeysChanged(before.Rows, after.Rows));
    }

    [Fact]
    public void A_non_finite_inserted_extent_answers_no_motion_at_all()
    {
        var oldDoc = Classic(pinnedCollapsed: false);
        var newDoc = Classic(pinnedCollapsed: true);
        var input = Input("pl:a");
        var oldPlan = Plan(oldDoc, input);
        var newPlan = Plan(newDoc, input);
        var seeds = SidebarDedupeMotion.ForPublish(oldDoc, newDoc, oldPlan.Rows, newPlan.Rows, Array.Empty<string>(),
            Extents(oldPlan, oldDoc), _ => float.NaN, out _);
        Assert.Empty(seeds);
    }

    [Fact]
    public void Only_choreographed_flips_take_the_keyed_path()
    {
        var oldDoc = Classic(pinnedCollapsed: false);
        var newDoc = Classic(pinnedCollapsed: true);
        Assert.False(SidebarDedupeMotion.FlippedSectionsAll(oldDoc, newDoc, new HashSet<string>()));
        Assert.True(SidebarDedupeMotion.FlippedSectionsAll(oldDoc, newDoc, new HashSet<string> { Pinned }));
        Assert.False(SidebarDedupeMotion.FlippedSectionsAll(oldDoc, newDoc, new HashSet<string> { "collections" }));
        Assert.False(SidebarDedupeMotion.FlippedSectionsAll(oldDoc, oldDoc, new HashSet<string> { Pinned }));   // nothing flipped
    }
}
