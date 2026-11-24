---
kind: added
issues: [240]
contract: additive
contract-note: "LocalCollectionPageProps gains Selected, Pick and Pane; LibraryAlbumPaneProps gains Saveable and Openable; LibraryAlsoByModel gains Heading"
---
**On this PC's Albums and Artists lists show the picked item beside the list, like Library.** In the list view, on a
window wide enough for Library to split, the list stands in a column on the left and clicking a row shows its detail on
the right without leaving the page: an album's cover, name, artist and songs with Play, Shuffle and "Open album", and its
artist's other albums underneath; an artist's songs and albums. The arrow keys move through the list, the pick is
remembered, and clicking the artist under an album shows that artist. On a narrow window, and in the grid view, clicking
an album still opens it as before. (WinUI and Classic; Zune keeps its cover wall.) (#240)
