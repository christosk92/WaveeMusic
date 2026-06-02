using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.ImageEditor;

/// <summary>
/// Custom square-image reframe editor (our own crop tool, not ImageCropper). A big Win2D scene
/// renders the photo plus a live blur/solid padding fill wherever the crop window falls outside
/// it; a resizable square crop frame with corner + edge handles and direction-aware cursors sits
/// on top. WYSIWYG — the crop frame interior is the result; on confirm it renders to an
/// <see cref="ImageReframeOptions.OutputSide"/>² JPEG ≤256 KB via <see cref="PlaylistCoverHelper"/>.
/// </summary>
public sealed partial class ImageReframeDialog : ContentDialog
{
    private const double ViewportSide = 460.0;   // must match the <Grid x:Name="Viewport"> size
    private const double MinFrame = 80.0;

    private enum FillKind { Blur, Solid }

    private static readonly InputCursor CursorNwse = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly InputCursor CursorNesw = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly InputCursor CursorNs = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private static readonly InputCursor CursorWe = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor CursorMove = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    private readonly SoftwareBitmap _softwareBitmap;
    private readonly ImageReframeOptions _options;
    private readonly Border[] _swatches;
    private readonly SolidColorBrush _swatchSelected;
    // Opaque mid-grey ring so every chip — including the white one on a light
    // dialog and the black one on a dark dialog — has a visible outline.
    private readonly SolidColorBrush _swatchUnselected = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    private FrameworkElement? _themeRoot;

    private CanvasBitmap? _canvasBitmap;
    private double _nw, _nh;
    private double _scale, _icx, _icy;           // photo scale + center (viewport coords)
    private double _fx, _fy, _fs;                // crop frame (square) in viewport coords
    private double _fitScale, _coverScale, _minScale, _maxScale;
    private double[] _snapScales = Array.Empty<double>();  // magnetic zoom detents (viewport scale)
    private bool _initialized;
    private bool _cursorsAssigned;
    private bool _suppressSlider;
    private bool _suppressColorPicker;
    private FillKind _fill = FillKind.Blur;
    private Color _solidColor = Microsoft.UI.Colors.Black;
    private byte[]? _result;

    private ImageReframeDialog(SoftwareBitmap bitmap, ImageReframeOptions options)
    {
        _softwareBitmap = bitmap;
        _options = options;
        InitializeComponent();

        Title = options.Title;
        PrimaryButtonText = options.PrimaryButtonText;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var accentValue) && accentValue is Color c
            ? c
            : Microsoft.UI.Colors.DodgerBlue;
        _swatchSelected = new SolidColorBrush(accent);
        _swatches = new[] { Swatch0, Swatch1, Swatch2, Swatch3, SwatchCustom };
        SelectSwatch(Swatch0);

        foreach (var t in Handles())
        {
            t.DragStarted += Handle_DragStarted;
            t.DragDelta += Handle_DragDelta;
            t.DragCompleted += Handle_DragCompleted;
        }

        PrimaryButtonClick += OnPrimaryButtonClick;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public static async Task<byte[]?> ShowAsync(XamlRoot xamlRoot, StorageFile file, ImageReframeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(file);
        options ??= new ImageReframeOptions();

        var bitmap = await DecodeOrientedAsync(file);

        var dialog = new ImageReframeDialog(bitmap, options)
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ResolveTheme(xamlRoot),
            Style = Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style)
                    && style is Style contentDialogStyle
                ? contentDialogStyle
                : null,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog._result : null;
    }

    private IEnumerable<Thumb> Handles()
        => new[] { HandleNW, HandleNE, HandleSW, HandleSE, HandleN, HandleS, HandleW, HandleE };

    // ── Win2D scene ───────────────────────────────────────────────────────────

    private void SceneCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _canvasBitmap?.Dispose();
        _canvasBitmap = CanvasBitmap.CreateFromSoftwareBitmap(sender, _softwareBitmap);

        if (!_initialized)
        {
            InitState();
            _initialized = true;
        }
        if (!_cursorsAssigned)
        {
            AssignCursors();
            _cursorsAssigned = true;
        }

        IsPrimaryButtonEnabled = true;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void InitState()
    {
        _nw = _softwareBitmap.PixelWidth;
        _nh = _softwareBitmap.PixelHeight;
        _fitScale = ViewportSide / Math.Max(_nw, _nh);
        _coverScale = ViewportSide / Math.Min(_nw, _nh);
        _minScale = _fitScale * 0.4;             // zoom out well past Fit for a generous border
        _maxScale = _coverScale * 3.0;
        BuildSnapStops();
        ApplyPreset(fill: true);                 // default to Fill, centered
    }

    /// <summary>
    /// Magnetic zoom detents at 25% increments of the default (Fill = 100%) scale —
    /// …0.5×, 0.75×, 1.0× (the default/normal), 1.25×… — clamped to the usable range.
    /// </summary>
    private void BuildSnapStops()
    {
        var stops = new List<double>();
        for (var m = 0.25; m <= 3.0 + 1e-6; m += 0.25)
        {
            var s = _coverScale * m;
            if (s >= _minScale && s <= _maxScale)
                stops.Add(s);
        }
        // The default (1.0×) is the most important stop — make sure it's present.
        if (_coverScale >= _minScale && _coverScale <= _maxScale
            && !stops.Exists(s => Math.Abs(s - _coverScale) < 1e-6))
            stops.Add(_coverScale);
        stops.Sort();
        _snapScales = stops.ToArray();
    }

    /// <summary>
    /// Pulls a scale onto the nearest detent when it's close enough; the default
    /// (100%) gets a slightly wider catch zone so it's easy to snap back to normal.
    /// </summary>
    private double SnapScale(double scale)
    {
        if (_snapScales.Length == 0) return scale;
        var best = scale;
        var bestRel = double.MaxValue;
        var bestIsDefault = false;
        foreach (var s in _snapScales)
        {
            var rel = Math.Abs(scale - s) / s;
            if (rel < bestRel)
            {
                bestRel = rel;
                best = s;
                bestIsDefault = Math.Abs(s - _coverScale) < 1e-6;
            }
        }
        var threshold = bestIsDefault ? 0.05 : 0.035;
        return bestRel <= threshold ? best : scale;
    }

    private void AssignCursors()
    {
        HandleNW.ChangeCursor(CursorNwse); HandleSE.ChangeCursor(CursorNwse);
        HandleNE.ChangeCursor(CursorNesw); HandleSW.ChangeCursor(CursorNesw);
        HandleN.ChangeCursor(CursorNs); HandleS.ChangeCursor(CursorNs);
        HandleW.ChangeCursor(CursorWe); HandleE.ChangeCursor(CursorWe);
        PanSurface.ChangeCursor(CursorMove);
    }

    private void SceneCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_canvasBitmap is null) return;
        DrawScene(args.DrawingSession, new Rect(0, 0, ViewportSide, ViewportSide), ViewportSide, CanvasImageInterpolation.Linear);
    }

    /// <summary>Renders the padded composite for both the live preview and the export.</summary>
    private void DrawScene(CanvasDrawingSession ds, Rect crop, double target, CanvasImageInterpolation interpolation)
    {
        var k = target / crop.Width;             // viewport px → target px

        if (_fill == FillKind.Solid)
        {
            ds.Clear(_solidColor);
        }
        else
        {
            var bgc = _coverScale * 1.18 * k;    // blurred copy of the photo, covering the square
            var bw = _nw * bgc;
            var bh = _nh * bgc;
            using var scaled = new Transform2DEffect
            {
                Source = _canvasBitmap,
                TransformMatrix = Matrix3x2.CreateScale((float)bgc),
                InterpolationMode = CanvasImageInterpolation.Linear,
            };
            using var blur = new GaussianBlurEffect { Source = scaled, BlurAmount = (float)(target * 0.055) };
            ds.DrawImage(blur, new Vector2((float)(target / 2 - bw / 2), (float)(target / 2 - bh / 2)));
            ds.FillRectangle(0, 0, (float)target, (float)target, Color.FromArgb(97, 0, 0, 0)); // ≈ brightness .62
        }

        var w = _nw * _scale * k;
        var h = _nh * _scale * k;
        var imgLeft = (_icx - _nw * _scale / 2 - crop.X) * k;
        var imgTop = (_icy - _nh * _scale / 2 - crop.Y) * k;
        ds.DrawImage(_canvasBitmap, new Rect(imgLeft, imgTop, w, h), _canvasBitmap!.Bounds, 1.0f, interpolation);
    }

    // ── State → visuals ───────────────────────────────────────────────────────

    private void Refresh()
    {
        ClampPan();
        UpdateSlider();
        UpdateCropVisuals();
        UpdatePresetHighlight();
        SceneCanvas.Invalidate();
    }

    private void ClampPan()
    {
        _icx = Clamp(_icx, 0, ViewportSide);
        _icy = Clamp(_icy, 0, ViewportSide);
    }

    private void UpdateSlider()
    {
        _suppressSlider = true;
        ZoomSlider.Value = (_scale - _minScale) / (_maxScale - _minScale) * 1000.0;
        _suppressSlider = false;
        // Percentage is relative to the default (Fill = 100%) so the detents read
        // as round numbers (…75%, 100%, 125%…).
        if (_coverScale > 0)
            ZoomPercentText.Text = $"{Math.Round(_scale / _coverScale * 100)}%";
    }

    private void UpdateCropVisuals()
    {
        CropFrame.Margin = new Thickness(_fx, _fy, 0, 0);
        CropFrame.Width = _fs;
        CropFrame.Height = _fs;
        ScrimInner.Rect = new Rect(_fx, _fy, _fs, _fs);

        PositionHandle(HandleNW, _fx, _fy);
        PositionHandle(HandleNE, _fx + _fs, _fy);
        PositionHandle(HandleSW, _fx, _fy + _fs);
        PositionHandle(HandleSE, _fx + _fs, _fy + _fs);
        PositionHandle(HandleN, _fx + _fs / 2, _fy);
        PositionHandle(HandleS, _fx + _fs / 2, _fy + _fs);
        PositionHandle(HandleW, _fx, _fy + _fs / 2);
        PositionHandle(HandleE, _fx + _fs, _fy + _fs / 2);
    }

    private static void PositionHandle(Thumb t, double cx, double cy)
    {
        Canvas.SetLeft(t, Clamp(cx - t.Width / 2, 0, ViewportSide - t.Width));
        Canvas.SetTop(t, Clamp(cy - t.Height / 2, 0, ViewportSide - t.Height));
    }

    private void UpdatePresetHighlight()
    {
        var full = _fx == 0 && _fy == 0 && Math.Abs(_fs - ViewportSide) < 0.5;
        FillToggle.IsChecked = full && Math.Abs(_scale - _coverScale) < 0.01;
        FitToggle.IsChecked = full && Math.Abs(_scale - _fitScale) < 0.01;
    }

    // ── Interactions ──────────────────────────────────────────────────────────

    private void ApplyPreset(bool fill)
    {
        _fx = 0;
        _fy = 0;
        _fs = ViewportSide;
        _scale = fill ? _coverScale : _fitScale;
        _icx = ViewportSide / 2;
        _icy = ViewportSide / 2;
    }

    private void SetScale(double next, double anchorX, double anchorY)
    {
        var clamped = Clamp(SnapScale(next), _minScale, _maxScale);
        var ratio = clamped / _scale;
        _icx = anchorX + (_icx - anchorX) * ratio;
        _icy = anchorY + (_icy - anchorY) * ratio;
        _scale = clamped;
        Refresh();
    }

    private void PanSurface_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e) => SetGrid(true);

    private void PanSurface_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_initialized) return;
        _icx += e.Delta.Translation.X;
        _icy += e.Delta.Translation.Y;
        Refresh();
    }

    private void PanSurface_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) => SetGrid(false);

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_initialized) return;
        var pt = e.GetCurrentPoint(Viewport);
        var factor = pt.Properties.MouseWheelDelta > 0 ? 1.08 : 1.0 / 1.08;
        SetScale(_scale * factor, pt.Position.X, pt.Position.Y);
        e.Handled = true;
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || !_initialized) return;
        var t = e.NewValue / 1000.0;
        SetScale(_minScale + t * (_maxScale - _minScale), ViewportSide / 2, ViewportSide / 2);
    }

    private void Handle_DragStarted(object sender, DragStartedEventArgs e) => SetGrid(true);
    private void Handle_DragCompleted(object sender, DragCompletedEventArgs e) => SetGrid(false);

    private void Handle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_initialized) return;
        ResizeByHandle((string)((FrameworkElement)sender).Tag, e.HorizontalChange, e.VerticalChange);
        Refresh();
    }

    /// <summary>Square-locked crop resize anchored at the opposite corner/edge.</summary>
    private void ResizeByHandle(string handle, double dx, double dy)
    {
        double right = _fx + _fs, bottom = _fy + _fs, cxc = _fx + _fs / 2, cyc = _fy + _fs / 2;
        double nfx = _fx, nfy = _fy, nfs = _fs;

        switch (handle)
        {
            case "se":
                nfs = Clamp(Math.Max(_fs + dx, _fs + dy), MinFrame, Math.Min(ViewportSide - _fx, ViewportSide - _fy));
                break;
            case "nw":
            {
                double px = _fx + dx, py = _fy + dy;
                nfs = Clamp(Math.Max(right - px, bottom - py), MinFrame, Math.Min(right, bottom));
                nfx = right - nfs; nfy = bottom - nfs;
                break;
            }
            case "ne":
            {
                double px = _fx + _fs + dx, py = _fy + dy;
                nfs = Clamp(Math.Max(px - _fx, bottom - py), MinFrame, Math.Min(ViewportSide - _fx, bottom));
                nfy = bottom - nfs;
                break;
            }
            case "sw":
            {
                double px = _fx + dx, py = _fy + _fs + dy;
                nfs = Clamp(Math.Max(right - px, py - _fy), MinFrame, Math.Min(right, ViewportSide - _fy));
                nfx = right - nfs;
                break;
            }
            case "n":
            {
                double py = _fy + dy;
                nfs = Clamp(bottom - py, MinFrame, Math.Min(bottom, Math.Min(2 * cxc, 2 * (ViewportSide - cxc))));
                nfy = bottom - nfs; nfx = cxc - nfs / 2;
                break;
            }
            case "s":
            {
                double py = _fy + _fs + dy;
                nfs = Clamp(py - _fy, MinFrame, Math.Min(ViewportSide - _fy, Math.Min(2 * cxc, 2 * (ViewportSide - cxc))));
                nfx = cxc - nfs / 2;
                break;
            }
            case "w":
            {
                double px = _fx + dx;
                nfs = Clamp(right - px, MinFrame, Math.Min(right, Math.Min(2 * cyc, 2 * (ViewportSide - cyc))));
                nfx = right - nfs; nfy = cyc - nfs / 2;
                break;
            }
            case "e":
            {
                double px = _fx + _fs + dx;
                nfs = Clamp(px - _fx, MinFrame, Math.Min(ViewportSide - _fx, Math.Min(2 * cyc, 2 * (ViewportSide - cyc))));
                nfy = cyc - nfs / 2;
                break;
            }
        }

        _fs = nfs;
        _fx = Clamp(nfx, 0, ViewportSide - nfs);
        _fy = Clamp(nfy, 0, ViewportSide - nfs);
    }

    private void FillToggle_Click(object sender, RoutedEventArgs e) { ApplyPreset(true); Refresh(); }
    private void FitToggle_Click(object sender, RoutedEventArgs e) { ApplyPreset(false); Refresh(); }
    private void ResetButton_Click(object sender, RoutedEventArgs e) { ApplyPreset(true); Refresh(); }

    private void BlurToggle_Click(object sender, RoutedEventArgs e) => SetFill(FillKind.Blur);
    private void SolidToggle_Click(object sender, RoutedEventArgs e) => SetFill(FillKind.Solid);

    private void SetFill(FillKind kind)
    {
        _fill = kind;
        BlurToggle.IsChecked = kind == FillKind.Blur;
        SolidToggle.IsChecked = kind == FillKind.Solid;
        // Swatches stay tappable in Blur mode (tapping one selects Solid); just
        // de-emphasise them while Blur is the active fill.
        SwatchPanel.Opacity = kind == FillKind.Solid ? 1.0 : 0.55;
        SceneCanvas.Invalidate();
    }

    private void Swatch_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var border = (Border)sender;
        _solidColor = ParseColor((string)border.Tag);
        SelectSwatch(border);
        if (_fill != FillKind.Solid)
            SetFill(FillKind.Solid);   // picking a colour implies Solid padding
        else
            SceneCanvas.Invalidate();
    }

    private void SwatchCustom_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Seed the picker with the active colour without it registering as a change.
        _suppressColorPicker = true;
        CustomColorPicker.Color = _solidColor;
        _suppressColorPicker = false;
        FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
    }

    private void CustomColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressColorPicker) return;
        _solidColor = args.NewColor;
        SwatchCustom.Background = new SolidColorBrush(args.NewColor);
        SelectSwatch(SwatchCustom);
        if (_fill != FillKind.Solid)
            SetFill(FillKind.Solid);
        else
            SceneCanvas.Invalidate();
    }

    private void SelectSwatch(Border selected)
    {
        foreach (var sw in _swatches)
            sw.BorderBrush = sw == selected ? _swatchSelected : _swatchUnselected;
    }

    private void SetGrid(bool on) => GridLines.Opacity = on ? 0.6 : 0.0;

    // ── Confirm / export ──────────────────────────────────────────────────────

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            _result = await ExportAsync();
        }
        catch (Exception)
        {
            _result = null;
            Subtitle.Text = "Couldn't process that image — try a different crop or photo.";
            if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var errBrush) && errBrush is Brush brush)
                Subtitle.Foreground = brush;
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<byte[]?> ExportAsync()
    {
        if (_canvasBitmap is null) return null;

        var side = _options.OutputSide;
        byte[] bgra;
        using (var rt = new CanvasRenderTarget(SceneCanvas.Device, side, side, 96))
        {
            using (var ds = rt.CreateDrawingSession())
                DrawScene(ds, new Rect(_fx, _fy, _fs, _fs), side, CanvasImageInterpolation.HighQualityCubic);
            bgra = rt.GetPixelBytes();
        }
        return await PlaylistCoverHelper.EncodeSquareJpegAsync(bgra, (uint)side);
    }

    // ── Theme awareness (reactive to live light/dark switches) ─────────────────

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _themeRoot = XamlRoot?.Content as FrameworkElement;
        if (_themeRoot is null) return;
        RequestedTheme = _themeRoot.ActualTheme;
        _themeRoot.ActualThemeChanged += OnRootActualThemeChanged;
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
        => RequestedTheme = sender.ActualTheme;

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_themeRoot is not null)
        {
            _themeRoot.ActualThemeChanged -= OnRootActualThemeChanged;
            _themeRoot = null;
        }
        SceneCanvas.RemoveFromVisualTree();
        _canvasBitmap?.Dispose();
        _canvasBitmap = null;
        _softwareBitmap?.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<SoftwareBitmap> DecodeOrientedAsync(StorageFile file)
    {
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var transform = new BitmapTransform();
        const uint cap = 2560;
        uint pw = decoder.PixelWidth, ph = decoder.PixelHeight;
        var sc = Math.Min(1.0, (double)cap / Math.Max(pw, ph));
        if (sc < 1.0)
        {
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(pw * sc));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(ph * sc));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
    }

    private static ElementTheme ResolveTheme(XamlRoot xamlRoot)
        => xamlRoot.Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default;

    private static Color ParseColor(string argbHex)
    {
        var hex = argbHex.TrimStart('#');
        var a = Convert.ToByte(hex.Substring(0, 2), 16);
        var r = Convert.ToByte(hex.Substring(2, 2), 16);
        var g = Convert.ToByte(hex.Substring(4, 2), 16);
        var b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }

    private static double Clamp(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), Math.Max(lo, hi));
}
