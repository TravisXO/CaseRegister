import { useState } from "react";
import { api } from "../api/endpoints";
import { describeError } from "../api/client";
import { Empty, Loaded } from "../components/States";
import { useLoad } from "../lib/hooks";
import { formatZambianTime } from "../lib/time";

export function SessionsPage() {
  const { state, reload } = useLoad(() => api.sessions());
  const [ending, setEnding] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  const end = async (id: string) => {
    if (ending !== null) {
      return;
    }

    setEnding(id);
    setProblem(null);
    try {
      await api.endSession(id);
      reload();
    } catch (error) {
      setProblem(describeError(error).message);
    } finally {
      setEnding(null);
    }
  };

  return (
    <div className="page">
      <h1 className="heading">My sessions</h1>
      <p className="mb-4">Everywhere you are signed in. End any you do not recognise, and then change your password.</p>
      {problem === null ? null : (
        <p role="alert" className="alert-error">
          {problem}
        </p>
      )}
      <Loaded load={state} retry={reload} what="Loading your sessions">
        {(sessions) =>
          sessions.length === 0 ? (
            <Empty>No sessions.</Empty>
          ) : (
            <ul className="space-y-3">
              {sessions.map((session) => (
                <li key={session.id} className="card">
                  <p className="font-medium">{session.isCurrent ? "This session" : "Another session"}</p>
                  <p>Signed in {formatZambianTime(session.signedInAt)}</p>
                  <p>Last active {formatZambianTime(session.lastActiveAt)}</p>
                  <p className="break-all text-sm text-slate-700">
                    {session.ipAddress ?? "Unknown address"} · {session.userAgent ?? "Unknown device"}
                  </p>
                  {session.isCurrent ? null : (
                    <button
                      type="button"
                      className="btn-secondary mt-2"
                      disabled={ending !== null}
                      onClick={() => {
                        void end(session.id);
                      }}
                    >
                      {ending === session.id ? "Please wait…" : "End this session"}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )
        }
      </Loaded>
    </div>
  );
}
