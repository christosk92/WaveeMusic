using System;
using System.IO;
using System.Text.Json;
using Wavee;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Actions;

// Part 5 of the playback-modules pass — the "Play ▸ Link…" surface's DECISIONS. Everything the submenu and the dialog
// decide is engine-free and localization-free (PlayLinkActions takes its localized strings as arguments), so the whole
// surface is pinned here against production code rather than against a screenshot:
//   • what the field is prefilled with, and what the clipboard must look like to earn a prefill,
//   • which installed modules earn a menu row, what that row is called and what its dialog asks for,
//   • the status sentence, incl. the two segments that DROP rather than print a placeholder,
//   • which failure is a toast (with the module's own words) and which is a status line the card stays open on,
//   • the module's declared form → the app's play FORM, incl. why audio is Default and not Audio.
public class PlayLinkActionsTests
{
    static ModuleManifest Manifest(string id = "wavee.youtube", string display = "YouTube",
        string[]? capabilities = null, ModuleMenu? menu = null)
        => new(1, id, "1.0.0", display, "wavee", 1, "Wavee.Module.YouTube.exe",
               capabilities ?? ["playback", "match"], ["youtube.com", "youtu.be"], menu);

    // ── the input ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  https://youtu.be/abc  ", "https://youtu.be/abc")]
    [InlineData("https://youtu.be/abc\r\n", "https://youtu.be/abc")]
    public void Normalize_TrimsWhatTheRouterWillSee(string? input, string expected)
        => Assert.Equal(expected, PlayLinkActions.Normalize(input));

    /// <summary>The trim is not cosmetic: a module's url-pattern prefilter is a plain substring test over the input, and
    /// a link copied off a chat line arrives with a trailing newline. Untrimmed, that input reaches every module.</summary>
    [Fact]
    public void CanSubmit_IsFalseForWhitespaceOnly()
    {
        Assert.False(PlayLinkActions.CanSubmit(null));
        Assert.False(PlayLinkActions.CanSubmit("   \r\n"));
        Assert.True(PlayLinkActions.CanSubmit("  x  "));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg", true)]
    [InlineData("http://stream.example.org:8000/live", true)]
    [InlineData("HTTPS://TWITCH.TV/somebody", true)]
    [InlineData("  https://youtu.be/abc  ", true)]           // trimmed first
    [InlineData("youtube.com/watch?v=abc", false)]           // no scheme — not clipboard-prefill material
    [InlineData(@"C:\music\track.mp3", false)]
    [InlineData("https://a b", false)]                        // inner whitespace: a sentence, not a link
    [InlineData("https://", false)]
    [InlineData(null, false)]
    public void LooksLikeUrl_IsTheClipboardPrefillTest(string? text, bool expected)
        => Assert.Equal(expected, PlayLinkActions.LooksLikeUrl(text));

    /// <summary>A clipboard holding a link seeds the field (the whole gesture is "copy a link, play it"); a clipboard
    /// holding anything else is left alone rather than pasted as junk the user has to clear first.</summary>
    [Fact]
    public void PrefillFrom_SeedsOnlyLinks()
    {
        Assert.Equal("https://youtu.be/abc", PlayLinkActions.PrefillFrom("  https://youtu.be/abc "));
        Assert.Equal("", PlayLinkActions.PrefillFrom("Daft Punk – Around the World"));
        Assert.Equal("", PlayLinkActions.PrefillFrom(null));
    }

    // ── the menu ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Capabilities are DECLARED, never probed: a module without <c>match</c> is skipped before its process is
    /// ever spawned, so it can neither earn a menu row nor be asked about a pasted link.</summary>
    [Fact]
    public void DeclaresMatch_ReadsTheManifest()
    {
        Assert.True(PlayLinkActions.DeclaresMatch(Manifest(capabilities: ["playback", "match"])));
        Assert.True(PlayLinkActions.DeclaresMatch(Manifest(capabilities: ["Match"])));   // case-insensitive
        Assert.False(PlayLinkActions.DeclaresMatch(Manifest(capabilities: ["playback"])));
        Assert.False(PlayLinkActions.DeclaresMatch(Manifest(capabilities: [])));
        Assert.False(PlayLinkActions.DeclaresMatch(null));
    }

    [Fact]
    public void MenuLabel_PrefersTheManifestsOwnCopy()
        => Assert.Equal("YouTube…",
            PlayLinkActions.MenuLabel(Manifest(menu: new ModuleMenu("YouTube…", "Paste a YouTube link"))));

    /// <summary>Without authored copy the display name gets the dialog-opening ellipsis, so the contributed row still
    /// reads like the "File…" / "Link…" rows above it.</summary>
    [Fact]
    public void MenuLabel_FallsBackToTheDisplayNamePlusAnEllipsis()
    {
        Assert.Equal("Twitch…", PlayLinkActions.MenuLabel(Manifest(display: "Twitch", menu: null)));
        Assert.Equal("Twitch…", PlayLinkActions.MenuLabel(Manifest(display: "Twitch…", menu: null)));
        Assert.Equal("Radio…", PlayLinkActions.MenuLabel(Manifest(display: "Radio",
            menu: new ModuleMenu("   ", "x"))));                                     // blank label = no label
        Assert.Equal("wavee.mystery…", PlayLinkActions.MenuLabel(Manifest(id: "wavee.mystery", display: "")));
    }

    /// <summary>A module opened BY NAME gets to say what it wants; the generic "Link…" row, and a module with no
    /// authored placeholder, both fall back to the surface's own.</summary>
    [Fact]
    public void PlaceholderFor_LetsTheNamedModuleSpeak()
    {
        const string generic = "Paste a YouTube, Twitch or radio stream link";
        Assert.Equal("Paste a YouTube link",
            PlayLinkActions.PlaceholderFor(Manifest(menu: new ModuleMenu("YouTube…", "Paste a YouTube link")), generic));
        Assert.Equal(generic, PlayLinkActions.PlaceholderFor(Manifest(menu: null), generic));
        Assert.Equal(generic, PlayLinkActions.PlaceholderFor(Manifest(menu: new ModuleMenu("X…", "  ")), generic));
        Assert.Equal(generic, PlayLinkActions.PlaceholderFor(null, generic));
    }

    // ── the status line ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MatchStatus_IsModuleThenTitleThenLive()
        => Assert.Equal("YouTube · Claude FM · LIVE",
            PlayLinkActions.MatchStatus("YouTube", "Claude FM", isLive: true, "LIVE"));

    /// <summary>Each segment DROPS when it is not a fact, rather than printing a placeholder — a module that matched on
    /// the url shape alone has no title yet, and a finite stream is not live.</summary>
    [Fact]
    public void MatchStatus_DropsTheSegmentsItCannotState()
    {
        Assert.Equal("YouTube · Claude FM", PlayLinkActions.MatchStatus("YouTube", "Claude FM", false, "LIVE"));
        Assert.Equal("YouTube · LIVE", PlayLinkActions.MatchStatus("YouTube", null, true, "LIVE"));
        Assert.Equal("YouTube · LIVE", PlayLinkActions.MatchStatus("YouTube", "   ", true, "LIVE"));
        Assert.Equal("YouTube", PlayLinkActions.MatchStatus("YouTube", "", false, "LIVE"));
        Assert.Equal("Claude FM", PlayLinkActions.MatchStatus("", "Claude FM", false, "LIVE"));
        Assert.Equal("", PlayLinkActions.MatchStatus("", null, false, "LIVE"));
    }

    [Fact]
    public void MatchStatus_TrimsEverySegment()
        => Assert.Equal("YouTube · Claude FM", PlayLinkActions.MatchStatus("  YouTube ", " Claude FM  ", false, "LIVE"));

    // ── failure ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Nobody owns this" is not a toast — it is the status line's own answer, and the card stays open so the
    /// user can fix the link they just pasted.</summary>
    [Fact]
    public void IsNotOwned_SeparatesTheStatusLineFromTheToast()
    {
        Assert.True(PlayLinkActions.IsNotOwned(new ModuleException(ModuleErrorCode.NotOwned, "no")));
        Assert.False(PlayLinkActions.IsNotOwned(new ModuleException(ModuleErrorCode.Offline, "no")));
        Assert.False(PlayLinkActions.IsNotOwned(new InvalidOperationException("no")));
        Assert.False(PlayLinkActions.IsNotOwned(null));
    }

    [Fact]
    public void IsCancelled_IsSilent()
    {
        Assert.True(PlayLinkActions.IsCancelled(new OperationCanceledException()));
        Assert.Equal("", PlayLinkActions.ErrorText(new OperationCanceledException(), "fallback"));
        Assert.Equal("", PlayLinkActions.ErrorText(null, "fallback"));
    }

    /// <summary>The module wrote the message and is the only party that knows why, so it is shown verbatim. Everything
    /// else — including a module that threw with nothing to say — falls back to the surface's own sentence rather than
    /// leaking an exception shape at the user.</summary>
    [Theory]
    [InlineData(ModuleErrorCode.Unavailable, "YouTube is blocking this network", "YouTube is blocking this network")]
    [InlineData(ModuleErrorCode.NeedsAuth, "  subscriber-only  ", "subscriber-only")]
    [InlineData(ModuleErrorCode.Offline, "", "fallback")]
    [InlineData(ModuleErrorCode.NotOwned, "the module said no", "fallback")]
    public void ErrorText_PrefersTheModulesOwnWords(ModuleErrorCode code, string message, string expected)
        => Assert.Equal(expected, PlayLinkActions.ErrorText(new ModuleException(code, message), "fallback"));

    [Fact]
    public void ErrorText_FallsBackForANonModuleFailure()
        => Assert.Equal("boom", PlayLinkActions.ErrorText(new InvalidOperationException("boom"), "fallback"));

    // ── the hand-off ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Video is an explicit one-play "show me this"; audio is <c>Default</c> and NOT <c>Audio</c>, because
    /// <c>Audio</c> would turn a standing video intent off for the rest of the queue — a radio stream has no opinion
    /// about the surface, it simply has no video.</summary>
    [Fact]
    public void FormFor_MapsVideoToVideoAndAudioToDefault()
    {
        Assert.Equal(Wavee.Core.MediaForm.Video, PlayLinkActions.FormFor(MediaForm.Video));
        Assert.Equal(Wavee.Core.MediaForm.Default, PlayLinkActions.FormFor(MediaForm.Audio));
        Assert.NotEqual(Wavee.Core.MediaForm.Audio, PlayLinkActions.FormFor(MediaForm.Audio));
    }

    // ── localization ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every key the Play surfaces resolve exists in the base catalog. The generator would not catch a key
    /// renamed in json only (it regenerates the const and the call site keeps compiling), so the check is against the
    /// file itself.</summary>
    [Fact]
    public void LocKeys_ForThePlaySurfaces_ExistInTheBaseCatalog()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        Assert.True(doc.RootElement.TryGetProperty("play", out var block), "the base catalog has no \"play\" block");

        foreach (string key in new[]
                 {
                     Strings.Play.Menu, Strings.Play.File, Strings.Play.Link, Strings.Play.Title,
                     Strings.Play.Placeholder, Strings.Play.Start, Strings.Play.LookingUp,
                     Strings.Play.NoOwner, Strings.Play.Failed, Strings.Play.Live,
                 })
        {
            Assert.StartsWith("play.", key, StringComparison.Ordinal);
            Assert.True(block.TryGetProperty(key["play.".Length..], out _), key + " is not in the base catalog");
        }
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
