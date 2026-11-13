# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [0.3.9] - unreleased

### Added

- **Mini player.** A small window that stays on top while you work, in any shape you drag it to: a strip, a card, a square
  or a tall 9:16 column; on release it settles on the nearest shape and snaps into a screen corner. Pick what fills it:
  the cover, lyrics (a karaoke ticker in the strip), the Canvas, the visualizer or a player style (vinyl, CD, cassette,
  reel). It looks like your theme: WinUI's acrylic card, Zune's big type and pink, Classic's album-coloured wash with its
  rippling seek line. Its ⋯ button opens one panel in your theme's look that stays open while you choose (shape, face,
  keep on top, "Colour from album art", back to Wavee): each pick shows at once in the player beside it. Its device
  button opens the player bar's own device picker. Scroll over it to change the volume. Open it from the new button in the player bar (or its ⋯ menu when the bar is narrow), with
  Ctrl+Alt+M, or from the taskbar's right-click menu; the full window hides while it is open and comes back from the art,
  the back button or any way you bring Wavee back, and the music never stops. Opening it and going back are one calm motion:
  the full window fades and settles toward the player bar's art while the mini player grows out of that side, and the reverse
  on the way back. The mini player keeps every frame while you drag or it settles, and its lyrics run at your display's full
  rate (#197).

- **Full screen opens on the music video, in every theme.** With a video on, the app's full screen opens on the picture,
  edge to edge, instead of on the cover and lyrics: Video is its first tab, Lyrics, Visualizer, Up next and Artist stay one
  click away. The top bar, the title and the controls fade after 3 seconds without the pointer (the cursor too) and come
  back on any move or key; they also go when the mouse leaves the window. Space, the arrows and the volume keys work as
  before; F11, "Video in fullscreen" and the title bar's full-screen button all open this one full screen. "Switch to
  audio" in the top bar turns the video off. Each theme draws it its own way: Classic with dark pills on the picture,
  WinUI with one floating command bar in the style of the Windows 11 Media Player, Zune with its flat plate, pivots and
  big thin type.

- **Theatre mode in the video full screen.** "Theatre mode" slides a panel in beside the music video: the picture moves
  to the left at its own shape with the title, "Playing from", the artist (photo, monthly listeners, Follow), the song's
  facts and the start of the artist's story under it, and Up next or the lyrics beside it; nothing ever covers the
  picture. "Fill the screen" takes it back, and full screen remembers the choice. Esc in the full picture steps to
  Theatre; Esc in Theatre leaves full screen. The controls come and go with the mouse in Theatre too. Opening and leaving
  full screen glides the picture between the docked card (or the mini player) and the screen.

- **An Overview tab for songs.** When the playing song has no music video, the first tab reads Overview and shows the
  same full screen with the song's own colour in the picture's place: a soft field from the cover's palette, the cover
  and the synced lyrics, in the full picture and in Theatre, with the same controls and the same Esc. When a song with a
  video comes up, the tab turns into Video in place and the picture comes back by itself.

- **Up next shows which songs have a music video.** In the full screen's Up next tab, the Theatre list and the shelf of
  cards over the picture, a song with a music video carries a small film icon. While videos are on, those songs show
  the video's still instead of the cover, so you can see what will play as video next; the rows stay the same height.

- **"Up next in 5" when a music video ends in full screen.** The video's last frame stays, dimmed, and a card counts down
  five seconds to the next track: its thumbnail, title, artist and where it plays from, with Play now and Cancel; the rest
  of the queue waits below it. Cancel leaves the video stopped at its end, and Play starts the next track whenever you
  like; Next, Space and the media keys play it at once. Only in the video full screen and only after a video: songs, a
  video docked or in the mini player, skips and the end of the queue carry on at once as before. Turn it off in Settings,
  Playback, Video ("Count down before the next video").

- **Hide the queue in full screen.** The Up next column beside the lyrics has a small tab on its left edge: it slides the
  column off the right edge of the screen and back, the lyrics stay where they are, and full screen remembers whether you
  left it open.

- **AI word timing for Japanese lyrics.** Japanese joins English, Spanish, Dutch and Korean in the AI lyrics languages
  (add it under Settings, AI lyrics, Languages): a lyric written mostly in kana and kanji is timed character by character (a long vowel mark or
  a small kana goes with the sound before it), and Chinese lyrics, which have no kana, are never sent to it. English
  words inside a Japanese line are placed between their timed neighbours; a line that is mostly English keeps its line
  highlight. Every song timed before is timed once more, since the language decision changed.
- **What's New can show a feature in motion.** A highlight can carry a short looping clip of the real app instead of a
  still; this release's video full screen highlight plays one, at 60 fps, only while it is on screen.

### Changed

- **Moving a music video between the right panel and the mini player is one smooth motion (every theme).** The picture
  glides from the panel to the mini player's corner, and back, instead of jumping there; the panel slides away (or in)
  with its contents as they were, and the page widens or narrows once, its titles taking their new line breaks on the
  first frame instead of rewrapping step by step as the panel moves. The page's foot makes room for the mini player as
  the picture arrives, instead of cutting off at once, and the "Recently played" tiles change their columns in one step
  (a narrowing page showed twice its rows for a frame).
- **Home keeps still while the right panel opens or closes (every theme).** When the panel's slide (a music video moving
  to the mini player and back, or a window resize) takes Home to another layout, WinUI's daylist card changes shape as one
  motion (the cover, the copy and the clock column ease to their new places, the title cross-fades between its sizes)
  instead of jumping, and its "Following" button fades out of one place before it appears in the other instead of
  showing twice. Zune's "new" releases cross-fade between their row and the place beside the history tiles instead of
  sliding across them (they never leave the page empty mid-slide), and Classic's daylist buttons glide to their new place. With the right panel open on Now playing, Queue,
  Lyrics or Friends, the docked video card fades out (or in) while the panel's contents ease up (or down) into its place,
  and the cover takes over from the card with a cross-fade, instead of the panel jumping by the card's height. The play bar
  and the panel no longer redraw when only the page's layout changes.
- **The controls over a video go the moment the pointer leaves the window, as on YouTube.** In full screen (Cinema and
  Theatre), over the docked, floating and pop-out video and in the mini player, moving the pointer out of the window (off
  any edge, onto another screen or the taskbar, under a window that comes up over Wavee) or switching away with Alt+Tab
  or the Windows key starts their usual short fade at once, with no wait. Moving onto one of Wavee's own menus is not
  leaving, a seek or volume drag keeps them until you let go, and while paused they stay, as on YouTube.
- **The Canvas dissolves into the now-playing panel itself (Classic).** The picture fades out on a smooth curve and the
  panel below it is exactly the panel the Cover style shows, in your window's own colour, instead of a flat dark ground.
  The drifting waves at its foot and the coloured glow under the small cover are gone.
- **The docked and floating video's controls look like the full screen's (Classic).** A seek bar along the bottom of the
  picture with one row under it: play, previous, next and mute, the time, and the volume and "..." buttons. As the picture
  gets narrower the time shows only how far you are, then the volume and then previous and next move into "..." as a row
  of buttons at its top; play and "..." always stay.
- **The Classic player bar has one button for the player views.** Now playing is the button; the mini player and full
  screen sit behind its chevron, set apart from the queue and devices by a thin rule. At the widest bar the "..." menu
  has nothing left to hold and is gone.
- **Icons that say what they do.** The queue is lines with a note, now playing a window with a play mark in its side
  pane, the mini player a window folding into its corner. The video menu shows a docked pane, picture in picture, a
  pop-out window and a crossed-out camera, and its full-screen row reads "Video in fullscreen"; the app's own full
  screen keeps the regular arrows, and the video's is a framed screen.
- **The video full screen's controls sit on the picture, as on YouTube.** A thin seek bar runs along the bottom of the
  picture with one row of controls under it: play, previous, next, mute (with the volume) and the time on the left; like,
  shuffle, repeat, the device picker, "...", the video's settings (aspect ratio, quality, playback speed, where the video
  plays), the full picture or Theatre, and leave full screen on the right. Narrow windows fold shuffle, repeat and the
  device picker into "..." first.
- **Theatre takes the song's colours.** Around the picture the page takes the playing song's colours instead of plain
  black, and the Up next list beside it is an open list on that page rather than a framed card.
- **Up next cards over the full picture lose their frames.** A music video's card is its picture, an audio-only track's
  card is its square cover, with the length on the art.
- **Full screen has a button in the Classic title bar.** It sits right beside minimise, after the bell, friends and pin; the player bar's menus keep the row for narrow windows.
- **Music videos start faster and at the quality you picked.** With a fixed video quality, a video opens at that quality
  instead of starting lower and switching up a moment later: in testing the first frame came after 0.7 s instead of 2.2 s.
- **Menus open faster.** The first menu of a session appears in about 33 ms instead of 72 ms and later ones in about
  21 ms instead of 30 ms.
- **AI word timing on Japanese songs lands closer, and on more songs.** A Japanese line can no longer slide seconds into
  the instrumental break after it or the long intro before it: the aligner knows about how long a line's characters
  take to sing, and one short character timed at zero length no longer costs a line its word timing. In every language,
  a song whose provider line times sit a steady second or two off the music is no longer turned down for it, and a line
  whose words start far later than the line itself keeps its line highlight instead of wiping in the wrong place.
  Measured on 34 Japanese songs against human timing: 9 in 10 characters within 0.59 s instead of 0.74 s, lines more
  than a second off halved, 23 of 27 songs timed instead of 20. Japanese songs timed before are timed once more.
- **AI word timing holds each word as long as it is sung.** A word used to end where its sound began, so short words
  (a fifth of all Korean words) lit for a blink and the highlight then stood still; a word now lasts until the voice goes
  quiet or the next word starts, and a line's last word ends by the time the next line starts, so its last syllable is
  never cut off by the hand-off. A result full of words that short is no longer shown. Every song timed before is timed
  once more the next time it plays.
- **Going full screen with a video is one calm motion.** The picture starts gliding from the panel at once, the page stays
  under the full screen's background as it fades in, and the title, the controls and the top bar follow one after the
  other once the picture has nearly arrived. Switching between the full picture and Theatre moves one title with the
  picture instead of fading out one and fading in another, and leaving fades the controls first, then the picture flies
  back while the page shows through. In every theme.

### Fixed

- **Lyrics found for songs whose title is in another script.** A Korean, Japanese or Chinese song listed on Spotify
  under its English title ("I loved you too much to just laugh it off") found no lyrics, though QQ Music, NetEase and
  KuGou had them under the Korean title: a title in another alphabet now counts as unknown rather than as a mismatch, so
  the same artist at the same length (within 1.4 s) is accepted; the sources' own translated titles and "원제 (English)"
  titles are read too. Plain-text lyrics from lrclib were also being thrown away as fake timing; they now show (and the
  on-device AI can time them). Songs that found nothing before are searched again instead of answering from the
  remembered "no lyrics".
- **The mini player's lyrics fade out at the edges, and the AI note is one short line.** On a song timed by the
  on-device AI, the lines above the note were cut off flat and the note itself was faded and took two lines; the lines
  now dissolve at the top and just above the note, and the note reads "AI word timing" on one line (the side pane and
  full screen keep the whole sentence).
- **Music videos showed at the wrong size for a moment.** Going full screen, changing quality or resizing the window made
  the picture too small or too large for a few frames, and a Canvas could show a black column: Windows now stretches the
  picture to its box on every frame, whatever size the video arrives in.
- **A music video stayed frozen on one frame.** After a seek, or when a video came back into full screen, the picture
  could stand still while the song played on, until you pressed pause and play. It now keeps playing.
- **The app froze for a moment during music videos.** Wavee's own drawing waited behind the video's work on the graphics
  chip for 30 ms to half a second at a time, most often at a video switch; it now goes first. Switching tracks also no
  longer spends a frame looking up the next song's lyrics.
- **Entering and leaving full screen showed the old picture for a moment** (small with a border on the way in, cropped
  on the way out): the window now changes size together with the first picture drawn at the new size. Leaving full
  screen also lays the page out once instead of twice.
- **Switching between music videos of different shapes no longer squeezes the old picture.** The last frame of the video you
  leave now stays exactly where and as large as it was until the next video's first frame is actually on screen, and the
  docked video's card keeps the old picture's shape until then, easing to the new one with the new picture. A video
  opened from nothing still reserves its shape at once, and its cover stays up until the picture is there.
- **Menus over a playing video were see-through.** A menu over a video is frosted, and closing one no longer shows the
  video through it for a moment. Long menus, track menus included, scroll inside a short window instead of running off
  its edge; Left closes a submenu that opened on hover.
- **The Home daylist card and the grids under it stepped when the right panel opened or closed.** The card follows the
  panel in one motion, its title picks its size once, and shelves and grids change their number of columns once, at the
  start of the slide. Releasing the panel's divider no longer makes the panel's content jump.
- **Settings descriptions ran out of their rows (Classic and WinUI)** and overlapped the row below; rows now grow to hold
  them, and Classic's descriptions next to a switch get more room at narrow widths. In Zune's narrow settings the
  sentence under each heading was hidden above the list; it shows again. A notice no longer pushes Zune's settings
  rows past the edge.
- **What's New tiles opened their description only on hover;** keyboard focus opens it too.
- **The video full screen's controls blinked at the bottom edge of the picture in Theatre;** moving just below the picture
  no longer counts as leaving it, and clicking the Theatre toggle no longer hides them.
- **The video full screen (Classic): Theatre's about block was missing on big screens and "Read more" sat out of line;**
  Theatre now always leaves room for the title and the about block under the picture, and "Read more" lines up with the
  text above it. Theatre's Up next builds only the rows on screen, so a long queue opens and moves as fast as a short one.
  Skipping from a video to a song no longer hitches, and the saved heart in the full screen's controls no longer jumps
  when the window crosses 1920 pixels.
- **The app kept working under the full screen.** With the full screen up, the window under it no longer ticks its seek
  bar and times or re-draws the player bar, "Next up", the docked video's details, its equalizers or its lyrics on every
  track change, and the full screen's hidden controls stop ticking too; closing the full screen shows everything current
  at once.
- **Switching the video full screen between the full picture and Theatre stuttered.** The picture's move no longer repaints
  the screen under it on every frame, and the Theatre panel and the transport no longer re-blur what is behind them while
  the picture moves beside them (they look the same). While the video fills the screen, the visualizer's clock stops
  instead of running every frame for nothing on screen.
- **The full screen's controls stayed up although the mouse was idle**, also with the mouse on another monitor. A video
  that is still loading no longer keeps them up (a pause, the end and an error still do); resting the mouse on the empty
  part of the top bar or the transport card lets them fade like resting on the picture (only the buttons, tabs and seek bar
  hold them); moving the mouse over the picture or over the lyrics brings them back; and the mouse leaving the window lets
  them go. Each show and hide is now written to the log with what caused it and what held it.
- **The full screen's controls lingered after a menu closed outside the window.** Closing a menu (or ending a seek) with
  the mouse already outside the window now lets them go after the short leave delay, not the whole idle time.
- **The seek bar came back from hidden catching up.** When the full screen closes, the player bar's seek bar shows where
  the song is at once instead of sliding there.
- **Skipping in the video full screen with Up next open hitched.** Every track change rebuilt every row of the list; now
  only the rows that changed update.
- **Esc and F11 left full screen open.** Pressing Esc (or F11) in full screen did nothing, with or without a video: the
  keys went to a part of the screen that did not handle them. They work again; Space and the arrows keep playing, seeking
  and setting the volume.
- **"Synced lyrics aren't available while a video is playing" was cut off** beside the video in Theatre: it wraps now.
- **A Canvas that stayed a still picture.** When the looping video failed to open (its cached loop file had been
  cleaned up, for one), the still stayed over the playing music for the whole track and nothing was logged. The loop is
  rebuilt, a failed open is logged and tried once more, and the loop cache now keeps the canvases you played last.
- **Acrylic over the Canvas in full screen.** The player bar, the Up next column and the view switcher at the top were flat
  dark slabs while a Canvas played behind them. They are frosted glass again: the video shows through them blurred and
  tinted, as the album art does.
- **Full screen follows your player style.** The Canvas plays behind the lyrics only when the player style is Canvas
  (Settings > Appearance > Now playing, or the style chip in Now playing); every other style keeps the blurred album art
  there. Before, full screen played the Canvas whatever the style, so there was no way to turn it off there alone.
- **Lyrics beside the queue in full screen read in phrases.** On a 1920-wide screen the lyrics next to the Up next column
  fell to one word per line; the gap to the column now gives way first, so lines wrap at a readable width.
- **Skipping songs in full screen is smooth.** The cover cross-fades to the next song; the title, the artists and the chips
  fade out first and the next song's fade in after them, drifting in from the side you skipped to; the lyrics fade out
  and the next ones fade in; the Canvas fades through its still into the next picture instead of cutting; and a burst of
  presses settles on the last song without stutter.
- **A scrolling song title kept the wrong edge fade.** The first letters of a title scrolling in the player bar could
  show cut off at full brightness instead of fading in.
- **AI word timing: lines that flashed past.** A line followed by one the model could not time was squeezed to zero
  length.
- **AI word timing only in a language it knows.** A Japanese, Greek, Russian or French song no longer gets English word
  timing made up for it: the language comes from the lyric's script, then the provider's tag, then a clear majority of
  its words, and a song that matches no installed model keeps its line-by-line sync. A line the model could not hear
  stays the provider's line instead of getting invented times, a line keeps the provider's start, and a result with too
  few well-timed lines is thrown away instead of shown or saved. Songs timed before this update are timed again the next
  time they play.
- **A failed language download in AI lyrics.** When adding a language fails, Settings now says which language and why in
  plain words (not on the download server yet, offline, the connection dropped, a file failed its check, not enough free
  space), with Details for the raw reason and Try again for that language; the languages already installed keep timing
  songs. A first setup that stops on a language's file names that language. Every theme lists the languages in one order.
- **The word highlight ran ahead of the words.** The lit edge of a word-synced line follows each word's place in the line
  as it is drawn, so it no longer runs into the next word after short words (Korean especially), in the full screen's
  caption or in Verse; it waits at a pause instead of lighting the next word early.
- **A line lit before its time after a break.** During an instrumental break the next line no longer turns white seconds
  before it is sung when it has no word timing of its own.
- **The lyrics under the full screen's cover were hard to read on light covers;** the soft dark ground behind them now
  adjusts to the cover and stays inside the caption, never over the cover.
- **Verse in Zune hid its current line under the now-playing card;** it stands beside or above the card now.
- **Previous did nothing on the first song after the queue ran out.** With the first song of the list selected and
  nothing playing (the queue ended with repeat off, or playback stopped), Previous now plays that song from its start.
- **Previous, Next and seeking stayed disabled after taking over from another device.** The limits a phone's playback set
  (a one-song list, say) now end once Wavee plays itself, instead of lasting the whole session.
- **The full screen's transport stepped when the window crossed 1920 wide (Classic).** Its controls now glide to their
  new places, the saved heart among them.
- **Zune's switches that can't be changed showed only a grey dot.** A disabled switch (AI lyrics on a PC without an NPU,
  for one) now draws its track under the dimmed knob, on and off.

## [0.3.8] - 2026-11-08

### Added

- **Album credits in Zune.** An album page names who wrote, produced and played on the record, gathered from its
  songs' credits, and "all credits" opens the full list grouped by role. The song credits sheet, which showed only its
  title in Zune and WinUI, lists everyone again.
- **Storage & cache shows the AI models.** The models that time lyrics word by word on this PC (around a gigabyte
  per language once compiled for the NPU) now count in the "On this PC" total, with their own colour in the bar
  and a row that says how many languages are installed, opens their folder and removes them after asking. Removing
  them stops a lyrics job in progress safely, and the Lyrics settings can download them again. When none are
  downloaded the row shows 0 B and says where to get them.
- **Storage & cache counts the rest of Wavee's data.** A new "Other app data" row covers the lyrics cache, crash
  reports, What's new, podcast transcripts and modules, so the total matches what Wavee keeps on this PC.
- **Spotify Canvas.** When the playing song has a Canvas (the short looping visual artists add to their tracks), it
  plays in full screen behind the lyrics and the queue, and in the Canvas player style, where it fills the Now playing
  panel edge to edge with the song's title over it (in Classic it ends in a slow wave in the album art's colours, over
  the plain panel). In light mode the text over the Canvas stays light on a soft shade and the video fades into the
  light panel below it, and the panel's rounded corner cuts the video. It grows with the panel's width. It is silent, loops without a seam, and pauses while you cannot see it.
  Wavee asks for it only for the song that is playing and the next two in the queue, keeps the videos on disk, and
  shows them offline for up to 30 days. A song without a Canvas looks the same as before.
- **Resize the docked music video.** Drag the handle under the video in the side panel to make it taller or
  shorter; double-click puts it back, the arrow keys work too, and Wavee remembers the height.
- **Show Canvas** setting under Settings > Playback > Video, on by default. Turned off, no Canvas is fetched or shown
  anywhere.
- **Music videos with Spotify Connect.** Wavee now tells your other devices which songs have a music video, as the
  official app does, so Spotify on your phone or computer shows its video switch while Wavee plays. Switching between
  the video and the song from another device switches Wavee too, and a music video started from another device opens
  as a video in Wavee.
- **Switch another device's video.** While a phone, computer or speaker plays, Wavee's video button switches that
  device between the video and the song. Instead of a "Loading…" that never ended, the video panel says which device is
  playing, with a Play here button, and "Up next" shows the songs in that device's queue.
- **Playlists that play their videos.** Spotify's "Video" choice on a playlist now plays the music videos. Playlists
  without it, such as Today's Top Videos, have a Video option in their "…" menu, and Wavee remembers it for each
  playlist. A playlist set to video shows how long each video is.
- **Pin by dragging in Zune.** Drag a playlist, album or artist onto "pinned" to pin it, and right-click a pin to open
  it, unpin it or move it. While you drag a playlist, the preview shows the playlist itself.
- **Scroll over the volume slider to change the volume.** Each notch of the mouse wheel moves it 5%, the same as the
  arrow keys, on the player bar, the volume popups and the full screen player. (!220)

### Changed

- **WinUI theme: pages move like Windows apps do.** Changing pages in the WinUI theme now uses the navigation
  transitions of WinUI itself instead of fading the whole page as one block. A page opened from the navigation pane
  rises into place, a card or row opens its album, artist or playlist with a short zoom, and Back plays the reverse.
  The library's pages and the artist page's Overview, Albums, Singles & EPs, Appears on and About tabs slide in from the
  side of the tab you picked. The parts of the new page arrive one after another, the header first, then the sections,
  rows and cards that are on screen, and the whole page is in place within about half a second. A page that is still
  loading enters with its placeholders, and its content fades in where they stood. With Windows animation effects
  turned off, pages just fade. Zune and Classic move exactly as before.
- **WinUI podcast pages look like WinUI.** In the WinUI theme the show page's header reads as one line ("publisher ·
  rating · episodes · weekly · since"), the played counts sit under it, and Follow shows its check. At Compact the
  Resume button fills the row beside a square Following button. The show's About, topics and (on large windows) its
  details stand beside the episodes, or under them on small windows. The episode page has WinUI buttons for Mark as
  played and Save, a WinUI tab bar, and About and Chapters beside the Transcript. The podcasts library opens on a
  "Library" heading with tabs for each kind, and the selected show's panel uses WinUI's buttons. The Continue and
  "New since you were here" sections stay at the top of the episodes.
- **More Popular songs beside a tall side column (WinUI).** On a WinUI artist page with two columns, Popular shows as
  many songs as fit next to the latest release, discography and artist pick (up to ten), so the space under it is no
  longer empty. Pages of the song list follow that size. Artists whose columns already line up keep five.
- **The artist's About card in WinUI.** The card no longer looks clickable without doing anything: it shows the
  monthly listeners and world rank, and "Read more" opens the full biography with the artist's photos in place.
- **Artist photos keep the face in view.** The About card's photo is cropped around the faces it finds, and a tall
  portrait without a detected face keeps its top, instead of showing the middle of the picture.
- **Release cards open the album; the arrow shows its songs.** On an artist's Singles & EPs and other release grids,
  clicking a card opens the album like any other card. The arrow after "1 track" is now a proper button with hover and
  focus states: it opens the songs inline under the row, and closes them again. The panel opens and closes smoothly
  instead of drawing rows over each other for a moment.
- **Fewer hitches while scrolling and changing pages.** Wavee now reuses graphics memory instead of handing it back to
  the graphics driver in the middle of a frame, so the slowest frames while moving between pages take about half as
  long to reach the graphics card. A page that slides in is drawn from one picture instead of being redrawn on every
  frame. To do this Wavee keeps up to about 30 MB more graphics memory while it runs.
- **Crash reports explain sudden closes.** When the .NET runtime closes Wavee itself, the crash report now keeps the
  runtime's own reason and how long Wavee had been unresponsive, under your existing diagnostics settings.
- **Crash reports say what led up to the crash.** A report now lists the app's last events (the kind of page, a track
  starting or skipping, the window being minimized or restored, a graphics reset or GPU switch, memory clean-ups), which
  native component the crashing thread was in, the graphics driver version and any overlay loaded into Wavee. Nothing
  personal is in the list, and sending a report stays your choice.
- **A crash in native code is reported as itself.** Wavee's crash hook no longer turns some native crashes into a
  "fail fast" with no details; they now arrive with their real error and a memory snapshot.
- **No crash loop on playback.** If starting a song crashes Wavee twice within a few launches, Wavee shows those songs
  as unavailable for that version instead of crashing on every start. An update, or a week without the crash, brings
  them back. (#217)
- **The video mark in every song list.** A song with a music video shows the video mark in every list. The playlist,
  album and Liked Songs tables of the WinUI theme were missing it.
- **"Watch the official video" plays it.** The card on an album page now plays the music video.
- **Spotify links open in Wavee.** A link to Spotify in Wavee's notifications, Windows notifications, playlist
  descriptions, artist biographies and these release notes opens the page in Wavee instead of the browser, and a link
  to a song plays it.
- **The first video opens sooner.** Wavee gets video playback ready as soon as a song that has a music video plays, so
  the first video opens up to about a second sooner. Listening to songs without a video does none of this work.
- **Exact seeks in a video are about 30 % faster.**
- **A smoother queue.** The queue draws only the songs you can see, so pressing Play does about half the work and
  starting a playlist no longer hitches. Dragging a song in the queue slides the others into place.
- **Menus stay open while you use them.** Clicking inside a flyout no longer closes it. Submenus open after a short
  pause and stay open while you move the pointer toward them, also several levels deep, and the arrow keys move
  through them.
- **The WinUI artist page stays put when the navigation pane opens or closes.** Its right-hand column used to fly
  across the page; it now reflows in place, and other pages resize the same way while you drag the window's edge.
- **One player style for Now playing, for everyone.** The Cover/Player switch, the style gear and its Finish, Size,
  Speed, Sleeve and Background options are gone. Now playing has a single style: Cover (the default), Canvas,
  Visualizer, Vinyl, CD, Cassette, Reel-to-reel, Pocket, Hi-fi, Skin or Metro. The chip beside the heading opens a
  picker right in the art: step through the styles with the arrows, the dots or a swipe, see each one live, then
  Done or Cancel. Only Vinyl and Pocket have finishes, and the record spins at 45 for singles and EPs, 33⅓ otherwise.
  Your old choice carries over, and the same setting is in Settings for every theme, no longer only in Developer mode.
- **Menus and drop-down lists mark your choice in each theme's style.** In Zune the chosen item has an accent dot and
  accent-coloured words, in Classic an accent dot on a lightly highlighted row, and in WinUI the check and the pill of
  Windows 11's own controls.
- **WinUI drop-down lists react to a press.** The selected item's pill squishes when you press it, as in Windows 11.
- **Save from the "…" menu in Zune.** An album's or playlist's "…" menu now has "save to your library" (or "remove
  from your library"), the same as the save button in the band. Your own playlists don't show it.
- **The video menu says what is set.** Its rows show the current aspect, quality and speed, check marks show what is
  playing, and right-clicking the video opens the same menu.

### Fixed

- **The CD player style spins calmly.** The disc turned at a real CD's 200 to 500 rpm, about 44 degrees from one frame to
  the next, so its printed cover strobed and blurred instead of turning. It now turns at a steady, readable speed that still
  slows a little as the disc plays, spins up and coasts down like a motor, stops when you pause and holds still with
  Animation effects off. The disc now sits half out of its jewel case with the cover printed on it. The rainbow sheen shows
  only at its rim and centre, around a clear ring and the player's clamp.
- **The Vinyl player style plays like a turntable.** The tonearm swung the wrong way, so the stylus never reached the
  record. Now it drops onto the first groove when you press play, moves inward as the song plays, lifts when you pause,
  crosses to where you seek and goes back to its rest. When you skip, it lifts from where it is instead of first jumping
  out to the edge of the record. When the queue ends, the arm rides the run-out groove and returns,
  because the deck no longer mistakes the stopped player's position for a jump back to the start. The record has a paper
  label, smooth gaps between songs and a sheen that stays with the light as it turns. It sits on a graphite turntable
  with a machined platter, a straight arm with a counterweight and headshell, an arm rest and a power lamp that is lit
  while the motor runs.
- **The artist photo shows again behind its name.** In the Classic theme the light or dark veil under an artist's name,
  bio and buttons covered most of the photo since 0.3.6, so the picture was barely visible, worst in light mode. The
  veil is back to 0.2's: strong at the left edge and fading out behind the copy, so the photo shows across the hero.
  It now reads each photo and stays stronger only as far as that photo needs for the text to stay readable (a bright
  photo in dark mode, a dark one in light mode).
- **Pages no longer guess whether they are stuck.** Artist, album, playlist and search pages know whether their data
  is loading, there, missing or failed, show exactly that, and recover by themselves when the connection comes back,
  instead of waiting on a 10-second timer.
- **The daylist's picture fills its whole card on a narrow Home.** In the Classic theme at the narrowest size, the
  daylist card's picture stopped at a hard line two thirds of the way down, with the title, tags, buttons and countdown
  on a flat plate under it. The picture is now the whole card's background and fades smoothly behind the text.
- **Repeat All repeats the playlist, and only the playlist.** With Repeat All on, Wavee no longer plays
  suggested songs from outside the playlist or album, with or without shuffle: after the last song it starts again
  from the first, and songs you queued or that Autoplay picked do not come round again. (#215)
- **Next moves on under Repeat One.** Repeat One still replays a song when it ends, but pressing Next now plays the
  next song and switches to Repeat All, as Spotify does. (#215)
- **Autoplay only continues when your music ends.** With the Autoplay setting off (Settings > Playback), playback
  stops at the end of the playlist, even if suggestions were already in the queue; with it on, suggestions only play
  after the last song and never while Repeat is on. The queue only shows the suggestions when they will play. (#215)
- **Shuffle covers the whole playlist.** Turning shuffle on mixes in the songs before the current one too, and never
  mixes suggestions in among the playlist's own songs. Turning it off puts the playlist back in order and carries on
  from the song that is playing. (#215)
- **Previous on the first song** restarts it, and with Repeat All on goes to the last song; it used to do nothing.
  Repeat All on a long playlist started part-way now also comes back to its first song. (#215)
- **Repeat on another device.** Changing repeat while another device plays now sets that device to exactly the mode
  you chose; going from Repeat One to off or to Repeat All used to leave it repeating the song.
- **WinUI library sort and view menus look like Windows.** In the WinUI theme the library's sort is a button saying the
  order in force ("Recently added") whose menu lists the orders in sentence case under "Sort by", with Ascending and
  Descending below; the view menu lists the four views and the grid sizes the same way. The lowercase word rails,
  "more" and the bold "alphabetical" button from the Zune look are gone from WinUI; Classic and Zune are unchanged.
- **WinUI podcasts and shows follow the WinUI design.** A show's episodes are filtered with a WinUI pivot ("All · 14",
  "Unplayed · 10"), sorted with a "Newest" menu, and the headings, the Continue card and the episode rows use WinUI's
  type and the accent color instead of the show cover's color. Following a show reads "Following" next to Resume.
- **The player bar stays still when you skip.** Skipping a song no longer moves the play controls, the seek bar, the
  heart or the buttons on the right. In the WinUI theme the play controls sit in the middle of the bar, the song title
  has a fixed space, and the video button stays in its place, greyed out for a song without a video. The controls,
  the lyrics button and the times also stay as they are while the next song loads, instead of greying out or
  showing 0:00 for a moment.
- **No crash after a long time minimized.** Restoring Wavee after it sat minimized for hours on a big playlist, artist
  or Liked Songs page could crash it on the first frame.
- **No crash when a song starts as the play log saves.** The play log and the visit history now take their snapshot on
  the UI thread, so a song starting at the moment the save fired can no longer crash Wavee.
- **Zune titles are never cut at the left.** A large playlist or album title that hangs into the margin draws its
  first letter whole, and its focus ring and edit underline sit where they belong.
- **Pages settle without jumping.** A show's header grows smoothly as its details load instead of dropping the episode
  list in one step, and the Zune band keeps its place until the next page has landed.
- **Artist pages no longer load forever.** An artist page could stay on its grey placeholders for good, most often
  when you opened the artist of the song that was playing: its top tracks arrived last and the page never noticed.
  It now shows as soon as everything has arrived, and an artist whose details stopped coming shows "Something went
  wrong" with Retry instead of loading forever.
- **No page waits forever.** An artist, album, playlist, show or episode page now knows the moment its details stop
  coming (an answer without them, a failed request, no connection) and shows "Something went wrong" with Retry right
  away, instead of loading for good or for a fixed wait. While Wavee cannot reach Spotify a page says it is offline, and
  it fills in by itself once the connection is back. The log names the page and what it was waiting for
  (`page.failed`).
- **Status bars stack when they do not fit.** A title and message that would not fit on one line now sit one above the
  other instead of squeezing side by side and wrapping, at any width.
- **No empty band under an artist's concerts, merch or gallery.** On the artist page, "Upcoming concerts" left about
  170 px of empty space under its date cards, and the gallery about 70, because their rows were sized for album
  covers. Each row is now as tall as its own cards, in every theme. Merch cards all share one height, so a two-line
  name no longer runs past the row.
- **The time keeps running in video mode.** The time and the total no longer stand still and then jump while a video
  plays.
- **Turning video off resumes the song where the video was.**
- **Dragging the seek bar no longer freezes the video.**
- **No "protected content" error on the first video.** A rare failure stopped the first music video after starting
  Wavee.
- **No wait when turning video on.** Turning video on could stall for up to 10 seconds.
- **Video plays at Wavee's volume from the start.**
- **Full screen opens on the monitor the video window is on.**
- **Video stays on when you skip.** Going to the next song keeps the video on when that song has one.
- **Spotify's mixes can be saved again.** Mixes Spotify makes for you, such as "Comforting Mix", were treated as your
  own playlists, so there was no way to save them. In every theme.
- **One selected item in the sidebar.** In the Classic and WinUI themes the sidebar could show two items selected at
  once.
- **The "Playing on …" line fits in Classic.** A long device name now ends in "…" instead of being cut off.
- **No restart on Snapdragon X PCs.** Blurring in the background could crash the graphics driver and restart Windows
  on Snapdragon X devices.
- **Playback speed works for video podcasts.** Changing the speed of a video podcast used to leave it playing at normal
  speed. For a music video the speed is greyed out and says "Podcasts only".
- **A video's quality no longer carries over.** A quality you picked for one video no longer holds the next video at
  the wrong quality.
- **Auto quality switches promptly.** Turning a quality choice back to Auto used to keep the lower quality for over a
  minute; the best quality for your connection now returns within seconds.
- **Always on top stays off when you turn it off.** A video window that went back into Wavee while on top came out on
  top again even after you turned Always on top off.

### Security

- **Built on .NET 11.** Wavee carries its own .NET runtime, and this release moves it to .NET 11 (release candidate 3),
  which also fixes an out-of-bounds write in the Brotli decoder (CVE-2026-111497).

## [0.3.7] - 2026-10-26

### Changed

- **WinUI and Zune are there for everyone.** They were only offered with Developer mode on, so most installs, the
  Microsoft Store one included, never showed them. Settings > Appearance > Theme now lists all three.
- **WinUI uses the new What's new page,** the same one as Wavee Classic.
- **What's new shows more at a glance.** The biggest highlight shows its description without hovering on every window
  but the narrowest, and the changes filter says how many changes each section has: All (52), Added (5), and so on.
- **The artist header shows more of the photograph.** The panel behind the name and buttons now fades out toward the
  top of the header, where there is no text, instead of covering it in a flat colour; the text stays as readable as
  before.

### Fixed

- What's new: hovering a highlight no longer makes its title jump; the frosted band grows and shrinks smoothly with
  its description.
- What's new: with Energy saver on, or Windows transparency effects off, a highlight's text sits on a solid band
  instead of directly on the picture.
- What's new no longer jumps when the window gets shorter or taller than 720 pixels.
- An artist page no longer flashes its bare photograph for a frame when the colours from its photo load.
- Synced lyrics no longer show the whole column at its next position for one frame before scrolling there, which made
  some line changes look like a jump.
- The player bar no longer slides down out of the window and back when you select an album in your library or the page
  under it resizes.

## [0.3.6] - 2026-10-26

### Added

- **A Zune theme that brings back the Zune HD look.** Settings > Appearance > Theme > Zune gives Wavee big lowercase
  type, pivots across the top, colour washes from the playing cover and a side panel that is one surface with the page.
  It covers every page, dialog and menu, in dark and light.
- **A WinUI theme that looks like Windows 11.** Settings > Appearance > Theme > WinUI gives Wavee tabs in the title bar,
  a navigation pane, card lists and WinUI-style controls. Settings are grouped into seven tabs, the BPM and Key column
  is optional and off by default, long artist names shrink to fit before they are cut, and the docked side panel moves
  with the page and resizes live while you drag its edge.
- **Every page now has a design for every window size, from a phone-sized narrow window to 4K.** Narrow windows get
  one-column pages with buttons large enough to tap, medium windows leave out what does not fit, and very large windows
  use the extra room for more columns, bigger covers and wider side columns. The first frame is already laid out for the
  window's size, and the entries under Changed list what each area does.
- **Three Classic options for how much colour the artwork brings.** Settings > Appearance > Wavee Classic options >
  Surfaces has "Tinted surfaces" as three preview cards: Off, Subtle (the look until now, and the default) and a new
  Rich that tints the title bar, sidebar, page ground and Home's washes more strongly. "Accent from artwork" (on by
  default) lets Play, Follow, hearts and links take their colour from the cover, and "Now playing colours" (on by
  default) does the same for full screen, its lyrics and the queue's now-playing chip. Turn either off to use your
  Windows accent or neutral colours.
- **The sidebar can show its playlists as plain text rows.** A new "Show covers" switch in Settings > Appearance > Wavee
  Classic options > Sidebar (on by default, Classic layout only) turns playlist, album and pin rows into compact text
  rows when you switch it off. Folders keep their icon, child rows stay indented, and the icon rail keeps its covers.

### Changed

- **Wavee needs less graphics memory.** The window's background and chrome are drawn directly instead of being kept as
  cached pixels (about 20 MB less while you browse), small thumbnails take less GPU room, and the peak over a long
  session is about 12 % lower. On Snapdragon PCs the cover cache now keeps to its size instead of growing to twice it,
  up to 45 MB less after browsing many pages. Covers no longer stay in memory after they reach the screen.
- **Moving between pages is lighter.** Opening an album, artist or playlist with the queue open no longer redraws the
  queue, and a short pause after every page change, spent reading the app's own memory use, is gone.
- **Auto zoom picks a smaller zoom on 1440p and 4K monitors, and applies at once.** On a large monitor Auto no longer
  zooms so far that a wide window gets narrower pages, so there is more room and the text is a little smaller; 1080p
  monitors are unchanged. The new zoom applies in the same frame when you maximize, restore, snap, release a window edge
  or change monitor. A zoom you pick by hand (the Zoom row, Ctrl+Plus/Minus, Ctrl+wheel) stops before the page gets too
  narrow.
- **The window adapts to its size in four steps.** In a narrow window the sidebar is a drawer behind the ☰ button, the
  page runs to the window's edge, and the title bar folds its tabs into one count button with Back and search beside it.
  Medium windows get the slim icon rail, large windows the full resizable sidebar, and very large windows a wider title
  bar search and wider tabs. A window resting on a step no longer flickers between the two layouts, and pages share one
  margin that grows with the window.
- **Home, Browse and Search use the room they have.** Narrow windows stack the daylist, show Recently played two tiles
  across, turn shelves into strips you swipe and run Search edge to edge with a scrolling tab strip; Home's facet row
  ends in a Friend activity button. Very large windows show ten covers across a shelf, the daylist as a hero beside
  Recently played, more genre columns in Browse, and in Search the top result beside Best matches with an Artists row of
  round cards. A short window caps the daylist so the page shows content.
- **Album, playlist, Liked Songs, show and episode pages fit every window size.** In a narrow window song rows are
  compact with the heart and "…" always visible, the song's length sits under its title, buttons are large enough to
  tap, Filter opens as a bottom sheet, and playlists and Liked Songs drop the "#" column and show Recommended songs
  below the list. Medium windows turn Sort into an icon and drop the Album and Plays columns. An empty playlist shows
  "Nothing here yet" and a field that searches for songs to add at every size, and Local files follows the same layouts.
- **Very large windows use their room on album, playlist and Liked Songs pages.** An album gets a right-hand column with
  About this release, the artist (a large photo, monthly listeners, the bio), Fans also like and More by, and the songs
  keep their margin instead of running to the window's edge. A playlist shows its recommended songs and Insights in a
  side column, and Liked Songs opens with Insights showing; close it and it stays closed for that visit. Changing
  between the hero and side-rail layouts now cross-fades once the window has settled instead of snapping.
- **Artist, profile and concert pages fit every window size.** In a narrow window the artist's photo fills the top with
  the name and listener counts on it, buttons sit in a row below, Top tracks is one column, shelves are strips you
  swipe, and profiles and concerts get a centred, tappable layout; short windows cap the header height. Very large
  windows show a row of section links under the photo, a More button after Radio, Top tracks in more columns, an "Artist
  pick" heading with the artist's next concert under it, and a concert's lineup beside related dates. The photo viewer's
  buttons are tap-sized and its thumbnails scroll instead of running off the window.
- **The artist header reads better, and Follow no longer looks like Like.** The name, bio and stats now end a little
  past the middle of the header instead of running over the bright half of the photo, and the dark side of the photo
  reaches exactly as far as the text. Follow on artists, profiles and search results shows a person (with a plus before
  you follow) instead of a heart, and Top tracks appears together with the rest of the page instead of fading in after
  it.
- **The library and Recents fit every window size.** In a narrow window the library is one column with kind chips over
  the title, a Sort button that opens a menu, list and grid buttons and a breadcrumb back from an opened album, show or
  artist; the change between columns and one column fades instead of jumping. Medium windows show Sort as one button,
  two covers a row and a tidier Recents with an aligned time column. Very large windows tint the list, show four covers
  a row, put "Also by … in your library" beside an album's songs, and start the library list and sidebar wider until you
  drag them yourself; sort words that do not fit go into a "more" menu. Recents shows its Overview calendar beside the
  list, and clicking a day scrolls the list to it.
- **The library page is one surface, and its controls match the rest of the app.** The list and the album or artist
  detail beside it sit on one content card split by a thin divider instead of three differently tinted panels. The
  artist header's buttons are one row (Play all, Follow, then Shuffle and Open artist page as quiet icons that fold into
  a ⋯ menu when the row is too narrow), the sidebar library's sort reads "Recents ⌄", and Browse's Top and For you links
  use the same chips as every other filter.
- **Settings and the utility pages fit every window size.** In a narrow window the tabs are one strip that scrolls
  sideways, each setting sits under its name with its control full width, controls are large enough to tap and the theme
  picker is two columns. Very large windows put each tab's sections in two columns and the theme cards in one row, and
  sections slide between one and two columns without losing your scroll position, open sections or focus. History, the
  Home customizer, the log viewer, the diagnostics and module pages and the not-found page become one column in a narrow
  window and stop at a comfortable width in a very large one.
- **Full screen and lyrics fit every window size.** In a phone-shaped window full screen is one column: cover and title,
  lyrics, Up next or the artist, and a playback card across the bottom, with every button tap-sized and a visualizer
  chip instead of the gallery opening on its own. Medium windows get a smaller playback card and smaller lyrics. Very
  large windows add an Up next column and visualizer bars beside the lyrics, let the visualizer gallery run to the
  bottom with the controls beside it, and put like and more buttons next to the video title. In a window only 330 tall
  the cover, title and controls stay apart.
- **The player bar, side panels and queue fit every window size.** In a narrow window the player bar is a mini bar with
  a thin progress line, the song as one button that opens Now playing as a sheet, the heart and play; lyrics, the queue,
  friends and Now playing open as bottom sheets that a tap outside closes. Medium windows keep previous, next, the
  times, volume, lyrics and the queue in the bar, with side panels floating over the page, while large windows dock one
  panel beside it. Very large windows spread the bar into three zones and open the queue in its own panel beside lyrics
  or Now playing, and the Queue button now stays in the bar from narrow widths up.
- **The now-playing sheet and the queue are easier to use in a narrow window.** The sheet has a header with a close
  chevron, "Playing from" with the album or playlist name (tap it to open the page) and a "⋯" button for the track menu,
  and a Devices button between Lyrics and Queue opens the device picker. The queue's hearts, remove buttons, toggles and
  Clear are tap-sized, and the video mini player shows full screen and close all the time instead of only on hover.
- **Menus, toasts and the empty, error and offline pages fit a narrow window.** A right-click or "…" menu opens as a
  bottom sheet with the song's art and name, a row of Play, Play next, Play after and Save, then large rows, with
  sub-menus opening in place. Toasts are as wide as the window and sit just above the mini player, the offline bar and
  its Retry button are full width, the offline and empty pages stack their buttons, and the notification panel and
  account menu never leave a narrow window.
- **Dialogs and the stock controls fit a narrow window.** Dialogs (Set up local playback, rename, confirm, log out, play
  a link, the playlist picker, credits) fill the window, keep their buttons in view and scroll their body when they are
  taller than the window; the report-a-problem dialog and the crash prompt fold the report preview behind a row, and in
  a very large window they open as a two-pane card with the live report beside the form. Dialog buttons share the row,
  dropdowns, number boxes and menu rows are taller, and none of it stretches in a very large window.
- **Setup fits every window size.** In a narrow window setup is a full-screen sheet with a stacked footer, medium windows
  show the two sign-in cards side by side, and very large windows get a larger setup plate.
- **What's new is easier to read, and its pictures follow light and dark mode.** The release's highlights lead the page
  the way the Microsoft Store does, one large tile and smaller ones beside or under it, and open in full with a click.
  Every change is one line you can open for the details, grouped by Added, Changed, Fixed and so on, with a bar to show
  one group. The release list is a plain list, and in a narrow window a picker under the title. Highlight pictures have a
  light version in light mode, and the highlight viewer's page dots and text are readable in light mode. The after-update
  plate fits narrow windows and its footer buttons are no longer cut off in a short window.
- **Offline and loading states no longer move the page.** While you are offline, saved pages show a slim "You're offline
  · showing saved content." bar with Retry above the page, and a page with nothing saved says "You're offline" with
  Retry and a "Go to Your Library" link instead of "Something went wrong". A failed Home view shows its error bar over
  the top of the content instead of pushing the sections down, and loading placeholders now match what replaces them in
  the library, on album and playlist pages, in Liked Songs and in the artist header. Podcast episode lengths follow the
  app's language.
- **Album, playlist and Liked Songs pages say when they hold songs you can't play.** A bar over the list reads, for
  example, "2 songs here can't be played: one isn't available in your country, one isn't out yet." Its **Hide them**
  button turns on the filter's "Hide songs I can't play", and the bar slides open and closed instead of moving the list.
- **Liked Songs is a list with a title, not a cover and a rail.** The page is the title, the song count and one bar with
  Play (and its ⌄ for more ways to play), Shuffle, genre chips and on the right Filter, Date added, Find, Insights and
  ⋯. Rows run the full width, the heart shows only on the song you point at, and a list sorted by Date added is grouped
  into "This week" and one group per month. The cover and its style picker stay on the narrow-window header.
- **Track rows are quieter at rest.** A song shows a heart only when you like it, and the outline heart and the "…"
  button appear when you point at the row or move to it with the keyboard. The versions chevron appears only on songs
  that have another version, a song with a music video gets a small film icon after its title, and a song you can't play
  says why in its time column ("Unavailable" or its release date) with the full reason on hover.
- **Album, playlist and Liked Songs pages lead with one Play button and a simpler song list bar.** Play has a small ⌄
  beside it (Add to queue, Play next, Start radio), followed by Shuffle, the heart, Share and ⋯; your own playlists no
  longer show a heart. The bar above the songs holds Play next and Shuffle on the left and Filter, the sort, Find,
  Insights and ⋯ on the right, and Insights now opens as a sheet over the list on every playlist and on Liked Songs. A
  colour taken from the artwork always keeps enough contrast for the Play label, hearts and accent text, and "Add to
  playlist" in a playlist's list menu reads "Copy to playlist".
- **The title bar search and page heads are tidier.** Clicking into the search box widens it without covering a tab or
  your profile picture, and its suggestions line up exactly with the box. Ctrl+, opens Settings. Browse categories, Home
  sections and the Concerts pages show one large title with a small "Home › Browse" trail above it instead of dimmed
  parent titles, and Settings drops the empty gap above its first section and cross-fades between tabs like a page.
- **Settings > Appearance is regrouped around themes.** It starts with a Theme picker, then one expandable "Wavee
  Classic options" card holding the sidebar, track list, track page and surface settings that used to be spread over
  several sections, followed by App mode, Zoom and Marquee text; the daylist clock moved to its own Home section.
  Sidebar layout and density, Color washes, Track list style, row density, "Always hide track artwork" and the track
  page layout are now Classic options with your current choices carried over, and "Reset options" puts them back
  together. The command palette lists a theme's options ("Layout: Classic", "Layout: Library"), and the setup wizard's
  second page is now the Theme page.
- **A pinned row's pin is a button.** A pinned sidebar row shows a real "Unpin" button that unpins in one click, and
  "Pin to sidebar" / "Unpin from sidebar" is a top-level item of a playlist's or folder's right-click menu instead of
  sitting inside "Organize".
- **Player style is hidden until it changes Now playing.** The gear above the Now playing art, the art's "Player style…"
  menu, the Settings > Appearance > Now playing section and the command palette's two player-style commands are only
  offered in developer mode, where the styles actually show; elsewhere Now playing shows the cover, as it already did.
- **Dutch and Korean cover the new layouts.** The text added for the themes, the theme picker and its options, the
  "can't be played" bar and the narrow-window sheets now has Dutch (nl) and Korean (ko-KR) text instead of falling back
  to English. These are unreviewed drafts, so both languages stay out of the language picker until their tables are
  reviewed.

### Removed

- **The developer-only "Page motion" setting.** Settings > Appearance no longer offers it. Pages always animate with the
  standard fade-through that everyone already had, and a value stored by an older build is ignored.
- **The lyrics blur "Auto" choice.** The intensity slider in Settings > Appearance > Lyrics is the one setting, and 0
  turns the blur off. If you had Auto, your lyrics keep the full blur until you move the slider.
- **The light/dark button in the title bar.** Switch with Settings > Appearance > App mode or the theme row in the
  profile menu.

### Fixed

- **Repeated songs in another device's queue are separate rows again.** When you watch the queue of a phone or another
  computer that repeats its album or playlist, clicking the second copy of a song used to play the first. Clicking a
  copy now plays that copy, and the queue no longer redraws every row each time the other device updates it.
- **The audio cache keeps caching once it is full.** A cache just under its size limit used to refuse every new piece of
  a song, so new music stopped being cached. It now evicts the songs you played longest ago to make room.
- **Resizing the window past a size step no longer jumps or rebuilds the page.** Home's shelves, Browse and Search
  grids, the Recents tabs, the Settings tab strip, the What's new picker, the log list and the artist page's shelves,
  chart and albums used to be rebuilt and lost their scroll position, page or focus; they now glide into their new size.
  Browse's Charts and Search's Best matches cross-fade instead of shoving the sections below, the library no longer
  flashes its one-column view as the window narrows, and Top tracks and full-screen lyrics resize in place.
- **Pages you return to and the first frame appear at their final layout.** A page opened again after leaving the page
  cache no longer shows a default-width layout for one frame and then snaps. Wavee shows its window only once the first
  frame is drawn, the setup screen is already up on a first start, and with Zoom on Auto the first frame opens at the
  right zoom instead of resizing half a second later.
- **"Choose device" opens the Devices list in a narrow window.** The button on a message (and the tray's Devices entry)
  now brings up the Now playing sheet and its device list, and the sheet's Lyrics, Devices and Queue buttons sit at the
  designed distance under the playback controls.
- **The narrow-window drawer, the Now Playing sheet and the other bottom sheets behave like overlays.** The mouse wheel
  and touchpad now scroll the drawer or sheet instead of the page behind it, and the drawer shows the full navigation
  again. The Now Playing sheet fits the window and has its own seek bar and playback buttons, and you can drag any sheet
  down by its handle to close it. Opening one moves keyboard focus into it, Tab stays inside, and Escape closes it.
- **Store screenshots are no longer dimmed.** They used to come out with a grey cast and black chrome; they now show the
  window's own background colour.
- **The window can no longer be dragged down to a title-bar strip.** It now stops at 330 tall, which keeps the title
  bar, a usable page and the player bar on screen. (#197)
- **The album and playlist side column shows again, in the cover's colour.** In Classic the left column of an album or
  playlist page (cover, title, artist, song count and Play) was blank, and the collapsed column lost its expand arrow.
  The page also takes its cover's colour when that colour arrives after the page opened, and turning Surfaces off with a
  page open no longer leaves its colour behind.
- **Page changes move as one.** The Browse and Concerts title now leaves with its page and fades in with the next one,
  the window colour from a page's artwork changes when the new page arrives, and an album opened from another album no
  longer shows the previous album's colour behind its placeholders. The tinted ground behind an album or playlist now
  fades when you switch between light and dark, and Home's loading bar fades in its fixed place instead of popping.
- **The sidebar says what it cannot show.** A cut-short sidebar name shows in full as a tooltip. A pinned item that
  cannot load while you are offline keeps its last name and cover, dimmed, with a "Not available offline" mark. When
  Your Library cannot be refreshed, a banner says "Couldn't refresh your library. Showing what was loaded." with Retry
  instead of loading forever, and the grid shows placeholder tiles while it loads.
- **Lyrics say when they need a connection.** Offline, a song whose lyrics are not saved shows "Lyrics need a
  connection" with Retry instead of "No lyrics available", and the lyrics appear by themselves when you are back online.
  Saved lyrics still show offline.
- **Full-screen visualizers share one light/dark setting.** Every visualizer face now picks its light or dark look from
  the same place as the rest of full screen, and the darkening behind the cover or lyrics suits each face (deeper behind
  Bloom and Ring, lighter behind the others).

### Known limitations

- **WinUI theme: the artist page can jump while a side panel closes.** The page restacks as the panel goes away, and the
  content can shift for a moment.
- **WinUI theme: the side panel's tab strip is cut off at its narrowest width.**
- **WinUI theme: the player bar cuts long song credits without "…".** It also does not follow the scrolling-text
  (Marquee) setting yet.

## [0.3.5] - 2026-10-13

### Changed

- **More useful crash reports.** Exit reports retain the failing app's process, session and GPU context, and their
  log excerpt follows that process. The crash dashboard explains when a local dump was omitted from an upload;
  recovery and shutdown checkpoints help diagnose remaining crashes. Existing consent settings still apply. (#209)
- **Smooth expand and collapse.** Sidebar sections and folders, the song details under a track row, Recents, the
  discography, the Blend card, log rows, episode replies and an album's "Show all" now open and close with one smooth
  motion that is the same in both directions. Everything below moves along with it instead of jumping at the start or
  end, a second click reverses it mid-way, and several sidebar sections can open or close at once instead of the
  earlier one snapping shut.
- **A new sidebar.** The sidebar now looks and behaves like a Windows navigation pane: rows, headers and the selection
  bar match Windows 11, the pane opens and closes on Windows 11's pane curve, and it switches on its own between the
  full pane, a 48-px icon rail and a pane that slides over the page as the window narrows. Pick one of two layouts in
  Settings › Appearance or from the sidebar's right-click menu: Classic (Home, Pinned, Collections, Playlists, and optional
  Recently played and New releases sections) or Library (one list with filters, like Spotify's Your Library). In
  Library, the "Your Library" button opens your Albums, Artists, Podcasts or Audiobooks page, and the chips and the
  toolbar under it filter, sort and search the list. "Edit sidebar…" lets you show, hide and reorder sections and pins
  in place, with Undo. Pins now belong to the signed-in account, so switching accounts shows that account's pins. Your
  current sidebar carries over.
- **About 900 MB less memory when on-device lyrics sync is not working.** The NPU models it loads for a song are
  released 90 seconds after the song is timed instead of after 10 idle minutes, which while you listen meant never,
  and they are now also released while Wavee is minimized. The next song loads them again in about a second and a
  half, while its audio downloads.
- **Filter is a button of its own on song lists.** Album, playlist and Liked Songs pages show a labelled Filter
  button with a count of the filters that are on. It opens one card with a dropdown per filter, switches for liked and
  playable songs, an "X of N songs" heading and Reset, and the card follows filters you change elsewhere. On a narrow
  window the button first drops its label, then moves into ⋯ as "Filter…".
- **Every filter chip looks the same.** Chips on Liked Songs, Concerts, the sidebar's Library and notifications are
  now the standard Windows toggle chip; a lit sidebar chip says "Clear filter" when you point at it.
- **A tidier sidebar.** Covers, glyphs and section titles start on one line, a cover no longer touches the selection
  bar, the "+" sits beside its section's chevron, and a count or pin mark ends at the row's edge instead of leaving a
  gap; pointing at the row swaps it for the row's ⋯ button.
- **Less space between a page's side column and its songs.** The gap beside a resizable album or playlist column is
  half as wide, the same as everywhere else; the column still resizes from that edge.

### Removed

- **The Custom sidebar layout and its full-page customizer.** Its sections are mapped onto the closest Classic or
  Library setup the first time this version starts (a toast says which ones could not be kept); everything it offered
  is now in the sidebar's Edit mode.
- **The Settings bar at the bottom of the sidebar.** Settings is in the profile menu and the command palette, and the
  sidebar's options are on its right-click menu.

### Fixed

- **Resizing after graphics recovery can no longer strand the rendering thread.** Closing after a removed graphics
  device also skips its invalid fence drain while healthy devices still finish their queued work. These correct
  reproduced engine defects; the reported AMD, NVIDIA and unknown-module access violations remain under investigation. (#209)
- **Album covers, avatars and headers load sharp.** Images that Spotify describes only by their maximum width used to
  load the smallest (64 px) version.
- **The now-playing equalizer keeps moving after you navigate back to a page,** and a paused one no longer restarts
  when the window comes back.
- **Collapsing or expanding a sidebar section slides its rows** instead of wiping or snapping them, pinning and
  unpinning animates too, and rows no longer go missing after collapsing Pinned.
- **Home respects "Colour washes".** With washes off, Home no longer tints the title bar from cover art.
- **Long Browse and Concerts titles stay on one line** instead of drawing over the page.
- **The in-list search stays open** on narrow album, playlist and Liked Songs pages instead of closing the moment it
  opens.
- **The Local playback setup footer wraps** instead of cutting off "Try again".
- **Album pages no longer jump while they load,** and "Featured on" rows no longer keep a stuck accent outline.
- **Artist, profile and episode pages line their tab words up with the page title.**
- **Liked Songs shows the selected genre once,** not as both a lit chip and a second pill.
- **Pointing at a sidebar section header no longer lights up its ⋯ button.**
- **Artist names on narrow album and playlist pages stay readable** instead of collapsing to "…".
- **Search suggestions keep their height while you type** instead of shrinking to a bar on every keystroke.

- **A pinned Your Episodes opens Your Episodes.** It used to open a playlist page with recommended songs, "2
  collaborators" and no episodes; now it opens the Your Episodes tab of the Podcasts page, and its sidebar row counts
  episodes instead of songs. On the Podcasts page, the "Followed shows | Your Episodes" switch now has its own row, so
  "Your Episodes" no longer slides off the edge of a narrow column. (#200)

## [0.3.4] - 2026-10-06

### Added

- **New full-screen visualizer layouts.** The sliders button in the full-screen view now opens a "Full screen" panel with two
  tabs: Visualizer (pick any visualizer from a grid, filter by group, or open the gallery) and Layout. Next to the classic
  layout (now "Visualizer first") are three new layouts: large album art beside the lyrics, a centered cover inside a ring of light, and an
  artist-image view that fills the screen with the artist's own header photo (slowly panning and zooming, with the lyrics
  over it if you like). Each shows a bar, ring or line spectrum, or none, and the image dimming and the slow pan are
  yours to set; artists without a header photo use the large-art layout. Press [ and ] to step through the layouts.

- **Choose what the daylist clock shows.** Settings › Appearance › Lists › Daylist clock offers "Countdown and update
  time" (as before), "Update time only" or "Countdown only", on the daylist's playlist page and its Home card. Update
  time only shows just "Next update at {time}", with no ticking digits moving at the edge of your view, and still
  switches to "Updating your daylist…" the moment the daylist rolls over. (#185)

### Changed

- **Lyrics, track changes and scrolling use far less of the GPU.** Wavee now redraws only the pixels that changed
  instead of whole 1024 x 512 tiles, keeps the lyrics' blurred lines instead of re-blurring them every frame, and
  skips frames that would look identical. Measured on a Snapdragon X Elite: the lyrics rail uses about 70% less GPU
  time, a track change about 85% less, and scrolling a playlist about 40% less.
- **Playing music in the background barely touches the GPU.** The now-playing meter, the player bar's scrolling
  title and the full-screen karaoke line now animate on the render thread instead of waking the whole app for every
  frame. With music playing on Home, the GPU is about 75% less busy, with the same motion on screen.
- **Opening pages and playlists creates about half as much garbage.** Navigating between pages allocates 46-62%
  less memory per frame and runs half as many garbage collections; opening a playlist allocates about 53% less.
- **Wavee gives memory back.** The metadata cache now has a size budget, and while Wavee is minimized and silent it
  compacts its memory and returns it to Windows. It never does so during playback.
- **Memory is freed while Wavee is minimized or in the tray.** After two seconds hidden, Wavee releases its drawing
  surfaces and the cover pictures that are not on screen. The moment the window comes back it is redrawn in full, so
  nothing is blank or stale; released covers load again when you scroll to them. A window that is only covered by
  another one waits 30 seconds, so switching back with Alt+Tab stays instant. After five minutes minimized or in
  the tray, the covers still on screen are released too, apart from the one on the player bar; when you bring Wavee
  back its first frame waits a moment (normally under 0.2 seconds) for them to load, so the window never
  shows an empty cover. A window that was only covered never gets this far.
- **A minimized Wavee wakes up far less often.** Its touchpad-scrolling support used to wake the app four times a
  second while the window was minimized or in the tray; it now sleeps until something it has to do needs it.


- **Play, pause and launch no longer stall the window while the taskbar jump list updates.** Every launch and every
  play/pause rebuilt the taskbar's right-click menu on the window's own thread, which froze Wavee for 10 to 90 ms
  each time. The menu is now written in the background, and a quick run of play/pause presses writes it once, for the
  state you end on. (#115)


- **The AI lyrics models load only when a song needs them.** Wavee loaded them at every start, even when nothing
  would be timed, which kept about 900 MB of model files and NPU buffers in its memory for the first ten minutes.
  They now load when a song first needs word-by-word timing, while the song's audio is being fetched, so the
  lyrics are not later; they still unload after ten idle minutes.

### Fixed

- **The tray icon shows on scaled displays.** At some display scales (125% / 150%) Windows asked for an icon size that failed to load and the tray icon could be missing; Wavee now falls back to the nearest size.
- **Skipping through a playlist asks Spotify less.** Changing track used to send several separate metadata requests within milliseconds; they are now combined into one, which keeps rapid skipping well under the request-storm warning.
- **Liked Songs sync more often stays incremental.** A delta that named a song you never held (or one you already had) threw off the count by one and forced a full re-download of your Liked Songs; only real changes are counted now.
- **Playing a local file no longer sends its file path to Spotify.** While a local file played, Wavee told
  Spotify Connect about it by an id that contained the file's full path, including your Windows user name (Spotify
  rejected it). Your other devices are now told only the track's title, artist, album and length, the way Spotify's
  own app does it, and nothing that is not a Spotify item (local files, local playlists, module streams, covers on
  your disk) is sent to Spotify anymore.
- **Karaoke lyrics no longer stall for up to a second.** Lyrics could freeze between lines, and sometimes for a
  whole stretch, when a timer fired a few milliseconds early; the wipe now always moves on time.
- **Images are sharp on scaled displays.** Images with a fixed size were decoded at their unscaled size and stretched
  at 125-200% display scaling; they now decode at the screen's real resolution, also after moving the window to
  another monitor.
- **No more stale pixels after redraws.** Text that runs past its box, and offscreen effects that were re-rendered
  in the same frame, could leave old pixels on screen until something else repainted them.
- **No more hitch when Wavee saves a diagnostics snapshot.** Building the automatic evidence bundle froze the window
  for one frame (about 36 ms); it is now written in the background.

- **Notification sound and quiet hours can no longer be switched while Windows notifications are off.** Their cards
  greyed out, but the switch inside each one stayed live and looked enabled. Both rows now stay visible and go inert
  together until Windows notifications are turned back on. (#183)

- **The daylist countdown respects reduced motion.** With Windows' animation effects turned off, the daylist's flip
  digits still slid and faded every second. They now change in place. (#185)

- **Word-by-word AI lyrics no longer hold on to about 100 MB.** Timing the first song of a session kept a copy of
  every intermediate step of the aligner in memory (about 98 MB, two of them over 30 MB each) until the AI models
  unloaded. The steps now hand their results straight to each other, and only the final timing is kept; the
  timings are identical.

- **Changing a playlist's cover works, and no longer freezes Wavee.** Clicking "Change cover" opened the Windows file
  picker on the same thread that draws Wavee, so the window stopped drawing for as long as the picker was open, and
  when the picker was slow to appear the window was left greyed out, answering every click with the error sound and
  no picker anywhere. The picker now opens on its own thread, still in front of and modal to Wavee, while Wavee keeps
  drawing and playing; if it never appears, Wavee hands the window back after a few seconds. A chosen or dropped
  JPEG, PNG, BMP, GIF or WebP is cropped to a square, turned upright and made small enough for Spotify, shown
  straight away, and put back with a message if Spotify refuses it. The new cover is now actually saved: Wavee used to
  upload and register the image but never set it on the playlist. "Change cover" and a new "Remove cover" are also in
  the playlist's More menu. (#155)
- **Every file and folder dialog keeps Wavee drawing.** Choosing a cache location in Settings > Storage, exporting
  logs, saving a problem report, saving an artist gallery image, playing a local file, attaching or locating a local
  video, and choosing a Spotify.dll during setup all opened their Windows dialog on the thread that draws Wavee, the
  same freeze as "Change cover": Wavee stopped drawing while the dialog was open, and could be left greyed out with no
  dialog in sight. They now open on their own thread, still modal to Wavee, with the same hand-back after a few
  seconds if a dialog never appears; only one can be open at a time.

- **The taskbar jump list offers the right verb again.** Right-clicking Wavee in the taskbar offered "Pause" while the
  music was paused and "Resume" while it played, because the menu was built from the state just before the change.
  It now says "Pause" while playing and "Resume" while paused. (#115)

- **Wavee's crash-reporting helper can no longer keep running after Wavee closes if it fails to start.** The helper
  that watches Wavee for crashes could get stuck while starting (seen when Wavee was launched under a diagnostics
  tool) and then stay running for hours after Wavee quit. Windows now ends a helper that never came up when Wavee
  exits, and Wavee restarts one that does not report in within ten seconds.

- **Local music videos (.mp4) play their sound.** A dropped or imported .mp4 (or any other MP4-family file in Local
  files, such as .m4a or .mov) was handed to the MP3 decoder, which could not read it, so it failed with "This track
  isn't available" however many times Retry was pressed. Wavee now decides what a local file is from its contents,
  not its name: the AAC sound of an MP4 plays and seeks like any other local track, and the attached video is still
  one click on the video button away. A file in a format Wavee cannot play (an Opus or WAV file, encrypted or Apple Lossless audio in an
  MP4) now stops at once with "Wavee can't play this file's format" instead of being opened with a guessed decoder,
  and the bar no longer offers a Retry that can only fail again.

## [0.3.3] - 2026-10-05

### Fixed

- **What's new shows the pictures of earlier releases again.** Opening an older release on the What's new page, or
  the welcome after an update, showed its highlight cards with empty grey bands: only the installed version's
  pictures were ever on disk. Wavee now downloads a release's pictures with its notes and keeps them offline.

## [0.3.2] - 2026-10-05

### Fixed

- **The title bar's search box no longer shrinks to a "Search" stub.** After a visit to the search page, the box in
  the title bar could stay collapsed to a sliver with its placeholder spilling out of it, on every page, until Wavee
  restarted. It now always fills its slot.
- **The full-screen visualizer no longer leaves an empty column.** With the visualizer gallery open, the picture made
  room for the gallery, but the gallery hid whenever the controls faded out, leaving the visualizer cut off beside an
  empty third of the screen. An open gallery now stays until you close it.
- **Settings' expandable cards keep their full width after a zoom change.** After zooming in or out, the headers of
  Settings' expandable cards could stay squeezed into a narrow sliver, with their icon gone and the switch spilling
  below the card. They now always span the card.
- **Switching songs no longer plays a bit of the old one, or clicks.** On next, previous or a click on another song,
  the old song kept playing while the new one opened and was then cut off mid-sound. It now fades out at once.
- **Word-by-word AI lyrics no longer leave Wavee holding hundreds of megabytes.** Every song the NPU timed left large
  buffers behind that were never handed back, so memory stayed several hundred MB higher for the rest of the session.
  Timing a song now reuses its buffers, and the memory is returned when the AI models unload after 10 idle minutes.

### Known limitations

- **The title bar's search box can still come back as a stub after full screen.** If a zoom change while full screen
  is open leaves the title bar showing only the search icon, the search box is a narrow stub when you leave full
  screen.

## [0.3.1] - 2026-10-05

### Added

- **Nineteen visualizers in full screen.** The visualizer gallery now has five groups. Lyrics has Verse, where the
  song's words land as they are sung, held notes grow and a returning chorus lights up. Fluent has Bloom, Bars, Ring,
  Orbit, Aurora and Timeline. Classics, after Winamp and Windows Media Player, has Classic, Warp, Tunnel, Ambience,
  Kaleido, Scope and Drift. Zune has Type, Mosaic and Spotlight, and iTunes has Magneto and Flow. Every face takes its
  colours from the cover, works in light and dark mode, and keeps moving from the song's own waveform when Spotify
  Connect plays elsewhere. Press `[` and `]` to step through the faces and `G` to open the gallery; the gallery
  animates only the previews you can see, and a preview you rest the pointer on comes to life.
- **Change with the music.** Every eight bars, on the downbeat, the visualizer rotates the cover's colours with a short
  cross-fade, and a returning chorus does the same. A new song cross-fades to its own colours. Turn it off in the
  gallery or in Settings > Appearance > "Change with the music".
- **A new Artist pane in full screen.** The artist's photo fills the band, with monthly listeners and world rank, Follow
  and Go to artist. Below it: the full biography, a photo strip, this song's credits grouped by person, the five most
  popular songs, where people listen and related artists.
- **Word-by-word lyrics with on-device AI.** On a Copilot+ PC with a Snapdragon NPU, Settings › Appearance ›
  Lyrics can download the AI models once (about 700 MB, English; Spanish optional). Wavee then times every word
  of a song's line-synced lyrics on the NPU while the song plays, so the lyrics wipe word by word. The song and
  its lyrics never leave the PC, and a ✦ in the lyrics header shows when the word timing is AI-generated. (#175)

### Changed

- **Motion runs at your display's refresh rate.** The buffering spinner, loading shimmers, the now-playing equalizer,
  the decks, the full-screen visualizer and the full-screen lyrics caption were held to 30 frames a second (24 on
  battery), which looked choppy on a 120 or 144 Hz screen. They now run at the panel rate, and Wavee no longer slows
  its animations down when its window loses focus. With Windows Energy Saver on, all motion is capped at 30 frames a
  second; scrolling and dragging never are.
- **Context menus open faster.** A menu that fits inside the window is drawn in the window instead of in a separate
  popup window, so a track's right-click menu reaches the screen in less than half the time.
- **Full-screen Now Playing uses a big screen.** On an ultrawide or a large monitor the cover grows to fill its
  column, the lyrics grow with the stage, and Up next rows get larger art and type. The Up next list stops at a
  readable width, opens with the next song set apart, splits into sections with their counts, numbers each row and
  marks explicit tracks.
- **The fullscreen visualizer redraws only what moves.** The drifting background and the visualizer bars no longer
  redraw the whole screen on every frame, the frosted panels stop re-blurring when nothing under them changed, and the
  background is drawn at a quarter of the screen resolution (it is a soft gradient, so it looks the same). Before, every
  frame of the visualizer redrew all of a 3440 x 1440 screen, which took about half of the GPU at 120 Hz on a
  Snapdragon X laptop.
- **The fullscreen stage no longer draws the app hidden behind it.** While the stage is open, the pages under it are
  left out of every frame, and the stage's background (the blurred cover, the drifting colours and the dark tint) is
  drawn as one layer instead of three. In the offline demo on a Snapdragon X laptop at 120 Hz, the stage takes 1.69 ms
  of GPU time per frame instead of 2.35 ms (Task Manager: about 22-25% GPU instead of 32%).
- **Opening pages leaves less memory behind.** Spotify's answers (the Home feed, artist and album pages, track
  metadata, playlists, Connect updates) are now read into reused buffers and decoded in place instead of being copied
  into fresh memory several times over. A large answer used to leave two to five times its own size of garbage in the
  part of memory that is the slowest to clean up and the easiest to fragment: about 2.7 MB for the Home feed and up to
  10 MB for a large metadata batch. The play history is also written to disk without building the whole file in
  memory first.
- **Pages with nothing moving stop drawing.** An animation that is hidden, covered or too small to change a pixel no
  longer keeps the app drawing frames. Measured on a Snapdragon X laptop at 120 Hz, an idle Home went from
  3,570 presented frames in 30 seconds to 15, and the app's CPU time from about 2.3 seconds to about 0.3 seconds in 20.
- **The window redraws only what changed.** When one part of the window changes, such as a progress bar, a hover or a
  lyric, Wavee now redraws and presents only that part instead of the whole window, and it skips whatever an opaque
  layer covers.
- **Lyrics take a fraction of the work.** The karaoke wipe across each line now runs beside the GPU work instead of on
  the app's main thread, and the lyrics column no longer wakes the app for a scrollbar it never shows. With the same
  wipe, the open lyrics view now runs the app's main thread about 28 times a second instead of 106 (offline demo,
  Snapdragon X, 120 Hz).
- **Scrollbars wait without drawing.** After a scroll, the two seconds a scrollbar waits before it fades no longer
  redraw the window every frame; the app sleeps until the fade starts.
- **Every graphics chip gets the same picture.** On integrated and lower-end GPUs, Wavee turned off the shimmer on
  loading cover art and used a weaker lyrics blur by default. Both now look the same on every GPU; only memory limits
  still scale with the hardware.

### Fixed

- **Light mode in full screen.** The player bar, the now-playing card and the visualizer gallery stayed dark with
  dark text in light mode, and a few controls kept white outlines. They now follow the theme.
- **The pop-out video window no longer slows down the main window, and video never slows the main window.** The pop-out
  used the main window's frame slot, so opening a flyout or resizing could freeze the app for up to 0.6 s while a video
  played in the pop-out. Each window now presents on its own, the pop-out's frame is presented before the main window
  waits for its own, and opening and closing the pop-out no longer shows an empty window. Nothing is throttled for it:
  the main window keeps its full frame rate whether or not a video plays, docked or popped out. (#171)
- **No more ghost edges around DRM video.** The picture and the hole the UI leaves for it now move in the same frame, the
  video is scaled smoothly instead of with blocky pixels, and the last frame of the previous video no longer shows
  through after a switch. (#172)
- **Switching videos and moving the video between docked, fullscreen and the pop-out is faster.** Turning video on keeps
  the music playing until the first video frame, an outdated load is cancelled, and leaving fullscreen no longer drops
  the video to its lowest quality. (#173)
- **Steadier video playback.** Lyrics, the player-bar text and the equalizer no longer redraw the window when they are
  hidden or the window is covered, the player-bar title glides at the display rate as before and keeps scrolling beside
  a video, and the diagnostics log now records dropped frames and how long each switch takes. (#174)
- **A clock that does not wait no longer burns a CPU core.** On some setups the compositor clock that paces frames
  returned at once instead of waiting, and Wavee spun on it: a second window with the full-screen stage open held about
  one core with nothing animating. Wavee now notices such a clock and paces itself on a timer instead (10-second
  measurement: 11.7 s of CPU down to 0.8 s, still 120 frames a second).
- **Mouse-wheel scrolling keeps working right after you leave a page.** Turning the wheel while going back or switching
  pages sent the next notches to the page you had left, until the wheel paused for a moment. They now scroll the page
  under the pointer.

## [0.3.0] - 2026-10-02

Crest is Wavee rebuilt from the ground up. Every page, the playback engine, Spotify Connect and the local library were
rewritten on one data model, so pages open faster, cards and rows behave the same everywhere, and scrolling runs on a
new model shared by wheel, touchpad and scrollbar. On top of 0.2's features it adds a new Home, pages for podcast
episodes and for people, playback speed, a notification-area icon and opt-in crash reports you can read before
they're sent.

### Added

- **A new Home.** A pivot across the top switches between All, Music, Podcasts and Audiobooks, with a Following toggle
  beside it; the daylist gets its own card, and the page below is laid out in zones. Home has its own customizer, where
  sections can be shown, hidden and reordered, and the change shows on Home at once.
- **Podcast episodes have their own page.** An episode opens with its chapters on a timeline, its transcript, the
  discussion and its ratings. Where you stopped is kept with your Spotify account, so an episode resumes from the same
  place on every device.
- **Playback speed.** While an episode plays, the player bar has a speed control.
- **An Audiobooks page in your library.** Saved audiobooks get their own library page beside Podcasts, with its own
  filters.
- **A notification-area icon.** Wavee can sit in the corner of the taskbar: closing or minimizing the window can hide it
  there and keep the music playing, it can start hidden when it starts with Windows, and the icon's menu opens or hides
  Wavee, picks a device, saves the current song to Liked Songs and quits. Settings decides whether the icon shows
  always, only while Wavee is hidden, or never. (#152)
- **Profile pages.** A `spotify:user:` link, a playlist's owner, a user in search, a friend in the activity rail and
  the account menu's new Profile row now open the person's profile: a round avatar hero with their followers,
  following and public playlists, Follow, their public playlists and recently played artists, who they follow and who
  follows them, and — on your own profile — your top artists this month. Following and Followers open full list pages
  with filter chips, a find box and a letter strip. The page loads the same profile view the official client does and
  re-reads it on every visit without flashing back to its skeleton. (#161)
- **A real full-screen Now Playing.** The lyrics stage is now a true full-screen view: the window goes borderless on
  its monitor, the title bar and the player bar step aside, and one transport card sits over the music. A selector
  switches between Lyrics, Visualizer, Up next and Artist while the cover morphs between its hero and thumbnail
  places; the accent follows the album art on every control; the controls hide after three seconds of idleness and
  a hairline keeps the progress visible. Eight visualizers — Field, Halo, Horizon, Matrix, Aurora, Spectrum, Pulse,
  Tape — move on a real spectrum analysis of what is playing, aligned to what you actually hear (with a sync offset
  for Bluetooth), and fall back to Spotify's waveform and beat data when the audio is not local. A gallery shows all
  eight live, with sensitivity, lyrics-overlay and calmer-motion switches; the window adapts to ultrawide, portrait
  and small sizes. The rail's analyser decks now read the same real spectrum. (#166)
- **The sidebar resizes freely and folds into an icon rail.** Drag its edge anywhere between 180 and 460 DIP; it no
  longer collapses on its own when the window gets narrower. Dragging past the minimum snaps it into an icon rail in
  one of three sizes — Compact (48), Default (56) or Large (80) — which you can also pick from the right-click menu's
  "Collapsed rail size" or in Settings › Appearance › Sidebar. A double-click on the splitter collapses or expands it,
  and with the splitter focused the arrow keys step its width.
- **In a narrow window the sidebar opens as a drawer.** When the window is too narrow for the sidebar and the page side
  by side (under 660 DIP), the sidebar shows your icon rail and the full pane opens as a drawer over the page.
- **Opt-in crash reporting.** Off by default: the setup wizard asks once, and Settings › Privacy & diagnostics offers
  Off, Ask each time or Automatic. After a crash or a hang the next launch says what happened and lets you read the
  full, redacted report — and attach a memory snapshot if you like — before you send it. Settings › Privacy &
  diagnostics › Crash reports lists every saved report; each row's menu offers View, Copy, Send and Delete. When Wavee
  crashes twice in a row before its window appears, it opens a small recovery window instead that can start it again,
  send the report or reset Wavee. "Delete my data…" erases every report this PC ever sent. A report says "sent" only
  once the upload has succeeded; one made while you're offline waits on your PC and goes out when you're back online or
  at the next launch; a report you deleted is never sent; and a large report with a memory snapshot gets time in
  proportion to its size. A build you compiled yourself can't send reports automatically, so it offers Off and Ask each
  time and says why. Reports go to Wavee's own crash service at crash.cproducts.dev; `PRIVACY.md` lists exactly what
  one contains. (#165)
- **Crash reports resolve to method names.** Store, stable and beta builds all carry the crash service's address and
  key, and every release uploads its symbol map, the Microsoft Store packages included, so a report arrives with
  Wavee's own method names instead of bare offsets. A release can no longer ship without them. (#165)
- **Native crashes name the faulting module and Wavee's stack.** A crash inside a driver or a Windows library now
  reports which program file it happened in and where, plus the Wavee code that called into it, so these crashes
  group by cause instead of all landing in one bucket. Their memory snapshots keep the exception record, so a
  debugger opens straight on the faulting instruction. (#165)
- **Send a test crash report (Developer mode).** Settings › Privacy & diagnostics › Developer can now build a real
  crash report without crashing — a caught test exception thrown through Wavee's own code, the redacted log tail and a
  memory snapshot of the running app — and send it to the crash service the normal way, then say whether it arrived,
  with its report id. Wavee keeps running, and the next launch doesn't mistake the test for a crash. (#165)
- **A Privacy & diagnostics tab.** Settings gets one tab for what Wavee records and what leaves your PC, in place of
  the Logs tab and the rows that were scattered through General. It starts with what leaves this PC (Spotify, lyrics
  providers, GitHub, and the crash service only when you opt in) and the three-way crash-report choice, Off, Ask each
  time or Automatic, with the memory-snapshot switch beneath it; "Delete my data…" and the privacy policy sit beside
  your install id. Saved crash reports are one list, each with its own "…" menu. Under Logs it shows the session's
  events, warnings and errors as they change, and the two detail levels; Playback runtime, Spotify Connect and Realtime
  capture are together under Tools; and the developer switches share one group at the bottom.
- **The log viewer is a page of its own.** It opens from the tab or from `wavee://open?route=logs`, and Back or Esc
  returns to where you were. The session picker and Open folder sit in the header, and one toolbar holds the filter,
  the level segments with their counts ("Warnings · 368"), the category list, Copy, Export and a "…" menu for Newest
  first, Group repeats, Wrap long lines, Verbose, Clear view and Report this session…. The Time header sorts the list,
  and the footer says how many events are shown and loads more.
- **Scrubbing is audible.** Dragging the seek bar now plays what is under the thumb: short, soft snippets that follow
  the speed of your drag and fall silent 150 ms after you stop moving, with one seek landing when you let go. A music
  video shows the frame under the thumb as you drag. While a song is paused, or playing on another device, the drag
  stays visual and a single seek is sent when you let go. Audible scrubbing covers Ogg Vorbis and FLAC; other formats
  scrub silently. (#167)
- **Keyboard seeking has a speed ladder.** With the player bar or the full-screen view focused, Left and Right step 5
  seconds; keep a key held and the steps grow to 15 seconds after half a second and 30 seconds after a second and a
  half, at most one step every 150 ms, and Shift makes a fine 1-second step. The bar and its time label show where you
  are heading, and one seek lands 250 ms after the last press; a live podcast stays inside its window. (#167)
- **Normalization has modes.** Settings › Playback now offers Quiet, Normal and Loud (−23, −14 and −11 LUFS) and an
  Album option that keeps the loudness differences between an album's tracks. They apply to Spotify streams, lossless
  and local files (their ReplayGain tags), and a change is heard on the song that is playing within a moment instead of
  from the next one. A first run also starts at a volume of about −6 dB instead of −9 dB. (#167)
- **A "Playback health" card on the Diagnostics page.** It says whether this session's audio has been clean, how many
  dropouts there were and how long the longest one lasted, the likely cause (the decoder fell behind, the output device
  was late, a garbage-collection pause or a slow download), whether the audio threads got Windows's audio priority
  registration, how full the decode buffer is, and how long the last seek took and how it was served. Every dropout is
  also one `audio.glitch` line in the log, and each session ends with an `audio.session.summary` line. (#167)

### Changed

- **The full-screen view's lyrics stay sharp.** Lines other than the one being sung are no longer blurred there (the
  sung line keeps its glow), which was the view's biggest per-frame cost. The Winamp deck's oscilloscope option is
  gone: there was no waveform to draw, so the trace was synthesised. (#166)
- **Scrolling runs on a new model.** Every list and page scrolls through one motion plan per viewport, shared by the
  wheel, the touchpad, touch, the scrollbar and the keyboard, and posed at each frame the screen shows. In practice:
  the wheel glides from notch to notch instead of stepping and pulsing, and works over a track list's column header
  too; a touchpad swipe moves the distance your fingers did, without overshooting or a hiccup where the coast takes
  over; a fling ends cleanly instead of crawling for a second, and doesn't stall or drop rows when a page grows under
  it; leaving a page mid-fling no longer leaves the app in "scrolling" mode; and a page you return to lands on its
  position at once instead of creeping into it. Sticky headers, parallax and edge fades follow the same plan, and
  scroll positions are remembered per tab.
- **Spotify Connect follows the device that's playing.** While your phone plays, the Queue panel mirrors its queue and
  what it's playing from, and a click on one of its rows skips there. A song you start in Wavee starts that song on the
  phone instead of just resuming it, from a row or a page's Play button alike. Picking "This computer" in the device
  picker takes playback over, mute is one state that every mute button reads, and the connection recovers on its own
  after sleep or a network change.
- **The queue survives a restart.** The whole queue comes back — the songs you queued by hand and the autoplay
  suggestions — with their titles and "Playing from", instead of only the current song.
- **Wavee decodes the audio itself.** Ogg Vorbis and FLAC now play through Wavee's own decoders, and a seek lands
  exactly where you asked. Lossless is offered when it's available.
- **Decryption keys are kept between launches.** A song you have played before starts without fetching its key again;
  the keys are stored encrypted on your PC, tied to your account.
- **Browse sections load whole.** A section page loads all of its items at once instead of paging them in, the Charts
  card shows Spotify's real chart playlists, and a section that can't load offers Retry instead of an empty page.
- **A finished daylist says it is updating instead of counting to zero or keeping the old edition.** When a daylist
  window ends, Home and the daylist page show "Updating your daylist…" with a spinning ring and the next daypart on its
  way, and the hero card, the playlist page and its tracks move to the new edition as soon as Spotify has it — no
  relaunch needed. The first check goes out 5 seconds after the window ends, with a few more over the next minutes
  (and on wake from sleep or when you return to the window) while Spotify still serves the old one; if the new edition
  is still late after that, Home says so and offers Check again. A newer edition always replaces the saved one, the
  countdown stays in step with the clock after sleep, opening the daylist page no longer erases the hero's masthead,
  and the hero card shows the daylist's whole cover art. (#162)
- **Developer mode and the FPS overlay apply at once.** Turning either on now shows the lyrics inspector, the
  test-notification rows and the overlay straight away instead of after a restart, and the items under Developer grey
  out while developer mode is off.
- **The capture and file log levels left the viewer.** The two level lists that sat in the viewer's filter row are
  Settings › Privacy & diagnostics › Logs › Detail level, "In the viewer" and "In the log file"; the viewer keeps
  Verbose in its "…" menu.
- **Log files are kept for 7 days, up to 250 MB, instead of the newest 7 files.** At about 60 MB a day the old rule held
  little more than a day, so past sessions were usually gone. A new file still starts each day or at 10 MB, the file
  Wavee is writing to is never removed, and a size roll is named `wavee-yyyyMMdd-HHmmss.log` in local time.
- **Memory samples and the engine's pacing lines are no longer logged as warnings.** A memory sample warns only when
  the working set reaches a new peak, and the render-pace, wake and display lines are information, so the Warnings
  count in the viewer shows what needs a look and the log files stay small.
- **Cleaner sample-rate conversion and a transparent limiter.** A 44.1 kHz song on a 48 kHz output is now resampled with
  a 64-tap windowed-sinc filter instead of straight-line interpolation, so bright material no longer carries aliasing.
  The limiter that catches EQ boosts and loud masters looks 2 ms ahead of the music so it can ease peaks down in time,
  and it now sits before the volume control, so the tone no longer changes with the volume slider. (#167)

### Fixed

- **Audio no longer halts or stutters while the computer is busy.** The threads that decode ahead and keep time now run
  with Windows's audio scheduling priority and are exempt from power throttling, so a game, a build or a minimized
  window no longer starves them. The decode buffer is 2 seconds deep (it was half a second), with the last second kept
  behind the playhead, and when it does run dry the output fills the gap with a short silence and resumes behind a
  larger cushion instead of stopping and restarting the audio device over and over, which sounded like a buzzing
  stutter. A lost output device is noticed at once instead of leaving pause and seek hanging. (#167)
- **Seeking is instant inside what is already loaded, and the seek bar never jumps back.** A seek within the last second
  or the next two seconds of decoded audio is a 5 ms crossfade. A seek into data that is already downloaded opens a
  second decoder and swaps over while the old audio keeps playing until the new spot is ready; a far seek lets the old
  audio run for up to 80 ms, then goes quiet until the new position arrives. Several seeks in a row land only the last
  target, and a second seek can no longer end a Vorbis track early. The bar and the time label stay where you dropped
  them, in the player bar and the full-screen view alike and while another device plays over Spotify Connect (the bar
  jumps at once and one seek is sent), instead of stepping back to the old position until the next report. (#167)
- **Browse section pages failed to load, showed their title twice, and a decade lost its parent crumb.** Featured
  Charts, Food, drinks & music and every other section drill went straight to "Failed". The page now loads its
  section, shows its title once in the masthead, and a category tile on a category page drills with that page as its
  parent, so Decades › 00s keeps its breadcrumb. Home's "See all" carries the section's title and the Home crumb too.
  (#156)
- **Section and Browse grid cards had no hover, play button, menu or now-playing pill.** The section drill grid
  (Browse › a category › a section, Home "See all", Featured Charts) and the Browse category grid drew their own cards.
  Both now use the app's one shared card — hover plate, hand cursor, play button, "…" and right-click menu,
  now-playing pill, drag, and a tooltip on a title only while it is cut off — and their loading skeletons are the
  card's own shape. A click on a cut-off title inside a grid now opens it. (#157)
- **Cards and rows behaved differently depending on the page: some had no hover, no click, no menu or no play button.**
  Every media card, row and tile is now one shared surface, so the hover plate, hand cursor, focus ring, play button,
  "…" and right-click menu, now-playing pill, drag and a tooltip on a cut-off title are the same everywhere. A card
  lights up while its album, playlist, artist or show plays, and its play button then pauses instead of starting it
  again. The rail's "Next up" rows can be clicked (they skip to that song, exactly as the queue panel does); playlist
  recommendation rows get a row click, play button and menu; Home and Recents episode rows get the episode menu; album
  and artist music videos get a real play button, menu and drag; the album page's "More by" rows get their menu;
  Search and library search results, queue rows, concert and module cards follow the same rules; and the sidebar's
  pinned cards, grid tiles and collapsed-rail tiles gain hover, focus, the now-playing pill, drag and (on the rail) a
  right-click menu. Text-first tiles (podcast doors, show cards, Browse and chart tiles, the artist pick, a clickable
  detail cover) keep their own look but take the same hand cursor, keyboard focus and title tooltip. The play button
  now appears when you hover a card, not only when the pointer lands on the button itself, and a card's hover
  controls are only built when they're needed, which takes noticeable time off opening an artist page. (#160)
- **Library › Artists crushed the artist name and the album titles to one letter in a narrow reader.** The artist band
  and every album head were single rows in which only the text could shrink, so at a ~390 DIP reader the name read
  "Tro…" and titles "P…". Below 772 DIP the band stacks its controls under the name (which may take two lines), below
  640 DIP the album head puts the title on its own line above the length and the circles, the album pane's "Open album"
  link no longer clips, and a cut-off name or title shows its full text in a tooltip. (#158)
- **Shelf cards took two Tab stops, and arrow keys after a click started from the old card.** A card inside a shelf now
  leaves focus and the click to the shelf's slot: one Tab stop per card, arrows move from the card you clicked, and the
  card still lights up its play button and "…" while its slot has keyboard focus. (#159)
- **Podcast pages: filtering, right-click, overlapping cards and the toolbar.** A filter or a search now shows just
  the matching episodes with a count that matches the rows (sorting keeps the resume card and Up next); a search that
  matched only the resume episode no longer shows "1 of 326" over an empty list. Up next, the resume card, the
  new-visitor cards and More like this have a right-click menu; Up next shows covers when the episodes aren't
  numbered instead of a blank column, and no longer repeats what New since your last visit shows. Cards no longer
  overlap the next section when a filter changes the list's width. The toolbar measures itself: the search box folds
  to an icon before any filter chip is cut off. More like this loads once per visit instead of on every keystroke.
  (#164)
- **Covers no longer turn blurry or stay blank.** A sharper cover already on screen is never replaced by a softer copy
  (the soft podcast covers), and artwork past the first few of an artist's top tracks no longer stays blank.
- **Your own playlists showed an empty tile in the side rail until their page had been opened once.** A playlist with
  no cover of its own shows a 2×2 mosaic of its first four album covers; the sidebar now fetches those covers itself,
  in one batched request, and keeps them on disk, so the mosaic paints as soon as the playlist list loads after a
  relaunch. Editorial playlists with a real cover are unaffected.
- **A cold playlist open showed empty rows between the shimmer and the tracks, and the detail rail snapped in.** The
  track list now stays in its skeleton until the first page of rows knows its title, album, duration and art, so a
  cold open no longer flashes blank rows, and a row further down keeps its placeholder until its own data lands. The
  two-column detail rail has a skeleton shaped like the loaded rail (cover, owner, title, meta, Play, blurb) that
  cross-fades once when the header answers — album, playlist, show and Liked Songs share it.
- **Artist and album pages flashed the app accent before their own colour.** Play, links, the pivot underline, hearts
  and the queue's Shuffle/Repeat/Autoplay chips painted blue for a few frames and then flipped to the cover's colour.
  Cover colours are now kept on disk (warm on relaunch), the album carries Spotify's extracted cover colour like the
  artist header already did, and every accent surface holds the previous page's colour until its own is known,
  cross-fading when it lands.
- **The artist hero popped in at full brightness under a blue veil.** The photograph now fades up, and the veil and wash
  take the artist's own header colour on the first frame — neutral when there is none — instead of the app accent.
- **The album page's right column jumped when its sections landed.** The column reserves its space once from what the
  album already knows (a single reserves no rows block), the remaining difference eases instead of snapping, the About
  card no longer stretches to fill a short page and then shrinks as "More by" fills in, and the watch-video card and
  "Fans also like" arrive together with the tracklist rather than a beat later.
- **Album track lists dropped their shimmer for two rows and then filled in.** The list stays in shimmer until its
  first page is in (twelve rows, or all of a shorter list).
- **The album's About and Featured-on sections appeared one after the other.** The band waits for both, or 400 ms,
  before its one swap, and asks for them at the tracklist's priority.
- **An artist's Top tracks could shimmer forever after a network hiccup.** A partly refused answer no longer seals the
  missing fields for the whole session: they are asked again when the session comes back online, the band shows Retry
  when nothing answered, and Retry re-asks everything the band needs.
- **Search painted its facet tabs a beat before the results.** The tab row now holds its skeleton until the query's
  first results have answered and enters with the same rise-and-fade, so both arrive on the same frame.
- **The tab's label changed before its page did.** The label follows the page and switches after the outgoing
  page's exit.
- **A tooltip outlived the page it described.** A tooltip opened from a row that navigated away under a still pointer
  stayed open; every navigation now closes the open tooltip.
- **Discography cards painted their hover plate and selection outline 20 DIP past the meta line.** The card grew into
  the grid's row gap; it is pinned to its own height.
- **An expanded discography drawer showed a grey stub pill under its rows.** The floating selection bar now appears
  only while something is selected.
- **A single's "Watch the official video" card read "0 songs".** An unknown track count is no longer shown as zero.
- **Artist biographies showed `&#34;` and `&#8217;` as text.** One HTML-entity decoder now handles numeric, hex and
  the named set for the bio lead, the About card and rich text alike.
- **The verified check on the album's About-the-artist card sat at the far right.** The name shrinks to its text
  with the check directly after it.
- **Emoji rendered as grey silhouettes.** Colour emoji now draw in colour.
- **The search box showed a grey completion while it was not focused.** The inline suggestion now shows only while the
  box has focus and clears when you leave it.
- **The title bar could stay on "Connecting…" after you were signed in.** It now turns into your avatar as soon as the
  account is ready, without needing a resize.
- **The Queue panel clipped its Shuffle/Repeat/Autoplay row and squeezed its rows in a narrow panel.** The chip row
  wraps instead of clipping, and queue rows keep the title and artist readable.
- **The player bar's transport jumped and the seek bar breathed when a track started.** Every slot on the right of the
  bar is now reserved up front and only its contents fade with playback state.
- **Leaving the now-playing title mid-scroll left it cut off.** The scrolling title glides back to its start when you
  move away, and the bar's title scrolls on its own at a slower pace.
- **Playing another track from the same album or playlist re-asked autoplay and appended fifty rows each time.** The
  autoplay suggestions already in the queue are kept when you play from the context that's already playing.
- **Autoplay suggestions appeared only after a skip, and never for a restored session.** Suggestions — like a context's
  next page — are now asked for when about three quarters of the current run has played (or a few rows remain), an
  ask made offline is retried once you're back online instead of giving up, and they follow on from Spotify's previous
  answer instead of repeating it.
- **The next track was not warmed for a restored session.** The next two songs are prepared ahead of time after a
  restart too, and again once the session is online.
- **Spotify Connect lost Wavee as the active device every couple of minutes.** The connection to Spotify timed out
  just before Spotify's own keep-alive arrived, so the session dropped and re-registered the device — hundreds of times
  a day — and every other client saw Wavee leave. It now outlasts two keep-alives.
- **A relinked liked track showed as blank and could not play.** Spotify serves some old track ids under a newer id;
  the old id now picks up the newer one's title, art, artists and duration, and playback picks a version that's
  allowed in your market.
- **A liked track Spotify no longer resolves rendered as an empty, playable row.** Such tracks are now marked
  unavailable: dimmed, "Unavailable" in the duration column, no play button, and hidden by the playable-only filter.
- **A failed load left the player stuck until another song was played.** Clicking the dead row now retries it, and
  Previous / Next stay available after an error so you can skip past it.
- **Playback stopped at a dead row.** Next / Previous and the natural advance now skip unplayable rows, a song that
  fails to load during an automatic advance skips to the next playable one (at most three in a row), and the queue
  labels such rows "Unavailable" instead of showing loading bars.
- **Music videos stuttered a few seconds in, and again every time the pop-out was reopened.** The video picked a low
  quality from a pessimistic guess, then rebuilt itself twice as it measured the real speed. It now measures fast
  connections correctly, never changes quality on a guess, and remembers the last measured speed across launches to
  pick the starting quality.
- **A track with a music video didn't always show the video badge.** Playlist tracks Spotify lists under an older id
  never showed it, a cached track could lose it for good, and the Classic track list never drew it. All three show it
  now.
- **The player bar kept an empty hole where the video button would be.** The video button's space now exists only
  while the current track has a video; volume, lyrics and the rest take its width otherwise, and the bar eases between
  the two widths.
- **Scrolling stuttered while covers were downloading.** With covers and colours streaming in, track rows, cards,
  tooltips, section headers, the sidebar, the playlist page and the lyrics rows re-rendered on every scroll frame.
  Updates are now paced while a scroll is live, and each of these redraws only when a value it actually shows changes.
- **Track rows rebuilt themselves on every recycle.** A row reused during a fling rebuilt its whole contents, up to
  40 MB of garbage in one fling on a long playlist; each row is now built once and only its values change.
- **Cover uploads hitched the frame.** A downloaded cover was copied again before it reached the GPU, through a freshly
  created upload buffer each time. It now goes straight to the GPU, upload buffers are reused and budgeted per frame,
  covers for rows just off screen are fetched at a lower priority, and the cover cache on disk trims as designed.
- **Cover downloads churned shared memory.** Download buffers are sized from the response and reused from a dedicated
  pool.
- **Storage › Delete old logs deleted the file Wavee was writing to.** It matched today's `wavee-<date>.log` along with
  the old ones, so the folder emptied until the next roll, past sessions were gone and a crash report's log tail came
  up empty. It now keeps the file Wavee is writing to, as its confirmation always said.
- **A crash report's log tail could be yesterday's file and miss the lines that led up to the crash.** The tail is now
  the day's file at the moment of the crash, read after the log queue is flushed, so it ends with those lines.
- **The log viewer's session picker looked empty without saying why.** It now says when it is reading, when there are
  no past sessions on disk, and when the log folder couldn't be read, and every walk of the folder logs one
  `log.sessions.listed` line.
- **Export session on the live log exported only the visible rows, without timestamps.** It now writes the whole
  session with timestamps whatever the filter, and a past session's footer says how much of it is shown ("last 4,096
  of 17,936 events").
- **A deep link that arrived with a trailing NUL carried it into route ids and log lines.** The NUL is trimmed before
  the link is parsed.
- **Diagnostics flagged handled playlist and collection updates as ignored.** The flight recorder marked every
  `hm://playlist/…` and `hm://collection/…` push as an ignored frame although the library applied it — hundreds of
  false ambers a day in the anomaly list. A frame is now marked ignored only when nothing took it, with whether that
  was on purpose. (#163)

### Known limitations

- **The API console is gone from Developer mode.**
- **Library edits made while offline are not replayed later.** 0.2 kept a queue of failed library changes and sent
  them at the next sign-in; 0.3 doesn't.
- **The output-device list only remembers your pick.** Audio follows the Windows default output device.

### Developer

- **`Entities.Invalidate` / `InvalidateEdge` — the planner's fifth mark, `Stale`.** A known group can now be declared
  out of date without blanking it: the row keeps rendering, the planner re-asks through the ordinary demand path, the
  next answer at any authority lands, and the mark clears itself. The daylist rollover is its first user. (#162)
- **Scroll diagnostics.** Every wheel/drag burst writes a `scroll.burst` line (the engine's own per-burst verdict,
  written while the scroll probe is at Summary or Trace), and Diagnostics gains a Scroll card: probe level, feel
  profile and a CSV export to `logs/scroll-<timestamp>.csv`, both settings persisted.
- **`frame.slack`.** When the loop did not run for more than 12 ms while a scroll was live, a `frame.slack` line now
  says whether the gap was a GC pause, the wake model sleeping or pre-emption, so a hole mid-drag names itself instead
  of showing up as an unattributed long frame.
- **`scroll.frames` names the sidebar's re-plans.** Every wheel/drag burst's `scroll.frames` line also says which
  planner input made the sidebar re-plan during the burst (`sidebarReplans= railBumps= causes=`).
- **`nav.frames` reported idle refreshes as missed vblanks.** The counter counts every refresh between two presents, so
  a page the user was reading logged "missed=301"; the navigation rollup keeps only the figures that count frames that
  ran long, and the scroll rollup keeps the cadence counter where the loop is continuously live.
- **`dotnet build Wavee.slnx -c Release` shipped the Debug engine in the app's output.** MSBuild unsets the
  configuration for project references that live outside the solution, so the engine built as Debug and its
  assemblies were copied into `bin\Release`; that engine runs a full-scene parity audit every frame (two thirds of all
  CPU, about 500 MB/s of allocation) and made every JIT run look pathologically slow. References now keep the parent
  configuration, and the startup log names the engine flavor (`engine=release|diag`).
- **Connect put-state warning.** Every accepted put-state whose echo does not name Wavee as the active device now logs
  a warning, so a lost Connect state can be traced.
- **Dutch and Korean crash and consent text are drafts.** The crash prompt, the recovery window, the saved-reports list, the
  privacy settings and the setup wizard's crash-reporting card have Dutch (nl) and Korean (ko-KR) drafts; both
  languages stay disabled in the language picker until their tables are reviewed. (#165)
- **`--stress-audio` load test.** `Wavee.exe --stress-audio --file <track> --burners 2 --memory-mib 2048 --minimize
  --seconds 60` plays through the real audio device while spin threads at every priority class, memory churn, a
  minimized window and (with `--battery-saver`) execution-speed throttling lean on the machine, then prints the glitch
  ledger and exits 0 only when there were no dropouts (`--allow-one` tolerates one), so "does it halt under heavy use"
  has a number instead of an opinion. (#167)

## [0.2.10] - 2026-09-12

Idle GPU and memory on the Snapdragon. Wavee sat at 50-68 % GPU and 520 MB-1 GB of working set with nothing on
screen moving, and the investigation behind this section is in `docs/plans/wavee/gpu-memory-investigation-2026-09-11.md`.
Almost all of it is engine work; the app side is the memory governor, the response buffers and the instruments.

### Fixed

- **Cached album pages could remain empty after startup.** An incomplete page opened before the live backend was
  ready now retries its normal catalog load when authentication finishes, preserving the cached header. (#118)
- **Playback did work for hidden meters and neutral effects.** Level analysis now follows visible meter demand,
  unity DSP passes skip their sample loops, and audio management sleeps until work arrives. Constant non-unity
  gain uses SIMD without changing ramps or PCM values. Snapshot text measurements no longer reserve a large
  payload for every non-text scene slot. (#118)
- **Artwork could flash during playback.** Image reveals now use shared wall time instead of the animation
  clock's clamped/resynced delta, so sparse UI publications cannot rewind a completed fade. (#118)
- **Paused immersive lyrics kept repainting the background.** The decorative drift timer now stops while paused
  and resumes from its held phase. (#118)
- **Pause could leave lyrics animating after audio stopped.** A late host position report can no longer overwrite
  pause intent while the asynchronous audio pause is still queued. Seek-bar position updates also avoid rebuilding
  its component tree. (#118)
- **Lyrics and scrolling triggered unnecessary full redraws.** Verified unchanged publications now preserve
  submission continuity, and off-window damage no longer counts toward the full-redraw cutoff. (#118)
- **An exit animation could disable its own cleanup backstop.** With animations owned by the render thread,
  an orphan could not request the UI frame needed to reclaim it. Ready or expired orphans now wake cleanup,
  and an otherwise idle window waits no longer than the existing two-second backstop. (#118)
- **Lyrics playback repainted static parts of the page.** The Liked Songs heart clip no longer rejects partial
  repaint when its mask and rendering layers are separate. Consumed damage now expires independently from new
  descendant updates, and an older snapshot cannot hold the damage history open after navigation. (#118)
- **The render loop never went idle.** Leaving a page while its scrollbar was still inside the two-second idle-hide
  after a scroll froze that row forever: it was skipped before the liveness check so it was never dropped, it was
  counted as needing a frame anyway, and the ticker's own parked-set was never cleared when the page was evicted —
  so a recycled node index inherited it and froze a *new* page's scrollbar too. The loop held at panel rate with
  nothing animating, for the rest of the session. Parking now lands the bar at rest and retires its row. A bar that
  was visible when you navigate away is gone when you come back, rather than resuming mid-fade. (#118)
- **Open lyrics held the UI thread at panel rate through every instrumental gap.** The per-frame stepper mounted on
  "is playing", so it ran for the whole of a track whether or not anything on the surface was moving: an instrumental
  intro with the panel open measured 4146 frames in 30 s, 4085 of them recorded and thrown away, to render 61. It now
  mounts only while a lane is actually in flight — a line being sung, a glow cross-fade, the σ ramp, a handoff
  cascade, the interlude dots, an owed follow landing or the detached/resync countdown — and an idle surface re-arms
  from a one-shot timer at the next syllable instead of polling for it. Nothing is lost across the gap: the wipe, the
  glow and the dots are read from the media clock rather than accumulated, so the first frame back lands exactly where
  it would have. An intro, a break between verses, an outro, a track with no timed lyrics at all, and a paused surface
  now cost the loop nothing. (#118)
- **Every awake frame repainted the whole window.** An animated node marked every ancestor up to the root as moved,
  and the renderer damages a moved node's subtree bounds — which at the root is the window. One scrolling title
  therefore repainted every pixel, at panel rate, against the renderer's own promise that "a spinner repaints a tiny
  region". A pose now damages only the node that actually moved: measured coverage for a small animated leaf goes
  from 100 % to 0.53 %. A row whose value did not change damages nothing at all, so a 60 Hz animation on a 120 Hz
  panel stops paying for the frames in between — and those frames now skip the scene recording as well as the
  present. (#118)
- **Two things moving in different corners forced a full repaint.** The partial-repaint path was capped at a single
  rectangle whenever anything on screen was mid-fade, so a scrolling title near the top and a playhead at the bottom
  were merged into one rectangle covering most of the window, which then failed the coverage test and repainted
  everything. They stay separate now. (#118)
- **A fading edge disabled partial repaint entirely.** Every list in Wavee has a soft fade at its edges, and that
  alone made a frame ineligible no matter how little of it had changed — so in practice almost every frame repainted
  the whole window. (#118)
- **A blur on screen did the same thing, for the whole of playback.** The lyrics surface softens the lines around the
  one you are on, and any blur at all disqualified the frame from partial repaint — so with lyrics open, every frame
  repainted every pixel at the panel's rate while a single wiping line was all that had actually changed. That was
  the difference between 69 % of the GPU and a strip. A blurred surface now repaints the strip: the renderer draws a
  blurred group's source slightly wider than the damaged region so the blur has real pixels to read at the edges,
  redraws only the damaged part, and grows a moved thing's damage by the reach of any blur above it so nothing stale
  is left ringing it. Verified pixel-for-pixel against a full redraw on the Snapdragon's GPU. (#118)
- **The weak-GPU memory budgets never applied.** The adapter was classified *after* the image pipeline had already
  been sized, and an unclassified adapter counts as a discrete one, so every UMA machine — the Snapdragon included —
  silently ran with desktop-GPU budgets: 32 MB of pixel pool instead of 16, 64 MB of image cache instead of 24,
  16 MB of blur cache instead of 8. (#118)
- **Album art was budgeted at the wrong size.** The cache counted decoded pixels while the GPU commits a rounded-up
  square texture, so a 150-pixel cover was counted as 90 KB and actually held 262 KB. The cache believed it was
  inside its cap while holding roughly three and a half times it. Covers are also now released when you navigate
  away from a page, instead of only when some unrelated image finished loading. (#118)
- **A video kept its decoder alive for the whole session.** Playing one video left a second graphics device, a Media
  Foundation engine and its decode surfaces resident until the app closed — none of it visible to any memory
  counter, because the app only ever receives a handle to it. It is now released after the video has been idle for
  half a minute, while skipping between tracks still costs nothing. (#118)
- **The memory governor could never fire.** It read whole-machine memory pressure, which on a 16-32 GB laptop stays
  near zero while Wavee holds a gigabyte, and nothing was registered at the level it sheds at when pressure is
  normal — so the thirty-second check freed nothing, ever, on either count. (#118)
- **A playlist could sit on placeholder rows forever.** Opening one from a link whose address carried an encoded
  trailing space sent that space all the way to Spotify, which rejected the request — and because the track list had
  no way to say "this failed", it went on shimmering as though the songs were still on their way. Nine minutes of it
  in one session, over a playlist that had opened perfectly a few minutes earlier. Link addresses are now trimmed
  after they are decoded rather than before, and a fetch that dies says so, with a Retry, instead of pretending to
  load. A list that already has songs never blanks itself into an error if a later refresh fails.
- **A pending tooltip kept the UI thread awake.** The tooltip's show delay, its five-second dwell and the menu and
  command-bar cascade timers were an invisible animation polled from a per-frame re-render, so hovering anything while
  scrolling pinned the loop at panel rate: one twelve-second scroll counted several hundred needless tooltip renders
  and 463 frames held awake by that poller alone. They are now one-shot host timers that fire once and never
  re-render; the delays are unchanged. (#118)
- **A fading edge leased a 56 MB scratch for a few strips.** The edge-fade snapshot stacked its four edge strips
  vertically at the widest strip's width, so a full-window fade asked for a 1792×8192 surface to hold under a
  tenth of that area. The strips now pack side by side and the same fade leases 28 MB. (#118)
- **A scrollbar's hover dwell kept the loop awake.** The conscious scrollbar counted its 400 and 500 ms expand and
  contract dwells by re-rendering a stepper every frame, so every hover and every scroll bought a run of frames in
  which nothing moved. The dwell is now a one-shot host timer; only the 167 ms width tween and a held page-repeat
  still step per frame. (#118)

### Changed

- **Large responses are no longer copied twice.** Each catalog response was buffered and then copied again, and
  compressed bodies were copied a third time purely to satisfy a constructor — every one of them on the large-object
  heap, which nothing in the app ever compacts. (#118)
- **The diagnostics can now see GPU memory the engine does not own.** `mem.sample` reports the adapter's own
  per-process figure beside the tracked total, so the difference — Media Foundation's surfaces, the driver's arenas,
  shader compilation — is a named number instead of an unexplained gap. Image memory is also broken down per texture
  size rather than collapsed into one row, and frame repaint coverage is reported as a percentage, since every
  successful partial frame used to round to "0.0". (#118)
- **The frame log now names what is keeping the loop awake.** When something subscribes to the frame clock the loop
  runs at panel rate for as long as it stays subscribed, and the log could only say *that* one had — never which one,
  which turned a one-line answer into an afternoon of guessing. The thirty-second line now ends with the count and
  the components themselves (`pollers=1:LyricsFrameStepper`), and a gate holds both halves: that the names are right,
  and that the count falls back to zero when they unmount.

## [0.2.9] - 2026-09-11

The engine underneath is FluentGpu's "Operation ultra-fast" work, and much of the app side is what that engine
exposed — retained pages, per-frame component churn, art slots that never repainted. On top of it: the Now Playing
player styles, one authority for who owns Spotify Connect playback, lyrics that sweep and a blur you can turn down,
a pop-out video window you can move again, and a pass over every flicker, jump and colour flash a frame-by-frame
look at the app found. It also lands the always-on frame instrumentation, without which none of it could be
measured.

### Added

- **Appearance › Lyrics blur.** A strength slider (0–100 %) for the lyrics depth-of-field and glow blur; 0 removes
  every lyrics blur layer. Auto picks 40 % on a weak GPU tier (the Snapdragon's Adreno) and 100 % elsewhere. The
  depth-of-field ramp now writes in the renderer's own 0.5 σ buckets, and the active line's glow never nests inside a
  blurred layer, so a line hand-off no longer re-blurs the voice row every frame. Moving the slider applies at once,
  playing or paused, instead of at the next line. (#141)
- **The track expander shows who added the track.** "Added by" carries the same avatar + name chip as the Added-by
  column instead of a bare name. (#135)
- **Sidebar pins sync with Spotify.** The Pinned section mirrors the account's own pins (Spotify's `ylpin` set):
  playlists, albums, artists, shows, Liked Songs and playlist folders,
  read on sign-in and on every push, and written back when you pin or unpin in Wavee. Liked Songs uses the spelling
  Spotify actually sends (`spotify:collection`), a folder syncs by its rootlist id and renders disabled with a reason
  until its rootlist entry arrives, and pins Wavee cannot show (Your Episodes, Local Files, audiobooks) are left
  untouched on the server. A pinned item no longer appears a second time in Playlists, Your Library, the Library V3
  list or the rail — in every design, and a Liked Songs pin that arrives from Spotify is a proper route pin:
  titled, clickable and the same height as its neighbours. In the collapsed rail a pinned folder is a folder tile
  that opens the folder, not a playlist cover that does nothing. (#102)
- **Player styles for Now Playing.** The Now Playing rail's cover gets a header with a Cover | Player switch and a Player style flyout: twelve
  players in three rows — Record, Cassette, Reel-to-reel, CD/MiniDisc; Turntable, iPod Classic, Winamp, Hi-fi VU; Zune, WMP visualizer, Canvas
  drift, Picture disc — each with its own options (vinyl finish, size and speed, cassette shell and label, iPod body, Winamp skin, …). The
  record's tonearm cues, lifts on pause, seeks, rides the run-out into a locked groove and returns to its rest the way a turntable does. The
  choice is remembered per user and is also reachable from the artwork's right-click menu, Settings › Appearance › Now playing and the command
  palette. (#142)

### Changed

- **Sidebar shortcut rows are 40 DIP, and the Shortcuts band has no header.** Glyph rows (Your Library, DevTools,
  any Curated shortcut section) used the subtitle pitch of 44 while never showing a subtitle, so they read taller
  than the pinned rows beside them; they now take the ladder's Cozy-without-subtitle height in the pane and the
  rail. Home sits directly under the title bar; the quick layout menu moved to the Pinned header. (#134)
- **The app measures its own frames.** `frame.slow` writes one line per frame over the panel's refresh interval
  — rate-limited but counted — with the phase split, GC deltas and, when the render census fired, which
  components rendered and which allocated. `nav.frames` rolls the four seconds after a route change up into fps,
  counts over budget / 33 ms / 100 ms, missed vblanks, time to first frame and the worst frame; `scroll.frames`
  does the same for a wheel or drag burst; `mem.sample` records the working set at each window close and at
  process end. The census is on unconditionally: a slow frame that cannot be attributed is a slow frame that does
  not get fixed. `ops/tools` gains the readers — perf-tour, perf-tour-analysis, nav-measure, nav-live-check,
  scroll-measure, perf-profile and perf-ws-attribution.
- Startup lines read as a timeline: `WaveeLog` anchors `SinceStartMs` to the real process start, and a Core or
  Window activation step that exceeds the 8.3 ms frame budget logs at Warning instead of passing unremarked.
- **The shell keeps three pages alive instead of eight.** A parked page holds its whole element tree and signal
  graph, so eight of them was most of a session's navigation history resident at once — over one perf tour that
  took scene nodes from 494 to 15668 and live components from 93 to 1479, with the working set never coming back
  down. Three is the live page plus a two-deep back stack. Parked pages already release their image pins, so a
  deeper back-navigation costs one rebuild, never a re-download.
- The player's elapsed-time label no longer re-renders its component on every playback tick. Reading the playhead
  in `Render` subscribed the whole component — box, hover and pressed fills, click handler and caption — to a
  signal that moves at tick rate, to produce a string that changes once a second, and two of these are mounted at
  all times. The label is now a bound channel: a tick writes one text value and nothing re-renders.
- Every shelf passes its collection rather than a count, following the engine's retained-shelf rework (21 call
  sites across 9 files). A shelf now does its own capping, so a card callback can no longer be handed an index
  past the end. Mechanical port: no shelf changes its reserved height, page size or measurement mode.
- Ambient motion is paced per source (`DefaultLoopHz`), not by a host-inferred cap.
- Scrolling and pointer motion feel closer to the finger: the present queue no longer holds a finished frame
  back a whole refresh, and the render loop picks the frame to show after the present slot opens rather than
  before. Roughly one to one and a half refreshes less latency at the same frame rate.

### Fixed

- **The render loop never went idle, and the GPU sat at ~70 % with the Lyrics panel open.** A pointer resting over a
  scrollable view held a scrollbar row forever, parked scroll bodies stayed "active", and layout re-posted an
  identical scroll frame every frame — each alone enough to keep every frame awake, and an awake frame re-submits the
  scene whenever a pixel moves. The engine now separates "needs a frame" from "has a row", ignores parked bodies for
  wake, posts a scroll frame only when it changed, names the term in its `[wake]` line, and reports the real
  `blurHeld` count. (#136)
- **Lyrics: Musixmatch's anti-scraping decoy could win.** 206 uniform four-second nonsense lines running to 13:56 on
  a 3:30 track were chosen because an ISRC match was verified by construction, tier beat score, and a better provider
  could never replace the first winner. A document longer than the track or with uniform line timing is rejected as a
  decoy, a verified candidate must align with the reference text, the background pass replaces an unverified winner,
  and Musixmatch's own status code (captcha / quota) is honoured with a token refresh. (#137)
- **The karaoke wipe stepped, even with lyrics blur at 0.** Lyrics extrapolated media time from the 15.6 ms system
  tick and applied every position sample as a jump, so at 120 Hz the wipe advanced 0 or 15.6 ms a frame and froze
  after a backward correction; and the engine's render-thread capture skipped a node written every frame until the
  next full capture, about five times a second. Lyrics now extrapolate from timestamped position samples to the
  frame's own present time and correct drift by a bounded rate change, never a step; the capture records every write.
  The record player's clock reads the same frame time. `lyrics.clock` logs zero-advance frames and the largest step
  every 30 s. (#143)
- Lyrics no longer open with the provider's title/credit header when it carries a bracketed annotation or a
  trailing "title - artist" line (Kugou's Chinese credit block). (#144)
- **The pop-out video window could not be moved.** It lost its title bar when it became chromeless and nothing took
  the title bar's place. Dragging the picture now moves the window through Windows' own move loop (Aero Snap, shake
  and the snap bar included); a click, double-click or right-click keeps its meaning and a press on a control never
  moves the window. (#145)
- **Video controls hid and showed like a flickering overlay.** Only real user activity shows them now — a slow mouse
  creep counts, a resting hand's jitter does not; buffering, a quality switch or a new track never pop them up;
  hovering them, an open menu, seeking or a pause keeps them; leaving the window hides them within half a second.
  They fade in 150 ms and out 400 ms from wherever they are (the fade used to start at its end value, so it cut), the
  seek bar keeps its colour and thumb while it fades, and in the pop-out the cursor hides with them. Main-window
  fullscreen controls respond to the mouse again — an invisible click shield sat on top of them. (#146)
- **Navigation no longer flashes the window's colour.** The chrome tint (sidebar, title bar, player bar) dropped to a
  darker neutral for a few frames at almost every navigation and sometimes jumped back to the previous page's colour:
  a page cleared the tint when it left, the next page's colour was not graded yet, "no colour" was painted as
  transparent black, and a page still fading out could republish over its successor. Pages now hand the tint over —
  the incoming page claims it and keeps the current colour until its own is ready, only the current owner may
  refresh it, pages with no colour of their own ease to a real neutral, and a clicked card's already-graded colour
  seeds the destination. (#147)
- **Page transitions no longer superimpose two pages.** The outgoing page faded on an accelerate curve and was still
  ~70 % opaque when the incoming one started, so every navigation showed a frame of mixed, unreadable text; it now
  fades out fast first, and the incoming page slides 8 DIP instead of 30. (#148)
- **Late data no longer moves what you are reading.** The player bar changes track in one step — no old artwork, no
  old position against the new duration, no Play glyph flicker, and a reserved slot for the video button; its title
  no longer ping-pong scrolls while idle. An album page no longer shows the "Minified album view" notice or a wrong
  total duration while its rows load, and "About this release" appears once, complete. Artist pages keep one
  play-count format and never re-create the Top tracks rows when the column width settles. A daylist's countdown
  ticks. A playlist or mix opened after it changed waits briefly for the fresh copy instead of painting yesterday's
  and swapping it, and its rows only animate for real edits. Sidebar clicks hand the page a preview like Home cards
  do. Search and Browse skeletons have the shape of the page they stand in for. (#149)
- What's new: the highlight cards are the same translucent cards as the rest of the page instead of a darker opaque
  grey, the viewer's page dots are centred under the picture, and stepping between highlights no longer makes the
  viewer grow and snap back — it keeps the tallest highlight's size and cross-fades the text in place.
- **Less motion that was not asked for.** Wide track rows no longer shrink when pressed, cards under a resting
  pointer no longer grow right after navigating back, the sidebar equaliser honours reduced motion, and a lyric line's
  hand-off dims and brightens at the same pace. (#150)
- **Connect: who owns playback is decided in one place.** Six incidents had one cause — two ownership authorities
  (the controller's own flag and active-device transitions, the projection's raw active id with a 5 s wall-clock
  grace) and several writers that told Spotify `is_active=true` without asking who owns playback. While a phone
  played, Wavee restarted its last track every ten seconds at the phone's position and stopped it again as "stray";
  picking "This computer" resumed Wavee's stale session at the phone's playhead while the bar still said "Playing on
  iPhone", and the play button then went to the phone (403); a launch announced Wavee active over a playing phone;
  the transport showed Play while audio played; a takeover that arrives as an empty frame and then the phone's was
  read as a stray. Ownership is now one state — this device, another device, or nobody — folded from the server's own
  timestamps, with the server's answer to our claim as the verdict; routing, the audio host, now playing and the wire
  all read it. Picking this computer while another device plays asks Spotify to transfer playback here, a claim that
  loses is stopped at once, and a click queued behind a takeover is dropped instead of pulling playback back.
  Settings › Playback links a Connect diagnostics page, and `connect.owner`, `connect.cluster`, `connect.echo` and
  `playback.load` lines log every decision. (#138)
- **The player bar repeated a line: "LP / LP", "Damiano David / Damiano David", or the title where the artist
  belongs.** When "Playing on <device>" appeared above the title — or went away — the title and artist lines had no
  identity of their own and the engine matched them by position, so one slot reused the other line (frozen at mount).
  Both lines are keyed now, and the engine matches unkeyed siblings by their order among unkeyed siblings, so a keyed
  line inserted or removed in front no longer shifts them. The track metadata was never wrong. (#107, #139)
- **Video: the previous video's duration was adopted for the new track on a host switch**, so the seek bar jumped.
  Duration events are accepted only for the host's current source. (#140)
- **Storage & cache cards overlapped on narrow windows.** The per-location cards kept their intrinsic width and,
  once a description wrapped, painted under the next card: their accent-bar wrapper was a row, so the card never
  stretched and its wrapped height never reached the layout. It is a column now. (#132)
- **Collaborators showed raw user ids.** The face pile captured the playlist model at mount and never read the
  re-mapped model that carries the hydrated profiles, while the Added-by column beside it did. It now reads the
  page's live model, like the owner row. (#133)
- **Command-line probes were blind in Release builds.** `--spotify-collection`, `--spotify-login` and the other
  `--spotify-*` / `--backend-selftest` / `--qr-dump` / `--connect-live` arms logged only to the daily file, exited
  before the log queue drained, and — worst — a probe whose stored login Spotify rejected wiped the app's own signed-in
  credential. They now echo to the terminal, flush before exiting, print `added_at` per collection item, and never
  clear the credential; `.claude/skills/wavee/probes.md` says how to run them. (#131)
- **Syllable-synced (karaoke) lyrics played a whole track with no wipe and no held-note glow, then worked on the
  next play.** A lyric row freezes its line at mount, and the upgrade gate compared the per-line text alone — so
  when the aggregator answered with Spotify's line-synced transcription first and published the word-synced
  document a second later with byte-identical text (`text=1.00 lcs=69/69` in one capture), the rows were kept
  holding a line that had no syllables at all. The next play worked only because the disk cache then held the
  word-synced document. Keeping the rows now requires every field a row actually freezes to match: the text, the
  word timing, and both secondary layers. (#125)
- The profile chip and flyout header rendered the avatar's URL as text inside the circle instead of showing the
  picture — the photo was passed where the control expects initials, which outrank everything but a group icon.
  An account with no photo still falls through to its generated initials. (#111)
- A rename or a new profile picture made anywhere else — the web player, the phone, the account page — now
  reaches a running client instead of waiting for the next sign-in. Spotify pushes these over the dealer and
  nothing subscribed, so every one was logged unhandled. The apply is coalesced, because the service pushes per
  keystroke: the first push lands immediately and the rest collapse into one trailing apply, so the chip never
  spells out a half-typed name. It also updates playlist owners, collaborator piles and the added-by column. A
  push carrying no image updates the name and leaves the picture standing — several pushes during a rename have
  no image yet, and assigning them wholesale would blank the avatar for the length of the rename. (#126)
- Art slots that paint their neutral tile directly — sidebar rows and pins, every artwork grid cell, the mosaic
  cells — stayed flat grey no matter how correctly the cover plane graded them. The placeholder colour was
  evaluated once at mount and frozen; a landed grading now repaints exactly that tile, never a component
  re-render and never a global fan-out. (#127)
- Play and pause no longer tear down and rebuild the entire equalizer subtree. The element keyed itself on the
  animating flag, so a transition that should only start or stop an animation changed the key and made the
  reconciler rebuild everything under it. (#128)
- **Plugging headphones in or out while "Default" is the output device no longer silences playback until the next
  track.** When the new endpoint was not ready yet — or the old one was invalidated by the jack switch — the engine
  adopted a dead sink and then asked itself to rebuild every 80 ms, which kept postponing its own 250 ms debounce
  forever. A sink failure now starts a rebuild without postponing it, a device that is not ready is retried at
  250 ms / 1 s / 3 s while the previous one keeps playing, a failed device open is logged with the step and HRESULT,
  and a device-format change that cannot be reopened in place keeps the track audible or offers Retry instead of
  going quiet. (#112)
- **Resuming a track at its saved position no longer hangs on an endless buffering bar.** The launch restore (and a
  video-to-audio swap mid-track) seeks to the saved position on the same serialized queue that attaches the encrypted
  body a moment later; the new engine's seek waits for decoded audio at the target, the fast-start session held only
  the clear head, so the seek blocked the queue waiting for the very attach queued behind it. A seek that arrives
  before the body is attached is now parked and applied the instant the body lands, and the player shows the
  restored position instead of 0:00 while it waits.
- The editable playlist title no longer runs the engine's shrink-to-fit search against a width it is never drawn
  at. The hover pill's title declared no width of its own, so layout measured it at the row's leftover and again at
  its arranged width — two cache keys, up to two eight-probe searches per invalidation — and fitted it to a measure
  28 DIP narrower than the type plan solved for, so an owned playlist could snap one size smaller than the same title
  read-only. It now declares the exact width it gets: one key, one search, both arms agree. (#92)
- Decode-ahead runs on a dedicated above-normal-priority thread again. The engine's per-voice producer had moved to
  a normal-priority task, so on a busy machine (a compiler, an indexer) the decoder lost its scheduling edge and
  playback stalled to rebuffer; the `xrun` log line's `ageMs` field, which always printed 0, now reports the real age.
- A detail page's track list could re-render every mounted row for no reason: the row shape is a record struct
  holding an array, and a record struct compares an array by reference, so an otherwise identical shape compared
  unequal whenever the backing array instance changed. It is compared by value now. The array is usually
  reference-stable today, so this closes a latent bug rather than winning back a measurable cost — it matters
  when the cache misses, which is exactly when the list is already busy.

## [0.2.8] - 2026-09-04

### Fixed

- Album pages no longer sit on blank rows with a nonsense running time ("31 songs · 6 min") until something else
  happens to repair them. `getAlbum`'s named tracklist was being discarded by an adoption gate that compared row
  COUNTS only, so a resident list of gid-only AlbumV4 rows tied the named list that arrived and won — the album
  then sealed at its rung and every later open short-circuited before the repair or the thin-row tripwire could
  run. Equal length is now decided on quality: the resident list only wins a tie when it is no thinner than what
  landed. (#90)
- The detail hero no longer leaves a band of dead space under the action row. The identity column is pinned to the
  cover's edge and the slack is spread across its own gaps instead of pooling in one hole, so the actions and the
  blurb under them land on the cover's baseline while the cover's top stays on the title's. (#78)
- The hero title is sized from the space AND the title itself instead of a six-step ladder keyed on width alone: a
  short name grows to fill the height the cover sets, a long one takes a second line rather than ellipsizing, and
  the authored line height no longer fights the face's natural line box (which the engine was silently using
  anyway, discarding every paired value above 20). (#79)
- The narrow detail hero puts the cover beside the copy from 424 DIP instead of 540, so a ~470-DIP page stops
  wasting ~157 DIP to the right of a 280-DIP cover and stops pushing the first track ~280 DIP down the page. (#80)
- The artist hero is 452 DIP at Compact and 476 at Narrow, down from 644 and 628 — the name caps at two lines, the
  stats go back on one row, and the copy sits at the bottom of its band instead of floating centred in it, so Top
  tracks is no longer entirely below the fold. (#81)
- Clicking a Mixview neighbour re-centres the graph and moves the surrounding pane with it. The ring was rebuilt
  from data captured at mount — `Responsive.Of` freezes its build closure — so the click wrote a signal nothing
  read. (#83)
- The top bar's search box holds still when the tab title changes width. The merged row centres between its two
  clusters rather than in the window, so a 90-DIP tab swing moved the box by up to 45; the tab lane now reserves a
  quantised width, the pin button keeps its slot when it declines, and the profile name is capped. (#88)
- The What's New viewer dims the whole window again and shows its caption. The overlay root shrink-wrapped to the
  plate, so the veil covered only the plate's own box, and the caption's scroll viewport measured zero tall and
  was clipped away entirely. (#89)
- Navigation no longer fills in behind itself. Two deliberate engine render budgets — a cold list growing 4 rows a
  frame, and a returning page replaying its parked render debt 24 components a frame — were the reason a page kept
  assembling after it arrived. Both are off; a page realizes in the flush that opens it.
- The Library V3 destination rail shows which destination you are on (its accent underline had no width and never
  drew), starts on the same left edge as the rows beneath it, and its words are reachable at a narrow pane through
  hover chevrons with fades that only appear on a side that actually has more to show.
- Playlist save counts read as counts. "18713647 saves" is now compact, through the same formatter the Plays column
  uses, and the meta line wraps to a second line instead of ellipsising the duration away.
- The What's New viewer's page dots sit under the middle of the image instead of at its leading edge.
- The player bar's indeterminate seek sweep spans the bar. It was a hard 2400-DIP literal, so on a 3440-DIP
  ultrawide it stopped 1040 DIP short. (#94)
- Settings' Zoom row no longer shows a stale percentage. The chords, the Ctrl+wheel hook and the palette all change
  the live zoom without touching the settings store, and nothing subscribed to the engine's own change event — so
  zooming with the page open left the picker reading whatever it read at mount, with the app visibly at 200 % over a
  control still saying 100 %. (#93)
- Overlay veils no longer tint under the cursor and flash on press. A full-bleed dismiss surface carries an
  `OnClick`, so the recorder interpolated it toward an unset hover brush — the engine's own scrim pins both, and
  Wavee's did not. (#91)

### Changed

- **Wavee sizes itself to the display.** The zoom is no longer a monitor-blind number seeded at 100 %: on a large
  screen it derives its own default from the window's DIP extent, so a 3440-DIP ultrawide opens at 150 % instead of
  spending twice the design width on the same fixed constants. It takes the smaller of the two axis ratios, so it can
  never buy size by giving up structure, and it snaps to the 100/125/150/175/200 % plateaus where Windows' 4-epx rule
  lands on whole pixels. A manual pick or a Ctrl+± still wins and pins the mode to Manual, and an install that already
  had a zoom set keeps it. (#94)
- Album art decodes at the size it is painted. The decode budgets were raw DIP literals, so at 150 % zoom every cover
  was decoded at two-thirds of the resolution it was drawn at. (#94)
- Library V3 can reach Liked Songs, Albums, Artists, Podcasts and Local files. They were absent from the design
  entirely. They arrive as a row of words under Home — not five stacked rows and not icon-only tiles, both of
  which were tried and rejected: labels stay visible at every pane width, the active word carries the count and a
  2-DIP accent underline, and the rail scrolls with the last word peeking past a fade. The collapsed 56-DIP rail
  carries them too. Pinned rows now show a pin marker, the "+" accepts drops, and a refusal to reorder under a
  sorted lens offers to switch to Custom order. (#85)
- The sidebar resizes down to 180 DIP instead of stopping at 240: grid strips shed a column rather than shrink
  their cells, section titles ellipsize instead of pushing, and the splitter still resists and dims from 240 down
  before it snaps to the rail. (#84)
- "Your top artists" fills the width it is given — the avatar ramp is solved from the measured width instead of
  three hard-coded sizes — and the surface is tightened throughout. Artists are reachable from it at last, by
  context menu or double-click, from both the podium and the Mixview graph. (#82)
- The Concerts and Browse cards on Home have artwork: a procedurally drawn, endlessly drifting panel per card
  rather than an empty grey rectangle, on looping animation tracks at co-prime durations under the 30 Hz ambient
  cap, and reduced motion swaps the keyframes rather than branching. (#86)
- **The detail page's left rail can be one size for every page.** Settings › Appearance › Track page layout, on
  Automatic, gains "Keep left-rail same size": resize the rail once and every album, playlist, Liked Songs and
  podcast page uses it. With it off, each surface keeps its own remembered width as before, and a "Clear all
  remembered sizes" action forgets them.
- The artist pick loses its "Artist pick" heading and keeps its column beside Top tracks — the card already says
  what it is. (#87)

## [0.2.7] - 2026-09-03

### Changed

- **The Library V3 sidebar is rebuilt around one left edge.** Home is fixed chrome above "Your Library" instead of
  a "Shortcuts" section inside the filtered list, so filters and search never hide it and the rail keeps its tile.
  The search field is inline when the pane has room — transparent, borderless, beside the full "Recents" pill — and
  collapses to a magnifier on a narrow pane that morphs open in place, clears on one Escape, closes on a second or
  on an empty blur, and is never remembered across launches. Filter pills sit on a bordered control surface, keep
  their width when selected, use contrast-picked ink on the accent, and picking a kind slides in a round ✕ that
  clears everything; a qualifier fuses into the shared segmented pill with an opaque segment and an ✕. Every row of
  every design now shares one art column: glyph rows get a real art-wide column, the tree's reserved chevron cell is
  gone (the folder chevron moved to the trailing edge), a pinned folder sits flush with its siblings, the
  multi-select lane no longer pushes rootlist rows right, and the header glyph, magnifier and first chip line up with
  the rows' art. (#71)
- **The artist page's album drawer is compact, instant and unmistakable.** 32-DIP rows under a 40-DIP header (was 44
  under 56), two track columns on wide windows so a whole album fits without scrolling, a "Show all" row past 12 / 24
  tracks, a caret pointing at the cover you clicked and that cover in the drawer's header, and the clicked row always
  scrolled to the same spot under the tabs with one 200 ms open — no more skeleton flash for an album you already
  opened, no more previous album's tracks under the new cover, no more drawer hopping between rows while the page
  scrolls after it, no more section-wide reflow on every click. (#77)

### Fixed

- The Library V3 search box no longer cross-fades between two controls, swallows Escape, stays open forever or
  reopens after a relaunch: it is one control that morphs in place, Escape clears then closes, an empty blur closes,
  the ✕ clears, and typing is debounced so a keystroke no longer rebuilds the whole library projection. (#69)
- Library V3 filter pills: the fused "Playlists │ By you" segment is legible again (an opaque segment instead of a
  translucent card on the accent), selected ink follows the live accent's contrast, the label no longer shifts when a
  pill is selected, resting pills have a visible border, the trailing glyph is an ✕ because tapping clears, and a
  leading ✕ clears every filter at once. (#70)
- The sidebar's "Recents" sort follows what you played, not what you clicked: opening a playlist no longer moves it
  to the top, playing one (here or on another device) does, and never-played items keep their added-date order
  below. (#72)
- Sidebar rows share one left edge in every design: glyph rows, pinned covers, playlist covers, folders and the V3
  chrome all start on the same art column, and a rootlist with a folder no longer indents the whole list. (#73)
- **Switching the output device (or its sample rate) mid-track no longer breaks the next track.** The gapless hand-off
  used to be scheduled from the old device's clock, so the next song started up to minutes late while the bar counted
  past the end, the time readout flicked between two values, and the pre-decoded next song played slow and flat at the
  old rate. The join now follows the live session clock, a pre-decoded song is re-prepared for the new rate, and the
  readout is clamped on every path. (#65)
- Row size now scales the cover art with the row: Cozy rows get 40-DIP art and Comfortable rows 48-DIP, instead of a
  32-DIP square floating in a 64-DIP row; the Settings › Appearance density preview follows. Dragging the Row size
  slider no longer bounces between levels (the toolbar button relabelled itself mid-drag and dragged its flyout
  sideways under the pointer), and the thumb tip says Compact / Default / Cozy / Comfortable instead of 0–3. (#66)
- The setup sign-in page no longer cuts off its last line: the page body now really scrolls (and shows its rail) when
  it overflows, the QR code respects its 80-DIP box instead of growing to 111, and the scan card's text fits one
  line. (#67)
- "Report a problem…" could open with Question selected; the requested kind now travels with the request and is part
  of the form's identity, and every open is logged. (#68)
- Selecting an artist (or album/podcast) in Your Library no longer throws the list back to the top and then scrolls
  it back: the navigator keeps its scroll position across every refresh, and a refresh that changes nothing no longer
  rebuilds the list at all. (#74)
- "Recents" in Your Library › Artists / Albums / Podcasts — and in an artist's discography column — now means
  recently *played*: what you listened to most recently comes first (including plays from other devices, via your
  listening history), everything you have not played keeps its old order below. "Recently added" now really is
  newest-added first (it listed the oldest first), and the direction chevron works for every sort. (#75)
- **The album page's "About this release" tiles no longer re-arrange themselves as details arrive.** The block is a
  fixed two-column grid from its first paint — Songs · Length over a full-width release date — the date refines in
  place from year to full date, the label joins the notes below instead of becoming a fourth tile, nothing slides or
  overlaps, and a long date or label can no longer run past its tile (it wraps or ellipsizes inside it). The same
  stat tile now serves the module page, the pre-release countdown and the track facts strip. (#76)

## [0.2.6] - 2026-09-02

### Added

- **Report a problem from inside Wavee.** Settings › About has "Report a problem…" and "Suggest a feature…", the
  About tab lists recent crash reports with a Report button, and after a crash the next launch offers to file
  it. Wavee opens the matching GitHub form with the version, install source, architecture and Windows build filled
  in, and copies a redacted report (personal paths, account details, secrets and addresses removed; track names
  kept) to the clipboard and to a `wavee-report-<date>.txt` file beside the logs for you to paste. The "closed
  unexpectedly" prompt is offered once per run that left no report or dump behind — a process stopped from the IDE
  or Task Manager on every run no longer re-asks after every "Not now" — and "Don't ask again after a crash" now
  silences that evidence-free prompt entirely (a real crash report or dump still surfaces as a quiet toast).
- **A real Logs tab.** Settings › Logs is a full-height log viewer: a command bar (refresh, copy, export, open the
  log folder, clear; newest-first, group repeats, and a Verbose switch that captures Debug/Trace for the running
  session), a search box with level and category filters, rows that expand on a single click to show their fields
  and exception, and a session picker that actually lists your previous runs. (#55)

### Changed

- **Setup is three screens** — welcome & terms, sign in, local playback — instead of seven. The appearance,
  sidebar, sound and notification tour pages are gone; those choices live in Settings with the same defaults the
  wizard used to pick, and "Run setup again" is gone with them. Signing in continues straight to the local playback
  step without an "Is this you?" stop. (#53)
- **Settings is regrouped** into General · Appearance · Playback · Notifications · Storage · Logs · About, and every
  row now carries its own icon instead of repeating its section's. "Disable marquee text" / "Disable color washes"
  are now the on-switches "Marquee text" / "Color washes". (#54)
- **The title bar's "…" menu is gone.** Pin to sidebar, Settings, notifications and Friends are direct buttons in
  the top-right cluster (the notification badge moved from your avatar to a bell), all on the same geometry as the
  theme toggle. (#58)
- **Setup heroes are real animations.** The welcome, sign-in and local-playback pages play the Lottie scenes Rise
  Media Player's setup uses, recoloured to your accent and played the same way (first half once, then hold). (#53)
- **Setup looks like a WinUI dialog.** A 762×490 plate, the Lottie beside a title and plain text with settings
  cards, a progress bar and an Accept/Continue footer — no more hero cards, chips or captions. (#53)
- **Setup opens straight away.** The "Welcome to Wavee · Start setup" splash before the dialog is gone; a first run
  shows the terms page immediately. The sign-in primary is the standard accent button (the Spotify-green one was too
  harsh in dark mode), the local-playback step says "Checking…" on its button while the long status lives on the
  page, and the sign-in page fits the dialog: a smaller QR, one-line card text, and "Wavee needs Spotify Premium ·
  Sign up" on one row. Any setup page that still overflows shows the scrollbar rail. (#53)

### Removed

- The palette picker (Settings and the profile menu), the Mica / Mica Alt choice, "Limit page color to the hero"
  and "Track page layout": Wavee always uses the neutral palette, Mica, the automatic page layout and full-page
  tone. (#54)
- The `WAVEE_LOG_LEVEL` / `WAVEE_LOG_FILE_LEVEL` / `WAVEE_LOG_RING` environment overrides — use Settings › Logs. (#55)

### Fixed

- Setup showed the raw Spotify account id instead of your display name on the account card and the final page. (#51)
- The "Local playback needs a one-time setup" toast and banner no longer pop up on top of the setup wizard, whose
  own local-playback step is that prompt. (#52)
- Report a problem: scrolling the form no longer paints a dark band over the fields under the title. (#56)
- **Dialog text that vanished after scrolling.** In "Report a problem" (and any dialog with a long body), scrolling
  the body down could blank the last checkbox's label and the "Report on GitHub" / "Not now" labels while their
  buttons stayed: the renderer's per-frame text budget filled up on the report preview and silently dropped every
  glyph after it. The budget now grows to what the frame needs and the frame repaints whole. (#56)
- **Text from the previous screen showing through the setup dialog.** Page text underneath an opaque dialog could
  be painted over it after a partial repaint; the renderer now keeps paint order whenever a fill covers earlier
  text. (#53)
- Search: clicking an artist link on the top-result card (or in any result row's subtitle) navigated *and* played the
  song; a link click is now only the link. The top-result card also gained the right-click menu, a "…" button and
  drag-to-playlist that every other result already had. (#57)
- The pop-out video window now fits the video to the window and keeps the transport bar visible at any size,
  instead of pushing it below the fold until the window was stretched. (#59)
- Audio quality now applies to every track, not only ones Wavee had never seen before: the chosen bitrate was
  cached per track for the whole session. The bitrate Wavee picked is now in the log at Info level. (#60)
- Equalizer changes (on/off, preset, dragging a band) apply immediately to the playing track instead of on the next
  track, and the equalizer now also applies to local files, radio and module playback. (#60)
- The title bar said "Sign in" while you were already signed in (the shell mounts before the silent resume
  finishes), and pressing it signed you in as the demo account on top of the real resume. The chip now follows the
  shell's real auth state — Connecting… / Reconnect / Sign in — and Sign in runs the real resume. (#61)
- A track restored paused at launch showed the buffering bar sweeping forever, and its elapsed/remaining readout
  could show a position past the track's own length (e.g. "35:32 / −0:00" on a 2:54 song) — the position clock now
  clamps to `[0, duration]` on every read, paused or playing, instead of only while playing. (#62)
- The profile menu showed your Spotify account id instead of your name when the profile arrived before go-live. (#63)
- Crash reports are written again. The report writer could not read the live log (a sharing violation, swallowed
  silently), so the report was lost and the next launch only knew about the Windows dump; the second handler no longer
  overwrites the first, a failed write is logged, and a crash whose report failed still prompts on the next launch. The
  crash prompt itself now says "Reading the last crash report…" while it reads, stacks its title over its message,
  and shows a taller preview; the redactor no longer rewrites "Wavee" or a GitHub handle inside URLs when they merely
  contain a device or display name. (#64)
- Home could show only the notification feed, a failed Charts card and Concerts/Browse — no shelves, no "nothing
  here yet" either — for a moment right after launch, and the same empty read could also flash a stray "Nothing
  here yet" card over a perfectly normal account. Home now waits for a real feed (or a confirmed-empty one, once
  the live session has actually had its say) before it paints anything — and could stay waiting forever on some
  launches, because the wait only re-checked on a feed-cache bump and the session going live published none of its
  own; Home now also re-reads the instant sign-in actually completes, and force-releases whatever feed it has after
  8 s no matter what, so the skeleton can never hang indefinitely. (#53)
- **Home opened in three jolts**: the cached "Jump back in" grid and library sections appeared while Wavee was still
  connecting, a lone "No charts right now" row painted under them, the new-releases timeline popped in once the session
  went live, and a second later the live feed replaced everything — chips and the hero appeared, the grid dropped
  350 px and every row remounted. Home now keeps its loading shimmer until the session has had its say (live, or
  confirmed offline) and the Charts deck and notification feeds have landed too (capped at 1.5 s), then reveals the
  settled page once; later refreshes swap in place and never re-run the reveal or re-skeletonize a row. Offline, the
  cached shelves still reveal once from cache; the Charts deck no longer shows a failed card offline. (#53)

## [0.2.5] - 2026-09-01

### Fixed

- **Audio could glitch or stutter under disk or CPU load.** Writing a streamed chunk to the local cache used to
  hash it, check free disk space and fsync — all synchronously on the audio decode thread, for every 64 KB chunk.
  That work now runs on a background thread, and a CDN cache miss during playback no longer blocks the decode
  thread on a network fetch either.
- **Read-ahead buffering is now adaptive** instead of a fixed 256 KB window — it grows toward several minutes of
  audio on a fast connection (fewer chances to run dry) and shrinks on a metered one (less data used), based on
  measured throughput and the track's bitrate.
- Audio underruns are now logged individually, with timing and cause, instead of only as a running count.
- **Sign-in no longer blocks the app on every launch.** Wavee now shows your library immediately from the last
  session and reconnects to Spotify quietly behind it; if your credentials were revoked, it still falls back to
  the sign-in screen.
- **Microsoft Store builds** — "Check for updates" was a silent no-op; it now opens the Store listing. The
  after-update "What's new" plate also now appears correctly on Store installs, not only sideloaded ones.

## [0.2.4] - 2026-09-01

### Added

- **Wavee is on the Microsoft Store** — the What's-new page and the after-update dialog now announce the Store
  listing to sideloaded installs, with a button that opens it. Store-installed copies (which already get updates
  through the Store) don't see the announcement.

### Fixed

- **Music videos: smooth switching, no more freezes.** Turning video on, or skipping tracks while video was
  playing, could freeze the whole app for seconds — and every track change tore the video player down and rebuilt
  it from scratch. Video now switches in place: the previous frame holds until the next video's first frame
  arrives, the next track's video is looked up ahead of time during playback, and play/pause/seek stay responsive
  through the whole switch. Also fixed along the way: the docked video card letterboxed wider-than-16:9 videos
  (it now fits the video's real shape), the video controls could lay out wider than their card and stick that way,
  and two loading spinners could stack on top of each other while a video started.
- The What's-new highlight cards could cut their text off mid-line — cards in a row were sized against a wider
  text wrap than they actually got. Rows of equal-width cards now measure at their real share of the width, so the
  cards fit their text.

## [0.2.3] - 2026-08-31

### Fixed

- Fixed rare text rendering glitches (a letter occasionally drawing incorrectly) that could persist on some screens.
- A background page could still scroll while a dialog was open on top of it.

## [0.2.2] - 2026-08-31

### Added

- **Settings > About** — the graphics adapter in use is now shown, with a picker to select a different GPU (applies
  live). Useful on hybrid-GPU laptops/desktops.
- **App zoom** — Ctrl+Plus/Minus/0 (top row and numpad), Ctrl+mouse-wheel, and three new command-palette entries
  (Zoom in/out/reset) scale the whole app UI.

### Fixed

- **Crash navigating to an artist page.** A placeholder layout shape built while an artist's real data is still
  loading could overflow an internal index and crash the app.
- **Stability and smoothness on integrated/weak GPUs** (e.g. hybrid laptops) — the app could intermittently freeze
  (visuals stuck, audio still playing) during heavy navigation or long sessions on some integrated GPUs. Frame
  pacing, texture upload, and memory-budget handling were hardened so a GPU hiccup now recovers automatically in
  under a second instead of freezing the window; large album art / cover images that could get stuck as blank
  placeholders on affected hardware now load correctly.
- **Correct GPU selection** — on machines with more than one GPU (e.g. a laptop with both an integrated and a
  dedicated graphics card), Wavee now reliably picks the faster one instead of sometimes landing on the weaker
  integrated GPU.
- Playlist/album pages now show a clear, in-place notice when Spotify reports content as deleted, revoked, or still
  loading (minified), instead of silently failing to update.

## [0.2.1] - 2026-08-30

### Fixed

- **Tabs** — clicking a tab in the tab strip now shows that tab's page. Activating a tab is a restore, not a new
  navigation: it no longer pushes onto Back history or rewrites the destination's breadcrumb origin, and closing a
  background tab no longer navigates.
- **Breadcrumbs** — a section opened from Home (Weekly Song Charts, Concerts, ...) now reads
  `Home › Browse › ...` instead of claiming you came from Browse.
- **Browse, Concerts, artist schedules** — page content no longer scrolls up through the breadcrumb band; the
  Concerts filter card docks below the crumb instead of over it.
- **Search box** — typing a letter no longer flashes "No results found" before the suggestions arrive. Suggestions
  survive a re-mount of the search field, a superseded request can no longer leave the box stuck loading, and a
  transport failure now says "Couldn't load suggestions" with a retry instead of pretending there were no results.
- **Queue** — every upcoming row (Next in queue, Next up, Autoplay) can be drag-reordered within its own section; a
  queue row dragged around the queue can no longer land as "Added to <playlist>".
- **Recents** — expanding a row reveals every track played from that context (the full list, numbered 1..n, with the
  time it was played) with a smooth reveal instead of a stalled sliver that snaps open.
- **Liked Songs** — the left rail is resizable and remembers its width (podcast shows too).
- **Home cards** — the hover play button on the Recents rail was wired to nothing; it now plays. A card's play button
  no longer pauses/resumes an unrelated context just because the playing track shares an artist, and it stays under
  the pointer while you press it.
- **Liked Songs sync** — the local collection could silently lose its newest likes after a truncated snapshot and
  then never recover. Snapshots are now verified before anything is swept or the sync token advances, the app
  reconciles the collection against the server at start, on reconnect and periodically, fresh likes are shielded from
  a sweep, and the header, sidebar and stats all report the same membership count. Existing installs repair
  themselves on first launch.
- **Settings › Playback** — the "On metered connections" card shows whether Windows currently reports a metered
  network, updating the moment the network cost changes.
- **Crash on the Liked Songs page** — opening Liked Songs before its tag data had hydrated (every track untagged)
  crashed the app in the blend card; the empty "Other" tail is now a real empty list.
- **Crash reports** now carry the build commit, module base and frame offsets, and every release keeps its symbol
  map, so a NativeAOT crash can be resolved to a method.
- **Playlist owner names/avatars** — a user-profile payload that is not plain JSON is decoded instead of being
  memoised as "no profile" for the session.
- **Icons** — the icon font ships inside the app, so glyphs no longer depend on the Windows version (the "Tune"
  and "What's new" icons rendered as boxes on Windows 10).
- **Opening a playlist** — a cold open no longer flashes "You no longer have access to this playlist", "0 songs" or
  "Nothing here yet" while the header and tracks are still loading; it shows the skeleton until they land.
- **Home facets** — the Music / Podcasts / Audiobooks chips now swap the whole feed: a facet is its own document
  (server sections in server order, its own scroll position), the feed carries the facet it was read for so a stale
  poll can never land under a newer chip, and shelves re-describe on every feed change (a facet swap, the periodic
  refresh, a daylist rollover) instead of keeping the first feed they mounted with. The Home pill's trailing mark is
  an X that clears the sub-facet, not a chevron that promised a menu.
- **Hero layout** — the cover art no longer renders detached at the bottom-left of the page, and the facts bento (this
  week's likes, tempo, top artists) moved from the hero column to the page footer so the first track is above the
  fold; the two-column layout keeps it in the rail.
- **Concert filter pills** — the pill's trailing glyph reflects what a tap does (reopen the chooser vs clear).
- **Lyrics** — credit and metadata rows that some providers ship inside the lyric body (lyricist / composer /
  arranger headers, boilerplate, stray tags) no longer reach the screen or the ranking. The cleaner first honours what
  the provider marks structurally, then trims leading/trailing lines that align to nothing in the reference document
  (language-agnostic), and only then falls back to a "key: value" grammar — never a bare word list.

## [0.2.0] - 2026-08-30

### Added

- **Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. Off by default, so a normal
  install no longer exposes developer-only tooling.
- Updates: **What's new** — a page that shows the release highlights, the full changelog, and the current GitHub
  state of every issue a release references. The first launch after an update opens a short summary of what changed;
  a checkbox turns that off for good.
- Updates: **Release channel and update timing** — Settings › About names the channel this build follows. Updates
  apply on the next launch; optionally Wavee installs a waiting update as it closes (“Install a waiting update when
  I quit Wavee”, off by default) so the new version is simply there next time. Metered connections are left alone
  unless you opt in.
- **In-app update check** — Settings › About checks for a newer published release and reports what it finds, alongside
  the installed version.
- **Privacy policy and third-party notices** — reachable from Settings › About. `THIRD-PARTY-NOTICES.txt` is generated
  at packaging time and ships both inside the package and as a release asset.
- **Start on login** — an opt-in setting to launch Wavee when Windows starts.
- **`wavee://` protocol and toast activation declared in the MSIX manifest** — deep links and notification clicks now
  activate the packaged app through its registered identity/AUMID instead of the unpackaged HKCU fallback.
- **Crash notice on next launch** — if the previous run died, the next launch says so and points at the crash report
  and log, rather than failing silently.

### Changed

- Updates: **Updates download in the background and apply on the next launch.** “Update now” downloads, stages and
  restarts with real progress; the app no longer hands you to App Installer and hopes.
- **Dealer archive is off by default.** The raw dealer-message archive is a debugging aid; it now writes nothing unless
  it is turned on.
- **Lossless removed from the audio-quality picker** until it actually ships. Offering an option that silently did not
  apply was worse than not offering it.
- **API console, lyrics inspector, and test notifications moved behind developer mode.** They remain fully available —
  just not on the default Settings surface.
- **Image cache moved under `%LOCALAPPDATA%\Wavee\cache`**, joining the rest of the app's per-user state in one place
  that a factory reset can clear.

### Fixed

- Updates: **no more blank "App Installer" window at every launch.** The feed's on-launch check now runs silently in
  the background; a new version is staged while you keep using Wavee and applied the next time it starts.
- App icon: **no more white corners on the taskbar.** The icon's rounded corners are now genuinely transparent
  instead of white pixels left over from the artwork's export.
- Updates: **The "What's new" plate no longer opens on top of the setup wizard.** After a sign-in or a setup rerun it
  waits until setup is done, then appears in the same session.
- Setup: **The Terms step's full agreement opens in place and stays in its column.** The summary card grows into the
  scrollable agreement with a proper Close button (and Escape), instead of a separate panel that could paint over the
  page's own heading at some window widths.
- Updates: **A background update check that cannot reach the feed no longer interrupts you.** It is still shown in
  Settings › About (with Retry); only a check you started yourself, or a failed install, raises a toast.
- Updates: **Installing an update on quit no longer trips Windows' hang detector.** Wavee kept answering Windows while
  the update installed, instead of going quiet and being closed as an unresponsive app on the way out.
- Updates: **“Check for updates” could never find a release.** The check pointed at the repository's global latest
  release — the FluentGpu gallery's — so a published Wavee build was invisible to it. It now reads Wavee's own feed.
- **Fabricated listening history on fresh installs.** A brand-new profile showed recently-played entries that had never
  been played; a fresh install now starts empty.
- **The first-run wizard could be dismissed with `Esc`,** dropping the user into an unconfigured app.
- **"Cancel" while signing in quit the app** instead of returning to the previous step.
- **Setup › Local playback showed a stray chip row** with nothing behind it.
- **Hard-coded Premium pre-launch gate.** The account-tier check no longer depends on a compiled-in assumption about
  the signed-in account.

### Removed

- **Environment-variable switches.** `WAVEE_SETUP_START_PAGE`, `WAVEE_FPS`, and `WAVEE_DEALER_ARCHIVE` are gone, as are
  the `--free` flag and `WAVEE_FORCE_FREE`. Behaviour is configured in Settings (or reported by the diagnostics pages),
  never by an env var a shipped build would have to honour.
- Updates: **The `ms-appinstaller:` hand-off.** Windows disables that protocol by default on consumer installs, so the
  button opened nothing and then reported success. Wavee downloads and stages the update itself.

### Known limitations

- No dedicated **episode** or **profile** pages.
- **`nl` and `ko-KR`** localizations are partial and therefore hidden from the language picker.
- No **system tray** integration.
- No **podcast playback-speed** control.
- **FLAC seek** is not implemented.
- The **UI Automation tree** is incomplete — screen-reader coverage is partial.

[0.2.0]: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.0
