// ── Screens/SettingsThemeOptions.cs — the active theme's options as the page shows them (themes plan 8.1-8.2, T1b; T11b) ───────────────
//
// Role: CORE. `ThemeOptionsSection.Current` turns the active composition's exposed schema into rows; THIS file groups them for display: the legacy rows
// (the `SettingsDoc` rows a structure still binds to today's keys, listed by `IStructureLayer.LegacyRows`) first, in order, then the generated ones, a group
// per label in the order its first row arrived. A row the facts hide (developer only, composed away) does not exist. The sidebar's two notes (a layout file
// the app could not read; what a migration dropped) ride the sidebar rows. The structure's `Settings.ThemeOptions` view draws what this returns (the Classic
// expander, the wizard's plate rows); the wizard step asks `HasWizardOptions` to know whether to place it at all.
//
// WHAT THIS FILE NEVER DOES: name a theme, or draw. A theme with no options gets one quiet row from the view; one with only legacy rows gets them.

using Wavee.Theming;

namespace Wavee;

/// <summary>What one entry of an options group is.</summary>
public enum SettingsOptionItemKind : byte
{
    /// <summary>A <see cref="SettingsRow"/> a structure still binds to a legacy key, drawn by its document id.</summary>
    Legacy,

    /// <summary>A row generated from the theme's schema.</summary>
    Generated,

    /// <summary>"The sidebar layout file could not be read" (rides the layout cards).</summary>
    SidebarFileFault,

    /// <summary>"What changed" after a sidebar migration dropped something; <see cref="SettingsOptionItem.Text"/> names what.</summary>
    SidebarWhatChanged,
}

/// <summary>One entry of an options group.</summary>
public sealed class SettingsOptionItem
{
    /// <summary>The entry's identity in its group (<c>legacy:sidebarDesign</c>, <c>opt:style.density</c>): the view keys the row by it.</summary>
    public required string Key { get; init; }

    /// <inheritdoc cref="SettingsOptionItemKind"/>
    public required SettingsOptionItemKind Kind { get; init; }

    /// <summary>The legacy row (<see cref="SettingsOptionItemKind.Legacy"/>).</summary>
    public SettingsRow? Row { get; init; }

    /// <summary>The generated row (<see cref="SettingsOptionItemKind.Generated"/>).</summary>
    public GeneratedOptionRow? Generated { get; init; }

    /// <summary>The note's description (<see cref="SettingsOptionItemKind.SidebarWhatChanged"/>).</summary>
    public string? Text { get; init; }
}

/// <summary>One group of options under one label, in the order the rows arrived.</summary>
public sealed class SettingsOptionGroup(string title)
{
    /// <summary>The group's label (already worded).</summary>
    public string Title { get; } = title;

    /// <summary>Its entries.</summary>
    public List<SettingsOptionItem> Items { get; } = new(4);
}

/// <summary>The options of the active theme, ready to draw.</summary>
/// <param name="NameKey">The loc key of the theme's name (the section reads "‹name› options").</param>
/// <param name="Groups">The groups; none means the theme has nothing to configure here.</param>
/// <param name="Reset">"Reset options", or null when nothing is stored.</param>
public sealed record SettingsOptionsView(string NameKey, IReadOnlyList<SettingsOptionGroup> Groups, SettingsRow? Reset);

/// <summary>Builds <see cref="SettingsOptionsView"/>.</summary>
public static class SettingsThemeOptions
{
    /// <summary>Everything an options view re-renders on: the page epoch (legacy writes), the active option cells (a write from the palette lands here
    /// too), the sidebar's own signals and Developer mode. Call it from a render: it subscribes the caller.</summary>
    public static void Track()
    {
        _ = Settings.PageEpoch.Value;
        _ = ThemeHost.Options.EpochSignal.Value;
        _ = Sidebar.LayoutVersion.Value;
        _ = Platform.Developer.Enabled.Value;
    }

    /// <summary>The active theme's options, grouped. Subscribes the caller (see <see cref="Track"/>).</summary>
    public static SettingsOptionsView Current(bool wizard)
    {
        Track();
        ThemeOptionsModel model = ThemeOptionsSection.Current(Platform.Developer.Enabled.Value);
        return new SettingsOptionsView(model.NameKey, Groups(model, SettingsFacts.Live, wizard), model.Reset);
    }

    /// <summary>The wizard's Theme step places its options only when there is something to pick. Subscribes the caller.</summary>
    public static bool HasWizardOptions() => Current(wizard: true).Groups.Count > 0;

    /// <summary>The rows of the model grouped for display (see the file header). The wizard shows only the sidebar layout cards (until M7a makes it a
    /// generated Cards row) and the generated rows that are not actions (a wizard step resets nothing).</summary>
    public static IReadOnlyList<SettingsOptionGroup> Groups(ThemeOptionsModel model, ISettingsFacts facts, bool wizard)
    {
        var groups = new List<SettingsOptionGroup>(4);
        SettingsOptionGroup GroupFor(string key)
        {
            string title = ThemeOptionsSection.GroupTitle(key);
            foreach (SettingsOptionGroup g in groups)
                if (string.Equals(g.Title, title, StringComparison.Ordinal)) return g;
            var created = new SettingsOptionGroup(title);
            groups.Add(created);
            return created;
        }

        foreach (LegacyOptionRow legacy in model.Legacy)
        {
            if (wizard && legacy.RowId != "sidebarDesign") continue;   // the wizard's Layout step, until M7a makes it a generated Cards row
            if (!SettingsDoc.TryRow(Settings.Tab.Appearance, legacy.RowId, out SettingsRow row) || !row.Visible(facts)) continue;
            SettingsOptionGroup group = GroupFor(legacy.Group);
            // The sidebar's two notes (a layout file the app could not read; what a migration dropped) ride the sidebar rows.
            if (legacy.RowId == "sidebarDesign" && Sidebar.LayoutFileFault && !wizard)
                group.Items.Add(new SettingsOptionItem { Key = "legacy:sidebarFileFault", Kind = SettingsOptionItemKind.SidebarFileFault });
            group.Items.Add(new SettingsOptionItem { Key = "legacy:" + legacy.RowId, Kind = SettingsOptionItemKind.Legacy, Row = row });
            if (legacy.RowId == "sidebarResetEverything" && SettingsFacts.SidebarMigrationDropped() is { Length: > 0 } dropped)
                group.Items.Add(new SettingsOptionItem
                {
                    Key = "legacy:sidebarWhatChanged", Kind = SettingsOptionItemKind.SidebarWhatChanged,
                    Text = Strings.Settings.Sidebar.WhatChangedSub(Sidebar.DroppedNames(dropped.Split(','))),
                });
        }

        foreach (GeneratedOptionRow generated in model.Rows)
        {
            if (!generated.Row.Visible(facts)) continue;
            if (wizard && generated.Row.Kind == SettingsRowKind.Action) continue;
            GroupFor(generated.Group).Items.Add(new SettingsOptionItem { Key = "opt:" + generated.QualifiedId, Kind = SettingsOptionItemKind.Generated, Generated = generated });
        }
        return groups;
    }
}
