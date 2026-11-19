// ── Screens/SettingsExplain.cs — explanation badges (design 6.5): which dials have a clip, where the clips live, the release check ─────
//
// Role: CORE (settings rework phase 3; owner decision 15). A badge explains a setting whose effect is something you SEE or HEAR, where the
// words alone do not show it: a short looping clip of the before and after, in a tip, with a one-line caption. It is on demand, never a
// nudge, so it never counts against the hint budget.
//
//   · THE LIST (`s_clips`): the dial, the clip that explains it, and the themes it is registered for. A dial is registered for a theme
//     only when its clips for that theme exist in BOTH app modes (committed under assets/settings-explain): the list is the committed clip
//     set, exactly, and grows as clips are recorded. The full target list of design 6.5, with why each missing one is missing, is the
//     recorder's manifest (`tools/settings-explain/clips.json`, a status per entry). The clip id IS the dial id, except where one clip
//     serves two dials (Crossfade duration shows Crossfade's). The registry sets `SettingsDial.Explainer` (and `ExplainerThemes`) from it
//     on every dial it names (`SettingsSpec.Make`, `ThemeOptionsSection.Make`), so this table is the one place a dial joins the list.
//   · THE CLIPS: one per clip id per theme per app mode, an animated WebP at
//     `assets/settings-explain/<theme>/<mode>/<clip>.webp` (mode `dark` | `light`), recorded from the real app by `tools/settings-explain`,
//     committed like the What's new media. A clip shared by two dials (Crossfade and its duration) is one file.
//   · A BADGE SHOWS ONLY WHERE ITS CLIP EXISTS for the active theme and mode (owner decision): `InfoFor` and `PropsFor` answer null
//     otherwise, so no theme draws a badge, or a tip without its clip, for a dial whose clip is not recorded yet.
//   · THE RELEASE CHECK: `Wavee.exe --check-explainer-clips <assets/settings-explain>` lists every clip the table needs that is missing,
//     and every clip over the size budget (`SettingsExplainRules`), and exits 1 when there is one. The Wavee project runs it after a
//     Release build when the MSBuild property `WaveeExplainerClipCheck` is `true` (Wavee.csproj, target `WaveeCheckExplainerClips`): on
//     by default since the clips landed (phase 6), so a Release build fails on a registered badge without its clip. A Debug build never
//     runs it.
//
// Captions: `settings.explain.<dialId>` (en-US in assets/loc), also the clip's alt text.

using FluentGpu.Dsl;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The explanation clips' size budget (design 6.5): shipped in the package, so it is small and bounded.</summary>
public static class SettingsExplainRules
{
    /// <summary>At most this many bytes per clip.</summary>
    public const long MaxClipBytes = 120 * 1024;
    /// <summary>At most this many bytes for every clip together.</summary>
    public const long MaxTotalBytes = 9L * 1024 * 1024;
}

/// <summary>Explanation badges (see the file header).</summary>
public static class SettingsExplain
{
    /// <summary>One row of the badge list: the dial, the themes its badge is registered for (null: every shipped theme; a theme option's is
    /// its own theme alone), and the dial whose clip it shares (null: its own).</summary>
    public readonly record struct Clip(string DialId, string[]? Themes = null, string? SharesWith = null)
    {
        /// <summary>The clip's id: the dial's own id, or the id of the dial it shares a clip with.</summary>
        public string Id => SharesWith ?? DialId;
    }

    /// <summary>The registered badges, in the order of design 6.5: each exactly where its clips are committed for both app modes. Not
    /// registered yet (clips.json says why): Calm motion, WinUI's Backdrop and Canvas (no honest headless recording yet), the Liked Songs cover
    /// in Classic and Zune (their Liked page draws no cover at the default layout), and Classic's Tinted surfaces (no visible change in light mode).</summary>
    static readonly Clip[] s_clips =
    [
        // Appearance
        new("appearance.connectedAnimations", ["winui"]),   // only WinUI's pages fly: Classic and Zune have no connected flight, the dial does not show
        new("appearance.fastNavigation", ["classic", "winui"]),   // Zune defers its page mount: nothing overlaps, the dial does not show
        new("appearance.likedCover", ["winui"]),
        new(SettingsSpec.ThemeOptionPrefix + "structure.navigationPane", ["winui"]),
        new(SettingsSpec.ThemeOptionPrefix + "style.rowDensity", ["winui"]),
        new(SettingsSpec.ThemeOptionPrefix + "structure.artworkInRows", ["winui"]),
        new(SettingsSpec.ThemeOptionPrefix + "structure.rowStyle", ["classic"]),
        new(SettingsSpec.ThemeOptionPrefix + "structure.sidebar", ["classic"]),
        new("appearance.marquee", ["classic"]),
        new("appearance.daylistClock"),
        new(SettingsSpec.ThemeOptionPrefix + "structure.background", ["zune"]),
        // Sound (animated diagrams: tips never make a sound)
        new("sound.normalization"),
        new("sound.normalization.mode"),
        // Playback
        new("playback.crossfade"),
        new("playback.crossfade.duration", SharesWith: "playback.crossfade"),
        new("playback.video.endCountdown"),
        new("playback.previewSound"),
        // Lyrics & Now playing
        new("lyrics.blur"),
        new("lyrics.ai"),
        new("lyrics.playerStyle"),
        new("lyrics.playerStyle.finish"),
        new("lyrics.stage.layout"),
        new("lyrics.stage.spectrum"),
        new("lyrics.mini.face"),
        new("lyrics.mini.artColour"),
    ];

    /// <summary>The badge list.</summary>
    public static ReadOnlySpan<Clip> Clips => s_clips;

    /// <summary>The folder under the app's <c>assets</c> that holds the clips.</summary>
    public const string Folder = "settings-explain";

    /// <summary>The app modes a clip is recorded in.</summary>
    public static readonly string[] Modes = ["dark", "light"];

    /// <summary>The caption's loc key.</summary>
    public static string CaptionKey(string dialId) => "settings.explain." + dialId;

    /// <summary>The badge row of an app dial (null when it has none). A theme option's row is found by <see cref="ThemeOptionClip"/>.</summary>
    internal static Clip? Of(string dialId)
    {
        foreach (var clip in s_clips)
            if (!IsThemeOption(clip) && string.Equals(clip.DialId, dialId, StringComparison.Ordinal)) return clip;
        return null;
    }

    static bool IsThemeOption(in Clip clip) => clip.DialId.StartsWith(SettingsSpec.ThemeOptionPrefix, StringComparison.Ordinal);

    /// <summary>Every app dial the list names must exist, and a theme option's row names its one theme (a typo fails loudly at the
    /// registry's first lookup, never drops a badge).</summary>
    internal static void Validate(IReadOnlyDictionary<string, SettingsDial> dials)
    {
        foreach (var clip in s_clips)
        {
            if (!IsThemeOption(clip) && !dials.ContainsKey(clip.DialId))
                throw new InvalidOperationException("SettingsExplain: the badge list names '" + clip.DialId + "', which is no dial.");
            if (IsThemeOption(clip) && clip.Themes is not { Length: 1 })
                throw new InvalidOperationException("SettingsExplain: the theme option '" + clip.DialId + "' names its one theme.");
        }
    }

    /// <summary>The clip id of <paramref name="themeId"/>'s option <paramref name="qualifiedId"/>, or null.</summary>
    public static string? ThemeOptionClip(string themeId, string qualifiedId)
    {
        foreach (var clip in s_clips)
            if (clip.Themes is { } themes && Contains(themes, themeId)
                && clip.DialId.Length == SettingsSpec.ThemeOptionPrefix.Length + qualifiedId.Length
                && clip.DialId.StartsWith(SettingsSpec.ThemeOptionPrefix, StringComparison.Ordinal)
                && clip.DialId.AsSpan(SettingsSpec.ThemeOptionPrefix.Length).SequenceEqual(qualifiedId))
                return clip.Id;
        return null;
    }

    /// <summary>The themes a clip row applies to: its own theme, or every shipped theme.</summary>
    public static IReadOnlyList<string> ThemesOf(in Clip clip) => clip.Themes ?? App.ShippedThemeIds;

    /// <summary>The clip's path relative to the clip folder.</summary>
    public static string RelativePath(string clipId, string themeId, string mode) => Path.Combine(themeId, mode, clipId + ".webp");

    static readonly Dictionary<string, string?> s_exists = new(StringComparer.Ordinal);

    /// <summary>The clip on disk for (<paramref name="clipId"/>, <paramref name="themeId"/>, <paramref name="mode"/>), or null when it is not
    /// there. Each path is looked up once per process (clips ship with the build and never change under it).</summary>
    public static string? ClipPath(string clipId, string themeId, string mode)
    {
        string relative = RelativePath(clipId, themeId, mode);
        if (s_exists.TryGetValue(relative, out string? known)) return known;
        string full = Path.Combine(AppContext.BaseDirectory, "assets", Folder, relative);
        string? found = File.Exists(full) ? full : null;
        s_exists[relative] = found;
        return found;
    }

    /// <summary>The badge of <paramref name="dial"/> for the active theme and app mode, or null when it has none here: no clip id, a theme
    /// it does not apply to, or no clip on disk (a badge shows only where its clip exists).</summary>
    public static ExplainerProps? PropsFor(SettingsDial dial, string surface = SettingsSurfaces.Settings)
    {
        string theme = ThemePickerFacts.State.Peek().CurrentId;   // the one "current" a core reader may ask, as data (the clips' folder)
        if (dial.Explainer is not { } clip || theme.Length == 0) return null;
        if (dial.ExplainerThemes is { } themes && !Contains(themes, theme)) return null;
        string? path = ClipPath(clip, theme, Tok.Theme == ThemeKind.Light ? "light" : "dark");
        if (path is null) return null;
        return new ExplainerProps
        {
            DialId = dial.Id, Caption = Loc.Get(CaptionKey(dial.Id)), ClipPath = path, ReducedMotion = Design.Reduced, Surface = surface,
        };
    }

    /// <summary>The badge of <paramref name="dial"/> as the page model carries it (<see cref="SettingsExplainInfo"/>): its clip id, caption, the
    /// clip's path and the props to mount (<see cref="PropsFor"/>). Null wherever no badge shows: no clip id, another theme's option, or no clip
    /// on disk for the active theme and app mode (owner decision: a badge shows only where its clip exists).</summary>
    public static SettingsExplainInfo? InfoFor(SettingsDial dial, string surface = SettingsSurfaces.Settings)
        => PropsFor(dial, surface) is { ClipPath: { } path } props ? new SettingsExplainInfo(dial.Explainer!, props.Caption, path, props) : null;

    static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── the release check (Wavee.csproj target WaveeCheckExplainerClips, gated by WaveeExplainerClipCheck) ─────────────────

    /// <summary>The switch of the release check.</summary>
    public const string CheckSwitch = "--check-explainer-clips";

    /// <summary><c>Wavee.exe --check-explainer-clips &lt;folder&gt;</c>: every clip the badge list needs, for each theme and mode it applies to,
    /// must exist under <c>&lt;folder&gt;</c> and fit the budget. Prints one line per problem; the code is 1 when there is one, 64 on a bad
    /// command line. Reads only the table and the folder: nothing boots.</summary>
    public static bool TryRunCheck(string[] args, out int code)
    {
        code = 0;
        int at = Array.IndexOf(args, CheckSwitch);
        if (at < 0) return false;
        if (at + 1 >= args.Length)
        {
            Console.Error.WriteLine(CheckSwitch + " needs the clip folder (assets/" + Folder + ").");
            code = 64;
            return true;
        }
        string root = args[at + 1];
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int needed = 0;
        foreach (var clip in s_clips)
            foreach (string theme in ThemesOf(clip))
                foreach (string mode in Modes)
                {
                    string relative = RelativePath(clip.Id, theme, mode);
                    if (!seen.Add(relative)) continue;   // a clip two dials share is one file
                    needed++;
                    string full = Path.Combine(root, relative);
                    if (!File.Exists(full)) { problems.Add("missing  " + relative + "  (" + clip.DialId + ")"); continue; }
                    long bytes = new FileInfo(full).Length;
                    total += bytes;
                    if (bytes > SettingsExplainRules.MaxClipBytes)
                        problems.Add("too big  " + relative + "  " + bytes + " bytes (budget " + SettingsExplainRules.MaxClipBytes + ")");
                }
        if (total > SettingsExplainRules.MaxTotalBytes)
            problems.Add("too big  all clips together: " + total + " bytes (budget " + SettingsExplainRules.MaxTotalBytes + ")");
        foreach (string problem in problems) Console.Out.WriteLine("explainer clips: " + problem);
        Console.Out.WriteLine("explainer clips: " + needed + " needed, " + problems.Count + " problem(s), " + total + " bytes");
        code = problems.Count == 0 ? 0 : 1;
        return true;
    }
}
