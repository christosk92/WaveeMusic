using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.Backend.Modules;

// ── MODULE DISCOVERY — the two roots, the validation gate, and the diagnostics trail ─────────────────────────────────
// A playback module is a DIRECTORY carrying a `wavee-module.json` manifest next to its entry point. Two roots, in this
// order of precedence per id (highest COMPATIBLE protocolVersion wins, then the highest version):
//
//   <app dir>\modules\<id>\wavee-module.json                              (bundled — first-party, never removed)
//   %LOCALAPPDATA%\Wavee\modules\<id>\<version>\wavee-module.json         (user store — installed/updated at runtime)
//
// Everything that is probed and REJECTED is kept (with the reason) rather than dropped, exactly like the PlayPlay
// runtime locator: "nothing happened" is never an acceptable answer on the diagnostics page. The whole class is
// engine-free and file-system-injectable, so discovery + every rejection reason is unit-testable with no disk.

/// <summary>One module the host will actually run: a validated manifest plus the directory it lives in.</summary>
/// <param name="Id">The manifest id (also the directory name under either root).</param>
/// <param name="Version">The manifest version string.</param>
/// <param name="Dir">The absolute module directory (the working directory of its process).</param>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="Bundled">True when it came from the app-directory root (a first-party, shipped module).</param>
public sealed record InstalledModule(string Id, string Version, string Dir, ModuleManifest Manifest, bool Bundled);

/// <summary>A directory that looked like a module and was refused — surfaced verbatim on the diagnostics page.</summary>
/// <param name="Dir">The directory that was probed.</param>
/// <param name="Reason">Why it was refused, in one line.</param>
public sealed record ModuleRejection(string Dir, string Reason);

/// <summary>The injectable file-system seam: discovery is pure over these four probes, so the whole walk (including
/// every rejection reason) is testable without touching a disk.</summary>
/// <param name="DirectoryExists">Does this directory exist?</param>
/// <param name="EnumerateDirectories">Immediate child directories (absolute paths); empty when the parent is missing.</param>
/// <param name="FileExists">Does this file exist?</param>
/// <param name="ReadAllText">Read a file's whole text; may throw (the walk turns that into a rejection).</param>
public sealed record ModuleFileSystem(
    Func<string, bool> DirectoryExists,
    Func<string, string[]> EnumerateDirectories,
    Func<string, bool> FileExists,
    Func<string, string> ReadAllText)
{
    /// <summary>The real disk. Enumeration failures answer "empty" rather than throwing — an unreadable root is a
    /// missing root as far as discovery is concerned, and the rejection list still says so.</summary>
    public static ModuleFileSystem Real { get; } = new(
        Directory.Exists,
        static dir => { try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); } },
        File.Exists,
        File.ReadAllText);
}

/// <summary>Discovers, validates and ranks the installed playback modules across the two roots.</summary>
public sealed class ModuleCatalog
{
    /// <summary>The manifest file name inside a module directory.</summary>
    public const string ManifestFileName = "wavee-module.json";

    /// <summary>The lowest wire-protocol version this host still speaks.</summary>
    public const int MinProtocol = ModuleProtocol.MinSupported;

    /// <summary>The highest wire-protocol version this host speaks.</summary>
    public const int MaxProtocol = ModuleProtocol.Version;

    ModuleCatalog(IReadOnlyList<InstalledModule> modules, IReadOnlyList<ModuleRejection> rejections,
        string bundledRoot, string userRoot)
    {
        Modules = modules;
        Rejections = rejections;
        BundledRoot = bundledRoot;
        UserRoot = userRoot;
    }

    /// <summary>The modules that will be launched, one per id, highest compatible version first-and-only.</summary>
    public IReadOnlyList<InstalledModule> Modules { get; }

    /// <summary>Every directory that was probed and refused, with the reason.</summary>
    public IReadOnlyList<ModuleRejection> Rejections { get; }

    /// <summary>The app-directory root this catalog walked.</summary>
    public string BundledRoot { get; }

    /// <summary>The user-store root this catalog walked.</summary>
    public string UserRoot { get; }

    /// <summary>The bundled root: <c>&lt;app dir&gt;\modules</c>.</summary>
    public static string DefaultBundledRoot => Path.Combine(AppContext.BaseDirectory, "modules");

    /// <summary>The user store: <c>%LOCALAPPDATA%\Wavee\modules</c>.</summary>
    public static string DefaultUserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "modules");

    /// <summary>A module's private, writable data directory: <c>%LOCALAPPDATA%\Wavee\modules-data\&lt;id&gt;</c>.</summary>
    /// <param name="moduleId">The module id.</param>
    public static string DataDirFor(string moduleId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "modules-data", moduleId);

    /// <summary>An empty catalog — the honest answer for a build with no modules root at all.</summary>
    public static ModuleCatalog Empty { get; } =
        new(Array.Empty<InstalledModule>(), Array.Empty<ModuleRejection>(), "", "");

    /// <summary>Walk both roots, validate every manifest, and rank the survivors per id.</summary>
    /// <param name="bundledRoot">The app-directory root; null uses <see cref="DefaultBundledRoot"/>.</param>
    /// <param name="userRoot">The user-store root; null uses <see cref="DefaultUserRoot"/>.</param>
    /// <param name="fs">The file-system seam; null uses the real disk.</param>
    public static ModuleCatalog Discover(string? bundledRoot = null, string? userRoot = null, ModuleFileSystem? fs = null)
    {
        fs ??= ModuleFileSystem.Real;
        bundledRoot ??= DefaultBundledRoot;
        userRoot ??= DefaultUserRoot;

        var candidates = new List<InstalledModule>();
        var rejections = new List<ModuleRejection>();

        // Bundled: <root>\<id>\wavee-module.json
        if (fs.DirectoryExists(bundledRoot))
            foreach (string dir in fs.EnumerateDirectories(bundledRoot))
                Probe(fs, dir, bundled: true, candidates, rejections);

        // User store: <root>\<id>\<version>\wavee-module.json
        if (fs.DirectoryExists(userRoot))
            foreach (string idDir in fs.EnumerateDirectories(userRoot))
                foreach (string versionDir in fs.EnumerateDirectories(idDir))
                    Probe(fs, versionDir, bundled: false, candidates, rejections);

        return new ModuleCatalog(Rank(candidates), rejections, bundledRoot, userRoot);
    }

    static void Probe(ModuleFileSystem fs, string dir, bool bundled,
        List<InstalledModule> candidates, List<ModuleRejection> rejections)
    {
        string manifestPath = Path.Combine(dir, ManifestFileName);
        if (!fs.FileExists(manifestPath))
        {
            rejections.Add(new ModuleRejection(dir, "no " + ManifestFileName));
            return;
        }

        ModuleManifest? manifest;
        try
        {
            string json = fs.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize(json, SdkJsonContext.Default.ModuleManifest);
        }
        catch (Exception ex)
        {
            rejections.Add(new ModuleRejection(dir, "unreadable manifest: " + ex.GetType().Name + ": " + ex.Message));
            return;
        }

        if (manifest is null)
        {
            rejections.Add(new ModuleRejection(dir, "empty manifest"));
            return;
        }

        string? reason = Validate(manifest, dir, bundled);
        if (reason is not null)
        {
            rejections.Add(new ModuleRejection(dir, reason));
            return;
        }

        candidates.Add(new InstalledModule(manifest.Id, manifest.Version, dir, manifest, bundled));
    }

    /// <summary>The whole manifest gate, as one pure function: null = accepted, otherwise the rejection reason.
    /// Kept public so the diagnostics page and the tests state the exact same rules.</summary>
    /// <param name="m">The parsed manifest.</param>
    /// <param name="dir">The directory it was read from.</param>
    /// <param name="bundled">True for the app-directory root (the directory name must be the id).</param>
    public static string? Validate(ModuleManifest m, string dir, bool bundled)
    {
        if (m.SchemaVersion < 1) return "unsupported schemaVersion " + m.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!WaveeExtensionKey.IsValid(m.Id))
            return "invalid module id '" + (m.Id ?? "") + "' (publisher.name, ASCII, <= 128 chars)";
        if (string.IsNullOrWhiteSpace(m.Version)) return "missing version";
        if (string.IsNullOrWhiteSpace(m.Entry)) return "missing entry";
        if (m.ProtocolVersion < MinProtocol || m.ProtocolVersion > MaxProtocol)
            return "protocolVersion " + m.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " outside the host range " + MinProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + ".." + MaxProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The entry must resolve INSIDE the module directory — a manifest is untrusted input and `..\..\cmd.exe` is
        // exactly the shape a path-traversal launch would take.
        if (!ResolvesInside(dir, m.Entry!)) return "entry '" + m.Entry + "' escapes the module directory";

        // The directory name is the id for a bundled module and for the user store's <id>\<version> layout: a manifest
        // that claims a different id could shadow another publisher's namespace.
        string expected = bundled ? LeafName(dir) : LeafName(ParentName(dir));
        if (expected.Length > 0 && !string.Equals(expected, m.Id, StringComparison.OrdinalIgnoreCase))
            return "directory '" + expected + "' does not match manifest id '" + m.Id + "'";

        return null;
    }

    static bool ResolvesInside(string dir, string entry)
    {
        if (Path.IsPathRooted(entry)) return false;
        if (entry.IndexOfAny(['\0']) >= 0) return false;
        try
        {
            string root = Path.GetFullPath(dir);
            string full = Path.GetFullPath(Path.Combine(root, entry));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string LeafName(string dir)
    {
        string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int i = trimmed.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    static string ParentName(string dir)
    {
        string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int i = trimmed.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return i > 0 ? trimmed[..i] : "";
    }

    /// <summary>One winner per id: highest compatible <c>protocolVersion</c>, then the highest version, and — on a dead
    /// tie — the user store over the bundled copy (that is what an install/update is FOR). The bundled copy is never
    /// removed from disk, so it stays the floor a failed update falls back to on the next discovery.</summary>
    static IReadOnlyList<InstalledModule> Rank(List<InstalledModule> candidates)
    {
        var best = new Dictionary<string, InstalledModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!best.TryGetValue(c.Id, out var cur)) { best[c.Id] = c; continue; }
            if (Beats(c, cur)) best[c.Id] = c;
        }

        var list = new List<InstalledModule>(best.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    static bool Beats(InstalledModule a, InstalledModule b)
    {
        if (a.Manifest.ProtocolVersion != b.Manifest.ProtocolVersion)
            return a.Manifest.ProtocolVersion > b.Manifest.ProtocolVersion;
        int v = CompareVersions(a.Version, b.Version);
        if (v != 0) return v > 0;
        return !a.Bundled && b.Bundled;
    }

    /// <summary>Dotted numeric version compare, tolerant of non-numeric tails ("1.2.0-beta" &lt; "1.2.0" is NOT claimed —
    /// the tail is compared ordinally after the numeric segments agree). Public for the catalog tests.</summary>
    /// <param name="a">Left version string.</param>
    /// <param name="b">Right version string.</param>
    public static int CompareVersions(string? a, string? b)
    {
        string x = a ?? "", y = b ?? "";
        int i = 0, j = 0;
        while (true)
        {
            bool xNum = TryTakeNumber(x, ref i, out int xn);
            bool yNum = TryTakeNumber(y, ref j, out int yn);
            if (!xNum && !yNum) return string.CompareOrdinal(x[Math.Min(i, x.Length)..], y[Math.Min(j, y.Length)..]);
            if (!xNum) return -1;
            if (!yNum) return 1;
            if (xn != yn) return xn < yn ? -1 : 1;
        }
    }

    static bool TryTakeNumber(string s, ref int i, out int value)
    {
        value = 0;
        if (i >= s.Length) return false;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            value = value > 100_000_000 ? value : (value * 10) + (s[i] - '0');
            i++;
        }

        if (i == start) return false;
        if (i < s.Length && s[i] == '.') i++;
        return true;
    }
}
