import { useState } from "react";

/** The text as people read it aloud or type it in: groups of four, with a space between. */
export function inGroupsOfFour(text: string): string {
  return text.replace(/\s+/g, "").replace(/(.{4})(?=.)/g, "$1 ");
}

/**
 * Copies text to the clipboard. What happened is said in a live region, so a screen reader hears it without the focus moving, and
 * a failure (a browser that does not allow it) is said too, with what to do instead.
 */
export function CopyButton({ text, label, what }: { text: string; label: string; what: string }) {
  const [outcome, setOutcome] = useState<"idle" | "copied" | "failed">("idle");

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setOutcome("copied");
    } catch {
      setOutcome("failed");
    }
  };

  return (
    <>
      <button
        type="button"
        className="btn-secondary print:hidden"
        onClick={() => {
          void copy();
        }}
      >
        {label}
      </button>
      <span role="status" aria-live="polite" className={outcome === "idle" ? "sr-only" : "self-center text-sm print:hidden"}>
        {outcome === "copied" ? `${what} copied.` : outcome === "failed" ? `${what} could not be copied. Select it and copy it by hand.` : ""}
      </span>
    </>
  );
}
