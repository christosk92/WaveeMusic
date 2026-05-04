// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;

#if NET8_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace Wavee.UI.WinUI.Controls.SessionTokens;

[TemplatePart(Name = TokenViewScrollViewerName, Type = typeof(ScrollViewer))]
[TemplatePart(Name = TokenViewScrollBackButtonName, Type = typeof(ButtonBase))]
[TemplatePart(Name = TokenViewScrollForwardButtonName, Type = typeof(ButtonBase))]
public partial class SessionTokenView : ListViewBase
{
    private const string TokenViewScrollViewerName = "ScrollViewer";
    private const string TokenViewScrollBackButtonName = "ScrollBackButton";
    private const string TokenViewScrollForwardButtonName = "ScrollForwardButton";
    private int _internalSelectedIndex = -1;

    private ScrollViewer? _tokenViewScroller;
    private ButtonBase? _tokenViewScrollBackButton;
    private ButtonBase? _tokenViewScrollForwardButton;

    public event EventHandler<SessionTokenItemRemovingEventArgs>? TokenItemRemoving;

#if NET8_0_OR_GREATER
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The 'ItemsSource' change handler accesses the 'Remove' method of the collection in a trim-unsafe (we should revisit this later).")]
#endif
    public SessionTokenView()
    {
        this.DefaultStyleKey = typeof(SessionTokenView);

        RegisterPropertyChangedCallback(ItemsSourceProperty, ItemsSource_PropertyChanged);
        RegisterPropertyChangedCallback(SelectedIndexProperty, SelectedIndex_PropertyChanged);
    }

    protected override DependencyObject GetContainerForItemOverride() => new SessionTokenItem();

    protected override bool IsItemItsOwnContainerOverride(object item)
    {
        return item is SessionTokenItem;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        SelectedIndex = _internalSelectedIndex;
        PreviewKeyDown -= TokenView_PreviewKeyDown;
        SizeChanged += TokenView_SizeChanged;
        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.Loaded -= ScrollViewer_Loaded;
        }

        _tokenViewScroller = GetTemplateChild(TokenViewScrollViewerName) as ScrollViewer;

        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.Loaded += ScrollViewer_Loaded;
        }

        PreviewKeyDown += TokenView_PreviewKeyDown;
        OnIsWrappedChanged();
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        if (element is SessionTokenItem tokenItem)
        {
            tokenItem.Loaded += Token_Loaded;
            tokenItem.Removing += Token_Removing;

            if (tokenItem.IsRemoveable != true && tokenItem.ReadLocalValue(SessionTokenItem.IsRemoveableProperty) == DependencyProperty.UnsetValue)
            {
                var isRemovableBinding = new Binding()
                {
                    Source = this,
                    Path = new PropertyPath(nameof(CanRemoveTokens)),
                    Mode = BindingMode.OneWay,
                };
                tokenItem.SetBinding(SessionTokenItem.IsRemoveableProperty, isRemovableBinding);
            }

            // Convention: if the bound data item exposes an IsLoading bool,
            // forward it to the container's IsLoading DP so the chase-border
            // beam tracks per-item state. WinUI 3 Style Setters with
            // {Binding} silently no-op (resolver scope limitations), so we
            // can't do this from the consumer's XAML — has to be programmatic.
            // Items without an IsLoading property silently no-op the binding,
            // which is fine.
            var isLoadingBinding = new Binding()
            {
                Path = new PropertyPath(nameof(SessionTokenItem.IsLoading)),
                Mode = BindingMode.OneWay,
            };
            tokenItem.SetBinding(SessionTokenItem.IsLoadingProperty, isLoadingBinding);
        }
    }

    private bool RemoveItem()
    {
        if (GetCurrentContainerItem() is SessionTokenItem currentContainerItem && currentContainerItem.IsRemoveable)
        {
            Items.Remove(currentContainerItem);
            return true;
        }
        else
        {
            return false;
        }
    }

    private void UpdateScrollButtonsVisibility()
    {
        if (_tokenViewScrollForwardButton != null && _tokenViewScroller != null)
        {
            if (_tokenViewScroller.ScrollableWidth > 0)
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Visible;
            }
            else
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private SessionTokenItem? GetCurrentContainerItem()
    {
        if (ControlHelpers.IsXamlRootAvailable && XamlRoot != null)
        {
            return FocusManager.GetFocusedElement(XamlRoot) as SessionTokenItem;
        }
        else
        {
            return FocusManager.GetFocusedElement() as SessionTokenItem;
        }
    }

    private void SelectedIndex_PropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_internalSelectedIndex == -1 && SelectedIndex > -1)
        {
            _internalSelectedIndex = SelectedIndex;
        }
    }
}
