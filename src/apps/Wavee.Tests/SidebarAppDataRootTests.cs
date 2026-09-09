using System.IO;
using Xunit;

namespace Wavee.Tests;

/// <summary>The unpackaged-profile folder decision (<see cref="UnpackagedAppDataRoot"/>). Engine-free: the test
/// drives the REAL function, the SetupGating / AppUpdateToasts pattern. A --fake process must not share
/// %LOCALAPPDATA%\Wavee with the real session — that is how FakeData titles landed in sidebar-layout.json.</summary>
public class SidebarAppDataRootTests
{
    [Fact]
    public void RealRun_UsesWaveeFolder()
    {
        Assert.Equal("Wavee", UnpackagedAppDataRoot.FolderName(fake: false));
        Assert.Equal(
            Path.Combine("X:\\Local", "Wavee"),
            UnpackagedAppDataRoot.Resolve("X:\\Local", fake: false));
    }

    [Fact]
    public void FakeRun_UsesIsolatedWaveeFakeFolder()
    {
        Assert.Equal("Wavee-fake", UnpackagedAppDataRoot.FolderName(fake: true));
        Assert.Equal(
            Path.Combine("X:\\Local", "Wavee-fake"),
            UnpackagedAppDataRoot.Resolve("X:\\Local", fake: true));
    }

    [Fact]
    public void FakeAndReal_NeverShareARoot()
    {
        string local = Path.Combine("C:", "Users", "me", "AppData", "Local");
        string real = UnpackagedAppDataRoot.Resolve(local, fake: false);
        string fake = UnpackagedAppDataRoot.Resolve(local, fake: true);
        Assert.NotEqual(real, fake);
        Assert.False(real.StartsWith(fake, System.StringComparison.Ordinal));
        Assert.False(fake.StartsWith(real + Path.DirectorySeparatorChar, System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownStorePaths_AllLiveUnderTheResolvedRoot(bool fake)
    {
        string local = Path.Combine("D:", "AppData", "Local");
        string root = UnpackagedAppDataRoot.Resolve(local, fake);
        string[] paths = UnpackagedAppDataRoot.KnownStorePaths(local, fake);

        Assert.Equal(6, paths.Length);
        Assert.Contains(Path.Combine(root, UnpackagedAppDataRoot.MusicFolderName, "history.json"), paths);
        Assert.Contains(Path.Combine(root, UnpackagedAppDataRoot.MusicFolderName, "home-layout.json"), paths);
        Assert.Contains(Path.Combine(root, UnpackagedAppDataRoot.MusicFolderName, "sidebar-layout.json"), paths);
        Assert.Contains(Path.Combine(root, UnpackagedAppDataRoot.MusicFolderName, "play-log.json"), paths);
        Assert.Contains(Path.Combine(root, UnpackagedAppDataRoot.MusicFolderName, "session.json"), paths);
        Assert.Contains(Path.Combine(root, "Cache", "cover-colors.json"), paths);

        string prefix = root + Path.DirectorySeparatorChar;
        foreach (string path in paths)
        {
            Assert.StartsWith(prefix, path, System.StringComparison.Ordinal);
            Assert.DoesNotContain(
                UnpackagedAppDataRoot.FolderName(!fake) + Path.DirectorySeparatorChar,
                path,
                System.StringComparison.Ordinal);
        }
    }
}
