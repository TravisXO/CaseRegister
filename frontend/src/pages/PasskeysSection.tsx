import { useId, useState, type SyntheticEvent } from "react";
import { api } from "../api/endpoints";
import type { Schemas } from "../api/types";
import { ErrorSummary } from "../components/ErrorSummary";
import { SubmitButton, TextField } from "../components/Fields";
import { Empty, Loaded } from "../components/States";
import { TableRegion } from "../components/TableRegion";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useLoad, useSubmit } from "../lib/hooks";
import { formatZambianTime } from "../lib/time";
import { answerWithPasskey, makePasskey, passkeysSupported } from "../lib/webauthn";

type Passkey = Schemas["PasskeyView"];

/** The default name offered for a new passkey. */
const DEFAULT_NAME = "My passkey";

function Rename({ passkey, onDone }: { passkey: Passkey; onDone: (renamed: boolean) => void }) {
  const [name, setName] = useState(passkey.name);
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const field = useId();

  const save = useSubmit(async () => {
    const mistakes = required(name, field, "Enter a name.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.renamePasskey(passkey.id, name.trim());
      onDone(true);
    } catch (error) {
      setProblems(problemsFrom(error));
    }
  });

  return (
    <form
      noValidate
      aria-label={`Rename ${passkey.name}`}
      onSubmit={(event) => {
        event.preventDefault();
        void save.run();
      }}
    >
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <TextField id={field} label="New name" value={name} maxLength={60} onChange={(event) => { setName(event.target.value); }} error={errorFor(problems.problems, field)} />
      <SubmitButton pending={save.pending}>Save name</SubmitButton>
      <button
        type="button"
        className="btn-secondary ml-2"
        onClick={() => {
          onDone(false);
        }}
      >
        Cancel
      </button>
    </form>
  );
}

function Remove({ passkey, others, onDone }: { passkey: Passkey; others: number; onDone: (removed: boolean) => void }) {
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const field = useId();

  const remove = useSubmit(async (how: "code" | "passkey" = "code") => {
    const mistakes = how === "passkey" ? [] : required(code, field, "Enter a code from your authenticator app.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const proof = how === "passkey" ? { passkeyAssertion: await answerWithPasskey(await api.passkeyStepUpOptions()) } : { twoFactorCode: code.trim() };
      await api.removePasskey(passkey.id, proof);
      onDone(true);
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  return (
    <form
      noValidate
      aria-label={`Remove ${passkey.name}`}
      onSubmit={(event) => {
        event.preventDefault();
        void remove.run();
      }}
    >
      <p className="mb-2">
        Remove <strong>{passkey.name}</strong>? Your other sessions will end. Your authenticator app keeps working.
      </p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <TextField id={field} label="Code from your authenticator app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, field)} />
      <SubmitButton pending={remove.pending} variant="danger">
        Remove passkey
      </SubmitButton>
      {others > 0 ? (
        <button
          type="button"
          className="btn-secondary ml-2"
          disabled={remove.pending}
          onClick={() => {
            void remove.run("passkey");
          }}
        >
          Remove with another passkey
        </button>
      ) : null}
      <button
        type="button"
        className="btn-secondary ml-2"
        onClick={() => {
          onDone(false);
        }}
      >
        Keep it
      </button>
    </form>
  );
}

function AddPasskey({ onAdded }: { onAdded: (name: string) => void }) {
  const [name, setName] = useState(DEFAULT_NAME);
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const add = useSubmit(async () => {
    const mistakes = [...required(name, "passkey-name", "Enter a name for this passkey."), ...required(code, "passkey-code", "Enter a code from your authenticator app.")];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      // The browser asks first (a PIN, a fingerprint or a face); the code is checked with what it made.
      const credential = await makePasskey(await api.passkeyCreationOptions());
      const added = await api.addPasskey({ credential, name: name.trim(), twoFactorCode: code.trim() });
      setCode("");
      setProblems({ problems: [] });
      onAdded(added.name);
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  return (
    <form
      noValidate
      aria-labelledby="add-passkey-title"
      onSubmit={(event: SyntheticEvent) => {
        event.preventDefault();
        void add.run();
      }}
    >
      <h3 id="add-passkey-title" className="subheading">
        Add a passkey
      </h3>
      <p className="mb-2">Your device will ask you to confirm with your PIN, fingerprint or face.</p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <TextField id="passkey-name" label="A name for this passkey" hint="For example: Work laptop, or Phone." value={name} maxLength={60} onChange={(event) => { setName(event.target.value); }} error={errorFor(problems.problems, "passkey-name")} />
      <TextField id="passkey-code" label="Code from your authenticator app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "passkey-code")} />
      <SubmitButton pending={add.pending}>Add passkey</SubmitButton>
    </form>
  );
}

/**
 * Passkeys on the profile: the list, adding, renaming and removing. A passkey is a sign-in that cannot be phished. It is an extra: the
 * authenticator app always keeps working, so removing every passkey, or having none, locks nobody out.
 */
export function PasskeysSection() {
  const { state, reload } = useLoad(() => (passkeysSupported() ? api.passkeys() : Promise.resolve([])));
  const [editing, setEditing] = useState<{ id: string; what: "rename" | "remove" } | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  if (!passkeysSupported()) {
    return (
      <section aria-labelledby="passkeys-title">
        <h2 id="passkeys-title" className="subheading">
          Passkeys
        </h2>
        <p className="alert-info">This browser cannot make passkeys. You sign in with your password and authenticator app, which always works.</p>
      </section>
    );
  }

  return (
    <section aria-labelledby="passkeys-title">
      <h2 id="passkeys-title" className="subheading">
        Passkeys
      </h2>
      <p className="mb-4">A passkey lets you sign in with your device&rsquo;s PIN, fingerprint or face, with no password or code, and it cannot be given away to a fake site.</p>
      {notice === null ? null : (
        <p role="status" className="alert-success">
          {notice}
        </p>
      )}
      <Loaded load={state} retry={reload} what="Loading your passkeys">
        {(passkeys) => (
          <>
            {passkeys.length === 0 ? (
              <Empty>You have no passkeys yet.</Empty>
            ) : (
              <TableRegion label="Your passkeys">
                <table className="table">
                  <caption className="sr-only">Your passkeys</caption>
                  <thead>
                    <tr>
                      <th scope="col">Name</th>
                      <th scope="col">Added</th>
                      <th scope="col">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {passkeys.map((passkey) => (
                      <tr key={passkey.id}>
                        <th scope="row">{passkey.name}</th>
                        <td>{formatZambianTime(passkey.createdAt)}</td>
                        <td>
                          <button
                            type="button"
                            className="link mr-4"
                            onClick={() => {
                              setNotice(null);
                              setEditing({ id: passkey.id, what: "rename" });
                            }}
                          >
                            Rename <span className="sr-only">{passkey.name}</span>
                          </button>
                          <button
                            type="button"
                            className="link"
                            onClick={() => {
                              setNotice(null);
                              setEditing({ id: passkey.id, what: "remove" });
                            }}
                          >
                            Remove <span className="sr-only">{passkey.name}</span>
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </TableRegion>
            )}
            {passkeys.map((passkey) =>
              editing?.id === passkey.id ? (
                <div key={passkey.id} className="card my-4">
                  {editing.what === "rename" ? (
                    <Rename
                      passkey={passkey}
                      onDone={(done) => {
                        setEditing(null);
                        if (done) {
                          setNotice("Passkey renamed.");
                          reload();
                        }
                      }}
                    />
                  ) : (
                    <Remove
                      passkey={passkey}
                      others={passkeys.length - 1}
                      onDone={(done) => {
                        setEditing(null);
                        if (done) {
                          setNotice("Passkey removed. Your other sessions were ended.");
                          reload();
                        }
                      }}
                    />
                  )}
                </div>
              ) : null,
            )}
            {passkeys.length >= 10 ? (
              <p className="alert-info mt-4">You have the most passkeys you can have (10). Remove one to add another.</p>
            ) : (
              <AddPasskey
                onAdded={(name) => {
                  setNotice(`Added ${name}. You can use it to sign in now.`);
                  reload();
                }}
              />
            )}
          </>
        )}
      </Loaded>
    </section>
  );
}
