import { useEffect, useState } from "react";
import { api } from "../api/endpoints";

/**
 * On every page, including the one shown when something breaks: this is a demonstration, and nothing in it is real. What kind of deployment
 * this is comes from the API (Deployment:Mode), which only starts as "Demo" for now. If the answer has not come yet, or never does, the
 * notice is shown: the page never claims to be anything but a demo on a guess.
 */
export function DemoNotice() {
  const [mode, setMode] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    api
      .deployment()
      .then((answer) => {
        if (current) {
          setMode(answer.mode);
        }
      })
      .catch(() => undefined);

    return () => {
      current = false;
    };
  }, []);

  if (mode !== null && mode !== "Demo") {
    return null;
  }

  return (
    <div role="note" className="border-b border-amber-300 bg-amber-100 px-4 py-2 text-center text-sm text-amber-950">
      <strong>Demo: all data is fake.</strong> Do not enter real personal details, or a password you use anywhere else.
    </div>
  );
}
