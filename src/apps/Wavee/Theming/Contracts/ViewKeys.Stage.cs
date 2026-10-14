// ── Theming/Contracts/ViewKeys.Stage.cs — the Stage family of published view keys (themes plan 6.0, T7a1) ───────────────────
//
// T7a1 publishes the stage's frame: the root (the layer stack, the chrome placement, the hairline), the top bar, the transport
// card, the options popover, the visualizer gallery and the lyric caption. The four layouts, the hero and the artist pane are
// T7a2's, the lyrics pane T7b's, the queue pane T7d's (`Queue.StagePane`, in ViewKeys.Queue.cs). The core layers (the pane host, the caption host, the
// zero-size machinery) reach the root as builders (`StageLayers`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The fullscreen stage: the layer stack (backdrop, face, cover, pane, caption), where the chrome sits and how it comes
    /// and goes, the hairline while the chrome rests. Mounted by the frame inside the immersive layer's Show. Props:
    /// <see cref="StageRootProps"/>.</summary>
    public static readonly ViewKey<StageRootProps> StageRoot = new("Stage.Root");

    /// <summary>The stage's top bar: brand, the mode selector, the options and exit buttons. Mounted by the stage root. Props:
    /// <see cref="StageTopBarProps"/>.</summary>
    public static readonly ViewKey<StageTopBarProps> StageTopBar = new("Stage.TopBar");

    /// <summary>The stage's transport card: the seek row and the three clusters. Mounted by the stage root. Props:
    /// <see cref="StageTransportProps"/>.</summary>
    public static readonly ViewKey<StageTransportProps> StageTransport = new("Stage.Transport");

    /// <summary>The "Full screen" options popover (Visualizer and Layout tabs). Mounted by the stage's core host into the overlay
    /// layer. Props: <see cref="StageOptionsProps"/>.</summary>
    public static readonly ViewKey<StageOptionsProps> StageOptions = new("Stage.Options");

    /// <summary>The visualizer gallery pane (grouped tiles and the settings rows). Mounted by the stage root. Props:
    /// <see cref="StageGalleryProps"/>.</summary>
    public static readonly ViewKey<StageGalleryProps> StageGallery = new("Stage.Gallery");

    /// <summary>The lyric caption over the face: previous, active (karaoke wipe and bloom) and next line, or the break dots. Mounted
    /// by the core caption host, which keeps the playhead clock, the edge wake and the render-thread wipe. Props:
    /// <see cref="StageCaptionProps"/>.</summary>
    public static readonly ViewKey<StageCaptionProps> StageCaption = new("Stage.Caption");
}
