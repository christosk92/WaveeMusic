# XM kind probe overview (one-batch decode)

One `POST /extended-metadata/v0/extended-metadata`: **7 entities × kinds 1–258** (skip 104). 2026-08-15.
Envelope: `docs/plans/wavee/xm-playcount-handoff.md`. Do **not** implement unless asked.

| Entity | URI |
|---|---|
| Time | `spotify:track:6ZFbXIJkuI1dVNWvzJzown` |
| Blinding Lights | `spotify:track:0VjIjW4GlUZAMYd2vXMi3b` |
| Never Gonna Give You Up | `spotify:track:4uLU6hMCjMI75M1A2tKUQC` |
| Mr. Brightside | `spotify:track:3n3Ppam7vgaVa1iaRUc9Lp` |
| Shape of You | `spotify:track:7qiZfU4dY1lWllzX7mPBI3` |
| Hans Zimmer | `spotify:artist:0YC192cP3KPCRWx8zr8MfZ` |
| Interstellar Expanded | `spotify:album:3B61kSKTxlY36cYgzvf3cP` |

HTTP 200, uncompressed 621 KB, **1799** inner rows, **370** with `Any.value`. Inner mix: 438×200 / 1092×404 / 175×400 / 48×502 / 28×501 / 7×503 / 7×504 / 2×500 / 2×204.

Raw dump (gitignored): `tmp/xm-probe/one-batch.json`. Field walks: `tmp/xm-probe/findings-*.md`.

**Batch stress:** 5xx on leftover kinds (78 PLAYABILITY, 211 CONTENT_BY_OR_ABOUT, video-preview 117/120, …) is **not** a proven 404. Catalogue / credits / video / mix / artist-section families in this POST were clean 200/400/404 except 172 (502×6) and 173 album/artist (500).

---

## What to actually add

Highest value, in order. Wavee already fetches the ones marked *already*.

1. **185 on tracks** → `PlayCount` (`f3`). Confirmed: Time 481,338,906 … Shape of You 5,051,477,464.
2. **185 on artists** → monthly listeners (`f1`) + verified (`f2`) + followers (`f4`). **Different schema.** Album **404**.
3. **186 on tracks** → credits drawer (grouped roles + label). 232 = 186 + MusicBrainz. 96 is a thin subset.
4. **183 on albums** → calendar date + two ©/℗ lines. Tracks are date-only (27 B). Trust `f1`, not sentinel unix `413146860`.
5. **239 on tracks** → offline slot on all five; video slot grows on Brightside + Shape of You (same two that have 99).
6. **16 on tracks** → canvas mp4 + posters. 1 ≡ 16 (byte-identical). Only Brightside + Shape of You paid. 404 = none.
7. **181 on tracks** → preview URL + **relink**. Rick `4uLU6hMCjMI75M1A2tKUQC` → `4PTG3Z6ehGkBFwjybzWkR8` (116 404s on the old id).
8. **Artist page XM** (GraphQL already covers some): 152 popular releases, 165 related, 169 top playables, 203 appears-on, 137→204 concerts, 209/213 videos, 184 about, 255 facts, 206 headline, 151 playlists, 138 prerelease.
9. **153 on albums** → related releases (includes Inception + the Ice Age prerelease).
10. **28 primary IN/OUT** only if skip-intro / fade-out is wanted. 217/218/247/142 stay Mix-lens.

*Already:* 5, 6, 8/9/10, 99, 138, 151 (album), 178/179/182, 212, 222, 237.

Do **not** fetch 60 (404 everywhere). Do **not** treat 118’s `"Music video"` as has-video. Do **not** write artist-185 `f1` onto a track. Do **not** read playcounts from V4 `popularity` or 168 `f3=6185314`.

---

## 1. Reputation / flags

| Kind | Who | Wire | Verdict |
|---|---|---|---|
| **185** | 5 tracks 6 B; Zimmer 18 B; album **404** | track `f3` = plays; artist `f1=5269983` `f2=1` `f4=12739453` `f9{1,1}` | **FETCH** track+artist |
| **239** | tracks 30 B or 57 B; artist/album empty 16 B | slot 2 `offline=1` on all tracks; slot 4 `music-video-disabled=1` only Brightside + Shape | **FETCH** tracks |
| 251 | tracks 115 B; artist/album 404 | licensed track/album/artist URIs; Rick remaps to `4PTG3Z6ehGkBFwjybzWkR8` | fetch if remap |
| 49 | tracks `f1=1`; artist/album empty-200 | add-to-playlist gate | optional |
| 173 | tracks identical 10 B; artist/album **500** | highlightable | tracks only if UI |
| 227 | tracks+artist `f1=1`; album 404 | SongDNA gate for 232/236 | fetch if SongDNA |
| 48 / 149 / 158–160 / 197 | identical on all 7 | tautology / playlist leftovers | **SKIP** |
| 127 / 180 / 220 / 249 | track=1 album=2 artist=11 (220 only) | URI-scheme echo | **SKIP** |
| 246 | 3 identical playlist URIs on all 5 tracks | account-constant, max 1/POST | **SKIP** |
| 257 | tracks `f1=1`; artist/album 404 | speed allowed | skip unless speed UI |

---

## 2. Catalogue / identity

V4 is **type-gated** (wrong scheme → 400). **No playcount** in 8/9/10 — only `popularity` (14–184) + timestamps.

| Kind | Who | What |
|---|---|---|
| **10 TRACK_V4** | 5 tracks 713–767 B | name, album, ISRC, files, duration, lyrics flag, `canonical_uri`. Rick: no `file[]`, playable via `alternative` + restriction. Brightside+Rick: canonical ≠ requested. Time: `zxx`, no lyrics. |
| **9 ALBUM_V4** | album 1363 B | WaterTower, 2014-11-18, UPC `794043201943`, 29 track gids, two ©/℗, cover. |
| **8 ARTIST_V4** | Zimmer 18 KB | popularity 158, top-track gids (includes Time), discography groups. |
| **178** | all 7; artist **1405 B** | type + name + parent uris; artist `f3` = 1381 B bio. Wavee already requests, does not project. |
| **179** | all 7 | covers + colours. Canvas `f4` on Brightside / Rick / Shape. Artist/album extra header art on `f6`. |
| **182** | tracks+album; artist 404 | duration-seconds + experience mask. `0x02` = video-ish (Brightside/Rick/Shape). Not a substitute for 99 (Blinding has no `0x02`). |
| 97 / 128 / 129 / 168 | all 7 | cheap card / name / file_ids / share template. Redundant with 178/179/V4. 168 `f3=6185314` is a **template id**. |
| 113 | all 7 | `f1` = request URI. Tautology. |
| 141 / 205 | album rich / artist “Hans Zimmer Popular” / tracks `format=identity` stub | playlist header kinds. **SKIP** on catalogue uris. 205 stays playlist-only. |
| 154 | identical 132 B ×7 | lyrics-share channel denylist. |
| 76 | 83 B on Blinding+Brightside only; empty-200 elsewhere | opaque policy ids. |
| 177 | 26 B thumb on Brightside/Rick/Shape; Blinding **404** | preview-catalog still. 179.f2 already has it. |
| 172 | **502** ×6; Shape 3 B | unusable this batch. |

---

## 3. Credits / publishing / descriptors

| Kind | Who | What |
|---|---|---|
| **186** | all 5 tracks 1.8–5.1 KB; artist/album 404 | **The drawer.** Groups: Artist / Composition & Lyrics / Production & Engineering / Performers + label (`WaterTower` / `Republic` / `BMG` / `EMI` / `Atlantic`). |
| 232 | all 5 tracks 2.5–5.8 KB | 186 ⊆ 232. Extra rows are MusicBrainz (Nolan as Producer on Time, etc.). Gate with 227. |
| 96 | all 5 tracks 403–963 B | core roles only (Main Artist / Composer / Lyricist / Producer). Skip if 186. |
| 131 | **album only** 56 B | one album-artist row. Not a drawer. |
| **183** | tracks 27 B date; album **262 B**; artist 8 B junk | album ©/℗: *“© 2020 Motion Picture Artwork… Warner Bros… Paramount…”* and *“℗ 2014 This compilation WaterTower Music…”*. Track dates: Time 2010-07-09, Blinding 2020-03-20, Rick 1987-11-12, Brightside 2004-06-15, Shape 2017-03-03. |
| 130 | **album only** 16 B | edition `2020-11-12` + original `2014-11-18`. |
| **6** | all 5 tracks; artist/album 404 | mood/genre chips. Time: Soundtrack/Orchestral/Epic. Blinding: Pop/Electropop/Upbeat. *Already.* |
| 19 | same entities, 66–171 B | packed 8-byte signatures of 6. Skip. |

---

## 4. Video / canvas / preview

| Kind | Who paid | What |
|---|---|---|
| **16 ≡ 1** | Brightside 634 B, Shape 535 B; everyone else **404** | same `Canvaz` message, byte-identical. Brightside artist-sourced mp4; Shape licensor-sourced. Fetch **16 only**. |
| **99** | Brightside → `track:1fBPxbaJrGpYqFZPUgEPk5`; Shape → `track:1JzIEU5HWTcZx3EyS2qK1H`; **Blinding Lights 404** | real MV counterpart + file ids. Famous MV ≠ 99 hit. *Already.* |
| 85 | empty-200 ×5 tracks; artist/album 404 | “no original video on this audio gid.” Skip. |
| 216 | Time → `4YsZPn2Wc5dPrwPN7WIZKj`; Shape → `7tTwSfrn8YrW6uhFGTWrcG`; Blinding empty-200 | **not** the same counterpart as 99. Sparse. Skip. |
| **181** | all 5 tracks | preview `.mp3` + URI. Rick relink `4PTG3Z6ehGkBFwjybzWkR8`. Prefer over 116. |
| 116 | 4 tracks; **Rick 404**; artist/album 404 | same hashes as 181, no `.mp3`, no relink. |
| 118 | all 5 tracks labeled **“Music video”** (even Time); album honest “Album” | **card template, not has-video.** |
| 119 | album + artist only | home shortcut card. |
| **209** | Zimmer 7 mapped video track URIs | artist Music Videos shelf. |
| **213** | Zimmer 6 unmapped video track URIs (includes Time’s 216 hop) | disjoint from 209. |
| 101 / 114 / 226 | watch-feed expressions / explorer / seed | TTL 60–10800. `type=video` is not has-video. Skip unless Watch tab. |

Rick has canvas in 179 and `0x02` in 182, but **no** kind 16 and **no** kind 99 — canvas-in-179 ≠ official MV.

---

## 5. Mix / audio

Artist/album **404** on real analysis. Only 219/225/235 answer on those schemes (flags / empty).

| Kind | Class | Notes |
|---|---|---|
| **5** *already* | playback | OGG 96/160/320 + AAC_24; FLAC 16 on 4/5; Time also FLAC 24. Rick **no FLAC on this URI**. |
| **222** *already* | BPM/key | Time 63.46 G major (9B); Blinding 171 C **minor (mode=1, 5A)**; Rick 113 G# maj; Brightside 148 C# maj; Shape 96 F# **minor**. Mode **1 = minor, 2 = major** (proto comment was stale). |
| **237** *already, expand* | waveform | 30–41 KB, hop 20 ms. Do not put on the row bundle. |
| **212** *already, video* | formats + alias | wider format list than 5; Rick describes the relink catalog. Not the lossless source. |
| 28 | DJ | primary IN/OUT + ~20 candidates. Time 0:00.36→4:04 @ 126 (2× 222). Blinding 0:11→3:16 @ 171. |
| 247 | DJ | dense scored transition grid (types 1–4). Sibling of 28. |
| 217 / 218 / 142 | DJ | beatgrid / vocal series / single-band envelope. Skip unless Mix. |
| 219 | flag | `true`/`1.0` on **all 7** including album. Useless. |
| 235 | flag | tracks+artist `1`, album `0`. Cheap eligibility only. |
| 225 | session | empty-200 ×7. Not catalog. |

---

## 6. Artist / album sections

Tracks: **no payload** (404 or 400). This family did **not** 5xx.

| Kind | Who | Decoded |
|---|---|---|
| **152** | artist 163 B | `0WqsmcnmSgs6OW2zTeLMXH`, **`3B61kSKTxlY36cYgzvf3cP` (Interstellar)**, **`2qvA7HmSg1iM6XMiFF76dp` (Inception)**, `3tjIKRAPBy5Qu4z8F5HmBz` |
| **169** | artist 403 B | 10 top track URIs (includes Time). No counts — pair with 185. |
| **165** | artist 495 B | 12 related artist URIs. |
| **203** | artist 1003 B | 25 appears-on album URIs. |
| **175** | artist 2003 B | 50-track cloud. 169 is enough for a top-tracks row. |
| **137 → 204** | artist 2 B / 200 B | gate `f1=1` then five `spotify:concert:…` ids. |
| **184** | artist 3449 B | About bio (stale press copy) + image slots. |
| **255** | artist 543 B | 4 fact strings (since 1982, Disney playlists, 2026 concerts, claimed S4A). |
| 236 | artist 6 B | `f1=1050 f2=6674` counts only. Skip. |
| **206** | artist 174 B | headline: “Pre-save the upcoming album” → Ice Age `19YmKK8Sndae07T8Y2vOjo`. |
| **138 ≡ 102** | artist 560 B | `spotify:prerelease:4FTnhjkU3Kxy0Qfs4ljjuo` *Prehistoric Planet - Ice Age*, 2026-08-20. *138 already.* Skip 102. |
| **151** | album 6 + artist 8 playlist URIs | tracks **400**. *Album already.* Fetch artist the same way. |
| **153** | album 451 B; artist **400** | 11 related (prerelease + Inception + others). |
| 167 | album 452 B | promo card + first-10 title run. |
| 200 | artist 45 B | one creator playlist `4TArpT8rNOwavdZpZKLSAj`. |
| 87 | **album** 215 B; artist 404 | concerts hub chip, no concert ids. |

---

## 7. Leftovers (170 kinds)

**Nothing to fetch.**

- **132 all-404** including **60 STREAM_COUNT** (ttl 3.5 d) and 61 AUDIO_ATTRIBUTES v1 (BPM is 222).
- **12 all-400** — podcast/show/user/venue kinds on music URIs.
- **PAY-200:** only 4 / 21 / 38 — `f1` = request URI (same tautology as 113).
- **Empty-200:** 20, 37, 86, 147, 170, 190, 196.
- **5xx-only (not proven 404):** 75, 115, 117, 120 (501); 52, 63, 78 PLAYABILITY, 198, 211, 215 (502); 24 (503); 194 (504). Re-probe one-kind if a surface needs them.
- 112 MERCH: 204 artist+album, 400 tracks. No body.
- 254 ALBUM_MUSIC_VIDEOS: 404 here (artist video lists are 209/213).

---

## Entity cheat sheet

| Want | Kind | On |
|---|---|---|
| Playcount | 185 `f3` | track |
| Monthly listeners / followers | 185 `f1`/`f4` | artist |
| Credits drawer | 186 | track |
| ©/℗ | 183 `f4` | album |
| Release calendar | 183 `f1` (track) / 130 (album pair) | track / album |
| Canvas | 16 | track (404 = none) |
| Official MV | 99 | track (404 = none, even famous MVs) |
| Preview + relink | 181 | track |
| Offline / video capability | 239 | track |
| BPM / key | 222 | track (*already*) |
| Mood chips | 6 | track (*already*) |
| Popular releases | 152 | artist |
| Top tracks | 169 + 185 | artist |
| Concerts | 137 then 204 | artist |
| Related albums | 153 | album |

Full field dumps: `tmp/xm-probe/findings-{catalogue-identity,reputation-flags,credits-publishing,video-canvas-preview,mix-audio,artist-album-sections,leftovers}.md`.

---

## 8. Versus Wavee (what to add / keep / replace)

> **Status 2026-08-16 — the three "real gaps" below are implemented, on the hydration façade.** They did not land as
> the new clients this section imagined; they landed as **projectors on one shared trait POST**
> (`docs/plans/wavee/hydration-facade-design.md` §2.4, `.claude/skills/wavee/hydration.md`):
>
> | This section's gap | Where it landed |
> |---|---|
> | **1. Kind 185 is implemented and almost unused** | `Backend/Hydration/Projectors/PlayCountProjector.cs` (`TraitSet.PlayCount`). Wired to the album page unconditionally (`TraitSurface.AlbumOpen` — the Plays star is that surface's identity), to the artist chart as its ordering (`ArtistPopular`), and to playlist / Liked **only when the Plays column is actually rendered** (`TraitPolicy` reads the setting; flipping it on fires `TraitSurface.PlaysToggle` for the rows already on screen). `Backend/Metadata/TrackPlayCounts.cs` and `TrackPlayCountHydrator` are deleted — a reader that built its own request plus a hydrator that built its own cap and memo were why the album page asked for 185 twice. |
> | **2. Credits are GraphQL-only and capped** | `SpotifyTrackCreditsService` is now a thin projection over `IExtensionReader` (kind 186 `CREDITS_V2_TRAIT`), with the polymorphic guard at the call site *before the request exists*. NPV survives for merch / related-video / about-artist, as this section recommends. It is a **display-only read**, not a trait — it decorates a drawer and never writes the store. |
> | **3. Album ©/℗ still rides getAlbum** | `Backend/Hydration/Projectors/PublishingProjector.cs` (kind 183, album-only per `TraitApplicability`). It rides the album's **trait pass**, not a fused step-0 catalogue kind — which is what carries the album from `Open` to `Rich`, so the ©/℗ line and the Plays star are there at first paint. `getAlbum` is untouched and demoted to the `Full` rung (below-the-fold, 10-minute cached). |
>
> Also landed from this section: the artist's **extended chart** is now the artist ladder's `Full` rung (spclient
> `artist-top-tracks-extensions` → TrackV4 → the 185 counts the extension rows lack), and 179 / 6 / 222 / 99+182 /
> 178+220 are the four `RowBundle` projectors plus the wire-fidelity one. Still **not** done, deliberately: kind 16
> canvas (no canvas surface), artist 185 on library/home cards, artist 151, projecting 178's fields.

Wavee already has a real XM stack. The gap is not “we don’t talk to this endpoint” — it is **which kinds ride which surface**, and which GraphQL calls are still doing work XM can now do cheaper.

### What Wavee already fetches (keep)

| Kind | Where | Why keep |
|---|---|---|
| 10 / 9 / 8 / 11 / 12 / 205 | `MetadataService` / `ExtendedMetadataSource` | Catalogue. V4 has **no playcount**. |
| 178 / 179 / 220 | Recents `headerTraits:true` | Wire-shape bundle. **179 is also decoded** by `SpotifyTrackAdornmentService` (cover tint). **178/220 are discarded** in `ProjectParsed` (`default: continue`). |
| **185** | `TrackPlayCountSource` → **artist Popular extension tail only** | Reader + etag cache + tests exist. Only `SpotifyArtistPopularTracksService` calls it. |
| 99 + 182 | `SpotifyVideoService` | Ground-truth MV. Probe confirms 99 is sparse (Blinding Lights 404). Do not replace with 118/239/85. |
| 212 | video alias recovery | Relink / associated video gid. Rick’s playable files live on the alternative catalog. |
| 5 | playback + expand | Lossless file ids. 212 is a wider format list, **not** the FLAC source. |
| 6 + 222 | row adornments | Chips + BPM/key. Camelot code already distinguishes minor (`5A`) from major (`9B`); proto comment that “mode 1 is unseen” is stale, UI is fine. |
| 237 + 98 | expand drawer | Waveform + audio associations. Correctly **not** on the row bundle. |
| 138 | `SpotifyPreReleaseService` | Identical to 102 in this probe. Keep 138, skip 102. |
| 151 → 205 | album “Featured on” | Already the XM path. Artist 151 is the same kind, not wired. |
| 15 | user profiles | Wrong entity class on music URIs (400). Fine. |

GraphQL that is **richer than the XM twin** and should stay:

| GraphQL | XM twin | Why GraphQL wins |
|---|---|---|
| `queryArtistOverview` | 185 artist + 152/165/169/184/203 | Overview is stats **plus** named popular releases, related with art, world rank, pinned, header/palette, `preReleaseV2`, top-10 **with** playcounts. 185 artist is 18 B (listeners/verified/followers only). 152/169 are URI lists. |
| `ArtistConcerts` | 137 → 204 | Pathfinder has venues/dates. 204 is five bare `spotify:concert:` ids. |
| `getAlbum` Full envelope | 183 + 9 | OtherVersions, playability, more-by, merch, precision. 183 is only date + ©/℗. |
| Playlist `popcount` | 60 | Different thing. 60 is 404. Keep the spclient popcount service. |

### The real gaps

**1. Kind 185 is implemented and almost unused.**

`ITrackPlayCountSource` is a first-class batch reader. The only production caller is the artist chart’s step three (extension rows beyond the overview’s ~10). Album `ShowPlays` still waits on `getAlbum` (`EnsureAlbumAsync` comment: “required even for a named V4 list because V4 has no play-count field”). Playlist / Liked **do not show plays** (`DetailConfig.ShowPlays` is album-only; `ListColumns` has no Plays lane).

`getTrack` still writes `playcount` for the now-playing upgrade. That is one GraphQL call per track.

**Add:** wire the existing source into album open (and any other track list that paints `PlayCount`). Do **not** write a second 185 client.

**Do not replace** `queryArtistOverview` with artist-185. Optionally **add** artist-185 on Library/home artist cards that never call overview (those surfaces are documented 100% V4 and currently show 0 listeners).

**Do not replace** `getAlbum`. After 185 fills plays on the V4-first path, getAlbum remains the Full upgrade (copyright/label/other versions/playability). 185 just stops the Plays column from being the reason getAlbum is on the critical path.

**2. Credits are GraphQL-only and capped.**

`queryNpvArtist` (`GetNowPlayingInfoAsync`) feeds Now Playing + `TrackCreditsDialog`. Variables include `contributorsLimit: 10`. Kind **186** is the uncapped grouped drawer (16–40 rows here) and needs only the track URI.

**Add 186** as the credits fetch. **Replace** the credits half of NPV. Keep NPV until merch / related-video / about-artist have another home (they do not in XM at useful fidelity — 112 MERCH was empty-204).

232 is 186 + MusicBrainz + `source` names. NPV’s `creditsTrait.sources` maps to the “Source: Republic Records” line. If that line matters, fetch **232 instead of 186**, not both. Default: **186** (groups match the existing UI). Skip 96/131.

**3. Album ©/℗ still rides getAlbum.**

`Album.Copyright` / `Label` come from `SpotifyExportMapper.AlbumFromUnion`. Kind **183** on the album is 262 B with both ©/℗ lines; tracks are a 27 B calendar date.

**Add 183 on album V4-first** so the About tile can paint before Full. **Do not replace** getAlbum. Skip artist 183 (8 B junk date). Prefer 183 `f1` over unix `f2`/`f3` (sentinel `413146860` on Time/Brightside).

**4. Canvas is modelled, not played, and not fetched via XM.**

`TrackCanvas` is filled from NPV `trackUnion.canvas`. Nothing in the app plays it. Kind **16** is the real mp4 (only 2/5 tracks here). Kind **179** already fetched for tint sometimes carries `f4` canvas (Brightside / Rick / Shape) — unused.

**Do not fetch 16** until there is a canvas surface. When there is, fetch 16 (not 1; they are byte-identical). Do not treat 179.f4 as official MV (Rick has 179 canvas and no 99).

### Do not add / do not replace

| Tempting | Why not |
|---|---|
| 60 `STREAM_COUNT` | 404. Dead name. |
| 99 → 239 / 118 / 85 / 226 | 118 labels Time “Music video”. 85 is empty-200. 239’s video slot correlates with 99 but is a restriction map, not a counterpart URI. Wavee already batches 99. |
| 169 vs artist-top-tracks-extensions | 169 is 10 URIs, no counts. Wavee’s SpClient page + 185 is strictly better. |
| 152 / 165 / 203 vs overview facets | URI-only. Overview already lands named cards + art. |
| 184 / 255 vs overview bio | V4 + overview already fill `Artist.Bio`. 184 is stale press copy. |
| 181 vs TrackV4 `alternative` + 212 | Relink is already solved for playback. 181 is a 30s preview URL. No browse-preview UI. |
| 28 / 217 / 218 / 247 / 142 | Mix-lens. No skip-intro / DJ surface. |
| 220 projection | Tautology. Keep sending for wire fidelity; do not decode. |
| Replacing 5 with 212 | Rick 212 describes the *other* catalog. Lossless stays on 5. |

### Recommended fetch set (if implementing later)

**Do now (highest leverage, existing types/seams):**

1. Album (and later playlist/liked) **185** through `ITrackPlayCountSource` — fill `Track.PlayCount`, honor `PlayCount > 0` merge.
2. Track **186** (or 232 if source line) — credits dialog + Now Playing; drop NPV for that field.
3. Album **183** — copyright/date on the V4-first path.

**Do when a surface exists:**

4. Artist **185** on Library/home cards that skip overview.
5. Kind **16** when canvas actually plays.
6. Artist **151** the same way album 151 already works.
7. Project **178** `f1/f2/f4/f5` (already paid on recents) — type + name + parent uris. Artist `f3` bio is optional; V4/overview already have one.

**Never:** 60, 1+16 together, 96+186 together, 102+138 together, 118 as has-video, artist-185 `f1` into `Track.PlayCount`.
