using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Concert row in the V4A "Upcoming concerts" panel — tear-off date block on
/// the left, venue/city in the middle, Buy hyperlink on the right. The accent
/// stripe colour picks up the artist palette so a row reads as a real ticket
/// stub. <see cref="IsNearUser"/> surfaces the "NEAR YOU" chip.
/// </summary>
public sealed partial class TicketStub : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;
    public event EventHandler<RoutedEventArgs>? BuyClick;

    public static readonly DependencyProperty MonthProperty =
        DependencyProperty.Register(nameof(Month), typeof(string), typeof(TicketStub),
            new PropertyMetadata(string.Empty, OnMonthChanged));

    public static readonly DependencyProperty DayProperty =
        DependencyProperty.Register(nameof(Day), typeof(string), typeof(TicketStub),
            new PropertyMetadata(string.Empty, OnDayChanged));

    public static readonly DependencyProperty VenueProperty =
        DependencyProperty.Register(nameof(Venue), typeof(string), typeof(TicketStub),
            new PropertyMetadata(string.Empty, OnVenueChanged));

    public static readonly DependencyProperty CityProperty =
        DependencyProperty.Register(nameof(City), typeof(string), typeof(TicketStub),
            new PropertyMetadata(string.Empty, OnCityChanged));

    public static readonly DependencyProperty IsNearUserProperty =
        DependencyProperty.Register(nameof(IsNearUser), typeof(bool), typeof(TicketStub),
            new PropertyMetadata(false, OnIsNearUserChanged));

    public string Month { get => (string)GetValue(MonthProperty); set => SetValue(MonthProperty, value); }
    public string Day { get => (string)GetValue(DayProperty); set => SetValue(DayProperty, value); }
    public string Venue { get => (string)GetValue(VenueProperty); set => SetValue(VenueProperty, value); }
    public string City { get => (string)GetValue(CityProperty); set => SetValue(CityProperty, value); }
    public bool IsNearUser { get => (bool)GetValue(IsNearUserProperty); set => SetValue(IsNearUserProperty, value); }

    public TicketStub() => InitializeComponent();

    private static void OnMonthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TicketStub s) s.MonthText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnDayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TicketStub s) s.DayText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnVenueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TicketStub s) s.VenueText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnCityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TicketStub s) s.CityText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnIsNearUserChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TicketStub s && e.NewValue is bool b)
            s.NearYouChip.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => CardClick?.Invoke(this, e);
    private void BuyLink_Click(object sender, RoutedEventArgs e) => BuyClick?.Invoke(this, e);
}
