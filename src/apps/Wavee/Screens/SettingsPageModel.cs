// ── Screens/SettingsPageModel.cs — the registry's Settings page as a theme draws it: categories, a category's body, the home, the arrival ──
//
// Role: CORE (settings rework phase 4, handoff/settings-2026-11-18/DESIGN.md 3.2-3.3, 4.9, 5.1, 6.5, 7; contract 1.116). The registry
// (`SettingsSpec`) says where a dial lives; THIS file composes what a page draws from it right now, so no theme rebuilds a category itself:
//   · `SettingsPageModel.Categories`   the ten entries (the home, then the nine), each with its full title and its short strip name;
//   · `SettingsBodySource.Of(category)` one category's groups and rows: the dials the live facts compose (a developer dial outside Developer
//                                       mode, another theme's option and a dial composed away are absent), each row worded (label, caption,
//                                       the reason it is greyed), with its value, writer, enablement, children (the parent's expander), its
//                                       explanation badge (`SettingsExplain.InfoFor`) and its "Also in" places with their doors. The theme
//                                       options group lists the ACTIVE theme's options as the expander always grouped them
//                                       (`SettingsThemeOptions.Groups`: a sub-heading per option group, the legacy rows the structure lists,
//                                       the sidebar notes); a reference to a core preference is that core dial, in its own group;
//   · `SettingsBodySource.Row(dial)`    one dial's row with its children: what a search result in Classic and WinUI draws (the result IS the row);
//   · `SettingsHomeSource.Read()`       the home's six facts at a glance, the hint service's top three suggestions and the five recent changes;
//                                       `Suggestion(props)` mounts one suggestion through the theme's hint view and records its show;
//   · `SettingsPageModel.ArrivalOf`     a link's arrival: its category and group, the expanders to open, the highlight duration (the
//                                       structure's `SettingsPagePolicy`, from its Rules).
// COST. A body, a row and the home facts are built when a view renders (the page epoch moves on a write, the hints epoch on a dismissal, the
// writes epoch on a dial write), never per frame; the delegates they carry (the doors) are built once per dial and kept. Each reader
// subscribes its caller to what it reads through `Track()`.

using System.Collections.Immutable;
using System.Globalization;
using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>A category's body and a dial's row, composed from the registry (see the file header). One instance, carried by
/// <see cref="SettingsPageProps.Body"/>.</summary>
public sealed class SettingsBodySource
{
    SettingsBodySource() { }

    /// <summary>The one instance.</summary>
    public static SettingsBodySource Shared { get; } = new();

    /// <summary>The ten entries, in order (the home first).</summary>
    public ImmutableArray<SettingsCategoryEntry> Categories => SettingsPageModel.Categories;

    /// <summary>Subscribe the caller to everything a body is composed from: the page epoch (a write that asks the page to re-read), the
    /// active theme's option cells, the sidebar's layout and Developer mode.</summary>
    public void Track() => SettingsThemeOptions.Track();

    /// <summary>Category <paramref name="index"/>'s body (0 = the home: no groups). Subscribes the caller (<see cref="Track"/>).</summary>
    public SettingsBody Of(int index)
    {
        Track();
        return SettingsPageModel.BodyOf(index);
    }

    /// <summary>The body of the category <paramref name="slug"/> names (its own slug or an old one; an unknown one is the home).</summary>
    public SettingsBody Of(string slug) => Of(SettingsPageModel.IndexOf(SettingsSpec.CategoryFromSlug(slug)));

    /// <summary>One dial's row, its children composed: a search result's row. Null for a dial the facts compose away right now.
    /// Subscribes the caller (<see cref="Track"/>).</summary>
    public SettingsBodyRow? Row(SettingsDial dial)
    {
        Track();
        return SettingsPageModel.RowOf(dial);
    }

    /// <summary>The row of the dial <paramref name="id"/> names (a registry id or an old row id), or null.</summary>
    public SettingsBodyRow? Row(string id) => SettingsSpec.TryResolve(id, out var dial, out _) ? Row(dial) : null;
}

/// <summary>The Settings home's data as a theme draws it (design 3.3; see the file header). One instance, carried by
/// <see cref="SettingsPageProps.Home"/>.</summary>
public sealed class SettingsHomeSource
{
    SettingsHomeSource() { }

    /// <summary>The one instance.</summary>
    public static SettingsHomeSource Shared { get; } = new();

    /// <summary>Subscribe the caller to what the home shows: a hint dismissed or retired, a dial written from anywhere, the page epoch.</summary>
    public void Track()
    {
        _ = Hints.Epoch.Value;
        _ = SettingsWrites.Epoch.Value;
        _ = Settings.PageEpoch.Value;
    }

    /// <summary>The six facts at a glance, the top three suggestions and the five recent changes. Subscribes the caller (<see cref="Track"/>).</summary>
    public SettingsHomeFacts Read()
    {
        Track();
        IReadOnlyList<SettingsGlance> glance = SettingsHome.Glance();
        var tiles = ImmutableArray.CreateBuilder<SettingsGlanceTile>(glance.Count);
        foreach (SettingsGlance g in glance)
            tiles.Add(new SettingsGlanceTile(g.Id, Loc.Get(g.TitleKey), g.Value, g.DialId, SettingsPageModel.GlyphOf(g.DialId), SettingsPageModel.DoorTo(g.DialId)));

        IReadOnlyList<HintDef> defs = SettingsHome.Suggestions();
        var suggestions = ImmutableArray.CreateBuilder<HintProps>(defs.Count);
        foreach (HintDef def in defs) suggestions.Add(SettingsHome.SuggestionProps(def));

        IReadOnlyList<SettingsRecentChange> recent = SettingsHome.Recent();
        var rows = ImmutableArray.CreateBuilder<SettingsRecentRow>(recent.Count);
        foreach (SettingsRecentChange r in recent)
        {
            SettingsDial? dial = SettingsSpec.TryResolve(r.DialId, out var d, out _) ? d : null;
            rows.Add(new SettingsRecentRow(r.DialId, r.TitleKey is { } title ? SettingsPageModel.Text(title)! : r.DialId,
                dial is null ? "" : SettingsPageModel.ValueWords(dial, r.Value),
                r.FromKey is { } from && Localization.Has(from) ? Loc.Get(from) : "", r.AtMs,
                dial is null ? null : SettingsPageModel.DoorTo(dial.Id)));
        }
        return new SettingsHomeFacts(tiles.ToImmutable(), suggestions.ToImmutable(), rows.ToImmutable());
    }

    /// <summary>One suggestion, mounted through the theme's hint view (<see cref="ViewKeys.Hint"/>, surface <c>settings.home</c>); it records
    /// the show when it mounts (the same budget as the hint in the app: once per day). Key the element by the suggestion's id.</summary>
    public Element Suggestion(HintProps props) => Embed.Comp(props, static () => new SuggestionHost()) with { Key = "settings:suggestion:" + props.Id };

    /// <summary>A theme that lays the suggestions out itself (without <see cref="Suggestion"/>) calls this once it draws them.</summary>
    public void SuggestionsShown()
    {
        foreach (HintDef def in SettingsHome.Suggestions()) Hints.SuggestionShown(def);
    }

    [CoreHost]
    sealed class SuggestionHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<HintProps>();
            string id = p.Id;
            UseEffect(() => { if (Hints.Find(id) is { } def) Hints.SuggestionShown(def); }, DepKey.Empty);
            return ViewSlots.View(ViewKeys.Hint, p);
        }
    }
}

/// <summary>The page model's builders (see the file header).</summary>
public static class SettingsPageModel
{
    // ── the categories ──────────────────────────────────────────────────────────────────────────────────────────────

    static ImmutableArray<SettingsCategoryEntry> s_categories;
    static string? s_categoriesCulture;

    /// <summary>The ten entries (the home, then the nine of owner decision 1), worded in the UI language; built once per culture.</summary>
    public static ImmutableArray<SettingsCategoryEntry> Categories
    {
        get
        {
            string culture = Loc.CurrentCulture;
            if (s_categories.IsDefault || !string.Equals(s_categoriesCulture, culture, StringComparison.Ordinal))
            {
                var all = SettingsSpec.Categories;
                var b = ImmutableArray.CreateBuilder<SettingsCategoryEntry>(all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    SettingsCategory c = all[i];
                    b.Add(new SettingsCategoryEntry(i, c.Slug, Text(c.TitleKey)!, Text(c.StripKey)!, Text(c.SubtitleKey)!, c.Glyph));
                }
                s_categories = b.MoveToImmutable();
                s_categoriesCulture = culture;
            }
            return s_categories;
        }
    }

    /// <summary>The index of <paramref name="category"/> in the list (0 = the home).</summary>
    public static int IndexOf(SettingsCategory category)
    {
        var all = SettingsSpec.Categories;
        for (int i = 0; i < all.Count; i++)
            if (ReferenceEquals(all[i], category)) return i;
        return 0;
    }

    /// <summary>The index of the category <paramref name="slug"/> names (an old slug too); 0 (the home) for an unknown one.</summary>
    public static int IndexOf(string? slug) => IndexOf(SettingsSpec.CategoryFromSlug(slug));

    // ── a body ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A dial as a group lists it: its theme option facts and the notes it carries.</summary>
    readonly record struct Entry(SettingsDial Dial, string? Subgroup, GeneratedOptionRow? Option, SettingsBodyNote? Before, SettingsBodyNote? After);

    internal static SettingsBody BodyOf(int index)
    {
        var all = SettingsSpec.Categories;
        index = Math.Clamp(index, 0, all.Count - 1);
        SettingsCategory category = all[index];
        ISettingsFacts facts = SettingsFacts.Live;
        var groups = ImmutableArray.CreateBuilder<SettingsBodyGroup>(category.Groups.Count);
        var entries = new List<Entry>(16);
        foreach (SettingsGroup group in category.Groups)
        {
            entries.Clear();
            string title = Entries(group, facts, entries);
            ImmutableArray<SettingsBodyRow> rows = Compose(entries, facts);
            if (rows.Length == 0) continue;
            groups.Add(new SettingsBodyGroup(group.Id, title, Text(group.SubtitleKey), rows));
        }
        return new SettingsBody(Categories[index], groups.ToImmutable());
    }

    /// <summary>The dials <paramref name="group"/> lists right now, in the order its page draws them; returns the group's heading.</summary>
    static string Entries(SettingsGroup group, ISettingsFacts facts, List<Entry> into)
    {
        if (group.Live is null)
        {
            foreach (SettingsDial dial in group.Dials)
                if (dial.Visible(facts)) into.Add(new Entry(dial, null, null, null, null));
            return Text(group.TitleKey)!;
        }
        // The "‹Theme› options" group: the active theme's options as its expander groups them (generated rows, then the legacy rows its
        // structure lists, per option group), the sidebar's notes riding their rows, then "Reset options". A reference to a core preference
        // is that core dial, which its own group lists.
        ThemeOptionsModel model = ThemeOptionsSection.Current(Platform.Developer.Enabled.Peek());
        SettingsBodyNote? before = null;
        foreach (SettingsOptionGroup optionGroup in SettingsThemeOptions.Groups(model, facts, wizard: false))
            foreach (SettingsOptionItem item in optionGroup.Items)
                switch (item.Kind)
                {
                    case SettingsOptionItemKind.SidebarFileFault:
                        before = new SettingsBodyNote("settings:note:sidebarFileFault", Loc.Get(Strings.Settings.Sidebar.FileFault), null, "Warning");
                        break;
                    case SettingsOptionItemKind.SidebarWhatChanged:
                        if (into.Count > 0)
                            into[^1] = into[^1] with
                            {
                                After = new SettingsBodyNote("settings:note:sidebarWhatChanged", Loc.Get(Strings.Settings.Sidebar.WhatChanged), item.Text, "Info"),
                            };
                        break;
                    case SettingsOptionItemKind.Legacy:
                        into.Add(new Entry(item.Row!, optionGroup.Title, null, before, null));
                        before = null;
                        break;
                    default:
                        if (item.Generated is not { Pref: null } generated) break;
                        into.Add(new Entry(generated.Row, optionGroup.Title, generated, before, null));
                        before = null;
                        break;
                }
        if (model.Reset is { } reset && reset.Visible(facts)) into.Add(new Entry(reset, null, null, null, null));
        return Loc.Format(group.TitleKey, ("theme", model.NameKey.Length > 0 ? Loc.Get(model.NameKey) : ""));
    }

    /// <summary>The group's top-level rows: a dial whose parent the group lists rides that parent's expander.</summary>
    static ImmutableArray<SettingsBodyRow> Compose(List<Entry> entries, ISettingsFacts facts)
    {
        var top = ImmutableArray.CreateBuilder<SettingsBodyRow>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Dial.Parent is { } parent && Find(entries, parent) >= 0) continue;
            top.Add(Build(entries[i], entries, facts, 0));
        }
        return top.ToImmutable();
    }

    static int Find(List<Entry> entries, string id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Dial.Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>One row, its children (the entries whose parent it is) composed under it.</summary>
    static SettingsBodyRow Build(in Entry entry, List<Entry>? siblings, ISettingsFacts facts, int depth)
    {
        SettingsDial dial = entry.Dial;
        ImmutableArray<SettingsBodyRow> children = [];
        if (siblings is not null && depth < 4)
        {
            ImmutableArray<SettingsBodyRow>.Builder? kids = null;
            for (int i = 0; i < siblings.Count; i++)
                if (string.Equals(siblings[i].Dial.Parent, dial.Id, StringComparison.Ordinal))
                    (kids ??= ImmutableArray.CreateBuilder<SettingsBodyRow>()).Add(Build(siblings[i], siblings, facts, depth + 1));
            if (kids is not null) children = kids.ToImmutable();
        }

        SettingsEnablement enablement = dial.Enabled(facts);
        string? caption = Text(dial.Caption(facts));
        string? reason = !enablement.Enabled ? Text(enablement.ReasonKey) : null;
        ImmutableArray<string> choices = [];
        if (dial.ChoiceKeys.Count > 0)
        {
            var b = ImmutableArray.CreateBuilder<string>(dial.ChoiceKeys.Count);
            foreach (string key in dial.ChoiceKeys) b.Add(Text(key)!);
            choices = b.MoveToImmutable();
        }
        else if (dial.LiveChoices is { } live) choices = [.. live()];
        ImmutableArray<SettingsAlsoIn> alsoIn = AlsoIn(dial, out string? alsoInLine);
        return new SettingsBodyRow
        {
            Dial = dial, Label = Text(dial.TitleKey)!, Caption = caption, Description = reason ?? caption, Subgroup = entry.Subgroup,
            Glyph = dial.Glyph, Value = dial.Current, Choices = choices, Enabled = enablement.Enabled, Reason = reason,
            Silent = dial.SilentWhenDisabled, Children = children, Explain = SettingsExplain.InfoFor(dial), AlsoIn = alsoIn,
            AlsoInLine = alsoInLine, Option = entry.Option, NoteBefore = entry.Before, NoteAfter = entry.After,
        };
    }

    /// <summary>One dial's row with its children, as its own group composes it; null when the facts compose it away.</summary>
    internal static SettingsBodyRow? RowOf(SettingsDial dial)
    {
        ISettingsFacts facts = SettingsFacts.Live;
        if (!dial.Visible(facts)) return null;
        if (!SettingsSpec.TryPlace(dial, out _, out SettingsGroup group)) return Build(new Entry(dial, null, null, null, null), null, facts, 0);
        var entries = new List<Entry>(group.Dials.Count + 4);
        Entries(group, facts, entries);
        int at = -1;
        for (int i = 0; i < entries.Count; i++)
            if (ReferenceEquals(entries[i].Dial, dial)) { at = i; break; }
        return at >= 0 ? Build(entries[at], entries, facts, 0) : Build(new Entry(dial, null, null, null, null), null, facts, 0);
    }

    // ── "Also in" ───────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Dictionary<string, Action?> s_doors = new(StringComparer.Ordinal);

    static ImmutableArray<SettingsAlsoIn> AlsoIn(SettingsDial dial, out string? line)
    {
        line = null;
        if (dial.Contexts.Count == 0) return [];
        var b = ImmutableArray.CreateBuilder<SettingsAlsoIn>(dial.Contexts.Count);
        var places = new string[dial.Contexts.Count];
        for (int i = 0; i < dial.Contexts.Count; i++)
        {
            string surface = dial.Contexts[i];
            string place = PlaceOf(surface);
            places[i] = place;
            b.Add(new SettingsAlsoIn(surface, place, DoorOf(surface)));
        }
        line = Strings.Settings.AlsoIn.Line(string.Join(Loc.Get(Strings.Settings.AlsoIn.Separator), places));
        return b.MoveToImmutable();
    }

    /// <summary>A surface's name for "Also in" ("Full screen › Options"); the surface id where it has none yet.</summary>
    public static string PlaceOf(string surface)
    {
        string key = "settings.alsoIn.place." + surface;
        return Localization.Has(key) ? Loc.Get(key) : surface;
    }

    /// <summary>The door to a surface a dial also lives on, or null where it has none of its own (a menu, the bar that is always on screen,
    /// a flyout that needs its anchor). Built once per surface.</summary>
    static Action? DoorOf(string surface)
    {
        if (InContextDoors.Handles(surface)) return InContextDoors.Of(surface);   // asked every time: those places come and go with the window
        if (s_doors.TryGetValue(surface, out Action? known)) return known;
        Action? door = surface switch
        {
            SettingsSurfaces.QueuePanel => static () =>
            {
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Queue)) Shell.Ui.Toggle(Shell.RailMode.Queue);
            },
            SettingsSurfaces.StageOptions or SettingsSurfaces.StageGallery => static () => PlayerVerbs.Default.OpenStage(null, "settings-also-in"),
            SettingsSurfaces.LikedCover => static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked)),
            SettingsSurfaces.LogsMenu => static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Logs)),
            _ => null,
        };
        s_doors[surface] = door;
        return door;
    }

    // ── the home's pieces ───────────────────────────────────────────────────────────────────────────────────────────

    static readonly Dictionary<string, Action> s_dialDoors = new(StringComparer.Ordinal);

    /// <summary>The door to a dial (the deep link: its category opens and its arrival plays), built once per dial.</summary>
    public static Action DoorTo(string dialId)
    {
        if (!s_dialDoors.TryGetValue(dialId, out Action? door)) s_dialDoors[dialId] = door = () => SettingsLinks.Open(dialId);
        return door;
    }

    /// <summary>The glyph NAME of the category that holds <paramref name="dialId"/> (the home's for none).</summary>
    public static string GlyphOf(string dialId)
        => SettingsSpec.TryResolve(dialId, out var dial, out _) && SettingsSpec.TryPlace(dial, out var category, out _)
            ? category.Glyph : SettingsSpec.Categories[0].Glyph;

    /// <summary>A dial's value in words: a toggle's On/Off, a choice's label, a number; empty for none.</summary>
    public static string ValueWords(SettingsDial dial, object? value) => value switch
    {
        null => "",
        bool on => Loc.Get(on ? Strings.Settings.Value.On : Strings.Settings.Value.Off),
        int i when dial.Kind is SettingsRowKind.Choice or SettingsRowKind.Segmented or SettingsRowKind.Picker && (uint)i < (uint)dial.ChoiceKeys.Count
            => Text(dial.ChoiceKeys[i])!,
        int i => i.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // ── the arrival ─────────────────────────────────────────────────────────────────────────────────────────────────

    static SettingsArrival s_arrival = SettingsArrival.None;

    /// <summary>The arrival of <paramref name="focus"/> (design 4.9): the dial's category and group, the expanders that open for it (outermost
    /// first) and the highlight's duration. The last one is kept, so an unchanged focus re-pushes an equal record.</summary>
    public static SettingsArrival ArrivalOf(SettingsFocus focus, int highlightMs)
    {
        if (focus.Seq == 0 || focus.DialId.Length == 0) return SettingsArrival.None;
        SettingsArrival last = s_arrival;
        if (last.Seq == focus.Seq && last.HighlightMs == highlightMs && string.Equals(last.DialId, focus.DialId, StringComparison.Ordinal)) return last;
        string category = "";
        string? group = null, parent = null;
        var expand = new List<string>(2);
        if (SettingsSpec.TryResolve(focus.DialId, out SettingsDial dial, out _))
        {
            if (SettingsSpec.TryPlace(dial, out var c, out var g)) { category = c.Slug; group = g.Id; }
            parent = dial.Parent;
            for (string? p = dial.Parent; p is not null && expand.Count < 8; p = SettingsSpec.TryDial(p, out var up) ? up.Parent : null)
                expand.Add(p);
            expand.Reverse();
        }
        return s_arrival = new SettingsArrival
        {
            DialId = focus.DialId, Seq = focus.Seq, Category = category, Group = group, Parent = parent, Expand = [.. expand], HighlightMs = highlightMs,
        };
    }

    // ── words ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A loc key's text; a string that is no key is already text; null stays null.</summary>
    internal static string? Text(string? key) => key is null ? null : Localization.Has(key) ? Loc.Get(key) : key;
}
