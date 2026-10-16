// ── Wavee.ShellTests/TierLayoutTests.Artist.cs — the artist and people pages at every tier size and threshold edge (themes plan 3.5; TC6) ───────
//
// The REAL Classic frame with each artist and people page on screen (Artist at 3840 too, a key surface; Discography, the concert hub, the concert event
// page, Profile and ProfileList at the page sizes), through `AssertPage` (TierLayoutTests.cs: the tier final in the first frame, the frame's records, the
// regions inside the window and apart, the Compact title-bar targets). Then, on the records, never on pixels:
//
//   · the window tier's page record is the one the core reads through the policy (`ThemePolicies.ArtistTiers`) and the one the views read;
//   · the hero's ARM on screen is the record's for the page's own width (one arm mounted once the page settled), and the hero is at most 60 % of a Short
//     window's page;
//   · at Compact every hit target of the hero's action row is at least 40 x 40.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── the fixtures (the fake catalogue's artist ar1, its discography, the concert hub and a feed show, the fake user mia-solace and her list) ──

    public static readonly TierPage ArtistPage = new("artist", Shell.For(EntityUri.Parse("spotify:artist:ar1"), "Artist"), "artist-page:");
    public static readonly TierPage DiscographyPage = new("discography", Shell.Parse("disco:0:spotify:artist:ar1"), "discography-page");
    public static readonly TierPage ConcertHub = new("concert hub", new Shell.Route(Shell.RouteKind.Concerts), "concert-hub:page");
    public static readonly TierPage ConcertEvent = new("concert", Shell.For(EntityUri.Parse("spotify:concert:cf0"), "Concert"), "concert-event:column");
    public static readonly TierPage ProfilePage = new("profile", ProfileRoute.For(EntityUri.Parse("spotify:user:mia-solace"), "Mia"), "profile-page:");
    public static readonly TierPage ProfileListPage = new("profile list",
        ProfileListRoute.For(EntityUri.Parse("spotify:user:mia-solace"), ProfileFacet.Following), "people:page");

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_artist_page_holds_its_tier_layout(string size) => AssertPage(ArtistPage, size, (shell, tiers) => AssertArtist(shell, tiers, size));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_discography_page_holds_its_tier_layout(string size) => AssertPage(DiscographyPage, size, (shell, tiers) =>
    {
        var layout = ClassicArtistTiers.Of(tiers);
        Assert.Equal(Sizes[size].Window, layout.Tier);
        Assert.Equal(Sizes[size].Window == WidthTier.Compact ? 128f : 180f, layout.DiscoMinColumn);
    });

    [Theory, MemberData(nameof(PageSizes))]
    public void The_concert_hub_holds_its_tier_layout(string size) => AssertPage(ConcertHub, size, (shell, tiers) =>
        Assert.Equal(Sizes[size].Window, ClassicConcertTiers.Of(tiers).Tier));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_concert_event_page_holds_its_tier_layout(string size) => AssertPage(ConcertEvent, size, (shell, tiers) =>
    {
        var record = ClassicConcertTiers.Of(tiers);
        Assert.Equal(Sizes[size].Window, record.Tier);
        Assert.Equal(Sizes[size].Window == WidthTier.Compact ? 40f : 32f, record.Target);
        Assert.Equal(Sizes[size].Window switch { WidthTier.Compact or WidthTier.Small => 0f, WidthTier.Standard => 320f, _ => 360f }, record.RailWidth);
    });

    [Theory, MemberData(nameof(PageSizes))]
    public void The_profile_page_holds_its_tier_layout(string size) => AssertPage(ProfilePage, size, (shell, tiers) => AssertProfile(shell, tiers, size));

    [Theory, MemberData(nameof(PageSizes))]
    public void The_profile_list_page_holds_its_tier_layout(string size) => AssertPage(ProfileListPage, size, (shell, tiers) =>
    {
        var record = ClassicProfileTiers.Of(tiers);
        Assert.Equal(Sizes[size].Window, record.Tier);
        Assert.Equal(record.ListMinCell, ShellPolicy.Now.ArtistTiers.ProfileListMinCell(tiers));
    });

    // ── the artist page's own assertions ────────────────────────────────────────────────────────────────────────────

    static void AssertArtist(Shell.HeadlessShell shell, TierFacts tiers, string sizeName)
    {
        var size = Sizes[sizeName];
        var scene = shell.Host.Scene;

        // The record in use is the structure's for the window tier, the same one the core's chart reads.
        var layout = ClassicArtistTiers.Of(tiers);
        Assert.Equal(size.Window, layout.Tier);
        Assert.Same(ClassicArtistTiers.Policy, ShellPolicy.Now.ArtistTiers);
        Assert.Equal(layout.ChartColumns, ShellPolicy.Now.ArtistTiers.ChartMaxColumns(tiers));

        // The hero: one arm once the page has settled, the record's for the page's own width.
        var page = shell.FindPrefix("artist-page:");
        Assert.True(page.Count >= 1, $"{size.Name}: the artist page is on screen");
        float pageW = scene.AbsoluteRect(page[0].Node).W;
        var arms = shell.FindPrefix("artist-hero:");
        if (arms.Count == 0) return;   // the body is still pending: the records above are what the first frame lays out
        Assert.True(arms.Count == 1, $"{size.Name}: {arms.Count} hero arms mounted ({string.Join(", ", arms.Select(a => a.Key))})");
        var form = ClassicArtistTiers.HeroForm(ClassicArtistTiers.HeroTierFor(pageW), in tiers, pageW);
        if (size.From is null)
            Assert.True(arms[0].Key == "artist-hero:" + form.Arm.ToString().ToLowerInvariant(), $"{size.Name}: the {pageW}-wide hero shows {arms[0].Key}, not {form.Arm}");
        float heroH = scene.AbsoluteRect(arms[0].Node).H;
        if (size.Tall == HeightTier.Short)
            Assert.True(heroH <= ClassicArtistTiers.ShortHeroCap + 0.5f, $"{size.Name}: the hero is {heroH} tall in a Short window");
        if (size.Window == WidthTier.Compact) AssertTargets(shell, size, "artist-hero-actions");
    }

    // ── the profile page's own assertions ───────────────────────────────────────────────────────────────────────────

    static void AssertProfile(Shell.HeadlessShell shell, TierFacts tiers, string sizeName)
    {
        var size = Sizes[sizeName];
        var record = ClassicProfileTiers.Of(tiers);
        Assert.Equal(size.Window, record.Tier);
        Assert.Equal(size.Window == WidthTier.Compact ? 164f : 0f, record.Shelf.FixedCardW);
        if (size.Window == WidthTier.Compact) AssertTargets(shell, size, "profile-hero-actions");
    }

    /// <summary>At Compact every focusable target under the keyed row <paramref name="prefix"/> is at least 40 x 40 (the brief's Compact rule).</summary>
    static void AssertTargets(Shell.HeadlessShell shell, TierSize size, string prefix)
    {
        var scene = shell.Host.Scene;
        foreach (var (root, _) in shell.FindPrefix(prefix))
            foreach (var n in Subtree(scene, root))
            {
                if (scene.InCollapsedSubtree(n)) continue;
                var flags = scene.Flags(n);
                if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W <= 0f || r.H <= 0f) continue;
                Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a {prefix} target is {r.W} x {r.H}, under 40 x 40");
            }
    }
}
