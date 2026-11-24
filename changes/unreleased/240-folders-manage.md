---
kind: added
issues: [240]
contract: additive
contract-note: "LocalFolderMenus (a folder's menu and the Folders browser's empty-area menu, built by the core) and an optional LocalCollectionPageProps.FolderMenus"
---
**Manage your folders on On this PC.** The Folders tab's folders are now real cards: they light up on hover and press, show a
play button and a "⋯", take keyboard focus and open with Enter, and can be listed as rows with the same grid | list switch
as Albums and Artists. Right-click a folder (or use its "⋯", or the "⋯" beside Play folder on an open folder) to play,
shuffle, play next or add it to the queue, open it, show it in File Explorer, rescan it, hide it from Local Files, remove a
music folder from your library, make a new folder inside it, rename it, delete it, or see its folder info (where it is,
songs, size, formats, when it was last scanned). Right-click the empty area or the breadcrumb to add a music folder, make a
new folder, rescan or switch the view. Nothing touches your files without asking: renaming and new folders check the name
first and say in plain words what went wrong (a name Windows refuses, a folder by that name, a folder in use), a rename keeps
every song as it was (its links to Spotify included), Delete only ever moves a folder to the Recycle Bin after a confirmation that names it and its
songs (and refuses on drives that have no Recycle Bin), and hiding, removing, renaming and new folders can be undone from
their notification. Empty folders now show too. In all three themes. (#240)
