// ── Themes/Zune/Structure/Views/Episode/ZuneEpisode.cs — the registration of Zune's episode discussion and its quiet line (themes plan 6.3) ─────────
//
// The episode page is the podcast pages v2 (`Views\Podcast\ZunePodcastEpisode.cs`); its comments are the core's discussion (`Episode.Discussion.cs`),
// drawn by `ZuneEpisodeDiscussion.cs` through the keys registered here. The older page's slots (the primary, the attribution, the actions, the save,
// the rail's about, the rating popover, the reader, about and chapters) were deleted with it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's episode family: the discussion's registration and the quiet lines it shares.</summary>
internal static class ZuneEpisode
{
    /// <summary>Registers the discussion's keys (the comments the podcast pages' episode page mounts under its "comments" pivot).</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.EpisodeDiscussion, ZuneEpisodeDiscussion.ThreadTemplate, "zune.episode.discussion");
        views.Add(ViewKeys.EpisodeCommentCard, ZuneEpisodeDiscussion.CardTemplate, "zune.episode.comment-card");
        views.Add(ViewKeys.EpisodeCommentSeed, ZuneEpisodeDiscussion.SeedTemplate, "zune.episode.comment-seed");
        views.Add(ViewKeys.EpisodeLoadMore, ZuneEpisodeDiscussion.LoadMoreTemplate, "zune.episode.load-more");
        views.Add(ViewKeys.EpisodeReactionFlyout, ZuneEpisodeDiscussion.FlyoutTemplate, "zune.episode.reaction-flyout");
        views.Add(ViewKeys.EpisodeReactionRoster, ZuneEpisodeDiscussion.RosterTemplate, "zune.episode.reaction-roster");
        views.Add(ViewKeys.EpisodeReactionPeople, ZuneEpisodeDiscussion.PeopleTemplate, "zune.episode.reaction-people");
    }

    /// <summary>A quiet sentence the theme writes (the empty, closed and failed notes): the meta voice, wrapping, lowered as a label.</summary>
    internal static Element Quiet(string text) => R.Type.TrackMeta(text) with
    {
        Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };
}
