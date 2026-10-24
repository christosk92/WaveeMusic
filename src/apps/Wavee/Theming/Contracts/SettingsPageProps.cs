// ── Theming/Contracts/SettingsPageProps.cs — the props of the Settings views (themes plan 6.0, T11b) ───────────────────────────
//
// `Settings.PageHost` (`Screens\Settings.Page.cs`, `[CoreHost]`) keeps the hooks, the page epoch, the page context and the Storage census; it hands the
// view the pivot's state and the census as data. The rows themselves are not in the props: a view reads the document (`SettingsDoc.Tabs`, `SettingsDoc.Row`)
// and writes through each row's writer, so a second theme draws the same page from the same source. Every member is read-only (WVTH006); the writable
// pivot cell travels as a `Func<Signal<T>>`.

using FluentGpu.Signals;
using Wavee.Sdk.Streams;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.SettingsPage"/>. The host builds a new record on every render it makes, and a record that differs from the
/// last one re-runs the template: <see cref="Revision"/> is how a bump of the page epoch (a writer that asks the page to re-read) reaches it.</summary>
public sealed record SettingsPageProps
{
    /// <summary>The visible section, a <see cref="Settings.Area"/> index clamped to the twelve areas.</summary>
    public required int Area { get; init; }

    /// <summary>SUPERSEDED by <see cref="Area"/> (contract 1.48; a minor bump only adds, so the pre-areas member stays): the document tab of the open area's first section, a
    /// <see cref="Settings.Tab"/> index. A theme still written against the seven-tab pivot reads this and keeps working; the page itself is the area.</summary>
    public int Tab => (int)Settings.FirstTab((Settings.Area)Math.Clamp(Area, 0, Settings.AreaSlugs.Length - 1));

    /// <summary>The structure's grouping of the areas into tabs (P6 WK, <c>ThemePolicies.Settings</c>): <see cref="SettingsGrouping.Default"/>, one tab per area,
    /// where the structure declares none. A pivot of tabs draws <see cref="SettingsGrouping.Tabs"/> and each tab's sections (<see cref="SettingsGrouping.PartsOf"/>).</summary>
    public SettingsGrouping Grouping { get; init; } = SettingsGrouping.Default;

    /// <summary>The tab the open area lives in (P6 WK): <see cref="SettingsGrouping.GroupOf"/> of <see cref="Area"/>. Equal to <see cref="Area"/> under the default grouping.</summary>
    public int Group { get; init; }

    /// <summary>Show tab <c>i</c> of <see cref="Grouping"/> (P6 WK): the area cell moves to the tab's first home area. A pivot over the areas keeps using <see cref="AreaCell"/>.</summary>
    public Action<int> OpenGroup { get; init; } = static _ => { };

    /// <summary>The page epoch plus the two foreign epochs the Appearance area reads (the player bar's and the fullscreen stage's). It only grows.</summary>
    public required int Revision { get; init; }

    /// <summary>The pivot's cell (root-lifetime state: a remounted root opens the first tab, a theme pick opens Appearance). The pivot binds it once.</summary>
    public required Func<Signal<int>> AreaCell { get; init; }

    /// <summary>SUPERSEDED by <see cref="AreaCell"/> (contract 1.48): the same cell. It now holds the AREA index (the pivot's twelve sections), not a document tab's.</summary>
    public Func<Signal<int>> TabCell => AreaCell;

    /// <summary>The route names an open section (its arg is the area's slug). A theme with a sections-then-section page (Zune's Compact) shows that section's page; a theme with a
    /// pivot ignores it (the area is <see cref="Area"/> either way).</summary>
    public bool OpenSection { get; init; }

    /// <summary>SUPERSEDED by <see cref="OpenArea"/> (contract 1.48): open the area that holds the document tab <c>i</c> (a <see cref="Settings.Tab"/> index) as a page of its own.</summary>
    public Action<int> OpenTab => i => OpenArea((int)Settings.HomeArea((Settings.Tab)Math.Clamp(i, 0, Settings.TabSlugs.Length - 1)));

    /// <summary>Open section <c>i</c> (a <see cref="Settings.Area"/> index) as a page of its own: a history step from the sections list.</summary>
    public Action<int> OpenArea { get; init; } = static _ => { };

    /// <summary>Close the open section: back to the sections list (Back when the list is the entry below, else the page swapped in place).</summary>
    public Action CloseSection { get; init; } = static () => { };

    /// <summary>The Storage census while the Storage tab is visible, else <see cref="SettingsStorageProps.Idle"/>.</summary>
    public required SettingsStorageProps Storage { get; init; }
}

/// <summary>The Storage tab's census as the host holds it (written off the UI thread, posted back, never read from the disk by a view).</summary>
/// <param name="Phase">NotStarted → Loading → Ready | Failed.</param>
/// <param name="Snapshot">The seven category sizes, null until the census answers.</param>
/// <param name="Error">The failed census's message.</param>
/// <param name="Audio">The audio-body cache's own status (its directory, bytes, budget, reserve), null when it does not exist (--fake).</param>
/// <param name="KeyCount">How many license keys the audio path keeps, null when the store is off.</param>
/// <param name="Meta">The metadata cache tier's census, null until it answers.</param>
/// <param name="AudioDirectory">The audio-body cache's folder (the cache's own report, else the resolved setting): the Cache location row's description.</param>
public sealed record SettingsStorageProps(Settings.StorageLoadPhase Phase, Settings.StorageSnapshot? Snapshot, string? Error,
    AudioBodyCacheStatus? Audio, int? KeyCount, Settings.MetadataCacheSnapshot? Meta, string AudioDirectory)
{
    /// <summary>What a tab other than Storage carries: nothing is read, nothing subscribes.</summary>
    public static SettingsStorageProps Idle { get; } = new(Settings.StorageLoadPhase.NotStarted, null, null, null, null, null, "");
}

/// <summary>The props of <see cref="ViewKeys.SettingsThemePicker"/>: none. The picker reads <c>ThemePickerFacts</c> and Developer mode itself, so it
/// re-renders on its own signals.</summary>
public sealed record SettingsThemePickerProps
{
    /// <summary>The one instance (an equal record skips the template).</summary>
    public static SettingsThemePickerProps Instance { get; } = new();
}

/// <summary>The props of <see cref="ViewKeys.SettingsThemeOptions"/>.</summary>
public sealed record SettingsThemeOptionsProps
{
    /// <summary>False: the Settings page's expander. True: the wizard's plate rows (group labels over plate cards, without the actions).</summary>
    public required bool Wizard { get; init; }

    /// <summary>The Settings page's options.</summary>
    public static SettingsThemeOptionsProps Page { get; } = new() { Wizard = false };

    /// <summary>The wizard's options.</summary>
    public static SettingsThemeOptionsProps WizardPlate { get; } = new() { Wizard = true };
}
