using System;

namespace Wavee.Features.Detail;

/// <summary>Everything the drawer needs, decided in ONE place from plain inputs, so the panel, the reserved slot height and
/// the bring-into-view scroll can never disagree — and so the verdict is the same whichever component renders first.
/// Selected vs loaded identity is checked HERE: a loaded album that is not the selected one is simply "not loaded".</summary>
public readonly record struct DrawerVerdict(
    string Uri, int Rows, int Columns, int Shown, int Total, bool Loading, bool ReadyEmpty, bool ShowAllRow, float PanelHeight, float SlotHeight);

public static class AlbumDrawerVerdict
{
    public const float HeaderH = 40f;          // 6 pad + 28 header row + 6 pad
    public const float RowPitch = 32f;         // compact list pitch; TrackRow content 28
    // The old single 16-DIP BottomGap split in two: TopGap reserves room ABOVE the panel for the caret that pins the
    // drawer to the clicked card ("this card opened"); BottomGap is the remaining space to the next card row. The sum
    // stays 16, so SlotHeight is unchanged and every pre-existing height number below still holds.
    public const float TopGap = 8f;            // caret band, above the panel
    public const float BottomGap = 8f;         // drawer → next card row
    public const int CapPerColumn = 12;        // 1 column: 12 rows (= 424 DIP, fits a 720p viewport with the card row above)
    public const int TwoColumnMinGridCols = 5; // ≥ 5 album columns ⇒ the drawer is ≥ ~968 DIP wide ⇒ two track columns
    public const int FallbackShimmerRows = 3;

    public static int ColumnsFor(int gridCols) => gridCols >= TwoColumnMinGridCols ? 2 : 1;

    /// <param name="selectedUri">the card the user clicked ("" = closed)</param>
    /// <param name="loadedUri">the uri of the album the resource currently holds (null = nothing)</param>
    /// <param name="loadedTracks">that album's tracks</param>
    /// <param name="thinTracks">tracks already on the discography card itself (often present, sometimes empty)</param>
    /// <param name="thinTrackCount">the card's advertised count (used to size the placeholder before the fetch lands)</param>
    public static DrawerVerdict For(string selectedUri, string? loadedUri, int loadedTracks, int thinTracks, int thinTrackCount,
                                    bool pending, int gridCols)
    {
        if (selectedUri.Length == 0) return default;
        bool match = loadedUri == selectedUri;                        // C1: identity, not "whatever is in hand"
        int have = match ? loadedTracks : thinTracks;                 // never another album's list
        bool loading = !match && pending;                             // pending for THIS uri ⇒ placeholder, even if thin rows exist
        bool readyEmpty = match && !pending && have == 0;
        int columns = ColumnsFor(gridCols);
        int cap = CapPerColumn * columns;
        int total = have > 0 ? have : Math.Max(thinTrackCount, 0);
        int shown = loading ? Math.Min(total > 0 ? total : FallbackShimmerRows, cap) : Math.Min(have, cap);
        bool showAll = !loading && total > cap;
        int rows = readyEmpty ? 2 : (int)Math.Ceiling((shown + (showAll ? 1 : 0)) / (float)columns);
        float panel = HeaderH + rows * RowPitch;
        return new(selectedUri, rows, columns, shown, total, loading, readyEmpty, showAll, panel, panel + TopGap + BottomGap);
    }
}
