// ── Entities/User.Page.Library.Episodes.cs ───────────────────────────────────────────────────────────────────────────
// The PODCASTS library's "your episodes first" arm: a structure that draws the page as "your episodes · N" pinned over the shows list, with the feed (or the picked show) beside it,
// asks for it with `LibraryTierPolicy.PodcastsEpisodesFirst`. The host keeps the mode (`PodcastsMode`, 1 = Your Episodes) exactly as it was, so both bodies stay reachable: the
// pinned row writes 1, a show picked in the list writes 0. A structure that does not ask (Classic) keeps its two mode words, and no props are published.

using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>The followed show has an episode that is NEW: unplayed and published after the show's last play (`ShowLedger.IsFresh`, the show page's own predicate over its resident
    /// episodes). A show never played has no "since", so none is new. Reactive: it reads the episode table and the show's membership.</summary>
    internal static bool ShowHasNew(int showSlot)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        _ = scope.Episodes.Changed.Value;
        var episodes = scope.Edges.ShowEpisodes.Targets(showSlot);
        int last = 0;
        for (int i = 0; i < episodes.Length; i++) last = Math.Max(last, new Episode(episodes[i]).PlayedAt);
        if (last == 0) return false;
        for (int i = 0; i < episodes.Length; i++)
        {
            var e = new Episode(episodes[i]);
            float pct = e.ProgressMs == int.MaxValue ? 1f : e.DurationMs > 0 ? Math.Clamp(e.ProgressMs / (float)e.DurationMs, 0f, 1f) : 0f;
            if (ShowLedger.IsFresh(pct, e.PublishedAt, last)) return true;
        }
        return false;
    }

    sealed partial class LibraryPage
    {
        LibraryEpisodesProps? _episodesProps;
        Prop<string>? _savedCountText;

        /// <summary>The podcasts page, the structure asked, and the page is split (the one-column drill-in keeps the mode words).</summary>
        // A stored GRID view keeps its grid: the shows list is the pinned row's column only while the view is a list (the grid takes the old arrangement, with the mode words).
        bool EpisodesFirst => IsPodcasts && ShellPolicy.Tracked.LibraryTiers.PodcastsEpisodesFirst && !IsGridView(View.Value)
                              && !(_layoutTier is { } tier && Theming.LibraryLadders.IsSingleColumn(tier.Value));

        LibraryEpisodesProps EpisodesProps() => _episodesProps ??= new LibraryEpisodesProps(
            Prop.Of(SavedEpisodeCount), static () => PodcastsMode.Value == 1, static () => PodcastsMode.Value = 1);

        /// <summary>Your Episodes' count as ONE bound text, allocated once: a borrowing structure words its Your Episodes mode with it ("Your Episodes · 8",
        /// contract 1.113), as it words Followed shows with the page's own count. Read whichever mode is on screen, so the word says what switching to it shows;
        /// empty until the listen-later list's count is known, as the shows' count is while the relation has not answered.</summary>
        Prop<string> SavedCountText() => _savedCountText ??= Prop.Of(static () => SavedEpisodeCount() is var n and > 0 ? FormatCache.Int(n) : "");

        /// <summary>How many episodes the listen-later list holds: its membership's total once the list has answered (what Your Episodes' table shows), else the
        /// playlist row's track count (discovery lands the row before anything asks for the rows); 0 while it is not discovered or neither has landed. Reactive.</summary>
        static int SavedEpisodeCount()
        {
            _ = Entities.ScopeEpoch.Value;
            string uri = Platform.Args.Fake ? "spotify:playlist:37i9dQZF1FgnTBfUlzkeKt" : Spotify.Podcasts.SavedPlaylistUri;
            if (uri.Length == 0) return 0;
            var table = Entities.Current.Playlists;
            _ = table.Changed.Value;
            if (!table.TryGetSlot(EntityId.Parse(uri), out int slot)) return 0;
            var edge = Entities.Current.Edges.PlaylistTracks;
            _ = edge.Changed.Value;
            if (edge.State(slot) != EdgeState.Unknown) return edge.Total(slot);
            var playlist = new Playlist(slot);
            return playlist.IsValid && playlist.Knows(PlaylistFields.TrackCount) ? playlist.TrackCount : 0;
        }
    }
}
