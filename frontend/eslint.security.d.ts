import type { Linter } from "eslint";

export const restrictedSyntax: { selector: string; message: string }[];
export const securityRules: Linter.RulesRecord;
