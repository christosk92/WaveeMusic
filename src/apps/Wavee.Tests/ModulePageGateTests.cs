// The module page's two driven gates — ported from 0.2.9 Wavee.Tests/ModulePageGateTests.cs: the http(s)-only launch
// guard every module-supplied url passes through (`Actions.PlayLinkRules.IsWebUrl`, exercised through the page's own
// `Pages.OpenUrlOf`), and the loc-key parity check against the base catalog on disk. The Store deep-link facts of the
// 0.2.9 file are the update owner's (`Update.StorePageUrl`), not the module page's, and are not restated here.

using System.Text.Json;
using Wavee.Sdk;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

public class ModulePageGateTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("http://example.com/")]
    [InlineData("https://usher.ttvnw.net/api/v2/channel/hls/x.m3u8?sig=1&token=2")]
    public void AWebLink_IsOpenable(string url)
    {
        Assert.True(Actions.PlayLinkRules.IsWebUrl(url));
        Assert.Equal(url, Pages.OpenUrlOf(DocOpening(url)));
    }

    /// <summary>The strings that must NEVER reach the shell. A page document crosses a pipe as DATA; handing an arbitrary
    /// member of it to the shell launches file paths, UNC shares, executables and any registered protocol handler.</summary>
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
    public void AnythingElse_IsRefused_AndNeverBecomesTheEscapeHatch(string? url)
    {
        Assert.False(Actions.PlayLinkRules.IsWebUrl(url));
        Assert.Null(Pages.OpenUrlOf(DocOpening(url)));
    }

    static ModulePageDoc DocOpening(string? url)
        => new(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity, null,
            [new PageAction("open", PageAction.KindOpenUrl, "Open", null, url, false)], [], null);

    [Fact]
    public void LocKeys_ForTheModulePage_ExistInTheBaseCatalog()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        Assert.True(doc.RootElement.TryGetProperty("modulePage", out var block), "the base catalog has no \"modulePage\" block");

        foreach (string key in new[] { Strings.ModulePage.Title, Strings.ModulePage.Error, Strings.ModulePage.OpenInBrowser, Strings.ModulePage.Playing })
        {
            Assert.StartsWith("modulePage.", key, StringComparison.Ordinal);
            Assert.True(block.TryGetProperty(key["modulePage.".Length..], out _), key + " is not in the base catalog");
        }

        // The parameterized one is a METHOD, pinned by shape: it resolves, and its key carries {name} in the catalog.
        Assert.NotEmpty(Strings.ModulePage.OpenOn("YouTube"));
        Assert.True(block.TryGetProperty("openOn", out var openOn), "modulePage.openOn is not in the base catalog");
        Assert.Contains("{name}", openOn.GetString() ?? "", StringComparison.Ordinal);

        // The watch page's LIVE badge and the shared play-failure copy the page's toasts use.
        Assert.True(doc.RootElement.TryGetProperty("play", out var play));
        foreach (string leaf in new[] { "live", "failed", "tryAgain", "placeholder" })
            Assert.True(play.TryGetProperty(leaf, out _), "play." + leaf + " is not in the base catalog");
    }

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
