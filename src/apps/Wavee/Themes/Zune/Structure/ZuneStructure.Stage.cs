// ── Themes/Zune/Structure/ZuneStructure.Stage.cs — Z4b's registrar: the stage's frame, Card and Large art (P5 Z4b) ─────────────────
//
// The fullscreen stage's frame (root, top bar, transport plate, options, gallery, caption, pane region) and its layout layers (backdrop,
// face host, face scrim, spectrum, now-playing card, identity). The faces themselves are core (owner Q21, NQ3: Bloom is the default face
// in every theme): Zune draws the frame around them. Stage.Hero, Stage.ArtistName, Stage.ArtistPane, Queue.StagePane, Lyrics.View and
// Video.* are Z4c's (`RegisterStageMore`); until then they come from the stand-in, mounted in the slots Zune's root keeps for them.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterStage(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.StageRoot, ZuneStageRoot.Template, "zune.stage.root");
        views.Add(ViewKeys.StageTopBar, ZuneStageTopBar.Template, "zune.stage.top-bar");
        views.Add(ViewKeys.StageTransport, ZuneStageTransport.Template, "zune.stage.transport");
        views.Add(ViewKeys.StageOptions, ZuneStageOptions.Template, "zune.stage.options");
        views.Add(ViewKeys.StageGallery, ZuneStageGallery.Template, "zune.stage.gallery");
        views.Add(ViewKeys.StageCaption, ZuneStageCaption.Template, "zune.stage.caption");
        views.Add(ViewKeys.StageBackdrop, ZuneStageBackdrop.Template, "zune.stage.backdrop");
        views.Add(ViewKeys.StageFace, ZuneStageFace.Template, "zune.stage.face");
        views.Add(ViewKeys.StageFaceScrim, ZuneStageFaceScrim.Template, "zune.stage.face-scrim");
        views.Add(ViewKeys.StageSpectrum, ZuneStageSpectrum.Template, "zune.stage.spectrum");
        views.Add(ViewKeys.StageNowPlayingCard, ZuneStageNowPlayingCard.Template, "zune.stage.now-playing-card");
        views.Add(ViewKeys.StageIdentity, ZuneStageIdentity.Template, "zune.stage.identity");
        views.Add(ViewKeys.StagePane, ZuneStagePane.Template, "zune.stage.pane");
        // Video mode (Cinema and Theatre): the chrome over the picture (mounted by the core above the video hole), the "up next" strip over the
        // picture and the end-of-video card. `ZuneStageTiers.Policy` states VideoStage, so F11 and "video in fullscreen" open the stage on the picture.
        views.Add(ViewKeys.StagePictureChrome, ZuneStagePictureChrome.Template, "zune.stage.picture-chrome");
        views.Add(ViewKeys.QueueStageShelf, ZuneStageShelf.Template, "zune.queue.stage-shelf");
        views.Add(ViewKeys.StageEndCard, ZuneStageEndCard.Template, "zune.stage.end-card");
    }
}
