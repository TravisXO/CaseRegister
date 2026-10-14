import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api } from "../api/endpoints";
import { ApiError, forgetCsrfToken, onSessionEnded, refreshCsrfToken } from "../api/client";
import type { Profile, Role, SessionResponse } from "../api/types";

/** Why someone is back at sign-in, so the page can say so. */
export type SignedOutNotice = "session-ended" | "signed-out" | null;

export type SessionState =
  | { status: "loading" }
  | { status: "signed-out"; notice: SignedOutNotice }
  /** Signed in with a password but two-factor is not set up yet: the only way forward is to set it up. */
  | { status: "needs-two-factor" }
  | { status: "ready"; session: SessionResponse; profile: Profile };

interface SessionContextValue {
  state: SessionState;
  /** After a successful sign-in or enrolment: fetches a fresh anti-forgery token and loads who is signed in. */
  refresh: () => Promise<void>;
  signOut: () => Promise<void>;
}

const SessionContext = createContext<SessionContextValue | null>(null);

/**
 * Everything the app knows about the signed-in person lives here, in memory, and nowhere else: not in localStorage, not in
 * sessionStorage, not in a cookie this code writes. Signing out, or any 401, replaces it all with "signed out".
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ status: "loading" });

  const load = useCallback(async () => {
    try {
      const session = await api.session();
      if (!session.twoFactorComplete) {
        setState({ status: "needs-two-factor" });

        return;
      }

      const profile = await api.profile();
      setState({ status: "ready", session, profile });
    } catch (error) {
      // 401 is the normal answer for someone who is not signed in. Any other failure also leaves them signed out here.
      setState((current) => (current.status === "signed-out" ? current : { status: "signed-out", notice: null }));
      if (!(error instanceof ApiError) || error.status !== 401) {
        throw error;
      }
    }
  }, []);

  useEffect(() => {
    onSessionEnded(() => {
      forgetCsrfToken();
      setState({ status: "signed-out", notice: "session-ended" });
    });
    // Asking the API who is signed in, when the app opens: loading from an outside system, which this rule cannot tell from a loop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    load().catch(() => undefined);

    return () => {
      onSessionEnded(null);
    };
  }, [load]);

  const refresh = useCallback(async () => {
    await refreshCsrfToken();
    await load();
  }, [load]);

  const signOut = useCallback(async () => {
    try {
      await api.signOut();
    } finally {
      // Whatever the server said, nothing about the person stays in this page.
      forgetCsrfToken();
      setState({ status: "signed-out", notice: "signed-out" });
    }
  }, []);

  const value = useMemo(() => ({ state, refresh, signOut }), [state, refresh, signOut]);

  return <SessionContext value={value}>{children}</SessionContext>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (value === null) {
    throw new Error("useSession is used outside the SessionProvider.");
  }

  return value;
}

export function hasRole(profile: Profile, ...roles: Role[]): boolean {
  return roles.some((role) => profile.roles.includes(role));
}
