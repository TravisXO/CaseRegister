import { ApiError, describeError } from "../api/client";
import type { FormProblem } from "../components/ErrorSummary";

export interface Problems {
  problems: FormProblem[];
  traceId?: string | undefined;
}

/**
 * Turns a failed call into what the form shows. The API names fields its own way ("Email", "CertificateYear"); `fields` maps those
 * names (in lower case) to the ids of this form's inputs. What the API said is shown as it said it, and nothing is added: in
 * particular nothing that would tell someone what the API deliberately does not.
 */
export function problemsFrom(error: unknown, fields: Record<string, string> = {}): Problems {
  if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
    const problems = Object.entries(error.fieldErrors).flatMap(([name, messages]) =>
      messages.map((message): FormProblem => {
        const field = fields[name.toLowerCase()];

        return field === undefined ? { message } : { field, message };
      }),
    );

    return error.traceId === undefined ? { problems } : { problems, traceId: error.traceId };
  }

  const { message, traceId } = describeError(error);
  const listed = error instanceof ApiError ? error.problems.map((problem): FormProblem => ({ message: problem.message })) : [];

  return { problems: [{ message }, ...listed], traceId };
}

/** The message for one field from a list of problems, if any is about it. */
export function errorFor(problems: FormProblem[], field: string): string | undefined {
  return problems.find((problem) => problem.field === field)?.message;
}

export function required(value: string, field: string, message: string): FormProblem[] {
  return value.trim() === "" ? [{ field, message }] : [];
}
