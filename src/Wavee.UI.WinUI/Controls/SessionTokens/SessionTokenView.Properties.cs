// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.SessionTokens;

public partial class SessionTokenView : ListViewBase
{
    public static readonly DependencyProperty IsWrappedProperty = DependencyProperty.Register(
        nameof(IsWrapped),
        typeof(bool),
        typeof(SessionTokenView),
        new PropertyMetadata(defaultValue: false, (d, e) => ((SessionTokenView)d).OnIsWrappedPropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    public static readonly DependencyProperty CanRemoveTokensProperty = DependencyProperty.Register(
      nameof(CanRemoveTokens),
      typeof(bool),
      typeof(SessionTokenView),
      new PropertyMetadata(defaultValue: false, (d, e) => ((SessionTokenView)d).OnCanRemoveTokensPropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    public bool CanRemoveTokens
    {
        get => (bool)GetValue(CanRemoveTokensProperty);
        set => SetValue(CanRemoveTokensProperty, value);
    }

    public bool IsWrapped
    {
        get => (bool)GetValue(IsWrappedProperty);
        set => SetValue(IsWrappedProperty, value);
    }

    protected virtual void OnIsWrappedPropertyChanged(bool oldValue, bool newValue)
    {
        OnIsWrappedChanged();
    }

    protected virtual void OnCanRemoveTokensPropertyChanged(bool oldValue, bool newValue)
    {
        OnCanRemoveTokensChanged();
    }

    private void OnCanRemoveTokensChanged()
    {
    }
}
