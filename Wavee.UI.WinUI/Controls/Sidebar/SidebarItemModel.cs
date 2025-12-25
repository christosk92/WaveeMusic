// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Sidebar;

/// <summary>
/// ViewModel implementation for sidebar items.
/// </summary>
public class SidebarItemModel : ISidebarItemModel
{
    private object? _children;
    private IconSource? _iconSource;
    private bool _isExpanded = true;
    private string _text = string.Empty;
    private object _toolTip = string.Empty;
    private bool _paddedItem;
    private string? _tag;
    private bool _showEmptyPlaceholder;
    private string _emptyPlaceholderText = "Drop items here to pin";
    private int? _badgeCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public object? Children
    {
        get => _children;
        set => SetProperty(ref _children, value);
    }

    /// <inheritdoc />
    public IconSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    /// <inheritdoc />
    public FrameworkElement? ItemDecorator { get; set; }

    /// <inheritdoc />
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <inheritdoc />
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <inheritdoc />
    public object ToolTip
    {
        get => _toolTip;
        set => SetProperty(ref _toolTip, value);
    }

    /// <inheritdoc />
    public bool PaddedItem
    {
        get => _paddedItem;
        set => SetProperty(ref _paddedItem, value);
    }

    /// <summary>
    /// Custom tag for navigation purposes.
    /// </summary>
    public string? Tag
    {
        get => _tag;
        set => SetProperty(ref _tag, value);
    }

    /// <summary>
    /// Whether to show an empty placeholder when this group has no children.
    /// </summary>
    public bool ShowEmptyPlaceholder
    {
        get => _showEmptyPlaceholder;
        set => SetProperty(ref _showEmptyPlaceholder, value);
    }

    /// <summary>
    /// The text to display in the empty placeholder.
    /// </summary>
    public string EmptyPlaceholderText
    {
        get => _emptyPlaceholderText;
        set => SetProperty(ref _emptyPlaceholderText, value);
    }

    /// <summary>
    /// The count to display in the badge. Set to null to hide the badge.
    /// </summary>
    public int? BadgeCount
    {
        get => _badgeCount;
        set => SetProperty(ref _badgeCount, value);
    }

    public SidebarItemModel() { }

    public SidebarItemModel(string text, IconSource? iconSource = null, string? tag = null)
    {
        Text = text;
        IconSource = iconSource;
        Tag = tag;
        ToolTip = text;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
