using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Small "profile fact" tile used in the V4A artist page's "About &amp; Links"
/// section — big accent number, small secondary caption. 6 of these render in
/// a 2-column UniformGridLayout. The <see cref="AccentBrush"/> DP lets the
/// host page push the artist's palette accent through to the number text so
/// the tile reads as part of the artist's visual identity rather than the
/// system theme.
/// </summary>
public sealed partial class StatCard : UserControl
{
    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.Register(nameof(Number), typeof(string), typeof(StatCard),
            new PropertyMetadata("", OnNumberChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatCard),
            new PropertyMetadata("", OnLabelChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(StatCard),
            new PropertyMetadata(null, OnAccentBrushChanged));

    public string Number { get => (string)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public StatCard()
    {
        InitializeComponent();
    }

    private static void OnNumberChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatCard c) c.NumberText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatCard c) c.LabelText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // AccentBrush DP preserved for backward compatibility but no longer
        // applied — the NumberText uses the system theme accent
        // (AccentTextFillColorPrimaryBrush) from the StatCard.xaml default.
    }
}
