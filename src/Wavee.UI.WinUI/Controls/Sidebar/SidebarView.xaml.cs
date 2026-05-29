// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Helpers.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Wavee.UI.WinUI.Controls.Sidebar;

[ContentProperty(Name = "InnerContent")]
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class SidebarView : UserControl, INotifyPropertyChanged
{
	private const double MinExpandedPaneWidth = 200;
	private const double CompactPaneLength = 56; // == SidebarCompactOpenPaneLength resource

	public event EventHandler<ItemInvokedEventArgs>? ItemInvoked;
	public event EventHandler<ItemContextInvokedArgs>? ItemContextInvoked;
	public event EventHandler<ItemDroppedEventArgs>? ItemDropped;
	public event EventHandler<SidebarItemModel>? PinButtonClicked;
	public event PropertyChangedEventHandler? PropertyChanged;

	internal SidebarItem? SelectedItemContainer = null;

	private bool draggingSidebarResizer;
	private double preManipulationSidebarWidth = 0;
	private DragStateService? _dragStateService;
	private Storyboard? _contentOffsetStoryboard;
	private bool _suppressPaneTransition = true; // first layout / startup is always instant

	public SidebarView()
	{
		InitializeComponent();
	}

	internal void UpdateSelectedItemContainer(SidebarItem container)
	{
		SelectedItemContainer = container;
	}

	internal void RaiseItemInvoked(SidebarItem item, PointerUpdateKind pointerUpdateKind)
	{
		// Only leaves can be selected
		if (item.Item is null || item.IsGroupHeader) return;
		if (item.Item is SidebarItemModel { IsEnabled: false }) return;

		SelectedItem = item.Item;
		ItemInvoked?.Invoke(item, new(pointerUpdateKind));
	}

	internal void RaiseContextRequested(SidebarItem item, Point e)
	{
		ItemContextInvoked?.Invoke(item, new(item.Item, e));
	}

	internal void RaiseItemDropped(SidebarItem sideBarItem, SidebarItemDropPosition dropPosition, DragEventArgs rawEvent)
	{
		if (sideBarItem.Item is null) return;
		ItemDropped?.Invoke(this, new(sideBarItem.Item, rawEvent.DataView, dropPosition, rawEvent));
	}

	internal void RaisePinButtonClicked(SidebarItemModel model)
	{
		PinButtonClicked?.Invoke(this, model);
	}

	private void UpdateMinimalMode()
	{
		if (DisplayMode != SidebarDisplayMode.Minimal) return;

		if (IsPaneOpen)
		{
			VisualStateManager.GoToState(this, "MinimalExpanded", true);
		}
		else
		{
			VisualStateManager.GoToState(this, "MinimalCollapsed", true);
		}
	}

	private void UpdateDisplayMode()
	{
		// Discrete toggles get compositor-friendly content motion. Startup and
		// live resize-drag stay instant.
		bool animate = !_suppressPaneTransition
			&& !draggingSidebarResizer
			&& Visibility == Visibility.Visible;

		switch (DisplayMode)
		{
			case SidebarDisplayMode.Compact:
				VisualStateManager.GoToState(this, "Compact", true);
				ApplyPaneWidth(CompactPaneLength, animate);
				return;
			case SidebarDisplayMode.Expanded:
				VisualStateManager.GoToState(this, "Expanded", true);
				// For users whose saved OpenPaneLength is something other than the
				// default (e.g. resized to 320), restore their last width.
				ApplyPaneWidth(OpenPaneLength, animate);
				return;
			case SidebarDisplayMode.Minimal:
				IsPaneOpen = false;
				UpdateMinimalMode();
				return;
		}
	}

	private void ApplyPaneWidth(double targetWidth, bool animate)
	{
		double currentWidth = PaneColumnDefinition.Width.Value;
		if (double.IsNaN(currentWidth) || currentWidth <= 0)
			currentWidth = PaneColumnGrid.ActualWidth;

		double currentOffset = ContentCardShadowHostTransform.TranslateX;
		double fromOffset = currentWidth + currentOffset - targetWidth;

		_contentOffsetStoryboard?.Stop();
		PaneColumnDefinition.Width = new GridLength(targetWidth);

		if (!animate || Math.Abs(fromOffset) < 0.5)
		{
			ResetContentOffset();
			return;
		}

		AnimateContentOffsetFrom(fromOffset);
	}

	private void AnimateContentOffsetFrom(double fromOffset)
	{
		ContentCardShadowHostTransform.TranslateX = fromOffset;
		SidebarResizerTransform.TranslateX = fromOffset;

		var storyboard = new Storyboard();
		storyboard.Children.Add(CreateOffsetAnimation(ContentCardShadowHostTransform, fromOffset));
		storyboard.Children.Add(CreateOffsetAnimation(SidebarResizerTransform, fromOffset));
		storyboard.Completed += (_, _) =>
		{
			if (!ReferenceEquals(_contentOffsetStoryboard, storyboard))
				return;

			_contentOffsetStoryboard = null;
			ResetContentOffset();
		};

		_contentOffsetStoryboard = storyboard;
		storyboard.Begin();
	}

	private static DoubleAnimationUsingKeyFrames CreateOffsetAnimation(CompositeTransform target, double fromOffset)
	{
		var anim = new DoubleAnimationUsingKeyFrames();
		anim.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = fromOffset });
		anim.KeyFrames.Add(new SplineDoubleKeyFrame
		{
			KeyTime = TimeSpan.FromMilliseconds(180),
			Value = 0,
			KeySpline = new KeySpline
			{
				ControlPoint1 = new Point(0.1, 0.9),
				ControlPoint2 = new Point(0.2, 1.0),
			},
		});
		Storyboard.SetTarget(anim, target);
		Storyboard.SetTargetProperty(anim, nameof(CompositeTransform.TranslateX));
		return anim;
	}

	private void ResetContentOffset()
	{
		ContentCardShadowHostTransform.TranslateX = 0;
		SidebarResizerTransform.TranslateX = 0;
	}

	private void UpdateDisplayModeForPaneWidth(double newPaneWidth)
	{
		OpenPaneLength = Math.Max(newPaneWidth, MinExpandedPaneWidth);
		DisplayMode = SidebarDisplayMode.Expanded;
	}

	private void UpdateOpenPaneLengthColumn()
	{
		// OpenPaneLength is the "last known expanded width". In Compact/Minimal,
		// the display-mode logic owns PaneColumnDefinition.Width; applying
		// OpenPaneLength here would clobber the Compact-mode 56 px width and produce
		// a wide pane with Compact-mode items inside, which is what users saw on app
		// re-open after closing with the sidebar collapsed.
		if (DisplayMode != SidebarDisplayMode.Expanded)
			return;
		PaneColumnDefinition.Width = new GridLength(OpenPaneLength);
	}

	private void SidebarView_Loaded(object sender, RoutedEventArgs e)
	{
		// The initial layout (incl. a persisted Compact or custom-width restore)
		// must be applied instantly.
		_suppressPaneTransition = true;
		UpdateDisplayMode();
		ResetContentOffset();
		PaneColumnGrid.Translation = new System.Numerics.Vector3(0, 0, 32);

		_dragStateService = Ioc.Default.GetService<DragStateService>();
		if (_dragStateService != null)
			_dragStateService.DragStateChanged += OnDragStateChanged;

		// Scroll viewer owns the gap's drop fallback so the displaced-row hit-test
		// hole is still droppable (see SidebarView.Reorder.cs).
		AttachReorderSurface();

		Unloaded += SidebarView_Unloaded;

		// Arm the transition - every later display-mode toggle now gets motion.
		_suppressPaneTransition = false;
	}

	private void SidebarView_Unloaded(object sender, RoutedEventArgs e)
	{
		if (_dragStateService != null)
			_dragStateService.DragStateChanged -= OnDragStateChanged;
	}

	private void OnDragStateChanged(bool isDragging)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			ContentCardBorder.Opacity = isDragging ? 0.3 : 1.0;
			if (!isDragging)
			{
				StopReorderAutoScroll();
				// Authoritative drag-end: clear any gap left open if the drag ended
				// off any row (cancel, drop on empty space) where no row's Drop fired.
				ClearReorderGap(animate: false);
			}
		});
	}

	// ── Reorder auto-scroll (rbd quadratic edge ramp) ─────────────────────
	// The sidebar reorder gesture stays OLE-driven (it must hit-test across rows
	// for nest / drop-onto-playlist / move-to-root), but it adopts the same
	// edge auto-scroll the in-list ReorderController uses. SidebarItem.DragOver
	// forwards the raw event here; we project the pointer into the scroll
	// viewport and drive ReorderAutoScroller.

	private Reorder.ReorderAutoScroller? _reorderAutoScroller;

	internal void UpdateReorderAutoScroll(DragEventArgs e)
	{
		if (MenuItemHostScrollViewer is not { } sv) return;
		_reorderAutoScroller ??= new Reorder.ReorderAutoScroller(
			delta => sv.ChangeView(null, sv.VerticalOffset + delta, null, true),
			_ => { /* OLE re-raises DragOver as content shifts; no manual re-project needed */ });
		var y = e.GetPosition(sv).Y;
		_reorderAutoScroller.Update(y, sv.ActualHeight);
	}

	internal void StopReorderAutoScroll() => _reorderAutoScroller?.Stop();

	private void SidebarResizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
	{
		draggingSidebarResizer = true;
		preManipulationSidebarWidth = PaneColumnGrid.ActualWidth;
		VisualStateManager.GoToState(this, "ResizerPressed", true);
		e.Handled = true;
	}

	private void SidebarResizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
	{
		var newWidth = preManipulationSidebarWidth + e.Cumulative.Translation.X;
		UpdateDisplayModeForPaneWidth(newWidth);
		e.Handled = true;
	}

	private void SidebarResizerControl_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if
		(
			e.Key != VirtualKey.Space &&
			e.Key != VirtualKey.Enter &&
			e.Key != VirtualKey.Left &&
			e.Key != VirtualKey.Right &&
			e.Key != VirtualKey.Control
		)
			return;

		var primaryInvocation = e.Key == VirtualKey.Space || e.Key == VirtualKey.Enter;
		if (DisplayMode == SidebarDisplayMode.Expanded)
		{
			if (primaryInvocation)
			{
				DisplayMode = SidebarDisplayMode.Compact;
				return;
			}

			var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
			var increment = ctrl.HasFlag(CoreVirtualKeyStates.Down) ? 5 : 1;

			// Left makes the pane smaller so we invert the increment
			if (e.Key == VirtualKey.Left)
				increment = -increment;

			var newWidth = OpenPaneLength + increment;
			UpdateDisplayModeForPaneWidth(newWidth);
			e.Handled = true;
			return;
		}
		else if (DisplayMode == SidebarDisplayMode.Compact)
		{
			if (primaryInvocation || e.Key == VirtualKey.Right)
			{
				DisplayMode = SidebarDisplayMode.Expanded;
				e.Handled = true;
			}
		}
	}

	private void PaneLightDismissLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		IsPaneOpen = false;
		e.Handled = true;
	}

	private void PaneLightDismissLayer_Tapped(object sender, TappedRoutedEventArgs e)
	{
		IsPaneOpen = false;
		e.Handled = true;
	}

	private void SidebarResizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (DisplayMode == SidebarDisplayMode.Expanded)
		{
			DisplayMode = SidebarDisplayMode.Compact;
			e.Handled = true;
		}
		else
		{
			DisplayMode = SidebarDisplayMode.Expanded;
			e.Handled = true;
		}
	}

	private void SidebarResizer_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		var sidebarResizer = (FrameworkElement)sender;
		sidebarResizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
		VisualStateManager.GoToState(this, "ResizerPointerOver", true);
		e.Handled = true;
	}

	private void SidebarResizer_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (draggingSidebarResizer)
			return;

		var sidebarResizer = (FrameworkElement)sender;
		sidebarResizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
		VisualStateManager.GoToState(this, "ResizerNormal", true);
		e.Handled = true;
	}

	private void SidebarResizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
	{
		draggingSidebarResizer = false;
		VisualStateManager.GoToState(this, "ResizerNormal", true);
		e.Handled = true;
	}

	private void MenuItemHostScrollViewer_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		ItemContextInvoked?.Invoke(this, new(null, e.TryGetPosition(this, out var point) ? point : default));
		e.Handled = true;
	}

	private void MenuItemsHost_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
	{
		if (args.Element is SidebarItem sidebarItem)
		{
			sidebarItem.HandleItemChange();
		}
	}
}
