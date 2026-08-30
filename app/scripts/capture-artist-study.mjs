import { chromium } from "playwright";
const b = await chromium.launch();
const out = "artifacts/artist-hero-study";
const shot = async (p, name) => p.screenshot({ path: `${out}/${name}.png` });

for (const [w, h, tag] of [[1440, 900, "wide"], [1024, 900, "1024"], [720, 900, "720"], [420, 820, "420"]]) {
  const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
  for (const v of ["editorial", "band", "plate", "current"]) {
    for (const t of ["light", "dark"]) {
      if (tag !== "wide" && t === "dark") continue;
      await p.goto(`http://127.0.0.1:4173/?study=artist-hero&variant=${v}&theme=${t}&chrome=0`);
      await p.waitForSelector("[data-testid=track-row]");
      await p.waitForTimeout(350);
      await shot(p, `${v}-${tag}-${t}`);
      if (tag === "wide" && t === "light") {
        await p.evaluate(() => document.querySelector("[data-testid=content-pane]").scrollTo({ top: 460 }));
        await p.waitForTimeout(250);
        await shot(p, `${v}-${tag}-scrolled`);
      }
    }
  }
  await p.close();
}
// one capture with the study switcher visible
const p = await b.newPage({ viewport: { width: 1440, height: 900 } });
await p.goto("http://127.0.0.1:4173/?study=artist-hero&variant=band&theme=light");
await p.waitForSelector("[data-testid=track-row]");
await p.waitForTimeout(300);
await shot(p, "band-wide-with-switcher");
await b.close();
console.log("done");
