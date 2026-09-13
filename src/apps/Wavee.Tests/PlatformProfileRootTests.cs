// ── Wavee.Tests/PlatformProfileRootTests.cs ────────────────────────────────────────────────────────────────────────
// Platform.ProfileRoot: the `--profile <dir>` folder every profile path hangs off (headless plan §3.6). The default
// stays the per-user Wavee folder (not exercised here: reading LocalFolder creates it, and a test must never leave a
// real %LOCALAPPDATA%\Wavee behind); a named root replaces it for store.json, logs and the rest.

using System;
using System.IO;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class PlatformProfileRootTests
{
    [Fact]
    public void A_named_profile_root_carries_every_profile_path()
    {
        string saved = Platform.ProfileRoot;
        string root = Path.Combine(Path.GetTempPath(), "wavee-profile-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            Platform.ProfileRoot = root;
            Assert.Equal(root, Platform.LocalFolder);
            Assert.True(Directory.Exists(root));
            Assert.Equal(Path.Combine(root, "store.json"), Platform.StorePath);
            Assert.Equal(Path.Combine(root, "logs"), Platform.LogFolder);
        }
        finally
        {
            Platform.ProfileRoot = saved;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
