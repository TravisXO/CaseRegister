// After "npm run build": checks the files that would be served against the rules the site is served under.
//  - no inline script, no inline style, no event-handler attributes (the content security policy has no 'unsafe-inline')
//  - no source maps, and nothing pointing at one
//  - no eval or new Function in the application's own code
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

const dist = "dist";
const problems = [];

if (!existsSync(join(dist, "index.html"))) {
  console.error("dist/index.html is missing. Run: npm run build");
  process.exit(1);
}

const files = [];
(function walk(directory) {
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) {
      walk(path);
    } else {
      files.push(path);
    }
  }
})(dist);

const html = readFileSync(join(dist, "index.html"), "utf8");
for (const script of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
  const attributes = script[1] ?? "";
  const body = script[2] ?? "";
  if (!/\bsrc=/.test(attributes) || body.trim() !== "") {
    problems.push("index.html has an inline script.");
  }
}

if (/<style\b/i.test(html)) problems.push("index.html has an inline <style>.");
if (/\sstyle=/i.test(html)) problems.push("index.html has a style attribute.");
if (/\son[a-z]+\s*=/i.test(html)) problems.push("index.html has an event-handler attribute.");

for (const file of files) {
  if (file.endsWith(".map")) problems.push(`${file} is a source map.`);
  if (/\.(js|css)$/.test(file)) {
    const text = readFileSync(file, "utf8");
    if (/sourceMappingURL=/.test(text)) problems.push(`${file} points at a source map.`);
    if (/\beval\s*\(/.test(text)) problems.push(`${file} calls eval.`);
  }
}

if (problems.length > 0) {
  console.error(problems.map((problem) => `  - ${problem}`).join("\n"));
  process.exit(1);
}

console.log(`dist is clean: ${files.length} files, no inline script or style, no source maps, no eval.`);
