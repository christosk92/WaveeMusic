// ── Shell/Rail.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// what the right rail still keeps in the core: the "Next up" rows' data the now-playing body and the video body share, the queue
// body's seam and the shared reads (the playing track, its cover)
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1400 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// T6b MOVED THE PICTURE OF THE PANEL TO THE STRUCTURE. The rail's frame (docked + floating), its header, the docked video cap's
// body, the NowPlaying arm and the other arms are `Shell.Panel` (`Themes\Classic\Structure\Views\Panel\ClassicPanel.cs`); the hero
// tile, its header row, the art menu and the now-playing body are `Player.NowPlayingHero` (`ClassicNowPlayingHero.cs`); the friends
// body is `Panel.Friends` (`ClassicFriends.cs`); the style flyout, the twelve mini-arts and the swatches are `Player.DeckStyleFlyout`
// (`ClassicDeckFlyout.cs`, `Rules\DeckThumbnails.cs`). The core half (what each view reads, what each click asks for) is
// `RailWiring` (`Shell/Rail.Wiring.cs`); the state machine stays `Shell.PanelHost` and the decisions stay `Rail.VideoCoupling`.
//
// T7c MOVED THE VIDEO BODY (the track meta, the "Up next" eyebrow, the "no video for this song" placeholder) to the structure
// (`ClassicVideoPanelBody`, `Themes\Classic\Structure\Views\Video`); the docked card is the `Video.Docked` key.
//
// P3 MOVED THE LAST PICTURES: the lyrics header (the title, the second-line toggle, the AI sparkle, expand, the inspector, close) is
// `Panel.LyricsHeader` (`ClassicLyricsHeader.cs`; its facts and verbs are `RailWiring`'s, the sparkle's verdict `AiLyrics.HeaderFacts`) and
// the "Next up" rows are `Panel.NextUp` (`ClassicNextUp.cs`, which the now-playing and video bodies place); what stays here is the rows'
// DATA (`NextUpRows`: the window, the queue reads, each row's verbs and key). The queue body is the core host
// `Queue.RailPanelHost` (`Entities/Queue.UI.cs`), which installs itself through `Rail.QueueBody` and mounts the `Queue.Panel` view (T7d).
//
// Mounted from other stage-B files: `Deck.View()` (K2), `Lyrics.NpvPeek()` (the core host of `Player.LyricsPeek`) and `Lyrics.View()` (K3).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Rail
{
    /// <summary>The Queue body — the rail's queue panel, installed once at composition by <c>Queue.InstallUi</c> (`Entities/Queue.UI.cs`),
    /// which App.cs runs unconditionally before the first frame. A headless host that mounts the queue mode installs it first.</summary>
    public static Func<Element>? QueueBody { get; set; }

    // ── shared reads ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playing TRACK — subscribes the playable and the track table — or an invalid handle (an episode, a
    /// module playable or nothing).</summary>
    internal static Track NowTrack()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
    }

    /// <summary>The playing track's cover url, read LIVE (for a bound thunk that must follow a track change).</summary>
    internal static string? NowArtUrl()
    {
        var r = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? Controls.ArtUrl(new Track(r.Slot).ImageId) : null;
    }

    /// <summary>The track's FIRST credited artist — the one the rail's About card and the fullscreen stage's Artist pane
    /// both read. <c>default</c> when the track is not (yet) valid or carries no artist edge.</summary>
    internal static Artist NowPlayingArtist(Track track)
    {
        if (!track.IsValid) return default;
        var slots = track.ArtistSlots;
        return slots.Length > 0 && slots[0] > 0 ? new Artist(slots[0]) : default;
    }

    // ── "Next up" rows (the NPV section and the Video body share them; the picture is `Panel.NextUp`) ────────────────────────────

    /// <summary>The "Next up" rows' data, read inside the `Panel.NextUp` view's render: it subscribes what the rows read (the queue edge,
    /// the track table — a title or an artist line landing late — and the artwork preference), so a table edge re-renders these five
    /// rows and not the panel that holds them.</summary>
    internal static NextUpRow[] NextUpRows()
    {
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table reads below
        _ = Entities.Current.Edges.Queue.Changed.Value;
        bool art = ShellPolicy.Tracked.Lists.TrackArtwork;
        Span<int> window = stackalloc int[Shell.NextUpWindow.Max];
        int n = Shell.NextUpWindow.Fill(window);
        var rows = new NextUpRow[n];
        var items = Queue.Rows;
        for (int k = 0; k < n; k++)
        {
            int i = window[k];
            rows[k] = NextUpRowOf(new Track(Queue.RefAt(i).Slot), i, items[i].ItemId, art);
        }
        return rows;
    }

    /// <summary>One "Next up" row's data for the shared media surface (<c>Track.RowData</c>): the hand, the play FAB over the art, a track
    /// drag and the right-click menu. The row CLICK plays that queue item —
    /// the same <c>play</c> the FAB runs (<see cref="Wavee.Theming.Contracts.IQueueVerbs.Play"/>, the queue panel's own skip), run through
    /// <c>Track.Invoke</c> so the deck row toggles pause instead. The item is resolved inside the LIVE queue when clicked:
    /// the surface keeps its data across a queue shift (a data-equal re-push carries no new delegate into the render), so
    /// a captured index would go stale. The key carries
    /// the queue item and the artwork flag, so a settings flip REMOUNTS rather than re-binds. Like every queue row it has
    /// no "…" button (<see cref="QueueRowRules.ShowsMenuButton"/>): the menu is the right-click.</summary>
    static NextUpRow NextUpRowOf(Track t, int queueIndex, ulong itemId, bool art)
    {
        // The queue panel's own skip (IQueueVerbs.Play): resolves the item in the LIVE queue at click time, trims
        // the rows skipped over and plays — so a rail click and a queue-panel click are the same act.
        Action play = () => Shell.QueueVerbs.Default.Play(itemId, queueIndex, new EntityRef(EntityKind.Track, t.Slot));
        Action click = () => Track.Invoke(t, play);
        var data = Track.RowData(t, new Track.RowDataOptions(OnClick: click, OnPlay: play, ShowArtwork: art,
                                                             ShowExplicit: false, ShowVideo: true,
                                                             ShowMenu: QueueRowRules.ShowsMenuButton));
        return new NextUpRow((itemId != 0 ? "vrp:i" + itemId : "vrp:e" + queueIndex) + (art ? ":art" : ":noart"), art, data);
    }
}
