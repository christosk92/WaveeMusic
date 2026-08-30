import { PlayerBar, TitleBar } from "./components/AppChrome";
import { ContentPage } from "./components/ContentPage";
import { SidebarCustomizer } from "./components/Customizer";
import { OnboardingChooser, SettingsPanel, ToastViewport } from "./components/Overlays";
import { SidebarShell } from "./components/sidebar/SidebarShell";
import { PrototypeProvider, usePrototype } from "./PrototypeContext";

function AppSurface() {
  const { theme, route } = usePrototype();
  return (
    <div className="wavee-app" data-theme={theme}>
      <TitleBar />
      <div className="app-workspace">
        <SidebarShell />
        <main className={`app-main ${route === "sidebar-customize" ? "is-customizing" : ""}`}>
          {route === "sidebar-customize" ? <SidebarCustomizer /> : <ContentPage />}
        </main>
      </div>
      <PlayerBar />
      <OnboardingChooser />
      <SettingsPanel />
      <ToastViewport />
    </div>
  );
}

export function App() {
  return (
    <PrototypeProvider>
      <AppSurface />
    </PrototypeProvider>
  );
}
