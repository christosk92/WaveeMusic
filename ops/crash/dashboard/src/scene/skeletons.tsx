import { Card, Skeleton, SkeletonItem, makeStyles, tokens } from "@fluentui/react-components";
import type { ReactNode } from "react";
import { Panel } from "./Panel";

const useStyles = makeStyles({
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))",
    gap: tokens.spacingHorizontalL,
    gridColumn: "span 12",
  },
  card: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    width: "100%",
  },
  gridRow: {
    display: "flex",
    gap: tokens.spacingHorizontalM,
    width: "100%",
  },
  gridCell: {
    flex: 1,
    minWidth: 0,
  },
  factsGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingVerticalS,
    width: "100%",
  },
});

/** Every `SkeletonItem` here gets an explicit `width: "100%"` — without it the item collapses to its
 *  intrinsic size and renders as a tiny centred sliver instead of filling its row (plan §0.4: skeletons
 *  must be shaped like the final layout, not just present). */
const FULL_WIDTH = { width: "100%" } as const;

/** Five stat cards, shaped like Overview's stat row (plan §0.4). Mirrors `StatTile`'s own bare `Card`
 *  (no `CardHeader`) so the loading and ready chrome match exactly. */
export function StatsSkeleton() {
  const styles = useStyles();
  return (
    <div className={styles.statsRow}>
      {Array.from({ length: 5 }, (_, i) => (
        <Card key={i} appearance="filled" size="medium">
          <Skeleton animation="wave" className={styles.card}>
            <SkeletonItem shape="rectangle" size={12} style={FULL_WIDTH} />
            <SkeletonItem shape="rectangle" size={32} style={FULL_WIDTH} />
            <SkeletonItem shape="rectangle" size={12} style={FULL_WIDTH} />
          </Skeleton>
        </Card>
      ))}
    </div>
  );
}

export interface TitledSkeletonProps {
  title?: ReactNode;
  span?: number;
}

/** A chart-shaped placeholder wrapped in the real `Panel` (same title/card chrome as the ready state). */
export function ChartSkeleton({ title = "", span = 8, height = 220 }: TitledSkeletonProps & { height?: number }) {
  const styles = useStyles();
  return (
    <Panel title={title} span={span}>
      <Skeleton animation="wave" className={styles.card}>
        <SkeletonItem shape="rectangle" size={12} style={{ width: "30%" }} />
        <SkeletonItem shape="rectangle" style={{ ...FULL_WIDTH, height }} />
      </Skeleton>
    </Panel>
  );
}

export function GridSkeleton({ title = "", rows = 6, cols = 4, span = 12 }: TitledSkeletonProps & { rows?: number; cols?: number }) {
  const styles = useStyles();
  return (
    <Panel title={title} span={span}>
      <Skeleton animation="wave" className={styles.card}>
        <SkeletonItem shape="rectangle" size={12} style={{ width: "20%" }} />
        {Array.from({ length: rows }, (_, r) => (
          <div key={r} className={styles.gridRow}>
            {Array.from({ length: cols }, (_, c) => (
              <SkeletonItem key={c} shape="rectangle" size={16} className={styles.gridCell} style={{ width: "100%" }} />
            ))}
          </div>
        ))}
      </Skeleton>
    </Panel>
  );
}

export function FactsSkeleton({ title = "", rows = 6, span = 8 }: TitledSkeletonProps & { rows?: number }) {
  const styles = useStyles();
  return (
    <Panel title={title} span={span}>
      <Skeleton animation="wave" className={styles.factsGrid}>
        {Array.from({ length: rows * 2 }, (_, i) => (
          <SkeletonItem key={i} shape="rectangle" size={12} style={FULL_WIDTH} />
        ))}
      </Skeleton>
    </Panel>
  );
}

export function DetailSkeleton() {
  return (
    <>
      <Panel title="" span={12}>
        <Skeleton animation="wave">
          <SkeletonItem shape="rectangle" size={12} style={{ width: "40%" }} />
          <SkeletonItem
            shape="rectangle"
            size={24}
            style={{ width: "70%", marginTop: tokens.spacingVerticalS }}
          />
        </Skeleton>
      </Panel>
      <ChartSkeleton span={8} height={320} />
      <ChartSkeleton span={4} height={320} />
    </>
  );
}
