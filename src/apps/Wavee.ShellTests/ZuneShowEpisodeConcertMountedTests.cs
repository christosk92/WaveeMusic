// ── Wavee.ShellTests/ZuneShowEpisodeConcertMountedTests.cs — Zune's show, episode and concerts, mounted under Zune (themes plan 6.3; P5 Z6b2) ──
//
// The app mounted under Zune (`ZuneHost`) at 1440 x 960 (Standard), against the fake dataset (sh0: the returning serial; its episode sh0e3; the feed
// concert cf0):
//
//   registered   Zune's own registration draws every episode and concert key (zune.*) and registers the show reader's style
//   records      the concert records per content tier (the slab beside the facts from Small, the 40 targets at Compact, the stated tile extent)
//   show         the show page draws Zune's frame with the show reader's toolbar and the CTA's ring (resume or follow), and publishes row 2:
//                "episodes" as the view and follow / following as the action, which the band draws
//   episode      the episode page draws Zune's reader (its stacked sections, no pivot of its own) with the play ring, and publishes the four words
//                (about · chapters · transcript · comments) as row 2, the spy's section current, which the band draws
//   concert      the event page draws Zune's column (the date block, the facts) and publishes details (current) as row 2; the hub draws its head
//                over the pinned filter row
//   tiers        at 400 x 860 (Compact) the episode's ring is a 40 target and the concert column fits the page; at 3840 x 2160 the column stays
//                inside the 2400 page cap
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget; RunFrames(60) settles the page's entrance slide before a page
// rect is read. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;
using FluentGpu.Theming;

namespace Wavee.ShellTests;

public sealed class ZuneShowEpisodeConcertMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    const string ShowUri = "spotify:show:sh0", EpisodeUri = "spotify:episode:sh0e3", ConcertUri = "spotify:concert:cf0";

    static readonly Shell.Route s_show = Shell.For(EntityUri.Parse(ShowUri), "Show");
    static readonly Shell.Route s_episode = Shell.For(EntityUri.Parse(EpisodeUri), "Episode");
    static readonly Shell.Route s_concert = Shell.For(EntityUri.Parse(ConcertUri), "Concert");

    const string ConcertColumnKey = "zune-concert:column", ConcertDateKey = "zune-concert:date", HubPageKey = "concert-hub:page",
        HubHeadKey = "concert-hub:head", FilterRowKey = "concert-filter-bar:row";

    static PageChrome? Chrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    static string RingKey(string id) => ZuneDetailRules.RingPrefix + id;

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_episode_and_concert_key_and_registers_the_show_style()
    {
        var builder = new ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        string[] ids =
        [
            ViewKeys.EpisodePrimary.Id, ViewKeys.EpisodeAttribution.Id, ViewKeys.EpisodeActions.Id, ViewKeys.EpisodeSave.Id, ViewKeys.EpisodeRailAbout.Id,
            ViewKeys.EpisodeReader.Id, ViewKeys.EpisodeAbout.Id, ViewKeys.EpisodeChapters.Id, ViewKeys.EpisodeDiscussion.Id, ViewKeys.EpisodeCommentCard.Id,
            ViewKeys.EpisodeCommentSeed.Id, ViewKeys.EpisodeLoadMore.Id, ViewKeys.EpisodeReactionFlyout.Id, ViewKeys.EpisodeReactionRoster.Id,
            ViewKeys.EpisodeReactionPeople.Id, ViewKeys.EpisodeRating.Id,
            ViewKeys.ConcertsHub.Id, ViewKeys.ArtistConcerts.Id, ViewKeys.ConcertDetail.Id, ViewKeys.ConcertTail.Id, ViewKeys.ConcertPlacePicker.Id,
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = registry.TemplateIdOf(id) ?? "";
            Assert.True(template.StartsWith("zune.", StringComparison.Ordinal), $"{id} is drawn by '{template}'");
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.True(registry.TryGetStyle(StyleKeys.ShowReader, out var style) && style is not null, "Zune registers the show reader's style");

        // Zune's registration wins over the stand-in's, key by key: the composed registry holds Zune's style, not Classic's.
        var composed = new ZuneStructure(static views => new Wavee.Themes.Classic.ClassicStructure().Register(views));
        var merged = new ViewRegistryBuilder();
        composed.Register(merged);
        var both = merged.Build();
        Assert.True(both.TryGetStyle(StyleKeys.ShowReader, out var winner));
        Assert.Same(style, winner);
        Assert.Equal("zune.concert.detail", both.TemplateIdOf(ViewKeys.ConcertDetail.Id));
    }

    // ── the records (pure) ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_concert_records_follow_the_boards_per_content_tier()
    {
        Assert.Equal(0f, ZuneConcertTiers.Compact.TicketsW);            // the slab is stacked, bled to the gutters
        Assert.True(ZuneConcertTiers.Compact.SlabBleed);
        Assert.Equal(260f, ZuneConcertTiers.Small.TicketsW);
        Assert.Equal(320f, ZuneConcertTiers.Standard.TicketsW);
        Assert.Equal(440f, ZuneConcertTiers.Huge.TicketsW);
        Assert.Equal(150f, ZuneConcertTiers.Huge.NumeralSize);
        Assert.Equal(64f, ZuneConcertTiers.Standard.NumeralSize);

        // Compact targets are 40; the filter pills are 40 at Compact and 32 on the desktop tiers.
        Assert.True(ZuneConcertTiers.Compact.Target >= 40f);
        Assert.True(ZuneConcertTiers.Compact.OfferRowH >= 40f);
        Assert.Equal(40f, ZuneConcertTiers.Compact.HubPill);
        Assert.Equal(32f, ZuneConcertTiers.Standard.HubPill);

        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var record = ZuneConcertTiers.For(tier);
            Assert.Equal(tier, record.Tier);
            Assert.True(record.TitleLine >= record.TitleSize, "a title's line holds its size at " + tier);
            Assert.True(record.NumeralLine >= record.NumeralSize, "the numeral's line holds its size at " + tier);
            Assert.True(record.FactsLine >= record.FactsSize, "a fact's line holds its size at " + tier);
            Assert.True(record.RelatedColumns >= 1, "at least one related column at " + tier);
            Assert.True(record.GridMinColumn >= 150f, "the tile grid's column floor at " + tier);
        }

        // No frame resolved the tiers (a view mounted alone): Standard's record. The tile's stated extent is the lazy grid's arithmetic.
        Assert.Same(ZuneConcertTiers.Standard, ZuneConcertTiers.Of(TierFacts.Unknown));
        Assert.Equal(ZuneConcertRules.TileChrome + ZuneConcertRules.GridGap, ZuneConcertRules.GridRowExtra);
        Assert.Equal(ZuneConcertRules.TileGap + ZuneConcertRules.TileCaptionLine + ZuneConcertRules.TileTitleLine + ZuneConcertRules.TilePlaceLine
                     + 2f * ZuneConcertRules.TileTextGap, ZuneConcertRules.TileChrome);

        // The episode ring's label arrives as one string and is split at its dot.
        Assert.Equal(" · ", ZuneEpisodeRules.LabelSplit);
    }

    // ── the show ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_show_draws_zunes_frame_with_the_readers_toolbar_and_the_rail_ring()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_show);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.FrameKey).IsNull, "Zune's detail frame on sh0");
            Assert.True(ThemeHost.Registry.TryGetStyle(StyleKeys.ShowReader, out var style) && style is not null);
            Assert.True(shell.Find("detail:two-column").IsNull, "Classic's two-column page is not drawn");

            UntilBackground(shell, () => !shell.Find("tb:right").IsNull, "the show reader's sticky toolbar");
            UntilBackground(shell, () => !shell.Find(RingKey("show-play")).IsNull || !shell.Find(RingKey("show-follow")).IsNull,
                "the CTA's ring (resume or follow)");
            shell.RunFrames(60);

            // The toolbar stays ONE row inside the plane the core reserves for it, and the strip's column is the core's width.
            var group = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "tb:right"));
            Assert.True(group.H <= ShowReaderRules.RailExtent + 0.5f, "the toolbar is one row");
            var years = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "rd:years"));
            Assert.Equal(ShowReaderRules.YearStripWidth, years.W, 0.5f);
            Assert.False(shell.Find("rd:sticky-month").IsNull, "the pinned month is always mounted");

            // The ring is a round 36 target at Standard; the stand-in's bell is not drawn.
            var ring = shell.Host.Scene.AbsoluteRect(Node(shell, shell.Find(RingKey("show-play")).IsNull ? RingKey("show-follow") : RingKey("show-play")));
            Assert.True(ring.H >= 36f - 0.5f, $"the CTA's ring is at least a 36 target ({ring})");
            Assert.True(shell.Find("sat:bell").IsNull, "the disabled bell is the stand-in's");
            Assert.False(shell.Find(RingKey("share")).IsNull, "the share ring");
        }, W, H);

    [Fact]
    public void The_show_publishes_its_row_two_and_the_band_draws_it()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_show);
            UntilBackground(shell, () => Chrome() is { Views.Count: > 0 } c && c.Actions.Any(a => a.Id == "follow"), "sh0's row 2 with its follow word");
            var chrome = Chrome()!;
            Assert.Equal("episodes", chrome.CurrentView?.Id);
            Assert.Contains(chrome.Actions, a => a.Id == "follow" && a.Kind == PageActionKind.Word);

            UntilBackground(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "the band's row 2");
            Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + "episodes").IsNull, "the episodes view in the band");
            Assert.False(shell.Find(ZuneBandRules.ActionKeyPrefix + "follow").IsNull, "the follow action in the band");
        }, W, H);

    // ── the episode ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_episode_draws_zunes_reader_and_publishes_its_four_words()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_episode);
            UntilBackground(shell, () => !shell.Find(ZuneEpisodeRules.ReaderKey).IsNull, "Zune's episode reader on sh0e3");
            Assert.Equal("zune.episode.reader", ThemeHost.Registry.TemplateIdOf(ViewKeys.EpisodeReader.Id));
            Assert.False(shell.Find(ZuneDetailRules.FrameKey).IsNull, "the page stands in Zune's detail frame");

            // The four sections stack under their anchors, in order; the reader draws no pivot band of its own.
            UntilBackground(shell, () => shell.FindPrefix("episode-section:").Count == 4, "the four stacked sections");
            var sections = shell.FindPrefix("episode-section:").OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
            float previous = float.NegativeInfinity;
            foreach (var section in sections)
            {
                float y = shell.Host.Scene.AbsoluteRect(section.Node).Y;
                Assert.True(y >= previous - 0.5f, "the sections keep their order");
                previous = y;
            }

            // Row 2: about (current) · chapters · transcript · comments, drawn by the band.
            UntilBackground(shell, () => Chrome() is { Views.Count: 4 } c && c.Views[0].Id == "about", "the episode's four words in row 2");   // the band shows the page on screen: Home has four words too
            var chrome = Chrome()!;
            Assert.Equal(new[] { "about", "chapters", "transcript", "comments" }, chrome.Views.Select(v => v.Id).ToArray());
            Assert.Equal("about", chrome.CurrentView?.Id);
            UntilBackground(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "the band's row 2");
            foreach (string id in new[] { "about", "chapters", "transcript", "comments" })
                Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + id).IsNull, id + " in the band");

            // The play ring stands in the identity column.
            UntilBackground(shell, () => !shell.Find(RingKey("episode-play")).IsNull, "the episode's play ring");
        }, W, H);

    [Fact]
    public void A_compact_episode_keeps_a_40_play_ring()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_episode);
            UntilBackground(shell, () => !shell.Find(RingKey("episode-play")).IsNull, "the episode's play ring at Compact");
            shell.RunFrames(60);
            var ring = shell.Host.Scene.AbsoluteRect(Node(shell, RingKey("episode-play")));
            Assert.True(ring.H >= 40f - 0.5f, $"the Compact play ring is a 40 target ({ring})");
        }, 400f, 860f);

    // ── the concerts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_event_page_draws_zunes_column_and_publishes_details()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_concert);
            UntilBackground(shell, () => !shell.Find(ConcertColumnKey).IsNull && !shell.Find(ConcertDateKey).IsNull, "Zune's event page on cf0");
            Assert.Equal("zune.concert.detail", ThemeHost.Registry.TemplateIdOf(ViewKeys.ConcertDetail.Id));
            Assert.True(shell.Find("concert-event:column").IsNull, "Classic's event column is not drawn");

            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var column = scene.AbsoluteRect(Node(shell, ConcertColumnKey));
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            Assert.True(column.X >= content.X - 0.5f && column.X + column.W <= content.X + content.W + 0.5f, $"the column fits the page ({column} in {content})");

            UntilBackground(shell, () => Chrome() is { Views.Count: > 0 }, "cf0's row 2");
            Assert.Equal("details", Chrome()!.CurrentView?.Id);
            UntilBackground(shell, () => !shell.Find(ZuneBandRules.ViewKeyPrefix + "details").IsNull, "the details view in the band");
        }, W, H);

    [Fact]
    public void The_hub_draws_its_head_over_the_pinned_filter_row()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(Concert.HubRoute);
            UntilBackground(shell, () => !shell.Find(HubPageKey).IsNull && !shell.Find(FilterRowKey).IsNull, "Zune's concert hub");
            Assert.Equal("zune.concerts.hub", ThemeHost.Registry.TemplateIdOf(ViewKeys.ConcertsHub.Id));
            Assert.False(shell.Find(HubHeadKey).IsNull, "the hub's head");

            shell.RunFrames(60);
            var head = shell.Host.Scene.AbsoluteRect(Node(shell, HubHeadKey));
            var bar = shell.Host.Scene.AbsoluteRect(Node(shell, FilterRowKey));
            Assert.True(bar.Y >= head.Y + head.H - 0.5f, "the filter row stands under the head");
        }, W, H);

    [Fact]
    public void A_compact_concert_page_fits_its_page()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_concert);
            UntilBackground(shell, () => !shell.Find(ConcertColumnKey).IsNull, "Zune's event page at Compact");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var column = scene.AbsoluteRect(Node(shell, ConcertColumnKey));
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            Assert.True(column.X >= content.X - 0.5f && column.X + column.W <= content.X + content.W + 0.5f, $"no horizontal overflow ({column} in {content})");
        }, 400f, 860f);

    [Fact]
    public void A_4k_concert_page_stays_inside_the_page_cap()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_concert);
            UntilBackground(shell, () => !shell.Find(ConcertColumnKey).IsNull, "Zune's event page at 3840 x 2160");
            shell.RunFrames(60);
            var column = shell.Host.Scene.AbsoluteRect(Node(shell, ConcertColumnKey));
            Assert.True(column.W <= ZuneDetailRules.PageCap + 0.5f, $"the column is capped at {ZuneDetailRules.PageCap} ({column})");
        }, 3840f, 2160f);
}
