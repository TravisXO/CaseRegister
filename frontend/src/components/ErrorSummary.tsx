import { useEffect, useRef } from "react";

export interface FormProblem {
  /** The id of the field it is about, if it is about one. */
  field?: string;
  message: string;
}

/**
 * The list of what is wrong, above the form, used by every form. When it appears it takes keyboard focus, and being an alert it is read out, so
 * someone using a keyboard or a screen reader lands on the whole list first; each item that is about a field is a link that moves focus to
 * that field. (Each field also names its own problem: aria-invalid, and aria-describedby pointing at the message.)
 */
export function ErrorSummary({ problems, traceId }: { problems: FormProblem[]; traceId?: string | undefined }) {
  const summary = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (problems.length === 0) {
      return;
    }

    summary.current?.focus();
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
                className="link inline-flex min-h-6 items-center py-0.5"
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
