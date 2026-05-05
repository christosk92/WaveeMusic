using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class FeedbackPage : Page, ITabBarItemContent
{
    private static readonly Brush AccentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private static readonly Brush ErrorBrush = new SolidColorBrush(Colors.Red);
    private static readonly Brush DefaultCardBorder = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
    private static readonly Brush SelectedCardBackground = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
    private static readonly Brush DefaultCardBackground = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    private Border[] _cards = [];
    private Brush? _defaultTitleBorder;
    private Brush? _defaultBodyBorder;

    public FeedbackViewModel ViewModel { get; }
    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;
    public event EventHandler<TabItemParameter>? ContentChanged;

    public FeedbackPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<FeedbackViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations.
        Bindings?.StopTracking();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cards = [CardBug, CardTicket, CardFeature, CardGeneral];
        _defaultTitleBorder = TitleBox.BorderBrush;
        _defaultBodyBorder = BodyBox.BorderBrush;

        UpdateCardSelection();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.FeedbackTypeIndex):
                UpdateCardSelection();
                break;
            case nameof(ViewModel.HasTitleError):
                TitleBox.BorderBrush = ViewModel.HasTitleError ? ErrorBrush : _defaultTitleBorder;
                break;
            case nameof(ViewModel.HasBodyError):
                BodyBox.BorderBrush = ViewModel.HasBodyError ? ErrorBrush : _defaultBodyBorder;
                break;
        }
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag && int.TryParse(tag, out var index))
        {
            ViewModel.FeedbackTypeIndex = index;
        }
    }

    private void UpdateCardSelection()
    {
        for (var i = 0; i < _cards.Length; i++)
        {
            var selected = i == ViewModel.FeedbackTypeIndex;
            _cards[i].BorderBrush = selected ? AccentBrush : DefaultCardBorder;
            _cards[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _cards[i].Background = selected ? SelectedCardBackground : DefaultCardBackground;
        }
    }

    // ── Markdown formatting helpers ──

    private void WrapSelection(string prefix, string suffix)
    {
        var start = BodyBox.SelectionStart;
        var len = BodyBox.SelectionLength;
        var text = BodyBox.Text ?? "";

        if (len > 0)
        {
            var selected = text.Substring(start, len);
            var replacement = prefix + selected + suffix;
            BodyBox.Text = text.Remove(start, len).Insert(start, replacement);
            BodyBox.SelectionStart = start + prefix.Length;
            BodyBox.SelectionLength = len;
        }
        else
        {
            var placeholder = prefix + suffix;
            BodyBox.Text = text.Insert(start, placeholder);
            BodyBox.SelectionStart = start + prefix.Length;
            BodyBox.SelectionLength = 0;
        }

        BodyBox.Focus(FocusState.Programmatic);
    }

    private void InsertAtLineStart(string prefix)
    {
        var start = BodyBox.SelectionStart;
        var text = BodyBox.Text ?? "";

        // Find start of current line
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        BodyBox.Text = text.Insert(lineStart, prefix);
        BodyBox.SelectionStart = lineStart + prefix.Length;
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void FormatBold_Click(object sender, RoutedEventArgs e) => WrapSelection("**", "**");
    private void FormatItalic_Click(object sender, RoutedEventArgs e) => WrapSelection("_", "_");
    private void FormatCode_Click(object sender, RoutedEventArgs e) => WrapSelection("`", "`");
    private void FormatBulletList_Click(object sender, RoutedEventArgs e) => InsertAtLineStart("- ");
    private void FormatNumberedList_Click(object sender, RoutedEventArgs e) => InsertAtLineStart("1. ");
    private void FormatLink_Click(object sender, RoutedEventArgs e) => WrapSelection("[", "](url)");

    // ── Image attachment handlers ──

    private async void BrowseImages_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;

        // Initialize with the window handle
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null) return;

        foreach (var file in files)
        {
            ViewModel.TryAddImage(file.Path, out _);
        }
    }

    private async void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add screenshot";
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        foreach (var item in items)
        {
            if (item is StorageFile file &&
                imageExtensions.Contains(Path.GetExtension(file.Path).ToLowerInvariant()))
            {
                ViewModel.TryAddImage(file.Path, out _);
            }
        }
    }

    private async void ImageThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image img) return;

        // Walk up to find the DataContext (AttachedImage)
        var parent = img.Parent as FrameworkElement;
        while (parent is not null && parent.DataContext is not AttachedImage)
            parent = parent.Parent as FrameworkElement;

        if (parent?.DataContext is AttachedImage attached)
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 200 };
                var file = await StorageFile.GetFileFromPathAsync(attached.FilePath);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                await bitmap.SetSourceAsync(stream);
                img.Source = bitmap;
            }
            catch
            {
                // Silently ignore if file can't be loaded
            }
        }
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;

        var parent = el.Parent as FrameworkElement;
        while (parent is not null && parent.DataContext is not AttachedImage)
            parent = parent.Parent as FrameworkElement;

        if (parent?.DataContext is AttachedImage attached)
        {
            ViewModel.RemoveImageCommand.Execute(attached);
        }
    }
}
