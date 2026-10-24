// ── Themes/WinUi/Structure/WinUiStructure.StageMore.cs — W4c's registrar (the Centered and Artist layouts, lyrics, the stage queue, video) ─────────
//
// So far only the in-window video overlay is WinUI's own (audit C0, W-Video): `Views\Video\WinUiVideoPip.cs`, numbers in `Rules\WinUiVideoRules.cs`.
// Every other key of the stage's second half (Stage.Hero / ArtistName / ArtistPane, Queue.StagePane, Lyrics.View, the other Video.* keys) still comes
// from the stand-in, mounted in the slots WinUI's root keeps for them.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterStageMore(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.VideoPip, WinUiVideoPip.Template, "winui.video.pip");
    }
}
