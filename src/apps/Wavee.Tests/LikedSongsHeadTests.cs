// ── Wavee.Tests/LikedSongsHeadTests.cs — Liked Songs without a rail (tuned Classic D16, D8; board C-Liked) ──────────────────────────────
//
// The pure half of CT6a: which pages have no rail and a quiet list (`Detail.Config`), the date groups' rule (`Track.DateGroupRules`: the
// last seven days, then calendar months, a month of another year with its year, the runs of the sorted view), the quiet set's trailing
// heart on the relief ladder and in the width tracks, and the head's arithmetic (the 156 head, the list's inset). The mounted geometry
// is `Wavee.ShellTests/DetailFrameMountedTests`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Xunit;
using Config = Wavee.Detail.Config;

namespace Wavee.Tests;

public sealed class LikedSongsHeadTests
{
    // ── the config ──

    [Fact]
    public void Liked_songs_alone_has_no_rail_a_quiet_list_and_date_groups()
    {
        Assert.False(Config.Liked.Rail);
        Assert.True(Config.Liked.QuietRows);
        Assert.True(Config.Liked.DateGroups);
        foreach (var other in new[] { Config.Album, Config.Playlist, Config.Compilation, Config.Show, Config.Episode })
        {
            Assert.True(other.Rail);
            Assert.False(other.QuietRows);
            Assert.False(other.DateGroups);
        }
    }

    // ── the date groups ──

    const long Day = 24L * 60 * 60;
    static readonly long Now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void The_last_seven_days_are_this_week_and_older_songs_fall_in_their_month()
    {
        Assert.Equal(Track.DateGroupRules.ThisWeek, Track.DateGroupRules.KeyOf(Now, Now, TimeSpan.Zero));
        Assert.Equal(Track.DateGroupRules.ThisWeek, Track.DateGroupRules.KeyOf(Now - 6 * Day, Now, TimeSpan.Zero));
        int october = Track.DateGroupRules.KeyOf(Now - 7 * Day, Now, TimeSpan.Zero);
        Assert.Equal(2026 * 12 + 9, october);
        Assert.Equal(2026 * 12 + 8, Track.DateGroupRules.KeyOf(Now - 30 * Day, Now, TimeSpan.Zero));
        Assert.Equal(Track.DateGroupRules.Undated, Track.DateGroupRules.KeyOf(0, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_month_is_local_time()
    {
        // 30 Sep 23:30 UTC is 1 Oct in UTC+2.
        long late = new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(2026 * 12 + 8, Track.DateGroupRules.KeyOf(late, Now, TimeSpan.Zero));
        Assert.Equal(2026 * 12 + 9, Track.DateGroupRules.KeyOf(late, Now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void The_label_names_the_month_and_the_year_only_when_it_is_another_year()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("This week", Track.DateGroupRules.LabelOf(Track.DateGroupRules.ThisWeek, 2026, c, "This week", "Earlier"));
        Assert.Equal("Earlier", Track.DateGroupRules.LabelOf(Track.DateGroupRules.Undated, 2026, c, "This week", "Earlier"));
        Assert.Equal("September", Track.DateGroupRules.LabelOf(2026 * 12 + 8, 2026, c, "This week", "Earlier"));
        Assert.Equal("September 2024", Track.DateGroupRules.LabelOf(2024 * 12 + 8, 2026, c, "This week", "Earlier"));
    }

    [Fact]
    public void A_group_head_carries_its_run_and_every_other_row_none()
    {
        int[] keys = [5, 5, 5, 4, 3, 3];
        var heads = new int[keys.Length];
        Track.DateGroupRules.RunsOf(keys, heads);
        Assert.Equal(new[] { 3, 0, 0, 1, 2, 0 }, heads);
    }

    // ── the quiet set's trailing heart ──

    static Track.ColumnSet QuietSet => new(Album: true, By: false, Date: true, Plays: false, Heart: false, Thumb: true,
                                           HeartTrailing: true, Quiet: true);

    [Fact]
    public void The_quiet_heart_is_the_trailing_32_lane_before_the_duration()
    {
        var set = QuietSet;
        var tracks = Track.TracksFor(in set, 32f);
        Assert.Equal(8, tracks.Length);   // # · art · Title · Album · Date · ♥ · Duration · Actions (no leading heart)
        // … Date · ♥ · Duration · Actions
        Assert.Equal(TrackSize.Px(Track.Lane.HeartTrailing), tracks[^3]);
        Assert.Equal(TrackSize.Px(Track.Lane.Duration), tracks[^2]);
    }

    [Fact]
    public void The_trailing_heart_yields_with_the_heart_lane_and_never_turns_into_a_leading_one()
    {
        var set = QuietSet;
        Assert.True(Track.TableRules.LanesOf(in set).Heart, "the trailing heart is the ladder's heart lane");
        for (int step = 0; step <= Track.TableRules.MaxRelief; step++)
        {
            var relieved = Track.TableRules.ApplyRelief(in set, step);
            Assert.False(relieved.Heart);
            Assert.Equal(Track.TableRules.Relieve(Track.TableRules.LanesOf(in set), step).Heart, relieved.HeartTrailing);
        }
    }

    // ── the head ──

    [Fact]
    public void The_head_is_156_and_the_list_plates_start_8_before_its_words()
    {
        // head (24 + 52 + 4 + 16 + 4) + the list chrome's air (8) + the 44 bar + the 4 under it = the board's 156
        float head = ClassicTiers.HeadTop + ClassicDetailLayout.HeadTitleLine + ClassicTiers.HeadMetaGap
                     + ClassicDetailLayout.HeadMetaLine + ClassicDetailLayout.HeadPadBottom;
        Assert.Equal(156f, head + Spacing.S + Detail.VerticalLayout.ToolbarRowHeight + Spacing.XS);
        float gutter = ClassicTiers.Page(Wavee.Theming.Contracts.WidthTier.Standard).Gutter;   // the board's 36 at Standard (TC1's ladder)
        Assert.Equal(36f, gutter);
        Assert.Equal(28f, ClassicDetailLayout.HeadListInset(gutter));
        Assert.Equal(8f, ClassicDetailLayout.HeadListInset(16f));                            // Compact: the plates still start 8 before the words
        // D8: the inline chips keep 120 and end 8 before the tools
        Assert.Equal(120f, Track.CommandBarLayout.InlineChipsMin);
        Assert.Equal(8f, Track.CommandBarLayout.InlineChipsTrail);
    }
}
