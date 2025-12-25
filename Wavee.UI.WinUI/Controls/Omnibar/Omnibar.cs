using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Omnibar;

/// <summary>
/// A search bar control styled like Files app's Omnibar.
/// </summary>
public sealed partial class Omnibar : Control
{
    private AutoSuggestBox? _searchBox;

    public Omnibar()
    {
        DefaultStyleKey = typeof(Omnibar);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _searchBox = GetTemplateChild("PART_SearchBox") as AutoSuggestBox;

        if (_searchBox != null)
        {
            _searchBox.TextChanged += SearchBox_TextChanged;
            _searchBox.QuerySubmitted += SearchBox_QuerySubmitted;
            _searchBox.SuggestionChosen += SearchBox_SuggestionChosen;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            TextChanged?.Invoke(this, new OmnibarTextChangedEventArgs(sender.Text));
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        QuerySubmitted?.Invoke(this, new OmnibarQuerySubmittedEventArgs(args.QueryText, args.ChosenSuggestion));
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        SuggestionChosen?.Invoke(this, new OmnibarSuggestionChosenEventArgs(args.SelectedItem));
    }

    #region Dependency Properties

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Omnibar),
            new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(Omnibar),
            new PropertyMetadata("Search"));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(Omnibar),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Omnibar omnibar && omnibar._searchBox != null)
        {
            omnibar._searchBox.ItemsSource = e.NewValue;
        }
    }

    #endregion

    #region Events

    public event TypedEventHandler<Omnibar, OmnibarTextChangedEventArgs>? TextChanged;
    public event TypedEventHandler<Omnibar, OmnibarQuerySubmittedEventArgs>? QuerySubmitted;
    public event TypedEventHandler<Omnibar, OmnibarSuggestionChosenEventArgs>? SuggestionChosen;

    #endregion
}

public class OmnibarTextChangedEventArgs(string text)
{
    public string Text { get; } = text;
}

public class OmnibarQuerySubmittedEventArgs(string queryText, object? chosenSuggestion)
{
    public string QueryText { get; } = queryText;
    public object? ChosenSuggestion { get; } = chosenSuggestion;
}

public class OmnibarSuggestionChosenEventArgs(object? selectedItem)
{
    public object? SelectedItem { get; } = selectedItem;
}
