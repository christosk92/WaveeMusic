using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Extensions;

internal static class ListExtensions
{
    public static void Shuffle<T>(this IList<T> list)
    {
        var rng = Random.Shared;
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
