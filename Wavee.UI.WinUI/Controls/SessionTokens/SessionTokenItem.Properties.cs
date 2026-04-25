// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.SessionTokens;

public partial class SessionTokenItem : ListViewItem
{
    public static readonly DependencyProperty IsRemoveableProperty =
        DependencyProperty.Register(nameof(IsRemoveable), typeof(bool), typeof(SessionTokenItem),
            new PropertyMetadata(defaultValue: false, (d, e) => ((SessionTokenItem)d).OnIsRemoveablePropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(IconElement),
        typeof(SessionTokenItem),
        new PropertyMetadata(defaultValue: null, (d, e) => ((SessionTokenItem)d).OnIconPropertyChanged((IconElement)e.OldValue, (IconElement)e.NewValue)));

    /// <summary>
    /// Extension: drives the chase-around-border loading animation on the token.
    /// Set true while the server is processing a click; set false when the response
    /// is applied or an error reverts the selection.
    /// </summary>
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading),
        typeof(bool),
        typeof(SessionTokenItem),
        new PropertyMetadata(defaultValue: false, (d, e) => ((SessionTokenItem)d).OnIsLoadingPropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    public bool IsRemoveable
    {
        get => (bool)GetValue(IsRemoveableProperty);
        set => SetValue(IsRemoveableProperty, value);
    }

    public IconElement Icon
    {
        get => (IconElement)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }
}
