using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>Engine-free shaping rules shared by the playlist Tune command and headless tests.</summary>
internal static class PlaylistTuneMenuModel
{
    public static bool IsEligible(PlaylistTuning? tuning, bool sourceAvailable)
    {
        if (!sourceAvailable || tuning is null) return false;
        for (int i = 0; i < tuning.Available.Count; i++)
        {
            var option = tuning.Available[i];
            if (option.Kind == PlaylistTuningOptionKind.Choice && !string.IsNullOrWhiteSpace(option.DisplayName))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<PlaylistTuningOption> VisibleChoices(PlaylistTuning tuning)
    {
        var visible = new List<PlaylistTuningOption>(tuning.Available.Count);
        for (int i = 0; i < tuning.Available.Count; i++)
        {
            var option = tuning.Available[i];
            if (option.Kind == PlaylistTuningOptionKind.Choice && !string.IsNullOrWhiteSpace(option.DisplayName))
                visible.Add(option);
        }
        return visible;
    }

    public static PlaylistTuningOption? ResetOption(PlaylistTuning tuning)
    {
        if (tuning.SelectedIdentifier is null) return null;
        for (int i = 0; i < tuning.Available.Count; i++)
            if (tuning.Available[i].Kind == PlaylistTuningOptionKind.Reset)
                return tuning.Available[i];
        return null;
    }
}
