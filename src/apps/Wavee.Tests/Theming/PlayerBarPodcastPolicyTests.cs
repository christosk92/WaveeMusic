// ── Wavee.Tests/Theming/PlayerBarPodcastPolicyTests.cs — what the podcast bar carries is the level's (T6a) ───────────────
//
// `Shell.PodcastBarLayout` compared the level ordinals (previous / next from Wide, the time labels from Comfortable, the heart
// from Medium, a 40 / 120 DIP identity block at the two narrowest levels). The same map is now data on the level
// (`PlayerTier.PodcastSlots`, `PodcastLeftW`), so a structure states its own. These tests pin that Classic's data reproduces the
// ordinal map exactly (the oracle below is the old function, kept only here), that a level without data falls back to the generic
// rule, that another structure's map reaches the layout, and that the policy refuses a podcast set the level cannot carry.

using System.Collections.Immutable;
using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class PlayerBarPodcastPolicyTests
{
    static readonly PlayerBarPolicy Classic = ClassicPolicies.PlayerBar;

    /// <summary>The podcast pressure map before T6a, restated; the title block flag is the old left-flow condition (the cover alone at the
    /// narrowest level), which was an ordinal comparison at the call site.</summary>
    static Shell.PlayerBarLayout OldPodcast(Shell.PlayerBarLayout layout)
    {
        var next = layout with
        {
            ShowShuffleRepeat = false,
            ShowVolumeSlider = false,
            ShowPrevNext = layout.Tier >= Shell.PlayerBarTier.Wide,
            ShowTimesElapsed = layout.Tier >= Shell.PlayerBarTier.Comfortable,
            ShowTimesRemaining = layout.Tier >= Shell.PlayerBarTier.Comfortable,
            ShowLikeSlot = layout.Tier >= Shell.PlayerBarTier.Medium,
            LeftW = layout.Tier switch { Shell.PlayerBarTier.Minimal => layout.ArtSize, Shell.PlayerBarTier.Compact => 120f, _ => layout.LeftW },
            ShowTitleBlock = layout.Tier != Shell.PlayerBarTier.Minimal,
        };
        return next with { RightWMax = Shell.PlayerBarRules.RightWidth(in next, hasVideo: true) };
    }

    [Fact]
    public void Classics_data_reproduces_the_ordinal_map_at_every_level()
    {
        for (int t = 0; t <= (int)Shell.PlayerBarTier.Full; t++)
        {
            var layout = Shell.PlayerBarLayout.ForTier((Shell.PlayerBarTier)t, 0f, Classic);
            Assert.Equal(OldPodcast(layout), Shell.PodcastBarLayout(layout, Classic));
            Assert.Equal(OldPodcast(layout), Shell.PodcastBarLayout(layout));   // the active policy is the baseline
        }
    }

    [Fact]
    public void The_baseline_and_classic_state_the_same_podcast_map()
    {
        for (int i = 0; i < PlayerBarPolicy.TierCount; i++)
        {
            Assert.Equal(PlayerBarPolicy.Default.Tiers[i].PodcastSet, Classic.Tiers[i].PodcastSet);
            Assert.Equal(PlayerBarPolicy.Default.Tiers[i].PodcastLeft, Classic.Tiers[i].PodcastLeft);
        }
    }

    [Fact]
    public void The_narrowest_podcast_level_shows_the_cover_alone()
    {
        var minimal = Shell.PodcastBarLayout(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal, 0f, Classic), Classic);
        Assert.False(minimal.ShowTitleBlock);
        Assert.False(minimal.ShowLikeSlot);
        Assert.Equal(minimal.ArtSize, minimal.LeftW);
        var compact = Shell.PodcastBarLayout(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Compact, 0f, Classic), Classic);
        Assert.True(compact.ShowTitleBlock);
        Assert.Equal(120f, compact.LeftW);
        Assert.True(Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal, 0f, Classic).ShowTitleBlock);   // the music bar always shows it
    }

    [Fact]
    public void A_level_without_podcast_data_drops_the_shuffle_repeat_buttons_and_the_inline_volume_rail()
    {
        var plain = Classic with
        {
            Tiers = ImmutableArray.CreateRange(Classic.Tiers, tier => tier with { PodcastSlots = null, PodcastLeftW = float.NaN }),
        };
        var full = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full, 0f, plain);
        Assert.True(full.ShowShuffleRepeat);
        Assert.True(full.ShowVolumeSlider);
        var podcast = Shell.PodcastBarLayout(full, plain);
        Assert.False(podcast.ShowShuffleRepeat);
        Assert.False(podcast.ShowVolumeSlider);
        Assert.True(podcast.ShowPrevNext);          // the generic rule keeps everything else the level carries
        Assert.True(podcast.ShowTimesElapsed);
        Assert.Equal(full.LeftW, podcast.LeftW);
        Assert.Equal(full.ShowQueue, podcast.ShowQueue);
    }

    [Fact]
    public void Another_structures_podcast_map_reaches_the_layout()
    {
        var medium = Classic.Tiers[(int)Shell.PlayerBarTier.Medium];
        var other = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Medium,
                medium with { PodcastSlots = medium.PodcastSet | PlayerBarSlots.PrevNext | PlayerBarSlots.TimesElapsed, PodcastLeftW = 200f }),
        };
        var layout = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Medium, 0f, other);
        var podcast = Shell.PodcastBarLayout(layout, other);
        Assert.True(podcast.ShowPrevNext);
        Assert.True(podcast.ShowTimesElapsed);
        Assert.Equal(200f, podcast.LeftW);
        Assert.False(Shell.PodcastBarLayout(layout, Classic).ShowPrevNext);   // Classic's map at the same level does not
    }

    [Fact]
    public void A_podcast_set_outside_the_level_is_clipped_to_what_the_level_carries()
    {
        var medium = Classic.Tiers[(int)Shell.PlayerBarTier.Medium];
        var other = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Medium,
                medium with { PodcastSlots = medium.PodcastSet | PlayerBarSlots.Expand }),
        };
        var layout = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Medium, 0f, other);
        Assert.False(Shell.PodcastBarLayout(layout, other).ShowExpand);
    }

    [Fact]
    public void The_policy_refuses_a_podcast_set_the_level_cannot_carry()
    {
        Assert.Empty(Classic.Validate());
        var medium = Classic.Tiers[(int)Shell.PlayerBarTier.Medium];

        var outside = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Medium, medium with { PodcastSlots = medium.PodcastSet | PlayerBarSlots.Expand }),
        };
        Assert.NotEmpty(outside.Validate());

        var noDevices = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Medium, medium with { PodcastSlots = PlayerBarSlots.Subtitle }),
        };
        Assert.NotEmpty(noDevices.Validate());

        var negative = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Medium, medium with { PodcastLeftW = -1f }),
        };
        Assert.NotEmpty(negative.Validate());
    }
}
