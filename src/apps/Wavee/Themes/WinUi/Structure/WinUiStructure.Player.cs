// ── Themes/WinUi/Structure/WinUiStructure.Player.cs — W4a's registrar: the play bar, its parts, the panel and its bodies (P6 W4a) ─────────
//
// The play bar (`Player.Bar`) and the transport pieces it shares with the panel's sheet, the stage and the strip over a video (the seek rail,
// the chapter ticks and tip, the scrub readout, the time labels), the device picker and the on-media strip; the panel's frame (`Shell.Panel`:
// the tabs, the dock / overlay crossfade, the Compact sheet, Huge's two panes) and the bodies it places under its tabs: the queue, the
// now-playing hero and its style flyout, the lyrics header, the lyrics peek, "next up" and friends. Numbers: `Rules\Tiers.Player.cs` (the
// levels and the panel per tier), `Rules\WinUiPlayerLayout.cs` and `Rules\WinUiPanelLayout.cs` (the parts).

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterPlayer(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.PlayerBar, WinUiPlayerBar.Template, "winui.player.bar");
        views.Add(ViewKeys.SeekRail, WinUiTransport.SeekRail, "winui.player.seek-rail");
        views.Add(ViewKeys.SeekChapters, WinUiTransport.SeekChapters, "winui.player.seek-chapters");
        views.Add(ViewKeys.ChapterTip, WinUiTransport.ChapterTip, "winui.player.chapter-tip");
        views.Add(ViewKeys.SeekReadout, WinUiTransport.SeekReadout, "winui.player.seek-readout");
        views.Add(ViewKeys.TimeLabel, WinUiTransport.TimeLabel, "winui.player.time-label");
        views.Add(ViewKeys.DevicePicker, WinUiDevicePicker.Template, "winui.player.device-picker");
        views.Add(ViewKeys.OnMediaTransport, WinUiOnMedia.Template, "winui.player.on-media-transport");
        views.Add(ViewKeys.ShellPanel, WinUiPanel.Template, "winui.shell.panel");
        views.Add(ViewKeys.NowPlayingHero, WinUiNowPlayingHero.Template, "winui.player.now-playing-hero");
        views.Add(ViewKeys.DeckStyleFlyout, WinUiNowPlayingHero.FlyoutTemplate, "winui.player.deck-style-flyout");
        views.Add(ViewKeys.LyricsPeek, WinUiLyricsPeek.Template, "winui.player.lyrics-peek");
        views.Add(ViewKeys.PanelFriends, WinUiFriends.Template, "winui.panel.friends");
        views.Add(ViewKeys.PanelLyricsHeader, WinUiLyricsHeader.Template, "winui.panel.lyrics-header");
        views.Add(ViewKeys.PanelNextUp, WinUiNextUp.Template, "winui.panel.next-up");
        views.Add(ViewKeys.QueuePanel, WinUiQueue.PanelTemplate, "winui.queue.panel");
    }
}
