import {
  AppItem,
  Body1,
  Caption1,
  CounterBadge,
  Divider,
  Hamburger,
  Menu,
  MenuButton,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  NavDrawer,
  NavDrawerBody,
  NavDrawerFooter,
  NavDrawerHeader,
  NavItem,
  NavSectionHeader,
  Persona,
  Tooltip,
  makeStyles,
  tokens,
  type MenuProps,
} from "@fluentui/react-components";
import {
  Bug24Regular,
  Code24Regular,
  DocumentBulletList24Regular,
  Pulse24Regular,
  Tag24Regular,
  WeatherMoon24Regular,
  WeatherSunny24Regular,
} from "@fluentui/react-icons";
import { useEffect, useState, type MouseEvent, type ReactElement } from "react";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import { useStats } from "../api/hooks";
import { isMockEnabled } from "../api/mock";
import { useThemeContext, type ThemeMode, type UseThemeResult } from "./theme";

const NARROW_QUERY = "(max-width: 640px)";

function useIsNarrow(): boolean {
  const [isNarrow, setIsNarrow] = useState(() =>
    typeof window === "undefined" ? false : window.matchMedia(NARROW_QUERY).matches,
  );
  useEffect(() => {
    const mq = window.matchMedia(NARROW_QUERY);
    const onChange = () => setIsNarrow(mq.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);
  return isNarrow;
}

const DRAWER_WIDTH = "260px";

const useStyles = makeStyles({
  root: {
    display: "flex",
    height: "100%",
    minHeight: 0,
    width: "100%",
  },
  drawer: {
    width: DRAWER_WIDTH,
    minWidth: DRAWER_WIDTH,
    maxWidth: DRAWER_WIDTH,
    flexShrink: 0,
    flexGrow: 0,
    height: "100%",
  },
  content: {
    flex: "1 1 auto",
    minWidth: 0,
    minHeight: 0,
    display: "flex",
    flexDirection: "column",
  },
  main: {
    flex: "1 1 auto",
    minWidth: 0,
    minHeight: 0,
    overflow: "auto",
    scrollbarGutter: "stable",
    display: "flex",
    flexDirection: "column",
  },
  // Narrow viewports: the drawer is an overlay, so the hamburger lives in its own bar above `<main>`
  // instead of floating over the page title.
  narrowBar: {
    flexShrink: 0,
    display: "flex",
    alignItems: "center",
    height: "48px",
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  footer: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    paddingTop: tokens.spacingVerticalM,
  },
  footerRow: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: tokens.spacingHorizontalS,
  },
  appItemBody: {
    display: "flex",
    flexDirection: "column",
    alignItems: "flex-start",
    gap: 0,
  },
});

function selectedValueFor(pathname: string): string {
  if (pathname === "/" || pathname === "") return "overview";
  if (pathname.startsWith("/issues")) return "issues";
  if (pathname.startsWith("/reports")) return "reports";
  if (pathname.startsWith("/versions")) return "versions";
  if (pathname.startsWith("/symbols")) return "symbols";
  return "overview";
}

const THEME_ICONS: Record<ThemeMode, ReactElement> = {
  auto: <WeatherSunny24Regular />,
  light: <WeatherSunny24Regular />,
  dark: <WeatherMoon24Regular />,
};

function ThemeMenu({ mode, setMode }: Pick<UseThemeResult, "mode" | "setMode">) {
  const onCheckedValueChange: MenuProps["onCheckedValueChange"] = (_event, data) => {
    const next = data.checkedItems[0];
    if (next === "auto" || next === "light" || next === "dark") setMode(next);
  };

  return (
    <Menu checkedValues={{ theme: [mode] }} onCheckedValueChange={onCheckedValueChange}>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content="Theme" relationship="label">
          <MenuButton appearance="subtle" icon={THEME_ICONS[mode]}>
            Theme
          </MenuButton>
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItemRadio name="theme" value="auto">
            Auto
          </MenuItemRadio>
          <MenuItemRadio name="theme" value="light">
            Light
          </MenuItemRadio>
          <MenuItemRadio name="theme" value="dark">
            Dark
          </MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

/** The app shell (plan §2.3): an inline `NavDrawer` (overlay under 640px) beside the one scroll
 *  container, `<main>`. Nav selection is derived from the route; items navigate via `useNavigate` but
 *  still carry `href` so middle-click / open-in-new-tab work. */
export function AppFrame() {
  const styles = useStyles();
  const theme = useThemeContext();
  const navigate = useNavigate();
  const location = useLocation();
  const isNarrow = useIsNarrow();
  // Inline (wide): always open. Overlay (narrow): closed until the hamburger opens it — an overlay
  // that starts open would cover the whole page at phone widths.
  const [open, setOpen] = useState(() => !isNarrow);
  useEffect(() => {
    setOpen(!isNarrow);
  }, [isNarrow]);
  const selectedValue = selectedValueFor(location.pathname);

  // Mock mode has no session, so the "openIssues" badge always has something to show in the shots.
  const { data: stats } = useStats();

  function go(path: string) {
    return (event: MouseEvent) => {
      if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
      event.preventDefault();
      navigate(path);
      if (isNarrow) setOpen(false);
    };
  }

  return (
    <div className={styles.root}>
      <NavDrawer
        open={isNarrow ? open : true}
        onOpenChange={(_e, data) => setOpen(data.open)}
        type={isNarrow ? "overlay" : "inline"}
        size="medium"
        selectedValue={selectedValue}
        className={isNarrow ? undefined : styles.drawer}
      >
        <NavDrawerHeader>
          <Tooltip content={open ? "Collapse navigation" : "Expand navigation"} relationship="label">
            <Hamburger onClick={() => setOpen((v) => !v)} />
          </Tooltip>
        </NavDrawerHeader>
        <NavDrawerBody>
          <AppItem icon={<Pulse24Regular />} as="a" href="/" onClick={go("/")}>
            <span className={styles.appItemBody}>
              <Body1>Wavee crashes</Body1>
              <Caption1>crash.wavee.app{isMockEnabled() ? " · mock" : ""}</Caption1>
            </span>
          </AppItem>
          <NavSectionHeader>Monitor</NavSectionHeader>
          <NavItem value="overview" icon={<Pulse24Regular />} href="/" onClick={go("/")}>
            Overview
          </NavItem>
          <NavItem
            value="issues"
            icon={<Bug24Regular />}
            href="/issues"
            onClick={go("/issues")}
          >
            Issues
            {typeof stats?.openIssues === "number" && stats.openIssues > 0 && (
              <CounterBadge count={stats.openIssues} size="small" style={{ marginLeft: tokens.spacingHorizontalS }} />
            )}
          </NavItem>
          <NavItem value="reports" icon={<DocumentBulletList24Regular />} href="/reports" onClick={go("/reports")}>
            Reports
          </NavItem>
          <NavSectionHeader>Manage</NavSectionHeader>
          <NavItem value="versions" icon={<Tag24Regular />} href="/versions" onClick={go("/versions")}>
            Versions
          </NavItem>
          <NavItem value="symbols" icon={<Code24Regular />} href="/symbols" onClick={go("/symbols")}>
            Symbols
          </NavItem>
        </NavDrawerBody>
        <NavDrawerFooter>
          <div className={styles.footer}>
            <Divider />
            <div className={styles.footerRow}>
              <ThemeMenu mode={theme.mode} setMode={theme.setMode} />
            </div>
            <Persona name="Wavee" secondaryText="Cloudflare Access" />
          </div>
        </NavDrawerFooter>
      </NavDrawer>
      <div className={styles.content}>
        {isNarrow && (
          <div className={styles.narrowBar}>
            <Tooltip content="Open navigation" relationship="label">
              <Hamburger onClick={() => setOpen(true)} />
            </Tooltip>
          </div>
        )}
        <main className={styles.main}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
