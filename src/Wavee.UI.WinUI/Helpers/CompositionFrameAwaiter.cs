using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Helpers;

public static class CompositionFrameAwaiter
{
    public static Task NextFrameAsync()
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            tcs.TrySetResult(null);
        };

        CompositionTarget.Rendering += handler;
        return tcs.Task;
    }
}
