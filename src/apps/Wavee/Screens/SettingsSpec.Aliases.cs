// ── Screens/SettingsSpec.Aliases.cs — every old name keeps resolving: the slug aliases and the dials' old row ids ──────────────
//
// Role: CORE (settings rework phase 1, design 3.4). ONE table of slugs replaces the tab slugs, the area slugs, their aliases and the
// grouping's tab slugs once the page draws the registry: each slug a link ever used lands on the category that now holds what it opened,
// and an unknown slug opens the Settings home. A dial answers to its registry id and to its old row ids (`SettingsDial.LegacyIds`,
// declared at the dial), so an inspector line (`setting id=crossfade`) or an old link still names the same writer. A settings LINK (design
// 4.9, `ParseLink`) is read once into a `SettingsLink`: `category/dialId`, `dialId`, `category` or any of those old names.

namespace Wavee;

/// <summary>Where a settings link points: a category, and the dial in it when the link names one.</summary>
/// <param name="Category">The category that opens (the dial's own when a dial is named).</param>
/// <param name="Dial">The dial to bring into view and highlight, or null for the category alone.</param>
/// <param name="Known">False when nothing answered the arg (the Settings home opens).</param>
public readonly record struct SettingsLink(SettingsCategory Category, SettingsDial? Dial, bool Known);

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

    /// <summary>A settings link's arg (<c>wavee://open?route=settings&amp;arg=…</c>, the palette, a toast, the inspector's <c>settings
    /// focus=</c>) read once: <c>category/dialId</c>, <c>dialId</c> (the registry knows its category), <c>category</c> (a category's slug
    /// or any old slug), an old row id, or a word that is one dial's invariant alias (<c>eq</c>). An unknown or empty arg opens the
    /// Settings home (<see cref="SettingsLink.Known"/> false for an unknown one).</summary>
    public static SettingsLink ParseLink(string? arg)
    {
        string a = arg?.Trim() ?? "";
        if (a.Length == 0) return new SettingsLink(Categories[0], null, true);
        int slash = a.IndexOf('/');
        if (slash >= 0)
        {
            string dialPart = a[(slash + 1)..].Trim();
            if (dialPart.Length > 0 && ResolveDial(dialPart) is { } named) return LinkTo(named);
            return TryCategory(a[..slash].Trim(), out var only) ? new SettingsLink(only, null, true) : new SettingsLink(Categories[0], null, false);
        }
        s_byIdFolded ??= IndexFolded();
        if (s_byIdFolded.TryGetValue(a, out var byId)) return LinkTo(byId);
        if (TryCategory(a, out var category)) return new SettingsLink(category, null, true);
        if (ResolveDial(a) is { } dial) return LinkTo(dial);
        return new SettingsLink(Categories[0], null, false);
    }

    /// <summary>The one dial <paramref name="name"/> names: its id or an old row id (<see cref="TryResolve"/>), else the one dial whose
    /// invariant aliases hold the word; null when nothing, or more than one dial, answers.</summary>
    static SettingsDial? ResolveDial(string name)
    {
        if (TryResolve(name, out var dial, out _)) return dial;
        SettingsDial? only = null;
        foreach (var d in All)
            foreach (string alias in d.Aliases)
                if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (only is not null && !ReferenceEquals(only, d)) return null;
                    only = d;
                }
        return only;
    }

    static SettingsLink LinkTo(SettingsDial dial)
        => new(TryPlace(dial, out var category, out _) ? category : Categories[0], dial, true);

    static Dictionary<string, SettingsDial>? s_byIdFolded;

    static Dictionary<string, SettingsDial> IndexFolded()
    {
        var index = new Dictionary<string, SettingsDial>(StringComparer.OrdinalIgnoreCase);
        foreach (var dial in All) index[dial.Id] = dial;
        return index;
    }
}
