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
using Wavee.UI.Services.DragDrop;

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
    private bool _isLoadingChildren;
    private bool _isAliasSelected;
    private bool _showCompactSeparatorBefore;
    private bool _showUnpinButton;
    private bool _showPinToggleButton;
    private bool _isPinned;
    private bool _isEnabled = true;
    private bool _isDropZoneOnly;

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
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
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
    /// Original cover image URL (or <c>spotify:mosaic:...</c>) the item was
    /// created with. Used by the shell to decide whether a Mercury-driven
    /// content change should trigger a sidebar mosaic rebuild — playlists
    /// with a real uploaded cover (HTTPS URL) skip the rebuild.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// True when this row represents a playlist owned by the current user.
    /// Lets the right-click context menu surface owner-only items (Delete,
    /// Rename, etc.) and suppress non-sensical ones (Report, Exclude from
    /// taste, Remove from Library).
    /// </summary>
    public bool IsOwner { get; set; }

    /// <summary>
    /// True when the current user can mutate this playlist's tracks (owner OR
    /// collaborative). Drag-drop drop predicates gate add-tracks gestures on
    /// this so drops onto non-editable rows (Discover Weekly, friends'
    /// playlists, etc.) are rejected before they round-trip to the server.
    /// </summary>
    public bool CanEditItems { get; set; }

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
    /// When true, the sidebar template renders a shimmer skeleton in place of the
    /// children presenter — used to signal "fetching, please wait" for sections
    /// whose content is loaded asynchronously (e.g. user playlists on cold launch).
    /// Flip back to false once the section has been populated. Toggling at runtime
    /// re-runs the VSM transition via <see cref="SidebarItem"/>'s property handler.
    /// </summary>
    public bool IsLoadingChildren
    {
        get => _isLoadingChildren;
        set => SetProperty(ref _isLoadingChildren, value);
    }

    /// <summary>
    /// True when this row is not the primary <see cref="SelectedSidebarItem"/>
    /// but represents the same logical destination as the row that is — e.g.
    /// the Your-Library "Liked Songs" row when a pinned <c>spotify:collection</c>
    /// row is selected. The sidebar template renders both with the selected
    /// visual state so the user can see both surfaces light up. Set by
    /// <c>ShellViewModel</c> whenever <c>SelectedSidebarItem</c> changes.
    /// </summary>
    public bool IsAliasSelected
    {
        get => _isAliasSelected;
        set => SetProperty(ref _isAliasSelected, value);
    }

    /// <summary>
    /// Adds a hairline section break above this top-level section in compact
    /// sidebar mode. Expanded mode keeps the normal text section headers.
    /// </summary>
    public bool ShowCompactSeparatorBefore
    {
        get => _showCompactSeparatorBefore;
        set => SetProperty(ref _showCompactSeparatorBefore, value);
    }

    /// <summary>
    /// Renders an always-visible unpin (pushpin) button on the right side of
    /// the row. Used by Pinned-section rows so the user can remove a pin in
    /// one click. Mutually exclusive with <see cref="ShowPinToggleButton"/>.
    /// </summary>
    public bool ShowUnpinButton
    {
        get => _showUnpinButton;
        set => SetProperty(ref _showUnpinButton, value);
    }

    /// <summary>
    /// Renders an unpin button on the right side of pinned canonical
    /// Your-Library Liked-Songs / Podcasts rows whose pinned destination is
    /// represented by a pseudo-URI.
    /// </summary>
    public bool ShowPinToggleButton
    {
        get => _showPinToggleButton;
        set => SetProperty(ref _showPinToggleButton, value);
    }

    /// <summary>
    /// Whether the item this row represents is currently pinned. Drives the
    /// visibility of rows that show <see cref="ShowPinToggleButton"/>.
    /// Pinned-section rows always show the Unpin glyph regardless of this flag.
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>
    /// Per-instance predicate that determines whether this item accepts a given drag payload.
    /// </summary>
    public Func<IDragPayload, bool>? DropPredicate { get; init; }

    /// <summary>
    /// True for placeholder rows that exist purely as drop targets — currently
    /// the "Drop here to pin to sidebar" placeholder at the end of the Pinned
    /// section. <see cref="SidebarItem"/> keeps these rows collapsed when no
    /// drag is in flight, and reveals them only when the active drag payload
    /// matches <see cref="DropPredicate"/>. Their accept logic flows through
    /// the normal drop pipeline; the special tag tells the shell how to
    /// translate the drop into an action (e.g. pin-to-sidebar).
    /// </summary>
    public bool IsDropZoneOnly
    {
        get => _isDropZoneOnly;
        set => SetProperty(ref _isDropZoneOnly, value);
    }

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
