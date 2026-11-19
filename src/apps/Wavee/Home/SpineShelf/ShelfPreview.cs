// ── Home/SpineShelf/ShelfPreview.cs — the song previews' hover media (the Spine Shelf, the Release Spotlight): ONE canvas player, ONE clip player, one policy ─────────────────────────
//
//   Live      the tile the pointer (or focus) is on, "<item key>#<song>" ("" none). Set at once on enter; the tile widens on it.
//   Showing   the tile whose canvas video element is mounted (under its still, in the still's box). Set when the tile has come to rest
//             AND its canvas file is on disk (the open's start cost lands on a still frame); the still fades out on the video's first
//             frame. On a leave the tile keeps it while its still fades back in over it and the tile narrows (the video follows the
//             tile's moving clip: the engine keeps a video's ancestor chain UI-owned), then the shelf releases it (Release); a tile that
//             settles meanwhile waits for that release (one player).
//
// Only one video decodes: the one player is re-opened per hover. The player is built once, the first time a shelf mounts in a window
// that composites video (MF startup and the engine pair are paid there, never on a hover), over its own MF backend as the canvas host's
// is (Shell/Canvas.cs). Opening a card asks for its five canvas files (CanvasVideo.Files, background), so a hover opens a local file.
//
// The 30 s clip is PreviewAudio's (owner spec 2026-11-17): every hover plays it, muted unless the shelf's sound button is on, with the
// user's music ducked under it; its ownership check ends it whatever exit event is missed. This file hands it the claim and the settle.
// A window with no video compositor builds no canvas player; the inspector's `mount media=fake` builds it over its fake backend
// (CanvasVideo.InspectBackend).

using FluentGpu.Media;

using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

public static class ShelfPreview
{
    /// <summary>The live tile ("item#song"), "" none.</summary>
    public static readonly Signal<string> Live = new("");

    /// <summary>The tile whose video is presented, "" none.</summary>
    public static readonly Signal<string> Showing = new("");

    static FluentGpu.Media.Windows.MfMediaPlayer? s_videoBackend;
    static IMediaBackend? s_builtOver;
    static MediaPlayer? s_video;
    static string s_openFile = "";
    static string s_waiting = "";   // a settled tile waiting for the previous tile's video to be released
    static ShelfSong s_song;
    static bool s_media, s_fake;

    /// <summary>The inspector's `mount media=real`: the headless test host may build real players (otherwise only the fake backend's).</summary>
    public static bool RealInTestHost { get; set; }

    /// <summary>The canvas player (null in a window that composites no video).</summary>
    public static MediaPlayer? Video => s_video;

    /// <summary>A shelf mounted: build the players once (the warm-up: MF startup and the engine pair are paid here, never on a hover), over
    /// their own Media Foundation backends (the canvas host's reasoning, Shell/Canvas.cs), or over the inspector's fake backend under
    /// `mount media=fake` (CanvasVideo.InspectBackend). A player opens nothing until a tile has settled under the pointer.</summary>
    public static void Warm(bool composites)
    {
        PreviewAudio.Warm();
        var fake = CanvasVideo.InspectBackend;
        if (s_media && ReferenceEquals(s_builtOver, fake)) return;
        // The headless inspector builds real Media Foundation players only when a mount asked for them (`media=real`: the hardware lane).
        if (fake is null && (!composites || Shell.IsTestHost && !RealInTestHost)) return;
        Leave();
        if (s_video is { } oldV) _ = oldV.DisposeAsync();
        s_builtOver = fake;
        s_fake = fake is not null;
        s_media = true;
        s_openFile = "";
        var v = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, fake ?? (s_videoBackend ??= new FluentGpu.Media.Windows.MfMediaPlayer())).Build();
        v.SetMuted(true);
        s_video = v;
    }

    /// <summary>An item opened: fetch its songs' canvas files in the background and ask for their track rows (the artist line).</summary>
    public static void Prepare(ShelfItem item) => Prepare(item.Songs);

    /// <summary>Songs a pointer may preview soon (a shelf item opened, a spotlight page shown): fetch their canvas files in the background and ask
    /// for their track rows (the artist line).</summary>
    public static void Prepare(ShelfSong[] songs)
    {
        Span<int> slots = stackalloc int[SpineShelfRules.Songs];
        int n = 0;
        foreach (var s in songs)
        {
            if (s.IsBlank) continue;
            if (s_media && !s_fake && s.Canvas is { } url) CanvasVideo.Files.Ensure(url, priority: false);
            if (s.Card.Kind == HomeCardKind.Track && n < slots.Length) slots[n++] = s.Card.Target.Slot;
        }
        if (n > 0) Entities.Ensure(Entities.Current.Tracks, slots[..n], (uint)TrackFields.Artists);
    }

    /// <summary>The pointer (or focus) came onto a tile.</summary>
    public static void Enter(string tile, in ShelfSong song)
    {
        if (song.IsBlank) return;
        if (Live.Peek() == tile) return;
        Leave();
        s_song = song;
        Live.Value = tile;
        if (s_media && !s_fake && song.Canvas is { } url) CanvasVideo.Files.Ensure(url, priority: true);
    }

    /// <summary>The live tile finished widening: the clip starts (an engine's open never lands on a frame of the widening), and the canvas is
    /// presented when its file is on disk (the shelf calls again when a file lands).</summary>
    public static void Settled(string tile)
    {
        if (Live.Peek() != tile) return;
        if (s_song.Clip is { } clip) PreviewAudio.Start(tile, clip);   // always (muted unless the sound button is on); a re-settle is a no-op
        if (Showing.Peek() == tile) return;
        if (!s_media || s_video is not { } player || s_song.Canvas is not { } url) return;
        if (Showing.Peek().Length > 0) { s_waiting = tile; return; }   // the tile left is still handing its video back (Release)
        // The fake backend (the inspector) opens anything: it takes the url itself; a real one plays the cached file.
        string file = s_fake ? url : CanvasVideo.Files.Ensure(url, priority: true);
        if (file.Length == 0) return;
        Showing.Value = tile;
        if (file != s_openFile)
        {
            s_openFile = file;
            OpenVideo(player, file);
        }
        else _ = player.PlayAsync();
    }

    /// <summary>The pointer left the live tile (or its card closed): the clip fades out (PreviewAudio), and the video stops now unless
    /// <paramref name="release"/> is false (the tile keeps it under its still's fade back in, then calls <see cref="Release"/>).</summary>
    public static void Leave(bool release = true)
    {
        string tile = Live.Peek();
        if (tile.Length == 0) return;
        s_waiting = "";
        if (release)
        {
            Showing.Value = "";
            if (s_video is { } v) _ = v.PauseAsync();
        }
        Live.Value = "";
        PreviewAudio.Leave(tile);   // ignored when the pointer already moved on to another tile (that one holds the claim)
    }

    /// <summary>The tile the pointer left has covered its video with its still again: the video element goes, and a tile that settled meanwhile
    /// gets the player now (else it pauses).</summary>
    public static void Release(string tile)
    {
        if (Showing.Peek() != tile) return;
        Showing.Value = "";
        string waiting = s_waiting;
        s_waiting = "";
        if (waiting.Length > 0 && waiting == Live.Peek()) Settled(waiting);
        else if (s_video is { } v) _ = v.PauseAsync();
    }

    static async void OpenVideo(MediaPlayer player, string file)
    {
        try
        {
            player.SetMuted(true);
            var source = s_fake ? MediaSource.FromUri(file) : MediaSource.FromFile(file);
            await player.OpenAsync(source.WithKind(MediaKind.MfVideoOrFile).Loop(), new MediaOpenOptions { StartPaused = true });
            // Back on the UI thread (the open may finish anywhere): play it only if its tile still shows it.
            CanvasVideo.Files.Post(() => { if (Showing.Peek().Length > 0 && s_openFile == file) _ = player.PlayAsync(); });
        }
        catch (Exception ex)
        {
            Log.Warn("home", "shelf.canvas open failed " + ex.GetType().Name + ": " + ex.Message);
            CanvasVideo.Files.Post(() => { if (s_openFile == file) s_openFile = ""; });
        }
    }
}
