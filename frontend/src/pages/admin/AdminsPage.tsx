import { api } from "../../api/endpoints";
import { Loaded } from "../../components/States";
import { useLoad } from "../../lib/hooks";

/** Who the admins are. It warns while there are fewer than three active ones: with two, one admin away leaves nobody to confirm anything. */
export function AdminsPage() {
  const { state, reload } = useLoad(() => api.admins());

  return (
    <div className="page">
      <h1 className="heading">Admins</h1>
      <Loaded load={state} retry={reload} what="Loading the admins">
        {(list) => (
          <>
            {list.warning ? (
              <p className="alert-warning" data-testid="admin-warning">
                There {list.activeCount === 1 ? "is only 1 active admin" : `are only ${String(list.activeCount)} active admins`}. Everything that needs a second admin depends on them being available. Add another admin (invite staff, or give an existing staff member the Admin role) so that at least {list.recommended} are active. Access can never go below {list.minimum}, except from the command line.
              </p>
            ) : (
              <p className="mb-4">{list.activeCount} active admins.</p>
            )}
            <ul className="space-y-2">
              {list.admins.map((admin) => (
                <li key={admin.id} className="card">
                  <p className="font-medium">{admin.fullName}</p>
                  <p className="text-sm text-slate-700">
                    {admin.isActive ? "Active" : "Not active (suspended)"}. {admin.twoFactorOn ? "Two-factor is on." : "Two-factor is not set up."}
                  </p>
                  <p className="text-sm text-slate-700">Account ID: {admin.id}</p>
                </li>
              ))}
            </ul>
          </>
        )}
      </Loaded>
    </div>
  );
}
