// ── Wavee.Tests/EntitiesFakeLibraryTests.cs — owner O's seed contract (Entities.Fake.Library.cs) ──────────────────────
//
// The WP-5.O contract §5 as facts: determinism (two seeds, one set of columns), the P4 chart vocabulary, the daylist and
// tuning arms, X0's recommendations under an empty Complete membership, the notices (X2 / X3 / X4), X5's mixed rows, L0's
// fourteen local rows, T0 / T1 on every pool track, and C0's evidence split. Everything drives the real
// `Entities.SeedFake` and reads HANDLES afterwards — no source text, no private seed arrays (the fixture constants are
// restated here, as the album seed's tests do, because the seed class is internal).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EntitiesFakeLibraryTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated
    const long Hour = 3600;

    static readonly string[] Descriptors = ["Pop", "Dance", "Indie", "Hip Hop", "Rock", "Electronic"];
    static readonly string[] CuratedTitles = ["Pop", "Dance", "Jazz", "Indie", "Hip Hop", "Chill", "Rock", "Electronic"];

    static void Seed(long now0 = Now0)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(now0);
    }

    static string N(int i) => i.ToString(CultureInfo.InvariantCulture);
    static Playlist Core(int i) => Entities.Playlist(EntityUri.Parse("spotify:playlist:pl" + N(i)));
    static Playlist Extra(int k) => Entities.Playlist(EntityUri.Parse("spotify:playlist:plx" + N(k)));
    static Playlist LocalFiles => Entities.Playlist(EntityUri.Parse(Playlist.LocalFilesUri));
    static Track Pool(int i) => Entities.Track(EntityUri.Parse("spotify:track:tr" + N(i)));
    static string S(StringId id) => Entities.Strings.Resolve(id);

    // ── determinism (ch 31 §7.2) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_seeds_at_the_same_epoch_write_the_same_library_surfaces()
    {
        Seed();
        string first = Fingerprint();
        Seed();
        Assert.Equal(first, Fingerprint());
    }

    static string Fingerprint()
    {
        var sb = new StringBuilder();
        void List(Playlist p)
        {
            sb.Append(p.Uri.Text).Append('|').Append(S(p.TitleId)).Append('|').Append((int)p.Notice).Append('|')
              .Append((int)p.Format).Append('|').Append((int)p.Caps).Append('|').Append(p.Saves).Append('|')
              .Append(p.DaylistExpiresAt).Append('|').Append(p.ChartNewEntries).Append('|').Append(p.ChartUpdatedAt).Append('|')
              .Append((int)p.MembershipState).Append('|').Append(p.EpisodeCount).Append('|').Append(p.DurationMs).Append('\n');
            var slots = p.TrackSlots;
            var edges = p.TrackEdges;
            for (int i = 0; i < slots.Length; i++)
            {
                sb.Append(new Track(slots[i]).Uri.Text);
                if (i < edges.Length)
                {
                    var e = edges[i];
                    sb.Append(':').Append(S(e.ItemId)).Append(':').Append(e.AddedAt).Append(':').Append(e.AddedBy)
                      .Append(':').Append(e.ChartStatus).Append(':').Append(e.ChartPos).Append(':').Append(e.ChartPrev);
                }
                sb.Append(';');
            }
            foreach (var o in p.TuningOptions) sb.Append(S(o.Identifier)).Append('/').Append(S(o.DisplayName)).Append('/').Append(o.Kind).Append(';');
            sb.Append(S(p.TuningSelectedId)).Append('|');
            foreach (int r in p.RecommendationSlots) sb.Append(new Track(r).Uri.Text).Append(';');
            sb.Append('\n');
        }
        for (int i = 0; i < 7; i++) List(Core(i));
        for (int k = 0; k < 6; k++) List(Extra(k));
        List(LocalFiles);
        for (int i = 0; i < 166; i++)
        {
            var t = Pool(i);
            foreach (var tag in t.Tags) sb.Append(S(tag)).Append(',');
            foreach (int a in t.ArtistSlots) sb.Append(new Artist(a).Uri.Text).Append(',');
            sb.Append(';');
        }
        var me = User.Me;
        foreach (var title in me.ContentFilterTitles) sb.Append(S(title)).Append(',');
        return sb.ToString();
    }

    // ── P0-P6 ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P0_is_the_owned_public_list_with_dates_by_one_adder()
    {
        Seed();
        var p = Core(0);
        Assert.Equal(18_700_000, p.Saves);
        Assert.True((p.Caps & PlaylistCaps.IsOwner) != 0);
        Assert.Equal(EdgeState.Complete, p.MembershipState);
        Assert.True(p.HasDateAddedColumn);
        Assert.False(p.HasAddedByColumn);                      // one adder: the column is noise
        var edges = p.TrackEdges;
        Assert.Equal(p.TrackSlots.Length, edges.Length);
        for (int k = 0; k < edges.Length; k++)
        {
            Assert.Equal((int)(Now0 - k * 7 * Hour), edges[k].AddedAt);
            Assert.Equal(16, S(edges[k].ItemId).Length);
        }
    }

    [Fact]
    public void P1_is_collaborative_with_four_adders()
    {
        Seed();
        var p = Core(1);
        Assert.True((p.Caps & PlaylistCaps.IsCollaborative) != 0);
        Assert.True(p.HasAddedByColumn);
        Span<int> collaborators = stackalloc int[8];
        Assert.True(p.CollaboratorSlots(collaborators) >= 3);
    }

    [Fact]
    public void P3_is_a_daylist_whose_window_is_a_function_of_the_epoch()
    {
        Seed();
        var p = Core(3);
        Assert.Equal(PlaylistFormat.Daylist, p.Format);
        Assert.Equal((int)(Now0 + 4 * Hour + 37 * 60), p.DaylistExpiresAt);
        Assert.Equal((int)(Now0 - 3 * Hour - 23 * 60), p.DaylistCreatedAt);
    }

    [Fact]
    public void P4_carries_the_chart_vocabulary()
    {
        Seed();
        var p = Core(4);
        Assert.Equal(PlaylistFormat.Chart, p.Format);
        Assert.Equal(7, p.ChartNewEntries);
        Assert.Equal((int)(Now0 - 7200), p.ChartUpdatedAt);
        Assert.Equal("weekly", S(p.ChartRankTypeId));

        var edges = p.TrackEdges;
        Assert.True(edges.Length >= 13);
        for (int k = 0; k < edges.Length; k++)
        {
            var e = edges[k];
            int pos = k + 1;
            Assert.Equal(pos, e.ChartPos);
            var (status, prev) = k switch
            {
                0 => (4, 0),
                <= 3 => (2, pos + 2),
                <= 7 => (1, pos),
                <= 11 => (3, pos - 2),
                _ => (1, pos),
            };
            Assert.Equal(status, e.ChartStatus);
            Assert.Equal(prev, e.ChartPrev);
        }
    }

    [Fact]
    public void P5_is_untuned_and_P6_is_tuned_to_its_second_choice()
    {
        Seed();
        var p5 = Core(5);
        var p6 = Core(6);
        Assert.True(p5.Knows(PlaylistFields.Tuning));
        Assert.Equal(4, p5.TuningOptions.Length);
        Assert.Equal((byte)TuningOptionKind.Reset, p5.TuningOptions[3].Kind);
        Assert.True(p5.TuningSelectedId.IsEmpty);
        Assert.True(p5.TuningCurrent);

        Assert.Equal(4, p6.TuningOptions.Length);
        Assert.Equal(p6.TuningOptions[1].Identifier, p6.TuningSelectedId);
        Assert.Equal("session_control_display$mix$soft_pop:nl_genre", S(p6.TuningSelectedId));
    }

    // ── X0-X5 ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void X0_is_empty_and_complete_with_twenty_four_recommendations()
    {
        Seed();
        var p = Extra(0);
        Assert.Equal(EdgeState.Complete, p.MembershipState);
        Assert.Equal(0, p.TrackSlots.Length);
        Assert.Equal(PlaylistRowsState.Empty, p.RowsState(0));
        Assert.True(p.Editable);

        var recs = p.RecommendationSlots;
        Assert.Equal(24, recs.Length);
        var distinct = new HashSet<int>();
        foreach (int r in recs)
        {
            Assert.True(new Track(r).IsValid);
            Assert.True(distinct.Add(r));
        }
    }

    [Fact]
    public void X1_holds_twelve_rows_from_twelve_distinct_albums()
    {
        Seed();
        var p = Extra(1);
        Assert.Equal(12, p.TrackSlots.Length);
        var albums = new HashSet<int>();
        foreach (int slot in p.TrackSlots) albums.Add(new Track(slot).Album.Slot);
        Assert.Equal(12, albums.Count);
    }

    [Fact]
    public void X2_X3_X4_carry_the_three_playlist_notices()
    {
        Seed();
        Assert.Equal(DetailNotice.Deleted, Extra(2).Notice);
        Assert.True(Extra(2).DeletedByOwner);
        Assert.Equal(10, Extra(2).TrackSlots.Length);

        Assert.Equal(DetailNotice.AccessRevoked, Extra(3).Notice);
        Assert.False(Extra(3).CanView);
        Assert.Equal(8, Extra(3).TrackSlots.Length);

        var x4 = Extra(4);
        Assert.Equal(DetailNotice.CreateFailed, x4.Notice);
        Assert.True(x4.CreateFailed);
        Assert.Equal(EdgeState.Complete, x4.MembershipState);
        Assert.Equal(0, x4.TrackSlots.Length);
    }

    [Fact]
    public void X5_mixes_three_episodes_into_twenty_rows()
    {
        Seed();
        var p = Extra(5);
        var slots = p.TrackSlots;
        Assert.Equal(20, slots.Length);
        // X5's three "episodes" (plan §3.1, ledger row 12) are real Episode-kind membership edges resolving into
        // Current.Episodes now, not Track rows flagged TrackFlags.Podcast — the superseded shape.
        var payload = Entities.Current.Edges.PlaylistTracks.Payload(p.Slot);
        for (int k = 0; k < slots.Length; k++)
        {
            bool shouldBeEpisode = k is 4 or 11 or 17;
            Assert.Equal(shouldBeEpisode ? PlaylistItemKind.Episode : PlaylistItemKind.Track, payload[k].Kind);
            if (shouldBeEpisode) Assert.True(new Episode(slots[k]).IsValid);
            else Assert.True(new Track(slots[k]).IsValid);
        }
        Assert.Equal(3, p.EpisodeCount);
        Assert.True(p.IsMixed);
    }

    [Fact]
    public void L0_is_fourteen_local_rows_owned_by_this_device()
    {
        Seed();
        var p = LocalFiles;
        Assert.Equal(EdgeState.Complete, p.MembershipState);
        var slots = p.TrackSlots;
        Assert.Equal(14, slots.Length);
        foreach (int slot in slots)
        {
            var t = new Track(slot);
            Assert.True(t.IsLocal);
            Assert.True(t.Knows(TrackFields.Availability));
            Assert.True(t.DurationMs > 0);
        }
        Assert.Equal("On this device", S(p.Owner.NameId));
    }

    // ── T0 / T1 / C0 ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_pool_track_answers_tags_and_credits_and_Chill_is_a_second_descriptor()
    {
        Seed();
        int chill = 0;
        for (int i = 0; i < 166; i++)
        {
            var t = Pool(i);
            Assert.True(t.Knows(TrackFields.Tags));
            Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackTags.State(t.Slot));
            Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackArtists.State(t.Slot));
            Assert.True(t.ArtistSlots.Length > 0);

            var tags = t.Tags;
            Assert.True(tags.Length > 0);
            Assert.Contains(S(tags[0]), Descriptors);                // the primary is never "Chill" and never "Jazz"
            for (int k = 0; k < tags.Length; k++)
            {
                Assert.NotEqual("Jazz", S(tags[k]));
                if (k > 0 && S(tags[k]) == "Chill") chill++;
            }
        }
        Assert.True(chill >= ContentFilterTags.MinTrackCount);
    }

    [Fact]
    public void C0_is_eight_curated_chips_seven_evidenced_by_the_liked_rows_and_Jazz_last()
    {
        Seed();
        var me = User.Me;
        Assert.True(me.Knows(UserFields.ContentFilters));

        var titles = me.ContentFilterTitles;
        var tokens = me.ContentFilterTokens;
        Assert.Equal(8, titles.Length);
        Assert.Equal(8, tokens.Length);
        var curated = new ContentFilterChip[titles.Length];
        for (int i = 0; i < curated.Length; i++)
        {
            Assert.Equal(CuratedTitles[i], S(titles[i]));
            curated[i] = new ContentFilterChip(S(titles[i]), S(tokens[i]));
        }

        var liked = MemoryMarshal.Cast<int, Track>(me.LikedTrackSlots);
        Assert.Equal(166, liked.Length);
        var set = LikedFactsRules.ChipSet(true, curated, liked);
        Assert.Equal(8, set.Count);
        Assert.Equal(7, set.EvidencedCount);
        Assert.Equal(new[] { "Pop", "Dance", "Indie", "Hip Hop", "Chill", "Rock", "Electronic", "Jazz" }, set.Titles.ToArray());
        Assert.False(set.IsEvidenced(7));
    }
}
