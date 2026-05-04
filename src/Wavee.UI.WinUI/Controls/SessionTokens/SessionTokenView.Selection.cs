// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.SessionTokens;

public partial class SessionTokenView : ListViewBase
{
    private enum MoveDirection
    {
        Next,
        Previous
    }

    private bool MoveFocus(MoveDirection direction)
    {
        bool retVal = false;

        if (ItemFromContainer(GetCurrentContainerItem()) is DependencyObject currentItem)
        {
            var previousIndex = Items.IndexOf(currentItem);
            var index = previousIndex;

            if (direction == MoveDirection.Previous)
            {
                if (previousIndex > 0)
                {
                    index -= 1;
                }
                else
                {
                    retVal = true;
                }
            }
            else if (direction == MoveDirection.Next)
            {
                if (previousIndex < Items.Count - 1)
                {
                    index += 1;
                }
            }

            if (index != previousIndex)
            {
                if (ContainerFromIndex(index) is SessionTokenItem newItem)
                {
                    newItem.Focus(FocusState.Keyboard);
                }
                retVal = true;
            }
        }
        return retVal;
    }
}
