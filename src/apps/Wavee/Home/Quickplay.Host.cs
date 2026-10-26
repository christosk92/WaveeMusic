// ── Home/Quickplay.Host.cs — the quickplay strip's core host (`Home.Quickplay`, contract 1.28, P5 Z5) ───────────────────────────
//
// Role: CORE host (the picture is the structure's `Home.Quickplay` view: Zune's `Views\Home\ZuneHomeQuickplay.cs`; Classic registers Nothing)
//
// A structure that heads Home with a quickplay strip (Zune: "pins · history · new", board Z-Home) mounts this host where it wants the strip,
// with the facet's Recents zone and its release-list zone. The host hands the view what a theme may not reach for itself (WVTH002):
//   · the pins: the band's own tracked read (`Shell.PageChromeChannel.Band.Pins`: the user's order, capped by the structure's MaxBandPins);
//   · the history: the Recents zone's body through ITS core host (`RecentGrid`, mounting `Home.Zone.RecentGrid`), so the strip reads
//     Recents exactly as the Recently-played grid does and a structure draws the history once, under that key;
//   · the "new" group: the release-list zone as it is.
// The pins' edit place is none yet (a band-style structure has no nav tree to edit them in): `EditPins` stays null.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee.HomeUi;

/// <summary>Props for <see cref="QuickplayHost"/>: the zones by value (a re-plan with equal content is a no-op at the embed boundary), the
/// overlay service by reference.</summary>
/// <param name="History">The facet's Recents zone, or null.</param>
/// <param name="Fresh">The facet's release-list zone, or null.</param>
/// <param name="Overlay">The overlay service the cards' menus open on.</param>
public sealed record QuickplayHostProps(Zone? History, Zone? Fresh, IOverlayService? Overlay)
{
    public bool Equals(QuickplayHostProps? o) => o is not null && Equals(History, o.History) && Equals(Fresh, o.Fresh) && ReferenceEquals(Overlay, o.Overlay);
    public override int GetHashCode() => HashCode.Combine(History, Fresh);
}

/// <summary>The quickplay strip's host: mounts <see cref="ViewKeys.HomeQuickplay"/> with the pins, the history's own host and the release zone.</summary>
[CoreHost]
public sealed class QuickplayHost : Component
{
    static readonly Func<IReadOnlyList<BandPin>> s_pins = static () => Shell.PageChromeChannel.Band.Pins();

    public override Element Render()
    {
        var p = UseProps<QuickplayHostProps>();
        Element? history = p.History is { } zone
            ? Embed.Comp(new RecentGridProps(zone), static () => new RecentGrid()) with { Key = "quickplay:history" }
            : null;
        return Slots.View(ViewKeys.HomeQuickplay, new HomeQuickplayProps
        {
            Pins = s_pins, EditPins = null, History = history, Fresh = p.Fresh, Overlay = p.Overlay,
        });
    }
}
