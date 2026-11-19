// ── Screens/SettingsSpec.cs — the settings registry: category › group › dial, each dial with its ONE writer ─────────────────
//
// Role: CORE (settings rework phase 1, handoff/settings-2026-11-18/DESIGN.md 3.7). ONE table decides where a setting lives. It
// replaced the document tabs and sections, the page areas and the structures' grouping of them into tabs, the glyph catalogue
// (`Settings.Catalog`) and the developer gate; every theme draws the registry (`SettingsPageModel.cs`).
//
// A DIAL is today's Settings row with a stable address of its own: `SettingsDial { Id, LegacyIds, Parent, Glyph, Tier, Contexts,
// Aliases, ChoiceKeys, Kind, TitleKey, Caption, Value, Writer, Enabled, Visible, Confirm, SilentWhenDisabled }`:
//   · `Id`        stable and unique app-wide ("playback.crossfade"); `LegacyIds` the old row ids ("crossfade"), so an inspector line or
//                 an old link keeps resolving (`SettingsSpec.Aliases.cs`);
//   · `Parent`    the dial whose expander it opens with (a child row), or null;
//   · `Glyph`     the glyph NAME a theme resolves to `Icons.*` (was `Settings.Catalog`); null where the row paints none;
//   · `Tier`      Common · Advanced · Developer (composed away outside Developer mode);
//   · `Contexts`  the surfaces outside Settings that draw the same dial in place today (`SettingsSurfaces`): "Also in" and the hints;
//   · `Aliases`, `ChoiceKeys`  what the search indexes beside the title, caption and the authored `settings.terms.<id>` (`SettingsSearch.cs`):
//                 invariant words (brand and format names) and the loc keys of a choice dial's choices;
//   · `Kind`      what a view draws for it (toggle, choice, segmented, slider, picker, action, fact, composite);
//   · `Value`     reads the CURRENT value from a settings store (bool / int / float / string), no UI mirror;
//   · `Writer`    the ONE verb that changes it, returning whether the page must re-read (the views' `Bump`). A read-only dial says so
//                 (`SettingsWriter.ReadOnly`), so "every dial has a writer" is a checkable statement;
//   · `Enabled`   enabled or greyed, and the REASON (a loc key) a greyed dial gives; `SilentWhenDisabled` marks one greyed with none;
//   · `Visible`   composed at all (developer-only dials are ABSENT, never greyed);
//   · `Explainer` the explanation clip of a dial whose effect is seen or heard (design 6.5), set from `SettingsExplain`'s badge list.
//
// EVERY WRITE IS SEEN: a value writer is wrapped when it is built, so a write from any surface reports (dial id, source, time) to
// `SettingsWrites` (Recently changed, retire-on-use; settings rework phase 3). `WriteToggle(on, surface)` and friends write from a
// place that is not Settings, with that place as the source.
//
// A theme's options are dials too: the "‹Theme› options" group lists the ACTIVE theme's generated options
// (`appearance.option.<qualifiedId>`, `ThemeOptionsSection`) and the legacy rows its structure lists (Classic's sidebar actions).
//
// File layout: this file is the model, the builders and the lookups; `SettingsSpec.<Category>.cs` hold each category's dials and the
// verbs they call (`SettingsVerbs`); `SettingsSpec.Aliases.cs` the slug aliases and the id resolution.

namespace Wavee;

/// <summary>What a view draws for a dial.</summary>
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
    /// <summary>A card of several controls that is one dial (it owns its own writers, each listed as its own dial or verb).</summary>
    Composite,
}

/// <summary>Who a dial is for.</summary>
public enum SettingsTier : byte
{
    /// <summary>Everyone.</summary>
    Common,
    /// <summary>Diagnostics and tools: shown, ranked after the common dials.</summary>
    Advanced,
    /// <summary>Composed away outside Developer mode (absent, never greyed).</summary>
    Developer,
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

/// <summary>The verb that changes a dial. Exactly one delegate is set, matching the dial's <see cref="SettingsRowKind"/>; each
/// returns whether the page must re-read its rows afterwards.
/// <para>THE ONE WRITE PATH (settings rework phase 3): a value writer (<see cref="Toggle"/>, <see cref="Choose"/>, <see cref="Slide"/>)
/// is wrapped once, when it is built, so every call — a Settings view, a panel, a hint, the inspector — reports the write to
/// <see cref="SettingsWrites"/> (Recently changed, retire-on-use) under the dial it belongs to (<see cref="DialId"/>, bound by the dial).
/// A press (<see cref="Run"/>) is a verb, not a value, and records nothing.</para></summary>
public sealed class SettingsWriter
{
    SettingsWriter(string verb) => Verb = verb;

    /// <summary>The verb's stable name ("tray.iconMode"): what a log or the inspector calls it.</summary>
    public string Verb { get; }

    /// <summary>The dial this writer belongs to (bound once, by <see cref="SettingsDial"/>), or null for a writer no dial holds yet.</summary>
    public string? DialId { get; private set; }

    /// <summary>Bind the writer to its dial: the first dial that holds it names its writes. A shared writer (<see cref="ReadOnly"/>) writes nothing.</summary>
    internal void Bind(string dialId) => DialId ??= dialId;

    /// <summary>On/off.</summary>
    public Func<bool, bool>? Toggle { get; private init; }

    /// <summary>The index picked.</summary>
    public Func<int, bool>? Choose { get; private init; }

    /// <summary>The number set.</summary>
    public Func<float, bool>? Slide { get; private init; }

    /// <summary>A button pressed.</summary>
    public Func<bool>? Run { get; private init; }

    /// <summary>A composite dial whose per-item verbs belong to the module that owns the card (named here).</summary>
    public bool IsOwned { get; private init; }

    /// <summary>A read-only dial has no writer.</summary>
    public bool IsReadOnly => !IsOwned && Toggle is null && Choose is null && Slide is null && Run is null;

    public static readonly SettingsWriter ReadOnly = new("readonly");

    /// <summary>The dial's verbs are the owning module's (<paramref name="owner"/> names them): a card with its own state machine.</summary>
    public static SettingsWriter Owned(string owner) => new(owner) { IsOwned = true };

    // Each value writer is wrapped ONCE here (one closure per writer, at registry build): a call allocates nothing beyond what the
    // writer itself does, and the record is a ring slot update (`SettingsWrites.Recorded`).
    public static SettingsWriter OfToggle(string verb, Func<bool, bool> write)
    {
        SettingsWriter w = null!;
        w = new(verb) { Toggle = on => SettingsWrites.Recorded(w, write(on)) };
        return w;
    }

    public static SettingsWriter OfChoice(string verb, Func<int, bool> write)
    {
        SettingsWriter w = null!;
        w = new(verb) { Choose = i => SettingsWrites.Recorded(w, write(i)) };
        return w;
    }

    public static SettingsWriter OfSlider(string verb, Func<float, bool> write)
    {
        SettingsWriter w = null!;
        w = new(verb) { Slide = v => SettingsWrites.Recorded(w, write(v)) };
        return w;
    }

    public static SettingsWriter OfAction(string verb, Func<bool> run) => new(verb) { Run = run };
}

/// <summary>One setting: its address in the registry, its words and its one writer (see the file header).</summary>
public sealed class SettingsDial
{
    readonly string _id = "";
    readonly SettingsWriter _writer = SettingsWriter.ReadOnly;

    /// <summary>Stable and unique app-wide ("playback.crossfade"); a theme option's is <c>appearance.option.&lt;qualifiedId&gt;</c>.</summary>
    public required string Id
    {
        get => _id;
        init
        {
            _id = value;
            // Whichever of Id and Writer is set second binds the writer to its dial (the one write path); the shared read-only
            // writer belongs to no dial.
            if (!ReferenceEquals(_writer, SettingsWriter.ReadOnly)) _writer.Bind(value);
        }
    }

    /// <summary>The old row ids this dial answers to (the inspector's <c>setting id=</c>, old links); the first is its row id on its old tab.</summary>
    public IReadOnlyList<string> LegacyIds { get; init; } = [];

    /// <summary>The dial whose expander this one opens with, or null.</summary>
    public string? Parent { get; init; }

    /// <summary>The glyph NAME a theme resolves to <c>Icons.*</c>, or null where the row paints none.</summary>
    public string? Glyph { get; init; }

    public SettingsTier Tier { get; init; }

    /// <summary>The loc key of the dial's search terms (<c>settings.terms.&lt;id&gt;</c>, design 4.2).</summary>
    public string TermsKey => "settings.terms." + Id;

    /// <summary>Invariant search words (never translated) beside the authored terms.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The loc keys of the choices a choice, segmented or picker dial offers, in order (what the search indexes as choice labels,
    /// design 4.2); empty where the choices are not words (numbers, adapters, devices, hours).</summary>
    public IReadOnlyList<string> ChoiceKeys { get; init; } = [];

    /// <summary>The choices' words where they are live names rather than loc keys (the output device: the system default, then this PC's endpoints),
    /// read when a body row is built; null for a dial whose words are its <see cref="ChoiceKeys"/> (or that has none).</summary>
    public Func<IReadOnlyList<string>>? LiveChoices { get; init; }

    /// <summary>The surfaces outside Settings that draw this dial in place (<see cref="SettingsSurfaces"/>).</summary>
    public IReadOnlyList<string> Contexts { get; init; } = [];

    public required SettingsRowKind Kind { get; init; }

    /// <summary>The dial's title (a loc key).</summary>
    public required string TitleKey { get; init; }

    /// <summary>The dial's description (a loc key) for the given facts; null = none.</summary>
    public Func<ISettingsFacts, string?> Caption { get; init; } = static _ => null;

    /// <summary>The current value, read from a settings store: a boxed bool, int, float or string; null for a dial with no value.</summary>
    public Func<IAppSettings, object?> Value { get; init; } = static _ => null;

    /// <summary>The dial's value read NOW from the live settings store: what a view seeds a control from (a theme never reaches the
    /// store itself, WVTH002). Null for a dial with no value.</summary>
    public object? Current => Value(Platform.Settings);

    /// <summary>The ONE verb that changes the dial; its value writers report every write under <see cref="Id"/> (<see cref="SettingsWrites"/>).</summary>
    public required SettingsWriter Writer
    {
        get => _writer;
        init
        {
            _writer = value;
            if (_id.Length > 0 && !ReferenceEquals(value, SettingsWriter.ReadOnly)) value.Bind(_id);
        }
    }

    /// <summary>The explanation clip's id (design 6.5: a dial whose effect is something you see or hear), or null. The badge shows only
    /// where the clip exists for the active theme and app mode (<see cref="SettingsExplain"/>); its caption is
    /// <c>settings.explain.&lt;Id&gt;</c>.</summary>
    public string? Explainer { get; init; }

    /// <summary>The themes the explanation applies to (theme ids), or null for every theme that shows the dial. A theme option's
    /// explanation applies only to its own theme.</summary>
    public IReadOnlyList<string>? ExplainerThemes { get; init; }

    // ── writes from a place that is not Settings (a panel, a hint, the inspector): the same writer, with its source ─────────────

    /// <summary>Write a toggle dial from <paramref name="surface"/> (Recently changed says "from" it). Returns whether the page re-reads.</summary>
    public bool WriteToggle(bool on, string surface)
    {
        using var _ = SettingsWrites.From(surface);
        return _writer.Toggle is { } toggle && toggle(on);
    }

    /// <summary>Write a choice dial (an index) from <paramref name="surface"/>.</summary>
    public bool WriteChoice(int index, string surface)
    {
        using var _ = SettingsWrites.From(surface);
        return _writer.Choose is { } choose && choose(index);
    }

    /// <summary>Write a slider dial from <paramref name="surface"/>.</summary>
    public bool WriteSlider(float value, string surface)
    {
        using var _ = SettingsWrites.From(surface);
        return _writer.Slide is { } slide && slide(value);
    }

    /// <summary>Press an action dial from <paramref name="surface"/>.</summary>
    public bool Press(string surface)
    {
        using var _ = SettingsWrites.From(surface);
        return _writer.Run is { } run && run();
    }

    public Func<ISettingsFacts, SettingsEnablement> Enabled { get; init; } = static _ => SettingsEnablement.On;

    public Func<ISettingsFacts, bool> Visible { get; init; } = static _ => true;

    /// <summary>The confirmation the writer asks first, or null.</summary>
    public SettingsConfirm? Confirm { get; init; }

    /// <summary>True when the dial greys WITHOUT a reason of its own (its description stays the same): the developer items while
    /// developer mode is off, the equalizer controls while the equalizer is off. Every other greyed dial gives a reason.</summary>
    public bool SilentWhenDisabled { get; init; }

    /// <summary>The description a view prints: the dial's reason while it is greyed with one, else its caption.</summary>
    public string? DescriptionKey(ISettingsFacts facts)
    {
        var enablement = Enabled(facts);
        return !enablement.Enabled && enablement.ReasonKey is { } reason ? reason : Caption(facts);
    }
}

/// <summary>A group of a category: a heading over its dials, in order.</summary>
/// <param name="Id">Unique within its category ("equalizer").</param>
/// <param name="TitleKey">The heading (a loc key).</param>
/// <param name="SubtitleKey">The caption under the heading (a loc key), or null.</param>
/// <param name="Dials">Its dials, in order (a child right after its parent).</param>
public sealed record SettingsGroup(string Id, string TitleKey, string? SubtitleKey, IReadOnlyList<SettingsDial> Dials)
{
    /// <summary>The dials the group draws from the live state after <see cref="Dials"/> (the active theme's generated options), or null.</summary>
    public Func<IReadOnlyList<SettingsDial>>? Live { get; init; }
}

/// <summary>A category of the Settings page (design 3.2), in list order.</summary>
/// <param name="Id">The category's id, which is also its slug (the deep-link name).</param>
/// <param name="TitleKey">The page title (a loc key).</param>
/// <param name="StripKey">The strip's short word (a loc key; owner decision 1).</param>
/// <param name="SubtitleKey">What the category is for (a loc key).</param>
/// <param name="Glyph">The glyph NAME.</param>
/// <param name="Groups">Its groups, in order.</param>
public sealed record SettingsCategory(string Id, string TitleKey, string StripKey, string SubtitleKey, string Glyph, IReadOnlyList<SettingsGroup> Groups)
{
    /// <summary>The deep-link name.</summary>
    public string Slug => Id;
}

/// <summary>The surfaces outside Settings that draw a dial in place today (design 5.4): a dial's <see cref="SettingsDial.Contexts"/>, and
/// the source a write records for Recently changed (<see cref="SettingsWrites"/>).</summary>
public static class SettingsSurfaces
{
    public const string StageOptions = "stage.options", StageGallery = "stage.gallery", QueuePanel = "queue.panel", PlayerBar = "playerBar",
        DevicePicker = "devicePicker", MiniPlayerOptions = "miniPlayer.options", LikedCover = "liked.cover", TrackListMenu = "trackList.menu",
        NowPlayingPicker = "nowPlaying.picker", WhatsNew = "whatsNew.dialog", CrashPrompt = "crash.prompt", LogsMenu = "logs.menu",
        TrayMenu = "tray.menu", VideoMenu = "video.menu", SidebarMenu = "sidebar.menu";

    /// <summary>Phase 5's homes (design 5.4): the lyrics pane's ⋯, the profile menu, and WinUI's title bar menu (a theme option names it in its
    /// own <c>ThemeOption.Contexts</c>).</summary>
    public const string LyricsPane = "lyrics.pane", ProfileMenu = "profile.menu", TitleBarMenu = "titleBar.menu";

    /// <summary>The full screen's Up next column (its Autoplay and Crossfade switches, design 5.4's "Queue / Up next").</summary>
    public const string StageUpNext = "stage.upNext";

    /// <summary>Home's places (design 5.3, 5.4): the Spine Shelf's and the Release Spotlight's preview sound button, and the daylist tile's
    /// options (its Clock and "Tell me when it refreshes" under "For every daylist").</summary>
    public const string HomeShelf = "home.shelf", HomeDaylist = "home.daylist";

    /// <summary>The setup wizard's Terms page (its crash-consent card writes the crash-reports dial, owner decision 10): a source, not a context.</summary>
    public const string SetupWizard = "setup.wizard";

    /// <summary>Sources of a write that are not a dial's in-context home: the Settings page (the default), a hint's inline action, the
    /// command palette, the inspector's <c>setting</c> verb.</summary>
    public const string Settings = "settings", Hint = "hint", Palette = "palette", Inspector = "inspector";
}

/// <summary>The registry (see the file header).</summary>
public static partial class SettingsSpec
{
    static IReadOnlyList<SettingsCategory>? s_categories;

    /// <summary>The ten categories, in list order: the home, then the nine of owner decision 1. Built once.</summary>
    public static IReadOnlyList<SettingsCategory> Categories => s_categories ??=
    [
        HomeCategory(), AppearanceCategory(), SoundCategory(), PlaybackCategory(), LyricsCategory(), NotificationsCategory(),
        SystemCategory(), StorageCategory(), AccountCategory(), HelpCategory(),
    ];

    /// <summary>Every dial the registry declares, in list order (the live theme options are not among them: <see cref="LiveDials"/>).</summary>
    public static IEnumerable<SettingsDial> All
    {
        get
        {
            foreach (var category in Categories)
                foreach (var group in category.Groups)
                    foreach (var dial in group.Dials)
                        yield return dial;
        }
    }

    /// <summary>The dials the groups add from the live state right now (the active theme's generated options), in list order.</summary>
    public static IEnumerable<(SettingsCategory Category, SettingsGroup Group, SettingsDial Dial)> LiveDials
    {
        get
        {
            foreach (var category in Categories)
                foreach (var group in category.Groups)
                    if (group.Live is { } live)
                        foreach (var dial in live())
                            yield return (category, group, dial);
        }
    }

    /// <summary>The dial <paramref name="id"/> (its registry id). An unknown id throws: a typo must fail loudly, never drop a dial.</summary>
    public static SettingsDial Dial(string id)
        => TryDial(id, out var dial) ? dial : throw new InvalidOperationException("SettingsSpec: no dial '" + id + "'.");

    /// <summary>The dial <paramref name="id"/> (its registry id), or false.</summary>
    public static bool TryDial(string id, out SettingsDial dial)
    {
        s_byId ??= IndexById();
        return s_byId.TryGetValue(id, out dial!);
    }

    /// <summary>The category and group that hold <paramref name="dial"/>, or false for a dial the registry does not declare.</summary>
    public static bool TryPlace(SettingsDial dial, out SettingsCategory category, out SettingsGroup group)
    {
        foreach (var c in Categories)
            foreach (var g in c.Groups)
            {
                foreach (var d in g.Dials)
                    if (ReferenceEquals(d, dial)) { category = c; group = g; return true; }
                if (g.Live is not null && dial.Id.StartsWith(ThemeOptionPrefix, StringComparison.Ordinal)) { category = c; group = g; return true; }
            }
        category = null!;
        group = null!;
        return false;
    }

    static Dictionary<string, SettingsDial>? s_byId;

    static Dictionary<string, SettingsDial> IndexById()
    {
        var index = new Dictionary<string, SettingsDial>(StringComparer.Ordinal);
        foreach (var dial in All)
            if (!index.TryAdd(dial.Id, dial))
                throw new InvalidOperationException("SettingsSpec: the dial id '" + dial.Id + "' is declared twice.");
        SettingsExplain.Validate(index);
        return index;
    }

    // ── builders (the category files use these) ─────────────────────────────────────────────────────────────────────

    static readonly Func<ISettingsFacts, string?> NoCaption = static _ => null;
    static readonly Func<IAppSettings, object?> NoValue = static _ => null;
    static readonly Func<ISettingsFacts, SettingsEnablement> AlwaysEnabled = static _ => SettingsEnablement.On;
    static readonly Func<ISettingsFacts, bool> AlwaysVisible = static _ => true;
    static readonly Func<ISettingsFacts, bool> DeveloperModeOn = static f => f.DeveloperMode;

    /// <summary>A dial. <paramref name="legacy"/> is its old row id (and any other it answers to); a <see cref="SettingsTier.Developer"/>
    /// dial is visible only in developer mode, on top of its own <paramref name="visible"/>.</summary>
    static SettingsDial Make(string id, SettingsRowKind kind, string titleKey, SettingsWriter writer,
        string[]? legacy = null, string? parent = null, string? glyph = null, SettingsTier tier = SettingsTier.Common,
        string? captionKey = null, Func<ISettingsFacts, string?>? caption = null, Func<IAppSettings, object?>? value = null,
        Func<ISettingsFacts, SettingsEnablement>? enabled = null, Func<ISettingsFacts, bool>? visible = null,
        SettingsConfirm? confirm = null, bool silent = false, string[]? contexts = null, string[]? aliases = null,
        IReadOnlyList<string>? choices = null, Func<IReadOnlyList<string>>? liveChoices = null)
    {
        SettingsExplain.Clip? explain = SettingsExplain.Of(id);   // the badge list (design 6.5) decides which dials explain themselves
        Func<ISettingsFacts, string?> cap = caption ?? NoCaption;
        if (caption is null && captionKey is not null) cap = _ => captionKey;
        Func<ISettingsFacts, bool> vis = visible ?? AlwaysVisible;
        if (tier == SettingsTier.Developer)
            vis = visible is null ? DeveloperModeOn : f => f.DeveloperMode && visible(f);
        return new SettingsDial
        {
            Id = id, LegacyIds = legacy ?? [], Parent = parent, Glyph = glyph, Tier = tier,
            Kind = kind, TitleKey = titleKey, Writer = writer,
            Caption = cap,
            Value = value ?? NoValue,
            Enabled = enabled ?? AlwaysEnabled,
            Visible = vis,
            Confirm = confirm,
            SilentWhenDisabled = silent,
            Contexts = contexts ?? [],
            Aliases = aliases ?? [],
            ChoiceKeys = choices ?? [],
            LiveChoices = liveChoices,
            Explainer = explain?.Id,
            ExplainerThemes = explain?.Themes,
        };
    }

    static SettingsGroup Group(string id, string titleKey, string? subtitleKey, params SettingsDial[] dials) => new(id, titleKey, subtitleKey, dials);

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
