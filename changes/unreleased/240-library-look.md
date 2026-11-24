---
kind: changed
issues: [240]
contract: additive
contract-note: "LocalArtistSort (new); LocalCollectionPageProps gains ArtistSort, SetArtistSort and the writable cells ListView, ListSize and Filter (Func<Signal<T>>); LibraryActivity gains Running, LineKind and PageLine (app types, not theme contract)"
---
**"On this PC" now looks like the rest of your library.** The page opens with the same plain title as Library, the
collection's counts under it ("2 songs · 1 artist · 4 minutes", never "0 albums"), your music folders and the scan
status, and Play all, Shuffle and Add music beside the title. Albums and Artists have Library's toolbar: a "Recently
added ⌄" sort menu, a Filter box and the grid/list switch with its view and size menu, and they show the same cards and
rows as Library and Home. Music without cover art shows the usual placeholder instead of a big coloured letter. The
status under the title and the one in the sidebar now always agree, and the header no longer jumps when a folder or the
scan line appears. (WinUI and Classic; Zune keeps its own look.) (#240)
