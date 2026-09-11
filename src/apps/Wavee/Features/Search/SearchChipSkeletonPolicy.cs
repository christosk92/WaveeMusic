namespace Wavee;

/// <summary>S3 #16: the search facet tab row's loading-SKELETON decision, pulled out of <c>SearchPage.ChipBar</c> so
/// it is testable without a page, a service, or the engine. Show the skeleton pill row only while the query's first
/// response has never landed AND is still genuinely in flight — once a response arrives (even one with zero chips)
/// or a chip source is already cached from an earlier response, the real tab row shows instead, including while a
/// LATER facet-switch fetch is pending (clicking a tab must never re-trigger the skeleton row).</summary>
internal static class SearchChipSkeletonPolicy
{
    internal static bool ShouldShowSkeleton(bool hasChipSource, bool isPending) => !hasChipSource && isPending;
}
