// ── Wavee.Tests/SidebarDesignTests.cs — the design vocabulary, the chooser gate, Classic's locked document, the ────
// nav band, and Library V3 (metrics/search/chips/order/document) ────────────────────────────────────────────────
//
// Ported from the 0.2.9 suite (src/apps/_old/Wavee.Tests/SidebarModeStateTests.cs, SidebarDesignGatingTests.cs,
// SidebarBuiltInDocumentTests.cs, SidebarNavBandTests.cs, LibraryV3ChipStripTests.cs, LibraryV3SearchRulesTests.cs,
// LibraryV3DocumentTests.cs) against the 0.3 production code in src/apps/Wavee/Shell/Sidebar.Modes.cs (+ the
// per-design pane bounds on Sidebar.cs's SidebarPaneBounds, + the settings seam on Platform/Platform.cs). Types
// kept their 0.2.9 names and dropped the `Wavee.Core.Sidebar` qualifier — everything lives directly under `Wavee`.
//
// 0.3 CHANGE carried through every fact below: the per-design width tiers, the 1400/1800 breakpoints, the 24-DIP
// shrink hysteresis and the [180,460] clamp are NOT on a shell type any more — 0.2.9's `ShellResponsiveLayout` is
// gone. They all live on `SidebarPaneBounds` (Shell/Sidebar.cs), which every design's tier table
// (`SidebarDesignInfo.Tiers`) is built on top of. Every fact that used to read `ShellResponsiveLayout.*` now reads
// `SidebarPaneBounds.*` instead — same numbers, one fewer owner.
//
// `MemoryAppSettings` (the in-memory `IAppSettings` fake) is NOT redeclared here — it already exists in this
// project (Wavee.Tests/PlatformTests.cs), in this same namespace, and a second declaration would collide. Reusing
// it satisfies the "never write through the real Platform.Settings / registry" rule exactly as declaring a private
// one inline would.
//
// ACCESSIBILITY NOTE (resolved): every type this file needs — SidebarDesignGating, SidebarNavBandModel /
// SidebarNavBandTile, LibraryV3Metrics, LibraryV3Labels, LibraryV3SearchRules, LibraryV3View, LibraryV3Window,
// LibraryV3Document, LibraryV3DocState — was briefly `internal` (no access modifier) in Sidebar.Modes.cs, which
// this test project (a `ProjectReference`, not a source-include, per Wavee.Tests.csproj) could not have reached
// without an `InternalsVisibleTo` from Wavee.csproj that did not exist. That has since been fixed at the source
// (all now `public`), so every region below is ported in full.
//
// TWO THINGS DROPPED, not weakened — reasons at their would-be location:
//   · SidebarDesignGatingTests' bootstrap hand-off facts (FreshInstall_SeesTheChooserOnceOnClassic,
//     ExistingInstall_NeverSeesTheChooserAndStaysClassic) call `SidebarBootstrap.Run`, a type that exists only
//     under src/apps/_old — not yet ported into the active Wavee project. `ChangingDesignLaterNeverReArmsTheChooser`
//     is KEPT, rewritten to start from a bare MemoryAppSettings instead of a bootstrap run: its actual claim (mark
//     seen once, then no later design change ever reopens the chooser) does not depend on the bootstrap step.
//   · LibraryV3DocumentTests' `Build_NeverEmitsTheTopBarSentinelSection` and `DefaultTopBar_StaysTheHomeShortcutAlone`
//     are dropped as duplicates: SidebarReducerTests.cs already owns both claims
//     (`LibraryV3_never_carries_the_shortcut_band_as_a_section` and `The_default_top_bar_is_the_home_route_shortcut`
//     respectively).

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 1 — PER-DESIGN PANE STATE: the width tiers, the breakpoints/hysteresis, snapshot/restore/latch ─────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// C8.6 — per-mode remembered state (locked decision 3) and the per-design width tiers (locked decision 14). Drives
// the REAL rules: SidebarPaneState (the snapshot/restore/latch decisions behind a design switch), SidebarDesignInfo
// and SidebarPaneBounds, over an in-memory MemoryAppSettings. No engine, no window, no registry.
public class SidebarPaneStateTests
{
    const float Min = SidebarPaneBounds.NavPaneMinW;   // 180 (issue #84: lowered from 240)
    const float Max = SidebarPaneBounds.NavPaneMaxW;   // 460

    // SidebarPaneState.WidthKey is PRIVATE in 0.3 (Sidebar.Modes.cs:116) — 0.2.9 exposed the equivalent composition
    // as public `SidebarKeys.Width(d)`. These three helpers reconstruct exactly what that private method builds
    // (Platform.Keys.SidebarWidth/WidthUserSet/Collapsed, keyed by SidebarDesignInfo.Slug), so a fact can still read
    // back what SidebarPaneState wrote through Platform.Keys — but a fact that inspects a key's `.Name`/`.Default`
    // below is checking that THIS reconstruction agrees with itself, not independently probing the private one.
    static SettingKey<float> WidthKey(SidebarDesign d)
        => Platform.Keys.SidebarWidth(SidebarDesignInfo.Slug(d), SidebarDesignInfo.Tiers(d).Narrow);
    static SettingKey<bool> WidthUserSetKey(SidebarDesign d) => Platform.Keys.SidebarWidthUserSet(SidebarDesignInfo.Slug(d));
    static SettingKey<bool> CollapsedKey(SidebarDesign d) => Platform.Keys.SidebarCollapsed(SidebarDesignInfo.Slug(d));

    // ── tier tables (locked decision 14) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachDesign_HasItsOwnTierTriple()
    {
        Assert.Equal((240f, 280f, 320f), SidebarDesignInfo.Tiers(SidebarDesign.Classic));
        Assert.Equal((300f, 340f, 380f), SidebarDesignInfo.Tiers(SidebarDesign.LibraryV3));
        Assert.Equal((280f, 320f, 360f), SidebarDesignInfo.Tiers(SidebarDesign.Curated));

        // Every tier of every design sits inside the ONE clamp pair — no per-design literal may escape it.
        foreach (var d in SidebarDesignInfo.All)
        {
            var (narrow, mid, wide) = SidebarDesignInfo.Tiers(d);
            Assert.InRange(narrow, Min, Max);
            Assert.InRange(mid, Min, Max);
            Assert.InRange(wide, Min, Max);
            Assert.True(narrow <= mid && mid <= wide);
        }
    }

    [Theory]
    // viewport 1200 → narrow tier · 1500 → mid · 1900 → wide, for all three designs
    [InlineData(1200f, 240f, 300f, 280f)]
    [InlineData(1500f, 280f, 340f, 320f)]
    [InlineData(1900f, 320f, 380f, 360f)]
    public void FirstVisitToADesign_UsesItsOwnDefaultTier(float viewport, float classic, float v3, float curated)
    {
        var s = new MemoryAppSettings();   // nothing written ⇒ no design has ever been user-set

        Assert.Equal(classic, SidebarPaneState.Restore(s, SidebarDesign.Classic, viewport).Width);
        Assert.Equal(v3, SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, viewport).Width);
        Assert.Equal(curated, SidebarPaneState.Restore(s, SidebarDesign.Curated, viewport).Width);

        Assert.False(SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, viewport).WidthUserSet);
    }

    [Fact]
    public void Breakpoints_AndHysteresis_AreSharedByEveryDesign()
    {
        // The 1400/1800 enters and the 24-DIP shrink hysteresis are identical for all three designs — only the tier
        // VALUES differ. V3: 1400 widens to 340 at once, and 340 holds down to 1376.
        var v3 = SidebarDesignInfo.Tiers(SidebarDesign.LibraryV3);
        Assert.Equal(340f, SidebarPaneBounds.NavPaneDefaultFor(1400f, 300f, true, v3));
        Assert.Equal(340f, SidebarPaneBounds.NavPaneDefaultFor(1380f, 340f, true, v3));
        Assert.Equal(300f, SidebarPaneBounds.NavPaneDefaultFor(1370f, 340f, true, v3));

        // …and the no-triple overloads still mean CLASSIC, so every pre-existing call site is unchanged.
        Assert.Equal(SidebarPaneBounds.NominalNavPaneDefaultFor(1500f),
                     SidebarPaneBounds.NominalNavPaneDefaultFor(1500f, SidebarDesignInfo.Tiers(SidebarDesign.Classic)));
        Assert.Equal(280f, SidebarPaneBounds.NominalNavPaneDefaultFor(1500f));
    }

    [Fact]
    public void PreMeasureSeed_TakesTheDesignsNarrowTier()
    {
        // The shell constructor has no viewport yet; the seed must still be the INCOMING design's own narrow tier.
        Assert.Equal(240f, SidebarPaneState.TierDefault(SidebarDesign.Classic, 0f));
        Assert.Equal(300f, SidebarPaneState.TierDefault(SidebarDesign.LibraryV3, 0f));
        Assert.Equal(280f, SidebarPaneState.TierDefault(SidebarDesign.Curated, 0f));
    }

    // ── snapshot / restore (locked decision 3) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingDesigns_SnapshotsOutgoing_AndRestoresIncoming()
    {
        var s = new MemoryAppSettings();
        const float viewport = 1500f;

        // Classic: the user drags to 410 and collapses the pane.
        SidebarPaneState.CommitWidth(s, SidebarDesign.Classic, 410f);
        SidebarPaneState.Snapshot(s, SidebarDesign.Classic, new SidebarPaneSnapshot(410f, Collapsed: true, WidthUserSet: true));

        // → Library V3, never visited: its OWN mid tier, not Classic's 410, and not Classic's collapsed state.
        var v3 = SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, viewport);
        Assert.Equal(340f, v3.Width);
        Assert.False(v3.Collapsed);
        Assert.False(v3.WidthUserSet);

        // The user drags V3 to 300 and leaves it expanded, then switches back.
        SidebarPaneState.CommitWidth(s, SidebarDesign.LibraryV3, 300f);
        SidebarPaneState.Snapshot(s, SidebarDesign.LibraryV3, new SidebarPaneSnapshot(300f, false, true));

        // → Classic restores byte-for-byte.
        var back = SidebarPaneState.Restore(s, SidebarDesign.Classic, viewport);
        Assert.Equal(new SidebarPaneSnapshot(410f, true, true), back);

        // …and V3 still remembers its own, independently.
        Assert.Equal(new SidebarPaneSnapshot(300f, false, true),
                     SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, viewport));
    }

    [Fact]
    public void PinningOneDesignsWidth_NeverLatches_NorClears_Another()
    {
        var s = new MemoryAppSettings();

        SidebarPaneState.CommitWidth(s, SidebarDesign.LibraryV3, 360f);

        Assert.True(SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, 1900f).WidthUserSet);
        Assert.False(SidebarPaneState.Restore(s, SidebarDesign.Classic, 1900f).WidthUserSet);
        // Classic's ladder is therefore still live: it takes its wide tier, not V3's pinned 360.
        Assert.Equal(320f, SidebarPaneState.Restore(s, SidebarDesign.Classic, 1900f).Width);
    }

    [Fact]
    public void TierLadderReSeeds_OnSwitch_WhileTheIncomingDesignIsUnpinned()
    {
        var s = new MemoryAppSettings();

        // Curated was last seen at a NARROW window and wrote 280 as its responsive default (never a drag ⇒ never
        // latched).
        s.Set(WidthKey(SidebarDesign.Curated), 280f);

        // The window is now wide. Because the flag is false, the stored 280 is only a stale responsive default and
        // the restore hands back the design's tier at the LIVE viewport — the re-seed obligation.
        Assert.Equal(360f, SidebarPaneState.Restore(s, SidebarDesign.Curated, 1900f).Width);

        // Once it IS latched, the stored width wins at every viewport.
        SidebarPaneState.CommitWidth(s, SidebarDesign.Curated, 280f);
        Assert.Equal(280f, SidebarPaneState.Restore(s, SidebarDesign.Curated, 1900f).Width);
    }

    [Fact]
    public void CollapsedIsNotAWidthChoice()
    {
        var s = new MemoryAppSettings();

        // Collapsing writes ONLY the collapse key — per design.
        s.Set(CollapsedKey(SidebarDesign.Classic), true);

        var restored = SidebarPaneState.Restore(s, SidebarDesign.Classic, 1900f);
        Assert.True(restored.Collapsed);
        Assert.False(restored.WidthUserSet);
        Assert.False(s.WasWritten(WidthUserSetKey(SidebarDesign.Classic)));
        Assert.Equal(320f, restored.Width);            // the ladder still owns the width
    }

    [Fact]
    public void ResetWidth_ClearsUserSet_AndReSeedsFromTier()
    {
        var s = new MemoryAppSettings();
        SidebarPaneState.CommitWidth(s, SidebarDesign.LibraryV3, 455f);
        Assert.True(SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, 1500f).WidthUserSet);

        var reset = SidebarPaneState.ResetWidth(s, SidebarDesign.LibraryV3, 1500f);
        Assert.False(reset.WidthUserSet);
        Assert.Equal(340f, reset.Width);               // V3's mid tier at 1500
        Assert.False(s.Get(WidthUserSetKey(SidebarDesign.LibraryV3)));
        Assert.Equal(340f, SidebarPaneState.Restore(s, SidebarDesign.LibraryV3, 1500f).Width);
    }

    [Fact]
    public void CommitWidth_ClampsThroughTheOneOwner()
    {
        var s = new MemoryAppSettings();
        Assert.Equal(Max, SidebarPaneState.CommitWidth(s, SidebarDesign.Curated, 9000f));
        Assert.Equal(Min, SidebarPaneState.CommitWidth(s, SidebarDesign.Curated, 10f));
        Assert.Equal(Min, SidebarPaneState.Restore(s, SidebarDesign.Curated, 1500f).Width);
    }

    [Fact]
    public void AWidthPersistedByAnOlderBuild_IsClampedAtTheSeed_NotUsedRaw()
    {
        var s = new MemoryAppSettings();
        SidebarPaneState.CommitWidth(s, SidebarDesign.Classic, 300f);
        s.Set(WidthKey(SidebarDesign.Classic), 5000f);      // a hand-edited / older-build value
        Assert.Equal(Max, SidebarPaneState.Restore(s, SidebarDesign.Classic, 1500f).Width);
    }

    [Fact]
    public void SwitchingDesigns_TouchesOnlyThatDesignsPaneKeys()
    {
        // The shared-pins invariant at the persistence layer: a design switch snapshots/restores the per-design PANE
        // keys and never writes a pin, a V3 custom-order or another design's key.
        var s = new MemoryAppSettings();
        SidebarPaneState.Snapshot(s, SidebarDesign.Classic, new SidebarPaneSnapshot(300f, false, true));
        _ = SidebarPaneState.Restore(s, SidebarDesign.Curated, 1500f);

        Assert.Equal(3, s.WrittenCount);
        Assert.True(s.WasWritten(WidthKey(SidebarDesign.Classic)));
        Assert.True(s.WasWritten(CollapsedKey(SidebarDesign.Classic)));
        Assert.True(s.WasWritten(WidthUserSetKey(SidebarDesign.Classic)));
        Assert.False(s.WasWritten(WidthKey(SidebarDesign.Curated)));
        Assert.False(s.WasWritten(WidthKey(SidebarDesign.LibraryV3)));
    }

    // ── the design enum / slug contract (persisted — never renumber, never rename) ────────────────────────────────────

    [Fact]
    public void DesignValues_AndSlugs_ArePersistedAndStable()
    {
        Assert.Equal(0, (int)SidebarDesign.Classic);   // 0 is load-bearing: an install that never wrote the key stays Classic
        Assert.Equal(1, (int)SidebarDesign.LibraryV3);
        Assert.Equal(2, (int)SidebarDesign.Curated);

        Assert.Equal("classic", SidebarDesignInfo.Slug(SidebarDesign.Classic));
        Assert.Equal("v3", SidebarDesignInfo.Slug(SidebarDesign.LibraryV3));
        Assert.Equal("curated", SidebarDesignInfo.Slug(SidebarDesign.Curated));

        Assert.Equal("sidebar.classic", SidebarDesignInfo.MountKey(SidebarDesign.Classic));
        Assert.Equal("sidebar.v3", SidebarDesignInfo.MountKey(SidebarDesign.LibraryV3));
        Assert.Equal("sidebar.curated", SidebarDesignInfo.MountKey(SidebarDesign.Curated));

        // The three mount keys must be distinct, or a switch would reuse the outgoing mode's hooks.
        Assert.Equal(SidebarDesignInfo.Count, SidebarDesignInfo.All.Length);
        var mountKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in SidebarDesignInfo.All) Assert.True(mountKeys.Add(SidebarDesignInfo.MountKey(d)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void UnknownStoredDesignValue_FallsBackToClassic(int stored)
        => Assert.Equal(SidebarDesign.Classic, SidebarDesignInfo.FromInt(stored));

    [Fact]
    public void PerDesignKeyNames_AreDisjoint_AndSlugDerived()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in SidebarDesignInfo.All)
        {
            Assert.True(names.Add(WidthKey(d).Name));
            Assert.True(names.Add(WidthUserSetKey(d).Name));
            Assert.True(names.Add(CollapsedKey(d).Name));
            Assert.StartsWith("sidebar." + SidebarDesignInfo.Slug(d) + ".", WidthKey(d).Name);
        }
        // …and they must not collide with the legacy v0 global keys, which stay on disk for a downgrade.
        Assert.DoesNotContain(Platform.Keys.SidebarWidthLegacy.Name, names);
        Assert.DoesNotContain(Platform.Keys.SidebarCollapsedLegacy.Name, names);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 2 — THE ONE-TIME CHOOSER GATE ────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// §C6 + F.4.3 — the SELECTION UX's pure decisions: the one-time chooser's gate, the marker that closes it forever,
// the card values the three preview cards select through, and the rule that lights the "Customize sidebar"
// affordance. One-boolean-read / one-boolean-write decisions, worth pinning: a marker burned too early permanently
// denies the chooser to the fresh installs it exists for, and a marker never written turns a "one-time" dialog into
// a launch ritual. Neither failure is recoverable per install and neither is visible in a diff.
public class SidebarDesignGatingTests
{
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
        settings.Set(Platform.Keys.SidebarOnboardingSeen, true);
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
    }

    [Fact]
    public void Chooser_GateIgnoresEverythingButTheMarker()
    {
        // The gate must NOT re-derive freshness. A marked install with a Curated design and no data files is still
        // suppressed; an unmarked install with every other signal pointing at "existing" still shows — otherwise the
        // two deciders can disagree and the chooser is lost.
        var marked = new MemoryAppSettings();
        marked.Set(Platform.Keys.SidebarOnboardingSeen, true);
        marked.Set(Platform.Keys.SidebarDesign, (int)SidebarDesign.Curated);
        Assert.False(SidebarDesignGating.ShouldShowChooser(marked));

        var unmarked = new MemoryAppSettings();
        unmarked.Set(Platform.Keys.SidebarDesign, (int)SidebarDesign.Classic);
        unmarked.Set(Platform.Keys.SidebarBootstrapVersion, 1);
        Assert.True(SidebarDesignGating.ShouldShowChooser(unmarked));
    }

    [Fact]
    public void Chooser_NoSettingsSeamNeverOpens()
    {
        // A host without a settings seam has nowhere to record the answer, so opening a ONE-TIME dialog there would
        // show it on every launch.
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
        Assert.True(settings.Get(Platform.Keys.SidebarOnboardingSeen));
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
        // dialog hangs it on the overlay handle's ClosedAction precisely so none of them can forget. Whatever design
        // was applied when the dialog closed survives untouched.
        foreach (var applied in new[] { SidebarDesign.Classic, SidebarDesign.LibraryV3, SidebarDesign.Curated })
        {
            var settings = new MemoryAppSettings();
            settings.Set(Platform.Keys.SidebarDesign, (int)applied);
            SidebarDesignGating.MarkChooserSeen(settings);
            Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
            Assert.Equal(applied, SidebarDesignGating.ActiveDesign(settings));
        }
    }

    [Fact]
    public void MarkSeen_NeverWritesTheDesign()
    {
        // The chooser answers "did you see it?", never "which one?" — the cards already applied the design through
        // SwitchDesign. A marker write that also touched sidebar.design would silently stomp a user who picked a card
        // and then pressed Escape.
        var settings = new MemoryAppSettings();
        SidebarDesignGating.MarkChooserSeen(settings);
        Assert.False(settings.WasWritten(Platform.Keys.SidebarDesign));
    }

    [Fact]
    public void MarkSeen_ToleratesNoSettingsSeam()
    {
        Assert.False(SidebarDesignGating.MarkChooserSeen(null));
    }

    // ── DROPPED: FreshInstall_SeesTheChooserOnceOnClassic / ExistingInstall_NeverSeesTheChooserAndStaysClassic ────────
    // Both called `SidebarBootstrap.Run`, which exists only under src/apps/_old (not yet ported into the active
    // Wavee project) — see this file's header.

    [Fact]
    public void ChangingDesignLaterNeverReArmsTheChooser()
    {
        // Rewritten to not depend on SidebarBootstrap (see header): the claim under test — mark seen once, then no
        // later design change ever reopens the chooser — needs only a marked settings bag, not a bootstrap run.
        var settings = new MemoryAppSettings();
        SidebarDesignGating.MarkChooserSeen(settings);

        foreach (var design in SidebarDesignInfo.All)
        {
            settings.Set(Platform.Keys.SidebarDesign, SidebarDesignGating.IndexOf(design));
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
        settings.Set(Platform.Keys.SidebarDesign, 7);
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
        // Three cards, three titles, three subtitles, no shared key — a duplicated key is how two cards end up
        // reading the same name and the picker becomes unusable in exactly the dialog nobody re-tests.
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
        // The picker's copy is `sidebar.design.*` — the same namespace the quick layout menu's radio rows read, so
        // a translator renaming one design renames it in both surfaces.
        foreach (var design in SidebarDesignInfo.All)
        {
            Assert.StartsWith("sidebar.design.", SidebarDesignGating.TitleKey(design), StringComparison.Ordinal);
            Assert.StartsWith("sidebar.design.", SidebarDesignGating.SubtitleKey(design), StringComparison.Ordinal);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 3 — CLASSIC AS A LOCKED BUILT-IN DOCUMENT, AND ITS RENDER CACHE ──────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// Classic used to be a hand-built pane body; it is now a document over the ONE pane renderer, which means its
// information architecture and the density intent behind its row metrics are ordinary data, pinned here because
// "Classic looks exactly like Classic" is a pixel contract no future template edit may silently relitigate.
public sealed class SidebarBuiltInDocumentTests
{
    /// <summary>The document a NORMAL user gets: every section open, developer mode OFF (so no Tools section).</summary>
    static SidebarCustomLayout Classic() => SidebarBuiltInDocuments.Classic(true, true, true);

    /// <summary>The document a DEVELOPER gets — the same thing plus the Tools section and its leading divider.</summary>
    static SidebarCustomLayout ClassicDev() => SidebarBuiltInDocuments.Classic(true, true, true, devTools: true);

    static SidebarSectionSpec Section(SidebarCustomLayout l, string id)
    {
        var s = l.Find(id);
        Assert.NotNull(s);
        return s!;
    }

    static SidebarSectionKind[] Kinds(SidebarCustomLayout doc)
    {
        var kinds = new SidebarSectionKind[doc.Sections.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = doc.Sections[i].Kind;
        return kinds;
    }

    [Fact]
    public void Classic_ReproducesTodaysInformationArchitecture()
    {
        var doc = ClassicDev();

        // Pinned · rule · Your Library · rule · Playlists · rule · DevTools.
        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
            SidebarSectionKind.Divider,
            SidebarSectionKind.StaticLinks,
        }, Kinds(doc));

        // Pinned is FIRST, so the planner emits no leading divider before it (Classic's `rule: false`).
        Assert.Equal(SidebarSectionKind.Pinned, doc.Sections[0].Kind);
    }

    // ── developer mode gates the API console (and takes its divider with it) ──────────────────────────────────────────

    /// <summary>The API console is DEVELOPER surface. Off — the product default — Classic ends at Playlists, and the
    /// divider that used to separate Playlists from Tools goes too: a trailing rule under the last real section,
    /// with nothing after it, is exactly how a reader spots that something was removed rather than never offered.</summary>
    [Fact]
    public void Classic_WithoutDeveloperMode_EndsAtPlaylistsAndCarriesNoToolsDivider()
    {
        var doc = Classic();

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
        }, Kinds(doc));

        Assert.Null(doc.Find(SidebarBuiltInDocuments.ToolsId));
        Assert.Equal(SidebarSectionKind.PlaylistTree, doc.Sections[^1].Kind);
    }

    /// <summary>Turning developer mode on is PURELY ADDITIVE: every section the plain document had is still there,
    /// in the same order, with the same ids — Tools is appended, nothing is rearranged to make room for it.</summary>
    [Fact]
    public void Classic_DeveloperMode_OnlyAppendsTheToolsBand()
    {
        var plain = Classic();
        var dev = ClassicDev();

        Assert.Equal(plain.Sections.Count + 2, dev.Sections.Count);   // the divider AND the section
        for (int i = 0; i < plain.Sections.Count; i++)
            Assert.Equal(plain.Sections[i].Id, dev.Sections[i].Id);
        Assert.Equal(SidebarBuiltInDocuments.ToolsId, dev.Sections[^1].Id);
    }

    [Fact]
    public void Classic_LibraryShortcutsKeepTodaysOrderAndIcons()
    {
        var lib = Section(Classic(), SidebarBuiltInDocuments.LibraryId);
        var keys = new string[lib.ItemList.Count];
        var icons = new string?[lib.ItemList.Count];
        for (int i = 0; i < keys.Length; i++) { keys[i] = lib.ItemList[i].Key; icons[i] = lib.ItemList[i].IconOverride; }

        Assert.Equal(new[] { "albums", "artists", "liked", "podcasts", "local" }, keys);
        Assert.Equal(new string?[] { "Album", "Contact", "Heart", "RadioTower", "Folder" }, icons);
        Assert.True(lib.Opts.CountBadges);   // the counts survive — as quiet numbers, never the accent pill
    }

    [Fact]
    public void Classic_DensityIntentYields44DipArtRowsAnd40DipGlyphRows()
    {
        var doc = ClassicDev();   // the Tools band only exists in developer mode, and its density is pinned below

        // Glyph bands: Cozy + Subtitles:false ⇒ HeightFor == 40. Neither section ever paints a subtitle line, so
        // claiming one it never draws only pitches the row a size taller than its content. Artwork stays false
        // either way (these are 16-DIP glyph rows).
        foreach (string id in new[] { SidebarBuiltInDocuments.LibraryId, SidebarBuiltInDocuments.ToolsId })
        {
            var s = Section(doc, id);
            Assert.Equal(SidebarDensity.Cozy, s.Opts.Density);
            Assert.False(s.Opts.Subtitles);
            Assert.False(s.Opts.Artwork);
        }

        // Artwork bands: Cozy + subtitles ⇒ HeightFor == 44 with 32-DIP covers (Classic's pinned + playlist rows).
        foreach (string id in new[] { SidebarBuiltInDocuments.PinnedId, SidebarBuiltInDocuments.PlaylistsId })
        {
            var s = Section(doc, id);
            Assert.Equal(SidebarDensity.Cozy, s.Opts.Density);
            Assert.True(s.Opts.Subtitles);
            Assert.True(s.Opts.Artwork);
        }
    }

    [Fact]
    public void Classic_DevToolsSectionIsHeaderlessAndBadgeless()
    {
        var tools = Section(ClassicDev(), SidebarBuiltInDocuments.ToolsId);
        // No Title and no TitleLocKey ⇒ the planner emits NO SectionHeader row: a flat row outside every section.
        Assert.Null(tools.Title);
        Assert.Null(tools.TitleLocKey);
        Assert.False(tools.Opts.CountBadges);
        // …and it is EXPANDED-ONLY: the 56-DIP rail would show a bare glyph with no label, plus the divider that
        // precedes it. The planner drops both once the section opts out.
        Assert.False(tools.Opts.ShowInRail);
        Assert.Single(tools.ItemList);
        Assert.Equal(SidebarBuiltInDocuments.DevToolsRoute, tools.ItemList[0].Key);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Classic_CollapseFlagsDriveTheThreeCollapsibleSections(bool pinned, bool library, bool playlists)
    {
        var doc = SidebarBuiltInDocuments.Classic(pinned, library, playlists);
        Assert.Equal(!pinned, Section(doc, SidebarBuiltInDocuments.PinnedId).Collapsed);
        Assert.Equal(!library, Section(doc, SidebarBuiltInDocuments.LibraryId).Collapsed);
        Assert.Equal(!playlists, Section(doc, SidebarBuiltInDocuments.PlaylistsId).Collapsed);
    }

    [Fact]
    public void Classic_SectionIdsAreStableAcrossRebuildsAndMapToTheirPreferenceFlag()
    {
        // NOT a minted id: the pane keys its reorder bands, collapse routing and section identity off these, so a
        // fresh id per rebuild would reset all three on every toggle.
        var a = ClassicDev();
        var b = SidebarBuiltInDocuments.Classic(false, false, false, devTools: true);
        for (int i = 0; i < a.Sections.Count; i++) Assert.Equal(a.Sections[i].Id, b.Sections[i].Id);

        Assert.Equal(ClassicSection.Pinned, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.PinnedId));
        Assert.Equal(ClassicSection.Library, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.LibraryId));
        Assert.Equal(ClassicSection.Playlists, SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.PlaylistsId));
        // A non-collapsible section (a divider, the header-less tools links) must be a NO-OP, never a mis-write.
        Assert.Null(SidebarBuiltInDocuments.ClassicSectionOf(SidebarBuiltInDocuments.ToolsId));
        Assert.Null(SidebarBuiltInDocuments.ClassicSectionOf("nope"));
    }

    [Fact]
    public void Classic_TemplateIdIsNotOneOfTheCuratedTemplates()
    {
        // Classic is never offered in the customizer's template palette, and a Curated document must never claim
        // its id.
        Assert.False(SidebarTemplates.IsKnown(SidebarBuiltInDocuments.ClassicId));
    }

    // ── the render cache: identity, not just content ────────────────────────────────────────────────────────────────
    //
    // ClassicDocumentCache exists because the pane's publish stage decides per-row-diff vs. whole-window re-skin by
    // a document REFERENCE check — these facts pin the cache's hit/miss contract directly, which the 0.2.9 suite had
    // no equivalent type for (Classic used to be rebuilt inline, never cached).

    [Fact]
    public void FlagsOf_PacksTheThreeCollapseBitsIndependently()
    {
        Assert.Equal(0, ClassicDocumentCache.FlagsOf(false, false, false));
        Assert.Equal(1, ClassicDocumentCache.FlagsOf(true, false, false));
        Assert.Equal(2, ClassicDocumentCache.FlagsOf(false, true, false));
        Assert.Equal(4, ClassicDocumentCache.FlagsOf(false, false, true));
        Assert.Equal(7, ClassicDocumentCache.FlagsOf(true, true, true));
    }

    [Fact]
    public void Get_ReturnsTheSameInstance_WhileFlagsDevToolsAndTopBarAreUnchanged()
    {
        var cache = new ClassicDocumentCache();
        var topBar = SidebarCustomLayout.DefaultTopBar;

        var first = cache.Get(true, true, true, false, topBar);
        var second = cache.Get(true, true, true, false, topBar);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Get_MintsAFreshInstance_OnAnyCollapseFlagFlip(bool pinned, bool library, bool playlists)
    {
        var cache = new ClassicDocumentCache();
        var first = cache.Get(true, true, true, devTools: false);
        var second = cache.Get(pinned, library, playlists, devTools: false);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Get_MintsAFreshInstance_WhenDeveloperModeFlips()
    {
        var cache = new ClassicDocumentCache();
        var first = cache.Get(true, true, true, devTools: false);
        var second = cache.Get(true, true, true, devTools: true);
        Assert.NotSame(first, second);
        Assert.Null(first.Find(SidebarBuiltInDocuments.ToolsId));
        Assert.NotNull(second.Find(SidebarBuiltInDocuments.ToolsId));
    }

    [Fact]
    public void Get_MintsAFreshInstance_WhenTheTopBarReferenceChanges()
    {
        // Compared by REFERENCE, per the class's own doc comment: every band edit rebuilds the list and replaces
        // the layout record, so a reference match is what proves the content still matches without a value compare.
        var cache = new ClassicDocumentCache();
        var bandA = SidebarCustomLayout.DefaultTopBar;
        var bandB = new List<SidebarItemSpec>(bandA).AsReadOnly();   // a different reference, same content

        var first = cache.Get(true, true, true, false, bandA);
        var second = cache.Get(true, true, true, false, bandB);
        Assert.NotSame(first, second);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 4 — THE SHELL TOP-BAR/NAV BAND'S PURE SHAPING RULES ──────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// SidebarNavBandModel — the shortcut band's pure SHAPING rules, as a model. The bespoke component this was
// originally written against is gone: the band materialises as an ordinary "Shortcuts" StaticLinks section at the
// head of every design's document (SidebarShortcutsSection), so it is planned/rowed/railed/reordered by the one
// SidebarPane with no second render path. The MODEL survives because its four rules are the band's, not a widget's,
// and map 1:1 onto the planner's Route/Entity/Track/Action arms: item target → tile shape, tile → the route key a
// selection mark reads, document order, and the truncation bound that keeps a hand-edited over-long band from
// outgrowing the pane.
//
// The band's CONTENT and every mutation of it are pinned in SidebarReducerTests.cs (AddTopBarItem/MoveTopBarItem/
// RemoveTopBarItem, the wire, the materialisation into a section + sentinel-id routing) and deliberately not
// restated here.
public class SidebarNavBandTests
{
    static SidebarItemSpec Route(string key, string id = "itm_route")
        => new(id, SidebarItemTarget.Route, key);

    static SidebarItemSpec Entity(string uri, SidebarEntityKind kind = SidebarEntityKind.Playlist,
                                  string id = "itm_entity")
        => new(id, SidebarItemTarget.Entity, uri, kind);

    static SidebarItemSpec Track(string uri, string id = "itm_track")
        => new(id, SidebarItemTarget.Track, uri);

    static SidebarItemSpec Action(string id = "itm_action")
        => new(id, SidebarItemTarget.Action, "", Action: SidebarActionBinding.Simple("wavee", "play"));

    static List<SidebarNavBandTile> Shape(IReadOnlyList<SidebarItemSpec>? band, int cap = SidebarNavBandModel.MaxTiles)
    {
        var into = new List<SidebarNavBandTile>();
        int n = SidebarNavBandModel.Shape(band, into, cap);
        Assert.Equal(n, into.Count);
        return into;
    }

    // ── the empty-collapse (both forms draw nothing) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptiedBand_RendersNothing()
    {
        Assert.False(SidebarNavBandModel.Renders(Array.Empty<SidebarItemSpec>()));
        Assert.Empty(Shape(Array.Empty<SidebarItemSpec>()));
    }

    [Fact]
    public void NullBand_RendersNothing()
    {
        Assert.False(SidebarNavBandModel.Renders(null));
        Assert.Empty(Shape(null));
    }

    [Fact]
    public void DefaultBand_IsTheSingleHomeTile()
    {
        var band = SidebarCustomLayout.DefaultTopBar;
        Assert.True(SidebarNavBandModel.Renders(band));

        var tiles = Shape(band);
        var tile = Assert.Single(tiles);
        Assert.Equal(SidebarNavBandTileKind.Route, tile.Kind);
        Assert.Equal("home", tile.Key);
        Assert.Equal("home", tile.RouteKey);
        Assert.Equal(SidebarIds.TopBarHomeItem, tile.ItemId);
        Assert.Equal(0, tile.Index);
    }

    // ── ordering ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_KeepsDocumentOrderAndIndexes()
    {
        var band = new[] { Route("home", "a"), Route("search", "b"), Route("liked", "c") };
        var tiles = Shape(band);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(new[] { "a", "b", "c" }, new[] { tiles[0].ItemId, tiles[1].ItemId, tiles[2].ItemId });
        Assert.Equal(new[] { 0, 1, 2 }, new[] { tiles[0].Index, tiles[1].Index, tiles[2].Index });
    }

    [Fact]
    public void Shape_ClearsTheCallerScratch()
    {
        var into = new List<SidebarNavBandTile> { new(9, "stale", SidebarNavBandTileKind.Track, "x", null) };
        SidebarNavBandModel.Shape(new[] { Route("home") }, into);

        var tile = Assert.Single(into);
        Assert.Equal("itm_route", tile.ItemId);
    }

    // ── truncation ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTiles_IsTheReducersCap()
        => Assert.Equal(SidebarLayoutReducer.MaxTopBarItems, SidebarNavBandModel.MaxTiles);

    [Fact]
    public void Shape_TruncatesTheTailPastTheCap()
    {
        var band = new List<SidebarItemSpec>();
        for (int i = 0; i < SidebarNavBandModel.MaxTiles + 3; i++) band.Add(Route("r" + i, "itm_" + i));

        var tiles = Shape(band);
        Assert.Equal(SidebarNavBandModel.MaxTiles, tiles.Count);
        // HEAD kept, tail dropped — the user's leading choices survive.
        Assert.Equal("itm_0", tiles[0].ItemId);
        Assert.Equal("itm_" + (SidebarNavBandModel.MaxTiles - 1), tiles[^1].ItemId);
    }

    [Fact]
    public void Shape_HonoursASmallerCap()
    {
        var band = new[] { Route("home", "a"), Route("search", "b"), Route("liked", "c") };
        Assert.Equal(2, Shape(band, cap: 2).Count);
        Assert.Empty(Shape(band, cap: 0));
        Assert.Empty(Shape(band, cap: -1));
    }

    // ── the four tile shapes ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarItemTarget.Route, SidebarNavBandTileKind.Route)]
    [InlineData(SidebarItemTarget.Entity, SidebarNavBandTileKind.Entity)]
    [InlineData(SidebarItemTarget.Track, SidebarNavBandTileKind.Track)]
    [InlineData(SidebarItemTarget.Action, SidebarNavBandTileKind.Action)]
    public void KindOf_MapsEveryTarget(SidebarItemTarget target, SidebarNavBandTileKind expected)
        => Assert.Equal(expected, SidebarNavBandModel.KindOf(new SidebarItemSpec("itm_x", target, "k")));

    /// <summary>An unknown/future target must degrade to the ROUTE shape — the one that can always draw a glyph and a
    /// label — rather than to a hole (the preserve-don't-destroy discipline, at the render site).</summary>
    [Fact]
    public void KindOf_UnknownTargetDegradesToRoute()
        => Assert.Equal(SidebarNavBandTileKind.Route,
                        SidebarNavBandModel.KindOf(new SidebarItemSpec("itm_x", (SidebarItemTarget)99, "k")));

    // ── the destination the selection mark reads ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RouteKeyOf_RouteItemIsItsOwnKey()
        => Assert.Equal("home", SidebarNavBandModel.RouteKeyOf(Route("home")));

    [Fact]
    public void RouteKeyOf_EmptyRouteKeyHasNoDestination()
        => Assert.Null(SidebarNavBandModel.RouteKeyOf(Route("")));

    /// <summary>The uri → route map is the PIN SCHEME's (one owner): a playlist/album/artist/show id IS its route key.</summary>
    [Fact]
    public void RouteKeyOf_EntityUsesThePinScheme()
    {
        string uri = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
        Assert.Equal(SidebarPinId.FromUri(uri), SidebarNavBandModel.RouteKeyOf(Entity(uri)));
        Assert.NotNull(SidebarNavBandModel.RouteKeyOf(Entity(uri)));
    }

    /// <summary>A uri the pin scheme refuses (an episode / a track / a hand-edited document) has nowhere to navigate,
    /// so the tile renders visible-but-inert instead of lying about a destination.</summary>
    [Fact]
    public void RouteKeyOf_UnresolvableEntityHasNoDestination()
        => Assert.Null(SidebarNavBandModel.RouteKeyOf(Entity("spotify:episode:abc", SidebarEntityKind.None)));

    [Fact]
    public void RouteKeyOf_TrackAndActionNeverNavigate()
    {
        Assert.Null(SidebarNavBandModel.RouteKeyOf(Track("spotify:track:abc")));
        Assert.Null(SidebarNavBandModel.RouteKeyOf(Action()));
    }

    [Fact]
    public void SelectsRoute_MatchesTheActiveRouteOrdinally()
    {
        var home = Route("home");
        Assert.True(SidebarNavBandModel.SelectsRoute(home, "home"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, "Home"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, "search"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, ""));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, null));
    }

    [Fact]
    public void SelectsRoute_PlayableTilesAreNeverSelected()
    {
        Assert.False(SidebarNavBandModel.SelectsRoute(Track("spotify:track:abc"), "spotify:track:abc"));
        Assert.False(SidebarNavBandModel.SelectsRoute(Action(), "home"));
    }

    [Fact]
    public void SelectsRoute_EntityFollowsItsResolvedRoute()
    {
        string uri = "spotify:album:1";
        var item = Entity(uri, SidebarEntityKind.Album);
        string route = SidebarPinId.FromUri(uri)!;
        Assert.True(SidebarNavBandModel.SelectsRoute(item, route));
        Assert.False(SidebarNavBandModel.SelectsRoute(item, uri));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 5 — LIBRARY V3's FILTER RAIL: the idle / filtered / fused chip strip ─────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The Library V3 filter rail's ORDER, driven directly: what idle/filtered/fused look like, what a tap on each
// position writes back, and how roving focus survives a relayout. These are the rules the eye reads — the ✕ pops in
// only once something is active, the qualifier only ever rides Playlists.
public sealed class LibraryV3ChipStripTests
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Podcasts = (int)SidebarV3Filter.Podcasts;
    const int Albums = (int)SidebarV3Filter.Albums;
    const int Artists = (int)SidebarV3Filter.Artists;
    const int Any = (int)SidebarV3Qualifier.Any;
    const int ByYou = (int)SidebarV3Qualifier.ByYou;
    const int BySpotify = (int)SidebarV3Qualifier.BySpotify;
    const int Mixed = (int)SidebarV3Qualifier.Mixed;

    // ── idle ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_NoFilter_IsFourUnselectedFacets_NoClear()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal(V3ChipKind.Facet, s.Kind));
        Assert.All(slots, s => Assert.False(s.Selected));
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Clear);
        Assert.Equal(new[] { Playlists, Podcasts, Albums, Artists },
            new[] { slots[0].Code, slots[1].Code, slots[2].Code, slots[3].Code });

        // Tapping an idle facet SELECTS it (Any qualifier — a fresh filter never carries over a stale sub-filter).
        Assert.Equal(Playlists, slots[0].SelectFilter);
        Assert.Equal(Any, slots[0].SelectQualifier);
        Assert.Equal("v3f" + Playlists, slots[0].Key);
    }

    // ── filtered, no qualifier available/relevant ────────────────────────────────────────────────────────────────

    [Fact]
    public void Filtered_NonPlaylists_IsClearPlusTheOneSelectedFacet_NoOptions()
    {
        foreach (var f in new[] { Podcasts, Albums, Artists })
        {
            var slots = LibraryV3ChipStrip.Slots(f, Any, qualifiersAvailable: true);

            Assert.Equal(2, slots.Count);
            Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
            Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
            Assert.True(slots[1].Selected);
            Assert.Equal(f, slots[1].Code);
            Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
        }
    }

    [Fact]
    public void Filtered_Playlists_QualifiersUnavailable_NoOptionsSpill()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: false);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
    }

    // ── filtered, Playlists with qualifiers evidenced (spilled) ──────────────────────────────────────────────────

    [Fact]
    public void Filtered_Playlists_QualifiersAvailable_SpillsTheThreeOptionsAfterTheFacet()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);

        Assert.Equal(5, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.Equal(Playlists, slots[1].Code);
        Assert.Equal("v3f" + Playlists, slots[1].Key);

        Assert.Equal(V3ChipKind.Option, slots[2].Kind);
        Assert.Equal(ByYou, slots[2].Code);
        Assert.Equal("v3q" + ByYou, slots[2].Key);
        Assert.False(slots[2].Selected);
        Assert.Equal(Playlists, slots[2].SelectFilter);
        Assert.Equal(ByYou, slots[2].SelectQualifier);

        Assert.Equal(BySpotify, slots[3].Code);
        Assert.Equal(Mixed, slots[4].Code);
    }

    // ── fused ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fused_PlaylistsWithQualifierPicked_IsClearPlusOneFusedSlot_NoOptions()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        var fused = slots[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.True(fused.Selected);
        Assert.Equal(Playlists, fused.Code);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);

        // Tapping the fused pill steps back ONE level — drops the qualifier, keeps the facet — not all the way to All.
        Assert.Equal(Playlists, fused.SelectFilter);
        Assert.Equal(Any, fused.SelectQualifier);
    }

    [Fact]
    public void Fused_And_LooseFacet_ShareTheSameKey_ForTheSameCode()
    {
        // The morph mechanism: the reconciler must see ONE node across the loose ⇄ fused transition.
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[1];
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];

        Assert.Equal(V3ChipKind.Facet, loose.Kind);
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Equal(loose.Key, fused.Key);
        Assert.Equal("v3f" + Playlists, loose.Key);
    }

    [Fact]
    public void QualifierPicked_ButNotPlaylists_NeverFuses()
    {
        // A stale/unevidenced qualifier under a non-Playlists filter must not fuse — Slots is a pure function of its
        // three inputs and never assumes the caller already normalized qualifier against filter.
        var slots = LibraryV3ChipStrip.Slots(Albums, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Fused);
    }

    // ── what a tap writes ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearSlot_WritesAllAndAny()
    {
        var clear = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[0];
        Assert.Equal(V3ChipKind.Clear, clear.Kind);
        Assert.Equal(All, clear.SelectFilter);
        Assert.Equal(Any, clear.SelectQualifier);
    }

    [Fact]
    public void SelectedFacetSlot_TapClears_TheChosenPillIsItsOwnToggle()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.True(facet.Selected);
        Assert.Equal(All, facet.SelectFilter);
        Assert.Equal(Any, facet.SelectQualifier);
    }

    [Fact]
    public void OptionSlot_WritesItsOwnFilterAndQualifier()
    {
        var option = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[3]; // BySpotify
        Assert.Equal(V3ChipKind.Option, option.Kind);
        Assert.Equal(BySpotify, option.Code);
        Assert.Equal(Playlists, option.SelectFilter);
        Assert.Equal(BySpotify, option.SelectQualifier);
    }

    // ── FocusIndex ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusIndex_FindsTheMatchingKey()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false);
        Assert.Equal(2, LibraryV3ChipStrip.FocusIndex(slots, "v3f" + Albums));
    }

    [Fact]
    public void FocusIndex_FallsBackToZero_WhenTheKeyIsGone()
    {
        // The focused chip (an option, say) vanished from the rail on this render — focus lands on the leading slot
        // instead of throwing or leaving the rail with no roving position at all.
        var slots = LibraryV3ChipStrip.Slots(Albums, Any, qualifiersAvailable: false);
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, "v3q" + ByYou));
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, null));
    }

    [Fact]
    public void FocusIndex_SurvivesTheLooseToFusedRelayout_BecauseTheKeyIsShared()
    {
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        int focusedAt = LibraryV3ChipStrip.FocusIndex(loose, "v3f" + Playlists);
        Assert.Equal(1, focusedAt);

        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);
        Assert.Equal(1, LibraryV3ChipStrip.FocusIndex(fused, "v3f" + Playlists));
    }

    // ── Route (issue #85, H4 approach 3) ─────────────────────────────────────────────────────────────────────────────
    // Exactly Albums/Artists/Podcasts have an actual library page; Playlists has no "all playlists" destination, so
    // its chip — loose OR fused — never carries one, same as Clear and every Option slot.

    [Theory]
    [InlineData(Albums, "albums")]
    [InlineData(Artists, "artists")]
    [InlineData(Podcasts, "podcasts")]
    [InlineData(Playlists, null)]
    public void RouteFor_IsPopulatedForExactlyTheThreeKindsWithAPage(int filter, string? expected)
        => Assert.Equal(expected, LibraryV3ChipStrip.RouteFor(filter));

    [Fact]
    public void IdleFacets_CarryTheirRoute_ExceptPlaylists()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);
        foreach (var s in slots)
            Assert.Equal(LibraryV3ChipStrip.RouteFor(s.Code), s.Route);
        Assert.Null(slots[0].Route);   // Playlists is always index 0 in the idle order
    }

    [Fact]
    public void SelectedFacetSlot_StillCarriesItsRoute()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.Equal("podcasts", facet.Route);
    }

    [Fact]
    public void ClearAndOptionSlots_NeverCarryARoute()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Null(slots[0].Route);
        foreach (var s in slots)
            if (s.Kind == V3ChipKind.Option) Assert.Null(s.Route);
    }

    [Fact]
    public void FusedPlaylistsSlot_HasNoRoute_PlaylistsNeverHasOne()
    {
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Null(fused.Route);
    }

    [Fact]
    public void ATap_OnlyEverWritesTheFilterOrQualifier_RouteIsASeparateSecondaryField()
    {
        // The contract H4 decided: Route is data a RENDERER may act on with a secondary gesture (Library V3 chose a
        // double-click) — it must never change what SelectFilter/SelectQualifier themselves write for a plain tap.
        var albums = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false)[2];
        Assert.Equal(Albums, albums.Code);
        Assert.Equal("albums", albums.Route);
        Assert.Equal(Albums, albums.SelectFilter);   // a tap still only selects the Albums filter
        Assert.Equal(Any, albums.SelectQualifier);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 6 — LIBRARY V3's SEARCH HOST: the Escape ladder, blur-close, open-width arithmetic ───────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The search host's morph logic is decided by LibraryV3SearchRules (System-only), never by the component that
// renders it, so the Escape ladder / blur-close / open-width arithmetic are pinned here without an EditableText, a
// signal or a frame.
public sealed class LibraryV3SearchRulesTests
{
    // ── Escape ladder ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escape_WithText_Clears()
        => Assert.Equal(LibraryV3SearchRules.EscapeAction.Clear, LibraryV3SearchRules.OnEscape("blue"));

    [Fact]
    public void Escape_WhenEmpty_Closes()
        => Assert.Equal(LibraryV3SearchRules.EscapeAction.Close, LibraryV3SearchRules.OnEscape(""));

    // ── blur ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Blur_WhenEmpty_Closes() => Assert.True(LibraryV3SearchRules.ClosesOnBlur(""));

    [Fact]
    public void Blur_WithQuery_StaysOpen() => Assert.False(LibraryV3SearchRules.ClosesOnBlur("blue"));

    // ── open width ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenWidth_IsThePaneMinusPaddingMinusThePillAndGap()
    {
        // 320 pane, 43 toolbar padding (LeadInset 27 + ContentLaneEnd 16) -> 320 - 43 - 28 - 4 = 245.
        float width = LibraryV3SearchRules.OpenWidth(320f, 43f);
        Assert.Equal(245f, width);
    }

    [Fact]
    public void OpenWidth_NeverGoesBelowClosedWidth()
    {
        // A pane too narrow to fit the pill+gap must not yield a negative or shrinking host.
        float width = LibraryV3SearchRules.OpenWidth(40f, 43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }

    [Fact]
    public void Resolve_WidePane_IsInlineAndExpanded_WithALabelledPill()
    {
        var wide = LibraryV3SearchRules.Resolve(LibraryV3SearchRules.InlineWidth, openedByUser: false, hasText: false);
        Assert.True(wide.Inline);
        Assert.True(wide.Expanded);
        Assert.False(wide.SortIconOnly);
    }

    [Fact]
    public void Resolve_NarrowPane_IsAButtonUntilOpenedOrTyped()
    {
        float narrow = LibraryV3SearchRules.InlineWidth - 1f;
        var closed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: false);
        Assert.False(closed.Inline);
        Assert.False(closed.Expanded);
        Assert.False(closed.SortIconOnly);            // 299 ≥ 280: the pill keeps its label while the field is a button

        var opened = LibraryV3SearchRules.Resolve(narrow, openedByUser: true, hasText: false);
        Assert.True(opened.Expanded);
        Assert.True(opened.SortIconOnly);             // the field owns the row

        // A query typed while wide survives a drag past the threshold: text alone keeps the field expanded.
        var typed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: true);
        Assert.True(typed.Expanded);
    }

    [Fact]
    public void Resolve_VeryNarrowPane_DropsThePillLabelEvenWhenClosed()
    {
        var tiny = LibraryV3SearchRules.Resolve(240f, openedByUser: false, hasText: false);
        Assert.False(tiny.Expanded);
        Assert.True(tiny.SortIconOnly);
    }

    [Fact]
    public void OpenWidth_AtTheFloorBoundary_IsExact()
    {
        // paneWidth - padH - pill - gap == ClosedWidth exactly: the floor must not clip a legitimate value.
        float width = LibraryV3SearchRules.OpenWidth(
            LibraryV3SearchRules.ClosedWidth + 43f + LibraryV3SearchRules.SortIconOnlyWidth + LibraryV3SearchRules.Gap,
            43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 7 — LIBRARY V3 AS A SYNTHESIZED DOCUMENT, AND THE TREE RE-GROUPING ORDER ─────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// LibraryV3Document.Build's content used to be its own pane container; it is now two pure halves — a document (what
// to render) and an order (in which sequence) — and both are pinned here, because the mapping IS the behaviour: get
// KindFor wrong and folders lose their indent; get QueryFor wrong and the 56-DIP rail sorts differently from the
// pane it collapsed from.
public sealed class LibraryV3DocumentTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static LibraryV3DocState State(
        SidebarV3Filter filter = SidebarV3Filter.All,
        SidebarV3View view = SidebarV3View.List,
        SidebarV3Sort sort = SidebarV3Sort.Recents,
        bool descending = false,
        SidebarV3Qualifier qualifier = SidebarV3Qualifier.Any,
        bool qualifiersAvailable = false,
        bool searching = false,
        string? drill = null,
        bool hasPins = false,
        bool likedPinned = false,
        int columns = 2,
        bool dragInFlight = false)
        => new((int)filter, (int)qualifier, (int)sort, descending, (int)view, columns, searching, drill,
               hasPins, likedPinned, qualifiersAvailable, dragInFlight);

    static SidebarSectionSpec? Find(SidebarCustomLayout doc, string id) => doc.Find(id);

    static SidebarSectionSpec Library(SidebarCustomLayout doc)
    {
        var s = doc.Find(LibraryV3Document.LibraryId);
        Assert.NotNull(s);
        return s!;
    }

    static string[] IdsOf(SidebarCustomLayout doc)
    {
        var ids = new string[doc.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = doc.Sections[i].Id;
        return ids;
    }

    // ── section kind per lens ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlaylistLenses_InAListView_RenderAsATree()
    {
        // PlaylistTree is the ONLY planner path that stamps a row's nesting depth and emits the given order verbatim,
        // so the two lenses that can contain folders must use it.
        foreach (var filter in new[] { SidebarV3Filter.All, SidebarV3Filter.Playlists })
            foreach (var view in new[] { SidebarV3View.CompactList, SidebarV3View.List })
            {
                var state = State(filter, view);
                Assert.True(LibraryV3Document.FoldersApply(in state));
                Assert.Equal(SidebarSectionKind.PlaylistTree, Library(LibraryV3Document.Build(in state)).Kind);
            }
    }

    [Fact]
    public void FlatLenses_RenderAsAnEntityList()
    {
        foreach (var filter in new[] { SidebarV3Filter.Albums, SidebarV3Filter.Artists, SidebarV3Filter.Podcasts })
        {
            var state = State(filter);
            Assert.False(LibraryV3Document.FoldersApply(in state));
            Assert.Equal(SidebarSectionKind.EntityList, Library(LibraryV3Document.Build(in state)).Kind);
        }
    }

    [Fact]
    public void GridViews_AlwaysRenderAsAnEntityList_BecauseATreeCannotPresentAGrid()
    {
        foreach (var view in new[] { SidebarV3View.CompactGrid, SidebarV3View.Grid })
        {
            var section = Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, view)));
            Assert.Equal(SidebarSectionKind.EntityList, section.Kind);
            Assert.Equal(SidebarPresentation.Grid, section.Opts.Presentation);
        }
    }

    [Fact]
    public void Searching_FlattensToAnEntityList_AndDissolvesThePinBand()
    {
        // A search flattens the tree and its results are ONE relevance list: the matching pins still lead the
        // projection, but they are not a separate band — which is what makes "no rows" mean "no results".
        var state = State(SidebarV3Filter.Playlists, searching: true, hasPins: true);
        var doc = LibraryV3Document.Build(in state);
        Assert.Equal(SidebarSectionKind.EntityList, Library(doc).Kind);
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
        Assert.Null(Find(doc, LibraryV3Document.LikedId));
    }

    [Fact]
    public void ADrillLevel_IsOneFlatFolderLevel_WithNoPinBandAndNoShortcut()
    {
        var state = State(SidebarV3Filter.Playlists, drill: "f1", hasPins: true);
        var doc = LibraryV3Document.Build(in state);
        Assert.True(state.Drilled);
        Assert.False(LibraryV3Document.FoldersApply(in state));      // the level is already flat
        Assert.Equal(SidebarSectionKind.EntityList, Library(doc).Kind);
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
        Assert.Null(Find(doc, LibraryV3Document.LikedId));
    }

    // ── the view code → presentation / density / columns ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarV3View.CompactList, SidebarPresentation.List, SidebarDensity.Compact, false)]
    [InlineData(SidebarV3View.List, SidebarPresentation.List, SidebarDensity.Cozy, true)]
    [InlineData(SidebarV3View.CompactGrid, SidebarPresentation.Grid, SidebarDensity.Cozy, false)]
    [InlineData(SidebarV3View.Grid, SidebarPresentation.Grid, SidebarDensity.Cozy, true)]
    public void TheViewCode_DrivesPresentationDensityAndSubtitles(SidebarV3View view,
        SidebarPresentation presentation, SidebarDensity density, bool subtitles)
    {
        // Through the ONE shared height ladder these are 32 (Compact) and 44 (Cozy + subtitle) — the two row heights
        // the landed V3 list hard-coded per view.
        var opts = Library(LibraryV3Document.Build(State(view: view))).Opts;
        Assert.Equal(presentation, opts.Presentation);
        Assert.Equal(density, opts.Density);
        Assert.Equal(subtitles, opts.Subtitles);
        Assert.True(opts.Artwork);
        Assert.False(opts.CountBadges);
        Assert.Equal(0, opts.MaxItems);
    }

    [Theory]
    [InlineData(0, 2)]     // a pane too narrow for two columns still gets two: the pane's strip wraps
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 4)]     // the reducer's [2,4] range binds a DERIVED count exactly as it binds a persisted one
    public void TheDerivedColumnCount_IsClampedToTheDocumentRange(int derived, int expected)
    {
        var opts = Library(LibraryV3Document.Build(State(view: SidebarV3View.Grid, columns: derived))).Opts;
        Assert.Equal(expected, opts.GridColumns);
        Assert.Equal(expected, LibraryV3Document.ClampColumns(derived));
    }

    [Fact]
    public void ThePinBand_MirrorsTheContentLadder()
    {
        // One ladder for both bands: a pin row and a library row of the same view must be the same height, in the
        // same presentation, or the pane reads as two lists.
        var doc = LibraryV3Document.Build(State(view: SidebarV3View.Grid, hasPins: true, columns: 3));
        var pins = Find(doc, LibraryV3Document.PinsId);
        Assert.NotNull(pins);
        Assert.Equal(SidebarSectionKind.Pinned, pins!.Kind);
        var library = Library(doc).Opts;
        Assert.Equal(library.Presentation, pins.Opts.Presentation);
        Assert.Equal(library.Density, pins.Opts.Density);
        Assert.Equal(library.Subtitles, pins.Opts.Subtitles);
        Assert.Equal(library.GridColumns, pins.Opts.GridColumns);
    }

    // ── the pin band + the Liked Songs shortcut ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePinBand_ExistsOnlyWhenAPinSurvivedTheLens()
    {
        Assert.Null(Find(LibraryV3Document.Build(State(hasPins: false)), LibraryV3Document.PinsId));
        Assert.NotNull(Find(LibraryV3Document.Build(State(hasPins: true)), LibraryV3Document.PinsId));
    }

    [Fact]
    public void ThePinBand_LeadsTheDocument_AndTheShortcutFollowsIt()
    {
        // The order is now pins → the library. Liked Songs left the DOCUMENT when the chrome's destination strip
        // took over every fixed destination (#85 H4): one destination, one row.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        Assert.Equal(new[] { LibraryV3Document.PinsId, LibraryV3Document.LibraryId }, IdsOf(doc));
    }

    [Theory]
    [InlineData(SidebarV3Filter.All)]
    [InlineData(SidebarV3Filter.Playlists)]
    [InlineData(SidebarV3Filter.Albums)]
    [InlineData(SidebarV3Filter.Artists)]
    [InlineData(SidebarV3Filter.Podcasts)]
    public void TheLikedShortcut_NeverRidesTheDocument_TheChromeStripCarriesIt(SidebarV3Filter filter)
    {
        // It used to be a list row scoped to the lenses where a saved-songs shortcut reads truthfully. It is now in
        // the chrome instead (the destination strip), which is ALWAYS present and therefore truthful under every
        // lens — so the document emits it under none of them.
        Assert.Null(Find(LibraryV3Document.Build(State(filter)), LibraryV3Document.LikedId));
        Assert.True(LibraryV3Document.ChromeCarriesDestinations);
    }

    [Fact]
    public void TheLikedShortcut_IsAbsentWhenItIsItselfPinned()
    {
        // It is then rendered as pin #n — never twice.
        Assert.Null(Find(LibraryV3Document.Build(State(hasPins: true, likedPinned: true)), LibraryV3Document.LikedId));
    }

    [Fact]
    public void EveryFixedDestination_IsOwnedByTheOneSharedList()
    {
        // The strip, the Classic section and anything else that needs "which routes are fixed library destinations"
        // read SidebarShortcutsSection.LibraryDestinations. A second copy is how the same row gets rendered twice.
        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "local" },
            SidebarShortcutsSection.LibraryDestinations);
        foreach (var key in SidebarShortcutsSection.LibraryDestinations)
            Assert.True(SidebarShortcutsSection.IsLibraryDestination(key));
        Assert.False(SidebarShortcutsSection.IsLibraryDestination("home"));
        Assert.False(SidebarShortcutsSection.IsLibraryDestination(null));
    }

    [Fact]
    public void PinsBand_StaysHiddenAtRest_WithNoPinsAndNoDrag()
        => Assert.Null(Find(LibraryV3Document.Build(State(hasPins: false, dragInFlight: false)),
                             LibraryV3Document.PinsId));

    [Fact]
    public void PinsBand_AppearsWithZeroPins_WhileADragIsLive()
    {
        // With zero pins the band used to disappear ENTIRELY, so a first pin could never be made by drag (the only
        // other path in is a row's context menu). The band's own drop-zone card is what makes the empty band useful
        // — this only asserts the SECTION exists so that card gets a chance to mount.
        var doc = LibraryV3Document.Build(State(hasPins: false, dragInFlight: true));
        Assert.NotNull(Find(doc, LibraryV3Document.PinsId));
    }

    [Fact]
    public void PinsBand_StaysDissolvedByASearch_EvenMidDrag()
    {
        // A search flattening the tree is a stronger rule than "a drag is live" — the pin band never reappears while
        // searching, whatever else is going on.
        var doc = LibraryV3Document.Build(State(hasPins: false, dragInFlight: true, searching: true));
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
    }

    [Fact]
    public void PinsBand_StaysAbsentAtADrilledLevel_EvenMidDrag()
    {
        var doc = LibraryV3Document.Build(State(hasPins: false, dragInFlight: true, drill: "folder-1"));
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
    }

    // ── H4 (#85) — the missing library destinations, reachable through the seeded top bar ─────────────────────────────
    //
    // DROPPED as duplicates (see file header): DefaultTopBar_StaysTheHomeShortcutAlone and
    // Build_NeverEmitsTheTopBarSentinelSection — both already pinned in SidebarReducerTests.cs.

    [Fact]
    public void TheDestinationStrip_MakesEveryFixedRouteReachable_OnADefaultInstall()
    {
        // The point of H4: a default V3 install can reach all five without customizing anything. They are not in the
        // document and not in the top bar — the chrome owns them — so this asserts the contract the chrome renders
        // against, and that the document does not also emit one of them even when a real shell band is handed in.
        Assert.True(LibraryV3Document.ChromeCarriesDestinations);
        Assert.Contains(LibraryV3Document.LikedRouteKey, SidebarShortcutsSection.LibraryDestinations);
        Assert.Null(Find(LibraryV3Document.Build(State(), SidebarCustomLayout.DefaultTopBar),
                         LibraryV3Document.LikedId));
    }

    // ── the query mirror ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarV3Filter.All, SidebarEntityKinds.All)]
    [InlineData(SidebarV3Filter.Playlists, SidebarEntityKinds.Playlists)]
    [InlineData(SidebarV3Filter.Albums, SidebarEntityKinds.Albums)]
    [InlineData(SidebarV3Filter.Artists, SidebarEntityKinds.Artists)]
    [InlineData(SidebarV3Filter.Podcasts, SidebarEntityKinds.Shows)]
    public void TheQuery_MirrorsTheLensKinds(SidebarV3Filter filter, SidebarEntityKinds kinds)
    {
        var query = Library(LibraryV3Document.Build(State(filter, searching: true))).Query;
        Assert.NotNull(query);
        Assert.Equal(kinds, query!.Kinds);
        Assert.Equal(kinds, LibraryV3Document.KindsFor((int)filter));
    }

    [Theory]
    [InlineData(SidebarV3Sort.Recents, SidebarSortMode.Recents)]
    [InlineData(SidebarV3Sort.RecentlyAdded, SidebarSortMode.RecentlyAdded)]
    [InlineData(SidebarV3Sort.Alphabetical, SidebarSortMode.Alphabetical)]
    [InlineData(SidebarV3Sort.Creator, SidebarSortMode.Creator)]
    [InlineData(SidebarV3Sort.Custom, SidebarSortMode.CustomOrder)]
    public void TheQuery_MirrorsTheSortMode(SidebarV3Sort sort, SidebarSortMode mode)
    {
        var query = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, sort: sort, searching: true))).Query;
        Assert.Equal(mode, query!.Sort);
    }

    [Fact]
    public void CustomOrder_OutsideThePlaylistsLens_FallsBackToAlphabetical()
    {
        // The fallback is FOR DISPLAY — exactly what SidebarSort.Effective does for the projection — and the
        // persisted preference is never rewritten here.
        var query = Library(LibraryV3Document.Build(State(SidebarV3Filter.Albums, sort: SidebarV3Sort.Custom))).Query;
        Assert.Equal(SidebarSortMode.Alphabetical, query!.Sort);
        Assert.Equal(SidebarSortMode.CustomOrder,
            LibraryV3Document.SortFor((int)SidebarV3Sort.Custom, (int)SidebarV3Filter.Playlists));
    }

    [Theory]
    // V3's flag means "REVERSE the sort's natural direction"; the query's means "descending" literally, and the
    // planner's comparator undoes that mapping again for the two recency modes. Only those two therefore invert.
    [InlineData(SidebarV3Sort.Recents, false, true)]
    [InlineData(SidebarV3Sort.Recents, true, false)]
    [InlineData(SidebarV3Sort.RecentlyAdded, false, true)]
    [InlineData(SidebarV3Sort.Alphabetical, false, false)]
    [InlineData(SidebarV3Sort.Alphabetical, true, true)]
    [InlineData(SidebarV3Sort.Creator, true, true)]
    public void TheQuery_ReconcilesTheDirectionVocabulary(SidebarV3Sort sort, bool v3Descending, bool queryDescending)
    {
        var query = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, sort: sort, descending: v3Descending, searching: true))).Query;
        Assert.Equal(queryDescending, query!.Descending);
    }

    [Fact]
    public void TheQualifier_IsTheEFFECTIVEOne_NeverThePersistedOne()
    {
        // Two coercions the projection already applied, mirrored so the query can never filter MORE than the rows it
        // describes: a qualifier the data cannot evidence, and a qualifier outside the Playlists lens.
        Assert.Equal(SidebarPlaylistQualifier.Any,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists,
                qualifier: SidebarV3Qualifier.ByYou, qualifiersAvailable: false, searching: true))).Query!.Qualifier);

        Assert.Equal(SidebarPlaylistQualifier.Any,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Albums,
                qualifier: SidebarV3Qualifier.ByYou, qualifiersAvailable: true))).Query!.Qualifier);

        Assert.Equal(SidebarPlaylistQualifier.BySpotify,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists,
                qualifier: SidebarV3Qualifier.BySpotify, qualifiersAvailable: true, searching: true))).Query!.Qualifier);
    }

    [Fact]
    public void TreeDocument_PreservesTheShapedOrderWithANullQuery()
    {
        var tree = Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, SidebarV3View.List)));
        Assert.Equal(SidebarSectionKind.PlaylistTree, tree.Kind);
        Assert.Null(tree.Query);

        var flat = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, SidebarV3View.List, searching: true)));
        Assert.Equal(SidebarSectionKind.EntityList, flat.Kind);
        Assert.NotNull(flat.Query);
    }

    // ── document-level invariants ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionIds_AreStableAcrossEveryState()
    {
        // The pane keys its reorder bands, its scroll identity and its section lookup off these ids, and this
        // document is rebuilt on every keystroke — a minted id per rebuild would reset all three continuously.
        string[] states =
        [
            .. IdsOf(LibraryV3Document.Build(State(hasPins: true))),
            .. IdsOf(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, SidebarV3View.Grid, hasPins: true))),
            .. IdsOf(LibraryV3Document.Build(State(SidebarV3Filter.Albums, searching: true, hasPins: true))),
        ];
        foreach (string id in states)
            Assert.Contains(id, new[] { LibraryV3Document.PinsId, LibraryV3Document.LikedId, LibraryV3Document.LibraryId });

        Assert.Equal(IdsOf(LibraryV3Document.Build(State(hasPins: true))),
                     IdsOf(LibraryV3Document.Build(State(hasPins: true))));
    }

    [Fact]
    public void NoSectionCarriesATitle_SoThePaneEmitsNoHeaderRows()
    {
        // V3 has no section headers — structure is the chrome's job. A title (or a title loc key) would make the
        // planner emit a SectionHeader row and hand it the quick-layout menu V3 keeps in its overflow.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        foreach (var s in doc.Sections)
        {
            Assert.Null(s.Title);
            Assert.Null(s.TitleLocKey);
            Assert.False(s.Collapsed);
            Assert.False(s.Hidden);
        }
    }

    [Fact]
    public void TheLibrarySection_SuppressesTheSharedEmptyBody()
    {
        // The three empty states are ACTIONABLE and name the query, so V3's chrome owns them; the shared renderer's
        // quiet one-line hint would be a second, weaker message under them.
        Assert.Equal(SidebarEmptyBehavior.HideBody, Library(LibraryV3Document.Build(State())).Opts.EmptyBehavior);
    }

    [Fact]
    public void EverySection_ShowsInTheRail_SoACollapsedPaneHonoursTheLens()
    {
        // The rail's tiles are the document's (pins first, then the current filtered library), which is what makes
        // collapsing the pane never silently widen the visible set.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        foreach (var s in doc.Sections) Assert.True(s.Opts.ShowInRail);
    }

    [Fact]
    public void TheDocument_IsNotACuratedTemplate()
    {
        string id = LibraryV3Document.Build(State()).TemplateId;
        Assert.Equal(LibraryV3Document.TemplateId, id);
        foreach (string template in SidebarTemplates.All) Assert.NotEqual(template, id);
    }
}

// The V3 content ORDER: the one thing the retired LibraryV3Index was genuinely for. The published projection is
// sorted FLAT, so a nested playlist can land above the folder that contains it; the re-grouping puts folders among
// their siblings and each folder's children ordered within it. These tests pin that re-grouping, the drill slice,
// and the two facts the custom-order commit depends on (the sibling clamp and the materialized order).
public sealed class LibraryV3ViewTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // `Cover: StringId.Empty` — not `Cover: null` as 0.2.9's fixtures wrote it: SidebarLibraryEntry.Cover is a
    // non-nullable `StringId` in 0.3 (Shell/Sidebar.cs), and StringId has no implicit conversion from a null literal.

    static SidebarLibraryEntry Playlist(string slug, string folderId = "", int order = 0, int depth = 0)
        => new(Id: "pl:spotify:playlist:" + slug, Kind: SidebarEntryKind.Playlist,
               Uri: "spotify:playlist:" + slug, Name: slug, Creator: "Owner", Cover: StringId.Empty, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0, SourceOrder: order, Depth: depth,
               Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = folderId, FolderName = folderId, FirstArtistName = "" };

    static SidebarLibraryEntry Folder(string id, int order = 0, int depth = 0)
        => new(Id: "folder:" + id, Kind: SidebarEntryKind.Folder, Uri: "", Name: id, Creator: "", Cover: StringId.Empty,
               MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
               SourceOrder: order, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = id, FolderName = id, FirstArtistName = "" };

    static SidebarLibraryEntry Album(string slug, int order = 0)
        => new(Id: "album:spotify:album:" + slug, Kind: SidebarEntryKind.Album, Uri: "spotify:album:" + slug,
               Name: slug, Creator: "Artist", Cover: StringId.Empty, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0,
               SortStamp: 2, LastVisitedTicksUtc: 0, SourceOrder: order, Depth: 0, Circular: false,
               Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };

    /// <summary>The binder's fully flattened tree slice — folders included at every depth, which is the ONLY place a
    /// folder's PARENT is recoverable (a folder row's own FolderId is itself).</summary>
    static SidebarLibraryEntry[] Tree() =>
    [
        Folder("outer", order: 0, depth: 0),
        Folder("inner", order: 1, depth: 1),
        Playlist("deep", folderId: "inner", order: 2, depth: 2),
        Playlist("mid", folderId: "outer", order: 3, depth: 1),
        Playlist("top", order: 4),
    ];

    static string[] NamesOf(LibraryV3View view)
    {
        var names = new string[view.Count];
        for (int i = 0; i < names.Length; i++) names[i] = view.Rows[i].Name;
        return names;
    }

    static int[] DepthsOf(LibraryV3View view)
    {
        var depths = new int[view.Count];
        for (int i = 0; i < depths.Length; i++) depths[i] = view.Rows[i].Depth;
        return depths;
    }

    // ── grouping ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRootLevel_ReGroupsChildrenUnderTheirFolder()
    {
        // The published order is what a FLAT sort produces: the nested playlists sort above the folders that contain
        // them.
        var published = new[]
        {
            Playlist("deep", folderId: "inner"),
            Playlist("mid", folderId: "outer"),
            Folder("outer"),
            Folder("inner"),
            Playlist("top"),
        };

        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);

        Assert.Equal(new[] { "outer", "mid", "inner", "deep", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 1, 1, 2, 0 }, DepthsOf(view));
    }

    [Fact]
    public void Grouping_PreservesTheSiblingOrderTheProjectionPublished()
    {
        // Siblings keep their published (sorted) order — the re-grouping only moves children UNDER their parent, it
        // never re-sorts a level.
        var published = new[] { Folder("outer"), Playlist("b", folderId: "outer"), Playlist("a", folderId: "outer") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "b", "a" }, NamesOf(view));
    }

    [Fact]
    public void ARowWhoseFolderIsNotVisible_IsPromotedToTopLevel()
    {
        // Nothing is ever hidden because its container happens to be elsewhere (pinned into the band, dropped by the
        // lens, or a cold tree) — that would silently lose playlists.
        var published = new[] { Playlist("orphan", folderId: "outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "orphan", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
    }

    [Fact]
    public void TheLeadingPinBand_IsSkipped_WhenItIsRenderedAsItsOwnSection()
    {
        var published = new[] { Playlist("pinned"), Folder("outer"), Playlist("mid", folderId: "outer") };
        var view = new LibraryV3View();
        view.Build(published, 1, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "mid" }, NamesOf(view));
    }

    [Fact]
    public void FlatMode_PassesTheSliceThrough_AtDepthZero()
    {
        // A search has already flattened the projection and a grid cannot express disclosure: both want the
        // published order verbatim, with no indent inherited from the tree the entries came out of.
        var published = new[] { Playlist("deep", folderId: "inner", depth: 2), Album("one"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { "deep", "one", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0, 0 }, DepthsOf(view));
    }

    // ── the drill level ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADrillLevel_IsExactlyOneFoldersDirectChildren_AtDepthZero()
    {
        var published = new[]
        {
            Folder("outer"), Folder("inner"), Playlist("mid", folderId: "outer"),
            Playlist("deep", folderId: "inner"), Playlist("top"),
        };

        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);

        // "inner" is a child folder of "outer" and stays a row (it can be drilled into again); "deep" belongs to
        // inner and does NOT leak into this level.
        Assert.Equal(new[] { "inner", "mid" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrillLevel_IgnoresTheSkip_BecauseThereIsNoPinBandInside()
    {
        // A pinned playlist that lives inside the folder must still appear inside it.
        var published = new[] { Playlist("mid", folderId: "outer"), Folder("outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 1, Tree(), 1, "outer", group: true);
        Assert.Equal(new[] { "mid" }, NamesOf(view));
    }

    [Fact]
    public void AnEmptyFolder_IsALegitimateLevel_NotAMissingTarget()
    {
        // Popping out of an empty folder would make an empty folder impossible to open.
        var published = new[] { Folder("outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.Equal(0, view.Count);
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrilledFolderThatVanished_IsReportedMissing()
    {
        var published = new[] { Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.True(view.DrillTargetMissing);
        Assert.Equal(0, view.Count);
    }

    [Fact]
    public void AColdProjection_IsNotAMissingDrillTarget()
    {
        var view = new LibraryV3View();
        view.Build(Array.Empty<SidebarLibraryEntry>(), 0, Tree(), 1, "outer", group: true);
        Assert.False(view.DrillTargetMissing);
        Assert.Equal(0, view.Count);
    }

    // ── the two facts the custom-order commit rests on ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceOrder_IsRewrittenToThePosition_SoAReSortIsANoOp()
    {
        // The planner's EntityList path re-sorts, and its CustomOrder comparator (with no rank map) is SourceOrder
        // ascending — so stamping the position here is what makes the grid views reproduce this exact order.
        var published = new[] { Playlist("a", order: 40), Playlist("b", order: 10), Playlist("c", order: 25) };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { view.Rows[0].SourceOrder, view.Rows[1].SourceOrder, view.Rows[2].SourceOrder });
    }

    [Fact]
    public void SameParent_ClampsADragAcrossAFolderBoundary()
    {
        var published = new[] { Folder("outer"), Playlist("mid", folderId: "outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);

        // 0 = the folder row (top level), 1 = its child, 2 = a top-level playlist.
        Assert.Equal("", view.ParentOf(0));
        Assert.Equal("outer", view.ParentOf(1));
        Assert.True(view.SameParent(0, 2));
        Assert.False(view.SameParent(1, 2));
    }

    [Fact]
    public void MaterializeOrder_WritesTheWholeVisibleOrder_WithTheRowMoved()
    {
        var published = new[] { Playlist("a"), Playlist("b"), Playlist("c") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);

        var into = new List<string>();
        view.MaterializeOrder(into, 0, 2);
        Assert.Equal(new[] { view.KeyAt(1), view.KeyAt(2), view.KeyAt(0) }, into);

        view.MaterializeOrder(into, 2, 0);
        Assert.Equal(new[] { view.KeyAt(2), view.KeyAt(0), view.KeyAt(1) }, into);

        view.MaterializeOrder(into, 1, 1);
        Assert.Equal(new[] { view.KeyAt(0), view.KeyAt(1), view.KeyAt(2) }, into);
    }

    [Fact]
    public void MaterializeOrder_SkipsRowsThatArePartOfNoPlaylistOrder()
    {
        // An authored route row (Liked Songs, a pinned route) has no place in a playlist order.
        var published = new[] { SidebarLibraryEntry.ForRoute("liked", "Liked Songs"), Playlist("a"), Playlist("b") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);

        var into = new List<string>();
        view.MaterializeOrder(into, 1, 2);
        Assert.Equal(new[] { view.KeyAt(2), view.KeyAt(1) }, into);
    }

    [Fact]
    public void ARebuild_ReusesItsBuffers_AndNeverLeaksTheOldOrder()
    {
        var view = new LibraryV3View();
        view.Build(new[] { Folder("outer"), Playlist("mid", folderId: "outer") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(2, view.Count);

        view.Build(new[] { Playlist("top") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "top" }, NamesOf(view));

        view.Build(Array.Empty<SidebarLibraryEntry>(), 0, Tree(), 1, null, group: true);
        Assert.Equal(0, view.Count);
    }
}
