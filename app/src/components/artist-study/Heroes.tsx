import { Check, Pin } from "lucide-react";
import { ActionRow, Cover, IdentityCopy } from "./Parts";
import { artist, type ArtistVariant } from "./data";

const HERO_SRC = "/assets/conan-gray-hero.webp";

/**
 * EDITORIAL SPLIT — the recommended primary.
 *
 * Combines the general hero craft with Fluent semantics:
 *  · Asymmetric split (NN/g: guides the eye more predictably than a uniform full-width band).
 *  · Oversized type using Fluent's real Display step (68/92) — expressive AND on-ramp.
 *  · The photograph is full-bleed to the pane's top and right edges; only its INNER corner is
 *    rounded, because Fluent geometry says straight edges meeting straight edges are not.
 *  · The left field carries the artwork-extracted wash, so the page is tinted by the art itself.
 *  · Still nothing composited over the photograph.
 */
function EditorialHero() {
  return (
    <header className="av-hero av-hero--editorial" data-testid="artist-hero">
      <div className="av-hero__wash" aria-hidden="true" />
      <div className="av-hero__split">
        <div className="av-identity">
          <IdentityCopy />
        </div>
        <div className="av-hero__figure" data-testid="artist-photo">
          <img src={HERO_SRC} alt="" role="presentation" />
          <span className="av-hero__vignette" aria-hidden="true" />
        </div>
      </div>
    </header>
  );
}

/**
 * WIDE BAND — the real 2660×1139 header runs full-bleed, and every piece of identity text sits
 * on the content layer below the hairline. Nothing composites over photography, so there is no
 * scrim, no per-artist contrast tuning, and light theme works by construction.
 */
function BandHero() {
  return (
    <header className="av-hero av-hero--band" data-testid="artist-hero">
      <div className="av-hero__photo" data-testid="artist-photo">
        <img src={HERO_SRC} alt="" role="presentation" />
      </div>
      <div className="av-identity">
        <IdentityCopy />
      </div>
    </header>
  );
}

/**
 * PORTRAIT PLATE — the photograph becomes a framed 320² object beside the identity text, over a
 * 6% palette wash. The most conservatively Fluent reading; also the cheapest vertically.
 */
function PlateHero() {
  return (
    <header className="av-hero av-hero--plate" data-testid="artist-hero">
      <div className="av-hero__wash" aria-hidden="true" />
      <div className="av-identity">
        <div className="av-hero__plate" data-testid="artist-photo">
          <img src={HERO_SRC} alt="" role="presentation" />
        </div>
        <IdentityCopy />
      </div>
    </header>
  );
}

/**
 * CURRENT FADE — the control, rebuilt against the real native constants from ArtistHeroLayout.cs
 * (WideHeight 420, clamp(0.32w, 420, 560), PhotoFadeBandFor clamp(0.28h, 120, 180),
 * ContentBlendTail 96) so "today vs the alternatives" is a true A/B. v1's version masked the
 * photo down to a hard #203b4d slate its own annotation never described.
 *
 * This is the only variant that sets text on the photograph — that is the property under test.
 */
function CurrentHero() {
  return (
    <header className="av-hero av-hero--current" data-testid="artist-hero">
      <div className="av-hero__photo" data-testid="artist-photo">
        <img src={HERO_SRC} alt="" role="presentation" />
        <div className="av-hero__scrim" aria-hidden="true" />
      </div>
      <div className="av-hero__overlay">
        <div className="av-hero__badges">
          <span className="av-badge av-badge--verified">
            <Check size={12} /> Verified Artist
          </span>
          <span className="av-badge">#419 in the world</span>
        </div>
        <h1 className="av-identity__name" data-testid="artist-name">
          {artist.name}
        </h1>
        <p className="av-hero__bio">{artist.bio}</p>
        <p className="av-hero__stats">
          <span>
            <span className="av-num">{artist.monthlyListeners}</span> monthly listeners
          </span>
          <span aria-hidden="true"> · </span>
          <span>
            <span className="av-num">{artist.followers}</span> followers
          </span>
        </p>
        <ActionRow />
      </div>
      <article className="av-hero__pinned" data-testid="pinned-release-card">
        <Cover name={artist.latest.cover} alt={`${artist.latest.title} artwork`} className="av-cover--56" />
        <div>
          <span>
            <Pin size={11} /> Pinned
          </span>
          <strong>{artist.latest.title}</strong>
          <small>wishbone deluxe, out now.</small>
        </div>
      </article>
      <div className="av-hero__blend" aria-hidden="true" />
    </header>
  );
}

export function Hero({ variant }: { variant: ArtistVariant }) {
  if (variant === "editorial") return <EditorialHero />;
  if (variant === "band") return <BandHero />;
  if (variant === "plate") return <PlateHero />;
  return <CurrentHero />;
}
