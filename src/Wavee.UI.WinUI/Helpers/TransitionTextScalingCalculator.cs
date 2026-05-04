using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// IScalingCalculator for TransitionHelper that scales a text element by the
/// ratio of the target's FontSize to the source's FontSize, so a large hero
/// title can morph smoothly into a compact header title.
/// </summary>
public sealed class TransitionTextScalingCalculator : IScalingCalculator
{
    public Vector2 GetScaling(UIElement source, UIElement target)
    {
        var sourceText = source?.FindDescendantOrSelf<TextBlock>();
        var targetText = target?.FindDescendantOrSelf<TextBlock>();
        if (sourceText is not null && targetText is not null && sourceText.FontSize > 0)
        {
            var scale = (float)(targetText.FontSize / sourceText.FontSize);
            return new Vector2(scale);
        }

        return new Vector2(1f);
    }
}
