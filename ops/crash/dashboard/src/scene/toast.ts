import { Toast, ToastBody, ToastTitle, useToastController } from "@fluentui/react-components";
import { createElement, type ReactNode } from "react";

/** Fixed id for the single app-wide `Toaster` mounted in `main.tsx` (plan §1, §2.4). */
export const APP_TOASTER_ID = "wavee-crash-toaster";

export interface AppToast {
  success: (title: string, body?: ReactNode) => void;
  error: (title: string, body?: ReactNode) => void;
}

/** `useAppToast()` over `useToastController(APP_TOASTER_ID)` (plan §2.4). */
export function useAppToast(): AppToast {
  const { dispatchToast } = useToastController(APP_TOASTER_ID);

  const show = (intent: "success" | "error", title: string, body?: ReactNode) => {
    dispatchToast(
      createElement(
        Toast,
        null,
        createElement(ToastTitle, null, title),
        body ? createElement(ToastBody, null, body) : null,
      ),
      { intent },
    );
  };

  return {
    success: (title, body) => show("success", title, body),
    error: (title, body) => show("error", title, body),
  };
}
