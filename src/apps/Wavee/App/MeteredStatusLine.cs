using System;
using System.Text;
using FluentGpu.WindowsApi.Network;

namespace Wavee;

/// <summary>The headline state of the "On metered connections" status line — which sentence leads it.</summary>
public enum MeteredStatusKind
{
    /// <summary>The probe failed or NLM reported no cost kind: the app treats this as unmetered and says so, because a
    /// silent card is indistinguishable from "detection works and you are not metered".</summary>
    Unknown,
    /// <summary>NLM reports an unrestricted plan.</summary>
    NotMetered,
    /// <summary>A Fixed/Variable plan, and the cap is lowering the chosen quality.</summary>
    Metered,
    /// <summary>A Fixed/Variable plan, but the chosen quality already sits at or under the cap — the cap changes nothing.</summary>
    MeteredWithinCap,
}

/// <summary>
/// The settings card's metered status line, decided from a <see cref="NetworkCost"/> snapshot: a headline
/// (<see cref="Kind"/>) plus the plan-status suffixes NLM exposes. Pure and engine-free so the decision table is unit
/// tested; the page renders it with <see cref="Render"/> through the loc seam.
/// </summary>
/// <param name="Kind">Which headline leads the line.</param>
/// <param name="OverDataLimit">Append the "over data limit" suffix.</param>
/// <param name="Roaming">Append the "roaming" suffix.</param>
public readonly record struct MeteredStatusLine(MeteredStatusKind Kind, bool OverDataLimit, bool Roaming)
{
    /// <summary>The separator between the headline and each suffix (the same middle dot the settings cards use).</summary>
    public const string Separator = " · ";

    /// <summary>
    /// Decide the line for <paramref name="cost"/>. <paramref name="capInEffect"/> is whether the metered cap is lower
    /// than the user's chosen quality (so it actually bites); it only matters on a metered plan. The limit/roaming bits
    /// are passed through as NLM reports them — they are already false on the fail-soft <see cref="NetworkCost.Unknown"/>.
    /// </summary>
    public static MeteredStatusLine For(NetworkCost cost, bool capInEffect)
    {
        MeteredStatusKind kind = cost.Kind switch
        {
            NetworkCostKind.Fixed or NetworkCostKind.Variable => capInEffect ? MeteredStatusKind.Metered : MeteredStatusKind.MeteredWithinCap,
            NetworkCostKind.Unrestricted => MeteredStatusKind.NotMetered,
            _ => MeteredStatusKind.Unknown,
        };
        return new MeteredStatusLine(kind, cost.OverDataLimit, cost.Roaming);
    }

    /// <summary>The loc key of the headline (<c>settings.playback.meteredStatus.*</c>).</summary>
    public string HeadlineKey => Kind switch
    {
        MeteredStatusKind.Metered => Strings.Settings.Playback.MeteredStatus.Metered,
        MeteredStatusKind.MeteredWithinCap => Strings.Settings.Playback.MeteredStatus.MeteredWithinCap,
        MeteredStatusKind.NotMetered => Strings.Settings.Playback.MeteredStatus.NotMetered,
        _ => Strings.Settings.Playback.MeteredStatus.Unknown,
    };

    /// <summary>Compose the line: headline, then the over-limit and roaming suffixes in that fixed order, each joined
    /// with <see cref="Separator"/>. <paramref name="loc"/> resolves a key to its text (the page passes <c>Loc.Get</c>).</summary>
    public string Render(Func<string, string> loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (!OverDataLimit && !Roaming)
            return loc(HeadlineKey);

        var sb = new StringBuilder(64);
        sb.Append(loc(HeadlineKey));
        if (OverDataLimit) sb.Append(Separator).Append(loc(Strings.Settings.Playback.MeteredStatus.OverLimit));
        if (Roaming) sb.Append(Separator).Append(loc(Strings.Settings.Playback.MeteredStatus.Roaming));
        return sb.ToString();
    }
}
