import { useEffect, useState } from "react";

/**
 * Extract a dominant colour from the artwork and harmonise it into a UI-safe wash.
 *
 * This is the iTunes 11 / Android Palette / Spotify approach: downscale the image, cluster the
 * pixels, pick the cluster that actually carries the artwork's identity. The important half is
 * the HARMONISATION — Android's HarmonizedColors and Apple Music's "Increase Contrast" both
 * exist because a raw extracted colour will happily produce unreadable UI. We keep the hue,
 * then clamp saturation and lightness into a band that cannot move text contrast meaningfully.
 */

interface Palette {
  /** "r, g, b" — the harmonised hue, for tinting surfaces at low alpha. */
  washRgb: string;
  /** A saturated-but-safe version for the photo's placeholder fill. */
  seedRgb: string;
}

function rgbToHsl(r: number, g: number, b: number): [number, number, number] {
  const rn = r / 255;
  const gn = g / 255;
  const bn = b / 255;
  const max = Math.max(rn, gn, bn);
  const min = Math.min(rn, gn, bn);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h: number;
  if (max === rn) h = ((gn - bn) / d + (gn < bn ? 6 : 0)) / 6;
  else if (max === gn) h = ((bn - rn) / d + 2) / 6;
  else h = ((rn - gn) / d + 4) / 6;
  return [h, s, l];
}

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
  if (s === 0) {
    const v = Math.round(l * 255);
    return [v, v, v];
  }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  const channel = (t: number) => {
    let tt = t;
    if (tt < 0) tt += 1;
    if (tt > 1) tt -= 1;
    if (tt < 1 / 6) return p + (q - p) * 6 * tt;
    if (tt < 1 / 2) return q;
    if (tt < 2 / 3) return p + (q - p) * (2 / 3 - tt) * 6;
    return p;
  };
  return [
    Math.round(channel(h + 1 / 3) * 255),
    Math.round(channel(h) * 255),
    Math.round(channel(h - 1 / 3) * 255),
  ];
}

export function useArtworkPalette(src: string, fallback: Palette): Palette {
  const [palette, setPalette] = useState<Palette>(fallback);

  useEffect(() => {
    let cancelled = false;
    const image = new Image();
    image.crossOrigin = "anonymous";
    image.src = src;

    image.onload = () => {
      if (cancelled) return;
      const size = 28;
      const canvas = document.createElement("canvas");
      canvas.width = size;
      canvas.height = size;
      const ctx = canvas.getContext("2d", { willReadFrequently: true });
      if (!ctx) return;
      ctx.drawImage(image, 0, 0, size, size);

      let data: Uint8ClampedArray;
      try {
        data = ctx.getImageData(0, 0, size, size).data;
      } catch {
        return; // tainted canvas — keep the fallback rather than throwing
      }

      // 16 hue buckets, weighted by saturation so a grey background cannot win over the subject.
      const buckets = new Array(16).fill(0).map(() => ({ weight: 0, h: 0, s: 0, l: 0, n: 0 }));
      for (let i = 0; i < data.length; i += 4) {
        if (data[i + 3] < 128) continue;
        const [h, s, l] = rgbToHsl(data[i], data[i + 1], data[i + 2]);
        // Ignore near-black and near-white: they carry no hue identity.
        if (l < 0.12 || l > 0.94) continue;
        const bucket = buckets[Math.min(15, Math.floor(h * 16))];
        const weight = s * s + 0.05;
        bucket.weight += weight;
        bucket.h += h * weight;
        bucket.s += s * weight;
        bucket.l += l * weight;
        bucket.n += 1;
      }

      const best = buckets.reduce((a, b) => (b.weight > a.weight ? b : a), buckets[0]);
      if (!best || best.weight === 0) return;

      const h = best.h / best.weight;
      const s = best.s / best.weight;
      const l = best.l / best.weight;

      // Harmonise: keep the hue, clamp chroma and lightness so the wash can never fight text.
      const washS = Math.min(Math.max(s, 0.22), 0.62);
      const wash = hslToRgb(h, washS, Math.min(Math.max(l, 0.32), 0.55));
      const seed = hslToRgb(h, Math.min(washS + 0.08, 0.7), Math.min(Math.max(l, 0.24), 0.42));

      if (!cancelled) {
        setPalette({ washRgb: wash.join(", "), seedRgb: seed.join(", ") });
      }
    };

    return () => {
      cancelled = true;
    };
  }, [src]);

  return palette;
}
