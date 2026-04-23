// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Wavee.UI.WinUI.DragDrop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Sidebar;

public sealed partial class SidebarItem : Control
{
	private const double DROP_REPOSITION_THRESHOLD = 0.2; // Percentage of top/bottom at which we consider a drop to be a reposition/insertion

	public bool HasChildren => Item?.Children is IList enumerable && enumerable.Count > 0;
	public bool IsGroupHeader => Item?.Children is not null;
	public bool CollapseEnabled => DisplayMode != SidebarDisplayMode.Compact;

	private bool hasChildSelection => selectedChildItem != null;
	private bool isPointerOver = false;
	private bool isClicking = false;
	private object? selectedChildItem = null;
	private ItemsRepeater? childrenRepeater;
	private ISidebarItemModel? lastSubscriber;
	private ContentPresenter? iconPresenter;
	private long _displayModeCallbackToken;
	private long _selectedItemCallbackToken;
	private SidebarView? _ownerAtSubscription;
	private INotifyCollectionChanged? _subscribedCollection;
	private DragStateService? _dragStateService;
	private Border? _elementBorder;
	private Action? _themeChangedHandler;
	private Services.ThemeColorService? _themeColorService;
	private CancellationTokenSource? _lazyIconCts;

	public SidebarItem()
	{
		_themeColorService = Ioc.Default.GetService<Services.ThemeColorService>();
		DefaultStyleKey = typeof(SidebarItem);

		PointerReleased += Item_PointerReleased;
		KeyDown += (sender, args) =>
		{
			if (args.Key == Windows.System.VirtualKey.Enter)
			{
				Clicked(PointerUpdateKind.Other);
				args.Handled = true;
			}
		};
		DragStarting += SidebarItem_DragStarting;

		Loaded += SidebarItem_Loaded;
		Unloaded += SidebarItem_Unloaded;
	}

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		return new SidebarItemAutomationPeer(this);
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		iconPresenter = GetTemplateChild("IconPresenter") as ContentPresenter;
		UpdateIconPresenter();
	}

	private void UpdateIconPresenter()
	{
		if (iconPresenter is null)
			return;

		if (_themeColorService != null && _themeChangedHandler != null)
		{
			_themeColorService.ThemeChanged -= _themeChangedHandler;
			_themeChangedHandler = null;
		}

		if (Item?.IconSource is null)
		{
			iconPresenter.Content = null;
			return;
		}

		var rawIcon = CreateSidebarIcon(Item.IconSource);
		var isFolder = Item is SidebarItemModel folderModel && folderModel.IsFolder;

		// Folders wrap their glyph in a 32×32 accent-tinted rounded tile so the row
		// carries the same visual mass as a playlist row (which shows 32×32 artwork).
		// The inner FontIcon still receives theme-aware foreground wiring below.
		FrameworkElement icon;
		FontIcon? fontIcon;
		Border? folderTile = null;
		if (isFolder)
		{
			icon = CreateFolderIcon(rawIcon, out var themed, out folderTile);
			fontIcon = themed;
		}
		else
		{
			icon = rawIcon;
			fontIcon = rawIcon as FontIcon;
		}

		if (_themeColorService != null && (fontIcon is not null || folderTile is not null))
		{
			var colors = _themeColorService;
			if (fontIcon is not null)
			{
				fontIcon.FontSize = 16;
				fontIcon.Foreground = colors.TextPrimary;
			}
			if (folderTile is not null)
				folderTile.Background = colors.AppAccent;

			// One handler refreshes both the glyph foreground AND the folder-tile fill
			// on live theme changes (light/dark swap, accent palette shift).
			var capturedFontIcon = fontIcon;
			var capturedTile = folderTile;
			_themeChangedHandler = () =>
			{
				var dq = capturedFontIcon?.DispatcherQueue ?? capturedTile?.DispatcherQueue;
				dq?.TryEnqueue(() =>
				{
					if (capturedFontIcon is not null)
						capturedFontIcon.Foreground = colors.TextPrimary;
					if (capturedTile is not null)
						capturedTile.Background = colors.AppAccent;
				});
			};
			colors.ThemeChanged += _themeChangedHandler;
		}
		else if (fontIcon is not null)
		{
			fontIcon.FontSize = 16;
		}

		// Artwork (playlist thumbnail) and folder tiles render at 32 px so they align
		// vertically; bare-glyph icons stay at 16. Row height is 44 px to fit the tile.
		var hostTag = (icon as FrameworkElement)?.Tag as string;
		var isTile = hostTag == "ArtworkIcon" || hostTag == "FolderIcon";
		iconPresenter.Width = isTile ? 32 : 16;
		iconPresenter.Height = isTile ? 32 : 16;
		iconPresenter.Margin = isTile ? new Thickness(6, 0, 0, 0) : new Thickness(8, 0, 0, 0);
		iconPresenter.Content = icon;
	}

	/// <summary>
	/// Wraps a folder's inner glyph in a 32×32 rounded accent-tinted tile so folder rows
	/// have the same visual mass as playlist rows (which show artwork in the same slot).
	/// Returns the host element and surfaces both the inner <see cref="FontIcon"/> and the
	/// tile <see cref="Border"/> so the caller can refresh their colors on theme change.
	/// </summary>
	private FrameworkElement CreateFolderIcon(FrameworkElement innerGlyph, out FontIcon? innerFontIcon, out Border tile)
	{
		innerFontIcon = innerGlyph as FontIcon;
		if (innerFontIcon is not null)
		{
			innerFontIcon.FontSize = 16;
			innerFontIcon.HorizontalAlignment = HorizontalAlignment.Center;
			innerFontIcon.VerticalAlignment = VerticalAlignment.Center;
		}

		var host = new Grid
		{
			Width = 32,
			Height = 32,
			Tag = "FolderIcon"
		};

		// Use the app's accent color at low opacity for a soft tint, NOT the Fluent
		// AccentFillColor*Brush system brushes — those are designed as solid button fills
		// (near-full opacity) and render far too loud as a background tile.
		tile = new Border
		{
			CornerRadius = new CornerRadius(6),
			Background = _themeColorService?.AppAccent
				?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1D, 0xB9, 0x54)),
			Opacity = 0.2
		};

		host.Children.Add(tile);
		host.Children.Add(innerGlyph);
		return host;
	}

	internal void Select()
	{
		if (Owner is not null)
			Owner.SelectedItem = Item!;
	}

	private void SidebarItem_Loaded(object sender, RoutedEventArgs e)
	{
		HookupOwners();

		if (GetTemplateChild("ElementBorder") is Border border)
		{
			_elementBorder = border;
			border.PointerEntered += ItemBorder_PointerEntered;
			border.PointerExited += ItemBorder_PointerExited;
			border.PointerCanceled += ItemBorder_PointerCanceled;
			border.PointerPressed += ItemBorder_PointerPressed;
			border.ContextRequested += ItemBorder_ContextRequested;
			border.DragLeave += ItemBorder_DragLeave;
			border.DragOver += ItemBorder_DragOver;
			border.Drop += ItemBorder_Drop;
			border.AllowDrop = true;
			border.IsTabStop = false;
		}

		if (GetTemplateChild("ChildrenPresenter") is ItemsRepeater repeater)
		{
			childrenRepeater = repeater;
			repeater.ElementPrepared += ChildrenPresenter_ElementPrepared;
		}
		if (GetTemplateChild("FlyoutChildrenPresenter") is ItemsRepeater flyoutRepeater)
		{
			flyoutRepeater.ElementPrepared += ChildrenPresenter_ElementPrepared;
		}

		HandleItemChange();

		_dragStateService = Ioc.Default.GetService<DragStateService>();
		if (_dragStateService != null)
			_dragStateService.DragStateChanged += OnGlobalDragStateChanged;
	}

	public void HandleItemChange()
	{
		HookupItemChangeListener(null, Item);
		UpdateExpansionState();
		ReevaluateSelection();

		if (Item is not null)
			Decorator = Item.ItemDecorator;

		TryStartLazyIconLoad();
	}

	/// <summary>
	/// Spotify "custom" playlists arrive without a single cover image — instead the model
	/// carries a <see cref="SidebarItemModel.LazyIconSourceLoader"/> that, when invoked,
	/// fetches the playlist's tracks and composes a 2×2 mosaic. This runs at most once per
	/// model: the loader nulls itself on success so subsequent container recycles for the
	/// same model skip the work. On Unloaded the per-container CTS cancels in-flight work,
	/// and PlaylistMosaicService de-dupes via its in-flight task cache.
	/// </summary>
	private void TryStartLazyIconLoad()
	{
		// A new model is being bound — cancel any work tied to the previous one.
		_lazyIconCts?.Cancel();
		_lazyIconCts?.Dispose();
		_lazyIconCts = null;

		if (Item is not SidebarItemModel model) return;
		var loader = model.LazyIconSourceLoader;
		if (loader is null) return;

		var cts = new CancellationTokenSource();
		_lazyIconCts = cts;
		var ct = cts.Token;
		var dispatcher = DispatcherQueue;

		// Up to MaxAttempts passes, each spaced RetryDelay apart. The original
		// "will retry on next realization" comment assumed the row would
		// scroll out and back — but Spotify-style sidebars stay realized,
		// so a row that failed once (cancel cascade at startup, transient
		// network blip, etc.) never recovered and the placeholder glyph stuck.
		_ = Task.Run(async () =>
		{
			const int MaxAttempts = 3;
			for (int attempt = 1; attempt <= MaxAttempts; attempt++)
			{
				try
				{
					var icon = await loader(ct).ConfigureAwait(false);
					if (ct.IsCancellationRequested) return;
					if (icon is null)
					{
						// Nothing composed (e.g. tile URLs resolved to zero,
						// or every tile failed to load). Back off and retry.
					}
					else
					{
						dispatcher.TryEnqueue(() =>
						{
							model.IconSource = icon;
							model.LazyIconSourceLoader = null;
						});
						return;
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					// Outer row-unload cancel. Don't retry — a new bind will
					// kick off its own TryStartLazyIconLoad.
					return;
				}
				catch (OperationCanceledException)
				{
					// Inner cancel (e.g. sync-complete cascade) — not ours.
					// Fall through to retry.
				}
				catch (Exception)
				{
					// Same — retry silently after a pause.
				}

				if (attempt == MaxAttempts) return;
				try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
				catch (OperationCanceledException) { return; }
			}
		}, ct);
	}

	private void HookupOwners()
	{
		FrameworkElement resolvingTarget = this;
		if (GetTemplateRoot(Parent) is FrameworkElement element)
		{
			resolvingTarget = element;
		}
		Owner = resolvingTarget.FindAscendant<SidebarView>()!;
		_ownerAtSubscription = Owner; // Store reference for safe unsubscription

		_displayModeCallbackToken = Owner.RegisterPropertyChangedCallback(SidebarView.DisplayModeProperty, (sender, args) =>
		{
			DisplayMode = Owner.DisplayMode;
		});
		DisplayMode = Owner.DisplayMode;

		_selectedItemCallbackToken = Owner.RegisterPropertyChangedCallback(SidebarView.SelectedItemProperty, (sender, args) =>
		{
			ReevaluateSelection();
		});
		ReevaluateSelection();
	}

	private void SidebarItem_Unloaded(object sender, RoutedEventArgs e)
	{
		// Clean up PropertyChangedCallbacks using stored reference to prevent memory leaks
		if (_ownerAtSubscription != null)
		{
			_ownerAtSubscription.UnregisterPropertyChangedCallback(SidebarView.DisplayModeProperty, _displayModeCallbackToken);
			_ownerAtSubscription.UnregisterPropertyChangedCallback(SidebarView.SelectedItemProperty, _selectedItemCallbackToken);
			_ownerAtSubscription = null;
		}

		// Clean up collection subscription
		if (_subscribedCollection != null)
		{
			_subscribedCollection.CollectionChanged -= ChildItems_CollectionChanged;
			_subscribedCollection = null;
		}

		// Clean up item property change listener
		if (lastSubscriber != null)
		{
			lastSubscriber.PropertyChanged -= ItemPropertyChangedHandler;
			lastSubscriber = null;
		}

		// Clean up drag state subscription
		if (_dragStateService != null)
		{
			_dragStateService.DragStateChanged -= OnGlobalDragStateChanged;
			_dragStateService = null;
		}

		// Clean up ThemeChanged subscription on singleton
		if (_themeColorService != null && _themeChangedHandler != null)
		{
			_themeColorService.ThemeChanged -= _themeChangedHandler;
			_themeChangedHandler = null;
			_themeColorService = null;
		}

		// Cancel any in-flight lazy icon (mosaic) load tied to this container.
		_lazyIconCts?.Cancel();
		_lazyIconCts?.Dispose();
		_lazyIconCts = null;
	}

	private void HookupItemChangeListener(ISidebarItemModel? oldItem, ISidebarItemModel? newItem)
	{
		// Unsubscribe from stored collection reference
		if (_subscribedCollection != null)
		{
			_subscribedCollection.CollectionChanged -= ChildItems_CollectionChanged;
			_subscribedCollection = null;
		}

		// Unsubscribe from lastSubscriber (if different from oldItem, to avoid double-unsubscribe)
		if (lastSubscriber != null && lastSubscriber != oldItem)
		{
			lastSubscriber.PropertyChanged -= ItemPropertyChangedHandler;
		}

		// Unsubscribe from oldItem
		if (oldItem != null)
		{
			oldItem.PropertyChanged -= ItemPropertyChangedHandler;
		}

		lastSubscriber = null;

		// Subscribe to newItem
		if (newItem != null)
		{
			newItem.PropertyChanged += ItemPropertyChangedHandler;
			lastSubscriber = newItem;

			// Store and subscribe to collection
			if (newItem.Children is INotifyCollectionChanged observableCollection)
			{
				_subscribedCollection = observableCollection;
				_subscribedCollection.CollectionChanged += ChildItems_CollectionChanged;
			}
		}
		UpdateIcon();
	}

	private void SidebarItem_DragStarting(UIElement sender, DragStartingEventArgs args)
	{
		args.Data.SetData(StandardDataFormats.Text, Item!.Text.ToString());
	}

	private void SetFlyoutOpen(bool isOpen = true)
	{
		if (Item?.Children is null) return;

		var flyoutOwner = (GetTemplateChild("ElementGrid") as FrameworkElement)!;
		if (isOpen)
		{
			FlyoutBase.ShowAttachedFlyout(flyoutOwner);
		}
		else
		{
			FlyoutBase.GetAttachedFlyout(flyoutOwner)?.Hide();
		}
	}

	private void ChildItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		ReevaluateSelection();
		UpdateExpansionState();
		if (DisplayMode == SidebarDisplayMode.Compact && !HasChildren)
		{
			SetFlyoutOpen(false);
		}
	}

	void ItemPropertyChangedHandler(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName == nameof(ISidebarItemModel.IconSource))
		{
			UpdateIcon();
		}
	}

	private void ReevaluateSelection()
	{
		if (!IsGroupHeader)
		{
			IsSelected = Item == Owner?.SelectedItem;
			if (IsSelected)
			{
				Owner?.UpdateSelectedItemContainer(this);
			}
		}
		else if (Item?.Children is IList list)
		{
			IsSelected = false; // Group headers should never be selected
			if (list.Contains(Owner?.SelectedItem))
			{
				selectedChildItem = Owner?.SelectedItem;
				SetFlyoutOpen(false);
			}
			else
			{
				selectedChildItem = null;
			}
			UpdateSelectionState();
		}
	}

	private void ChildrenPresenter_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
	{
		if (args.Element is SidebarItem item)
		{
			if (Item?.Children is IList enumerable)
			{
				var newElement = enumerable[args.Index];
				if (newElement == selectedChildItem)
				{
					(args.Element as SidebarItem)!.IsSelected = true;
				}
				else
				{
					(args.Element as SidebarItem)!.IsSelected = false;
				}
				item.HandleItemChange();
			}
		}
	}

	internal void Clicked(PointerUpdateKind pointerUpdateKind)
	{
		if (IsGroupHeader)
		{
			if (CollapseEnabled)
			{
				IsExpanded = !IsExpanded;
			}
			else if (HasChildren)
			{
				SetFlyoutOpen(true);
			}
		}
		RaiseItemInvoked(pointerUpdateKind);
	}

	internal void RaiseItemInvoked(PointerUpdateKind pointerUpdateKind)
	{
		Owner?.RaiseItemInvoked(this, pointerUpdateKind);
	}

	private void SidebarDisplayModeChanged(SidebarDisplayMode oldValue)
	{
		var useAnimations = oldValue != SidebarDisplayMode.Minimal;
		switch (DisplayMode)
		{
			case SidebarDisplayMode.Expanded:
				UpdateExpansionState(useAnimations);
				UpdateSelectionState();
				SetFlyoutOpen(false);
				break;
			case SidebarDisplayMode.Minimal:
				UpdateExpansionState(useAnimations);
				SetFlyoutOpen(false);
				break;
			case SidebarDisplayMode.Compact:
				UpdateExpansionState(useAnimations);
				UpdateSelectionState();
				break;
		}
		if (!IsInFlyout)
		{
			if (DisplayMode == SidebarDisplayMode.Compact)
				VisualStateManager.GoToState(this, IsGroupHeader ? "CompactGroupHeader" : "Compact", true);
			else
				VisualStateManager.GoToState(this, "NonCompact", true);
		}
	}

	private void UpdateSelectionState()
	{
		VisualStateManager.GoToState(this, ShouldShowSelectionIndicator() ? "Selected" : "Unselected", true);
		UpdatePointerState();
	}

	private void UpdateIcon()
	{
		Icon = Item?.IconSource is null ? null : CreateSidebarIcon(Item.IconSource);
		if (Icon is not null)
			AutomationProperties.SetAccessibilityView(Icon, AccessibilityView.Raw);
		UpdateIconPresenter();
	}

	private FrameworkElement CreateSidebarIcon(IconSource iconSource)
	{
		if (iconSource is ImageIconSource imageIconSource)
			return CreateArtworkIcon(imageIconSource.ImageSource);

		return iconSource.CreateIconElement();
	}

	private FrameworkElement CreateArtworkIcon(ImageSource? imageSource)
	{
		// Matches the IconPresenter size set in UpdateIconPresenter (isArtwork branch).
		var host = new Grid
		{
			Width = 32,
			Height = 32,
			Tag = "ArtworkIcon"
		};

		var background = new Border
		{
			CornerRadius = new CornerRadius(6),
			Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush")
				?? ResolveBrush("CardBackgroundFillColorDefaultBrush")
				?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x7F, 0x7F, 0x7F))
		};

		var fallbackIcon = new FontIcon
		{
			Glyph = "\uE189",
			FontSize = 10,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = ResolveBrush("TextFillColorSecondaryBrush")
				?? new SolidColorBrush(Microsoft.UI.Colors.Gray)
		};

		host.Children.Add(background);
		host.Children.Add(fallbackIcon);

		if (imageSource != null)
		{
			host.Children.Add(new Border
			{
				CornerRadius = new CornerRadius(6),
				Background = new ImageBrush
				{
					ImageSource = imageSource,
					Stretch = Stretch.UniformToFill
				}
			});
		}

		return host;
	}

	private static Brush? ResolveBrush(string resourceKey)
	{
		return Application.Current?.Resources.TryGetValue(resourceKey, out var resource) == true
			? resource as Brush
			: null;
	}

	private bool ShouldShowSelectionIndicator()
	{
		if (IsExpanded && CollapseEnabled)
		{
			return IsSelected;
		}
		else
		{
			return IsSelected || hasChildSelection;
		}
	}

	private void UpdatePointerState(bool isPointerDown = false)
	{
		var useSelectedState = ShouldShowSelectionIndicator();
		if (isPointerDown)
		{
			VisualStateManager.GoToState(this, useSelectedState ? "PressedSelected" : "Pressed", true);
		}
		else if (isPointerOver)
		{
			VisualStateManager.GoToState(this, useSelectedState ? "PointerOverSelected" : "PointerOver", true);
		}
		else
		{
			VisualStateManager.GoToState(this, useSelectedState ? "NormalSelected" : "Normal", true);
		}
	}

	private void UpdateExpansionState(bool useAnimations = true)
	{
		var model = Item as SidebarItemModel;
		var isSectionHeader = model is { IsSectionHeader: true };
		var showPlaceholder = model is { ShowEmptyPlaceholder: true };

		if (Item?.Children is null || !CollapseEnabled)
		{
			var state = isSectionHeader
				? "SectionHeaderCollapsed"
				: (Item?.PaddedItem == true ? "NoExpansionWithPadding" : "NoExpansion");
			VisualStateManager.GoToState(this, state, useAnimations);
		}
		else if (!HasChildren)
		{
			string state;
			if (isSectionHeader)
			{
				// Section headers reuse Expanded/Collapsed chrome states regardless of children
				// count; the placeholder follows IsExpanded only when ShowEmptyPlaceholder is set.
				state = (showPlaceholder && IsExpanded)
					? "SectionHeaderExpanded"
					: "SectionHeaderCollapsed";
			}
			else if (showPlaceholder)
			{
				state = IsExpanded ? "NoChildrenWithPlaceholderExpanded" : "NoChildrenWithPlaceholderCollapsed";
				VisualStateManager.GoToState(this, IsExpanded ? "ExpandedIconNormal" : "CollapsedIconNormal", useAnimations);
			}
			else
			{
				state = "NoChildren";
			}
			VisualStateManager.GoToState(this, state, useAnimations);
		}
		else
		{
			if (Item?.Children is IList enumerable && enumerable.Count > 0)
			{
				var childHeight = 32d;
				if (childrenRepeater?.ItemsSource is not null)
				{
					var firstChild = childrenRepeater.GetOrCreateElement(0);

					// Collapsed elements might have a desired size of 0 so we need to have a sensible fallback
					childHeight = firstChild.DesiredSize.Height > 0 ? firstChild.DesiredSize.Height : 32d;
				}

				ChildrenPresenterHeight = enumerable.Count * childHeight;
			}
			if (isSectionHeader)
			{
				VisualStateManager.GoToState(this, IsExpanded ? "SectionHeaderExpanded" : "SectionHeaderCollapsed", useAnimations);
			}
			else
			{
				VisualStateManager.GoToState(this, IsExpanded ? "Expanded" : "Collapsed", useAnimations);
				VisualStateManager.GoToState(this, IsExpanded ? "ExpandedIconNormal" : "CollapsedIconNormal", useAnimations);
			}
		}
		UpdateSelectionState();
	}

	private void ItemBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		isPointerOver = true;
		UpdatePointerState();
	}

	private void ItemBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		isPointerOver = false;
		isClicking = false;
		UpdatePointerState();
	}

	private void ItemBorder_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		isClicking = false;
		UpdatePointerState();
	}

	private void ItemBorder_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		isClicking = true;
		UpdatePointerState(true);
		VisualStateManager.GoToState(this, IsExpanded ? "ExpandedIconPressed" : "CollapsedIconPressed", true);
	}

	private void Item_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		if (!isClicking)
			return;

		isClicking = false;
		e.Handled = true;
		UpdatePointerState();

		VisualStateManager.GoToState(this, IsExpanded ? "ExpandedIconNormal" : "CollapsedIconNormal", true);
		var pointerUpdateKind = e.GetCurrentPoint(null).Properties.PointerUpdateKind;
		if (pointerUpdateKind == PointerUpdateKind.LeftButtonReleased ||
			pointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
		{
			Clicked(pointerUpdateKind);
		}
	}

	private async void ItemBorder_DragOver(object sender, DragEventArgs e)
	{
		// Accept drops only when CanDrop allows it
		if (e.DataView.Contains("WaveeTrackIds")
			&& Item is ISidebarItemModel model
			&& _dragStateService?.CurrentPayload is { } payload
			&& model.CanDrop(payload))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			VisualStateManager.GoToState(this, "DragOnTop", true);
			Owner?.RaiseItemDragOver(this, SidebarItemDropPosition.Center, e);
			return;
		}

		if (HasChildren)
		{
			IsExpanded = true;
		}

		var insertsAbove = DetermineDropTargetPosition(e);
		if (insertsAbove == SidebarItemDropPosition.Center)
		{
			VisualStateManager.GoToState(this, "DragOnTop", true);
		}
		else if (insertsAbove == SidebarItemDropPosition.Top)
		{
			VisualStateManager.GoToState(this, "DragInsertAbove", true);
		}
		else if (insertsAbove == SidebarItemDropPosition.Bottom)
		{
			VisualStateManager.GoToState(this, "DragInsertBelow", true);
		}

		Owner?.RaiseItemDragOver(this, insertsAbove, e);
	}

	private void ItemBorder_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
	{
		Owner?.RaiseContextRequested(this, args.TryGetPosition(this, out var point) ? point : default);
		args.Handled = true;
	}

	private void ItemBorder_DragLeave(object sender, DragEventArgs e)
	{
		if (_dragStateService?.IsDragging == true
			&& Item is ISidebarItemModel model
			&& _dragStateService.CurrentPayload is { } payload
			&& model.CanDrop(payload))
			VisualStateManager.GoToState(this, "DragAvailable", true);
		else
			UpdatePointerState();
	}

	private void ItemBorder_Drop(object sender, DragEventArgs e)
	{
		UpdatePointerState();
		Owner?.RaiseItemDropped(this, DetermineDropTargetPosition(e), e);
	}

	private void OnGlobalDragStateChanged(bool isDragging)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			var payload = _dragStateService?.CurrentPayload;
			if (isDragging && payload != null)
			{
				if (Item is ISidebarItemModel model && model.CanDrop(payload))
					VisualStateManager.GoToState(this, "DragAvailable", true);
				else if (_elementBorder != null)
					_elementBorder.Opacity = 0.3;
			}
			else
			{
				if (_elementBorder != null)
					_elementBorder.Opacity = 1.0;
				UpdatePointerState();
			}
		});
	}

	private SidebarItemDropPosition DetermineDropTargetPosition(DragEventArgs args)
	{
		if (UseReorderDrop)
		{
			if (GetTemplateChild("ElementGrid") is Grid grid)
			{
				var position = args.GetPosition(grid);
				if (position.Y < grid.ActualHeight * DROP_REPOSITION_THRESHOLD)
				{
					return SidebarItemDropPosition.Top;
				}
				if (position.Y > grid.ActualHeight * (1 - DROP_REPOSITION_THRESHOLD))
				{
					return SidebarItemDropPosition.Bottom;
				}
				return SidebarItemDropPosition.Center;
			}
		}
		return SidebarItemDropPosition.Center;
	}
}
