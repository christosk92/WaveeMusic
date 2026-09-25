import { makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import type { PropsWithChildren } from "react";

const useStyles = makeStyles({
  body: {
    display: "grid",
    gridTemplateColumns: "repeat(12, 1fr)",
    gap: tokens.spacingHorizontalL,
    paddingLeft: tokens.spacingHorizontalXXL,
    paddingRight: tokens.spacingHorizontalXXL,
    paddingBottom: tokens.spacingVerticalXXL,
    paddingTop: tokens.spacingVerticalXL,
    // `<main>` is a flex column; auto side margins on a flex item absorb the free space and shrink the
    // item to its content width — which is nothing during the Skeleton state. An explicit width keeps
    // the grid full-bleed up to the max and lets the margins centre it beyond that.
    width: "100%",
    maxWidth: "1600px",
    marginLeft: "auto",
    marginRight: "auto",
    transitionProperty: "opacity",
    transitionDuration: tokens.durationNormal,
  },
  fetching: {
    opacity: 0.6,
  },
});

export interface PageBodyProps extends PropsWithChildren {
  /** A background refetch keeps the current content mounted (no `Skeleton` swap) but dims it slightly
   *  and marks the region busy — pair with `PageHeader`'s own `isFetching` for the pinned `ProgressBar`. */
  isFetching?: boolean;
}

/** The 12-column page grid (plan §2.4) — children set `gridColumn: "span n"` for their own width. */
export function PageBody({ children, isFetching }: PageBodyProps) {
  const styles = useStyles();
  return (
    <div className={mergeClasses(styles.body, isFetching && styles.fetching)} aria-busy={isFetching || undefined}>
      {children}
    </div>
  );
}
