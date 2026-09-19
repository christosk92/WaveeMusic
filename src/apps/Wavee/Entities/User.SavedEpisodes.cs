using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>Your Episodes, over the ONE track table (B2 plan §3.2/§3.5): the listen-later list is a real Spotify
    /// playlist (<see cref="Spotify.Podcasts.SavedPlaylistUri"/>), so once <see cref="Spotify.Podcasts.SavedState"/>
    /// discovers it the episodes are just <see cref="Track.TableSource.ForPlaylist"/> rows — the same selection bar,
    /// menu and row template as any other episode-carrying table, no bespoke list here any more. The states this
    /// component still owns are the ones ABOVE the table: has the listen-later playlist even been discovered yet
    /// (<see cref="Spotify.Podcasts.SavedReadState"/> Pending/Empty/Failed) — Ready hands off to the table, which then
    /// owns its OWN membership loading via the edge's state.</summary>
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
            int slot = uri.Length > 0 && Entities.Current.Playlists.TryGetSlot(EntityId.Parse(uri), out int s)
                ? s : Table.None;

            // Fake mode never runs discovery (ReadSavedAsync short-circuits to 501), so SavedState never reaches
            // Ready there — the fixture playlist's OWN membership edge is what Fake mode has, so a resolved slot is
            // Ready by itself.
            var state = Platform.Args.Fake
                ? (slot > Table.None ? Spotify.Podcasts.SavedReadState.Ready : Spotify.Podcasts.SavedReadState.Pending)
                : Spotify.Podcasts.SavedState;

            UseEffect(() =>
            {
                if (slot > Table.None) Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot, 0);
            }, DepKey.From(slot, (int)epoch));

            return new SkelRegionEl(
                Pending: () => state == Spotify.Podcasts.SavedReadState.Pending,
                Failed: () => state == Spotify.Podcasts.SavedReadState.Failed,
                Content: () => state == Spotify.Podcasts.SavedReadState.Ready && slot > Table.None
                    ? TableOf(slot)
                    : PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.SavedEmpty)),
                ShimmerSource: () => new BoxEl { Direction = 1, Children = SeedRows() },
                OnFailed: () => PodcastReaderUI.Failed(read.Refresh),
                Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null);
        }

        Element TableOf(int slot) => Track.Table(new Track.TableArgs
        {
            Source = Track.TableSource.ForPlaylist(new Playlist(slot)),
            Profile = s_profile,
            ShowToolbar = false,
            Embedded = true,
            ScrollKey = "saved-episodes:" + slot,
        });

        static Element[] SeedRows()
        {
            var rows = new Element[6];
            for (int i = 0; i < rows.Length; i++) rows[i] = Episode.SeedReaderRow(false);
            return rows;
        }
    }
}

public static class SavedEpisodeLayout
{
    public static bool Compact(float width, bool previous)
        => width < (previous ? 664f : 616f);
}
