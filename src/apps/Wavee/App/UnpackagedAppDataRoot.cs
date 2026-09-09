using System;
using System.IO;

namespace Wavee;

/// <summary>The ONE unpackaged-profile decision. Every file that today lands under
/// <c>%LOCALAPPDATA%\Wavee</c> (settings publisher, <c>sidebar-layout.json</c>, logs, image cache, …) must
/// resolve through here — a scatter of folder-name literals is how a <c>--fake</c> session once wrote
/// FakeData pin titles into the real user's layout document.
///
/// <para>WHY A SEPARATE FILE. The folder name is unrecoverable per install if it is wrong: fake and real
/// sharing a root poisons display caches; a packaged (MSIX) run must keep using the same real folder
/// name so OS virtualization into the package LocalCache is unchanged. This class is engine-free
/// (System only) so <c>SidebarAppDataRootTests</c> drives the REAL function, the SetupGating pattern.</para>
///
/// <para>Packaged runs are unaffected: this does not change the path scheme, only the folder name for an
/// unpackaged <c>--fake</c> process. MSIX still redirects <c>%LOCALAPPDATA%</c> into the package cache.</para></summary>
static class UnpackagedAppDataRoot
{
    public const string RealFolderName = "Wavee";
    public const string FakeFolderName = "Wavee-fake";
    public const string MusicFolderName = "WaveeMusic";

    static bool _fake;

    /// <summary>Latch the process-wide variant. Called ONCE from <c>Program.Main</c> before settings, logs,
    /// or any <c>DefaultPath</c> is built — the same "before anyone can ask" rule as
    /// <c>DeveloperMode.Load</c>.</summary>
    public static void Configure(bool fake) => _fake = fake;

    /// <summary>True after <see cref="Configure"/> saw <c>--fake</c>. Readers that already have
    /// <c>Services.UseRealBackend</c> should keep using that; this exists so a path helper never has to
    /// reach Services.</summary>
    public static bool Fake => _fake;

    /// <summary>The one decision: a fake process writes under <see cref="FakeFolderName"/> so it cannot
    /// touch the real profile.</summary>
    public static string FolderName(bool fake) => fake ? FakeFolderName : RealFolderName;

    /// <summary>Root directory for an unpackaged run: <c>{localApplicationData}\{FolderName}</c>.</summary>
    public static string Resolve(string localApplicationData, bool fake)
        => Path.Combine(localApplicationData, FolderName(fake));

    public static string CurrentFolderName => FolderName(_fake);

    /// <summary>The live unpackaged root for this process. Packaged virtualization still applies on top
    /// of whatever <c>LocalApplicationData</c> the OS hands back.</summary>
    public static string Current => Resolve(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _fake);

    /// <summary>A file under the live root (logs, lyrics, modules, …).</summary>
    public static string UnderCurrent(params string[] relative)
        => Combine(Current, relative);

    /// <summary>A file under <see cref="MusicFolderName"/> on the live root (history / layout / session / play-log).</summary>
    public static string MusicFile(string fileName)
        => Path.Combine(Current, MusicFolderName, fileName);

    /// <summary>The six paint-before-data / session stores a --fake run must never share with a real profile.
    /// Pure: takes LocalApplicationData + the fake flag, never the process latch.</summary>
    public static string[] KnownStorePaths(string localApplicationData, bool fake)
    {
        string root = Resolve(localApplicationData, fake);
        return
        [
            Combine(root, MusicFolderName, "history.json"),
            Combine(root, MusicFolderName, "home-layout.json"),
            Combine(root, MusicFolderName, "sidebar-layout.json"),
            Combine(root, MusicFolderName, "play-log.json"),
            Combine(root, MusicFolderName, "session.json"),
            Combine(root, "Cache", "cover-colors.json"),
        ];
    }

    static string Combine(string root, params string[] relative)
    {
        if (relative.Length == 0) return root;
        if (relative.Length == 1) return Path.Combine(root, relative[0]);
        if (relative.Length == 2) return Path.Combine(root, relative[0], relative[1]);
        string[] parts = new string[relative.Length + 1];
        parts[0] = root;
        Array.Copy(relative, 0, parts, 1, relative.Length);
        return Path.Combine(parts);
    }
}
