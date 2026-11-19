// ── Theming/Composition/ThemePreviews.cs — a theme picker card's still: where it lives, how a card draws it, the release check ─────────
//
// Every theme names its picker stills in its manifest (`ThemePreview`: one for light, one for dark), as paths under `<exe>/assets/`
// (assets/themes/<id>-<mode>.jpg). Each still is that theme's Home, rendered headless with the fake catalogue and composed from the
// window's TOP-LEFT corner (the window inset on the theme's backdrop, bleeding off the right and the bottom) by
// `tools/settings-explain/previews.py`, which regenerates all six. A card draws the still for the app mode it is in (`R.IsLight`).
//
//   · THE CROP: the cards are not one shape (16:9 at WinUI's fluid cards, 2.2 : 1 at Classic's, 1.4 : 1 at Zune's tile, 48 x 32 at WinUI's Small
//     row), so a card covers its box from the still's top-left corner (`Layer`): every shape keeps the corner, the navigation and the page's
//     title, and loses only window that bleeds off anyway.
//   · THE FORMAT: JPEG (or PNG), which Windows decodes everywhere. A WebP needs the WebP Image Extension, and a machine without it would draw
//     nothing at all, so a still is never a WebP.
//   · THE RELEASE CHECK: `Wavee.exe --check-theme-previews <assets>` lists every still a shipped theme names that is missing, not a JPEG or
//     PNG, or over the size budget (`ThemePreviewRules`), and exits 1 when there is one. The Wavee project runs it after a Release build
//     (Wavee.csproj, target `WaveeCheckThemePreviews`, MSBuild property `WaveeThemePreviewCheck`, on by default). Every shipped theme
//     therefore has its stills, and a card draws no stand-in: until the decode lands (one frame or two) the card's plate shows, then the
//     still fades in over it, in a box of the same size.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming;

/// <summary>The theme picker stills' size budget: shipped in the package, so small and bounded.</summary>
public static class ThemePreviewRules
{
    /// <summary>At most this many bytes per still.</summary>
    public const long MaxBytes = 64 * 1024;
}

/// <summary>A theme picker card's still (see the file header).</summary>
public static class ThemePreviews
{
    static readonly Dictionary<string, string?> s_paths = new(StringComparer.Ordinal);

    /// <summary>The full path of <paramref name="preview"/>'s still for the app mode (<paramref name="light"/>), or null when it is not next to
    /// the exe. Each path is looked up once per process (the stills ship with the build and never change under it).</summary>
    public static string? PathOf(ThemePreview preview, bool light)
    {
        string asset = light ? preview.LightAsset : preview.DarkAsset;
        if (asset.Length == 0) return null;
        if (s_paths.TryGetValue(asset, out string? known)) return known;
        string? path = null;
        try
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "assets", asset.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) path = candidate;
        }
        catch (Exception) { path = null; }
        s_paths[asset] = path;
        return path;
    }

    /// <summary>The still as a card's layer: it covers the card's box from the still's top-left corner (the window's corner, where the
    /// navigation and the page's title are), over a transparent placeholder, so the card's plate shows until the decode lands. The card adds
    /// its box (<c>Height</c> or <c>AspectRatio</c>), its decode width and its fade.</summary>
    public static ImageEl Layer(string path) => new()
    {
        Source = path, Fit = ImageFit.Cover, FocusX = 0f, FocusY = 0f, FocusFrameX = 0f, FocusFrameY = 0f,
        Placeholder = ColorF.Transparent, AlignSelf = FlexAlign.Stretch,
    };

    // ── the release check (Wavee.csproj target WaveeCheckThemePreviews, gated by WaveeThemePreviewCheck) ───────────────────────────────

    /// <summary>The switch of the release check.</summary>
    public const string CheckSwitch = "--check-theme-previews";

    /// <summary><c>Wavee.exe --check-theme-previews &lt;assets&gt;</c>: both stills of every manifest <paramref name="manifests"/> answers must
    /// exist under <c>&lt;assets&gt;</c>, be a JPEG or a PNG and fit the budget. Prints one line per problem; the code is 1 when there is one,
    /// 64 on a bad command line. Reads only the manifests and the folder: nothing boots.</summary>
    public static bool TryRunCheck(string[] args, Func<IReadOnlyList<ThemeManifest>> manifests, out int code)
    {
        code = 0;
        int at = Array.IndexOf(args, CheckSwitch);
        if (at < 0) return false;
        if (at + 1 >= args.Length)
        {
            Console.Error.WriteLine(CheckSwitch + " needs the assets folder (src/apps/Wavee/assets).");
            code = 64;
            return true;
        }
        string root = args[at + 1];
        var problems = new List<string>();
        int needed = 0;
        long total = 0;
        foreach (ThemeManifest manifest in manifests())
            foreach (string asset in new[] { manifest.Preview.LightAsset, manifest.Preview.DarkAsset })
            {
                needed++;
                if (asset.Length == 0) { problems.Add("missing  (no path)  (" + manifest.Id + ")"); continue; }
                string full = Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { problems.Add("missing  " + asset + "  (" + manifest.Id + ")"); continue; }
                long bytes = new FileInfo(full).Length;
                total += bytes;
                if (!DecodesEverywhere(full))
                    problems.Add("format   " + asset + "  is not a JPEG or a PNG (a WebP needs an extension Windows may not have)");
                if (bytes > ThemePreviewRules.MaxBytes)
                    problems.Add("too big  " + asset + "  " + bytes + " bytes (budget " + ThemePreviewRules.MaxBytes + ")");
            }
        foreach (string problem in problems) Console.Out.WriteLine("theme previews: " + problem);
        Console.Out.WriteLine("theme previews: " + needed + " needed, " + problems.Count + " problem(s), " + total + " bytes");
        code = problems.Count == 0 ? 0 : 1;
        return true;
    }

    /// <summary>The file starts as a JPEG (FF D8 FF) or a PNG (89 'P' 'N' 'G') does: the stills every Windows decodes without an extension.</summary>
    static bool DecodesEverywhere(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
        bool jpeg = head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        bool png = head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G';
        return jpeg || png;
    }
}
