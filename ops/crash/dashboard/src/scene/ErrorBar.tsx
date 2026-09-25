import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from "@fluentui/react-components";

export interface ErrorBarProps {
  error: unknown;
  onRetry?: () => void;
  title?: string;
}

function messageFor(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;
  return "Something went wrong.";
}

/** `MessageBar intent="error"` + Retry (plan §0.4, §2.4) — every scene's error state. */
export function ErrorBar({ error, onRetry, title = "Couldn't load this page" }: ErrorBarProps) {
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {messageFor(error)}
      </MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button appearance="secondary" onClick={onRetry}>
            Retry
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}
