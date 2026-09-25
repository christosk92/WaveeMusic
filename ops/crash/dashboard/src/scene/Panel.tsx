import {
  Body1Strong,
  Card,
  CardFooter,
  CardHeader,
  Caption1,
  makeStyles,
  mergeClasses,
  tokens,
} from "@fluentui/react-components";
import type { KeyboardEvent, MouseEvent, PropsWithChildren, ReactElement, ReactNode } from "react";

/** Narrow-canvas breakpoint (plan §0.2): below it every panel is full width whatever its `span`. */
export const PANEL_STACK_QUERY = "@media (max-width: 900px)";

const useStyles = makeStyles({
  card: {
    display: "flex",
    flexDirection: "column",
    minWidth: 0,
    [PANEL_STACK_QUERY]: {
      gridColumn: "span 12",
    },
  },
  body: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    minWidth: 0,
    flex: 1,
  },
});

const useSpanStyles = makeStyles({
  s1: { gridColumn: "span 1" },
  s2: { gridColumn: "span 2" },
  s3: { gridColumn: "span 3" },
  s4: { gridColumn: "span 4" },
  s5: { gridColumn: "span 5" },
  s6: { gridColumn: "span 6" },
  s7: { gridColumn: "span 7" },
  s8: { gridColumn: "span 8" },
  s9: { gridColumn: "span 9" },
  s10: { gridColumn: "span 10" },
  s11: { gridColumn: "span 11" },
  s12: { gridColumn: "span 12" },
});

export interface PanelProps extends PropsWithChildren {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactElement;
  footer?: ReactNode;
  /** Number of the 12-column grid this panel spans. */
  span?: number;
  onClick?: (event: MouseEvent | KeyboardEvent) => void;
}

/** A dashboard card (plan §2.4): `Card` + `CardHeader` + body + optional `CardFooter`. Clickable panels
 *  pass `onClick` and get `focusMode="tab-exit"` so Tab exits the card as one stop and Enter/Space
 *  activate it, matching every other interactive stock control. */
export function Panel({ title, description, action, footer, span = 12, onClick, children }: PanelProps) {
  const styles = useStyles();
  const spans = useSpanStyles();
  const clamped = Math.min(12, Math.max(1, Math.round(span))) as 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12;
  return (
    <Card
      className={mergeClasses(spans[`s${clamped}`], styles.card)}
      appearance="filled"
      size="medium"
      focusMode={onClick ? "tab-exit" : undefined}
      onClick={onClick}
    >
      <CardHeader
        header={<Body1Strong>{title}</Body1Strong>}
        description={description ? <Caption1>{description}</Caption1> : undefined}
        action={action}
      />
      <div className={styles.body}>{children}</div>
      {footer && <CardFooter>{footer}</CardFooter>}
    </Card>
  );
}
