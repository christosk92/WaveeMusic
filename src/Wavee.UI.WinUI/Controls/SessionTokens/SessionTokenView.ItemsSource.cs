// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Collections;

#if NET8_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace Wavee.UI.WinUI.Controls.SessionTokens;

public partial class SessionTokenView : ListViewBase
{
    private MethodInfo? _removeItemsSourceMethod;

    protected override void OnItemsChanged(object e)
    {
        _ = (IVectorChangedEventArgs)e;
        base.OnItemsChanged(e);
    }

#if NET8_0_OR_GREATER
    [RequiresUnreferencedCode("This method accesses the 'Remove' method of the assigned items source collection in a trim-unsafe way.")]
#endif
    private void ItemsSource_PropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (ItemsSource != null)
        {
            _removeItemsSourceMethod = ItemsSource.GetType().GetMethod("Remove");
        }
        else
        {
            _removeItemsSourceMethod = null;
        }
    }
}
