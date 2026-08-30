using System;

namespace Wavee.Features.Detail;

/// <summary>Track-row count policy for an album drawer while its full track list is loading.</summary>
static class AlbumDrawerRows
{
    internal const int RowCap = 10;
    const int FallbackShimmerRows = 3;

    /// <summary>Honours a usable thin-album count; only unknown counts use the fallback placeholder shape.</summary>
    internal static int PendingCount(int trackCount)
        => trackCount > 0 ? Math.Min(trackCount, RowCap) : FallbackShimmerRows;
}
