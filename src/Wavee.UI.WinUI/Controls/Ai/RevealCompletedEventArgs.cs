using System;

namespace Wavee.UI.WinUI.Controls.Ai;

public sealed class RevealCompletedEventArgs : EventArgs
{
    public RevealCompletedEventArgs(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
