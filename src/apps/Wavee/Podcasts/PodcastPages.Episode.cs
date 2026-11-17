// ── Podcasts/PodcastPages.Episode.cs — the podcast episode page's core host (podcast pages v2) ── STUB: owned by the episode lane ──

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The episode page's host: the demand, the snapshot, the cells and the verbs.</summary>
// [CoreHost] once it returns Slots.View (the stub draws nothing)
public sealed class PodcastEpisodeScreen : Component
{
    public override Element Render()
    {
        _ = UseProps<PodcastPages.HostProps>();
        return new BoxEl();
    }
}
