// ── Wavee.ShellTests/TierLayoutTests.SettingsCrossing.cs — Settings keeps its sections and its theme picker across a crossing (themes plan 3.3) ───────────
//
// The owner's rule (a crossing never rebuilds a part) for the two Settings parts that used to change their tree with the tier:
//
//   · the tab body's sections: Huge lays them out in two columns, every other tier in one. The split is made at EVERY tier (the keyed pair
//     `settings:columns` holding `settings:column:0` and `:1`, a row at Huge and a column elsewhere), so crossing into or out of Huge keeps every section
//     header, row and expander as the same node, keeps the page scroll and the focus, and glides the columns;
//   · the theme picker: a two-column grid at Compact, a scrolling strip at Small, the tuned wrap at Standard, the five-up fit at Huge. The radio group
//     always rides the one lane `settings:theme:lane` (sideways and scrolling at Small, a content-sized box elsewhere), so every crossing keeps the group,
//     its cards and the focused card, and the cards glide.
//
// The Huge crossings are 1440 <-> 2048 (clear of every hold: the window and the content region both cross) and the window edge itself, 1919 -> 1920
// widening and 1920 -> 1879 narrowing (the 40 hold: back below 1880). At the edge the CONTENT tier may or may not cross with the window (the page reads the
// content tier); either way the parts are kept, and the columns follow the record of the tier the facts hold after the crossing.
//
// The crossing rig (the resized frame that keeps the old tier, then the frame that commits) is `TierLayoutTests.Crossing.cs`'s.

using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    /// <summary>Into and out of Huge: clear of the holds both ways, and the window edge from each side.</summary>
    public static TheoryData<float, float> HugeCrossings() => new()
    {
        { 1440f, 2048f }, { 2048f, 1440f },
        { 1919f, 1920f },   // widening onto the edge: Standard -> Huge
        { 1920f, 1879f },   // narrowing past the hold: Huge -> Standard
    };

    /// <summary>Every crossing the picker changes its form on: 528 and 1008 both ways, and Huge.</summary>
    public static TheoryData<float, float> PickerCrossings()
    {
        var data = WindowCrossings();
        data.Add(1440f, 2048f);
        data.Add(2048f, 1440f);
        return data;
    }

    /// <summary>Open Settings on <paramref name="tab"/> at <paramref name="from"/> wide and run <paramref name="body"/> against the settled page.</summary>
    void OnSettings(Settings.Tab tab, float from, Action<Shell.HeadlessShell> body)
    {
        host.Run(() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings)));
        host.Mounted(shell =>
        {
            Settings.Open(tab);
            string slug = Settings.TabSlugs[(int)tab];
            Until(shell, () => !shell.Find("settings:scroll:" + slug).IsNull, "the " + slug + " tab");
            shell.RunFrames(90);
            body(shell);
        }, from, CrossH);
    }

    [Theory, MemberData(nameof(HugeCrossings))]
    public void A_huge_crossing_keeps_every_settings_section_the_scroll_and_the_focus(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Settings Huge {from} -> {to}";
        OnSettings(Settings.Tab.Playback, from, shell =>
        {
            var scene = shell.Host.Scene;
            var pair = PartOf(shell, "settings:columns");
            var left = PartOf(shell, "settings:column:0");
            var right = PartOf(shell, "settings:column:1");
            // Every section header, and every card of both columns (a row, an expander), is a part that must survive.
            var sections = PartsOf(shell, "settings:section:", static k => !k.StartsWith("settings:section-caption:", StringComparison.Ordinal));
            Assert.True(sections.Count >= 2, $"{what}: the Playback tab has its sections ({sections.Count})");
            var cards = new List<Part>();
            foreach (var column in new[] { left, right })
                for (var c = scene.FirstChild(column.Node); !c.IsNull; c = scene.NextSibling(c))
                    cards.Add(PartOf(shell, "card#" + cards.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), c));

            var page = PartOf(shell, "settings:scroll:playback");
            var viewport = ScrollUnder(shell, page.Node);
            Assert.False(viewport.IsNull, $"{what}: the tab has its scroller");
            double offset = ScrollTo(shell, viewport, 300.0);

            // Focus a control in the SECOND column: the half that moves (beside the first at Huge, under it elsewhere).
            var focus = Subtree(scene, right.Node).FirstOrDefault(n => (scene.Flags(n) & NodeFlags.Focusable) != 0 && !scene.HasScroll(n));
            if (!focus.IsNull)
            {
                shell.Host.Input.SetFocus(focus, visual: false);
                shell.RunFrames(2);
            }

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [pair, left, right, .. sections, .. cards]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, pair, "settings:columns", what);
            AssertSameNode(shell, left, "settings:column:0", what);
            AssertSameNode(shell, right, "settings:column:1", what);
            Assert.True(shell.Find("settings:column").IsNull, $"{what}: a lone column appeared beside the pair");

            shell.RunFrames(60);
            foreach (var p in parts) Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} did not survive the settle");
            Assert.True(Single(shell, "settings:scroll:playback").Node.Equals(page.Node), $"{what}: the tab's scroller was remounted");
            if (offset > 50.0 && Handle(shell, viewport).MaxOffset > 0.5)
                Assert.True(Handle(shell, viewport).OffsetNow > 0.5, $"{what}: the tab's scroll was reset to the top");
            if (!focus.IsNull) Assert.True(shell.Host.Input.Focused.Equals(focus), $"{what}: the focused control lost focus on the crossing");

            // The pair follows the record of the tier the facts hold now: side by side at Huge, stacked (one column's edge) elsewhere.
            var layout = ClassicSettingsTiers.Of(Tiers);
            var l = scene.AbsoluteRect(left.Node);
            var r = scene.AbsoluteRect(right.Node);
            if (layout.Columns > 1)
                Assert.True(l.X + l.W + layout.ColumnGap <= r.X + 0.5f, $"{what}: the Huge columns are not side by side ({l}, {r})");
            else
            {
                Assert.Equal(l.X, r.X, 0.5f);
                Assert.True(r.Y >= l.Y + l.H - 0.5f, $"{what}: the stacked halves overlap ({l}, {r})");
            }
        });
    }

    [Theory, MemberData(nameof(PickerCrossings))]
    public void A_crossing_keeps_the_theme_picker_its_cards_and_its_focus(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Theme picker {from} -> {to}";
        OnSettings(Settings.Tab.Appearance, from, shell =>
        {
            var scene = shell.Host.Scene;
            Until(shell, () => shell.FindPrefix("settings:theme:card:").Count > 0, "the theme cards");
            var picker = PartOf(shell, "settings:theme:picker");
            var lane = PartOf(shell, "settings:theme:lane");
            var cards = PartsOf(shell, "settings:theme:card:");
            var radio = Subtree(scene, lane.Node).FirstOrDefault(n => (scene.Flags(n) & NodeFlags.Focusable) != 0 && !scene.HasScroll(n));
            Assert.False(radio.IsNull, $"{what}: the picker has a focusable card");
            shell.Host.Input.SetFocus(radio, visual: true);
            shell.RunFrames(2);
            Assert.True(shell.Host.Input.Focused.Equals(radio), $"{what}: the card took focus");

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [picker, lane, .. cards]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, picker, "settings:theme:picker", what);
            AssertSameNode(shell, lane, "settings:theme:lane", what);

            shell.RunFrames(60);
            foreach (var p in parts) Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} did not survive the settle");
            Assert.True(shell.Host.Input.Focused.Equals(radio), $"{what}: the focused theme card lost focus on the crossing");
            // The lane takes the new record's form: sideways only where the record scrolls.
            var layout = ClassicSettingsTiers.Of(Tiers);
            Assert.True((scene.ScrollRef(lane.Node).Orientation == 1) == layout.PickerScrolls, $"{what}: the lane's orientation does not follow the {layout.Tier} record");
        });
    }
}
