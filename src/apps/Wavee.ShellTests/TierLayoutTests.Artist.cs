// ── Wavee.ShellTests/TierLayoutTests.Artist.cs — the artist and people pages at every tier size and threshold edge (themes plan 3.5; TC6) ───────
//
// The REAL Classic frame with each artist and people page on screen (Artist at 3840 too, a key surface; Discography, the concert hub, the concert event
// page, Profile and ProfileList at the page sizes), through `AssertPage` (TierLayoutTests.cs: the tier final in the first frame, the frame's records, the
// regions inside the window and apart, the Compact title-bar targets). Then, on the records, never on pixels:
//
//   · the window tier's page record is the one the core reads through the policy (`ThemePolicies.ArtistTiers`) and the one the views read (Huge 4K's
//     past the 3200 rung: the chart's four rows);
//   · the hero's ARM on screen is the record's for the page's own width (one arm mounted once the page settled), and the hero is at most 60 % of a Short
//     window's page;
//   · ONE alignment source: the hero's copy starts where the magazine's sections start (the 4K review found the copy at the 36 gutter while Top tracks sat
//     in the capped, centred column);
//   · the 56 section row is under the hero at Huge only, the More button ends the Huge hero's actions, the albums are a strip at Compact only;
//   · at Compact every hit target of the hero's action row is at least 40 x 40, and the concert hub's pills are 40 tall.

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
        // The drawer's geometry per tier (C-Discography-Compact / -Small / -Huge), the one the core's verdict sizes.
        Assert.Equal(Sizes[size].Window switch { WidthTier.Compact => 1, WidthTier.Small => 2, WidthTier.Standard => 0, _ => 4 }, layout.Drawer.Columns);
        Assert.Equal(Sizes[size].Window == WidthTier.Compact ? 40f : 32f, layout.Drawer.RowPitch);
        Assert.Equal(Sizes[size].Window == WidthTier.Compact, !float.IsNaN(layout.DiscoTab));
    });

    [Theory, MemberData(nameof(PageSizes))]
    public void The_concert_hub_holds_its_tier_layout(string size) => AssertPage(ConcertHub, size, (shell, tiers) =>
    {
        var record = ClassicConcertTiers.Of(tiers);
        Assert.Equal(Sizes[size].Window, record.Tier);
        Assert.Equal(Sizes[size].Window == WidthTier.Compact ? 40f : 32f, record.HubPill);
        // The filter bar's when pill (the rest pill or the fused one, one key) is the record's height: the Compact 40 target.
        var scene = shell.Host.Scene;
        foreach (var (pill, _) in shell.FindPrefix("when-pill"))
        {
            if (scene.InCollapsedSubtree(pill)) continue;
            var r = scene.AbsoluteRect(pill);
            if (r.H > 0f) Assert.True(r.H >= record.HubPill - 0.5f, $"{size}: the when pill is {r.H} tall, under {record.HubPill}");
        }
    });

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

        // The record in use is the structure's for the window tier (Huge 4K's past the 3200 rung), the same one the core's chart reads.
        var layout = ClassicArtistTiers.Of(tiers);
        Assert.Equal(size.Window, layout.Tier);
        Assert.Same(ClassicDetailTiers.Policy.IsHugeWide(tiers) ? ClassicArtistTiers.Huge4K : ClassicArtistTiers.For(size.Window), layout);
        Assert.Same(ClassicArtistTiers.Policy, ShellPolicy.Now.ArtistTiers);
        Assert.Equal(layout.ChartColumns, ShellPolicy.Now.ArtistTiers.ChartMaxColumns(tiers));
        Assert.Equal(layout.ChartRows, ShellPolicy.Now.ArtistTiers.ChartRows(tiers));

        // The section row: 56 under the hero at Huge (55 under the divider), nothing elsewhere; mounted at every tier, so a crossing tweens it.
        var row = shell.Find("artist-section-row");
        if (!row.IsNull)
        {
            float rowH = scene.AbsoluteRect(row).H;
            float expected = layout.SectionRow ? ClassicArtistTiers.SectionRowHeight - 1f : 0f;
            Assert.True(MathF.Abs(rowH - expected) <= 0.5f, $"{size.Name}: the section row is {rowH} tall, not {expected}");
        }

        // The discography: a strip of album cards at Compact, the inline grid elsewhere (never both).
        Assert.Empty(shell.FindPrefix(layout.DiscoStrip ? "disco-arm:grid" : "disco-arm:strip"));

        // The pick's "Artist pick" heading (C-Artist-Huge, C-Artist-Huge4K): on the record's tiers alone and only in the rail arm (the pick beside the chart), standing over the
        // card on its left edge; the card itself is one keyed node whatever the heading does. Skipped for an artist with no pick and while the body is pending.
        var pickCard = shell.Find("featured:pick:card");
        var pickHeading = shell.Find("featured:pick:heading");
        if (!pickHeading.IsNull) Assert.True(layout.PickHeading, $"{size.Name}: an Artist pick heading below Huge");
        if (!pickCard.IsNull)
        {
            bool rail = !shell.Find("featured:pick:rail").IsNull;
            Assert.True(pickHeading.IsNull != (layout.PickHeading && rail), $"{size.Name}: the Artist pick heading is {(pickHeading.IsNull ? "missing" : "shown")} (record {layout.PickHeading}, rail arm {rail})");
            if (!pickHeading.IsNull)
            {
                var heading = scene.AbsoluteRect(pickHeading);
                var card = scene.AbsoluteRect(pickCard);
                Assert.True(heading.Y + heading.H <= card.Y + 0.5f, $"{size.Name}: the Artist pick heading ({heading}) overlaps the card ({card})");
                Assert.True(MathF.Abs(heading.X - card.X) <= 0.5f, $"{size.Name}: the Artist pick heading starts at {heading.X}, the card at {card.X}");
            }
        }

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

        // The More button ends the action row exactly where the form shows it (C-Artist-Huge).
        bool more = form.More && form.Actions != ClassicHeroActions.Primary;
        if (size.From is null)
            Assert.True(more == shell.FindPrefix("artist-hero-more:").Count > 0, $"{size.Name}: the hero's More button is {(more ? "missing" : "shown")}");

        // ONE alignment source: the hero's copy starts on the magazine's column (its sections' left edge) at every width, 4K included.
        var copy = shell.FindPrefix("artist-hero-copy");
        var sections = shell.FindPrefix("anchor:");
        if (copy.Count == 1 && sections.Count > 0 && size.From is null)
        {
            float copyX = scene.AbsoluteRect(copy[0].Node).X;
            float columnX = sections.Min(s => scene.AbsoluteRect(s.Node).X);
            Assert.True(MathF.Abs(copyX - columnX) <= 1f, $"{size.Name}: the hero's copy starts at {copyX}, the magazine's column at {columnX}");
            float pageX = scene.AbsoluteRect(page[0].Node).X;
            Assert.True(MathF.Abs(copyX - pageX - layout.ColumnInset(pageW, ClassicTiers.PageOf(tiers).Gutter)) <= 1f,
                $"{size.Name}: the hero's copy is {copyX - pageX} in, not the column inset");
        }
    }

    // ── the profile page's own assertions ───────────────────────────────────────────────────────────────────────────

    static void AssertProfile(Shell.HeadlessShell shell, TierFacts tiers, string sizeName)
    {
        var size = Sizes[sizeName];
        var record = ClassicProfileTiers.Of(tiers);
        Assert.Equal(size.Window, record.Tier);
        Assert.Equal(size.Window == WidthTier.Compact ? 164f : 0f, record.Shelf.FixedCardW);
        // The section pivot's words are the record's target tall: 40 at Compact (C-Profile-Compact's 48 row).
        Assert.Equal(size.Window == WidthTier.Compact ? 40f : 32f, record.Target);
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
