// ── Home/SpineShelf/ShelfPreview.cs — the Spine Shelf's hover media: ONE canvas player, ONE clip player, one policy ─────────────────────────
//
//   Live      the tile the pointer (or focus) is on, "<item key>#<song>" ("" none). Set at once on enter; the tile widens on it.
//   Showing   the tile whose canvas video is presented. Set when the tile has finished widening AND its canvas file is on disk: the
//             engine draws video in a DirectComposition hole that cannot follow a moving clip (Shell/Video.UI.cs rule 1), so the video
//             takes over the still only on a settled tile, and leaves first (Showing clears before the tile narrows).
//
// Only one video decodes: the one player is re-opened per hover. The player is built once, the first time a shelf mounts in a window
// that composites video (MF startup and the engine pair are paid there, never on a hover), over its own MF backend as the canvas host's
// is (Shell/Canvas.cs). Opening a card asks for its five canvas files (CanvasVideo.Files, background), so a hover opens a local file.
//
// The clip (owner call, scratch/pins/spine-shelf-tech.md): only when nothing plays (Playback.IsPlaying, which follows Connect too), at
// ClipVolume × the user's volume, on its own MF engine. It never calls the player, the queue or Connect. A window with no video
// compositor, and the headless inspector (Shell.IsTestHost), builds neither player: the hover state still runs, so the inspector proves it without media.

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

    /// <summary>Whether the live tile's clip is playing (diagnostics, the inspector).</summary>
    public static readonly Signal<bool> ClipPlaying = new(false);

    static FluentGpu.Media.Windows.MfMediaPlayer? s_videoBackend, s_clipBackend;
    static MediaPlayer? s_video, s_clip;
    static string s_openFile = "";
    static ShelfSong s_song;
    static bool s_media;

    /// <summary>The canvas player (null in a window that composites no video).</summary>
    public static MediaPlayer? Video => s_video;

    /// <summary>A shelf mounted in a window that composites video: build the players once (the warm-up).</summary>
    public static void Warm(bool composites)
    {
        if (!composites || s_media) return;
        s_media = true;
        var v = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, s_videoBackend ??= new FluentGpu.Media.Windows.MfMediaPlayer()).Build();
        v.SetMuted(true);
        s_video = v;
        s_clip = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, s_clipBackend ??= new FluentGpu.Media.Windows.MfMediaPlayer()).Build();
    }

    /// <summary>An item opened: fetch its songs' canvas files in the background and ask for their track rows (the artist line).</summary>
    public static void Prepare(ShelfItem item)
    {
        Span<int> slots = stackalloc int[SpineShelfRules.Songs];
        int n = 0;
        foreach (var s in item.Songs)
        {
            if (s.IsBlank) continue;
            if (s_media && s.Canvas is { } url) CanvasVideo.Files.Ensure(url, priority: false);
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
        if (!s_media) return;
        if (song.Canvas is { } url) CanvasVideo.Files.Ensure(url, priority: true);
        if (song.Clip is { } clip && !Playback.IsPlaying.Peek() && s_clip is { } player)
        {
            float volume = Playback.Muted.Peek() ? 0f : Playback.Volume.Peek() * SpineShelfRules.ClipVolume;
            player.SetVolume(volume);
            OpenClip(player, clip);
        }
    }

    /// <summary>The live tile finished widening: present its canvas when its file is on disk (the shelf calls again when a file lands).</summary>
    public static void Settled(string tile)
    {
        if (Live.Peek() != tile || Showing.Peek() == tile) return;
        if (!s_media || s_video is not { } player || s_song.Canvas is not { } url) return;
        string file = CanvasVideo.Files.Ensure(url, priority: true);
        if (file.Length == 0) return;
        Showing.Value = tile;
        if (file != s_openFile)
        {
            s_openFile = file;
            OpenVideo(player, file);
        }
        else _ = player.PlayAsync();
    }

    /// <summary>The pointer left the live tile (or its card closed): the video and the clip stop now.</summary>
    public static void Leave()
    {
        if (Live.Peek().Length == 0) return;
        Showing.Value = "";
        Live.Value = "";
        if (s_video is { } v) _ = v.PauseAsync();
        if (s_clip is { } c && ClipPlaying.Peek()) c.Stop();
        ClipPlaying.Value = false;
    }

    static async void OpenVideo(MediaPlayer player, string file)
    {
        try
        {
            player.SetMuted(true);
            await player.OpenAsync(MediaSource.FromFile(file).Loop(), new MediaOpenOptions { StartPaused = true });
            // Back on the UI thread (the open may finish anywhere): play it only if its tile still shows it.
            CanvasVideo.Files.Post(() => { if (Showing.Peek().Length > 0 && s_openFile == file) _ = player.PlayAsync(); });
        }
        catch (Exception ex)
        {
            Log.Warn("home", "shelf.canvas open failed " + ex.GetType().Name + ": " + ex.Message);
            CanvasVideo.Files.Post(() => { if (s_openFile == file) s_openFile = ""; });
        }
    }

    static async void OpenClip(MediaPlayer player, string url)
    {
        ClipPlaying.Value = true;
        try
        {
            await player.OpenAsync(MediaSource.FromUri(url), new MediaOpenOptions { StartPaused = false });
        }
        catch (Exception ex)
        {
            Log.Warn("home", "shelf.clip open failed " + ex.GetType().Name + ": " + ex.Message);
            CanvasVideo.Files.Post(static () => ClipPlaying.Value = false);
        }
    }
}
