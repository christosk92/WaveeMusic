---
kind: changed
issues: []
contract: additive
contract-note: "ViewKeys.LocalCollectionPage (Pages.LocalCollection) and LocalCollectionPageProps with its view records (LocalCollectionView, LocalAlbumCard, LocalArtistCard, LocalFolderNode, LocalSongItem, LocalShelfGroup, LocalDecade, LocalFolderChip, LocalShare, LocalInsights, LocalCollectionVerbs) and enums (LocalPivot, LocalAlbumSort, LocalLibraryPhase, LocalChipState, LocalAddedWhen); the props carry the Local Files onboarding, first-scan flag and scan line as delegates"
---
**Local Files is now "On this PC", a page for your own collection.** The sidebar's Local Files opens a page built from
your music folders: a hero with a mosaic of your album covers, the collection's numbers (songs, albums, artists, hours,
how much is lossless), a chip per music folder with its state and the library's activity, and Play all, Shuffle and Add
music. Pivots switch between Home (Recently added grouped by today, this week and earlier this month; your lossless
albums; Shuffle by decade from your year tags), an Albums cover wall sortable by recently added, artist, title or year,
Artists, Songs (the familiar track list), a Folders browser that follows how the files are laid out on disk, and
Insights (formats and bitrates, tag health, what can't be played, per folder counts; computed on this PC only). The
page remembers the last pivot. Albums without art show a tile in their own colour with their initial. Classic and
WinUI share one layout; Zune shows "collection" in its large type, with the pivots and verbs in its band. (#240.)
