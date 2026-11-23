# #240 Play the local files a playlist lists: implementation (0.4 "Drift")

> **Design and briefs.** Design board with the screens and spec diagrams (architecture, uri and matching ladder,
> hand-off, phases, capture checklist, decisions, risks): https://claude.ai/artifact/QA2Vq8BHDhuJKBnvd6GfwE (private
> until shared). Shareable brief for the reporter: [`briefs/240-local-files-brief.pdf`](briefs/240-local-files-brief.pdf).

Base code: `release/0.3.13` (the app). Every file:line below was read there. Paths are
relative to `src/apps/Wavee/` unless they start with a repo folder. Work branches from the 0.4 release branch once
it exists.

## Cross-plan notes (0.4)

- **G1, local library groundwork, is owned by this plan (#240):** the music-folders setting, the tag scan and index,
  the local track identity, and general local-files playback (the Local Files list filled from the folders).
  - #48 (Mix) consumes G1 only in one way: a local track has no Spotify tempo or key, so Mix falls back to a plain
    crossfade for a transition that touches one. Nothing in this plan computes BPM or key.
  - #48 moved: Mix is now a per-playlist Spotify state with transition chips between rows on the playlist page.
    A chip next to a local row shows no transition in 0.4. Mix transitions never apply to a local track in 0.4; a
    boundary that touches one gets the base hand-off this plan defines in 3.13 (the user's normal crossfade or
    gapless join).
  - #154 (timecode) gets local tracks for free: they play on the same audio host as Spotify audio
    (`HostHandoff.SameHost`, `Playback/Playback.Transitions.cs:1683`), so its timeline tap sees them unchanged.
    "Same host" is not "same hand-off": 3.13 says what a local boundary does today.
- **This plan owns the base hand-off for local tracks** (3.13): it lifts the `MediaSwitch.AllowCrossfade` gate
  (`Playback.Transitions.cs:1672`) so local boundaries get the normal crossfade and gapless join. #48 builds on
  that and adds nothing for local tracks.
- **G2, the playback timeline tap, the aux audio output and the module observer hook, is owned by #154; the mix
  transition planner is owned by #48.** This plan depends on neither and touches no engine audio code.
- **#184 (custom palettes) is independent.** Both plans add Settings rows. This plan touches
  `Screens/SettingsSpec.Storage.cs`, `Screens/SettingsFacts.cs`, `Screens/Settings.Host.cs`,
  `Screens/SettingsSpec.Aliases.cs` (only if a slug alias is wanted) and the three themes' Storage views
  (`ClassicSettings.Storage.cs`, `WinUiSettings.Body.Storage.cs` + `WinUiSettings.Rows.cs`,
  `ZuneSettings.Storage.cs` + `ZuneSettings.Composites.cs`). #184 should stay in the Appearance files; the only
  shared files are the loc JSONs (`assets/loc/*.json`), where the two plans add disjoint key groups.
- **The capture landed.** The wire-level parts of this plan now follow
  the capture report (kept outside the repo: it holds account data) (official desktop 1.3.10.2586, 2026-11-22).
  Section 6 holds the checklist with each answer; section 7 records a probable bug in shipped Wavee that the capture
  showed and that is **not** a 0.4 feature item. The capture added one phase (P3, M): Connect and report parity for
  local plays.
- **Suggested 0.4 build order:** #240 and #184 in parallel first, then #154 phase 1 (the timeline tap), then #48,
  then the rest of #154.

---

## 1. What was asked, and what exists today

**Asked (#240).** A playlist made in the official client can list local files as
`spotify:local:<artist>:<album>:<title>:<seconds>`. Wavee should play those rows when the same file is on this PC.
The owner's comment on the issue: "i will pick this up in 0.4 with general local files playback support too".

**Exists.**

| Piece | Where | State |
|---|---|---|
| A playlist's local item is parsed and drawn from its own uri | `Entities/Track.LocalUri.cs:19-74` (`LocalTrackUri.Is`, `TryParse`, `StageFace`, `NamesOf`) | Shipped 0.3.12 (#231). `StageFace` writes title, artist line, duration and **always** `TrackFlags.Unavailable` (line 67). |
| Staged at both list landings | `Spotify/Spotify.Decode.cs:1924` (wire read), `Entities/Store.Lists.cs:785` (disk read and replayed diff) | Runs on api and store threads, not the UI thread. |
| The greyed row, its tooltip and its duration | `Entities/Track.Rules.cs:670` (`IsLocalItem`), `Entities/Track.UI.Bound.cs:213,231` (duration shown, reason `detail.trackFacts.localReason` = "A local file: Wavee can't play local files yet", `assets/loc/en-US.json:333`), album lane `Entities/Track.UI.cs:377` | Shared by all three themes. |
| "N are local files" in the unavailable-songs bar | `Entities/Track.Rules.cs:1033-1044` (`UnavailableTally.LocalFiles`), `Entities/Track.Table.Chrome.cs:1032,1044-1055`; bar contract `Theming/Contracts/TrackChromeStyle.cs:199` (`TrackUnavailableBarArgs`: one action, "Hide them") | Every theme draws it from the args. |
| The play gate skips a ruled-dead row | `Playback/Playback.Transitions.cs:950-962` (`RowPlayable` → `Track.Unplayable()`, used by `Queue.NextPlayable`) | So a local item is stepped over today. |
| Local audio playback | `Playback/Playback.Audio.cs:4532-4539` (`RouteOf(EntityProvider)`), `:4557` (`SourceRoute.Local`), `:4638-4701` (`LocalSource`), `:4729-4753` (`ClassifyLocal`) | Decodes by content: FLAC, Ogg Vorbis, MP3, raw ADTS AAC, and MP4 with an AAC track when Windows has an AAC decoder. WAV, Ogg Opus and Ogg FLAC are refused with a logged reason (`:4739-4745`). `Opened.DurationMs` is 0 for everything but MP4 (`:4699`). |
| Local track identity | `Entities/Playlist.cs:1378-1396` (`wavee:local:file:<base64url(path)>`, `LocalFileUri`, `LocalPathOf`); the seam `Playback.Audio.LocalPath` (`Playback.Audio.cs:4506`) is bound to `Playlist.LocalPathOf` in `Platform/Modules.UI.cs:38` | |
| The Local Files list | One ordinary playlist row `wavee:local:playlist:all` (`Entities/Playlist.cs:374,382`), route `local` (`Shell/Shell.cs:166`), settled locally (`Entities/Playlist.Page.cs:882-890`), header staged by `EnsureLocalFilesHeader` (`:1329-1344`) | Filled only by drag-and-drop and "Play ▸ File…": `ImportLocalFile` (`:1348-1370`) stages a row whose title is the **file name** (`LocalTitleOf`), no artist, no album, no duration. |
| Drop and pick | `Playlist.Page.cs:1375-1399` (`PlayFiles`, `PickAndPlay`), extension rule `Entities/Playlist.cs:1415-1418` (.mp3 .ogg .flac; .mp4 as video) | |
| A local track told to Connect | `Spotify/Spotify.Egress.cs:90-128` (`Egress.LocalTrackUri` builds the same uri shape from tags), `:140-148` (`Egress.TrackUri`), put-state `Spotify/Spotify.Decode.PutState.cs:518` | A `spotify:` id is admitted as is (`Egress.Admits`, `:33`); a `wavee:local:` track is named from the wire window's tags and never by its path. |
| Tag reading | FLAC: `Playback/Playback.Audio.Flac.cs:144-153` (`Tags`: Title, Artist, Album, AlbumArtist, Date, TrackNumber, picture, ReplayGain), parsed by `ParseVorbisComment` (`:258`). Ogg Vorbis and FLAC ReplayGain: `Playback.Audio.cs:1618-1680` (`ReplayGainTags.FromCommentBlock`). MP3: only the ID3v2 **length** (`Id3v2Length`, `:5058`) and the LAME/Xing gapless block (`Mp3Tag`, `:3047`). | No ID3 text-frame reader, no MP4 `ilst` reader, and nothing writes a file's tags into a track row. |
| Folder picker | `Platform/ModalFilePicker.Rules.cs:20` (`PickerRequest.Folder`), shown through `Platform/Pickers.cs:21` (`Pickers.Pick`, off the UI thread, answer posted back) | Already used by the cache-location dial. |
| Settings | One registry: `Screens/SettingsSpec.cs` (dial model), `Screens/SettingsSpec.Storage.cs:19-103` (Storage category), store `Platform.Settings` with `SettingKey<T>` (`Platform/Platform.Settings.cs:39`); a list-with-verbs precedent is the attached-videos dial (`Screens/SettingsSpec.Playback.cs:73-81`, drawn in `ClassicSettings.VideoOverrides.cs`, `WinUiSettings.Rows.cs:271`, `ZuneSettings.Composites.cs:34`) | |
| Small persisted maps | `Playback/OnThisPc.FileMap.cs` (`AudioFileMap`: a versioned TSV under `Platform.LocalFolder\Wavee\Cache`, one `Lock`, temp file + rename, background load and save, a `Version` counter for readers) | The model for the index file below. |

**Missing:** a music-folders setting, a scan that reads tags, an index, a matcher, and a play path for a matched
`spotify:local:` row.

**Not verified** (flagged again where it matters):
1. Whether the Local Files list's membership survives a restart today. `ImportLocalFile` commits through
   `Entities.Commit`; I did not trace whether the store persists a `wavee:local:` playlist edge.
2. How a staged Availability group merges flag bits when a later staging clears `Unavailable` (I read the staging
   call sites, not `Store`'s merge).
3. Where a locally started playlist play takes the deck row's uid from (`snap.Uid`, written at
   `Spotify/Spotify.Decode.PutState.cs:520`). The capture says it must be the playlist item's `item_id` in hex; the
   edge carries it (`PlaylistTrackEdge.ItemId`, `Entities/Edges.cs:703`), but I did not trace it to the snapshot.
4. Whether the collection decode keeps a `spotify:local:` uri as a Liked Songs row (the capture shows the official
   client saving one there).

**The capture** (`localfiles-capture-report.md`, read in full). What it settles, with the report's section:

| Fact | Report |
|---|---|
| Nothing about local files is uploaded or synced: no index, path, hash or fingerprint reaches a content service. Matching happens on the device. | 6 "Sync" |
| The uri is `spotify:local:<artist>:<album>:<title>:<seconds>`; an empty artist or album is an empty field (`spotify:local:::<title>:<seconds>`). | 4.1 |
| Each part: unreserved `A-Za-z0-9-_.~` kept, space as `+`, everything else uppercase `%XX` over UTF-8; `! ' ( ) *` are always percent-encoded. | 4.1 |
| `<seconds>` is the file's length in whole seconds, **truncated** (252 839 ms is `:252`). | 4.1 |
| An untagged file's title is its **file name without the extension**. | 4.2, 6.1 |
| A playlist item carries the uri and the ordinary attributes; its 8-byte `item_id`, as lowercase hex, is the row's uid everywhere. No length, hash or flag. | 3.1 |
| Liking a local file is `POST /collection/v2/write`; Liked Songs then lists the uri with a 10-byte `item_id`. | 3.2 |
| Extended-metadata answers 404 for a local uri; clients build title, artist and cover from the uri. | 3.3, 4.3 |
| Connect-state while a playlist's local file plays: `track.uri` = the playlist's string, `uid` = item_id hex, the file's real duration in ms, `playback_quality { strategy: local_file, target_bitrate_level: veryhigh, hifi_status: off, 7 (file_origin): 3 }`, the playlist as the context. | 3.4 |
| The Local Files context is `spotify:local-files`, origin `local_files`, with `disallow_transferring_playback_reasons: "local_list"` and every file in `next_tracks`. | 3.4 |
| The end-of-play event `RawCoreStream` has `content_uri: "spotify:local::::"` and the real uri in `local_content_uri` (field 41); herodotus records the play in play history. | 3.6, 3.8 |
| A device without the file skips the row (web player) or starts at the playlist's first Spotify track and leaves the row out of its queue (iPhone). | 3.9 |

What it cannot answer is in section 6 ("still open"); those points keep the mark "to confirm against the capture"
(a second capture with tagged files would settle most of them).

---

## 2. Decisions for the owner

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | What a matched playlist row is | (a) keep the row's `spotify:local:` identity and resolve the file at open; (b) swap the row for the `wavee:local:file:` track | **(a).** The row stays the playlist's member, so the now-playing highlight, the queue, the playlist edit verbs and Connect all keep working, and Connect is told the playlist's own uri, which is already a `spotify:` identity that `Egress` admits. (b) breaks the member identity and needs a second mapping back. |
| D2 | Where the folders setting lives | Storage, Playback, or a new "Library" category | **Storage, a new first group "Music on this PC".** The category already says "what Wavee keeps on this PC" and holds every other folder door. |
| D3 | Default folders | none; or the user's Music folder offered on first open | **None scanned until the user adds one**, with a one-press "Add my Music folder" beside "Add a folder…". No surprise disk scan, no surprise rows. |
| D4 | What the Local Files list shows | (a) every indexed file plus anything dropped; (b) dropped files only | **(a).** That is the "general local files playback" half of the owner's comment. A dropped file outside the folders stays in the list for the session as today. |
| D5 | Unmatched playlist rows | stay greyed with a new reason | **Greyed, reason "Not found in your music folders"**, and the unavailable-songs bar gains one door: "Add a music folder" (opens the Storage group). |
| D6 | Play reports for a local play | (a) Connect put-state only; (b) the reports the official client sends | **(b), changed by the capture.** The official client writes play history (herodotus `item_uri` = the local uri) and `RawCoreStream` with `content_uri: "spotify:local::::"` and `local_content_uri` = the real uri (report 3.6, 3.8). Wavee already has both senders; without a local arm it would put the local uri in `content_uri`, which the official client never does. Private session suppresses them as for any track. The official client's **folder and source telemetry** (`LocalFilesSourceEvent` with an absolute folder path, report 3.8) is **never** mirrored. |
| D7 | Formats in 0.4 | what decodes today; or add WAV, Opus, ALAC | **What decodes today** (MP3, FLAC, Ogg Vorbis, AAC in .aac/.m4a/.mp4). The scan lists other audio files as "can't be played" with the count in Settings, so nothing is silently missing. New decoders are "After 0.4". |
| D9 | Hand-off at a local boundary | (a) lift the gate: local boundaries get the user's crossfade or gapless join; (b) keep today's cut | **(a)**, section 3.13. A local library that hard-cuts between every song while Spotify songs crossfade is not "general local playback". |
| D10 | The Local Files list on Connect | (a) publish it as the official context `spotify:local-files` with the `local_list` restriction; (b) keep today's "track only, no context" | **(a)**, in P3. It is what a controller expects for this context, and `local_list` stops a controller offering a transfer that cannot work. |
| D11 | An inbound Connect `play` of `spotify:local-files` | (a) refuse it cleanly; (b) play Wavee's own Local Files list | **(a) for 0.4.** The command names the row only by a uid the *sending* client made up for its own list (report 3.5); Wavee cannot know which file was meant, and starting a different song is worse than not starting. Revisit when uid generation is understood (section 6). |
| D12 | Liked Songs and playlist writes | (a) read side only; (b) also like a local file and add one to a playlist from Wavee | **(a) for 0.4:** a local item already in Liked Songs or a playlist is matched and played like any other row. Writing (`collection/v2/write`, a playlist ADD with a fresh 8-byte `item_id`) is "After 0.4"; the server accepts both (report 3.1, 3.2). |
| D8 | Embedded cover art | in 0.4 or after | **After 0.4.** Local rows show the placeholder cover as the Local Files list does today. I did not verify that the image pipeline takes a file or bytes source, so it is not a safe 0.4 promise. |

---

## 3. Architecture

### 3.1 Shape

```
 Settings ▸ Storage ▸ Music on this PC          (UI thread)
        │ add / remove / rescan
        ▼
 LocalLibrary (static façade, Playback/LocalLibrary.cs)
   ├─ LocalFolders      the persisted folder list (one SettingKey<string>)
   ├─ LocalScan         one background thread: enumerate → (size, mtime) diff → LocalTags.Read
   ├─ LocalIndex        the index: entries + two match maps, one Lock, Version, TSV on disk
   └─ LocalMatch        PURE: normalise, pick a candidate, tie-break
        │ Version moved (posted to the UI thread)
        ▼
 LocalLibrary.Restate()                          (UI thread)
   ├─ stages every indexed file as a wavee:local:file: track row (tags, duration, Local flag)
   │  and replaces the Local Files membership edge
   ├─ re-rules every spotify:local: row seen this session: matched → playable, else unavailable
   └─ fills the open-time path map  EntityId → path

 play:  Playback.Audio.Open(id) ─ RouteOf(id) = Local ─► LocalSource(id) ─► LocalLibrary.PathOf(id)
        (pump thread; a dictionary probe under a Lock; no string is built)
```

Nothing here runs per frame. The row facts a table reads per frame are the existing flag bits
(`Track.Unplayable()`, `Track.IsLocal`), written once per restate.

### 3.2 Identity

- A file in a music folder: `wavee:local:file:<base64url(path)>`, unchanged (`Playlist.LocalFileUri`).
  `EntityProvider.Local`, `EntityKind.Track` (`Entities/Entities.cs:837-843`).
- A playlist's item: its own `spotify:local:…` text-form id, unchanged. Its provider is `EntityProvider.Spotify`
  because the uri starts with `spotify:` (`Entities.cs:783`). That is what makes D1(a) free on the Connect side:
  the capture requires `track.uri` to be the playlist's string byte for byte (report 5.2), and a text-form id is
  that string.
- **Its kind is `Unknown` today.** `SpotifyKind` hands the head `local` to `CatalogKind`, which has no arm for it
  (`Entities/Entities.cs:845-860`), so `EntityId.IsPlayable` (`Kind is Track or Episode`, `:246`) is false for a
  local item even though its row lives in the track table. Gates that ask it would drop the row: the remote-load
  queue builder (`Playback/Playback.Host.Context.cs:681`), the play telemetry (`:1336`), and the callers at
  `Playback.Host.Context.cs:716,1155,1218`, `Playback.Host.Wire.cs:553,638`, `Playback.Host.Forward.cs:218`,
  `Playback.Host.Remote.cs:457` (listed from a search; not each read). P4 adds the arm: a `spotify:local:` uri with
  **four** parts is `EntityKind.Track`; the two-part and one-part forms (`local-album`, `local-artist`, report 4.1)
  stay `Unknown`.

### 3.3 The index entry and the file

```csharp
// Playback/LocalIndex.cs
namespace Wavee;

/// <summary>One audio file in a music folder, as the scan read it. Immutable; replaced whole on a rescan.</summary>
public sealed record LocalEntry(
    string Path, long Size, long ModifiedTicks,          // identity of the bytes: unchanged ⇒ the tags are not re-read
    Spotify.Audio.Format Format, int DurationMs, int BitrateKbps,
    string Title, string Artist, string Album, string AlbumArtist, int TrackNumber, int Year,
    bool Playable);                                       // false: an audio file this build has no decoder for (D7)

/// <summary>The scanned library. One Lock; writers are the scan thread, readers are the UI thread, the api and store
/// threads (StageFace) and the audio pump. Modelled on Playback.AudioFileMap.</summary>
public sealed class LocalIndex
{
    public const string FileName = "localindex.tsv";      // Platform.LocalFolder\Wavee\Cache\, beside audiofiles.tsv
    const string Header = "wavee.localindex.v1";

    public int Version { get; }                           // bumped by Replace / Load; Volatile read
    public int Count { get; }
    public int UnplayableCount { get; }

    /// <summary>Swap in a finished scan. Rebuilds both match maps under the lock, bumps Version.</summary>
    public void Replace(IReadOnlyList<LocalEntry> entries);

    /// <summary>The entry a playlist item names, or null. Any thread; allocates only the normalised key (never on a
    /// frame: callers are a list landing, a restate and a track open).</summary>
    public LocalEntry? Match(string artist, string album, string title, int seconds);

    /// <summary>A stable snapshot for the UI thread's restate.</summary>
    public LocalEntry[] Snapshot();

    public int Load(string path);                         // background; 0 for a missing or foreign file
    public bool Save(string path);                        // background; temp file + File.Move(overwrite: true)
}
```

The file is one header line, then one tab-separated line per entry; a tab, CR or LF inside a tag is written as
`\t`, `\r`, `\n` and a backslash as `\\`. It is a cache: a file this build cannot read is ignored and rebuilt by the
next scan, exactly as `AudioFileMap.Load` treats its own (`OnThisPc.FileMap.cs:166-176`). It is deliberately **not**
a table in the entity store: that file is named for its schema fingerprint and reaped on every DDL change
(`Entities/Store.Files.cs:6-12`), which would throw the scan away with every release.

Size: about 200 bytes per track, so 10 MB at 50,000 tracks, read once on a background thread at start.

### 3.4 Matching (PURE, `Playback/LocalMatch.cs`)

Matching is Wavee's own design: nothing is synced, so the official client matches on the device too, and how it does
so is not on the wire (report 6, open questions). What the capture fixes is the **input**: the parts are decoded
strings (`+` is a space, `%XX` a UTF-8 byte; `LocalTrackUri.TryParse` already does this, report 5.8), an untagged
file's title is its file name without the extension, an empty artist or album is empty, and the seconds are the
file's length truncated. The encoder's unreserved set does not affect matching, because matching compares decoded
text.

```csharp
public static class LocalMatch
{
    /// <summary>Tier 1 key: NFKC, upper-invariant, trimmed, inner whitespace runs collapsed to one space.</summary>
    public static string Exact(string text);

    /// <summary>Tier 2 key: Exact, then diacritics removed (NFD, drop Mn), every char that is not a letter or digit
    /// dropped, a trailing "(…)" or "[…]" group dropped, a leading "THE " dropped, and "FEAT."/"FT."/"FEATURING" and
    /// everything after it dropped.</summary>
    public static string Loose(string text);

    public static string Key(string title, string artist) => title + "\u001f" + artist;

    /// <summary>Does a candidate's length agree with the uri's seconds? The uri carries the length TRUNCATED to whole
    /// seconds, so the entry is truncated the same way before the compare. seconds == 0 (unknown) agrees with anything.</summary>
    public static bool DurationAgrees(int uriSeconds, int entryMs, int toleranceSeconds)
        => uriSeconds <= 0 || entryMs <= 0 || Math.Abs(entryMs / 1000 - uriSeconds) <= toleranceSeconds;

    /// <summary>The winner among candidates that share a key, or -1. Order: album equal (Exact) first, then the
    /// smallest duration difference, then lossless over lossy, then the higher bitrate, then the ordinal-smallest
    /// path (so the answer never depends on scan order).</summary>
    public static int Pick(ReadOnlySpan<LocalEntry> candidates, string album, int uriSeconds);
}
```

The index stores, for every file, the title it would have in a uri: its title tag, or the file name without the
extension when it has none (the official rule for an untagged file, and `Playlist.LocalTitleOf` already). The walk
`LocalIndex.Match` runs:

1. `Key(Exact(title), Exact(artist))` in the exact map, candidates filtered by `DurationAgrees(…, 1)`. An untagged
   file on both sides lands here: empty artist, the file name as the title.
2. Else, when the uri's artist **and** album are empty (the shape of an untagged file): the file-name map (`Exact`
   of the name without its extension) against the uri's title, filtered by `DurationAgrees(…, 1)`. This catches a
   file that was untagged when the playlist row was made and has been tagged since.
3. Else `Key(Loose(title), Loose(artist))` in the loose map, filtered by `DurationAgrees(…, 2)`.
4. Else no match.

Tolerances: the uri's seconds and the entry's truncated seconds should be **equal**; 1 s allows for two decoders
measuring one file differently (padding, a trailing partial frame), and 2 s on the loose tier for a different encode
of the same master (the report suggests 1 to 2 s, section 6.1). A file whose length is a bitrate estimate (an MP3
without a Xing block, raw ADTS) gets 5 s on tier 1 only. A candidate outside the tolerance is never picked, even
when it is the only one: a wrong song playing under a playlist row is worse than a greyed row. What the uri
carries when the official client does not know a file's length is to confirm against the capture; `0` is treated
as "unknown" and agrees with anything.

Tagged files: every local file in the capture is untagged (report 4.2), so which tags feed the artist and album
parts, and what a multi-value artist tag becomes, is to confirm against the capture. Until then the scan stores the
first artist value as `Artist` and adds a second exact-map key for the values joined with ", ". Both keys are cheap
and the log line below shows which tier answered.

Every match and every miss on a list landing is one `Log.Info("local", …)` line with the tier and the candidate
count, always on (no switch), so a "why is this row grey" report can be answered from the log.

### 3.5 Tag reading (`Playback/LocalTags.cs`)

```csharp
public readonly record struct LocalTagSet(
    string Title, string Artist, string Album, string AlbumArtist, int TrackNumber, int Year, int DurationMs, int BitrateKbps);

public static class LocalTags
{
    /// <summary>Read one file's format, tags and length from its headers. Scan thread only. Never decodes audio and
    /// never throws: an unreadable file answers Format.Unknown.</summary>
    public static bool TryRead(string path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why);
}
```

Per format, reusing what exists:

| Format | Sniff | Tags | Length |
|---|---|---|---|
| FLAC | `ClassifyLocal` (`Playback.Audio.cs:4729`) | `Flac.ParseHeaders` → `Tags` (`Playback.Audio.Flac.cs:144`), already has Title/Artist/Album/AlbumArtist/Date/TrackNumber | STREAMINFO total samples ÷ rate |
| Ogg Vorbis | same | the comment header packet through the same comment-block walk `ReplayGainTags.FromCommentBlock` uses (`Playback.Audio.cs:1666`); **lift that walk into one shared `VorbisComments.ForEach(block, visitor)`** and make both callers use it (no second parser) | last page's granule position ÷ rate (read the file's last 64 KiB and find the last `OggS`) |
| MP3 | same, past `Id3v2Length` (`:5058`) | **new** ID3v2 reader: v2.2 (`TT2`/`TP1`/`TAL`/`TRK`/`TYE`), v2.3 and v2.4 (`TIT2`/`TPE1`/`TALB`/`TPE2`/`TRCK`/`TYER`/`TDRC`), the four text encodings, unsynchronisation, v2.4 syncsafe frame sizes; `APIC` and every other frame skipped by seeking. Fallback: the 128-byte ID3v1 block at the end. | `Mp3Tag.TryProbe` total samples (`:3069`) when a Xing/Info block is there; else (file size − tag sizes) × 8 ÷ the first frame's bitrate |
| MP4 / M4A | `Mp4.LooksLikeMp4` | **new** `moov/udta/meta/ilst` reader: `©nam`, `©ART`, `©alb`, `aART`, `trkn`, `©day` | `Mp4.TryReadAacTrack` → `DurationMs` (already read at `Playback.Audio.cs:4669`) |
| ADTS .aac | `Modules.Adts` | ID3v2 if present | size ÷ first frame's bitrate (an estimate) |
| WAV, Opus, Ogg FLAC, anything else with an audio extension | `ClassifyLocal`'s refusal | none | none; the entry is kept with `Playable = false` and its `Why` |

A file with no title tag takes its file name without the extension as its title (`Playlist.LocalTitleOf`,
`Entities/Playlist.cs:1399`), the rule `ImportLocalFile` already uses.

The scan considers `.mp3 .flac .ogg .oga .m4a .mp4 .aac .wav .opus` (the last two only to be counted as
unplayable). The drop rule (`IsLocalAudioFile`, `Playlist.cs:1415`) gains `.m4a` and `.aac`, which `LocalSource`
already decodes; the rejected-file sentence (`localFile.rejected`) is reworded to match.

### 3.6 The scan (`Playback/LocalScan.cs`)

- **One background thread**, named `local-scan`, `IsBackground = true`, `ThreadPriority.BelowNormal`. Never the
  UI thread, never the audio pump, never the thread pool (a 50,000-file first scan would hold a pool thread for
  minutes).
- **Incremental:** enumerate each folder recursively (`EnumerationOptions { RecurseSubdirectories = true,
  IgnoreInaccessible = true, AttributesToSkip = Hidden | System | ReparsePoint }`), and for each file reuse the old
  entry when `(Size, ModifiedTicks)` are unchanged; otherwise `LocalTags.TryRead`.
- **A folder that cannot be opened** (unplugged drive, offline share) keeps its previous entries and is marked
  offline for Settings; its rows are not dropped. The loc strings for the state already exist
  (`statusDriveOffline`, `assets/loc/en-US.json`, the video-override roster).
- **When:** at start after `LocalIndex.Load` (so the app is usable from the saved index at once), on every folder
  add or remove, on "Rescan now", and from one `FileSystemWatcher` per folder (Created, Deleted, Renamed, Changed),
  debounced 2 s into a rescan of that folder. A watcher that faults (network share) is dropped with one log line;
  that folder then updates at start and on "Rescan now".
- **Progress** is two numbers the scan thread writes with `Volatile.Write` (files seen, files read) and one
  `Signal<int>` epoch bumped on the UI thread at most four times a second through the existing poster
  (`Spotify.Post`, as `Pickers.cs:39` does). Settings binds its status text to that epoch.
- **Cancel:** a new scan request cancels the running one through a `CancellationTokenSource`; a removed folder is
  honoured at the next file boundary.
- On finish: `LocalIndex.Replace`, `LocalIndex.Save` on the same thread, then post `LocalLibrary.Restate`.

### 3.7 The façade, the restate and the play path

```csharp
// Playback/LocalLibrary.cs
public static class LocalLibrary
{
    public static LocalIndex Index { get; }
    public static readonly Signal<int> Epoch;                    // UI thread; moves after every Restate and on scan progress

    public static IReadOnlyList<LocalFolder> Folders { get; }    // UI thread
    public static bool AddFolder(string path);                   // UI thread; false: already covered by a listed folder
    public static void RemoveFolder(string path);
    public static void Rescan();
    public static LocalScanStatus Status { get; }                // Idle / Scanning(seen, read) / last finished at

    /// <summary>App start (after Platform.Settings is up; never under --fake unless the inspector seeds it).</summary>
    public static void Use(string indexPath);

    /// <summary>Note a spotify:local: uri a list landed, so a later index change can re-rule it. Any thread.</summary>
    public static void NoteItem(string uri);

    /// <summary>UI THREAD. Re-state every row the index decides (3.1).</summary>
    public static void Restate();

    /// <summary>The file a track id plays from, or null. ANY THREAD (the audio pump): one Lock, one dictionary probe
    /// by EntityId; no id text is read here.</summary>
    public static string? PathOf(EntityId id);

    /// <summary>The indexed length of a local track, or 0. Any thread.</summary>
    public static int DurationMsOf(EntityId id);
}

public readonly record struct LocalFolder(string Path, bool Online, int Files, int Unplayable);
```

`Restate` uses only the staging pattern `ImportLocalFile` already uses (`Playlist.Page.cs:1350-1369`):
`Staging.Rent`, `staging.Tracks.RowFor(id, authority, groups)`, `Entities.Commit`, `Staging.Return`,
`e.Replace(local.Slot, …)`, `local.Refold()`, `Entities.Publish()`.

1. **Indexed files.** For each entry: a row keyed `wavee:local:file:…` at `Authority.Local` with
   `TrackFields.Identity | Face | Availability`: `Title`, `ArtistLine`, `DurationMs`, `Flags = TrackFlags.Local`
   (plus `Unavailable` when `Playable` is false). Staged in batches of 2,000 so a 50,000-file library is 25 commits
   spread over 25 frames by a UI-thread timer, not one long frame. Then the Local Files membership is replaced
   with the indexed tracks ordered by album artist, album, track number, title, followed by any session-dropped
   files not in the index.
2. **Playlist items.** For each uri noted by `NoteItem`: parse (`LocalTrackUri.TryParse`), `Index.Match`, and stage
   the row at `Authority.Full` with `Face | Availability | Identity`: matched → no `Unavailable`, `TrackFlags.Local`
   set, duration from the entry when the uri's was 0; unmatched → today's face. (Unverified item 2: confirm that a
   staged Availability group with the bit clear clears a previously committed `Unavailable`; if the store merges by
   OR, `Table` needs a clearing write, which is an app change, not an engine one.)
3. **The path map.** `Dictionary<EntityId, (string Path, int DurationMs)>` rebuilt under the lock: every
   `wavee:local:file:` slot id and every matched `spotify:local:` slot id. The ids are taken from the committed rows
   on the UI thread, so the pump never reads id text. (`Spotify/Spotify.Decode.cs:1188-1191` documents that a
   text-form id's payload indexes the UI thread's interner; today's `Playlist.LocalPathOf` reads `id.Text` on the
   pump, which this replaces.)

`LocalTrackUri.StageFace` changes in two ways: it calls `LocalLibrary.NoteItem(uri)`, and it asks
`LocalLibrary.Index.Match(…)` (thread-safe) so a list that lands **after** the index is loaded is ruled correctly
in the same staging, with no greyed flash followed by a restate. Its always-unavailable line (`Track.LocalUri.cs:67`)
is replaced, not kept beside the new rule.

**Play path changes** (all in `Playback/Playback.Audio.cs` unless noted):

```csharp
// replaces RouteOf(EntityProvider) at :4532; the one caller is Open (:4551)
public static SourceRoute RouteOf(EntityId id) => id.Provider switch
{
    EntityProvider.Spotify => LocalLibrary.PathOf(id) is not null ? SourceRoute.Local : SourceRoute.Spotify,
    EntityProvider.WaveePodcast => SourceRoute.Podcast,
    EntityProvider.Local => SourceRoute.Local,
    EntityProvider.Module => SourceRoute.Module,
    EntityProvider.Fake => SourceRoute.Silent,
    _ => SourceRoute.None,
};
```

A gid-form Spotify id is never in the path map, so the extra probe on a normal Spotify open is one failed dictionary
lookup per track open (not per frame, not per buffer).

- `Platform/Modules.UI.cs:38` and `Screens/Diagnostics.Probe.cs:1535`: `Playback.Audio.LocalPath = LocalLibrary.PathOf;`
  `LocalLibrary.PathOf` answers a session-dropped file too (the drop path registers its id and path in the same
  map), so `Playlist.LocalPathOf` loses its last caller on the play path and is deleted with it if nothing else
  reads it (the uri codec `LocalFileUri` stays).
- `LocalSource` (`:4699`): `opened = new Opened(format, durationMs > 0 ? durationMs : LocalLibrary.DurationMsOf(id), …)`,
  so a local FLAC/MP3/Ogg has a length from the first frame instead of 0.
- `KindOfRow` (`Playback.Transitions.cs:932`) already treats `TrackFlags.Local` as a local file, so a matched
  playlist row needs no change there.
- The play gate needs no change: a matched row is no longer `Unplayable()`.
- A file that vanished between the scan and the play: `LocalSource` answers `Fault.Unavailable` (`:4643`) as today;
  additionally post one `LocalLibrary.Rescan()` so the row greys instead of failing again.

**Connect and reports** are section 3.14 (rewritten from the capture).

### 3.8 Settings: "Music on this PC" (all three themes)

Registry (`Screens/SettingsSpec.Storage.cs`, a new first group in `StorageCategory()`):

```csharp
Group("music", Strings.Settings.Groups.Storage.Music, Strings.Settings.Storage.MusicSub,
    // The folder list with its per-folder verbs (show in Explorer · remove) is one composite; its verbs are SettingsVerbs members.
    Make("storage.music.folders", K.Composite, Strings.Settings.Storage.MusicFolders,
        SettingsWriter.Owned("SettingsVerbs.RemoveMusicFolder/RevealMusicFolder"), glyph: "Folder",
        aliases: ["local files", "mp3", "flac"]),
    Make("storage.music.add", K.Action, Strings.Settings.Storage.AddMusicFolder,
        SettingsWriter.OfAction("music.addFolder", SettingsVerbs.AddMusicFolder), parent: "storage.music.folders", glyph: "FolderOpen"),
    Make("storage.music.addDefault", K.Action, Strings.Settings.Storage.AddMyMusic,
        SettingsWriter.OfAction("music.addDefault", SettingsVerbs.AddDefaultMusicFolder), parent: "storage.music.folders",
        visible: f => f.DefaultMusicFolderOffered),
    Make("storage.music.rescan", K.Action, Strings.Settings.Storage.RescanMusic,
        SettingsWriter.OfAction("music.rescan", SettingsVerbs.RescanMusic), parent: "storage.music.folders",
        enabled: f => f.MusicFolderCount > 0 ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true)),
```

(The named arguments are `Make`'s own: `legacy`, `parent`, `glyph`, `captionKey`, `enabled`, `visible`, `silent`,
`aliases`, `SettingsSpec.cs:418-423`. The `Strings.Settings.*` consts are generated from the new loc keys.)

- Persistence: `Platform.Keys.MusicFolders = new SettingKey<string>("library.musicFolders", "")`, the folders joined
  with `|` (not a legal path character on Windows). One string so a write is atomic, the reasoning the rate-nudge
  ledger states (`Platform/Platform.Settings.cs:38-39`).
- `SettingsVerbs.AddMusicFolder` → `Pickers.Pick(PickerRequest.Folder(title), done: path => LocalLibrary.AddFolder(path), …)`.
  A folder inside a listed folder is refused with a toast; a folder that contains listed ones replaces them.
- `ISettingsFacts` (`Screens/SettingsFacts.cs`) gains `MusicFolderCount` and `DefaultMusicFolderOffered`.
- The composite is drawn by each theme's Storage view, reading `LocalLibrary.Folders`, `Status` and `Epoch`.

Wireframes. The list region reserves one row's height while empty (the empty sentence sits in it), and a row that
appears or leaves animates its height with the theme's existing settings-row disclosure (`SizeMode.FlowReveal`),
so adding the first folder does not jump the page. The status line is one fixed-height line whose text is bound.

**WinUI** (`WinUiSettings.Body.Storage.cs`, card case added in `WinUiSettings.Rows.cs` next to `:271`): one
settings card with an expander, WinUI's own card and row kit.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 🗀  Music folders                                    [ Add a folder… ]   ⌃  │
│     Wavee plays the music in these folders, and the local files your         │
│     playlists list.                                                          │
├──────────────────────────────────────────────────────────────────────────────┤
│     D:\Music                     4,812 songs · 3 can't be played     ⋯   ✕  │
│     \\nas\share\flac             Drive offline · 1,204 songs kept    ⋯   ✕  │
├──────────────────────────────────────────────────────────────────────────────┤
│     Scanned 2 minutes ago                                  [ Rescan now ]    │
└──────────────────────────────────────────────────────────────────────────────┘
   ⋯ = Show in Explorer      ✕ = Remove (no confirm: it removes nothing from disk; a toast offers Undo)
```

**Classic** (`ClassicSettings.Storage.cs`, beside `StorageRow`, `:97-103`): the same content as Classic settings
rows, using `Row(…)` and `StdButton(…)` as `ClassicSettings.VideoOverrides.cs` does; folders are child rows under
the header row, the status and "Rescan now" a trailing child row.

```
 Music folders                                   [ Add a folder… ] [ Add my Music folder ]
 Wavee plays the music in these folders, and the local files your playlists list.
   D:\Music                         4,812 songs · 3 can't be played    [Show] [Remove]
   \\nas\share\flac                 Drive offline · 1,204 songs kept   [Show] [Remove]
   Scanning… 1,930 of 6,016                                            [ Rescan now ]
```

**Zune** (`ZuneSettings.Storage.cs`, composite case added in `ZuneSettings.Composites.cs` next to `:34`): Zune's
lowercase display type and text verbs; nothing is shrunk to fit, a long path wraps or fades at the plate edge.

```
 music folders
 wavee plays the music in these folders, and the local files your playlists list.

   D:\Music                                  4,812 songs
   \\nas\share\flac                          drive offline

   add a folder     add my music folder     rescan now          scanned 2 minutes ago
```
(a folder's "show in explorer" and "remove" are in its row's Zune "options" card, as the lyrics pane's ⋯ is.)

Compact tier in every theme: the folder row stacks (path over its status), and every target is at least 40 DIP.

### 3.9 Playlist surfaces

Shared core, no theme structure change except the bar's second action:

- **Reason text.** `Track.UI.Bound.cs:231`: `detail.trackFacts.localReason` becomes "A local file: not found in
  your music folders" (all three cultures: `en-US`, `nl`, `ko-KR`). A matched row has no withheld reason and reads
  like any playable row; its expanded drawer states the existing "Local file" fact (`FactKind.LocalFile`,
  `Track.Rules.cs:1356`, already drawn by all three theme drawers) because `Local` is now set on it.
- **The unavailable-songs bar.** `TrackUnavailableBarArgs` gains an appended, optional second action
  (`string SecondaryLabel = ""`, `Action? Secondary`), set only when `tally.LocalFiles > 0`: "Add a music folder"
  opens Settings at `storage.music.folders`. Each theme's bar template draws it beside "Hide them" when present:
  `Themes/Classic/Structure/Lists/ClassicTrackChrome.cs:54`, and the WinUI and Zune equivalents (same contract
  member; I read only the Classic one). This is a theme contract change and is declared as one (the repo's
  `wavee-contributing` rule: declared, not numbered). The bar's reserved extent does not change: the action sits in
  the existing action lane; in Compact it wraps under the sentence inside the Compact record's extent, which must
  be re-measured per theme.
- **Counts.** `UnavailableTally.LocalFiles` now counts only unmatched items, with no code change: it counts rows
  that are `IsLocalItem` **and** hidden by the playable filter (`Track.Table.Chrome.cs:1032`).
- **Row activation.** No change: a matched row is playable, so click, Play, shuffle and "play in context" build
  the queue from it like any row (`Playlist.Page.cs:892-903`, `Playback.PlayRows`).
- **Offline.** `OfflineGone` (`Track.UI.Bound.cs:197`) already exempts `t.IsLocal`, so a matched row stays lit and
  playable offline.

### 3.10 The Local Files list

No new page: the `local` route stays the playlist page's local arm (`Playlist.Page.cs:397,749`). What changes is
its content (3.7 step 1) and its empty state: with no folders and nothing dropped, the existing empty state gains
one action, "Add a music folder" (same verb). The drop hint and "Play ▸ File…" stay.

Rows now have an artist line and a duration. The album lane reads a name without an album entity the way a
playlist's local item already does (`Track.UI.cs:377`): extend that fallback from `IsLocalItem` to any `IsLocal`
row by asking `LocalLibrary` for the entry's album (a dictionary probe by slot id, cached per row render like
`LocalTrackUri.NamesOf`, never per frame).

### 3.11 Threads

| Thread | Does | Never |
|---|---|---|
| `local-scan` (one, below normal) | enumerate, read tags, `Index.Replace`, save the TSV | touch the entity tables, a signal, or the UI |
| UI | folder verbs, `Restate` (batched), the path map rebuild, Settings | read a file, enumerate a folder |
| api / store threads | `StageFace` → `Index.Match`, `NoteItem` (both lock-guarded) | |
| audio pump | `RouteOf` and `LocalSource` → `PathOf`, `DurationMsOf` (one lock, one probe) | allocate per buffer; nothing here is on the feed or render callback |

Steady frames allocate nothing: no per-frame code is added. The only per-row-render addition is the album-name
probe in 3.10, which returns a string already held by the entry.

### 3.12 Files

| File | Change |
|---|---|
| `Playback/LocalLibrary.cs` (new) | façade, folders, restate, path map |
| `Playback/LocalIndex.cs` (new) | entries, match maps, TSV |
| `Playback/LocalMatch.cs` (new) | normalisation, tolerance, tie-break (pure) |
| `Playback/LocalTags.cs` (new) | ID3v2/v1, MP4 `ilst`, lengths; calls the FLAC and Vorbis-comment readers |
| `Playback/LocalScan.cs` (new) | the scan thread, watchers, progress |
| `Playback/Playback.Audio.cs` | `RouteOf(EntityId)`; `LocalSource` duration and `Opened.DurationExact`; lift the comment-block walk into a shared helper; 3.13: `s_activeKind` at the hand-off commit, the overlap rule |
| `Playback/Playback.Transitions.cs` | 3.13: `MediaSwitch.AllowCrossfade` |
| `Playback/Playback.Host.cs` | 3.13: pass the next row's kind to `Audio.Prepare` (`:873`) |
| `Playback/Playback.Audio.Flac.cs` | expose `ParseHeaders`' tags to `LocalTags` (visibility only) |
| `Entities/Track.LocalUri.cs` | `StageFace` rules from the index; `NoteItem` |
| `Entities/Track.UI.cs`, `Track.UI.Bound.cs` | album name for any local row; reason text |
| `Entities/Track.Table.Chrome.cs` | fill the bar's secondary action |
| `Entities/Playlist.Page.cs`, `Entities/Playlist.cs` | Local Files content and empty-state action; drop extensions; `ImportLocalFile` reads tags through `LocalTags` and registers the path |
| `Theming/Contracts/TrackChromeStyle.cs` | `TrackUnavailableBarArgs` secondary action (declared contract change) |
| `Themes/{Classic,WinUi,Zune}/Structure/Lists/*TrackChrome.cs` | draw the secondary action |
| `Screens/SettingsSpec.Storage.cs`, `SettingsFacts.cs`, `Settings.Host.cs` | group, verbs, facts |
| `Themes/Classic/…/ClassicSettings.Storage.cs`, `Themes/WinUi/…/WinUiSettings.Body.Storage.cs` + `WinUiSettings.Rows.cs`, `Themes/Zune/…/ZuneSettings.Storage.cs` + `ZuneSettings.Composites.cs` | the composite |
| `Platform/Platform.Settings.cs` | `Keys.MusicFolders` |
| `Platform/Modules.UI.cs`, `Screens/Diagnostics.Probe.cs` | bind `LocalPath` to `LocalLibrary.PathOf` |
| `Spotify/Spotify.Egress.cs` | 3.14: the encoder's kept set (E1); `ForConnect` publishes the Local Files context and names its rows (E3, E4) |
| `Spotify/Spotify.Decode.PutState.cs` | 3.14: the local `playback_quality` arm (E5), restriction 13 `local_list` and the Local Files context metadata (E4) |
| `Spotify/Protos/player.proto` | 3.14: `PlaybackQuality` fields 6 and 7 (E5) |
| `Spotify/Spotify.Telemetry.cs`, `Playback/Playback.Host.Context.cs` | 3.14: `localContentUri` on the registration, the blanked `content_uri`, play history for a local play (E7, E8); refuse a remote `play` of `spotify:local-files` (D11) |
| `Playback/Playback.Host.Wire.cs` | 3.14: the wire row's artist and album for a local track (if the check finds it missing) |
| `Entities/Entities.cs` | 3.2: the four-part `spotify:local:` uri is `EntityKind.Track` |
| `App.cs` | `LocalLibrary.Use(path)` at start |
| `Entities/Entities.Fake.Library.cs`, `Screens/Diagnostics.Inspect.*.cs` | fixtures and an inspector state (section 5) |
| `assets/loc/en-US.json`, `nl.json`, `ko-KR.json` | new `settings.storage.music*` keys, reworded `localReason` and `localFile.rejected`, bar action |
| `changes/unreleased/*.md` | one change file per phase, `issues: [240]` |

No engine (fluent-gpu) change is needed by any phase.

### 3.13 The hand-off at a local boundary (owned here; #48 builds on it)

**What the code does today** (read, not run):

- The gate: `MediaSwitch.AllowCrossfade(from, to) => from == to && from == PlayableKind.Audio`
  (`Playback/Playback.Transitions.cs:1672`). A local file is `PlayableKind.LocalFile`
  (`KindOf`, `:1652`; `KindOfRow`, `:923-936`), so the gate refuses every boundary that names one.
- Both facts in the cross-plan note are true and do not contradict each other. Audio and LocalFile share the audio
  host (`HostHandoff.SameHost`, `:1683`: "a plain reload, nothing to hand over"), so nothing is stopped or swapped
  between them. The gate is about the **splice** (a prepared next voice overlapping or butt-joining the outgoing
  one). With the splice refused, the boundary is a reload on the same host: the outgoing track runs to its end,
  then the next one is opened cold.
- The gate is read in one place: `s_prepOverlap = MediaSwitch.AllowCrossfade(activeKind, kind)` when a prepared
  slot is adopted (`Playback/Playback.Audio.cs:3330`), and `HandOffAt` answers `None` without it (`:136`).
- `kind` there is `Audio.Prepare`'s parameter, which defaults to `PlayableKind.Audio` (`:809`), and the only caller
  passes no kind: `Audio.Prepare(s_fx.NextRow, s_fx.NextId)` (`Playback/Playback.Host.cs:873`). So in practice the
  gate tests only the **active** kind (`s_activeKind`, whose one write is the load path, `Playback.Audio.cs:1107`).
- A local FLAC, MP3 or Ogg opens with `Opened.DurationMs = 0` (`:4699`), so `s_activeDurMs` is 0 (`:1215`) and
  `EndgamePlan.Decide` returns `Wait` at its first line (`Playback/Playback.Endgame.cs:57`): the endgame never
  asks for a prepare while such a file plays.

| Boundary today | What happens |
|---|---|
| Spotify → Spotify | The user's setting: crossfade, or a gapless join when the fade is 0. |
| **local → local**, **local → Spotify** | Hard reload. Refused twice over: the active kind is LocalFile, and for FLAC/MP3/Ogg the duration is 0 so nothing is even prepared. The listener hears the track end, a short silence while the next opens, then the next track. |
| **Spotify → local** | By the code as written the gate **passes** (active = Audio, the next kind defaulted to Audio), the local file is prepared through the ordinary `Open(id, …, prepared: true)` → `LocalSource` path, and the boundary crossfades or joins. This contradicts the comment at `Playback.Audio.cs:3327-3328` ("either → a local file changes HOSTS") and looks unintended. I did not run it. After such a splice `s_activeKind` is still Audio (nothing writes it at the commit), so the following boundary is decided on a stale kind. |

**Recommendation for 0.4: lift the gate.** Local and Spotify audio are the same host, the same mixer and the same
decoders' output, and the prepare path already stamps the mix rate the next voice is resampled for
(`PrepareContext.For(session.Format, …)`, `Playback.Audio.cs:3316-3317`). What made a cut the safe answer was the
unknown length of a local file, and P1 removes that. The rule becomes:

```csharp
// Playback/Playback.Transitions.cs: replaces the body at :1672
/// <summary>Whether a prepared-next splice (crossfade or gapless join) is allowed across this boundary: both sides
/// play on the audio host. Every boundary that touches video is a hard cut.</summary>
public static bool AllowCrossfade(PlayableKind from, PlayableKind to)
    => from != PlayableKind.Video && to != PlayableKind.Video;
```

What it takes, all app-side (no engine change):

1. **A known, trustworthy length for the active local track.** `Opened` (`Playback.Audio.cs:4495`) gains
   `bool DurationExact = true`. `LocalSource` fills `DurationMs` from the index (3.7) and sets `DurationExact` from
   the entry: exact for FLAC (STREAMINFO), MP4 (the sample table), Ogg Vorbis (the last granule) and MP3 with a
   Xing/Info block; **not exact** for an MP3 or ADTS file whose length is a bitrate estimate.
2. **No splice on an estimated length.** A join or a fade is scheduled against the duration
   (`GaplessJoinClock.JoinFrameFor`, `Playback.Audio.cs:1224`; `HandOffAt`, `:137-138`), so an estimate that is
   short would cut the tail off the song. When the active track's length is not exact, `s_prepOverlap` is false
   for that boundary and it stays today's reload at the real end. The existing `[gapless] arm … reason=` line
   (`:4038-4040`) gains a reason code for it, so the log says why a boundary was a cut.
3. **The next row's kind reaches the gate.** `Playback.Host.cs:873` passes the next row's kind
   (`KindOfRow(s_fx.NextRow, videoWanted)`, the call the load path uses), so a next row that will load as video
   is refused by the gate, not admitted by a default.
4. **`s_activeKind` follows a splice.** Where a crossfade or a join commits and the prepared slot becomes the active
   voice, the prepared kind is written to `s_activeKind` (kept beside `s_prepDurMs`, `:3327`). This fixes the stale
   kind noted above for every boundary, not only local ones.
5. **The prepare path for a local next row** needs nothing new: `Open` routes to `LocalSource`, the tail wait
   applies only to a `RingSource` (`:3307`), and the decoder is chosen from the sniffed format.

**What a boundary does in 0.4, after this:**

| Boundary in 0.4 | What happens |
|---|---|
| local → local, local → Spotify, Spotify → local, outgoing length exact | The user's setting, exactly as Spotify → Spotify: a crossfade of the set length, or a gapless join at 0. The queue's Crossfade switch applies unchanged. |
| the outgoing local file's length is an estimate (MP3 without Xing, raw ADTS) | Reload at the real end, as today. Never a truncated tail. |
| anything ↔ video | Hard cut, unchanged. |
| Mix (#48) | Never applied to a boundary that touches a local track; that boundary gets the rows above. |

Loudness across the fade: a local file's gain comes from its ReplayGain tags when it has them
(`ReplayGainTags`, `Playback.Audio.cs:1618`) and is 0 dB otherwise, so an untagged file fading into a normalized
Spotify track can step in level. That is true of a hard cut too; measuring the loudness of untagged files is
"After 0.4".

### 3.14 Connect, remote commands and reports for a local play (from the capture)

Each row is a difference the report lists in its section 5, checked against the 0.3.13 code, with the phase that
fixes it. "Verified" means I read the cited lines.

| # | Report 5.x | Wavee 0.3.13 (verified) | Official client | Fix | Phase |
|---|---|---|---|---|---|
| E1 | 5.1 encoder | `Egress.LocalTrackUri` keeps `A-Za-z0-9 - _ . *` raw (`Spotify/Spotify.Egress.cs:120-121`); truncation of the seconds is already right (`:98`, `durationMs / 1000`) | keeps `A-Za-z0-9 - _ . ~`; `*` is `%2A` | Swap `*` for `~` in the kept set. One line. A title with `*` or `~` is named differently from the official client today. | **P1** (a fix for dropped files too) |
| E2 | 5.2 where the uri comes from | `Egress.TrackUri` formats a `spotify:` id as is (`:142-143`) and rebuilds a `wavee:local:` track's uri from its tags (`:144-147`) | publishes the playlist item's string unchanged, with that row's uid | Holds by design under D1(a): the matched row keeps its `spotify:local:` text-form id, so the first arm applies and nothing is rebuilt. The rebuild arm stays for Wavee's own Local Files rows, which have no other name. **To check in P4:** `snap.Uid` is the item_id hex for a locally started playlist play (unverified item 3). | **P4** |
| E3 | 5.3 local rows in prev/next | `Kept` drops every row whose id is not `spotify:` (`:207-215`); `Clean` fails on one (`:188-194`) | lists local rows in `prev_tracks`/`next_tracks` with uri and uid | A playlist's local rows are `spotify:` ids under D1(a), so they are kept as they are. Wavee's own Local Files rows are `wavee:local:file:` ids and are dropped today: E4 gives them a name. | P4 (free); **P3** for E4 |
| E4 | 5.4 the Local Files context | `ForConnect` drops a non-Spotify context, its origin and its rows (`:159-185`); no `local_list` anywhere (searched): the put-state's restrictions block (`Spotify/Spotify.Decode.PutState.cs:382-409`) writes no field 13, and the only other mention of `disallow_transferring_playback_reasons` is an empty key in the set-queue JSON (`Spotify/Spotify.Connect.Outbound.cs:646`); the proto has the field (`Spotify/Protos/player.proto:85`) | `context_uri: "spotify:local-files"`, `context_url: "context://spotify:local-files"`, `play_origin.feature_identifier: "local_files"`, `context_metadata` (`context_description: "Local Files"`, `context_owner: "spotify"`, `format_list_type: "local-list"`, `playlist_number_of_tracks`), every file in `next_tracks` as its `spotify:local:` uri with a 16-hex uid, `restrictions.disallow_transferring_playback_reasons: "local_list"` | In `ForConnect`: when the context is the Local Files list (`wavee:local:playlist:all`), publish it as `spotify:local-files` instead of dropping it; name every `wavee:local:file:` row in the window by `Egress.LocalTrackUri` from its tags (a row with no title is left out, as today); uid = the row's own queue item id as 16 hex (`QueueUid.Format`, `Playback/Playback.Transitions.cs:433`); write restriction field 13 `local_list` and the four context-metadata keys in the put-state encoder. Paths never leave: the uri is built from tags only (`Spotify.Egress.cs:89`). | **P3** |
| E5 | 5.5 playback_quality | always `w.U(1, 3); w.U(2, 4); w.U(3, 3); w.B(4, true); w.U(5, 1)` = high · cached_file · high · available · off (`Spotify/Spotify.Decode.PutState.cs:434-436`); `PlaybackQuality` has fields 1-5 only (`player.proto:173-179`); `local_file = 5` exists (`:164`), `veryhigh = 4` (`:153`) | `strategy: local_file (5)`, `target_bitrate_level: veryhigh (4)`, `hifi_status: off (1)`, field 7 `file_origin = 3`; no field 1, no field 4 | A local arm in the encoder: `w.U(2, 5); w.U(3, 4); w.U(5, 1); w.U(7, 3);` when the deck row is local (`TrackFlags.Local`, carried on the snapshot as one bool). Add fields 6 (`highest_available_quality`) and 7 (`file_origin`) to `PlaybackQuality` in `player.proto` so the schema matches what is written and read. The streamed arm stays as it is (see the note under the table). | **P3** |
| E6 | 5.7 metadata keys | `Head` writes `title`, `artist_name`, `album_title` on every row (`Spotify.Decode.PutState.cs:557-559`) | writes none; controllers parse a local name from the uri | Nothing to do. No tag-derived names are added for local rows. | none |
| E7 | 5.9 telemetry | the schema has `local_content_uri = 41` (`Spotify/Protos/event_sender_events.proto:49`) and nothing sets it (searched); `content_uri` is the registration's uri (`Spotify/Spotify.Telemetry.cs:448,561,593`); a non-Spotify or non-playable id never registers (`Playback/Playback.Host.Context.cs:1336`) | `content_uri: "spotify:local::::"`, `local_content_uri` = the real uri, `media_id` empty, `audio_format` "MP3/Unknown" or "Unknown"; `RawCoreStreamSegment.content_uri` blanked the same way | `Telemetry.Started` gains `string localContentUri = ""`. When set: `ContentUri = "spotify:local::::"` on every event of that registration and `LocalContentUri` on `RawCoreStream`; `media_id` stays empty (it already is when the id is not a gid, `Host.Context.cs:1345`). The caller passes the playlist row's uri (P4) or the minted uri of a Local Files row (P3). | **P3** (the arm, Local Files plays), **P4** (playlist rows) |
| E8 | 3.6 play history | `Telemetry.PlayHistory(uri, unix)` after a start, unless private (`Host.Context.cs:1358`); only `spotify:` playable ids reach it | herodotus `item_uri` = the local uri | Call it with the same uri as E7. The official client also writes the **playlist's resume point**; Wavee writes resume points for episodes only (`Spotify.Telemetry.cs:17`), so that stays out. | P3 / P4 with E7 |
| E9 | 5.8 parsing | `TryParse` accepts the 4-part form and empty artist or album (`Entities/Track.LocalUri.cs:31-53`); `StageFace` stages seconds × 1000 and `TrackFlags.Unavailable` (`:66`) | same parse; pathfinder reports seconds × 1000 too | The grey-out becomes the match (3.7). When a matched file plays, the deck's duration is the file's real ms: the open posts it (`Playback/Playback.Audio.cs:1270`) once `Opened.DurationMs` is filled from the index. | P4 |
| E10 | 5.10 advertising | `audio/local` is already in `supported_types` (`Spotify.Decode.PutState.cs:739`) | same | Nothing to do. | none |

Note on E5: for a **streamed** track the official client writes `strategy: best_matching`, field 6 and
`file_origin = 1`, where Wavee writes `cached_file` for everything. That difference is older than local files and
is not part of #240; it is listed here so it is not lost.

**Remote commands (report 3.5, 6.3).**

- A remote `play` that targets a playlist's local row names only `skip_to.track_uid` (the item_id hex); the uri is
  never in the command. Wavee already resolves a start row by uid against its own copy of the context
  (`RemotePlan.StartIndex(buffer, context, load.SkipToUid, …)`, `Playback/Playback.Host.Context.cs:516`). Two
  things are needed in P4: the kind arm of 3.2, so `AddRow` does not drop the row (`:681`), and nothing else for
  the outcome: a matched row plays, an unmatched one is `Unplayable()` and the play gate steps to the next playable
  row (`Playback.Transitions.cs:950-962`), which is what the iPhone and the web player do (report 3.9).
- A remote `play` of the context `spotify:local-files` is **refused** in 0.4 (D11): no load, no origin captured,
  one log line. This also removes the trigger of the bug in section 7, but it is not that bug's fix.
- While Wavee plays its own Local Files list it publishes `local_list` (E4), so a controller does not offer a
  transfer of that context.

**What never leaves the PC.** A path, a folder name, the folder list, file counts and scan timings. The official
client sends source telemetry that includes an absolute folder path (`LocalFilesSourceEvent`, report 3.8); Wavee
sends none of the `LocalFiles*` events. `Egress.NamesWaveeUri` stays the backstop for a body that names a
`wavee:` uri (`Spotify.Egress.cs:46-48`; the gabo sender asks it, `Spotify.Telemetry.cs:269`).

**The wire window's names.** `WireRow.ArtistName` and `AlbumTitle` (`Playback/Playback.Wire.cs:82-83`) feed
`Egress.TrackUri`'s rebuild for a Local Files row (`Spotify.Egress.cs:146-147`). The row is built in
`Playback/Playback.Host.Wire.cs:283-332`; I did not read how a local track's artist and album get there. If it
reads artist and album entities only, that fill gains a local arm from the row's `ArtistLine` and the index entry's
album (S, in P3). Without it a tagged local file would be published with an empty artist and album.

---

## 4. Phases (all in 0.4; each one ships on its own)

| # | Phase | What the user gets | Size | Engine |
|---|---|---|---|---|
| P1 | Tags and the index, no UI: `LocalTags`, `LocalMatch`, `LocalIndex`, `LocalScan`, `LocalLibrary` (folders API, no restate yet); `LocalSource` takes its duration from a one-off `LocalTags.TryRead` for a dropped file; `ImportLocalFile` stages the real title and artist; **the uri encoder's kept set (E1)** | A dropped file shows its real title, artist and length instead of its file name, and is named to Connect as the official client would name it | L | none |
| P2 | Music folders: the Storage group in all three themes, the scan wired to it, `Restate` step 1 and step 3, the Local Files list filled from the folders, its empty-state action, the drop extensions, **and the base hand-off for local tracks (3.13)** | "General local files playback": add a folder, browse and play it from Local files, offline too, with the same crossfade or gapless join as any other song | L | none |
| P3 | **Connect and reports for a local play (3.14):** the Local Files list published as `spotify:local-files` with its rows and `local_list` (E3, E4), `playback_quality` local_file with `file_origin` and the two proto fields (E5), the telemetry local arm and play history for Local Files plays (E7, E8), the wire row's artist and album, the refusal of a remote `play` of `spotify:local-files` (D11) | Another device shows what Wavee plays from Local files the way it shows the official client's, and cannot start a transfer that would fail | M | none |
| P4 | Playlist rows: the `spotify:local:` kind arm (3.2), `StageFace` from the index, `Restate` step 2, `RouteOf(EntityId)`, the uid check (E2), telemetry and play history with the playlist's own uri (E7, E8), a remote `play` by uid (3.14), the Liked Songs check (unverified item 4), the reworded reason | #240 itself: a playlist's local files play in place, from Wavee or from a remote | M | none |
| P5 | The bar's "Add a music folder" (contract change, three themes) and the always-on match log lines | An unmatched row tells the user what to do about it | S | none |

P1 → P2 → P3 → P4 → P5 in order; P5 could ship with P4, and P3 and P4 can swap if #240's rows are wanted first
(P4 then carries the telemetry arm). Each phase ends with a clean Debug build and the inspector pass in section 5,
and adds its own change file.

**Changed by the capture:** P1 gains one line (E1). P3 is new (M). The old P3 is P4, still M: it loses the
"Connect only" gate and gains the kind arm, the uid check and the reports. The old P4 is P5. Total: L, L, M, M, S.

### After 0.4

- Embedded cover art for local tracks (D8), once the image pipeline's file or bytes source is confirmed.
- New decoders: WAV/PCM, Ogg Opus, ALAC (D7). The index already records these files as unplayable, so the day a
  decoder lands a rescan lights them up.
- Local albums and artists as browsable entities (today a local file's album and artist are names, not rows).
- Loudness analysis of local files without ReplayGain tags (3.13).
- Fuzzy matching beyond the loose key (edit distance, acoustic fingerprint).
- Liking a local file and adding one to a Spotify playlist from Wavee (D12): `collection/v2/write` with the uri, and
  a playlist ADD with the uri and a fresh 8-byte `item_id`. Both use the official encoder (E1).
- Honouring a remote `play` of `spotify:local-files` (D11), once the uid question in section 6 is answered.
- The playlist's resume point in herodotus after a local play (the official client writes one; Wavee writes none
  for any playlist).
- The streamed-track `playback_quality` parity noted under 3.14's table.
- BPM and key analysis of local files for #48.
- Local video files in folders as music videos (today: drop only, through the video-override path).

---

## 5. Risks, unknowns and verification

| Risk | Why it matters | Mitigation |
|---|---|---|
| Matching the wrong file | The wrong song plays under a playlist row | Duration is a hard filter on every tier; the loosest tier needs both durations; deterministic tie-break; every decision logged. |
| The official client's matching rules and the uri of a tagged file are unknown (to confirm against the capture; every file in it is untagged) | Misses on tagged libraries | The untagged case is now exact (file name, truncated seconds). For tagged files: both artist spellings are indexed, the loose tier follows, and the match log names the tier. A second capture with a tagged, a multi-artist and a `*`/`~` title file is asked for before P4 ships. |
| A report the server rejects | A failed put-state or event (0.3.3 had a 422 for a different local leak, `Spotify.Egress.cs:4-5`) | Every shape in 3.14 is copied from a body the server accepted in the capture, and each is checked once in a real session (secondary monitor, no `--spotify-*` probes) with the official client watching the cluster. |
| The row uid | A controller that cannot find Wavee's current row by uid shows the wrong row or none | Unverified item 3: trace `snap.Uid` first thing in P4. |
| The kind arm changes a gate elsewhere | A `spotify:local:` id becomes playable for every `IsPlayable` caller at once | Read each caller listed in 3.2 before the change; the unmatched row is still `Unplayable()`, which the play gate honours. |
| Clearing `Unavailable` on an already committed row | A matched row stays grey until restart | Unverified item 2: read `Store`'s group merge first thing in P4; if it ORs flags, add a clearing table write. |
| ID3 in the wild | v2.2, UTF-16 without BOM, unsynchronised frames, 10 MB `APIC` | Frame walk by seeking, sizes bounded by the tag length, every read in `try`, a bad tag falls back to the file name. No third-party tag library (TagLibSharp is LGPL and reflection-heavy for NativeAOT with full trimming; the four readers needed are small). |
| MP3 length without a Xing block | A VBR file's estimated length is off, so the duration filter rejects a true match | Estimated lengths are flagged in the entry and get a 5 s tolerance on tier 1 only. |
| Large libraries | A long first scan; a long restate | Dedicated below-normal thread; incremental by size and mtime; restate in 2,000-row batches; the saved index makes every later start instant. |
| Network and removable folders | Watcher faults, slow enumeration, rows vanishing | Offline folders keep their entries; a faulted watcher degrades to start and manual rescans. |
| Privacy | A path contains the Windows account name | Paths stay in the index file and the `wavee:local:file:` id, which `Egress` already refuses (`Spotify.Egress.cs:46-48`); the match log prints the file name only, never the folder. The official client's folder telemetry is not mirrored (3.14). |
| MSIX file access | A packaged app's access to arbitrary folders | Wavee already opens dropped files and a user-chosen cache folder by path; treat as low risk but check one folder outside the user profile in the Store build. |
| Local Files persistence (unverified item 1) | Whether dropped files survive a restart today is unknown | With P2 the folders are the durable source; dropped files outside them stay session-only. State that in the change file. |

**Verification per phase** (no tests; a clean Debug build with `dotnet build src/apps/Wavee/Wavee.csproj` into an
artifacts path, then the headless inspector on a copy of the exe, screenshots read):

- **Fixtures first (P1).** `Entities.Fake.Library.cs` gains: tags on the fake local tracks, and three
  `spotify:local:` items in one fake playlist (one that matches a fake local track, one that does not, one with an
  empty artist). The inspector gains one state, `state kind=music-folders value=none|scanning|ready|offline`, that
  seeds `LocalLibrary` with a fixed in-memory folder list and index (no disk, no thread), beside the existing
  `state kind=…` vacancies.
- **P1:** render the Local files route in all three themes at each theme's board sizes; a dropped fixture file's
  row shows title, artist and length; `lint min=error` clean. A real MP3, FLAC, Ogg and M4A played once in a
  real-window run (secondary monitor, never focused) to confirm lengths and seeking.
- **P2:** Settings ▸ Storage in Classic, WinUI and Zune, light and dark, Compact and Standard, in each
  `music-folders` state; `trace` over "none → ready" and over a folder removal with the gate on (no part moved at
  once, nothing rebuilt); `crossing` at each tier edge the Storage page spans, both ways; `lint min=error` clean
  and `min=warn` read. The Local files route with 0, 12 and 5,000 fixture rows (the list virtualizes; check the
  frame cost with the inspector's perf verb while scrolling).
- **P2, the hand-off (3.13):** a base-commit run first, to confirm the "today" table. Then a real-window run
  (secondary monitor, never focused) with a folder of FLAC and of Xing-less MP3, crossfade at 5 s and at 0: read
  the `[gapless] arm` lines in the audio log for local → local, local → Spotify and Spotify → local (a hand-off
  with `overlap=True` for the exact-length files, the new estimate reason and a reload for the others), and listen
  to each boundary once.
- **P3:** there is no UI. A real-account run with the official desktop open as the observer: play from Local
  files and compare the cluster's view of Wavee with the report's section 3.4 field by field (context uri, origin,
  `format_list_type`, `local_list`, the rows' uris and uids, `playback_quality`); send a `play` of Local Files from
  the official client to Wavee and confirm nothing changes on Wavee and its published origin stays what it was.
  The inspector's transport verbs cover the headless half: the put-state bytes for a fake local row are dumped and
  read against the same list.
- **P4:** the fake playlist in all three themes: the matched row is lit and playable, the unmatched one greyed with
  the new tooltip, the bar counts one fewer; `trace` over the restate (index lands while the playlist is open): the
  row changes colour without moving. Play the matched row headless and check the transport and queue verbs' reply.
  One real-account run with the capture's own case: the playlist that lists the untagged MP3, the file in a music
  folder. Expect in the official client: the same uri, the row's uid, the real duration, `local_file`; then a
  remote `play` of that row from the official client to Wavee, and of a local row Wavee has no file for (it must
  move to the next playable row).
- **P5:** the bar with its second action in three themes at Compact and Standard; the action opens Settings on the
  group; `lint` for a verb offered twice (the bar and the empty state must not both show on one page).
- Throughout: the log's `local` category read after each run; the frame-allocation counter unchanged on a steady
  playlist frame.

---

## 6. The capture checklist, answered

Source: the capture report (kept outside the repo: it holds account data). Quotes are the report's words.

| # | Question | Status | The report | Effect on the plan |
|---|---|---|---|---|
| C1 | Which tags feed the parts, and how are they encoded? | **Encoding: answered differently. Tags: still open.** | 4.1: "unreserved = `A-Z a-z 0-9 - _ . ~`; space = `+`; everything else, including `:` `+` `%` `/` `'`, is `%XX` in **uppercase** hex over **UTF-8** bytes." 5.1: Wavee "keeps `A-Za-z0-9 - _ . *` raw". 4.2: "There is no example with an artist or album". | E1 (P1): the kept set is fixed. Matching is unaffected (decoded text). Which tags are read stays marked in 3.4. |
| C2 | The uri of a file with no tags? | **Confirmed.** | 4.1: "an untagged file is `spotify:local:::<title>:<seconds>`". 6.1: "For untagged files the official client uses the file name (without its extension) as the title." | 3.4: the file-name rule moves into tier 1 and tier 2; no longer a guess. |
| C3 | A multi-value artist tag? | **Still open.** | 4.2: "All 15 local files in the capture are untagged". | 3.4 keeps both artist keys and the mark. |
| C4 | Seconds rounded or truncated? | **Answered differently: truncated.** Unknown-length case still open. | 4.1: "`<seconds>` is the file's duration in **whole seconds, truncated**: the file playing for 252 839 ms is named `:252`". | 3.4: `DurationAgrees` truncates the entry; tolerances drop from 2 s / 3 s to 1 s / 2 s. |
| C5 | What does the playlist item carry? | **Confirmed**, plus the uid rule. | 3.1: "No format attributes, no length or flag on the item: the uri is the only description of the file." and "The `uid` other clients use for the row … is **`item_id` as lowercase hex**". | No stricter match is possible. E2: the uid must be the item_id hex (unverified item 3). |
| C6 | Connect put-state for a playlist's local item? | **Confirmed**, with two additions. | 3.4: `track.uri` and `requested_uri` are the playlist's uri, `uid: "28e348e99b3acf93"  # = the playlist item_id`, `duration: "252839"  # the FILE's real length, ms`, `playback_quality { strategy: local_file (5) … 7: 3 }`. | D1(a) stands. E5 (`playback_quality`, proto fields 6 and 7) and E9 (real ms) are new work. Wavee writes no `requested_uri` for any row today (searched) and adds none. |
| C7 | A local file played from Local Files? | **Answered differently.** | 3.4: `context_uri: "spotify:local-files"`, `play_origin { feature_identifier: "local_files" … }`, `restrictions { disallow_transferring_playback_reasons: "local_list" … }   # only for this context`. | D10, E3, E4 (P3): the context is published, not dropped. |
| C8 | Play history and events? | **Answered differently: the official client reports, and the server takes it.** | 3.6: "Local plays are written to the account's **play history** and to the playlist's **resume point**". 3.8: `RawCoreStream` "`content_uri: "spotify:local::::"` (the four parts **blanked**), `local_content_uri` (field 41): the real `spotify:local:…` uri". | D6 reversed; E7, E8. The `Egress.IsLocalItem` gate is dropped from the plan. |
| C9 | Any metadata asks for a local uri? | **Answered differently, no effect.** | 3.3: "`spotify:local:…` … **404** every time", except CONTENT_CAPABILITY_TRAIT; "The clients make up a local track's metadata themselves". | Wavee keeps not asking (`Entities/Fetch.cs:202`); the face stays the uri's (`StageFace`). |
| C10 | What other devices do? | **Confirmed** for a device without the file. | 3.9: the web player "**skips the local row**"; the iPhone "started the playlist at its first Spotify track. Its `next_tracks` / `prev_tracks` **left the local row out**". | 3.14: an unmatched row on Wavee steps to the next playable row; no design change. |

**Still open: the capture cannot answer these** (report 6, "Open questions"). Each keeps the mark "to confirm
against the capture" where the plan depends on it.

1. **Matching rules.** How the official client matches a uri to a file (tags or file name, the duration tolerance,
   case and Unicode normalisation) happens on the device. Section 3.4 is Wavee's own rule.
2. **The uri of a tagged file.** Every file in the capture is untagged; that artist and album are encoded like the
   title comes from the web player's formatter, not from an example. Multi-value artist tags are unknown (C3).
3. **Local Files uid generation.** How the client mints the 16-hex uids of rows in `spotify:local-files` (random or
   derived from the path). Decides D11; Wavee mints its own (E4).
4. What the seconds part is when the client does not know a file's length (C4).
5. Whether a desktop or phone that **has** the matching file plays the row after a transfer.
6. The legacy collection's type number for `local_track` (one sample, type 2); matters only for the Liked Songs
   read check in P4 and the write side after 0.4.

A second capture that would close 1 to 4: a tagged MP3 with one artist, one with two artist values, one whose title
contains `*`, `~` and a non-ASCII letter, and one file the client cannot measure; each added to a playlist and
played once.

---

## 7. A probable existing bug in shipped Wavee (not a 0.4 feature item)

Reported in the capture report's section 5.6; it concerns the released app and should get its own issue and a fix
on the open release branch, independent of this plan.

**Observed (capture, 23:21:17):** the official desktop sent a Connect `play` of the context `spotify:local-files`
to Wavee, twice. Wavee kept playing its album, but its next put-state published
`play_origin.feature_identifier = "local_files"` and the command's `playlist_play_request` / `playlist_query` in its
`context_metadata`. So Wavee told the cluster it was playing from Local Files while it played an album.

**What the code shows (read, not reproduced):**

- `StartContext` captures the command's origin first (`CaptureOrigin(in load, buffer)`,
  `Playback/Playback.Host.Context.cs:507`; the capture itself is `Playback/Playback.Host.Wire.cs:53-70`, which
  overwrites `s_wireOrigin` and `s_wireOriginContext`).
- Only afterwards does it find that the load has no row to play and return
  (`"context load had nothing to play"`, `Playback.Host.Context.cs:543-547`). The old track keeps playing; the new
  origin stays.
- The put-state's extras carry `s_wireOrigin` as it is (`Playback.Host.Wire.cs:129`), and the encoder writes its
  metadata into `context_metadata` (`Spotify/Spotify.Decode.PutState.cs:426-428`). Other paths compare the playing
  context with `s_wireOriginContext` before using or keeping the origin (`Playback.Host.Wire.cs:115,159`,
  `Playback.Host.cs:600`); I did not read whether one of them should have cleared it here.

**Likely cause:** the origin is committed before the load is known to start anything. **Likely fix:** capture the
origin only once a start row is chosen (after the nothing-to-play return), or restore the previous origin on that
return. The same would happen for any remote `play` whose context resolves to nothing playable, not only
`spotify:local-files`. This is the report's hypothesis and mine; neither of us reproduced it.

