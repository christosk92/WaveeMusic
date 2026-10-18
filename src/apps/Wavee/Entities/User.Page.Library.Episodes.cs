// ── Entities/User.Page.Library.Episodes.cs ───────────────────────────────────────────────────────────────────────────
// The PODCASTS library's "your episodes first" arm: a structure that draws the page as "your episodes · N" pinned over the shows list, with the feed (or the picked show) beside it,
// asks for it with `LibraryTierPolicy.PodcastsEpisodesFirst`. The host keeps the mode (`PodcastsMode`, 1 = Your Episodes) exactly as it was, so both bodies stay reachable: the
// pinned row writes 1, a show picked in the list writes 0. A structure that does not ask (Classic) keeps its two mode words, and no props are published.

using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct User
{
    sealed partial class LibraryPage
    {
        LibraryEpisodesProps? _episodesProps;

        /// <summary>The podcasts page, the structure asked, and the page is split (the one-column drill-in keeps the mode words).</summary>
        bool EpisodesFirst => IsPodcasts && ShellPolicy.Tracked.LibraryTiers.PodcastsEpisodesFirst
                              && !(_layoutTier is { } tier && Theming.LibraryLadders.IsSingleColumn(tier.Value));

        LibraryEpisodesProps EpisodesProps() => _episodesProps ??= new LibraryEpisodesProps(
            Prop.Of(SavedEpisodeCount), static () => PodcastsMode.Value == 1, static () => PodcastsMode.Value = 1);

        /// <summary>How many episodes the listen-later list holds (0 while it is not discovered or its count has not landed).</summary>
        static int SavedEpisodeCount()
        {
            _ = Entities.ScopeEpoch.Value;
            string uri = Platform.Args.Fake ? "spotify:playlist:37i9dQZF1FgnTBfUlzkeKt" : Spotify.Podcasts.SavedPlaylistUri;
            if (uri.Length == 0) return 0;
            var table = Entities.Current.Playlists;
            _ = table.Changed.Value;
            if (!table.TryGetSlot(EntityId.Parse(uri), out int slot)) return 0;
            var playlist = new Playlist(slot);
            return playlist.IsValid && playlist.Knows(PlaylistFields.TrackCount) ? playlist.TrackCount : 0;
        }
    }
}
