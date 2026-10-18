import { useState, useSyncExternalStore } from "react";
import { checkServerIsUp } from "../api/client";
import { snapshot, subscribe } from "../api/startup";

/**
 * Said when the server is waking up (it sleeps when nobody has used it for a while and can take up to a minute). A status message, so a screen
 * reader says it without the person having to find it. A page that was only reading asks again by itself once, after 30 seconds; a change that
 * failed is never repeated by the page: the person does it again when the server is up. The Retry button asks whether the server is up now.
 */
export function StartingUpNotice() {
  const state = useSyncExternalStore(subscribe, snapshot, snapshot);
  const [checking, setChecking] = useState(false);
  const [stillDown, setStillDown] = useState(false);

  if (state === "none") {
    return null;
  }

  const retry = async () => {
    setChecking(true);
    setStillDown(false);
    const up = await checkServerIsUp();
    setChecking(false);
    setStillDown(!up);
  };

  return (
    <div role="status" className="border-b border-sky-300 bg-sky-100 px-4 py-3 text-center text-sm text-sky-950">
      <p className="font-medium">The server is starting up.</p>
      <p>
        {state === "waiting"
          ? "This can take up to a minute. The page will ask again by itself in 30 seconds."
          : "That did not go through, or was not confirmed. If you were changing something, it may not have been saved. When the server is ready, check and do it again if you need to."}
        {stillDown ? " It is still starting; wait a little and try again." : ""}
      </p>
      <button
        type="button"
        className="btn-secondary mt-2"
        disabled={checking}
        onClick={() => {
          void retry();
        }}
      >
        {checking ? "Checking" : "Retry"}
      </button>
    </div>
  );
}
