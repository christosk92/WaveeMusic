using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// LikedFactsPanel.cs and ArtistPortrait.cs are ENGINE-BOUND (FluentGpu.Controls / FluentGpu.Dsl / FluentGpu.Hooks —
// Component, Embed.Comp, BoxEl, UseProps, …) and Wavee.Tests.csproj carries no ProjectReference to the engine's
// Controls/Engine assemblies and no reference to Wavee.csproj itself (see the source-include ItemGroup: only pure,
// System+Wavee.Core files like LikedFactsRules.cs are pulled in by path). So the actual internal
// `LikedArtistsCard.Props`, `LikedBlendCard.Props`, `TempoCard.Props` and `ArtistPortrait.Props` records are NOT
// reachable from this project — not an accessibility problem (making them `public` would not help; the assembly
// itself is never compiled in), a project-wiring one. Widening Wavee.Tests to reference the engine is out of scope
// for this change (SidebarSubtitleRulesTests.cs documents the identical situation for Pane/SidebarPaneText.cs).
//
// So this file pins the CONTENT-equality CONTRACT those four Props records now promise (LikedFactsPanel.cs /
// ArtistPortrait.cs) by exercising the exact same comparisons — element-wise over the real
// LikedFactsRules.ArtistCount / TagShare value types and the real Wavee.Core Artist/Image records — against the
// mirrored predicates below. A regression that changes the shape of `ArtistCount`/`TagShare`/`Artist` in a way that
// breaks the intended "same content, different instance ⇒ equal" contract will show up here even though the Props
// records themselves cannot be constructed in this project.
public sealed class LikedFactsPanelPropsTests
{
    // ── mirrors of the Props' hand-written Equals bodies ────────────────────────────────────────────────────────────

    // LikedArtistsCard.Props.Equals' list comparison (Handlers/Liked elided — both are trivial reference/value
    // compares already covered by the record default).
    static bool RankedEqual(IReadOnlyList<LikedFactsRules.ArtistCount> a, IReadOnlyList<LikedFactsRules.ArtistCount> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // LikedBlendCard.Props.Equals' list comparison.
    static bool SharesEqual(IReadOnlyList<LikedFactsRules.TagShare> a, IReadOnlyList<LikedFactsRules.TagShare> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // ArtistPortrait.Props.Equals.
    static bool ArtistPortraitPropsEqual(Artist seedA, float sizeA, Artist seedB, float sizeB)
        => sizeA == sizeB
        && string.Equals(seedA.Uri, seedB.Uri, System.StringComparison.Ordinal)
        && string.Equals(seedA.Name, seedB.Name, System.StringComparison.Ordinal)
        && string.Equals(seedA.Image?.Url, seedB.Image?.Url, System.StringComparison.Ordinal);

    static ArtistRef Ref(string id) => new(id, "spotify:artist:" + id, "Artist " + id);

    // ── Ranked (LikedArtistsCard.Props) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ranked_EqualContentNewInstances_AreEqual()
    {
        var a = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 5), new(Ref("2"), 3) };
        var b = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 5), new(Ref("2"), 3) };

        Assert.NotSame(a, b);
        Assert.True(RankedEqual(a, b));
    }

    [Fact]
    public void Ranked_ChangedCount_AreNotEqual()
    {
        var a = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 5) };
        var b = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 6) };

        Assert.False(RankedEqual(a, b));
    }

    [Fact]
    public void Ranked_DifferentLength_AreNotEqual()
    {
        var a = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 5), new(Ref("2"), 3) };
        var b = new List<LikedFactsRules.ArtistCount> { new(Ref("1"), 5) };

        Assert.False(RankedEqual(a, b));
    }

    // ── Shares (LikedBlendCard.Props) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shares_EqualContentNewInstances_AreEqual()
    {
        var a = new List<LikedFactsRules.TagShare> { new("Pop", 40, 0.4f), new("Rock", 20, 0.2f) };
        var b = new List<LikedFactsRules.TagShare> { new("Pop", 40, 0.4f), new("Rock", 20, 0.2f) };

        Assert.NotSame(a, b);
        Assert.True(SharesEqual(a, b));
    }

    [Fact]
    public void Shares_ChangedFraction_AreNotEqual()
    {
        var a = new List<LikedFactsRules.TagShare> { new("Pop", 40, 0.4f) };
        var b = new List<LikedFactsRules.TagShare> { new("Pop", 40, 0.41f) };

        Assert.False(SharesEqual(a, b));
    }

    // ── ArtistPortrait.Props ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtistPortrait_RecreatedArtist_SameUriNameImage_IsEqual()
    {
        var a = new Artist("1", "spotify:artist:1", "Vaultboy", new Image("https://img/1.jpg"));
        // A fresh instance — as LikedArtistsCard.Resolve allocates on every render — carrying the same identity.
        var b = new Artist("1", "spotify:artist:1", "Vaultboy", new Image("https://img/1.jpg"));

        Assert.NotSame(a, b);
        Assert.True(ArtistPortraitPropsEqual(a, 28f, b, 28f));
    }

    [Fact]
    public void ArtistPortrait_DifferentImageUrl_IsNotEqual()
    {
        var a = new Artist("1", "spotify:artist:1", "Vaultboy", new Image("https://img/1.jpg"));
        var b = new Artist("1", "spotify:artist:1", "Vaultboy", new Image("https://img/2.jpg"));

        Assert.False(ArtistPortraitPropsEqual(a, 28f, b, 28f));
    }

    [Fact]
    public void ArtistPortrait_DifferentSize_IsNotEqual()
    {
        var a = new Artist("1", "spotify:artist:1", "Vaultboy", null);
        var b = new Artist("1", "spotify:artist:1", "Vaultboy", null);

        Assert.False(ArtistPortraitPropsEqual(a, 28f, b, 32f));
    }

    [Fact]
    public void ArtistPortrait_NullImageBothSides_IsEqual()
    {
        var a = new Artist("1", "spotify:artist:1", "Vaultboy", null);
        var b = new Artist("1", "spotify:artist:1", "Vaultboy", null);

        Assert.True(ArtistPortraitPropsEqual(a, 28f, b, 28f));
    }
}
