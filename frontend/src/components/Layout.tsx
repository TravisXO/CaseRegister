import { Link, NavLink, Outlet, useNavigate } from "react-router-dom";
import { useState } from "react";
import { describeError } from "../api/client";
import type { Profile, Role } from "../api/types";
import { hasRole, useSession } from "../auth/session";
import { DemoNotice } from "./DemoNotice";

export interface MenuItem {
  to: string;
  label: string;
}

/** What the menu offers: only what this person's roles allow. It is a convenience, not security: the API refuses the rest. */
export function menuFor(profile: Profile): MenuItem[] {
  const items: MenuItem[] = [
    { to: "/profile", label: "My profile" },
    { to: "/sessions", label: "My sessions" },
  ];

  const is = (...roles: Role[]) => hasRole(profile, ...roles);
  if (is("Admin")) {
    items.push(
      { to: "/admin/applications", label: "Applications" },
      { to: "/admin/proposals", label: "Proposals" },
      { to: "/admin/invite", label: "Invite staff" },
      { to: "/admin/actions", label: "Account actions" },
    );
  }

  if (is("Admin", "AgencyComplianceOfficer")) {
    items.push({ to: "/admin/audit", label: "Audit log" });
  }

  return items;
}

/** Says what is limited, and how to fix it, for a member whose certificate or agency stops them using the register. */
export function StatusBanners({ profile }: { profile: Profile }) {
  const isMember = profile.membershipNumber !== null;

  return (
    <>
      {isMember && !profile.hasCurrentPractisingCertificate ? (
        <div role="status" className="alert-warning">
          <strong>You do not have a current practising certificate.</strong> Until you do, you cannot use the parts of the register that need one. Add this year&apos;s certificate on{" "}
          <Link to="/profile" className="link">
            your profile
          </Link>
          ; an admin has to verify it before it counts.
        </div>
      ) : null}
      {profile.agency !== null && !profile.agency.isActive ? (
        <div role="status" className="alert-warning">
          <strong>Your agency, {profile.agency.name}, is suspended.</strong> Its members cannot use the parts of the register that depend on the agency until it is reactivated. Ask your agency&apos;s compliance officer or the association.
        </div>
      ) : null}
    </>
  );
}

export function Layout() {
  const { state, signOut } = useSession();
  const navigate = useNavigate();
  const [problem, setProblem] = useState<string | null>(null);

  const leave = async () => {
    try {
      await signOut();
    } catch (error) {
      setProblem(describeError(error).message);
    }

    void navigate("/sign-in", { replace: true });
  };

  return (
    <div className="flex min-h-screen flex-col bg-slate-50 text-slate-900">
      <DemoNotice />
      <header className="border-b border-slate-300 bg-white">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
          <Link to="/" className="text-lg font-semibold">
            CaseRegister
          </Link>
          {state.status === "ready" ? (
            <nav aria-label="Main" className="flex flex-1 flex-wrap gap-x-4 gap-y-1">
              {menuFor(state.profile).map((item) => (
                <NavLink key={item.to} to={item.to} className={({ isActive }) => (isActive ? "font-semibold underline" : "link")}>
                  {item.label}
                </NavLink>
              ))}
            </nav>
          ) : (
            <span className="flex-1" />
          )}
          {state.status === "ready" || state.status === "needs-two-factor" ? (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => {
                void leave();
              }}
            >
              Sign out
            </button>
          ) : null}
        </div>
      </header>
      <main className="flex-1">
        {problem === null ? null : (
          <p role="alert" className="alert-error mx-auto max-w-3xl">
            {problem}
          </p>
        )}
        {state.status === "ready" ? (
          <div className="mx-auto max-w-3xl px-4 pt-4">
            <StatusBanners profile={state.profile} />
          </div>
        ) : null}
        <Outlet />
      </main>
      <footer className="border-t border-slate-300 bg-white px-4 py-4 text-center text-sm text-slate-700">Independent concept, not affiliated with ZIEA</footer>
    </div>
  );
}
