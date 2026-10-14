import { Navigate, Outlet, createBrowserRouter, createMemoryRouter, type RouteObject } from "react-router-dom";
import { RequireRole, RequireSignedIn } from "./auth/guards";
import { SessionProvider, useSession } from "./auth/session";
import { Layout } from "./components/Layout";
import { ErrorBoundary, Loading } from "./components/States";
import { ActionsPage } from "./pages/admin/ActionsPage";
import { ApplicationsPage } from "./pages/admin/ApplicationsPage";
import { AuditPage } from "./pages/admin/AuditPage";
import { InviteStaffPage } from "./pages/admin/InviteStaffPage";
import { ProposalPage, ProposalsPage } from "./pages/admin/ProposalsPage";
import { EnrolPage } from "./pages/EnrolPage";
import { NotFoundPage } from "./pages/NotFoundPage";
import { ProfilePage } from "./pages/ProfilePage";
import { SessionsPage } from "./pages/SessionsPage";
import { SignInPage } from "./pages/SignInPage";
import { SignUpPage } from "./pages/SignUpPage";
import { StaffSetupPage } from "./pages/StaffSetupPage";
import { TwoFactorCodePage } from "./pages/TwoFactorCodePage";

function Home() {
  const { state } = useSession();
  if (state.status === "loading") {
    return <Loading />;
  }

  return <Navigate to={state.status === "ready" ? "/profile" : state.status === "needs-two-factor" ? "/two-factor/set-up" : "/sign-in"} replace />;
}

function Root() {
  return (
    <ErrorBoundary>
      <SessionProvider>
        <Outlet />
      </SessionProvider>
    </ErrorBoundary>
  );
}

export const routes: RouteObject[] = [
  {
    element: <Root />,
    children: [
      {
        element: <Layout />,
        children: [
          { index: true, element: <Home /> },
          { path: "sign-in", element: <SignInPage /> },
          { path: "sign-in/code", element: <TwoFactorCodePage /> },
          { path: "sign-up", element: <SignUpPage /> },
          { path: "staff/setup", element: <StaffSetupPage /> },
          { path: "two-factor/set-up", element: <EnrolPage /> },
          {
            element: <RequireSignedIn />,
            children: [
              { path: "profile", element: <ProfilePage /> },
              { path: "sessions", element: <SessionsPage /> },
              {
                element: <RequireRole roles={["Admin"]} />,
                children: [
                  { path: "admin/applications", element: <ApplicationsPage /> },
                  { path: "admin/proposals", element: <ProposalsPage /> },
                  { path: "admin/proposals/:id", element: <ProposalPage /> },
                  { path: "admin/invite", element: <InviteStaffPage /> },
                  { path: "admin/actions", element: <ActionsPage /> },
                ],
              },
              {
                element: <RequireRole roles={["Admin", "AgencyComplianceOfficer"]} />,
                children: [{ path: "admin/audit", element: <AuditPage /> }],
              },
            ],
          },
          { path: "*", element: <NotFoundPage /> },
        ],
      },
    ],
  },
];

export const createAppRouter = () => createBrowserRouter(routes);

/** For tests: the same routes, starting at a chosen address, with no real browser history. */
export const createTestRouter = (initialEntries: string[]) => createMemoryRouter(routes, { initialEntries });
