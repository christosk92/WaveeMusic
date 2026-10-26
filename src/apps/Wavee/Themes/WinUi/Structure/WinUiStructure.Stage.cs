// ── Themes/WinUi/Structure/WinUiStructure.Stage.cs — W4b's registrar: the stage's frame, Card and Large art (P6 W4b) ─────────────────────
//
// The fullscreen stage's frame (root, top bar, transport card, options, gallery, caption, pane region) and its layout layers (backdrop, face
// host, face scrim, spectrum, now-playing card, identity): `Views\Stage\WinUiStage*.cs`, numbers in `Rules\Tiers.Stage.cs` and
// `Rules\WinUiStageGeometry*.cs`, the policy in `Rules\WinUiPolicies.Stage.cs`. The faces are core (Bloom by default in every theme). The
// Centered and Artist layouts' views, Stage.Hero / ArtistName / ArtistPane, Queue.StagePane, Lyrics.View and Video.* are W4c's
// (`RegisterStageMore`); until then they come from the stand-in, mounted in the slots WinUI's root keeps for them.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterStage(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.StageRoot, WinUiStageRoot.Template, "winui.stage.root");
        views.Add(ViewKeys.StageTopBar, WinUiStageTopBar.Template, "winui.stage.top-bar");
        views.Add(ViewKeys.StageTransport, WinUiStageTransport.Template, "winui.stage.transport");
        views.Add(ViewKeys.StageOptions, WinUiStageOptions.Template, "winui.stage.options");
        views.Add(ViewKeys.StageGallery, WinUiStageGallery.Template, "winui.stage.gallery");
        views.Add(ViewKeys.StageCaption, WinUiStageCaption.Template, "winui.stage.caption");
        views.Add(ViewKeys.StageBackdrop, WinUiStageBackdrop.Template, "winui.stage.backdrop");
        views.Add(ViewKeys.StageFace, WinUiStageFace.Template, "winui.stage.face");
        views.Add(ViewKeys.StageFaceScrim, WinUiStageFaceScrim.Template, "winui.stage.face-scrim");
        views.Add(ViewKeys.StageSpectrum, WinUiStageSpectrum.Template, "winui.stage.spectrum");
        views.Add(ViewKeys.StageNowPlayingCard, WinUiStageNowPlayingCard.Template, "winui.stage.now-playing-card");
        views.Add(ViewKeys.StageIdentity, WinUiStageIdentity.Template, "winui.stage.identity");
        views.Add(ViewKeys.StagePane, WinUiStagePane.Template, "winui.stage.pane");
    }
}
