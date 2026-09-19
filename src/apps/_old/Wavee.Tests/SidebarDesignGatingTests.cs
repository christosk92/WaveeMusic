using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Wavee.Tests;

// §C6 + F.4.3 — the SELECTION UX's pure decisions: the one-time chooser's gate, the marker that closes it forever, the
// card values the three preview cards select through, and the rule that lights the "Customize sidebar" affordance.
//
// These are one-boolean-read / one-boolean-write decisions, which is exactly why they are worth pinning: a marker burned
// too early permanently denies the chooser to the fresh installs it exists for, and a marker never written turns a
// "one-time" dialog into a launch ritual. Neither failure is recoverable per install and neither is visible in a diff.
//
// Everything here drives the PRODUCTION types (SidebarDesignGating + SidebarBootstrap + SidebarDesignInfo, all
// source-included), never a copy of their rules.
public class SidebarDesignGatingTests : IDisposable
{
    readonly string _local = Path.Combine(Path.GetTempPath(), "wavee-sidebar-gating-tests", Guid.NewGuid().ToString("n"));

    public SidebarDesignGatingTests() => Directory.CreateDirectory(_local);

    public void Dispose()
    {
        try { Directory.Delete(_local, recursive: true); } catch (Exception) { }
    }

    /// <summary>An "existing install" witness: the account database the real probe looks for first.</summary>
    void WriteLibraryDb()
    {
        string d = Path.Combine(_local, "Wavee");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "library.db"), "sqlite");
    }

    // ── the gate (F.4.3: exactly one boolean read) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Chooser_ShowsWhenMarkerUnset()
    {
        var settings = new MemoryAppSettings();
        Assert.True(SidebarDesignGating.ShouldShowChooser(settings));
    }

    [Fact]
    public void Chooser_SuppressedOnceMarkerSet()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarOnboardingSeen, true);
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
    }

    [Fact]
    public void Chooser_GateIgnoresEverythingButTheMarker()
    {
        // The gate must NOT re-derive freshness (the bootstrap already did, before library.db existed). A marked install
        // with a Curated design and no data files is still suppressed; an unmarked install with every other signal
        // pointing at "existing" still shows — otherwise the two deciders can disagree and the chooser is lost.
        var marked = new MemoryAppSettings();
        marked.Set(WaveeSettings.SidebarOnboardingSeen, true);
        marked.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.Curated);
        Assert.False(SidebarDesignGating.ShouldShowChooser(marked));

        var unmarked = new MemoryAppSettings();
        unmarked.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.Classic);
        unmarked.Set(WaveeSettings.SidebarBootstrapVersion, SidebarBootstrap.TargetVersion);
        Assert.True(SidebarDesignGating.ShouldShowChooser(unmarked));
    }

    [Fact]
    public void Chooser_NoSettingsSeamNeverOpens()
    {
        // A host without a settings seam has nowhere to record the answer, so opening a ONE-TIME dialog there would show
        // it on every launch.
        Assert.False(SidebarDesignGating.ShouldShowChooser(null));
    }

    // ── the marker (every close path, exactly once, forever) ──────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_FlipsOnceThenIsIdempotent()
    {
        var settings = new MemoryAppSettings();
        Assert.True(SidebarDesignGating.MarkChooserSeen(settings));
        Assert.False(SidebarDesignGating.MarkChooserSeen(settings));
        Assert.False(SidebarDesignGating.MarkChooserSeen(settings));
        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));
    }

    [Fact]
    public void MarkSeen_ClosesTheChooserForever()
    {
        var settings = new MemoryAppSettings();
        Assert.True(SidebarDesignGating.ShouldShowChooser(settings));
        SidebarDesignGating.MarkChooserSeen(settings);
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
    }

    [Fact]
    public void MarkSeen_EveryCloseCauseLandsInTheSameState()
    {
        // "Use this layout", "Not now", Escape and a shutdown-time close are four call sites of the SAME write — the
        // dialog hangs it on the overlay handle's ClosedAction precisely so none of them can forget. Whatever design was
        // applied when the dialog closed survives untouched.
        foreach (var applied in new[] { SidebarDesign.Classic, SidebarDesign.LibraryV3, SidebarDesign.Curated })
        {
            var settings = new MemoryAppSettings();
            settings.Set(WaveeSettings.SidebarDesign, (int)applied);
            SidebarDesignGating.MarkChooserSeen(settings);
            Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
            Assert.Equal(applied, SidebarDesignGating.ActiveDesign(settings));
        }
    }

    [Fact]
    public void MarkSeen_NeverWritesTheDesign()
    {
        // The chooser answers "did you see it?", never "which one?" — the cards already applied the design through
        // SwitchDesign. A marker write that also touched sidebar.design would silently stomp a user who picked a card and
        // then pressed Escape.
        var settings = new MemoryAppSettings();
        SidebarDesignGating.MarkChooserSeen(settings);
        Assert.False(settings.WasWritten(WaveeSettings.SidebarDesign));
    }

    [Fact]
    public void MarkSeen_ToleratesNoSettingsSeam()
    {
        Assert.False(SidebarDesignGating.MarkChooserSeen(null));
    }

    // ── the bootstrap hand-off (the two installs that matter) ─────────────────────────────────────────────────────────

    [Fact]
    public void FreshInstall_SeesTheChooserOnceOnClassic()
    {
        var settings = new MemoryAppSettings();
        SidebarBootstrap.Run(settings, _local);

        Assert.True(SidebarDesignGating.ShouldShowChooser(settings));
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.ActiveDesign(settings));

        // …and having answered it (by any exit path), never again — including across a re-run of the bootstrap.
        SidebarDesignGating.MarkChooserSeen(settings);
        SidebarBootstrap.Run(settings, _local);
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
    }

    [Fact]
    public void ExistingInstall_NeverSeesTheChooserAndStaysClassic()
    {
        WriteLibraryDb();
        var settings = new MemoryAppSettings();
        SidebarBootstrap.Run(settings, _local);

        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.ActiveDesign(settings));
        Assert.False(settings.WasWritten(WaveeSettings.SidebarDesign));   // untouched, not written to 0
    }

    [Fact]
    public void ChangingDesignLaterNeverReArmsTheChooser()
    {
        var settings = new MemoryAppSettings();
        SidebarBootstrap.Run(settings, _local);
        SidebarDesignGating.MarkChooserSeen(settings);

        foreach (var design in SidebarDesignInfo.All)
        {
            settings.Set(WaveeSettings.SidebarDesign, SidebarDesignGating.IndexOf(design));
            Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
            Assert.Equal(design, SidebarDesignGating.ActiveDesign(settings));
        }
    }

    // ── the card values (one numbering: card index == persisted int == enum member) ────────────────────────────────────

    [Fact]
    public void CardValues_AreThePersistedInts()
    {
        Assert.Equal(0, SidebarDesignGating.IndexOf(SidebarDesign.Classic));
        Assert.Equal(1, SidebarDesignGating.IndexOf(SidebarDesign.LibraryV3));
        Assert.Equal(2, SidebarDesignGating.IndexOf(SidebarDesign.Curated));
    }

    [Fact]
    public void CardValues_RoundTripEveryDesign()
    {
        foreach (var design in SidebarDesignInfo.All)
            Assert.Equal(design, SidebarDesignGating.FromIndex(SidebarDesignGating.IndexOf(design)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    public void CardValues_OutOfRangeFallsBackToClassic(int value)
    {
        // A hand-edited settings file or a document from a future build must land on today's sidebar, never on a
        // surprise redesign and never on a throw.
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.FromIndex(value));
    }

    [Fact]
    public void ActiveDesign_CoercesAnUnknownPersistedValue()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarDesign, 7);
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.ActiveDesign(settings));
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.ActiveDesign(null));
    }

    // ── the customize rule (the Settings link row + the chooser's follow-up) ──────────────────────────────────────────

    [Fact]
    public void Customize_OnlyOfferedForCurated()
    {
        foreach (var design in SidebarDesignInfo.All)
        {
            bool curated = design == SidebarDesign.Curated;
            Assert.Equal(curated, SidebarDesignGating.OffersCustomize(design));
            Assert.Equal(curated, SidebarDesignGating.CanCustomize(design));
        }
    }

    // ── the card copy ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CardKeys_AreDistinctAndPresentForEveryDesign()
    {
        // Three cards, three titles, three subtitles, no shared key — a duplicated key is how two cards end up reading
        // the same name and the picker becomes unusable in exactly the dialog nobody re-tests.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var design in SidebarDesignInfo.All)
        {
            string title = SidebarDesignGating.TitleKey(design);
            string sub = SidebarDesignGating.SubtitleKey(design);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(sub));
            Assert.NotEqual(title, sub);
            Assert.True(keys.Add(title), $"duplicate title key: {title}");
            Assert.True(keys.Add(sub), $"duplicate subtitle key: {sub}");
        }
        Assert.Equal(SidebarDesignInfo.Count * 2, keys.Count);
    }

    [Fact]
    public void CardKeys_LiveInTheSidebarDesignNamespace()
    {
        // The picker's copy is `sidebar.design.*` (§C7) — the same namespace the quick layout menu's radio rows read, so
        // a translator renaming one design renames it in both surfaces.
        foreach (var design in SidebarDesignInfo.All)
        {
            Assert.StartsWith("sidebar.design.", SidebarDesignGating.TitleKey(design), StringComparison.Ordinal);
            Assert.StartsWith("sidebar.design.", SidebarDesignGating.SubtitleKey(design), StringComparison.Ordinal);
        }
    }
}
