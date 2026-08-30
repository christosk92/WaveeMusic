using System;
using System.IO;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The module page's two driven gates: the http(s)-only shell-launch guard (a module supplies the string it would
/// launch, so it is exercised for real), and the loc-key parity check against the base catalog on disk.
///
/// <para>Both assert VALUES. The class was once called <c>ModulePageSourceGateTests</c> and read
/// <c>ModulePage.cs</c>'s own text — pinning a comment banner and a set of identifiers — which passes for a file
/// nobody edits and blocks every file anyone does: the watch-page rewrite would have failed it while changing nothing
/// the user can see. The layout DECISIONS it was reaching for now live in the pure <c>WatchPageModel</c> and are
/// asserted as answers in <c>WatchPageModelTests</c>, which is the shape this repo's gates take.</para>
/// </summary>
public class ModulePageGateTests
{
    // ── the http(s)-only launch guard (driven, not scanned) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("http://example.com/")]
    [InlineData("https://usher.ttvnw.net/api/v2/channel/hls/x.m3u8?sig=1&token=2")]
    public void AWebLink_IsOpenable(string url) => Assert.True(ShellOpen.IsWebUrl(url));

    /// <summary>The strings that must NEVER reach <c>UseShellExecute</c>. A module's page document crosses a pipe as
    /// DATA; handing an arbitrary member of it to the shell launches file paths, UNC shares, executables and any
    /// registered protocol handler. The guard is a whitelist of two schemes, not a blacklist of the bad ones.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("wavee://play?link=x")]
    [InlineData("ftp://example.com/a")]
    [InlineData("https://")]
    [InlineData("not a url at all")]
    public void AnythingElse_IsRefusedAndOpensNothing(string? url)
    {
        Assert.False(ShellOpen.IsWebUrl(url));
        Assert.False(ShellOpen.OpenUrl(url));   // false = the launch was never attempted
    }

    // ── the strings ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocKeys_ForTheModulePage_ExistInTheBaseCatalog()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        Assert.True(doc.RootElement.TryGetProperty("modulePage", out var block),
            "the base catalog has no \"modulePage\" block");

        foreach (string key in new[]
                 {
                     Strings.ModulePage.Title, Strings.ModulePage.Error, Strings.ModulePage.OpenInBrowser,
                     // The WATCH layout's inert state capsule: the play capsule is REPLACED by it while this page's
                     // entity is the item in the bar, so it is user-facing copy and needs a real key like any other.
                     Strings.ModulePage.Playing,
                 })
        {
            Assert.StartsWith("modulePage.", key, StringComparison.Ordinal);
            Assert.True(block.TryGetProperty(key["modulePage.".Length..], out _), key + " is not in the base catalog");
        }

        // The parameterized one is a METHOD (the loc generator's shape for a key carrying {name}) rather than a key
        // constant, so it is pinned by SHAPE — it resolves to something, and its key is in the catalog. Never by
        // localized copy: no catalogue is loaded in this process, so the method answers with its key marker.
        Assert.NotEmpty(Strings.ModulePage.OpenOn("YouTube"));
        Assert.True(block.TryGetProperty("openOn", out var openOn), "modulePage.openOn is not in the base catalog");
        Assert.Contains("{name}", openOn.GetString() ?? "", StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    static string? FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }
}
