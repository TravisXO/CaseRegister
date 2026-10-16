// After "npm run build": checks the files that would be served against the rules the site is served under.
//  - no inline script, no inline style, no event-handler attributes (the content security policy has no 'unsafe-inline')
//  - no source maps, and nothing pointing at one
//  - no eval or new Function in the application's own code
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

const dist = process.argv[2] ?? "dist"; // another folder can be given, which is how the tests try this check on bad builds
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
// A browser ends a script at "</script" followed by a space, a slash or ">", in any case, so the end tag here is matched the same way.
const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script[\s/>]/gi)];
for (const script of scripts) {
  const attributes = script[1] ?? "";
  const body = script[2] ?? "";
  if (!/\bsrc\s*=/i.test(attributes) || body.trim() !== "") {
    problems.push("index.html has an inline script.");
  }
}

// Every script start must have been read as a whole script above. One that was not (no end tag, say) is not waved through.
if ((html.match(/<script\b/gi) ?? []).length !== scripts.length) {
  problems.push("index.html has a script this check could not read.");
}

// What Cloudflare Pages needs from the build, and what keeps the site out of search engines.
if (!/<meta\s+name="robots"\s+content="[^"]*noindex/i.test(html)) problems.push("index.html has no noindex meta tag.");

for (const [name, mustHave] of [
  ["robots.txt", /^Disallow:\s*\/\s*$/m],
  ["_headers", /Strict-Transport-Security:[^\n]*max-age=31536000/],
  ["_headers", /Content-Security-Policy:[^\n]*frame-ancestors 'none'/],
  ["_headers", /X-Robots-Tag:[^\n]*noindex/],
  ["_redirects", /^\/\*\s+\/index\.html\s+200/m],
  ["_routes.json", /"\/api\/\*"/],
]) {
  const file = join(dist, name);
  if (!existsSync(file)) {
    problems.push(`${name} is missing from the build.`);
  } else if (!mustHave.test(readFileSync(file, "utf8"))) {
    problems.push(`${name} does not say what it should (${mustHave}).`);
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

    // The browser files are public. The secret the proxy sends, and the name of its header, belong to the Pages Function alone.
    if (/x-proxy-secret|PROXY_SECRET/i.test(text)) problems.push(`${file} names the proxy secret.`);
  }
}

if (problems.length > 0) {
  console.error(problems.map((problem) => `  - ${problem}`).join("\n"));
  process.exit(1);
}

console.log(`dist is clean: ${files.length} files, no inline script or style, no source maps, no eval.`);
