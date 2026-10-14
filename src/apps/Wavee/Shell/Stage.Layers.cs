// ── Shell/Stage.Layers.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the core half of the stage's layout layers (themes plan T7a2): the props each layer's view is mounted with, and the facts they read
//
// Role: CORE
// Spec: docs/guide/theming.md section 2b (the stage's layers)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHICH LAYOUT IS DRAWN IS CORE (owner Q16): the allocator (`Stage.Layout`, `ClassOf/Resolve`, its authored extents), `StagePolicy`, the
// look (`Stage.Look`, `LayoutRules`) and the predicates that decide which layers are mounted. The structure's templates draw each layer
// for the look the core decided, in every aspect class; they read the stage context's signals inside their own renders (so a layer
// re-renders on exactly what it read) and call the facts below for what they may not name (`Entities`, `Playback`, `Shell`, `Prefs`).
// Each fact is the body of the old component's render that touched those tables, moved verbatim: the same reads, in the same order,
// so the same signals subscribe the same renders.

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Stage
{
    // ══ the props, one record per layer, built ONCE per stage context ═══════════════════════════════════════════════

    /// <summary>The backdrop's props: it is hidden under an opaque Artist header photo (Visualizer mode, the Artist layout, the photo known).</summary>
    static StageBackdropProps BackdropProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Shown = () => !(ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist && ctx.HeroFact.Value == HeroState.Header),
    };

    /// <summary>The artist hero photo's props: the next track's header decodes hidden under the hero in the last seconds.</summary>
    static StageHeroProps HeroProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        PrefetchDue = static () => HeroRules.PrefetchDue(Playback.PositionMs.Value, Playback.DurationMs.Value),
    };

    /// <summary>The face scrim's props: the stage veil at <see cref="LayoutRules.BackdropScrim"/> over a backdrop face, none outside Visualizer mode.</summary>
    static StageFaceScrimProps FaceScrimProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Alpha = () =>
        {
            var eff = ctx.Look.Value.Eff;
            return ctx.Mode.Value == Mode.Visualizer ? LayoutRules.BackdropScrim(eff) : 0f;
        },
    };

    /// <summary>The now-playing card's props: Visualizer mode, a layout that has the card, room for the chips.</summary>
    static StageNowPlayingCardProps NowPlayingCardProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Shown = () => ctx.Mode.Value == Mode.Visualizer && ctx.Layout.Value.ShowChips && LayoutRules.ShowsNowPlayingCard(ctx.Look.Value.Eff),
    };

    /// <summary>The Artist layout's name layer: the artist whose photo shows; while the overview is pending, the first billed artist.</summary>
    static StageArtistNameProps ArtistNameProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Name = () =>
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;
            var track = ctx.RowValue();
            int slot = ctx.HeroArtist.Value;
            var artist = slot > 0 ? new Artist(slot) : Rail.NowPlayingArtist(track);
            return artist.IsValid ? artist.Name : "";
        },
    };

    /// <summary>The identity's props: the title, the meta line, the chips, the "…" menu and the artist link.</summary>
    static StageIdentityProps IdentityProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Title = () => ShownTitle(ctx),
        Meta = heroArtist => MetaFacts(ctx, heroArtist),
        Chips = () => ChipFacts(ctx),
        NowPlayingMenu = static () => NowPlayingMenu?.Invoke(),
        OpenArtist = OpenPlayingArtist,
    };

    // ══ the identity's facts ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The title shown: the playing track's, or "Nothing playing" (reads the track key).</summary>
    static string ShownTitle(StageCtx ctx)
    {
        var track = ctx.RowValue();
        string uri = track.IsValid ? track.Uri.Text : "";
        string title = track.IsValid ? track.Title : "";
        return Transport.UsesTitle(title, uri) ? title : Loc.Get(Strings.Player.NothingPlaying);
    }

    /// <summary>"Artist · Album" (the previous stage's MetaLink): subscribed to the track key and to Albums.Changed for the album title. With a
    /// <paramref name="heroArtist"/> slot the line carries the OTHER billed artists ("with Cheat Codes · Sex").</summary>
    static StageMetaFacts MetaFacts(StageCtx ctx, int heroArtist)
    {
        _ = Entities.ScopeEpoch.Value;                    // FIRST: a scope switch re-points the table reads below
        var track = ctx.RowValue();
        _ = Entities.Current.Albums.Changed.Value;
        _ = Entities.Current.Artists.Changed.Value;       // the names land after the credits
        _ = Entities.Current.Edges.TrackArtists.Changed.Value;
        string album = track.IsValid && track.Album.IsValid ? track.Album.Title : "";
        string line;
        if (heroArtist > 0 && track.IsValid)
        {
            // the big name is the artist whose header shows: this line carries the OTHERS (never the same name twice)
            string others = OtherArtistNames(track, heroArtist);
            string with = others.Length > 0 ? Strings.Stage.WithArtists(others) : "";
            line = with.Length > 0 && album.Length > 0 ? with + " · " + album : with.Length > 0 ? with : album;
        }
        else
        {
            string artists = track.IsValid ? ArtistNames(track) : "";
            line = artists.Length > 0 && album.Length > 0 ? artists + " · " + album : artists.Length > 0 ? artists : album;
        }
        if (line.Length == 0) return new StageMetaFacts("", false);   // nothing playing: an empty line, never a null one
        return new StageMetaFacts(line, Shell.LinkFor(track, Shell.LinkSlot.Artist).Kind != Shell.RouteKind.NotFound);
    }

    /// <summary>The billed artists but <paramref name="exceptSlot"/>, ", " joined (the Artist hero's "with …" line).</summary>
    static string OtherArtistNames(Track track, int exceptSlot)
    {
        var slots = track.ArtistSlots;
        string joined = "";
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == exceptSlot) continue;
            string name = new Artist(slots[i]).Name;
            if (name.Length == 0) continue;
            joined = joined.Length == 0 ? name : joined + ", " + name;
        }
        return joined;
    }

    /// <summary>The billed artists, ", " joined, from the track's credits (Edges.TrackArtists — the player bar's source,
    /// Track.UI.cs MetadataLine: never the flat line first); the flat <c>ArtistLineId</c> only when no credit has a name yet.</summary>
    static string ArtistNames(Track track)
    {
        var slots = track.ArtistSlots;
        string joined = "";
        for (int i = 0; i < slots.Length; i++)
        {
            string name = new Artist(slots[i]).Name;
            if (name.Length == 0) continue;
            joined = joined.Length == 0 ? name : joined + ", " + name;
        }
        return joined.Length > 0 ? joined : Entities.Strings.Resolve(track.ArtistLineId);
    }

    /// <summary>The playing track's artist page: leave the stage BEFORE navigating (V-U29).</summary>
    static void OpenPlayingArtist(Action<string>? begin)
    {
        var r = Playback.Current.Peek();
        if (r.Kind != EntityKind.Track) return;
        var route = Shell.LinkFor(new Track(r.Slot), Shell.LinkSlot.Artist);
        if (route.Kind == Shell.RouteKind.NotFound) return;
        StageVerbs.NavigateClosingStage(begin, route);
    }

    /// <summary>The chips under the title: the format chip, "Synced lyrics", and (outside Visualizer mode) BPM from the Audio group. The reads
    /// are the old chips component's, so the format / device / owner signals re-render THIS row only (V-U50).</summary>
    static IReadOnlyList<StageChipFact> ChipFacts(StageCtx ctx)
    {
        var mode = ctx.Mode.Value;
        var track = ctx.RowValue();
        bool hasTimed = ctx.HasTimedLyrics.Value;
        var kids = new List<StageChipFact>(4);
        var fmt = Playback.StreamFormat.Value;
        _ = Playback.Devices.Changed.Value;                   // the roster is part of the remote verdict
        bool remote = Shell.DevicePicker.IsRemote(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows);
        if (Transport.ShowsQualityBadge(!fmt.IsEmpty, remote)) kids.Add(new StageChipFact(Icons.Equalizer, Entities.Strings.Resolve(fmt)));
        if (hasTimed) kids.Add(new StageChipFact(Icons.Document, Loc.Get(Strings.Stage.SyncedLyrics)));
        if (mode != Mode.Visualizer && track.IsValid && track.Knows(TrackFields.Audio) && track.Tempo > 0)
            kids.Add(new StageChipFact(null, Strings.Stage.Bpm(((track.Tempo + 5) / 10).ToString(System.Globalization.CultureInfo.InvariantCulture))));   // a formatted key: the generated method already formats (V-D15)
        return kids;
    }
}
