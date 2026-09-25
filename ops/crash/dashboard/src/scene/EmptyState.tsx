import { Body1Strong, Button, Caption1, makeStyles, tokens } from "@fluentui/react-components";
import type { ReactNode } from "react";

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    textAlign: "center",
    gap: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalXXL,
    paddingBottom: tokens.spacingVerticalXXL,
    color: tokens.colorNeutralForeground3,
  },
  icon: {
    color: tokens.colorNeutralForeground3,
    "& svg": {
      width: "48px",
      height: "48px",
    },
  },
});

export interface EmptyStateProps {
  icon: ReactNode;
  title: string;
  hint?: string;
  action?: { label: string; onClick: () => void };
}

/** The empty-state composition every scene uses (plan §0.4, §2.4): a 48px icon, `Body1Strong` title,
 *  `Caption1` hint, optional `Button`. */
export function EmptyState({ icon, title, hint, action }: EmptyStateProps) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <div className={styles.icon}>{icon}</div>
      <Body1Strong>{title}</Body1Strong>
      {hint && <Caption1>{hint}</Caption1>}
      {action && (
        <Button appearance="secondary" onClick={action.onClick}>
          {action.label}
        </Button>
      )}
    </div>
  );
}
