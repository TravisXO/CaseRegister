// Passkeys in the browser. The API sends and takes WebAuthn as JSON (the shapes the browser's own JSON methods read and write), so this
// is thin: it hands the options to the browser, and gives back what the browser made, as the text the API expects.

export type PasskeyUse = "create" | "get";

/** Whether this browser can make and use passkeys at all. */
export function passkeysSupported(): boolean {
  return (
    typeof window !== "undefined" &&
    typeof window.PublicKeyCredential === "function" &&
    typeof navigator.credentials !== "undefined" &&
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function"
  );
}

/** What went wrong with the browser's part, in words for a person. The browser's own messages are not shown. */
export class PasskeyError extends Error {
  readonly cancelled: boolean;

  constructor(message: string, cancelled: boolean) {
    super(message);
    this.name = "PasskeyError";
    this.cancelled = cancelled;
  }
}

function explain(error: unknown, use: PasskeyUse): PasskeyError {
  const name = error instanceof DOMException ? error.name : "";
  if (name === "NotAllowedError" || name === "AbortError") {
    // Cancelled, timed out, or no passkey chosen. A browser does not say which, and nor does this.
    return new PasskeyError(use === "create" ? "The passkey was not added. You can try again." : "No passkey was used. You can try again, or use your password and authenticator app.", true);
  }

  if (name === "InvalidStateError") {
    return new PasskeyError("This device already has a passkey for your account.", false);
  }

  return new PasskeyError(
    use === "create" ? "This device could not make a passkey. You can still use your authenticator app." : "A passkey could not be used here. Use your password and authenticator app instead.",
    false,
  );
}

/** Asks the browser to make a passkey from the API's creation options. Returns what to send back (the credential, as JSON text). */
export async function makePasskey(optionsJson: unknown): Promise<string> {
  try {
    const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(optionsJson as PublicKeyCredentialCreationOptionsJSON);
    const credential = await navigator.credentials.create({ publicKey });
    if (!(credential instanceof PublicKeyCredential)) {
      throw new PasskeyError("No passkey was made.", true);
    }

    return JSON.stringify(credential.toJSON());
  } catch (error) {
    throw error instanceof PasskeyError ? error : explain(error, "create");
  }
}

/** Asks the browser to answer the API's request options with a passkey. Returns what to send back (the credential, as JSON text). */
export async function answerWithPasskey(optionsJson: unknown): Promise<string> {
  try {
    const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(optionsJson as PublicKeyCredentialRequestOptionsJSON);
    const credential = await navigator.credentials.get({ publicKey });
    if (!(credential instanceof PublicKeyCredential)) {
      throw new PasskeyError("No passkey was used.", true);
    }

    return JSON.stringify(credential.toJSON());
  } catch (error) {
    throw error instanceof PasskeyError ? error : explain(error, "get");
  }
}
