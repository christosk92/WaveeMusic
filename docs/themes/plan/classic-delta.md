# Classic tuning delta (for technical-plan §6.6.3)

Source: canvas "Wavee Classic theme (tuned)" https://claude.ai/artifact/2zekYF4UVNaJtAE5XTszbN (56 boards; the "what changed" board is authoritative).
Files: `scratchpad\themes\classic-canvas\project\`. Ground truth for today's look: `classic-spec.md`.
"OWNER CHECK" = a recognisable change the owner must confirm before it lands.

Designer assumptions to confirm: sidebar drawn at 280 (production default 320); default of the new Surfaces option = Subtle;
D-10 list-bar layout used although round 4c is paused; Liked/Detail-Bar approved board (NDBcNd34ZF7AuzCnsWwH2r) was not
readable, drawn from the local copy + PR 205 code.

| ID | Area | Change | Source |
|---|---|---|---|
| F1 | Frame | Content card hairline (top-left r8, 1 px left/top), Classic only | round4 I1-A, D-1 |
| F2 | Frame | Light theme: card one step lighter than chrome on artwork pages | I1-B |
| F3 | Frame | One page inset 36/32/16 by width; one 16 px pane inset | WinUI audit §9, PageGeometry |
| F4 | Frame | OWNER CHECK: one title style per level (page 40/52 light Display, pane 28/36 semibold, card 20/28 semibold) | WinUI audit §4 |
| F5 | Frame | Breadcrumb B: one title + small parent trail; hero never repeats title | Crumb-B, Oct 10 decisions |
| F6 | Frame | Search box/text resize together, never cover tabs/avatar; flyout = box width, opens at full height with placeholder rows; closing animation | round4 C1-C4 |
| F7 | Frame | Leaving page frozen during exit; head arrives with body; Home loading bar fades in a fixed slot (90/90/210 ms unchanged) | A2, A3, A5 |
| F8 | Frame | Window minimum height | #197 |
| F9 | Frame | First frame is final (wizard/shell, zoom, width tier decided before it) | map-shell §0.2, map-engine §4.4 |
| F10 | Frame | One 250 ms light/dark cross-fade incl. caption buttons and cached pages | map-engine §4.4 |
| F11 | Frame | Entrance animations start on their first shown frame (no pop); choreography unchanged | Fable B.7 |
| F12 | Frame | Scroll restored in the first layout | abstraction-map E8 |
| F13 | Frame | Light/dark toggle moves from title bar to Settings + profile menu | abstraction-map §4 #1, PR 205 |
| S1 | Sidebar | Footer row removed from pane and rail; Ctrl+, opens Settings | D-5, D-11 |
| S2 | Sidebar | Row count and ⋯ share one slot | I2 |
| S3 | Sidebar | Text rows 36 tall | I3 |
| S4 | Sidebar | Folder glyph x16, label x40 | D-2 |
| S5 | Sidebar | Pin is a real "Unpin" button; Pin/Unpin top-level menu item | J1, J2 |
| S6 | Sidebar | Section header hover no longer lights its buttons | J3 |
| S7 | Sidebar | Expand/collapse: headers never vanish; pinned playlist animates out of Playlists; symmetric interruptible slide (~250/~200 ms); scroll holds; second toggle queues | S1-S4 |
| S8 | Sidebar | Tooltip on truncated names | WinUI audit §2 |
| S9 | Sidebar | Offline pins keep last title and cover | map-shell P3 |
| S10 | Sidebar | Failed library load shows error + Retry; grid view gets loading placeholders | map-shell P3 |
| P1 | Player | Queue button from the compact tier up | L2 |
| P2 | Player | One button size, one shared "on" plate (Lyrics, Queue, Devices, Video) | WinUI audit §8 |
| P3 | Player | Player style hidden until it affects Now playing | #199 |
| R1 | Panel/lyrics | Lyrics text size change doesn't make the column jump | map-shell P4 |
| R2 | Panel/lyrics | Lyrics offline message | map-shell P4 |
| R3 | Panel/lyrics | Equalizer always animates | K1 |
| R4 | Panel/lyrics | Classic panel stays a card under the title bar (no photo bleed) | G4, D-4 |
| R5 | Full screen | Per-face background darkening; one consistent light/dark ink | map-shell P4, §0.2 |
| H1 | Home | Failed section shows its error bar in a reserved slot | map-pages §3.1 |
| H2 | Home | Episode captions localised | map-pages §5 |
| D1 | Detail | Rail actions: Play split, Shuffle, quiet ♥, Share, ⋯ | Detail-Bar option C |
| D2 | Detail | Play split button reads as one accent control, "More ways to play" menu | N1 |
| D3 | Detail | List bar: Play next + Shuffle left; Filter, Sort, Find, Insights, ⋯ right; Row size/Select/Columns into ⋯ | D-6, D-10 |
| D4 | Detail | Song count/length in the rail; "3 of 13 songs" while filtered | D-7, D-10 |
| D5 | Detail | Filter = labelled button opening flyout B | EQEH, Oct 10 decisions |
| D6 | Detail | Sort: one label + one chevron, highlighted only when not the default | D4, WinUI audit §6 |
| D7 | Detail | One chip style everywhere (32, r4, checked = accent) | 5a3154fe |
| D8 | Detail | Chip row ends 8 px before the tools, with a fade | D3 |
| D9 | Detail | 16 px rail-to-list gap; column header aligned with rows | B1, V10 |
| D10 | Detail | Album loading skeleton matches the real page | B2 |
| D11 | Detail | No rail scrollbar flash on load | B3 |
| D12 | Detail | "Featured on" loses the stuck outline | B4 |
| D13 | Detail | Artwork colours keep a contrast floor | WinUI audit §5 |
| D14 | Detail | Rail: artist faces give way before names | L1 |
| D15 | Detail | Page tint arrives with the page | Fable A.9 |
| D16 | Liked | Approved Liked Songs redesign; header rule full width | round 3, D-9 |
| D17 | Artist | Pre-bleed production artist header | D-4, G1 |
| D18 | Artist | Hero copy width and fade fixed at medium widths | V4 |
| D19 | Artist | Top tracks no second wave | round4c |
| D20 | Detail | OWNER CHECK: one Play button style across pages | WinUI audit §6 |
| D21 | Artist | OWNER CHECK: Follow uses a person glyph, not a heart | WinUI audit §6 |
| Y1 | Library | OWNER CHECK: list and detail on one card, split by a divider | WinUI audit §3 |
| Y2 | Library | "Recents ⌄": one label, one chevron | WinUI audit §6, D4 |
| Y3 | Library | Artist panel buttons: one consistent row | WinUI audit §6 |
| L1 | Rows | Versions chevron only on songs with versions | WinUI audit §7 |
| L2 | Rows | OWNER CHECK: at rest only a filled heart if liked; outline heart and ⋯ on hover | WinUI audit §7 |
| L3 | Rows | No film glyph at rest on Liked rows | D6 |
| L4 | Rows | Unavailable songs show the reason; "Hide songs I can't play" filter | map-pages §5, Filter B |
| T1 | Settings | No empty gap above the first setting | E2, F4 |
| T2 | Settings | OWNER CHECK: Settings tabs use the page cross-fade | map-shell P6 |
| T3 | Settings | Appearance: theme picker + "Classic options" section | abstraction-map §4, §6.1 |
| X1 | States | Offline voice used: strip on saved pages, offline empty state elsewhere | map-pages §2.5 |
| X2 | States | Every skeleton matches its final layout | no-layout-jumps, B2 |
| X3 | States | Local-playback dialog footer wraps ("Try again" not clipped) | E1 |

GitHub issues: #197 and #199 are the only open visual issues; #184, #154, #113, #48 are features/non-visual (out).
Note: several F/R items (F9-F12, R1) are engine/transaction work already in the plan (E-items); map them, don't duplicate.
