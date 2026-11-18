// ── Themes/Classic/Structure/Views/Episode/ClassicEpisode.cs — the registration of Classic's episode discussion (themes plan 6.1, T9c) ──────────
//
// The episode page is the podcast pages v2 (`Views\Podcast\ClassicPodcastEpisode.cs`); its Comments section is the core's discussion
// (`PodcastReaderUI.Discussion`), drawn by `ClassicDiscussion.cs` through the keys registered here. The older page's slots (the primary, the show
// attribution, the action satellites, the Save heart, the rail's About block, the show rating popover) and its reader went with it.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's episode family: the discussion's registration.</summary>
internal static class ClassicEpisode
{
    /// <summary>Registers the discussion's keys (called by <see cref="ClassicStructure.Register"/>).</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.EpisodeDiscussion, ClassicDiscussion.ThreadTemplate, "classic.episode.discussion");
        views.Add(ViewKeys.EpisodeCommentCard, ClassicDiscussion.CardTemplate, "classic.episode.comment-card");
        views.Add(ViewKeys.EpisodeCommentSeed, ClassicDiscussion.SeedTemplate, "classic.episode.comment-seed");
        views.Add(ViewKeys.EpisodeLoadMore, ClassicDiscussion.LoadMoreTemplate, "classic.episode.load-more");
        views.Add(ViewKeys.EpisodeReactionFlyout, ClassicDiscussion.FlyoutTemplate, "classic.episode.reaction-flyout");
        views.Add(ViewKeys.EpisodeReactionRoster, ClassicDiscussion.RosterTemplate, "classic.episode.reaction-roster");
        views.Add(ViewKeys.EpisodeReactionPeople, ClassicDiscussion.PeopleTemplate, "classic.episode.reaction-people");
    }
}
