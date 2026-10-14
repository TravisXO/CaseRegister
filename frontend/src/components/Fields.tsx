import { useId, useState, type InputHTMLAttributes, type ReactNode } from "react";

interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id" | "className"> {
  id: string;
  label: string;
  hint?: ReactNode;
  error?: string | undefined;
}

/** A labelled input. The label is tied to it, and a problem is tied to it too, so a screen reader reads both. */
export function TextField({ id, label, hint, error, ...input }: FieldProps) {
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy = [hint === undefined ? null : hintId, error === undefined ? null : errorId].filter(Boolean).join(" ");

  return (
    <div className="mb-4">
      <label htmlFor={id} className="label">
        {label}
      </label>
      {hint === undefined ? null : (
        <span id={hintId} className="hint">
          {hint}
        </span>
      )}
      <input
        id={id}
        {...input}
        className={error === undefined ? "input" : "input input-error"}
        aria-invalid={error === undefined ? undefined : true}
        aria-describedby={describedBy === "" ? undefined : describedBy}
      />
      {error === undefined ? null : (
        <span id={errorId} className="field-error">
          {error}
        </span>
      )}
    </div>
  );
}

/** A password field with a show/hide button. Pasting is allowed (a password manager needs it). */
export function PasswordField({ id, label, hint, error, autoComplete, ...input }: FieldProps & { autoComplete: "current-password" | "new-password" }) {
  const [visible, setVisible] = useState(false);
  const toggleId = useId();

  return (
    <div className="mb-4">
      <TextField id={id} label={label} hint={hint} error={error} autoComplete={autoComplete} {...input} type={visible ? "text" : "password"} />
      <button
        id={toggleId}
        type="button"
        className="link -mt-2 mb-1 text-sm"
        aria-pressed={visible}
        aria-controls={id}
        onClick={() => {
          setVisible((value) => !value);
        }}
      >
        {visible ? "Hide password" : "Show password"}
      </button>
    </div>
  );
}

export function SelectField({
  id,
  label,
  error,
  children,
  ...select
}: {
  id: string;
  label: string;
  error?: string | undefined;
  children: ReactNode;
} & Omit<React.SelectHTMLAttributes<HTMLSelectElement>, "id" | "className">) {
  const errorId = `${id}-error`;

  return (
    <div className="mb-4">
      <label htmlFor={id} className="label">
        {label}
      </label>
      <select id={id} {...select} className={error === undefined ? "input" : "input input-error"} aria-invalid={error === undefined ? undefined : true} aria-describedby={error === undefined ? undefined : errorId}>
        {children}
      </select>
      {error === undefined ? null : (
        <span id={errorId} className="field-error">
          {error}
        </span>
      )}
    </div>
  );
}

/** A reason for an admin action: 10 to 500 characters, with a live count so the limit is never a surprise. */
export function ReasonField({ id, value, onChange, error, min, max }: { id: string; value: string; onChange: (value: string) => void; error?: string | undefined; min: number; max: number }) {
  const length = value.trim().length;
  const countId = `${id}-count`;
  const errorId = `${id}-error`;
  const describedBy = [countId, error === undefined ? null : errorId].filter(Boolean).join(" ");

  return (
    <div className="mb-4">
      <label htmlFor={id} className="label">
        Reason
      </label>
      <span className="hint">Say why, in {min} to {max} characters. It is kept in the audit log.</span>
      <textarea
        id={id}
        value={value}
        rows={3}
        maxLength={max + 200}
        onChange={(event) => {
          onChange(event.target.value);
        }}
        className={error === undefined ? "input" : "input input-error"}
        aria-invalid={error === undefined ? undefined : true}
        aria-describedby={describedBy}
      />
      <span id={countId} className={length > max || (length > 0 && length < min) ? "field-error" : "hint"} aria-live="polite">
        {length} of {max} characters{length < min ? ` (at least ${min})` : ""}
      </span>
      {error === undefined ? null : (
        <span id={errorId} className="field-error">
          {error}
        </span>
      )}
    </div>
  );
}

export function SubmitButton({ pending, children, variant = "primary" }: { pending: boolean; children: ReactNode; variant?: "primary" | "danger" }) {
  return (
    <button type="submit" disabled={pending} className={variant === "danger" ? "btn-danger" : "btn-primary"} aria-busy={pending}>
      {pending ? "Please wait…" : children}
    </button>
  );
}
