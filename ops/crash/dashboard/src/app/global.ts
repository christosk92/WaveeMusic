import { makeStaticStyles, tokens } from "@fluentui/react-components";

/**
 * Shell fundamentals (plan §0.2, §2.1): no body margin, html/body/#root fill the viewport, body never
 * scrolls (only `<main>` in AppFrame does), box-sizing everywhere, brand text selection colour.
 */
export const useGlobalStyles = makeStaticStyles({
  "html, body, #root": {
    margin: 0,
    padding: 0,
    height: "100%",
    width: "100%",
  },
  body: {
    overflow: "hidden",
    fontFamily: tokens.fontFamilyBase,
    WebkitFontSmoothing: "antialiased",
  },
  "*, *::before, *::after": {
    boxSizing: "border-box",
  },
  "::selection": {
    backgroundColor: tokens.colorBrandBackground2,
  },
});
