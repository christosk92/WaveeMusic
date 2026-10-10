// ── Wavee.Tests/LikedGroupsTests.cs — Liked Songs' date groups ─────────────────────────────────────────────────────
//
// The gate for `Track.LikedGroups` (Entities/Track.Rules.cs) and for the group strip the table slot draws with it.
//
//   THE RULES are pure: which group an Added stamp falls in (LOCAL calendar weeks, starting on the culture's first day),
//   where a group starts in the displayed order, the strip's label, and the ONE height formula a row's slot has
//   (`Extent`). The calendar facts use local wall-clock noons, so they hold in any zone; the culture is pinned invariant.
//
//   THE SLOT is mounted for real: the fake catalog's liked songs in the embedded table (Config.Liked, Date added
//   descending by default), at every row density. The list's extents are ANALYTIC (a MeasuredStackVirtualLayout seeded
//   with `LikedGroups.Extent` and re-seeded for each plan), so the assertion is that the slot the list realizes is exactly
//   the height it seeded: no measure-correct jump. A group-starting
//   row's focus ring (drawn on the slot root, which ItemsView focuses) is inset by the strip, so it wraps the row only.

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Wavee;
using Xunit;
using Groups = Wavee.Track.LikedGroups;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LikedGroupsTests
{
    sealed class InvariantCultureScope : IDisposable
    {
        readonly CultureInfo _saved = CultureInfo.CurrentCulture;
        public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        public void Dispose() => CultureInfo.CurrentCulture = _saved;
    }

    static long LocalNoon(int year, int month, int day)
        => new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

    // 2026-09-13 is a SUNDAY: a Monday-first week began on Sep 7, a Sunday-first one began today.
    static readonly long Now = LocalNoon(2026, 9, 13);

    // ── which group ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyOf_with_Monday_first_puts_Saturday_and_Monday_in_this_week_and_the_Sunday_before_in_last_week()
    {
        var monday = DayOfWeek.Monday;

        Assert.Equal(new Groups.Key(Groups.ThisWeek, 0, 0), Groups.KeyOf(Now, Now, monday));
        Assert.Equal(new Groups.Key(Groups.ThisWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 12), Now, monday));
        Assert.Equal(new Groups.Key(Groups.ThisWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 7), Now, monday));
        Assert.Equal(new Groups.Key(Groups.LastWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 6), Now, monday));
        Assert.Equal(new Groups.Key(Groups.LastWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 8, 31), Now, monday));
        Assert.Equal(new Groups.Key(Groups.MonthKind, 2026, 8), Groups.KeyOf(LocalNoon(2026, 8, 30), Now, monday));
    }

    [Fact]
    public void KeyOf_with_Sunday_first_moves_the_boundary_a_day_and_a_week_with_it()
    {
        var sunday = DayOfWeek.Sunday;

        Assert.Equal(new Groups.Key(Groups.ThisWeek, 0, 0), Groups.KeyOf(Now, Now, sunday));
        Assert.Equal(new Groups.Key(Groups.LastWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 12), Now, sunday));
        Assert.Equal(new Groups.Key(Groups.LastWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 6), Now, sunday));
        Assert.Equal(new Groups.Key(Groups.MonthKind, 2026, 9), Groups.KeyOf(LocalNoon(2026, 9, 5), Now, sunday));
        Assert.Equal(new Groups.Key(Groups.MonthKind, 2026, 8), Groups.KeyOf(LocalNoon(2026, 8, 30), Now, sunday));
    }

    [Fact]
    public void KeyOf_names_a_month_by_year_and_month_and_tolerates_a_future_or_missing_stamp()
    {
        var monday = DayOfWeek.Monday;

        Assert.Equal(new Groups.Key(Groups.MonthKind, 2025, 12), Groups.KeyOf(LocalNoon(2025, 12, 30), Now, monday));
        Assert.Equal(new Groups.Key(Groups.MonthKind, 2026, 1), Groups.KeyOf(LocalNoon(2026, 1, 2), Now, monday));
        Assert.Equal(new Groups.Key(Groups.ThisWeek, 0, 0), Groups.KeyOf(LocalNoon(2026, 9, 20), Now, monday));   // clock skew
        Assert.Equal(new Groups.Key(Groups.MonthKind, 0, 0), Groups.KeyOf(0, Now, monday));                        // no stamp
    }

    // ── the label ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Label_adds_the_year_only_when_it_is_not_the_current_one()
    {
        using var _ = new InvariantCultureScope();

        Assert.Equal(Loc.Get(Strings.Detail.ThisWeek), Groups.Label(new Groups.Key(Groups.ThisWeek, 0, 0), 2026));
        Assert.Equal(Loc.Get(Strings.Detail.LastWeek), Groups.Label(new Groups.Key(Groups.LastWeek, 0, 0), 2026));
        Assert.Equal("September", Groups.Label(new Groups.Key(Groups.MonthKind, 2026, 9), 2026));
        Assert.Equal("September 2025", Groups.Label(new Groups.Key(Groups.MonthKind, 2025, 9), 2026));
        Assert.Equal(Track.Format.Dash, Groups.Label(new Groups.Key(Groups.MonthKind, 0, 0), 2026));
    }

    // ── where a group starts ─────────────────────────────────────────────────────────────────────────────────────────

    static Groups.Key W(byte kind, int year = 0, int month = 0) => new(kind, year, month);

    [Fact]
    public void StartsGroup_marks_the_first_row_of_each_run_in_descending_order()
    {
        // newest first: this week ×2, last week ×1, September ×3, August ×1
        Groups.Key[] keys =
        [
            W(Groups.ThisWeek), W(Groups.ThisWeek), W(Groups.LastWeek),
            W(Groups.MonthKind, 2026, 9), W(Groups.MonthKind, 2026, 9), W(Groups.MonthKind, 2026, 9), W(Groups.MonthKind, 2026, 8),
        ];

        bool[] starts = Enumerable.Range(0, keys.Length).Select(i => Groups.StartsGroup(keys, i)).ToArray();

        Assert.Equal([true, false, true, true, false, false, true], starts);
    }

    [Fact]
    public void StartsGroup_marks_the_first_row_of_each_run_in_ascending_order()
    {
        // oldest first: the same rule, the runs just read the other way
        Groups.Key[] keys =
        [
            W(Groups.MonthKind, 2026, 8), W(Groups.MonthKind, 2026, 9), W(Groups.MonthKind, 2026, 9),
            W(Groups.LastWeek), W(Groups.ThisWeek), W(Groups.ThisWeek),
        ];

        bool[] starts = Enumerable.Range(0, keys.Length).Select(i => Groups.StartsGroup(keys, i)).ToArray();

        Assert.Equal([true, true, false, true, true, false], starts);
    }

    [Fact]
    public void Counts_names_the_size_of_each_group_on_the_row_that_starts_it()
    {
        Groups.Key[] keys = [W(Groups.ThisWeek), W(Groups.ThisWeek), W(Groups.LastWeek), W(Groups.MonthKind, 2026, 9), W(Groups.MonthKind, 2026, 9)];
        var counts = new int[keys.Length];

        Groups.Counts(keys, counts);

        Assert.Equal([2, 0, 1, 2, 0], counts);
    }

    // ── the one height formula ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_strip_is_forty_dip_and_a_slot_is_the_row_plus_it_when_it_starts_a_group()
    {
        Assert.Equal(40f, Groups.HeaderH);
        foreach (float rowH in new[] { 40f, 48f, 56f, 64f })
        {
            Assert.Equal(rowH + 40f, Groups.Extent(rowH, startsGroup: true));
            Assert.Equal(rowH, Groups.Extent(rowH, startsGroup: false));
        }
    }

    [Fact]
    public void Groups_apply_only_to_a_hover_heart_page_in_date_added_order_either_way()
    {
        Assert.True(Groups.Applies(true, new Track.SortSpec(Track.SortColumn.DateAdded, true)));
        Assert.True(Groups.Applies(true, new Track.SortSpec(Track.SortColumn.DateAdded, false)));
        Assert.False(Groups.Applies(true, new Track.SortSpec(Track.SortColumn.Title, false)));
        Assert.False(Groups.Applies(true, Track.SortSpec.Default));
        Assert.False(Groups.Applies(false, new Track.SortSpec(Track.SortColumn.DateAdded, true)));
    }

    [Fact]
    public void DayOf_is_one_number_per_local_day()
    {
        Assert.Equal(Groups.DayOf(LocalNoon(2026, 9, 13)), Groups.DayOf(LocalNoon(2026, 9, 13) + 3_600));
        Assert.Equal(Groups.DayOf(LocalNoon(2026, 9, 13)) + 1, Groups.DayOf(LocalNoon(2026, 9, 14)));
    }

    // ── the slot, mounted ────────────────────────────────────────────────────────────────────────────────────────────

    sealed class LikedTable : Component
    {
        public override Element Render()
            => new BoxEl
            {
                Width = 900f, Height = 600f, Direction = 1,
                Children =
                [
                    Track.Table(new Track.TableArgs
                    {
                        Source = Track.TableSource.ForLiked(new User(Entities.Current.MeSlot)),
                        Profile = Track.TableProfile.From(Detail.Config.Liked),
                        ShowToolbar = false, Embedded = true, ScrollKey = "liked-groups",
                    }),
                ],
            };
    }

    static NodeHandle FindList(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n) && s.TryGetScroll(n, out var sc) && sc.ItemCount > 20) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var found = FindList(s, c);
            if (!found.IsNull) return found;
        }
        return NodeHandle.Null;
    }

    /// <summary>Which DISPLAYED rows start a group, computed straight from the fake catalog the way the table orders it
    /// (Date added, newest first; ties keep the source order) - independent of the table's own plan.</summary>
    static bool[] ExpectedStarts(Track.TableSource src)
    {
        int n = src.Count;
        int[] order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) => { int c = src.AddedAt(b).CompareTo(src.AddedAt(a)); return c != 0 ? c : a.CompareTo(b); });
        long now = Store.ToUnix(Entities.Now);
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var keys = order.Select(o => Groups.KeyOf(src.AddedAt(o), now, first)).ToArray();
        return Enumerable.Range(0, n).Select(i => Groups.StartsGroup(keys, i)).ToArray();
    }

    /// <summary>Every realized slot is the height the list seeded for it, and a group-starting slot's focus ring is inset
    /// by exactly the strip.</summary>
    static void AssertSlots(SceneStore scene, NodeHandle list, bool[] starts, float rowH)
    {
        scene.TryGetScroll(list, out var sc);
        var tops = new Dictionary<int, int>(starts.Length);
        float y = 0f;
        for (int i = 0; i < starts.Length; i++) { tops[(int)MathF.Round(y)] = i; y += Groups.Extent(rowH, starts[i]); }

        int checkedSlots = 0, headers = 0, plain = 0;
        for (var c = scene.FirstChild(sc.ContentNode); !c.IsNull; c = scene.NextSibling(c))
        {
            var b = scene.Bounds(c);
            Assert.True(tops.TryGetValue((int)MathF.Round(b.Y), out int i), $"a slot sits at y={b.Y}, which is no row's analytic top");
            Assert.Equal(Groups.Extent(rowH, starts[i]), b.H, 0.5);
            Assert.Equal(starts[i] ? Groups.HeaderH : 0f, scene.Interaction(c).FocusVisualMargin.Top, 0.01);
            Assert.Equal(0f, scene.Interaction(c).FocusVisualMargin.Bottom, 0.01);
            if (starts[i]) headers++; else plain++;
            checkedSlots++;
        }
        Assert.True(checkedSlots >= 8, $"only {checkedSlots} slots realized");
        Assert.True(headers >= 1 && plain >= 1, $"headers={headers} plain={plain}: the window must show both shapes");
        // the content extent is the analytic sum, not an estimate that measure feedback had to correct
        Assert.Equal(starts.Length * rowH + starts.Count(s => s) * Groups.HeaderH, sc.ContentH, 1.0);
    }

    [Fact]
    public void A_group_starting_slot_is_the_row_plus_the_strip_at_every_density_and_its_ring_wraps_only_the_row()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(1_790_000_000);
        Platform.UseSettings(new MemoryAppSettings());
        try
        {
            for (int density = 0; density <= 3; density++)
            {
                Prefs.Appearance.Set(Platform.Keys.RowDensity, density);
                float rowH = Track.TableRules.RowHeightFor(density, classic: false);

                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("liked-groups", new Size2(900f, 600f), 1f));
                window.Show();
                var dev = new HeadlessGpuDevice();
                var strings = Entities.Strings;
                using var host = new AppHost(app, window, dev, new HeadlessFontSystem(strings), strings, new LikedTable());
                for (int i = 0; i < 30; i++) host.RunFrame();

                var list = FindList(host.Scene, host.Scene.Root);
                Assert.False(list.IsNull, "the liked table's virtual list did not mount");
                var src = Track.TableSource.ForLiked(new User(Entities.Current.MeSlot));
                var starts = ExpectedStarts(src);
                Assert.True(starts.Length >= 100 && starts[0], "the fake liked list should start with a group");
                Assert.True(starts.Count(s => s) >= 2, "the fake liked list should hold at least two groups");

                AssertSlots(host.Scene, list, starts, rowH);

                // …and again after a scroll recycled every slot: a recycled slot keeps its pool's shape.
                var handle = host.TryGetScrollHandle(list);
                Assert.NotNull(handle);
                handle!.ScrollTo(1500.0, ScrollMove.Immediate);
                for (int i = 0; i < 6; i++) host.RunFrame();
                AssertSlots(host.Scene, list, starts, rowH);
            }
        }
        finally { Platform.UseSettings(null); }
    }

    static void CollectTexts(SceneStore scene, StringTable strings, NodeHandle n, List<string> into)
    {
        if (n.IsNull) return;
        var id = scene.Paint(n).Text;
        if (id != default) into.Add(strings.Resolve(id));
        for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) CollectTexts(scene, strings, c, into);
    }

    [Fact]
    public void Unliking_a_song_in_the_first_group_updates_the_strip_count_in_place()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(1_790_000_000);
        Platform.UseSettings(new MemoryAppSettings());
        try
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("liked-groups-live", new Size2(900f, 600f), 1f));
            window.Show();
            var dev = new HeadlessGpuDevice();
            var strings = Entities.Strings;
            using var host = new AppHost(app, window, dev, new HeadlessFontSystem(strings), strings, new LikedTable());
            for (int i = 0; i < 30; i++) host.RunFrame();

            var list = FindList(host.Scene, host.Scene.Root);
            Assert.False(list.IsNull, "the liked table's virtual list did not mount");
            var me = new User(Entities.Current.MeSlot);
            var src = Track.TableSource.ForLiked(me);
            var starts = ExpectedStarts(src);
            int size = 1;
            while (size < starts.Length && !starts[size]) size++;
            Assert.True(size >= 2, "the fake liked list's first group should hold at least two songs");

            List<string> StripTexts()
            {
                host.Scene.TryGetScroll(list, out var sc);
                var texts = new List<string>();
                CollectTexts(host.Scene, strings, host.Scene.FirstChild(sc.ContentNode), texts);
                return texts;
            }
            Assert.Contains(size.ToString("N0", CultureInfo.CurrentCulture), StripTexts());

            int[] order = Enumerable.Range(0, src.Count).ToArray();
            Array.Sort(order, (a, b) => { int c = src.AddedAt(b).CompareTo(src.AddedAt(a)); return c != 0 ? c : a.CompareTo(b); });
            var gone = src.At(order[0]);
            int before0 = src.Count;
            me.Unlike(gone);
            me.Settle(LibraryEdgeKind.Liked, gone.Slot, true);   // the server agreed: the row leaves the edge
            Entities.Publish();                                  // …and the tables wake the subscribed table
            for (int i = 0; i < 12; i++) host.RunFrame();
            Assert.Equal(before0 - 1, Track.TableSource.ForLiked(me).Count);

            var after = StripTexts();
            
            Assert.Contains((size - 1).ToString("N0", CultureInfo.CurrentCulture), after);
            if (size >= 10) Assert.DoesNotContain(size.ToString("N0", CultureInfo.CurrentCulture), after);
        }
        finally { Platform.UseSettings(null); }
    }
}
