# Local Files page: from a playlist to "your collection" (concept, 2026-11-23)

Owner: the Local Files page "looks like a generic playlist; make it a bit nicer". Today it is the playlist page's local
arm (`Playlist.Page.cs` route `local`): a grey folder square, "Music imported from this computer", an "OD · On this
device" owner line, Play/Shuffle and a track table. That framing is wrong for a library of thousands of songs the user
owns: a playlist is a list somebody made; a collection is a place you browse.

## What the loved players do (research, 2026-11-23)

- **Roon:** the home screen opens with **your collection's own numbers** (artists, albums, tracks, composers) "right at
  the top", and since 1.8 a visualisation of your listening and "Recent activity". Users ask most for control over the
  layout and complain about wasted space.
- **Plexamp:** a home made of **hubs** (shelves) the user can customise: recently added, recently played, library radio
  and "time travel" (decades); praised for local discovery.
- **Doppler (Mac/iPhone):** **album- and artist-first**; "Recently Added" is the main door into new music; clean,
  native, few controls.
- **Zune (and the Zune theme Wavee already ships):** "collection" as a place, big pivots (artists · albums · songs ·
  genres), cover mosaics.
- **Apple Music / Windows Media Player:** Library sections (Recently Added, Albums, Artists, Songs) with an album grid
  as the default view.

Sources: Roon community and blog (home screen counts, 1.8 home), Plex forums (Plexamp home customisation), MacStories,
9to5Mac and iDownloadBlog reviews of Doppler. Links are in the conversation that produced this file.

## The concept: "On this PC", a collection with a home

### 1. Hero: the collection, not a cover square
- A **living cover mosaic** of the user's own albums (recently added and most played), softly blurred into the page
  ground, replacing the grey folder square. With no covers yet, a calm gradient.
- Title **"On this PC"** (sidebar label stays "Local Files"), and one stats line in the Roon spirit: "6,016 songs ·
  512 albums · 287 artists · 312 hours · 57% lossless".
- **Folder chips** under it: "D:\Music ✓", "\\nas\share\flac · offline", "+ Add folder"; the library activity state
  lives in the hero ("Up to date · scanned 2 min ago" or the live scan bar), so S1 and the page agree.
- Actions: **Play all**, **Shuffle**, **Add music**.

### 2. Pivots instead of one list
**Home · Albums · Artists · Songs · Folders · Insights** (Zune/Apple Music library sections). The page remembers the
last pivot. Songs is today's track table (dense, sortable, with optional Format/Quality columns); Insights is S10.

### 3. Home: hubs of shelves (Plexamp/Doppler)
- **Recently added**, grouped by when: Today · This week · Earlier this month (album covers, "New" tag the first day).
- **Jump back in**: local albums played recently.
- **Lossless & hi-res**: albums with a quality pill (FLAC · 24/96 Hi-Res).
- **From your playlists**: the local songs your Spotify playlists list, with "52 of 56 play" and the 4 not found.
- **Rediscover**: albums not played in six months.
- **Shuffle by decade** chips (1970s · 1980s · 1990s · 2000s · 2010s · 2020s), from the year tags (Plexamp's time
  travel).
- Shelves can be hidden and reordered (the most-asked Roon request), from a small "Edit home" affordance.

### 4. Albums: a cover wall
Large covers, artist under the title, quality pill on hover/focus, year dividers when sorted by year; sort: Recently
added · Artist · Title · Year. Tracks of one album share one cover file (§3.6a).

### 5. Folders: browse the way the files are laid out
Unique to local music: many people think in folders. A breadcrumb (On this PC › D:\Music › Coral Hours), folder tiles
with their cover.jpg (or a 2×2 mosaic of the covers inside), file counts and total length, and the songs of the
current folder below. Play or shuffle any folder.

### 6. Quality, shown quietly
A small pill wherever it helps: "FLAC", "Hi-Res 24/96", "MP3 320". Never shouted; consistent with the player bar's
existing "Lossless" mark.

### Themes
- **WinUI:** the mosaic hero with acrylic over it, the pivot as a WinUI SelectorBar, shelves as horizontal scrollers.
- **Zune:** "collection" in huge lowercase type over a full-bleed cover mosaic; pivots "home · albums · artists · songs
  · folders"; shelves as big cover rows with lowercase captions; numbers in display type.
- **Classic:** uses the WinUI layout. The owner is converging Classic and WinUI, so there is no Classic-only design.

### What it changes in the plan
§3.10 (the Local Files list) becomes a **collection page** with its own route and pivots; the track table moves into
the Songs pivot unchanged; Insights moves in as a pivot. Albums and Artists here are built from the index's tags
(names, not Spotify entities; after 0.4 they can become browsable local entities, as the plan's After list says).
Size: an extra M in P2 (hero, pivots, Home shelves, Albums wall) and S (Folders), all reading `LocalIndex`.
