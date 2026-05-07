// Wavee Home — Klankhuis hero-led redesign prototype
// Slide cycling + page accent lerp. Vanilla JS, ~60 lines.

const slides = [
    {
        eyebrow: "PICK UP WHERE YOU LEFT OFF",
        title: "i,i",
        tagline: "Bon Iver · Paused 14 minutes ago at “Hey, Ma”",
        cta1: "Resume",
        cta2: "Open album",
        accent: "#F59E0B",
        cover: "linear-gradient(135deg, #FCD34D 0%, #DC2626 100%)"
    },
    {
        eyebrow: "MADE FOR YOU",
        title: "Daily Mix 1",
        tagline: "Bon Iver, Phoebe Bridgers, Sufjan Stevens and more",
        cta1: "Play",
        cta2: "Open mix",
        accent: "#3B82F6",
        cover: "linear-gradient(135deg, #93C5FD 0%, #1E3A8A 100%)"
    },
    {
        eyebrow: "JUMP BACK IN",
        title: "Punisher",
        tagline: "Phoebe Bridgers · You’ve played this album 4 times this month",
        cta1: "Play",
        cta2: "Open album",
        accent: "#A855F7",
        cover: "linear-gradient(135deg, #E9D5FF 0%, #581C87 100%)"
    },
    {
        eyebrow: "NEW RELEASES FOR YOU",
        title: "Manning Fireworks",
        tagline: "MJ Lenderman · Out today",
        cta1: "Play",
        cta2: "Open album",
        accent: "#10B981",
        cover: "linear-gradient(135deg, #6EE7B7 0%, #064E3B 100%)"
    },
    {
        eyebrow: "BASED ON YOUR FAVORITES",
        title: "Daily Mix 3",
        tagline: "Big Thief, Adrianne Lenker, Mount Eerie and more",
        cta1: "Play",
        cta2: "Open mix",
        accent: "#F43F5E",
        cover: "linear-gradient(135deg, #FDA4AF 0%, #881337 100%)"
    },
    {
        eyebrow: "NEW EVERY MONDAY",
        title: "Discover Weekly",
        tagline: "30 hand-picked tracks, refreshed for you",
        cta1: "Play",
        cta2: "Open playlist",
        accent: "#6366F1",
        cover: "linear-gradient(135deg, #A5B4FC 0%, #1E1B4B 100%)"
    }
];

const carousel = document.getElementById("carousel");
const pips = document.getElementById("pips");
const prevBtn = document.getElementById("prevBtn");
const nextBtn = document.getElementById("nextBtn");

slides.forEach((s, i) => {
    const slide = document.createElement("div");
    slide.className = "slide";
    slide.style.setProperty("--accent", s.accent);
    slide.innerHTML = `
        <div class="slide-backdrop"></div>
        <div class="slide-noise"></div>
        <div class="slide-vignette"></div>
        <div class="slide-content">
            <div class="slide-text">
                <span class="slide-eyebrow">${s.eyebrow}</span>
                <h2 class="slide-title">${s.title}</h2>
                <p class="slide-tagline">${s.tagline}</p>
                <div class="slide-ctas">
                    <button class="cta cta-primary">${s.cta1}</button>
                    <button class="cta cta-secondary">${s.cta2}</button>
                </div>
            </div>
            <div class="slide-cover" style="--cover:${s.cover};"></div>
        </div>`;
    carousel.appendChild(slide);

    const pip = document.createElement("button");
    pip.className = "pip" + (i === 0 ? " pip-active" : "");
    pip.setAttribute("role", "tab");
    pip.setAttribute("aria-label", `Slide ${i + 1}: ${s.title}`);
    pip.addEventListener("click", () => goTo(i, true));
    pips.appendChild(pip);
});

let currentIndex = 0;
let autoplayTimer = null;
const AUTOPLAY_MS = 7000;

function goTo(i, manual) {
    currentIndex = (i + slides.length) % slides.length;
    carousel.style.transform = `translateX(-${currentIndex * 100}%)`;
    document.querySelectorAll(".pip").forEach((p, idx) => {
        p.classList.toggle("pip-active", idx === currentIndex);
    });
    document.documentElement.style.setProperty("--page-accent", slides[currentIndex].accent);
    if (manual) restartAutoplay();
}

function startAutoplay() {
    autoplayTimer = setInterval(() => goTo(currentIndex + 1, false), AUTOPLAY_MS);
}
function restartAutoplay() {
    if (autoplayTimer) clearInterval(autoplayTimer);
    startAutoplay();
}

prevBtn.addEventListener("click", () => goTo(currentIndex - 1, true));
nextBtn.addEventListener("click", () => goTo(currentIndex + 1, true));

document.documentElement.style.setProperty("--page-accent", slides[0].accent);
startAutoplay();

// ---------- Chip filter: hide/show regions ----------
const chipBtns = document.querySelectorAll(".chip");
const allRegions = () => document.querySelectorAll(".region");

const chipToRegions = {
    all:        ["recents", "madeforyou", "discover", "podcasts"],
    music:      ["recents", "madeforyou", "discover"],
    podcasts:   ["podcasts"],
    audiobooks: []
};

chipBtns.forEach(btn => {
    btn.addEventListener("click", () => {
        chipBtns.forEach(b => b.classList.remove("chip-active"));
        btn.classList.add("chip-active");
        const filter = btn.dataset.chip || "all";
        const visibleKinds = chipToRegions[filter] || chipToRegions.all;
        allRegions().forEach(region => {
            const kind = region.dataset.region;
            region.classList.toggle("is-hidden", !visibleKinds.includes(kind));
        });
    });
});

// ---------- Browse all genre selector ----------
// Real dataset from the Pathfinder browseAll persistedQuery response. 68 items.
const browseItems = [
    { label: "Music", hex: "#1e3264", uri: "spotify:page:0JQ5DAqbMKFSi39LMRT0Cy" },
    { label: "Podcasts", hex: "#006450", uri: "spotify:page:0JQ5DArNBzkmxXHCqFLx2J" },
    { label: "Audiobooks", hex: "#1e3264", uri: "spotify:page:0JQ5DAqbMKFETqK4t8f1n3" },
    { label: "Live Events", hex: "#8400e7", uri: "spotify:xlink:0JQ5DAozXW0GUBAKjHsifL" },
    { label: "Made For You", hex: "#1e3264", uri: "spotify:page:0JQ5DAt0tbjZptfcdMSKl3" },
    { label: "New Releases", hex: "#608108", uri: "spotify:page:0JQ5DAqbMKFz6FAsUtgAab" },
    { label: "Fitness", hex: "#777777", uri: "spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx" },
    { label: "Pop", hex: "#477d95", uri: "spotify:page:0JQ5DAqbMKFEC4WFtoNRpw" },
    { label: "Hip-Hop", hex: "#477d95", uri: "spotify:page:0JQ5DAqbMKFQ00XGBls6ym" },
    { label: "Dance/Electronic", hex: "#af2896", uri: "spotify:page:0JQ5DAqbMKFHOzuVTgTizF" },
    { label: "Dutch music", hex: "#e61e32", uri: "spotify:page:0JQ5DAqbMKFCLroFGPFVr5" },
    { label: "Charts", hex: "#8d67ab", uri: "spotify:page:0JQ5DAudkNjCgYMM0TZXDw" },
    { label: "Podcast Charts", hex: "#0d73ec", uri: "spotify:page:0JQ5DAB3zgCauRwnvdEQjJ" },
    { label: "Video Podcasts", hex: "#bc5900", uri: "spotify:page:0JQ5DAqbMKFRKgqLjIIZq4" },
    { label: "Lifestyle & Health", hex: "#8d67ab", uri: "spotify:page:0JQ5DAqbMKFyehRoixyYKI" },
    { label: "Mystery & Thriller", hex: "#e8115b", uri: "spotify:page:0JQ5DAqbMKFHF46W0MwaAA" },
    { label: "Fiction & Literature", hex: "#477d95", uri: "spotify:page:0JQ5DAqbMKFN0lmhry1LfG" },
    { label: "Self-Help", hex: "#27856a", uri: "spotify:page:0JQ5DAqbMKFJ4DMqAKdPAs" },
    { label: "Mood", hex: "#006450", uri: "spotify:page:0JQ5DAqbMKFzHmL4tf05da" },
    { label: "Indie", hex: "#a56752", uri: "spotify:page:0JQ5DAqbMKFCWjUTdzaG0e" },
    { label: "Party", hex: "#8d67ab", uri: "spotify:page:0JQ5DAqbMKFA6SOHvT3gck" },
    { label: "In the car", hex: "#2d46b9", uri: "spotify:page:0JQ5DAqbMKFIRybaNTYXXy" },
    { label: "Discover", hex: "#8d67ab", uri: "spotify:page:0JQ5DAtOnAEpjOgUKwXyxj" },
    { label: "R&B", hex: "#ba5d07", uri: "spotify:page:0JQ5DAqbMKFEZPnFQSFB1T" },
    { label: "Workout Music", hex: "#777777", uri: "spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4" },
    { label: "GLOW", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFGnsSfvg90Wo" },
    { label: "Afro", hex: "#8c1932", uri: "spotify:page:0JQ5DAqbMKFNQ0fGp4byGU" },
    { label: "K-pop", hex: "#e61e32", uri: "spotify:page:0JQ5DAqbMKFGvOw3O4nLAf" },
    { label: "Soul", hex: "#dc148c", uri: "spotify:page:0JQ5DAqbMKFIpEuaCnimBj" },
    { label: "Chill", hex: "#b06239", uri: "spotify:page:0JQ5DAqbMKFFzDl7qN9Apr" },
    { label: "Rock", hex: "#006450", uri: "spotify:page:0JQ5DAqbMKFDXXwE9BDJAr" },
    { label: "Latin", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFxXaXKP7zcDp" },
    { label: "Decades", hex: "#777777", uri: "spotify:page:0JQ5DAqbMKFIVNxQgRNSg0" },
    { label: "EQUAL", hex: "#148a08", uri: "spotify:page:0JQ5DAqbMKFPw634sFwguI" },
    { label: "RADAR", hex: "#a56752", uri: "spotify:page:0JQ5DAqbMKFOOxftoKZxod" },
    { label: "Fresh Finds", hex: "#ff0090", uri: "spotify:page:0JQ5DAqbMKFImHYGo3eTSg" },
    { label: "At Home", hex: "#5179a1", uri: "spotify:page:0JQ5DAqbMKFx0uLQR2okcc" },
    { label: "Sleep", hex: "#1e3264", uri: "spotify:page:0JQ5DAqbMKFCuoRTxhYWow" },
    { label: "Love", hex: "#dc148c", uri: "spotify:page:0JQ5DAqbMKFAUsdyVjCQuL" },
    { label: "Metal", hex: "#e91429", uri: "spotify:page:0JQ5DAqbMKFDkd668ypn6O" },
    { label: "Folk & Acoustic", hex: "#bc5900", uri: "spotify:page:0JQ5DAqbMKFy78wprEpAjl" },
    { label: "Country", hex: "#d84000", uri: "spotify:page:0JQ5DAqbMKFKLfwjuJMoNC" },
    { label: "Trending", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFQIL0AXnG5AK" },
    { label: "Classical", hex: "#7d4b32", uri: "spotify:page:0JQ5DAqbMKFPrEiAOxgac3" },
    { label: "Focus", hex: "#a56752", uri: "spotify:page:0JQ5DAqbMKFCbimwdOYlsl" },
    { label: "Kids & Family", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFFoimhOqWzLB" },
    { label: "Gaming", hex: "#e8115b", uri: "spotify:page:0JQ5DAqbMKFCfObibaOZbv" },
    { label: "Anime", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFziKOShCi009" },
    { label: "TV & Movies", hex: "#148a08", uri: "spotify:page:0JQ5DAqbMKFOzQeOmemkuw" },
    { label: "Netflix", hex: "#e61e32", uri: "spotify:page:0JQ5DAqbMKFEOEBCABAxo9" },
    { label: "Instrumental", hex: "#537aa1", uri: "spotify:page:0JQ5DAqbMKFRieVZLLoo9m" },
    { label: "Alternative", hex: "#e13300", uri: "spotify:page:0JQ5DAqbMKFFtlLYUHv8bT" },
    { label: "Punk", hex: "#e61e32", uri: "spotify:page:0JQ5DAqbMKFAjfauKLOZiv" },
    { label: "Ambient", hex: "#148a08", uri: "spotify:page:0JQ5DAqbMKFLjmiZRss79w" },
    { label: "Blues", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFQiK2EHwyjcU" },
    { label: "Cooking & Dining", hex: "#ba5d07", uri: "spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0" },
    { label: "Travel", hex: "#0d72ed", uri: "spotify:page:0JQ5DAqbMKFAQy4HL4XU2D" },
    { label: "Caribbean", hex: "#0d73ec", uri: "spotify:page:0JQ5DAqbMKFObNLOHydSW8" },
    { label: "Jazz", hex: "#8d67ab", uri: "spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m" },
    { label: "Songwriters", hex: "#8c1932", uri: "spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O" },
    { label: "Nature & Noise", hex: "#477d95", uri: "spotify:page:0JQ5DAqbMKFI3pNLtYMD9S" },
    { label: "Funk & Disco", hex: "#af2896", uri: "spotify:page:0JQ5DAqbMKFFsW9N8maB6z" },
    { label: "Spotify Singles", hex: "#777777", uri: "spotify:page:0JQ5DAqbMKFDBgllo2cUIN" },
    { label: "Reggae", hex: "#006450", uri: "spotify:page:0JQ5DAqbMKFJKoGyUMo2hE" },
    { label: "Arab", hex: "#e61e32", uri: "spotify:page:0JQ5DAqbMKFQ1UFISXj59F" },
    { label: "Tastemakers", hex: "#e8115b", uri: "spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm" },
    { label: "Wellness", hex: "#148a08", uri: "spotify:page:0JQ5DAqbMKFLb2EqgLtpjC" },
    { label: "Mixed By", hex: "#8d67ab", uri: "spotify:page:0JQ5DAqbMKFPSyykKYdCTj" }
];

// Group taxonomy. Items not matched here fall into "MORE" automatically.
const browseGroups = {
    "TOP":             ["Music", "Podcasts", "Audiobooks", "Live Events"],
    "FOR YOU":         ["Made For You", "Discover", "Fresh Finds", "RADAR", "EQUAL", "New Releases", "Trending"],
    "GENRES":          ["Pop", "Hip-Hop", "Rock", "Dance/Electronic", "Indie", "R&B", "Soul", "Country", "Classical", "Jazz", "Metal", "Punk", "Alternative", "Folk & Acoustic", "Funk & Disco", "Reggae", "Blues", "Ambient", "K-pop", "Latin", "Afro", "Dutch music", "Arab", "Caribbean"],
    "MOOD & ACTIVITY": ["Mood", "Chill", "Party", "Sleep", "Love", "Focus", "Workout Music", "Fitness", "In the car", "At Home", "Travel", "Cooking & Dining", "Wellness", "Songwriters", "Nature & Noise"],
    "CHARTS":          ["Charts", "Podcast Charts"]
};
const browseGroupOrder = ["TOP", "FOR YOU", "GENRES", "MOOD & ACTIVITY", "CHARTS", "MORE"];

function classifyBrowse(label) {
    for (const [group, members] of Object.entries(browseGroups)) {
        if (members.includes(label)) return group;
    }
    return "MORE";
}

function renderBrowse() {
    const host = document.getElementById("browseGroups");
    if (!host) return;
    const buckets = new Map();
    for (const item of browseItems) {
        const key = classifyBrowse(item.label);
        if (!buckets.has(key)) buckets.set(key, []);
        buckets.get(key).push(item);
    }
    // Within each group: alphabetical scan order (TOP keeps API order — short list,
    // semantic order matters: Music → Podcasts → Audiobooks → Live Events).
    for (const [key, items] of buckets) {
        if (key === "TOP") continue;
        items.sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: "base" }));
    }
    let html = "";
    for (const key of browseGroupOrder) {
        const items = buckets.get(key);
        if (!items || items.length === 0) continue;
        html += `<div class="browse-group">`;
        html +=   `<span class="browse-group-eyebrow">${key}</span>`;
        html +=   `<div class="browse-grid">`;
        for (const item of items) {
            html += `<a class="browse-chip" data-uri="${item.uri}" style="--chip-accent:${item.hex};">`;
            html +=   `<span class="browse-chip-label">${item.label}</span>`;
            html += `</a>`;
        }
        html +=   `</div>`;
        html += `</div>`;
    }
    host.innerHTML = html;
    host.querySelectorAll(".browse-chip").forEach(chip => {
        chip.addEventListener("click", () => console.log("[browse]", chip.dataset.uri));
    });
}

// ---------- Lazy load: shimmer until the section nears the viewport ----------
// Mirrors the production behaviour: don't fetch browseAll on home load (most
// sessions never reach it). Render a shimmer skeleton, IntersectionObserver
// fires when the section is within ~1 viewport of being on-screen, fake a
// 700 ms network delay, then crossfade in the real chips.

function renderBrowseShimmer() {
    const host = document.getElementById("browseGroups");
    if (!host) return;
    // Approximate counts per group (TOP, FOR YOU, GENRES, MOOD, CHARTS, MORE)
    // so the skeleton's vertical extent matches the populated state — no
    // layout jump when the swap happens.
    const groupCounts = [4, 7, 14, 10, 2, 8];
    let html = "";
    for (const count of groupCounts) {
        html += `<div class="browse-group">`;
        html +=   `<span class="browse-shimmer-eyebrow"></span>`;
        html +=   `<div class="browse-grid">`;
        for (let i = 0; i < count; i++) {
            html += `<span class="browse-shimmer-chip"></span>`;
        }
        html +=   `</div>`;
        html += `</div>`;
    }
    host.innerHTML = html;
}

let browseLoadTriggered = false;

function setupBrowseLazyLoad() {
    const section = document.querySelector(".browse-all");
    const host = document.getElementById("browseGroups");
    if (!section || !host) return;

    renderBrowseShimmer();

    const observer = new IntersectionObserver((entries) => {
        if (browseLoadTriggered) return;
        for (const entry of entries) {
            if (!entry.isIntersecting) continue;
            browseLoadTriggered = true;
            observer.disconnect();
            // Simulate a 700 ms server round-trip
            setTimeout(() => {
                host.style.opacity = "0";
                // Wait for the fade-out, then swap content + fade in
                setTimeout(() => {
                    renderBrowse();
                    host.style.opacity = "1";
                }, 180);
            }, 700);
            break;
        }
    }, { rootMargin: "600px 0px" });

    observer.observe(section);
}

setupBrowseLazyLoad();

