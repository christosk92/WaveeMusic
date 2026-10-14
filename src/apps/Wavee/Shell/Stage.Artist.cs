// ── Shell/Stage.Artist.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the core half of the stage's Artist pane (themes plan T7a2): what the pane asks for and the facts its sections paint
//
// Role: CORE (the picture is the structure's `Stage.ArtistPane` view, `Themes\Classic\Structure\Views\Stage\ClassicStageArtistPane.cs`)
// Owner: K
// Wave: 7
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10 (the Artist mode); `ArtistRules` (Stage.Rules.cs) holds the decisions
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE ROOT, SIX READERS. The pane resolves the artist exactly as the rail does (`Rail.NowPlayingArtist`), asks for what it paints ONCE
// per (artist, track, scope) and is laid out from `Stage.Layout` by the structure. Each section reads its own tables through one fact
// below (the header on the artist row, This song on the credits edge, Popular on the popular edge + the track rows + the deck's current
// id ...), so a publication re-renders only the section that paints it; the root re-renders on a track, layout or scope change. A fact
// answers null (or an empty list) for "no section": the pane leaves no gap for it.
//
//   • The facts are the old section components' render bodies that touched the entity tables, moved verbatim: the same reads in the same
//     order, so the same signals subscribe the same renders.
//   • Navigation CLOSES the stage first (V-U29): the shell body is collapsed under it.

using System.Buffers;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The Artist pane's props for one stage context: the facts, the demands and the navigation verbs (built once; the pane host caches it).</summary>
    static StageArtistPaneProps ArtistPaneProps(StageCtx ctx)
    {
        var facts = new ArtistFacts(ctx);
        return new StageArtistPaneProps
        {
            Stage = ctx,
            Target = facts.Target,
            Demand = Demand,
            Header = ArtistFacts.Header,
            About = facts.About,
            Song = ArtistFacts.Song,
            Popular = ArtistFacts.Popular,
            Cities = ArtistFacts.Cities,
            Fans = ArtistFacts.Fans,
            EnsurePopular = ArtistFacts.EnsureTop,
            EnsureRelated = ArtistFacts.EnsureRelated,
            GoToArtist = GoToArtist,
            OpenLink = static (begin, key) => StageVerbs.NavigateClosingStage(begin, Shell.Parse(key)),
        };
    }

    /// <summary>What the pane paints, asked ONCE per key: the overview (stats, bio, header, tour — and with it the
    /// gallery and the cities), the popular and related edges while nobody has answered, the track's credits.</summary>
    static void Demand(int artistSlot, int trackSlot)
    {
        var e = Entities.Current.Edges;
        var artist = new Artist(artistSlot);
        if (artist.IsValid)
        {
            Entities.Ensure(artist, ArtistFields.Overview);
            if (e.ArtistPopular.State(artistSlot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistPopular, artistSlot);
            if (e.ArtistRelated.State(artistSlot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistRelated, artistSlot);
        }
        if (new Track(trackSlot).IsValid && e.TrackCredits.State(trackSlot) == EdgeState.Unknown)
            Entities.EnsureEdge(FetchEdge.TrackCredits, trackSlot);
    }

    /// <summary>Open an artist page from the pane: leave the stage FIRST (V-U29).</summary>
    static void GoToArtist(Action<string>? begin, int artistSlot)
    {
        var a = new Artist(artistSlot);
        if (!a.IsValid || !a.Uri.IsValid) return;
        StageVerbs.NavigateClosingStage(begin, Shell.For(a.Uri, a.Name));
    }

    /// <summary>The sections' facts. An instance per stage context holds the one thing that is cached across renders: the bio's plain length.</summary>
    sealed class ArtistFacts(StageCtx ctx)
    {
        StringId _bioKey;
        int _bioChars;

        /// <summary>The artist and track the pane shows. Re-renders the root on the playing track's key, the scope and the billed artists' edge.</summary>
        public StageArtistTarget Target()
        {
            var track = ctx.RowValue();
            _ = Entities.ScopeEpoch.Value;                     // FIRST: a scope switch re-points the table reads below
            uint epoch = Entities.ScopeEpoch.Peek();
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;   // the billed artists land after the row
            var artist = Rail.NowPlayingArtist(track);        // the rail's resolver (§4.12)
            return new StageArtistTarget(artist.IsValid, artist.Slot, track.Slot, epoch);
        }

        /// <summary>The header band: the artist's photo (HeroImageId — the landscape header, else the portrait), name, verified, "listeners · rank", the tour line. Subscribed to the artist table only.</summary>
        public static StageArtistHeaderFacts? Header(int artistSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            var a = new Artist(artistSlot);
            if (!a.IsValid) return null;
            string listeners = a.MonthlyListeners > 0 ? Strings.Stage.Artist.ListenersShort(ArtistRules.ListenerCount(a.MonthlyListeners)) : "";
            string rank = a.WorldRank > 0 ? Strings.Stage.Artist.WorldRank(FormatCache.Int(a.WorldRank)) : "";
            string eyebrow = "";
            string? headline = null;
            if (!a.TourHeadlineId.IsEmpty)
            {
                eyebrow = Entities.Strings.Resolve(a.TourEyebrowId);
                headline = Entities.Strings.Resolve(a.TourHeadlineId);
            }
            return new StageArtistHeaderFacts(a.Name, a.IsVerified, ArtistRules.MetaLine(listeners, rank), eyebrow, headline, a.Uri.Text, Controls.ArtUrl(a.HeroImageId));
        }

        /// <summary>"About": the WHOLE biography and the gallery strip's photos. The bio's plain length is asked once per bio, not per render.</summary>
        public StageArtistAboutFacts? About(int artistSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Edges.ArtistGallery.Changed.Value;
            var a = new Artist(artistSlot);
            if (!a.IsValid) return null;
            StringId bioId = a.BioId.IsEmpty ? a.BioLeadId : a.BioId;
            string bio = Entities.Strings.Resolve(bioId);
            var gallery = a.GallerySlots;
            if (bio.Length == 0 && gallery.Length == 0) return null;
            if (!bioId.Equals(_bioKey)) { _bioKey = bioId; _bioChars = HomeCardText.PlainText(bio)?.Length ?? 0; }   // once per bio, not per render
            int n = Math.Min(gallery.Length, ArtistRules.GalleryCount);
            var urls = new string?[n];
            for (int i = 0; i < n; i++) urls[i] = Controls.ArtUrl(gallery[i]);
            return new StageArtistAboutFacts(bio, _bioChars, urls);
        }

        /// <summary>"This song": the track's credits (Edges.TrackCredits, the drawer's source) GROUPED BY PERSON — "Joey Tempest
        /// · Composer, Lyricist" — each name a link when it resolves to an artist (the credit's own target, else a billed artist
        /// of that name). Null until the credits answer.</summary>
        public static IReadOnlyList<ArtistRules.CreditPerson>? Song(int trackSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            var e = Entities.Current.Edges;
            _ = e.TrackCredits.Changed.Value;
            _ = e.TrackArtists.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;   // the billed names land after the credits
            var t = new Track(trackSlot);
            if (!t.IsValid) return null;
            var payload = e.TrackCredits.Payload(t.Slot);
            if (payload.Length == 0) return null;
            var targets = e.TrackCredits.Targets(t.Slot);

            var names = new string[payload.Length];
            var roles = new string[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                names[i] = Entities.Strings.Resolve(payload[i].Name);
                roles[i] = Entities.Strings.Resolve(payload[i].Role);
            }
            var billed = t.ArtistSlots;
            var billedNames = new string[billed.Length];
            for (int i = 0; i < billed.Length; i++) billedNames[i] = billed[i] > 0 ? new Artist(billed[i]).Name : "";
            var people = ArtistRules.GroupCredits(names, roles, targets, billedNames, billed, ArtistRules.CreditPeopleMax);
            return people.Length == 0 ? null : people;
        }

        /// <summary>"Popular": the artist's top five, the playing one flagged. A click plays the chart FROM that row with the chart's own rows as
        /// the queue (Artist.UI.Chart StartAt — never PlayContext(artist, track), which the server resolves to a different list); the deck
        /// row toggles (Track.Invoke). The rows' text is asked once per popular-edge version (the pane's effect, keyed on the epoch and the version).</summary>
        public static StageArtistPopularFacts Popular(int artistSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            uint epoch = Entities.ScopeEpoch.Peek();
            var scope = Entities.Current;
            _ = scope.Edges.ArtistPopular.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            uint version = scope.Edges.ArtistPopular.Version(artistSlot);
            var a = new Artist(artistSlot);
            if (!a.IsValid) return new StageArtistPopularFacts(epoch, version, false, Array.Empty<StageArtistPopularRow>());
            var slots = a.PopularSlots;
            int n = Math.Min(slots.Length, ArtistRules.PopularCount);
            if (n == 0) return new StageArtistPopularFacts(epoch, version, false, Array.Empty<StageArtistPopularRow>());

            bool art = !Prefs.Appearance.TrackArtworkHidden();
            var rows = new List<StageArtistPopularRow>(n);
            for (int i = 0; i < n; i++)
            {
                var t = new Track(slots[i]);
                if (!t.IsValid || !t.Knows(TrackFields.Title)) continue;   // a thin row waits for its text, never a bare uri
                rows.Add(Row(t, i, artistSlot, art));
            }
            return new StageArtistPopularFacts(epoch, version, art, rows);
        }

        static StageArtistPopularRow Row(Track t, int index, int artistSlot, bool art)
        {
            bool now = Track.IsNowPlaying(t);                       // subscribes Playback.CurrentId
            bool playing = now && Playback.IsPlaying.Value;
            string? image = null;
            if (art)
            {
                StringId id = t.ImageId.IsEmpty ? t.Album.ImageId : t.ImageId;   // a thin chart hit may carry no cover of its own
                image = Controls.ArtUrl(id);
            }
            uint plays = t.PlayCount;
            return new StageArtistPopularRow(index, now, playing, image, t.Title, t.IsExplicit, plays > 0 ? Track.Format.PlaysLabel(plays) : "",
                t.DurationMs > 0 ? Playback.TimeFormat.Clock(t.DurationMs) : "", () => Track.Invoke(t, () => PlayFrom(artistSlot, index)));
        }

        public static void EnsureTop(int artistSlot)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var slots = a.PopularSlots;
            int n = Math.Min(slots.Length, ArtistRules.PopularCount);
            if (n > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots[..n]), TrackFields.Row);
        }

        /// <summary>Play the chart from <paramref name="index"/>, the chart's own rows as the queue (Artist.UI.Chart StartAt).
        /// The slots are re-read at CLICK time: the edge may have been re-committed since the render.</summary>
        static void PlayFrom(int artistSlot, int index)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var slots = a.PopularSlots;
            if ((uint)index >= (uint)slots.Length) return;
            var refs = ArrayPool<EntityRef>.Shared.Rent(slots.Length);
            try
            {
                for (int i = 0; i < slots.Length; i++) refs[i] = new EntityRef(EntityKind.Track, slots[i]);
                Playback.PlayRows(refs.AsSpan(0, slots.Length), index, a.Id);
            }
            finally { ArrayPool<EntityRef>.Shared.Return(refs); }
        }

        /// <summary>"Where people listen": the top five cities, each a name · compact count and its share of the loudest city.</summary>
        public static IReadOnlyList<StageArtistCityRow>? Cities(int artistSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.ArtistCities.Changed.Value;
            var a = new Artist(artistSlot);
            if (!a.IsValid) return null;
            var cities = a.TopCities;
            int n = Math.Min(cities.Length, ArtistRules.CityCount);
            if (n == 0) return null;
            uint max = 0;
            for (int i = 0; i < n; i++) if (cities[i].Listeners > max) max = cities[i].Listeners;
            var rows = new StageArtistCityRow[n];
            for (int i = 0; i < n; i++)
                rows[i] = new StageArtistCityRow(Entities.Strings.Resolve(cities[i].City), ArtistRules.ListenerCount(cities[i].Listeners), ArtistRules.CityFraction(cities[i].Listeners, max));
            return rows;
        }

        /// <summary>"Fans also like": the related artists that have a name (the structure fits as many as the column holds, ≤ 8). Their
        /// identities are asked once per related-edge version (the pane's effect, keyed on the epoch and the version).</summary>
        public static StageArtistFansFacts Fans(int artistSlot)
        {
            _ = Entities.ScopeEpoch.Value;
            uint epoch = Entities.ScopeEpoch.Peek();
            var scope = Entities.Current;
            _ = scope.Edges.ArtistRelated.Changed.Value;
            _ = scope.Artists.Changed.Value;
            uint version = scope.Edges.ArtistRelated.Version(artistSlot);
            var a = new Artist(artistSlot);
            if (!a.IsValid) return new StageArtistFansFacts(epoch, version, 0, Array.Empty<StageArtistFan>());
            var related = a.RelatedSlots;
            var fans = new List<StageArtistFan>(Math.Min(related.Length, ArtistRules.FansMax));
            for (int i = 0; i < related.Length && fans.Count < ArtistRules.FansMax; i++)
            {
                var r = new Artist(related[i]);
                if (!r.IsValid || r.Name.Length == 0) continue;
                fans.Add(new StageArtistFan(r.Slot, r.Name, Controls.ArtUrl(r.ImageId)));
            }
            return new StageArtistFansFacts(epoch, version, related.Length, fans);
        }

        public static void EnsureRelated(int artistSlot)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var related = a.RelatedSlots;
            int n = Math.Min(related.Length, ArtistRules.FansMax);
            if (n > 0) Entities.Ensure(MemoryMarshal.Cast<int, Artist>(related[..n]), ArtistFields.Identity);
        }
    }
}
