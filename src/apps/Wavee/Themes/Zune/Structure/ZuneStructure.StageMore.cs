// ── Themes/Zune/Structure/ZuneStructure.StageMore.cs — Z4c's registrar: the rest of the stage, the lyrics view and video (P5 Z4c) ────
//
// The keys the stage's frame (Z4b, `ZuneStructure.Stage.cs`) left slots for, plus the lyrics view and the six video surfaces:
//
//   Stage.Hero        the Artist layout's photo (Z-NowPlaying-Artist): the stage's Ken Burns kept whole, Zune's scrims
//   Stage.ArtistName  its huge name, the display face at 300, bottom-anchored above the identity row
//   Stage.ArtistPane  the Artist pivot's pane: "about the artist" as the board has it, the other sections built from Zune's parts
//   Queue.StagePane   the Up next column (Z-Queue-Page): flat 60 rows, display-face numbers, section headings with a quiet word
//   Lyrics.View       the lyrics reading surface (Z-Lyrics): the accent "resync" pill, the accent dots, the video note; the lines are Z3's
//   Video.Docked      the docked card (Z-Mini): the hollow reservation, and the watch page's frame
//   Video.Pip         the in-window overlay: the docked strip's words, the floating mini player's chrome and resize zones
//   Video.PopOut      the detached window's content and its title band
//   Video.Fullscreen  the fullscreen bar (Z-Video): "video · full screen", the title, "exit full screen"
//   Video.JoinPoster  the "no picture yet" poster and its notice
//   Video.PlacementMenu  the placement flyout's presenter (the plate is Zune's menu style)
//
// Each registration wins over Classic's stand-in key by key (`ZuneStructure.Register` copies only the keys Zune does not register). The faces
// stay core (owner Q21, NQ3: Bloom is the default face in every theme); the media pipeline, the lyrics clock and the queue lane stay core.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterStageMore(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.StageHero, ZuneStageHero.Template, "zune.stage.hero");
        views.Add(ViewKeys.StageArtistName, ZuneStageArtistName.Template, "zune.stage.artist-name");
        views.Add(ViewKeys.StageArtistPane, ZuneStageArtistPane.Template, "zune.stage.artist-pane");
        views.Add(ViewKeys.QueueStagePane, ZuneStageUpNext.Template, "zune.queue.stage-pane");
        views.Add(ViewKeys.LyricsView, ZuneStageLyrics.Template, "zune.lyrics.view");
        views.Add(ViewKeys.VideoDocked, ZuneStageVideoDocked.Template, "zune.video.docked");
        views.Add(ViewKeys.VideoPip, ZuneStageVideoPip.Template, "zune.video.pip");
        views.Add(ViewKeys.VideoPopOut, ZuneStageVideoPopOut.Template, "zune.video.pop-out");
        views.Add(ViewKeys.VideoFullscreen, ZuneStageVideoFullscreen.Template, "zune.video.fullscreen");
        views.Add(ViewKeys.VideoJoinPoster, ZuneStageVideoPoster.Template, "zune.video.join-poster");
        views.Add(ViewKeys.VideoPlacementMenu, ZuneStageVideoPlacementMenu.Template, "zune.video.placement-menu");
    }
}
