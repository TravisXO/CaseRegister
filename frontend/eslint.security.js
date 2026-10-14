// The rules that keep the browser side safe. They live here, apart from the rest of the ESLint setup, so the tests can
// run the very same rules over snippets of bad code and prove that each one is caught.

// The browser must never keep anything in localStorage or sessionStorage: a stolen disk or a shared computer would then hold a
// signed-in user's data. This is enforced here, in every form it can be written, not just asked for in a comment.
const storage = "localStorage|sessionStorage";
const storageMessage = "Nothing may be kept in localStorage or sessionStorage.";

export const restrictedSyntax = [
  { selector: `Identifier[name=/^(${storage})$/]`, message: storageMessage },
  { selector: `MemberExpression[property.name=/^(${storage})$/]`, message: storageMessage },
  { selector: `MemberExpression[property.value=/^(${storage})$/]`, message: storageMessage },
  { selector: `Literal[value=/^(${storage})$/]`, message: storageMessage },
  { selector: "Identifier[name='indexedDB']", message: "Nothing may be kept in the browser's databases." },
  { selector: "MemberExpression[property.name='cookie'][object.name='document']", message: "Cookies are the server's business, not this code's." },
  // A timer that calls the API would keep resetting the 30-minute idle timeout, so there are no repeating timers at all.
  { selector: "CallExpression[callee.name='setInterval']", message: "No repeating timers: a background call would keep the session from timing out." },
  { selector: "CallExpression[callee.property.name='setInterval']", message: "No repeating timers: a background call would keep the session from timing out." },
  { selector: "CallExpression[callee.name='requestAnimationFrame']", message: "No background loops." },
  { selector: "NewExpression[callee.name='Function']", message: "No dynamic code." },
  { selector: "NewExpression[callee.name='EventSource']", message: "No background connections." },
  { selector: "NewExpression[callee.name='WebSocket']", message: "No background connections." },
];

export const securityRules = {
  "no-restricted-syntax": ["error", ...restrictedSyntax],
  "no-restricted-globals": ["error", "localStorage", "sessionStorage", "eval", "setInterval"],
  "no-restricted-properties": [
    "error",
    { object: "window", property: "localStorage", message: "Nothing may be kept in localStorage." },
    { object: "window", property: "sessionStorage", message: "Nothing may be kept in sessionStorage." },
    { object: "globalThis", property: "localStorage", message: "Nothing may be kept in localStorage." },
    { object: "globalThis", property: "sessionStorage", message: "Nothing may be kept in sessionStorage." },
    { object: "window", property: "setInterval", message: "No repeating timers." },
    { property: "eval", message: "No eval." },
  ],
  "no-eval": "error",
  "no-implied-eval": "error",
  "react/no-danger": "error",
  "react/no-danger-with-children": "error",
  "react/jsx-no-target-blank": ["error", { enforceDynamicLinks: "always" }],
  "react/jsx-no-script-url": "error",
};
