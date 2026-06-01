/* Mock data for the Library rework prototype.
   Names mirror the real screenshots so the look reads true. Covers are
   generated as deterministic gradients (offline-friendly) with initials. */

// Deterministic hue from a string.
function hueFrom(str) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) % 360;
  return h;
}
function initials(name) {
  const parts = name.replace(/[^\p{L}\p{N} ]/gu, '').trim().split(/\s+/);
  return ((parts[0]?.[0] || '') + (parts[1]?.[0] || '')).toUpperCase() || name[0]?.toUpperCase() || '?';
}
// CSS gradient + initials → a cover. Returns {style, label}.
function cover(name) {
  const h = hueFrom(name);
  const h2 = (h + 38) % 360;
  return {
    style: `background:
      radial-gradient(120% 120% at 20% 0%, hsl(${h} 62% 46% / .95), transparent 60%),
      linear-gradient(150deg, hsl(${h} 55% 30%), hsl(${h2} 60% 20%));`,
    label: initials(name),
  };
}

const TRACKS_SHORT = (names) =>
  names.map((n, i) => ({ n: i + 1, title: n.t, dur: n.d, liked: !!n.l, video: !!n.v }));

const ALBUMS = [
  { id: 'al1', title: 'rosie', artist: 'ROSÉ', year: 2024, recents: 'Played 1d ago', liked: true,
    tracks: TRACKS_SHORT([{t:'number one girl',d:'3:13',l:1},{t:'toxic till the end',d:'2:46'},{t:'APT.',d:'2:49',l:1},{t:'3am',d:'3:01'},{t:'drinks or coffee',d:'2:39'},{t:'two years',d:'3:18'}]) },
  { id: 'al2', title: 'WLUWD', artist: 'Tristam', year: 2021, recents: 'Played 1d ago', liked: true,
    tracks: TRACKS_SHORT([{t:'Black Beauty',d:'3:47',l:1},{t:'Ruthless',d:'3:06'},{t:'Mistake',d:'3:26'},{t:'Children in the Dark',d:'3:32',l:1},{t:'Burn',d:'3:14'},{t:'1992',d:'3:23'},{t:'Over the Edge',d:'3:17'},{t:'Take a Chance',d:'2:54'},{t:'Different',d:'4:03'}]) },
  { id: 'al3', title: 'Starting Over', artist: 'Yu Takahashi', year: 2019, recents: 'Played 3d ago',
    tracks: TRACKS_SHORT([{t:'Ashita Mata',d:'4:11'},{t:'Hajimari no Uta',d:'3:52',l:1},{t:'Water',d:'4:30'}]) },
  { id: 'al4', title: 'Free', artist: 'Troye Sivan', year: 2023, recents: 'Played 4d ago', liked: true,
    tracks: TRACKS_SHORT([{t:'Got Me Started',d:'3:04',l:1,v:1},{t:'Rush',d:'2:36',v:1},{t:'One of Your Girls',d:'3:43',l:1}]) },
  { id: 'al5', title: 'For Demacia (Original Soundtrack)', artist: 'League of Legends', year: 2026, recents: 'Added May 20, 2026',
    tracks: TRACKS_SHORT([{t:'Salvation',d:'2:51'},{t:'From Greatness',d:'3:12',l:1},{t:'Burden of Honor',d:'2:44'},{t:'Wall of Stone',d:'3:30'},{t:'For Demacia',d:'4:01',l:1},{t:"History's Court",d:'2:58'},{t:'Conquest',d:'3:21'},{t:'The Vanguard',d:'2:39'}]) },
  { id: 'al6', title: 'Trials of Twilight', artist: 'League of Legends', year: 2025, recents: 'Played 2d ago',
    tracks: TRACKS_SHORT([{t:'Twilight Rises',d:'3:02'},{t:'Spirit of the Hunt',d:'2:47',l:1},{t:'Last Light',d:'3:33'}]) },
  { id: 'al7', title: 'Spirit Blossom Beyond', artist: 'League of Legends', year: 2025, recents: 'Played 2d ago',
    tracks: TRACKS_SHORT([{t:'Blossom Path',d:'3:18'},{t:'Beyond',d:'2:55',l:1},{t:'Lantern',d:'3:40'}]) },
  { id: 'al8', title: 'Arcane League of Legends', artist: 'League of Legends', year: 2024, recents: 'Played 5d ago', liked: true,
    tracks: TRACKS_SHORT([{t:'Enemy',d:'2:53',l:1,v:1},{t:'Guns for Hire',d:'3:01'},{t:'Playground',d:'2:31',l:1}]) },
  { id: 'al9', title: 'Hit Me Hard and Soft', artist: 'Billie Eilish', year: 2024, recents: 'Played 6d ago', liked: true,
    tracks: TRACKS_SHORT([{t:'SKINNY',d:'3:40'},{t:'LUNCH',d:'2:59',l:1},{t:'BIRDS OF A FEATHER',d:'3:30',l:1,v:1}]) },
  { id: 'al10', title: 'GUTS', artist: 'Olivia Rodrigo', year: 2023, recents: 'Played 8d ago',
    tracks: TRACKS_SHORT([{t:'all-american bitch',d:'2:44'},{t:'bad idea right?',d:'3:04',l:1},{t:'vampire',d:'3:39',v:1}]) },
  { id: 'al11', title: 'The Secret of Us', artist: 'Gracie Abrams', year: 2024, recents: 'Added Apr 30, 2026',
    tracks: TRACKS_SHORT([{t:'Risk',d:'2:48',l:1},{t:'I Love You, Im Sorry',d:'3:21'},{t:'Close To You',d:'3:09',l:1}]) },
  { id: 'al12', title: 'Short n Sweet', artist: 'Sabrina Carpenter', year: 2024, recents: 'Added Apr 22, 2026', liked: true,
    tracks: TRACKS_SHORT([{t:'Taste',d:'2:37',l:1},{t:'Please Please Please',d:'3:06',v:1},{t:'Espresso',d:'2:55',l:1,v:1}]) },
];

const ARTISTS = [
  { id: 'ar1', name: 'vaultboy', recents: 'Played 10h ago', likedCount: 12, followed: true,
    groups: [
      { album: 'everything sucks', year: 2023, tracks: TRACKS_SHORT([{t:'everything sucks',d:'2:39',l:1},{t:'cigarettes',d:'2:51'},{t:'hometown',d:'3:02',l:1}]) },
      { album: 'singles', year: 2024, tracks: TRACKS_SHORT([{t:'better days',d:'2:48',l:1},{t:'overrated',d:'3:10'}]) },
    ] },
  { id: 'ar2', name: 'League of Legends', recents: 'Played 2d ago', likedCount: 6, followed: true, saved: 1,
    groups: [
      { album: 'For Demacia (Original Soundtrack)', year: 2026, saved: true, tracks: TRACKS_SHORT([{t:'Salvation',d:'2:51'},{t:'From Greatness',d:'3:12',l:1},{t:'For Demacia',d:'4:01',l:1}]) },
      { album: 'Trials of Twilight', year: 2025, tracks: TRACKS_SHORT([{t:'Twilight Rises',d:'3:02'},{t:'Spirit of the Hunt',d:'2:47',l:1}]) },
      { album: 'Arcane League of Legends', year: 2024, tracks: TRACKS_SHORT([{t:'Enemy',d:'2:53',l:1,v:1},{t:'Playground',d:'2:31',l:1}]) },
    ] },
  { id: 'ar3', name: 'Jukjae', recents: 'Played 3d ago', likedCount: 5, followed: true,
    groups: [ { album: 'Reading My Mind', year: 2021, tracks: TRACKS_SHORT([{t:'Reading My Mind',d:'4:02',l:1},{t:'Walk in the Rain',d:'3:48'}]) } ] },
  { id: 'ar4', name: 'In Love With a Ghost', recents: 'Played 5d ago', likedCount: 2, followed: true,
    groups: [ { album: 'lullabies for the brokenhearted', year: 2020, tracks: TRACKS_SHORT([{t:'flowers',d:'2:21',l:1},{t:'we talked all night',d:'3:33',l:1}]) } ] },
  { id: 'ar5', name: 'Lauv', recents: 'Played 6d ago', likedCount: 2, followed: true,
    groups: [ { album: '~how im feeling~', year: 2020, tracks: TRACKS_SHORT([{t:'Modern Loneliness',d:'3:42',l:1},{t:'fuck, im lonely',d:'3:01'},{t:'Mean It',d:'3:13',l:1}]) } ] },
  { id: 'ar6', name: 'Michael Jackson', recents: 'Played 6d ago', likedCount: 9, followed: true,
    groups: [
      { album: 'Thriller', year: 1982, saved: true, tracks: TRACKS_SHORT([{t:'Billie Jean',d:'4:54',l:1},{t:'Beat It',d:'4:18',l:1,v:1},{t:'Thriller',d:'5:57'}]) },
      { album: 'Bad', year: 1987, tracks: TRACKS_SHORT([{t:'Smooth Criminal',d:'4:17',l:1},{t:'Bad',d:'4:07',v:1}]) },
    ] },
  { id: 'ar7', name: 'IVE', recents: 'Played May 20, 2026', likedCount: 3, followed: true,
    groups: [ { album: 'IVE SWITCH', year: 2024, tracks: TRACKS_SHORT([{t:'HEYA',d:'3:01',l:1,v:1},{t:'Accendio',d:'2:58'}]) } ] },
  { id: 'ar8', name: 'Rex Orange County', recents: 'Played May 15, 2026', likedCount: 2, followed: true,
    groups: [ { album: 'Pony', year: 2019, tracks: TRACKS_SHORT([{t:'10/10',d:'2:57',l:1},{t:'Pluto Projector',d:'4:21',l:1}]) } ] },
];

const GENRE_CHIPS = ['All', 'Chill', 'Pop', 'K-Pop', 'Romantic', 'Nostalgia', 'Electronica', 'Love', 'Soft', 'Mellow', 'Quiet', 'Relaxing', 'Slow'];

// Liked songs flat list. tag = which chips it belongs to (besides All).
const LIKED_SONGS = [
  { title: 'number one girl', artist: 'ROSÉ', album: 'rosie', added: 'May 30, 2026', dur: '3:13', video: false, tags: ['Pop','Romantic','Love'] },
  { title: 'APT.', artist: 'ROSÉ, Bruno Mars', album: 'rosie', added: 'May 30, 2026', dur: '2:49', video: true, tags: ['Pop'] },
  { title: 'Black Beauty', artist: 'Tristam', album: 'WLUWD', added: 'May 29, 2026', dur: '3:47', video: false, tags: ['Electronica','Chill'] },
  { title: 'Children in the Dark', artist: 'Tristam', album: 'WLUWD', added: 'May 29, 2026', dur: '3:32', video: false, tags: ['Electronica','Mellow'] },
  { title: 'EYES CLOSED (with ZAYN)', artist: 'JISOO, ZAYN', album: 'AMORTAGE', added: 'May 28, 2026', dur: '3:08', video: true, tags: ['Pop','Love','Romantic'] },
  { title: 'Modern Loneliness', artist: 'Lauv', album: '~how im feeling~', added: 'May 26, 2026', dur: '3:42', video: false, tags: ['Pop','Nostalgia','Mellow'] },
  { title: 'flowers', artist: 'In Love With a Ghost', album: 'lullabies', added: 'May 24, 2026', dur: '2:21', video: false, tags: ['Chill','Soft','Quiet','Relaxing'] },
  { title: 'HEYA', artist: 'IVE', album: 'IVE SWITCH', added: 'May 22, 2026', dur: '3:01', video: true, tags: ['K-Pop','Pop'] },
  { title: 'Pluto Projector', artist: 'Rex Orange County', album: 'Pony', added: 'May 20, 2026', dur: '4:21', video: false, tags: ['Soft','Nostalgia','Slow','Mellow'] },
  { title: 'Reading My Mind', artist: 'Jukjae', album: 'Reading My Mind', added: 'May 18, 2026', dur: '4:02', video: false, tags: ['Chill','Soft','Relaxing','Slow'] },
  { title: 'BIRDS OF A FEATHER', artist: 'Billie Eilish', album: 'Hit Me Hard and Soft', added: 'May 16, 2026', dur: '3:30', video: true, tags: ['Pop','Love','Romantic'] },
  { title: 'Risk', artist: 'Gracie Abrams', album: 'The Secret of Us', added: 'May 14, 2026', dur: '2:48', video: false, tags: ['Pop','Soft'] },
  { title: 'Taste', artist: 'Sabrina Carpenter', album: 'Short n Sweet', added: 'May 12, 2026', dur: '2:37', video: true, tags: ['Pop'] },
  { title: 'Enemy', artist: 'Imagine Dragons, JID', album: 'Arcane', added: 'May 10, 2026', dur: '2:53', video: true, tags: ['Pop','Electronica'] },
  { title: 'Beat It', artist: 'Michael Jackson', album: 'Thriller', added: 'May 8, 2026', dur: '4:18', video: true, tags: ['Pop','Nostalgia'] },
  { title: 'we talked all night', artist: 'In Love With a Ghost', album: 'lullabies', added: 'May 6, 2026', dur: '3:33', video: false, tags: ['Chill','Quiet','Relaxing','Soft'] },
  { title: 'Mean It', artist: 'Lauv, LANY', album: '~how im feeling~', added: 'May 4, 2026', dur: '3:13', video: false, tags: ['Pop','Romantic','Love'] },
  { title: 'everything sucks', artist: 'vaultboy', album: 'everything sucks', added: 'May 2, 2026', dur: '2:39', video: false, tags: ['Pop','Nostalgia','Mellow'] },
];

const PODCASTS = [
  { id: 'pc1', show: 'Huberman Lab', publisher: 'Scicomm Media', followed: true, savedCount: 0,
    episodes: [
      { title: 'Essentials: The Science & Process of Healing from Grief', date: 'May 28, 2026', dur: '39:19', progress: 0, video: true, desc: 'In this Huberman Lab Essentials episode, I explain the neuroscience of grief, including how the brain maps relationships across three dimensions — space, time, and closeness — and why losing someone requires a remapping of those neural circuits.' },
      { title: 'Build Muscle, Lose Fat & Optimize Recovery', date: 'May 24, 2026', dur: '2:16:49', progress: 0, video: true, desc: 'A deep dive into resistance training, hypertrophy, and recovery science with practical protocols.' },
      { title: 'Essentials: How Hormones Control Hunger', date: 'May 21, 2026', dur: '32:45', progress: 0.17, video: true, desc: 'How leptin, ghrelin, and insulin regulate appetite and what you can do about it.' },
      { title: 'How to Overcome Procrastination', date: 'May 17, 2026', dur: '2:30:24', progress: 0, video: true, desc: 'The dopamine and prefrontal-cortex basis of procrastination, plus tools to beat it.' },
      { title: 'Essentials: Master Your Sleep', date: 'May 14, 2026', dur: '37:21', progress: 0, video: true, desc: 'Light, temperature, and timing — the levers that set your circadian rhythm.' },
    ] },
  { id: 'pc2', show: 'ColdFusion', publisher: 'ColdFusion', followed: true, savedCount: 0,
    episodes: [
      { title: 'The Rise and Fall of a Tech Giant', date: 'May 27, 2026', dur: '28:11', progress: 0, video: false, desc: 'A documentary look at a once-dominant company and the decisions that undid it.' },
      { title: 'How AI Actually Works', date: 'May 20, 2026', dur: '24:03', progress: 0.42, video: false, desc: 'A grounded explanation of modern machine learning, minus the hype.' },
    ] },
  { id: 'pc3', show: 'Hey Tablo', publisher: 'Team Epikase', followed: true, savedCount: 0,
    episodes: [ { title: 'On Writing and Doubt', date: 'May 19, 2026', dur: '41:32', progress: 0, video: false, desc: 'Tablo on the creative process and living with self-doubt.' } ] },
  { id: 'pc4', show: 'Patrick Boyle On Finance', publisher: 'Patrick Boyle', followed: true, savedCount: 0,
    episodes: [ { title: 'What the Markets Got Wrong', date: 'May 25, 2026', dur: '33:50', progress: 0, video: false, desc: 'A dry-witted breakdown of recent market mispricing.' } ] },
  { id: 'pc5', show: 'The Pog State', publisher: 'Riot Games Korea', followed: true, savedCount: 0,
    episodes: [ { title: 'Worlds Recap', date: 'May 23, 2026', dur: '58:14', progress: 0, video: true, desc: 'Breaking down the biggest moments of the tournament.' } ] },
  { id: 'pc6', show: 'The Checkup with Doctor Mike', publisher: 'DM Operations Inc.', followed: true, savedCount: 0,
    episodes: [ { title: 'Myths About Sleep', date: 'May 21, 2026', dur: '47:09', progress: 0, video: true, desc: 'Debunking common sleep misconceptions with evidence.' } ] },
  { id: 'pc7', show: 'The Brave Technologist', publisher: 'Brave', followed: true, savedCount: 2,
    episodes: [ { title: 'Privacy in the Age of AI', date: 'May 18, 2026', dur: '36:22', progress: 0, video: false, desc: 'What data really powers AI and how to keep yours.' } ] },
];

window.MOCK = { cover, ALBUMS, ARTISTS, GENRE_CHIPS, LIKED_SONGS, PODCASTS };
