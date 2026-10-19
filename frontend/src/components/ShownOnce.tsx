import { useState } from "react";
import { useLeaveWarning } from "../lib/hooks";
import { CopyButton } from "./CopyButton";

/**
 * Something that is shown once and never again: recovery codes, or a staff setup link. It can be copied and downloaded, and the page
 * warns before it is left (closed, reloaded or moved away from) so it is not lost by accident.
 */
export function ShownOnce({ title, lines, fileName, instructions, onDone, doneLabel }: { title: string; lines: string[]; fileName: string; instructions: string; onDone: () => void; doneLabel: string }) {
  const [saved, setSaved] = useState(false);
  useLeaveWarning(!saved);

  const text = lines.join("\n");

  const download = () => {
    const url = URL.createObjectURL(new Blob([`${text}\n`], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="card">
      <h2 className="subheading mt-0">{title}</h2>
      <p className="alert-warning">{instructions} This is the only time it is shown.</p>
      <ul className="mb-4 font-mono text-lg" aria-label={title}>
        {lines.map((line) => (
          <li key={line} className="break-all py-1">
            {line}
          </li>
        ))}
      </ul>
      <div className="mb-4 flex flex-wrap gap-2">
        <CopyButton text={text} label="Copy" what="The codes" />
        <button type="button" className="btn-secondary print:hidden" onClick={download}>
          Download as a text file
        </button>
        <button
          type="button"
          className="btn-secondary print:hidden"
          onClick={() => {
            window.print();
          }}
        >
          Print
        </button>
      </div>
      <label className="mb-4 flex items-start gap-2 print:hidden">
        <input
          type="checkbox"
          className="mt-0.5 size-6"
          checked={saved}
          onChange={(event) => {
            setSaved(event.target.checked);
          }}
        />
        <span>I have saved this somewhere safe.</span>
      </label>
      <button type="button" className="btn-primary print:hidden" disabled={!saved} onClick={onDone}>
        {doneLabel}
      </button>
    </div>
  );
}
