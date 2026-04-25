// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using System;

namespace Wavee.UI.WinUI.Controls.SessionTokens;

public class SessionTokenItemRemovingEventArgs : EventArgs
{
    public SessionTokenItemRemovingEventArgs(object item, SessionTokenItem tokenItem)
    {
        Item = item;
        TokenItem = tokenItem;
    }

    public object Item { get; private set; }

    public SessionTokenItem TokenItem { get; private set; }
}
