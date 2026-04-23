// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.DragDrop;

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
    private int _depth;
    private bool _isFolder;
    private bool _isSectionHeader;

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

    /// <summary>
    /// Nesting depth used to indent the row. 0 = top-level, +1 per folder we are inside of.
    /// The sidebar template binds this through <c>DepthToThicknessConverter</c> (20 px/level)
    /// to produce the row's left padding.
    /// </summary>
    public int Depth
    {
        get => _depth;
        set => SetProperty(ref _depth, value);
    }

    /// <summary>
    /// True if this row represents a folder (vs. a playlist or other leaf). The sidebar
    /// template uses this together with <see cref="IsExpanded"/> to swap the folder glyph
    /// between closed (E8B7) and open (E838), and to render the accent-tinted folder tile.
    /// </summary>
    public bool IsFolder
    {
        get => _isFolder;
        set => SetProperty(ref _isFolder, value);
    }

    /// <summary>
    /// True for top-level section-label groups (e.g. "Your Library", "Playlists", "Made For You").
    /// The sidebar template renders these chromeless as all-caps tertiary labels — distinct
    /// from interactive folder rows. User-created folders must leave this <c>false</c>.
    /// </summary>
    public bool IsSectionHeader
    {
        get => _isSectionHeader;
        set => SetProperty(ref _isSectionHeader, value);
    }

    /// <summary>
    /// Per-instance predicate that determines whether this item accepts a given drag payload.
    /// </summary>
    public Func<IDragPayload, bool>? DropPredicate { get; init; }

    /// <summary>
    /// Optional lazy loader for the icon. When non-null, the SidebarItem control invokes
    /// it the first time the row is realized, then sets it back to null so recycling
    /// doesn't re-trigger the work. Used for Spotify custom-playlist mosaic icons that
    /// require fetching playlist tracks before composing.
    /// </summary>
    public Func<CancellationToken, Task<IconSource?>>? LazyIconSourceLoader { get; set; }

    /// <inheritdoc />
    public bool CanDrop(IDragPayload payload) => DropPredicate?.Invoke(payload) ?? false;

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
