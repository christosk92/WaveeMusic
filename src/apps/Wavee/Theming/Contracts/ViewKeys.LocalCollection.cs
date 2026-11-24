// ── Theming/Contracts/ViewKeys.LocalCollection.cs — the "On this PC" collection page's key (#240 P2, boards 18-21) ───────────

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Local Files route's page, "On this PC": the collection of the user's own music (#240 P2, boards 18-21).</summary>
    public static readonly ViewKey<LocalCollectionPageProps> LocalCollectionPage = new("Pages.LocalCollection");
}
