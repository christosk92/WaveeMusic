# Your files and Spotify, together: research, people, stories and flows (2026-11-23)

Owner's ideas: (1) auto-tag local files from Spotify (search or your own collection): metadata and images; (2) optionally
link a local song to a Spotify track, for example after auto-tagging, so that a local remix that is not on Spotify can
live as a **version** of the Spotify track, under the row's expander chevron as the attached music video does today.
This file defines the problem before any screen is drawn. Nothing here is scheduled yet.

## 1. Research: how others do it, and what people complain about

| Who | What they do | Lesson for Wavee |
|---|---|---|
| **Spotify (official app)** | Silently tries to match every local file to its catalogue and then plays the *Spotify* version. Community threads: a **live version plays instead of the studio track** I own; phone and desktop disagree; synced copies swap to another recording; some say the feature was dropped, staff say it still runs "and may or may not work". Other users ask for the opposite: replace my local files with Spotify versions. Local FLAC/WAV show no art or metadata; art over 300 px is ignored. | **Silent swapping destroys trust.** Every link must be visible, explainable and reversible, and the user decides which source plays. People want both directions. Wavee already wins on FLAC tags and big covers. |
| **iTunes / Apple Music Match** | Two visible states per song: **Matched** (catalogue copy) or **Uploaded** (yours). Matching works per song, so one album can be half matched. Matched copies are 256 kbps AAC, so a lossless original is *replaced* by a lossy one in the cloud. | Show a clear per-song status. Never let a link downgrade quality: your FLAC stays the source unless you choose otherwise. |
| **Plex** | Album-level **Fix Match**; "**Prefer local metadata**" makes your tags and artwork win. Users report the preference not sticking and Fix Match not updating tracks. | Album-first matching; one simple "whose info wins" choice that *sticks*; fixing a wrong match must update everything at once. |
| **Roon** | Identifies albums against canonical releases (track order, disc numbers, durations). "**Prefer File**" per field and per object; users find it inconsistent and confusing; a wrong release cannot be fixed by preferring file data, only by un-identifying. | Album structure is the strongest signal. Per-field preferences are too much; offer "Spotify's info" vs "your file's info" per album or song, plus manual edits. |
| **MusicBrainz Picard / beets** | Cluster by album → look up by metadata → **acoustic fingerprint (AcoustID) as a fallback**; a match score with a threshold; the user reviews. Users find Lookup vs Scan confusing. | A clear confidence level instead of jargon; one button; a fingerprint fallback later for files with no useful tags. |
| **Apple Music "Other Versions" / YouTube Music "Other performances"** | Apple groups remasters, remixes, demos and deluxe editions under the album; YouTube Music lists other performances (live, covers, remixes) per song from Now Playing. A request elsewhere asks to **link versions the system did not catch**. | Song-level **Versions** fits Wavee's track drawer; manual linking of versions is a real, asked-for need. |

Sources: Spotify Community threads ("Replace local files with Spotify versions", "Play local file instead of Spotify
version", "Stop linking up local files to Spotify catalog", "Local files not showing album art"); MacRumors and Macworld
on iTunes Match; Plex support "Correcting your music content matches" and forum threads; Roon help "Editing and
Grooming Your Collection" and community threads; Picard docs (workflows, Lookup, Scan); MacRumors on Apple Music
alternate versions; 9to5Google on YouTube Music "Other performances"; a Chartmetric feature request. Links are in the
conversation that produced this file.

## 2. The people

| Person | Their music on disk | What they want | What scares them |
|---|---|---|---|
| **The collector** | 6,000 FLAC songs ripped from CDs and bought on Bandcamp, tidy folders | Their files to look as good as Spotify's (covers, names, credits) and to *hear their lossless copy* | A wrong match; their tags being overwritten; a lossy stream replacing their file |
| **The digger / DJ** | Extended mixes, edits, bootleg remixes from SoundCloud and Beatport | Their versions next to the original in playlists, and a way to make "their" version the default | Losing the link between a remix and its song; the app treating the remix as a duplicate |
| **The casual listener** | A few hundred old MP3s, songs that vanished from Spotify or are region-locked | Local songs in playlists that look normal (cover, title), lyrics if possible | Any extra work; grey rows they don't understand |
| **The archivist / fan** | Live bootlegs, demos, unreleased tracks of a few artists | Their rarities shown with the artist, "not on Spotify" as a badge of honour | Wavee "fixing" a demo into the album version |
| **The issue reporter (0dm)** | Playlists made in the official client that list local files | Those rows to play and look right | Mismatches between devices |

## 3. Jobs to be done

1. **Make my files look right** (cover, title, artists, album, year) without me tagging by hand.
2. **Hear the best copy I have**: my lossless file when I have it, Spotify when I don't.
3. **Keep versions together**: a remix, live take or edit lives with its original.
4. **Never play the wrong song**: no silent swaps; I can always see and undo a link.
5. **Keep my files mine**: nothing written to disk unless I ask, and always undoable.
6. **Fill Spotify's gaps**: a greyed (removed or region-locked) Spotify song plays from my file.

## 4. User stories (with acceptance criteria)

**Matching and tagging**
- **US1.** As a collector, I want Wavee to find Spotify's info for my albums, so they get covers and correct names.
  - *Given* matching is on, *when* a scan finishes, *then* Wavee proposes matches per album with a confidence
    (Exact, Strong, Likely, Check) and applies nothing I haven't accepted, except Exact when I chose "accept exact
    automatically".
- **US2.** As anyone, I want to see why a match was made, so I can trust it.
  - Every match shows its evidence: "same ISRC", "11 of 11 track lengths line up", "title, artist and length".
- **US3.** As a collector, I want my files untouched unless I say so.
  - Default: Spotify's info is shown in Wavee only. "Write to my files" shows a per-field before/after, keeps the
    old tags for Undo, and never writes a file that is playing.
- **US4.** As an archivist, I want to mark a song "Not on Spotify", so Wavee never matches it again.
  - The mark is sticky across rescans; the song gets the "Not on Spotify" badge; matching skips it.
- **US5.** As anyone, I want to fix a wrong match in one place.
  - "Not this song" on the song or album: pick another candidate, search, or "Not on Spotify"; the whole album
    updates at once (Plex users' top complaint).

**Linking**
- **US6.** As a collector, I want my FLAC to play when I start the Spotify album, so I hear lossless.
  - With a "Same recording" link and "Prefer my files" on, playing the Spotify album plays my files; the player bar
    says "Your file · FLAC 24/96" and lets me switch to Spotify for this song.
- **US7.** As a digger, I want to attach my extended mix to the original as a version.
  - "Link as a version of…" with a type (remix, live, demo, extended, edit, acoustic, instrumental, cover); the
    original's drawer lists it under **Versions** beside the music video; its row shows "+1".
- **US8.** As a digger, I want my version to play instead of the original in my playlists.
  - "Play this version by default" swaps it on this PC only, visibly ("Playing your Extended Mix"), and is undoable.
- **US9.** As a casual listener, I want a greyed Spotify song I own as a file to just play.
  - A removed or region-locked track with a linked file lights up: "Plays from your file".
- **US10.** As anyone, I want links to never surprise me.
  - No link is made without a confidence of Exact (and my opt-in) or my click; every link shows on the song and can
    be removed; Wavee reports a file play as a local play, never as a Spotify stream.

**Discovery**
- **US11.** As an archivist, I want my rarities on the artist's page.
  - An artist page shows "On this PC: 14 songs, 9 not on Spotify".
- **US12.** As a collector, I want to complete an album I only partly own.
  - A local album with missing tracks shows Spotify's missing tracks inline, streamable, in order.

## 5. User flows

**F-A. Opt in, then the first matching**
1. Local Files shows a calm banner once: "Wavee can add covers and correct names from Spotify to 42 albums" ·
   [Show me] · [Not now].
2. Show me → an explainer card: what is searched (titles, artists, ISRCs), what never leaves the PC (paths, files),
   "your files stay untouched" → [Turn on matching].
3. Matching runs in the background with the library activity chip ("Matching · 12 of 48 albums").
4. Done → "36 albums matched exactly · 8 strong · 4 to check" → [Review].
5. Review page: per album: cover before → after, evidence, "N fields improve" → [Accept] / [Not this album]; at the
   top [Accept all exact]. The "to check" ones show 2-3 candidates or "Not on Spotify".

**F-B. Fix a wrong match**
1. A song shows the wrong title or cover → row menu › "Spotify match…" (or File info › Match).
2. The current match with its evidence → [Not this song].
3. Candidates and a search → pick one, or "Not on Spotify".
4. The album updates in place (crossfade, no jump); the choice is sticky.

**F-C. Link a remix as a version**
1. From the local song: row menu › "Link to Spotify…"; *or* from the Spotify song's drawer › Versions › "Add a version
   from your files…".
2. Search (pre-filled from the file's title and artist; "From your library" first) → pick the original.
3. Link type: Same recording · A version of [remix ▾] · Not on Spotify → [Link].
4. The Spotify row gets "+1"; its drawer lists "Extended Mix · your file · FLAC 24/96" under Versions; optional "Play
   by default".

**F-D. Hear my lossless copy**
1. A Spotify album page shows "You have this album on this PC · FLAC 24/96" · [Play from your files].
2. Playing: the player bar pill "Your file · FLAC" ⇄ "Spotify" for this song.
3. Settings › Playback: "Prefer my files when I have them" (off by default; on for Exact links when the user turns it
   on).

**F-E. A greyed Spotify song comes back**
1. A playlist row is greyed (removed or region-locked) and a linked or matching local file exists.
2. The row lights up with the file mark and the tooltip "Not available on Spotify here · plays from your file".

**F-F. Write the tags to my files**
1. Album ⋯ › "Write Spotify's info to the files" (only after a match).
2. A diff per field, "fill empty fields only" or "replace", cover embedded or as cover.jpg → [Write].
3. Progress; a toast "Wrote 11 files" · [Undo] (the old tags are kept in Wavee's cache).

**F-G. Unlink**
1. Any linked song shows its link (file mark, Spotify mark, or "+1"); the drawer and File info show the link with
   [Remove link].
2. Removing returns the song to its own file info at once; nothing on disk changes.

## 6. Principles that follow
1. **Never silent.** No source swap or info change without a visible mark and an undo (Spotify's top complaint).
2. **Evidence, not scores.** "Same ISRC", "track lengths line up": plain words instead of percentages.
3. **Album first.** Match and fix whole albums; songs inherit.
4. **Your files are yours.** Show-only by default; writing is opt-in, previewed and undoable.
5. **The best copy plays, honestly reported.** Lossless file when chosen; a file play is reported as a local play.
6. **Two simple choices, not twenty.** "Whose info wins" (Spotify's or the file's) per album or song; "which source
   plays" per song, with one global preference.

## 7. Decided (owner, 2026-11-23: "keep the defaults")
- "Prefer my files": **off** by default; the user turns it on.
- Exact matches: **reviewed** unless the user ticks "Accept exact matches automatically" at opt-in (unticked).
- Writing tags to files: in the first version, **behind a click** (show-only is the default mode).
- Fingerprint fallback (AcoustID/MusicBrainz): **later**.
- Release: **after 0.4**, as one feature, "Your files and Spotify".
- A "Play by default" version: applies **on this PC only**, including in playlists you follow.

## 7a. The questions as they were asked
1. Is "Prefer my files" off by default (safest) or on for Exact links?
2. Should Exact matches be accepted automatically after the opt-in, or always reviewed the first time?
3. Do we want writing tags to files at all in the first version, or show-only first?
4. Fingerprint fallback (AcoustID/MusicBrainz) for files Spotify can't place: worth adding later?
5. Release: after 0.4 as one feature ("Your files and Spotify"), or "Same recording" links first?
6. Should a "Play by default" version also replace the original in playlists you follow but don't own (on this PC)?
