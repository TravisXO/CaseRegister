import { api } from "../api/endpoints";
import { useLoad } from "./hooks";
import { answerWithPasskey, passkeysSupported } from "./webauthn";

/** Whether this person can use a passkey as the proof for a sensitive act here: the browser can, and they have at least one. */
export function usePasskeyStepUp(): { available: boolean; answer: () => Promise<string> } {
  const { state } = useLoad(() => (passkeysSupported() ? api.passkeys() : Promise.resolve([])));

  return {
    available: state.status === "ready" && state.data.length > 0,
    answer: async () => answerWithPasskey(await api.passkeyStepUpOptions()),
  };
}
