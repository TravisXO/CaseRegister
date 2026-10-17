import { useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../api/endpoints";
import { describeError } from "../api/client";
import type { AppNotification } from "../api/types";
import { Empty, Loaded } from "../components/States";
import { useLoad } from "../lib/hooks";
import { formatZambianTime } from "../lib/time";
import { unreadText, useNotifications } from "../notifications/notifications";

const severityLabel: Record<AppNotification["severity"], string> = { Info: "Information", Warning: "Warning", Critical: "Critical" };

/** Only a path inside this application is ever followed. The API refuses to store anything else; this is the second lock. */
function safeLink(link: string): string | null {
  return link.startsWith("/") && !link.startsWith("//") && !link.includes("\\") ? link : null;
}

/** Open means unread, or (for a critical one) not yet acknowledged. */
function isOpen(notification: AppNotification): boolean {
  return notification.needsAcknowledgement ? notification.acknowledgedAt === null : notification.readAt === null;
}

export function NotificationsPage() {
  const { state, reload } = useLoad(() => api.notifications(50, false));
  const { refresh } = useNotifications();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const act = async (action: () => Promise<unknown>) => {
    if (busy) {
      return;
    }

    setBusy(true);
    setProblem(null);
    try {
      await action();
      reload();
      // The person acted, so the new count is also said aloud.
      await refresh(true);
    } catch (error) {
      setProblem(describeError(error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="page">
      <h1 className="heading">Notifications</h1>
      <p className="mb-4">Things that happened that you should know about. Nothing here goes away by itself while it needs your attention.</p>
      {problem === null ? null : (
        <p role="alert" className="alert-error">
          {problem}
        </p>
      )}
      <Loaded load={state} retry={reload} what="Loading your notifications">
        {(list) => (
          <>
            <p className="mb-3" data-testid="unread-summary">
              {unreadText(list.unreadCount)}
              {list.needsAcknowledgementCount > 0 ? `, ${list.needsAcknowledgementCount} waiting to be acknowledged` : ""}.
            </p>
            {list.items.some((item) => !item.needsAcknowledgement && isOpen(item)) ? (
              <button
                type="button"
                className="btn-secondary mb-4"
                disabled={busy}
                onClick={() => {
                  void act(() => api.markAllNotificationsRead());
                }}
              >
                Mark all as read
              </button>
            ) : null}
            {list.items.length === 0 ? (
              <Empty>You have no notifications.</Empty>
            ) : (
              <ul className="space-y-3">
                {list.items.map((item) => {
                  const target = safeLink(item.link);
                  const open = isOpen(item);

                  return (
                    <li key={item.id} className={`card ${item.severity === "Critical" ? "border-red-700" : ""}`}>
                      <p className="text-sm font-semibold">
                        {severityLabel[item.severity]}
                        {open ? " · unread" : ""}
                        <span className="ml-2 font-normal text-slate-700">{formatZambianTime(item.createdAt)}</span>
                      </p>
                      <p className="my-1">{item.text}</p>
                      <div className="mt-2 flex flex-wrap gap-3">
                        {target === null ? null : (
                          <Link to={target} className="link">
                            Open
                          </Link>
                        )}
                        {item.needsAcknowledgement ? (
                          item.acknowledgedAt === null ? (
                            <button
                              type="button"
                              className="btn-primary"
                              disabled={busy}
                              onClick={() => {
                                void act(() => api.acknowledgeNotification(item.id));
                              }}
                            >
                              Acknowledge
                            </button>
                          ) : (
                            <span>Acknowledged {formatZambianTime(item.acknowledgedAt)}</span>
                          )
                        ) : open ? (
                          <button
                            type="button"
                            className="btn-secondary"
                            disabled={busy}
                            onClick={() => {
                              void act(() => api.markNotificationRead(item.id));
                            }}
                          >
                            Mark as read
                          </button>
                        ) : null}
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}
