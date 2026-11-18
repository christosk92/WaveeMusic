// ── Screens/SettingsSpec.Aliases.cs — every old name keeps resolving: the slug aliases and the dials' old row ids ──────────────
//
// Role: CORE (settings rework phase 1, design 3.4). ONE table of slugs replaces the tab slugs, the area slugs, their aliases and the
// grouping's tab slugs once the page draws the registry: each slug a link ever used lands on the category that now holds what it opened,
// and an unknown slug opens the Settings home. A dial answers to its registry id and to its old row ids (`SettingsDial.LegacyIds`,
// declared at the dial), so an inspector line (`setting id=crossfade`) or an old link still names the same writer.

namespace Wavee;

public static partial class SettingsSpec
{
    /// <summary>The slugs older links used and the boards' spellings, with the category that now holds what each opened. A category's
    /// own slug is not listed (it resolves first).</summary>
    static readonly (string Slug, string Category)[] s_slugAliases =
    [
        ("general", "system"), ("language", "system"), ("graphics", "system"), ("links", "system"), ("tray", "system"),
        ("audio", "sound"), ("devices", "sound"), ("devices-connect", "sound"), ("connect", "sound"),
        ("video", "playback"),
        ("fullscreen", "lyrics"), ("nowplaying", "lyrics"),
        ("storage-cache", "storage"), ("downloads", "storage"), ("downloads-storage", "storage"), ("cache", "storage"),
        ("privacy", "account"), ("privacy-diagnostics", "account"),
        ("about", "help"), ("feedback", "help"), ("feedback-help", "help"), ("keyboard", "help"), ("shortcuts", "help"),
        ("logs", "help"), ("diagnostics", "help"),
    ];

    /// <summary>A link's name → the category: its own slug, else an old slug's new home, else the Settings home (a bad arg opens the
    /// page, never nowhere).</summary>
    public static SettingsCategory CategoryFromSlug(string? slug)
        => TryCategory(slug, out var category) ? category : Categories[0];

    /// <summary>The category <paramref name="slug"/> names (its own slug or an old one), or false.</summary>
    public static bool TryCategory(string? slug, out SettingsCategory category)
    {
        if (slug is { Length: > 0 })
        {
            foreach (var c in Categories)
                if (string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase)) { category = c; return true; }
            foreach (var (alias, target) in s_slugAliases)
                if (string.Equals(alias, slug, StringComparison.OrdinalIgnoreCase))
                    foreach (var c in Categories)
                        if (c.Id == target) { category = c; return true; }
        }
        category = null!;
        return false;
    }

    /// <summary>The dial <paramref name="name"/> names: its registry id, else an old row id, among the declared dials and then the
    /// active theme's live options (case-insensitive, as the inspector always matched). False when nothing answers, or when an old id
    /// is shared (About's and Privacy's "privacyPolicy"): <paramref name="matches"/> then lists every dial that answers.</summary>
    public static bool TryResolve(string name, out SettingsDial dial, out IReadOnlyList<SettingsDial> matches)
    {
        s_byIdFolded ??= IndexFolded();
        if (s_byIdFolded.TryGetValue(name, out dial!)) { matches = [dial]; return true; }

        var found = new List<SettingsDial>(2);
        foreach (var d in All)
            if (AnswersTo(d, name)) found.Add(d);
        if (found.Count == 0)
            foreach (var (_, _, live) in LiveDials)
                if (string.Equals(live.Id, name, StringComparison.OrdinalIgnoreCase) || AnswersTo(live, name)) found.Add(live);
        matches = found;
        dial = found.Count == 1 ? found[0] : null!;
        return found.Count == 1;
    }

    /// <summary>Does <paramref name="dial"/> answer to the old row id <paramref name="name"/>?</summary>
    public static bool AnswersTo(SettingsDial dial, string name)
    {
        foreach (string legacy in dial.LegacyIds)
            if (string.Equals(legacy, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static Dictionary<string, SettingsDial>? s_byIdFolded;

    static Dictionary<string, SettingsDial> IndexFolded()
    {
        var index = new Dictionary<string, SettingsDial>(StringComparer.OrdinalIgnoreCase);
        foreach (var dial in All) index[dial.Id] = dial;
        return index;
    }
}
