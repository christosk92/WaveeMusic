// ── Theming/Contracts/SettingsPageProps.cs — the props of the Settings views (themes plan 6.0, T11b) ───────────────────────────
//
// `Settings.PageHost` (`Screens\Settings.Page.cs`, `[CoreHost]`) keeps the hooks, the page epoch, the page context and the Storage census; it hands the
// view the open category, the ten entries, a link's arrival, the search box model, the home, the category bodies and the census as data
// (`SettingsPageModel.cs` beside this file). The rows are the registry's dials (`SettingsSpec`) composed into bodies: a view draws them and writes
// through each dial's writer, so every theme draws the same page from the same source. Every member is read-only (WVTH006); a writable cell
// travels as a `Func<Signal<T>>`.
//
// Contract 2.0 (settings rework phase 4c) removed the old page's members: the area (`Area`, `AreaCell`, `OpenArea`), the document tab (`Tab`,
// `TabCell`, `OpenTab`) and the structure's grouping (`Grouping`, `Group`, `OpenGroup`). A theme reads the category members only.

using System.Collections.Immutable;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.SettingsPage"/>. The host builds a new record on every render it makes, and a record that differs from the
/// last one re-runs the template: <see cref="Revision"/> is how a bump of the page epoch (a writer that asks the page to re-read) reaches it.</summary>
public sealed record SettingsPageProps
{
    /// <summary>The page epoch plus the two foreign epochs the Now playing and full-screen dials read (the player bar's and the fullscreen stage's). It only grows.</summary>
    public required int Revision { get; init; }

    /// <summary>The route names an open section (its arg is the category's slug). A theme with a sections-then-section page (Zune's Compact) shows that
    /// section's page; a theme with a strip ignores it (the category is <see cref="Category"/> either way).</summary>
    public bool OpenSection { get; init; }

    /// <summary>Close the open section: back to the sections list (Back when the list is the entry below, else the page swapped in place).</summary>
    public Action CloseSection { get; init; } = static () => { };

    /// <summary>The Storage census while the Storage category is open, else <see cref="SettingsStorageProps.Idle"/>.</summary>
    public required SettingsStorageProps Storage { get; init; }

    // ── the registry's page (contract 1.116, settings rework phase 4: `SettingsPageModel.cs` beside this file) ────────────────────────

    /// <summary>The open category: an index into <see cref="Categories"/> (0 = the Settings home). Settings opens on its home unless a link
    /// names a category or a dial; a link, a door (<see cref="OpenCategory"/>) and a strip (<see cref="CategoryCell"/>) move it.</summary>
    public int Category { get; init; }

    /// <summary>The ten entries in order: the Settings home, then the nine categories, each with its full title and its short strip name.</summary>
    public ImmutableArray<SettingsCategoryEntry> Categories { get; init; } = [];

    /// <summary>The category cell (root-lifetime): a strip binds it and switches in place, with no history step.</summary>
    public Func<Signal<int>> CategoryCell { get; init; } = Settings.CategoryCell;

    /// <summary>Open a category by its slug as a place (a drill-in list, a door): a history step from the home, the entry swapped from another
    /// category. <c>home</c> opens the Settings home.</summary>
    public Action<string> OpenCategory { get; init; } = static slug => Settings.OpenCategory(slug);

    /// <summary>The arrival a link asked for (design 4.9): the dial, its <see cref="SettingsArrival.Seq"/>, the expanders to open and the
    /// highlight's duration; <see cref="SettingsArrival.None"/> when no link named a dial. The page has opened its category already.</summary>
    public SettingsArrival Focus { get; init; } = SettingsArrival.None;

    /// <summary>The page's search box (design 4.8): its query cell, its ranked results over the model's own buffers, the no-match state, its
    /// keyboard and its focus. A theme binds its box with <c>Search.Box(frame)</c>; it draws the results while <c>Search.Active</c>.</summary>
    public SettingsSearchModel Search { get; init; } = SettingsSearchModel.Current;

    /// <summary>The Settings home (design 3.3): the six facts at a glance, the top three suggestions, the five recent changes (<c>Home.Read()</c>).</summary>
    public SettingsHomeSource Home { get; init; } = SettingsHomeSource.Shared;

    /// <summary>A category's body and a dial's row, composed from the registry (<c>Body.Of(Category)</c>, <c>Body.Row(dial)</c>): a theme draws
    /// these and never rebuilds a category itself.</summary>
    public SettingsBodySource Body { get; init; } = SettingsBodySource.Shared;
}

/// <summary>The Storage category's census as the host holds it (written off the UI thread, posted back, never read from the disk by a view).</summary>
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
    /// <summary>What a category other than Storage carries: nothing is read, nothing subscribes.</summary>
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
