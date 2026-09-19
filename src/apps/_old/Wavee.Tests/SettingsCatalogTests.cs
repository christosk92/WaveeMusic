using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins the rule Settings regroup (Workstream B) exists to enforce: "every row carries its own glyph; the
/// section header's glyph is never reused by a row." Runs against the plain <see cref="SettingsCatalog"/> data — no
/// engine, no render — so it catches a regression the moment someone edits the table, not the next time a human
/// happens to look at the Settings page.</summary>
public class SettingsCatalogTests
{
    static IEnumerable<(SettingsTab Tab, string Section)> AllSections()
        => SettingsCatalog.Sections.Select(s => (s.Tab, Section: s.Title)).Distinct();

    [Fact]
    public void EverySection_HasAtLeastOneRow()
    {
        foreach (var (tab, section) in AllSections())
        {
            bool any = SettingsCatalog.Rows.Any(r => r.Tab == tab && r.Section == section);
            Assert.True(any, $"{tab}/{section} has a header but no rows.");
        }
    }

    [Fact]
    public void NoRow_ReusesItsOwnSectionsGlyph()
    {
        foreach (var row in SettingsCatalog.Rows)
        {
            string sectionGlyph = SettingsCatalog.SectionGlyph(row.Tab, row.Section);
            Assert.False(row.Glyph == sectionGlyph,
                $"{row.Tab}/{row.Section}: row '{row.RowId}' repeats the section's own glyph '{sectionGlyph}'.");
        }
    }

    [Fact]
    public void NoGlyph_RepeatsWithinASection()
    {
        foreach (var group in SettingsCatalog.Rows.GroupBy(r => (r.Tab, r.Section)))
        {
            var dupes = group.GroupBy(r => r.Glyph).Where(g => g.Count() > 1).ToArray();
            Assert.True(dupes.Length == 0,
                $"{group.Key.Tab}/{group.Key.Section}: glyph(s) {string.Join(", ", dupes.Select(d => d.Key))} repeat across rows.");
        }
    }

    [Fact]
    public void RowIds_AreUniquePerTab()
    {
        foreach (var group in SettingsCatalog.Rows.GroupBy(r => r.Tab))
        {
            var dupes = group.GroupBy(r => r.RowId).Where(g => g.Count() > 1).ToArray();
            Assert.True(dupes.Length == 0,
                $"{group.Key}: row id(s) {string.Join(", ", dupes.Select(d => d.Key))} are not unique on this tab.");
        }
    }

    [Fact]
    public void RowGlyph_ThrowsForAnUnknownRow()
        => Assert.Throws<System.InvalidOperationException>(() => SettingsCatalog.RowGlyph(SettingsTab.General, "no-such-row"));

    [Fact]
    public void SectionGlyph_ThrowsForAnUnknownSection()
        => Assert.Throws<System.InvalidOperationException>(() => SettingsCatalog.SectionGlyph(SettingsTab.General, "No Such Section"));
}
