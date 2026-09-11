using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Presentation visibility for mounted sidebar surfaces, including the closed overlay drawer.</summary>
static class SidebarDemandVisibility
{
    public static readonly Context<IReadSignal<bool>?> Slot = new(null);
}
