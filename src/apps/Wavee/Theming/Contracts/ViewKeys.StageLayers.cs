// ── Theming/Contracts/ViewKeys.StageLayers.cs — the stage's layout layers, published as view keys (themes plan 6.0, T7a2) ────
//
// T7a1 published the stage's frame. T7a2 publishes the layers the four layouts are made of: the backdrop, the Artist layout's hero
// photo and its name, the face and its scrim, the spectrum, the now-playing card, the identity (the morphing cover and its title
// column) and the Artist pane. WHICH layout is drawn stays core (`Stage.Layout.ClassOf/Resolve`, `Stage.LayoutRules`, the allocator's
// authored extents, `StagePolicy`): a template draws the layer for the look the core decided, in every aspect class. The root mounts
// each of them in the order the stack has always had.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The stage's floor: the baked cover (the old one kept under the new), the base field and the scrim. Props: <see cref="StageBackdropProps"/>.</summary>
    public static readonly ViewKey<StageBackdropProps> StageBackdrop = new("Stage.Backdrop");

    /// <summary>The Artist layout's photo (a slow pan and zoom, a cross-fade on a new artist) and its two scrims. Props: <see cref="StageHeroProps"/>.</summary>
    public static readonly ViewKey<StageHeroProps> StageHero = new("Stage.Hero");

    /// <summary>The Artist layout's "NOW PLAYING" eyebrow and the huge artist name. Props: <see cref="StageArtistNameProps"/>.</summary>
    public static readonly ViewKey<StageArtistNameProps> StageArtistName = new("Stage.ArtistName");

    /// <summary>The chosen visualizer face, remounted per face and width bucket. Props: <see cref="StageLayerProps"/>.</summary>
    public static readonly ViewKey<StageLayerProps> StageFace = new("Stage.Face");

    /// <summary>The scrim above a backdrop face (Large art, Centered). Props: <see cref="StageFaceScrimProps"/>.</summary>
    public static readonly ViewKey<StageFaceScrimProps> StageFaceScrim = new("Stage.FaceScrim");

    /// <summary>The layouts' spectrum: a strip or a ring around the cover. Props: <see cref="StageLayerProps"/>.</summary>
    public static readonly ViewKey<StageLayerProps> StageSpectrum = new("Stage.Spectrum");

    /// <summary>The acrylic now-playing card behind the Card layout's thumb. Props: <see cref="StageNowPlayingCardProps"/>.</summary>
    public static readonly ViewKey<StageNowPlayingCardProps> StageNowPlayingCard = new("Stage.NowPlayingCard");

    /// <summary>The morphing cover and its title column (the layout switch's Bounds spring and the titles' Position spring). Props:
    /// <see cref="StageIdentityProps"/>.</summary>
    public static readonly ViewKey<StageIdentityProps> StageIdentity = new("Stage.Identity");

    /// <summary>The Artist pane of the stage's pane region: the header band, About, This song, Popular, Where people listen, Fans also
    /// like. Mounted by the core pane host under its KeepAlive. Props: <see cref="StageArtistPaneProps"/>.</summary>
    public static readonly ViewKey<StageArtistPaneProps> StageArtistPane = new("Stage.ArtistPane");
}
