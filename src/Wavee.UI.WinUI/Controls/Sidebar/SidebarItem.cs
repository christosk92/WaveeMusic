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
	// Tracks the model whose mosaic load is currently in-flight (or just
	// completed) on this container. Lets TryStartLazyIconLoad detect "same
	// model being re-bound" and skip the cancel-and-restart that would
	// otherwise kill an in-progress build mid-realization (visible as
	// playlists in expanded folders never showing their image).
	private SidebarItemModel? _lastLazyIconModel;

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

		// Guaranteed icon rebuild on Loaded. Unloaded nulls iconPresenter.Content
		// + Icon (for memory). HandleItemChange → HookupItemChangeListener calls
		// UpdateIcon, but folder items specifically rely on _themeColorService
		// being re-seated during HookupOwners — if that order varies, the green
		// folder tile never materialises. Calling UpdateIconPresenter here after
		// HookupOwners + HandleItemChange guarantees a full rebuild from the
		// current Item state. Idempotent on pages where the icon was already
		// populated by HookupItemChangeListener.
		UpdateIconPresenter();
	}

	public void HandleItemChange()
	{
		HookupItemChangeListener(null, Item);
		UpdateExpansionState();
		// Reset the per-evaluation caches: the bound Item may have changed
		// identity (container recycle), so the previous IsSelected /
		// containsSelected verdict no longer applies — force ReevaluateSelection
		// to do real work on this rebind.
		_lastAppliedIsSelected = null;
		_lastGroupContainsSelected = null;
		ReevaluateSelection();

		if (Item is not null)
			Decorator = Item.ItemDecorator;

		TryStartLazyIconLoad();
		ReapplyCurrentDisplayModeState(useAnimations: false);
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
		if (Item is not SidebarItemModel model) return;

		// Same model being re-bound (Loaded fires twice in a virtualized
		// expand-then-scroll, or the row was Unloaded+Loaded across the same
		// frame) — don't kill the in-flight build. Without this guard, fast
		// folder-expand cycles cancel mosaic loads mid-flight and the
		// placeholder glyph sticks until app restart.
		if (ReferenceEquals(_lastLazyIconModel, model) && _lazyIconCts is not null)
			return;
		// Already-completed load on this same model — model.IconSource is set
		// and LazyIconSourceLoader is null; nothing more to do.
		if (ReferenceEquals(_lastLazyIconModel, model) && model.LazyIconSourceLoader is null)
			return;

		// Different model than last time — cancel any work tied to the previous one.
		_lazyIconCts?.Cancel();
		_lazyIconCts?.Dispose();
		_lazyIconCts = null;

		_lastLazyIconModel = model;
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

		// Don't cancel an in-flight mosaic load on Unloaded — the build's
		// result is per-model and gets assigned to model.IconSource once it
		// completes, so it's still useful even if THIS container scrolled out.
		// The next time the row scrolls back in, IconSource is already set
		// and TryStartLazyIconLoad's "already-completed" guard skips rework.
		// Just sever this container's tracking pointer; the Task keeps its
		// own CTS alive in its closure and finishes on the thread pool.
		_lazyIconCts = null;
		_lastLazyIconModel = null;

		// Null the icon presenter content so the ImageBrush + BitmapImage
		// referenced by the last-seen model are eligible for GC immediately,
		// instead of waiting for WinUI's container-recycling pool to release
		// them. Each held bitmap is ~30–400 KB; across a long session of
		// rootlist churn this accumulates into tens of MB of deferred-free
		// memory. Re-realization is guaranteed by the explicit
		// UpdateIconPresenter() call in SidebarItem_Loaded (Step 9 Fix A).
		if (iconPresenter is not null)
			iconPresenter.Content = null;
		Icon = null;
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

		var flyoutOwner = GetTemplateChild("ElementGrid") as FrameworkElement;
		if (flyoutOwner is null)
			return;

		// ItemsRepeater can call HandleItemChange/SidebarDisplayModeChanged while
		// preparing a recycled element, before the template root is attached to a
		// XamlRoot. FlyoutBase.GetAttachedFlyout rejects that owner with
		// ArgumentException("element"). If we are only trying to close the flyout,
		// there is nothing visible to close yet, so skip the WinUI call.
		if (!flyoutOwner.IsLoaded || flyoutOwner.XamlRoot is null)
			return;

		if (isOpen)
		{
			FlyoutBase.ShowAttachedFlyout(flyoutOwner);
		}
		else
		{
			(GetTemplateChild("ChildrenFlyout") as FlyoutBase
			 ?? FlyoutBase.GetAttachedFlyout(flyoutOwner))?.Hide();
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

	// Cached results of the previous evaluation. Without these, every
	// SelectedItem change on the Owner re-runs UpdateSelectionState /
	// UpdateExpansionState / SetFlyoutOpen on EVERY realized SidebarItem
	// — even ones whose actual state didn't change. With them, per-nav
	// cost drops from O(realized items) to O(items whose selection
	// actually flipped) — at most 2 (old selected → false, new selected →
	// true). HandleItemChange resets these on container recycle so a
	// rebound row evaluates fresh.
	private bool? _lastAppliedIsSelected;
	private bool? _lastGroupContainsSelected;

	private void ReevaluateSelection()
	{
		if (!IsGroupHeader)
		{
			bool isNowSelected = Item == Owner?.SelectedItem;
			if (_lastAppliedIsSelected == isNowSelected) return;
			_lastAppliedIsSelected = isNowSelected;

			IsSelected = isNowSelected;
			if (IsSelected)
			{
				Owner?.UpdateSelectedItemContainer(this);
			}
		}
		else if (Item?.Children is IList list)
		{
			bool containsSelected = list.Contains(Owner?.SelectedItem);
			if (_lastGroupContainsSelected == containsSelected) return;
			_lastGroupContainsSelected = containsSelected;

			IsSelected = false; // Group headers should never be selected
			if (containsSelected)
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

	private void ReapplyCurrentDisplayModeState(bool useAnimations)
	{
		if (Owner is not null)
			DisplayMode = Owner.DisplayMode;

		SidebarDisplayModeChanged(DisplayMode, useAnimations);
	}

	private void SidebarDisplayModeChanged(SidebarDisplayMode oldValue, bool? useAnimationsOverride = null)
	{
		var useAnimations = useAnimationsOverride ?? oldValue != SidebarDisplayMode.Minimal;
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
			{
				// CompactGroupHeader force-shows ChildrenPresenter so section labels
				// like "Your Library" keep their kids visible as icons in the rail.
				// Folders are ALSO group headers (IsGroupHeader := has children), but
				// they should actually collapse in compact mode — otherwise the whole
				// folder subtree bleeds into the narrow rail. Gate on IsSectionHeader
				// so only true section labels get the force-visible state.
				// Section headers with an ItemDecorator (e.g. Playlists "+") get the
				// WithDecorator variant so the decorator stays clickable in the rail.
				var sectionHeader = Item as SidebarItemModel;
				var isSectionHeader = sectionHeader is { IsSectionHeader: true };
				var hasDecorator = isSectionHeader && sectionHeader!.ItemDecorator is not null;
				var compactState = isSectionHeader
					? (hasDecorator ? "CompactGroupHeaderWithDecorator" : "CompactGroupHeader")
					: "Compact";
				VisualStateManager.GoToState(this, compactState, useAnimations);
			}
			else
			{
				VisualStateManager.GoToState(this, "NonCompact", useAnimations);
				// Compact / CompactGroupHeader forcibly hide layout owned by ExpansionStates
				// (children, placeholder, chevron). Re-assert the expansion state after
				// leaving the rail so expanded groups reliably restore their content.
				UpdateExpansionState(false);
			}
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
			// Fade the artwork in over the placeholder glyph instead of hard-
			// swapping — UpdateIconPresenter replaces content synchronously when
			// the lazy mosaic loader resolves, and a hard swap pops with no
			// transition. Gate the fade on ImageBrush.ImageOpened, NOT Border.Loaded.
			// If the BitmapImage fails (404, auth, network), ImageOpened never fires,
			// so Opacity stays at 0 and the fallback glyph+tile underneath remain
			// visible. Previously the fade ran on Loaded unconditionally — failed
			// loads ended at opacity 1 with a transparent brush covering the
			// fallback, producing the blank gray rectangles for folder-nested /
			// Daily-Mix rows whose images don't actually load.
			var imageBrush = new ImageBrush
			{
				ImageSource = imageSource,
				Stretch = Stretch.UniformToFill
			};
			var artwork = new Border
			{
				CornerRadius = new CornerRadius(6),
				Background = imageBrush,
				Opacity = 0
			};

			void FadeIn()
			{
				var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
				{
					From = 0,
					To = 1,
					Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(250)),
					EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
					{
						EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
					}
				};
				var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
				Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, artwork);
				Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
				sb.Children.Add(fade);
				sb.Begin();
			}

			imageBrush.ImageOpened += (_, _) => FadeIn();
			// Already-decoded BitmapImage (cache hit from a prior row) may not
			// re-fire ImageOpened for this fresh ImageBrush — detect that and fade
			// in from Loaded so the artwork still becomes visible.
			if (imageSource is BitmapImage bmp && bmp.PixelWidth > 0)
			{
				artwork.Loaded += (_, _) => FadeIn();
			}
			// No explicit ImageFailed handler: opacity stays 0, placeholder shows through.

			host.Children.Add(artwork);
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
		if (DisplayMode == SidebarDisplayMode.Compact
			&& Item is SidebarItemModel { IsSectionHeader: true })
		{
			return IsSelected;
		}

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
				state = (showPlaceholder && IsExpanded)
					? "SectionHeaderExpandedWithPlaceholder"
					: (IsExpanded ? "SectionHeaderExpanded" : "SectionHeaderCollapsed");
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
					// TryGetElement (not GetOrCreateElement): this path runs
					// inside the measure cascade when DisplayMode changes
					// during a splitter drag (SidebarView.UpdateDisplayModeForPaneWidth
					// → set_DisplayMode → SidebarDisplayModeChanged).
					// GetOrCreateElement forces realisation; invoking it while
					// the repeater is mid-layout throws COMException("Element is
					// already the child of another element") because the element
					// tree is already being measured on another branch.
					// TryGetElement returns the existing element or null, so we
					// safely fall through to the 32d default when no container
					// is realised yet (collapsed / never-expanded subtree).
					var firstChild = childrenRepeater.TryGetElement(0) as FrameworkElement;

					// Collapsed elements might have a desired size of 0 so we need to have a sensible fallback
					if (firstChild is not null && firstChild.DesiredSize.Height > 0)
						childHeight = firstChild.DesiredSize.Height;
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
		// Capture position in SidebarView coordinates (the Owner). The shell
		// targets the SidebarView when opening the flyout, so the position has
		// to be in the same coordinate space — using `this` (the SidebarItem)
		// produced offsets that anchored the flyout at the top of the sidebar
		// regardless of where the user actually right-clicked.
		var reference = (UIElement?)Owner ?? this;
		Owner?.RaiseContextRequested(this, args.TryGetPosition(reference, out var point) ? point : default);
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
