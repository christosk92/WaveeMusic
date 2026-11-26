---
kind: added
issues: [48]
contract: additive
contract-note: "TrackChromeStyle.MixCommand(label, on, onClick), a default member (the labelled command), for the list bar's Mix toggle; ViewKeys.MixSeam with MixSeamProps (the transition chip between two rows); PanelMode.Transition and PanelProps.TransitionBody (optional) for the editor in the right panel; ViewKeys.MixLesson with MixLessonProps (the Mix lesson page), MixEditorProps.Teach and .Learn (optional: the editor's teaching mode and its Learn how button), MixTimelineModel.Window, .Heard, .Layers, .Ruler, .CutAtMs, .PlayheadMs (optional: the timeline's layer view), HintSurfaces.MixBar"
---
**See and switch Mix.** A playlist Spotify has mixed shows a lit Mix button in its list bar and "Mixed · 2 of 9
transitions edited" on its meta line. The button turns Mix on for a playlist of your own, or lets you listen without
it. While a transition plays, the player bar's time reads "Mixing into <next song>". A song's menu in a mixed playlist
offers "Don't mix into the next song", which makes that one pair a normal track change. All three themes.

**Transitions in the list, as Spotify shows them.** With Mix on, a mixed playlist reads as one timeline: a thin line runs from
each song's cover to the next, and between every two songs sits a small chip saying what that transition does: "Auto", the
preset's name ("Fade", "Rise"…), "Custom" for one with its own curves or styles, or "No transition", where the line breaks. An
edited transition's line takes the accent, and one Spotify doesn't have yet says so on its chip ("This PC", "Not saved"). A BPM
· Key column takes the place of Date added. Turning Mix on draws it all in: the songs make room, the line and the chips appear
one after another down the list, the "Mixed" badge grows out of the cover's corner and the meta line hands its words over;
turning it off takes them away the same way. Click a chip, or tab to it and press Enter, to edit that transition. The Mix menu
also offers "Listen without Mix" and "Mix when shuffling". All three themes, each in its own look.

**Learn Mix: build a transition together.** A hands-on lesson in the Mix editor. It opens on a finished transition between two
songs (Listen first), takes it apart layer by layer down to a hard cut, then builds it back one layer at a time: Volume, Bass,
Filter, Echo and Tempo. At each step you drag that layer's one handle onto its marker in the Edit transition editor itself, hear
the layer on its own and then with everything, and flip between the original and your version while it plays. The guide beside
the editor holds every word and every button, as a stepper you can jump around in, with its progress. Open it from the editor's
"?" (Learn how), from Settings › Help & about › Learn, or from the tip the first time you turn Mix on. Its two songs are made on
this PC for the lesson, so it works offline; your music pauses while it plays and comes back when you leave. All three themes,
each in its own voice.
