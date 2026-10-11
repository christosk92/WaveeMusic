// ── Theming/ThemeBootstrap.cs — the migration of legacy presentation settings into theme options (themes plan 7.1) ─────
//
// Each retired presentation switch gets ONE named step (added by its migration PR). A step reads the legacy key(s) and
// writes the theme option key(s) that replace them. The mechanism guarantees what no numbering scheme can:
//
//   · NAMED, never numbered. A step is identified by its name and records `theme.bootstrap.done.<Name> = true` once it ran.
//     Lanes merge their steps in any order and a patch release ships any subset, so a step can never be "skipped because a
//     later number already ran".
//   · COPY, never delete. A step writes a new key only when the new key is ABSENT and the legacy key is PRESENT; the legacy
//     key stays as inert data. A downgrade finds its keys; a build of another branch sharing the store loses nothing.
//   · ORDER-INDEPENDENT. A step declares the keys it reads and the keys it writes; `Disjointness` rejects a step set in
//     which two steps write the same key or one reads what another writes. That is what makes every merge order produce the
//     same end state (the tests run every permutation).
//   · Value rules (BP 1.6): an unknown or out-of-range legacy value becomes the option default (the step simply does not
//     copy it); a renamed option starts at its default; a removed choice becomes the default.
//
// T1 ships the machinery with NO step registered. It runs from `Platform.Boot` right after `ZoomAutoPolicy.MigrateMode`
// and before anything reads a presentation setting. WVTH010 keeps legacy key names and deleted member names inside this file.

namespace Wavee.Theming;

/// <summary>One migration step. <paramref name="ReadsLegacyKeys"/> and <paramref name="WritesKeys"/> are setting NAMES.</summary>
public sealed record ThemeBootstrapStep(string Name, string[] ReadsLegacyKeys, string[] WritesKeys, Action<IAppSettings> Run);

/// <summary>Runs the migration steps.</summary>
public static class ThemeBootstrap
{
    /// <summary>The prefix of the done markers: <c>theme.bootstrap.done.&lt;Name&gt;</c>.</summary>
    public const string DonePrefix = "theme.bootstrap.done.";

    /// <summary>The steps shipped in this build. Empty in T1: each migration PR appends its own.</summary>
    public static IReadOnlyList<ThemeBootstrapStep> Steps { get; } = [];

    /// <summary>The setting name of a step's done marker.</summary>
    public static string DoneKey(string stepName) => DonePrefix + stepName;

    /// <summary>Runs the shipped steps against <paramref name="settings"/>.</summary>
    public static void Run(IAppSettings settings) => Run(settings, Steps);

    /// <summary>Runs <paramref name="steps"/> against <paramref name="settings"/>, in the order given. A step that already
    /// recorded its marker is skipped; a step that throws is logged and retried on the next launch (no marker), and does
    /// not stop the others. Returns how many steps ran.</summary>
    public static int Run(IAppSettings settings, IReadOnlyList<ThemeBootstrapStep> steps)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(steps);
        int ran = 0;
        foreach (var step in steps)
        {
            var done = new SettingKey<bool>(DoneKey(step.Name), false);
            if (settings.Get(done)) continue;
            try
            {
                step.Run(settings);
            }
            catch (Exception ex)
            {
                Log.Warn("theme", "bootstrap step '" + step.Name + "' failed; it runs again next launch", ex);
                continue;
            }
            settings.Set(done, true);
            ran++;
        }
        return ran;
    }

    /// <summary>Copies a legacy value to its replacement: only when <paramref name="target"/> is absent and
    /// <paramref name="legacy"/> is present. <paramref name="map"/> converts the value; returning false leaves the target absent
    /// (the option default applies). Returns true when a value was written. The legacy key is never touched.</summary>
    public static bool Copy<TLegacy, TTarget>(IAppSettings settings, SettingKey<TLegacy> legacy, SettingKey<TTarget> target,
        Func<TLegacy, (bool Valid, TTarget Value)> map)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(map);
        if (settings.Has(target.Name)) return false;
        if (!settings.TryGet(legacy, out TLegacy old)) return false;
        var (valid, value) = map(old);
        if (!valid) return false;
        settings.Set(target, value);
        return true;
    }

    /// <summary>The same-type copy: the legacy value goes to the target unchanged.</summary>
    public static bool Copy<T>(IAppSettings settings, SettingKey<T> legacy, SettingKey<T> target)
        => Copy(settings, legacy, target, static value => (true, value));

    /// <summary>Why <paramref name="steps"/> are not order-independent, or null when they are: step names are unique and
    /// non-empty, no two steps write the same key, no step reads a key another step writes, and no step writes a done marker.</summary>
    public static string? Disjointness(IReadOnlyList<ThemeBootstrapStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var writers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name)) return "a step has no name";
            if (!names.Add(step.Name)) return "two steps are named '" + step.Name + "'";
            foreach (string key in step.WritesKeys)
            {
                if (key.StartsWith(DonePrefix, StringComparison.Ordinal))
                    return "step '" + step.Name + "' writes the done marker '" + key + "'";
                if (writers.TryGetValue(key, out string? other))
                    return "steps '" + other + "' and '" + step.Name + "' both write '" + key + "'";
                writers[key] = step.Name;
            }
        }
        foreach (var step in steps)
        {
            foreach (string key in step.ReadsLegacyKeys)
            {
                if (writers.TryGetValue(key, out string? writer) && !string.Equals(writer, step.Name, StringComparison.Ordinal))
                    return "step '" + step.Name + "' reads '" + key + "', which step '" + writer + "' writes";
                if (writers.TryGetValue(key, out writer))
                    return "step '" + step.Name + "' both reads and writes '" + key + "'";
            }
        }
        return null;
    }
}
