using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>Your Episodes, over the ONE track table (B2 plan §3.2/§3.5): the listen-later list is a real Spotify
    /// playlist (<see cref="Spotify.Podcasts.SavedPlaylistUri"/>), so once <see cref="Spotify.Podcasts.SavedState"/>
    /// discovers it the episodes are just <see cref="Track.TableSource.ForPlaylist"/> rows — the same selection bar,
    /// menu and row template as any other episode-carrying table, no bespoke list here any more. The states this
    /// component still owns are the ones ABOVE the table: has the listen-later playlist even been discovered yet
    /// (<see cref="Spotify.Podcasts.SavedReadState"/> Pending/Empty/Failed) — Ready hands off to the table, which then
    /// owns its OWN membership loading via the edge's state.
    /// <para>T10c1: the states' picture (the cold seed rows, the fade-only reveal) is the structure's `Library.SavedEpisodes` view (Classic: `ClassicLibrarySavedEpisodes`).</para></summary>
    [CoreHost]
    sealed class SavedEpisodesReader : Component
    {
        static readonly Track.TableProfile s_profile = Track.TableProfile.From(
            Detail.Config.Playlist with
            {
                Content = DetailContent.Episodes,
                Heart = HeartMode.None,
                Recommendations = false,
                ShowTempo = false,
                ShowVersions = false,
                PlaysColumnOptIn = false,
                ShowTrackArtist = false,
            });

        /// <summary>The one fixture the Fake demo carries for the listen-later playlist (present in
        /// <c>assets/spotify/playlists.json</c>); discovery itself is not faked (<see cref="Spotify.Podcasts.ReadSavedAsync"/>
        /// short-circuits under <c>--fake</c>), so this is the fixed point Fake mode resolves instead.</summary>
        const string FakeSavedUri = "spotify:playlist:37i9dQZF1FgnTBfUlzkeKt";

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var read = UseResource(ct => Platform.Args.Fake
                ? Task.FromResult(new Spotify.Podcasts.Mutation(true, 200))
                : Spotify.Podcasts.ReadSavedAsync(ct), new Spotify.Podcasts.Mutation(false, 0), DepKey.From((int)epoch));

            string uri = Platform.Args.Fake ? FakeSavedUri : Spotify.Podcasts.SavedPlaylistUri;
            // Fake mode runs no discovery, so nothing else ever asks for the fixture playlist: its row is minted here (the factory allocates an empty row for an unseen uri, which
            // the effect below then fills), instead of waiting forever on a row nobody creates.
            int slot = uri.Length == 0 ? Table.None
                : Platform.Args.Fake ? Entities.Playlist(EntityId.Parse(uri)).Slot
                : Entities.Current.Playlists.TryGetSlot(EntityId.Parse(uri), out int s) ? s : Table.None;

            // Fake mode never runs discovery (ReadSavedAsync short-circuits to 501), so SavedState never reaches
            // Ready there — the fixture playlist's OWN membership edge is what Fake mode has, so a resolved slot is
            // Ready by itself.
            var state = Platform.Args.Fake
                ? (slot > Table.None ? Spotify.Podcasts.SavedReadState.Ready : Spotify.Podcasts.SavedReadState.Pending)
                : Spotify.Podcasts.SavedState;

            UseEffect(() =>
            {
                if (slot <= Table.None) return;
                if (Platform.Args.Fake) Entities.Ensure(new Playlist(slot), PlaylistFields.Identity | PlaylistFields.TrackCount);
                Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot, 0);
            }, DepKey.From(slot, (int)epoch));

            return Slots.View(ViewKeys.LibrarySavedEpisodes, new LibrarySavedEpisodesProps
            {
                Pending = state == Spotify.Podcasts.SavedReadState.Pending,
                Failed = state == Spotify.Podcasts.SavedReadState.Failed,
                Content = () => state == Spotify.Podcasts.SavedReadState.Ready && slot > Table.None
                    ? TableOf(slot)
                    : PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.SavedEmpty)),
                OnFailed = () => PodcastReaderUI.Failed(read.Refresh),
                Feed = () => FeedOf(slot),
                PlayEpisode = static uri => Playback.PlayOrToggleContext(uri),
                OpenEpisode = static (uri, title) => { var id = EntityId.Parse(uri); if (id.IsValid) Shell.GoTo(Shell.For(new EntityUri(id), title)); },
            });
        }

        /// <summary>The listen-later episodes as rows, newest published first (a reactive read: the membership, the episode table and the show names). Asks for the rows' identity and
        /// progress, like the table does; a track in a mixed list is not an episode and is left out.</summary>
        static LibraryEpisodesFeed FeedOf(int slot)
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.PlaylistTracks.Changed.Value; _ = scope.Episodes.Changed.Value; _ = scope.Shows.Changed.Value;
            if (slot <= Table.None) return new LibraryEpisodesFeed(Array.Empty<LibraryEpisodeRow>(), 0);
            var targets = scope.Edges.PlaylistTracks.Targets(slot);
            var payload = scope.Edges.PlaylistTracks.Payload(slot);
            var episodes = new List<int>(targets.Length);
            for (int i = 0; i < targets.Length; i++)
                if (i < payload.Length && payload[i].Kind == PlaylistItemKind.Episode) episodes.Add(targets[i]);
            if (episodes.Count > 0) Entities.Ensure(System.Runtime.InteropServices.MemoryMarshal.Cast<int, Episode>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(episodes)), EpisodeFields.Identity | EpisodeFields.Progress);
            var items = new List<(int Published, LibraryEpisodeRow Row)>(episodes.Count);
            long total = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var playing = Playback.CurrentId.Value;
            foreach (int e in episodes)
            {
                var ep = new Episode(e);
                if (!ep.IsValid) continue;
                float pct = ep.ProgressMs == int.MaxValue ? 1f : ep.DurationMs > 0 ? Math.Clamp(ep.ProgressMs / (float)ep.DurationMs, 0f, 1f) : 0f;
                byte status = (byte)(Episode.Rules.Played(pct) ? 2 : Episode.Rules.InProgress(pct) ? 1 : 0);
                long age = now - ep.PublishedAt;
                byte bucket = (byte)(age < 86_400L ? 0 : age < 7 * 86_400L ? 1 : 2);
                string show = ep.ShowSlot > Table.None ? new Show(ep.ShowSlot).Title : "";
                total += Math.Max(0, ep.DurationMs);
                string uri = ep.Uri.Text;
                items.Add((ep.PublishedAt, new LibraryEpisodeRow(uri, ep.Title, show, Controls.ArtUrl(ep.ImageId) ?? "", Episode.DurationLabel(ep.DurationMs),
                    status == 1 ? Episode.LeftLabel(ep.ProgressMs, ep.DurationMs) : "", status, bucket, ep.Id == playing)));
            }
            items.Sort(static (a, b) => b.Published.CompareTo(a.Published));
            var rows = new LibraryEpisodeRow[items.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = items[i].Row;
            return new LibraryEpisodesFeed(rows, total);
        }

        Element TableOf(int slot) => Track.Table(new Track.TableArgs
        {
            Source = Track.TableSource.ForPlaylist(new Playlist(slot)),
            Profile = s_profile,
            ShowToolbar = false,
            Embedded = true,
            ScrollKey = "saved-episodes:" + slot,
        });
    }
}
