// ── Wavee.Tests/EpisodeTests.cs — the episode columns, the show back-link, and progress as USER state ─────────────
//
// Wave 1's gate for `Entities/Episode.cs`. The fact that matters most is the last one: an episode's resume position is
// per-user state with its OWN authority column, so a catalogue answer can never rewind the position this device just
// played to (ch 09 DATA GAPS, first row).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EpisodeTests
{
    [Fact]
    public void Row_excludes_progress_because_unknown_progress_renders_as_unplayed()
    {
        // ch 09 §7: "an episode whose progress is unknown must render as UNPLAYED, never as a half-drawn bar" — so the
        // list's shimmer gate cannot wait for it.
        Assert.Equal(EpisodeFields.Identity, EpisodeFields.Row);
        Assert.False(EpisodeFields.Row.HasFlag(EpisodeFields.Progress));
        Assert.False(EpisodeFields.Row.HasFlag(EpisodeFields.About));
    }

    [Fact]
    public void Identity_carries_the_date_the_duration_and_the_show_back_link()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Episodes.Add();
        row.Id = s.Text("spotify:episode:e1");
        row.Title = s.Text("The one about slabs");
        row.Image = s.Text("spotify:image:ab67656300005f1f11111111111111111111111f");
        row.DurationMs = 3_600_000;
        row.PublishedAt = 1_699_000_000;
        row.ShowUri = s.Text("spotify:show:s1");
        row.Known = (uint)EpisodeFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var episode = Entities.Episode(EntityUri.Parse("spotify:episode:e1"));
        Assert.True(episode.Knows(EpisodeFields.Row));
        Assert.Equal("The one about slabs", episode.Title);
        Assert.Equal(3_600_000, episode.DurationMs);
        Assert.Equal(1_699_000_000, episode.PublishedAt);

        // ch 09 DATA GAPS: the back-link is what makes the row's subtitle a LINK, and it is where `EpisodeAsTrack` puts
        // the show — the album slot of a playlist row.
        Assert.True(episode.Show.IsValid);
        Assert.Equal("spotify:show:s1", episode.Show.Uri.Text);
    }

    [Fact]
    public void The_description_is_its_own_group_so_a_late_row_never_holds_the_page()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Episodes.Add();
        row.Id = s.Text("spotify:episode:e2");
        row.Title = s.Text("Late prose");
        row.Known = (uint)EpisodeFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var episode = Entities.Episode(EntityUri.Parse("spotify:episode:e2"));
        Assert.True(episode.Knows(EpisodeFields.Row));
        Assert.False(episode.Knows(EpisodeFields.About));

        var late = Staging.Rent();
        ref var about = ref late.Episodes.Add();
        about.Id = late.Text("spotify:episode:e2");
        about.Description = late.Text("Two lines, clamped.");
        about.Known = (uint)EpisodeFields.About;
        about.Authority = Authority.Full;
        TestScope.CommitAndPublish(late);

        Assert.True(episode.Knows(EpisodeFields.About));
        Assert.Equal("Two lines, clamped.", Entities.Strings.Resolve(episode.DescriptionId));
    }

    [Fact]
    public void A_catalogue_answer_cannot_rewind_a_local_resume_position()
    {
        // The player writes progress at Local authority; the catalogue's playback-state trait writes it at Thin. D16
        // over a SEPARATE authority column is what keeps a stale server position from undoing the last thirty minutes.
        TestScope.Fresh();

        var local = Staging.Rent();
        ref var mine = ref local.Episodes.Add();
        mine.Id = local.Text("spotify:episode:resume");
        mine.ProgressMs = 1_800_000;
        mine.Known = (uint)EpisodeFields.Progress;
        mine.Authority = Authority.Local;
        TestScope.CommitAndPublish(local);

        var stale = Staging.Rent();
        ref var server = ref stale.Episodes.Add();
        server.Id = stale.Text("spotify:episode:resume");
        server.ProgressMs = 12_000;
        server.Known = (uint)EpisodeFields.Progress;
        server.Authority = Authority.Thin;
        TestScope.CommitAndPublish(stale);

        var episode = Entities.Episode(EntityUri.Parse("spotify:episode:resume"));
        Assert.Equal(1_800_000, episode.ProgressMs);

        // And the same Thin answer is still free to fill a group nobody filled.
        var about = Staging.Rent();
        ref var text = ref about.Episodes.Add();
        text.Id = about.Text("spotify:episode:resume");
        text.Description = about.Text("Blurb");
        text.Known = (uint)EpisodeFields.About;
        text.Authority = Authority.Thin;
        TestScope.CommitAndPublish(about);

        Assert.True(episode.Knows(EpisodeFields.About));
    }

    // ── the packed identity, and the text the row owns (defect 1, doc §4.4) ──────────────────────────

    /// <summary>An episode's uri is a view over the packed id: kind and provider are field loads, and a catalog episode
    /// interns nothing at all for its identity (doc §1.2 — the 158 B/row this change removes).</summary>
    [Fact]
    public void The_uri_is_a_view_over_the_id_and_a_catalog_episode_interns_nothing_for_it()
    {
        TestScope.Fresh();
        var t = Entities.Current.Episodes;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:episode:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        Assert.Equal(before, Entities.Strings.MapCount);

        var episode = new Episode(slot);
        Assert.Equal(EntityKind.Episode, episode.Id.Kind);
        Assert.Equal(EntityProvider.Spotify, episode.Uri.Provider);
        Assert.True(episode.Id.IsPlayable);
        Assert.Equal("spotify:episode:4uLU6hMCjMI75M1A2tKUQC", episode.Uri.Text);
    }

    /// <summary>DEFECT 1 for this kind: three text columns, and the description is the long one. The count is the
    /// check — miss a <c>ClearText</c> and a trim frees the row while its blurb stays for the life of the
    /// process.</summary>
    [Fact]
    public void A_freed_episode_row_hands_back_all_three_of_its_strings_and_its_uri()
    {
        TestScope.Fresh();
        var t = Entities.Current.Episodes;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:episode:teardown-20260912".AsSpan());          // a TEXT-form row: the uri is owned too
        t.SetText(ref t.Title, slot, Entities.Strings.Intern("episode teardown title 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("episode teardown cover 20260912"));
        t.SetText(ref t.Description, slot, Entities.Strings.Intern("episode teardown blurb 20260912"));
        Assert.Equal(before + 4, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.True(t.Description[slot].IsEmpty);
    }
}
