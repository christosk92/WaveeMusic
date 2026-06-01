/* Library rework prototype — interactions.
   Renders the unified toolbar + master/detail (or table) per tab, wires
   selection (cross-fade), source toggle, sort/view popover, view modes,
   and chip + free-text filtering. Icons are Segoe Fluent Icons via numeric
   character references (no raw PUA literals → reliable round-trip). */

const M = window.MOCK;

/* Segoe Fluent Icons codepoints */
const G = {
  play: '&#xE768;', shuffle: '&#xE8B1;', prev: '&#xE892;', next: '&#xE893;',
  eye: '&#xE7B3;', open: '&#xE8A7;', heart: '&#xEB52;', heartOff: '&#xEB51;',
  check: '&#xE73E;', star: '&#xE734;', sort: '&#xE8CB;', chev: '&#xE70D;',
  filter: '&#xE71C;', link: '&#xE71B;', music: '&#xE8D6;', video: '&#xE714;',
  clock: '&#xE917;', search: '&#xE721;', add: '&#xE710;',
  vClist: '&#xE8FD;', vList: '&#xEA37;', vCgrid: '&#xE80A;', vGrid: '&#xF0E2;',
};

function hue(str) { let h = 0; for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) % 360; return h; }
function glow(name) { return `hsl(${hue(name)} 70% 45% / 0.20)`; }
function art(name, cls) { const c = M.cover(name); return `<div class="${cls}" style="${c.style}"><span class="ini">${c.label}</span></div>`; }
const heart = (on) => `<span class="gi ${on ? 'on' : ''}">${on ? G.heart : G.heartOff}</span>`;
const esc = (s) => (s || '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const SORTS = {
  albums: ['Recents', 'Recently added', 'Alphabetical', 'Creator', 'Release date'],
  artists: ['Recents', 'Recently added', 'Alphabetical'],
  podcasts: ['Recents', 'Recently added', 'Alphabetical'],
  liked: ['Date added', 'Title', 'Artist', 'Album'],
};
const VIEWS = [
  { k: 'clist', g: G.vClist, label: 'Compact list' },
  { k: 'list', g: G.vList, label: 'List' },
  { k: 'cgrid', g: G.vCgrid, label: 'Compact grid' },
  { k: 'grid', g: G.vGrid, label: 'Grid' },
];

const TABS = [
  { key: 'albums', label: 'Albums', art: 'square', source: true, view: true },
  { key: 'artists', label: 'Artists', art: 'circle', source: true, view: true },
  { key: 'liked', label: 'Liked Songs', table: true },
  { key: 'podcasts', label: 'Podcasts', art: 'square', source: false, view: false, masterList: true },
];

const state = {
  tab: 'albums',
  albums: { source: 'saved', view: 'grid', sort: 'Recents', sel: 'al1', filter: '', detailMode: 'liked' },
  artists: { source: 'saved', view: 'grid', sort: 'Recents', sel: 'ar2', filter: '', disco: 'list' },
  podcasts: { source: 'latest', view: 'list', sort: 'Recents', sel: 'pc1', filter: '' },
  liked: { sort: 'Date added', chip: 'All', filter: '', video: false },
};

let openPopover = null;
const $ = (sel, root = document) => root.querySelector(sel);
const tabsEl = $('#tabs'), toolbarEl = $('#toolbar'), bodyEl = $('#tabBody');

/* ---------------- top-level render ---------------- */
function render() { renderTabs(); renderToolbar(); renderBody(); }

function renderTabs() {
  tabsEl.innerHTML = TABS.map((t) =>
    `<button class="tab ${state.tab === t.key ? 'active' : ''}" data-tab="${t.key}">${t.label}</button>`).join('');
}

function renderToolbar() {
  const tab = TABS.find((t) => t.key === state.tab);
  const s = state[state.tab];
  let html = '';

  if (tab.source) {
    const src = s.source;
    html += `<div class="segmented" data-role="source">
      <button class="seg ${src === 'saved' ? 'active' : ''}" data-src="saved"><span class="gi">${G.check}</span>Saved</button>
      <button class="seg ${src === 'liked' ? 'active' : ''}" data-src="liked"><span class="gi">${G.heart}</span>From Liked Songs</button>
    </div>`;
  }

  const sortOnly = tab.table;
  const curView = VIEWS.find((v) => v.k === (s.view || 'grid')) || VIEWS[3];
  html += `<div class="sortview" data-role="sortview">
      <span class="lead"><span class="gi">${G.sort}</span>${s.sort}</span>
      <span class="chev gi">${G.chev}</span>
      ${sortOnly ? '' : `<span class="vsep"></span><span class="vmode"><span class="gi">${curView.g}</span></span>`}
    </div>`;

  if (state.tab === 'podcasts') {
    html += `<button class="tool-btn" data-role="browse"><span class="gi">${G.link}</span>Browse podcasts</button>`;
  }

  const ph = state.tab === 'albums' ? 'Filter albums…'
    : state.tab === 'artists' ? 'Filter artists…'
      : state.tab === 'podcasts' ? 'Filter podcasts…' : 'Filter songs…';
  html += `<label class="filter"><span class="gi">${G.filter}</span>
      <input type="text" data-role="filter" placeholder="${ph}" value="${esc(s.filter)}" /></label>`;

  toolbarEl.innerHTML = html;
}

function renderBody() {
  const tab = TABS.find((t) => t.key === state.tab);
  if (tab.table) return renderLiked();
  bodyEl.innerHTML = `
    <div class="masterdetail">
      <div class="master" id="master"></div>
      <div class="splitter"></div>
      <div class="detail-wrap"><div class="detail" id="detail"></div></div>
    </div>`;
  renderMaster();
  renderDetail(true);
}

/* ---------------- master (grid/list) ---------------- */
function items() {
  if (state.tab === 'albums') return M.ALBUMS;
  if (state.tab === 'artists') return M.ARTISTS;
  if (state.tab === 'podcasts') return M.PODCASTS;
  return [];
}
function nameOf(it) { return it.title || it.name || it.show; }
function matchesFilter(it) {
  const f = state[state.tab].filter.trim().toLowerCase();
  if (!f) return true;
  return (nameOf(it) + ' ' + (it.artist || it.publisher || '')).toLowerCase().includes(f);
}

function renderMaster() {
  const tab = TABS.find((t) => t.key === state.tab);
  const s = state[state.tab];
  const master = $('#master'); if (!master) return;
  const list = items().filter(matchesFilter);
  const view = tab.masterList ? 'list' : s.view;
  const circle = tab.art === 'circle';

  let inner;
  if (view === 'grid' || view === 'cgrid') {
    inner = `<div class="grid ${view === 'cgrid' ? 'compact' : 'def'}">` + list.map((it, i) => {
      const sub = it.artist || it.publisher || '';
      const badge = state.tab === 'artists' ? `${it.likedCount} liked songs` : (it.recents || '');
      return `<div class="card ${circle ? 'circle-card' : ''} ${s.sel === it.id ? 'sel' : ''}" data-id="${it.id}" style="animation-delay:${Math.min(i * 26, 360)}ms">
        ${art(nameOf(it), 'card-art ' + (circle ? 'circle' : ''))}
        <button class="play-fab gi">${G.play}</button>
        <div class="card-title">${esc(nameOf(it))}</div>
        ${sub ? `<div class="card-sub">${esc(sub)}</div>` : ''}
        ${badge ? `<div class="card-badge">${esc(badge)}</div>` : ''}
      </div>`;
    }).join('') + `</div>`;
  } else {
    inner = `<div class="list ${view === 'clist' ? 'compact' : ''}">` + list.map((it, i) => {
      const sub = it.artist || it.publisher || (state.tab === 'artists' ? `${it.likedCount} liked songs` : '');
      return `<div class="lrow ${s.sel === it.id ? 'sel' : ''}" data-id="${it.id}" style="animation-delay:${Math.min(i * 22, 300)}ms">
        ${art(nameOf(it), 'lrow-art ' + (circle ? 'circle' : ''))}
        <div class="lrow-main"><div class="lrow-title">${esc(nameOf(it))}</div>${sub ? `<div class="lrow-sub">${esc(sub)}</div>` : ''}</div>
        ${it.recents ? `<div class="lrow-badge">${esc(it.recents)}</div>` : ''}
      </div>`;
    }).join('') + `</div>`;
  }
  master.innerHTML = inner || `<div class="detail-empty"><span class="gi">${G.search}</span><div class="et">No matches</div></div>`;
}

/* ---------------- shared detail panel ---------------- */
function selected() { const s = state[state.tab]; return items().find((it) => it.id === s.sel); }
function actionBtn(a) {
  const cls = ['act'];
  if (a.primary) cls.push('primary');
  if (a.icon) cls.push('icon');
  if (a.toggle) { cls.push('toggle'); if (a.on) cls.push('on'); }
  const g = a.glyph ? `<span class="gi ${a.likeOn ? 'like-on' : ''}">${a.glyph}</span>` : '';
  return `<button class="${cls.join(' ')}">${g}${a.label ? esc(a.label) : ''}</button>`;
}
function trackRows(tracks, showHeader) {
  return `<div class="tlist">
    ${showHeader ? `<div class="thead"><div>#</div><div>Title</div><div class="gi">${G.clock}</div></div>` : ''}
    ${tracks.map((t) => `<div class="trow">
      <div class="tn">${t.n}</div>
      <div class="theart ${t.liked ? 'on' : ''}">${heart(t.liked)}</div>
      <div class="ttitle"><span class="nm">${esc(t.title)}</span>${t.video ? `<span class="vbadge gi">${G.video}</span>` : ''}</div>
      <div class="tdur">${t.dur}</div>
    </div>`).join('')}
  </div>`;
}

function renderDetail(fade) {
  const detail = $('#detail'); if (!detail) return;
  const tab = TABS.find((t) => t.key === state.tab);
  const s = state[state.tab];
  const it = selected();
  detail.style.setProperty('--hero-glow', it ? glow(nameOf(it)) : 'transparent');

  if (!it) {
    detail.innerHTML = `<div class="detail-empty"><span class="gi">${G.music}</span><div class="et">Select an item to see details</div></div>`;
    return;
  }

  let hero, actions, subtoggle = '', content;
  const circle = tab.art === 'circle';
  const heroArt = art(nameOf(it), 'hero-art ' + (circle ? 'circle' : ''));

  if (state.tab === 'albums') {
    const liked = s.source === 'liked';
    hero = `<div class="hero">${heroArt}<div class="hero-info">
      <div class="hero-kicker">ALBUM</div>
      <div class="hero-title">${esc(it.title)}</div>
      <div class="hero-sub"><a href="#">${esc(it.artist)}</a></div>
      <div class="hero-meta">${it.year} · ${it.tracks.length} tracks · ${esc(it.recents)}</div>
    </div></div>`;
    actions = [
      { primary: true, glyph: G.play, label: liked ? 'Play liked' : 'Play' },
      { glyph: G.shuffle, label: 'Shuffle' },
      { glyph: liked ? G.open : G.eye, label: liked ? 'Open album' : 'View album' },
      liked ? null : { toggle: true, on: it.liked, glyph: G.heart, likeOn: it.liked, label: it.liked ? 'Unheart' : 'Heart' },
    ].filter(Boolean);
    if (liked) {
      subtoggle = `<div class="subtoggle"><div class="segmented block" data-role="detailmode">
        <button class="seg ${s.detailMode === 'liked' ? 'active' : ''}" data-mode="liked">Liked tracks</button>
        <button class="seg ${s.detailMode === 'full' ? 'active' : ''}" data-mode="full">Full album</button>
      </div></div>`;
      const tks = s.detailMode === 'liked' ? it.tracks.filter((t) => t.liked) : it.tracks;
      content = trackRows((tks.length ? tks : it.tracks).map((t, i) => ({ ...t, n: i + 1 })), true);
    } else {
      content = trackRows(it.tracks, true);
    }
  } else if (state.tab === 'artists') {
    const liked = s.source === 'liked';
    hero = `<div class="hero">${heroArt}<div class="hero-info">
      <div class="hero-kicker">ARTIST</div>
      <div class="hero-title">${esc(it.name)}</div>
      <div class="hero-meta">${esc(it.recents)} · ${it.likedCount} liked songs</div>
    </div></div>`;
    actions = [
      { primary: true, glyph: G.play, label: 'Play' },
      { glyph: G.shuffle, label: 'Shuffle' },
      { glyph: liked ? G.open : G.eye, label: liked ? 'Open artist' : 'View artist' },
      liked ? null : { toggle: true, on: it.followed, glyph: G.check, label: it.followed ? 'Following' : 'Follow' },
      liked ? null : { toggle: true, on: false, glyph: G.star, label: `Saved only (${it.saved || 0})` },
    ].filter(Boolean);
    if (liked) {
      const flat = it.groups.flatMap((g) => g.tracks).filter((t) => t.liked).map((t, i) => ({ ...t, n: i + 1 }));
      content = trackRows(flat, true);
    } else {
      content = it.groups.map((g, gi) => {
        const body = state.artists.disco === 'grid'
          ? `<div class="disco-grid">${g.tracks.map((t) => `<div class="card">${art(t.title, 'card-art')}<div class="card-title">${esc(t.title)}</div><div class="card-sub">${t.dur}</div></div>`).join('')}</div>`
          : trackRows(g.tracks.map((t, i) => ({ ...t, n: i + 1 })), false);
        return `<div class="group ${gi > 1 ? 'collapsed' : ''}" data-group="${gi}">
          <div class="group-head">
            <span class="gtoggle gi">${G.chev}</span>
            ${art(g.album, 'group-art')}
            <span class="group-name">${esc(g.album)}</span>
            <span class="group-year">· ${g.year}</span>
            ${g.saved ? `<span class="group-saved"><span class="gi">${G.check}</span>Saved</span>` : ''}
          </div>
          <div class="group-body">${body}</div>
        </div>`;
      }).join('');
    }
  } else if (state.tab === 'podcasts') {
    hero = `<div class="hero">${heroArt}<div class="hero-info">
      <div class="hero-kicker">PODCAST</div>
      <div class="hero-title">${esc(it.show)}</div>
      <div class="hero-sub"><a href="#">${esc(it.publisher)}</a></div>
      <div class="hero-meta">${it.episodes.length} episodes · Followed</div>
    </div></div>`;
    actions = [
      { primary: true, glyph: G.play, label: 'Play' },
      { glyph: G.shuffle, label: 'Shuffle' },
      { glyph: G.open, label: 'Open show' },
    ];
    subtoggle = `<div class="subtoggle"><div class="segmented block" data-role="scope">
      <button class="seg ${state.podcasts.source === 'saved' ? 'active' : ''}" data-scope="saved">Saved</button>
      <button class="seg ${state.podcasts.source === 'latest' ? 'active' : ''}" data-scope="latest">Latest</button>
    </div></div>`;
    content = `<div class="tlist">${it.episodes.map((e, i) => `<div class="trow" style="grid-template-columns:28px 24px 1fr auto">
        <div class="tn">${i + 1}</div>
        <div class="theart">${heart(false)}</div>
        <div class="ttitle" style="flex-direction:column;align-items:flex-start;gap:3px">
          <div style="display:flex;align-items:center;gap:8px;width:100%"><span class="nm">${esc(e.title)}</span>${e.video ? `<span class="vbadge gi">${G.video}</span>` : ''}</div>
          <div class="lrow-sub">${e.date} · ${e.dur}${e.progress ? ' · ' + Math.round(e.progress * 100) + '% played' : ''}</div>
          ${e.progress ? `<div class="ep-progress" style="width:160px"><i style="width:${e.progress * 100}%"></i></div>` : ''}
        </div>
        <div class="tdur">${e.dur}</div>
      </div>`).join('')}</div>`;
  }

  detail.classList.remove('fade');
  detail.innerHTML = hero + `<div class="actions">${actions.map(actionBtn).join('')}</div>` + subtoggle + `<div class="detail-content">${content}</div>`;
  if (fade) { void detail.offsetWidth; detail.classList.add('fade'); }
}

/* ---------------- Liked Songs (table) ---------------- */
function renderLiked() {
  const s = state.liked;
  let songs = M.LIKED_SONGS.slice();
  if (s.chip !== 'All') songs = songs.filter((x) => x.tags.includes(s.chip));
  if (s.video) songs = songs.filter((x) => x.video);
  const f = s.filter.trim().toLowerCase();
  if (f) songs = songs.filter((x) => (x.title + ' ' + x.artist + ' ' + x.album).toLowerCase().includes(f));
  const sorters = {
    Title: (a, b) => a.title.localeCompare(b.title),
    Artist: (a, b) => a.artist.localeCompare(b.artist),
    Album: (a, b) => a.album.localeCompare(b.album),
    'Date added': () => 0,
  };
  songs.sort(sorters[s.sort] || (() => 0));
  const totalMin = M.LIKED_SONGS.length * 3;

  const chips = M.GENRE_CHIPS.map((c) => `<button class="chip ${s.chip === c ? 'active' : ''}" data-chip="${c}">${c}</button>`).join('');
  const rows = songs.map((x, i) => `<div class="bigrow" style="animation:rise .4s cubic-bezier(.2,.8,.2,1) both;animation-delay:${Math.min(i * 16, 240)}ms">
      <div class="tn">${i + 1}</div>
      <div class="theart on">${heart(true)}</div>
      <div class="c-title">${art(x.album, 'art')}<div class="tx"><div class="nm">${esc(x.title)}</div><div class="ar">${esc(x.artist)}</div></div></div>
      <div class="muted">${esc(x.album)}</div>
      <div class="muted">${x.added}</div>
      <div class="dur">${x.video ? `<span class="vbadge gi">${G.video}</span>` : ''}${x.dur}</div>
    </div>`).join('');

  bodyEl.innerHTML = `<div class="liked-wrap">
    <div class="chips">${chips}</div>
    <div class="liked-actions">
      <button class="act primary" style="height:40px"><span class="gi">${G.play}</span>Play</button>
      <button class="act" style="height:40px"><span class="gi">${G.shuffle}</span>Shuffle</button>
      <span class="liked-stats">${songs.length} songs · ${Math.floor(totalMin / 60)} hr ${totalMin % 60} min</span>
      <button class="tool-btn" data-role="videoonly" style="margin-left:auto;${s.video ? 'background:var(--accent);border-color:var(--accent);color:var(--accent-ink)' : ''}"><span class="gi">${G.video}</span>Video only</button>
    </div>
    <div class="liked-table">
      <div class="bigrow bighead"><div class="tn">#</div><div></div><div>Title</div><div>Album</div><div>Date added</div><div class="dur gi">${G.clock}</div></div>
      ${rows || `<div class="detail-empty"><span class="gi">${G.search}</span><div class="et">No songs match</div></div>`}
    </div>
  </div>`;
}

/* ---------------- popover (sort + view) ---------------- */
function openSortView(anchor) {
  closePopover();
  const tab = TABS.find((t) => t.key === state.tab);
  const s = state[state.tab];
  const sortKeys = SORTS[state.tab] || [];
  const sortOnly = !!tab.table;
  const cur = s.view || 'grid';
  const showSize = !sortOnly && (cur === 'grid' || cur === 'cgrid');

  const pop = document.createElement('div');
  pop.className = 'popover';
  pop.innerHTML = `
    <div class="pop-label">Sort by</div>
    ${sortKeys.map((k) => `<button class="pop-row ${s.sort === k ? 'sel' : ''}" data-sort="${k}">
      <span class="check gi">${G.check}</span>${k}<span class="dir gi">${G.chev}</span></button>`).join('')}
    ${sortOnly ? '' : `<div class="pop-sep"></div><div class="pop-label">View as</div>
      <div class="pop-views">${VIEWS.map((v) => `<button class="vtoggle ${cur === v.k ? 'active' : ''}" data-view="${v.k}" title="${v.label}"><span class="gi">${v.g}</span></button>`).join('')}</div>
      ${showSize ? `<div class="pop-size"><input type="range" min="0.6" max="1.6" step="0.05" value="1"></div>` : ''}`}
  `;
  anchor.appendChild(pop);
  openPopover = pop;
}
function closePopover() { if (openPopover) { openPopover.remove(); openPopover = null; } }

/* ---------------- events ---------------- */
tabsEl.addEventListener('click', (e) => {
  const b = e.target.closest('[data-tab]'); if (!b) return;
  state.tab = b.dataset.tab; closePopover(); render();
});

toolbarEl.addEventListener('click', (e) => {
  const src = e.target.closest('[data-src]');
  if (src) { state[state.tab].source = src.dataset.src; state[state.tab].sel = items()[0]?.id; renderToolbar(); renderBody(); return; }
  const sv = e.target.closest('[data-role="sortview"]');
  if (sv) { if (openPopover && openPopover.parentElement === sv) closePopover(); else openSortView(sv); return; }
});
toolbarEl.addEventListener('input', (e) => {
  const f = e.target.closest('[data-role="filter"]');
  if (f) { state[state.tab].filter = f.value; state.tab === 'liked' ? renderLiked() : renderMaster(); }
});

document.addEventListener('click', (e) => {
  if (!openPopover) return;
  if (openPopover.contains(e.target)) {
    const so = e.target.closest('[data-sort]');
    if (so) { state[state.tab].sort = so.dataset.sort; closePopover(); renderToolbar(); state.tab === 'liked' ? renderLiked() : renderMaster(); return; }
    const vw = e.target.closest('[data-view]');
    if (vw) { state[state.tab].view = vw.dataset.view; if (state.tab === 'artists') state.artists.disco = (vw.dataset.view === 'grid' || vw.dataset.view === 'cgrid') ? 'grid' : 'list'; closePopover(); renderToolbar(); renderBody(); return; }
    return;
  }
  if (!e.target.closest('[data-role="sortview"]')) closePopover();
});

bodyEl.addEventListener('click', (e) => {
  const card = e.target.closest('[data-id]');
  if (card && !e.target.closest('.play-fab')) {
    state[state.tab].sel = card.dataset.id;
    document.querySelectorAll('#master .sel').forEach((n) => n.classList.remove('sel'));
    card.classList.add('sel');
    renderDetail(true);
    return;
  }
  const dm = e.target.closest('[data-mode]');
  if (dm) { state.albums.detailMode = dm.dataset.mode; renderDetail(false); return; }
  const sc = e.target.closest('[data-scope]');
  if (sc) { state.podcasts.source = sc.dataset.scope; renderDetail(false); return; }
  const gh = e.target.closest('.group-head');
  if (gh) { gh.parentElement.classList.toggle('collapsed'); return; }
  const th = e.target.closest('.theart');
  if (th) { const on = !th.classList.contains('on'); th.classList.toggle('on', on); th.innerHTML = heart(on); return; }
  const act = e.target.closest('.act.toggle');
  if (act) { act.classList.toggle('on'); return; }
  const chip = e.target.closest('[data-chip]');
  if (chip) { state.liked.chip = chip.dataset.chip; renderLiked(); return; }
  const vo = e.target.closest('[data-role="videoonly"]');
  if (vo) { state.liked.video = !state.liked.video; renderLiked(); return; }
});

/* Ctrl+F focuses the filter pill (mirrors the app's IInPageFilterable) */
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') {
    const inp = toolbarEl.querySelector('[data-role="filter"]');
    if (inp) { e.preventDefault(); inp.focus(); }
  }
  if (e.key === 'Escape') closePopover();
});

render();
