using Xunit;

namespace Wavee.Tests;

/// <summary>C8.6 — per-mode remembered state (locked decision 3) and the per-design width tiers (locked decision 14).
/// Drives the REAL rules: <see cref="SidebarPaneState"/> (the snapshot/restore/latch decisions behind
/// <c>SidebarPreferences.SwitchDesign</c>), <see cref="SidebarDesignInfo"/> and <see cref="ShellResponsiveLayout"/>, over an
/// in-memory <see cref="MemoryAppSettings"/>. No engine, no window, no registry — which is exactly why the switch semantics
/// are testable at all.</summary>
public class SidebarModeStateTests
{
    const float Min = ShellResponsiveLayout.NavPaneMinW;   // 240
    const float Max = ShellResponsiveLayout.NavPaneMaxW;   // 460

    // ── tier tables (locked decision 14) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachDesign_HasItsOwnTierTriple()
    {
        Assert.Equal((240f, 280f, 320f), ShellResponsiveLayout.NavPaneTiers(SidebarDesign.Classic));
        Assert.Equal((300f, 340f, 380f), ShellResponsiveLayout.NavPaneTiers(SidebarDesign.LibraryV3));
        Assert.Equal((280f, 320f, 360f), ShellResponsiveLayout.NavPaneTiers(SidebarDesign.Curated));

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
        // The 1400/1800 enters and the 24-DIP shrink hysteresis are identical for all three designs — only the tier VALUES
        // differ. V3: 1400 widens to 340 at once, and 340 holds down to 1376.
        var v3 = SidebarDesignInfo.Tiers(SidebarDesign.LibraryV3);
        Assert.Equal(340f, ShellResponsiveLayout.NavPaneDefaultFor(1400f, 300f, initialized: true, v3));
        Assert.Equal(340f, ShellResponsiveLayout.NavPaneDefaultFor(1380f, 340f, initialized: true, v3));
        Assert.Equal(300f, ShellResponsiveLayout.NavPaneDefaultFor(1370f, 340f, initialized: true, v3));

        // …and the no-triple overloads still mean CLASSIC, so every pre-existing shell call site is unchanged.
        Assert.Equal(ShellResponsiveLayout.NominalNavPaneDefaultFor(1500f),
                     ShellResponsiveLayout.NominalNavPaneDefaultFor(1500f, SidebarDesignInfo.Tiers(SidebarDesign.Classic)));
        Assert.Equal(280f, ShellResponsiveLayout.NominalNavPaneDefaultFor(1500f));
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

        // Curated was last seen at a NARROW window and wrote 280 as its responsive default (never a drag ⇒ never latched).
        s.Set(SidebarKeys.Width(SidebarDesign.Curated), 280f);

        // The window is now wide. Because the flag is false, the stored 280 is only a stale responsive default and the
        // restore hands back the design's tier at the LIVE viewport — this is the re-seed §3.0 obligation 1 requires.
        Assert.Equal(360f, SidebarPaneState.Restore(s, SidebarDesign.Curated, 1900f).Width);

        // Once it IS latched, the stored width wins at every viewport.
        SidebarPaneState.CommitWidth(s, SidebarDesign.Curated, 280f);
        Assert.Equal(280f, SidebarPaneState.Restore(s, SidebarDesign.Curated, 1900f).Width);
    }

    [Fact]
    public void CollapsedIsNotAWidthChoice()
    {
        var s = new MemoryAppSettings();

        // Collapsing writes ONLY the collapse key — the invariant the uncommitted shell work just fixed, now per design.
        s.Set(SidebarKeys.Collapsed(SidebarDesign.Classic), true);

        var restored = SidebarPaneState.Restore(s, SidebarDesign.Classic, 1900f);
        Assert.True(restored.Collapsed);
        Assert.False(restored.WidthUserSet);
        Assert.False(s.WasWritten(SidebarKeys.WidthUserSet(SidebarDesign.Classic)));
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
        Assert.False(s.Get(SidebarKeys.WidthUserSet(SidebarDesign.LibraryV3)));
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
        s.Set(SidebarKeys.Width(SidebarDesign.Classic), 5000f);      // a hand-edited / older-build value
        Assert.Equal(Max, SidebarPaneState.Restore(s, SidebarDesign.Classic, 1500f).Width);
    }

    [Fact]
    public void SwitchingDesigns_TouchesOnlyThatDesignsPaneKeys()
    {
        // The shared-pins invariant at the persistence layer: a design switch snapshots/restores the per-design PANE keys
        // and never writes a pin, a V3 custom-order or another design's key. Pins live in sidebar-layout.json precisely so
        // they can be shared by all three designs (locked decision 4).
        var s = new MemoryAppSettings();
        SidebarPaneState.Snapshot(s, SidebarDesign.Classic, new SidebarPaneSnapshot(300f, false, true));
        _ = SidebarPaneState.Restore(s, SidebarDesign.Curated, 1500f);

        Assert.Equal(3, s.WrittenCount);
        Assert.True(s.WasWritten(SidebarKeys.Width(SidebarDesign.Classic)));
        Assert.True(s.WasWritten(SidebarKeys.Collapsed(SidebarDesign.Classic)));
        Assert.True(s.WasWritten(SidebarKeys.WidthUserSet(SidebarDesign.Classic)));
        Assert.False(s.WasWritten(SidebarKeys.Width(SidebarDesign.Curated)));
        Assert.False(s.WasWritten(SidebarKeys.Width(SidebarDesign.LibraryV3)));
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
        var mountKeys = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
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
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var d in SidebarDesignInfo.All)
        {
            Assert.True(names.Add(SidebarKeys.Width(d).Name));
            Assert.True(names.Add(SidebarKeys.WidthUserSet(d).Name));
            Assert.True(names.Add(SidebarKeys.Collapsed(d).Name));
            Assert.StartsWith("sidebar." + SidebarDesignInfo.Slug(d) + ".", SidebarKeys.Width(d).Name);
            // Each design's width key DEFAULT is its own narrow tier, so an absent key already reads correctly.
            Assert.Equal(SidebarDesignInfo.Tiers(d).Narrow, SidebarKeys.Width(d).Default);
        }
        // …and they must not collide with the legacy v0 global keys, which stay on disk for a downgrade.
        Assert.DoesNotContain("sidebar.width", names);
        Assert.DoesNotContain("sidebar.collapsed", names);
    }
}
