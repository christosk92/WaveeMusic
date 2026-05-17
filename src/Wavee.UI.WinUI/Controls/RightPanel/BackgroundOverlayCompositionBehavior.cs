using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

/// <summary>
/// What the overlay should look like right now. Drives every visual's opacity
/// and colour without the parent needing to poke individual brushes.
/// </summary>
internal enum OverlayState
{
    /// <summary>All overlay sprites collapsed — chrome is hidden.</summary>
    Hidden,
    /// <summary>Full details-canvas treatment (tint + highlight + scrim + blends).</summary>
    DetailsCanvas,
    /// <summary>Embedded host — only the tab-content fade lit up, no panel scrim.</summary>
    EmbeddedTransparent,
}

/// <summary>
/// Owns the composition <see cref="SpriteVisual"/> stack that renders the
/// right-panel background chrome — tint, highlight gradient, top/bottom blend
/// gradients, scrim, dim — and the bottom-of-tab content fade (blur + gradient).
/// </summary>
/// <remarks>
/// <para>
/// Why a manager class rather than 14 individual fields on the parent: the
/// composition objects share lifetime, share a single host element, and need
/// to be torn down in one place when the panel unloads. Burying the
/// brush/visual graph here lets the parent treat the chrome as a single,
/// state-machine-shaped surface.
/// </para>
/// <para>
/// Lifetime: <see cref="Attach"/> in <c>Loaded</c>, <see cref="Detach"/> in
/// <c>Unloaded</c>. <see cref="ApplyState"/> and <see cref="ApplyColors"/> are
/// safe to call before <see cref="Attach"/> — they no-op until the
/// composition graph exists.
/// </para>
/// </remarks>
internal sealed class BackgroundOverlayCompositionBehavior
{
    private FrameworkElement? _backgroundHost;
    private FrameworkElement? _tabFadeHost;

    // ── Background overlay (panel-wide chrome over canvas media) ──
    private readonly List<CompositionObject> _backgroundCompositionObjects = [];
    private ContainerVisual? _backgroundContainer;
    private Compositor? _backgroundCompositor;

    private CompositionColorBrush? _backgroundTintBrush;
    private CompositionColorBrush? _backgroundNonDetailsDimBrush;
    private CompositionLinearGradientBrush? _backgroundHighlightBrush;
    private CompositionColorGradientStop? _backgroundHighlightStartStop;
    private CompositionColorGradientStop? _backgroundHighlightMidStop;
    private CompositionColorGradientStop? _backgroundHighlightEndStop;
    private CompositionLinearGradientBrush? _backgroundScrimBrush;
    private CompositionColorGradientStop? _backgroundScrimTopStop;
    private CompositionColorGradientStop? _backgroundScrimMidStop;
    private CompositionColorGradientStop? _backgroundScrimBottomStop;
    private CompositionLinearGradientBrush? _backgroundBottomBlendBrush;
    private CompositionColorGradientStop? _backgroundBottomBlendTopStop;
    private CompositionColorGradientStop? _backgroundBottomBlendMidStop;
    private CompositionColorGradientStop? _backgroundBottomBlendLowerMidStop;
    private CompositionColorGradientStop? _backgroundBottomBlendBottomStop;
    private CompositionLinearGradientBrush? _backgroundTopBlendBrush;
    private CompositionColorGradientStop? _backgroundTopBlendTopStop;
    private CompositionColorGradientStop? _backgroundTopBlendMidStop;
    private CompositionColorGradientStop? _backgroundTopBlendLowerMidStop;
    private CompositionColorGradientStop? _backgroundTopBlendBottomStop;
    private SpriteVisual? _backgroundTintVisual;
    private SpriteVisual? _backgroundHighlightVisual;
    private SpriteVisual? _backgroundScrimVisual;
    private SpriteVisual? _backgroundNonDetailsDimVisual;
    private SpriteVisual? _backgroundBottomBlendVisual;
    private SpriteVisual? _backgroundTopBlendVisual;

    // ── Tab-content bottom fade ──
    private readonly List<CompositionObject> _tabFadeCompositionObjects = [];
    private Compositor? _tabFadeCompositor;
    private ContainerVisual? _tabFadeContainer;
    private CompositionEffectBrush? _tabBlurBrush;
    private SpriteVisual? _tabBlurVisual;
    private SpriteVisual? _tabFadeVisual;
    private CompositionLinearGradientBrush? _tabFadeBrush;
    private CompositionColorGradientStop? _tabFadeStop0;
    private CompositionColorGradientStop? _tabFadeStop1;
    private CompositionColorGradientStop? _tabFadeStop2;
    private CompositionColorGradientStop? _tabFadeStop3;

    public bool IsAttached => _backgroundContainer != null || _tabFadeContainer != null;

    /// <summary>
    /// Bind the manager to its two hosts and build the composition stacks.
    /// Idempotent: the per-host build happens once even on repeat calls.
    /// </summary>
    public void Attach(FrameworkElement? backgroundHost, FrameworkElement? tabFadeHost)
    {
        _backgroundHost = backgroundHost;
        _tabFadeHost = tabFadeHost;
        EnsureBackgroundOverlayComposition();
        EnsureTabContentFadeComposition();
    }

    /// <summary>
    /// Dispose every composition object and break the parent associations.
    /// Safe to call multiple times.
    /// </summary>
    public void Detach()
    {
        TeardownBackgroundOverlayComposition();
        TeardownTabContentFadeComposition();
        _backgroundHost = null;
        _tabFadeHost = null;
    }

    /// <summary>
    /// Master visibility on the background-overlay container — used to fade
    /// the entire chrome out when media isn't resolved.
    /// </summary>
    public void SetBackgroundContainerOpacity(float opacity)
    {
        if (_backgroundContainer != null)
            _backgroundContainer.Opacity = opacity;
    }

    /// <summary>
    /// Push new gradient/tint colours into the background overlay stops. Inputs
    /// are precomputed by the parent via <see cref="RightPanelThemeResolver"/>
    /// so this method is pure plumbing.
    /// </summary>
    public void ApplyBackgroundColors(BackgroundOverlayColors colors)
    {
        if (_backgroundTintBrush == null) return;

        _backgroundTintBrush.Color = colors.TintColor;
        _backgroundNonDetailsDimBrush!.Color = colors.NonDetailsDimColor;

        _backgroundHighlightStartStop!.Color = colors.HighlightStart;
        _backgroundHighlightMidStop!.Color = colors.HighlightMid;
        _backgroundHighlightEndStop!.Color = colors.HighlightEnd;

        _backgroundScrimTopStop!.Color = colors.ScrimTop;
        _backgroundScrimMidStop!.Color = colors.ScrimMid;
        _backgroundScrimBottomStop!.Color = colors.ScrimBottom;

        var bottom = colors.BottomBlendColor;
        _backgroundBottomBlendTopStop!.Color = Color.FromArgb(0, bottom.R, bottom.G, bottom.B);
        _backgroundBottomBlendMidStop!.Color = Color.FromArgb(20, bottom.R, bottom.G, bottom.B);
        _backgroundBottomBlendLowerMidStop!.Color = Color.FromArgb(86, bottom.R, bottom.G, bottom.B);
        _backgroundBottomBlendBottomStop!.Color = Color.FromArgb(255, bottom.R, bottom.G, bottom.B);

        _backgroundTopBlendTopStop!.Color = Color.FromArgb(255, bottom.R, bottom.G, bottom.B);
        _backgroundTopBlendMidStop!.Color = Color.FromArgb(86, bottom.R, bottom.G, bottom.B);
        _backgroundTopBlendLowerMidStop!.Color = Color.FromArgb(20, bottom.R, bottom.G, bottom.B);
        _backgroundTopBlendBottomStop!.Color = Color.FromArgb(0, bottom.R, bottom.G, bottom.B);
    }

    /// <summary>
    /// Update each background visual's opacity according to the requested
    /// <paramref name="state"/>. <see cref="OverlayState.Hidden"/> drops them
    /// all to zero; <see cref="OverlayState.DetailsCanvas"/> lights the full
    /// stack at the canonical levels; <see cref="OverlayState.EmbeddedTransparent"/>
    /// suppresses every background overlay since the host owns the chrome.
    /// </summary>
    public void ApplyState(OverlayState state)
    {
        if (_backgroundContainer == null) return;

        if (state == OverlayState.EmbeddedTransparent)
        {
            if (_backgroundTintVisual != null) _backgroundTintVisual.Opacity = 0f;
            if (_backgroundHighlightVisual != null) _backgroundHighlightVisual.Opacity = 0f;
            if (_backgroundScrimVisual != null) _backgroundScrimVisual.Opacity = 0f;
            if (_backgroundNonDetailsDimVisual != null) _backgroundNonDetailsDimVisual.Opacity = 0f;
            if (_backgroundBottomBlendVisual != null) _backgroundBottomBlendVisual.Opacity = 0f;
            if (_backgroundTopBlendVisual != null) _backgroundTopBlendVisual.Opacity = 0f;
            return;
        }

        var showDetailsCanvasChrome = state == OverlayState.DetailsCanvas;

        if (_backgroundTintVisual != null)
            _backgroundTintVisual.Opacity = showDetailsCanvasChrome ? 0.10f : 0f;
        if (_backgroundHighlightVisual != null)
            _backgroundHighlightVisual.Opacity = showDetailsCanvasChrome ? 0.52f : 0f;
        if (_backgroundScrimVisual != null)
            _backgroundScrimVisual.Opacity = showDetailsCanvasChrome ? 0.74f : 0f;
        if (_backgroundBottomBlendVisual != null)
            _backgroundBottomBlendVisual.Opacity = showDetailsCanvasChrome ? 0.88f : 0f;
        if (_backgroundTopBlendVisual != null)
            _backgroundTopBlendVisual.Opacity = showDetailsCanvasChrome ? 0.64f : 0f;
        if (_backgroundNonDetailsDimVisual != null)
            _backgroundNonDetailsDimVisual.Opacity = 0f;
    }

    /// <summary>
    /// Set the four-stop gradient that fades the bottom of the tab content
    /// into the panel background. <paramref name="isEmbeddedChromeTransparent"/>
    /// switches to a softer envelope and lights the underlying acrylic-like
    /// backdrop blur so embedded hosts get a smoother handoff.
    /// </summary>
    public void ApplyTabFadeColor(Color target, bool isEmbeddedChromeTransparent)
    {
        if (_tabFadeStop0 == null || _tabFadeStop1 == null
            || _tabFadeStop2 == null || _tabFadeStop3 == null)
            return;

        if (_tabBlurVisual != null)
            _tabBlurVisual.Opacity = isEmbeddedChromeTransparent ? 0.28f : 0f;

        // Carry the target RGB on every stop so the gradient interpolation stays
        // in the correct hue rather than fading through neutral gray.
        if (isEmbeddedChromeTransparent)
        {
            _tabFadeStop0.Color = Color.FromArgb(0, target.R, target.G, target.B);
            _tabFadeStop1.Color = Color.FromArgb(18, target.R, target.G, target.B);
            _tabFadeStop2.Color = Color.FromArgb(92, target.R, target.G, target.B);
            _tabFadeStop3.Color = Color.FromArgb(185, target.R, target.G, target.B);
            return;
        }

        _tabFadeStop0.Color = Color.FromArgb(0, target.R, target.G, target.B);
        _tabFadeStop1.Color = Color.FromArgb(40, target.R, target.G, target.B);
        _tabFadeStop2.Color = Color.FromArgb(190, target.R, target.G, target.B);
        _tabFadeStop3.Color = Color.FromArgb(255, target.R, target.G, target.B);
    }

    private void EnsureBackgroundOverlayComposition()
    {
        if (_backgroundContainer != null || _backgroundHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(_backgroundHost);
        _backgroundCompositor = hostVisual.Compositor;

        _backgroundContainer = TrackBackground(_backgroundCompositor.CreateContainerVisual());
        _backgroundContainer.RelativeSizeAdjustment = Vector2.One;
        _backgroundContainer.Opacity = 0f;

        _backgroundTintBrush = TrackBackground(_backgroundCompositor.CreateColorBrush(Colors.Transparent));
        _backgroundTintVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundTintVisual.Brush = _backgroundTintBrush;
        _backgroundTintVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundContainer.Children.InsertAtBottom(_backgroundTintVisual);

        _backgroundHighlightBrush = TrackBackground(_backgroundCompositor.CreateLinearGradientBrush());
        _backgroundHighlightBrush.StartPoint = new Vector2(0.08f, 0f);
        _backgroundHighlightBrush.EndPoint = new Vector2(0.82f, 0.5f);
        _backgroundHighlightStartStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundHighlightMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.44f, Colors.Transparent));
        _backgroundHighlightEndStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightStartStop);
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightMidStop);
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightEndStop);
        _backgroundHighlightVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundHighlightVisual.Brush = _backgroundHighlightBrush;
        _backgroundHighlightVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundContainer.Children.InsertAtTop(_backgroundHighlightVisual);

        _backgroundScrimBrush = TrackBackground(_backgroundCompositor.CreateLinearGradientBrush());
        _backgroundScrimBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundScrimBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundScrimTopStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundScrimMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.42f, Colors.Transparent));
        _backgroundScrimBottomStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimTopStop);
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimMidStop);
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimBottomStop);
        _backgroundScrimVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundScrimVisual.Brush = _backgroundScrimBrush;
        _backgroundScrimVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundContainer.Children.InsertAtTop(_backgroundScrimVisual);

        _backgroundNonDetailsDimBrush = TrackBackground(_backgroundCompositor.CreateColorBrush(Colors.Transparent));
        _backgroundNonDetailsDimVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundNonDetailsDimVisual.Brush = _backgroundNonDetailsDimBrush;
        _backgroundNonDetailsDimVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundContainer.Children.InsertAtTop(_backgroundNonDetailsDimVisual);

        _backgroundBottomBlendBrush = TrackBackground(_backgroundCompositor.CreateLinearGradientBrush());
        _backgroundBottomBlendBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundBottomBlendBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundBottomBlendTopStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundBottomBlendMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.18f, Colors.Transparent));
        _backgroundBottomBlendLowerMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.62f, Colors.Transparent));
        _backgroundBottomBlendBottomStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendTopStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendMidStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendLowerMidStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendBottomStop);
        _backgroundBottomBlendVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundBottomBlendVisual.Brush = _backgroundBottomBlendBrush;
        _backgroundBottomBlendVisual.RelativeSizeAdjustment = new Vector2(1f, 0.42f);
        _backgroundBottomBlendVisual.RelativeOffsetAdjustment = new Vector3(0f, 0.58f, 0f);
        _backgroundContainer.Children.InsertAtTop(_backgroundBottomBlendVisual);

        _backgroundTopBlendBrush = TrackBackground(_backgroundCompositor.CreateLinearGradientBrush());
        _backgroundTopBlendBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundTopBlendBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundTopBlendTopStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundTopBlendMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.38f, Colors.Transparent));
        _backgroundTopBlendLowerMidStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(0.82f, Colors.Transparent));
        _backgroundTopBlendBottomStop = TrackBackground(_backgroundCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendTopStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendMidStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendLowerMidStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendBottomStop);
        _backgroundTopBlendVisual = TrackBackground(_backgroundCompositor.CreateSpriteVisual());
        _backgroundTopBlendVisual.Brush = _backgroundTopBlendBrush;
        _backgroundTopBlendVisual.RelativeSizeAdjustment = new Vector2(1f, 0.35f);
        _backgroundContainer.Children.InsertAtTop(_backgroundTopBlendVisual);

        ElementCompositionPreview.SetElementChildVisual(_backgroundHost, _backgroundContainer);
    }

    private void TeardownBackgroundOverlayComposition()
    {
        if (_backgroundHost != null)
            ElementCompositionPreview.SetElementChildVisual(_backgroundHost, null);

        for (int i = _backgroundCompositionObjects.Count - 1; i >= 0; i--)
            _backgroundCompositionObjects[i].Dispose();
        _backgroundCompositionObjects.Clear();

        _backgroundContainer = null;
        _backgroundCompositor = null;
        _backgroundTintBrush = null;
        _backgroundNonDetailsDimBrush = null;
        _backgroundHighlightBrush = null;
        _backgroundHighlightStartStop = null;
        _backgroundHighlightMidStop = null;
        _backgroundHighlightEndStop = null;
        _backgroundScrimBrush = null;
        _backgroundScrimTopStop = null;
        _backgroundScrimMidStop = null;
        _backgroundScrimBottomStop = null;
        _backgroundBottomBlendBrush = null;
        _backgroundBottomBlendTopStop = null;
        _backgroundBottomBlendMidStop = null;
        _backgroundBottomBlendLowerMidStop = null;
        _backgroundBottomBlendBottomStop = null;
        _backgroundTopBlendBrush = null;
        _backgroundTopBlendTopStop = null;
        _backgroundTopBlendMidStop = null;
        _backgroundTopBlendLowerMidStop = null;
        _backgroundTopBlendBottomStop = null;
        _backgroundTintVisual = null;
        _backgroundHighlightVisual = null;
        _backgroundScrimVisual = null;
        _backgroundNonDetailsDimVisual = null;
        _backgroundBottomBlendVisual = null;
        _backgroundTopBlendVisual = null;
    }

    // ── Tab content bottom fade ──
    // A single composition-backed vertical gradient that overlays all right-panel tabs
    // and fades the scrolling content into the panel background, so the last row bleeds
    // cleanly into the player bar below.
    private void EnsureTabContentFadeComposition()
    {
        if (_tabFadeVisual != null || _tabFadeHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(_tabFadeHost);
        _tabFadeCompositor = hostVisual.Compositor;

        _tabFadeContainer = TrackTabFade(_tabFadeCompositor.CreateContainerVisual());
        _tabFadeContainer.RelativeSizeAdjustment = Vector2.One;

        var backdropBrush = TrackTabFade(_tabFadeCompositor.CreateBackdropBrush());
        using var blurEffect = new GaussianBlurEffect
        {
            Name = "TabContentBackdropBlur",
            Source = new CompositionEffectSourceParameter("Backdrop"),
            BlurAmount = 14f,
            BorderMode = EffectBorderMode.Hard
        };
        var blurFactory = TrackTabFade(_tabFadeCompositor.CreateEffectFactory(blurEffect));
        _tabBlurBrush = TrackTabFade(blurFactory.CreateBrush());
        _tabBlurBrush.SetSourceParameter("Backdrop", backdropBrush);

        _tabBlurVisual = TrackTabFade(_tabFadeCompositor.CreateSpriteVisual());
        _tabBlurVisual.Brush = _tabBlurBrush;
        _tabBlurVisual.RelativeSizeAdjustment = Vector2.One;
        _tabFadeContainer.Children.InsertAtBottom(_tabBlurVisual);

        _tabFadeBrush = TrackTabFade(_tabFadeCompositor.CreateLinearGradientBrush());
        _tabFadeBrush.StartPoint = new Vector2(0.5f, 0f);
        _tabFadeBrush.EndPoint = new Vector2(0.5f, 1f);

        // 4-stop gradient — same cadence as _backgroundBottomBlendBrush for visual consistency.
        _tabFadeStop0 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.00f, Colors.Transparent));
        _tabFadeStop1 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.35f, Colors.Transparent));
        _tabFadeStop2 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.72f, Colors.Transparent));
        _tabFadeStop3 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(1.00f, Colors.Transparent));
        _tabFadeBrush.ColorStops.Add(_tabFadeStop0);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop1);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop2);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop3);

        _tabFadeVisual = TrackTabFade(_tabFadeCompositor.CreateSpriteVisual());
        _tabFadeVisual.Brush = _tabFadeBrush;
        _tabFadeVisual.RelativeSizeAdjustment = Vector2.One;
        _tabFadeContainer.Children.InsertAtTop(_tabFadeVisual);

        ElementCompositionPreview.SetElementChildVisual(_tabFadeHost, _tabFadeContainer);
    }

    private void TeardownTabContentFadeComposition()
    {
        if (_tabFadeHost != null)
            ElementCompositionPreview.SetElementChildVisual(_tabFadeHost, null);

        for (int i = _tabFadeCompositionObjects.Count - 1; i >= 0; i--)
            _tabFadeCompositionObjects[i].Dispose();
        _tabFadeCompositionObjects.Clear();

        _tabFadeCompositor = null;
        _tabFadeContainer = null;
        _tabBlurBrush = null;
        _tabBlurVisual = null;
        _tabFadeBrush = null;
        _tabFadeStop0 = null;
        _tabFadeStop1 = null;
        _tabFadeStop2 = null;
        _tabFadeStop3 = null;
        _tabFadeVisual = null;
    }

    private T TrackBackground<T>(T obj) where T : CompositionObject
    {
        _backgroundCompositionObjects.Add(obj);
        return obj;
    }

    private T TrackTabFade<T>(T obj) where T : CompositionObject
    {
        _tabFadeCompositionObjects.Add(obj);
        return obj;
    }
}

/// <summary>
/// All colours the background overlay needs in one record so the parent can
/// push the whole palette through <see cref="BackgroundOverlayCompositionBehavior.ApplyBackgroundColors"/>
/// in a single call.
/// </summary>
internal readonly record struct BackgroundOverlayColors(
    Color TintColor,
    Color NonDetailsDimColor,
    Color HighlightStart,
    Color HighlightMid,
    Color HighlightEnd,
    Color ScrimTop,
    Color ScrimMid,
    Color ScrimBottom,
    Color BottomBlendColor);
