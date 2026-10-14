import { useState, type SyntheticEvent } from "react";
import { api } from "../../api/endpoints";
import type { AuditVerification } from "../../api/types";
import { ErrorPanel, Empty, Loaded } from "../../components/States";
import { TextField } from "../../components/Fields";
import { hasRole, useSession } from "../../auth/session";
import { useLoad, useSubmit } from "../../lib/hooks";
import { formatZambianTime } from "../../lib/time";

const PAGE_SIZE = 25;

/** A date as typed (2026-10-13) to the start or end of that day in Zambian time (UTC+2). */
function zambianBoundary(date: string, end: boolean): string | undefined {
  return date === "" ? undefined : `${date}T${end ? "23:59:59.999" : "00:00:00"}+02:00`;
}

function VerifyChain() {
  const [fromAnchor, setFromAnchor] = useState(false);
  const [result, setResult] = useState<AuditVerification | null>(null);
  const [error, setError] = useState<unknown>(null);

  const verify = useSubmit(async () => {
    setError(null);
    try {
      setResult(await api.verifyAudit(fromAnchor));
    } catch (caught) {
      setResult(null);
      setError(caught);
    }
  });

  return (
    <section className="card mb-6" aria-labelledby="verify-title">
      <h2 id="verify-title" className="mb-2 text-lg font-semibold">
        Check the chain
      </h2>
      <p className="mb-2">Recomputes every entry&apos;s hash and its link to the one before, and checks the anchors. Checking is itself written to the log.</p>
      <label className="mb-3 flex items-center gap-2">
        <input
          type="checkbox"
          checked={fromAnchor}
          onChange={(event) => {
            setFromAnchor(event.target.checked);
          }}
        />
        <span>Start from the last good anchor (quicker)</span>
      </label>
      <button
        type="button"
        className="btn-primary"
        disabled={verify.pending}
        onClick={() => {
          void verify.run();
        }}
      >
        {verify.pending ? "Checking…" : "Verify chain"}
      </button>
      {error === null ? null : (
        <div className="mt-3">
          <ErrorPanel error={error} />
        </div>
      )}
      {result === null ? null : result.intact ? (
        <p role="status" className="alert-success mt-3">
          The chain is intact: {result.entriesChecked} entries checked up to number {result.headSequence}
          {result.anchorStoreConfigured ? `, ${result.anchorsChecked} anchors looked at.` : ". No anchor store is set up here, so no anchors were checked."} {formatZambianTime(result.verifiedAt)}
        </p>
      ) : (
        <p role="alert" className="alert-error mt-3">
          <strong>The chain is broken</strong>
          {result.firstBreak === null ? "." : ` at entry ${result.firstBreak.sequence ?? "unknown"}: ${result.firstBreak.kind}. ${result.firstBreak.message}`}
        </p>
      )}
    </section>
  );
}

export function AuditPage() {
  const { state: session } = useSession();
  const isAdmin = session.status === "ready" && hasRole(session.profile, "Admin");
  const [filters, setFilters] = useState({ from: "", to: "", actor: "", action: "" });
  const [applied, setApplied] = useState(filters);
  const [page, setPage] = useState(1);
  const { state, reload } = useLoad(
    () =>
      api.auditEntries({
        from: zambianBoundary(applied.from, false),
        to: zambianBoundary(applied.to, true),
        actor: applied.actor.trim(),
        action: applied.action.trim(),
        page,
        pageSize: PAGE_SIZE,
      }),
    [applied, page],
  );

  const apply = (event: SyntheticEvent) => {
    event.preventDefault();
    setPage(1);
    setApplied(filters);
  };

  return (
    <div className="page max-w-5xl">
      <h1 className="heading">Audit log</h1>
      {isAdmin ? <VerifyChain /> : null}
      <form onSubmit={apply} className="card mb-4 grid grid-cols-1 gap-x-4 sm:grid-cols-2" noValidate>
        <TextField id="from" label="From (date)" type="date" value={filters.from} onChange={(event) => { setFilters({ ...filters, from: event.target.value }); }} />
        <TextField id="to" label="To (date)" type="date" value={filters.to} onChange={(event) => { setFilters({ ...filters, to: event.target.value }); }} />
        <TextField id="actor" label="Who did it (account ID)" autoComplete="off" spellCheck={false} value={filters.actor} onChange={(event) => { setFilters({ ...filters, actor: event.target.value }); }} />
        <TextField id="action" label="What was done" hint="For example SignedIn or ActionConfirmed." autoComplete="off" value={filters.action} onChange={(event) => { setFilters({ ...filters, action: event.target.value }); }} />
        <div className="sm:col-span-2">
          <button type="submit" className="btn-secondary">
            Show
          </button>
        </div>
      </form>
      <Loaded load={state} retry={reload} what="Loading the audit log">
        {(result) => {
          const showAddresses = result.entries.some((entry) => entry.ipAddress !== null);
          const lastPage = Math.max(1, Math.ceil(result.total / PAGE_SIZE));

          return result.entries.length === 0 ? (
            <Empty>No entries match.</Empty>
          ) : (
            <>
              <div className="overflow-x-auto">
                <table className="table">
                  <caption className="mb-2 text-left text-sm text-slate-700">
                    {result.total} entries, newest first. Page {result.page} of {lastPage}.
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">No.</th>
                      <th scope="col">When</th>
                      <th scope="col">Who</th>
                      <th scope="col">What</th>
                      <th scope="col">About</th>
                      {showAddresses ? <th scope="col">Address</th> : null}
                    </tr>
                  </thead>
                  <tbody>
                    {result.entries.map((entry) => (
                      <tr key={entry.sequence}>
                        <td>{entry.sequence}</td>
                        <td>{formatZambianTime(entry.occurredAt)}</td>
                        <td>
                          {entry.actorRole ?? entry.actorType}
                          {entry.actorUserId === null ? null : <span className="block break-all font-mono text-xs">{entry.actorUserId}</span>}
                        </td>
                        <td>
                          {entry.action}
                          {entry.detail === null ? null : <span className="block break-words text-xs text-slate-700">{entry.detail}</span>}
                          {entry.before === null && entry.after === null ? null : (
                            <details className="text-xs">
                              <summary>Before and after</summary>
                              <pre className="overflow-x-auto whitespace-pre-wrap">{JSON.stringify({ before: entry.before, after: entry.after }, null, 2)}</pre>
                            </details>
                          )}
                        </td>
                        <td>
                          {entry.targetType ?? ""}
                          {entry.targetId === null ? null : <span className="block break-all font-mono text-xs">{entry.targetId}</span>}
                        </td>
                        {showAddresses ? <td>{entry.ipAddress}</td> : null}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <nav aria-label="Pages" className="mt-4 flex gap-2">
                <button type="button" className="btn-secondary" disabled={page <= 1} onClick={() => { setPage(page - 1); }}>
                  Newer
                </button>
                <button type="button" className="btn-secondary" disabled={page >= lastPage} onClick={() => { setPage(page + 1); }}>
                  Older
                </button>
              </nav>
            </>
          );
        }}
      </Loaded>
    </div>
  );
}
