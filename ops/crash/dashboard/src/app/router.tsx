import { createBrowserRouter } from "react-router-dom";
import { lazy, Suspense, type ReactNode } from "react";
import { AppFrame } from "./AppFrame";
import { PageHeader } from "../scene/PageHeader";
import { PageBody } from "../scene/PageBody";
import { EmptyState } from "../scene/EmptyState";
import { ClipboardTaskListLtr24Regular } from "@fluentui/react-icons";

const OverviewPage = lazy(() => import("../pages/Overview"));
const IssuesPage = lazy(() => import("../pages/Issues"));
const IssueDetailPage = lazy(() => import("../pages/IssueDetail"));
const ReportsPage = lazy(() => import("../pages/Reports"));
const ReportDetailPage = lazy(() => import("../pages/ReportDetail"));
const VersionsPage = lazy(() => import("../pages/Versions"));
const SymbolsPage = lazy(() => import("../pages/Symbols"));

function NotFound() {
  return (
    <>
      <PageHeader title="Not found" />
      <PageBody>
        <div style={{ gridColumn: "span 12" }}>
          <EmptyState icon={<ClipboardTaskListLtr24Regular />} title="There is no page at this address" />
        </div>
      </PageBody>
    </>
  );
}

function suspended(node: ReactNode) {
  return <Suspense fallback={null}>{node}</Suspense>;
}

/** `createBrowserRouter` with one layout route (`AppFrame`, the shell) and lazy page routes (plan §2.3).
 *  `AppFrame` reads the current theme from `ThemeContext` rather than a prop, so it stays in sync with
 *  the `FluentProvider` mounted above `RouterProvider` in `main.tsx` without recreating the router. */
export function createRouter() {
  return createBrowserRouter([
    {
      path: "/",
      element: <AppFrame />,
      children: [
        { index: true, element: suspended(<OverviewPage />) },
        { path: "issues", element: suspended(<IssuesPage />) },
        { path: "issues/:fp", element: suspended(<IssueDetailPage />) },
        { path: "reports", element: suspended(<ReportsPage />) },
        { path: "reports/:id", element: suspended(<ReportDetailPage />) },
        { path: "versions", element: suspended(<VersionsPage />) },
        { path: "symbols", element: suspended(<SymbolsPage />) },
        { path: "*", element: <NotFound /> },
      ],
    },
  ]);
}
