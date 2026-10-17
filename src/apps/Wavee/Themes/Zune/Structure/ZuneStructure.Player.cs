// ── Themes/Zune/Structure/ZuneStructure.Player.cs — Z4a's registrar: the play bar, its parts and the panel's bodies (P5 Z4a) ─────────────
//
// The play bar (`Player.Bar`) and the transport pieces it shares with the stage's plate and the strip over a video (the 2-px seek line, the
// chapter ticks and tip, the scrub readout, the time labels), the device picker, the on-media strip, and the bodies the panel's frame (Z2a)
// places under its pivots: the now-playing hero and its style flyout, the lyrics header (no title, no close: the pivot head has them), the
// lyrics peek, "next up", the friends and the queue. Each replaces the stand-in's (Classic's) key by key.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterPlayer(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.PlayerBar, ZunePlayerBar.Template, "zune.player.bar");
        views.Add(ViewKeys.SeekRail, ZuneTransport.SeekRail, "zune.player.seek-rail");
        views.Add(ViewKeys.SeekChapters, ZuneTransport.SeekChapters, "zune.player.seek-chapters");
        views.Add(ViewKeys.ChapterTip, ZuneTransport.ChapterTip, "zune.player.chapter-tip");
        views.Add(ViewKeys.SeekReadout, ZuneTransport.SeekReadout, "zune.player.seek-readout");
        views.Add(ViewKeys.TimeLabel, ZuneTransport.TimeLabel, "zune.player.time-label");
        views.Add(ViewKeys.DevicePicker, ZuneDevicePicker.Template, "zune.player.device-picker");
        views.Add(ViewKeys.OnMediaTransport, ZuneOnMedia.Template, "zune.player.on-media-transport");
        views.Add(ViewKeys.NowPlayingHero, ZuneNowPlayingHero.Template, "zune.player.now-playing-hero");
        views.Add(ViewKeys.DeckStyleFlyout, ZuneNowPlayingHero.FlyoutTemplate, "zune.player.deck-style-flyout");
        views.Add(ViewKeys.LyricsPeek, ZuneLyricsPeek.Template, "zune.player.lyrics-peek");
        views.Add(ViewKeys.PanelFriends, ZuneFriends.Template, "zune.panel.friends");
        views.Add(ViewKeys.PanelLyricsHeader, ZuneLyricsHeader.Template, "zune.panel.lyrics-header");
        views.Add(ViewKeys.PanelNextUp, ZuneNextUp.Template, "zune.panel.next-up");
        views.Add(ViewKeys.QueuePanel, ZuneQueue.PanelTemplate, "zune.queue.panel");
    }
}
