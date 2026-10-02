// ── Wavee.Tests/EpisodeRowDataTests.cs — what an episode's media row decides and carries (Episode.UI.cs §6, #160) ────
//
// The episode is one more media row on the app's shared surface (Home's episode zones, Recents' members), fed by ONE
// adapter, `Episode.RowData`. The decisions it makes — which parts of the meta row exist, when the resume rung is drawn,
// whether there is a menu, that an episode never drags — are `Episode.RowPlan`, a pure value pinned here without a table
// or an element; the adapter facts then read the same decisions off the `CardData` it builds over a resident episode.
// Separators are built from their code point, so the source stays ASCII (the file Episode.UI.cs's own convention).

using System.Globalization;
using FluentGpu.Dsl;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeRowDataTests
{
    const long Min = 60_000L;
    static readonly string Dot = " " + (char)0x00B7 + " ";

    static Episode.RowFacts Facts(long durationMs = 44 * Min, long resumeMs = 0, bool isExplicit = false, bool video = false)
        => new("spotify:episode:e1", "Episode title", "The Show", "https://img/e1", isExplicit, video, 0L, durationMs, resumeMs);

    // ── the resume rung: only for an episode the listener has resumed ───────────────────────────────────────────────

    [Fact]
    public void Plan_has_no_progress_rung_until_the_listener_has_resumed()
    {
        var fresh = Episode.RowPlan.Of(Facts(resumeMs: 0), date: "", resolvable: true, showMeta: true);
        Assert.False(fresh.ShowsProgress);
        Assert.Equal(0f, fresh.Progress);

        var resumed = Episode.RowPlan.Of(Facts(resumeMs: 11 * Min), date: "", resolvable: true, showMeta: true);
        Assert.True(resumed.ShowsProgress);
    }

    [Fact]
    public void Plan_progress_is_the_fraction_played_and_never_leaves_zero_to_one()
    {
        Assert.Equal(0.25f, Episode.RowPlan.Of(Facts(resumeMs: 11 * Min), "", true, true).Progress, 4);
        Assert.Equal(1f, Episode.RowPlan.Of(Facts(resumeMs: 99 * Min), "", true, true).Progress, 4);     // past the end reads the end
        Assert.Equal(0f, Episode.RowPlan.Of(Facts(durationMs: 0, resumeMs: 5 * Min), "", true, true).Progress, 4);   // unknown length: no guess
    }

    // ── the length and the caption ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Length_is_what_is_left_while_resumed_and_the_whole_duration_otherwise()
    {
        Assert.Equal("44 min", Episode.RowPlan.LengthOf(44 * Min, 0));
        Assert.Equal("33 min left", Episode.RowPlan.LengthOf(44 * Min, 11 * Min));
        Assert.Equal("1 h 30 min", Episode.RowPlan.LengthOf(90 * Min, 0));
        Assert.Equal("", Episode.RowPlan.LengthOf(0, 0));
    }

    [Fact]
    public void Caption_is_date_then_length_or_whichever_exists()
    {
        Assert.Equal("Today" + Dot + "44 min", Episode.RowPlan.JoinCaption("Today", "44 min"));
        Assert.Equal("44 min", Episode.RowPlan.JoinCaption("", "44 min"));
        Assert.Equal("Today", Episode.RowPlan.JoinCaption("Today", ""));
        Assert.Equal("", Episode.RowPlan.JoinCaption("", ""));
    }

    [Fact]
    public void Plan_caption_leads_with_the_date_and_swaps_the_length_for_what_is_left_while_resumed()
    {
        Assert.Equal("Today" + Dot + "44 min", Episode.RowPlan.Of(Facts(), "Today", true, true).Caption);
        Assert.Equal("Today" + Dot + "33 min left", Episode.RowPlan.Of(Facts(resumeMs: 11 * Min), "Today", true, true).Caption);
    }

    // ── the meta row: any part, only when the site asks ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_meta_row_exists_when_any_of_its_parts_does()
    {
        Assert.True(Episode.RowPlan.Of(Facts(durationMs: 0, isExplicit: true), "", true, true).HasMetaRow);   // the badge alone
        Assert.True(Episode.RowPlan.Of(Facts(durationMs: 0, video: true), "", true, true).HasMetaRow);        // the video mark alone
        Assert.True(Episode.RowPlan.Of(Facts(durationMs: 0), "Today", true, true).HasMetaRow);                // the date alone
        Assert.True(Episode.RowPlan.Of(Facts(), "", true, true).HasMetaRow);                                  // the length alone
        Assert.False(Episode.RowPlan.Of(Facts(durationMs: 0), "", true, true).HasMetaRow);                    // nothing known: no ghost row
    }

    [Fact]
    public void Plan_marks_show_only_where_the_facts_carry_them()
    {
        var both = Episode.RowPlan.Of(Facts(isExplicit: true, video: true), "", true, true);
        Assert.True(both.ShowsExplicit);
        Assert.True(both.ShowsVideo);
        var neither = Episode.RowPlan.Of(Facts(), "", true, true);
        Assert.False(neither.ShowsExplicit);
        Assert.False(neither.ShowsVideo);
    }

    [Fact]
    public void Plan_compact_row_carries_no_meta_row_marks_caption_or_rung_whatever_the_facts()
    {
        var plan = Episode.RowPlan.Of(Facts(resumeMs: 11 * Min, isExplicit: true, video: true), "Today", resolvable: true, showMeta: false);
        Assert.False(plan.HasMetaRow);
        Assert.False(plan.ShowsExplicit);
        Assert.False(plan.ShowsVideo);
        Assert.False(plan.ShowsProgress);
        Assert.Equal("", plan.Caption);
    }

    // ── the menu: only for a handle that resolves ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_has_a_menu_only_for_a_resolvable_episode_whatever_else_the_site_asks()
    {
        Assert.True(Episode.RowPlan.Of(Facts(), "", resolvable: true, showMeta: true).HasMenu);
        Assert.True(Episode.RowPlan.Of(Facts(), "", resolvable: true, showMeta: false).HasMenu);
        Assert.False(Episode.RowPlan.Of(Facts(), "", resolvable: false, showMeta: true).HasMenu);
        Assert.False(Episode.RowPlan.Of(Facts(), "", resolvable: false, showMeta: false).HasMenu);
    }

    // ── the options: construct with names ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_default_to_the_full_row_with_go_to_show()
    {
        var o = new Episode.RowOptions();
        Assert.True(o.ShowMeta);
        Assert.True(o.ShowGoToShow);
        Assert.Null(o.OnClick);
        Assert.Null(o.OnPlay);
        Assert.Null(o.Trailing);
        Assert.Null(o.Clock);
    }
}

/// <summary>The same decisions read off the <see cref="Controls.CardData"/> the adapter builds over a RESIDENT episode.</summary>
[Collection(EntitiesCollection.Name)]
public class EpisodeRowDataAdapterTests
{
    const long Min = 60_000L;
    // A fixed clock: the adapter never reads the system's when the site states one.
    static readonly Episode.RowClock Clock = new(1_700_000_000_000L, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

    static Episode Resident()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Episodes.Add();
        row.Id = s.Text("spotify:episode:row1");
        row.Title = s.Text("The one about slabs");
        row.Image = s.Text("spotify:image:ab67656300005f1f11111111111111111111111f");
        row.DurationMs = (int)(60 * Min);
        row.PublishedAt = 1_699_000_000;
        row.Known = (uint)EpisodeFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        return Entities.Episode(EntityUri.Parse("spotify:episode:row1"));
    }

    static Episode.RowFacts Facts(string? show = "The Show", long resumeMs = 0, bool isExplicit = false)
        => new("spotify:episode:row1", "The one about slabs", show, "https://img/row1", isExplicit, false, 0L, 60 * Min, resumeMs);

    [Fact]
    public void A_resolved_episode_is_an_episode_drag_source_and_an_unresolved_handle_is_not()
    {
        var episode = Resident();
        var facts = Facts();
        var options = new Episode.RowOptions(Clock: Clock);
        Assert.NotNull(Episode.RowData(episode, in facts, in options).Drag);
        var compact = new Episode.RowOptions(ShowMeta: false);
        Assert.NotNull(Episode.RowData(episode, in facts, in compact).Drag);
        Assert.Null(Episode.RowData(default, in facts, in compact).Drag);
    }

    [Fact]
    public void A_resolved_episode_has_the_menu_and_an_unresolved_handle_has_none()
    {
        var episode = Resident();
        var facts = Facts();
        var options = new Episode.RowOptions(Clock: Clock);
        Assert.NotNull(Episode.RowData(episode, in facts, in options).Menu);
        Assert.Null(Episode.RowData(default, in facts, in options).Menu);
    }

    [Fact]
    public void The_resume_rung_sits_Below_only_while_resumed_and_only_on_the_full_row()
    {
        var episode = Resident();
        var resumed = Facts(resumeMs: 20 * Min);
        var fresh = Facts();
        var full = new Episode.RowOptions(Clock: Clock);
        var compact = new Episode.RowOptions(ShowMeta: false);
        Assert.NotNull(Episode.RowData(episode, in resumed, in full).Below);
        Assert.Null(Episode.RowData(episode, in fresh, in full).Below);
        Assert.Null(Episode.RowData(episode, in resumed, in compact).Below);
    }

    [Fact]
    public void The_meta_row_is_the_full_rows_and_the_compact_row_has_none()
    {
        var episode = Resident();
        var facts = Facts(isExplicit: true);
        var full = new Episode.RowOptions(Clock: Clock);
        var compact = new Episode.RowOptions(ShowMeta: false);
        Assert.NotNull(Episode.RowData(episode, in facts, in full).MetaRow);
        Assert.Null(Episode.RowData(episode, in facts, in compact).MetaRow);
    }

    [Fact]
    public void The_row_carries_the_facts_identity_both_verbs_and_the_menu_button()
    {
        var episode = Resident();
        var facts = Facts();
        var options = new Episode.RowOptions(Clock: Clock);
        var data = Episode.RowData(episode, in facts, in options);
        Assert.Equal("spotify:episode:row1", data.Uri);
        Assert.Equal("The one about slabs", data.Title);
        Assert.Equal("https://img/row1", data.CoverUrl);
        Assert.NotNull(data.OnClick);
        Assert.NotNull(data.OnPlay);
        Assert.False(data.Circular);
        Assert.True(data.ShowMenu);                 // the "..." button is not opted out; the shape places it, the Menu feeds it
    }

    [Fact]
    public void The_show_name_is_the_subtitle_and_no_name_means_no_subtitle()
    {
        var episode = Resident();
        var named = Facts("The Show");
        var anonymous = Facts(show: null);
        var blank = Facts(show: "");
        var options = new Episode.RowOptions(Clock: Clock);
        Assert.NotNull(Episode.RowData(episode, in named, in options).Subtitle);
        Assert.Null(Episode.RowData(episode, in anonymous, in options).Subtitle);
        Assert.Null(Episode.RowData(episode, in blank, in options).Subtitle);
    }

    [Fact]
    public void A_sites_own_verbs_and_trailing_are_the_rows()
    {
        var episode = Resident();
        var facts = Facts();
        int opened = 0, played = 0;
        var trailing = new TextEl("52:00");
        var options = new Episode.RowOptions(OnClick: () => opened++, OnPlay: () => played++, ShowMeta: false, Trailing: trailing);
        var data = Episode.RowData(episode, in facts, in options);
        data.OnClick!();
        data.OnPlay!();
        Assert.Equal(1, opened);
        Assert.Equal(1, played);
        Assert.Same(trailing, data.Trailing);
    }

    [Fact]
    public void RowFactsOf_reads_the_residents_columns_and_a_fresh_episode_has_no_resume_point()
    {
        var episode = Resident();
        var facts = Episode.RowFactsOf(episode);
        Assert.Equal("spotify:episode:row1", facts.Uri);
        Assert.Equal("The one about slabs", facts.Title);
        Assert.Null(facts.Show);                      // no show row is resident
        Assert.Equal(60 * Min, facts.DurationMs);
        Assert.Equal(1_699_000_000_000L, facts.ReleasedAtMs);
        Assert.Equal(0L, facts.ResumeMs);
        Assert.False(facts.Explicit);
        Assert.False(facts.Video);
    }

    [Fact]
    public void RowFactsOf_an_unresolved_handle_is_empty_and_never_throws()
    {
        Resident();
        var facts = Episode.RowFactsOf(default);
        Assert.Equal("", facts.Title);
        Assert.Equal("", facts.Uri);
        Assert.Equal(0L, facts.DurationMs);
    }
}
