import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { ArtistStudyPage } from "./components/artist-study/ArtistStudyPage";
import "./styles.css";

const isArtistHeroStudy = new URLSearchParams(window.location.search).get("study") === "artist-hero";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {isArtistHeroStudy ? <ArtistStudyPage /> : <App />}
  </StrictMode>,
);
