// Exports the ASP.NET Core development certificate for the Vite dev server, so the browser trusts this site exactly as it trusts the API.
import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";

mkdirSync(".certs", { recursive: true });

run(["dev-certs", "https", "--export-path", ".certs/dev.pem", "--format", "Pem", "--no-password"]);
console.log("Written .certs/dev.pem and .certs/dev.key (kept out of git).");

// The browser only trusts it if the machine does. Say so, rather than showing a security prompt from a script.
const trusted = spawnSync("dotnet", ["dev-certs", "https", "--check", "--trust"], { stdio: "ignore" });
if (trusted.status !== 0) {
  console.log("The development certificate is not trusted on this machine yet. Run once:  dotnet dev-certs https --trust");
}

console.log("Now: npm run dev");

function run(args) {
  const result = spawnSync("dotnet", args, { stdio: "inherit" });
  if (result.status !== 0) {
    console.error(`"dotnet ${args.join(" ")}" failed. Is the .NET SDK installed?`);
    process.exit(result.status ?? 1);
  }
}