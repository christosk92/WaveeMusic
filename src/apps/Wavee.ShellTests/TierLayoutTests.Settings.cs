// ── Wavee.ShellTests/TierLayoutTests.Settings.cs — Classic's Settings page at every tier size and threshold edge (themes plan 3.5; TC8) ─────────────
//
// The Appearance and Playback tabs (the boards' tabs: `C-Settings*`, `C-SettingsSections*`, `C-ThemeOptions*`) mounted through the real frame at the plan 3.5 sizes (400 x 860,
// 800 x 330, 800 x 600, 1440 x 960, 2560 x 1440 and the floors, the Auto sizes and every threshold edge of Classic's ladder), with `TierLayoutTests.AssertPage`'s frame assertions
// and, on top, the page's own: the record in use is `ClassicSettingsTiers`' for the CONTENT tier, the tab strip scrolls only where the record says so, the body is one column
// (capped at 1000) or, at Huge, two 1000-capped columns that never overlap and start on the gutter (left-aligned, never centred), and at Compact every control that can be
// pressed is at least 40 x 40. Asserted on the records and the node geometry, never on pixels.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    static readonly TierPage SettingsPage = new("settings", new Shell.Route(Shell.RouteKind.Settings), "settings:title");

    [Theory, MemberData(nameof(PageSizes))]
    public void Settings_Appearance_holds_its_record_at_every_tier(string size) => AssertSettings(Settings.Tab.Appearance, size);

    [Theory, MemberData(nameof(PageSizes))]
    public void Settings_Playback_holds_its_record_at_every_tier(string size) => AssertSettings(Settings.Tab.Playback, size);

    [Fact]
    public void Settings_General_at_the_floor_has_no_target_under_40()
        => AssertSettings(Settings.Tab.General, "floor 300x640");

    void AssertSettings(Settings.Tab tab, string size)
    {
        host.Run(() => Settings.Open(tab));   // the next mount opens on the tab
        AssertPage(SettingsPage, size, (shell, tiers) =>
        {
            var layout = ClassicSettingsTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);   // the record is the content tier's
            Assert.Equal(layout.Tier == WidthTier.Compact, layout.Touch);

            var scene = shell.Host.Scene;
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            float gutter = ClassicTiers.PageOf(tiers).Gutter;

            // The tab strip: one scrolling lane (Compact, Small) or the stock strip in the column (Standard, Huge).
            Assert.Equal(layout.TabsScroll, !shell.Find("settings:tabs").IsNull);
            if (layout.TabsScroll)
            {
                var strip = scene.AbsoluteRect(Node(shell, "settings:tabs"));
                Assert.True(strip.W <= card.W + 0.5f, $"{size}: the tab strip is wider than the card");
                Assert.True(strip.H >= layout.TabHeight - 0.5f, $"{size}: the tab strip is {strip.H} tall, under the record's {layout.TabHeight}");
            }

            // The body: one column capped by the record, or two that share the row without overlap, left-aligned on the gutter.
            if (layout.Columns == 1)
            {
                Assert.True(shell.Find("settings:column:0").IsNull && shell.Find("settings:column:1").IsNull, $"{size}: a one-column tier has two columns");
                var column = scene.AbsoluteRect(Node(shell, "settings:column"));
                Assert.True(column.W <= layout.ColumnMaxW + 0.5f, $"{size}: the column is {column.W}, over its cap {layout.ColumnMaxW}");
                Assert.True(column.X + column.W <= card.X + card.W + 0.5f, $"{size}: the column runs outside the card");
                Assert.Equal(card.X + gutter, column.X, 1.5f);   // left-aligned on the gutter, never centred
            }
            else
            {
                Assert.True(shell.Find("settings:column").IsNull, $"{size}: a two-column tier has one column");
                var left = scene.AbsoluteRect(Node(shell, "settings:column:0"));
                var right = scene.AbsoluteRect(Node(shell, "settings:column:1"));
                Assert.True(left.W <= layout.ColumnMaxW + 0.5f && right.W <= layout.ColumnMaxW + 0.5f, $"{size}: a column is over its cap ({left.W}, {right.W})");
                Assert.True(left.X + left.W + layout.ColumnGap <= right.X + 0.5f, $"{size}: the columns overlap or sit closer than the gap ({left}, {right})");
                Assert.True(right.X + right.W <= card.X + card.W + 0.5f, $"{size}: the second column runs outside the card");
                Assert.Equal(card.X + gutter, left.X, 1.5f);
            }

            // The theme picker (Appearance) takes the record's form: the grid's cards share two columns, the strip's cards are one row.
            var cards = shell.FindPrefix("settings:theme:card:");
            if (cards.Count > 0)
            {
                var rects = cards.Select(c => scene.AbsoluteRect(c.Node)).ToList();
                if (layout.Picker == ClassicThemePickerForm.Grid && rects.Count >= 2)
                    Assert.Equal(2, rects.Select(r => (int)MathF.Round(r.X)).Distinct().Count());
                if (layout.Picker == ClassicThemePickerForm.Strip)
                    Assert.Single(rects.Select(r => (int)MathF.Round(r.Y)).Distinct());
                if (layout.PickerCardW > 0f) foreach (var r in rects) Assert.Equal(layout.PickerCardW, r.W, 1f);
            }

            // Compact: every control that can be pressed in the body and the strip is at least 40 x 40 (the brief's rule).
            if (layout.Touch) AssertSettingsTargets(shell, tab, size);
            AssertStockCombos(shell, tab, size, layout.Touch);
        });
    }

    /// <summary>The page's choices are the stock ComboBox at every tier: its Large 40-DIP field at Compact, the engine's own 32 elsewhere (unchanged).</summary>
    static void AssertStockCombos(Shell.HeadlessShell shell, Settings.Tab tab, string size, bool touch)
    {
        var scene = shell.Host.Scene;
        string slug = Settings.TabSlugs[(int)tab];
        int combos = 0;
        foreach (var n in Subtree(scene, Node(shell, "settings:scroll:" + slug)))
        {
            if (scene.InCollapsedSubtree(n) || scene.Interaction(n).Role != AutomationRole.ComboBox) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0.5f || r.H <= 0.5f) continue;
            combos++;
            Assert.True(MathF.Abs(r.H - (touch ? 40f : ComboBox.MinHeight)) <= 0.5f, $"{size}: a {slug} combo is {r.H:0.#} tall");
        }
        if (tab == Settings.Tab.Playback) Assert.True(combos > 0, $"{size}: the Playback tab shows its combos");
    }

    static readonly AutomationRole[] SettingsTargetRoles =
    [
        AutomationRole.Button, AutomationRole.ToggleButton, AutomationRole.CheckBox, AutomationRole.RadioButton, AutomationRole.Hyperlink,
        AutomationRole.ToggleSwitch, AutomationRole.ComboBox, AutomationRole.Tab,
    ];

    static void AssertSettingsTargets(Shell.HeadlessShell shell, Settings.Tab tab, string size)
    {
        var scene = shell.Host.Scene;
        string slug = Settings.TabSlugs[(int)tab];
        var roots = new[] { Node(shell, "settings:tabs"), Node(shell, "settings:scroll:" + slug) };
        var offenders = new List<string>();
        int targets = 0;
        foreach (var root in roots)
            foreach (var n in Subtree(scene, root))
            {
                if (scene.InCollapsedSubtree(n) || !SettingsTargetRoles.Contains(scene.Interaction(n).Role)) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W <= 0.5f || r.H <= 0.5f) continue;
                targets++;
                if (r.W < 39.5f || r.H < 39.5f) offenders.Add($"{scene.Interaction(n).Role} {r.W:0.#} x {r.H:0.#} at ({r.X:0}, {r.Y:0})");
            }
        Assert.True(targets >= 4, $"{size}: the {slug} tab offers its targets (found {targets})");
        Assert.True(offenders.Count == 0, $"{size}: the {slug} tab has targets under 40 x 40: " + string.Join("; ", offenders.Take(8)));
    }
}
