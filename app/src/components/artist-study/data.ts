/**
 * Deterministic fixtures for the artist-page study.
 *
 * The catalogue deliberately exercises the real 50-single bound so the study has to solve
 * orientation at the former cutoff instead of stopping before it.
 */

export type ArtistVariant = "editorial" | "band" | "plate" | "current";

export interface VariantDefinition {
  id: ArtistVariant;
  label: string;
  eyebrow: string;
  thesis: string;
  tradeoff: string;
}

export const variants: VariantDefinition[] = [
  {
    id: "editorial",
    label: "Editorial split",
    eyebrow: "Recommended",
    thesis:
      "An asymmetric split: oversized Fluent Display type on a palette-tinted left field, the photograph full-bleed to the right and top edges.",
    tradeoff:
      "An asymmetric split guides the eye more predictably than a uniform full-width band, and no text sits on the photo.",
  },
  {
    id: "band",
    label: "Wide band",
    eyebrow: "Photo-forward alt",
    thesis: "A full-bleed band from the real 2660×1139 header, with all identity text on the content layer beneath it.",
    tradeoff: "Keeps the cinematic read and never sets text on photography, but two stacked full-width blocks cost the most vertical space.",
  },
  {
    id: "plate",
    label: "Portrait plate",
    eyebrow: "Fluent-restrained",
    thesis: "A framed 320² portrait beside the identity text over a 6% palette wash.",
    tradeoff: "The most conservatively Fluent and the strongest light theme, at the cost of photographic scale.",
  },
  {
    id: "current",
    label: "Current fade",
    eyebrow: "Control",
    thesis: "Today's shipping hero: a deep alpha feather over an overlaid identity, rebuilt against the real native constants.",
    tradeoff: "The honest baseline. Text sits on the photograph, so contrast is per-artist luck.",
  },
];

export interface Track {
  rank: number;
  title: string;
  plays: string;
  duration: string;
  cover: string;
  liked?: boolean;
  explicit?: boolean;
  /** The now-playing row: an equalizer replaces the rank and the title takes accent colour. */
  playing?: boolean;
}

/** Ten per page × five pages. */
export const TRACKS_PER_PAGE = 10;

const COVERS = [
  "heather",
  "superache",
  "found-heaven",
  "vodka-cranberry",
  "maniac",
  "family-line",
  "people-watching",
  "astronomy",
  "kid-krow",
  "this-song",
  "wishbone",
  "wishbone-deluxe",
];

const TITLES = [
  "Heather", "The Cut That Always Bleeds", "Memories", "Vodka Cranberry", "Maniac",
  "Family Line", "People Watching", "Astronomy", "Wish You Were Sober", "The Exit",
  "Checkmate", "Crush Culture", "Generation Why", "Comfort Crowd", "Fight or Flight",
  "Greek God", "Affluenza", "The Story", "Little League", "Overdrive",
  "Jigsaw", "Yours", "Best Friend", "Footnote", "Movies",
  "Disaster", "Never Ending Song", "Winner", "Summer Child", "Telepath",
  "Alley Rose", "Killing Me", "Lonely Dancers", "Bourgeoisieses", "Miss You",
  "Boys & Girls", "Actor", "Care", "Found Heaven", "Forever With Me",
  "Eye of the Night", "Fainted Love", "Sunset Tower", "Purgatory", "Class Clown",
  "Nauseous", "Grow", "Wishbone", "Vodka Sunrise", "Landslide",
];

const PLAYS = [
  2381125897, 662053974, 758810137, 125421696, 1026908442,
  356740228, 535169474, 454859476, 358988981, 171204338,
  148930221, 402118764, 388470112, 331902845, 96412008,
  74220913, 61008442, 58330217, 51902884, 47118220,
  44902118, 41330884, 38118442, 35902117, 33118904,
  30884221, 28330118, 26118902, 24902330, 22118884,
  20904118, 19330902, 17118440, 15902118, 14330884,
  13118902, 12118330, 11330118, 10902884, 9884118,
  9118330, 8330902, 7902118, 7118884, 6884330,
  6118902, 5902118, 5330884, 4902118, 4118330,
];

function humanPlays(n: number) {
  if (n >= 1_000_000_000) return `${(n / 1_000_000_000).toFixed(2)}B plays`;
  return `${(n / 1_000_000).toFixed(1)}M plays`;
}

export const topTracks: Track[] = TITLES.map((title, i) => ({
  rank: i + 1,
  title,
  plays: humanPlays(PLAYS[i]),
  duration: `${2 + ((i * 7) % 3)}:${String(5 + ((i * 13) % 55)).padStart(2, "0")}`,
  cover: COVERS[i % COVERS.length],
  liked: i % 5 === 0,
  explicit: i % 9 === 8,
  playing: i === 0,
}));

export interface ReleaseTrack {
  id: string;
  title: string;
  duration: string;
  explicit?: boolean;
}

export interface Release {
  id: string;
  title: string;
  releaseDate: string;
  year: number;
  cover: string;
  trackCount: number;
  duration: string;
  tracks: ReleaseTrack[];
}

export interface ReleaseGroup {
  id: "albums" | "singles-and-eps" | "compilations";
  type: string;
  items: Release[];
}

const TRACK_SUFFIXES = [
  "Intro",
  "Afterglow",
  "Blue Hour",
  "Interlude",
  "Paper Hearts",
  "Night Drive",
  "Polaroid",
  "Second Wind",
  "Outro",
  "Live Again",
  "Acoustic",
  "Reprise",
  "Home Video",
  "Voice Note",
  "Finale",
  "Bonus Track",
  "Demo",
];

function releaseTracks(id: string, title: string, count: number): ReleaseTrack[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `${id}-track-${i + 1}`,
    title: i === 0 ? title : `${title} — ${TRACK_SUFFIXES[(i - 1) % TRACK_SUFFIXES.length]}`,
    duration: `${2 + ((i * 7) % 3)}:${String(8 + ((i * 17) % 51)).padStart(2, "0")}`,
    explicit: i > 0 && i % 7 === 0,
  }));
}

function release(
  id: string,
  title: string,
  releaseDate: string,
  cover: string,
  trackCount: number,
  durationMinutes: number,
): Release {
  return {
    id,
    title,
    releaseDate,
    year: Number(releaseDate.slice(0, 4)),
    cover,
    trackCount,
    duration: `${durationMinutes} min`,
    tracks: releaseTracks(id, title, trackCount),
  };
}

const albums = [
  release("wishbone-deluxe", "Wishbone Deluxe", "2026-06-12", "wishbone-deluxe", 17, 52),
  release("wishbone", "Wishbone", "2025-08-15", "wishbone", 13, 41),
  release("found-heaven", "Found Heaven", "2024-04-05", "found-heaven", 13, 37),
  release("superache", "Superache", "2022-06-24", "superache", 12, 40),
  release("kid-krow", "Kid Krow", "2020-03-20", "kid-krow", 12, 33),
  release("sunset-season", "Sunset Season", "2018-11-16", "astronomy", 5, 18),
];

const SINGLE_YEARS = [
  ...Array(6).fill(2026),
  ...Array(6).fill(2025),
  ...Array(7).fill(2024),
  ...Array(7).fill(2023),
  ...Array(7).fill(2022),
  ...Array(6).fill(2021),
  ...Array(6).fill(2020),
  ...Array(5).fill(2019),
] as number[];

const singles = TITLES.map((title, i) => {
  const year = SINGLE_YEARS[i];
  const month = String(12 - (i % 6)).padStart(2, "0");
  const day = String(24 - (i % 8)).padStart(2, "0");
  const trackCount = 1 + (i % 4);
  return release(
    `single-${i + 1}`,
    title,
    `${year}-${month}-${day}`,
    COVERS[(i + 3) % COVERS.length],
    trackCount,
    4 + trackCount * 3,
  );
}).sort((a, b) => b.releaseDate.localeCompare(a.releaseDate));

const compilations = [
  release("the-collection", "The Collection", "2025-02-14", "wishbone", 16, 54),
  release("early-works", "Early Works", "2023-09-08", "kid-krow", 11, 36),
];

export const discography: ReleaseGroup[] = [
  { id: "albums", type: "Albums", items: albums },
  { id: "singles-and-eps", type: "Singles & EPs", items: singles },
  { id: "compilations", type: "Compilations", items: compilations },
];

export const appearsOn: Release[] = [
  release("appears-bedroom-pop", "Bedroom Pop Now", "2026-03-20", "this-song", 18, 57),
  release("appears-night-drives", "Late Night Drives", "2025-10-17", "superache", 16, 49),
  release("appears-void", "Songs for the Void", "2024-07-12", "astronomy", 14, 45),
  release("appears-teen-anthems", "Teen Anthems", "2023-05-26", "vodka-cranberry", 20, 63),
  release("appears-heartbreak", "Indie Heartbreak", "2022-02-11", "people-watching", 15, 48),
  release("appears-coming-of-age", "Coming of Age", "2021-06-04", "family-line", 13, 41),
  release("appears-dorm-room", "Dorm Room Demos", "2020-09-18", "found-heaven", 12, 38),
];

export const releaseCount = discography.reduce((total, group) => total + group.items.length, 0);

export type PickShape = "compact" | "hero";

export const artistPick = {
  comment: "30 years of Daydream 💛",
  badge: "LOCKE OUT NOW!",
  title: "Daydream - 30 Years",
  kind: "Playlist",
  cover: "found-heaven",
  compactTitle: "Back from the Brink",
  compactKind: "Single",
  compactCover: "superache",
};

export const artist = {
  name: "Conan Gray",
  bio: "Nobody has tapped into the thoughts and feelings of this era quite like Conan Gray.",
  monthlyListeners: "20,577,457",
  followers: "13,357,890",
  worldRank: 419,
  latest: {
    eyebrow: "New release · 12 Jun 2026",
    title: "Wishbone Deluxe",
    meta: "Album · 17 tracks · 52 min",
    cover: "wishbone-deluxe",
  },
};
