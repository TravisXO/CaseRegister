import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { api } from "../api/endpoints";
import { useSession } from "../auth/session";

interface Counts {
  unread: number;
  needsAcknowledgement: number;
}

const NONE: Counts = { unread: 0, needsAcknowledgement: 0 };

interface NotificationsContextValue extends Counts {
  /**
   * Asks the API for the counts. This is the only way they change: on moving to another page, and after the person does something with a
   * notification. Nothing asks on a timer. With `announce`, the new count is also said to screen readers (only ever after the person acted).
   */
  refresh: (announce?: boolean) => Promise<void>;
  /** What the polite live region says. It changes only after a person acted, never because the page moved on. */
  announcement: string;
  say: (text: string) => void;
}

const NotificationsContext = createContext<NotificationsContextValue | null>(null);

export function unreadText(unread: number): string {
  return unread === 0 ? "No unread notifications" : `${unread} unread notification${unread === 1 ? "" : "s"}`;
}

/**
 * The bell's count, kept in memory for as long as someone is signed in and dropped the moment they are not. It is fetched when the page
 * changes and when the person acts, never on a timer: a background call would keep resetting the 30-minute idle timeout.
 */
export function NotificationsProvider({ children }: { children: ReactNode }) {
  const { state } = useSession();
  const { pathname } = useLocation();
  const [counts, setCounts] = useState<Counts>(NONE);
  const [announcement, setAnnouncement] = useState("");
  const signedIn = state.status === "ready";

  // Signing out, or a session that ended, drops the count in the same render: no effect, nothing left for the next person at this screen.
  const [wasSignedIn, setWasSignedIn] = useState(signedIn);
  if (signedIn !== wasSignedIn) {
    setWasSignedIn(signedIn);
    if (!signedIn) {
      setCounts(NONE);
      setAnnouncement("");
    }
  }

  const refresh = useCallback(async (announce = false) => {
    try {
      const list = await api.notifications(1, false);
      setCounts({ unread: list.unreadCount, needsAcknowledgement: list.needsAcknowledgementCount });
      if (announce) {
        setAnnouncement(unreadText(list.unreadCount));
      }
    } catch {
      // The bell is a convenience. If it cannot be read, the page the person came for still works.
    }
  }, []);

  useEffect(() => {
    if (signedIn) {
      // Asking the API what the count is, when the person moves to another page: loading from an outside system, which this rule cannot tell from a loop.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      void refresh();
    }
  }, [signedIn, pathname, refresh]);

  const value = useMemo(() => ({ ...counts, refresh, announcement, say: setAnnouncement }), [counts, refresh, announcement]);

  return <NotificationsContext value={value}>{children}</NotificationsContext>;
}

export function useNotifications(): NotificationsContextValue {
  const value = useContext(NotificationsContext);
  if (value === null) {
    throw new Error("useNotifications is used outside the NotificationsProvider.");
  }

  return value;
}
