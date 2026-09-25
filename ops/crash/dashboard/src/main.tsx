import { FluentProvider, Toaster, tokens } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { createRouter } from "./app/router";
import { ThemeContext, useTheme } from "./app/theme";
import { useGlobalStyles } from "./app/global";
import { APP_TOASTER_ID } from "./scene/toast";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});

const router = createRouter();

function App() {
  useGlobalStyles();
  const theme = useTheme();

  return (
    <ThemeContext.Provider value={theme}>
      <FluentProvider
        theme={theme.theme}
        applyStylesToPortals
        style={{
          height: "100%",
          backgroundColor: tokens.colorNeutralBackground2,
          color: tokens.colorNeutralForeground1,
        }}
      >
        <QueryClientProvider client={queryClient}>
          <RouterProvider router={router} />
          <Toaster toasterId={APP_TOASTER_ID} />
        </QueryClientProvider>
      </FluentProvider>
    </ThemeContext.Provider>
  );
}

const rootElement = document.getElementById("root");
if (!rootElement) throw new Error("#root element not found");

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
