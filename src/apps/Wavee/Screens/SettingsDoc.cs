// ── Screens/SettingsDoc.cs — the Settings page as DATA: tabs → sections → rows, each with its writer ───────────────
//
// Role: CORE (themes plan §5.1, PR C13; extraction X34-X36, §8.1). The Settings views used to own, row by row, what the row
// is called, which glyph it wears, when it is greyed and why, and what writing it DOES (a store write, a DSP push, an OS
// registration, a toast). That knowledge is this document now; each theme's `Pages.Settings` view (Classic's
// `Themes\Classic\Structure\Views\Screens\ClassicSettings*.cs`) draws it. The decisions stay engine-free, so `SettingsDocTests`
// drive them over a fake `ISettingsFacts`.
//
// A ROW is `SettingsRow { Kind, Title, Caption, Value, Writer, Enabled (+ Reason), Visible, GlyphRole }`:
//   · `Kind`     what a view draws for it (toggle, choice, segmented, slider, picker, action, fact, composite);
//   · `Value`    reads the CURRENT value from a settings store (bool / int / float / string), no UI mirror;
//   · `Writer`   the ONE verb that changes it. It returns whether the page must re-read (the views' `Bump`); the view never
//                writes the store itself. A read-only row (a fact) says so explicitly (`SettingsWriter.ReadOnly`), so "every
//                row has a writer" is a checkable statement;
//   · `Enabled`  enabled or greyed, and the REASON (a loc key) a greyed row gives. A row greyed with no reason of its own
//                (the developer items, the equalizer controls while the equalizer is off) says so with `SilentWhenDisabled`;
//   · `Visible`  composed at all? (developer-only rows are ABSENT, never greyed; the rail sub-rows follow the page layout);
//   · `GlyphRole` the catalog glyph NAME (`Settings.Catalog`), resolved to `Icons.*` by the view; null for a row the catalog
//                does not list (Notifications and About paint their own).
//
// The Appearance rows still point at today's keys: each moves to a theme option in its M-PR (§2.3), re-binding ONE row here.
//
// File layout: this file is the model and the registry; the rows and their verbs are `SettingsDoc.Rows.<Tab>.cs`.

namespace Wavee;

/// <summary>What a view draws for a row.</summary>
public enum SettingsRowKind : byte
{
    /// <summary>An on/off switch (or a check box).</summary>
    Toggle,
    /// <summary>One value from a list, in a combo box.</summary>
    Choice,
    /// <summary>One value from a short list, as segments.</summary>
    Segmented,
    /// <summary>A number on a slider (or a number box).</summary>
    Slider,
    /// <summary>One value from a strip of picture cards.</summary>
    Picker,
    /// <summary>A button or a clickable card that does something.</summary>
    Action,
    /// <summary>Read-only information.</summary>
    Fact,
    /// <summary>A card of several controls that is one row in the catalog (it owns its own writers, each listed as its own row or verb).</summary>
    Composite,
}

/// <summary>Enabled, or greyed with the reason a view prints in the row's description.</summary>
public readonly record struct SettingsEnablement(bool Enabled, string? ReasonKey)
{
    /// <summary>Enabled.</summary>
    public static readonly SettingsEnablement On = new(true, null);

    /// <summary>Greyed because of <paramref name="reasonKey"/> (a loc key; null = no reason of its own).</summary>
    public static SettingsEnablement Off(string? reasonKey) => new(false, reasonKey);
}

/// <summary>A confirmation a destructive writer asks first (the views open the ONE confirm dialog, Cancel the default).</summary>
public sealed record SettingsConfirm(string TitleKey, string BodyKey, string PrimaryKey);

/// <summary>The verb that changes a row. Exactly one delegate is set, matching the row's <see cref="SettingsRowKind"/>; each
/// returns whether the page must re-read its rows afterwards.</summary>
public sealed class SettingsWriter
{
    SettingsWriter(string verb) => Verb = verb;

    /// <summary>The verb's stable name ("tray.iconMode"): what a log or a test calls it.</summary>
    public string Verb { get; }

    /// <summary>On/off.</summary>
    public Func<bool, bool>? Toggle { get; private init; }

    /// <summary>The index picked.</summary>
    public Func<int, bool>? Choose { get; private init; }

    /// <summary>The number set.</summary>
    public Func<float, bool>? Slide { get; private init; }

    /// <summary>A button pressed.</summary>
    public Func<bool>? Run { get; private init; }

    /// <summary>A composite row whose per-item verbs belong to the module that owns the card (named here).</summary>
    public bool IsOwned { get; private init; }

    /// <summary>A read-only row has no writer.</summary>
    public bool IsReadOnly => !IsOwned && Toggle is null && Choose is null && Slide is null && Run is null;

    public static readonly SettingsWriter ReadOnly = new("readonly");

    /// <summary>The row's verbs are the owning module's (<paramref name="owner"/> names them): a card with its own state machine.</summary>
    public static SettingsWriter Owned(string owner) => new(owner) { IsOwned = true };

    public static SettingsWriter OfToggle(string verb, Func<bool, bool> write) => new(verb) { Toggle = write };
    public static SettingsWriter OfChoice(string verb, Func<int, bool> write) => new(verb) { Choose = write };
    public static SettingsWriter OfSlider(string verb, Func<float, bool> write) => new(verb) { Slide = write };
    public static SettingsWriter OfAction(string verb, Func<bool> run) => new(verb) { Run = run };
}

/// <summary>One row of the document.</summary>
public sealed class SettingsRow
{
    public required Settings.Tab Tab { get; init; }

    /// <summary>The catalog section title (English; the key into <see cref="Settings.Catalog"/>).</summary>
    public required string Section { get; init; }

    /// <summary>Unique within the tab (the catalog's row id for the catalog tabs).</summary>
    public required string Id { get; init; }

    public required SettingsRowKind Kind { get; init; }

    /// <summary>The row's title (a loc key).</summary>
    public required string TitleKey { get; init; }

    /// <summary>The row's description (a loc key) for the given facts; null = none.</summary>
    public Func<ISettingsFacts, string?> Caption { get; init; } = static _ => null;

    /// <summary>The current value, read from a settings store: a boxed bool, int, float or string; null for a row with no value.</summary>
    public Func<IAppSettings, object?> Value { get; init; } = static _ => null;

    /// <summary>The row's value read NOW from the live settings store: what a view seeds a control from (a theme never reaches the
    /// store itself, WVTH002). Null for a row with no value.</summary>
    public object? Current => Value(Platform.Settings);

    public required SettingsWriter Writer { get; init; }

    public Func<ISettingsFacts, SettingsEnablement> Enabled { get; init; } = static _ => SettingsEnablement.On;

    public Func<ISettingsFacts, bool> Visible { get; init; } = static _ => true;

    /// <summary>The catalog glyph NAME, or null when the catalog does not list the row.</summary>
    public string? GlyphRole { get; init; }

    /// <summary>The confirmation the writer asks first, or null.</summary>
    public SettingsConfirm? Confirm { get; init; }

    /// <summary>True when the row greys WITHOUT a reason of its own (its description stays the same): the developer items
    /// while developer mode is off, the equalizer controls while the equalizer is off. Every other greyed row gives a reason.</summary>
    public bool SilentWhenDisabled { get; init; }

    /// <summary>The description a view prints: the row's reason while it is greyed with one, else its caption.</summary>
    public string? DescriptionKey(ISettingsFacts facts)
    {
        var enablement = Enabled(facts);
        return !enablement.Enabled && enablement.ReasonKey is { } reason ? reason : Caption(facts);
    }
}

/// <summary>A section of a tab.</summary>
/// <param name="Tab">Its tab.</param>
/// <param name="Section">The catalog title (the glyph key).</param>
/// <param name="TitleKey">The heading (a loc key).</param>
/// <param name="SubtitleKey">The caption under the heading (a loc key), or null.</param>
/// <param name="Rows">Its rows, in order.</param>
public sealed record SettingsSection(Settings.Tab Tab, string Section, string TitleKey, string? SubtitleKey, IReadOnlyList<SettingsRow> Rows);

/// <summary>A tab: its sections in order.</summary>
public sealed record SettingsTabDoc(Settings.Tab Tab, IReadOnlyList<SettingsSection> Sections);

/// <summary>The Settings document.</summary>
public static partial class SettingsDoc
{
    static IReadOnlyList<SettingsTabDoc>? s_tabs;

    /// <summary>All seven tabs, built once.</summary>
    public static IReadOnlyList<SettingsTabDoc> Tabs => s_tabs ??= Build();

    static IReadOnlyList<SettingsTabDoc> Build() =>
    [
        new(Settings.Tab.General, GeneralSections()),
        new(Settings.Tab.Appearance, AppearanceSections()),
        new(Settings.Tab.Playback, PlaybackSections()),
        new(Settings.Tab.Notifications, NotificationSections()),
        new(Settings.Tab.Storage, StorageSections()),
        new(Settings.Tab.PrivacyDiagnostics, PrivacySections()),
        new(Settings.Tab.About, AboutSections()),
    ];

    /// <summary>Every row of every tab, in document order.</summary>
    public static IEnumerable<SettingsRow> AllRows
    {
        get
        {
            foreach (var tab in Tabs)
                foreach (var section in tab.Sections)
                    foreach (var row in section.Rows)
                        yield return row;
        }
    }

    /// <summary>The row <paramref name="id"/> of <paramref name="tab"/>. An unknown id throws, exactly as
    /// <see cref="Settings.Catalog.RowGlyph"/> does — a typo must fail loudly, never silently drop a row.</summary>
    public static SettingsRow Row(Settings.Tab tab, string id)
    {
        s_index ??= IndexRows();
        if (s_index.TryGetValue((tab, id), out var row)) return row;
        throw new InvalidOperationException("SettingsDoc: no row '" + id + "' on tab " + tab + ".");
    }

    /// <summary>The row <paramref name="id"/> of <paramref name="tab"/>, or false when the document has none (a theme that lists a
    /// legacy row this build no longer has: the page leaves it out instead of failing).</summary>
    public static bool TryRow(Settings.Tab tab, string id, out SettingsRow row)
    {
        s_index ??= IndexRows();
        return s_index.TryGetValue((tab, id), out row!);
    }

    static Dictionary<(Settings.Tab, string), SettingsRow>? s_index;

    static Dictionary<(Settings.Tab, string), SettingsRow> IndexRows()
    {
        var index = new Dictionary<(Settings.Tab, string), SettingsRow>();
        foreach (var row in AllRows) index[(row.Tab, row.Id)] = row;
        return index;
    }

    /// <summary>The section <paramref name="section"/> (catalog title) of <paramref name="tab"/>.</summary>
    public static SettingsSection Section(Settings.Tab tab, string section)
    {
        foreach (var t in Tabs)
            if (t.Tab == tab)
                foreach (var s in t.Sections)
                    if (string.Equals(s.Section, section, StringComparison.Ordinal))
                        return s;
        throw new InvalidOperationException("SettingsDoc: no section '" + section + "' on tab " + tab + ".");
    }

    // ── builders (the rows files use these) ──────────────────────────────────────────────────────────────────────────

    static readonly Func<ISettingsFacts, string?> NoCaption = static _ => null;
    static readonly Func<IAppSettings, object?> NoValue = static _ => null;
    static readonly Func<ISettingsFacts, SettingsEnablement> AlwaysEnabled = static _ => SettingsEnablement.On;
    static readonly Func<ISettingsFacts, bool> AlwaysVisible = static _ => true;

    /// <summary>A row. For the catalog tabs (<paramref name="catalog"/>) its glyph is the catalog's for (tab, id) and a
    /// developer-only row is visible only in developer mode; Notifications and About rows are not in the catalog.</summary>
    static SettingsRow Make(Settings.Tab tab, string section, string id, SettingsRowKind kind, string titleKey,
        SettingsWriter writer, string? captionKey = null, Func<ISettingsFacts, string?>? caption = null,
        Func<IAppSettings, object?>? value = null, Func<ISettingsFacts, SettingsEnablement>? enabled = null,
        Func<ISettingsFacts, bool>? visible = null, SettingsConfirm? confirm = null, bool silent = false, bool catalog = true)
    {
        Func<ISettingsFacts, string?> cap = caption ?? NoCaption;
        if (caption is null && captionKey is not null) cap = _ => captionKey;
        Func<ISettingsFacts, bool> vis = visible ?? AlwaysVisible;
        if (visible is null && catalog) vis = f => Settings.Catalog.RowVisible(tab, id, f.DeveloperMode);
        return new SettingsRow
        {
            Tab = tab, Section = section, Id = id, Kind = kind, TitleKey = titleKey, Writer = writer,
            Caption = cap,
            Value = value ?? NoValue,
            Enabled = enabled ?? AlwaysEnabled,
            Visible = vis,
            GlyphRole = catalog ? Settings.Catalog.RowGlyph(tab, id) : null,
            Confirm = confirm,
            SilentWhenDisabled = silent,
        };
    }

    static SettingsSection Sect(Settings.Tab tab, string section, string titleKey, string? subtitleKey, params SettingsRow[] rows)
        => new(tab, section, titleKey, subtitleKey, rows);

    static Func<IAppSettings, object?> BoolOf(SettingKey<bool> key) => s => s.Get(key);
    static Func<IAppSettings, object?> IntOf(SettingKey<int> key) => s => s.Get(key);

    /// <summary>Write a bool key, then <paramref name="after"/>; the page re-reads.</summary>
    static Func<bool, bool> WriteBool(SettingKey<bool> key, Action<bool>? after = null) => on =>
    {
        Platform.Settings.Set(key, on);
        after?.Invoke(on);
        return true;
    };
}
