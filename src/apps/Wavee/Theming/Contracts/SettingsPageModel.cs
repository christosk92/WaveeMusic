// ── Theming/Contracts/SettingsPageModel.cs — what the registry's Settings page draws (contract 1.116, settings rework phase 4) ─────────
//
// The page model of handoff/settings-2026-11-18/DESIGN.md 3.2-3.3 (categories, the Settings home), 4.8-4.9 (the box, its results, the
// keyboard, the arrival), 5.1-5.2 ("Also in"), 6.5 (explanation badges) and 7 (full title and short strip name). The STATE and every
// decision stay core (`Screens/SettingsPageModel.cs`, `Screens/SettingsSearchModel.cs`): which category is open, what a link names, which
// dials are composed, what each says, what the search ranks, what the home shows. A theme draws these records in its own pattern and
// calls the verbs they carry; it never rebuilds a category from the registry and never reaches the store (WVTH002).
//
// KEYS the themes give their nodes (`SettingsKeys`), so a link's arrival, the inspector and `lint` find the same node in every theme:
//   · `settings:search`            the page's search box (`SettingsSearchModel.Box` keys it for the theme);
//   · `settings:cat:<slug>`        a category's strip item or list word (Classic, WinUI);
//   · `settings:dial:<dialId>`     a dial's row in a category body (Classic, WinUI);
//   · `settings:result:<dialId>`   a search result (a pointer's id for a pointer result);
//   · `settings:explain:<dialId>`  a dial's explanation badge;
//   · Zune keeps its own families: `zs:link:<slug>` (a section word) and `zs:row:<dialId>` (a row).

using System.Collections.Immutable;

namespace Wavee.Theming.Contracts;

/// <summary>The node keys of the registry's Settings page (see the file header). Each builder allocates its string: call it where the
/// element is built, never per frame.</summary>
public static class SettingsKeys
{
    /// <summary>The page's search box.</summary>
    public const string Search = "settings:search";

    /// <summary>A category's strip item or list word (Classic, WinUI).</summary>
    public static string Category(string slug) => "settings:cat:" + slug;

    /// <summary>A dial's row in a category body (Classic, WinUI).</summary>
    public static string Dial(string dialId) => "settings:dial:" + dialId;

    /// <summary>A search result (a dial's id, or a pointer's).</summary>
    public static string Result(string id) => "settings:result:" + id;

    /// <summary>A dial's explanation badge.</summary>
    public static string Explain(string dialId) => "settings:explain:" + dialId;

    /// <summary>Zune's section word (its own family).</summary>
    public static string ZuneSection(string slug) => "zs:link:" + slug;

    /// <summary>Zune's row of a dial (its own family).</summary>
    public static string ZuneRow(string dialId) => "zs:row:" + dialId;
}

/// <summary>One entry of the category strip or list: the Settings home, then the nine categories of owner decision 1, in order.</summary>
/// <param name="Index">Its place in the list (0 = the Settings home): what <see cref="SettingsPageProps.Category"/> and the category cell hold.</param>
/// <param name="Slug">The deep-link name (<c>home</c>, <c>appearance</c>, …): <see cref="SettingsPageProps.OpenCategory"/>'s argument.</param>
/// <param name="Title">The full title (a page heading, Zune's list word): "Lyrics &amp; Now playing".</param>
/// <param name="Strip">The short strip name (design 7, <c>ShortKey</c>): "Lyrics".</param>
/// <param name="Subtitle">What the category is for: "The lyrics, Now playing, full screen and the mini player".</param>
/// <param name="Glyph">The glyph NAME a theme resolves to <c>Icons.*</c>.</param>
public sealed record SettingsCategoryEntry(int Index, string Slug, string Title, string Strip, string Subtitle, string Glyph);

/// <summary>A link's arrival at one dial (design 4.9): open its category, open its parent's expander, bring its row into view and
/// highlight it for <see cref="HighlightMs"/>, each in the theme's own motion. A new <see cref="Seq"/> replays the arrival (the same dial
/// asked twice). Reduced motion keeps every step (a cut instead of the glide, a hold instead of the fade).</summary>
public sealed record SettingsArrival
{
    /// <summary>The dial (a registry id; a theme option's <c>appearance.option.&lt;qualifiedId&gt;</c>), or empty for none.</summary>
    public required string DialId { get; init; }

    /// <summary>Grows with every link to a dial; 0 = no arrival.</summary>
    public required int Seq { get; init; }

    /// <summary>The slug of the dial's category (the page has opened it already: <see cref="SettingsPageProps.Category"/>).</summary>
    public string Category { get; init; } = "";

    /// <summary>The id of the dial's group in its category, or null.</summary>
    public string? Group { get; init; }

    /// <summary>The dial whose expander opens for it (its parent), or null for a top-level row.</summary>
    public string? Parent { get; init; }

    /// <summary>Every expander that opens for it, outermost first (its parent's parent, …, its parent); empty for a top-level row.</summary>
    public ImmutableArray<string> Expand { get; init; } = [];

    /// <summary>How long the highlight holds and fades (the structure's <c>SettingsPagePolicy.HighlightMs</c>, from its Rules).</summary>
    public int HighlightMs { get; init; }

    /// <summary>No arrival.</summary>
    public static SettingsArrival None { get; } = new() { DialId = "", Seq = 0 };

    /// <summary>True when no link asked for a dial.</summary>
    public bool IsNone => Seq == 0 || DialId.Length == 0;
}

/// <summary>One category's body as the registry composes it right now (design 3.2): its groups in order, each with the dials that exist
/// under the live facts (a developer dial outside Developer mode, another theme's option, a dial the facts compose away: absent).</summary>
/// <param name="Category">The category.</param>
/// <param name="Groups">Its groups, in order; a group with no composed dial is left out. The Settings home has none (its parts are
/// <see cref="SettingsPageProps.Home"/>).</param>
public sealed record SettingsBody(SettingsCategoryEntry Category, ImmutableArray<SettingsBodyGroup> Groups);

/// <summary>A group of a category body: its heading and its top-level rows (a child row rides its parent's <see cref="SettingsBodyRow.Children"/>).</summary>
/// <param name="Id">Unique within its category (<c>betweenSongs</c>).</param>
/// <param name="Title">The heading (resolved; the theme options group reads "WinUI options").</param>
/// <param name="Subtitle">The caption under the heading, or null.</param>
/// <param name="Rows">Its top-level rows, in order.</param>
public sealed record SettingsBodyGroup(string Id, string Title, string? Subtitle, ImmutableArray<SettingsBodyRow> Rows);

/// <summary>One dial as a row draws it: its words resolved, its value, its enablement and reason, its children (the parent's expander), its
/// explanation badge and its in-context homes. Built when the page renders (the page epoch moves on a write), never per frame. Write through
/// <see cref="Writer"/> (the Settings page is the source) or <see cref="Dial"/>'s <c>Write*</c> with another surface.</summary>
public sealed record SettingsBodyRow
{
    /// <summary>The registry dial (its live value is <c>Dial.Current</c>).</summary>
    public required SettingsDial Dial { get; init; }

    /// <summary>The dial's id (<c>playback.crossfade</c>): the row's key is <see cref="SettingsKeys.Dial"/> of it.</summary>
    public string Id => Dial.Id;

    /// <summary>What the row draws (toggle, choice, segmented, slider, picker, action, fact, composite).</summary>
    public SettingsRowKind Kind => Dial.Kind;

    /// <summary>The ONE verb that changes it (null delegates for a read-only dial: <c>Writer.IsReadOnly</c>).</summary>
    public SettingsWriter Writer => Dial.Writer;

    /// <summary>The confirmation a destructive writer asks first, or null.</summary>
    public SettingsConfirm? Confirm => Dial.Confirm;

    /// <summary>Common · Advanced · Developer.</summary>
    public SettingsTier Tier => Dial.Tier;

    /// <summary>The label (resolved).</summary>
    public required string Label { get; init; }

    /// <summary>The caption (resolved), or null.</summary>
    public string? Caption { get; init; }

    /// <summary>What the row prints under its label: <see cref="Reason"/> while it is greyed with one, else <see cref="Caption"/>.</summary>
    public string? Description { get; init; }

    /// <summary>A theme option's own group ("Window", "Track lists"), for a sub-heading inside the options group; else null.</summary>
    public string? Subgroup { get; init; }

    /// <summary>The glyph NAME a theme resolves to <c>Icons.*</c>, or null.</summary>
    public string? Glyph { get; init; }

    /// <summary>The value when the row was built: a boxed bool (toggle), int (choice, segmented, picker index), float (slider) or string;
    /// null for a dial with none.</summary>
    public object? Value { get; init; }

    /// <summary>The choices' words (resolved), in order; empty where the choices are not words (numbers, devices, adapters).</summary>
    public ImmutableArray<string> Choices { get; init; } = [];

    /// <summary>Enabled, or greyed.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Why it is greyed (resolved), or null (enabled, or greyed with no reason of its own).</summary>
    public string? Reason { get; init; }

    /// <summary>It greys WITHOUT a reason of its own (the developer items, the equalizer's controls while it is off).</summary>
    public bool Silent { get; init; }

    /// <summary>The rows its expander holds (its children, composed), in order; empty for a row with no expander.</summary>
    public ImmutableArray<SettingsBodyRow> Children { get; init; } = [];

    /// <summary>The explanation badge (design 6.5), or null for a dial that has none under the active theme.</summary>
    public SettingsExplainInfo? Explain { get; init; }

    /// <summary>The places outside Settings that draw the same dial ("Also in", design 5.1), in order; empty for none.</summary>
    public ImmutableArray<SettingsAlsoIn> AlsoIn { get; init; } = [];

    /// <summary>The "Also in: Queue · Full screen › Options" line (resolved), or null for none.</summary>
    public string? AlsoInLine { get; init; }

    /// <summary>A theme option's schema facts (how it applies, its picker style, its choices), or null for an app dial.</summary>
    public GeneratedOptionRow? Option { get; init; }

    /// <summary>A note drawn above the row (the sidebar layout file could not be read), or null.</summary>
    public SettingsBodyNote? NoteBefore { get; init; }

    /// <summary>A note drawn below the row (what a sidebar migration dropped), or null.</summary>
    public SettingsBodyNote? NoteAfter { get; init; }
}

/// <summary>A dial's explanation badge (design 6.5): which clip explains it, its caption, and whether the clip exists for the active theme
/// and app mode. A badge draws only where it does: hand <see cref="Props"/> to <see cref="ViewKeys.Explainer"/>, keyed
/// <see cref="SettingsKeys.Explain"/>.</summary>
/// <param name="ClipId">The clip (<c>crossfade</c>; two dials may share one).</param>
/// <param name="Caption">The caption (resolved <c>settings.explain.&lt;dialId&gt;</c>), also the clip's alt text.</param>
/// <param name="ClipPath">The clip on disk for the active theme and app mode, or null when it is not there (then nothing draws).</param>
/// <param name="Props">The badge's props, or null when <see cref="ClipPath"/> is.</param>
public sealed record SettingsExplainInfo(string ClipId, string Caption, string? ClipPath, ExplainerProps? Props);

/// <summary>A place outside Settings that draws the same dial (design 5.1): a link to it when the core can open it now.</summary>
/// <param name="Surface">The surface (<c>SettingsSurfaces</c>: <c>queue.panel</c>).</param>
/// <param name="Place">Its name (resolved): "Queue", "Full screen › Options".</param>
/// <param name="Open">Opens the place, or null where it has no door of its own (a menu, the player bar that is always on screen).</param>
public sealed record SettingsAlsoIn(string Surface, string Place, Action? Open);

/// <summary>A note a row carries (it is not a dial): a sidebar layout file the app could not read, what a migration dropped.</summary>
/// <param name="Key">Stable within the page (<c>settings:note:sidebarFileFault</c>).</param>
/// <param name="Title">The note's line (resolved).</param>
/// <param name="Text">Its description (resolved), or null.</param>
/// <param name="Glyph">The glyph NAME (<c>Warning</c>, <c>Info</c>).</param>
public sealed record SettingsBodyNote(string Key, string Title, string? Text, string Glyph);

/// <summary>What the Settings home shows (design 3.3): no controls, state and doors.</summary>
/// <param name="Glance">The six at-a-glance tiles, in order: each the current value at rest and a door to its dial.</param>
/// <param name="Suggestions">At most three, from the hint service (owner decision 14), worded and wired as in the app (surface
/// <c>settings.home</c>: every row is a door). Draw them through <see cref="ViewKeys.Hint"/>, or with
/// <c>SettingsHomeSource.SuggestionSlot()</c>, which also records the show; a theme that lays them out itself calls
/// <c>SettingsHomeSource.SuggestionsShown()</c> when it draws them.</param>
/// <param name="Recent">The last five dials written from any surface, newest first.</param>
public sealed record SettingsHomeFacts(ImmutableArray<SettingsGlanceTile> Glance, ImmutableArray<HintProps> Suggestions, ImmutableArray<SettingsRecentRow> Recent);

/// <summary>One at-a-glance tile.</summary>
/// <param name="Id">Stable (<c>theme</c>, <c>quality</c>, <c>notifications</c>, <c>storage</c>, <c>lyrics</c>, <c>account</c>).</param>
/// <param name="Title">"Audio quality".</param>
/// <param name="Value">"Very high · Normalization on".</param>
/// <param name="DialId">The dial the tile opens.</param>
/// <param name="Glyph">The glyph NAME of the dial's category.</param>
/// <param name="Open">Opens the dial in its category (the deep-link door: its arrival plays).</param>
public sealed record SettingsGlanceTile(string Id, string Title, string Value, string DialId, string Glyph, Action Open);

/// <summary>One Recently changed row ("Crossfade · On · from the queue").</summary>
/// <param name="DialId">The dial written.</param>
/// <param name="Title">Its label (resolved; the id when the dial is not here now: another theme's option).</param>
/// <param name="Value">Its value now, in words ("On", "Very high", "40"), or empty.</param>
/// <param name="From">Where the write came from ("from the queue"), or empty.</param>
/// <param name="AtMs">When (unix ms).</param>
/// <param name="Open">Opens the dial in its category, or null when it is not here now.</param>
public sealed record SettingsRecentRow(string DialId, string Title, string Value, string From, long AtMs, Action? Open);

/// <summary>A run of a result's title that matched the query (Zune draws it in the accent): a start and a length in UTF-16 units.</summary>
public readonly record struct SettingsTextSpan(int Start, int Length);
