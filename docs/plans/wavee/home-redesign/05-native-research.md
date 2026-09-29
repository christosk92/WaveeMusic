# 05 — What makes a Windows app feel native (research for the Home redo)

Why this exists: the owner rejected the "Made for you right now" mix cards (wide empty dark tile, 44px cover,
tiny text) and said the page, especially the 388px scrim banner hero, "feels very web-y". This note distils
Fluent 2 / Windows 11 guidance, Microsoft's inbox apps and what users say about native vs web-wrapper apps into
rules that can be drawn.

## 1. Principles (the short version)

1. **Two layers, not a poster.** Windows 11 apps have a *base* layer (Mica: title bar, nav) and a *content*
   layer; the content layer is either one contiguous surface or split into **cards** (MS Learn, Layering).
   Content sits *on* the layer. A full-bleed photo with text painted over a gradient is a web landing-page
   idiom. Microsoft's own apps put imagery *inside* a card or a FlipView item and keep the text on the layer
   beside or below it (Media Player album header, Store product pages, Settings home cards).
2. **Geometry is fixed: 8 / 4 / 0.** 8px only for top-level containers (window, flyout, dialog, and large
   content cards); 4px for in-page elements: buttons, list backplates, **cover art**, tiles; 0 where straight
   edges meet (MS Learn, Geometry). Pill (full-radius) buttons don't exist in WinUI except the round
   media-transport play button.
3. **The type ramp is small and fixed.** Caption 12/16, Body 14/20, Body Strong 14/20 semibold, Body Large 18/24,
   Subtitle 20/28, Title 28/36, Title Large 40/52, Display 68/92. Regular and Semibold only (no Bold, no Light
   for UI). Sentence case. **Minimum 12px regular / 14px semibold.** Left-aligned by default (MS Learn,
   Typography). 9.5 / 10.5 / 11.5px text is a web-ism, and so is lowercase forced with CSS.
4. **Controls are the Windows controls.** 32px-tall buttons, 4px radius, a 1px elevation stroke (lighter top,
   darker bottom), an Accent button for the one primary action, Standard for secondary, Subtle (transparent
   until hover) for icon buttons. Hover means a slightly different fill, press means a lower fill with dimmed text.
   No lift, no glow, no scale on buttons. Focus is the two-tone focus rectangle (2px outer, 1px inner), shown
   only for keyboard use.
5. **Collections look like GridView/ListView item templates.** The documented templates are *image and text
   below* (square 180×180 or 16:9 320×180, 12px between image and text), *icon and text* (a 264×48 tile), and
   *image with a text overlay on a solid band* (MS Learn, GridView item templates). Hover draws a subtle
   rounded backplate behind the item. Items don't jump up (translateY lift is a web card idiom).
6. **Carousels are FlipView + PipsPager**, which work best for small sets (≤25), with arrows that show on hover
   and pips centred or aligned under it. For long shelves Windows uses a horizontally paged GridView with
   chevrons (Store, Xbox app) plus a "See all" hyperlink.
7. **Restraint with colour and chrome.** One accent (the system accent), used for the primary action, the
   selection indicator, progress and links. No rainbow tiles, no decorative gradients, no drop shadows at rest
   (shadow means elevation: flyout 32, card 8, control 2). Zune got the same result through typography: big type,
   monochrome text, accent used sparingly, no glossy buttons or gradient menus (Kimball; Slate; Wikipedia Metro).
8. **Density is a feature.** Desktop users read 6–8 items per row, hover affordances and lists. Media Player and
   Settings use lists and compact cards (SettingsCard: header, description, trailing chevron or control).
   People who love foobar2000/MusicBee praise information density, and people who love Media Player/Groove
   praise the big clean cover grids. Use both: big covers where the art is the content, lists where the metadata is.

## 2. What people say separates native from "web wrapper"

(HN threads 20275063, 44227583, 18631738, 42891109; plus the Files / Media Player coverage.)
- A web-app app "inflicts the company's own design system" on the OS. A native one uses the OS's design
  language, quieter and more subdued than branded UI.
- Tell-tales: the hand cursor on buttons (Windows uses the arrow; the hand is for hyperlinks only), selectable
  UI labels, hover that moves things, big "data views"/Material-style cards, missing keyboard focus, instant
  response missing, custom scrollbars, marketing-banner heroes.
- Praise for Files, Media Player and the Store redesign is about *matching Windows 11*: Mica, rounded 4/8
  geometry, standard controls, smooth but short page transitions, and "less cluttered" heroes.

## 3. Inbox-app patterns worth copying

- **Media Player** — album/playlist header: square cover (4px radius) on the left, title (Title/Title Large
  semibold), metadata line, then a row of Standard/Accent buttons ("Play all", "Shuffle", "Add to"). Library is
  a GridView of square covers with title + caption beneath; hovering shows a round play button on the art.
- **Microsoft Store** — Home spotlight is a FlipView of 8px-rounded cards with arrows and pips. The newer product
  pages put the hero image *inside* the card and the text on the page surface. Shelves have a title, a "See all"
  link and chevrons, and page in whole items.
- **Settings home** — a dashboard of 8px cards on the layer, each with a header, a short description and an action
  aligned right. Lists inside cards use 4px row backplates.
- **Xbox app / Store "Jump back in"** — horizontal item tiles with art on the left and text on the right, in a
  fixed-column grid.
- **WinUI 3 Gallery home** — a header image band *behind* the page title with tiles on the layer. Microsoft uses
  it for a showcase app, not for a content app.
- **Third party** (Files, Ambie, Screenbox, Rise, Wintoys, Unigram, Dev Home): cards with a 1px stroke on Mica,
  SelectorBar/NavigationView tabs, list rows with 40–48px thumbnails, standard buttons. Ambie and Wintoys use
  image tiles with text *below*, not over.

## 4. Do / Don't (drawable)

DO
- Hero is a **card on the layer**: text column (eyebrow caption, Title Large name, tag hyperlinks, metadata, a
  button row with Accent Play + Standard Shuffle + Subtle icon buttons), with the header image **inset** at 4px
  radius on the right. No scrim, no text on the image.
- Big square covers (≥ 150px, ideally ~200px) with Body Strong title + 12px secondary caption below, like a
  GridView "image and text" template. Round art only for artists.
- 16:9 header images as **"rectangle image + text below"** cards (~428×241) where a playlist has a header image.
- Chapter header = Subtitle 20/28 semibold + secondary 14px description + right-aligned [See all] hyperlink
  button and a PipsPager-style pager (28px subtle chevrons, 4/6px pips). Chevrons disable at the ends. Paging
  moves by exactly one page of whole items.
- Lists for metadata-heavy content (new releases, "because you like" clusters): 56–64px rows, 40–48px art,
  4px backplate on hover.
- SettingsCard-style tiles for browse/navigation: 48px art, title + description, trailing chevron.
- The two-tone focus rectangle on every interactive element. Arrow cursor on buttons.
- 4px grid spacing: 8 / 12 / 16 / 24 / 32 / 40.

DON'T
- Full-bleed banner with a gradient scrim and text on the picture.
- Pill buttons or pill "chips" everywhere; tag chips over images.
- Text under 12px; lowercase transforms; Light/Thin weights for UI text.
- Card hover lift (translateY), glowing play circles, scale-on-hover for whole tiles.
- One-off card types per shelf (numeral badges, stacked-avatar radio, pill rails, big faded numerals).
- Wide empty tiles with a thumbnail: if the art is small, the container must be a list row, not a card.
- Decorative shadows at rest; accent colour used as decoration.

## Sources

- Layering and elevation — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering
- Typography — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography
- Geometry — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry
- FlipView + PipsPager — https://learn.microsoft.com/en-us/windows/apps/design/controls/flipview
- GridView item templates — https://learn.microsoft.com/en-us/windows/apps/design/controls/item-templates-gridview
- SettingsCard (Community Toolkit) — https://learn.microsoft.com/en-us/dotnet/api/communitytoolkit.winui.controls.settingscard
- Settings home cards — https://en.wikipedia.org/wiki/Settings_(Windows)
- Media Player (2022) — https://en.wikipedia.org/wiki/Windows_Media_Player_(2022) ; https://www.howtogeek.com/reasons-to-use-windows-11-media-player/
- Store redesign — https://blogs.windows.com/windowsexperience/2021/10/04/11-things-to-know-about-the-new-microsoft-store-on-windows-11/ ; https://www.windowscentral.com/software-apps/windows-11/microsoft-store-update-adds-immersive-experience-to-showcase-apps-and-games
- Zune design language — https://davidvkimball.com/posts/zunes-design-language-and-how-it-evolved-into-windows-phone/ ; https://slate.com/technology/2012/10/microsoft-zune-how-one-of-the-biggest-flops-in-tech-history-helped-revive-a-great-american-tech-company.html ; https://en.wikipedia.org/wiki/Metro_(design_language)
- Native vs web wrapper (HN) — https://news.ycombinator.com/item?id=20275063 ; https://news.ycombinator.com/item?id=44227583 ; https://news.ycombinator.com/item?id=18631738 ; https://news.ycombinator.com/item?id=42891109
- Files app coverage — https://tech.yahoo.com/apps/articles/5-free-windows-apps-outperform-053000735.html

## 5. Wide lead items: where they go and why (owner addition)

Native model: a GridView/ItemsView with a **variable-size lead item** (Store "spotlight" tiles, the Xbox app's
lead game tile). The lead spans exactly **two cells** of the same grid, so its height equals the square covers'
height, its text sits below it like its neighbours' text, and a page is still N whole cells wide (the pager
still steps 1332px = one viewport plus one gap). It is not a banner and has no scrim.

Where a lead goes (the item must have a `header` image and actually be the most important thing on the shelf):
- **Made for you → Discover Weekly** (header 6a62…, 2 cells of the 6-column grid, 428×206). It's the one weekly,
  heavyweight personal playlist among daily mixes, and a Monday refresh is worth flagging. Daily Mixes stay
  square because their covers carry the mix number.
- **Radio & mixes → Savage Garden Radio** (header 8f89…, 2 cells of the 8-column grid, 317×150). It's the first
  "Recommended Stations" item, and a lead helps break the long run of stations. Its duplicate row in "Because you
  like: Savage Garden" was removed.
- **Daylist → the hero card** (header 75ba…, inset). It's the page's one play-now answer.
- **It's New Music Friday!** already *is* the wide shelf: every item there has a header image (Release Radar,
  Fresh Pop, All New K-Pop, New Dance Pop, the new alt, Indie Update), so the whole shelf is a 3-up of 16:9
  image-and-text tiles, with Release Radar first. Adding a lead there would put a wide item inside a wide shelf.

Where a lead does NOT go:
- **Recently played**: resume needs density and equal weight, so it uses a 4×2 grid of horizontal row items.
- **Because you like…**: metadata-led clusters, so it uses list rows in grouped cards.
- **Jump back in**: history with no editorial priority and no header images, so it uses squares plus round
  artists.
- **Browse**: navigation, so it uses SettingsCard-style tiles with chevrons.

Page rhythm (no two neighbouring shelves share a template): hero card, then row items (Recents), then
wide lead + squares (Made for you), then a 3-up of 16:9 tiles + a release list (Friday), then grouped list
cards (Because you like), then squares + artist circles (Jump back in), then wide lead + station circles +
squares for mixes (Radio & mixes), then navigation tiles (Browse). Charts stay as three navigation tiles and
are not a numbered track list: the captured data has no chart tracks, and inventing a Top 50 would be a fake fact.
