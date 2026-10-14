import js from "@eslint/js";
import jsxA11y from "eslint-plugin-jsx-a11y";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";
import security from "eslint-plugin-security";
import globals from "globals";
import tseslint from "typescript-eslint";
import { securityRules } from "./eslint.security.js";

export default tseslint.config(
  { ignores: ["dist", "coverage", "node_modules", "src/api/schema.d.ts", ".certs"] },
  js.configs.recommended,
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,
  react.configs.flat.recommended,
  react.configs.flat["jsx-runtime"],
  jsxA11y.flatConfigs.recommended,
  security.configs.recommended,
  { settings: { react: { version: "detect" } } }, // for every file, so the plugin does not warn on the .js ones
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2023,
      globals: { ...globals.browser },
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { "react-hooks": reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      ...securityRules,
      "react/self-closing-comp": "error",
      "react/prop-types": "off", // TypeScript checks the props
      "@typescript-eslint/restrict-template-expressions": ["error", { allowNumber: true }],
      "@typescript-eslint/no-confusing-void-expression": ["error", { ignoreArrowShorthand: true }],
      // Indexing an object by a key is how this code reads form data and error maps; the rule cannot tell those from a risk.
      "security/detect-object-injection": "off",
    },
  },
  {
    // Tests may reach for the things the application may not, to prove the application does not.
    files: ["src/**/*.test.{ts,tsx}", "src/test/**"],
    rules: {
      "no-restricted-syntax": "off",
      "no-restricted-globals": "off",
      "no-restricted-properties": "off",
      "security/detect-non-literal-fs-filename": "off", // tests read the project's own files
      "@typescript-eslint/no-non-null-assertion": "off",
      "@typescript-eslint/no-unsafe-assignment": "off",
      "@typescript-eslint/no-unsafe-member-access": "off",
      "@typescript-eslint/unbound-method": "off",
    },
  },
  {
    ...tseslint.configs.disableTypeChecked,
    files: ["scripts/**/*.mjs", "vite.config.ts", "eslint.config.js", "eslint.security.js"],
    languageOptions: { ...tseslint.configs.disableTypeChecked.languageOptions, globals: { ...globals.node } },
    rules: { ...tseslint.configs.disableTypeChecked.rules, "security/detect-non-literal-fs-filename": "off" }, // these read the project's own files
  },
);
