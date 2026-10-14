import { useEffect, useRef } from "react";

export interface FormProblem {
  /** The id of the field it is about, if it is about one. */
  field?: string;
  message: string;
}

/**
 * The list of what is wrong, above the form. When it appears, keyboard focus moves to the first field that has a problem (or to the
 * list itself when none is about a field), so someone using a keyboard or a screen reader lands on what to fix.
 */
export function ErrorSummary({ problems, traceId }: { problems: FormProblem[]; traceId?: string | undefined }) {
  const summary = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (problems.length === 0) {
      return;
    }

    const first = problems.find((problem) => problem.field !== undefined);
    const field = first?.field === undefined ? null : document.getElementById(first.field);
    (field ?? summary.current)?.focus();
  }, [problems]);

  if (problems.length === 0) {
    return null;
  }

  return (
    <div ref={summary} role="alert" tabIndex={-1} className="alert-error" aria-labelledby="error-summary-title">
      <h2 id="error-summary-title" className="mb-1 font-semibold">
        There is a problem
      </h2>
      <ul className="list-inside list-disc">
        {problems.map((problem, index) => (
          <li key={`${problem.field ?? "form"}-${index}`}>
            {problem.field === undefined ? (
              problem.message
            ) : (
              <a
                href={`#${problem.field}`}
                className="link"
                onClick={(event) => {
                  event.preventDefault();
                  document.getElementById(problem.field ?? "")?.focus();
                }}
              >
                {problem.message}
              </a>
            )}
          </li>
        ))}
      </ul>
      {traceId === undefined ? null : <p className="mt-2">Reference: {traceId}</p>}
    </div>
  );
}
