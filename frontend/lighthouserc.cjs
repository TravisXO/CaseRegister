// Lighthouse CI: the built site, on a phone (the default "mobile" settings: a small screen, slow 4G, a CPU four times slower), in the screens
// anyone can reach without signing in. It fails on any accessibility problem Lighthouse can find, and on more than 200 KB of JavaScript sent
// (compressed, as it is over the network). Run with: npm run lighthouse
module.exports = {
  ci: {
    collect: {
      startServerCommand: "node scripts/serve-dist.mjs dist-e2e 4174 --stub-api",
      startServerReadyPattern: "Serving",
      url: ["http://localhost:4174/sign-in", "http://localhost:4174/sign-up", "http://localhost:4174/privacy", "http://localhost:4174/terms"],
      numberOfRuns: 1,
      settings: {
        onlyCategories: ["accessibility", "best-practices", "performance"],
        // Chrome in a runner is started by Lighthouse; these flags are what a container needs.
        chromeFlags: "--no-sandbox --headless=new",
      },
    },
    assert: {
      assertions: {
        "categories:accessibility": ["error", { minScore: 1 }],
        "resource-summary:script:size": ["error", { maxNumericValue: 204800 }],
        "resource-summary:third-party:count": ["warn", { maxNumericValue: 3 }],
      },
    },
    upload: { target: "filesystem", outputDir: ".lighthouseci" },
  },
};
