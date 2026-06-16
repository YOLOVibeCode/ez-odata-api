import js from "@eslint/js";
import tseslint from "@typescript-eslint/eslint-plugin";
import tsparser from "@typescript-eslint/parser";

export default [
  {
    ignores: ["dist/**", "node_modules/**", "coverage/**"],
  },
  js.configs.recommended,
  {
    files: ["**/*.ts"],
    languageOptions: {
      parser: tsparser,
      parserOptions: {
        ecmaVersion: 2022,
        sourceType: "module",
      },
    },
    plugins: {
      "@typescript-eslint": tseslint,
    },
    rules: {
      ...tseslint.configs.recommended.rules,
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      "@typescript-eslint/explicit-module-boundary-types": "off",
      // We intentionally use the TS value+type companion pattern throughout
      // (e.g. `const Verb` + `type Verb` for a bitmask enum), which both the
      // core and type-aware no-redeclare rules flag as a false positive.
      "no-redeclare": "off",
      "@typescript-eslint/no-redeclare": "off",
      // TypeScript resolves globals/identifiers; no-undef is redundant and flags
      // DOM/Node ambient types like AbortSignal/URL (typescript-eslint guidance).
      "no-undef": "off",
      "no-console": "warn",
    },
  },
];
