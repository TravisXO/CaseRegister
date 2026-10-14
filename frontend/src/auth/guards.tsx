import { Navigate, Outlet } from "react-router-dom";
import { Forbidden, Loading } from "../components/States";
import type { Role } from "../api/types";
import { hasRole, useSession } from "./session";

/** Anything behind this needs someone fully signed in. The API checks again on every call: this only decides what is drawn. */
export function RequireSignedIn() {
  const { state } = useSession();

  switch (state.status) {
    case "loading":
      return <Loading what="Checking your session" />;
    case "signed-out":
      return <Navigate to="/sign-in" replace />;
    case "needs-two-factor":
      return <Navigate to="/two-factor/set-up" replace />;
    case "ready":
      return <Outlet />;
  }
}

/** A page for some roles only. Anyone else is told they have no access (the API would refuse them anyway). */
export function RequireRole({ roles }: { roles: Role[] }) {
  const { state } = useSession();
  if (state.status !== "ready") {
    return null;
  }

  return hasRole(state.profile, ...roles) ? <Outlet /> : <Forbidden />;
}
